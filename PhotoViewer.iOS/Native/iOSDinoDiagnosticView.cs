using CoreAnimation;
using CoreGraphics;
using Foundation;
using PhotoViewer.Core.AI;
using PhotoViewer.Core.Image;
using UIKit;

namespace PhotoViewer.iOS.Native;

/// <summary>
/// 原生诊断页（UIKit 版）：纯 AutoLayout 布局并经 <see cref="UIViewController.SetContentScrollView"/>
/// 接入系统导航栏滚动边缘模糊与半屏 sheet 协调。
/// AI 版保留 3+2 瓦片（第六格留空），标准版保留三瓦片同一行；刚体拟合文本以连续圆角分组卡片置于网格下方。
/// 全部数据来自共享 <see cref="DinoDiagnosticPresenter"/> 或 <see cref="CvDiagnosticPresenter"/>，本视图只读不驱动推理。
/// </summary>
internal sealed class iOSDinoDiagnosticView : UIViewController
{
    private const double PageMarginX = 16;
    private const double TileSpacing = 8;

#if PHOTOVIEWER_DINO
    private readonly DinoDiagnosticPresenter _presenter;
#else
    private readonly CvDiagnosticPresenter _presenter;
#endif
    private readonly Action? _onClose;
    private readonly UIScrollView _scroll = new()
    {
        BackgroundColor = UIColor.Clear,
        AlwaysBounceVertical = true,
        TranslatesAutoresizingMaskIntoConstraints = false,
    };
    private readonly DocumentView _document;
    private readonly UILabel _status = WrappingLabel(string.Empty, UIFont.PreferredFootnote!, UIColor.SecondaryLabel);
#if PHOTOVIEWER_DINO
    private readonly UILabel _patchHeader = WrappingLabel("DINO patch（32×32，点击任意图设定参考点）", UIFont.PreferredSubheadline!, UIColor.SecondaryLabel);
#endif
    private readonly UILabel _cvHeader = WrappingLabel("CV 网格诊断（32×32 中心采样，像素单位）", UIFont.PreferredSubheadline!, UIColor.SecondaryLabel);
    private readonly UILabel _rigidTitle = WrappingLabel("抖动分析（刚体拟合）", UIFont.PreferredSubheadline!, UIColor.Label);
    private readonly UILabel _rigidMetrics = WrappingLabel("|T| / |ω| / residual", UIFont.PreferredCaption1!, UIColor.SecondaryLabel);
    private readonly UIView _rigidCard = new()
    {
        BackgroundColor = UIColor.SecondarySystemGroupedBackground,
        ClipsToBounds = true,
        TranslatesAutoresizingMaskIntoConstraints = false,
    };
    private readonly UITextView _rigidText;
    private readonly iOSDiagnosticTile _thumbnailTile = new();
#if PHOTOVIEWER_DINO
    private readonly iOSDiagnosticTile _pcaTile = new();
    private readonly iOSDiagnosticTile _cosineTile = new();
#endif
    private readonly iOSDiagnosticTile _sharpnessTile = new();
    private readonly iOSDiagnosticTile _shakeTile = new();
    private readonly iOSShakeFieldView _shakeOverlay = new();
    private readonly List<TileRowView> _tileRows = [];
    private readonly Dictionary<AnalysisTile, TileImages> _images = [];
    private readonly List<UIImage> _stale = [];
    private string? _rigidSource;
    private bool _disposed;

    /// <summary>页面上的诊断瓦片（AI 版五张，标准版三张；验收读取数量与内容）。</summary>
    internal IReadOnlyList<iOSDiagnosticTile> Tiles
        =>
#if PHOTOVIEWER_DINO
        [_thumbnailTile, _pcaTile, _cosineTile, _sharpnessTile, _shakeTile];
#else
        [_thumbnailTile, _sharpnessTile, _shakeTile];
#endif

    /// <summary>共享诊断 presenter（验收读取状态）。</summary>
#if PHOTOVIEWER_DINO
    internal DinoDiagnosticPresenter Presenter => _presenter;
#else
    internal CvDiagnosticPresenter Presenter => _presenter;
#endif

