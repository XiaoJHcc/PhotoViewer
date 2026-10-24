using System;
using System.Threading;
using Android.Animation;
using Android.App;
using Android.Content;
using Android.Graphics;
using Android.OS;
using Android.Views;
using Android.Widget;
using PhotoViewer.Android.Core;
using PhotoViewer.Core;
using PhotoViewer.Core.Color;
using PhotoViewer.Core.Image;
using PhotoViewer.Core.Input;
using PhotoViewer.Core.Memory;
using PhotoViewer.Shared;

namespace PhotoViewer.Android.Native;

/// <summary>
/// Android 主图宿主（A2）：<see cref="SurfaceView"/> + 自有 EGL 渲染核心，触摸手势直接驱动共享
/// <see cref="PhotoViewport"/>（视图坐标 = dp，物理倍率 = density）。
/// 缩放/平移不用系统滚动容器：单指拖动只在放大态平移，双指捏合锚定焦点，双击/命令走
/// <see cref="PhotoViewport.ToggleFit"/>；代理↔Full 热替换只换纹理，不碰视口状态。
/// </summary>
internal sealed class AndroidPhotoView : FrameLayout, ISurfaceHolderCallback, IMemoryClient
{
    /// <summary>双击与命令缩放允许的物理倍率上限（与共享视口一致）。</summary>
    private const double MaximumPhysicalScale = 16;
    /// <summary>适应态横向轻扫的最小水平位移（dp）。</summary>
    private const double SwipeThresholdDp = 24;
    /// <summary>横向分量需超过纵向分量的倍数。</summary>
    private const double SwipeDominance = 1.5;
    /// <summary>视口动画时长（秒）。</summary>
    private const double AnimationSeconds = .25;

    private readonly PhotoViewport _viewport = new();
    private readonly SurfaceView _surfaceView;
    private readonly GestureDetector _gestures;
    private readonly ScaleGestureDetector _scaleDetector;
    private readonly double _density;
    private readonly int _touchSlop;
    private readonly float _scrollFactorPx;
    private AndroidPhotoRenderer _renderer = new();
    private DecodedImage? _image;
    private (int Width, int Height, int Orientation) _identity;
    /// <summary>当前展示照片的代际（来自 <see cref="PhotoLoadSession.Generation"/>）：代际变化即切图，不是热替换。</summary>
    private int _generation;
    private ValueAnimator? _animation;
    private bool _surfaceReady;
    private bool _disposed;
    private bool _dragging;
    private bool _pinching;
    private bool _multiTouch;
    private bool _swipeStartedFit;
    private bool _moved;
    private float _downX, _downY, _lastX, _lastY, _lastFocusX, _lastFocusY;
    private bool _hasLastFocus;
    /// <summary>离散滚轮尚未累积到一格的增量（INPUT-04）。</summary>
    private double _wheelAccumulator;
    /// <summary>纹理占用快照（含 mip 链），GPU 私有内存不在 RssAnon 里，供内存预算源与管家读取。</summary>
    private long _textureBytes;
    /// <summary>挂到 <see cref="AndroidMemoryBudgetProvider.ExternalFootprint"/> 的读数委托（释放时只摘自己这份）。</summary>
    private readonly Func<long> _textureFootprint;

    /// <summary>EGL 窗口表面就绪（首图必须等表面就绪后再解码，保证 P3 协商结果参与编码选择）。</summary>
    internal event Action? SurfaceReady;
    /// <summary>视口变化通知（会话据此决定代理尺寸与 Full 升级，覆盖层据此刷新）。</summary>
    internal event Action? ViewportChanged;
    /// <summary>适应态横向轻扫：-1 上一张、+1 下一张。</summary>
    internal event Action<int>? SwipeRequested;
    /// <summary>
    /// 滚轮命令派发（宿主按共享绑定表查询并执行）：参数为向上/向下与原生修饰键掩码，返回是否已执行命令。
    /// 带修饰键的滚轮先走这里，未命中绑定才回落到默认语义（精确平移 / 一格一命令）。
    /// </summary>
    internal Func<bool, int, bool>? WheelCommand { get; set; }
    /// <summary>主图长按：弹上下文菜单；参数为触点在本视图内的像素坐标。</summary>
    internal event Action<float, float>? LongPressRequested;
    /// <summary>主图单击（空态打开文件夹 / 点空白清除细节预览）。</summary>
    internal event Action? TapRequested;

