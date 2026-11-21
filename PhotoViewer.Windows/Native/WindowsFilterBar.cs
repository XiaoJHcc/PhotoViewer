using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using PhotoViewer.Core.Design;
using PhotoViewer.Shared;
using PhotoViewer.ViewModels.Main;
using PhotoViewer.ViewModels.Main.File;
using Windows.Foundation;

namespace PhotoViewer.Windows.Native;

/// <summary>
/// 筛选控件（FILTER-01）：排序方式、排序方向与星级筛选收在同一个玻璃胶囊里。
/// 桌面宽形态是三段合体（FILTER-07）——每段显示当前选项、各自弹出原生菜单；
/// 三段放不下时退化为单一「筛选」胶囊，弹出同一套「排序 / 方向 / 星级」无标题分组菜单。
/// 排序方式与方向是原生单选（<see cref="RadioMenuFlyoutItem"/>），星级条件是各自独立的原生多选
/// （<see cref="ToggleMenuFlyoutItem"/>），「全部」清空条件（FILTER-03）；菜单项带 Fluent 语义图标（FILTER-08）。
/// 勾选一律由共享筛选状态回填，点选后就地校正，菜单重开时再同步一次。
/// 几何全部取共享 <see cref="WideFilterLayout"/>：行布局整组居中、列布局固定胶片卡片列宽并按
/// <see cref="DesignTokens.ShellFilterLeadingInset"/> 让开整宽标题栏的 caption 区；
/// AI 版末位另有相似聚类开关（FILTER-06），标准版不创建也不留空槽（EDITION-02）。
/// </summary>
internal sealed class WindowsFilterBar : Panel
{
    /// <summary>本条筛选条是否渲染相似聚类开关（标准版为 false）。</summary>
    internal const bool ShowsSimilarityToggle = WindowsProductInfo.HasDino;

    private const string PrimaryForegroundBrushKey = "TextFillColorPrimaryBrush";
    private const string SecondaryForegroundBrushKey = "TextFillColorSecondaryBrush";
    private const string SubtleHoverBrushKey = "SubtleFillColorSecondaryBrush";
    private const string SubtlePressedBrushKey = "SubtleFillColorTertiaryBrush";
    private const string CapsuleBorderBrushKey = "ControlElevationBorderBrush";
    private const string DividerBrushKey = "DividerStrokeColorDefaultBrush";

    /// <summary>状态层四周的内缩量（TOOLBAR-10：段内按同心圆角内缩，端段与胶囊同为半圆端）。</summary>
    private const double StatePillInset = 3;

    private static readonly SolidColorBrush TransparentBrush = new(Microsoft.UI.Colors.Transparent);

    /// <summary>胶囊高度（玻璃按钮边长）。</summary>
    private static readonly double CapsuleHeight = WideFilterLayout.Height;

    /// <summary>胶囊圆角（高度一半，端段即半圆端）。</summary>
    private static readonly double CapsuleCornerRadius = CapsuleHeight / 2;

    /// <summary>状态层圆角：与胶囊同心，故取 高度 / 2 − 内缩。</summary>
    private static readonly double StatePillCornerRadius = CapsuleHeight / 2 - StatePillInset;

    private readonly FilterBarViewModel _filter;
    private readonly Border _capsule;
    private readonly Border _dividerBeforeOrder;
    private readonly Border _dividerBeforeRating;
    private readonly Button _sortEntry;
    private readonly Button _orderEntry;
    private readonly Button _ratingEntry;
    private readonly Button _capsuleEntry;
    private readonly MenuFlyout _sortMenu;
    private readonly MenuFlyout _orderMenu;
    private readonly MenuFlyout _ratingMenu;
    private readonly MenuFlyout _capsuleMenu;
    private readonly FilterRadioGroup _sortGroup;
    private readonly FilterRadioGroup _orderGroup;
    private readonly FilterRatingGroup _ratingGroup;
    private readonly TextBlock _capsuleLabel;
    private readonly Button[] _segmentedEntries;
    private readonly Button[] _capsuleEntries;
#if PHOTOVIEWER_DINO
    private readonly ToggleButton _similarityToggle;
#endif
    private FilterControlMode _mode = FilterControlMode.Segmented;
    private WideFilterFrames _frames;
    private (double Sort, double Order, double Rating) _naturalWidths;
    private bool _naturalWidthsMeasured;
    private bool _syncing;
    private bool _isRowLayout = true;

