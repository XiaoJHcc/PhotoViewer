using System;
using System.Buffers.Binary;
using SkiaSharp;

namespace PhotoViewer.Windows.Native;

internal sealed partial class WindowsDisplayTransform
{
    /// <summary>ICC 头 + tag 表起点。</summary>
    private const int TagTableOffset = 132;

    /// <summary>XYZ 类 tag 的最小长度(签名 + 保留 + 3 个 s15Fixed16)。</summary>
    private const int XyzTagMinimum = 20;

    /// <summary>曲线类 tag 的最小长度(签名 + 保留 + 计数/函数类型)。</summary>
    private const int CurveTagMinimum = 12;

    /// <summary>参数曲线 tag 的最小长度(还需一个参数类型字节)。</summary>
    private const int ParametricCurveTagMinimum = 14;

    private const uint AcspSignature = 0x61637370;          // 'acsp'
    private const uint RgbDataSpace = 0x52474220;           // 'RGB '
    private const uint XyzPcs = 0x58595A20;                 // 'XYZ '
    private const uint XyzTagType = 0x58595A20;             // 'XYZ '
    private const uint CurveTagType = 0x63757276;           // 'curv'
    private const uint ParametricCurveTagType = 0x70617261; // 'para'

    /// <summary>矩阵/TRC 描述必需的标签:前三项是基色 XYZ,后三项是各通道传递曲线。</summary>
    private static readonly uint[] MatrixTagSignatures =
    [
        Signature("rXYZ"), Signature("gXYZ"), Signature("bXYZ"),
        Signature("rTRC"), Signature("gTRC"), Signature("bTRC"),
    ];

    /// <summary>
    /// A2B/B2A/D2B/B2D 类查找表标签:出现任意一个就说明 profile 用 LUT 描述转换,矩阵近似会丢掉校准。
    /// </summary>
    private static readonly uint[] LutTagSignatures = BuildLutSignatures();

    private static unsafe float[]? TryCreateMatrixLut(byte[] profile, int size)
    {
        // 先确认 profile 真的是"矩阵 + TRC":否则 skcms 只会读到 rXYZ/gXYZ/bXYZ,把 LUT 描述里的
        // 校准整段丢掉(退化成一个纯矩阵近似)。判定不通过就交回 WCS(mscms)完整解释。
        if (!IsMatrixTrcProfile(profile, out string reason))
        {
            NativeStartupLog.Write("display color: ICC 不走 skcms 矩阵(" + reason + "),改用 WCS");
            return null;
        }

        using var target = SKColorSpace.CreateIcc(profile);
        if (target == null || !target.ToColorSpaceXyz(out _))
        {
            NativeStartupLog.Write("display color: skcms 未能解析 matrix/TRC profile,改用 WCS");
            return null;
        }

        var matrix = new float[9];
        for (int i = 0; i < matrix.Length; i++) matrix[i] = (float)WideToXyz[i];
        using var source = SKColorSpace.CreateRgb(new SKColorSpaceTransferFn(1.80078125f, 1, 0, 0, 0, 0, 0), new SKColorSpaceXyz(matrix));
        int count = checked(size * size * size);
        var input = new float[count * 4];
        var output = new float[input.Length];
        for (int b = 0, at = 0; b < size; b++)
            for (int g = 0; g < size; g++)
                for (int r = 0; r < size; r++, at += 4)
                {
                    input[at] = r / (size - 1f); input[at + 1] = g / (size - 1f); input[at + 2] = b / (size - 1f); input[at + 3] = 1;
                }
        var src = new SKImageInfo(size, size * size, SKColorType.RgbaF32, SKAlphaType.Opaque, source);
        var dst = new SKImageInfo(size, size * size, SKColorType.RgbaF32, SKAlphaType.Opaque, target);
        fixed (float* inputPointer = input)
        fixed (float* outputPointer = output)
        {
            using var pixels = new SKPixmap(src, (IntPtr)inputPointer, size * 16);
            if (!pixels.ReadPixels(dst, (IntPtr)outputPointer, size * 16)) return null;
        }
        return output;
    }