    /// <summary>共享视口（验收读取缩放/平移状态）。</summary>
    internal PhotoViewport Viewport => _viewport;
    /// <summary>是否处于适应状态。</summary>
    internal bool IsFit => _viewport.IsFit;
    /// <summary>源像素对屏幕物理像素的倍率。</summary>
    internal double PhysicalScale => _viewport.PhysicalScale;
    /// <summary>当前全分辨率细节需求。</summary>
    internal bool NeedsFull => _viewport.NeedsFull;
    /// <summary>当前是否已有纹理。</summary>
    internal bool HasImage => _image != null && _renderer.HasTexture;
    /// <summary>屏幕密度（验收换算 dp/px）。</summary>
    internal double Density => _density;
    /// <summary>导航图逻辑点尺寸。</summary>
    internal (double Width, double Height) NavigationSize => _viewport.NavigationSize;
    /// <summary>导航图可见区域（逻辑点）。</summary>
    internal (double X, double Y, double Width, double Height) NavigationViewport => _viewport.NavigationViewport;
    /// <summary>当前表面是否 P3。</summary>
    internal bool SurfaceIsP3 => _renderer.SurfaceIsP3;

    /// <summary>内存客户端名称。</summary>
    public string Name => "AndroidPhotoView";
    /// <summary>纹理（当前 + 最近缓存 + 空纹理，含 mip 链）与表面估算字节（8 位 RGBA）。</summary>
    public long FootprintBytes
    {
        get
        {
            long texture = Interlocked.Read(ref _textureBytes);
            double backing = _viewport.BackingScale;
            return texture + (long)(_viewport.ViewWidth * _viewport.ViewHeight * backing * backing * 4);
        }
    }

    /// <summary>建立 SurfaceView、渲染器与手势识别。</summary>
    /// <param name="context">宿主上下文（Activity）。</param>
    public AndroidPhotoView(Context context) : base(context)
    {
        _density = Resources?.DisplayMetrics?.Density ?? 1;
        _touchSlop = ViewConfiguration.Get(context)?.ScaledTouchSlop ?? 8;
        // API 26 起提供滚动因子；更低版本回退到系统默认的 64px/格。
        _scrollFactorPx = OperatingSystem.IsAndroidVersionAtLeast(26)
            ? ViewConfiguration.Get(context)?.ScaledVerticalScrollFactor ?? 64
            : 64;
        SetBackgroundColor(Color.Black);
        _surfaceView = new SurfaceView(context);
        _surfaceView.SetZOrderOnTop(false);
        _surfaceView.Holder?.AddCallback(this);
        AddView(_surfaceView, new LayoutParams(LayoutParams.MatchParent, LayoutParams.MatchParent));
        _gestures = new GestureDetector(context, new PhotoGestureListener(this));
        _scaleDetector = new ScaleGestureDetector(context, new PhotoScaleListener(this));
        // GPU 纹理不占 RssAnon，预算源要单独加上这份占用才与 MemoryLimiter 的可用量同口径。
        _textureFootprint = () => Interlocked.Read(ref _textureBytes);
        AndroidMemoryBudgetProvider.ExternalFootprint = _textureFootprint;
        MemoryGovernor.Register(this);
    }

    /// <summary>同步上传当前展示图；null 表示清空。</summary>
    /// <param name="image">展示产物（会话持有所有权，本视图只在本调用内读取像素）。</param>
    /// <param name="generation">会话照片代际（<see cref="PhotoLoadSession.Generation"/>）：代际变化即切图，
    /// 同机型连拍的两张源尺寸与方向相同，只比几何无法区分切图与代理↔Full 热替换（STATE-01）。</param>
    public void SetImage(DecodedImage? image, int generation)
    {
        if (_disposed) return;
        bool cut = generation != _generation;
        _generation = generation;
        if (image == null)
        {
            _image = null;
            _identity = default;
            _wheelAccumulator = 0;
            _renderer.SetImage(null);
            RefreshTextureBytes();
            _viewport.ClearTexture();
            Draw();
            ViewportChanged?.Invoke();
            return;
        }
        // 同一实例 + 同一代际：会话的加载态/增强态等通知，纹理与几何都没变，只重绘当前帧、不重传。
        if (!cut && ReferenceEquals(_image, image))
        {
            Draw();
            ViewportChanged?.Invoke();
            return;
        }
        var identity = (image.SourceWidth, image.SourceHeight, image.Orientation);
        // STATE-01：重新选中同一文件命中同一缓存实例时代际变了，纹理不必重传，几何仍按切图重算。
        bool reuseTexture = cut && ReferenceEquals(_image, image);
        _image = image;
        if (_surfaceReady)
        {
            if (!reuseTexture)
            {
                _renderer.SetImage(image);
                RefreshTextureBytes();
            }
            if (!cut && identity == _identity)
            {
                // 同一张照片的代理 ↔ Full 热替换：只换纹理。
                _viewport.ApplyTexture(_renderer.TextureWidth);
                Draw();
                ViewportChanged?.Invoke();
                return;
            }
        }
        _identity = identity;
        _wheelAccumulator = 0;
        _viewport.BeginImageChange();
        _viewport.ApplyImage(image.SourceWidth, image.SourceHeight, image.Orientation, _renderer.TextureWidth);
        Draw();
        ViewportChanged?.Invoke();
    }

