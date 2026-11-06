using AppKit;
using CoreGraphics;
using Foundation;
using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using PhotoViewer.Apple;
using PhotoViewer.Core.Design;
using PhotoViewer.Core.Image;
using PhotoViewer.Core.Input;
using PhotoViewer.Core.Settings;
using PhotoViewer.Mac.Core;
using PhotoViewer.ViewModels.Settings;

namespace PhotoViewer.Mac.Native;

/// <summary>设置窗口实际控件与系统事件回归，仅在隔离冒烟会话中运行。
/// 窗口由 SwiftUI（libMacSettingsUI.dylib）承载，验收通过 C# 侧展示器的公开接口驱动。</summary>
internal static class NativeSettingsAcceptance
{
    /// <summary>验证五分类、深色外观、SwiftUI 宿主、分页历史前进后退、勾选、行内录制/清空/取消、排序与窗口缩放，
    /// 导出分页截图并留出真实窗口截图机会，最后核对关闭回写与重开恢复。</summary>
    internal static async Task RunAsync(MacMainWindow main, MacNativeSettingsPresenter presenter, string output)
    {
        var checks = new List<string>();
        presenter.TryPresent(main.Main.Settings);
        if (!presenter.IsPresenting) throw new InvalidOperationException("设置窗口未创建");
        await Task.Delay(600);
        if (!presenter.IsDarkAppearance) throw new InvalidOperationException("设置窗口不是深色外观");
        if (!presenter.HasHostingController) throw new InvalidOperationException("设置未挂载 SwiftUI 宿主视图");
        checks.Add("DarkAqua / SwiftUI NSHostingController mounted");
        await File.WriteAllTextAsync(Path.Combine(output, "settings-window-id.txt"), presenter.WindowNumber.ToString());

        for (int category = 0; category < 5; category++)
        {
            presenter.ShowCategory(category);
            await Task.Delay(500);
            string page = Path.Combine(output, $"settings-{category}.png");
            presenter.RenderTo(page);
            AssertRendered(page, $"分类 {category} 未渲染出控件");
            CaptureScreen(presenter, Path.Combine(output, $"settings-window-{category}.png"), checks);
            checks.Add($"category {category}: SwiftUI grouped form rendered");
            if (category == 0)
            {
                // 桌面端缓存上限可编辑：两档张数直接按工作副本内存上限估算，超限只挂一句简短警告
                var settings = presenter.WorkingSettings;
                if (!settings.ShowsCacheLimits) throw new InvalidOperationException("桌面端缓存上限两项不应隐藏");
                var baseline = settings.CreateSnapshot();
                string budgetInfo = settings.PerformanceBudgetInfo;
                string countInfo = settings.BitmapCacheCountInfo;
                if (budgetInfo.Contains("设备性能", StringComparison.Ordinal) || budgetInfo.Contains("核 / 内存", StringComparison.Ordinal)
                    || budgetInfo.Contains("上次系统内存告警", StringComparison.Ordinal))
                    throw new InvalidOperationException("系统内存提示仍是旧的运行时预算口径：" + budgetInfo);
                AssertSystemMemoryHint(budgetInfo, "默认设置");
                AssertCountWording(countInfo, "默认设置");
                AssertWarningSuffix(budgetInfo, settings.PerformanceBudgetInfoLevel, "默认设置");
                if (settings.BitmapCacheCountInfoLevel != PerfInfoLevel.Normal)
                    throw new InvalidOperationException("张数提示不按用户设置定级：" + settings.BitmapCacheCountInfoLevel);
                checks.Add("settings06: 系统内存与两档张数提示：" + budgetInfo + " / " + countInfo);
                checks.Add("file footer: " + budgetInfo);
                checks.Add("file footer: " + countInfo);

                try
                {
                    // 内存拉条逐档改变张数：每档都按所选上限直接换算（不乘安全比例、不按预算/可用内存截断）
                    bool screenNumeric = false;
                    foreach (int limitMB in new[] { 1024, 8192, SettingsDocumentCodec.MaximumCacheMemoryMB })
                    {
                        settings.BitmapCacheMaxMemory = limitMB;
                        string stage = $"缓存内存上限 {limitMB} MB";
                        var counts = ParseCounts(settings.BitmapCacheCountInfo, stage);
                        screenNumeric |= counts.Screen != null;
                        AssertCountsForLimit(counts, limitMB, stage);
                        AssertCountWording(settings.BitmapCacheCountInfo, stage);
                        AssertSystemMemoryHint(settings.PerformanceBudgetInfo, stage);
                        AssertWarningSuffix(settings.PerformanceBudgetInfo, settings.PerformanceBudgetInfoLevel, stage);
                        checks.Add($"settings06: {stage} → {settings.BitmapCacheCountInfo}；级别 {settings.PerformanceBudgetInfoLevel}");
                    }
                    if (!screenNumeric)
                        checks.Add("NOTE settings06: 当前无物理视口，适屏档按规范显示“未知”，只核对了完整档张数");

                    // 张数只随内存上限变化：数量/预载/解码线程改了也不动
                    string atLimit = settings.BitmapCacheCountInfo;
                    settings.BitmapCacheMaxCount = 1;
                    settings.PreloadForwardCount = 0;
                    settings.NativePreloadParallelism = 1;
                    if (settings.BitmapCacheCountInfo != atLimit)
                        throw new InvalidOperationException("数量/预载/解码线程改了张数估算：" + settings.BitmapCacheCountInfo);

                    var pageState = presenter.BuildState();
                    string expectedLevel = MacNativeSettingsPresenter.LevelName(settings.PerformanceBudgetInfoLevel);
                    if (pageState.File.BudgetLevel != expectedLevel || pageState.File.CacheCountLevel != "normal")
                        throw new InvalidOperationException($"提示级别未传到 SwiftUI 状态：{pageState.File.BudgetLevel}/{expectedLevel}");
                    if (pageState.File.WarningColor != DesignTokens.ColorSettingsWarning)
                        throw new InvalidOperationException(
                            $"警告色未取 color.settings.warning 令牌：0x{pageState.File.WarningColor:X8}");
                    checks.Add($"settings06: 上限 {SettingsDocumentCodec.MaximumCacheMemoryMB} MB 时张数与数量/预载/线程无关，级别 {expectedLevel} 与警告色令牌 0x{pageState.File.WarningColor:X8} 已推给 SwiftUI");
                }
                finally
                {
                    settings.ApplySnapshot(baseline);
                }
                if (settings.BitmapCacheCountInfo != countInfo)
                    throw new InvalidOperationException("恢复设置快照后张数未回到进入时的口径：" + settings.BitmapCacheCountInfo);
                AssertWarningSuffix(settings.PerformanceBudgetInfo, settings.PerformanceBudgetInfoLevel, "快照恢复后");
                checks.Add("settings06: 内存拉条（1 GB / 8 GB / 上限）按所选上限换算张数，超限只追加简短警告；快照恢复后提示复原");
                checks.Add("SETTINGS-06 footer: 两段提示为可换行文本（SwiftUI VStack + fixedSize(vertical)），级别取 levelColor（normal/warning/error）");
            }
            await Task.Delay(300);
        }
        CaptureScreen(presenter, Path.Combine(output, "settings-window.png"), checks);

        // SETTINGS-01：控制栏功能行的命令图标与 iOS/控制栏同源（AppleSymbolNames.ForCommand），
        // 勾选→图标→名称的间距由 SwiftUI 渲染，这里只核对推送状态，实际外观由下方截图取证。
        var state = presenter.BuildState();
        if (state.Control.Hotkeys.Count != presenter.WorkingSettings.Hotkeys.Count)
            throw new InvalidOperationException("控制栏功能状态行数与工作副本不一致");
        for (int index = 0; index < state.Control.Hotkeys.Count; index++)
        {
            var row = state.Control.Hotkeys[index];
            string expected = AppleSymbolNames.ForCommand(presenter.WorkingSettings.Hotkeys[index].Command) ?? string.Empty;
            if (string.IsNullOrEmpty(row.Symbol) || row.Symbol != expected)
                throw new InvalidOperationException($"第 {index} 行命令图标不是命令目录口径：{row.Symbol}/{expected}");
        }
        checks.Add($"SETTINGS-01 hotkey icons: {state.Control.Hotkeys.Count} 行的 SF Symbols 全部取自 AppleSymbolNames.ForCommand（勾选→图标→名称间距由 SwiftUI 渲染，见截图）");

        // SETTINGS-07/16：预览页去掉指示器分组小标题；拉条行在最小窗口宽度下仍同行，导出截图供目视核对。
        // 说明：SwiftUI 分组表单的分组标题与行内几何无法从 C# 侧读取，本轮只核对推送状态 + 真实窗口位图。
        presenter.ShowCategory(1);
        await Task.Delay(300);
        foreach (var (category, name, width) in new[]
        {
            (1, "settings-preview", 860.0), (1, "settings-preview-narrow", 760.0),
            (0, "settings-file-narrow", 760.0), (4, "settings-ai-narrow", 760.0),
        })
        {
            presenter.SetContentSize(width, 720);
            presenter.ShowCategory(category);
            await Task.Delay(350);
            string shot = Path.Combine(output, name + ".png");
            presenter.RenderTo(shot);
            AssertRendered(shot, $"分类 {category} 在 {width:0} 宽度下未渲染出控件");
        }
        presenter.SetContentSize(860, 720);
        checks.Add("SETTINGS-07/16 narrow: 860 与 760 DIP 下文件/AI 拉条行、预览页与设置窗截图已导出待目视核对（拉条与数值同在标题行；预览页无“缩放位置显示”小标题；未做像素断言）");

        presenter.ShowCategory(0);
        await Task.Delay(100);
        presenter.ShowCategory(1);
        await Task.Delay(100);
        presenter.ShowCategory(2);
        await Task.Delay(100);
        if (presenter.CurrentCategory != 2) throw new InvalidOperationException("分类切换未生效");
        presenter.NavigateBack();
        if (presenter.CurrentCategory != 1) throw new InvalidOperationException("历史后退未回到上一分类");
        presenter.NavigateBack();
        if (presenter.CurrentCategory != 0) throw new InvalidOperationException("历史后退未到达更早分类");
        presenter.NavigateForward();
        if (presenter.CurrentCategory != 1) throw new InvalidOperationException("历史前进未返回下一分类");
        presenter.NavigateForward();
        if (presenter.CurrentCategory != 2) throw new InvalidOperationException("历史前进未回到最新分类");
        checks.Add("category history back/forward");

        bool displayed = presenter.WorkingSettings.Hotkeys[0].IsDisplay;
        string beforeToggle = Path.Combine(output, "settings-toggle-before.png");
        string afterToggle = Path.Combine(output, "settings-toggle-after.png");
        presenter.RenderTo(beforeToggle);
        presenter.ApplyAction("{\"type\":\"setBool\",\"key\":\"control.hotkey.display.0\",\"boolValue\":"
            + (!displayed).ToString().ToLowerInvariant() + "}");
        if (presenter.WorkingSettings.Hotkeys[0].IsDisplay == displayed) throw new InvalidOperationException("勾选动作未更新工作副本");
        await Task.Delay(300);
        presenter.RenderTo(afterToggle);
        if (File.ReadAllBytes(beforeToggle).SequenceEqual(File.ReadAllBytes(afterToggle)))
            throw new InvalidOperationException("勾选后 SwiftUI 未重绘界面");
        presenter.ApplyAction("{\"type\":\"setBool\",\"key\":\"control.hotkey.display.0\",\"boolValue\":"
            + displayed.ToString().ToLowerInvariant() + "}");
        if (presenter.WorkingSettings.Hotkeys[0].IsDisplay != displayed) throw new InvalidOperationException("勾选动作未可逆");
        checks.Add("toggle action updates working copy and repaints SwiftUI");

        var first = presenter.WorkingSettings.Hotkeys[0];
        var original = first.PrimaryHotkey;
        presenter.BeginRecording(0, true);
        if (!presenter.IsRecording) throw new InvalidOperationException("未进入录制态");
        PostKey(presenter.WindowNumber, 12, "q", NSEventModifierMask.CommandKeyMask | NSEventModifierMask.ShiftKeyMask);
        await Task.Delay(250);
        if (presenter.IsRecording) throw new InvalidOperationException("录制未在有效手势后结束");
        if (first.PrimaryHotkey == original || first.PrimaryHotkey?.Key?.Key != GestureKey.Q) throw new InvalidOperationException("原生快捷键录制失败");
        checks.Add("inline recording consumes actual AppKit key event");
        presenter.BeginRecording(0, true);
        PostKey(presenter.WindowNumber, 51, "\b", 0);
        await Task.Delay(250);
        if (first.PrimaryHotkey != null) throw new InvalidOperationException("删除键未清空快捷键");
        checks.Add("delete clears hotkey");
        presenter.BeginRecording(0, true);
        PostKey(presenter.WindowNumber, 53, "\u001b", 0);
        await Task.Delay(250);
        if (presenter.IsRecording) throw new InvalidOperationException("Escape 未取消录制");
        if (first.PrimaryHotkey != null) throw new InvalidOperationException("取消录制不应改动快捷键");
        checks.Add("Escape cancels recording");
        first.PrimaryHotkey = original;
        presenter.WorkingSettings.CheckHotkeyConflicts();

        var formatFirst = presenter.WorkingSettings.FileFormats[0];
        presenter.ApplyAction("{\"type\":\"moveFormat\",\"from\":0,\"to\":2}");
        if (!ReferenceEquals(presenter.WorkingSettings.FileFormats[2], formatFirst)) throw new InvalidOperationException("文件格式排序未生效");
        presenter.ApplyAction("{\"type\":\"moveFormat\",\"from\":2,\"to\":0}");
        var hotkeyFirst = presenter.WorkingSettings.Hotkeys[0];
        presenter.ApplyAction("{\"type\":\"moveHotkey\",\"from\":0,\"to\":2}");
        if (!ReferenceEquals(presenter.WorkingSettings.Hotkeys[2], hotkeyFirst)) throw new InvalidOperationException("快捷键排序未生效");
        presenter.ApplyAction("{\"type\":\"moveHotkey\",\"from\":2,\"to\":0}");
        var exifFirst = presenter.WorkingSettings.ExifDisplayItems[0];
        presenter.ApplyAction("{\"type\":\"moveExif\",\"from\":0,\"to\":3}");
        if (!ReferenceEquals(presenter.WorkingSettings.ExifDisplayItems[3], exifFirst)) throw new InvalidOperationException("EXIF 排序未生效");
        presenter.ApplyAction("{\"type\":\"moveExif\",\"from\":3,\"to\":0}");
        checks.Add("reorder actions: file formats / hotkeys / exif display items");

        presenter.ShowCategory(1);
        var preset = presenter.WorkingSettings.ScalePresets[0];
        string presetId = presenter.PresetId(preset);
        var neighbor = presenter.WorkingSettings.ScalePresets[1];
        string neighborId = presenter.PresetId(neighbor);
        var originalPresets = main.Main.Settings.ScalePresets.Select(item => item.Text).ToArray();
        presenter.ApplyAction($"{{\"type\":\"applyPreset\",\"presetId\":\"{presetId}\",\"text\":\"800\"}}");
        presenter.ApplyAction($"{{\"type\":\"applyPreset\",\"presetId\":\"{neighborId}\",\"text\":\"33.333\"}}");
        if (preset.Text != "800" || neighbor.Text != "33.333" || presenter.PresetId(preset) != presetId)
            throw new InvalidOperationException("预设重排后逻辑身份或相邻编辑丢失");
        if (!main.Main.Settings.ScalePresets.Select(item => item.Text).SequenceEqual(originalPresets))
            throw new InvalidOperationException("预设编辑提前影响源设置");
        presenter.ApplyAction($"{{\"type\":\"applyPreset\",\"presetId\":\"{presetId}\",\"text\":\"1601\"}}");
        if (preset.Text != "800") throw new InvalidOperationException("非法预设覆盖原值");
        await Task.Delay(300);
        // 后台启动的冒烟进程拿不到 macOS 激活（KeyWindow 可能为空），按窗口号直接取设置窗口并置前；
        // 取到即继续，键盘事件按窗口号投递，不依赖应用激活。
        NSWindow? settingsWindow = null;
        for (int attempt = 0; attempt < 60 && settingsWindow == null; attempt++)
        {
            var key = NSApplication.SharedApplication.KeyWindow;
            settingsWindow = key != null && key.WindowNumber == presenter.WindowNumber
                ? key
                : NSApplication.SharedApplication.WindowWithWindowNumber(presenter.WindowNumber);
            if (settingsWindow != null)
            {
                NSApplication.SharedApplication.Activate();
                settingsWindow.MakeKeyAndOrderFront(null);
                break;
            }
            await Task.Delay(50);
        }
        if (settingsWindow == null) throw new InvalidOperationException("设置窗口未获焦");
        if (settingsWindow.WindowNumber != presenter.WindowNumber) throw new InvalidOperationException("设置窗口不是当前键盘窗口");
        var field = PresetField(settingsWindow.ContentView!, presetId);
        settingsWindow.MakeFirstResponder(field);
        field.SelectText(field);
        using (var text = new NSString("812")) ((NSTextView)settingsWindow.FirstResponder!).InsertText(text, new NSRange(nint.MaxValue, 0));
        if (preset.Text == "812") throw new InvalidOperationException("预设草稿在提交前已写入工作副本");
        PostKey(presenter.WindowNumber, 36, "\r", 0);
        await Task.Delay(300);
        if (preset.Text != "812" || !ReferenceEquals(field, PresetField(settingsWindow.ContentView!, presetId)))
            throw new InvalidOperationException("原生预设 Enter 未提交或重排重建了输入框");
        field.SelectText(field);
        using (var text = new NSString("813")) ((NSTextView)settingsWindow.FirstResponder!).InsertText(text, new NSRange(nint.MaxValue, 0));
        var otherField = PresetField(settingsWindow.ContentView!, neighborId);
        settingsWindow.MakeFirstResponder(otherField);
        await Task.Delay(250);
        if (preset.Text != "813") throw new InvalidOperationException("预设失焦丢失草稿");
        otherField.SelectText(otherField);
        using (var text = new NSString("77")) ((NSTextView)settingsWindow.FirstResponder!).InsertText(text, new NSRange(nint.MaxValue, 0));
        var blank = new CGPoint(settingsWindow.ContentView!.Bounds.Width * .65, 20);
        foreach (var type in new[] { NSEventType.LeftMouseDown, NSEventType.LeftMouseUp })
        {
            var click = NSEvent.MouseEvent(type, blank, 0, NSProcessInfo.ProcessInfo.SystemUptime, settingsWindow.WindowNumber, null!, 1, 1, 1)!;
            NSApplication.SharedApplication.PostEvent(click, false);
        }
        await Task.Delay(250);
        if (neighbor.Text != "77") throw new InvalidOperationException("点击设置空白未提交草稿");
        otherField.SelectText(otherField);
        using (var text = new NSString("77")) ((NSTextView)settingsWindow.FirstResponder!).InsertText(text, new NSRange(nint.MaxValue, 0));
        presenter.ShowCategory(2);
        await Task.Delay(300);
        if (neighbor.Text != "77") throw new InvalidOperationException("分类导航丢失预设草稿");
        presenter.ShowCategory(1);
        await Task.Delay(200);
        checks.Add("preset native editor: real text insertion + Enter / focus loss / category navigation commit drafts; sorting retains native input identity");
        presenter.ApplyAction($"{{\"type\":\"removePreset\",\"presetId\":\"{neighborId}\"}}");
        if (presenter.WorkingSettings.ScalePresets.Contains(neighbor) || !presenter.WorkingSettings.ScalePresets.Contains(preset))
            throw new InvalidOperationException("预设重排后删除误写了索引");
        foreach (var item in presenter.WorkingSettings.ScalePresets.ToArray())
            presenter.ApplyAction($"{{\"type\":\"removePreset\",\"presetId\":\"{presenter.PresetId(item)}\"}}");
        if (presenter.WorkingSettings.ScalePresets.Count != 1) throw new InvalidOperationException("最后一项预设未禁止删除");
        checks.Add("presets: stable logical IDs survive sort/edit/delete; invalid input retains value; last item cannot be removed; working copy isolated");

        foreach (var size in new[] { new CGSize(760, 540), new CGSize(1060, 800), new CGSize(860, 720) })
        {
            presenter.SetContentSize(size.Width, size.Height);
            await Task.Delay(350);
            string path = Path.Combine(output, $"settings-control-{size.Width}.png");
            presenter.RenderTo(path);
            AssertRendered(path, "缩放后设置窗口未渲染");
            checks.Add($"window resize {size.Width}x{size.Height}: rendered");
        }
        presenter.SetContentSize(860, 720);

        presenter.ShowCategory(0);
        await Task.Delay(300);
        presenter.RenderTo(Path.Combine(output, "settings-final.png"));
        await File.WriteAllLinesAsync(Path.Combine(output, "settings-ui-checks.txt"), checks);

        presenter.ShowCategory(1);
        await Task.Delay(200);
        var lastPreset = presenter.WorkingSettings.ScalePresets.Single();
        var lastField = PresetField(settingsWindow.ContentView!, presenter.PresetId(lastPreset));
        settingsWindow.MakeFirstResponder(lastField);
        lastField.SelectText(lastField);
        using (var text = new NSString("1333")) ((NSTextView)settingsWindow.FirstResponder!).InsertText(text, new NSRange(nint.MaxValue, 0));
        await presenter.CloseWindowAsync();
        await Task.Delay(300);
        if (!main.Main.Settings.ScalePresets.Any(item => item.Text == "1333")) throw new InvalidOperationException("关闭窗口丢失未提交预设草稿");
        checks.Add("preset close: uncommitted native editor draft is applied once and survives reopen");
        if (presenter.IsPresenting) throw new InvalidOperationException("关闭后设置窗口仍在展示");
        presenter.TryPresent(main.Main.Settings);
        await Task.Delay(400);
        if (presenter.WorkingSettings.Hotkeys.Count != main.Main.Settings.Hotkeys.Count)
            throw new InvalidOperationException("关闭后重开设置丢失功能行");
        presenter.ShowCategory(2);
        await Task.Delay(200);
        presenter.RenderTo(Path.Combine(output, "settings-reopen.png"));
        presenter.WorkingSettings.ScalePresets = new(originalPresets.Select(text => new PhotoViewer.ViewModels.Settings.SettingsViewModel.ScalePreset(text)));
        await presenter.CloseWindowAsync();
        checks.Add("close applies working copy / reopen restores all rows");
        await File.WriteAllLinesAsync(Path.Combine(output, "settings-ui-checks.txt"), checks);
    }

