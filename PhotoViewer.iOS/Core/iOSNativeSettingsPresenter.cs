using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CoreGraphics;
using Foundation;
using PhotoViewer.Core.Design;
using PhotoViewer.Core.Settings;
using PhotoViewer.ViewModels.Settings;
using UIKit;

namespace PhotoViewer.iOS.Core;

/// <summary>
/// iOS 原生设置页展示器。
/// 负责以系统半屏浮窗形式承载共享设置 ViewModel 的原生编辑界面。
/// </summary>
public sealed class iOSNativeSettingsPresenter : INativeSettingsPresenter
{
    private NativeSettingsSession? _session;

    /// <summary>
    /// 尝试展示 iOS 原生设置页。
    /// </summary>
    /// <param name="settings">共享层设置 ViewModel。</param>
    /// <returns>成功接管展示时返回 true，否则返回 false。</returns>
    public bool TryPresent(SettingsViewModel settings)
    {
        var presentingController = iOSViewControllerLocator.GetPresentingController();
        if (presentingController == null)
        {
            return false;
        }

        void Present()
        {
            if (FindExistingSettingsController(presentingController) != null)
            {
                return;
            }

            if (iOSViewControllerLocator.GetTopMostViewController() is not { } topController)
            {
                return;
            }

            // 同一时刻只保留一份工作副本；关闭后由 Closed 清空，下次展示重建。
            var session = _session ??= new NativeSettingsSession(settings);
            var rootController = new iOSNativeSettingsRootViewController(session, settings, Closed);
            var navigationController = new iOSNativeSettingsNavigationController(rootController, rootController);
            // SETTINGS-03：设置页固定深色，不随系统亮/暗切换（与工具页同一口径）。
            navigationController.OverrideUserInterfaceStyle = UIUserInterfaceStyle.Dark;
            ConfigureSheetPresentation(navigationController.SheetPresentationController);

            topController.PresentViewController(navigationController, true, null);
        }

        if (NSThread.IsMain)
        {
            Present();
        }
        else
        {
            UIApplication.SharedApplication.BeginInvokeOnMainThread(Present);
        }

        return true;
    }

    /// <summary>设置页关闭且工作副本已提交/释放：丢弃会话引用，等待下次展示重建。</summary>
    private void Closed() => _session = null;

    /// <summary>
    /// 查找当前已经展示的原生设置导航控制器。
    /// </summary>
    /// <param name="rootController">当前宿主控制器。</param>
    /// <returns>已展示时返回导航控制器，否则返回 null。</returns>
    private static UINavigationController? FindExistingSettingsController(UIViewController rootController)
    {
        UIViewController? current = rootController;
        while (current != null)
        {
            if (current is UINavigationController navigationController &&
                navigationController.ViewControllers?.FirstOrDefault() is iOSNativeSettingsRootViewController)
            {
                return navigationController;
            }

            current = current.PresentedViewController;
        }

        return null;
    }

    /// <summary>
    /// 配置 iOS 半屏浮窗的 detents 与交互参数。
    /// 不覆写 <see cref="UISheetPresentationController.PreferredCornerRadius"/>，保持系统默认
    /// <see cref="UISheetPresentationControllerDetent.AutomaticDimension"/>，由 iOS 26 自动计算底部同心圆角与原生材质。
    /// </summary>
    /// <param name="sheetPresentationController">系统 Sheet 控制器。</param>
    internal static void ConfigureSheetPresentation(UISheetPresentationController? sheetPresentationController)
    {
        if (sheetPresentationController == null)
        {
            return;
        }

        sheetPresentationController.Detents =
        [
            UISheetPresentationControllerDetent.CreateMediumDetent(),
            UISheetPresentationControllerDetent.CreateLargeDetent(),
        ];
        sheetPresentationController.PrefersGrabberVisible = true;
        // SETTINGS-04：内容区滚动优先滚动列表，不借滚动拉大 sheet（收放由顶部把手/导航栏承担）。
        sheetPresentationController.PrefersScrollingExpandsWhenScrolledToEdge = false;
    }
}

/// <summary>
/// iOS 原生设置导航控制器。
/// 负责持有关闭代理与内容滚动守卫，确保内容区下滑不误关半屏，且系统下拉/关闭按钮都会统一提交并释放工作副本。
/// </summary>
internal sealed class iOSNativeSettingsNavigationController : UINavigationController
{
    private readonly iOSNativeSettingsDismissDelegate _dismissDelegate;
    private readonly iOSSheetScrollGuard _scrollGuard = new();

    /// <summary>内容区滚动守卫（验收核对内容滑动不触发关闭、把手与外部点击可关闭）。</summary>
    internal iOSSheetScrollGuard ScrollGuard => _scrollGuard;

    /// <summary>
    /// 初始化原生设置导航控制器。
    /// </summary>
    /// <param name="rootViewController">根控制器。</param>
    /// <param name="settingsRoot">设置页根控制器，关闭回写与释放由它统一负责。</param>
    public iOSNativeSettingsNavigationController(UIViewController rootViewController, iOSNativeSettingsRootViewController settingsRoot)
        : base(rootViewController)
    {
        ModalPresentationStyle = UIModalPresentationStyle.PageSheet;
        Delegate = _scrollGuard;
        _dismissDelegate = new iOSNativeSettingsDismissDelegate(this, settingsRoot);
    }

    /// <summary>
    /// 页面即将显示时挂接系统 dismiss 回调：presentation controller 在展示开始时才存在，
    /// 放在 viewDidLoad 会拿不到它，导致系统下拉关闭不回写设置。
    /// </summary>
    /// <param name="animated">系统动画标记。</param>
    public override void ViewWillAppear(bool animated)
    {
        base.ViewWillAppear(animated);
        _scrollGuard.Attach(TopViewController);
        if (PresentationController != null)
        {
            PresentationController.Delegate = _dismissDelegate;
        }
    }
}

/// <summary>
/// iOS 原生设置关闭代理。
/// 拦截内容区下滑到顶引起的误关闭，并统一处理关闭按钮与系统下拉收起后的设置提交。
/// </summary>
internal sealed class iOSNativeSettingsDismissDelegate : UIAdaptivePresentationControllerDelegate
{
    private readonly iOSNativeSettingsNavigationController _navigation;
    private readonly iOSNativeSettingsRootViewController _root;

    /// <summary>
    /// 初始化关闭代理。
    /// </summary>
    /// <param name="navigation">设置导航控制器。</param>
    /// <param name="root">设置页根控制器。</param>
    public iOSNativeSettingsDismissDelegate(
        iOSNativeSettingsNavigationController navigation,
        iOSNativeSettingsRootViewController root)
    {
        _navigation = navigation;
        _root = root;
    }

    /// <summary>内容滚动手势期间不因下滑到顶误关半屏；顶部把手/导航栏下拉与外部点击正常放行。</summary>
    /// <param name="presentationController">当前展示控制器。</param>
    /// <returns>允许关闭时返回 true。</returns>
    public override bool ShouldDismiss(UIPresentationController presentationController)
        => _navigation.ScrollGuard.ShouldAllowDismiss(_navigation.TopViewController);

    /// <summary>
    /// 在系统 sheet 完成关闭后提交工作副本并释放会话（重复触发只执行一次）。
    /// </summary>
    /// <param name="presentationController">当前展示控制器。</param>
    public override void DidDismiss(UIPresentationController presentationController)
    {
        _ = _root.CloseAsync();
    }
}

/// <summary>
/// iOS 原生设置页根控制器。
/// 负责展示一级分组入口并提供关闭按钮。
/// </summary>
internal sealed class iOSNativeSettingsRootViewController : UITableViewController
{
    private readonly NativeSettingsSession _session;
    private readonly SettingsViewModel _source;
    private readonly SettingsViewModel _settings;
    private readonly Action _closed;

    private readonly (string Title, string? Subtitle, Func<UIViewController> Factory)[] _items;

    /// <summary>当前编辑会话（测试缝：验收核对关闭回写后的设置传播）。</summary>
    internal NativeSettingsSession Session => _session;

    /// <summary>源设置实例（测试缝）。</summary>
    internal SettingsViewModel Source => _source;

    /// <summary>
    /// 初始化原生设置页根控制器。
    /// </summary>
    /// <param name="session">共享原生设置会话（关闭时一次性提交）。</param>
    /// <param name="source">共享层当前生效的设置实例。</param>
    /// <param name="closed">关闭完成回调，用于让展示器丢弃会话引用。</param>
    public iOSNativeSettingsRootViewController(NativeSettingsSession session, SettingsViewModel source, Action closed)
        : base(UITableViewStyle.InsetGrouped)
    {
        _session = session;
        _source = source;
        _closed = closed;
        _settings = session.WorkingSettings;
        _items =
        [
            ("文件", "同名合并、文件格式与缓存预加载", () => new iOSNativeFileSettingsViewController(_settings)),
            ("预览", "缩放指示器与缩放比例预设", () => new iOSNativePreviewSettingsViewController(_settings)),
            ("控制", "布局、控制栏显示、快捷键与键盘映射", () => new iOSNativeControlSettingsViewController(_settings)),
            ("EXIF", "EXIF 显示项与评分写回策略", () => new iOSNativeExifSettingsViewController(_settings)),
            ("AI", null, () => new iOSNativeAiSettingsViewController(_settings)),
        ];
    }

    private int _closedFlag;
    private Task? _closeTask;

    /// <summary>提交工作副本并释放会话；关闭按钮与系统下拉共用，重复调用只执行一次。</summary>
    internal Task CloseAsync() => _closeTask ??= CloseCoreAsync();

    private async Task CloseCoreAsync()
    {
        if (Interlocked.Exchange(ref _closedFlag, 1) != 0) return;
        try
        {
            View?.EndEditing(true);
            await _session.ApplyAsync().ConfigureAwait(false);
        }
        finally
        {
            _session.Dispose();
            _closed();
        }
    }

    /// <summary>
    /// 初始化导航栏与表格样式。
    /// </summary>
    public override void ViewDidLoad()
    {
        base.ViewDidLoad();
        Title = "设置";
        NavigationItem.RightBarButtonItem = new UIBarButtonItem(UIBarButtonSystemItem.Close, (_, _) =>
        {
            _ = CloseAsync();
            DismissViewController(true, null);
        });
        // 首页与子页、工具页统一用同一层系统分组背景，sheet 玻璃在各页取到相同材质。
        TableView.BackgroundColor = UIColor.SystemGroupedBackground;
        TableView.CellLayoutMarginsFollowReadableWidth = false;
    }

    /// <summary>页面被系统或代码关闭时兜底提交：覆盖不经关闭代理的程序化 dismiss，保证会话一定释放。</summary>
    /// <param name="animated">系统动画标记。</param>
    public override void ViewDidDisappear(bool animated)
    {
        base.ViewDidDisappear(animated);
        if (IsBeingDismissed || NavigationController?.IsBeingDismissed == true)
            _ = CloseAsync();
    }

    /// <summary>
    /// 返回分组数量。
    /// </summary>
    /// <param name="tableView">当前表格。</param>
    /// <returns>固定 1 个分组。</returns>
    public override nint NumberOfSections(UITableView tableView) => 1;

