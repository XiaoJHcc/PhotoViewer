using System;
using System.Collections.Generic;
using System.ComponentModel;
using Android.Content;
using Android.Graphics;
using Android.OS;
using Android.Views;
using Android.Widget;
using AndroidX.Core.View;
using AndroidX.Core.View.Accessibility;
using AndroidX.CustomView.Widget;
using PhotoViewer.Core.Design;
using PhotoViewer.Core.Image;
using PhotoViewer.Core.Platform;
using PhotoViewer.Shared;
using PhotoViewer.ViewModels.Main;

namespace PhotoViewer.Android.Native;

/// <summary>卡片长按的手势阶段：起手同时打开星级弹窗，并把同一次触摸继续交给它跟踪。</summary>
internal enum CardRatingPopupPhase
{
    /// <summary>长按起手：打开弹窗并在弹窗星排上起手。</summary>
    Began,

    /// <summary>按住移动：按弹窗星排投影预览。</summary>
    Moved,

    /// <summary>抬手：提交一次或保持弹窗。</summary>
    Ended,

    /// <summary>系统打断或越出取消区：清预览、不写回。</summary>
    Cancelled,
}

/// <summary>
/// 卡片星级弹窗入口（FILM-05、RATING-04）：冲突三角只需打开弹窗，卡片长按还要把后续触摸一并转发，
/// 卡片自身不持有弹窗也不做评分菜单。
/// </summary>
internal interface ICardRatingPopupHost
{
    /// <summary>打开该卡片的星级弹窗（照片有组级冲突时自动为来源条目形态）。</summary>
    /// <param name="card">被点的卡片。</param>
    void OpenCardRatingPopup(AndroidThumbnailCard card);

    /// <summary>把卡片上的长按手势阶段转发给星级弹窗。</summary>
    /// <param name="card">发起长按的卡片。</param>
    /// <param name="e">卡片收到的触摸事件。</param>
    /// <param name="phase">手势阶段。</param>
    void TrackCardRatingPopup(AndroidThumbnailCard card, MotionEvent e, CardRatingPopupPhase phase);
}

/// <summary>
/// 胶片栏卡片（A3，[FILM-05]）：96×144 外框、90×138 占位区、80 方形照片区、文件名、拍摄时间、
/// 6 个星级、抖动徽标、当前项蓝框与 Full 缓存细边；整卡自绘，尺寸与 iOS 原生壳同口径。
/// 星级命中优先于整卡选择（点星不切图）；组级冲突时 0 号换警示三角，点击只打开星级弹窗（RATING-04）。
/// 长按同样打开星级弹窗（卡片没有独立评分菜单），不切图、不改变布局。
/// </summary>
internal sealed class AndroidThumbnailCard : View
{
    private const double Inset = 3, SurfaceWidth = 90, SurfaceHeight = 138, ImageSize = 80, StarSize = 14;
    private const double SecondLineWidth = 80;
    /// <summary>星级行的绘制/命中左边缘（6 颗 14dp 星在 96dp 卡片内居中的起点）。</summary>
    private static readonly double StarRowLeft = (CardWidthDp - StarSize * 6) / 2;
    /// <summary>星级行的绘制/命中上边缘；绘制与触摸共用同一几何，锚点即真实三角位置。</summary>
    private const double StarRowTop = 119;
    private readonly Paint _surfacePaint;
    private readonly Paint _thumbnailBackground;
    private readonly Paint _textPaint;
    private readonly Paint _datePaint;
    private readonly Paint _starOutline;
    private readonly Paint _starFilled;
    private readonly Paint _badgePaint;
    private readonly Paint _badgeTextPaint;
    private readonly Paint _clearPaint;
    private readonly Paint _conflictPaint;
    private readonly GestureDetector _gestures;
    private readonly CardExploreByTouchHelper _accessibility;
    private ImageFile? _file;
    private Bitmap? _thumbnail;
    private DecodedImage? _thumbnailSource;
    private Action<ImageFile>? _select;
    private Action<ImageFile, int>? _rate;
    private ICardRatingPopupHost? _popupHost;
    private bool _showRating = true;
    private FilmCardTier _tier = FilmCardTier.Large;
    private readonly Paint _smallNamePaint;
    private readonly Paint _smallBadgePaint;
    private readonly Paint _smallStarPaint;
    private readonly Paint _smallConflictPaint;
    private string? _secondLineText;
    private double? _secondLineProgress;
    private bool _selectByDisplayedImage;
    private bool _popupGestureActive;
    private ImageFile? _touchFile;
    private bool _disposed;

    /// <summary>当前绑定的业务身份（验收核对复用不错位）。</summary>
    internal ImageFile? BoundFile => _file;
    /// <summary>卡片外框宽度（dp）。</summary>
    internal static double CardWidthDp => 96;
    /// <summary>卡片外框高度（dp）。</summary>
    internal static double CardHeightDp => 144;
    /// <summary>当前卡片档位（验收读取）。</summary>
    internal FilmCardTier Tier => _tier;

