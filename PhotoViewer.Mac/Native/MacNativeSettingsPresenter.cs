using AppKit;
using System.Globalization;
using System.Reactive.Linq;
using System.Text.Json;
using PhotoViewer.Core.Input;
using PhotoViewer.Core.Settings;
using PhotoViewer.ViewModels.Settings;

namespace PhotoViewer.Mac.Native;

/// <summary>macOS 26 原生设置：宿主为 SwiftUI（libMacSettingsUI.dylib），
/// C# 侧持有工作副本、校验与持久化，只通过 JSON 状态快照与动作回传驱动界面。
/// 淘汰此前用 AppKit 手搓的模拟设置界面，主窗口不受影响。</summary>
public sealed class MacNativeSettingsPresenter : INativeSettingsPresenter
{
    private static readonly string[] CategoryNames = ["文件", "预览", "控制", "EXIF", "AI"];

    private NativeSettingsSession? _session;
    private bool _presenting;
    private bool _closing;
    private int _category = -1;
    private int _recordingIndex = -1;
    private bool _recordingPrimary;
    private readonly List<int> _historyBack = [];
    private readonly List<int> _historyForward = [];

    /// <summary>当前原生设置窗口是否展示中。</summary>
    internal bool IsPresenting => _presenting;
    /// <summary>验收读取当前分类索引。</summary>
    internal int CurrentCategory => _category;
    /// <summary>验收检查当前是否处于快捷键录制态。</summary>
    internal bool IsRecording => _recordingIndex >= 0;
    /// <summary>验收检查工作副本隔离性，UI 外部不直接访问编辑实例。</summary>
    internal SettingsViewModel WorkingSettings => Settings;

    /// <summary>切换原生设置分类，记录历史以便顶部导航回退。</summary>
    public void ShowCategory(int category)
    {
        if (!_presenting) return;
        int target = Math.Clamp(category, 0, CategoryNames.Length - 1);
        if (target != _category)
        {
            if (_category >= 0) { _historyBack.Add(_category); _historyForward.Clear(); }
            _category = target;
        }
        PushState();
    }

    /// <summary>验收驱动历史后退。</summary>
    internal void NavigateBack() => GoBack();
    /// <summary>验收驱动历史前进。</summary>
    internal void NavigateForward() => GoForward();
    /// <summary>验收读取窗口号，供宿主截图取证与事件投递。</summary>
    internal int WindowNumber => MacSettingsNative.WindowNumber();
    /// <summary>验收校验窗口深色外观。</summary>
    internal bool IsDarkAppearance => MacSettingsNative.IsDark();
    /// <summary>验收校验 SwiftUI 宿主视图已挂载。</summary>
    internal bool HasHostingController => MacSettingsNative.HasHostingController();
    /// <summary>验收调整窗口内容尺寸。</summary>
    internal void SetContentSize(double width, double height) => MacSettingsNative.SetContentSize(width, height);
    /// <summary>验收导出窗口控件绘制。</summary>
    internal void RenderTo(string path) => MacSettingsNative.Render(path);
    /// <summary>验收进入快捷键录制态。</summary>
    internal void BeginRecording(int hotkeyIndex, bool primary) => MacSettingsNative.BeginRecording(hotkeyIndex, primary);
    /// <summary>关闭设置窗口并触发工作副本回写；从未展示过时不动 SwiftUI 侧
    /// （退出路径会调用本方法，此时不该为了关一个不存在的窗口去载入设置动态库）。</summary>
    internal void CloseWindow()
    {
        if (!_presenting) return;
        MacSettingsNative.Close();
    }
    /// <summary>当前是否有已展示的设置窗口（退出路径回归读取）。</summary>
    internal bool IsPresented => _presenting;
    /// <summary>验收以与 SwiftUI 相同的动作通道驱动一次交互。</summary>
    internal int ApplyAction(string json) => HandleAction(json);

    /// <summary>复用已打开窗口，否则建立工作副本并交由 SwiftUI 宿主展示。</summary>
    public bool TryPresent(SettingsViewModel settings)
    {
        MacSettingsNative.EnsureLoaded();
        if (!_presenting)
        {
            _session = new NativeSettingsSession(settings);
            MacSettingsNative.ActionSink = HandleAction;
            MacSettingsNative.CloseSink = HandleClose;
            _category = 0;
            _presenting = true;
        }
        MacSettingsNative.Show(JsonSerializer.Serialize(BuildState(), MacSettingsJsonContext.Default.MacSettingsStateDto));
        return true;
    }

    private SettingsViewModel Settings => _session?.WorkingSettings ?? throw new InvalidOperationException("设置会话未建立");