    /// <summary>
    /// 创建筛选控件。
    /// </summary>
    /// <param name="filter">筛选视图模型，选项名、取值与写入都来自它。</param>
    public WindowsFilterBar(FilterBarViewModel filter)
    {
        _filter = filter;

        _sortGroup = new FilterRadioGroup("Sort", "排序方式",
            _filter.SortModes.Select(option => option.DisplayName).ToArray(),
            _filter.SortModes.Select(option => option.Value).ToArray(),
            () => _filter.SortMode,
            index =>
            {
                if (!_syncing) _filter.SortMode = (SortMode)_filter.SortModes[index].Value;
            },
            index => SortModeGlyph((SortMode)_filter.SortModes[index].Value));
        _orderGroup = new FilterRadioGroup("Order", "排序方向",
            _filter.SortOrders.Select(option => option.DisplayName).ToArray(),
            _filter.SortOrders.Select(option => option.Value).ToArray(),
            () => _filter.SortOrder,
            index =>
            {
                if (!_syncing) _filter.SortOrder = (SortOrder)_filter.SortOrders[index].Value;
            },
            index => SortOrderGlyph((SortOrder)_filter.SortOrders[index].Value));
        _ratingGroup = new FilterRatingGroup(_filter);

        _capsuleLabel = CreateEntryText(DesignTokens.DropdownFontSize, PrimaryForegroundBrushKey);
        _capsuleLabel.Text = "筛选";

        _sortEntry = CreateEntryButton(_sortGroup.Label);
        _orderEntry = CreateEntryButton(_orderGroup.Label);
        _ratingEntry = CreateEntryButton(_ratingGroup.Label);
        _capsuleEntry = CreateEntryButton(_capsuleLabel);

        _capsule = new Border
        {
            Background = new SolidColorBrush(WindowsLayout.MainButtonFill),
            BorderBrush = ThemeBrush(CapsuleBorderBrushKey),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(CapsuleCornerRadius),
            // 与 Web 参考壳一致：胶囊只是材质衬底，指针交给它上面的段按钮。
            IsHitTestVisible = false,
        };
        _dividerBeforeOrder = CreateDivider();
        _dividerBeforeRating = CreateDivider();

        _sortMenu = CreateMenu(_sortGroup.SegmentItems);
        _orderMenu = CreateMenu(_orderGroup.SegmentItems);
        _ratingMenu = CreateMenu(_ratingGroup.SegmentItems);
        _capsuleMenu = CreateMenu(_sortGroup.CapsuleItems);
        AddMenuGroup(_capsuleMenu, _orderGroup.CapsuleItems, separator: true);
        AddMenuGroup(_capsuleMenu, _ratingGroup.CapsuleItems, separator: true);
        _sortEntry.Flyout = _sortMenu;
        _orderEntry.Flyout = _orderMenu;
        _ratingEntry.Flyout = _ratingMenu;
        _capsuleEntry.Flyout = _capsuleMenu;

#if PHOTOVIEWER_DINO
        _similarityToggle = CreateSimilarityToggle();
#endif

        _segmentedEntries = new[] { _sortEntry, _orderEntry, _ratingEntry };
        _capsuleEntries = new[] { _capsuleEntry };

        Children.Add(_capsule);
        Children.Add(_sortEntry);
        Children.Add(_dividerBeforeOrder);
        Children.Add(_orderEntry);
        Children.Add(_dividerBeforeRating);
        Children.Add(_ratingEntry);
        Children.Add(_capsuleEntry);
#if PHOTOVIEWER_DINO
        Children.Add(_similarityToggle);
#endif
        ApplyMode(FilterControlMode.Segmented);

        _filter.PropertyChanged += OnFilterChanged;
        SyncFromViewModel();
    }

    /// <summary>RATING-04：菜单打开前关闭来源浮窗。</summary>
    internal event Action? PopupOpening;

    /// <summary>实际筛选控件边界在布局后发生变化，标题栏据此重算挖空与窄宽度回退。</summary>
    internal event Action? InteractiveBoundsChanged;

    /// <summary>验收专用：最近一次落位得到的控件形态。</summary>
    internal FilterControlMode Mode => _mode;

    /// <summary>验收专用：当前形态下的筛选入口（分段为排序 / 方向 / 星级，单胶囊为唯一入口）。</summary>
    internal IReadOnlyList<Button> Entries => _mode == FilterControlMode.Segmented ? _segmentedEntries : _capsuleEntries;

    /// <summary>验收专用：各入口当前显示的选项名。</summary>
    internal IReadOnlyList<string> EntryLabels => _mode == FilterControlMode.Segmented
        ? new[] { _sortGroup.Label.Text, _orderGroup.Label.Text, _ratingGroup.Label.Text }
        : new[] { _capsuleLabel.Text };

    /// <summary>验收专用：当前承载排序选项的入口（排序段，或单胶囊）。</summary>
    internal Button SortEntry => _mode == FilterControlMode.Segmented ? _sortEntry : _capsuleEntry;

    /// <summary>验收专用：当前承载排序选项的菜单（排序段菜单，或含三分组的单胶囊菜单）。</summary>
    internal MenuFlyout SortMenu => _mode == FilterControlMode.Segmented ? _sortMenu : _capsuleMenu;

    /// <summary>验收专用：当前承载星级条件的入口（星级段，或单胶囊）。</summary>
    internal Button RatingEntry => _mode == FilterControlMode.Segmented ? _ratingEntry : _capsuleEntry;

    /// <summary>验收专用：当前承载星级条件的菜单（星级段菜单，或含三分组的单胶囊菜单）。</summary>
    internal MenuFlyout RatingMenu => _mode == FilterControlMode.Segmented ? _ratingMenu : _capsuleMenu;

