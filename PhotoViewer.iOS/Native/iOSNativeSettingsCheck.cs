using Foundation;
using PhotoViewer.Apple;
using PhotoViewer.Core.Input;
using PhotoViewer.Core.Platform;
using PhotoViewer.iOS.Core;
using UIKit;

namespace PhotoViewer.iOS.Native;

internal static class iOSNativeSettingsCheck
{
    internal static async Task RunAsync(iOSNativeSettingsRootViewController root, List<string> lines)
    {
        var settings = root.Session.WorkingSettings;
        var snapshot = settings.CreateSnapshot();
        await Task.Delay(700);
        await UiDispatcher.InvokeAsync(() => root.RowSelected(root.TableView, NSIndexPath.FromRowSection(1, 0)));
        await WaitForPage<iOSNativePreviewSettingsViewController>(root);
        await UiDispatcher.InvokeAsync(() =>
        {
            var page = root.NavigationController!.TopViewController!;
            var editor = Descendants(page.View!).OfType<iOSScalePresetEditor>().Single();
            var preset = settings.ScalePresets[0];
            var field = editor.Fields[0];
            field.Text = "66.667";
            field.SendActionForControlEvents(UIControlEvent.EditingDidEnd);
            if (Math.Abs(preset.Value - .66667) > .000001 || !editor.Fields.Contains(field)) throw new InvalidOperationException("倍率提交重建了输入框或数值未写副本");
            field.Text = "";
            field.SendActionForControlEvents(UIControlEvent.EditingDidEnd);
            if (!editor.HasValidationError || field.Text != preset.Text) throw new InvalidOperationException("非法倍率未提示/恢复");
            int count = settings.ScalePresets.Count;
            editor.AddButton.SendActionForControlEvents(UIControlEvent.TouchUpInside);
            if (settings.ScalePresets.Count != count + 1 || !settings.ScalePresets.Any(value => value.Value == 1)) throw new InvalidOperationException("+未新增100%");
            while (settings.ScalePresets.Count > 1) editor.DeleteButtons[0].SendActionForControlEvents(UIControlEvent.TouchUpInside);
            if (editor.DeleteButtons.Single().Enabled) throw new InvalidOperationException("最后倍率项未禁删");
            root.NavigationController.PopViewController(false);
        });
        lines.Add("PASS SETTINGS-07 控件事件注入：单输入、失焦校验、非法恢复、保留输入框身份、+100%、最后项禁删（非手指实测）");
        await UiDispatcher.InvokeAsync(() => root.RowSelected(root.TableView, NSIndexPath.FromRowSection(2, 0)));
        await WaitForPage<iOSNativeControlSettingsViewController>(root);
        await VerifyBindingPresentationAsync(root, lines);
        await VerifyCaptureBlockingAsync(root, lines);
        await UiDispatcher.InvokeAsync(() =>
        {
            var page = (iOSNativeControlSettingsViewController)root.NavigationController!.TopViewController!;
            var editor = page.HotkeyEditor!;
            var item = settings.Hotkeys[0];
            var original = item.PrimaryHotkey;
            page.Capabilities.Inject(false, false);
            editor.BeginRecording(item, false);
            if (editor.IsRecording || editor.HasExternalInput) throw new InvalidOperationException("纯触控仍可录制");
            page.Capabilities.Inject(true, false);
            editor.BeginRecording(item, false);
            editor.CaptureKey(8, AppleInputMapping.CommandMask);
            var expected = AppleInputMapping.FromHidUsage(8, AppleInputMapping.CommandMask, settings);
            if (item.PrimaryHotkey?.Key?.Key != expected?.Key?.Key || item.PrimaryHotkey?.Key?.Modifiers != expected?.Key?.Modifiers) throw new InvalidOperationException("键盘录制未复用AppleInputMapping");
            settings.Hotkeys[1].PrimaryHotkey = item.PrimaryHotkey;
            settings.CheckHotkeyConflicts();
            if (!item.HasPrimaryConflict || !settings.Hotkeys[1].HasPrimaryConflict) throw new InvalidOperationException("冲突未同时标记双方");
            page.Capabilities.Inject(false, true);
            editor.BeginRecording(item, true);
            editor.CaptureMouse(2);
            if (item.SecondaryHotkey?.Mouse?.Action != MouseAction.MiddleClick) throw new InvalidOperationException("仅鼠标连接未允许完整录制");
            editor.BeginRecording(item, true);
            editor.CaptureMouse(0, -1);
            if (item.SecondaryHotkey?.Mouse?.Action != MouseAction.WheelDown) throw new InvalidOperationException("滚轮录制未生效");
            var before = item.PrimaryHotkey;
            editor.BeginRecording(item, false);
            page.Capabilities.Inject(false, false);
            if (editor.IsRecording || !ReferenceEquals(item.PrimaryHotkey, before)) throw new InvalidOperationException("最后外设断开未取消或丢失原绑定");
            page.Capabilities.Inject(true, false);
            editor.BeginRecording(item, false);
            editor.CaptureKey(41, 0);
            if (!ReferenceEquals(item.PrimaryHotkey, before)) throw new InvalidOperationException("Esc改写了绑定");
            editor.BeginRecording(item, false);
            editor.CaptureKey(42, 0);
            if (item.PrimaryHotkey != null) throw new InvalidOperationException("Backspace未清空");
            if (!ReferenceEquals(page.HotkeyEditor, editor)) throw new InvalidOperationException("能力变化重建页面");
            root.NavigationController.PopViewController(false);
            settings.ApplySnapshot(snapshot);
        });
        lines.Add("PASS INPUT-13 系统能力测试缝注入：无外设/仅键盘/仅鼠标，键盘/中键/滚轮录制、Esc/清空/断连取消、同工作副本与同页面；非硬件连接实测");
    }
    private static async Task VerifyBindingPresentationAsync(iOSNativeSettingsRootViewController root, List<string> lines)
    {
        var page = (iOSNativeControlSettingsViewController)root.NavigationController!.TopViewController!;
        var editor = page.HotkeyEditor!;
        var settings = root.Session.WorkingSettings;
        var item = settings.Hotkeys[0];
        var draft = item.PrimaryHotkey;
        settings.Hotkeys[1].PrimaryHotkey = draft;
        settings.CheckHotkeyConflicts();
        iOSHotkeyEditor.BindingCell? stableCell = null;
        foreach (var mode in new (bool Keyboard, bool Mouse, string Stage)[] { (false, false, "touch"), (true, false, "keyboard"), (false, true, "mouse"), (false, false, "touch-restored") })
        {
            await UiDispatcher.InvokeAsync(() =>
            {
                page.Capabilities.Inject(mode.Keyboard, mode.Mouse);
                page.View!.LayoutIfNeeded();
                editor.Table.LayoutIfNeeded();
                var cell = editor.Cells.FirstOrDefault(value => value.NameLabel.Text == item.Name)
                    ?? throw new InvalidOperationException("未生成真实快捷键行");
                cell.LayoutIfNeeded();
                if (cell.Window == null || stableCell != null && !ReferenceEquals(stableCell, cell))
                    throw new InvalidOperationException("能力变化重建了行或行未在真实视图树内");
                stableCell = cell;
                bool external = mode.Keyboard || mode.Mouse;
                foreach (var button in cell.BindingButtons.Concat(cell.ClearButtons))
                {
                    if (button.Hidden == external || button.AccessibilityElementsHidden == external || button.IsAccessibilityElement != external)
                        throw new InvalidOperationException("绑定/清空控件Hidden或AX与外设能力不符");
                    if (!external && button.Frame != CoreGraphics.CGRect.Empty)
                        throw new InvalidOperationException("纯触控仍保留绑定控件占位");
                }
                if (!external && cell.BindingButtons.Any(button => !string.IsNullOrEmpty(button.Title(UIControlState.Normal))))
                    throw new InvalidOperationException("纯触控仍泄露绑定/冲突文本");
                if (external && !cell.BindingButtons[0].Title(UIControlState.Normal)!.Contains("冲突", StringComparison.Ordinal))
                    throw new InvalidOperationException("有外设时未恢复冲突文本");
                if (Math.Abs((double)editor.Table.RowHeight - (external ? 100 : 52)) > .1
                    || Math.Abs((double)cell.Bounds.Height - (external ? 100 : 52)) > .1)
                    throw new InvalidOperationException($"能力变化未调整真实行高：{cell.Bounds.Height}/{editor.Table.RowHeight}");
                if (cell.NameLabel.Hidden || cell.DisplayToggle.Hidden || !cell.ShowsReorderControl || cell.NameLabel.Frame.Height <= 0)
                    throw new InvalidOperationException("纯触控未保留名称/显隐/排序");
                if (page.KeyboardMappingSection is not { } mapping || mapping.Hidden == external || mapping.AccessibilityElementsHidden == external)
                    throw new InvalidOperationException("Apple映射可见性/AX未同步外设能力");
                if (!ReferenceEquals(item.PrimaryHotkey, draft) || !item.HasPrimaryConflict)
                    throw new InvalidOperationException("能力变化丢失绑定草稿/冲突状态");
                for (var parent = editor.Superview; parent != null; parent = parent.Superview)
                    if (parent is UIScrollView scroll)
                    {
                        scroll.ScrollRectToVisible(editor.ConvertRectToView(new CoreGraphics.CGRect(0, 0, editor.Bounds.Width, 140), scroll), false);
                        break;
                    }
            });
            await SnapshotStageAsync(mode.Stage);
        }
        lines.Add("PASS INPUT-13 实际行控件：纯touch绑定/清空/冲突文本Hidden、AX隐藏且零占位/52高；keyboard-only与mouse-only均恢复完整控件/100高；名称显隐排序保留，cell身份与草稿不变");
    }

