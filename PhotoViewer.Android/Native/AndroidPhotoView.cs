using System;
using System.Diagnostics;
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
/// Android 主图宿主（A2）：<see cref="TextureView"/> + 自有 EGL 渲染核心，触摸手势直接驱动共享
/// <see cref="PhotoViewport"/>（视图坐标 = dp，物理倍率 = density）。
/// EGL 窗口表面建在 <see cref="TextureView"/> 的表面纹理上，因此主图与其余界面层同属一个窗口缓冲，
/// 界面层可以取到主图像素；缓冲尺寸恒等于视图物理像素、视图不做缩放平移变换，物理 100% 不经二次重采样。
/// 缩放/平移不用系统滚动容器：单指拖动只在放大态平移，双指捏合锚定焦点，双击/命令走
/// <see cref="PhotoViewport.ToggleFit"/>；代理↔Full 热替换只换纹理，不碰视口状态。
/// </summary>
internal sealed partial class AndroidPhotoView : FrameLayout, TextureView.ISurfaceTextureListener, IMemoryClient
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
    private readonly TextureView _textureView;
    /// <summary>当前可用的表面纹理：所有权在 <see cref="TextureView"/>，本视图只在销毁时丢引用，绝不释放它。</summary>
    private SurfaceTexture? _surfaceTexture;
    /// <summary>包住表面纹理的 EGL 窗口表面来源：本视图所有，销毁与重建时统一经 <see cref="ReleaseWindowSurface"/> 释放。</summary>
    private Surface? _windowSurface;
    /// <summary>表面纹理被界面层取用的累计次数（单调递增，只供验收取样）。</summary>
    private int _textureUpdates;
    /// <summary>最近一次表面纹理被取用的时间戳（<see cref="Stopwatch.GetTimestamp"/>，单调，0 表示还没取用过）。</summary>
    private long _lastTextureUpdateTimestamp;
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
    private bool _touchActive;
    private bool _gestureCancelled;
    private bool _longPressed;
    private long _downTime;
    /// <summary>本轮 Down 真正被视图接收的时刻（uptime）：积压事件的按住时长从这里起算，不把阻塞时长当成已按住。</summary>
    private long _downReceivedAt;
    /// <summary>触摸轮次：每次 Down 递增，异步确认据此丢弃上一轮的长按候选。</summary>
    private int _touchRound;
    /// <summary>待确认的长按候选；被新候选取代或作废后为 null，已投递的旧回收不再生效。</summary>
    private Java.Lang.Runnable? _longPressConfirm;
    private float _longPressX, _longPressY;
    /// <summary>系统长按阈值（毫秒），与手势识别器读同一份系统配置。</summary>
    private readonly long _longPressTimeoutMs = (int)ViewConfiguration.LongPressTimeout;
    private float _downX, _downY, _lastX, _lastY, _lastFocusX, _lastFocusY;
    private bool _hasLastFocus;
    /// <summary>离散滚轮尚未累积到一格的增量（INPUT-04）。</summary>
    private double _wheelAccumulator;
    /// <summary>纹理占用快照（含 mip 链），GPU 私有内存不在 RssAnon 里，供内存预算源与管家读取。</summary>
    private long _textureBytes;
    /// <summary>挂到 <see cref="AndroidMemoryBudgetProvider.ExternalFootprint"/> 的读数委托（释放时只摘自己这份）。</summary>
    private readonly Func<long> _textureFootprint;

    /// <summary>EGL 窗口表面就绪，允许宿主启动加载并按实际输出上传照片。</summary>
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
    /// <summary>主图单击（首屏空态打开文件夹 / 点空白清除细节预览）；参数为触点在本视图内的像素坐标。</summary>
    internal event Action<float, float>? TapRequested;

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
    /// <summary>验收用：平台长按候选被登记的次数（含随后被积压的移动/抬起作用作废的候选）。</summary>
    internal int LongPressCandidatesForChecks { get; private set; }
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

    /// <summary>建立主图纹理视图、渲染器与手势识别。</summary>
    /// <param name="context">宿主上下文（Activity）。</param>
    public AndroidPhotoView(Context context) : base(context)
    {
        _density = Resources?.DisplayMetrics?.Density ?? 1;
        _touchSlop = ViewConfiguration.Get(context)?.ScaledTouchSlop ?? 8;
        // API 26 起提供滚动因子；更低版本回退到系统默认的 64px/格。
        float scrollFactor = OperatingSystem.IsAndroidVersionAtLeast(26)
            ? ViewConfiguration.Get(context)?.ScaledVerticalScrollFactor ?? 0
            : 0;
        // 部分模拟器/设备返回 0；精确滚动仍需一个有效像素步长，0 会让触控板平移变成空操作。
        _scrollFactorPx = scrollFactor > .01f ? scrollFactor : 64;
        SetBackgroundColor(Color.Black);
        _textureView = new TextureView(context);
        // 主图区恒为整块不透明画面：显式走不透明合成路径，避免额外离屏缓冲与逐层混合。
        _textureView.SetOpaque(true);
        _textureView.SurfaceTextureListener = this;
        AddView(_textureView, new LayoutParams(LayoutParams.MatchParent, LayoutParams.MatchParent));
        _gestures = new GestureDetector(context, new PhotoGestureListener(this));
        _scaleDetector = new ScaleGestureDetector(context, new PhotoScaleListener(this));
        // GPU 纹理不占 RssAnon，预算源要单独加上这份占用才与 MemoryLimiter 的可用量同口径。
        _textureFootprint = () => Interlocked.Read(ref _textureBytes);
        AndroidMemoryBudgetProvider.ExternalFootprint = _textureFootprint;
        MemoryGovernor.Register(this);
    }

    private void SetImageCore(DecodedImage? image, int generation, DecodedImage? prepared = null)
    {
        if (_disposed) return;
        bool cut = generation != _generation;
        if (!cut && ReferenceEquals(_image, image) && _renderer.HasTexture)
        {
            RequestDraw();
            ViewportChanged?.Invoke();
            return;
        }
        var previous = _image;
        bool replace = !ReferenceEquals(previous, image);
        if (replace && image != null && !image.TryAcquire())
            throw new ObjectDisposedException(nameof(image));
        try
        {
            if (_surfaceReady) _renderer.SetImage(image, prepared);
        }
        catch
        {
            if (replace) image?.Release();
            throw;
        }
        _image = image;
        _generation = generation;
        if (replace) previous?.Release();
        RefreshTextureBytes();
        if (image == null)
        {
            _identity = default;
            _wheelAccumulator = 0;
            _viewport.ClearTexture();
        }
        else
        {
            var identity = (image.SourceWidth, image.SourceHeight, image.Orientation);
            if (!cut && identity == _identity)
                _viewport.ApplyTexture(_renderer.TextureWidth);
            else
            {
                _identity = identity;
                _wheelAccumulator = 0;
                _viewport.BeginImageChange();
                _viewport.ApplyImage(image.SourceWidth, image.SourceHeight, image.Orientation, _renderer.TextureWidth);
            }
        }
        RequestDraw();
        ViewportChanged?.Invoke();
    }

    /// <summary>
    /// 主图适应区四边内边距（SHELL-11）：主图视图铺满窗口，照片在适应区内适应、放大后可越过栏缘；
    /// 适应态重新贴合，放大态保持当前中心与倍率并按新边界钳制。
    /// </summary>
    /// <param name="insets">四边内边距（dp）。</param>
    internal void SetFitInsets(EdgeInsets insets)
    {
        var fit = new EdgeInsets(Math.Max(0, insets.Left), Math.Max(0, insets.Top),
            Math.Max(0, insets.Right), Math.Max(0, insets.Bottom));
        if (Math.Abs(fit.Left - _viewport.FitInsets.Left) < .01
            && Math.Abs(fit.Top - _viewport.FitInsets.Top) < .01
            && Math.Abs(fit.Right - _viewport.FitInsets.Right) < .01
            && Math.Abs(fit.Bottom - _viewport.FitInsets.Bottom) < .01)
            return;
        var center = _viewport.CenterUV;
        _viewport.SetFitInsets(fit);
        if (_viewport.IsFit)
        {
            _viewport.FitToView();
        }
        else
        {
            // 保持放大态中心：共享视口按 (.5 - (Pan - FitCenter)/width) 解释中心，平移必须包含 FitCenter。
            _viewport.CenterOnNormalizedPoint(center.U, center.V);
        }
        RequestDraw();
        Post(RequestDraw);
        ViewportChanged?.Invoke();
    }

    /// <summary>适应视口。</summary>
    public void FitToView()
    {
        if (_image == null) return;
        _viewport.FitToView();
        RequestDraw();
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
        RequestDraw();
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

    internal bool SurfaceReadyForChecks => _surfaceReady;
    internal bool FramebufferEncodingForChecks(int encoding) => _renderer.FramebufferEncoding == encoding;
    internal bool EglColorspaceForChecks(int colorspace) => _renderer.SurfaceColorspace == colorspace;

    /// <summary>验收用：当前 EGL 窗口表面的真实来源（未就绪时抛出，产品路径不读取）。</summary>
    internal Surface PresentationSurfaceForChecks
        => _windowSurface ?? throw new InvalidOperationException("主图表面未就绪");

    /// <summary>验收用：EGL 窗口表面的实际缓冲尺寸（像素），用于核对缓冲等于视图物理像素。</summary>
    internal (int Width, int Height) SurfaceExtentForChecks => _renderer.SurfaceExtentForChecks;

    /// <summary>验收用：表面纹理被界面层取用的累计次数（单调递增，只用于「输入 → 界面层取用」的延迟取样）。</summary>
    internal int TextureUpdatesForChecks => Volatile.Read(ref _textureUpdates);

    /// <summary>
    /// 验收用：最近一次表面纹理被取用的时间戳（<see cref="Stopwatch.GetTimestamp"/>，与检查侧同源同单调）；
    /// 全程累计、不随表面重建清零，0 表示还没取用过。
    /// </summary>
    internal long LastTextureUpdateTimestampForChecks => Interlocked.Read(ref _lastTextureUpdateTimestamp);

    /// <summary>
    /// 回读当前照片在视口归一化坐标处的真实输出像素（默认帧缓冲，不是屏幕像素）。
    /// 回读内部会补一次带 present 的绘制，这里顺带让界面层取用这一帧，避免窗口停在更早的内容上。
    /// </summary>
    /// <param name="u">显示横向归一化坐标。</param>
    /// <param name="v">显示纵向归一化坐标。</param>
    /// <param name="bypassOutputTransfer">是否跳过着色器对 sRGB 帧缓冲的逆传递。</param>
    /// <returns>回读到的 RGBA 通道。</returns>
    internal (byte R, byte G, byte B, byte A) ReadPixelForChecks(double u, double v, bool bypassOutputTransfer = false)
    {
        var pixel = _renderer.ReadPixelForChecks(_viewport, _density, u, v, bypassOutputTransfer);
        _textureView.Invalidate();
        return pixel;
    }

    /// <summary>
    /// 验收用：按视图左上原点整块回读默认帧缓冲（RGBA8，行序已翻成左上原点），返回前补一次重绘，
    /// 让窗口取用刚提交的这帧。产品路径不读取。
    /// </summary>
    /// <param name="viewport">当前视口。</param>
    /// <param name="density">绘制密度。</param>
    /// <param name="x">区域左上横坐标（缓冲像素，视图左上原点）。</param>
    /// <param name="y">区域左上纵坐标（缓冲像素，视图左上原点）。</param>
    /// <param name="width">区域宽（像素）。</param>
    /// <param name="height">区域高（像素）。</param>
    /// <returns>RGBA8 缓冲（<c>width × height × 4</c> 字节）。</returns>
    internal byte[] ReadRegionForChecks(PhotoViewport viewport, double density, int x, int y, int width, int height)
    {
        var pixels = _renderer.ReadRegionForChecks(viewport, density, x, y, width, height);
        _textureView.Invalidate();
        return pixels;
    }

    /// <summary>
    /// 验收用：在同一个表面纹理上销毁并重建 EGL 窗口表面与上下文，重传当前图。
    /// 需要表面已就绪（没有真实表面时不做无表面的假重建），产品路径不调用。
    /// </summary>
    /// <param name="allowP3">是否允许 P3 表面。</param>
    /// <param name="allowPassthrough">是否允许 P3 passthrough。</param>
    internal void RecreateSurfaceForChecks(bool allowP3, bool allowPassthrough = true)
    {
        var surface = PresentationSurfaceForChecks;
        _surfaceReady = false;
        _surfaceVersion++;
        InvalidatePresentation();
        CancelDraw();
        _renderer.Dispose();
        _renderer = CreateRenderer();
        _renderer.Create(surface, _density, allowP3, allowPassthrough);
        _renderer.Resize(Width, Height);
        _surfaceReady = true;
        if (_image != null)
        {
            _renderer.SetImage(_image);
            _viewport.ApplyTexture(_renderer.TextureWidth);
        }
        RefreshTextureBytes();
        RequestDraw();
    }

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

    internal void Draw()
    {
        CancelDraw();
        if (!_surfaceReady || _disposed) return;
        // 窗口不可见时表面纹理没有消费者，swap 会在 dequeueBuffer 上阻塞主线程：这一帧直接跳过，
        // 重新可见时由 OnWindowVisibilityChanged 再排一帧。
        if (WindowVisibility != ViewStates.Visible) return;
        if (_renderer.DrawFrame(_viewport, _density))
        {
            _renderedFrames++;
            // 表面纹理的内容只在视图重绘时被界面渲染线程取用：swap 之后必须安排一次重绘，否则新帧要等下一次无关重绘。
            _textureView.Invalidate();
        }
    }

    /// <summary>窗口重新可见时补一帧：不可见期间的绘制请求被跳过，画面仍是上一帧。</summary>
    /// <param name="visibility">窗口可见性。</param>
    protected override void OnWindowVisibilityChanged(ViewStates visibility)
    {
        base.OnWindowVisibilityChanged(visibility);
        if (visibility == ViewStates.Visible) RequestDraw();
    }

    /// <summary>上报视口像素尺寸与 Full 需求。</summary>
    internal void ReportViewport()
    {
        if (Width < 1 || Height < 1) return;
        ViewportChanged?.Invoke();
    }

    /// <summary>
    /// 表面可用：建立 EGL 窗口表面与上下文，重传当前图并通知宿主可以开始加载。
    /// </summary>
    /// <param name="surface">表面纹理（所有权在 <see cref="TextureView"/>，这里只借用）。</param>
    /// <param name="width">表面宽（像素）。</param>
    /// <param name="height">表面高（像素）。</param>
    public void OnSurfaceTextureAvailable(SurfaceTexture surface, int width, int height)
    {
        if (_disposed) return;
        EstablishSurface(surface, width, height);
    }

    /// <summary>
    /// 建立（或重建）EGL 窗口表面：按视图物理像素设定缓冲尺寸，把在途色域准备作废（表面可能换了色域能力），
    /// 重传当前图并通知宿主可以开始加载。可用回调与「尺寸就绪但表面尚未建立」的补建路径共用同一段顺序。
    /// </summary>
    /// <param name="surface">表面纹理（所有权在 <see cref="TextureView"/>，这里只借用）。</param>
    /// <param name="width">表面宽（像素）。</param>
    /// <param name="height">表面高（像素）。</param>
    private void EstablishSurface(SurfaceTexture surface, int width, int height)
    {
        // 回调带的尺寸在个别时序上可能还是 0（视图尚未完成布局）：退回本视图已量测的像素尺寸。
        if (width < 1 || height < 1)
        {
            width = Width;
            height = Height;
        }
        if (width < 1 || height < 1) return;
        try
        {
            _surfaceVersion++;
            InvalidatePresentation();
            _surfaceTexture = surface;
            SetSurfaceBufferSize(width, height);
            // 先销毁可能残留的 EGL 表面，再撤旧的生产者引用，最后建新的表面来源：
            // 任何时刻都不让 EGL 表面指向已释放的 Surface。
            _renderer.Dispose();
            _renderer = CreateRenderer();
            ReleaseWindowSurface();
            _windowSurface = new Surface(surface);
            _renderer.Create(_windowSurface, _density);
            _renderer.Resize(width, height);
            _surfaceReady = true;
            PresentImage(_requestedImage ?? _image, _requestedGeneration);
            RefreshTextureBytes();
            RequestDraw();
            SurfaceReady?.Invoke();
        }
        catch (Exception exception)
        {
            _surfaceReady = false;
            // 建表面失败时先销毁已建的 EGL 表面，再撤本视图的生产者引用，最后把表面纹理交还 TextureView（不在这里释放它）。
            _renderer.Dispose();
            ReleaseWindowSurface();
            _surfaceTexture = null;
            RefreshTextureBytes();
            Console.Error.WriteLine($"[Native] 主图表面创建失败：{exception.Message}");
        }
    }

    /// <summary>
    /// 表面缓冲尺寸变化：先把表面纹理的默认缓冲尺寸改到新尺寸，再刷新视口像素并补帧。
    /// EGL 窗口表面不重建（缓冲随下一次出队跟随），纹理与纹理缓存都不丢；
    /// 若表面此前因尺寸无效没有建成，则在这里补建。
    /// </summary>
    /// <param name="surface">表面纹理。</param>
    /// <param name="width">新宽（像素）。</param>
    /// <param name="height">新高（像素）。</param>
    public void OnSurfaceTextureSizeChanged(SurfaceTexture surface, int width, int height)
    {
        if (_disposed) return;
        _surfaceTexture = surface;
        SetSurfaceBufferSize(width, height);
        if (!_surfaceReady)
        {
            EstablishSurface(surface, width, height);
            return;
        }
        _renderer.Resize(width, height);
        RequestDraw();
        // 与原先的表面尺寸变化同样处理：新尺寸的缓冲要到下一帧才稳定就绪，补两帧避免停在旧尺寸的帧上。
        Post(RequestDraw);
        PostDelayed(RequestDraw, 32);
    }

    /// <summary>
    /// 表面销毁：释放 EGL 与窗口表面，把在途色域准备作废，并交还表面纹理的所有权。
    /// 返回 <c>true</c> 表示由 <see cref="TextureView"/> 自己释放表面纹理——本视图绝不代它释放。
    /// </summary>
    /// <param name="surface">即将销毁的表面纹理。</param>
    /// <returns>恒为 <c>true</c>。</returns>
    public bool OnSurfaceTextureDestroyed(SurfaceTexture surface)
    {
        // 已释放的视图：EGL 与窗口表面已在 Dispose 里收过，这里只把表面纹理交还 TextureView。
        if (_disposed) return true;
        _surfaceReady = false;
        _surfaceVersion++;
        InvalidatePresentation();
        CancelDraw();
        // 先销毁 EGL 窗口表面（内含 EglDestroySurface），再撤本视图的生产者引用，顺序不可颠倒。
        _renderer.Dispose();
        _renderer = CreateRenderer();
        ReleaseWindowSurface();
        _surfaceTexture = null;
        RefreshTextureBytes();
        return true;
    }

    /// <summary>
    /// 表面纹理更新：界面渲染线程已取用新帧。这里只记录单调计数与时间戳，
    /// 供验收把「一次输入」与「该帧真正进入界面层」对应起来；不改任何渲染或视口状态。
    /// </summary>
    /// <param name="surface">刚更新的表面纹理。</param>
    public void OnSurfaceTextureUpdated(SurfaceTexture surface)
    {
        Interlocked.Increment(ref _textureUpdates);
        Interlocked.Exchange(ref _lastTextureUpdateTimestamp, Stopwatch.GetTimestamp());
    }

    /// <summary>
    /// 释放当前 EGL 窗口表面来源并清空引用：先调 Java 的 <c>Surface.Release()</c> 立刻撤掉本进程的生产者引用，
    /// 再 <c>Dispose()</c> 释放 JNI 引用（<c>Dispose</c> 自身不会调用 <c>Release</c>，少了前一步就只等 Java 终结器）。
    /// 调用前必须已经销毁 EGL 窗口表面——顺序绝不能反过来。
    /// </summary>
    private void ReleaseWindowSurface()
    {
        var surface = _windowSurface;
        _windowSurface = null;
        if (surface == null) return;
        try
        {
            surface.Release();
        }
        catch (Exception exception)
        {
            // 已经释放或包装器已失效：仍要把托管引用收干净，下一次可用回调会重新建。
            Console.Error.WriteLine($"[Native] 主图表面来源释放失败：{exception.Message}");
        }
        finally
        {
            surface.Dispose();
        }
    }

    /// <summary>
    /// 把表面纹理的默认缓冲尺寸设成视图物理像素：EGL 窗口表面按它分配缓冲，
    /// 视图自身不做缩放平移变换，物理 100% 因此不经二次重采样。
    /// </summary>
    /// <param name="width">宽（像素）。</param>
    /// <param name="height">高（像素）。</param>
    private void SetSurfaceBufferSize(int width, int height)
    {
        if (_surfaceTexture == null || width < 1 || height < 1) return;
        try
        {
            _surfaceTexture.SetDefaultBufferSize(width, height);
        }
        catch (Exception exception)
        {
            // 表面已交还或托管包装器已失效：本次不设尺寸，等下一次可用回调重新建立。
            Console.Error.WriteLine($"[Native] 主图缓冲尺寸设置失败：{exception.Message}");
        }
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
            _viewport.CenterOnNormalizedPoint(center.U, center.V);
        }
        // 表面纹理的缓冲尺寸要显式跟随视图物理像素：布局变化与表面回调的到达顺序在设备间并不固定。
        SetSurfaceBufferSize(w, h);
        _renderer.Resize(w, h);
        RequestDraw();
        // 缓冲可能在下一帧才跟上：再补一帧，避免缩略图/控制栏/分析栏开合时停在旧尺寸。
        Post(RequestDraw);
        ViewportChanged?.Invoke();
    }

    /// <summary>触摸：捏合 → 缩放；放大态拖动 → 平移；适应态横滑 → 翻页。</summary>
    /// <param name="e">触摸事件。</param>
    /// <returns>始终消费（主图区域没有可滚动子视图）。</returns>
    public override bool OnTouchEvent(MotionEvent? e)
    {
        if (e == null || _disposed) return false;
        switch (e.ActionMasked)
        {
            case MotionEventActions.Down:
                CancelAnimation();
                // 新一轮按下：上一轮尚未确认的长按候选作废，按住时长从这次真正送达视图起算。
                InvalidateLongPressCandidate();
                _touchRound++;
                _downReceivedAt = SystemClock.UptimeMillis();
                _downX = _lastX = e.GetX();
                _downY = _lastY = e.GetY();
                _downTime = e.DownTime;
                _touchActive = true;
                _gestureCancelled = false;
                _longPressed = false;
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
                InvalidateLongPressCandidate();
                CancelTapGesture(e);
                break;

            case MotionEventActions.Move:
            case MotionEventActions.Up:
                if (_touchActive) UpdateTouchMovement(e);
                break;

            case MotionEventActions.Cancel:
                InvalidateLongPressCandidate();
                _touchActive = false;
                CancelTapGesture(e);
                break;
        }

        _scaleDetector.OnTouchEvent(e);
        if (!_gestureCancelled) _gestures.OnTouchEvent(e);
        switch (e.ActionMasked)
        {
            case MotionEventActions.Move:
                if (!_touchActive || _longPressed || e.PointerCount > 1 || _pinching) break;
                float x = e.GetX(), y = e.GetY();
                double dx = (x - _lastX) / _density, dy = (y - _lastY) / _density;
                if (!_viewport.IsFit && _moved)
                {
                    _viewport.PanBy(dx, dy);
                    _lastX = x;
                    _lastY = y;
                    RequestDraw();
                    ViewportChanged?.Invoke();
                }
                break;

            case MotionEventActions.PointerUp:
                _hasLastFocus = false;
                int remaining = e.ActionIndex == 0 ? 1 : 0;
                if (remaining < e.PointerCount)
                {
                    _lastX = e.GetX(remaining);
                    _lastY = e.GetY(remaining);
                }
                break;

            case MotionEventActions.Up:
                InvalidateLongPressCandidate();
                float upX = e.GetX(), upY = e.GetY();
                if (_touchActive && !_longPressed && !_multiTouch && _swipeStartedFit && _moved)
                    ApplySwipe((upX - _downX) / _density, (upY - _downY) / _density);
                _touchActive = false;
                _dragging = false;
                _pinching = false;
                _hasLastFocus = false;
                if (!_moved && !_multiTouch && !_longPressed) PerformClick();
                break;

            case MotionEventActions.Cancel:
                _dragging = false;
                _pinching = false;
                _hasLastFocus = false;
                break;
        }
        return true;
    }

    private void UpdateTouchMovement(MotionEvent e)
    {
        if (!_moved)
        {
            for (int history = 0; history < e.HistorySize; history++)
            {
                if (!ExceededTouchSlop(e.GetHistoricalX(history), e.GetHistoricalY(history))) continue;
                _moved = true;
                break;
            }
            if (ExceededTouchSlop(e.GetX(), e.GetY())) _moved = true;
        }
        if (_moved)
        {
            InvalidateLongPressCandidate();
            CancelTapGesture(e);
        }
    }

    private bool ExceededTouchSlop(float x, float y)
    {
        double dx = x - _downX, dy = y - _downY;
        return dx * dx + dy * dy > (double)_touchSlop * _touchSlop;
    }

    private void CancelTapGesture(MotionEvent e)
    {
        if (_gestureCancelled) return;
        _gestureCancelled = true;
        using var cancel = MotionEvent.Obtain(e)!;
        cancel.Action = MotionEventActions.Cancel;
        _gestures.OnTouchEvent(cancel);
    }

    /// <summary>
    /// 平台长按候选：只登记，不立即判定。平台按 <c>DownTime</c> 计时，Down 因主线程阻塞而积压时
    /// 候选会在事件刚送达就触发，此时积压的移动/抬起还没落地；按住时长改从本轮 Down 真正送达视图起算。
    /// 事件只在本次调用内读取触发点，不跨异步持有。
    /// </summary>
    /// <param name="e">触发候选的事件。</param>
    private void BeginLongPressCandidate(MotionEvent e)
    {
        if (_disposed || !_touchActive || _longPressed || _moved || _multiTouch || _pinching || _gestureCancelled) return;
        if (e.DownTime != _downTime) return;
        LongPressCandidatesForChecks++;
        _longPressX = e.GetX();
        _longPressY = e.GetY();
        long remaining = _downReceivedAt + _longPressTimeoutMs - SystemClock.UptimeMillis();
        ArmLongPressConfirmation(remaining, nextFrame: remaining <= 0);
    }

    /// <summary>排定一次长按确认：按住时长不足时按剩余时间重排，够时长也先让一帧再最终判定。</summary>
    /// <param name="delayMs">距本轮 Down 真正送达满阈值的剩余毫秒。</param>
    /// <param name="nextFrame">是否等到下一帧，把本轮已积压/待处理的移动与抬起先交给手势状态。</param>
    private void ArmLongPressConfirmation(long delayMs, bool nextFrame)
    {
        int round = _touchRound;
        Java.Lang.Runnable confirmation = null!;
        confirmation = new Java.Lang.Runnable(() =>
        {
            if (!ReferenceEquals(_longPressConfirm, confirmation) || round != _touchRound) return;
            if (nextFrame) ConfirmLongPress(round, confirmation);
            else ArmLongPressConfirmation(0, nextFrame: true);
        });
        _longPressConfirm = confirmation;
        if (nextFrame) PostOnAnimation(confirmation);
        else PostDelayed(confirmation, Math.Max(1L, delayMs));
    }

    /// <summary>最终确认：本轮标识与按住状态在确认这一刻重新核对，已作废或已过期的候选不生效。</summary>
    /// <param name="round">登记候选时的触摸轮次。</param>
    /// <param name="confirmation">本次确认的回调，与当前候选不同即表示已被取代或作废。</param>
    private void ConfirmLongPress(int round, Java.Lang.Runnable confirmation)
    {
        if (!ReferenceEquals(_longPressConfirm, confirmation)) return;
        _longPressConfirm = null;
        if (_disposed || round != _touchRound || !_touchActive || _longPressed || _moved
            || _multiTouch || _pinching || _gestureCancelled) return;
        long elapsed = SystemClock.UptimeMillis() - _downReceivedAt;
        if (elapsed < _longPressTimeoutMs)
        {
            ArmLongPressConfirmation(_longPressTimeoutMs - elapsed, nextFrame: false);
            return;
        }
        _longPressed = true;
        _moved = true;
        LongPressRequested?.Invoke(_longPressX, _longPressY);
    }

    /// <summary>作废旧候选：新 Down、第二指、越容差的移动、抬起、取消与释放都让当前候选失效。</summary>
    private void InvalidateLongPressCandidate()
    {
        var pending = _longPressConfirm;
        _longPressConfirm = null;
        if (pending != null) RemoveCallbacks(pending);
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
                RequestDraw();
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
            RequestDraw();
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
            RequestDraw();
            ViewportChanged?.Invoke();
        };
        animator.AnimationEnd += (_, _) =>
        {
            // 取消（用户触摸接管、被新动画取代、释放）同样会走到结束回调：只让仍是当前动画的正常完成落地，
            // 迟到的旧结束不得把视口拉回旧终点，触摸接管停在当前帧。
            if (!ReferenceEquals(_animation, animator)) return;
            _animation = null;
            _viewport.ApplyGestureView(endScale, endX, endY, interactive: false);
            if (endFit || _viewport.Scale < _viewport.FitScale - 1e-9) _viewport.FitToView();
            RequestDraw();
            ViewportChanged?.Invoke();
        };
        _animation = animator;
        animator.Start();
    }

    /// <summary>取消进行中的视口动画（用户触摸接管、被新动画取代）：停在当前帧，迟到结束回调不再改写视口。</summary>
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
            _surfaceVersion++;
            InvalidatePresentation();
            CancelDraw();
            SetRequestedImage(null, _generation);
            _image?.Release();
            _image = null;
            CancelAnimation();
            InvalidateLongPressCandidate();
            MemoryGovernor.Unregister(this);
            if (ReferenceEquals(AndroidMemoryBudgetProvider.ExternalFootprint, _textureFootprint))
                AndroidMemoryBudgetProvider.ExternalFootprint = null;
            _renderer.Dispose();
            Interlocked.Exchange(ref _textureBytes, 0);
            // 先摘监听（不再收表面回调），再撤生产者引用；表面纹理仍归 TextureView，由基类释放视图时处理。
            _textureView.SurfaceTextureListener = null;
            ReleaseWindowSurface();
            _surfaceTexture = null;
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
            if (e != null && _owner._touchActive && !_owner._moved && !_owner._multiTouch && !_owner._longPressed)
                _owner.TapRequested?.Invoke(e.GetX(), e.GetY());
            return true;
        }

        /// <summary>长按：登记候选，由视图按本轮实际按住时长与积压输入最终判定。</summary>
        /// <param name="e">事件。</param>
        public override void OnLongPress(MotionEvent? e)
        {
            if (e == null) return;
            _owner.BeginLongPressCandidate(e);
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
            _owner.RequestDraw();
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
            _owner.RequestDraw();
            _owner.ViewportChanged?.Invoke();
        }
    }
}
