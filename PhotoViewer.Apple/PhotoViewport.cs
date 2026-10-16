namespace PhotoViewer.Apple;

/// <summary>
/// 照片视口的平台中立数学：适应缩放、锚点缩放、平移钳制、导航图几何与 100% 细节判定。
/// 只处理 double 与像素/点尺寸；宿主视图负责把结果换算成原生坐标类型并驱动重绘。
/// </summary>
public sealed class PhotoViewport
{
    /// <summary>导航图长边逻辑点，与原 Avalonia 缩放指示器一致。</summary>
    private const double NavigationLongEdge = 150;

    private double _relativePanX;
    private double _relativePanY;

    /// <summary>视图可用宽度（逻辑点）。</summary>
    public double ViewWidth { get; private set; }
    /// <summary>视图可用高度（逻辑点）。</summary>
    public double ViewHeight { get; private set; }
    /// <summary>视图物理倍率（Retina 2.0 等）。</summary>
    public double BackingScale { get; private set; } = 1;
    /// <summary>源像素宽度（未应用 EXIF 方向）。</summary>
    public int SourceWidth { get; private set; }
    /// <summary>源像素高度（未应用 EXIF 方向）。</summary>
    public int SourceHeight { get; private set; }
    /// <summary>EXIF 方向码，1 表示未旋转。</summary>
    public int Orientation { get; private set; } = 1;
    /// <summary>当前 GPU 纹理宽度（未应用 EXIF 方向）；0 表示没有纹理。</summary>
    public int TextureWidth { get; private set; }
    /// <summary>图像像素到视图点的当前缩放（点/像素）。</summary>
    public double Scale { get; private set; } = 1;
    /// <summary>照片中心相对视口中心的横向偏移（逻辑点）。</summary>
    public double PanX { get; private set; }
    /// <summary>照片中心相对视口中心的纵向偏移（逻辑点）。</summary>
    public double PanY { get; private set; }
    /// <summary>是否处于适应状态。</summary>
    public bool IsFit { get; private set; } = true;
    /// <summary>缩放是否跟随对焦点（无锚点的键盘放大）。</summary>
    public bool FollowsFocus { get; private set; }
    /// <summary>归一化对焦点横坐标；无对焦点时为 null。</summary>
    public double? FocusX { get; private set; }
    /// <summary>归一化对焦点纵坐标；无对焦点时为 null。</summary>
    public double? FocusY { get; private set; }

    /// <summary>显示朝向的逻辑图像宽（像素）。</summary>
    public double ImageWidth => Orientation >= 5 ? SourceHeight : SourceWidth;
    /// <summary>显示朝向的逻辑图像高（像素）。</summary>
    public double ImageHeight => Orientation >= 5 ? SourceWidth : SourceHeight;
    /// <summary>源像素对屏幕物理像素的倍率。</summary>
    public double PhysicalScale => Scale * BackingScale;
    /// <summary>当前视图尺寸下照片恰好放满的缩放（逻辑点/图像像素）。</summary>
    public double FitScale => ImageWidth > 0 && ImageHeight > 0
        ? Math.Min(ViewWidth / ImageWidth, ViewHeight / ImageHeight)
        : 1;
    /// <summary>当前全分辨率细节需求，包括代理覆盖不足的放大。</summary>
    public bool NeedsFull => !IsFit && (PhysicalScale >= 1
        || (TextureWidth > 0 && PhysicalScale > (double)TextureWidth / Math.Max(1, SourceWidth)));

    /// <summary>导航图逻辑点尺寸：长边 150、按显示朝向照片比例缩小，不补方形衬边；无照片时为 150 方形占位。</summary>
    public (double Width, double Height) NavigationSize
    {
        get
        {
            double imageWidth = ImageWidth, imageHeight = ImageHeight;
            if (imageWidth <= 0 || imageHeight <= 0) return (NavigationLongEdge, NavigationLongEdge);
            double factor = NavigationLongEdge / Math.Max(imageWidth, imageHeight);
            return (imageWidth * factor, imageHeight * factor);
        }
    }

    /// <summary>导航图内的可见区域（导航图逻辑点坐标）；缩放小于适应时退化为整幅照片。</summary>
    public (double X, double Y, double Width, double Height) NavigationViewport
    {
        get
        {
            var (navigationWidth, navigationHeight) = NavigationSize;
            double displayedWidth = Math.Max(1, ImageWidth * Scale), displayedHeight = Math.Max(1, ImageHeight * Scale);
            var horizontal = NavigationAxis(navigationWidth, ViewWidth / displayedWidth, .5 - PanX / displayedWidth);
            var vertical = NavigationAxis(navigationHeight, ViewHeight / displayedHeight, .5 - PanY / displayedHeight);
            return (horizontal.Offset, vertical.Offset, horizontal.Extent, vertical.Extent);
        }
    }

