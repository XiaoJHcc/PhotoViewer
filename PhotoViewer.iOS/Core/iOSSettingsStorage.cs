using System;
using System.Globalization;
using System.Text.Json;
using System.Threading.Tasks;
using Foundation;
using PhotoViewer.Core.Settings;

namespace PhotoViewer.iOS.Core;

/// <summary>
/// iOS 用户偏好存储；JSON 损坏时另存带时间戳的偏好备份键，再以默认值启动（SETTINGS-14）。
/// </summary>
public sealed class iOSSettingsStorage : ISettingsStorage
{
    private const string SettingsKey = "cc.xiaojh.PhotoViewer.Settings.json";
    private string? _corruptJson;
    private bool _corruptPending;

    public Task<SettingsModel?> LoadAsync()
    {
        return Task.Run(() =>
        {
            string? json = null;
            try
            {
                json = NSUserDefaults.StandardUserDefaults.StringForKey(SettingsKey);
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
                Console.WriteLine($"iOSSettingsStorage.LoadAsync failed: {ex.Message}");
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

            var json = JsonSerializer.Serialize(model, SettingsJsonContext.Default.SettingsModel);
            NSUserDefaults.StandardUserDefaults.SetString(json, SettingsKey);
            NSUserDefaults.StandardUserDefaults.Synchronize();
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

                var defaults = NSUserDefaults.StandardUserDefaults;
                var backupKey = BuildBackupKey();
                defaults.SetString(json, backupKey);
                defaults.RemoveObject(SettingsKey);
                defaults.Synchronize();
                _corruptJson = null;
                _corruptPending = false;
                return backupKey;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"iOSSettingsStorage.BackupCorruptAsync failed: {ex.Message}");
                return null;
            }
        });
    }

    private static string BuildBackupKey()
    {
        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        var candidate = $"{SettingsKey}.corrupt-{stamp}";
        var suffix = 1;
        while (NSUserDefaults.StandardUserDefaults.StringForKey(candidate) != null)
        {
            candidate = $"{SettingsKey}.corrupt-{stamp}-{suffix++}";
        }
        return candidate;
    }
}

