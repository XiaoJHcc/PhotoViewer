using System;
using System.Collections.Generic;
using Android.Content;
using Android.Content.Res;
using Android.Graphics;
using Android.Graphics.Drawables;
using Android.Graphics.Drawables.Shapes;
using Android.OS;
using Android.Util;
using Android.Views;
using Android.Widget;
using AndroidX.Core.View;
using AndroidX.Core.View.Accessibility;
using AndroidX.CustomView.Widget;
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
/// 栏位背景：中性灰独立色层承载（SHELL-12）。Android 没有对「应用内主图上取样模糊」的公开接口
/// （<c>RenderEffect</c> 只作用于视图自身内容、<c>Window</c> 背景模糊会连窗口后面的系统界面一起模糊），
/// 因此这里只铺 <c>color.bar.overlay</c> 与 <c>shell.bar.overlay.opacity</c> 的同一色层，
/// 不做截屏、PixelCopy、离屏拷贝或自绘模糊。
/// </summary>
internal sealed class AndroidBarScrim : View
{
    /// <summary>建一个中性灰色层栏背景。</summary>
    /// <param name="context">宿主上下文。</param>
    public AndroidBarScrim(Context context) : base(context)
    {
        SetBackgroundColor(AndroidUiTheme.BarOverlay);
        ImportantForAccessibility = ImportantForAccessibility.No;
    }

    /// <summary>栏底实际颜色（验收读取）。</summary>
    internal Color ColorForChecks => Background is ColorDrawable drawable ? drawable.Color : Color.Transparent;
}

/// <summary>
/// uicons 字形图标：按字形墨迹在自身边界内居中，可作为 <see cref="MaterialButton"/> 的图标槽（评分快捷命令另叠 1–5 数字）。
/// 着色取 Material 的 <c>iconTint</c>（含禁用态），绘制时按当前状态解析到画笔。
/// </summary>
internal sealed class AndroidGlyphDrawable : Drawable
{
    private readonly string _glyph;
    private readonly string? _digit;
    private readonly Paint _glyphPaint;
    private readonly Paint? _digitPaint;
    private readonly int _size;
    private ColorStateList? _tint;
    private int _alpha = 255;

    /// <summary>建一个字形图标。</summary>
    /// <param name="context">宿主上下文。</param>
    /// <param name="glyph">uicons 字形。</param>
    /// <param name="solid">是否取实心图标字体（评分快捷命令的星底）。</param>
    /// <param name="digit">叠在字形中心的数字（评分快捷命令 1–5；null 表示不叠）。</param>
    public AndroidGlyphDrawable(Context context, string glyph, bool solid, string? digit, int? sizePx = null)
    {
        _glyph = glyph;
        _digit = string.IsNullOrEmpty(digit) ? null : digit;
        _size = sizePx ?? Math.Max(1, (int)Math.Ceiling(AndroidUiTheme.Sp(context, DesignTokens.ToolbarCommandGlyphFontSize)));
        _glyphPaint = AndroidUiTheme.Paint(Color.White);
        _glyphPaint.TextSize = _size;
        var typeface = solid ? AndroidUiTheme.IconSolidTypeface(context) : AndroidUiTheme.IconTypeface(context);
        if (typeface != null) _glyphPaint.SetTypeface(typeface);
        if (_digit == null) return;
        _digitPaint = AndroidUiTheme.TextPaint(context, DesignTokens.ToolbarRatingDigitFontSize,
            AndroidUiTheme.FromToken(DesignTokens.ColorToolbarCommandBackground), bold: true);
        _digitPaint.TextSize = AndroidUiTheme.Sp(context, DesignTokens.ToolbarRatingDigitFontSize);
    }

    /// <summary>字形文本（验收读取）。</summary>
    internal string Glyph => _glyph;

    /// <summary>叠在字形中心的数字（null 表示没有）。</summary>
    internal string? Digit => _digit;

    /// <summary>固有边长（px）：与字形字号一致，图标槽按它定尺寸并在按钮内居中。</summary>
    public override int IntrinsicWidth => _size;

    /// <summary>固有边长（px）。</summary>
    public override int IntrinsicHeight => _size;

    /// <summary>图标是否随状态变色（禁用只降内容强调）。</summary>
    public override bool IsStateful => _tint?.IsStateful == true;

    /// <summary>半透明像素：字形边缘需要抗锯齿混合。</summary>
    public override int Opacity => (int)Format.Translucent;

    /// <summary>外部透明度（图标槽动画等）与状态色 alpha 相乘后绘制。</summary>
    /// <param name="alpha">0–255。</param>
    public override void SetAlpha(int alpha)
    {
        if (_alpha == alpha) return;
        _alpha = alpha;
        InvalidateSelf();
    }

    /// <summary>颜色滤镜直接作用于字形画笔。</summary>
    /// <param name="colorFilter">滤镜。</param>
    public override void SetColorFilter(ColorFilter? colorFilter) => _glyphPaint.SetColorFilter(colorFilter);

    /// <summary>Material 的 <c>iconTint</c> 着色入口。</summary>
    /// <param name="tint">图标状态色。</param>
    public override void SetTintList(ColorStateList? tint)
    {
        _tint = tint;
        InvalidateSelf();
    }

    /// <summary>状态变化（启用/禁用、按下、悬停）后按状态色重绘。</summary>
    /// <param name="state">当前 Drawable 状态。</param>
    /// <returns>是否重绘。</returns>
    protected override bool OnStateChange(int[] state)
    {
        if (_tint == null) return false;
        InvalidateSelf();
        return true;
    }

