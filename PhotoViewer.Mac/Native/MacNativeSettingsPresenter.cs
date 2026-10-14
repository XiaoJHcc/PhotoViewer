using AppKit;
using CoreGraphics;
using Foundation;
using System.Reactive.Linq;
using PhotoViewer.Core.Settings;
using PhotoViewer.ViewModels.Settings;

namespace PhotoViewer.Mac.Native;

/// <summary>macOS 原生设置，分类侧栏与系统表单；功能复用共享 VM 和 iOS 工作副本模式。</summary>
public sealed partial class MacNativeSettingsPresenter : INativeSettingsPresenter
{
    private NSWindow? _window;
    private NativeSettingsSession? _session;
    private readonly List<NSView> _rows = [];
    private FormView? _form;
    private NSScrollView? _scroll;
    private NSObject? _recordMonitor;
    private int _category;
    private bool _closing;
    private Action? _finishEditor;
    private CategorySource? _categorySource;
    private CategoryDelegate? _categoryDelegate;
    private NSTableView? _categoryTable;

    /// <summary>当前原生设置窗口，供回归验证取得已展示的实例。</summary>
    public NSWindow? Window => _window;
    /// <summary>验收工具检查工作副本隔离性，UI 外部不直接访问编辑实例。</summary>
    internal SettingsViewModel WorkingSettings => Settings;
    /// <summary>切换原生设置分类，供菜单导航与程序化冒烟复用。</summary>
    public void ShowCategory(int category)
    {
        int selected = Math.Clamp(category, 0, 4);
        if (_categoryTable != null && _categoryTable.SelectedRow != selected) { _categoryTable.SelectRow(selected, false); return; }
        _finishEditor?.Invoke(); _category = selected; BuildCategory();
    }
    /// <summary>复用已打开窗口，否则创建原生分类侧栏与可滚动表单。</summary>
    public bool TryPresent(SettingsViewModel settings)
    {
        if (_window != null) { _window.MakeKeyAndOrderFront(null); return true; }
        _session = new NativeSettingsSession(settings);
        _window = new NSWindow(new CGRect(180, 160, 800, 700), NSWindowStyle.Titled | NSWindowStyle.Closable | NSWindowStyle.Resizable, NSBackingStore.Buffered, false)
        {
            Title = "PhotoViewer 设置", MinSize = new CGSize(680, 500), BackgroundColor = NSColor.WindowBackground,
        };
        _window.FrameAutosaveName = "PhotoViewer.Native.Settings";
        var root = new SettingsRoot(new CGRect(0, 0, 800, 700));
        root.WantsLayer = true;
        root.Layer!.BackgroundColor = NSColor.WindowBackground.CGColor;
        var sidebar = new NSView { WantsLayer = true };
        sidebar.Layer!.BackgroundColor = NSColor.ControlBackground.CGColor;
        string[] names = ["文件", "预览", "控制", "EXIF", "AI"];
        var table = new NSTableView { HeaderView = null, RowHeight = 38, Style = NSTableViewStyle.SourceList };
        _categoryTable = table;
        table.AddColumn(new NSTableColumn("Category") { Width = 140 });
        _categorySource = new CategorySource(names);
        _categoryDelegate = new CategoryDelegate(names, row => { if (row >= 0) ShowCategory((int)row); });
        table.DataSource = _categorySource; table.Delegate = _categoryDelegate;
        table.Frame = new CGRect(0, 0, 144, names.Length * 38);
        table.ReloadData();
        var sidebarScroll = new NSScrollView { DocumentView = table, DrawsBackground = false, HasVerticalScroller = false };
        sidebar.AddSubview(sidebarScroll);
        _form = new FormView(new CGRect(0, 0, 600, 680));
        _scroll = new NSScrollView { DocumentView = _form, HasVerticalScroller = true, DrawsBackground = false };
        root.AddSubview(sidebar); root.AddSubview(_scroll); _window.ContentView = root;
        root.Resized = () =>
        {
            sidebar.Frame = new CGRect(0, 0, 160, root.Bounds.Height);
            sidebarScroll.Frame = new CGRect(8, 12, 144, Math.Max(1, root.Bounds.Height - 24));
            _scroll.Frame = new CGRect(160, 0, Math.Max(1, root.Bounds.Width - 160), root.Bounds.Height);
            LayoutForm();
        };
        _window.WillClose += (_, _) => CloseSession();
        table.SelectRow(0, false);
        root.Resized();
        _window.MakeKeyAndOrderFront(null);
        return true;
    }

