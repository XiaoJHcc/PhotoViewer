using Foundation;
using PhotoViewer.Apple;
using PhotoViewer.Core.Input;
using PhotoViewer.Core.Platform;
using PhotoViewer.iOS.Core;
using PhotoViewer.ViewModels.Settings;
using UIKit;

namespace PhotoViewer.iOS.Native;

internal static class iOSNativeSettingsCheck
{
    private static string BindingFrameNote = string.Empty;

    internal static async Task RunAsync(iOSNativeSettingsRootViewController root, List<string> lines)
    {
        var settings = root.Session.WorkingSettings;
        var snapshot = settings.CreateSnapshot();
        await Task.Delay(700);
        await SnapshotStageAsync("settings-root");
        await UiDispatcher.InvokeAsync(() => root.RowSelected(root.TableView, NSIndexPath.FromRowSection(1, 0)));
        await WaitForPage<iOSNativePreviewSettingsViewController>(root);
        await SnapshotStageAsync("settings-preview");
        await UiDispatcher.InvokeAsync(() =>
        {
            var page = root.NavigationController!.TopViewController!;
            var editor = Descendants(page.View!).OfType<iOSScalePresetEditor>().Single();
            editor.LayoutIfNeeded();
            var groups = editor.FieldGroups;
            var deletes = editor.DeleteButtons;
            for (int index = 0; index < groups.Count; index++)
            {
                var row = groups[index].Superview!;
                double groupCenter = (double)groups[index].Frame.GetMidX();
                if (Math.Abs(groupCenter - (double)row.Bounds.Width / 2) > .5)
                    throw new InvalidOperationException($"倍率输入组未在整行居中：{groupCenter}/{row.Bounds.Width}");
                if (deletes[index].Bounds.Width < 44 || deletes[index].Bounds.Height < 44)
                    throw new InvalidOperationException("删除按钮命中区小于44点");
                double deleteX = (double)deletes[index].ConvertRectToView(deletes[index].Bounds, editor).GetMidX();
                double addX = (double)editor.AddButton.ConvertRectToView(editor.AddButton.Bounds, editor).GetMidX();
                if (Math.Abs(deleteX - addX) > .5) throw new InvalidOperationException("新增按钮未与删除按钮同列");
            }
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
            editor.AddButton.SendActionForControlEvents(UIControlEvent.TouchUpInside);
            editor.AddButton.SendActionForControlEvents(UIControlEvent.TouchUpInside);
        });
        await SnapshotStageAsync("settings-preview-presets");
        await VerifyKeyboardDismissAsync(root, lines);
        await UiDispatcher.InvokeAsync(() => root.NavigationController!.PopViewController(false));
        await WaitForPage<iOSNativeSettingsRootViewController>(root);
        lines.Add("PASS SETTINGS-07 控件事件注入：输入组整行居中、删除/新增同列44点、失焦校验、非法恢复、保留输入框身份、+100%、最后项禁删（非手指实测）");
        await UiDispatcher.InvokeAsync(() => root.RowSelected(root.TableView, NSIndexPath.FromRowSection(2, 0)));
        await WaitForPage<iOSNativeControlSettingsViewController>(root);
        var control = (iOSNativeControlSettingsViewController)root.NavigationController!.TopViewController!;
        await VerifyListPresentationAsync(control, settings, lines);
        await VerifyInlineBindingAsync(control, settings, lines);
        await VerifyCaptureAsync(root, control, settings, lines);
        await UiDispatcher.InvokeAsync(() =>
        {
            control.Capabilities.Inject(false, false);
            root.NavigationController!.PopToViewController(root, false);
            settings.ApplySnapshot(snapshot);
        });
    }

