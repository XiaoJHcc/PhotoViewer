using PhotoViewer.Windows.Core.Heif;

namespace HeifFastDecodeTest;

/// <summary>不依赖样张的自检:变换→EXIF 方向真值表。</summary>
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
        Console.WriteLine(failures == 0 ? "selftest: PASS" : $"selftest: {failures} FAILURE(S)");
        return failures == 0 ? 0 : 1;
    }

    /// <summary>
    /// 枚举长度 1–4 的全部 irot/imir 序列,按像素映射复合出显示结果,
    /// 与 8 个规范 EXIF 方向逐一比对,再与 ToExifOrientation 的折算结果对照。
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
            var transforms = new List<HeifTransform>(sequence.Count);
            foreach (var op in sequence)
            {
                image = ApplyOp(image, op);
                transforms.Add(op switch
                {
                    Op.Rot0 => new HeifTransform(HeifTransformKind.Rotation, 0),
                    Op.Rot1 => new HeifTransform(HeifTransformKind.Rotation, 1),
                    Op.Rot2 => new HeifTransform(HeifTransformKind.Rotation, 2),
                    Op.Rot3 => new HeifTransform(HeifTransformKind.Rotation, 3),
                    Op.Mirror0 => new HeifTransform(HeifTransformKind.Mirror, 0),
                    _ => new HeifTransform(HeifTransformKind.Mirror, 1),
                });
            }

            var expected = FindExif(source, image);
            if (expected == 0)
            {
                Console.WriteLine("  ! composite has no EXIF equivalent: " + Describe(sequence));
                failures++;
                continue;
            }

            var actual = HeifContainer.ToExifOrientation(transforms);
            if (actual != expected)
            {
                Console.WriteLine(
                    $"  ! {Describe(sequence)}: expected EXIF {expected}, got {actual}");
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
