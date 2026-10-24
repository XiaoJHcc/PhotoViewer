using PhotoViewer.Core.Image;
using PhotoViewer.Core.Storage;

namespace PhotoViewer.Core;

public enum RatingWriteOutcome
{
    /// <summary>组内全部文件已写入并通过写后校验；只有这一结果可由上层同步数据库。</summary>
    Applied,

    /// <summary>等待期间被同组更新的请求取代，未做任何物理修改。</summary>
    Superseded,

    /// <summary>写入中途失败，已按逆序补偿，且补偿全部成功（磁盘回到写入前状态）。</summary>
    RolledBack,

    /// <summary>写入失败且至少一个文件的补偿未成功；组内文件可能不一致，需按逐文件状态重新展示。</summary>
    Partial,

    /// <summary>首次物理修改前被取消，未做任何物理修改。</summary>
    Canceled,
}

/// <summary>
/// 单个文件在本次评分写入中的结果。失败路径的 <see cref="After"/> 来自重新读取，
/// 不是请求值，代表磁盘真实状态。
/// </summary>
public sealed record RatingFileOutcome(
    string FileName,
    IFileRef File,
    XmpRatingSnapshot? Before,
    XmpRatingSnapshot? After,
    bool Written,
    string? Error = null,
    string? CompensationError = null);

/// <summary>
/// 同组评分写入的结果。<see cref="Files"/> 按实际写入顺序排列；失败路径会把组内每个文件都列出来。
/// </summary>
public sealed record RatingWriteResult(
    string GroupId,
    int RequestedRating,
    RatingWriteOutcome Outcome,
    IReadOnlyList<RatingFileOutcome> Files,
    string? FailureReason = null)
{
    public bool IsApplied => Outcome == RatingWriteOutcome.Applied;

    public bool AnyFileModified => Files.Any(file => file.Written);
}

/// <summary>
/// 应用实例级的评分写入服务（无静态状态）。同一 <see cref="PhotoGroup.Id"/> 串行执行，
/// 组内文件顺序写并收集回执；任一文件失败按逆序补偿已写入的文件。
/// 只保证“全部成功或按回执回滚”，不宣称跨文件原子。
/// </summary>
/// <remarks>
/// <para>快速连按时（例如 3→4→5），仍在排队等待的旧请求以 <see cref="RatingWriteOutcome.Superseded"/>
/// 返回且不写盘；已经开始物理写入的请求不会被取消，会完整走完或回滚。</para>
/// <para>取消只在首次物理修改前生效（排队、读取阶段）；一旦开始写入，本次操作不再响应取消，
/// 避免取消留下跨文件半写状态。</para>
/// <para>适配器/存储在自己的 <c>WriteAsync</c> 内负责写后语义校验并在失败时自行恢复原内容；
/// 本类只对拿到 <see cref="XmpRatingWriteReceipt"/> 的文件做补偿。</para>
/// </remarks>
public sealed class RatingService
{
    private readonly Func<IFileRef, IXmpRatingStore> _storeFactory;
    private readonly object _registryLock = new();
    private readonly Dictionary<string, GroupGate> _gates = new(StringComparer.Ordinal);
    private long _sequence;

    public RatingService()
        : this(XmpRatingStoreFactory.Create)
    {
    }

    public RatingService(Func<IFileRef, IXmpRatingStore> storeFactory)
    {
        ArgumentNullException.ThrowIfNull(storeFactory);
        _storeFactory = storeFactory;
    }

