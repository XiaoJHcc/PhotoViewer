using CoreGraphics;
using Foundation;
using PhotoViewer.Apple;
using PhotoViewer.Core.Image;
using PhotoViewer.Core.Memory;
using UIKit;

namespace PhotoViewer.iOS.Native;

/// <summary>
/// iPad 主图宿主（I2）：共享 <see cref="MetalPhotoRenderer"/> 画在 CAMetalLayer 子层上，
/// 视口数学取共享 <see cref="PhotoViewport"/>；缩放/平移/回弹/惯性交给覆盖其上的透明 <see cref="UIScrollView"/>，
/// 只把 zoomScale/contentOffset 映射成视口变换并在同一回调里重绘（避免滞后一帧）。
/// 手势是唯一交互真源：程序化改视口（适应/100% 双击）后按同一映射写回滚动视图。
/// </summary>
internal sealed class iOSPhotoView : UIView, IMemoryClient
{
    /// <summary>双击切换的物理倍率上限（与共享视口锚点缩放的 16 倍上限同口径）。</summary>
    private const double MaximumPhysicalScale = 16;

    private readonly PhotoViewport _viewport = new();
    private readonly MetalPhotoRenderer _renderer;
    private readonly UIScrollView _scroll = new();
    private bool _syncingScroll;

    /// <summary>视口变化通知，加载会话据此决定代理尺寸与 Full 升级。</summary>
    public event Action? ViewportChanged;

    /// <summary>共享视口（验收读取缩放/平移状态）。</summary>
    internal PhotoViewport Viewport => _viewport;
    /// <summary>缩放是否处于适应状态。</summary>
    internal bool IsFit => _viewport.IsFit;
    /// <summary>源像素对屏幕物理像素的倍率。</summary>
    internal double PhysicalScale => _viewport.PhysicalScale;
    /// <summary>当前全分辨率细节需求。</summary>
    internal bool NeedsFull => _viewport.NeedsFull;
    /// <summary>内存客户端名称。</summary>
    public string Name => "iOSPhotoView";
    /// <summary>纹理与三重缓冲的估算 GPU 字节（绘制目标恒为 RGBA16F）。</summary>
    public long FootprintBytes => _renderer.TextureBytes
        + (long)(_viewport.ViewWidth * _viewport.ViewHeight * _viewport.BackingScale * _viewport.BackingScale * 8 * 3);

    /// <summary>当前照片的归一化对焦点（共享视口 API，供后续对焦定位复用；本里程碑双击一律以点按位置为锚点）。</summary>
    internal CGPoint? FocusPoint
    {
        get => _viewport.FocusX is { } x && _viewport.FocusY is { } y ? new CGPoint(x, y) : null;
        set
        {
            if (value.HasValue) _viewport.SetFocus((double)value.Value.X, (double)value.Value.Y);
            else _viewport.SetFocus(null, null);
        }
    }

    /// <summary>建立共享 Metal 渲染核心并叠加透明滚动视图作手势与物理驱动。</summary>
    /// <param name="frame">初始 frame。</param>
    public iOSPhotoView(CGRect frame) : base(frame)
    {
        _renderer = new MetalPhotoRenderer(_viewport);
        Layer.AddSublayer(_renderer.Layer);

        BackgroundColor = UIColor.FromRGB(18, 18, 18);
        _scroll.BackgroundColor = UIColor.Clear;
        _scroll.ShowsHorizontalScrollIndicator = false;
        _scroll.ShowsVerticalScrollIndicator = false;
        _scroll.Bounces = true;
        _scroll.BouncesZoom = true;
        _scroll.DecelerationRate = UIScrollView.DecelerationRateNormal;
        _scroll.ContentInsetAdjustmentBehavior = UIScrollViewContentInsetAdjustmentBehavior.Never;
        _scroll.Delegate = new ScrollDelegate(this);
        AddSubview(_scroll);

        var doubleTap = new UITapGestureRecognizer(OnDoubleTap) { NumberOfTapsRequired = 2 };
        AddGestureRecognizer(doubleTap);
        MemoryGovernor.Register(this);
    }

    /// <summary>同步上传像素并重算视口；null 表示清空照片。</summary>
    /// <param name="image">展示产物（F16 线性 scRGB 或 8 位 Display P3），所有权仍在调用方。</param>
    public void SetImage(DecodedImage? image)
    {
        _viewport.BeginImageChange();
        _renderer.SetTexture(image);
        if (image == null) _viewport.ClearTexture();
        else _viewport.ApplyImage(image.SourceWidth, image.SourceHeight, image.Orientation, _renderer.TextureWidth);
        SyncScrollFromViewport();
        DrawFrame();
        ViewportChanged?.Invoke();
    }

    /// <summary>适应视口，保持照片比例并在物理像素超采样代理上绘制。</summary>
    public void FitToView()
    {
        _viewport.FitToView();
        SyncScrollFromViewport();
        DrawFrame();
        ViewportChanged?.Invoke();
    }

