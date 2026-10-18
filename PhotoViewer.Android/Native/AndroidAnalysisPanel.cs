using System;
using System.Collections.Generic;
using System.Linq;
using Android.Content;
using Android.Graphics;
using Android.OS;
using Android.Views;
using PhotoViewer.Core.AI;
using PhotoViewer.Core.Image;
using PhotoViewer.Shared;
using PhotoViewer.ViewModels.Main;

namespace PhotoViewer.Android.Native;

/// <summary>
/// Android 分析栏宿主（A5）：把主 VM 的可见性、即时计算开关、当前图与展示会话的图像上报共享
/// <see cref="AnalysisPresenter"/>，与旧壳/macOS/iPad/Windows 共用同一份编排。
/// </summary>
internal sealed class AndroidAnalysisHost : IAnalysisHost
{
    private readonly MainViewModel _main;
    private readonly PhotoLoadSession _session;

    /// <summary>绑定主 VM 与展示会话。</summary>
    /// <param name="main">共享主 VM。</param>
    /// <param name="session">展示加载会话。</param>
    public AndroidAnalysisHost(MainViewModel main, PhotoLoadSession session)
    {
        _main = main;
        _session = session;
    }

    /// <summary>分析栏是否可见。</summary>
    public bool AnalysisVisible { get; private set; }
    /// <summary>即时计算开关（设置页）。</summary>
    public bool ImmediateCompute => _main.Settings.AnalysisImmediateCompute;
    /// <summary>当前照片。</summary>
    public ImageFile? CurrentFile => _main.CurrentFile;
    /// <summary>当前展示图（原片或增强图）。</summary>
    public DecodedImage? DisplayImage => _session.Image;
    /// <summary>当前展示图是否增强图。</summary>
    public bool EnhancedDisplay => _session.EnhancedDisplay;
    /// <summary>缩略图通道是否忙（未提取回退让位）。</summary>
    public bool ThumbnailsBusy => _main.FileVM.ThumbnailList.IsThumbnailLoadingBusy();

    /// <summary>宿主状态变化。</summary>
    public event Action? HostChanged;

    /// <summary>更新可见性并通知 presenter。</summary>
    /// <param name="visible">是否可见。</param>
    public void SetVisible(bool visible)
    {
        if (AnalysisVisible == visible) return;
        AnalysisVisible = visible;
        Raise();
    }

    /// <summary>通知 presenter 重新路由（切图/展示图变化/设置变化）。</summary>
    public void Raise() => HostChanged?.Invoke();
}

/// <summary>
/// Android 分析栏（A5）：按共享 <see cref="AnalysisPanelGeometry"/> 排布中心/对焦细节预览、直方图与
/// 4 张 DINO-CV 诊断瓦片；瓦片自绘，细节裁剪走共享 <see cref="DisplayCrop"/>（代理期留空）。
/// 诊断瓦片点按落准星并重算 cosine；细节预览点按定格、再点或点主图空白解除。
/// </summary>
internal sealed class AndroidAnalysisPanel : View
{
    private const int TileRadiusDp = 6;
    private const int ContentInsetDp = 1;

    private readonly AnalysisPresenter _presenter;
    private readonly PhotoLoadSession _session;
    private readonly Paint _tileFill;
    private readonly Paint _tileBorder;
    private readonly Paint _textPaint;
    private readonly Paint _labelPaint;
    private readonly Paint _placeholderPaint;
    private readonly Paint _crosshairPaint;
    private readonly Paint _focusPaint;
    private readonly Dictionary<DecodedImage, Bitmap> _images = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<AnalysisTile, LayoutRect> _frames = [];
    private readonly Dictionary<AnalysisTile, int> _frameOrder = [];
    private bool _horizontal = true;
    private double _tileSize;
    private double _availableMain;
    private double _startInset;
    private bool _disposed;

    private AnalysisTile? _detailTile;
    private DecodedImage? _histogramLogged;
    private AnalysisTile? _pendingDetailTile;
    private long _lastDetailTapTime;
    private bool _detailActive;
    private Bitmap? _detailBitmap;
    private (DecodedImage? Source, double U, double V, int Pixels) _detailKey;

    /// <summary>瓦片数量变化（对焦点出现/消失）时通知宿主重新分区。</summary>
    internal event Action? TileCountChanged;
    /// <summary>细节预览定格/解除后通知宿主更新主图绿框。</summary>
    internal event Action? DetailChanged;
    /// <summary>细节预览双击：把主图定位到该瓦片中心并放大到物理 100%。</summary>
    internal event Action<double, double>? DetailFocusRequested;

