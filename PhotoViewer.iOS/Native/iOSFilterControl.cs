using CoreGraphics;
using PhotoViewer.Core.Design;
using PhotoViewer.ViewModels.Main;
using PhotoViewer.ViewModels.Main.File;
using UIKit;

namespace PhotoViewer.iOS.Native;

/// <summary>
/// 宽形态筛选控件的落位结果（FILTER-07，iOS）：两段合体胶囊、筛选/更多两段、相似聚类开关与控件主轴长度。
/// </summary>
/// <param name="Control">合体胶囊矩形（文件栏内容区坐标）。</param>
/// <param name="Filter">筛选段（相对胶囊左上角）。</param>
/// <param name="More">更多段（相对胶囊左上角）。</param>
/// <param name="Toggle">相似聚类开关矩形（文件栏内容区坐标；标准版为零矩形）。</param>
/// <param name="MainLength">筛选控件自身的主轴长度（含外边距）。</param>
internal readonly record struct iOSFilterFrames(
    LayoutRect Control,
    LayoutRect Filter,
    LayoutRect More,
    LayoutRect Toggle,
    double MainLength);

/// <summary>
/// 宽形态筛选控件的纯几何（FILTER-07，iOS）：筛选/更多两段合体宽 88（2 × 44），列布局在 116 带内居中，
/// 行布局把合体与相似聚类开关当成一组按自然宽居中；列布局开关 96 宽落在合体下方隔 8 点。
/// 全部以点为单位，只做算术，不依赖 UIKit。
/// </summary>
internal static class iOSFilterLayout
{
    /// <summary>单段边长（也是控件高）。</summary>
    internal static double Segment => DesignTokens.ShellGlassButtonSize;

    /// <summary>两段合体宽（筛选 + 更多）。</summary>
    internal static double GroupWidth => Segment * 2;

    /// <summary>控件与相似聚类开关之间的间距。</summary>
    internal static double Gap => DesignTokens.ShellGlassGroupGap;

    /// <summary>行布局的外边距（同时也是控件到内容区边缘的最小距离）。</summary>
    internal static double Margin => MainScreenLayout.FilterMargin;

    /// <summary>控件自身的主轴长度：行布局为高加两侧外边距，列布局为前导让位、合体、开关与尾部外边距之和。</summary>
    /// <param name="row">是否行布局。</param>
    /// <param name="leadingInset">列布局的前导让位（行布局忽略）。</param>
    /// <param name="includeToggle">是否显示相似聚类开关（标准版为 false）。</param>
    /// <returns>主轴长度。</returns>
    internal static double MainLength(bool row, double leadingInset, bool includeToggle)
        => row
            ? Segment + Margin * 2
            : leadingInset + Segment + (includeToggle ? Gap + Segment : 0) + Margin;

    /// <summary>
    /// 计算合体胶囊、两段与相似聚类开关的矩形：行布局整组（含开关）自然宽居中，
    /// 列布局合体在内容带内居中、开关在合体下方隔 <see cref="Gap"/>。
    /// </summary>
    /// <param name="row">是否行布局。</param>
    /// <param name="contentWidth">文件栏内容区宽度（列布局即 116 带）。</param>
    /// <param name="leadingInset">列布局的前导让位。</param>
    /// <param name="toggleWidth">相似聚类开关宽度（行布局为自然宽，列布局为卡片列宽）；0 表示没有开关。</param>
    /// <returns>各矩形与主轴长度。</returns>
    internal static iOSFilterFrames Compute(bool row, double contentWidth, double leadingInset, double toggleWidth)
    {
        bool includeToggle = toggleWidth > 0;
        if (row)
        {
            double total = GroupWidth + (includeToggle ? Gap + toggleWidth : 0);
            double start = Math.Max(Margin, (contentWidth - total) / 2);
            var control = new LayoutRect(start, Margin, GroupWidth, Segment);
            var toggle = includeToggle
                ? new LayoutRect(control.MaxX + Gap, Margin, toggleWidth, Segment)
                : default;
            return new iOSFilterFrames(control, SegmentRect(0), SegmentRect(1), toggle, Segment + Margin * 2);
        }

        var column = new LayoutRect((contentWidth - GroupWidth) / 2, leadingInset, GroupWidth, Segment);
        var columnToggle = includeToggle
            ? new LayoutRect((contentWidth - toggleWidth) / 2, column.MaxY + Gap, toggleWidth, Segment)
            : default;
        return new iOSFilterFrames(column, SegmentRect(0), SegmentRect(1), columnToggle,
            leadingInset + Segment + (includeToggle ? Gap + Segment : 0) + Margin);
    }