    /// <summary>适应视口。</summary>
    public void FitToView()
    {
        if (_image == null) return;
        _viewport.FitToView();
        Draw();
        ViewportChanged?.Invoke();
    }

    /// <summary>适应 ↔ 物理 100% 切换（控制栏 Fit 命令与快捷键；对焦点优先）。</summary>
    public void ToggleFit()
    {
        if (_image == null) return;
        // 素材本身小于视口物理像素（小图/内嵌预览）时物理 100% 低于适应态：按 iOS 口径夹回适应，不缩小照片。
        if (_viewport.IsFit && _viewport.FitScale > 1 / _viewport.BackingScale + 1e-9)
        {
            FitToView();
            return;
        }
        AnimateViewport(() => _viewport.ToggleFit(null));
    }

    /// <summary>双击落点（手势与验收共用）：适应态以触点为中心到物理 100%，放大态回适应。</summary>
    /// <param name="viewPoint">主图视图坐标（dp）。</param>
    internal void DoubleTapAt((double X, double Y) viewPoint)
    {
        if (_image == null) return;
        if (_viewport.IsFit && _viewport.FitScale > 1 / _viewport.BackingScale + 1e-9)
        {
            FitToView();
            return;
        }
        AnimateViewport(() => _viewport.ToggleFit(viewPoint));
    }

    /// <summary>跳到预设物理倍率（视口中心）。</summary>
    /// <param name="scale">物理倍率。</param>
    public void ZoomToPhysical(double scale)
    {
        if (_image == null) return;
        AnimateViewport(() => _viewport.ZoomToPhysical(scale));
    }

    /// <summary>按比例缩放（视口中心）。</summary>
    /// <param name="factor">倍率因子。</param>
    public void ZoomBy(double factor)
    {
        if (_image == null) return;
        AnimateViewport(() => _viewport.ZoomBy(factor, null));
    }

    /// <summary>按物理倍率缩放并把显示朝向归一化点移到视口中心（细节预览双击用）。</summary>
    /// <param name="u">归一化横坐标。</param>
    /// <param name="v">归一化纵坐标。</param>
    /// <param name="physicalScale">目标物理倍率。</param>
    public void FocusOnPoint(double u, double v, double physicalScale)
    {
        if (_image == null) return;
        // 小图/内嵌预览的物理 100% 可能低于适应态：按既有小图口径留在适应态，不缩小、不跳位。
        if (_viewport.FitScale > 1 / _viewport.BackingScale + 1e-9)
        {
            FitToView();
            return;
        }
        AnimateViewport(() => _viewport.FocusOnPoint(u, v, physicalScale));
    }

    /// <summary>把显示朝向归一化点移到视口中心（导航图定位）。</summary>
    /// <param name="u">归一化横坐标。</param>
    /// <param name="v">归一化纵坐标。</param>
    internal void CenterOnNormalizedPoint(double u, double v)
    {
        if (_image == null || _viewport.IsFit) return;
        double width = _viewport.ImageWidth * _viewport.Scale;
        double height = _viewport.ImageHeight * _viewport.Scale;
        if (width <= 0 || height <= 0) return;
        _viewport.ApplyGestureView(_viewport.Scale, (.5 - u) * width, (.5 - v) * height, interactive: true);
        Draw();
        ViewportChanged?.Invoke();
    }

