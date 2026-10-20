using System.ComponentModel;
using AppKit;
using CoreGraphics;
using Foundation;
using PhotoViewer.Core.Platform;
using PhotoViewer.ViewModels.Main;
using PhotoViewer.ViewModels.Tools;

namespace PhotoViewer.Mac.Native;

internal sealed class MacToolsWindow : IDisposable
{
    private readonly MainViewModel _main;
    private readonly bool _automatedValidation;
    private readonly NSSplitViewController _split = new();
    private readonly NSTableView _sidebar = new();
    private readonly DetailPane _detail = new();
    private readonly SidebarSource _source;
    private readonly SidebarDelegate _sidebarDelegate;
    private NSWindow? _window;
    private ToolsWindowDelegate? _windowDelegate;
    private MacExifDetailView? _exifPage;
    private MacDinoDiagnosticView? _dinoPage;
    private bool _open;
    private bool _rendering;
    private bool _disposed;
    private bool? _exifAvailable;

    public NSWindow Window => _window ?? throw new InvalidOperationException("工具窗口尚未创建");
    public bool IsOpen => _open;
    internal NSTableView Sidebar => _sidebar;

    public MacToolsWindow(MainViewModel main, bool automatedValidation = false)
    {
        _main = main;
        _automatedValidation = automatedValidation;
        _source = new SidebarSource();
        _sidebarDelegate = new SidebarDelegate(this);
        _sidebar.AddColumn(new NSTableColumn("tool") { Width = 180 });
        _sidebar.ColumnAutoresizingStyle = NSTableViewColumnAutoresizingStyle.LastColumnOnly;
        _sidebar.AutoresizingMask = NSViewResizingMask.WidthSizable;
        _sidebar.HeaderView = null;
        _sidebar.RowHeight = 34;
        _sidebar.Style = NSTableViewStyle.Inset;
        _sidebar.AllowsEmptySelection = false;
        _sidebar.AllowsMultipleSelection = false;
        _sidebar.DataSource = _source;
        _sidebar.Delegate = _sidebarDelegate;
        ((NSView)_sidebar).AccessibilityLabel = "工具分类";
        var scroll = new NSScrollView { DocumentView = _sidebar, DrawsBackground = false, BorderType = NSBorderType.NoBorder };
        var sidebarController = new NSViewController { View = scroll };
        var sidebarItem = NSSplitViewItem.CreateSidebar(sidebarController);
        sidebarItem.MinimumThickness = 180;
        sidebarItem.MaximumThickness = 220;
        sidebarItem.CanCollapse = false;
        _split.AddSplitViewItem(sidebarItem);
        _split.AddSplitViewItem(NSSplitViewItem.FromViewController(new NSViewController { View = _detail }));
        _split.View.Frame = new CGRect(0, 0, 880, 720);
        _main.Tools.PropertyChanged += OnToolsChanged;
    }

    public void ShowOrActivate()
    {
        if (_disposed) return;
        if (_window == null) BuildWindow();
        bool opening = !_open;
        _open = true;
        _main.Tools.IsPresented = true;
        if (opening) SelectDefault();
        Render();
        _window!.MakeKeyAndOrderFront(null);
        if (_automatedValidation) return;
        if (OperatingSystem.IsMacOSVersionAtLeast(14)) NSApplication.SharedApplication.Activate();
        else NSApplication.SharedApplication.ActivateIgnoringOtherApps(true);
    }

    private void SelectDefault()
    {
        if (_main.Tools.IsExifDetailEnabled) SelectTool(0);
        else SelectTool(1);
    }

    private void SelectTool(int row)
    {
        if (row == 0 && _main.Tools.IsExifDetailEnabled)
        {
            if (_main.Tools.CurrentTool is not ExifDetailViewModel)
            {
                _main.Tools.Deactivate();
                _main.Tools.OpenExifDetail();
            }
        }
        else if (row == 1 && _main.Tools.CurrentTool is not DinoDebugViewModel)
            _main.Tools.OpenDinoDebug();
        Render();
    }

    public void SyncCurrentFile() => Render();

    private void OnToolsChanged(object? sender, PropertyChangedEventArgs args) => UiDispatcher.Post(Render);

    private void Render()
    {
        if (_disposed || !_open || _rendering) return;
        _rendering = true;
        try
        {
            if (_main.Tools.CurrentTool == null || (_main.Tools.CurrentTool is ExifDetailViewModel && !_main.Tools.IsExifDetailEnabled))
            {
                if (_main.Tools.IsExifDetailEnabled) _main.Tools.OpenExifDetail();
                else _main.Tools.OpenDinoDebug();
            }
            bool exif = _main.Tools.CurrentTool is ExifDetailViewModel;
            if (_exifAvailable != _main.Tools.IsExifDetailEnabled)
            {
                _exifAvailable = _main.Tools.IsExifDetailEnabled;
                _sidebar.ReloadData();
            }
            if (_sidebar.SelectedRow != (exif ? 0 : 1)) _sidebar.SelectRow(exif ? 0 : 1, false);
            if (exif)
            {
                _exifPage ??= AddPage(new MacExifDetailView());
                _exifPage.Show((ExifDetailViewModel)_main.Tools.CurrentTool!);
            }
            else
            {
                _dinoPage ??= AddPage(new MacDinoDiagnosticView(_main.Tools.DinoPresenter));
                _dinoPage.Sync();
            }
            if (_exifPage != null) _exifPage.Hidden = !exif;
            if (_dinoPage != null) _dinoPage.Hidden = exif;
            _detail.LayoutPages();
            _sidebar.NeedsDisplay = true;
        }
        finally { _rendering = false; }
    }

