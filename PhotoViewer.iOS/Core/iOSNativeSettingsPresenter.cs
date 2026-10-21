using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Foundation;
using PhotoViewer.Core.Platform;
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

            var session = new iOSNativeSettingsSession(settings);
            var rootController = new iOSNativeSettingsRootViewController(session);
            var navigationController = new iOSNativeSettingsNavigationController(rootController, session);
            // SETTINGS-03：设置页固定深色，不随系统亮/暗切换（与工具页同一口径）。
            navigationController.OverrideUserInterfaceStyle = UIUserInterfaceStyle.Dark;
            ConfigureSheetPresentation(navigationController.SheetPresentationController);
            if (iOSViewControllerLocator.GetTopMostViewController() is not { } topController)
            {
                return;
            }

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
        sheetPresentationController.PrefersScrollingExpandsWhenScrolledToEdge = false;
        sheetPresentationController.PreferredCornerRadius = 24;
    }
}

/// <summary>
/// iOS 原生设置编辑会话。
/// 负责持有一个可编辑副本，并在关闭时一次性回写到共享设置实例。
/// </summary>
internal sealed class iOSNativeSettingsSession
{
    private readonly SettingsViewModel _source;
    private bool _hasApplied;

    /// <summary>
    /// 当前会话使用的工作设置副本。
    /// </summary>
    public SettingsViewModel WorkingSettings { get; }

    /// <summary>
    /// 共享层当前生效的设置实例；持久化状态只从源实例读取（工作副本用临时存储，永远没有失败状态）。
    /// </summary>
    public SettingsViewModel Source => _source;

    /// <summary>
    /// 初始化原生设置编辑会话。
    /// </summary>
    /// <param name="source">共享层当前生效的设置实例。</param>
    public iOSNativeSettingsSession(SettingsViewModel source)
    {
        _source = source;
        WorkingSettings = new SettingsViewModel(
            settingsService: new iOSNativeTransientSettingsService(),
            initialModel: source.CreateSnapshot(), applyRuntimeSettings: false);
    }

    /// <summary>
    /// 将暂存改动一次性应用回共享设置实例。
    /// 重复调用时只会真正应用一次，避免关闭回调多次触发。
    /// </summary>
    public void ApplyPendingChanges()
    {
        if (_hasApplied)
        {
            return;
        }

        iOSViewControllerLocator.GetTopMostViewController()?.View?.EndEditing(true);
        _source.ApplySnapshot(WorkingSettings.CreateSnapshot());
        _hasApplied = true;
    }
}

/// <summary>
/// iOS 原生设置临时存储服务。
/// 用于屏蔽工作副本的自动持久化，避免编辑过程中提前落盘。
/// </summary>
internal sealed class iOSNativeTransientSettingsService : ISettingsService
{
    /// <summary>工作副本读取始终成功，没有读取错误。</summary>
    public string? LastLoadError => null;

    /// <summary>工作副本不落盘，没有保存错误。</summary>
    public string? LastSaveError => null;

    /// <summary>
    /// 读取临时设置。
    /// 工作副本通过初始快照同步，这里始终返回空模型。
    /// </summary>
    /// <returns>空设置模型。</returns>
    public Task<SettingsModel> LoadAsync()
    {
        return Task.FromResult(new SettingsModel());
    }

    /// <summary>
    /// 忽略工作副本的持久化请求。
    /// 真正的写回在设置页关闭时统一发生。
    /// </summary>
    /// <param name="model">待保存的临时模型。</param>
    public Task SaveAsync(SettingsModel model)
    {
        return Task.CompletedTask;
    }
}

/// <summary>
/// iOS 原生设置导航控制器。
/// 负责持有关闭代理，确保系统下拉关闭时也会统一应用暂存设置。
/// </summary>
internal sealed class iOSNativeSettingsNavigationController : UINavigationController
{
    private readonly iOSNativeSettingsDismissDelegate _dismissDelegate;

    /// <summary>
    /// 初始化原生设置导航控制器。
    /// </summary>
    /// <param name="rootViewController">根控制器。</param>
    /// <param name="session">当前设置编辑会话。</param>
    public iOSNativeSettingsNavigationController(UIViewController rootViewController, iOSNativeSettingsSession session)
        : base(rootViewController)
    {
        ModalPresentationStyle = UIModalPresentationStyle.PageSheet;
        _dismissDelegate = new iOSNativeSettingsDismissDelegate(session);
    }

    /// <summary>
    /// 页面即将显示时挂接系统 dismiss 回调：presentation controller 在展示开始时才存在，
    /// 放在 viewDidLoad 会拿不到它，导致系统下拉关闭不回写设置。
    /// </summary>
    /// <param name="animated">系统动画标记。</param>
    public override void ViewWillAppear(bool animated)
    {
        base.ViewWillAppear(animated);
        if (PresentationController != null)
        {
            PresentationController.Delegate = _dismissDelegate;
        }
    }
}

/// <summary>
/// iOS 原生设置关闭代理。
/// 用于统一处理关闭按钮与下拉收起后的设置提交。
/// </summary>
internal sealed class iOSNativeSettingsDismissDelegate : UIAdaptivePresentationControllerDelegate
{
    private readonly iOSNativeSettingsSession _session;

    /// <summary>
    /// 初始化关闭代理。
    /// </summary>
    /// <param name="session">当前设置编辑会话。</param>
    public iOSNativeSettingsDismissDelegate(iOSNativeSettingsSession session)
    {
        _session = session;
    }

    /// <summary>
    /// 在系统 sheet 完成关闭后统一应用暂存设置。
    /// </summary>
    /// <param name="presentationController">当前展示控制器。</param>
    public override void DidDismiss(UIPresentationController presentationController)
    {
        _session.ApplyPendingChanges();
    }
}

/// <summary>
/// iOS 原生设置页根控制器。
/// 负责展示一级分组入口并提供关闭按钮。
/// </summary>
internal sealed class iOSNativeSettingsRootViewController : UITableViewController
{
    private readonly iOSNativeSettingsSession _session;
    private readonly SettingsViewModel _settings;
    private readonly UIView _statusHeader = new();
    private readonly UILabel _statusLabel = new();

    private readonly (string Title, string? Subtitle, Func<UIViewController> Factory)[] _items;

    /// <summary>当前编辑会话（测试缝：验收核对关闭回写后的设置传播）。</summary>
    internal iOSNativeSettingsSession Session => _session;

    /// <summary>设置页顶部的持久化失败状态行（SETTINGS-14，测试缝）。</summary>
    internal UILabel PersistenceStatusLabel => _statusLabel;

