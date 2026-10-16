using AppKit;
using CoreAnimation;
using CoreGraphics;
using Foundation;
using Metal;
using PhotoViewer.Core.Color;
using PhotoViewer.Core.Image;
using PhotoViewer.Core.Memory;

namespace PhotoViewer.Mac.Native;

/// <summary>原生照片表面；线性 RGBA16F、三线性 mipmap、GPU 方向变换与原生视口状态。</summary>
public sealed class MetalPhotoView : NSView, IMemoryClient
{
    private readonly IMTLDevice _device;
    private readonly IMTLCommandQueue _queue;
    private readonly IMTLRenderPipelineState _pipeline;
    private readonly CAMetalLayer _metalLayer;
    private IMTLTexture? _texture;
    private int _sourceWidth;
    private int _sourceHeight;
    private int _orientation = 1;
    private double _scale = 1;
    private double _panX;
    private double _panY;
    private bool _fit = true;
    private CGPoint _lastDrag;
    private long _textureBytes;
    private double _lastBackingScale = 1;

    /// <summary>视口变化通知，加载会话据此决定代理尺寸与 Full 升级。</summary>
    public event Action? ViewportChanged;
    /// <summary>离散滚轮或按键等交由窗口按用户快捷键表匹配。</summary>
    public Func<NSEvent, bool>? InputHandler { get; set; }
    /// <summary>当前照片的归一化对焦点；键盘放大与对焦连续翻页复用。</summary>
    public CGPoint? FocusPoint { get; set; }
    private bool _followsFocus;
    /// <summary>内存客户端名称。</summary>
    public string Name => "MetalPhotoView";
    /// <summary>纹理及三重缓冲的估算 GPU 字节，避免只记托管解码缓冲。</summary>
    public long FootprintBytes => Interlocked.Read(ref _textureBytes) + (long)(Bounds.Width * Bounds.Height * BackingScale * BackingScale * 8 * 3);
    /// <summary>原生坐标使用从上到下的逻辑点。</summary>
    public override bool IsFlipped => true;
    /// <summary>照片区域可接收键盘与手势。</summary>
    public override bool AcceptsFirstResponder() => true;
    /// <summary>缩放是否处于适应状态。</summary>
    public bool IsFit => _fit;
    /// <summary>源像素对屏幕物理像素的倍率。</summary>
    public double PhysicalScale => _scale * BackingScale;
    /// <summary>当前全分辨率细节需求，包括代理覆盖不足的放大。</summary>
    public bool NeedsFull => !_fit && (PhysicalScale >= 1 || (_texture != null && PhysicalScale > (double)_texture.Width / Math.Max(1, _sourceWidth)));
    private double BackingScale => (double)(Window?.BackingScaleFactor ?? 1);
    private double ImageWidth => _orientation >= 5 ? _sourceHeight : _sourceWidth;
    private double ImageHeight => _orientation >= 5 ? _sourceWidth : _sourceHeight;

    /// <summary>显示朝向的逻辑图像宽（像素）。</summary>
    public double DisplayWidth => ImageWidth;
    /// <summary>显示朝向的逻辑图像高（像素）。</summary>
    public double DisplayHeight => ImageHeight;
    /// <summary>图像像素到视图点的当前缩放（点/像素）。</summary>
    public double PointScale => _scale;

    /// <summary>把显示朝向的归一化图像点换算成视图坐标（点）。</summary>
    /// <param name="u">归一化横坐标。</param>
    /// <param name="v">归一化纵坐标。</param>
    /// <returns>视图坐标系（顶边为原点）中的点。</returns>
    public CGPoint ViewPoint(double u, double v)
    {
        double width = ImageWidth * _scale, height = ImageHeight * _scale;
        return new CGPoint(
            (double)Bounds.Width / 2 - width / 2 + _panX + u * width,
            (double)Bounds.Height / 2 - height / 2 + _panY + v * height);
    }

