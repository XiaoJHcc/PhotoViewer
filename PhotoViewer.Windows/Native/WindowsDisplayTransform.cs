using System;
using System.Buffers.Binary;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

namespace PhotoViewer.Windows.Native;

internal sealed partial class WindowsDisplayTransform
{
    internal static WindowsDisplayTransform Srgb { get; } = new("sRGB fallback", null, 0);
    internal static WindowsDisplayTransform SystemManaged { get; } = new("Windows Advanced Color", null, 0);
    private static readonly double[] WideToXyz =
        [0.7976749, 0.1351917, 0.0313534, 0.2880402, 0.7118741, 0.0000857, 0, 0, 0.8252100];
    internal static readonly float[] LinearSrgbToWide = Multiply(Inverse(WideToXyz),
        [0.4360747, 0.3850649, 0.1430804, 0.2225045, 0.7168786, 0.0606169, 0.0139322, 0.0971045, 0.7141733]);

    internal string Description { get; }
    internal float[]? Lut { get; }
    internal int Size { get; }

    /// <summary>
    /// SDR 白电平倍数：HDR 下显示屏 SDR 白相对参考白的倍数（1 = 不缩放）。
    /// 只作用于无 LUT 的线性映射（系统托管 ACM/HDR），GPU 侧单独一个 pass 乘它；XAML 位图不预乘。
    /// </summary>
    internal float SdrWhiteScale { get; }

    private WindowsDisplayTransform(string description, float[]? lut, int size, float sdrWhiteScale = 1)
    {
        Description = description;
        Lut = lut;
        Size = size;
        SdrWhiteScale = sdrWhiteScale;
    }

    /// <summary>
    /// 系统托管（ACM/HDR）变换：白电平为 1 时返回共享的 <see cref="SystemManaged"/>，
    /// 否则返回无 LUT、只带 <see cref="SdrWhiteScale"/> 的实例（线性域按该倍数缩放，编码与 XAML 路径不缩）。
    /// </summary>
    /// <param name="sdrWhiteScale">Windows 报告的 SDR 白电平倍数（<c>SDRWhiteLevel / 1000</c>）；非有限、≤0 或约等于 1 时按 1 处理。</param>
    /// <returns>系统托管变换。</returns>
    internal static WindowsDisplayTransform ForSystemManaged(float sdrWhiteScale)
    {
        if (!float.IsFinite(sdrWhiteScale) || sdrWhiteScale <= 0) return SystemManaged;
        if (MathF.Abs(sdrWhiteScale - 1) < 1e-3f) return SystemManaged;
        return new WindowsDisplayTransform(
            SystemManaged.Description + "; SDR white scale " + sdrWhiteScale.ToString("0.###", CultureInfo.InvariantCulture),
            null, 0, sdrWhiteScale);
    }

