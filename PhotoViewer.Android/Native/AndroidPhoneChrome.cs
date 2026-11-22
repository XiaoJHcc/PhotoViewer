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
using PhotoViewer.Android.Backdrop;
using PhotoViewer.Core.Design;
using PhotoViewer.Core.Input;
using PhotoViewer.Core.Platform;
using PhotoViewer.Shared;
using PhotoViewer.ViewModels.Main;
using PhotoViewer.ViewModels.Settings;

namespace PhotoViewer.Android.Native;

/// <summary>
/// 栏位背景材质（SHELL-12@android）：API 31 及以上从共享照片层取样（提饱和 → 模糊）再叠
/// <c>color.bar.overlay@android</c> × <c>shell.bar.overlay.opacity@android</c> 的同一栏底色层；
/// API 24–30 不取样，只画这层正式色层。产品路径不截屏、不用 <c>PixelCopy</c>、不做离屏拷贝。
/// </summary>
internal sealed class AndroidBarScrim : View
{
    private readonly AndroidPhotoMaterial? _material;
    private readonly AndroidPhotoMaterial.Consumer? _materialConsumer;
    private readonly int[] _location = new int[2];

    /// <summary>建一个栏底材质视图。</summary>
    /// <param name="context">宿主上下文。</param>
    /// <param name="material">共享照片层材质；null 表示不取样，只画色层（检查与低版本路径）。</param>
    public AndroidBarScrim(Context context, AndroidPhotoMaterial? material = null) : base(context)
    {
        _material = material;
        _materialConsumer = material?.CreateConsumer(material.BarToneGamma);
        ImportantForAccessibility = ImportantForAccessibility.No;
    }

    /// <summary>栏底色层的实际颜色（验收读取）：两种分级都铺这层同一色层。</summary>
    internal Color ColorForChecks => AndroidUiTheme.BarOverlay;

    /// <summary>本视图是否走真实照片层材质（验收读取；API 24–30 恒为 false）。</summary>
    internal bool UsesPhotoMaterialForChecks => _materialConsumer is not null;

    /// <summary>本视图实际取样的源（验收读取：与根视图照片层同一份 <see cref="IBackdrop"/>；不取样时为 null）。</summary>
    internal IBackdrop? BackdropSourceForChecks => _material?.Source;

    /// <summary>绘制栏底材质：先裁到自身边界，取样后叠色层，save/restore 成对。</summary>
    /// <param name="canvas">画布。</param>
    protected override void OnDraw(Canvas canvas)
    {
        base.OnDraw(canvas);
        // 取样画的是整层，必须先裁到本视图边界；漏了会画到别处，且 Canvas 裁剪不会自动收回。
        int save = canvas.Save();
        try
        {
            canvas.ClipRect(0, 0, Width, Height);
            _materialConsumer?.Draw(canvas, BackdropDrawContext.ForView(this, _location));
            canvas.DrawColor(AndroidUiTheme.BarOverlay);
        }
        finally
        {
            canvas.RestoreToCount(save);
        }
    }

    /// <summary>释放本视图的效果 scope 并注销离屏占用。</summary>
    /// <param name="disposing">是否释放托管资源。</param>
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _materialConsumer?.Dispose();
        }

        base.Dispose(disposing);
    }
}

/// <summary>
/// Fluent UI System Icons 字形图标：按字体单元中心（原生 advance/基线）在自身布局边界内居中，可作为
/// <see cref="MaterialButton"/> 的图标槽（评分快捷命令另叠 1–5 数字，数字仍按墨迹居中）。
/// 布局边界与字号解耦：字号永远取令牌的精确浮点像素，边界只决定占位与绘制中心，绝不反向缩放字形；
/// 字形超出边界时由宿主按钮/分段的完整矩形裁切。着色取 Material 的 <c>iconTint</c>（含禁用态）。
/// </summary>
internal sealed class AndroidGlyphDrawable : Drawable
{
    private readonly string _glyph;
    private readonly string? _digit;
    private readonly Paint _glyphPaint;
    private readonly Paint? _digitPaint;
    private readonly int _slot;
    private readonly bool _solid;
    private readonly View? _clipTo;
    private ColorStateList? _tint;
    private int _alpha = 255;

