using System.ComponentModel;
using AppKit;
using CoreGraphics;
using Foundation;
using PhotoViewer.Apple;
using PhotoViewer.Core;
using PhotoViewer.Core.Platform;
using PhotoViewer.ViewModels.Tools;

namespace PhotoViewer.Mac.Native;

/// <summary>
/// 原生 EXIF 详情页：与旧壳 <c>ExifDetailView</c> 同构 —— 顶部居中显示不含后缀的文件名与格式切换按钮，
/// 下方按元数据分组渲染圆角卡片，每行 7 列（ID 60 点、1 点分隔线、名称/中文名/值按 2:2:3 分配）。
/// 数据全部来自共享 <see cref="ExifDetailViewModel"/>（含惰性加载、异步补全与当前格式状态），本视图只读不写业务状态。
/// </summary>
internal sealed class MacExifDetailView : NSView
{
    /// <summary>页面左右外边距（旧壳 StackPanel Margin 12 与卡片 Margin 12,4）。</summary>
    private const double SideMargin = 12;

    /// <summary>页面底部外边距（旧壳 StackPanel Margin 0,0,0,24）。</summary>
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

    /// <summary>卡片的下外边距。</summary>
    private const double CardBottomMargin = 4;

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

    /// <summary>格式按钮左右内边距（旧壳 ListBoxItem 内容 Margin 8,4）。</summary>
    private const double FormatPaddingX = 8;

    /// <summary>格式按钮上下内边距。</summary>
    private const double FormatPaddingY = 4;

    /// <summary>格式按钮之间的间距（旧壳 StackPanel Spacing 4）。</summary>
    private const double FormatSpacing = 4;

    /// <summary>文件名与格式切换之间的间距（旧壳 StackPanel Spacing 8）。</summary>
    private const double HeaderSpacing = 8;

    private static readonly NSColor GroupHeaderColor = NSColor.Label;
    private static readonly NSColor IdTextColor = NSColor.SecondaryLabel;
    private static readonly NSColor NameColor = NSColor.SecondaryLabel;
    private static readonly NSColor ChineseColor = NSColor.SecondaryLabel;
    private static readonly NSColor ValueColor = NSColor.Label;
    private static readonly NSColor FileNameColor = NSColor.Label;
    private static readonly NSColor LoadingColor = NSColor.SecondaryLabel;

    /// <summary>文件名 14 点字体。</summary>
    private static readonly NSFont FileNameFont = NSFont.SystemFontOfSize(14) ?? throw new InvalidOperationException("系统字体不可用");

    /// <summary>分组标题 16 点粗体。</summary>
    private static readonly NSFont GroupHeaderFont = NSFont.SystemFontOfSize(16, NSFontWeight.Bold) ?? throw new InvalidOperationException("系统字体不可用");

    /// <summary>单元格 13 点字体。</summary>
    private static readonly NSFont CellFont = NSFont.SystemFontOfSize(13) ?? throw new InvalidOperationException("系统字体不可用");

    /// <summary>Tag ID 11 点等宽字体。</summary>
    private static readonly NSFont IdFont = NSFont.UserFixedPitchFontOfSize(11) ?? throw new InvalidOperationException("系统字体不可用");

    /// <summary>格式切换 13 点字体。</summary>
    private static readonly NSFont FormatFont = NSFont.SystemFontOfSize(13) ?? throw new InvalidOperationException("系统字体不可用");

    private readonly NSScrollView _scroll;
    private readonly NSView _document = new FlippedPane();
    private readonly NSTextField _fileName = SingleLineLabel("", FileNameFont, FileNameColor);
    private readonly NSTextField _formatLabel = SingleLineLabel("", FormatFont, ChineseColor);
    private readonly NSTextField _loading = SingleLineLabel("加载中...", FormatFont, LoadingColor);
    private readonly NSTextField _measure = SingleLineLabel("", CellFont, NameColor);
    private readonly List<NSView> _built = [];
    private readonly List<GroupBlock> _groups = [];
    private readonly List<FormatButton> _formatButtons = [];
    private ExifDetailViewModel? _viewModel;
    private bool _pending;
    private bool _disposed;

    /// <summary>建立滚动容器与文件名/格式/加载提示三个常驻标签。</summary>
    public MacExifDetailView()
    {
        WantsLayer = true;
        Layer!.BackgroundColor = NSColor.Clear.CGColor;
        _loading.Alignment = NSTextAlignment.Center;
        _document.AddSubview(_fileName);
        _document.AddSubview(_formatLabel);
        _document.AddSubview(_loading);
        _scroll = new NSScrollView
        {
            DocumentView = _document,
            HasVerticalScroller = true,
            HasHorizontalScroller = false,
            AutohidesScrollers = true,
            DrawsBackground = false,
            BorderType = NSBorderType.NoBorder,
            ScrollerStyle = NSScrollerStyle.Overlay,
            AutomaticallyAdjustsContentInsets = false,
        };
        AddSubview(_scroll);
    }

