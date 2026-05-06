using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Foundation;
using PhotoViewer.Core.Settings;
using PhotoViewer.ViewModels;
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
        var presentingController = GetPresentingController();
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
            ConfigureSheetPresentation(navigationController.SheetPresentationController);
            GetTopMostPresentedViewController(presentingController).PresentViewController(navigationController, true, null);
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
    /// 获取当前可用于弹出设置页的宿主控制器。
    /// </summary>
    /// <returns>前台可见的控制器；没有时返回 null。</returns>
    private static UIViewController? GetPresentingController()
    {
        var window = UIApplication.SharedApplication
            .ConnectedScenes
            .OfType<UIWindowScene>()
            .Where(scene =>
                scene.ActivationState == UISceneActivationState.ForegroundActive ||
                scene.ActivationState == UISceneActivationState.ForegroundInactive)
            .SelectMany(scene => scene.Windows)
            .FirstOrDefault(window => window.IsKeyWindow)
            ?? UIApplication.SharedApplication
                .ConnectedScenes
                .OfType<UIWindowScene>()
                .SelectMany(scene => scene.Windows)
                .FirstOrDefault();

        return window?.RootViewController;
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
                navigationController.ViewControllers.FirstOrDefault() is iOSNativeSettingsRootViewController)
            {
                return navigationController;
            }

            current = current.PresentedViewController;
        }

        return null;
    }

    /// <summary>
    /// 获取当前展示链顶部的控制器。
    /// </summary>
    /// <param name="controller">起始控制器。</param>
    /// <returns>当前最顶层的控制器。</returns>
    private static UIViewController GetTopMostPresentedViewController(UIViewController controller)
    {
        var current = controller;
        while (current.PresentedViewController != null)
        {
            current = current.PresentedViewController;
        }

        return current switch
        {
            UINavigationController navigationController => navigationController.VisibleViewController ?? navigationController,
            UITabBarController tabBarController => tabBarController.SelectedViewController ?? tabBarController,
            _ => current,
        };
    }

    /// <summary>
    /// 配置 iOS 半屏浮窗的 detents 与交互参数。
    /// </summary>
    /// <param name="sheetPresentationController">系统 Sheet 控制器。</param>
    private static void ConfigureSheetPresentation(UISheetPresentationController? sheetPresentationController)
    {
        if (sheetPresentationController == null)
        {
            return;
        }

        var detents = CreateDefaultDetents();
        if (detents.Length > 0)
        {
            sheetPresentationController.Detents = detents;
        }

        sheetPresentationController.PrefersGrabberVisible = true;
        sheetPresentationController.PrefersScrollingExpandsWhenScrolledToEdge = false;
        sheetPresentationController.PreferredCornerRadius = 24;
    }

    /// <summary>
    /// 通过反射兼容不同 .NET iOS 绑定版本的 detent API。
    /// </summary>
    /// <returns>可用的默认 detent 数组。</returns>
    private static UISheetPresentationControllerDetent[] CreateDefaultDetents()
    {
        var detentType = typeof(UISheetPresentationControllerDetent);
        var medium = CreateDetent(detentType, "MediumDetent", "Medium", "CreateMediumDetent");
        var large = CreateDetent(detentType, "LargeDetent", "Large", "CreateLargeDetent");

        return new[] { medium, large }.OfType<UISheetPresentationControllerDetent>().ToArray();
    }

    /// <summary>
    /// 通过反射创建指定名称的 detent。
    /// </summary>
    /// <param name="detentType">detent 类型。</param>
    /// <param name="propertyName">候选属性名。</param>
    /// <param name="methodName">候选方法名一。</param>
    /// <param name="factoryMethodName">候选方法名二。</param>
    /// <returns>成功时返回 detent，否则返回 null。</returns>
    private static UISheetPresentationControllerDetent? CreateDetent(
        Type detentType,
        string propertyName,
        string methodName,
        string factoryMethodName)
    {
        var property = detentType.GetProperty(propertyName, BindingFlags.Public | BindingFlags.Static);
        if (property?.GetValue(null) is UISheetPresentationControllerDetent propertyDetent)
        {
            return propertyDetent;
        }

        var method = detentType.GetMethod(
                methodName,
                BindingFlags.Public | BindingFlags.Static,
                binder: null,
                types: Type.EmptyTypes,
                modifiers: null)
            ?? detentType.GetMethod(
                factoryMethodName,
                BindingFlags.Public | BindingFlags.Static,
                binder: null,
                types: Type.EmptyTypes,
                modifiers: null);
        return method?.Invoke(null, null) as UISheetPresentationControllerDetent;
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
    /// 初始化原生设置编辑会话。
    /// </summary>
    /// <param name="source">共享层当前生效的设置实例。</param>
    public iOSNativeSettingsSession(SettingsViewModel source)
    {
        _source = source;
        WorkingSettings = new SettingsViewModel(
            settingsService: new iOSNativeTransientSettingsService(),
            initialModel: source.CreateSnapshot());
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
    /// 页面加载后挂接系统 dismiss 回调。
    /// </summary>
    public override void ViewDidLoad()
    {
        base.ViewDidLoad();
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

    private readonly (string Title, string Subtitle, Func<UIViewController> Factory)[] _items;

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
            ("控制", "布局、控制栏显示与快捷键只读查看", () => new iOSNativeControlSettingsViewController(_settings)),
            ("EXIF", "EXIF 显示项与评分写回策略", () => new iOSNativeExifSettingsViewController(_settings)),
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
        TableView.CellLayoutMarginsFollowReadableWidth = false;
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
        cell.TextLabel.Text = item.Title;
        cell.DetailTextLabel.Text = item.Subtitle;
        cell.DetailTextLabel.TextColor = UIColor.SecondaryLabel;
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
        View.BackgroundColor = UIColor.SystemGroupedBackground;
        ConfigureLayout();
        RebuildContent();
    }

    /// <summary>
    /// 页面重新出现时刷新表单内容，保证动态列表与说明文字为最新状态。
    /// </summary>
    /// <param name="animated">系统动画标记。</param>
    public override void ViewWillAppear(bool animated)
    {
        base.ViewWillAppear(animated);
        RebuildContent();
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
        _contentStack.TranslatesAutoresizingMaskIntoConstraints = false;
        _contentStack.Axis = UILayoutConstraintAxis.Vertical;
        _contentStack.Spacing = 20;
        _contentStack.LayoutMarginsRelativeArrangement = true;
        _contentStack.LayoutMargins = new UIEdgeInsets(16, 16, 24, 16);

        View.AddSubview(_scrollView);
        _scrollView.AddSubview(_contentStack);

        NSLayoutConstraint.ActivateConstraints(
        [
            _scrollView.TopAnchor.ConstraintEqualTo(View.SafeAreaLayoutGuide.TopAnchor),
            _scrollView.LeadingAnchor.ConstraintEqualTo(View.LeadingAnchor),
            _scrollView.TrailingAnchor.ConstraintEqualTo(View.TrailingAnchor),
            _scrollView.BottomAnchor.ConstraintEqualTo(View.BottomAnchor),

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

    /// <summary>
    /// 创建缩放比例预设编辑器。
    /// </summary>
    /// <returns>可直接编辑的原生列表。</returns>
    private UIView CreateScalePresetEditor()
    {
        var stack = new UIStackView
        {
            Axis = UILayoutConstraintAxis.Vertical,
            Spacing = 0,
            TranslatesAutoresizingMaskIntoConstraints = false
        };

        for (var index = 0; index < Settings.ScalePresets.Count; index++)
        {
            if (index > 0)
            {
                stack.AddArrangedSubview(CreateSeparator());
            }

            stack.AddArrangedSubview(CreateScalePresetRow(Settings.ScalePresets[index]));
        }

        if (Settings.ScalePresets.Count > 0)
        {
            stack.AddArrangedSubview(CreateSeparator());
        }

        stack.AddArrangedSubview(CreateAddScalePresetRow());
        return stack;
    }

    /// <summary>
    /// 创建单个缩放比例预设行。
    /// </summary>
    /// <param name="preset">当前预设。</param>
    /// <returns>可编辑行。</returns>
    private UIView CreateScalePresetRow(SettingsViewModel.ScalePreset preset)
    {
        var content = new UIStackView
        {
            Axis = UILayoutConstraintAxis.Horizontal,
            Alignment = UIStackViewAlignment.Center,
            Spacing = 12,
            TranslatesAutoresizingMaskIntoConstraints = false
        };

        var textField = new UITextField
        {
            Text = preset.Text,
            KeyboardType = UIKeyboardType.DecimalPad,
            BorderStyle = UITextBorderStyle.RoundedRect,
            TextAlignment = UITextAlignment.Center,
            TranslatesAutoresizingMaskIntoConstraints = false
        };
        textField.WidthAnchor.ConstraintEqualTo(96).Active = true;
        textField.InputAccessoryView = CreateDoneToolbar(textField);

        void ApplyText()
        {
            if (TryApplyScalePresetValue(textField.Text ?? string.Empty, preset, textField))
            {
                RebuildContent();
                return;
            }

            textField.Text = preset.Text;
        }

        textField.EditingDidEnd += (_, _) => ApplyText();

        var percentLabel = new UILabel
        {
            Text = "%",
            Font = UIFont.SystemFontOfSize(16, UIFontWeight.Medium),
            TextColor = UIColor.SecondaryLabel,
            TranslatesAutoresizingMaskIntoConstraints = false
        };

        var displayLabel = CreateTrailingValueLabel();
        displayLabel.Text = preset.Display;

        var deleteButton = new UIButton(UIButtonType.System)
        {
            TranslatesAutoresizingMaskIntoConstraints = false
        };
        deleteButton.SetTitle("-", UIControlState.Normal);
        deleteButton.TitleLabel.Font = UIFont.SystemFontOfSize(22, UIFontWeight.Semibold);
        deleteButton.TouchUpInside += (_, _) =>
        {
            Settings.RemoveScalePreset(preset);
            RebuildContent();
        };

        content.AddArrangedSubview(textField);
        content.AddArrangedSubview(percentLabel);
        content.AddArrangedSubview(displayLabel);
        content.AddArrangedSubview(CreateFlexibleSpacer());
        content.AddArrangedSubview(deleteButton);

        return CreateCustomRow(content);
    }

    /// <summary>
    /// 创建新增缩放比例行。
    /// </summary>
    /// <returns>新增按钮行。</returns>
    private UIView CreateAddScalePresetRow()
    {
        var content = new UIStackView
        {
            Axis = UILayoutConstraintAxis.Horizontal,
            Alignment = UIStackViewAlignment.Center,
            Spacing = 12,
            TranslatesAutoresizingMaskIntoConstraints = false
        };

        var addButton = new UIButton(UIButtonType.System)
        {
            TranslatesAutoresizingMaskIntoConstraints = false
        };
        addButton.SetTitle("+", UIControlState.Normal);
        addButton.TitleLabel.Font = UIFont.SystemFontOfSize(22, UIFontWeight.Semibold);
        addButton.TouchUpInside += (_, _) =>
        {
            Settings.AddScalePreset();
            RebuildContent();
        };

        content.AddArrangedSubview(CreateFlexibleSpacer());
        content.AddArrangedSubview(addButton);
        return CreateCustomRow(content);
    }

    /// <summary>
    /// 将文本输入应用到缩放预设。
    /// </summary>
    /// <param name="text">用户输入的百分比文本。</param>
    /// <param name="preset">目标预设。</param>
    /// <param name="textFieldResponder">校验失败后重新聚焦的输入框。</param>
    /// <returns>应用成功返回 true，否则返回 false。</returns>
    private bool TryApplyScalePresetValue(string text, SettingsViewModel.ScalePreset preset, UIView textFieldResponder)
    {
        if (!TryParsePercentage(text, out var normalizedText))
        {
            PresentValidationAlert("请输入大于 0 的数字，例如 100 或 66.7。", textFieldResponder: textFieldResponder);
            return false;
        }

        preset.Text = normalizedText;
        Settings.ApplyScalePreset();
        return true;
    }

    /// <summary>
    /// 解析百分比输入并标准化字符串。
    /// </summary>
    /// <param name="text">原始文本。</param>
    /// <param name="normalizedText">标准化后的文本。</param>
    /// <returns>解析成功返回 true，否则返回 false。</returns>
    private static bool TryParsePercentage(string text, out string normalizedText)
    {
        normalizedText = string.Empty;
        if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var invariantValue) && invariantValue > 0)
        {
            normalizedText = invariantValue.ToString("0.###", CultureInfo.InvariantCulture);
            return true;
        }

        if (double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out var localValue) && localValue > 0)
        {
            normalizedText = localValue.ToString("0.###", CultureInfo.InvariantCulture);
            return true;
        }

        return false;
    }

    /// <summary>
    /// 弹出输入校验失败提示。
    /// </summary>
    /// <param name="message">提示内容。</param>
    /// <param name="textFieldResponder">关闭后重新聚焦的目标视图。</param>
    private void PresentValidationAlert(string message, UIView textFieldResponder)
    {
        var alert = UIAlertController.Create("输入无效", message, UIAlertControllerStyle.Alert);
        alert.AddAction(UIAlertAction.Create("确定", UIAlertActionStyle.Default, _ => textFieldResponder.BecomeFirstResponder()));
        PresentViewController(alert, true, null);
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
/// iOS 原生控制设置页。
/// </summary>
internal sealed class iOSNativeControlSettingsViewController : iOSNativeSettingsFormViewController
{
    /// <summary>
    /// 初始化控制设置页。
    /// </summary>
    /// <param name="settings">共享设置 ViewModel。</param>
    public iOSNativeControlSettingsViewController(SettingsViewModel settings)
        : base(settings)
    {
    }

    /// <summary>
    /// 构建控制设置内容。
    /// </summary>
    /// <param name="contentStack">页面主内容栈。</param>
    protected override void BuildContent(UIStackView contentStack)
    {
        Title = "控制";
        contentStack.AddArrangedSubview(
            CreateSection(
                "控制栏布局位置",
                null,
                CreateNavigationRow(
                    "布局模式",
                    null,
                    GetLayoutModeDisplayName(Settings.LayoutMode),
                    PresentLayoutModePicker)));

        contentStack.AddArrangedSubview(
            CreateSection(
                "控制栏功能",
                null,
                new iOSNativeEmbeddedCheckableReorderListView<SettingsViewModel.HotkeyItem>(
                    itemsProvider: () => Settings.Hotkeys,
                    titleSelector: item => item.Name,
                    subtitleSelector: _ => null,
                    isEnabledGetter: item => item.IsDisplay,
                    isEnabledSetter: (item, value) => item.IsDisplay = value,
                    moveAction: Settings.MoveHotkey,
                    rowHeight: 52,
                    cellStyle: UITableViewCellStyle.Default)));
    }

    /// <summary>
    /// 弹出布局模式选择对话框。
    /// </summary>
    private void PresentLayoutModePicker()
    {
        var alert = UIAlertController.Create("布局模式", null, UIAlertControllerStyle.ActionSheet);
        foreach (var item in Settings.LayoutModes)
        {
            alert.AddAction(UIAlertAction.Create(item.DisplayName, UIAlertActionStyle.Default, _ => Settings.LayoutMode = item.Value));
        }

        alert.AddAction(UIAlertAction.Create("取消", UIAlertActionStyle.Cancel, null));

        if (alert.PopoverPresentationController != null)
        {
            alert.PopoverPresentationController.SourceView = View;
            alert.PopoverPresentationController.SourceRect = new CoreGraphics.CGRect(
                View.Bounds.X + (View.Bounds.Width / 2),
                View.Bounds.Y + (View.Bounds.Height / 2),
                1,
                1);
        }

        PresentViewController(alert, true, null);
    }

    /// <summary>
    /// 获取布局模式的当前展示名称。
    /// </summary>
    /// <param name="layoutMode">当前布局模式。</param>
    /// <returns>用于右侧摘要展示的文本。</returns>
    private string GetLayoutModeDisplayName(LayoutMode layoutMode)
    {
        return Settings.LayoutModes.FirstOrDefault(item => item.Value == layoutMode)?.DisplayName ?? layoutMode.ToString();
    }
}

/// <summary>
/// iOS 内联可勾选可排序列表视图。
/// </summary>
/// <typeparam name="TItem">列表项类型。</typeparam>
internal sealed class iOSNativeEmbeddedCheckableReorderListView<TItem> : UIView
{
    private readonly Func<IReadOnlyList<TItem>> _itemsProvider;
    private readonly UITableView _tableView;
    private readonly NSLayoutConstraint _heightConstraint;

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
        UITableViewCellStyle cellStyle = UITableViewCellStyle.Subtitle)
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
        _tableView.Source = new iOSNativeEmbeddedCheckableReorderListSource<TItem>(
            itemsProvider,
            titleSelector,
            subtitleSelector,
            isEnabledGetter,
            isEnabledSetter,
            moveAction,
            cellStyle,
            Refresh);

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
        _heightConstraint.Constant = Math.Max(1, _itemsProvider().Count) * _tableView.RowHeight;
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
        Action refreshAction)
    {
        _itemsProvider = itemsProvider;
        _titleSelector = titleSelector;
        _subtitleSelector = subtitleSelector;
        _isEnabledGetter = isEnabledGetter;
        _isEnabledSetter = isEnabledSetter;
        _moveAction = moveAction;
        _cellStyle = cellStyle;
        _refreshAction = refreshAction;
    }

    /// <summary>
    /// 返回列表项数量。
    /// </summary>
    public override nint RowsInSection(UITableView tableView, nint section)
    {
        return _itemsProvider().Count;
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

        var item = _itemsProvider()[indexPath.Row];
        cell.Configure(
            title: _titleSelector(item),
            subtitle: showsSubtitle ? _subtitleSelector(item) : null,
            isEnabled: _isEnabledGetter(item));
        cell.ShowsReorderControl = true;
        cell.BackgroundColor = UIColor.Clear;
        return cell;
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
/// 左侧固定显示勾选位，右侧保留系统拖拽手柄。
/// </summary>
internal sealed class iOSNativeEmbeddedCheckableReorderCell : UITableViewCell
{
    private readonly UIImageView _checkmarkView;
    private readonly UILabel _titleLabel;
    private readonly UILabel? _subtitleLabel;

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
            Lines = 1
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

        if (_subtitleLabel == null)
        {
            _titleLabel.LeadingAnchor.ConstraintEqualTo(_checkmarkView.TrailingAnchor, spacing).Active = true;
            _titleLabel.TrailingAnchor.ConstraintLessThanOrEqualTo(ContentView.TrailingAnchor, -16).Active = true;
            _titleLabel.CenterYAnchor.ConstraintEqualTo(ContentView.CenterYAnchor).Active = true;
        }
        else
        {
            _titleLabel.LeadingAnchor.ConstraintEqualTo(_checkmarkView.TrailingAnchor, spacing).Active = true;
            _titleLabel.TrailingAnchor.ConstraintLessThanOrEqualTo(ContentView.TrailingAnchor, -16).Active = true;
            _titleLabel.TopAnchor.ConstraintEqualTo(ContentView.TopAnchor, 10).Active = true;

            _subtitleLabel.LeadingAnchor.ConstraintEqualTo(_titleLabel.LeadingAnchor).Active = true;
            _subtitleLabel.TrailingAnchor.ConstraintLessThanOrEqualTo(ContentView.TrailingAnchor, -16).Active = true;
            _subtitleLabel.TopAnchor.ConstraintEqualTo(_titleLabel.BottomAnchor, 2).Active = true;
            _subtitleLabel.BottomAnchor.ConstraintLessThanOrEqualTo(ContentView.BottomAnchor, -10).Active = true;
        }
    }

    /// <summary>
    /// 刷新单元格展示状态。
    /// </summary>
    /// <param name="title">主标题。</param>
    /// <param name="subtitle">副标题。</param>
    /// <param name="isEnabled">是否启用。</param>
    public void Configure(string title, string? subtitle, bool isEnabled)
    {
        _titleLabel.Text = title;
        if (_subtitleLabel != null)
        {
            _subtitleLabel.Text = subtitle;
            _subtitleLabel.Hidden = string.IsNullOrWhiteSpace(subtitle);
        }

        _checkmarkView.Hidden = !isEnabled;
    }
}
