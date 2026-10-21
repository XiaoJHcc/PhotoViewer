using CoreGraphics;
using Foundation;
using PhotoViewer.Apple;
using PhotoViewer.Core.Image;
using PhotoViewer.Core.Memory;
using UIKit;

namespace PhotoViewer.iOS.Native;

/// <summary>
/// iPad 主图宿主：共享 <see cref="MetalPhotoRenderer"/> 画在 CAMetalLayer 子层上，视口数学取共享 <see cref="PhotoViewport"/>。
///
/// <para>缩放/平移采用 UIKit 的标准照片缩放模式，**单一真源是 <see cref="UIScrollView"/> 的
/// zoomScale / contentOffset / contentInset**：透明缩放内容视图的尺寸只在 zoomScale=1 时写成"适应态显示尺寸"，
/// UIKit 在捏合时只改它的变换；视口变换在每个 DidZoom/Scrolled 回调里由滚动状态反算（<see cref="PullViewportFromScroll"/>）。
/// 程序化缩放（双击 / Fit 命令 / 键盘 / 细节预览定位）一律先由共享视口决定目标（<see cref="PhotoViewport.ToggleFit"/>、
/// ZoomToPhysical、FocusOnPoint），再表达成滚动视图操作（ZoomToRect / SetZoomScale）——两边同时写会互相取消，
/// 并且对带缩放变换的视图写 Frame 会让 contentSize 崩掉（旧实现"放大后被拽到左上角"的根因）。</para>
///
/// <para>代理 ↔ Full 热替换（同一张照片、ImageSize 恒定）只换纹理，完全不碰滚动状态；照片身份或逻辑尺寸变化（切图，
/// 含方向码变化）沿用旧壳 / Mac 的 <see cref="PhotoViewport.ApplyImage"/> 语义：适应态重新适应、跟随对焦时
/// 保持倍率并居中到新照片对焦点、其余情况保持倍率与同一归一化中心点（不重置到适应态）。</para>
/// </summary>
internal sealed class iOSPhotoView : UIView, IMemoryClient
{
    /// <summary>双击与键盘缩放允许的物理倍率上限（与共享视口锚点缩放的 16 倍上限同口径）。</summary>
    private const double MaximumPhysicalScale = 16;
    /// <summary>横向轻扫翻页的最小水平位移（点）：低于该值视为点按抖动。</summary>
    private const double SwipeThreshold = 24;
    /// <summary>横向分量需超过纵向分量的倍数，避免斜向拖动误翻页。</summary>
    private const double SwipeDominance = 1.5;
    /// <summary>zoomScale 与 1 的判定阈值：低于它视为适应态（与共享视口的适应判定同一量级）。</summary>
    private const double ZoomEpsilon = 1e-6;

    private readonly PhotoViewport _viewport = new();
    private readonly MetalPhotoRenderer _renderer;
    private readonly UIScrollView _scroll = new();
    /// <summary>缩放内容视图：尺寸 = 适应态显示尺寸（zoomScale=1 的基准），由 UIScrollView 缩放；没有它 UIKit 永远不会捏合缩放。</summary>
    private readonly UIView _zoomContent = new();
    private readonly UIPanGestureRecognizer _swipe;
    private readonly UIPanGestureRecognizer _wheel;
    private readonly UITapGestureRecognizer _doubleTap;
    /// <summary>当前照片的未旋转源尺寸与 EXIF 方向；default 表示没有照片。</summary>
    private (int Width, int Height, int Orientation) _identity;
    /// <summary>已上传的展示产物引用：同一对象再次上屏（会话状态通知）时只重绘，不重复上传整张纹理。</summary>
    private DecodedImage? _textureSource;
    /// <summary>正在程序化写入滚动视图：期间忽略回调，写完统一派生一次，避免边写边读的反馈环。</summary>
    private bool _syncingScroll;
    /// <summary>滚动基准（contentSize / 缩放范围 / 内边距）是否已按当前照片与视口尺寸建立；首图早于布局上屏时为 false。</summary>
    private bool _scrollGeometryValid;
    /// <summary>推迟滚动基准的取证日志是否已输出（每段连续未布局期只记一次）。</summary>
    private bool _deferredGeometryLogged;
    /// <summary>正在执行程序化缩放（ZoomToRect / SetZoomScale 动画）：期间的 DidZoom 不重估跟随对焦。</summary>
    private bool _programmaticZoom;
    private bool _swipeStartedFit;
    private double _wheelTranslation;

    /// <summary>视口变化通知，加载会话据此决定代理尺寸与 Full 升级。</summary>
    public event Action? ViewportChanged;