    /// <summary>刚体拟合标题在页面内容坐标系中的 frame（验收核对整宽块位于网格下方，TOOLS-05）。</summary>
    internal CGRect RigidTitleFrame => _rigidTitle.ConvertRectToView(_rigidTitle.Bounds, _document);

    /// <summary>最后一行瓦片在页面内容坐标系中的底边纵坐标。</summary>
    internal double SecondRowBottom
    {
        get
        {
            var sharpness = _sharpnessTile.ConvertRectToView(_sharpnessTile.Bounds, _document);
            var shake = _shakeTile.ConvertRectToView(_shakeTile.Bounds, _document);
            return Math.Max((double)sharpness.GetMaxY(), (double)shake.GetMaxY());
        }
    }

#if PHOTOVIEWER_DINO
    internal iOSDinoDiagnosticView(DinoDiagnosticPresenter presenter, Action? onClose = null) : base(null, null)
#else
    internal iOSDinoDiagnosticView(CvDiagnosticPresenter presenter, Action? onClose = null) : base(null, null)
#endif
    {
        _presenter = presenter;
        _onClose = onClose;
        _document = new DocumentView(presenter.ClearCrosshair) { TranslatesAutoresizingMaskIntoConstraints = false };

        var monoDescriptor = UIFontDescriptor.PreferredFootnote.CreateWithDesign(UIFontDescriptorSystemDesign.Monospaced);
        var monoFont = monoDescriptor != null
            ? UIFont.FromDescriptor(monoDescriptor, 0)
            : UIFontMetrics.DefaultMetrics.GetScaledFont(UIFont.FromName("Menlo", 12) ?? UIFont.SystemFontOfSize(12)!);
        _rigidText = new UITextView
        {
            Editable = false,
            Selectable = true,
            ScrollEnabled = false,
            BackgroundColor = UIColor.Clear,
            Font = monoFont,
            AdjustsFontForContentSizeCategory = true,
            TextColor = UIColor.Label,
            TextContainerInset = new UIEdgeInsets(12, 14, 12, 14),
            TranslatesAutoresizingMaskIntoConstraints = false,
        };

        _shakeTile.Overlay = _shakeOverlay;
        foreach (var tile in Tiles)
        {
            tile.TileClicked = presenter.OnTileClicked;
            tile.BlankClicked = presenter.ClearCrosshair;
        }
        presenter.Changed += Sync;
        presenter.CrosshairChanged += Sync;
    }

