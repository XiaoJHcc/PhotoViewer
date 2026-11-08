using System;
using System.Collections.Immutable;
using System.Linq;
using PhotoViewer.Core.Input;
using PhotoViewer.Core.Settings;

namespace PhotoViewer.ViewModels.Settings;

/// <summary>原生展示结构受影响的区域；只用于结构刷新，文本绑定仍走属性通知。</summary>
[Flags]
public enum SettingsChangeImpact
{
    None = 0,
    Layout = 1,
    Navigator = 2,
    Rating = 4,
    Exif = 8,
    Commands = 16,
    InputBindings = 32,
    FileProjection = 64,
    Analysis = 128,
    Similarity = 256,

    /// <summary>全部区域；供 null/空属性名的“全部属性已变”语义使用。</summary>
    All = Layout | Navigator | Rating | Exif | Commands | InputBindings | FileProjection | Analysis | Similarity,
}

public partial class SettingsViewModel
{
    private readonly object _presentationGate = new();
    private int _presentationBatchDepth;
    private SettingsChangeImpact _pendingPresentationImpact;

    /// <summary>原生展示刷新通道；订阅方按位刷新结构。Dispose 后不再触发。</summary>
    public event Action<SettingsChangeImpact>? PresentationChanged;

    /// <summary>
    /// 属性名到展示影响的正向白名单：只映射会影响展示结构的属性，诊断/文本/未知属性返回
    /// <see cref="SettingsChangeImpact.None"/>；null 或空属性名（“全部属性已变”）返回
    /// <see cref="SettingsChangeImpact.All"/>。
    /// </summary>
    public static SettingsChangeImpact GetPresentationImpact(string? propertyName)
    {
        if (string.IsNullOrEmpty(propertyName)) return SettingsChangeImpact.All;

        return propertyName switch
        {
            nameof(LayoutMode) => SettingsChangeImpact.Layout,
            nameof(ShowZoomIndicator) => SettingsChangeImpact.Navigator,
            nameof(ShowRating) => SettingsChangeImpact.Rating,
            nameof(EnabledExifItems) => SettingsChangeImpact.Exif,
            nameof(Hotkeys) => SettingsChangeImpact.Commands | SettingsChangeImpact.InputBindings,
            nameof(UseAppleKeyboard) or nameof(MapCommandTarget) or nameof(MapOptionTarget) or nameof(MapControlTarget)
                => SettingsChangeImpact.Commands | SettingsChangeImpact.InputBindings,
            nameof(SelectedFormats) or nameof(SameNameAsOnePhoto) => SettingsChangeImpact.FileProjection,
            nameof(AnalysisImmediateCompute) or nameof(AnalysisImmediateCvCompute) => SettingsChangeImpact.Analysis,
            nameof(SimilarityPanelExpanded) or nameof(SimilarityThreshold) or nameof(SimilarityMaxResults)
                => SettingsChangeImpact.Similarity,
            _ => SettingsChangeImpact.None,
        };
    }

    /// <summary>批次内并入累计旗标，由最外层统一发布；已释放的生命周期不发布。</summary>
    private void PublishPresentationImpact(SettingsChangeImpact impact)
    {
        if (impact == SettingsChangeImpact.None || !_initialized) return;

        lock (_presentationGate)
        {
            if (_disposed) return;
            if (_presentationBatchDepth > 0)
            {
                _pendingPresentationImpact |= impact;
                return;
            }
        }

        PresentationChanged?.Invoke(impact);
    }

    private void BeginPresentationBatch()
    {
        lock (_presentationGate) _presentationBatchDepth++;
    }

    private void EndPresentationBatch()
    {
        SettingsChangeImpact pending;
        lock (_presentationGate)
        {
            if (_presentationBatchDepth > 0) _presentationBatchDepth--;
            if (_presentationBatchDepth > 0) return;

            pending = _pendingPresentationImpact;
            _pendingPresentationImpact = SettingsChangeImpact.None;
        }

        // 异常退出同样发布已应用的部分，避免界面停在半更新的结构上。
        PublishPresentationImpact(pending);
    }

    private void RunInPresentationBatch(Action action)
    {
        BeginPresentationBatch();
        try
        {
            action();
        }
        finally
        {
            EndPresentationBatch();
        }
    }

    private ImmutableArray<HotkeySetting> CaptureHotkeySettings() => Hotkeys
        .Select(item => new HotkeySetting(item.Command, item.IsDisplay, item.PrimaryHotkey, item.SecondaryHotkey))
        .ToImmutableArray();

    private static bool HotkeySettingsEqual(ImmutableArray<HotkeySetting> left, ImmutableArray<HotkeySetting> right)
    {
        if (left.Length != right.Length) return false;

        for (var index = 0; index < left.Length; index++)
        {
            if (left[index].CommandId != right[index].CommandId
                || left[index].IsDisplay != right[index].IsDisplay
                || !GesturesEqual(left[index].PrimaryGesture, right[index].PrimaryGesture)
                || !GesturesEqual(left[index].SecondaryGesture, right[index].SecondaryGesture))
            {
                return false;
            }
        }

        return true;
    }

    private static bool GesturesEqual(AppGesture? left, AppGesture? right)
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
