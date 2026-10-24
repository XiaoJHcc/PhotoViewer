using System.Text.Json;

namespace PhotoViewer.Core.Settings;

/// <summary>schemaVersion 2 的设置文档形状。可空字段只表示 JSON 中缺失。</summary>
internal sealed class SettingsDocumentDto
{
    public int? SchemaVersion { get; set; }

    public string? LayoutMode { get; set; }

    public bool? ShowZoomIndicator { get; set; }

    public List<double>? ScalePresets { get; set; }

    public bool? ShowRating { get; set; }

    public bool? SafeSetRating { get; set; }

    public bool? SameNameAsOnePhoto { get; set; }

    public List<FileFormatDto>? FileFormats { get; set; }

    public List<ExifFieldDto>? ExifFields { get; set; }

    public List<HotkeyDto>? Hotkeys { get; set; }

    public bool? UseAppleKeyboard { get; set; }

    public string? MapCommandTarget { get; set; }

    public string? MapOptionTarget { get; set; }

    public string? MapControlTarget { get; set; }

    public double? SimilarityThreshold { get; set; }

    public int? SimilarityMaxResults { get; set; }

    public bool? AnalysisImmediateCompute { get; set; }

    public int? BitmapCacheMaxCount { get; set; }

    public int? BitmapCacheMaxMemory { get; set; }

    public int? PreloadForwardCount { get; set; }

    public int? PreloadBackwardCount { get; set; }

    public int? VisibleCenterPreloadCount { get; set; }

    public int? VisibleCenterDelayMs { get; set; }

    public int? NativePreloadParallelism { get; set; }

    public int? CpuPreloadParallelism { get; set; }
}

internal sealed class FileFormatDto
{
    public string? Id { get; set; }

    public bool? IsEnabled { get; set; }
}

internal sealed class ExifFieldDto
{
    public string? Id { get; set; }

    public bool? IsEnabled { get; set; }
}

internal sealed class HotkeyDto
{
    public string? Command { get; set; }

    public bool? IsVisible { get; set; }

    /// <summary>属性缺失时为 <see cref="JsonValueKind.Undefined"/>，显式 <c>null</c> 时为 <see cref="JsonValueKind.Null"/>。</summary>
    public JsonElement Primary { get; set; }

    /// <summary>属性缺失时为 <see cref="JsonValueKind.Undefined"/>，显式 <c>null</c> 时为 <see cref="JsonValueKind.Null"/>。</summary>
    public JsonElement Secondary { get; set; }
}

internal sealed class GestureDto
{
    public string? Kind { get; set; }

    public string? Key { get; set; }

    /// <summary><c>GestureModifiers</c> 的冻结数值契约。</summary>
    public int? Modifiers { get; set; }

    public string? MouseAction { get; set; }
}
