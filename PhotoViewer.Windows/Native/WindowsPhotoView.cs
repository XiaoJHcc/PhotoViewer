using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using DirectN;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using PhotoViewer.Core.Color;
using PhotoViewer.Core.Image;
using PhotoViewer.Core.Memory;
using SkiaSharp;
using Windows.Foundation;
using WinRT;

namespace PhotoViewer.Windows.Native;

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
    HRESULT SetSwapChain(IDXGISwapChain? swapChain);
}

/// <summary>
/// 标题栏亚克力配方:WinUI <c>AcrylicBrush</c> 的运行时取值(<c>TintColor</c> / <c>TintOpacity</c> /
/// <c>TintLuminosityOpacity</c> / <c>FallbackColor</c>),换色不改渲染代码。
/// </summary>
internal readonly struct AcrylicBackdropRecipe
{
    /// <summary>构建配方。</summary>
    /// <param name="tint">TintColor(sRGB 分量,0–1)。</param>
    /// <param name="tintOpacity">TintOpacity。</param>
    /// <param name="luminosityOpacity">TintLuminosityOpacity;负值表示主题画刷没有设置亮度层。</param>
    /// <param name="fallbackLinear">FallbackColor(线性 scRGB 分量,0–1),亮度层缺失时整条带用它。</param>
    public AcrylicBackdropRecipe((float R, float G, float B) tint, float tintOpacity, float luminosityOpacity,
        (float R, float G, float B) fallbackLinear)
    {
        Tint = tint;
        TintOpacity = tintOpacity;
        LuminosityOpacity = luminosityOpacity;
        FallbackLinear = fallbackLinear;
    }

    /// <summary>TintColor(sRGB 0–1);亮度层与颜色层的源色都用它。</summary>
    public (float R, float G, float B) Tint { get; }

    /// <summary>TintOpacity:颜色层不透明度。</summary>
    public float TintOpacity { get; }

    /// <summary>TintLuminosityOpacity:亮度层不透明度;负值表示未设置,此时退化为 FallbackColor 纯色带。</summary>
    public float LuminosityOpacity { get; }

    /// <summary>FallbackColor(线性 scRGB),亮度层缺失时的纯色带颜色。</summary>
    public (float R, float G, float B) FallbackLinear { get; }

    /// <summary>是否有亮度层(主题画刷设置了 <c>TintLuminosityOpacity</c>)。</summary>
    public bool HasLuminosityLayer => LuminosityOpacity >= 0;

    /// <summary>WinUI 亚克力噪点层不透明度(AcrylicBrush 的 sc_noiseOpacity)。</summary>
    public const float NoiseOpacity = 0.02f;

    /// <summary>WinUI 噪点纹理的平均灰度(NoiseAsset_256x256 六级灰阶的均值,约 128/255)。</summary>
    private const float NoiseMean = 0.503f;

    /// <summary>
    /// 背板是一整块 FallbackColor 时的亚克力结果(sRGB 0–1,含噪点均值):WinUI 背板取不到交换链时就落在它上面,
    /// 主图区段与 XAML 亚克力段在交界处都渐变到这个值,接缝两侧取值一致。与着色器 <c>BandTint</c> 同一套混合。
    /// </summary>
    /// <returns>sRGB 分量。</returns>
    public (float R, float G, float B) SeamColor()
    {
        var fallback = (Encode(FallbackLinear.R), Encode(FallbackLinear.G), Encode(FallbackLinear.B));
        if (!HasLuminosityLayer)
        {
            return fallback;
        }

        var tintLuminance = Luminance(Tint);
        var step1 = Lerp(fallback, SetLuminance(fallback, tintLuminance), LuminosityOpacity);
        var step2 = Lerp(step1, SetLuminance(Tint, Luminance(step1)), TintOpacity);
        return Lerp(step2, (NoiseMean, NoiseMean, NoiseMean), NoiseOpacity);
    }

    private static float Encode(float linear)
    {
        linear = Math.Clamp(linear, 0f, 1f);
        return linear <= 0.0031308f ? linear * 12.92f : 1.055f * MathF.Pow(linear, 1f / 2.4f) - 0.055f;
    }

    private static float Luminance((float R, float G, float B) c) => 0.3f * c.R + 0.59f * c.G + 0.11f * c.B;

    private static (float R, float G, float B) Lerp((float R, float G, float B) a, (float R, float G, float B) b, float t)
        => (a.R + (b.R - a.R) * t, a.G + (b.G - a.G) * t, a.B + (b.B - a.B) * t);

    /// <summary>W3C 合成规范的 SetLum + ClipColor(与着色器 bandSetLuminance 一致)。</summary>
    private static (float R, float G, float B) SetLuminance((float R, float G, float B) c, float l)
    {
        var d = l - Luminance(c);
        float r = c.R + d, g = c.G + d, b = c.B + d;
        var lo = MathF.Min(r, MathF.Min(g, b));
        var hi = MathF.Max(r, MathF.Max(g, b));
        if (lo < 0)
        {
            var k = l / MathF.Max(l - lo, 1e-5f);
            (r, g, b) = (l + (r - l) * k, l + (g - l) * k, l + (b - l) * k);
        }

        if (hi > 1)
        {
            var k = (1 - l) / MathF.Max(hi - l, 1e-5f);
            (r, g, b) = (l + (r - l) * k, l + (g - l) * k, l + (b - l) * k);
        }

        return (Math.Clamp(r, 0f, 1f), Math.Clamp(g, 0f, 1f), Math.Clamp(b, 0f, 1f));
    }

    /// <summary>与另一份配方的取值是否一致(避免重复重绘)。</summary>
    /// <param name="other">另一份配方。</param>
    /// <returns>全部取值相同返回 true。</returns>
    public bool NearlyEquals(AcrylicBackdropRecipe other)
        => Tint == other.Tint
            && Math.Abs(TintOpacity - other.TintOpacity) < 1e-4
            && Math.Abs(LuminosityOpacity - other.LuminosityOpacity) < 1e-4
            && FallbackLinear == other.FallbackLinear;
}

/// <summary>
/// 标题栏背板区段两侧的邻接关系:区段左/右缘是窗口边(越界钳到边缘像素)还是 XAML 内容(越界按 FallbackColor 补),
/// 以及该侧是否紧接一段 XAML 亚克力(需要渐变到同一个值,避免接缝)。
/// </summary>
/// <param name="PadLeft">左缘外是 XAML 内容</param>
/// <param name="PadRight">右缘外是 XAML 内容</param>
/// <param name="FadeLeft">左缘紧接 XAML 亚克力段</param>
/// <param name="FadeRight">右缘紧接 XAML 亚克力段</param>
internal readonly record struct TitleBarBackdropEdges(bool PadLeft, bool PadRight, bool FadeLeft, bool FadeRight);

