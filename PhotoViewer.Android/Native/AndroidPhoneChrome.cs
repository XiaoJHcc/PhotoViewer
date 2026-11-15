using System;
using System.Collections.Generic;
using Android.Content;
using Android.Content.Res;
using Android.Graphics;
using Android.Views;
using Android.Widget;
using Google.Android.Material.Button;
using Google.Android.Material.Card;
using Google.Android.Material.Shape;
using PhotoViewer.Core.Design;
using PhotoViewer.Core.Input;
using PhotoViewer.Core.Platform;
using PhotoViewer.Shared;
using PhotoViewer.ViewModels.Main;
using PhotoViewer.ViewModels.Settings;

namespace PhotoViewer.Android.Native;

/// <summary>
/// 栏位背景：深色半透明 View 承载（SHELL-12）。Android 没有对「应用内主图上取样模糊」的公开接口
/// （<c>RenderEffect</c> 只作用于视图自身内容、<c>Window</c> 背景模糊会连窗口后面的系统界面一起模糊），
/// 因此这里只用半透明深色铺底并登记材质差异，不做截屏、PixelCopy、离屏拷贝或自绘模糊。
/// </summary>
internal sealed class AndroidBarScrim : View
{
    /// <summary>建一个深色半透明栏背景。</summary>
    /// <param name="context">宿主上下文。</param>
    public AndroidBarScrim(Context context) : base(context)
    {
        SetBackgroundColor(Color.Argb(0xE6, Color.GetRedComponent(AndroidUiTheme.BarBackground),
            Color.GetGreenComponent(AndroidUiTheme.BarBackground), Color.GetBlueComponent(AndroidUiTheme.BarBackground)));
        ImportantForAccessibility = ImportantForAccessibility.No;
    }
}

/// <summary>
/// 手机形态的玻璃圆按钮（SHELL-12）：Material 3 <see cref="MaterialButton"/> 承载胶囊底、状态层与涟漪，
/// 字形取设计令牌的 uicons 码位，尺寸固定 <c>shell.glass.button.size</c>（44），不随文字拉宽。
/// </summary>
internal sealed class AndroidGlassButton : MaterialButton
{
    private string _glyph;

    /// <summary>按钮边长（dp）。</summary>
    internal static double Size => DesignTokens.ShellGlassButtonSize;

    /// <summary>建一个图标按钮。</summary>
    /// <param name="context">宿主上下文。</param>
    /// <param name="glyph">uicons 字形（空串表示纯文字合体段由调用方另行设置）。</param>
    /// <param name="description">读屏名称（与按钮语义一致）。</param>
    /// <param name="iconFont">是否用 uicons 字形绘制（false 时按普通文字绘制，用于文字入口）。</param>
    public AndroidGlassButton(Context context, string glyph, string description, bool iconFont = true) : base(context)
    {
        _glyph = glyph.StartsWith("U+", StringComparison.Ordinal) ? AndroidUiTheme.IconGlyph(glyph) : glyph;
        Text = iconFont ? string.Empty : glyph;
        SetIncludeFontPadding(false);
        SetWillNotDraw(iconFont);
        DrawsGlyph = iconFont;
        ContentDescription = description;
        SetAllCaps(false);
        TextSize = 18;
        Gravity = GravityFlags.Center;
        if (iconFont && AndroidUiTheme.IconTypeface(context) is { } typeface) SetTypeface(typeface, TypefaceStyle.Normal);
        else
        {
            TextSize = 12;
            SetTypeface(Typeface.Create(Typeface.Default, TypefaceStyle.Normal), TypefaceStyle.Normal);
        }
        SetTextColor(AndroidUiTheme.TextPrimary);
        InsetTop = 0;
        InsetBottom = 0;
        InsetLeft = 0;
        InsetRight = 0;
        SetMinWidth(0);
        SetMinimumWidth(0);
        SetMinHeight(0);
        SetMinimumHeight(0);
        SetPadding(0, 0, 0, 0);
        var density = (float)AndroidUiTheme.Density(context);
        CornerRadius = (int)Math.Round(Size / 2 * density);
        StrokeWidth = 0;
        BackgroundTintList = ColorStateList.ValueOf(AndroidUiTheme.ControlFill);
        RippleColor = ColorStateList.ValueOf(Color.Argb(0x33, 255, 255, 255));
        Focusable = true;
    }