    /// <summary>
    /// 设置卡片档位（FILM-05）：大档 96×144、小档 64×76，尺寸由胶片栏的布局参数给出，
    /// 这里只切换绘制内容与命中口径（小档不设逐星命中）。
    /// </summary>
    /// <param name="tier">档位。</param>
    internal void SetTier(FilmCardTier tier)
    {
        if (_tier == tier) return;
        _tier = tier;
        Invalidate();
    }
    /// <summary>第二行文字（相似聚类为百分比；null 表示拍摄时间）。</summary>
    internal string? SecondLineText => _secondLineText;
    /// <summary>大档冲突三角字号（验收：小档绘制不得污染）。</summary>
    internal float LargeConflictGlyphSizeForChecks => _conflictPaint.TextSize;
    /// <summary>小档冲突三角字号（验收）。</summary>
    internal float SmallConflictGlyphSizeForChecks => _smallConflictPaint.TextSize;

    /// <summary>0 号槽位是否为星级冲突警示三角（验收用）。</summary>
    internal bool ShowsRatingConflict => _file?.HasRatingConflict == true;

    /// <summary>
    /// 给定槽位在卡片视图内的矩形（px，含列布局的水平居中偏移）；锚点与实际绘制/命中同一几何。
    /// </summary>
    /// <param name="index">0–5。</param>
    /// <returns>矩形；卡片尚未测量或索引越界返回 null。</returns>
    internal Rect? StarSlotRectPx(int index)
    {
        if (Width < 1 || index < 0 || index > 5) return null;
        float density = (float)AndroidUiTheme.Density(Context!);
        float scale = Math.Min(Width / (float)(CardWidthDp * density), 1f);
        float originX = (Width - (float)(96 * density)) / 2;
        float left = originX + (float)((StarRowLeft + index * StarSize) * density) * scale;
        float top = (float)(StarRowTop * density) * scale;
        float side = (float)(StarSize * density) * scale;
        return new Rect((int)Math.Round(left), (int)Math.Round(top),
            (int)Math.Round(left + side), (int)Math.Round(top + side));
    }

    /// <summary>创建卡片并建好画笔/手势识别。</summary>
    /// <param name="context">宿主上下文。</param>
    public AndroidThumbnailCard(Context context) : base(context)
    {
        _surfacePaint = AndroidUiTheme.Paint(AndroidUiTheme.CardBackground);
        _thumbnailBackground = AndroidUiTheme.Paint(AndroidUiTheme.FromToken(DesignTokens.ColorFilmThumbnailWell));
        _textPaint = AndroidUiTheme.TextPaint(context, DesignTokens.FilmCardNameFontSize, AndroidUiTheme.TextPrimary, bold: true);
        _datePaint = AndroidUiTheme.TextPaint(context, DesignTokens.FilmCardSecondLineFontSize, AndroidUiTheme.TextPrimary, bold: true);
        _starOutline = AndroidUiTheme.Paint(AndroidUiTheme.StarInactive);
        _starFilled = AndroidUiTheme.Paint(AndroidUiTheme.StarActive);
        if (AndroidUiTheme.IconTypeface(context) is { } outlineTypeface) _starOutline.SetTypeface(outlineTypeface);
        if (AndroidUiTheme.IconSolidTypeface(context) is { } solidTypeface) _starFilled.SetTypeface(solidTypeface);
        _starOutline.TextSize = AndroidUiTheme.Sp(context, DesignTokens.FilmCardStarFontSize);
        _starFilled.TextSize = AndroidUiTheme.Sp(context, DesignTokens.FilmCardStarFontSize);
        _badgePaint = AndroidUiTheme.Paint(AndroidUiTheme.ShakeBadge);
        _badgeTextPaint = AndroidUiTheme.TextPaint(context, DesignTokens.FilmCardBadgeFontSize, Color.White, bold: true);
        // FILM-05：清零叉使用中性图形色，不随评分点亮。
        _clearPaint = AndroidUiTheme.Paint(AndroidUiTheme.StarClear);
        if (AndroidUiTheme.IconTypeface(context) is { } clearTypeface) _clearPaint.SetTypeface(clearTypeface);
        _clearPaint.TextSize = AndroidUiTheme.Sp(context, DesignTokens.FilmCardStarFontSize);
        _conflictPaint = AndroidUiTheme.TextPaint(context, DesignTokens.FilmCardStarFontSize, AndroidUiTheme.StarConflict);
        if (AndroidUiTheme.IconTypeface(context) is { } iconTypeface) _conflictPaint.SetTypeface(iconTypeface);
        _smallNamePaint = AndroidUiTheme.TextPaint(context, DesignTokens.FilmCardSmallNameFontSize, AndroidUiTheme.TextPrimary, bold: true);
        _smallBadgePaint = AndroidUiTheme.TextPaint(context, DesignTokens.FilmCardSmallBadgeFontSize, Color.White, bold: true);
        _smallStarPaint = AndroidUiTheme.TextPaint(context, DesignTokens.FilmCardSmallBadgeFontSize, AndroidUiTheme.StarActive);
        if (AndroidUiTheme.IconSolidTypeface(context) is { } smallSolid) _smallStarPaint.SetTypeface(smallSolid);
        _smallConflictPaint = AndroidUiTheme.TextPaint(context, DesignTokens.FilmCardSmallBadgeFontSize, AndroidUiTheme.StarConflict);
        if (AndroidUiTheme.IconTypeface(context) is { } smallIcon) _smallConflictPaint.SetTypeface(smallIcon);
        var listener = new CardGestureListener(this);
        _gestures = new GestureDetector(context, listener);
        Focusable = true;
        Clickable = true;
        LongClickable = true;
        LongClick += (_, _) => { if (_file != null) _popupHost?.OpenCardRatingPopup(this); };
        _gestures.SetContextClickListener(listener);
        Click += (_, _) => { if (_file != null) _select?.Invoke(_file); };
        _accessibility = new CardExploreByTouchHelper(this);
        ViewCompat.SetAccessibilityDelegate(this, _accessibility);
    }

