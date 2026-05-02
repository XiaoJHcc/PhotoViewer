using System;
using System.Text.Json;
using System.Threading.Tasks;
using Android.App;
using Android.Content;
using PhotoViewer.Core.Settings;

namespace PhotoViewer.Android.Core;

public sealed class AndroidSettingsStorage : ISettingsStorage
{
    private const string PreferenceName = "cc.xiaojh.PhotoViewer.Settings";
    private const string SettingsKey = "settings_json";

    public Task<SettingsModel?> LoadAsync()
    {
        return Task.Run(() =>
        {
            try
            {
                var prefs = Application.Context.GetSharedPreferences(PreferenceName, FileCreationMode.Private);
                var json = prefs.GetString(SettingsKey, null);
                if (string.IsNullOrWhiteSpace(json)) return (SettingsModel?)null;

                return JsonSerializer.Deserialize(json, SettingsJsonContext.Default.SettingsModel);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"AndroidSettingsStorage.LoadAsync failed: {ex.Message}");
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
                var prefs = Application.Context.GetSharedPreferences(PreferenceName, FileCreationMode.Private);
                var json = JsonSerializer.Serialize(model, SettingsJsonContext.Default.SettingsModel);
                using var editor = prefs.Edit();
                editor.PutString(SettingsKey, json);
                editor.Apply();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"AndroidSettingsStorage.SaveAsync failed: {ex.Message}");
            }
        });
    }
}