    /// <summary>
    /// 软键盘收起：输入框获焦后经 Done 按钮或空白点击收起，提交后 1.5 秒内不得再次弹出键盘或抢回焦点。
    /// 空白点击走页面挂接的同一识别器委托判定 + 同一 EndEditing 动作（UITouch 不可合成）。
    /// </summary>
    private static async Task VerifyKeyboardDismissAsync(iOSNativeSettingsRootViewController root, List<string> lines)
    {
        bool keyboardVisible = false;
        double keyboardHeight = 0;
        DateTime lastHide = DateTime.MinValue, lastShow = DateTime.MinValue;
        using var showObserver = UIKeyboard.Notifications.ObserveWillShow((_, args) =>
        {
            keyboardVisible = true;
            keyboardHeight = Math.Max(keyboardHeight, (double)args.FrameEnd.Height);
            lastShow = DateTime.UtcNow;
        });
        using var hideObserver = UIKeyboard.Notifications.ObserveWillHide((_, _) =>
        {
            keyboardVisible = false;
            lastHide = DateTime.UtcNow;
        });
        foreach (var path in new[] { "done", "blank" })
        {
            iOSScalePresetEditor? editor = null;
            await UiDispatcher.InvokeAsync(() =>
            {
                var page = root.NavigationController!.TopViewController!;
                editor = Descendants(page.View!).OfType<iOSScalePresetEditor>().Single();
                if (!editor.Fields[0].BecomeFirstResponder()) throw new InvalidOperationException("倍率输入框无法获焦");
            });
            await Task.Delay(700);
            if (path == "done") await SnapshotStageAsync("settings-preview-keyboard");
            await UiDispatcher.InvokeAsync(() =>
            {
                var field = editor!.Fields[0];
                if (!field.IsFirstResponder) throw new InvalidOperationException("倍率输入框获焦后丢失焦点");
                field.Text = path == "done" ? "150" : "250";
                if (path == "done")
                {
                    var done = (field.InputAccessoryView as UIToolbar)?.Items?.LastOrDefault()
                        ?? throw new InvalidOperationException("倍率输入框缺少 Done");
                    if (done.Target is NSObject target && done.Action is { } action) target.PerformSelector(action, done, 0);
                    else throw new InvalidOperationException("Done 按钮没有动作");
                }
                else
                {
                    var page = root.NavigationController!.TopViewController!;
                    var scroll = page.View!.Subviews.OfType<UIScrollView>().Single();
                    var tap = scroll.GestureRecognizers?.OfType<UITapGestureRecognizer>().SingleOrDefault(recognizer => recognizer.Delegate is iOSNonControlTapDelegate)
                        ?? throw new InvalidOperationException("页面缺少空白点击收键盘识别器");
                    if (tap.CancelsTouchesInView) throw new InvalidOperationException("空白点击识别器吞掉了控件点击");
                    page.View.EndEditing(true);
                }
            });
            await Task.Delay(1500);
            await UiDispatcher.InvokeAsync(() =>
            {
                if (editor!.Fields.Any(field => field.IsFirstResponder)) throw new InvalidOperationException($"{path} 收起后输入框重新获焦");
                if (keyboardVisible || lastShow > lastHide) throw new InvalidOperationException($"{path} 收起后键盘再次弹出");
                if (!root.Session.WorkingSettings.ScalePresets.Any(preset => Math.Abs(preset.Value - (path == "done" ? 1.5 : 2.5)) < 1e-9))
                    throw new InvalidOperationException($"{path} 收起未提交倍率");
            });
        }
        lines.Add($"PASS SETTINGS-07 键盘收起：Done 与空白点击收起后提交倍率，1.5s 后键盘保持收起、无焦点抢回（键盘区高度 {keyboardHeight:0}；空白点击为同一识别器动作注入，非手指实测）");
    }