    /// <summary>
    /// 初始化原生设置页根控制器。
    /// </summary>
    /// <param name="settings">共享设置 ViewModel。</param>
    public iOSNativeSettingsRootViewController(iOSNativeSettingsSession session)
        : base(UITableViewStyle.InsetGrouped)
    {
        _session = session;
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

    /// <summary>
    /// 初始化导航栏与表格样式。
    /// </summary>
    public override void ViewDidLoad()
    {
        base.ViewDidLoad();
        Title = "设置";
        NavigationItem.RightBarButtonItem = new UIBarButtonItem(UIBarButtonSystemItem.Close, (_, _) =>
        {
            _session.ApplyPendingChanges();
            DismissViewController(true, null);
        });
        // 首页与子页、工具页统一用同一层系统分组背景，sheet 玻璃在各页取到相同材质。
        TableView.BackgroundColor = UIColor.SystemGroupedBackground;
        TableView.CellLayoutMarginsFollowReadableWidth = false;
        // SETTINGS-14：读取/保存失败在设置页顶部显示一行可见状态（源设置实例才有真实持久化状态）。
        _statusLabel.Font = UIFont.SystemFontOfSize(12)!;
        _statusLabel.TextColor = UIColor.SystemRed;
        _statusLabel.Lines = 0;
        _statusLabel.LineBreakMode = UILineBreakMode.WordWrap;
        _statusLabel.BackgroundColor = UIColor.Clear;
        _statusHeader.BackgroundColor = UIColor.Clear;
        _statusHeader.AddSubview(_statusLabel);
        TableView.TableHeaderView = _statusHeader;
        RefreshPersistenceStatus();
    }

    /// <summary>页面出现时跟随源设置的持久化状态；离开时解除订阅。</summary>
    /// <param name="animated">系统动画标记。</param>
    public override void ViewWillAppear(bool animated)
    {
        base.ViewWillAppear(animated);
        _session.Source.PropertyChanged += OnSourceSettingsChanged;
        RefreshPersistenceStatus();
    }

    /// <summary>页面离开时解除源设置订阅。</summary>
    /// <param name="animated">系统动画标记。</param>
    public override void ViewWillDisappear(bool animated)
    {
        _session.Source.PropertyChanged -= OnSourceSettingsChanged;
        base.ViewWillDisappear(animated);
    }

    /// <summary>表格宽度变化后重算状态行高度，避免状态文本被截断。</summary>
    public override void ViewDidLayoutSubviews()
    {
        base.ViewDidLayoutSubviews();
        LayoutStatusHeader();
    }

    /// <summary>源设置持久化状态变化（跨线程）后回到 UI 线程刷新。</summary>
    /// <param name="sender">事件源（源设置 VM）。</param>
    /// <param name="args">属性变化参数。</param>
    private void OnSourceSettingsChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(SettingsViewModel.PersistenceStatus)) UiDispatcher.Post(RefreshPersistenceStatus);
    }

    /// <summary>同步设置页顶部状态行：没有失败信息时整行收起。</summary>
    private void RefreshPersistenceStatus()
    {
        string? status = _session.Source.PersistenceStatus;
        _statusLabel.Text = status ?? string.Empty;
        _statusLabel.Hidden = string.IsNullOrWhiteSpace(status);
        LayoutStatusHeader();
    }

    /// <summary>按文本换行测量状态行高度并挂回表格头。</summary>
    private void LayoutStatusHeader()
    {
        double width = Math.Max(1, (double)TableView.Bounds.Width);
        double textWidth = Math.Max(1, width - 32);
        double height = _statusLabel.Hidden
            ? 0
            : Math.Ceiling((double)_statusLabel.SizeThatFits(new CoreGraphics.CGSize(textWidth, 10000)).Height) + 16;
        _statusLabel.Frame = new CoreGraphics.CGRect(16, 8, textWidth, Math.Max(1, height - 16));
        var size = new CoreGraphics.CGSize(width, height);
        // 只比较尺寸：UIKit 自己会把表格头原点挪到内容起点（y=35），比较原点会让每次布局都重挂表头并无限重排。
        if (!ReferenceEquals(TableView.TableHeaderView, _statusHeader) || _statusHeader.Frame.Size != size)
        {
            _statusHeader.Frame = new CoreGraphics.CGRect(0, 0, width, height);
            TableView.TableHeaderView = _statusHeader;
        }
    }

    /// <summary>
    /// 返回分组数量。
    /// </summary>
    /// <param name="tableView">当前表格。</param>
    /// <returns>固定 1 个分组。</returns>
    public override nint NumberOfSections(UITableView tableView)
    {
        return 1;
    }

    /// <summary>
    /// 返回当前分组的行数。
    /// </summary>
    /// <param name="tableView">当前表格。</param>
    /// <param name="section">分组索引。</param>
    /// <returns>一级分类数量。</returns>
    public override nint RowsInSection(UITableView tableView, nint section)
    {
        return _items.Length;
    }

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
/// 原生设置滚动表单基类。
/// 负责搭建带卡片分组的滚动容器并提供常用原生控件行。
/// </summary>
internal abstract class iOSNativeSettingsFormViewController : UIViewController
{
    private readonly UIScrollView _scrollView = new();
    private readonly UIStackView _contentStack = new();

    /// <summary>
    /// 共享设置 ViewModel。
    /// </summary>
    protected SettingsViewModel Settings { get; }

    /// <summary>
    /// 初始化原生表单基类。
    /// </summary>
    /// <param name="settings">共享设置 ViewModel。</param>
    protected iOSNativeSettingsFormViewController(SettingsViewModel settings)
    {
        Settings = settings;
    }

    /// <summary>
    /// 初始化滚动容器。
    /// </summary>
    public override void ViewDidLoad()
    {
        base.ViewDidLoad();
        View!.BackgroundColor = UIColor.SystemGroupedBackground;
        ConfigureLayout();
        RebuildContent();
    }

    public override void ViewWillDisappear(bool animated)
    {
        View?.EndEditing(true);
        base.ViewWillDisappear(animated);
    }

    /// <summary>
    /// 子类构建页面内容。
    /// </summary>
    /// <param name="contentStack">页面主内容栈。</param>
    protected abstract void BuildContent(UIStackView contentStack);

    /// <summary>
    /// 重新生成整页内容。
    /// </summary>
    protected void RebuildContent()
    {
        foreach (var view in _contentStack.ArrangedSubviews)
        {
            _contentStack.RemoveArrangedSubview(view);
            view.RemoveFromSuperview();
        }

        BuildContent(_contentStack);
    }

    /// <summary>
    /// 配置滚动布局与安全区约束。
    /// </summary>
    private void ConfigureLayout()
    {
        _scrollView.TranslatesAutoresizingMaskIntoConstraints = false;
        var dismissKeyboard = new UITapGestureRecognizer(() => View?.EndEditing(true)) { CancelsTouchesInView = false };
        dismissKeyboard.Delegate = new iOSNonControlTapDelegate();
        _scrollView.AddGestureRecognizer(dismissKeyboard);
        _scrollView.KeyboardDismissMode = UIScrollViewKeyboardDismissMode.Interactive;
        _contentStack.TranslatesAutoresizingMaskIntoConstraints = false;
        _contentStack.Axis = UILayoutConstraintAxis.Vertical;
        _contentStack.Spacing = 20;
        _contentStack.LayoutMarginsRelativeArrangement = true;
        _contentStack.LayoutMargins = new UIEdgeInsets(16, 16, 24, 16);

        var rootView = View!;
        rootView.AddSubview(_scrollView);
        _scrollView.AddSubview(_contentStack);

        NSLayoutConstraint.ActivateConstraints(
        [
            _scrollView.TopAnchor.ConstraintEqualTo(rootView.SafeAreaLayoutGuide.TopAnchor),
            _scrollView.LeadingAnchor.ConstraintEqualTo(rootView.LeadingAnchor),
            _scrollView.TrailingAnchor.ConstraintEqualTo(rootView.TrailingAnchor),
            _scrollView.BottomAnchor.ConstraintEqualTo(rootView.BottomAnchor),

            _contentStack.TopAnchor.ConstraintEqualTo(_scrollView.ContentLayoutGuide.TopAnchor),
            _contentStack.LeadingAnchor.ConstraintEqualTo(_scrollView.ContentLayoutGuide.LeadingAnchor),
            _contentStack.TrailingAnchor.ConstraintEqualTo(_scrollView.ContentLayoutGuide.TrailingAnchor),
            _contentStack.BottomAnchor.ConstraintEqualTo(_scrollView.ContentLayoutGuide.BottomAnchor),
            _contentStack.WidthAnchor.ConstraintEqualTo(_scrollView.FrameLayoutGuide.WidthAnchor),
        ]);
    }

    /// <summary>
    /// 创建带标题与说明的分组卡片。
    /// </summary>
    /// <param name="title">分组标题。</param>
    /// <param name="footer">分组底部说明。</param>
    /// <param name="rows">分组行视图。</param>
    /// <returns>完整分组视图。</returns>
    protected UIView CreateSection(string title, string? footer, params UIView[] rows)
    {
        var sectionStack = new UIStackView
        {
            Axis = UILayoutConstraintAxis.Vertical,
            Spacing = 8,
            TranslatesAutoresizingMaskIntoConstraints = false
        };

        if (!string.IsNullOrWhiteSpace(title))
        {
            var titleLabel = CreateSectionTitleLabel(title);
            sectionStack.AddArrangedSubview(titleLabel);
        }

        sectionStack.AddArrangedSubview(CreateCard(rows));

        if (!string.IsNullOrWhiteSpace(footer))
        {
            sectionStack.AddArrangedSubview(CreateSectionFooterLabel(footer));
        }

        return sectionStack;
    }

