using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using PhotoViewer.Core.Storage;

namespace PhotoViewer.Core.Image;

public interface IHeifDecoder
{
    bool IsSupported { get; }

    /// <summary>解码产物是否已是显示朝向(解码器内部应用了方向)。true 时 <see cref="BitmapLoader"/> 不再二次旋转。</summary>
    bool OutputPreRotated { get; }

    /// <summary>
    /// 解码 HEIF 图像;<paramref name="maxSize"/> 非空时代理缩解码(长边上限像素,质量不低于 Mitchell 级重采样),
    /// null 时全分辨率。失败返回 null,原因记录到 <see cref="HeifLoader.LastDecodeError"/>。
    /// </summary>
    Task<DecodedImage?> DecodeAsync(IFileRef file, int? maxSize);

}

public static class HeifLoader
{
    private static IHeifDecoder _decoder = new NoopHeifDecoder();
    private static SemaphoreSlim _cpuDecodeSemaphore = new(8, 8);
    private static volatile int _cpuDecodeParallelism = 8;

    /// <summary>
    /// 最近一次 HEIF 解码失败时各回退级别的详细原因。
    /// 由平台解码器（如 AndroidLibHeifDecoder）在解码失败时写入；成功时清空。
    /// </summary>
    private static volatile string? _lastDecodeError;

    /// <summary>
    /// 最近一次 HEIF 解码失败时各回退级别的详细错误描述。
    /// 当 DecodeAsync 返回 null 时可读取此属性辅助诊断。
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

    /// <summary>当前解码器产物是否已是显示朝向。</summary>
    public static bool OutputPreRotated => _decoder.IsSupported && _decoder.OutputPreRotated;

    public static bool IsHeifFile(string filePath)
    {
        if (string.IsNullOrEmpty(filePath)) return false;
        var extension = Path.GetExtension(filePath).ToLowerInvariant();
        return extension is ".heif" or ".heic" or ".avif" or ".hif";
    }

    public static bool IsHeifFile(IFileRef file)
    {
        if (file is null) return false;
        // 先用显示名识别（SAF document id 可能没有扩展名），再看本地路径。
        try
        {
            if (IsHeifFile(file.Name)) return true;
        }
        catch
        {
            // Name 不可用时继续尝试本地路径。
        }

        var localPath = file.TryGetLocalPath();
        return !string.IsNullOrEmpty(localPath) && IsHeifFile(localPath);
    }

    /// <summary>
    /// 解码 HEIF;<paramref name="maxSize"/> 为代理长边上限(null = 全分辨率)。
    /// </summary>
    public static Task<DecodedImage?> DecodeAsync(IFileRef file, int? maxSize = null)
    {
        _lastDecodeError = null;
        if (!_decoder.IsSupported)
        {
            _lastDecodeError = "HEIF 解码器不可用（未初始化或当前平台不支持）";
            return Task.FromResult<DecodedImage?>(null);
        }
        return _decoder.DecodeAsync(file, maxSize);
    }

    /// <summary>
    /// 设置 CPU 软件解码路径的最大并发数。
    /// </summary>
    public static void SetCpuDecodeParallelism(int parallelism)
    {
        var clamped = Math.Max(1, parallelism);
        if (clamped == _cpuDecodeParallelism)
        {
            return;
        }

        Interlocked.Exchange(ref _cpuDecodeSemaphore, new SemaphoreSlim(clamped, clamped));
        _cpuDecodeParallelism = clamped;
    }

    /// <summary>
    /// 在 CPU 软件解码预算下执行任务。
    /// </summary>
    public static async Task<T> RunCpuDecodeAsync<T>(Func<Task<T>> action)
    {
        var semaphore = _cpuDecodeSemaphore;
        await semaphore.WaitAsync();
        try
        {
            return await action();
        }
        finally
        {
            semaphore.Release();
        }
    }

}

// 为空
internal sealed class NoopHeifDecoder : IHeifDecoder
{
    public bool IsSupported => false;
    public bool OutputPreRotated => false;

    public Task<DecodedImage?> DecodeAsync(IFileRef file, int? maxSize) => Task.FromResult<DecodedImage?>(null);
}
