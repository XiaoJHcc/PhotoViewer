using System.Reflection;
using PhotoViewer.Core.Image;
using PhotoViewer.Core.Memory;

namespace CoreContractCheck;

/// <summary>
/// 内存准入与像素缓冲池的压力契约：
/// 非 P0 并发准入的检查与记账必须原子提交（不越级别预留上限）、被拒申请不涨在途预留、许可归还归零、
/// P0 始终豁免但记账、Warning/Critical 优先级矩阵一致；
/// 池在持续压力下 Trim 之后不得被后续归还重新填满，恢复 Normal 后可正常池化。
/// 用例只注入替身预算源驱动真实状态机与真实级联修剪，结束时把预算源、压力级别与池设置复原。
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
