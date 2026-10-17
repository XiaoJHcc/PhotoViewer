using System;
using System.ComponentModel;
using Android.Content;
using Android.Graphics;
using Android.Views;
using Android.Widget;
using PhotoViewer.Core.Image;
using PhotoViewer.ViewModels.Main;

namespace PhotoViewer.Android.Native;

/// <summary>
/// 胶片栏卡片（A3，[FILM-05]）：96×144 外框、90×138 占位区、80 方形照片区、文件名、拍摄时间、
/// 6 个星级、抖动徽标、当前项蓝框与 Full 缓存细边；整卡自绘，尺寸与 iOS 原生壳同口径。
/// 星级命中优先于整卡选择（点星不切图）。
/// </summary>
internal sealed class AndroidThumbnailCard : View
{
    private const double Inset = 3, SurfaceWidth = 90, SurfaceHeight = 138, ImageSize = 80, StarSize = 14;
    private const double SecondLineWidth = 80;
    private readonly Paint _surfacePaint;
    private readonly Paint _thumbnailBackground;
    private readonly Paint _textPaint;
    private readonly Paint _datePaint;
    private readonly Paint _starPaint;
    private readonly Paint _starFillPaint;
    private readonly Paint _badgePaint;
    private readonly Paint _badgeTextPaint;
    private readonly Paint _linePaint;
    private readonly GestureDetector _gestures;
    private ImageFile? _file;
    private Bitmap? _thumbnail;
    private DecodedImage? _thumbnailSource;
    private Action<ImageFile>? _select;
    private Action<ImageFile, int>? _rate;
    private bool _showRating = true;
    private string? _secondLineText;
    private double? _secondLineProgress;
    private bool _selectByDisplayedImage;
    private bool _disposed;

    /// <summary>当前绑定的业务身份（验收核对复用不错位）。</summary>
    internal ImageFile? BoundFile => _file;
    /// <summary>卡片外框宽度（dp）。</summary>
    internal static double CardWidthDp => 96;
    /// <summary>卡片外框高度（dp）。</summary>
    internal static double CardHeightDp => 144;
    /// <summary>第二行文字（相似聚类为百分比；null 表示拍摄时间）。</summary>
    internal string? SecondLineText => _secondLineText;

