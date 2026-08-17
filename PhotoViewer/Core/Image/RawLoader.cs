using System;
using System.IO;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;

namespace PhotoViewer.Core.Image;

public interface IRawDecoder
{
    bool IsSupported { get; }

    Task<Bitmap?> LoadBitmapAsync(IStorageFile file);
    Task<Bitmap?> LoadThumbnailAsync(IStorageFile file, int maxSize);
}

/// <summary>
/// RAW 解码门面：扩展名识别 + 平台解码器分发。
/// 目前仅 macOS 提供实现（ImageIO 系统 RAW 引擎）；其他平台为 Noop，解码返回 null。
/// 可解码的机型/压缩格式取决于系统 ImageIO 支持列表（随 macOS 版本更新），
/// 不支持的文件由平台解码器返回 null 并记录 <see cref="LastDecodeError"/>。
/// </summary>
public static class RawLoader
{
    private static IRawDecoder _decoder = new NoopRawDecoder();

    /// <summary>
    /// 最近一次 RAW 解码失败的原因。当 LoadRawBitmapAsync 返回 null 时可读取此属性辅助诊断。
    /// </summary>
    private static volatile string? _lastDecodeError;

    public static string? LastDecodeError => _lastDecodeError;

    public static void SetLastDecodeError(string? error) => _lastDecodeError = error;

    // 由各平台启动时注入具体实现
    public static void Initialize(IRawDecoder decoder)
    {
        _decoder = decoder ?? new NoopRawDecoder();
    }

    /// <summary>当前平台是否有可用的 RAW 解码器。</summary>
    public static bool IsSupported => _decoder.IsSupported;

    // 与设置页「RAW」格式组的扩展名保持一致
    public static bool IsRawFile(string filePath)
    {
        if (string.IsNullOrEmpty(filePath)) return false;
        var extension = Path.GetExtension(filePath).ToLowerInvariant();
        return extension is ".cr2" or ".cr3" or ".nef" or ".arw" or ".dng"
            or ".raf" or ".orf" or ".rw2" or ".srw";
    }

    public static bool IsRawFile(IStorageFile file)
    {
        if (file is null) return false;
        // Android 上可能是 content:// 无法从 LocalPath 识别扩展名，回退到 Name
        var fromPath = file.Path?.LocalPath ?? string.Empty;
        if (IsRawFile(fromPath)) return true;

        try
        {
            var name = (file as IStorageItem)?.Name ?? string.Empty;
            return IsRawFile(name);
        }
        catch
        {
            return false;
        }
    }

    public static Task<Bitmap?> LoadRawBitmapAsync(IStorageFile file)
    {
        _lastDecodeError = null;
        if (!_decoder.IsSupported)
        {
            _lastDecodeError = "RAW 解码器不可用（未初始化或当前平台不支持）";
            return Task.FromResult<Bitmap?>(null);
        }
        return _decoder.LoadBitmapAsync(file);
    }

    public static Task<Bitmap?> LoadRawThumbnailAsync(IStorageFile file, int maxSize = 120)
        => _decoder.IsSupported ? _decoder.LoadThumbnailAsync(file, maxSize) : Task.FromResult<Bitmap?>(null);

    private sealed class NoopRawDecoder : IRawDecoder
    {
        public bool IsSupported => false;
        public Task<Bitmap?> LoadBitmapAsync(IStorageFile file) => Task.FromResult<Bitmap?>(null);
        public Task<Bitmap?> LoadThumbnailAsync(IStorageFile file, int maxSize) => Task.FromResult<Bitmap?>(null);
    }
}
