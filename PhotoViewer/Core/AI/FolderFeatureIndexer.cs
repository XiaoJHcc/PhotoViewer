using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using PhotoViewer.Core.Database;
using PhotoViewer.Core.Image;
using PhotoViewer.Core.Memory;

namespace PhotoViewer.Core.AI;

/// <summary>
/// 全文件夹批量特征提取调度器。
/// 对 <see cref="ImageFile"/> 列表按指纹聚合后,一轮扫描同时提取 DINO CLS + patch token + CV grid 三类原始数据
/// 并写入 <see cref="PhotoDatabase"/>。同次曝光的 RAW+HEIF/JPG 共享同一指纹,只跑一次。
///
/// 有界流水线:producer 串行评估每组缺失 → DINO 缩略图解码 → 单路推理;CV 全图解码 → 单路计算;
/// 每条路径结束即把不可变结果快照交单 writer 落库(部分成果不等另一路),两路都结束才复核库、推进度、还许可。
/// 解码/推理/写库都不在 UI 线程;内存准入与解码排队由 <see cref="BatchAnalysisContext"/> 承担(P3 门),
/// 本类不按内存压力级整批停顿。
/// </summary>
public sealed class FolderFeatureIndexer
{
    /// <summary>喂给 DINO 的图片短边像素,与 <see cref="DinoFeatureCache"/> 保持一致。</summary>
    private const int FeaturingShortSide = 560;

    /// <summary>在途组上限。</summary>
    private const int InflightGroupLimit = 8;

    /// <summary>job 队列容量(组)。</summary>
    private const int JobQueueCapacity = 8;

    /// <summary>已解码待计算队列容量。</summary>
    private const int ReadyQueueCapacity = 1;

    /// <summary>待写库结果队列容量。</summary>
    private const int ResultQueueCapacity = 8;

    /// <summary>跨实例闸门:同一时刻只允许一条提取流水线占用整份资源。</summary>
    private static readonly SemaphoreSlim _pipelineGate = new(1, 1);

    private int _runClaim;
    private int _ticketSeq;
    private int _completed;
    private int _failed;
    private int _total;
    private long _dinoMs;
    private long _cvMs;
    private long _writeMs;
    private string? _lastError;

    /// <summary>是否正在运行(认领通过后立即为真,收尾必归位)。</summary>
    public bool IsRunning { get; private set; }

    /// <summary>首个失败组的错误信息;无失败时为 null。</summary>
    public string? LastError => _lastError;

    /// <summary>进度事件:每组完成(成功或跳过)后触发。</summary>
    public event Action<IndexProgress>? ProgressChanged;

    /// <summary>
    /// 每次成功写入一组特征后触发（静态，库内容变化是全局事实）；
    /// 分析栏据此对当前照片重新路由，避免把「提取全部」之前的空结果继续贴在诊断瓦片上。
    /// </summary>
    public static event Action? FeaturesWritten;

    /// <summary>
    /// 把文件列表按指纹聚合:同指纹的多个文件合并为一组,代表文件按解码代价升序,
    /// 无法计算指纹的文件每个独占一组且 <see cref="FingerprintGroup.Fingerprint"/> 为空字符串。
    /// </summary>
    public static Task<IReadOnlyList<FingerprintGroup>> GroupByFingerprintAsync(
        IReadOnlyList<ImageFile> files, CancellationToken cancellationToken = default)
    {
        return FingerprintGrouping.GroupByFingerprintAsync(files, cancellationToken);
    }

    /// <summary>查询某指纹三路数据齐备情况。</summary>
    public static Task<MissingParts> EvaluateMissingPartsAsync(string fingerprint)
    {
        return PhotoDatabase.EvaluateMissingPartsAsync(
            fingerprint, DinoModelResources.ModelId, CvGridResult.CurrentVersion);
    }

    /// <summary>对 <paramref name="files"/> 中尚未入库的照片批量提取三类特征。进度按指纹组推进。</summary>
    public Task RunAsync(IReadOnlyList<ImageFile> files) => RunAsync(files, CancellationToken.None);

