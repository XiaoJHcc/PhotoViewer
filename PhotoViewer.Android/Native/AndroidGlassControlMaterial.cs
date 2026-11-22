using System;
using System.Collections.Generic;
using System.Threading;
using Android.Animation;
using Android.Content.Res;
using Android.Graphics;
using Android.Views;
using AndroidX.Core.Content;
using PhotoViewer.Android.Backdrop;
using PhotoViewer.Core.Design;

namespace PhotoViewer.Android.Native;

/// <summary>玻璃控件层的锚点内容：内容或状态变化时通知绘制层重绘。</summary>
internal interface IGlassAnchorContent
{
    event Action? GlassContentInvalidated;
}

/// <summary>主屏玻璃控件类型。</summary>
internal enum GlassControlKind
{
    Button,
    Combo,
    Rating,
}

/// <summary>玻璃控件层验收快照（窗口 px）。</summary>
internal readonly record struct GlassControlSnapshot(
    GlassControlKind Kind,
    View Anchor,
    Rect AnchorRect,
    Rect ClipRect,
    BackdropTier Tier,
    bool UsesMaterial,
    bool HasLens,
    bool HasShadow,
    bool HasHighlight,
    Color SurfaceColor,
    BackdropEffect Effect,
    float EffectPaddingPx,
    float VibrancySaturation,
    float BlurRadiusDp,
    float LensHeightDp,
    float LensAmountDp,
    float ScaleX,
    float ScaleY,
    float TranslationX,
    float TranslationY,
    float PressProgress,
    float OffsetX,
    float OffsetY,
    bool Pressed,
    bool Hovered,
    bool Focused,
    bool FocusRingVisible,
    bool Enabled,
    float HoverIntensity,
    bool AnimationsEnabled);

/// <summary>
/// 单个主屏玻璃控件的材质与交互状态：独立 <see cref="BackdropEffectScope"/>、G2 轮廓、
/// 高光/阴影绘制器、按压弹簧与触摸/悬停观察器。由 <see cref="AndroidGlassControlLayer"/> 持有。
/// </summary>
internal sealed class AndroidGlassControlMaterial : IDisposable
{
    private readonly AndroidGlassControlLayer _owner;
    private readonly View _anchor;
    private readonly GlassControlKind _kind;
    private readonly BackdropShape _shape = new BackdropCapsule();
    private readonly BackdropShapeOutline _outline;
    private readonly BackdropEffectScope _scope = new();
    private readonly BackdropInteractiveHighlight _interaction;
    private readonly BackdropShadowRenderer _shadow = new();
    private readonly BackdropHighlightRenderer _highlight = new();
    private readonly BackdropPressTracker _pressTracker = new();
    private readonly View.IOnTouchListener _touchListener;
    private readonly View.IOnHoverListener _hoverListener;
    private readonly EventHandler<View.FocusChangeEventArgs> _focusHandler;
    private readonly List<View> _targets = [];
    private readonly Paint _statePaint = new() { AntiAlias = true };
    private readonly Paint _borderPaint = new() { AntiAlias = true };
    private readonly Paint _focusPaint = new() { AntiAlias = true };
    private readonly ColorStateList? _stateLayer;
    private readonly BackdropShadow _shadowConfig;
    private readonly BackdropHighlight _highlightConfig;
    private readonly float _vibrancy = (float)DesignTokens.ShellGlassAndroidButtonVibrancySaturation;
    private readonly float _blurDp = (float)DesignTokens.ShellGlassAndroidButtonBlurRadius;
    private readonly float _lensHeightDp = (float)DesignTokens.ShellGlassAndroidButtonLensHeight;
    private readonly float _lensAmountDp = (float)DesignTokens.ShellGlassAndroidButtonLensAmount;
    private readonly float _pressGrowDp = (float)DesignTokens.ShellGlassAndroidButtonPressGrow;
    private readonly float _dragStretchDp = (float)DesignTokens.ShellGlassAndroidButtonDragStretch;
    private readonly float _translationFactor = (float)DesignTokens.ShellGlassAndroidButtonDragTranslationFactor;
    private readonly float _hoverIntensity = (float)DesignTokens.ShellGlassAndroidButtonHoverIntensity;
    private BackdropEffect _effect = BackdropEffect.None;
    private (float Density, int Width, int Height) _effectKey = (-1f, -1, -1);
    private Rect _anchorRect = new();
    private Rect _clipRect = new();
    private bool _pressed;
    private bool _hovered;
    private bool _disposed;
    private float _hoverX;
    private float _hoverY;
    private float _touchOffsetX;
    private float _touchOffsetY;
    private float _lastScaleX = 1f;
    private float _lastScaleY = 1f;
    private float _lastTranslationX;
    private float _lastTranslationY;
    private long _footprintBytes;
    private View? _touchTarget;

