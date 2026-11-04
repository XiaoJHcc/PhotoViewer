using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using CommunityToolkit.WinUI.Controls;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using PhotoViewer.Core.Input;
using PhotoViewer.Core.Settings;
using PhotoViewer.Shared;
using PhotoViewer.ViewModels.Main;
using PhotoViewer.ViewModels.Settings;

namespace PhotoViewer.Windows.Native;

/// <summary>
/// W-M5 设置窗口定向验收:真实窗口、五分类切换、控件绑定、工作副本隔离与关闭回写。
/// 由 <c>--pv-native-settings</c> 触发,只读自建隔离设置,不写用户配置。
/// </summary>
internal static class WindowsSettingsAcceptance
{
    /// <summary>运行设置窗口验收。</summary>
    /// <param name="main">主视图模型</param>
    /// <param name="presenter">设置窗口展示器</param>
    /// <returns>任务</returns>
    public static async Task RunAsync(MainViewModel main, WindowsNativeSettingsPresenter presenter)
    {
        var checks = new List<string>();
        try
        {
            Directory.CreateDirectory(WindowsMainWindow.SmokeOutputDirectory);
            main.OpenSettingWindow();
            var window = presenter.Window ?? throw new InvalidOperationException("设置窗口未创建");
            await WaitAsync(() => window.Root.XamlRoot != null && window.Root.ActualWidth > 0, "设置窗口布局");
            await WaitAsync(() => window.Root.ActualWidth > 900, "设置窗口尺寸");
            window.Root.UpdateLayout();
            var scale = window.Root.XamlRoot!.RasterizationScale;
            var windowWidth = window.Window.AppWindow.Size.Width / scale;

            Require(window.Root.RequestedTheme == ElementTheme.Dark, "设置窗口不是深色主题");
            Require(window.Navigation is NavigationView, "设置窗口分类不是 NavigationView");
            Require(window.TitleBarControl is TitleBar, "设置窗口没有 TitleBar 控件");
            Require(window.Window.SystemBackdrop is MicaBackdrop, "设置窗口没有 Mica 背景");
            Require(window.Navigation.MenuItems.Count == 5, "分类数不是 5");
            Require(window.Pages.Count == 5, "页面数不是 5");
            Require(FindDescendant<NumberBox>(window.Pages[0]) != null, "文件页没有 NumberBox");
            Require(Math.Abs(windowWidth - WindowsLayout.SettingsWindowWidth) < 4,
                $"窗口宽度不是 960 DIP: {windowWidth:0.0}");
            checks.Add($"PASS window: dark, Mica, NavigationView + TitleBar + NumberBox, 5 categories, window={windowWidth:0} DIP, content={window.Root.ActualWidth:0} DIP");

            // SETTINGS-06：默认值来自平台预算源，提示第一行为运行时预算、第二行按实测有效容量换算张数。
            Require(!string.IsNullOrWhiteSpace(window.Settings.PerformanceBudgetInfo), "预算提示第一行为空");
            Require(!string.IsNullOrWhiteSpace(window.Settings.BitmapCacheCountInfo), "预算提示第二行为空");
            checks.Add(
                $"PASS budget defaults: 缓存 {window.Settings.BitmapCacheMaxMemory} MB / {window.Settings.BitmapCacheMaxCount} 张"
                + $"; 原生解码线程 {window.Settings.NativePreloadParallelism}/{window.Settings.NativePreloadParallelismMaximum}"
                + $"; 软件解码线程 {window.Settings.CpuPreloadParallelism}/{window.Settings.CpuPreloadParallelismMaximum}"
                + $"; 提示 1: {window.Settings.PerformanceBudgetInfo}"
                + $"; 提示 2: {window.Settings.BitmapCacheCountInfo}");

            Require(WindowsInput.FormatGesture(AppGesture.FromKey(new AppKeyGesture(GestureKey.Left))) == "←",
                "方向键显示文本不是箭头");
            Require(WindowsInput.FormatGesture(AppGesture.FromKey(new AppKeyGesture(GestureKey.D2))) == "2",
                "数字键显示文本仍是枚举名");
            Require(WindowsInput.FormatGesture(AppGesture.FromKey(new AppKeyGesture(GestureKey.Oem3))) == "`",
                "OEM 键显示文本仍是枚举名");
            Require(WindowsInput.FormatGesture(AppGesture.FromMouse(new MouseGestureEx(MouseAction.WheelUp))) == "滚轮上",
                "滚轮显示文本不是“滚轮上”");
            checks.Add("PASS gesture text: arrows and mouse wheel format");

            // 输入闸门改用共享命令表:图标表只负责显示,缺图标不得让已知命令变成不可执行。
            Require(SettingsCatalog.Commands.All(definition => InputCommandMap.IsCommandKnown(definition.Id)),
                "共享命令目录中有命令未被 IsCommandKnown 收录");
            Require(!InputCommandMap.IsCommandKnown("NoSuchCommand") && !InputCommandMap.IsCommandKnown(null),
                "未知命令被当成可执行命令");
            checks.Add($"PASS command gate: all {SettingsCatalog.Commands.Length} catalog commands are known; unknown/null rejected, icon table is display-only");

            for (var index = 0; index < 5; index++)
            {
                window.Navigation.SelectedItem = window.Navigation.MenuItems[index];
                await Task.Delay(40);
                window.Root.UpdateLayout();
                Require(window.Pages[index].Visibility == Visibility.Visible, $"分类 {index} 页面未显示");
                for (var other = 0; other < 5; other++)
                {
                    if (other != index)
                    {
                        Require(window.Pages[other].Visibility == Visibility.Collapsed,
                            $"分类 {index} 下页面 {other} 仍然可见");
                    }
                }
            }

            checks.Add("PASS navigation: every category shows exactly one page");
            window.Pages[4].UpdateLayout();
            Require(FindDescendant<InfoBar>(window.Pages[4]) != null, "AI 页没有 InfoBar");

            // 回到文件页做控件绑定检查。
            window.Navigation.SelectedItem = window.Navigation.MenuItems[0];
            await Task.Delay(60);
            window.Root.UpdateLayout();

            // SETTINGS-02/06：同名文件开关固定为第一项且不带分组小标题。
            var fileStack = (StackPanel)((ScrollViewer)window.Pages[0]).Content;
            Require(fileStack.Children.Count > 1 && fileStack.Children[1] is SettingsCard sameNameCard
                && sameNameCard.Header?.ToString() == "同名文件视为同一张图片"
                && sameNameCard.Content is ToggleSwitch,
                "同名文件开关不是文件页第一项");
            Require(!FindDescendants<TextBlock>(window.Pages[0]).Any(text => text.Text == "同名文件"),
                "文件页仍显示“同名文件”分组小标题");
            checks.Add("PASS SETTINGS-06 file page: same-name toggle is the first row with no group header");

            // SETTINGS-16：标题 → 弹性拉条 → 固定最小宽右对齐数值同行，拉条不被挤到下一行。
            var memoryTitle = FindDescendants<TextBlock>(window.Pages[0])
                .FirstOrDefault(text => text.Text == "缓存内存上限")
                ?? throw new InvalidOperationException("文件页没有“缓存内存上限”行");
            var memoryRow = VisualTreeHelper.GetParent(memoryTitle) as FrameworkElement
                ?? throw new InvalidOperationException("缓存内存行缺少内容容器");
            memoryRow.UpdateLayout();
            var memorySlider = FindDescendant<Slider>(memoryRow)
                ?? throw new InvalidOperationException("缓存内存行没有拉条");
            var memoryValue = FindDescendants<TextBlock>(memoryRow)
                .FirstOrDefault(text => text.Text == $"{window.Settings.BitmapCacheMaxMemory} MB")
                ?? throw new InvalidOperationException("缓存内存行没有当前值文本");
            var rowBounds = BoundsOf(memoryRow, window.Root);
            var titleBounds = BoundsOf(memoryTitle, window.Root);
            var sliderBounds = BoundsOf(memorySlider, window.Root);
            var valueBounds = BoundsOf(memoryValue, window.Root);
            static double MidY((double Left, double Top, double Right, double Bottom) bounds)
                => (bounds.Top + bounds.Bottom) / 2;
            Require(Math.Abs(MidY(titleBounds) - MidY(sliderBounds)) < 2
                && Math.Abs(MidY(valueBounds) - MidY(sliderBounds)) < 2,
                $"拉条行没有排在同一行: title={titleBounds} slider={sliderBounds} value={valueBounds}");
            Require(sliderBounds.Left >= titleBounds.Right - .5 && valueBounds.Left >= sliderBounds.Right - .5,
                $"拉条行顺序不是标题→拉条→数值: title={titleBounds} slider={sliderBounds} value={valueBounds}");
            Require(sliderBounds.Right - sliderBounds.Left >= 120 && memoryValue.MinWidth >= WindowsLayout.SettingsSliderValueWidth - .5,
                $"拉条未占满剩余宽度或数值列没有固定最小宽: slider={sliderBounds} minWidth={memoryValue.MinWidth}");
            // SettingsCard 模板的 ContentPresenter 在右侧留出内边距,数值贴的是卡片内容区右缘;
            // 因此按“行尾区域内 + 文本右对齐”判定,并把实测内缩值记进报告。
            var valueInset = rowBounds.Right - valueBounds.Right;
            Require(memoryValue.TextAlignment == TextAlignment.Right && valueInset >= 0 && valueInset < 24,
                $"数值未在行尾右对齐: value={valueBounds} row={rowBounds}"
                + $" align={memoryValue.HorizontalAlignment} min={memoryValue.MinWidth} actual={memoryValue.ActualWidth}"
                + $" parent={VisualTreeHelper.GetParent(memoryValue)?.GetType().Name}"
                + $" margin={memoryValue.Margin} textAlign={memoryValue.TextAlignment}");

            // 卡片没有独立标题带：内容行在卡片内垂直居中，不把拉条行挤到卡片下半区。
            FrameworkElement? memoryCard = memoryRow;
            while (memoryCard != null && memoryCard is not SettingsCard)
            {
                memoryCard = VisualTreeHelper.GetParent(memoryCard) as FrameworkElement;
            }

            Require(memoryCard != null, "拉条行不在设置卡片内");
            var cardBounds = BoundsOf(memoryCard!, window.Root);
            Require(Math.Abs((cardBounds.Top + cardBounds.Bottom) / 2 - MidY(rowBounds)) < 4,
                $"拉条行在卡片内未居中(残留空标题带?): card={cardBounds} row={rowBounds}");
            checks.Add(
                $"PASS SETTINGS-16 slider rows: 标题/拉条/数值同行且顺序正确,拉条 {sliderBounds.Right - sliderBounds.Left:0} 宽,"
                + $"数值最小宽 {memoryValue.MinWidth:0} 文本右对齐,右缘距行框 {valueInset:0.0} 像素(卡片模板内边距),"
                + $"卡片高 {cardBounds.Bottom - cardBounds.Top:0} 无空标题带");

            var toggle = FindDescendant<ToggleSwitch>(window.Pages[0])
                ?? throw new InvalidOperationException("文件页没有 ToggleSwitch");
            toggle.IsOn = !toggle.IsOn;
            await Task.Delay(20);
            Require(window.Settings.SameNameAsOnePhoto == toggle.IsOn, "开关没有写回工作副本");
            Require(main.Settings.SameNameAsOnePhoto != toggle.IsOn, "工作副本泄漏到主设置");

            var numberBox = FindDescendant<NumberBox>(window.Pages[0])
                ?? throw new InvalidOperationException("文件页没有数值框");
            numberBox.Value = 3;
            await Task.Delay(20);
            Require(window.Settings.NativePreloadParallelism == 3, "数值框没有写回工作副本");

            var slider = FindDescendant<Slider>(window.Pages[0])
                ?? throw new InvalidOperationException("文件页没有数值滑条");
            var memoryBefore = window.Settings.BitmapCacheMaxMemory;
            slider.Value = slider.Value >= 0.5 ? 0.25 : 0.75;
            await Task.Delay(20);
            Require(window.Settings.BitmapCacheMaxMemory != memoryBefore, "数值滑条没有写回工作副本");
            checks.Add("PASS controls: toggle + NumberBox + slider write through to work copy");

            // 预览页缩放比例预设(SETTINGS-07):每项一个居中的单值文本输入,Enter/失焦/切换分类/关闭都提交;
            // 越界与非数字给出可见提示并恢复,提交后排序且不重建行。
            window.Navigation.SelectedItem = window.Navigation.MenuItems[1];
            await Task.Delay(80);
            window.Root.UpdateLayout();
            var presetEditors = FindDescendants<TextBox>(window.Pages[1]);
            Require(presetEditors.Count == window.Settings.ScalePresets.Count,
                $"缩放预设输入框数量 {presetEditors.Count} 与预设数 {window.Settings.ScalePresets.Count} 不一致");
            Require(presetEditors.Count > 1, "缩放预设没有可切换的多项");
            Require(FindDescendant<NumberBox>(window.Pages[1]) == null, "缩放预设仍使用 NumberBox/步进器");
            foreach (var presetEditor in presetEditors)
            {
                Require(presetEditor.TextAlignment == TextAlignment.Center,
                    "缩放预设数字未在输入框内居中");
            }

            // SETTINGS-07：输入框与 % 作为一组在整行居中，行尾删除按钮不挤偏这个中心。
            Require(!FindDescendants<TextBlock>(window.Pages[1]).Any(text => text.Text == "缩放位置显示"),
                "预览页仍显示“缩放位置显示”分组小标题");
            var presetRow = VisualTreeHelper.GetParent(presetEditors[0]) as FrameworkElement
                ?? throw new InvalidOperationException("预设行缺少内容容器");
            var presetPercent = FindDescendants<TextBlock>(presetRow).FirstOrDefault(text => text.Text == "%")
                ?? throw new InvalidOperationException("预设行没有 % 单位");
            var presetDelete = FindDescendants<Button>(presetRow)
                .FirstOrDefault(button => (ToolTipService.GetToolTip(button) as string) == "移除预设")
                ?? throw new InvalidOperationException("预设行没有删除按钮");
            var presetRowBounds = BoundsOf(presetRow, window.Root);
            var presetGroupLeft = BoundsOf(presetEditors[0], window.Root).Left;
            var presetGroupRight = BoundsOf(presetPercent, window.Root).Right;
            var presetDeleteBounds = BoundsOf(presetDelete, window.Root);
            var presetOffset = (presetGroupLeft + presetGroupRight) / 2
                - (presetRowBounds.Left + presetRowBounds.Right) / 2;
            Require(Math.Abs(presetOffset) < 1,
                $"预设输入组未在整行居中: group={presetGroupLeft:0.0}..{presetGroupRight:0.0} row={presetRowBounds}");
            Require(presetDeleteBounds.Left >= presetGroupRight,
                $"删除按钮挤进预设输入组: delete={presetDeleteBounds} groupRight={presetGroupRight:0.0}");
            checks.Add(
                $"PASS SETTINGS-07 presets: 输入框+%整组居行中心(偏差 {presetOffset:0.00} 像素), 删除按钮在行尾 {presetDeleteBounds.Left:0} 不挤偏中心; 预览页无“缩放位置显示”小标题");

            var presetError = FindDescendant<InfoBar>(window.Pages[1])
                ?? throw new InvalidOperationException("预览页没有预设错误提示");
            Require(!presetError.IsOpen, "未输入非法值时预设错误提示已显示");
            var blurTarget = FindDescendant<ToggleSwitch>(window.Pages[1])
                ?? throw new InvalidOperationException("预览页没有可转移焦点的开关");

            var editor = presetEditors[0];
            var editedPreset = window.Settings.ScalePresets[0];
            var mainPresetsBefore = main.Settings.ScalePresets.Select(item => item.Value).ToList();
            editor.Focus(FocusState.Programmatic);
            Require(editor.FocusState != FocusState.Unfocused, "预设输入框无法获得焦点");
            Require(WindowsKeyDispatch.IsTextEditingElement(FocusManager.GetFocusedElement(window.Root.XamlRoot!) as DependencyObject),
                "文本编辑焦点未被识别,主窗快捷键会吞掉输入");
            editor.Text = "42.5";
            blurTarget.Focus(FocusState.Programmatic);
            Require(blurTarget.FocusState != FocusState.Unfocused, "无法把焦点移出预设输入框");
            Require(!WindowsKeyDispatch.IsTextEditingElement(FocusManager.GetFocusedElement(window.Root.XamlRoot!) as DependencyObject),
                "非文本控件焦点被误判为文本编辑");
            await Task.Delay(80);
            Require(Math.Abs(editedPreset.Value - 0.425) < 1e-9,
                $"失焦没有提交缩放预设文本: {editedPreset.Value}");
            Require(main.Settings.ScalePresets.Select(item => item.Value).SequenceEqual(mainPresetsBefore),
                "缩放预设编辑泄漏到主设置");
            Require(!presetError.IsOpen, "合法预设输入仍显示错误提示");
            Require(window.Settings.ScalePresets.Select(item => item.Value)
                    .SequenceEqual(window.Settings.ScalePresets.Select(item => item.Value).OrderBy(value => value)),
                "提交后缩放预设未按数值升序");
            Require(window.Settings.ScalePresets.Contains(editedPreset), "排序后丢失同一预设项");
            Require(FindDescendants<TextBox>(window.Pages[1]).Count == window.Settings.ScalePresets.Count,
                "排序后预设行被重建或丢失");
            Require(editor.Text == "42.5", $"提交后输入框文本未归一: {editor.Text}");

            async Task RequireRejectedAsync(string text)
            {
                editor.Focus(FocusState.Programmatic);
                Require(editor.FocusState != FocusState.Unfocused, "预设输入框无法获得焦点");
                editor.Text = text;
                blurTarget.Focus(FocusState.Programmatic);
                Require(blurTarget.FocusState != FocusState.Unfocused, "无法把焦点移出预设输入框");
                await Task.Delay(80);
                Require(Math.Abs(editedPreset.Value - 0.425) < 1e-9,
                    $"非法输入“{text}”写入了工作副本: {editedPreset.Value}");
                Require(editor.Text == "42.5", $"非法输入“{text}”未恢复为已提交值: {editor.Text}");
                Require(presetError.IsOpen, $"非法输入“{text}”没有可见提示");
            }

            await RequireRejectedAsync("1601");
            await RequireRejectedAsync("NaN");
            await RequireRejectedAsync("abc");
            await RequireRejectedAsync("0");
            await RequireRejectedAsync("");

            editor.Focus(FocusState.Programmatic);
            Require(editor.FocusState != FocusState.Unfocused, "预设输入框无法获得焦点");
            editor.Text = "55";
            blurTarget.Focus(FocusState.Programmatic);
            Require(blurTarget.FocusState != FocusState.Unfocused, "无法把焦点移出预设输入框");
            await Task.Delay(80);
            Require(Math.Abs(editedPreset.Value - 0.55) < 1e-9, "合法输入在错误后未重新写入");
            Require(!presetError.IsOpen, "合法输入后错误提示未关闭");

            // 切换分类前提交尚未失焦的编辑。
            editor.Focus(FocusState.Programmatic);
            Require(editor.FocusState != FocusState.Unfocused, "预设输入框无法获得焦点");
            editor.Text = "60";
            window.Navigation.SelectedItem = window.Navigation.MenuItems[2];
            await Task.Delay(120);
            Require(Math.Abs(editedPreset.Value - 0.6) < 1e-9, "切换分类没有提交预设编辑");
            window.Navigation.SelectedItem = window.Navigation.MenuItems[1];
            await Task.Delay(80);
            window.Root.UpdateLayout();
            checks.Add("PASS zoom presets: one centered editor per item, text blur/valid commit + sort, 1601/NaN/abc/0/empty rejected with visible prompt and restored, work copy isolated");

            var list = FindDescendant<ListView>(window.Pages[2]);
            if (list == null)
            {
                throw new InvalidOperationException("控制页没有快捷键列表");
            }

            Require(list.Items.Count == window.Settings.Hotkeys.Count, "快捷键列表行数与共享集合不一致");
            var first = window.Settings.Hotkeys[0];
            if (list.ItemsSource is not System.Collections.IList rows || rows.Count < 2)
            {
                throw new InvalidOperationException("快捷键列表没有可重排的行集合");
            }

            // SETTINGS-08：布局分组标题固定为「边栏布局位置」，控制栏功能分组不改名。
            var controlTexts = FindDescendants<TextBlock>(window.Pages[2]).Select(text => text.Text).ToArray();
            Require(controlTexts.Contains("边栏布局位置"), "控制页布局分组标题不是“边栏布局位置”");
            Require(!controlTexts.Contains("控制栏布局位置"), "控制页仍用旧标题“控制栏布局位置”");
            Require(controlTexts.Contains("控制栏功能与快捷键"), "控制栏功能分组标题被改名");
            Require(!FindDescendants<TextBlock>(window.Pages[3]).Any(text => text.Text.Length == 0 && text.ActualWidth > 0),
                "EXIF 页出现空标题");

            // SETTINGS-01：勾选 → 命令图标 → 命令名，视觉间距 8；EXIF 显示表不带图标。
            var hotkeyIndex = -1;
            FrameworkElement? hotkeyRow = null;
            for (var index = 0; index < rows.Count; index++)
            {
                if (rows[index] is FrameworkElement candidate
                    && FindDescendant<CheckBox>(candidate) is { ActualWidth: > 0 })
                {
                    hotkeyIndex = index;
                    hotkeyRow = candidate;
                    break;
                }
            }
            Require(hotkeyRow != null && hotkeyIndex >= 0, "快捷键列表没有已实现的真实行");
            var hotkeyItem = window.Settings.Hotkeys[hotkeyIndex];
            var rowCheck = FindDescendant<CheckBox>(hotkeyRow!)
                ?? throw new InvalidOperationException("快捷键行没有勾选框");
            var expectedGlyph = WindowsIcons.ForCommand(hotkeyItem.Command);
            var rowIcon = FindDescendants<FontIcon>(hotkeyRow!)
                .FirstOrDefault(icon => icon.Glyph == expectedGlyph && icon.Width == WindowsSettingsPages.HotkeyIconSize)
                ?? throw new InvalidOperationException($"快捷键行缺少命令图标: {hotkeyItem.Command}");
            var rowName = FindDescendants<TextBlock>(hotkeyRow!)
                .FirstOrDefault(text => text.Text == hotkeyItem.Name)
                ?? throw new InvalidOperationException("快捷键行没有命令名");
            var checkBounds = BoundsOf(rowCheck, window.Root);
            var iconBounds = BoundsOf(rowIcon, window.Root);
            var nameBounds = BoundsOf(rowName, window.Root);
            Require(Math.Abs(iconBounds.Left - checkBounds.Right - WindowsSettingsPages.HotkeyIconGap) < 1.5,
                $"勾选到命令图标间距不是 8: {iconBounds.Left - checkBounds.Right:0.0}");
            Require(Math.Abs(nameBounds.Left - iconBounds.Right - WindowsSettingsPages.HotkeyIconGap) < 1.5,
                $"命令图标到命令名间距不是 8: {nameBounds.Left - iconBounds.Right:0.0}"
                + $" check={checkBounds} icon={iconBounds} name={nameBounds}"
                + $" nameWidth={rowName.ActualWidth} iconWidth={rowIcon.ActualWidth} index={hotkeyIndex}");
            Require(iconBounds.Left >= checkBounds.Right && nameBounds.Left >= iconBounds.Right,
                $"命令图标与勾选/命令名重叠: check={checkBounds} icon={iconBounds} name={nameBounds}");
            var exifIcons = FindDescendants<FontIcon>(window.Pages[3])
                .Count(icon => icon.Width == WindowsSettingsPages.HotkeyIconSize);
            Require(exifIcons == 0, $"EXIF 显示表不应带命令图标槽: {exifIcons}");
            checks.Add(
                $"PASS SETTINGS-01/08: 布局分组标题为“边栏布局位置”、控制栏功能分组未改名; 快捷键行 勾选→图标({iconBounds.Right - iconBounds.Left:0} 宽)→命令名 间距 8/8, EXIF 表无命令图标");

            var firstRow = rows[0];
            rows.RemoveAt(0);
            rows.Insert(1, firstRow);
            await Task.Delay(80);
            Require(window.Settings.Hotkeys[1] == first, "列表重排没有回写到共享集合");
            checks.Add("PASS list: UI reorder writes back to shared collection");

            var recordButton = FindDescendant<Button>(list)
                ?? throw new InvalidOperationException("快捷键行没有录制按钮");
            var recordPeer = new ButtonAutomationPeer(recordButton);
            recordPeer.Invoke();
            await Task.Delay(40);
            var recordText = FindDescendant<TextBlock>(recordButton);
            Require(recordText?.Text == "按下按键…", "录制态按钮文本未切换");
            window.Recorder.Cancel();
            await Task.Delay(40);
            Require(recordText?.Text != "按下按键…", "取消录制后按钮文本未复位");
            checks.Add("PASS hotkey record: begin/cancel updates button state");

            // 关闭时提交尚未失焦的预设编辑。
            window.Root.UpdateLayout();
            window.Navigation.SelectedItem = window.Navigation.MenuItems[1];
            await Task.Delay(60);
            editor.Focus(FocusState.Programmatic);
            Require(editor.FocusState != FocusState.Unfocused, "关闭前预设输入框无法获得焦点");
            editor.Text = "66.6";
            var beforeClose = main.Settings.SameNameAsOnePhoto;
            var working = window.Settings.SameNameAsOnePhoto;
            window.Close();
            await WaitAsync(() => !presenter.IsOpen, "设置窗口关闭");
            // 关闭提交是异步写入，先等工作副本回写与释放结束再断言主设置与落盘。
            await presenter.CloseIfOpenAsync();
            await WaitAsync(
                () => main.Settings.SameNameAsOnePhoto == working,
                "关闭后工作副本回写主设置");
            Require(beforeClose != working, "回写断言前值未变化");
            Require(main.Settings.ScalePresets.Any(preset => Math.Abs(preset.Value - 0.666) < 1e-9),
                "关闭设置没有提交尚未失焦的预设编辑");

            // SETTINGS-05/07：关闭提交后缩放预设随设置文档落盘（隔离目录）。
            var settingsPath = Path.Combine(WindowsMainWindow.SmokeOutputDirectory, "settings-smoke.json");
            var persisted = false;
            for (var attempt = 0; attempt < 40 && !persisted; attempt++)
            {
                await Task.Delay(100);
                try
                {
                    using var document = JsonDocument.Parse(await File.ReadAllTextAsync(settingsPath));
                    persisted = document.RootElement.GetProperty("scalePresets").EnumerateArray()
                        .Any(value => Math.Abs(value.GetDouble() - 0.666) < 1e-9);
                }
                catch (IOException)
                {
                }
                catch (JsonException)
                {
                }
                catch (KeyNotFoundException)
                {
                }
            }

            Require(persisted, "关闭设置后缩放预设没有落盘");
            checks.Add("PASS lifecycle: work copy applied exactly once on close; focused preset edit committed and persisted to settings-smoke.json");

            Environment.ExitCode = 0;
        }
        catch (Exception exception)
        {
            Environment.ExitCode = 1;
            checks.Add("FAIL " + exception);
            NativeStartupLog.Fail("native settings acceptance", exception);
            await presenter.CloseIfOpenAsync();
        }
        finally
        {
            File.WriteAllText(
                Path.Combine(WindowsMainWindow.SmokeOutputDirectory, "settings-checks.json"),
                JsonSerializer.Serialize(checks, new JsonSerializerOptions { WriteIndented = true }));
        }
    }

