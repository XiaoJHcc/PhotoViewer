using System;
using System.IO;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;

namespace PhotoViewer.Core;

public interface IHeifDecoder
{
    bool IsSupported { get; }
    
    Task<Bitmap?> LoadBitmapAsync(IStorageFile file);
    Task<Bitmap?> LoadThumbnailAsync(IStorageFile file, int maxSize);

}

public static class HeifLoader
{
    private static IHeifDecoder _decoder = new NoopHeifDecoder();

    // 由各平台启动时注入具体实现
    public static void Initialize(IHeifDecoder decoder)
    {
        _decoder = decoder ?? new NoopHeifDecoder();
    }

    public static bool IsHeifFile(string filePath)
    {
        if (string.IsNullOrEmpty(filePath)) return false;
        var extension = Path.GetExtension(filePath).ToLowerInvariant();
        return extension is ".heif" or ".heic" or ".avif" or ".hif";
    }

    public static bool IsHeifFile(IStorageFile file)
    {
        if (file is null) return false;
        // Android 上可能是 content:// 无法从 LocalPath 识别扩展名，回退到 Name
        var fromPath = file.Path?.LocalPath ?? string.Empty;
        if (IsHeifFile(fromPath)) return true;

        // 某些平台 Name 可能包含扩展名
        try
        {
            var name = (file as IStorageItem)?.Name ?? string.Empty;
            return IsHeifFile(name);
        }
        catch
        {
            return false;
        }
    }

    public static Task<Bitmap?> LoadHeifBitmapAsync(IStorageFile file)
        => _decoder.IsSupported ? _decoder.LoadBitmapAsync(file) : Task.FromResult<Bitmap?>(null);

    public static Task<Bitmap?> LoadHeifThumbnailAsync(IStorageFile file, int maxSize = 120)
        => _decoder.IsSupported ? _decoder.LoadThumbnailAsync(file, maxSize) : Task.FromResult<Bitmap?>(null);
    
}

// 为空
internal sealed class NoopHeifDecoder : IHeifDecoder
{
    public bool IsSupported => false;

    public Task<Bitmap?> LoadBitmapAsync(IStorageFile file) => Task.FromResult<Bitmap?>(null);
    public Task<Bitmap?> LoadThumbnailAsync(IStorageFile file, int maxSize) => Task.FromResult<Bitmap?>(null);
}