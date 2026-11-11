using CoreGraphics;
using PhotoViewer.Core.Design;
using PhotoViewer.ViewModels.Main;
using PhotoViewer.ViewModels.Main.File;
using UIKit;

namespace PhotoViewer.iOS.Native;

/// <summary>
/// 宽形态筛选控件（FILTER-07）：行布局为交互式玻璃胶囊内的三段原生弹出按钮（排序 / 方向 / 星级），
/// 列布局或三段放不下时改为一个系统玻璃按钮，弹出与手机形态相同的三分组菜单（点选不关闭、失焦关闭）；
/// AI 版另有相似聚类开关，行布局紧随三段、列布局落在单胶囊下方。
/// <para>段宽、胶囊宽与开关宽一律取原生 <c>SizeThatFits</c> 的自然值（含 configuration 内边距与弹出指示器），
/// 形态判定、整组居中、各矩形与主轴长度全部取共享 <see cref="WideFilterLayout"/>；
/// 本类只把结果落成 UIKit frame 并写回共享 <see cref="FilterBarViewModel"/>。
/// 本阶段不含筛选后计数（计数在后续阶段收入）。</para>
/// </summary>
internal sealed class iOSFilterControl : UIView
{
    /// <summary>相似聚类开关是否参与布局（仅 AI 版）。</summary>
    internal const bool ShowsSimilarityToggle = iOSAppCapabilities.HasDino;

    private readonly MainViewModel _main;
    private readonly UIVisualEffectView _segmented;
    private readonly UIButton _sort;
    private readonly UIButton _order;
    private readonly UIButton _rating;
    private readonly UIButton _capsule;
    private readonly UIButton? _similarity;
    /// <summary>上一次刷新的筛选内容摘要：菜单只在选项/选中项真的变化时重建，避免无关通知换掉已展开的下拉。</summary>
    private string _menuSignature = string.Empty;
    private FilterControlMode _mode = FilterControlMode.Segmented;
    private WideFilterFrames _frames;

    /// <summary>排序段按钮（验收读取几何）。</summary>
    internal UIButton SortSegment => _sort;

    /// <summary>升降序段按钮。</summary>
    internal UIButton OrderSegment => _order;

    /// <summary>星级筛选段按钮。</summary>
    internal UIButton RatingSegment => _rating;

    /// <summary>单胶囊按钮（列布局与行布局溢出时使用）。</summary>
    internal UIButton CapsuleButton => _capsule;

    /// <summary>三合体的交互式玻璃容器（胶囊外形由系统给出）。</summary>
    internal UIVisualEffectView SegmentedContainer => _segmented;

    /// <summary>相似聚类开关；标准版不创建该控件，为 null。</summary>
    internal UIButton? SimilarityToggle => _similarity;

    /// <summary>最近一次排布采用的形态。</summary>
    internal FilterControlMode Mode => _mode;

    /// <summary>最近一次排布算出的各矩形与主轴长度。</summary>
    internal WideFilterFrames Frames => _frames;

    /// <summary>构建三合体容器、三段弹出按钮、单胶囊与相似聚类开关。</summary>
    /// <param name="main">共享主 VM。</param>
    public iOSFilterControl(MainViewModel main) : base(CGRect.Empty)
    {
        _main = main;
        BackgroundColor = UIColor.Clear;
        _segmented = new UIVisualEffectView(new UIGlassEffect { Interactive = true })
        {
            CornerConfiguration = UICornerConfiguration.CreateCapsule(),
            BackgroundColor = UIColor.Clear,
            Hidden = true,
        };
        _sort = Segment("排序方式");
        _order = Segment("排序方向");
        _rating = Segment("星级筛选");
        _segmented.ContentView.AddSubview(_sort);
        _segmented.ContentView.AddSubview(_order);
        _segmented.ContentView.AddSubview(_rating);
        AddSubview(_segmented);

        _capsule = new UIButton(UIButtonType.System)
        {
            Configuration = CapsuleConfiguration(),
            ShowsMenuAsPrimaryAction = true,
            AccessibilityLabel = "筛选",
            AccessibilityHint = "下拉选择排序、方向与星级筛选",
            Hidden = true,
        };
        AddSubview(_capsule);

#if PHOTOVIEWER_DINO
        _similarity = new UIButton(UIButtonType.System)
        {
            Configuration = ToggleConfiguration(false),
            AccessibilityLabel = "相似聚类",
            AccessibilityHint = "展开/关闭相似聚类面板",
        };
        _similarity.TouchUpInside += (_, _) =>
            _main.FileVM.FilterBar.IsSimilarityPanelOpen = !_main.FileVM.FilterBar.IsSimilarityPanelOpen;
        AddSubview(_similarity);
#else
        _similarity = null;
#endif
        Refresh();
    }

