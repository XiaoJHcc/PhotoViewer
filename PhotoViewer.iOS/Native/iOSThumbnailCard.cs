using CoreGraphics;
using Foundation;
using PhotoViewer.Core.Design;
using PhotoViewer.Core.Image;
using PhotoViewer.Core.Platform;
using PhotoViewer.Shared;
using System.ComponentModel;
using UIKit;

namespace PhotoViewer.iOS.Native;

/// <summary>
/// 胶片栏卡片（[FILM-05]）两档共用一套外观：
/// 大档 96×144 外框 + 四周 3 点占位、80 方形照片区、文件名、第二行（拍摄时间）、六格星级、抖动徽标；
/// 小档 64×76 外框、54 方形照片区、一行文件名、照片区右下角星级角标与右上角抖动点。
/// 几何全部取共享 <see cref="FilmCardGeometry"/> 的固定内部 frame 与 <see cref="DesignTokens"/>，便于程序化对拍。
/// <para>输入分流：大档星排只挂收间接指针、且只在星排矩形内接收的点按识别器——指针点星写值不选中卡片，
/// 手指落在星排上落到整卡选择按钮只选中不写值；小档没有逐星目标。</para>
/// <para>长按（两档）与指针辅助点按打开星级弹窗，同一次触摸可以继续滑到弹窗星排上取值；卡片不再有上下文菜单。</para>
/// 组级星级冲突（RATING-04）时大档 0 号槽位画 <c>exclamationmark.triangle.fill</c>（<c>color.star.conflict</c>），
/// 指针点按打开星级弹窗（按来源逐条写回）、不写值；小档在角标前加同一枚红色三角。
/// </summary>
[Register("PViOSThumbnailCard")]
internal sealed class iOSThumbnailCard : UICollectionViewCell
{
    /// <summary>大档外框四周占位与本卡内部尺寸（90×138 占位区、80 方形图、星级 14 点）。</summary>
    private const double Inset = 3, SurfaceWidth = 90, SurfaceHeight = 138, ImageSize = 80, StarSize = 14;
    /// <summary>大档第二行柱状条宽度（与第二行文字同宽）。</summary>
    private const double SecondLineWidth = 80;
    /// <summary>小档角标与抖动点的内容留白与字形间距。</summary>
    private const double BadgePadding = 2, BadgeGlyph = 8, BadgeGap = 1;
    /// <summary>大档与小档文件名、角标数字字体（系统字体不可用时直接报错）。</summary>
    private static readonly UIFont LargeNameFont =
        UIFont.SystemFontOfSize((nfloat)DesignTokens.FilmCardNameFontSize, UIFontWeight.Semibold)
        ?? throw new InvalidOperationException("系统字体不可用");
    private static readonly UIFont SmallNameFont =
        UIFont.SystemFontOfSize((nfloat)DesignTokens.FilmCardSmallNameFontSize, UIFontWeight.Semibold)
        ?? throw new InvalidOperationException("系统字体不可用");
    private static readonly UIFont BadgeDigitFont =
        UIFont.SystemFontOfSize((nfloat)DesignTokens.FilmCardSmallBadgeFontSize, UIFontWeight.Bold)
        ?? throw new InvalidOperationException("系统字体不可用");
    /// <summary>托管常驻集合：复用池里的卡片由 UIKit 原生持有，托管实例被回收会导致身份与订阅丢失。</summary>
    private static readonly HashSet<iOSThumbnailCard> LiveCells = [];
    private UIView _surface = null!;
    private UIImageView _thumbnail = null!;
    private UIImageView _placeholder = null!;
    private UIActivityIndicatorView _progress = null!;
    private UILabel _name = null!;
    private UILabel _date = null!;
    private UIView _secondLineBar = null!;
    private UIView _secondLineFill = null!;
    private UILabel _shake = null!;
    private UIButton _select = null!;
    private UIImageView[] _stars = null!;
    private UIView _smallBadge = null!;
    private UIImageView _smallBadgeTriangle = null!;
    private UIImageView _smallBadgeStar = null!;
    private UILabel _smallBadgeDigit = null!;
    private UIView _shakeDot = null!;
    private UITapGestureRecognizer _starPointerTap = null!;
    private StarTapDelegate _starTapDelegate = null!;
    private UITapGestureRecognizer _secondaryTap = null!;
    private UILongPressGestureRecognizer _longPress = null!;
    private FilmCardTier _tier = FilmCardTier.Large;
    private ImageFile? _file;
    private DecodedImage? _lastThumbnail;
    private UIImage? _nativeThumbnail;
    private Action<ImageFile>? _selectFile;
    private Action<ImageFile, int>? _rate;
    private Action<ImageFile, UIView>? _ratingPopupRequested;
    private Action<iOSThumbnailCard, UIGestureRecognizerState, CGPoint>? _longPressCallback;
    private string? _secondLineText;
    private double? _secondLineProgress;
    private bool _showRating = true;
    /// <summary>0 号槽位当前字形：冲突为警示三角，非冲突为清零叉。</summary>
    private string _clearSymbol = "xmark";
    /// <summary>选中态取主图身份（相似聚类）还是列表锚点（胶片栏）。</summary>
    private bool _selectByDisplayedImage;

