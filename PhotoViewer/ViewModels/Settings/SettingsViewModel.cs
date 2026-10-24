using System;
using System.Collections.Generic;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Threading.Tasks;
using PhotoViewer.Core.Image;
using PhotoViewer.Core.Platform;
using PhotoViewer.Core.Settings;
using ReactiveUI;

namespace PhotoViewer.ViewModels.Settings;

public partial class SettingsViewModel : ReactiveObject, IDisposable
{
    private readonly bool _applyRuntimeSettings;
    private readonly IViewStateStore _viewState;
    private readonly SettingsRepository? _repository;
    private readonly SettingsSession? _session;
    private readonly CompositeDisposable _runtimeSubscriptions = new();
    private readonly object _saveGate = new();
    private AppSettings? _pendingSnapshot;
    private bool _saveRequested;
    private bool _saveLoopRunning;
    private bool _repositoryReady;
    private bool _hasLocalEdits;
    private bool _isRestoring;
    private bool _initialized;
    private bool _disposed;

    /// <summary>相似聚类面板是否展开（跨会话保留，走 <see cref="IViewStateStore"/>，不进入设置文档）。</summary>
    private bool _similarityPanelExpanded;
    public bool SimilarityPanelExpanded
    {
        get => _similarityPanelExpanded;
        set
        {
            if (!this.RaiseAndSetIfChanged(ref _similarityPanelExpanded, value)) return;
            _viewState.SetBool(SettingsViewStateKeys.SimilarityPanelExpanded, value);
        }
    }

    /// <summary>live 模式下拥有的设置仓库；隔离构造与 draft 模式为 null。</summary>
    public SettingsRepository? Repository => _repository;

    /// <summary>draft 模式下的编辑会话；live 与隔离构造为 null。</summary>
    public SettingsSession? Session => _session;

    /// <summary>不读写文件的隔离构造，供工具与旧验收使用。</summary>
    public SettingsViewModel()
        : this(
            SettingsDefaults.CreateDefault(
                new DefaultPerformanceBudget(),
                OperatingSystem.IsMacOS() || OperatingSystem.IsIOS(),
                BitmapLoader.AutomaticCacheLimit),
            new InMemorySettingsStore(),
            applyRuntimeSettings: false,
            repository: null,
            session: null)
    {
    }

    /// <summary>live 设置：绑定仓库当前快照，编辑后提交，仓库状态变化回填界面。</summary>
    public SettingsViewModel(SettingsRepository repository, IViewStateStore? viewState = null)
        : this(
            repository.Current,
            viewState ?? new InMemorySettingsStore(),
            applyRuntimeSettings: true,
            repository,
            session: null)
    {
        _ = InitializeRepositoryAsync();
    }

    /// <summary>draft 设置：独立工作副本，编辑只更新 <see cref="SettingsSession.Draft"/>，不写盘。</summary>
    public SettingsViewModel(SettingsSession session)
        : this(
            session.Draft,
            new InMemorySettingsStore(),
            applyRuntimeSettings: false,
            repository: null,
            session)
    {
    }

    private SettingsViewModel(
        AppSettings initial,
        IViewStateStore viewState,
        bool applyRuntimeSettings,
        SettingsRepository? repository,
        SettingsSession? session)
    {
        _applyRuntimeSettings = applyRuntimeSettings;
        _viewState = viewState;
        _repository = repository;
        _session = session;
        _similarityPanelExpanded = viewState.GetBool(SettingsViewStateKeys.SimilarityPanelExpanded) ?? false;

        SortScalePreset();
        InitializeLayoutModes();
        InitializeFileFormats();
        InitializeHotkeys();
        InitializeExifDisplay();
        InitializeBitMapCache();
        ApplySnapshot(initial);
        _initialized = true;
        if (_session is not null)
            _session.Draft = CreateSnapshot();

        if (_repository is not null)
        {
            var repositorySource = _repository;
            EventHandler<SettingsRepositoryStateChangedEventArgs> handler = OnRepositoryStateChanged;
            repositorySource.StateChanged += handler;
            _runtimeSubscriptions.Add(Disposable.Create(() => repositorySource.StateChanged -= handler));
        }

        _runtimeSubscriptions.Add(this.Changed
            .Where(args => args.PropertyName is null || PersistedPropertyNames.Contains(args.PropertyName))
            .Subscribe(_ => RequestSave()));
    }

    /// <summary>结束设置生命周期，释放订阅与全局内存通知；不等待在途保存。</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _runtimeSubscriptions.Dispose();
        lock (_saveGate)
        {
            _saveRequested = false;
            _pendingSnapshot = null;
        }
    }

    private async Task InitializeRepositoryAsync()
    {
        var repository = _repository;
        if (repository is null) return;

        try
        {
            await repository.InitializeAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            SetPersistenceStatus($"读取设置失败：{ex.Message}");
            return;
        }

        await InvokeOnUiAsync(() =>
        {
            if (_disposed) return;
            _repositoryReady = true;
            RefreshPersistenceStatus();
            if (_hasLocalEdits)
            {
                _hasLocalEdits = false;
                QueueRepositoryCommit();
            }
        }).ConfigureAwait(false);
    }

    private void OnRepositoryStateChanged(object? sender, SettingsRepositoryStateChangedEventArgs args)
    {
        if (_disposed) return;
        if (UiDispatcher.CheckAccess())
        {
            HandleRepositoryState(args);
            return;
        }

        UiDispatcher.Post(() => HandleRepositoryState(args));
    }

    private void HandleRepositoryState(SettingsRepositoryStateChangedEventArgs args)
    {
        if (_disposed) return;

        _repositoryReady = true;
        RefreshPersistenceStatus();

        if (_hasLocalEdits)
        {
            _hasLocalEdits = false;
            QueueRepositoryCommit();
            return;
        }

        // 本地还有更新的编辑未提交时，不能用较早的仓库快照覆盖界面。
        if (_saveRequested) return;

        if (!CreateSnapshot().ContentEquals(args.Settings))
            ApplySnapshot(args.Settings);
    }

    private static async Task InvokeOnUiAsync(Action action)
    {
        if (UiDispatcher.CheckAccess())
        {
            action();
            return;
        }

        await UiDispatcher.InvokeAsync(action).ConfigureAwait(false);
    }
}