    /// <summary>设置有对焦点的归一化位置；两个分量都传 null 表示当前照片没有对焦点。</summary>
    /// <param name="x">归一化横坐标。</param>
    /// <param name="y">归一化纵坐标。</param>
    public void SetFocus(double? x, double? y)
    {
        FocusX = x;
        FocusY = y;
    }

    /// <summary>更新视图可用尺寸与物理倍率；非适应状态下按倍率变化换算缩放，随后由调用方选择重新适应或钳制平移。</summary>
    /// <param name="width">视图宽度（逻辑点）。</param>
    /// <param name="height">视图高度（逻辑点）。</param>
    /// <param name="backingScale">视图物理倍率。</param>
    public void ResizeView(double width, double height, double backingScale)
    {
        if (!IsFit) Scale *= BackingScale / backingScale;
        BackingScale = backingScale;
        ViewWidth = width;
        ViewHeight = height;
    }

    /// <summary>替换照片纹理前记录相对平移，供新纹理就绪后还原视口位置。</summary>
    public void BeginImageChange()
    {
        _relativePanX = PanX / Math.Max(1, ImageWidth);
        _relativePanY = PanY / Math.Max(1, ImageHeight);
    }

    /// <summary>应用新照片的源尺寸、方向与纹理宽度，并按当前状态归位：适应 / 跟随对焦 / 还原相对平移。</summary>
    /// <param name="sourceWidth">源像素宽（未应用 EXIF 方向）。</param>
    /// <param name="sourceHeight">源像素高（未应用 EXIF 方向）。</param>
    /// <param name="orientation">EXIF 方向码。</param>
    /// <param name="textureWidth">实际纹理宽度。</param>
    public void ApplyImage(int sourceWidth, int sourceHeight, int orientation, int textureWidth)
    {
        SourceWidth = sourceWidth;
        SourceHeight = sourceHeight;
        Orientation = orientation;
        TextureWidth = textureWidth;
        if (IsFit) FitToView();
        else if (FollowsFocus && FocusX.HasValue && FocusY.HasValue) CenterFocus();
        else
        {
            PanX = _relativePanX * ImageWidth;
            PanY = _relativePanY * ImageHeight;
            ClampPan();
        }
    }

    /// <summary>纹理被移除（无照片）时清除纹理宽度；保留上一次源尺寸供导航图占位，与移除前的行为一致。</summary>
    public void ClearTexture() => TextureWidth = 0;

    /// <summary>适应视口，保持照片比例并在物理像素超采样代理上绘制。</summary>
    public void FitToView()
    {
        IsFit = true;
        FollowsFocus = false;
        if (ImageWidth > 0 && ImageHeight > 0) Scale = FitScale;
        PanX = 0;
        PanY = 0;
    }

    /// <summary>在适应与物理 100% 间切换；给定锚点或存在对焦点时保留对焦位置。</summary>
    /// <param name="anchor">视图坐标锚点；null 表示使用视口中心或对焦点。</param>
    public void ToggleFit((double X, double Y)? anchor)
    {
        if (!IsFit)
        {
            FitToView();
            return;
        }
        if (anchor == null && FocusX.HasValue && FocusY.HasValue)
        {
            Scale = 1 / BackingScale;
            IsFit = false;
            FollowsFocus = true;
            CenterFocus();
            return;
        }
        var point = anchor ?? (ViewWidth / 2, ViewHeight / 2);
        ZoomTo(1 / BackingScale, point.X, point.Y);
    }

    /// <summary>按物理倍率缩放并把显示朝向的归一化点移到视口中心（细节预览双击用）。</summary>
    /// <param name="u">归一化横坐标。</param>
    /// <param name="v">归一化纵坐标。</param>
    /// <param name="physicalScale">目标物理倍率（1 = 源像素对屏幕物理像素 1:1）。</param>
    public void FocusOnPoint(double u, double v, double physicalScale)
    {
        Scale = physicalScale / BackingScale;
        IsFit = false;
        FollowsFocus = false;
        PanX = (.5 - u) * ImageWidth * Scale;
        PanY = (.5 - v) * ImageHeight * Scale;
        ClampPan();
    }

    /// <summary>按比例放大或缩小，锚定指针或视口中心。</summary>
    /// <param name="factor">缩放倍率。</param>
    /// <param name="anchor">视图坐标锚点；null 表示视口中心。</param>
    public void ZoomBy(double factor, (double X, double Y)? anchor)
        => ZoomTo(Scale * factor, anchor?.X ?? ViewWidth / 2, anchor?.Y ?? ViewHeight / 2);