    /// <summary>瓦片数量（当前 presenter 顺序）。</summary>
    internal int TileCount => _presenter.Tiles.Count;
    /// <summary>当前细节预览是否定格。</summary>
    internal bool DetailActive => _detailActive;
    /// <summary>定格瓦片的裁剪中心（归一化），不取手指在瓦片内的落点。</summary>
    internal (double U, double V) DetailPoint => _detailTile is { } tile ? (tile.CenterU, tile.CenterV) : (.5, .5);
    /// <summary>定格瓦片的 Sony 对焦框宽（归一化）；无对焦框为 null。</summary>
    internal double? DetailFrameWidth => _detailTile?.FocusFrameWidth;
    /// <summary>定格瓦片的 Sony 对焦框高（归一化）；无对焦框为 null。</summary>
    internal double? DetailFrameHeight => _detailTile?.FocusFrameHeight;
    /// <summary>细节裁剪方形边长（源像素）；主图绿框按它乘当前视口缩放。</summary>
    internal int CropPixels => Math.Max(1, (int)Math.Round(_tileSize * AndroidUiTheme.Density(Context!)));
    /// <summary>当前瓦片边长（dp，验收读取）。</summary>
    internal double TileSize => _tileSize;

    /// <summary>建立分析栏并订阅 presenter。</summary>
    /// <param name="context">宿主上下文。</param>
    /// <param name="presenter">共享分析 presenter。</param>
    /// <param name="session">展示加载会话。</param>
    public AndroidAnalysisPanel(Context context, AnalysisPresenter presenter, PhotoLoadSession session) : base(context)
    {
        _presenter = presenter;
        _session = session;
        _tileFill = AndroidUiTheme.Paint(Color.ParseColor("#222222")!);
        _tileBorder = AndroidUiTheme.Paint(Color.ParseColor("#444444")!, Math.Max(1, AndroidUiTheme.Dp(context, 1)));
        _textPaint = AndroidUiTheme.TextPaint(context, 9, Color.White, bold: true);
        _labelPaint = AndroidUiTheme.Paint(Color.Argb(160, 0, 0, 0)!);
        _placeholderPaint = AndroidUiTheme.TextPaint(context, 10, Color.ParseColor("#777777")!);
        _crosshairPaint = AndroidUiTheme.Paint(Color.Argb(220, 255, 255, 255)!, Math.Max(1, AndroidUiTheme.Dp(context, 1)));
        _focusPaint = AndroidUiTheme.Paint(Color.Rgb(0, 255, 0)!, Math.Max(1, AndroidUiTheme.Dp(context, 1)));
        SetWillNotDraw(false);
        Clickable = true;
        _presenter.TilesChanged += OnTilesChanged;
        _presenter.CrosshairChanged += OnTilesChanged;
    }

    /// <summary>分析栏“空白点击清准星”入口（主图点按与面板空白共用）。</summary>
    internal void ClearCrosshair()
    {
        _presenter.ClearCrosshair();
        ClearDetail();
    }

    /// <summary>解除细节预览定格。</summary>
    internal void ClearDetail()
    {
        CancelPendingDetailTap();
        if (!_detailActive) return;
        _detailActive = false;
        _detailTile = null;
        _detailKey = default;
        DetailChanged?.Invoke();
        Invalidate();
    }

    /// <summary>位图变化后重绘。</summary>
    private void OnTilesChanged()
        => Post(() =>
        {
            if (_disposed) return;
            Invalidate();
            if (_detailTile != null && !_presenter.Tiles.Contains(_detailTile)) ClearDetail();
            if (_presenterTileCount != _presenter.Tiles.Count)
            {
                _presenterTileCount = _presenter.Tiles.Count;
                TileCountChanged?.Invoke();
            }
        });

    private int _presenterTileCount = -1;

    /// <summary>按行/列与可用主轴长度重排瓦片（dp）。</summary>
    /// <param name="horizontal">是否行布局。</param>
    /// <param name="availableMain">主轴可用长度（dp）。</param>
    /// <param name="startInset">首瓦片前导让位（dp，安全区）。</param>
    internal void Arrange(bool horizontal, double availableMain, double startInset)
    {
        _horizontal = horizontal;
        _availableMain = Math.Max(1, availableMain);
        _startInset = Math.Max(0, startInset);
        var tiles = _presenter.Tiles;
        _tileSize = AnalysisPanelGeometry.TileSize(_availableMain, tiles.Count);
        _frames.Clear();
        _frameOrder.Clear();
        for (int index = 0; index < tiles.Count; index++)
        {
            double offset = _startInset + AnalysisPanelGeometry.TileOffset(index, _tileSize);
            _frames[tiles[index]] = horizontal
                ? new LayoutRect(offset, AnalysisPanelGeometry.Padding, _tileSize, _tileSize)
                : new LayoutRect(AnalysisPanelGeometry.Padding, offset, _tileSize, _tileSize);
            _frameOrder[tiles[index]] = index;
        }
        _presenterTileCount = tiles.Count;
        Invalidate();
    }