    /// <summary>当前绘制/读屏使用的字形（验收读取）。</summary>
    internal string Glyph => _glyph;

    /// <summary>合体容器统一绘制两段字形（按钮自身在 MaterialCardView 内会被内容区裁切时不重复绘制）。</summary>
    internal bool DrawsGlyph { get; set; } = true;

    /// <summary>按固定基线居中绘制字形，避免图标字体的原生行高把墨迹推到按钮底缘。</summary>
    /// <param name="canvas">画布。</param>
    protected override void OnDraw(Canvas canvas)
    {
        base.OnDraw(canvas);
        if (!DrawsGlyph || string.IsNullOrEmpty(_glyph)) return;
        if (AndroidUiTheme.IconTypeface(Context!) is not { } typeface) return;
        using var paint = AndroidUiTheme.Paint(AndroidUiTheme.TextPrimary);
        paint.TextSize = AndroidUiTheme.Sp(Context!, 18);
        paint.SetTypeface(typeface);
        paint.TextAlign = global::Android.Graphics.Paint.Align.Center;
        canvas.DrawText(_glyph, Width / 2f, Height / 2f + paint.TextSize * .35f, paint);
    }

    /// <summary>固定 44 见方：MaterialButton 的 style 最小宽高会把合体段撑成 88，这里只接受外部精确尺寸。</summary>
    /// <param name="widthMeasureSpec">宽约束。</param>
    /// <param name="heightMeasureSpec">高约束。</param>
    protected override void OnMeasure(int widthMeasureSpec, int heightMeasureSpec)
    {
        SetMeasuredDimension(MeasureSpec.GetSize(widthMeasureSpec), MeasureSpec.GetSize(heightMeasureSpec));
    }


    /// <summary>换成另一个图标字形（外观状态变化时只改文字，不改变几何）。</summary>
    /// <param name="glyph">新字形。</param>
    internal void SetGlyph(string glyph)
        => _glyph = glyph.StartsWith("U+", StringComparison.Ordinal) ? AndroidUiTheme.IconGlyph(glyph) : glyph;
}

/// <summary>
/// 两段玻璃合体按钮（SHELL-08、SHELL-09）：一个自绘胶囊容器（MaterialShapeDrawable 圆角与描边）内放两段
/// <see cref="AndroidGlassButton"/>，胶囊只由容器一层绘制，段自身透明但仍保留 Material 状态层与涟漪；
/// 段各自 44×44、读屏各有一项，镜像时只交换段的位置。
/// </summary>
internal sealed class AndroidGlassCombo : FrameLayout
{
    private readonly AndroidGlassButton _first;
    private readonly AndroidGlassButton _second;
    private bool _vertical;

    /// <summary>建两段合体按钮。</summary>
    /// <param name="context">宿主上下文。</param>
    /// <param name="firstGlyph">前段字形。</param>
    /// <param name="firstDescription">前段读屏名称。</param>
    /// <param name="secondGlyph">后段字形。</param>
    /// <param name="secondDescription">后段读屏名称。</param>
    public AndroidGlassCombo(Context context, string firstGlyph, string firstDescription,
        string secondGlyph, string secondDescription) : base(context)
    {
        var shape = new ShapeAppearanceModel.Builder()
            .SetAllCornerSizes((float)AndroidUiTheme.Dp(context, Size / 2))
            .Build();
        var backdrop = new MaterialShapeDrawable(shape);
        backdrop.FillColor = ColorStateList.ValueOf(AndroidUiTheme.ControlFill);
        backdrop.StrokeColor = ColorStateList.ValueOf(AndroidUiTheme.ControlBorder);
        backdrop.StrokeWidth = AndroidUiTheme.Dp(context, 1);
        Background = backdrop;
        _first = new AndroidGlassButton(context, firstGlyph, firstDescription);
        _second = new AndroidGlassButton(context, secondGlyph, secondDescription);
        // 段默认透明但保留 Material 按下/聚焦状态层；胶囊外形由容器 Outline 裁剪。
        var stateStates = new[]
        {
            new[] { global::Android.Resource.Attribute.StatePressed },
            new[] { global::Android.Resource.Attribute.StateFocused },
            new int[0],
        };
        var stateColors = new[]
        {
            Color.Argb(0x33, 255, 255, 255).ToArgb(),
            Color.Argb(0x1F, 255, 255, 255).ToArgb(),
            Color.Transparent!.ToArgb(),
        };
        var segmentTint = new ColorStateList(stateStates, stateColors);
        foreach (var button in new[] { _first, _second })
        {
            button.BackgroundTintList = segmentTint;
            button.CornerRadius = 0;
            AddView(button);
        }
        SetClipChildren(true);
        ClipToOutline = true;
    }