    /// <summary>卡片当前绑定的业务身份（验收核对视图与顺序不错位）。</summary>
    internal ImageFile? BoundFile => _file;
    /// <summary>是否处于当前项高亮态（身份来自共享 <see cref="ImageFile.IsCurrent"/>）。</summary>
    internal bool IsHighlighted { get; private set; }
    /// <summary>整卡选择按钮（点选只改当前照片，由调用方保证不主动居中）。</summary>
    internal UIButton SelectButton => _select;
    /// <summary>0–5 星级字形，索引即星级（大档纯显示，指针点按经识别器分流）。</summary>
    internal IReadOnlyList<UIImageView> StarViews => _stars;
    /// <summary>卡片外框占位区（大档 96×144 内部 90×138，小档 64×76 内部 60×72）。</summary>
    internal UIView Surface => _surface;
    /// <summary>照片区（大档 80 方形，小档 54 方形）。</summary>
    internal UIView ThumbnailView => _thumbnail;
    /// <summary>小档照片区右下角的星级角标（大档隐藏）。</summary>
    internal UIView SmallRatingBadge => _smallBadge;
    /// <summary>小档角标里的星级数字。</summary>
    internal UILabel SmallRatingDigit => _smallBadgeDigit;
    /// <summary>小档照片区右上角的抖动点（大档用「抖」字徽标，本视图隐藏）。</summary>
    internal UIView ShakeDot => _shakeDot;
    /// <summary>第二行覆盖文字（相似聚类为百分比；null 表示显示拍摄时间）。</summary>
    internal string? SecondLineText => _secondLineText;
    /// <summary>第二行柱状条占比（null 表示不显示柱状条）。</summary>
    internal double? SecondLineProgress => _secondLineProgress;
    /// <summary>第二行柱状条是否可见。</summary>
    internal bool IsSecondLineBarVisible => !_secondLineBar.Hidden;
    /// <summary>0 号槽位当前字形名称（RATING-04：冲突 <c>exclamationmark.triangle.fill</c>，否则 <c>xmark</c>）。</summary>
    internal string ClearRatingSymbol => _clearSymbol;

    /// <summary>卡片档位；换档立即重排内部几何并刷新一轮显隐（外框尺寸由胶片栏布局给出）。</summary>
    internal FilmCardTier Tier
    {
        get => _tier;
        set
        {
            if (_tier == value) return;
            _tier = value;
            if (_file != null) Refresh();
            else ApplyTierLayout();
        }
    }

    /// <summary>原生复用池按 frame 构造卡片。</summary>
    /// <param name="frame">卡片外框。</param>
    [Export("initWithFrame:")]
    public iOSThumbnailCard(CGRect frame) : base(frame) => InitializeView();

    /// <summary>平台创建原生实例后由运行时按句柄构造托管包装。</summary>
    /// <param name="handle">原生句柄。</param>
    public iOSThumbnailCard(ObjCRuntime.NativeHandle handle) : base(handle) => InitializeView();