    /// <summary>控制栏功能列表：纯触控只留勾选/名称/排序，外设连接后同一行就地出现主/次绑定按钮。</summary>
    private static async Task VerifyListPresentationAsync(iOSNativeControlSettingsViewController page, SettingsViewModel settings, List<string> lines)
    {
        var list = page.HotkeyList ?? throw new InvalidOperationException("控制栏功能列表未创建");
        await UiDispatcher.InvokeAsync(() =>
        {
            page.View!.LayoutIfNeeded();
            var picker = Descendants(page.View).OfType<iOSLayoutModePicker>().Single();
            var table = picker.Subviews.OfType<UITableView>().Single();
            if (Math.Abs((double)(table.ContentSize.Height - picker.Bounds.Height)) > 1)
                throw new InvalidOperationException($"布局模式卡片高度与内容不符：{picker.Bounds.Height}/{table.ContentSize.Height}");
            foreach (var cell in table.VisibleCells)
                if (cell.Bounds.Height < 44) throw new InvalidOperationException("布局模式行高小于44");
            if (table.VisibleCells.Count(cell => cell.Accessory == UITableViewCellAccessory.Checkmark) != 1)
                throw new InvalidOperationException("布局模式不是三选一勾选");
        });
        var item = settings.Hotkeys[0];
        var draft = item.PrimaryHotkey;
        double scrollOffset = 0;
        iOSNativeEmbeddedCheckableReorderCell? stableCell = null;
        await UiDispatcher.InvokeAsync(() =>
        {
            settings.Hotkeys[1].PrimaryHotkey = draft;
            settings.CheckHotkeyConflicts();
            if (Ancestor<UIScrollView>(list) is { } scroll)
            {
                scroll.ScrollRectToVisible(list.ConvertRectToView(new CoreGraphics.CGRect(0, 0, list.Bounds.Width, 160), scroll), false);
                scroll.LayoutIfNeeded();
                scrollOffset = (double)scroll.ContentOffset.Y;
            }
        });
        foreach (var mode in new (bool Keyboard, bool Mouse, string Stage)[] { (false, false, "control-touch"), (true, false, "control-keyboard"), (false, true, "control-mouse"), (false, false, "control-touch-restored") })
        {
            await UiDispatcher.InvokeAsync(() =>
            {
                page.Capabilities.Inject(mode.Keyboard, mode.Mouse);
                page.View!.LayoutIfNeeded();
                bool external = mode.Keyboard || mode.Mouse;
                var cell = list.TableView.CellAt(NSIndexPath.FromRowSection(0, 0)) as iOSNativeEmbeddedCheckableReorderCell
                    ?? throw new InvalidOperationException("未生成真实快捷键行");
                if (cell.Window == null || stableCell != null && !ReferenceEquals(stableCell, cell))
                    throw new InvalidOperationException("能力变化重建了行或行未在真实视图树内");
                stableCell = cell;
                cell.LayoutIfNeeded();
                if (cell.TitleLabel.Text != item.Name || !cell.ShowsReorderControl || cell.CheckmarkView.Hidden == item.IsDisplay)
                    throw new InvalidOperationException("行未保留名称/勾选/排序");
                if (Math.Abs((double)cell.Bounds.Height - 44) > .5)
                    throw new InvalidOperationException($"控制栏功能行不是44点：{cell.Bounds.Height}");
                if (cell.EditingAccessory != UITableViewCellAccessory.None)
                    throw new InvalidOperationException("行尾不应再有 ⓘ 详情入口");
                if (!external)
                {
                    if (cell.TrailingAccessory != null || Descendants(cell).OfType<UIButton>().Any(button => !button.Hidden))
                        throw new InvalidOperationException("纯触控仍显示绑定按钮");
                    if (!string.IsNullOrEmpty(cell.AccessibilityValue))
                        throw new InvalidOperationException("纯触控仍有无障碍绑定值");
                    if (Math.Abs((double)cell.TitleLabel.Frame.GetMidY() - (double)cell.ContentView.Bounds.Height / 2) > 1)
                        throw new InvalidOperationException("纯触控标题未垂直居中");
                }
                else
                {
                    VerifyBindingButtons(page, cell, item);
                }
                if (page.KeyboardMappingSection is not { } mapping || mapping.Hidden == external || mapping.AccessibilityElementsHidden == external)
                    throw new InvalidOperationException("键盘映射可见性/AX未同步外设能力");
                if (external)
                {
                    mapping.LayoutIfNeeded();
                    var pickers = Descendants(mapping).OfType<UIButton>().Where(button => button.ShowsMenuAsPrimaryAction).ToArray();
                    if (pickers.Length != 3) throw new InvalidOperationException($"键盘映射弹出按钮数量异常：{pickers.Length}");
                    foreach (var picker in pickers)
                    {
                        var row = picker.Superview!.Superview!;
                        var frame = picker.ConvertRectToView(picker.Bounds, row);
                        var label = picker.Superview.Subviews.OfType<UILabel>().First();
                        var labelFrame = label.ConvertRectToView(label.Bounds, row);
                        string? title = picker.CurrentTitle ?? picker.Configuration?.Title;
                        if (!picker.ChangesSelectionAsPrimaryAction || picker.Configuration?.Indicator != UIButtonConfigurationIndicator.Popup
                            || string.IsNullOrEmpty(title) || row.Bounds.Height < 44
                            || frame.GetMaxX() > row.Bounds.Width + .5 || frame.IntersectsWith(labelFrame)
                            || Math.Abs((double)frame.GetMaxX() - ((double)row.Bounds.Width - 16)) > 1)
                            throw new InvalidOperationException($"键盘映射行不是标准弹出行：title={title} frame={frame} label={labelFrame} row={row.Bounds}");
                    }
                }
                if (!ReferenceEquals(item.PrimaryHotkey, draft) || !item.HasPrimaryConflict)
                    throw new InvalidOperationException("能力变化丢失绑定草稿/冲突状态");
                if (Ancestor<UIScrollView>(list) is { } scroll && Math.Abs((double)scroll.ContentOffset.Y - scrollOffset) > .5)
                    throw new InvalidOperationException("能力变化改变了滚动位置");
            });
            await SnapshotStageAsync(mode.Stage);
        }
        await UiDispatcher.InvokeAsync(() =>
        {
            bool displayed = item.IsDisplay;
            list.TableView.Source!.RowSelected(list.TableView, NSIndexPath.FromRowSection(0, 0));
            if (item.IsDisplay == displayed) throw new InvalidOperationException("点行未切换控制栏显示");
            list.TableView.Source.RowSelected(list.TableView, NSIndexPath.FromRowSection(0, 0));
            var second = settings.Hotkeys[1];
            list.TableView.Source.MoveRow(list.TableView, NSIndexPath.FromRowSection(1, 0), NSIndexPath.FromRowSection(0, 0));
            if (!ReferenceEquals(settings.Hotkeys[0], second)) throw new InvalidOperationException("拖拽排序未写工作副本");
            list.TableView.Source.MoveRow(list.TableView, NSIndexPath.FromRowSection(1, 0), NSIndexPath.FromRowSection(0, 0));
        });
        lines.Add("NOTE 行内绑定按钮几何：" + BindingFrameNote);
        lines.Add("PASS INPUT-13 控制栏功能列表：纯触控仅勾选/名称/排序、标题居中44高、无绑定按钮与AX；键盘或鼠标注入后同一cell就地出现两个108×28主/次绑定按钮（垂直居中、不压名称与拖拽手柄、标题取自同一格式化）、44高；滚动位置与草稿不变；点行勾选、拖拽排序写副本");
    }

