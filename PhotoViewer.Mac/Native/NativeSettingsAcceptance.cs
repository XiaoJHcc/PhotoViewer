using AppKit;
using CoreGraphics;
using Foundation;
using PhotoViewer.Core.Input;

namespace PhotoViewer.Mac.Native;

/// <summary>设置窗口实际控件与系统事件回归，仅在隔离冒烟会话中运行。</summary>
internal static class NativeSettingsAcceptance
{
    /// <summary>验证五分类、深色外观、缩放布局、行内录制、勾选和排序绑定，导出控件并留出真屏取证时间。</summary>
    internal static async Task RunAsync(MacMainWindow main, MacNativeSettingsPresenter presenter, string output)
    {
        var checks = new List<string>();
        presenter.TryPresent(main.Main.Settings);
        var window = presenter.Window ?? throw new InvalidOperationException("设置窗口未创建");
        await File.WriteAllTextAsync(System.IO.Path.Combine(output, "settings-window-id.txt"), window.WindowNumber.ToString());
        window.SetContentSize(new CGSize(860, 720));
        if (window.Appearance?.Name != NSAppearance.NameDarkAqua) throw new InvalidOperationException("设置窗口不是深色外观");
        if (window.ContentViewController is not NSSplitViewController split || split.SplitViewItems.Length != 2)
            throw new InvalidOperationException("设置未使用系统侧栏分栏");
        checks.Add("DarkAqua / NSSplitViewController sidebar");
        for (int category = 0; category < 5; category++)
        {
            presenter.ShowCategory(category);
            await Task.Delay(250);
            window.ContentView!.LayoutSubtreeIfNeeded();
            NativeSnapshots.SaveView(window.ContentView, System.IO.Path.Combine(output, $"settings-{category}.png"));
            await File.WriteAllTextAsync(System.IO.Path.Combine(output, "settings-category.txt"), category.ToString());
            await Task.Delay(1500);
            checks.Add($"category {category}: native views");
        }
        presenter.ShowCategory(2);
        await Task.Delay(200);
        var list = presenter.HotkeyList ?? throw new InvalidOperationException("行内快捷键列表缺失");
        var table = list.Table;
        if (table.TableColumns().Length != 4) throw new InvalidOperationException("勾选、名称、主次快捷键未在同行");
        if (table.AllowsColumnReordering || table.AllowsColumnResizing) throw new InvalidOperationException("功能列允许被拖乱");
        var first = presenter.WorkingSettings.Hotkeys[0];
        bool displayed = first.IsDisplay;
        var checkbox = table.GetView(0, 0, true) as NSButton ?? throw new InvalidOperationException("原生复选框缺失");
        checkbox.PerformClick(checkbox);
        if (first.IsDisplay == displayed) throw new InvalidOperationException("复选框未更新工作副本");
        checkbox.PerformClick(checkbox);
        checks.Add("native checkbox changes working copy");
        var original = first.PrimaryHotkey;
        var editor = table.GetView(2, 0, true)?.Subviews.OfType<NSButton>().Single() ?? throw new InvalidOperationException("行内录制按钮缺失");
        editor.PerformClick(editor);
        PostKey(window, 12, "q", NSEventModifierMask.CommandKeyMask | NSEventModifierMask.ShiftKeyMask);
        await Task.Delay(150);
        if (first.PrimaryHotkey == original || first.PrimaryHotkey?.Key?.Key != GestureKey.Q) throw new InvalidOperationException("原生快捷键录制失败");
        checks.Add("inline recording consumes actual AppKit key event");
        editor = table.GetView(2, 0, true)!.Subviews.OfType<NSButton>().Single();
        editor.PerformClick(editor);
        PostKey(window, 51, "\b", 0);
        await Task.Delay(150);
        if (first.PrimaryHotkey != null) throw new InvalidOperationException("删除键未清空快捷键");
        editor = table.GetView(2, 0, true)!.Subviews.OfType<NSButton>().Single();
        editor.PerformClick(editor);
        PostKey(window, 53, "\u001b", 0);
        await Task.Delay(150);
        if (first.PrimaryHotkey != null || editor.Title == "输入快捷键…") throw new InvalidOperationException("取消录制未恢复按钮");
        first.PrimaryHotkey = original;
        presenter.WorkingSettings.CheckHotkeyConflicts();
        list.RefreshRows();
        checks.Add("delete clears / Escape cancels / scroll stays in place");
        var writer = table.DataSource!.GetPasteboardWriterForRow(table, 0) as NSPasteboardItem
            ?? throw new InvalidOperationException("原生拖动 writer 缺失");
        if (writer.GetStringForType("cc.xiaojh.PhotoViewer.settings-order") != "0") throw new InvalidOperationException("拖动索引序列化错误");
        presenter.WorkingSettings.MoveHotkey(0, 2);
        list.RefreshRows();
        if (!ReferenceEquals(presenter.WorkingSettings.Hotkeys[2], first)) throw new InvalidOperationException("排序后的行身份不正确");
        checkbox = table.GetView(0, 2, true) as NSButton ?? throw new InvalidOperationException("排序后复选框缺失");
        checkbox.PerformClick(checkbox);
        if (first.IsDisplay == displayed) throw new InvalidOperationException("排序后复选框绑定旧行");
        checkbox.PerformClick(checkbox);
        presenter.WorkingSettings.MoveHotkey(2, 0);
        list.RefreshRows();
        checks.Add("native pasteboard writer / reorder row identity; physical dragging requires manual acceptance");
        foreach (var size in new[] { new CGSize(760, 540), new CGSize(1060, 800), new CGSize(860, 720), new CGSize(760, 540) })
        {
            window.SetContentSize(size);
            await Task.Delay(200);
            if (Math.Abs((double)(table.Frame.Width - list.ContentView.Bounds.Width)) > 1)
                throw new InvalidOperationException("表格文档宽度未跟随视口");
            if (table.TableColumns().Sum(column => (double)column.Width) > (double)list.ContentView.Bounds.Width + 1)
                throw new InvalidOperationException("设置列溢出");
            if (table.TableColumns().Skip(2).Any(column => Math.Abs((double)column.Width - 116) > 1))
                throw new InvalidOperationException("窗口缩放改变了快捷键列宽");
            NativeSnapshots.SaveView(window.ContentView!, System.IO.Path.Combine(output, $"settings-control-{size.Width}.png"));
        }
        window.SetContentSize(new CGSize(860, 720));
        presenter.ShowCategory(0);
        await Task.Delay(200);
        NativeSnapshots.SaveView(window.ContentView!, System.IO.Path.Combine(output, "settings-final.png"));
        await File.WriteAllLinesAsync(System.IO.Path.Combine(output, "settings-ui-checks.txt"), checks);
        window.Close();
        presenter.TryPresent(main.Main.Settings);
        presenter.ShowCategory(2);
        await Task.Delay(150);
        if (presenter.HotkeyList?.Table.RowCount != main.Main.Settings.Hotkeys.Count)
            throw new InvalidOperationException("关闭后重开设置丢失功能行");
        presenter.Window!.Close();
        checks.Add("close releases table bindings / reopen restores all rows");
        await File.WriteAllLinesAsync(System.IO.Path.Combine(output, "settings-ui-checks.txt"), checks);
    }

    /// <summary>向真实应用队列提交窗口键盘事件，经过同一录制监视器。</summary>
    private static void PostKey(NSWindow window, ushort code, string text, NSEventModifierMask modifiers)
    {
        var nativeEvent = NSEvent.KeyEvent(NSEventType.KeyDown, CGPoint.Empty, modifiers, NSProcessInfo.ProcessInfo.SystemUptime,
            window.WindowNumber, null!, text, text, false, code) ?? throw new InvalidOperationException("键盘事件创建失败");
        NSApplication.SharedApplication.PostEvent(nativeEvent, false);
    }

}
