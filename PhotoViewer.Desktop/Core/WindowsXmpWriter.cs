using System;
using System.Threading.Tasks;
using Avalonia.Platform.Storage;
using PhotoViewer.Core;

namespace PhotoViewer.Desktop.Core;

/// <summary>
/// Windows 平台的 XMP 字节写入器。
/// 使用真实文件路径原位写入，并启用写穿透降低系统缓存延迟。
/// </summary>
public sealed class WindowsXmpWriter : IXmpPlatformWriter
{
    /// <summary>
    /// 在 Windows 文件系统上执行单字节原位写入。
    /// </summary>
    public Task<bool> TryWriteByteAsync(IStorageFile file, long bytePosition, byte expectedByte, byte newByte, bool enableSafeMode)
    {
        try
        {
            if (!XmpLocalFileWriter.TryGetWritableLocalPath(file, out var localPath))
            {
                Console.WriteLine("[XMP Writer] Windows: Local path unavailable");
                return Task.FromResult(false);
            }

            return Task.FromResult(XmpLocalFileWriter.TryWriteByte(localPath, bytePosition, expectedByte, newByte, enableSafeMode, "Windows", useWriteThrough: true));
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[XMP Writer] Windows writer failed: {ex.Message}");
            return Task.FromResult(false);
        }
    }
}