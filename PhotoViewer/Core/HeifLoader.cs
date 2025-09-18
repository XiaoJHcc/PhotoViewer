using System;
using System.IO;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;

namespace PhotoViewer.Core;

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

    public static bool IsHeifFile(IStorageFile file) => IsHeifFile(file.Path.LocalPath);

    public static Task<Bitmap?> LoadHeifBitmapAsync(IStorageFile file)
        => _decoder.IsSupported ? _decoder.LoadBitmapAsync(file) : Task.FromResult<Bitmap?>(null);

    public static Task<Bitmap?> LoadHeifThumbnailAsync(IStorageFile file, int maxSize = 120)
        => _decoder.IsSupported ? _decoder.LoadThumbnailAsync(file, maxSize) : Task.FromResult<Bitmap?>(null);

    [Obsolete("Obsolete")]
    public static Task<Bitmap?> LoadHeifBitmapFromStreamAsync(Stream stream)
        => _decoder.IsSupported ? _decoder.LoadBitmapFromStreamAsync(stream) : Task.FromResult<Bitmap?>(null);

    [Obsolete("Obsolete")]
    public static Task<Bitmap?> LoadHeifThumbnailFromStreamAsync(Stream stream, int maxSize = 120)
        => _decoder.IsSupported ? _decoder.LoadThumbnailFromStreamAsync(stream, maxSize) : Task.FromResult<Bitmap?>(null);
}

// 为空
internal sealed class NoopHeifDecoder : IHeifDecoder
{
    public bool IsSupported => false;

    public bool IsHeifFile(string filePath)
    {
        if (string.IsNullOrEmpty(filePath)) return false;
        var ext = System.IO.Path.GetExtension(filePath).ToLowerInvariant();
        return ext is ".heif" or ".heic" or ".avif" or ".hif";
    }

    public Task<Bitmap?> LoadBitmapAsync(IStorageFile file) => Task.FromResult<Bitmap?>(null);
    public Task<Bitmap?> LoadThumbnailAsync(IStorageFile file, int maxSize) => Task.FromResult<Bitmap?>(null);
    public Task<Bitmap?> LoadBitmapFromStreamAsync(Stream stream) => Task.FromResult<Bitmap?>(null);
    public Task<Bitmap?> LoadThumbnailFromStreamAsync(Stream stream, int maxSize) => Task.FromResult<Bitmap?>(null);
}