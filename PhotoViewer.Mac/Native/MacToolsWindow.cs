using System.ComponentModel;
using AppKit;
using CoreGraphics;
using Foundation;
using PhotoViewer.Core.Platform;
using PhotoViewer.ViewModels.Main;
using PhotoViewer.ViewModels.Tools;

namespace PhotoViewer.Mac.Native;

/// <summary>
/// macOS 原生工具窗口：非模态单例，首次 <see cref="ShowOrActivate"/> 时才创建 AppKit 窗口，用户关闭后对象保留可重开。
/// 外壳两态与旧壳 <c>ToolsView</c> 同构 —— 工具列表首页（返回按钮 + 工具入口）与子页面（EXIF 详情 / DINO 诊断）。
/// 导航状态完全复用共享 <see cref="ToolsViewModel"/>，本类只做原生渲染与状态同步，不改变共享状态、不触发推理。
/// </summary>
internal sealed class MacToolsWindow : IDisposable
{
    /// <summary>初始内容宽度（与旧壳工具窗口一致）。</summary>
    private const double WindowWidth = 880;

    /// <summary>初始内容高度（与旧壳工具窗口一致）。</summary>
    private const double WindowHeight = 720;

    /// <summary>最小内容宽度。</summary>
    private const double WindowMinWidth = 560;

    /// <summary>最小内容高度。</summary>
    private const double WindowMinHeight = 420;

    /// <summary>返回按钮与工具入口按钮高度。</summary>
    private const double ButtonHeight = 32;

    /// <summary>返回按钮四周外边距（旧壳 Margin 8）。</summary>
    private const double BackMargin = 8;

    /// <summary>工具列表外边距（旧壳 Margin 16）。</summary>
    private const double ListMargin = 16;

    /// <summary>工具列表按钮间距（旧壳 Spacing 8）。</summary>
    private const double ListSpacing = 8;

    private readonly MainViewModel _main;
    private readonly bool _automatedValidation;
    private readonly ShellView _shell = new(new CGRect(0, 0, WindowWidth, WindowHeight));
    private readonly NSView _listPane = new FlippedPane();
    private readonly NSView _pagePane = new FlippedPane();
    private readonly NSButton _backButton;
    private readonly NSButton _exifButton;
    private readonly NSButton _dinoButton;
    private NSWindow? _window;
    private ToolsWindowDelegate? _windowDelegate;
    private MacExifDetailView? _exifPage;
    private MacDinoDiagnosticView? _dinoPage;
    private NSView? _visiblePage;
    private bool _open;
    private bool _disposed;

    /// <summary>真实窗口对象；未打开前访问会显式报错，避免验收拿到空引用。</summary>
    public NSWindow Window => _window ?? throw new InvalidOperationException("工具窗口尚未创建");

    /// <summary>窗口当前是否打开；用户关闭后回到 false，可再次 <see cref="ShowOrActivate"/>。</summary>
    public bool IsOpen => _open;

    /// <summary>创建两态外壳与全部原生控件；不创建窗口，也不触发任何推理。</summary>
    /// <param name="main">共享主 VM。</param>
    /// <param name="automatedValidation">自动化验收模式：不激活整个 app，避免抢走宿主焦点。</param>
    public MacToolsWindow(MainViewModel main, bool automatedValidation = false)
    {
        _main = main;
        _automatedValidation = automatedValidation;
        _shell.Resized = LayoutShell;
        _shell.WantsLayer = true;
        _shell.Layer!.BackgroundColor = NSColor.FromWhite(.07f, 1).CGColor;
        _backButton = PushButton("返回", () =>
        {
            _main.Tools.ShowList();
            Render();
        });
        _exifButton = PushButton("EXIF 详情", () =>
        {
            _main.Tools.OpenExifDetail();
            Render();
        });
        _dinoButton = PushButton("DINO 诊断", () =>
        {
            _main.Tools.OpenDinoDebug();
            Render();
        });
        // 「照片数据统计」仅 Windows 可用（IsPhotoStatsAvailable == false），macOS 不建立该入口。
        _listPane.AddSubview(_exifButton);
        _listPane.AddSubview(_dinoButton);
        _shell.AddSubview(_backButton);
        _shell.AddSubview(_listPane);
        _shell.AddSubview(_pagePane);
        _main.Tools.PropertyChanged += OnToolsChanged;
        Render();
    }