    /// <summary>窗口关闭：完成一次性回写并释放工作副本，等待下次展示重建。</summary>
    private void HandleClose()
    {
        if (_closing) return;
        _closing = true;
        _session?.Apply();
        _session?.Dispose();
        _session = null;
        _presenting = false;
        _category = -1;
        _recordingIndex = -1;
        _recordingPrimary = false;
        _historyBack.Clear();
        _historyForward.Clear();
        _closing = false;
    }

    /// <summary>回到上一个浏览过的分类，并把当前分类压入前进栈。</summary>
    private void GoBack()
    {
        if (_historyBack.Count == 0) return;
        int target = _historyBack[^1];
        _historyBack.RemoveAt(_historyBack.Count - 1);
        _historyForward.Add(_category);
        _category = target;
        PushState();
    }

    /// <summary>前进到下一个浏览过的分类，并把当前分类压入后退栈。</summary>
    private void GoForward()
    {
        if (_historyForward.Count == 0) return;
        int target = _historyForward[^1];
        _historyForward.RemoveAt(_historyForward.Count - 1);
        _historyBack.Add(_category);
        _category = target;
        PushState();
    }

    /// <summary>把最新派生状态推回 SwiftUI，保持单一数据源。</summary>
    private void PushState()
    {
        if (!_presenting) return;
        MacSettingsNative.UpdateState(JsonSerializer.Serialize(BuildState(), MacSettingsJsonContext.Default.MacSettingsStateDto));
    }

    /// <summary>构建完整窗口状态快照，含派生文本、冲突与语义级别。</summary>
    private MacSettingsStateDto BuildState()
    {
        var settings = Settings;
        int layoutIndex = 0;
        for (int index = 0; index < settings.LayoutModes.Count; index++)
        {
            if (settings.LayoutModes[index].Value == settings.LayoutMode) { layoutIndex = index; break; }
        }
        return new MacSettingsStateDto
        {
            Category = _category,
            Title = _category >= 0 ? CategoryNames[_category] : string.Empty,
            CanGoBack = _historyBack.Count > 0,
            CanGoForward = _historyForward.Count > 0,
            File = new MacFileStateDto
            {
                SameNameAsOnePhoto = settings.SameNameAsOnePhoto,
                Formats = settings.FileFormats.Select(item => new MacFormatDto
                {
                    Name = item.DisplayName, Extensions = item.ExtensionsText, Enabled = item.IsEnabled,
                }).ToList(),
                NativeParallelism = settings.NativePreloadParallelism,
                NativeParallelismMax = settings.NativePreloadParallelismMaximum,
                CpuParallelism = settings.CpuPreloadParallelism,
                CpuParallelismMax = settings.CpuPreloadParallelismMaximum,
                CacheMemory = settings.BitmapCacheMaxMemory,
                CacheCount = settings.BitmapCacheMaxCount,
                PreloadForward = settings.PreloadForwardCount,
                PreloadBackward = settings.PreloadBackwardCount,
                PreloadCenter = settings.VisibleCenterPreloadCount,
                PreloadDelay = settings.VisibleCenterDelayMs,
                PreloadMax = settings.PreloadMaximum,
                BudgetInfo = settings.PerformanceBudgetInfo,
                BudgetLevel = LevelName(settings.PerformanceBudgetInfoLevel),
                CacheCountInfo = settings.BitmapCacheCountInfo,
                CacheCountLevel = LevelName(settings.BitmapCacheCountInfoLevel),
            },
            Preview = new MacPreviewStateDto
            {
                ShowZoomIndicator = settings.ShowZoomIndicator,
                Presets = settings.ScalePresets.Select((preset, index) => new MacPresetDto
                {
                    Index = index, Text = preset.Text, Display = preset.Display,
                }).ToList(),
            },
            Control = new MacControlStateDto
            {
                LayoutModes = settings.LayoutModes.Select(mode => new MacLayoutModeDto
                {
                    Name = mode.DisplayName, Description = mode.Description,
                }).ToList(),
                LayoutIndex = layoutIndex,
                Hotkeys = settings.Hotkeys.Select((item, index) => new MacHotkeyDto
                {
                    Name = item.Name,
                    IsDisplay = item.IsDisplay,
                    Primary = MacInput.FormatGesture(item.PrimaryHotkey, settings),
                    Secondary = MacInput.FormatGesture(item.SecondaryHotkey, settings),
                    PrimaryConflict = item.HasPrimaryConflict,
                    SecondaryConflict = item.HasSecondaryConflict,
                    Recording = _recordingIndex == index ? (_recordingPrimary ? "primary" : "secondary") : string.Empty,
                }).ToList(),
                UseAppleKeyboard = settings.UseAppleKeyboard,
                MappingTargets = ["不映射", "Ctrl", "Alt", "Win"],
                CommandTarget = (int)settings.MapCommandTarget,
                OptionTarget = (int)settings.MapOptionTarget,
                ControlTarget = (int)settings.MapControlTarget,
                RecordingActive = _recordingIndex >= 0,
            },
            Exif = new MacExifStateDto
            {
                Items = settings.ExifDisplayItems.Select(item => new MacExifItemDto
                {
                    Name = item.DisplayName, Enabled = item.IsEnabled,
                }).ToList(),
                ShowRating = settings.ShowRating,
                SafeSetRating = settings.SafeSetRating,
            },
            Ai = new MacAiStateDto
            {
                SimilarityThreshold = (int)Math.Round(settings.SimilarityThreshold * 100),
                MaxResults = settings.SimilarityMaxResults,
                ImmediateCompute = settings.AnalysisImmediateCompute,
                ClearButtonText = settings.ClearDbButtonText,
                ClearStatus = settings.ClearDbStatus,
                ClearBusy = settings.ClearDbBusy,
            },
        };
    }

