using System.ComponentModel;
using AppKit;
using CoreGraphics;
using Foundation;
using ObjCRuntime;
using PhotoViewer.Core;
using PhotoViewer.Core.Platform;
using PhotoViewer.ViewModels.Tools;

namespace PhotoViewer.Mac.Native;

/// <summary>
/// 原生 EXIF 详情页：与旧壳 <c>ExifDetailView</c> 同构 —— 顶部居中显示不含后缀的文件名与格式切换，
/// 下方按元数据分组渲染圆角卡片，每行 7 列（ID 60 点、1 点分隔线、名称/中文名/值按 2:2:3 分配）。
/// 内容由单列表格虚拟化承载，只有可见行实例化文本控件；行高按文本换行测量并随宽度缓存。
/// 数据全部来自共享 <see cref="ExifDetailViewModel"/>（含惰性加载、异步补全与当前格式状态），本视图只读不写业务状态。
/// </summary>
internal sealed class MacExifDetailView : NSView
{
    /// <summary>页面左右外边距（旧壳 StackPanel Margin 12 与卡片 Margin 12,4）。</summary>
    private const double SideMargin = 12;

    /// <summary>滚动内容底部留白（旧壳 StackPanel Margin 0,0,0,24）。</summary>
    private const double BottomMargin = 24;

    /// <summary>文件名行的上外边距（旧壳 Margin 12,12,12,4）。</summary>
    private const double HeaderTopMargin = 12;

    /// <summary>文件名行的下外边距。</summary>
    private const double HeaderBottomMargin = 4;

    /// <summary>加载提示的上下外边距（旧壳 Margin 12,12）。</summary>
    private const double LoadingMargin = 12;

    /// <summary>分组标题的上外边距（旧壳 Margin 12,18,12,6）。</summary>
    private const double GroupHeaderTopMargin = 18;

    /// <summary>分组标题的下外边距。</summary>
    private const double GroupHeaderBottomMargin = 6;

    /// <summary>卡片的上外边距（旧壳 Margin 12,4）。</summary>
    private const double CardTopMargin = 4;

    /// <summary>卡片圆角半径。</summary>
    private const double CardRadius = 6;

    /// <summary>Tag ID 列宽（共享 7 列几何：旧壳 ColumnDefinitions 首列 60）。</summary>
    private const double IdColumnWidth = ExifTableGeometry.IdColumnWidth;

    /// <summary>列分隔线与行分隔线厚度（共享 7 列几何）。</summary>
    private const double DividerThickness = ExifTableGeometry.DividerThickness;

    /// <summary>名称/中文名/值单元格的左右内边距（共享 7 列几何）。</summary>
    private const double CellPaddingX = ExifTableGeometry.CellPaddingX;

    /// <summary>名称/中文名/值单元格的上下内边距（共享 7 列几何）。</summary>
    private const double CellPaddingY = ExifTableGeometry.CellPaddingY;

    /// <summary>值列最多显示三行（共享 7 列几何：旧壳 MaxLines=3）。</summary>
    private const int ValueMaxLines = ExifTableGeometry.ValueMaxLines;

    /// <summary>测量换行高度的宽度上限。</summary>
    private const double MeasureLimit = 10000;

    /// <summary>格式切换控件的上下内边距（旧壳 ListBoxItem 内容 Margin 8,4）。</summary>
    private const double FormatPaddingY = 4;

    /// <summary>文件名与格式切换之间的间距（旧壳 StackPanel Spacing 8）。</summary>
    private const double HeaderSpacing = 8;

    /// <summary>单行文本控件的宽度余量：避免按测量宽落位时尾部出现省略号。</summary>
    private const double LabelSlack = 8;

    /// <summary>Tag 行单元格的复用标识。</summary>
    private const string TagCellIdentifier = "exifTagCell";
    private const string HeaderCellIdentifier = "exifHeaderCell";
    private const string GroupTitleCellIdentifier = "exifGroupTitleCell";
    private const string LoadingCellIdentifier = "exifLoadingCell";

    private static readonly NSColor GroupHeaderColor = NSColor.Label;
    private static readonly NSColor IdTextColor = NSColor.SecondaryLabel;
    private static readonly NSColor NameColor = NSColor.SecondaryLabel;
    private static readonly NSColor ChineseColor = NSColor.SecondaryLabel;
    private static readonly NSColor ValueColor = NSColor.Label;
    private static readonly NSColor FileNameColor = NSColor.Label;
    private static readonly NSColor LoadingColor = NSColor.SecondaryLabel;

    /// <summary>分组卡片底色：与设置窗口 Form(.grouped) 分组底色同为低对比系统填充色。</summary>
    private static readonly NSColor CardFillColor = NSColor.QuaternarySystemFill;

    /// <summary>文件名 14 点字体。</summary>
    private static readonly NSFont FileNameFont = NSFont.SystemFontOfSize(14) ?? throw new InvalidOperationException("系统字体不可用");

    /// <summary>分组标题 16 点粗体。</summary>
    private static readonly NSFont GroupHeaderFont = NSFont.SystemFontOfSize(16, NSFontWeight.Bold) ?? throw new InvalidOperationException("系统字体不可用");

    /// <summary>单元格 13 点字体。</summary>
    private static readonly NSFont CellFont = NSFont.SystemFontOfSize(13) ?? throw new InvalidOperationException("系统字体不可用");

    /// <summary>Tag ID 11 点等宽字体。</summary>
    private static readonly NSFont IdFont = NSFont.UserFixedPitchFontOfSize(11) ?? throw new InvalidOperationException("系统字体不可用");

