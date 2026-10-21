using CoreGraphics;
using Foundation;
using UIKit;

namespace PhotoViewer.iOS.Core;

internal sealed class iOSHotkeyCaptureView : UIView
{
    private readonly iOSHotkeyRecorder _owner;
    private double _scrollTranslation;
    internal bool PointerActive { get; private set; }
    internal bool ScrollActive { get; private set; }

    internal iOSHotkeyCaptureView(iOSHotkeyRecorder owner, CGRect frame) : base(frame)
    {
        _owner = owner;
        BackgroundColor = UIColor.Clear;
        Opaque = false;
        AutoresizingMask = UIViewAutoresizing.FlexibleWidth | UIViewAutoresizing.FlexibleHeight;
        AccessibilityViewIsModal = true;
        IsAccessibilityElement = true;
        AccessibilityLabel = "快捷键录制";
        AddGestureRecognizer(new UIPanGestureRecognizer(OnScroll)
        {
            MinimumNumberOfTouches = 0,
            MaximumNumberOfTouches = 0,
            AllowedScrollTypesMask = UIScrollTypeMask.All,
        });
    }

    public override UIView? HitTest(CGPoint point, UIEvent? evt)
        => !Hidden && UserInteractionEnabled && Bounds.Contains(point) ? this : null;

    internal void PointerDown(nint button, ulong modifiers)
    {
        PointerActive = true;
        _owner.UpdateModifiers(modifiers);
        _owner.CaptureMouse(button);
    }

    internal void PointerUp()
    {
        PointerActive = false;
        _owner.ReleaseCapturedPointer();
    }

    public override void TouchesBegan(NSSet touches, UIEvent? evt)
    {
        // 直接触控（手指）落在捕获层上即退出录制：录制态不能把触控整个吞掉，否则用户无法离开。
        if (touches.Cast<UITouch>().Any(touch => touch.Type == UITouchType.Direct))
        {
            CancelFromDirectTouch();
            return;
        }
        if (!touches.Cast<UITouch>().Any(touch => touch.Type == UITouchType.IndirectPointer)) return;
        ulong mask = (ulong)(evt?.ButtonMask ?? default);
        nint button = (mask & 2) != 0 ? 1 : (mask & 4) != 0 ? 2 : 0;
        PointerDown(button, _owner.CurrentModifiers);
    }

    /// <summary>直接触控取消录制（保留原绑定）；这次触摸被捕获层吞掉，不落到下层控件。</summary>
    internal void CancelFromDirectTouch() => _owner.CancelRecording();

    public override void TouchesMoved(NSSet touches, UIEvent? evt) { }
    public override void TouchesEnded(NSSet touches, UIEvent? evt) => PointerUp();
    public override void TouchesCancelled(NSSet touches, UIEvent? evt) => PointerUp();

    private void OnScroll(UIPanGestureRecognizer recognizer)
    {
        if (recognizer.State is UIGestureRecognizerState.Began or UIGestureRecognizerState.Changed)
        {
            ScrollActive = true;
            double translation = recognizer.TranslationInView(this).Y;
            double delta = translation - _scrollTranslation;
            _scrollTranslation = translation;
            _owner.UpdateModifiers((ulong)recognizer.ModifierFlags);
            if (delta != 0) _owner.CaptureMouse(0, delta);
        }
        else if (recognizer.State is UIGestureRecognizerState.Ended or UIGestureRecognizerState.Cancelled or UIGestureRecognizerState.Failed)
        {
            ScrollActive = false;
            _scrollTranslation = 0;
            _owner.ReleaseCapturedPointer();
        }
    }
}
