using System;
using ReactiveUI;
using PhotoViewer.Core;

namespace PhotoViewer.ViewModels;

public partial class SettingsViewModel
{
    ///////////////////
    /// 位图缓存与预取设置
    ///////////////////

    /// <summary>
    /// 初始化数据（缓存数量、内存大小、预载数量）
    /// 初始化监听（缓存数量、内存大小）
    /// </summary>
    private void InitializeBitMapCache()
    {
        // 读取系统内存上限，设置默认值
        try
        {
            var systemMemoryLimit = MemoryBudget.AppMemoryLimitMB;

            // 设置默认内存上限为系统限制的 50%，但不超过 4GB
            var defaultMemory = Math.Min(systemMemoryLimit * 1 / 2, 4096);
            BitmapCacheMaxMemory = Math.Max(512, defaultMemory);

            if (BitmapCacheMaxMemory < 4096)
            {
                BitmapCacheMaxCount = BitmapCacheMaxMemory / 132; // 以 33MP 估算张数上限
                PreloadParallelism = (int)(BitmapCacheMaxMemory / 4096.0 * 8.0); // 同比减少线程数
            }

            MemoryBudgetInfo = $"设备内存上限: {systemMemoryLimit} MB";
            if (IsIOS)
            {
                MemoryBudgetInfo += "\niOS 内存限制更加严格，如遇闪退请调小限值";
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to initialize memory budget: {ex.Message}");
            BitmapCacheMaxMemory = 2048;
            MemoryBudgetInfo = "设备内存上限: 未知";
        }
        
        // 初始化三个预载滑条的 百分比 backing 字段（0~33.3333），确保 UI 初始位置正确
        // 注意：百分比是相对总缓存数的百分比（0 ~ 33.3333），实际预载数量由 percent/100 * BitmapCacheMaxCount 计算
        _preloadForwardPercent = BitmapCacheMaxCount <= 0 ? 0 : Math.Min(33.3333333, _preloadForwardCount * 100.0 / Math.Max(1, BitmapCacheMaxCount));
        _preloadBackwardPercent = BitmapCacheMaxCount <= 0 ? 0 : Math.Min(33.3333333, _preloadBackwardCount * 100.0 / Math.Max(1, BitmapCacheMaxCount));
        _visibleCenterPreloadPercent = BitmapCacheMaxCount <= 0 ? 0 : Math.Min(33.3333333, _visibleCenterPreloadCount * 100.0 / Math.Max(1, BitmapCacheMaxCount));

        // 监听缓存最大数量变化：保持百分比不变，仅在新域内重算整数
        this.WhenAnyValue(v => v.BitmapCacheMaxCount)
            .Subscribe(v =>
            {
                BitmapLoader.MaxCacheCount = v;

                _freezePreloadExp = true;
                try
                {
                    var newMax = Math.Max(0, v / 3);
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
                BitmapLoader.MaxCacheSize = v * 1024L * 1024L;
                
                // 计算当前内存能够满足多少张照片
                BitmapCacheCountInfo = "当前内存设置下可缓存的照片数量上限: \n24MP < " + v/(24*4) + " 张，33MP < " + v/(33*4) + " 张，42MP < " + v/(42*4) + " 张，61MP < " + v/(61*4) + " 张";
            });
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
            this.RaiseAndSetIfChanged(ref _bitmapCacheMaxMemory, Math.Max(256, value));
            this.RaisePropertyChanged(nameof(BitmapCacheMaxMemoryExp));
        }
    }
    // 0~1：256~32768 的指数映射
    public double BitmapCacheMaxMemoryExp
    {
        get => ToExp(BitmapCacheMaxMemory, 256, 32768);
        set => BitmapCacheMaxMemory = FromExp(value, 256, 32768);
    }

    private string _memoryBudgetInfo = string.Empty;
    public string MemoryBudgetInfo
    {
        get => _memoryBudgetInfo;
        private set => this.RaiseAndSetIfChanged(ref _memoryBudgetInfo, value);
    }
    
    private string _bitmapCacheCountInfo = string.Empty;
    public string BitmapCacheCountInfo
    {
        get => _bitmapCacheCountInfo;
        private set => this.RaiseAndSetIfChanged(ref _bitmapCacheCountInfo, value);
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
    
    private int _preloadParallelism = 8;
    public int PreloadParallelism
    {
        get => _preloadParallelism;
        set
        {
            this.RaiseAndSetIfChanged(ref _preloadParallelism, Math.Clamp(value, 1, 32));
            this.RaisePropertyChanged(nameof(PreloadParallelismExp));
        }
    }
    // 0~1：1~32 的指数映射
    public double PreloadParallelismExp
    {
        get => ToExp(PreloadParallelism, 1, 32);
        set => PreloadParallelism = FromExp(value, 1, 32);
    }
}
