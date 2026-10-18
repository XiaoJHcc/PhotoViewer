using CoreGraphics;
using PhotoViewer.Apple;
using PhotoViewer.Core.Design;
using PhotoViewer.ViewModels.Main;
using PhotoViewer.ViewModels.Main.File;
using UIKit;

namespace PhotoViewer.iOS.Native;

/// <summary>
/// 文件栏筛选条（[FILTER-01]）：排序 / 升降序 / 星级筛选三个 UIButton 走原生下拉菜单
/// （<c>ShowsMenuAsPrimaryAction</c> + UIMenu），外加计数文本；几何全部取共享 <see cref="MainScreenLayout"/>，
/// 与 Mac 原生壳同一份 90×32 / 间距 5 / 外边距 10 的口径，只把结果落成 UIKit frame。
/// 相似聚类 toggle（90×27）在 I5 起显示，点击写共享 <see cref="FilterBarViewModel.IsSimilarityPanelOpen"/>，
/// 面板本体由 <see cref="iOSSimilarityPanel"/> 渲染。
/// </summary>
internal sealed class iOSFilterBar : UIView
{
    /// <summary>相似聚类 toggle 是否参与筛选条布局（I5 起显示）。</summary>
    internal const bool ShowsSimilarityToggle = true;

    private readonly MainViewModel _main;
    private readonly UIButton _sort;
    private readonly UIButton _order;
    private readonly UIButton _rating;
    private readonly UILabel _count = new();
    private readonly UIButton _similarity;

    /// <summary>排序控件（验收读取几何）。</summary>
    internal UIButton SortControl => _sort;
    /// <summary>升降序控件。</summary>
    internal UIButton OrderControl => _order;
    /// <summary>星级筛选控件。</summary>
    internal UIButton RatingControl => _rating;
    /// <summary>筛选后计数文本。</summary>
    internal UILabel CountLabel => _count;
    /// <summary>相似聚类 toggle。</summary>
    internal UIButton SimilarityToggle => _similarity;
    /// <summary>计数文本的自然宽度（本轮布局用）。</summary>
    internal double CountWidth { get; private set; }

    /// <summary>构建三个下拉控件、计数文本与相似聚类 toggle。</summary>
    /// <param name="main">共享主 VM。</param>
    public iOSFilterBar(MainViewModel main) : base(CGRect.Empty)
    {
        _main = main;
        BackgroundColor = UIColor.Clear;
        _sort = Control();
        _order = Control();
        _rating = Control();
        _count.Font = UIFont.SystemFontOfSize(12);
        _count.TextColor = UIColor.FromRGB(0xDD, 0xDD, 0xDD);
        _count.TextAlignment = UITextAlignment.Center;
        _similarity = Control();
        _similarity.SetTitle("相似聚类", UIControlState.Normal);
        _similarity.AccessibilityHint = "展开/关闭相似聚类面板";
        _similarity.Hidden = !ShowsSimilarityToggle;
        _similarity.TouchUpInside += (_, _) => _main.FileVM.FilterBar.IsSimilarityPanelOpen = !_main.FileVM.FilterBar.IsSimilarityPanelOpen;
        SyncToggle();
        AddSubview(_sort);
        AddSubview(_order);
        AddSubview(_rating);
        AddSubview(_count);
        AddSubview(_similarity);
        Refresh();
    }

    /// <summary>同步共享筛选状态到原生控件（程序化改动与文件源刷新都不留旧选中项）。</summary>
    internal void Refresh()
    {
        var filter = _main.FileVM.FilterBar;
        _count.Text = $"共 {filter.FilteredCount} 张";
        _sort.SetTitle(filter.SortModes.FirstOrDefault(option => (SortMode)option.Value == filter.SortMode)?.DisplayName ?? "", UIControlState.Normal);
        _order.SetTitle(filter.SortOrders.FirstOrDefault(option => (SortOrder)option.Value == filter.SortOrder)?.DisplayName ?? "", UIControlState.Normal);
        _rating.SetTitle(filter.RatingFilters.FirstOrDefault(option => option.Key == filter.SelectedRatingFilter)?.DisplayName ?? "", UIControlState.Normal);
        SyncToggle();
        BindMenu(_sort, filter.SortModes.Select(option => (option.DisplayName, (object)(SortMode)option.Value)).ToArray(), filter.SortMode);
        BindMenu(_order, filter.SortOrders.Select(option => (option.DisplayName, (object)(SortOrder)option.Value)).ToArray(), filter.SortOrder);
        BindMenu(_rating, filter.RatingFilters.Select(option => (option.DisplayName, (object)option.Key)).ToArray(), filter.SelectedRatingFilter);
    }