    /// <summary>
    /// 绑定照片身份与回调；重挂模型订阅并立即刷新一次。
    /// </summary>
    /// <param name="file">卡片对应照片。</param>
    /// <param name="showRating">是否显示星级。</param>
    /// <param name="select">整卡点选。</param>
    /// <param name="rate">星级点击（0–5）。</param>
    /// <param name="secondLineText">第二行文字；null 显示拍摄时间。</param>
    /// <param name="secondLineProgress">第二行进度条占比；null 不显示。</param>
    /// <param name="selectByDisplayedImage">选中态取 <see cref="ImageFile.IsCurrentImage"/>（相似聚类）。</param>
    /// <param name="popupHost">星级弹窗入口：冲突三角与长按都用它打开同一个弹窗。</param>
    internal void Bind(ImageFile file, bool showRating, Action<ImageFile> select, Action<ImageFile, int> rate,
        string? secondLineText = null, double? secondLineProgress = null, bool selectByDisplayedImage = false,
        ICardRatingPopupHost? popupHost = null)
    {
        if (_file != null) _file.PropertyChanged -= OnFileChanged;
        _file = file;
        _select = select;
        _rate = rate;
        _popupHost = popupHost;
        _showRating = showRating;
        _secondLineText = secondLineText;
        _secondLineProgress = secondLineProgress;
        _selectByDisplayedImage = selectByDisplayedImage;
        _popupGestureActive = false;
        _thumbnail = null;
        _thumbnailSource = null;
        file.PropertyChanged += OnFileChanged;
        UpdateAccessibilityName();
        Invalidate();
    }

    /// <summary>复用/回收前解除订阅与位图引用。</summary>
    internal void Recycle()
    {
        if (_file != null) _file.PropertyChanged -= OnFileChanged;
        _file = null;
        _select = null;
        _rate = null;
        _popupHost = null;
        _popupGestureActive = false;
        _thumbnail = null;
        _thumbnailSource = null;
        ContentDescription = null;
    }

    /// <summary>模型事件封送主线程，消费前核对复用后的绑定。</summary>
    /// <param name="sender">事件源。</param>
    /// <param name="args">变化参数。</param>
    private void OnFileChanged(object? sender, PropertyChangedEventArgs args)
        => Post(() =>
        {
            if (!ReferenceEquals(sender, _file)) return;
            UpdateAccessibilityName();
            Invalidate();
        });

    /// <summary>整卡读屏名称：文件名 + 当前星级（组级冲突如实朗读）。</summary>
    private void UpdateAccessibilityName()
    {
        if (_file == null)
        {
            ContentDescription = null;
            return;
        }
        string rating = _file.HasRatingConflict ? "星级冲突" : _file.Rating > 0 ? $"{_file.Rating} 星" : "无星级";
        ContentDescription = $"{_file.DisplayName}，{rating}";
    }

