using AppKit;
using CoreGraphics;
using Foundation;
using PhotoViewer.Core.AI;
using PhotoViewer.Core.Image;

namespace PhotoViewer.Mac.Native;

/// <summary>
/// 原生 DINO 诊断页：与旧壳 <c>DinoDebugView</c> 同构 —— 标题与状态文本、DINO patch 三列瓦片
/// （原图缩略图 / PCA-RGB / Cosine 相似度）、CV 网格三列瓦片（锐度 / 抖动拖影矢量 / 刚体拟合文本面板）。
/// 全部数据来自共享 <see cref="DinoDiagnosticPresenter"/>（读库快路径与推理都在那里完成），
/// 本视图只做位图适配与点击回传，绝不自行驱动推理。
/// </summary>
internal sealed class MacDinoDiagnosticView : NSView
{
    /// <summary>页面左右外边距（旧壳 StackPanel Margin 16）。</summary>
    private const double PageMarginX = 16;

    /// <summary>页面上外边距（旧壳 StackPanel Margin 16）。</summary>
    private const double PageMarginTop = 16;

    /// <summary>页面下外边距（旧壳 StackPanel Margin 0,0,0,24）。</summary>
    private const double PageMarginBottom = 24;

    /// <summary>标题下外边距（旧壳 Margin 0,0,0,12）。</summary>
    private const double TitleBottomMargin = 12;

    /// <summary>状态文本下外边距（旧壳 Margin 0,0,0,16）。</summary>
    private const double StatusBottomMargin = 16;

    /// <summary>栏目标题下外边距（旧壳 Margin 0,0,0,6）。</summary>
    private const double SectionHeaderBottomMargin = 6;

    /// <summary>两行瓦片网格之间的间距（旧壳 UniformGrid Margin 0,0,0,20）。</summary>
    private const double GridGap = 20;

    /// <summary>单个瓦片四周外边距（旧壳 Margin 4）。</summary>
    private const double TileMargin = 4;

    /// <summary>瓦片列数（旧壳 UniformGrid Columns 3）。</summary>
    private const int TileColumns = 3;

    /// <summary>刚体拟合面板的行间距（旧壳 StackPanel Spacing 6）。</summary>
    private const double PanelSpacing = 6;

    /// <summary>刚体拟合多行文本的上外边距（旧壳 Margin 0,4,0,0）。</summary>
    private const double RigidTextTopMargin = 4;

    /// <summary>刚体拟合多行文本的行高（旧壳 LineHeight 20）。</summary>
    private const double RigidLineHeight = 20;

    /// <summary>测量换行高度的宽度上限。</summary>
    private const double MeasureLimit = 10000;

    /// <summary>标题颜色 #EEE。</summary>
    private static readonly NSColor TitleColor = NSColor.FromRgb(0xEE, 0xEE, 0xEE);

    /// <summary>状态文本颜色 #888。</summary>
    private static readonly NSColor StatusColor = NSColor.FromRgb(0x88, 0x88, 0x88);

    /// <summary>栏目标题颜色 #AAA。</summary>
    private static readonly NSColor SectionColor = NSColor.FromRgb(0xAA, 0xAA, 0xAA);

    /// <summary>面板标题颜色 #CCC。</summary>
    private static readonly NSColor PanelTitleColor = NSColor.FromRgb(0xCC, 0xCC, 0xCC);

    /// <summary>面板量纲说明颜色 #666。</summary>
    private static readonly NSColor PanelMetricsColor = NSColor.FromRgb(0x66, 0x66, 0x66);

    /// <summary>刚体拟合文本颜色 #DDD。</summary>
    private static readonly NSColor RigidTextColor = NSColor.FromRgb(0xDD, 0xDD, 0xDD);

    /// <summary>标题 16 点半粗字体。</summary>
    private static readonly NSFont TitleFont = NSFont.SystemFontOfSize(16, NSFontWeight.Semibold) ?? throw new InvalidOperationException("系统字体不可用");