    internal static WindowsDisplayTransform FromProfile(byte[] profile, string description, int size = 65, bool useWcs = false)
    {
        if (size is < 2 or > 129) throw new ArgumentOutOfRangeException(nameof(size));
        if (!useWcs && TryCreateMatrixLut(profile, size) is { } matrixLut)
            return new WindowsDisplayTransform(description + "; skcms matrix/TRC", matrixLut, size);
        var sourceBytes = CreateWideProfile();
        var sourcePin = GCHandle.Alloc(sourceBytes, GCHandleType.Pinned);
        var targetPin = GCHandle.Alloc(profile, GCHandleType.Pinned);
        IntPtr source = IntPtr.Zero, target = IntPtr.Zero, transform = IntPtr.Zero;
        try
        {
            var sourceProfile = new Profile { Type = 2, Data = sourcePin.AddrOfPinnedObject(), Size = (uint)sourceBytes.Length };
            var targetProfile = new Profile { Type = 2, Data = targetPin.AddrOfPinnedObject(), Size = (uint)profile.Length };
            source = OpenColorProfile(ref sourceProfile, ProfileAccessRead, ProfileShareReadWrite, ProfileOpenExisting);
            if (source == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error(), "无法打开工作空间 ICC");
            target = OpenColorProfile(ref targetProfile, ProfileAccessRead, ProfileShareReadWrite, ProfileOpenExisting);
            if (target == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error(), "无法打开显示器 ICC");
            transform = CreateMultiProfileTransform([source, target], 2, [1u, 1u], 2, 3, 0);
            if (transform == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error(), "无法建立显示器 ICC 转换");
            int count = checked(size * size * size);
            var input = new Color[count];
            var output = new Color[count];
            for (int b = 0, at = 0; b < size; b++)
                for (int g = 0; g < size; g++)
                    for (int r = 0; r < size; r++, at++)
                        input[at] = new Color { R = Quantize(r / (double)(size - 1)), G = Quantize(g / (double)(size - 1)), B = Quantize(b / (double)(size - 1)) };
            if (!TranslateColors(transform, input, (uint)count, 2, output, 2))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "显示器 ICC 查表转换失败");
            var lut = new float[count * 4];
            for (int i = 0; i < count; i++)
            {
                lut[i * 4] = output[i].R / 65535f;
                lut[i * 4 + 1] = output[i].G / 65535f;
                lut[i * 4 + 2] = output[i].B / 65535f;
                lut[i * 4 + 3] = 1;
            }
            return new WindowsDisplayTransform(description, lut, size);
        }
        finally
        {
            if (transform != IntPtr.Zero) DeleteColorTransform(transform);
            if (target != IntPtr.Zero) CloseColorProfile(target);
            if (source != IntPtr.Zero) CloseColorProfile(source);
            targetPin.Free();
            sourcePin.Free();
        }
    }

    /// <summary>
    /// 把线性 scRGB 映射到显示器线性域：有 LUT（SDR + 显示器 ICC 烘焙）走查表；
    /// 无 LUT（系统托管 ACM/HDR）只把线性值乘上 <see cref="SdrWhiteScale"/>（白电平 == 1 时即恒等）。
    /// </summary>
    internal (float R, float G, float B) MapLinear(float r, float g, float b)
    {
        if (Lut == null) return (r * SdrWhiteScale, g * SdrWhiteScale, b * SdrWhiteScale);
        var encoded = MapToDevice(r, g, b);
        return (Decode(encoded.R), Decode(encoded.G), Decode(encoded.B));
    }

    /// <summary>
    /// 映射到给 XAML 位图用的编码值。有 LUT 时走广色域查表；无 LUT（系统托管 ACM/HDR）时只做 sRGB 编码、
    /// **不乘 SDR 白电平** —— XAML 位图由系统按 SDR 处理，预乘会把它推到 HDR 白。
    /// </summary>
    internal (float R, float G, float B) MapToDevice(float r, float g, float b)
    {
        if (Lut == null) return (Encode(r), Encode(g), Encode(b));
        var m = LinearSrgbToWide;
        float x = Coordinate(m[0] * r + m[1] * g + m[2] * b);
        float y = Coordinate(m[3] * r + m[4] * g + m[5] * b);
        float z = Coordinate(m[6] * r + m[7] * g + m[8] * b);
        int ix = Math.Min((int)x, Size - 2), iy = Math.Min((int)y, Size - 2), iz = Math.Min((int)z, Size - 2);
        float fx = x - ix, fy = y - iy, fz = z - iz;
        return (Sample(0), Sample(1), Sample(2));

        float Coordinate(float value)
        {
            // NaN 一律当 0（否则 (int)NaN 是不确定值）；±Inf 由 Clamp 收敛到 0/1。
            if (float.IsNaN(value)) return 0;
            return MathF.Pow(Math.Clamp(value, 0, 1), 1f / 1.80078125f) * (Size - 1);
        }
        float Sample(int channel)
        {
            float result = 0;
            for (int dz = 0; dz <= 1; dz++)
                for (int dy = 0; dy <= 1; dy++)
                    for (int dx = 0; dx <= 1; dx++)
                        result += Lut[(((iz + dz) * Size + iy + dy) * Size + ix + dx) * 4 + channel]
                            * (dx == 0 ? 1 - fx : fx) * (dy == 0 ? 1 - fy : fy) * (dz == 0 ? 1 - fz : fz);
            return result;
        }
    }

    internal static float Decode(float value) => value <= 0.04045f ? value / 12.92f : MathF.Pow((value + 0.055f) / 1.055f, 2.4f);
    internal static float Encode(float value) => value <= 0.0031308f ? value * 12.92f : 1.055f * MathF.Pow(value, 1 / 2.4f) - 0.055f;
    private static ushort Quantize(double value) => (ushort)Math.Clamp(Math.Round(value * 65535), 0, 65535);

    private static byte[] CreateWideProfile()
    {
        (string Name, byte[] Data)[] tags =
        [
            ("desc", DescriptionTag("PhotoViewer wide gamut LUT input")),
            ("cprt", TextTag("PhotoViewer")),
            ("wtpt", XyzTag(0.9642, 1, 0.8249)),
            ("rXYZ", XyzTag(WideToXyz[0], WideToXyz[3], WideToXyz[6])),
            ("gXYZ", XyzTag(WideToXyz[1], WideToXyz[4], WideToXyz[7])),
            ("bXYZ", XyzTag(WideToXyz[2], WideToXyz[5], WideToXyz[8])),
            ("rTRC", CurveTag()), ("gTRC", CurveTag()), ("bTRC", CurveTag()),
        ];
        int length = 132 + tags.Length * 12;
        foreach (var tag in tags) length += (tag.Data.Length + 3) & ~3;
        var bytes = new byte[length];
        PutUInt(bytes, 0, (uint)length);
        PutUInt(bytes, 8, 0x02100000);
        PutText(bytes, 12, "mntr"); PutText(bytes, 16, "RGB "); PutText(bytes, 20, "XYZ ");
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(24), 2026);
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(26), 1);
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(28), 1);
        PutText(bytes, 36, "acsp"); PutText(bytes, 40, "MSFT");
        PutFixed(bytes, 68, 0.9642); PutFixed(bytes, 72, 1); PutFixed(bytes, 76, 0.8249);
        PutUInt(bytes, 128, (uint)tags.Length);
        int offset = 132 + tags.Length * 12;
        for (int i = 0; i < tags.Length; i++)
        {
            PutText(bytes, 132 + i * 12, tags[i].Name);
            PutUInt(bytes, 136 + i * 12, (uint)offset);
            PutUInt(bytes, 140 + i * 12, (uint)tags[i].Data.Length);
            tags[i].Data.CopyTo(bytes, offset);
            offset += (tags[i].Data.Length + 3) & ~3;
        }
        return bytes;
    }

    private static byte[] XyzTag(double x, double y, double z)
    {
        var bytes = new byte[20]; PutText(bytes, 0, "XYZ ");
        PutFixed(bytes, 8, x); PutFixed(bytes, 12, y); PutFixed(bytes, 16, z);
        return bytes;
    }
    private static byte[] CurveTag()
    {
        var bytes = new byte[14]; PutText(bytes, 0, "curv"); PutUInt(bytes, 8, 1);
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(12), (ushort)Math.Round(1.8 * 256));
        return bytes;
    }
    private static byte[] DescriptionTag(string value)
    {
        var bytes = new byte[12 + value.Length + 1 + 78]; PutText(bytes, 0, "desc");
        PutUInt(bytes, 8, (uint)value.Length + 1); PutText(bytes, 12, value); return bytes;
    }
    private static byte[] TextTag(string value)
    {
        var bytes = new byte[8 + value.Length + 1]; PutText(bytes, 0, "text"); PutText(bytes, 8, value); return bytes;
    }
    private static void PutText(byte[] bytes, int at, string value) => Encoding.ASCII.GetBytes(value).CopyTo(bytes, at);
    private static void PutUInt(byte[] bytes, int at, uint value) => BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(at), value);
    private static void PutFixed(byte[] bytes, int at, double value) => PutUInt(bytes, at, unchecked((uint)(int)Math.Round(value * 65536)));

    private static double[] Inverse(double[] a)
    {
        double d = a[0] * (a[4] * a[8] - a[5] * a[7]) - a[1] * (a[3] * a[8] - a[5] * a[6]) + a[2] * (a[3] * a[7] - a[4] * a[6]);
        return [(a[4]*a[8]-a[5]*a[7])/d, (a[2]*a[7]-a[1]*a[8])/d, (a[1]*a[5]-a[2]*a[4])/d,
            (a[5]*a[6]-a[3]*a[8])/d, (a[0]*a[8]-a[2]*a[6])/d, (a[2]*a[3]-a[0]*a[5])/d,
            (a[3]*a[7]-a[4]*a[6])/d, (a[1]*a[6]-a[0]*a[7])/d, (a[0]*a[4]-a[1]*a[3])/d];
    }
    private static float[] Multiply(double[] a, double[] b)
    {
        var c = new float[9];
        for (int y = 0; y < 3; y++) for (int x = 0; x < 3; x++)
            for (int k = 0; k < 3; k++) c[y * 3 + x] += (float)(a[y * 3 + k] * b[k * 3 + x]);
        return c;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Profile { public uint Type; public IntPtr Data; public uint Size; }

    /// <summary>OpenColorProfile: PROFILE_READ。</summary>
    private const uint ProfileAccessRead = 1;

    /// <summary>OpenColorProfile: FILE_SHARE_READ | FILE_SHARE_WRITE（显示 ICC 可能被系统工具同时改写，别独占）。</summary>
    private const uint ProfileShareReadWrite = 3;

    /// <summary>OpenColorProfile: OPEN_EXISTING。</summary>
    private const uint ProfileOpenExisting = 3;

    [StructLayout(LayoutKind.Explicit, Size = 16)]
    private struct Color
    {
        [FieldOffset(0)] public ushort R;
        [FieldOffset(2)] public ushort G;
        [FieldOffset(4)] public ushort B;
    }
    [DllImport("mscms.dll", EntryPoint = "OpenColorProfileW", SetLastError = true)]
    private static extern IntPtr OpenColorProfile(ref Profile profile, uint access, uint share, uint creation);
    [DllImport("mscms.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CloseColorProfile(IntPtr profile);
    [DllImport("mscms.dll", SetLastError = true)]
    private static extern IntPtr CreateMultiProfileTransform([In] IntPtr[] profiles, uint count, [In] uint[] intents, uint intentCount, uint flags, uint preferredCmm);
    [DllImport("mscms.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DeleteColorTransform(IntPtr transform);
    [DllImport("mscms.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool TranslateColors(IntPtr transform, [In] Color[] input, uint count, uint inputType, [Out] Color[] output, uint outputType);
}
