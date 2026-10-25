using System.ComponentModel;
using AppKit;
using CoreGraphics;
using Foundation;
using PhotoViewer.Apple;
using PhotoViewer.Core.AI;
using PhotoViewer.Core.Design;
using PhotoViewer.Core.Image;
using PhotoViewer.Core.Platform;
using PhotoViewer.ViewModels.Main;
using PhotoViewer.ViewModels.Main.File;

namespace PhotoViewer.Mac.Native;

/// <summary>
/// 原生相似聚类面板:几何与视觉与旧壳 <c>SimilarityListView</c> 同口径 —— 外框 <c>#1E1E1E</c> 底、
/// <c>#404040</c> 1 点描边 + 圆角 6、最小 106×154(右侧 4 点外边距由调用方摆放时留出)。
/// 无相似项时占位文字与动作区整体居中(间距 8、四周 4 点);有相似项时动作区按布局方向停靠
/// (行布局停靠在右、列布局停靠在底),其余区域为单轴列表。
/// 列表复用 <see cref="MacFilmstripLayout"/> 与 <see cref="MacThumbnailItem"/> 卡片,卡片第二行换成
/// 相似度百分比与 75%~100% 映射的柱状条(与旧壳两个相似度转换器同口径);
/// 点击卡片走 <see cref="SimilarityPanelViewModel.SelectItemCommand"/>,不直接切当前图,保留聚类锚点。
/// </summary>
internal sealed class MacSimilarityPanel : NSView
{
    /// <summary>旧壳面板 MinWidth（共享几何）。</summary>
    private const double MinWidth = SimilarityPanelGeometry.MinWidth;
    /// <summary>旧壳面板 MinHeight（共享几何）。</summary>
    private const double MinHeight = SimilarityPanelGeometry.MinHeight;
    /// <summary>外框 1 点描边。</summary>
    private const double BorderWidth = 1;
    /// <summary>外框圆角。</summary>
    private const double CornerRadius = 6;
    /// <summary>空态 StackPanel 间距。</summary>
    private const double EmptySpacing = 8;
    /// <summary>空态四周外边距。</summary>
    private const double EmptyMargin = 4;
    /// <summary>动作按钮/进度条尺寸,与卡片 90 点宽对齐,避免撑宽面板。</summary>
    private const double ActionWidth = 90;
    private const double ActionHeight = 28;
    /// <summary>列表态动作区两行内容的间距。</summary>
    private const double DockSpacing = 6;
    /// <summary>柱状条下限分数,与 <c>SimilarityScoreToProgressFractionConverter</c> 的 LowerBound 一致。</summary>
    private const double ScoreLowerBound = .75;
    /// <summary>换行文字测量的高度上限。</summary>
    private const double MeasureLimit = 10000;
    /// <summary>错误提示的最大宽度(旧壳 MaxWidth 120)。</summary>
    private const double ErrorMaxWidth = 120;
    /// <summary>列布局下面板宽度（共享几何：卡片 96 + 列表左右内边距 4/8 + 两侧 1 点描边）。</summary>
    private const double PanelWidth = SimilarityPanelGeometry.PanelWidth;
    /// <summary>行布局下列表交叉轴长度（共享几何：卡片高 + 上下内边距 + 两侧描边）。</summary>
    private static readonly double ListCross = SimilarityPanelGeometry.ListCross;
    /// <summary>行布局停靠区左右外边距（令牌 sim.dock.margin，与 SIM-07 的停靠区宽度公式同源）。</summary>
    private static readonly double DockRowMargin = DesignTokens.SimDockMargin;
    /// <summary>行布局停靠区下外边距(旧壳 Margin='3,3,3,8')。</summary>
    private const double DockRowBottom = 8;
    /// <summary>列布局停靠区左右外边距(旧壳 Margin='8,3,8,3')。</summary>
    private const double DockColumnMargin = 8;
    /// <summary>列布局停靠区上下外边距。</summary>
    private const double DockColumnMarginVertical = 3;

