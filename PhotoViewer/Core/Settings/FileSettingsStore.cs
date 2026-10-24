using System.Globalization;
using System.Text;
using PhotoViewer.Core.Storage;

namespace PhotoViewer.Core.Settings;

/// <summary>本地文件实现，写入走 <see cref="LocalFileRef.AtomicWriteAsync"/>（唯一临时名 + 原子替换）。</summary>
public sealed class FileSettingsStore : ISettingsStore
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private readonly LocalFileRef _file;

    public FileSettingsStore(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        FilePath = System.IO.Path.GetFullPath(path);
        _file = new LocalFileRef(FilePath);
    }

    public string FilePath { get; }

    public async Task<SettingsStoreLoadResult> LoadAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var content = await File.ReadAllTextAsync(FilePath, Utf8NoBom, cancellationToken)
                .ConfigureAwait(false);
            return SettingsStoreLoadResult.FromContent(content);
        }
        catch (FileNotFoundException)
        {
            return SettingsStoreLoadResult.Absent;
        }
        catch (DirectoryNotFoundException)
        {
            return SettingsStoreLoadResult.Absent;
        }
    }

    public async Task SaveAsync(string json, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(json);

        var directory = System.IO.Path.GetDirectoryName(FilePath);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        var bytes = Utf8NoBom.GetBytes(json);
        await _file.AtomicWriteAsync(
            async (stream, token) =>
            {
                await stream.WriteAsync(bytes.AsMemory(), token).ConfigureAwait(false);
            },
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    public Task<string?> BackupCorruptAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!File.Exists(FilePath))
            throw new IOException($"没有可备份的设置文件：{FilePath}");

        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        var backupPath = $"{FilePath}.corrupt-{stamp}-{Guid.NewGuid():N}";
        File.Copy(FilePath, backupPath, overwrite: false);
        TrimCorruptBackups(backupPath);
        return Task.FromResult<string?>(backupPath);
    }

    private void TrimCorruptBackups(string newBackupPath)
    {
        var directory = System.IO.Path.GetDirectoryName(FilePath);
        if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory)) return;
        var prefix = System.IO.Path.GetFileName(FilePath) + ".corrupt-";
        var stale = Directory.GetFiles(directory, prefix + "*")
            .Select(path => new FileInfo(path))
            .OrderByDescending(info => info.CreationTimeUtc)
            .ThenByDescending(info => info.Name, StringComparer.Ordinal)
            .Where(info => !string.Equals(info.FullName, newBackupPath, StringComparison.Ordinal))
            .Skip(2);
        foreach (var info in stale)
        {
            try { info.Delete(); }
            catch (Exception exception)
            {
                Console.WriteLine($"清理旧设置备份失败（{info.FullName}）：{exception.Message}");
            }
        }
    }
}
