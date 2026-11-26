using System.ComponentModel;
using Foundation;
using PhotoViewer.Core.Platform;
using PhotoViewer.iOS.Core;
using PhotoViewer.ViewModels.Tools;
using UIKit;

namespace PhotoViewer.iOS.Native;

/// <summary>
/// iOS / iPadOS 原生工具页宿主：以设置同款 <see cref="UIModalPresentationStyle.PageSheet"/> 承载
/// <see cref="UINavigationController"/>，根页为原生 <see cref="UITableViewStyle.InsetGrouped"/> 工具列表
/// （EXIF 详情 / 当前发行版诊断工具；照片数据统计为 Windows 专属，不建入口），子页为 EXIF 详情与诊断页。
/// 导航状态以共享 <see cref="ToolsViewModel"/> 为准，关闭后页面对象保留可重开。
/// </summary>
internal sealed class iOSNativeToolsPresenter
{
    private ToolsViewModel? _tools;
    private iOSNativeToolsNavigationController? _navigation;
    private iOSNativeToolsListViewController? _list;
    private iOSExifDetailView? _exif;
    private iOSDinoDiagnosticView? _diagnostic;

    /// <summary>当前进程里的工具页宿主（验收读取真实导航栈与页面）。</summary>
    internal static iOSNativeToolsPresenter? Current { get; private set; }

    /// <summary>内容区滚动守卫（区分“未到顶滚动页面”与“到顶后再下拉关闭”）。</summary>
    internal iOSSheetScrollGuard ScrollGuard => _navigation?.ScrollGuard ?? throw new InvalidOperationException("工具页导航控制器尚未创建");

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

    /// <summary>发行版诊断页（AI 版 DINO 诊断、标准版 CV 诊断）；尚未创建时为 null。</summary>
    internal iOSDinoDiagnosticView? DiagnosticPage => _diagnostic;

    /// <summary>打开或前置工具页；已在展示时只做内容同步。</summary>
    /// <param name="tools">共享工具壳 ViewModel。</param>
    /// <param name="openExif">控制栏 EXIF 信息块入口：首次展示直接进入 EXIF 详情，无可用 EXIF 时回退列表。</param>
    internal void ShowOrActivate(ToolsViewModel tools, bool openExif = false)
    {
        _tools = tools;
        if (_navigation == null) Build(tools);
        else Subscribe(tools);

        if (IsPresented)
        {
            if (openExif)
            {
                ShowExifEntry(tools);
                return;
            }
            SyncCurrentPage();
            return;
        }

        var host = iOSViewControllerLocator.GetTopMostViewController()
            ?? throw new InvalidOperationException("工具页没有可展示的宿主控制器");

        // 重开复用同一导航控制器时重新配置系统 sheet 控制器（detents / grabber / 自动同心圆角 / 不拉大策略）。
        ApplySheetStyle();
        if (openExif)
        {
            ShowExifEntry(tools);
        }
        else
        {
            _navigation!.SetViewControllers([_list!], false);
            tools.ShowList();
        }

        tools.IsPresented = true;
        host.PresentViewController(_navigation!, true, null);
    }

    /// <summary>
    /// 控制栏 EXIF 入口的导航：可用时压入 EXIF 详情（列表页保留为返回目标），
    /// 不可用时回退列表；已展示其他诊断页时同样切过来（TOOLBAR-06）。
    /// </summary>
    private void ShowExifEntry(ToolsViewModel tools)
    {
        if (_navigation == null) return;
        if (tools.IsExifDetailEnabled && tools.ExifDetail is { } detail)
        {
            if (_navigation.TopViewController is iOSExifDetailView && tools.CurrentTool is ExifDetailViewModel)
            {
                SyncCurrentPage();
                return;
            }
            _exif ??= new iOSExifDetailView(Dismiss);
            _exif.Show(detail);
            _navigation.SetViewControllers([_list!, _exif], false);
            tools.OpenExifDetail();
        }
        else
        {
            _navigation.SetViewControllers([_list!], false);
            tools.ShowList();
        }
    }

    /// <summary>按设置页同一份配置设置工具 sheet：系统 PageSheet + 固定深色 + 半屏/全屏两档 detents。</summary>
    private void ApplySheetStyle()
    {
        if (_navigation == null) return;
        _navigation.ModalPresentationStyle = UIModalPresentationStyle.PageSheet;
        _navigation.OverrideUserInterfaceStyle = UIUserInterfaceStyle.Dark;
        iOSNativeSettingsPresenter.ConfigureSheetPresentation(_navigation.SheetPresentationController);
        _navigation.BindDismissDelegate();
    }

