using AppKit;
using CoreGraphics;
using Foundation;
using PhotoViewer.Apple;
using PhotoViewer.Core.Image;
using PhotoViewer.Core.Memory;

namespace PhotoViewer.Mac.Native;

/// <summary>原生照片表面的 AppKit 宿主：窗口事件、命中测试与展示接线；视口数学与 Metal 渲染在共享 Apple 源中。</summary>
public sealed class MetalPhotoView : NSView, IMemoryClient
{
    private readonly PhotoViewport _viewport = new();
    private readonly MetalPhotoRenderer _renderer;
    private CGPoint _lastDrag;
    /// <summary>已上传的展示产物引用：同一对象再次上屏（会话状态通知）时只重绘，不重复上传整张纹理。</summary>
    private DecodedImage? _textureSource;

    /// <summary>视口变化通知，加载会话据此决定代理尺寸与 Full 升级。</summary>
    public event Action? ViewportChanged;
    /// <summary>离散滚轮或按键等交由窗口按用户快捷键表匹配。</summary>
    public Func<NSEvent, bool>? InputHandler { get; set; }
    /// <summary>当前照片的归一化对焦点；键盘放大与对焦连续翻页复用。</summary>
    public CGPoint? FocusPoint
    {
        get => _viewport.FocusX is { } x && _viewport.FocusY is { } y ? new CGPoint(x, y) : null;
        set
        {
            if (value.HasValue) _viewport.SetFocus((double)value.Value.X, (double)value.Value.Y);
            else _viewport.SetFocus(null, null);
        }
    }
    /// <summary>内存客户端名称。</summary>
    public string Name => "MetalPhotoView";
    /// <summary>纹理及三重缓冲的估算 GPU 字节，避免只记托管解码缓冲。</summary>
    public long FootprintBytes => _renderer.TextureBytes
        + (long)(_viewport.ViewWidth * _viewport.ViewHeight * _viewport.BackingScale * _viewport.BackingScale * 8 * 3);
    /// <summary>原生坐标使用从上到下的逻辑点。</summary>
    public override bool IsFlipped => true;
    /// <summary>照片区域可接收键盘与手势。</summary>
    public override bool AcceptsFirstResponder() => true;
    /// <summary>缩放是否处于适应状态。</summary>
    public bool IsFit => _viewport.IsFit;
    /// <summary>源像素对屏幕物理像素的倍率。</summary>
    public double PhysicalScale => _viewport.PhysicalScale;
    /// <summary>当前全分辨率细节需求，包括代理覆盖不足的放大。</summary>
    public bool NeedsFull => _viewport.NeedsFull;
    /// <summary>显示朝向的逻辑图像宽（像素）。</summary>
    public double DisplayWidth => _viewport.ImageWidth;
    /// <summary>显示朝向的逻辑图像高（像素）。</summary>
    public double DisplayHeight => _viewport.ImageHeight;
    /// <summary>图像像素到视图点的当前缩放（点/像素）。</summary>
    public double PointScale => _viewport.Scale;
    /// <summary>导航图逻辑点尺寸：长边 150、按显示朝向照片比例缩小，不补方形衬边；无照片时为 150 方形占位。</summary>
    public CGSize NavigationSize
    {
        get
        {
            var (width, height) = _viewport.NavigationSize;
            return new CGSize(width, height);
        }
    }
    /// <summary>导航图内的可见区域（NavigationSize 逻辑点坐标）。</summary>
    public CGRect NavigationViewport
    {
        get
        {
            var (x, y, width, height) = _viewport.NavigationViewport;
            return new CGRect(x, y, width, height);
        }
    }
    private double BackingScale => (double)(Window?.BackingScaleFactor ?? 1);

    /// <summary>把显示朝向的归一化图像点换算成视图坐标（点）。</summary>
    /// <param name="u">归一化横坐标。</param>
    /// <param name="v">归一化纵坐标。</param>
    /// <returns>视图坐标系（顶边为原点）中的点。</returns>
    public CGPoint ViewPoint(double u, double v)
    {
        var (x, y) = _viewport.ViewPoint(u, v);
        return new CGPoint(x, y);
    }