    /// <summary>
    /// 批量提取。<paramref name="cancellationToken"/> 用于用户/进程/移动端后台终止;面板收起或换目录不取消。
    /// 认领失败(本实例或在途流水线)抛 <see cref="InvalidOperationException"/>,取消以异常上抛。
    /// </summary>
    public Task RunAsync(IReadOnlyList<ImageFile> files, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(files);

        if (Interlocked.CompareExchange(ref _runClaim, 1, 0) != 0)
        {
            Interlocked.CompareExchange(ref _lastError, "批量特征提取已在本实例运行中", null);
            throw new InvalidOperationException("批量特征提取已在本实例运行中");
        }

        if (!_pipelineGate.Wait(0))
        {
            Interlocked.Exchange(ref _runClaim, 0);
            Interlocked.CompareExchange(ref _lastError, "已有另一条批量特征提取流水线在运行", null);
            throw new InvalidOperationException("已有另一条批量特征提取流水线在运行");
        }

        IsRunning = true;
        return Task.Run(() => RunCoreAsync(files, cancellationToken), CancellationToken.None);
    }

    private async Task RunCoreAsync(IReadOnlyList<ImageFile> files, CancellationToken cancellationToken)
    {
        try
        {
            try
            {
                await ExecuteAsync(files, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[FolderFeatureIndexer] aborted: {ex}");
                Interlocked.CompareExchange(ref _lastError, ex.Message, null);
            }
        }
        finally
        {
            IsRunning = false;
            _pipelineGate.Release();
            Interlocked.Exchange(ref _runClaim, 0);
        }
    }

    private async Task ExecuteAsync(IReadOnlyList<ImageFile> files, CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken, BatchAnalysisContext.ApplicationToken);
        var token = linked.Token;
        var applicationToken = BatchAnalysisContext.ApplicationToken;

        // 会话范围一次进入:stage 任务在其后启动,继承同一份工作集准入与共享 CPU 调度器。
        using var session = BatchAnalysisContext.Enter(token);

        _completed = 0;
        _failed = 0;
        _lastError = null;
        _dinoMs = 0;
        _cvMs = 0;
        _writeMs = 0;
        Interlocked.Exchange(ref _ticketSeq, 0);
        var batchClock = Stopwatch.StartNew();

        var groups = await GroupByFingerprintAsync(files, token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();

        _total = groups.Count;
        try { ProgressChanged?.Invoke(new IndexProgress(0, _total, 0)); }
        catch (Exception ex) { Console.WriteLine($"[FolderFeatureIndexer] progress handler: {ex}"); }
        Console.WriteLine($"[FolderFeatureIndexer] batch start groups={_total} files={files.Count}");
        if (groups.Count == 0)
        {
            Console.WriteLine($"[FolderFeatureIndexer] batch end groups=0 elapsed={batchClock.ElapsedMilliseconds}ms");
            return;
        }

        var dinoJobs = Channel.CreateBounded<GroupTicket>(new BoundedChannelOptions(JobQueueCapacity)
        {
            SingleWriter = true,
            SingleReader = false,
            FullMode = BoundedChannelFullMode.Wait,
        });
        var thumbReady = Channel.CreateBounded<DinoReady>(new BoundedChannelOptions(ReadyQueueCapacity)
        {
            SingleWriter = false,
            SingleReader = true,
            FullMode = BoundedChannelFullMode.Wait,
        });
        var cvJobs = Channel.CreateBounded<GroupTicket>(new BoundedChannelOptions(JobQueueCapacity)
        {
            SingleWriter = true,
            SingleReader = true,
            FullMode = BoundedChannelFullMode.Wait,
        });
        var fullReady = Channel.CreateBounded<CvReady>(new BoundedChannelOptions(ReadyQueueCapacity)
        {
            SingleWriter = true,
            SingleReader = true,
            FullMode = BoundedChannelFullMode.Wait,
        });
        var results = Channel.CreateBounded<PathResult>(new BoundedChannelOptions(ResultQueueCapacity)
        {
            SingleWriter = false,
            SingleReader = true,
            FullMode = BoundedChannelFullMode.Wait,
        });

        var inflight = new SemaphoreSlim(InflightGroupLimit, InflightGroupLimit);
        int thumbDecoders = OperatingSystem.IsAndroid() || OperatingSystem.IsIOS() ? 1 : 2;

        var writerTask = WriteResultsAsync(results.Reader, inflight);

        var stages = new List<Task>(thumbDecoders + 7)
        {
            ProduceAsync(groups, dinoJobs.Writer, cvJobs.Writer, results.Writer, inflight, token),
        };

        var thumbDecodeTasks = new List<Task>(thumbDecoders);
        for (int i = 0; i < thumbDecoders; i++)
            thumbDecodeTasks.Add(DecodeThumbnailsAsync(dinoJobs.Reader, thumbReady.Writer, results.Writer, token));
        stages.AddRange(thumbDecodeTasks);
        stages.Add(CompleteWhenDoneAsync(thumbDecodeTasks, thumbReady.Writer));
        stages.Add(InferDinoAsync(thumbReady.Reader, results.Writer, token));

        var fullDecodeTask = DecodeFullAsync(cvJobs.Reader, fullReady.Writer, results.Writer, token);
        stages.Add(fullDecodeTask);
        stages.Add(CompleteWhenDoneAsync(new[] { fullDecodeTask }, fullReady.Writer));
        stages.Add(ComputeCvAsync(fullReady.Reader, results.Writer, token));

        // 任一 stage 或 writer 意外失败立即取消整条流水线,避免其他 stage 永久阻塞在 channel 上。
        foreach (var task in stages)
            CancelOnFault(task, linked);
        CancelOnFault(writerTask, linked);
        _ = writerTask.ContinueWith(task =>
        {
            if (task.IsFaulted) results.Writer.TryComplete(task.Exception);
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);

        Exception? pipelineError = null;
        try
        {
            try
            {
                await Task.WhenAll(stages).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                pipelineError = ex;
            }

            results.Writer.TryComplete();

            try
            {
                await writerTask.ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                pipelineError ??= ex;
            }
        }
        finally
        {
            // 无条件归还:stage 已结束,预备队列里没人消费的租约与在途许可都要还。
            while (thumbReady.Reader.TryRead(out var thumb)) thumb.Lease.Dispose();
            while (fullReady.Reader.TryRead(out var full)) full.Lease.Dispose();
            inflight.Dispose();

            Console.WriteLine(
                $"[FolderFeatureIndexer] batch end groups={_total} ok={_completed - _failed} failed={_failed} " +
                $"elapsed={batchClock.ElapsedMilliseconds}ms dino={Interlocked.Read(ref _dinoMs)}ms " +
                $"cv={Interlocked.Read(ref _cvMs)}ms write={Interlocked.Read(ref _writeMs)}ms");
        }

        if (pipelineError != null)
        {
            Console.WriteLine($"[FolderFeatureIndexer] pipeline failed: {pipelineError}");
            Interlocked.CompareExchange(ref _lastError, pipelineError.Message, null);
        }

        // 取消不当作成功:交给调用方(VM)识别;内部故障取消不算调用方取消。
        if (cancellationToken.IsCancellationRequested || applicationToken.IsCancellationRequested)
            throw new OperationCanceledException(token);
    }

    private static void CancelOnFault(Task task, CancellationTokenSource linked)
    {
        _ = task.ContinueWith(
            t =>
            {
                if (!t.IsFaulted) return;
                try { linked.Cancel(); }
                catch (ObjectDisposedException) { }
            },
            CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    /// <summary>producer:串行评估每组缺失,同一张票据派到 DINO 与 CV 两路。在途组数有界。</summary>
    private async Task ProduceAsync(
        IReadOnlyList<FingerprintGroup> groups,
        ChannelWriter<GroupTicket> dinoJobs,
        ChannelWriter<GroupTicket> cvJobs,
        ChannelWriter<PathResult> results,
        SemaphoreSlim inflight,
        CancellationToken ct)
    {
        bool sessionPrepared = false;
        string? preparationError = null;
        try
        {
            foreach (var group in groups)
            {
                ct.ThrowIfCancellationRequested();

                if (string.IsNullOrEmpty(group.Fingerprint))
                {
                    var orphan = GroupTicket.Unfingerprinted(group);
                    orphan.Id = Interlocked.Increment(ref _ticketSeq);
                    await EmitAsync(results, new PathResult(orphan, cvPath: false)).ConfigureAwait(false);
                    continue;
                }

                await inflight.WaitAsync(ct).ConfigureAwait(false);

                GroupTicket? ticket = null;
                bool keepPermit = false;
                try
                {
                    var missing = await EvaluateMissingPartsAsync(group.Fingerprint).ConfigureAwait(false);
                    if (missing.AnyMissing)
                    {
                        ticket = new GroupTicket(group, missing);
                        ticket.Id = Interlocked.Increment(ref _ticketSeq);
                        keepPermit = true;
                    }
                }
                finally
                {
                    if (!keepPermit) inflight.Release();
                }

                if (ticket == null)
                {
                    var done = GroupTicket.AlreadyComplete(group);
                    done.Id = Interlocked.Increment(ref _ticketSeq);
                    await EmitAsync(results, new PathResult(done, cvPath: false)).ConfigureAwait(false);
                    continue;
                }

                if (ticket.NeedDino)
                {
                    if (!sessionPrepared && preparationError == null)
                    {
                        try
                        {
                            await DinoFeatureExtractor.PrepareBatchSessionAsync(ct).ConfigureAwait(false);
                            sessionPrepared = true;
                        }
                        catch (OperationCanceledException) when (ct.IsCancellationRequested)
                        {
                            throw;
                        }
                        catch (AnalysisMemoryException ex)
                        {
                            preparationError = "DINO 会话内存不足";
                            Console.WriteLine($"[FolderFeatureIndexer] session refused: {ex.Message}");
                        }
                        catch (Exception ex)
                        {
                            preparationError = "DINO 初始化失败";
                            Console.WriteLine($"[FolderFeatureIndexer] session failed: {ex}");
                        }
                    }

                    if (preparationError == null)
                        await dinoJobs.WriteAsync(ticket, ct).ConfigureAwait(false);
                    else
                        await EmitAsync(results, new PathResult(ticket, cvPath: false) { Error = preparationError })
                            .ConfigureAwait(false);
                }
                if (ticket.NeedCv) await cvJobs.WriteAsync(ticket, ct).ConfigureAwait(false);
            }
        }
        finally
        {
            dinoJobs.TryComplete();
            cvJobs.TryComplete();
        }
    }

    /// <summary>DINO 路解码:按解码代价顺序取第一张能出缩略图的伴侣。</summary>
    private async Task DecodeThumbnailsAsync(
        ChannelReader<GroupTicket> jobs,
        ChannelWriter<DinoReady> ready,
        ChannelWriter<PathResult> results,
        CancellationToken ct)
    {
        await foreach (var ticket in jobs.ReadAllAsync(ct).ConfigureAwait(false))
        {
            AnalysisImageLease? lease = null;
            string? error = null;
            var clock = Stopwatch.StartNew();
            try
            {
                foreach (var file in ticket.Group.Files)
                {
                    ct.ThrowIfCancellationRequested();
                    await YieldIfP0Async().ConfigureAwait(false);
                    try
                    {
                        lease = await BitmapLoader.AcquireAnalysisThumbnailAsync(
                            file.File, FeaturingShortSide, ct).ConfigureAwait(false);
                        if (lease != null) break;
                        Console.WriteLine($"[FolderFeatureIndexer] thumbnail miss {file.Name}");
                    }
                    catch (AnalysisMemoryException ex)
                    {
                        // 资源不足与解码失败不同:不再拿别的伴侣反复试。
                        error = "内存不足";
                        Console.WriteLine($"[FolderFeatureIndexer] thumbnail refused {file.Name}: {ex.Message}");
                        break;
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        Console.WriteLine($"[FolderFeatureIndexer] thumbnail failed {file.Name}: {ex}");
                    }
                }
            }
            catch (OperationCanceledException)
            {
                lease?.Dispose();
                throw;
            }

            if (lease == null)
            {
                await EmitAsync(results, new PathResult(ticket, cvPath: false)
                {
                    Error = error ?? "缩略图解码失败",
                    ElapsedMs = clock.Elapsed.TotalMilliseconds,
                }).ConfigureAwait(false);
                continue;
            }

            try
            {
                await ready.WriteAsync(
                    new DinoReady(ticket, lease, clock.Elapsed.TotalMilliseconds), ct).ConfigureAwait(false);
                lease = null; // 所有权移交推理 stage
            }
            finally
            {
                lease?.Dispose();
            }
        }
    }

    /// <summary>单路 DINO 推理:批量请求排在交互请求之后。</summary>
    private async Task InferDinoAsync(
        ChannelReader<DinoReady> ready,
        ChannelWriter<PathResult> results,
        CancellationToken ct)
    {
        await foreach (var item in ready.ReadAllAsync(ct).ConfigureAwait(false))
        {
            var ticket = item.Ticket;
            var result = new PathResult(ticket, cvPath: false);
            var clock = Stopwatch.StartNew();
            try
            {
                var (cls, patches) = await DinoFeatureExtractor.ExtractDualAsync(
                    item.Lease.Image, includePatches: ticket.NeedPatches, ct: ct, background: true).ConfigureAwait(false);

                if (ticket.NeedCls)
                {
                    result.ClsBlob = EncodeFloatArray(cls);
                    result.ClsVector = cls;
                }
                if (ticket.NeedPatches && patches != null) result.PatchBlob = EncodeFloatArray(patches);
            }
            catch (OperationCanceledException)
            {
                // 取消:只交已经算好的部分,不补造结果。
            }
            catch (AnalysisMemoryException ex)
            {
                result.Error = "内存不足";
                Console.WriteLine($"[FolderFeatureIndexer] dino refused {ticket.Group.Representative.Name}: {ex.Message}");
            }
            catch (Exception ex)
            {
                result.Error = "DINO 推理失败";
                Console.WriteLine($"[FolderFeatureIndexer] dino failed {ticket.Group.Representative.Name}: {ex}");
            }
            finally
            {
                item.Lease.Dispose();
                result.ElapsedMs = item.DecodeMs + clock.Elapsed.TotalMilliseconds;
                if (result.HasData || !ct.IsCancellationRequested)
                    await EmitAsync(results, result).ConfigureAwait(false);
            }
        }
    }

    /// <summary>CV 路解码:单路全图解码,预备一份给计算 stage。</summary>
    private async Task DecodeFullAsync(
        ChannelReader<GroupTicket> jobs,
        ChannelWriter<CvReady> ready,
        ChannelWriter<PathResult> results,
        CancellationToken ct)
    {
        await foreach (var ticket in jobs.ReadAllAsync(ct).ConfigureAwait(false))
        {
            AnalysisImageLease? lease = null;
            string? error = null;
            var clock = Stopwatch.StartNew();
            try
            {
                foreach (var file in ticket.Group.Files)
                {
                    ct.ThrowIfCancellationRequested();
                    await YieldIfP0Async().ConfigureAwait(false);
                    try
                    {
                        lease = await BitmapLoader.AcquireAnalysisImageAsync(file.File, ct).ConfigureAwait(false);
                        if (lease != null) break;
                        Console.WriteLine($"[FolderFeatureIndexer] full decode miss {file.Name}");
                    }
                    catch (AnalysisMemoryException ex)
                    {
                        error = "内存不足";
                        Console.WriteLine($"[FolderFeatureIndexer] full decode refused {file.Name}: {ex.Message}");
                        break;
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        Console.WriteLine($"[FolderFeatureIndexer] full decode failed {file.Name}: {ex}");
                    }
                }
            }
            catch (OperationCanceledException)
            {
                lease?.Dispose();
                throw;
            }

            if (lease == null)
            {
                await EmitAsync(results, new PathResult(ticket, cvPath: true)
                {
                    Error = error ?? "解码失败",
                    ElapsedMs = clock.Elapsed.TotalMilliseconds,
                }).ConfigureAwait(false);
                continue;
            }

            try
            {
                await ready.WriteAsync(
                    new CvReady(ticket, lease, clock.Elapsed.TotalMilliseconds), ct).ConfigureAwait(false);
                lease = null; // 所有权移交计算 stage
            }
            finally
            {
                lease?.Dispose();
            }
        }
    }

    /// <summary>单路 CV 计算:租约图已是显示朝向 sRGB8,进会话共享的全核调度器。</summary>
    private async Task ComputeCvAsync(
        ChannelReader<CvReady> ready,
        ChannelWriter<PathResult> results,
        CancellationToken ct)
    {
        await foreach (var item in ready.ReadAllAsync(ct).ConfigureAwait(false))
        {
            var ticket = item.Ticket;
            var result = new PathResult(ticket, cvPath: true);
            var clock = Stopwatch.StartNew();
            try
            {
                var cvResult = await CvGridExtractor.ExtractAsync(item.Lease.Image, ct).ConfigureAwait(false);
                result.CvBlob = cvResult.Encode();
                result.CvSpec = CvGridResult.CurrentVersion;
                result.CvWidth = item.Lease.Image.Width;
                result.CvHeight = item.Lease.Image.Height;
            }
            catch (OperationCanceledException)
            {
                // 取消:只交已经算好的部分。
            }
            catch (AnalysisMemoryException ex)
            {
                result.Error = "内存不足";
                Console.WriteLine($"[FolderFeatureIndexer] cv refused {ticket.Group.Representative.Name}: {ex.Message}");
            }
            catch (Exception ex)
            {
                result.Error = "CV 计算失败";
                Console.WriteLine($"[FolderFeatureIndexer] cv failed {ticket.Group.Representative.Name}: {ex}");
            }
            finally
            {
                item.Lease.Dispose();
                result.ElapsedMs = item.DecodeMs + clock.Elapsed.TotalMilliseconds;
                if (result.HasData || !ct.IsCancellationRequested)
                    await EmitAsync(results, result).ConfigureAwait(false);
            }
        }
    }

    /// <summary>单 writer:每条结果即写它自己的成功部分;不接受取消,取消前的成果照样落库。</summary>
    private async Task WriteResultsAsync(ChannelReader<PathResult> reader, SemaphoreSlim inflight)
    {
        var slots = new Dictionary<int, Slot>();
        while (await reader.WaitToReadAsync().ConfigureAwait(false))
        {
            while (reader.TryRead(out var result))
            {
                try
                {
                    await ApplyResultAsync(result, slots, inflight).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[FolderFeatureIndexer] writer {result.Ticket.Group.Representative.Name}: {ex}");
                }
            }
        }
    }

    private async Task ApplyResultAsync(PathResult result, Dictionary<int, Slot> slots, SemaphoreSlim inflight)
    {
        var ticket = result.Ticket;
        if (!slots.TryGetValue(ticket.Id, out var slot))
        {
            slot = new Slot(ticket.PendingPaths, ticket.HoldsInflight);
            if (ticket.DefaultError != null) slot.Error = ticket.DefaultError;
            slots[ticket.Id] = slot;
        }

        if (result.Error != null) slot.Error ??= result.Error;
        if (result.CvPath)
        {
            slot.CvMs += result.ElapsedMs;
            Interlocked.Add(ref _cvMs, (long)result.ElapsedMs);
        }
        else
        {
            slot.DinoMs += result.ElapsedMs;
            Interlocked.Add(ref _dinoMs, (long)result.ElapsedMs);
        }

        var writeError = await WritePathAsync(result, slot).ConfigureAwait(false);
        if (writeError != null) slot.Error ??= writeError;

        if (--slot.Remaining > 0) return;
        if (slot.Finished) return;
        slot.Finished = true;
        slots.Remove(ticket.Id);

        bool failed = await ClassifyGroupAsync(ticket).ConfigureAwait(false);
        if (slot.HoldsInflight) inflight.Release();
        if (failed) Interlocked.CompareExchange(ref _lastError, slot.Error ?? "部分数据未能落库", null);
        ReportProgress(failed);

        Console.WriteLine(
            $"[FolderFeatureIndexer] group {ticket.Group.Representative.Name} " +
            $"dino={slot.DinoMs:F0}ms cv={slot.CvMs:F0}ms write={slot.WriteMs:F0}ms => " +
            (failed ? $"failed: {slot.Error ?? "部分数据未能落库"}" : "ok"));
    }

    /// <summary>写一条结果自身的成功部分(可能只有 CLS/patch,也可能只有 CV)。</summary>
    private async Task<string?> WritePathAsync(PathResult result, Slot slot)
    {
        if (!result.HasData) return null;

        var ticket = result.Ticket;
        var clock = Stopwatch.StartNew();
        try
        {
            // 顺手带 EXIF + rating 快照(Plan-2-4):代表文件 ExifData 在 GroupByFingerprintAsync 已加载
            await PhotoDatabase.WriteIndexedAsync(
                ticket.Group.Input, ticket.Group.Fingerprint, DinoModelResources.ModelId,
                result.ClsBlob, result.PatchBlob, result.CvBlob, result.CvSpec,
                result.CvWidth, result.CvHeight,
                BuildExifSnapshot(ticket.Group.Representative)).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[FolderFeatureIndexer] write {ticket.Group.Representative.Name}: {ex}");
            return "写库失败";
        }
        finally
        {
            var ms = clock.ElapsedMilliseconds;
            slot.WriteMs += ms;
            Interlocked.Add(ref _writeMs, ms);
        }

        if (result.ClsVector != null) DinoFeatureCache.PutMemoryCache(ticket.Group.Fingerprint, result.ClsVector);

        AnalysisResultCache.Invalidate(ticket.Group.Fingerprint);
        try { FeaturesWritten?.Invoke(); }
        catch (Exception ex) { Console.WriteLine($"[FolderFeatureIndexer] features-written handler: {ex}"); }

        return null;
    }

    /// <summary>组成功与否按库里实际齐备情况判定。</summary>
    private static async Task<bool> ClassifyGroupAsync(GroupTicket ticket)
    {
        if (ticket.DefaultError != null) return true;
        if (ticket.Verified) return false;
        try
        {
            var missing = await EvaluateMissingPartsAsync(ticket.Group.Fingerprint).ConfigureAwait(false);
            return missing.AnyMissing;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[FolderFeatureIndexer] verify {ticket.Group.Representative.Name}: {ex}");
            return true;
        }
    }

    /// <summary>解码 stage 全部退出后关闭 ready 队列,让下游 stage 正常收尾。</summary>
    private static async Task CompleteWhenDoneAsync<T>(IReadOnlyList<Task> tasks, ChannelWriter<T> writer)
    {
        try
        {
            await Task.WhenAll(tasks).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[FolderFeatureIndexer] decode stage: {ex}");
        }
        writer.TryComplete();
    }

    /// <summary>结果投递不做取消中断:已算完的成果必须交到 writer。</summary>
    private static async Task EmitAsync(ChannelWriter<PathResult> results, PathResult result)
    {
        if (results.TryWrite(result)) return;
        try
        {
            await results.WriteAsync(result).ConfigureAwait(false);
        }
        catch (ChannelClosedException)
        {
        }
    }

    /// <summary>解码下一项前若有 P0 在途,让出一次执行机会(只让一次;排队优先级仍由底层 P3 门决定)。</summary>
    private static async Task YieldIfP0Async()
    {
        if (DecodeScheduler.P0InFlightCount > 0) await Task.Yield();
    }

    private void ReportProgress(bool failed)
    {
        if (failed) Interlocked.Increment(ref _failed);
        int done = Interlocked.Increment(ref _completed);
        try
        {
            ProgressChanged?.Invoke(new IndexProgress(done, _total, Volatile.Read(ref _failed)));
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[FolderFeatureIndexer] progress handler: {ex}");
        }
    }

    private static byte[] EncodeFloatArray(float[] data)
    {
        var bytes = new byte[data.Length * sizeof(float)];
        for (int i = 0; i < data.Length; i++)
            BinaryPrimitives.WriteSingleLittleEndian(
                bytes.AsSpan(i * sizeof(float), sizeof(float)), data[i]);
        return bytes;
    }

    /// <summary>
    /// 训练用评级只取该文件自身内嵌来源的 Rated 1..5；Missing / Rejected / Invalid / Conflict
    /// 以及 0 星统一为 null。不采用同组其它文件或 sidecar 的值，避免用代表值伪造样本。
    /// </summary>
    public static int? ResolveTrainingRating(ImageFile file)
    {
        ArgumentNullException.ThrowIfNull(file);
        return file.RatingState == XmpRatingState.Rated && file.Rating is >= 1 and <= 5
            ? file.Rating
            : null;
    }

    /// <summary>
    /// 从代表文件的 EXIF 抽出训练用拍摄参数快照(实际焦距/光圈/快门/CMOS 倍率)+ 当前 rating。
    /// `crop_factor` = EquivFocalLength / FocalLength,两者任一缺失则 NULL。
    /// </summary>
    private static ExifSnapshot BuildExifSnapshot(ImageFile file)
    {
        var exif = file.ExifData;
        double? focal = ToDoubleOrNull(exif?.FocalLength);
        double? aperture = ToDoubleOrNull(exif?.Aperture);
        double? shutter = ToDoubleOrNull(exif?.ExposureTime);
        double? equivFocal = ToDoubleOrNull(exif?.EquivFocalLength);

        double? cropFactor = null;
        if (focal.HasValue && focal.Value > 0 && equivFocal.HasValue && equivFocal.Value > 0)
            cropFactor = equivFocal.Value / focal.Value;

        return new ExifSnapshot
        {
            FocalLength = focal,
            Aperture = aperture,
            ShutterSpeed = shutter,
            CropFactor = cropFactor,
            Rating = ResolveTrainingRating(file),
        };
    }

    private static double? ToDoubleOrNull(MetadataExtractor.Rational? r)
    {
        if (!r.HasValue) return null;
        var v = r.Value;
        if (v.Denominator == 0) return null;
        return (double)v.Numerator / v.Denominator;
    }

    /// <summary>一组的工作票:只读,生产后由各路共享引用。</summary>
    private sealed class GroupTicket
    {
        public GroupTicket(FingerprintGroup group, MissingParts missing)
        {
            Group = group;
            NeedCls = missing.NeedCls;
            NeedPatches = missing.NeedPatches;
            NeedCv = missing.NeedCv;
            NeedDino = missing.NeedCls || missing.NeedPatches;
            PendingPaths = (NeedDino ? 1 : 0) + (NeedCv ? 1 : 0);
            HoldsInflight = true;
        }

        private GroupTicket(FingerprintGroup group, bool verified)
        {
            Group = group;
            Verified = verified;
            PendingPaths = 1;
        }

        private GroupTicket(FingerprintGroup group, string defaultError)
        {
            Group = group;
            DefaultError = defaultError;
            PendingPaths = 1;
        }

        public static GroupTicket AlreadyComplete(FingerprintGroup group) => new(group, true);

        public static GroupTicket Unfingerprinted(FingerprintGroup group) => new(group, "无拍摄时间,无法分组");

        /// <summary>writer 的组计数键,生产时分配。</summary>
        public int Id { get; set; }

        public readonly FingerprintGroup Group;
        public readonly bool NeedDino;
        public readonly bool NeedCls;
        public readonly bool NeedPatches;
        public readonly bool NeedCv;

        /// <summary>库中已确认齐备,无需写库与复核。</summary>
        public readonly bool Verified;

        /// <summary>非空表示生产者已判定该组失败(空指纹)。</summary>
        public readonly string? DefaultError;

        /// <summary>该组需要 settle 的路径数。</summary>
        public readonly int PendingPaths;

        /// <summary>在途组许可由 writer 归还。</summary>
        public readonly bool HoldsInflight;
    }

    /// <summary>一条路径结束时的不可变快照:只由产出它的 worker 填写,交接后只读。</summary>
    private sealed class PathResult
    {
        public PathResult(GroupTicket ticket, bool cvPath)
        {
            Ticket = ticket;
            CvPath = cvPath;
        }

        public readonly GroupTicket Ticket;
        public readonly bool CvPath;

        public string? Error;
        public double ElapsedMs;

        public byte[]? ClsBlob;
        public float[]? ClsVector;
        public byte[]? PatchBlob;
        public byte[]? CvBlob;
        public string? CvSpec;
        public int CvWidth;
        public int CvHeight;

        public bool HasData => ClsBlob != null || PatchBlob != null || CvBlob != null;
    }

    private sealed class Slot
    {
        public Slot(int pendingPaths, bool holdsInflight)
        {
            Remaining = pendingPaths;
            HoldsInflight = holdsInflight;
        }

        public int Remaining;
        public bool Finished;
        public string? Error;
        public double DinoMs;
        public double CvMs;
        public double WriteMs;
        public readonly bool HoldsInflight;
    }

    private readonly record struct DinoReady(GroupTicket Ticket, AnalysisImageLease Lease, double DecodeMs);

    private readonly record struct CvReady(GroupTicket Ticket, AnalysisImageLease Lease, double DecodeMs);
}

/// <summary>批量提取进度快照。</summary>
public readonly struct IndexProgress
{
    /// <summary>已收尾的指纹组数，含已齐备组与失败组；中断时可小于总数。</summary>
    public int Completed { get; }

    /// <summary>总指纹组数。</summary>
    public int Total { get; }

    /// <summary>已收尾组中未能补齐三路数据的组数。</summary>
    public int Failed { get; }

    public IndexProgress(int completed, int total, int failed)
    {
        Completed = completed;
        Total = total;
        Failed = failed;
    }
}
