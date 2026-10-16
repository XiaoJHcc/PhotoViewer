using System;
using System.Runtime.InteropServices;
using System.Text;
using DirectN;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using PhotoViewer.Core.Color;
using PhotoViewer.Core.Image;
using Windows.Foundation;
using WinRT;

namespace PhotoViewer.Desktop.Native;

/// <summary>
/// WinUI 3 的 <c>ISwapChainPanelNative</c>。DirectN 只带 UWP 版本的 IID(<c>f92f19d2-…</c>),
/// WinUI 3 是另一个接口(IID 见 <c>microsoft.ui.xaml.media.dxinterop.h</c>),必须自行声明。
/// 取接口只能显式调 <c>WinRT.CastExtensions.As&lt;T&gt;(panel)</c>:<c>SwapChainPanel</c> 自带同名静态
/// <c>As&lt;T&gt;</c>,写成 <c>panel.As&lt;T&gt;()</c> 会绑定到它并 QI 失败(实测 InvalidCastException)。
/// </summary>
[ComImport]
[Guid("63aad0b8-7c24-40ff-85a8-640d944cc325")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface ISwapChainPanelNativeWinUI
{
    /// <summary>把合成交换链交给 XAML 合成器;传 null 表示脱开。</summary>
    /// <param name="swapChain">合成交换链</param>
    /// <returns>HRESULT。</returns>
    [PreserveSig]
    HRESULT SetSwapChain(IDXGISwapChain swapChain);
}

/// <summary>
/// GPU 照片表面(阶段 W-M1/W-M2):D3D11 + DXGI 合成翻转链(<c>R16G16B16A16_FLOAT</c>),
/// EXIF 方向以 UV 置换在顶点着色器内完成(绝不旋转像素)、三线性 mipmap、适应/100% 缩放与拖动平移。
///
/// 契约与 macOS 的 <c>MetalPhotoView</c> 对等:只接受 <c>RgbaF16</c> + <c>LinearSrgb</c> 像素(不降级),
/// 代理图 → 全分辨率热替换保持相对平移、不跳变,100% 口径为 1 源像素 = 1 物理屏幕像素。
/// 全部方法只在 UI 线程调用,且 <see cref="SetImage"/> 必须在上层回调内同步完成。
/// </summary>
internal sealed class WindowsPhotoView : SwapChainPanel, IDisposable
{
    /// <summary>着色器:顶点阶段做布局变换与 EXIF 方向的 UV 置换,像素阶段三线性采样。</summary>
    private const string ShaderSource = """
        cbuffer PhotoParams : register(b0)
        {
            float4 viewportSize;    // xy = 物理视口像素
            float4 imageSize;       // xy = 显示朝向的源像素尺寸
            float4 scalePan;        // x = 每源像素的逻辑单位数, yz = 逻辑平移, w = EXIF 方向
            float4 rasterization;   // x = 物理像素 / 逻辑单位
        };

        struct VSOutput
        {
            float4 position : SV_POSITION;
            float2 uv : TEXCOORD0;
        };

        VSOutput VSMain(uint id : SV_VertexID)
        {
            float2 corners[4] = { float2(-0.5, -0.5), float2(0.5, -0.5), float2(-0.5, 0.5), float2(0.5, 0.5) };
            float2 corner = corners[id];
            float2 physical = (corner * imageSize.xy * scalePan.x + float2(scalePan.y, scalePan.z)) * rasterization.x;
            VSOutput output;
            output.position = float4(physical.x * 2.0 / viewportSize.x, -physical.y * 2.0 / viewportSize.y, 0, 1);
            float2 uv = corner + 0.5;
            int orientation = (int)scalePan.w;
            if (orientation == 2) uv = float2(1 - uv.x, uv.y);
            else if (orientation == 3) uv = 1 - uv;
            else if (orientation == 4) uv = float2(uv.x, 1 - uv.y);
            else if (orientation == 5) uv = uv.yx;
            else if (orientation == 6) uv = float2(uv.y, 1 - uv.x);
            else if (orientation == 7) uv = float2(1 - uv.y, 1 - uv.x);
            else if (orientation == 8) uv = float2(1 - uv.y, uv.x);
            output.uv = uv;
            return output;
        }

        Texture2D<float4> photo : register(t0);
        SamplerState photoSampler : register(s0);

        float4 PSMain(VSOutput input) : SV_Target
        {
            return photo.Sample(photoSampler, input.uv);
        }
        """;

    /// <summary>交换链缓冲数(翻转模型要求 ≥ 2)。</summary>
    private const uint BufferCount = 2;

    /// <summary>缩放下限(每源像素的逻辑单位数),再除以栅格化比。</summary>
    private const double MinScale = 0.01;

    /// <summary>缩放上限(每源像素的逻辑单位数),再除以栅格化比。</summary>
    private const double MaxScale = 16;

    /// <summary>画布底色(照片外沿用设计稿的 #000000)。</summary>
    private static readonly float[] ClearColor = [0f, 0f, 0f, 1f];

    /// <summary>已上传纹理为空时用于解除像素着色器绑定的空槽位。</summary>
    private static readonly ID3D11ShaderResourceView[] NoShaderResources = new ID3D11ShaderResourceView[1];

    private readonly IComObject<ID3D11Device> _device;
    private readonly IComObject<ID3D11DeviceContext> _context;
    private readonly IComObject<ID3D11VertexShader> _vertexShader;
    private readonly IComObject<ID3D11PixelShader> _pixelShader;
    private readonly IComObject<ID3D11SamplerState> _sampler;
    private readonly IComObject<ID3D11Buffer> _parameters;
    private readonly IComObject<IDXGISwapChain3> _swapChain;
    private readonly float[] _parameterData = new float[16];
    private IComObject<ID3D11RenderTargetView>? _renderTarget;
    private IComObject<ID3D11Texture2D>? _texture;
    private IComObject<ID3D11ShaderResourceView>? _textureView;
    private uint _pixelWidth = 1;
    private uint _pixelHeight = 1;
    private int _textureWidth;
    private int _textureHeight;
    private int _sourceWidth;
    private int _sourceHeight;
    private int _orientation = 1;
    private double _scale = 1;
    private double _panX;
    private double _panY;
    private bool _fit = true;
    private bool _panning;
    private bool _laidOut;
    private Point _lastPointer;
    private double _lastRasterization = 1;
    private (uint Width, uint Height, bool NeedsFull)? _reported;
    private bool _disposed;

    /// <summary>视口物理尺寸或 LOD 需求变化时触发(首次布局前不触发),供上层驱动 <c>PhotoLoadSession.UpdateViewport</c>。</summary>
    public event Action? ViewportChanged;

    /// <summary>当前视口的物理像素尺寸;面板完成首次布局前为 <c>(0, 0)</c>。</summary>
    public (double Width, double Height) PhysicalViewport => _laidOut ? (_pixelWidth, _pixelHeight) : (0, 0);

    /// <summary>当前物理缩放(每个源像素占多少物理屏幕像素)。</summary>
    public double PhysicalScale => _scale * PhysicalPixelsPerDip;

    /// <summary>是否需要全分辨率:非适应态且已达/超过 100%,或放大超出代理纹理能力。</summary>
    public bool NeedsFull
    {
        get
        {
            if (_fit || _textureView == null) return false;
            double physical = PhysicalScale;
            return physical >= 1 || physical > TextureDisplayWidth / Math.Max(1.0, DisplayWidth);
        }
    }

    /// <summary>显示百分比(1.0 = 100%,与物理缩放同口径)。</summary>
    public double ZoomRatio => PhysicalScale;

    /// <summary>每逻辑单位(DIP)对应的物理像素数;不在可视树内时为 1。</summary>
    private double PhysicalPixelsPerDip
    {
        get
        {
            double scale = XamlRoot?.RasterizationScale ?? 1;
            return scale > 0 ? scale : 1;
        }
    }

    /// <summary>显示朝向的源像素宽(EXIF 方向 5–8 交换横纵)。</summary>
    private double DisplayWidth => _orientation >= 5 ? _sourceHeight : _sourceWidth;

    /// <summary>显示朝向的源像素高。</summary>
    private double DisplayHeight => _orientation >= 5 ? _sourceWidth : _sourceHeight;

    /// <summary>代理纹理在显示朝向横轴上的像素宽,用于判断代理是否够撑满当前缩放。</summary>
    private double TextureDisplayWidth => _orientation >= 5 ? _textureHeight : _textureWidth;

    /// <summary>
    /// 建立 D3D11 设备、着色器、合成交换链并挂到本面板;仅在 UI 线程调用。
    /// 交换链先按 1×1 建好(构造期尚无布局尺寸),首次 <c>SizeChanged</c> 再 <c>ResizeBuffers</c> 到真实物理尺寸。
    /// </summary>
    public WindowsPhotoView()
    {
        NativeStartupLog.Write("photo view: 创建 D3D11 设备");
        _device = D3D11Functions.D3D11CreateDevice(null,
            D3D_DRIVER_TYPE.D3D_DRIVER_TYPE_HARDWARE,
            D3D11_CREATE_DEVICE_FLAG.D3D11_CREATE_DEVICE_BGRA_SUPPORT,
            out _context);

        var compileFlags = 0u;
#if DEBUG
        compileFlags = (uint)(Constants.D3DCOMPILE_DEBUG | Constants.D3DCOMPILE_SKIP_OPTIMIZATION);
#endif
        NativeStartupLog.Write("photo view: 编译着色器");
        var source = Encoding.UTF8.GetBytes(ShaderSource);
        _vertexShader = _device.CreateVertexShader(D3D11Functions.D3DCompile(source, "photo.hlsl", "VSMain", "vs_5_0", compileFlags));
        _pixelShader = _device.CreatePixelShader(D3D11Functions.D3DCompile(source, "photo.hlsl", "PSMain", "ps_5_0", compileFlags));
        _sampler = _device.CreateSamplerState(new D3D11_SAMPLER_DESC
        {
            Filter = D3D11_FILTER.D3D11_FILTER_MIN_MAG_MIP_LINEAR,
            AddressU = D3D11_TEXTURE_ADDRESS_MODE.D3D11_TEXTURE_ADDRESS_CLAMP,
            AddressV = D3D11_TEXTURE_ADDRESS_MODE.D3D11_TEXTURE_ADDRESS_CLAMP,
            AddressW = D3D11_TEXTURE_ADDRESS_MODE.D3D11_TEXTURE_ADDRESS_CLAMP,
            ComparisonFunc = D3D11_COMPARISON_FUNC.D3D11_COMPARISON_NEVER,
            BorderColor = new float[4],
            MinLOD = 0,
            MaxLOD = float.MaxValue,
        });
        _parameters = _device.CreateBuffer(new D3D11_BUFFER_DESC
        {
            ByteWidth = (uint)(_parameterData.Length * sizeof(float)),
            Usage = D3D11_USAGE.D3D11_USAGE_DEFAULT,
            BindFlags = (uint)D3D11_BIND_FLAG.D3D11_BIND_CONSTANT_BUFFER,
        }, null);

        NativeStartupLog.Write("photo view: 创建合成交换链");
        using (var dxgiDevice = _device.AsComObject<IDXGIDevice1>(true))
        using (var adapter = dxgiDevice.GetAdapter())
        using (var factory = adapter.GetFactory2())
        {
            _swapChain = factory.CreateSwapChainForComposition<IDXGISwapChain3>(dxgiDevice, new DXGI_SWAP_CHAIN_DESC1
            {
                Width = _pixelWidth,
                Height = _pixelHeight,
                Format = DXGI_FORMAT.DXGI_FORMAT_R16G16B16A16_FLOAT,
                Stereo = false,
                SampleDesc = new DXGI_SAMPLE_DESC { Count = 1, Quality = 0 },
                BufferUsage = (uint)Constants.DXGI_USAGE_RENDER_TARGET_OUTPUT,
                BufferCount = BufferCount,
                Scaling = DXGI_SCALING.DXGI_SCALING_STRETCH,
                SwapEffect = DXGI_SWAP_EFFECT.DXGI_SWAP_EFFECT_FLIP_SEQUENTIAL,
                AlphaMode = DXGI_ALPHA_MODE.DXGI_ALPHA_MODE_UNSPECIFIED,
                Flags = 0,
            }, null);
        }
        _swapChain.Object.SetColorSpace1(DXGI_COLOR_SPACE_TYPE.DXGI_COLOR_SPACE_RGB_FULL_G10_NONE_P709).ThrowOnError();
        CreateRenderTarget();

        NativeStartupLog.Write("photo view: 挂载到 SwapChainPanel");
        // 必须显式走 CsWinRT 的扩展:SwapChainPanel 自带同名静态 As<T>,写成 this.As<T>() 会绑定到它并 QI 失败。
        CastExtensions.As<ISwapChainPanelNativeWinUI>(this).SetSwapChain(_swapChain.Object).ThrowOnError();
        ApplyCompositionTransform();

        SizeChanged += OnSizeChanged;
        CompositionScaleChanged += OnCompositionScaleChanged;
        PointerPressed += OnPointerPressed;
        PointerMoved += OnPointerMoved;
        PointerReleased += OnPointerReleased;
        PointerCaptureLost += OnPointerCaptureLost;
        DoubleTapped += OnDoubleTapped;
    }

    /// <summary>
    /// 上传并显示一张图(传 null 清屏)。只接受线性 RGBA16F,其余编码/色彩空间直接拒绝。
    /// 纹理上传与 mipmap 生成同步完成,不跨回调持有像素引用;换图时按相对平移还原视口位置。
    /// </summary>
    /// <param name="image">已解码图像;null 表示清空画面。</param>
    public unsafe void SetImage(DecodedImage? image)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (image != null)
        {
            if (image.Encoding != PixelEncoding.RgbaF16 || image.ColorSpace != ImageColorSpace.LinearSrgb)
                throw new ArgumentException("Windows 主图必须为线性 RGBA16F", nameof(image));
            if (image.Pixels.Length == 0) throw new ArgumentException("像素租约已归零", nameof(image));
        }

        double relativePanX = _panX / Math.Max(1.0, DisplayWidth);
        double relativePanY = _panY / Math.Max(1.0, DisplayHeight);
        var previousTexture = _texture;
        var previousView = _textureView;
        _texture = null;
        _textureView = null;
        if (image != null)
        {
            _textureWidth = image.Width;
            _textureHeight = image.Height;
            _sourceWidth = image.SourceWidth;
            _sourceHeight = image.SourceHeight;
            _orientation = image.Orientation;
            var texture = _device.CreateTexture2D(new D3D11_TEXTURE2D_DESC
            {
                Width = (uint)image.Width,
                Height = (uint)image.Height,
                MipLevels = 0,
                ArraySize = 1,
                Format = DXGI_FORMAT.DXGI_FORMAT_R16G16B16A16_FLOAT,
                SampleDesc = new DXGI_SAMPLE_DESC { Count = 1, Quality = 0 },
                Usage = D3D11_USAGE.D3D11_USAGE_DEFAULT,
                BindFlags = (uint)(D3D11_BIND_FLAG.D3D11_BIND_SHADER_RESOURCE | D3D11_BIND_FLAG.D3D11_BIND_RENDER_TARGET),
                MiscFlags = (uint)D3D11_RESOURCE_MISC_FLAG.D3D11_RESOURCE_MISC_GENERATE_MIPS,
            }, null);
            fixed (byte* pixels = image.Pixels)
            {
                _context.Object.UpdateSubresource(texture.Object, 0, IntPtr.Zero, (IntPtr)pixels, (uint)image.Stride, 0);
            }
            var view = _device.CreateShaderResourceView(texture, null);
            _context.Object.GenerateMips(view.Object);
            _texture = texture;
            _textureView = view;
            NativeStartupLog.Write($"photo view: 上传照片 纹理={image.Width}x{image.Height} 源={image.SourceWidth}x{image.SourceHeight} " +
                $"方向={image.Orientation} 显示={DisplayWidth}x{DisplayHeight} 视口={PhysicalViewport.Width}x{PhysicalViewport.Height}");
            if (_fit) UpdateFitScale();
            else
            {
                _panX = relativePanX * DisplayWidth;
                _panY = relativePanY * DisplayHeight;
                ClampPan();
            }
        }

        if (previousView != null)
        {
            _context.Object.PSSetShaderResources(0, 1, NoShaderResources);
            previousView.Dispose();
        }
        previousTexture?.Dispose();
        Draw();
        NotifyViewportChanged();
    }

    /// <summary>适应视口:整幅照片铺满可见区,居中显示。</summary>
    public void FitToView()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _fit = true;
        _panX = 0;
        _panY = 0;
        UpdateFitScale();
        Draw();
        NotifyViewportChanged();
    }

    /// <summary>在“适应”与“100%”之间切换;切到 100% 时以给定视口坐标(默认视口中心)为锚点。</summary>
    /// <param name="anchorX">锚点 X(逻辑单位);null 取视口中心。</param>
    /// <param name="anchorY">锚点 Y(逻辑单位);null 取视口中心。</param>
    public void ToggleFit(double? anchorX = null, double? anchorY = null)
    {
        if (!_fit)
        {
            FitToView();
            return;
        }
        ZoomTo(1 / PhysicalPixelsPerDip, anchorX ?? ActualWidth / 2, anchorY ?? ActualHeight / 2);
    }

    /// <summary>以视口坐标为锚点按比例缩放。</summary>
    /// <param name="factor">缩放倍率。</param>
    /// <param name="anchorX">锚点 X(逻辑单位);null 取视口中心。</param>
    /// <param name="anchorY">锚点 Y(逻辑单位);null 取视口中心。</param>
    public void ZoomBy(double factor, double? anchorX = null, double? anchorY = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ZoomTo(_scale * factor, anchorX ?? ActualWidth / 2, anchorY ?? ActualHeight / 2);
    }

    /// <summary>释放全部 GPU 资源(按创建逆序),不触碰可视树。</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _context.Object.PSSetShaderResources(0, 1, NoShaderResources);
        _textureView?.Dispose();
        _texture?.Dispose();
        _renderTarget?.Dispose();
        _swapChain.Dispose();
        _parameters.Dispose();
        _sampler.Dispose();
        _pixelShader.Dispose();
        _vertexShader.Dispose();
        _context.Dispose();
        _device.Dispose();
    }

    /// <summary>以锚点为中心落到指定逻辑缩放,并重建平移、上报展示需求。</summary>
    private void ZoomTo(double scale, double anchorX, double anchorY)
    {
        double raster = PhysicalPixelsPerDip;
        scale = Math.Clamp(scale, MinScale / raster, MaxScale / raster);
        double centerX = ActualWidth / 2, centerY = ActualHeight / 2;
        double ratio = scale / Math.Max(1e-6, _scale);
        _panX = (anchorX - centerX) - ((anchorX - centerX) - _panX) * ratio;
        _panY = (anchorY - centerY) - ((anchorY - centerY) - _panY) * ratio;
        _scale = scale;
        _fit = false;
        ClampPan();
        Draw();
        NotifyViewportChanged();
    }

    /// <summary>按适应态重算逻辑缩放;尺寸未就绪时保持原值。</summary>
    private void UpdateFitScale()
    {
        if (DisplayWidth <= 0 || DisplayHeight <= 0 || ActualWidth <= 0 || ActualHeight <= 0) return;
        _scale = Math.Min(ActualWidth / DisplayWidth, ActualHeight / DisplayHeight);
    }

    /// <summary>约束平移,使照片不会整个被拖出视口;小于视口的轴居中。</summary>
    private void ClampPan()
    {
        double maxX = Math.Max(0, (DisplayWidth * _scale - ActualWidth) / 2);
        double maxY = Math.Max(0, (DisplayHeight * _scale - ActualHeight) / 2);
        _panX = Math.Clamp(_panX, -maxX, maxX);
        _panY = Math.Clamp(_panY, -maxY, maxY);
    }

    /// <summary>视口物理尺寸变化:重建缓冲与渲染目标、重设合成矩阵、重算适应缩放并重绘。</summary>
    /// <param name="sender">事件源。</param>
    /// <param name="e">尺寸变化参数。</param>
    private void OnSizeChanged(object sender, SizeChangedEventArgs e) => UpdateSurface("resize");

    /// <summary>跨屏 DPI 变化:逻辑尺寸可能不变,但物理缓冲必须按新栅格化比重建。</summary>
    /// <param name="sender">事件源。</param>
    /// <param name="args">事件参数。</param>
    private void OnCompositionScaleChanged(SwapChainPanel sender, object args) => UpdateSurface("composition scale");

    /// <summary>物理缓冲重建与几何重算的唯一落点;面板尚无布局尺寸时只标记未布局,不上报视口。</summary>
    /// <param name="stage">诊断阶段名。</param>
    private void UpdateSurface(string stage)
    {
        if (_disposed) return;
        try
        {
            if (ActualWidth <= 0 || ActualHeight <= 0)
            {
                // 首次布局前 ActualWidth/Height 为 0。此时交换链仍是构造期的 1×1 占位尺寸,
                // 绝不能把它当成真实视口上报:上层 UpdateViewport 会按 max(宽,高)×2 定代理长边,
                // 1×1 会把整批照片的代理目标压成 2 像素(实测主图退化成 2×2 放大后的色块)。
                _laidOut = false;
                return;
            }
            double raster = PhysicalPixelsPerDip;
            if (!_fit && raster != _lastRasterization) _scale *= _lastRasterization / raster;
            _lastRasterization = raster;
            _laidOut = true;
            uint width = (uint)Math.Round(ActualWidth * raster);
            uint height = (uint)Math.Round(ActualHeight * raster);
            if (width != _pixelWidth || height != _pixelHeight)
            {
                _renderTarget?.Dispose();
                _renderTarget = null;
                _swapChain.ResizeBuffers(BufferCount, width, height, DXGI_FORMAT.DXGI_FORMAT_R16G16B16A16_FLOAT, 0);
                _pixelWidth = width;
                _pixelHeight = height;
                CreateRenderTarget();
            }
            ApplyCompositionTransform();
            if (_fit) UpdateFitScale();
            else ClampPan();
            Draw();
            NotifyViewportChanged();
        }
        catch (Exception ex)
        {
            NativeStartupLog.Fail("photo view " + stage, ex);
        }
    }

    /// <summary>为当前后台缓冲建立渲染目标视图;翻转链的缓冲随时可能被 <c>ResizeBuffers</c> 重建。</summary>
    private void CreateRenderTarget()
    {
        using var buffer = _swapChain.GetBuffer<ID3D11Texture2D>(_swapChain.Object.GetCurrentBackBufferIndex());
        _renderTarget = _device.CreateRenderTargetView(buffer, null);
    }

    /// <summary>告诉合成器交换链缓冲已是物理像素,抵消 XAML 再乘一次的合成缩放。</summary>
    private void ApplyCompositionTransform()
    {
        double raster = PhysicalPixelsPerDip;
        var matrix = new DXGI_MATRIX_3X2_F { _11 = (float)(1 / raster), _22 = (float)(1 / raster) };
        _swapChain.Object.SetMatrixTransform(ref matrix).ThrowOnError();
    }

    /// <summary>按当前几何重绘一帧并呈现;无纹理时只清屏。</summary>
    private unsafe void Draw()
    {
        if (_disposed || _renderTarget == null) return;
        _context.Object.OMSetRenderTargets(1, [_renderTarget.Object], null);
        _context.Object.ClearRenderTargetView(_renderTarget.Object, ClearColor);
        if (_textureView != null)
        {
            _parameterData[0] = _pixelWidth;
            _parameterData[1] = _pixelHeight;
            _parameterData[4] = (float)DisplayWidth;
            _parameterData[5] = (float)DisplayHeight;
            _parameterData[8] = (float)_scale;
            _parameterData[9] = (float)_panX;
            _parameterData[10] = (float)_panY;
            _parameterData[11] = _orientation;
            _parameterData[12] = (float)PhysicalPixelsPerDip;
            fixed (float* pointer = _parameterData)
                _context.Object.UpdateSubresource(_parameters.Object, 0, IntPtr.Zero, (IntPtr)pointer, 0, 0);

            _context.Object.RSSetViewports(1, [new D3D11_VIEWPORT { Width = _pixelWidth, Height = _pixelHeight, MinDepth = 0, MaxDepth = 1 }]);
            _context.Object.IASetPrimitiveTopology(D3D_PRIMITIVE_TOPOLOGY.D3D_PRIMITIVE_TOPOLOGY_TRIANGLESTRIP);
            _context.Object.VSSetShader(_vertexShader.Object, null, 0);
            _context.Object.PSSetShader(_pixelShader.Object, null, 0);
            _context.Object.VSSetConstantBuffers(0, 1, [_parameters.Object]);
            _context.Object.PSSetShaderResources(0, 1, [_textureView.Object]);
            _context.Object.PSSetSamplers(0, 1, [_sampler.Object]);
            _context.Object.Draw(4, 0);
        }
        _swapChain.Present(1, 0);
    }

    /// <summary>视口物理尺寸或 LOD 需求变化时上报一次(重复值不重报,避免每帧打扰加载会话)。</summary>
    private void NotifyViewportChanged()
    {
        if (_disposed || !_laidOut) return;
        bool needsFull = NeedsFull;
        if (_reported is { } reported && reported.Width == _pixelWidth && reported.Height == _pixelHeight && reported.NeedsFull == needsFull)
            return;
        _reported = (_pixelWidth, _pixelHeight, needsFull);
        ViewportChanged?.Invoke();
    }

    /// <summary>左键按下开始拖动平移,并捕获指针以接收视口外移动。</summary>
    /// <param name="sender">事件源。</param>
    /// <param name="e">指针事件参数。</param>
    private void OnPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        try
        {
            var point = e.GetCurrentPoint(this);
            if (!point.Properties.IsLeftButtonPressed) return;
            _lastPointer = point.Position;
            _panning = CapturePointer(e.Pointer);
        }
        catch (Exception ex)
        {
            NativeStartupLog.Fail("photo view press", ex);
        }
    }

    /// <summary>拖动平移(仅放大态);平移不改变视口与 LOD 需求,故不上报。</summary>
    /// <param name="sender">事件源。</param>
    /// <param name="e">指针事件参数。</param>
    private void OnPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_panning) return;
        try
        {
            var position = e.GetCurrentPoint(this).Position;
            if (_fit)
            {
                _lastPointer = position;
                return;
            }
            _panX += position.X - _lastPointer.X;
            _panY += position.Y - _lastPointer.Y;
            _lastPointer = position;
            ClampPan();
            Draw();
            e.Handled = true;
        }
        catch (Exception ex)
        {
            NativeStartupLog.Fail("photo view move", ex);
        }
    }

    /// <summary>松开左键结束拖动。</summary>
    /// <param name="sender">事件源。</param>
    /// <param name="e">指针事件参数。</param>
    private void OnPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_panning) return;
        _panning = false;
        ReleasePointerCapture(e.Pointer);
    }

    /// <summary>指针捕获丢失(如窗口失活)时同样结束拖动。</summary>
    /// <param name="sender">事件源。</param>
    /// <param name="e">指针事件参数。</param>
    private void OnPointerCaptureLost(object sender, PointerRoutedEventArgs e) => _panning = false;

    /// <summary>双击在适应与 100% 之间切换,并以双击点作锚点。</summary>
    /// <param name="sender">事件源。</param>
    /// <param name="e">双击事件参数。</param>
    private void OnDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        try
        {
            _panning = false;
            var position = e.GetPosition(this);
            ToggleFit(position.X, position.Y);
            e.Handled = true;
        }
        catch (Exception ex)
        {
            NativeStartupLog.Fail("photo view double tap", ex);
        }
    }
}