    /// <summary>幂等建卡：多构造入口与托管包装重建都只建一次子视图。</summary>
    private void InitializeView()
    {
        if (_surface != null) return;
        LiveCells.Add(this);
        ContentView.BackgroundColor = UIColor.Clear;
        _surface = new UIView(new CGRect(Inset, Inset, SurfaceWidth, SurfaceHeight));
        _surface.BackgroundColor = UIColor.FromWhiteAlpha(2f / 15, 1);
        _surface.Layer.BorderWidth = 2;
        _surface.Layer.CornerRadius = 7;
        _surface.Layer.MasksToBounds = true;
        ContentView.AddSubview(_surface);
        _thumbnail = new UIImageView(new CGRect(5, 5, ImageSize, ImageSize));
        _thumbnail.ContentMode = UIViewContentMode.ScaleAspectFit;
        _thumbnail.BackgroundColor = UIColor.FromWhiteAlpha(.2f, 1);
        _thumbnail.Layer.CornerRadius = 4;
        _thumbnail.Layer.BorderWidth = 1;
        _thumbnail.Layer.MasksToBounds = true;
        _placeholder = new UIImageView(new CGRect((SurfaceWidth - 24) / 2, (SurfaceHeight - 72) / 2, 24, 24))
        {
            Image = iOSSymbols.Symbol("photo"),
            TintColor = UIColor.TertiaryLabel,
        };
        _progress = new UIActivityIndicatorView(new CGRect(SurfaceWidth / 2 - 8, 37, 16, 16)) { HidesWhenStopped = true };
        _name = Label(new CGRect(2, 89, 86, 12), 10, UIFontWeight.Semibold);
        _date = Label(new CGRect(5, 103, SecondLineWidth, 11), 9, UIFontWeight.Bold);
        _secondLineBar = new UIView(new CGRect(5, 103, SecondLineWidth, 11));
        _secondLineBar.BackgroundColor = UIColor.FromRGB(0x44, 0x44, 0x44);
        _secondLineBar.Layer.CornerRadius = 4;
        _secondLineBar.Layer.MasksToBounds = true;
        _secondLineFill = new UIView(new CGRect(0, 0, 0, 11)) { BackgroundColor = UIColor.FromRGBA(0x1E, 0x90, 0xFF, 0x99) };
        _secondLineBar.AddSubview(_secondLineFill);
        _secondLineBar.Hidden = true;
        _shake = Label(new CGRect(67, 67, 16, 16), 11, UIFontWeight.Bold);
        _shake.Text = "抖";
        _shake.TextColor = UIColor.White;
        _shake.BackgroundColor = UIColor.FromRGB(255, 85, 0);
        _shake.Layer.CornerRadius = 2;
        _shake.Layer.MasksToBounds = true;
        _select = new UIButton(new CGRect(Inset, Inset, SurfaceWidth, SurfaceHeight));
        _select.BackgroundColor = UIColor.Clear;
        _select.AccessibilityLabel = "选择照片";
        _stars = new UIImageView[RatingControlGeometry.SlotCount];
        for (int rating = 0; rating < _stars.Length; rating++)
        {
            var glyph = new UIImageView(CGRect.Empty)
            {
                ContentMode = UIViewContentMode.Center,
                UserInteractionEnabled = false,
                BackgroundColor = UIColor.Clear,
            };
            _stars[rating] = glyph;
        }
        // 小档角标：黑色半透明胶囊，内容为冲突三角（可选）+ 金星 + 数字。
        _smallBadge = new UIView(CGRect.Empty)
        {
            BackgroundColor = UIColor.FromWhiteAlpha(0, .6f),
            UserInteractionEnabled = false,
        };
        _smallBadge.Layer.CornerRadius = (nfloat)(DesignTokens.FilmCardSmallBadgeHeight / 2);
        _smallBadgeTriangle = new UIImageView(new CGRect(0, 0, BadgeGlyph, BadgeGlyph))
        {
            ContentMode = UIViewContentMode.ScaleAspectFit,
            UserInteractionEnabled = false,
            BackgroundColor = UIColor.Clear,
        };
        _smallBadgeStar = new UIImageView(new CGRect(0, 0, BadgeGlyph, BadgeGlyph))
        {
            ContentMode = UIViewContentMode.ScaleAspectFit,
            UserInteractionEnabled = false,
            BackgroundColor = UIColor.Clear,
            Image = iOSSymbols.Symbol("star.fill", DesignTokens.FilmCardSmallBadgeFontSize),
            TintColor = iOSColors.FromToken(DesignTokens.ColorStarOn),
        };
        _smallBadgeDigit = new UILabel(CGRect.Empty)
        {
            Font = BadgeDigitFont,
            TextColor = UIColor.White,
            TextAlignment = UITextAlignment.Center,
            BackgroundColor = UIColor.Clear,
        };
        _smallBadge.AddSubview(_smallBadgeTriangle);
        _smallBadge.AddSubview(_smallBadgeStar);
        _smallBadge.AddSubview(_smallBadgeDigit);
        _shakeDot = new UIView(CGRect.Empty)
        {
            BackgroundColor = iOSColors.FromToken(DesignTokens.ColorFilmShakeBadge),
            UserInteractionEnabled = false,
        };
        _shakeDot.Layer.CornerRadius = (nfloat)(DesignTokens.FilmCardSmallShakeDotSize / 2);
        _surface.AddSubview(_thumbnail);
        _surface.AddSubview(_placeholder);
        _surface.AddSubview(_progress);
        _surface.AddSubview(_name);
        _surface.AddSubview(_secondLineBar);
        _surface.AddSubview(_date);
        _surface.AddSubview(_shake);
        _surface.AddSubview(_smallBadge);
        _surface.AddSubview(_shakeDot);
        foreach (var glyph in _stars) _surface.AddSubview(glyph);
        // 选择按钮覆盖整卡占位区；大档星排由只收间接指针的点按识别器分流，手指仍落到整卡选择。
        _surface.AddSubview(_select);
        _select.TouchUpInside += (_, _) => { if (_file != null) _selectFile?.Invoke(_file); };

        _starTapDelegate = new StarTapDelegate(this);
        _starPointerTap = new UITapGestureRecognizer(OnStarPointerTap)
        {
            AllowedTouchTypes = [NSNumber.FromInt64((long)UITouchType.IndirectPointer)],
            Delegate = _starTapDelegate,
        };
        _surface.AddGestureRecognizer(_starPointerTap);
        // 指针辅助点按（右键/两指点按）与长按同义：打开星级弹窗。
        _secondaryTap = new UITapGestureRecognizer(OnSecondaryClick)
        {
            ButtonMaskRequired = UIEventButtonMask.Secondary,
        };
        _surface.AddGestureRecognizer(_secondaryTap);
        _longPress = new UILongPressGestureRecognizer(OnLongPress)
        {
            MinimumPressDuration = DesignTokens.MotionRatingLongPress / 1000.0,
        };
        ContentView.AddGestureRecognizer(_longPress);
        ApplyTierLayout();
    }