    /// <summary>在自身边界中心按墨迹绘制字形，再叠数字。</summary>
    /// <param name="canvas">画布。</param>
    public override void Draw(Canvas canvas)
    {
        var bounds = Bounds;
        if (string.IsNullOrEmpty(_glyph) || bounds.Width() <= 0 || bounds.Height() <= 0) return;
        int color = _tint?.GetColorForState(GetState(), Color.White) ?? Color.White.ToArgb();
        int alpha = Color.GetAlphaComponent(color) * _alpha / 255;
        _glyphPaint.Color = new Color(color);
        _glyphPaint.Alpha = alpha;
        AndroidUiTheme.DrawGlyphCentered(canvas, _glyph, _glyphPaint, bounds.ExactCenterX(), bounds.ExactCenterY());
        if (_digitPaint == null || _digit == null) return;
        _digitPaint.Alpha = alpha;
        AndroidUiTheme.DrawGlyphCentered(canvas, _digit, _digitPaint, bounds.ExactCenterX(), bounds.ExactCenterY());
    }

}

/// <summary>
/// 玻璃圆按钮（SHELL-12）：Material 3 图标按钮样式 <c>Widget.PhotoViewer.GlassButton</c> 承载常态底材、
/// 状态层与涟漪；常态底材取 <c>color.toolbar.button.tint</c> 与 <c>shell.glass.button.tint.opacity</c> 的同一色层，
/// uicons 字形放在 Material 图标槽并按墨迹居中，尺寸固定 <c>shell.glass.button.size</c>（44），
/// 不随内容拉宽；禁用只把内容降到 <c>color.toolbar.disabled.content.alpha</c>，底色与几何不变。
/// </summary>
internal sealed class AndroidGlassButton : MaterialButton
{
    private readonly string _glyph;
    private readonly string? _number;
    private readonly bool _iconFont;

    /// <summary>按钮边长（dp）。</summary>
    internal static double Size => DesignTokens.ShellGlassButtonSize;

    /// <summary>建一个图标按钮。</summary>
    /// <param name="context">宿主上下文。</param>
    /// <param name="glyph">uicons 字形或 <c>U+</c> 码位令牌；<paramref name="iconFont"/> 为 false 时是普通文字。</param>
    /// <param name="description">读屏名称（与按钮语义一致）。</param>
    /// <param name="solid">是否取实心图标字体（评分快捷命令的星底）。</param>
    /// <param name="number">评分快捷命令的星级（1–5 叠在星形中心，"0" 为零清叉、不叠数字；null 表示非评分命令）。</param>
    /// <param name="iconFont">是否用 uicons 字形绘制（false 为普通文字入口，如相似聚类开关）。</param>
    public AndroidGlassButton(Context context, string glyph, string description, bool solid = false,
        string? number = null, bool iconFont = true) : base(context, null, Resource.Attribute.pvGlassButtonStyle)
    {
        _glyph = iconFont && glyph.StartsWith("U+", StringComparison.Ordinal) ? AndroidUiTheme.IconGlyph(glyph) : glyph;
        _number = number;
        _iconFont = iconFont;
        ContentDescription = description;
        CornerRadius = (int)Math.Round(Size / 2 * AndroidUiTheme.Density(context));
        BackgroundTintList = ColorStateList.ValueOf(AndroidUiTheme.GlassButtonFill);
        Focusable = true;
        Clickable = true;
        if (iconFont)
        {
            var icon = new AndroidGlyphDrawable(context, _glyph, solid, number == "0" ? null : number);
            Icon = icon;
            IconSize = icon.IntrinsicWidth;
            IconTint = ContentColors(context);
            return;
        }
        Text = _glyph;
        SetTextSize(ComplexUnitType.Sp, 12);
        SetTypeface(Typeface.Create(Typeface.Default, TypefaceStyle.Normal), TypefaceStyle.Normal);
        SetTextColor(ContentColors(context));
    }

    /// <summary>当前使用的字形（验收读取）。</summary>
    internal string Glyph => _glyph;

    /// <summary>评分快捷命令的星级（null 表示非评分命令；"0" 为零清叉、不叠数字）。</summary>
    internal string? Number => _number;

    /// <summary>按钮实际底色（验收读取）。</summary>
    internal Color FillColorForChecks => new(BackgroundTintList?.DefaultColor ?? 0);

    /// <summary>按钮在指定状态下的实际底色（验收读取；用于可勾选入口的未选中态）。</summary>
    /// <param name="state">视图状态。</param>
    /// <returns>该状态的底色。</returns>
    internal Color FillColorForStateForChecks(int[] state)
    {
        var tint = BackgroundTintList;
        return tint == null ? Color.Transparent : new(tint.GetColorForState(state, Color.Transparent));
    }

    /// <summary>字形颜色：可用主色、禁用按令牌降内容强调（不改底色）。</summary>
    private static ColorStateList ContentColors(Context context)
    {
        var primary = AndroidUiTheme.TextPrimary;
        int alpha = (int)Math.Round(255 * DesignTokens.ColorToolbarDisabledContentAlpha);
        return new ColorStateList(
            [new[] { global::Android.Resource.Attribute.StateEnabled }, []],
            [primary.ToArgb(), Color.Argb(alpha, Color.GetRedComponent(primary),
                Color.GetGreenComponent(primary), Color.GetBlueComponent(primary)).ToArgb()]);
    }

