using System.Collections.ObjectModel;
using System.Reactive.Concurrency;
using System.Reflection;
using PhotoViewer.Core.Input;
using PhotoViewer.Core.Memory;
using PhotoViewer.Core.Platform;
using PhotoViewer.Core.Settings;
using PhotoViewer.ViewModels.Main;
using PhotoViewer.ViewModels.Settings;
using ReactiveUI;

namespace CoreContractCheck;

/// <summary>
/// 设置展示变化契约：<see cref="SettingsViewModel.PresentationChanged"/> 只携带影响原生展示结构的旗标，
/// 属性通知仍逐项保留。用例覆盖映射白名单、真实 VM 的常规/嵌套热键/格式/EXIF 变更、
/// ApplySnapshot 合并、Dispose 与周期诊断探测。
/// </summary>
internal static class SettingsPresentationChecks
{
    private static readonly SettingsChangeImpact CommandsAndBindings =
        SettingsChangeImpact.Commands | SettingsChangeImpact.InputBindings;

    private static readonly FieldInfo MemoryProviderField =
        typeof(MemoryGovernor).GetField("_provider", BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException("MemoryGovernor._provider 字段不存在，探测替身无法注入。");

    public static void Run(Checks c)
    {
        c.Section("设置展示变化契约");
        // 周期诊断用例依赖 MemoryGovernor 的真实探测与 VM 内 500ms 节流，需要能调度延迟的 UI 调度器。
        UiDispatcher.Initialize(new ThreadPoolDispatcher());
        try
        {
            RunMappingChecks(c);
            RunPresentationChecks(c);
            RunPeriodicProbeCheck(c);
            RunMainViewModelChecks(c);
        }
        finally
        {
            UiDispatcher.Initialize(new InlineDispatcher());
        }
    }

    private static void RunMappingChecks(Checks c)
    {
        c.Equal("空属性名按全部刷新", SettingsChangeImpact.All, SettingsViewModel.GetPresentationImpact(null));
        c.Equal("空字符串按全部刷新", SettingsChangeImpact.All, SettingsViewModel.GetPresentationImpact(string.Empty));
        c.Equal("布局映射", SettingsChangeImpact.Layout,
            SettingsViewModel.GetPresentationImpact(nameof(SettingsViewModel.LayoutMode)));
        c.Equal("导航器映射", SettingsChangeImpact.Navigator,
            SettingsViewModel.GetPresentationImpact(nameof(SettingsViewModel.ShowZoomIndicator)));
        c.Equal("星级映射", SettingsChangeImpact.Rating,
            SettingsViewModel.GetPresentationImpact(nameof(SettingsViewModel.ShowRating)));
        c.Equal("EXIF 映射", SettingsChangeImpact.Exif,
            SettingsViewModel.GetPresentationImpact(nameof(SettingsViewModel.EnabledExifItems)));
        c.Equal("快捷键表映射到命令与输入绑定", CommandsAndBindings,
            SettingsViewModel.GetPresentationImpact(nameof(SettingsViewModel.Hotkeys)));
        foreach (var name in new[]
                 {
                     nameof(SettingsViewModel.UseAppleKeyboard),
                     nameof(SettingsViewModel.MapCommandTarget),
                     nameof(SettingsViewModel.MapOptionTarget),
                     nameof(SettingsViewModel.MapControlTarget),
                 })
        {
            c.Equal("键盘映射 " + name, CommandsAndBindings, SettingsViewModel.GetPresentationImpact(name));
        }

        c.Equal("格式集合映射到文件投影", SettingsChangeImpact.FileProjection,
            SettingsViewModel.GetPresentationImpact(nameof(SettingsViewModel.SelectedFormats)));
        c.Equal("同名合并映射到文件投影", SettingsChangeImpact.FileProjection,
            SettingsViewModel.GetPresentationImpact(nameof(SettingsViewModel.SameNameAsOnePhoto)));
        c.Equal("DINO 即时计算映射到分析栏", SettingsChangeImpact.Analysis,
            SettingsViewModel.GetPresentationImpact(nameof(SettingsViewModel.AnalysisImmediateCompute)));
        c.Equal("CV 即时计算映射到分析栏", SettingsChangeImpact.Analysis,
            SettingsViewModel.GetPresentationImpact(nameof(SettingsViewModel.AnalysisImmediateCvCompute)));
        foreach (var name in new[]
                 {
                     nameof(SettingsViewModel.SimilarityPanelExpanded),
                     nameof(SettingsViewModel.SimilarityThreshold),
                     nameof(SettingsViewModel.SimilarityMaxResults),
                 })
        {
            c.Equal("相似聚类 " + name, SettingsChangeImpact.Similarity, SettingsViewModel.GetPresentationImpact(name));
        }

        var allSingles = SettingsChangeImpact.Layout | SettingsChangeImpact.Navigator
            | SettingsChangeImpact.Rating | SettingsChangeImpact.Exif | SettingsChangeImpact.Commands
            | SettingsChangeImpact.InputBindings | SettingsChangeImpact.FileProjection
            | SettingsChangeImpact.Analysis | SettingsChangeImpact.Similarity;
        c.Equal("All 覆盖全部单项", allSingles, SettingsChangeImpact.All);

        foreach (var name in new[]
                 {
                     nameof(SettingsViewModel.BitmapCacheCountInfo),
                     nameof(SettingsViewModel.BitmapCacheCountInfoLevel),
                     nameof(SettingsViewModel.PerformanceBudgetInfo),
                     nameof(SettingsViewModel.PerformanceBudgetInfoLevel),
                 })
        {
            c.Equal("诊断属性不映射 " + name, SettingsChangeImpact.None, SettingsViewModel.GetPresentationImpact(name));
        }

        c.Equal("未知属性不映射", SettingsChangeImpact.None, SettingsViewModel.GetPresentationImpact("NotARealProperty"));
        c.Equal("缓存预算属性不映射", SettingsChangeImpact.None,
            SettingsViewModel.GetPresentationImpact(nameof(SettingsViewModel.BitmapCacheMaxCount)));
        c.Equal("缩放预设不映射", SettingsChangeImpact.None,
            SettingsViewModel.GetPresentationImpact(nameof(SettingsViewModel.ScalePresets)));
    }

    private static void RunPresentationChecks(Checks c)
    {
        using var settings = new SettingsViewModel();
        var recorder = new Recorder();
        var changedNames = new ChangedNames();
        settings.PresentationChanged += recorder.OnImpact;
        settings.PropertyChanged += (_, args) => changedNames.Add(args.PropertyName);

        settings.ShowRating = !settings.ShowRating;
        Single(c, "评分可见性发布 Rating", recorder, SettingsChangeImpact.Rating);

        settings.LayoutMode = settings.LayoutMode == LayoutMode.Col ? LayoutMode.Row : LayoutMode.Col;
        Single(c, "布局模式发布 Layout", recorder, SettingsChangeImpact.Layout);

        settings.ShowZoomIndicator = !settings.ShowZoomIndicator;
        Single(c, "缩放指示发布 Navigator", recorder, SettingsChangeImpact.Navigator);

        settings.SameNameAsOnePhoto = !settings.SameNameAsOnePhoto;
        Single(c, "同名合并发布 FileProjection", recorder, SettingsChangeImpact.FileProjection);

        settings.AnalysisImmediateCompute = !settings.AnalysisImmediateCompute;
        Single(c, "DINO 即时计算发布 Analysis", recorder, SettingsChangeImpact.Analysis);

        settings.AnalysisImmediateCvCompute = !settings.AnalysisImmediateCvCompute;
        Single(c, "CV 即时计算发布 Analysis", recorder, SettingsChangeImpact.Analysis);

        settings.SimilarityThreshold = settings.SimilarityThreshold > 0.9 ? 0.8 : 0.9;
        Single(c, "相似阈值发布 Similarity", recorder, SettingsChangeImpact.Similarity);

        settings.SimilarityPanelExpanded = !settings.SimilarityPanelExpanded;
        Single(c, "相似面板展开发布 Similarity", recorder, SettingsChangeImpact.Similarity);

        settings.UseAppleKeyboard = !settings.UseAppleKeyboard;
        Single(c, "Apple 键盘开关发布命令与绑定", recorder, CommandsAndBindings);

        settings.MapCommandTarget = settings.MapCommandTarget == AppleMappingTarget.Ctrl
            ? AppleMappingTarget.Alt
            : AppleMappingTarget.Ctrl;
        Single(c, "修饰键映射发布命令与绑定", recorder, CommandsAndBindings);

        settings.FileFormats[0].IsEnabled = !settings.FileFormats[0].IsEnabled;
        Single(c, "格式启停发布 FileProjection", recorder, SettingsChangeImpact.FileProjection);

        settings.FileFormats[0].IsEnabled = !settings.FileFormats[0].IsEnabled;
        Single(c, "格式恢复启停发布 FileProjection", recorder, SettingsChangeImpact.FileProjection);

        settings.MoveFileFormat(0, 1);
        Single(c, "格式排序只发一次 FileProjection", recorder, SettingsChangeImpact.FileProjection);

        settings.ExifDisplayItems[0].IsEnabled = !settings.ExifDisplayItems[0].IsEnabled;
        Single(c, "EXIF 启停发布 Exif", recorder, SettingsChangeImpact.Exif);

        settings.ExifDisplayItems[0].IsEnabled = !settings.ExifDisplayItems[0].IsEnabled;
        Single(c, "EXIF 恢复启停发布 Exif", recorder, SettingsChangeImpact.Exif);

        settings.MoveExifDisplay(0, 1);
        Single(c, "EXIF 排序只发一次 Exif", recorder, SettingsChangeImpact.Exif);

        settings.Hotkeys[0].IsDisplay = !settings.Hotkeys[0].IsDisplay;
        Single(c, "热键显隐发布命令与绑定", recorder, CommandsAndBindings);

        settings.Hotkeys[0].PrimaryHotkey = AppGesture.FromKey(new AppKeyGesture(GestureKey.Q));
        Single(c, "热键主绑定修改发布命令与绑定", recorder, CommandsAndBindings);

        settings.MoveHotkey(0, 2);
        Single(c, "热键排序只发一次命令与绑定", recorder, CommandsAndBindings);

        settings.Hotkeys[0].HasPrimaryConflict = !settings.Hotkeys[0].HasPrimaryConflict;
        ((IReactiveObject)settings.Hotkeys[0]).RaisePropertyChanged(nameof(SettingsViewModel.HotkeyItem.PrimaryHotkeyText));
        c.Equal("派生冲突与文本通知不发布", 0, recorder.Count);

        var diagnosticNames = new[]
        {
            nameof(SettingsViewModel.BitmapCacheCountInfo),
            nameof(SettingsViewModel.BitmapCacheCountInfoLevel),
            nameof(SettingsViewModel.PerformanceBudgetInfo),
            nameof(SettingsViewModel.PerformanceBudgetInfoLevel),
        };
        foreach (var name in diagnosticNames)
            ((IReactiveObject)settings).RaisePropertyChanged(name);
        c.Equal("诊断属性名不发布展示事件", 0, recorder.Count);
        c.True("诊断通知仍走 PropertyChanged", diagnosticNames.All(changedNames.Contains),
            "缺少 " + string.Join(", ", diagnosticNames.Where(name => !changedNames.Contains(name))));

        RunApplySnapshotCheck(c, settings, recorder, changedNames);
        RunPrecisionChecks(c, settings, recorder);

        recorder.Reset();
        c.ThrowsExact<ArgumentNullException>("ApplySnapshot 拒绝 null",
            () => settings.ApplySnapshot(null!));
        c.Equal("ApplySnapshot 异常路径不发布", 0, recorder.Count);

        var lateEvents = 0;
        recorder.Reset();
        settings.Dispose();
        settings.PresentationChanged += _ => lateEvents++;
        settings.ShowRating = !settings.ShowRating;
        settings.Hotkeys[0].IsDisplay = !settings.Hotkeys[0].IsDisplay;
        c.Equal("Dispose 后属性与热键变更不再发布", 0, recorder.Count + lateEvents);
    }

    private static void RunApplySnapshotCheck(
        Checks c,
        SettingsViewModel settings,
        Recorder recorder,
        ChangedNames changedNames)
    {
        recorder.Reset();
        changedNames.Clear();

        var current = settings.CreateSnapshot();
        var updated = current with
        {
            LayoutMode = current.LayoutMode == LayoutMode.Col ? LayoutMode.Auto : LayoutMode.Col,
            ShowRating = !current.ShowRating,
            SimilarityThreshold = current.SimilarityThreshold >= 0.9 ? 0.8 : 0.9,
            AnalysisImmediateCompute = !current.AnalysisImmediateCompute,
            FileFormats = current.FileFormats.SetItem(
                0, current.FileFormats[0] with { IsEnabled = !current.FileFormats[0].IsEnabled }),
            ExifFields = current.ExifFields.SetItem(
                0, current.ExifFields[0] with { IsEnabled = !current.ExifFields[0].IsEnabled }),
            Hotkeys = current.Hotkeys.SetItem(
                0, current.Hotkeys[0] with { IsDisplay = !current.Hotkeys[0].IsDisplay }),
        };

        settings.ApplySnapshot(updated);

        var events = recorder.Snapshot();
        recorder.Reset();
        c.Equal("ApplySnapshot 多字段只发一次展示事件", 1, events.Length);
        var expected = SettingsChangeImpact.Layout | SettingsChangeImpact.Rating
            | SettingsChangeImpact.FileProjection | SettingsChangeImpact.Exif
            | SettingsChangeImpact.Commands | SettingsChangeImpact.InputBindings
            | SettingsChangeImpact.Similarity | SettingsChangeImpact.Analysis;
        c.True("ApplySnapshot 旗标包含真实变更",
            events.Length == 1 && (events[0] & expected) == expected,
            events.Length == 1 ? $"实际 {events[0]}" : $"实际 {events.Length} 次事件");
        c.True("ApplySnapshot 未报告未变更区域",
            events.Length == 1 && (events[0] & SettingsChangeImpact.Navigator) == 0,
            events.Length == 1 ? $"实际 {events[0]}" : $"实际 {events.Length} 次事件");
        c.True("ApplySnapshot 仍逐项发 PropertyChanged",
            changedNames.Contains(nameof(SettingsViewModel.ShowRating))
                && changedNames.Contains(nameof(SettingsViewModel.LayoutMode)),
            "未收到 ShowRating / LayoutMode 通知");
    }

    private static void RunPrecisionChecks(Checks c, SettingsViewModel settings, Recorder recorder)
    {
        recorder.Reset();
        settings.ApplySnapshot(settings.CreateSnapshot());
        c.Equal("ApplySnapshot 相同快照零展示事件", 0, recorder.Count);

        var cacheOnly = settings.CreateSnapshot();
        recorder.Reset();
        settings.ApplySnapshot(cacheOnly with
        {
            BitmapCacheMaxCount = cacheOnly.BitmapCacheMaxCount >= 400
                ? 399
                : cacheOnly.BitmapCacheMaxCount + 1,
            BitmapCacheMaxMemory = cacheOnly.BitmapCacheMaxMemory >= 32768
                ? 32767
                : cacheOnly.BitmapCacheMaxMemory + 1,
        });
        c.Equal("仅缓存预算变更零展示事件", 0, recorder.Count);

        var zoomOnly = settings.CreateSnapshot();
        recorder.Reset();
        settings.ApplySnapshot(zoomOnly with { ShowZoomIndicator = !zoomOnly.ShowZoomIndicator });
        Single(c, "仅缩放指示变更精确 Navigator", recorder, SettingsChangeImpact.Navigator);

        var hotkeyOnly = settings.CreateSnapshot();
        recorder.Reset();
        settings.ApplySnapshot(hotkeyOnly with
        {
            Hotkeys = hotkeyOnly.Hotkeys.SetItem(
                0, hotkeyOnly.Hotkeys[0] with { IsDisplay = !hotkeyOnly.Hotkeys[0].IsDisplay }),
        });
        Single(c, "仅单个热键变更精确命令与绑定", recorder, CommandsAndBindings);

        // ResetHotkeys 重新挂接后不得叠加订阅，否则一次子项变更会发多次。
        settings.Hotkeys[0].IsDisplay = !settings.Hotkeys[0].IsDisplay;
        Single(c, "ResetHotkeys 后子项变更只发一次", recorder, CommandsAndBindings);

        RunHotkeyReplacementChecks(c, settings, recorder);
    }

    private static void RunHotkeyReplacementChecks(Checks c, SettingsViewModel settings, Recorder recorder)
    {
        var oldCollection = settings.Hotkeys;
        var replacement = new ObservableCollection<SettingsViewModel.HotkeyItem>(
            oldCollection.Select(CopyHotkey));

        recorder.Reset();
        settings.Hotkeys = settings.Hotkeys;
        c.Equal("热键同引用赋值不发布", 0, recorder.Count);

        recorder.Reset();
        settings.Hotkeys = replacement;
        Single(c, "热键整体替换发布命令与绑定", recorder, CommandsAndBindings);

        settings.Hotkeys[0].IsDisplay = !settings.Hotkeys[0].IsDisplay;
        Single(c, "替换后新集合子项变更发布", recorder, CommandsAndBindings);

        recorder.Reset();
        oldCollection[0].IsDisplay = !oldCollection[0].IsDisplay;
        c.Equal("替换后旧集合子项变更不发布", 0, recorder.Count);

        // Clear 的 Reset 通知不带 OldItems：已移除子项必须已被退订。
        var removedItem = settings.Hotkeys[0];
        recorder.Reset();
        settings.Hotkeys.Clear();
        Single(c, "热键清空发布一次命令与绑定", recorder, CommandsAndBindings);

        recorder.Reset();
        removedItem.IsDisplay = !removedItem.IsDisplay;
        c.Equal("清空后已移除子项改动不发布", 0, recorder.Count);

        recorder.Reset();
        settings.Hotkeys.Add(new SettingsViewModel.HotkeyItem("测试项", "Next", "N", "测试", true));
        Single(c, "清空后新增子项发布一次", recorder, CommandsAndBindings);
    }

    private static SettingsViewModel.HotkeyItem CopyHotkey(SettingsViewModel.HotkeyItem item)
        => new(item.Name, item.Command, item.DisplaySymbol, item.Tooltip, item.IsDisplay,
            item.PrimaryHotkey, item.SecondaryHotkey);

    private static void RunPeriodicProbeCheck(Checks c)
    {
        using var settings = new SettingsViewModel();
        var recorder = new Recorder();
        var changed = new ChangedNames();
        settings.PresentationChanged += recorder.OnImpact;
        settings.PropertyChanged += (_, args) => changed.Add(args.PropertyName);

        // 反射注入预算源后调用真实 EvaluateNow：不启动计时器，也不写用户 AppSupport 日志。
        var previousProvider = (IMemoryBudgetProvider?)MemoryProviderField.GetValue(null);
        var provider = new FixedBudgetProvider();
        MemoryProviderField.SetValue(null, provider);
        try
        {
            MemoryGovernor.EvaluateNow();

            var observed = SpinWait.SpinUntil(
                () => changed.Contains(nameof(SettingsViewModel.BitmapCacheCountInfo)),
                TimeSpan.FromSeconds(5));
            c.True("周期探测仍发诊断 PropertyChanged", observed,
                $"5s 内未收到 {nameof(SettingsViewModel.BitmapCacheCountInfo)}");
            var firstReading = settings.PerformanceBudgetInfo;

            // 读数变化后按 500ms 限流再刷新一次：两行文本与两个级别都跟新读数走，且仍不发展示事件。
            // 可编辑缓存上限的平台张数只看用户上限，因此刷新证据取系统内存那一行。
            changed.Clear();
            provider.BudgetBytes = 2L * 1024 * 1024 * 1024;
            provider.AvailableBytes = 1L * 1024 * 1024 * 1024;
            MemoryGovernor.EvaluateNow();

            var refreshed = SpinWait.SpinUntil(
                () => settings.PerformanceBudgetInfo != firstReading,
                TimeSpan.FromSeconds(5));
            c.True("读数变化后设备能力提示跟随探测刷新", refreshed,
                $"5s 内提示未变化：{firstReading} / {settings.PerformanceBudgetInfo}");

            foreach (var name in new[]
                     {
                         nameof(SettingsViewModel.PerformanceBudgetInfo),
                         nameof(SettingsViewModel.PerformanceBudgetInfoLevel),
                         nameof(SettingsViewModel.BitmapCacheCountInfo),
                         nameof(SettingsViewModel.BitmapCacheCountInfoLevel),
                     })
            {
                c.True("探测刷新 " + name, changed.Contains(name), "探测后未收到 " + name);
            }

            c.Equal("周期探测不发布展示事件", 0, recorder.Count);
        }
        finally
        {
            MemoryProviderField.SetValue(null, previousProvider);
        }
    }

    private static void RunMainViewModelChecks(Checks c)
    {
        // 默认仓库是内存实现，构造返回时设置已初始化并完成首轮回填；先建立稳定基线再订阅。
        using var main = new MainViewModel();
        var control = main.ControlVM;
        var settings = main.Settings;

        var exifRaises = 0;
        var controlsRaises = 0;
        var presentation = new Recorder();

        control.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(ControlViewModel.EnabledExifItems))
                Interlocked.Increment(ref exifRaises);
            else if (args.PropertyName == nameof(ControlViewModel.EnabledControls))
                Interlocked.Increment(ref controlsRaises);
        };
        settings.PresentationChanged += presentation.OnImpact;

