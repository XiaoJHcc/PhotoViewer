namespace PhotoViewer.Core.Settings;

public sealed class InMemorySettingsDocumentStore : ISettingsStore
{
    private string? _content;
    private string? _backup;

    public Task<SettingsStoreLoadResult> LoadAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(_content == null
            ? SettingsStoreLoadResult.Absent
            : SettingsStoreLoadResult.FromContent(_content));
    }

    public Task SaveAsync(string json, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _content = json;
        return Task.CompletedTask;
    }

    public Task<string?> BackupCorruptAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_content == null) return Task.FromResult<string?>(null);
        _backup = _content;
        _content = null;
        return Task.FromResult<string?>("memory");
    }
}
