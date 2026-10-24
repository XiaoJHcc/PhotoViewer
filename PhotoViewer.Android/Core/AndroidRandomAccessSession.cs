using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Android.Content;
using Java.Nio.Channels;
using PhotoViewer.Core.Storage;
using AndroidUri = Android.Net.Uri;
using JavaFileDescriptor = Java.IO.FileDescriptor;
using JavaFileInputStream = Java.IO.FileInputStream;
using JavaFileOutputStream = Java.IO.FileOutputStream;
using ParcelFileDescriptor = Android.OS.ParcelFileDescriptor;

namespace PhotoViewer.Android.Core;

/// <summary>
/// SAF 文档的随机访问会话：<see cref="ContentProvider"/> 侧文件描述符 + <see cref="FileChannel"/> 定位，
/// 字节搬运走 <see cref="JavaFileInputStream"/>/<see cref="JavaFileOutputStream"/> 的托管 <c>byte[]</c> 重载
/// （<see cref="Java.Nio.ByteBuffer"/> 会把托管数组复制到 Java 侧，读回的数据留在副本里）。
/// 读写模式对同一描述符 <c>dup</c> 出独立的输入流，两个通道共享文件偏移；不支持原子替换。
/// 所有阻塞 Java IO 都放在线程池，调用方（UI 线程）不会被文件系统或云 Provider 卡住。
/// </summary>
internal sealed class AndroidRandomAccessSession : IStorageRandomAccessSession
{
    private readonly object _gate = new();
    private readonly FileChannel? _readChannel;
    private readonly FileChannel? _writeChannel;
    private readonly JavaFileInputStream? _input;
    private readonly JavaFileOutputStream? _output;
    private readonly JavaFileDescriptor _fileDescriptor;
    private readonly StorageAccessMode _mode;
    private readonly string _name;
    private readonly long? _knownLength;
    private bool _disposed;

    private AndroidRandomAccessSession(
        FileChannel? readChannel,
        FileChannel? writeChannel,
        JavaFileInputStream? input,
        JavaFileOutputStream? output,
        JavaFileDescriptor fileDescriptor,
        StorageAccessMode mode,
        string name,
        long? knownLength,
        DateTimeOffset? knownModified)
    {
        _readChannel = readChannel;
        _writeChannel = writeChannel;
        _input = input;
        _output = output;
        _fileDescriptor = fileDescriptor;
        _mode = mode;
        _name = name;
        _knownLength = knownLength;
        Revision = new StorageRevision(knownLength, knownModified);
    }

    /// <summary>
    /// 打开 SAF 文档的随机访问会话；描述符不可用（例如云端 Provider 不提供文件、目标只读）时
    /// 返回 <see cref="StorageException"/> 的明确错误，而不是吞掉异常。
    /// </summary>
    /// <param name="resolver">内容解析器。</param>
    /// <param name="uri">文档 Uri。</param>
    /// <param name="mode">只读或读写。</param>
    /// <param name="knownSize">列表投影里已知的字节数（描述符不支持取长度时兜底）。</param>
    /// <param name="knownModified">列表投影里已知的修改时间。</param>
    /// <param name="name">显示名（错误信息用）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>随机访问会话。</returns>
    public static async Task<IStorageRandomAccessSession> OpenAsync(
        ContentResolver resolver,
        AndroidUri uri,
        StorageAccessMode mode,
        ulong? knownSize,
        DateTimeOffset? knownModified,
        string name,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            return await Task.Run(
                () => OpenCore(resolver, uri, mode, knownSize, knownModified, name, cancellationToken),
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not StorageException and not OperationCanceledException)
        {
            throw AndroidStorageSupport.Wrap(exception, $"打开 {name}");
        }
    }