    internal AndroidGlassControlMaterial(AndroidGlassControlLayer owner, View anchor, GlassControlKind kind)
    {
        _owner = owner;
        _anchor = anchor;
        _kind = kind;
        _outline = new BackdropShapeOutline(_shape);
        _stateLayer = ContextCompat.GetColorStateList(anchor.Context!, Resource.Color.pv_glass_state_layer);
        _statePaint.SetStyle(Paint.Style.Fill);
        _borderPaint.SetStyle(Paint.Style.Stroke);
        _focusPaint.SetStyle(Paint.Style.Stroke);
        _focusPaint.StrokeWidth = Math.Max(1f, AndroidUiTheme.Dp(anchor.Context!, 2));
        _focusPaint.Color = AndroidUiTheme.Accent;
        _shadowConfig = new BackdropShadow(
            radiusDp: (float)DesignTokens.ShellGlassAndroidButtonShadowRadius,
            offsetXDp: 0f,
            offsetYDp: (float)DesignTokens.ShellGlassAndroidButtonShadowOffsetY,
            color: AndroidUiTheme.FromToken(DesignTokens.ShellGlassAndroidButtonShadowColor),
            alpha: 1f);
        _highlightConfig = new BackdropHighlight(
            widthDp: (float)DesignTokens.ShellGlassAndroidButtonHighlightWidth,
            alpha: 1f,
            style: new BackdropDefaultHighlightStyle(
                color: AndroidUiTheme.FromToken(DesignTokens.ShellGlassAndroidButtonHighlightColor),
                angle: (float)DesignTokens.ShellGlassAndroidButtonHighlightAngle,
                falloff: (float)DesignTokens.ShellGlassAndroidButtonHighlightFalloff));
        _interaction = new BackdropInteractiveHighlight(
            position: null,
            surfaceColor: AndroidUiTheme.FromToken(DesignTokens.ShellGlassAndroidButtonPressSurfaceColor),
            radialColor: AndroidUiTheme.FromToken(DesignTokens.ShellGlassAndroidButtonPressRadialColor),
            fallbackSurfaceColor: AndroidUiTheme.FromToken(DesignTokens.ShellGlassAndroidButtonPressSurfaceColor),
            radialRadiusFactor: (float)DesignTokens.ShellGlassAndroidButtonPressRadialRadius,
            dampingRatio: (float)DesignTokens.ShellGlassAndroidButtonSpringDamping,
            stiffness: (float)DesignTokens.ShellGlassAndroidButtonSpringStiffness,
            animationsEnabled: AnimatorsEnabled());

        _touchListener = new TouchListener(this);
        _hoverListener = new HoverListener(this);
        _focusHandler = (_, _) => _owner.Invalidate();
        _pressTracker.PressStarted += OnPressStarted;
        _pressTracker.Moved += OnPressMoved;
        _pressTracker.PressEnded += OnPressEnded;
        _pressTracker.PressCancelled += OnPressCancelled;
    }

    internal View Anchor => _anchor;

    internal GlassControlKind Kind => _kind;

    internal BackdropEffect Effect => _effect;

    internal Rect AnchorRect => _anchorRect;

    internal Rect ClipRect => _clipRect;

    internal float PressProgress => _interaction.PressProgress;

    internal float OffsetX => _interaction.OffsetX;

    internal float OffsetY => _interaction.OffsetY;

    internal bool Pressed => _pressed;

