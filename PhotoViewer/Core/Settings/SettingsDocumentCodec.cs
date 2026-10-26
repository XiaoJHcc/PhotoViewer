using System.Collections.Immutable;
using System.Text.Json;
using PhotoViewer.Core.Input;

namespace PhotoViewer.Core.Settings;

/// <summary>设置文档读取结果分类。</summary>
public enum SettingsDocumentReadStatus
{
    /// <summary>schemaVersion 2，已成功读取。</summary>
    Loaded,

    /// <summary>JSON 无法解析。</summary>
    Corrupted,

    /// <summary>版本高于当前支持的 schemaVersion。</summary>
    UnsupportedVersion,

    /// <summary>版本缺失或为旧版 schemaVersion 1。</summary>
    UnsupportedLegacy,
}

public readonly record struct SettingsDocumentReadResult(
    SettingsDocumentReadStatus Status,
    AppSettings? Settings,
    int? Version);

/// <summary>settings.json schemaVersion 2 的编解码与归一化。</summary>
public static class SettingsDocumentCodec
{
    public const int SchemaVersion = 2;

    private const double ScalePresetMinimum = 0.00001;
    private const double ScalePresetMaximum = 16;
    private const double SimilarityThresholdMin = 0.75;
    private const double SimilarityThresholdMax = 0.95;
    private const int SimilarityMaxResultsMin = 1;
    private const int SimilarityMaxResultsMax = 32;
    private const int AutomaticPreloadMaximum = 10;
    public const int MinimumCacheCount = 1;
    public const int MaximumCacheCount = 400;
    public const int MinimumCacheMemoryMB = 512;
    public const int MaximumCacheMemoryMB = 32768;
    private const int MinimumVisibleCenterDelayMs = 100;
    private const int MaximumVisibleCenterDelayMs = 5000;
    private const int MinimumParallelism = 1;
    private const int MaximumParallelism = 32;

    /// <summary>解析完整文档；缺字段取 <paramref name="defaults"/>，未知字段与未知 Id 忽略。</summary>
    public static SettingsDocumentReadResult Read(
        string json,
        AppSettings defaults,
        bool automaticCacheLimit = false)
    {
        ArgumentNullException.ThrowIfNull(json);
        ArgumentNullException.ThrowIfNull(defaults);

        SettingsDocumentDto? document;
        try
        {
            document = JsonSerializer.Deserialize(json, SettingsDocumentJsonContext.Default.SettingsDocumentDto);
        }
        catch (JsonException)
        {
            return new SettingsDocumentReadResult(SettingsDocumentReadStatus.Corrupted, null, null);
        }

        if (document is null)
            return new SettingsDocumentReadResult(SettingsDocumentReadStatus.Corrupted, null, null);

        if (document.SchemaVersion is not { } version)
            return new SettingsDocumentReadResult(SettingsDocumentReadStatus.UnsupportedLegacy, null, null);

        if (version < SchemaVersion)
            return new SettingsDocumentReadResult(SettingsDocumentReadStatus.UnsupportedLegacy, null, version);

        if (version > SchemaVersion)
            return new SettingsDocumentReadResult(SettingsDocumentReadStatus.UnsupportedVersion, null, version);

        var settings = Normalize(Map(document, defaults), automaticCacheLimit);
        return new SettingsDocumentReadResult(SettingsDocumentReadStatus.Loaded, settings, version);
    }