    /// <summary>验收专用：当前形态实际使用的星级切换项。</summary>
    internal IReadOnlyList<ToggleMenuFlyoutItem> RatingItems => _mode == FilterControlMode.Segmented
        ? _ratingGroup.SegmentItems
        : _ratingGroup.CapsuleItems;

    /// <summary>验收专用：当前形态实际使用的排序键单选项。</summary>
    internal IReadOnlyList<RadioMenuFlyoutItem> SortItems => _mode == FilterControlMode.Segmented
        ? _sortGroup.SegmentItems
        : _sortGroup.CapsuleItems;

    /// <summary>验收专用：当前形态实际使用的排序方向单选项。</summary>
    internal IReadOnlyList<RadioMenuFlyoutItem> OrderItems => _mode == FilterControlMode.Segmented
        ? _orderGroup.SegmentItems
        : _orderGroup.CapsuleItems;

    /// <summary>验收专用：星级段菜单的切换项（单胶囊形态下仍须与共享状态同步）。</summary>
    internal IReadOnlyList<ToggleMenuFlyoutItem> SegmentRatingItems => _ratingGroup.SegmentItems;

    /// <summary>验收专用：单胶囊菜单的星级切换项。</summary>
    internal IReadOnlyList<ToggleMenuFlyoutItem> CapsuleRatingItems => _ratingGroup.CapsuleItems;

    /// <summary>验收专用：星级段显示的摘要（全部 / 单选条件 / 多选）。</summary>
    internal string RatingLabel => _ratingGroup.Label.Text;

    /// <summary>验收专用：星级入口的读屏名称（含完整条件）。</summary>
    internal string RatingAutomationName => AutomationProperties.GetName(RatingEntry);

#if PHOTOVIEWER_DINO
    /// <summary>验收专用：相似聚类开关。</summary>
    internal ToggleButton SimilarityToggle => _similarityToggle;
#endif

    /// <summary>
    /// 释放订阅。
    /// </summary>
    public void Detach() => _filter.PropertyChanged -= OnFilterChanged;

    /// <summary>
    /// 切换排布方向：上下布局整组横向居中，左右布局在文件栏单列内纵排。
    /// </summary>
    /// <param name="isRowLayout">是否为上下布局。</param>
    public void SetOrientation(bool isRowLayout)
    {
        if (_isRowLayout == isRowLayout)
        {
            return;
        }

        _isRowLayout = isRowLayout;
        InvalidateMeasure();
        InvalidateArrange();
    }

    /// <summary>
    /// 计算当前宽度下的形态与矩形（纯计算，布局与验收共用）。
    /// 列布局的前导让位取 <see cref="DesignTokens.ShellFilterLeadingInset"/>：标题栏在列布局整宽盖住文件栏，
    /// 首控件按标题栏高加 filter.margin 让开 caption；行布局整组仍从 <see cref="MainScreenLayout.FilterMargin"/> 起排。
    /// 该让位计入主轴长度，胶片栏与列布局相似面板据此自然下移。
    /// </summary>
    /// <param name="row">是否行布局。</param>
    /// <param name="contentWidth">文件栏内容区宽度。</param>
    /// <returns>形态、整块矩形、三段局部矩形、开关矩形与主轴长度。</returns>
    internal WideFilterFrames ComputeFrames(bool row, double contentWidth)
    {
        var (sortWidth, orderWidth, ratingWidth) = NaturalSegmentWidths();
        double capsuleWidth = FilmCardGeometry.Width(FilmCardTier.Large);
        double leadingInset = row ? MainScreenLayout.FilterMargin : DesignTokens.ShellFilterLeadingInset;
        return WideFilterLayout.Compute(row, contentWidth, capsuleWidth, leadingInset,
            sortWidth, orderWidth, ratingWidth, capsuleWidth, ShowsSimilarityToggle, DesignTokens.FilterToggleWidth);
    }

    /// <summary>取得实际筛选控件的边界，供标题栏避让；不包含横向拉伸的外层网格。</summary>
    /// <param name="relativeTo">用于表示边界坐标的参考元素。</param>
    /// <returns>筛选控件（AI 版含相似聚类开关）在参考元素内的逻辑坐标边界。</returns>
    internal Rect GetInteractiveBounds(UIElement relativeTo)
    {
        var control = _frames.Control;
        double left = control.X;
        double top = control.Y;
        double right = control.MaxX;
        double bottom = control.MaxY;
#if PHOTOVIEWER_DINO
        var toggle = _frames.Toggle;
        left = Math.Min(left, toggle.X);
        top = Math.Min(top, toggle.Y);
        right = Math.Max(right, toggle.MaxX);
        bottom = Math.Max(bottom, toggle.MaxY);
#endif
        return TransformToVisual(relativeTo).TransformBounds(new Rect(left, top, right - left, bottom - top));
    }