    /// <summary>绘制所有瓦片。</summary>
    /// <param name="canvas">画布。</param>
    protected override void OnDraw(Canvas canvas)
    {
        base.OnDraw(canvas);
        float density = (float)AndroidUiTheme.Density(Context!);
        var tiles = _presenter.Tiles;
        foreach (var tile in tiles)
        {
            if (!_frames.TryGetValue(tile, out var frame)) continue;
            var outer = ToRectF(frame, density);
            AndroidUiTheme.DrawRounded(canvas, outer, TileRadiusDp * density, Color.ParseColor("#222222")!, AndroidUiTheme.ControlBorder, Math.Max(1, density));
            float inset = ContentInsetDp * density;
            var content = new RectF(outer.Left + inset, outer.Top + inset, outer.Right - inset, outer.Bottom - inset);
            Bitmap? source = tile.Kind == AnalysisTileKind.Detail ? DetailSource(tile) : ImageFor(tile);
            var background = tile.Kind == AnalysisTileKind.Detail ? null : BitmapFor(tile.Background);
            canvas.Save();
            using var clip = new Path();
            clip.AddRoundRect(content, (TileRadiusDp - 1) * density, (TileRadiusDp - 1) * density, Path.Direction.Cw!);
            canvas.ClipPath(clip);
            canvas.DrawColor(Color.ParseColor("#1A1A1A")!);
            if (background != null) DrawFitted(canvas, background, content, .30f);
            if (tile.Kind == AnalysisTileKind.Histogram && !ReferenceEquals(_histogramLogged, tile.Source))
            {
                _histogramLogged = tile.Source;
                Console.WriteLine($"[Native] 直方图源={tile.Source?.Width}x{tile.Source?.Height} pixels={tile.Source?.Pixels.Length} bitmap={source != null}");
            }
            if (source != null) DrawFitted(canvas, source, content, 1f);
            else if (string.IsNullOrEmpty(tile.Placeholder) == false)
            {
                AndroidUiTheme.DrawCentered(canvas, tile.Placeholder, _placeholderPaint, content.CenterX(),
                    content.CenterY() + _placeholderPaint.TextSize * .36f, content.Width() - 8 * density);
            }
            if (tile.Kind == AnalysisTileKind.Diagnostic)
                DrawShake(canvas, tile, content, density);
            if (tile.Kind != AnalysisTileKind.Detail && _presenter.CrosshairU is { } u && _presenter.CrosshairV is { } v)
                DrawCrosshair(canvas, content, u, v);
            if (tile.Kind == AnalysisTileKind.Detail)
            {
                if (tile.FocusFrameWidth is { } frameWidth && tile.FocusFrameHeight is { } frameHeight)
                    DrawFocusFrame(canvas, content, frameWidth, frameHeight);
                if (_detailActive && ReferenceEquals(tile, _detailTile))
                {
                    using var highlight = new Paint(PaintFlags.AntiAlias) { StrokeWidth = 2 * density };
                    highlight.SetStyle(global::Android.Graphics.Paint.Style.Stroke);
                    highlight.Color = Color.Rgb(0, 255, 0)!;
                    canvas.DrawRoundRect(content, (TileRadiusDp - 1) * density, (TileRadiusDp - 1) * density, highlight);
                }
            }
            canvas.Restore();

            // 左上角药丸角标。
            string label = tile.ShortLabel;
            if (!string.IsNullOrEmpty(label))
            {
                var labelWidth = _textPaint.MeasureText(label) + 10 * density;
                var pill = new RectF(outer.Left + 3 * density, outer.Top + 3 * density,
                    Math.Min(outer.Right - 3 * density, outer.Left + 3 * density + labelWidth), outer.Top + 17 * density);
                AndroidUiTheme.DrawRounded(canvas, pill, 8 * density, Color.Argb(160, 0, 0, 0)!);
                AndroidUiTheme.DrawCentered(canvas, label, _textPaint, pill.CenterX(),
                    pill.CenterY() + _textPaint.TextSize * .36f, pill.Width() - 2 * density);
            }
        }
    }

