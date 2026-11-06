using PhotoViewer.Core.Color;
using PhotoViewer.Core.Image;
using PhotoViewer.Core.Image.Heif;

namespace HeifFastDecodeTest;

/// <summary>不依赖样张的自检:变换→EXIF 方向真值表、容器色彩声明、原生平面内存估值的纯计算口径。</summary>
internal static class SelfTest
{
    private enum Op
    {
        Rot0,
        Rot1,
        Rot2,
        Rot3,
        Mirror0,
        Mirror1,
    }

    public static int Run()
    {
        var failures = TransformTruthTable();
        failures += ColorTests.RunContainerCases();
        failures += NativePlaneEstimateCases();
        Console.WriteLine(failures == 0 ? "selftest: PASS" : $"selftest: {failures} FAILURE(S)");
        return failures == 0 ? 0 : 1;
    }

    /// <summary>
    /// 原生平面临时内存估值的纯计算口径:合成容器(64×32 单瓦片、4:2:0、hvcC 声明 10 位)上手算期望值。
    /// </summary>
    /// <remarks>
    /// 每块瓦片 = <c>strideY×codedHeight + 2×strideC×chromaHeight</c>(码流 16 像素、行距 64 字节对齐),
    /// 全部瓦片求和后乘 2(解码帧 + 解码器保留表面);ICC 源另加转换中间图,最终展示缓冲不计。
    /// </remarks>
    /// <returns>失败项数。</returns>
    private static int NativePlaneEstimateCases()
    {
        var icc = new byte[132];
        for (var i = 0; i < icc.Length; i++) icc[i] = (byte)(0x30 + i);

        var plain = HeifContainer.TryParse(ColorTests.BuildContainer(null, null), out var plainReason);
        var withIcc = HeifContainer.TryParse(ColorTests.BuildContainer(null, icc), out var iccReason);
        if (plain is null || withIcc is null)
        {
            Console.WriteLine($"  ! 估值用例的合成容器解析失败 plain={plainReason} icc={iccReason}");
            return 1;
        }

        var failures = 0;

        // 64×32、4:2:0、10 位(2 字节/样本):coded 64×32、strideY = align(128,64) = 128、
        // 色度 32×16、strideC = align(64,64) = 64 → 单瓦片 128×32 + 2×64×16 = 6144,两份表面 = 12288。
        failures += ExpectEstimate("单瓦片 4:2:0 10 位平面", plain, null, PixelEncoding.RgbaF16, 12288, 0);
        // ICC 中间图按目标尺寸:64×32 = 2048 像素;位深 > 8 且出口为 RGBA16F/RGBA8 时 8 字节/像素。
        failures += ExpectEstimate("ICC F16 中间图 8 字节/像素", withIcc, null, PixelEncoding.RgbaF16, 12288, 16384);
        // 8 位 RGBA 非 P3 出口还有一张 sRGB8 中转图(4 字节/像素)。
        failures += ExpectEstimate("ICC RGBA8 sRGB 多一张中转图", withIcc, null, PixelEncoding.Rgba8, 12288, 24576);
        failures += ExpectEstimate("ICC RGBA8 P3 无中转图", withIcc, null, PixelEncoding.Rgba8, 12288, 16384,
            ImageColorSpace.DisplayP3);
        // 8 位出口(BGRA32)用 4 字节/像素的 ICC 码值图。
        failures += ExpectEstimate("ICC BGRA32 中间图 4 字节/像素", withIcc, null, PixelEncoding.Bgra32, 12288, 8192);
        // 目标面积缩小只改 ICC 中间图(8×4 = 32 像素),平面估值不变:原生解码仍是全瓦片。
        failures += ExpectEstimate("代理目标不改平面估值", withIcc, 8, PixelEncoding.RgbaF16, 12288, 256);

        Console.WriteLine($"native plane estimate cases: {(failures == 0 ? "ok" : "FAILED")}");
        return failures;
    }

    /// <summary>断言一次估值与手算期望一致。</summary>
    /// <param name="label">用例名。</param>
    /// <param name="container">容器。</param>
    /// <param name="maxLongEdge">目标长边上限。</param>
    /// <param name="encoding">目标编码。</param>
    /// <param name="planeBytes">期望平面字节。</param>
    /// <param name="iccBytes">期望 ICC 中间图字节。</param>
    /// <param name="colorSpace">8 位 RGBA 的目标色彩空间。</param>
    /// <returns>0 = 通过,1 = 失败。</returns>
    private static int ExpectEstimate(
        string label,
        HeifContainer container,
        int? maxLongEdge,
        PixelEncoding encoding,
        long planeBytes,
        long iccBytes,
        ImageColorSpace colorSpace = ImageColorSpace.Srgb)
    {
        var estimate = FastHeifDecoder.EstimateScratch(container, maxLongEdge, encoding, colorSpace);
        var ok = estimate.PlaneBytes == planeBytes && estimate.IccIntermediateBytes == iccBytes;
        Console.WriteLine($"  {(ok ? "ok" : "! ")} {label}: 平面 {estimate.PlaneBytes}(期望 {planeBytes}) "
            + $"ICC {estimate.IccIntermediateBytes}(期望 {iccBytes})");
        return ok ? 0 : 1;
    }

