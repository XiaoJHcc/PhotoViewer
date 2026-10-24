using System.Globalization;
using Foundation;
using PhotoViewer.Core.Settings;

namespace PhotoViewer.Apple;

/// <summary>
/// macOS / iOS 用户偏好设置存储：整个文档以原始 JSON 文本存在单个 NSUserDefaults 键里，
/// 解析、损坏分类与写回时机归 <see cref="SettingsRepository"/>。
/// 损坏文档另存为带时间戳的独立偏好键并保留原文，备份键只保留最近 <see cref="MaxBackups"/> 份。
/// </summary>
public sealed class AppleSettingsStore : ISettingsStore
{
    /// <summary>生产设置文档键；与历史版本同名，旧文档按版本分类处理。</summary>
    public const string DefaultKey = "cc.xiaojh.PhotoViewer.Settings.json";

    /// <summary>保留的损坏备份份数。</summary>
    public const int MaxBackups = 3;

    /// <summary>备份键时间戳段长度（yyyyMMdd-HHmmss）。</summary>
    private const int StampLength = 15;

    private readonly string _key;

    public AppleSettingsStore(string key = DefaultKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        _key = key;
    }

    /// <summary>设置文档所在的偏好键。</summary>
    public string Key => _key;

    public Task<SettingsStoreLoadResult> LoadAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.Run(() =>
        {
            string? json = NSUserDefaults.StandardUserDefaults.StringForKey(_key);
            return string.IsNullOrWhiteSpace(json)
                ? SettingsStoreLoadResult.Absent
                : SettingsStoreLoadResult.FromContent(json);
        }, cancellationToken);
    }

    public Task SaveAsync(string json, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(json);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.Run(() =>
        {
            var defaults = NSUserDefaults.StandardUserDefaults;
            defaults.SetString(json, _key);
            if (!defaults.Synchronize())
                throw new IOException($"设置写入偏好未完成：{_key}");
        }, cancellationToken);
    }

    public Task<string?> BackupCorruptAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.Run<string?>(() =>
        {
            var defaults = NSUserDefaults.StandardUserDefaults;
            string? json = defaults.StringForKey(_key);
            if (string.IsNullOrEmpty(json))
                throw new InvalidOperationException($"没有可备份的设置内容：{_key}");

            string backupKey = BuildBackupKey();
            // 先腾出位置再写入：新备份绝不会在同一轮里被自己的裁剪删掉。
            TrimBackups(defaults, MaxBackups - 1);
            defaults.SetString(json, backupKey);
            if (!defaults.Synchronize())
                throw new IOException($"设置备份写入偏好未完成：{backupKey}");
            return backupKey;
        }, cancellationToken);
    }

    /// <summary>时间戳 + 随机后缀：即使同一秒内重复备份、或旧同名键已被裁剪，也不会复用键名。</summary>
    private string BuildBackupKey()
    {
        string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        return $"{_key}.corrupt-{stamp}-{Guid.NewGuid():N}";
    }

    /// <summary>删除最早的备份，使保留份数不超过 <paramref name="keep"/>；时间戳段定宽，按序即时间序。</summary>
    private void TrimBackups(NSUserDefaults defaults, int keep)
    {
        string prefix = $"{_key}.corrupt-";
        var backups = defaults.ToDictionary().Keys
            .Select(key => key?.ToString())
            .OfType<string>()
            .Where(key => key.StartsWith(prefix, StringComparison.Ordinal))
            .OrderBy(key => key.Length >= prefix.Length + StampLength
                ? key.Substring(prefix.Length, StampLength)
                : string.Empty, StringComparer.Ordinal)
            .ThenBy(key => key, StringComparer.Ordinal)
            .ToArray();
        foreach (string stale in backups.Take(Math.Max(0, backups.Length - keep)))
            defaults.RemoveObject(stale);
    }
}
