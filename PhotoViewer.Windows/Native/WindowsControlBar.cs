using System;
using System.Collections.Generic;
using System.ComponentModel;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using PhotoViewer.Core.Design;
using PhotoViewer.Core.Image;
using PhotoViewer.Core.Input;
using PhotoViewer.Core.Platform;
using PhotoViewer.Shared;
using PhotoViewer.ViewModels.Main;
using PhotoViewer.ViewModels.Settings;
using Windows.Foundation;
using Windows.UI;

namespace PhotoViewer.Windows.Native;

/// <summary>
/// 原生控制栏:EXIF 摘要、评分胶囊、命令三个整体分组,顺序固定,空间不足时整组换行/换列并在行内居中
/// (TOOLBAR-01、TOOLBAR-02)。分组尺寸与换行全部取共享 <see cref="ToolbarFlowLayout"/>,
/// 分组内容来自共享设置,壳不维护第二份工具栏配置。
/// <para>
/// 控制栏自身不画背景与分隔线:工具区的模糊材质与朝主图的外边界由主窗口承担(SHELL-12),
/// 只有评分胶囊与命令圆按钮保留各自的原生按钮材质,主窗口按 <see cref="GetMaterialShapes"/> 为它们铺背板。
/// </para>
/// </summary>
internal sealed class WindowsControlBar : Grid
{
    /// <summary>未点亮星形的描边字形(<c>icon.star</c>)。</summary>
    private const string StarOutlineGlyph = "\uE734";

    /// <summary>EXIF 块衬底底色(WinUI 卡片材质)。</summary>
    private const string ExifBlockBackgroundKey = "CardBackgroundFillColorDefaultBrush";

    /// <summary>EXIF 块衬底描边。</summary>
    private const string ExifBlockStrokeKey = "CardStrokeColorDefaultBrush";

    /// <summary>评分胶囊的按钮材质底色。</summary>
    private const string CapsuleBackgroundKey = "ControlFillColorDefaultBrush";

    /// <summary>评分胶囊的按钮材质描边(原生按钮的抬升描边)。</summary>
    private const string CapsuleStrokeKey = "ControlElevationBorderBrush";

    /// <summary>EXIF 标签文字色。</summary>
    private const string ExifLabelBrushKey = "TextFillColorSecondaryBrush";

    /// <summary>EXIF 数值文字色。</summary>
    private const string ExifValueBrushKey = "TextFillColorPrimaryBrush";

    private readonly MainViewModel _main;
    private readonly Action<string> _execute;
    private readonly Action<Button> _showSources;
    private readonly Action? _openTools;

    private readonly ScrollViewer _scroll;
    private readonly ControlDocumentPanel _document;
    private readonly Grid _exifHost;
    private readonly Border _exifChrome;
    private readonly PlacedPanel _exifPanel;
    private readonly List<ExifItem> _exifItems = new();
    private readonly Grid _ratingHost;
    private readonly Border _ratingChrome;
    private readonly PlacedPanel _ratingPanel;
    private readonly List<RatingSlot> _ratingSlots = new();
    private readonly PlacedPanel _commandHost;
    private readonly Dictionary<string, Button> _commands = new(StringComparer.Ordinal);
    private readonly List<string> _commandOrder = new();
    private ImageFile? _currentFile;
    private bool _refreshPending;
    private bool _fileRefreshPending;
    private bool _ratingRefreshPending;
    private bool _commandsRefreshPending;
    private bool _isRowLayout = true;
    private bool _joined;
    private bool _restoreScrollPending;
    private bool _materialShapesPending;
    private bool _detached;
    private double _savedHorizontalOffset;
    private double _savedVerticalOffset;
    private double _hostAvailableMain;
    private int _lineCount = 1;
    private (int Exif, bool Rating, int Commands) _contentSignature;
    private List<Rect> _reportedShapes = new();

    /// <summary>文档尺寸相关结构变化后触发(EXIF 项数、评分显隐或命令数),宿主据此重算控制栏主轴长度。</summary>
    internal event Action? ContentChanged;

    /// <summary>玻璃控件的实际位置变化后(投递,不在布局回调内)触发,主窗口按它重画亚克力背板(SHELL-12)。</summary>
    internal event Action? MaterialShapesChanged;

    /// <summary>
    /// 创建控制栏。
    /// </summary>
    /// <param name="main">主视图模型</param>
    /// <param name="execute">命令执行入口(原生壳的命令分发)</param>
    /// <param name="showSources">冲突三角的来源浮窗入口</param>
    /// <param name="openTools">EXIF 组点击时打开工具窗口;null 表示不接线</param>
    public WindowsControlBar(MainViewModel main, Action<string> execute, Action<Button> showSources,
        Action? openTools = null)
    {
        _main = main;
        _execute = execute;
        _showSources = showSources;
        _openTools = openTools;

        // TOOLBAR-06：整个 EXIF 组(含块间留白)是一个点击目标，透明底让留白也能命中。
        _exifHost = new Grid { Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent) };
        _exifHost.Tapped += (_, _) => _openTools?.Invoke();
        _exifChrome = new Border
        {
            IsHitTestVisible = false,
            Background = WindowsControlStyles.ThemeBrush(ExifBlockBackgroundKey, WindowsLayout.BlockBackground),
            BorderBrush = WindowsControlStyles.ThemeBrush(ExifBlockStrokeKey, WindowsLayout.Divider),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(ExifBlockGeometry.CornerRadius),
        };
        _exifPanel = new PlacedPanel();
        _exifHost.Children.Add(_exifChrome);
        _exifHost.Children.Add(_exifPanel);
        if (_openTools != null)
        {
            ToolTipService.SetToolTip(_exifHost, "工具");
        }

        // RATING-07：评分组是可点的胶囊外框，六个槽位按钮直接排在胶囊内容区上。
        _ratingHost = new Grid();
        _ratingChrome = new Border
        {
            IsHitTestVisible = false,
            Background = WindowsControlStyles.ThemeBrush(CapsuleBackgroundKey, WindowsLayout.CommandButtonBackground),
            BorderBrush = WindowsControlStyles.ThemeBrush(CapsuleStrokeKey, WindowsLayout.CommandButtonBorder),
            BorderThickness = new Thickness(1),
        };
        _ratingPanel = new PlacedPanel();
        _ratingHost.Children.Add(_ratingChrome);
        _ratingHost.Children.Add(_ratingPanel);
        for (var value = 0; value < RatingControlGeometry.SlotCount; value++)
        {
            var slot = CreateRatingSlot(value);
            _ratingSlots.Add(slot);
            _ratingPanel.Children.Add(slot.Button);
        }