    /// <summary>建立 Metal 队列、着色器和带正确线性色彩标签的照片层；不支持时显式失败。</summary>
    public MetalPhotoView(CGRect frame) : base(frame)
    {
        _device = MTLDevice.SystemDefault ?? throw new InvalidOperationException("Metal 设备不可用");
        _queue = _device.CreateCommandQueue() ?? throw new InvalidOperationException("Metal 命令队列不可用");
        using var library = _device.CreateLibrary(ShaderSource, new MTLCompileOptions(), out var libraryError)
            ?? throw new InvalidOperationException(libraryError?.LocalizedDescription ?? "Metal 着色器编译失败");
        using var descriptor = new MTLRenderPipelineDescriptor
        {
            VertexFunction = library.CreateFunction("photo_vertex"),
            FragmentFunction = library.CreateFunction("photo_fragment"),
        };
        descriptor.ColorAttachments[0].PixelFormat = MTLPixelFormat.RGBA16Float;
        _pipeline = _device.CreateRenderPipelineState(descriptor, out var pipelineError)
            ?? throw new InvalidOperationException(pipelineError?.LocalizedDescription ?? "Metal 管线创建失败");
        using var colorSpace = CGColorSpace.CreateWithName(CGColorSpaceNames.ExtendedLinearSrgb);
        _metalLayer = new CAMetalLayer
        {
            Device = _device,
            PixelFormat = MTLPixelFormat.RGBA16Float,
            ColorSpace = colorSpace,
            Opaque = true,
            FramebufferOnly = true,
        };
        WantsLayer = true;
        Layer = _metalLayer;
        MemoryGovernor.Register(this);
    }

    /// <summary>同步上传 pinned 像素并生成 mipmap；Metal 命令队列顺序保证绘制前生成完毕。</summary>
    public unsafe void SetImage(DecodedImage? image)
    {
        if (image != null && (image.Encoding != PixelEncoding.RgbaF16 || image.ColorSpace != ImageColorSpace.LinearSrgb))
            throw new InvalidOperationException("Metal 主图必须为线性 RGBA16F");
        var previous = _texture;
        double relativePanX = _panX / Math.Max(1, ImageWidth), relativePanY = _panY / Math.Max(1, ImageHeight);
        _texture = null;
        if (image != null)
        {
            _sourceWidth = image.SourceWidth;
            _sourceHeight = image.SourceHeight;
            _orientation = image.Orientation;
            using var descriptor = MTLTextureDescriptor.CreateTexture2DDescriptor(MTLPixelFormat.RGBA16Float, (nuint)image.Width, (nuint)image.Height, true);
            descriptor.StorageMode = MTLStorageMode.Shared;
            descriptor.Usage = MTLTextureUsage.ShaderRead;
            _texture = _device.CreateTexture(descriptor) ?? throw new InvalidOperationException("照片纹理分配失败");
            fixed (byte* pixels = image.Pixels)
                _texture.ReplaceRegion(MTLRegion.Create2D(0, 0, (nuint)image.Width, (nuint)image.Height), 0, (IntPtr)pixels, (nuint)image.Stride);
            using var command = _queue.CommandBuffer() ?? throw new InvalidOperationException("Metal 命令缓冲不可用");
            using var blit = command.BlitCommandEncoder ?? throw new InvalidOperationException("Metal mipmap 编码器不可用");
            blit.GenerateMipmapsForTexture(_texture);
            blit.EndEncoding();
            command.Commit();
            Interlocked.Exchange(ref _textureBytes, (long)image.Width * image.Height * 8 * 4 / 3);
            if (_fit) FitToView();
            else if (_followsFocus && FocusPoint.HasValue) CenterFocus();
            else { _panX = relativePanX * ImageWidth; _panY = relativePanY * ImageHeight; ClampPan(); }
        }
        else Interlocked.Exchange(ref _textureBytes, 0);
        previous?.Dispose();
        DrawFrame();
        ViewportChanged?.Invoke();
    }

    /// <summary>适应视口，保持照片比例并在物理像素超采样代理上绘制。</summary>
    public void FitToView()
    {
        _fit = true;
        _followsFocus = false;
        if (ImageWidth > 0 && ImageHeight > 0)
            _scale = Math.Min((double)Bounds.Width / ImageWidth, (double)Bounds.Height / ImageHeight);
        _panX = 0;
        _panY = 0;
        DrawFrame();
        ViewportChanged?.Invoke();
    }

    /// <summary>在适应和物理 100% 间切换；点击锚点在缩放时保留位置。</summary>
    public void ToggleFit(CGPoint? point = null)
    {
        if (!_fit) FitToView();
        else if (!point.HasValue && FocusPoint.HasValue)
        {
            _scale = 1 / BackingScale;
            _fit = false;
            _followsFocus = true;
            CenterFocus();
            DrawFrame();
            ViewportChanged?.Invoke();
        }
        else ZoomTo(1 / BackingScale, point ?? new CGPoint(Bounds.Width / 2, Bounds.Height / 2));
    }

