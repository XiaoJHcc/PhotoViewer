using PhotoViewer.Core.Image;
using PhotoViewer.Core.Storage;

namespace PhotoViewer.Core;

public enum RatingWriteOutcome
{
    /// <summary>组内全部来源已写入并通过写后校验；只有这一结果可由上层同步数据库。</summary>
    Applied,

    /// <summary>组内全部来源本来就已经是目标值，没有发生任何物理修改。</summary>
    NoChange,

    /// <summary>等待期间被同组更新的请求取代，未做任何物理修改。</summary>
    Superseded,

    /// <summary>写入中途失败，已按逆序补偿，且补偿全部成功（磁盘回到写入前状态）。</summary>
    RolledBack,

    /// <summary>写入失败且至少一个来源的补偿未成功；组内可能不一致，需按逐来源状态重新展示。</summary>
    Partial,

    /// <summary>首次物理修改前被取消，未做任何物理修改。</summary>
    Canceled,
}

/// <summary>
/// 一个实际评分来源在本次写入中的结果。<see cref="After"/> 在失败路径来自重新读取，代表磁盘真实状态。
/// </summary>
public sealed record RatingSourceOutcome(
    XmpRatingSource Source,
    IFileRef File,
    XmpRatingSnapshot? Before,
    XmpRatingSnapshot? After,
    bool Written,
    bool NoChange,
    string? Error = null,
    string? CompensationError = null)
{
    public string DisplayName => File.Name;
}

/// <summary>一个文件在本次写入中的结果；<see cref="Sources"/> 包含它的内嵌来源与同名 sidecar。</summary>
public sealed record RatingFileOutcome(
    string FileName,
    IFileRef File,
    IReadOnlyList<RatingSourceOutcome> Sources,
    string? Error = null)
{
    public XmpRatingSnapshot? EmbeddedAfter =>
        Sources.FirstOrDefault(source => source.Source == XmpRatingSource.Embedded)?.After;

    public bool Written => Sources.Any(source => source.Written);
}

/// <summary>
/// 同组评分写入的结果。<see cref="Files"/> 按实际写入顺序排列；失败路径会把组内每个文件与其全部来源都列出来。
/// </summary>
public sealed record RatingWriteResult(
    string GroupId,
    int RequestedRating,
    RatingWriteOutcome Outcome,
    IReadOnlyList<RatingFileOutcome> Files,
    string? FailureReason = null)
{
    public bool IsApplied => Outcome is RatingWriteOutcome.Applied or RatingWriteOutcome.NoChange;

    public bool AnyFileModified => Files.SelectMany(file => file.Sources).Any(source => source.Written);
}

/// <summary>
/// 一次评分失败的完整载荷：结果/原因/是否 Partial 与原文对象一起传递，
/// 避免共享 <c>LastRatingWriteError</c> 被后发请求清空造成错误文案串台。
/// </summary>
public sealed record RatingWriteFailure(
    ImageFile File,
    int RequestedRating,
    RatingWriteOutcome? Outcome,
    string? Reason,
    bool IsPartial);

/// <summary>
/// 应用实例级的评分写入服务（无静态状态）。同一 <see cref="PhotoGroup.Id"/> 串行执行。
/// </summary>
/// <remarks>
/// <para>写入目标是组内全部<strong>实际来源</strong>：每个文件的内嵌 XMP，加上已存在的
/// <c>&lt;stem&gt;.xmp</c>（同名共享 sidecar 只出现一次）。sidecar 不存在时不会创建。
/// 写入时逐个文件直接核查当前磁盘，不使用列表读取用的 sidecar 索引。</para>
/// <para>两个阶段：先对全部来源做只读预检（<see cref="IXmpRatingStore.PrepareAsync"/>，包含写权限探测），
/// 任一来源无法安全改写则在首次修改前整组失败；预检全部通过后才按“代表文件优先”的顺序提交。
/// 任一提交失败时按逆序补偿已拿到回执的来源。</para>
/// <para>成功前还有一次整组校验：重新发现实际来源集合，并逐来源重读磁盘（同值 NoChange 也重读）。
/// 集合新增/消失或任一来源不再等于目标值都不算成功，走与提交失败相同的补偿/真值路径。</para>
/// <para>快速连按时（例如 3→4→5），仍在排队等待的旧请求以 <see cref="RatingWriteOutcome.Superseded"/>
/// 返回且不写盘；已经开始物理写入的请求不会被取消，会完整走完或回滚。取消只在首次物理修改前生效。</para>
/// <para>结果为 <see cref="RatingWriteOutcome.Partial"/> 时逐来源重新读取磁盘真值，不宣称跨文件原子。</para>
/// </remarks>
public sealed class RatingService
{
    private readonly Func<IFileRef, IXmpRatingStore> _embeddedStoreFactory;
    private readonly Func<IFileRef, IXmpRatingStore> _sidecarStoreFactory;
    private readonly object _registryLock = new();
    private readonly Dictionary<string, GroupGate> _gates = new(StringComparer.Ordinal);
    private long _sequence;