    /// <summary>两段合体的边长（dp）：两枚玻璃按钮。</summary>
    internal static double Size => AndroidGlassButton.Size * 2;

    /// <summary>前段按钮（验收读屏/命中用）。</summary>
    internal AndroidGlassButton First => _first;

    /// <summary>后段按钮。</summary>
    internal AndroidGlassButton Second => _second;

    /// <summary>是否竖排（横屏控制栏的缩放/增强合体）。</summary>
    internal bool Vertical => _vertical;

    /// <summary>设置排布方向：横排两段左右相接，竖排两段上下相接。</summary>
    /// <param name="vertical">是否竖排。</param>
    internal void SetVertical(bool vertical)
    {
        if (_vertical == vertical) return;
        _vertical = vertical;
        RequestLayout();
    }

    /// <summary>两段各占一半，镜像只改变视觉顺序（由调用方决定哪一段在窗口缘一侧）。</summary>
    /// <param name="changed">是否变化。</param>
    /// <param name="l">左。</param>
    /// <param name="t">上。</param>
    /// <param name="r">右。</param>
    /// <param name="b">下。</param>
    protected override void OnLayout(bool changed, int l, int t, int r, int b)
    {
        int width = r - l, height = b - t;
        if (_vertical)
        {
            int half = height / 2;
            _first.Layout(0, 0, width, half);
            _second.Layout(0, half, width, height);
        }
        else
        {
            int half = width / 2;
            _first.Layout(0, 0, half, height);
            _second.Layout(half, 0, width, height);
        }
    }

    /// <summary>两段各自精确测量为一半容器尺寸，不参与内容撑开。</summary>
    /// <param name="widthMeasureSpec">宽约束。</param>
    /// <param name="heightMeasureSpec">高约束。</param>
    protected override void OnMeasure(int widthMeasureSpec, int heightMeasureSpec)
    {
        int width = MeasureSpec.GetSize(widthMeasureSpec), height = MeasureSpec.GetSize(heightMeasureSpec);
        SetMeasuredDimension(width, height);
        int childWidth = _vertical ? width : width / 2;
        int childHeight = _vertical ? height / 2 : height;
        foreach (var button in new[] { _first, _second })
            button.Measure(MeasureSpec.MakeMeasureSpec(childWidth, MeasureSpecMode.Exactly),
                MeasureSpec.MakeMeasureSpec(childHeight, MeasureSpecMode.Exactly));
    }
}

/// <summary>
/// 星级控件（RATING-07）：Material <see cref="MaterialCardView"/> 胶囊外层承载材质与状态层，
/// 槽位、字形与命中全部取共享 <see cref="RatingControlGeometry"/>；展开态六格可点，
/// 紧凑态按共享星簇静态绘制。
/// </summary>
internal sealed class AndroidRatingControl : MaterialCardView
{
    private readonly Paint _starActive = AndroidUiTheme.Paint(AndroidUiTheme.StarActive);
    private readonly Paint _starInactive = AndroidUiTheme.Paint(AndroidUiTheme.StarInactive);
    private readonly Paint _starClear;
    private readonly Paint _conflictPaint;
    private bool _conflict;
    private int _rating;
    private bool _enabled = true;