    /// <summary>按物理倍率缩放并把显示朝向的归一化点移到视口中心（细节预览双击用）。</summary>
    /// <param name="u">归一化横坐标。</param>
    /// <param name="v">归一化纵坐标。</param>
    /// <param name="physicalScale">目标物理倍率（1 = 源像素对屏幕物理像素 1:1）。</param>
    public void FocusOnPoint(double u, double v, double physicalScale)
    {
        _scale = physicalScale / BackingScale;
        _fit = false;
        _followsFocus = false;
        _panX = (.5 - u) * ImageWidth * _scale;
        _panY = (.5 - v) * ImageHeight * _scale;
        ClampPan();
        DrawFrame();
        ViewportChanged?.Invoke();
    }

    /// <summary>以显示朝向的归一化对焦位置为视口中心，并约束边缘平移。</summary>
    private void CenterFocus()
    {
        if (FocusPoint is not { } focus) return;
        _panX = (.5 - (double)focus.X) * ImageWidth * _scale;
        _panY = (.5 - (double)focus.Y) * ImageHeight * _scale;
        ClampPan();
    }

    /// <summary>按比例放大或缩小，锚定指针或视口中心。</summary>
    public void ZoomBy(double factor, CGPoint? point = null)
        => ZoomTo(_scale * factor, point ?? new CGPoint(Bounds.Width / 2, Bounds.Height / 2));

    /// <summary>跳到预设物理倍率，避免 Retina 下将 100% 解释为 2×2 屏幕像素。</summary>
    public void ZoomToPhysical(double scale) => ZoomTo(scale / BackingScale, new CGPoint(Bounds.Width / 2, Bounds.Height / 2));

    /// <summary>按锚点计算缩放后的平移，限制缩放范围并上报展示需求。</summary>
    private void ZoomTo(double scale, CGPoint point)
    {
        scale = Math.Clamp(scale, .01 / BackingScale, 16 / BackingScale);
        double ratio = scale / Math.Max(.000001, _scale);
        _panX = (double)(point.X - Bounds.Width / 2) - ((double)(point.X - Bounds.Width / 2) - _panX) * ratio;
        _panY = (double)(point.Y - Bounds.Height / 2) - ((double)(point.Y - Bounds.Height / 2) - _panY) * ratio;
        _scale = scale;
        _fit = false;
        _followsFocus = false;
        ClampPan();
        DrawFrame();
        ViewportChanged?.Invoke();
    }

    /// <summary>使至少一部分照片保持在视口中；小于视口的轴居中。</summary>
    private void ClampPan()
    {
        double maxX = Math.Max(0, (ImageWidth * _scale - (double)Bounds.Width) / 2);
        double maxY = Math.Max(0, (ImageHeight * _scale - (double)Bounds.Height) / 2);
        _panX = Math.Clamp(_panX, -maxX, maxX);
        _panY = Math.Clamp(_panY, -maxY, maxY);
    }

    /// <summary>更新表面物理尺寸和显示缩放；跨屏后由系统根据层色彩标签重新管理输出。</summary>
    public override void SetFrameSize(CGSize newSize)
    {
        base.SetFrameSize(newSize);
        if (_metalLayer == null) return;
        if (!_fit) _scale *= _lastBackingScale / BackingScale;
        _lastBackingScale = BackingScale;
        _metalLayer.ContentsScale = (System.Runtime.InteropServices.NFloat)BackingScale;
        _metalLayer.DrawableSize = new CGSize(Math.Max(1, newSize.Width * BackingScale), Math.Max(1, newSize.Height * BackingScale));
        if (_fit) FitToView();
        else { ClampPan(); DrawFrame(); ViewportChanged?.Invoke(); }
    }

    /// <summary>连接窗口后同步 Retina 物理尺寸。</summary>
    public override void ViewDidMoveToWindow() { base.ViewDidMoveToWindow(); SetFrameSize(Frame.Size); }

    /// <summary>生成单帧照片；变换与 EXIF 方向都由顶点阶段处理，缩小时使用三线性 mipmap。</summary>
    public unsafe void DrawFrame()
    {
        if (_metalLayer == null || Bounds.Width < 1 || Bounds.Height < 1 || Window == null) return;
        using var drawable = _metalLayer.NextDrawable();
        if (drawable == null) return;
        using var command = _queue.CommandBuffer() ?? throw new InvalidOperationException("Metal 命令缓冲不可用");
        EncodeFrame(command, drawable.Texture);
        command.PresentDrawable(drawable);
        command.Commit();
    }