    /// <summary>跳到预设物理倍率，避免 Retina 下将 100% 解释为 2×2 屏幕像素。</summary>
    /// <param name="scale">目标物理倍率。</param>
    public void ZoomToPhysical(double scale) => ZoomTo(scale / BackingScale, ViewWidth / 2, ViewHeight / 2);

    /// <summary>按视图点位移平移照片（拖动/触控板），跟随对焦随之取消并保持边缘钳制。</summary>
    /// <param name="deltaX">横向位移（逻辑点）。</param>
    /// <param name="deltaY">纵向位移（逻辑点）。</param>
    public void PanBy(double deltaX, double deltaY)
    {
        FollowsFocus = false;
        PanX += deltaX;
        PanY += deltaY;
        ClampPan();
    }

    /// <summary>
    /// 宿主手势驱动（iOS 用 UIScrollView 提供回弹/惯性/捏合，把 zoomScale 与 contentOffset 换算后写入这里）：
    /// 缩放与平移一律按视口范围钳制，缩放贴合适应值即视为适应态，缩放上限与锚点缩放共用同一常量。
    /// </summary>
    /// <param name="scale">图像像素到逻辑点的缩放。</param>
    /// <param name="panX">照片中心相对视口中心的横向偏移（逻辑点）。</param>
    /// <param name="panY">照片中心相对视口中心的纵向偏移（逻辑点）。</param>
    public void ApplyGestureView(double scale, double panX, double panY)
    {
        Scale = Math.Clamp(scale, .01 / BackingScale, 16 / BackingScale);
        PanX = panX;
        PanY = panY;
        FollowsFocus = false;
        IsFit = Scale <= FitScale + 1e-6;
        ClampPan();
    }

    /// <summary>使至少一部分照片保持在视口中；小于视口的轴居中。</summary>
    public void ClampPan()
    {
        double maxX = Math.Max(0, (ImageWidth * Scale - ViewWidth) / 2);
        double maxY = Math.Max(0, (ImageHeight * Scale - ViewHeight) / 2);
        PanX = Math.Clamp(PanX, -maxX, maxX);
        PanY = Math.Clamp(PanY, -maxY, maxY);
    }

    /// <summary>把显示朝向的归一化图像点换算成视图坐标（逻辑点，顶边为原点）。</summary>
    /// <param name="u">归一化横坐标。</param>
    /// <param name="v">归一化纵坐标。</param>
    /// <returns>视图坐标中的点。</returns>
    public (double X, double Y) ViewPoint(double u, double v)
    {
        double width = ImageWidth * Scale, height = ImageHeight * Scale;
        return (ViewWidth / 2 - width / 2 + PanX + u * width, ViewHeight / 2 - height / 2 + PanY + v * height);
    }

    /// <summary>按锚点计算缩放后的平移，限制缩放范围。</summary>
    /// <param name="scale">目标点缩放。</param>
    /// <param name="anchorX">视图坐标锚点横坐标。</param>
    /// <param name="anchorY">视图坐标锚点纵坐标。</param>
    private void ZoomTo(double scale, double anchorX, double anchorY)
    {
        scale = Math.Clamp(scale, .01 / BackingScale, 16 / BackingScale);
        double ratio = scale / Math.Max(.000001, Scale);
        PanX = anchorX - ViewWidth / 2 - (anchorX - ViewWidth / 2 - PanX) * ratio;
        PanY = anchorY - ViewHeight / 2 - (anchorY - ViewHeight / 2 - PanY) * ratio;
        Scale = scale;
        IsFit = false;
        FollowsFocus = false;
        ClampPan();
    }

    /// <summary>以显示朝向的归一化对焦位置为视口中心，并约束边缘平移。</summary>
    private void CenterFocus()
    {
        if (FocusX is not { } focusX || FocusY is not { } focusY) return;
        PanX = (.5 - focusX) * ImageWidth * Scale;
        PanY = (.5 - focusY) * ImageHeight * Scale;
        ClampPan();
    }

    /// <summary>把一条轴上的可见比例与中心比例换算成导航图内的偏移和长度，并钳制在导航图范围内。</summary>
    /// <param name="navigationExtent">导航图该轴长度。</param>
    /// <param name="visibleFraction">视口占照片的比例。</param>
    /// <param name="centerFraction">视口中心在照片内的比例。</param>
    /// <returns>导航图内的偏移与长度。</returns>
    private static (double Offset, double Extent) NavigationAxis(double navigationExtent, double visibleFraction, double centerFraction)
    {
        double extent = Math.Clamp(Math.Clamp(visibleFraction, 0, 1) * navigationExtent, Math.Min(1, navigationExtent), navigationExtent);
        double offset = centerFraction * navigationExtent - extent / 2;
        return (Math.Clamp(offset, 0, navigationExtent - extent), extent);
    }
}
