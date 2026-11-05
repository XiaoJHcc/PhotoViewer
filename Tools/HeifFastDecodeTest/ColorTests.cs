using System.Buffers.Binary;
using System.Text;
using PhotoViewer.Core.Image.Heif;

namespace HeifFastDecodeTest;

/// <summary>
/// 色彩链自检:合成容器的 <c>colr</c> 合并(ICC 优先面)与转换器的 nclx 原色/传递函数、ICC 编码值出口。
/// 不依赖样张:容器按 ISOBMFF 子集现拼,像素用合成平面,期望值来自独立公式。
/// </summary>
internal static class ColorTests
{
    // ---------------------------------------------------------------
    // 容器:合成 colr 组合
    // ---------------------------------------------------------------

    /// <summary>运行容器侧色彩声明合并用例。</summary>
    /// <returns>失败数。</returns>
    public static int RunContainerCases()
    {
        var failures = 0;
        failures += ContainerCase(
            "colr nclx+ICC 双盒",
            nclx: (Primaries: 12, Transfer: 13, Matrix: 9, Full: true),
            icc: FakeIcc(0x11),
            expectNclx: true,
            expectIcc: true);
        failures += ContainerCase("colr 仅 ICC", nclx: null, icc: FakeIcc(0x22), expectNclx: false, expectIcc: true);
        failures += ContainerCase("colr 仅 nclx", nclx: (Primaries: 9, Transfer: 16, Matrix: 9, Full: false), icc: null, expectNclx: true, expectIcc: false);
        failures += ContainerCase("colr 无", nclx: null, icc: null, expectNclx: false, expectIcc: false);

        Console.WriteLine($"container color cases: {(failures == 0 ? "ok" : "FAILED")}");
        return failures;
    }

    private static int ContainerCase(
        string label,
        (int Primaries, int Transfer, int Matrix, bool Full)? nclx,
        byte[]? icc,
        bool expectNclx,
        bool expectIcc)
    {
        var data = BuildContainer(nclx, icc);
        var container = HeifContainer.TryParse(data, out var reason);
        if (container is null)
        {
            Console.WriteLine($"  ! {label}: 解析失败 {reason}");
            return 1;
        }

        var color = container.Color;
        var failures = 0;
        if (color.HasNclx != expectNclx)
        {
            Console.WriteLine($"  ! {label}: HasNclx={color.HasNclx}, 期望 {expectNclx}");
            failures++;
        }

        if (color.HasIccProfile != expectIcc)
        {
            Console.WriteLine($"  ! {label}: HasIccProfile={color.HasIccProfile}, 期望 {expectIcc}");
            failures++;
        }

        if (nclx is { } n && color.HasNclx
            && (color.Primaries != n.Primaries || color.Transfer != n.Transfer || color.Matrix != n.Matrix || color.FullRange != n.Full))
        {
            Console.WriteLine($"  ! {label}: nclx 读成 {color}, 期望 ({n.Primaries},{n.Transfer},{n.Matrix},{(n.Full ? "full" : "limited")})");
            failures++;
        }

        if (icc is not null && (!color.HasIccProfile || !color.IccProfile!.AsSpan().SequenceEqual(icc)))
        {
            Console.WriteLine($"  ! {label}: ICC 字节不一致(读回 {color.IccProfile?.Length ?? 0}B, 期望 {icc.Length}B)");
            failures++;
        }

        if (failures == 0) Console.WriteLine($"  ok {label}: {color}");
        return failures;
    }

    /// <summary>一行假的 ICC 字节:首尾带可辨认标记,便于断言字节被原样搬运。</summary>
    private static byte[] FakeIcc(byte marker)
    {
        var profile = new byte[132];
        profile[0] = marker;
        profile[^1] = (byte)(marker ^ 0xFF);
        for (var i = 1; i < profile.Length - 1; i++) profile[i] = (byte)(marker + i);
        return profile;
    }