    /// <summary>状态文本 12 点字体。</summary>
    private static readonly NSFont StatusFont = NSFont.SystemFontOfSize(12) ?? throw new InvalidOperationException("系统字体不可用");

    /// <summary>栏目标题 13 点中粗字体。</summary>
    private static readonly NSFont SectionFont = NSFont.SystemFontOfSize(13, NSFontWeight.Medium) ?? throw new InvalidOperationException("系统字体不可用");

    /// <summary>面板标题 13 点中粗字体。</summary>
    private static readonly NSFont PanelTitleFont = NSFont.SystemFontOfSize(13, NSFontWeight.Medium) ?? throw new InvalidOperationException("系统字体不可用");

    /// <summary>面板量纲说明 11 点字体。</summary>
    private static readonly NSFont PanelMetricsFont = NSFont.SystemFontOfSize(11) ?? throw new InvalidOperationException("系统字体不可用");

    /// <summary>刚体拟合文本 12 点等宽字体。</summary>
    private static readonly NSFont RigidTextFont = NSFont.UserFixedPitchFontOfSize(12) ?? throw new InvalidOperationException("系统字体不可用");

    private readonly DinoDiagnosticPresenter _presenter;
    private readonly NSScrollView _scroll;
    private readonly DocumentView _document;
    private readonly NSTextField _title = SingleLineLabel("DINO 诊断", TitleFont, TitleColor);
    private readonly NSTextField _status = SingleLineLabel("", StatusFont, StatusColor);
    private readonly NSTextField _patchHeader = SingleLineLabel("DINO patch (32×32，点击任意图设定参考点)", SectionFont, SectionColor);
    private readonly NSTextField _cvHeader = SingleLineLabel("CV 网格诊断（32×32 中心采样，像素单位）", SectionFont, SectionColor);
    private readonly NSTextField _rigidTitle = SingleLineLabel("抖动分析（刚体拟合）", PanelTitleFont, PanelTitleColor);
    private readonly NSTextField _rigidMetrics = SingleLineLabel("|T| / |ω| / residual", PanelMetricsFont, PanelMetricsColor);
    private readonly NSTextField _rigidText = WrappedLabel("", RigidTextFont, RigidTextColor);
    private readonly MacDiagnosticTile _thumbnailTile = Tile();
    private readonly MacDiagnosticTile _pcaTile = Tile();
    private readonly MacDiagnosticTile _cosineTile = Tile();
    private readonly MacDiagnosticTile _sharpnessTile = Tile();
    private readonly MacDiagnosticTile _shakeTile = Tile();
    private readonly MacShakeFieldView _shakeOverlay = new();
    private readonly Dictionary<AnalysisTile, TileImages> _images = [];
    private readonly List<NSImage> _stale = [];
    private string? _rigidSource;
    private bool _disposed;

    /// <summary>建立滚动容器、五个诊断瓦片与刚体拟合文本面板，并订阅 presenter 变化。</summary>
    /// <param name="presenter">共享 DINO 诊断 presenter（由工具壳持有）。</param>
    public MacDinoDiagnosticView(DinoDiagnosticPresenter presenter)
    {
        _presenter = presenter;
        _document = new DocumentView(presenter.ClearCrosshair);
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
        foreach (var view in new NSView[] { _title, _status, _patchHeader, _cvHeader, _rigidTitle, _rigidMetrics, _rigidText,
            _thumbnailTile, _pcaTile, _cosineTile, _sharpnessTile, _shakeTile })
            _document.AddSubview(view);
        _shakeTile.Overlay = _shakeOverlay;
        foreach (var tile in new[] { _thumbnailTile, _pcaTile, _cosineTile, _sharpnessTile, _shakeTile })
            tile.TileClicked = presenter.OnTileClicked;
        presenter.Changed += Sync;
        presenter.CrosshairChanged += Sync;
        Sync();
    }

