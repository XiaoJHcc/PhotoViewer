using System.Reflection;
using PhotoViewer.Core.Image;
using PhotoViewer.Core.Memory;

namespace CoreContractCheck;

/// <summary>
/// 内存准入与像素缓冲池的压力契约：
/// 非 P0 并发准入的检查与记账必须原子提交（不越级别预留上限）、被拒申请不涨在途预留、许可归还归零、
/// P0 始终豁免但记账、Warning/Critical 优先级矩阵一致；
/// 池在持续压力下 Trim 之后不得被后续归还重新填满，恢复 Normal 后可正常池化。
/// 批量分析的显式意图路径（Caution/Warning 按实测字节放行、Critical 仍拒、可用量 0 走数值判据、
/// 作用域拒因隔离、取消代际、CPU 调度器通道互斥）按真实调用覆盖，不做真实解码、不持分析图租约。
/// 用例只注入替身预算源驱动真实状态机与真实级联修剪，结束时把预算源、压力级别、池设置与应用活跃闸复原。
/// </summary>
internal static class MemoryAdmissionChecks
{
    private const long Mib = 1024 * 1024;
    private const long Budget = 64 * Mib;

    private static readonly FieldInfo ProviderField =
        typeof(MemoryGovernor).GetField("_provider", BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException("MemoryGovernor._provider 字段不存在，准入替身无法注入。");

    public static void Run(Checks c)
    {
        c.Section("内存准入与缓冲池压力契约");

        // 在途解码会同时改变 ReservedBytes 与空闲池字节，先等前面的用例完全收尾再取基线。
        if (!SpinWait.SpinUntil(() => MemoryGovernor.ReservedBytes == 0, TimeSpan.FromSeconds(3)))
        {
            c.Fail("准入与缓冲池用例基线干净", $"等待 3s 后仍有 {MemoryGovernor.ReservedBytes} 字节在途预留");
            c.Skip("准入与缓冲池压力契约", "其他用例仍在解码，跳过以免在污染的状态上断言");
            return;
        }

        // 反射注入：不启动周期探测、不写用户目录日志，与设置页探测用例同一手法。
        var previousProvider = (IMemoryBudgetProvider?)ProviderField.GetValue(null);
        var previousMaxIdle = ImageBufferPool.MaxIdleBytes;
        var provider = new FakeBudgetProvider();
        ProviderField.SetValue(null, provider);

        try
        {
            RunAdmissionChecks(c, provider);
            RunPressureMatrixChecks(c, provider);
            RunConcurrentAdmissionChecks(c, provider);
            RunBufferPoolChecks(c, provider);
            RunCacheCapacityChecks(c, provider);
            RunBatchAnalysisAdmissionChecks(c, provider);
            RunBatchAnalysisFlowChecks(c, provider);
            RunAnalysisSchedulerChecks(c);
        }
        finally
        {
            try
            {
                // 先把池清空（Normal → Critical 的升级级联会 Trim 全部尺寸类），再逐级退到 Normal，
                // 避免把 Critical 级别与未清空的空闲池留给后续用例/宿主。
                DriveToLevel(provider, PressureLevel.Normal);
                DriveToLevel(provider, PressureLevel.Critical);
                ImageBufferPool.MaxIdleBytes = previousMaxIdle;
                DriveToLevel(provider, PressureLevel.Normal);
            }
            finally
            {
                ProviderField.SetValue(null, previousProvider);
            }
        }
    }

    // ---- 准入基础 ----

    private static void RunAdmissionChecks(Checks c, FakeBudgetProvider provider)
    {
        const long request = 8 * Mib;
        long cap = (long)(Budget * 0.50);
        long baseline = MemoryGovernor.ReservedBytes;

        DriveToLevel(provider, PressureLevel.Normal);
        c.Equal("准入用例起始在途预留为 0", 0L, baseline);

        // 上限内获批：记账即时可见
        bool granted = MemoryGovernor.TryAcquire(request, DecodePriority.P1_HighValue, out var first);
        c.True("Normal 预留上限内非 P0 获批", granted && first != null);
        c.Equal("获批立刻计入在途预留", baseline + request, MemoryGovernor.ReservedBytes);

        // 单次不超当前可用的一半（此处只改读数，不重跑探测以免级别跟着变）
        provider.AvailableBytes = 10 * Mib;
        bool tooLarge = MemoryGovernor.TryAcquire(6 * Mib, DecodePriority.P1_HighValue, out _);
        bool fitsHalf = MemoryGovernor.TryAcquire(4 * Mib, DecodePriority.P1_HighValue, out var fitsHalfPermit);
        c.True("单次申请超过当前可用一半被拒", !tooLarge);
        c.True("单次申请在可用一半内获批", fitsHalf && fitsHalfPermit != null);
        fitsHalfPermit?.Dispose();
        provider.AvailableBytes = long.MaxValue;

        // 连续申请到级别上限：总量不越上限，且被拒不再涨预留
        var permits = new List<MemoryGovernor.AdmissionPermit>();
        int extra = 0;
        while (extra < 32 && MemoryGovernor.TryAcquire(request, DecodePriority.P1_HighValue, out var permit) && permit != null)
        {
            permits.Add(permit);
            extra++;
        }

        long total = MemoryGovernor.ReservedBytes;
        c.True("Normal 预留总量封顶预算 50%", total <= cap && total + request > cap,
            $"在途预留 {total / Mib}MiB（上限 {cap / Mib}MiB），再加一次 {request / Mib}MiB 应越界");
        long refusedAt = total;
        bool refused = MemoryGovernor.TryAcquire(request, DecodePriority.P1_HighValue, out _);
        c.True("空闲额度不足时拒绝而非排队", !refused);
        c.Equal("被拒申请不涨在途预留", refusedAt, MemoryGovernor.ReservedBytes);

        foreach (var permit in permits) permit.Dispose();
        first?.Dispose();
        c.Equal("许可归还后在途预留归零", baseline, MemoryGovernor.ReservedBytes);

        // P0 豁免：Critical 也放行，且仍记账
        DriveToLevel(provider, PressureLevel.Critical);
        long p0Before = MemoryGovernor.ReservedBytes;
        bool p0Granted = MemoryGovernor.TryAcquire(1024 * Mib, DecodePriority.P0_Current, out var p0Permit);
        c.True("Critical 下 P0 仍放行", p0Granted && p0Permit != null);
        c.Equal("P0 放行仍记账", p0Before + 1024 * Mib, MemoryGovernor.ReservedBytes);
        p0Permit?.Dispose();
        c.Equal("P0 许可归还在途预留归零", p0Before, MemoryGovernor.ReservedBytes);

        // 未注入预算源的进程（工具/测试）准入全放行、不记账
        ProviderField.SetValue(null, null);
        try
        {
            long detachedBefore = MemoryGovernor.ReservedBytes;
            bool detached = MemoryGovernor.TryAcquire(request, DecodePriority.P3_Background, out var detachedPermit);
            c.True("未注入预算源时准入全放行", detached && detachedPermit != null);
            detachedPermit?.Dispose();
            c.Equal("未注入预算源时准入不记账", detachedBefore, MemoryGovernor.ReservedBytes);
        }
        finally
        {
            ProviderField.SetValue(null, provider);
        }
    }

    // ---- 压力矩阵 ----

    private static void RunPressureMatrixChecks(Checks c, FakeBudgetProvider provider)
    {
        const long request = 1 * Mib;

        DriveToLevel(provider, PressureLevel.Normal);
        c.Equal("可用 60% 落在 Normal", PressureLevel.Normal, MemoryGovernor.CurrentLevel);
        c.True("Normal 放行 P2 预取", Admits(provider, PressureLevel.Normal, DecodePriority.P2_Prefetch, request));
        c.True("Normal 放行 P3 后台", Admits(provider, PressureLevel.Normal, DecodePriority.P3_Background, request));

        DriveToLevel(provider, PressureLevel.Caution);
        c.Equal("可用 30% 落在 Caution", PressureLevel.Caution, MemoryGovernor.CurrentLevel);
        c.True("Caution 放行 P2 预取", Admits(provider, PressureLevel.Caution, DecodePriority.P2_Prefetch, request));
        c.True("Caution 放行 P3 后台", Admits(provider, PressureLevel.Caution, DecodePriority.P3_Background, request));

        DriveToLevel(provider, PressureLevel.Warning);
        c.Equal("可用 20% 落在 Warning", PressureLevel.Warning, MemoryGovernor.CurrentLevel);
        c.True("Warning 放行 P1", Admits(provider, PressureLevel.Warning, DecodePriority.P1_HighValue, request));
        c.True("Warning 拒绝 P2 预取", !Admits(provider, PressureLevel.Warning, DecodePriority.P2_Prefetch, request));
        c.True("Warning 拒绝 P3 后台", !Admits(provider, PressureLevel.Warning, DecodePriority.P3_Background, request));

        DriveToLevel(provider, PressureLevel.Critical);
        c.Equal("可用 10% 落在 Critical", PressureLevel.Critical, MemoryGovernor.CurrentLevel);
        c.True("Critical 拒绝 P1", !Admits(provider, PressureLevel.Critical, DecodePriority.P1_HighValue, request));
        c.True("Critical 拒绝 P2", !Admits(provider, PressureLevel.Critical, DecodePriority.P2_Prefetch, request));
        c.True("Critical 拒绝 P3", !Admits(provider, PressureLevel.Critical, DecodePriority.P3_Background, request));

        // 各级别的在途预留上限按预算比例收窄：Normal 50% / Caution 25% / Warning(P1) 10%
        CheckReservationCap(c, provider, "Normal 预留上限为预算 50%", PressureLevel.Normal,
            DecodePriority.P1_HighValue, (long)(Budget * 0.50));
        CheckReservationCap(c, provider, "Caution 预留上限为预算 25%", PressureLevel.Caution,
            DecodePriority.P1_HighValue, (long)(Budget * 0.25));
        CheckReservationCap(c, provider, "Warning 预留上限为预算 10%", PressureLevel.Warning,
            DecodePriority.P1_HighValue, (long)(Budget * 0.10));
    }

    /// <summary>单个申请在指定级别下是否获批（用完立即归还，不改变基线）。</summary>
    private static bool Admits(FakeBudgetProvider provider, PressureLevel level, DecodePriority priority, long bytes)
    {
        DriveToLevel(provider, level);
        long baseline = MemoryGovernor.ReservedBytes;
        bool granted = MemoryGovernor.TryAcquire(bytes, priority, out var permit);
        permit?.Dispose();
        if (granted && MemoryGovernor.ReservedBytes != baseline) return false;
        return granted;
    }

    /// <summary>
    /// 按 1MiB 逐次申请直到被拒，断言在途预留正好饱和在级别上限：不越上限，且再加一次 1MiB 就必然越界。
    /// </summary>
    private static void CheckReservationCap(
        Checks c, FakeBudgetProvider provider, string item, PressureLevel level, DecodePriority priority, long cap)
    {
        const long request = 1 * Mib;
        DriveToLevel(provider, level);

        var permits = new List<MemoryGovernor.AdmissionPermit>();
        while (permits.Count < 64 && MemoryGovernor.TryAcquire(request, priority, out var permit) && permit != null)
            permits.Add(permit);

        long reserved = MemoryGovernor.ReservedBytes;
        foreach (var permit in permits) permit.Dispose();

        c.True(item, reserved <= cap && reserved + request > cap,
            $"饱和后在途预留 {reserved / Mib}MiB，上限 {cap / Mib}MiB");
    }

    // ---- 并发准入 ----

    private static void RunConcurrentAdmissionChecks(Checks c, FakeBudgetProvider provider)
    {
        const int threadCount = 48;
        const int rounds = 4;
        const long request = 1 * Mib;
        long cap = (long)(Budget * 0.50);

        DriveToLevel(provider, PressureLevel.Normal);
        provider.AvailableBytes = long.MaxValue;

        bool capHeld = true;
        bool accounted = true;
        bool drained = true;
        bool anyGranted = false;
        long peakReserved = 0;

        for (int round = 0; round < rounds; round++)
        {
            long baseline = MemoryGovernor.ReservedBytes;
            var permits = new MemoryGovernor.AdmissionPermit?[threadCount];
            int granted = 0;
            var threads = new Thread[threadCount];
            using var barrier = new Barrier(threadCount);

            for (int i = 0; i < threadCount; i++)
            {
                int slot = i;
                threads[i] = new Thread(() =>
                {
                    barrier.SignalAndWait();
                    if (MemoryGovernor.TryAcquire(request, DecodePriority.P1_HighValue, out var permit))
                    {
                        permits[slot] = permit;
                        Interlocked.Increment(ref granted);
                    }
                })
                {
                    IsBackground = true,
                };
                threads[i].Start();
            }

            foreach (var thread in threads) thread.Join();

            long reserved = MemoryGovernor.ReservedBytes - baseline;
            if (reserved > cap) capHeld = false;
            if (reserved != granted * request) accounted = false;
            if (reserved > peakReserved) peakReserved = reserved;
            if (granted > 0) anyGranted = true;

            foreach (var permit in permits) permit?.Dispose();
            if (MemoryGovernor.ReservedBytes != baseline) drained = false;
        }

        c.True("并发非 P0 准入不越预留上限", capHeld,
            $"{threadCount} 线程同步起跑时在途预留峰值 {peakReserved / Mib}MiB 超过上限 {cap / Mib}MiB");
        c.True("并发准入记账与获批数一致", accounted,
            $"在途预留峰值 {peakReserved / Mib}MiB 与获批数换算不一致");
        c.True("并发许可归还后在途预留归零", drained);
        c.True("并发准入有申请获批", anyGranted);
    }

    // ---- 像素缓冲池 ----

    private static void RunBufferPoolChecks(Checks c, FakeBudgetProvider provider)
    {
        long previousMaxIdle = ImageBufferPool.MaxIdleBytes;
        try
        {
            // 触发池的静态构造（自登记为内存客户端），确保依赖级联 Trim 的断言有确定的起点
            _ = ImageBufferPool.Rent(0);

            // Critical 级联修剪清空空闲池，作为确定性基线
            DriveToLevel(provider, PressureLevel.Critical);
            c.Equal("压力修剪清空全部空闲缓冲", 0L, ImageBufferPool.IdleBytes);

            DriveToLevel(provider, PressureLevel.Normal);
            var big = ImageBufferPool.Rent(2 * (int)Mib);
            ImageBufferPool.Return(big);
            c.Equal("Normal 下大缓冲入池", (long)big.Length, ImageBufferPool.IdleBytes);

            var small = ImageBufferPool.Rent(4096);
            ImageBufferPool.Return(small);
            c.Equal("Normal 下小缓冲入池", (long)big.Length + small.Length, ImageBufferPool.IdleBytes);

            DriveToLevel(provider, PressureLevel.Caution);
            c.Equal("Caution 修剪只清 ≥1MB 空闲", (long)small.Length, ImageBufferPool.IdleBytes);

            // 持续 Caution：归还不再回填大缓冲（否则清理一次后又被重新填满）
            bool refilled = false;
            for (int i = 0; i < 8; i++)
            {
                var churn = ImageBufferPool.Rent(2 * (int)Mib);
                ImageBufferPool.Return(churn);
                if (ImageBufferPool.IdleBytes != small.Length) refilled = true;
            }
            c.True("持续 Caution 归还大缓冲不回填", !refilled,
                $"Caution 下空闲缓冲涨到 {ImageBufferPool.IdleBytes / Mib}MiB");

            DriveToLevel(provider, PressureLevel.Critical);
            c.Equal("Critical 清空小档空闲", 0L, ImageBufferPool.IdleBytes);
            var tiny = ImageBufferPool.Rent(4096);
            ImageBufferPool.Return(tiny);
            c.Equal("Critical 下归还任何缓冲都不保留", 0L, ImageBufferPool.IdleBytes);

            DriveToLevel(provider, PressureLevel.Normal);
            var recovered = ImageBufferPool.Rent(2 * (int)Mib);
            ImageBufferPool.Return(recovered);
            c.Equal("压力恢复后可正常池化", (long)recovered.Length, ImageBufferPool.IdleBytes);

            // 空闲上限：超帽直接丢弃，账目不留半截（先租出已入池的那份，再归还一份超帽的）
            ImageBufferPool.MaxIdleBytes = recovered.Length / 2;
            var overCap = ImageBufferPool.Rent(2 * (int)Mib);
            long idleAfterRent = ImageBufferPool.IdleBytes;
            ImageBufferPool.Return(overCap);
            c.Equal("超过空闲上限的缓冲不入池", idleAfterRent, ImageBufferPool.IdleBytes);
            ImageBufferPool.MaxIdleBytes = previousMaxIdle;

            RunPoolTrimRaceChecks(c, provider);
        }
        finally
        {
            ImageBufferPool.MaxIdleBytes = previousMaxIdle;
        }
    }

    /// <summary>归还与压力修剪的竞态：线程持续租还大缓冲，其间级别抬到 Critical，收尾后不得留任何大缓冲。</summary>
    private static void RunPoolTrimRaceChecks(Checks c, FakeBudgetProvider provider)
    {
        DriveToLevel(provider, PressureLevel.Normal);

        const int threadCount = 8;
        const int iterations = 4;
        var threads = new Thread[threadCount];
        using var start = new Barrier(threadCount);

        for (int i = 0; i < threadCount; i++)
        {
            threads[i] = new Thread(() =>
            {
                start.SignalAndWait();
                for (int iteration = 0; iteration < iterations; iteration++)
                {
                    var buffer = ImageBufferPool.Rent(2 * (int)Mib);
                    ImageBufferPool.Return(buffer);
                }
            })
            {
                IsBackground = true,
            };
            threads[i].Start();
        }

        DriveToLevel(provider, PressureLevel.Critical);
        foreach (var thread in threads) thread.Join();

        c.Equal("归还与修剪竞态后不留空闲大缓冲", 0L, ImageBufferPool.IdleBytes);
    }

    // ---- 有效缓存容量 ----

    /// <summary>缓存容量回归用的预算与实测驻留(推导值 ≈ 预算 55% − 驻留,远高于 256MB 下限)。</summary>
    private const long CacheBudget = 2L * 1024 * Mib;
    private const long CacheFootprint = 300 * Mib;
    private const long CacheUserLimit = 8L * 1024 * Mib;

    /// <summary>
    /// 有效缓存容量的真实口径回归:非缓存占用 = max(0, 实测 footprint − 主分区缓存),
    /// 所以缩略图与池空闲字节都留在缓存预算内被扣除,不会漏在预算之外。
    /// 用可调的假 footprint 配合真实池空闲字节验证 1:1 收窄,不做大额实际分配。
    /// </summary>
    private static void RunCacheCapacityChecks(Checks c, FakeBudgetProvider provider)
    {
        long previousMaxCache = BitmapLoader.MaxCacheSize;
        bool previousAutomatic = BitmapLoader.AutomaticCacheLimit;
        try
        {
            BitmapLoader.AutomaticCacheLimit = false;
            BitmapLoader.ClearCache();                      // 主分区归零,推导值可手算

            DriveToLevel(provider, PressureLevel.Normal);   // 池归还只在 Normal 入池
            provider.BudgetBytes = CacheBudget;
            provider.FootprintBytes = CacheFootprint;
            BitmapLoader.MaxCacheSize = CacheUserLimit;     // 用户上限高于推导值,不参与夹取
            MemoryGovernor.EvaluateNow();

            long derived = (long)(CacheBudget
                * (1 - (Math.Clamp(MemoryGovernor.CautionEnterPercent, 0, 90) + 5) / 100.0));
            long baseline = BitmapLoader.EffectiveCacheCapacityBytes;
            c.Equal("用户上限高于推导时容量落在推导值", derived - CacheFootprint, baseline);

            long idleBefore = ImageBufferPool.IdleBytes;
            var scratch = new List<byte[]>(3);
            for (int i = 0; i < 3; i++) scratch.Add(ImageBufferPool.Rent(2 * (int)Mib));
            foreach (var buffer in scratch) ImageBufferPool.Return(buffer);
            long idleDelta = ImageBufferPool.IdleBytes - idleBefore;
            c.True("池空闲经池 API 真实增长", idleDelta > 0, $"IdleBytes 未增长({idleDelta} 字节)");

            // 真实预算源的 footprint 含池空闲字节:空闲涨多少,容量就收窄多少
            provider.FootprintBytes = CacheFootprint + idleDelta;
            MemoryGovernor.EvaluateNow();
            c.Equal("池空闲计入非缓存占用(容量等额收窄)", baseline - idleDelta,
                BitmapLoader.EffectiveCacheCapacityBytes);

            // 缩略图等主分区以外的占用同样经 footprint 全额扣除
            provider.FootprintBytes = CacheFootprint + idleDelta + (10 * Mib);
            MemoryGovernor.EvaluateNow();
            c.Equal("主分区以外占用按 footprint 等额扣除", baseline - idleDelta - (10 * Mib),
                BitmapLoader.EffectiveCacheCapacityBytes);

            provider.FootprintBytes = derived + (100 * Mib);   // 推导为负
            MemoryGovernor.EvaluateNow();
            c.Equal("推导为负时仍由 256MB 下限兜底", 256 * Mib, BitmapLoader.EffectiveCacheCapacityBytes);
        }
        finally
        {
            provider.FootprintBytes = 0;
            provider.BudgetBytes = Budget;
            provider.AvailableBytes = long.MaxValue;
            BitmapLoader.MaxCacheSize = previousMaxCache;
            BitmapLoader.AutomaticCacheLimit = previousAutomatic;
            DriveToLevel(provider, PressureLevel.Critical);     // 清空池,与本节其他用例收尾一致
        }
    }

    // ---- 批量分析上下文与显式分析准入口 ----

    /// <summary>分析批次的工作集申请量（实测字节口径，非 P0）。</summary>
    private const long AnalysisRequest = 4 * Mib;

    /// <summary>取走本执行流作用域上记录的准入拒因（没有则 null）；与取图入口同一语义，取出即清空。</summary>
    private static AnalysisMemoryException? TakeRefusal()
    {
        try
        {
            BatchAnalysisContext.ThrowIfAdmissionRefused();
            return null;
        }
        catch (AnalysisMemoryException refusal)
        {
            return refusal;
        }
    }

    /// <summary>等在途预留归零（短超时：宁可跳过本节，也不挂住工具）。</summary>
    private static long DrainReserved()
    {
        SpinWait.SpinUntil(() => MemoryGovernor.ReservedBytes == 0, TimeSpan.FromSeconds(2));
        return MemoryGovernor.ReservedBytes;
    }

    /// <summary>
    /// 显式分析意图（<see cref="BatchAnalysisContext.IsActive"/>）的准入口真实调用回归：
    /// Caution/Warning 下 P3 分析申请按实测字节获批、同级别作用域外的普通 P3 仍被拒、Critical 仍拒、
    /// 可用量 0 走数值判据（不靠刷新级别）、许可归还在途预留回基线。
    /// 全节只走公开 API，不做真实解码，也不持分析图租约（否则第二条 root 会无限等）。
    /// </summary>
    private static void RunBatchAnalysisAdmissionChecks(Checks c, FakeBudgetProvider provider)
    {
        c.Section("批量分析上下文与显式分析准入口");

        long baseline = DrainReserved();
        if (baseline != 0)
        {
            c.Skip("批量分析准入口契约", $"在途预留 {baseline} 字节未归零，跳过以免在污染的账本上断言");
            return;
        }

        // 作用域外（无分析意图）：Warning 起普通 P3 仍按压力级别拒绝
        DriveToLevel(provider, PressureLevel.Warning);
        bool outsideGranted = MemoryGovernor.TryAcquire(AnalysisRequest, DecodePriority.P3_Background, out _);
        c.True("Warning 下作用域外普通 P3 仍被拒", !outsideGranted);
        c.Equal("作用域外被拒不涨在途预留", baseline, MemoryGovernor.ReservedBytes);

        // 作用域内：Caution/Warning 不按级别拒绝，改按实测预算/可用量/在途判据
        foreach (var level in new[] { PressureLevel.Caution, PressureLevel.Warning })
        {
            DriveToLevel(provider, level);
            using var scope = BatchAnalysisContext.Enter();
            c.True($"{level} 下作用域内声明为分析批次", BatchAnalysisContext.IsActive);

            bool granted = MemoryGovernor.TryAcquire(AnalysisRequest, DecodePriority.P3_Background, out var permit);
            c.True($"{level} 下作用域内 P3 分析申请按实测字节获批", granted && permit != null);
            c.Equal($"{level} 下获批即时计入在途预留", baseline + AnalysisRequest, MemoryGovernor.ReservedBytes);

            permit?.Dispose();
            c.Equal($"{level} 下许可归还在途预留回基线", baseline, MemoryGovernor.ReservedBytes);
            c.True($"{level} 下获批不记准入拒因", TakeRefusal() == null);
        }

        c.True("退出作用域后不再处于分析批次", !BatchAnalysisContext.IsActive);

        // Critical：分析意图也不放行（只保留 P0 豁免）
        DriveToLevel(provider, PressureLevel.Critical);
        using (var criticalScope = BatchAnalysisContext.Enter())
        {
            bool granted = MemoryGovernor.TryAcquire(AnalysisRequest, DecodePriority.P3_Background, out var permit);
            permit?.Dispose();
            var refusal = TakeRefusal();
            string reason = refusal?.Reason ?? string.Empty;
            c.True("Critical 下作用域内分析申请仍被拒", !granted);
            c.True("Critical 拒绝原因指向严重不足", reason.Contains("严重"), refusal == null ? "未记录拒绝原因" : "原因=" + reason);
            c.Equal("Critical 被拒不涨在途预留", baseline, MemoryGovernor.ReservedBytes);
        }

        // 可用量 0：只改读数、不跑探测（级别留在 Caution），拒绝必须来自数值判据
        DriveToLevel(provider, PressureLevel.Caution);
        var levelBefore = MemoryGovernor.CurrentLevel;
        provider.AvailableBytes = 0;
        using (var emptyScope = BatchAnalysisContext.Enter())
        {
            bool granted = MemoryGovernor.TryAcquire(AnalysisRequest, DecodePriority.P3_Background, out var permit);
            permit?.Dispose();
            var refusal = TakeRefusal();
            string reason = refusal?.Reason ?? string.Empty;
            c.True("可用量 0 时分析申请被拒", !granted);
            c.True("可用量 0 的拒绝来自可用量判据（不是 Critical 级别）",
                reason.Contains("可用内存不足") && !reason.Contains("严重"),
                refusal == null ? "未记录拒绝原因" : "原因=" + reason);
            c.Equal("可用量 0 被拒不涨在途预留", baseline, MemoryGovernor.ReservedBytes);
        }
        c.Equal("可用量 0 不改压力级别（拒绝不靠刷新级别）", levelBefore, MemoryGovernor.CurrentLevel);
        provider.AvailableBytes = long.MaxValue;

        // 回到 Normal：作用域外普通 P3 恢复获批，只在级别/数值判据上被拒过
        DriveToLevel(provider, PressureLevel.Normal);
        bool normalGranted = MemoryGovernor.TryAcquire(AnalysisRequest, DecodePriority.P3_Background, out var normalPermit);
        normalPermit?.Dispose();
        c.True("Normal 下作用域外普通 P3 获批", normalGranted);
        c.Equal("作用域外许可归还后在途预留归零", baseline, MemoryGovernor.ReservedBytes);
    }

    /// <summary>
    /// 作用域隔离与取消契约的真实调用回归：拒因只记在本操作作用域上（嵌套内层不继承外层、外层不被内层覆盖、
    /// 并发执行流互不可见）；作用域令牌链在调用方与外层作用域上；应用代际进后台即取消，回前台换新代际且不复活旧令牌。
    /// </summary>
    private static void RunBatchAnalysisFlowChecks(Checks c, FakeBudgetProvider provider)
    {
        c.Section("批量分析作用域隔离与取消契约");

        long baseline = DrainReserved();

        // 嵌套作用域：拒因归属各自一层
        DriveToLevel(provider, PressureLevel.Caution);
        provider.AvailableBytes = 0;    // 只改读数：作用域内的申请都在数值判据上被拒
        try
        {
            using (var outerScope = BatchAnalysisContext.Enter())
            {
                MemoryGovernor.TryAcquire(AnalysisRequest, DecodePriority.P3_Background, out _);
                using (var innerScope = BatchAnalysisContext.Enter())
                {
                    c.True("嵌套内层作用域不继承外层拒因", TakeRefusal() == null);
                    MemoryGovernor.TryAcquire(AnalysisRequest, DecodePriority.P3_Background, out _);
                    var innerRefusal = TakeRefusal();
                    c.True("嵌套内层作用域记录自己的拒因",
                        innerRefusal != null && innerRefusal.Bytes == AnalysisRequest,
                        innerRefusal == null ? "内层未记录拒因" : "字节数 " + innerRefusal.Bytes);
                }

                var outerRefusal = TakeRefusal();
                c.True("外层拒因在内层退出后仍归属外层",
                    outerRefusal != null && outerRefusal.Bytes == AnalysisRequest,
                    outerRefusal == null ? "外层拒因被内层覆盖或清除" : "字节数 " + outerRefusal.Bytes);
            }
        }
        finally
        {
            provider.AvailableBytes = long.MaxValue;
        }

        c.True("嵌套作用域全部退出后不在分析批次内", !BatchAnalysisContext.IsActive);

        // 并发执行流（两条并行 stage）：一条记录拒绝、一条正常获批，拒因互不可见
        DriveToLevel(provider, PressureLevel.Caution);
        long oversized = (long)(Budget * 0.75);     // 超过 min(预算 50%, 可用 50%) 上限 → 与可用读数无关地必拒
        bool refusedFlowRefused = false;
        bool refusedFlowReasoned = false;
        bool grantedFlowGranted = false;
        bool grantedFlowClean = false;

        using (var start = new Barrier(2))
        {
            var refusedFlow = Task.Run(() =>
            {
                using var scope = BatchAnalysisContext.Enter();
                start.SignalAndWait(TimeSpan.FromSeconds(10));
                bool granted = MemoryGovernor.TryAcquire(oversized, DecodePriority.P3_Background, out var permit);
                permit?.Dispose();
                refusedFlowRefused = !granted;
                refusedFlowReasoned = TakeRefusal()?.Bytes == oversized;
            });

            var grantedFlow = Task.Run(() =>
            {
                using var scope = BatchAnalysisContext.Enter();
                start.SignalAndWait(TimeSpan.FromSeconds(10));
                bool granted = MemoryGovernor.TryAcquire(AnalysisRequest, DecodePriority.P3_Background, out var permit);
                permit?.Dispose();
                grantedFlowGranted = granted;
                grantedFlowClean = TakeRefusal() == null;
            });

            bool completed;
            string detail = string.Empty;
            try
            {
                completed = Task.WaitAll(new[] { refusedFlow, grantedFlow }, TimeSpan.FromSeconds(10));
            }
            catch (AggregateException failure)
            {
                completed = false;
                detail = failure.InnerException?.Message ?? failure.Message;
            }

            c.True("并发分析作用域用例按时收尾", completed, detail.Length > 0 ? detail : "10s 内未收尾");
        }

        c.True("超上限的分析申请被拒并记在自己的作用域上", refusedFlowRefused && refusedFlowReasoned);
        c.True("另一条并发执行流不受其拒因影响（正常获批且无拒因）", grantedFlowGranted && grantedFlowClean);
        c.Equal("并发作用域许可归还后在途预留回基线", baseline, MemoryGovernor.ReservedBytes);

        // 取消令牌：调用方令牌链到外层作用域，外层链到内层；退出作用域后回到未激活
        c.True("进入作用域前不存在批次令牌", !BatchAnalysisContext.CancellationToken().CanBeCanceled);
        using (var caller = new CancellationTokenSource())
        {
            using (var outer = BatchAnalysisContext.Enter(caller.Token))
            {
                c.True("作用域令牌链在调用方令牌上（未取消时可用）",
                    !BatchAnalysisContext.CancellationToken().IsCancellationRequested);
                using (var inner = BatchAnalysisContext.Enter())
                {
                    caller.Cancel();
                    c.True("调用方取消后内层作用域令牌同步取消",
                        BatchAnalysisContext.CancellationToken().IsCancellationRequested);
                }

                c.True("调用方取消后外层作用域令牌同步取消",
                    BatchAnalysisContext.CancellationToken().IsCancellationRequested);
            }
        }
        c.True("退出全部作用域后批次令牌回到未激活",
            !BatchAnalysisContext.CancellationToken().CanBeCanceled);

        // 应用活跃闸：进后台取消当前代际，回前台换新代际；已取消的批次不因回前台复活
        var activeGeneration = BatchAnalysisContext.ApplicationToken;
        c.True("默认在前台且当前代际未取消",
            BatchAnalysisContext.IsApplicationActive && !activeGeneration.IsCancellationRequested);
        try
        {
            BatchAnalysisContext.SetApplicationActive(false);
            c.True("进后台立即报告为非活跃并取消当前代际",
                !BatchAnalysisContext.IsApplicationActive && activeGeneration.IsCancellationRequested);

            using (var backgroundScope = BatchAnalysisContext.Enter())
                c.True("后台期间进入的作用域继承已取消的代际令牌",
                    BatchAnalysisContext.CancellationToken().IsCancellationRequested);
        }
        finally
        {
            BatchAnalysisContext.SetApplicationActive(true);
        }

        c.True("回前台换新代际且未取消",
            BatchAnalysisContext.IsApplicationActive
            && !BatchAnalysisContext.ApplicationToken.IsCancellationRequested
            && BatchAnalysisContext.ApplicationToken != activeGeneration);
        c.True("回前台不复活已取消的旧代际令牌", activeGeneration.IsCancellationRequested);
        using (var foregroundScope = BatchAnalysisContext.Enter())
            c.True("回前台后的新作用域令牌可用", !BatchAnalysisContext.CancellationToken().IsCancellationRequested);

        DriveToLevel(provider, PressureLevel.Normal);
    }

    /// <summary>
    /// 分析 CPU 调度器的通道互斥回归：独占通道（native 推理）占用期间并发通道一个都不开跑，
    /// 独占任务之间也不重叠。用短 Task 加信号量验证，不做 CPU 重负载，全部等待都有短超时。
    /// </summary>
    private static void RunAnalysisSchedulerChecks(Checks c)
    {
        c.Section("分析 CPU 调度器通道互斥");

        var exclusiveEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseExclusive = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        int concurrentRuns = 0;

        // 独占通道占住执行槽直到测试放行（同步等待才在槽内，异步让出即释放槽）
        var exclusive = Task.Factory.StartNew(
            () =>
            {
                exclusiveEntered.TrySetResult(true);
                releaseExclusive.Task.Wait(TimeSpan.FromSeconds(10));
            },
            CancellationToken.None,
            TaskCreationOptions.None,
            BatchAnalysisContext.CpuExclusiveScheduler);

        Task[] concurrent = Array.Empty<Task>();
        try
        {
            bool entered = exclusiveEntered.Task.Wait(TimeSpan.FromSeconds(10));
            c.True("独占通道任务按时开始", entered, "10s 内独占任务未开始");

            if (entered)
            {
                concurrent = new[]
                {
                    Task.Factory.StartNew(() => { Interlocked.Increment(ref concurrentRuns); }, CancellationToken.None,
                        TaskCreationOptions.None, BatchAnalysisContext.CpuScheduler),
                    Task.Factory.StartNew(() => { Interlocked.Increment(ref concurrentRuns); }, CancellationToken.None,
                        TaskCreationOptions.None, BatchAnalysisContext.CpuScheduler),
                };

                // 独占通道持槽期间的观察窗（短睡眠，非负载压测）
                Thread.Sleep(200);
                c.Equal("独占通道运行期间并发通道未开跑", 0, Volatile.Read(ref concurrentRuns));
            }

            releaseExclusive.TrySetResult(true);

            if (concurrent.Length > 0)
            {
                bool ran = Task.WaitAll(concurrent, TimeSpan.FromSeconds(10));
                c.True("独占通道释放后并发通道任务全部执行",
                    ran && Volatile.Read(ref concurrentRuns) == concurrent.Length,
                    $"已完成 {Volatile.Read(ref concurrentRuns)}/{concurrent.Length}");
            }

            c.True("独占通道任务按时收尾", exclusive.Wait(TimeSpan.FromSeconds(10)), "10s 内独占任务未收尾");
        }
        finally
        {
            releaseExclusive.TrySetResult(true);
        }

        // 独占任务之间互不重叠
        int exclusiveActive = 0;
        int overlap = 0;
        Task ExclusiveProbe() => Task.Factory.StartNew(
            () =>
            {
                if (Interlocked.Increment(ref exclusiveActive) != 1) Interlocked.Increment(ref overlap);
                Thread.Sleep(100);
                Interlocked.Decrement(ref exclusiveActive);
            },
            CancellationToken.None,
            TaskCreationOptions.None,
            BatchAnalysisContext.CpuExclusiveScheduler);

        var probes = new[] { ExclusiveProbe(), ExclusiveProbe() };
        bool probesRan = Task.WaitAll(probes, TimeSpan.FromSeconds(15));
        c.True("独占通道任务之间不重叠", probesRan && Volatile.Read(ref overlap) == 0,
            probesRan ? $"观测到 {overlap} 次重叠" : "独占探测任务未按时收尾");
    }

    // ---- 替身预算源与级别驱动 ----

    /// <summary>
    /// 把级别驱动到目标值：先按目标级别的典型可用比例设读数，再跑真实探测（探测一次只退一级，
    /// 所以可能要多轮）。驱动结束后把可用读数放回 long.MaxValue，准入用例只看级别与单次申请闸门。
    /// </summary>
    private static void DriveToLevel(FakeBudgetProvider provider, PressureLevel target)
    {
        double percent = target switch
        {
            PressureLevel.Normal => 60,
            PressureLevel.Caution => 30,
            PressureLevel.Warning => 20,
            _ => 10,
        };

        provider.BudgetBytes = Budget;
        provider.AvailableBytes = (long)(Budget * percent / 100.0);
        for (int i = 0; i < 8 && MemoryGovernor.CurrentLevel != target; i++)
            MemoryGovernor.EvaluateNow();

        provider.AvailableBytes = long.MaxValue;
    }

    private sealed class FakeBudgetProvider : IMemoryBudgetProvider
    {
        private long _budget = Budget;
        private long _available = long.MaxValue;
        private long _footprint;

        public long BudgetBytes
        {
            get => Interlocked.Read(ref _budget);
            set => Interlocked.Exchange(ref _budget, value);
        }

        public long AvailableBytes
        {
            get => Interlocked.Read(ref _available);
            set => Interlocked.Exchange(ref _available, value);
        }

        /// <summary>可调的实测驻留(缓存容量推导用);0 = 未知,容量只按用户上限。</summary>
        public long FootprintBytes
        {
            get => Interlocked.Read(ref _footprint);
            set => Interlocked.Exchange(ref _footprint, value);
        }

        public long GetBudgetBytes() => BudgetBytes;

        public long GetAvailableBytes() => AvailableBytes;

        public long GetFootprintBytes() => FootprintBytes;

        public SystemMemoryInfo GetSystemMemoryInfo() => new(16L * 1024 * Mib, AvailableBytes);
    }
}