    /// <summary>系统内存提示必须给出总物理内存与系统当前可用内存，且与平台读数同源。</summary>
    private static void AssertSystemMemoryHint(string text, string stage)
    {
        var info = new MacMemoryBudgetProvider().GetSystemMemoryInfo();
        if (info.TotalBytes <= 0) throw new InvalidOperationException(stage + "：未能读到系统总物理内存");
        if (!ContainsSize(text, info.TotalBytes))
            throw new InvalidOperationException($"{stage}：系统内存提示未给出总物理内存 {info.TotalBytes} 字节：{text}");
        if (info.AvailableBytes < 0)
        {
            if (!text.Contains("未知", StringComparison.Ordinal))
                throw new InvalidOperationException(stage + "：系统可用内存读取失败时应如实显示“未知”：" + text);
            return;
        }
        if (info.AvailableBytes > info.TotalBytes)
            throw new InvalidOperationException($"{stage}：系统可用内存超过总内存：{info.AvailableBytes}/{info.TotalBytes}");
        // 提示随 2s 探测周期刷新，可读量可能滞后于本次直读；此时只要求两段尺寸都在场
        if (!ContainsSize(text, info.AvailableBytes) && CountSizes(text) < 2)
            throw new InvalidOperationException($"{stage}：系统内存提示未给出系统可用内存 {info.AvailableBytes} 字节：{text}");
    }