    /// <summary>绘制整卡。</summary>
    /// <param name="canvas">画布。</param>
    protected override void OnDraw(Canvas canvas)
    {
        base.OnDraw(canvas);
        if ( _file == null) return;
        if (_tier == FilmCardTier.Small)
        {
            DrawSmall(canvas);
            return;
        }
        float density = (float)AndroidUiTheme.Density(Context!);
        float width = Width, height = Height;
        // 卡片内容按 96×144 参考缩放：列布局里卡片视图比内容宽时仍居中。
        float scale = Math.Min(width / (float)(CardWidthDp * density), 1f);
        canvas.Save();
        canvas.Translate((width - (float)(96 * density)) / 2, 0);
        canvas.Scale(scale, scale);
        float d = density;
        var surfaceRect = new RectF((float)(Inset * d), (float)(Inset * d),
            (float)((Inset + SurfaceWidth) * d), (float)((Inset + SurfaceHeight) * d));
        bool selected = _selectByDisplayedImage ? _file.IsCurrentImage : _file.IsCurrent;
        AndroidUiTheme.DrawRounded(canvas, surfaceRect, (float)(DesignTokens.FilmCardCornerRadius * d),
            AndroidUiTheme.CardBackground, selected ? AndroidUiTheme.Accent : null,
            (float)(DesignTokens.FilmCardBorderWidth * d));

        // 照片区 80 方形；缩略图已由共享服务 letterbox 到 80 方形。
        // 80dp 图片在 96dp 卡片内水平居中（左右各 8dp），与标题/星级同轴。
        var imageRect = new RectF((float)(8 * d), (float)(8 * d), (float)(88 * d), (float)(88 * d));
        canvas.Save();
        using var clip = new Path();
        clip.AddRoundRect(imageRect, 4 * d, 4 * d, Path.Direction.Cw!);
        canvas.ClipPath(clip);
        canvas.DrawRect(imageRect, _thumbnailBackground);
        var thumbnail = EnsureThumbnail();
        if (thumbnail != null)
        {
            // 位图可能不是 1:1（HIF/RAW 内嵌预览、非方形缩略图）：等比装入 80 方形，不拉伸。
            var src = new Rect(0, 0, thumbnail.Width, thumbnail.Height);
            float fit = Math.Min(imageRect.Width() / thumbnail.Width, imageRect.Height() / thumbnail.Height);
            float drawWidth = thumbnail.Width * fit, drawHeight = thumbnail.Height * fit;
            var dst = new RectF(imageRect.CenterX() - drawWidth / 2, imageRect.CenterY() - drawHeight / 2,
                imageRect.CenterX() + drawWidth / 2, imageRect.CenterY() + drawHeight / 2);
            using var paint = new Paint(PaintFlags.FilterBitmap) { FilterBitmap = true };
            canvas.DrawBitmap(thumbnail, src, dst, paint);
        }
        else
        {
            string state = _file.IsThumbnailLoading ? "…" : "IMG";
            var placeholderPaint = AndroidUiTheme.TextPaint(Context!, 13, Color.ParseColor("#777777")!, bold: true);
            AndroidUiTheme.DrawCentered(canvas, state, placeholderPaint, imageRect.CenterX(),
                imageRect.CenterY() + placeholderPaint.TextSize * .36f, imageRect.Width() - 6 * d);
        }
        canvas.Restore();
        if (_file.IsInCache)
        {
            // FILM-05：主图已缓存细边取令牌（含 70.6% 不透明度）。
            using var cachePaint = AndroidUiTheme.Paint(
                AndroidUiTheme.FromToken(DesignTokens.ColorFilmCardCachedBorder), Math.Max(1, d));
            canvas.DrawRoundRect(imageRect, 4 * d, 4 * d, cachePaint);
        }

        // 文件名 / 第二行。
        AndroidUiTheme.DrawCentered(canvas, _file.DisplayName, _textPaint, (float)(48 * d),
            (float)(103 * d), (float)(86 * d));
        var secondLineRect = new RectF((float)(8 * d), (float)(106 * d), (float)(88 * d), (float)(117 * d));
        if (_secondLineProgress is { } fraction)
        {
            float barRadius = (float)(DesignTokens.FilmCardSecondLineBarCornerRadius * d);
            canvas.DrawRoundRect(secondLineRect, barRadius, barRadius,
                AndroidUiTheme.Paint(AndroidUiTheme.FromToken(DesignTokens.ColorFilmCardSecondLineTrack)));
            var fill = new RectF((float)(8 * d), (float)(106 * d),
                (float)((8 + Math.Clamp(fraction, 0, 1) * SecondLineWidth) * d), (float)(117 * d));
            using var fillPaint = AndroidUiTheme.Paint(
                AndroidUiTheme.FromToken(DesignTokens.ColorFilmCardSecondLineFill));
            canvas.DrawRoundRect(fill, barRadius, barRadius, fillPaint);
            AndroidUiTheme.DrawCentered(canvas, _secondLineText, _datePaint, (float)(48 * d), (float)(112 * d),
                (float)(80 * d));
        }
        else
        {
            string date = _secondLineText ?? ShotTimeText.FormatShortShotTime(_file.PhotoDate) ?? "";
            AndroidUiTheme.DrawCentered(canvas, date, _datePaint, (float)(48 * d), (float)(115 * d), (float)(80 * d));
        }

        // 抖动徽标。
        if (_file.IsShake == true)
        {
            var badge = new RectF((float)(70 * d), (float)(70 * d), (float)(86 * d), (float)(86 * d));
            AndroidUiTheme.DrawRounded(canvas, badge, 2 * d, AndroidUiTheme.ShakeBadge);
            AndroidUiTheme.DrawCentered(canvas, "抖", _badgeTextPaint, badge.CenterX(),
                badge.CenterY() + _badgeTextPaint.TextSize * .36f, badge.Width());
        }

        // 星级：0 号为清除（组级冲突时为红色警示三角），1–5 号实心/空心星，按当前评分点亮。
        if (_showRating)
        {
            bool conflict = _file.HasRatingConflict;
            for (int rating = 0; rating < 6; rating++)
            {
                float cx = (float)((StarRowLeft + rating * StarSize + StarSize / 2) * d);
                float cy = (float)((StarRowTop + StarSize / 2) * d);
                if (rating == 0)
                {
                    if (conflict)
                    {
                        AndroidUiTheme.DrawGlyphCentered(canvas, AndroidUiTheme.IconGlyph(DesignTokens.IconWarning), _conflictPaint, cx, cy);
                        continue;
                    }
                    AndroidUiTheme.DrawGlyphCentered(canvas, AndroidUiTheme.IconGlyph(DesignTokens.IconClear), _clearPaint, cx, cy);
                    continue;
                }
                bool active = rating <= _file.Rating;
                AndroidUiTheme.DrawGlyphCentered(canvas,
                    AndroidUiTheme.IconGlyph(active ? DesignTokens.IconStarFilled : DesignTokens.IconStar),
                    active ? _starFilled : _starOutline, cx, cy);
            }
        }
        canvas.Restore();
    }

