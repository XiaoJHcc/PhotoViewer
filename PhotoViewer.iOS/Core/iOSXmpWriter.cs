using System;
using System.Threading.Tasks;
using Avalonia.Platform.Storage;
using PhotoViewer.Core;

namespace PhotoViewer.iOS.Core;

/// <summary>
/// iOS 平台的 XMP 字节写入器。
/// 基于沙盒内或系统授权后的真实文件路径执行原位写入。
/// </summary>
public sealed class iOSXmpWriter : IXmpPlatformWriter
{
    /// <summary>
    /// 在 iOS 文件系统上执行单字节原位写入。
    /// </summary>
    public Task<bool> TryWriteByteAsync(IStorageFile file, long bytePosition, byte expectedByte, byte newByte, bool enableSafeMode)
    {
        try
        {
            if (!XmpLocalFileWriter.TryGetWritableLocalPath(file, out var localPath))
            {
                Console.WriteLine("[XMP Writer] iOS: Local path unavailable");
                return Task.FromResult(false);
            }

            return Task.FromResult(XmpLocalFileWriter.TryWriteByte(localPath, bytePosition, expectedByte, newByte, enableSafeMode, "iOS", useWriteThrough: false));
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[XMP Writer] iOS writer failed: {ex.Message}");
            return Task.FromResult(false);
        }
    }
}