using System;
using System.Threading;
using System.Threading.Tasks;
using PhotoViewer.ViewModels.Settings;

namespace PhotoViewer.Core.Settings;

/// <summary>原生设置共用工作副本：编辑独立 draft，关闭时提交一次。</summary>
public sealed class NativeSettingsSession : IDisposable
{
    private readonly SettingsViewModel _source;
    private readonly SettingsSession _session;

    /// <summary>可编辑的设置工作副本。</summary>
    public SettingsViewModel WorkingSettings { get; }

    /// <summary>从 live 设置建立 draft 会话。</summary>
    public NativeSettingsSession(SettingsViewModel source)
    {
        ArgumentNullException.ThrowIfNull(source);
        var repository = source.Repository
            ?? throw new InvalidOperationException("原生设置会话需要 live SettingsViewModel");

        _source = source;
        _session = repository.BeginSession();
        WorkingSettings = new SettingsViewModel(_session);
    }

    /// <summary>把 draft 提交到仓库；重复调用幂等，并刷新源设置的持久化状态。</summary>
    public async Task<bool> ApplyAsync(CancellationToken cancellationToken = default)
    {
        var success = await _session.CommitAsync(cancellationToken).ConfigureAwait(false);
        _source.RefreshPersistenceStatus();
        return success;
    }

    /// <summary>结束工作副本生命周期；提交由 <see cref="ApplyAsync"/> 明确触发。</summary>
    public void Dispose() => WorkingSettings.Dispose();
}