    /// <summary>绘制到任意 RGBA16F 目标，窗口呈现与验收读回复用同一着色器和变换。</summary>
    private unsafe void EncodeFrame(IMTLCommandBuffer command, IMTLTexture target, float[]? customTransform = null)
    {
        using var pass = MTLRenderPassDescriptor.CreateRenderPassDescriptor();
        pass.ColorAttachments[0].Texture = target;
        pass.ColorAttachments[0].LoadAction = MTLLoadAction.Clear;
        pass.ColorAttachments[0].StoreAction = MTLStoreAction.Store;
        pass.ColorAttachments[0].ClearColor = new MTLClearColor(0, 0, 0, 1);
        using var encoder = command.CreateRenderCommandEncoder(pass) ?? throw new InvalidOperationException("Metal 渲染编码器不可用");
        if (_texture != null)
        {
            encoder.SetRenderPipelineState(_pipeline);
            float[] transform = customTransform ?? [(float)Bounds.Width, (float)Bounds.Height, (float)ImageWidth, (float)ImageHeight,
                (float)_scale, (float)_panX, (float)_panY, _orientation];
            fixed (float* values = transform) encoder.SetVertexBytes((IntPtr)values, (nuint)(transform.Length * sizeof(float)), 0);
            encoder.SetFragmentTexture(_texture, 0);
            encoder.DrawPrimitives(MTLPrimitiveType.TriangleStrip, 0, 4);
        }
        encoder.EndEncoding();
    }

    /// <summary>导航图长边逻辑点，与原 Avalonia 缩放指示器一致。</summary>
    private const double NavigationLongEdge = 150;

    /// <summary>导航图逻辑点尺寸：长边 150、按显示朝向照片比例缩小，不补方形衬边；无照片时为 150 方形占位。</summary>
    public CGSize NavigationSize
    {
        get
        {
            double imageWidth = ImageWidth, imageHeight = ImageHeight;
            if (imageWidth <= 0 || imageHeight <= 0) return new CGSize(NavigationLongEdge, NavigationLongEdge);
            double factor = NavigationLongEdge / Math.Max(imageWidth, imageHeight);
            return new CGSize(imageWidth * factor, imageHeight * factor);
        }
    }

    /// <summary>读取真实 GPU 照片帧，供自动验收和截图导出；不作为屏幕合成器色彩对拍的替代。
    /// navigation=true 时输出 NavigationSize 比例的小纹理（按 Retina 倍率取整），照片铺满目标且方向与主图一致。</summary>
    public unsafe DecodedImage CaptureLinearFrame(bool navigation = false)
    {
        int width, height;
        float[]? transform = null;
        if (navigation)
        {
            var navigationSize = NavigationSize;
            double navigationWidth = (double)navigationSize.Width, navigationHeight = (double)navigationSize.Height;
            width = Math.Max(1, (int)Math.Round(navigationWidth * BackingScale));
            height = Math.Max(1, (int)Math.Round(navigationHeight * BackingScale));
            double fit = Math.Min(navigationWidth / Math.Max(1, ImageWidth), navigationHeight / Math.Max(1, ImageHeight));
            transform = [(float)navigationWidth, (float)navigationHeight, (float)ImageWidth, (float)ImageHeight, (float)fit, 0, 0, _orientation];
        }
        else
        {
            width = Math.Max(1, (int)Math.Round((double)Bounds.Width * BackingScale));
            height = Math.Max(1, (int)Math.Round((double)Bounds.Height * BackingScale));
        }
        using var descriptor = MTLTextureDescriptor.CreateTexture2DDescriptor(MTLPixelFormat.RGBA16Float, (nuint)width, (nuint)height, false);
        descriptor.StorageMode = MTLStorageMode.Shared; descriptor.Usage = MTLTextureUsage.RenderTarget;
        using var texture = _device.CreateTexture(descriptor) ?? throw new InvalidOperationException("GPU 验收目标分配失败");
        using var command = _queue.CommandBuffer() ?? throw new InvalidOperationException("GPU 验收命令分配失败");
        EncodeFrame(command, texture, transform); command.Commit(); command.WaitUntilCompleted();
        if (command.Error != null) throw new InvalidOperationException(command.Error.LocalizedDescription);
        int stride = checked(width * 8);
        var buffer = DecodedImage.RentPixels(checked(stride * height));
        fixed (byte* pixels = buffer) texture.GetBytes((IntPtr)pixels, (nuint)stride, MTLRegion.Create2D(0, 0, (nuint)width, (nuint)height), 0);
        return DecodedImage.CreatePooled(buffer, width, height, stride, PixelEncoding.RgbaF16, ImageColorSpace.LinearSrgb);
    }

