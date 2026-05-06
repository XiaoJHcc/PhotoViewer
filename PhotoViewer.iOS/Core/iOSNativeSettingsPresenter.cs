using System;
using System.Globalization;
using System.Linq;
using System.Reflection;
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

            var rootController = new iOSNativeSettingsRootViewController(settings);
            var navigationController = new UINavigationController(rootController)
            {
                ModalPresentationStyle = UIModalPresentationStyle.PageSheet
            };

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

        return [.. new UISheetPresentationControllerDetent?[] { medium, large }.OfType<UISheetPresentationControllerDetent>()];
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
/// iOS 原生设置页根控制器。
/// 负责展示一级分组入口并提供关闭按钮。
/// </summary>
internal sealed class iOSNativeSettingsRootViewController : UITableViewController
{
    private readonly SettingsViewModel _settings;

    private readonly (string Title, string Subtitle, Func<UIViewController> Factory)[] _items;

    /// <summary>
    /// 初始化原生设置页根控制器。
    /// </summary>
    /// <param name="settings">共享设置 ViewModel。</param>
    public iOSNativeSettingsRootViewController(SettingsViewModel settings)
        : base(UITableViewStyle.InsetGrouped)
    {
        _settings = settings;
        _items =
        [
            ("文件", "同名合并、文件格式与缓存预加载", () => new iOSNativeFileSettingsViewController(_settings)),
            ("预览", "缩放指示器与缩放比例预设", () => new iOSNativePreviewSettingsViewController(_settings)),
            ("控制", "热键录制与排序拖拽后续补齐", () => new iOSNativeControlSettingsPlaceholderViewController(_settings)),
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
        NavigationItem.RightBarButtonItem = new UIBarButtonItem(UIBarButtonSystemItem.Close, (_, _) => DismissViewController(true, null));
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
        cell.DetailTextLabel.TextColor = UIColor.SecondaryLabelColor;
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
        View.BackgroundColor = UIColor.SystemGroupedBackgroundColor;
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

        var titleLabel = CreateSectionTitleLabel(title);
        sectionStack.AddArrangedSubview(titleLabel);
        sectionStack.AddArrangedSubview(CreateCard(rows));

        if (!string.IsNullOrWhiteSpace(footer))
        {
            sectionStack.AddArrangedSubview(CreateSectionFooterLabel(footer));
        }

        return sectionStack;
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
            TextColor = UIColor.TertiaryLabelColor,
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
    private UIView CreateCard(params UIView[] rows)
    {
        var card = new UIView
        {
            BackgroundColor = UIColor.SecondarySystemGroupedBackgroundColor,
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
            TextColor = UIColor.SecondaryLabelColor,
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
            TextColor = UIColor.SecondaryLabelColor,
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
            TextColor = UIColor.LabelColor,
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
            TextColor = UIColor.SecondaryLabelColor,
            Lines = 0,
            TranslatesAutoresizingMaskIntoConstraints = false
        };
    }

    /// <summary>
    /// 创建右侧数值标签。
    /// </summary>
    /// <returns>数值展示标签。</returns>
    private static UILabel CreateTrailingValueLabel()
    {
        return new UILabel
        {
            Font = UIFont.MonospacedDigitSystemFontOfSize(14, UIFontWeight.Medium),
            TextColor = UIColor.SecondaryLabelColor,
            Lines = 1,
            TranslatesAutoresizingMaskIntoConstraints = false
        };
    }

    /// <summary>
    /// 创建可伸缩空白占位视图。
    /// </summary>
    /// <returns>用于把右侧控件推向边缘的占位视图。</returns>
    private static UIView CreateFlexibleSpacer()
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
    private static UIView CreateSeparator()
    {
        var separator = new UIView
        {
            BackgroundColor = UIColor.SeparatorColor,
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
            TextColor = UIColor.SecondaryLabelColor,
            Lines = 0,
            TranslatesAutoresizingMaskIntoConstraints = false
        };
        var cacheInfo = new UILabel
        {
            Text = Settings.BitmapCacheCountInfo,
            Font = UIFont.SystemFontOfSize(12),
            TextColor = UIColor.SecondaryLabelColor,
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
                "文件行为",
                null,
                CreateSwitchRow(
                    "同名文件视为同一张图片",
                    "按优先级选择代表图显示，并在标星时同步修改同名文件。",
                    () => Settings.SameNameAsOnePhoto,
                    value => Settings.SameNameAsOnePhoto = value)));

        contentStack.AddArrangedSubview(
            CreateSection(
                "文件格式",
                "第一阶段先支持启用/停用，排序后置。",
                [.. Settings.FileFormats.Select(item =>
                    CreateSwitchRow(
                        item.DisplayName,
                        item.ExtensionsText,
                        () => item.IsEnabled,
                        value => item.IsEnabled = value))]));

        contentStack.AddArrangedSubview(
            CreateSection(
                "缓存与预加载",
                null,
                CreateStepperRow(
                    "原生解码线程",
                    "影响 HEIF 等原生路径的后台解码并发。",
                    () => Settings.NativePreloadParallelism,
                    value => Settings.NativePreloadParallelism = value,
                    () => 1,
                    () => Settings.NativePreloadParallelismMaximum,
                    valueFormatter: value => $"{value} 线程"),
                CreateStepperRow(
                    "软件解码线程",
                    "影响托管路径的后台解码并发。",
                    () => Settings.CpuPreloadParallelism,
                    value => Settings.CpuPreloadParallelism = value,
                    () => 1,
                    () => Settings.CpuPreloadParallelismMaximum,
                    valueFormatter: value => $"{value} 线程"),
                CreateSliderRow(
                    "缓存内存上限",
                    "iOS 内存更敏感，过高可能触发系统内存告警。",
                    () => Settings.BitmapCacheMaxMemory,
                    value => Settings.BitmapCacheMaxMemory = value,
                    512,
                    32768,
                    valueFormatter: value => $"{value} MB",
                    onValueChanged: RefreshInfoLabels,
                    onEditingCompleted: RebuildContent),
                CreateSliderRow(
                    "缓存数量上限",
                    "缓存数量改变后会同步重算预载数量上限。",
                    () => Settings.BitmapCacheMaxCount,
                    value => Settings.BitmapCacheMaxCount = value,
                    1,
                    400,
                    valueFormatter: value => $"{value} 张",
                    onValueChanged: RefreshInfoLabels,
                    onEditingCompleted: RebuildContent),
                CreateStepperRow(
                    "下一张预载数量",
                    "进入下一张时优先预读前方邻居图像。",
                    () => Settings.PreloadForwardCount,
                    value => Settings.PreloadForwardCount = value,
                    () => 0,
                    () => Settings.PreloadMaximum,
                    valueFormatter: value => $"{value} 张"),
                CreateStepperRow(
                    "上一张预载数量",
                    "返回上一张时优先命中后方缓存。",
                    () => Settings.PreloadBackwardCount,
                    value => Settings.PreloadBackwardCount = value,
                    () => 0,
                    () => Settings.PreloadMaximum,
                    valueFormatter: value => $"{value} 张"),
                CreateStepperRow(
                    "列表滚动预载数量",
                    "缩略图快速滚动时补充中心区域的预载。",
                    () => Settings.VisibleCenterPreloadCount,
                    value => Settings.VisibleCenterPreloadCount = value,
                    () => 0,
                    () => Settings.PreloadMaximum,
                    valueFormatter: value => $"{value} 张"),
                CreateSliderRow(
                    "滚动预载延时",
                    "延时越高，滚动期间触发预载越保守。",
                    () => Settings.VisibleCenterDelayMs,
                    value => Settings.VisibleCenterDelayMs = value,
                    100,
                    5000,
                    valueFormatter: value => $"{value} ms"))));

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
                    "在图片角落显示当前画面位置与缩放状态。",
                    () => Settings.ShowZoomIndicator,
                    value => Settings.ShowZoomIndicator = value)));

        contentStack.AddArrangedSubview(
            CreateSection(
                "缩放比例预设",
                string.Join(" / ", Settings.ScalePresets.Select(item => item.Display)),
                CreateNavigationRow(
                    "管理缩放比例预设",
                    "支持新增、修改、删除，并在保存时自动排序。",
                    $"{Settings.ScalePresets.Count} 项",
                    () => NavigationController?.PushViewController(new iOSNativeScalePresetListViewController(Settings), true))));
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
                "EXIF 显示项",
                "第一阶段先支持启用/停用，排序后置。",
                [.. Settings.ExifDisplayItems.Select(item =>
                    CreateSwitchRow(
                        item.DisplayName,
                        null,
                        () => item.IsEnabled,
                        value => item.IsEnabled = value))]));

        contentStack.AddArrangedSubview(
            CreateSection(
                "评分与写回",
                null,
                CreateSwitchRow(
                    "显示星级评分",
                    "控制主界面与侧栏中的评分显示。",
                    () => Settings.ShowRating,
                    value => Settings.ShowRating = value),
                CreateSwitchRow(
                    "安全修改文件星级",
                    "写回前校验文件变化，降低原文件损坏风险。",
                    () => Settings.SafeSetRating,
                    value => Settings.SafeSetRating = value))));
    }
}