    /// <summary>校验行内主/次绑定按钮的尺寸、位置、标题与无障碍信息（外设连接态）。</summary>
    private static void VerifyBindingButtons(iOSNativeControlSettingsViewController page, iOSNativeEmbeddedCheckableReorderCell cell, SettingsViewModel.HotkeyItem item)
    {
        var buttons = cell.TrailingAccessory as iOSHotkeyBindingButtons
            ?? throw new InvalidOperationException("外设连接后未在行内显示绑定按钮");
        var reorder = Descendants(cell).FirstOrDefault(view => view is UIControl and not UIButton && !view.Hidden);
        var reorderFrame = reorder?.ConvertRectToView(reorder.Bounds, cell) ?? CoreGraphics.CGRect.Empty;
        var titleFrame = cell.TitleLabel.ConvertRectToView(cell.TitleLabel.Bounds, cell);
        var contentFrame = cell.ContentView.ConvertRectToView(cell.ContentView.Bounds, cell);
        var frames = new List<CoreGraphics.CGRect>();
        foreach (var (button, secondary) in new[] { (buttons.PrimaryButton, false), (buttons.SecondaryButton, true) })
        {
            var frame = button.ConvertRectToView(button.Bounds, cell);
            frames.Add(frame);
            if (Math.Abs((double)frame.Width - 108) > .5 || Math.Abs((double)frame.Height - 28) > .5)
                throw new InvalidOperationException($"绑定按钮尺寸不是108×28：{frame}");
            if (Math.Abs((double)frame.GetMidY() - (double)cell.Bounds.Height / 2) > .5)
                throw new InvalidOperationException($"绑定按钮未在44点行内垂直居中：{frame} 行高 {cell.Bounds.Height}");
            if (frame.IntersectsWith(titleFrame))
                throw new InvalidOperationException($"绑定按钮与命令名重叠：{frame} title={titleFrame}");
            if (reorder != null && frame.IntersectsWith(reorderFrame))
                throw new InvalidOperationException($"绑定按钮与拖拽手柄重叠：{frame} reorder={reorderFrame}");
            if (frame.X < contentFrame.X - .5 || frame.GetMaxX() > contentFrame.GetMaxX() + .5)
                throw new InvalidOperationException($"绑定按钮超出内容区：{frame} content={contentFrame}");
            string expectedTitle = page.Recorder.Display(secondary ? item.SecondaryHotkey : item.PrimaryHotkey);
            var gesture = secondary ? item.SecondaryHotkey : item.PrimaryHotkey;
            bool conflict = gesture != null && (secondary ? item.HasSecondaryConflict : item.HasPrimaryConflict);
            string expectedValue = conflict ? expectedTitle + "，冲突" : expectedTitle;
            if (TitleOf(button) != expectedTitle)
                throw new InvalidOperationException($"绑定按钮标题不是当前手势：{TitleOf(button)}/{expectedTitle}");
            if (button.AccessibilityValue != expectedValue || !button.IsAccessibilityElement)
                throw new InvalidOperationException($"绑定按钮读屏信息不完整：{button.AccessibilityValue}/{expectedValue}");
        }
        if (frames[1].X < frames[0].GetMaxX())
            throw new InvalidOperationException($"主/次按钮相互重叠：{frames[0]} {frames[1]}");
        BindingFrameNote = $"主 {frames[0]} 次 {frames[1]} 拖拽 {reorderFrame} 名称 {titleFrame} 内容 {contentFrame}";
    }