    /// <summary>居中原生标签（尾部截断，超宽文件名先截断显示，完整名走星级弹窗入口）。</summary>
    /// <param name="frame">标签 frame。</param>
    /// <param name="fontSize">字号。</param>
    /// <param name="weight">字重。</param>
    /// <returns>配置完成的标签。</returns>
    private static UILabel Label(CGRect frame, double fontSize, UIFontWeight weight)
        => new(frame)
        {
            Font = UIFont.SystemFontOfSize((nfloat)fontSize, weight),
            TextColor = UIColor.FromRGB(0xEE, 0xEE, 0xEE),
            TextAlignment = UITextAlignment.Center,
            LineBreakMode = UILineBreakMode.TailTruncation,
            BackgroundColor = UIColor.Clear,
        };

    /// <summary>绑定业务身份：重挂模型订阅、设置星级显隐并立即刷新一次。</summary>
    /// <param name="file">卡片对应的照片。</param>
    /// <param name="showRating">是否显示星级（大档星排与小档角标都受它控制）。</param>
    /// <param name="select">整卡点选回调。</param>
    /// <param name="rate">星级写回回调（指针点星、长按滑动提交都走它）。</param>
    /// <param name="secondLineText">第二行文字；null 显示拍摄时间。</param>
    /// <param name="secondLineProgress">第二行柱状条占比（0~1）；null 只显示文字（相似聚类用 75% 起的占比）。</param>
    /// <param name="selectByDisplayedImage">选中态取 <see cref="ImageFile.IsCurrentImage"/>（相似聚类面板）而非列表锚点 <see cref="ImageFile.IsCurrent"/>（胶片栏）。</param>
    /// <param name="ratingPopup">打开星级弹窗（冲突三角、指针辅助点按），锚点为整张卡片。</param>
    /// <param name="longPress">长按手势（宿主把同一次触摸延续到弹窗星排上）。</param>
    public void Bind(ImageFile file, bool showRating, Action<ImageFile> select, Action<ImageFile, int> rate,
        string? secondLineText = null, double? secondLineProgress = null, bool selectByDisplayedImage = false,
        Action<ImageFile, UIView>? ratingPopup = null,
        Action<iOSThumbnailCard, UIGestureRecognizerState, CGPoint>? longPress = null)
    {
        if (_file != null) _file.PropertyChanged -= OnFileChanged;
        _file = file;
        _selectFile = select;
        _rate = rate;
        _ratingPopupRequested = ratingPopup;
        _longPressCallback = longPress;
        _secondLineText = secondLineText;
        _secondLineProgress = secondLineProgress;
        _selectByDisplayedImage = selectByDisplayedImage;
        _showRating = showRating;
        _lastThumbnail = null;
        file.PropertyChanged += OnFileChanged;
        Refresh();
    }