    /// <summary>取诊断/直方图瓦片的原生位图（同一源引用只编码一次）。</summary>
    private Bitmap? ImageFor(AnalysisTile tile) => BitmapFor(tile.Source);

    /// <summary>共享 DecodedImage → Bitmap 的瓦片缓存（按源引用，不释放源）。</summary>
    private Bitmap? BitmapFor(DecodedImage? source)
    {
        if (source == null) return null;
        if (_images.TryGetValue(source, out var cached)) return cached;
        var bitmap = AndroidBitmapConverter.FromDecodedImage($"analysis-{source.GetHashCode()}-{source.Width}x{source.Height}", source);
        if (bitmap != null) _images[source] = bitmap;
        return bitmap;
    }

    /// <summary>细节预览裁剪：代理期留空，定格中心优先于 presenter 中心。</summary>
    private Bitmap? DetailSource(AnalysisTile tile)
    {
        var source = _session.IsProxy ? null : _session.Image;
        int pixels = Math.Max(1, (int)Math.Round(_tileSize * AndroidUiTheme.Density(Context!)));
        // 裁剪中心恒为该瓦片自身的中心（对焦点/画面中心）；手指落点只决定“定格/解除”，不移动裁剪中心。
        double u = tile.CenterU;
        double v = tile.CenterV;
        var key = (source, u, v, pixels);
        if (_detailKey == key && _detailBitmap != null) return _detailBitmap;
        _detailKey = key;
        if (source == null || !source.TryAcquire())
        {
            _detailBitmap = null;
            return null;
        }
        DecodedImage? crop = null;
        try { crop = DisplayCrop.CropSquare(source, source.Orientation, u, v, pixels); }
        finally { source.Release(); }
        if (crop == null)
        {
            _detailBitmap = null;
            return null;
        }
        try
        {
            _detailBitmap = AndroidBitmapConverter.FromDecodedImage($"detail-{u:0.###}-{v:0.###}-{pixels}", crop);
            return _detailBitmap;
        }
        finally { crop.Dispose(); }
    }

    /// <summary>按原始比例装入矩形（letterbox，不拉伸）。</summary>
    private static void DrawFitted(Canvas canvas, Bitmap? bitmap, RectF content, float alpha)
    {
        if (bitmap == null || bitmap.IsRecycled) return;
        float scale = Math.Min(content.Width() / bitmap.Width, content.Height() / bitmap.Height);
        float width = bitmap.Width * scale, height = bitmap.Height * scale;
        var dst = new RectF(content.CenterX() - width / 2, content.CenterY() - height / 2,
            content.CenterX() + width / 2, content.CenterY() + height / 2);
        using var paint = new Paint(PaintFlags.FilterBitmap) { FilterBitmap = true };
        paint.Alpha = (int)Math.Clamp(alpha * 255, 0, 255);
        canvas.DrawBitmap(bitmap, new Rect(0, 0, bitmap.Width, bitmap.Height), dst, paint);
    }

    /// <summary>绘制准星（归一化坐标，落在内容矩形内）。</summary>
    private void DrawCrosshair(Canvas canvas, RectF content, double u, double v)
    {
        float x = content.Left + (float)u * content.Width();
        float y = content.Top + (float)v * content.Height();
        canvas.DrawLine(content.Left, y, content.Right, y, _crosshairPaint);
        canvas.DrawLine(x, content.Top, x, content.Bottom, _crosshairPaint);
    }

    /// <summary>绘制对焦框/定格框（绿色 1px 方框）。</summary>
    private void DrawFocusFrame(Canvas canvas, RectF content, double width, double height)
    {
        float cx = content.CenterX(), cy = content.CenterY();
        float halfWidth = (float)(width * content.Width() / 2);
        float halfHeight = (float)(height * content.Height() / 2);
        canvas.DrawRect(cx - halfWidth, cy - halfHeight, cx + halfWidth, cy + halfHeight, _focusPaint);
    }