    private SettingsViewModel Settings => _session?.WorkingSettings ?? throw new InvalidOperationException("设置会话未建立");

    /// <summary>原生窗口关闭时先完成编辑、校验，再一次性应用设置。</summary>
    private void CloseSession()
    {
        if (_closing) return;
        _closing = true;
        _finishEditor?.Invoke();
        StopRecording();
        _session?.Apply();
        _session?.Dispose();
        _finishEditor = null;
        _rows.Clear();
        if (_categoryTable != null) { _categoryTable.DataSource = null; _categoryTable.Delegate = null; }
        _form = null; _scroll = null; _categoryTable = null;
        _categorySource?.Dispose(); _categoryDelegate?.Dispose();
        _categorySource = null; _categoryDelegate = null;
        _window = null; _session = null; _closing = false;
    }

    /// <summary>切换设置分类，保留同一工作副本并按 macOS 含义分组。</summary>
    private void BuildCategory()
    {
        if (_form == null) return;
        StopRecording();
        foreach (var row in _rows) { row.RemoveFromSuperview(); row.Dispose(); }
        _rows.Clear(); _finishEditor = null;
        switch (_category)
        {
            case 0: BuildFile(); break;
            case 1: BuildPreview(); break;
            case 2: BuildControl(); break;
            case 3: BuildExif(); break;
            case 4: BuildAi(); break;
        }
        LayoutForm();
    }

    /// <summary>按右侧可用宽度布置原生表单，长内容使用系统滚动条。</summary>
    private void LayoutForm()
    {
        if (_form == null || _scroll == null) return;
        double width = Math.Max(480, (double)_scroll.ContentView.Bounds.Width);
        double top = 22;
        foreach (var row in _rows) { row.Frame = new CGRect(24, top, width - 48, row.Frame.Height); top += (double)row.Frame.Height + 8; }
        _form.Frame = new CGRect(0, 0, width, Math.Max(top + 16, (double)_scroll.ContentView.Bounds.Height));
    }

    /// <summary>把一行添加到原生表单。</summary>
    private void Add(NSView row) { _rows.Add(row); _form!.AddSubview(row); }
    /// <summary>创建系统表单组标题。</summary>
    private void Group(string title)
    {
        var label = Text(title, 14, true); label.Frame = new CGRect(0, 0, 500, 28); Add(label);
    }
    /// <summary>创建系统静态标签，字号与颜色使用 AppKit 默认。</summary>
    private static NSTextField Text(string text, double size = 13, bool emphasis = false)
        => new() { StringValue = text, Editable = false, Selectable = false, Bordered = false, DrawsBackground = false,
            Font = (emphasis ? NSFont.BoldSystemFontOfSize((nfloat)size) : NSFont.SystemFontOfSize((nfloat)size)) ?? throw new InvalidOperationException("系统字体不可用"), TextColor = NSColor.Label };
    /// <summary>一行左右标签/原生控件的布局。</summary>
    private NSView Row(string title, NSView control, double controlWidth = 180, double height = 40)
    {
        var row = new SettingRow(new CGRect(0, 0, 500, height));
        var label = Text(title); row.AddSubview(label); row.AddSubview(control);
        row.Resized = () => { label.Frame = new CGRect(0, 10, Math.Max(100, row.Bounds.Width - controlWidth - 16), height - 12); control.Frame = new CGRect(row.Bounds.Width - controlWidth, 7, controlWidth, height - 12); };
        row.Resized(); Add(row); return row;
    }
    /// <summary>原生布尔开关，改变工作副本。</summary>
    private void Toggle(string title, Func<bool> getter, Action<bool> setter, bool enabled = true)
    {
        var toggle = new NSSwitch { State = getter() ? 1 : 0, Enabled = enabled };
        toggle.Activated += (_, _) => setter(toggle.State == 1);
        Row(title, toggle, 40);
    }
    /// <summary>有限范围的数值编辑，编辑结束时才提交；非法输入恢复当前有效值。</summary>
    private void Number(string title, Func<double> getter, Action<double> setter, double min, double max, bool integer = true, string suffix = "")
    {
        var field = new NSTextField { StringValue = getter().ToString(integer ? "0" : "0.##", System.Globalization.CultureInfo.InvariantCulture), Alignment = NSTextAlignment.Right };
        void Apply()
        {
            if (double.TryParse(field.StringValue, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var value) && double.IsFinite(value) && value >= min && value <= max)
                setter(integer ? Math.Round(value) : value);
            field.StringValue = getter().ToString(integer ? "0" : "0.##", System.Globalization.CultureInfo.InvariantCulture);
        }
        field.EditingEnded += (_, _) => Apply();
        _finishEditor += Apply;
        Row(title + (suffix.Length > 0 ? " (" + suffix + ")" : ""), field, 110);
    }
    /// <summary>原生下拉选项，直接写入共享枚举/索引。</summary>
    private void Choice(string title, string[] options, int selected, Action<int> setter)
    {
        var popup = new NSPopUpButton(new CGRect(0, 0, 180, 28), false); popup.AddItems(options); popup.SelectItem(Math.Clamp(selected, 0, options.Length - 1));
        popup.Activated += (_, _) => setter((int)popup.IndexOfSelectedItem); Row(title, popup);
    }

