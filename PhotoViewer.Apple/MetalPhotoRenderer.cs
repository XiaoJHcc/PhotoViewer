using CoreAnimation;
using CoreGraphics;
using Foundation;
using Metal;
using PhotoViewer.Core.Color;
using PhotoViewer.Core.Image;
using PhotoViewer.Core.Memory;

namespace PhotoViewer.Apple;

/// <summary>
/// Metal 照片渲染核心：RGBA16F 扩展线性 sRGB 目标、双线性 mipmap、GPU EXIF 方向变换与线性帧读回。
/// 接受两种主图纹理：RGBA16F 线性 scRGB（代理档）与 RGBA8 sRGB 传递曲线的 Display P3（Full 档/压力降级），
/// 后者经片段着色器 3×3 矩阵换算到线性 scRGB，故热替换不产生可见色偏（P3 立方体外的原色除外，见解码器注释）。
/// 只依赖 Metal/CoreAnimation/CoreGraphics，宿主视图负责把它挂到自身图层并转发窗口事件。
///
/// <para>当前纹理之外缓存最近两份已上传纹理（只持产物弱引用，不持 CPU 租约）。
/// 所有真正使用 Metal 的入口（SetTexture / DrawFrame / CaptureLinearFrame / ResizeDrawable / Dispose）
/// 在同一个 gate 内串行执行；探测线程的 Trim 只置待处理标记，实际修剪由主线程 ApplyPendingTrim 排空。</para>
/// </summary>
public sealed class MetalPhotoRenderer : IDisposable
{
    /// <summary>线性 Display P3 → 扩展线性 sRGB（D65，行主序）；着色器 uniform 与验收核对共用同一常量。</summary>
    internal static readonly float[] P3ToExtendedLinearSrgb =
    [
        1.2249401f, -0.2249404f, 0.0000000f,
        -0.0420569f, 1.0420571f, 0.0000000f,
        -0.0196376f, -0.0786361f, 1.0982735f,
    ];

    private const int RecentCapacity = 2;
    private const long RecentByteCeiling = 256L << 20;

    private readonly object _gate = new();
    private readonly List<CachedTexture> _recent = new(RecentCapacity);
    private readonly long _recentByteLimit;
    private readonly IMTLDevice _device;
    private readonly IMTLCommandQueue _queue;
    private readonly IMTLRenderPipelineState _linearPipeline;
    private readonly IMTLRenderPipelineState _displayP3Pipeline;
    private readonly PhotoViewport _viewport;
    private CachedTexture? _current;
    private long _textureBytes;
    private long _uploads;
    private long _cacheHits;
    private int _pendingTrim;
    private bool _disposed;

    /// <summary>照片层（RGBA16F 扩展线性 sRGB、不保留绘制内容），宿主把它作为视图图层。</summary>
    public CAMetalLayer Layer { get; }
    /// <summary>当前纹理宽度；0 表示没有照片（清空时最近纹理可能仍在缓存里）。</summary>
    public int TextureWidth { get; private set; }
    /// <summary>当前纹理与最近缓存纹理的估算字节，含 mipmap 链（每张按 4/3 基图计）。</summary>
    public long TextureBytes => Interlocked.Read(ref _textureBytes);
    /// <summary>累计真实上传次数；缓存命中不计入。</summary>
    internal long UploadCount => Interlocked.Read(ref _uploads);
    /// <summary>累计缓存命中次数（同一产物实例不再重复上传）。</summary>
    internal long CacheHitCount => Interlocked.Read(ref _cacheHits);
    /// <summary>最近缓存中的纹理份数（不含当前纹理）。</summary>
    internal int CachedTextureCount
    {
        get { lock (_gate) return _recent.Count; }
    }

    /// <summary>建立 Metal 队列、着色器和带正确线性色彩标签的照片层；不支持时显式失败。</summary>
    public MetalPhotoRenderer(PhotoViewport viewport)
    {
        _viewport = viewport;
        _device = MTLDevice.SystemDefault ?? throw new InvalidOperationException("Metal 设备不可用");
        _queue = _device.CreateCommandQueue() ?? throw new InvalidOperationException("Metal 命令队列不可用");
        using var library = _device.CreateLibrary(ShaderSource, new MTLCompileOptions(), out var libraryError)
            ?? throw new InvalidOperationException(libraryError?.LocalizedDescription ?? "Metal 着色器编译失败");
        _linearPipeline = CreatePipeline(library, "photo_fragment");
        _displayP3Pipeline = CreatePipeline(library, "photo_fragment_p3");
        using var colorSpace = CGColorSpace.CreateWithName(CGColorSpaceNames.ExtendedLinearSrgb);
        Layer = new CAMetalLayer
        {
            Device = _device,
            PixelFormat = MTLPixelFormat.RGBA16Float,
            ColorSpace = colorSpace,
            Opaque = true,
            FramebufferOnly = true,
        };
    }