    /// <inheritdoc/>
    protected override Size MeasureOverride(Size availableSize)
    {
        double contentWidth = double.IsInfinity(availableSize.Width) ? NaturalContentWidth() : availableSize.Width;
        var frames = ComputeFrames(_isRowLayout, contentWidth);
        // FILTER-07：形态在量测阶段就定好，切换只改可见性，避免重排后段内容滞后一帧。
        ApplyMode(frames.Mode);
        MeasureChildren(frames);
        return new Size(contentWidth, frames.MainLength);
    }

    /// <inheritdoc/>
    protected override Size ArrangeOverride(Size finalSize)
    {
        var previous = _frames;
        var frames = ComputeFrames(_isRowLayout, finalSize.Width);
        _frames = frames;
        ApplyMode(frames.Mode);

        ArrangeChild(_capsule, RectOf(frames.Control));
        if (frames.Mode == FilterControlMode.Segmented)
        {
            ArrangeChild(_sortEntry, Offset(frames.Control, frames.Sort));
            ArrangeChild(_orderEntry, Offset(frames.Control, frames.Order));
            ArrangeChild(_ratingEntry, Offset(frames.Control, frames.Rating));
            ArrangeChild(_dividerBeforeOrder, DividerRect(frames, frames.Sort.MaxX));
            ArrangeChild(_dividerBeforeRating, DividerRect(frames, frames.Order.MaxX));
        }
        else
        {
            ArrangeChild(_capsuleEntry, RectOf(frames.Control));
        }
#if PHOTOVIEWER_DINO
        ArrangeChild(_similarityToggle, RectOf(frames.Toggle));
#endif
        if (frames != previous)
        {
            InteractiveBoundsChanged?.Invoke();
        }

        return finalSize;
    }

    /// <summary>未知宽度下的自然宽：三段 + 相似聚类开关运行段 + 两侧外边距（列布局退回文件栏内容宽）。</summary>
    /// <returns>自然内容宽。</returns>
    private double NaturalContentWidth()
    {
        if (!_isRowLayout)
        {
            return MainScreenLayout.FileBarContentWidth;
        }

        var (sortWidth, orderWidth, ratingWidth) = NaturalSegmentWidths();
        double toggleRun = ShowsSimilarityToggle ? WideFilterLayout.Gap + DesignTokens.FilterToggleWidth : 0;
        return sortWidth + orderWidth + ratingWidth + toggleRun + WideFilterLayout.Margin * 2;
    }

    /// <summary>
    /// 三段自然宽：各段选项名在 <see cref="DesignTokens.DropdownFontSize"/> 下的最大文本宽加两侧内边距，上取整。
    /// 选项固定故只算一次。
    /// </summary>
    /// <returns>排序、方向、星级三段的自然宽。</returns>
    private (double Sort, double Order, double Rating) NaturalSegmentWidths()
    {
        if (!_naturalWidthsMeasured)
        {
            _naturalWidths = (
                MeasureSegment(_filter.SortModes.Select(option => option.DisplayName)),
                MeasureSegment(_filter.SortOrders.Select(option => option.DisplayName)),
                MeasureSegment(_filter.RatingFilters.Select(option => option.DisplayName)));
            _naturalWidthsMeasured = true;
        }

        return _naturalWidths;
    }

    /// <summary>单段自然宽：用离树 <see cref="TextBlock"/> 量测该段最长选项名，再加左右内边距。</summary>
    /// <param name="names">该段的选项显示名。</param>
    /// <returns>自然宽（点）。</returns>
    private static double MeasureSegment(IEnumerable<string> names)
    {
        var probe = new TextBlock { FontSize = DesignTokens.DropdownFontSize };
        double textWidth = 0;
        foreach (var name in names)
        {
            probe.Text = name;
            probe.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            textWidth = Math.Max(textWidth, probe.DesiredSize.Width);
        }

        return Math.Ceiling(textWidth) + DesignTokens.DropdownPaddingLeft * 2;
    }

    /// <summary>按几何给出的矩形量测子控件：段按钮按自身段宽，单胶囊按整块宽。</summary>
    /// <param name="frames">本次落位结果。</param>
    private void MeasureChildren(WideFilterFrames frames)
    {
        MeasureChild(_capsule, RectOf(frames.Control));
        if (frames.Mode == FilterControlMode.Segmented)
        {
            MeasureChild(_sortEntry, Offset(frames.Control, frames.Sort));
            MeasureChild(_orderEntry, Offset(frames.Control, frames.Order));
            MeasureChild(_ratingEntry, Offset(frames.Control, frames.Rating));
            MeasureChild(_dividerBeforeOrder, DividerRect(frames, frames.Sort.MaxX));
            MeasureChild(_dividerBeforeRating, DividerRect(frames, frames.Order.MaxX));
        }
        else
        {
            MeasureChild(_capsuleEntry, RectOf(frames.Control));
        }
#if PHOTOVIEWER_DINO
        MeasureChild(_similarityToggle, RectOf(frames.Toggle));
#endif
    }

