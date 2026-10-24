using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Android.App;
using Android.Content;
using PhotoViewer.Core.Settings;

namespace PhotoViewer.Android.Core;

/// <summary>
/// Android SharedPreferences 的原始设置文档存储（schemaVersion 2 JSON 文本）。
/// 解析、版本分类与备份时机由共享 <see cref="SettingsRepository"/> 决定，本类型只负责 IO。
/// 写入在后台线程用 <c>editor.Commit()</c> 并检查返回值（不使用无条件成功的 <c>Apply()</c>）。
/// </summary>
public sealed class AndroidSettingsStorage : ISettingsStore
{
    private const string PreferenceName = "cc.xiaojh.PhotoViewer.Settings";
    private const string SettingsKey = "settings_json";
    private const string CorruptBackupPrefix = SettingsKey + ".corrupt-";
    private const int MaximumCorruptBackups = 3;

    private static string _preferenceName = PreferenceName;

    /// <summary>
    /// 切到隔离档：压测/验收换用独立 SharedPreferences，不读也不覆盖用户设置，每轮从默认值起跑。
    /// 必须在设置仓库初始化前调用。
    /// </summary>
    /// <param name="profile">档名（如 <c>stress</c>）；空值恢复用户档。</param>
    public static void UseIsolatedProfile(string profile)
        => _preferenceName = string.IsNullOrWhiteSpace(profile) ? PreferenceName : PreferenceName + "." + profile;

    /// <summary>读取原始 JSON 文本；文档缺失或为空视为未创建。</summary>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>未创建或原始内容。</returns>
    public Task<SettingsStoreLoadResult> LoadAsync(CancellationToken cancellationToken = default)
    {
        return Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            string? json = Preferences().GetString(SettingsKey, null);
            return string.IsNullOrWhiteSpace(json)
                ? SettingsStoreLoadResult.Absent
                : SettingsStoreLoadResult.FromContent(json!);
        }, cancellationToken);
    }

    /// <summary>在后台提交完整 JSON 文档；<c>Commit</c> 返回 false 时抛 <see cref="IOException"/>。</summary>
    /// <param name="json">完整设置文档。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>写入任务。</returns>
    public Task SaveAsync(string json, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(json);
        return Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var editor = Preferences().Edit() ?? throw new IOException("无法编辑设置偏好存储");
            editor.PutString(SettingsKey, json);
            if (!editor.Commit()) throw new IOException("设置偏好提交失败");
        }, cancellationToken);
    }

    /// <summary>
    /// 把当前原始内容另存为唯一时间戳偏好键（最多保留 3 份），成功后移除原文档避免重复恢复。
    /// 没有可备份内容或提交失败时抛异常，由 <see cref="SettingsRepository"/> 转入不可写状态。
    /// </summary>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>备份偏好键名。</returns>
    public Task<string?> BackupCorruptAsync(CancellationToken cancellationToken = default)
    {
        return Task.Run<string?>(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var prefs = Preferences();
            string? json = prefs.GetString(SettingsKey, null);
            if (string.IsNullOrEmpty(json)) throw new IOException("没有可备份的设置内容");

            var existing = CorruptBackupKeys(prefs);
            string backupKey = BuildBackupKey(prefs);

            using var editor = prefs.Edit() ?? throw new IOException("无法编辑设置偏好存储");
            editor.PutString(backupKey, json);
            editor.Remove(SettingsKey);
            int keepExisting = MaximumCorruptBackups - 1;
            for (int index = 0; index < existing.Count - keepExisting; index++)
                editor.Remove(existing[index]);
            if (!editor.Commit()) throw new IOException("设置损坏备份提交失败");

            return backupKey;
        }, cancellationToken);
    }

    private static ISharedPreferences Preferences()
        => Application.Context.GetSharedPreferences(_preferenceName, FileCreationMode.Private)
           ?? throw new InvalidOperationException("无法打开设置偏好存储");

    /// <summary>按时间戳排序的既有损坏备份键（从旧到新）；时间戳格式保证字典序即时间序。</summary>
    private static List<string> CorruptBackupKeys(ISharedPreferences prefs)
    {
        var keys = new List<string>();
        var all = prefs.All;
        if (all != null)
        {
            foreach (string key in all.Keys)
            {
                if (key.StartsWith(CorruptBackupPrefix, StringComparison.Ordinal)) keys.Add(key);
            }
        }
        keys.Sort(StringComparer.Ordinal);
        return keys;
    }

    private static string BuildBackupKey(ISharedPreferences prefs)
    {
        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        string candidate = $"{CorruptBackupPrefix}{stamp}";
        int suffix = 1;
        while (prefs.GetString(candidate, null) != null)
            candidate = $"{CorruptBackupPrefix}{stamp}-{suffix++}";
        return candidate;
    }
}
