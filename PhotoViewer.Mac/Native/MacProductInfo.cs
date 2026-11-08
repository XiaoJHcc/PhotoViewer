namespace PhotoViewer.Mac.Native;

internal static class MacProductInfo
{
#if PHOTOVIEWER_DINO
    public const string DisplayName = "PhotoViewer AI";
    public const string DiagnosticName = "DINO 诊断";
    public const string DiagnosticSymbol = "sparkles";
#else
    public const string DisplayName = "PhotoViewer";
    public const string DiagnosticName = "CV 诊断";
    public const string DiagnosticSymbol = "waveform.path.ecg";
#endif
}
