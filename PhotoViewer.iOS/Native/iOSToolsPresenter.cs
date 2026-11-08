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
/// <see cref="UINavigationController"/>，根页为工具列表（EXIF 详情 / 诊断工具；照片数据统计为 Windows
/// 专属，不建入口），子页为 EXIF 详情与当前发行版的诊断页（AI 版 DINO 诊断、标准版 CV 诊断）。
/// 导航状态以共享 <see cref="ToolsViewModel"/> 为准：
/// 进入子页写共享状态，返回列表页恢复 <c>ShowList</c>；关闭后对象保留，可再次展示。
/// </summary>
internal sealed class iOSNativeToolsPresenter
{
    private ToolsViewModel? _tools;
    private UINavigationController? _navigation;
    private iOSNativeToolsListViewController? _list;
    private iOSExifDetailView? _exif;
    private iOSDinoDiagnosticView? _diagnostic;
    private ToolsDismissDelegate? _dismissDelegate;

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
            // TOOLBAR-06：已展示时 EXIF 入口仍然明确导航到 EXIF 详情（或按 TOOLS-02 回退列表），
            // 不被"已展示只前置"拦截；主图菜单入口（openExif=false）仍只同步当前页。
            if (openExif)
            {
                ShowExifEntry(tools);
                return;
            }
            SyncCurrentPage();
            return;
        }
        var host = iOSViewControllerLocator.GetTopMostViewController();
        if (host == null)
        {
            throw new InvalidOperationException("工具页没有可展示的宿主控制器");
        }
        _dismissDelegate ??= new ToolsDismissDelegate(this);
        // TOOLS-01：每次展示都按设置页同一份 sheet 配置重新挂一次（detents/grabber/圆角/滚动扩展策略）：
        // 重开复用同一导航控制器时，展示期重建的 sheet 控制器会丢掉上次的配置，只在 Build 时配一次不够。
        ApplySheetStyle();
        // 展示控制器只在展示时存在：接着挂自适应关闭代理，覆盖下滑关闭与关闭按钮两条路径。
        if (_navigation!.PresentationController is { } presentation)
            presentation.Delegate = _dismissDelegate;
        if (openExif)
            ShowExifEntry(tools);
        else
        {
            _navigation.SetViewControllers([_list!], false);
            tools.ShowList();
        }
        // TOOLS-01：主窗口据此判断"已展示只前置、未展示才回首页"。
        tools.IsPresented = true;
        host.PresentViewController(_navigation, true, () =>
        {
            if (_navigation.PresentationController is { } controller)
            {
                controller.Delegate = _dismissDelegate;
            }
        });
    }

    /// <summary>
    /// 控制栏 EXIF 入口的导航：可用时压入 EXIF 详情（列表页保留为返回目标），
    /// 不可用时回退列表；已展示其他诊断页时同样切过来（TOOLBAR-06）。
    /// </summary>
    /// <param name="tools">共享工具壳 ViewModel。</param>
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
            _exif ??= new iOSExifDetailView();
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
    }

    /// <summary>关闭工具页（sheet 下拉关闭同样走系统路径）；对象保留可重开。</summary>
    internal void Dismiss()
    {
        if (!IsPresented) return;
        // 关闭按钮/程序化关闭立即复位共享状态；下滑关闭由 DidDismiss 走同一收口（重复调用幂等）。
        HandleDismissed();
        _navigation!.DismissViewController(true, null);
    }

    /// <summary>创建导航控制器与列表首页，并订阅共享导航状态。</summary>
    /// <param name="tools">共享工具壳 ViewModel。</param>
    private void Build(ToolsViewModel tools)
    {
        _list = new iOSNativeToolsListViewController(tools, this);
        _navigation = new UINavigationController(_list);
        ApplySheetStyle();
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

    /// <summary>列表页按钮：进入当前发行版的诊断工具（共享状态先行，再压入页面）。</summary>
    internal void OpenDiagnostic()
    {
        if (_tools == null || _navigation == null) return;
        _tools.OpenDiagnostic();
#if PHOTOVIEWER_DINO
        _diagnostic ??= new iOSDinoDiagnosticView(_tools.DinoPresenter);
#else
        _diagnostic ??= new iOSDinoDiagnosticView(_tools.CvPresenter);
#endif
        _diagnostic.Sync();
        _navigation.PushViewController(_diagnostic, true);
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
        if (ReferenceEquals(_tools.CurrentTool, _tools.DiagnosticDebug)) _diagnostic?.Sync();
    }

    /// <summary>共享导航状态变化：EXIF VM 被替换或回到列表态时同步页面内容。</summary>
    /// <param name="sender">事件源（工具壳 VM）。</param>
    /// <param name="args">属性变化参数。</param>
    private void OnToolsChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(ToolsViewModel.ExifDetail) && _exif != null && _tools?.ExifDetail != null)
            _exif.Show(_tools.ExifDetail);
        if (args.PropertyName == nameof(ToolsViewModel.DiagnosticDebug)) _diagnostic?.Sync();
    }

    /// <summary>
    /// sheet 已关闭（下滑、关闭按钮或程序化 Dismiss 的统一收口）：清除共享展示态并取消在途诊断任务，
    /// 页面对象保留以便重开（TOOLS-01/TOOLS-06）。
    /// </summary>
    private void HandleDismissed()
    {
        if (_tools == null) return;
        _tools.IsPresented = false;
        _tools.Deactivate();
    }

    /// <summary>自适应展示代理：把系统关闭路径转交给宿主统一处理。</summary>
    private sealed class ToolsDismissDelegate(iOSNativeToolsPresenter owner) : UIAdaptivePresentationControllerDelegate
    {
        /// <summary>系统完成关闭后复位共享状态并取消后台计算。</summary>
        /// <param name="presentationController">本次展示控制器。</param>
        public override void DidDismiss(UIPresentationController presentationController) => owner.HandleDismissed();
    }
}

