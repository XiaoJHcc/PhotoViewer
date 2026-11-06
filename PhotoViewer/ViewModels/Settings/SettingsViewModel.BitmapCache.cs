using System;
using ReactiveUI;

using PhotoViewer.Core;
using PhotoViewer.Core.Image;
using PhotoViewer.Core.Memory;
using PhotoViewer.Core.Platform;
using System.Reactive.Linq;

namespace PhotoViewer.ViewModels.Settings;

/// <summary>
/// 设置页提示信息的语义级别,由 View 层决定具体颜色。
/// </summary>
public enum PerfInfoLevel
{
    /// <summary>常规提示。</summary>
    Normal,
    /// <summary>警告(接近或达到建议阈值)。</summary>
    Warning,
    /// <summary>错误/超限。</summary>
    Error,
}

public partial class SettingsViewModel
{
    ///////////////////
    /// 位图缓存与预取设置
    ///////////////////

    private int _systemNativePreloadLimit = 8;
    private int _systemCpuPreloadLimit = 8;

    /// <summary>自动口径下（缓存张数不参与运行时）的预载数量上限，与默认预载数量一致。</summary>
    private const int AutomaticPreloadMaximum = 10;

    /// <summary>预载默认值的基准缓存张数：张数不少于它时直接用默认份数（上一张 10 / 下一张 5 / 列表滚动 5）。</summary>
    private const int DefaultPreloadBaselineCount = 30;

    /// <summary>基准张数下的上一张预载默认份数。</summary>
    private const int DefaultPreloadForwardCount = 10;

    /// <summary>基准张数下的下一张预载默认份数。</summary>
    private const int DefaultPreloadBackwardCount = 5;

    /// <summary>基准张数下的列表滚动预载默认份数。</summary>
    private const int DefaultPreloadCenterCount = 5;

    /// <summary>最近一次读取的设备能力估算与判定阈值（超限只看设备阈值，用户上限只决定可编辑平台的两档张数）。</summary>
    private DeviceCacheEstimate _deviceCacheEstimate = new(0, -1, 0, null, null);

    private long _deviceCacheBudgetBytes;

    private long? _deviceSafeCapacityBytes;

    /// <summary>
    /// 是否显示并允许编辑「缓存内存上限 / 缓存数量上限」。
    /// 缓存容量由实测预算自动管理的平台（iPad）不显示这两项，用户字段仍保留。
    /// </summary>
    public bool ShowsCacheLimits => !BitmapLoader.AutomaticCacheLimit;