/// <summary>
/// iOS 原生控制设置占位页。
/// </summary>
internal sealed class iOSNativeControlSettingsPlaceholderViewController : iOSNativeSettingsFormViewController
{
    /// <summary>
    /// 初始化控制设置占位页。
    /// </summary>
    /// <param name="settings">共享设置 ViewModel。</param>
    public iOSNativeControlSettingsPlaceholderViewController(SettingsViewModel settings)
        : base(settings)
    {
    }

    /// <summary>
    /// 构建控制设置占位内容。
    /// </summary>
    /// <param name="contentStack">页面主内容栈。</param>
    protected override void BuildContent(UIStackView contentStack)
    {
        Title = "控制";
        contentStack.AddArrangedSubview(
            CreateSection(
                "后续规划",
                "iOS 第一阶段先不复刻热键录制与拖拽排序，后续再补原生交互。",
                CreateStaticInfoRow(
                    "当前状态",
                    "本页先保留信息架构占位，避免与现有 Avalonia 四分页结构完全耦合。")));
    }
}

/// <summary>
/// iOS 原生缩放比例预设列表页。
/// </summary>
internal sealed class iOSNativeScalePresetListViewController : UITableViewController
{
    private readonly SettingsViewModel _settings;

    /// <summary>
    /// 初始化缩放比例预设列表页。
    /// </summary>
    /// <param name="settings">共享设置 ViewModel。</param>
    public iOSNativeScalePresetListViewController(SettingsViewModel settings)
        : base(UITableViewStyle.InsetGrouped)
    {
        _settings = settings;
    }

