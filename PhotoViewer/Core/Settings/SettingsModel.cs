using System.Collections.Generic;
using Avalonia.Input;
using PhotoViewer.ViewModels;

namespace PhotoViewer.Core.Settings;

public sealed class SettingsModel
{
    public int Version { get; set; } = 1;
    public LayoutMode LayoutMode { get; set; } = LayoutMode.Auto;
    public bool ShowZoomIndicator { get; set; } = true;
    public List<double> ScalePresets { get; set; } = new();

    public bool ShowRating { get; set; } = true;
    public bool SafeSetRating { get; set; } = true;
    public bool SameNameAsOnePhoto { get; set; } = true;

    public List<FileFormatModel> FileFormats { get; set; } = new();
    public List<ExifDisplayModel> ExifDisplayItems { get; set; } = new();

    public List<HotkeyModel> Hotkeys { get; set; } = new();
    public bool UseAppleKeyboard { get; set; }
    public AppleMappingTarget MapCommandTarget { get; set; } = AppleMappingTarget.Ctrl;
    public AppleMappingTarget MapOptionTarget { get; set; } = AppleMappingTarget.Alt;
    public AppleMappingTarget MapControlTarget { get; set; } = AppleMappingTarget.Ctrl;

    public int BitmapCacheMaxCount { get; set; }
    public int BitmapCacheMaxMemory { get; set; }
    public int PreloadForwardCount { get; set; }
    public int PreloadBackwardCount { get; set; }
    public int VisibleCenterPreloadCount { get; set; }
    public int VisibleCenterDelayMs { get; set; }
    public int NativePreloadParallelism { get; set; }
    public int CpuPreloadParallelism { get; set; }
    public int PreloadParallelism { get; set; }
}

public sealed class FileFormatModel
{
    public string DisplayName { get; set; } = string.Empty;
    public List<string> Extensions { get; set; } = new();
    public bool IsEnabled { get; set; }
}

public sealed class ExifDisplayModel
{
    public string DisplayName { get; set; } = string.Empty;
    public string PropertyName { get; set; } = string.Empty;
    public bool IsEnabled { get; set; }
}

public sealed class HotkeyModel
{
    public string Name { get; set; } = string.Empty;
    public string Command { get; set; } = string.Empty;
    public string DisplaySymbol { get; set; } = string.Empty;
    public string Tooltip { get; set; } = string.Empty;
    public bool IsDisplay { get; set; }
    public GestureModel? Primary { get; set; }
    public GestureModel? Secondary { get; set; }
}

public enum GestureKind
{
    Key,
    Mouse
}

public sealed class GestureModel
{
    public GestureKind Kind { get; set; }
    public string? Key { get; set; }
    public KeyModifiers Modifiers { get; set; }
    public MouseAction? MouseAction { get; set; }
}

