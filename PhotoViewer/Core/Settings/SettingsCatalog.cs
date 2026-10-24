using System.Collections.Immutable;
using PhotoViewer.Core.Input;

namespace PhotoViewer.Core.Settings;

/// <summary>设置项的文件格式定义：稳定 Id、显示名、默认扩展名与默认启用状态。</summary>
public sealed record FileFormatDefinition(
    string Id,
    string DisplayName,
    ImmutableArray<string> Extensions,
    bool IsEnabledByDefault);

/// <summary>EXIF 展示字段定义：稳定 Id（等于属性名）、显示名与默认启用状态。</summary>
public sealed record ExifFieldDefinition(string Id, string DisplayName, bool IsEnabledByDefault);

/// <summary>控制栏命令定义：稳定 Id、展示文案与默认绑定。</summary>
public sealed record CommandDefinition(
    string Id,
    string Name,
    string Icon,
    string Tooltip,
    bool IsVisibleByDefault,
    AppGesture? PrimaryGesture = null,
    AppGesture? SecondaryGesture = null);

/// <summary>三张可排序表与命令绑定的稳定目录；显示名、图标、扩展名只在这里维护。</summary>
public static class SettingsCatalog
{
    public static ImmutableArray<FileFormatDefinition> FileFormats { get; } =
    [
        new("jpg", "JPG", [".jpg", ".jpeg"], true),
        new("heif", "HEIF", [".heif", ".heic", ".avif", ".hif"], true),
        new("png", "PNG", [".png"], true),
        new("tiff", "TIFF", [".tiff", ".tif"], false),
        new("webp", "WebP", [".webp"], true),
        new("raw", "RAW", [".cr2", ".cr3", ".nef", ".arw", ".dng", ".raf", ".orf", ".rw2", ".srw"], true),
        new("bmp", "BMP", [".bmp"], false),
        new("gif", "GIF", [".gif"], false),
    ];

    public static ImmutableArray<ExifFieldDefinition> ExifFields { get; } =
    [
        new("Aperture", "光圈", true),
        new("ExposureTime", "快门", true),
        new("Iso", "ISO", true),
        new("EquivFocalLength", "等效焦距", true),
        new("FocalLength", "实际焦距", false),
        new("CameraModel", "相机型号", false),
        new("LensModel", "镜头型号", false),
        new("DateTimeOriginal", "拍摄时间", false),
        new("ExposureBias", "曝光补偿", false),
        new("WhiteBalance", "白平衡", false),
        new("Flash", "闪光灯", false),
    ];