    public RatingService()
        : this(XmpRatingStoreFactory.Create, XmpRatingStoreFactory.CreateSidecar)
    {
    }

    /// <summary>测试/平台注入：内嵌与 sidecar 使用同一个工厂。</summary>
    public RatingService(Func<IFileRef, IXmpRatingStore> storeFactory)
        : this(storeFactory, storeFactory)
    {
    }

    public RatingService(
        Func<IFileRef, IXmpRatingStore> embeddedStoreFactory,
        Func<IFileRef, IXmpRatingStore> sidecarStoreFactory)
    {
        ArgumentNullException.ThrowIfNull(embeddedStoreFactory);
        ArgumentNullException.ThrowIfNull(sidecarStoreFactory);
        _embeddedStoreFactory = embeddedStoreFactory;
        _sidecarStoreFactory = sidecarStoreFactory;
    }

    public async Task<RatingWriteResult> SetRatingAsync(
        PhotoGroup group,
        int rating,
        bool safeMode,
        RatingSidecarIndex? sidecarIndex = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(group);
        // 核心全链路允许 -1（已拒绝）；UI 不提供该入口。
        if (rating is < -1 or > 5)
            throw new ArgumentOutOfRangeException(nameof(rating), rating, "XMP Rating 必须在 -1 到 5 之间");

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

            // sidecar 索引只服务列表读取；写入必须逐个文件核查当前磁盘，避免混合/陈旧索引漏写。
            _ = sidecarIndex;
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
        List<RatingWriteSource> sources;
        try
        {
            sources = await CollectSourcesAsync(group, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return new RatingWriteResult(
                group.Id, rating, RatingWriteOutcome.Canceled, [], "首次物理修改前被取消，未修改文件");
        }
        catch (Exception exception)
        {
            return new RatingWriteResult(
                group.Id, rating, RatingWriteOutcome.RolledBack, PlannedOutcomes(group, []),
                $"读取评分来源失败：{Describe(exception)}；未修改任何文件");
        }

        // 预检：只读，任一来源不支持就整组失败。
        var preparations = new List<(RatingWriteSource Source, XmpRatingPreparation Preparation)>(sources.Count);
        var precheckErrors = new List<string>();
        foreach (var source in sources)
        {
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (source.Kind == XmpRatingSource.Embedded)
                {
                    // 不依赖内存里的读取结果：写入前重新枚举内嵌 XMP 目录，
                    // 多份/扩展 XMP 无法证明唯一目标时拒绝，即使 canonical 缺 Rating 也不插入。
                    var probe = await XmpRatingReader
                        .ProbeEmbeddedAsync(source.Target, cancellationToken)
                        .ConfigureAwait(false);
                    if (probe.XmpDirectoryCount > 1)
                    {
                        throw new StorageException(
                            StorageError.Unsupported,
                            $"{source.DisplayName} 存在多份 XMP 目录，无法证明唯一写入目标");
                    }

                    if (probe.HasExtendedXmp)
                    {
                        throw new StorageException(
                            StorageError.Unsupported,
                            $"{source.DisplayName} 声明了 extended XMP，无法证明唯一写入目标");
                    }

                    if (probe.HasNullMeta)
                    {
                        throw new StorageException(
                            StorageError.Unsupported,
                            $"{source.DisplayName} 的 XMP 目录没有可解析的元数据");
                    }
                }

                var preparation = await source.Store
                    .PrepareAsync(rating, safeMode, cancellationToken)
                    .ConfigureAwait(false);
                preparations.Add((source, preparation));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return new RatingWriteResult(
                    group.Id, rating, RatingWriteOutcome.Canceled,
                    PlannedOutcomes(group, preparations), "首次物理修改前被取消，未修改文件");
            }
            catch (Exception exception)
            {
                precheckErrors.Add($"{source.DisplayName}：{Describe(exception)}");
            }
        }

        if (precheckErrors.Count > 0)
        {
            return new RatingWriteResult(
                group.Id,
                rating,
                RatingWriteOutcome.RolledBack,
                PlannedOutcomes(group, preparations),
                "预检未通过，未修改任何文件：" + string.Join("；", precheckErrors));
        }

        // 预检可能较慢，正式写盘前再确认一次：期间有更新的同组请求时直接让位。
        if (IsStale(gate, sequence))
        {
            return new RatingWriteResult(
                group.Id, rating, RatingWriteOutcome.Superseded, PlannedOutcomes(group, preparations),
                "已被同组更新的评分请求取代，未修改文件");
        }

        var receipts = new List<(RatingWriteSource Source, XmpRatingWriteReceipt Receipt)>();
        RatingWriteSource? failedSource = null;
        string? failedSourceError = null;
        string? failureReason = null;
        var started = false;

        foreach (var (source, preparation) in preparations)
        {
            if (preparation.NoChange) continue;
            try
            {
                if (!started)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    started = true;
                }

                var receipt = await source.Store.CommitAsync(preparation, CancellationToken.None)
                    .ConfigureAwait(false);
                receipts.Add((source, receipt));
                if (receipt.Failure is { } commitFailure)
                {
                    // 存储已尝试自愈但无法证明恢复成功：回执必须参与逆序补偿。
                    failedSource = source;
                    failedSourceError = "写入失败";
                    failureReason = commitFailure;
                    break;
                }
            }
            catch (OperationCanceledException) when (!started)
            {
                return new RatingWriteResult(
                    group.Id, rating, RatingWriteOutcome.Canceled,
                    PlannedOutcomes(group, preparations), "首次物理修改前被取消，未修改文件");
            }
            catch (Exception exception)
            {
                failedSource = source;
                failedSourceError = "写入失败";
                failureReason = Describe(exception);
                break;
            }
        }

