using System;
using Gfx = Android.Graphics;

namespace PhotoViewer.Android.Backdrop;

/// <summary>
/// 跟手高光位置换算：给定视图尺寸与触点，返回着色器采样的位置。
/// 上游示例 <c>InteractiveHighlight</c> 的 <c>position</c> 参数，默认不改坐标。
/// </summary>
/// <param name="widthPx">视图宽（px）。</param>
/// <param name="heightPx">视图高（px）。</param>
/// <param name="xPx">触点或弹簧位置 X（px）。</param>
/// <param name="yPx">触点或弹簧位置 Y（px）。</param>
/// <returns>着色器采样位置。</returns>
public delegate (float X, float Y) BackdropHighlightPosition(
    float widthPx,
    float heightPx,
    float xPx,
    float yPx);

/// <summary>
/// 按压跟手高光，对应上游示例 <c>utils/InteractiveHighlight.kt</c>：按压进度弹簧、
/// 位置弹簧、整面白 8% 与径向白 15% 的 Plus 叠加，以及跟手径向 AGSL 着色器。
/// </summary>
/// <remarks>
/// <para>
/// 弹簧参数默认与上游一致：按压进度与位置都是阻尼 0.5、刚度 300，可见阈值分别为 0.001 与 0.01；
/// 颜色、径向半径系数与动画开关可由调用方按产品令牌参数化，缺省值仍是上游默认。按下即把位置钉在
/// 触点、进度弹向 1；移动时位置直接钉住（不经过弹簧）；抬手/取消时进度与位置都弹回。
/// 着色器在 API 33 以下使用调用方给出的回退整面颜色（缺省上游白 25% × 进度）。
/// </para>
/// <para>
/// 本类只负责数学与绘制，不接管视图触摸；触摸语义由 <see cref="BackdropPressTracker"/> 或
/// 接入层自己的手势处理给出。需要逐帧调用 <see cref="Advance"/> 并在返回 <c>true</c> 时重绘。
/// </para>
/// </remarks>
public sealed class BackdropInteractiveHighlight : IDisposable
{
    private const float DefaultDampingRatio = 0.5f;
    private const float DefaultStiffness = 300f;
    private const float PressVisibilityThreshold = 0.001f;
    private const float PositionVisibilityThreshold = 0.01f;

    private static readonly BackdropHighlightPosition DefaultPosition =
        static (_, _, x, y) => (x, y);

    private readonly BackdropSpring _pressProgress;
    private readonly BackdropSpring _offsetX;
    private readonly BackdropSpring _offsetY;
    private readonly BackdropHighlightPosition _position;
    private readonly Gfx.Color _surfaceColor;
    private readonly Gfx.Color _radialColor;
    private readonly Gfx.Color _fallbackSurfaceColor;
    private readonly float _radialRadiusFactor;
    private readonly Gfx.Paint _paint = new() { AntiAlias = true };
    private readonly Gfx.PorterDuffXfermode _xfermode;
    private readonly BackdropRuntimeShader? _shader;

    private float _startX;
    private float _startY;
    private bool _disposed;