    /// <summary>显示朝向归一化点 → 主图视图坐标（dp）。</summary>
    /// <param name="u">归一化横坐标。</param>
    /// <param name="v">归一化纵坐标。</param>
    /// <returns>视图坐标。</returns>
    internal (double X, double Y) ViewPoint(double u, double v) => _viewport.ViewPoint(u, v);

    /// <summary>当前照片的归一化对焦点。</summary>
    /// <param name="x">横坐标。</param>
    /// <param name="y">纵坐标。</param>
    internal void SetFocus(double? x, double? y) => _viewport.SetFocus(x, y);

    /// <summary>离屏渲染导航图（长边 150 点）；无纹理时 null。</summary>
    /// <returns>导航图位图。</returns>
    internal Bitmap? CaptureNavigationBitmap() => _renderer.CaptureNavigationBitmap(_viewport, _density);

    /// <summary>单指轻扫判定入口（手势与验收共用）。</summary>
    /// <param name="dx">水平位移（dp，右正）。</param>
    /// <param name="dy">纵向位移（dp，下正）。</param>
    /// <returns>是否发起翻页。</returns>
    internal bool ApplySwipe(double dx, double dy)
    {
        if (!_viewport.IsFit) return false;
        if (Math.Abs(dx) < SwipeThresholdDp || Math.Abs(dx) < Math.Abs(dy) * SwipeDominance) return false;
        SwipeRequested?.Invoke(dx < 0 ? 1 : -1);
        return true;
    }

    /// <summary>重绘当前帧并把视口上报给加载会话。</summary>
    internal void Draw()
    {
        if (_surfaceReady) _renderer.DrawFrame(_viewport, _density);
    }

    /// <summary>上报视口像素尺寸与 Full 需求。</summary>
    internal void ReportViewport()
    {
        if (Width < 1 || Height < 1) return;
        ViewportChanged?.Invoke();
    }

    /// <summary>Surface 创建：建立 EGL，重传当前图并通知宿主可以开始加载。</summary>
    /// <param name="holder">表面持有者。</param>
    public void SurfaceCreated(ISurfaceHolder holder)
    {
        try
        {
            _renderer = new AndroidPhotoRenderer();
            _renderer.Create(holder, _density);
            _surfaceReady = true;
            if (_image != null)
            {
                _renderer.SetImage(_image);
                _viewport.ApplyTexture(_renderer.TextureWidth);
            }
            RefreshTextureBytes();
            Draw();
            SurfaceReady?.Invoke();
        }
        catch (Exception exception)
        {
            _surfaceReady = false;
            Console.Error.WriteLine($"[Native] 主图表面创建失败：{exception.Message}");
        }
    }

    /// <summary>Surface 尺寸变化。</summary>
    /// <param name="holder">表面持有者。</param>
    /// <param name="format">像素格式。</param>
    /// <param name="width">宽（像素）。</param>
    /// <param name="height">高（像素）。</param>
    public void SurfaceChanged(ISurfaceHolder holder, Format format, int width, int height)
    {
        _renderer.Resize(width, height);
        Draw();
        // 栏位显隐后 SurfaceView 的新缓冲可能在本帧尚未就绪：下一帧再绘制一次，避免停在旧尺寸的帧上。
        Post(Draw);
        PostDelayed(Draw, 32);
    }

    /// <summary>Surface 销毁：释放 EGL，等待下一次创建后重传纹理。</summary>
    /// <param name="holder">表面持有者。</param>
    public void SurfaceDestroyed(ISurfaceHolder holder)
    {
        _surfaceReady = false;
        _renderer.Dispose();
        _renderer = new AndroidPhotoRenderer();
        RefreshTextureBytes();
    }