    /// <summary>把提示语义级别映射为 Swift 侧可读名称。</summary>
    private static string LevelName(PerfInfoLevel level) => level switch
    {
        PerfInfoLevel.Warning => "warning", PerfInfoLevel.Error => "error", _ => "normal",
    };

    /// <summary>处理 SwiftUI 回传动作；录制事件返回消费标记，其余返回 0。</summary>
    private int HandleAction(string json)
    {
        if (!_presenting) return 0;
        MacSettingsActionDto? action;
        try { action = JsonSerializer.Deserialize(json, MacSettingsJsonContext.Default.MacSettingsActionDto); }
        catch (JsonException exception)
        {
            Console.Error.WriteLine("[MacSettingsUI] action decode failed: " + exception.Message + " <- " + json);
            return 0;
        }
        if (action == null) return 0;
        switch (action.Type)
        {
            case "selectCategory":
                ShowCategory(action.Index);
                return 0;
            case "navigate":
                if (action.Forward) GoForward(); else GoBack();
                return 0;
            case "setBool":
                ApplyBool(action.Key ?? throw new InvalidOperationException("动作缺少键名"), action.BoolValue);
                PushState();
                return 0;
            case "setInt":
                ApplyInt(action.Key ?? throw new InvalidOperationException("动作缺少键名"), action.IntValue);
                PushState();
                return 0;
            case "moveFormat":
                Settings.MoveFileFormat(action.From, action.To);
                PushState();
                return 0;
            case "moveHotkey":
                Settings.MoveHotkey(action.From, action.To);
                PushState();
                return 0;
            case "moveExif":
                Settings.MoveExifDisplay(action.From, action.To);
                PushState();
                return 0;
            case "addPreset":
                Settings.AddScalePreset();
                PushState();
                return 0;
            case "removePreset":
                if (action.Index >= 0 && action.Index < Settings.ScalePresets.Count) Settings.RemoveScalePreset(Settings.ScalePresets[action.Index]);
                PushState();
                return 0;
            case "applyPreset":
                ApplyPreset(action.Index, action.Text ?? string.Empty);
                PushState();
                return 0;
            case "beginRecord":
                _recordingIndex = action.Index;
                _recordingPrimary = action.IntValue != 0;
                PushState();
                return 0;
            case "clearDatabase":
                _ = ClearDatabaseAsync();
                PushState();
                return 0;
            case "recordEvent":
                return HandleRecordEvent(action);
            default:
                throw new InvalidOperationException("未知的设置动作：" + action.Type);
        }
    }

    /// <summary>按标量键写入工作副本，未知键显式报错避免静默失配。</summary>
    private void ApplyBool(string key, bool value)
    {
        var settings = Settings;
        switch (key)
        {
            case "file.sameName": settings.SameNameAsOnePhoto = value; break;
            case "preview.showZoom": settings.ShowZoomIndicator = value; break;
            case "control.useAppleKeyboard": settings.UseAppleKeyboard = value; break;
            case "exif.showRating": settings.ShowRating = value; break;
            case "exif.safeSetRating": settings.SafeSetRating = value; break;
            case "ai.immediateCompute": settings.AnalysisImmediateCompute = value; break;
            default:
                if (key.StartsWith("file.format.", StringComparison.Ordinal))
                {
                    settings.FileFormats[int.Parse(key["file.format.".Length..], CultureInfo.InvariantCulture)].IsEnabled = value;
                    break;
                }
                if (key.StartsWith("control.hotkey.display.", StringComparison.Ordinal))
                {
                    settings.Hotkeys[int.Parse(key["control.hotkey.display.".Length..], CultureInfo.InvariantCulture)].IsDisplay = value;
                    break;
                }
                if (key.StartsWith("exif.item.", StringComparison.Ordinal))
                {
                    settings.ExifDisplayItems[int.Parse(key["exif.item.".Length..], CultureInfo.InvariantCulture)].IsEnabled = value;
                    break;
                }
                throw new InvalidOperationException("未知的布尔设置键：" + key);
        }
    }