    /// <summary>张数文案只允许「可缓存 N 张适屏照片 / N 张 3300 万像素照片」（未知项写“未知”），不带“缓存能力/最大/约”。</summary>
    private static void AssertCountWording(string text, string stage)
    {
        foreach (var banned in new[] { "缓存能力", "最大", "约" })
            if (text.Contains(banned, StringComparison.Ordinal))
                throw new InvalidOperationException($"{stage}：张数文案仍含「{banned}」：{text}");
        ParseCounts(text, stage);
    }

    /// <summary>解析两档张数；数字缺失且不是“未知”即口径不符。</summary>
    private static (long? Screen, long? Full) ParseCounts(string text, string stage)
        => (ParseCountSlot(text, ScreenCountPattern, ScreenUnknownPattern, "适屏照片", stage),
            ParseCountSlot(text, FullCountPattern, FullUnknownPattern, "3300 万像素照片", stage));

    private static long? ParseCountSlot(string text, Regex countPattern, Regex unknownPattern, string slot, string stage)
    {
        if (countPattern.Match(text) is { Success: true } match && long.TryParse(match.Groups[1].Value, out var value))
            return value;
        if (unknownPattern.IsMatch(text)) return null;
        throw new InvalidOperationException($"{stage}：{slot}张数缺失或口径不符：{text}");
    }