        _commandHost = new PlacedPanel();

        _document = new ControlDocumentPanel(this);
        // TOOLBAR-08：宽形态始终有滚动宿主，只有单组超过整条主轴时才出现滚动条。
        _scroll = new ScrollViewer
        {
            Content = _document,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            HorizontalScrollMode = ScrollMode.Enabled,
            VerticalScrollMode = ScrollMode.Disabled,
            ZoomMode = ZoomMode.Disabled,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            VerticalContentAlignment = VerticalAlignment.Top,
        };
        _scroll.LayoutUpdated += (_, _) => ApplyPendingScroll();
        _scroll.SizeChanged += (_, _) =>
        {
            // 视口变化会改变换行结果与可见玻璃范围，两者都不在布局回调里同步处理。
            _document.InvalidateMeasure();
            ScheduleMaterialShapesChanged();
        };
        _scroll.ViewChanged += (_, _) => ScheduleMaterialShapesChanged();
        Children.Add(_scroll);

        _main.PropertyChanged += OnMainChanged;
        _main.ControlVM.PropertyChanged += OnControlChanged;
        _main.Settings.PresentationChanged += OnSettingsPresentationChanged;
        AttachCurrentFile(_main.CurrentFile);
        Refresh();
    }

    /// <summary>
    /// 切换主轴方向：行布局三组沿宽横排、控制栏横向滚动；列布局三组沿高竖排、控制栏纵向滚动。
    /// EXIF 块随方向在衬底横排与无衬底纵排之间换形(TOOLBAR-05)。
    /// </summary>
    /// <param name="isRowLayout">是否为上下布局</param>
    public void SetOrientation(bool isRowLayout)
    {
        if (_isRowLayout == isRowLayout)
        {
            return;
        }

        SaveScrollOffset();
        _isRowLayout = isRowLayout;
        _scroll.HorizontalScrollMode = isRowLayout ? ScrollMode.Enabled : ScrollMode.Disabled;
        _scroll.VerticalScrollMode = isRowLayout ? ScrollMode.Disabled : ScrollMode.Enabled;
        _scroll.HorizontalScrollBarVisibility = isRowLayout ? ScrollBarVisibility.Auto : ScrollBarVisibility.Disabled;
        _scroll.VerticalScrollBarVisibility = isRowLayout ? ScrollBarVisibility.Disabled : ScrollBarVisibility.Auto;
        ApplyGeometry();
        _document.InvalidateMeasure();
        RestoreScrollOffset();
    }

    /// <summary>
    /// 是否与相邻分析栏共处同一工具区(SHELL-12)：共处时交叉轴前导内边距为 0，接缝只在分析内容一侧。
    /// </summary>
    /// <param name="joined">是否相邻</param>
    internal void SetJoined(bool joined)
    {
        if (_joined == joined)
        {
            return;
        }

        _joined = joined;
        _document.InvalidateMeasure();
    }

    /// <summary>
    /// 按当前内容求控制栏文档尺寸，供宿主在排布前决定控制栏主轴长度(TOOLBAR-07)。
    /// 纯查询：不改动视图，也不依赖已经排布过的视口。
    /// </summary>
    /// <param name="horizontal">文档主轴是否为水平方向。</param>
    /// <param name="availableMain">控制栏沿主轴的可用长度(整条控制栏长度)。</param>
    /// <param name="joined">是否与相邻分析栏共处同一工具区。</param>
    /// <returns>文档宽高(已含文档外边距)。</returns>
    internal Size MeasureDocument(bool horizontal, double availableMain, bool joined)
    {
        var sizes = GroupSizes(horizontal);
        var document = ToolbarFlowLayout.Arrange(sizes, horizontal, availableMain, joined ? 0 : null);
        return new Size(document.Width, document.Height);
    }

    /// <summary>当前实际显示的 EXIF 项数(工具区材质出现条件之一，SHELL-12)。</summary>
    internal int ExifItemCount => _exifItems.Count;

    /// <summary>验收专用：当前换行/换列后的行(列)数。</summary>
    internal int GroupLineCount => _lineCount;

    /// <summary>验收专用：滚动宿主在主轴上还能滚动的长度；换行足够时为 0。</summary>
    internal double ScrollableMain => _isRowLayout ? _scroll.ScrollableWidth : _scroll.ScrollableHeight;

    /// <summary>EXIF 整组点击入口（与真实 Tapped 同一回调；验收不依赖合成手势）。</summary>
    internal void TapExifGroup() => _openTools?.Invoke();

    /// <summary>
    /// 需要玻璃材质的控件形状(SHELL-12)：评分胶囊与每个命令圆按钮，圆角 22。
    /// 只返回可见、且与滚动视口相交的形状；工具区自身没有材质时，主窗口按这些矩形铺亚克力背板。
    /// </summary>
    /// <param name="relativeTo">坐标参照元素。</param>
    /// <returns>每个形状在参照元素坐标中的矩形与其圆角。</returns>
    internal IReadOnlyList<(Rect Bounds, double CornerRadius)> GetMaterialShapes(UIElement relativeTo)
    {
        var shapes = new List<(Rect Bounds, double CornerRadius)>();
        if (XamlRoot == null || relativeTo.XamlRoot == null)
        {
            return shapes;
        }

        double radius = ToolbarFlowLayout.CommandSize / 2;
        var viewport = new Rect(0, 0, Math.Max(0, _scroll.ViewportWidth), Math.Max(0, _scroll.ViewportHeight));
        try
        {
            Add(_ratingHost, radius);
            foreach (var child in _commandHost.Children)
            {
                if (child is Button button)
                {
                    Add(button, radius);
                }
            }
        }
        catch (ArgumentException)
        {
            // 元素尚未挂进同一棵可视树时不能变换坐标，按无形状返回，下一次排布会重报。
            return new List<(Rect, double)>();
        }

        return shapes;

        void Add(FrameworkElement element, double corner)
        {
            if (element.Visibility != Visibility.Visible || element.ActualWidth <= 0 || element.ActualHeight <= 0)
            {
                return;
            }

            var bounds = new Rect(0, 0, element.ActualWidth, element.ActualHeight);
            var visible = element.TransformToVisual(_scroll).TransformBounds(bounds);
            visible.Intersect(viewport);
            if (visible.IsEmpty)
            {
                return;
            }

            shapes.Add((element.TransformToVisual(relativeTo).TransformBounds(bounds), corner));
        }
    }

    /// <summary>
    /// 释放订阅；窗口关闭后不再处理投递中的刷新与形状复核。
    /// </summary>
    public void Detach()
    {
        _detached = true;
        _main.PropertyChanged -= OnMainChanged;
        _main.ControlVM.PropertyChanged -= OnControlChanged;
        _main.Settings.PresentationChanged -= OnSettingsPresentationChanged;
        AttachCurrentFile(null);
    }

    /// <inheritdoc/>
    protected override Size MeasureOverride(Size availableSize)
    {
        // 视口尚未完成第一次布局时，用宿主给出的主轴长度兜底（宿主按 MeasureDocument 定过尺寸）。
        _hostAvailableMain = _isRowLayout ? availableSize.Width : availableSize.Height;
        return base.MeasureOverride(availableSize);
    }

    /// <summary>
    /// 重建控制栏全部内容(EXIF 项、命令按钮与评分)，再按新结构重算文档(TOOLBAR-07)。
    /// </summary>
    private void Refresh()
    {
        RefreshExif();
        RefreshCommands();
        RefreshRating();
        ApplyStructure();
    }

    /// <summary>
    /// 重建文件相关内容(EXIF 项、评分与命令可用态);命令按钮只依赖设置，不在此重建。
    /// </summary>
    private void RefreshFileParts()
    {
        RefreshExif();
        RefreshRating();
        UpdateCommandAvailability();
        ApplyStructure();
    }

    /// <summary>
    /// 结构落定后重算几何并同步文档：分组尺寸、组内矩形、参与流式排布的分组与文档尺寸变化通知。
    /// </summary>
    private void ApplyStructure()
    {
        ApplyGeometry();
        SyncDocumentGroups();
        ReportContentChanged();
    }

    /// <summary>
    /// 三个分组几何一次落到共享排布输入上：分组自身尺寸固定，交叉轴由文档在行内居中(TOOLBAR-02)。
    /// </summary>
    private void ApplyGeometry()
    {
        ApplyExifGeometry();
        ApplyRatingGeometry();
        ApplyCommandGeometry();
    }

    /// <summary>EXIF 组几何：横排是有衬底的深色圆角块，竖排取无衬底变体，各项取共享矩形(TOOLBAR-05)。</summary>
    private void ApplyExifGeometry()
    {
        var horizontal = _isRowLayout;
        var bare = !horizontal;
        int count = _exifItems.Count;
        var (width, height) = ExifBlockGeometry.Size(count, horizontal, bare);
        _exifHost.Width = width;
        _exifHost.Height = height;
        // 无衬底变体不绘制块背景与圆角，只剩项目与项距直接放在工具区材质上。
        _exifChrome.Visibility = horizontal && count > 0 ? Visibility.Visible : Visibility.Collapsed;

        double blockMain = horizontal ? width : height;
        var rects = new List<LayoutRect>(count);
        for (var index = 0; index < count; index++)
        {
            var rect = ExifBlockGeometry.ItemRect(index, count, horizontal, blockMain, bare);
            var item = _exifItems[index];
            item.Root.Width = rect.Width;
            item.Root.Height = rect.Height;
            rects.Add(rect);
        }

        _exifPanel.SetRects(rects);
    }

    /// <summary>
    /// 评分组几何(RATING-07)：胶囊恒为展开态，主轴取 <see cref="RatingControlGeometry.ControlFrame"/> 的展开长度、
    /// 交叉轴取控件厚度，六格槽位按方向取共享矩形；槽位坐标用整只胶囊，不受 1 点描边内缩影响。
    /// </summary>
    private void ApplyRatingGeometry()
    {
        var horizontal = _isRowLayout;
        double main = RatingControlGeometry.ExpandedLength;
        double cross = RatingControlGeometry.Thickness;
        _ratingHost.Width = horizontal ? main : cross;
        _ratingHost.Height = horizontal ? cross : main;
        _ratingChrome.CornerRadius = new CornerRadius(cross / 2);

        var rects = new List<LayoutRect>(RatingControlGeometry.SlotCount);
        for (var value = 0; value < RatingControlGeometry.SlotCount; value++)
        {
            // 行布局 0→5 自左向右，列布局 5 在上、0 在底端（TOOLBAR-03）。
            var rect = RatingControlGeometry.SlotRect(value, main, cross, horizontal, RatingControlGeometry.Slot);
            var slot = _ratingSlots[value];
            slot.Button.Width = rect.Width;
            slot.Button.Height = rect.Height;
            // 状态层是槽位内切正圆：直径取槽位短边，不越出槽位。
            slot.Halo.Width = Math.Min(rect.Width, rect.Height);
            slot.Halo.Height = Math.Min(rect.Width, rect.Height);
            rects.Add(rect);
        }

        _ratingPanel.SetRects(rects);
    }

    /// <summary>命令组几何：圆按钮沿主轴按固定间距排开（TOOLBAR-02）。</summary>
    private void ApplyCommandGeometry()
    {
        int count = _commandOrder.Count;
        bool horizontal = _isRowLayout;
        var size = ToolbarFlowLayout.Commands(count);
        _commandHost.Width = horizontal ? size.Main : size.Cross;
        _commandHost.Height = horizontal ? size.Cross : size.Main;
        var rects = new List<LayoutRect>(count);
        for (var index = 0; index < count; index++)
        {
            double offset = ToolbarFlowLayout.CommandOffset(index);
            rects.Add(horizontal
                ? new LayoutRect(offset, 0, ToolbarFlowLayout.CommandSize, ToolbarFlowLayout.CommandSize)
                : new LayoutRect(0, offset, ToolbarFlowLayout.CommandSize, ToolbarFlowLayout.CommandSize));
        }

        _commandHost.SetRects(rects);
    }

    /// <summary>
    /// 按 EXIF → 评分 → 命令的固定顺序投影当前参与排布的分组，空分组整组移除、不占组距(TOOLBAR-01)。
    /// </summary>
    private void SyncDocumentGroups()
    {
        var active = new List<UIElement>(3);
        _exifHost.Visibility = _exifItems.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (_exifItems.Count > 0)
        {
            active.Add(_exifHost);
        }

        _ratingHost.Visibility = RatingVisible ? Visibility.Visible : Visibility.Collapsed;
        if (RatingVisible)
        {
            active.Add(_ratingHost);
        }

        _commandHost.Visibility = _commandOrder.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (_commandOrder.Count > 0)
        {
            active.Add(_commandHost);
        }

        bool same = _document.Children.Count == active.Count;
        if (same)
        {
            for (var index = 0; index < active.Count; index++)
            {
                if (ReferenceEquals(_document.Children[index], active[index]))
                {
                    continue;
                }

                same = false;
                break;
            }
        }

        if (!same)
        {
            _document.Children.Clear();
            foreach (var host in active)
            {
                _document.Children.Add(host);
            }
        }

        _document.InvalidateMeasure();
    }

    /// <summary>
    /// 文档尺寸相关结构变化时通知宿主重算控制栏主轴长度(TOOLBAR-07)：
    /// 只比 EXIF 项数、评分显隐与命令数，评分值、EXIF 文本与命令可用态走就地刷新。
    /// </summary>
    private void ReportContentChanged()
    {
        var signature = (_exifItems.Count, RatingVisible, _commandOrder.Count);
        if (signature == _contentSignature)
        {
            return;
        }

        _contentSignature = signature;
        ContentChanged?.Invoke();
    }

    /// <summary>「显示评分」开关：关闭时整组从测量与排布中移除，命令组前移(TOOLBAR-03)。</summary>
    private bool RatingVisible => _main.ControlVM.ShowRating;

    /// <summary>按当前内容给出参与排布的分组尺寸；空分组不出现。</summary>
    /// <param name="horizontal">文档主轴是否为水平方向。</param>
    /// <returns>按 EXIF → 评分 → 命令顺序的分组尺寸。</returns>
    private List<ToolbarGroupSize> GroupSizes(bool horizontal)
    {
        var sizes = new List<ToolbarGroupSize>(3);
        if (_exifItems.Count > 0)
        {
            sizes.Add(ToolbarFlowLayout.Exif(_exifItems.Count, horizontal, bare: !horizontal));
        }

        if (RatingVisible)
        {
            // RATING-07：桌面端恒用展开态，空间不足由整组换行或滚动吸收。
            sizes.Add(ToolbarFlowLayout.Rating(RatingControlGeometry.ExpandedLength));
        }

        if (_commandOrder.Count > 0)
        {
            sizes.Add(ToolbarFlowLayout.Commands(_commandOrder.Count));
        }

        return sizes;
    }

    /// <summary>
    /// 按当前内容与滚动视口求文档(TOOLBAR-01、TOOLBAR-08)：主轴可用长度取视口而不是无限测量约束，
    /// 只要各组能整组换行容纳就不交给滚动；文档尺寸不小于视口。
    /// </summary>
    /// <returns>分组尺寸、分组矩形与文档尺寸。</returns>
    private (List<ToolbarGroupSize> Sizes, IReadOnlyList<LayoutRect> Groups, Size Size) BuildDocument()
    {
        var horizontal = _isRowLayout;
        var sizes = GroupSizes(horizontal);
        double availableMain = AvailableMain(horizontal);
        var document = ToolbarFlowLayout.Arrange(sizes, horizontal, availableMain, _joined ? 0 : null);
        _lineCount = ToolbarFlowLayout.PackLines(sizes, ToolbarFlowLayout.ContentMain(availableMain)).Count;
        var viewport = ViewportSize();
        return (sizes, document.Groups,
            new Size(Math.Max(viewport.Width, document.Width), Math.Max(viewport.Height, document.Height)));
    }

    /// <summary>
    /// 主轴可用长度：宿主按共享分区给控制栏落了显式尺寸,本轮量测约束就是滚动视口的主轴长度;
    /// 滚动视口的 Viewport 要等下一轮布局才更新,换向当轮仍是旧方向的值,只在宿主长度未知时兜底。
    /// </summary>
    /// <param name="horizontal">主轴是否为水平方向。</param>
    /// <returns>主轴可用长度。</returns>
    private double AvailableMain(bool horizontal)
    {
        if (!double.IsInfinity(_hostAvailableMain) && _hostAvailableMain > 0)
        {
            return _hostAvailableMain;
        }

        var (width, height) = ViewportSize();
        return Math.Max(0, horizontal ? width : height);
    }

    /// <summary>滚动视口尺寸；尚未布局时退回滚动宿主的实际尺寸。</summary>
    /// <returns>视口宽高，未知时为 0。</returns>
    private (double Width, double Height) ViewportSize()
    {
        double width = _scroll.ViewportWidth > 0 ? _scroll.ViewportWidth : _scroll.ActualWidth;
        double height = _scroll.ViewportHeight > 0 ? _scroll.ViewportHeight : _scroll.ActualHeight;
        return (Math.Max(0, width), Math.Max(0, height));
    }

    /// <summary>分组在视图坐标系中的宽高：主轴方向决定宽高与主轴/交叉轴的对应关系。</summary>
    /// <param name="size">共享流式排布给出的分组尺寸。</param>
    /// <param name="horizontal">主轴是否为水平方向。</param>
    /// <returns>分组自身的宽高。</returns>
    private static Size HostSize(ToolbarGroupSize size, bool horizontal)
        => horizontal ? new Size(size.Main, size.Cross) : new Size(size.Cross, size.Main);

    /// <summary>文档完成排布：玻璃位置可能变化，投递一次形状复核(不在布局回调内触发事件)。</summary>
    private void OnDocumentArranged() => ScheduleMaterialShapesChanged();

    /// <summary>
    /// 投递一次材质形状复核：排布与滚动都会移动玻璃，只有真的变化才通知宿主重画背板(SHELL-12)。
    /// </summary>
    private void ScheduleMaterialShapesChanged()
    {
        if (_materialShapesPending)
        {
            return;
        }

        _materialShapesPending = true;
        UiDispatcher.Post(() =>
        {
            _materialShapesPending = false;
            if (_detached)
            {
                return;
            }

            var shapes = GetMaterialShapes(_document);
            if (SameShapes(shapes, _reportedShapes))
            {
                return;
            }

            _reportedShapes = new List<Rect>(shapes.Count);
            foreach (var (bounds, _) in shapes)
            {
                _reportedShapes.Add(bounds);
            }

            MaterialShapesChanged?.Invoke();
        }, UiPriority.Background);
    }

    /// <summary>形状矩形是否与上次上报一致（同一位置的浮点微差不算变化）。</summary>
    /// <param name="current">本次形状。</param>
    /// <param name="previous">上次上报的矩形。</param>
    /// <returns>没有可见变化时返回 true。</returns>
    private static bool SameShapes(IReadOnlyList<(Rect Bounds, double CornerRadius)> current,
        IReadOnlyList<Rect> previous)
    {
        if (current.Count != previous.Count)
        {
            return false;
        }

        for (var index = 0; index < current.Count; index++)
        {
            var (bounds, _) = current[index];
            var last = previous[index];
            if (Math.Abs(bounds.X - last.X) > .5 || Math.Abs(bounds.Y - last.Y) > .5
                || Math.Abs(bounds.Width - last.Width) > .5 || Math.Abs(bounds.Height - last.Height) > .5)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>换向前记住用户滚动位置(TOOLBAR-08)。</summary>
    private void SaveScrollOffset()
    {
        _savedHorizontalOffset = _scroll.HorizontalOffset;
        _savedVerticalOffset = _scroll.VerticalOffset;
    }

    /// <summary>布局稳定后把偏移约束到新几何的合法范围;不主动回到起点。</summary>
    private void RestoreScrollOffset()
    {
        _restoreScrollPending = true;
    }

    /// <summary>新几何就绪后应用保存的偏移;内容尚未测量时等下一轮布局。</summary>
    private void ApplyPendingScroll()
    {
        if (!_restoreScrollPending)
        {
            return;
        }

        double maxHorizontal = _scroll.ScrollableWidth;
        double maxVertical = _scroll.ScrollableHeight;
        // 只恢复新方向的主轴偏移；旧方向的偏移在新几何里没有意义。
        double wantedHorizontal = _isRowLayout ? _savedHorizontalOffset : 0;
        double wantedVertical = _isRowLayout ? 0 : _savedVerticalOffset;
        if (wantedHorizontal > maxHorizontal + 0.5 || wantedVertical > maxVertical + 0.5)
        {
            // 内容仍在逐轮测量,此时夹取会把远端偏移永久压回 0。
            return;
        }

        _restoreScrollPending = false;
        _scroll.ChangeView(
            Math.Clamp(wantedHorizontal, 0, maxHorizontal),
            Math.Clamp(wantedVertical, 0, maxVertical),
            null,
            disableAnimation: true);
    }

    /// <summary>无照片时除“打开文件”外的命令按钮走原生禁用外观(TOOLBAR-04、TOOLBAR-09)。</summary>
    private void UpdateCommandAvailability()
    {
        bool hasFile = _main.CurrentFile != null;
        foreach (var child in _commandHost.Children)
        {
            if (child is Button { Tag: string command } button)
            {
                button.IsEnabled = hasFile || command == "Open";
            }
        }
    }

    /// <summary>
    /// 验收用:取当前已生成命令按钮的 (命令, 悬停提示, 读屏名称) 三元组,
    /// 供 harness 核对 TOOLBAR-04 的文案格式与提示/读屏同源。
    /// </summary>
    /// <returns>按控制栏显示顺序排列的三元组。</returns>
    internal IReadOnlyList<(string Command, string Tooltip, string AutomationName)> DescribeCommandButtons()
    {
        var described = new List<(string, string, string)>();
        foreach (var child in _commandHost.Children)
        {
            if (child is Button { Tag: string command } button)
            {
                described.Add((
                    command,
                    ToolTipService.GetToolTip(button) as string ?? string.Empty,
                    AutomationProperties.GetName(button)));
            }
        }

        return described;
    }

    /// <summary>
    /// 同步 EXIF 组(TOOLBAR-05)：项数不变只换两行文本与悬停提示，项实例与几何不动；
    /// 项数变化才重建项集合。无照片时保留标签并显示占位值，块数与布局不变。
    /// </summary>
    private void RefreshExif()
    {
        var entries = _main.ControlVM.EnabledExifItems;
        if (_exifItems.Count != entries.Count)
        {
            _exifPanel.Children.Clear();
            _exifItems.Clear();
            for (var index = 0; index < entries.Count; index++)
            {
                var item = CreateExifItem();
                _exifItems.Add(item);
                _exifPanel.Children.Add(item.Root);
            }
        }

        for (var index = 0; index < entries.Count; index++)
        {
            var item = _exifItems[index];
            item.Label.Text = entries[index].DisplayName;
            item.Value.Text = entries[index].DisplayValue;
            // TOOLBAR-05：完整值靠悬停提示给出，格式 `标签 · 值`。
            ToolTipService.SetToolTip(item.Root, $"{entries[index].DisplayName} · {entries[index].DisplayValue}");
        }
    }

    /// <summary>建一个 EXIF 信息项：标签在上、值在下的两行居中文本(TOOLBAR-05)。</summary>
    /// <returns>信息项。</returns>
    private ExifItem CreateExifItem()
    {
        var label = new TextBlock
        {
            FontSize = DesignTokens.ToolbarExifLabelFontSize,
            Foreground = WindowsControlStyles.ThemeBrush(ExifLabelBrushKey, WindowsLayout.ExifLabelForeground),
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.NoWrap,
            TextTrimming = TextTrimming.CharacterEllipsis,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        var value = new TextBlock
        {
            FontSize = DesignTokens.ToolbarExifValueFontSize,
            // TOOLBAR-05@win：小字号下半粗与常规难以区分，值取加粗。
            FontWeight = FontWeights.Bold,
            Foreground = WindowsControlStyles.ThemeBrush(ExifValueBrushKey, WindowsLayout.ExifValueForeground),
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.NoWrap,
            TextTrimming = TextTrimming.CharacterEllipsis,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        var stack = new StackPanel
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Center,
            Children = { label, value },
        };
        return new ExifItem(new Grid { Children = { stack } }, label, value);
    }

    /// <summary>
    /// 命令按钮就地同步(TOOLBAR-04)：同名命令保留原按钮实例，只刷新悬停提示与读屏名称；
    /// 新命令追加、消失的命令摘除，排版顺序跟随本次生效顺序。
    /// </summary>
    private void RefreshCommands()
    {
        var desired = new List<string>();
        bool hasFile = _main.CurrentFile != null;
        foreach (var item in _main.ControlVM.EnabledControls)
        {
            var glyph = WindowsIcons.ForCommand(item.Command);
            if (glyph == null)
            {
                continue;
            }

            if (!_commands.TryGetValue(item.Command, out var button))
            {
                button = CreateCommandButton(item.Command, glyph);
                _commands.Add(item.Command, button);
            }

            var tooltip = BuildTooltip(item);
            if (!Equals(ToolTipService.GetToolTip(button), tooltip))
            {
                ToolTipService.SetToolTip(button, tooltip);
            }

            // TOOLBAR-04：读屏名称与悬停提示同源，含命令名与当前主/次绑定。
            if (AutomationProperties.GetName(button) != tooltip)
            {
                AutomationProperties.SetName(button, tooltip);
            }

            button.IsEnabled = hasFile || item.Command == "Open";
            desired.Add(item.Command);
        }

        foreach (var command in _commandOrder)
        {
            if (desired.Contains(command))
            {
                continue;
            }

            if (_commands.Remove(command, out var stale))
            {
                _commandHost.Children.Remove(stale);
            }
        }

        bool same = _commandOrder.Count == desired.Count;
        if (same)
        {
            for (var index = 0; index < desired.Count; index++)
            {
                if (string.Equals(_commandOrder[index], desired[index], StringComparison.Ordinal))
                {
                    continue;
                }

                same = false;
                break;
            }
        }

        if (!same)
        {
            _commandOrder.Clear();
            _commandOrder.AddRange(desired);
            _commandHost.Children.Clear();
            foreach (var command in _commandOrder)
            {
                _commandHost.Children.Add(_commands[command]);
            }
        }
    }

    /// <summary>创建一个命令圆按钮：原生按钮材质与系统状态，尺寸与圆角取自共享几何(TOOLBAR-04)。</summary>
    /// <param name="command">命令标识。</param>
    /// <param name="glyph">命令字形。</param>
    /// <returns>命令按钮。</returns>
    private Button CreateCommandButton(string command, string glyph)
    {
        var content = new Grid
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        if (WindowsIcons.TryGetRatingStars(command, out var stars))
        {
            content.Children.Add(CreateRatingCommandContent(stars));
        }
        else
        {
            content.Children.Add(CreateCommandGlyph(glyph));
        }

        var button = new Button
        {
            Width = ToolbarFlowLayout.CommandSize,
            Height = ToolbarFlowLayout.CommandSize,
            Padding = new Thickness(0),
            // TOOLBAR-10、SHELL-12：原生按钮材质、抬升描边与系统状态外观，壳不覆盖状态色。
            CornerRadius = new CornerRadius(ToolbarFlowLayout.CommandSize / 2),
            Content = content,
            Tag = command,
            IsEnabled = _main.CurrentFile != null || command == "Open",
        };
        button.Click += (_, _) => _execute(command);
        return button;
    }

    /// <summary>
    /// 命令字形(TOOLBAR-04)：字号取令牌，颜色不显式设置，
    /// 常态由原生按钮模板给主文字色、禁用时自动落到系统禁用色(TOOLBAR-10)。
    /// </summary>
    /// <param name="glyph">字形码位。</param>
    /// <returns>字形元素。</returns>
    private static FontIcon CreateCommandGlyph(string glyph) => new()
    {
        Glyph = glyph,
        FontFamily = new FontFamily(WindowsIcons.FluentFontFamily),
        FontSize = DesignTokens.ToolbarCommandGlyphFontSize,
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center,
    };

    /// <summary>
    /// 评分快捷命令的图标(TOOLBAR-04)：实心星按命令字形字号画在按钮中心，
    /// 1–5 的数字用评分数字字号半粗、取按钮底色叠在星心；0 只画清零叉。
    /// 数字不参与命中测试，点击仍落到按钮。
    /// </summary>
    /// <param name="stars">星数(0–5)</param>
    /// <returns>按钮内容。</returns>
    private static Grid CreateRatingCommandContent(int stars)
    {
        var host = new Grid
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        if (stars == 0)
        {
            var clear = CreateCommandGlyph(WindowsIcons.ForRatingGlyph(0));
            clear.FontSize = DesignTokens.ToolbarRatingClearGlyphFontSize;
            host.Children.Add(clear);
            return host;
        }

        host.Children.Add(CreateCommandGlyph(WindowsIcons.ForRatingGlyph(stars)));
        host.Children.Add(new TextBlock
        {
            Text = stars.ToString(),
            FontSize = DesignTokens.ToolbarRatingDigitFontSize,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(WindowsLayout.CommandButtonBackground),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            IsHitTestVisible = false,
        });
        return host;
    }

    /// <summary>建一个评分槽位：槽内状态层在下、字形在上(RATING-07)，槽位按钮全状态透明。</summary>
    /// <param name="value">星级值(0 为清零叉/冲突入口)。</param>
    /// <returns>槽位视图。</returns>
    private RatingSlot CreateRatingSlot(int value)
    {
        var halo = new Ellipse
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            IsHitTestVisible = false,
        };
        var glyph = new TextBlock
        {
            Text = WindowsIcons.ClearGlyph,
            FontFamily = new FontFamily(WindowsIcons.FluentFontFamily),
            FontSize = DesignTokens.ToolbarRatingGlyphFontSize,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            IsHitTestVisible = false,
        };
        var button = new Button
        {
            Padding = new Thickness(0),
            BorderThickness = new Thickness(0),
            Content = new Grid { Children = { halo, glyph } },
        };
        WindowsControlStyles.ApplyRatingSlot(button, halo);
        button.Click += (_, _) => OnRatingSlotClicked(value);
        return new RatingSlot(button, halo, glyph);
    }

    /// <summary>
    /// 拼装 tooltip(TOOLBAR-04):命令名 + 实际主/次绑定,格式 <c>命令名（主 / 次）</c>。
    /// 拼装规则在共享 <see cref="CommandTooltipText"/>,键名仍由 <see cref="WindowsInput.FormatGesture"/> 给;
    /// 未设置的槽不传给 Format,避免出现"未设置"。
    /// </summary>
    private static string BuildTooltip(SettingsViewModel.HotkeyItem item)
        => CommandTooltipText.Format(
            item.Name,
            item.PrimaryHotkey != null ? WindowsInput.FormatGesture(item.PrimaryHotkey) : null,
            item.SecondaryHotkey != null ? WindowsInput.FormatGesture(item.SecondaryHotkey) : null);

    /// <summary>
    /// 按当前照片评分刷新槽位(RATING-07)：点亮星取实心、未点亮取描边、0 号为清零叉，颜色取
    /// <c>color.star.*</c>；组级冲突时 0 号换成警示三角，几何与命中区不变(RATING-04)。
    /// 无照片时槽位禁用，但保留几何与命中区，只把内容降到禁用强调(TOOLBAR-03)。
    /// </summary>
    private void RefreshRating()
    {
        var file = _main.CurrentFile;
        var rating = file?.Rating ?? 0;
        var hasFile = file != null;
        var hasConflict = file?.HasRatingConflict == true;
        for (var value = 0; value < _ratingSlots.Count; value++)
        {
            var slot = _ratingSlots[value];
            var on = value > 0 && rating >= value;
            var glyph = value == 0
                ? hasConflict ? WindowsIcons.WarningGlyph : WindowsIcons.ClearGlyph
                : on ? WindowsIcons.StarGlyph : StarOutlineGlyph;
            var size = value == 0 && !hasConflict
                ? DesignTokens.ToolbarRatingClearGlyphFontSize
                : DesignTokens.ToolbarRatingGlyphFontSize;
            var tint = value == 0
                ? hasConflict ? WindowsLayout.StarConflict : WindowsLayout.StarClear
                : on ? WindowsLayout.StarOn : WindowsLayout.StarOff;
            slot.Glyph.Text = glyph;
            slot.Glyph.FontSize = size;
            slot.Glyph.Foreground = new SolidColorBrush(tint);
            slot.Glyph.Opacity = hasFile ? 1 : WindowsLayout.DisabledContentAlpha;

            var label = value == 0
                ? hasConflict ? "评分来源冲突" : "无星级"
                : $"{value} 星";
            if (!Equals(ToolTipService.GetToolTip(slot.Button), label))
            {
                ToolTipService.SetToolTip(slot.Button, label);
            }

            if (AutomationProperties.GetName(slot.Button) != label)
            {
                AutomationProperties.SetName(slot.Button, label);
            }

            slot.Button.IsEnabled = hasFile;
        }
    }

    /// <summary>RATING-04：冲突三角只打开来源浮窗，不写值；其余槽位整组写回。</summary>
    private void OnRatingSlotClicked(int rating)
    {
        if (rating == 0 && _main.CurrentFile is { HasRatingConflict: true })
        {
            _showSources(_ratingSlots[0].Button);
            return;
        }

        RateCurrent(rating);
    }

    /// <summary>验收专用:0 号评分槽位按钮。</summary>
    internal Button RatingZeroButton => _ratingSlots[0].Button;

    /// <summary>验收专用:0 号评分槽位当前字形。</summary>
    internal string RatingZeroGlyph => _ratingSlots[0].Glyph.Text;

    /// <summary>验收专用:0 号评分槽位当前颜色。</summary>
    internal Color RatingZeroColor => (_ratingSlots[0].Glyph.Foreground as SolidColorBrush)?.Color ?? default;

    /// <summary>验收专用:评分组当前是否可见;评分显隐设置变化后由此核对控制栏已经响应。</summary>
    internal bool RatingGroupVisible => _ratingHost.Visibility == Visibility.Visible;

    /// <summary>验收专用:当前命令按钮实例(按显示顺序),用于核对诊断通知没有重建按钮、真实绑定变化会重建。</summary>
    internal IReadOnlyList<Button> CommandButtons
    {
        get
        {
            var buttons = new List<Button>();
            foreach (var child in _commandHost.Children)
            {
                if (child is Button button)
                {
                    buttons.Add(button);
                }
            }

            return buttons;
        }
    }

    /// <summary>按评分设置当前照片。</summary>
    private async void RateCurrent(int rating)
    {
        try
        {
            await _main.SetRatingAsync(_main.CurrentFile, rating);
        }
        catch (Exception ex)
        {
            NativeStartupLog.Fail($"设置评分失败(rating={rating})", ex);
        }
    }

    /// <summary>主视图模型变化:切换照片时只刷新文件相关内容(命令按钮只依赖设置)。</summary>
    private void OnMainChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.CurrentFile))
        {
            AttachCurrentFile(_main.CurrentFile);
            ScheduleFileRefresh();
        }
    }

    /// <summary>
    /// 订阅当前文件:评分来源在后台读完后只改文件属性,控制栏要跟着刷新冲突三角,
    /// 不能只等 CurrentFile 变化。
    /// </summary>
    private void AttachCurrentFile(ImageFile? file)
    {
        if (ReferenceEquals(_currentFile, file))
        {
            return;
        }

        if (_currentFile != null)
        {
            _currentFile.PropertyChanged -= OnCurrentFileChanged;
        }

        _currentFile = file;
        if (_currentFile != null)
        {
            _currentFile.PropertyChanged += OnCurrentFileChanged;
        }
    }

    /// <summary>当前文件的评分/冲突来源变化:刷新评分组,不重建命令按钮。</summary>
    private void OnCurrentFileChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!ReferenceEquals(sender, _currentFile))
        {
            return;
        }

        if (e.PropertyName is nameof(ImageFile.Rating)
            or nameof(ImageFile.HasRatingConflict)
            or nameof(ImageFile.RatingSources))
        {
            ScheduleRatingRefresh();
        }
    }

    /// <summary>控制栏业务摘要变化:当前照片的 EXIF 条目与命令集合来自共享控制 VM,只重建对应分组。</summary>
    private void OnControlChanged(object? sender, PropertyChangedEventArgs e)
    {
        // EXIF 信息项随当前文件的 EXIF 与显示设置变化,归入文件相关刷新。
        if (e.PropertyName == nameof(ControlViewModel.EnabledExifItems))
        {
            ScheduleFileRefresh();
        }
        else if (e.PropertyName == nameof(ControlViewModel.EnabledControls))
        {
            // 命令按钮只由显隐、快捷键绑定与排序决定。
            ScheduleCommandsRefresh();
        }
        else if (e.PropertyName == nameof(ControlViewModel.StarOpacity))
        {
            ScheduleRatingRefresh();
        }
    }

    /// <summary>
    /// 设置展示影响:按共享 <see cref="SettingsChangeImpact"/> 位刷新对应分组,
    /// 设置页只改一个分组时不必重建整条控制栏。未登记属性(诊断、缓存预算文本)不进入这里。
    /// </summary>
    /// <param name="impact">本次设置变化影响的主屏区域。</param>
    private void OnSettingsPresentationChanged(SettingsChangeImpact impact)
    {
        if (impact == SettingsChangeImpact.None)
        {
            return;
        }

        if ((impact & SettingsChangeImpact.Exif) != 0)
        {
            ScheduleFileRefresh();
        }

        if ((impact & (SettingsChangeImpact.Commands | SettingsChangeImpact.InputBindings)) != 0)
        {
            ScheduleCommandsRefresh();
        }

        if ((impact & SettingsChangeImpact.Rating) != 0)
        {
            ScheduleRatingRefresh();
        }
    }

    /// <summary>安排文件相关刷新:EXIF、评分与命令可用态。</summary>
    private void ScheduleFileRefresh()
    {
        _fileRefreshPending = true;
        ScheduleRefresh();
    }

    /// <summary>安排评分刷新:显示开关或当前文件评分变化。</summary>
    private void ScheduleRatingRefresh()
    {
        _ratingRefreshPending = true;
        ScheduleRefresh();
    }

    /// <summary>安排命令按钮同步:显隐、快捷键绑定或排序变化。</summary>
    private void ScheduleCommandsRefresh()
    {
        _commandsRefreshPending = true;
        ScheduleRefresh();
    }

    /// <summary>合并刷新:一轮内多次变化只投递一次回调,并按待办范围只执行对应部分。</summary>
    private void ScheduleRefresh()
    {
        if (_refreshPending)
        {
            return;
        }

        _refreshPending = true;
        UiDispatcher.Post(() =>
        {
            _refreshPending = false;
            if (_detached)
            {
                return;
            }

            var file = _fileRefreshPending;
            var rating = _ratingRefreshPending;
            var commands = _commandsRefreshPending;
            _fileRefreshPending = false;
            _ratingRefreshPending = false;
            _commandsRefreshPending = false;

            if (commands)
            {
                RefreshCommands();
            }

            if (file)
            {
                RefreshFileParts();
                return;
            }

            if (rating)
            {
                RefreshRating();
            }

            ApplyStructure();
        }, UiPriority.Background);
    }

    /// <summary>按共享几何给出的矩形摆放子元素（EXIF 项、评分槽位、命令按钮）。</summary>
    private sealed class PlacedPanel : Panel
    {
        private IReadOnlyList<LayoutRect> _rects = Array.Empty<LayoutRect>();

        /// <summary>设置每个子元素的目标矩形，顺序与子元素顺序一致。</summary>
        /// <param name="rects">目标矩形。</param>
        internal void SetRects(IReadOnlyList<LayoutRect> rects)
        {
            _rects = rects;
            InvalidateMeasure();
        }

        /// <inheritdoc/>
        protected override Size MeasureOverride(Size availableSize)
        {
            double width = 0;
            double height = 0;
            for (var index = 0; index < Children.Count; index++)
            {
                var rect = index < _rects.Count ? _rects[index] : default;
                Children[index].Measure(new Size(Math.Max(0, rect.Width), Math.Max(0, rect.Height)));
                width = Math.Max(width, rect.MaxX);
                height = Math.Max(height, rect.MaxY);
            }

            // 容器尺寸由上层按共享几何给定；这里只保证不小于内容占用。
            return new Size(Math.Min(width, availableSize.Width), Math.Min(height, availableSize.Height));
        }

        /// <inheritdoc/>
        protected override Size ArrangeOverride(Size finalSize)
        {
            for (var index = 0; index < Children.Count; index++)
            {
                var rect = index < _rects.Count ? _rects[index] : default;
                Children[index].Arrange(new Rect(rect.X, rect.Y, rect.Width, rect.Height));
            }

            return finalSize;
        }
    }

    /// <summary>
    /// 控制栏文档（TOOLBAR-01、TOOLBAR-08）：主轴可用长度取滚动视口而不是无限测量约束，
    /// 只要各组能整组换行容纳就不交给滚动；文档尺寸不小于视口，分组矩形取共享 <see cref="ToolbarFlowLayout"/>。
    /// </summary>
    private sealed class ControlDocumentPanel : Panel
    {
        private readonly WindowsControlBar _owner;
        private IReadOnlyList<ToolbarGroupSize> _sizes = Array.Empty<ToolbarGroupSize>();
        private IReadOnlyList<LayoutRect> _groups = Array.Empty<LayoutRect>();

        internal ControlDocumentPanel(WindowsControlBar owner) => _owner = owner;

        /// <inheritdoc/>
        protected override Size MeasureOverride(Size availableSize)
        {
            var (sizes, groups, size) = _owner.BuildDocument();
            _sizes = sizes;
            _groups = groups;
            for (var index = 0; index < Children.Count && index < sizes.Count; index++)
            {
                Children[index].Measure(HostSize(sizes[index], _owner._isRowLayout));
            }

            return size;
        }

        /// <inheritdoc/>
        protected override Size ArrangeOverride(Size finalSize)
        {
            bool horizontal = _owner._isRowLayout;
            for (var index = 0; index < Children.Count && index < _groups.Count && index < _sizes.Count; index++)
            {
                var frame = _groups[index];
                var size = _sizes[index];
                // TOOLBAR-02：分组矩形交叉轴已按行高拉伸，组自身保持内容尺寸并在其中居中。
                double crossExtent = horizontal ? frame.Height : frame.Width;
                double offset = Math.Max(0, (crossExtent - size.Cross) / 2);
                Children[index].Arrange(horizontal
                    ? new Rect(frame.X, frame.Y + offset, size.Main, size.Cross)
                    : new Rect(frame.X + offset, frame.Y, size.Cross, size.Main));
            }

            _owner.OnDocumentArranged();
            return finalSize;
        }
    }

    /// <summary>EXIF 信息项：标签在上、值在下的两行居中文本(TOOLBAR-05)。</summary>
    private sealed record ExifItem(Grid Root, TextBlock Label, TextBlock Value);

    /// <summary>评分槽位：透明按钮 + 槽内内切正圆状态层 + 字形(RATING-07)。</summary>
    private sealed record RatingSlot(Button Button, Ellipse Halo, TextBlock Glyph);
}
