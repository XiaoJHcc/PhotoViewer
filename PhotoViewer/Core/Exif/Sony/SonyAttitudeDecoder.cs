using System;
using MetadataExtractor.Formats.Exif.Makernotes;

namespace PhotoViewer.Core;

/// <summary>
/// Sony 0x940F 静照加速度计的姿态解码结果（数值版）。
/// 三轴按 tag 采样顺序导出（归一到 m/s²）；俯仰/横滚仅已校准机型有值。
/// </summary>
/// <param name="RawX">0x86 int16 LE（tag 采样顺序）</param>
/// <param name="RawY">0x88 int16 LE</param>
/// <param name="RawZ">0x8A int16 LE</param>
/// <param name="Ax">X 轴加速度（m/s²，本帧 |raw| 归一到标准重力）</param>
/// <param name="Ay">Y 轴加速度（m/s²）</param>
/// <param name="Az">Z 轴加速度（m/s²）</param>
/// <param name="PitchDeg">俯仰角（°，光轴相对水平仰角）；未校准机型为 null</param>
/// <param name="RollDeg">横滚角（°，绕光轴；竖拍 ≈ ±90°）；未校准机型为 null</param>
public readonly record struct AccelAttitude(
    short RawX, short RawY, short RawZ,
    double Ax, double Ay, double Az,
    double? PitchDeg, double? RollDeg);

/// <summary>
/// Sony 0x940F 静照加速度计姿态解码（公开数值 API）。
/// 显示层（<c>SonyCipherTags.DecodeAccelerometer940F</c>）与训练侧导出（DatasetBuilder
/// <c>--dump-accel</c>）共用同一数值真源，禁止分叉实现。
/// 未知机型轴序未标定，不臆造角度——单帧重力无法在不知轴映射时唯一确定横滚。
/// </summary>
public static class SonyAttitudeDecoder
{
    private const int TagId = 0x940F;
    private const int OffAx = 0x86;
    private const int OffAy = 0x88;
    private const int OffAz = 0x8A;
    private const int MinLen = OffAz + 2;
    // 标准重力加速度：本帧 |raw| 对应 1g，再换算为 m/s²
    private const double StandardGravity = 9.80665;

    /// <summary>
    /// 尝试解码 0x940F。数据不足/缺失返回 false；成功时三轴恒有值，
    /// pitch/roll 仅对已校准机型（ILCE-7CM2 / ILCE-6700）非空。
    /// </summary>
    /// <param name="directory">Sony MakerNote 目录</param>
    /// <param name="cameraModel">相机型号（如 "ILCE-6700"），用于选择是否/如何做轴映射</param>
    /// <param name="attitude">解码结果</param>
    public static bool TryDecode(
        SonyType1MakernoteDirectory directory, string? cameraModel, out AccelAttitude attitude)
    {
        attitude = default;

        var data = DecryptRaw(directory);
        if (data == null)
            return false;

        short rawX = BitConverter.ToInt16(data, OffAx);
        short rawY = BitConverter.ToInt16(data, OffAy);
        short rawZ = BitConverter.ToInt16(data, OffAz);

        // 本帧向量模长作 1g 刻度；静照 |g| 应接近常量，避免写死 8200 因机型/校准漂移
        double mag = Math.Sqrt((double)rawX * rawX + (double)rawY * rawY + (double)rawZ * rawZ);
        if (mag < 1.0)
            return false;

        // 三轴始终按 tag 采样顺序导出（归一到 m/s²）；轴序语义仅在已校准机型上与 body 一致
        double axMs2 = rawX / mag * StandardGravity;
        double ayMs2 = rawY / mag * StandardGravity;
        double azMs2 = rawZ / mag * StandardGravity;

        double? pitchDeg = null, rollDeg = null;
        if (TryMapAccelToBodyAxes(rawX, rawY, rawZ, cameraModel, out short bx, out short by, out short bz))
        {
            double bMag = Math.Sqrt((double)bx * bx + (double)by * by + (double)bz * bz);
            if (bMag >= 1.0)
            {
                double gx = bx / bMag;
                double gy = by / bMag;
                double gz = bz / bMag;
                // pitch = 光轴相对水平的仰角
                // roll  = 传感器平面内重力方位（绕光轴）；竖拍 ≈ ±90°
                // 注意：不要用 atan2(gx, √(gy²+gz²))，那会把俯仰泄漏进横滚。
                double horiz = Math.Sqrt(gx * gx + gy * gy);
                pitchDeg = Math.Atan2(gz, horiz) * (180.0 / Math.PI);
                rollDeg = Math.Atan2(gx, -gy) * (180.0 / Math.PI);
            }
        }

        attitude = new AccelAttitude(rawX, rawY, rawZ, axMs2, ayMs2, azMs2, pitchDeg, rollDeg);
        return true;
    }

    /// <summary>
    /// 诊断用：读取并解密 0x940F 块，返回明文字节（新机型轴序/偏移标定用）。
    /// 标签缺失或长度不足返回 null。
    /// </summary>
    public static byte[]? DecryptRaw(SonyType1MakernoteDirectory directory)
    {
        var obj = directory.GetObject(TagId);
        if (obj is not byte[] raw || raw.Length < MinLen)
            return null;
        var data = SonyCipherTags.Decipher(raw);
        return data.Length < MinLen ? null : data;
    }

    /// <summary>
    /// 将 0x940F raw 三轴映射到统一 body 系；仅对已校准机型返回 true。
    /// body 约定（A7C2 校准序列）：水平 ay≈−g；仰拍 +Z；右横滚 −X。
    /// <list type="bullet">
    /// <item>ILCE-7CM2：恒等 (rawX, rawY, rawZ)</item>
    /// <item>ILCE-6700：(-rawY, -rawZ, -rawX)（golden_star2 31 张穷举 48 置换唯一零坏点解）</item>
    /// </list>
    /// 未知机型返回 false——单帧重力在轴序未知时无法唯一确定 pitch/roll。
    /// </summary>
    private static bool TryMapAccelToBodyAxes(
        short rawX, short rawY, short rawZ, string? cameraModel,
        out short bodyX, out short bodyY, out short bodyZ)
    {
        bodyX = bodyY = bodyZ = 0;
        if (string.IsNullOrEmpty(cameraModel))
            return false;

        // ILCE-7CM2（A7C2）：tag 轴即 body 轴
        if (cameraModel.Contains("7CM2", StringComparison.OrdinalIgnoreCase))
        {
            bodyX = rawX;
            bodyY = rawY;
            bodyZ = rawZ;
            return true;
        }

        // ILCE-6700：body = (-rawY, -rawZ, -rawX)
        if (cameraModel.Contains("6700", StringComparison.OrdinalIgnoreCase))
        {
            // 饱和保护：-short.MinValue 会溢出
            static short Neg(short v) => v == short.MinValue ? short.MaxValue : (short)(-v);
            bodyX = Neg(rawY);
            bodyY = Neg(rawZ);
            bodyZ = Neg(rawX);
            return true;
        }

        return false;
    }
}