    /// <summary>跳到预设物理倍率（验收用；与双击的 100% 同意义）。</summary>
    /// <param name="scale">目标物理倍率（1 = 源像素对屏幕物理像素 1:1）。</param>
    public void ZoomToPhysical(double scale)
    {
        _viewport.ZoomToPhysical(scale);
        SyncScrollFromViewport();
        DrawFrame();
        ViewportChanged?.Invoke();
    }

    /// <summary>宿主布局变化：刷新物理尺寸、按当前状态归位并重绘。</summary>
    public override void LayoutSubviews()
    {
        base.LayoutSubviews();
        _renderer.Layer.Frame = Layer.Bounds;
        _scroll.Frame = Bounds;
        _renderer.ResizeDrawable((double)Bounds.Width, (double)Bounds.Height, DisplayScale);
        _viewport.ResizeView((double)Bounds.Width, (double)Bounds.Height, DisplayScale);
        if (_viewport.IsFit) _viewport.FitToView();
        else _viewport.ClampPan();
        SyncScrollFromViewport();
        DrawFrame();
        ViewportChanged?.Invoke();
    }

    /// <summary>生成单帧照片；变换与 EXIF 方向都由顶点阶段处理，缩小时使用三线性 mipmap。</summary>
    public void DrawFrame()
    {
        if ((double)Bounds.Width < 1 || (double)Bounds.Height < 1) return;
        _renderer.DrawFrame();
    }

    /// <summary>读取真实 GPU 照片帧，供自动验收；navigation=true 时输出导航图尺寸的等比小图。</summary>
    /// <param name="navigation">是否输出导航图尺寸的等比小图。</param>
    /// <returns>线性 RGBA16F 帧，缓冲由池化租约持有。</returns>
    public DecodedImage CaptureLinearFrame(bool navigation = false) => _renderer.CaptureLinearFrame(navigation);

    /// <summary>压力响应保留当前纹理，避免观片瞬间清屏；缓存由共享 Governor 修剪。</summary>
    /// <param name="level">压力级别。</param>
    public void Trim(PressureLevel level) { }