    public async Task<RatingWriteResult> SetRatingAsync(
        PhotoGroup group,
        int rating,
        bool safeMode,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(group);
        if (rating is < 0 or > 5)
            throw new ArgumentOutOfRangeException(nameof(rating), rating, "XMP Rating 必须在 0 到 5 之间");

        var (gate, sequence) = Register(group.Id);
        var acquired = false;
        try
        {
            try
            {
                await gate.Semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
                acquired = true;
            }
            catch (OperationCanceledException)
            {
                return new RatingWriteResult(
                    group.Id, rating, RatingWriteOutcome.Canceled, [],
                    "等待同组评分写入时被取消，未修改文件");
            }

            if (IsStale(gate, sequence))
            {
                return new RatingWriteResult(
                    group.Id, rating, RatingWriteOutcome.Superseded, [],
                    "已被同组更新的评分请求取代，未修改文件");
            }

            return await ApplyAsync(group, rating, safeMode, gate, sequence, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            if (acquired) gate.Semaphore.Release();
            Unregister(group.Id, gate);
        }
    }

    private async Task<RatingWriteResult> ApplyAsync(
        PhotoGroup group,
        int rating,
        bool safeMode,
        GroupGate gate,
        long sequence,
        CancellationToken cancellationToken)
    {
        var files = OrderFiles(group);
        var plans = new List<(ImageFile Image, IXmpRatingStore Store, XmpRatingSnapshot Before)>(files.Length);

        for (var index = 0; index < files.Length; index++)
        {
            var image = files[index];
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                var store = _storeFactory(image.File);
                cancellationToken.ThrowIfCancellationRequested();
                var before = await store.ReadAsync(cancellationToken).ConfigureAwait(false);
                plans.Add((image, store, before));
            }
            catch (OperationCanceledException)
            {
                return new RatingWriteResult(
                    group.Id, rating, RatingWriteOutcome.Canceled, PlannedOutcomes(plans),
                    "首次物理修改前被取消，未修改文件");
            }
            catch (Exception exception)
            {
                var failure = Describe(exception);
                var plannedOutcomes = PlannedOutcomes(plans);
                plannedOutcomes.Add(new RatingFileOutcome(
                    image.Name, image.File, null, null, Written: false, Error: failure));
                return new RatingWriteResult(
                    group.Id, rating, RatingWriteOutcome.RolledBack, plannedOutcomes,
                    $"读取 {image.Name} 的评分失败：{failure}；未修改任何文件");
            }
        }

        // 预读可能较慢，正式写盘前再确认一次：期间有更新的同组请求时直接让位。
        if (IsStale(gate, sequence))
        {
            return new RatingWriteResult(
                group.Id, rating, RatingWriteOutcome.Superseded, PlannedOutcomes(plans),
                "已被同组更新的评分请求取代，未修改文件");
        }

        var results = new List<RatingFileOutcome>(plans.Count);
        var receipts = new List<(IXmpRatingStore Store, XmpRatingWriteReceipt Receipt)>(plans.Count);
        var started = false;
        string? failureReason = null;

        for (var index = 0; index < plans.Count; index++)
        {
            var (image, store, before) = plans[index];
            try
            {
                if (!started)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    started = true;
                }

                var receipt = await store.WriteAsync(rating, safeMode, CancellationToken.None)
                    .ConfigureAwait(false);
                receipts.Add((store, receipt));
                results.Add(new RatingFileOutcome(image.Name, image.File, before, receipt.After, Written: true));
            }
            catch (OperationCanceledException) when (!started)
            {
                return new RatingWriteResult(
                    group.Id, rating, RatingWriteOutcome.Canceled, PlannedOutcomes(plans),
                    "首次物理修改前被取消，未修改文件");
            }
            catch (Exception exception)
            {
                failureReason = Describe(exception);
                results.Add(new RatingFileOutcome(
                    image.Name, image.File, before, null, Written: false, Error: failureReason));
                break;
            }
        }

        if (failureReason is null)
        {
            return new RatingWriteResult(group.Id, rating, RatingWriteOutcome.Applied, results);
        }

        // 逆序补偿已拿到回执的文件；没有回执的文件不做补偿（适配器负责自行恢复）。
        var compensationErrors = new string?[receipts.Count];
        for (var index = receipts.Count - 1; index >= 0; index--)
        {
            try
            {
                await receipts[index].Store
                    .RestoreAsync(receipts[index].Receipt, CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                compensationErrors[index] = Describe(exception);
            }
        }

        var outcomes = new List<RatingFileOutcome>(plans.Count);
        var compensationFailed = false;
        for (var index = 0; index < plans.Count; index++)
        {
            var (image, store, before) = plans[index];
            var attempted = index < results.Count;
            var written = attempted && results[index].Written;
            var error = attempted ? results[index].Error : null;
            var compensationError = written && index < compensationErrors.Length
                ? compensationErrors[index]
                : null;

            var read = await TryReadAsync(store).ConfigureAwait(false);
            if (!read.Ok)
            {
                error = Combine(error, $"重新读取失败：{read.Error}");
            }
            else if (written && compensationError is null && !SameSemantics(before, read.Snapshot))
            {
                compensationError = $"补偿后磁盘评分为 {Describe(read.Snapshot)}，与写入前不一致";
            }

            if (compensationError is not null) compensationFailed = true;

            outcomes.Add(new RatingFileOutcome(
                image.Name, image.File, before, read.Snapshot, written, error, compensationError));
        }

        var suffix = receipts.Count == 0
            ? "；未修改任何文件"
            : compensationFailed
                ? "；部分文件中补偿失败，磁盘状态可能不一致"
                : "；已按逆序恢复所有已写文件";

        return new RatingWriteResult(
            group.Id,
            rating,
            compensationFailed ? RatingWriteOutcome.Partial : RatingWriteOutcome.RolledBack,
            outcomes,
            Combine(failureReason, suffix.TrimStart('；')));
    }

