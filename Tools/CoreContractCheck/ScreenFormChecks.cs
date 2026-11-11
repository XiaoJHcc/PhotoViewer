using PhotoViewer.Core.Settings;
using PhotoViewer.Shared;

namespace CoreContractCheck;

/// <summary>主屏形态判定表：平台能力、设备类型、用户模式、两轴紧凑与窗口方向的所有分支。</summary>
internal static class ScreenFormChecks
{
    public static void Run(Checks c)
    {
        c.Section("主屏形态判定");

        var cases = new (string Name, bool HCompact, bool VCompact, double Width, double Height,
            bool PhoneDevice, LayoutMode Mode, bool Supported, ScreenForm Expected)[]
        {
            ("未实现手机形态的平台读到手机模式仍按宽形态", true, true, 390, 844, true, LayoutMode.Phone, false, ScreenForm.Wide),
            ("未实现手机形态的平台自动模式恒宽形态", true, true, 844, 390, true, LayoutMode.Auto, false, ScreenForm.Wide),
            ("手机设备竖屏忽略上下模式", false, false, 390, 844, true, LayoutMode.Row, true, ScreenForm.PhonePortrait),
            ("手机设备横屏忽略左右模式", false, false, 844, 390, true, LayoutMode.Col, true, ScreenForm.PhoneLandscape),
            ("手机设备两轴常规仍为手机形态", false, false, 402, 874, true, LayoutMode.Auto, true, ScreenForm.PhonePortrait),
            ("平板选手机模式竖屏", false, false, 390, 844, false, LayoutMode.Phone, true, ScreenForm.PhonePortrait),
            ("平板选手机模式横屏", false, false, 844, 390, false, LayoutMode.Phone, true, ScreenForm.PhoneLandscape),
            ("自动模式水平紧凑进入手机形态", true, false, 390, 844, false, LayoutMode.Auto, true, ScreenForm.PhonePortrait),
            ("自动模式垂直紧凑进入手机形态横屏", false, true, 844, 390, false, LayoutMode.Auto, true, ScreenForm.PhoneLandscape),
            ("自动模式两轴紧凑进入手机形态", true, true, 844, 390, false, LayoutMode.Auto, true, ScreenForm.PhoneLandscape),
            ("自动模式两轴常规竖屏为宽形态", false, false, 1024, 1366, false, LayoutMode.Auto, true, ScreenForm.Wide),
            ("自动模式两轴常规横屏为宽形态", false, false, 1366, 1024, false, LayoutMode.Auto, true, ScreenForm.Wide),
            ("上下模式忽略紧凑类别", true, true, 390, 844, false, LayoutMode.Row, true, ScreenForm.Wide),
            ("左右模式忽略紧凑类别", true, true, 844, 390, false, LayoutMode.Col, true, ScreenForm.Wide),
            ("方窗口手机设备取竖屏", false, false, 600, 600, true, LayoutMode.Auto, true, ScreenForm.PhonePortrait),
            ("未实现手机形态的平板自动模式恒宽形态", true, true, 390, 844, false, LayoutMode.Auto, false, ScreenForm.Wide),
        };

        foreach (var item in cases)
        {
            c.Equal(item.Name, item.Expected,
                ScreenFormResolver.Resolve(item.HCompact, item.VCompact, item.Width, item.Height,
                    item.PhoneDevice, item.Mode, item.Supported));
        }

        bool exhaustive = true;
        var mismatch = string.Empty;
        foreach (var supported in new[] { false, true })
        foreach (var phoneDevice in new[] { false, true })
        foreach (var mode in new[] { LayoutMode.Row, LayoutMode.Col, LayoutMode.Auto, LayoutMode.Phone })
        foreach (var horizontalCompact in new[] { false, true })
        foreach (var verticalCompact in new[] { false, true })
        foreach (var landscape in new[] { false, true })
        {
            double width = landscape ? 900 : 400;
            double height = landscape ? 400 : 900;
            var actual = ScreenFormResolver.Resolve(horizontalCompact, verticalCompact, width, height,
                phoneDevice, mode, supported);
            var expected = Expected(horizontalCompact, verticalCompact, width, height, phoneDevice, mode, supported);
            if (actual == expected) continue;

            exhaustive = false;
            mismatch = $"supported={supported} phone={phoneDevice} mode={mode} "
                + $"compact={horizontalCompact}/{verticalCompact} landscape={landscape} "
                + $"期望 {expected} 实际 {actual}";
            break;
        }

        c.True("形态判定覆盖全部能力/设备/模式/紧凑组合", exhaustive, mismatch);
    }

    private static ScreenForm Expected(bool horizontalCompact, bool verticalCompact, double width, double height,
        bool phoneDevice, LayoutMode mode, bool supported)
    {
        if (!supported) return ScreenForm.Wide;

        var phone = phoneDevice || mode == LayoutMode.Phone;
        if (mode == LayoutMode.Auto && (horizontalCompact || verticalCompact)) phone = true;

        return phone
            ? width > height ? ScreenForm.PhoneLandscape : ScreenForm.PhonePortrait
            : ScreenForm.Wide;
    }
}