    /// <summary>把 presenter 的瓦片状态同步到原生控件并重排；位图按引用比较，未变化不重编码。</summary>
    public void Sync()
    {
        if (_disposed) return;
        _status.StringValue = _presenter.Status;
        SyncRigidText(_presenter.RigidMotionText);
        SyncTile(_thumbnailTile, _presenter.ThumbnailTile);
        SyncTile(_pcaTile, _presenter.PcaTile);
        SyncTile(_cosineTile, _presenter.CosineTile);
        SyncTile(_sharpnessTile, _presenter.SharpnessTile);
        SyncTile(_shakeTile, _presenter.ShakeTile);
        _shakeOverlay.ShakeField = _presenter.ShakeTile.ShakeField;
        foreach (var image in _stale) image.Dispose();
        _stale.Clear();
        Relayout();
    }

    /// <summary>尺寸变化时把滚动容器铺满并重排文档内容。</summary>
    public override void SetFrameSize(CGSize newSize)
    {
        base.SetFrameSize(newSize);
        _scroll.Frame = Bounds;
        Relayout();
    }

    /// <summary>同步单张瓦片的位图、说明、占位文字与全局十字准星。</summary>
    /// <param name="tile">原生瓦片。</param>
    /// <param name="source">presenter 侧的瓦片状态。</param>
    private void SyncTile(MacDiagnosticTile tile, AnalysisTile source)
    {
        var images = _images.TryGetValue(source, out var cached) ? cached : _images[source] = new TileImages();
        tile.AspectRatio = _presenter.AspectRatio;
        tile.Label = source.Caption;
        tile.PlaceholderText = source.Placeholder;
        tile.Source = ImageFor(images, source.Source, false);
        tile.BackgroundSource = ImageFor(images, source.Background, true);
        tile.SetCrosshair(_presenter.CrosshairU, _presenter.CrosshairV);
    }

    /// <summary>按 <see cref="DecodedImage"/> 引用取原生位图；源变化时重编码并登记旧图待释放。</summary>
    /// <param name="images">该瓦片的位图缓存。</param>
    /// <param name="key">当前源位图；null 表示清空。</param>
    /// <param name="background">是否是压暗背景层。</param>
    /// <returns>调用方引用的原生位图。</returns>
    private NSImage? ImageFor(TileImages images, DecodedImage? key, bool background)
    {
        var previousKey = background ? images.Background : images.Source;
        var previousImage = background ? images.BackgroundImage : images.SourceImage;
        if (ReferenceEquals(previousKey, key)) return previousImage;
        var image = key == null ? null : MacSymbols.CreateThumbnail(key);
        if (background)
        {
            images.Background = key;
            images.BackgroundImage = image;
        }
        else
        {
            images.Source = key;
            images.SourceImage = image;
        }
        if (previousImage != null) _stale.Add(previousImage);
        return image;
    }

    /// <summary>更新刚体拟合多行文本；文本未变时保留带 20 点行高的属性串。</summary>
    /// <param name="text">presenter 给出的多行判定文本。</param>
    private void SyncRigidText(string text)
    {
        if (_rigidSource == text) return;
        _rigidSource = text;
        using var paragraph = new NSMutableParagraphStyle
        {
            LineBreakMode = NSLineBreakMode.ByWordWrapping,
            MinimumLineHeight = (nfloat)RigidLineHeight,
            MaximumLineHeight = (nfloat)RigidLineHeight,
        };
        using var attributes = new NSDictionary(
            NSStringAttributeKey.Font, RigidTextFont,
            NSStringAttributeKey.ForegroundColor, RigidTextColor,
            NSStringAttributeKey.ParagraphStyle, paragraph);
        _rigidText.AttributedStringValue = new NSAttributedString(text ?? string.Empty, attributes);
    }