    private static readonly NSColor PanelColor = NSColor.FromRgb(0x1E, 0x1E, 0x1E);
    private static readonly NSColor BorderColor = NSColor.FromRgb(0x40, 0x40, 0x40);
    private static readonly NSColor PlaceholderColor = NSColor.FromRgb(0x55, 0x55, 0x55);
    private static readonly NSColor IndexErrorColor = NSColor.FromRgb(0xCC, 0x44, 0x44);
    private static readonly NSColor UnindexedColor = NSColor.FromRgb(0x77, 0x77, 0x77);

    private readonly MainViewModel _main;
    private readonly SimilarityPanelViewModel _panel;
    private readonly NSView _emptyStack = new FlippedView(CGRect.Empty);
    private readonly NSView _listHost = new FlippedView(CGRect.Empty);
    private readonly NSView _listDock = new FlippedView(CGRect.Empty);
    private readonly NSTextField _notExtracted;
    private readonly NSTextField _noSimilar;
    private readonly NSTextField _indexError;
    private readonly NSTextField _dockError;
    private readonly NSTextField _unindexed;
    private readonly ActionArea _emptyAction;
    private readonly ActionArea _dockAction;
    private readonly MacFilmstripLayout _layout;
    private readonly NSCollectionView _collection;
    private readonly MacFilmstripScrollView _scroll;
    private readonly SimilaritySource _source;
    private IReadOnlyList<SimilarityItem> _items = Array.Empty<SimilarityItem>();
    private bool _rowLayout = true;
    private bool _showRating;
    private double _availableCross;
    private double _crossSize = MinHeight;
    private bool _ready;
    private bool _opened;
    private bool _disposed;

    /// <summary>RATING-04：相似卡片冲突时 0 号槽位的来源浮窗入口，由主窗口提供。</summary>
    internal Action<ImageFile, NSView>? ShowRatingSources { get; set; }

    /// <summary>顶边为原点的布局坐标。</summary>
    public override bool IsFlipped => true;

    /// <summary>相似项原生列表,供验收读取真实卡片与几何。</summary>
    public NSCollectionView? List => _collection;

    /// <summary>交叉轴需要的长度:行布局为内容高度(最小 154),列布局为面板宽度。</summary>
    public double CrossSize => _crossSize;

    /// <summary>建立原生视图树并订阅相似面板 VM 的属性变化;构造函数除订阅外不触发推理、解码或读写文件。</summary>
    /// <param name="main">共享主 VM,面板状态取自 <c>main.FileVM.SimilarityPanel</c>。</param>
    public MacSimilarityPanel(MainViewModel main)
    {
        _main = main;
        _panel = main.FileVM.SimilarityPanel;

        WantsLayer = true;
        Layer!.BackgroundColor = PanelColor.CGColor;
        Layer.BorderColor = BorderColor.CGColor;
        Layer.BorderWidth = (nfloat)BorderWidth;
        Layer.CornerRadius = (nfloat)CornerRadius;
        Layer.MasksToBounds = true;

        _notExtracted = WrappedLabel("尚未提取特征", 11, PlaceholderColor);
        _noSimilar = WrappedLabel("无相似照片", 11, PlaceholderColor);
        _indexError = WrappedLabel("", 10, IndexErrorColor);
        _dockError = WrappedLabel("", 10, IndexErrorColor);
        _unindexed = WrappedLabel("", 10, UnindexedColor);
        _emptyAction = new ActionArea(StartIndexing);
        _dockAction = new ActionArea(StartIndexing);

        _emptyStack.WantsLayer = true;
        _emptyStack.AddSubview(_notExtracted);
        _emptyStack.AddSubview(_noSimilar);
        _emptyStack.AddSubview(_emptyAction);
        _emptyStack.AddSubview(_indexError);
        AddSubview(_emptyStack);

        _listHost.WantsLayer = true;
        _listDock.WantsLayer = true;
        _listDock.AddSubview(_unindexed);
        _listDock.AddSubview(_dockAction);
        _listDock.AddSubview(_dockError);
        _layout = new MacFilmstripLayout { Horizontal = true };
        _collection = new NSCollectionView(new CGRect(0, 0, PanelWidth, ListCross))
        {
            CollectionViewLayout = _layout,
            Selectable = false,
            BackgroundColors = [PanelColor],
        };
        _collection.RegisterClassForItem(typeof(MacThumbnailItem), "PhotoCard");
        _source = new SimilaritySource(this);
        _collection.DataSource = _source;
        _scroll = new MacFilmstripScrollView
        {
            DocumentView = _collection,
            Horizontal = true,
            HasHorizontalScroller = true,
            HasVerticalScroller = false,
            DrawsBackground = false,
            BorderType = NSBorderType.NoBorder,
            ScrollerStyle = NSScrollerStyle.Overlay,
            AutomaticallyAdjustsContentInsets = false,
        };
        _listHost.AddSubview(_scroll);
        _listHost.AddSubview(_listDock);
        AddSubview(_listHost);

        _ready = true;
        _panel.PropertyChanged += OnPanelChanged;
        Refresh();
    }

