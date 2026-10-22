using System.Runtime.InteropServices;
using PhotoViewer.Core.Color;
using PhotoViewer.Core.Image;

namespace PhotoViewer.Apple;

/// <summary>
/// Apple 原生壳共用的程序化验收判定（Mac 的 --pv-native-smoke 与 iOS 的 --pv-native-photo-check 同一口径）：
/// GPU EXIF 八方向角点核对、色卡中心像素与期望的线性 scRGB 值核对。
/// 只依赖共享解码/渲染产物与 Core 色彩引擎，不含任何平台视图类型。
/// </summary>
public static class AppleAcceptanceChecks
{
    /// <summary>导航图线性帧内的四个采样点（显示朝向归一化坐标），取自原色区内部以避开 mipmap 边缘。</summary>
    private static readonly (double Horizontal, double Vertical)[] Corners = [(.05, .05), (.95, .05), (.05, .95), (.95, .95)];

    /// <summary>
    /// 每种 EXIF 方向下，显示朝向四角（左上/右上/左下/右下）对应的容器朝向原角编号（0=左上红、1=右上蓝、2=左下白、3=右下灰）。
    /// </summary>
    private static readonly int[][] SourceCorners = [[0, 1, 2, 3], [1, 0, 3, 2], [3, 2, 1, 0], [2, 3, 0, 1], [0, 2, 1, 3], [2, 0, 3, 1], [3, 1, 2, 0], [1, 3, 0, 2]];

    /// <summary>
    /// 以 EXIF 标准角点映射验证 GPU 八种旋转/镜像，采样原色区内部以避开 mipmap 边缘。
    /// </summary>
    /// <param name="captureNavigationFrame">按导航图尺寸抓取线性 RGBA16F GPU 帧的回调（F16 线性 scRGB）。</param>
    /// <param name="orientation">容器方向码 1..8。</param>
    public static void VerifyOrientation(Func<bool, DecodedImage> captureNavigationFrame, int orientation)
    {
        using var frame = captureNavigationFrame(true);
        if (frame.Encoding != PixelEncoding.RgbaF16) throw new InvalidOperationException("线性帧读回必须是 RGBA16F");
        double width = frame.Width;
        double height = frame.Height;
        for (int corner = 0; corner < Corners.Length; corner++)
        {
            int horizontal = (int)(width * Corners[corner].Horizontal);
            int vertical = (int)(height * Corners[corner].Vertical);
            var values = MemoryMarshal.Cast<byte, Half>(frame.Pixels.AsSpan(vertical * frame.Stride + horizontal * 8, 8));
            int original = SourceCorners[orientation - 1][corner];
            float red = (float)values[0], green = (float)values[1], blue = (float)values[2];
            bool valid = original switch
            {
                0 => red > .9f && green < .05f && blue < .05f,
                1 => blue > .9f && red < .05f && green < .05f,
                2 => red > .9f && green > .9f && blue > .9f,
                _ => red > .025f && red < .08f && Math.Abs(red - green) < .01f && Math.Abs(red - blue) < .01f,
            };
            if (!valid) throw new InvalidOperationException($"GPU EXIF {orientation} 角点 {corner} 方向错误：{red}/{green}/{blue}");
        }
    }

    /// <summary>
    /// 色卡期望（扩展线性 sRGB，红原色块中心）：按展示编码给出红通道下界与通道约束。
    /// 8 位 Display P3 立方体装不下的原色只能钳到 P3 原色——Adobe RGB 红原色需 1.3985，P3 路径实测 ≈1.2145，
    /// 故其下界按 P3 钳位值给（这属于 8 位 P3 编码的既有边界，非解码错误）。
    /// </summary>
    /// <param name="filename">色卡文件名。</param>
    /// <param name="displayP3">是否为 8 位 Display P3 展示产物。</param>
    /// <returns>红通道下界、绿通道上界（P3 红原色在 scRGB 下为负）、是否要求绿蓝接近零（sRGB 红原色）。</returns>
    public static (float RedFloor, float GreenCeiling, bool RequiresNeutralChroma) ColorExpectation(string filename, bool displayP3)
    {
        string name = System.IO.Path.GetFileNameWithoutExtension(filename);
        return name switch
        {
            "color-srgb" => (.98f, .02f, true),
            "color-p3" => (1.15f, -.02f, false),
            "color-adobe" or "color-untagged-adobe" => (displayP3 ? 1.18f : 1.3f, .02f, false),
            _ => throw new ArgumentOutOfRangeException(nameof(filename), $"未登记的色卡 {filename}"),
        };
    }

    /// <summary>
    /// 用已知测试色块检验解码通道与行朝向：把展示产物归一到线性 scRGB 后读归一化点，P3/Adobe 红原色不能提前裁剪。
    /// </summary>
    /// <param name="image">展示产物（F16 线性 scRGB 或 8 位 Display P3，后者先经 <see cref="ColorEngine.ToScRgb16"/>）。</param>
    /// <param name="filename">色卡文件名（按文件名取期望值）。</param>
    /// <param name="u">归一化横坐标（容器朝向）。</param>
    /// <param name="v">归一化纵坐标（容器朝向）。</param>
    public static void VerifyColor(DecodedImage image, string filename, double u, double v)
    {
        var (red, green, blue) = ReadLinearPixel(image, u, v);
        var (floor, greenCeiling, neutralChroma) = ColorExpectation(filename, image.Encoding != PixelEncoding.RgbaF16);
        if (red < floor) throw new InvalidOperationException($"{filename} 红通道过小（{red:0.####} < {floor}），色域或 ICC 解释错误");
        if (green > greenCeiling) throw new InvalidOperationException($"{filename} 绿通道异常（{green:0.####} > {greenCeiling}）");
        if (neutralChroma && (Math.Abs(green) > .02f || Math.Abs(blue) > .02f))
            throw new InvalidOperationException($"{filename} 通道不中性（G={green:0.####} B={blue:0.####}），行朝向或色彩解释错误");
    }

    /// <summary>
    /// 读取归一化点的扩展线性 sRGB 值；8 位 Display P3 产物先经色彩引擎转换（与渲染器片元的 P3→scRGB 换算同口径）。
    /// </summary>
    /// <param name="image">展示产物。</param>
    /// <param name="u">归一化横坐标（容器朝向）。</param>
    /// <param name="v">归一化纵坐标（容器朝向）。</param>
    /// <returns>扩展线性 sRGB 三通道。</returns>
    public static (float Red, float Green, float Blue) ReadLinearPixel(DecodedImage image, double u, double v)
    {
        DecodedImage? converted = image.Encoding == PixelEncoding.RgbaF16 ? null : ColorEngine.ToScRgb16(image);
        var source = converted ?? image;
        try
        {
            int horizontal = Math.Clamp((int)(source.Width * u), 0, source.Width - 1);
            int vertical = Math.Clamp((int)(source.Height * v), 0, source.Height - 1);
            var values = MemoryMarshal.Cast<byte, Half>(source.Pixels.AsSpan(vertical * source.Stride + horizontal * 8, 8));
            return ((float)values[0], (float)values[1], (float)values[2]);
        }
        finally
        {
            converted?.Release();
        }
    }
}
