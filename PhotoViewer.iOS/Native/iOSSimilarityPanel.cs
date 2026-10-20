using System.ComponentModel;
using CoreGraphics;
using Foundation;
using PhotoViewer.Apple;
using PhotoViewer.Core.AI;
using PhotoViewer.Core.Image;
using PhotoViewer.Core.Platform;
using PhotoViewer.ViewModels.Main;
using PhotoViewer.ViewModels.Main.File;
using UIKit;

namespace PhotoViewer.iOS.Native;

/// <summary>
/// 原生相似聚类面板（UIKit 版）：几何与视觉与旧壳 <c>SimilarityListView</c>/Mac 原生面板同口径 ——
/// 外框 <c>#1E1E1E</c> 底、<c>#404040</c> 1 点描边 + 圆角 6、最小 106×154（右侧 4 点外边距由调用方摆放时留出）。
/// 无相似项时占位文字与动作区整体居中（间距 8、四周 4 点）；有相似项时动作区按布局方向停靠
/// （行布局停靠在右、列布局停靠在底），其余区域为单轴列表。
/// 列表复用 <see cref="iOSFilmstripLayout"/> 与 <see cref="iOSThumbnailCard"/> 卡片，卡片第二行换成
/// 相似度百分比与 75%~100% 映射的柱状条（与旧壳两个相似度转换器同口径）；
/// 点击卡片走 <see cref="SimilarityPanelViewModel.SelectItemCommand"/>，不直接切当前图，保留聚类锚点。
/// </summary>
internal sealed class iOSSimilarityPanel : UIView
{
    /// <summary>外框 1 点描边。</summary>
    private const double BorderWidth = 1;
    /// <summary>外框圆角。</summary>
    private const double CornerRadius = 6;
    /// <summary>空态 StackPanel 间距。</summary>
    private const double EmptySpacing = 8;
    /// <summary>空态四周外边距。</summary>
    private const double EmptyMargin = 4;
    /// <summary>动作按钮/进度条尺寸，与卡片 90 点宽对齐，避免撑宽面板。</summary>
    private const double ActionWidth = 90;
    private const double ActionHeight = 28;
    /// <summary>列表态动作区两行内容的间距。</summary>
    private const double DockSpacing = 6;
    /// <summary>柱状条下限分数，与 <c>SimilarityScoreToProgressFractionConverter</c> 的 LowerBound 一致。</summary>
    private const double ScoreLowerBound = .75;
    /// <summary>换行文字测量的高度上限。</summary>
    private const double MeasureLimit = 10000;
    /// <summary>错误提示的最大宽度（旧壳 MaxWidth 120）。</summary>
    private const double ErrorMaxWidth = 120;
    /// <summary>行布局停靠区左右外边距。</summary>
    private const double DockRowMargin = 3;
    /// <summary>行布局停靠区下外边距（旧壳 Margin='3,3,3,8'）。</summary>
    private const double DockRowBottom = 8;
    /// <summary>列布局停靠区左右外边距（旧壳 Margin='8,3,8,3'）。</summary>
    private const double DockColumnMargin = 8;
    /// <summary>列布局停靠区上下外边距。</summary>
    private const double DockColumnMarginVertical = 3;

    private static readonly UIColor PanelColor = UIColor.FromRGB(0x1E, 0x1E, 0x1E);
    private static readonly UIColor BorderColor = UIColor.FromRGB(0x40, 0x40, 0x40);
    private static readonly UIColor PlaceholderColor = UIColor.FromRGB(0x55, 0x55, 0x55);
    private static readonly UIColor IndexErrorColor = UIColor.FromRGB(0xCC, 0x44, 0x44);
    private static readonly UIColor UnindexedColor = UIColor.FromRGB(0x77, 0x77, 0x77);

    private readonly MainViewModel _main;
    private readonly SimilarityPanelViewModel _panel;
    private readonly UIView _emptyStack = new();
    private readonly UIView _listHost = new();
    private readonly UIView _listDock = new();
    private readonly UILabel _notExtracted;
    private readonly UILabel _noSimilar;
    private readonly UILabel _indexError;
    private readonly UILabel _dockError;
    private readonly UILabel _unindexed;
    private readonly ActionArea _emptyAction;
    private readonly ActionArea _dockAction;
    private readonly iOSFilmstripLayout _layout = new();
    private readonly UICollectionView _collection;
    private readonly Source _source;
    private IReadOnlyList<SimilarityItem> _items = Array.Empty<SimilarityItem>();
    private bool _rowLayout = true;
    private bool _showRating;
    private double _availableCross;
    private double _crossSize = SimilarityPanelGeometry.MinHeight;
    private bool _disposed;