    /// <summary>布局尺寸变化：刷新 dp 视口并保持适应态贴合。</summary>
    /// <param name="w">新宽（像素）。</param>
    /// <param name="h">新高（像素）。</param>
    /// <param name="oldw">旧宽。</param>
    /// <param name="oldh">旧高。</param>
    protected override void OnSizeChanged(int w, int h, int oldw, int oldh)
    {
        base.OnSizeChanged(w, h, oldw, oldh);
        if (w < 1 || h < 1) return;
        double widthDp = w / _density, heightDp = h / _density;
        bool sizeChanged = Math.Abs(_viewport.ViewWidth - widthDp) > .01 || Math.Abs(_viewport.ViewHeight - heightDp) > .01;
        if (!sizeChanged) return;
        var center = _viewport.CenterUV;
        _viewport.ResizeView(widthDp, heightDp, _density);
        if (_viewport.IsFit)
        {
            _viewport.FitToView();
        }
        else
        {
            // 栏位显隐/旋转会改主图分区：保持原来落在视口中心的图像点仍在中心，再按新边界钳制。
            double width = _viewport.ImageWidth * _viewport.Scale;
            double height = _viewport.ImageHeight * _viewport.Scale;
            _viewport.ApplyGestureView(_viewport.Scale, (.5 - center.U) * width, (.5 - center.V) * height, interactive: false);
        }
        Draw();
        // 布局变化后 SurfaceView 的缓冲可能在下一帧才跟上：再补一帧，避免缩略图/控制栏/分析栏开合时停在旧尺寸。
        Post(Draw);
        ViewportChanged?.Invoke();
    }

    /// <summary>触摸：捏合 → 缩放；放大态拖动 → 平移；适应态横滑 → 翻页。</summary>
    /// <param name="e">触摸事件。</param>
    /// <returns>始终消费（主图区域没有可滚动子视图）。</returns>
    public override bool OnTouchEvent(MotionEvent? e)
    {
        if (e == null) return false;
        _scaleDetector.OnTouchEvent(e);
        _gestures.OnTouchEvent(e);
        switch (e.ActionMasked)
        {
            case MotionEventActions.Down:
                CancelAnimation();
                _downX = _lastX = e.GetX();
                _downY = _lastY = e.GetY();
                _swipeStartedFit = _viewport.IsFit;
                _multiTouch = false;
                _moved = false;
                _dragging = !_viewport.IsFit;
                _hasLastFocus = false;
                break;

            case MotionEventActions.PointerDown:
                _multiTouch = true;
                _moved = true;
                _dragging = false;
                _hasLastFocus = false;
                break;

            case MotionEventActions.Move:
                if (e.PointerCount > 1 || _pinching) break;
                float x = e.GetX(), y = e.GetY();
                double dx = (x - _lastX) / _density, dy = (y - _lastY) / _density;
                if (Math.Abs(x - _downX) > _touchSlop || Math.Abs(y - _downY) > _touchSlop) _moved = true;
                if (!_viewport.IsFit && _moved)
                {
                    _viewport.PanBy(dx, dy);
                    _lastX = x;
                    _lastY = y;
                    Draw();
                    ViewportChanged?.Invoke();
                }
                break;

            case MotionEventActions.PointerUp:
                _hasLastFocus = false;
                if (e.PointerCount <= 1) _dragging = false;
                break;

            case MotionEventActions.Up:
                float upX = e.GetX(), upY = e.GetY();
                if (!_multiTouch && _swipeStartedFit && _moved)
                    ApplySwipe((upX - _downX) / _density, (upY - _downY) / _density);
                _dragging = false;
                _pinching = false;
                _hasLastFocus = false;
                PerformClick();
                break;

            case MotionEventActions.Cancel:
                _dragging = false;
                _pinching = false;
                _hasLastFocus = false;
                break;
        }
        return true;
    }

    /// <summary>
    /// 滚轮分流（INPUT-04）：带修饰键的滚轮先按共享绑定表执行（如 Ctrl+滚轮缩放，INPUT-03），
    /// 未命中绑定再回落到默认语义——精确/非整格滚动平移照片，整格离散滚轮按格派发命令。
    /// </summary>
    /// <param name="e">通用运动事件。</param>
    /// <returns>是否消费。</returns>
    public override bool OnGenericMotionEvent(MotionEvent? e)
    {
        if (e == null || e.ActionMasked != MotionEventActions.Scroll)
            return base.OnGenericMotionEvent(e);
        if ((e.Source & (InputSourceType.ClassPointer | InputSourceType.Touchpad)) == 0)
            return base.OnGenericMotionEvent(e);
        float vertical = e.GetAxisValue(Axis.Vscroll), horizontal = e.GetAxisValue(Axis.Hscroll);
        if (Math.Abs(vertical) < .01f && Math.Abs(horizontal) < .01f) return base.OnGenericMotionEvent(e);
        int metaState = (int)e.MetaState;
        if (Math.Abs(vertical) >= .01f && AndroidInputMapping.ModifiersOf(metaState) != GestureModifiers.None
            && WheelCommand?.Invoke(vertical < 0, metaState) == true)
            return true;
        if (IsPreciseScroll(e, vertical))
        {
            if (!_viewport.IsFit)
            {
                _viewport.PanBy(-horizontal * _scrollFactorPx / _density, -vertical * _scrollFactorPx / _density);
                Draw();
                ViewportChanged?.Invoke();
            }
            return true;
        }
        if (Math.Abs(vertical) < .01f) return true;
        _wheelAccumulator += vertical;
        if (Math.Abs(_wheelAccumulator) >= 1)
        {
            bool up = _wheelAccumulator < 0;
            _wheelAccumulator = 0;
            WheelCommand?.Invoke(up, metaState);
        }
        return true;
    }