    /// <summary>关闭工具页（sheet 下拉关闭同样走系统路径）；对象保留可重开。</summary>
    internal void Dismiss()
    {
        if (!IsPresented) return;
        HandleDismissed();
        _navigation!.DismissViewController(true, null);
    }

    private void Build(ToolsViewModel tools)
    {
        _list = new iOSNativeToolsListViewController(tools, this);
        _navigation = new iOSNativeToolsNavigationController(_list, HandleDismissed);
        ApplySheetStyle();
        Subscribe(tools);
    }

    private void Subscribe(ToolsViewModel tools)
    {
        tools.PropertyChanged -= OnToolsChanged;
        tools.PropertyChanged += OnToolsChanged;
    }

    /// <summary>列表页点击：进入 EXIF 详情（共享状态先行，再压入页面）。</summary>
    internal void OpenExif()
    {
        if (_tools == null || _navigation == null || _tools.ExifDetail == null) return;
        _tools.OpenExifDetail();
        _exif ??= new iOSExifDetailView(Dismiss);
        _exif.Show(_tools.ExifDetail);
        _navigation.PushViewController(_exif, true);
    }

    /// <summary>列表页点击：进入当前发行版的诊断工具（共享状态先行，再压入页面）。</summary>
    internal void OpenDiagnostic()
    {
        if (_tools == null || _navigation == null) return;
        _tools.OpenDiagnostic();
#if PHOTOVIEWER_DINO
        _diagnostic ??= new iOSDinoDiagnosticView(_tools.DinoPresenter, Dismiss);
#else
        _diagnostic ??= new iOSDinoDiagnosticView(_tools.CvPresenter, Dismiss);
#endif
        _diagnostic.Sync();
        _navigation.PushViewController(_diagnostic, true);
    }

    /// <summary>返回列表首页（系统返回按钮/滑动返回后由列表页调用）。</summary>
    internal void BackToList() => _tools?.ShowList();

    /// <summary>当前照片变化或共享导航状态改变后同步可见子页（不抢焦点、不改变栈结构）。</summary>
    internal void SyncCurrentPage()
    {
        if (_tools == null) return;
        if (_tools.CurrentTool is ExifDetailViewModel)
        {
            if (_exif != null && _tools.ExifDetail is { } detail) _exif.Show(detail);
            return;
        }
        if (ReferenceEquals(_tools.CurrentTool, _tools.DiagnosticDebug)) _diagnostic?.Sync();
    }

    private void OnToolsChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(ToolsViewModel.ExifDetail) && _exif != null && _tools?.ExifDetail != null)
            _exif.Show(_tools.ExifDetail);
        if (args.PropertyName == nameof(ToolsViewModel.DiagnosticDebug)) _diagnostic?.Sync();
    }

    /// <summary>sheet 已关闭（下滑、关闭按钮或程序化 Dismiss 的统一收口）：清除共享展示态并取消在途诊断。</summary>
    private void HandleDismissed()
    {
        if (_tools == null) return;
        _tools.IsPresented = false;
        _tools.Deactivate();
    }
}

/// <summary>
/// 工具页导航控制器：与设置页 <see cref="iOSNativeSettingsNavigationController"/> 同构，
/// 统一持有 <see cref="iOSSheetScrollGuard"/> 与自适应关闭代理。
/// </summary>
internal sealed class iOSNativeToolsNavigationController : UINavigationController
{
    private readonly iOSSheetScrollGuard _scrollGuard = new();
    private readonly ToolsDismissDelegate _dismissDelegate;

    internal iOSSheetScrollGuard ScrollGuard => _scrollGuard;

    public iOSNativeToolsNavigationController(UIViewController rootViewController, Action onDismissed)
        : base(rootViewController)
    {
        ModalPresentationStyle = UIModalPresentationStyle.PageSheet;
        OverrideUserInterfaceStyle = UIUserInterfaceStyle.Dark;
        Delegate = _scrollGuard;
        _dismissDelegate = new ToolsDismissDelegate(this, onDismissed);
    }

    internal void BindDismissDelegate()
    {
        if (PresentationController != null)
        {
            PresentationController.Delegate = _dismissDelegate;
        }
    }

    public override void ViewWillAppear(bool animated)
    {
        base.ViewWillAppear(animated);
        _scrollGuard.Attach(TopViewController);
        BindDismissDelegate();
    }

