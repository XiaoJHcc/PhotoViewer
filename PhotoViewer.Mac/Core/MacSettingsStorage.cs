using System;
using System.Text.Json;
using System.Threading.Tasks;
using Foundation;
using PhotoViewer.Core.Settings;

namespace PhotoViewer.Mac.Core;

/// <summary>
/// macOS 用户偏好存储，使用 NSUserDefaults 写入 ~/Library/Preferences。
/// </summary>
public sealed class MacSettingsStorage : ISettingsStorage
{
    private const string SettingsKey = "cc.xiaojh.PhotoViewer.Settings.json";

    public Task<SettingsModel?> LoadAsync()
    {
        return Task.Run(() =>
        {
            try
            {
                var json = NSUserDefaults.StandardUserDefaults.StringForKey(SettingsKey);
                if (string.IsNullOrWhiteSpace(json)) return null;
                return JsonSerializer.Deserialize(json, SettingsJsonContext.Default.SettingsModel);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"MacSettingsStorage.LoadAsync failed: {ex.Message}");
                return null;
            }
        });
    }

    public Task SaveAsync(SettingsModel model)
    {
        return Task.Run(() =>
        {
            try
            {
                var json = JsonSerializer.Serialize(model, SettingsJsonContext.Default.SettingsModel);
                NSUserDefaults.StandardUserDefaults.SetString(json, SettingsKey);
                NSUserDefaults.StandardUserDefaults.Synchronize();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"MacSettingsStorage.SaveAsync failed: {ex.Message}");
            }
        });
    }
}