    internal bool Hovered => _hovered;

    internal bool AnimationsEnabled => _interaction.AnimationsEnabled;

    /// <summary>本控件效果层的估算占用（RGBA8）；尚未绘制或低版本为 0。</summary>
    internal long FootprintBytes => Volatile.Read(ref _footprintBytes);

    internal bool Focused
    {
        get
        {
            if (_kind != GlassControlKind.Combo) return _anchor.IsFocused;
            foreach (var segment in ((AndroidGlassCombo)_anchor).SegmentsForChecks)
                if (segment.IsFocused) return true;
            return false;
        }
    }

    internal bool FocusRingVisible => Focused && !_anchor.IsInTouchMode;

    internal bool Enabled
    {
        get
        {
            if (_kind == GlassControlKind.Rating) return ((AndroidRatingControl)_anchor).InteractionEnabled;
            if (_kind == GlassControlKind.Combo)
            {
                foreach (var segment in ((AndroidGlassCombo)_anchor).SegmentsForChecks)
                    if (segment.Enabled) return true;
                return false;
            }
            return _anchor.Enabled;
        }
    }

    internal GlassControlSnapshot Snapshot() => new(
        _kind,
        _anchor,
        new Rect(_anchorRect),
        new Rect(_clipRect),
        BackdropCapabilities.Tier,
        UsesMaterial,
        HasLens,
        HasShadow,
        HasHighlight,
        SurfaceColor,
        _effect,
        _effect.PaddingPx,
        _vibrancy,
        _blurDp,
        _lensHeightDp,
        _lensAmountDp,
        _lastScaleX,
        _lastScaleY,
        _lastTranslationX,
        _lastTranslationY,
        _interaction.PressProgress,
        _interaction.OffsetX,
        _interaction.OffsetY,
        _pressed,
        _hovered,
        Focused,
        FocusRingVisible,
        Enabled,
        _hoverIntensity,
        _interaction.AnimationsEnabled);

    private bool UsesMaterial => BackdropCapabilities.Tier != BackdropTier.ColorLayer;

    private bool HasLens => BackdropCapabilities.Tier == BackdropTier.Full;

    private bool HasShadow => BackdropCapabilities.Tier == BackdropTier.Full;

    private bool HasHighlight => BackdropCapabilities.Tier == BackdropTier.Full;

    private Color SurfaceColor => _kind switch
    {
        GlassControlKind.Button => ((AndroidGlassButton)_anchor).GlassSurfaceColor,
        _ => AndroidUiTheme.GlassButtonFill,
    };

    private Color BorderColor => _kind == GlassControlKind.Button
        ? ((AndroidGlassButton)_anchor).GlassBorderColor
        : Color.Transparent!;

    private int BorderWidth => _kind == GlassControlKind.Button
        ? ((AndroidGlassButton)_anchor).GlassBorderWidth
        : 0;

    /// <summary>同步合体段或单控件的触摸/悬停目标。</summary>
    internal void SyncTargets()
    {
        if (_disposed) return;
        if (_kind != GlassControlKind.Combo)
        {
            if (_targets.Count == 0) Attach(_anchor);
            return;
        }

        var segments = ((AndroidGlassCombo)_anchor).SegmentsForChecks;
        for (int index = _targets.Count - 1; index >= 0; index--)
        {
            var target = _targets[index];
            if (!Contains(segments, target)) Detach(target);
        }
        foreach (var segment in segments) Attach(segment);
    }

    internal bool Advance(float deltaSeconds)
    {
        if (_disposed) return false;
        if (_pressed && !CanPress(_touchTarget))
        {
            _pressed = false;
            _interaction.Cancel();
        }
        return _interaction.Advance(deltaSeconds);
    }

    internal void SetHoverForChecks(bool hovered, float x, float y)
    {
        _hovered = hovered;
        _hoverX = x;
        _hoverY = y;
        _owner.Invalidate();
    }