    /// <summary>未打开则创建窗口并前置；已打开则仅前置。</summary>
    public void ShowOrActivate()
    {
        if (_disposed) return;
        if (_window == null) BuildWindow();
        _open = true;
        Render();
        _window!.MakeKeyAndOrderFront(null);
        if (_automatedValidation) return;
        if (OperatingSystem.IsMacOSVersionAtLeast(14)) NSApplication.SharedApplication.Activate();
        else NSApplication.SharedApplication.ActivateIgnoringOtherApps(true);
    }

    /// <summary>当前照片或其 EXIF 变化后重新渲染当前页面；不改变导航状态，也不抢焦点。</summary>
    public void SyncCurrentFile()
    {
        if (_disposed) return;
        switch (_main.Tools.CurrentTool)
        {
            case ExifDetailViewModel exif:
                _exifPage?.Show(exif);
                break;
            case DinoDebugViewModel:
                _dinoPage?.Sync();
                break;
        }
    }

    /// <summary>关闭窗口；对象与子页面保留，可再次打开。</summary>
    public void CloseWindow()
    {
        if (_disposed || _window == null) return;
        _open = false;
        _window.Close();
    }

    /// <summary>解除全部订阅并释放视图与窗口。</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _open = false;
        _main.Tools.PropertyChanged -= OnToolsChanged;
        _exifPage?.Dispose();
        _exifPage = null;
        _dinoPage?.Dispose();
        _dinoPage = null;
        _visiblePage = null;
        if (_window != null)
        {
            _window.Delegate = null;
            _window.Close();
            _window.Dispose();
            _window = null;
        }
        _windowDelegate?.Dispose();
        _windowDelegate = null;
        _shell.Dispose();
    }

    /// <summary>创建真实窗口：深色外观、可关闭/最小化/缩放，初始 880×720、最小 560×420，并居中于主窗口。</summary>
    private void BuildWindow()
    {
        _window = new NSWindow(new CGRect(0, 0, WindowWidth, WindowHeight),
            NSWindowStyle.Titled | NSWindowStyle.Closable | NSWindowStyle.Miniaturizable | NSWindowStyle.Resizable,
            NSBackingStore.Buffered, false)
        {
            Title = "工具",
            Appearance = NSAppearance.GetAppearance(NSAppearance.NameDarkAqua),
            ContentMinSize = new CGSize(WindowMinWidth, WindowMinHeight),
            BackgroundColor = NSColor.FromWhite(.07f, 1),
        };
        _window.ReleaseWhenClosed(false);
        _windowDelegate = new ToolsWindowDelegate(this);
        _window.Delegate = _windowDelegate;
        _window.ContentView = _shell;
        CenterWindow();
    }

    /// <summary>把窗口居中到当前主窗口；取不到主窗口时退回屏幕居中。</summary>
    private void CenterWindow()
    {
        var parent = NSApplication.SharedApplication.MainWindow;
        if (parent == null || ReferenceEquals(parent, _window) || !parent.IsVisible)
        {
            _window!.Center();
            return;
        }
        var host = parent.Frame;
        var size = _window!.Frame.Size;
        _window.SetFrameOrigin(new CGPoint(host.X + (host.Width - size.Width) / 2, host.Y + (host.Height - size.Height) / 2));
    }

    /// <summary>共享工具导航状态变化后重排界面；跨线程通知统一回到主线程。</summary>
    private void OnToolsChanged(object? sender, PropertyChangedEventArgs args) => UiDispatcher.Post(Render);

    /// <summary>按共享导航状态渲染：入口可用性、当前子页面与两态外壳。</summary>
    private void Render()
    {
        if (_disposed) return;
        _exifButton.Enabled = _main.Tools.IsExifDetailEnabled;
        switch (_main.Tools.CurrentTool)
        {
            case ExifDetailViewModel exif:
                _exifPage ??= AddPage(new MacExifDetailView());
                _exifPage.Show(exif);
                _visiblePage = _exifPage;
                break;
            case DinoDebugViewModel:
                _dinoPage ??= AddPage(new MacDinoDiagnosticView(_main.Tools.DinoPresenter));
                _dinoPage.Sync();
                _visiblePage = _dinoPage;
                break;
            default:
                _visiblePage = null;
                break;
        }
        LayoutShell();
    }

    /// <summary>把子页面挂到页面容器并返回；实例由工具窗口持有到释放为止。</summary>
    private T AddPage<T>(T page) where T : NSView
    {
        _pagePane.AddSubview(page);
        return page;
    }

    /// <summary>排列返回按钮、列表页与页面容器：返回按钮仅可返回时可见，列表与子页面互斥。</summary>
    private void LayoutShell()
    {
        if (_disposed) return;
        double width = Math.Max(1, (double)_shell.Bounds.Width);
        double height = Math.Max(1, (double)_shell.Bounds.Height);
        bool canGoBack = _main.Tools.CanGoBack;
        _backButton.Hidden = !canGoBack;
        if (canGoBack)
            _backButton.Frame = new CGRect(BackMargin, BackMargin, Math.Max(1, width - BackMargin * 2), ButtonHeight);
        double bodyTop = canGoBack ? BackMargin * 2 + ButtonHeight : 0;
        var body = new CGRect(0, bodyTop, width, Math.Max(1, height - bodyTop));
        _listPane.Hidden = canGoBack;
        _pagePane.Hidden = !canGoBack;
        _listPane.Frame = body;
        _pagePane.Frame = body;
        double listWidth = Math.Max(1, (double)_listPane.Bounds.Width - ListMargin * 2);
        _exifButton.Frame = new CGRect(ListMargin, ListMargin, listWidth, ButtonHeight);
        _dinoButton.Frame = new CGRect(ListMargin, ListMargin + ButtonHeight + ListSpacing, listWidth, ButtonHeight);
        if (_exifPage != null) SyncPage(_exifPage, canGoBack);
        if (_dinoPage != null) SyncPage(_dinoPage, canGoBack);
    }

    /// <summary>把子页面铺满页面容器；只有当前页面在子页面态可见。</summary>
    /// <param name="page">目标子页面。</param>
    /// <param name="canGoBack">共享导航是否处于子页面态。</param>
    private void SyncPage(NSView page, bool canGoBack)
    {
        page.Hidden = !canGoBack || !ReferenceEquals(page, _visiblePage);
        page.Frame = new CGRect(0, 0, _pagePane.Bounds.Width, _pagePane.Bounds.Height);
    }

    /// <summary>创建原生圆角文字按钮，点击走共享工具导航命令。</summary>
    /// <param name="title">按钮标题。</param>
    /// <param name="action">点击回调。</param>
    /// <returns>已接线的原生按钮。</returns>
    private static NSButton PushButton(string title, Action action)
    {
        var button = new NSButton(new CGRect(0, 0, 0, ButtonHeight))
        {
            Title = title,
            BezelStyle = NSBezelStyle.Rounded,
            Font = NSFont.SystemFontOfSize(13) ?? throw new InvalidOperationException("系统字体不可用"),
        };
        button.Activated += (_, _) => action();
        return button;
    }

    /// <summary>窗口关闭通知：只更新打开状态，窗口对象保留以便重开。</summary>
    private sealed class ToolsWindowDelegate(MacToolsWindow owner) : NSWindowDelegate
    {
        /// <summary>用户关闭窗口（红点或 Cmd+W）后标记为已关闭。</summary>
        public override void WillClose(NSNotification notification) => owner._open = false;
    }

    /// <summary>顶边为原点的窗口内容容器，尺寸变化驱动外壳布局。</summary>
    private sealed class ShellView(CGRect frame) : NSView(frame)
    {
        /// <summary>尺寸变化回调。</summary>
        public Action? Resized { get; set; }

        /// <summary>布局坐标自顶向下。</summary>
        public override bool IsFlipped => true;

        /// <summary>窗口尺寸变化后重排子视图。</summary>
        public override void SetFrameSize(CGSize newSize)
        {
            base.SetFrameSize(newSize);
            Resized?.Invoke();
        }
    }

    /// <summary>顶边为原点的内容分区。</summary>
    private sealed class FlippedPane : NSView
    {
        /// <summary>布局坐标自顶向下。</summary>
        public override bool IsFlipped => true;
    }
}