    /// <summary>星级写回入口（0–5；冲突时 0 号由宿主决定是否改走来源浮窗）。</summary>
    internal event Action<int>? RateRequested;

    /// <summary>冲突三角点击（0 号槽位，不写值）。</summary>
    internal event Action? ConflictRequested;

    /// <summary>胶囊是否横排（竖屏底行为 true，横屏控制栏为 false）。</summary>
    internal bool Horizontal { get; set; } = true;

    /// <summary>胶囊主轴长度（dp，布局落定后由 <see cref="SetSlot"/> 给出）。</summary>
    internal double SlotMain => Horizontal ? Width : Height;

    /// <summary>当前形态（验收读取）。</summary>
    internal RatingControlForm Form => RatingControlGeometry.ResolveForm(SlotMain / AndroidUiTheme.Density(Context!));

    /// <summary>建星级控件。</summary>
    /// <param name="context">宿主上下文。</param>
    public AndroidRatingControl(Context context) : base(context)
    {
        Radius = (float)AndroidUiTheme.Dp(context, RatingControlGeometry.Thickness / 2);
        CardElevation = 0;
        StrokeWidth = 0;
        SetCardBackgroundColor(AndroidUiTheme.CardBackground);
        SetContentPadding(0, 0, 0, 0);
        Clickable = false;
        _starClear = AndroidUiTheme.Paint(AndroidUiTheme.StarClear, Math.Max(1, AndroidUiTheme.Dp(context, 1)));
        _conflictPaint = AndroidUiTheme.TextPaint(context, DesignTokens.ToolbarRatingGlyphFontSize, AndroidUiTheme.StarConflict);
        if (AndroidUiTheme.IconTypeface(context) is { } typeface) _conflictPaint.SetTypeface(typeface);
        Focusable = true;
    }

    /// <summary>同步当前照片的评分与组级冲突态（不改变几何）。</summary>
    /// <param name="rating">代表文件评分。</param>
    /// <param name="conflict">组级冲突。</param>
    /// <param name="enabled">是否有当前照片。</param>
    internal void SetState(int rating, bool conflict, bool enabled)
    {
        if (_rating == rating && _conflict == conflict && _enabled == enabled) return;
        _rating = rating;
        _conflict = conflict;
        _enabled = enabled;
        Invalidate();
    }

    /// <summary>按槽位尺寸更新胶囊朝向；几何变化后胶囊保持自身厚度并铺满主轴。</summary>
    /// <param name="horizontal">主轴是否水平。</param>
    internal void SetOrientation(bool horizontal)
    {
        if (Horizontal == horizontal) return;
        Horizontal = horizontal;
        Invalidate();
    }

    /// <summary>六格槽位的本地像素矩形（0–5；展开态按共享步长，紧凑态按星簇中心给出可读位置）。</summary>
    /// <param name="value">星级值。</param>
    /// <returns>槽位矩形。</returns>
    internal Rect SlotRectPx(int value)
    {
        float density = (float)AndroidUiTheme.Density(Context!);
        var form = Form;
        var (control, rowLength, _) = ResolveControl();
        if (form == RatingControlForm.Compact && value == 0 && !_conflict) return new Rect();
        if (form == RatingControlForm.Expanded)
        {
            var local = RatingControlGeometry.SlotRect(value, rowLength, rowCrossFor(control), Horizontal,
                RatingControlGeometry.Slot);
            return Px(control.X + local.X, control.Y + local.Y, local.Width, local.Height, density);
        }
        return CompactGlyphRectPx(value, control, density);
    }

    /// <summary>按实际视图宽高求共享胶囊矩形与星排两轴尺寸。</summary>
    private (LayoutRect Control, double RowLength, double RowCross) ResolveControl()
    {
        double density = AndroidUiTheme.Density(Context!);
        var slotRect = new LayoutRect(0, 0, Width / density, Height / density);
        var control = RatingControlGeometry.ControlFrame(slotRect, Horizontal);
        return (control, Horizontal ? control.Width : control.Height,
            Horizontal ? control.Height : control.Width);
    }