    /// <summary>
    /// 固定分段尺寸：先让按钮以最终外框建立内容布局（图标槽居中依赖它），再强制精确外框；
    /// 文本入口在无界测量时按文本自然宽加左右内边距给出宽度。
    /// </summary>
    /// <param name="widthMeasureSpec">宽约束。</param>
    /// <param name="heightMeasureSpec">高约束。</param>
    protected override void OnMeasure(int widthMeasureSpec, int heightMeasureSpec)
    {
        int width = MeasureSpec.GetSize(widthMeasureSpec);
        int height = MeasureSpec.GetSize(heightMeasureSpec);
        if (!_iconFont && MeasureSpec.GetMode(widthMeasureSpec) != MeasureSpecMode.Exactly && Paint != null)
            width = (int)Math.Ceiling(Paint.MeasureText(Text) + AndroidUiTheme.Dp(Context!, 24));
        base.OnMeasure(MeasureSpec.MakeMeasureSpec(width, MeasureSpecMode.Exactly),
            MeasureSpec.MakeMeasureSpec(height, MeasureSpecMode.Exactly));
        SetMeasuredDimension(width, height);
    }

    /// <summary>
    /// 图标槽字形在按钮内的墨迹中心与边界（验收用）：把按钮实际绘制到离屏位图，取字形色（高亮且近不透明）像素的外接矩形，
    /// 图标槽的横向落位（Material 图标位置）与纵向落位（TextView 复合图标居中）都以真实绘制结果为准。
    /// </summary>
    /// <returns>中心坐标（px）与墨迹矩形（px）。</returns>
    internal (float CenterX, float CenterY, Rect Bounds) GlyphCenterForChecks()
    {
        int width = Width, height = Height;
        if (width <= 0 || height <= 0) return (width / 2f, height / 2f, new Rect());
        using var bitmap = Bitmap.CreateBitmap(width, height, Bitmap.Config.Argb8888!)!;
        using (var canvas = new Canvas(bitmap)) Draw(canvas);
        var pixels = new int[width * height];
        bitmap.GetPixels(pixels, 0, width, 0, 0, width, height);
        int left = width, top = height, right = -1, bottom = -1;
        for (int y = 0; y < height; y++)
        for (int x = 0; x < width; x++)
        {
            int pixel = pixels[y * width + x];
            if (Color.GetAlphaComponent(pixel) < 128 || Color.GetRedComponent(pixel) < 0x99) continue;
            left = Math.Min(left, x);
            top = Math.Min(top, y);
            right = Math.Max(right, x + 1);
            bottom = Math.Max(bottom, y + 1);
        }
        if (right < 0) return (width / 2f, height / 2f, new Rect());
        return ((left + right) / 2f, (top + bottom) / 2f, new Rect(left, top, right, bottom));
    }

    /// <summary>合体分段用透明底：状态层与涟漪仍由 Material 背景绘制，胶囊外形只由容器一层承担。</summary>
    internal void UseTransparentFill()
    {
        BackgroundTintList = ColorStateList.ValueOf(Color.Transparent);
        CornerRadius = 0;
    }
}

/// <summary>
/// 两段玻璃合体按钮（SHELL-08、SHELL-09）：Material <see cref="MaterialCardView"/> 胶囊容器承载材质，
/// 内部两段透明底的 <see cref="AndroidGlassButton"/>，胶囊只由容器一层绘制，不画分隔线；
/// 段各自 44×44、读屏各有一项，镜像时只交换两段的位置。
/// </summary>
internal sealed class AndroidGlassCombo : MaterialCardView
{
    private readonly AndroidGlassButton _first;
    private readonly AndroidGlassButton _second;
    private bool _vertical;
    private bool _mirrored;