    private sealed class ToolsDismissDelegate(iOSNativeToolsNavigationController navigation, Action onDismissed)
        : UIAdaptivePresentationControllerDelegate
    {
        public override bool ShouldDismiss(UIPresentationController presentationController)
            => navigation.ScrollGuard.ShouldAllowDismiss(navigation.TopViewController);

        public override void DidDismiss(UIPresentationController presentationController) => onDismissed();
    }
}

/// <summary>
/// 工具列表首页：原生 <see cref="UITableViewStyle.InsetGrouped"/> 分组列表，
/// 承载 EXIF 详情与当前发行版诊断工具两个入口（照片数据统计为 Windows 专属，不建入口）。
/// </summary>
internal sealed class iOSNativeToolsListViewController : UITableViewController
{
    private const string CellReuseId = "NativeToolsListCell";

    private readonly ToolsViewModel _tools;
    private readonly iOSNativeToolsPresenter _owner;

    internal static IReadOnlyList<string> Titles => ["EXIF 详情", iOSAppCapabilities.DiagnosticName];
    internal IReadOnlyList<string> Entries => Titles;

    internal iOSNativeToolsListViewController(ToolsViewModel tools, iOSNativeToolsPresenter owner)
        : base(UITableViewStyle.InsetGrouped)
    {
        _tools = tools;
        _owner = owner;
    }

    public override void ViewDidLoad()
    {
        base.ViewDidLoad();
        Title = "工具";
        NavigationItem.RightBarButtonItem = new UIBarButtonItem(UIBarButtonSystemItem.Close, (_, _) => _owner.Dismiss());
        TableView.BackgroundColor = UIColor.SystemGroupedBackground;
        TableView.CellLayoutMarginsFollowReadableWidth = false;
    }

    public override void ViewWillAppear(bool animated)
    {
        base.ViewWillAppear(animated);
        // 只有列表页真的成为可见页时才回到列表态：控制栏直达 EXIF 时列表仍在导航栈底部，不能把共享导航状态改回列表。
        if (ReferenceEquals(NavigationController?.TopViewController, this)) _owner.BackToList();
        TableView.ReloadData();
    }

    public override nint NumberOfSections(UITableView tableView) => 1;

    public override nint RowsInSection(UITableView tableView, nint section) => 2;

    public override UITableViewCell GetCell(UITableView tableView, NSIndexPath indexPath)
    {
        var cell = tableView.DequeueReusableCell(CellReuseId)
            ?? new UITableViewCell(UITableViewCellStyle.Subtitle, CellReuseId);

        bool isExif = indexPath.Row == 0;
        bool enabled = !isExif || _tools.IsExifDetailEnabled;

        var config = UIListContentConfiguration.SubtitleCellConfiguration;
        config.Text = Entries[(int)indexPath.Row];
        config.TextProperties.Color = enabled ? UIColor.Label : UIColor.TertiaryLabel;
        config.SecondaryText = isExif
            ? (enabled ? "完整元数据与伴侣格式" : "当前照片无可用 EXIF")
            : (iOSAppCapabilities.HasDino ? "DINO patch 与 CV 网格诊断" : "锐度与抖动网格诊断");
        config.SecondaryTextProperties.Color = enabled ? UIColor.SecondaryLabel : UIColor.TertiaryLabel;
        config.Image = UIImage.GetSystemImage(isExif ? "info.circle" : "square.grid.3x3");
        config.ImageProperties.TintColor = enabled ? UIColor.SystemBlue : UIColor.TertiaryLabel;
        cell.ContentConfiguration = config;

        cell.Accessory = enabled ? UITableViewCellAccessory.DisclosureIndicator : UITableViewCellAccessory.None;
        cell.SelectionStyle = enabled ? UITableViewCellSelectionStyle.Default : UITableViewCellSelectionStyle.None;
        cell.AccessibilityTraits = enabled
            ? cell.AccessibilityTraits & ~UIAccessibilityTrait.NotEnabled
            : cell.AccessibilityTraits | UIAccessibilityTrait.NotEnabled;
        return cell;
    }

    public override NSIndexPath? WillSelectRow(UITableView tableView, NSIndexPath indexPath)
        => indexPath.Row == 0 && !_tools.IsExifDetailEnabled ? null : indexPath;

    public override void RowSelected(UITableView tableView, NSIndexPath indexPath)
    {
        tableView.DeselectRow(indexPath, true);
        if (indexPath.Row == 0)
        {
            if (_tools.IsExifDetailEnabled) _owner.OpenExif();
        }
        else
        {
            _owner.OpenDiagnostic();
        }
    }
}