    private double rowCrossFor(LayoutRect control) => Horizontal ? control.Height : control.Width;

    private Rect CompactGlyphRectPx(int value, LayoutRect control, float density)
    {
        double offset = RatingControlGeometry.CompactOffset(value, _conflict, Horizontal,
            Horizontal ? control.Width : control.Height);
        double glyph = RatingControlGeometry.CompactGlyph;
        double centerMain = (Horizontal ? control.X : control.Y) + (Horizontal ? control.Width : control.Height) / 2 + offset;
        double centerCross = (Horizontal ? control.Y : control.X) + (Horizontal ? control.Height : control.Width) / 2;
        return Horizontal
            ? Px(centerMain - glyph / 2, centerCross - glyph / 2, glyph, glyph, density)
            : Px(centerCross - glyph / 2, centerMain - glyph / 2, glyph, glyph, density);
    }

    private static Rect Px(double x, double y, double width, double height, float density)
        => new((int)Math.Round(x * density), (int)Math.Round(y * density),
            (int)Math.Round((x + width) * density), (int)Math.Round((y + height) * density));

    /// <summary>绘制胶囊内的星排或紧凑星簇。</summary>
    /// <param name="canvas">画布。</param>
    protected override void DispatchDraw(Canvas canvas)
    {
        base.DispatchDraw(canvas);
        float density = (float)AndroidUiTheme.Density(Context!);
        double main = SlotMain / density;
        double cross = (Horizontal ? Height : Width) / density;
        if (main <= 0 || cross <= 0) return;
        int alpha = _enabled ? 255 : (int)Math.Round(255 * DesignTokens.ColorToolbarDisabledContentAlpha);
        var form = Form;
        var (control, rowLength, rowCross) = ResolveControl();
        if (form == RatingControlForm.Expanded)
        {
            for (int value = 0; value < RatingControlGeometry.SlotCount; value++)
            {
                var slot = RatingControlGeometry.SlotRect(value, rowLength, rowCross, Horizontal, RatingControlGeometry.Slot);
                double centerMain = (Horizontal ? control.X + slot.X : control.Y + slot.Y)
                    + (Horizontal ? slot.Width : slot.Height) / 2;
                double centerCross = (Horizontal ? control.Y : control.X) + (Horizontal ? control.Height : control.Width) / 2;
                DrawGlyph(canvas, value, centerMain, centerCross, density, alpha);
            }
            return;
        }

        bool conflict = _conflict;
        double capsuleMain = Horizontal ? control.Width : control.Height;
        double capsuleCross = Horizontal ? control.Height : control.Width;
        if (conflict)
        {
            double offset = RatingControlGeometry.CompactOffset(0, true, Horizontal, capsuleMain);
            DrawGlyph(canvas, 0, (Horizontal ? control.X : control.Y) + capsuleMain / 2 + offset,
                (Horizontal ? control.Y : control.X) + capsuleCross / 2, density, alpha);
        }
        for (int value = 1; value <= 5; value++)
        {
            double offset = RatingControlGeometry.CompactOffset(value, conflict, Horizontal, capsuleMain);
            DrawGlyph(canvas, value, (Horizontal ? control.X : control.Y) + capsuleMain / 2 + offset,
                (Horizontal ? control.Y : control.X) + capsuleCross / 2, density, alpha);
        }
    }