    /// <summary>适应态横向轻扫请求：-1 上一张、+1 下一张（由宿主走共享命令表）。</summary>
    internal event Action<int>? SwipeRequested;

    /// <summary>离散滚轮事件：向上/向下与原生修饰键掩码（宿主查共享命令表）。</summary>
    internal event Action<bool, ulong>? WheelScrolled;

    /// <summary>共享视口（验收读取缩放/平移状态）。</summary>
    internal PhotoViewport Viewport => _viewport;
    /// <summary>手势滚动容器（交互真源：zoomScale/contentOffset/contentInset）。</summary>
    internal UIScrollView ScrollView => _scroll;
    /// <summary>缩放内容视图（验收核对基准尺寸与 contentSize 口径）。</summary>
    internal UIView ZoomContent => _zoomContent;
    /// <summary>轻扫翻页识别器（验收核对单指与同时识别配置）。</summary>
    internal UIPanGestureRecognizer SwipeRecognizer => _swipe;
    /// <summary>触控板/鼠标滚轮识别器（验收核对只接收离散滚动）。</summary>
    internal UIPanGestureRecognizer WheelRecognizer => _wheel;
    /// <summary>双击识别器（验收核对与轻扫/上下文菜单共存）。</summary>
    internal UITapGestureRecognizer DoubleTapRecognizer => _doubleTap;
    /// <summary>缩放是否处于适应状态。</summary>
    internal bool IsFit => _viewport.IsFit;
    internal DecodedImage? DisplayedImage => _textureSource;
    internal bool PresentationReady => _textureSource != null && _identity != default && _scrollGeometryValid
        && _renderer.TextureWidth > 0 && Bounds.Width > 0 && Bounds.Height > 0;
    /// <summary>源像素对屏幕物理像素的倍率。</summary>
    internal double PhysicalScale => _viewport.PhysicalScale;
    /// <summary>当前全分辨率细节需求。</summary>
    internal bool NeedsFull => _viewport.NeedsFull;
    /// <summary>显示朝向的逻辑图像宽（像素）。</summary>
    internal double DisplayWidth => _viewport.ImageWidth;
    /// <summary>显示朝向的逻辑图像高（像素）。</summary>
    internal double DisplayHeight => _viewport.ImageHeight;
    /// <summary>图像像素到视图点的当前缩放（点/像素）。</summary>
    internal double PointScale => _viewport.Scale;
    /// <summary>导航图逻辑点尺寸（长边 150、按显示朝向比例缩小，不补方形衬边）。</summary>
    internal CGSize NavigationSize
    {
        get
        {
            var (width, height) = _viewport.NavigationSize;
            return new CGSize(width, height);
        }
    }
    /// <summary>导航图内的可见区域（NavigationSize 坐标系，逻辑点）。</summary>
    internal CGRect NavigationViewport
    {
        get
        {
            var (x, y, width, height) = _viewport.NavigationViewport;
            return new CGRect(x, y, width, height);
        }
    }
    /// <summary>内存客户端名称。</summary>
    public string Name => "iOSPhotoView";
    /// <summary>纹理与三重缓冲的估算 GPU 字节（绘制目标恒为 RGBA16F）。</summary>
    public long FootprintBytes => _renderer.TextureBytes
        + (long)(_viewport.ViewWidth * _viewport.ViewHeight * _viewport.BackingScale * _viewport.BackingScale * 8 * 3);

    /// <summary>当前照片的归一化对焦点（共享视口 API，供后续对焦定位复用；双击一律以点按位置为锚点）。</summary>
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
        // 透明的缩放内容视图：UIScrollView 通过它承载 zoomScale（缩放只改变该视图的变换），
        // 照片仍由 Metal 图层按视口变换绘制。基准尺寸在 ResetScrollForImage 里设为适应态显示尺寸。
        _zoomContent.BackgroundColor = UIColor.Clear;
        _zoomContent.UserInteractionEnabled = false;
        _scroll.AddSubview(_zoomContent);
        AddSubview(_scroll);

        var doubleTap = new UITapGestureRecognizer(OnDoubleTap) { NumberOfTapsRequired = 2 };
        _doubleTap = doubleTap;
        AddGestureRecognizer(doubleTap);

        // 适应态单指横向轻扫 = 上一张/下一张（放大态交给滚动视图平移）；最多一指，
        // 双指捏合一开始就不会落到本识别器，杜绝捏合起手误翻页。
        _swipe = new UIPanGestureRecognizer(OnSwipePan)
        {
            MinimumNumberOfTouches = 1,
            MaximumNumberOfTouches = 1,
            CancelsTouchesInView = false,
            DelaysTouchesBegan = false,
        };
        _swipe.Delegate = new SimultaneousRecognizerDelegate();
        AddGestureRecognizer(_swipe);