        // 全组成功前必须重读每个来源（含同值 NoChange）并重新发现来源集合：
        // 预检后新增/消失的 sidecar、写入期间被外部改动的同值来源都不允许以 Applied/NoChange 收尾。
        if (failureReason is null)
        {
            var verification = await VerifyAsync(group, rating, preparations).ConfigureAwait(false);
            if (verification.Problems.Count > 0)
            {
                failureReason = string.Join("；", verification.Problems);
                failedSource = verification.FailedSource;
                failedSourceError = "写入后校验不一致";
            }
        }

        if (failureReason is null)
        {
            var outcomes = BuildOutcomes(group, preparations, receipts, compensationErrors: [], failedSource: null, failedSourceError: null);
            var allNoChange = preparations.All(item => item.Preparation.NoChange);
            return new RatingWriteResult(
                group.Id,
                rating,
                allNoChange ? RatingWriteOutcome.NoChange : RatingWriteOutcome.Applied,
                outcomes);
        }

        // 安全模式：逆序补偿已拿到回执的来源；关闭时保留已写来源的磁盘真值，不假装整组一致。
        var compensationErrors = new Dictionary<RatingWriteSource, string>();
        if (safeMode)
        {
            for (var index = receipts.Count - 1; index >= 0; index--)
            {
                try
                {
                    await receipts[index].Source.Store
                        .RestoreAsync(receipts[index].Receipt, CancellationToken.None)
                        .ConfigureAwait(false);
                }
                catch (Exception exception)
                {
                    compensationErrors[receipts[index].Source] = Describe(exception);
                }
            }
        }