    /// <summary>按形态切换段内容与单胶囊内容：只改可见性，不重建控件，也不动已展开的菜单。</summary>
    /// <param name="mode">目标形态。</param>
    private void ApplyMode(FilterControlMode mode)
    {
        _mode = mode;
        bool segmented = mode == FilterControlMode.Segmented;
        Show(_sortEntry, segmented);
        Show(_orderEntry, segmented);
        Show(_ratingEntry, segmented);
        Show(_dividerBeforeOrder, segmented);
        Show(_dividerBeforeRating, segmented);
        Show(_capsuleEntry, !segmented);
    }

    /// <summary>幂等设置可见性，避免每次布局都触发无效重排。</summary>
    /// <param name="element">子控件。</param>
    /// <param name="visible">是否显示。</param>
    private static void Show(UIElement element, bool visible)
    {
        var target = visible ? Visibility.Visible : Visibility.Collapsed;
        if (element.Visibility != target)
        {
            element.Visibility = target;
        }
    }

    /// <summary>段矩形是相对控件左上角的局部坐标，叠加控件原点后才是本面板坐标。</summary>
    /// <param name="control">整块矩形。</param>
    /// <param name="segment">段局部矩形。</param>
    /// <returns>面板坐标矩形。</returns>
    private static Rect Offset(LayoutRect control, LayoutRect segment)
        => new(control.X + segment.X, control.Y + segment.Y, segment.Width, segment.Height);

    /// <summary>共享矩形转布局矩形。</summary>
    /// <param name="rect">共享矩形。</param>
    /// <returns>布局矩形。</returns>
    private static Rect RectOf(LayoutRect rect) => new(rect.X, rect.Y, rect.Width, rect.Height);

    /// <summary>把子控件落到指定矩形。</summary>
    /// <param name="child">子控件。</param>
    /// <param name="rect">目标矩形。</param>
    private static void ArrangeChild(UIElement child, Rect rect) => child.Arrange(rect);

    /// <summary>量测子控件到指定矩形尺寸。</summary>
    /// <param name="child">子控件。</param>
    /// <param name="rect">目标矩形。</param>
    private static void MeasureChild(UIElement child, Rect rect) => child.Measure(new Size(rect.Width, rect.Height));

    /// <summary>段间 1 点分隔线：贴段边界，上下按 filter.margin 内缩（TOOLBAR-10；控件内部竖向内缩，与前导让位无关）。</summary>
    /// <param name="frames">本次落位结果。</param>
    /// <param name="localX">段边界在控件内的横坐标。</param>
    /// <returns>面板坐标矩形。</returns>
    private static Rect DividerRect(WideFilterFrames frames, double localX)
    {
        double inset = MainScreenLayout.FilterMargin;
        return new Rect(frames.Control.X + localX, frames.Control.Y + inset, MainScreenLayout.Divider,
            Math.Max(0, frames.Control.Height - inset * 2));
    }

    /// <summary>
    /// 段 / 胶囊入口按钮：透明 Subtle 底，悬停与按下在段内按同心圆角内缩填状态色（TOOLBAR-10），
    /// 模板内边距清零、内容拉伸到整块，使文字与命中区都是那个内缩胶囊。
    /// </summary>
    /// <param name="content">入口内容（当前选项文字）。</param>
    /// <returns>入口按钮。</returns>
    private static Button CreateEntryButton(UIElement content)
    {
        var button = new Button
        {
            Content = content,
            Margin = new Thickness(StatePillInset),
            Padding = new Thickness(0),
            MinWidth = 0,
            MinHeight = 0,
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(StatePillCornerRadius),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Center,
        };
        ApplySubtleStates(button);
        return button;
    }

    /// <summary>
    /// 覆盖按钮模板的四态键：常态透明、悬停/按下取菜单项状态色、描边恒透明、前景恒主文字色，
    /// 禁用不换底色（只由系统降内容强调）。
    /// </summary>
    /// <param name="button">入口按钮。</param>
    private static void ApplySubtleStates(Button button)
    {
        button.Resources["ButtonBackground"] = TransparentBrush;
        button.Resources["ButtonBackgroundPointerOver"] = ThemeBrush(SubtleHoverBrushKey);
        button.Resources["ButtonBackgroundPressed"] = ThemeBrush(SubtlePressedBrushKey);
        button.Resources["ButtonBackgroundDisabled"] = TransparentBrush;
        button.Resources["ButtonBorderBrush"] = TransparentBrush;
        button.Resources["ButtonBorderBrushPointerOver"] = TransparentBrush;
        button.Resources["ButtonBorderBrushPressed"] = TransparentBrush;
        button.Resources["ButtonBorderBrushDisabled"] = TransparentBrush;
        button.Resources["ButtonForeground"] = ThemeBrush(PrimaryForegroundBrushKey);
        button.Resources["ButtonForegroundPointerOver"] = ThemeBrush(PrimaryForegroundBrushKey);
        button.Resources["ButtonForegroundPressed"] = ThemeBrush(SecondaryForegroundBrushKey);
        button.Resources["ButtonForegroundDisabled"] = ThemeBrush(PrimaryForegroundBrushKey);
    }