    /// <summary>
    /// 创建自定义内容行。
    /// </summary>
    /// <param name="content">行内内容视图。</param>
    /// <returns>带默认边距的行视图。</returns>
    protected UIView CreateCustomRow(UIView content)
    {
        var row = CreateRowContainer();
        row.AddSubview(content);
        NSLayoutConstraint.ActivateConstraints(
        [
            content.TopAnchor.ConstraintEqualTo(row.LayoutMarginsGuide.TopAnchor),
            content.LeadingAnchor.ConstraintEqualTo(row.LayoutMarginsGuide.LeadingAnchor),
            content.TrailingAnchor.ConstraintEqualTo(row.LayoutMarginsGuide.TrailingAnchor),
            content.BottomAnchor.ConstraintEqualTo(row.LayoutMarginsGuide.BottomAnchor),
        ]);

        return row;
    }

    /// <summary>
    /// 创建开关设置行。
    /// </summary>
    /// <param name="title">主标题。</param>
    /// <param name="subtitle">副标题。</param>
    /// <param name="getter">读取当前值的方法。</param>
    /// <param name="setter">写入值的方法。</param>
    /// <param name="onValueChanged">值变化后的额外回调。</param>
    /// <returns>原生开关行。</returns>
    protected UIView CreateSwitchRow(
        string title,
        string? subtitle,
        Func<bool> getter,
        Action<bool> setter,
        Action? onValueChanged = null)
    {
        var toggle = new UISwitch
        {
            On = getter()
        };
        toggle.SetContentHuggingPriority((float)UILayoutPriority.Required, UILayoutConstraintAxis.Horizontal);
        toggle.ValueChanged += (_, _) =>
        {
            setter(toggle.On);
            onValueChanged?.Invoke();
        };

        return CreateAccessoryRow(title, subtitle, toggle);
    }

    /// <summary>
    /// 创建步进器设置行。
    /// </summary>
    /// <param name="title">主标题。</param>
    /// <param name="subtitle">副标题。</param>
    /// <param name="getter">读取当前值的方法。</param>
    /// <param name="setter">写入值的方法。</param>
    /// <param name="minimumProvider">最小值提供器。</param>
    /// <param name="maximumProvider">最大值提供器。</param>
    /// <param name="step">步进值。</param>
    /// <param name="valueFormatter">数值格式化器。</param>
    /// <param name="onValueChanged">值变化后的额外回调。</param>
    /// <returns>原生步进器行。</returns>
    protected UIView CreateStepperRow(
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
        var stepper = new UIStepper
        {
            StepValue = step
        };
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
            TranslatesAutoresizingMaskIntoConstraints = false
        };
        accessoryStack.AddArrangedSubview(valueLabel);
        accessoryStack.AddArrangedSubview(stepper);