    internal void Draw(
        Canvas canvas,
        Rect anchorWindow,
        Rect clipWindow,
        int layerOriginX,
        int layerOriginY,
        float density)
    {
        if (_disposed) return;
        SyncTargets();
        _anchorRect = new Rect(anchorWindow);
        _clipRect = new Rect(clipWindow);
        int width = Math.Max(0, _anchor.Width);
        int height = Math.Max(0, _anchor.Height);
        if (width <= 0 || height <= 0) return;

        _outline.Update(width, height, density);
        RefreshEffect(density, width, height);

        bool canPress = CanPress(_touchTarget);
        if (!canPress && _pressed)
        {
            _pressed = false;
            _interaction.Cancel();
        }
        bool animating = _interaction.IsAnimating;
        var transform = BackdropInteractiveTransform.Compute(
            width,
            height,
            canPress ? _interaction.PressProgress : 0f,
            _kind == GlassControlKind.Rating ? 0f : _interaction.OffsetX,
            _kind == GlassControlKind.Rating ? 0f : _interaction.OffsetY,
            density * _pressGrowDp,
            _kind == GlassControlKind.Rating ? 0f : density * _dragStretchDp,
            _translationFactor);
        if (BackdropCapabilities.Tier == BackdropTier.ColorLayer || !_interaction.AnimationsEnabled || !canPress)
        {
            transform = new BackdropInteractiveTransform
            {
                Scale = 1f,
                ScaleX = 1f,
                ScaleY = 1f,
            };
        }
        _lastScaleX = transform.ScaleX;
        _lastScaleY = transform.ScaleY;
        _lastTranslationX = transform.TranslationX;
        _lastTranslationY = transform.TranslationY;

        float widthPx = width;
        float heightPx = height;
        long footprint = 0;
        if (UsesMaterial && !_effect.IsEmpty)
        {
            long paddedWidth = (long)Math.Ceiling(widthPx + (2.0 * _effect.PaddingPx));
            long paddedHeight = (long)Math.Ceiling(heightPx + (2.0 * _effect.PaddingPx));
            if (paddedWidth > 0 && paddedHeight > 0) footprint = paddedWidth * paddedHeight * 4;
        }
        Interlocked.Exchange(ref _footprintBytes, footprint);

        var context = new BackdropDrawContext(
            density,
            _anchor.Resources?.Configuration?.FontScale ?? 1f,
            widthPx,
            heightPx,
            anchorWindow.Left + transform.TranslationX,
            anchorWindow.Top + transform.TranslationY,
            new BackdropLayerTransform(
                transform.ScaleX <= 0f ? 1f : transform.ScaleX,
                transform.ScaleY <= 0f ? 1f : transform.ScaleY,
                0f));

        int save = canvas.Save();
        try
        {
            canvas.ClipRect(
                clipWindow.Left - layerOriginX,
                clipWindow.Top - layerOriginY,
                clipWindow.Right - layerOriginX,
                clipWindow.Bottom - layerOriginY);
            canvas.Translate(anchorWindow.Left - layerOriginX, anchorWindow.Top - layerOriginY);
            if (transform.TranslationX != 0f || transform.TranslationY != 0f
                || transform.ScaleX != 1f || transform.ScaleY != 1f)
            {
                canvas.Translate(transform.TranslationX, transform.TranslationY);
                canvas.Scale(transform.ScaleX, transform.ScaleY, widthPx / 2f, heightPx / 2f);
            }

            if (HasShadow) _shadow.Draw(canvas, _outline, _shadowConfig);
            int shapeSave = canvas.Save();
            try
            {
                canvas.ClipPath(_outline.Path);
                if (UsesMaterial) BackdropDrawer.Draw(canvas, _owner.Source, _effect, context);
                canvas.DrawColor(SurfaceColor);
                if (BorderWidth > 0 && BorderColor.A > 0)
                {
                    _borderPaint.StrokeWidth = BorderWidth;
                    _borderPaint.Color = BorderColor;
                    canvas.DrawPath(_outline.Path, _borderPaint);
                }
                DrawFlatState(canvas);
                if (UsesMaterial) _interaction.Draw(canvas, widthPx, heightPx);
                if (HasHighlight && _hovered && !_pressed)
                {
                    _interaction.DrawRadial(canvas, widthPx, heightPx, _hoverX, _hoverY, _hoverIntensity);
                }
                DrawContent(canvas);
                if (HasHighlight) _highlight.Draw(canvas, _outline, _highlightConfig);
            }
            finally
            {
                canvas.RestoreToCount(shapeSave);
            }

            DrawFocus(canvas, widthPx, heightPx, density);
        }
        finally
        {
            canvas.RestoreToCount(save);
        }

        if (animating) _owner.PostInvalidateOnAnimation();
    }

