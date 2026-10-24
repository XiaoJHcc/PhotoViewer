namespace PhotoViewer.Core.Settings;

public enum SettingsStoreLoadStatus
{
    /// <summary>文档不存在。</summary>
    Absent,

    /// <summary>读取到原始 JSON 文本。</summary>
    Content,
}

public readonly record struct SettingsStoreLoadResult(SettingsStoreLoadStatus Status, string? Content)
{
    public static SettingsStoreLoadResult Absent { get; } = new(SettingsStoreLoadStatus.Absent, null);

    public static SettingsStoreLoadResult FromContent(string content)
        => new(SettingsStoreLoadStatus.Content, content);
}

/// <summary>
/// 设置文档的原始文本读写。解析、损坏分类与备份时机由 <see cref="SettingsRepository"/> 决定，
/// 存储实现只负责 IO；读取失败向上抛出。
/// </summary>
public interface ISettingsStore
{
    Task<SettingsStoreLoadResult> LoadAsync(CancellationToken cancellationToken = default);

    Task SaveAsync(string json, CancellationToken cancellationToken = default);

    /// <summary>把当前文档另存为唯一备份并返回位置；没有可备份内容或备份失败时抛出异常。</summary>
    Task<string?> BackupCorruptAsync(CancellationToken cancellationToken = default);
}