    /// <summary>格式切换与加载提示 13 点字体。</summary>
    private static readonly NSFont FormatFont = NSFont.SystemFontOfSize(13) ?? throw new InvalidOperationException("系统字体不可用");

    /// <summary>文件名行内容高度。</summary>
    private static readonly double HeaderContentHeight = Math.Ceiling(Math.Max(LineHeight(FileNameFont), LineHeight(FormatFont) + FormatPaddingY * 2));

    /// <summary>加载提示行高度。</summary>
    private static readonly double LoadingRowHeight = LoadingMargin * 2 + Math.Ceiling(LineHeight(FormatFont));

    /// <summary>分组标题行高度（含上外边距、标题行、下外边距与卡片上外边距）。</summary>
    private static readonly double GroupTitleRowHeight = GroupHeaderTopMargin + Math.Ceiling(LineHeight(GroupHeaderFont)) + GroupHeaderBottomMargin + CardTopMargin;

    /// <summary>文件名行高度。</summary>
    private static readonly double HeaderRowHeight = HeaderTopMargin + HeaderContentHeight + HeaderBottomMargin;

    private NSTextField? _selectionField;
    private ExifTagCellView? _selectionCell;
    private ExifTextColumn _selectionColumn;

    private readonly NSTableColumn _column;
    private readonly ExifTableView _table;
    private readonly NSScrollView _scroll;
    private readonly ExifTableSource _source;
    private readonly NSObject _clipBoundsObserver;
    private readonly Dictionary<MetadataTag, TagText> _tagTexts = [];
    private readonly List<ExifRow> _rows = [];
    private TagMetrics[] _tagMetrics = [];
    private double _metricsWidth = -1;
    private double _contentWidth;
    private double _measuredWidth = -1;
    private ExifDetailViewModel? _viewModel;
    private List<MetadataGroup>? _loadedGroups;
    private FormatEntry? _loadedFormat;
    private List<FormatEntry>? _loadedFormats;
    private bool _loadedLoading;
    private string _loadedBaseName = string.Empty;
    private bool _reloaded;
    private bool _pending;
    private bool _disposed;

    /// <summary>建立滚动容器与单列虚拟化表格。</summary>
    public MacExifDetailView()
    {
        _table = new ExifTableView(this, CGRect.Empty)
        {
            HeaderView = null,
            UsesAutomaticRowHeights = false,
            SelectionHighlightStyle = NSTableViewSelectionHighlightStyle.None,
            Style = NSTableViewStyle.Plain,
            RowSizeStyle = NSTableViewRowSizeStyle.Custom,
            RowHeight = 24,
            IntercellSpacing = CGSize.Empty,
            BackgroundColor = NSColor.Clear,
            GridStyleMask = NSTableViewGridStyle.None,
            AllowsMultipleSelection = false,
            AllowsEmptySelection = true,
            AllowsColumnReordering = false,
            AllowsColumnResizing = false,
            ColumnAutoresizingStyle = NSTableViewColumnAutoresizingStyle.Uniform,
            AutoresizingMask = NSViewResizingMask.WidthSizable,
            FocusRingType = NSFocusRingType.None,
        };
        _column = new NSTableColumn("exif")
        {
            ResizingMask = NSTableColumnResizing.Autoresizing,
            Width = 100,
        };
        _table.AddColumn(_column);
        ((NSView)_table).AccessibilityLabel = "EXIF 详情";
        _source = new ExifTableSource(this);
        _table.Source = _source;

        _scroll = new NSScrollView
        {
            DocumentView = _table,
            HasVerticalScroller = true,
            HasHorizontalScroller = false,
            AutohidesScrollers = true,
            DrawsBackground = false,
            BorderType = NSBorderType.NoBorder,
            ScrollerStyle = NSScrollerStyle.Overlay,
            AutomaticallyAdjustsContentInsets = false,
            ContentInsets = new NSEdgeInsets(0, 0, (nfloat)BottomMargin, 0),
        };
        AddSubview(_scroll);
        _clipBoundsObserver = NSNotificationCenter.DefaultCenter.AddObserver(NSView.BoundsChangedNotification, OnClipBoundsChanged, _scroll.ContentView);
    }

    /// <summary>绑定 EXIF 详情 VM；数据未变化时不重建表格。</summary>
    /// <param name="viewModel">共享 EXIF 详情 VM。</param>
    public void Show(ExifDetailViewModel viewModel)
    {
        if (_disposed) return;
        if (!ReferenceEquals(_viewModel, viewModel))
        {
            if (_viewModel != null) _viewModel.PropertyChanged -= OnViewModelChanged;
            _viewModel = viewModel;
            _viewModel.PropertyChanged += OnViewModelChanged;
            _reloaded = false;
        }
        Refresh();
    }

    /// <summary>尺寸变化时把滚动容器铺满并按新宽度刷新行高。</summary>
    public override void SetFrameSize(CGSize newSize)
    {
        base.SetFrameSize(newSize);
        SetFrame(_scroll, Bounds);
        ApplyWidth((double)_scroll.ContentView.Bounds.Width);
    }

    /// <summary>布局时同步滚动容器与行高缓存宽度。</summary>
    public override void Layout()
    {
        base.Layout();
        SetFrame(_scroll, Bounds);
        ApplyWidth((double)_scroll.ContentView.Bounds.Width);
    }