    /// <summary>两档张数必须等于「所选内存上限 ÷ 单张字节数」向下取整：不乘安全比例，也不按预算/可用内存/数量截断。</summary>
    private static void AssertCountsForLimit((long? Screen, long? Full) counts, int limitMB, string stage)
    {
        long limitBytes = (long)limitMB * 1024 * 1024;
        int screenBytesPerPixel = BitmapLoader.ProxyBytesPerPixel;
        int fullBytesPerPixel = BitmapLoader.NormalFullBytesPerPixel;
        int proxyLongEdge = BitmapLoader.ProxyTargetLongEdge;
        if (proxyLongEdge > 0 && screenBytesPerPixel > 0)
        {
            long pixels = Math.Min((long)proxyLongEdge * proxyLongEdge * 2 / 3, FullPhotoPixels);
            AssertCount(counts.Screen, limitBytes / (pixels * screenBytesPerPixel), "适屏照片", stage);
        }
        else if (counts.Screen != null)
        {
            throw new InvalidOperationException(stage + "：视口未知时不得给出适屏张数：" + counts.Screen);
        }

        if (fullBytesPerPixel > 0)
            AssertCount(counts.Full, limitBytes / (FullPhotoPixels * fullBytesPerPixel), "3300 万像素照片", stage);
    }