    /// <summary>文件格式、合并、缓存/解码预算与预载；全部保留原共享范围。</summary>
    private void BuildFile()
    {
        Group("文件"); Toggle("同名文件视为同一张图片", () => Settings.SameNameAsOnePhoto, value => Settings.SameNameAsOnePhoto = value);
        Group("支持的文件格式");
        Add(MacReorderList.Create(Settings.FileFormats, item => item.DisplayName + "  " + item.ExtensionsText, item => item.IsEnabled, (item, value) => item.IsEnabled = value, Settings.MoveFileFormat));
        Group("缓存与预加载");
        Number("原生解码线程", () => Settings.NativePreloadParallelism, value => Settings.NativePreloadParallelism = (int)value, 1, Settings.NativePreloadParallelismMaximum);
        Number("软件解码线程", () => Settings.CpuPreloadParallelism, value => Settings.CpuPreloadParallelism = (int)value, 1, Settings.CpuPreloadParallelismMaximum);
        Number("缓存内存上限", () => Settings.BitmapCacheMaxMemory, value => Settings.BitmapCacheMaxMemory = (int)value, 512, 65536, suffix: "MB");
        Number("缓存数量上限", () => Settings.BitmapCacheMaxCount, value => Settings.BitmapCacheMaxCount = (int)value, 1, 4096);
        Number("下一张预载数量", () => Settings.PreloadForwardCount, value => Settings.PreloadForwardCount = (int)value, 0, Settings.PreloadMaximum);
        Number("上一张预载数量", () => Settings.PreloadBackwardCount, value => Settings.PreloadBackwardCount = (int)value, 0, Settings.PreloadMaximum);
        Number("列表滚动预载数量", () => Settings.VisibleCenterPreloadCount, value => Settings.VisibleCenterPreloadCount = (int)value, 0, Settings.PreloadMaximum);
        Number("滚动预载延时", () => Settings.VisibleCenterDelayMs, value => Settings.VisibleCenterDelayMs = (int)value, 100, 5000, suffix: "ms");
        var budget = Text(Settings.PerformanceBudgetInfo + "\n" + Settings.BitmapCacheCountInfo, 11); budget.Frame = new CGRect(0, 0, 500, 64); Add(budget);
    }
    /// <summary>缩放导航与预设，独立原生列表支持增删编辑。</summary>
    private void BuildPreview()
    {
        Group("预览"); Toggle("显示缩放指示器", () => Settings.ShowZoomIndicator, value => Settings.ShowZoomIndicator = value);
        Group("缩放比例预设");
        foreach (var preset in Settings.ScalePresets.ToArray())
        {
            var row = new SettingRow(new CGRect(0, 0, 500, 40));
            var field = new NSTextField { StringValue = preset.Text, Alignment = NSTextAlignment.Right };
            var percent = Text("%");
            var remove = NSButton.CreateButton("移除", () => { _finishEditor?.Invoke(); Settings.RemoveScalePreset(preset); BuildCategory(); });
            void Apply()
            {
                if (double.TryParse(field.StringValue, out double value) && double.IsFinite(value) && value > 0 && value <= 1600) { preset.Text = value.ToString(System.Globalization.CultureInfo.InvariantCulture); Settings.ApplyScalePreset(); }
                field.StringValue = preset.Text;
            }
            field.EditingEnded += (_, _) => Apply(); _finishEditor += Apply;
            row.AddSubview(field); row.AddSubview(percent); row.AddSubview(remove);
            row.Resized = () => { field.Frame = new CGRect(0, 7, 110, 26); percent.Frame = new CGRect(118, 10, 30, 24); remove.Frame = new CGRect(row.Bounds.Width - 80, 6, 80, 28); };
            row.Resized(); Add(row);
        }
        var add = NSButton.CreateButton("添加比例", () => { _finishEditor?.Invoke(); Settings.AddScalePreset(); BuildCategory(); });
        Row("", add, 120);
    }
    /// <summary>控制布局、按钮显隐/排序、键盘映射与完整可编辑快捷键。</summary>
    private void BuildControl()
    {
        Group("布局"); Choice("布局模式", Settings.LayoutModes.Select(mode => mode.DisplayName).ToArray(), Settings.LayoutModes.ToList().FindIndex(mode => mode.Value == Settings.LayoutMode), index => Settings.LayoutMode = Settings.LayoutModes[index].Value);
        Group("控制栏功能");
        Add(MacReorderList.Create(Settings.Hotkeys, item => item.Name, item => item.IsDisplay, (item, value) => item.IsDisplay = value, Settings.MoveHotkey));
        Group("苹果键盘映射"); Toggle("使用苹果键盘", () => Settings.UseAppleKeyboard, value => Settings.UseAppleKeyboard = value);
        string[] targets = ["不映射", "Ctrl", "Alt", "Win"];
        Choice("Command", targets, (int)Settings.MapCommandTarget, index => Settings.MapCommandTarget = (PhotoViewer.Core.Input.AppleMappingTarget)index);
        Choice("Option", targets, (int)Settings.MapOptionTarget, index => Settings.MapOptionTarget = (PhotoViewer.Core.Input.AppleMappingTarget)index);
        Choice("Control", targets, (int)Settings.MapControlTarget, index => Settings.MapControlTarget = (PhotoViewer.Core.Input.AppleMappingTarget)index);
        Group("快捷键");
        foreach (var hotkey in Settings.Hotkeys)
        {
            var row = new SettingRow(new CGRect(0, 0, 500, 42));
            var label = Text(hotkey.Name);
            var primary = NSButton.CreateButton(hotkey.PrimaryHotkeyText, () => { });
            var secondary = NSButton.CreateButton(hotkey.SecondaryHotkeyText, () => { });
            primary.Activated += (_, _) => BeginRecording(primary, hotkey, true);
            secondary.Activated += (_, _) => BeginRecording(secondary, hotkey, false);
            primary.ToolTip = hotkey.HasPrimaryConflict ? "快捷键冲突" : "点击录制；删除键清空，Escape 取消";
            secondary.ToolTip = hotkey.HasSecondaryConflict ? "快捷键冲突" : "点击录制；删除键清空，Escape 取消";
            row.AddSubview(label); row.AddSubview(primary); row.AddSubview(secondary);
            row.Resized = () => { label.Frame = new CGRect(0, 10, Math.Max(120, row.Bounds.Width - 280), 24); primary.Frame = new CGRect(row.Bounds.Width - 270, 6, 130, 28); secondary.Frame = new CGRect(row.Bounds.Width - 130, 6, 130, 28); };
            row.Resized(); Add(row);
        }
    }
    /// <summary>EXIF 项选择和排序、星级显示及安全写回。</summary>
    private void BuildExif()
    {
        Group("EXIF 显示项");
        Add(MacReorderList.Create(Settings.ExifDisplayItems, item => item.DisplayName, item => item.IsEnabled, (item, value) => item.IsEnabled = value, Settings.MoveExifDisplay));
        Group("评分"); Toggle("显示星级评分", () => Settings.ShowRating, value => Settings.ShowRating = value);
        Toggle("安全修改文件星级", () => Settings.SafeSetRating, value => Settings.SafeSetRating = value);
    }
    /// <summary>保留 AI 配置值，顺延功能暂不可编辑；清库命令仍需系统二次确认。</summary>
    private void BuildAi()
    {
        Group("相似聚类");
        var threshold = Text(Settings.SimilarityThreshold.ToString("P0")); threshold.TextColor = NSColor.SecondaryLabel;
        var count = Text(Settings.SimilarityMaxResults.ToString()); count.TextColor = NSColor.SecondaryLabel;
        Row("相似度阈值", threshold, 80); Row("最多数量", count, 80);
        Toggle("即时计算 DINO/CV", () => Settings.AnalysisImmediateCompute, _ => { }, false);
        Group("特征数据库");
        var clear = NSButton.CreateButton("清除特征数据库", () =>
        {
            using var alert = new NSAlert { MessageText = "清除特征数据库？", InformativeText = "已提取的 DINO/CV 特征将被清除。", AlertStyle = NSAlertStyle.Warning };
            alert.AddButton("清除"); alert.AddButton("取消");
            if (alert.RunModal() != 1000) return;
            _ = ClearDatabaseAsync();
        });
        Row("", clear, 180);
    }
    /// <summary>系统确认后顺序完成共享两段确认命令，避免 ReactiveCommand 的执行状态竞态。</summary>
    private async Task ClearDatabaseAsync()
    {
        var settings = Settings;
        if (!settings.ClearDbConfirming) await settings.ClearDatabaseCommand.Execute();
        await settings.ClearDatabaseCommand.Execute();
    }
    /// <summary>原生手势录制，设置窗口中只捕获下一次键/鼠标手势。</summary>
    private void BeginRecording(NSButton button, SettingsViewModel.HotkeyItem hotkey, bool primary)
    {
        StopRecording(); button.Title = "输入快捷键…";
        _recordMonitor = NSEvent.AddLocalMonitorForEventsMatchingMask(NSEventMask.KeyDown | NSEventMask.LeftMouseDown | NSEventMask.RightMouseDown | NSEventMask.OtherMouseDown | NSEventMask.ScrollWheel, nativeEvent =>
        {
            if (nativeEvent.Window != _window) return nativeEvent;
            if (nativeEvent.Type == NSEventType.KeyDown && nativeEvent.KeyCode == 53) { StopRecording(); BuildCategory(); return null!; }
            var gesture = nativeEvent.Type == NSEventType.KeyDown && nativeEvent.KeyCode is 51 or 117 ? null : MacInput.FromEvent(nativeEvent, Settings);
            if (gesture == null && nativeEvent.KeyCode is not (51 or 117)) return nativeEvent;
            if (primary) hotkey.PrimaryHotkey = gesture; else hotkey.SecondaryHotkey = gesture;
            Settings.CheckHotkeyConflicts(); StopRecording(); BuildCategory(); return null!;
        });
    }
    /// <summary>结束手势录制并移除系统监视器。</summary>
    private void StopRecording() { if (_recordMonitor != null) { NSEvent.RemoveMonitor(_recordMonitor); _recordMonitor.Dispose(); _recordMonitor = null; } }