    /// <summary>VM 内容变化（格式切换、EXIF 异步到达、切换照片）后合并刷新，跨线程通知统一回到主线程。</summary>
    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (_disposed || !ReferenceEquals(sender, _viewModel)) return;
        if (_pending) return;
        _pending = true;
        UiDispatcher.Post(() =>
        {
            _pending = false;
            if (_disposed || !ReferenceEquals(sender, _viewModel)) return;
            Refresh();
        });
    }

    /// <summary>裁剪区宽度变化时清空行高缓存并通知表格重算，不重建行模型。</summary>
    private void OnClipBoundsChanged(NSNotification notification)
    {
        if (_disposed) return;
        ApplyWidth((double)_scroll.ContentView.Bounds.Width);
        if (_selectionField != null && !_selectionField.Frame.IntersectsWith(_scroll.ContentView.Bounds))
            RemoveTextSelection();
    }

    /// <summary>记录新的内容宽度；宽度确实变化时清空高度缓存并就地刷新全部行高。</summary>
    private void ApplyWidth(double width)
    {
        width = Math.Max(1, width);
        if (Math.Abs(width - _contentWidth) < 0.5) return;
        RemoveTextSelection();
        _contentWidth = width;
        _column.Width = (nfloat)width;
        _metricsWidth = -1;
        _tagMetrics = [];
        if (_reloaded && _rows.Count > 0 && _table.RowCount == _rows.Count)
            _table.NoteHeightOfRowsWithIndexesChanged(NSIndexSet.FromNSRange(new NSRange(0, _rows.Count)));
    }

    /// <summary>内容签名变化时重建行模型并重载表格，保留滚动位置。</summary>
    private void Refresh()
    {
        if (_disposed) return;
        var viewModel = _viewModel;
        if (viewModel == null) return;
        if (_reloaded
            && ReferenceEquals(viewModel.Groups, _loadedGroups)
            && ReferenceEquals(viewModel.SelectedFormat, _loadedFormat)
            && ReferenceEquals(viewModel.Formats, _loadedFormats)
            && viewModel.IsLoading == _loadedLoading
            && string.Equals(viewModel.BaseFileName, _loadedBaseName, StringComparison.Ordinal))
            return;
        Rebuild(viewModel);
    }

    /// <summary>由 VM 生成扁平行模型（文件名、加载提示、分组标题与标签行）并重载表格。</summary>
    private void Rebuild(ExifDetailViewModel viewModel)
    {
        RemoveTextSelection();
        _tagTexts.Clear();
        var origin = _scroll.ContentView.Bounds.Location;
        _rows.Clear();
        _rows.Add(new ExifRow
        {
            Kind = ExifRowKind.Header,
            Text = viewModel.BaseFileName,
            Formats = viewModel.HasMultipleFormats ? viewModel.Formats : null,
            SelectedFormat = viewModel.HasMultipleFormats ? viewModel.Formats.IndexOf(viewModel.SelectedFormat) : 0,
            Extension = viewModel.HasMultipleFormats ? null : viewModel.SelectedFormat.Label,
        });
        if (viewModel.IsLoading) _rows.Add(new ExifRow { Kind = ExifRowKind.Loading, Text = "加载中…" });
        foreach (var group in viewModel.Groups ?? [])
        {
            _rows.Add(new ExifRow { Kind = ExifRowKind.GroupTitle, Text = group.Name });
            for (int index = 0; index < group.Tags.Count; index++)
            {
                _rows.Add(new ExifRow
                {
                    Kind = ExifRowKind.Tag,
                    Tag = group.Tags[index],
                    FirstInGroup = index == 0,
                    LastInGroup = index == group.Tags.Count - 1,
                });
            }
        }
        _tagMetrics = new TagMetrics[_rows.Count];
        _metricsWidth = -1;
        _table.ReloadData();
        RestoreScroll(origin);
        _loadedGroups = viewModel.Groups;
        _loadedFormat = viewModel.SelectedFormat;
        _loadedFormats = viewModel.Formats;
        _loadedLoading = viewModel.IsLoading;
        _loadedBaseName = viewModel.BaseFileName;
        _reloaded = true;
    }

    /// <summary>按标签对象缓存的四列绘制文本，滚动时复用，避免每帧重建属性字符串。</summary>
    internal TagText TagTextFor(MetadataTag tag)
    {
        if (_tagTexts.TryGetValue(tag, out var texts)) return texts;
        texts = new TagText(
            ExifTagCellView.CreateText(tag.TagIdHex, IdFont, IdTextColor, NSLineBreakMode.TruncatingTail, NSTextAlignment.Center),
            ExifTagCellView.CreateText(tag.Name, CellFont, NameColor, NSLineBreakMode.ByWordWrapping, NSTextAlignment.Left),
            ExifTagCellView.CreateText(tag.ChineseName ?? string.Empty, CellFont, ChineseColor, NSLineBreakMode.ByWordWrapping, NSTextAlignment.Left),
            ExifTagCellView.CreateText(tag.Value, CellFont, ValueColor, NSLineBreakMode.ByWordWrapping, NSTextAlignment.Left));
        _tagTexts[tag] = texts;
        return texts;
    }

    /// <summary>当前覆盖在文本上的可选文本框；没有选中文本时为 null。</summary>
    internal NSTextField? SelectionField => _selectionField;

    /// <summary>覆盖文本框所在单元格与列是否应暂停自绘，避免文字重影。</summary>
    internal bool IsSuppressingText(ExifTagCellView cell, ExifTextColumn column)
        => ReferenceEquals(_selectionCell, cell) && _selectionColumn == column;

    /// <summary>在一次点击的文本矩形上放置（或复用）可选文本框，并把真实事件转交给它开始选择。</summary>
    private void BeginTextSelection(ExifTagCellView cell, ExifTextColumn column, NSEvent theEvent)
    {
        var field = _selectionField;
        if (field == null)
        {
            field = new NSTextField(CGRect.Empty)
            {
                Identifier = "exifSelection",
                Editable = false,
                Selectable = true,
                Bordered = false,
                Bezeled = false,
                DrawsBackground = false,
                FocusRingType = NSFocusRingType.None,
            };
            _selectionField = field;
        }
        field.StringValue = cell.TextFor(column);
        field.Font = ExifTagCellView.FontFor(column);
        field.TextColor = ExifTagCellView.ColorFor(column);
        field.LineBreakMode = ExifTagCellView.LineBreakFor(column);
        field.Alignment = ExifTagCellView.AlignmentFor(column);
        field.MaximumNumberOfLines = ExifTagCellView.MaxLinesFor(column);
        var fieldCell = field.Cell;
        if (fieldCell != null)
            fieldCell.Wraps = column is ExifTextColumn.Name or ExifTextColumn.Chinese or ExifTextColumn.Value;
        var previousCell = _selectionCell;
        _selectionCell = cell;
        _selectionColumn = column;
        if (previousCell != null) previousCell.NeedsDisplay = true;
        cell.NeedsDisplay = true;
        field.Frame = cell.ConvertRectToView(cell.TextRect(column), _table);
        field.RemoveFromSuperview();
        _table.AddSubview(field);
        _table.Window?.MakeFirstResponder(field);
        field.MouseDown(theEvent);
    }

    /// <summary>移除覆盖文本框并结束编辑；滚动出视口、重载或点击其他位置时调用。</summary>
    internal void RemoveTextSelection()
    {
        var field = _selectionField;
        if (field == null) return;
        _selectionField = null;
        _selectionCell?.NeedsDisplay = true;
        _selectionCell = null;
        if (field.CurrentEditor != null) field.Window?.MakeFirstResponder(_table);
        field.RemoveFromSuperview();
        field.Dispose();
    }

    /// <summary>重载后回到原滚动位置；内容变短时夹取到底部。</summary>
    private void RestoreScroll(CGPoint origin)
    {
        double maxY = Math.Max(0, (double)_table.Bounds.Height - (double)_scroll.ContentView.Bounds.Height);
        double y = Math.Max(0, Math.Min((double)origin.Y, maxY));
        _scroll.ContentView.ScrollToPoint(new CGPoint(origin.X, y));
        _scroll.ReflectScrolledClipView(_scroll.ContentView);
    }

    /// <summary>行的测量宽度（表格可用宽）。</summary>
    private double RowWidth => _contentWidth > 1 ? _contentWidth : Math.Max(1, (double)Bounds.Width);

    /// <summary>取得行高；标签行按当前宽度测量并缓存。</summary>
    private double HeightForRow(int row)
    {
        if (row < 0 || row >= _rows.Count) return 24;
        var entry = _rows[row];
        switch (entry.Kind)
        {
            case ExifRowKind.Header: return HeaderRowHeight;
            case ExifRowKind.Loading: return LoadingRowHeight;
            case ExifRowKind.GroupTitle: return GroupTitleRowHeight;
            default:
                return MetricsForRow(row, entry.Tag!).RowHeight;
        }
    }

    /// <summary>取得标签行的三列文本高度与行高；按当前宽度缓存，行高查询与单元格布局共用。</summary>
    private TagMetrics MetricsForRow(int row, MetadataTag tag)
    {
        double width = RowWidth;
        if (Math.Abs(width - _metricsWidth) > 0.5)
        {
            _tagMetrics = new TagMetrics[_rows.Count];
            _metricsWidth = width;
        }
        if (row < 0 || row >= _tagMetrics.Length) return new TagMetrics(1, 1, 1);
        var metrics = _tagMetrics[row];
        if (metrics.RowHeight > 0) return metrics;
        var columns = ExifTableGeometry.Compute(Math.Max(1, width - SideMargin * 2));
        metrics = new TagMetrics(
            TextHeight(tag.Name, CellFont, columns.NameTextWidth, 0),
            TextHeight(tag.ChineseName ?? string.Empty, CellFont, columns.ChineseTextWidth, 0),
            TextHeight(tag.Value, CellFont, columns.ValueTextWidth, ValueMaxLines));
        _tagMetrics[row] = metrics;
        return metrics;
    }

    /// <summary>取得行视图；Tag 行经复用队列回收，其余行按需新建。</summary>
    private NSView ViewForRow(int row)
    {
        var entry = _rows[row];
        switch (entry.Kind)
        {
            case ExifRowKind.Header:
                var header = _table.MakeView(HeaderCellIdentifier, _table) as HeaderCellView;
                if (header == null) header = new HeaderCellView { Identifier = HeaderCellIdentifier };
                header.Configure(entry, _viewModel!);
                return header;
            case ExifRowKind.Loading:
                var loading = _table.MakeView(LoadingCellIdentifier, _table) as LoadingCellView;
                if (loading == null) loading = new LoadingCellView { Identifier = LoadingCellIdentifier };
                loading.Configure(entry.Text);
                return loading;
            case ExifRowKind.GroupTitle:
                var title = _table.MakeView(GroupTitleCellIdentifier, _table) as GroupTitleCellView;
                if (title == null) title = new GroupTitleCellView { Identifier = GroupTitleCellIdentifier };
                title.Configure(entry.Text);
                return title;
            default:
                var cell = _table.MakeView(TagCellIdentifier, _table) as ExifTagCellView;
                if (cell == null)
                {
                    cell = new ExifTagCellView(this) { Identifier = TagCellIdentifier };
                }
                cell.Configure(row, entry.Tag!);
                return cell;
        }
    }

    /// <summary>行背景：标签行绘制分组卡片、列分隔线与行分隔线。</summary>
    private sealed class ExifRowView : NSTableRowView
    {
        private readonly bool _card;
        private readonly bool _first;
        private readonly bool _last;

        public ExifRowView(ExifRow entry)
        {
            _card = entry.Kind == ExifRowKind.Tag;
            _first = entry.FirstInGroup;
            _last = entry.LastInGroup;
        }

        public override bool IsFlipped => true;

        public override void DrawBackground(CGRect dirtyRect)
        {
            if (!_card) return;
            double width = (double)Bounds.Width;
            double height = (double)Bounds.Height;
            double cardWidth = Math.Max(1, width - SideMargin * 2);
            var card = new CGRect(SideMargin, _first ? 0 : -CardRadius, cardWidth, height + (_first ? 0 : CardRadius) + (_last ? 0 : CardRadius));
            var context = NSGraphicsContext.CurrentContext;
            context?.SaveGraphicsState();
            using (var clip = NSBezierPath.FromRect(Bounds)) clip.AddClip();
            using (var path = NSBezierPath.FromRoundedRect(card, (nfloat)CardRadius, (nfloat)CardRadius))
            {
                CardFillColor.SetFill();
                path.Fill();
            }
            context?.RestoreGraphicsState();

            var columns = ExifTableGeometry.Compute(cardWidth);
            double dividerX = SideMargin + IdColumnWidth;
            FillColumnDivider(dividerX, height);
            dividerX += DividerThickness + columns.NameColumn;
            FillColumnDivider(dividerX, height);
            dividerX += DividerThickness + columns.ChineseColumn;
            FillColumnDivider(dividerX, height);
            if (!_last)
            {
                NSColor.Separator.SetFill();
                using var rowDivider = NSBezierPath.FromRect(new CGRect(SideMargin, height - DividerThickness, cardWidth, DividerThickness));
                rowDivider.Fill();
            }
        }

        /// <summary>绘制一条贯穿整行的 1 点列分隔线。</summary>
        private static void FillColumnDivider(double x, double height)
        {
            NSColor.Separator.SetFill();
            using var line = NSBezierPath.FromRect(new CGRect(x, 0, DividerThickness, height));
            line.Fill();
        }
    }

    /// <summary>文件名行：居中文件名 + 多格式分段控件或单格式后缀。</summary>
    private sealed class HeaderCellView : NSTableCellView
    {
        private readonly NSTextField _name;
        private readonly NSTextField _extension;
        private NSSegmentedControl? _formats;
        private ExifDetailViewModel? _viewModel;

        public HeaderCellView()
        {
            _name = SingleLineLabel(string.Empty, FileNameFont, FileNameColor);
            _name.Alignment = NSTextAlignment.Center;
            _extension = SingleLineLabel(string.Empty, FormatFont, ChineseColor);
            _extension.Hidden = true;
            AddSubview(_name);
            AddSubview(_extension);
        }

        public override bool IsFlipped => true;

        /// <summary>更新文件名与格式切换控件；分段控件与标签在复用时保持同一实例。</summary>
        public void Configure(ExifRow entry, ExifDetailViewModel viewModel)
        {
            _viewModel = viewModel;
            SetText(_name, entry.Text);
            if (entry.Formats is { Count: > 0 } formats)
            {
                if (_formats == null)
                {
                    NSSegmentedControl? control = null;
                    control = NSSegmentedControl.FromLabels([], NSSegmentSwitchTracking.SelectOne, () => Select(control));
                    control.SegmentStyle = NSSegmentStyle.Rounded;
                    _formats = control;
                    AddSubview(control);
                }
                if (_formats.SegmentCount != formats.Count) _formats.SegmentCount = formats.Count;
                for (int index = 0; index < formats.Count; index++) _formats.SetLabel(formats[index].Label, index);
                _formats.SelectedSegment = entry.SelectedFormat;
                _formats.SizeToFit();
                _formats.Hidden = false;
                _extension.Hidden = true;
            }
            else
            {
                if (_formats != null) _formats.Hidden = true;
                string extension = entry.Extension ?? string.Empty;
                SetText(_extension, extension);
                _extension.Hidden = false;
            }
            NeedsLayout = true;
        }

        /// <summary>分段控件选择变化写回共享 VM。</summary>
        private void Select(NSSegmentedControl? control)
        {
            if (control == null || _viewModel == null) return;
            int index = (int)control.SelectedSegment;
            if (index >= 0 && index < _viewModel.Formats.Count && !ReferenceEquals(_viewModel.SelectedFormat, _viewModel.Formats[index]))
                _viewModel.SelectedFormat = _viewModel.Formats[index];
        }

        public override void Layout()
        {
            base.Layout();
            double available = Math.Max(1, (double)Bounds.Width - SideMargin * 2);
            double nameWidth = Math.Min(TextWidth(_name.StringValue, FileNameFont) + LabelSlack, available);
            bool multi = _formats is { Hidden: false };
            double otherWidth = multi ? (double)_formats!.Frame.Width : TextWidth(_extension.StringValue, FormatFont) + LabelSlack;
            double spacing = !multi && _extension.Hidden ? 0 : HeaderSpacing;
            nameWidth = Math.Min(nameWidth, Math.Max(1, available - spacing - otherWidth));
            double total = nameWidth + spacing + otherWidth;
            double start = Math.Max(SideMargin, ((double)Bounds.Width - total) / 2);
            double nameHeight = Math.Ceiling(LineHeight(FileNameFont));
            SetFrame(_name, new CGRect(start, HeaderTopMargin + (HeaderContentHeight - nameHeight) / 2, nameWidth, nameHeight));
            double otherHeight = multi ? Math.Min(HeaderContentHeight, (double)_formats!.Frame.Height) : Math.Ceiling(LineHeight(FormatFont));
            var otherFrame = new CGRect(start + nameWidth + spacing, HeaderTopMargin + (HeaderContentHeight - otherHeight) / 2, otherWidth, otherHeight);
            if (multi) SetFrame(_formats!, otherFrame);
            else SetFrame(_extension, otherFrame);
        }
    }

    /// <summary>分组标题行（16 点粗体，左侧与卡片对齐）。</summary>
    private sealed class GroupTitleCellView : NSTableCellView
    {
        private readonly NSTextField _title;

        public GroupTitleCellView()
        {
            _title = SingleLineLabel(string.Empty, GroupHeaderFont, GroupHeaderColor);
            AddSubview(_title);
        }

        public override bool IsFlipped => true;

        public void Configure(string title)
        {
            SetText(_title, title);
            NeedsLayout = true;
        }

        public override void Layout()
        {
            base.Layout();
            SetFrame(_title, new CGRect(SideMargin, GroupHeaderTopMargin, Math.Max(1, (double)Bounds.Width - SideMargin * 2), Math.Ceiling(LineHeight(GroupHeaderFont))));
        }
    }

    /// <summary>加载提示行（次级色、居中）。</summary>
    private sealed class LoadingCellView : NSTableCellView
    {
        private readonly NSTextField _label;

        public LoadingCellView()
        {
            _label = SingleLineLabel(string.Empty, FormatFont, LoadingColor);
            _label.Alignment = NSTextAlignment.Center;
            AddSubview(_label);
        }

        public override bool IsFlipped => true;

        public void Configure(string text)
        {
            SetText(_label, text);
            NeedsLayout = true;
        }

        public override void Layout()
        {
            base.Layout();
            SetFrame(_label, new CGRect(SideMargin, LoadingMargin, Math.Max(1, (double)Bounds.Width - SideMargin * 2), Math.Ceiling(LineHeight(FormatFont))));
        }
    }

    /// <summary>标签行可选中复制的四列之一。</summary>
    internal enum ExifTextColumn
    {
        Id,
        Name,
        Chinese,
        Value,
    }

    /// <summary>单条元数据的 7 列行：ID、名称、中文名与最多三行的值。
    /// 文本直接绘制（无子控件），只有被点击时才由详情页覆盖一个可选 NSTextField。</summary>
    internal sealed class ExifTagCellView : NSTableCellView
    {
        private readonly MacExifDetailView _owner;
        private int _row = -1;
        private MetadataTag? _tag;
        private NSAttributedString? _idText;
        private NSAttributedString? _nameText;
        private NSAttributedString? _chineseText;
        private NSAttributedString? _valueText;

        public ExifTagCellView(MacExifDetailView owner)
        {
            _owner = owner;
            AccessibilityElement = true;
            AccessibilityRole = "AXStaticText";
        }

        public override bool IsFlipped => true;

        internal MetadataTag? Tag => _tag;
        internal int RowIndex => _row;

        /// <summary>更新所属行与四列文本，并重建绘制用的属性字符串。</summary>
        public void Configure(int row, MetadataTag tag)
        {
            _row = row;
            _tag = tag;
            var texts = _owner.TagTextFor(tag);
            _idText = texts.Id;
            _nameText = texts.Name;
            _chineseText = texts.Chinese;
            _valueText = texts.Value;
            NeedsDisplay = true;
        }

        /// <summary>标签行的无障碍静态文本值（ID / 名称 / 中文名 / 值）。</summary>
        public override NSObject AccessibilityValue
            => new NSString(_tag == null ? string.Empty : $"{_tag.TagIdHex} {_tag.Name} {_tag.ChineseName} {_tag.Value}");

        /// <summary>把四列文本绘制到与行高缓存相同的矩形中。</summary>
        public override void DrawRect(CGRect dirtyRect)
        {
            base.DrawRect(dirtyRect);
            if (_tag == null) return;
            var rects = TextRects();
            const NSStringDrawingOptions options = NSStringDrawingOptions.UsesLineFragmentOrigin | NSStringDrawingOptions.UsesFontLeading;
            if (!_owner.IsSuppressingText(this, ExifTextColumn.Id)) DrawText(_idText, rects.Id, options);
            if (!_owner.IsSuppressingText(this, ExifTextColumn.Name)) DrawText(_nameText, rects.Name, options);
            if (!_owner.IsSuppressingText(this, ExifTextColumn.Chinese)) DrawText(_chineseText, rects.Chinese, options);
            if (!_owner.IsSuppressingText(this, ExifTextColumn.Value)) DrawText(_valueText, rects.Value, options | NSStringDrawingOptions.TruncatesLastVisibleLine);
        }

        /// <summary>指定列的文本矩形（单元格坐标）。</summary>
        internal CGRect TextRect(ExifTextColumn column)
        {
            var rects = TextRects();
            return column switch
            {
                ExifTextColumn.Id => rects.Id,
                ExifTextColumn.Name => rects.Name,
                ExifTextColumn.Chinese => rects.Chinese,
                _ => rects.Value,
            };
        }

        /// <summary>四列文本矩形（单元格坐标），与高度缓存的测量口径一致。</summary>
        internal (CGRect Id, CGRect Name, CGRect Chinese, CGRect Value) TextRects()
        {
            var metrics = _tag == null || _row < 0 ? default : _owner.MetricsForRow(_row, _tag);
            var columns = ExifTableGeometry.Compute(Math.Max(1, (double)Bounds.Width - SideMargin * 2));
            double contentHeight = Math.Max(0, (double)Bounds.Height - DividerThickness);
            double idHeight = Math.Ceiling(LineHeight(IdFont));
            var id = new CGRect(SideMargin, (contentHeight - idHeight) / 2, IdColumnWidth, idHeight);
            double x = SideMargin + IdColumnWidth + DividerThickness;
            var name = new CGRect(x + CellPaddingX, CellPaddingY, columns.NameTextWidth, metrics.NameHeight);
            x += columns.NameColumn + DividerThickness;
            var chinese = new CGRect(x + CellPaddingX, CellPaddingY, columns.ChineseTextWidth, metrics.ChineseHeight);
            x += columns.ChineseColumn + DividerThickness;
            var value = new CGRect(x + CellPaddingX, CellPaddingY, columns.ValueTextWidth, metrics.ValueHeight);
            return (id, name, chinese, value);
        }

        /// <summary>把点击位置换算成列；点在文本矩形外时返回 false。</summary>
        internal bool TryBeginTextSelection(NSEvent theEvent, out ExifTextColumn column)
        {
            column = ExifTextColumn.Id;
            if (_tag == null) return false;
            var point = ConvertPointFromView(theEvent.LocationInWindow, null);
            if (Contains(TextRect(ExifTextColumn.Id), point)) { column = ExifTextColumn.Id; return true; }
            if (Contains(TextRect(ExifTextColumn.Name), point)) { column = ExifTextColumn.Name; return true; }
            if (Contains(TextRect(ExifTextColumn.Chinese), point)) { column = ExifTextColumn.Chinese; return true; }
            if (Contains(TextRect(ExifTextColumn.Value), point)) { column = ExifTextColumn.Value; return true; }
            return false;
        }

        /// <summary>某个文本矩形是否包含单元格坐标中的点。</summary>
        private static bool Contains(CGRect rect, CGPoint point)
            => point.X >= rect.X && point.X <= rect.Right && point.Y >= rect.Y && point.Y <= rect.Bottom;

        /// <summary>指定列的文本内容。</summary>
        internal string TextFor(ExifTextColumn column) => column switch
        {
            ExifTextColumn.Id => _tag?.TagIdHex ?? string.Empty,
            ExifTextColumn.Name => _tag?.Name ?? string.Empty,
            ExifTextColumn.Chinese => _tag?.ChineseName ?? string.Empty,
            _ => _tag?.Value ?? string.Empty,
        };

        /// <summary>指定列的字体。</summary>
        internal static NSFont FontFor(ExifTextColumn column) => column == ExifTextColumn.Id ? IdFont : CellFont;

        /// <summary>指定列的文字颜色。</summary>
        internal static NSColor ColorFor(ExifTextColumn column) => column switch
        {
            ExifTextColumn.Id => IdTextColor,
            ExifTextColumn.Name => NameColor,
            ExifTextColumn.Chinese => ChineseColor,
            _ => ValueColor,
        };

        /// <summary>指定列的换行模式。</summary>
        internal static NSLineBreakMode LineBreakFor(ExifTextColumn column) => column switch
        {
            ExifTextColumn.Id => NSLineBreakMode.TruncatingTail,
            ExifTextColumn.Name => NSLineBreakMode.ByWordWrapping,
            ExifTextColumn.Chinese => NSLineBreakMode.ByWordWrapping,
            _ => NSLineBreakMode.TruncatingTail,
        };

        /// <summary>指定列的最多行数；0 表示不限。</summary>
        internal static int MaxLinesFor(ExifTextColumn column) => column == ExifTextColumn.Value ? ValueMaxLines : 0;

        /// <summary>指定列的对齐方式。</summary>
        internal static NSTextAlignment AlignmentFor(ExifTextColumn column) => column == ExifTextColumn.Id ? NSTextAlignment.Center : NSTextAlignment.Left;

        /// <summary>构造带字体、颜色与段落样式的绘制文本。</summary>
        internal static NSAttributedString CreateText(string? value, NSFont font, NSColor color, NSLineBreakMode lineBreak, NSTextAlignment alignment)
        {
            var paragraph = new NSMutableParagraphStyle { LineBreakMode = lineBreak, Alignment = alignment };
            return new NSAttributedString(value ?? string.Empty, new NSStringAttributes
            {
                Font = font,
                ForegroundColor = color,
                ParagraphStyle = paragraph,
            });
        }

        /// <summary>按换行矩形绘制单段文本。</summary>
        private static void DrawText(NSAttributedString? text, CGRect rect, NSStringDrawingOptions options)
        {
            if (text == null || text.Length == 0 || rect.Width < 1 || rect.Height < 1) return;
            text.DrawWithRect(rect, options, null!);
        }
    }

    /// <summary>一条标签行的四列绘制文本。</summary>
    internal readonly struct TagText(NSAttributedString id, NSAttributedString name, NSAttributedString chinese, NSAttributedString value)
    {
        public NSAttributedString Id { get; } = id;
        public NSAttributedString Name { get; } = name;
        public NSAttributedString Chinese { get; } = chinese;
        public NSAttributedString Value { get; } = value;
    }

    /// <summary>标签行的三列文本高度与整行高度（含内边距与行分隔线）。</summary>
    private readonly struct TagMetrics(double nameHeight, double chineseHeight, double valueHeight)
    {
        public double NameHeight { get; } = nameHeight;
        public double ChineseHeight { get; } = chineseHeight;
        public double ValueHeight { get; } = valueHeight;
        public double RowHeight { get; } = Math.Max(nameHeight, Math.Max(chineseHeight, valueHeight)) + CellPaddingY * 2 + DividerThickness;
    }

    /// <summary>表格数据源/代理：行数、行高、行视图与单元格视图全部取自本视图的行模型。</summary>
    private sealed class ExifTableSource(MacExifDetailView owner) : NSTableViewSource
    {
        public override nint GetRowCount(NSTableView tableView) => owner._rows.Count;

        public override nfloat GetRowHeight(NSTableView tableView, nint row) => (nfloat)owner.HeightForRow((int)row);

        public override NSView GetViewForItem(NSTableView tableView, NSTableColumn? column, nint row) => owner.ViewForRow((int)row);

        public override NSTableRowView GetRowView(NSTableView tableView, nint row)
        {
            return new ExifRowView(owner._rows[(int)row]);
        }
    }

    /// <summary>表格：把落在标签文本上的首击交给详情页覆盖的可选文本框，其余点击清除覆盖层。</summary>
    private sealed class ExifTableView(MacExifDetailView owner, CGRect frame) : NSTableView(frame)
    {
        public override void MouseDown(NSEvent theEvent)
        {
            var point = ConvertPointFromView(theEvent.LocationInWindow, null);
            nint row = GetRow(point);
            if (row >= 0 && GetView(0, row, false) is ExifTagCellView cell
                && cell.TryBeginTextSelection(theEvent, out var column))
            {
                owner.BeginTextSelection(cell, column, theEvent);
                return;
            }
            owner.RemoveTextSelection();
            base.MouseDown(theEvent);
        }
    }

    /// <summary>写入单元格文本；绕过控件层赋值通知，避免每次复用都重算文本控件外观。</summary>
    private static void SetText(NSTextField field, string? text)
    {
        text ??= string.Empty;
        if (field.StringValue == text) return;
        if (field.Cell != null) field.Cell.StringValue = text;
        else field.StringValue = text;
        field.NeedsDisplay = true;
    }

    /// <summary>仅在目标 frame 变化时写入，避免可选文本控件重复排版。</summary>
    private static void SetFrame(NSView view, CGRect frame)
    {
        var current = view.Frame;
        if (Math.Abs((double)current.X - (double)frame.X) < 0.01
            && Math.Abs((double)current.Y - (double)frame.Y) < 0.01
            && Math.Abs((double)current.Width - (double)frame.Width) < 0.01
            && Math.Abs((double)current.Height - (double)frame.Height) < 0.01)
            return;
        view.Frame = frame;
    }

    /// <summary>字体的单行高度（上升部+下降部+行距）。</summary>
    private static double LineHeight(NSFont font)
        => Math.Ceiling((double)font.Ascender - (double)font.Descender + (double)font.Leading);

    /// <summary>文本自然宽度。</summary>
    private static double TextWidth(string? text, NSFont font)
    {
        if (string.IsNullOrEmpty(text)) return 0;
        using var attributed = new NSAttributedString(text, new NSStringAttributes { Font = font });
        return Math.Ceiling((double)attributed.Size.Width);
    }

    /// <summary>文本在给定宽度下换行后的高度；<paramref name="maxLines"/> 为 0 时不限行数。</summary>
    private static double TextHeight(string? text, NSFont font, double width, int maxLines)
    {
        double lineHeight = LineHeight(font);
        if (string.IsNullOrEmpty(text)) return lineHeight;
        using var attributed = new NSAttributedString(text, new NSStringAttributes { Font = font });
        if (!text.Contains('\n') && (double)attributed.Size.Width <= width) return lineHeight;
        var rect = attributed.BoundingRectWithSize(new CGSize(Math.Max(1, width), MeasureLimit),
            NSStringDrawingOptions.UsesLineFragmentOrigin | NSStringDrawingOptions.UsesFontLeading);
        double height = Math.Ceiling((double)rect.Height);
        if (maxLines > 0) height = Math.Min(height, Math.Ceiling(lineHeight * maxLines));
        return Math.Max(1, height);
    }

    /// <summary>创建单行只读标签（尾部截断）。</summary>
    private static NSTextField SingleLineLabel(string text, NSFont font, NSColor color)
        => Label(CGRect.Empty, text, font, color, NSLineBreakMode.TruncatingTail, false);

    /// <summary>创建可选中但不接受编辑的原生标签。</summary>
    private static NSTextField Label(CGRect frame, string text, NSFont font, NSColor color, NSLineBreakMode lineBreakMode, bool wraps)
    {
        var field = new NSTextField(frame)
        {
            StringValue = text ?? string.Empty,
            Editable = false,
            Selectable = true,
            Bordered = false,
            DrawsBackground = false,
            Font = font,
            TextColor = color,
            LineBreakMode = lineBreakMode,
        };
        var cell = field.Cell;
        if (cell != null) cell.Wraps = wraps;
        return field;
    }

    /// <summary>解除订阅并释放表格与滚动容器。</summary>
    /// <param name="disposing">是否由显式释放触发。</param>
    protected override void Dispose(bool disposing)
    {
        if (disposing && !_disposed)
        {
            _disposed = true;
            if (_viewModel != null)
            {
                _viewModel.PropertyChanged -= OnViewModelChanged;
                _viewModel = null;
            }
            NSNotificationCenter.DefaultCenter.RemoveObserver(_clipBoundsObserver);
            RemoveTextSelection();
            _table.Source = null;
            _table.RemoveFromSuperview();
            _table.Dispose();
            _source.Dispose();
            _scroll.Dispose();
        }
        base.Dispose(disposing);
    }

    /// <summary>扁平行模型的种类。</summary>
    private enum ExifRowKind
    {
        Header,
        Loading,
        GroupTitle,
        Tag,
    }

    /// <summary>扁平行模型：文件名、加载提示、分组标题或一条元数据标签。</summary>
    private sealed class ExifRow
    {
        public ExifRowKind Kind { get; init; }
        public string Text { get; init; } = string.Empty;
        public MetadataTag? Tag { get; init; }
        public bool FirstInGroup { get; init; }
        public bool LastInGroup { get; init; }
        public List<FormatEntry>? Formats { get; init; }
        public int SelectedFormat { get; init; }
        public string? Extension { get; init; }
    }
}
