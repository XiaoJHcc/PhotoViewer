using System.ComponentModel;
using AppKit;
using CoreGraphics;
using Foundation;
using PhotoViewer.Core.Platform;
using PhotoViewer.ViewModels.Main;
using PhotoViewer.ViewModels.Tools;

namespace PhotoViewer.Mac.Native;

/// <summary>macOS 原生工具窗口：EXIF 与当前版本的诊断页共用非模态单例。</summary>
internal sealed class MacToolsWindow : IDisposable
{
    private readonly MainViewModel _main;
    private readonly bool _automatedValidation;
    private readonly DetailPane _detail = new();
    private NSWindow? _window;
    private ToolsWindowDelegate? _windowDelegate;
    private MacExifDetailView? _exifPage;
    private MacDiagnosticView? _diagnosticPage;
    private bool _open;
    private bool _rendering;
    private bool _disposed;
    private int _selected;
    private int _pushedSelected = -1;
    private bool? _pushedExif;

    public NSWindow Window => _window ?? throw new InvalidOperationException("工具窗口尚未创建");
    public bool IsOpen => _open;
    internal int SelectedCategory => _selected;

    public MacToolsWindow(MainViewModel main, bool automatedValidation = false)
    {
        _main = main;
        _automatedValidation = automatedValidation;
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

    private void SelectDefault() => SelectTool(_main.Tools.IsExifDetailEnabled ? 0 : 1);

    private void SelectTool(int row)
    {
        if (row == 0 && !_main.Tools.IsExifDetailEnabled) return;
        if (row == 0)
        {
            if (_main.Tools.CurrentTool is not ExifDetailViewModel)
            {
                _main.Tools.Deactivate();
                _main.Tools.OpenExifDetail();
            }
        }
        else if (row == 1 && !ReferenceEquals(_main.Tools.CurrentTool, _main.Tools.DiagnosticDebug))
            _main.Tools.OpenDiagnostic();
        _selected = row;
        Render();
    }

    internal void SelectCategory(int row) => SelectTool(row);

    private void HandleSidebarSelection(int row)
    {
        if (_disposed || !_open) return;
        SelectTool(row);
    }

    internal void SelectCategoryForTesting(int row) => MacToolsNative.Select(row);
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
                else _main.Tools.OpenDiagnostic();
            }
            bool exif = _main.Tools.CurrentTool is ExifDetailViewModel;
            _selected = exif ? 0 : 1;
            if (exif)
            {
                _exifPage ??= AddPage(new MacExifDetailView());
                _exifPage.Show((ExifDetailViewModel)_main.Tools.CurrentTool!);
            }
            else
            {
#if PHOTOVIEWER_DINO
                _diagnosticPage ??= AddPage(new MacDiagnosticView(_main.Tools.DinoPresenter));
#else
                _diagnosticPage ??= AddPage(new MacDiagnosticView(_main.Tools.CvPresenter));
#endif
                _diagnosticPage.Sync();
            }
            if (_exifPage != null) _exifPage.Hidden = !exif;
            if (_diagnosticPage != null) _diagnosticPage.Hidden = exif;
            _detail.LayoutPages();
            PushState(_main.Tools.IsExifDetailEnabled);
        }
        finally { _rendering = false; }
    }

    private void PushState(bool exifEnabled)
    {
        if (_pushedSelected == _selected && _pushedExif == exifEnabled) return;
        _pushedSelected = _selected;
        _pushedExif = exifEnabled;
        MacToolsNative.UpdateState($"{{\"selected\":{_selected},\"exifEnabled\":{(exifEnabled ? "true" : "false")},\"diagnosticTitle\":\"{MacProductInfo.DiagnosticName}\",\"diagnosticSymbol\":\"{MacProductInfo.DiagnosticSymbol}\"}}");
    }

    private T AddPage<T>(T page) where T : NSView
    {
        _detail.AddSubview(page);
        return page;
    }

    private void BuildWindow()
    {
        nint pointer = MacToolsNative.Create(_detail.Handle);
        _window = ObjCRuntime.Runtime.GetNSObject<NSWindow>(pointer)
            ?? throw new InvalidOperationException("工具窗口创建失败");
        _window.Title = "工具";
        _window.ContentMinSize = new CGSize(560, 420);
        _window.SetContentSize(new CGSize(880, 720));
        _windowDelegate = new ToolsWindowDelegate(this);
        _window.Delegate = _windowDelegate;
        MacToolsNative.SelectSink = HandleSidebarSelection;
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
        MacToolsNative.SelectSink = null;
        _exifPage?.Dispose();
        _diagnosticPage?.Dispose();
        if (_window != null) { _window.Delegate = null; _window.Dispose(); }
        _windowDelegate?.Dispose();
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