        return CreateAccessoryRow(title, subtitle, accessoryStack);
    }

    /// <summary>
    /// 创建滑条设置行。
    /// </summary>
    /// <param name="title">主标题。</param>
    /// <param name="subtitle">副标题。</param>
    /// <param name="getter">读取当前值的方法。</param>
    /// <param name="setter">写入值的方法。</param>
    /// <param name="minimum">最小值。</param>
    /// <param name="maximum">最大值。</param>
    /// <param name="valueFormatter">数值格式化器。</param>
    /// <param name="onValueChanged">值变化后的额外回调。</param>
    /// <param name="onEditingCompleted">结束拖动后的额外回调。</param>
    /// <returns>原生滑条行。</returns>
    protected UIView CreateSliderRow(
        string title,
        string? subtitle,
        Func<int> getter,
        Action<int> setter,
        int minimum,
        int maximum,
        Func<int, string>? valueFormatter = null,
        Action? onValueChanged = null,
        Action? onEditingCompleted = null)
    {
        var row = CreateRowContainer();
        var verticalStack = new UIStackView
        {
            Axis = UILayoutConstraintAxis.Vertical,
            Spacing = 10,
            TranslatesAutoresizingMaskIntoConstraints = false
        };

        var headerStack = new UIStackView
        {
            Axis = UILayoutConstraintAxis.Horizontal,
            Alignment = UIStackViewAlignment.Top,
            Spacing = 12,
            TranslatesAutoresizingMaskIntoConstraints = false
        };
        var textStack = CreateTextStack(title, subtitle);
        var valueLabel = CreateTrailingValueLabel();
        valueLabel.TextAlignment = UITextAlignment.Right;
        valueLabel.Lines = 2;
        valueLabel.SetContentCompressionResistancePriority((float)UILayoutPriority.Required, UILayoutConstraintAxis.Horizontal);
        headerStack.AddArrangedSubview(textStack);
        headerStack.AddArrangedSubview(CreateFlexibleSpacer());
        headerStack.AddArrangedSubview(valueLabel);

        var slider = new UISlider
        {
            MinValue = minimum,
            MaxValue = maximum,
            Continuous = true
        };

        void Refresh()
        {
            var value = Math.Clamp(getter(), minimum, maximum);
            slider.Value = value;
            valueLabel.Text = valueFormatter?.Invoke(value) ?? value.ToString(CultureInfo.InvariantCulture);
        }

        slider.ValueChanged += (_, _) =>
        {
            var newValue = Math.Clamp((int)Math.Round(slider.Value), minimum, maximum);
            setter(newValue);
            Refresh();
            onValueChanged?.Invoke();
        };

        slider.TouchUpInside += (_, _) => onEditingCompleted?.Invoke();
        slider.TouchUpOutside += (_, _) => onEditingCompleted?.Invoke();
        slider.TouchCancel += (_, _) => onEditingCompleted?.Invoke();

        Refresh();

        verticalStack.AddArrangedSubview(headerStack);
        verticalStack.AddArrangedSubview(slider);
        row.AddSubview(verticalStack);

        NSLayoutConstraint.ActivateConstraints(
        [
            verticalStack.TopAnchor.ConstraintEqualTo(row.LayoutMarginsGuide.TopAnchor),
            verticalStack.LeadingAnchor.ConstraintEqualTo(row.LayoutMarginsGuide.LeadingAnchor),
            verticalStack.TrailingAnchor.ConstraintEqualTo(row.LayoutMarginsGuide.TrailingAnchor),
            verticalStack.BottomAnchor.ConstraintEqualTo(row.LayoutMarginsGuide.BottomAnchor),
        ]);

        return row;
    }

    /// <summary>
    /// 创建导航跳转行。
    /// </summary>
    /// <param name="title">主标题。</param>
    /// <param name="subtitle">副标题。</param>
    /// <param name="trailingText">右侧补充文本。</param>
    /// <param name="onTap">点击回调。</param>
    /// <returns>可点击的导航行。</returns>
    protected UIView CreateNavigationRow(
        string title,
        string? subtitle,
        string? trailingText,
        Action onTap)
    {
        var row = CreateRowContainer();
        var button = new UIControl
        {
            TranslatesAutoresizingMaskIntoConstraints = false
        };
        button.TouchUpInside += (_, _) => onTap();
        row.AddSubview(button);

        var contentStack = new UIStackView
        {
            Axis = UILayoutConstraintAxis.Horizontal,
            Alignment = UIStackViewAlignment.Center,
            Spacing = 12,
            TranslatesAutoresizingMaskIntoConstraints = false
        };
        var textStack = CreateTextStack(title, subtitle);
        var trailingLabel = CreateTrailingValueLabel();
        trailingLabel.Text = trailingText;
        trailingLabel.TextAlignment = UITextAlignment.Right;
        trailingLabel.Lines = 2;
        var chevronLabel = new UILabel
        {
            Text = "›",
            Font = UIFont.SystemFontOfSize(20, UIFontWeight.Semibold),
            TextColor = UIColor.TertiaryLabel,
            TranslatesAutoresizingMaskIntoConstraints = false
        };
        chevronLabel.SetContentCompressionResistancePriority((float)UILayoutPriority.Required, UILayoutConstraintAxis.Horizontal);

        contentStack.AddArrangedSubview(textStack);
        contentStack.AddArrangedSubview(CreateFlexibleSpacer());
        contentStack.AddArrangedSubview(trailingLabel);
        contentStack.AddArrangedSubview(chevronLabel);
        button.AddSubview(contentStack);

        NSLayoutConstraint.ActivateConstraints(
        [
            button.TopAnchor.ConstraintEqualTo(row.TopAnchor),
            button.LeadingAnchor.ConstraintEqualTo(row.LeadingAnchor),
            button.TrailingAnchor.ConstraintEqualTo(row.TrailingAnchor),
            button.BottomAnchor.ConstraintEqualTo(row.BottomAnchor),

            contentStack.TopAnchor.ConstraintEqualTo(button.LayoutMarginsGuide.TopAnchor),
            contentStack.LeadingAnchor.ConstraintEqualTo(button.LayoutMarginsGuide.LeadingAnchor),
            contentStack.TrailingAnchor.ConstraintEqualTo(button.LayoutMarginsGuide.TrailingAnchor),
            contentStack.BottomAnchor.ConstraintEqualTo(button.LayoutMarginsGuide.BottomAnchor),
        ]);

        button.LayoutMargins = row.LayoutMargins;
        return row;
    }

    /// <summary>
    /// 创建只读说明行。
    /// </summary>
    /// <param name="title">主标题。</param>
    /// <param name="subtitle">说明文本。</param>
    /// <returns>只读信息行。</returns>
    protected UIView CreateStaticInfoRow(string title, string? subtitle)
    {
        return CreateAccessoryRow(title, subtitle, null);
    }

    /// <summary>
    /// 创建通用左右布局行。
    /// </summary>
    /// <param name="title">主标题。</param>
    /// <param name="subtitle">副标题。</param>
    /// <param name="accessoryView">右侧原生控件。</param>
    /// <returns>构建完成的行视图。</returns>
    private UIView CreateAccessoryRow(string title, string? subtitle, UIView? accessoryView)
    {
        var row = CreateRowContainer();
        var horizontalStack = new UIStackView
        {
            Axis = UILayoutConstraintAxis.Horizontal,
            Alignment = UIStackViewAlignment.Center,
            Spacing = 12,
            TranslatesAutoresizingMaskIntoConstraints = false
        };

        var textStack = CreateTextStack(title, subtitle);
        horizontalStack.AddArrangedSubview(textStack);
        horizontalStack.AddArrangedSubview(CreateFlexibleSpacer());

        if (accessoryView != null)
        {
            horizontalStack.AddArrangedSubview(accessoryView);
        }

        row.AddSubview(horizontalStack);
        NSLayoutConstraint.ActivateConstraints(
        [
            horizontalStack.TopAnchor.ConstraintEqualTo(row.LayoutMarginsGuide.TopAnchor),
            horizontalStack.LeadingAnchor.ConstraintEqualTo(row.LayoutMarginsGuide.LeadingAnchor),
            horizontalStack.TrailingAnchor.ConstraintEqualTo(row.LayoutMarginsGuide.TrailingAnchor),
            horizontalStack.BottomAnchor.ConstraintEqualTo(row.LayoutMarginsGuide.BottomAnchor),
        ]);

        return row;
    }

    /// <summary>
    /// 创建卡片容器，并在多行之间自动插入分隔线。
    /// </summary>
    /// <param name="rows">卡片中的行视图。</param>
    /// <returns>圆角卡片视图。</returns>
    protected UIView CreateCard(params UIView[] rows)
    {
        var card = new UIView
        {
            BackgroundColor = UIColor.SecondarySystemGroupedBackground,
            TranslatesAutoresizingMaskIntoConstraints = false
        };
        card.Layer.CornerRadius = 16;
        card.Layer.MasksToBounds = true;

        var stack = new UIStackView
        {
            Axis = UILayoutConstraintAxis.Vertical,
            Spacing = 0,
            TranslatesAutoresizingMaskIntoConstraints = false
        };
        card.AddSubview(stack);

        NSLayoutConstraint.ActivateConstraints(
        [
            stack.TopAnchor.ConstraintEqualTo(card.TopAnchor),
            stack.LeadingAnchor.ConstraintEqualTo(card.LeadingAnchor),
            stack.TrailingAnchor.ConstraintEqualTo(card.TrailingAnchor),
            stack.BottomAnchor.ConstraintEqualTo(card.BottomAnchor),
        ]);

        for (var index = 0; index < rows.Length; index++)
        {
            stack.AddArrangedSubview(rows[index]);
            if (index < rows.Length - 1)
            {
                stack.AddArrangedSubview(CreateSeparator());
            }
        }

        return card;
    }

    /// <summary>
    /// 创建数字输入完成工具栏。
    /// </summary>
    /// <param name="textField">目标输入框。</param>
    /// <returns>带完成按钮的工具栏。</returns>
    protected static UIToolbar CreateDoneToolbar(UITextField textField)
    {
        var toolbar = new UIToolbar
        {
            TranslatesAutoresizingMaskIntoConstraints = false
        };
        toolbar.SizeToFit();

        var flexibleItem = new UIBarButtonItem(UIBarButtonSystemItem.FlexibleSpace);
        var doneItem = new UIBarButtonItem(UIBarButtonSystemItem.Done, (_, _) => textField.ResignFirstResponder());
        toolbar.SetItems([flexibleItem, doneItem], false);
        return toolbar;
    }

    /// <summary>
    /// 创建标准行容器。
    /// </summary>
    /// <returns>带默认边距的空行视图。</returns>
    private static UIView CreateRowContainer()
    {
        return new UIView
        {
            TranslatesAutoresizingMaskIntoConstraints = false,
            LayoutMargins = new UIEdgeInsets(14, 16, 14, 16)
        };
    }

    /// <summary>
    /// 创建主副标题文本栈。
    /// </summary>
    /// <param name="title">主标题。</param>
    /// <param name="subtitle">副标题。</param>
    /// <returns>纵向文本栈。</returns>
    private static UIStackView CreateTextStack(string title, string? subtitle)
    {
        var stack = new UIStackView
        {
            Axis = UILayoutConstraintAxis.Vertical,
            Spacing = 4,
            TranslatesAutoresizingMaskIntoConstraints = false
        };
        stack.AddArrangedSubview(CreateTitleLabel(title));

        if (!string.IsNullOrWhiteSpace(subtitle))
        {
            stack.AddArrangedSubview(CreateSubtitleLabel(subtitle));
        }

        return stack;
    }

    /// <summary>
    /// 创建分组标题标签。
    /// </summary>
    /// <param name="title">标题文本。</param>
    /// <returns>分组标题标签。</returns>
    private static UILabel CreateSectionTitleLabel(string title)
    {
        return new UILabel
        {
            Text = title,
            Font = UIFont.SystemFontOfSize(13, UIFontWeight.Semibold),
            TextColor = UIColor.SecondaryLabel,
            TranslatesAutoresizingMaskIntoConstraints = false
        };
    }

    /// <summary>
    /// 创建分组底部说明标签。
    /// </summary>
    /// <param name="footer">说明文本。</param>
    /// <returns>说明标签。</returns>
    private static UILabel CreateSectionFooterLabel(string footer)
    {
        return new UILabel
        {
            Text = footer,
            Font = UIFont.SystemFontOfSize(12),
            TextColor = UIColor.SecondaryLabel,
            Lines = 0,
            TranslatesAutoresizingMaskIntoConstraints = false
        };
    }

    /// <summary>
    /// 创建主标题标签。
    /// </summary>
    /// <param name="title">标题文本。</param>
    /// <returns>主标题标签。</returns>
    private static UILabel CreateTitleLabel(string title)
    {
        return new UILabel
        {
            Text = title,
            Font = UIFont.SystemFontOfSize(16),
            TextColor = UIColor.Label,
            Lines = 0,
            TranslatesAutoresizingMaskIntoConstraints = false
        };
    }

    /// <summary>
    /// 创建副标题标签。
    /// </summary>
    /// <param name="subtitle">副标题文本。</param>
    /// <returns>副标题标签。</returns>
    private static UILabel CreateSubtitleLabel(string subtitle)
    {
        return new UILabel
        {
            Text = subtitle,
            Font = UIFont.SystemFontOfSize(12),
            TextColor = UIColor.SecondaryLabel,
            Lines = 0,
            TranslatesAutoresizingMaskIntoConstraints = false
        };
    }

    /// <summary>
    /// 创建右侧数值标签。
    /// </summary>
    /// <returns>数值展示标签。</returns>
    protected static UILabel CreateTrailingValueLabel()
    {
        return new UILabel
        {
            Font = UIFont.MonospacedDigitSystemFontOfSize(14, UIFontWeight.Medium),
            TextColor = UIColor.SecondaryLabel,
            Lines = 1,
            TranslatesAutoresizingMaskIntoConstraints = false
        };
    }

    /// <summary>
    /// 创建可伸缩空白占位视图。
    /// </summary>
    /// <returns>用于把右侧控件推向边缘的占位视图。</returns>
    protected static UIView CreateFlexibleSpacer()
    {
        var spacer = new UIView
        {
            TranslatesAutoresizingMaskIntoConstraints = false
        };
        spacer.SetContentHuggingPriority((float)UILayoutPriority.DefaultLow, UILayoutConstraintAxis.Horizontal);
        spacer.SetContentCompressionResistancePriority((float)UILayoutPriority.DefaultLow, UILayoutConstraintAxis.Horizontal);
        return spacer;
    }

    /// <summary>
    /// 创建分隔线。
    /// </summary>
    /// <returns>分隔线视图。</returns>
    protected static UIView CreateSeparator()
    {
        var separator = new UIView
        {
            BackgroundColor = UIColor.Separator,
            TranslatesAutoresizingMaskIntoConstraints = false
        };
        separator.HeightAnchor.ConstraintEqualTo(1 / UIScreen.MainScreen.Scale).Active = true;
        return separator;
    }
}

