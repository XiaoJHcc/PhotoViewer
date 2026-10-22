using System;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace PhotoViewer.Core.Settings;

public interface ISettingsStorage
{
    Task<SettingsModel?> LoadAsync();
    Task SaveAsync(SettingsModel model);

    /// <summary>
    /// 读取因 JSON 损坏失败时，把原始内容另存为带 <c>yyyyMMdd-HHmmss</c> 时间戳的备份（SETTINGS-14）。
    /// </summary>
    /// <returns>备份位置（文件路径或偏好键名）；没有可备份内容或备份失败时返回 null。</returns>
    Task<string?> BackupCorruptAsync();
}

/// <summary>设置文档存在但 JSON 无法解析时由存储层抛出，供 <see cref="SettingsService"/> 先备份再恢复默认。</summary>
public sealed class SettingsDocumentCorruptedException : Exception
{
    /// <summary>用说明与内部异常创建实例。</summary>
    /// <param name="message">说明。</param>
    /// <param name="innerException">反序列化异常。</param>
    public SettingsDocumentCorruptedException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}

public sealed class SettingsService : ISettingsService
{
    private readonly ISettingsStorage _storage;
    private readonly SemaphoreSlim _mutex = new(1, 1);
    private SettingsModel _cached = new();

    /// <inheritdoc />
    public string? LastLoadError { get; private set; }

    /// <inheritdoc />
    public string? LastSaveError { get; private set; }

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
            LastLoadError = null;
            var model = await _storage.LoadAsync().ConfigureAwait(false);
            _cached = model ?? new SettingsModel { Version = 0 };
            return Clone(_cached);
        }
        catch (SettingsDocumentCorruptedException ex)
        {
            // SETTINGS-14：先备份损坏文档，再以默认值启动，绝不直接覆盖原内容。
            var backup = await TryBackupCorruptAsync().ConfigureAwait(false);
            LastLoadError = backup == null
                ? "设置文件已损坏，未能备份，已恢复默认设置"
                : $"设置文件已损坏，已备份为 {backup} 并恢复默认设置";
            Console.WriteLine($"SettingsService.LoadAsync failed: {ex.Message}");
            _cached = new SettingsModel();
            return Clone(_cached);
        }
        catch (Exception ex)
        {
            LastLoadError = $"读取设置失败：{ex.Message}";
            Console.WriteLine($"SettingsService.LoadAsync failed: {ex.Message}");
            _cached = new SettingsModel();
            return Clone(_cached);
        }
        finally
        {
            _mutex.Release();
        }
    }

    /// <summary>调用存储层备份损坏内容；备份失败只记录日志，不阻断默认值启动。</summary>
    private async Task<string?> TryBackupCorruptAsync()
    {
        try
        {
            return await _storage.BackupCorruptAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"SettingsService.BackupCorruptAsync failed: {ex.Message}");
            return null;
        }
    }

    public async Task SaveAsync(SettingsModel model)
    {
        await _mutex.WaitAsync().ConfigureAwait(false);
        try
        {
            _cached = Clone(model);
            await _storage.SaveAsync(model).ConfigureAwait(false);
            LastSaveError = null;
        }
        catch (Exception ex)
        {
            LastSaveError = ex.Message;
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
        var json = JsonSerializer.Serialize(source, SettingsJsonContext.Default.SettingsModel);
        return JsonSerializer.Deserialize(json, SettingsJsonContext.Default.SettingsModel) ?? new SettingsModel();
    }

    public static ISettingsStorage CreateFileStorage()
    {
        var path = SettingsPathHelper.GetDefaultPath("PhotoViewer");
        return new FileSettingsStorage(path);
    }
}

public interface ISettingsService
{
    /// <summary>最近一次读取失败的用户可读说明；读取成功后为 null。</summary>
    string? LastLoadError { get; }

    /// <summary>最近一次保存失败的用户可读说明；保存成功后为 null。</summary>
    string? LastSaveError { get; }

    Task<SettingsModel> LoadAsync();
    Task SaveAsync(SettingsModel model);
}

public sealed class FileSettingsStorage : ISettingsStorage
{
    private readonly string _path;
    private bool _corruptPending;

    public FileSettingsStorage(string path)
    {
        _path = path;
    }

    public async Task<SettingsModel?> LoadAsync()
    {
        if (!File.Exists(_path)) return null;

        try
        {
            using var stream = File.OpenRead(_path);
            return await JsonSerializer.DeserializeAsync(stream, SettingsJsonContext.Default.SettingsModel).ConfigureAwait(false);
        }
        catch (JsonException ex)
        {
            _corruptPending = true;
            throw new SettingsDocumentCorruptedException($"设置文件 JSON 损坏：{ex.Message}", ex);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"FileSettingsStorage.LoadAsync failed: {ex.Message}");
            throw;
        }
    }

    public async Task SaveAsync(SettingsModel model)
    {
        if (_corruptPending)
        {
            throw new IOException("设置文件尚未备份，拒绝覆盖损坏内容");
        }

        var directory = Path.GetDirectoryName(_path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var tempPath = _path + ".tmp";
        await using (var stream = File.Create(tempPath))
        {
            await JsonSerializer.SerializeAsync(stream, model, SettingsJsonContext.Default.SettingsModel).ConfigureAwait(false);
        }

        if (File.Exists(_path))
        {
            File.Delete(_path);
        }
        File.Move(tempPath, _path);
    }

    public Task<string?> BackupCorruptAsync()
    {
        try
        {
            if (!File.Exists(_path)) return Task.FromResult<string?>(null);

            var backup = BuildBackupPath();
            File.Move(_path, backup);
            _corruptPending = false;
            return Task.FromResult<string?>(backup);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"FileSettingsStorage.BackupCorruptAsync failed: {ex.Message}");
            return Task.FromResult<string?>(null);
        }
    }

    private string BuildBackupPath()
    {
        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        var candidate = $"{_path}.corrupt-{stamp}";
        var suffix = 1;
        while (File.Exists(candidate))
        {
            candidate = $"{_path}.corrupt-{stamp}-{suffix++}";
        }
        return candidate;
    }
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

        if (OperatingSystem.IsMacCatalyst() || OperatingSystem.IsIOS())
            // iOS 沙盒:HOME 口径的 ~/.config 实测对 devicectl(house_arrest)不可见,诊断日志无法取回;
            // Documents 在容器内可直接读写且可经 devicectl copy 拉取,诊断产物落此。
            return Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);

        if (OperatingSystem.IsMacOS())
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData));

        var xdgConfig = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        if (!string.IsNullOrWhiteSpace(xdgConfig))
            return xdgConfig;

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(home, ".config");
    }
}