    /// <summary>相似项原生列表，供验收读取真实卡片与几何。</summary>
    internal UICollectionView List => _collection;

    /// <summary>交叉轴需要的长度：行布局为内容高度（最小 154），列布局为面板宽度。</summary>
    internal double CrossSize => _crossSize;

    /// <summary>当前列表项（与共享 VM 同源）。</summary>
    internal IReadOnlyList<SimilarityItem> Items => _items;

    /// <summary>列表态是否可见（与空态互斥）。</summary>
    internal bool IsListVisible => !_listHost.Hidden;

    /// <summary>空态容器（验收按可见文本核对三态）。</summary>
    internal UIView EmptyStack => _emptyStack;

    /// <summary>列表区容器（列表 + 停靠动作区）。</summary>
    internal UIView ListHost => _listHost;

    /// <summary>空态"提取全部"按钮（验收触发真实点击）。</summary>
    internal UIButton EmptyActionButton => _emptyAction.Button;

    /// <summary>列表态"补齐全部"按钮（验收触发真实点击）。</summary>
    internal UIButton DockActionButton => _dockAction.Button;

    /// <summary>列表态动作区的错误标签（SIM-10：与当前状态同时可见）。</summary>
    internal UILabel DockErrorLabel => _dockError;

    /// <summary>建立原生视图树并订阅相似面板 VM 的属性变化；构造函数除订阅外不触发推理、解码或读写文件。</summary>
    /// <param name="main">共享主 VM，面板状态取自 <c>main.FileVM.SimilarityPanel</c>。</param>
    internal iOSSimilarityPanel(MainViewModel main) : base(CGRect.Empty)
    {
        _main = main;
        _panel = main.FileVM.SimilarityPanel;
        BackgroundColor = PanelColor;
        Layer.BorderColor = BorderColor.CGColor;
        Layer.BorderWidth = (nfloat)BorderWidth;
        Layer.CornerRadius = (nfloat)CornerRadius;
        ClipsToBounds = true;

        _notExtracted = WrappedLabel("尚未提取特征", 11, PlaceholderColor);
        _noSimilar = WrappedLabel("无相似照片", 11, PlaceholderColor);
        _indexError = WrappedLabel("", 10, IndexErrorColor);
        _dockError = WrappedLabel("", 10, IndexErrorColor);
        _unindexed = WrappedLabel("", 10, UnindexedColor);
        _emptyAction = new ActionArea(StartIndexing);
        _dockAction = new ActionArea(StartIndexing);

        _emptyStack.AddSubview(_notExtracted);
        _emptyStack.AddSubview(_noSimilar);
        _emptyStack.AddSubview(_emptyAction);
        _emptyStack.AddSubview(_indexError);
        AddSubview(_emptyStack);

        _listDock.AddSubview(_dockError);
        _listDock.AddSubview(_unindexed);
        _listDock.AddSubview(_dockAction);
        _layout.Horizontal = true;
        _layout.CountProvider = () => _items.Count;
        _collection = new UICollectionView(CGRect.Empty, _layout)
        {
            BackgroundColor = PanelColor,
            AllowsSelection = false,
            ShowsHorizontalScrollIndicator = true,
            ShowsVerticalScrollIndicator = false,
            ContentInsetAdjustmentBehavior = UIScrollViewContentInsetAdjustmentBehavior.Never,
        };
        _collection.RegisterClassForCell(typeof(iOSThumbnailCard), Source.CellId);
        _source = new Source(this);
        _collection.DataSource = _source;
        _listHost.AddSubview(_collection);
        _listHost.AddSubview(_listDock);
        AddSubview(_listHost);

        _panel.PropertyChanged += OnPanelChanged;
        Refresh();
    }