/// <summary>
/// iOS 原生文件设置页。
/// </summary>
internal sealed class iOSNativeFileSettingsViewController : iOSNativeSettingsFormViewController
{
    /// <summary>
    /// 初始化文件设置页。
    /// </summary>
    /// <param name="settings">共享设置 ViewModel。</param>
    public iOSNativeFileSettingsViewController(SettingsViewModel settings)
        : base(settings)
    {
    }

    /// <summary>
    /// 构建文件设置页面内容。
    /// </summary>
    /// <param name="contentStack">页面主内容栈。</param>
    protected override void BuildContent(UIStackView contentStack)
    {
        Title = "文件";

        var performanceInfo = new UILabel
        {
            Text = Settings.PerformanceBudgetInfo,
            Font = UIFont.SystemFontOfSize(12),
            TextColor = UIColor.SecondaryLabel,
            Lines = 0,
            TranslatesAutoresizingMaskIntoConstraints = false
        };
        var cacheInfo = new UILabel
        {
            Text = Settings.BitmapCacheCountInfo,
            Font = UIFont.SystemFontOfSize(12),
            TextColor = UIColor.SecondaryLabel,
            Lines = 0,
            TranslatesAutoresizingMaskIntoConstraints = false
        };

        void RefreshInfoLabels()
        {
            performanceInfo.Text = Settings.PerformanceBudgetInfo;
            cacheInfo.Text = Settings.BitmapCacheCountInfo;
        }

        contentStack.AddArrangedSubview(
            CreateSection(
                "",
                null,
                CreateSwitchRow(
                    "同名文件视为同一张图片",
                    "按列表排序优先级高的作为代表显示图片，修改星级时同步至所有同名文件",
                    () => Settings.SameNameAsOnePhoto,
                    value => Settings.SameNameAsOnePhoto = value)));

        contentStack.AddArrangedSubview(
            CreateSection(
                "支持的文件格式",
                null,
                new iOSNativeEmbeddedCheckableReorderListView<SettingsViewModel.FileFormatItem>(
                    itemsProvider: () => Settings.FileFormats,
                    titleSelector: item => item.DisplayName,
                    subtitleSelector: item => item.ExtensionsText,
                    isEnabledGetter: item => item.IsEnabled,
                    isEnabledSetter: (item, value) => item.IsEnabled = value,
                    moveAction: Settings.MoveFileFormat)));

        contentStack.AddArrangedSubview(
            CreateSection(
                "缓存与预加载",
                null,
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
                CreateSliderRow(
                    "缓存内存上限",
                    null,
                    () => Settings.BitmapCacheMaxMemory,
                    value => Settings.BitmapCacheMaxMemory = value,
                    512,
                    32768,
                    valueFormatter: value => $"{value} MB",
                    onValueChanged: RefreshInfoLabels,
                    onEditingCompleted: RebuildContent),
                CreateSliderRow(
                    "缓存数量上限",
                    null,
                    () => Settings.BitmapCacheMaxCount,
                    value => Settings.BitmapCacheMaxCount = value,
                    1,
                    400,
                    valueFormatter: value => $"{value} 张",
                    onValueChanged: RefreshInfoLabels,
                    onEditingCompleted: RebuildContent),
                CreateStepperRow(
                    "下一张预载数量",
                    null,
                    () => Settings.PreloadForwardCount,
                    value => Settings.PreloadForwardCount = value,
                    () => 0,
                    () => Settings.PreloadMaximum,
                    valueFormatter: value => $"{value} 张"),
                CreateStepperRow(
                    "上一张预载数量",
                    null,
                    () => Settings.PreloadBackwardCount,
                    value => Settings.PreloadBackwardCount = value,
                    () => 0,
                    () => Settings.PreloadMaximum,
                    valueFormatter: value => $"{value} 张"),
                CreateStepperRow(
                    "列表滚动预载数量",
                    null,
                    () => Settings.VisibleCenterPreloadCount,
                    value => Settings.VisibleCenterPreloadCount = value,
                    () => 0,
                    () => Settings.PreloadMaximum,
                    valueFormatter: value => $"{value} 张"),
                CreateSliderRow(
                    "滚动预载延时",
                    null,
                    () => Settings.VisibleCenterDelayMs,
                    value => Settings.VisibleCenterDelayMs = value,
                    100,
                    5000,
                    valueFormatter: value => $"{value} ms")));

        contentStack.AddArrangedSubview(performanceInfo);
        contentStack.AddArrangedSubview(cacheInfo);
        RefreshInfoLabels();
    }
}

/// <summary>
/// iOS 原生预览设置页。
/// </summary>
internal sealed class iOSNativePreviewSettingsViewController : iOSNativeSettingsFormViewController
{
    /// <summary>
    /// 初始化预览设置页。
    /// </summary>
    /// <param name="settings">共享设置 ViewModel。</param>
    public iOSNativePreviewSettingsViewController(SettingsViewModel settings)
        : base(settings)
    {
    }

    /// <summary>
    /// 构建预览设置页面内容。
    /// </summary>
    /// <param name="contentStack">页面主内容栈。</param>
    protected override void BuildContent(UIStackView contentStack)
    {
        Title = "预览";

        contentStack.AddArrangedSubview(
            CreateSection(
                "缩放位置显示",
                null,
                CreateSwitchRow(
                    "缩放时显示指示器",
                    null,
                    () => Settings.ShowZoomIndicator,
                    value => Settings.ShowZoomIndicator = value)));

        contentStack.AddArrangedSubview(
            CreateSection(
                "缩放比例预设",
                null,
                CreateScalePresetEditor()));
    }

    private UIView CreateScalePresetEditor() => new iOSScalePresetEditor(Settings);

    public override void ViewWillDisappear(bool animated)
    {
        View?.EndEditing(true);
        base.ViewWillDisappear(animated);
    }
}

/// <summary>
/// iOS 原生 EXIF 设置页。
/// </summary>
internal sealed class iOSNativeExifSettingsViewController : iOSNativeSettingsFormViewController
{
    /// <summary>
    /// 初始化 EXIF 设置页。
    /// </summary>
    /// <param name="settings">共享设置 ViewModel。</param>
    public iOSNativeExifSettingsViewController(SettingsViewModel settings)
        : base(settings)
    {
    }