    public override void ViewDidLoad()
    {
        base.ViewDidLoad();
        Title = iOSAppCapabilities.DiagnosticName;
        if (_onClose != null)
        {
            NavigationItem.RightBarButtonItem = new UIBarButtonItem(UIBarButtonSystemItem.Close, (_, _) => _onClose());
        }

        View!.BackgroundColor = UIColor.SystemGroupedBackground;
        _rigidCard.Layer.CornerRadius = 16;
        _rigidCard.Layer.CornerCurve = CACornerCurve.Continuous;
        _rigidCard.AddSubview(_rigidText);
        NSLayoutConstraint.ActivateConstraints(
        [
            _rigidText.TopAnchor.ConstraintEqualTo(_rigidCard.TopAnchor),
            _rigidText.LeadingAnchor.ConstraintEqualTo(_rigidCard.LeadingAnchor),
            _rigidText.TrailingAnchor.ConstraintEqualTo(_rigidCard.TrailingAnchor),
            _rigidText.BottomAnchor.ConstraintEqualTo(_rigidCard.BottomAnchor),
        ]);

        var rigidHeaderStack = new UIStackView([_rigidTitle, _rigidMetrics])
        {
            Axis = UILayoutConstraintAxis.Vertical,
            Spacing = 2,
            TranslatesAutoresizingMaskIntoConstraints = false,
        };
        var rigidSection = new UIStackView([rigidHeaderStack, _rigidCard])
        {
            Axis = UILayoutConstraintAxis.Vertical,
            Spacing = 8,
            TranslatesAutoresizingMaskIntoConstraints = false,
        };

        var rootStack = new UIStackView
        {
            Axis = UILayoutConstraintAxis.Vertical,
            Spacing = 16,
            TranslatesAutoresizingMaskIntoConstraints = false,
        };
        rootStack.AddArrangedSubview(_status);
        rootStack.SetCustomSpacing(12, _status);

#if PHOTOVIEWER_DINO
        var patchRow = new TileRowView(_thumbnailTile, _pcaTile, _cosineTile);
        _tileRows.Add(patchRow);
        var patchSection = new UIStackView([_patchHeader, patchRow])
        {
            Axis = UILayoutConstraintAxis.Vertical,
            Spacing = 8,
            TranslatesAutoresizingMaskIntoConstraints = false,
        };
        rootStack.AddArrangedSubview(patchSection);

        var cvRow = new TileRowView(_sharpnessTile, _shakeTile, null);
#else
        var cvRow = new TileRowView(_thumbnailTile, _sharpnessTile, _shakeTile);
#endif
        _tileRows.Add(cvRow);
        var cvSection = new UIStackView([_cvHeader, cvRow])
        {
            Axis = UILayoutConstraintAxis.Vertical,
            Spacing = 8,
            TranslatesAutoresizingMaskIntoConstraints = false,
        };
        rootStack.AddArrangedSubview(cvSection);
        rootStack.AddArrangedSubview(rigidSection);

        _document.AddSubview(rootStack);
        _scroll.AddSubview(_document);
        View.AddSubview(_scroll);
        // 显式登记主滚动视图，接入导航栏滚动边缘效果与半屏 sheet 下拉协调。
        SetContentScrollView(_scroll, NSDirectionalRectEdge.All);

        NSLayoutConstraint.ActivateConstraints(
        [
            _scroll.TopAnchor.ConstraintEqualTo(View.TopAnchor),
            _scroll.LeadingAnchor.ConstraintEqualTo(View.LeadingAnchor),
            _scroll.TrailingAnchor.ConstraintEqualTo(View.TrailingAnchor),
            _scroll.BottomAnchor.ConstraintEqualTo(View.BottomAnchor),

            _document.TopAnchor.ConstraintEqualTo(_scroll.ContentLayoutGuide.TopAnchor),
            _document.LeadingAnchor.ConstraintEqualTo(_scroll.ContentLayoutGuide.LeadingAnchor),
            _document.TrailingAnchor.ConstraintEqualTo(_scroll.ContentLayoutGuide.TrailingAnchor),
            _document.BottomAnchor.ConstraintEqualTo(_scroll.ContentLayoutGuide.BottomAnchor),
            _document.WidthAnchor.ConstraintEqualTo(_scroll.FrameLayoutGuide.WidthAnchor),

            rootStack.TopAnchor.ConstraintEqualTo(_document.TopAnchor, 12),
            rootStack.BottomAnchor.ConstraintEqualTo(_document.BottomAnchor, -20),
            rootStack.LeadingAnchor.ConstraintEqualTo(_document.SafeAreaLayoutGuide.LeadingAnchor, (nfloat)PageMarginX),
            rootStack.TrailingAnchor.ConstraintEqualTo(_document.SafeAreaLayoutGuide.TrailingAnchor, (nfloat)(-PageMarginX)),
        ]);

        Sync();
    }

    public override void ViewWillAppear(bool animated)
    {
        base.ViewWillAppear(animated);
        Sync();
    }

    /// <summary>把 presenter 的瓦片状态同步到原生控件；位图按引用比较，未变化不重编码。</summary>
    internal void Sync()
    {
        if (_disposed) return;
        _status.Text = _presenter.Status;
        SyncRigidText(_presenter.RigidMotionText);
        SyncTile(_thumbnailTile, _presenter.ThumbnailTile);
#if PHOTOVIEWER_DINO
        SyncTile(_pcaTile, _presenter.PcaTile);
        SyncTile(_cosineTile, _presenter.CosineTile);
#endif
        SyncTile(_sharpnessTile, _presenter.SharpnessTile);
        SyncTile(_shakeTile, _presenter.ShakeTile);
        _shakeOverlay.ShakeField = _presenter.ShakeTile.ShakeField;
        foreach (var image in _stale) image.Dispose();
        _stale.Clear();
        foreach (var row in _tileRows) row.RefreshHeight();
    }

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