    /// <summary>
    /// 拼一个最小可解析的 HEIF:hvc1 主图 + <c>ispe</c>/<c>hvcC</c> + 可选 <c>colr</c>/<c>irot</c>/<c>imir</c>,
    /// 数据放 <c>idat</c>。
    /// </summary>
    /// <param name="nclx">要写入的 nclx 三元组;null 表示不写。</param>
    /// <param name="icc">要写入的 ICC 字节;null 表示不写。</param>
    /// <param name="transforms">按 ipma 顺序写入的变换;<c>Kind</c> 0 = <c>irot</c>、1 = <c>imir</c>。</param>
    /// <returns>完整文件字节。</returns>
    internal static byte[] BuildContainer(
        (int Primaries, int Transfer, int Matrix, bool Full)? nclx,
        byte[]? icc,
        IReadOnlyList<(int Kind, int Value)>? transforms = null)
    {
        const int itemId = 1;
        const int width = 64;
        const int height = 32;

        var properties = new List<byte[]>
        {
            Box("ispe", SetU32s(new byte[12], 4, (uint)width, (uint)height)),
            Box("hvcC", MinimalHvcC()),
        };
        if (nclx is { } n) properties.Add(Box("colr", NclxPayload(n)));
        if (icc is not null) properties.Add(Box("colr", IccPayload(icc)));
        if (transforms is not null)
        {
            foreach (var (kind, value) in transforms)
            {
                // irot/imir 载荷只有 1 个取值字节(不带版本/标志)。
                properties.Add(Box(kind == 0 ? "irot" : "imir", new[] { (byte)value }));
            }
        }

        var ipco = Box("ipco", Concat(properties));

        var ipmaPayload = new List<byte> { 0, 0, 0, 0 };           // version 0 + flags 0(索引为 8 位)
        ipmaPayload.AddRange(U32(1));                              // entry_count
        ipmaPayload.AddRange(U16(itemId));
        ipmaPayload.Add((byte)properties.Count);
        for (var i = 0; i < properties.Count; i++) ipmaPayload.Add((byte)(i + 1));
        var ipma = Box("ipma", ipmaPayload.ToArray());

        var iprp = Box("iprp", Concat(new[] { ipco, ipma }));

        var pitm = Box("pitm", U16Pair(0, itemId));
        var iinf = Box("iinf", IinfPayload(itemId, "hvc1"));
        var itemData = ItemData();
        var iloc = Box("iloc", IlocPayload(itemId, itemData.Length));
        var idat = Box("idat", itemData);

        var meta = Box("meta", Concat(new[] { FullBoxHeader(), pitm, iinf, iloc, iprp, idat }));
        var ftyp = Box("ftyp", Concat(new[] { Encoding.ASCII.GetBytes("heic"), U32(0), Encoding.ASCII.GetBytes("mif1heic") }));
        return Concat(new[] { ftyp, meta });
    }

    private static byte[] ItemData()
    {
        // lengthSize = 4:hvcC 里声明,数据取一段 8 字节的假 NAL。
        var data = new byte[12];
        BinaryPrimitives.WriteUInt32BigEndian(data, 8);
        for (var i = 4; i < data.Length; i++) data[i] = (byte)(0x40 + i);
        return data;
    }

    private static byte[] MinimalHvcC()
    {
        var payload = new byte[23];
        payload[0] = 1;                       // configurationVersion
        payload[12] = 120;                    // 无所谓,解析不读
        payload[16] = 0x01 | 0xFC;            // chromaFormat = 1 (4:2:0)
        payload[17] = 0x02 | 0xF8;            // bitDepthLumaMinus8 = 2 (10 位)
        payload[18] = 0x02 | 0xF8;            // bitDepthChromaMinus8 = 2
        payload[21] = 0x03 | 0xFC;            // lengthSizeMinusOne = 3
        payload[22] = 0;                      // numOfArrays = 0
        return payload;
    }

    private static byte[] NclxPayload((int Primaries, int Transfer, int Matrix, bool Full) nclx)
    {
        var payload = new byte[11];
        Encoding.ASCII.GetBytes("nclx").CopyTo(payload, 0);
        BinaryPrimitives.WriteUInt16BigEndian(payload.AsSpan(4), (ushort)nclx.Primaries);
        BinaryPrimitives.WriteUInt16BigEndian(payload.AsSpan(6), (ushort)nclx.Transfer);
        BinaryPrimitives.WriteUInt16BigEndian(payload.AsSpan(8), (ushort)nclx.Matrix);
        payload[10] = nclx.Full ? (byte)0x80 : (byte)0x00;
        return payload;
    }

    private static byte[] IccPayload(byte[] icc)
    {
        var payload = new byte[4 + icc.Length];
        Encoding.ASCII.GetBytes("prof").CopyTo(payload, 0);
        icc.CopyTo(payload, 4);
        return payload;
    }

    private static byte[] IinfPayload(int itemId, string itemType)
    {
        var infe = new List<byte> { 2, 0, 0, 0 };                 // version 2, flags 0
        infe.AddRange(U16(itemId));
        infe.AddRange(U16(0));                                    // item_protection_index
        infe.AddRange(Encoding.ASCII.GetBytes(itemType));
        var payload = new List<byte> { 0, 0, 0, 0 };              // FullBox 头
        payload.AddRange(U16(1));                                 // entry_count
        payload.AddRange(Box("infe", infe.ToArray()));
        return payload.ToArray();
    }

    private static byte[] IlocPayload(int itemId, int length)
    {
        // version 1(construction_method 有效),offset/length/base 各 4/4/0 字节。
        var payload = new List<byte> { 1, 0, 0, 0 };
        payload.AddRange(U16(0x4400));                            // offsetSize=4, lengthSize=4, baseOffsetSize=0
        payload.AddRange(U16(1));                                 // item_count
        payload.AddRange(U16(itemId));
        payload.AddRange(U16(1));                                 // construction_method = 1 (idat)
        payload.AddRange(U16(0));                                 // data_reference_index
        payload.AddRange(U16(1));                                 // extent_count
        payload.AddRange(U32(0));                                 // extent_offset 相对 idat 载荷
        payload.AddRange(U32((uint)length));                      // extent_length
        return payload.ToArray();
    }