    /// <summary>由主屏每次布局时调用：记录布局方向与交叉轴可用长度，并把内容决定的交叉轴长度同步到自身 Frame（主轴仍由调用方决定）。</summary>
    /// <param name="rowLayout">是否行布局（面板在主图下方/胶片栏右侧，列表横排）。</param>
    /// <param name="availableCross">交叉轴可用长度；小于等于 0 表示不限。</param>
    internal void SetLayout(bool rowLayout, double availableCross)
    {
        _availableCross = availableCross;
        if (_rowLayout != rowLayout)
        {
            _rowLayout = rowLayout;
            _layout.Horizontal = rowLayout;
            _collection.ShowsHorizontalScrollIndicator = rowLayout;
            _collection.ShowsVerticalScrollIndicator = !rowLayout;
            _layout.InvalidateLayout();
        }
        ApplyCross();
        Arrange();
    }

    /// <summary>从 VM 重新拉取状态并就地刷新：文字直接更新，列表仅在项目集引用变化时重载。</summary>
    internal void Refresh()
    {
        if (_disposed) return;
        bool hasItems = _panel.HasItems;
        _emptyStack.Hidden = hasItems;
        _listHost.Hidden = !hasItems;
        _emptyAction.Hidden = !_panel.ShowActionArea;
        _listDock.Hidden = !_panel.ShowActionArea;
        _unindexed.Hidden = !_panel.HasUnindexed;
        if (!_unindexed.Hidden) _unindexed.Text = _panel.UnindexedCount + " 张未提取";
        _notExtracted.Hidden = !_panel.ShowUnindexedPlaceholder;
        _noSimilar.Hidden = !_panel.ShowFullPlaceholder;
        _indexError.Hidden = !_panel.HasIndexError;
        _indexError.Text = _panel.IndexError ?? "";
        // SIM-10：列表态的错误与当前状态同时可见，显示在停靠动作区。
        _dockError.Hidden = !_panel.ShowActionAreaError;
        _dockError.Text = _panel.IndexError ?? "";
        UpdateAction(_emptyAction);
        UpdateAction(_dockAction);

        var items = _panel.SimilarItems;
        bool showRating = _main.Settings.ShowRating;
        if (!ReferenceEquals(items, _items) || showRating != _showRating)
        {
            _items = items;
            _showRating = showRating;
            _collection.ReloadData();
            _layout.InvalidateLayout();
        }

        ApplyCross();
        Arrange();
    }

    /// <summary>尺寸变化时立即重排内容，不重建视图。</summary>
    public override void LayoutSubviews()
    {
        base.LayoutSubviews();
        Arrange();
    }

    /// <summary>只更新交叉轴长度并写入自身 Frame，主轴长度与位置交给调用方。</summary>
    private void ApplyCross()
    {
        double needed = NaturalCross();
        _crossSize = _availableCross > 0 ? Math.Min(needed, _availableCross) : needed;
        var frame = Frame;
        if (_rowLayout) frame.Height = (nfloat)_crossSize;
        else frame.Width = (nfloat)_crossSize;
        if (frame != Frame) Frame = frame;
    }

    /// <summary>内容决定的交叉轴长度：行布局为占位/动作区或列表的自然高度，列布局为卡片与内边距决定的宽度。</summary>
    /// <returns>交叉轴长度。</returns>
    private double NaturalCross()
    {
        if (!_rowLayout) return Math.Max(SimilarityPanelGeometry.MinWidth, SimilarityPanelGeometry.PanelWidth);
        return Math.Max(SimilarityPanelGeometry.MinHeight, _panel.HasItems ? SimilarityPanelGeometry.ListCross : EmptyStackHeight());
    }

    /// <summary>空态居中内容的自然高度：占位文字 + 动作区 + 错误提示，间距 8、四周 4 点外边距。</summary>
    /// <returns>自然高度。</returns>
    private double EmptyStackHeight()
    {
        double limit = Math.Max(1, SimilarityPanelGeometry.PanelWidth - BorderWidth * 2 - EmptyMargin * 2);
        double total = 0;
        int rows = 0;
        if (!_notExtracted.Hidden) { total += MeasureHeight(_notExtracted, limit); rows++; }
        if (!_noSimilar.Hidden) { total += MeasureHeight(_noSimilar, limit); rows++; }
        if (!_emptyAction.Hidden) { total += ActionHeight; rows++; }
        if (!_indexError.Hidden) { total += MeasureHeight(_indexError, Math.Min(ErrorMaxWidth, limit)); rows++; }
        return total + EmptySpacing * Math.Max(0, rows - 1) + EmptyMargin * 2;
    }