    /// <summary>叠加抖动矢量场（32×32，按有效格画线段）。</summary>
    private void DrawShake(Canvas canvas, AnalysisTile tile, RectF content, float density)
    {
        var field = tile.ShakeField;
        if (field == null || field.Direction.Length < 1024) return;
        float cell = content.Width() / 32f;
        float offsetX = content.Left + (content.Width() - cell * 32) / 2;
        float offsetY = content.Top + (content.Height() - cell * 32) / 2;
        using var paint = new Paint(PaintFlags.AntiAlias) { StrokeWidth = Math.Max(1, density) };
        for (int y = 0; y < 32; y++)
        {
            for (int x = 0; x < 32; x++)
            {
                int index = y * 32 + x;
                if (!field.Mask[index]) continue;
                float direction = field.Direction[index];
                if (float.IsNaN(direction)) continue;
                float width = field.Width[index];
                if (float.IsNaN(width)) continue;
                double dragR = field.Diagonal > 0 ? width / field.Diagonal : 0;
                var color = CvHeatmap.ColorForShake((float)dragR, field.LocalConsistency[index], 1f);
                paint.Color = Color.Argb(220, color.R, color.G, color.B);
                float cx = offsetX + x * cell + cell / 2;
                float cy = offsetY + y * cell + cell / 2;
                float half = Math.Max(1.2f, (float)dragR * cell * .6f);
                float dx = (float)Math.Cos(direction) * half;
                float dy = (float)Math.Sin(direction) * half;
                canvas.DrawLine(cx - dx, cy - dy, cx + dx, cy + dy, paint);
            }
        }
    }

    /// <summary>点按命中：细节预览单指点按定格/解除、双击定位；诊断瓦片落准星并重算。</summary>
    /// <param name="e">事件。</param>
    /// <returns>消费。</returns>
    public override bool OnTouchEvent(MotionEvent? e)
    {
        if (e == null) return false;
        if (e.ActionMasked != MotionEventActions.Up) return true;
        float density = (float)AndroidUiTheme.Density(Context!);
        double x = e.GetX() / density, y = e.GetY() / density;
        foreach (var tile in _presenter.Tiles)
        {
            if (!_frames.TryGetValue(tile, out var frame)) continue;
            if (!(x >= frame.X && x <= frame.MaxX && y >= frame.Y && y <= frame.MaxY)) continue;
            double contentInset = ContentInsetDp;
            double localU = (x - frame.X - contentInset) / Math.Max(1, _tileSize - contentInset * 2);
            double localV = (y - frame.Y - contentInset) / Math.Max(1, _tileSize - contentInset * 2);
            if (tile.Kind == AnalysisTileKind.Detail)
            {
                HandleDetailTap(tile);
                return true;
            }
            CancelPendingDetailTap();
            if (tile.Kind == AnalysisTileKind.Diagnostic)
            {
                _presenter.OnTileClicked(Math.Clamp(localU, 0, 1), Math.Clamp(localV, 0, 1));
                return true;
            }
            return true;
        }
        ClearDetail();
        _presenter.ClearCrosshair();
        return true;
    }

    /// <summary>细节瓦片单/双击判定：单点延时 280ms 定格，双击改为定位主图（不先定格）。</summary>
    private void HandleDetailTap(AnalysisTile tile)
    {
        long now = SystemClock.UptimeMillis();
        if (ReferenceEquals(_pendingDetailTile, tile) && now - _lastDetailTapTime <= 280)
        {
            CancelPendingDetailTap();
            DetailFocusRequested?.Invoke(tile.CenterU, tile.CenterV);
            return;
        }
        CancelPendingDetailTap();
        _pendingDetailTile = tile;
        _lastDetailTapTime = now;
        Action pending = () =>
        {
            if (!ReferenceEquals(_pendingDetailTile, tile)) return;
            _pendingDetailTile = null;
            ToggleDetail(tile);
        };
        _pendingTap = pending;
        PostDelayed(pending, 280);
    }

    private Action? _pendingTap;

    private void CancelPendingDetailTap()
    {
        if (_pendingTap != null) RemoveCallbacks(_pendingTap);
        _pendingTap = null;
        _pendingDetailTile = null;
    }

    private void ToggleDetail(AnalysisTile tile)
    {
        if (_detailActive && ReferenceEquals(_detailTile, tile))
        {
            ClearDetail();
            return;
        }
        _detailTile = tile;
        _detailActive = true;
        _detailKey = default;
        DetailChanged?.Invoke();
        Invalidate();
    }

    private static RectF ToRectF(LayoutRect rect, float density) => new(
        (float)rect.X * density, (float)rect.Y * density,
        (float)rect.MaxX * density, (float)rect.MaxY * density);

    /// <summary>释放订阅与位图（位图由转换缓存持有，面板不释放共享源）。</summary>
    /// <param name="disposing">是否释放托管资源。</param>
    protected override void Dispose(bool disposing)
    {
        if (disposing && !_disposed)
        {
            _disposed = true;
            _presenter.TilesChanged -= OnTilesChanged;
            _presenter.CrosshairChanged -= OnTilesChanged;
            _detailBitmap = null;
            _images.Clear();
        }
        base.Dispose(disposing);
    }
}
