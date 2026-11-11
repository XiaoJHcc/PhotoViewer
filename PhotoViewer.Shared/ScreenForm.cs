using PhotoViewer.Core.Settings;

namespace PhotoViewer.Shared;

/// <summary>主屏形态：宽形态沿用上下/左右分区，手机形态分竖屏与横屏两种排布。</summary>
public enum ScreenForm
{
    /// <summary>宽形态（上下或左右分区）。</summary>
    Wide,

    /// <summary>手机形态竖屏。</summary>
    PhonePortrait,

    /// <summary>手机形态横屏。</summary>
    PhoneLandscape,
}

/// <summary>主屏形态判定（平台中立纯函数）：尺寸类别与设备类型决定是否进入手机形态，窗口比例决定横竖。</summary>
public static class ScreenFormResolver
{
    /// <summary>
    /// 判定主屏形态。平台未实现手机形态时恒为宽形态；手机设备恒为手机形态并忽略用户模式；
    /// 用户选「手机」即手机形态；「自动」在任一轴紧凑时进入手机形态；其余（上下/左右，或自动且两轴常规）为宽形态。
    /// </summary>
    /// <param name="horizontalCompact">水平方向是否为平台原生的紧凑尺寸类别。</param>
    /// <param name="verticalCompact">垂直方向是否为平台原生的紧凑尺寸类别。</param>
    /// <param name="width">窗口宽（逻辑点）。</param>
    /// <param name="height">窗口高（逻辑点）。</param>
    /// <param name="isPhoneDevice">设备本身是否手机。</param>
    /// <param name="mode">用户在设置里选的布局模式。</param>
    /// <param name="phoneSupported">平台是否实现了手机形态。</param>
    /// <returns>形态；手机形态再按宽高比区分竖屏与横屏。</returns>
    public static ScreenForm Resolve(
        bool horizontalCompact,
        bool verticalCompact,
        double width,
        double height,
        bool isPhoneDevice,
        LayoutMode mode,
        bool phoneSupported)
    {
        if (!phoneSupported) return ScreenForm.Wide;

        var phone = isPhoneDevice
            || mode == LayoutMode.Phone
            || (mode == LayoutMode.Auto && (horizontalCompact || verticalCompact));

        if (!phone) return ScreenForm.Wide;

        return width > height ? ScreenForm.PhoneLandscape : ScreenForm.PhonePortrait;
    }
}