    private T AddPage<T>(T page) where T : NSView
    {
        _detail.AddSubview(page);
        return page;
    }

    private void BuildWindow()
    {
        _window = new NSWindow(new CGRect(0, 0, 880, 720),
            NSWindowStyle.Titled | NSWindowStyle.Closable | NSWindowStyle.Miniaturizable | NSWindowStyle.Resizable | NSWindowStyle.FullSizeContentView,
            NSBackingStore.Buffered, false)
        {
            Title = "工具", TitlebarAppearsTransparent = true,
            Appearance = NSAppearance.GetAppearance(NSAppearance.NameDarkAqua),
            ContentMinSize = new CGSize(560, 420),
            ContentViewController = _split,
            Toolbar = new NSToolbar("PhotoViewer.Tools"),
            ToolbarStyle = NSWindowToolbarStyle.Unified,
            BackgroundColor = NSColor.WindowBackground,
        };
        _window.ReleaseWhenClosed(false);
        _windowDelegate = new ToolsWindowDelegate(this);
        _window.Delegate = _windowDelegate;
        _window.Center();
    }

    public void CloseWindow()
    {
        if (_disposed || _window == null) return;
        _window.Close();
    }

    public void Dispose()
    {
        if (_disposed) return;
        CloseWindow();
        _disposed = true;
        _main.Tools.PropertyChanged -= OnToolsChanged;
        _exifPage?.Dispose();
        _dinoPage?.Dispose();
        if (_window != null) { _window.Delegate = null; _window.Dispose(); }
        _windowDelegate?.Dispose();
        _sidebar.Delegate = null;
        _sidebar.DataSource = null;
        _sidebarDelegate.Dispose();
        _source.Dispose();
        _split.Dispose();
    }

    private sealed class ToolsWindowDelegate(MacToolsWindow owner) : NSWindowDelegate
    {
        public override void WillClose(NSNotification notification)
        {
            owner._open = false;
            owner._main.Tools.IsPresented = false;
            owner._main.Tools.Deactivate();
            owner._main.Tools.ShowList();
        }
    }

    private sealed class SidebarSource : NSTableViewDataSource
    {
        public override nint GetRowCount(NSTableView tableView) => 2;
    }

    private sealed class SidebarDelegate(MacToolsWindow owner) : NSTableViewDelegate
    {
        public override bool ShouldSelectRow(NSTableView tableView, nint row) => row == 1 || owner._main.Tools.IsExifDetailEnabled;
        public override void SelectionDidChange(NSNotification notification)
        {
            if (!owner._rendering && owner._open) owner.SelectTool((int)owner._sidebar.SelectedRow);
        }
        public override NSView GetViewForItem(NSTableView tableView, NSTableColumn? column, nint row)
        {
            var cell = new NSTableCellView(new CGRect(0, 0, 200, 34));
            var icon = new SidebarIcon(new CGRect(4, 6, 22, 22))
            {
                Image = MacSymbols.Get(row == 0 ? "info.circle.fill" : "sparkles"),
                ContentTintColor = NSColor.White,
                WantsLayer = true,
            };
            icon.Layer!.BackgroundColor = (row == 0 ? NSColor.SystemOrange : NSColor.SystemPink).CGColor;
            icon.Layer.CornerRadius = 6;
            var text = new NSTextField(new CGRect(34, 8, 155, 20))
            {
                StringValue = row == 0 ? "EXIF 详情" : "DINO 诊断",
                Editable = false, Selectable = false, Bordered = false, DrawsBackground = false,
                Font = NSFont.SystemFontOfSize(13) ?? throw new InvalidOperationException("系统字体不可用"),
                TextColor = row == 0 && !owner._main.Tools.IsExifDetailEnabled ? NSColor.DisabledControlText : NSColor.Label,
            };
            cell.ImageView = icon;
            cell.TextField = text;
            cell.AddSubview(icon); cell.AddSubview(text);
            return cell;
        }
    }

    private sealed class SidebarIcon(CGRect frame) : NSImageView(frame)
    {
        public override bool AllowsVibrancy => false;
    }

    private sealed class DetailPane : NSView
    {
        public override bool IsFlipped => true;
        internal void LayoutPages()
        {
            var insets = SafeAreaInsets;
            var frame = new CGRect(insets.Left, insets.Top, Math.Max(1, (double)(Bounds.Width - insets.Left - insets.Right)), Math.Max(1, (double)(Bounds.Height - insets.Top - insets.Bottom)));
            foreach (var page in Subviews) page.Frame = frame;
        }
        public override void Layout() { base.Layout(); LayoutPages(); }
        public override void SetFrameSize(CGSize size) { base.SetFrameSize(size); LayoutPages(); }
    }
}