    /// <summary>按当前宽度重排标题、状态、两行瓦片网格与刚体拟合面板，并刷新文档高度。</summary>
    private void Relayout()
    {
        double width = Math.Max(1, (double)_scroll.ContentView.Bounds.Width);
        double contentWidth = Math.Max(1, width - PageMarginX * 2);
        double cell = contentWidth / TileColumns;
        double tileWidth = Math.Max(1, cell - TileMargin * 2);

        double y = PageMarginTop;
        _title.Frame = new CGRect(PageMarginX, y, contentWidth, LineHeight(TitleFont));
        y += LineHeight(TitleFont) + TitleBottomMargin;
        _status.Frame = new CGRect(PageMarginX, y, contentWidth, LineHeight(StatusFont));
        y += LineHeight(StatusFont) + StatusBottomMargin;
        _patchHeader.Frame = new CGRect(PageMarginX, y, contentWidth, LineHeight(SectionFont));
        y += LineHeight(SectionFont) + SectionHeaderBottomMargin;

        double rowTop = y;
        y = rowTop + PlaceTiles(rowTop, cell, tileWidth, _thumbnailTile, _pcaTile, _cosineTile) + GridGap;
        _cvHeader.Frame = new CGRect(PageMarginX, y, contentWidth, LineHeight(SectionFont));
        y += LineHeight(SectionFont) + SectionHeaderBottomMargin;

        rowTop = y;
        double tileRowHeight = PlaceTiles(rowTop, cell, tileWidth, _sharpnessTile, _shakeTile, null);
        double panelBottom = PlaceRigidPanel(rowTop, cell);
        y = rowTop + Math.Max(tileRowHeight, panelBottom - rowTop);
        _document.Frame = new CGRect(0, 0, width, Math.Max(1, y + PageMarginBottom));
    }

    /// <summary>把瓦片按等宽三列摆放（第三列可为空），返回该行高度（含上下外边距）。</summary>
    /// <param name="top">行顶边纵坐标。</param>
    /// <param name="cell">单列宽度。</param>
    /// <param name="tileWidth">瓦片宽度。</param>
    /// <param name="first">第一列瓦片。</param>
    /// <param name="second">第二列瓦片。</param>
    /// <param name="third">第三列瓦片；null 表示该格由调用方另排（刚体拟合面板）。</param>
    /// <returns>行高度。</returns>
    private static double PlaceTiles(double top, double cell, double tileWidth, MacDiagnosticTile first, MacDiagnosticTile second, MacDiagnosticTile? third)
    {
        double height = 0;
        Place(first, 0);
        Place(second, 1);
        if (third != null) Place(third, 2);
        return height;

        void Place(MacDiagnosticTile tile, int column)
        {
            double tileHeight = tile.RequiredHeight(tileWidth);
            tile.Frame = new CGRect(PageMarginX + column * cell + TileMargin, top + TileMargin, tileWidth, tileHeight);
            height = Math.Max(height, tileHeight + TileMargin * 2);
        }
    }

    /// <summary>摆放第三列的刚体拟合文本面板（标题、量纲说明与多行判定文本），返回面板底边纵坐标。</summary>
    /// <param name="top">行顶边纵坐标。</param>
    /// <param name="cell">单列宽度。</param>
    /// <returns>面板底边纵坐标。</returns>
    private double PlaceRigidPanel(double top, double cell)
    {
        double panelX = PageMarginX + cell * 2 + TileMargin;
        double panelWidth = Math.Max(1, cell - TileMargin * 2);
        double y = top + TileMargin;
        _rigidTitle.Frame = new CGRect(panelX, y, panelWidth, LineHeight(PanelTitleFont));
        y += LineHeight(PanelTitleFont) + PanelSpacing;
        _rigidMetrics.Frame = new CGRect(panelX, y, panelWidth, LineHeight(PanelMetricsFont));
        y += LineHeight(PanelMetricsFont) + PanelSpacing + RigidTextTopMargin;
        double textHeight = MeasureRigidText(panelWidth);
        _rigidText.Frame = new CGRect(panelX, y, panelWidth, textHeight);
        return y + textHeight + TileMargin;
    }