    /// <summary>按当前尺寸摆放"空态居中内容"或"列表 + 停靠动作区"。</summary>
    private void Arrange()
    {
        if (_disposed) return;
        double width = Bounds.Width, height = Bounds.Height;
        if (width <= 0 || height <= 0) return;
        if (_panel.HasItems)
        {
            _listHost.Frame = Bounds;
            ArrangeList(width, height);
        }
        else
        {
            _emptyStack.Frame = Bounds;
            ArrangeEmpty(width, height);
        }
    }

    /// <summary>空态：占位文字、动作区与错误提示整体垂直居中。</summary>
    /// <param name="width">面板宽。</param>
    /// <param name="height">面板高。</param>
    private void ArrangeEmpty(double width, double height)
    {
        double limit = Math.Max(1, width - BorderWidth * 2 - EmptyMargin * 2);
        double errorWidth = Math.Min(ErrorMaxWidth, limit);
        var rows = new List<(UIView View, double Height, double RowWidth)>();
        if (!_notExtracted.Hidden) rows.Add((_notExtracted, MeasureHeight(_notExtracted, limit), limit));
        if (!_noSimilar.Hidden) rows.Add((_noSimilar, MeasureHeight(_noSimilar, limit), limit));
        if (!_emptyAction.Hidden) rows.Add((_emptyAction, ActionHeight, ActionWidth));
        if (!_indexError.Hidden) rows.Add((_indexError, MeasureHeight(_indexError, errorWidth), errorWidth));

        double total = rows.Sum(row => row.Height) + EmptySpacing * Math.Max(0, rows.Count - 1);
        double y = Math.Max(BorderWidth + EmptyMargin, (height - total) / 2);
        foreach (var row in rows)
        {
            row.View.Frame = new CGRect(BorderWidth + (width - BorderWidth * 2 - row.RowWidth) / 2, y, row.RowWidth, row.Height);
            y += row.Height + EmptySpacing;
        }
    }

    /// <summary>列表态：动作区按方向停靠，列表占据剩余内容区。</summary>
    /// <param name="width">面板宽。</param>
    /// <param name="height">面板高。</param>
    private void ArrangeList(double width, double height)
    {
        double innerWidth = Math.Max(1, width - BorderWidth * 2);
        double innerHeight = Math.Max(1, height - BorderWidth * 2);
        var list = new CGRect(BorderWidth, BorderWidth, innerWidth, innerHeight);
        if (!_listDock.Hidden)
        {
            if (_rowLayout)
            {
                double dockWidth = DockWidth(innerWidth);
                _listDock.Frame = new CGRect(BorderWidth + innerWidth - dockWidth, BorderWidth, dockWidth, innerHeight);
                ArrangeDockContent(_listDock.Bounds, DockRowMargin, DockRowMargin, DockRowMargin, DockRowBottom, true);
                list.Width = (nfloat)Math.Max(1, innerWidth - dockWidth);
            }
            else
            {
                double dockHeight = DockHeight(innerWidth);
                _listDock.Frame = new CGRect(BorderWidth, BorderWidth + innerHeight - dockHeight, innerWidth, dockHeight);
                ArrangeDockContent(_listDock.Bounds, DockColumnMargin, DockColumnMarginVertical, DockColumnMargin, DockColumnMarginVertical, false);
                list.Height = (nfloat)Math.Max(1, innerHeight - dockHeight);
            }
        }
        UpdateListLayout(list);
    }

    /// <summary>
    /// 摆放停靠区内的"错误文案 + 未提取张数 + 动作"内容；行布局把整块内容贴底（旧壳 VerticalAlignment=Bottom）。
    /// </summary>
    /// <param name="region">停靠区。</param>
    /// <param name="left">左外边距。</param>
    /// <param name="top">上外边距。</param>
    /// <param name="right">右外边距。</param>
    /// <param name="bottom">下外边距。</param>
    /// <param name="bottomAligned">内容是否贴底。</param>
    private void ArrangeDockContent(CGRect region, double left, double top, double right, double bottom, bool bottomAligned)
    {
        double width = Math.Max(1, region.Width - left - right);
        double errorWidth = Math.Min(ErrorMaxWidth, width);
        double errorHeight = _dockError.Hidden ? 0 : MeasureHeight(_dockError, errorWidth);
        double labelHeight = _unindexed.Hidden ? 0 : MeasureHeight(_unindexed, width);
        double contentHeight = (errorHeight > 0 ? errorHeight + DockSpacing : 0)
            + (labelHeight > 0 ? labelHeight + DockSpacing : 0) + ActionHeight;
        double y = bottomAligned ? Math.Max(top, region.Height - bottom - contentHeight) : top;
        if (errorHeight > 0)
        {
            _dockError.Frame = new CGRect(region.X + (region.Width - errorWidth) / 2, y, errorWidth, errorHeight);
            y += errorHeight + DockSpacing;
        }
        if (labelHeight > 0)
        {
            _unindexed.Frame = new CGRect(region.X + left, y, width, labelHeight);
            y += labelHeight + DockSpacing;
        }
        _dockAction.Frame = new CGRect(region.X + (region.Width - ActionWidth) / 2, y, ActionWidth, ActionHeight);
    }