    /// <summary>
    /// 初始化数据（缓存数量、内存大小、预载数量）
    /// 初始化监听（设备能力提示、有效缓存容量）
    /// </summary>
    private void InitializeBitMapCache()
    {
        // 读取设备能力，默认值由平台预算源给出（IPerformanceBudget.GetRecommended*）
        try
        {
            _systemNativePreloadLimit = Math.Max(1, PerformanceBudget.NativePreloadThreadLimit);
            _systemCpuPreloadLimit = Math.Max(1, PerformanceBudget.CpuPreloadThreadLimit);

            BitmapCacheMaxMemory = PerformanceBudget.RecommendedCacheMemoryMB;
            BitmapCacheMaxCount = PerformanceBudget.RecommendedCacheCount(BitmapCacheMaxMemory);
            NativePreloadParallelism = PerformanceBudget.RecommendedNativePreloadParallelism(BitmapCacheMaxMemory);
            CpuPreloadParallelism = PerformanceBudget.RecommendedCpuPreloadParallelism(BitmapCacheMaxMemory);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to initialize performance budget: {ex.Message}");
            BitmapCacheMaxMemory = 2048;
            _systemNativePreloadLimit = Math.Min(Math.Max(1, Environment.ProcessorCount), 8);
            _systemCpuPreloadLimit = Math.Min(Math.Max(1, Environment.ProcessorCount), 4);
            NativePreloadParallelism = _systemNativePreloadLimit;
            CpuPreloadParallelism = Math.Min(_systemCpuPreloadLimit, 1);
        }

        RefreshDeviceCacheCapability();

        // 初始化三个预载滑条的 百分比 backing 字段（0~33.3333），确保 UI 初始位置正确
        // 注意：百分比是相对总缓存数的百分比（0 ~ 33.3333），实际预载数量由 percent/100 * BitmapCacheMaxCount 计算
        InitializePreloadDefaults();

        // 监听缓存最大数量变化：保持百分比不变，仅在新域内重算整数
        this.WhenAnyValue(v => v.BitmapCacheMaxCount)
            .Subscribe(v =>
            {
                if (_applyRuntimeSettings) BitmapLoader.MaxCacheCount = v;

                _freezePreloadExp = true;
                try
                {
                    // 自动口径下缓存张数不参与运行时，预载上限取与默认一致的固定值
                    var newMax = ShowsCacheLimits ? Math.Max(0, v / 3) : AutomaticPreloadMaximum;
                    PreloadMaximum = newMax;

                    // 根据当前百分比（保持不变）重新计算整数预载数量（相对于新的 BitmapCacheMaxCount）
                    PreloadForwardCount = (int)Math.Round(_preloadForwardPercent / 100.0 * v);
                    PreloadBackwardCount = (int)Math.Round(_preloadBackwardPercent / 100.0 * v);
                    VisibleCenterPreloadCount = (int)Math.Round(_visibleCenterPreloadPercent / 100.0 * v);

                    // 保证范围裁剪（PreloadCount 的 setter 会进行 clamp）
                }
                finally
                {
                    _freezePreloadExp = false;
                }
            });

        // 监听内存上限变化同步到 BitmapLoader；可编辑平台的两档张数与警告都按用户上限立即重算
        this.WhenAnyValue(v => v.BitmapCacheMaxMemory)
            .Subscribe(v =>
            {
                if (_applyRuntimeSettings) BitmapLoader.MaxCacheSize = v * 1024L * 1024L;
                RefreshDeviceCacheCapability();
            });

        // 新增：监听“忽略透明度”设置变化同步到 BitmapLoader
        this.WhenAnyValue(v => v.IgnoreAlpha)
            .Subscribe(v => { if (_applyRuntimeSettings) BitmapLoader.IgnoreAlpha = v; });

        this.WhenAnyValue(v => v.CpuPreloadParallelism)
            .Subscribe(v => { if (_applyRuntimeSettings) HeifLoader.SetCpuDecodeParallelism(v); });

        // 设备内存读数与视口代理尺寸随周期探测变化，探测本身 2s 一轮，这里再限流一次避免提示行抖动。
        _runtimeSubscriptions.Add(Observable
            .FromEvent(h => MemoryGovernor.Probed += h, h => MemoryGovernor.Probed -= h)
            .Throttle(TimeSpan.FromMilliseconds(500), UiDispatcher.MainThreadScheduler)
            .ObserveOn(UiDispatcher.MainThreadScheduler)
            .Subscribe(_ => RefreshDeviceCacheCapability()));
    }
    
    /// <summary>
    /// 全新设置的预载默认值：缓存张数达到基准（30 张）时即默认的上一张 10 / 下一张 5 / 列表滚动 5；
    /// 张数更少时按同一比例收敛到推荐张数，保证不超过预载上限，
    /// 且「前后预载 + 当前 + 对比」不超过缓存张数。
    /// 已保存的设置由 <see cref="SettingsViewModel.Persistence"/> 在之后覆盖，不走这里。
    /// </summary>
    private void InitializePreloadDefaults()
    {
        var count = Math.Max(1, BitmapCacheMaxCount);
        var maximum = ShowsCacheLimits ? Math.Max(0, count / 3) : AutomaticPreloadMaximum;
        PreloadMaximum = maximum;

        _freezePreloadExp = true;
        try
        {
            PreloadForwardCount = ScalePreloadDefault(DefaultPreloadForwardCount);
            PreloadBackwardCount = ScalePreloadDefault(DefaultPreloadBackwardCount);
            VisibleCenterPreloadCount = ScalePreloadDefault(DefaultPreloadCenterCount);

            // 滑条位置按张数换算，与默认份数一致；缓存数量上限变化时继续按百分比保持
            _preloadForwardPercent = PreloadPercent(PreloadForwardCount);
            _preloadBackwardPercent = PreloadPercent(PreloadBackwardCount);
            _visibleCenterPreloadPercent = PreloadPercent(VisibleCenterPreloadCount);
        }
        finally
        {
            _freezePreloadExp = false;
        }

        // 自动口径下缓存张数不参与运行时，份数保持基准默认值。
        int ScalePreloadDefault(int baseline) => !ShowsCacheLimits || count >= DefaultPreloadBaselineCount
            ? Math.Min(baseline, maximum)
            : Math.Clamp(
                (int)Math.Round(baseline * (double)count / DefaultPreloadBaselineCount), 0, maximum);

        double PreloadPercent(int value) => Math.Min(33.3333333, value * 100.0 / count);
    }