    /// <summary>绑定 EXIF 详情 VM 并按当前状态重建内容；VM 实例变化时切换订阅。</summary>
    /// <param name="viewModel">共享 EXIF 详情 VM。</param>
    public void Show(ExifDetailViewModel viewModel)
    {
        if (_disposed) return;
        if (!ReferenceEquals(_viewModel, viewModel))
        {
            if (_viewModel != null) _viewModel.PropertyChanged -= OnViewModelChanged;
            _viewModel = viewModel;
            _viewModel.PropertyChanged += OnViewModelChanged;
        }
        Build();
    }

    /// <summary>尺寸变化时把滚动容器铺满并重排文档内容。</summary>
    public override void SetFrameSize(CGSize newSize)
    {
        base.SetFrameSize(newSize);
        _scroll.Frame = Bounds;
        Relayout();
    }

    /// <summary>VM 内容变化（格式切换、EXIF 异步到达、切换照片）后合并重建，跨线程通知统一回到主线程。</summary>
    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (_disposed || !ReferenceEquals(sender, _viewModel)) return;
        if (_pending) return;
        _pending = true;
        UiDispatcher.Post(() =>
        {
            _pending = false;
            if (_disposed || !ReferenceEquals(sender, _viewModel)) return;
            Build();
        });
    }

    /// <summary>释放上一次内容视图并重建：文件名、格式切换按钮、分组卡片与全部行视图。</summary>
    private void Build()
    {
        ClearBuilt();
        var viewModel = _viewModel;
        if (viewModel == null)
        {
            Relayout();
            return;
        }

        _fileName.StringValue = viewModel.BaseFileName;
        if (viewModel.HasMultipleFormats)
        {
            _formatLabel.Hidden = true;
            for (int index = 0; index < viewModel.Formats.Count; index++)
            {
                var entry = viewModel.Formats[index];
                var button = new FormatButton(entry.Label, entry.IsSelected, () => SelectFormat(entry));
                _formatButtons.Add(button);
                _built.Add(button);
                _document.AddSubview(button);
            }
        }
        else
        {
            _formatLabel.Hidden = false;
            _formatLabel.StringValue = viewModel.SelectedFormat.Label;
        }

        foreach (var group in viewModel.Groups ?? [])
            _groups.Add(BuildGroup(group));
        Relayout();
    }

    /// <summary>切换格式：写回共享 VM（触发惰性加载）后立即重建。</summary>
    /// <param name="entry">目标格式条目。</param>
    private void SelectFormat(FormatEntry entry)
    {
        if (_viewModel == null) return;
        _viewModel.SelectedFormat = entry;
        Build();
    }

    /// <summary>建立单个元数据分组的标题、圆角卡片与其中全部行视图。</summary>
    /// <param name="group">共享元数据分组。</param>
    /// <returns>该分组的原生视图集合。</returns>
    private GroupBlock BuildGroup(MetadataGroup group)
    {
        var header = SingleLineLabel(group.Name, GroupHeaderFont, GroupHeaderColor);
        var card = new NSBox
        {
            BoxType = NSBoxType.NSBoxCustom, TitlePosition = NSTitlePosition.NoTitle,
            BorderColor = NSColor.Separator,
            FillColor = NSColor.ControlBackground, CornerRadius = 6,
            ContentViewMargins = CGSize.Empty, ContentView = new FlippedPane(),
        };
        _built.Add(header);
        _built.Add(card);
        _document.AddSubview(header);
        _document.AddSubview(card);

        var rows = new List<TagRow>(group.Tags.Count);
        foreach (var tag in group.Tags)
        {
            var row = BuildRow((NSView)card.ContentView!, tag);
            rows.Add(row);
        }
        return new GroupBlock { Header = header, Card = card, Rows = rows };
    }

    /// <summary>建立单条元数据的 7 列行：ID 单元格、三条列分隔线、名称/中文名/值与行分隔线。</summary>
    /// <param name="card">所属卡片容器。</param>
    /// <param name="tag">共享元数据标签。</param>
    /// <returns>该行的原生视图集合。</returns>
    private TagRow BuildRow(NSView card, MetadataTag tag)
    {
        var idCell = new FlippedPane();
        var idText = SingleLineLabel(tag.TagIdHex, IdFont, IdTextColor);
        idText.Alignment = NSTextAlignment.Center;
        var name = WrappedLabel(tag.Name, CellFont, NameColor, 0);
        var chinese = WrappedLabel(tag.ChineseName ?? "", CellFont, ChineseColor, 0);
        var value = WrappedLabel(tag.Value, CellFont, ValueColor, ValueMaxLines);
        var dividers = new[] { Divider(), Divider(), Divider() };
        var rowDivider = Divider();

        card.AddSubview(idCell);
        idCell.AddSubview(idText);
        card.AddSubview(rowDivider);
        card.AddSubview(name);
        card.AddSubview(chinese);
        card.AddSubview(value);
        foreach (var divider in dividers) card.AddSubview(divider);

        var row = new TagRow { IdCell = idCell, IdText = idText, Dividers = dividers, Name = name, Chinese = chinese, Value = value, RowDivider = rowDivider };
        _built.Add(idCell);
        _built.Add(idText);
        _built.Add(name);
        _built.Add(chinese);
        _built.Add(value);
        foreach (var divider in dividers) _built.Add(divider);
        _built.Add(rowDivider);
        return row;
    }

    /// <summary>按当前宽度重排文件名行、加载提示与全部分组卡片，并刷新文档高度。</summary>
    private void Relayout()
    {
        double width = Math.Max(1, (double)_scroll.ContentView.Bounds.Width);
        double available = Math.Max(1, width - SideMargin * 2);
        _loading.Hidden = !(_viewModel?.IsLoading ?? false);

        double fileNameWidth = MeasureWidth(_fileName.StringValue, FileNameFont);
        var formatSizes = FormatSizes();
        double formatWidth = formatSizes.Sum()
            + (_formatButtons.Count > 1 ? FormatSpacing * (_formatButtons.Count - 1) : 0);
        double spacing = formatWidth > 0 ? HeaderSpacing : 0;
        double groupWidth = Math.Min(fileNameWidth + spacing + formatWidth, available);
        double nameWidth = Math.Max(1, groupWidth - spacing - formatWidth);
        double start = Math.Max(SideMargin, (width - groupWidth) / 2);
        double lineHeight = LineHeight(FileNameFont);
        double formatHeight = LineHeight(FormatFont) + FormatPaddingY * 2;
        double headerHeight = Math.Max(lineHeight, formatHeight);
        double headerTop = HeaderTopMargin;

        _fileName.Frame = new CGRect(start, headerTop + (headerHeight - lineHeight) / 2, nameWidth, lineHeight);
        double x = start + nameWidth + spacing;
        for (int index = 0; index < _formatButtons.Count; index++)
        {
            double size = formatSizes[index];
            _formatButtons[index].Frame = new CGRect(x, headerTop + (headerHeight - formatHeight) / 2, size, formatHeight);
            x += size + FormatSpacing;
        }
        if (_formatButtons.Count == 0)
        {
            double labelWidth = Math.Max(1, Math.Min(formatWidth, width - x - SideMargin));
            _formatLabel.Frame = new CGRect(x, headerTop + (headerHeight - lineHeight) / 2, labelWidth, lineHeight);
        }
        double y = headerTop + headerHeight + HeaderBottomMargin;

        double loadingHeight = LineHeight(FormatFont);
        if (!_loading.Hidden)
        {
            y += LoadingMargin;
            _loading.Frame = new CGRect(SideMargin, y, available, loadingHeight);
            y += loadingHeight + LoadingMargin;
        }

        foreach (var block in _groups)
        {
            y += GroupHeaderTopMargin;
            double groupHeaderHeight = LineHeight(GroupHeaderFont);
            block.Header.Frame = new CGRect(SideMargin, y, available, groupHeaderHeight);
            y += groupHeaderHeight + GroupHeaderBottomMargin + CardTopMargin;
            double cardHeight = RelayoutCard(block, available);
            block.Card.Frame = new CGRect(SideMargin, y, available, cardHeight);
            y += cardHeight + CardBottomMargin;
        }

        _document.Frame = new CGRect(0, 0, width, Math.Max(1, y + BottomMargin));
    }

    /// <summary>按 2:2:3 列宽计算每条行的换行高度并摆放全部单元格（行坐标以卡片左上角为原点），返回卡片总高。</summary>
    /// <param name="block">目标分组。</param>
    /// <param name="cardWidth">卡片宽度。</param>
    /// <returns>卡片高度。</returns>
    private static double RelayoutCard(GroupBlock block, double cardWidth)
    {
        var columns = ExifTableGeometry.Compute(cardWidth);
        double nameColumn = columns.NameColumn;
        double chineseColumn = columns.ChineseColumn;
        double nameTextWidth = columns.NameTextWidth;
        double chineseTextWidth = columns.ChineseTextWidth;
        double valueTextWidth = columns.ValueTextWidth;
        double y = 0;
        foreach (var row in block.Rows)
        {
            double nameHeight = MeasureHeight(row.Name, nameTextWidth, 0);
            double chineseHeight = MeasureHeight(row.Chinese, chineseTextWidth, 0);
            double valueHeight = MeasureHeight(row.Value, valueTextWidth, ValueMaxLines);
            double rowHeight = Math.Max(nameHeight, Math.Max(chineseHeight, valueHeight)) + CellPaddingY * 2;
            double idHeight = MeasureHeight(row.IdText, IdColumnWidth, 1);

            row.IdCell.Frame = new CGRect(0, y, IdColumnWidth, rowHeight);
            row.IdText.Frame = new CGRect(0, (rowHeight - idHeight) / 2, IdColumnWidth, idHeight);
            double x = IdColumnWidth;
            row.Dividers[0].Frame = new CGRect(x, y, DividerThickness, rowHeight);
            x += DividerThickness;
            row.Name.Frame = new CGRect(x + CellPaddingX, y + CellPaddingY, nameTextWidth, nameHeight);
            x += nameColumn;
            row.Dividers[1].Frame = new CGRect(x, y, DividerThickness, rowHeight);
            x += DividerThickness;
            row.Chinese.Frame = new CGRect(x + CellPaddingX, y + CellPaddingY, chineseTextWidth, chineseHeight);
            x += chineseColumn;
            row.Dividers[2].Frame = new CGRect(x, y, DividerThickness, rowHeight);
            x += DividerThickness;
            row.Value.Frame = new CGRect(x + CellPaddingX, y + CellPaddingY, valueTextWidth, valueHeight);
            row.RowDivider.Frame = new CGRect(0, y + rowHeight, cardWidth, DividerThickness);
            y += rowHeight + DividerThickness;
        }
        return y;
    }

    /// <summary>格式切换的逐项宽度（多格式时为按钮宽度含左右内边距，单格式时为标签宽度）。</summary>
    /// <returns>按显示顺序排列的宽度数组。</returns>
    private double[] FormatSizes()
    {
        if (_formatButtons.Count == 0)
            return _formatLabel.Hidden ? [] : [MeasureWidth(_formatLabel.StringValue, FormatFont)];
        var sizes = new double[_formatButtons.Count];
        for (int index = 0; index < _formatButtons.Count; index++)
            sizes[index] = MeasureWidth(_formatButtons[index].Title, FormatFont) + FormatPaddingX * 2;
        return sizes;
    }

    /// <summary>释放全部动态内容视图（格式按钮、分组标题、卡片及其行），保留三个常驻标签。</summary>
    private void ClearBuilt()
    {
        for (int index = _built.Count - 1; index >= 0; index--)
        {
            _built[index].RemoveFromSuperview();
            _built[index].Dispose();
        }
        _built.Clear();
        _groups.Clear();
        _formatButtons.Clear();
    }

    /// <summary>单行文本的自然宽度。</summary>
    /// <param name="text">目标文本。</param>
    /// <param name="font">测量字体。</param>
    /// <returns>宽度（点）。</returns>
    private double MeasureWidth(string text, NSFont font)
    {
        _measure.Font = font;
        _measure.StringValue = text ?? string.Empty;
        var size = _measure.Cell?.CellSize ?? CGSize.Empty;
        return (double)size.Width;
    }

    /// <summary>量出文本在给定宽度下换行后的高度；<paramref name="maxLines"/> 为 0 时不限行数。</summary>
    /// <param name="field">持有文本与换行设置的标签。</param>
    /// <param name="width">可用宽度。</param>
    /// <param name="maxLines">最多显示行数。</param>
    /// <returns>文本高度（点）。</returns>
    private static double MeasureHeight(NSTextField field, double width, int maxLines)
    {
        var font = field.Font ?? CellFont;
        double lineHeight = LineHeight(font);
        var cell = field.Cell;
        if (cell == null || width <= 0) return lineHeight;
        double height = (double)cell.CellSizeForBounds(new CGRect(0, 0, width, MeasureLimit)).Height;
        if (maxLines > 0) height = Math.Min(height, lineHeight * maxLines);
        return Math.Max(1, Math.Ceiling(height));
    }

    /// <summary>字体的单行高度（上升部+下降部+行距）。</summary>
    /// <param name="font">目标字体。</param>
    /// <returns>行高（点）。</returns>
    private static double LineHeight(NSFont font)
        => Math.Ceiling((double)font.Ascender - (double)font.Descender + (double)font.Leading);

    /// <summary>创建单行只读标签（尾部截断）。</summary>
    /// <param name="text">初始文本。</param>
    /// <param name="font">字体。</param>
    /// <param name="color">文字颜色。</param>
    /// <returns>原生标签。</returns>
    private static NSTextField SingleLineLabel(string text, NSFont font, NSColor color)
    {
        var field = Label(text, font, color, NSLineBreakMode.TruncatingTail);
        var cell = field.Cell;
        if (cell != null) cell.Wraps = false;
        return field;
    }

    /// <summary>创建多行只读标签（按词换行，超出最多行数的尾部截断）。</summary>
    /// <param name="text">初始文本。</param>
    /// <param name="font">字体。</param>
    /// <param name="color">文字颜色。</param>
    /// <param name="maxLines">最多显示行数；0 表示不限。</param>
    /// <returns>原生标签。</returns>
    private static NSTextField WrappedLabel(string text, NSFont font, NSColor color, int maxLines)
    {
        var lineBreak = maxLines > 0 ? NSLineBreakMode.TruncatingTail : NSLineBreakMode.ByWordWrapping;
        var field = Label(text, font, color, lineBreak);
        var cell = field.Cell;
        if (cell != null) cell.Wraps = true;
        if (maxLines > 0) field.MaximumNumberOfLines = maxLines;
        return field;
    }

    /// <summary>创建可选中但不接受编辑的原生标签。</summary>
    /// <param name="text">初始文本。</param>
    /// <param name="font">字体。</param>
    /// <param name="color">文字颜色。</param>
    /// <param name="lineBreakMode">换行模式。</param>
    /// <returns>原生标签。</returns>
    private static NSTextField Label(string text, NSFont font, NSColor color, NSLineBreakMode lineBreakMode)
        => new(CGRect.Empty)
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

    /// <summary>创建 1 点宽的分隔线（颜色 #3a3a3a）。</summary>
    /// <returns>原生分隔线视图。</returns>
    private static NSView Divider() => new NSBox { BoxType = NSBoxType.NSBoxSeparator };

    /// <summary>解除订阅并释放全部原生视图。</summary>
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
            ClearBuilt();
            _scroll.Dispose();
            _document.Dispose();
            _fileName.Dispose();
            _formatLabel.Dispose();
            _loading.Dispose();
            _measure.Dispose();
        }
        base.Dispose(disposing);
    }

    /// <summary>分组标题、圆角卡片与其中全部行视图。</summary>
    private sealed class GroupBlock
    {
        /// <summary>分组标题标签。</summary>
        public required NSTextField Header { get; init; }

        /// <summary>背景 #2a2a2a、圆角 6 的卡片容器。</summary>
        public required NSView Card { get; init; }

        /// <summary>该分组内的全部元数据行。</summary>
        public required List<TagRow> Rows { get; init; }
    }

    /// <summary>单条元数据的 7 列行视图。</summary>
    private sealed class TagRow
    {
        /// <summary>Tag ID 单元格（背景 #222）。</summary>
        public required NSView IdCell { get; init; }

        /// <summary>Tag ID 文本（等宽 11、居中）。</summary>
        public required NSTextField IdText { get; init; }

        /// <summary>三条列分隔线，顺序为 ID 后、名称后、中文名后。</summary>
        public required NSView[] Dividers { get; init; }

        /// <summary>英文名称标签。</summary>
        public required NSTextField Name { get; init; }

        /// <summary>中文译名标签。</summary>
        public required NSTextField Chinese { get; init; }

        /// <summary>值标签（最多三行）。</summary>
        public required NSTextField Value { get; init; }

        /// <summary>行底部分隔线。</summary>
        public required NSView RowDivider { get; init; }
    }

    private sealed class FormatButton : NSButton
    {
        public FormatButton(string title, bool selected, Action clicked) : base(CGRect.Empty)
        {
            Title = title;
            BezelStyle = NSBezelStyle.TexturedRounded;
            Font = FormatFont;
            SetButtonType(NSButtonType.PushOnPushOff);
            State = selected ? NSCellStateValue.On : NSCellStateValue.Off;
            ((NSView)this).AccessibilityLabel = title;
            Activated += (_, _) => clicked();
        }
    }

    /// <summary>顶边为原点的容器。</summary>
    private sealed class FlippedPane : NSView
    {
        /// <summary>布局坐标自顶向下。</summary>
        public override bool IsFlipped => true;
    }
}