    /// <summary>导航图（NavigationSize 逻辑点坐标、无方形衬边）内的可见区域；缩放小于适应时退化为整幅照片。</summary>
    public CGRect NavigationViewport
    {
        get
        {
            var navigationSize = NavigationSize;
            double navigationWidth = (double)navigationSize.Width, navigationHeight = (double)navigationSize.Height;
            double displayedWidth = Math.Max(1, ImageWidth * _scale), displayedHeight = Math.Max(1, ImageHeight * _scale);
            var horizontal = NavigationAxis(navigationWidth, (double)Bounds.Width / displayedWidth, .5 - _panX / displayedWidth);
            var vertical = NavigationAxis(navigationHeight, (double)Bounds.Height / displayedHeight, .5 - _panY / displayedHeight);
            return new CGRect(horizontal.Offset, vertical.Offset, horizontal.Extent, vertical.Extent);
        }
    }

    /// <summary>把一条轴上的可见比例与中心比例换算成导航图内的偏移和长度，并钳制在导航图范围内。</summary>
    private static (double Offset, double Extent) NavigationAxis(double navigationExtent, double visibleFraction, double centerFraction)
    {
        double extent = Math.Clamp(Math.Clamp(visibleFraction, 0, 1) * navigationExtent, Math.Min(1, navigationExtent), navigationExtent);
        double offset = centerFraction * navigationExtent - extent / 2;
        return (Math.Clamp(offset, 0, navigationExtent - extent), extent);
    }

    /// <summary>接收原生连续捏合事件，保持手势中心。</summary>
    public override void MagnifyWithEvent(NSEvent theEvent) => ZoomBy(1 + (double)theEvent.Magnification, ConvertPointFromView(theEvent.LocationInWindow, null));
    /// <summary>精细触控板滚动平移并接受惯性；离散滚轮交给用户绑定。</summary>
    public override void ScrollWheel(NSEvent theEvent)
    {
        if (theEvent.HasPreciseScrollingDeltas && theEvent.ModifierFlags != 0 && InputHandler?.Invoke(theEvent) == true) return;
        if (!theEvent.HasPreciseScrollingDeltas) { if (InputHandler?.Invoke(theEvent) != true) base.ScrollWheel(theEvent); return; }
        if (!_fit)
        {
            _followsFocus = false;
            _panX += (double)theEvent.ScrollingDeltaX;
            _panY += (double)theEvent.ScrollingDeltaY;
            ClampPan(); DrawFrame(); ViewportChanged?.Invoke();
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
        if (!_fit) { _followsFocus = false; _panX += (double)(point.X - _lastDrag.X); _panY += (double)(point.Y - _lastDrag.Y); ClampPan(); DrawFrame(); ViewportChanged?.Invoke(); }
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
        if (disposing) { MemoryGovernor.Unregister(this); _texture?.Dispose(); _pipeline.Dispose(); _queue.Dispose(); _metalLayer.Dispose(); Interlocked.Exchange(ref _textureBytes, 0); }
        base.Dispose(disposing);
    }

    private const string ShaderSource = """
        #include <metal_stdlib>
        using namespace metal;
        struct PhotoVertex { float4 position [[position]]; float2 uv; };
        vertex PhotoVertex photo_vertex(uint index [[vertex_id]], constant float* data [[buffer(0)]]) {
            const float2 points[4] = {float2(-.5,-.5),float2(.5,-.5),float2(-.5,.5),float2(.5,.5)};
            float2 point = points[index];
            float2 position = point * float2(data[2],data[3]) * data[4] + float2(data[5],data[6]);
            PhotoVertex output;
            output.position = float4(position.x * 2/data[0], -position.y * 2/data[1], 0, 1);
            float2 uv = point + .5;
            switch (int(data[7])) {
                case 2: uv = float2(1-uv.x,uv.y); break;
                case 3: uv = 1-uv; break;
                case 4: uv = float2(uv.x,1-uv.y); break;
                case 5: uv = uv.yx; break;
                case 6: uv = float2(uv.y,1-uv.x); break;
                case 7: uv = float2(1-uv.y,1-uv.x); break;
                case 8: uv = float2(1-uv.y,uv.x); break;
            }
            output.uv = uv;
            return output;
        }
        fragment float4 photo_fragment(PhotoVertex input [[stage_in]], texture2d<half> image [[texture(0)]]) {
            constexpr sampler sampleFilter(coord::normalized, address::clamp_to_edge, filter::linear, mip_filter::linear);
            return float4(image.sample(sampleFilter,input.uv));
        }
        """;
}