    /// <summary>小档内部节点（卡片根坐标，dp）：占位面、照片区、文件名框与抖动点。</summary>
    internal (LayoutRect Surface, LayoutRect Image, LayoutRect Name, LayoutRect ShakeDot) SmallFramesForChecks()
        => SmallFrames();

    /// <summary>按 token 求小档内部节点，绘制与验收共用同一份几何。</summary>
    private static (LayoutRect Surface, LayoutRect Image, LayoutRect Name, LayoutRect ShakeDot) SmallFrames()
    {
        double inset = DesignTokens.FilmCardSmallInset;
        double thumbInset = DesignTokens.FilmCardSmallThumbInset;
        double thumb = DesignTokens.FilmCardSmallThumbSize;
        double dotInset = DesignTokens.FilmCardSmallShakeDotInset;
        double dot = DesignTokens.FilmCardSmallShakeDotSize;
        var surface = new LayoutRect(inset, inset,
            DesignTokens.FilmCardSmallWidth - inset * 2, DesignTokens.FilmCardSmallHeight - inset * 2);
        var image = new LayoutRect(surface.X + thumbInset, surface.Y + thumbInset, thumb, thumb);
        var name = new LayoutRect(surface.X, surface.Y + DesignTokens.FilmCardSmallNameY,
            surface.Width, DesignTokens.FilmCardSmallNameHeight);
        var shake = new LayoutRect(image.X + thumb - dotInset - dot, image.Y + dotInset, dot, dot);
        return (surface, image, name, shake);
    }