/// <summary>工具列表首页：EXIF 详情 / 发行版诊断工具两个入口（照片数据统计为 Windows 专属，不建入口）。</summary>
internal sealed class iOSNativeToolsListViewController : UITableViewController
{
    private readonly ToolsViewModel _tools;
    private readonly iOSNativeToolsPresenter _owner;
    internal static IReadOnlyList<string> Titles => ["EXIF 详情", iOSAppCapabilities.DiagnosticName];
    internal IReadOnlyList<string> Entries => Titles;
    internal iOSNativeToolsListViewController(ToolsViewModel tools, iOSNativeToolsPresenter owner) : base(UITableViewStyle.InsetGrouped)
    {
        _tools = tools;
        _owner = owner;
        Title = "工具";
        TableView.BackgroundColor = UIColor.SystemGroupedBackground;
        TableView.CellLayoutMarginsFollowReadableWidth = false;
        NavigationItem.RightBarButtonItem = new UIBarButtonItem(UIBarButtonSystemItem.Close, (_, _) => owner.Dismiss());
    }
    public override void ViewWillAppear(bool animated)
    {
        base.ViewWillAppear(animated);
        // 只有列表页真的成为可见页时才回到列表态：控制栏直达 EXIF 时列表仍在导航栈底部，
        // 不能把共享导航状态改回列表（否则详情页与 TopViewController 不同步）。
        if (ReferenceEquals(NavigationController?.TopViewController, this)) _owner.BackToList();
        TableView.ReloadData();
    }
    public override nint RowsInSection(UITableView tableView, nint section) => 2;
    public override UITableViewCell GetCell(UITableView tableView, NSIndexPath indexPath)
    {
        var cell = new UITableViewCell(UITableViewCellStyle.Default, null);
        var config = UIListContentConfiguration.CellConfiguration;
        config.Text = Entries[indexPath.Row];
        config.TextProperties.Color = indexPath.Row == 0 && !_tools.IsExifDetailEnabled ? UIColor.TertiaryLabel : UIColor.Label;
        cell.ContentConfiguration = config;
        cell.Accessory = UITableViewCellAccessory.DisclosureIndicator;
        return cell;
    }
    public override void RowSelected(UITableView tableView, NSIndexPath indexPath)
    {
        tableView.DeselectRow(indexPath, true);
        if (indexPath.Row == 0) { if (_tools.IsExifDetailEnabled) _owner.OpenExif(); }
        else _owner.OpenDiagnostic();
    }
}
