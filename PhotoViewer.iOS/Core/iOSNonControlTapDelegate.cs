using UIKit;

namespace PhotoViewer.iOS.Core;

internal sealed class iOSNonControlTapDelegate : UIGestureRecognizerDelegate
{
    public override bool ShouldReceiveTouch(UIGestureRecognizer recognizer, UITouch touch)
    {
        for (var view = touch.View; view != null; view = view.Superview)
            if (view is UIControl or UITextView) return false;
        return true;
    }
}