    private static IStorageRandomAccessSession OpenCore(
        ContentResolver resolver,
        AndroidUri uri,
        StorageAccessMode mode,
        ulong? knownSize,
        DateTimeOffset? knownModified,
        string name,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ParcelFileDescriptor? descriptor = null;
        ParcelFileDescriptor? readDescriptor = null;
        JavaFileInputStream? input = null;
        JavaFileOutputStream? output = null;
        try
        {
            string accessMode = mode == StorageAccessMode.Read ? "r" : "rw";
            descriptor = resolver.OpenFileDescriptor(uri, accessMode)
                ?? throw new StorageException(StorageError.ObjectGone, $"{name} 无法打开");
            var fileDescriptor = descriptor.FileDescriptor
                ?? throw new StorageException(StorageError.IoError, $"{name} 缺少文件描述符");

            if (mode == StorageAccessMode.Read)
            {
                input = new ParcelFileDescriptor.AutoCloseInputStream(descriptor);
            }
            else
            {
                // 写入后需要回读校验：dup 出独立输入流，两个 fd 共享文件偏移，定位后各自搬运字节。
                readDescriptor = descriptor.Dup()
                    ?? throw new StorageException(StorageError.IoError, $"{name} 无法复制文件描述符");
                input = new ParcelFileDescriptor.AutoCloseInputStream(readDescriptor);
                output = new ParcelFileDescriptor.AutoCloseOutputStream(descriptor);
            }

            var readChannel = input?.Channel;
            var writeChannel = output?.Channel;
            if (readChannel == null && writeChannel == null)
                throw new StorageException(StorageError.IoError, $"{name} 缺少文件通道");
            if (mode != StorageAccessMode.Read && (readChannel == null || writeChannel == null))
                throw new StorageException(StorageError.IoError, $"{name} 缺少读写通道");

            var lengthChannel = readChannel ?? writeChannel!;
            long? length = null;
            try
            {
                long size = lengthChannel.Size();
                if (size >= 0) length = size;
            }
            catch (Exception)
            {
                // 部分 Provider 的描述符不支持取长度；继续用 fstat / 列表投影兜底。
            }
            if (length == null)
            {
                try
                {
                    long statSize = descriptor.StatSize;
                    if (statSize >= 0) length = statSize;
                }
                catch (Exception)
                {
                    // fstat 也不可用时接受列表投影。
                }
            }
            if (length == null && knownSize is { } projected)
                length = projected > long.MaxValue ? long.MaxValue : (long)projected;

            // AutoClose* 流接管描述符（含 dup 出的输入描述符）的关闭。
            descriptor = null;
            readDescriptor = null;
            cancellationToken.ThrowIfCancellationRequested();
            return new AndroidRandomAccessSession(
                readChannel, writeChannel, input, output, fileDescriptor, mode, name, length, knownModified);
        }
        catch
        {
            try { output?.Dispose(); } catch (Exception) { }
            try { input?.Dispose(); } catch (Exception) { }
            try { readDescriptor?.Dispose(); } catch (Exception) { }
            try { descriptor?.Dispose(); } catch (Exception) { }
            throw;
        }
    }

    /// <summary>当前长度：优先取通道实际长度，其次用打开时的列表投影，二者都没有则明确不支持。</summary>
    public long Length
    {
        get
        {
            lock (_gate)
            {
                ThrowIfDisposed();
                Exception? failure = null;
                try
                {
                    long size = (_readChannel ?? _writeChannel!).Size();
                    if (size >= 0) return size;
                }
                catch (Exception exception)
                {
                    failure = exception;
                }
                if (_knownLength is { } known) return known;
                if (failure != null)
                    throw AndroidStorageSupport.Wrap(failure, $"读取 {_name} 长度");
                throw StorageException.Unsupported("读取文件长度");
            }
        }
    }

    /// <summary>打开时的长度与修改时间快照（写入后不自动更新）。</summary>
    public StorageRevision? Revision { get; }

