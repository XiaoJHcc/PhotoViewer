using PhotoViewer.Core.Image.Heif;
using PhotoViewer.Core.Storage;

namespace HeifFastDecodeTest;

internal static class ReadTests
{
    internal static int Run()
    {
        try
        {
            RunAsync().GetAwaiter().GetResult();
            Console.WriteLine("HEIF reads: PASS (seekable/nonseekable, short reads, changing length, cancellation, disposal, allocation)");
            return 0;
        }
        catch (Exception exception)
        {
            Console.WriteLine("HEIF reads: FAIL " + exception);
            return 1;
        }
    }

    private static async Task RunAsync()
    {
        var payload = new byte[8 * 1024 * 1024];
        new Random(42).NextBytes(payload);
        foreach (var (name, seekable, declared, offset) in new (string, bool, long, int)[]
        {
            ("known length", true, payload.Length, 0),
            ("nonseekable", false, 0, 0),
            ("shrinking", true, payload.Length + 1024, 0),
            ("growing", true, payload.Length - 1024, 0),
            ("remaining bytes", true, payload.Length, 123),
        })
        {
            var stream = new ReadStream(payload, seekable, declared, offset);
            var file = new ReadFile(stream);
            var result = await FastHeifDecoder.ReadAllBytesAsync(file);
            Require(result.AsSpan().SequenceEqual(payload.AsSpan(offset)), name + " bytes");
            Require(file.Opens == 1 && stream.Disposed, name + " single open/dispose");
        }
        var empty = new ReadStream([], true, 0);
        Require((await FastHeifDecoder.ReadAllBytesAsync(new ReadFile(empty))).Length == 0 && empty.Disposed, "empty");

        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        var unopened = new ReadFile(new ReadStream(payload, true, payload.Length));
        await ExpectAsync<OperationCanceledException>(() => FastHeifDecoder.ReadAllBytesAsync(unopened, cancelled.Token));
        Require(unopened.OpenToken == cancelled.Token, "open cancellation forwarded");

        using var during = new CancellationTokenSource();
        var interrupted = new ReadStream(payload, true, payload.Length) { CancelAfterRead = during };
        await ExpectAsync<OperationCanceledException>(() => FastHeifDecoder.ReadAllBytesAsync(new ReadFile(interrupted), during.Token));
        Require(interrupted.Disposed, "cancelled stream disposed");

        var failed = new ReadStream(payload, true, payload.Length) { FailAfterFirstRead = true };
        await ExpectAsync<IOException>(() => FastHeifDecoder.ReadAllBytesAsync(new ReadFile(failed)));
        Require(failed.Disposed, "failed stream disposed");

        var oversized = new ReadStream([], true, (long)Array.MaxLength + 1);
        await ExpectAsync<IOException>(() => FastHeifDecoder.ReadAllBytesAsync(new ReadFile(oversized)));
        Require(oversized.Disposed, "oversized stream disposed");

        _ = await FastHeifDecoder.ReadAllBytesAsync(new ReadFile(new ReadStream(payload, true, payload.Length)));
        var measured = new ReadFile(new ReadStream(payload, true, payload.Length));
        long before = GC.GetTotalAllocatedBytes(precise: true);
        var final = await FastHeifDecoder.ReadAllBytesAsync(measured);
        long allocated = GC.GetTotalAllocatedBytes(precise: true) - before;
        Require(final.AsSpan().SequenceEqual(payload), "allocation sample bytes");
        Require(allocated < payload.Length + 256 * 1024, $"known length allocates one file buffer: {allocated} for {payload.Length}");
        Console.WriteLine($"  HEIF 8MiB seekable read allocated={allocated}B payload={payload.Length}B");
    }

    private static async Task ExpectAsync<T>(Func<Task<byte[]>> action) where T : Exception
    {
        try { _ = await action(); }
        catch (T) { return; }
        throw new InvalidOperationException("Expected " + typeof(T).Name);
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class ReadFile(ReadStream stream) : IFileRef
    {
        public string Name => "read.heic";
        public Uri Path => new("file:///read.heic");
        public string? TryGetLocalPath() => null;
        public int Opens { get; private set; }
        public CancellationToken OpenToken { get; private set; }
        public Task<Stream> OpenReadAsync(CancellationToken cancellationToken = default)
        {
            Opens++;
            OpenToken = cancellationToken;
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult<Stream>(stream);
        }
        public Task<StorageItemBasicProperties> GetBasicPropertiesAsync(CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
        public Task<IFolderRef?> GetParentAsync(CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }

    private sealed class ReadStream(byte[] data, bool seekable, long declaredLength, int start = 0) : Stream
    {
        private int _position = start;
        private int _reads;
        public bool Disposed { get; private set; }
        public CancellationTokenSource? CancelAfterRead { get; init; }
        public bool FailAfterFirstRead { get; init; }
        public override bool CanRead => true;
        public override bool CanSeek => seekable;
        public override bool CanWrite => false;
        public override long Length => seekable ? declaredLength : throw new NotSupportedException();
        public override long Position { get => _position; set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
        public override int Read(Span<byte> buffer)
        {
            if (FailAfterFirstRead && _reads > 0) throw new IOException("read failure");
            _reads++;
            int count = Math.Min(Math.Min(buffer.Length, 32768), data.Length - _position);
            data.AsSpan(_position, count).CopyTo(buffer);
            _position += count;
            CancelAfterRead?.Cancel();
            return count;
        }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(Read(buffer.Span));
        }
        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