        void ResetCounters()
        {
            Volatile.Write(ref exifRaises, 0);
            Volatile.Write(ref controlsRaises, 0);
            presentation.Reset();
        }

        string Counters() =>
            $"exif={Volatile.Read(ref exifRaises)} controls={Volatile.Read(ref controlsRaises)} presentation={presentation.Count}";

        ResetCounters();
        settings.ApplySnapshot(settings.CreateSnapshot());
        c.True("主场景相同快照三路零结构事件",
            Volatile.Read(ref exifRaises) == 0
                && Volatile.Read(ref controlsRaises) == 0
                && presentation.Count == 0,
            Counters());

        var cacheOnly = settings.CreateSnapshot();
        ResetCounters();
        settings.ApplySnapshot(cacheOnly with
        {
            BitmapCacheMaxCount = cacheOnly.BitmapCacheMaxCount >= 400
                ? 399
                : cacheOnly.BitmapCacheMaxCount + 1,
            BitmapCacheMaxMemory = cacheOnly.BitmapCacheMaxMemory >= 32768
                ? 32767
                : cacheOnly.BitmapCacheMaxMemory + 1,
        });
        c.True("主场景仅缓存预算三路零结构事件",
            Volatile.Read(ref exifRaises) == 0
                && Volatile.Read(ref controlsRaises) == 0
                && presentation.Count == 0,
            Counters());