    private void DrawGlyph(Canvas canvas, int value, double centerMain, double centerCross, float density, int alpha)
    {
        float x = (float)((Horizontal ? centerMain : centerCross) * density);
        float y = (float)((Horizontal ? centerCross : centerMain) * density);
        if (value == 0)
        {
            if (_conflict)
            {
                _conflictPaint.Alpha = alpha;
                AndroidUiTheme.DrawGlyphCentered(canvas, AndroidUiTheme.IconGlyph(DesignTokens.IconWarning), _conflictPaint, x, y);
                return;
            }
            _starClear.Alpha = alpha;
            float arm = (float)(DesignTokens.ToolbarRatingClearGlyphFontSize / 2 * density);
            canvas.DrawLine(x - arm, y - arm, x + arm, y + arm, _starClear);
            canvas.DrawLine(x - arm, y + arm, x + arm, y - arm, _starClear);
            return;
        }
        _starActive.Alpha = alpha;
        _starInactive.Alpha = alpha;
        bool active = value <= _rating;
        using var star = AndroidUiTheme.StarPath(x, y,
            (float)(DesignTokens.ToolbarRatingGlyphFontSize / 2 * density),
            (float)(DesignTokens.ToolbarRatingGlyphFontSize * .2 * density));
        canvas.DrawPath(star, active ? _starActive : _starInactive);
    }

    /// <summary>展开态点按写回评分；紧凑态为静态星簇。</summary>
    /// <param name="e">触摸事件。</param>
    /// <returns>是否消费。</returns>
    public override bool OnTouchEvent(MotionEvent? e)
    {
        if (e == null) return false;
        if (e.ActionMasked != MotionEventActions.Up) return true;
        if (!_enabled || Form != RatingControlForm.Expanded) return true;
        float density = (float)AndroidUiTheme.Density(Context!);
        var (control, rowLength, _) = ResolveControl();
        double localMain = (Horizontal ? e.GetX() : e.GetY()) / density - (Horizontal ? control.X : control.Y);
        int value = RatingControlGeometry.ValueAt(localMain, rowLength, Horizontal, RatingControlGeometry.Slot);
        if (value == 0 && _conflict) ConflictRequested?.Invoke();
        else RateRequested?.Invoke(value);
        return true;
    }
}

/// <summary>
/// EXIF 摘要块（TOOLBAR-05）：项目、内边距与整项裁剪取共享 <see cref="ExifBlockGeometry"/>；
/// 竖屏顶行为带衬底的横排块（MaterialShapeDrawable 圆角半透明底），横屏控制栏为无衬底纵排列，
/// 容器宽度固定为玻璃按钮边长、文字在容器内尾部截断。整块点击进入 EXIF 详情（TOOLBAR-06）。
/// </summary>
internal sealed class AndroidExifBlock : View
{
    private readonly Paint _labelPaint;
    private readonly Paint _valuePaint;
    private readonly MaterialShapeDrawable? _backdrop;
    private IReadOnlyList<ExifDisplayEntry> _items = Array.Empty<ExifDisplayEntry>();
    private int _visibleCount;
    private bool _horizontal = true;
    private bool _bare;

    /// <summary>整块点击（进入 EXIF 详情）。</summary>
    internal event Action? ExifActivated;

    /// <summary>建 EXIF 块。</summary>
    /// <param name="context">宿主上下文。</param>
    public AndroidExifBlock(Context context) : base(context)
    {
        _labelPaint = AndroidUiTheme.TextPaint(context, DesignTokens.ToolbarExifLabelFontSize, AndroidUiTheme.TextSecondary);
        _valuePaint = AndroidUiTheme.TextPaint(context, DesignTokens.ToolbarExifValueFontSize, AndroidUiTheme.TextPrimary, bold: true);
        var shape = new ShapeAppearanceModel.Builder()
            .SetAllCornerSizes((float)AndroidUiTheme.Dp(context, DesignTokens.ToolbarExifCornerRadius))
            .Build();
        _backdrop = new MaterialShapeDrawable(shape);
        _backdrop.FillColor = ColorStateList.ValueOf(Color.Argb(0xD9, 0x22, 0x22, 0x22));
        Clickable = true;
        Focusable = true;
        ContentDescription = "EXIF 详情";
    }

    /// <summary>当前显示的 EXIF 项（验收读取）。</summary>
    internal IReadOnlyList<ExifDisplayEntry> Items => _items;