    /// <summary>建一个字形图标。</summary>
    /// <param name="context">宿主上下文。</param>
    /// <param name="glyph">Fluent UI System Icons 字形。</param>
    /// <param name="solid">是否取实心图标字体（评分快捷命令的星底）。</param>
    /// <param name="digit">叠在字形中心的数字（评分快捷命令 1–5；null 表示不叠）。</param>
    /// <param name="sizeDp">字形字号（dp 令牌），画笔字号取它的精确浮点像素值，不随槽位变化。</param>
    /// <param name="slotDp">
    /// 布局槽边长（dp 令牌）：<see langword="null"/> 时取字号对应的 1.2em 排版框加光栅余量，
    /// 保证字形完整落在槽内；有专用槽位令牌（如菜单图标、设置命令图标）时传该令牌。
    /// </param>
    /// <param name="clipTo">绘制时的裁切上下文（宿主按钮或分段），字形溢出图标槽时仍被控件矩形裁切。</param>
    public AndroidGlyphDrawable(Context context, string glyph, bool solid, string? digit,
        double sizeDp = DesignTokens.ToolbarCommandGlyphFontSize, double? slotDp = null, View? clipTo = null)
    {
        _glyph = glyph;
        _digit = string.IsNullOrEmpty(digit) ? null : digit;
        _solid = solid;
        _clipTo = clipTo;
        _slot = slotDp is { } slot ? AndroidUiTheme.IconSlotPx(context, slot) : AndroidUiTheme.GlyphSlotPx(context, sizeDp);
        _glyphPaint = AndroidUiTheme.Paint(Color.White);
        _glyphPaint.TextSize = AndroidUiTheme.Dp(context, sizeDp);
        var typeface = solid ? AndroidUiTheme.IconSolidTypeface(context) : AndroidUiTheme.IconTypeface(context);
        if (typeface != null) _glyphPaint.SetTypeface(typeface);
        if (_digit == null) return;
        _digitPaint = AndroidUiTheme.TextPaint(context, DesignTokens.ToolbarRatingDigitFontSize,
            AndroidUiTheme.FromToken(DesignTokens.ColorToolbarCommandBackground), bold: true);
        _digitPaint.TextSize = AndroidUiTheme.Dp(context, DesignTokens.ToolbarRatingDigitFontSize);
    }

    /// <summary>字形文本（验收读取）。</summary>
    internal string Glyph => _glyph;

    /// <summary>叠在字形中心的数字（null 表示没有）。</summary>
    internal string? Digit => _digit;

    /// <summary>是否使用实心图标字体（验收读取）。</summary>
    internal bool SolidGlyph => _solid;

    /// <summary>字形字号（px，验收读取）：恒为令牌 dp 的精确浮点像素，不因槽位变小而缩小。</summary>
    internal float FontSizePx => _glyphPaint.TextSize;

    /// <summary>叠字字号（px，验收读取）；不叠数字时为 0。</summary>
    internal float DigitFontSizePx => _digitPaint?.TextSize ?? 0;

    /// <summary>布局槽边长（px）：图标占位与绘制中心由它给出，与字号无关。</summary>
    internal int SlotPx => _slot;

    /// <summary>固有边长（px）：等于布局槽，按钮按它占位。</summary>
    public override int IntrinsicWidth => _slot;

    /// <summary>固有边长（px）。</summary>
    public override int IntrinsicHeight => _slot;

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

    /// <summary>
    /// 在自身布局边界中心按字体单元中心绘制字形，再按墨迹中心叠数字。
    /// 裁切上下文取宿主按钮/分段的完整矩形：字形比图标槽宽时仍完整落在控件内，不会被槽位边界截断。
    /// </summary>
    /// <param name="canvas">画布。</param>
    public override void Draw(Canvas canvas)
    {
        var bounds = Bounds;
        if (string.IsNullOrEmpty(_glyph) || bounds.Width() <= 0 || bounds.Height() <= 0) return;
        int color = _tint?.GetColorForState(GetState(), Color.White) ?? Color.White.ToArgb();
        int alpha = Color.GetAlphaComponent(color) * _alpha / 255;
        _glyphPaint.Color = new Color(color);
        _glyphPaint.Alpha = alpha;
        int save = -1;
        if (_clipTo is { Width: > 0, Height: > 0 } clip)
        {
            save = canvas.Save();
            canvas.ClipRect(0, 0, clip.Width, clip.Height);
        }
        AndroidUiTheme.DrawGlyphCentered(canvas, _glyph, _glyphPaint, bounds.ExactCenterX(), bounds.ExactCenterY());
        if (_digitPaint != null && _digit != null)
        {
            _digitPaint.Alpha = alpha;
            AndroidUiTheme.DrawTextInkCentered(canvas, _digit, _digitPaint, bounds.ExactCenterX(), bounds.ExactCenterY());
        }
        if (save >= 0) canvas.RestoreToCount(save);
    }

}