    /// <summary>第几段的局部矩形：两段沿主轴首尾相接，高占满控件。</summary>
    /// <param name="index">段序（0 = 筛选，1 = 更多）。</param>
    /// <returns>相对胶囊左上角的矩形。</returns>
    private static LayoutRect SegmentRect(int index) => new(index * Segment, 0, Segment, Segment);
}

/// <summary>
/// 宽形态筛选控件（FILTER-07）：一个交互式玻璃胶囊内并排两段——筛选段弹出与手机形态相同的三分组菜单
/// （点选不关闭、失焦关闭），更多段承接宿主传入的主图菜单（与手机「更多」同源，
/// 经 <see cref="SetMoreMenu"/> 一次性接入）。AI 版另有相似聚类开关，行布局紧随合体、列布局落在合体下方同宽。
/// <para>几何取本文件内的纯函数 <see cref="iOSFilterLayout"/>：合体宽 88（2 × 44），列布局在 116 带内居中，
/// 行布局整组（含开关）自然宽居中；本类只把结果落成 UIKit frame，并把筛选菜单交
/// <see cref="iOSFilterMenu"/> 按稳定 action 标识就地刷新。旧的三段合体与单胶囊形态不再创建。</para>
/// </summary>
internal sealed class iOSFilterControl : UIView
{
    /// <summary>相似聚类开关是否参与布局（仅 AI 版）。</summary>
    internal const bool ShowsSimilarityToggle = iOSAppCapabilities.HasDino;

    private readonly MainViewModel _main;
    private readonly iOSGlassButtonGroup _group;
    private readonly UIButton _filter;
    private readonly UIButton _more;
    private readonly UIButton? _similarity;
    private iOSFilterFrames _frames;

    /// <summary>筛选/更多合体胶囊（宿主据此挂系统滚动边缘交互）。</summary>
    internal iOSGlassButtonGroup CombinedGroup => _group;

    /// <summary>筛选段按钮（弹出三分组筛选菜单）。</summary>
    internal UIButton FilterButton => _filter;

    /// <summary>更多段按钮（主图菜单，内容由宿主提供）。</summary>
    internal UIButton MoreButton => _more;

    /// <summary>相似聚类开关；标准版不创建该控件，为 null。</summary>
    internal UIButton? SimilarityToggle => _similarity;

    /// <summary>最近一次排布算出的各矩形与主轴长度。</summary>
    internal iOSFilterFrames Frames => _frames;