    /// <summary>
    /// 小档卡片（FILM-05）：54 方形照片区、一行文件名、贴照片区右下角的星级数字角标（0 不显示、
    /// 冲突时前置警示三角）与右上角圆形抖动点；节点位置全部取 <c>film.card.small.*</c> 令牌。
    /// </summary>
    /// <param name="canvas">画布。</param>
    private void DrawSmall(Canvas canvas)
    {
        var file = _file!;
        float d = (float)AndroidUiTheme.Density(Context!);
        float width = Width, height = Height;
        float scale = Math.Min(width / (float)(DesignTokens.FilmCardSmallWidth * d),
            height / (float)(DesignTokens.FilmCardSmallHeight * d));
        canvas.Save();
        canvas.Translate((width - (float)(DesignTokens.FilmCardSmallWidth * d) * scale) / 2,
            (height - (float)(DesignTokens.FilmCardSmallHeight * d) * scale) / 2);
        canvas.Scale(scale, scale);
        var (surfaceFrame, imageFrame, nameFrame, shakeFrame) = SmallFrames();
        var surface = ToRectF(surfaceFrame, d);
        bool selected = _selectByDisplayedImage ? file.IsCurrentImage : file.IsCurrent;
        AndroidUiTheme.DrawRounded(canvas, surface, (float)(DesignTokens.FilmCardCornerRadius * d),
            AndroidUiTheme.CardBackground, selected ? AndroidUiTheme.Accent : null,
            (float)(DesignTokens.FilmCardBorderWidth * d));

        var imageRect = ToRectF(imageFrame, d);
        canvas.Save();
        using var clip = new Path();
        clip.AddRoundRect(imageRect, 4 * d, 4 * d, Path.Direction.Cw!);
        canvas.ClipPath(clip);
        canvas.DrawRect(imageRect, _thumbnailBackground);
        var thumbnail = EnsureThumbnail();
        if (thumbnail != null)
        {
            var src = new Rect(0, 0, thumbnail.Width, thumbnail.Height);
            float fit = Math.Min(imageRect.Width() / thumbnail.Width, imageRect.Height() / thumbnail.Height);
            float drawWidth = thumbnail.Width * fit, drawHeight = thumbnail.Height * fit;
            var dst = new RectF(imageRect.CenterX() - drawWidth / 2, imageRect.CenterY() - drawHeight / 2,
                imageRect.CenterX() + drawWidth / 2, imageRect.CenterY() + drawHeight / 2);
            using var paint = new Paint(PaintFlags.FilterBitmap) { FilterBitmap = true };
            canvas.DrawBitmap(thumbnail, src, dst, paint);
        }
        else
        {
            using var placeholderPaint = AndroidUiTheme.TextPaint(Context!, 11, Color.ParseColor("#777777")!, bold: true);
            AndroidUiTheme.DrawCentered(canvas, file.IsThumbnailLoading ? "…" : "IMG", placeholderPaint,
                imageRect.CenterX(), imageRect.CenterY() + placeholderPaint.TextSize * .36f, imageRect.Width() - 4 * d);
        }
        canvas.Restore();
        if (file.IsInCache)
        {
            using var cachePaint = AndroidUiTheme.Paint(
                AndroidUiTheme.FromToken(DesignTokens.ColorFilmCardCachedBorder), Math.Max(1, d));
            canvas.DrawRoundRect(imageRect, 4 * d, 4 * d, cachePaint);
        }

        // 星级角标：金色实心星 + 白色数字，贴照片区右下角；0 星不显示，冲突时前置警示三角。
        if (_showRating)
        {
            double badgeHeight = DesignTokens.FilmCardSmallBadgeHeight;
            float baseline = imageRect.Bottom - (float)(2 * d);
            bool conflict = file.HasRatingConflict;
            float right = imageRect.Right - (float)(2 * d);
            if (conflict)
            {
                string glyph = AndroidUiTheme.IconGlyph(DesignTokens.IconWarning);
                AndroidUiTheme.DrawGlyphCentered(canvas, glyph, _smallConflictPaint,
                    right - (float)(8 * d), baseline - (float)(badgeHeight * d) / 2);
                right -= (float)(10 * d);
            }
            if (file.Rating > 0)
            {
                AndroidUiTheme.DrawGlyphCentered(canvas, AndroidUiTheme.IconGlyph(DesignTokens.IconStarFilled),
                    _smallStarPaint, right - (float)(10 * d), baseline - (float)(badgeHeight * d) / 2);
                _smallBadgePaint.TextAlign = Paint.Align.Right;
                _smallBadgePaint.TextSize = AndroidUiTheme.Sp(Context!, DesignTokens.FilmCardSmallBadgeFontSize);
                canvas.DrawText(file.Rating.ToString(), right, baseline, _smallBadgePaint);
                _smallBadgePaint.TextAlign = Paint.Align.Center;
            }
        }

        // 抖动点：圆形，贴照片区右上角内缩。
        if (file.IsShake == true)
        {
            using var dotPaint = AndroidUiTheme.Paint(AndroidUiTheme.ShakeBadge);
            canvas.DrawOval(ToRectF(shakeFrame, d), dotPaint);
        }

        // 文件名：单行、半粗、尾部截断，位于照片区下方。
        _smallNamePaint.TextSize = AndroidUiTheme.Sp(Context!, DesignTokens.FilmCardSmallNameFontSize);
        AndroidUiTheme.DrawCentered(canvas, file.DisplayName, _smallNamePaint,
            (float)((nameFrame.X + nameFrame.Width / 2) * d),
            (float)((nameFrame.Y + nameFrame.Height * .78) * d),
            (float)(nameFrame.Width * d));
        canvas.Restore();
    }

    /// <summary>dp 矩形转像素矩形（小档绘制共用）。</summary>
    private static RectF ToRectF(LayoutRect rect, float density) => new(
        (float)(rect.X * density), (float)(rect.Y * density),
        (float)(rect.MaxX * density), (float)(rect.MaxY * density));

    /// <summary>取得缩略图位图（引用变了才重转）。</summary>
    /// <returns>位图或 null。</returns>
    private Bitmap? EnsureThumbnail()
    {
        if (_file == null) return null;
        var image = _file.Thumbnail;
        if (image == null)
        {
            _thumbnail = null;
            _thumbnailSource = null;
            return null;
        }
        if (ReferenceEquals(image, _thumbnailSource) && _thumbnail != null) return _thumbnail;
        if (!image.TryAcquire()) return _thumbnail;
        try
        {
            _thumbnail = AndroidBitmapConverter.FromDecodedImage(_file.File.StorageId.Value, image);
            _thumbnailSource = image;
            return _thumbnail;
        }
        finally { image.Release(); }
    }