    public static ImmutableArray<CommandDefinition> Commands { get; } =
    [
        new("Open", "打开文件", "\uf6b5", "打开文件", false,
            Key(GestureKey.N, GestureModifiers.Control),
            Key(GestureKey.O, GestureModifiers.Control)),
        new("Previous", "上一张", "\uf151", "上一张", true,
            Key(GestureKey.Left),
            Mouse(MouseAction.WheelUp)),
        new("Next", "下一张", "\uf152", "下一张", true,
            Key(GestureKey.Right),
            Mouse(MouseAction.WheelDown)),
        new("Exchange", "切换上一张", "\uf5ea", "切换上一张", true,
            Key(GestureKey.Z),
            Mouse(MouseAction.MiddleClick)),
        new("Fit", "缩放适应", "\uf1b2", "缩放适应", true,
            Key(GestureKey.D0, GestureModifiers.Control),
            Key(GestureKey.F)),
        new("ZoomInPreset", "放大（预设）", "\ufaac", "放大", true,
            Key(GestureKey.OemPlus, GestureModifiers.Control)),
        new("ZoomOutPreset", "缩小（预设）", "\uf94e", "缩小", true,
            Key(GestureKey.OemMinus, GestureModifiers.Control)),
        new("ZoomInScale", "放大（比例）", "\ufaac", "放大", false,
            Mouse(MouseAction.WheelUp, GestureModifiers.Control)),
        new("ZoomOutScale", "缩小（比例）", "\uf94e", "缩小", false,
            Mouse(MouseAction.WheelDown, GestureModifiers.Control)),
        new("ToggleAnalysisView", "分析栏展开/收纳", "\ufad2", "分析栏", false),
        new("ToggleEnhance", "雾天增强", "", "雾天增强", true,
            Key(GestureKey.E)),
        new("SetRating0", "评分 0 星", "\uf4f4", "0 星", false,
            Key(GestureKey.OemTilde),
            Key(GestureKey.NumPad0)),
        new("SetRating1", "评分 1 星", "\uf3ea", "1 星", false,
            Key(GestureKey.D1),
            Key(GestureKey.NumPad1)),
        new("SetRating2", "评分 2 星", "\uf3eb", "2 星", false,
            Key(GestureKey.D2),
            Key(GestureKey.NumPad2)),
        new("SetRating3", "评分 3 星", "\uf3ec", "3 星", false,
            Key(GestureKey.D3),
            Key(GestureKey.NumPad3)),
        new("SetRating4", "评分 4 星", "\uf3ed", "4 星", false,
            Key(GestureKey.D4),
            Key(GestureKey.NumPad4)),
        new("SetRating5", "评分 5 星", "\uf3ee", "5 星", false,
            Key(GestureKey.D5),
            Key(GestureKey.NumPad5)),
    ];

    /// <summary>缩放预设默认倍率（12.5% / 25% / 33.333% / 50% / 75% / 100% / 200% / 400%）。</summary>
    public static ImmutableArray<double> DefaultScalePresets { get; } =
        [0.125, 0.25, 0.33333, 0.5, 0.75, 1, 2, 4];

    public static ImmutableArray<FileFormatSetting> DefaultFileFormats { get; } =
        FileFormats.Select(definition => new FileFormatSetting(definition.Id, definition.IsEnabledByDefault)).ToImmutableArray();

    public static ImmutableArray<ExifFieldSetting> DefaultExifFields { get; } =
        ExifFields.Select(definition => new ExifFieldSetting(definition.Id, definition.IsEnabledByDefault)).ToImmutableArray();

    public static ImmutableArray<HotkeySetting> DefaultHotkeys { get; } =
        Commands.Select(definition => new HotkeySetting(
            definition.Id,
            definition.IsVisibleByDefault,
            definition.PrimaryGesture,
            definition.SecondaryGesture)).ToImmutableArray();

    private static readonly IReadOnlyDictionary<string, FileFormatDefinition> FileFormatsById =
        FileFormats.ToDictionary(definition => definition.Id, StringComparer.Ordinal);

    private static readonly IReadOnlyDictionary<string, ExifFieldDefinition> ExifFieldsById =
        ExifFields.ToDictionary(definition => definition.Id, StringComparer.Ordinal);

    private static readonly IReadOnlyDictionary<string, CommandDefinition> CommandsById =
        Commands.ToDictionary(definition => definition.Id, StringComparer.Ordinal);

    public static bool TryGetFileFormat(string id, out FileFormatDefinition definition)
        => FileFormatsById.TryGetValue(id, out definition!);

    public static bool TryGetExifField(string id, out ExifFieldDefinition definition)
        => ExifFieldsById.TryGetValue(id, out definition!);

    public static bool TryGetCommand(string id, out CommandDefinition definition)
        => CommandsById.TryGetValue(id, out definition!);

    public static string ResolveCommandId(string id) => id;

    private static AppGesture Key(GestureKey key, GestureModifiers modifiers = GestureModifiers.None)
        => AppGesture.FromKey(new AppKeyGesture(key, modifiers));

    private static AppGesture Mouse(MouseAction action, GestureModifiers modifiers = GestureModifiers.None)
        => AppGesture.FromMouse(new MouseGestureEx(action, modifiers));
}
