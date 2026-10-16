namespace PhotoViewer.Core.Image;

/// <summary>
/// 容器方向码(EXIF <c>Orientation</c> / HEIF 旋转)到"显示朝向"的几何映射。
/// 旋转与镜像口径与 <see cref="SkiaImageCodec.ApplyOrientation"/> 完全一致(先水平镜像,再顺时针旋转),
/// 是全仓唯一的方向映射表 — <see cref="PhotoViewer.Core.Color.ColorEngine.ToAnalysisSrgb8"/> 的方向变换与原生细节预览的
/// 裁剪点反映射都从这里取,避免两处表格漂移。
/// </summary>
public static class ImageOrientationMapping
{
    /// <summary>容器朝向图像变换到显示朝向所需的顺时针旋转角与水平镜像(镜像先于旋转)。</summary>
    /// <param name="orientation">容器方向码(1..8);非法值按 1 处理。</param>
    /// <returns>顺时针旋转角(0/90/180/270)与是否水平镜像。</returns>
    public static (int RotationCw, bool Mirror) ToDisplay(int orientation) => orientation switch
    {
        2 => (0, true),
        3 => (180, false),
        4 => (180, true),
        5 => (270, true),
        6 => (90, false),
        7 => (90, true),
        8 => (270, false),
        _ => (0, false),
    };

    /// <summary>方向码是否需要交换显示宽高(含 90°/270° 旋转的方向 5–8)。</summary>
    /// <param name="orientation">容器方向码。</param>
    /// <returns>显示朝向相对容器朝向宽高互换时为 true。</returns>
    public static bool SwapsAxes(int orientation) => ToDisplay(orientation).RotationCw is 90 or 270;

    /// <summary>
    /// 把显示朝向的归一化点反映射回容器(传感器)朝向的归一化点。
    /// 用于从容器朝向的原始像素里裁剪"显示朝向下该位置"的 1:1 细节块。
    /// </summary>
    /// <param name="orientation">容器方向码(1..8);非法值按 1 处理。</param>
    /// <param name="u">显示朝向归一化横坐标(0..1)。</param>
    /// <param name="v">显示朝向归一化纵坐标(0..1)。</param>
    /// <returns>容器朝向的归一化坐标。</returns>
    public static (double U, double V) DisplayToSource(int orientation, double u, double v) => orientation switch
    {
        2 => (1 - u, v),
        3 => (1 - u, 1 - v),
        4 => (u, 1 - v),
        5 => (1 - v, 1 - u),
        6 => (v, 1 - u),
        7 => (v, u),
        8 => (1 - v, u),
        _ => (u, v),
    };
}
