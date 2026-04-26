using System;
using System.IO;
using System.Threading.Tasks;
using Android.Content;
using Avalonia.Platform.Storage;
using Java.Nio;
using PhotoViewer.Core;
using AndroidUri = Android.Net.Uri;
using JavaFileInputStream = Java.IO.FileInputStream;
using JavaFileOutputStream = Java.IO.FileOutputStream;

namespace PhotoViewer.Android.Core;

/// <summary>
/// Android 平台的 XMP 字节写入器。
/// 对真实文件路径走原位单字节写入；对 content URI 走文件描述符定位写入并立即同步到存储介质。
/// </summary>
public sealed class AndroidXmpWriter : IXmpPlatformWriter
{
    private readonly ContentResolver _contentResolver;

    /// <summary>
    /// 构造 Android 平台写入器。
    /// </summary>
    /// <param name="contentResolver">用于访问 content URI 的解析器</param>
    public AndroidXmpWriter(ContentResolver contentResolver)
    {
        _contentResolver = contentResolver ?? throw new ArgumentNullException(nameof(contentResolver));
    }

    /// <summary>
    /// 以 Android 原生方式将单个字节写回文件，并在安全模式下做字节级回读校验。
    /// </summary>
    public Task<bool> TryWriteByteAsync(IStorageFile file, long bytePosition, byte expectedByte, byte newByte, bool enableSafeMode)
    {
        try
        {
            if (XmpLocalFileWriter.TryGetWritableLocalPath(file, out var localPath))
            {
                return Task.FromResult(WriteByLocalPath(localPath, bytePosition, expectedByte, newByte, enableSafeMode));
            }

            if (file.Path != null && string.Equals(file.Path.Scheme, ContentResolver.SchemeContent, StringComparison.OrdinalIgnoreCase))
            {
                var uri = AndroidUri.Parse(file.Path.AbsoluteUri);
                if (uri == null)
                {
                    Console.WriteLine($"[XMP Writer] Android: Failed to parse URI {file.Path.AbsoluteUri}");
                    return Task.FromResult(false);
                }

                return Task.FromResult(WriteByContentUri(uri, bytePosition, expectedByte, newByte, enableSafeMode));
            }

            Console.WriteLine($"[XMP Writer] Android: Unsupported path scheme {file.Path?.Scheme ?? "<null>"}");
            return Task.FromResult(false);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[XMP Writer] Android writer failed: {ex.Message}");
            return Task.FromResult(false);
        }
    }

    /// <summary>
    /// 在 Android 可直接访问的本地路径上执行单字节原位写入。
    /// </summary>
    private static bool WriteByLocalPath(string filePath, long bytePosition, byte expectedByte, byte newByte, bool enableSafeMode)
    {
        if (enableSafeMode)
        {
            var existingByte = XmpLocalFileWriter.ReadByte(filePath, bytePosition);
            if (existingByte != expectedByte)
            {
                Console.WriteLine($"[XMP Writer] Android local pre-check mismatch: expected={(char)expectedByte}, actual={(char)existingByte}");
                return false;
            }
        }

        using (var stream = new FileStream(filePath, FileMode.Open, FileAccess.ReadWrite, FileShare.Read, 1))
        {
            stream.Seek(bytePosition, SeekOrigin.Begin);
            stream.WriteByte(newByte);
            stream.Flush(true);
        }

        return !enableSafeMode || XmpLocalFileWriter.ReadByte(filePath, bytePosition) == newByte;
    }

    /// <summary>
    /// 通过 content URI 对单个字节做原位写入，并调用底层文件描述符同步到存储。
    /// </summary>
    private bool WriteByContentUri(AndroidUri uri, long bytePosition, byte expectedByte, byte newByte, bool enableSafeMode)
    {
        if (enableSafeMode)
        {
            var existingByte = ReadByteFromContentUri(uri, bytePosition);
            if (existingByte != expectedByte)
            {
                Console.WriteLine($"[XMP Writer] Android URI pre-check mismatch: expected={(char)expectedByte}, actual={(char)existingByte}");
                return false;
            }
        }

        using (var descriptor = _contentResolver.OpenFileDescriptor(uri, "rw"))
        {
            if (descriptor == null)
            {
                Console.WriteLine("[XMP Writer] Android: OpenFileDescriptor(rw) returned null");
                return false;
            }

            var fileDescriptor = descriptor.FileDescriptor;
            if (fileDescriptor == null)
            {
                Console.WriteLine("[XMP Writer] Android: Missing file descriptor for write");
                return false;
            }

            using var outputStream = new JavaFileOutputStream(fileDescriptor);
            using var channel = outputStream.Channel;
            if (channel == null)
            {
                Console.WriteLine("[XMP Writer] Android: Missing file channel for write");
                return false;
            }

            channel.Position(bytePosition);
            using var buffer = ByteBuffer.Wrap(new[] { newByte });
            while (buffer.HasRemaining)
            {
                channel.Write(buffer);
            }

            outputStream.Flush();
            channel.Force(true);
            fileDescriptor.Sync();
        }

        return !enableSafeMode || ReadByteFromContentUri(uri, bytePosition) == newByte;
    }

    /// <summary>
    /// 从 content URI 读取指定偏移处的单个字节。
    /// </summary>
    private byte ReadByteFromContentUri(AndroidUri uri, long bytePosition)
    {
        using var descriptor = _contentResolver.OpenFileDescriptor(uri, "r");
        if (descriptor == null)
        {
            throw new IOException("OpenFileDescriptor(r) returned null");
        }

        var fileDescriptor = descriptor.FileDescriptor;
        if (fileDescriptor == null)
        {
            throw new IOException("Missing file descriptor for read");
        }

        using var inputStream = new JavaFileInputStream(fileDescriptor);
        using var channel = inputStream.Channel;
        if (channel == null)
        {
            throw new IOException("Missing file channel for read");
        }

        channel.Position(bytePosition);
        using var buffer = ByteBuffer.Allocate(1);
        var read = channel.Read(buffer);
        if (read != 1)
        {
            throw new EndOfStreamException("Failed to read target byte from content URI");
        }

        buffer.Flip();
        return unchecked((byte)buffer.Get());
    }
}