    /// <summary>
    /// 同步共享筛选状态到原生控件（程序化改动与文件源刷新都不留旧选中项）：
    /// 三段与单胶囊标题、单胶囊菜单与相似聚类外观；选项或选中项未变时不重建已展开的菜单。
    /// </summary>
    internal void Refresh()
    {
        var filter = _main.FileVM.FilterBar;
        string sortTitle = filter.SortModes.FirstOrDefault(option => (SortMode)option.Value == filter.SortMode)?.DisplayName ?? "";
        string orderTitle = filter.SortOrders.FirstOrDefault(option => (SortOrder)option.Value == filter.SortOrder)?.DisplayName ?? "";
        string ratingTitle = filter.RatingFilters.FirstOrDefault(option => option.Key == filter.SelectedRatingFilter)?.DisplayName ?? "";
        SetTitle(_sort, sortTitle);
        SetTitle(_order, orderTitle);
        SetTitle(_rating, ratingTitle);
        SetCapsuleTitle(ratingTitle);
        RefreshToggle();
        string signature = string.Join("|",
            string.Join(",", filter.SortModes.Select(option => option.DisplayName + ":" + option.Value)),
            filter.SortMode + ":" + filter.SortOrder,
            filter.SelectedRatingFilter,
            string.Join(",", filter.SortOrders.Select(option => option.DisplayName + ":" + option.Value)),
            string.Join(",", filter.RatingFilters.Select(option => option.DisplayName + ":" + option.Key)));
        if (string.Equals(signature, _menuSignature, StringComparison.Ordinal)) return;
        _menuSignature = signature;
        _sort.Menu = SingleSelectionMenu(
            filter.SortModes.Select(option => (option.DisplayName, (object)(SortMode)option.Value)).ToArray(), filter.SortMode,
            value => filter.SortMode = (SortMode)value);
        _order.Menu = SingleSelectionMenu(
            filter.SortOrders.Select(option => (option.DisplayName, (object)(SortOrder)option.Value)).ToArray(), filter.SortOrder,
            value => filter.SortOrder = (SortOrder)value);
        _rating.Menu = SingleSelectionMenu(
            filter.RatingFilters.Select(option => (option.DisplayName, (object)option.Key)).ToArray(), filter.SelectedRatingFilter,
            value => filter.SelectedRatingFilter = (string)value);
        RefreshCapsuleMenu();
    }

    /// <summary>
    /// 筛选控件自身的主轴长度：行布局为控制高加上下外边距；列布局为前导让位、控制高、
    /// 相似聚类开关（含组间距）与尾部外边距之和。
    /// </summary>
    /// <param name="row">是否行布局。</param>
    /// <param name="leadingInset">列布局的前导让位。</param>
    /// <returns>主轴长度（行布局为高，列布局为内容区内的筛选段长度）。</returns>
    internal double MainLength(bool row, double leadingInset)
        => row
            ? WideFilterLayout.Height + WideFilterLayout.Margin * 2
            : leadingInset + WideFilterLayout.Height
                + (_similarity != null ? WideFilterLayout.Gap + WideFilterLayout.Height : 0)
                + WideFilterLayout.Margin;

    /// <summary>
    /// 按共享几何落位：只显示当前形态需要的变体（三段与单胶囊互斥），相似聚类开关按共享矩形紧随其后。
    /// </summary>
    /// <param name="row">是否行布局。</param>
    /// <param name="contentWidth">文件栏内容区宽度（行布局为内容宽，列布局为卡片带厚）。</param>
    /// <param name="cardWidth">列布局的胶片卡片列宽（大档 96 / 小档 64）。</param>
    /// <param name="leadingInset">列布局的前导让位。</param>
    /// <returns>形态、各矩形与主轴长度。</returns>
    internal WideFilterFrames Arrange(bool row, double contentWidth, double cardWidth, double leadingInset)
    {
        var frames = WideFilterLayout.Compute(row, contentWidth, cardWidth, leadingInset,
            NaturalWidth(_sort), NaturalWidth(_order), NaturalWidth(_rating), NaturalWidth(_capsule),
            _similarity != null, _similarity == null ? 0 : NaturalWidth(_similarity));
        _frames = frames;
        _mode = frames.Mode;
        bool segmented = frames.Mode == FilterControlMode.Segmented;
        _segmented.Hidden = !segmented;
        _capsule.Hidden = segmented;
        if (segmented)
        {
            _segmented.Frame = ToCGRect(frames.Control);
            _sort.Frame = ToCGRect(frames.Sort);
            _order.Frame = ToCGRect(frames.Order);
            _rating.Frame = ToCGRect(frames.Rating);
        }
        else
        {
            _capsule.Frame = ToCGRect(frames.Control);
        }
        if (_similarity != null) _similarity.Frame = ToCGRect(frames.Toggle);
        return frames;
    }

    /// <summary>单胶囊菜单的应用回调：写回已由菜单处理器完成，这里重赋菜单并就地刷新展开中的勾选态。</summary>
    private void RefreshCapsuleMenu()
    {
        var filter = _main.FileVM.FilterBar;
        _capsule.Menu = iOSFilterMenu.Build(filter, RefreshCapsuleMenu).Menu;
        SetCapsuleTitle(filter.RatingFilters.FirstOrDefault(option => option.Key == filter.SelectedRatingFilter)?.DisplayName ?? "");
        iOSFilterMenu.UpdateVisibleMenu(_capsule);
    }