    private static async Task VerifyCaptureBlockingAsync(iOSNativeSettingsRootViewController root, List<string> lines)
    {
        var page = (iOSNativeControlSettingsViewController)root.NavigationController!.TopViewController!;
        var editor = page.HotkeyEditor!;
        var item = root.Session.WorkingSettings.Hotkeys[0];
        foreach (nint button in new nint[] { 0, 1, 2 })
        {
            await UiDispatcher.InvokeAsync(() =>
            {
                page.Capabilities.Inject(false, true);
                editor.BeginRecording(item, false);
                var surface = editor.CaptureSurface ?? throw new InvalidOperationException("录制没有透明捕获层");
                var cell = editor.Cells.First(value => value.NameLabel.Text == item.Name);
                bool displayed = cell.DisplayToggle.On;
                var point = cell.DisplayToggle.ConvertPointToView(new CoreGraphics.CGPoint(cell.DisplayToggle.Bounds.Width / 2, cell.DisplayToggle.Bounds.Height / 2), surface.Window);
                if (!ReferenceEquals(surface.Window!.HitTest(point, null), surface)
                    || !ReferenceEquals(surface.Window.HitTest(new CoreGraphics.CGPoint(surface.Bounds.Width / 2, 30), null), surface))
                    throw new InvalidOperationException("录制期间底层设置控件或导航仍能命中");
                if (!page.IsFirstResponder || !surface.AccessibilityViewIsModal)
                    throw new InvalidOperationException("录制未保持非文本原生响应者或AX未隔离");
                surface.PointerDown(button, AppleInputMapping.CommandMask);
                var expected = AppleInputMapping.FromRaw(AppleInputMapping.OtherMouseDown, 0, AppleInputMapping.CommandMask, button, 0, false, null, root.Session.WorkingSettings);
                if (item.PrimaryHotkey?.Mouse?.Action != expected?.Mouse?.Action || item.PrimaryHotkey?.Mouse?.Modifiers != expected?.Mouse?.Modifiers)
                    throw new InvalidOperationException("捕获层鼠标事件没有沿同一Apple映射写工作副本");
                if (!ReferenceEquals(surface.Window.HitTest(point, null), surface) || cell.DisplayToggle.On != displayed)
                    throw new InvalidOperationException("鼠标Down提交后捕获层提前消失或底层控件变化");
            });
            if (button == 0) await SnapshotStageAsync("recording-capture");
            await UiDispatcher.InvokeAsync(() => editor.CaptureSurface!.PointerUp());
            await Task.Delay(60);
            await UiDispatcher.InvokeAsync(() =>
            {
                if (editor.CaptureSurface != null) throw new InvalidOperationException("鼠标Up后捕获层未释放");
            });
        }
        await UiDispatcher.InvokeAsync(() =>
        {
            editor.BeginRecording(item, true);
            editor.CaptureMouse(0, 1);
            if (editor.CaptureSurface == null || item.SecondaryHotkey?.Mouse?.Action != MouseAction.WheelUp)
                throw new InvalidOperationException("滚轮未写绑定或未保留捕获层吞同轮事件");
        });
        await Task.Delay(280);
        await UiDispatcher.InvokeAsync(() =>
        {
            if (editor.CaptureSurface != null) throw new InvalidOperationException("滚轮捕获层空闲后未释放");
            foreach (var pair in new[] { (GestureKey.D1, "1"), (GestureKey.NumPad1, "数字 1"), (GestureKey.Oem1, ";"), (GestureKey.Oem5, "\\"), (GestureKey.Return, "↩"), (GestureKey.Delete, "⌦"), (GestureKey.Space, "空格") })
                if (iOSHotkeyEditor.KeyLabel(pair.Item1) != pair.Item2) throw new InvalidOperationException("数字/符号/编辑键仍显示存储枚举名");
        });
        lines.Add("PASS INPUT-08 捕获层：实际Window.HitTest阻断设置控件/导航，左/右/中键Down写共享映射且留层吞Up，Up后释放；滚轮留层吞事件后空闲释放；非系统鼠标注入");
        lines.Add("PASS 快捷键可读键名：数字/数字键盘/Oem别名/编辑键转键帽文本，非第二张命令表");
    }

    internal static async Task SnapshotStageAsync(string stage)
    {
        if (!NSProcessInfo.ProcessInfo.Arguments.Contains("--pv-native-ui-snapshots", StringComparer.Ordinal)) return;
        string directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "PhotoViewer");
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(Path.Combine(directory, "native-ui-snapshot-stage.txt"), stage);
        await Task.Delay(2200);
    }

    private static async Task WaitForPage<T>(iOSNativeSettingsRootViewController root) where T : UIViewController
    {
        for (int attempt = 0; attempt < 120; attempt++)
        {
            bool ready = await UiDispatcher.InvokeAsync(() =>
            {
                if (root.NavigationController?.TopViewController is not T page) return false;
                page.LoadViewIfNeeded();
                return page.ViewIfLoaded?.Window != null;
            });
            if (ready) { await Task.Delay(400); return; }
            await Task.Delay(50);
        }
        throw new InvalidOperationException($"设置导航未到达{typeof(T).Name}，实际{root.NavigationController?.TopViewController?.GetType().Name}");
    }
    private static IEnumerable<UIView> Descendants(UIView root)
    {
        foreach (var child in root.Subviews)
        {
            yield return child;
            foreach (var nested in Descendants(child)) yield return nested;
        }
    }
}
