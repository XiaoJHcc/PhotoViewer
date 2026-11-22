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
/// 弹簧参数与上游一致：按压进度与位置都是阻尼 0.5、刚度 300，可见阈值分别为 0.001 与 0.01。
/// 按下即把位置钉在触点、进度弹向 1；移动时位置直接钉住（不经过弹簧）；抬手/取消时进度与
/// 位置都弹回。着色器在 API 33 以下退化为整面白 25% × 进度，与上游分支相同。
/// </para>
/// <para>
/// 本类只负责数学与绘制，不接管视图触摸；触摸语义由 <see cref="BackdropPressTracker"/> 或
/// 接入层自己的手势处理给出。需要逐帧调用 <see cref="Advance"/> 并在返回 <c>true</c> 时重绘。
/// </para>
/// </remarks>
public sealed class BackdropInteractiveHighlight : IDisposable
{
    private const float PressDampingRatio = 0.5f;
    private const float PressStiffness = 300f;
    private const float PressVisibilityThreshold = 0.001f;
    private const float PositionVisibilityThreshold = 0.01f;

    private static readonly BackdropHighlightPosition DefaultPosition =
        static (_, _, x, y) => (x, y);

    private readonly BackdropSpring _pressProgress =
        new(0f, PressDampingRatio, PressStiffness, PressVisibilityThreshold);
    private readonly BackdropSpring _offsetX =
        new(0f, PressDampingRatio, PressStiffness, PositionVisibilityThreshold);
    private readonly BackdropSpring _offsetY =
        new(0f, PressDampingRatio, PressStiffness, PositionVisibilityThreshold);
    private readonly BackdropHighlightPosition _position;
    private readonly Gfx.Paint _paint = new() { AntiAlias = true };
    private readonly Gfx.PorterDuffXfermode _xfermode;
    private readonly BackdropRuntimeShader? _shader;

    private float _startX;
    private float _startY;
    private bool _disposed;

    /// <summary>建一个跟手高光。</summary>
    /// <param name="position">位置换算；省略时不改坐标。</param>
    public BackdropInteractiveHighlight(BackdropHighlightPosition? position = null)
    {
        _position = position ?? DefaultPosition;
        _xfermode = new Gfx.PorterDuffXfermode(Gfx.PorterDuff.Mode.Add);
        _paint.SetXfermode(_xfermode);

        if (OperatingSystem.IsAndroidVersionAtLeast(33))
        {
            _shader = new BackdropRuntimeShader(BackdropShaders.InteractiveHighlight);
        }
    }

    /// <summary>按压进度 0–1。</summary>
    public float PressProgress => _pressProgress.Value;

    /// <summary>跟手位移 X（px，相对按下点）。</summary>
    public float OffsetX => _offsetX.Value;

    /// <summary>跟手位移 Y（px，相对按下点）。</summary>
    public float OffsetY => _offsetY.Value;

    /// <summary>是否仍在运动（需要下一帧）。</summary>
    public bool IsAnimating =>
        !_pressProgress.IsSettled || !_offsetX.IsSettled || !_offsetY.IsSettled;

    /// <summary>按下：位置钉在触点，进度弹向 1。</summary>
    /// <param name="x">触点 X（px）。</param>
    /// <param name="y">触点 Y（px）。</param>
    public void Begin(float x, float y)
    {
        _startX = x;
        _startY = y;
        _offsetX.SnapTo(0f);
        _offsetY.SnapTo(0f);
        _pressProgress.AnimateTo(1f);
    }

    /// <summary>跟手：位置直接钉在触点（上游 <c>snapTo</c>），不经过弹簧。</summary>
    /// <param name="x">触点 X（px）。</param>
    /// <param name="y">触点 Y（px）。</param>
    public void Move(float x, float y)
    {
        _offsetX.SnapTo(x - _startX);
        _offsetY.SnapTo(y - _startY);
    }

    /// <summary>抬手：进度与位置弹回。</summary>
    public void End() => Release();

    /// <summary>取消：进度与位置弹回。</summary>
    public void Cancel() => Release();

    /// <summary>推进一帧；返回是否仍需下一帧。</summary>
    /// <param name="deltaSeconds">距上一帧的秒数。</param>
    /// <returns>是否仍需下一帧。</returns>
    public bool Advance(float deltaSeconds)
    {
        var press = _pressProgress.Advance(deltaSeconds);
        var x = _offsetX.Advance(deltaSeconds);
        var y = _offsetY.Advance(deltaSeconds);
        return press || x || y;
    }

    /// <summary>把当前高光画到画布上；应在内容之前调用。</summary>
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
            _paint.Color = AlphaWhite(0.08f * progress);
            canvas.DrawRect(0f, 0f, widthPx, heightPx, _paint);

            var position = _position(widthPx, heightPx, _startX + OffsetX, _startY + OffsetY);
            var minDimension = MathF.Min(widthPx, heightPx);
            shader.SetFloatUniform("size", widthPx, heightPx);
            shader.SetColorUniform("color", AlphaWhite(0.15f * progress));
            shader.SetFloatUniform("radius", minDimension * 1.5f);
            shader.SetFloatUniform(
                "position",
                Math.Clamp(position.X, 0f, widthPx),
                Math.Clamp(position.Y, 0f, heightPx));
            _paint.SetShader(shader.PlatformShader);
            canvas.DrawRect(0f, 0f, widthPx, heightPx, _paint);
        }
        else
        {
            _paint.SetShader(null);
            _paint.Color = AlphaWhite(0.25f * progress);
            canvas.DrawRect(0f, 0f, widthPx, heightPx, _paint);
        }
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

    private static Gfx.Color AlphaWhite(float alpha)
    {
        var value = (int)MathF.Round(Math.Clamp(alpha, 0f, 1f) * 255f);
        return Gfx.Color.Argb(value, 255, 255, 255);
    }

    private void Release()
    {
        _pressProgress.AnimateTo(0f);
        _offsetX.AnimateTo(0f);
        _offsetY.AnimateTo(0f);
    }
}