    /// <summary>模型事件封送到主线程，消费前再次核对复用后的绑定。</summary>
    /// <param name="sender">事件源（照片）。</param>
    /// <param name="args">属性变化参数。</param>
    private void OnFileChanged(object? sender, PropertyChangedEventArgs args)
        => UiDispatcher.Post(() => { if (ReferenceEquals(sender, _file)) Refresh(); });

    /// <summary>更新高亮/缓存描边、文字、星级与已有抖动结果，不触发任何推理。</summary>
    private void Refresh()
    {
        if (_file == null) return;
        bool small = _tier == FilmCardTier.Small;
        _name.Text = _file.DisplayName;
        _date.Text = _secondLineText ?? _file.PhotoDate?.ToString("MM-dd HH:mm") ?? "";
        _secondLineBar.Hidden = small || _secondLineProgress == null;
        if (_secondLineProgress is { } fraction)
            _secondLineFill.Frame = new CGRect(0, 0, (nfloat)(Math.Clamp(fraction, 0.0, 1.0) * SecondLineWidth), 11);
        IsHighlighted = _selectByDisplayedImage ? _file.IsCurrentImage : _file.IsCurrent;
        _surface.Layer.BorderColor = IsHighlighted
            ? iOSColors.FromToken(DesignTokens.ColorFilmCardCurrentBorder).CGColor
            : UIColor.Clear.CGColor;
        _thumbnail.Layer.BorderColor = _file.IsInCache
            ? iOSColors.FromToken(DesignTokens.ColorFilmCardCachedBorder).CGColor
            : UIColor.Clear.CGColor;
        _placeholder.Hidden = !_file.ShowThumbnailPlaceholder;
        bool loading = _file.IsThumbnailLoading && _file.Thumbnail == null;
        if (loading) _progress.StartAnimating();
        else _progress.StopAnimating();
        bool shaking = _file.IsShake == true;
        _shake.Hidden = small || !shaking;
        _shakeDot.Hidden = !small || !shaking;
        foreach (var glyph in _stars) glyph.Hidden = small || !_showRating;
        // RATING-04：组级来源冲突时 0 号槽位换成红色警示三角；来源刷新（HasRatingConflict 变化）后立即回到清零叉。
        bool conflict = _file.HasRatingConflict;
        _clearSymbol = conflict ? "exclamationmark.triangle.fill" : "xmark";
        _stars[0].Image = iOSSymbols.Symbol(_clearSymbol,
            conflict ? DesignTokens.FilmCardStarFontSize : DesignTokens.FilmCardClearGlyphSize);
        _stars[0].TintColor = conflict
            ? iOSColors.FromToken(DesignTokens.ColorStarConflict)
            : iOSColors.FromToken(DesignTokens.ColorStarClear);
        for (int index = 1; index < _stars.Length; index++)
        {
            _stars[index].Image = iOSSymbols.Symbol(index <= _file.Rating ? "star.fill" : "star", 10);
            _stars[index].TintColor = index <= _file.Rating
                ? iOSColors.FromToken(DesignTokens.ColorStarOn)
                : UIColor.Gray;
        }
        // 小档角标：0 星且无冲突不显示；冲突在金星与数字前加红色警示三角。
        int rating = Math.Max(0, _file.Rating);
        bool badgeVisible = small && _showRating && (rating > 0 || conflict);
        _smallBadge.Hidden = !badgeVisible;
        _smallBadgeTriangle.Hidden = !conflict;
        _smallBadgeTriangle.Image = conflict
            ? iOSSymbols.Symbol("exclamationmark.triangle.fill", DesignTokens.FilmCardSmallBadgeFontSize)
            : null;
        _smallBadgeTriangle.TintColor = iOSColors.FromToken(DesignTokens.ColorStarConflict);
        _smallBadgeDigit.Text = rating.ToString();
        ApplyTierLayout();
        var thumbnail = _file.Thumbnail;
        if (ReferenceEquals(thumbnail, _lastThumbnail)) return;
        var previous = _nativeThumbnail;
        _nativeThumbnail = null;
        if (thumbnail != null && thumbnail.TryAcquire())
        {
            try { _nativeThumbnail = iOSSymbols.CreateThumbnail(thumbnail); }
            finally { thumbnail.Release(); }
        }
        _thumbnail.Image = _nativeThumbnail;
        _lastThumbnail = thumbnail;
        previous?.Dispose();
    }

