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
        _dismissDelegate ??= new ToolsDismissDelegate(this);
        // 展示控制器只在展示时存在：接着挂自适应关闭代理，覆盖下滑关闭与关闭按钮两条路径。
        if (_navigation!.PresentationController is { } presentation)
            presentation.Delegate = _dismissDelegate;
        // TOOLS-01：主窗口据此判断"已展示只前置、未展示才回首页"。
        tools.IsPresented = true;
        _navigation.SetViewControllers([_list!], false);
        tools.ShowList();
        host.PresentViewController(_navigation, true, () =>
        {
            if (_navigation.PresentationController is { } controller) controller.Delegate = _dismissDelegate;
        });
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
        _navigation = new UINavigationController(_list)
        {
            ModalPresentationStyle = UIModalPresentationStyle.PageSheet,
            OverrideUserInterfaceStyle = UIUserInterfaceStyle.Dark,
        };
        iOSNativeSettingsPresenter.ConfigureSheetPresentation(_navigation.SheetPresentationController);
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

    /// <summary>
    /// sheet 已关闭（下滑、关闭按钮或程序化 Dismiss 的统一收口）：清除共享展示态并取消在途 DINO 任务，
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

/// <summary>工具列表首页：EXIF 详情 / DINO 诊断两个入口（照片数据统计为 Windows 专属，不建入口）。</summary>
internal sealed class iOSNativeToolsListViewController : UITableViewController
{
    private readonly ToolsViewModel _tools;
    private readonly iOSNativeToolsPresenter _owner;
    internal IReadOnlyList<string> Entries => ["EXIF 详情", "DINO 诊断"];
    internal iOSNativeToolsListViewController(ToolsViewModel tools, iOSNativeToolsPresenter owner) : base(UITableViewStyle.InsetGrouped)
    {
        _tools = tools;
        _owner = owner;
        Title = "工具";
        OverrideUserInterfaceStyle = UIUserInterfaceStyle.Dark;
        TableView.BackgroundColor = UIColor.SystemGroupedBackground.GetResolvedColor(UITraitCollection.FromUserInterfaceStyle(UIUserInterfaceStyle.Dark));
        NavigationItem.RightBarButtonItem = new UIBarButtonItem(UIBarButtonSystemItem.Close, (_, _) => owner.Dismiss());
    }
    public override void ViewWillAppear(bool animated)
    {
        base.ViewWillAppear(animated);
        _owner.BackToList();
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
        else _owner.OpenDino();
    }
}