    /// <summary>同步内容与变体；只改文本与绘制，不改变外部几何。</summary>
    /// <param name="items">启用的 EXIF 项。</param>
    /// <param name="horizontal">是否横排。</param>
    /// <param name="bare">是否无衬底变体。</param>
    /// <param name="visibleCount">实际显示的整项数（无衬底纵排的裁剪结果）。</param>
    internal void SetContent(IReadOnlyList<ExifDisplayEntry> items, bool horizontal, bool bare, int visibleCount)
    {
        _items = items;
        _horizontal = horizontal;
        _bare = bare;
        _visibleCount = Math.Clamp(visibleCount, 0, items.Count);
        Background = bare ? null : _backdrop;
        Invalidate();
    }

    /// <summary>当前可见项目的实际矩形（dp，验收用）：按完整块固有高度计算，再按可见项数截取。</summary>
    /// <returns>前 <see cref="ExifBlockGeometry.ItemRect"/> 个可见项目矩形。</returns>
    internal IReadOnlyList<LayoutRect> ItemFramesForChecks()
    {
        var intrinsic = ExifBlockGeometry.Size(_items.Count, _horizontal, _bare);
        double density = AndroidUiTheme.Density(Context!);
        double blockMain = _bare && !_horizontal
            ? intrinsic.Height
            : (_horizontal ? Width : Height) / (density == 0 ? 1 : density);
        var frames = new List<LayoutRect>(_visibleCount);
        for (int index = 0; index < _visibleCount; index++)
            frames.Add(ExifBlockGeometry.ItemRect(index, _items.Count, _horizontal, blockMain, _bare));
        return frames;
    }

    /// <summary>绘制项目：标签在上、值在下，居中并在整项内尾部截断。</summary>
    /// <param name="canvas">画布。</param>
    protected override void OnDraw(Canvas canvas)
    {
        base.OnDraw(canvas);
        if (_items.Count == 0 || _visibleCount <= 0 || Width < 1 || Height < 1) return;
        float density = (float)AndroidUiTheme.Density(Context!);
        // 无衬底纵排按「完整块」的固有高度计算项目，再只画放得下的前 ExifItems 项；
        // 若把已裁短的外框高度当整块高度，各项会被垂直压缩。
        var intrinsic = ExifBlockGeometry.Size(_items.Count, _horizontal, _bare);
        // 只有无衬底纵排需要按完整块固有高度取整项；有衬底横排被槽位压短时仍等分缩窄。
        double blockMain = _bare && !_horizontal
            ? intrinsic.Height
            : (_horizontal ? Width : Height) / density;
        for (int index = 0; index < _visibleCount; index++)
        {
            var rect = ExifBlockGeometry.ItemRect(index, _items.Count, _horizontal, blockMain, _bare);
            var item = _items[index];
            float left = (float)(rect.X * density), top = (float)(rect.Y * density);
            float width = (float)(rect.Width * density), height = (float)(rect.Height * density);
            float centerX = left + width / 2;
            float labelBaseline = top + height * .36f;
            float valueBaseline = top + height * .82f;
            _labelPaint.TextSize = AndroidUiTheme.Sp(Context!, DesignTokens.ToolbarExifLabelFontSize);
            _valuePaint.TextSize = AndroidUiTheme.Sp(Context!, DesignTokens.ToolbarExifValueFontSize);
            canvas.Save();
            canvas.ClipRect(left, top, left + Math.Min(width, Width), top + height);
            AndroidUiTheme.DrawCentered(canvas, item.DisplayName, _labelPaint, centerX, labelBaseline, Math.Min(width, Width));
            AndroidUiTheme.DrawCentered(canvas, item.DisplayValue, _valuePaint, centerX, valueBaseline, Math.Min(width, Width));
            canvas.Restore();
        }
    }

    /// <summary>点按整块进入 EXIF 详情。</summary>
    /// <param name="e">触摸事件。</param>
    /// <returns>是否消费。</returns>
    public override bool OnTouchEvent(MotionEvent? e)
    {
        if (e == null) return false;
        if (e.ActionMasked == MotionEventActions.Up) ExifActivated?.Invoke();
        return true;
    }
}
