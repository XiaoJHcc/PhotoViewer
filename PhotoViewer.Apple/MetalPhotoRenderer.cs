using CoreAnimation;
using CoreGraphics;
using Foundation;
using Metal;
using PhotoViewer.Core.Color;
using PhotoViewer.Core.Image;

namespace PhotoViewer.Apple;

/// <summary>
/// Metal 照片渲染核心：RGBA16F 扩展线性 sRGB 目标、双线性 mipmap、GPU EXIF 方向变换与线性帧读回。
/// 接受两种主图纹理：RGBA16F 线性 scRGB（代理档）与 RGBA8 sRGB 传递曲线的 Display P3（Full 档/压力降级），
/// 后者经片段着色器 3×3 矩阵换算到线性 scRGB，故热替换不产生可见色偏（P3 立方体外的原色除外，见解码器注释）。
/// 只依赖 Metal/CoreAnimation/CoreGraphics，宿主视图负责把它挂到自身图层并转发窗口事件。
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

    private readonly IMTLDevice _device;
    private readonly IMTLCommandQueue _queue;
    private readonly IMTLRenderPipelineState _linearPipeline;
    private readonly IMTLRenderPipelineState _displayP3Pipeline;
    private readonly PhotoViewport _viewport;
    private IMTLTexture? _texture;
    private bool _textureIsDisplayP3;
    private long _textureBytes;

    /// <summary>照片层（RGBA16F 扩展线性 sRGB、不保留绘制内容），宿主把它作为视图图层。</summary>
    public CAMetalLayer Layer { get; }
    /// <summary>当前纹理宽度；0 表示没有照片。</summary>
    public int TextureWidth { get; private set; }
    /// <summary>纹理及 mipmap 链的估算字节。</summary>
    public long TextureBytes => Interlocked.Read(ref _textureBytes);

    /// <summary>建立 Metal 队列、着色器和带正确线性色彩标签的照片层；不支持时显式失败。</summary>
    /// <param name="viewport">视口状态；渲染变换与导航帧读回都从这里取值。</param>
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

    /// <summary>建立一条渲染管线；两个纹理编码共用顶点阶段与同一 RGBA16F 目标格式。</summary>
    /// <param name="library">已编译的着色器库。</param>
    /// <param name="fragment">片段函数名。</param>
    /// <returns>可用的管线状态。</returns>
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

    /// <summary>按展示编码选择 Metal 纹理格式；不支持的编码显式抛出，不做隐式降级。</summary>
    /// <param name="image">展示产物。</param>
    /// <returns>纹理像素格式与该纹理是否需要 P3→scRGB 换算。</returns>
    private static (MTLPixelFormat Format, bool DisplayP3) TextureFormat(DecodedImage image)
        => (image.Encoding, image.ColorSpace) switch
        {
            (PixelEncoding.RgbaF16, ImageColorSpace.LinearSrgb) => (MTLPixelFormat.RGBA16Float, false),
            // sRGB 传递曲线纹理：采样即得线性 P3，再由片段着色器换算到扩展线性 sRGB。
            (PixelEncoding.Rgba8, ImageColorSpace.DisplayP3) => (MTLPixelFormat.RGBA8Unorm_sRGB, true),
            _ => throw new NotSupportedException($"Metal 主图不支持的展示编码 {image.Encoding}/{image.ColorSpace}"),
        };

    /// <summary>同步上传 pinned 像素并生成 mipmap；Metal 命令队列顺序保证绘制前生成完毕。</summary>
    /// <param name="image">展示主图（F16 线性 scRGB 或 8 位 Display P3）；null 表示移除当前照片。</param>
    public unsafe void SetTexture(DecodedImage? image)
    {
        var previous = _texture;
        _texture = null;
        TextureWidth = 0;
        if (image == null)
        {
            Interlocked.Exchange(ref _textureBytes, 0);
            previous?.Dispose();
            return;
        }
        var (format, displayP3) = TextureFormat(image);
        using var descriptor = MTLTextureDescriptor.CreateTexture2DDescriptor(format, (nuint)image.Width, (nuint)image.Height, true);
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
        _textureIsDisplayP3 = displayP3;
        TextureWidth = image.Width;
        Interlocked.Exchange(ref _textureBytes, (long)image.Width * image.Height * DecodedImage.BytesPerPixel(image.Encoding) * 4 / 3);
        previous?.Dispose();
    }

    /// <summary>更新图层物理尺寸与显示缩放；跨屏后由系统根据层色彩标签重新管理输出。</summary>
    /// <param name="width">视图宽度（逻辑点）。</param>
    /// <param name="height">视图高度（逻辑点）。</param>
    /// <param name="backingScale">视图物理倍率。</param>
    public void ResizeDrawable(double width, double height, double backingScale)
    {
        Layer.ContentsScale = (System.Runtime.InteropServices.NFloat)backingScale;
        Layer.DrawableSize = new CGSize(Math.Max(1, width * backingScale), Math.Max(1, height * backingScale));
    }

    /// <summary>生成单帧照片并呈现；变换与 EXIF 方向都由顶点阶段处理，缩小时使用三线性 mipmap。</summary>
    public void DrawFrame()
    {
        using var drawable = Layer.NextDrawable();
        if (drawable == null) return;
        using var command = _queue.CommandBuffer() ?? throw new InvalidOperationException("Metal 命令缓冲不可用");
        EncodeFrame(command, drawable.Texture);
        command.PresentDrawable(drawable);
        command.Commit();
    }

    /// <summary>读取真实 GPU 照片帧，供自动验收和截图导出；不作为屏幕合成器色彩对拍的替代。
    /// navigation=true 时输出 NavigationSize 比例的小纹理（按 Retina 倍率取整），照片铺满目标且方向与主图一致。</summary>
    /// <param name="navigation">是否输出导航图尺寸的等比小图。</param>
    /// <returns>线性 RGBA16F 帧，缓冲由池化租约持有。</returns>
    public unsafe DecodedImage CaptureLinearFrame(bool navigation)
    {
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
        using var texture = _device.CreateTexture(descriptor) ?? throw new InvalidOperationException("GPU 验收目标分配失败");
        using var command = _queue.CommandBuffer() ?? throw new InvalidOperationException("GPU 验收命令分配失败");
        EncodeFrame(command, texture, transform); command.Commit(); command.WaitUntilCompleted();
        if (command.Error != null) throw new InvalidOperationException(command.Error.LocalizedDescription);
        int stride = checked(width * 8);
        var buffer = DecodedImage.RentPixels(checked(stride * height));
        fixed (byte* pixels = buffer) texture.GetBytes((IntPtr)pixels, (nuint)stride, MTLRegion.Create2D(0, 0, (nuint)width, (nuint)height), 0);
        return DecodedImage.CreatePooled(buffer, width, height, stride, PixelEncoding.RgbaF16, ImageColorSpace.LinearSrgb);
    }

    /// <summary>绘制到任意 RGBA16F 目标，窗口呈现与验收读回复用同一着色器和变换。</summary>
    /// <param name="command">目标命令缓冲。</param>
    /// <param name="target">颜色附件纹理。</param>
    /// <param name="customTransform">自定义顶点参数；null 表示使用当前视口与照片。</param>
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
            encoder.SetRenderPipelineState(_textureIsDisplayP3 ? _displayP3Pipeline : _linearPipeline);
            float[] transform = customTransform ?? [(float)_viewport.ViewWidth, (float)_viewport.ViewHeight,
                (float)_viewport.ImageWidth, (float)_viewport.ImageHeight, (float)_viewport.Scale,
                (float)_viewport.PanX, (float)_viewport.PanY, _viewport.Orientation];
            fixed (float* values = transform) encoder.SetVertexBytes((IntPtr)values, (nuint)(transform.Length * sizeof(float)), 0);
            encoder.SetFragmentTexture(_texture, 0);
            if (_textureIsDisplayP3)
                fixed (float* matrix = P3ToExtendedLinearSrgb)
                    encoder.SetFragmentBytes((IntPtr)matrix, (nuint)(P3ToExtendedLinearSrgb.Length * sizeof(float)), 0);
            encoder.DrawPrimitives(MTLPrimitiveType.TriangleStrip, 0, 4);
        }
        encoder.EndEncoding();
    }

    /// <summary>释放纹理、两条管线、队列与照片层。</summary>
    public void Dispose()
    {
        _texture?.Dispose();
        _linearPipeline.Dispose();
        _displayP3Pipeline.Dispose();
        _queue.Dispose();
        Layer.Dispose();
        Interlocked.Exchange(ref _textureBytes, 0);
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