    /// <summary>段内文字：单行、水平居中、超宽尾部省略。</summary>
    /// <param name="fontSize">字号。</param>
    /// <param name="brushKey">前景主题画刷键。</param>
    /// <returns>文字控件。</returns>
    private static TextBlock CreateEntryText(double fontSize, string brushKey) => new()
    {
        FontSize = fontSize,
        Foreground = ThemeBrush(brushKey),
        HorizontalAlignment = HorizontalAlignment.Stretch,
        VerticalAlignment = VerticalAlignment.Center,
        TextAlignment = TextAlignment.Center,
        TextWrapping = TextWrapping.NoWrap,
        TextTrimming = TextTrimming.CharacterEllipsis,
    };

    /// <summary>段间分隔线：1 点宽的纯色条，不参与命中。</summary>
    /// <returns>分隔线条。</returns>
    private static Border CreateDivider() => new()
    {
        Background = ThemeBrush(DividerBrushKey),
        IsHitTestVisible = false,
    };

    /// <summary>
    /// 原生菜单：贴入口正下方展开，展开前通知宿主关闭来源浮窗（RATING-04）并按共享状态回填全部勾选
    /// （FILTER-08：菜单重开时校正系统可能残留的切换态）。
    /// 入口用 <see cref="Button.Flyout"/> 挂载，点击、Enter 与空格都由原生按钮语义打开。
    /// </summary>
    /// <param name="items">首组选项。</param>
    /// <returns>菜单。</returns>
    private MenuFlyout CreateMenu(IReadOnlyList<MenuFlyoutItemBase> items)
    {
        var menu = new MenuFlyout { Placement = FlyoutPlacementMode.Bottom };
        foreach (var item in items)
        {
            menu.Items.Add(item);
        }

        menu.Opening += (_, _) =>
        {
            PopupOpening?.Invoke();
            SyncFromViewModel();
        };
        return menu;
    }

    /// <summary>向单胶囊菜单追加一组选项；多组之间只放分隔线，不加标题（FILTER-08）。</summary>
    /// <param name="menu">单胶囊菜单。</param>
    /// <param name="items">该组选项。</param>
    /// <param name="separator">是否在本组之前放分隔线。</param>
    private static void AddMenuGroup(MenuFlyout menu, IReadOnlyList<MenuFlyoutItemBase> items, bool separator)
    {
        if (separator)
        {
            menu.Items.Add(new MenuFlyoutSeparator());
        }

        foreach (var item in items)
        {
            menu.Items.Add(item);
        }
    }

#if PHOTOVIEWER_DINO
    /// <summary>
    /// 相似聚类开关（FILTER-06）：原生 <see cref="ToggleButton"/> 默认样式——勾选态走系统强调色，
    /// 只校准未选中时的常态填充；勾选写回视图模型（持久化在设置里）。
    /// 标准版没有这个入口，不创建控件（EDITION-02）。
    /// </summary>
    /// <returns>开关。</returns>
    private ToggleButton CreateSimilarityToggle()
    {
        var toggle = new ToggleButton
        {
            Content = "相似聚类",
            Width = DesignTokens.FilterToggleWidth,
            Height = CapsuleHeight,
            MinWidth = 0,
            MinHeight = 0,
            FontSize = DesignTokens.FilterToggleFontSize,
            CornerRadius = new CornerRadius(CapsuleCornerRadius),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
        };
        WindowsControlStyles.ApplyMainButtonFill(toggle);
        ToolTipService.SetToolTip(toggle, "展开/关闭相似聚类面板");
        toggle.Checked += (_, _) =>
        {
            if (!_syncing) _filter.IsSimilarityPanelOpen = true;
        };
        toggle.Unchecked += (_, _) =>
        {
            if (!_syncing) _filter.IsSimilarityPanelOpen = false;
        };
        return toggle;
    }
#endif

