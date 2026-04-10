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

    /// <summary>
    /// 最近一次 HEIF 解码失败时各回退级别的详细原因。
    /// 由平台解码器（如 AndroidLibHeifDecoder）在解码失败时写入；成功时清空。
    /// </summary>
    private static volatile string? _lastDecodeError;

    /// <summary>
    /// 最近一次 HEIF 解码失败时各回退级别的详细错误描述。
    /// 当 LoadHeifBitmapAsync 返回 null 时可读取此属性辅助诊断。
    /// </summary>
    public static string? LastDecodeError => _lastDecodeError;

    /// <summary>
    /// 由各平台解码器调用，记录解码失败的详细原因。
    /// </summary>
    public static void SetLastDecodeError(string? error) => _lastDecodeError = error;

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
    {
        _lastDecodeError = null;
        if (!_decoder.IsSupported)
        {
            _lastDecodeError = "HEIF 解码器不可用（未初始化或当前平台不支持）";
            return Task.FromResult<Bitmap?>(null);
        }
        return _decoder.LoadBitmapAsync(file);
    }

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