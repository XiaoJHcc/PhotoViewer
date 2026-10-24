namespace PhotoViewer.Core.Settings;

/// <summary>
/// 设置编辑会话：持有独立 draft，关闭时一次性提交；无自动保存、无 Rx、不依赖 ViewModel。
/// 重复调用 <see cref="CommitAsync"/> 在首次成功后不再写盘。
/// </summary>
public sealed class SettingsSession
{
    private readonly SettingsRepository _repository;
    private readonly SemaphoreSlim _commitGate = new(1, 1);
    private AppSettings _draft;
    private bool _committed;

    public SettingsSession(SettingsRepository repository, AppSettings draft)
    {
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentNullException.ThrowIfNull(draft);

        _repository = repository;
        _draft = draft;
    }

    /// <summary>会话私有的工作副本；赋值必须是不可变快照。</summary>
    public AppSettings Draft
    {
        get => _draft;
        set => _draft = value ?? throw new ArgumentNullException(nameof(value));
    }

    /// <summary>一次性提交 draft。首次成功后重复调用直接返回 true，不再触发写入。</summary>
    public async Task<bool> CommitAsync(CancellationToken cancellationToken = default)
    {
        await _commitGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_committed) return true;

            var success = await _repository.CommitAsync(_draft, cancellationToken).ConfigureAwait(false);
            if (success) _committed = true;
            return success;
        }
        finally
        {
            _commitGate.Release();
        }
    }
}
