using System;
using System.Threading.Tasks;
using Avalonia.Platform.Storage;
using PhotoViewer.Core;

namespace PhotoViewer.Mac.Core;

/// <summary>
/// macOS 平台的 XMP 字节写入器。
/// 使用真实文件路径原位写入，并依赖 Flush(true) 将修改同步到底层文件系统。
/// </summary>
public sealed class MacXmpWriter : IXmpPlatformWriter
{
    /// <summary>
    /// 在 macOS 文件系统上执行单字节原位写入。
    /// </summary>
    public Task<bool> TryWriteByteAsync(IStorageFile file, long bytePosition, byte expectedByte, byte newByte, bool enableSafeMode)
    {
        try
        {
            if (!XmpLocalFileWriter.TryGetWritableLocalPath(file, out var localPath))
            {
                Console.WriteLine("[XMP Writer] Mac: Local path unavailable");
                return Task.FromResult(false);
            }

            return Task.FromResult(XmpLocalFileWriter.TryWriteByte(localPath, bytePosition, expectedByte, newByte, enableSafeMode, "Mac", useWriteThrough: false));
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[XMP Writer] Mac writer failed: {ex.Message}");
            return Task.FromResult(false);
        }
    }
}