    /// <summary>
    /// 触摸：单击命中星级或整卡；长按打开星级弹窗，此后同一次触摸的移动/抬手/取消都转发给弹窗，
    /// 不再切图、不再命中星级。
    /// </summary>
    /// <param name="e">事件。</param>
    /// <returns>消费。</returns>
    public override bool OnTouchEvent(MotionEvent? e)
    {
        if (e == null || _file == null) return false;
        if (e.ActionMasked == MotionEventActions.Down)
        {
            _popupGestureActive = false;
            _touchFile = _file;
        }
        if (_popupGestureActive)
        {
            switch (e.ActionMasked)
            {
                case MotionEventActions.Move:
                    _popupHost?.TrackCardRatingPopup(this, e, CardRatingPopupPhase.Moved);
                    return true;
                case MotionEventActions.Up:
                    _popupGestureActive = false;
                    _popupHost?.TrackCardRatingPopup(this, e, CardRatingPopupPhase.Ended);
                    return true;
                case MotionEventActions.Cancel:
                    _popupGestureActive = false;
                    _popupHost?.TrackCardRatingPopup(this, e, CardRatingPopupPhase.Cancelled);
                    return true;
                default:
                    return true;
            }
        }
        _gestures.OnTouchEvent(e);
        if (e.ActionMasked == MotionEventActions.Up && ReferenceEquals(_touchFile, _file))
        {
            int slot = SlotAtPx(e.GetX(), e.GetY());
            if (slot == 0 && _file.HasRatingConflict)
            {
                ActivateSlot(slot);
                return true;
            }
            if (e.GetToolType(0) == MotionEventToolType.Mouse && slot >= 0)
            {
                ActivateSlot(slot);
                return true;
            }
            PerformClick();
        }
        return true;
    }

    public override bool OnGenericMotionEvent(MotionEvent? e)
        => (e != null && _gestures.OnGenericMotionEvent(e)) || base.OnGenericMotionEvent(e);

    /// <summary>
    /// 触点命中的星级槽位（含列布局的水平居中偏移）；未命中返回 -1。
    /// 与绘制、<see cref="StarSlotRectPx"/> 共用 <see cref="StarRowLeft"/> / <see cref="StarRowTop"/>。
    /// </summary>
    /// <param name="x">触点横坐标（px）。</param>
    /// <param name="y">触点纵坐标（px）。</param>
    /// <returns>0–5 或 -1。</returns>
    internal int SlotAtPx(float x, float y)
    {
        if (!_showRating || Width < 1 || _tier != FilmCardTier.Large) return -1;
        float density = (float)AndroidUiTheme.Density(Context!);
        float scale = Math.Min(Width / (float)(CardWidthDp * density), 1f);
        if (scale <= 0) return -1;
        float originX = (Width - (float)(96 * density)) / 2;
        float localX = (x - originX) / scale, localY = y / scale;
        float starLeft = (float)(StarRowLeft * density);
        float starTop = (float)(StarRowTop * density);
        float starBottom = (float)((StarRowTop + StarSize) * density);
        float starRight = starLeft + (float)(6 * StarSize * density);
        if (localY < starTop || localY > starBottom || localX < starLeft || localX > starRight) return -1;
        return Math.Clamp((int)((localX - starLeft) / (float)(StarSize * density)), 0, 5);
    }

    /// <summary>
    /// 槽位动作：普通槽位写回评分；冲突时的 0 号只打开星级弹窗的来源条目，不写值、不切图（RATING-04）。
    /// 触摸与读屏虚拟动作共用本入口。
    /// </summary>
    /// <param name="rating">0–5。</param>
    /// <returns>是否执行了动作。</returns>
    internal bool ActivateSlot(int rating)
    {
        if (_file == null || !_showRating || rating < 0 || rating > 5) return false;
        if (rating == 0 && _file.HasRatingConflict)
        {
            _popupHost?.OpenCardRatingPopup(this);
            return true;
        }
        _rate?.Invoke(_file, rating);
        return true;
    }

    /// <summary>卡片外框在屏幕上的矩形（星级弹窗的真实入口）。</summary>
    internal Rect? ScreenRect
    {
        get
        {
            if (!IsAttachedToWindow || !IsShown || Width < 1 || Height < 1) return null;
            var location = new int[2];
            GetLocationOnScreen(location);
            return new Rect(location[0], location[1], location[0] + Width, location[1] + Height);
        }
    }

    /// <summary>释放订阅。</summary>
    /// <param name="disposing">是否释放托管资源。</param>
    protected override void Dispose(bool disposing)
    {
        if (disposing && !_disposed)
        {
            _disposed = true;
            Recycle();
        }
        base.Dispose(disposing);
    }