    private void RefreshEffect(float density, int width, int height)
    {
        if (!UsesMaterial)
        {
            _effect = BackdropEffect.None;
            return;
        }

        var key = (density, width, height);
        if (key == _effectKey) return;

        _effectKey = key;
        _scope.Shape = _shape;
        _scope.Apply(scope =>
        {
            scope.ColorControls(saturation: _vibrancy);
            scope.Blur(scope.DpToPx(_blurDp));
            scope.Lens(scope.DpToPx(_lensHeightDp), scope.DpToPx(_lensAmountDp));
        }, density, width, height);
        _effect = _scope.Build();
    }

    private void DrawContent(Canvas canvas)
    {
        switch (_anchor)
        {
            case AndroidGlassButton button:
                button.DrawGlassContent(canvas);
                break;
            case AndroidGlassCombo combo:
                combo.DrawGlassContent(canvas);
                break;
            case AndroidRatingControl rating:
                rating.DrawGlassContent(canvas);
                break;
        }
    }

    private void DrawFlatState(Canvas canvas)
    {
        if (BackdropCapabilities.Tier == BackdropTier.Full) return;
        bool pressed = _pressed && BackdropCapabilities.Tier == BackdropTier.ColorLayer;
        bool hovered = _hovered && !_pressed;
        if (!pressed && !hovered) return;

        int[] state = pressed ? [global::Android.Resource.Attribute.StatePressed]
            : [global::Android.Resource.Attribute.StateHovered];
        if (_stateLayer is not { } layer) return;
        _statePaint.Color = new(layer.GetColorForState(state, Color.Transparent));
        canvas.DrawPath(_outline.Path, _statePaint);
    }

    private void DrawFocus(Canvas canvas, float width, float height, float density)
    {
        if (_kind == GlassControlKind.Rating || _anchor.IsInTouchMode) return;
        float inset = Math.Max(0f, density);
        if (_kind == GlassControlKind.Combo)
        {
            foreach (var segment in ((AndroidGlassCombo)_anchor).SegmentsForChecks)
            {
                if (!segment.IsFocused) continue;
                DrawFocusRing(canvas, segment.Left, segment.Top, segment.Width, segment.Height, inset);
            }
            return;
        }
        if (_anchor.IsFocused) DrawFocusRing(canvas, 0f, 0f, width, height, inset);
    }

    private void DrawFocusRing(Canvas canvas, float left, float top, float width, float height, float inset)
    {
        if (width <= inset * 2f || height <= inset * 2f) return;
        float radius = MathF.Min(width, height) / 2f;
        canvas.DrawRoundRect(
            left + inset,
            top + inset,
            left + width - inset,
            top + height - inset,
            MathF.Max(0f, radius - inset),
            MathF.Max(0f, radius - inset),
            _focusPaint);
    }

    private void Attach(View target)
    {
        if (IndexOfTarget(target) >= 0) return;
        target.SetOnTouchListener(_touchListener);
        target.SetOnHoverListener(_hoverListener);
        target.FocusChange += _focusHandler;
        _targets.Add(target);
    }

    private void Detach(View target)
    {
        try
        {
            target.SetOnTouchListener(null);
            target.SetOnHoverListener(null);
            target.FocusChange -= _focusHandler;
        }
        catch (ObjectDisposedException)
        {
        }
        int index = IndexOfTarget(target);
        if (index >= 0) _targets.RemoveAt(index);
        if (ReferenceEquals(_touchTarget, target)) _touchTarget = null;
    }

