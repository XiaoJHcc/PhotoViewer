using PhotoViewer.Core.Platform;

namespace PhotoViewer.Core.Settings;

public enum SettingsLoadOutcome
{
    /// <summary>文档不存在，使用默认值；未修改时不会创建文件。</summary>
    Absent,

    /// <summary>正常读取 schemaVersion 2 文档。</summary>
    Loaded,

    /// <summary>文档损坏，已成功备份原始内容并以默认值运行。</summary>
    Recovered,

    /// <summary>读取或备份失败，进入不可写状态，绝不覆盖原文件。</summary>
    Unreadable,

    /// <summary>文档版本高于当前支持版本，进入不可写状态。</summary>
    UnsupportedVersion,

    /// <summary>文档缺失版本或为旧版 schemaVersion 1；不迁移，备份成功后以默认值运行。</summary>
    UnsupportedLegacy,
}

public sealed class SettingsRepositoryStateChangedEventArgs : EventArgs
{
    public SettingsRepositoryStateChangedEventArgs(
        AppSettings settings,
        SettingsLoadOutcome loadOutcome,
        bool canCommit,
        string? corruptBackupPath)
    {
        Settings = settings;
        LoadOutcome = loadOutcome;
        CanCommit = canCommit;
        CorruptBackupPath = corruptBackupPath;
    }

    public AppSettings Settings { get; }

    public SettingsLoadOutcome LoadOutcome { get; }

    public bool CanCommit { get; }

    public string? CorruptBackupPath { get; }
}

/// <summary>
/// 设置文档的唯一写者：初始化读取、归一化提交、保存成功后才更新 <see cref="Current"/>，
/// 失败时保留 last good。未修改的默认设置不会自动创建文件。
/// </summary>
public sealed class SettingsRepository
{
    private readonly ISettingsStore _store;
    private readonly AppSettings _defaults;
    private readonly bool _automaticCacheLimit;
    private readonly SemaphoreSlim _writeGate = new(1, 1);

    private AppSettings _current;
    private SettingsLoadOutcome _loadOutcome = SettingsLoadOutcome.Absent;
    private bool _writable = true;
    private bool _initialized;
    private string? _corruptBackupPath;

    public SettingsRepository(
        ISettingsStore store,
        IPerformanceBudget budget,
        bool appleKeyboard,
        bool automaticCacheLimit = false)
        : this(
            store,
            SettingsDefaults.CreateDefault(budget, appleKeyboard, automaticCacheLimit),
            automaticCacheLimit)
    {
    }

    public SettingsRepository(
        ISettingsStore store,
        AppSettings defaults,
        bool automaticCacheLimit = false)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(defaults);