    /// <summary>
    /// 返回当前分组的行数。
    /// </summary>
    /// <param name="tableView">当前表格。</param>
    /// <param name="section">分组索引。</param>
    /// <returns>一级分类数量。</returns>
    public override nint RowsInSection(UITableView tableView, nint section) => _items.Length;

    /// <summary>
    /// 构建一级分类单元格。
    /// </summary>
    /// <param name="tableView">当前表格。</param>
    /// <param name="indexPath">目标索引。</param>
    /// <returns>配置完成的原生单元格。</returns>
    public override UITableViewCell GetCell(UITableView tableView, NSIndexPath indexPath)
    {
        const string reuseIdentifier = "NativeSettingsRootCell";
        var cell = tableView.DequeueReusableCell(reuseIdentifier)
            ?? new UITableViewCell(UITableViewCellStyle.Subtitle, reuseIdentifier);

        var item = _items[indexPath.Row];
        var configuration = UIListContentConfiguration.SubtitleCellConfiguration;
        configuration.Text = item.Title;
        configuration.SecondaryText = item.Subtitle;
        configuration.SecondaryTextProperties.Color = UIColor.SecondaryLabel;
        cell.ContentConfiguration = configuration;
        cell.Accessory = UITableViewCellAccessory.DisclosureIndicator;
        return cell;
    }

    /// <summary>
    /// 响应一级分类点击并推入对应详情页。
    /// </summary>
    /// <param name="tableView">当前表格。</param>
    /// <param name="indexPath">被点击的索引。</param>
    public override void RowSelected(UITableView tableView, NSIndexPath indexPath)
    {
        tableView.DeselectRow(indexPath, true);
        NavigationController?.PushViewController(_items[indexPath.Row].Factory(), true);
    }
}

/// <summary>
/// 原生设置分组列表基类：直接继承系统 <see cref="UITableViewController"/>（<see cref="UITableViewStyle.InsetGrouped"/>），
/// 由 UIKit 统一承担圆角卡片、系统边距、发丝分隔线、导航栏滚动边缘模糊、底部安全区避让与分组内拖拽排序。
/// </summary>
internal abstract class iOSNativeSettingsFormViewController : UITableViewController
{
    /// <summary>拉条行内拉条的最小宽度（行宽不足时压缩标题而不是压掉拉条）。</summary>
    private const double SliderRowSliderMinWidth = 88;

    /// <summary>拉条行当前值的最小宽度（等宽数字右对齐）。</summary>
    private const double SliderRowValueMinWidth = 64;

    private readonly List<SettingsSection> _sections = [];

    /// <summary>共享设置 ViewModel。</summary>
    protected SettingsViewModel Settings { get; }

    /// <summary>当前页面的分组清单（子类与验收只读访问）。</summary>
    protected List<SettingsSection> Sections => _sections;

    /// <summary>初始化原生分组表单基类。</summary>
    /// <param name="settings">共享设置 ViewModel。</param>
    protected iOSNativeSettingsFormViewController(SettingsViewModel settings)
        : base(UITableViewStyle.InsetGrouped)
    {
        Settings = settings;
    }

    /// <summary>初始化原生分组列表、子页关闭按钮与空白点击收键盘手势。</summary>
    public override void ViewDidLoad()
    {
        base.ViewDidLoad();
        NavigationItem.RightBarButtonItem = new UIBarButtonItem(UIBarButtonSystemItem.Close, (_, _) => DismissSettings());
        TableView.BackgroundColor = UIColor.SystemGroupedBackground;
        TableView.CellLayoutMarginsFollowReadableWidth = false;
        TableView.KeyboardDismissMode = UIScrollViewKeyboardDismissMode.Interactive;
        TableView.RowHeight = UITableView.AutomaticDimension;
        TableView.EstimatedRowHeight = (nfloat)iOSNativeSettingsMetrics.ListRowHeight;
        TableView.SectionHeaderHeight = UITableView.AutomaticDimension;
        TableView.SectionFooterHeight = UITableView.AutomaticDimension;

        var dismissKeyboard = new UITapGestureRecognizer(() => View?.EndEditing(true)) { CancelsTouchesInView = false };
        dismissKeyboard.Delegate = new iOSNonControlTapDelegate();
        TableView.AddGestureRecognizer(dismissKeyboard);

        RebuildContent();
    }

    private void DismissSettings()
    {
        if (NavigationController?.ViewControllers?.FirstOrDefault() is iOSNativeSettingsRootViewController root)
        {
            _ = root.CloseAsync();
        }
        DismissViewController(true, null);
    }

    public override void ViewWillDisappear(bool animated)
    {
        View?.EndEditing(true);
        base.ViewWillDisappear(animated);
    }

    /// <summary>子类构建原生分组列表的各个 section。</summary>
    /// <param name="sections">目标分组集合。</param>
    protected abstract void BuildSections(List<SettingsSection> sections);

    /// <summary>重新构建整页分组；保留用户滚动位置，不把长表单送回顶部。</summary>
    protected void RebuildContent()
    {
        var offset = TableView.ContentOffset;
        bool hadLayout = TableView.Bounds.Height > 0 && TableView.ContentSize.Height > 0;
        _sections.Clear();
        BuildSections(_sections);
        bool hasReorderable = _sections.Any(section => section.CanReorder);
        TableView.SetEditing(hasReorderable, false);
        TableView.AllowsSelectionDuringEditing = true;
        TableView.ReloadData();
        if (!hadLayout) return;

        TableView.LayoutIfNeeded();
        double minY = -(double)TableView.AdjustedContentInset.Top;
        double maxY = Math.Max(
            minY,
            (double)TableView.ContentSize.Height - (double)TableView.Bounds.Height + (double)TableView.AdjustedContentInset.Bottom);
        var clamped = new CGPoint(offset.X, Math.Clamp((double)offset.Y, minY, maxY));
        if (Math.Abs((double)(clamped.Y - TableView.ContentOffset.Y)) > .5)
            TableView.SetContentOffset(clamped, false);
    }

    /// <summary>按当前内容就地重算单元格高度（不调用 ReloadData，不打断输入框焦点）。</summary>
    protected void RefreshRowHeightsInPlace()
    {
        if (ViewIfLoaded?.Window == null) return;
        UIView.PerformWithoutAnimation(() =>
        {
            TableView.BeginUpdates();
            TableView.EndUpdates();
        });
    }

    /// <summary>查找指定分组在表格中的当前 section 索引；未挂入时返回 -1。</summary>
    /// <param name="section">目标分组。</param>
    /// <returns>section 索引或 -1。</returns>
    internal int SectionIndexOf(SettingsSection? section) => section == null ? -1 : _sections.IndexOf(section);

    public override nint NumberOfSections(UITableView tableView) => _sections.Count;

    public override nint RowsInSection(UITableView tableView, nint section)
        => section >= 0 && section < _sections.Count ? _sections[(int)section].RowCount : 0;

    public override string? TitleForHeader(UITableView tableView, nint section)
    {
        if (section < 0 || section >= _sections.Count) return null;
        var title = _sections[(int)section].HeaderTitle;
        return string.IsNullOrWhiteSpace(title) ? null : title;
    }

    public override nfloat EstimatedHeightForHeader(UITableView tableView, nint section)
        => section >= 0 && section < _sections.Count && !string.IsNullOrWhiteSpace(_sections[(int)section].HeaderTitle) ? 32 : 0;

    public override UIView GetViewForFooter(UITableView tableView, nint section)
        => (section >= 0 && section < _sections.Count ? _sections[(int)section].FooterView : null)!;

    public override nfloat EstimatedHeightForFooter(UITableView tableView, nint section)
        => section >= 0 && section < _sections.Count && _sections[(int)section].FooterView != null ? 44 : 0;

    public override UITableViewCell GetCell(UITableView tableView, NSIndexPath indexPath)
        => _sections[(int)indexPath.Section].GetCell(tableView, indexPath);

    public override nfloat GetHeightForRow(UITableView tableView, NSIndexPath indexPath)
        => _sections[(int)indexPath.Section].GetRowHeight(tableView, indexPath);

    public override nfloat EstimatedHeight(UITableView tableView, NSIndexPath indexPath)
        => _sections[(int)indexPath.Section].GetEstimatedRowHeight(tableView, indexPath);

    public override void RowSelected(UITableView tableView, NSIndexPath indexPath)
        => _sections[(int)indexPath.Section].OnRowSelected(tableView, indexPath);

    public override UISwipeActionsConfiguration? GetTrailingSwipeActionsConfiguration(UITableView tableView, NSIndexPath indexPath)
        => indexPath.Section >= 0 && indexPath.Section < _sections.Count
            ? _sections[(int)indexPath.Section].GetTrailingSwipeActions(tableView, indexPath)
            : null;

    public override bool CanEditRow(UITableView tableView, NSIndexPath indexPath)
        => indexPath.Section >= 0 && indexPath.Section < _sections.Count && _sections[(int)indexPath.Section].CanReorder;

    public override bool CanMoveRow(UITableView tableView, NSIndexPath indexPath)
        => indexPath.Section >= 0 && indexPath.Section < _sections.Count && _sections[(int)indexPath.Section].CanReorder;

    public override UITableViewCellEditingStyle EditingStyleForRow(UITableView tableView, NSIndexPath indexPath)
        => UITableViewCellEditingStyle.None;

    public override bool ShouldIndentWhileEditing(UITableView tableView, NSIndexPath indexPath) => false;

    /// <summary>限制拖拽排序只在原分组内移动，不能跨分组拖出。</summary>
    public override NSIndexPath CustomizeMoveTarget(
        UITableView tableView,
        NSIndexPath sourceIndexPath,
        NSIndexPath proposedIndexPath)
    {
        if (proposedIndexPath.Section < sourceIndexPath.Section)
            return NSIndexPath.FromRowSection(0, sourceIndexPath.Section);
        if (proposedIndexPath.Section > sourceIndexPath.Section)
        {
            int lastRow = Math.Max(0, _sections[(int)sourceIndexPath.Section].RowCount - 1);
            return NSIndexPath.FromRowSection(lastRow, sourceIndexPath.Section);
        }
        return proposedIndexPath;
    }

    public override void MoveRow(UITableView tableView, NSIndexPath sourceIndexPath, NSIndexPath destinationIndexPath)
    {
        if (sourceIndexPath.Section != destinationIndexPath.Section) return;
        _sections[(int)sourceIndexPath.Section].OnMoveRow(tableView, (int)sourceIndexPath.Section, sourceIndexPath.Row, destinationIndexPath.Row);
    }

    /// <summary>创建原生静态行分组。</summary>
    /// <param name="title">分组标题；空字符串或 null 时不显示标题。</param>
    /// <param name="footerView">分组底部视图；可为 null。</param>
    /// <param name="rows">分组单元格列表。</param>
    /// <returns>原生静态分组。</returns>
    protected static SettingsSection CreateSection(string? title, UIView? footerView, params UITableViewCell[] rows)
        => new StaticRowsSection(title, rows, footerView);