    /// <summary>按档位落位内部几何：大档沿用旧的 96×144 口径，小档为 64×76 的紧凑排布。</summary>
    private void ApplyTierLayout()
    {
        if (_surface == null) return;
        bool small = _tier == FilmCardTier.Small;
        _surface.Frame = small
            ? new CGRect(DesignTokens.FilmCardSmallInset, DesignTokens.FilmCardSmallInset,
                DesignTokens.FilmCardSmallWidth - DesignTokens.FilmCardSmallInset * 2,
                DesignTokens.FilmCardSmallHeight - DesignTokens.FilmCardSmallInset * 2)
            : new CGRect(Inset, Inset, SurfaceWidth, SurfaceHeight);
        _select.Frame = _surface.Bounds;
        double thumbInset = small ? DesignTokens.FilmCardSmallThumbInset : 5;
        double thumbSize = small ? DesignTokens.FilmCardSmallThumbSize : ImageSize;
        _thumbnail.Frame = new CGRect(thumbInset, thumbInset, thumbSize, thumbSize);
        _placeholder.Frame = new CGRect(thumbInset + (thumbSize - 24) / 2, thumbInset + (thumbSize - 24) / 2, 24, 24);
        _progress.Frame = new CGRect(thumbInset + thumbSize / 2 - 8, thumbInset + thumbSize / 2 - 8, 16, 16);
        if (small)
        {
            _name.Frame = new CGRect(2, DesignTokens.FilmCardSmallNameY, _surface.Bounds.Width - 4,
                DesignTokens.FilmCardSmallNameHeight);
            _name.Font = SmallNameFont;
            LayoutSmallBadge(thumbInset + thumbSize, thumbInset);
            _shakeDot.Frame = new CGRect(thumbInset + thumbSize - DesignTokens.FilmCardSmallShakeDotInset
                - DesignTokens.FilmCardSmallShakeDotSize, thumbInset + DesignTokens.FilmCardSmallShakeDotInset,
                DesignTokens.FilmCardSmallShakeDotSize, DesignTokens.FilmCardSmallShakeDotSize);
            return;
        }
        _name.Frame = new CGRect(2, DesignTokens.FilmCardNameY, DesignTokens.FilmCardNameWidth,
            DesignTokens.FilmCardNameHeight);
        _name.Font = LargeNameFont;
        _date.Frame = new CGRect(5, DesignTokens.FilmCardSecondLineY, SecondLineWidth, DesignTokens.FilmCardSecondLineHeight);
        _secondLineBar.Frame = _date.Frame;
        _secondLineFill.Frame = new CGRect(0, 0, _secondLineFill.Frame.Width, DesignTokens.FilmCardSecondLineHeight);
        _shake.Frame = new CGRect(67, 67, DesignTokens.FilmCardBadgeSize, DesignTokens.FilmCardBadgeSize);
        for (int rating = 0; rating < _stars.Length; rating++)
            _stars[rating].Frame = new CGRect(Inset + rating * StarSize, DesignTokens.FilmCardStarY, StarSize, StarSize);
    }

