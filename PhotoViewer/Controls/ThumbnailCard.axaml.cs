using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using PhotoViewer.Core.Image;
using PhotoViewer.ViewModels.Main;
using System.Windows.Input;

namespace PhotoViewer.Controls;

/// <summary>
/// 通用照片缩略图卡片。
/// 视觉结构:90×138 外框 + 80×80 缩略图 + 文件名 + 自定义第二行 + 6 星级按钮。
/// 由主缩略图列表与相似聚类列表共用;差异仅通过属性接入:
///   - <see cref="File"/>:卡片绑定的文件
///   - <see cref="IsSelected"/>:选中边框来源(主列表=IsCurrent,相似列表=IsCurrentImage)
///   - <see cref="SecondLineText"/> / <see cref="SecondLineToolTip"/>:第二行(拍摄时间 / 相似度分数)
///   - <see cref="ShowRating"/>:星级按钮是否可见
///   - <see cref="SelectCommand"/> + <see cref="SelectCommandParameter"/>:点击卡片触发的命令
///   - <see cref="Main"/>:用于星级点击直接调用 <see cref="MainViewModel.SetRatingAsync"/>
/// </summary>
public partial class ThumbnailCard : UserControl
{
    /// <summary>绑定的照片文件,卡片内部子绑定(缩略图、文件名、评分等)走此属性转发。</summary>
    public static readonly StyledProperty<ImageFile?> FileProperty =
        AvaloniaProperty.Register<ThumbnailCard, ImageFile?>(nameof(File));

    /// <summary>绑定的照片文件。</summary>
    public ImageFile? File
    {
        get => GetValue(FileProperty);
        set => SetValue(FileProperty, value);
    }

    /// <summary>是否选中(驱动描边高亮),调用方决定绑 IsCurrent 还是 IsCurrentImage。</summary>
    public static readonly StyledProperty<bool> IsSelectedProperty =
        AvaloniaProperty.Register<ThumbnailCard, bool>(nameof(IsSelected));

    /// <summary>是否选中。</summary>
    public bool IsSelected
    {
        get => GetValue(IsSelectedProperty);
        set => SetValue(IsSelectedProperty, value);
    }

    /// <summary>第二行文案(例如拍摄时间、相似度百分比)。</summary>
    public static readonly StyledProperty<string?> SecondLineTextProperty =
        AvaloniaProperty.Register<ThumbnailCard, string?>(nameof(SecondLineText));

    /// <summary>第二行文案。</summary>
    public string? SecondLineText
    {
        get => GetValue(SecondLineTextProperty);
        set => SetValue(SecondLineTextProperty, value);
    }

    /// <summary>第二行 ToolTip(相似聚类传 null 即可)。</summary>
    public static readonly StyledProperty<string?> SecondLineToolTipProperty =
        AvaloniaProperty.Register<ThumbnailCard, string?>(nameof(SecondLineToolTip));

    /// <summary>第二行 ToolTip。</summary>
    public string? SecondLineToolTip
    {
        get => GetValue(SecondLineToolTipProperty);
        set => SetValue(SecondLineToolTipProperty, value);
    }

    /// <summary>
    /// 第二行进度条占比([0,1])。非 null 时在文本背后绘制一条"柱状条",左侧填色按比例延伸;
    /// null 表示不显示(主缩略图列表用法)。相似聚类列表把分数 75%~100% 映射到此区间。
    /// </summary>
    public static readonly StyledProperty<double?> SecondLineProgressProperty =
        AvaloniaProperty.Register<ThumbnailCard, double?>(nameof(SecondLineProgress));

    /// <summary>第二行进度条占比。</summary>
    public double? SecondLineProgress
    {
        get => GetValue(SecondLineProgressProperty);
        set => SetValue(SecondLineProgressProperty, value);
    }

    /// <summary>是否显示星级按钮,等同于 Settings.ShowRating。</summary>
    public static readonly StyledProperty<bool> ShowRatingProperty =
        AvaloniaProperty.Register<ThumbnailCard, bool>(nameof(ShowRating));

    /// <summary>是否显示星级按钮。</summary>
    public bool ShowRating
    {
        get => GetValue(ShowRatingProperty);
        set => SetValue(ShowRatingProperty, value);
    }

    /// <summary>点击卡片触发的选中命令(主列表:SelectImageCommand / 相似列表:SelectItemCommand)。</summary>
    public static readonly StyledProperty<ICommand?> SelectCommandProperty =
        AvaloniaProperty.Register<ThumbnailCard, ICommand?>(nameof(SelectCommand));

    /// <summary>选中命令。</summary>
    public ICommand? SelectCommand
    {
        get => GetValue(SelectCommandProperty);
        set => SetValue(SelectCommandProperty, value);
    }

    /// <summary>选中命令的参数(主列表:ImageFile / 相似列表:SimilarityItem)。</summary>
    public static readonly StyledProperty<object?> SelectCommandParameterProperty =
        AvaloniaProperty.Register<ThumbnailCard, object?>(nameof(SelectCommandParameter));

    /// <summary>选中命令的参数。</summary>
    public object? SelectCommandParameter
    {
        get => GetValue(SelectCommandParameterProperty);
        set => SetValue(SelectCommandParameterProperty, value);
    }

    /// <summary>主视图模型引用,星级点击时调用 <see cref="MainViewModel.SetRatingAsync"/>。</summary>
    public static readonly StyledProperty<MainViewModel?> MainProperty =
        AvaloniaProperty.Register<ThumbnailCard, MainViewModel?>(nameof(Main));

    /// <summary>主视图模型引用。</summary>
    public MainViewModel? Main
    {
        get => GetValue(MainProperty);
        set => SetValue(MainProperty, value);
    }

    public ThumbnailCard()
    {
        InitializeComponent();
    }

    /// <summary>
    /// 星级按钮点击:从 Tag 解析星级,调用 <see cref="MainViewModel.SetRatingAsync"/>。
    /// 由于 <see cref="File"/> 在 ItemsControl 虚拟化场景下由容器的 DataContext 转发设置,
    /// 直接从自身 <see cref="File"/> 属性取值即可,无须从 Button 的 DataContext 反推。
    /// </summary>
    private void OnStarClick(object? sender, RoutedEventArgs e)
    {
        var main = Main;
        var file = File;
        if (main == null || file == null) return;

        if (sender is Button btn &&
            btn.Tag is string s &&
            int.TryParse(s, out var rating))
        {
            _ = main.SetRatingAsync(file, rating);
        }
    }
}