    /// <summary>建两段合体按钮。</summary>
    /// <param name="context">宿主上下文。</param>
    /// <param name="firstGlyph">前段字形。</param>
    /// <param name="firstDescription">前段读屏名称。</param>
    /// <param name="secondGlyph">后段字形。</param>
    /// <param name="secondDescription">后段读屏名称。</param>
    public AndroidGlassCombo(Context context, string firstGlyph, string firstDescription,
        string secondGlyph, string secondDescription) : base(context)
    {
        Radius = (float)AndroidUiTheme.Dp(context, Size / 2);
        CardElevation = 0;
        StrokeWidth = 0;
        SetCardBackgroundColor(AndroidUiTheme.GlassButtonFill);
        SetContentPadding(0, 0, 0, 0);
        Clickable = false;
        _first = new AndroidGlassButton(context, firstGlyph, firstDescription);
        _second = new AndroidGlassButton(context, secondGlyph, secondDescription);
        foreach (var button in new[] { _first, _second })
        {
            button.UseTransparentFill();
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

    /// <summary>横排时前段是否落在容器末端（刘海在右的手机横屏镜像）。</summary>
    internal bool Mirrored => _mirrored;

    /// <summary>胶囊容器实际底色（验收读取）。</summary>
    internal Color FillColorForChecks => new(CardBackgroundColor?.DefaultColor ?? 0);

    /// <summary>设置排布方向：横排两段左右相接，竖排两段上下相接。</summary>
    /// <param name="vertical">是否竖排。</param>
    internal void SetVertical(bool vertical)
    {
        if (_vertical == vertical) return;
        _vertical = vertical;
        RequestLayout();
    }

    /// <summary>设置横排镜像：前段落在容器末端，与镜像后的真实槽位一致。</summary>
    /// <param name="mirrored">是否镜像。</param>
    internal void SetMirrored(bool mirrored)
    {
        if (_mirrored == mirrored) return;
        _mirrored = mirrored;
        RequestLayout();
    }

    /// <summary>两段各占一半；镜像只改变视觉顺序。</summary>
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
            return;
        }
        int firstWidth = width / 2;
        if (_mirrored)
        {
            _second.Layout(0, 0, firstWidth, height);
            _first.Layout(firstWidth, 0, width, height);
        }
        else
        {
            _first.Layout(0, 0, firstWidth, height);
            _second.Layout(firstWidth, 0, width, height);
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
/// 紧凑态按共享星簇绘制并作为星级弹窗入口（起手即打开，手势由 <see cref="Popup"/> 继续跟踪）。
/// 展开态逐格悬停/按下在槽位内画内切正圆，胶囊外轮廓不变；
/// 读屏经 <see cref="ExploreByTouchHelper"/> 暴露槽位、0–5 的可调节范围与增减动作。
/// </summary>
internal sealed class AndroidRatingControl : MaterialCardView
{
    private readonly Paint _starActive;
    private readonly Paint _starInactive;
    private readonly Paint _starClear;
    private readonly Paint _conflictPaint;
    private readonly RippleDrawable[] _slotRipples = new RippleDrawable[RatingControlGeometry.SlotCount];
    private readonly ColorStateList _stateLayer;
    private readonly Paint _focusPaint;
    private readonly RatingAccessibilityHelper _accessibility;
    private bool _conflict;
    private int _rating;
    private bool _enabled = true;
    private int _hoverSlot = -1;
    private int _pressedSlot = -1;
    private bool _forwarding;
    private bool _capsuleHovered;
    private RatingGesture? _ratingGesture;

    /// <summary>星级写回入口（0–5；冲突时 0 号不写值，改由宿主打开弹窗）。</summary>
    internal event Action<int>? RateRequested;

    /// <summary>打开星级弹窗（紧凑态起手、展开态冲突三角与读屏激活）。</summary>
    internal event Action? PopupRequested;

    /// <summary>宿主注入的星级弹窗：紧凑态把同一次触摸转发给它（展开态不参与）。</summary>
    internal AndroidRatingPopup? Popup { get; set; }

    /// <summary>胶囊是否横排（竖屏底行为 true，横屏控制栏为 false）。</summary>
    internal bool Horizontal { get; set; } = true;

    /// <summary>胶囊主轴长度（dp，取布局落定后的视图宽高）。</summary>
    internal double SlotMain => Horizontal ? Width : Height;

    /// <summary>当前形态（验收读取）。</summary>
    internal RatingControlForm Form => RatingControlGeometry.ResolveForm(SlotMain / AndroidUiTheme.Density(Context!));

    /// <summary>悬停槽位（-1 表示没有；验收读取）。</summary>
    internal int HoverSlotForChecks => _hoverSlot;

    /// <summary>按下槽位（-1 表示没有；验收读取）。</summary>
    internal int PressedSlotForChecks => _pressedSlot;

    /// <summary>胶囊实际底色（验收读取）：与命令圆按钮同一按钮材质。</summary>
    internal Color FillColorForChecks => new(CardBackgroundColor?.DefaultColor ?? 0);

    /// <summary>逐格悬停/按下状态层颜色（验收读取）：与按钮同一状态层色板，取按下态。</summary>
    internal Color HighlightColorForChecks
        => new(_stateLayer.GetColorForState([global::Android.Resource.Attribute.StatePressed], Color.White));

    /// <summary>指定槽位的状态层（验收读取）。</summary>
    /// <param name="slot">槽位 0–5。</param>
    /// <returns>该槽位的涟漪 Drawable。</returns>
    internal RippleDrawable SlotRippleForChecks(int slot) => _slotRipples[Math.Clamp(slot, 0, RatingControlGeometry.SlotCount - 1)];

    /// <summary>指定槽位的内切正圆边界（px，验收读取）。</summary>
    /// <param name="slot">槽位 0–5。</param>
    /// <returns>该槽位的悬停/按下圆。</returns>
    internal Rect HoverCircleBoundsForChecks(int slot)
    {
        var rect = SlotRectPx(slot);
        int radius = Math.Min(rect.Width(), rect.Height()) / 2;
        int cx = rect.CenterX(), cy = rect.CenterY();
        return new Rect(cx - radius, cy - radius, cx + radius, cy + radius);
    }

    /// <summary>建星级控件。</summary>
    /// <param name="context">宿主上下文。</param>
    public AndroidRatingControl(Context context) : base(context)
    {
        Radius = (float)AndroidUiTheme.Dp(context, RatingControlGeometry.Thickness / 2);
        CardElevation = 0;
        StrokeWidth = 0;
        SetCardBackgroundColor(AndroidUiTheme.GlassButtonFill);
        SetContentPadding(0, 0, 0, 0);
        Clickable = false;
        _starActive = StarPaint(context, AndroidUiTheme.StarActive, solid: true);
        _starInactive = StarPaint(context, AndroidUiTheme.StarInactive);
        _starClear = StarPaint(context, AndroidUiTheme.StarClear);
        _conflictPaint = StarPaint(context, AndroidUiTheme.StarConflict);
        _stateLayer = AndroidX.Core.Content.ContextCompat.GetColorStateList(context, Resource.Color.pv_glass_state_layer)
            ?? ColorStateList.ValueOf(Color.Transparent);
        for (int slot = 0; slot < _slotRipples.Length; slot++)
        {
            var mask = new ShapeDrawable(new OvalShape());
            var ripple = new RippleDrawable(_stateLayer, null, mask);
            ripple.SetCallback(this);
            ripple.SetVisible(true, false);
            _slotRipples[slot] = ripple;
        }
        _focusPaint = AndroidUiTheme.Paint(AndroidUiTheme.Accent, Math.Max(1, AndroidUiTheme.Dp(context, 2)));
        Focusable = true;
        ImportantForAccessibility = ImportantForAccessibility.Yes;
        _accessibility = new RatingAccessibilityHelper(this);
        ViewCompat.SetAccessibilityDelegate(this, _accessibility);
        UpdateAccessibilityName();
    }

    private static Paint StarPaint(Context context, Color color, bool solid = false)
    {
        var paint = AndroidUiTheme.Paint(color);
        paint.TextSize = AndroidUiTheme.Sp(context, DesignTokens.ToolbarRatingGlyphFontSize);
        var typeface = solid ? AndroidUiTheme.IconSolidTypeface(context) : AndroidUiTheme.IconTypeface(context);
        if (typeface != null) paint.SetTypeface(typeface);
        return paint;
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
        UpdateAccessibilityName();
        _accessibility.InvalidateRoot();
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

    /// <summary>绘制胶囊内的星排或紧凑星簇；展开态先画槽位内的悬停/按下正圆。</summary>
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
        if (form == RatingControlForm.Expanded && _enabled)
        {
            for (int value = 0; value < RatingControlGeometry.SlotCount; value++)
                DrawSlotStateLayer(canvas, value, density);
        }
        if (form == RatingControlForm.Compact && _capsuleHovered)
        {
            // 紧凑态整胶囊高亮：与按钮/槽位同一份状态层色板（Material 悬停不透明度）。
            using var hover = AndroidUiTheme.Paint(new Color(_stateLayer.GetColorForState(
                [global::Android.Resource.Attribute.StateHovered], AndroidUiTheme.TextPrimary)));
            var rect = new RectF((float)(control.X * density), (float)(control.Y * density),
                (float)(control.MaxX * density), (float)(control.MaxY * density));
            canvas.DrawRoundRect(rect, Math.Min(rect.Width(), rect.Height()) / 2, Math.Min(rect.Width(), rect.Height()) / 2, hover);
        }
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
        }
        else
        {
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
        if (IsFocused)
        {
            var focus = new RectF(
                (float)(control.X * density) + density, (float)(control.Y * density) + density,
                (float)(control.MaxX * density) - density, (float)(control.MaxY * density) - density);
            canvas.DrawRoundRect(focus, focus.Height() / 2, focus.Height() / 2, _focusPaint);
        }
    }

    /// <summary>
    /// 逐格状态层：原生 <see cref="RippleDrawable"/>（与按钮同一状态层色板、椭圆 mask），
    /// 边界为该槽位的内切正圆，按悬停/按下设置 state_hovered / state_pressed。
    /// </summary>
    private void DrawSlotStateLayer(Canvas canvas, int value, float density)
    {
        var rect = SlotRectPx(value);
        if (rect.Width() <= 0 || rect.Height() <= 0) return;
        int radius = Math.Min(rect.Width(), rect.Height()) / 2;
        int cx = rect.CenterX(), cy = rect.CenterY();
        var ripple = _slotRipples[value];
        ripple.SetBounds(cx - radius, cy - radius, cx + radius, cy + radius);
        int[] state = value == _pressedSlot ? _pressedState
            : value == _hoverSlot ? _hoveredState
            : _emptyState;
        ripple.SetState(state);
        ripple.Draw(canvas);
    }

    private static readonly int[] _pressedState = [global::Android.Resource.Attribute.StatePressed];
    private static readonly int[] _hoveredState = [global::Android.Resource.Attribute.StateHovered];
    private static readonly int[] _emptyState = [];

    /// <summary>按星级值绘制字形：0 为清零叉（冲突时警示三角），1–5 取实心/描边星。</summary>
    private void DrawGlyph(Canvas canvas, int value, double centerMain, double centerCross, float density, int alpha)
    {
        float x = (float)((Horizontal ? centerMain : centerCross) * density);
        float y = (float)((Horizontal ? centerCross : centerMain) * density);
        double glyphSize = Form == RatingControlForm.Compact
            ? RatingControlGeometry.CompactGlyph
            : DesignTokens.ToolbarRatingGlyphFontSize;
        if (value == 0)
        {
            if (_conflict)
            {
                _conflictPaint.TextSize = AndroidUiTheme.Sp(Context!, DesignTokens.ToolbarRatingGlyphFontSize);
                _conflictPaint.Alpha = alpha;
                AndroidUiTheme.DrawGlyphCentered(canvas, AndroidUiTheme.IconGlyph(DesignTokens.IconWarning), _conflictPaint, x, y);
                return;
            }
            _starClear.TextSize = AndroidUiTheme.Sp(Context!, DesignTokens.ToolbarRatingClearGlyphFontSize);
            _starClear.Alpha = alpha;
            AndroidUiTheme.DrawGlyphCentered(canvas, AndroidUiTheme.IconGlyph(DesignTokens.IconClear), _starClear, x, y);
            return;
        }
        bool active = value <= (_ratingGesture?.Preview ?? _rating);
        var paint = active ? _starActive : _starInactive;
        paint.TextSize = AndroidUiTheme.Sp(Context!, glyphSize);
        paint.Alpha = alpha;
        AndroidUiTheme.DrawGlyphCentered(canvas, AndroidUiTheme.IconGlyph(
            active ? DesignTokens.IconStarFilled : DesignTokens.IconStar), paint, x, y);
    }

    /// <summary>
    /// 展开态按下记录槽位、抬手按槽位写回（冲突三角只打开弹窗）；
    /// 紧凑态起手即打开星级弹窗并把同一次触摸的移动/抬手/取消转发给它。
    /// </summary>
    /// <param name="e">触摸事件。</param>
    /// <returns>是否消费。</returns>
    public override bool OnTouchEvent(MotionEvent? e)
    {
        if (e == null) return false;
        if (Form == RatingControlForm.Compact)
        {
            TrackCompact(e);
            return true;
        }
        double density = AndroidUiTheme.Density(Context!);
        var (control, length, crossLength) = ResolveControl();
        double main = Horizontal ? e.GetX() / density - control.X : e.GetY() / density - control.Y;
        double cross = Horizontal ? e.GetY() / density - control.Y : e.GetX() / density - control.X;
        int value = RatingControlGeometry.ValueAt(main, length, Horizontal, RatingControlGeometry.Slot);
        bool inBand = RatingControlGeometry.InBand(cross, 0, crossLength);
        switch (e.ActionMasked)
        {
            case MotionEventActions.Down:
                if (!_enabled) return true;
                Parent?.RequestDisallowInterceptTouchEvent(true);
                _ratingGesture = new(Math.Max(0, _rating), forceCommit: _conflict, zeroOpensPopup: _conflict);
                _ratingGesture.Begin(main, cross, value);
                _pressedSlot = value;
                break;
            case MotionEventActions.Move when _ratingGesture is { } moving:
                if (moving.Move(main, cross, value, inBand) && moving.Preview.HasValue)
                    PerformHapticFeedback(OperatingSystem.IsAndroidVersionAtLeast(34)
                        ? FeedbackConstants.SegmentTick : FeedbackConstants.ClockTick);
                _pressedSlot = moving.Preview ?? -1;
                break;
            case MotionEventActions.Up when _ratingGesture is { } ending:
                var result = ending.End(main, cross, value, inBand);
                _ratingGesture = null;
                _pressedSlot = -1;
                if (!_enabled) break;
                if (result.Outcome == RatingGestureOutcome.OpenPopup) PopupRequested?.Invoke();
                else if (result.Outcome == RatingGestureOutcome.Commit) RateRequested?.Invoke(result.Value);
                break;
            case MotionEventActions.Cancel:
            case MotionEventActions.PointerDown:
                _ratingGesture = null;
                _pressedSlot = -1;
                break;
        }
        Invalidate();
        return true;
    }

    /// <summary>
    /// 紧凑胶囊的手指跟踪：起手打开弹窗并把落点交给弹窗星排，之后的移动与抬手原样转发；
    /// 弹窗未打开（无当前照片）或为冲突来源条目时不转发，整套手势仍由本控件消费。
    /// </summary>
    /// <param name="e">触摸事件。</param>
    private void TrackCompact(MotionEvent e)
    {
        switch (e.ActionMasked)
        {
            case MotionEventActions.Down:
                _forwarding = false;
                _pressedSlot = -1;
                if (!_enabled) return;
                // 起手即属于弹窗预览：禁止持有滚动/翻页的祖先在拖动中抢走这次触摸。
                Parent?.RequestDisallowInterceptTouchEvent(true);
                PopupRequested?.Invoke();
                if (Popup is { IsOpen: true } popup && popup.BeginExternal(this, e)) _forwarding = true;
                Invalidate();
                return;
            case MotionEventActions.Move:
                if (_forwarding) Popup?.MoveExternal(this, e);
                return;
            case MotionEventActions.Up:
                if (_forwarding) Popup?.EndExternal(this, e);
                _forwarding = false;
                return;
            case MotionEventActions.Cancel:
            case MotionEventActions.PointerDown:
                if (_forwarding) Popup?.CancelExternal(this, e);
                _forwarding = false;
                return;
        }
    }

    /// <summary>真实鼠标/触控板悬停：展开态记录命中的槽位，紧凑态整胶囊高亮；位置变化才重绘。</summary>
    /// <param name="e">悬停事件。</param>
    /// <returns>是否消费。</returns>
    public override bool OnHoverEvent(MotionEvent? e)
    {
        if (e == null) return false;
        bool exited = e.ActionMasked == MotionEventActions.HoverExit || !_enabled;
        bool capsule = !exited && Form == RatingControlForm.Compact;
        int slot = exited || capsule ? -1 : SlotAt(e.GetX(), e.GetY());
        if (slot == _hoverSlot && capsule == _capsuleHovered) return true;
        _hoverSlot = slot;
        _capsuleHovered = capsule;
        Invalidate();
        return true;
    }

    /// <summary>触点命中的展开态槽位（0–5）；未命中或紧凑态返回 -1。</summary>
    /// <param name="x">本地横坐标（px）。</param>
    /// <param name="y">本地纵坐标（px）。</param>
    /// <returns>槽位值或 -1。</returns>
    internal int SlotAt(float x, float y)
    {
        if (Form != RatingControlForm.Expanded) return -1;
        for (int value = 0; value < RatingControlGeometry.SlotCount; value++)
        {
            var rect = SlotRectPx(value);
            if (rect.Width() > 0 && rect.Contains((int)x, (int)y)) return value;
        }
        return -1;
    }

    private void UpdateAccessibilityName()
        => ContentDescription = !_enabled
            ? "无照片"
            : _conflict ? "星级冲突" : _rating <= 0 ? "无星级" : $"星级 {_rating}";

    /// <summary>读屏：六格各为一个按钮节点，整体带 0–5 的整数范围与增减动作（RATING-07）。</summary>
    private sealed class RatingAccessibilityHelper : ExploreByTouchHelper
    {
        private readonly AndroidRatingControl _owner;

        /// <summary>绑定星级控件。</summary>
        /// <param name="owner">星级控件。</param>
        public RatingAccessibilityHelper(AndroidRatingControl owner) : base(owner) => _owner = owner;

        /// <summary>按本地像素坐标找槽位（紧凑态整胶囊算一个节点）。</summary>
        protected override int GetVirtualViewAt(float x, float y)
            => _owner.Form == RatingControlForm.Compact ? (_owner._enabled ? 0 : -1) : _owner.SlotAt(x, y);

        /// <summary>展开态列出六格；紧凑态只暴露一个代表整胶囊的可激活节点。</summary>
        protected override void GetVisibleVirtualViews(IList<Java.Lang.Integer>? virtualViewIds)
        {
            if (virtualViewIds == null) return;
            if (_owner.Form == RatingControlForm.Compact)
            {
                virtualViewIds.Add(Java.Lang.Integer.ValueOf(0));
                return;
            }
            for (int value = 0; value < RatingControlGeometry.SlotCount; value++)
                virtualViewIds.Add(Java.Lang.Integer.ValueOf(value));
        }

        /// <summary>填充名称、选中态、范围、增减动作与屏幕边界。</summary>
        protected override void OnPopulateNodeForVirtualView(int virtualViewId, AccessibilityNodeInfoCompat? node)
        {
            if (node == null) return;
            if (virtualViewId < 0 || virtualViewId >= RatingControlGeometry.SlotCount)
            {
                node.ContentDescription = "";
                node.SetBoundsInScreen(new Rect());
                return;
            }
            if (_owner.Form == RatingControlForm.Compact)
            {
                node.ContentDescription = _owner.ContentDescription;
                node.ClassName = "android.widget.Button";
                node.Focusable = true;
                node.Enabled = _owner._enabled;
                node.Clickable = _owner._enabled;
                node.VisibleToUser = true;
                if (_owner._enabled)
                {
                    node.AddAction((int)global::Android.Views.Accessibility.Action.Click);
                    node.AddAction((int)global::Android.Views.Accessibility.Action.ScrollForward);
                    node.AddAction((int)global::Android.Views.Accessibility.Action.ScrollBackward);
                }
                node.RangeInfo = AccessibilityNodeInfoCompat.RangeInfoCompat.Obtain(
                    AccessibilityNodeInfoCompat.RangeInfoCompat.RangeTypeInt, 0, 5,
                    Math.Clamp(_owner._rating, 0, 5));
                var location = new int[2];
                _owner.GetLocationOnScreen(location);
                node.SetBoundsInScreen(new Rect(location[0], location[1],
                    location[0] + _owner.Width, location[1] + _owner.Height));
                return;
            }
            node.ContentDescription = virtualViewId == 0
                ? (_owner._conflict ? "星级冲突" : "无星级")
                : $"{virtualViewId} 星";
            node.ClassName = "android.widget.RadioButton";
            node.Focusable = true;
            node.Enabled = _owner._enabled;
            node.Clickable = _owner._enabled;
            node.VisibleToUser = true;
            node.Selected = virtualViewId > 0 && _owner._rating == virtualViewId;
            if (_owner._enabled)
            {
                node.AddAction((int)global::Android.Views.Accessibility.Action.Click);
                node.AddAction((int)global::Android.Views.Accessibility.Action.ScrollForward);
                node.AddAction((int)global::Android.Views.Accessibility.Action.ScrollBackward);
            }
            node.RangeInfo = AccessibilityNodeInfoCompat.RangeInfoCompat.Obtain(
                AccessibilityNodeInfoCompat.RangeInfoCompat.RangeTypeInt, 0, 5, Math.Clamp(_owner._rating, 0, 5));
            var rect = _owner.SlotRectPx(virtualViewId);
            var slotLocation = new int[2];
            _owner.GetLocationOnScreen(slotLocation);
            node.SetBoundsInScreen(new Rect(slotLocation[0] + rect.Left, slotLocation[1] + rect.Top,
                slotLocation[0] + rect.Right, slotLocation[1] + rect.Bottom));
        }

        /// <summary>
        /// 读屏点按与增减：增减直接写回相邻星级；点按在紧凑态与冲突时打开星级弹窗，
        /// 展开态非冲突时写回该格（与触摸同一入口）。
        /// </summary>
        protected override bool OnPerformActionForVirtualView(int virtualViewId, int action, Bundle? arguments)
        {
            if (!_owner._enabled) return false;
            if (action == (int)global::Android.Views.Accessibility.Action.ScrollForward)
            {
                _owner.RateRequested?.Invoke(Math.Clamp(_owner._rating + 1, 0, 5));
                return true;
            }
            if (action == (int)global::Android.Views.Accessibility.Action.ScrollBackward)
            {
                _owner.RateRequested?.Invoke(Math.Clamp(_owner._rating - 1, 0, 5));
                return true;
            }
            if (action != (int)global::Android.Views.Accessibility.Action.Click) return false;
            if (_owner.Form == RatingControlForm.Compact || (virtualViewId == 0 && _owner._conflict))
            {
                _owner.PopupRequested?.Invoke();
                return true;
            }
            _owner.RateRequested?.Invoke(virtualViewId);
            return true;
        }
    }
}

/// <summary>
/// EXIF 摘要块（TOOLBAR-05）：项目、内边距与整项裁剪取共享 <see cref="ExifBlockGeometry"/>；
/// 横排为带衬底的块（<c>color.toolbar.block.background</c> 圆角底），竖排无衬底变体直接放在栏背景上，
/// 容器宽度为项目固有宽、文字在容器内尾部截断。整块只有一个点按目标并直达 EXIF 详情（TOOLBAR-06）。
/// </summary>
internal sealed class AndroidExifBlock : View
{
    private readonly Paint _labelPaint;
    private readonly Paint _valuePaint;
    private readonly MaterialShapeDrawable _backdrop;
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
        _backdrop.FillColor = ColorStateList.ValueOf(AndroidUiTheme.FromToken(DesignTokens.ColorToolbarBlockBackground));
        Clickable = true;
        Focusable = true;
        ContentDescription = "EXIF 详情";
        Click += (_, _) => ExifActivated?.Invoke();
    }

    /// <summary>当前显示的 EXIF 项（验收读取）。</summary>
    internal IReadOnlyList<ExifDisplayEntry> Items => _items;

    /// <summary>衬底颜色（验收读取；无衬底变体仍返回令牌值，绘制由视图背景决定）。</summary>
    internal Color BackdropColorForChecks => AndroidUiTheme.FromToken(DesignTokens.ColorToolbarBlockBackground);

    /// <summary>是否使用衬底（验收读取）。</summary>
    internal bool HasBackdropForChecks => !_bare;

    /// <summary>实际显示的项数（整项裁剪后）。</summary>
    internal int ShownItemCount => Math.Min(_items.Count, Math.Max(0, _visibleCount));

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
    /// <returns>前 <see cref="ShownItemCount"/> 个可见项目矩形。</returns>
    internal IReadOnlyList<LayoutRect> ItemFramesForChecks()
    {
        int shown = ShownItemCount;
        var intrinsic = ExifBlockGeometry.Size(shown, _horizontal, _bare);
        double density = AndroidUiTheme.Density(Context!);
        double blockMain = _bare && !_horizontal
            ? intrinsic.Height
            : (_horizontal ? Width : Height) / (density == 0 ? 1 : density);
        var frames = new List<LayoutRect>(shown);
        for (int index = 0; index < shown; index++)
            frames.Add(ExifBlockGeometry.ItemRect(index, shown, _horizontal, blockMain, _bare));
        return frames;
    }

    /// <summary>绘制项目：标签在上、值在下，按字体度量成对居中，并在整项内尾部截断。</summary>
    /// <param name="canvas">画布。</param>
    protected override void OnDraw(Canvas canvas)
    {
        base.OnDraw(canvas);
        int shown = ShownItemCount;
        if (shown <= 0 || Width < 1 || Height < 1) return;
        float density = (float)AndroidUiTheme.Density(Context!);
        // 无衬底纵排按「完整块」的固有高度计算项目，再只画放得下的前几项；
        // 若把已裁短的外框高度当整块高度，各项会被垂直压缩。
        var intrinsic = ExifBlockGeometry.Size(shown, _horizontal, _bare);
        double blockMain = _bare && !_horizontal
            ? intrinsic.Height
            : (_horizontal ? Width : Height) / density;
        float gap = AndroidUiTheme.Dp(Context!, 1);
        for (int index = 0; index < shown; index++)
        {
            var rect = ExifBlockGeometry.ItemRect(index, shown, _horizontal, blockMain, _bare);
            var item = _items[index];
            float left = (float)(rect.X * density), top = (float)(rect.Y * density);
            float width = (float)(rect.Width * density), height = (float)(rect.Height * density);
            float centerX = left + width / 2;
            _labelPaint.TextSize = AndroidUiTheme.Sp(Context!, DesignTokens.ToolbarExifLabelFontSize);
            _valuePaint.TextSize = AndroidUiTheme.Sp(Context!, DesignTokens.ToolbarExifValueFontSize);
            var labelMetrics = _labelPaint.GetFontMetrics()!;
            var valueMetrics = _valuePaint.GetFontMetrics()!;
            float labelHeight = labelMetrics.Bottom - labelMetrics.Top;
            float valueHeight = valueMetrics.Bottom - valueMetrics.Top;
            float pairTop = top + (height - (labelHeight + gap + valueHeight)) / 2;
            float labelBaseline = pairTop - labelMetrics.Top;
            float valueBaseline = pairTop + labelHeight + gap - valueMetrics.Top;
            canvas.Save();
            canvas.ClipRect(left, top, left + Math.Min(width, Width), top + height);
            AndroidUiTheme.DrawCentered(canvas, item.DisplayName, _labelPaint, centerX, labelBaseline, Math.Min(width, Width));
            AndroidUiTheme.DrawCentered(canvas, item.DisplayValue, _valuePaint, centerX, valueBaseline, Math.Min(width, Width));
            canvas.Restore();
        }
    }
}