    /// <summary>行布局停靠区宽度：按钮、未提取文字与错误文案较宽者加左右外边距。</summary>
    /// <param name="innerWidth">列表内容宽。</param>
    /// <returns>停靠区宽度。</returns>
    private double DockWidth(double innerWidth)
    {
        double errorWidth = _dockError.Hidden ? 0 : Math.Min(ErrorMaxWidth, MeasureWidth(_dockError, innerWidth));
        return Math.Min(innerWidth,
            Math.Max(ActionWidth, Math.Max(MeasureWidth(_unindexed, innerWidth), errorWidth)) + DockRowMargin * 2);
    }

    /// <summary>
    /// 行布局列表态停靠区的自然宽度（SIM-07）：max(sim.action.width, 未提取计数/错误文本的自然宽) + 2 × sim.dock.margin。
    /// 主屏用它给共享 <c>RowWidth</c> 预留停靠区，否则 1–2 项列表会被动作区挤成零宽。
    /// </summary>
    internal double DockMeasureWidth()
    {
        double text = _unindexed.Hidden ? 0 : MeasureWidth(_unindexed, MeasureLimit);
        double error = _dockError.Hidden ? 0 : Math.Min(ErrorMaxWidth, MeasureWidth(_dockError, MeasureLimit));
        return Math.Max(ActionWidth, Math.Max(text, error)) + DockRowMargin * 2;
    }

    /// <summary>列布局停靠区高度：错误文案、未提取张数与动作区加左右外边距 8、上下外边距 3。</summary>
    /// <param name="innerWidth">列表内容宽。</param>
    /// <returns>停靠区高度。</returns>
    private double DockHeight(double innerWidth)
    {
        double width = Math.Max(1, innerWidth - DockColumnMargin * 2);
        double errorHeight = _dockError.Hidden ? 0 : MeasureHeight(_dockError, Math.Min(ErrorMaxWidth, width));
        double labelHeight = _unindexed.Hidden ? 0 : MeasureHeight(_unindexed, width);
        return (errorHeight > 0 ? errorHeight + DockSpacing : 0)
            + (labelHeight > 0 ? labelHeight + DockSpacing : 0) + ActionHeight + DockColumnMarginVertical * 2;
    }

    /// <summary>把列表容器贴合到指定区域，并按当前轴刷新收集区内容尺寸与滚动范围。</summary>
    /// <param name="region">列表区域。</param>
    private void UpdateListLayout(CGRect region)
    {
        var position = _collection.ContentOffset;
        if (_collection.Frame != region) _collection.Frame = region;
        _layout.InvalidateLayout();
        _collection.LayoutIfNeeded();
        var document = _layout.CollectionViewContentSize;
        var viewport = _collection.Bounds.Size;
        var clamped = new CGPoint(
            Math.Clamp((double)position.X, 0, Math.Max(0, (double)document.Width - (double)viewport.Width)),
            Math.Clamp((double)position.Y, 0, Math.Max(0, (double)document.Height - (double)viewport.Height)));
        if (clamped != position) _collection.SetContentOffset(clamped, false);
    }

    /// <summary>按 VM 状态切换按钮与进度条（两者互斥），并更新按钮文案与进度值。</summary>
    /// <param name="area">目标动作区。</param>
    private void UpdateAction(ActionArea area)
    {
        area.ShowButton = _panel.ShowActionButton;
        area.ShowProgress = _panel.IsStateIndexing;
        if (_panel.ShowActionButton) area.Title = _panel.ActionButtonText;
        if (_panel.IsStateIndexing) area.SetProgress(_panel.IndexProgress, _panel.IndexTotal, _panel.IsIndexTotalUnknown);
    }