    /// <summary>
    /// 初始化导航栏与新增按钮。
    /// </summary>
    public override void ViewDidLoad()
    {
        base.ViewDidLoad();
        Title = "缩放比例预设";
        NavigationItem.RightBarButtonItem = new UIBarButtonItem(UIBarButtonSystemItem.Add, (_, _) => PresentAddPresetAlert());
    }

    /// <summary>
    /// 页面出现时刷新列表。
    /// </summary>
    /// <param name="animated">系统动画标记。</param>
    public override void ViewWillAppear(bool animated)
    {
        base.ViewWillAppear(animated);
        TableView.ReloadData();
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
    /// 返回缩放预设数量。
    /// </summary>
    /// <param name="tableView">当前表格。</param>
    /// <param name="section">分组索引。</param>
    /// <returns>预设数量。</returns>
    public override nint RowsInSection(UITableView tableView, nint section)
    {
        return _settings.ScalePresets.Count;
    }

    /// <summary>
    /// 构建缩放预设单元格。
    /// </summary>
    /// <param name="tableView">当前表格。</param>
    /// <param name="indexPath">目标索引。</param>
    /// <returns>配置完成的原生单元格。</returns>
    public override UITableViewCell GetCell(UITableView tableView, NSIndexPath indexPath)
    {
        const string reuseIdentifier = "ScalePresetCell";
        var cell = tableView.DequeueReusableCell(reuseIdentifier)
            ?? new UITableViewCell(UITableViewCellStyle.Subtitle, reuseIdentifier);

        var preset = _settings.ScalePresets[indexPath.Row];
        cell.TextLabel.Text = preset.Display;
        cell.DetailTextLabel.Text = "点击修改，左滑删除";
        cell.DetailTextLabel.TextColor = UIColor.SecondaryLabelColor;
        cell.Accessory = UITableViewCellAccessory.DisclosureIndicator;
        return cell;
    }

    /// <summary>
    /// 允许缩放预设行进入删除编辑态。
    /// </summary>
    /// <param name="tableView">当前表格。</param>
    /// <param name="indexPath">目标索引。</param>
    /// <returns>始终返回 true。</returns>
    public override bool CanEditRow(UITableView tableView, NSIndexPath indexPath)
    {
        return true;
    }

    /// <summary>
    /// 处理缩放预设删除。
    /// </summary>
    /// <param name="tableView">当前表格。</param>
    /// <param name="editingStyle">编辑动作。</param>
    /// <param name="indexPath">目标索引。</param>
    public override void CommitEditingStyle(UITableView tableView, UITableViewCellEditingStyle editingStyle, NSIndexPath indexPath)
    {
        if (editingStyle != UITableViewCellEditingStyle.Delete)
        {
            return;
        }

        var preset = _settings.ScalePresets[indexPath.Row];
        _settings.RemoveScalePreset(preset);
        tableView.ReloadData();
    }

    /// <summary>
    /// 响应列表点击并打开编辑对话框。
    /// </summary>
    /// <param name="tableView">当前表格。</param>
    /// <param name="indexPath">被点击的索引。</param>
    public override void RowSelected(UITableView tableView, NSIndexPath indexPath)
    {
        tableView.DeselectRow(indexPath, true);
        PresentEditPresetAlert(_settings.ScalePresets[indexPath.Row]);
    }

    /// <summary>
    /// 弹出新增缩放预设对话框。
    /// </summary>
    private void PresentAddPresetAlert()
    {
        PresentPresetEditor("新增缩放比例", "100", text => TryApplyPresetValue(text, null));
    }

    /// <summary>
    /// 弹出编辑缩放预设对话框。
    /// </summary>
    /// <param name="preset">当前预设项。</param>
    private void PresentEditPresetAlert(SettingsViewModel.ScalePreset preset)
    {
        PresentPresetEditor("编辑缩放比例", preset.Text, text => TryApplyPresetValue(text, preset));
    }

    /// <summary>
    /// 弹出预设编辑对话框。
    /// </summary>
    /// <param name="title">对话框标题。</param>
    /// <param name="defaultText">默认输入值。</param>
    /// <param name="onConfirm">确认回调。</param>
    private void PresentPresetEditor(string title, string defaultText, Action<string> onConfirm)
    {
        var alert = UIAlertController.Create(title, "请输入百分比数值，例如 100 或 66.7。", UIAlertControllerStyle.Alert);
        alert.AddTextField(textField =>
        {
            textField.Text = defaultText;
            textField.Placeholder = "百分比";
            textField.KeyboardType = UIKeyboardType.DecimalPad;
        });
        alert.AddAction(UIAlertAction.Create("取消", UIAlertActionStyle.Cancel, null));
        alert.AddAction(UIAlertAction.Create("保存", UIAlertActionStyle.Default, _ =>
        {
            var text = alert.TextFields?.FirstOrDefault()?.Text ?? string.Empty;
            onConfirm(text);
        }));
        PresentViewController(alert, true, null);
    }

    /// <summary>
    /// 将文本输入应用到缩放预设。
    /// </summary>
    /// <param name="text">用户输入的百分比文本。</param>
    /// <param name="preset">现有预设；新增时为 null。</param>
    private void TryApplyPresetValue(string text, SettingsViewModel.ScalePreset? preset)
    {
        if (!TryParsePercentage(text, out var normalizedText))
        {
            PresentValidationAlert("请输入大于 0 的数字，例如 100 或 66.7。");
            return;
        }

        if (preset == null)
        {
            _settings.AddScalePreset();
            preset = _settings.ScalePresets.Last();
        }

        preset.Text = normalizedText;
        _settings.ApplyScalePreset();
        TableView.ReloadData();
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
    private void PresentValidationAlert(string message)
    {
        var alert = UIAlertController.Create("输入无效", message, UIAlertControllerStyle.Alert);
        alert.AddAction(UIAlertAction.Create("确定", UIAlertActionStyle.Default, null));
        PresentViewController(alert, true, null);
    }
}