    /// <summary>验收缝：把最近缓存的字节上限压到给定值（仍取与预算推导值的较小者），产品路径只用单参构造。</summary>
    internal MetalPhotoRenderer(PhotoViewport viewport, long recentByteLimit) : this(viewport)
        => _recentByteLimit = recentByteLimit;

    private IMTLRenderPipelineState CreatePipeline(IMTLLibrary library, string fragment)
    {
        using var descriptor = new MTLRenderPipelineDescriptor
        {
            VertexFunction = library.CreateFunction("photo_vertex"),
            FragmentFunction = library.CreateFunction(fragment),
        };
        descriptor.ColorAttachments[0].PixelFormat = MTLPixelFormat.RGBA16Float;
        return _device.CreateRenderPipelineState(descriptor, out var pipelineError)
            ?? throw new InvalidOperationException(pipelineError?.LocalizedDescription ?? "Metal 管线创建失败");
    }

    private static (MTLPixelFormat Format, bool DisplayP3) TextureFormat(DecodedImage image)
        => (image.Encoding, image.ColorSpace) switch
        {
            (PixelEncoding.RgbaF16, ImageColorSpace.LinearSrgb) => (MTLPixelFormat.RGBA16Float, false),
            // sRGB 传递曲线纹理：采样即得线性 P3，再由片段着色器换算到扩展线性 sRGB。
            (PixelEncoding.Rgba8, ImageColorSpace.DisplayP3) => (MTLPixelFormat.RGBA8Unorm_sRGB, true),
            _ => throw new NotSupportedException($"Metal 主图不支持的展示编码 {image.Encoding}/{image.ColorSpace}"),
        };

    /// <summary>同步上传并置换当前纹理：新纹理完全成功后才替换，失败保留旧图；null 清空为黑屏，但把上一张留在最近缓存。</summary>
    public unsafe void SetTexture(DecodedImage? image)
    {
        var (retain, cap) = Retention();
        lock (_gate)
        {
            if (_disposed)
            {
                if (image == null) return;
                throw new ObjectDisposedException(nameof(MetalPhotoRenderer));
            }
            DrainPendingTrimLocked();
            if (image == null)
            {
                SwapCurrentLocked(null, retain, cap);
                return;
            }
            var found = FindLocked(image);
            if (found != null)
            {
                Interlocked.Increment(ref _cacheHits);
                if (!ReferenceEquals(found, _current))
                {
                    _recent.Remove(found);
                    SwapCurrentLocked(found, retain, cap);
                }
                DrainPendingTrimLocked();
                return;
            }
            var (format, displayP3) = TextureFormat(image);
            var created = Upload(image, format);
            Interlocked.Increment(ref _uploads);
            SwapCurrentLocked(new CachedTexture(created, image, displayP3, TextureByteCount(image.Width, image.Height, image.Encoding)), retain, cap);
            DrainPendingTrimLocked();
        }
    }

    private unsafe IMTLTexture Upload(DecodedImage image, MTLPixelFormat format)
    {
        if (image.Pixels.Length == 0) throw new InvalidOperationException("展示产物像素已释放，不能上传纹理");
        IMTLTexture? created = null;
        try
        {
            using var descriptor = MTLTextureDescriptor.CreateTexture2DDescriptor(format, (nuint)image.Width, (nuint)image.Height, true);
            descriptor.StorageMode = MTLStorageMode.Shared;
            descriptor.Usage = MTLTextureUsage.ShaderRead;
            created = _device.CreateTexture(descriptor) ?? throw new InvalidOperationException("照片纹理分配失败");
            fixed (byte* pixels = image.Pixels)
                created.ReplaceRegion(MTLRegion.Create2D(0, 0, (nuint)image.Width, (nuint)image.Height), 0, (IntPtr)pixels, (nuint)image.Stride);
            using var command = _queue.CommandBuffer() ?? throw new InvalidOperationException("Metal 命令缓冲不可用");
            using var blit = command.BlitCommandEncoder ?? throw new InvalidOperationException("Metal mipmap 编码器不可用");
            blit.GenerateMipmapsForTexture(created);
            blit.EndEncoding();
            command.Commit();
            return created;
        }
        catch
        {
            created?.Dispose();
            throw;
        }
    }