    /// <summary>按标量键写入工作副本，未知键显式报错避免静默失配。</summary>
    private void ApplyInt(string key, int value)
    {
        var settings = Settings;
        switch (key)
        {
            case "file.nativeParallelism": settings.NativePreloadParallelism = value; break;
            case "file.cpuParallelism": settings.CpuPreloadParallelism = value; break;
            case "file.cacheMemory": settings.BitmapCacheMaxMemory = value; break;
            case "file.cacheCount": settings.BitmapCacheMaxCount = value; break;
            case "file.preloadForward": settings.PreloadForwardCount = value; break;
            case "file.preloadBackward": settings.PreloadBackwardCount = value; break;
            case "file.preloadCenter": settings.VisibleCenterPreloadCount = value; break;
            case "file.preloadDelay": settings.VisibleCenterDelayMs = value; break;
            case "control.layout": settings.LayoutMode = settings.LayoutModes[Math.Clamp(value, 0, settings.LayoutModes.Count - 1)].Value; break;
            case "control.mapCommand": settings.MapCommandTarget = (AppleMappingTarget)value; break;
            case "control.mapOption": settings.MapOptionTarget = (AppleMappingTarget)value; break;
            case "control.mapControl": settings.MapControlTarget = (AppleMappingTarget)value; break;
            case "ai.threshold": settings.SimilarityThreshold = value / 100.0; break;
            case "ai.maxResults": settings.SimilarityMaxResults = value; break;
            default: throw new InvalidOperationException("未知的整数设置键：" + key);
        }
    }

    /// <summary>校验缩放比例输入并应用；非法输入保持原值，由状态回推复位界面。</summary>
    private void ApplyPreset(int index, string text)
    {
        if (index < 0 || index >= Settings.ScalePresets.Count) return;
        var preset = Settings.ScalePresets[index];
        if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double value) && double.IsFinite(value) && value > 0 && value <= 1600)
        {
            preset.Text = value.ToString("0.###", CultureInfo.InvariantCulture);
        }
        Settings.ApplyScalePreset();
    }

    /// <summary>系统确认后顺序完成共享两段确认命令，避免 ReactiveCommand 执行状态竞态。</summary>
    private async Task ClearDatabaseAsync()
    {
        var settings = Settings;
        if (!settings.ClearDbConfirming) await settings.ClearDatabaseCommand.Execute();
        await settings.ClearDatabaseCommand.Execute();
        PushState();
    }

    /// <summary>录制态处理真实事件：Escape 取消、Delete/Backspace 清空，其余复用共享手势映射。返回消费标记。</summary>
    private int HandleRecordEvent(MacSettingsActionDto action)
    {
        if (_recordingIndex < 0 || _recordingIndex >= Settings.Hotkeys.Count) return 0;
        if (action.EventType == (int)NSEventType.KeyDown && action.KeyCode == 53) { StopRecording(); PushState(); return 1; }
        bool clear = action.EventType == (int)NSEventType.KeyDown && action.KeyCode is 51 or 117;
        if (clear) { AssignGesture(null); PushState(); return 1; }
        var gesture = MacInput.FromRaw(action.EventType, (ushort)action.KeyCode, action.Modifiers, action.ButtonNumber,
            action.ScrollingDeltaY, action.Precise, action.Characters, Settings);
        if (gesture == null) return 0;
        AssignGesture(gesture);
        PushState();
        return 1;
    }

    /// <summary>把录制到的手势写入当前行主/次绑定并重新检测冲突。</summary>
    private void AssignGesture(AppGesture? gesture)
    {
        var hotkey = Settings.Hotkeys[_recordingIndex];
        if (_recordingPrimary) hotkey.PrimaryHotkey = gesture; else hotkey.SecondaryHotkey = gesture;
        _recordingIndex = -1;
        _recordingPrimary = false;
        Settings.CheckHotkeyConflicts();
    }

    /// <summary>结束录制态。</summary>
    private void StopRecording()
    {
        _recordingIndex = -1;
        _recordingPrimary = false;
    }
}