    /// <summary>
    /// 按 ICC tag 表判定 profile 能否只用"矩阵 + TRC"表达:rXYZ/gXYZ/bXYZ 与 rTRC/gTRC/bTRC 齐备、
    /// 且没有任何 A2B/B2A/D2B/B2D 查找表标签;每个 tag 的 offset/size 都必须落在声明长度内。
    /// </summary>
    /// <remarks>
    /// ICC 规定 AToB/BToA 存在时优先于矩阵/TRC,skcms 却只按 rXYZ/gXYZ/bXYZ 建色域:对这类 profile 用矩阵
    /// 等于把查找表里的校准(含各亮度段的偏色修正)全部丢掉。这里只做字节级判定,不解释 tag 内容;
    /// 判定通过后仍由 skcms 自己解析,解析失败再回落 WCS。
    /// </remarks>
    /// <param name="profile">显示器 ICC 字节。</param>
    /// <param name="reason">不走矩阵时的原因(诊断用)。</param>
    /// <returns>可用矩阵/TRC 表达返回 true。</returns>
    private static bool IsMatrixTrcProfile(byte[] profile, out string reason)
    {
        reason = string.Empty;
        if (profile.Length < TagTableOffset)
        {
            reason = "长度不足 ICC 头";
            return false;
        }

        long declared = BinaryPrimitives.ReadUInt32BigEndian(profile);
        if (declared < TagTableOffset || declared > profile.Length)
        {
            reason = "声明长度 " + declared + " 与字节数 " + profile.Length + " 不符";
            return false;
        }

        int bound = (int)declared; // 只用声明长度内的字节,不读尾部填充
        if (ReadSignature(profile, 36) != AcspSignature)
        {
            reason = "缺 acsp 签名";
            return false;
        }

        if (ReadSignature(profile, 16) != RgbDataSpace)
        {
            reason = "数据颜色空间不是 RGB";
            return false;
        }

        if (ReadSignature(profile, 20) != XyzPcs)
        {
            reason = "PCS 不是 XYZ";
            return false;
        }

        long count = BinaryPrimitives.ReadUInt32BigEndian(profile.AsSpan(128, 4));
        if (count * 12 + TagTableOffset > bound)
        {
            reason = "tag 表越界(数量 " + count + ")";
            return false;
        }

        var found = new bool[MatrixTagSignatures.Length];
        for (long index = 0; index < count; index++)
        {
            int entry = TagTableOffset + (int)(index * 12);
            long offset = BinaryPrimitives.ReadUInt32BigEndian(profile.AsSpan(entry + 4, 4));
            long length = BinaryPrimitives.ReadUInt32BigEndian(profile.AsSpan(entry + 8, 4));
            var signature = ReadSignature(profile, entry);
            if (length <= 0 || offset < TagTableOffset || offset + length > bound)
            {
                reason = "tag " + Describe(signature) + " 的 offset/size 越界";
                return false;
            }

            if (Array.IndexOf(LutTagSignatures, signature) >= 0)
            {
                reason = "含 LUT 标签 " + Describe(signature);
                return false;
            }

            int required = Array.IndexOf(MatrixTagSignatures, signature);
            if (required < 0) continue;

            int dataOffset = (int)offset;
            int dataLength = (int)length;
            if (required < 3)
            {
                if (ReadSignature(profile, dataOffset) != XyzTagType || dataLength < XyzTagMinimum)
                {
                    reason = Describe(signature) + " 不是 XYZ 类型或长度不足";
                    return false;
                }
            }
            else
            {
                var type = ReadSignature(profile, dataOffset);
                if (type != CurveTagType && type != ParametricCurveTagType)
                {
                    reason = Describe(signature) + " 不是 curv/para 曲线";
                    return false;
                }

                if (dataLength < CurveTagMinimum || (type == ParametricCurveTagType && dataLength < ParametricCurveTagMinimum))
                {
                    reason = Describe(signature) + " 曲线长度不足";
                    return false;
                }
            }

            found[required] = true;
        }

        for (int i = 0; i < found.Length; i++)
        {
            if (found[i]) continue;
            reason = "缺 " + Describe(MatrixTagSignatures[i]);
            return false;
        }

        return true;
    }

    private static uint[] BuildLutSignatures()
    {
        string[] prefixes = ["A2B", "B2A", "D2B", "B2D"];
        var result = new uint[prefixes.Length * 4];
        for (int i = 0; i < prefixes.Length; i++)
            for (int index = 0; index < 4; index++)
                result[i * 4 + index] = Signature(prefixes[i] + (char)('0' + index));
        return result;
    }

    private static uint Signature(string name)
        => ((uint)name[0] << 24) | ((uint)name[1] << 16) | ((uint)name[2] << 8) | name[3];

    private static uint ReadSignature(byte[] bytes, int at)
        => BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(at, 4));

    private static string Describe(uint signature)
        => new(new[]
        {
            (char)(signature >> 24), (char)((signature >> 16) & 0xFF), (char)((signature >> 8) & 0xFF), (char)(signature & 0xFF),
        });
}