    /// <summary>把快照写成完整的 schemaVersion 2 文档。</summary>
    public static string Serialize(AppSettings settings, bool automaticCacheLimit = false)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var document = MapToDocument(Normalize(settings, automaticCacheLimit));
        return JsonSerializer.Serialize(document, SettingsDocumentJsonContext.Default.SettingsDocumentDto);
    }

    /// <summary>按当前取值范围夹取数值，并把三张表与稳定目录合并（保留用户顺序，补齐缺失项）。</summary>
    public static AppSettings Normalize(AppSettings settings, bool automaticCacheLimit = false)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var cacheCount = Math.Clamp(settings.BitmapCacheMaxCount, MinimumCacheCount, MaximumCacheCount);
        var preloadMaximum = automaticCacheLimit
            ? AutomaticPreloadMaximum
            : Math.Max(0, cacheCount / 3);

        var threshold = double.IsFinite(settings.SimilarityThreshold)
            ? Math.Clamp(settings.SimilarityThreshold, SimilarityThresholdMin, SimilarityThresholdMax)
            : 0.85;

        return settings with
        {
            LayoutMode = Enum.IsDefined(settings.LayoutMode) ? settings.LayoutMode : LayoutMode.Auto,
            ScalePresets = NormalizeScalePresets(settings.ScalePresets),
            FileFormats = NormalizeFileFormats(settings.FileFormats),
            ExifFields = NormalizeExifFields(settings.ExifFields),
            Hotkeys = NormalizeHotkeys(settings.Hotkeys),
            MapCommandTarget = NormalizeMappingTarget(settings.MapCommandTarget, AppleMappingTarget.Ctrl),
            MapOptionTarget = NormalizeMappingTarget(settings.MapOptionTarget, AppleMappingTarget.Alt),
            MapControlTarget = NormalizeMappingTarget(settings.MapControlTarget, AppleMappingTarget.Ctrl),
            SimilarityThreshold = threshold,
            SimilarityMaxResults = Math.Clamp(
                settings.SimilarityMaxResults,
                SimilarityMaxResultsMin,
                SimilarityMaxResultsMax),
            BitmapCacheMaxCount = cacheCount,
            BitmapCacheMaxMemory = Math.Clamp(settings.BitmapCacheMaxMemory, MinimumCacheMemoryMB, MaximumCacheMemoryMB),
            PreloadForwardCount = Math.Clamp(settings.PreloadForwardCount, 0, preloadMaximum),
            PreloadBackwardCount = Math.Clamp(settings.PreloadBackwardCount, 0, preloadMaximum),
            VisibleCenterPreloadCount = Math.Clamp(settings.VisibleCenterPreloadCount, 0, preloadMaximum),
            VisibleCenterDelayMs = Math.Clamp(
                settings.VisibleCenterDelayMs,
                MinimumVisibleCenterDelayMs,
                MaximumVisibleCenterDelayMs),
            NativePreloadParallelism = Math.Clamp(
                settings.NativePreloadParallelism,
                MinimumParallelism,
                MaximumParallelism),
            CpuPreloadParallelism = Math.Clamp(
                settings.CpuPreloadParallelism,
                MinimumParallelism,
                MaximumParallelism),
        };
    }

    private static AppSettings Map(SettingsDocumentDto document, AppSettings defaults)
    {
        return defaults with
        {
            LayoutMode = ParseEnum(document.LayoutMode, defaults.LayoutMode),
            ShowZoomIndicator = document.ShowZoomIndicator ?? defaults.ShowZoomIndicator,
            ScalePresets = document.ScalePresets is null
                ? defaults.ScalePresets
                : document.ScalePresets.ToImmutableArray(),
            ShowRating = document.ShowRating ?? defaults.ShowRating,
            SafeSetRating = document.SafeSetRating ?? defaults.SafeSetRating,
            SameNameAsOnePhoto = document.SameNameAsOnePhoto ?? defaults.SameNameAsOnePhoto,
            FileFormats = document.FileFormats is null
                ? defaults.FileFormats
                : MapFileFormats(document.FileFormats),
            ExifFields = document.ExifFields is null
                ? defaults.ExifFields
                : MapExifFields(document.ExifFields),
            Hotkeys = document.Hotkeys is null
                ? defaults.Hotkeys
                : MapHotkeys(document.Hotkeys),
            UseAppleKeyboard = document.UseAppleKeyboard ?? defaults.UseAppleKeyboard,
            MapCommandTarget = ParseEnum(document.MapCommandTarget, defaults.MapCommandTarget),
            MapOptionTarget = ParseEnum(document.MapOptionTarget, defaults.MapOptionTarget),
            MapControlTarget = ParseEnum(document.MapControlTarget, defaults.MapControlTarget),
            SimilarityThreshold = document.SimilarityThreshold ?? defaults.SimilarityThreshold,
            SimilarityMaxResults = document.SimilarityMaxResults ?? defaults.SimilarityMaxResults,
            AnalysisImmediateCompute = document.AnalysisImmediateCompute ?? defaults.AnalysisImmediateCompute,
            BitmapCacheMaxCount = document.BitmapCacheMaxCount ?? defaults.BitmapCacheMaxCount,
            BitmapCacheMaxMemory = document.BitmapCacheMaxMemory ?? defaults.BitmapCacheMaxMemory,
            PreloadForwardCount = document.PreloadForwardCount ?? defaults.PreloadForwardCount,
            PreloadBackwardCount = document.PreloadBackwardCount ?? defaults.PreloadBackwardCount,
            VisibleCenterPreloadCount = document.VisibleCenterPreloadCount ?? defaults.VisibleCenterPreloadCount,
            VisibleCenterDelayMs = document.VisibleCenterDelayMs ?? defaults.VisibleCenterDelayMs,
            NativePreloadParallelism = document.NativePreloadParallelism ?? defaults.NativePreloadParallelism,
            CpuPreloadParallelism = document.CpuPreloadParallelism ?? defaults.CpuPreloadParallelism,
        };
    }

    private static ImmutableArray<FileFormatSetting> MapFileFormats(IEnumerable<FileFormatDto> items)
    {
        var builder = ImmutableArray.CreateBuilder<FileFormatSetting>();
        foreach (var item in items)
        {
            var id = item.Id?.Trim() ?? string.Empty;
            if (id.Length == 0) continue;

            var enabled = item.IsEnabled
                ?? (SettingsCatalog.TryGetFileFormat(id, out var definition) && definition.IsEnabledByDefault);
            builder.Add(new FileFormatSetting(id, enabled));
        }

        return builder.ToImmutable();
    }

    private static ImmutableArray<ExifFieldSetting> MapExifFields(IEnumerable<ExifFieldDto> items)
    {
        var builder = ImmutableArray.CreateBuilder<ExifFieldSetting>();
        foreach (var item in items)
        {
            var id = item.Id?.Trim() ?? string.Empty;
            if (id.Length == 0) continue;

            var enabled = item.IsEnabled
                ?? (SettingsCatalog.TryGetExifField(id, out var definition) && definition.IsEnabledByDefault);
            builder.Add(new ExifFieldSetting(id, enabled));
        }

        return builder.ToImmutable();
    }

    private static ImmutableArray<HotkeySetting> MapHotkeys(IEnumerable<HotkeyDto> items)
    {
        var builder = ImmutableArray.CreateBuilder<HotkeySetting>();
        foreach (var item in items)
        {
            var commandId = SettingsCatalog.ResolveCommandId(item.Command?.Trim() ?? string.Empty);
            if (commandId.Length == 0) continue;

            var known = SettingsCatalog.TryGetCommand(commandId, out var definition);
            var isDisplay = item.IsVisible ?? (known && definition.IsVisibleByDefault);
            var primary = ReadGesture(item.Primary, known ? definition.PrimaryGesture : null);
            var secondary = ReadGesture(item.Secondary, known ? definition.SecondaryGesture : null);
            builder.Add(new HotkeySetting(commandId, isDisplay, primary, secondary));
        }

        return builder.ToImmutable();
    }

    private static AppGesture? ReadGesture(JsonElement element, AppGesture? fallback)
    {
        if (element.ValueKind == JsonValueKind.Undefined) return fallback;
        if (element.ValueKind == JsonValueKind.Null) return null;
        if (element.ValueKind != JsonValueKind.Object) return fallback;

        GestureDto? gesture;
        try
        {
            gesture = element.Deserialize(SettingsDocumentJsonContext.Default.GestureDto);
        }
        catch (JsonException)
        {
            return fallback;
        }

        return gesture is null ? fallback : ParseGesture(gesture) ?? fallback;
    }

    private static AppGesture? ParseGesture(GestureDto gesture)
    {
        var modifiers = gesture.Modifiers is { } rawModifiers
            ? (GestureModifiers)rawModifiers
            : GestureModifiers.None;

        if (string.Equals(gesture.Kind, "Mouse", StringComparison.OrdinalIgnoreCase)
            || (string.IsNullOrEmpty(gesture.Kind) && !string.IsNullOrEmpty(gesture.MouseAction)))
        {
            if (Enum.TryParse<MouseAction>(gesture.MouseAction, true, out var action)
                && Enum.IsDefined(action))
            {
                return AppGesture.FromMouse(new MouseGestureEx(action, modifiers));
            }

            return null;
        }

        if (string.Equals(gesture.Kind, "Key", StringComparison.OrdinalIgnoreCase)
            || (string.IsNullOrEmpty(gesture.Kind) && !string.IsNullOrEmpty(gesture.Key)))
        {
            if (Enum.TryParse<GestureKey>(gesture.Key, true, out var key)
                && Enum.IsDefined(key))
            {
                return AppGesture.FromKey(new AppKeyGesture(key, modifiers));
            }

            return null;
        }

        return null;
    }

    private static ImmutableArray<double> NormalizeScalePresets(ImmutableArray<double> presets)
    {
        if (presets.IsDefaultOrEmpty) return SettingsCatalog.DefaultScalePresets;

        var values = new List<double>(presets.Length);
        foreach (var value in presets)
        {
            var clamped = double.IsFinite(value)
                ? Math.Clamp(value, ScalePresetMinimum, ScalePresetMaximum)
                : 1;
            values.Add(clamped);
        }

        if (values.Count == 0) return SettingsCatalog.DefaultScalePresets;
        values.Sort();
        return values.ToImmutableArray();
    }

    private static ImmutableArray<FileFormatSetting> NormalizeFileFormats(
        ImmutableArray<FileFormatSetting> items)
    {
        var builder = ImmutableArray.CreateBuilder<FileFormatSetting>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        if (!items.IsDefault)
        {
            foreach (var item in items)
            {
                if (item is null) continue;
                var id = item.Id;
                if (string.IsNullOrWhiteSpace(id)
                    || !SettingsCatalog.TryGetFileFormat(id, out _)
                    || !seen.Add(id))
                {
                    continue;
                }

                builder.Add(item);
            }
        }

        foreach (var definition in SettingsCatalog.FileFormats)
        {
            if (seen.Add(definition.Id))
                builder.Add(new FileFormatSetting(definition.Id, definition.IsEnabledByDefault));
        }

        return builder.ToImmutable();
    }

    private static ImmutableArray<ExifFieldSetting> NormalizeExifFields(
        ImmutableArray<ExifFieldSetting> items)
    {
        var builder = ImmutableArray.CreateBuilder<ExifFieldSetting>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        if (!items.IsDefault)
        {
            foreach (var item in items)
            {
                if (item is null) continue;
                var id = item.Id;
                if (string.IsNullOrWhiteSpace(id)
                    || !SettingsCatalog.TryGetExifField(id, out _)
                    || !seen.Add(id))
                {
                    continue;
                }

                builder.Add(item);
            }
        }

        foreach (var definition in SettingsCatalog.ExifFields)
        {
            if (seen.Add(definition.Id))
                builder.Add(new ExifFieldSetting(definition.Id, definition.IsEnabledByDefault));
        }

        return builder.ToImmutable();
    }

    private static ImmutableArray<HotkeySetting> NormalizeHotkeys(ImmutableArray<HotkeySetting> items)
    {
        var builder = ImmutableArray.CreateBuilder<HotkeySetting>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        if (!items.IsDefault)
        {
            foreach (var item in items)
            {
                if (item is null) continue;
                var id = SettingsCatalog.ResolveCommandId(item.CommandId);
                if (string.IsNullOrWhiteSpace(id)
                    || !SettingsCatalog.TryGetCommand(id, out _)
                    || !seen.Add(id))
                {
                    continue;
                }

                builder.Add(item.CommandId == id ? item : item with { CommandId = id });
            }
        }

        foreach (var definition in SettingsCatalog.Commands)
        {
            if (seen.Add(definition.Id))
            {
                builder.Add(new HotkeySetting(
                    definition.Id,
                    definition.IsVisibleByDefault,
                    definition.PrimaryGesture,
                    definition.SecondaryGesture));
            }
        }

        return builder.ToImmutable();
    }

    private static TEnum ParseEnum<TEnum>(string? text, TEnum fallback)
        where TEnum : struct, Enum
    {
        return Enum.TryParse<TEnum>(text, true, out var value) && Enum.IsDefined(value)
            ? value
            : fallback;
    }

    private static AppleMappingTarget NormalizeMappingTarget(
        AppleMappingTarget target,
        AppleMappingTarget fallback)
        => Enum.IsDefined(target) ? target : fallback;

    private static SettingsDocumentDto MapToDocument(AppSettings settings)
    {
        return new SettingsDocumentDto
        {
            SchemaVersion = SchemaVersion,
            LayoutMode = settings.LayoutMode.ToString(),
            ShowZoomIndicator = settings.ShowZoomIndicator,
            ScalePresets = [.. settings.ScalePresets],
            ShowRating = settings.ShowRating,
            SafeSetRating = settings.SafeSetRating,
            SameNameAsOnePhoto = settings.SameNameAsOnePhoto,
            FileFormats = [.. settings.FileFormats.Select(item => new FileFormatDto
            {
                Id = item.Id,
                IsEnabled = item.IsEnabled,
            })],
            ExifFields = [.. settings.ExifFields.Select(item => new ExifFieldDto
            {
                Id = item.Id,
                IsEnabled = item.IsEnabled,
            })],
            Hotkeys = [.. settings.Hotkeys.Select(item => new HotkeyDto
            {
                Command = item.CommandId,
                IsVisible = item.IsDisplay,
                Primary = ToJsonElement(item.PrimaryGesture),
                Secondary = ToJsonElement(item.SecondaryGesture),
            })],
            UseAppleKeyboard = settings.UseAppleKeyboard,
            MapCommandTarget = settings.MapCommandTarget.ToString(),
            MapOptionTarget = settings.MapOptionTarget.ToString(),
            MapControlTarget = settings.MapControlTarget.ToString(),
            SimilarityThreshold = settings.SimilarityThreshold,
            SimilarityMaxResults = settings.SimilarityMaxResults,
            AnalysisImmediateCompute = settings.AnalysisImmediateCompute,
            BitmapCacheMaxCount = settings.BitmapCacheMaxCount,
            BitmapCacheMaxMemory = settings.BitmapCacheMaxMemory,
            PreloadForwardCount = settings.PreloadForwardCount,
            PreloadBackwardCount = settings.PreloadBackwardCount,
            VisibleCenterPreloadCount = settings.VisibleCenterPreloadCount,
            VisibleCenterDelayMs = settings.VisibleCenterDelayMs,
            NativePreloadParallelism = settings.NativePreloadParallelism,
            CpuPreloadParallelism = settings.CpuPreloadParallelism,
        };
    }

    private static JsonElement ToJsonElement(AppGesture? gesture)
    {
        if (gesture is null) return NullJsonElement;

        var dto = gesture.Key is { } key
            ? new GestureDto
            {
                Kind = "Key",
                Key = key.Key.ToString(),
                Modifiers = (int)key.Modifiers,
            }
            : gesture.Mouse is { } mouse
                ? new GestureDto
                {
                    Kind = "Mouse",
                    Modifiers = (int)mouse.Modifiers,
                    MouseAction = mouse.Action.ToString(),
                }
                : null;

        return dto is null
            ? NullJsonElement
            : JsonSerializer.SerializeToElement(dto, SettingsDocumentJsonContext.Default.GestureDto);
    }

    private static JsonElement CreateNullJsonElement()
    {
        using var document = JsonDocument.Parse("null");
        return document.RootElement.Clone();
    }

    private static readonly JsonElement NullJsonElement = CreateNullJsonElement();
}