    /// <summary>从指定偏移读取；返回 0 表示已到文件末尾。</summary>
    public async ValueTask<int> ReadAsync(
        long offset,
        Memory<byte> buffer,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfDisposed();
        if (offset < 0) throw new ArgumentOutOfRangeException(nameof(offset));
        if (buffer.IsEmpty) return 0;

        int read = await Task.Run(() => ReadCore(offset, buffer), cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        return read;
    }

    /// <summary>在指定偏移写入；只读会话写入映射为 <see cref="StorageError.ReadOnly"/>。</summary>
    public async ValueTask WriteAsync(
        long offset,
        ReadOnlyMemory<byte> buffer,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfDisposed();
        if (offset < 0) throw new ArgumentOutOfRangeException(nameof(offset));
        if (_mode == StorageAccessMode.Read)
            throw new StorageException(StorageError.ReadOnly, $"{_name} 以只读方式打开，不能写入");
        if (buffer.IsEmpty) return;

        await Task.Run(() => WriteCore(offset, buffer), cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
    }

    /// <summary>
    /// 调整文件长度（截断或扩展）。只读会话映射为 <see cref="StorageError.ReadOnly"/>；
    /// 长度为负或 Provider 不支持截断时给出 typed <see cref="StorageException"/>，成功后强制落盘。
    /// </summary>
    /// <param name="length">目标长度（字节）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    public async ValueTask SetLengthAsync(long length, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfDisposed();
        if (length < 0)
            throw new StorageException(StorageError.IoError, $"无效的文件长度：{length}");
        if (_mode == StorageAccessMode.Read)
            throw new StorageException(StorageError.ReadOnly, $"{_name} 以只读方式打开，不能调整文件长度");

        await Task.Run(() => SetLengthCore(length), cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
    }

    /// <summary>写入模式下强制落盘（通道 Force + 文件描述符 Sync）；只读会话无事可做。</summary>
    public async ValueTask FlushAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_mode == StorageAccessMode.Read) return;
        await Task.Run(FlushCore, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
    }

    /// <summary>释放通道、流与描述符；重复释放安全。</summary>
    public ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            if (_disposed) return default;
            _disposed = true;
            try { _input?.Dispose(); } catch (Exception) { }
            try { _output?.Dispose(); } catch (Exception) { }
        }
        return default;
    }

    private int ReadCore(long offset, Memory<byte> buffer)
    {
        if (MemoryMarshal.TryGetArray<byte>(buffer, out var segment) && segment.Array is { } array)
            return ReadInto(offset, array, segment.Offset, segment.Count);

        byte[] temp = new byte[buffer.Length];
        int read = ReadInto(offset, temp, 0, temp.Length);
        temp.AsMemory(0, read).CopyTo(buffer);
        return read;
    }

    private int ReadInto(long offset, byte[] array, int arrayOffset, int count)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            try
            {
                var channel = _readChannel
                    ?? throw new StorageException(StorageError.NotSeekable, $"{_name} 以只写方式打开，不能读取");
                channel.Position(offset);
                int read = _input!.Read(array, arrayOffset, count);
                return read < 0 ? 0 : read;
            }
            catch (Exception exception) when (exception is not StorageException)
            {
                throw AndroidStorageSupport.Wrap(exception, $"读取 {_name}");
            }
        }
    }

    private void WriteCore(long offset, ReadOnlyMemory<byte> buffer)
    {
        if (MemoryMarshal.TryGetArray(buffer, out var segment) && segment.Array is { } array)
        {
            WriteFrom(offset, array, segment.Offset, segment.Count);
            return;
        }

        byte[] temp = buffer.ToArray();
        WriteFrom(offset, temp, 0, temp.Length);
    }

    private void WriteFrom(long offset, byte[] array, int arrayOffset, int count)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            try
            {
                var channel = _writeChannel
                    ?? throw new StorageException(StorageError.NotSeekable, $"{_name} 没有可写通道");
                channel.Position(offset);
                _output!.Write(array, arrayOffset, count);
            }
            catch (Exception exception) when (exception is not StorageException)
            {
                throw AndroidStorageSupport.Wrap(exception, $"写入 {_name}");
            }
        }
    }

    private void SetLengthCore(long length)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            try
            {
                var channel = _writeChannel
                    ?? throw new StorageException(StorageError.NotSeekable, $"{_name} 没有可写通道");
                channel.Truncate(length);
                channel.Force(true);
                _fileDescriptor.Sync();
            }
            catch (Exception exception) when (exception is not StorageException)
            {
                throw WrapSetLengthFailure(exception);
            }
        }
    }

    /// <summary>把 ftruncate 的 Provider 失败归类为 typed 异常：只读、不支持截断或一般 IO。</summary>
    private StorageException WrapSetLengthFailure(Exception exception)
    {
        if (exception is Java.Nio.Channels.NonWritableChannelException)
            return new StorageException(StorageError.ReadOnly, $"{_name} 的通道不支持写入，无法调整长度", exception);

        string message = exception.Message ?? string.Empty;
        if (message.Contains("ENOTSUP", StringComparison.Ordinal)
            || message.Contains("EOPNOTSUPP", StringComparison.Ordinal)
            || message.Contains("Function not implemented", StringComparison.OrdinalIgnoreCase)
            || message.Contains("Operation not supported", StringComparison.OrdinalIgnoreCase))
            return new StorageException(StorageError.Unsupported, $"当前存储不支持调整 {_name} 的长度", exception);

        return AndroidStorageSupport.Wrap(exception, $"调整 {_name} 长度");
    }

    private void FlushCore()
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            try
            {
                _writeChannel!.Force(true);
                _fileDescriptor.Sync();
            }
            catch (Exception exception)
            {
                throw AndroidStorageSupport.Wrap(exception, $"写入 {_name}");
            }
        }
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(AndroidRandomAccessSession), $"{_name} 的存储会话已关闭");
    }
}