    /// <summary>
    /// 构建 EXIF 设置页面内容。
    /// </summary>
    /// <param name="contentStack">页面主内容栈。</param>
    protected override void BuildContent(UIStackView contentStack)
    {
        Title = "EXIF";

        contentStack.AddArrangedSubview(
            CreateSection(
                "EXIF 显示/收纳",
                null,
                new iOSNativeEmbeddedCheckableReorderListView<SettingsViewModel.ExifDisplayItem>(
                    itemsProvider: () => Settings.ExifDisplayItems,
                    titleSelector: item => item.DisplayName,
                    subtitleSelector: _ => null,
                    isEnabledGetter: item => item.IsEnabled,
                    isEnabledSetter: (item, value) => item.IsEnabled = value,
                    moveAction: Settings.MoveExifDisplay,
                    rowHeight: 52,
                    cellStyle: UITableViewCellStyle.Default)));

        contentStack.AddArrangedSubview(
            CreateSection(
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
/// iOS 原生控制设置页：布局模式三选一、控制栏功能勾选排序列表与键盘映射。
/// 每行与 Mac 单行布局一致：勾选、名称、主/次快捷键按钮、系统拖拽手柄。纯触控只显示勾选、名称与
/// 拖拽手柄；连接键盘或鼠标后同一行就地出现两个绑定按钮，点按钮进入录制，Delete/Backspace 清空，
/// 长按菜单「清空」。外设连接变化就地刷新行，不重建页面、不丢工作副本。
/// </summary>
internal sealed class iOSNativeControlSettingsViewController : iOSNativeSettingsFormViewController
{
    private readonly iOSInputCapabilities _capabilities = new();
    private readonly iOSHotkeyRecorder _recorder;
    private readonly Dictionary<SettingsViewModel.HotkeyItem, iOSHotkeyBindingButtons> _bindingButtons = [];
    private iOSNativeEmbeddedCheckableReorderListView<SettingsViewModel.HotkeyItem>? _hotkeys;
    private UIView? _mapping;

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

    /// <summary>控制栏功能列表（验收读取真实单元格）。</summary>
    internal iOSNativeEmbeddedCheckableReorderListView<SettingsViewModel.HotkeyItem>? HotkeyList => _hotkeys;

    /// <summary>键盘映射分组（验收读取可见性）。</summary>
    internal UIView? KeyboardMappingSection => _mapping;

    protected override void BuildContent(UIStackView contentStack)
    {
        Title = "控制";
        contentStack.AddArrangedSubview(CreateSection("布局模式", null, new iOSLayoutModePicker(Settings)));
        _hotkeys = new iOSNativeEmbeddedCheckableReorderListView<SettingsViewModel.HotkeyItem>(
            itemsProvider: () => Settings.Hotkeys,
            titleSelector: item => item.Name,
            subtitleSelector: _ => null,
            isEnabledGetter: item => item.IsDisplay,
            isEnabledSetter: (item, value) => item.IsDisplay = value,
            moveAction: Settings.MoveHotkey,
            rowHeight: 44,
            cellStyle: UITableViewCellStyle.Default,
            trailingAccessorySelector: BindingButtons);
        contentStack.AddArrangedSubview(CreateSection("控制栏功能", null, _hotkeys));

        var rows = new List<UIView>
        {
            CreateSwitchRow("使用苹果键盘", null, () => Settings.UseAppleKeyboard, value => Settings.UseAppleKeyboard = value, RefreshHotkeyRows),
            CreateMappingRow("Command", () => Settings.MapCommandTarget, value => Settings.MapCommandTarget = value),
            CreateMappingRow("Option", () => Settings.MapOptionTarget, value => Settings.MapOptionTarget = value),
            CreateMappingRow("Control", () => Settings.MapControlTarget, value => Settings.MapControlTarget = value),
        };
        _mapping = CreateSection("键盘映射", null, rows.ToArray());
        contentStack.AddArrangedSubview(_mapping);
        RefreshCapabilities();
    }

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

    /// <summary>录制期间接管硬件按键：Esc 取消、Delete/Backspace 清空，其余经共享映射写工作副本。</summary>
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

    private UIView CreateMappingRow(string title, Func<PhotoViewer.Core.Input.AppleMappingTarget> getter, Action<PhotoViewer.Core.Input.AppleMappingTarget> setter)
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
        };
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
            Font = UIFont.SystemFontOfSize(16)!,
            TextColor = UIColor.Label,
            TranslatesAutoresizingMaskIntoConstraints = false,
        };
        var stack = new UIStackView([label, CreateFlexibleSpacer(), button])
        {
            Axis = UILayoutConstraintAxis.Horizontal,
            Alignment = UIStackViewAlignment.Center,
            Spacing = 12,
            TranslatesAutoresizingMaskIntoConstraints = false,
        };
        var row = new UIView { TranslatesAutoresizingMaskIntoConstraints = false, LayoutMargins = new UIEdgeInsets(4, 16, 4, 16) };
        row.AddSubview(stack);
        NSLayoutConstraint.ActivateConstraints(
        [
            stack.TopAnchor.ConstraintEqualTo(row.LayoutMarginsGuide.TopAnchor),
            stack.LeadingAnchor.ConstraintEqualTo(row.LayoutMarginsGuide.LeadingAnchor),
            stack.TrailingAnchor.ConstraintEqualTo(row.LayoutMarginsGuide.TrailingAnchor),
            stack.BottomAnchor.ConstraintEqualTo(row.LayoutMarginsGuide.BottomAnchor),
            row.HeightAnchor.ConstraintGreaterThanOrEqualTo(44),
        ]);
        return row;
    }

    /// <summary>就地刷新行与行内按钮（录制、外设能力与验收直接改工作副本后共用）。</summary>
    internal void RefreshHotkeyRows()
    {
        RefreshBindingButtons();
        _hotkeys?.RefreshInPlace();
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

    private void RefreshCapabilities()
    {
        bool external = _capabilities.HasExternalInput;
        if (_mapping != null)
        {
            _mapping.Hidden = !external;
            _mapping.AccessibilityElementsHidden = !external;
        }
        RefreshHotkeyRows();
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
            _capabilities.Changed -= RefreshCapabilities;
            _recorder.Changed -= RefreshHotkeyRows;
            _recorder.Dispose();
            _capabilities.Dispose();
        }
        base.Dispose(disposing);
    }
}

/// <summary>
/// iOS 原生 AI 设置页。
/// 用于配置相似聚类的阈值与最多返回数量。
/// </summary>
internal sealed class iOSNativeAiSettingsViewController : iOSNativeSettingsFormViewController
{
    /// <summary>
    /// 初始化 AI 设置页。
    /// </summary>
    /// <param name="settings">共享设置 ViewModel。</param>
    public iOSNativeAiSettingsViewController(SettingsViewModel settings)
        : base(settings)
    {
    }