    /// <summary>代表文件（通常是主格式）先写，其余保持组内顺序。</summary>
    private static ImageFile[] OrderFiles(PhotoGroup group)
    {
        if (group.Files.Count <= 1) return group.Files.ToArray();
        return group.Files
            .OrderBy(file => ReferenceEquals(file, group.Representative) ? 0 : 1)
            .ToArray();
    }

    private static List<RatingFileOutcome> PlannedOutcomes(
        List<(ImageFile Image, IXmpRatingStore Store, XmpRatingSnapshot Before)> plans) =>
        plans
            .Select(plan => new RatingFileOutcome(
                plan.Image.Name, plan.Image.File, plan.Before, plan.Before, Written: false))
            .ToList();

    private static async Task<(bool Ok, XmpRatingSnapshot? Snapshot, string? Error)> TryReadAsync(
        IXmpRatingStore store)
    {
        try
        {
            var snapshot = await store.ReadAsync(CancellationToken.None).ConfigureAwait(false);
            return (true, snapshot, null);
        }
        catch (Exception exception)
        {
            return (false, null, Describe(exception));
        }
    }

    private static bool SameSemantics(XmpRatingSnapshot? left, XmpRatingSnapshot? right) =>
        left is { } leftValue &&
        right is { } rightValue &&
        leftValue.State == rightValue.State &&
        leftValue.Value == rightValue.Value;

    private static string? Combine(string? first, string? second)
    {
        if (string.IsNullOrEmpty(first)) return second;
        if (string.IsNullOrEmpty(second)) return first;
        return $"{first}；{second}";
    }

    private static string Describe(XmpRatingSnapshot? snapshot) => snapshot switch
    {
        null => "未知",
        { State: XmpRatingState.Rated } value => $"Rating {value.Value}",
        { State: XmpRatingState.Rejected } => "Rating -1（拒绝）",
        { State: XmpRatingState.Missing } => "无评分",
        { State: XmpRatingState.Conflict } => "多个 XMP 评分冲突",
        _ => "无效评分",
    };

    private static string Describe(Exception exception) => exception switch
    {
        StorageException storage => storage.Message,
        OperationCanceledException => "操作已取消",
        _ => $"{exception.GetType().Name}: {exception.Message}",
    };

    private static bool IsStale(GroupGate gate, long sequence) =>
        sequence != Volatile.Read(ref gate.LatestSequence);

    private (GroupGate Gate, long Sequence) Register(string groupId)
    {
        lock (_registryLock)
        {
            if (!_gates.TryGetValue(groupId, out var gate))
            {
                gate = new GroupGate();
                _gates[groupId] = gate;
            }

            gate.References++;
            var sequence = Interlocked.Increment(ref _sequence);
            Volatile.Write(ref gate.LatestSequence, sequence);
            return (gate, sequence);
        }
    }

    private void Unregister(string groupId, GroupGate gate)
    {
        lock (_registryLock)
        {
            gate.References--;
            if (gate.References <= 0 &&
                _gates.TryGetValue(groupId, out var current) &&
                ReferenceEquals(current, gate))
            {
                _gates.Remove(groupId);
            }
        }
    }

    private sealed class GroupGate
    {
        public SemaphoreSlim Semaphore { get; } = new(1, 1);

        public long LatestSequence;

        public int References;
    }
}