    private static long TextureByteCount(int width, int height, PixelEncoding encoding)
        => (long)width * height * DecodedImage.BytesPerPixel(encoding) * 4 / 3;

    private CachedTexture? FindLocked(DecodedImage image)
    {
        if (_current != null && Matches(_current, image)) return _current;
        for (int i = 0; i < _recent.Count; i++)
            if (Matches(_recent[i], image)) return _recent[i];
        return null;
    }

    private static bool Matches(CachedTexture entry, DecodedImage image)
        => image.Pixels.Length > 0
            && entry.Width == image.Width && entry.Height == image.Height
            && entry.Encoding == image.Encoding && entry.ColorSpace == image.ColorSpace
            && entry.Source.TryGetTarget(out var target) && ReferenceEquals(target, image);

    // 保留策略与预算在进入 gate 之前取值：绝不在持锁时回调 MemoryGovernor，避免与探测线程倒锁。
    private (bool Retain, long Cap) Retention()
    {
        if (MemoryGovernor.CurrentLevel != PressureLevel.Normal || MemoryGovernor.InRecoveryHold) return (false, 0);
        long budget = MemoryGovernor.BudgetBytes;
        long cap = budget > 0 ? Math.Min(RecentByteCeiling, budget / 16) : RecentByteCeiling;
        return (true, _recentByteLimit > 0 ? Math.Min(cap, _recentByteLimit) : cap);
    }

    private void SwapCurrentLocked(CachedTexture? next, bool retain, long cap)
    {
        var previous = _current;
        _current = next;
        TextureWidth = next?.Width ?? 0;
        if (previous != null) PushRecentLocked(previous, retain, cap);
        UpdateBytesLocked();
    }

    private void PushRecentLocked(CachedTexture entry, bool retain, long cap)
    {
        PruneDeadRecentLocked();
        if (!retain)
        {
            DropRecentLocked();
            entry.Texture.Dispose();
            return;
        }
        if (!entry.Source.TryGetTarget(out _))
        {
            entry.Texture.Dispose();
            return;
        }
        long total = entry.Bytes;
        foreach (var item in _recent) total += item.Bytes;
        while (_recent.Count > 0 && (_recent.Count >= RecentCapacity || total > cap))
        {
            var oldest = _recent[^1];
            _recent.RemoveAt(_recent.Count - 1);
            total -= oldest.Bytes;
            oldest.Texture.Dispose();
        }
        if (_recent.Count >= RecentCapacity || total > cap)
        {
            entry.Texture.Dispose();
            return;
        }
        _recent.Insert(0, entry);
    }

    private void PruneDeadRecentLocked()
    {
        for (int i = _recent.Count - 1; i >= 0; i--)
        {
            if (_recent[i].Source.TryGetTarget(out _)) continue;
            _recent[i].Texture.Dispose();
            _recent.RemoveAt(i);
        }
    }

    private void DropRecentLocked()
    {
        foreach (var item in _recent) item.Texture.Dispose();
        _recent.Clear();
    }

    private void DrainPendingTrimLocked()
    {
        if (Volatile.Read(ref _pendingTrim) == 0) return;
        Interlocked.Exchange(ref _pendingTrim, 0);
        DropRecentLocked();
        UpdateBytesLocked();
    }

    private void UpdateBytesLocked()
    {
        long bytes = _current?.Bytes ?? 0;
        foreach (var item in _recent) bytes += item.Bytes;
        Interlocked.Exchange(ref _textureBytes, bytes);
    }

    /// <summary>压力入口：只置待处理标记，不取锁、不释放 GPU 资源（可能在 Governor 探测线程上调用）。</summary>
    internal void RequestTrim(PressureLevel level)
    {
        if (level <= PressureLevel.Normal) return;
        Interlocked.Exchange(ref _pendingTrim, 1);
    }