    /// <summary>行内绑定按钮：点按钮录制、键盘提交、Esc 取消、Delete 清空、菜单清空、冲突橙色、纯触控隐藏。</summary>
    private static async Task VerifyInlineBindingAsync(iOSNativeControlSettingsViewController page, SettingsViewModel settings, List<string> lines)
    {
        var item = settings.Hotkeys[0];
        var other = settings.Hotkeys[1];
        var originalPrimary = item.PrimaryHotkey;
        var originalSecondary = item.SecondaryHotkey;
        var recorder = page.Recorder;
        var list = page.HotkeyList ?? throw new InvalidOperationException("控制栏功能列表未创建");
        await UiDispatcher.InvokeAsync(() =>
        {
            page.Capabilities.Inject(true, false);
            page.View!.LayoutIfNeeded();
            var cell = list.TableView.CellAt(NSIndexPath.FromRowSection(0, 0)) as iOSNativeEmbeddedCheckableReorderCell
                ?? throw new InvalidOperationException("未生成真实快捷键行");
            var buttons = cell.TrailingAccessory as iOSHotkeyBindingButtons
                ?? throw new InvalidOperationException("键盘连接后未在行内显示绑定按钮");
            var primary = buttons.PrimaryButton;

            primary.SendActionForControlEvents(UIControlEvent.TouchUpInside);
            if (!recorder.IsRecordingSlot(item, false))
                throw new InvalidOperationException("点主绑定按钮未进入录制态");
            if (!page.IsFirstResponder) throw new InvalidOperationException("录制未让控制页成为按键响应者");
            if (!buttons.IsTinted(false) || TitleOf(primary) != "按下按键…")
                throw new InvalidOperationException($"录制槽位未显示按下按键…：{TitleOf(primary)}");
            if (buttons.IsTinted(true)) throw new InvalidOperationException("非录制槽位也换成了录制配色");
        });
        await SnapshotStageAsync("control-recording");
        await UiDispatcher.InvokeAsync(() =>
        {
            var cell = list.TableView.CellAt(NSIndexPath.FromRowSection(0, 0)) as iOSNativeEmbeddedCheckableReorderCell;
            var buttons = (iOSHotkeyBindingButtons)cell!.TrailingAccessory!;
            var primary = buttons.PrimaryButton;
            recorder.CaptureKey(8, AppleInputMapping.CommandMask);
            var expected = AppleInputMapping.FromHidUsage(8, AppleInputMapping.CommandMask, settings);
            if (recorder.IsRecording || item.PrimaryHotkey?.Key?.Key != expected?.Key?.Key
                || item.PrimaryHotkey?.Key?.Modifiers != expected?.Key?.Modifiers)
                throw new InvalidOperationException("键盘录制未复用AppleInputMapping");
            if (TitleOf(primary) != recorder.Display(item.PrimaryHotkey) || buttons.IsTinted(false))
                throw new InvalidOperationException($"提交后行内按钮未刷新：{TitleOf(primary)}");

            other.PrimaryHotkey = item.PrimaryHotkey;
            settings.CheckHotkeyConflicts();
            page.RefreshHotkeyRows();
            if (!item.HasPrimaryConflict || !other.HasPrimaryConflict)
                throw new InvalidOperationException("冲突未同时标记双方");
            if (!(TitleColorOf(primary)?.Equals(UIColor.SystemOrange) ?? false)
                || primary.AccessibilityValue?.Contains("冲突", StringComparison.Ordinal) != true)
                throw new InvalidOperationException($"冲突槽位未以橙色标出：{TitleColorOf(primary)} {primary.AccessibilityValue}");

            var before = item.PrimaryHotkey;
            primary.SendActionForControlEvents(UIControlEvent.TouchUpInside);
            recorder.CaptureKey(41, 0);
            if (recorder.IsRecording || !ReferenceEquals(item.PrimaryHotkey, before))
                throw new InvalidOperationException("Esc改写了绑定或未退出录制");

            primary.SendActionForControlEvents(UIControlEvent.TouchUpInside);
            recorder.CaptureKey(42, 0);
            if (item.PrimaryHotkey != null || item.HasPrimaryConflict || other.HasPrimaryConflict)
                throw new InvalidOperationException("Delete未清空或未重算冲突");
            if (TitleOf(primary) != "未设置")
                throw new InvalidOperationException($"清空后按钮未显示未设置：{TitleOf(primary)}");

            item.SecondaryHotkey = before;
            settings.CheckHotkeyConflicts();
            page.RefreshHotkeyRows();
            var secondary = buttons.SecondaryButton;
            var menu = secondary.Menu ?? throw new InvalidOperationException("绑定按钮缺少清空菜单");
            var actions = menu.Children.OfType<UIAction>().ToArray();
            if (actions.Length != 1 || actions[0].Title != "清空"
                || (actions[0].Attributes & UIMenuElementAttributes.Destructive) == 0)
                throw new InvalidOperationException("清空菜单不是单条破坏性动作");
            if (secondary.ShowsMenuAsPrimaryAction)
                throw new InvalidOperationException("清空菜单抢占了按钮主操作");
            buttons.ClearAction(true);
            if (item.SecondaryHotkey != null || TitleOf(secondary) != "未设置")
                throw new InvalidOperationException("菜单清空未清掉次绑定");

            bool displayed = item.IsDisplay;
            list.TableView.Source!.RowSelected(list.TableView, NSIndexPath.FromRowSection(0, 0));
            if (item.IsDisplay == displayed)
                throw new InvalidOperationException("行内按钮存在时点行未切换勾选");
            list.TableView.Source.RowSelected(list.TableView, NSIndexPath.FromRowSection(0, 0));

            primary.SendActionForControlEvents(UIControlEvent.TouchUpInside);
            if (!recorder.IsRecording) throw new InvalidOperationException("断开前未进入录制态");
            page.Capabilities.Inject(false, false);
            if (recorder.IsRecording || recorder.CaptureSurface != null)
                throw new InvalidOperationException("最后外设断开未取消录制");
            var touchCell = list.TableView.CellAt(NSIndexPath.FromRowSection(0, 0)) as iOSNativeEmbeddedCheckableReorderCell;
            if (touchCell?.TrailingAccessory != null || Descendants(touchCell!).OfType<UIButton>().Any(button => !button.Hidden))
                throw new InvalidOperationException("断开外设后行内仍显示绑定按钮");
            if (!(iOSHotkeyRecorder.KeyLabel(GestureKey.D1) == "1" && iOSHotkeyRecorder.KeyLabel(GestureKey.NumPad1) == "数字 1"
                && iOSHotkeyRecorder.KeyLabel(GestureKey.Oem1) == ";" && iOSHotkeyRecorder.KeyLabel(GestureKey.Oem5) == "\\"
                && iOSHotkeyRecorder.KeyLabel(GestureKey.Return) == "↩" && iOSHotkeyRecorder.KeyLabel(GestureKey.Delete) == "⌦"
                && iOSHotkeyRecorder.KeyLabel(GestureKey.Space) == "空格"))
                throw new InvalidOperationException("数字/符号/编辑键仍显示存储枚举名");

            item.PrimaryHotkey = originalPrimary;
            item.SecondaryHotkey = originalSecondary;
            settings.CheckHotkeyConflicts();
            page.RefreshHotkeyRows();
        });
        lines.Add("PASS INPUT-13 行内绑定：点按钮录制（tinted+按下按键…并成为按键响应者）、键盘⌘提交走AppleInputMapping、Esc取消、Delete清空并重算冲突、长按菜单单条破坏性「清空」清掉次绑定、冲突橙色+读屏冲突、按钮不抢行勾选；断开最后外设取消录制并隐藏按钮；系统能力测试缝注入，非硬件连接实测");
        lines.Add("PASS 快捷键可读键名：数字/数字键盘/Oem别名/编辑键转键帽文本，非第二张命令表");
    }