    /// <summary>视图模型 → 控件（单向，不回写避免循环）。</summary>
    /// <param name="sender">事件源。</param>
    /// <param name="e">变更参数。</param>
    private void OnFilterChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(FilterBarViewModel.SortMode):
            case nameof(FilterBarViewModel.SortOrder):
            case nameof(FilterBarViewModel.SelectedRatingFilters):
                SyncFromViewModel();
                break;
#if PHOTOVIEWER_DINO
            case nameof(FilterBarViewModel.IsSimilarityPanelOpen):
                SyncFromViewModel();
                break;
#endif
        }
    }

    /// <summary>
    /// 按视图模型就地同步：只改菜单项勾选与段标签文字，不重建菜单或按钮，
    /// 因此无关通知不会关掉已展开的菜单（FILTER-08）。
    /// </summary>
    private void SyncFromViewModel()
    {
        _syncing = true;
        try
        {
            SyncGroup(_sortGroup);
            SyncGroup(_orderGroup);
            _ratingGroup.SyncChecks();
#if PHOTOVIEWER_DINO
            _similarityToggle.IsChecked = _filter.IsSimilarityPanelOpen;
#endif
            UpdateAutomationNames();
        }
        finally
        {
            _syncing = false;
        }
    }

    /// <summary>把单选分组的当前值同步到段菜单与单胶囊菜单的勾选态，并更新段标签。</summary>
    /// <param name="group">单选分组。</param>
    private void SyncGroup(FilterRadioGroup group)
    {
        int current = CurrentIndexOf(group);
        for (var index = 0; index < group.Names.Length; index++)
        {
            bool selected = index == current;
            group.SegmentItems[index].IsChecked = selected;
            group.CapsuleItems[index].IsChecked = selected;
        }

        group.Label.Text = current < 0 ? string.Empty : group.Names[current];
    }

    /// <summary>当前值在选项里的下标；不一致时记录日志并返回 -1，避免配置漂移在 UI 回调里抛异常。</summary>
    /// <param name="group">单选分组。</param>
    /// <returns>下标，找不到为 -1。</returns>
    private static int CurrentIndexOf(FilterRadioGroup group)
    {
        var values = group.Values;
        var target = group.CurrentValue();
        for (var index = 0; index < values.Length; index++)
        {
            if (Equals(values[index], target))
            {
                return index;
            }
        }

        NativeStartupLog.Write($"筛选选项缺少取值 {target};视图模型与控件选项不一致,已清空选择。");
        return -1;
    }

    /// <summary>读屏名称随选项更新（分段报当前值与完整星级条件，单胶囊报三分组当前值）。</summary>
    private void UpdateAutomationNames()
    {
        string rating = _filter.RatingFilterSelectionText;
        AutomationProperties.SetName(_sortEntry, $"{_sortGroup.AutomationLabel}：{_sortGroup.Label.Text}");
        AutomationProperties.SetName(_orderEntry, $"{_orderGroup.AutomationLabel}：{_orderGroup.Label.Text}");
        AutomationProperties.SetName(_ratingEntry, $"{_ratingGroup.AutomationLabel}：{rating}");
        AutomationProperties.SetName(_capsuleEntry,
            $"筛选，排序方式：{_sortGroup.Label.Text}，排序方向：{_orderGroup.Label.Text}，星级筛选：{rating}");
    }

    /// <summary>排序键的语义前置图标（FILTER-08）。</summary>
    /// <param name="mode">排序键。</param>
    /// <returns>Fluent 字形。</returns>
    private static string SortModeGlyph(SortMode mode) => mode switch
    {
        SortMode.Date => WindowsIcons.SortDateGlyph,
        SortMode.Size => WindowsIcons.SortSizeGlyph,
        _ => WindowsIcons.DocumentGlyph,
    };

    /// <summary>排序方向的语义前置图标（FILTER-08）。</summary>
    /// <param name="order">排序方向。</param>
    /// <returns>Fluent 字形。</returns>
    private static string SortOrderGlyph(SortOrder order)
        => order == SortOrder.Descending ? WindowsIcons.DownGlyph : WindowsIcons.UpGlyph;

    /// <summary>星级条件的语义前置图标（FILTER-08）；数字档使用对应数字圆。</summary>
    /// <param name="key">条件键。</param>
    /// <returns>Fluent 字形。</returns>
    private static string RatingGlyph(string key) => key switch
    {
        "None" => WindowsIcons.UnratedGlyph,
        "Rejected" => WindowsIcons.BlockedGlyph,
        "Conflict" => IconCatalog.WindowsGlyph(IconCatalog.FilterConflict),
        _ when key.StartsWith("Eq", StringComparison.Ordinal) => WindowsIcons.ForRatingFilter(key),
        _ => WindowsIcons.SelectAllGlyph,
    };

    /// <summary>菜单项的 Fluent 前置图标（MENU-06 同一套字体与尺寸）。</summary>
    /// <param name="glyph">字形码位。</param>
    /// <returns>可直接作为 <c>MenuFlyoutItem.Icon</c> 的图标元素。</returns>
    private static FontIcon CreateMenuIcon(string glyph) => new()
    {
        FontFamily = new FontFamily(WindowsIcons.FontFamilyForGlyph(glyph)),
        Glyph = glyph,
        FontSize = DesignTokens.MenuIconSize,
    };

    /// <summary>读取 WinUI 主题画刷；缺失时退回透明，避免资源键漂移让控件构造失败。</summary>
    /// <param name="key">资源键。</param>
    /// <returns>画刷。</returns>
    private static Brush ThemeBrush(string key)
        => Application.Current.Resources.TryGetValue(key, out var value) && value is Brush brush
            ? brush
            : TransparentBrush;

    /// <summary>
    /// 一个单选分组（排序方式 / 排序方向）：选项名与共享取值来自视图模型，
    /// 菜单项备两套实例——段菜单与单胶囊菜单各持一套（同一项不能同时属于两个菜单）。
    /// </summary>
    private sealed class FilterRadioGroup
    {
        /// <summary>创建分组。</summary>
        /// <param name="key">菜单分组名前缀，保证同组单选且不与别的组串联。</param>
        /// <param name="automationLabel">读屏名称里的组名（排序方式 / 排序方向）。</param>
        /// <param name="names">选项显示名。</param>
        /// <param name="values">选项对应的共享取值。</param>
        /// <param name="currentValue">读取视图模型当前取值。</param>
        /// <param name="apply">写入视图模型。</param>
        /// <param name="glyph">选项的菜单图标字形。</param>
        internal FilterRadioGroup(string key, string automationLabel, string[] names, object[] values,
            Func<object?> currentValue, Action<int> apply, Func<int, string> glyph)
        {
            AutomationLabel = automationLabel;
            Names = names;
            Values = values;
            CurrentValue = currentValue;
            Apply = apply;
            Label = CreateEntryText(DesignTokens.DropdownFontSize, PrimaryForegroundBrushKey);
            for (var index = 0; index < names.Length; index++)
            {
                SegmentItems.Add(CreateItem(key + "Segment", index, glyph(index)));
                CapsuleItems.Add(CreateItem(key + "Capsule", index, glyph(index)));
            }
        }

        /// <summary>读屏名称里的组名。</summary>
        internal string AutomationLabel { get; }

        /// <summary>选项显示名。</summary>
        internal string[] Names { get; }

        /// <summary>选项对应的共享取值。</summary>
        internal object[] Values { get; }

        /// <summary>读取视图模型当前取值。</summary>
        internal Func<object?> CurrentValue { get; }

        /// <summary>写入视图模型。</summary>
        internal Action<int> Apply { get; }

        /// <summary>段按钮上的当前选项文字。</summary>
        internal TextBlock Label { get; }

        /// <summary>段菜单的选项（进入本组的段弹出菜单）。</summary>
        internal List<RadioMenuFlyoutItem> SegmentItems { get; } = new();

        /// <summary>单胶囊菜单的选项（三分组共用同一个菜单）。</summary>
        internal List<RadioMenuFlyoutItem> CapsuleItems { get; } = new();

        /// <summary>创建一个原生单选菜单项，选中即写回共享状态（原生菜单随后自行收起）。</summary>
        /// <param name="groupName">单选组名。</param>
        /// <param name="index">选项下标。</param>
        /// <param name="glyph">选项图标字形。</param>
        /// <returns>菜单项。</returns>
        private RadioMenuFlyoutItem CreateItem(string groupName, int index, string glyph)
        {
            var item = new RadioMenuFlyoutItem
            {
                Text = Names[index],
                GroupName = groupName,
                Icon = CreateMenuIcon(glyph),
            };
            item.Click += (_, _) => Apply(index);
            return item;
        }
    }

    /// <summary>
    /// 星级筛选分组（FILTER-03）：每项一个独立的多选开关，首项「全部」在无条件时保持勾选；
    /// 段菜单与单胶囊菜单各持一套切换项，勾选一律从共享条件集回填。
    /// </summary>
    private sealed class FilterRatingGroup
    {
        private readonly FilterBarViewModel _filter;

        /// <summary>创建星级分组。</summary>
        /// <param name="filter">筛选视图模型。</param>
        internal FilterRatingGroup(FilterBarViewModel filter)
        {
            _filter = filter;
            Label = CreateEntryText(DesignTokens.DropdownFontSize, PrimaryForegroundBrushKey);
            for (var index = 0; index < filter.RatingFilters.Count; index++)
            {
                SegmentItems.Add(CreateItem(index));
                CapsuleItems.Add(CreateItem(index));
            }
        }

        /// <summary>读屏名称里的组名。</summary>
        internal string AutomationLabel => "星级筛选";

        /// <summary>段按钮上的当前条件摘要（全部 / 单选项 / 多选）。</summary>
        internal TextBlock Label { get; }

        /// <summary>段菜单的星级切换项。</summary>
        internal List<ToggleMenuFlyoutItem> SegmentItems { get; } = new();

        /// <summary>单胶囊菜单的星级切换项。</summary>
        internal List<ToggleMenuFlyoutItem> CapsuleItems { get; } = new();

        /// <summary>按共享条件集回填两套菜单的勾选态与段摘要。</summary>
        internal void SyncChecks()
        {
            for (var index = 0; index < _filter.RatingFilters.Count; index++)
            {
                bool selected = _filter.IsRatingFilterSelected(_filter.RatingFilters[index].Key);
                SegmentItems[index].IsChecked = selected;
                CapsuleItems[index].IsChecked = selected;
            }

            Label.Text = _filter.RatingFilterLabel;
        }

        /// <summary>创建一个原生多选菜单项；点选只切换共享条件，勾选随后由共享状态回填。</summary>
        /// <param name="index">选项下标。</param>
        /// <returns>菜单项。</returns>
        private ToggleMenuFlyoutItem CreateItem(int index)
        {
            var option = _filter.RatingFilters[index];
            var item = new ToggleMenuFlyoutItem
            {
                Text = option.DisplayName,
                Icon = CreateMenuIcon(RatingGlyph(option.Key)),
            };
            item.Click += (_, _) =>
            {
                _filter.ToggleRatingFilter(option.Key);
                // WinUI 在 Click 之前已翻转本项 IsChecked：空条件点「全部」会被系统取消勾选，
                // 这里按共享条件立即回填，「全部」与多选的真实状态始终一致（FILTER-03/08）。
                SyncChecks();
            };
            return item;
        }
    }
}