    private static byte[] FullBoxHeader() => new byte[4];

    private static byte[] U16Pair(int first, int second)
    {
        var payload = new byte[6];
        BinaryPrimitives.WriteUInt16BigEndian(payload.AsSpan(4), (ushort)second);
        return payload;
    }

    private static byte[] SetU32s(byte[] payload, int first, uint second, uint third)
    {
        BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(first), second);
        BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(first + 4), third);
        return payload;
    }

    private static byte[] U16(int value)
    {
        var bytes = new byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(bytes, (ushort)value);
        return bytes;
    }

    private static byte[] U32(uint value)
    {
        var bytes = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(bytes, value);
        return bytes;
    }

    private static byte[] Box(string type, byte[] payload)
    {
        var box = new byte[8 + payload.Length];
        BinaryPrimitives.WriteUInt32BigEndian(box, (uint)box.Length);
        Encoding.ASCII.GetBytes(type).CopyTo(box, 4);
        payload.CopyTo(box, 8);
        return box;
    }

    private static byte[] Concat(IReadOnlyList<byte[]> parts)
    {
        var total = 0;
        for (var i = 0; i < parts.Count; i++) total += parts[i].Length;
        var result = new byte[total];
        var offset = 0;
        for (var i = 0; i < parts.Count; i++)
        {
            parts[i].CopyTo(result, offset);
            offset += parts[i].Length;
        }
        return result;
    }

    // ---------------------------------------------------------------
    // 转换器:nclx 原色/传递函数与 ICC 编码值出口
    // ---------------------------------------------------------------

    /// <summary>运行转换器侧色彩用例。</summary>
    /// <returns>失败数。</returns>
    public static int RunConverterCases()
    {
        var failures = 0;
        failures += TransferCurveCases();
        failures += PrimariesCases();
        failures += IccCodeValueCases();
        failures += RealFixtureCases();
        failures += HighBitDepthCases();
        failures += FallbackTransferCases();
        failures += UnsupportedCases();
        Console.WriteLine($"converter color cases: {(failures == 0 ? "ok" : "FAILED")}");
        return failures;
    }

    /// <summary>
    /// 真实夹具的 ICC 优先:取 <c>Fixtures/heif/color-p3.heic</c> 里真实的 ICC,配上一组"故意不同"的
    /// nclx(原色 BT.2020 + BT.709 曲线),编码值出口必须与 nclx 无关地给出同一组码值;
    /// 同时验证同一 nclx 在没有 ICC 时确实会换算出不同结果(否则上面的不变性是空断言)。
    /// </summary>
    private static int RealFixtureCases()
    {
        var path = FindFixture("color-p3.heic");
        if (path is null)
        {
            Console.WriteLine("  ! 真实夹具缺失:Fixtures/heif/color-p3.heic(ICC 优先用例)");
            return 1;
        }

        var container = HeifContainer.TryParse(File.ReadAllBytes(path), out var parseReason);
        var icc = container?.Color.IccProfile;
        if (icc is null || icc.Length == 0)
        {
            Console.WriteLine($"  ! 真实夹具未解析出 ICC:{parseReason ?? "colr 无 ICC"}");
            return 1;
        }

        var failures = 0;
        const int depth = 10;
        var withIccDifferentNclx = Synthetic(
            depth, HeifChromaSampling.Yuv444,
            lumaAt: (_, _) => 300, cbAt: (_, _) => 400, crAt: (_, _) => 600,
            primaries: 9, transfer: 1, fullRange: true, icc: icc);
        var withIccSrgbNclx = Synthetic(
            depth, HeifChromaSampling.Yuv444,
            lumaAt: (_, _) => 300, cbAt: (_, _) => 400, crAt: (_, _) => 600,
            primaries: 1, transfer: 13, fullRange: true, icc: icc);

        var different = EncodeF16(withIccDifferentNclx, out var differentReason);
        var srgbNclx = EncodeF16(withIccSrgbNclx, out var srgbReason);
        if (different is null || srgbNclx is null)
        {
            Console.WriteLine($"  ! 真实 ICC 编码值出口被拒绝:{differentReason ?? srgbReason}");
            return 1;
        }

        if (!different.AsSpan().SequenceEqual(srgbNclx))
        {
            Console.WriteLine("  ! 真实 ICC 优先:同一 ICC 下 nclx(BT.2020+709 曲线)改变了码值出口");
            failures++;
        }
        else
        {
            Console.WriteLine($"  ok 真实 ICC 优先:{icc.Length}B profile 下 nclx 原色/传递函数不参与码值出口");
        }

        // 无 ICC 时同一 nclx 必须真的算出不同颜色,证明上面的"不变性"不是空断言。
        var noIccDifferentNclx = Synthetic(
            depth, HeifChromaSampling.Yuv444,
            lumaAt: (_, _) => 300, cbAt: (_, _) => 400, crAt: (_, _) => 600,
            primaries: 9, transfer: 1, fullRange: true);
        if (!Convert(noIccDifferentNclx, out var plain, out var plainReason))
        {
            Console.WriteLine($"  ! 无 ICC 的 BT.2020+709 曲线被拒绝:{plainReason}");
            return failures + 1;
        }

        var shifted = 0;
        for (var i = 0; i < plain.Length; i += 4)
        {
            for (var c = 0; c < 3; c++)
            {
                var sampled = (int)MathF.Round(different[i + c] / (float)ushort.MaxValue * 255f);
                shifted = Math.Max(shifted, Math.Abs(sampled - plain[i + c]));
            }
        }

        // 不同原色/曲线在这组码值上至少差出十几个 8 位码值;否则不变性用例没有区分度。
        if (shifted < 10)
        {
            Console.WriteLine($"  ! ICC 与 nclx 的解释差异只有 {shifted} 个码值,用例区分度不足");
            failures++;
        }
        else
        {
            Console.WriteLine($"  ok 同一 nclx 无 ICC 时与 ICC 解释相差 {shifted} 个码值(说明用例有区分度)");
        }

        return failures;
    }

    /// <summary>
    /// 高位深码值精度:10/12 位源经 ICC 编码值出口后,半浮点码值必须保留源码值(不先量化到 8 位);
    /// 与 8 位出口相比,前者对"非 8 位对齐码值"的误差必须明显更小。
    /// </summary>
    private static int HighBitDepthCases()
    {
        var failures = 0;
        foreach (var (depth, codes) in new (int Depth, int[] Codes)[] { (10, new[] { 5, 7, 9 }), (12, new[] { 17, 33, 49 }) })
        {
            var icc = new byte[128];
            icc[0] = 0x5A;
            var mid = 1 << (depth - 1);
            var source = Synthetic(
                depth, HeifChromaSampling.Yuv444,
                lumaAt: (_, _) => codes[0], cbAt: (_, _) => mid, crAt: (_, _) => mid,
                primaries: 1, transfer: 13, fullRange: true, icc: icc);

            var half = EncodeF16(source, out var reason);
            if (half is null)
            {
                Console.WriteLine($"  ! {depth} 位码值:ICC 半浮点出口被拒绝:{reason}");
                failures++;
                continue;
            }

            // 恒定 Y/Cb/Cr + 中性色度 ⇒ 码值应等于 Y/((1<<depth)-1)(矩阵反解回同一灰度)。
            var exact = codes[0] / (float)((1 << depth) - 1);
            var f16 = (float)(Half)BitConverter.UInt16BitsToHalf((ushort)(half[0] | (half[1] << 8)));
            var quantized8 = plainByte(source, out var plainReason);
            if (quantized8 < 0)
            {
                Console.WriteLine($"  ! {depth} 位码值:8 位出口被拒绝:{plainReason}");
                failures++;
                continue;
            }

            var f16Error = Math.Abs(f16 - exact);
            var byte8Error = Math.Abs((quantized8 / 255f) - exact);
            if (f16Error > 1.5e-4f)
            {
                Console.WriteLine($"  ! {depth} 位码值:半浮点 {f16:F6} 与源码值 {exact:F6} 相差 {f16Error:E2}(应 ≤ 1.5e-4)");
                failures++;
                continue;
            }

            if (byte8Error < 4f * f16Error || byte8Error < 5e-5f)
            {
                Console.WriteLine(
                    $"  ! {depth} 位码值:8 位出口误差 {byte8Error:E2} 未明显大于半浮点误差 {f16Error:E2}(用例失去区分度)");
                failures++;
                continue;
            }

            Console.WriteLine(
                $"  ok {depth} 位码值:半浮点 {f16:F6} vs 源 {exact:F6}(误差 {f16Error:E2});"
                + $" 8 位出口 {quantized8}/255(误差 {byte8Error:E2},约 {byte8Error / f16Error:F0} 倍)");
        }

        return failures;
    }

    /// <summary>
    /// Windows 回落用的 skcms 传递函数映射必须与共享转换器的曲线同值:同一条 nclx 传递特性,
    /// 一边走共享 <see cref="YuvImageConverter"/> 的曲线,一边走 <see cref="LibHeifDecoder.TransferFnOf"/>
    /// 给出的 Skia 传递函数,8 位结果相差不得超过 1 级。
    /// </summary>
    /// <remarks>
    /// 这条用例把 Windows 回落里"标签词汇表达不了的曲线"与共享实现钉在一起:伽马 2.2/2.8 用的是纯幂参数
    /// (a=1、其余为 0),BT.709 曲线族用 Skia 的 Rec2020 预设;任何一边改错都会被这条断言抓到。
    /// </remarks>
    private static int FallbackTransferCases()
    {
        var failures = 0;
        foreach (var (code, label) in new (int Code, string Label)[]
                 {
                     (13, "sRGB(13)"),
                     (2, "未指定(2)→sRGB"),
                     (1, "BT.709(1)"),
                     (6, "BT.601(6)"),
                     (4, "伽马 2.2(4)"),
                     (5, "伽马 2.8(5)"),
                 })
        {
            var luma = 51; // 8 位码值 0.2:各曲线的分歧在这附近最明显。
            var source = Synthetic(
                8, HeifChromaSampling.Yuv444,
                lumaAt: (_, _) => luma, cbAt: (_, _) => 128, crAt: (_, _) => 128,
                primaries: 1, transfer: code, fullRange: true);
            if (!Convert(source, out var bgra, out var reason))
            {
                Console.WriteLine($"  ! 回落曲线 {label}: 共享转换被拒绝:{reason}");
                failures++;
                continue;
            }

            using var space = SkiaSharp.SKColorSpace.CreateRgb(
                PhotoViewer.Windows.Core.LibHeifDecoder.TransferFnOf(code),
                PhotoViewer.Windows.Core.LibHeifDecoder.XyzOf(1));
            var linear = Reference.ToLinearF16WithSpace(new[] { (byte)luma, (byte)luma, (byte)luma, (byte)255 }, 1, 1, space);
            var skiaLinear = (float)(Half)BitConverter.UInt16BitsToHalf((ushort)(linear[0] | (linear[1] << 8)));
            var skiaCode = Quantize(EncodeSrgb(skiaLinear));

            var diff = Math.Abs(skiaCode - bgra[0]);
            if (diff > 1)
            {
                Console.WriteLine($"  ! 回落曲线 {label}: skcms 给 {skiaCode},共享转换器给 {bgra[0]}(相差 {diff})");
                failures++;
                continue;
            }

            Console.WriteLine($"  ok 回落曲线 {label}: skcms {skiaCode} vs 共享转换器 {bgra[0]}");
        }

        return failures;
    }

    /// <summary>把合成图走 ICC 半浮点出口,返回原始缓冲(逐像素 RGBA 半浮点)。</summary>
    private static byte[]? EncodeF16(YuvImage source, out string? reason)
    {
        var buffer = new byte[source.CanvasWidth * source.CanvasHeight * 8];
        return YuvImageConverter.TryConvertToIccEncodedRgbaF16(
            source, source.CanvasWidth, source.CanvasHeight, buffer, source.CanvasWidth * 8, out reason)
            ? buffer
            : null;
    }

    /// <summary>把合成图走 ICC 8 位出口,返回左上角像素的 R 通道(RGBA 顺序的是 B 在前,这里取 R)。</summary>
    private static int plainByte(YuvImage source, out string? reason)
    {
        var buffer = new byte[source.CanvasWidth * source.CanvasHeight * 4];
        if (!YuvImageConverter.TryConvertToIccEncodedBgra32(
                source, source.CanvasWidth, source.CanvasHeight, buffer, source.CanvasWidth * 4, out reason))
        {
            return -1;
        }

        return buffer[2];
    }

    /// <summary>按文件名在仓库里找夹具:从当前目录与程序目录逐级向上找 <c>Fixtures/heif/&lt;name&gt;</c>。</summary>
    /// <param name="name">夹具文件名。</param>
    /// <returns>绝对路径;找不到为 null。</returns>
    internal static string? FindFixture(string name)
    {
        foreach (var start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        {
            var directory = new DirectoryInfo(start);
            while (directory is not null)
            {
                var candidate = Path.Combine(directory.FullName, "Fixtures", "heif", name);
                if (File.Exists(candidate)) return candidate;
                directory = directory.Parent;
            }
        }

        return null;
    }

    /// <summary>传递函数:BT.709(1/6/14/15)与 sRGB(2/13)必须给出各自的曲线,线性(8)直通。</summary>
    private static int TransferCurveCases()
    {
        var failures = 0;
        foreach (var (code, label, linearize) in new (int Code, string Label, Func<float, float> Linearize)[]
                 {
                     (13, "sRGB(13)", SrgbToLinear),
                     (2, "未指定(2)→sRGB", SrgbToLinear),
                     (1, "BT.709(1)", Bt709ToLinear),
                     (6, "BT.601(6)→BT.709 曲线", Bt709ToLinear),
                     (14, "BT.2020 10 位(14)", Bt709ToLinear),
                     (4, "伽马 2.2(4)", static value => MathF.Pow(value, 2.2f)),
                     (5, "伽马 2.8(5)", static value => MathF.Pow(value, 2.8f)),
                     (8, "线性(8)", static value => value),
                 })
        {
            // 恒定亮度码值 0.2:三种曲线的结果各不相同,任何"统一按 sRGB"都会被抓到。
            const int depth = 10;
            const int maximum = (1 << depth) - 1;
            var luma = (int)MathF.Round(0.2f * maximum);
            var source = Synthetic(
                depth,
                HeifChromaSampling.Yuv444,
                lumaAt: (_, _) => luma,
                cbAt: (_, _) => 1 << (depth - 1),
                crAt: (_, _) => 1 << (depth - 1),
                primaries: 1,
                transfer: code,
                fullRange: true);

            var expected = (byte)Quantize(EncodeSrgb(linearize(0.2f)));
            if (!Convert(source, out var bgra, out var reason))
            {
                Console.WriteLine($"  ! 传递特性 {label}: 被拒绝 {reason}");
                failures++;
                continue;
            }

            var actual = bgra[0];
            if (Math.Abs(actual - expected) > 1)
            {
                Console.WriteLine($"  ! 传递特性 {label}: 8 位值 {actual}, 期望 {expected}");
                failures++;
                continue;
            }

            Console.WriteLine($"  ok 传递特性 {label}: 8 位值 {actual}");
        }

        return failures;
    }

    /// <summary>原色:BT.2020/P3 必须按各自矩阵换算,中性灰在三套原色间保持不变。</summary>
    private static int PrimariesCases()
    {
        var failures = 0;
        foreach (var (code, label, matrix) in new (int Code, string Label, float[]? Matrix)[]
                 {
                     (1, "BT.709(1)", null),
                     (12, "Display P3(12)", P3ToSrgb),
                     (9, "BT.2020(9)", Bt2020ToSrgb),
                 })
        {
            // 中等饱和的红偏品:三套原色下的 sRGB 码值互不相同,又不至于全部被色域裁剪掩盖。
            var source = Synthetic(
                8,
                HeifChromaSampling.Yuv444,
                lumaAt: (_, _) => 120,
                cbAt: (_, _) => 140,
                crAt: (_, _) => 180,
                primaries: code,
                transfer: 13,
                fullRange: true);

            if (!Convert(source, out var bgra, out var reason))
            {
                Console.WriteLine($"  ! 原色 {label}: 被拒绝 {reason}");
                failures++;
                continue;
            }

            // 期望:先把 YCbCr 反矩阵成 8 位码值,再按原色矩阵换基色(与实现同源的独立算式)。
            var codeR = (120 / 255f) + (2f * (1f - 0.2126f) * ((180 - 128) / 255f));
            var codeG = (120 / 255f) - ((2f * 0.0722f * (1f - 0.0722f) / (1f - 0.2126f - 0.0722f)) * ((140 - 128) / 255f))
                - ((2f * 0.2126f * (1f - 0.2126f) / (1f - 0.2126f - 0.0722f)) * ((180 - 128) / 255f));
            var codeB = (120 / 255f) + (2f * (1f - 0.0722f) * ((140 - 128) / 255f));
            var lr = SrgbToLinear(Math.Clamp(codeR, 0f, 1f));
            var lg = SrgbToLinear(Math.Clamp(codeG, 0f, 1f));
            var lb = SrgbToLinear(Math.Clamp(codeB, 0f, 1f));
            var (er, eg, eb) = matrix is null ? (lr, lg, lb) : Apply(matrix, lr, lg, lb);
            var expected = new[]
            {
                (byte)Quantize(EncodeSrgb(eb)),
                (byte)Quantize(EncodeSrgb(eg)),
                (byte)Quantize(EncodeSrgb(er)),
            };

            var actual = new[] { bgra[0], bgra[1], bgra[2] };
            var worst = 0;
            for (var i = 0; i < 3; i++) worst = Math.Max(worst, Math.Abs(actual[i] - expected[i]));
            if (worst > 2)
            {
                Console.WriteLine($"  ! 原色 {label}: BGRA {actual[0]},{actual[1]},{actual[2]} 期望 {expected[0]},{expected[1]},{expected[2]}");
                failures++;
                continue;
            }

            Console.WriteLine($"  ok 原色 {label}: BGRA {actual[0]},{actual[1]},{actual[2]}(最大偏差 {worst})");
        }

        return failures;
    }

    /// <summary>ICC 编码值出口:只做反矩阵,不重复施加原色与传递函数。</summary>
    private static int IccCodeValueCases()
    {
        var failures = 0;
        const int depth = 10;
        var luma = 512;
        var cb = 400;
        var cr = 700;

        // 参照:源按 sRGB 曲线 + BT.709 原色解释时的 8 位输出(= 纯反矩阵 + 量化)。
        var reference = Synthetic(
            depth, HeifChromaSampling.Yuv422,
            lumaAt: (_, _) => luma, cbAt: (_, _) => cb, crAt: (_, _) => cr,
            primaries: 1, transfer: 13, fullRange: true);
        if (!Convert(reference, out var plain, out var plainReason))
        {
            Console.WriteLine("  ! ICC 参照转换被拒绝: " + plainReason);
            return 1;
        }

        // 同一源带 ICC,并把原色/传递改成 P3 + BT.709 曲线:ICC 优先时这些声明都不该生效。
        var withIcc = Synthetic(
            depth, HeifChromaSampling.Yuv422,
            lumaAt: (_, _) => luma, cbAt: (_, _) => cb, crAt: (_, _) => cr,
            primaries: 12, transfer: 1, fullRange: true,
            icc: FakeIcc(0x33));

        var encoded = new byte[withIcc.CanvasWidth * withIcc.CanvasHeight * 4];
        if (!YuvImageConverter.TryConvertToIccEncodedBgra32(
                withIcc, withIcc.CanvasWidth, withIcc.CanvasHeight, encoded, withIcc.CanvasWidth * 4, out var reason))
        {
            Console.WriteLine("  ! ICC 编码值出口被拒绝: " + reason);
            return 1;
        }

        var mismatch = 0;
        for (var i = 0; i < encoded.Length; i += 4)
        {
            for (var c = 0; c < 3; c++) mismatch = Math.Max(mismatch, Math.Abs(encoded[i + c] - plain[i + c]));
        }

        if (mismatch != 0)
        {
            Console.WriteLine($"  ! ICC 编码值出口与原色无关性: 最大偏差 {mismatch}(应逐字节一致)");
            failures++;
        }
        else
        {
            Console.WriteLine("  ok ICC 编码值出口: 与 nclx 无关,只做 YCbCr 反矩阵");
        }

        // 半浮点出口的码值必须与 8 位出口同一口径(半精度内)。
        var half = new byte[withIcc.CanvasWidth * withIcc.CanvasHeight * 8];
        if (!YuvImageConverter.TryConvertToIccEncodedRgbaF16(
                withIcc, withIcc.CanvasWidth, withIcc.CanvasHeight, half, withIcc.CanvasWidth * 8, out var halfReason))
        {
            Console.WriteLine("  ! ICC 半浮点出口被拒绝: " + halfReason);
            return failures + 1;
        }

        var halfMismatch = 0;
        for (var pixel = 0; pixel < withIcc.CanvasWidth * withIcc.CanvasHeight; pixel++)
        {
            var offset = pixel * 8;
            var r = (Half)BitConverter.UInt16BitsToHalf(BinaryPrimitives.ReadUInt16LittleEndian(half.AsSpan(offset)));
            var g = (Half)BitConverter.UInt16BitsToHalf(BinaryPrimitives.ReadUInt16LittleEndian(half.AsSpan(offset + 2)));
            var b = (Half)BitConverter.UInt16BitsToHalf(BinaryPrimitives.ReadUInt16LittleEndian(half.AsSpan(offset + 4)));
            halfMismatch = Math.Max(halfMismatch, Math.Abs(Quantize((float)r) - encoded[pixel * 4 + 2]));
            halfMismatch = Math.Max(halfMismatch, Math.Abs(Quantize((float)g) - encoded[pixel * 4 + 1]));
            halfMismatch = Math.Max(halfMismatch, Math.Abs(Quantize((float)b) - encoded[pixel * 4 + 0]));
        }

        if (halfMismatch > 1)
        {
            Console.WriteLine($"  ! ICC 半浮点出口与 8 位出口不一致: 最大偏差 {halfMismatch}");
            failures++;
        }
        else
        {
            Console.WriteLine($"  ok ICC 半浮点出口: 与 8 位出口一致(最大偏差 {halfMismatch})");
        }

        return failures;
    }

    /// <summary>支持面边界:HDR 与非支持原色必须显式拒绝,且原因可读。</summary>
    private static int UnsupportedCases()
    {
        var failures = 0;
        foreach (var (transfer, primaries, expectSupported, label) in new (int Transfer, int Primaries, bool ExpectSupported, string Label)[]
                 {
                     (16, 9, false, "PQ(16) HDR"),
                     (18, 9, false, "HLG(18) HDR"),
                     (7, 1, false, "SMPTE 240M 曲线(7)"),
                     (11, 1, false, "xvYCC(11)"),
                     (13, 5, false, "BT.470BG 原色(5)"),
                     (1, 9, true, "BT.2020 + BT.709 曲线"),
                     (4, 1, true, "伽马 2.2(4)"),
                     (5, 1, true, "伽马 2.8(5)"),
                 })
        {
            var source = Synthetic(
                8, HeifChromaSampling.Yuv444,
                lumaAt: (_, _) => 128, cbAt: (_, _) => 128, crAt: (_, _) => 128,
                primaries: primaries, transfer: transfer, fullRange: true);
            var supported = YuvImageConverter.IsSupported(source, out var reason);
            if (supported != expectSupported)
            {
                Console.WriteLine($"  ! 支持面 {label}: IsSupported={supported}, 期望 {expectSupported}({reason})");
                failures++;
                continue;
            }

            Console.WriteLine($"  ok 支持面 {label}: {(expectSupported ? "支持" : "拒绝")}"
                + (reason is null ? string.Empty : " — " + reason));
        }

        return failures;
    }

    private static bool Convert(YuvImage source, out byte[] bgra, out string? reason)
    {
        bgra = new byte[source.CanvasWidth * source.CanvasHeight * 4];
        return YuvImageConverter.TryConvertToBgra32(
            source, source.CanvasWidth, source.CanvasHeight, bgra, source.CanvasWidth * 4, out reason);
    }

    /// <summary>建一张 16×16、单瓦片、恒定 Y/Cb/Cr 的合成图。</summary>
    private static YuvImage Synthetic(
        int bitDepth,
        HeifChromaSampling chroma,
        Func<int, int, int> lumaAt,
        Func<int, int, int> cbAt,
        Func<int, int, int> crAt,
        int primaries,
        int transfer,
        bool fullRange,
        byte[]? icc = null)
    {
        const int width = 16;
        const int height = 16;
        var bytesPerSample = bitDepth > 8 ? 2 : 1;
        var chromaWidth = chroma == HeifChromaSampling.Yuv444 ? width : (width + 1) / 2;
        var chromaHeight = chroma == HeifChromaSampling.Yuv420 ? (height + 1) / 2 : height;
        if (chroma == HeifChromaSampling.Monochrome)
        {
            chromaWidth = 1;
            chromaHeight = 1;
        }

        var y = Fill(width, height, bitDepth, lumaAt);
        var cb = Fill(chromaWidth, chromaHeight, bitDepth, cbAt);
        var cr = Fill(chromaWidth, chromaHeight, bitDepth, crAt);
        var yHandle = System.Runtime.InteropServices.GCHandle.Alloc(y, System.Runtime.InteropServices.GCHandleType.Pinned);
        var cbHandle = System.Runtime.InteropServices.GCHandle.Alloc(cb, System.Runtime.InteropServices.GCHandleType.Pinned);
        var crHandle = System.Runtime.InteropServices.GCHandle.Alloc(cr, System.Runtime.InteropServices.GCHandleType.Pinned);

        // 句柄由进程生命周期兜住:本工具是短命验收进程,合成用例数量固定。
        var tile = new YuvTile
        {
            Y = yHandle.AddrOfPinnedObject(),
            Cb = cbHandle.AddrOfPinnedObject(),
            Cr = crHandle.AddrOfPinnedObject(),
            StrideY = width * bytesPerSample,
            StrideCb = Math.Max(4, chromaWidth) * bytesPerSample,
            StrideCr = Math.Max(4, chromaWidth) * bytesPerSample,
            Column = 0,
            Row = 0,
            Width = width,
            Height = height,
        };

        return new YuvImage
        {
            Tiles = new[] { tile },
            CanvasWidth = width,
            CanvasHeight = height,
            BitDepth = bitDepth,
            Chroma = chroma,
            MatrixCode = 1,
            PrimariesCode = primaries,
            TransferCode = transfer,
            FullRange = fullRange,
            IccProfile = icc,
        };
    }

    private static byte[] Fill(int width, int height, int bitDepth, Func<int, int, int> valueAt)
    {
        var stride = Math.Max(4, width) * (bitDepth > 8 ? 2 : 1);
        var plane = new byte[stride * height];
        for (var row = 0; row < height; row++)
        {
            for (var column = 0; column < width; column++)
            {
                var value = valueAt(column, row);
                var index = (row * stride) + (column * (bitDepth > 8 ? 2 : 1));
                if (bitDepth > 8)
                {
                    plane[index] = (byte)(value & 0xFF);
                    plane[index + 1] = (byte)(value >> 8);
                }
                else
                {
                    plane[index] = (byte)value;
                }
            }
        }
        return plane;
    }

    private static (float R, float G, float B) Apply(float[] matrix, float r, float g, float b)
        => (
            (matrix[0] * r) + (matrix[1] * g) + (matrix[2] * b),
            (matrix[3] * r) + (matrix[4] * g) + (matrix[5] * b),
            (matrix[6] * r) + (matrix[7] * g) + (matrix[8] * b));

    private static readonly float[] P3ToSrgb =
    {
        1.2249401f, -0.2249404f, 0.0000000f,
        -0.0420569f, 1.0420569f, 0.0000000f,
        -0.0196376f, -0.0786361f, 1.0982735f,
    };

    private static readonly float[] Bt2020ToSrgb =
    {
        1.6604910f, -0.5876411f, -0.0728499f,
        -0.1245505f, 1.1328999f, -0.0083494f,
        -0.0181508f, -0.1005789f, 1.1187297f,
    };

    private static float SrgbToLinear(float value)
        => value <= 0.04045f ? value / 12.92f : MathF.Pow((value + 0.055f) / 1.055f, 2.4f);

    private static float Bt709ToLinear(float value)
        => value <= 0.081f ? value / 4.5f : MathF.Pow((value + 0.099f) / 1.099f, 1f / 0.45f);

    private static float EncodeSrgb(float linear)
        => linear <= 0.0031308f ? linear * 12.92f : (1.055f * MathF.Pow(linear, 1f / 2.4f)) - 0.055f;

    private static int Quantize(float gamma)
    {
        var value = gamma * 255f;
        if (!(value > 0f)) return 0;
        if (value >= 255f) return 255;
        return (int)(value + 0.5f);
    }
}