    /// <summary>建一个跟手高光。</summary>
    /// <param name="position">位置换算；省略时不改坐标。</param>
    /// <param name="surfaceColor">按压整面高光颜色（含 alpha），默认白 8%。</param>
    /// <param name="radialColor">跟手径向高光颜色（含 alpha），默认白 15%。</param>
    /// <param name="fallbackSurfaceColor">API 33 以下的整面高光颜色（含 alpha），默认白 25%。</param>
    /// <param name="radialRadiusFactor">径向半径相对短边的倍数，默认 1.5。</param>
    /// <param name="dampingRatio">弹簧阻尼比，默认 0.5。</param>
    /// <param name="stiffness">弹簧刚度，默认 300。</param>
    /// <param name="animationsEnabled">是否播放弹簧；false 时按下/抬手立即到位（减少动态效果）。</param>
    public BackdropInteractiveHighlight(
        BackdropHighlightPosition? position = null,
        Gfx.Color? surfaceColor = null,
        Gfx.Color? radialColor = null,
        Gfx.Color? fallbackSurfaceColor = null,
        float radialRadiusFactor = 1.5f,
        float dampingRatio = DefaultDampingRatio,
        float stiffness = DefaultStiffness,
        bool animationsEnabled = true)
    {
        _position = position ?? DefaultPosition;
        _surfaceColor = surfaceColor ?? Gfx.Color.Argb(20, 255, 255, 255);
        _radialColor = radialColor ?? Gfx.Color.Argb(38, 255, 255, 255);
        _fallbackSurfaceColor = fallbackSurfaceColor ?? Gfx.Color.Argb(64, 255, 255, 255);
        _radialRadiusFactor = radialRadiusFactor;
        AnimationsEnabled = animationsEnabled;
        DampingRatio = dampingRatio;
        Stiffness = stiffness;
        _pressProgress = new BackdropSpring(0f, dampingRatio, stiffness, PressVisibilityThreshold);
        _offsetX = new BackdropSpring(0f, dampingRatio, stiffness, PositionVisibilityThreshold);
        _offsetY = new BackdropSpring(0f, dampingRatio, stiffness, PositionVisibilityThreshold);
        _xfermode = new Gfx.PorterDuffXfermode(Gfx.PorterDuff.Mode.Add);
        _paint.SetXfermode(_xfermode);

        if (OperatingSystem.IsAndroidVersionAtLeast(33))
        {
            _shader = new BackdropRuntimeShader(BackdropShaders.InteractiveHighlight);
        }
    }

    /// <summary>弹簧阻尼比。</summary>
    public float DampingRatio { get; }

    /// <summary>弹簧刚度。</summary>
    public float Stiffness { get; }

    /// <summary>是否播放弹簧动画。</summary>
    public bool AnimationsEnabled { get; }

    /// <summary>按压整面高光颜色（含 alpha）。</summary>
    public Gfx.Color SurfaceColor => _surfaceColor;

    /// <summary>跟手径向高光颜色（含 alpha）。</summary>
    public Gfx.Color RadialColor => _radialColor;

    /// <summary>API 33 以下的整面高光颜色（含 alpha）。</summary>
    public Gfx.Color FallbackSurfaceColor => _fallbackSurfaceColor;

    /// <summary>按压进度 0–1。</summary>
    public float PressProgress => _pressProgress.Value;

    /// <summary>跟手位移 X（px，相对按下点）。</summary>
    public float OffsetX => _offsetX.Value;

    /// <summary>跟手位移 Y（px，相对按下点）。</summary>
    public float OffsetY => _offsetY.Value;

    /// <summary>是否仍在运动（需要下一帧）。</summary>
    public bool IsAnimating =>
        !_pressProgress.IsSettled || !_offsetX.IsSettled || !_offsetY.IsSettled;

    /// <summary>按下：位置钉在触点，进度弹向 1（减少动态效果时立即到位）。</summary>
    /// <param name="x">触点 X（px）。</param>
    /// <param name="y">触点 Y（px）。</param>
    /// <param name="initialProgress">最低起始亮度；默认从当前进度起步。</param>
    public void Begin(float x, float y, float initialProgress = 0f)
    {
        _startX = x;
        _startY = y;
        _offsetX.SnapTo(0f);
        _offsetY.SnapTo(0f);
        if (AnimationsEnabled)
        {
            if (initialProgress > PressProgress) _pressProgress.SnapTo(Math.Min(1f, initialProgress));
            _pressProgress.AnimateTo(1f);
        }
        else _pressProgress.SnapTo(1f);
    }

    /// <summary>跟手：位置直接钉在触点（上游 <c>snapTo</c>），不经过弹簧。</summary>
    /// <param name="x">触点 X（px）。</param>
    /// <param name="y">触点 Y（px）。</param>
    public void Move(float x, float y)
    {
        _offsetX.SnapTo(x - _startX);
        _offsetY.SnapTo(y - _startY);
    }

    /// <summary>抬手：进度与位置弹回（减少动态效果时立即收起）。</summary>
    public void End() => Release();

    /// <summary>取消：进度与位置弹回（减少动态效果时立即收起）。</summary>
    public void Cancel() => Release();

    /// <summary>推进一帧；返回是否仍需下一帧。</summary>
    /// <param name="deltaSeconds">距上一帧的秒数。</param>
    /// <returns>是否仍需下一帧。</returns>
    public bool Advance(float deltaSeconds)
    {
        if (!AnimationsEnabled) return false;

        var press = _pressProgress.Advance(deltaSeconds);
        var x = _offsetX.Advance(deltaSeconds);
        var y = _offsetY.Advance(deltaSeconds);
        return press || x || y;
    }