    /// <summary>创建自定义内容单元格（撑满系统分组卡片，由系统给出圆角与材质）。</summary>
    /// <param name="content">单元格内容视图。</param>
    /// <returns>原生单元格。</returns>
    protected static UITableViewCell CreateCustomCell(UIView content)
    {
        var cell = new UITableViewCell(UITableViewCellStyle.Default, null)
        {
            SelectionStyle = UITableViewCellSelectionStyle.None,
        };
        content.TranslatesAutoresizingMaskIntoConstraints = false;
        cell.ContentView.AddSubview(content);
        NSLayoutConstraint.ActivateConstraints(
        [
            content.TopAnchor.ConstraintEqualTo(cell.ContentView.TopAnchor),
            content.LeadingAnchor.ConstraintEqualTo(cell.ContentView.LeadingAnchor),
            content.TrailingAnchor.ConstraintEqualTo(cell.ContentView.TrailingAnchor),
            content.BottomAnchor.ConstraintEqualTo(cell.ContentView.BottomAnchor),
        ]);
        return cell;
    }

    /// <summary>创建原生开关单元格（系统列表内容配置 + UISwitch 附件）。</summary>
    protected static UITableViewCell CreateSwitchRow(
        string title,
        string? subtitle,
        Func<bool> getter,
        Action<bool> setter,
        Action? onValueChanged = null)
    {
        var cell = new UITableViewCell(UITableViewCellStyle.Subtitle, null)
        {
            SelectionStyle = UITableViewCellSelectionStyle.None,
        };
        var configuration = UIListContentConfiguration.SubtitleCellConfiguration;
        configuration.Text = title;
        configuration.TextProperties.NumberOfLines = 0;
        if (!string.IsNullOrWhiteSpace(subtitle))
        {
            configuration.SecondaryText = subtitle;
            configuration.SecondaryTextProperties.Color = UIColor.SecondaryLabel;
            configuration.SecondaryTextProperties.NumberOfLines = 0;
        }
        cell.ContentConfiguration = configuration;

        var toggle = new UISwitch { On = getter() };
        toggle.ValueChanged += (_, _) =>
        {
            setter(toggle.On);
            onValueChanged?.Invoke();
        };
        cell.AccessoryView = toggle;
        return cell;
    }

    /// <summary>创建原生步进器单元格。</summary>
    protected static UITableViewCell CreateStepperRow(
        string title,
        string? subtitle,
        Func<int> getter,
        Action<int> setter,
        Func<int> minimumProvider,
        Func<int> maximumProvider,
        int step = 1,
        Func<int, string>? valueFormatter = null,
        Action? onValueChanged = null)
    {
        var valueLabel = CreateTrailingValueLabel();
        var stepper = new UIStepper { StepValue = step };
        stepper.SetContentHuggingPriority((float)UILayoutPriority.Required, UILayoutConstraintAxis.Horizontal);

        void Refresh()
        {
            var minimum = minimumProvider();
            var maximum = Math.Max(minimum, maximumProvider());
            var value = Math.Clamp(getter(), minimum, maximum);

            stepper.MinimumValue = minimum;
            stepper.MaximumValue = maximum;
            stepper.Value = value;
            valueLabel.Text = valueFormatter?.Invoke(value) ?? value.ToString(CultureInfo.InvariantCulture);
        }

        stepper.ValueChanged += (_, _) =>
        {
            var minimum = minimumProvider();
            var maximum = Math.Max(minimum, maximumProvider());
            var newValue = Math.Clamp((int)Math.Round(stepper.Value), minimum, maximum);
            setter(newValue);
            Refresh();
            onValueChanged?.Invoke();
        };

        Refresh();

        var accessoryStack = new UIStackView
        {
            Axis = UILayoutConstraintAxis.Horizontal,
            Alignment = UIStackViewAlignment.Center,
            Spacing = 10,
            TranslatesAutoresizingMaskIntoConstraints = false,
        };
        accessoryStack.AddArrangedSubview(valueLabel);
        accessoryStack.AddArrangedSubview(stepper);

        return CreateAccessoryRow(title, subtitle, accessoryStack);
    }

    /// <summary>
    /// 创建原生滑条单元格：标题 → 弹性拉条 → 固定最小宽右对齐当前值，三者同行（SETTINGS-16）。
    /// 行宽不足时标题先换行收缩，拉条保有自己的最小宽度，不与数值叠压。
    /// </summary>
    protected static UITableViewCell CreateSliderRow(
        string title,
        string? subtitle,
        Func<int> getter,
        Action<int> setter,
        int minimum,
        int maximum,
        int step = 0,
        Func<int, string>? valueFormatter = null,
        Action? onValueChanged = null,
        Action? onEditingCompleted = null)
    {
        var cell = CreateRowCell();
        var contentStack = new UIStackView
        {
            Axis = UILayoutConstraintAxis.Horizontal,
            Alignment = UIStackViewAlignment.Center,
            Spacing = 12,
            TranslatesAutoresizingMaskIntoConstraints = false,
        };

        var textStack = CreateTextStack(title, subtitle);
        textStack.SetContentCompressionResistancePriority(
            (float)UILayoutPriority.DefaultLow, UILayoutConstraintAxis.Horizontal);

        var valueLabel = CreateTrailingValueLabel();
        valueLabel.TextAlignment = UITextAlignment.Right;
        valueLabel.Lines = 1;
        valueLabel.SetContentHuggingPriority((float)UILayoutPriority.DefaultHigh, UILayoutConstraintAxis.Horizontal);
        valueLabel.SetContentCompressionResistancePriority(
            (float)UILayoutPriority.DefaultHigh + 1, UILayoutConstraintAxis.Horizontal);

        var slider = new UISlider
        {
            MinValue = minimum,
            MaxValue = maximum,
            Continuous = true,
        };
        slider.SetContentHuggingPriority((float)UILayoutPriority.DefaultLow - 1, UILayoutConstraintAxis.Horizontal);
        slider.SetContentCompressionResistancePriority(
            (float)UILayoutPriority.DefaultLow - 1, UILayoutConstraintAxis.Horizontal);

        contentStack.AddArrangedSubview(textStack);
        contentStack.AddArrangedSubview(slider);
        contentStack.AddArrangedSubview(valueLabel);

        var sliderMinimum = slider.WidthAnchor.ConstraintGreaterThanOrEqualTo((nfloat)SliderRowSliderMinWidth);
        var valueMinimum = valueLabel.WidthAnchor.ConstraintGreaterThanOrEqualTo((nfloat)SliderRowValueMinWidth);
        sliderMinimum.Priority = (float)UILayoutPriority.DefaultHigh;
        valueMinimum.Priority = (float)UILayoutPriority.DefaultHigh;
        NSLayoutConstraint.ActivateConstraints([sliderMinimum, valueMinimum]);

        int Snap(double raw)
        {
            var value = Math.Clamp((int)Math.Round(raw), minimum, maximum);
            if (step <= 0) return value;

            var steps = (int)Math.Round((value - minimum) / (double)step, MidpointRounding.AwayFromZero);
            return Math.Clamp(minimum + (steps * step), minimum, maximum);
        }

        void Refresh()
        {
            var value = Snap(getter());
            slider.Value = value;
            valueLabel.Text = valueFormatter?.Invoke(value) ?? value.ToString(CultureInfo.InvariantCulture);
        }

        slider.ValueChanged += (_, _) =>
        {
            setter(Snap(slider.Value));
            Refresh();
            onValueChanged?.Invoke();
        };

        slider.TouchUpInside += (_, _) => onEditingCompleted?.Invoke();
        slider.TouchUpOutside += (_, _) => onEditingCompleted?.Invoke();
        slider.TouchCancel += (_, _) => onEditingCompleted?.Invoke();

        Refresh();

        cell.ContentView.AddSubview(contentStack);
        NSLayoutConstraint.ActivateConstraints(
        [
            contentStack.TopAnchor.ConstraintEqualTo(cell.ContentView.LayoutMarginsGuide.TopAnchor),
            contentStack.LeadingAnchor.ConstraintEqualTo(cell.ContentView.LayoutMarginsGuide.LeadingAnchor),
            contentStack.TrailingAnchor.ConstraintEqualTo(cell.ContentView.LayoutMarginsGuide.TrailingAnchor),
            contentStack.BottomAnchor.ConstraintEqualTo(cell.ContentView.LayoutMarginsGuide.BottomAnchor),
        ]);

        return cell;
    }

    /// <summary>验收：用同一构建器生成一条滑条单元格（不挂到页面），用于核对同行几何与间距。</summary>
    internal UITableViewCell CreateSliderRowForChecks(string title, string valueText)
        => CreateSliderRow(title, null, () => 85, _ => { }, 75, 95, valueFormatter: _ => valueText);

    private static UITableViewCell CreateAccessoryRow(string title, string? subtitle, UIView? accessoryView)
    {
        var cell = CreateRowCell();
        var textStack = CreateTextStack(title, subtitle);
        cell.ContentView.AddSubview(textStack);

        var minHeight = cell.ContentView.HeightAnchor.ConstraintGreaterThanOrEqualTo(44);
        minHeight.Priority = 999;

        var constraints = new List<NSLayoutConstraint>
        {
            minHeight,
            textStack.LeadingAnchor.ConstraintEqualTo(cell.ContentView.LayoutMarginsGuide.LeadingAnchor),
            textStack.TopAnchor.ConstraintGreaterThanOrEqualTo(cell.ContentView.LayoutMarginsGuide.TopAnchor),
            textStack.BottomAnchor.ConstraintLessThanOrEqualTo(cell.ContentView.LayoutMarginsGuide.BottomAnchor),
            textStack.CenterYAnchor.ConstraintEqualTo(cell.ContentView.CenterYAnchor),
        };

        if (accessoryView != null)
        {
            accessoryView.TranslatesAutoresizingMaskIntoConstraints = false;
            cell.ContentView.AddSubview(accessoryView);
            constraints.Add(textStack.TrailingAnchor.ConstraintLessThanOrEqualTo(accessoryView.LeadingAnchor, -12));
            constraints.Add(accessoryView.TrailingAnchor.ConstraintEqualTo(cell.ContentView.LayoutMarginsGuide.TrailingAnchor));
            constraints.Add(accessoryView.TopAnchor.ConstraintGreaterThanOrEqualTo(cell.ContentView.LayoutMarginsGuide.TopAnchor));
            constraints.Add(accessoryView.BottomAnchor.ConstraintLessThanOrEqualTo(cell.ContentView.LayoutMarginsGuide.BottomAnchor));
            constraints.Add(accessoryView.CenterYAnchor.ConstraintEqualTo(cell.ContentView.CenterYAnchor));
        }
        else
        {
            constraints.Add(textStack.TrailingAnchor.ConstraintEqualTo(cell.ContentView.LayoutMarginsGuide.TrailingAnchor));
        }

        NSLayoutConstraint.ActivateConstraints(constraints.ToArray());
        return cell;
    }

    protected static UITableViewCell CreateRowCell()
    {
        var cell = new UITableViewCell(UITableViewCellStyle.Default, null)
        {
            SelectionStyle = UITableViewCellSelectionStyle.None,
        };
        cell.ContentView.DirectionalLayoutMargins = new NSDirectionalEdgeInsets(10, 16, 10, 16);
        return cell;
    }