    // 指数映射工具：t ∈ [0,1]
    private static double ToExp(double value, double min, double max)
    {
        if (max <= 0) return 0;
        if (min <= 0) min = 1; // 0 需特殊处理，见调用处
        value = Math.Clamp(value, min, max);
        var denom = Math.Log(max / min, 2);
        if (denom == 0) return 0;
        var t = Math.Log(value / min, 2) / denom;
        if (double.IsNaN(t) || double.IsInfinity(t)) return 0;
        return Math.Clamp(t, 0, 1);
    }

    private static int FromExp(double t, double min, double max, bool allowZero = false)
    {
        t = Math.Clamp(t, 0, 1);
        if (allowZero && t <= 0) return 0;
        if (min <= 0) min = 1;
        if (max < min) max = min;
        var v = min * Math.Pow(max / min, t);
        return (int)Math.Round(v);
    }

    /// <summary>
    /// 重新读取设备能力（系统内存读数、平台预算、视口代理长边、两档 Normal 压力每像素字节数），
    /// 按当前工作副本的用户上限（自动平台按设备最大安全容量）重算两档张数，刷新两行文本与级别。
    /// 构造、周期探测与用户上限变化后调用；只发属性通知，不触发展示结构变更。
    /// </summary>
    private void RefreshDeviceCacheCapability()
    {
        _deviceCacheBudgetBytes = MemoryGovernor.BudgetBytes;
        _deviceSafeCapacityBytes = DeviceCacheCapability.DeviceSafeCapacity(
            _deviceCacheBudgetBytes, MemoryGovernor.CautionEnterPercent);

        var capacityBytes = ShowsCacheLimits
            ? BitmapCacheMaxMemory * 1024L * 1024L
            : _deviceSafeCapacityBytes;

        _deviceCacheEstimate = DeviceCacheCapability.Compute(
            MemoryGovernor.GetSystemMemoryInfo(),
            capacityBytes,
            BitmapLoader.ProxyTargetLongEdge,
            BitmapLoader.ProxyBytesPerPixel,
            BitmapLoader.NormalFullBytesPerPixel);

        RefreshCacheLimitWarning();
        this.RaisePropertyChanged(nameof(BitmapCacheCountInfo));
        this.RaisePropertyChanged(nameof(BitmapCacheCountInfoLevel));
    }

    // 冻结标志：缓存数量上限变化时冻结 百分比 更新，保持滑条位置不变
    private bool _freezePreloadExp = false;
    // 预载数量的 百分比 backing 字段（0~33.3333）
    private double _preloadForwardPercent;
    private double _preloadBackwardPercent;
    private double _visibleCenterPreloadPercent;

    private int _bitmapCacheMaxCount = 30;
    public int BitmapCacheMaxCount
    {
        get => _bitmapCacheMaxCount;
        set
        {
            this.RaiseAndSetIfChanged(ref _bitmapCacheMaxCount, Math.Clamp(value,
                Core.Settings.SettingsDocumentCodec.MinimumCacheCount,
                Core.Settings.SettingsDocumentCodec.MaximumCacheCount));
            this.RaisePropertyChanged(nameof(BitmapCacheMaxCountExp));
        }
    }
    // 0~1：1~400 的指数映射
    public double BitmapCacheMaxCountExp
    {
        get => ToExp(BitmapCacheMaxCount, 1, 400);
        set => BitmapCacheMaxCount = FromExp(value, 1, 400);
    }
    
    private int _bitmapCacheMaxMemory = 2048;
    public int BitmapCacheMaxMemory
    {
        get => _bitmapCacheMaxMemory;
        set
        {
            this.RaiseAndSetIfChanged(ref _bitmapCacheMaxMemory, Math.Clamp(value,
                Core.Settings.SettingsDocumentCodec.MinimumCacheMemoryMB,
                Core.Settings.SettingsDocumentCodec.MaximumCacheMemoryMB));
            this.RaisePropertyChanged(nameof(BitmapCacheMaxMemoryExp));
        }
    }
    // 0~1：512~32768 的指数映射
    public double BitmapCacheMaxMemoryExp
    {
        get => ToExp(BitmapCacheMaxMemory, 512, 32768);
        set => BitmapCacheMaxMemory = FromExp(value, 512, 32768);
    }

    private string _performanceBudgetInfo = string.Empty;
    /// <summary>第一行：系统总内存 / 当前可用内存；用户缓存上限超限时在同一行追加简短警告。</summary>
    public string PerformanceBudgetInfo
    {
        get => _performanceBudgetInfo;
        private set => this.RaiseAndSetIfChanged(ref _performanceBudgetInfo, value);
    }

