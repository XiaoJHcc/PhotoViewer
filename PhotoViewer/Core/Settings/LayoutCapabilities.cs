namespace PhotoViewer.Core.Settings;

/// <summary>
/// 平台布局能力开关：启动期由平台壳注入，默认按未实现处理。
/// 与 <c>BitmapLoader.AutomaticCacheLimit</c> 同一注入方式，设置页与共享层只读取结果。
/// </summary>
public static class LayoutCapabilities
{
    /// <summary>平台是否实现了手机形态的主屏排布；为 true 时布局模式才提供「手机」选项。</summary>
    public static bool PhoneLayoutSupported { get; set; }
}
