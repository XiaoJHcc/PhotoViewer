using System;
using System.Globalization;
using System.Text.Json;
using System.Threading.Tasks;
using Android.App;
using Android.Content;
using PhotoViewer.Core.Settings;

namespace PhotoViewer.Android.Core;

/// <summary>
/// Android SharedPreferences 存储；JSON 损坏时另存带时间戳的偏好备份键，再以默认值启动（SETTINGS-14）。
/// </summary>
public sealed class AndroidSettingsStorage : ISettingsStorage
{
    private const string PreferenceName = "cc.xiaojh.PhotoViewer.Settings";
    private const string SettingsKey = "settings_json";
    private static string _preferenceName = PreferenceName;
    private string? _corruptJson;
    private bool _corruptPending;

    /// <summary>
    /// 切到隔离档：压测换用独立 SharedPreferences，不读也不覆盖用户设置，每轮从默认值起跑。
    /// 必须在共享设置首次加载前调用（原生壳建 <c>MainViewModel</c> 之前）。
    /// </summary>
    /// <param name="profile">档名（如 <c>stress</c>）；空值恢复用户档。</param>
    public static void UseIsolatedProfile(string profile)
        => _preferenceName = string.IsNullOrWhiteSpace(profile) ? PreferenceName : PreferenceName + "." + profile;

    public Task<SettingsModel?> LoadAsync()
    {
        return Task.Run(() =>
        {
            string? json = null;
            try
            {
                var prefs = Preferences();
                json = prefs.GetString(SettingsKey, null);
                if (string.IsNullOrWhiteSpace(json)) return (SettingsModel?)null;

                return JsonSerializer.Deserialize(json, SettingsJsonContext.Default.SettingsModel);
            }
            catch (JsonException ex)
            {
                _corruptJson = json;
                _corruptPending = true;
                throw new SettingsDocumentCorruptedException($"设置 JSON 损坏：{ex.Message}", ex);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"AndroidSettingsStorage.LoadAsync failed: {ex.Message}");
                throw;
            }
        });
    }

    public Task SaveAsync(SettingsModel model)
    {
        return Task.Run(() =>
        {
            if (_corruptPending)
            {
                throw new InvalidOperationException("设置 JSON 尚未备份，拒绝覆盖损坏内容");
            }

            var prefs = Preferences();
            var json = JsonSerializer.Serialize(model, SettingsJsonContext.Default.SettingsModel);
            using var editor = prefs.Edit() ?? throw new InvalidOperationException("无法编辑设置偏好存储");
            editor.PutString(SettingsKey, json);
            editor.Apply();
        });
    }

    public Task<string?> BackupCorruptAsync()
    {
        return Task.Run<string?>(() =>
        {
            try
            {
                var json = _corruptJson;
                if (string.IsNullOrEmpty(json)) return null;

                var prefs = Preferences();
                var backupKey = BuildBackupKey(prefs);
                using var editor = prefs.Edit() ?? throw new InvalidOperationException("无法编辑设置偏好存储");
                editor.PutString(backupKey, json);
                editor.Remove(SettingsKey);
                editor.Apply();
                _corruptJson = null;
                _corruptPending = false;
                return backupKey;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"AndroidSettingsStorage.BackupCorruptAsync failed: {ex.Message}");
                return null;
            }
        });
    }

    private static ISharedPreferences Preferences()
        => Application.Context.GetSharedPreferences(_preferenceName, FileCreationMode.Private)
           ?? throw new InvalidOperationException("无法打开设置偏好存储");

    private static string BuildBackupKey(ISharedPreferences prefs)
    {
        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        var candidate = $"{SettingsKey}.corrupt-{stamp}";
        var suffix = 1;
        while (prefs.GetString(candidate, null) != null)
        {
            candidate = $"{SettingsKey}.corrupt-{stamp}-{suffix++}";
        }
        return candidate;
    }
}