    private void SyncRigidText(string text)
    {
        if (_rigidSource == text) return;
        _rigidSource = text;
        _rigidText.Text = text ?? string.Empty;
    }

    private static UILabel WrappingLabel(string text, UIFont font, UIColor color) => new()
    {
        Text = text,
        Font = font,
        AdjustsFontForContentSizeCategory = true,
        TextColor = color,
        Lines = 0,
        LineBreakMode = UILineBreakMode.WordWrap,
        BackgroundColor = UIColor.Clear,
        TranslatesAutoresizingMaskIntoConstraints = false,
    };

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

    /// <summary>
    /// 等宽三列诊断瓦片行的 AutoLayout 宿主：列间距 8 点（<c>analysis.tile.spacing</c>），
    /// 第三列可留空（TOOLS-05 第六格），高度随实际宽度与说明文字行数自动更新。
    /// </summary>
    private sealed class TileRowView : UIView
    {
        private readonly iOSDiagnosticTile?[] _slots;
        private readonly NSLayoutConstraint _heightConstraint;

        internal TileRowView(iOSDiagnosticTile first, iOSDiagnosticTile second, iOSDiagnosticTile? third)
        {
            TranslatesAutoresizingMaskIntoConstraints = false;
            BackgroundColor = UIColor.Clear;
            _slots = [first, second, third];
            foreach (var tile in _slots)
            {
                if (tile != null) AddSubview(tile);
            }
            _heightConstraint = HeightAnchor.ConstraintEqualTo(100);
            _heightConstraint.Priority = 999;
            _heightConstraint.Active = true;
        }

        public override void LayoutSubviews()
        {
            base.LayoutSubviews();
            RefreshHeight();
        }

        internal void RefreshHeight()
        {
            double width = (double)Bounds.Width;
            if (width <= 1) return;
            double tileWidth = Math.Max(1, (width - TileSpacing * 2) / 3.0);
            double maxHeight = 0;
            for (int column = 0; column < 3; column++)
            {
                if (_slots[column] is not { } tile) continue;
                double height = tile.RequiredHeight(tileWidth);
                tile.Frame = new CGRect(
                    (nfloat)(column * (tileWidth + TileSpacing)),
                    0,
                    (nfloat)tileWidth,
                    (nfloat)height);
                maxHeight = Math.Max(maxHeight, height);
            }
            double target = Math.Max(1, Math.Ceiling(maxHeight));
            if (Math.Abs((double)_heightConstraint.Constant - target) > .5)
            {
                _heightConstraint.Constant = (nfloat)target;
            }
        }
    }

    private sealed class TileImages
    {
        public DecodedImage? Source { get; set; }
        public UIImage? SourceImage { get; set; }
        public DecodedImage? Background { get; set; }
        public UIImage? BackgroundImage { get; set; }
    }

    /// <summary>滚动内容容器；空白处点击清空诊断准星（瓦片与刚体文本自身消费点击）。</summary>
    private sealed class DocumentView : UIView
    {
        private readonly UITapGestureRecognizer _blankTap;

        public DocumentView(Action clearCrosshair)
        {
            BackgroundColor = UIColor.Clear;
            _blankTap = new UITapGestureRecognizer(clearCrosshair) { CancelsTouchesInView = false };
            _blankTap.Delegate = new TileExcludingDelegate();
            AddGestureRecognizer(_blankTap);
        }

        private sealed class TileExcludingDelegate : UIGestureRecognizerDelegate
        {
            public override bool ShouldReceiveTouch(UIGestureRecognizer gestureRecognizer, UITouch touch)
            {
                for (var view = touch.View; view != null; view = view.Superview)
                    if (view is iOSDiagnosticTile or UITextView) return false;
                return true;
            }
        }
    }
}