    /// <summary>构建合体胶囊（筛选 / 更多两段）与相似聚类开关，并接入当前筛选状态。</summary>
    /// <param name="main">共享主 VM。</param>
    public iOSFilterControl(MainViewModel main) : base(CGRect.Empty)
    {
        _main = main;
        BackgroundColor = UIColor.Clear;
        _group = new iOSGlassButtonGroup();
        _filter = _group.Add("line.3.horizontal.decrease", "筛选", null);
        _filter.ShowsMenuAsPrimaryAction = true;
        _filter.AccessibilityHint = "下拉选择排序、方向与星级筛选";
        _more = _group.Add("ellipsis", "更多", null);
        _more.ShowsMenuAsPrimaryAction = true;
        AddSubview(_group);

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

    public override UIView? HitTest(CGPoint point, UIEvent? uievent)
    {
        var hit = base.HitTest(point, uievent);
        return ReferenceEquals(hit, this) ? null : hit;
    }

    /// <summary>接入更多段的菜单（与手机形态「更多」同源，内容不在此另建）。</summary>
    /// <param name="menu">更多段菜单。</param>
    internal void SetMoreMenu(UIMenu menu) => _more.Menu = menu;

    /// <summary>
    /// 同步共享筛选状态到原生控件：筛选段菜单按稳定标识就地刷新勾选（展开中的菜单不重建、不回到顶部），
    /// 读屏值取当前星级筛选，相似聚类开关同步外观。
    /// </summary>
    internal void Refresh()
    {
        var filter = _main.FileVM.FilterBar;
        iOSFilterMenu.UpdateVisibleMenu(_filter, filter, Refresh);
        _filter.AccessibilityValue = filter.RatingFilters.FirstOrDefault(option => option.Key == filter.SelectedRatingFilter)?.DisplayName ?? "";
        RefreshToggle();
    }

    /// <summary>
    /// 筛选控件自身的主轴长度：行布局为控制高加上下外边距；列布局为前导让位、合体、相似聚类开关（含组间距）
    /// 与尾部外边距之和。
    /// </summary>
    /// <param name="row">是否行布局。</param>
    /// <param name="leadingInset">列布局的前导让位。</param>
    /// <returns>主轴长度（行布局为高，列布局为内容区内的筛选段长度）。</returns>
    internal double MainLength(bool row, double leadingInset)
        => iOSFilterLayout.MainLength(row, leadingInset, _similarity != null);

    /// <summary>
    /// 按纯几何落位：合体胶囊、两段与相似聚类开关。
    /// </summary>
    /// <param name="row">是否行布局。</param>
    /// <param name="contentWidth">文件栏内容区宽度（列布局为 116 带）。</param>
    /// <param name="cardWidth">列布局的胶片卡片列宽（相似聚类开关与卡片列同宽）。</param>
    /// <param name="leadingInset">列布局的前导让位。</param>
    /// <returns>各矩形与主轴长度。</returns>
    internal iOSFilterFrames Arrange(bool row, double contentWidth, double cardWidth, double leadingInset)
    {
        double toggleWidth = _similarity == null ? 0 : row ? NaturalWidth(_similarity) : cardWidth;
        var frames = iOSFilterLayout.Compute(row, contentWidth, leadingInset, toggleWidth);
        _frames = frames;
        _group.Vertical = false;
        _group.Frame = ToCGRect(frames.Control);
        _group.LayoutIfNeeded();
        if (_similarity != null) _similarity.Frame = ToCGRect(frames.Toggle);
        return frames;
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

    /// <summary>同步相似聚类开关的外观与读屏值。</summary>
    private void RefreshToggle()
    {
        if (_similarity == null) return;
        bool open = _main.FileVM.FilterBar.IsSimilarityPanelOpen;
        _similarity.Configuration = ToggleConfiguration(open);
        _similarity.AccessibilityValue = open ? "已展开" : "已收起";
    }

    /// <summary>原生量得的自然宽度（向上取整，含 configuration 内边距）。</summary>
    /// <param name="button">目标按钮。</param>
    /// <returns>宽度，至少 1 点。</returns>
    private static double NaturalWidth(UIButton button)
    {
        double width = Math.Ceiling((double)button.SizeThatFits(
            new CGSize(0, (nfloat)DesignTokens.ShellGlassButtonSize)).Width);
        if (width <= 0) width = Math.Ceiling((double)button.IntrinsicContentSize.Width);
        return Math.Max(1, width);
    }

    /// <summary>共享布局矩形 → UIKit frame。</summary>
    /// <param name="rect">共享布局矩形。</param>
    /// <returns>UIKit frame。</returns>
    private static CGRect ToCGRect(LayoutRect rect) => new(rect.X, rect.Y, rect.Width, rect.Height);
}