    /// <summary>量出刚体拟合文本在给定宽度下的高度（按 20 点行高，至少覆盖全部显式换行）。</summary>
    /// <param name="width">可用宽度。</param>
    /// <returns>文本高度（点）。</returns>
    private double MeasureRigidText(double width)
    {
        var cell = _rigidText.Cell;
        double measured = cell == null ? 0 : (double)cell.CellSizeForBounds(new CGRect(0, 0, width, MeasureLimit)).Height;
        int lines = 1;
        foreach (char character in _rigidSource ?? "") if (character == '\n') lines++;
        return Math.Max(measured, lines * RigidLineHeight);
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
        => new(CGRect.Empty)
        {
            StringValue = text,
            Editable = false,
            Selectable = true,
            Bordered = false,
            DrawsBackground = false,
            Font = font,
            TextColor = color,
            LineBreakMode = NSLineBreakMode.TruncatingTail,
        };

    /// <summary>创建刚体拟合文本标签（按词换行、不限行数）。</summary>
    /// <param name="text">初始文本。</param>
    /// <param name="font">字体。</param>
    /// <param name="color">文字颜色。</param>
    /// <returns>原生标签。</returns>
    private static NSTextField WrappedLabel(string text, NSFont font, NSColor color)
    {
        var field = new NSTextField(CGRect.Empty)
        {
            StringValue = text,
            Editable = false,
            Selectable = true,
            Bordered = false,
            DrawsBackground = false,
            Font = font,
            TextColor = color,
            LineBreakMode = NSLineBreakMode.ByWordWrapping,
        };
        var cell = field.Cell;
        if (cell != null) cell.Wraps = true;
        return field;
    }

    /// <summary>创建空说明的诊断瓦片（说明与内容随后由 presenter 状态填充）。</summary>
    /// <returns>原生瓦片。</returns>
    private static MacDiagnosticTile Tile() => new();

    /// <summary>解除 presenter 订阅并释放位图与原生视图。</summary>
    /// <param name="disposing">是否由显式释放触发。</param>
    protected override void Dispose(bool disposing)
    {
        if (disposing && !_disposed)
        {
            _disposed = true;
            _presenter.Changed -= Sync;
            _presenter.CrosshairChanged -= Sync;
            foreach (var images in _images.Values)
            {
                images.SourceImage?.Dispose();
                images.BackgroundImage?.Dispose();
            }
            _images.Clear();
            foreach (var image in _stale) image.Dispose();
            _stale.Clear();
            _shakeTile.Overlay = null;
            _shakeOverlay.Dispose();
            _scroll.Dispose();
            _document.Dispose();
        }
        base.Dispose(disposing);
    }

    /// <summary>单张瓦片当前的主图与背景位图缓存。</summary>
    private sealed class TileImages
    {
        /// <summary>当前主图源（<see cref="AnalysisTile.Source"/>）。</summary>
        public DecodedImage? Source { get; set; }

        /// <summary>主图对应的原生位图。</summary>
        public NSImage? SourceImage { get; set; }

        /// <summary>当前压暗背景源（<see cref="AnalysisTile.Background"/>）。</summary>
        public DecodedImage? Background { get; set; }

        /// <summary>背景对应的原生位图。</summary>
        public NSImage? BackgroundImage { get; set; }
    }

    /// <summary>顶边为原点的滚动内容容器；空白处点击清空诊断准星。</summary>
    private sealed class DocumentView(Action clearCrosshair) : NSView
    {
        /// <summary>布局坐标自顶向下。</summary>
        public override bool IsFlipped => true;

        /// <summary>点击内容空白处（瓦片会自行消费自身范围内的点击）时清空准星。</summary>
        public override void MouseDown(NSEvent theEvent) => clearCrosshair();
    }
}