    /// <summary>录制捕获层：阻断底层控件/导航，主键按左键、右/中键与滚轮写映射并留层；直接触控取消录制。</summary>
    private static async Task VerifyCaptureAsync(iOSNativeSettingsRootViewController root, iOSNativeControlSettingsViewController page,
        SettingsViewModel settings, List<string> lines)
    {
        var item = settings.Hotkeys[0];
        var recorder = page.Recorder;
        var list = page.HotkeyList!;
        await UiDispatcher.InvokeAsync(() => page.Capabilities.Inject(false, true));
        foreach (nint button in new nint[] { 0, 1, 2 })
        {
            await UiDispatcher.InvokeAsync(() =>
            {
                page.BeginRecording(item, false);
                var surface = recorder.CaptureSurface ?? throw new InvalidOperationException("录制没有透明捕获层");
                var target = list.TableView.CellAt(NSIndexPath.FromRowSection(0, 0)) as iOSNativeEmbeddedCheckableReorderCell
                    ?? throw new InvalidOperationException("未找到快捷键行");
                var point = target.ConvertPointToView(new CoreGraphics.CGPoint(target.Bounds.Width / 2, target.Bounds.Height / 2), surface.Window);
                if (!ReferenceEquals(surface.Window!.HitTest(point, null), surface)
                    || !ReferenceEquals(surface.Window.HitTest(new CoreGraphics.CGPoint(surface.Bounds.Width / 2, 30), null), surface))
                    throw new InvalidOperationException("录制期间底层设置控件或导航仍能命中");
                if (!page.IsFirstResponder || !surface.AccessibilityViewIsModal)
                    throw new InvalidOperationException("录制未保持非文本原生响应者或AX未隔离");
                surface.PointerDown(button, AppleInputMapping.CommandMask);
                int eventType = button == 0 ? AppleInputMapping.LeftMouseDown : AppleInputMapping.OtherMouseDown;
                var expected = AppleInputMapping.FromRaw(eventType, 0, AppleInputMapping.CommandMask, button, 0, false, null, settings);
                if (item.PrimaryHotkey?.Mouse?.Action != expected?.Mouse?.Action
                    || item.PrimaryHotkey?.Mouse?.Modifiers != expected?.Mouse?.Modifiers)
                    throw new InvalidOperationException($"捕获层鼠标按钮 {button} 没有沿同一Apple映射写工作副本");
                if (!ReferenceEquals(surface.Window.HitTest(point, null), surface))
                    throw new InvalidOperationException("鼠标Down提交后捕获层提前消失");
            });
            await UiDispatcher.InvokeAsync(() => recorder.CaptureSurface!.PointerUp());
            await Task.Delay(60);
            await UiDispatcher.InvokeAsync(() =>
            {
                if (recorder.CaptureSurface != null) throw new InvalidOperationException("鼠标Up后捕获层未释放");
            });
        }
        await UiDispatcher.InvokeAsync(() =>
        {
            page.BeginRecording(item, true);
            recorder.CaptureMouse(0, 1);
            if (recorder.CaptureSurface == null || item.SecondaryHotkey?.Mouse?.Action != MouseAction.WheelUp)
                throw new InvalidOperationException("滚轮未写绑定或未保留捕获层吞同轮事件");
        });
        await Task.Delay(280);
        await UiDispatcher.InvokeAsync(() =>
        {
            if (recorder.CaptureSurface != null) throw new InvalidOperationException("滚轮捕获层空闲后未释放");

            // 直接触控落在捕获层上即退出录制（保留原绑定），随后页面立刻恢复响应。
            page.BeginRecording(item, false);
            var surface = recorder.CaptureSurface ?? throw new InvalidOperationException("直接触控前没有捕获层");
            var before = item.PrimaryHotkey;
            surface.CancelFromDirectTouch();
            if (recorder.IsRecording || recorder.CaptureSurface != null || !ReferenceEquals(item.PrimaryHotkey, before))
                throw new InvalidOperationException("直接触控未取消录制或改写了绑定");
            var cell = list.TableView.CellAt(NSIndexPath.FromRowSection(0, 0)) as iOSNativeEmbeddedCheckableReorderCell
                ?? throw new InvalidOperationException("未找到快捷键行");
            var window = page.View?.Window ?? throw new InvalidOperationException("控制页不在窗口内");
            var point = cell.ConvertPointToView(new CoreGraphics.CGPoint(cell.Bounds.Width / 2, cell.Bounds.Height / 2), window);
            if (ReferenceEquals(window.HitTest(point, null), surface))
                throw new InvalidOperationException("取消后捕获层仍拦截触摸");
            bool displayed = item.IsDisplay;
            list.TableView.Source!.RowSelected(list.TableView, NSIndexPath.FromRowSection(0, 0));
            if (item.IsDisplay == displayed) throw new InvalidOperationException("取消录制后点行未切换勾选");
            list.TableView.Source.RowSelected(list.TableView, NSIndexPath.FromRowSection(0, 0));
        });
        await UiDispatcher.InvokeAsync(() =>
        {
            page.BeginRecording(item, false);
            root.NavigationController!.PopViewController(false);
        });
        await WaitForPage<iOSNativeSettingsRootViewController>(root);
        await UiDispatcher.InvokeAsync(() =>
        {
            if (recorder.IsRecording || recorder.CaptureSurface != null) throw new InvalidOperationException("离开控制页未取消录制");
        });
        lines.Add("PASS INPUT-08 捕获层：实际Window.HitTest阻断控制页控件/导航，主键按左键、右/中键Down写共享映射且留层吞Up，Up后释放；滚轮留层吞事件后空闲释放；离页取消录制；非系统鼠标注入");
        lines.Add("PASS INPUT-14 直接触控取消：录制中直接触控捕获层 → IsRecording=false、原绑定不变、CaptureSurface=null，随后点行勾选立即生效（直接触控为同一入口注入，非手指实测）");
    }