    private static UIStackView CreateTextStack(string title, string? subtitle)
    {
        var stack = new UIStackView
        {
            Axis = UILayoutConstraintAxis.Vertical,
            Spacing = 4,
            TranslatesAutoresizingMaskIntoConstraints = false,
        };
        stack.AddArrangedSubview(new UILabel
        {
            Text = title,
            Font = UIFont.PreferredBody!,
            AdjustsFontForContentSizeCategory = true,
            TextColor = UIColor.Label,
            Lines = 0,
            TranslatesAutoresizingMaskIntoConstraints = false,
        });

        if (!string.IsNullOrWhiteSpace(subtitle))
        {
            stack.AddArrangedSubview(new UILabel
            {
                Text = subtitle,
                Font = UIFont.PreferredFootnote!,
                AdjustsFontForContentSizeCategory = true,
                TextColor = UIColor.SecondaryLabel,
                Lines = 0,
                TranslatesAutoresizingMaskIntoConstraints = false,
            });
        }

        return stack;
    }

    protected static UILabel CreateTrailingValueLabel() => new()
    {
        Font = UIFontMetrics.DefaultMetrics.GetScaledFont(
            UIFont.MonospacedDigitSystemFontOfSize(14, UIFontWeight.Medium)!),
        AdjustsFontForContentSizeCategory = true,
        TextColor = UIColor.SecondaryLabel,
        Lines = 1,
        TranslatesAutoresizingMaskIntoConstraints = false,
    };

    /// <summary>原生分组表内的单个 section 契约。</summary>
    internal abstract class SettingsSection
    {
        private readonly UIView? _footerView;

        internal string? HeaderTitle { get; }
        internal virtual UIView? FooterView => _footerView;
        internal virtual bool CanReorder => false;
        internal abstract int RowCount { get; }

        protected SettingsSection(string? headerTitle, UIView? footerView = null)
        {
            HeaderTitle = headerTitle;
            _footerView = footerView;
        }

        internal abstract UITableViewCell GetCell(UITableView tableView, NSIndexPath indexPath);
        internal virtual nfloat GetRowHeight(UITableView tableView, NSIndexPath indexPath) => UITableView.AutomaticDimension;
        internal virtual nfloat GetEstimatedRowHeight(UITableView tableView, NSIndexPath indexPath)
            => (nfloat)iOSNativeSettingsMetrics.ListRowHeight;
        internal virtual void OnRowSelected(UITableView tableView, NSIndexPath indexPath) { }
        internal virtual void OnMoveRow(UITableView tableView, int sectionIndex, int sourceRow, int destinationRow) { }
        internal virtual UISwipeActionsConfiguration? GetTrailingSwipeActions(UITableView tableView, NSIndexPath indexPath) => null;
    }

    /// <summary>静态单元格分组（开关、步进器、拉条、弹出按钮、预设编辑器等）。</summary>
    internal sealed class StaticRowsSection(string? headerTitle, IReadOnlyList<UITableViewCell> rows, UIView? footerView = null)
        : SettingsSection(headerTitle, footerView)
    {
        internal IReadOnlyList<UITableViewCell> Rows => rows;
        internal override int RowCount => rows.Count;
        internal override UITableViewCell GetCell(UITableView tableView, NSIndexPath indexPath) => rows[indexPath.Row];
    }

    /// <summary>布局模式单选分组：原生副标题单元格 + 系统勾选附件。</summary>
    internal sealed class LayoutModeSection : SettingsSection
    {
        private readonly SettingsViewModel _settings;
        private readonly bool _phoneOnly;
        private readonly SettingsViewModel.LayoutModeItem[] _items;

        internal LayoutModeSection(string headerTitle, SettingsViewModel settings, bool phoneOnly)
            : base(headerTitle)
        {
            _settings = settings;
            _phoneOnly = phoneOnly;
            _items = settings.LayoutModes.Where(item => !phoneOnly || item.Value == LayoutMode.Phone).ToArray();
        }

        internal override int RowCount => _items.Length;

        internal override UITableViewCell GetCell(UITableView tableView, NSIndexPath indexPath)
        {
            const string reuseIdentifier = "SettingsLayoutModeCell";
            var cell = tableView.DequeueReusableCell(reuseIdentifier)
                ?? new UITableViewCell(UITableViewCellStyle.Subtitle, reuseIdentifier);
            var item = _items[indexPath.Row];
            var configuration = UIListContentConfiguration.SubtitleCellConfiguration;
            configuration.Text = item.DisplayName;
            configuration.SecondaryText = item.Description;
            configuration.SecondaryTextProperties.NumberOfLines = 0;
            configuration.SecondaryTextProperties.Color = UIColor.SecondaryLabel;
            cell.ContentConfiguration = configuration;
            cell.Accessory = _phoneOnly || item.Value == _settings.LayoutMode
                ? UITableViewCellAccessory.Checkmark
                : UITableViewCellAccessory.None;
            return cell;
        }

        internal override void OnRowSelected(UITableView tableView, NSIndexPath indexPath)
        {
            tableView.DeselectRow(indexPath, true);
            _settings.LayoutMode = _items[indexPath.Row].Value;
            // 同步当前分组内勾选态（若触发 Wide ↔ Phone 切换，控制页的 OnLayoutModeChanged 会重建分组）。
            for (int row = 0; row < _items.Length; row++)
            {
                if (tableView.CellAt(NSIndexPath.FromRowSection(row, indexPath.Section)) is { } cell)
                {
                    cell.Accessory = _phoneOnly || _items[row].Value == _settings.LayoutMode
                        ? UITableViewCellAccessory.Checkmark
                        : UITableViewCellAccessory.None;
                }
            }
        }
    }

    /// <summary>可勾选、可选拖拽排序的原生分组（文件格式 / 控制栏功能 / 上一张下一张 / EXIF 显示）。</summary>
    internal sealed class CheckableListSection<TItem> : SettingsSection
    {
        private readonly Func<IReadOnlyList<TItem>> _itemsProvider;
        private readonly Func<TItem, string> _titleSelector;
        private readonly Func<TItem, string?> _subtitleSelector;
        private readonly Func<TItem, bool> _isEnabledGetter;
        private readonly Action<TItem, bool> _isEnabledSetter;
        private readonly Action<int, int> _moveAction;
        private readonly double _rowHeight;
        private readonly bool _showsSubtitle;
        private readonly bool _showsReorderControls;
        private readonly Func<TItem, UIImage?>? _leadingIconSelector;
        private readonly Func<TItem, UIView?>? _trailingAccessorySelector;
        private readonly Func<TItem, bool>? _isCheckmarkActiveSelector;
        private readonly Func<TItem, string?>? _accessibilityValueSelector;
        private readonly Action? _onChanged;

        internal CheckableListSection(
            string? headerTitle,
            Func<IReadOnlyList<TItem>> itemsProvider,
            Func<TItem, string> titleSelector,
            Func<TItem, string?> subtitleSelector,
            Func<TItem, bool> isEnabledGetter,
            Action<TItem, bool> isEnabledSetter,
            Action<int, int> moveAction,
            double rowHeight = 60,
            bool showsSubtitle = true,
            bool showsReorderControls = true,
            Func<TItem, UIImage?>? leadingIconSelector = null,
            Func<TItem, UIView?>? trailingAccessorySelector = null,
            Func<TItem, bool>? isCheckmarkActiveSelector = null,
            Func<TItem, string?>? accessibilityValueSelector = null,
            Action? onChanged = null)
            : base(headerTitle)
        {
            _itemsProvider = itemsProvider;
            _titleSelector = titleSelector;
            _subtitleSelector = subtitleSelector;
            _isEnabledGetter = isEnabledGetter;
            _isEnabledSetter = isEnabledSetter;
            _moveAction = moveAction;
            _rowHeight = rowHeight;
            _showsSubtitle = showsSubtitle;
            _showsReorderControls = showsReorderControls;
            _leadingIconSelector = leadingIconSelector;
            _trailingAccessorySelector = trailingAccessorySelector;
            _isCheckmarkActiveSelector = isCheckmarkActiveSelector;
            _accessibilityValueSelector = accessibilityValueSelector;
            _onChanged = onChanged;
        }

        internal override bool CanReorder => _showsReorderControls;
        internal override int RowCount => _itemsProvider().Count;

        internal override nfloat GetRowHeight(UITableView tableView, NSIndexPath indexPath) => UITableView.AutomaticDimension;

        internal override nfloat GetEstimatedRowHeight(UITableView tableView, NSIndexPath indexPath) => (nfloat)_rowHeight;

        internal override UITableViewCell GetCell(UITableView tableView, NSIndexPath indexPath)
        {
            var reuseIdentifier = _showsSubtitle
                ? "SettingsCheckableSubtitleCell"
                : "SettingsCheckableDefaultCell";
            var cell = tableView.DequeueReusableCell(reuseIdentifier) as iOSNativeCheckableSettingsCell
                ?? new iOSNativeCheckableSettingsCell(reuseIdentifier, _showsSubtitle, _rowHeight);
            ConfigureCell(tableView, cell, (int)indexPath.Section, (int)indexPath.Row);
            return cell;
        }

        internal void ConfigureCell(UITableView tableView, iOSNativeCheckableSettingsCell cell, int sectionIndex, int row)
        {
            var items = _itemsProvider();
            if (row < 0 || row >= items.Count) return;
            var item = items[row];
            cell.Configure(
                title: _titleSelector(item),
                subtitle: _showsSubtitle ? _subtitleSelector(item) : null,
                isEnabled: _isEnabledGetter(item),
                accessibilityValue: _accessibilityValueSelector?.Invoke(item),
                icon: _leadingIconSelector?.Invoke(item),
                checkmarkActive: _isCheckmarkActiveSelector?.Invoke(item) ?? true);
            cell.ShowsReorderControl = _showsReorderControls;
            cell.SetTrailingAccessory(_trailingAccessorySelector?.Invoke(item));
            cell.AccessibilityCustomActions = BuildReorderAccessibilityActions(tableView, sectionIndex, row, items.Count);
        }

        private UIAccessibilityCustomAction[]? BuildReorderAccessibilityActions(
            UITableView tableView, int sectionIndex, int row, int count)
        {
            if (!_showsReorderControls || count <= 1 || sectionIndex < 0) return null;
            var actions = new List<UIAccessibilityCustomAction>(2);
            if (row > 0)
            {
                actions.Add(new UIAccessibilityCustomAction("上移", (UIAccessibilityCustomActionHandler)(_ =>
                {
                    PerformMove(tableView, sectionIndex, row, row - 1);
                    return true;
                })));
            }
            if (row + 1 < count)
            {
                actions.Add(new UIAccessibilityCustomAction("下移", (UIAccessibilityCustomActionHandler)(_ =>
                {
                    PerformMove(tableView, sectionIndex, row, row + 1);
                    return true;
                })));
            }
            return actions.Count > 0 ? actions.ToArray() : null;
        }