    /// <summary>
    /// 卡片槽位的读屏节点（RATING-04）：0 号冲突时名称为「星级冲突」，点按走与触摸相同的
    /// <see cref="ActivateSlot"/>，因此读屏也能打开星级弹窗而不写值。
    /// </summary>
    private sealed class CardExploreByTouchHelper : ExploreByTouchHelper
    {
        private readonly AndroidThumbnailCard _owner;

        /// <summary>绑定卡片。</summary>
        /// <param name="owner">卡片视图。</param>
        public CardExploreByTouchHelper(AndroidThumbnailCard owner) : base(owner) => _owner = owner;

        /// <summary>按本地像素坐标找槽位。</summary>
        protected override int GetVirtualViewAt(float x, float y) => _owner.SlotAtPx(x, y);

        /// <summary>星级可见时列出 0–5 六个槽位。</summary>
        protected override void GetVisibleVirtualViews(IList<Java.Lang.Integer>? virtualViewIds)
        {
            if (virtualViewIds == null || _owner._file == null || !_owner._showRating) return;
            for (int rating = 0; rating < 6; rating++) virtualViewIds.Add(Java.Lang.Integer.ValueOf(rating));
        }

        /// <summary>填充名称、选中态与屏幕边界。</summary>
        protected override void OnPopulateNodeForVirtualView(int virtualViewId, AccessibilityNodeInfoCompat? node)
        {
            if (node == null) return;
            var file = _owner._file;
            if (file == null || virtualViewId < 0 || virtualViewId > 5 || !_owner._showRating)
            {
                node.ContentDescription = "";
                node.SetBoundsInScreen(new Rect());
                return;
            }
            bool conflict = file.HasRatingConflict;
            node.ContentDescription = virtualViewId == 0
                ? (conflict ? "星级冲突" : "无星级")
                : $"{virtualViewId} 星";
            node.ClassName = "android.widget.Button";
            node.Focusable = true;
            node.Enabled = true;
            node.Clickable = true;
            node.VisibleToUser = true;
            node.Selected = virtualViewId > 0 && file.Rating == virtualViewId;
            node.AddAction((int)global::Android.Views.Accessibility.Action.Click);
            if (_owner.StarSlotRectPx(virtualViewId) is { } rect)
            {
                var location = new int[2];
                _owner.GetLocationOnScreen(location);
                node.SetBoundsInScreen(new Rect(location[0] + rect.Left, location[1] + rect.Top,
                    location[0] + rect.Right, location[1] + rect.Bottom));
            }
            else
            {
                node.SetBoundsInScreen(new Rect());
            }
        }

        /// <summary>读屏点按：与触摸同一槽位入口（冲突三角只开浮窗、不写值）。</summary>
        protected override bool OnPerformActionForVirtualView(int virtualViewId, int action, Bundle? arguments)
        {
            if (action != (int)global::Android.Views.Accessibility.Action.Click) return false;
            return _owner.ActivateSlot(virtualViewId);
        }
    }

    /// <summary>卡片手势回调。</summary>
    private sealed class CardGestureListener : GestureDetector.SimpleOnGestureListener
    {
        private readonly AndroidThumbnailCard _owner;

        /// <summary>绑定卡片。</summary>
        /// <param name="owner">卡片视图。</param>
        public CardGestureListener(AndroidThumbnailCard owner) => _owner = owner;

        /// <summary>单击：由卡片的触摸命中逻辑处理（这里只消费事件，避免系统再合成点击）。</summary>
        /// <param name="e">事件。</param>
        /// <returns>消费。</returns>
        public override bool OnDown(MotionEvent? e) => true;

        public override bool OnSingleTapUp(MotionEvent? e) => true;

        public override bool OnContextClick(MotionEvent? e)
        {
            if (_owner._file == null || _owner._popupHost == null) return false;
            _owner._popupHost.OpenCardRatingPopup(_owner);
            return true;
        }

        /// <summary>
        /// 长按：打开星级弹窗并把这次触摸继续交给它（卡片没有独立评分菜单）。
        /// 星级被关闭时没有入口，仍吃掉这次长按，不让抬手变成点选。
        /// </summary>
        /// <param name="e">长按确认时的触摸事件。</param>
        public override void OnLongPress(MotionEvent? e)
        {
            if (e == null || _owner._file == null || !ReferenceEquals(_owner._file, _owner._touchFile)) return;
            _owner._popupGestureActive = true;
            if (!_owner._showRating) return;
            // 长按已起手弹窗预览：胶片栏不再把随后的拖动当作滚动。
            _owner.Parent?.RequestDisallowInterceptTouchEvent(true);
            _owner._popupHost?.TrackCardRatingPopup(_owner, e, CardRatingPopupPhase.Began);
        }
    }
}