        // 离散滚轮（鼠标滚轮）按用户绑定执行命令，与 Mac 的“非精确滚轮走命令表”同口径；
        // 触控板（连续滚动）仍交给滚动视图平移，捏合不受影响。
        _wheel = new UIPanGestureRecognizer(OnWheelPan)
        {
            MinimumNumberOfTouches = 0,
            MaximumNumberOfTouches = 0,
            AllowedScrollTypesMask = UIScrollTypeMask.Discrete,
        };
        AddGestureRecognizer(_wheel);
        // 离散滚轮只走命令表（与 Mac 一致）：滚动视图的触摸平移对该识别器 require-to-fail，
        // 手指拖动时本识别器因 0 触点上限定直接失败，滚动视图照常平移；捏合不受影响。
        _scroll.PanGestureRecognizer.RequireGestureRecognizerToFail(_wheel);
        MemoryGovernor.Register(this);
    }

    /// <summary>同步上传像素并重算视口；null 表示清空照片。</summary>
    /// <param name="image">展示产物（F16 线性 scRGB 或 8 位 Display P3），所有权仍在调用方。</param>
    public void SetImage(DecodedImage? image)
    {
        if (image == null)
        {
            _identity = default;
            _textureSource = null;
            _renderer.SetTexture(null);
            _viewport.ClearTexture();
            DrawFrame();
            ViewportChanged?.Invoke();
            return;
        }
        var identity = (image.SourceWidth, image.SourceHeight, image.Orientation);
        if (ReferenceEquals(image, _textureSource) && identity == _identity)
        {
            // 会话只是状态通知（如"增强计算中"提示）：同一份产物已在纹理里，重绘即可。
            DrawFrame();
            return;
        }
        _textureSource = image;
        _renderer.SetTexture(image);
        if (identity == _identity)
        {
            // 同一张照片的代理 ↔ Full 热替换：几何基准恒定（ImageSize 不变），只换纹理；
            // 滚动状态与视口缩放/平移一律不动，避免"增强/升 Full 时照片被拽走"。
            _viewport.ApplyTexture(_renderer.TextureWidth);
            DrawFrame();
            ViewportChanged?.Invoke();
            return;
        }
        _identity = identity;
        // 新照片沿用旧壳 / Mac 的切图语义（共享视口 ApplyImage）：适应态重新适应；
        // 对焦跟随开启时保持缩放倍率并居中到新照片对焦点；否则保持缩放倍率与同一归一化中心点。
        // 几何基准始终取全分辨率逻辑尺寸（SourceWidth/Height），与代理纹理尺寸无关。
        _viewport.BeginImageChange();
        _viewport.ApplyImage(image.SourceWidth, image.SourceHeight, image.Orientation, _renderer.TextureWidth);
        SyncScrollFromViewport(animated: false);
        DrawFrame();
        ViewportChanged?.Invoke();
    }

    /// <summary>适应视口，保持照片比例并在物理像素超采样代理上绘制。</summary>
    public void FitToView()
    {
        if (_identity == default) return;
        _viewport.FitToView();
        SyncScrollFromViewport(animated: false);
    }

    /// <summary>
    /// 适应 ↔ 物理 100% 切换（控制栏 Fit 命令与热键；旧壳 <c>ToggleFit()</c> 语义）：
    /// 适应态且照片有对焦点时以对焦点为中心放大到 100% 并开启跟随对焦，无对焦点时从视口中心放大；
    /// 已放大时回适应。视图状态一律经共享视口决定，再由滚动视图执行（动画与双击同一路径）。
    /// </summary>
    internal void ToggleFit()
    {
        if (_identity == default) return;
        _viewport.ToggleFit(null);
        SyncScrollFromViewport(animated: true);
    }

    /// <summary>跳到预设物理倍率（验收与预设缩放命令共用；锚点为视口中心）。</summary>
    /// <param name="scale">目标物理倍率（1 = 源像素对屏幕物理像素 1:1）。</param>
    public void ZoomToPhysical(double scale)
    {
        if (_identity == default) return;
        _viewport.ZoomToPhysical(scale);
        SyncScrollFromViewport(animated: true);
    }

    /// <summary>按比例放大或缩小，锚定视口中心（共享命令表的比例缩放命令）。</summary>
    /// <param name="factor">倍率因子（&gt;1 放大）。</param>
    public void ZoomBy(double factor)
    {
        if (_identity == default) return;
        _viewport.ZoomBy(factor, null);
        SyncScrollFromViewport(animated: true);
    }

    /// <summary>按物理倍率缩放并把显示朝向的归一化点移到视口中心（分析栏细节预览双击用）。</summary>
    /// <param name="u">归一化横坐标。</param>
    /// <param name="v">归一化纵坐标。</param>
    /// <param name="physicalScale">目标物理倍率（1 = 源像素对屏幕物理像素 1:1）。</param>
    public void FocusOnPoint(double u, double v, double physicalScale)
    {
        if (_identity == default) return;
        _viewport.FocusOnPoint(u, v, physicalScale);
        SyncScrollFromViewport(animated: true);
    }

    /// <summary>把显示朝向的归一化图像点移到视口中心（导航图拖动/点按用），滚动视图仍执行实际位移。</summary>
    /// <param name="u">归一化横坐标。</param>
    /// <param name="v">归一化纵坐标。</param>
    internal void CenterOnNormalizedPoint(double u, double v)
    {
        if (_identity == default) return;
        // 用户导航（点按/拖动导航图）：后续 Scrolled 属于交互口径，按 5% UV 重估跟随对焦。
        _programmaticZoom = false;
        double zoom = Math.Max(1, (double)_scroll.ZoomScale);
        var display = FitContentSize();
        var insets = CenteringInsets(zoom, display);
        double width = (double)Bounds.Width, height = (double)Bounds.Height;
        var offset = new CGPoint(
            Math.Clamp(u * (double)display.Width * zoom - width / 2, -(double)insets.Left, Math.Max(-(double)insets.Left, (double)display.Width * zoom - width + (double)insets.Right)),
            Math.Clamp(v * (double)display.Height * zoom - height / 2, -(double)insets.Top, Math.Max(-(double)insets.Top, (double)display.Height * zoom - height + (double)insets.Bottom)));
        _scroll.SetContentOffset(offset, false);
    }

    /// <summary>
    /// 双击落点（识别器与验收共用）：走共享 <see cref="PhotoViewport.ToggleFit"/>——适应态以触点为中心放大到物理 100%，
    /// 放大态回适应（旧壳双击 <c>ToggleFit(center)</c> 同一语义）。
    /// </summary>
    /// <param name="point">主图视图坐标（点）。</param>
    internal void DoubleTapAt(CGPoint point)
    {
        if (_identity == default) return;
        _viewport.ToggleFit(((double)point.X, (double)point.Y));
        SyncScrollFromViewport(animated: true);
    }

    /// <summary>显示朝向的归一化点 → 主图视图坐标（分析栏绿框定位用）。</summary>
    /// <param name="u">归一化横坐标。</param>
    /// <param name="v">归一化纵坐标。</param>
    /// <returns>视图坐标。</returns>
    internal CGPoint ViewPoint(double u, double v)
    {
        var (x, y) = _viewport.ViewPoint(u, v);
        return new CGPoint(x, y);
    }

    /// <summary>宿主布局变化：刷新物理尺寸并按"同一图像点留在视口中心、同一相对缩放"重建滚动基准。</summary>
    public override void LayoutSubviews()
    {
        base.LayoutSubviews();
        _renderer.Layer.Frame = Layer.Bounds;
        _scroll.Frame = Bounds;
        double width = (double)Bounds.Width, height = (double)Bounds.Height;
        _renderer.ResizeDrawable(width, height, DisplayScale);
        if (_identity == default || width < 1 || height < 1)
        {
            // 尚未成形（被移出视图树、尺寸归零）时只同步尺寸，不重建滚动基准；下一次有效布局会重建。
            _viewport.ResizeView(width, height, DisplayScale);
            DrawFrame();
            return;
        }
        bool sizeChanged = Math.Abs(_viewport.ViewWidth - width) > .5 || Math.Abs(_viewport.ViewHeight - height) > .5
            || Math.Abs(_viewport.BackingScale - DisplayScale) > 1e-6;
        if (!sizeChanged)
        {
            // 尺寸未变但滚动基准尚未建立（首图在视图完成布局前上屏、或上屏时尺寸为零）：
            // 必须在这里补建，否则滚动视图停在 contentSize=0 / MaximumZoomScale=1，双击与捏合都不生效。
            if (!_scrollGeometryValid) SyncScrollFromViewport(animated: false);
            DrawFrame();
            return;
        }
        // 先按"旧几何"记录相对缩放与中心图像点：接下来改滚动视图 frame 会让 UIKit 立刻约束偏移，
        // 从滚动状态反算就会读到新旧尺寸混在一起的状态。
        double relativeZoom = _scrollGeometryValid
            ? Math.Max(1, (double)_scroll.ZoomScale)
            : (_viewport.IsFit ? 1 : _viewport.Scale / Math.Max(1e-9, _viewport.FitScale));
        var center = _scrollGeometryValid ? ViewportCenterImagePoint() : _viewport.CenterUV;
        _viewport.ResizeView(width, height, DisplayScale);
        RebuildScrollGeometry(relativeZoom, center);
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

    /// <summary>视口中心（视图坐标）。</summary>
    private (double X, double Y) ViewCenter => ((double)Bounds.Width / 2, (double)Bounds.Height / 2);

    /// <summary>zoomScale=1 时的内容尺寸，即适应态显示尺寸。</summary>
    private CGSize FitContentSize()
        => new(_viewport.ImageWidth * _viewport.FitScale, _viewport.ImageHeight * _viewport.FitScale);

    /// <summary>允许的最大 zoomScale：物理 16 倍封顶，且不得小于 1（否则 UIKit 拒绝缩放）。</summary>
    private double MaximumZoomScale()
        => Math.Max(1, MaximumPhysicalScale / Math.Max(1e-6, _viewport.BackingScale) / Math.Max(1e-6, _viewport.FitScale));

    /// <summary>
    /// 单指轻扫判定入口（识别器与验收共用）：只有适应态、水平位移达到阈值且明显大于纵向位移时才翻页；
    /// 放大态的滑动保持平移语义，不产生翻页。
    /// </summary>
    /// <param name="dx">水平位移（点，右正）。</param>
    /// <param name="dy">纵向位移（点，下正）。</param>
    /// <param name="velocityX">水平速度（点/秒，右正）。</param>
    /// <returns>是否发起了翻页请求。</returns>
    internal bool ApplySwipe(double dx, double dy, double velocityX)
    {
        if (!_viewport.IsFit) return false;
        if (Math.Abs(dx) < SwipeThreshold || Math.Abs(dx) < Math.Abs(dy) * SwipeDominance) return false;
        if (velocityX != 0 && Math.Sign(velocityX) != Math.Sign(dx)) return false;
        SwipeRequested?.Invoke(dx < 0 ? 1 : -1);
        return true;
    }

    /// <summary>轻扫识别器：起手记录是否处于适应态，抬手时按同一位移口径判定。</summary>
    /// <param name="recognizer">轻扫识别器。</param>
    private void OnSwipePan(UIPanGestureRecognizer recognizer)
    {
        if (recognizer.State == UIGestureRecognizerState.Began)
        {
            _swipeStartedFit = _viewport.IsFit;
            return;
        }
        if (recognizer.State != UIGestureRecognizerState.Ended || !_swipeStartedFit) return;
        var translation = recognizer.TranslationInView(this);
        var velocity = recognizer.VelocityInView(this);
        ApplySwipe((double)translation.X, (double)translation.Y, (double)velocity.X);
    }

    /// <summary>
    /// 离散滚轮：按增量方向交共享手势映射（含修饰键与苹果键盘映射），再查用户绑定；
    /// 与 Mac 一致，没绑定任何命令时滚轮留在照片上无动作。
    /// </summary>
    /// <param name="recognizer">滚轮识别器。</param>
    private void OnWheelPan(UIPanGestureRecognizer recognizer)
    {
        if (recognizer.State is UIGestureRecognizerState.Began or UIGestureRecognizerState.Changed)
        {
            var translation = recognizer.TranslationInView(this);
            double delta = (double)translation.Y - _wheelTranslation;
            _wheelTranslation = (double)translation.Y;
            if (Math.Abs(delta) < 1) return;
            WheelScrolled?.Invoke(delta > 0, (ulong)recognizer.ModifierFlags);
            return;
        }
        if (recognizer.State is UIGestureRecognizerState.Ended or UIGestureRecognizerState.Cancelled or UIGestureRecognizerState.Failed)
            _wheelTranslation = 0;
    }

    /// <summary>双击识别器入口。</summary>
    /// <param name="recognizer">双击识别器。</param>
    private void OnDoubleTap(UITapGestureRecognizer recognizer) => DoubleTapAt(recognizer.LocationInView(this));

    /// <summary>缩放结束：清除程序化标记，动画最终帧之后按非交互口径再同步一次（不重估跟随对焦）。</summary>
    private void OnZoomingEnded()
    {
        _programmaticZoom = false;
        if (_syncingScroll) return;
        UpdateCenteringInsets();
        PullViewportFromScroll(interactive: false);
    }

    /// <summary>滚动视图缩放回调：先按新倍率重算居中内边距（它会影响 contentOffset），再从滚动状态派生视口。</summary>
    /// <param name="scrollView">发送事件的滚动视图。</param>
    private void OnDidZoom(UIScrollView scrollView)
    {
        if (_syncingScroll) return;
        UpdateCenteringInsets();
        PullViewportFromScroll(interactive: !_programmaticZoom);
    }

    /// <summary>滚动视图位移（拖动、惯性、回弹）后同步视口并重绘。</summary>
    /// <param name="scrollView">发送事件的滚动视图。</param>
    private void OnScrolled(UIScrollView scrollView)
    {
        if (_syncingScroll) return;
        // 用户拖动/惯性滚动会接管缩放动画（例如动画途中被拖住），此后按交互口径重估跟随对焦。
        if (scrollView.Dragging || scrollView.Decelerating) _programmaticZoom = false;
        PullViewportFromScroll(interactive: !_programmaticZoom);
    }

    /// <summary>
    /// 从滚动状态（zoomScale / contentOffset）派生视口缩放与平移并重绘。
    /// 内容尺寸 = 适应态显示尺寸，故缩放 = 适应缩放 × zoomScale；照片中心相对视口中心的偏移由可见内容原点反算。
    /// 捏合过程中视口会被共享数学钳制（照片不跟着回弹位移），回弹结束后偏移回到合法范围，两边自然一致。
    /// </summary>
    /// <param name="interactive">是否来自用户手势；程序化缩放/切图重建只同步状态，不重估跟随对焦。</param>
    private void PullViewportFromScroll(bool interactive = true)
    {
        if (_syncingScroll || _identity == default) return;
        if ((double)Bounds.Width < 1 || (double)Bounds.Height < 1) return;
        double zoom = Math.Max(ZoomEpsilon, (double)_scroll.ZoomScale);
        double scale = _viewport.FitScale * zoom;
        double displayedWidth = _viewport.ImageWidth * scale;
        double displayedHeight = _viewport.ImageHeight * scale;
        double panX = displayedWidth / 2 - (double)_scroll.ContentOffset.X - (double)Bounds.Width / 2;
        double panY = displayedHeight / 2 - (double)_scroll.ContentOffset.Y - (double)Bounds.Height / 2;
        double previousScale = _viewport.Scale, previousX = _viewport.PanX, previousY = _viewport.PanY;
        _viewport.ApplyGestureView(scale, panX, panY, interactive);
        DrawFrame();
        if (Math.Abs(previousScale - _viewport.Scale) > 1e-9
            || Math.Abs(previousX - _viewport.PanX) > 1e-6
            || Math.Abs(previousY - _viewport.PanY) > 1e-6)
            ViewportChanged?.Invoke();
    }

    /// <summary>
    /// 用共享视口当前状态重建/驱动滚动视图（视图仍是唯一交互真源，这里只是把它同步到视口意图）：
    /// 适应态 → zoomScale 1；放大态 → 相对适应缩放 = 视口缩放 / 适应缩放，锚点为视口中心的归一化图像点。
    /// 动画路径用一条 ZoomToRect 同时完成缩放与偏移；非动画路径直接重建滚动基准（切图/布局，不允许跳变）。
    /// 尺寸尚未成形时标记基准无效，由下一次 <see cref="LayoutSubviews"/> 补建，绝不留下未初始化的滚动状态。
    /// 由视口意图驱动的变化必须显式通知：滚动视图随后派生出的视口可能与目标一致（Pull 判定"无变化"），
    /// 不通知就会漏掉导航指示器刷新与 Full 升级（例如 Fit 之后 NeedsFull 由 true 变 false）。
    /// </summary>
    /// <param name="animated">是否用滚动视图动画过渡（双击/命令），false 表示立即到位（切图/布局）。</param>
    private void SyncScrollFromViewport(bool animated)
    {
        if (_identity == default)
        {
            _scrollGeometryValid = false;
            return;
        }
        if ((double)Bounds.Width < 1 || (double)Bounds.Height < 1)
        {
            // 首图早于视图布局上屏：此刻写不出 contentSize/缩放范围，推迟到下一次 LayoutSubviews 补建
            // （旧实现直接返回，滚动视图停在默认 MaximumZoomScale=1，双击/捏合只能"几乎不动"）。
            if (!_deferredGeometryLogged)
            {
                _deferredGeometryLogged = true;
                Console.WriteLine($"[Native] 照片上屏时视口尚未布局（{Bounds.Width:0}×{Bounds.Height:0}），滚动基准推迟到下一次布局");
            }
            _scrollGeometryValid = false;
            return;
        }
        _deferredGeometryLogged = false;
        // 滚动视图的最小缩放是适应态（zoomScale 1）：物理 100% 不高于适应倍率的素材（小图、RAW 内嵌预览）
        // 没有向上的缩放空间，双击/Fit 一律停回适应态，绝不把照片缩到比适应更小（Mac 允许缩到 100% 以下，
        // 那是 Mac 视口直接绘制的行为；iOS 以滚动视图为准，这里显式夹取，避免视口与滚动状态分叉）。
        if (!_viewport.IsFit && _viewport.Scale < _viewport.FitScale - 1e-9) _viewport.FitToView();
        double relativeZoom = _viewport.IsFit ? 1 : _viewport.Scale / Math.Max(1e-9, _viewport.FitScale);
        if (!animated) RebuildScrollGeometry(relativeZoom, _viewport.CenterUV);
        else
        {
            _programmaticZoom = true;
            if (relativeZoom > 1 + ZoomEpsilon) ZoomAnchored(relativeZoom, _viewport.CenterUV, ViewCenter);
            else _scroll.SetZoomScale(1, true);
        }
        DrawFrame();
        ViewportChanged?.Invoke();
    }

    /// <summary>布局尺寸/屏幕倍率变化：以"同一图像点留在视口中心 + 同一相对缩放"重建滚动基准。</summary>
    /// <param name="relativeZoom">变化前的 zoomScale（相对适应态）。</param>
    /// <param name="center">变化前视口中心对应的归一化图像点。</param>
    private void RebuildScrollGeometry(double relativeZoom, (double U, double V) center)
    {
        _syncingScroll = true;
        try
        {
            _scroll.SetZoomScale(1, false);
            var display = FitContentSize();
            _zoomContent.Frame = new CGRect(CGPoint.Empty, display);
            _scroll.ContentSize = display;
            _scroll.MinimumZoomScale = 1;
            _scroll.MaximumZoomScale = (nfloat)MaximumZoomScale();
            double zoom = Math.Clamp(relativeZoom, 1, (double)_scroll.MaximumZoomScale);
            _scroll.SetZoomScale((nfloat)zoom, false);
            var insets = CenteringInsets(zoom, display);
            _scroll.ContentInset = insets;
            double width = (double)Bounds.Width, height = (double)Bounds.Height;
            _scroll.ContentOffset = new CGPoint(
                Math.Clamp(center.U * (double)display.Width * zoom - width / 2, -(double)insets.Left, Math.Max(-(double)insets.Left, (double)display.Width * zoom - width + (double)insets.Right)),
                Math.Clamp(center.V * (double)display.Height * zoom - height / 2, -(double)insets.Top, Math.Max(-(double)insets.Top, (double)display.Height * zoom - height + (double)insets.Bottom)));
        }
        finally
        {
            _syncingScroll = false;
        }
        _scrollGeometryValid = true;
        PullViewportFromScroll(interactive: false);
    }

    /// <summary>
    /// 居中内边距：内容在某一轴上小于视口时，用等量内边距把它居中（UIScrollView 的标准居中模式）。
    /// 缩放跨过视图尺寸时内边距连续趋近 0，不产生内容跳动；捏合回调与每次滚动状态重建都更新。
    /// </summary>
    private void UpdateCenteringInsets()
    {
        var insets = CenteringInsets(Math.Max(1, (double)_scroll.ZoomScale), FitContentSize());
        if (_scroll.ContentInset != insets) _scroll.ContentInset = insets;
    }

    /// <summary>按给定缩放倍率与适应态内容尺寸计算居中内边距。</summary>
    /// <param name="zoom">相对适应态的缩放倍率。</param>
    /// <param name="display">适应态（zoomScale=1）内容尺寸。</param>
    /// <returns>四边等量的居中内边距。</returns>
    private UIEdgeInsets CenteringInsets(double zoom, CGSize display)
    {
        double horizontal = Math.Max(0, ((double)Bounds.Width - (double)display.Width * zoom) / 2);
        double vertical = Math.Max(0, ((double)Bounds.Height - (double)display.Height * zoom) / 2);
        return new UIEdgeInsets((nfloat)vertical, (nfloat)horizontal, (nfloat)vertical, (nfloat)horizontal);
    }

    /// <summary>从视口数学直接求视口中心的归一化图像点：布局重建时滚动视图的 frame 已变，不能用它反算。</summary>
    private (double U, double V) ViewportCenterImagePoint()
    {
        double width = _viewport.ImageWidth * _viewport.Scale, height = _viewport.ImageHeight * _viewport.Scale;
        if (width <= 0 || height <= 0) return (.5, .5);
        return (.5 - _viewport.PanX / width, .5 - _viewport.PanY / height);
    }

    /// <summary>视图坐标点对应的归一化图像点（显示朝向 0–1，越界钳制）；与 <see cref="ViewPoint"/> 互逆，验收也用它核对锚点。</summary>
    /// <param name="point">主图视图坐标（点）。</param>
    internal (double U, double V) ImagePointAt(CGPoint point)
    {
        var display = FitContentSize();
        double zoom = Math.Max(ZoomEpsilon, (double)_scroll.ZoomScale);
        if ((double)display.Width <= 0 || (double)display.Height <= 0) return (.5, .5);
        double contentX = ((double)point.X + (double)_scroll.ContentOffset.X) / zoom;
        double contentY = ((double)point.Y + (double)_scroll.ContentOffset.Y) / zoom;
        return (Math.Clamp(contentX / (double)display.Width, 0, 1), Math.Clamp(contentY / (double)display.Height, 0, 1));
    }

    /// <summary>
    /// 以"某个图像点保持在其视图落点"为锚点缩放：用一条 ZoomToRect 动画同时完成缩放与偏移
    /// （先 SetZoomScale 再 SetContentOffset 会互相取消，实测缩放停在原值）。
    /// </summary>
    /// <param name="relativeZoom">目标 zoomScale（相对适应态）。</param>
    /// <param name="anchor">锚点对应的归一化图像点。</param>
    /// <param name="viewPoint">锚点的视图坐标（缩放前后保持不动）。</param>
    private void ZoomAnchored(double relativeZoom, (double U, double V) anchor, (double X, double Y) viewPoint)
    {
        if (_identity == default) return;
        double target = Math.Clamp(relativeZoom, 1, MaximumZoomScale());
        if (target <= 1 + ZoomEpsilon)
        {
            _scroll.SetZoomScale(1, true);
            return;
        }
        var display = FitContentSize();
        double width = (double)Bounds.Width / target, height = (double)Bounds.Height / target;
        double centerX = anchor.U * (double)display.Width + ((double)Bounds.Width / 2 - viewPoint.X) / target;
        double centerY = anchor.V * (double)display.Height + ((double)Bounds.Height / 2 - viewPoint.Y) / target;
        _scroll.ZoomToRect(new CGRect(centerX - width / 2, centerY - height / 2, width, height), true);
    }

    /// <summary>滚动视图委托：缩放与位移都直接驱动视口，不做延迟合并。</summary>
    private sealed class ScrollDelegate : UIScrollViewDelegate
    {
        private readonly iOSPhotoView _owner;

        /// <summary>绑定宿主视图。</summary>
        /// <param name="owner">主图宿主。</param>
        public ScrollDelegate(iOSPhotoView owner) => _owner = owner;

        /// <summary>声明透明内容视图为缩放目标；没有它 UIKit 不会响应捏合，zoomScale 永不变化。</summary>
        /// <param name="scrollView">滚动视图。</param>
        /// <returns>缩放内容视图。</returns>
        public override UIView ViewForZoomingInScrollView(UIScrollView scrollView) => _owner._zoomContent;

        /// <summary>捏合缩放中每帧回调。</summary>
        /// <param name="scrollView">滚动视图。</param>
        public override void DidZoom(UIScrollView scrollView) => _owner.OnDidZoom(scrollView);

        /// <summary>位移（拖动、惯性、回弹）回调。</summary>
        /// <param name="scrollView">滚动视图。</param>
        public override void Scrolled(UIScrollView scrollView) => _owner.OnScrolled(scrollView);

        /// <summary>缩放结束（用户捏合或程序化动画）：解除程序化标记并按当前状态收尾。</summary>
        /// <param name="scrollView">滚动视图。</param>
        /// <param name="view">缩放内容视图。</param>
        /// <param name="scale">结束时的 zoomScale。</param>
        public override void ZoomingEnded(UIScrollView scrollView, UIView view, nfloat scale)
            => _owner.OnZoomingEnded();
    }

    /// <summary>轻扫与滚动视图平移并存：适应态轻扫翻页时背景仍可做回弹，两者都拿到完整手势。</summary>
    private sealed class SimultaneousRecognizerDelegate : UIGestureRecognizerDelegate
    {
        /// <summary>允许与滚动视图的平移识别器同时识别。</summary>
        /// <param name="gestureRecognizer">本识别器。</param>
        /// <param name="otherGestureRecognizer">其他识别器。</param>
        /// <returns>始终允许。</returns>
        public override bool ShouldRecognizeSimultaneously(UIGestureRecognizer gestureRecognizer, UIGestureRecognizer otherGestureRecognizer) => true;
    }
}