    /// <summary>
    /// 构建 AI 设置页内容。
    /// </summary>
    /// <param name="contentStack">页面主内容栈。</param>
    protected override void BuildContent(UIStackView contentStack)
    {
        Title = "AI";

        contentStack.AddArrangedSubview(
            CreateSection(
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

        contentStack.AddArrangedSubview(
            CreateSection(
                "分析诊断",
                null,
                CreateSwitchRow(
                    "即时计算DINO/CV",
                    null,
                    () => Settings.AnalysisImmediateCompute,
                    value => Settings.AnalysisImmediateCompute = value)));
    }
}

/// <summary>
/// iOS 内联可勾选可排序列表视图：勾选位、标题、可选副标题与系统拖拽手柄；
/// 可选的详情按钮（编辑态 ⓘ）与按行高度由调用方提供。
/// </summary>
/// <typeparam name="TItem">列表项类型。</typeparam>
internal sealed class iOSNativeEmbeddedCheckableReorderListView<TItem> : UIView
{
    private readonly Func<IReadOnlyList<TItem>> _itemsProvider;
    private readonly UITableView _tableView;
    private readonly NSLayoutConstraint _heightConstraint;
    private readonly iOSNativeEmbeddedCheckableReorderListSource<TItem> _source;

    /// <summary>内部原生表格（验收读取真实单元格）。</summary>
    internal UITableView TableView => _tableView;

    /// <summary>
    /// 初始化内联列表视图。
    /// </summary>
    public iOSNativeEmbeddedCheckableReorderListView(
        Func<IReadOnlyList<TItem>> itemsProvider,
        Func<TItem, string> titleSelector,
        Func<TItem, string?> subtitleSelector,
        Func<TItem, bool> isEnabledGetter,
        Action<TItem, bool> isEnabledSetter,
        Action<int, int> moveAction,
        double rowHeight = 60,
        UITableViewCellStyle cellStyle = UITableViewCellStyle.Subtitle,
        Func<TItem, double>? rowHeightSelector = null,
        Func<TItem, NSAttributedString?>? attributedSubtitleSelector = null,
        Func<TItem, string?>? accessibilityValueSelector = null,
        Func<TItem, bool>? showsDetailButton = null,
        Action<TItem>? detailAction = null,
        Func<TItem, UIView?>? trailingAccessorySelector = null)
    {
        _itemsProvider = itemsProvider;

        TranslatesAutoresizingMaskIntoConstraints = false;

        _tableView = new UITableView(CoreGraphics.CGRect.Empty, UITableViewStyle.Plain)
        {
            TranslatesAutoresizingMaskIntoConstraints = false,
            ScrollEnabled = false,
            BackgroundColor = UIColor.Clear,
            RowHeight = (nfloat)rowHeight,
            SeparatorInset = UIEdgeInsets.Zero,
            TableFooterView = new UIView(CoreGraphics.CGRect.Empty)
        };
        _tableView.SetEditing(true, false);
        _tableView.AllowsSelectionDuringEditing = true;
        _tableView.CellLayoutMarginsFollowReadableWidth = false;
        _source = new iOSNativeEmbeddedCheckableReorderListSource<TItem>(
            itemsProvider,
            titleSelector,
            subtitleSelector,
            isEnabledGetter,
            isEnabledSetter,
            moveAction,
            cellStyle,
            Refresh,
            rowHeight,
            rowHeightSelector,
            attributedSubtitleSelector,
            accessibilityValueSelector,
            showsDetailButton,
            detailAction,
            trailingAccessorySelector);
        _tableView.Source = _source;

        AddSubview(_tableView);
        _heightConstraint = _tableView.HeightAnchor.ConstraintEqualTo(1);

        NSLayoutConstraint.ActivateConstraints(
        [
            _tableView.TopAnchor.ConstraintEqualTo(TopAnchor),
            _tableView.LeadingAnchor.ConstraintEqualTo(LeadingAnchor),
            _tableView.TrailingAnchor.ConstraintEqualTo(TrailingAnchor),
            _tableView.BottomAnchor.ConstraintEqualTo(BottomAnchor),
            _heightConstraint,
        ]);

        Refresh();
    }

    /// <summary>
    /// 刷新列表内容与高度。
    /// </summary>
    public void Refresh()
    {
        _tableView.ReloadData();
        UpdateHeight();
    }

    /// <summary>
    /// 就地刷新可见行的内容、附件与行高（不重载数据、不复位滚动位置与单元格身份）。
    /// </summary>
    public void RefreshInPlace()
    {
        foreach (var cell in _tableView.VisibleCells)
        {
            if (_tableView.IndexPathForCell(cell) is { } indexPath && cell is iOSNativeEmbeddedCheckableReorderCell reorderCell)
                _source.ConfigureCell(reorderCell, indexPath);
        }
        UIView.PerformWithoutAnimation(() =>
        {
            _tableView.BeginUpdates();
            _tableView.EndUpdates();
        });
        UpdateHeight();
    }

    private void UpdateHeight()
    {
        var items = _itemsProvider();
        double height = 0;
        for (int index = 0; index < items.Count; index++) height += _source.HeightFor(items[index]);
        _heightConstraint.Constant = (nfloat)Math.Max(1, height);
    }
}

/// <summary>
/// iOS 内联可勾选可排序列表数据源。
/// </summary>
/// <typeparam name="TItem">列表项类型。</typeparam>
internal sealed class iOSNativeEmbeddedCheckableReorderListSource<TItem> : UITableViewSource
{
    private readonly Func<IReadOnlyList<TItem>> _itemsProvider;
    private readonly Func<TItem, string> _titleSelector;
    private readonly Func<TItem, string?> _subtitleSelector;
    private readonly Func<TItem, bool> _isEnabledGetter;
    private readonly Action<TItem, bool> _isEnabledSetter;
    private readonly Action<int, int> _moveAction;
    private readonly UITableViewCellStyle _cellStyle;
    private readonly Action _refreshAction;
    private readonly double _rowHeight;
    private readonly Func<TItem, double>? _rowHeightSelector;
    private readonly Func<TItem, NSAttributedString?>? _attributedSubtitleSelector;
    private readonly Func<TItem, string?>? _accessibilityValueSelector;
    private readonly Func<TItem, bool>? _showsDetailButton;
    private readonly Action<TItem>? _detailAction;
    private readonly Func<TItem, UIView?>? _trailingAccessorySelector;

    /// <summary>
    /// 初始化列表数据源。
    /// </summary>
    public iOSNativeEmbeddedCheckableReorderListSource(
        Func<IReadOnlyList<TItem>> itemsProvider,
        Func<TItem, string> titleSelector,
        Func<TItem, string?> subtitleSelector,
        Func<TItem, bool> isEnabledGetter,
        Action<TItem, bool> isEnabledSetter,
        Action<int, int> moveAction,
        UITableViewCellStyle cellStyle,
        Action refreshAction,
        double rowHeight,
        Func<TItem, double>? rowHeightSelector,
        Func<TItem, NSAttributedString?>? attributedSubtitleSelector,
        Func<TItem, string?>? accessibilityValueSelector,
        Func<TItem, bool>? showsDetailButton,
        Action<TItem>? detailAction,
        Func<TItem, UIView?>? trailingAccessorySelector)
    {
        _itemsProvider = itemsProvider;
        _titleSelector = titleSelector;
        _subtitleSelector = subtitleSelector;
        _isEnabledGetter = isEnabledGetter;
        _isEnabledSetter = isEnabledSetter;
        _moveAction = moveAction;
        _cellStyle = cellStyle;
        _refreshAction = refreshAction;
        _rowHeight = rowHeight;
        _rowHeightSelector = rowHeightSelector;
        _attributedSubtitleSelector = attributedSubtitleSelector;
        _accessibilityValueSelector = accessibilityValueSelector;
        _showsDetailButton = showsDetailButton;
        _detailAction = detailAction;
        _trailingAccessorySelector = trailingAccessorySelector;
    }

    /// <summary>指定项的行高。</summary>
    internal double HeightFor(TItem item) => _rowHeightSelector?.Invoke(item) ?? _rowHeight;

    /// <summary>
    /// 返回列表项数量。
    /// </summary>
    public override nint RowsInSection(UITableView tableView, nint section)
    {
        return _itemsProvider().Count;
    }

    /// <summary>
    /// 返回行高。
    /// </summary>
    public override nfloat GetHeightForRow(UITableView tableView, NSIndexPath indexPath)
    {
        return (nfloat)HeightFor(_itemsProvider()[indexPath.Row]);
    }

    /// <summary>
    /// 构建列表单元格。
    /// </summary>
    public override UITableViewCell GetCell(UITableView tableView, NSIndexPath indexPath)
    {
        var showsSubtitle = _cellStyle == UITableViewCellStyle.Subtitle;
        var reuseIdentifier = showsSubtitle
            ? "EmbeddedCheckableReorderSubtitleCell"
            : "EmbeddedCheckableReorderDefaultCell";
        var cell = tableView.DequeueReusableCell(reuseIdentifier) as iOSNativeEmbeddedCheckableReorderCell
            ?? new iOSNativeEmbeddedCheckableReorderCell(reuseIdentifier, showsSubtitle);
        ConfigureCell(cell, indexPath);
        return cell;
    }

    /// <summary>按当前数据刷新一个单元格（新建与就地刷新共用）。</summary>
    internal void ConfigureCell(iOSNativeEmbeddedCheckableReorderCell cell, NSIndexPath indexPath)
    {
        var showsSubtitle = _cellStyle == UITableViewCellStyle.Subtitle;
        var item = _itemsProvider()[indexPath.Row];
        cell.Configure(
            title: _titleSelector(item),
            subtitle: showsSubtitle ? _subtitleSelector(item) : null,
            isEnabled: _isEnabledGetter(item),
            attributedSubtitle: showsSubtitle ? _attributedSubtitleSelector?.Invoke(item) : null,
            accessibilityValue: _accessibilityValueSelector?.Invoke(item));
        cell.ShowsReorderControl = true;
        cell.BackgroundColor = UIColor.Clear;
        cell.EditingAccessory = _showsDetailButton?.Invoke(item) == true
            ? UITableViewCellAccessory.DetailButton
            : UITableViewCellAccessory.None;
        cell.SetTrailingAccessory(_trailingAccessorySelector?.Invoke(item));
    }

    /// <summary>
    /// 详情按钮（ⓘ）点击。
    /// </summary>
    public override void AccessoryButtonTapped(UITableView tableView, NSIndexPath indexPath)
    {
        _detailAction?.Invoke(_itemsProvider()[indexPath.Row]);
    }

    /// <summary>
    /// 允许所有行进入编辑态。
    /// </summary>
    public override bool CanEditRow(UITableView tableView, NSIndexPath indexPath)
    {
        return true;
    }

    /// <summary>
    /// 允许所有行拖拽排序。
    /// </summary>
    public override bool CanMoveRow(UITableView tableView, NSIndexPath indexPath)
    {
        return true;
    }

    /// <summary>
    /// 关闭删除样式，仅保留拖拽排序。
    /// </summary>
    public override UITableViewCellEditingStyle EditingStyleForRow(UITableView tableView, NSIndexPath indexPath)
    {
        return UITableViewCellEditingStyle.None;
    }

    /// <summary>
    /// 关闭编辑态下的左侧缩进，保持内容对齐。
    /// </summary>
    public override bool ShouldIndentWhileEditing(UITableView tableView, NSIndexPath indexPath)
    {
        return false;
    }

    /// <summary>
    /// 处理拖拽排序后的数据回写。
    /// </summary>
    public override void MoveRow(UITableView tableView, NSIndexPath sourceIndexPath, NSIndexPath destinationIndexPath)
    {
        _moveAction(sourceIndexPath.Row, destinationIndexPath.Row);
        _refreshAction();
    }

    /// <summary>
    /// 轻点切换当前行的勾选状态。
    /// </summary>
    public override void RowSelected(UITableView tableView, NSIndexPath indexPath)
    {
        tableView.DeselectRow(indexPath, true);
        var item = _itemsProvider()[indexPath.Row];
        _isEnabledSetter(item, !_isEnabledGetter(item));
        _refreshAction();
    }
}

/// <summary>
/// iOS 内联勾选排序列表单元格。
/// 左侧固定显示勾选位，右侧保留系统拖拽手柄；副标题为空时标题垂直居中。
/// </summary>
internal sealed class iOSNativeEmbeddedCheckableReorderCell : UITableViewCell
{
    private readonly UIImageView _checkmarkView;
    private readonly UILabel _titleLabel;
    private readonly UILabel? _subtitleLabel;
    private readonly NSLayoutConstraint[] _centeredConstraints = [];
    private readonly NSLayoutConstraint[] _stackedConstraints = [];
    private readonly NSLayoutConstraint _titleTrailingDefault;
    private NSLayoutConstraint? _titleTrailingAccessory;
    private NSLayoutConstraint[] _accessoryConstraints = [];
    private UIView? _trailingAccessory;

    /// <summary>行尾附加控件（控制栏功能行的主/次快捷键按钮组；验收读取）。</summary>
    internal UIView? TrailingAccessory => _trailingAccessory;

    /// <summary>标题标签（验收读取）。</summary>
    internal UILabel TitleLabel => _titleLabel;

    /// <summary>副标题标签；Default 样式为 null（验收读取）。</summary>
    internal UILabel? SubtitleLabel => _subtitleLabel;

    /// <summary>勾选标记（验收读取）。</summary>
    internal UIImageView CheckmarkView => _checkmarkView;

    /// <summary>
    /// 初始化列表单元格。
    /// </summary>
    /// <param name="reuseIdentifier">复用标识。</param>
    /// <param name="showsSubtitle">是否展示副标题。</param>
    public iOSNativeEmbeddedCheckableReorderCell(string reuseIdentifier, bool showsSubtitle)
        : base(UITableViewCellStyle.Default, reuseIdentifier)
    {
        BackgroundColor = UIColor.Clear;
        ContentView.BackgroundColor = UIColor.Clear;
        SelectionStyle = UITableViewCellSelectionStyle.Default;

        _checkmarkView = new UIImageView
        {
            TranslatesAutoresizingMaskIntoConstraints = false,
            TintColor = UIColor.SystemBlue,
            ContentMode = UIViewContentMode.ScaleAspectFit,
            Image = UIImage.GetSystemImage("checkmark")
        };

        _titleLabel = new UILabel
        {
            TranslatesAutoresizingMaskIntoConstraints = false,
            Font = UIFont.SystemFontOfSize(16),
            TextColor = UIColor.Label,
            Lines = 1,
            LineBreakMode = UILineBreakMode.TailTruncation
        };

        ContentView.AddSubview(_checkmarkView);
        ContentView.AddSubview(_titleLabel);

        UILabel? subtitleLabel = null;
        if (showsSubtitle)
        {
            subtitleLabel = new UILabel
            {
                TranslatesAutoresizingMaskIntoConstraints = false,
                Font = UIFont.SystemFontOfSize(12),
                TextColor = UIColor.SecondaryLabel,
                Lines = 1
            };
            ContentView.AddSubview(subtitleLabel);
        }

        _subtitleLabel = subtitleLabel;

        var leadingInset = 16f;
        var checkmarkWidth = 22f;
        var spacing = 12f;

        _checkmarkView.WidthAnchor.ConstraintEqualTo(checkmarkWidth).Active = true;
        _checkmarkView.HeightAnchor.ConstraintEqualTo(22).Active = true;
        _checkmarkView.LeadingAnchor.ConstraintEqualTo(ContentView.LeadingAnchor, leadingInset).Active = true;
        _checkmarkView.CenterYAnchor.ConstraintEqualTo(ContentView.CenterYAnchor).Active = true;
        _titleLabel.LeadingAnchor.ConstraintEqualTo(_checkmarkView.TrailingAnchor, spacing).Active = true;
        _titleTrailingDefault = _titleLabel.TrailingAnchor.ConstraintLessThanOrEqualTo(ContentView.TrailingAnchor, -16);
        _titleTrailingDefault.Active = true;

        _centeredConstraints = [_titleLabel.CenterYAnchor.ConstraintEqualTo(ContentView.CenterYAnchor)];
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

    /// <summary>
    /// 设置或移除行尾附加控件（控制栏功能行的主/次快捷键按钮组），标题在附加控件前尾部截断。
    /// </summary>
    /// <param name="accessory">附加控件；null 表示纯触控列表，不显示任何行尾控件。</param>
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

    /// <summary>
    /// 刷新单元格展示状态。
    /// </summary>
    /// <param name="title">主标题。</param>
    /// <param name="subtitle">副标题。</param>
    /// <param name="isEnabled">是否启用。</param>
    /// <param name="attributedSubtitle">带样式副标题（优先于纯文本）。</param>
    /// <param name="accessibilityValue">读屏值；null 时取副标题文本。</param>
    public void Configure(string title, string? subtitle, bool isEnabled,
        NSAttributedString? attributedSubtitle = null, string? accessibilityValue = null)
    {
        _titleLabel.Text = title;
        bool hasSubtitle = false;
        if (_subtitleLabel != null)
        {
            if (attributedSubtitle != null && attributedSubtitle.Length > 0)
            {
                _subtitleLabel.AttributedText = attributedSubtitle;
                hasSubtitle = true;
            }
            else
            {
                _subtitleLabel.AttributedText = null;
                _subtitleLabel.Text = subtitle;
                hasSubtitle = !string.IsNullOrWhiteSpace(subtitle);
            }
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
        AccessibilityValue = hasSubtitle ? accessibilityValue : null;
    }
}