/// <summary>
/// 玻璃圆按钮（SHELL-12）：Material 3 图标按钮样式 <c>Widget.PhotoViewer.GlassButton</c> 承载常态底材、
/// 状态层与涟漪；常态底材取 <c>color.toolbar.button.tint</c> 与 <c>shell.glass.button.tint.opacity</c> 的同一色层，
/// Fluent UI System Icons 字形放在 Material 图标槽并按墨迹居中，尺寸固定 <c>shell.glass.button.size</c>（44），
/// 不随内容拉宽；禁用只把内容降到 <c>color.toolbar.disabled.content.alpha</c>，底色与几何不变。
/// </summary>
internal sealed class AndroidGlassButton : MaterialButton, IGlassAnchorContent
{
    private readonly string _glyph;
    private readonly string? _number;
    private readonly bool _iconFont;
    private bool _glassAnchored;
    private bool _suppressScene;
    private bool _transparentFill;
    private ColorStateList? _glassBackgroundTint;
    private ColorStateList? _glassStrokeColor;
    private int _glassStrokeWidth;

    /// <summary>玻璃层内容或状态变化（由绘制层订阅，用于让玻璃层重绘）。</summary>
    public event Action? GlassContentInvalidated;

    /// <summary>按钮边长（dp）。</summary>
    internal static double Size => DesignTokens.ShellGlassButtonSize;