    /// <summary>小档角标：贴在照片区右下角内缩 2 点，宽度按内容（可选三角 + 金星 + 数字）量出。</summary>
    /// <param name="thumbRight">照片区右缘（占位区坐标）。</param>
    /// <param name="thumbBottom">照片区上缘（占位区坐标，加边长得到下缘）。</param>
    private void LayoutSmallBadge(double thumbRight, double thumbTop)
    {
        double height = DesignTokens.FilmCardSmallBadgeHeight;
        double inset = DesignTokens.FilmCardBadgeInset;
        double bottom = thumbTop + DesignTokens.FilmCardSmallThumbSize - inset;
        double digitWidth = Math.Max(BadgeGlyph,
            Math.Ceiling((double)_smallBadgeDigit.SizeThatFits(new CGSize(64, height)).Width));
        double width = BadgePadding * 2 + BadgeGlyph + BadgeGap + digitWidth
            + (_smallBadgeTriangle.Hidden ? 0 : BadgeGlyph + BadgeGap);
        var frame = new CGRect(thumbRight - inset - width, bottom - height, width, height);
        _smallBadge.Frame = frame;
        double x = BadgePadding;
        double y = (height - BadgeGlyph) / 2;
        if (!_smallBadgeTriangle.Hidden)
        {
            _smallBadgeTriangle.Frame = new CGRect(x, y, BadgeGlyph, BadgeGlyph);
            x += BadgeGlyph + BadgeGap;
        }
        _smallBadgeStar.Frame = new CGRect(x, y, BadgeGlyph, BadgeGlyph);
        x += BadgeGlyph + BadgeGap;
        _smallBadgeDigit.Frame = new CGRect(x, 0, digitWidth, height);
    }

    /// <summary>
    /// 星排点按：大档星排是纯显示视图，指针点按按 6 等分映射到星级后交给同一入口；
    /// 手指落在这里会落到整卡选择按钮，只选中、不写值。
    /// </summary>
    /// <param name="value">星级值（0 为清零/冲突槽）。</param>
    /// <param name="indirectPointer">是否来自间接指针（鼠标/触控板）点按。</param>
    internal void HandleStarTap(int value, bool indirectPointer)
    {
        if (_file == null) return;
        if (!indirectPointer)
        {
            _selectFile?.Invoke(_file);
            return;
        }
        if (value == 0 && _file.HasRatingConflict)
        {
            _ratingPopupRequested?.Invoke(_file, this);
            return;
        }
        _rate?.Invoke(_file, value);
    }

    /// <summary>长按接缝（识别器与验收共用）：把状态与卡片坐标中的触点交给宿主，由它延续到星级弹窗。</summary>
    /// <param name="state">手势状态。</param>
    /// <param name="pointInCard">卡片坐标中的触点。</param>
    internal void HandleLongPress(UIGestureRecognizerState state, CGPoint pointInCard)
    {
        if (_file == null) return;
        _longPressCallback?.Invoke(this, state, pointInCard);
    }

