using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace PhotoViewer.Core.Settings;

public interface ISettingsStorage
{
    Task<SettingsModel?> LoadAsync();
    Task SaveAsync(SettingsModel model);
}

public sealed class SettingsService : ISettingsService
{
    private readonly ISettingsStorage _storage;
    private readonly SemaphoreSlim _mutex = new(1, 1);
    private SettingsModel _cached = new();

    public SettingsService(ISettingsStorage storage)
    {
        _storage = storage;
    }

    private static readonly Lazy<ISettingsService> _lazyInstance = new(() => new SettingsService(CreateFileStorage()));
    private static ISettingsService? _overrideInstance;

    public static ISettingsService Instance
    {
        get => _overrideInstance ?? _lazyInstance.Value;
        private set => _overrideInstance = value;
    }

    public static void ConfigureStorage(ISettingsStorage storage)
    {
        Instance = new SettingsService(storage);
    }

    public async Task<SettingsModel> LoadAsync()
    {
        await _mutex.WaitAsync().ConfigureAwait(false);
        try
        {
            var model = await _storage.LoadAsync().ConfigureAwait(false);
            _cached = model ?? new SettingsModel { Version = 0 };
            return Clone(_cached);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"SettingsService.LoadAsync failed: {ex.Message}");
            return new SettingsModel();
        }
        finally
        {
            _mutex.Release();
        }
    }

    public async Task SaveAsync(SettingsModel model)
    {
        await _mutex.WaitAsync().ConfigureAwait(false);
        try
        {
            _cached = Clone(model);
            await _storage.SaveAsync(model).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"SettingsService.SaveAsync failed: {ex.Message}");
        }
        finally
        {
            _mutex.Release();
        }
    }

    private static SettingsModel Clone(SettingsModel source)
    {
        // Json round-trip clone to avoid accidental reference mutation
        var json = JsonSerializer.Serialize(source, SettingsJsonOptions.Default);
        return JsonSerializer.Deserialize<SettingsModel>(json, SettingsJsonOptions.Default) ?? new SettingsModel();
    }

    public static ISettingsStorage CreateFileStorage()
    {
        var path = SettingsPathHelper.GetDefaultPath("PhotoViewer");
        return new FileSettingsStorage(path);
    }
}

public interface ISettingsService
{
    Task<SettingsModel> LoadAsync();
    Task SaveAsync(SettingsModel model);
}

public sealed class FileSettingsStorage : ISettingsStorage
{
    private readonly string _path;

    public FileSettingsStorage(string path)
    {
        _path = path;
    }

    public async Task<SettingsModel?> LoadAsync()
    {
        try
        {
            if (!File.Exists(_path)) return null;

            using var stream = File.OpenRead(_path);
            return await JsonSerializer.DeserializeAsync<SettingsModel>(stream, SettingsJsonOptions.Default).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"FileSettingsStorage.LoadAsync failed: {ex.Message}");
            return null;
        }
    }

    public async Task SaveAsync(SettingsModel model)
    {
        try
        {
            var directory = Path.GetDirectoryName(_path);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var tempPath = _path + ".tmp";
            await using (var stream = File.Create(tempPath))
            {
                await JsonSerializer.SerializeAsync(stream, model, SettingsJsonOptions.Default).ConfigureAwait(false);
            }

            if (File.Exists(_path))
            {
                File.Delete(_path);
            }
            File.Move(tempPath, _path);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"FileSettingsStorage.SaveAsync failed: {ex.Message}");
        }
    }
}

public static class SettingsJsonOptions
{
    public static readonly JsonSerializerOptions Default = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };
}

public static class SettingsPathHelper
{
    public static string GetDefaultPath(string appFolderName)
    {
        var baseDir = GetBaseDirectory();
        var dir = Path.Combine(baseDir, appFolderName);
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, "settings.json");
    }

    private static string GetBaseDirectory()
    {
        if (OperatingSystem.IsWindows())
            return Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        if (OperatingSystem.IsMacOS())
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData));

        var xdgConfig = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        if (!string.IsNullOrWhiteSpace(xdgConfig))
            return xdgConfig;

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(home, ".config");
    }
}

