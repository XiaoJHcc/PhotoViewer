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
using PhotoViewer.ViewModels.Main;

namespace PhotoViewer.Android.Native;

/// <summary>
/// 胶片栏卡片（A3，[FILM-05]）：96×144 外框、90×138 占位区、80 方形照片区、文件名、拍摄时间、
/// 6 个星级、抖动徽标、当前项蓝框与 Full 缓存细边；整卡自绘，尺寸与 iOS 原生壳同口径。
/// 星级命中优先于整卡选择（点星不切图）；组级冲突时 0 号换警示三角，点击只打开来源浮窗（RATING-04）。
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
    private readonly Paint _starPaint;
    private readonly Paint _starFillPaint;
    private readonly Paint _badgePaint;
    private readonly Paint _badgeTextPaint;
    private readonly Paint _linePaint;
    private readonly Paint _conflictPaint;
    private readonly GestureDetector _gestures;
    private readonly CardExploreByTouchHelper _accessibility;
    private ImageFile? _file;
    private Bitmap? _thumbnail;
    private DecodedImage? _thumbnailSource;
    private Action<ImageFile>? _select;
    private Action<ImageFile, int>? _rate;
    private Action<ImageFile>? _showSources;
    private bool _showRating = true;
    private string? _secondLineText;
    private double? _secondLineProgress;
    private bool _selectByDisplayedImage;
    private bool _longPressConsumed;
    private bool _disposed;

    /// <summary>当前绑定的业务身份（验收核对复用不错位）。</summary>
    internal ImageFile? BoundFile => _file;
    /// <summary>卡片外框宽度（dp）。</summary>
    internal static double CardWidthDp => 96;
    /// <summary>卡片外框高度（dp）。</summary>
    internal static double CardHeightDp => 144;
    /// <summary>第二行文字（相似聚类为百分比；null 表示拍摄时间）。</summary>
    internal string? SecondLineText => _secondLineText;
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
        _starPaint = AndroidUiTheme.Paint(AndroidUiTheme.StarInactive, Math.Max(1, AndroidUiTheme.Dp(context, 1)));
        _starPaint.SetStyle(Paint.Style.Stroke);
        _starFillPaint = AndroidUiTheme.Paint(AndroidUiTheme.StarActive);
        _badgePaint = AndroidUiTheme.Paint(AndroidUiTheme.ShakeBadge);
        _badgeTextPaint = AndroidUiTheme.TextPaint(context, DesignTokens.FilmCardBadgeFontSize, Color.White, bold: true);
        // FILM-05：清零叉使用中性图形色，不随评分点亮。
        _linePaint = AndroidUiTheme.Paint(AndroidUiTheme.StarClear, Math.Max(1, AndroidUiTheme.Dp(context, 1)));
        _conflictPaint = AndroidUiTheme.Paint(AndroidUiTheme.StarConflict);
        _gestures = new GestureDetector(context, new CardGestureListener(this));
        Focusable = true;
        Clickable = true;
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
    /// <param name="showSources">冲突三角点击：打开该照片的来源浮窗。</param>
    internal void Bind(ImageFile file, bool showRating, Action<ImageFile> select, Action<ImageFile, int> rate,
        string? secondLineText = null, double? secondLineProgress = null, bool selectByDisplayedImage = false,
        Action<ImageFile>? showSources = null)
    {
        if (_file != null) _file.PropertyChanged -= OnFileChanged;
        _file = file;
        _select = select;
        _rate = rate;
        _showSources = showSources;
        _showRating = showRating;
        _secondLineText = secondLineText;
        _secondLineProgress = secondLineProgress;
        _selectByDisplayedImage = selectByDisplayedImage;
        _longPressConsumed = false;
        _thumbnail = null;
        _thumbnailSource = null;
        file.PropertyChanged += OnFileChanged;
        Invalidate();
    }

    /// <summary>复用/回收前解除订阅与位图引用。</summary>
    internal void Recycle()
    {
        if (_file != null) _file.PropertyChanged -= OnFileChanged;
        _file = null;
        _select = null;
        _rate = null;
        _showSources = null;
        _thumbnail = null;
        _thumbnailSource = null;
    }

    /// <summary>模型事件封送主线程，消费前核对复用后的绑定。</summary>
    /// <param name="sender">事件源。</param>
    /// <param name="args">变化参数。</param>
    private void OnFileChanged(object? sender, PropertyChangedEventArgs args)
        => Post(() => { if (ReferenceEquals(sender, _file)) Invalidate(); });

    /// <summary>绘制整卡。</summary>
    /// <param name="canvas">画布。</param>
    protected override void OnDraw(Canvas canvas)
    {
        base.OnDraw(canvas);
        if ( _file == null) return;
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
            var cachePaint = AndroidUiTheme.Paint(
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
            string date = _secondLineText ?? _file.PhotoDate?.ToString("MM-dd HH:mm") ?? "";
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
                        using var warning = AndroidUiTheme.WarningTriangle(cx, cy, 11f * d);
                        canvas.DrawPath(warning, _conflictPaint);
                        continue;
                    }
                    float arm = 3.2f * d;
                    canvas.DrawLine(cx - arm, cy - arm, cx + arm, cy + arm, _linePaint);
                    canvas.DrawLine(cx - arm, cy + arm, cx + arm, cy - arm, _linePaint);
                    continue;
                }
                bool active = rating <= _file.Rating;
                using var star = StarPath(cx, cy, 5.4f * d, 2.5f * d);
                if (active) canvas.DrawPath(star, _starFillPaint);
                else canvas.DrawPath(star, _starPaint);
            }
        }
        canvas.Restore();
    }

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

    /// <summary>生成五角星路径。</summary>
    private static Path StarPath(float cx, float cy, float outer, float inner)
    {
        var path = new Path();
        for (int i = 0; i < 10; i++)
        {
            double angle = -Math.PI / 2 + i * Math.PI / 5;
            float radius = i % 2 == 0 ? outer : inner;
            float x = (float)(cx + Math.Cos(angle) * radius);
            float y = (float)(cy + Math.Sin(angle) * radius);
            if (i == 0) path.MoveTo(x, y);
            else path.LineTo(x, y);
        }
        path.Close();
        return path;
    }

    /// <summary>触摸：单击命中星级或整卡，长按弹评分菜单。</summary>
    /// <param name="e">事件。</param>
    /// <returns>消费。</returns>
    public override bool OnTouchEvent(MotionEvent? e)
    {
        if (e == null || _file == null) return false;
        if (e.ActionMasked == MotionEventActions.Down) _longPressConsumed = false;
        _gestures.OnTouchEvent(e);
        if (e.ActionMasked == MotionEventActions.Cancel) return true;
        if (e.ActionMasked == MotionEventActions.Up)
        {
            // MENU-05：长按只弹菜单，抬手不再被点选或星级命中消费。
            if (_longPressConsumed)
            {
                _longPressConsumed = false;
                return true;
            }
            int slot = SlotAtPx(e.GetX(), e.GetY());
            if (slot >= 0)
            {
                ActivateSlot(slot);
                return true;
            }
            _select?.Invoke(_file);
        }
        return true;
    }

    /// <summary>
    /// 触点命中的星级槽位（含列布局的水平居中偏移）；未命中返回 -1。
    /// 与绘制、<see cref="StarSlotRectPx"/> 共用 <see cref="StarRowLeft"/> / <see cref="StarRowTop"/>。
    /// </summary>
    /// <param name="x">触点横坐标（px）。</param>
    /// <param name="y">触点纵坐标（px）。</param>
    /// <returns>0–5 或 -1。</returns>
    internal int SlotAtPx(float x, float y)
    {
        if (!_showRating || Width < 1) return -1;
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
    /// 槽位动作：普通槽位写回评分；冲突时的 0 号只打开来源浮窗，不写值、不切图（RATING-04）。
    /// 触摸与读屏虚拟动作共用本入口。
    /// </summary>
    /// <param name="rating">0–5。</param>
    /// <returns>是否执行了动作。</returns>
    internal bool ActivateSlot(int rating)
    {
        if (_file == null || !_showRating || rating < 0 || rating > 5) return false;
        if (rating == 0 && _file.HasRatingConflict)
        {
            _showSources?.Invoke(_file);
            return true;
        }
        _rate?.Invoke(_file, rating);
        return true;
    }

    /// <summary>长按弹卡片菜单（完整文件名 + 评分 0–5）。</summary>
    internal void ShowCardMenu()
    {
        if (_file == null) return;
        var menu = new PopupMenu(Context!, this);
        var menuItems = menu.Menu!;
        // MENU-05：标题一行承载完整文件名与拍摄时间，标题行不可点击。
        var title = menuItems.Add(0, 0, 0, _file.DisplayName)!;
        title.SetEnabled(false);
        var time = menuItems.Add(0, 1, 1,
            _file.PhotoDate?.ToString("yyyy-MM-dd HH:mm:ss zzz") ?? "")!;
        time.SetEnabled(false);
        for (int rating = 0; rating <= 5; rating++)
        {
            var item = menuItems.Add(0, 2 + rating, 2 + rating, rating == 0 ? "无星级" : $"{rating} 星")!;
            item.SetCheckable(true);
            item.SetChecked(_file.Rating == rating);
        }
        menu.MenuItemClick += (_, args) =>
        {
            int id = args.Item?.ItemId ?? -1;
            if (id >= 2) _rate?.Invoke(_file, id - 2);
        };
        menu.Show();
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
    /// <see cref="ActivateSlot"/>，因此读屏也能打开来源浮窗而不写值。
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
        public override bool OnSingleTapUp(MotionEvent? e) => true;

        /// <summary>长按：弹评分菜单，并让本次抬手不再触发点选或改星（MENU-05）。</summary>
        /// <param name="e">事件。</param>
        public override void OnLongPress(MotionEvent? e)
        {
            _owner._longPressConsumed = true;
            _owner.ShowCardMenu();
        }
    }
}