    /// <summary>侧栏分类数据。</summary>
    private sealed class CategorySource(string[] categories) : NSTableViewDataSource
    {
        /// <summary>分类行数。</summary>
        public override nint GetRowCount(NSTableView tableView) => categories.Length;
    }
    /// <summary>原生分类行。</summary>
    private sealed class CategoryDelegate(string[] categories, Action<nint> selected) : NSTableViewDelegate
    {
        /// <summary>分类选中由同一原生 delegate 消费，避免 .NET 事件覆盖已接入的 delegate。</summary>
        public override void SelectionDidChange(NSNotification notification) { if (notification.Object is NSTableView table) selected(table.SelectedRow); }
        /// <summary>使用系统标签展示分类。</summary>
        public override NSView GetViewForItem(NSTableView tableView, NSTableColumn? tableColumn, nint row)
        {
            var label = Text(categories[(int)row]);
            label.Frame = new CGRect(8, 8, 130, 24);
            var cell = new NSTableCellView(new CGRect(0, 0, 144, 38)) { TextField = label };
            cell.AddSubview(label); return cell;
        }
    }
    /// <summary>表单采用向下的布局坐标。</summary>
    private sealed class FormView(CGRect frame) : NSView(frame) { public override bool IsFlipped => true; }
    /// <summary>自适应原生设置根视图。</summary>
    private sealed class SettingsRoot(CGRect frame) : NSView(frame)
    {
        public Action? Resized { get; set; }
        public override bool IsFlipped => true;
        /// <summary>尺寸变化重新布置侧栏与右侧表单。</summary>
        public override void SetFrameSize(CGSize newSize) { base.SetFrameSize(newSize); Resized?.Invoke(); }
    }
    /// <summary>左右标签和控件的自适应行。</summary>
    private sealed class SettingRow(CGRect frame) : NSView(frame)
    {
        public Action? Resized { get; set; }
        public override bool IsFlipped => true;
        /// <summary>行宽变化重新对齐控件。</summary>
        public override void SetFrameSize(CGSize newSize) { base.SetFrameSize(newSize); Resized?.Invoke(); }
    }
}