    internal static async Task SnapshotStageAsync(string stage)
    {
        if (!NSProcessInfo.ProcessInfo.Arguments.Contains("--pv-native-ui-snapshots", StringComparer.Ordinal)) return;
        string directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "PhotoViewer");
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(Path.Combine(directory, "native-ui-snapshot-stage.txt"), stage);
        await Task.Delay(2200);
    }

    private static async Task<T> WaitForPage<T>(iOSNativeSettingsRootViewController root) where T : UIViewController
    {
        for (int attempt = 0; attempt < 120; attempt++)
        {
            var page = await UiDispatcher.InvokeAsync(() =>
            {
                if (root.NavigationController?.TopViewController is not T top) return null;
                top.LoadViewIfNeeded();
                return top.ViewIfLoaded?.Window != null && root.NavigationController.GetTransitionCoordinator() == null ? top : null;
            });
            if (page != null) { await Task.Delay(400); return page; }
            await Task.Delay(50);
        }
        throw new InvalidOperationException($"设置导航未到达{typeof(T).Name}，实际{root.NavigationController?.TopViewController?.GetType().Name}");
    }

    private static UIResponder? FindFirstResponder(UIView? view)
    {
        if (view == null) return null;
        if (view.IsFirstResponder) return view;
        foreach (var child in view.Subviews)
            if (FindFirstResponder(child) is { } found) return found;
        return null;
    }

    private static T? Ancestor<T>(UIView view) where T : UIView
    {
        for (var parent = view.Superview; parent != null; parent = parent.Superview)
            if (parent is T match) return match;
        return null;
    }

    /// <summary>按钮当前标题（configuration 的富文本标题优先）。</summary>
    private static string TitleOf(UIButton button)
        => button.Configuration?.AttributedTitle?.Value ?? button.Configuration?.Title ?? string.Empty;

    /// <summary>按钮标题颜色：富文本属性优先，其次取 configuration 的基础前景色。</summary>
    private static UIColor? TitleColorOf(UIButton button)
    {
        if (button.Configuration?.AttributedTitle is { Length: > 0 } title
            && title.GetAttribute(UIStringAttributeKey.ForegroundColor, 0, out _) is UIColor color)
        {
            return color;
        }
        return button.Configuration?.BaseForegroundColor;
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
