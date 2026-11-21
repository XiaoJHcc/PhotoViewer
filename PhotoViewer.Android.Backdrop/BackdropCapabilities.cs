using System;
using Android.OS;

namespace PhotoViewer.Android.Backdrop;

/// <summary>
/// 主屏材质能力分级（对应上游 <c>isRenderEffectSupported</c> / <c>isRuntimeShaderSupported</c>）。
/// 调用方按分级选择外观：不支持的级别画正式色层，不降级成半透明自绘玻璃。
/// </summary>
public enum BackdropTier
{
    /// <summary>API 24–30：不取样、不加效果，调用方使用正式色层外观。</summary>
    ColorLayer = 0,

    /// <summary>API 31–32：单层取样 + 模糊与色彩矩阵；无折射与着色器高光。</summary>
    Blur = 1,

    /// <summary>API 33+：另有 <c>RuntimeShader</c>，供阶段 3 的折射与高光使用。</summary>
    Full = 2,
}

/// <summary>
/// 平台能力判定。逐条对应上游 <c>com.kyant.backdrop.isRenderEffectSupported</c> 与
/// <c>isRuntimeShaderSupported</c>（<c>androidMain/Platform.kt</c>）：前者 API 31（S），后者 API 33（Tiramisu）。
/// </summary>
/// <remarks>
/// 这里的属性只用于分级与诊断上报。实现内部一律用 <c>OperatingSystem.IsAndroidVersionAtLeast</c>
/// 直接判定，让平台分析器也能识别版本门，避免在低版本执行 31/33 才有的调用。
/// </remarks>
public static class BackdropCapabilities
{
    /// <summary>RenderNode 录制与 <c>Canvas.drawRenderNode</c> 可用（API 29）。</summary>
    public static bool IsLayerRecordingSupported => OperatingSystem.IsAndroidVersionAtLeast(29);

    /// <summary>模糊与色彩矩阵可用（API 31，上游 <c>isRenderEffectSupported</c>）。</summary>
    public static bool IsRenderEffectSupported => OperatingSystem.IsAndroidVersionAtLeast(31);

    /// <summary>AGSL 运行时着色器可用（API 33，上游 <c>isRuntimeShaderSupported</c>）。</summary>
    public static bool IsRuntimeShaderSupported => OperatingSystem.IsAndroidVersionAtLeast(33);

    /// <summary>当前设备的材质分级。</summary>
    public static BackdropTier Tier
    {
        get
        {
            if (OperatingSystem.IsAndroidVersionAtLeast(33)) return BackdropTier.Full;
            if (OperatingSystem.IsAndroidVersionAtLeast(31)) return BackdropTier.Blur;
            return BackdropTier.ColorLayer;
        }
    }

    /// <summary>分级与 API 级别的单行诊断文本（写进检查报告用）。</summary>
    public static string Describe()
    {
        var recording = IsLayerRecordingSupported ? "yes" : "no";
        var effect = IsRenderEffectSupported ? "yes" : "no";
        var shader = IsRuntimeShaderSupported ? "yes" : "no";
        return $"tier={Tier} sdk={(int)Build.VERSION.SdkInt} recording={recording} renderEffect={effect} runtimeShader={shader}";
    }
}