    /// <summary>建一个图标按钮。</summary>
    /// <param name="context">宿主上下文。</param>
    /// <param name="glyph">Fluent UI System Icons 字形；<paramref name="iconFont"/> 为 false 时是普通文字。</param>
    /// <param name="description">读屏名称（与按钮语义一致）。</param>
    /// <param name="solid">是否取实心图标字体（评分快捷命令的星底）。</param>
    /// <param name="number">评分快捷命令的星级（1–5 叠在星形中心，"0" 为零清叉、不叠数字；null 表示非评分命令）。</param>
    /// <param name="iconFont">是否用 Fluent UI System Icons 字形绘制（false 为普通文字入口，如相似聚类开关）。</param>
    public AndroidGlassButton(Context context, string glyph, string description, bool solid = false,
        string? number = null, bool iconFont = true) : base(context, null, Resource.Attribute.pvGlassButtonStyle)
    {
        _glyph = glyph;
        _number = number;
        _iconFont = iconFont;
        ContentDescription = description;
        CornerRadius = (int)Math.Round(Size / 2 * AndroidUiTheme.Density(context));
        BackgroundTintList = ColorStateList.ValueOf(AndroidUiTheme.GlassButtonFill);
        Focusable = true;
        Clickable = true;
        if (iconFont)
        {
            var icon = new AndroidGlyphDrawable(context, _glyph, solid, number == "0" ? null : number,
                DesignTokens.ToolbarCommandGlyphFontSize, clipTo: this);
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

    /// <summary>本按钮的材质与图标是否由玻璃控件层绘制（验收读取）。</summary>
    internal bool IsGlassAnchored => _glassAnchored;

    /// <summary>玻璃层使用的表面色：常态取玻璃按钮色层，可勾选入口按 checked 态取值（验收读取）。</summary>
    internal Color GlassSurfaceColor
    {
        get
        {
            if (_transparentFill) return Color.Transparent!;
            if (!_glassAnchored || !Checkable) return AndroidUiTheme.GlassButtonFill;
            return _glassBackgroundTint is { } tint
                ? AndroidUiTheme.FromToken(unchecked((uint)tint.GetColorForState(GetDrawableState(), Color.Transparent)))
                : AndroidUiTheme.GlassButtonFill;
        }
    }

    /// <summary>玻璃层使用的描边色（仅可勾选入口的 checked 态；验收读取）。</summary>
    internal Color GlassBorderColor
    {
        get
        {
            if (!_glassAnchored || _glassStrokeWidth <= 0 || _glassStrokeColor is not { } stroke)
                return Color.Transparent!;
            return AndroidUiTheme.FromToken(unchecked((uint)stroke.GetColorForState(
                GetDrawableState(), Color.Transparent)));
        }
    }

    /// <summary>玻璃层使用的描边宽度（px，验收读取）。</summary>
    internal int GlassBorderWidth => _glassAnchored ? _glassStrokeWidth : 0;

    /// <summary>
    /// 把材质与图标交给玻璃控件层绘制：本视图保持透明，保留背景色/状态层供绘制层取色；
    /// 按钮仍是真实视图，承担布局、点击、焦点与读屏。
    /// </summary>
    internal void UseGlassAnchor()
    {
        if (_glassAnchored) return;
        _glassAnchored = true;
        _suppressScene = true;
        _glassBackgroundTint = BackgroundTintList;
        _glassStrokeColor = StrokeColor;
        _glassStrokeWidth = StrokeWidth;
        BackgroundTintList = ColorStateList.ValueOf(Color.Transparent!);
        StrokeWidth = 0;
        RippleColor = null;
        StateListAnimator = null;
        Elevation = 0;
        Invalidate();
    }

    /// <summary>玻璃控件层调用：在当前画布本地坐标绘制按钮内容（不含玻璃材质与背景）。</summary>
    /// <param name="canvas">已平移到按钮本地原点的画布。</param>
    internal void DrawGlassContent(Canvas canvas)
    {
        if (!_glassAnchored)
        {
            Draw(canvas);
            return;
        }
        base.OnDraw(canvas);
        base.DispatchDraw(canvas);
    }

    /// <inheritdoc />
    public override void Invalidate()
    {
        base.Invalidate();
        GlassContentInvalidated?.Invoke();
    }

    /// <summary>场景内不绘制自身内容；内容由玻璃控件层按锚点重绘。</summary>
    /// <param name="canvas">画布。</param>
    protected override void OnDraw(Canvas? canvas)
    {
        if (_suppressScene) return;
        base.OnDraw(canvas!);
    }

    /// <summary>场景内不绘制子内容。</summary>
    /// <param name="canvas">画布。</param>
    protected override void DispatchDraw(Canvas? canvas)
    {
        if (_suppressScene) return;
        base.DispatchDraw(canvas!);
    }

    /// <summary>评分快捷命令的星级（null 表示非评分命令；"0" 为零清叉、不叠数字）。</summary>
    internal string? Number => _number;

    /// <summary>按钮实际底色（验收读取）：玻璃锚点返回绘制层表面色。</summary>
    internal Color FillColorForChecks => _glassAnchored
        ? (_transparentFill ? Color.Transparent! : GlassSurfaceColor)
        : new(BackgroundTintList?.DefaultColor ?? 0);

    /// <summary>按钮在指定状态下的实际底色（验收读取；用于可勾选入口的未选中态）。</summary>
    /// <param name="state">视图状态。</param>
    /// <returns>该状态的底色。</returns>
    internal Color FillColorForStateForChecks(int[] state)
    {
        var tint = _glassAnchored ? _glassBackgroundTint : BackgroundTintList;
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
    /// 验收用：按产品真实绘制路径把按钮内容画到调用方画布（玻璃锚点为控件层内容，其余为按钮自绘），
    /// 供墨迹探针与无界参照字形逐像素比较。
    /// </summary>
    /// <param name="canvas">画布（按钮本地坐标）。</param>
    internal void DrawContentForChecks(Canvas canvas) => DrawGlassContent(canvas);

    /// <summary>验收用：按钮局部坐标中的预期图标槽。Drawable.Bounds 未包含 TextView 绘制复合图标时的画布平移。</summary>
    internal Rect IconSlotForChecks
    {
        get
        {
            int size = IconForChecks?.SlotPx ?? 0;
            int left = (Width - size) / 2, top = (Height - size) / 2;
            return new Rect(left, top, left + size, top + size);
        }
    }

    /// <summary>验收用：图标字形（普通文字入口为 null）。</summary>
    internal AndroidGlyphDrawable? IconForChecks => Icon as AndroidGlyphDrawable;

    /// <summary>合体分段用透明底：状态层与涟漪仍由 Material 背景绘制，胶囊外形只由容器一层承担。</summary>
    internal void UseTransparentFill()
    {
        _transparentFill = true;
        BackgroundTintList = ColorStateList.ValueOf(Color.Transparent);
        CornerRadius = 0;
    }
}

/// <summary>胶囊内一段：字形、读屏名称、命令标识与可用性。</summary>
/// <param name="Glyph">Fluent UI System Icons 字形。</param>
/// <param name="Description">读屏名称（与宽形态命令同源）。</param>
/// <param name="Command">共享命令标识；null 表示纯动作段（筛选/更多、上下张）。</param>
/// <param name="Enabled">是否可用（无照片时除打开文件外禁用）。</param>
/// <param name="Solid">评分快捷命令的实心星底。</param>
/// <param name="Number">评分快捷命令叠在星形中心的数字。</param>
internal readonly record struct GlassComboSegment(
    string Glyph,
    string Description,
    string? Command = null,
    bool Enabled = true,
    bool Solid = false,
    string? Number = null);

/// <summary>
/// 同胶囊玻璃按钮组（SHELL-08、SHELL-09）：Material <see cref="MaterialCardView"/> 胶囊容器承载材质，
/// 内部一至三段透明底的 <see cref="AndroidGlassButton"/>，胶囊只由容器一层绘制，不画分隔线；
/// 每段是独立可聚焦、可点按、带读屏名称的动作按钮（不是分段选择控件），镜像时只交换段的视觉顺序。
/// 筛选/更多与上下张为固定两段，手机可选命令组按生效命令动态 1–3 段。
/// </summary>
internal sealed class AndroidGlassCombo : MaterialCardView, IGlassAnchorContent
{
    private readonly List<AndroidGlassButton> _segments = [];
    private readonly List<string> _segmentKeys = [];
    private bool _vertical;
    private bool _mirrored;
    private bool _glassAnchored;
    private bool _suppressScene;

    /// <summary>玻璃层内容或状态变化（段按钮的状态也会冒泡到这里）。</summary>
    public event Action? GlassContentInvalidated;

    /// <summary>建空胶囊容器（手机可选命令组按生效命令填段）。</summary>
    /// <param name="context">宿主上下文。</param>
    public AndroidGlassCombo(Context context) : base(context)
    {
        CardElevation = 0;
        StrokeWidth = 0;
        SetCardBackgroundColor(AndroidUiTheme.GlassButtonFill);
        SetContentPadding(0, 0, 0, 0);
        Clickable = false;
        SetClipChildren(true);
        ClipToOutline = true;
    }

    /// <summary>建两段合体按钮。</summary>
    /// <param name="context">宿主上下文。</param>
    /// <param name="firstGlyph">前段字形。</param>
    /// <param name="firstDescription">前段读屏名称。</param>
    /// <param name="secondGlyph">后段字形。</param>
    /// <param name="secondDescription">后段读屏名称。</param>
    public AndroidGlassCombo(Context context, string firstGlyph, string firstDescription,
        string secondGlyph, string secondDescription) : this(context)
    {
        SetSegments([new GlassComboSegment(firstGlyph, firstDescription),
            new GlassComboSegment(secondGlyph, secondDescription)]);
    }

    /// <summary>两段合体（筛选/更多、上下张）的边长（dp）。</summary>
    internal static double Size => AndroidGlassButton.Size * 2;

    /// <summary>命令段被点按时触发（参数为共享命令标识）。</summary>
    internal event Action<string>? CommandActivated;

    /// <summary>前段按钮（验收读屏/命中用）。</summary>
    internal AndroidGlassButton First => _segments[0];

    /// <summary>后段按钮。</summary>
    internal AndroidGlassButton Second => _segments[1];

    /// <summary>当前段数（验收读取）。</summary>
    internal int SegmentCount => _segments.Count;

    /// <summary>全部段按钮（验收读取）。</summary>
    internal IReadOnlyList<AndroidGlassButton> SegmentsForChecks => _segments;

    /// <summary>是否竖排（横屏控制栏的命令组）。</summary>
    internal bool Vertical => _vertical;

    /// <summary>横排时前段是否落在容器末端（刘海在右的手机横屏镜像）。</summary>
    internal bool Mirrored => _mirrored;

    /// <summary>胶囊容器实际底色（验收读取）：玻璃锚点返回绘制层表面色。</summary>
    internal Color FillColorForChecks => _glassAnchored
        ? AndroidUiTheme.GlassButtonFill
        : new(CardBackgroundColor?.DefaultColor ?? 0);

    /// <summary>本合体的胶囊材质是否由玻璃控件层绘制（验收读取）。</summary>
    internal bool IsGlassAnchored => _glassAnchored;

    /// <summary>交给玻璃控件层绘制：整块胶囊材质由绘制层承担，段按钮只保留内容与命中。</summary>
    internal void UseGlassAnchor()
    {
        if (_glassAnchored) return;
        _glassAnchored = true;
        _suppressScene = true;
        SetCardBackgroundColor(Color.Transparent!);
        StrokeWidth = 0;
        CardElevation = 0;
        foreach (var button in _segments) button.UseGlassAnchor();
        Invalidate();
    }

    /// <summary>玻璃控件层调用：按真实段矩形绘制各段内容（不含胶囊材质）。</summary>
    /// <param name="canvas">已平移到合体本地原点的画布。</param>
    internal void DrawGlassContent(Canvas canvas)
    {
        foreach (var button in _segments)
        {
            if (button.Visibility != ViewStates.Visible) continue;
            int save = canvas.Save();
            try
            {
                canvas.Translate(button.Left, button.Top);
                button.DrawGlassContent(canvas);
            }
            finally
            {
                canvas.RestoreToCount(save);
            }
        }
    }

    /// <inheritdoc />
    public override void Invalidate()
    {
        base.Invalidate();
        GlassContentInvalidated?.Invoke();
    }

    /// <summary>场景内不绘制段内容；内容由玻璃控件层按锚点重绘。</summary>
    /// <param name="canvas">画布。</param>
    protected override void DispatchDraw(Canvas? canvas)
    {
        if (_suppressScene) return;
        base.DispatchDraw(canvas!);
    }

    /// <summary>设置排布方向：横排各段左右相接，竖排各段上下相接。</summary>
    /// <param name="vertical">是否竖排。</param>
    internal void SetVertical(bool vertical)
    {
        if (_vertical == vertical) return;
        _vertical = vertical;
        RequestLayout();
    }

    /// <summary>设置横排镜像：各段整体反向排布，与镜像后的真实槽位一致。</summary>
    /// <param name="mirrored">是否镜像。</param>
    internal void SetMirrored(bool mirrored)
    {
        if (_mirrored == mirrored) return;
        _mirrored = mirrored;
        RequestLayout();
    }

    /// <summary>
    /// 按顺序设置各段：命令标识、字形与叠字决定是否重建子按钮，读屏名称与可用性每次就地更新。
    /// 段数变化时不残留旧按钮。
    /// </summary>
    /// <param name="segments">从上到下/从左到右的段。</param>
    internal void SetSegments(IReadOnlyList<GlassComboSegment> segments)
    {
        bool rebuild = segments.Count != _segments.Count;
        if (!rebuild)
        {
            for (int index = 0; index < segments.Count; index++)
                if (_segmentKeys[index] != SegmentKey(segments[index])) rebuild = true;
        }
        if (rebuild)
        {
            foreach (var button in _segments)
            {
                RemoveView(button);
                button.Dispose();
            }
            _segments.Clear();
            _segmentKeys.Clear();
            foreach (var segment in segments)
            {
                var button = new AndroidGlassButton(Context!, segment.Glyph, segment.Description,
                    solid: segment.Solid, number: segment.Number);
                string? command = segment.Command;
                button.UseTransparentFill();
                button.Click += (_, _) =>
                {
                    if (button.Enabled && command != null) CommandActivated?.Invoke(command);
                };
                button.GlassContentInvalidated += OnSegmentInvalidated;
                if (_glassAnchored) button.UseGlassAnchor();
                AddView(button);
                _segments.Add(button);
                _segmentKeys.Add(SegmentKey(segment));
            }
        }
        bool changed = rebuild;
        for (int index = 0; index < segments.Count; index++)
        {
            var button = _segments[index];
            if (button.ContentDescription != segments[index].Description)
            {
                button.ContentDescription = segments[index].Description;
                changed = true;
            }
            if (button.Enabled != segments[index].Enabled)
            {
                button.Enabled = segments[index].Enabled;
                changed = true;
            }
        }
        if (!changed) return;
        RequestLayout();
        Invalidate();
    }

    private static string SegmentKey(in GlassComboSegment segment)
        => $"{segment.Command}|{segment.Glyph}|{segment.Solid}|{segment.Number}";

    private void OnSegmentInvalidated() => Invalidate();

    /// <summary>各段等分容器的轴长；镜像只改变视觉顺序。</summary>
    /// <param name="changed">是否变化。</param>
    /// <param name="l">左。</param>
    /// <param name="t">上。</param>
    /// <param name="r">右。</param>
    /// <param name="b">下。</param>
    protected override void OnLayout(bool changed, int l, int t, int r, int b)
    {
        int width = r - l, height = b - t, count = _segments.Count;
        if (count == 0) return;
        if (_vertical)
        {
            for (int index = 0; index < count; index++)
                _segments[index].Layout(0, index * height / count, width, (index + 1) * height / count);
            return;
        }
        for (int position = 0; position < count; position++)
        {
            var button = _mirrored ? _segments[count - 1 - position] : _segments[position];
            button.Layout(position * width / count, 0, (position + 1) * width / count, height);
        }
    }

    /// <summary>各段精确测量为等分容器尺寸，不参与内容撑开；圆角取实际交叉轴的一半。</summary>
    /// <param name="widthMeasureSpec">宽约束。</param>
    /// <param name="heightMeasureSpec">高约束。</param>
    protected override void OnMeasure(int widthMeasureSpec, int heightMeasureSpec)
    {
        int width = MeasureSpec.GetSize(widthMeasureSpec), height = MeasureSpec.GetSize(heightMeasureSpec);
        SetMeasuredDimension(width, height);
        float radius = Math.Min(width, height) / 2f;
        if (Math.Abs(Radius - radius) > .5f) Radius = radius;
        int count = Math.Max(1, _segments.Count);
        int childWidth = _vertical ? width : width / count;
        int childHeight = _vertical ? height / count : height;
        foreach (var button in _segments)
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
internal sealed class AndroidRatingControl : MaterialCardView, IGlassAnchorContent
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
    private bool _glassAnchored;
    private bool _suppressScene;

    /// <summary>玻璃层内容或状态变化（星级、悬停/按下、焦点等）。</summary>
    public event Action? GlassContentInvalidated;

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

    /// <summary>评分槽位当前是否接受输入（验收读取）。</summary>
    internal bool EnabledForChecks => _enabled;

    /// <summary>胶囊实际底色（验收读取）：玻璃锚点返回绘制层表面色。</summary>
    internal Color FillColorForChecks => _glassAnchored
        ? AndroidUiTheme.GlassButtonFill
        : new(CardBackgroundColor?.DefaultColor ?? 0);

    /// <summary>本控件的胶囊材质是否由玻璃控件层绘制（验收读取）。</summary>
    internal bool IsGlassAnchored => _glassAnchored;

    /// <summary>当前是否接受评分输入（与 <see cref="EnabledForChecks"/> 同义，供交互层判断）。</summary>
    internal bool InteractionEnabled => _enabled;

    /// <summary>交给玻璃控件层绘制：材质由绘制层承担，星排、状态层与焦点环仍由本控件绘制。</summary>
    internal void UseGlassAnchor()
    {
        if (_glassAnchored) return;
        _glassAnchored = true;
        _suppressScene = true;
        SetCardBackgroundColor(Color.Transparent!);
        StrokeWidth = 0;
        CardElevation = 0;
        Invalidate();
    }

    /// <summary>玻璃控件层调用：在当前画布本地坐标绘制星排、状态层与焦点环。</summary>
    /// <param name="canvas">已平移到控件本地原点的画布。</param>
    internal void DrawGlassContent(Canvas canvas) => DrawContent(canvas);

    /// <inheritdoc />
    public override void Invalidate()
    {
        base.Invalidate();
        GlassContentInvalidated?.Invoke();
    }

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
        if (OperatingSystem.IsAndroidVersionAtLeast(26)) DefaultFocusHighlightEnabled = false;
        ImportantForAccessibility = ImportantForAccessibility.Yes;
        _accessibility = new RatingAccessibilityHelper(this);
        ViewCompat.SetAccessibilityDelegate(this, _accessibility);
        UpdateAccessibilityName();
    }

    private static Paint StarPaint(Context context, Color color, bool solid = false)
    {
        var paint = AndroidUiTheme.Paint(color);
        paint.TextSize = AndroidUiTheme.Dp(context, DesignTokens.ToolbarRatingGlyphFontSize);
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
    protected override void DispatchDraw(Canvas? canvas)
    {
        if (_suppressScene || canvas is null) return;
        base.DispatchDraw(canvas);
        DrawContent(canvas);
    }

    private void DrawContent(Canvas canvas)
    {
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
        if (IsFocused && !IsInTouchMode)
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
                _conflictPaint.TextSize = AndroidUiTheme.Dp(Context!, DesignTokens.ToolbarRatingGlyphFontSize);
                _conflictPaint.Alpha = alpha;
                AndroidUiTheme.DrawGlyphCentered(canvas, AndroidIcons.Warning, _conflictPaint, x, y);
                return;
            }
            _starClear.TextSize = AndroidUiTheme.Dp(Context!, DesignTokens.ToolbarRatingClearGlyphFontSize);
            _starClear.Alpha = alpha;
            AndroidUiTheme.DrawGlyphCentered(canvas, AndroidIcons.Clear, _starClear, x, y);
            return;
        }
        bool active = value <= (_ratingGesture?.Preview ?? _rating);
        var paint = active ? _starActive : _starInactive;
        paint.TextSize = AndroidUiTheme.Dp(Context!, glyphSize);
        paint.Alpha = alpha;
        AndroidUiTheme.DrawGlyphCentered(canvas, active ? AndroidIcons.StarFilled : AndroidIcons.Star, paint, x, y);
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
/// 横排为带衬底的块，竖排无衬底变体直接放在栏背景上，容器宽度为项目固有宽、文字在容器内尾部截断。
/// 衬底按平台分级：API 31 及以上取共享照片层真模糊再叠 <c>color.toolbar.block.background</c> ×
/// <c>shell.glass.android.exif.tint.opacity@android</c>（圆角由本视图画布裁出）；API 24–30 保留
/// <c>color.toolbar.block.background</c> 的不透明圆角衬底。无衬底变体始终完全透明、不取样。
/// 整块只有一个点按目标并直达 EXIF 详情（TOOLBAR-06）。
/// </summary>
internal sealed class AndroidExifBlock : View
{
    private readonly Paint _labelPaint;
    private readonly Paint _valuePaint;
    private readonly MaterialShapeDrawable _backdrop;
    private readonly AndroidPhotoMaterial? _material;
    private readonly AndroidPhotoMaterial.Consumer? _materialConsumer;
    private readonly int[] _location = new int[2];
    private readonly Path _materialClip = new();
    private readonly RectF _materialBounds = new();
    private IReadOnlyList<ExifDisplayEntry> _items = Array.Empty<ExifDisplayEntry>();
    private int _visibleCount;
    private bool _horizontal = true;
    private bool _bare;

    /// <summary>整块点击（进入 EXIF 详情）。</summary>
    internal event Action? ExifActivated;

    /// <summary>建 EXIF 块。</summary>
    /// <param name="context">宿主上下文。</param>
    /// <param name="material">共享照片层材质；null 表示不取样，只用正式色层（检查路径）。</param>
    public AndroidExifBlock(Context context, AndroidPhotoMaterial? material = null) : base(context)
    {
        _material = material;
        _materialConsumer = material?.CreateConsumer();
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

    /// <summary>叠在照片层模糊上的深灰色层（验收读取；API 24–30 不取样，改用不透明色层）。</summary>
    internal Color MaterialTintForChecks => AndroidUiTheme.ExifGlassTint;

    /// <summary>本视图是否走真实照片层材质（验收读取；无衬底变体与 API 24–30 恒为 false）。</summary>
    internal bool UsesPhotoMaterialForChecks => !_bare && _materialConsumer is not null;

    /// <summary>本视图实际取样的源（验收读取：与根视图照片层同一份 <see cref="IBackdrop"/>；不取样时为 null）。</summary>
    internal IBackdrop? BackdropSourceForChecks => UsesPhotoMaterialForChecks ? _material?.Source : null;

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
        // 真实材质时不能留不透明衬底，否则会挡住模糊；无衬底变体始终透明。
        Background = bare || UsesPhotoMaterialForChecks ? null : _backdrop;
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
        if (UsesPhotoMaterialForChecks) DrawPhotoMaterial(canvas);
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

    /// <summary>
    /// 带衬底变体的真实材质：按本视图圆角裁出照片层取样，再叠深灰色层。
    /// 圆角只在本视图画布上裁；效果层已按本视图独立，不需要把形状传给共享绘制器。
    /// </summary>
    private void DrawPhotoMaterial(Canvas canvas)
    {
        float radius = AndroidUiTheme.Dp(Context!, DesignTokens.ToolbarExifCornerRadius);
        _materialBounds.Set(0, 0, Width, Height);
        _materialClip.Reset();
        _materialClip.AddRoundRect(_materialBounds, radius, radius, Path.Direction.Cw!);
        int save = canvas.Save();
        try
        {
            canvas.ClipPath(_materialClip);
            _materialConsumer?.Draw(canvas, BackdropDrawContext.ForView(this, _location));
            canvas.DrawColor(AndroidUiTheme.ExifGlassTint);
        }
        finally
        {
            canvas.RestoreToCount(save);
        }
    }

    /// <summary>释放本视图的效果 scope 并注销离屏占用（无衬底变体同样适用）。</summary>
    /// <param name="disposing">是否释放托管资源。</param>
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _materialConsumer?.Dispose();
        }

        base.Dispose(disposing);
    }
}