    /// <summary>精确滚动判据：触控板来源，或轴增量本身是连续的非整数。</summary>
    private static bool IsPreciseScroll(MotionEvent e, float vertical)
        => (e.Source & InputSourceType.Touchpad) != 0
            || Math.Abs(vertical) > .01f && Math.Abs(vertical - Math.Round(vertical)) > .01f;

    /// <summary>点击可访问性。</summary>
    /// <returns>是否处理。</returns>
    public override bool PerformClick()
    {
        base.PerformClick();
        return true;
    }

    /// <summary>播放视口动画：先把 mutate 算出的目标记下，再从当前状态缓出插值。</summary>
    /// <param name="mutate">直接改共享视口的动作。</param>
    private void AnimateViewport(Action mutate)
    {
        CancelAnimation();
        double startScale = _viewport.Scale, startX = _viewport.PanX, startY = _viewport.PanY;
        mutate();
        double endScale = _viewport.Scale, endX = _viewport.PanX, endY = _viewport.PanY;
        bool endFit = _viewport.IsFit;
        if (Math.Abs(endScale - startScale) < 1e-6 && Math.Abs(endX - startX) < 1e-6 && Math.Abs(endY - startY) < 1e-6)
        {
            Draw();
            ViewportChanged?.Invoke();
            return;
        }
        _viewport.ApplyGestureView(startScale, startX, startY, interactive: false);
        var animator = ValueAnimator.OfFloat(0f, 1f)!;
        animator.SetDuration((long)(AnimationSeconds * 1000));
        animator.Update += (_, args) =>
        {
            float linear = (float)(args.Animation?.AnimatedValue ?? 0f);
            double eased = FilmstripAnimation.EaseOutCubic(linear);
            _viewport.ApplyGestureView(
                startScale + (endScale - startScale) * eased,
                startX + (endX - startX) * eased,
                startY + (endY - startY) * eased,
                interactive: false);
            Draw();
            ViewportChanged?.Invoke();
        };
        animator.AnimationEnd += (_, _) =>
        {
            _viewport.ApplyGestureView(endScale, endX, endY, interactive: false);
            if (endFit || _viewport.Scale < _viewport.FitScale - 1e-9) _viewport.FitToView();
            _animation = null;
            Draw();
            ViewportChanged?.Invoke();
        };
        _animation = animator;
        animator.Start();
    }

    /// <summary>取消进行中的视口动画（用户触摸接管）。</summary>
    private void CancelAnimation()
    {
        var animation = _animation;
        _animation = null;
        animation?.Cancel();
    }

    /// <summary>释放渲染器与内存客户端登记。</summary>
    /// <param name="disposing">是否释放托管资源。</param>
    protected override void Dispose(bool disposing)
    {
        if (disposing && !_disposed)
        {
            _disposed = true;
            CancelAnimation();
            MemoryGovernor.Unregister(this);
            if (ReferenceEquals(AndroidMemoryBudgetProvider.ExternalFootprint, _textureFootprint))
                AndroidMemoryBudgetProvider.ExternalFootprint = null;
            _renderer.Dispose();
            Interlocked.Exchange(ref _textureBytes, 0);
            _surfaceView.Holder?.RemoveCallback(this);
        }
        base.Dispose(disposing);
    }