        ResetCounters();
        settings.ExifDisplayItems[0].IsEnabled = !settings.ExifDisplayItems[0].IsEnabled;
        var exifEvents = presentation.Snapshot();
        c.Equal("主场景真实 EXIF 开关通知控制栏", 1, Volatile.Read(ref exifRaises));
        c.True("主场景真实 EXIF 开关只发 Exif",
            exifEvents.Length == 1 && exifEvents[0] == SettingsChangeImpact.Exif,
            $"presentation=[{string.Join(", ", exifEvents.Select(e => e.ToString()))}]");
        c.Equal("主场景真实 EXIF 开关不扰命令栏", 0, Volatile.Read(ref controlsRaises));

        ResetCounters();
        settings.Hotkeys[0].IsDisplay = !settings.Hotkeys[0].IsDisplay;
        var hotkeyEvents = presentation.Snapshot();
        c.Equal("主场景热键显隐通知控制栏", 1, Volatile.Read(ref controlsRaises));
        c.True("主场景热键显隐只发命令与绑定",
            hotkeyEvents.Length == 1 && hotkeyEvents[0] == CommandsAndBindings,
            $"presentation=[{string.Join(", ", hotkeyEvents.Select(e => e.ToString()))}]");
        c.Equal("主场景热键显隐不扰 EXIF", 0, Volatile.Read(ref exifRaises));

