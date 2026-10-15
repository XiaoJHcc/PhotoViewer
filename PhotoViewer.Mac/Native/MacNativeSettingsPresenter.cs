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
    private NSSplitViewController? _split;
    private SettingGroup? _group;
    private MacReorderList? _hotkeyList;
    private Action? _cancelRecording;

    /// <summary>当前原生设置窗口，供回归验证取得已展示的实例。</summary>
    public NSWindow? Window => _window;
    /// <summary>验收工具检查工作副本隔离性，UI 外部不直接访问编辑实例。</summary>
    internal SettingsViewModel WorkingSettings => Settings;
    /// <summary>验收访问当前行内快捷键表格。</summary>
    internal MacReorderList? HotkeyList => _hotkeyList;
    /// <summary>切换原生设置分类，供菜单导航与程序化冒烟复用。</summary>
    public void ShowCategory(int category)
    {
        int selected = Math.Clamp(category, 0, 4);
        if (_categoryTable != null && _categoryTable.SelectedRow != selected) { _categoryTable.SelectRow(selected, false); return; }
        _finishEditor?.Invoke(); _category = selected; BuildCategory();
        if (_scroll != null) { _scroll.ContentView.ScrollToPoint(CGPoint.Empty); _scroll.ReflectScrolledClipView(_scroll.ContentView); }
    }
    /// <summary>复用已打开窗口，否则创建原生分类侧栏与可滚动表单。</summary>
    public bool TryPresent(SettingsViewModel settings)
    {
        if (_window != null) { _window.MakeKeyAndOrderFront(null); return true; }
        _session = new NativeSettingsSession(settings);
        _window = new NSWindow(new CGRect(180, 160, 860, 720), NSWindowStyle.Titled | NSWindowStyle.Closable | NSWindowStyle.Resizable, NSBackingStore.Buffered, false)
        {
            Title = "PhotoViewer 设置", MinSize = new CGSize(760, 540), BackgroundColor = NSColor.WindowBackground,
            Appearance = NSAppearance.GetAppearance(NSAppearance.NameDarkAqua),
            TitlebarSeparatorStyle = NSTitlebarSeparatorStyle.None,
        };
        _window.FrameAutosaveName = "PhotoViewer.Native.Settings";
        var sidebar = new NSVisualEffectView(new CGRect(0, 0, 190, 720))
        {
            Material = NSVisualEffectMaterial.Sidebar, BlendingMode = NSVisualEffectBlendingMode.BehindWindow,
            State = NSVisualEffectState.FollowsWindowActiveState,
        };
        string[] names = ["文件", "预览", "控制", "EXIF", "AI"];
        var table = new NSTableView
        {
            HeaderView = null, RowHeight = 32, Style = NSTableViewStyle.SourceList,
            BackgroundColor = NSColor.Clear, IntercellSpacing = new CGSize(0, 6),
            ColumnAutoresizingStyle = NSTableViewColumnAutoresizingStyle.LastColumnOnly,
        };
        _categoryTable = table;
        table.AddColumn(new NSTableColumn("Category") { Width = 180 });
        _categorySource = new CategorySource(names);
        _categoryDelegate = new CategoryDelegate(names, row => { if (row >= 0) ShowCategory((int)row); });
        table.DataSource = _categorySource; table.Delegate = _categoryDelegate;
        table.Frame = new CGRect(0, 0, 190, names.Length * 38 + 16);
        table.ReloadData();
        var sidebarScroll = new NSScrollView(new CGRect(0, 12, 190, 708))
        {
            DocumentView = table, DrawsBackground = false, HasVerticalScroller = false,
            BorderType = NSBorderType.NoBorder, AutoresizingMask = NSViewResizingMask.WidthSizable | NSViewResizingMask.HeightSizable,
            AutomaticallyAdjustsContentInsets = false,
        };
        sidebar.AddSubview(sidebarScroll);
        _form = new FormView(new CGRect(0, 0, 600, 680));
        _scroll = new NSScrollView
        {
            DocumentView = _form, HasVerticalScroller = true, DrawsBackground = false,
            BorderType = NSBorderType.NoBorder, ScrollerStyle = NSScrollerStyle.Overlay, AutomaticallyAdjustsContentInsets = false,
        };
        var detail = new SettingsRoot(new CGRect(0, 0, 670, 720));
        detail.AddSubview(_scroll);
        detail.Resized = () =>
        {
            _scroll.Frame = detail.Bounds;
            LayoutForm();
        };
        _split = new NSSplitViewController();
        _split.SplitView.IsVertical = true;
        _split.SplitView.DividerStyle = NSSplitViewDividerStyle.Thin;
        var sidebarItem = NSSplitViewItem.CreateSidebar(new NSViewController { View = sidebar });
        sidebarItem.MinimumThickness = 180; sidebarItem.MaximumThickness = 220; sidebarItem.CanCollapse = false;
        _split.AddSplitViewItem(sidebarItem);
        _split.AddSplitViewItem(NSSplitViewItem.FromViewController(new NSViewController { View = detail }));
        _window.ContentViewController = _split;
        _window.WillClose += (_, _) => CloseSession();
        table.SelectRow(0, false);
        detail.Resized();
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
        foreach (var row in _rows) { row.RemoveFromSuperview(); row.Dispose(); }
        _rows.Clear();
        if (_categoryTable != null) { _categoryTable.DataSource = null; _categoryTable.Delegate = null; }
        _form = null; _scroll = null; _categoryTable = null; _group = null; _hotkeyList = null;
        _categorySource?.Dispose(); _categoryDelegate?.Dispose();
        _categorySource = null; _categoryDelegate = null;
        _window = null; _session = null; _split = null; _closing = false;
    }

    /// <summary>切换设置分类，保留同一工作副本并按 macOS 含义分组。</summary>
    private void BuildCategory()
    {
        if (_form == null) return;
        StopRecording();
        foreach (var row in _rows) { row.RemoveFromSuperview(); row.Dispose(); }
        _rows.Clear(); _finishEditor = null; _group = null; _hotkeyList = null;
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
        double width = (double)_scroll.ContentView.Bounds.Width;
        double contentWidth = Math.Min(680, Math.Max(1, width - 48));
        double top = 24;
        foreach (var row in _rows)
        {
            if (row is SettingGroup group) group.LayoutRows(contentWidth);
            row.Frame = new CGRect((width - contentWidth) / 2, top, contentWidth, row.Frame.Height);
            top += (double)row.Frame.Height + (row is SettingGroup ? 24 : 8);
        }
        _form.Frame = new CGRect(0, 0, width, Math.Max(top, (double)_scroll.ContentView.Bounds.Height));
    }

    /// <summary>把一行添加到原生表单。</summary>
    private void Add(NSView row) => (_group ?? throw new InvalidOperationException("设置行必须归属分组")).AddRow(row);
    /// <summary>创建系统表单组标题。</summary>
    private void Group(string title)
    {
        var label = Text(title, 13, true); label.Frame = new CGRect(0, 0, 500, 22);
        _rows.Add(label); _form!.AddSubview(label);
        _group = new SettingGroup(); _rows.Add(_group); _form.AddSubview(_group);
    }
    /// <summary>创建系统静态标签，字号与颜色使用 AppKit 默认。</summary>
    private static NSTextField Text(string text, double size = 13, bool emphasis = false)
        => new() { StringValue = text, Editable = false, Selectable = false, Bordered = false, DrawsBackground = false,
            Font = (emphasis ? NSFont.BoldSystemFontOfSize((nfloat)size) : NSFont.SystemFontOfSize((nfloat)size)) ?? throw new InvalidOperationException("系统字体不可用"), TextColor = NSColor.Label };
    /// <summary>一行左右标签/原生控件的布局。</summary>
    private NSView Row(string title, NSView control, double controlWidth = 180, double height = 44)
    {
        var row = new SettingRow(new CGRect(0, 0, 500, height));
        var label = Text(title); row.AddSubview(label); row.AddSubview(control);
        row.Resized = () => { label.Frame = new CGRect(0, (height - 20) / 2, Math.Max(100, row.Bounds.Width - controlWidth - 16), 20); control.Frame = new CGRect(row.Bounds.Width - controlWidth, (height - 26) / 2, controlWidth, 26); };
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
        var field = new NSTextField { StringValue = getter().ToString(integer ? "0" : "0.##", System.Globalization.CultureInfo.InvariantCulture), Alignment = NSTextAlignment.Right, BezelStyle = NSTextFieldBezelStyle.Rounded };
        /// <summary>校验完成的数值输入，将有效值提交工作副本并恢复规范文本。</summary>
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
            /// <summary>完成缩放比例输入后校验范围，更新预设并恢复规范文本。</summary>
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
        Group("控制栏功能与快捷键");
        _hotkeyList = MacReorderList.Create(Settings.Hotkeys, item => item.Name, item => item.IsDisplay,
            (item, value) => item.IsDisplay = value, Settings.MoveHotkey, HotkeyEditor);
        Add(_hotkeyList);
        Group("苹果键盘映射"); Toggle("使用苹果键盘", () => Settings.UseAppleKeyboard, value => { Settings.UseAppleKeyboard = value; _hotkeyList?.RefreshRows(); });
        string[] targets = ["不映射", "Ctrl", "Alt", "Win"];
        Choice("⌘ Command", targets, (int)Settings.MapCommandTarget, index => { Settings.MapCommandTarget = (PhotoViewer.Core.Input.AppleMappingTarget)index; _hotkeyList?.RefreshRows(); });
        Choice("⌥ Option", targets, (int)Settings.MapOptionTarget, index => { Settings.MapOptionTarget = (PhotoViewer.Core.Input.AppleMappingTarget)index; _hotkeyList?.RefreshRows(); });
        Choice("⌃ Control", targets, (int)Settings.MapControlTarget, index => { Settings.MapControlTarget = (PhotoViewer.Core.Input.AppleMappingTarget)index; _hotkeyList?.RefreshRows(); });
    }
    /// <summary>在可排序功能行内创建主次手势录制按钮，冲突用系统橙色标明。</summary>
    private NSView HotkeyEditor(SettingsViewModel.HotkeyItem hotkey, bool primary)
    {
        string title = MacInput.FormatGesture(primary ? hotkey.PrimaryHotkey : hotkey.SecondaryHotkey, Settings);
        bool conflict = primary ? hotkey.HasPrimaryConflict : hotkey.HasSecondaryConflict;
        var button = NSButton.CreateButton(string.IsNullOrWhiteSpace(title) ? "未设置" : title, () => { });
        button.BezelStyle = NSBezelStyle.Rounded;
        button.ControlSize = NSControlSize.Small;
        button.ContentTintColor = conflict ? NSColor.SystemOrange : NSColor.Label;
        button.ToolTip = conflict ? "快捷键冲突" : "点击录制；删除键清空，Escape 取消";
        button.Activated += (_, _) => BeginRecording(button, hotkey, primary);
        var cell = new NSTableCellView(new CGRect(0, 0, 116, 34));
        button.Frame = new CGRect(4, 5, 108, 24);
        cell.AddSubview(button);
        return cell;
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
        StopRecording();
        string previousTitle = button.Title;
        _cancelRecording = () => button.Title = previousTitle;
        button.Title = "输入快捷键…";
        _recordMonitor = NSEvent.AddLocalMonitorForEventsMatchingMask(NSEventMask.KeyDown | NSEventMask.LeftMouseDown | NSEventMask.RightMouseDown | NSEventMask.OtherMouseDown | NSEventMask.ScrollWheel, nativeEvent =>
        {
            if (nativeEvent.Window != _window) return nativeEvent;
            if (nativeEvent.Type == NSEventType.KeyDown && nativeEvent.KeyCode == 53) { StopRecording(); return null!; }
            bool clear = nativeEvent.Type == NSEventType.KeyDown && nativeEvent.KeyCode is 51 or 117;
            var gesture = clear ? null : MacInput.FromEvent(nativeEvent, Settings);
            if (gesture == null && !clear) return nativeEvent;
            if (primary) hotkey.PrimaryHotkey = gesture; else hotkey.SecondaryHotkey = gesture;
            Settings.CheckHotkeyConflicts(); StopRecording(); _hotkeyList?.RefreshRows(); return null!;
        });
    }
    /// <summary>结束手势录制并移除系统监视器。</summary>
    private void StopRecording()
    {
        if (_recordMonitor != null) { NSEvent.RemoveMonitor(_recordMonitor); _recordMonitor.Dispose(); _recordMonitor = null; }
        _cancelRecording?.Invoke(); _cancelRecording = null;
    }

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
            string[] symbols = ["folder", "photo", "keyboard", "info.circle", "sparkles"];
            var label = Text(categories[(int)row]);
            label.Frame = new CGRect(36, 6, 130, 20);
            label.AutoresizingMask = NSViewResizingMask.WidthSizable;
            var icon = new NSImageView(new CGRect(8, 7, 18, 18))
            {
                Image = MacSymbols.Get(symbols[(int)row]), ContentTintColor = NSColor.ControlAccent,
            };
            var cell = new NSTableCellView(new CGRect(0, 0, 180, 32)) { TextField = label, ImageView = icon };
            cell.AddSubview(icon); cell.AddSubview(label); return cell;
        }
    }
    /// <summary>表单采用向下的布局坐标。</summary>
    private sealed class FormView(CGRect frame) : NSView(frame)
    {
        public override bool IsFlipped => true;
        /// <summary>在当前深色外观中解析语义底色，避免 CGColor 提前解析产生白边。</summary>
        public override void DrawRect(CGRect dirtyRect) { NSColor.WindowBackground.SetFill(); NSBezierPath.FillRect(Bounds); }
    }
    /// <summary>自适应原生设置根视图。</summary>
    private sealed class SettingsRoot(CGRect frame) : NSView(frame)
    {
        public Action? Resized { get; set; }
        public override bool IsFlipped => true;
        /// <summary>整块详情区域绘制同一语义底色，填满滚动容器的四周。</summary>
        public override void DrawRect(CGRect dirtyRect) { NSColor.WindowBackground.SetFill(); NSBezierPath.FillRect(Bounds); }
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

    /// <summary>同类设置的圆角分组，动态语义底色和轻量分隔线跟随窗口外观。</summary>
    private sealed class SettingGroup : NSView
    {
        private readonly List<NSView> _children = [];
        public override bool IsFlipped => true;

        /// <summary>把完整设置行加入当前分组，保留各行的原生控件。</summary>
        internal void AddRow(NSView row) { _children.Add(row); AddSubview(row); }

        /// <summary>根据表单宽度排列行，表格保留自身滚动区且不增加外框。</summary>
        internal void LayoutRows(double width)
        {
            double top = 6;
            foreach (var row in _children)
            {
                row.Frame = new CGRect(14, top, Math.Max(1, width - 28), row.Frame.Height);
                top += (double)row.Frame.Height;
            }
            Frame = new CGRect(Frame.X, Frame.Y, width, top + 6);
            NeedsDisplay = true;
        }

        /// <summary>仅绘制分组底色和行间分隔线，不烘焙浅色背景或边框。</summary>
        public override void DrawRect(CGRect dirtyRect)
        {
            using var fill = NSColor.WindowBackground.BlendedColor(.07f, NSColor.Label) ?? throw new InvalidOperationException("设置分组颜色不可用");
            fill.SetFill();
            using var background = NSBezierPath.FromRoundedRect(Bounds, 10, 10);
            background.Fill();
            NSColor.Separator.SetStroke();
            using var separator = new NSBezierPath { LineWidth = .5f };
            foreach (var row in _children.Skip(1))
            {
                separator.MoveTo(new CGPoint(14, row.Frame.Y));
                separator.LineTo(new CGPoint(Bounds.Width - 14, row.Frame.Y));
            }
            separator.Stroke();
        }

        /// <summary>分类更换时释放表格的托管数据源及其它子控件。</summary>
        protected override void Dispose(bool disposing)
        {
            if (disposing) { foreach (var row in _children) { row.RemoveFromSuperview(); row.Dispose(); } _children.Clear(); }
            base.Dispose(disposing);
        }
    }
}
