using System;
using System.Text.Json;
using System.Threading.Tasks;
using Foundation;
using PhotoViewer.Core.Settings;

namespace PhotoViewer.iOS.Core;

public sealed class iOSSettingsStorage : ISettingsStorage
{
    private const string SettingsKey = "cc.xiaojh.PhotoViewer.Settings.json";

    public Task<SettingsModel?> LoadAsync()
    {
        return Task.Run(() =>
        {
            try
            {
                var json = NSUserDefaults.StandardUserDefaults.StringForKey(SettingsKey);
                if (string.IsNullOrWhiteSpace(json)) return (SettingsModel?)null;

                return JsonSerializer.Deserialize<SettingsModel>(json, SettingsJsonOptions.Default);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"iOSSettingsStorage.LoadAsync failed: {ex.Message}");
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
                var json = JsonSerializer.Serialize(model, SettingsJsonOptions.Default);
                NSUserDefaults.StandardUserDefaults.SetString(json, SettingsKey);
                NSUserDefaults.StandardUserDefaults.Synchronize();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"iOSSettingsStorage.SaveAsync failed: {ex.Message}");
            }
        });
    }
}