    /// <summary>建立共享 Metal 渲染核心并把照片层作为本视图图层；不支持时显式失败。</summary>
    public MetalPhotoView(CGRect frame) : base(frame)
    {
        _renderer = new MetalPhotoRenderer(_viewport);
        WantsLayer = true;
        Layer = _renderer.Layer;
        MemoryGovernor.Register(this);
    }

    /// <summary>同步上传 pinned 像素并生成 mipmap；Metal 命令队列顺序保证绘制前生成完毕。编码支持范围由渲染核心判定。</summary>
    public void SetImage(DecodedImage? image)
    {
        if (image == null)
        {
            _textureSource = null;
        }
        else if (ReferenceEquals(image, _textureSource))
        {
            // 会话只是状态通知（例如"增强计算中"）：同一份产物已在纹理里，重绘即可。
            DrawFrame();
            return;
        }
        else
        {
            _textureSource = image;
        }
        _viewport.BeginImageChange();
        _renderer.SetTexture(image);
        if (image == null) _viewport.ClearTexture();
        else _viewport.ApplyImage(image.SourceWidth, image.SourceHeight, image.Orientation, _renderer.TextureWidth);
        DrawFrame();
        ViewportChanged?.Invoke();
    }

    /// <summary>适应视口，保持照片比例并在物理像素超采样代理上绘制。</summary>
    public void FitToView()
    {
        _viewport.FitToView();
        DrawFrame();
        ViewportChanged?.Invoke();
    }

    /// <summary>在适应和物理 100% 间切换；点击锚点在缩放时保留位置。</summary>
    public void ToggleFit(CGPoint? point = null)
    {
        _viewport.ToggleFit(point.HasValue ? ((double)point.Value.X, (double)point.Value.Y) : null);
        DrawFrame();
        ViewportChanged?.Invoke();
    }

    /// <summary>按物理倍率缩放并把显示朝向的归一化点移到视口中心（细节预览双击用）。</summary>
    /// <param name="u">归一化横坐标。</param>
    /// <param name="v">归一化纵坐标。</param>
    /// <param name="physicalScale">目标物理倍率（1 = 源像素对屏幕物理像素 1:1）。</param>
    public void FocusOnPoint(double u, double v, double physicalScale)
    {
        _viewport.FocusOnPoint(u, v, physicalScale);
        DrawFrame();
        ViewportChanged?.Invoke();
    }

    /// <summary>按比例放大或缩小，锚定指针或视口中心。</summary>
    public void ZoomBy(double factor, CGPoint? point = null)
    {
        _viewport.ZoomBy(factor, point.HasValue ? ((double)point.Value.X, (double)point.Value.Y) : null);
        DrawFrame();
        ViewportChanged?.Invoke();
    }

    /// <summary>跳到预设物理倍率，避免 Retina 下将 100% 解释为 2×2 屏幕像素。</summary>
    public void ZoomToPhysical(double scale)
    {
        _viewport.ZoomToPhysical(scale);
        DrawFrame();
        ViewportChanged?.Invoke();
    }

    /// <summary>更新表面物理尺寸和显示缩放；跨屏后由系统根据层色彩标签重新管理输出。</summary>
    public override void SetFrameSize(CGSize newSize)
    {
        base.SetFrameSize(newSize);
        double backingScale = BackingScale;
        SyncViewport();
        _renderer.ResizeDrawable((double)newSize.Width, (double)newSize.Height, backingScale);
        if (_viewport.IsFit) FitToView();
        else
        {
            _viewport.ClampPan();
            DrawFrame();
            ViewportChanged?.Invoke();
        }
    }

    /// <summary>连接窗口后同步 Retina 物理尺寸。</summary>
    public override void ViewDidMoveToWindow() { base.ViewDidMoveToWindow(); SetFrameSize(Frame.Size); }

    /// <summary>把视图当前 bounds 与物理倍率同步进共享视口；渲染与读回前都以此为准，尺寸未变化时为空操作。</summary>
    private void SyncViewport() => _viewport.ResizeView((double)Bounds.Width, (double)Bounds.Height, BackingScale);