    private static void AssertCount(long? actual, long expected, string slot, string stage)
    {
        if (actual is not { } value)
            throw new InvalidOperationException($"{stage}：{slot}张数应为具体数字，实际为“未知”");
        if (Math.Abs(value - expected) > 1)
            throw new InvalidOperationException($"{stage}：{slot}张数 {value} 与按所选上限换算的 {expected} 不符（应直接按内存上限估算）");
    }

    /// <summary>超限警告只允许「（当前设置超过 xx 上限）」，级别随原因固定：总内存/应用内存错误级，可用内存/设备缓存警告级。</summary>
    private static void AssertWarningSuffix(string text, PerfInfoLevel level, string stage)
    {
        var match = WarningPattern.Match(text);
        if (match.Success)
        {
            string cause = match.Groups[1].Value;
            var expected = cause is "系统总内存" or "应用内存" ? PerfInfoLevel.Error : PerfInfoLevel.Warning;
            if (level != expected)
                throw new InvalidOperationException($"{stage}：警告「当前设置超过{cause}上限」应为 {expected} 级，实际 {level}：{text}");
            return;
        }
        if (text.Contains("超过", StringComparison.Ordinal) || level != PerfInfoLevel.Normal)
            throw new InvalidOperationException($"{stage}：超限警告格式不符或级别缺原因：{text}");
    }

