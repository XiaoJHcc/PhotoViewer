using CoreGraphics;
using Foundation;
using PhotoViewer.Core.AI;
using PhotoViewer.Core.Image;
using UIKit;

namespace PhotoViewer.iOS.Native;

/// <summary>
/// 原生 DINO 诊断页（UIKit 版）：与旧壳 <c>DinoDebugView</c>/Mac 原生页同构 —— 标题与状态文本、
/// DINO patch 三列瓦片（原图缩略图 / PCA-RGB / Cosine 相似度）、CV 网格三列瓦片
/// （锐度 / 抖动拖影矢量，第六格留空），刚体拟合文本以整宽块放在网格下方。
/// 全部数据来自共享 <see cref="DinoDiagnosticPresenter"/>（读库快路径与推理都在那里完成），
/// 本视图只做位图适配与点击回传，绝不自行驱动推理。
/// </summary>
internal sealed class iOSDinoDiagnosticView : UIViewController
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

    private static readonly UIColor TitleColor = UIColor.Label;
    private static readonly UIColor StatusColor = UIColor.SecondaryLabel;
    private static readonly UIColor SectionColor = UIColor.SecondaryLabel;
    private static readonly UIColor PanelTitleColor = UIColor.Label;
    private static readonly UIColor PanelMetricsColor = UIColor.SecondaryLabel;
    private static readonly UIFont TitleFont = UIFont.PreferredHeadline!;
    private static readonly UIFont StatusFont = UIFont.PreferredFootnote!;
    private static readonly UIFont SectionFont = UIFont.PreferredSubheadline!;
    private static readonly UIFont PanelMetricsFont = UIFont.PreferredCaption1!;
    private static readonly UIFont RigidTextFont = UIFont.FromName("Menlo", 12) ?? UIFont.SystemFontOfSize(12)!;
    private static readonly UIColor RigidTextColor = UIColor.Label;

    private readonly DinoDiagnosticPresenter _presenter;
    private readonly UIScrollView _scroll;
    private readonly DocumentView _document;
    private readonly UILabel _title = SingleLineLabel("DINO 诊断", TitleFont, TitleColor);
    private readonly UILabel _status = SingleLineLabel("", StatusFont, StatusColor);
    private readonly UILabel _patchHeader = SingleLineLabel("DINO patch (32×32，点击任意图设定参考点)", SectionFont, SectionColor);
    private readonly UILabel _cvHeader = SingleLineLabel("CV 网格诊断（32×32 中心采样，像素单位）", SectionFont, SectionColor);
    private readonly UILabel _rigidTitle = SingleLineLabel("抖动分析（刚体拟合）", SectionFont, PanelTitleColor);
    private readonly UILabel _rigidMetrics = SingleLineLabel("|T| / |ω| / residual", PanelMetricsFont, PanelMetricsColor);
    private readonly UITextView _rigidText = new()
    {
        Editable = false, Selectable = true, ScrollEnabled = false,
        BackgroundColor = UIColor.SecondarySystemGroupedBackground,
        Font = RigidTextFont, TextColor = UIColor.Label,
        TextContainerInset = new UIEdgeInsets(12, 12, 12, 12),
    };
    private readonly iOSDiagnosticTile _thumbnailTile = new();
    private readonly iOSDiagnosticTile _pcaTile = new();
    private readonly iOSDiagnosticTile _cosineTile = new();
    private readonly iOSDiagnosticTile _sharpnessTile = new();
    private readonly iOSDiagnosticTile _shakeTile = new();
    private readonly iOSShakeFieldView _shakeOverlay = new();
    private readonly Dictionary<AnalysisTile, TileImages> _images = [];
    private readonly List<UIImage> _stale = [];
    private string? _rigidSource;
    private bool _disposed;

    /// <summary>页面上的五张诊断瓦片（验收读取数量与内容）。</summary>
    internal IReadOnlyList<iOSDiagnosticTile> Tiles => [_thumbnailTile, _pcaTile, _cosineTile, _sharpnessTile, _shakeTile];

    /// <summary>共享 DINO 诊断 presenter（导航栏与验收读取状态）。</summary>
    internal DinoDiagnosticPresenter Presenter => _presenter;

    /// <summary>刚体拟合标题块（验收核对整宽块位于网格下方，TOOLS-05）。</summary>
    internal CGRect RigidTitleFrame => _rigidTitle.Frame;

    /// <summary>第二行瓦片底边（验收核对刚体拟合块不挤进第六格）。</summary>
    internal double SecondRowBottom => Math.Max(_sharpnessTile.Frame.GetMaxY(), _shakeTile.Frame.GetMaxY());

    /// <summary>建立滚动容器、五个诊断瓦片与刚体拟合文本面板，并订阅 presenter 变化。</summary>
    /// <param name="presenter">共享 DINO 诊断 presenter（由工具壳持有）。</param>
    internal iOSDinoDiagnosticView(DinoDiagnosticPresenter presenter) : base(null, null)
    {
        _presenter = presenter;
        Title = "DINO 诊断";
        OverrideUserInterfaceStyle = UIUserInterfaceStyle.Dark;
        var groupedBackground = UIColor.SystemGroupedBackground.GetResolvedColor(UITraitCollection.FromUserInterfaceStyle(UIUserInterfaceStyle.Dark));
        View!.BackgroundColor = groupedBackground;
        _scroll = new UIScrollView { BackgroundColor = groupedBackground, AlwaysBounceVertical = true };
        _rigidText.Layer.CornerRadius = 12;
        _scroll.ContentInsetAdjustmentBehavior = UIScrollViewContentInsetAdjustmentBehavior.Never;
        _document = new DocumentView(presenter.ClearCrosshair);
        _scroll.AddSubview(_document);
        View!.AddSubview(_scroll);
        foreach (var view in new UIView[] { _title, _status, _patchHeader, _cvHeader, _rigidTitle, _rigidMetrics, _rigidText,
            _thumbnailTile, _pcaTile, _cosineTile, _sharpnessTile, _shakeTile })
            _document.AddSubview(view);
        _shakeTile.Overlay = _shakeOverlay;
        foreach (var tile in Tiles)
        {
            tile.TileClicked = presenter.OnTileClicked;
            tile.BlankClicked = presenter.ClearCrosshair;
        }
        presenter.Changed += Sync;
        presenter.CrosshairChanged += Sync;
        Sync();
    }

    /// <summary>页面出现在屏幕上时铺满并重排（presenter 数据由工具壳的导航入口驱动）。</summary>
    /// <param name="animated">系统动画标记。</param>
    public override void ViewWillAppear(bool animated)
    {
        base.ViewWillAppear(animated);
        _scroll.Frame = View!.SafeAreaLayoutGuide.LayoutFrame;
        Sync();
    }

    /// <summary>尺寸变化时把滚动容器铺满并重排文档内容。</summary>
    /// <param name="size">新的父容器尺寸。</param>
    public override void ViewDidLayoutSubviews()
    {
        base.ViewDidLayoutSubviews();
        _scroll.Frame = View!.SafeAreaLayoutGuide.LayoutFrame;
        Relayout();
    }

    /// <summary>把 presenter 的瓦片状态同步到原生控件并重排；位图按引用比较，未变化不重编码。</summary>
    internal void Sync()
    {
        if (_disposed) return;
        _status.Text = _presenter.Status;
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

    /// <summary>同步单张瓦片的位图、说明、占位文字与全局十字准星。</summary>
    /// <param name="tile">原生瓦片。</param>
    /// <param name="source">presenter 侧的瓦片状态。</param>
    private void SyncTile(iOSDiagnosticTile tile, AnalysisTile source)
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
    private UIImage? ImageFor(TileImages images, DecodedImage? key, bool background)
    {
        var previousKey = background ? images.Background : images.Source;
        var previousImage = background ? images.BackgroundImage : images.SourceImage;
        if (ReferenceEquals(previousKey, key)) return previousImage;
        var image = key == null ? null : iOSSymbols.CreateThumbnail(key);
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
        var paragraph = new NSMutableParagraphStyle
        {
            LineBreakMode = UILineBreakMode.WordWrap,
            MinimumLineHeight = (nfloat)RigidLineHeight,
            MaximumLineHeight = (nfloat)RigidLineHeight,
        };
        _rigidText.AttributedText = new NSAttributedString(text ?? string.Empty, new UIStringAttributes
        {
            Font = RigidTextFont,
            ForegroundColor = RigidTextColor,
            ParagraphStyle = paragraph,
        });
    }

    /// <summary>按当前宽度重排标题、状态、两行瓦片网格与刚体拟合面板，并刷新文档高度。</summary>
    private void Relayout()
    {
        double width = Math.Max(1, (double)_scroll.Bounds.Width);
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
        // TOOLS-05：第六格留空，刚体拟合文本以整宽块放在网格下方，不挤进瓦片格。
        y = PlaceRigidPanel(rowTop + tileRowHeight, contentWidth);
        _document.Frame = new CGRect(0, 0, width, Math.Max(1, y + PageMarginBottom));
        _scroll.ContentSize = _document.Frame.Size;
    }

    /// <summary>把瓦片按等宽三列摆放（第三列可为空），返回该行高度（含上下外边距）。</summary>
    /// <param name="top">行顶边纵坐标。</param>
    /// <param name="cell">单列宽度。</param>
    /// <param name="tileWidth">瓦片宽度。</param>
    /// <param name="first">第一列瓦片。</param>
    /// <param name="second">第二列瓦片。</param>
    /// <param name="third">第三列瓦片；null 表示该格留空（TOOLS-05 第六格）。</param>
    /// <returns>行高度。</returns>
    private static double PlaceTiles(double top, double cell, double tileWidth, iOSDiagnosticTile first, iOSDiagnosticTile second, iOSDiagnosticTile? third)
    {
        double height = 0;
        Place(first, 0);
        Place(second, 1);
        if (third != null) Place(third, 2);
        return height;

        void Place(iOSDiagnosticTile tile, int column)
        {
            double tileHeight = tile.RequiredHeight(tileWidth);
            tile.Frame = new CGRect(PageMarginX + column * cell + TileMargin, top + TileMargin, tileWidth, tileHeight);
            height = Math.Max(height, tileHeight + TileMargin * 2);
        }
    }

    /// <summary>摆放网格下方的整宽刚体拟合文本面板（标题、量纲说明与多行判定文本），返回面板底边纵坐标。</summary>
    /// <param name="top">面板顶边纵坐标。</param>
    /// <param name="contentWidth">页面内容宽度（整宽块）。</param>
    /// <returns>面板底边纵坐标。</returns>
    private double PlaceRigidPanel(double top, double contentWidth)
    {
        double panelX = PageMarginX;
        double panelWidth = Math.Max(1, contentWidth);
        double y = top + TileMargin;
        _rigidTitle.Frame = new CGRect(panelX, y, panelWidth, LineHeight(SectionFont));
        y += LineHeight(SectionFont) + PanelSpacing;
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
        double measured = (double)_rigidText.SizeThatFits(new CGSize(width, MeasureLimit)).Height;
        int lines = 1;
        foreach (char character in _rigidSource ?? "") if (character == '\n') lines++;
        return Math.Max(measured, lines * RigidLineHeight);
    }

    /// <summary>字体的单行高度（上升部 + 下降部 + 行距）。</summary>
    /// <param name="font">目标字体。</param>
    /// <returns>行高（点）。</returns>
    private static double LineHeight(UIFont font) => Math.Ceiling((double)font.LineHeight);

    /// <summary>创建单行只读标签（尾部截断）。</summary>
    /// <param name="text">初始文本。</param>
    /// <param name="font">字体。</param>
    /// <param name="color">文字颜色。</param>
    /// <returns>标签。</returns>
    private static UILabel SingleLineLabel(string text, UIFont font, UIColor color) => new()
    {
        Text = text,
        Font = font,
        TextColor = color,
        Lines = 1,
        LineBreakMode = UILineBreakMode.TailTruncation,
        BackgroundColor = UIColor.Clear,
    };

    /// <summary>解除 presenter 订阅并释放位图与原生视图。</summary>
    /// <param name="disposing">是否释放托管资源。</param>
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
        }
        base.Dispose(disposing);
    }

    /// <summary>单张瓦片当前的主图与背景位图缓存。</summary>
    private sealed class TileImages
    {
        /// <summary>当前主图源（<see cref="AnalysisTile.Source"/>）。</summary>
        public DecodedImage? Source { get; set; }

        /// <summary>主图对应的原生位图。</summary>
        public UIImage? SourceImage { get; set; }

        /// <summary>当前压暗背景源（<see cref="AnalysisTile.Background"/>）。</summary>
        public DecodedImage? Background { get; set; }

        /// <summary>背景对应的原生位图。</summary>
        public UIImage? BackgroundImage { get; set; }
    }

    /// <summary>滚动内容容器；空白处点击清空诊断准星（瓦片点按优先）。</summary>
    private sealed class DocumentView : UIView
    {
        private readonly UITapGestureRecognizer _blankTap;

        /// <summary>建立文档容器与空白点击识别器。</summary>
        /// <param name="clearCrosshair">空白点击动作。</param>
        public DocumentView(Action clearCrosshair)
        {
            BackgroundColor = UIColor.SystemGroupedBackground;
            _blankTap = new UITapGestureRecognizer(clearCrosshair);
            _blankTap.Delegate = new TileExcludingDelegate();
            AddGestureRecognizer(_blankTap);
        }

        /// <summary>只在空白处接收触摸，瓦片自身消费点击。</summary>
        private sealed class TileExcludingDelegate : UIGestureRecognizerDelegate
        {
            /// <summary>命中诊断瓦片时不接收。</summary>
            /// <param name="gestureRecognizer">本识别器。</param>
            /// <param name="touch">触点。</param>
            /// <returns>空白处为 true。</returns>
            public override bool ShouldReceiveTouch(UIGestureRecognizer gestureRecognizer, UITouch touch)
            {
                for (var view = touch.View; view != null; view = view.Superview)
                    if (view is iOSDiagnosticTile) return false;
                return true;
            }
        }
    }
}