        private void PerformMove(UITableView tableView, int sectionIndex, int sourceRow, int destinationRow)
        {
            _moveAction(sourceRow, destinationRow);
            tableView.BeginUpdates();
            tableView.MoveRow(
                NSIndexPath.FromRowSection(sourceRow, sectionIndex),
                NSIndexPath.FromRowSection(destinationRow, sectionIndex));
            tableView.EndUpdates();
            RefreshInPlace(tableView, sectionIndex);
            _onChanged?.Invoke();
        }

        /// <summary>就地刷新当前分组可见行的内容与附件（不重建单元格、不重置滚动位置）。</summary>
        internal void RefreshInPlace(UITableView tableView, int sectionIndex)
        {
            if (sectionIndex < 0) return;
            foreach (var cell in tableView.VisibleCells)
            {
                if (tableView.IndexPathForCell(cell) is { } indexPath
                    && indexPath.Section == sectionIndex
                    && cell is iOSNativeCheckableSettingsCell checkable)
                {
                    ConfigureCell(tableView, checkable, sectionIndex, (int)indexPath.Row);
                }
            }
        }

        internal override void OnRowSelected(UITableView tableView, NSIndexPath indexPath)
        {
            tableView.DeselectRow(indexPath, true);
            var item = _itemsProvider()[indexPath.Row];
            _isEnabledSetter(item, !_isEnabledGetter(item));
            RefreshInPlace(tableView, indexPath.Section);
            _onChanged?.Invoke();
        }

        internal override void OnMoveRow(UITableView tableView, int sectionIndex, int sourceRow, int destinationRow)
        {
            _moveAction(sourceRow, destinationRow);
            RefreshInPlace(tableView, sectionIndex);
            _onChanged?.Invoke();
        }
    }
}

/// <summary>
/// iOS 原生文件设置页。
/// </summary>
internal sealed class iOSNativeFileSettingsViewController : iOSNativeSettingsFormViewController
{
    private UILabel? _performanceInfo;
    private UILabel? _cacheInfo;
    private bool _infoRefreshHooked;
    private SettingsSection? _fileFormatSection;

    /// <summary>文件格式分组的当前 section 索引（验收定位真实分组）。</summary>
    internal int FileFormatSectionIndex => SectionIndexOf(_fileFormatSection);

    public iOSNativeFileSettingsViewController(SettingsViewModel settings)
        : base(settings)
    {
    }

    protected override void BuildSections(List<SettingsSection> sections)
    {
        Title = "文件";

        var performanceInfo = CreateInfoLabel();
        var cacheInfo = CreateInfoLabel();
        _performanceInfo = performanceInfo;
        _cacheInfo = cacheInfo;
        HookInfoRefresh();

        sections.Add(CreateSection(
            null,
            null,
            CreateSwitchRow(
                "同名文件视为同一张图片",
                "按列表排序优先级高的作为代表显示图片，修改星级时同步至所有同名文件",
                () => Settings.SameNameAsOnePhoto,
                value => Settings.SameNameAsOnePhoto = value)));

        _fileFormatSection = new CheckableListSection<SettingsViewModel.FileFormatItem>(
            headerTitle: "支持的文件格式",
            itemsProvider: () => Settings.FileFormats,
            titleSelector: item => item.DisplayName,
            subtitleSelector: item => item.ExtensionsText,
            isEnabledGetter: item => item.IsEnabled,
            isEnabledSetter: (item, value) => item.IsEnabled = value,
            moveAction: Settings.MoveFileFormat,
            rowHeight: 60,
            showsSubtitle: true,
            showsReorderControls: true);
        sections.Add(_fileFormatSection);

        // 缓存容量由实测预算自动管理的平台不显示用户上限两项，其余行保持
        var cacheRows = new List<UITableViewCell>
        {
            CreateStepperRow(
                "原生解码线程",
                null,
                () => Settings.NativePreloadParallelism,
                value => Settings.NativePreloadParallelism = value,
                () => 1,
                () => Settings.NativePreloadParallelismMaximum,
                valueFormatter: value => $"{value} 线程"),
            CreateStepperRow(
                "软件解码线程",
                null,
                () => Settings.CpuPreloadParallelism,
                value => Settings.CpuPreloadParallelism = value,
                () => 1,
                () => Settings.CpuPreloadParallelismMaximum,
                valueFormatter: value => $"{value} 线程"),
        };
        if (Settings.ShowsCacheLimits)
        {
            cacheRows.Add(CreateSliderRow(
                "缓存内存上限",
                null,
                () => Settings.BitmapCacheMaxMemory,
                value => Settings.BitmapCacheMaxMemory = value,
                512,
                32768,
                valueFormatter: value => $"{value} MB",
                onValueChanged: RefreshInfoLabels,
                onEditingCompleted: RebuildContent));
            cacheRows.Add(CreateSliderRow(
                "缓存数量上限",
                null,
                () => Settings.BitmapCacheMaxCount,
                value => Settings.BitmapCacheMaxCount = value,
                1,
                400,
                valueFormatter: value => $"{value} 张",
                onValueChanged: RefreshInfoLabels,
                onEditingCompleted: RebuildContent));
        }
        cacheRows.Add(CreateStepperRow(
            "下一张预载数量",
            null,
            () => Settings.PreloadForwardCount,
            value => Settings.PreloadForwardCount = value,
            () => 0,
            () => Settings.PreloadMaximum,
            valueFormatter: value => $"{value} 张"));
        cacheRows.Add(CreateStepperRow(
            "上一张预载数量",
            null,
            () => Settings.PreloadBackwardCount,
            value => Settings.PreloadBackwardCount = value,
            () => 0,
            () => Settings.PreloadMaximum,
            valueFormatter: value => $"{value} 张"));
        cacheRows.Add(CreateStepperRow(
            "列表滚动预载数量",
            null,
            () => Settings.VisibleCenterPreloadCount,
            value => Settings.VisibleCenterPreloadCount = value,
            () => 0,
            () => Settings.PreloadMaximum,
            valueFormatter: value => $"{value} 张"));
        cacheRows.Add(CreateSliderRow(
            "滚动预载延时",
            null,
            () => Settings.VisibleCenterDelayMs,
            value => Settings.VisibleCenterDelayMs = value,
            SettingsDocumentCodec.MinimumVisibleCenterDelayMs,
            SettingsDocumentCodec.MaximumVisibleCenterDelayMs,
            SettingsDocumentCodec.VisibleCenterDelayStepMs,
            valueFormatter: value => $"{value} ms"));

        var footer = CreateCacheFooterView(performanceInfo, cacheInfo);
        sections.Add(CreateSection("缓存与预加载", footer, cacheRows.ToArray()));
        RefreshInfoLabels();
    }

    /// <summary>分组脚注容器：用原生 <see cref="UITableViewHeaderFooterView"/> 承接系统分组左右边距。</summary>
    private static UITableViewHeaderFooterView CreateCacheFooterView(UILabel performanceInfo, UILabel cacheInfo)
    {
        var footer = new UITableViewHeaderFooterView();
        var stack = new UIStackView([performanceInfo, cacheInfo])
        {
            Axis = UILayoutConstraintAxis.Vertical,
            Spacing = 6,
            TranslatesAutoresizingMaskIntoConstraints = false,
        };
        footer.ContentView.AddSubview(stack);
        NSLayoutConstraint.ActivateConstraints(
        [
            stack.TopAnchor.ConstraintEqualTo(footer.ContentView.TopAnchor, 10),
            stack.LeadingAnchor.ConstraintEqualTo(footer.ContentView.LayoutMarginsGuide.LeadingAnchor),
            stack.TrailingAnchor.ConstraintEqualTo(footer.ContentView.LayoutMarginsGuide.TrailingAnchor),
            stack.BottomAnchor.ConstraintEqualTo(footer.ContentView.BottomAnchor, -20),
        ]);
        return footer;
    }

    /// <summary>能力提示行：多行文本整段换行，不截断，支持动态字体。</summary>
    private static UILabel CreateInfoLabel() => new()
    {
        Font = UIFont.PreferredFootnote!,
        AdjustsFontForContentSizeCategory = true,
        TextColor = UIColor.SecondaryLabel,
        Lines = 0,
        LineBreakMode = UILineBreakMode.WordWrap,
        TranslatesAutoresizingMaskIntoConstraints = false,
    };

    private void HookInfoRefresh()
    {
        if (_infoRefreshHooked) return;
        _infoRefreshHooked = true;
        Settings.PropertyChanged += OnSettingsPropertyChanged;
    }

    private void OnSettingsPropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName is not (nameof(SettingsViewModel.PerformanceBudgetInfo)
            or nameof(SettingsViewModel.PerformanceBudgetInfoLevel)
            or nameof(SettingsViewModel.BitmapCacheCountInfo)
            or nameof(SettingsViewModel.BitmapCacheCountInfoLevel))) return;
        if (NSThread.IsMain) RefreshInfoLabels();
        else BeginInvokeOnMainThread(RefreshInfoLabels);
    }

    private void RefreshInfoLabels()
    {
        bool heightChanged = false;
        string budgetText = Settings.PerformanceBudgetInfo;
        var budgetColor = InfoColor(Settings.PerformanceBudgetInfoLevel);
        if (_performanceInfo is { } performance && !SameInfo(performance, budgetText, budgetColor))
        {
            double before = (double)performance.Bounds.Height;
            performance.Text = budgetText;
            performance.TextColor = budgetColor;
            if (performance.Bounds.Width > 1)
            {
                double after = (double)performance.SizeThatFits(new CGSize(performance.Bounds.Width, nfloat.MaxValue)).Height;
                heightChanged |= Math.Abs(after - before) > 1;
            }
        }

        string countText = Settings.BitmapCacheCountInfo;
        var countColor = InfoColor(Settings.BitmapCacheCountInfoLevel);
        if (_cacheInfo is { } cache && !SameInfo(cache, countText, countColor))
        {
            double before = (double)cache.Bounds.Height;
            cache.Text = countText;
            cache.TextColor = countColor;
            if (cache.Bounds.Width > 1)
            {
                double after = (double)cache.SizeThatFits(new CGSize(cache.Bounds.Width, nfloat.MaxValue)).Height;
                heightChanged |= Math.Abs(after - before) > 1;
            }
        }

        if (heightChanged)
        {
            RefreshRowHeightsInPlace();
        }
    }

    private static bool SameInfo(UILabel label, string text, UIColor color)
    {
        var current = label.TextColor;
        return label.Text == text && current != null && (ReferenceEquals(current, color) || current.Equals(color));
    }

    private static UIColor InfoColor(PerfInfoLevel level) => level switch
    {
        PerfInfoLevel.Error => UIColor.SystemRed,
        PerfInfoLevel.Warning => PhotoViewer.iOS.Native.iOSColors.FromToken(
            PhotoViewer.Core.Design.DesignTokens.ColorSettingsWarning),
        _ => UIColor.SecondaryLabel,
    };

    protected override void Dispose(bool disposing)
    {
        if (disposing && _infoRefreshHooked)
        {
            _infoRefreshHooked = false;
            Settings.PropertyChanged -= OnSettingsPropertyChanged;
        }

        base.Dispose(disposing);
    }
}