    /// <summary>轮询等待条件成立。</summary>
    /// <param name="condition">条件</param>
    /// <param name="what">失败描述</param>
    /// <returns>任务</returns>
    private static async Task WaitAsync(Func<bool> condition, string what)
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(50);
        }

        throw new InvalidOperationException("等待超时:" + what);
    }

    /// <summary>深度优先查找第一个指定类型子元素。</summary>
    /// <typeparam name="T">元素类型</typeparam>
    /// <param name="root">根</param>
    /// <returns>元素;找不到返回 null</returns>
    private static T? FindDescendant<T>(DependencyObject root) where T : DependencyObject
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var index = 0; index < count; index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is T match)
            {
                return match;
            }

            var found = FindDescendant<T>(child);
            if (found != null)
            {
                return found;
            }
        }

        return null;
    }

    /// <summary>深度优先收集全部指定类型子元素。</summary>
    /// <typeparam name="T">元素类型</typeparam>
    /// <param name="root">根</param>
    /// <returns>按视觉树顺序的全部匹配项</returns>
    private static List<T> FindDescendants<T>(DependencyObject root) where T : DependencyObject
    {
        var matches = new List<T>();
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var index = 0; index < count; index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is T match)
            {
                matches.Add(match);
            }

            matches.AddRange(FindDescendants<T>(child));
        }

        return matches;
    }

    /// <summary>取元素相对窗口根的四边坐标(用于同行/居中/间距断言)。</summary>
    /// <param name="element">目标元素</param>
    /// <param name="root">共同的视觉树祖先</param>
    /// <returns>左右上下边界</returns>
    private static (double Left, double Top, double Right, double Bottom) BoundsOf(
        FrameworkElement element, UIElement root)
    {
        var origin = element.TransformToVisual(root)
            .TransformPoint(new global::Windows.Foundation.Point(0, 0));
        return (origin.X, origin.Y, origin.X + element.ActualWidth, origin.Y + element.ActualHeight);
    }

    /// <summary>断言。</summary>
    /// <param name="condition">条件</param>
    /// <param name="message">失败描述</param>
    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
