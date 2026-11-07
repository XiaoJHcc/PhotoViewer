using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Android.App;
using PhotoViewer.Android.Core;
using PhotoViewer.Core.Color;
using PhotoViewer.Core.Image;
using PhotoViewer.Core.Storage;
using IOPath = System.IO.Path;

namespace PhotoViewer.Android.Native;

/// <summary>
/// RAW 共享读取租约检查（验收用）：fake <see cref="IFileRef"/> 覆盖共读/取消/重试/释放/非寻址流与工作内存口径，
/// 真实 ARW 夹具核对「内嵌预览 + 代理开发」共读一次，以及同路径两对象各自 Full 的逐字节一致。
/// 入口 <see cref="RunRawReadLeaseChecksAsync"/> 由 <c>pv-check</c> 调度器调用，报告落盘由调用方负责。
/// </summary>
internal static partial class AndroidNativeChecks
{
    /// <summary>真实夹具代理档长边（小于 half_size 输出长边，覆盖 half 开发 + 精缩同时驻留）。</summary>
    private const int RawReadCheckProxyLongEdge = 1600;

    /// <param name="rawFixtureDirectory">真实 RAW 夹具目录；缺省 <c>files/pv-check-raw</c>。</param>
    internal static async Task RunRawReadLeaseChecksAsync(Activity activity, string? rawFixtureDirectory = null)
    {
        await RunStepAsync("raw 读取租约：重叠读取只读一次", CheckRawReadSharesOnePassAsync);
        await RunStepAsync("raw 读取租约：单方取消不影响另一消费者", CheckRawReadCancellationIsolationAsync);
        await RunStepAsync("raw 读取租约：读取失败可重试", CheckRawReadFailureRetryAsync);
        await RunStepAsync("raw 读取租约：释放即撤登记", CheckRawReadLeaseReleaseAsync);
        await RunStepAsync("raw 读取租约：非寻址流", CheckRawReadUnknownLengthAsync);
        await RunStepAsync("raw 工作内存：不重复产品账且不溢出", CheckRawWorkBytesEstimateAsync);
        await RunStepAsync("raw 读取租约：真实夹具共读一次", () => CheckRawFixtureSharedReadAsync(activity, rawFixtureDirectory));
        await RunStepAsync("raw 读取租约：两次 Full 逐字节一致", () => CheckRawIndependentFullMatchAsync(activity, rawFixtureDirectory));
    }

    // ── 受控 fake 文件 ────────────────────────────────────────────────────────

    /// <summary>把"读取已开始但还没返回"固定成确定状态。</summary>
    private sealed class RawReadGate
    {
        private readonly TaskCompletionSource _source = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal volatile bool HasWaiter;

        internal Task WaitAsync(CancellationToken ct)
        {
            HasWaiter = true;
            return _source.Task.WaitAsync(ct);
        }

        internal void Release() => _source.TrySetResult();
    }

    /// <summary>可伪装非寻址、可把首个读取卡在闸门上的流装饰器（真实夹具模式直接包真实流）。</summary>
    private sealed class RawReadGatedStream(Stream inner, RawReadGate? gate, bool? seekableOverride = null) : Stream
    {
        private int _gated;

        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => seekableOverride ?? inner.CanSeek;
        public override bool CanWrite => false;
        public override long Length => inner.Length;
        public override long Position
        {
            get => inner.Position;
            set => inner.Position = value;
        }

        public override void Flush() { }

        public override int Read(byte[] buffer, int offset, int count)
            => ReadAsync(buffer.AsMemory(offset, count), CancellationToken.None).AsTask().GetAwaiter().GetResult();

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (gate != null && Interlocked.Exchange(ref _gated, 1) == 0)
                await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            return await inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
        }