/// <summary>
/// iOS 原生预览设置页。
/// </summary>
internal sealed class iOSNativePreviewSettingsViewController : iOSNativeSettingsFormViewController
{
    private iOSScalePresetSection? _presetSection;

    /// <summary>缩放比例预设原生分组（验收访问字段与删增操作）。</summary>
    internal iOSScalePresetSection? PresetSection => _presetSection;

    /// <summary>缩放比例预设分组的当前 section 索引。</summary>
    internal int PresetSectionIndex => SectionIndexOf(_presetSection);

    public iOSNativePreviewSettingsViewController(SettingsViewModel settings)
        : base(settings)
    {
    }

    protected override void BuildSections(List<SettingsSection> sections)
    {
        Title = "预览";

        sections.Add(CreateSection(
            null,
            null,
            CreateSwitchRow(
                "缩放时显示指示器",
                null,
                () => Settings.ShowZoomIndicator,
                value => Settings.ShowZoomIndicator = value)));

        _presetSection = new iOSScalePresetSection("缩放比例预设", Settings, this);
        sections.Add(_presetSection);
    }
}

/// <summary>
/// iOS 原生 EXIF 设置页。
/// </summary>
internal sealed class iOSNativeExifSettingsViewController : iOSNativeSettingsFormViewController
{
    private SettingsSection? _exifDisplaySection;

    /// <summary>EXIF 显示分组的当前 section 索引（验收定位真实分组）。</summary>
    internal int ExifDisplaySectionIndex => SectionIndexOf(_exifDisplaySection);

    public iOSNativeExifSettingsViewController(SettingsViewModel settings)
        : base(settings)
    {
    }

    protected override void BuildSections(List<SettingsSection> sections)
    {
        Title = "EXIF";

        _exifDisplaySection = new CheckableListSection<SettingsViewModel.ExifDisplayItem>(
            headerTitle: "EXIF 显示/收纳",
            itemsProvider: () => Settings.ExifDisplayItems,
            titleSelector: item => item.DisplayName,
            subtitleSelector: _ => null,
            isEnabledGetter: item => item.IsEnabled,
            isEnabledSetter: (item, value) => item.IsEnabled = value,
            moveAction: Settings.MoveExifDisplay,
            rowHeight: iOSNativeSettingsMetrics.ListRowHeight,
            showsSubtitle: false,
            showsReorderControls: true);
        sections.Add(_exifDisplaySection);

        sections.Add(CreateSection(
            "评分与写回",
            null,
            CreateSwitchRow(
                "显示星级评分",
                null,
                () => Settings.ShowRating,
                value => Settings.ShowRating = value),
            CreateSwitchRow(
                "安全修改文件星级",
                "检验文件字节变化再写回，降低损坏原文件概率",
                () => Settings.SafeSetRating,
                value => Settings.SafeSetRating = value)));
    }
}

/// <summary>
/// iOS 原生控制设置页：布局模式、控制栏功能（上下张开关与命令表两张独立原生 InsetGrouped 卡片）与键盘映射。
/// 宽形态命令行与 Mac 单行布局一致；手机形态的命令表同样完整保留勾选与排序，
/// 不提供绑定编辑或键盘映射。宽形态连接键盘或鼠标后就地出现主/次绑定按钮与键盘映射分组。
/// </summary>
internal sealed class iOSNativeControlSettingsViewController : iOSNativeSettingsFormViewController
{
    private readonly iOSInputCapabilities _capabilities = new();
    private readonly iOSHotkeyRecorder _recorder;
    private readonly Dictionary<SettingsViewModel.HotkeyItem, iOSHotkeyBindingButtons> _bindingButtons = [];
    internal const string NavigationIconSymbol = "chevron.left.chevron.right";

    private LayoutModeSection? _layoutModeSection;
    private CheckableListSection<string>? _navigationSection;
    private CheckableListSection<SettingsViewModel.HotkeyItem>? _hotkeySection;
    private StaticRowsSection? _mappingSection;
    private bool _phoneForm;
    private bool _layoutModeHooked;

    public iOSNativeControlSettingsViewController(SettingsViewModel settings) : base(settings)
    {
        _recorder = new iOSHotkeyRecorder(settings, _capabilities);
        _recorder.Changed += RefreshHotkeyRows;
        _capabilities.Changed += RefreshCapabilities;
    }

    /// <summary>外设能力（验收注入）。</summary>
    internal iOSInputCapabilities Capabilities => _capabilities;

    /// <summary>快捷键录制器（行内按钮与验收共用）。</summary>
    internal iOSHotkeyRecorder Recorder => _recorder;

    /// <summary>布局模式分组索引（验收定位真实分组）。</summary>
    internal int LayoutModeSectionIndex => SectionIndexOf(_layoutModeSection);

    /// <summary>上一张/下一张开关分组索引（验收定位真实分组）。</summary>
    internal int NavigationSectionIndex => SectionIndexOf(_navigationSection);

    /// <summary>控制栏命令表分组索引（验收定位真实分组）。</summary>
    internal int HotkeySectionIndex => SectionIndexOf(_hotkeySection);

    /// <summary>键盘映射分组索引；不可见或手机形态时为 -1。</summary>
    internal int KeyboardMappingSectionIndex => SectionIndexOf(_mappingSection);

    /// <summary>键盘映射分组的静态行（外设连接且宽形态时存在；验收核对弹出按钮）。</summary>
    internal IReadOnlyList<UITableViewCell>? KeyboardMappingRows
        => KeyboardMappingSectionIndex >= 0 ? _mappingSection?.Rows : null;

    /// <summary>设备本身是手机（iPhone）：形态固定为手机，布局选择只列「手机」。</summary>
    internal static bool IsPhoneDevice => UIDevice.CurrentDevice.UserInterfaceIdiom == UIUserInterfaceIdiom.Phone;

    /// <summary>本页按手机形态排版：iPhone，或工作副本选了「手机」布局（SETTINGS-08）。</summary>
    internal bool PhoneForm => IsPhoneDevice || Settings.LayoutMode == LayoutMode.Phone;

    protected override void BuildSections(List<SettingsSection> sections)
    {
        Title = "控制";
        _phoneForm = PhoneForm;
        _layoutModeSection = new LayoutModeSection("边栏布局位置", Settings, IsPhoneDevice);
        sections.Add(_layoutModeSection);

        _navigationSection = new CheckableListSection<string>(
            headerTitle: "控制栏功能",
            itemsProvider: () => ["上一张/下一张"],
            titleSelector: item => item,
            subtitleSelector: _ => null,
            isEnabledGetter: _ => Settings.ShowNavigationButtons,
            isEnabledSetter: (_, value) => Settings.ShowNavigationButtons = value,
            moveAction: (_, _) => { },
            rowHeight: iOSNativeSettingsMetrics.ListRowHeight,
            showsSubtitle: false,
            showsReorderControls: false,
            leadingIconSelector: _ => SymbolIcon(NavigationIconSymbol),
            onChanged: () =>
            {
                // 组合开关决定独立上下张是否进入可选命令组：勾号的生效态立刻重算。
                if (_phoneForm) _hotkeySection?.RefreshInPlace(TableView, HotkeySectionIndex);
            });

        // SETTINGS-08：上下张组合开关与可勾选排序的命令表各占一张原生 InsetGrouped 卡片，
        // 只有第一张带「控制栏功能」标题，第二张不建标题，卡片间由系统分组间距自然隔开。
        _hotkeySection = new CheckableListSection<SettingsViewModel.HotkeyItem>(
            headerTitle: null,
            itemsProvider: () => Settings.Hotkeys,
            titleSelector: item => item.Name,
            subtitleSelector: _ => null,
            isEnabledGetter: item => item.IsDisplay,
            isEnabledSetter: (item, value) => item.IsDisplay = value,
            moveAction: Settings.MoveHotkey,
            rowHeight: iOSNativeSettingsMetrics.ListRowHeight,
            showsSubtitle: false,
            showsReorderControls: true,
            leadingIconSelector: CommandIcon,
            trailingAccessorySelector: _phoneForm
                ? null
                : (Func<SettingsViewModel.HotkeyItem, UIView?>?)BindingButtons,
            isCheckmarkActiveSelector: _phoneForm
                ? (Func<SettingsViewModel.HotkeyItem, bool>?)IsPhoneCommandActive
                : null,
            accessibilityValueSelector: _phoneForm
                ? (Func<SettingsViewModel.HotkeyItem, string?>?)PhoneCommandAccessibilityValue
                : null);

        sections.Add(_navigationSection);
        sections.Add(_hotkeySection);

        if (_phoneForm)
        {
            _mappingSection = null;
            return;
        }

        _mappingSection = new StaticRowsSection(
            "键盘映射",
            [
                CreateSwitchRow("使用苹果键盘", null, () => Settings.UseAppleKeyboard, value => Settings.UseAppleKeyboard = value, RefreshHotkeyRows),
                CreateMappingRow("Command", () => Settings.MapCommandTarget, value => Settings.MapCommandTarget = value),
                CreateMappingRow("Option", () => Settings.MapOptionTarget, value => Settings.MapOptionTarget = value),
                CreateMappingRow("Control", () => Settings.MapControlTarget, value => Settings.MapControlTarget = value),
            ]);

        if (_capabilities.HasExternalInput)
        {
            sections.Add(_mappingSection);
        }
    }

    /// <summary>
    /// 手机形态下该命令是否实际显示在控制栏：唯一来源是共享 <see cref="PhoneControlCommands.Resolve"/>
    /// （跳过未勾选项、组合开关开启时跳过独立上下张、最多三项），设置页不复制筛选规则。
    /// </summary>
    private bool IsPhoneCommandActive(SettingsViewModel.HotkeyItem item)
        => PhoneControlCommands.Resolve(
                Settings.Hotkeys.Where(hotkey => hotkey.IsDisplay).Select(hotkey => hotkey.Command),
                Settings.ShowNavigationButtons)
            .Contains(item.Command, StringComparer.Ordinal);

    /// <summary>手机形态命令行的读屏值：区分已勾选、已勾选但未在控制栏显示、未勾选。</summary>
    private string PhoneCommandAccessibilityValue(SettingsViewModel.HotkeyItem item)
        => item.IsDisplay
            ? (IsPhoneCommandActive(item) ? "已勾选" : "已勾选，未在手机控制栏显示")
            : "未勾选";