    private CacheLimitWarning _cacheLimitWarning = CacheLimitWarning.Normal;
    /// <summary>第一行的语义级别：只有用户缓存上限超限时才抬级，读数为未知时不误判。</summary>
    public PerfInfoLevel PerformanceBudgetInfoLevel => ToPerfInfoLevel(_cacheLimitWarning.Level);

    /// <summary>
    /// 重算用户缓存上限的超限警告；阈值一律取设备最大安全缓存容量，与按用户上限算出的张数无关。
    /// 自动管理缓存的平台不显示两项上限，也就没有超限警告。
    /// </summary>
    private void RefreshCacheLimitWarning()
    {
        _cacheLimitWarning = ShowsCacheLimits
            ? DeviceCacheCapability.EvaluateLimit(
                BitmapCacheMaxMemory * 1024L * 1024L,
                _deviceCacheEstimate.TotalBytes,
                _deviceCacheEstimate.AvailableBytes,
                _deviceCacheBudgetBytes,
                _deviceSafeCapacityBytes ?? 0)
            : CacheLimitWarning.Normal;

        var systemMemory = DeviceCacheCapability.DescribeSystemMemory(_deviceCacheEstimate);
        PerformanceBudgetInfo = _cacheLimitWarning.Reason is { } reason ? systemMemory + reason : systemMemory;
        this.RaisePropertyChanged(nameof(PerformanceBudgetInfoLevel));
    }

    private static PerfInfoLevel ToPerfInfoLevel(CacheCapabilityLevel level) => level switch
    {
        CacheCapabilityLevel.Error => PerfInfoLevel.Error,
        CacheCapabilityLevel.Warning => PerfInfoLevel.Warning,
        _ => PerfInfoLevel.Normal,
    };

    // 忽略透明度通道
    private bool _ignoreAlpha = false;
    public bool IgnoreAlpha
    {
        get => _ignoreAlpha;
        // 存在内存泄漏，暂停使用
        // set => this.RaiseAndSetIfChanged(ref _ignoreAlpha, value);
    }

    /// <summary>第二行：可编辑平台按用户上限、自动平台按设备最大安全容量算出的两档可缓存张数。</summary>
    public string BitmapCacheCountInfo => DeviceCacheCapability.DescribeCapacity(_deviceCacheEstimate);

    /// <summary>第二行不再按张数定级，级别恒为常规（保留接口供四端接线）。</summary>
    public PerfInfoLevel BitmapCacheCountInfoLevel => PerfInfoLevel.Normal;

    private int _preloadMaximum = 10;
    public int PreloadMaximum
    {
        get => _preloadMaximum;
        set
        {
            this.RaiseAndSetIfChanged(ref _preloadMaximum, value);
            // 域变化时通知 Percent，但由于 Percent getter 返回 backing 字段，冻结期间不会改变滑条位置
            this.RaisePropertyChanged(nameof(PreloadForwardPercent));
            this.RaisePropertyChanged(nameof(PreloadBackwardPercent));
            this.RaisePropertyChanged(nameof(VisibleCenterPreloadPercent));
        }
    }

    private int _preloadForwardCount = 10;
    public int PreloadForwardCount
    {
        get => _preloadForwardCount;
        set
        {
            this.RaiseAndSetIfChanged(ref _preloadForwardCount, Math.Clamp(value, 0, PreloadMaximum));
            this.RaisePropertyChanged(nameof(BitmapCacheCountInfo));
            // 仅在非冻结时，根据整数值反推更新 百分比（用于用户直接改数值或拖动预载滑条）
            if (!_freezePreloadExp)
            {
                // 计算相对于当前 BitmapCacheMaxCount 的百分比，并限定到 33.3333
                _preloadForwardPercent = BitmapCacheMaxCount <= 0 ? 0 : Math.Min(33.3333333, _preloadForwardCount * 100.0 / Math.Max(1, BitmapCacheMaxCount));
                this.RaisePropertyChanged(nameof(PreloadForwardPercent));
            }
        }
    }
    public double PreloadForwardPercent
    {
        get => _preloadForwardPercent;
        set
        {
            var p = Math.Clamp(value, 0.0, 33.3333333);
            _preloadForwardPercent = p; // 保持滑条当前位置
            this.RaisePropertyChanged(nameof(PreloadForwardPercent));

            // 冻结以避免在设置整数时触发反向计算更新 Percent
            _freezePreloadExp = true;
            try
            {
                PreloadForwardCount = (int)Math.Round(p / 100.0 * Math.Max(1, BitmapCacheMaxCount));
            }
            finally
            {
                _freezePreloadExp = false;
            }
        }
    }