    /// <summary>
    /// 同步相似聚类 toggle 的勾选态：FILTER-01 视觉取令牌四色（深蓝底/蓝边/浅蓝字，未选中 #AAA 字）。
    /// </summary>
    private void SyncToggle()
    {
        bool open = _main.FileVM.FilterBar.IsSimilarityPanelOpen;
        _similarity.BackgroundColor = open
            ? iOSColors.FromToken(DesignTokens.ColorFilterToggleCheckedBackground)
            : UIColor.FromWhiteAlpha(2f / 15, 1);
        _similarity.Layer.BorderColor = (open
            ? iOSColors.FromToken(DesignTokens.ColorFilterToggleCheckedBorder)
            : iOSColors.FromToken(DesignTokens.ColorFilterToggleBorder)).CGColor;
        _similarity.SetTitleColor(open
            ? iOSColors.FromToken(DesignTokens.ColorFilterToggleCheckedForeground)
            : iOSColors.FromToken(DesignTokens.ColorFilterToggleForeground), UIControlState.Normal);
        _similarity.AccessibilityValue = open ? "已展开" : "已收起";
    }

    /// <summary>按共享几何落位五个控件，并把计数文本量得的自然宽度交回布局。</summary>
    /// <param name="horizontal">是否行布局。</param>
    /// <param name="contentWidth">筛选条可用内容宽度（行布局为文件栏内容宽，列布局固定 116）。</param>
    /// <param name="leadingInset">列布局首控件的前导让位。</param>
    internal void Arrange(bool horizontal, double contentWidth, double leadingInset)
    {
        _count.SizeToFit();
        CountWidth = Math.Ceiling((double)_count.Frame.Width);
        // iPad 没有 macOS 的交通灯：行布局从安全区前导边 + 普通外边距起排，不在左上留空带。
        var frames = MainScreenLayout.FilterBarFrames(horizontal, contentWidth, leadingInset, CountWidth, ShowsSimilarityToggle,
            alignLeading: true);
        _sort.Frame = ToCGRect(frames.Sort);
        _order.Frame = ToCGRect(frames.Order);
        _rating.Frame = ToCGRect(frames.Rating);
        _count.Frame = ToCGRect(frames.Count);
        _similarity.Frame = ToCGRect(frames.Toggle);
    }

    /// <summary>按共享布局矩形落成 UIKit frame。</summary>
    /// <param name="rect">共享布局矩形。</param>
    /// <returns>UIKit frame。</returns>
    private static CGRect ToCGRect(LayoutRect rect) => new(rect.X, rect.Y, rect.Width, rect.Height);

    /// <summary>生成与 Mac 下拉同一外观定位的筛选按钮：90×32、圆角 6、深色底与描边。</summary>
    /// <returns>配置完成的按钮。</returns>
    private static UIButton Control()
    {
        var button = new UIButton(UIButtonType.System)
        {
            BackgroundColor = UIColor.FromWhiteAlpha(2f / 15, 1),
            AccessibilityHint = "下拉选择",
        };
        button.Layer.CornerRadius = 6;
        button.Layer.BorderWidth = 1;
        button.Layer.BorderColor = UIColor.FromRGB(0x44, 0x44, 0x44).CGColor;
        button.SetTitleColor(UIColor.White, UIControlState.Normal);
        button.TitleLabel!.Font = UIFont.SystemFontOfSize(13);
        button.TitleLabel.LineBreakMode = UILineBreakMode.TailTruncation;
        button.ShowsMenuAsPrimaryAction = true;
        return button;
    }

    /// <summary>用共享选项列表重建下拉菜单，当前项带勾选标记；选择即写共享状态（与 Mac 同一业务路径）。</summary>
    /// <param name="button">目标按钮。</param>
    /// <param name="options">选项（显示名 + 业务值）。</param>
    /// <param name="selected">当前业务值。</param>
    private void BindMenu(UIButton button, (string Title, object Value)[] options, object selected)
    {
        var actions = new UIMenuElement[options.Length];
        for (int index = 0; index < options.Length; index++)
        {
            int captured = index;
            var action = UIAction.Create(options[index].Title, null, null, _ => Apply(button, options[captured].Value));
            action.State = Equals(options[index].Value, selected) ? UIMenuElementState.On : UIMenuElementState.Off;
            actions[index] = action;
        }
        button.Menu = UIMenu.Create(actions);
    }

    /// <summary>把菜单选择写回共享筛选状态。</summary>
    /// <param name="button">来源按钮。</param>
    /// <param name="value">选中的业务值。</param>
    private void Apply(UIButton button, object value)
    {
        var filter = _main.FileVM.FilterBar;
        if (ReferenceEquals(button, _sort)) filter.SortMode = (SortMode)value;
        else if (ReferenceEquals(button, _order)) filter.SortOrder = (SortOrder)value;
        else if (ReferenceEquals(button, _rating)) filter.SelectedRatingFilter = (string)value;
    }
}