    /// <summary>VM 属性变化统一回到 UI 线程刷新，视图只在 UI 线程被触碰。</summary>
    /// <param name="sender">事件源（相似面板 VM）。</param>
    /// <param name="args">属性变化参数。</param>
    private void OnPanelChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (_disposed) return;
        if (UiDispatcher.CheckAccess()) Refresh();
        else UiDispatcher.Post(Refresh);
    }

    /// <summary>相似面板的"提取全部/补齐全部"入口。</summary>
    private void StartIndexing()
    {
        if (_disposed) return;
        _panel.StartIndexingCommand();
    }

    /// <summary>相似度分数（0~1）格式化为 "NN%" 文本，与 <c>SimilarityScoreConverter</c> 同口径。</summary>
    /// <param name="score">相似度分数。</param>
    /// <returns>百分比文本。</returns>
    private static string ScoreText(double score) => (int)Math.Round(Math.Clamp(score, 0.0, 1.0) * 100) + "%";

    /// <summary>相似度分数映射到柱状条占比（75% 以下为 0），与 <c>SimilarityScoreToProgressFractionConverter</c> 同口径。</summary>
    /// <param name="score">相似度分数。</param>
    /// <returns>柱状条占满宽度时的占比。</returns>
    private static double ScoreFraction(double score)
        => (Math.Clamp(score, ScoreLowerBound, 1.0) - ScoreLowerBound) / (1.0 - ScoreLowerBound);

    /// <summary>生成可换行的只读居中标签。</summary>
    /// <param name="text">初始文本。</param>
    /// <param name="size">字号。</param>
    /// <param name="color">文字颜色。</param>
    /// <returns>标签。</returns>
    private static UILabel WrappedLabel(string text, double size, UIColor color) => new()
    {
        Text = text,
        Font = UIFont.SystemFontOfSize((nfloat)size),
        TextColor = color,
        TextAlignment = UITextAlignment.Center,
        Lines = 0,
        LineBreakMode = UILineBreakMode.WordWrap,
        BackgroundColor = UIColor.Clear,
    };

    /// <summary>量出标签在给定宽度下换行后的高度。</summary>
    /// <param name="label">目标标签。</param>
    /// <param name="width">可用宽度。</param>
    /// <returns>高度。</returns>
    private static double MeasureHeight(UILabel label, double width)
    {
        if (width <= 0) return 0;
        return (double)label.SizeThatFits(new CGSize(width, MeasureLimit)).Height;
    }

    /// <summary>量出标签在给定上限内需要的宽度（单行标签按实际文字宽度）。</summary>
    /// <param name="label">目标标签。</param>
    /// <param name="limit">宽度上限。</param>
    /// <returns>宽度。</returns>
    private static double MeasureWidth(UILabel label, double limit)
    {
        if (limit <= 0) return 0;
        return Math.Ceiling((double)label.SizeThatFits(new CGSize(limit, MeasureLimit)).Width);
    }

    /// <summary>注销 VM 订阅并解除卡片数据源，避免视图销毁后继续回调。</summary>
    /// <param name="disposing">是否释放托管资源。</param>
    protected override void Dispose(bool disposing)
    {
        if (disposing && !_disposed)
        {
            _disposed = true;
            _panel.PropertyChanged -= OnPanelChanged;
            _collection.DataSource = null!;
        }
        base.Dispose(disposing);
    }

    /// <summary>90×28 动作区："提取全部/补齐全部"按钮与提取进度条叠在同一位置，互斥显示。</summary>
    private sealed class ActionArea : UIView
    {
        private readonly UIButton _button;
        private readonly UIView _progressHost;
        private readonly UIProgressView _progress;
        private readonly UILabel _progressText;

        /// <summary>按钮文案（旧壳 ActionButtonText）。</summary>
        internal string Title
        {
            set => _button.SetTitle(value, UIControlState.Normal);
        }

        /// <summary>是否显示按钮。</summary>
        internal bool ShowButton
        {
            set => _button.Hidden = !value;
        }

        /// <summary>是否显示进度条。</summary>
        internal bool ShowProgress
        {
            set => _progressHost.Hidden = !value;
        }

        /// <summary>构造动作区；按钮点击直接触发相似面板的批量提取入口。</summary>
        /// <param name="start">"提取全部/补齐全部"按钮动作。</param>
        internal ActionArea(Action start) : base(new CGRect(0, 0, ActionWidth, ActionHeight))
        {
            _button = new UIButton(UIButtonType.Custom) { Frame = Bounds };
            _button.BackgroundColor = UIColor.FromRGB(0x2A, 0x2A, 0x2A);
            _button.Layer.BorderColor = UIColor.FromRGB(0x55, 0x55, 0x55).CGColor;
            _button.Layer.BorderWidth = 1;
            _button.Layer.CornerRadius = (nfloat)CornerRadius;
            _button.SetTitleColor(UIColor.FromRGB(0xCC, 0xCC, 0xCC), UIControlState.Normal);
            _button.TitleLabel!.Font = UIFont.SystemFontOfSize(11);
            _button.TouchUpInside += (_, _) => start();

            _progressHost = new UIView(Bounds) { BackgroundColor = UIColor.FromRGB(0x2A, 0x2A, 0x2A) };
            _progressHost.Layer.CornerRadius = (nfloat)CornerRadius;
            _progressHost.ClipsToBounds = true;
            _progress = new UIProgressView(new CGRect(3, 10, ActionWidth - 6, 8));
            _progressText = new UILabel(new CGRect(0, (ActionHeight - 14) / 2, ActionWidth, 14))
            {
                Font = UIFont.SystemFontOfSize(10),
                TextColor = UIColor.FromRGB(0xCC, 0xCC, 0xCC),
                TextAlignment = UITextAlignment.Center,
                BackgroundColor = UIColor.Clear,
            };
            _progressHost.AddSubview(_progress);
            _progressHost.AddSubview(_progressText);

            AddSubview(_button);
            AddSubview(_progressHost);
            _button.Hidden = true;
            _progressHost.Hidden = true;
        }

        /// <summary>更新进度值与覆盖文字；总数为 0 时进度条走不定态（UIKit 无不定态，按 0 显示并只保留文字）。</summary>
        /// <param name="done">已完成张数。</param>
        /// <param name="total">总张数。</param>
        /// <param name="unknown">总数未知。</param>
        internal void SetProgress(int done, int total, bool unknown)
        {
            _progress.Progress = unknown ? 0 : Math.Clamp(done / (float)Math.Max(1, total), 0f, 1f);
            _progressText.Text = done + " / " + total;
        }

        /// <summary>动作区内的原生按钮（验收触发真实点击）。</summary>
        internal UIButton Button => _button;
    }

    /// <summary>相似项卡片数据源：复用胶片栏卡片，第二行换成相似度百分比与柱状条。</summary>
    private sealed class Source(iOSSimilarityPanel owner) : UICollectionViewDataSource
    {
        /// <summary>卡片复用标识。</summary>
        internal const string CellId = "PhotoCard";

        /// <summary>相似聚类始终是单个 section。</summary>
        /// <param name="collectionView">集合视图。</param>
        /// <returns>分区数。</returns>
        public override nint NumberOfSections(UICollectionView collectionView) => 1;

        /// <summary>返回当前相似项数量。</summary>
        /// <param name="collectionView">集合视图。</param>
        /// <param name="section">分区索引。</param>
        /// <returns>项目数。</returns>
        public override nint GetItemsCount(UICollectionView collectionView, nint section) => owner._items.Count;

        /// <summary>复用原生卡片，接上相似面板自身的选中命令与主 VM 的评分写回。</summary>
        /// <param name="collectionView">集合视图。</param>
        /// <param name="indexPath">项目索引路径。</param>
        /// <returns>已绑定的卡片。</returns>
        public override UICollectionViewCell GetCell(UICollectionView collectionView, NSIndexPath indexPath)
        {
            var card = (iOSThumbnailCard)collectionView.DequeueReusableCell(CellId, indexPath);
            var similar = owner._items[(int)indexPath.Item];
            card.Bind(similar.File, owner._main.Settings.ShowRating, _ => owner._panel.SelectItemCommand(similar),
                (file, rating) => _ = owner._main.SetRatingAsync(file, rating), ScoreText(similar.Score), ScoreFraction(similar.Score),
                selectByDisplayedImage: true);
            owner._main.FileVM.ThumbnailList.QueueThumbnailLoad(similar.File, true);
            return card;
        }
    }
}
