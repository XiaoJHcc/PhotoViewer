using System.ComponentModel;
using CoreGraphics;
using Foundation;
using PhotoViewer.Core.Platform;
using PhotoViewer.iOS.Core;
using PhotoViewer.ViewModels.Tools;
using UIKit;

namespace PhotoViewer.iOS.Native;

/// <summary>
/// iPad 原生工具页宿主：以 sheet（iPad 上为系统大页 sheet）承载一个
/// <see cref="UINavigationController"/>，根页为工具列表（EXIF 详情 / DINO 诊断；照片数据统计为 Windows
/// 专属，不建入口），子页为 EXIF 详情与 DINO 诊断。导航状态以共享 <see cref="ToolsViewModel"/> 为准：
/// 进入子页写共享状态，返回列表页恢复 <c>ShowList</c>；关闭后对象保留，可再次展示。
/// </summary>
internal sealed class iOSNativeToolsPresenter
{
    private ToolsViewModel? _tools;
    private UINavigationController? _navigation;
    private iOSNativeToolsListViewController? _list;
    private iOSExifDetailView? _exif;
    private iOSDinoDiagnosticView? _dino;

    /// <summary>当前进程里的工具页宿主（验收读取真实导航栈与页面）。</summary>
    internal static iOSNativeToolsPresenter? Current { get; private set; }

    /// <summary>建立宿主并登记为当前实例（不创建任何页面对象）。</summary>
    internal iOSNativeToolsPresenter() => Current = this;

    /// <summary>工具页当前是否已展示。</summary>
    internal bool IsPresented => _navigation?.PresentingViewController != null;

    /// <summary>当前导航控制器；未创建时为 null。</summary>
    internal UINavigationController? Navigation => _navigation;

    /// <summary>工具列表首页。</summary>
    internal iOSNativeToolsListViewController? ListPage => _list;

    /// <summary>EXIF 详情页；尚未创建时为 null。</summary>
    internal iOSExifDetailView? ExifPage => _exif;

    /// <summary>DINO 诊断页；尚未创建时为 null。</summary>
    internal iOSDinoDiagnosticView? DinoPage => _dino;

    /// <summary>打开或前置工具页；已在展示时只做内容同步。</summary>
    /// <param name="tools">共享工具壳 ViewModel。</param>
    internal void ShowOrActivate(ToolsViewModel tools)
    {
        _tools = tools;
        if (_navigation == null) Build(tools);
        else Subscribe(tools);
        if (IsPresented)
        {
            SyncCurrentPage();
            return;
        }
        var host = iOSViewControllerLocator.GetTopMostViewController();
        if (host == null)
        {
            throw new InvalidOperationException("工具页没有可展示的宿主控制器");
        }
        host.PresentViewController(_navigation!, true, null);
    }

    /// <summary>关闭工具页（sheet 下拉关闭同样走系统路径）；对象保留可重开。</summary>
    internal void Dismiss()
    {
        if (!IsPresented) return;
        _navigation!.DismissViewController(true, null);
    }

    /// <summary>创建导航控制器与列表首页，并订阅共享导航状态。</summary>
    /// <param name="tools">共享工具壳 ViewModel。</param>
    private void Build(ToolsViewModel tools)
    {
        _list = new iOSNativeToolsListViewController(tools, this);
        _navigation = new UINavigationController(_list)
        {
            ModalPresentationStyle = UIModalPresentationStyle.PageSheet,
            OverrideUserInterfaceStyle = UIUserInterfaceStyle.Dark,
        };
        // iOS 26 的导航栏默认取系统材质（浅色），必须显式给深色不透明外观才能与工具页深色表单一致。
        var appearance = new UINavigationBarAppearance();
        appearance.ConfigureWithOpaqueBackground();
        appearance.BackgroundColor = UIColor.FromRGB(0x12, 0x12, 0x12);
        appearance.ShadowColor = UIColor.FromRGB(0x33, 0x33, 0x33);
        appearance.TitleTextAttributes = new UIStringAttributes { ForegroundColor = UIColor.FromRGB(0xEE, 0xEE, 0xEE) };
        _navigation.NavigationBar.StandardAppearance = appearance;
        _navigation.NavigationBar.ScrollEdgeAppearance = appearance;
        _navigation.NavigationBar.CompactAppearance = appearance;
        _navigation.NavigationBar.TintColor = UIColor.FromRGB(0xEE, 0xEE, 0xEE);
        _navigation.NavigationBar.BarStyle = UIBarStyle.Black;
        _navigation.NavigationBar.Translucent = false;
        Subscribe(tools);
    }

    /// <summary>订阅共享工具导航状态（同一实例只订阅一次）。</summary>
    /// <param name="tools">共享工具壳 ViewModel。</param>
    private void Subscribe(ToolsViewModel tools)
    {
        tools.PropertyChanged -= OnToolsChanged;
        tools.PropertyChanged += OnToolsChanged;
    }

    /// <summary>列表页按钮：进入 EXIF 详情（共享状态先行，再压入页面）。</summary>
    internal void OpenExif()
    {
        if (_tools == null || _navigation == null || _tools.ExifDetail == null) return;
        _tools.OpenExifDetail();
        _exif ??= new iOSExifDetailView();
        _exif.Show(_tools.ExifDetail);
        _navigation.PushViewController(_exif, true);
    }