    /// <summary>
    /// 枚举长度 1–4 的全部 irot/imir 序列,按像素映射复合出显示结果,与 8 个规范 EXIF 方向逐一比对,
    /// 再与合成容器解析出的 <see cref="HeifContainer.ExifOrientation"/> 对照(经由真实解析路径,不直连内部映射)。
    /// </summary>
    private static int TransformTruthTable()
    {
        const int width = 7;
        const int height = 5;
        var source = new int[height, width];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++) source[y, x] = (y * width) + x;
        }

        var failures = 0;
        var ops = (Op[])Enum.GetValues(typeof(Op));
        foreach (var sequence in Sequences(ops, 4))
        {
            var image = source;
            var transforms = new List<(int Kind, int Value)>(sequence.Count);
            foreach (var op in sequence)
            {
                image = ApplyOp(image, op);
                transforms.Add(op switch
                {
                    Op.Rot0 => (0, 0),
                    Op.Rot1 => (0, 1),
                    Op.Rot2 => (0, 2),
                    Op.Rot3 => (0, 3),
                    Op.Mirror0 => (1, 0),
                    _ => (1, 1),
                });
            }

            var expected = FindExif(source, image);
            if (expected == 0)
            {
                Console.WriteLine("  ! composite has no EXIF equivalent: " + Describe(sequence));
                failures++;
                continue;
            }

            var data = ColorTests.BuildContainer(null, null, transforms);
            var container = HeifContainer.TryParse(data, out var reason);
            if (container is null)
            {
                Console.WriteLine($"  ! {Describe(sequence)}: 合成容器解析失败 {reason}");
                failures++;
                continue;
            }

            if (container.ExifOrientation != expected)
            {
                Console.WriteLine(
                    $"  ! {Describe(sequence)}: expected EXIF {expected}, got {container.ExifOrientation}");
                failures++;
            }
        }

        Console.WriteLine($"transform truth table: {(failures == 0 ? "ok" : "FAILED")}");
        return failures;
    }

    private static IEnumerable<List<Op>> Sequences(Op[] ops, int maxLength)
    {
        for (var length = 1; length <= maxLength; length++)
        {
            var indices = new int[length];
            while (true)
            {
                var sequence = new List<Op>(length);
                foreach (var index in indices) sequence.Add(ops[index]);
                yield return sequence;

                var position = length - 1;
                while (position >= 0)
                {
                    indices[position]++;
                    if (indices[position] < ops.Length) break;
                    indices[position] = 0;
                    position--;
                }
                if (position < 0) break;
            }
        }
    }

    private static string Describe(List<Op> sequence)
    {
        var parts = new List<string>(sequence.Count);
        foreach (var op in sequence)
        {
            parts.Add(op switch
            {
                Op.Rot0 => "irot0",
                Op.Rot1 => "irot1",
                Op.Rot2 => "irot2",
                Op.Rot3 => "irot3",
                Op.Mirror0 => "imir0",
                _ => "imir1",
            });
        }
        return string.Join(" ", parts);
    }

    /// <summary>把 irot/imir 作用到一张标号图上,返回变换后的图。</summary>
    private static int[,] ApplyOp(int[,] image, Op op)
    {
        var h = image.GetLength(0);
        var w = image.GetLength(1);
        return op switch
        {
            // 90° 逆时针:(x,y) → (y, W-1-x),新尺寸 W'=H, H'=W
            Op.Rot1 => Map(image, w, h, (x, y) => (y, w - 1 - x)),
            Op.Rot2 => Map(image, w, h, (x, y) => (w - 1 - x, h - 1 - y)),
            Op.Rot3 => Map(image, w, h, (x, y) => (h - 1 - y, x)),
            // 垂直轴镜像 = 左右翻转
            Op.Mirror0 => Map(image, w, h, (x, y) => (w - 1 - x, y)),
            // 水平轴镜像 = 上下翻转
            Op.Mirror1 => Map(image, w, h, (x, y) => (x, h - 1 - y)),
            _ => Map(image, w, h, (x, y) => (x, y)),
        };
    }

    /// <summary>按 EXIF 1–8 的显示语义变换标号图。</summary>
    private static int[,] ExifCanonical(int[,] source, int exif)
    {
        var h = source.GetLength(0);
        var w = source.GetLength(1);
        return exif switch
        {
            1 => Map(source, w, h, (x, y) => (x, y)),
            2 => Map(source, w, h, (x, y) => (w - 1 - x, y)),
            3 => Map(source, w, h, (x, y) => (w - 1 - x, h - 1 - y)),
            4 => Map(source, w, h, (x, y) => (x, h - 1 - y)),
            // 5 转置、(7 反转置)与 6/8 旋转都交换宽高
            5 => Map(source, w, h, (x, y) => (y, x)),
            6 => Map(source, w, h, (x, y) => (h - 1 - y, x)),
            7 => Map(source, w, h, (x, y) => (h - 1 - y, w - 1 - x)),
            8 => Map(source, w, h, (x, y) => (y, w - 1 - x)),
            _ => throw new ArgumentOutOfRangeException(nameof(exif)),
        };
    }

    private static int[,] Map(int[,] source, int width, int height, Func<int, int, (int X, int Y)> mapping)
    {
        var maxX = 0;
        var maxY = 0;
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var (nx, ny) = mapping(x, y);
                if (nx > maxX) maxX = nx;
                if (ny > maxY) maxY = ny;
            }
        }

        var result = new int[maxY + 1, maxX + 1];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var (nx, ny) = mapping(x, y);
                result[ny, nx] = source[y, x];
            }
        }
        return result;
    }

    private static int FindExif(int[,] source, int[,] target)
    {
        for (var exif = 1; exif <= 8; exif++)
        {
            var candidate = ExifCanonical(source, exif);
            if (Same(candidate, target)) return exif;
        }
        return 0;
    }

    private static bool Same(int[,] a, int[,] b)
    {
        if (a.GetLength(0) != b.GetLength(0) || a.GetLength(1) != b.GetLength(1)) return false;
        for (var y = 0; y < a.GetLength(0); y++)
        {
            for (var x = 0; x < a.GetLength(1); x++)
            {
                if (a[y, x] != b[y, x]) return false;
            }
        }
        return true;
    }
}