    /// <summary>由主窗口每次布局时调用:记录布局方向与交叉轴可用长度,并把内容决定的交叉轴长度同步到自身 Frame(主轴仍由调用方决定)。</summary>
    /// <param name="rowLayout">是否行布局(面板在主图下方,列表横排)。</param>
    /// <param name="availableCross">交叉轴可用长度;小于等于 0 表示不限。</param>
    public void SetLayout(bool rowLayout, double availableCross)
    {
        _availableCross = availableCross;
        if (_rowLayout != rowLayout)
        {
            _rowLayout = rowLayout;
            _layout.Horizontal = rowLayout;
            _scroll.Horizontal = rowLayout;
            _scroll.HasHorizontalScroller = rowLayout;
            _scroll.HasVerticalScroller = !rowLayout;
        }
        ApplyCross();
        Arrange();
    }

    /// <summary>从 VM 重新拉取状态并就地刷新:文字直接更新,列表仅在项目集引用变化时重载。</summary>
    public void Refresh()
    {
        if (_disposed || !_ready) return;
        bool hasItems = _panel.HasItems;
        _emptyStack.Hidden = hasItems;
        _listHost.Hidden = !hasItems;
        _emptyAction.Hidden = !_panel.ShowActionArea;
        _listDock.Hidden = !_panel.ShowActionArea;
        _unindexed.Hidden = !_panel.HasUnindexed;
        if (!_unindexed.Hidden) _unindexed.StringValue = _panel.UnindexedCount + " 张未提取";
        _notExtracted.Hidden = !_panel.ShowUnindexedPlaceholder;
        _noSimilar.Hidden = !_panel.ShowFullPlaceholder;
        _indexError.Hidden = !_panel.HasIndexError;
        _indexError.StringValue = _panel.IndexError ?? "";
        // SIM-10：列表态的错误挂在动作区，和空态错误互斥。
        _dockError.Hidden = !_panel.ShowActionAreaError;
        _dockError.StringValue = _panel.IndexError ?? "";
        UpdateAction(_emptyAction);
        UpdateAction(_dockAction);

        var items = _panel.SimilarItems;
        bool showRating = _main.Settings.ShowRating;
        if (!ReferenceEquals(items, _items) || showRating != _showRating)
        {
            _items = items;
            _showRating = showRating;
            _collection.ReloadData();
        }

        ApplyCross();
        Arrange();
    }

    /// <summary>面板首次进入窗口时通知 VM 重新统计未提取张数(旧壳在聚类开关打开时调用),只调用一次。</summary>
    public override void ViewDidMoveToWindow()
    {
        base.ViewDidMoveToWindow();
        if (!_ready || _disposed || _opened || Window == null) return;
        _opened = true;
        _panel.OnPanelOpened();
    }

    /// <summary>尺寸被外部改变(主轴拉伸或窗口缩放)时立即重排内容,不重建视图。</summary>
    public override void SetFrameSize(CGSize newSize)
    {
        base.SetFrameSize(newSize);
        if (_ready && !_disposed) Arrange();
    }

    /// <summary>只更新交叉轴长度并写入自身 Frame,主轴长度与位置交给调用方。</summary>
    private void ApplyCross()
    {
        double needed = NaturalCross();
        _crossSize = _availableCross > 0 ? Math.Min(needed, _availableCross) : needed;
        var frame = Frame;
        if (_rowLayout) frame.Height = (nfloat)_crossSize;
        else frame.Width = (nfloat)_crossSize;
        if (frame != Frame) Frame = frame;
    }

