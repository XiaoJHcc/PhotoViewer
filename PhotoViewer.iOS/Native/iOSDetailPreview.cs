using CoreGraphics;
using Foundation;
using UIKit;

namespace PhotoViewer.iOS.Native;

internal sealed class iOSDetailPreview : UIView
{
    private readonly iOSDiagnosticTile _tile = new() { UserInteractionEnabled = false };
    private readonly UIView _overlay;
    private double _centerU = .5, _centerV = .5;
    private double? _frameWidth, _frameHeight;
    internal Action<double, double>? FocusRequested { get; set; }
    internal Action<iOSDetailPreview, bool, long>? ActivityChanged { get; set; }
    internal Action<long, CGPoint, UITouchPhase>? TouchChanged { get; set; }
    internal Action<bool, double, double, double?, double?>? HighlightChanged { get; set; }
    internal bool IsHighlighted => !_overlay.Hidden;

    public iOSDetailPreview()
    {
        _tile.AspectRatio = 1;
        AddSubview(_tile);
        _overlay = new UIView { UserInteractionEnabled = false, Hidden = true };
        _overlay.Layer.BorderColor = UIColor.FromRGB(0, 255, 0).CGColor;
        _overlay.Layer.BorderWidth = 1;
        _overlay.Layer.CornerRadius = 6;
        AddSubview(_overlay);
        var doubleTap = new UITapGestureRecognizer(() => FocusRequested?.Invoke(_centerU, _centerV))
        {
            NumberOfTapsRequired = 2,
            CancelsTouchesInView = false,
            DelaysTouchesBegan = false,
            DelaysTouchesEnded = false,
        };
        AddGestureRecognizer(doubleTap);
        AddGestureRecognizer(new UIHoverGestureRecognizer(recognizer =>
            ActivityChanged?.Invoke(this, recognizer.State is UIGestureRecognizerState.Began or UIGestureRecognizerState.Changed, -(long)Handle)));
        IsAccessibilityElement = true;
        AccessibilityCustomActions =
        [
            new UIAccessibilityCustomAction("显示绿框", new Func<UIAccessibilityCustomAction, bool>(_ => { ActivityChanged?.Invoke(this, true, -(long)Handle); return true; })),
            new UIAccessibilityCustomAction("清除绿框", new Func<UIAccessibilityCustomAction, bool>(_ => { ActivityChanged?.Invoke(this, false, -(long)Handle); return true; })),
        ];
    }

    internal string? CornerLabel
    {
        get => _tile.CornerLabel;
        set { _tile.CornerLabel = value; AccessibilityLabel = value; }
    }
    internal void SetContent(UIImage? image) => _tile.Source = image;
    internal bool HasContent => _tile.HasContent;
    internal void SetCenter(double u, double v, double? frameWidth, double? frameHeight)
    {
        _centerU = u; _centerV = v; _frameWidth = frameWidth; _frameHeight = frameHeight;
    }
    internal void SetHighlighted(bool visible)
    {
        if (IsHighlighted == visible) return;
        _overlay.Hidden = !visible;
        AccessibilityValue = visible ? "绿框已显示" : "绿框已清除";
        HighlightChanged?.Invoke(visible, _centerU, _centerV, _frameWidth, _frameHeight);
    }
    internal void ClearHighlight() => SetHighlighted(false);
    public override void LayoutSubviews()
    {
        base.LayoutSubviews();
        _tile.Frame = Bounds;
        _overlay.Frame = Bounds;
    }
    public override void TouchesBegan(NSSet touches, UIEvent? evt) => RouteTouches(touches, UITouchPhase.Began);
    public override void TouchesMoved(NSSet touches, UIEvent? evt) => RouteTouches(touches, UITouchPhase.Moved);
    public override void TouchesEnded(NSSet touches, UIEvent? evt) => RouteTouches(touches, UITouchPhase.Ended);
    public override void TouchesCancelled(NSSet touches, UIEvent? evt) => RouteTouches(touches, UITouchPhase.Cancelled);
    private void RouteTouches(NSSet touches, UITouchPhase phase)
    {
        foreach (var touch in touches.Cast<UITouch>())
            if (touch.Type is UITouchType.Direct or UITouchType.Stylus)
                TouchChanged?.Invoke((long)touch.Handle, touch.LocationInView(Superview), phase);
    }
}
