using System.ComponentModel;
using AppKit;
using CoreGraphics;
using Foundation;
using PhotoViewer.Core.Platform;
using PhotoViewer.ViewModels.Main;
using PhotoViewer.ViewModels.Tools;

namespace PhotoViewer.Mac.Native;

/// <summary>
/// macOS 工具窗口：非模态懒创建单例，外壳由 SwiftUI（NavigationSplitView + 系统侧栏）
/// 绘制，与设置窗口共用同一套窗口外观；详情栏承载 C# 拥有的 EXIF / DINO 页面视图。
/// 只有 EXIF 详情 / DINO 诊断两类，没有工具首页或返回按钮；首次或关闭后重开默认 EXIF，
/// 无可用 EXIF 落 DINO，已打开只前置保留当前页。导航与计算复用共享 <see cref="ToolsViewModel"/>。
/// </summary>
internal sealed class MacToolsWindow : IDisposable
{
    private readonly MainViewModel _main;
    private readonly bool _automatedValidation;
    private readonly DetailPane _detail = new();
    private NSWindow? _window;
    private ToolsWindowDelegate? _windowDelegate;
    private MacExifDetailView? _exifPage;
    private MacDinoDiagnosticView? _dinoPage;
    private bool _open;
    private bool _rendering;
    private bool _disposed;
    private int _selected;
    private int _pushedSelected = -1;
    private bool? _pushedExif;

    public NSWindow Window => _window ?? throw new InvalidOperationException("工具窗口尚未创建");
    public bool IsOpen => _open;

    /// <summary>当前选中的工具分类（0 = EXIF 详情，1 = DINO 诊断）。</summary>
    internal int SelectedCategory => _selected;

    public MacToolsWindow(MainViewModel main, bool automatedValidation = false)
    {
        _main = main;
        _automatedValidation = automatedValidation;
        _main.Tools.PropertyChanged += OnToolsChanged;
    }

    /// <summary>展示并前置工具窗口；已打开时只前置并保留当前页。</summary>
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

    /// <summary>首次打开或关闭后重开时的默认页：EXIF 可用则 EXIF，否则 DINO。</summary>
    private void SelectDefault() => SelectTool(_main.Tools.IsExifDetailEnabled ? 0 : 1);

    /// <summary>切换工具分类并刷新页面；EXIF 不可用时忽略该分类。</summary>
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
        else if (row == 1 && _main.Tools.CurrentTool is not DinoDebugViewModel)
            _main.Tools.OpenDinoDebug();
        _selected = row;
        Render();
    }

    /// <summary>系统菜单直达入口，与侧栏共用分类切换和 EXIF 可用性守卫。</summary>
    internal void SelectCategory(int row) => SelectTool(row);

    /// <summary>按 SwiftUI 侧栏的选择结果切换分类（与用户点击同一条绑定路径）。</summary>
    private void HandleSidebarSelection(int row)
    {
        if (_disposed || !_open) return;
        SelectTool(row);
    }

    /// <summary>验收用选择入口：经 SwiftUI 选中绑定回传，与真实点击一致。</summary>
    internal void SelectCategoryForTesting(int row) => MacToolsNative.Select(row);

    public void SyncCurrentFile() => Render();

    private void OnToolsChanged(object? sender, PropertyChangedEventArgs args) => UiDispatcher.Post(Render);

    /// <summary>同步共享工具状态到页面视图与 SwiftUI 侧栏。</summary>
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
            _selected = exif ? 0 : 1;
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
            PushState(_main.Tools.IsExifDetailEnabled);
        }
        finally { _rendering = false; }
    }

    /// <summary>状态确有变化时把选中分类与 EXIF 可用性推给 SwiftUI 侧栏。</summary>
    private void PushState(bool exifEnabled)
    {
        if (_pushedSelected == _selected && _pushedExif == exifEnabled) return;
        _pushedSelected = _selected;
        _pushedExif = exifEnabled;
        MacToolsNative.UpdateState($"{{\"selected\":{_selected},\"exifEnabled\":{(exifEnabled ? "true" : "false")}}}");
    }

    private T AddPage<T>(T page) where T : NSView
    {
        _detail.AddSubview(page);
        return page;
    }

    /// <summary>经 SwiftUI 创建工具窗口并接管 AppKit 窗口代理。</summary>
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
        _dinoPage?.Dispose();
        if (_window != null) { _window.Delegate = null; _window.Dispose(); }
        _windowDelegate?.Dispose();
    }

    /// <summary>关闭即退出工具态并取消后台任务，下次打开重新落默认页。</summary>
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

    /// <summary>详情页容器：页面铺满 SwiftUI 详情栏，扣除系统安全区后落位。</summary>
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
