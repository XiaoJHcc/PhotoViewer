using Foundation;
using GameController;
using UIKit;

namespace PhotoViewer.iOS.Core;

internal sealed class iOSInputCapabilities : IDisposable
{
    private readonly List<NSObject> _observers = [];
    private (bool Keyboard, bool Mouse)? _injected;
    internal bool HasExternalInput => _injected is { } value ? value.Keyboard || value.Mouse : GCKeyboard.CoalescedKeyboard != null || GCMouse.Mice.Length > 0;
    internal event Action? Changed;
    internal iOSInputCapabilities()
    {
        foreach (var notification in new[] { GCKeyboard.DidConnectNotification, GCKeyboard.DidDisconnectNotification, GCMouse.DidConnectNotification, GCMouse.DidDisconnectNotification })
            _observers.Add(NSNotificationCenter.DefaultCenter.AddObserver(notification, _ => UIApplication.SharedApplication.BeginInvokeOnMainThread(() => Changed?.Invoke())));
    }
    internal void Inject(bool keyboard, bool mouse)
    {
        _injected = (keyboard, mouse);
        Changed?.Invoke();
    }
    public void Dispose()
    {
        foreach (var observer in _observers) NSNotificationCenter.DefaultCenter.RemoveObserver(observer);
        _observers.Clear();
    }
}