    /// <summary>列表页按钮：进入 DINO 诊断（共享状态先行，再压入页面）。</summary>
    internal void OpenDino()
    {
        if (_tools == null || _navigation == null) return;
        _tools.OpenDinoDebug();
        _dino ??= new iOSDinoDiagnosticView(_tools.DinoPresenter);
        _dino.Sync();
        _navigation.PushViewController(_dino, true);
    }

    /// <summary>返回列表首页（系统返回按钮/滑动返回后由列表页调用）。</summary>
    internal void BackToList()
    {
        _tools?.ShowList();
    }

    /// <summary>当前照片变化或共享导航状态改变后同步可见子页（不抢焦点、不改变栈结构）。</summary>
    internal void SyncCurrentPage()
    {
        if (_tools == null) return;
        if (_tools.CurrentTool is ExifDetailViewModel)
        {
            if (_exif != null && _tools.ExifDetail is { } detail) _exif.Show(detail);
            return;
        }
        if (_tools.CurrentTool is DinoDebugViewModel) _dino?.Sync();
    }

    /// <summary>共享导航状态变化：EXIF VM 被替换或回到列表态时同步页面内容。</summary>
    /// <param name="sender">事件源（工具壳 VM）。</param>
    /// <param name="args">属性变化参数。</param>
    private void OnToolsChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(ToolsViewModel.ExifDetail) && _exif != null && _tools?.ExifDetail != null)
            _exif.Show(_tools.ExifDetail);
        if (args.PropertyName == nameof(ToolsViewModel.DinoDebug)) _dino?.Sync();
    }
}

/// <summary>工具列表首页：EXIF 详情 / DINO 诊断两个入口（照片数据统计为 Windows 专属，不建入口）。</summary>
internal sealed class iOSNativeToolsListViewController : UIViewController
{
    /// <summary>入口按钮高度。</summary>
    private const double ButtonHeight = 32;

    /// <summary>工具列表外边距（旧壳 Margin 16）。</summary>
    private const double ListMargin = 16;

    /// <summary>工具列表按钮间距（旧壳 Spacing 8）。</summary>
    private const double ListSpacing = 8;

    private readonly ToolsViewModel _tools;
    private readonly iOSNativeToolsPresenter _owner;
    private readonly UIButton _exif;
    private readonly UIButton _dino;

    /// <summary>列表页的两个入口按钮（验收读取标题与触发真实点击）。</summary>
    internal IReadOnlyList<UIButton> Entries => [_exif, _dino];

    /// <summary>构造列表页与两个入口按钮。</summary>
    /// <param name="tools">共享工具壳 ViewModel。</param>
    /// <param name="owner">工具页宿主。</param>
    internal iOSNativeToolsListViewController(ToolsViewModel tools, iOSNativeToolsPresenter owner) : base(null, null)
    {
        _tools = tools;
        _owner = owner;
        Title = "工具";
        _exif = Entry("EXIF 详情", owner.OpenExif);
        _dino = Entry("DINO 诊断", owner.OpenDino);
        View!.BackgroundColor = UIColor.FromRGB(0x12, 0x12, 0x12);
        View.AddSubview(_exif);
        View.AddSubview(_dino);
        NavigationItem.RightBarButtonItem = new UIBarButtonItem("关闭", UIBarButtonItemStyle.Plain, (_, _) => owner.Dismiss());
    }

    /// <summary>每次回到列表页都把共享导航状态复位到首页并刷新入口可用性。</summary>
    /// <param name="animated">系统动画标记。</param>
    public override void ViewWillAppear(bool animated)
    {
        base.ViewWillAppear(animated);
        _owner.BackToList();
        _exif.Enabled = _tools.IsExifDetailEnabled;
    }

    /// <summary>两个入口按 16 点外边距与 8 点间距纵向排列。</summary>
    public override void ViewDidLayoutSubviews()
    {
        base.ViewDidLayoutSubviews();
        double width = Math.Max(1, (double)View!.Bounds.Width - ListMargin * 2);
        _exif.Frame = new CGRect(ListMargin, ListMargin, width, ButtonHeight);
        _dino.Frame = new CGRect(ListMargin, ListMargin + ButtonHeight + ListSpacing, width, ButtonHeight);
    }

    /// <summary>创建深色圆角入口按钮。</summary>
    /// <param name="title">按钮标题。</param>
    /// <param name="action">点击动作。</param>
    /// <returns>入口按钮。</returns>
    private static UIButton Entry(string title, Action action)
    {
        var button = new UIButton(UIButtonType.Custom);
        button.SetTitle(title, UIControlState.Normal);
        button.SetTitleColor(UIColor.FromRGB(0xEE, 0xEE, 0xEE), UIControlState.Normal);
        button.SetTitleColor(UIColor.FromRGB(0x77, 0x77, 0x77), UIControlState.Disabled);
        button.TitleLabel!.Font = UIFont.SystemFontOfSize(13);
        button.BackgroundColor = UIColor.FromRGB(0x2A, 0x2A, 0x2A);
        button.Layer.CornerRadius = 6;
        button.Layer.BorderWidth = 1;
        button.Layer.BorderColor = UIColor.FromRGB(0x44, 0x44, 0x44).CGColor;
        button.TouchUpInside += (_, _) => action();
        button.AccessibilityLabel = title;
        return button;
    }
}