        _store = store;
        _defaults = defaults;
        _automaticCacheLimit = automaticCacheLimit;
        _current = defaults;
    }

    public AppSettings Current => Volatile.Read(ref _current);

    public SettingsLoadOutcome LoadOutcome => _loadOutcome;

    /// <summary>读取失败、备份失败或文档版本过新时为 false；此时 <see cref="CommitAsync"/> 必定失败。</summary>
    public bool CanCommit => _writable;

    public string? LastLoadError { get; private set; }

    public string? LastSaveError { get; private set; }

    public string? CorruptBackupPath => _corruptBackupPath;

    /// <summary>初始化完成或提交成功后触发；在完成操作的线程上同步触发，调用方负责调度。</summary>
    public event EventHandler<SettingsRepositoryStateChangedEventArgs>? StateChanged;

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        SettingsRepositoryStateChangedEventArgs? state = null;

        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_initialized) return;

            LastLoadError = null;
            var settings = _defaults;
            var outcome = SettingsLoadOutcome.Absent;
            var writable = true;
            string? backupPath = null;

            try
            {
                var loaded = await _store.LoadAsync(cancellationToken).ConfigureAwait(false);
                if (loaded.Status == SettingsStoreLoadStatus.Content)
                {
                    var read = SettingsDocumentCodec.Read(
                        loaded.Content ?? string.Empty,
                        _defaults,
                        _automaticCacheLimit);

                    switch (read.Status)
                    {
                        case SettingsDocumentReadStatus.Loaded:
                            settings = read.Settings ?? _defaults;
                            outcome = SettingsLoadOutcome.Loaded;
                            break;

                        case SettingsDocumentReadStatus.UnsupportedVersion:
                            outcome = SettingsLoadOutcome.UnsupportedVersion;
                            writable = false;
                            LastLoadError = $"设置文件版本 {read.Version} 高于当前支持版本，已忽略";
                            break;

                        case SettingsDocumentReadStatus.UnsupportedLegacy:
                            outcome = SettingsLoadOutcome.UnsupportedLegacy;
                            backupPath = await TryBackupAsync(cancellationToken).ConfigureAwait(false);
                            writable = backupPath is not null;
                            if (writable)
                            {
                                LastLoadError = $"旧版设置已备份为 {backupPath}，已恢复默认设置";
                            }
                            else
                            {
                                LastLoadError = "旧版设置文件未能备份，已进入不可写状态";
                            }
                            break;

                        case SettingsDocumentReadStatus.Corrupted:
                            backupPath = await TryBackupAsync(cancellationToken).ConfigureAwait(false);
                            if (backupPath is null)
                            {
                                outcome = SettingsLoadOutcome.Unreadable;
                                writable = false;
                                LastLoadError = "设置文件已损坏，未能备份，已进入不可写状态";
                            }
                            else
                            {
                                outcome = SettingsLoadOutcome.Recovered;
                                LastLoadError = $"设置文件已损坏，已备份为 {backupPath} 并恢复默认设置";
                            }
                            break;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                outcome = SettingsLoadOutcome.Unreadable;
                writable = false;
                LastLoadError = $"读取设置失败：{ex.Message}";
            }

            _current = settings;
            _loadOutcome = outcome;
            _writable = writable;
            _corruptBackupPath = backupPath;
            _initialized = true;
            state = SnapshotState();
        }
        finally
        {
            _writeGate.Release();
        }

        if (state is not null)
            StateChanged?.Invoke(this, state);
    }

    /// <summary>提交快照。成功才更新 <see cref="Current"/>；未修改时不写文件。</summary>
    /// <returns>写入成功或无需写入时为 true；不可写或保存失败时为 false。</returns>
    public async Task<bool> CommitAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);

        if (!_initialized)
            await InitializeAsync(cancellationToken).ConfigureAwait(false);

        SettingsRepositoryStateChangedEventArgs? state = null;
        var success = false;

        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_writable)
            {
                LastSaveError = "设置处于不可写状态，未保存";
                return false;
            }

            var normalized = SettingsDocumentCodec.Normalize(settings, _automaticCacheLimit);
            if (normalized.ContentEquals(_current))
                return true;

            try
            {
                var json = SettingsDocumentCodec.Serialize(normalized, _automaticCacheLimit);
                await _store.SaveAsync(json, cancellationToken).ConfigureAwait(false);
                _current = normalized;
                LastSaveError = null;
                success = true;
                state = SnapshotState();
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                LastSaveError = ex.Message;
            }
        }
        finally
        {
            _writeGate.Release();
        }

        if (state is not null)
            StateChanged?.Invoke(this, state);

        return success;
    }

    /// <summary>只等待在途提交完成，不触发新的写入。</summary>
    public async Task FlushAsync(CancellationToken cancellationToken = default)
    {
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        _writeGate.Release();
    }

    /// <summary>创建持有独立 draft 的设置会话；会话不自动保存。</summary>
    public SettingsSession BeginSession()
        => new(this, Current);

    private async Task<string?> TryBackupAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await _store.BackupCorruptAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            LastLoadError = $"备份设置文件失败：{ex.Message}";
            return null;
        }
    }

    private SettingsRepositoryStateChangedEventArgs SnapshotState()
        => new(_current, _loadOutcome, _writable, _corruptBackupPath);
}
