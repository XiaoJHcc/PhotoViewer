using System.Collections.Immutable;
using PhotoViewer.Core.Input;

namespace PhotoViewer.Core.Settings;

/// <summary>不可变的运行时设置快照。所有集合均为只读，修改通过 <c>with</c> 生成新快照。</summary>
public sealed record AppSettings
{
    public LayoutMode LayoutMode { get; init; } = LayoutMode.Auto;

    public bool ShowZoomIndicator { get; init; } = true;

    /// <summary>缩放预设倍率（0.125 表示 12.5%）。</summary>
    public ImmutableArray<double> ScalePresets { get; init; } = SettingsCatalog.DefaultScalePresets;

    public bool ShowRating { get; init; } = true;

    public bool SafeSetRating { get; init; } = true;

    public bool SameNameAsOnePhoto { get; init; } = true;

    public ImmutableArray<FileFormatSetting> FileFormats { get; init; } = SettingsCatalog.DefaultFileFormats;

    public ImmutableArray<ExifFieldSetting> ExifFields { get; init; } = SettingsCatalog.DefaultExifFields;

    public ImmutableArray<HotkeySetting> Hotkeys { get; init; } = SettingsCatalog.DefaultHotkeys;

    public bool UseAppleKeyboard { get; init; }

    public AppleMappingTarget MapCommandTarget { get; init; } = AppleMappingTarget.Ctrl;

    public AppleMappingTarget MapOptionTarget { get; init; } = AppleMappingTarget.Alt;

    public AppleMappingTarget MapControlTarget { get; init; } = AppleMappingTarget.Ctrl;

    public double SimilarityThreshold { get; init; } = 0.85;

    public int SimilarityMaxResults { get; init; } = 8;

    public bool AnalysisImmediateCompute { get; init; }

    public int BitmapCacheMaxCount { get; init; } = 30;

    public int BitmapCacheMaxMemory { get; init; } = 2048;

    public int PreloadForwardCount { get; init; } = 10;

    public int PreloadBackwardCount { get; init; } = 5;

    public int VisibleCenterPreloadCount { get; init; } = 5;

    public int VisibleCenterDelayMs { get; init; } = 1000;

    public int NativePreloadParallelism { get; init; } = 8;

    public int CpuPreloadParallelism { get; init; } = 8;

    /// <summary>按字段内容比较两份快照（ImmutableArray 的默认相等性只比较底层数组引用）。</summary>
    public bool ContentEquals(AppSettings? other)
    {
        if (other is null) return false;
        if (ReferenceEquals(this, other)) return true;

        return LayoutMode == other.LayoutMode
            && ShowZoomIndicator == other.ShowZoomIndicator
            && SequenceEquals(ScalePresets, other.ScalePresets)
            && ShowRating == other.ShowRating
            && SafeSetRating == other.SafeSetRating
            && SameNameAsOnePhoto == other.SameNameAsOnePhoto
            && SequenceEquals(FileFormats, other.FileFormats)
            && SequenceEquals(ExifFields, other.ExifFields)
            && HotkeysEqual(Hotkeys, other.Hotkeys)
            && UseAppleKeyboard == other.UseAppleKeyboard
            && MapCommandTarget == other.MapCommandTarget
            && MapOptionTarget == other.MapOptionTarget
            && MapControlTarget == other.MapControlTarget
            && SimilarityThreshold.Equals(other.SimilarityThreshold)
            && SimilarityMaxResults == other.SimilarityMaxResults
            && AnalysisImmediateCompute == other.AnalysisImmediateCompute
            && BitmapCacheMaxCount == other.BitmapCacheMaxCount
            && BitmapCacheMaxMemory == other.BitmapCacheMaxMemory
            && PreloadForwardCount == other.PreloadForwardCount
            && PreloadBackwardCount == other.PreloadBackwardCount
            && VisibleCenterPreloadCount == other.VisibleCenterPreloadCount
            && VisibleCenterDelayMs == other.VisibleCenterDelayMs
            && NativePreloadParallelism == other.NativePreloadParallelism
            && CpuPreloadParallelism == other.CpuPreloadParallelism;
    }

    private static bool SequenceEquals<T>(ImmutableArray<T> left, ImmutableArray<T> right)
    {
        if (left.IsDefault || right.IsDefault) return left.IsDefault && right.IsDefault;
        return left.SequenceEqual(right);
    }

    private static bool HotkeysEqual(ImmutableArray<HotkeySetting> left, ImmutableArray<HotkeySetting> right)
    {
        if (left.IsDefault || right.IsDefault) return left.IsDefault && right.IsDefault;
        if (left.Length != right.Length) return false;

        for (int index = 0; index < left.Length; index++)
        {
            var a = left[index];
            var b = right[index];
            if (a.CommandId != b.CommandId || a.IsDisplay != b.IsDisplay) return false;
            if (!GestureEquals(a.PrimaryGesture, b.PrimaryGesture)) return false;
            if (!GestureEquals(a.SecondaryGesture, b.SecondaryGesture)) return false;
        }

        return true;
    }

    private static bool GestureEquals(AppGesture? left, AppGesture? right)
    {
        if (ReferenceEquals(left, right)) return true;
        if (left is null || right is null) return false;

        if (left.Key is { } leftKey && right.Key is { } rightKey)
            return leftKey.Key == rightKey.Key && leftKey.Modifiers == rightKey.Modifiers;

        if (left.Mouse is { } leftMouse && right.Mouse is { } rightMouse)
            return leftMouse.Action == rightMouse.Action && leftMouse.Modifiers == rightMouse.Modifiers;

        return false;
    }
}

/// <summary>文件格式的启停状态；显示名与扩展名由 <see cref="SettingsCatalog"/> 按 Id 派生。</summary>
public sealed record FileFormatSetting(string Id, bool IsEnabled);

/// <summary>EXIF 字段的展示开关；显示名由 <see cref="SettingsCatalog"/> 按 Id 派生。</summary>
public sealed record ExifFieldSetting(string Id, bool IsEnabled);

/// <summary>命令的主/次手势与显隐；名称、图标、提示由 <see cref="SettingsCatalog"/> 按命令 Id 派生。</summary>
public sealed record HotkeySetting(
    string CommandId,
    bool IsDisplay,
    AppGesture? PrimaryGesture = null,
    AppGesture? SecondaryGesture = null);