        ResetCounters();
        settings.Hotkeys[0].PrimaryHotkey = AppGesture.FromKey(new AppKeyGesture(GestureKey.Q));
        hotkeyEvents = presentation.Snapshot();
        c.True("主场景热键绑定变化只发命令与绑定",
            hotkeyEvents.Length == 1 && hotkeyEvents[0] == CommandsAndBindings,
            $"presentation=[{string.Join(", ", hotkeyEvents.Select(e => e.ToString()))}]");
        c.Equal("主场景热键绑定变化不扰 EXIF/命令栏",
            0, Volatile.Read(ref exifRaises) + Volatile.Read(ref controlsRaises));
    }

    private static void Single(Checks c, string item, Recorder recorder, SettingsChangeImpact expected)
    {
        var events = recorder.Snapshot();
        recorder.Reset();
        if (events.Length != 1)
        {
            c.Fail(item, $"期望 1 次事件，实际 {events.Length} 次 [{string.Join(", ", events.Select(e => e.ToString()))}]");
            return;
        }

        c.Equal(item, expected, events[0]);
    }

    private sealed class Recorder
    {
        private readonly object _gate = new();
        private readonly List<SettingsChangeImpact> _events = new();

        public int Count
        {
            get { lock (_gate) return _events.Count; }
        }

        public void OnImpact(SettingsChangeImpact impact)
        {
            lock (_gate) _events.Add(impact);
        }

        public SettingsChangeImpact[] Snapshot()
        {
            lock (_gate) return _events.ToArray();
        }

        public void Reset()
        {
            lock (_gate) _events.Clear();
        }
    }

    private sealed class ChangedNames
    {
        private readonly object _gate = new();
        private readonly HashSet<string> _names = new(StringComparer.Ordinal);

        public void Add(string? name)
        {
            if (name is null) return;
            lock (_gate) _names.Add(name);
        }

        public bool Contains(string name)
        {
            lock (_gate) return _names.Contains(name);
        }

        public void Clear()
        {
            lock (_gate) _names.Clear();
        }
    }

    private sealed class FixedBudgetProvider : IMemoryBudgetProvider
    {
        public long BudgetBytes { get; set; } = 8L * 1024 * 1024 * 1024;

        public long AvailableBytes { get; set; } = 8L * 1024 * 1024 * 1024;

        public SystemMemoryInfo GetSystemMemoryInfo() => new(16L * 1024 * 1024 * 1024, AvailableBytes);

        public long GetBudgetBytes() => BudgetBytes;

        public long GetAvailableBytes() => AvailableBytes;

        public long GetFootprintBytes() => 0;
    }

    private sealed class ThreadPoolDispatcher : IUiDispatcher
    {
        public bool CheckAccess() => true;

        public IScheduler MainThreadScheduler => DefaultScheduler.Instance;

        public void Post(Action action, UiPriority priority = UiPriority.Normal) => action();

        public Task InvokeAsync(Action action, UiPriority priority = UiPriority.Normal)
        {
            action();
            return Task.CompletedTask;
        }

        public Task<T> InvokeAsync<T>(Func<T> action, UiPriority priority = UiPriority.Normal)
            => Task.FromResult(action());
    }

    private sealed class InlineDispatcher : IUiDispatcher
    {
        public bool CheckAccess() => true;

        public IScheduler MainThreadScheduler => ImmediateScheduler.Instance;

        public void Post(Action action, UiPriority priority = UiPriority.Normal) => action();

        public Task InvokeAsync(Action action, UiPriority priority = UiPriority.Normal)
        {
            action();
            return Task.CompletedTask;
        }

        public Task<T> InvokeAsync<T>(Func<T> action, UiPriority priority = UiPriority.Normal)
            => Task.FromResult(action());
    }
}