    /// <summary>内容决定的交叉轴长度:行布局为占位/动作区或列表的自然高度,列布局为卡片与内边距决定的宽度。</summary>
    private double NaturalCross()
    {
        if (!_rowLayout) return Math.Max(MinWidth, PanelWidth);
        return Math.Max(MinHeight, (_panel.HasItems ? ListCross : EmptyStackHeight()));
    }

    /// <summary>空态居中内容的自然高度:占位文字 + 动作区 + 错误提示,间距 8、四周 4 点外边距。</summary>
    private double EmptyStackHeight()
    {
        double limit = Math.Max(1, PanelWidth - BorderWidth * 2 - EmptyMargin * 2);
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
        if (!_ready || _disposed) return;
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

    /// <summary>空态:占位文字、动作区与错误提示整体垂直居中。</summary>
    private void ArrangeEmpty(double width, double height)
    {
        double limit = Math.Max(1, width - BorderWidth * 2 - EmptyMargin * 2);
        double errorWidth = Math.Min(ErrorMaxWidth, limit);
        var rows = new List<(NSView View, double Height, double RowWidth)>();
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

    /// <summary>列表态:动作区按方向停靠,列表占据剩余内容区。</summary>
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

    /// <summary>摆放停靠区内的"未提取张数 + 动作 + 错误"内容;行布局把整块内容贴底(旧壳 VerticalAlignment=Bottom)。</summary>
    private void ArrangeDockContent(CGRect region, double left, double top, double right, double bottom, bool bottomAligned)
    {
        double width = Math.Max(1, region.Width - left - right);
        double labelHeight = _unindexed.Hidden ? 0 : MeasureHeight(_unindexed, width);
        double errorHeight = _dockError.Hidden ? 0 : MeasureHeight(_dockError, Math.Min(ErrorMaxWidth, width));
        double contentHeight = (labelHeight > 0 ? labelHeight + DockSpacing : 0) + ActionHeight
            + (errorHeight > 0 ? DockSpacing + errorHeight : 0);
        double y = bottomAligned ? Math.Max(top, region.Height - bottom - contentHeight) : top;
        if (labelHeight > 0)
        {
            _unindexed.Frame = new CGRect(region.X + left, y, width, labelHeight);
            y += labelHeight + DockSpacing;
        }
        _dockAction.Frame = new CGRect(region.X + (region.Width - ActionWidth) / 2, y, ActionWidth, ActionHeight);
        if (errorHeight > 0)
        {
            y += ActionHeight + DockSpacing;
            _dockError.Frame = new CGRect(region.X + (region.Width - Math.Min(ErrorMaxWidth, width)) / 2, y,
                Math.Min(ErrorMaxWidth, width), errorHeight);
        }
    }

    /// <summary>行布局停靠区宽度:按钮、未提取文字与错误提示较宽者加左右外边距。</summary>
    private double DockWidth(double innerWidth)
    {
        double errorWidth = _dockError.Hidden ? 0 : Math.Min(ErrorMaxWidth, Math.Max(1, innerWidth - DockRowMargin * 2));
        return Math.Min(innerWidth,
            Math.Max(Math.Max(ActionWidth, MeasureWidth(_unindexed, innerWidth)), errorWidth) + DockRowMargin * 2);
    }

    /// <summary>
    /// 行布局列表态停靠区的自然宽度（SIM-07）：max(sim.action.width, 未提取计数/错误文本的自然宽) + 2 × sim.dock.margin。
    /// 主窗口用它给共享 <c>RowWidth</c> 预留停靠区，否则 1–2 项列表会被动作区挤成零宽。
    /// </summary>
    internal double DockMeasureWidth()
    {
        double text = Math.Max(ActionWidth, MeasureWidth(_unindexed, MeasureLimit));
        double error = _dockError.Hidden ? 0 : Math.Min(ErrorMaxWidth, MeasureWidth(_dockError, MeasureLimit));
        return Math.Max(text, error) + DockRowMargin * 2;
    }

    /// <summary>验收读取停靠区错误标签的可见态。</summary>
    internal bool DockErrorVisible => !_listDock.Hidden && !_dockError.Hidden;

    /// <summary>验收读取停靠区错误标签文本。</summary>
    internal string DockErrorText => _dockError.StringValue;

    /// <summary>列布局停靠区高度:全部内容行加左右外边距 8、上下外边距 3。</summary>
    private double DockHeight(double innerWidth)
    {
        double width = Math.Max(1, innerWidth - DockColumnMargin * 2);
        double labelHeight = _unindexed.Hidden ? 0 : MeasureHeight(_unindexed, width);
        double errorHeight = _dockError.Hidden ? 0 : MeasureHeight(_dockError, Math.Min(ErrorMaxWidth, width));
        return (labelHeight > 0 ? labelHeight + DockSpacing : 0) + ActionHeight
            + (errorHeight > 0 ? errorHeight + DockSpacing : 0) + DockColumnMarginVertical * 2;
    }

    /// <summary>把列表容器贴合到指定区域,并按当前轴刷新收集区内容尺寸与滚动范围。</summary>
    private void UpdateListLayout(CGRect region)
    {
        var position = _scroll.ContentView.Bounds.Location;
        if (_scroll.Frame != region) _scroll.Frame = region;
        _layout.InvalidateLayout();
        var content = _layout.CollectionViewContentSize;
        if (_collection.Frame.Size != content) _collection.Frame = new CGRect(CGPoint.Empty, content);
        _collection.LayoutSubtreeIfNeeded();
        var document = _layout.CollectionViewContentSize;
        var viewport = _scroll.ContentView.Bounds.Size;
        _scroll.ContentView.ScrollToPoint(new CGPoint(
            Math.Clamp((double)position.X, 0, Math.Max(0, (double)(document.Width - viewport.Width))),
            Math.Clamp((double)position.Y, 0, Math.Max(0, (double)(document.Height - viewport.Height)))));
        _scroll.ReflectScrolledClipView(_scroll.ContentView);
    }

    /// <summary>按 VM 状态切换按钮与进度条(两者互斥),并更新按钮文案与进度值。</summary>
    private void UpdateAction(ActionArea area)
    {
        area.ShowButton = _panel.ShowActionButton;
        area.ShowProgress = _panel.IsStateIndexing;
        if (_panel.ShowActionButton) area.Title = _panel.ActionButtonText;
        if (_panel.IsStateIndexing) area.SetProgress(_panel.IndexProgress, _panel.IndexTotal, _panel.IsIndexTotalUnknown);
    }

    /// <summary>VM 属性变化统一回到 UI 线程刷新,视图只在 UI 线程被触碰。</summary>
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

    /// <summary>相似度分数(0~1)格式化为 "NN%" 文本,与 <c>SimilarityScoreConverter</c> 同口径。</summary>
    /// <param name="score">相似度分数。</param>
    /// <returns>百分比文本。</returns>
    private static string ScoreText(double score) => (int)Math.Round(Math.Clamp(score, 0.0, 1.0) * 100) + "%";

    /// <summary>相似度分数映射到柱状条占比(75% 以下为 0),与 <c>SimilarityScoreToProgressFractionConverter</c> 同口径。</summary>
    /// <param name="score">相似度分数。</param>
    /// <returns>柱状条占满宽度时的占比。</returns>
    private static double ScoreFraction(double score)
        => (Math.Clamp(score, ScoreLowerBound, 1.0) - ScoreLowerBound) / (1.0 - ScoreLowerBound);

    /// <summary>生成可换行的只读居中标签。</summary>
    private static NSTextField WrappedLabel(string text, double size, NSColor color)
    {
        var field = new NSTextField(CGRect.Empty)
        {
            StringValue = text,
            Editable = false,
            Selectable = false,
            Bordered = false,
            DrawsBackground = false,
            Alignment = NSTextAlignment.Center,
            Font = NSFont.SystemFontOfSize((nfloat)size) ?? throw new InvalidOperationException("系统字体不可用"),
            TextColor = color,
            LineBreakMode = NSLineBreakMode.ByWordWrapping,
        };
        var cell = field.Cell;
        if (cell != null) cell.Wraps = true;
        return field;
    }

    /// <summary>量出标签在给定宽度下换行后的高度。</summary>
    private static double MeasureHeight(NSTextField field, double width)
    {
        var cell = field.Cell;
        if (cell == null || width <= 0) return 0;
        return (double)cell.CellSizeForBounds(new CGRect(0, 0, (nfloat)width, MeasureLimit)).Height;
    }

    /// <summary>量出标签在给定上限内需要的宽度(单行标签按实际文字宽度)。</summary>
    private static double MeasureWidth(NSTextField field, double limit)
    {
        var cell = field.Cell;
        if (cell == null || limit <= 0) return 0;
        return Math.Ceiling((double)cell.CellSizeForBounds(new CGRect(0, 0, (nfloat)limit, MeasureLimit)).Width);
    }

    /// <summary>注销 VM 订阅并解除卡片数据源,避免视图销毁后继续回调。</summary>
    protected override void Dispose(bool disposing)
    {
        if (disposing && !_disposed)
        {
            _disposed = true;
            _panel.PropertyChanged -= OnPanelChanged;
            _collection.DataSource = null;
        }
        base.Dispose(disposing);
    }

    /// <summary>顶边为原点的容器,子视图坐标与面板一致。</summary>
    private sealed class FlippedView(CGRect frame) : NSView(frame)
    {
        /// <summary>顶边为原点的布局坐标。</summary>
        public override bool IsFlipped => true;
    }

    /// <summary>90×28 动作区:"提取全部/补齐全部"按钮与提取进度条叠在同一位置,互斥显示。</summary>
    private sealed class ActionArea : NSView
    {
        private readonly HoverButton _button;
        private readonly NSView _progressHost;
        private readonly NSProgressIndicator _progress;
        private readonly NSTextField _progressText;

        /// <summary>顶边为原点的布局坐标。</summary>
        public override bool IsFlipped => true;

        /// <summary>按钮文案(旧壳 ActionButtonText)。</summary>
        public string Title
        {
            set => SetTitle(value);
        }

        /// <summary>是否显示按钮。</summary>
        public bool ShowButton
        {
            set => _button.Hidden = !value;
        }

        /// <summary>是否显示进度条。</summary>
        public bool ShowProgress
        {
            set => _progressHost.Hidden = !value;
        }

        /// <summary>构造动作区;按钮点击直接触发相似面板的批量提取入口。</summary>
        /// <param name="start">"提取全部/补齐全部"按钮动作。</param>
        public ActionArea(Action start)
        {
            var frame = new CGRect(0, 0, ActionWidth, ActionHeight);
            _button = new HoverButton(frame, NSColor.FromRgb(0x2A, 0x2A, 0x2A), NSColor.FromRgb(0x3A, 0x3A, 0x3A));
            _button.Layer!.BorderColor = NSColor.FromRgb(0x55, 0x55, 0x55).CGColor;
            _button.Layer.BorderWidth = 1;
            _button.Layer.CornerRadius = (nfloat)CornerRadius;
            _button.Activated += (_, _) => start();

            _progressHost = new FlippedView(frame) { WantsLayer = true };
            _progressHost.Layer!.BackgroundColor = NSColor.FromRgb(0x2A, 0x2A, 0x2A).CGColor;
            _progressHost.Layer.CornerRadius = (nfloat)CornerRadius;
            _progressHost.Layer.MasksToBounds = true;
            _progress = new NSProgressIndicator(new CGRect(3, 10, ActionWidth - 6, 8))
            {
                Style = NSProgressIndicatorStyle.Bar,
                ControlSize = NSControlSize.Small,
                Indeterminate = true,
                MinValue = 0,
                MaxValue = 1,
            };
            _progressText = WrappedLabel("", 10, NSColor.FromRgb(0xCC, 0xCC, 0xCC));
            _progressText.Frame = new CGRect(0, (ActionHeight - 14) / 2, ActionWidth, 14);
            _progressHost.AddSubview(_progress);
            _progressHost.AddSubview(_progressText);

            AddSubview(_button);
            AddSubview(_progressHost);
            _button.Hidden = true;
            _progressHost.Hidden = true;
        }

        /// <summary>更新进度值与覆盖文字;总数为 0 时进度条走不定态。</summary>
        /// <param name="done">已完成张数。</param>
        /// <param name="total">总张数。</param>
        /// <param name="unknown">总数未知(不定态)。</param>
        public void SetProgress(int done, int total, bool unknown)
        {
            _progress.Indeterminate = unknown;
            _progress.MaxValue = Math.Max(1, total);
            _progress.DoubleValue = Math.Clamp(done, 0, Math.Max(1, total));
            _progressText.StringValue = done + " / " + total;
        }

        /// <summary>按钮标题按旧壳样式绘制:11 点、<c>#CCC</c>、居中。</summary>
        private void SetTitle(string title)
        {
            using var paragraph = new NSMutableParagraphStyle { Alignment = NSTextAlignment.Center };
            using var attributes = new NSDictionary(
                NSStringAttributeKey.Font, NSFont.SystemFontOfSize(11) ?? throw new InvalidOperationException("系统字体不可用"),
                NSStringAttributeKey.ForegroundColor, NSColor.FromRgb(0xCC, 0xCC, 0xCC),
                NSStringAttributeKey.ParagraphStyle, paragraph);
            _button.AttributedTitle = new NSAttributedString(title, attributes);
        }
    }

    /// <summary>自绘深色背景的方形按钮,鼠标进入时换成旧壳 <c>:pointerover</c> 的浅底色。</summary>
    private sealed class HoverButton : NSButton
    {
        private readonly NSColor _normal;
        private readonly NSColor _hover;
        private NSTrackingArea? _tracking;

        /// <summary>构造无边框按钮并设置常态/悬停底色。</summary>
        /// <param name="frame">按钮尺寸。</param>
        /// <param name="normal">常态底色。</param>
        /// <param name="hover">悬停底色。</param>
        public HoverButton(CGRect frame, NSColor normal, NSColor hover) : base(frame)
        {
            _normal = normal;
            _hover = hover;
            Bordered = false;
            WantsLayer = true;
            Layer!.BackgroundColor = normal.CGColor;
            Layer.MasksToBounds = true;
        }

        /// <summary>按钮为无边框自绘,鼠标进入/离开自行维护悬停底色。</summary>
        public override void UpdateTrackingAreas()
        {
            base.UpdateTrackingAreas();
            if (_tracking != null) RemoveTrackingArea(_tracking);
            _tracking = new NSTrackingArea(Bounds, NSTrackingAreaOptions.MouseEnteredAndExited | NSTrackingAreaOptions.ActiveInKeyWindow, this, null);
            AddTrackingArea(_tracking);
        }

        /// <summary>鼠标进入:切换到悬停底色。</summary>
        public override void MouseEntered(NSEvent theEvent) => Layer!.BackgroundColor = _hover.CGColor;

        /// <summary>鼠标离开:恢复常态底色。</summary>
        public override void MouseExited(NSEvent theEvent) => Layer!.BackgroundColor = _normal.CGColor;
    }

    /// <summary>相似项卡片数据源:复用胶片栏卡片,第二行换成相似度百分比与柱状条。</summary>
    private sealed class SimilaritySource(MacSimilarityPanel owner) : NSCollectionViewDataSource
    {
        /// <summary>相似聚类始终是单个 section。</summary>
        public override nint GetNumberOfSections(NSCollectionView collectionView) => 1;

        /// <summary>返回当前相似项数量。</summary>
        public override nint GetNumberofItems(NSCollectionView collectionView, nint section) => owner._items.Count;

        /// <summary>复用原生卡片,接上相似面板自身的选中命令与主 VM 的评分写回。</summary>
        public override NSCollectionViewItem GetItem(NSCollectionView collectionView, NSIndexPath indexPath)
        {
            var item = (MacThumbnailItem)collectionView.MakeItem("PhotoCard", indexPath);
            if (owner._layout.GetLayoutAttributesForItem(indexPath) is { } attributes && item.View.Frame != attributes.Frame)
                item.View.Frame = attributes.Frame;
            item.View.Hidden = false;
            var similar = owner._items[(int)indexPath.Item];
            item.Bind(similar.File, owner._main.Settings.ShowRating, _ => owner._panel.SelectItemCommand(similar),
                (file, rating) => _ = owner._main.SetRatingAsync(file, rating), ScoreText(similar.Score), ScoreFraction(similar.Score),
                selectByDisplayedImage: true, showSources: owner.ShowRatingSources);
            owner._main.FileVM.ThumbnailList.QueueThumbnailLoad(similar.File, true);
            return item;
        }
    }
}