    /// <summary>把当前按压高光画到画布上；应在内容之前调用。</summary>
    /// <param name="canvas">目标画布。</param>
    /// <param name="widthPx">视图宽（px）。</param>
    /// <param name="heightPx">视图高（px）。</param>
    public void Draw(Gfx.Canvas canvas, float widthPx, float heightPx)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(canvas);

        var progress = _pressProgress.Value;
        if (progress <= 0f || widthPx <= 0f || heightPx <= 0f) return;

        if (OperatingSystem.IsAndroidVersionAtLeast(33) && _shader is { } shader)
        {
            _paint.SetShader(null);
            _paint.Color = ScaleAlpha(_surfaceColor, progress);
            canvas.DrawRect(0f, 0f, widthPx, heightPx, _paint);

            var position = _position(widthPx, heightPx, _startX + OffsetX, _startY + OffsetY);
            DrawRadialShader(
                canvas,
                shader,
                widthPx,
                heightPx,
                position.X,
                position.Y,
                ScaleAlpha(_radialColor, progress));
        }
        else
        {
            _paint.SetShader(null);
            _paint.Color = ScaleAlpha(_fallbackSurfaceColor, progress);
            canvas.DrawRect(0f, 0f, widthPx, heightPx, _paint);
        }
    }

    /// <summary>
    /// 只画跟手径向高光（无整面底色），用于悬停等接入层差异；API 33 以下无着色器时不画。
    /// </summary>
    /// <param name="canvas">目标画布。</param>
    /// <param name="widthPx">视图宽（px）。</param>
    /// <param name="heightPx">视图高（px）。</param>
    /// <param name="xPx">高光中心 X（px）。</param>
    /// <param name="yPx">高光中心 Y（px）。</param>
    /// <param name="intensity">相对 <see cref="RadialColor"/> 的强度 0–1。</param>
    public void DrawRadial(
        Gfx.Canvas canvas,
        float widthPx,
        float heightPx,
        float xPx,
        float yPx,
        float intensity)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(canvas);

        if (intensity <= 0f || widthPx <= 0f || heightPx <= 0f) return;
        if (!OperatingSystem.IsAndroidVersionAtLeast(33) || _shader is not { } shader) return;

        DrawRadialShader(canvas, shader, widthPx, heightPx, xPx, yPx, ScaleAlpha(_radialColor, intensity));
    }

    /// <summary>释放画笔与着色器。</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _paint.Dispose();

        if (OperatingSystem.IsAndroidVersionAtLeast(33))
        {
            _shader?.Dispose();
        }

        _xfermode.Dispose();
    }

    [System.Runtime.Versioning.SupportedOSPlatform("android33.0")]
    private void DrawRadialShader(
        Gfx.Canvas canvas,
        BackdropRuntimeShader shader,
        float widthPx,
        float heightPx,
        float xPx,
        float yPx,
        Gfx.Color color)
    {
        var minDimension = MathF.Min(widthPx, heightPx);
        shader.SetFloatUniform("size", widthPx, heightPx);
        shader.SetColorUniform("color", color);
        shader.SetFloatUniform("radius", minDimension * _radialRadiusFactor);
        shader.SetFloatUniform(
            "position",
            Math.Clamp(xPx, 0f, widthPx),
            Math.Clamp(yPx, 0f, heightPx));
        _paint.Alpha = 255;
        _paint.SetShader(shader.PlatformShader);
        canvas.DrawRect(0f, 0f, widthPx, heightPx, _paint);
    }

    private static Gfx.Color ScaleAlpha(Gfx.Color color, float factor)
    {
        int alpha = (int)MathF.Round(Math.Clamp(color.A / 255f * factor, 0f, 1f) * 255f);
        return Gfx.Color.Argb(alpha, color.R, color.G, color.B);
    }

    private void Release()
    {
        if (AnimationsEnabled)
        {
            _pressProgress.AnimateTo(0f);
            _offsetX.AnimateTo(0f);
            _offsetY.AnimateTo(0f);
            return;
        }

        _pressProgress.SnapTo(0f);
        _offsetX.SnapTo(0f);
        _offsetY.SnapTo(0f);
    }
}