    /// <summary>布局模式在页内改成「手机」或改回宽形态时，按新形态重建分组（工作副本，不影响主屏）。</summary>
    private void OnLayoutModeChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName != nameof(SettingsViewModel.LayoutMode) || PhoneForm == _phoneForm) return;
        if (NSThread.IsMain) RebuildContent();
        else BeginInvokeOnMainThread(RebuildContent);
    }

    public override void ViewDidLoad()
    {
        base.ViewDidLoad();
        if (_layoutModeHooked) return;
        _layoutModeHooked = true;
        Settings.PropertyChanged += OnLayoutModeChanged;
    }

    private static UIImage? CommandIcon(SettingsViewModel.HotkeyItem item) => CommandIcon(item.Command);

    private static UIImage? CommandIcon(string command)
        => SymbolIcon(PhotoViewer.Apple.AppleSymbolNames.ForCommand(command));

    /// <summary>
    /// 命令图标：取命令目录登记的同一条 SF Symbols，并按命令字形字号（toolbar.command.glyph.fontSize）配置。
    /// 字形点数由这里给定，单元格只提供居中的图标槽位，不再缩放图像。
    /// </summary>
    private static UIImage? SymbolIcon(string? symbol)
        => symbol == null
            ? null
            : UIImage.GetSystemImage(symbol)
                ?.ApplyConfiguration(UIImageSymbolConfiguration.Create((nfloat)DesignTokens.ToolbarCommandGlyphFontSize))
                ?.ImageWithAlignmentRectInsets(UIEdgeInsets.Zero);

    /// <summary>每行的主/次快捷键按钮组：外设连接时创建或复用，纯触控返回 null 表示不显示。</summary>
    internal iOSHotkeyBindingButtons? BindingButtons(SettingsViewModel.HotkeyItem item)
    {
        if (!_capabilities.HasExternalInput) return null;
        if (!_bindingButtons.TryGetValue(item, out var buttons))
        {
            buttons = new iOSHotkeyBindingButtons(item, _recorder, BeginRecording);
            _bindingButtons.Add(item, buttons);
        }
        buttons.SetExternal(true);
        buttons.Update();
        return buttons;
    }

    /// <summary>点击行内按钮开始录制（按钮与验收共用）。</summary>
    internal void BeginRecording(SettingsViewModel.HotkeyItem item, bool secondary)
    {
        if (!_capabilities.HasExternalInput) return;
        View?.EndEditing(true);
        _recorder.BeginRecording(item, secondary, View?.Window, () => BecomeFirstResponder());
    }

    public override bool CanBecomeFirstResponder => true;

    public override void PressesBegan(NSSet<UIPress> presses, UIPressesEvent? evt)
    {
        if (_recorder.IsRecording)
        {
            foreach (var press in presses)
            {
                if (press.Key is { } key) _recorder.CaptureKey((int)key.KeyCode, (ulong)key.ModifierFlags);
            }
            return;
        }
        base.PressesBegan(presses, evt!);
    }

    public override void PressesEnded(NSSet<UIPress> presses, UIPressesEvent? evt)
    {
        foreach (var press in presses)
        {
            if (press.Key is { } key) _recorder.UpdateModifiers((ulong)key.ModifierFlags);
        }
        if (!_recorder.IsRecording) base.PressesEnded(presses, evt!);
    }

    private UITableViewCell CreateMappingRow(
        string title,
        Func<PhotoViewer.Core.Input.AppleMappingTarget> getter,
        Action<PhotoViewer.Core.Input.AppleMappingTarget> setter)
    {
        var configuration = UIButtonConfiguration.PlainButtonConfiguration;
        configuration.Indicator = UIButtonConfigurationIndicator.Popup;
        configuration.BaseForegroundColor = UIColor.SecondaryLabel;
        configuration.ContentInsets = new NSDirectionalEdgeInsets(6, 0, 6, 0);
        var button = new UIButton(UIButtonType.System)
        {
            Configuration = configuration,
            ShowsMenuAsPrimaryAction = true,
            ChangesSelectionAsPrimaryAction = true,
            AccessibilityLabel = title,
            TranslatesAutoresizingMaskIntoConstraints = false,
        };
        if (button.TitleLabel != null)
        {
            button.TitleLabel.AdjustsFontForContentSizeCategory = true;
        }
        button.SetContentHuggingPriority((float)UILayoutPriority.Required, UILayoutConstraintAxis.Horizontal);
        button.Menu = UIMenu.Create(Settings.AppleMappingTargets.Select(target =>
        {
            var action = UIAction.Create(target.ToString(), null, null, _ =>
            {
                setter(target);
                RefreshHotkeyRows();
            });
            action.State = target == getter() ? UIMenuElementState.On : UIMenuElementState.Off;
            return (UIMenuElement)action;
        }).ToArray());

        var label = new UILabel
        {
            Text = title,
            Font = UIFont.PreferredBody!,
            AdjustsFontForContentSizeCategory = true,
            TextColor = UIColor.Label,
            TranslatesAutoresizingMaskIntoConstraints = false,
        };
        var cell = new UITableViewCell(UITableViewCellStyle.Default, null)
        {
            SelectionStyle = UITableViewCellSelectionStyle.None,
        };
        cell.ContentView.DirectionalLayoutMargins = new NSDirectionalEdgeInsets(4, 16, 4, 16);
        cell.ContentView.AddSubview(label);
        cell.ContentView.AddSubview(button);
        var minHeight = cell.ContentView.HeightAnchor.ConstraintGreaterThanOrEqualTo(44);
        minHeight.Priority = 999;
        NSLayoutConstraint.ActivateConstraints(
        [
            minHeight,
            label.LeadingAnchor.ConstraintEqualTo(cell.ContentView.LayoutMarginsGuide.LeadingAnchor),
            label.CenterYAnchor.ConstraintEqualTo(cell.ContentView.CenterYAnchor),
            label.TrailingAnchor.ConstraintLessThanOrEqualTo(button.LeadingAnchor, -12),
            button.TrailingAnchor.ConstraintEqualTo(cell.ContentView.LayoutMarginsGuide.TrailingAnchor),
            button.TopAnchor.ConstraintGreaterThanOrEqualTo(cell.ContentView.LayoutMarginsGuide.TopAnchor),
            button.BottomAnchor.ConstraintLessThanOrEqualTo(cell.ContentView.LayoutMarginsGuide.BottomAnchor),
            button.CenterYAnchor.ConstraintEqualTo(cell.ContentView.CenterYAnchor),
        ]);
        return cell;
    }

    /// <summary>就地刷新行与行内按钮（录制、外设能力与验收直接改工作副本后共用）。</summary>
    internal void RefreshHotkeyRows()
    {
        RefreshBindingButtons();
        _navigationSection?.RefreshInPlace(TableView, NavigationSectionIndex);
        _hotkeySection?.RefreshInPlace(TableView, HotkeySectionIndex);
    }

    private void RefreshBindingButtons()
    {
        bool external = _capabilities.HasExternalInput;
        foreach (var buttons in _bindingButtons.Values)
        {
            buttons.SetExternal(external);
            buttons.Update();
        }
    }

    /// <summary>外设连接状态变化：就地刷新命令行，并按需增删键盘映射分组（不重建命令行、不改变滚动位置）。</summary>
    private void RefreshCapabilities()
    {
        bool external = _capabilities.HasExternalInput;
        var offset = TableView.ContentOffset;
        RefreshHotkeyRows();

        if (!_phoneForm && _mappingSection != null && ViewIfLoaded != null)
        {
            int mappingIndex = KeyboardMappingSectionIndex;
            if (external && mappingIndex < 0)
            {
                int insertAt = Sections.Count;
                Sections.Add(_mappingSection);
                UIView.PerformWithoutAnimation(() =>
                {
                    TableView.InsertSections(NSIndexSet.FromIndex(insertAt), UITableViewRowAnimation.None);
                    TableView.LayoutIfNeeded();
                    TableView.SetContentOffset(offset, false);
                });
            }
            else if (!external && mappingIndex >= 0)
            {
                Sections.RemoveAt(mappingIndex);
                UIView.PerformWithoutAnimation(() =>
                {
                    TableView.DeleteSections(NSIndexSet.FromIndex(mappingIndex), UITableViewRowAnimation.None);
                    TableView.LayoutIfNeeded();
                    TableView.SetContentOffset(offset, false);
                });
            }
        }
    }

    public override void ViewWillAppear(bool animated)
    {
        base.ViewWillAppear(animated);
        RefreshHotkeyRows();
    }

    public override void ViewWillDisappear(bool animated)
    {
        _recorder.CancelRecording();
        base.ViewWillDisappear(animated);
    }

    public override void ViewDidDisappear(bool animated)
    {
        base.ViewDidDisappear(animated);
        if (NavigationController == null || NavigationController.PresentingViewController == null)
        {
            _recorder.Dispose();
            _capabilities.Dispose();
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            if (_layoutModeHooked)
            {
                _layoutModeHooked = false;
                Settings.PropertyChanged -= OnLayoutModeChanged;
            }
            _capabilities.Changed -= RefreshCapabilities;
            _recorder.Changed -= RefreshHotkeyRows;
            _recorder.Dispose();
            _capabilities.Dispose();
        }
        base.Dispose(disposing);
    }
}

/// <summary>
/// iOS 原生 AI 设置页。AI 版配置相似聚类阈值/数量与「即时计算 DINO」「即时计算 CV」两个独立开关；
/// 标准版只保留「分析诊断」组的「即时计算 CV」，不建相似聚类分组或空槽。
/// </summary>
internal sealed class iOSNativeAiSettingsViewController : iOSNativeSettingsFormViewController
{
    public iOSNativeAiSettingsViewController(SettingsViewModel settings)
        : base(settings)
    {
    }

    protected override void BuildSections(List<SettingsSection> sections)
    {
        Title = "AI";

#if PHOTOVIEWER_DINO
        sections.Add(CreateSection(
            "相似聚类",
            null,
            CreateSliderRow(
                "相似度阈值",
                null,
                () => (int)Math.Round(Settings.SimilarityThreshold * 100),
                value => Settings.SimilarityThreshold = value / 100.0,
                (int)Math.Round(SettingsViewModel.SimilarityThresholdMin * 100),
                (int)Math.Round(SettingsViewModel.SimilarityThresholdMax * 100),
                valueFormatter: value => $"{value}%"),
            CreateSliderRow(
                "最多数量",
                null,
                () => Settings.SimilarityMaxResults,
                value => Settings.SimilarityMaxResults = value,
                SettingsViewModel.SimilarityMaxResultsMin,
                SettingsViewModel.SimilarityMaxResultsMax,
                valueFormatter: value => $"{value} 张")));
#endif

        sections.Add(CreateSection(
            "分析诊断",
            null,
#if PHOTOVIEWER_DINO
            CreateSwitchRow(
                "即时计算 DINO",
                null,
                () => Settings.AnalysisImmediateCompute,
                value => Settings.AnalysisImmediateCompute = value),
#endif
            CreateSwitchRow(
                "即时计算 CV",
                null,
                () => Settings.AnalysisImmediateCvCompute,
                value => Settings.AnalysisImmediateCvCompute = value)));
    }
}

/// <summary>设置页可勾选列表共用的标准行高常量。</summary>
internal static class iOSNativeSettingsMetrics
{
    /// <summary>控制栏命令表与 EXIF 显示表共用的标准行高（SETTINGS-01@ios / SETTINGS-08）。</summary>
    internal const double ListRowHeight = 52;
}

/// <summary>
/// iOS 原生分组表内的可勾选单元格：左侧勾选位、可选命令图标、标题、可选副标题、可选行尾附件，
/// 由所属 <see cref="UITableViewStyle.InsetGrouped"/> 表格直接提供圆角卡片、发丝分隔线与系统拖拽手柄。
/// </summary>
internal sealed class iOSNativeCheckableSettingsCell : UITableViewCell
{
    /// <summary>勾选 → 命令图标的视觉间距（SETTINGS-01）。</summary>
    private const double CheckToIconGap = 8;