    /// <summary>3300 万像素（SETTINGS-06 完整照片口径）。</summary>
    private const long FullPhotoPixels = 33_000_000;

    private static readonly Regex ScreenCountPattern = new(@"可缓存\s*([0-9]+)\s*张适屏照片");
    private static readonly Regex ScreenUnknownPattern = new(@"适屏照片[：:]\s*未知");
    private static readonly Regex FullCountPattern = new(@"([0-9]+)\s*张\s*3300\s*万像素照片");
    private static readonly Regex FullUnknownPattern = new(@"3300\s*万像素照片[：:]\s*未知");
    private static readonly Regex WarningPattern = new(@"（当前设置超过(系统总内存|应用内存|系统可用内存|设备缓存)上限）");

    /// <summary>文本里是否给出该字节量的近似数值（B/KB/MB/GB/TB 与 KiB/MiB/GiB/TiB，兼容 1000 与 1024 换算口径）。</summary>
    private static bool ContainsSize(string text, long bytes)
    {
        if (bytes <= 0) return false;
        foreach (Match match in SizePattern.Matches(text))
        {
            double value = double.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
            double factor = match.Groups[2].Value.ToUpperInvariant() switch
            {
                "TB" or "TIB" => 1024d * 1024 * 1024 * 1024,
                "GB" or "GIB" => 1024d * 1024 * 1024,
                "MB" or "MIB" => 1024d * 1024,
                "KB" or "KIB" => 1024d,
                _ => 1d,
            };
            if (Math.Abs(value * factor - bytes) <= Math.Max(bytes * 0.1, 512d * 1024 * 1024)) return true;
        }
        return false;
    }