    /// <summary>生成单帧照片；变换与 EXIF 方向都由顶点阶段处理，缩小时使用三线性 mipmap。</summary>
    public void DrawFrame()
    {
        if (Window == null || (double)Bounds.Width < 1 || (double)Bounds.Height < 1) return;
        SyncViewport();
        _renderer.DrawFrame();
    }

    /// <summary>读取真实 GPU 照片帧，供自动验收和截图导出；不作为屏幕合成器色彩对拍的替代。
    /// navigation=true 时输出 NavigationSize 比例的小纹理（按 Retina 倍率取整），照片铺满目标且方向与主图一致。</summary>
    /// <param name="navigation">是否输出导航图尺寸的等比小图。</param>
    /// <returns>线性 RGBA16F 帧。</returns>
    public DecodedImage CaptureLinearFrame(bool navigation = false)
    {
        SyncViewport();
        return _renderer.CaptureLinearFrame(navigation);
    }

    /// <summary>接收原生连续捏合事件，保持手势中心。</summary>
    public override void MagnifyWithEvent(NSEvent theEvent) => ZoomBy(1 + (double)theEvent.Magnification, ConvertPointFromView(theEvent.LocationInWindow, null));
    /// <summary>精细触控板滚动平移并接受惯性；离散滚轮交给用户绑定。</summary>
    public override void ScrollWheel(NSEvent theEvent)
    {
        if (theEvent.HasPreciseScrollingDeltas && theEvent.ModifierFlags != 0 && InputHandler?.Invoke(theEvent) == true) return;
        if (!theEvent.HasPreciseScrollingDeltas) { if (InputHandler?.Invoke(theEvent) != true) base.ScrollWheel(theEvent); return; }
        if (!_viewport.IsFit)
        {
            _viewport.PanBy((double)theEvent.ScrollingDeltaX, (double)theEvent.ScrollingDeltaY);
            DrawFrame();
            ViewportChanged?.Invoke();
        }
    }
    /// <summary>点击获焦，双击切换适应/100%，单击开始拖动。</summary>
    public override void MouseDown(NSEvent theEvent)
    {
        Window?.MakeFirstResponder(this);
        _lastDrag = ConvertPointFromView(theEvent.LocationInWindow, null);
        if (theEvent.ClickCount == 2) ToggleFit(_lastDrag);
        else InputHandler?.Invoke(theEvent);
    }
    /// <summary>放大后按原生鼠标位移平移照片。</summary>
    public override void MouseDragged(NSEvent theEvent)
    {
        var point = ConvertPointFromView(theEvent.LocationInWindow, null);
        if (!_viewport.IsFit)
        {
            _viewport.PanBy((double)(point.X - _lastDrag.X), (double)(point.Y - _lastDrag.Y));
            DrawFrame();
            ViewportChanged?.Invoke();
        }
        _lastDrag = point;
    }
    /// <summary>中键/侧键按照配置执行。</summary>
    public override void OtherMouseDown(NSEvent theEvent) { if (InputHandler?.Invoke(theEvent) != true) base.OtherMouseDown(theEvent); }
    /// <summary>右键优先匹配用户手势，否则显示原生上下文菜单。</summary>
    public override void RightMouseDown(NSEvent theEvent) { if (InputHandler?.Invoke(theEvent) != true) base.RightMouseDown(theEvent); }
    /// <summary>处理照片区域按键，未匹配事件交回系统。</summary>
    public override void KeyDown(NSEvent theEvent) { if (InputHandler?.Invoke(theEvent) != true) base.KeyDown(theEvent); }
    /// <summary>压力响应保留当前纹理，避免观片瞬间清屏；缓存由共享 Governor 修剪。</summary>
    public void Trim(PressureLevel level) { }
    /// <summary>关闭视图时释放 Metal 所有资源。</summary>
    protected override void Dispose(bool disposing)
    {
        if (disposing) { MemoryGovernor.Unregister(this); _renderer.Dispose(); }
        base.Dispose(disposing);
    }
}