        var compensationFailed = safeMode ? compensationErrors.Count > 0 : receipts.Count > 0;
        var finalOutcomes = await ReadTruthAsync(
                group, preparations, receipts, compensationErrors, failedSource, failedSourceError, verifyRestoration: safeMode)
            .ConfigureAwait(false);
        var suffix = receipts.Count == 0
            ? "；未修改任何文件"
            : !safeMode
                ? "；安全模式关闭，已写来源保持磁盘真值，整组可能不一致"
                : compensationFailed
                    ? "；部分来源补偿失败，磁盘状态可能不一致"
                    : "；已按逆序恢复所有已写来源";

        return new RatingWriteResult(
            group.Id,
            rating,
            compensationFailed ? RatingWriteOutcome.Partial : RatingWriteOutcome.RolledBack,
            finalOutcomes,
            Combine(failureReason, suffix.TrimStart('；')));
    }

    /// <summary>
    /// 写入后的整组校验：重新发现实际来源集合，并逐个来源重读磁盘（同值 NoChange 不写盘也必须复核）。
    /// 集合新增/消失或任一来源不再等于目标值都算失败，交给已有的补偿/真值路径收尾。
    /// </summary>
    private async Task<RatingVerification> VerifyAsync(
        PhotoGroup group,
        int rating,
        List<(RatingWriteSource Source, XmpRatingPreparation Preparation)> preparations)
    {
        var problems = new List<string>();
        RatingWriteSource? failed = null;

        List<RatingWriteSource> current;
        try
        {
            current = await CollectSourcesAsync(group, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            return new RatingVerification(
                ["写入后重新枚举评分来源失败：" + Describe(exception)], null);
        }

        var plannedByKey = new Dictionary<string, RatingWriteSource>(StringComparer.Ordinal);
        foreach (var (source, _) in preparations) plannedByKey[SourceKey(source)] = source;
        var currentByKey = new Dictionary<string, RatingWriteSource>(StringComparer.Ordinal);
        foreach (var source in current) currentByKey[SourceKey(source)] = source;

        foreach (var (source, _) in preparations)
        {
            if (currentByKey.ContainsKey(SourceKey(source))) continue;
            problems.Add($"{source.DisplayName} 在预检后消失");
            failed ??= source;
        }

        foreach (var source in current)
        {
            if (plannedByKey.ContainsKey(SourceKey(source))) continue;
            problems.Add($"{source.DisplayName} 在预检后新增，未参与本次写入");
            failed ??= source;
        }

        foreach (var (source, preparation) in preparations)
        {
            // 已消失的来源在上面的集合差异里报告过，不再重复读。
            if (!currentByKey.ContainsKey(SourceKey(source))) continue;

            // 同值来源本次没有写盘，所以任何 revision 变化都只能是外部修改：
            // 不能把“预检时同值”直接当成最终成功。
            if (preparation.NoChange)
            {
                try
                {
                    var properties = await source.Target
                        .GetBasicPropertiesAsync(CancellationToken.None)
                        .ConfigureAwait(false);
                    if (!properties.Exists || RevisionChanged(preparation.Revision, properties.Revision))
                    {
                        problems.Add($"{source.DisplayName} 在预检后被外部修改，不能算作无需写入");
                        failed ??= source;
                        continue;
                    }
                }
                catch (Exception exception)
                {
                    problems.Add($"{source.DisplayName} 无法复核修订：{Describe(exception)}");
                    failed ??= source;
                    continue;
                }
            }

            XmpRatingSnapshot actual;
            try
            {
                actual = await source.Store.ReadAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                problems.Add($"{source.DisplayName} 重新读取失败：{Describe(exception)}");
                failed ??= source;
                continue;
            }

            if (!IsSameRating(actual, rating))
            {
                problems.Add($"{source.DisplayName} 当前为 {Describe(actual)}，期望 {DescribeTarget(rating)}");
                failed ??= source;
            }
        }

        return new RatingVerification(problems, failed);
    }

    private static bool RevisionChanged(StorageRevision? expected, StorageRevision? current)
    {
        if (expected is not { } wanted) return false;
        var known = wanted.Size is not null || wanted.LastModified is not null || wanted.Version is not null;
        if (!known) return false;
        if (current is not { } actual) return true;

        return (wanted.Size is { } size && actual.Size != size)
            || (wanted.LastModified is { } modified && actual.LastModified != modified)
            || (wanted.Version is { } version &&
                !string.Equals(actual.Version, version, StringComparison.Ordinal));
    }

    private static string SourceKey(RatingWriteSource source) =>
        (source.Kind == XmpRatingSource.Embedded ? "E|" : "S|") + source.Target.StorageId.Value;

    private static bool IsSameRating(XmpRatingSnapshot snapshot, int rating) =>
        rating == -1
            ? snapshot.State == XmpRatingState.Rejected
            : snapshot.State == XmpRatingState.Rated && snapshot.Value == rating;

    private static string DescribeTarget(int rating) =>
        rating == -1 ? "Rating -1（拒绝）" : $"Rating {rating}";

    private async Task<List<RatingWriteSource>> CollectSourcesAsync(
        PhotoGroup group,
        CancellationToken cancellationToken)
    {
        var sources = new List<RatingWriteSource>(group.Files.Count + 1);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var ordered = OrderFiles(group);

        // 顺序只用于确定性与补偿：代表文件内嵌 → 其余文件内嵌 → 每个实际存在的 sidecar。
        foreach (var image in ordered)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (seen.Add("E|" + image.File.StorageId.Value))
            {
                sources.Add(new RatingWriteSource(
                    image,
                    image.File,
                    XmpRatingSource.Embedded,
                    _embeddedStoreFactory(image.File)));
            }
        }

        foreach (var image in ordered)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // 写入必须逐个文件核查当前磁盘：不采用文件对象上的索引，避免混合/陈旧索引漏掉新 sidecar。
            var sidecar = await RatingSourceReader
                .ResolveSidecarAsync(image.File, sidecarIndex: null, cancellationToken)
                .ConfigureAwait(false);
            if (sidecar is null) continue;
            if (seen.Add("S|" + sidecar.StorageId.Value))
            {
                sources.Add(new RatingWriteSource(
                    image,
                    sidecar,
                    XmpRatingSource.Sidecar,
                    _sidecarStoreFactory(sidecar)));
            }
        }

        return sources;
    }

    private static List<RatingFileOutcome> BuildOutcomes(
        PhotoGroup group,
        List<(RatingWriteSource Source, XmpRatingPreparation Preparation)> preparations,
        List<(RatingWriteSource Source, XmpRatingWriteReceipt Receipt)> receipts,
        Dictionary<RatingWriteSource, string> compensationErrors,
        RatingWriteSource? failedSource,
        string? failedSourceError)
    {
        var sourceOutcomes = new Dictionary<RatingWriteSource, RatingSourceOutcome>();
        foreach (var (source, preparation) in preparations)
        {
            var receipt = receipts.FirstOrDefault(item => ReferenceEquals(item.Source, source));
            var written = receipt.Receipt is not null;
            var after = receipt.Receipt is { } applied ? applied.After : preparation.Before;
            var error = ReferenceEquals(source, failedSource) ? failedSourceError : null;
            compensationErrors.TryGetValue(source, out var compensationError);
            sourceOutcomes[source] = new RatingSourceOutcome(
                source.Kind,
                source.Target,
                preparation.Before,
                after,
                written,
                preparation.NoChange,
                error,
                compensationError);
        }

        return GroupOutcomes(group, sourceOutcomes);
    }

    private async Task<List<RatingFileOutcome>> ReadTruthAsync(
        PhotoGroup group,
        List<(RatingWriteSource Source, XmpRatingPreparation Preparation)> preparations,
        List<(RatingWriteSource Source, XmpRatingWriteReceipt Receipt)> receipts,
        Dictionary<RatingWriteSource, string> compensationErrors,
        RatingWriteSource? failedSource,
        string? failedSourceError,
        bool verifyRestoration)
    {
        var sourceOutcomes = new Dictionary<RatingWriteSource, RatingSourceOutcome>();
        foreach (var (source, preparation) in preparations)
        {
            var receipt = receipts.FirstOrDefault(item => ReferenceEquals(item.Source, source));
            var written = receipt.Receipt != null;
            string? error = ReferenceEquals(source, failedSource) ? failedSourceError : null;
            XmpRatingSnapshot? after = null;
            try
            {
                after = await source.Store.ReadAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                error = Combine(error, "重新读取失败：" + Describe(exception));
            }

            compensationErrors.TryGetValue(source, out var compensationError);
            if (verifyRestoration && written && compensationError is null && after is { } actual &&
                !SameSemantics(preparation.Before, actual))
            {
                compensationError = $"补偿后磁盘评分为 {Describe(actual)}，与写入前不一致";
            }

            sourceOutcomes[source] = new RatingSourceOutcome(
                source.Kind,
                source.Target,
                preparation.Before,
                after,
                written,
                preparation.NoChange,
                error,
                compensationError);
        }

        return GroupOutcomes(group, sourceOutcomes);
    }

    private static List<RatingFileOutcome> GroupOutcomes(
        PhotoGroup group,
        Dictionary<RatingWriteSource, RatingSourceOutcome> sourceOutcomes)
    {
        var results = new List<RatingFileOutcome>(group.Files.Count);
        foreach (var image in OrderFiles(group))
        {
            var outcomes = sourceOutcomes
                .Where(item => ReferenceEquals(item.Key.Owner, image))
                .OrderBy(item => item.Key.Kind == XmpRatingSource.Embedded ? 0 : 1)
                .Select(item => item.Value)
                .ToArray();
            results.Add(new RatingFileOutcome(image.Name, image.File, outcomes));
        }

        return results;
    }

    private static List<RatingFileOutcome> PlannedOutcomes(
        PhotoGroup group,
        List<(RatingWriteSource Source, XmpRatingPreparation Preparation)> preparations)
    {
        var outcomes = new Dictionary<RatingWriteSource, RatingSourceOutcome>();
        foreach (var (source, preparation) in preparations)
        {
            outcomes[source] = new RatingSourceOutcome(
                source.Kind,
                source.Target,
                preparation.Before,
                preparation.Before,
                Written: false,
                preparation.NoChange);
        }

        return GroupOutcomes(group, outcomes);
    }

    /// <summary>代表文件（通常是主格式）先写，其余保持组内顺序。</summary>
    private static ImageFile[] OrderFiles(PhotoGroup group)
    {
        if (group.Files.Count <= 1) return group.Files.ToArray();
        return group.Files
            .OrderBy(file => ReferenceEquals(file, group.Representative) ? 0 : 1)
            .ToArray();
    }

    private static bool SameSemantics(XmpRatingSnapshot left, XmpRatingSnapshot right) =>
        left.State == right.State && left.Value == right.Value;

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
        { State: XmpRatingState.Unreadable } value => value.Error ?? "来源无法读取",
        { State: XmpRatingState.Conflict } => "多个 XMP 评分冲突",
        _ => "无效评分",
    };

    private static string Describe(Exception exception) => Platform.StatusMessages.ReasonFor(exception);

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

    /// <summary>组内唯一的一个物理写入目标（内嵌包或共享 sidecar）。</summary>
    private sealed record RatingWriteSource(
        ImageFile Owner,
        IFileRef Target,
        XmpRatingSource Kind,
        IXmpRatingStore Store)
    {
        public string DisplayName => Kind == XmpRatingSource.Embedded
            ? Owner.Name + "（内嵌）"
            : Target.Name + "（sidecar）";
    }

    /// <summary>写入后整组校验的结果：<see cref="Problems"/> 非空即不能按成功收尾。</summary>
    private sealed record RatingVerification(
        IReadOnlyList<string> Problems,
        RatingWriteSource? FailedSource);

    private sealed class GroupGate
    {
        public SemaphoreSlim Semaphore { get; } = new(1, 1);

        public long LatestSequence;

        public int References;
    }
}