    /// <summary>排空待处理修剪；由宿主派到主线程执行，当前纹理始终保留。</summary>
    internal void ApplyPendingTrim()
    {
        lock (_gate)
        {
            if (_disposed) return;
            DrainPendingTrimLocked();
        }
    }

    /// <summary>更新图层物理尺寸与显示缩放；跨屏后由系统根据层色彩标签重新管理输出。</summary>
    public void ResizeDrawable(double width, double height, double backingScale)
    {
        lock (_gate)
        {
            if (_disposed) return;
            Layer.ContentsScale = (System.Runtime.InteropServices.NFloat)backingScale;
            Layer.DrawableSize = new CGSize(Math.Max(1, width * backingScale), Math.Max(1, height * backingScale));
        }
    }

    /// <summary>生成单帧照片并呈现；变换与 EXIF 方向都由顶点阶段处理，缩小时使用三线性 mipmap。</summary>
    public void DrawFrame()
    {
        lock (_gate)
        {
            if (_disposed) return;
            DrainPendingTrimLocked();
            using var drawable = Layer.NextDrawable();
            if (drawable == null) return;
            using var command = _queue.CommandBuffer() ?? throw new InvalidOperationException("Metal 命令缓冲不可用");
            EncodeFrame(command, drawable.Texture, null);
            command.PresentDrawable(drawable);
            command.Commit();
        }
    }