    private static readonly Regex SizePattern =
        new(@"([0-9]+(?:\.[0-9]+)?)\s*(TIB|GIB|MIB|KIB|TB|GB|MB|KB|B)", RegexOptions.IgnoreCase);

    /// <summary>文本里的内存尺寸数量（用于接受周期探测周期内"当前可用"读数的正常滞后）。</summary>
    private static int CountSizes(string text) => SizePattern.Matches(text).Count;

    private static NSTextField PresetField(NSView root, string id)
    {
        var field = Walk(root).OfType<NSTextField>().FirstOrDefault(field => field.Identifier == "preset-" + id);
        return field ?? throw new InvalidOperationException("未找到原生预设输入框：" + id);
    }

    private static IEnumerable<NSView> Walk(NSView root)
    {
        yield return root;
        foreach (var child in root.Subviews)
            foreach (var view in Walk(child)) yield return view;
    }

    /// <summary>断言截图已生成且有实际内容，避免空渲染冒充通过。</summary>
    private static void AssertRendered(string path, string message)
    {
        if (!File.Exists(path) || new FileInfo(path).Length < 2048) throw new InvalidOperationException(message + "：" + path);
    }

    /// <summary>尝试用系统 screencapture 抓取真实窗口位图；无屏幕录制权限时在报告中如实记录。</summary>
    private static void CaptureScreen(MacNativeSettingsPresenter presenter, string path, List<string> checks)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo("screencapture", $"-o -x -l {presenter.WindowNumber} \"{path}\"")
            {
                UseShellExecute = false,
            });
            if (process != null)
            {
                bool exited = process.WaitForExit(5000);
                if (exited && process.ExitCode == 0 && File.Exists(path) && new FileInfo(path).Length > 0)
                {
                    checks.Add("real window screen capture: settings-window.png");
                    return;
                }
                if (!exited) process.Kill();
            }
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine("[NativeSmoke] screencapture failed: " + exception.Message);
        }
        checks.Add("real window screen capture unavailable (屏幕录制权限未授予)");
    }

    /// <summary>向真实应用队列提交窗口键盘事件，经过同一 SwiftUI 录制监视器与 C# 手势判定。</summary>
    private static void PostKey(int windowNumber, ushort code, string text, NSEventModifierMask modifiers)
    {
        var nativeEvent = NSEvent.KeyEvent(NSEventType.KeyDown, CGPoint.Empty, modifiers, NSProcessInfo.ProcessInfo.SystemUptime,
            windowNumber, null!, text, text, false, code) ?? throw new InvalidOperationException("键盘事件创建失败");
        NSApplication.SharedApplication.PostEvent(nativeEvent, false);
    }
}
