namespace PhotoViewer.Android.Native;

internal static class AndroidAppCapabilities
{
#if PHOTOVIEWER_DINO
    internal const bool HasDino = true;
    internal const string DiagnosticName = "DINO 诊断";
    internal const string EditionName = "ai";
#else
    internal const bool HasDino = false;
    internal const string DiagnosticName = "CV 诊断";
    internal const string EditionName = "standard";
#endif
}
