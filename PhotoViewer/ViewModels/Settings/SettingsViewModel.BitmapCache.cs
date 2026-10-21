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

    private int _systemMemoryLimitMB; // 记录设备上限，便于事件到来时重建 UI 文本
    private int _systemCpuCoreCount = 1;
    private int _systemNativePreloadLimit = 8;
    private int _systemCpuPreloadLimit = 8;

    /// <summary>自动口径下（缓存张数不参与运行时）的预载数量上限，与默认预载数量一致。</summary>
    private const int AutomaticPreloadMaximum = 10;

    /// <summary>按适屏尺寸估算照片字节数时的代理长边兜底（展示会话尚未上报视口时）。</summary>
    private const int UnknownProxyLongEdge = 3840;

    /// <summary>单张照片按多少像素估算（照片本身小于代理目标时按此上限）。</summary>
    private const long EstimatedPhotoPixelCap = 24_000_000L;

    /// <summary>
    /// 是否显示并允许编辑「缓存内存上限 / 缓存数量上限」。
    /// 缓存容量由实测预算自动管理的平台（iPad）不显示这两项，用户字段仍保留。
    /// </summary>
    public bool ShowsCacheLimits => !BitmapLoader.AutomaticCacheLimit;

    /// <summary>
    /// 提示与超限判定共用的内存上限（MB）：直接取缓存容量推导所用的内存压力预算，
    /// 保证"提示显示的上限 = 推导缓存所用的预算"；预算源未就绪时回退平台上报值。
    /// </summary>
    private int MemoryLimitMB
    {
        get
        {
            int budgetMB = (int)Math.Min(int.MaxValue, MemoryGovernor.BudgetBytes / (1024 * 1024));
            return budgetMB > 0 ? budgetMB : _systemMemoryLimitMB;
        }
    }

    /// <summary>
    /// 初始化数据（缓存数量、内存大小、预载数量）
    /// 初始化监听（缓存数量、内存大小）
    /// </summary>
    private void InitializeBitMapCache()
    {
        // 读取系统内存上限，设置默认值
        try
        {
            var systemMemoryLimit = PerformanceBudget.AppMemoryLimitMB;
            _systemMemoryLimitMB = systemMemoryLimit;
            _systemCpuCoreCount = Math.Max(1, PerformanceBudget.CpuCoreCount);
            _systemNativePreloadLimit = Math.Max(1, PerformanceBudget.NativePreloadThreadLimit);
            _systemCpuPreloadLimit = Math.Max(1, PerformanceBudget.CpuPreloadThreadLimit);

            // 设置默认内存上限为系统限制的 50%，但不超过 4GB
            var defaultMemory = Math.Min(systemMemoryLimit * 1 / 2, 4096);
            BitmapCacheMaxMemory = Math.Max(512, defaultMemory);

            if (BitmapCacheMaxMemory < 4096)
            {
                BitmapCacheMaxCount = Math.Min(BitmapCacheMaxMemory / 132, 30); // 按 33MP 估算减少张数
                NativePreloadParallelism = EstimateInitialPreloadParallelism(_systemNativePreloadLimit);
                CpuPreloadParallelism = EstimateInitialPreloadParallelism(_systemCpuPreloadLimit);
            }
            else
            {
                NativePreloadParallelism = _systemNativePreloadLimit;
                CpuPreloadParallelism = _systemCpuPreloadLimit;
            }

            PerformanceBudgetInfo = BuildPerformanceBudgetInfo();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to initialize performance budget: {ex.Message}");
            BitmapCacheMaxMemory = 2048;
            _systemMemoryLimitMB = 0;
            _systemCpuCoreCount = Math.Max(1, Environment.ProcessorCount);
            _systemNativePreloadLimit = Math.Min(_systemCpuCoreCount, 8);
            _systemCpuPreloadLimit = Math.Min(_systemCpuCoreCount, 4);
            NativePreloadParallelism = _systemNativePreloadLimit;
            CpuPreloadParallelism = Math.Min(_systemCpuPreloadLimit, 1);
            PerformanceBudgetInfo = "设备性能: 未知";
        }

        // 订阅内存告警事件：仅显示触发时快照（大小、数量、时间）
        _runtimeSubscriptions.Add(MessageBus.Current
            .Listen<BitmapLoader.MemoryWarningEvent>()
            .ObserveOn(UiDispatcher.MainThreadScheduler)
            .Subscribe(evt =>
            {
                PerformanceBudgetInfo =
                    $"{BuildPerformanceBudgetInfo()} / " +
                    $"上次系统内存告警：缓存 {evt.sizeMB} MB, {evt.count} 项 @ {evt.time:HH:mm:ss}";
            }));

        // 初始化三个预载滑条的 百分比 backing 字段（0~33.3333），确保 UI 初始位置正确
        // 注意：百分比是相对总缓存数的百分比（0 ~ 33.3333），实际预载数量由 percent/100 * BitmapCacheMaxCount 计算
        _preloadForwardPercent = BitmapCacheMaxCount <= 0 ? 0 : Math.Min(33.3333333, _preloadForwardCount * 100.0 / Math.Max(1, BitmapCacheMaxCount));
        _preloadBackwardPercent = BitmapCacheMaxCount <= 0 ? 0 : Math.Min(33.3333333, _preloadBackwardCount * 100.0 / Math.Max(1, BitmapCacheMaxCount));
        _visibleCenterPreloadPercent = BitmapCacheMaxCount <= 0 ? 0 : Math.Min(33.3333333, _visibleCenterPreloadCount * 100.0 / Math.Max(1, BitmapCacheMaxCount));

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
        
        // 监听内存上限变化同步到 BitmapLoader
        this.WhenAnyValue(v => v.BitmapCacheMaxMemory)
            .Subscribe(v =>
            {
                if (_applyRuntimeSettings) BitmapLoader.MaxCacheSize = v * 1024L * 1024L;
                
                // 更新内存信息颜色
                this.RaisePropertyChanged(nameof(PerformanceBudgetInfoLevel));
                
                // 计算当前内存能够满足多少张照片
                this.RaisePropertyChanged(nameof(BitmapCacheCountInfo));
            });

        // 新增：监听“忽略透明度”设置变化同步到 BitmapLoader
        this.WhenAnyValue(v => v.IgnoreAlpha)
            .Subscribe(v =>
            {
                if (_applyRuntimeSettings) BitmapLoader.IgnoreAlpha = v;
                // 计算当前内存能够满足多少张照片
                this.RaisePropertyChanged(nameof(BitmapCacheCountInfo));
            });

        this.WhenAnyValue(v => v.CpuPreloadParallelism)
            .Subscribe(v => { if (_applyRuntimeSettings) HeifLoader.SetCpuDecodeParallelism(v); });
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

    private int EstimateInitialPreloadParallelism(int maxParallelism)
    {
        if (maxParallelism <= 1)
        {
            return 1;
        }

        var scaled = (int)Math.Round(BitmapCacheMaxMemory / 4096.0 * maxParallelism);
        return Math.Clamp(scaled, 1, maxParallelism);
    }

    private string BuildPerformanceBudgetInfo()
        => $"设备性能: CPU {_systemCpuCoreCount} 核 / 内存 {MemoryLimitMB} MB";

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
            this.RaiseAndSetIfChanged(ref _bitmapCacheMaxCount, value < 1 ? 1 : value);
            this.RaisePropertyChanged(nameof(BitmapCacheMaxCountExp));
            this.RaisePropertyChanged(nameof(BitmapCacheCountInfo));
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
            this.RaiseAndSetIfChanged(ref _bitmapCacheMaxMemory, Math.Max(512, value));
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
    public string PerformanceBudgetInfo
    {
        get => _performanceBudgetInfo;
        private set => this.RaiseAndSetIfChanged(ref _performanceBudgetInfo, value);
    }
    
    public PerfInfoLevel PerformanceBudgetInfoLevel
    {
        get
        {
            // 自动口径下用户上限不参与运行时，超限判定没有意义
            if (!ShowsCacheLimits || _systemMemoryLimitMB <= 0)
                return PerfInfoLevel.Normal;

            if (BitmapCacheMaxMemory > MemoryLimitMB)
                return PerfInfoLevel.Error;
            else if (BitmapCacheMaxMemory > MemoryLimitMB * 0.51)
                return PerfInfoLevel.Warning;
            else
                return PerfInfoLevel.Normal;
        }
    }
    
    // 忽略透明度通道
    private bool _ignoreAlpha = false;
    public bool IgnoreAlpha
    {
        get => _ignoreAlpha;
        // 存在内存泄漏，暂停使用
        // set => this.RaiseAndSetIfChanged(ref _ignoreAlpha, value);
    }

    /// <summary>实际缓存容量（字节）：用户上限与按预算推导的上限取较小者；自动口径下只取推导上限。</summary>
    private long CacheCapacityBytes()
    {
        long userBytes = BitmapCacheMaxMemory * 1024L * 1024L;
        long derivedBytes = BitmapLoader.EffectiveCacheCapacityBytes;
        if (derivedBytes <= 0) return userBytes;
        return ShowsCacheLimits ? Math.Min(userBytes, derivedBytes) : derivedBytes;
    }

    /// <summary>单张适屏照片的字节数：代理长边按 3:2 取像素，照片本身小于代理目标时按 2400 万像素估算。</summary>
    private static long ProxyPhotoBytes()
    {
        int longEdge = BitmapLoader.ProxyTargetLongEdge > 0 ? BitmapLoader.ProxyTargetLongEdge : UnknownProxyLongEdge;
        long pixels = Math.Min((long)longEdge * longEdge * 2 / 3, EstimatedPhotoPixelCap);
        return pixels * BitmapLoader.ProxyBytesPerPixel;
    }

    public string BitmapCacheCountInfo
    {
        get
        {
            long photoBytes = ProxyPhotoBytes();
            long count = photoBytes > 0 ? CacheCapacityBytes() / photoBytes : 0;
            if (ShowsCacheLimits) count = Math.Min(count, BitmapCacheMaxCount);
            int needed = PreloadForwardCount + PreloadBackwardCount + 2;

            if (count < 3)
            {
                BitmapCacheCountInfoLevel = PerfInfoLevel.Error;
                return "⚠ 可缓存不足 3 张适屏照片";
            }
            if (count < needed)
            {
                BitmapCacheCountInfoLevel = PerfInfoLevel.Warning;
                return $"⚠ 可缓存约 {count} 张适屏照片，少于预载与对比所需的 {needed} 张";
            }
            BitmapCacheCountInfoLevel = PerfInfoLevel.Normal;
            return $"✓ 可缓存约 {count} 张适屏照片（预载与对比需 {needed} 张）";
        }
    }
    private PerfInfoLevel _bitmapCacheCountInfoLevel = PerfInfoLevel.Normal;
    public PerfInfoLevel BitmapCacheCountInfoLevel
    {
        get => _bitmapCacheCountInfoLevel;
        private set => this.RaiseAndSetIfChanged(ref _bitmapCacheCountInfoLevel, value);
    }

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
        set
        {
            this.RaiseAndSetIfChanged(ref _visibleCenterDelayMs, Math.Clamp(value, 100, 5000));
            this.RaisePropertyChanged(nameof(VisibleCenterDelayExp));
        }
    }
    // 0~1：100~5000 的指数映射
    public double VisibleCenterDelayExp
    {
        get => ToExp(VisibleCenterDelayMs, 100, 5000);
        set => VisibleCenterDelayMs = FromExp(value, 100, 5000);
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