    /// <summary>创建卡片并建好画笔/手势识别。</summary>
    /// <param name="context">宿主上下文。</param>
    public AndroidThumbnailCard(Context context) : base(context)
    {
        _surfacePaint = AndroidUiTheme.Paint(AndroidUiTheme.ControlFill);
        _thumbnailBackground = AndroidUiTheme.Paint(Color.ParseColor("#333333")!);
        _textPaint = AndroidUiTheme.TextPaint(context, 10, AndroidUiTheme.TextPrimary, bold: true);
        _datePaint = AndroidUiTheme.TextPaint(context, 9, AndroidUiTheme.TextPrimary, bold: true);
        _starPaint = AndroidUiTheme.Paint(AndroidUiTheme.StarInactive, Math.Max(1, AndroidUiTheme.Dp(context, 1)));
        _starPaint.SetStyle(Paint.Style.Stroke);
        _starFillPaint = AndroidUiTheme.Paint(AndroidUiTheme.StarActive);
        _badgePaint = AndroidUiTheme.Paint(AndroidUiTheme.ShakeBadge);
        _badgeTextPaint = AndroidUiTheme.TextPaint(context, 11, Color.White, bold: true);
        _linePaint = AndroidUiTheme.Paint(Color.ParseColor("#444444")!, Math.Max(1, AndroidUiTheme.Dp(context, 1)));
        _gestures = new GestureDetector(context, new CardGestureListener(this));
        Focusable = true;
        Clickable = true;
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
    internal void Bind(ImageFile file, bool showRating, Action<ImageFile> select, Action<ImageFile, int> rate,
        string? secondLineText = null, double? secondLineProgress = null, bool selectByDisplayedImage = false)
    {
        if (_file != null) _file.PropertyChanged -= OnFileChanged;
        _file = file;
        _select = select;
        _rate = rate;
        _showRating = showRating;
        _secondLineText = secondLineText;
        _secondLineProgress = secondLineProgress;
        _selectByDisplayedImage = selectByDisplayedImage;
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
        AndroidUiTheme.DrawRounded(canvas, surfaceRect, 7 * d, AndroidUiTheme.ControlFill,
            selected ? AndroidUiTheme.Accent : null, 2 * d);

        // 照片区 80 方形；缩略图已由共享服务 letterbox 到 80 方形。
        var imageRect = new RectF((float)(5 * d), (float)(5 * d), (float)(85 * d), (float)(85 * d));
        canvas.Save();
        using var clip = new Path();
        clip.AddRoundRect(imageRect, 4 * d, 4 * d, Path.Direction.Cw!);
        canvas.ClipPath(clip);
        canvas.DrawRect(imageRect, _thumbnailBackground);
        var thumbnail = EnsureThumbnail();
        if (thumbnail != null)
        {
            var src = new Rect(0, 0, thumbnail.Width, thumbnail.Height);
            canvas.DrawBitmap(thumbnail, src, imageRect, null);
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
            var cachePaint = AndroidUiTheme.Paint(Color.ParseColor("#4682B4")!, Math.Max(1, d));
            canvas.DrawRoundRect(imageRect, 4 * d, 4 * d, cachePaint);
        }

        // 文件名 / 第二行。
        AndroidUiTheme.DrawCentered(canvas, _file.DisplayName, _textPaint, (float)(48 * d),
            (float)(100 * d), (float)(86 * d));
        var secondLineRect = new RectF((float)(5 * d), (float)(103 * d), (float)(85 * d), (float)(114 * d));
        if (_secondLineProgress is { } fraction)
        {
            canvas.DrawRoundRect(secondLineRect, 4 * d, 4 * d, AndroidUiTheme.Paint(Color.ParseColor("#444444")!));
            var fill = new RectF((float)(5 * d), (float)(103 * d),
                (float)((5 + Math.Clamp(fraction, 0, 1) * SecondLineWidth) * d), (float)(114 * d));
            using var fillPaint = AndroidUiTheme.Paint(Color.ParseColor("#1E90FF")!);
            fillPaint.Alpha = 153;
            canvas.DrawRoundRect(fill, 4 * d, 4 * d, fillPaint);
            AndroidUiTheme.DrawCentered(canvas, _secondLineText, _datePaint, (float)(45 * d), (float)(112 * d),
                (float)(74 * d));
        }
        else
        {
            string date = _secondLineText ?? _file.PhotoDate?.ToString("MM-dd HH:mm") ?? "";
            AndroidUiTheme.DrawCentered(canvas, date, _datePaint, (float)(45 * d), (float)(112 * d), (float)(80 * d));
        }

        // 抖动徽标。
        if (_file.IsShake == true)
        {
            var badge = new RectF((float)(67 * d), (float)(67 * d), (float)(83 * d), (float)(83 * d));
            AndroidUiTheme.DrawRounded(canvas, badge, 2 * d, AndroidUiTheme.ShakeBadge);
            AndroidUiTheme.DrawCentered(canvas, "抖", _badgeTextPaint, badge.CenterX(),
                badge.CenterY() + _badgeTextPaint.TextSize * .36f, badge.Width());
        }

        // 星级：0 号为清除，1–5 号实心/空心星，按当前评分点亮。
        if (_showRating)
        {
            for (int rating = 0; rating < 6; rating++)
            {
                float cx = (float)((Inset + rating * StarSize + StarSize / 2) * d);
                float cy = (float)((116 + StarSize / 2) * d);
                if (rating == 0)
                {
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
            _thumbnail = AndroidBitmapConverter.FromDecodedImage(_file.File.PathKey, image);
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
        _gestures.OnTouchEvent(e);
        if (e.ActionMasked == MotionEventActions.Up)
        {
            float density = (float)AndroidUiTheme.Density(Context!);
            float x = e.GetX(), y = e.GetY();
            float starLeft = (float)(Inset * density);
            float starTop = (float)(116 * density);
            float starBottom = (float)((116 + StarSize) * density);
            if (_showRating && y >= starTop && y <= starBottom && x >= starLeft && x <= starLeft + (float)(6 * StarSize * density))
            {
                int rating = (int)((x - starLeft) / (float)(StarSize * density));
                rating = Math.Clamp(rating, 0, 5);
                _rate?.Invoke(_file, rating);
                return true;
            }
            _select?.Invoke(_file);
        }
        return true;
    }

    /// <summary>长按弹卡片菜单（完整文件名 + 评分 0–5）。</summary>
    internal void ShowCardMenu()
    {
        if (_file == null) return;
        var menu = new PopupMenu(Context!, this);
        var menuItems = menu.Menu!;
        menuItems.Add(_file.DisplayName);
        menuItems.Add(_file.PhotoDate?.ToString("yyyy-MM-dd HH:mm") ?? "");
        for (int rating = 0; rating <= 5; rating++)
        {
            int value = rating;
            var item = menuItems.Add(rating == 0 ? "无星级" : $"{rating} 星")!;
            item.SetCheckable(true);
            item.SetChecked(_file.Rating == rating);
            menu.MenuItemClick += (_, args) =>
            {
                if ((args.Item?.ItemId ?? -1) >= 2) _rate?.Invoke(_file, value);
            };
        }
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

        /// <summary>长按：弹评分菜单。</summary>
        /// <param name="e">事件。</param>
        public override void OnLongPress(MotionEvent? e) => _owner.ShowCardMenu();
    }
}