    /// <summary>释放 Metal 资源并注销内存客户端。</summary>
    /// <param name="disposing">是否释放托管资源。</param>
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            MemoryGovernor.Unregister(this);
            _renderer.Dispose();
        }
        base.Dispose(disposing);
    }

    /// <summary>屏幕物理倍率；未知时按 1 处理，LayoutSubviews 会随后刷新。</summary>
    private double DisplayScale
    {
        get
        {
            double scale = TraitCollection.DisplayScale;
            return scale > 0 ? scale : 1;
        }
    }

    /// <summary>双击：适应态 → 点按位置的物理 100%，否则回适应；两条路径都带 UIKit 动画。</summary>
    /// <param name="recognizer">双击识别器。</param>
    private void OnDoubleTap(UITapGestureRecognizer recognizer)
    {
        if (_viewport.IsFit)
        {
            var point = recognizer.LocationInView(this);
            _viewport.ToggleFit(((double)point.X, (double)point.Y));
        }
        else
        {
            _viewport.FitToView();
        }
        AnimateScrollToViewport();
        DrawFrame();
        ViewportChanged?.Invoke();
    }

    /// <summary>滚动视图完成捏合后：把缩放后的显示尺寸写回视口并在同一回调里重绘。</summary>
    /// <param name="scrollView">发送事件的滚动视图。</param>
    private void OnDidZoom(UIScrollView scrollView) => PullViewportFromScroll();

    /// <summary>滚动视图位移（拖动、惯性、回弹）后同步视口并重绘。</summary>
    /// <param name="scrollView">发送事件的滚动视图。</param>
    private void OnScrolled(UIScrollView scrollView)
    {
        if (!_syncingScroll) PullViewportFromScroll();
    }

    /// <summary>
    /// 把 scrollView 的 zoomScale/contentOffset 映射成视口缩放与平移：
    /// 内容尺寸 = 适应态显示尺寸，故缩放 = 适应缩放 × zoomScale；照片中心相对视口中心的偏移由可见内容原点反算。
    /// </summary>
    private void PullViewportFromScroll()
    {
        if (_syncingScroll || _viewport.ImageWidth <= 0 || _viewport.ImageHeight <= 0) return;
        double scale = _viewport.FitScale * (double)_scroll.ZoomScale;
        double displayedWidth = _viewport.ImageWidth * scale;
        double displayedHeight = _viewport.ImageHeight * scale;
        double panX = displayedWidth / 2 - (double)_scroll.ContentOffset.X - (double)Bounds.Width / 2;
        double panY = displayedHeight / 2 - (double)_scroll.ContentOffset.Y - (double)Bounds.Height / 2;
        _viewport.ApplyGestureView(scale, panX, panY);
        DrawFrame();
        ViewportChanged?.Invoke();
    }

    /// <summary>
    /// 把当前视口状态写回滚动视图：内容尺寸/居中内边距/缩放范围/zoomScale/contentOffset 一次配置。
    /// 内边距按适应态显示尺寸固定（不随捏合变化），否则缩放跨过视图尺寸时内容会突然跳动。
    /// </summary>
    private void SyncScrollFromViewport()
    {
        if (_viewport.ImageWidth <= 0 || _viewport.ImageHeight <= 0) return;
        _syncingScroll = true;
        try
        {
            double fit = _viewport.FitScale;
            // 该内容尺寸即"适应态下的显示尺寸"：它是 zoomScale=1 的基准，也是滚动位移的坐标系。
            var display = new CGSize(_viewport.ImageWidth * fit, _viewport.ImageHeight * fit);
            _scroll.ContentSize = display;
            _scroll.ContentInset = new UIEdgeInsets(
                (nfloat)Math.Max(0, ((double)Bounds.Height - (double)display.Height) / 2),
                (nfloat)Math.Max(0, ((double)Bounds.Width - (double)display.Width) / 2), 0, 0);
            _scroll.MinimumZoomScale = 1;
            _scroll.MaximumZoomScale = (nfloat)Math.Max(1, MaximumPhysicalScale / _viewport.BackingScale / Math.Max(1e-6, fit));
            _scroll.ZoomScale = (nfloat)Math.Max(1, _viewport.Scale / Math.Max(1e-6, fit));
            _scroll.ContentOffset = ContentOffsetForViewport();
        }
        finally
        {
            _syncingScroll = false;
        }
    }

    /// <summary>按当前视口状态算出滚动视图应处的 contentOffset（与 <see cref="PullViewportFromScroll"/> 互为逆映射）。</summary>
    private CGPoint ContentOffsetForViewport()
    {
        double displayedWidth = _viewport.ImageWidth * _viewport.Scale;
        double displayedHeight = _viewport.ImageHeight * _viewport.Scale;
        return new CGPoint(
            displayedWidth / 2 - (double)Bounds.Width / 2 - _viewport.PanX,
            displayedHeight / 2 - (double)Bounds.Height / 2 - _viewport.PanY);
    }

    /// <summary>
    /// 用 UIKit 动画把滚动视图移到当前视口状态（双击切换用）。
    /// 放大走 <c>ZoomToRect</c>：缩放与偏移由同一条动画完成——先 SetZoomScale 再 SetContentOffset 会互相取消
    /// （偏移 setter 会中断在途的缩放动画，实测缩放停在原值只动偏移）。缩回适应只需一次缩放动画，居中由内边距给出。
    /// </summary>
    private void AnimateScrollToViewport()
    {
        if (_viewport.ImageWidth <= 0 || _viewport.ImageHeight <= 0) return;
        double zoom = Math.Max(1, _viewport.Scale / Math.Max(1e-6, _viewport.FitScale));
        if (zoom <= 1 + 1e-9)
        {
            _scroll.SetZoomScale(1, true);
            return;
        }
        _scroll.ZoomToRect(ContentRectForViewport(zoom), true);
    }

    /// <summary>
    /// 当前视口在"适应态内容坐标"里对应的可见矩形（ZoomToRect 的入参口径），
    /// 与 <see cref="ContentOffsetForViewport"/> 同一映射：内容点 cx 出现在视图 x = cx×zoom − offset。
    /// </summary>
    /// <param name="zoom">相对适应态的缩放倍率。</param>
    private CGRect ContentRectForViewport(double zoom)
    {
        double fit = _viewport.FitScale;
        double contentWidth = _viewport.ImageWidth * fit, contentHeight = _viewport.ImageHeight * fit;
        double width = (double)Bounds.Width / zoom, height = (double)Bounds.Height / zoom;
        double x = contentWidth / 2 - ((double)Bounds.Width / 2 + _viewport.PanX) / zoom;
        double y = contentHeight / 2 - ((double)Bounds.Height / 2 + _viewport.PanY) / zoom;
        return new CGRect(x, y, width, height);
    }

    /// <summary>滚动视图委托：缩放与位移都直接驱动视口，不做延迟合并。</summary>
    private sealed class ScrollDelegate : UIScrollViewDelegate
    {
        private readonly iOSPhotoView _owner;

        /// <summary>绑定宿主视图。</summary>
        /// <param name="owner">主图宿主。</param>
        public ScrollDelegate(iOSPhotoView owner) => _owner = owner;

        /// <summary>捏合缩放中每帧回调。</summary>
        /// <param name="scrollView">滚动视图。</param>
        public override void DidZoom(UIScrollView scrollView) => _owner.OnDidZoom(scrollView);

        /// <summary>位移（拖动、惯性、回弹）回调。</summary>
        /// <param name="scrollView">滚动视图。</param>
        public override void Scrolled(UIScrollView scrollView) => _owner.OnScrolled(scrollView);
    }
}
