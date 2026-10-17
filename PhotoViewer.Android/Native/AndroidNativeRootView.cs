using System;
using Android.App;
using Android.Content;
using Android.Graphics;
using Android.Views;
using Android.Widget;
using PhotoViewer.ViewModels.Main;

namespace PhotoViewer.Android.Native;

/// <summary>
/// A1b 原生壳基座根视图：深色背景 + 明确的“原生壳已启动”标记。
/// A3 会替换为共享 <c>MainScreenLayout</c> 驱动的文件栏/主图/控制栏；
/// 这里不引入任何 Avalonia 控件。
/// </summary>
public sealed class AndroidNativeRootView : FrameLayout
{
    /// <summary>
    /// 创建原生壳占位根视图。
    /// </summary>
    /// <param name="activity">宿主 Activity。</param>
    /// <param name="main">共享主 VM（A3 起驱动真实布局）。</param>
    public AndroidNativeRootView(Activity activity, MainViewModel main) : base(activity)
    {
        SetBackgroundColor(Color.ParseColor("#111111"));

        var column = new LinearLayout(activity)
        {
            Orientation = Orientation.Vertical,
        };
        column.SetGravity(GravityFlags.Center);

        var title = new TextView(activity)
        {
            Text = "PhotoViewer",
            TextSize = 24,
            Gravity = GravityFlags.Center,
        };
        title.SetTextColor(Color.ParseColor("#FFFFFF"));

        var subtitle = new TextView(activity)
        {
            Text = "Android 原生壳 · A1b 基座",
            TextSize = 15,
            Gravity = GravityFlags.Center,
        };
        subtitle.SetTextColor(Color.ParseColor("#AAAAAA"));

        var detail = new TextView(activity)
        {
            Text = $"布局 {main.Settings.LayoutMode} · 原生展示 {main.UsesNativePresentation}",
            TextSize = 13,
            Gravity = GravityFlags.Center,
        };
        detail.SetTextColor(Color.ParseColor("#666666"));
        detail.SetPadding(0, 12, 0, 0);

        column.AddView(title);
        column.AddView(subtitle);
        column.AddView(detail);
        AddView(column, new LayoutParams(LayoutParams.MatchParent, LayoutParams.MatchParent));
    }
}
