namespace PhotoViewer.Core.Settings;

/// <summary>进程内界面状态实现，用于隔离构造与没有平台偏好实现的场景。</summary>
public sealed class InMemorySettingsStore : IViewStateStore
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, bool> _values =
        new(StringComparer.Ordinal);

    public bool? GetBool(string key)
        => _values.TryGetValue(key, out var value) ? value : null;

    public void SetBool(string key, bool value) => _values[key] = value;
}