    /// <summary>读取真实 GPU 照片帧，供自动验收和截图导出；不作为屏幕合成器色彩对拍的替代。
    /// navigation=true 时输出 NavigationSize 比例的小纹理（按 Retina 倍率取整），照片铺满目标且方向与主图一致。</summary>
    public unsafe DecodedImage CaptureLinearFrame(bool navigation)
    {
        lock (_gate)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(MetalPhotoRenderer));
            DrainPendingTrimLocked();
            int width, height;
            float[]? transform = null;
            if (navigation)
            {
                var (navigationWidth, navigationHeight) = _viewport.NavigationSize;
                width = Math.Max(1, (int)Math.Round(navigationWidth * _viewport.BackingScale));
                height = Math.Max(1, (int)Math.Round(navigationHeight * _viewport.BackingScale));
                double fit = Math.Min(navigationWidth / Math.Max(1, _viewport.ImageWidth), navigationHeight / Math.Max(1, _viewport.ImageHeight));
                transform = [(float)navigationWidth, (float)navigationHeight, (float)_viewport.ImageWidth, (float)_viewport.ImageHeight,
                    (float)fit, 0, 0, _viewport.Orientation];
            }
            else
            {
                width = Math.Max(1, (int)Math.Round(_viewport.ViewWidth * _viewport.BackingScale));
                height = Math.Max(1, (int)Math.Round(_viewport.ViewHeight * _viewport.BackingScale));
            }
            using var descriptor = MTLTextureDescriptor.CreateTexture2DDescriptor(MTLPixelFormat.RGBA16Float, (nuint)width, (nuint)height, false);
            descriptor.StorageMode = MTLStorageMode.Shared; descriptor.Usage = MTLTextureUsage.RenderTarget;
            using var target = _device.CreateTexture(descriptor) ?? throw new InvalidOperationException("GPU 验收目标分配失败");
            using var command = _queue.CommandBuffer() ?? throw new InvalidOperationException("GPU 验收命令分配失败");
            EncodeFrame(command, target, transform); command.Commit(); command.WaitUntilCompleted();
            if (command.Error != null) throw new InvalidOperationException(command.Error.LocalizedDescription);
            int stride = checked(width * 8);
            var buffer = DecodedImage.RentPixels(checked(stride * height));
            fixed (byte* pixels = buffer) target.GetBytes((IntPtr)pixels, (nuint)stride, MTLRegion.Create2D(0, 0, (nuint)width, (nuint)height), 0);
            return DecodedImage.CreatePooled(buffer, width, height, stride, PixelEncoding.RgbaF16, ImageColorSpace.LinearSrgb);
        }
    }

    // 绘制到任意 RGBA16F 目标；调用方持有 gate，当前纹理不会在此期间被替换或释放。
    private unsafe void EncodeFrame(IMTLCommandBuffer command, IMTLTexture target, float[]? customTransform)
    {
        using var pass = MTLRenderPassDescriptor.CreateRenderPassDescriptor();
        pass.ColorAttachments[0].Texture = target;
        pass.ColorAttachments[0].LoadAction = MTLLoadAction.Clear;
        pass.ColorAttachments[0].StoreAction = MTLStoreAction.Store;
        pass.ColorAttachments[0].ClearColor = new MTLClearColor(0, 0, 0, 1);
        using var encoder = command.CreateRenderCommandEncoder(pass) ?? throw new InvalidOperationException("Metal 渲染编码器不可用");
        if (_current != null)
        {
            encoder.SetRenderPipelineState(_current.DisplayP3 ? _displayP3Pipeline : _linearPipeline);
            float[] transform = customTransform ?? [(float)_viewport.ViewWidth, (float)_viewport.ViewHeight,
                (float)_viewport.ImageWidth, (float)_viewport.ImageHeight, (float)_viewport.Scale,
                (float)_viewport.PanX, (float)_viewport.PanY, _viewport.Orientation];
            fixed (float* values = transform) encoder.SetVertexBytes((IntPtr)values, (nuint)(transform.Length * sizeof(float)), 0);
            encoder.SetFragmentTexture(_current.Texture, 0);
            if (_current.DisplayP3)
                fixed (float* matrix = P3ToExtendedLinearSrgb)
                    encoder.SetFragmentBytes((IntPtr)matrix, (nuint)(P3ToExtendedLinearSrgb.Length * sizeof(float)), 0);
            encoder.DrawPrimitives(MTLPrimitiveType.TriangleStrip, 0, 4);
        }
        encoder.EndEncoding();
    }

    /// <summary>释放当前与最近缓存纹理、管线、队列与照片层；可重复调用。</summary>
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _current?.Texture.Dispose();
            DropRecentLocked();
            _current = null;
            TextureWidth = 0;
            Interlocked.Exchange(ref _textureBytes, 0);
            Interlocked.Exchange(ref _pendingTrim, 0);
            _linearPipeline.Dispose();
            _displayP3Pipeline.Dispose();
            _queue.Dispose();
            Layer.Dispose();
        }
    }

    // 已上传纹理：只留产物弱引用与上传时抄录的不可变元数据，命中时另要求产物像素仍有效。
    private sealed class CachedTexture
    {
        public CachedTexture(IMTLTexture texture, DecodedImage source, bool displayP3, long bytes)
        {
            Texture = texture;
            DisplayP3 = displayP3;
            Bytes = bytes;
            Width = source.Width;
            Height = source.Height;
            Encoding = source.Encoding;
            ColorSpace = source.ColorSpace;
            Source = new WeakReference<DecodedImage>(source);
        }

        public IMTLTexture Texture { get; }
        public WeakReference<DecodedImage> Source { get; }
        public int Width { get; }
        public int Height { get; }
        public PixelEncoding Encoding { get; }
        public ImageColorSpace ColorSpace { get; }
        public bool DisplayP3 { get; }
        public long Bytes { get; }
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
        // 统一采样器与基础采样：可见区域、缩小时的三线性 mipmap 口径两种编码完全一致。
        constexpr sampler photo_sampler(coord::normalized, address::clamp_to_edge, filter::linear, mip_filter::linear);
        // RGBA16F 线性 scRGB 纹理：采样值即扩展线性 sRGB，直接输出。
        fragment float4 photo_fragment(PhotoVertex input [[stage_in]], texture2d<half> image [[texture(0)]]) {
            return float4(image.sample(photo_sampler,input.uv));
        }
        // RGBA8 sRGB 纹理（Display P3 原色）：采样得到线性 P3，再经 3×3 矩阵（片段 uniform，行主序）换算到扩展线性 sRGB。
        // 矩阵唯一来源是 CPU 侧 MetalPhotoRenderer.P3ToExtendedLinearSrgb，验收按同一常量核对。
        fragment float4 photo_fragment_p3(PhotoVertex input [[stage_in]], texture2d<half> image [[texture(0)]], constant float* matrix [[buffer(0)]]) {
            float3x3 p3_to_srgb = float3x3(matrix[0], matrix[3], matrix[6],
                                           matrix[1], matrix[4], matrix[7],
                                           matrix[2], matrix[5], matrix[8]);
            half4 sampled = image.sample(photo_sampler,input.uv);
            return float4(p3_to_srgb * float3(sampled.rgb), sampled.a);
        }
        """;
}