    /// <summary>按引用查找已挂目标：已释放视图不能再参与值相等比较。</summary>
    private int IndexOfTarget(View target)
    {
        for (int index = 0; index < _targets.Count; index++)
            if (ReferenceEquals(_targets[index], target)) return index;
        return -1;
    }

    private static bool Contains(IReadOnlyList<AndroidGlassButton> segments, View target)
    {
        foreach (var segment in segments)
            if (ReferenceEquals(segment, target)) return true;
        return false;
    }

    private bool CanPress(View? target)
    {
        if (target is null) return false;
        if (_kind == GlassControlKind.Rating) return ((AndroidRatingControl)_anchor).InteractionEnabled;
        return target.Enabled;
    }

    private void OnPressStarted(float x, float y)
    {
        if (!CanPress(_touchTarget) || _disposed) return;
        _pressed = true;
        _interaction.Begin(x + _touchOffsetX, y + _touchOffsetY);
        _owner.Invalidate();
    }

    private void OnPressMoved(float x, float y)
    {
        if (!_pressed) return;
        _interaction.Move(x + _touchOffsetX, y + _touchOffsetY);
        _owner.Invalidate();
    }

    private void OnPressEnded()
    {
        if (!_pressed) return;
        _pressed = false;
        _interaction.End();
        _owner.Invalidate();
    }

    private void OnPressCancelled()
    {
        if (!_pressed) return;
        _pressed = false;
        _interaction.Cancel();
        _owner.Invalidate();
    }

    private bool HandleTouch(View? view, MotionEvent? e)
    {
        if (e is null || _disposed) return false;
        _touchTarget = view;
        _touchOffsetX = 0f;
        _touchOffsetY = 0f;
        if (_kind == GlassControlKind.Combo && view is not null)
        {
            _touchOffsetX = view.Left;
            _touchOffsetY = view.Top;
        }
        if (!CanPress(view))
        {
            _pressTracker.Cancel();
            return false;
        }
        _pressTracker.OnTouchEvent(e);
        return false;
    }

    private bool HandleHover(View? view, MotionEvent? e)
    {
        if (e is null || _disposed) return false;
        _touchTarget = view;
        float offsetX = 0f, offsetY = 0f;
        if (_kind == GlassControlKind.Combo && view is not null)
        {
            offsetX = view.Left;
            offsetY = view.Top;
        }
        if (!CanPress(view) || e.ActionMasked == MotionEventActions.HoverExit)
        {
            if (_hovered) _owner.Invalidate();
            _hovered = false;
            return false;
        }
        _hovered = true;
        _hoverX = e.GetX() + offsetX;
        _hoverY = e.GetY() + offsetY;
        _owner.Invalidate();
        return false;
    }

    /// <summary>释放材质、绘制器、监听器与离屏占用。</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var target in _targets.ToArray()) Detach(target);
        _targets.Clear();
        _pressTracker.PressStarted -= OnPressStarted;
        _pressTracker.Moved -= OnPressMoved;
        _pressTracker.PressEnded -= OnPressEnded;
        _pressTracker.PressCancelled -= OnPressCancelled;
        _interaction.Dispose();
        _shadow.Dispose();
        _highlight.Dispose();
        _scope.Dispose();
        _statePaint.Dispose();
        _borderPaint.Dispose();
        _focusPaint.Dispose();
        _outline.Path.Dispose();
        _effect = BackdropEffect.None;
    }

    private static bool AnimatorsEnabled()
        => !OperatingSystem.IsAndroidVersionAtLeast(26) || ValueAnimator.AreAnimatorsEnabled();

    private sealed class TouchListener : Java.Lang.Object, View.IOnTouchListener
    {
        private readonly AndroidGlassControlMaterial _material;

        internal TouchListener(AndroidGlassControlMaterial material) => _material = material;

        public bool OnTouch(View? v, MotionEvent? e) => _material.HandleTouch(v, e);
    }

    private sealed class HoverListener : Java.Lang.Object, View.IOnHoverListener
    {
        private readonly AndroidGlassControlMaterial _material;

        internal HoverListener(AndroidGlassControlMaterial material) => _material = material;

        public bool OnHover(View? v, MotionEvent? e) => _material.HandleHover(v, e);
    }
}
