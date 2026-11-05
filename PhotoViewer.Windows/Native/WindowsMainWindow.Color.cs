using System;

namespace PhotoViewer.Windows.Native;

internal sealed partial class WindowsMainWindow
{
    private WindowsDisplayColor? _displayColor;

    private void InitializeDisplayColor()
    {
        if (_displayColor != null || _closed) return;
        WindowsDisplayColor.Changed += OnDisplayColorChanged;
        _displayColor = new WindowsDisplayColor(WinRT.Interop.WindowNative.GetWindowHandle(this), DispatcherQueue);
        _photo.UpdateDisplayColor();
    }

    private void OnDisplayColorChanged()
    {
        if (_closed) return;
        _photo.UpdateDisplayColor();
        WindowsImageAdapter.UpdateDisplayColor();
    }

    private void DisposeDisplayColor()
    {
        WindowsDisplayColor.Changed -= OnDisplayColorChanged;
        _displayColor?.Dispose();
        _displayColor = null;
    }
}