    /// <summary>
    /// 压力响应：Caution 起丢掉非当前帧的缓存纹理；GL 调用必须在主图线程执行，
    /// 而本方法由内存探测线程调用，因此只投递、不在此等待。
    /// </summary>
    /// <param name="level">压力级别。</param>
    public void Trim(PressureLevel level)
    {
        if (level < PressureLevel.Caution || _disposed) return;
        try { Post(TrimCachedTextures); }
        catch (Exception exception) { Console.Error.WriteLine($"[Native] 纹理修剪投递失败：{exception.Message}"); }
    }

    /// <summary>丢掉非当前纹理与复用空纹理（主图线程）。</summary>
    private void TrimCachedTextures()
    {
        if (_disposed) return;
        _renderer.TrimCachedTextures();
        RefreshTextureBytes();
    }

    /// <summary>刷新纹理占用快照（供内存管家与预算源读取）。</summary>
    private void RefreshTextureBytes() => Interlocked.Exchange(ref _textureBytes, _renderer.TextureBytes);

    /// <summary>单击/长按/双击手势回调。</summary>
    private sealed class PhotoGestureListener : GestureDetector.SimpleOnGestureListener
    {
        private readonly AndroidPhotoView _owner;

        /// <summary>绑定宿主。</summary>
        /// <param name="owner">主图视图。</param>
        public PhotoGestureListener(AndroidPhotoView owner) => _owner = owner;

        /// <summary>单击：只在没有拖动时触发。</summary>
        /// <param name="e">事件。</param>
        /// <returns>消费。</returns>
        public override bool OnSingleTapUp(MotionEvent? e)
        {
            if (!_owner._moved) _owner.TapRequested?.Invoke();
            return true;
        }

        /// <summary>长按：弹主图菜单。</summary>
        /// <param name="e">事件。</param>
        public override void OnLongPress(MotionEvent? e)
        {
            _owner._moved = true;
            _owner.LongPressRequested?.Invoke(e?.GetX() ?? 0, e?.GetY() ?? 0);
        }

        /// <summary>双击切换适应/100%。</summary>
        /// <param name="e">事件。</param>
        /// <returns>消费。</returns>
        public override bool OnDoubleTap(MotionEvent? e)
        {
            if (e == null) return false;
            _owner._moved = true;
            _owner.DoubleTapAt((e.GetX() / _owner._density, e.GetY() / _owner._density));
            return true;
        }
    }

    /// <summary>捏合缩放：缩放与焦点平移都写共享视口。</summary>
    private sealed class PhotoScaleListener : ScaleGestureDetector.SimpleOnScaleGestureListener
    {
        private readonly AndroidPhotoView _owner;

        /// <summary>绑定宿主。</summary>
        /// <param name="owner">主图视图。</param>
        public PhotoScaleListener(AndroidPhotoView owner) => _owner = owner;

        /// <summary>开始捏合：取消动画。</summary>
        /// <param name="detector">识别器。</param>
        /// <returns>消费。</returns>
        public override bool OnScaleBegin(ScaleGestureDetector detector)
        {
            _owner.CancelAnimation();
            _owner._pinching = true;
            _owner._multiTouch = true;
            _owner._hasLastFocus = false;
            return true;
        }

        /// <summary>每帧缩放：先按焦点缩放，再按焦点位移平移。</summary>
        /// <param name="detector">识别器。</param>
        /// <returns>消费。</returns>
        public override bool OnScale(ScaleGestureDetector detector)
        {
            if (_owner._image == null) return false;
            double focusX = detector.FocusX / _owner._density;
            double focusY = detector.FocusY / _owner._density;
            _owner._viewport.ZoomBy(detector.ScaleFactor, (focusX, focusY));
            if (_owner._hasLastFocus)
            {
                _owner._viewport.PanBy(focusX - _owner._lastFocusX, focusY - _owner._lastFocusY);
            }
            if (_owner._viewport.Scale < _owner._viewport.FitScale - 1e-9) _owner._viewport.FitToView();
            _owner._lastFocusX = (float)focusX;
            _owner._lastFocusY = (float)focusY;
            _owner._hasLastFocus = true;
            _owner.Draw();
            _owner.ViewportChanged?.Invoke();
            return true;
        }

        /// <summary>结束捏合。</summary>
        /// <param name="detector">识别器。</param>
        public override void OnScaleEnd(ScaleGestureDetector detector)
        {
            _owner._pinching = false;
            _owner._hasLastFocus = false;
            _owner._viewport.ClampPan();
            _owner.Draw();
            _owner.ViewportChanged?.Invoke();
        }
    }
}
