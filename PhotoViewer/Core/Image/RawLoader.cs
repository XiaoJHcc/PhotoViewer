using System;
using System.IO;
using System.Threading.Tasks;
using PhotoViewer.Core.Storage;

namespace PhotoViewer.Core.Image;

public interface IRawDecoder
{
    bool IsSupported { get; }

    /// <summary>解码产物是否已是显示朝向(Apple ImageIO RAW 引擎为 true)。</summary>
    bool OutputPreRotated { get; }

    /// <summary>
    /// 解码 RAW 图像;<paramref name="maxSize"/> 非空时按长边上限缩渲染(ImageIO 支持),null 时全分辨率。
    /// 失败返回 null,原因记录到 <see cref="RawLoader.LastDecodeError"/>。
    /// </summary>
    Task<DecodedImage?> DecodeAsync(IFileRef file, int? maxSize);
}

/// <summary>
/// RAW 解码门面：扩展名识别 + 平台解码器分发。
/// 目前仅 macOS/iOS 提供实现（ImageIO 系统 RAW 引擎）；其他平台为 Noop，解码返回 null。
/// 可解码的机型/压缩格式取决于系统 ImageIO 支持列表（随 macOS/iOS 版本更新），
/// 不支持的文件由平台解码器返回 null 并记录 <see cref="LastDecodeError"/>。
/// </summary>
public static class RawLoader
{
    private static IRawDecoder _decoder = new NoopRawDecoder();

    /// <summary>
    /// 最近一次 RAW 解码失败的原因。当 DecodeAsync 返回 null 时可读取此属性辅助诊断。
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

    /// <summary>当前解码器产物是否已是显示朝向。</summary>
    public static bool OutputPreRotated => _decoder.IsSupported && _decoder.OutputPreRotated;

    // 与设置页「RAW」格式组的扩展名保持一致
    public static bool IsRawFile(string filePath)
    {
        if (string.IsNullOrEmpty(filePath)) return false;
        var extension = Path.GetExtension(filePath).ToLowerInvariant();
        return extension is ".cr2" or ".cr3" or ".nef" or ".arw" or ".dng"
            or ".raf" or ".orf" or ".rw2" or ".srw";
    }

    public static bool IsRawFile(IFileRef file)
    {
        if (file is null) return false;
        // Android 上可能是 content:// 无法从 LocalPath 识别扩展名，回退到 Name
        var fromPath = file.PathKey;
        if (IsRawFile(fromPath)) return true;

        try
        {
            var name = file.Name ?? string.Empty;
            return IsRawFile(name);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 解码 RAW;<paramref name="maxSize"/> 为代理长边上限(null = 全分辨率)。
    /// </summary>
    public static Task<DecodedImage?> DecodeAsync(IFileRef file, int? maxSize)
    {
        _lastDecodeError = null;
        if (!_decoder.IsSupported)
        {
            _lastDecodeError = "RAW 解码器不可用（未初始化或当前平台不支持）";
            return Task.FromResult<DecodedImage?>(null);
        }
        return _decoder.DecodeAsync(file, maxSize);
    }

    private sealed class NoopRawDecoder : IRawDecoder
    {
        public bool IsSupported => false;
        public bool OutputPreRotated => false;
        public Task<DecodedImage?> DecodeAsync(IFileRef file, int? maxSize) => Task.FromResult<DecodedImage?>(null);
    }
}