/// <summary>
/// GPU 照片表面(阶段 W-M1/W-M2):D3D11 + DXGI 合成翻转链(<c>R16G16B16A16_FLOAT</c>),
/// EXIF 方向以 UV 置换在顶点着色器内完成(绝不旋转像素)、三线性 mipmap、适应/100% 缩放与拖动平移。
///
/// 契约与 macOS 的 <c>MetalPhotoView</c> 对等:只接受 <c>RgbaF16</c> + <c>LinearSrgb</c> 像素(不降级),
/// 代理图 → 全分辨率热替换保持相对平移、不跳变,100% 口径为 1 源像素 = 1 物理屏幕像素。
/// 全部方法只在 UI 线程调用,且 <see cref="SetImage"/> 必须在上层回调内同步完成。
/// </summary>
internal sealed partial class WindowsPhotoView : SwapChainPanel, IDisposable
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

        // ===== 标题栏亚克力背板(SHELL-02)=====
        // WinUI 3 的 SwapChainPanel 不支持 AcrylicBrush 背板取样,标题栏压在照片上的区段由这里在交换链内
        // 画同一份亚克力,逐步对应 WinUI AcrylicBrush 的效果图:背板 → 高斯模糊 → 亮度混合 → 颜色混合
        // → 2% 噪点纹理(源覆盖合成)。配方与参数全部来自运行时主题画刷、WinUI 自带噪点资源与设计令牌。

        cbuffer BandParams : register(b1)
        {
            float4 bandTarget;      // xy = 目标左上(物理像素), zw = 目标尺寸
            float4 bandDestSize;    // xy = 目标缓冲尺寸(物理像素)
            float4 bandUv;          // xy = uv 起点, zw = uv 跨度
            float4 bandBlur;        // xy = 单步 uv(降采样阶段为逻辑源区单纹素 uv), z = sigma(已按降采样折算), w = 半径
            float4 bandTint;        // rgb = TintColor(sRGB), a = TintColor.A × TintOpacity
            float4 bandLuminosity;  // rgb = TintColor 的亮度层色(sRGB), a = TintLuminosityOpacity
            float4 bandSolid;       // rgb = FallbackColor(线性), a > 0.5 表示纯色回退
            float4 bandValid;       // 降采样阶段:逻辑源区 uv 里真实像素的范围(uMin, uMax, vMin, vMax)
            float4 bandPad;         // x/y > 0.5:左/右超出真实像素处按 FallbackColor 补(该侧是 XAML 内容,不是窗口边)
            float4 bandFade;        // x/y = 左/右渐变起点(物理像素,负值表示该侧不渐变), z = 渐变宽度
            float4 bandNoise;       // xy = 噪点平铺原点(物理像素), z = 每物理像素的噪点 uv, w = 噪点不透明度(0 = 无噪点)
        };

        struct BandOutput
        {
            float4 position : SV_POSITION;
            float2 uv : TEXCOORD0;
        };

        BandOutput BandVS(uint id : SV_VertexID)
        {
            float2 corners[4] = { float2(0, 0), float2(1, 0), float2(0, 1), float2(1, 1) };
            float2 corner = corners[id];
            float2 physical = bandTarget.xy + corner * bandTarget.zw;
            BandOutput output;
            output.position = float4(physical.x * 2.0 / bandDestSize.x - 1.0, -physical.y * 2.0 / bandDestSize.y + 1.0, 0, 1);
            output.uv = bandUv.xy + corner * bandUv.zw;
            return output;
        }

        Texture2D<float4> bandSource : register(t0);
        Texture2D<float4> bandNoiseTexture : register(t1);
        SamplerState bandSampler : register(s0);
        SamplerState bandNoiseSampler : register(s1);

        float3 BandLinearToSrgb(float3 c)
        {
            c = saturate(c);
            return c <= 0.0031308 ? c * 12.92 : 1.055 * pow(c, 1.0 / 2.4) - 0.055;
        }

        float3 BandSrgbToLinear(float3 c)
        {
            c = saturate(c);
            return c <= 0.04045 ? c / 12.92 : pow((c + 0.055) / 1.055, 2.4);
        }

        // 逻辑源区的一点:真实像素取后台缓冲拷贝(越界钳到边缘,对应窗口边);
        // 越过左/右边且该侧是 XAML 内容时取 FallbackColor —— WinUI 背板取不到交换链时同样落到它上面。
        float3 BandSourceAt(float2 uv)
        {
            float2 local = (uv - bandValid.xz) / (bandValid.yw - bandValid.xz);
            float3 pixel = bandSource.Sample(bandSampler, local).rgb;
            bool padded = (uv.x < bandValid.x && bandPad.x > 0.5) || (uv.x > bandValid.y && bandPad.y > 0.5);
            return padded ? bandSolid.rgb : pixel;
        }

        // 降采样 4 倍:双线性 4 采样覆盖 4x4 源纹素,顺手把线性 scRGB 夹到 [0,1] 并转成 sRGB ——
        // WinUI/D2D 的模糊与混合都在 gamma 编码值上做。
        float4 BandDownsamplePS(BandOutput input) : SV_Target
        {
            float2 texel = bandBlur.xy;
            float3 color = BandSourceAt(input.uv + texel * float2(-1, -1))
                + BandSourceAt(input.uv + texel * float2(1, -1))
                + BandSourceAt(input.uv + texel * float2(-1, 1))
                + BandSourceAt(input.uv + texel * float2(1, 1));
            return float4(BandLinearToSrgb(color * 0.25), 1.0);
        }

        // 可分离高斯的一趟:bandBlur.xy 是单步 uv 方向,sigma 已按降采样折算。
        float4 BandBlurPS(BandOutput input) : SV_Target
        {
            float2 uv = input.position.xy / bandDestSize.xy;
            float sigma = max(bandBlur.z, 0.0001);
            int radius = (int)bandBlur.w;
            float3 sum = bandSource.Sample(bandSampler, uv).rgb;
            float weightSum = 1.0;
            [loop] for (int i = 1; i <= radius; i++)
            {
                float weight = exp(-0.5 * (i * i) / (sigma * sigma));
                sum += bandSource.Sample(bandSampler, uv + bandBlur.xy * i).rgb * weight;
                sum += bandSource.Sample(bandSampler, uv - bandBlur.xy * i).rgb * weight;
                weightSum += 2.0 * weight;
            }
            return float4(sum / weightSum, 1.0);
        }

        float bandLuminance(float3 c) { return dot(c, float3(0.3, 0.59, 0.11)); }

        // W3C 合成规范的 ClipColor:SetLum 把源亮度套到目标色上并夹回色域。
        float3 bandSetLuminance(float3 c, float l)
        {
            float3 shifted = c + (l - bandLuminance(c));
            float lo = min(min(shifted.r, shifted.g), shifted.b);
            float hi = max(max(shifted.r, shifted.g), shifted.b);
            if (lo < 0.0) shifted = l + ((shifted - l) * l) / max(l - lo, 1e-5);
            if (hi > 1.0) shifted = l + ((shifted - l) * (1.0 - l)) / max(hi - l, 1e-5);
            return saturate(shifted);
        }

        // 亮度层混合(Luminosity:保留背板色相/饱和度,亮度取亮度层色)→ 颜色层混合(Color:色相/饱和度取色调色)。
        float3 BandTint(float3 blur)
        {
            float3 step1 = lerp(blur, bandSetLuminance(blur, bandLuminance(bandLuminosity.rgb)), bandLuminosity.a);
            return lerp(step1, bandSetLuminance(bandTint.rgb, bandLuminance(step1)), bandTint.a);
        }

        // 合成:模糊(sRGB)→ 亮度/颜色混合 → 贴 XAML 亚克力段的一侧渐变到"背板全是 FallbackColor"的结果
        // (那一段的 WinUI 背板取不到照片,接缝两边在交界处取同一个值)→ 噪点 → 解码回线性写出。
        float4 BandAcrylicPS(BandOutput input) : SV_Target
        {
            if (bandSolid.a > 0.5)
            {
                return float4(bandSolid.rgb, 1.0);
            }

            float3 color = BandTint(bandSource.Sample(bandSampler, input.uv).rgb);
            float x = input.position.x;
            float weight = 1.0;
            if (bandFade.x >= 0.0) weight = min(weight, smoothstep(0.0, bandFade.z, x - bandFade.x));
            if (bandFade.y >= 0.0) weight = min(weight, smoothstep(0.0, bandFade.z, bandFade.y - x));
            if (weight < 1.0)
            {
                color = lerp(BandTint(BandLinearToSrgb(bandSolid.rgb)), color, weight);
            }

            if (bandNoise.w > 0.0)
            {
                float noise = bandNoiseTexture.Sample(bandNoiseSampler, (input.position.xy - bandNoise.xy) * bandNoise.z).r;
                color = lerp(color, noise.xxx, bandNoise.w);
            }

            return float4(BandSrgbToLinear(color), 1.0);
        }
        """;

    /// <summary>交换链缓冲数(翻转模型要求 ≥ 2)。</summary>
    private const uint BufferCount = 2;

    /// <summary>除当前纹理外保留的最近帧纹理张数(对比切换/来回翻页不必重传)。</summary>
    private const int RecentTextureCapacity = 2;

    /// <summary>
    /// 像素数据达到该字节数时改走后台线程上传:整幅 CPU→GPU 拷贝在 UI 线程上会直接计入切换延迟
    /// (42MP 的 F16 全幅实测 44 ms 中位、首帧 400 ms 级)。
    /// </summary>
    private const long BackgroundUploadBytes = 32L * 1024 * 1024;

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

    // ===== 标题栏亚克力背板(SHELL-02):SwapChainPanel 取不到 AcrylicBrush 背板,改在交换链内画同一亚克力 =====
    private readonly IComObject<ID3D11VertexShader> _bandVertexShader;
    private readonly IComObject<ID3D11PixelShader> _bandDownsampleShader;
    private readonly IComObject<ID3D11PixelShader> _bandBlurShader;
    private readonly IComObject<ID3D11PixelShader> _bandAcrylicShader;
    private readonly IComObject<ID3D11Buffer> _bandParameters;
    private readonly IComObject<ID3D11SamplerState> _noiseSampler;
    private readonly float[] _bandParameterData = new float[44];
    private IComObject<ID3D11Texture2D>? _noiseTexture;
    private IComObject<ID3D11ShaderResourceView>? _noiseView;
    private bool _noiseLoaded;
    private readonly Stopwatch _bandClock = new();
    private IComObject<ID3D11Texture2D>? _bandSource;
    private IComObject<ID3D11ShaderResourceView>? _bandSourceView;
    private readonly IComObject<ID3D11Texture2D>?[] _bandScratch = new IComObject<ID3D11Texture2D>?[2];
    private readonly IComObject<ID3D11ShaderResourceView>?[] _bandScratchView = new IComObject<ID3D11ShaderResourceView>?[2];
    private readonly IComObject<ID3D11RenderTargetView>?[] _bandScratchTarget = new IComObject<ID3D11RenderTargetView>?[2];
    private (uint Width, uint Height) _bandSourceSize;
    private (uint Width, uint Height) _bandLogicalSize;
    private (uint Width, uint Height) _bandScratchSize;
    private long _bandBytes;
    private TitleBarBandRequest? _titleBarBand;
    private bool _bandFailed;
    private bool _bandCostLogged;
    private IComObject<ID3D11RenderTargetView>? _renderTarget;
    /// <summary>当前上屏的纹理条目;null 表示没有照片。</summary>
    private TextureEntry? _current;
    /// <summary>最近显示过的纹理(不含当前),按最近使用顺序排列,仅 UI 线程访问。</summary>
    private readonly List<TextureEntry> _recentTextures = new(RecentTextureCapacity);
    /// <summary>
    /// 退下来的同尺寸空纹理(最多一张),给下一次同步上传复用:每次切图新建一张 21 MB 的 mip 链纹理,
    /// 实测约 10% 的 <c>CreateTexture2D</c> 会在驱动里停 300–500 ms,那一段直接落在 UI 线程与切图延迟上。
    /// 只留一张、只收小图(大图全分辨率纹理几百 MB,不值得占着),压力 Caution 起随缓存一起清掉。
    /// </summary>
    private TextureEntry? _spareTexture;
    /// <summary>在途的大图暂存上传(同时最多一次),仅 UI 线程访问;后台线程仍在写映射内存时不能释放。</summary>
    private StagedUpload? _stagedUpload;
    private readonly PhotoViewport _viewport = new();
    /// <summary>GPU 纹理占用字节(当前 + 最近缓存),供内存管家读取;UI 线程写入。</summary>
    private long _gpuBytes;
    private GpuTextureMemoryClient? _memoryClient;
    /// <summary>当前期望上屏的帧(按实例);在途上传据此判断是否还要它。</summary>
    private WeakReference<DecodedImage>? _wanted;
    /// <summary>当前上屏照片的代际(来自 <see cref="PhotoLoadSession.Generation"/>);代际变化即切图,不是热替换。</summary>
    private int _generation;
    /// <summary>是否有当前期望帧的上传在途(0/1,可跨线程读)。</summary>
    private int _uploadPending;
    /// <summary>内存压力要求修剪缓存(0/1):Trim 在探测线程上调用,只能标记后由 UI 线程清场。</summary>
    private int _trimRequested;
    private uint _pixelWidth = 1;
    private uint _pixelHeight = 1;
    private bool _panning;
    private bool _laidOut;
    private Point _lastPointer;
    private (uint Width, uint Height, bool NeedsFull)? _reported;
    private bool _disposed;

    /// <summary>视口物理尺寸或 LOD 需求变化时触发(首次布局前不触发),供上层驱动 <c>PhotoLoadSession.UpdateViewport</c>。</summary>
    public event Action? ViewportChanged;

    /// <summary>缩放/平移/照片几何变化时触发(每帧重绘前),供导航图刷新可见框与百分比。</summary>
    public event Action? RenderStateChanged;

    /// <summary>当前是否处于适应态(导航图只在非适应态显示)。</summary>
    public bool IsFit => _viewport.IsFit;

    /// <summary>当前是否已上传照片纹理。</summary>
    public bool HasImage => _current != null;

    /// <summary>
    /// 是否有大图上传在后台进行:期间画面仍停在上一帧(不铺占位、不清黑),纹理与新几何在完成时一起交换。
    /// 验收在换图后核对纹理尺寸/物理缩放前应等它结束。
    /// </summary>
    public bool IsUploadPending => Volatile.Read(ref _uploadPending) != 0;

    /// <summary>当前上屏纹理的像素尺寸;无纹理时为 <c>(0, 0)</c>。供定向验收核对纹理是否真的换成了新的一帧。</summary>
    internal (int Width, int Height) CurrentTextureSize => _current is { } current ? (current.Width, current.Height) : (0, 0);

    /// <summary>设置当前照片的对焦点(归一化显示朝向);两个分量都为 null 表示没有对焦点。</summary>
    /// <param name="x">归一化横坐标。</param>
    /// <param name="y">归一化纵坐标。</param>
    public void SetFocus(double? x, double? y) => _viewport.SetFocus(x, y);

    /// <summary>
    /// 读取主图表面在给定显示相对坐标(u,v ∈ [0,1],相对照片显示区域)上的像素。
    /// 仅定向验收使用(核对 EXIF 方向在主图上的实际朝向);产品路径不读回像素。
    /// </summary>
    /// <param name="u">显示区域横向相对位置 0-1。</param>
    /// <param name="v">显示区域纵向相对位置 0-1。</param>
    /// <param name="color">线性 RGB(0-1)。</param>
    /// <returns>坐标落在视口内且回读成功时为 true。</returns>
    internal unsafe bool TryReadDisplayPixel(double u, double v, out (float R, float G, float B) color)
    {
        color = default;
        if (_disposed || _renderTarget == null || _current == null || _pixelWidth == 0 || _pixelHeight == 0)
        {
            return false;
        }

        double raster = PhysicalPixelsPerDip;
        double x = (ActualWidth / 2) + ((u - 0.5) * _viewport.ImageWidth * _viewport.Scale) + _viewport.PanX;
        double y = (ActualHeight / 2) + ((v - 0.5) * _viewport.ImageHeight * _viewport.Scale) + _viewport.PanY;
        return ReadBackBufferPixel((int)Math.Round(x * raster), (int)Math.Round(y * raster), out color);
    }

    /// <summary>
    /// 读取主图表面的物理像素(视口左上为原点,DIP 输入),含标题栏亚克力区段。仅定向验收使用。
    /// </summary>
    /// <param name="xDip">视口内横向 DIP。</param>
    /// <param name="yDip">视口内纵向 DIP。</param>
    /// <param name="color">线性 RGB(0-1)。</param>
    /// <returns>坐标落在视口内且回读成功时为 true。</returns>
    internal bool TryReadViewportPixel(double xDip, double yDip, out (float R, float G, float B) color)
    {
        color = default;
        if (_disposed || _renderTarget == null || _pixelWidth == 0 || _pixelHeight == 0)
        {
            return false;
        }

        double raster = PhysicalPixelsPerDip;
        return ReadBackBufferPixel((int)Math.Round(xDip * raster), (int)Math.Round(yDip * raster), out color);
    }

    /// <summary>
    /// 把后台缓冲的一块物理像素区域读成 8 位 sRGB BGRA(顶行在前)。仅定向验收使用,用于导出背板区段截图。
    /// </summary>
    /// <param name="regionX">区域左上横坐标(物理像素,视口左上为原点)。</param>
    /// <param name="regionY">区域左上纵坐标(物理像素)。</param>
    /// <param name="regionWidth">区域宽(物理像素)。</param>
    /// <param name="regionHeight">区域高(物理像素)。</param>
    /// <param name="bgra">BGRA 字节。</param>
    /// <param name="rowBytes">每行字节数。</param>
    /// <returns>区域合法且回读成功时为 true。</returns>
    internal unsafe bool TryReadBackBufferSrgb(int regionX, int regionY, int regionWidth, int regionHeight,
        out byte[] bgra, out int rowBytes)
    {
        bgra = Array.Empty<byte>();
        rowBytes = 0;
        if (_disposed || _renderTarget == null || _pixelWidth == 0 || _pixelHeight == 0)
        {
            return false;
        }

        int left = Math.Clamp(regionX, 0, (int)_pixelWidth);
        int top = Math.Clamp(regionY, 0, (int)_pixelHeight);
        int right = Math.Clamp(regionX + regionWidth, 0, (int)_pixelWidth);
        int bottom = Math.Clamp(regionY + regionHeight, 0, (int)_pixelHeight);
        if (right <= left || bottom <= top)
        {
            return false;
        }

        using var staging = CreateStagingTexture();
        using var backBuffer = _swapChain.GetBuffer<ID3D11Texture2D>(0);
        RenderScene();
        _context.Object.CopyResource(staging.Object, backBuffer.Object);
        var mapped = _context.Object.Map(staging.Object, 0, D3D11_MAP.D3D11_MAP_READ, 0);
        try
        {
            int width = right - left;
            int height = bottom - top;
            rowBytes = width * 4;
            bgra = new byte[rowBytes * height];
            for (var row = 0; row < height; row++)
            {
                var pixels = (ushort*)IntPtr.Add(mapped.pData, (int)(((top + row) * mapped.RowPitch) + (left * 8)));
                for (var column = 0; column < width; column++)
                {
                    var value = pixels + (column * 4);
                    var offset = (row * rowBytes) + (column * 4);
                    bgra[offset] = LinearToSrgbByte(HalfToFloat(value[2]));
                    bgra[offset + 1] = LinearToSrgbByte(HalfToFloat(value[1]));
                    bgra[offset + 2] = LinearToSrgbByte(HalfToFloat(value[0]));
                    bgra[offset + 3] = 0xFF;
                }
            }

            return true;
        }
        finally
        {
            _context.Object.Unmap(staging.Object, 0);
            _swapChain.Present(1, 0);
        }
    }

    /// <summary>读回后台缓冲的单个物理像素(含标题栏亚克力区段);回读后照常呈现。</summary>
    /// <param name="px">物理像素横坐标。</param>
    /// <param name="py">物理像素纵坐标。</param>
    /// <param name="color">线性 RGB(0-1)。</param>
    /// <returns>坐标落在视口内且回读成功时为 true。</returns>
    private unsafe bool ReadBackBufferPixel(int px, int py, out (float R, float G, float B) color)
    {
        color = default;
        if (_disposed || _renderTarget == null || px < 0 || py < 0 || px >= _pixelWidth || py >= _pixelHeight)
        {
            return false;
        }

        using var staging = CreateStagingTexture();
        using var backBuffer = _swapChain.GetBuffer<ID3D11Texture2D>(0);
        RenderScene();
        _context.Object.CopyResource(staging.Object, backBuffer.Object);
        var mapped = _context.Object.Map(staging.Object, 0, D3D11_MAP.D3D11_MAP_READ, 0);
        try
        {
            var pixel = (ushort*)IntPtr.Add(mapped.pData, (int)((py * mapped.RowPitch) + (px * 8)));
            color = (HalfToFloat(pixel[0]), HalfToFloat(pixel[1]), HalfToFloat(pixel[2]));
            return true;
        }
        finally
        {
            _context.Object.Unmap(staging.Object, 0);
            _swapChain.Present(1, 0);
        }
    }

    /// <summary>整幅回读用的暂存纹理(与后台缓冲同尺寸同格式)。</summary>
    /// <returns>可映射读取的暂存纹理。</returns>
    private IComObject<ID3D11Texture2D> CreateStagingTexture() => _device.CreateTexture2D(new D3D11_TEXTURE2D_DESC
    {
        Width = _pixelWidth,
        Height = _pixelHeight,
        MipLevels = 1,
        ArraySize = 1,
        Format = DXGI_FORMAT.DXGI_FORMAT_R16G16B16A16_FLOAT,
        SampleDesc = new DXGI_SAMPLE_DESC { Count = 1, Quality = 0 },
        Usage = D3D11_USAGE.D3D11_USAGE_STAGING,
        BindFlags = 0,
        CPUAccessFlags = (uint)D3D11_CPU_ACCESS_FLAG.D3D11_CPU_ACCESS_READ,
        MiscFlags = 0,
    }, null);

    /// <summary>线性 scRGB 分量 → 8 位 sRGB(导出截图用)。</summary>
    /// <param name="value">线性分量。</param>
    /// <returns>0–255 的 sRGB 值。</returns>
    private static byte LinearToSrgbByte(float value)
    {
        double linear = Math.Clamp(value, 0, 1);
        double encoded = linear <= 0.0031308 ? linear * 12.92 : (1.055 * Math.Pow(linear, 1 / 2.4)) - 0.055;
        return (byte)Math.Clamp(Math.Round(encoded * 255), 0, 255);
    }

    /// <summary>
    /// 把主图对准给定图像点:该点在视口居中,并按给定物理缩放呈现(1.0 = 100%)。
    /// 供分析栏细节预览双击使用(旧壳 <c>FocusOnImagePoint(point, 1.0)</c>)。
    /// </summary>
    /// <param name="u">图像点横向相对位置 0-1(显示朝向)</param>
    /// <param name="v">图像点纵向相对位置 0-1(显示朝向)</param>
    /// <param name="physicalScale">目标物理缩放(1.0 = 100%)</param>
    public void FocusOnImagePoint(double u, double v, double physicalScale = 1.0)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _viewport.FocusOnPoint(u, v, physicalScale);
        Draw();
        NotifyViewportChanged();
    }

    /// <summary>
    /// 保持当前缩放，把显示朝向的归一化图像点移到视口中心（导航图拖动定位）。
    /// </summary>
    /// <param name="u">归一化横坐标。</param>
    /// <param name="v">归一化纵坐标。</param>
    public void CenterOnNormalizedPoint(double u, double v)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_viewport.IsFit)
        {
            return;
        }

        _viewport.CenterOnNormalizedPoint(u, v);
        Draw();
        NotifyViewportChanged();
    }

    /// <summary>
    /// 把图像相对矩形(uv)换算成视口逻辑坐标矩形,供主图绿框等叠加层定位。
    /// </summary>
    /// <param name="uv">图像相对矩形</param>
    /// <param name="rect">视口逻辑坐标矩形</param>
    /// <returns>无图时为 false</returns>
    public bool TryGetImageRect(Rect uv, out Rect rect)
    {
        rect = default;
        if (_current == null || ActualWidth <= 0 || ActualHeight <= 0)
        {
            return false;
        }

        double x = (ActualWidth / 2) + (((uv.X - 0.5) * _viewport.ImageWidth * _viewport.Scale) + _viewport.PanX);
        double y = (ActualHeight / 2) + (((uv.Y - 0.5) * _viewport.ImageHeight * _viewport.Scale) + _viewport.PanY);
        rect = new Rect(x, y, uv.Width * _viewport.ImageWidth * _viewport.Scale, uv.Height * _viewport.ImageHeight * _viewport.Scale);
        return true;
    }

    /// <summary><c>D3D11_BOX</c> 的等价布局(复制后台缓冲的源区用)。</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct Box3D
    {
        /// <summary>左边界(含)。</summary>
        public uint Left;

        /// <summary>上边界(含)。</summary>
        public uint Top;

        /// <summary>前边界(含)。</summary>
        public uint Front;

        /// <summary>右边界(不含)。</summary>
        public uint Right;

        /// <summary>下边界(不含)。</summary>
        public uint Bottom;

        /// <summary>后边界(不含)。</summary>
        public uint Back;
    }

    /// <summary>半精度浮点转 float。</summary>
    private static float HalfToFloat(ushort value) => (float)BitConverter.UInt16BitsToHalf(value);

    /// <summary>
    /// 导航图逻辑尺寸(长边 150、按显示朝向比例缩小,不补方形衬边),与旧壳缩放指示器一致;
    /// 无照片时返回 150 方形占位。
    /// </summary>
    public (double Width, double Height) NavigationSize => _viewport.NavigationSize;

    /// <summary>
    /// 导航图(逻辑坐标,无外框内边距)内的可见区域;缩放小于适应时退化为整幅照片。
    /// </summary>
    public Rect NavigationViewport
    {
        get
        {
            var (x, y, width, height) = _viewport.NavigationViewport;
            return new Rect(x, y, width, height);
        }
    }

    /// <summary>当前视口的物理像素尺寸;面板完成首次布局前为 <c>(0, 0)</c>。</summary>
    public (double Width, double Height) PhysicalViewport => _laidOut ? (_pixelWidth, _pixelHeight) : (0, 0);

    /// <summary>视口中心对应的归一化图像点(显示朝向),供导航图拖动验收核对落点。</summary>
    public (double U, double V) ViewCenter => _viewport.CenterUV;

    /// <summary>当前物理缩放(每个源像素占多少物理屏幕像素)。</summary>
    public double PhysicalScale => _viewport.PhysicalScale;

    /// <summary>是否需要全分辨率:非适应态且已达/超过 100%,或放大超出代理纹理能力。</summary>
    public bool NeedsFull => _current != null && _viewport.NeedsFull;

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
    private double DisplayWidth => _viewport.ImageWidth;

    /// <summary>显示朝向的源像素高。</summary>
    private double DisplayHeight => _viewport.ImageHeight;

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
        _bandVertexShader = _device.CreateVertexShader(D3D11Functions.D3DCompile(source, "photo.hlsl", "BandVS", "vs_5_0", compileFlags));
        _bandDownsampleShader = _device.CreatePixelShader(D3D11Functions.D3DCompile(source, "photo.hlsl", "BandDownsamplePS", "ps_5_0", compileFlags));
        _bandBlurShader = _device.CreatePixelShader(D3D11Functions.D3DCompile(source, "photo.hlsl", "BandBlurPS", "ps_5_0", compileFlags));
        _bandAcrylicShader = _device.CreatePixelShader(D3D11Functions.D3DCompile(source, "photo.hlsl", "BandAcrylicPS", "ps_5_0", compileFlags));
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
        _noiseSampler = _device.CreateSamplerState(new D3D11_SAMPLER_DESC
        {
            Filter = D3D11_FILTER.D3D11_FILTER_MIN_MAG_MIP_LINEAR,
            AddressU = D3D11_TEXTURE_ADDRESS_MODE.D3D11_TEXTURE_ADDRESS_WRAP,
            AddressV = D3D11_TEXTURE_ADDRESS_MODE.D3D11_TEXTURE_ADDRESS_WRAP,
            AddressW = D3D11_TEXTURE_ADDRESS_MODE.D3D11_TEXTURE_ADDRESS_WRAP,
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
        _bandParameters = _device.CreateBuffer(new D3D11_BUFFER_DESC
        {
            ByteWidth = (uint)(_bandParameterData.Length * sizeof(float)),
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
        LogDisplayTopology();

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

        // 主图纹理是常驻 GPU 占用(42MP 的 F16 一帧约 350MB 含 mip 链),纳入统一内存管控:
        // 压力到 Caution 起丢掉非当前帧的缓存纹理。
        _memoryClient = new GpuTextureMemoryClient(this);
        MemoryGovernor.Register(_memoryClient);
    }

    /// <summary>
    /// 上传并显示一张图(传 null 清屏)。只接受线性 RGBA16F,其余编码/色彩空间直接拒绝。
    ///
    /// 三条路径:
    /// - 当前纹理或最近帧缓存里已有同一 <see cref="DecodedImage"/> 实例(引用相等)时直接复用 GPU 纹理,不上传。
    ///   实例身份就代表像素内容;缓存只持弱引用,不延长像素租约(纹理是独立的 GPU 副本)。
    /// - 像素数据小于 <see cref="BackgroundUploadBytes"/>:UI 线程同步建纹理 → UpdateSubresource → mip 链。
    /// - 更大:上一帧保持不变继续上屏,系统内存暂存纹理由后台线程逐行填满;
    ///   回到 UI 线程后只做「建带 mip 纹理 → 拷 mip0 → GenerateMips → 交换」。
    ///   同时只允许一次在途拷贝:切走又切回同一实例就继续用那次在途拷贝,切到别的帧时
    ///   把接力交给最新期望帧(连翻时不会堆积多份 262MB 拷贝);收尾时已不再期望的成果进最近纹理缓存而不上屏。
    ///
    /// STATE-01 / IMAGE-02:缓存命中切图不经过空图,同机型连拍的两张源尺寸与方向相同,只比几何无法区分
    /// 「切图」与「代理↔Full 热替换」,故按 <paramref name="generation"/> 判定:代际变化一律走切图语义,
    /// 代际相同而实例变化(代理→Full)才是热替换。
    /// </summary>
    /// <param name="image">已解码图像;null 表示清空画面。</param>
    /// <param name="generation">会话照片代际(<see cref="PhotoLoadSession.Generation"/>)。</param>
    public void SetImage(DecodedImage? image, int generation)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (image != null)
        {
            if (image.Encoding != PixelEncoding.RgbaF16 || image.ColorSpace != ImageColorSpace.LinearSrgb)
                throw new ArgumentException("Windows 主图必须为线性 RGBA16F", nameof(image));
            if (image.Pixels.Length == 0) throw new ArgumentException("像素租约已归零", nameof(image));
        }

        TrackWanted(image, generation);
        // 期望上屏的帧按实例记录:在途上传据此判断"还要不要"。
        _wanted = image == null ? null : new WeakReference<DecodedImage>(image);
        var key = _wanted;
        bool cut = generation != _generation;
        _generation = generation;
        RefreshUploadPending();

        // 会话的加载态/增强态等属性变化会带着同一帧重复触发 Changed;同一实例、同一代际没有任何 GPU 工作可做。
        if (image != null && _current?.Matches(image) == true)
        {
            if (!cut) return;

            // STATE-01:重新选中同一文件命中同一缓存实例 —— 纹理无需重传,但几何必须按切图重算
            // (适应态重新适应、跟随对焦居中到新照片的对焦点)。
            _viewport.BeginImageChange();
            _viewport.ApplyImage(image.SourceWidth, image.SourceHeight, image.Orientation, _current.Width);
            WindowsUploadStats.RecordCacheHit();
            Draw();
            NotifyViewportChanged();
            return;
        }

        // IMAGE-02：切图走共享 ApplyImage（适应态重新适应 / 跟随对焦 / 保持同一归一化中心）；
        // 代理↔Full 的源尺寸与对焦点相同，还原结果与热替换一致。
        _viewport.BeginImageChange();

        // 同一实例的后台拷贝仍在途(切走又切回):不重传,等它完成即可;收尾时按切图语义应用几何。
        if (key != null && _stagedUpload != null && Matches(_stagedUpload.Key, key))
        {
            return;
        }

        if (image == null || key == null)
        {
            BindTexture(null);
            _viewport.ClearTexture();
            Draw();
            NotifyViewportChanged();
            return;
        }

        if (TakeCached(image) is { } cached)
        {
            BindTexture(cached);
            _viewport.ApplyImage(image.SourceWidth, image.SourceHeight, image.Orientation, image.Width);
            WindowsUploadStats.RecordCacheHit();
            NativeStartupLog.Write($"photo view: 复用纹理 纹理={image.Width}x{image.Height} 缓存={_recentTextures.Count} " +
                $"视口={PhysicalViewport.Width}x{PhysicalViewport.Height}");
            Draw();
            NotifyViewportChanged();
            return;
        }

        if (image.ByteCount < BackgroundUploadBytes)
        {
            BindTexture(UploadSynchronously(image, key));
            _viewport.ApplyImage(image.SourceWidth, image.SourceHeight, image.Orientation, image.Width);
            NativeStartupLog.Write($"photo view: 上传照片 纹理={image.Width}x{image.Height} 源={image.SourceWidth}x{image.SourceHeight} " +
                $"方向={image.Orientation} 显示={DisplayWidth}x{DisplayHeight} 视口={PhysicalViewport.Width}x{PhysicalViewport.Height}");
            Draw();
            NotifyViewportChanged();
            return;
        }

        // 大图:同时只允许一次在途拷贝。已有别的帧在途时先让画面停在上一帧,
        // 那次拷贝收尾时会自动接力上传当前期望帧(连翻时不会把几十份 262MB 拷贝一起跑)。
        TryStartWantedUpload();
        if (Volatile.Read(ref _uploadPending) != 0)
        {
            return;
        }

        // 接力失败(实例已回收等):保持上一帧,上报状态即可。
        NativeStartupLog.Write("photo view: 大图上传未能启动,保持上一帧");
    }


    /// <summary>
    /// 主图纹理描述:自动 mip 链(GENERATE_MIPS,三线性采样用)。
    /// </summary>
    /// <param name="width">纹理宽</param>
    /// <param name="height">纹理高</param>
    /// <returns>纹理描述</returns>
    private static D3D11_TEXTURE2D_DESC MippedTextureDescription(uint width, uint height) => new()
    {
        Width = width,
        Height = height,
        MipLevels = 0,
        ArraySize = 1,
        Format = DXGI_FORMAT.DXGI_FORMAT_R16G16B16A16_FLOAT,
        SampleDesc = new DXGI_SAMPLE_DESC { Count = 1, Quality = 0 },
        Usage = D3D11_USAGE.D3D11_USAGE_DEFAULT,
        BindFlags = (uint)(D3D11_BIND_FLAG.D3D11_BIND_SHADER_RESOURCE | D3D11_BIND_FLAG.D3D11_BIND_RENDER_TARGET),
        MiscFlags = (uint)D3D11_RESOURCE_MISC_FLAG.D3D11_RESOURCE_MISC_GENERATE_MIPS,
    };

    /// <summary>
    /// UI 线程同步上传(小图):建纹理、整幅写入、建视图与 mip 链,分段计时进常驻统计。
    /// UpdateSubresource/GenerateMips 是同步档的 GPU 命令提交,这里量的是 CPU 提交成本(不含 GPU 完成)。
    /// </summary>
    /// <param name="image">源图(调用方保证像素租约有效)</param>
    /// <param name="key">缓存键(源图实例的弱引用)</param>
    /// <returns>可绑定的纹理条目</returns>
    private unsafe TextureEntry UploadSynchronously(DecodedImage image, WeakReference<DecodedImage> key)
    {
        long started = Stopwatch.GetTimestamp();
        var entry = TakeSpare(image.Width, image.Height);
        double createMs = 0;
        if (entry is null)
        {
            var texture = _device.CreateTexture2D(MippedTextureDescription((uint)image.Width, (uint)image.Height), null);
            try
            {
                var view = _device.CreateShaderResourceView(texture, null);
                entry = new TextureEntry(key, texture, view, image.Width, image.Height);
            }
            catch
            {
                texture.Dispose();
                throw;
            }
            createMs = WindowsUploadStats.Elapsed(started);
        }
        else
        {
            entry.Rebind(key);
            WindowsUploadStats.RecordReuse();
        }

        long copied = Stopwatch.GetTimestamp();
        fixed (byte* pixels = image.Pixels)
        {
            _context.Object.UpdateSubresource(entry.Texture.Object, 0, IntPtr.Zero, (IntPtr)pixels, (uint)image.Stride, 0);
        }
        double updateMs = WindowsUploadStats.Elapsed(copied);
        long mipsStarted = Stopwatch.GetTimestamp();
        _context.Object.GenerateMips(entry.View.Object);
        WindowsUploadStats.Record(new(image.Width, image.Height, false, 0, 0, createMs, updateMs,
            WindowsUploadStats.Elapsed(mipsStarted), 0, WindowsUploadStats.Elapsed(started)));
        return entry;
    }

    /// <summary>取走与目标尺寸一致的空纹理;没有或尺寸不符返回 null。</summary>
    /// <param name="width">目标宽</param>
    /// <param name="height">目标高</param>
    /// <returns>可复用的条目</returns>
    private TextureEntry? TakeSpare(int width, int height)
    {
        var spare = _spareTexture;
        if (spare is null || spare.Width != width || spare.Height != height) return null;
        _spareTexture = null;
        return spare;
    }

    /// <summary>退掉一张不再需要的纹理:留作同尺寸复用的空纹理,已有空纹理或尺寸过大时直接释放。</summary>
    /// <param name="entry">退掉的条目</param>
    private void Retire(TextureEntry entry)
    {
        if (CanRetainTextures && _spareTexture is null && entry.Bytes <= BackgroundUploadBytes)
        {
            entry.Rebind(null);
            _spareTexture = entry;
            return;
        }

        entry.Dispose();
    }

    /// <summary>暂存纹理描述:系统内存可写、只作 GPU 内拷贝来源(不绑定任何管线阶段)。</summary>
    /// <param name="width">纹理宽</param>
    /// <param name="height">纹理高</param>
    /// <returns>纹理描述</returns>
    private static D3D11_TEXTURE2D_DESC StagingTextureDescription(uint width, uint height) => new()
    {
        Width = width,
        Height = height,
        MipLevels = 1,
        ArraySize = 1,
        Format = DXGI_FORMAT.DXGI_FORMAT_R16G16B16A16_FLOAT,
        SampleDesc = new DXGI_SAMPLE_DESC { Count = 1, Quality = 0 },
        Usage = D3D11_USAGE.D3D11_USAGE_STAGING,
        BindFlags = 0,
        CPUAccessFlags = (uint)D3D11_CPU_ACCESS_FLAG.D3D11_CPU_ACCESS_WRITE,
        MiscFlags = 0,
    };

    /// <summary>
    /// 大图上传的第一段(UI 线程):建系统内存暂存纹理并映射一次,不拷任何像素;
    /// 整幅像素随后由后台线程逐行写进映射内存。
    ///
    /// 为什么不是"后台线程自己 CreateTexture2D":DirectN 的 COM 封装是经典 RCW,
    /// D3D11 接口没有注册 proxy/stub,跨套间调用会 E_NOINTERFACE(实测),
    /// 所以后台线程只能做纯指针写,任何 D3D/COM 调用都必须留在 UI 线程。
    /// </summary>
    /// <param name="key">源图实例的弱引用(缓存键)</param>
    /// <param name="image">源图(读取尺寸)</param>
    private unsafe void BeginStagedUpload(WeakReference<DecodedImage> key, DecodedImage image)
    {
        if (!image.TryAcquire()) return;
        IComObject<ID3D11Texture2D>? staging = null;
        bool transferred = false;
        long started = Stopwatch.GetTimestamp();
        try
        {
            staging = _device.CreateTexture2D(StagingTextureDescription((uint)image.Width, (uint)image.Height), null);
            double stagingMs = WindowsUploadStats.Elapsed(started);
            long mapStarted = Stopwatch.GetTimestamp();
            var mapped = _context.Object.Map(staging.Object, 0, D3D11_MAP.D3D11_MAP_WRITE, 0);
            double mapMs = WindowsUploadStats.Elapsed(mapStarted);
            if (mapped.pData == IntPtr.Zero) throw new InvalidOperationException("主图暂存纹理映射失败");
            var upload = new StagedUpload(key, image, staging, started, stagingMs,
                mapMs, checked((long)mapped.RowPitch * image.Height));
            staging = null;
            _stagedUpload = upload;
            transferred = true;
            Volatile.Write(ref upload.Copying, 1);
            UpdateGpuBytes();
            var destination = mapped.pData;
            var rowPitch = checked((int)mapped.RowPitch);
            _ = Task.Run(() => CopyIntoStaging(upload, destination, rowPitch));
            RefreshUploadPending();
        }
        catch (Exception ex)
        {
            NativeStartupLog.Fail("photo view staged upload", ex);
            staging?.Dispose();
            RefreshUploadPending();
        }
        finally
        {
            if (!transferred) image.Release();
        }
    }

    /// <summary>
    /// 大图上传的第二段(后台线程):把源像素逐行写进映射内存。纯指针写,不碰任何 D3D/COM 对象;
    /// 像素存活由拷贝期间持有的源图租约保证。
    /// </summary>
    /// <param name="upload">本次上传的暂存纹理</param>
    /// <param name="destination">映射内存首地址</param>
    /// <param name="rowPitch">映射内存行距(可能与源 stride 不同)</param>
    private void CopyIntoStaging(StagedUpload upload, IntPtr destination, int rowPitch)
    {
        long started = Stopwatch.GetTimestamp();
        try
        {
            CopyRows(upload.Source, destination, rowPitch);
            upload.Copied = true;
        }
        catch (Exception ex)
        {
            NativeStartupLog.Fail("photo view staged copy", ex);
        }
        finally
        {
            upload.Source.Release();
            Volatile.Write(ref upload.Copying, 0);
        }
        var backgroundMs = WindowsUploadStats.Elapsed(started);
        if (!TryPostToUiThread(() => CompleteStagedUpload(upload, backgroundMs)))
        {
            // 面板已释放:没人会消费这次结果,后台也不再写映射内存,这里可以直接释放暂存纹理。
            upload.ReleaseTexture();
            Volatile.Write(ref _uploadPending, 0);
        }
    }

    /// <summary>把源图逐行拷进映射内存(行距各自独立)。</summary>
    /// <param name="image">源图(调用方持租约)</param>
    /// <param name="destination">映射内存首地址</param>
    /// <param name="rowPitch">映射内存行距</param>
    private static unsafe void CopyRows(DecodedImage image, IntPtr destination, int rowPitch)
    {
        fixed (byte* source = image.Pixels)
        {
            int rowBytes = Math.Min(rowPitch, image.Stride);
            for (var y = 0; y < image.Height; y++)
            {
                Buffer.MemoryCopy(
                    source + ((long)y * image.Stride),
                    (void*)(destination + (y * rowPitch)),
                    rowBytes,
                    rowBytes);
            }
        }
    }

    /// <summary>
    /// 大图上传的第三段(UI 线程):解映射、建带 mip 的纹理、把暂存纹理拷进 mip0、生成 mip 链后整体交换上屏。
    /// 已切到别的帧时不上屏,但成果直接进最近纹理缓存 —— 来回对比时下一次切回来就是零成本命中,
    /// 否则"请求快于一次拷贝"时每份成果都会被丢掉,永远出不了图。
    /// </summary>
    /// <param name="upload">本次上传的暂存纹理</param>
    /// <param name="backgroundMs">后台逐行拷贝耗时(ms)</param>
    private void CompleteStagedUpload(StagedUpload upload, double backgroundMs)
    {
        IComObject<ID3D11Texture2D>? mipped = null;
        IComObject<ID3D11ShaderResourceView>? view = null;
        try
        {
            var staging = upload.Texture;
            if (_disposed) return;
            _context.Object.Unmap(staging.Object, 0);
            if (!upload.Copied || (!IsWanted(upload.Key) && !CanRetainTextures)) return;
            var image = upload.Source;
            long started = Stopwatch.GetTimestamp();
            mipped = _device.CreateTexture2D(MippedTextureDescription((uint)image.Width, (uint)image.Height), null);
            double createMs = WindowsUploadStats.Elapsed(started);
            long copied = Stopwatch.GetTimestamp();
            _context.Object.CopySubresourceRegion(mipped.Object, 0, 0, 0, 0, staging.Object, 0, IntPtr.Zero);
            double updateMs = WindowsUploadStats.Elapsed(copied);
            long mipsStarted = Stopwatch.GetTimestamp();
            view = _device.CreateShaderResourceView(mipped, null);
            _context.Object.GenerateMips(view.Object);
            double mipsMs = WindowsUploadStats.Elapsed(mipsStarted);
            WindowsUploadStats.Record(new(image.Width, image.Height, true, upload.StagingMs, upload.MapMs,
                createMs, updateMs, mipsMs, backgroundMs, WindowsUploadStats.Elapsed(upload.Started)));

            var entry = new TextureEntry(upload.Key, mipped, view, image.Width, image.Height);
            mipped = null;
            view = null;
            if (!IsWanted(upload.Key))
            {
                Remember(entry);
                return;
            }
            BindTexture(entry);
            _viewport.ApplyImage(image.SourceWidth, image.SourceHeight, image.Orientation, image.Width);
            Draw();
            NotifyViewportChanged();
        }
        catch (Exception ex)
        {
            NativeStartupLog.Fail("photo view upload complete", ex);
        }
        finally
        {
            view?.Dispose();
            mipped?.Dispose();
            upload.ReleaseTexture();
            if (ReferenceEquals(_stagedUpload, upload)) _stagedUpload = null;
            if (!_disposed)
            {
                UpdateGpuBytes();
                TryStartWantedUpload();
                RefreshUploadPending();
            }
        }
    }

    /// <summary>给定帧是否仍是当前期望上屏的那一帧(在途上传据此决定收尾时是否还要交换上屏)。</summary>
    /// <param name="key">待判断帧的弱引用</param>
    /// <returns>仍是期望帧时为 true</returns>
    private bool IsWanted(WeakReference<DecodedImage> key) => _wanted != null && Matches(_wanted, key);

    /// <summary>两个实例弱引用是否指向同一个 <see cref="DecodedImage"/>。</summary>
    /// <param name="left">其一</param>
    /// <param name="right">其二</param>
    /// <returns>引用相等时为 true</returns>
    private static bool Matches(WeakReference<DecodedImage> left, WeakReference<DecodedImage> right)
        => left.TryGetTarget(out var a) && right.TryGetTarget(out var b) && ReferenceEquals(a, b);

    /// <summary>
    /// 为当前期望帧启动一次大图后台拷贝(同时最多一次在途)。
    /// 小图、已在屏上、已有缓存或已有在途拷贝时直接返回。
    /// </summary>
    private void TryStartWantedUpload()
    {
        if (_disposed || _stagedUpload != null || _wanted == null) return;
        if (!_wanted.TryGetTarget(out var image)) return;
        if (_current?.Matches(image) == true || image.Pixels.Length == 0) return;
        if (image.ByteCount < BackgroundUploadBytes) return;
        if (TakeCached(image) is { } cached)
        {
            // 上一次拷贝其实就是这一帧(例如 A→B→A):命中最近纹理,直接上屏。
            BindTexture(cached);
            _viewport.ApplyImage(image.SourceWidth, image.SourceHeight, image.Orientation, image.Width);
            WindowsUploadStats.RecordCacheHit();
            Draw();
            NotifyViewportChanged();
            return;
        }

        BeginStagedUpload(_wanted, image);
    }

    /// <summary>按"当前期望帧是否有在途上传"刷新 <see cref="IsUploadPending"/>。</summary>
    private void RefreshUploadPending()
    {
        bool pending = _wanted?.TryGetTarget(out var image) == true && _current?.Matches(image) != true;
        Volatile.Write(ref _uploadPending, pending ? 1 : 0);
    }

    /// <summary>把动作投递回 UI 线程(与上层同一调度门面);调度器未就绪/已关闭时返回 false。</summary>
    /// <param name="action">UI 线程动作</param>
    /// <returns>投递成功为 true</returns>
    private static bool TryPostToUiThread(Action action)
    {
        try
        {
            PhotoViewer.Core.Platform.UiDispatcher.Post(action);
            return true;
        }
        catch (Exception ex)
        {
            NativeStartupLog.Fail("photo view post to ui thread", ex);
            return false;
        }
    }

    /// <summary>把纹理条目绑到画面:退下来的旧条目转入最近缓存(实例已回收则直接释放)。</summary>
    /// <param name="entry">新条目;null 表示清空</param>
    private void BindTexture(TextureEntry? entry)
    {
        var previous = _current;
        if (!ReferenceEquals(previous, entry))
        {
            _current = entry;
            if (previous != null)
            {
                // 先解绑再释放:像素着色器仍持有视图引用时释放资源会留下悬空绑定。
                _context.Object.PSSetShaderResources(0, 1, NoShaderResources);
                Remember(previous);
            }
        }

        UpdateGpuBytes();
        RefreshUploadPending();
    }

    /// <summary>取走最近缓存里与给定实例同一对象的纹理条目(命中即从缓存摘下)。</summary>
    /// <param name="image">目标实例</param>
    /// <returns>命中的条目;未命中为 null</returns>
    private TextureEntry? TakeCached(DecodedImage image)
    {
        for (var index = _recentTextures.Count - 1; index >= 0; index--)
        {
            var candidate = _recentTextures[index];
            if (!candidate.Matches(image)) continue;
            _recentTextures.RemoveAt(index);
            UpdateGpuBytes();
            return candidate;
        }

        return null;
    }

    private static bool CanRetainTextures => MemoryGovernor.CurrentLevel == PressureLevel.Normal
        && !MemoryGovernor.InRecoveryHold;

    /// <summary>把退下来的纹理收进最近缓存,超出容量淘汰最久未用的一张。</summary>
    /// <param name="entry">退下来的条目</param>
    private void Remember(TextureEntry entry)
    {
        if (!CanRetainTextures)
        {
            entry.Dispose();
            return;
        }
        // 实例已被回收的条目再也不会有命中机会,顺手清掉(数量上限只有两张)。
        for (var index = _recentTextures.Count - 1; index >= 0; index--)
        {
            if (_recentTextures[index].IsAlive) continue;
            var dead = _recentTextures[index];
            _recentTextures.RemoveAt(index);
            Retire(dead);
        }

        if (!entry.IsAlive)
        {
            Retire(entry);
            return;
        }

        _recentTextures.Add(entry);
        while (_recentTextures.Count > RecentTextureCapacity)
        {
            var oldest = _recentTextures[0];
            _recentTextures.RemoveAt(0);
            Retire(oldest);
        }
    }

    /// <summary>刷新内存管家读取的 GPU 纹理占用(当前 + 最近缓存 + 复用空纹理的字节,含 mip 链;另含标题栏背板纹理)。</summary>
    private void UpdateGpuBytes()
    {
        long total = _current?.Bytes ?? 0;
        foreach (var entry in _recentTextures) total += entry.Bytes;
        total += _spareTexture?.Bytes ?? 0;
        total += _bandBytes;
        total += _stagedUpload?.Bytes ?? 0;
        Interlocked.Exchange(ref _gpuBytes, total);
    }

    /// <summary>内存压力下的修剪请求(可能在探测线程调用):只标记,清场交给 UI 线程。</summary>
    private void RequestTrim()
    {
        Interlocked.Exchange(ref _trimRequested, 1);
        TryPostToUiThread(TrimCachedTextures);
    }

    /// <summary>丢掉全部非当前纹理(内存压力 Caution 及以上)。</summary>
    private void TrimCachedTextures()
    {
        if (Interlocked.Exchange(ref _trimRequested, 0) == 0 || _disposed) return;
        foreach (var entry in _recentTextures) entry.Dispose();
        _recentTextures.Clear();
        _spareTexture?.Dispose();
        _spareTexture = null;
        UpdateGpuBytes();
    }

    /// <summary>适应视口:整幅照片铺满可见区,居中显示。</summary>
    public void FitToView()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _viewport.FitToView();
        Draw();
        NotifyViewportChanged();
    }

    /// <summary>
    /// 在“适应”与“100%”之间切换;切到 100% 时优先回到照片对焦点,否则以给定视口坐标(默认视口中心)为锚点。
    /// </summary>
    /// <param name="anchorX">锚点 X(逻辑单位);null 取视口中心或对焦点。</param>
    /// <param name="anchorY">锚点 Y(逻辑单位);null 取视口中心或对焦点。</param>
    public void ToggleFit(double? anchorX = null, double? anchorY = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _viewport.ToggleFit(anchorX.HasValue && anchorY.HasValue ? (anchorX.Value, anchorY.Value) : null);
        Draw();
        NotifyViewportChanged();
    }

    /// <summary>以视口坐标为锚点按比例缩放。</summary>
    /// <param name="factor">缩放倍率。</param>
    /// <param name="anchorX">锚点 X(逻辑单位);null 取视口中心。</param>
    /// <param name="anchorY">锚点 Y(逻辑单位);null 取视口中心。</param>
    public void ZoomBy(double factor, double? anchorX = null, double? anchorY = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _viewport.ZoomBy(factor, anchorX.HasValue && anchorY.HasValue ? (anchorX.Value, anchorY.Value) : null);
        Draw();
        NotifyViewportChanged();
    }

    /// <summary>精确滚动(触控板/高精度滚轮)平移照片;平移不改变视口与 LOD 需求,故不上报。</summary>
    /// <param name="deltaX">横向位移(逻辑单位)。</param>
    /// <param name="deltaY">纵向位移(逻辑单位)。</param>
    public void PanBy(double deltaX, double deltaY)
    {
        if (_disposed || _viewport.IsFit || _current == null)
        {
            return;
        }

        _viewport.PanBy(deltaX, deltaY);
        Draw();
    }

    /// <summary>释放全部 GPU 资源(按创建逆序),不触碰可视树。</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        // 不再有期望帧:在途拷贝收尾时不会交换上屏(暂存纹理按是否仍在写映射内存决定能否立即释放)。
        _wanted = null;
        Volatile.Write(ref _uploadPending, 0);
        if (_memoryClient != null)
        {
            MemoryGovernor.Unregister(_memoryClient);
            _memoryClient = null;
        }

        CastExtensions.As<ISwapChainPanelNativeWinUI>(this).SetSwapChain(null).ThrowOnError();
        _context.Object.ClearState();
        // 后台仍在写映射内存的暂存纹理不能解映射/释放(会让后台线程写进已释放内存),留给进程回收;
        // 已经写完的直接清掉。
        if (_stagedUpload is { } staged)
        {
            _stagedUpload = null;
            if (Volatile.Read(ref staged.Copying) == 0)
            {
                _context.Object.Unmap(staged.Texture.Object, 0);
                staged.ReleaseTexture();
            }
        }

        _current?.Dispose();
        _current = null;
        foreach (var entry in _recentTextures) entry.Dispose();
        _recentTextures.Clear();
        _spareTexture?.Dispose();
        _spareTexture = null;
        _titleBarBand = null;
        ReleaseBandTextures();
        DisposeOutputColor();
        Interlocked.Exchange(ref _gpuBytes, 0);
        _renderTarget?.Dispose();
        _swapChain.Dispose();
        _noiseView?.Dispose();
        _noiseTexture?.Dispose();
        _noiseSampler.Dispose();
        _bandParameters.Dispose();
        _bandAcrylicShader.Dispose();
        _bandBlurShader.Dispose();
        _bandDownsampleShader.Dispose();
        _bandVertexShader.Dispose();
        _parameters.Dispose();
        _sampler.Dispose();
        _pixelShader.Dispose();
        _vertexShader.Dispose();
        _context.Dispose();
        _device.Dispose();
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
            _viewport.ResizeView(ActualWidth, ActualHeight, raster);
            _laidOut = true;
            uint width = (uint)Math.Round(ActualWidth * raster);
            uint height = (uint)Math.Round(ActualHeight * raster);
            if (width != _pixelWidth || height != _pixelHeight)
            {
                _context.Object.OMSetRenderTargets(0, null, null);
                _renderTarget?.Dispose();
                _renderTarget = null;
                _swapChain.ResizeBuffers(BufferCount, width, height, DXGI_FORMAT.DXGI_FORMAT_R16G16B16A16_FLOAT, 0);
                _pixelWidth = width;
                _pixelHeight = height;
                CreateRenderTarget();
            }
            ApplyCompositionTransform();
            if (_viewport.IsFit) _viewport.FitToView();
            else _viewport.ClampPan();
            Draw();
            NotifyViewportChanged();
        }
        catch (Exception ex)
        {
            NativeStartupLog.Fail("photo view " + stage, ex);
        }
    }

    /// <summary>为 D3D11 自动轮转的 buffer 0 建立渲染目标；仅在 ResizeBuffers 后重建。</summary>
    private void CreateRenderTarget()
    {
        using var buffer = _swapChain.GetBuffer<ID3D11Texture2D>(0);
        _renderTarget = _device.CreateRenderTargetView(buffer, null);
    }

    /// <summary>
    /// 启动期记一条图形拓扑：D3D11 设备落在哪个适配器、各适配器有哪些输出（谁接着显示器）。
    /// 设备跟随"接了输出的适配器",远程/虚拟显示会话里输出挂在核显上时设备也落在核显(与解码争系统内存),
    /// 这类问题只能从这条日志判断，不涉及任何每帧开销。
    /// </summary>
    private void LogDisplayTopology()
    {
        try
        {
            using var dxgiDevice = _device.AsComObject<IDXGIDevice1>(true);
            using var adapter = dxgiDevice.GetAdapter();
            var device = adapter.GetDesc();
            var text = new StringBuilder(256);
            text.Append("photo view: 设备适配器=").Append(device.Description)
                .Append(" vendor=0x").Append(device.VendorId.ToString("X4"))
                .Append(" luid=").Append(device.AdapterLuid.HighPart.ToString("X8")).Append(':').Append(device.AdapterLuid.LowPart.ToString("X8"))
                .Append(" dedicatedMB=").Append(device.DedicatedVideoMemory.ToInt64() >> 20);
            using var factory = adapter.GetFactory2();
            var index = 0;
            foreach (var each in factory.EnumAdapters())
            {
                using (each)
                {
                    var desc = each.GetDesc();
                    text.Append(" | [").Append(index).Append("] ").Append(desc.Description);
                    foreach (var output in each.EnumOutputs())
                    {
                        using (output)
                        {
                            var od = output.GetDesc();
                            text.Append(' ').Append(od.DeviceName)
                                .Append(' ').Append(od.DesktopCoordinates.right - od.DesktopCoordinates.left)
                                .Append('x').Append(od.DesktopCoordinates.bottom - od.DesktopCoordinates.top)
                                .Append(" attached=").Append(od.AttachedToDesktop);
                        }
                    }
                }

                index++;
            }

            NativeStartupLog.Write(text.ToString());
        }
        catch (Exception ex)
        {
            NativeStartupLog.Fail("display topology", ex);
        }
    }

    /// <summary>告诉合成器交换链缓冲已是物理像素,抵消 XAML 再乘一次的合成缩放。</summary>
    private void ApplyCompositionTransform()
    {
        double raster = PhysicalPixelsPerDip;
        var matrix = new DXGI_MATRIX_3X2_F { _11 = (float)(1 / raster), _22 = (float)(1 / raster) };
        _swapChain.Object.SetMatrixTransform(ref matrix).ThrowOnError();
    }

    /// <summary>按当前几何重绘一帧并呈现;无纹理时只清屏。</summary>
    private void Draw()
    {
        if (_disposed || _renderTarget == null) return;
        long started = Stopwatch.GetTimestamp();
        RenderScene();
        double renderMs = WindowsUploadStats.Elapsed(started);
        long presentStarted = Stopwatch.GetTimestamp();
        _swapChain.Present(1, 0);
        WindowsUploadStats.RecordDraw(renderMs, WindowsUploadStats.Elapsed(presentStarted));
        NotifyFrameSubmitted();
        RenderStateChanged?.Invoke();
    }

    /// <summary>只把当前几何画进后台缓冲(不呈现);回读像素的验收入口在呈现前调用它。</summary>
    private unsafe void RenderScene()
    {
        if (_disposed || _renderTarget == null) return;
        _context.Object.OMSetRenderTargets(1, [_renderTarget.Object], null);
        _context.Object.ClearRenderTargetView(_renderTarget.Object, ClearColor);
        if (_current is { } current)
        {
            _parameterData[0] = _pixelWidth;
            _parameterData[1] = _pixelHeight;
            _parameterData[4] = (float)DisplayWidth;
            _parameterData[5] = (float)DisplayHeight;
            _parameterData[8] = (float)_viewport.Scale;
            _parameterData[9] = (float)_viewport.PanX;
            _parameterData[10] = (float)_viewport.PanY;
            _parameterData[11] = _viewport.Orientation;
            _parameterData[12] = (float)PhysicalPixelsPerDip;
            fixed (float* pointer = _parameterData)
                _context.Object.UpdateSubresource(_parameters.Object, 0, IntPtr.Zero, (IntPtr)pointer, 0, 0);

            _context.Object.RSSetViewports(1, [new D3D11_VIEWPORT { Width = _pixelWidth, Height = _pixelHeight, MinDepth = 0, MaxDepth = 1 }]);
            _context.Object.IASetPrimitiveTopology(D3D_PRIMITIVE_TOPOLOGY.D3D_PRIMITIVE_TOPOLOGY_TRIANGLESTRIP);
            _context.Object.VSSetShader(_vertexShader.Object, null, 0);
            _context.Object.PSSetShader(_pixelShader.Object, null, 0);
            _context.Object.VSSetConstantBuffers(0, 1, [_parameters.Object]);
            _context.Object.PSSetShaderResources(0, 1, [current.View.Object]);
            _context.Object.PSSetSamplers(0, 1, [_sampler.Object]);
            _context.Object.Draw(4, 0);
        }

        // 标题栏亚克力区段最后画:它取的就是这一帧照片(含黑底)的实际像素。
        DrawTitleBarBackdrop();
        RenderOutputColor();
    }

    /// <summary>
    /// 设置标题栏压在照片上的区段(<paramref name="bandDip"/> 用本视图的 DIP 坐标,null 表示不画)。
    /// WinUI 3 的 <c>SwapChainPanel</c> 取不到 <c>AcrylicBrush</c> 背板,该区段的亚克力由这里在交换链内绘制。
    /// 每次正常绘制都按当前几何重画,视口/缩放/换图变化不必重新设置;取值未变时不重绘。
    /// </summary>
    /// <param name="bandDip">区段(本视图 DIP 坐标);null 关闭。</param>
    /// <param name="recipe">亚克力配方(主题画刷的运行时取值)。</param>
    /// <param name="edges">区段两侧的邻接关系(补色与接缝渐变)。</param>
    /// <param name="noiseOriginDip">噪点平铺原点(本视图 DIP 坐标,取整条标题栏的左上角)。</param>
    public void SetTitleBarBackdrop(Rect? bandDip, AcrylicBackdropRecipe recipe, TitleBarBackdropEdges edges, Point noiseOriginDip)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        TitleBarBandRequest? next = bandDip is { Width: > 0.5, Height: > 0.5 } band
            ? new TitleBarBandRequest(band, recipe, edges, noiseOriginDip)
            : null;
        if (_titleBarBand is { } current)
        {
            if (next is { } candidate && current.Band == candidate.Band && current.Recipe.NearlyEquals(candidate.Recipe)
                && current.Edges == candidate.Edges && current.NoiseOrigin == candidate.NoiseOrigin)
            {
                return;
            }
        }
        else if (next == null)
        {
            return;
        }

        _titleBarBand = next;
        Draw();
    }

    /// <summary>标题栏背板区段的一次请求:区段、配方、邻接关系与噪点原点(均为本视图 DIP 坐标)。</summary>
    /// <param name="Band">区段</param>
    /// <param name="Recipe">亚克力配方</param>
    /// <param name="Edges">两侧邻接关系</param>
    /// <param name="NoiseOrigin">噪点平铺原点</param>
    private readonly record struct TitleBarBandRequest(Rect Band, AcrylicBackdropRecipe Recipe, TitleBarBackdropEdges Edges, Point NoiseOrigin);

    /// <summary>当前标题栏背板区段(本视图 DIP 坐标);null 表示未开启。</summary>
    internal Rect? TitleBarBackdropBand => _titleBarBand?.Band;

    /// <summary>当前标题栏背板区段的两侧邻接关系(验收用);未开启时为 null。</summary>
    internal TitleBarBackdropEdges? TitleBarBackdropEdges => _titleBarBand?.Edges;

    /// <summary>WinUI 亚克力噪点纹理是否已载入(缺资源时区段不叠噪点,只记一次诊断)。</summary>
    internal bool HasTitleBarNoise => _noiseView != null;

    /// <summary>
    /// 在已画好照片的后台缓冲上补标题栏亚克力区段(SHELL-02):复制区域 → 降采样并转 sRGB(XAML 一侧越界补
    /// FallbackColor)→ 可分离高斯 → 按配方做亮度混合与颜色混合、贴 XAML 亚克力段一侧渐变、叠 2% 噪点,写回同一后台缓冲。
    /// 只碰 buffer 0 与自建临时纹理,不改变「单张可渲染缓冲」的既有约束。
    /// </summary>
    private unsafe void DrawTitleBarBackdrop()
    {
        if (_bandFailed || _renderTarget == null || _titleBarBand is not { } request) return;
        if (_pixelWidth == 0 || _pixelHeight == 0) return;

        try
        {
            _bandClock.Restart();
            double scale = PhysicalPixelsPerDip;
            var band = request.Band;
            int viewportWidth = (int)_pixelWidth;
            int viewportHeight = (int)_pixelHeight;
            int left = Math.Clamp((int)Math.Round(band.X * scale), 0, viewportWidth);
            int top = Math.Clamp((int)Math.Round(band.Y * scale), 0, viewportHeight);
            int right = Math.Clamp((int)Math.Round((band.X + band.Width) * scale), 0, viewportWidth);
            int bottom = Math.Clamp((int)Math.Round((band.Y + band.Height) * scale), 0, viewportHeight);
            if (right <= left || bottom <= top) return;

            // 模糊会带进邻域:逻辑源区按 3σ 外扩;真实像素只取视口内那部分,越界处窗口边钳边缘、XAML 一侧补 FallbackColor。
            float sigma = (float)(WindowsLayout.TitleBarBackdropBlurAmount * scale);
            int margin = (int)Math.Ceiling(3 * (double)sigma);
            int logicalLeft = left - margin;
            int logicalTop = top - margin;
            int logicalRight = right + margin;
            int logicalBottom = bottom + margin;
            int copyLeft = Math.Max(0, logicalLeft);
            int copyTop = Math.Max(0, logicalTop);
            int copyRight = Math.Min(viewportWidth, logicalRight);
            int copyBottom = Math.Min(viewportHeight, logicalBottom);
            uint logicalWidth = (uint)(logicalRight - logicalLeft);
            uint logicalHeight = (uint)(logicalBottom - logicalTop);
            uint copyWidth = (uint)(copyRight - copyLeft);
            uint copyHeight = (uint)(copyBottom - copyTop);
            if (copyWidth == 0 || copyHeight == 0) return;

            var created = EnsureBandTextures(copyWidth, copyHeight, logicalWidth, logicalHeight);
            if (_bandSource is null || _bandScratch[0] is null || _bandScratch[1] is null) return;
            EnsureNoiseTexture();
            uint reducedWidth = _bandScratchSize.Width;
            uint reducedHeight = _bandScratchSize.Height;
            var recipe = request.Recipe;
            var edges = request.Edges;

            using var backBuffer = _swapChain.GetBuffer<ID3D11Texture2D>(0);
            _context.Object.OMSetRenderTargets(0, null, null);
            _context.Object.PSSetShaderResources(0, 1, NoShaderResources);

            // 1) 后台缓冲的真实像素区域 → 背板源纹理。
            var box = new Box3D
            {
                Left = (uint)copyLeft,
                Top = (uint)copyTop,
                Front = 0,
                Right = (uint)copyRight,
                Bottom = (uint)copyBottom,
                Back = 1,
            };
            _context.Object.CopySubresourceRegion(_bandSource.Object, 0, 0, 0, 0, backBuffer.Object, 0, (IntPtr)(&box));

            _context.Object.IASetPrimitiveTopology(D3D_PRIMITIVE_TOPOLOGY.D3D_PRIMITIVE_TOPOLOGY_TRIANGLESTRIP);
            _context.Object.VSSetShader(_bandVertexShader.Object, null, 0);
            _context.Object.VSSetConstantBuffers(1, 1, [_bandParameters.Object]);
            _context.Object.PSSetConstantBuffers(1, 1, [_bandParameters.Object]);
            _context.Object.PSSetSamplers(0, 1, [_sampler.Object]);
            _context.Object.PSSetSamplers(1, 1, [_noiseSampler.Object]);

            // 配方与邻接参数:三个阶段共用,先写好(WriteBandParameters 只改 0–15 号槽)。
            _bandParameterData[16] = recipe.Tint.R;
            _bandParameterData[17] = recipe.Tint.G;
            _bandParameterData[18] = recipe.Tint.B;
            _bandParameterData[19] = recipe.TintOpacity;
            _bandParameterData[20] = recipe.Tint.R;
            _bandParameterData[21] = recipe.Tint.G;
            _bandParameterData[22] = recipe.Tint.B;
            _bandParameterData[23] = recipe.HasLuminosityLayer ? recipe.LuminosityOpacity : 0f;
            _bandParameterData[24] = recipe.FallbackLinear.R;
            _bandParameterData[25] = recipe.FallbackLinear.G;
            _bandParameterData[26] = recipe.FallbackLinear.B;
            _bandParameterData[27] = recipe.HasLuminosityLayer ? 0f : 1f;
            _bandParameterData[28] = (copyLeft - logicalLeft) / (float)logicalWidth;
            _bandParameterData[29] = (copyRight - logicalLeft) / (float)logicalWidth;
            _bandParameterData[30] = (copyTop - logicalTop) / (float)logicalHeight;
            _bandParameterData[31] = (copyBottom - logicalTop) / (float)logicalHeight;
            _bandParameterData[32] = edges.PadLeft ? 1f : 0f;
            _bandParameterData[33] = edges.PadRight ? 1f : 0f;
            _bandParameterData[34] = 0f;
            _bandParameterData[35] = 0f;
            // 接缝渐变宽度与 XAML 段一侧相同(WindowsLayout.TitleBarBackdropSeamWidth),与模糊在交界处本该带出的过渡同量级。
            _bandParameterData[36] = edges.FadeLeft ? left : -1f;
            _bandParameterData[37] = edges.FadeRight ? right : -1f;
            _bandParameterData[38] = (float)(WindowsLayout.TitleBarBackdropSeamWidth * scale);
            _bandParameterData[39] = 0f;
            // 噪点按 DIP 平铺(WinUI 噪点画刷 1 纹素 = 1 DIP,256 × 256),原点取整条标题栏左上角。
            _bandParameterData[40] = (float)(request.NoiseOrigin.X * scale);
            _bandParameterData[41] = (float)(request.NoiseOrigin.Y * scale);
            _bandParameterData[42] = (float)(1.0 / (NoiseTileSize * scale));
            _bandParameterData[43] = _noiseView != null ? AcrylicBackdropRecipe.NoiseOpacity : 0f;

            // 2) 降采样 4 倍并转 sRGB(逐纹素判断真实像素 / 补色)。
            SetBandTarget(_bandScratchTarget[0]!, reducedWidth, reducedHeight);
            WriteBandParameters(0, 0, reducedWidth, reducedHeight, reducedWidth, reducedHeight, 1, 1, 0, 0,
                1f / logicalWidth, 1f / logicalHeight, 0, 0);
            _context.Object.PSSetShader(_bandDownsampleShader.Object, null, 0);
            _context.Object.PSSetShaderResources(0, 1, [_bandSourceView!.Object]);
            _context.Object.Draw(4, 0);

            // 3) 可分离高斯:水平进第二张小图,垂直回第一张(半径按降采样折算)。
            float reducedSigma = sigma * 0.25f;
            float reducedRadius = (float)Math.Ceiling(3 * (double)reducedSigma);
            for (var pass = 0; pass < 2; pass++)
            {
                var horizontal = pass == 0;
                var source = horizontal ? _bandScratchView[0]! : _bandScratchView[1]!;
                var target = horizontal ? _bandScratchTarget[1]! : _bandScratchTarget[0]!;
                SetBandTarget(target, reducedWidth, reducedHeight);
                WriteBandParameters(0, 0, reducedWidth, reducedHeight, reducedWidth, reducedHeight, 1, 1, 0, 0,
                    horizontal ? 1f / reducedWidth : 0f, horizontal ? 0f : 1f / reducedHeight, reducedSigma, reducedRadius);
                _context.Object.PSSetShaderResources(0, 1, [source.Object]);
                _context.Object.PSSetShader(_bandBlurShader.Object, null, 0);
                _context.Object.Draw(4, 0);
            }

            // 4) 合成到后台缓冲的区段矩形:采样模糊结果,按配方混合,写不透明线性值。
            _context.Object.OMSetRenderTargets(1, [_renderTarget.Object], null);
            _context.Object.RSSetViewports(1, [new D3D11_VIEWPORT { Width = _pixelWidth, Height = _pixelHeight, MinDepth = 0, MaxDepth = 1 }]);
            WriteBandParameters(left, top, right - left, bottom - top, _pixelWidth, _pixelHeight,
                (right - left) / (float)logicalWidth, (bottom - top) / (float)logicalHeight,
                (left - logicalLeft) / (float)logicalWidth, (top - logicalTop) / (float)logicalHeight, 1, 1, 0, 0);
            _context.Object.PSSetShader(_bandAcrylicShader.Object, null, 0);
            _context.Object.PSSetShaderResources(0, 1, [_bandScratchView[0]!.Object]);
            if (_noiseView != null)
            {
                _context.Object.PSSetShaderResources(1, 1, [_noiseView.Object]);
            }

            _context.Object.Draw(4, 0);

            _context.Object.PSSetShaderResources(0, 1, NoShaderResources);
            _context.Object.PSSetShaderResources(1, 1, NoShaderResources);
            _bandClock.Stop();
            // 只报"纹理已就位"的稳态耗时:建纹理那一帧的量级完全不同。
            if (!_bandCostLogged && !created)
            {
                _bandCostLogged = true;
                NativeStartupLog.Write($"title bar backdrop: 稳态绘制耗时 {_bandClock.Elapsed.TotalMilliseconds:0.00}ms"
                    + $" 区段={right - left}x{bottom - top} 源区={copyWidth}x{copyHeight}/{logicalWidth}x{logicalHeight}"
                    + $" 降采样={reducedWidth}x{reducedHeight} sigma={sigma:0.0}px 邻接={edges}");
            }
        }
        catch (Exception ex)
        {
            _bandFailed = true;
            NativeStartupLog.Fail("title bar backdrop", ex);
        }
    }

    /// <summary>WinUI 亚克力噪点资源的边长(纹素;WinUI 按 1 纹素 = 1 DIP 平铺)。</summary>
    private const double NoiseTileSize = 256;

    /// <summary>
    /// 载入 WinUI 随包的亚克力噪点纹理(<c>Microsoft.UI.Xaml/Assets/NoiseAsset_256x256_PNG.png</c>,与 XAML 亚克力同一张),
    /// 只试一次;缺失或解码失败时区段不叠噪点并记一条诊断。
    /// </summary>
    private void EnsureNoiseTexture()
    {
        if (_noiseLoaded) return;
        _noiseLoaded = true;
        var path = Path.Combine(AppContext.BaseDirectory, "Microsoft.UI.Xaml", "Assets", "NoiseAsset_256x256_PNG.png");
        try
        {
            using var decoded = File.Exists(path) ? SKBitmap.Decode(path) : null;
            if (decoded == null)
            {
                NativeStartupLog.Write($"title bar backdrop: 缺少 WinUI 噪点资源 {path},主图区段不叠噪点");
                return;
            }

            using var pixels = decoded.Copy(SKColorType.Rgba8888);
            var description = new D3D11_TEXTURE2D_DESC
            {
                Width = (uint)pixels.Width,
                Height = (uint)pixels.Height,
                MipLevels = 1,
                ArraySize = 1,
                // 不用 _SRGB 格式:混合在 gamma 编码值上做,采样值就要是编码值本身。
                Format = DXGI_FORMAT.DXGI_FORMAT_R8G8B8A8_UNORM,
                SampleDesc = new DXGI_SAMPLE_DESC { Count = 1, Quality = 0 },
                Usage = D3D11_USAGE.D3D11_USAGE_DEFAULT,
                BindFlags = (uint)D3D11_BIND_FLAG.D3D11_BIND_SHADER_RESOURCE,
            };
            _noiseTexture = _device.CreateTexture2D(description, null);
            _context.Object.UpdateSubresource(_noiseTexture.Object, 0, IntPtr.Zero, pixels.GetPixels(), (uint)pixels.RowBytes, 0);
            _noiseView = _device.CreateShaderResourceView(_noiseTexture, null);
        }
        catch (Exception ex)
        {
            _noiseView?.Dispose();
            _noiseView = null;
            _noiseTexture?.Dispose();
            _noiseTexture = null;
            NativeStartupLog.Fail("title bar backdrop noise", ex);
        }
    }

    /// <summary>绑定背板临时纹理为渲染目标并设视口。</summary>
    /// <param name="target">渲染目标视图。</param>
    /// <param name="width">目标宽(物理像素)。</param>
    /// <param name="height">目标高(物理像素)。</param>
    private void SetBandTarget(IComObject<ID3D11RenderTargetView> target, uint width, uint height)
    {
        _context.Object.OMSetRenderTargets(1, [target.Object], null);
        _context.Object.RSSetViewports(1, [new D3D11_VIEWPORT { Width = width, Height = height, MinDepth = 0, MaxDepth = 1 }]);
    }

    /// <summary>写背板常量缓冲的目标矩形与 uv 变换。</summary>
    /// <param name="x">目标左上横坐标。</param>
    /// <param name="y">目标左上纵坐标。</param>
    /// <param name="width">目标宽。</param>
    /// <param name="height">目标高。</param>
    /// <param name="destWidth">目标缓冲宽。</param>
    /// <param name="destHeight">目标缓冲高。</param>
    /// <param name="uvScaleX">uv 横向跨度。</param>
    /// <param name="uvScaleY">uv 纵向跨度。</param>
    /// <param name="uvOffsetX">uv 横向起点。</param>
    /// <param name="uvOffsetY">uv 纵向起点。</param>
    /// <param name="blurScaleX">模糊单步 uv 横向(其他阶段忽略)。</param>
    /// <param name="blurScaleY">模糊单步 uv 纵向(其他阶段忽略)。</param>
    /// <param name="blurSigma">高斯标准差(降采样后)。</param>
    /// <param name="blurRadius">高斯半径。</param>
    private void WriteBandParameters(float x, float y, float width, float height, float destWidth, float destHeight,
        float uvScaleX, float uvScaleY, float uvOffsetX, float uvOffsetY,
        float blurScaleX, float blurScaleY, float blurSigma, float blurRadius)
    {
        _bandParameterData[0] = x;
        _bandParameterData[1] = y;
        _bandParameterData[2] = width;
        _bandParameterData[3] = height;
        _bandParameterData[4] = destWidth;
        _bandParameterData[5] = destHeight;
        _bandParameterData[6] = 0;
        _bandParameterData[7] = 0;
        _bandParameterData[8] = uvOffsetX;
        _bandParameterData[9] = uvOffsetY;
        _bandParameterData[10] = uvScaleX;
        _bandParameterData[11] = uvScaleY;
        _bandParameterData[12] = blurScaleX;
        _bandParameterData[13] = blurScaleY;
        _bandParameterData[14] = blurSigma;
        _bandParameterData[15] = blurRadius;
        UploadBandParameters();
    }

    /// <summary>把背板常量数据提交给 GPU(合成阶段会再改 16–27 号槽,故刷新入口独立)。</summary>
    private unsafe void UploadBandParameters()
    {
        fixed (float* pointer = _bandParameterData)
            _context.Object.UpdateSubresource(_bandParameters.Object, 0, IntPtr.Zero, (IntPtr)pointer, 0, 0);
    }

    /// <summary>
    /// 按真实像素区与逻辑源区尺寸建/重建背板纹理(源纹理收真实像素;两张降采样乒乓图覆盖整块逻辑源区),尺寸未变时复用。
    /// </summary>
    /// <param name="sourceWidth">真实像素区宽(物理像素)。</param>
    /// <param name="sourceHeight">真实像素区高(物理像素)。</param>
    /// <param name="logicalWidth">逻辑源区宽(含越界补色部分)。</param>
    /// <param name="logicalHeight">逻辑源区高(含越界部分)。</param>
    /// <returns>本次是否新建了纹理。</returns>
    private bool EnsureBandTextures(uint sourceWidth, uint sourceHeight, uint logicalWidth, uint logicalHeight)
    {
        if (_bandSource != null && _bandSourceSize == (sourceWidth, sourceHeight)
            && _bandLogicalSize == (logicalWidth, logicalHeight)) return false;
        ReleaseBandTextures();

        _bandSource = _device.CreateTexture2D(BandTextureDescription(sourceWidth, sourceHeight, renderTarget: false), null);
        _bandSourceView = _device.CreateShaderResourceView(_bandSource, null);
        uint width = Math.Max(1, (logicalWidth + 3) / 4);
        uint height = Math.Max(1, (logicalHeight + 3) / 4);
        for (var index = 0; index < _bandScratch.Length; index++)
        {
            var texture = _device.CreateTexture2D(BandTextureDescription(width, height, renderTarget: true), null);
            _bandScratch[index] = texture;
            _bandScratchView[index] = _device.CreateShaderResourceView(texture, null);
            _bandScratchTarget[index] = _device.CreateRenderTargetView(texture, null);
        }

        _bandSourceSize = (sourceWidth, sourceHeight);
        _bandLogicalSize = (logicalWidth, logicalHeight);
        _bandScratchSize = (width, height);
        // 噪点纹理 256 × 256 × RGBA8 一并计入(载入后常驻到设备收尾)。
        _bandBytes = ((long)sourceWidth * sourceHeight + 2L * width * height) * 8 + 256L * 256 * 4;
        UpdateGpuBytes();
        NativeStartupLog.Write($"title bar backdrop: 建纹理 源={sourceWidth}x{sourceHeight} 逻辑={logicalWidth}x{logicalHeight} 降采样={width}x{height}");
        return true;
    }

    /// <summary>背板临时纹理的通用描述(RGBA16F,可作 SRV;降采样乒乓图另加 RTV)。</summary>
    /// <param name="width">宽。</param>
    /// <param name="height">高。</param>
    /// <param name="renderTarget">是否同时作渲染目标。</param>
    /// <returns>纹理描述。</returns>
    private static D3D11_TEXTURE2D_DESC BandTextureDescription(uint width, uint height, bool renderTarget) => new()
    {
        Width = width,
        Height = height,
        MipLevels = 1,
        ArraySize = 1,
        Format = DXGI_FORMAT.DXGI_FORMAT_R16G16B16A16_FLOAT,
        SampleDesc = new DXGI_SAMPLE_DESC { Count = 1, Quality = 0 },
        Usage = D3D11_USAGE.D3D11_USAGE_DEFAULT,
        BindFlags = renderTarget
            ? (uint)(D3D11_BIND_FLAG.D3D11_BIND_SHADER_RESOURCE | D3D11_BIND_FLAG.D3D11_BIND_RENDER_TARGET)
            : (uint)D3D11_BIND_FLAG.D3D11_BIND_SHADER_RESOURCE,
        MiscFlags = 0,
    };

    /// <summary>释放背板纹理与视图(源区尺寸变化、设备收尾)。</summary>
    private void ReleaseBandTextures()
    {
        for (var index = 0; index < _bandScratch.Length; index++)
        {
            _bandScratchTarget[index]?.Dispose();
            _bandScratchTarget[index] = null;
            _bandScratchView[index]?.Dispose();
            _bandScratchView[index] = null;
            _bandScratch[index]?.Dispose();
            _bandScratch[index] = null;
        }

        _bandSourceView?.Dispose();
        _bandSourceView = null;
        _bandSource?.Dispose();
        _bandSource = null;
        _bandSourceSize = default;
        _bandLogicalSize = default;
        _bandScratchSize = default;
        _bandBytes = 0;
        UpdateGpuBytes();
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
            if (_viewport.IsFit)
            {
                _lastPointer = position;
                return;
            }
            _viewport.PanBy(position.X - _lastPointer.X, position.Y - _lastPointer.Y);
            _lastPointer = position;
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

    /// <summary>
    /// 一次在途的大图暂存上传:系统内存可写纹理 + 建纹理/映射的 UI 线程耗时。
    /// 后台线程按 <see cref="Copying"/> 标记是否仍在写映射内存(期间不得释放)。
    /// </summary>
    private sealed class StagedUpload
    {
        private int _released;
        internal StagedUpload(WeakReference<DecodedImage> key, DecodedImage source,
            IComObject<ID3D11Texture2D> texture, long started, double stagingMs, double mapMs, long bytes)
        {
            Key = key;
            Source = source;
            Texture = texture;
            Started = started;
            StagingMs = stagingMs;
            MapMs = mapMs;
            Bytes = bytes;
        }

        internal WeakReference<DecodedImage> Key { get; }
        internal DecodedImage Source { get; }
        internal IComObject<ID3D11Texture2D> Texture { get; }
        internal long Started { get; }
        internal double StagingMs { get; }
        internal double MapMs { get; }
        internal long Bytes { get; }
        internal bool Copied;
        internal int Copying;
        internal void ReleaseTexture()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0) Texture.Dispose();
        }
    }

    /// <summary>
    /// 一帧已上传的 GPU 纹理(纹理 + 着色器视图,已生成 mip 链),按 <see cref="DecodedImage"/> 实例身份缓存。
    /// 只持实例的弱引用:缓存的是独立的 GPU 副本,不延长像素租约,也不阻止源图回收。
    /// </summary>
    private sealed class TextureEntry : IDisposable
    {
        /// <summary>构造条目。</summary>
        /// <param name="key">源图实例的弱引用(引用相等即命中)</param>
        /// <param name="texture">已上传的主图纹理</param>
        /// <param name="view">纹理的着色器资源视图</param>
        /// <param name="width">纹理宽(像素)</param>
        /// <param name="height">纹理高(像素)</param>
        internal TextureEntry(
            WeakReference<DecodedImage> key,
            IComObject<ID3D11Texture2D> texture,
            IComObject<ID3D11ShaderResourceView> view,
            int width,
            int height)
        {
            Key = key;
            Texture = texture;
            View = view;
            Width = width;
            Height = height;
            // R16G16B16A16 每像素 8 字节,完整 mip 链约多出 1/3。
            Bytes = (long)width * height * 8 * 4 / 3;
        }

        /// <summary>纹理宽(像素)。</summary>
        internal int Width { get; }

        /// <summary>纹理高(像素)。</summary>
        internal int Height { get; }

        /// <summary>源图实例的弱引用;null 表示这张纹理已退成等待复用的空纹理。</summary>
        internal WeakReference<DecodedImage>? Key { get; private set; }

        /// <summary>主图纹理。</summary>
        internal IComObject<ID3D11Texture2D> Texture { get; }

        /// <summary>纹理的着色器资源视图。</summary>
        internal IComObject<ID3D11ShaderResourceView> View { get; }

        /// <summary>含 mip 链的显存占用估算(字节)。</summary>
        internal long Bytes { get; }

        /// <summary>源图实例是否仍存活(已回收的条目不会再命中)。</summary>
        internal bool IsAlive => Key is not null && Key.TryGetTarget(out _);

        /// <summary>是否就是给定实例上传的那一帧。</summary>
        /// <param name="image">待比较的已解码图像</param>
        /// <returns>引用相等时为 true</returns>
        internal bool Matches(DecodedImage image)
            => Key is not null && Key.TryGetTarget(out var target) && ReferenceEquals(target, image);

        /// <summary>把条目改挂到另一帧(复用空纹理时)或退成空纹理(<paramref name="key"/> 为 null)。</summary>
        /// <param name="key">新的源图弱引用;null 表示退成空纹理</param>
        internal void Rebind(WeakReference<DecodedImage>? key) => Key = key;

        /// <summary>释放 GPU 资源。</summary>
        public void Dispose()
        {
            View.Dispose();
            Texture.Dispose();
        }
    }

    /// <summary>
    /// 主图纹理的内存客户端:把当前帧与最近缓存帧的显存占用报给内存管家,
    /// 压力到 Caution 及以上时丢掉非当前帧的缓存纹理(清场在 UI 线程完成)。
    /// </summary>
    private sealed class GpuTextureMemoryClient : IMemoryClient
    {
        private readonly WindowsPhotoView _view;

        /// <summary>构造客户端。</summary>
        /// <param name="view">所属主图表面</param>
        internal GpuTextureMemoryClient(WindowsPhotoView view) => _view = view;

        /// <summary>压力快照里的客户端名。</summary>
        public string Name => "GpuTextures";

        /// <summary>当前帧与最近缓存帧的纹理字节合计(UI 线程写入,这里只读原子快照)。</summary>
        public long FootprintBytes => Interlocked.Read(ref _view._gpuBytes) + Interlocked.Read(ref _view._outputColorBytes);

        /// <summary>按压力级别修剪:标记后由 UI 线程丢掉全部非当前纹理(本方法在探测线程上,不得阻塞)。</summary>
        /// <param name="level">当前压力级别</param>
        public void Trim(PressureLevel level) => _view.RequestTrim();
    }
}