        public override long Seek(long offset, SeekOrigin origin)
            => CanSeek ? inner.Seek(offset, origin) : throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing) inner.Dispose();
            base.Dispose(disposing);
        }
    }

    /// <summary>合成内容的受控文件引用：统计打开次数、可注入打开失败、可伪装非寻址。</summary>
    private sealed class RawReadProbeFile(byte[] content, bool seekable, int failOpenNumber, RawReadGate? gate) : IFileRef
    {
        private int _opens;

        internal int Opens => Volatile.Read(ref _opens);

        public string Name => "raw-read-probe.bin";

        public Uri Path { get; } = new("file:///pv-check/raw-read-probe.bin");

        public string? TryGetLocalPath() => null;

        public Task<Stream> OpenReadAsync(CancellationToken cancellationToken = default)
        {
            int number = Interlocked.Increment(ref _opens);
            if (failOpenNumber > 0 && number == failOpenNumber)
                throw new IOException("injected raw read open failure");
            return Task.FromResult<Stream>(
                new RawReadGatedStream(new MemoryStream(content, writable: false), gate, seekable));
        }

        public Task<StorageItemBasicProperties> GetBasicPropertiesAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new StorageItemBasicProperties((ulong)content.Length, null));

        public Task<IFolderRef?> GetParentAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IFolderRef?>(null);
    }

    /// <summary>真实夹具引用：统计打开次数，首个读取卡在闸门上，其余原样转发。</summary>
    private sealed class RawReadCountingFile(IFileRef inner, RawReadGate? gate) : IFileRef
    {
        private int _opens;

        internal int Opens => Volatile.Read(ref _opens);

        public string Name => inner.Name;

        public Uri Path => inner.Path;

        public StorageItemId StorageId => inner.StorageId;

        public StorageCapabilities Capabilities => inner.Capabilities;

        public string? TryGetLocalPath() => inner.TryGetLocalPath();

        public async Task<Stream> OpenReadAsync(CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _opens);
            var stream = await inner.OpenReadAsync(cancellationToken).ConfigureAwait(false);
            return new RawReadGatedStream(stream, gate);
        }

        public Task<StorageItemBasicProperties> GetBasicPropertiesAsync(CancellationToken cancellationToken = default)
            => inner.GetBasicPropertiesAsync(cancellationToken);

        public Task<IFolderRef?> GetParentAsync(CancellationToken cancellationToken = default)
            => inner.GetParentAsync(cancellationToken);
    }

    // ── 步骤 ──────────────────────────────────────────────────────────────────

    /// <summary>重叠读取：只打开一次、只读一次、共用同一份池化缓冲。</summary>
    private static async Task CheckRawReadSharesOnePassAsync()
    {
        var content = CreateRawReadProbeContent(1_500_000);
        var file = new RawReadProbeFile(content, seekable: true, failOpenNumber: 0, gate: null);
        var before = AndroidRawReadStats.Snapshot();

        bool ready, sameBuffer, bytesMatch;
        var first = AndroidRawReadHandle.Open(file, DecodePriority.P2_Prefetch);
        var second = AndroidRawReadHandle.Open(file, DecodePriority.P2_Prefetch);
        string detail;
        try
        {
            var results = await Task.WhenAll(ReadRawProbeFileAsync(first), ReadRawProbeFileAsync(second));
            var left = results[0];
            var right = results[1];
            // 字节比较必须在仍持有租约时做：最后一个租约释放后缓冲已归还池中，不能再读。
            bool leftBytes = left.Bytes != null && left.Bytes.AsSpan(0, content.Length).SequenceEqual(content);
            bool rightBytes = right.Bytes != null && right.Bytes.AsSpan(0, content.Length).SequenceEqual(content);
            ready = left.Status == AndroidRawReadStatus.Ready && right.Status == AndroidRawReadStatus.Ready
                && left.Length == content.Length && right.Length == content.Length;
            sameBuffer = left.Bytes != null && ReferenceEquals(left.Bytes, right.Bytes);
            bytesMatch = leftBytes && rightBytes;
            detail = $"状态={left.Status}/{right.Status} 长度={left.Length}/{right.Length} 同缓冲={sameBuffer} 字节一致={bytesMatch}";
        }
        finally
        {
            first.Dispose();
            second.Dispose();
        }

        var after = AndroidRawReadStats.Snapshot();
        Check("raw 读取租约 重叠读取共用同一份缓冲", ready && sameBuffer && bytesMatch, detail);
        Check("raw 读取租约 重叠读取只打开并读一次文件",
            file.Opens == 1 && after.Reads - before.Reads == 1 && after.Bytes - before.Bytes > 0,
            $"opens={file.Opens} reads+={after.Reads - before.Reads} bytes+={after.Bytes - before.Bytes}");
        Check("raw 读取租约 重叠读取记共享命中", after.Shares - before.Shares == 1,
            $"shares+={after.Shares - before.Shares}（应 1）");
        Check("raw 读取租约 结束后登记表为空且缓冲已归还",
            after.ActiveEntries == 0 && after.Returned - before.Returned == 1,
            $"active={after.ActiveEntries} returned+={after.Returned - before.Returned}");
    }

    /// <summary>读取中取消一个消费者：取消方退出，另一消费者仍拿到完整字节。</summary>
    private static async Task CheckRawReadCancellationIsolationAsync()
    {
        var content = CreateRawReadProbeContent(2_000_000);
        var gate = new RawReadGate();
        var file = new RawReadProbeFile(content, seekable: true, failOpenNumber: 0, gate: gate);
        var before = AndroidRawReadStats.Snapshot();

        var cancelledHandle = AndroidRawReadHandle.Open(file, DecodePriority.P2_Prefetch);
        using var cancellation = new CancellationTokenSource();
        var cancelledTask = Task.Run(() =>
        {
            try
            {
                cancelledHandle.TryGetBytes(cancellation.Token, out _, out var status);
                return status;
            }
            catch (OperationCanceledException)
            {
                return AndroidRawReadStatus.Cancelled;
            }
            finally
            {
                cancelledHandle.Dispose();
            }
        });

        await WaitAsync(() => gate.HasWaiter, "首个消费者开始读取", 5000);

        var survivorHandle = AndroidRawReadHandle.Open(file, DecodePriority.P2_Prefetch);
        var survivorTask = Task.Run(() =>
        {
            try
            {
                var bytes = survivorHandle.TryGetBytes(CancellationToken.None, out int length, out var status);
                // 与第一处一样：字节比较在仍持有租约时完成（缓冲归还后不再读）。
                bool ok = bytes != null && status == AndroidRawReadStatus.Ready && length == content.Length
                    && bytes.AsSpan(0, content.Length).SequenceEqual(content);
                return (Ok: ok, Length: length, Status: status);
            }
            finally
            {
                survivorHandle.Dispose();
            }
        });

        await WaitAsync(() => AndroidRawReadStats.Snapshot().Shares > before.Shares, "第二个消费者并入租约", 5000);
        cancellation.Cancel();
        var cancelledStatus = await cancelledTask;
        gate.Release();
        var survivor = await survivorTask;

        var after = AndroidRawReadStats.Snapshot();

        Check("raw 读取租约 取消方退出等待", cancelledStatus == AndroidRawReadStatus.Cancelled,
            $"取消方状态={cancelledStatus}（应 {AndroidRawReadStatus.Cancelled}）");
        Check("raw 读取租约 单方取消后另一消费者仍拿到完整字节", survivor.Ok,
            $"状态={survivor.Status} 长度={survivor.Length}/{content.Length} 字节一致={survivor.Ok}");
        Check("raw 读取租约 单方取消不中断共享读取",
            file.Opens == 1 && after.Reads - before.Reads == 1 && after.Cancels - before.Cancels == 0,
            $"opens={file.Opens} reads+={after.Reads - before.Reads} cancels+={after.Cancels - before.Cancels}");
        Check("raw 读取租约 取消后登记表为空", after.ActiveEntries == 0, $"active={after.ActiveEntries}");
    }

    /// <summary>首次打开抛异常：撤下登记，下一次调用重新打开并读出完整字节。</summary>
    private static async Task CheckRawReadFailureRetryAsync()
    {
        var content = CreateRawReadProbeContent(512_000);
        var file = new RawReadProbeFile(content, seekable: true, failOpenNumber: 1, gate: null);
        var before = AndroidRawReadStats.Snapshot();

        AndroidRawReadStatus failedStatus;
        using (var handle = AndroidRawReadHandle.Open(file, DecodePriority.P2_Prefetch))
            handle.TryGetBytes(CancellationToken.None, out _, out failedStatus);
        var afterFailure = AndroidRawReadStats.Snapshot();

        bool retriedOk;
        int retriedLength;
        AndroidRawReadStatus retriedStatus;
        using (var handle = AndroidRawReadHandle.Open(file, DecodePriority.P2_Prefetch))
        {
            var retried = handle.TryGetBytes(CancellationToken.None, out retriedLength, out retriedStatus);
            retriedOk = retriedStatus == AndroidRawReadStatus.Ready && retried != null
                && retriedLength == content.Length && retried.AsSpan(0, content.Length).SequenceEqual(content);
        }
        var afterRetry = AndroidRawReadStats.Snapshot();

        Check("raw 读取租约 读取失败如实返回失败态", failedStatus == AndroidRawReadStatus.Failed,
            $"状态={failedStatus} failures+={afterFailure.Failures - before.Failures}");
        Check("raw 读取租约 失败后撤下登记允许重试", afterFailure.ActiveEntries == 0,
            $"active={afterFailure.ActiveEntries}");
        Check("raw 读取租约 重试重新打开文件并读出完整字节",
            retriedOk && file.Opens == 2 && afterRetry.Reads - before.Reads == 1,
            $"状态={retriedStatus} opens={file.Opens} reads+={afterRetry.Reads - before.Reads} "
            + $"字节一致={retriedOk}");
        Check("raw 读取租约 首轮失败不计入读取字节", afterFailure.Reads == before.Reads,
            $"reads+={afterFailure.Reads - before.Reads}");
    }

    /// <summary>最后一个租约释放即归还缓冲并移出登记表；未开始读取就释放同样要撤下登记。</summary>
    private static Task CheckRawReadLeaseReleaseAsync()
    {
        var content = CreateRawReadProbeContent(1_000_000);
        var file = new RawReadProbeFile(content, seekable: true, failOpenNumber: 0, gate: null);
        var before = AndroidRawReadStats.Snapshot();

        using (var handle = AndroidRawReadHandle.Open(file, DecodePriority.P2_Prefetch))
            handle.TryGetBytes(CancellationToken.None, out _, out _);
        var afterRelease = AndroidRawReadStats.Snapshot();

        bool reloadedOk;
        using (var handle = AndroidRawReadHandle.Open(file, DecodePriority.P2_Prefetch))
        {
            var reloaded = handle.TryGetBytes(CancellationToken.None, out int length, out _);
            reloadedOk = reloaded != null && length == content.Length
                && reloaded.AsSpan(0, content.Length).SequenceEqual(content);
        }
        var afterReload = AndroidRawReadStats.Snapshot();

        // Open 后不读直接释放：没有读取任务收尾，登记与取消源必须由这次释放撤掉。
        var abandoned = new RawReadProbeFile(content, seekable: true, failOpenNumber: 0, gate: null);
        var beforeAbandon = AndroidRawReadStats.Snapshot();
        AndroidRawReadHandle.Open(abandoned, DecodePriority.P2_Prefetch).Dispose();
        var afterAbandon = AndroidRawReadStats.Snapshot();

        Check("raw 读取租约 最后释放后登记表为空", afterRelease.ActiveEntries == 0,
            $"active={afterRelease.ActiveEntries}");
        Check("raw 读取租约 最后释放归还缓冲", afterRelease.Returned - before.Returned == 1,
            $"returned+={afterRelease.Returned - before.Returned}（应 1）");
        Check("raw 读取租约 释放后重新读取文件而非复用旧字节",
            file.Opens == 2 && afterReload.Reads - afterRelease.Reads == 1 && afterReload.Shares == afterRelease.Shares && reloadedOk,
            $"opens={file.Opens} reads+={afterReload.Reads - afterRelease.Reads} "
            + $"shares+={afterReload.Shares - afterRelease.Shares} 字节一致={reloadedOk}");
        Check("raw 读取租约 未开始读取就释放会撤下登记且不做 I/O",
            afterAbandon.ActiveEntries == 0 && abandoned.Opens == 0
            && afterAbandon.Reads == beforeAbandon.Reads && afterAbandon.Returned == beforeAbandon.Returned,
            $"active={afterAbandon.ActiveEntries} opens={abandoned.Opens} reads+={afterAbandon.Reads - beforeAbandon.Reads}");
        return Task.CompletedTask;
    }

    /// <summary>非寻址流走定长收集路径，读出的字节与源一致。</summary>
    private static Task CheckRawReadUnknownLengthAsync()
    {
        var content = CreateRawReadProbeContent(300_000);
        var file = new RawReadProbeFile(content, seekable: false, failOpenNumber: 0, gate: null);
        var before = AndroidRawReadStats.Snapshot();

        bool ok;
        int length;
        AndroidRawReadStatus status;
        using (var handle = AndroidRawReadHandle.Open(file, DecodePriority.P2_Prefetch))
        {
            var bytes = handle.TryGetBytes(CancellationToken.None, out length, out status);
            ok = status == AndroidRawReadStatus.Ready && bytes != null
                && length == content.Length && bytes.AsSpan(0, content.Length).SequenceEqual(content);
        }
        var after = AndroidRawReadStats.Snapshot();

        Check("raw 读取租约 非寻址流读出完整字节", ok,
            $"状态={status} 长度={length}/{content.Length} opens={file.Opens} reads+={after.Reads - before.Reads}");
        return Task.CompletedTask;
    }

    /// <summary>工作内存申请额：不得重复计入已由别处记账的字节；half 开发后精缩时中间缓冲仍计入；极端尺寸不为负。</summary>
    private static Task CheckRawWorkBytesEstimateAsync()
    {
        const int width = 7008;
        const int height = 4672;
        long halfWidth = (width + 1) / 2;
        long halfHeight = (height + 1) / 2;
        long peakFull = AndroidRawDecoder.EstimatePeakBytes(width, height, false);
        long peakHalf = AndroidRawDecoder.EstimatePeakBytes(width, height, true);

        long workFull = AndroidRawDecoder.EstimateWorkBytes(width, height, null);
        long workHalf = AndroidRawDecoder.EstimateWorkBytes(width, height, (int)halfWidth);
        long workHalfScaled = AndroidRawDecoder.EstimateWorkBytes(width, height, RawReadCheckProxyLongEdge);
        long workFullScaled = AndroidRawDecoder.EstimateWorkBytes(4000, 3000, 2500);

        Check("raw 工作内存 全分辨率扣掉调用方已记账产物",
            workFull == peakFull - (long)width * height * 4,
            $"work={workFull} peak={peakFull} product={(long)width * height * 4}");
        Check("raw 工作内存 half 产物已记账时只扣一次",
            workHalf == peakHalf - halfWidth * halfHeight * 4,
            $"work={workHalf} peak={peakHalf} product={halfWidth * halfHeight * 4}");
        Check("raw 工作内存 half 后精缩不抵扣中间缓冲", workHalfScaled == peakHalf,
            $"work={workHalfScaled} peak={peakHalf} proxy={RawReadCheckProxyLongEdge}");
        Check("raw 工作内存 全分辨率后精缩不抵扣产物",
            workFullScaled == AndroidRawDecoder.EstimatePeakBytes(4000, 3000, false),
            $"work={workFullScaled} peak={AndroidRawDecoder.EstimatePeakBytes(4000, 3000, false)}");
        Check("raw 工作内存 非法尺寸为零", AndroidRawDecoder.EstimateWorkBytes(0, 0, null) == 0,
            $"{AndroidRawDecoder.EstimateWorkBytes(0, 0, null)}");
        long hugeWork = AndroidRawDecoder.EstimateWorkBytes(int.MaxValue, int.MaxValue, null);
        long hugePeak = AndroidRawDecoder.EstimatePeakBytes(int.MaxValue, int.MaxValue, true);
        Check("raw 工作内存 极端尺寸不溢出成负数",
            hugeWork >= 0 && hugePeak > 0,
            $"work={hugeWork}（饱和后与实际产物相抵，不为负）peak={hugePeak}");
        return Task.CompletedTask;
    }

    /// <summary>真实 ARW：预览先读到闸门停住，代理开发并入同一租约后放行，断言只打开/只读一次且两条路径都出图。</summary>
    private static async Task CheckRawFixtureSharedReadAsync(Activity activity, string? fixtureDirectory)
    {
        string folder = fixtureDirectory ?? IOPath.Combine(activity.FilesDir!.AbsolutePath, "pv-check-raw");
        string? raw = Directory.Exists(folder)
            ? Directory.GetFiles(folder).FirstOrDefault(path => path.EndsWith(".arw", StringComparison.OrdinalIgnoreCase))
            : null;
        if (raw == null)
        {
            Check("raw 读取租约 真实夹具", false, $"缺少 {folder}/\\*.ARW（push 后 run-as cp 到 files/pv-check-raw）");
            return;
        }

        var gate = new RawReadGate();
        var counting = new RawReadCountingFile(new AndroidFileRef(raw), gate);
        var target = AndroidDisplayCapabilities.PresentationColorSpace;
        var before = AndroidRawReadStats.Snapshot();

        DecodedImage? preview = null;
        DecodedImage? developed = null;
        try
        {
            using (DecodeContext.Enter(DecodePriority.P0_Current))
            {
                var previewTask = Task.Run(() => AndroidRawDecoder.DecodeEmbeddedPreview(counting, target, CancellationToken.None));
                await WaitForRawReadStartAsync(gate, previewTask, "真实夹具读取开始", 20000);
                var developTask = Task.Run(() => AndroidRawDecoder.Decode(counting, RawReadCheckProxyLongEdge, target, CancellationToken.None));
                await WaitForRawReadShareAsync(developTask, before.Shares, "代理开发并入同一租约", 20000);
                gate.Release();
                preview = await previewTask;
                developed = await developTask;
            }
        }
        finally
        {
            gate.Release();
        }

        var after = AndroidRawReadStats.Snapshot();
        bool proxySized = developed != null
            && Math.Max(developed.Width, developed.Height) <= RawReadCheckProxyLongEdge + 1
            && developed.SourceWidth > 0 && developed.SourceHeight > 0;

        Check("raw 读取租约 真实夹具 预览与代理开发共用一次文件读取",
            counting.Opens == 1 && after.Reads - before.Reads == 1,
            $"{IOPath.GetFileName(raw)} opens={counting.Opens} reads+={after.Reads - before.Reads} "
            + $"shares+={after.Shares - before.Shares} bytes+={after.Bytes - before.Bytes}");
        Check("raw 读取租约 真实夹具 内嵌预览出图", preview != null,
            preview == null ? "预览为 null（无内嵌 JPEG 或解码失败）" : $"{preview.Width}x{preview.Height}");
        Check("raw 读取租约 真实夹具 代理开发出图", proxySized,
            developed == null
                ? "代理开发返回 null"
                : $"{developed.Width}x{developed.Height} 源={developed.SourceWidth}x{developed.SourceHeight} 方向={developed.Orientation}");

        preview?.Dispose();
        developed?.Dispose();
        var settled = AndroidRawReadStats.Snapshot();
        Check("raw 读取租约 真实夹具 结束后无持留", settled.ActiveEntries == 0,
            $"active={settled.ActiveEntries} returned+={settled.Returned - before.Returned}");
    }

    /// <summary>
    /// 同一路径的两个 <see cref="AndroidFileRef"/>：先用一条活跃共享租约罩住第一个，让它的 Full 开发并入同一次读取；
    /// 第二个对象作对照独立读取，两份 Full 必须几何/方向/颜色一致且每行有效像素逐字节相同。
    /// </summary>
    private static async Task CheckRawIndependentFullMatchAsync(Activity activity, string? fixtureDirectory)
    {
        string folder = fixtureDirectory ?? IOPath.Combine(activity.FilesDir!.AbsolutePath, "pv-check-raw");
        string? raw = Directory.Exists(folder)
            ? Directory.GetFiles(folder).FirstOrDefault(path => path.EndsWith(".arw", StringComparison.OrdinalIgnoreCase))
            : null;
        if (raw == null)
        {
            Check("raw 两次 Full 对照", false, $"缺少 {folder}/\\*.ARW（push 后 run-as cp 到 files/pv-check-raw）");
            return;
        }

        var target = AndroidDisplayCapabilities.PresentationColorSpace;
        var sharedFile = new AndroidFileRef(raw);
        var independentFile = new AndroidFileRef(raw);
        var before = AndroidRawReadStats.Snapshot();

        DecodedImage? shared = null;
        DecodedImage? independent = null;
        string? holdDetail = null;
        try
        {
            using (DecodeContext.Enter(DecodePriority.P0_Current))
            {
                var hold = AndroidRawReadHandle.Open(sharedFile, DecodePriority.P0_Current);
                try
                {
                    // 预读与 Full 开发放在同一个工作项里：租约保持活跃，Full 必然并入这次读取而不是自己再读一遍。
                    shared = await Task.Run(() =>
                    {
                        if (hold.TryGetBytes(CancellationToken.None, out _, out _) == null) return null;
                        return AndroidRawDecoder.Decode(sharedFile, null, target, CancellationToken.None);
                    });
                }
                finally
                {
                    holdDetail = hold.Detail;
                    hold.Dispose();
                }

                independent = await Task.Run(() => AndroidRawDecoder.Decode(independentFile, null, target, CancellationToken.None));
            }

            var after = AndroidRawReadStats.Snapshot();
            long reads = after.Reads - before.Reads;
            long shares = after.Shares - before.Shares;
            bool both = shared != null && independent != null;

            Check("raw 两次 Full 对照 两份都出图", both,
                $"shared={(shared == null ? $"null（{holdDetail ?? "无详情"}）" : $"{shared.Width}x{shared.Height}")} "
                + $"independent={(independent == null ? "null" : $"{independent.Width}x{independent.Height}")}");
            Check("raw 两次 Full 对照 同对象并入共享租约", shares == 1 && shared != null, $"shares+={shares}");
            Check("raw 两次 Full 对照 另一对象独立读取", reads == 2 && independent != null,
                $"reads+={reads}（预热 1 + 对照 1）");

            (bool Geometry, bool Orientation, bool Color, int Mismatched, string? Detail) compared = default;
            if (both) compared = await Task.Run(() => CompareRawFullDecodes(shared!, independent!, target));

            Check("raw 两次 Full 对照 源几何一致", both && compared.Geometry,
                both
                    ? $"{shared!.Width}x{shared.Height} 源={shared.SourceWidth}x{shared.SourceHeight} / "
                      + $"{independent!.Width}x{independent.Height} 源={independent.SourceWidth}x{independent.SourceHeight}"
                    : "无产物");
            Check("raw 两次 Full 对照 方向一致", both && compared.Orientation,
                both ? $"{shared!.Orientation}/{independent!.Orientation}" : "无产物");
            Check("raw 两次 Full 对照 颜色编码一致", both && compared.Color,
                both
                    ? $"{shared!.Encoding}/{shared.ColorSpace} vs {independent!.Encoding}/{independent.ColorSpace} 目标={target}"
                    : "无产物");
            Check("raw 两次 Full 对照 每行有效像素逐字节一致", both && compared.Mismatched == 0,
                !both ? "无产物"
                    : compared.Mismatched == 0
                        ? $"{shared!.Height} 行 × {shared.Width * 4} 字节"
                        : compared.Detail ?? "未知差异");
        }
        finally
        {
            shared?.Dispose();
            independent?.Dispose();
        }

        var settled = AndroidRawReadStats.Snapshot();
        Check("raw 两次 Full 对照 结束后无租约持留", settled.ActiveEntries == 0,
            $"active={settled.ActiveEntries}");
    }

    /// <summary>逐行比较有效像素（每行 Width*4 字节，不碰行距填充与池尾）。</summary>
    private static (bool Geometry, bool Orientation, bool Color, int Mismatched, string? Detail) CompareRawFullDecodes(
        DecodedImage left, DecodedImage right, ImageColorSpace target)
    {
        bool geometry = left.SourceWidth == right.SourceWidth && left.SourceHeight == right.SourceHeight
            && left.Width == left.SourceWidth && left.Height == left.SourceHeight
            && right.Width == right.SourceWidth && right.Height == right.SourceHeight;
        bool orientation = left.Orientation == right.Orientation;
        bool color = left.Encoding == PixelEncoding.Rgba8 && right.Encoding == PixelEncoding.Rgba8
            && left.ColorSpace == target && right.ColorSpace == target;

        if (left.Width != right.Width || left.Height != right.Height)
            return (geometry, orientation, color, -1, $"几何不同：{left.Width}x{left.Height} / {right.Width}x{right.Height}");

        int mismatched = 0;
        string? first = null;
        for (int row = 0; row < left.Height; row++)
        {
            var leftRow = left.Pixels.AsSpan(row * left.Stride, left.Width * 4);
            var rightRow = right.Pixels.AsSpan(row * right.Stride, right.Width * 4);
            if (leftRow.SequenceEqual(rightRow)) continue;
            mismatched++;
            if (first != null) continue;
            int index = 0;
            while (index < leftRow.Length && leftRow[index] == rightRow[index]) index++;
            first = $"行{row} 字节{index}：{leftRow[index]}/{rightRow[index]}";
        }
        return (geometry, orientation, color, mismatched, first == null ? null : $"{mismatched} 行不一致，首个 {first}");
    }

    // ── 工具 ──────────────────────────────────────────────────────────────────

    /// <summary>在产品路径同样的同步取字节方式下等待一个句柄。</summary>
    private static Task<(byte[]? Bytes, int Length, AndroidRawReadStatus Status)> ReadRawProbeFileAsync(
        AndroidRawReadHandle handle)
        => Task.Run(() =>
        {
            var bytes = handle.TryGetBytes(CancellationToken.None, out int length, out var status);
            return (bytes, length, status);
        });

    private static byte[] CreateRawReadProbeContent(int length)
    {
        var content = new byte[length];
        for (int i = 0; i < content.Length; i++) content[i] = (byte)(i * 31 + (i >> 8));
        return content;
    }

    /// <summary>等首个真实读取卡到闸门；解码任务提前结束（原生库缺失等）时立即返回，由断言如实报错。</summary>
    private static async Task WaitForRawReadStartAsync(RawReadGate gate, Task probe, string label, int timeoutMs)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            if (gate.HasWaiter || probe.IsCompleted) return;
            await Task.Delay(20);
        }
        throw new TimeoutException($"等待超时：{label}");
    }

    /// <summary>等代理开发并入共享租约；解码任务提前结束则立即返回。</summary>
    private static async Task WaitForRawReadShareAsync(Task develop, long sharesBefore, string label, int timeoutMs)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            if (develop.IsCompleted || AndroidRawReadStats.Snapshot().Shares > sharesBefore) return;
            await Task.Delay(20);
        }
        throw new TimeoutException($"等待超时：{label}");
    }
}