    /// <summary>指针辅助点按（右键/两指点按）：打开星级弹窗，不写值。</summary>
    internal void HandleSecondaryClick()
    {
        if (_file == null) return;
        _ratingPopupRequested?.Invoke(_file, this);
    }

    /// <summary>星排的显示矩形（占位区坐标）：0 号在最左，六格等宽。</summary>
    private CGRect StarRowRect()
        => new(Inset, DesignTokens.FilmCardStarY, _stars.Length * StarSize, StarSize);

    /// <summary>识别器只在大档、显示评分且触点落在星排矩形内时接收触摸。</summary>
    /// <param name="recognizer">星排点按识别器。</param>
    /// <param name="touch">系统给出的触摸。</param>
    /// <returns>是否接收该触摸。</returns>
    private bool ShouldReceiveStarTap(UIGestureRecognizer recognizer, UITouch touch)
        => _tier == FilmCardTier.Large && _showRating && StarRowRect().Contains(touch.LocationInView(_surface));

    /// <summary>星排点按：按触点到星排起点的距离映射到 0–5，越界夹到两端。</summary>
    /// <param name="recognizer">星排点按识别器。</param>
    private void OnStarPointerTap(UITapGestureRecognizer recognizer)
    {
        if (recognizer.State != UIGestureRecognizerState.Ended) return;
        var point = recognizer.LocationInView(_surface);
        int value = Math.Clamp((int)Math.Floor(((double)point.X - Inset) / StarSize), 0, _stars.Length - 1);
        HandleStarTap(value, indirectPointer: true);
    }

    /// <summary>指针辅助点按识别器入口：抬手时打开弹窗。</summary>
    /// <param name="recognizer">辅助点按识别器。</param>
    private void OnSecondaryClick(UITapGestureRecognizer recognizer)
    {
        if (recognizer.State != UIGestureRecognizerState.Ended) return;
        HandleSecondaryClick();
    }

    /// <summary>长按识别器入口：状态与触点原样交给宿主。</summary>
    /// <param name="recognizer">长按识别器。</param>
    private void OnLongPress(UILongPressGestureRecognizer recognizer)
        => HandleLongPress(recognizer.State, recognizer.LocationInView(this));

    /// <summary>复用前解除模型订阅与独立 UIImage（下一次绑定会重建）。</summary>
    public override void PrepareForReuse()
    {
        base.PrepareForReuse();
        if (_file != null) _file.PropertyChanged -= OnFileChanged;
        _file = null;
        _selectFile = null;
        _rate = null;
        _ratingPopupRequested = null;
        _longPressCallback = null;
        _thumbnail.Image = null;
        _nativeThumbnail?.Dispose();
        _nativeThumbnail = null;
        _lastThumbnail = null;
        _progress.StopAnimating();
        _secondLineBar.Hidden = true;
        _secondLineText = null;
        _secondLineProgress = null;
        _selectByDisplayedImage = false;
    }

    /// <summary>销毁时解除订阅并从常驻集合移除。</summary>
    /// <param name="disposing">是否释放托管资源。</param>
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            LiveCells.Remove(this);
            if (_file != null) _file.PropertyChanged -= OnFileChanged;
            _file = null;
            _nativeThumbnail?.Dispose();
            _nativeThumbnail = null;
            _lastThumbnail = null;
        }
        base.Dispose(disposing);
    }

    /// <summary>星排点按识别器的接收判定委托（识别器对委托是弱引用，必须由本卡强持有）。</summary>
    private sealed class StarTapDelegate(iOSThumbnailCard owner) : UIGestureRecognizerDelegate
    {
        /// <summary>只接收星排矩形内的触摸。</summary>
        /// <param name="recognizer">星排点按识别器。</param>
        /// <param name="touch">系统给出的触摸。</param>
        /// <returns>是否接收该触摸。</returns>
        public override bool ShouldReceiveTouch(UIGestureRecognizer recognizer, UITouch touch)
            => owner.ShouldReceiveStarTap(recognizer, touch);
    }
}