    /// <summary>命令图标 → 命令名的视觉间距（SETTINGS-01）。</summary>
    private const double IconToTitleGap = 8;

    private readonly UIImageView _checkmarkView;
    private readonly UILabel _titleLabel;
    private readonly UILabel? _subtitleLabel;
    private readonly NSLayoutConstraint[] _centeredConstraints = [];
    private readonly NSLayoutConstraint[] _stackedConstraints = [];
    private readonly NSLayoutConstraint _titleTrailingDefault;
    private readonly UIImageView _iconView;
    private readonly NSLayoutConstraint _titleLeadingIcon;
    private readonly NSLayoutConstraint _titleLeadingPlain;
    private NSLayoutConstraint? _titleTrailingAccessory;
    private NSLayoutConstraint[] _accessoryConstraints = [];
    private UIView? _trailingAccessory;

    /// <summary>行尾附加控件（命令表行的主/次快捷键按钮组；验收读取）。</summary>
    internal UIView? TrailingAccessory => _trailingAccessory;

    /// <summary>命令行图标（命令表；验收读取可见性与图像）。</summary>
    internal UIImageView LeadingIconView => _iconView;

    /// <summary>标题标签（验收读取）。</summary>
    internal UILabel TitleLabel => _titleLabel;

    /// <summary>副标题标签；Default 样式为 null（验收读取）。</summary>
    internal UILabel? SubtitleLabel => _subtitleLabel;

    /// <summary>勾选标记（验收读取）。</summary>
    internal UIImageView CheckmarkView => _checkmarkView;

    /// <summary>勾选是否在当前形态实际生效；false 表示已勾选但未在手机控制栏显示（只淡化勾号）。</summary>
    internal bool CheckmarkActive { get; private set; } = true;

    public iOSNativeCheckableSettingsCell(string reuseIdentifier, bool showsSubtitle, double minHeight = 52)
        : base(UITableViewCellStyle.Default, reuseIdentifier)
    {
        // 保留系统 InsetGrouped 默认单元格背景，使其与 sheet 材质和圆角卡片完全一致。
        ContentView.BackgroundColor = UIColor.Clear;
        SelectionStyle = UITableViewCellSelectionStyle.Default;

        _checkmarkView = new UIImageView
        {
            TranslatesAutoresizingMaskIntoConstraints = false,
            TintColor = UIColor.SystemBlue,
            ContentMode = UIViewContentMode.ScaleAspectFit,
            Image = UIImage.GetSystemImage("checkmark"),
        };

        _titleLabel = new UILabel
        {
            TranslatesAutoresizingMaskIntoConstraints = false,
            Font = UIFont.PreferredBody!,
            AdjustsFontForContentSizeCategory = true,
            TextColor = UIColor.Label,
            Lines = 1,
            LineBreakMode = UILineBreakMode.TailTruncation,
        };

        _iconView = new UIImageView
        {
            TranslatesAutoresizingMaskIntoConstraints = false,
            TintColor = UIColor.Label,
            // 字形按命令字号（toolbar.command.glyph.fontSize）原尺寸居中落进槽位：
            // AspectFit 在槽位比字形窄时会静默缩小 18 点字形，缩小后与控制栏按钮不再同口径。
            ContentMode = UIViewContentMode.Center,
            Hidden = true,
        };

        ContentView.AddSubview(_checkmarkView);
        ContentView.AddSubview(_iconView);
        ContentView.AddSubview(_titleLabel);

        UILabel? subtitleLabel = null;
        if (showsSubtitle)
        {
            subtitleLabel = new UILabel
            {
                TranslatesAutoresizingMaskIntoConstraints = false,
                Font = UIFont.PreferredFootnote!,
                AdjustsFontForContentSizeCategory = true,
                TextColor = UIColor.SecondaryLabel,
                Lines = 1,
            };
            ContentView.AddSubview(subtitleLabel);
        }

        _subtitleLabel = subtitleLabel;

        var leadingInset = 16f;
        var checkmarkWidth = 22f;
        var spacing = 12f;
        // 图标槽位与字形分开：槽位取共享手机命令图标槽位令牌（24 点，Android 同值），
        // 字形按自身 18 点原尺寸居中，槽位不再参与缩放。
        var iconSlot = (nfloat)DesignTokens.SettingsMobileCommandIconSlotSize;

        var minHeightConstraint = ContentView.HeightAnchor.ConstraintGreaterThanOrEqualTo((nfloat)minHeight);
        minHeightConstraint.Priority = 999;
        minHeightConstraint.Active = true;

        _checkmarkView.WidthAnchor.ConstraintEqualTo(checkmarkWidth).Active = true;
        _checkmarkView.HeightAnchor.ConstraintEqualTo(22).Active = true;
        _checkmarkView.LeadingAnchor.ConstraintEqualTo(ContentView.LeadingAnchor, leadingInset).Active = true;
        _checkmarkView.CenterYAnchor.ConstraintEqualTo(ContentView.CenterYAnchor).Active = true;
        _iconView.WidthAnchor.ConstraintEqualTo(iconSlot).Active = true;
        _iconView.HeightAnchor.ConstraintEqualTo(iconSlot).Active = true;
        // SETTINGS-01：勾选 → 命令图标与图标 → 命令名的视觉间距同为 8。
        _iconView.LeadingAnchor.ConstraintEqualTo(_checkmarkView.TrailingAnchor, (nfloat)CheckToIconGap).Active = true;
        _iconView.CenterYAnchor.ConstraintEqualTo(ContentView.CenterYAnchor).Active = true;
        _titleLeadingPlain = _titleLabel.LeadingAnchor.ConstraintEqualTo(_checkmarkView.TrailingAnchor, spacing);
        _titleLeadingIcon = _titleLabel.LeadingAnchor.ConstraintEqualTo(_iconView.TrailingAnchor, (nfloat)IconToTitleGap);
        _titleLeadingPlain.Active = true;
        _titleTrailingDefault = _titleLabel.TrailingAnchor.ConstraintLessThanOrEqualTo(ContentView.TrailingAnchor, -16);
        _titleTrailingDefault.Active = true;

        _centeredConstraints =
        [
            _titleLabel.CenterYAnchor.ConstraintEqualTo(ContentView.CenterYAnchor),
            _titleLabel.TopAnchor.ConstraintGreaterThanOrEqualTo(ContentView.TopAnchor, 8),
            _titleLabel.BottomAnchor.ConstraintLessThanOrEqualTo(ContentView.BottomAnchor, -8),
        ];
        if (_subtitleLabel != null)
        {
            _subtitleLabel.LeadingAnchor.ConstraintEqualTo(_titleLabel.LeadingAnchor).Active = true;
            _subtitleLabel.TrailingAnchor.ConstraintLessThanOrEqualTo(ContentView.TrailingAnchor, -16).Active = true;
            _stackedConstraints =
            [
                _titleLabel.TopAnchor.ConstraintEqualTo(ContentView.TopAnchor, 10),
                _subtitleLabel.TopAnchor.ConstraintEqualTo(_titleLabel.BottomAnchor, 2),
                _subtitleLabel.BottomAnchor.ConstraintLessThanOrEqualTo(ContentView.BottomAnchor, -10),
            ];
        }
        NSLayoutConstraint.ActivateConstraints(_subtitleLabel == null ? _centeredConstraints : _stackedConstraints);
    }

    /// <summary>设置或移除行尾附加控件（命令表行的主/次快捷键按钮组），标题在附加控件前尾部截断。</summary>
    internal void SetTrailingAccessory(UIView? accessory)
    {
        if (ReferenceEquals(_trailingAccessory, accessory)) return;
        NSLayoutConstraint.DeactivateConstraints(_accessoryConstraints);
        _accessoryConstraints = [];
        if (_trailingAccessory != null)
        {
            _titleTrailingAccessory?.Active = false;
            _titleTrailingAccessory = null;
            _trailingAccessory.RemoveFromSuperview();
        }

        _trailingAccessory = accessory;
        if (accessory == null)
        {
            _titleTrailingDefault.Active = true;
            return;
        }

        _titleTrailingDefault.Active = false;
        accessory.TranslatesAutoresizingMaskIntoConstraints = false;
        ContentView.AddSubview(accessory);
        _titleTrailingAccessory = _titleLabel.TrailingAnchor.ConstraintLessThanOrEqualTo(accessory.LeadingAnchor, -8);
        _accessoryConstraints =
        [
            accessory.TrailingAnchor.ConstraintEqualTo(ContentView.TrailingAnchor, -8),
            accessory.CenterYAnchor.ConstraintEqualTo(ContentView.CenterYAnchor),
            _titleTrailingAccessory,
        ];
        NSLayoutConstraint.ActivateConstraints(_accessoryConstraints);
        SetNeedsLayout();
    }

    /// <summary>刷新单元格展示状态。</summary>
    public void Configure(
        string title,
        string? subtitle,
        bool isEnabled,
        string? accessibilityValue = null,
        UIImage? icon = null,
        bool checkmarkActive = true)
    {
        _titleLabel.Text = title;
        bool hasIcon = icon != null;
        _iconView.Image = icon;
        _iconView.Hidden = !hasIcon;
        _iconView.IsAccessibilityElement = false;
        _iconView.AccessibilityElementsHidden = true;
        _titleLeadingIcon.Active = hasIcon;
        _titleLeadingPlain.Active = !hasIcon;
        if (_subtitleLabel != null)
        {
            _subtitleLabel.Text = subtitle;
            bool hasSubtitle = !string.IsNullOrWhiteSpace(subtitle);
            _subtitleLabel.Hidden = !hasSubtitle;
            _subtitleLabel.IsAccessibilityElement = hasSubtitle;
            _subtitleLabel.AccessibilityElementsHidden = !hasSubtitle;
            if (hasSubtitle)
            {
                NSLayoutConstraint.DeactivateConstraints(_centeredConstraints);
                NSLayoutConstraint.ActivateConstraints(_stackedConstraints);
            }
            else
            {
                NSLayoutConstraint.DeactivateConstraints(_stackedConstraints);
                NSLayoutConstraint.ActivateConstraints(_centeredConstraints);
            }
        }

        _checkmarkView.Hidden = !isEnabled;
        CheckmarkActive = isEnabled && checkmarkActive;
        // SETTINGS-08：已勾选但未在手机控制栏生效的命令只淡化勾号（浅灰白、低不透明度）；
        // 行、图标、文字、拖拽把手与勾选操作照常可用。
        _checkmarkView.TintColor = isEnabled && !checkmarkActive
            ? UIColor.FromWhiteAlpha((nfloat)1, (nfloat)DesignTokens.SettingsPhoneInactiveCheckOpacity)
            : UIColor.SystemBlue;
        AccessibilityTraits = isEnabled
            ? AccessibilityTraits | UIAccessibilityTrait.Selected
            : AccessibilityTraits & ~UIAccessibilityTrait.Selected;
        AccessibilityValue = accessibilityValue;
    }
}
