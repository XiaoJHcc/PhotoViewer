using System.Diagnostics;
using System.Text;
using PhotoViewer.Core.Image;
using PhotoViewer.Core.Storage;

namespace PhotoViewer.Apple;

/// <summary>
/// 验收专用（测试钩子）：iOS RAW 单路准入与提前窗口选择规则。
/// 只驱动准入闸与纯规划函数，用合成路径代替素材：不读用户照片、不创建 RAW 解码。
/// </summary>
public static class AppleRawDecodeChecks
{
    private static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan IdleProbe = TimeSpan.FromMilliseconds(150);
    private static readonly Func<bool> NotAlongside = static () => false;
    private static readonly Func<bool> AlongsideLane = static () => true;
    private static readonly Func<DecodePriority> Normal = static () => DecodePriority.P2_Prefetch;
    private static readonly Func<DecodePriority> Foreground = static () => DecodePriority.P0_Current;

    /// <summary>跑完整套准入检查；报告逐项落盘，返回全部检查是否通过。</summary>
    /// <param name="reportPath">报告文本路径。</param>
    public static Task<bool> RunAsync(string reportPath) => RunCoreAsync(reportPath);

    private static async Task<bool> RunCoreAsync(string reportPath)
    {
        var lines = new List<string>
        {
            "PhotoViewer iOS RAW 单路准入验收",
            "gate=single-slot order=foreground>alongside>background recheck=16ms ticket=promotable",
        };
        int passed = 0, failed = 0;

        async Task CheckAsync(string name, Func<RawDecodeAdmission, CancellationToken, Task> body)
        {
            var gate = new RawDecodeAdmission();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            try
            {
                await body(gate, cts.Token).ConfigureAwait(false);
                Assert(gate.ActiveCount == 0 && gate.WaitingCount == 0,
                    $"检查结束时仍有租约或等待者 active={gate.ActiveCount} waiting={gate.WaitingCount}");
                passed++;
                lines.Add($"PASS {name}");
            }
            catch (Exception exception)
            {
                failed++;
                lines.Add($"FAIL {name}: {exception.GetType().Name}: {exception.Message}");
            }
            finally
            {
                cts.Cancel();
            }
        }

        await CheckAsync("单路：同时只放行一个 RAW", async (gate, ct) =>
        {
            var first = await gate.AcquireAsync(Foreground, NotAlongside, ct).ConfigureAwait(false);
            try
            {
                var second = gate.AcquireAsync(Foreground, NotAlongside, ct);
                await Task.Delay(IdleProbe, CancellationToken.None).ConfigureAwait(false);
                Assert(!second.IsCompleted, "首路未释放时第二路就拿到了名额");
                Assert(gate.ActiveCount == 1, $"在途计数应为 1，实得 {gate.ActiveCount}");
                first.Dispose();
                using var secondLease = await second.WaitAsync(WaitTimeout).ConfigureAwait(false);
                Assert(gate.ActiveCount == 1 && gate.WaitingCount == 0,
                    $"第二路拿到名额后计数异常 active={gate.ActiveCount} waiting={gate.WaitingCount}");
            }
            finally
            {
                first.Dispose();
            }
        }).ConfigureAwait(false);

        await CheckAsync("等待取消：退出等待且名额可再获取", async (gate, ct) =>
        {
            var held = await gate.AcquireAsync(Foreground, NotAlongside, ct).ConfigureAwait(false);
            try
            {
                using var waitCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                var waiting = gate.AcquireAsync(Foreground, NotAlongside, waitCts.Token);
                Assert(await WaitUntilAsync(() => gate.WaitingCount == 1, WaitTimeout).ConfigureAwait(false),
                    "等待者未进入队列");
                waitCts.Cancel();
                bool cancelled = false;
                try
                {
                    await waiting.ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    cancelled = true;
                }

                Assert(cancelled, "取消后未退出等待");
                Assert(gate.WaitingCount == 0, $"取消的等待者仍留在队列 waiting={gate.WaitingCount}");
                held.Dispose();
                using var again = await gate.AcquireAsync(Foreground, NotAlongside, ct).WaitAsync(WaitTimeout).ConfigureAwait(false);
                Assert(gate.ActiveCount == 1, $"重新获取后计数应为 1，实得 {gate.ActiveCount}");
            }
            finally
            {
                held.Dispose();
            }
        }).ConfigureAwait(false);

        await CheckAsync("普通后台在 P0 在途时让位", async (gate, ct) =>
        {
            using var bgCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            using var p0 = DecodeScheduler.TrackP0();
            var background = gate.AcquireAsync(Normal, NotAlongside, bgCts.Token);
            await Task.Delay(IdleProbe, CancellationToken.None).ConfigureAwait(false);
            Assert(!background.IsCompleted, "P0 在途时普通后台 RAW 拿到了名额");
            var alongsideTask = gate.AcquireAsync(Normal, AlongsideLane, ct);
            using var alongsideLease = await alongsideTask.WaitAsync(WaitTimeout).ConfigureAwait(false);
            Assert(!background.IsCompleted, "旁路获得名额后普通后台仍不应放行");
            alongsideLease.Dispose();
            p0.Dispose();
            if (DecodeScheduler.P0InFlightCount != 0)
            {
                // 真实 P0 仍在途：只核对让位，收掉本检查自己的等待者。
                bgCts.Cancel();
                try
                {
                    await background.ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                }

                Assert(gate.WaitingCount == 0, $"取消后仍留等待者 waiting={gate.WaitingCount}");
                return;
            }

            using var backgroundLease = await background.WaitAsync(WaitTimeout).ConfigureAwait(false);
            Assert(gate.ActiveCount == 1, $"P0 结束后普通后台应取得名额，实得 active={gate.ActiveCount}");
        }).ConfigureAwait(false);

        await CheckAsync("P0 优先：前台 RAW 越过先到的旁路", async (gate, ct) =>
        {
            var held = await gate.AcquireAsync(Foreground, NotAlongside, ct).ConfigureAwait(false);
            IDisposable? foreground = null, alongside = null;
            try
            {
                var alongsideTask = gate.AcquireAsync(Normal, AlongsideLane, ct);
                Assert(await WaitUntilAsync(() => gate.WaitingCount == 1, WaitTimeout).ConfigureAwait(false),
                    "旁路等待者未进入队列");
                var foregroundTask = gate.AcquireAsync(Foreground, NotAlongside, ct);
                Assert(await WaitUntilAsync(() => gate.WaitingCount == 2, WaitTimeout).ConfigureAwait(false),
                    "前台等待者未进入队列");
                held.Dispose();
                foreground = await foregroundTask.WaitAsync(WaitTimeout).ConfigureAwait(false);
                Assert(!alongsideTask.IsCompleted, "旁路在等待中的前台 RAW 之前拿到了名额");
                foreground.Dispose();
                foreground = null;
                alongside = await alongsideTask.WaitAsync(WaitTimeout).ConfigureAwait(false);
            }
            finally
            {
                alongside?.Dispose();
                foreground?.Dispose();
                held.Dispose();
            }
        }).ConfigureAwait(false);

        await CheckAsync("票面提升：等待中被提升为前台后先拿名额", async (gate, ct) =>
        {
            var held = await gate.AcquireAsync(Foreground, NotAlongside, ct).ConfigureAwait(false);
            var priority = DecodePriority.P2_Prefetch;
            IDisposable? promotedLease = null, alongsideLease = null;
            try
            {
                var promotedTask = gate.AcquireAsync(() => priority, NotAlongside, ct);
                Assert(await WaitUntilAsync(() => gate.WaitingCount == 1, WaitTimeout).ConfigureAwait(false),
                    "待提升等待者未进入队列");
                var alongsideTask = gate.AcquireAsync(Normal, AlongsideLane, ct);
                Assert(await WaitUntilAsync(() => gate.WaitingCount == 2, WaitTimeout).ConfigureAwait(false),
                    "旁路等待者未进入队列");
                // 模拟 DecodeTicket.Promote：等待期间票面就地升为 P0。
                priority = DecodePriority.P0_Current;
                held.Dispose();
                promotedLease = await promotedTask.WaitAsync(WaitTimeout).ConfigureAwait(false);
                Assert(!alongsideTask.IsCompleted, "提升后的前台票面仍被旁路越过");
                promotedLease.Dispose();
                promotedLease = null;
                alongsideLease = await alongsideTask.WaitAsync(WaitTimeout).ConfigureAwait(false);
            }
            finally
            {
                alongsideLease?.Dispose();
                promotedLease?.Dispose();
                held.Dispose();
            }
        }).ConfigureAwait(false);

        await CheckAsync("租约重复释放：计数不破坏", async (gate, ct) =>
        {
            var lease = await gate.AcquireAsync(Foreground, NotAlongside, ct).ConfigureAwait(false);
            lease.Dispose();
            lease.Dispose();
            Assert(gate.ActiveCount == 0 && gate.WaitingCount == 0,
                $"重复释放后计数异常 active={gate.ActiveCount} waiting={gate.WaitingCount}");
            var again = await gate.AcquireAsync(Foreground, NotAlongside, ct).WaitAsync(WaitTimeout).ConfigureAwait(false);
            try
            {
                Assert(gate.ActiveCount == 1, $"再次获取后计数应为 1，实得 {gate.ActiveCount}");
            }
            finally
            {
                again.Dispose();
            }

            Assert(gate.ActiveCount == 0, $"释放后计数应为 0，实得 {gate.ActiveCount}");
        }).ConfigureAwait(false);

        await CheckAsync("规划：方向上最近 RAW 进窗口", (_, _) =>
        {
            var window = Files("a.jpg", "b.jpg", "c.arw", "d.jpg");
            Assert(BitmapPrefetcher.SelectRawAlongsideIndex(window, 0, 1, 6, 6, 6) == 2,
                "方向 +2 的 RAW 未被选中");
            Assert(BitmapPrefetcher.SelectRawAlongsideIndex(window, 0, 1, 6, 6, 2) == 2,
                "窗口 2 内方向 +2 的 RAW 未被选中");

            var far = Files("a.jpg", "b.jpg", "c.jpg", "d.arw", "e.jpg");
            Assert(BitmapPrefetcher.SelectRawAlongsideIndex(far, 0, 1, 6, 6, 6) == 3,
                "方向 +3 的 RAW 未进扩展窗口");
            Assert(BitmapPrefetcher.SelectRawAlongsideIndex(far, 0, 1, 6, 6, 2) == -1,
                "默认窗口选中了 +3 的 RAW");

            var outside = Files("0.jpg", "1.jpg", "2.jpg", "3.jpg", "4.jpg", "5.jpg", "6.jpg", "7.arw");
            Assert(BitmapPrefetcher.SelectRawAlongsideIndex(outside, 0, 1, 6, 6, 6) == -1,
                "窗口外（+7）的 RAW 被选中");
            Assert(BitmapPrefetcher.SelectRawAlongsideIndex(outside, 0, 1, 3, 6, 6) == -1,
                "超出配置预载份数的 RAW 被选中");

            var nearest = Files("a.jpg", "b.jpg", "c.arw", "d.jpg", "e.arw");
            Assert(BitmapPrefetcher.SelectRawAlongsideIndex(nearest, 0, 1, 6, 6, 6) == 2,
                "两张 RAW 时未选最近的一张");

            var backward = Files("0.jpg", "1.jpg", "2.jpg", "3.arw", "4.jpg", "5.jpg", "6.jpg");
            Assert(BitmapPrefetcher.SelectRawAlongsideIndex(backward, 6, -1, 6, 6, 6) == 3,
                "反方向翻页时未沿实际方向选中 RAW");
            Assert(BitmapPrefetcher.SelectRawAlongsideIndex(backward, 6, -1, 6, 2, 6) == -1,
                "反方向未受配置预载份数约束");

            Assert(BitmapPrefetcher.SelectRawAlongsideIndex(backward, 6, 1, 6, 6, 6) == -1,
                "末图向后翻页越界");
            Assert(BitmapPrefetcher.SelectRawAlongsideIndex(Files("a.jpg", "b.jpg"), 0, 1, 6, 6, 6) == -1,
                "没有 RAW 时仍选中了邻居");
            return Task.CompletedTask;
        }).ConfigureAwait(false);

        await CheckAsync("尺寸档：Full 需求的方向预取不解无用代理", (_, _) =>
        {
            Assert(!BitmapPrefetcher.ShouldPrefetchFull(3580, false, 1, true), "适应态邻居应只取代理");
            Assert(BitmapPrefetcher.ShouldPrefetchFull(3580, true, 1, false), "Full 需求的反方向近邻未取 Full");
            Assert(BitmapPrefetcher.ShouldPrefetchFull(3580, true, 2, true), "方向 +2 仍取代理，会触发重复解码");
            Assert(BitmapPrefetcher.ShouldPrefetchFull(3580, true, 4, true), "提前 RAW 仍取代理，会触发重复开发");
            Assert(!BitmapPrefetcher.ShouldPrefetchFull(3580, true, 4, false), "普通远邻不应扩大为 Full");
            Assert(BitmapPrefetcher.ShouldPrefetchFull(0, false, 4, false), "无代理目标时旧 Full 口径被改变");
            return Task.CompletedTask;
        }).ConfigureAwait(false);

        lines.Add($"completed=1 success={(failed == 0 ? 1 : 0)} passed={passed} failed={failed}");
        lines.Add(failed == 0 ? "SUMMARY PASS" : "SUMMARY FAIL");

        string? directory = Path.GetDirectoryName(Path.GetFullPath(reportPath));
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        File.WriteAllLines(reportPath, lines, new UTF8Encoding(false));
        Console.WriteLine($"[NativeSmoke] {(failed == 0 ? "PASS" : "FAIL")} raw-scheduling ({passed}/{passed + failed}) {reportPath}");
        return failed == 0;
    }

    private static List<ImageFile> Files(params string[] names)
        => names.Select(name => new ImageFile(new LocalFileRef("/pv-raw-scheduling/" + name))).ToList();

    private static async Task<bool> WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var watch = Stopwatch.StartNew();
        while (!condition())
        {
            if (watch.Elapsed > timeout) return false;
            await Task.Delay(5).ConfigureAwait(false);
        }

        return true;
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