    /// <summary>三段共用的弹出配置：无底色（玻璃由容器承担）、带系统弹出指示器。</summary>
    /// <param name="label">读屏名称。</param>
    /// <returns>配置完成的段按钮。</returns>
    private static UIButton Segment(string label)
    {
        var configuration = UIButtonConfiguration.PlainButtonConfiguration;
        configuration.Indicator = UIButtonConfigurationIndicator.Popup;
        configuration.BaseForegroundColor = UIColor.Label;
        return new UIButton(UIButtonType.System)
        {
            Configuration = configuration,
            ShowsMenuAsPrimaryAction = true,
            ChangesSelectionAsPrimaryAction = true,
            AccessibilityLabel = label,
            AccessibilityHint = "下拉选择",
        };
    }

    /// <summary>单胶囊的系统玻璃配置：筛选图标，标题为星级筛选当前值。</summary>
    /// <returns>配置完成的按钮配置。</returns>
    private static UIButtonConfiguration CapsuleConfiguration()
    {
        var configuration = UIButtonConfiguration.GlassButtonConfiguration;
        configuration.Image = iOSSymbols.Symbol("line.3.horizontal.decrease", DesignTokens.ToolbarCommandGlyphFontSize);
        configuration.BaseForegroundColor = UIColor.Label;
        return configuration;
    }

    /// <summary>相似聚类开关配置：关闭为普通玻璃，打开为强调玻璃。</summary>
    /// <param name="open">面板是否展开。</param>
    /// <returns>配置完成的按钮配置。</returns>
    private static UIButtonConfiguration ToggleConfiguration(bool open)
    {
        var configuration = open
            ? UIButtonConfiguration.ProminentGlassButtonConfiguration
            : UIButtonConfiguration.GlassButtonConfiguration;
        configuration.Title = "相似聚类";
        if (!open) configuration.BaseForegroundColor = UIColor.Label;
        return configuration;
    }

    /// <summary>改写段按钮标题（不动菜单与命中区）。</summary>
    /// <param name="button">目标按钮。</param>
    /// <param name="title">当前选项显示名。</param>
    private static void SetTitle(UIButton button, string title)
    {
        var configuration = button.Configuration;
        if (configuration == null) return;
        configuration.Title = title;
        button.Configuration = configuration;
        button.AccessibilityValue = title;
    }

    /// <summary>改写单胶囊标题为星级筛选当前值。</summary>
    /// <param name="title">当前选项显示名。</param>
    private void SetCapsuleTitle(string title)
    {
        var configuration = _capsule.Configuration;
        if (configuration == null) return;
        configuration.Title = title;
        _capsule.Configuration = configuration;
        _capsule.AccessibilityValue = title;
    }

    /// <summary>同步相似聚类开关的外观与读屏值。</summary>
    private void RefreshToggle()
    {
        if (_similarity == null) return;
        bool open = _main.FileVM.FilterBar.IsSimilarityPanelOpen;
        _similarity.Configuration = ToggleConfiguration(open);
        _similarity.AccessibilityValue = open ? "已展开" : "已收起";
    }

    /// <summary>三段的单选菜单：选中即写回并关闭，当前项带勾选标记。</summary>
    /// <param name="options">选项（显示名 + 业务值）。</param>
    /// <param name="selected">当前业务值。</param>
    /// <param name="apply">写回入口。</param>
    /// <returns>单选菜单。</returns>
    private static UIMenu SingleSelectionMenu((string Title, object Value)[] options, object selected, Action<object> apply)
    {
        var actions = new UIMenuElement[options.Length];
        for (int index = 0; index < options.Length; index++)
        {
            var option = options[index];
            var action = UIAction.Create(option.Title, null, null, _ => apply(option.Value));
            action.State = Equals(option.Value, selected) ? UIMenuElementState.On : UIMenuElementState.Off;
            actions[index] = action;
        }
        return UIMenu.Create(string.Empty, null, UIMenuIdentifier.None, UIMenuOptions.SingleSelection, actions);
    }

    /// <summary>原生量得的自然宽度（向上取整，含 configuration 内边距与弹出指示器）。</summary>
    /// <param name="button">目标按钮。</param>
    /// <returns>宽度，至少 1 点。</returns>
    private static double NaturalWidth(UIButton button)
    {
        double width = Math.Ceiling((double)button.SizeThatFits(
            new CGSize(0, (nfloat)WideFilterLayout.Height)).Width);
        if (width <= 0) width = Math.Ceiling((double)button.IntrinsicContentSize.Width);
        return Math.Max(1, width);
    }

    /// <summary>共享布局矩形 → UIKit frame。</summary>
    /// <param name="rect">共享布局矩形。</param>
    /// <returns>UIKit frame。</returns>
    private static CGRect ToCGRect(LayoutRect rect) => new(rect.X, rect.Y, rect.Width, rect.Height);
}