    private int _preloadBackwardCount = 5;
    public int PreloadBackwardCount
    {
        get => _preloadBackwardCount;
        set
        {
            this.RaiseAndSetIfChanged(ref _preloadBackwardCount, Math.Clamp(value, 0, PreloadMaximum));
            this.RaisePropertyChanged(nameof(BitmapCacheCountInfo));
            if (!_freezePreloadExp)
            {
                _preloadBackwardPercent = BitmapCacheMaxCount <= 0 ? 0 : Math.Min(33.3333333, _preloadBackwardCount * 100.0 / Math.Max(1, BitmapCacheMaxCount));
                this.RaisePropertyChanged(nameof(PreloadBackwardPercent));
            }
        }
    }
    public double PreloadBackwardPercent
    {
        get => _preloadBackwardPercent;
        set
        {
            var p = Math.Clamp(value, 0.0, 33.3333333);
            _preloadBackwardPercent = p;
            this.RaisePropertyChanged(nameof(PreloadBackwardPercent));

            _freezePreloadExp = true;
            try
            {
                PreloadBackwardCount = (int)Math.Round(p / 100.0 * Math.Max(1, BitmapCacheMaxCount));
            }
            finally
            {
                _freezePreloadExp = false;
            }
        }
    }

    private int _visibleCenterPreloadCount = 5;
    public int VisibleCenterPreloadCount
    {
        get => _visibleCenterPreloadCount;
        set
        {
            this.RaiseAndSetIfChanged(ref _visibleCenterPreloadCount, Math.Clamp(value, 0, PreloadMaximum));
            this.RaisePropertyChanged(nameof(BitmapCacheCountInfo));
            if (!_freezePreloadExp)
            {
                _visibleCenterPreloadPercent = BitmapCacheMaxCount <= 0 ? 0 : Math.Min(33.3333333, _visibleCenterPreloadCount * 100.0 / Math.Max(1, BitmapCacheMaxCount));
                this.RaisePropertyChanged(nameof(VisibleCenterPreloadPercent));
            }
        }
    }
    public double VisibleCenterPreloadPercent
    {
        get => _visibleCenterPreloadPercent;
        set
        {
            var p = Math.Clamp(value, 0.0, 33.3333333);
            _visibleCenterPreloadPercent = p;
            this.RaisePropertyChanged(nameof(VisibleCenterPreloadPercent));

            _freezePreloadExp = true;
            try
            {
                VisibleCenterPreloadCount = (int)Math.Round(p / 100.0 * Math.Max(1, BitmapCacheMaxCount));
            }
            finally
            {
                _freezePreloadExp = false;
            }
        }
    }
    
    private int _visibleCenterDelayMs = 1000;
    public int VisibleCenterDelayMs
    {
        get => _visibleCenterDelayMs;
        set => this.RaiseAndSetIfChanged(
            ref _visibleCenterDelayMs,
            Core.Settings.SettingsDocumentCodec.NormalizeVisibleCenterDelayMs(value));
    }

    private int _nativePreloadParallelism = 8;
    public int NativePreloadParallelism
    {
        get => _nativePreloadParallelism;
        set
        {
            this.RaiseAndSetIfChanged(ref _nativePreloadParallelism, Math.Clamp(value, 1, 32));
            this.RaisePropertyChanged(nameof(NativePreloadParallelismExp));
        }
    }

    public int NativePreloadParallelismMaximum => Math.Max(1, _systemNativePreloadLimit);

    // 0~1：1~32 的指数映射
    public double NativePreloadParallelismExp
    {
        get => ToExp(NativePreloadParallelism, 1, 32);
        set => NativePreloadParallelism = FromExp(value, 1, 32);
    }

    private int _cpuPreloadParallelism = 8;
    public int CpuPreloadParallelism
    {
        get => _cpuPreloadParallelism;
        set
        {
            this.RaiseAndSetIfChanged(ref _cpuPreloadParallelism, Math.Clamp(value, 1, 32));
            this.RaisePropertyChanged(nameof(CpuPreloadParallelismExp));
        }
    }

    public int CpuPreloadParallelismMaximum => Math.Max(1, _systemCpuPreloadLimit);

    // 0~1：1~32 的指数映射
    public double CpuPreloadParallelismExp
    {
        get => ToExp(CpuPreloadParallelism, 1, 32);
        set => CpuPreloadParallelism = FromExp(value, 1, 32);
    }
}
