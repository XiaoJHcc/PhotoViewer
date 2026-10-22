using System;
using System.Linq;
using System.Reactive;
using ReactiveUI;
using PhotoViewer.Core;
using PhotoViewer.Core.Settings;

namespace PhotoViewer.ViewModels.Settings;

public partial class SettingsViewModel : ReactiveObject, IDisposable
{
    private readonly bool _applyRuntimeSettings;
    private readonly System.Reactive.Disposables.CompositeDisposable _runtimeSubscriptions = new();
    private readonly ISettingsService _settingsService;
    private readonly System.Reactive.Subjects.Subject<Unit> _saveRequests = new();
    private readonly IDisposable _saveSubscription;
    private bool _isRestoring;
    private bool _hasLoaded;
    private bool _disposed;

    /// <summary>相似聚类面板是否展开（跨会话保留）。</summary>
    private bool _similarityPanelExpanded;
    public bool SimilarityPanelExpanded
    {
        get => _similarityPanelExpanded;
        set => this.RaiseAndSetIfChanged(ref _similarityPanelExpanded, value);
    }

    // 检查是否为安卓平台
    public static bool IsAndroid => OperatingSystem.IsAndroid();
    
    // 检查是否为 iOS 平台
    public static bool IsIOS => OperatingSystem.IsIOS();
    
    // 检查是否为 Apple 平台
    public static bool IsApple => OperatingSystem.IsIOS() || OperatingSystem.IsMacOS();
    
    /// <summary>初始化设置；原生工作副本可关闭运行时副作用，源设置仍按原有规则应用。</summary>
    public SettingsViewModel(ISettingsService? settingsService = null, SettingsModel? initialModel = null, bool applyRuntimeSettings = true)
    {
        _applyRuntimeSettings = applyRuntimeSettings;
        _settingsService = settingsService ?? SettingsService.Instance;
        _saveSubscription = InitializePersistence();
        // 设置 UI 初始化
        SortScalePreset();
        InitializeLayoutModes();
        
        // 设置排序列表数据初始化
        InitializeFileFormats();
        InitializeHotkeys();
        InitializeExifDisplay();
        
        // 设置缓存数据初始化
        InitializeBitMapCache();

        if (initialModel != null)
        {
            ApplyModel(initialModel, preserveDefaultValues: false);
            _hasLoaded = true;
        }
        else
        {
            _ = LoadSettingsAsync();
        }
        GC.KeepAlive(_saveSubscription); // keep subscription rooted
     }

    /// <summary>结束设置生命周期，释放全局消息订阅与延迟保存，防止原生工作副本长期驻留。</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _saveSubscription.Dispose();
        _runtimeSubscriptions.Dispose();
        _saveRequests.Dispose();
    }
  }
