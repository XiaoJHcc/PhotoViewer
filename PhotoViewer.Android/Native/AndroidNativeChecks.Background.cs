using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading.Tasks;
using PhotoViewer.Core.Image;
using PhotoViewer.Core.Memory;
using PhotoViewer.ViewModels.Main;

namespace PhotoViewer.Android.Native;

/// <summary>
/// <c>--es pv-check background</c>（验收专用）：后台预取暂停/恢复的接线与语义。
/// 全部走生产入口 <see cref="AndroidNativeShell.SetApplicationActive"/>（Activity 的 OnStart/OnStop 调用的就是它），
/// 用真实素材与真实缓存观察：默认活跃 → 进后台后不再排新工作且不谎报内存压力 → 重复上报幂等 →
/// 回前台换新代际令牌并按存活实例重新计划 → 恢复后邻图预取照常推进。
/// 不注入输入事件、不切前后台任务（真机禁输入注入时也能跑）。
/// </summary>
internal static partial class AndroidNativeChecks
{
    /// <summary>后台预取暂停/恢复的验收入口；由独立模式分发调用。</summary>
    internal static async Task RunBackgroundPrefetchChecksAsync()
    {
        await WaitForScreenAsync();
        string folder = EnsureLayoutFolder(_activity!);
        await OpenFolderAndWaitAsync(folder);
        // 先让首轮邻居与当前图预热跑完，后面的观察点只看"新增"的投递与状态
        await Task.Delay(600);

        var main = _main!;
        var baselineToken = BitmapPrefetcher.ApplicationLifetimeToken;
        var levelBefore = MemoryGovernor.CurrentLevel;
        bool holdBefore = MemoryGovernor.InRecoveryHold;
        int pausesBefore = BitmapPrefetcher.BackgroundPauseCount;
        int resumesBefore = BitmapPrefetcher.ForegroundResumeCount;
        int replanBefore = BitmapPrefetcher.ForegroundReplanCount;
        int suspendedBefore = BitmapPrefetcher.SuspendedScheduleCount;

        await RunStepAsync("background 生命周期接线", () =>
        {
            var type = typeof(AndroidNativeActivity);
            bool declared(string name)
                => type.GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)?.DeclaringType == type;
            Check("background Activity 自带 OnStart/OnStop 重写", declared("OnStart") && declared("OnStop"),
                $"OnStart={declared("OnStart")} OnStop={declared("OnStop")}");
            Check("background 默认活跃",
                BitmapPrefetcher.IsApplicationActive && !baselineToken.IsCancellationRequested,
                $"active={BitmapPrefetcher.IsApplicationActive} tokenCancelled={baselineToken.IsCancellationRequested}");
            return Task.CompletedTask;
        });

        await RunStepAsync("background 进入后台", () =>
        {
            bool changed = AndroidNativeShell.SetApplicationActive(false);
            Check("background 进后台报告状态变化", changed, $"changed={changed}");
            Check("background 进后台后状态不活跃", !BitmapPrefetcher.IsApplicationActive,
                $"active={BitmapPrefetcher.IsApplicationActive}");
            Check("background 进后台取消在途代际令牌", baselineToken.IsCancellationRequested,
                $"cancelled={baselineToken.IsCancellationRequested}");
            Check("background 暂停计数 +1", BitmapPrefetcher.BackgroundPauseCount == pausesBefore + 1,
                $"before={pausesBefore} now={BitmapPrefetcher.BackgroundPauseCount}");
            Check("background 暂停不谎报内存压力",
                MemoryGovernor.CurrentLevel == levelBefore && MemoryGovernor.InRecoveryHold == holdBefore,
                $"level={levelBefore}→{MemoryGovernor.CurrentLevel} hold={holdBefore}→{MemoryGovernor.InRecoveryHold}");
            return Task.CompletedTask;
        });

        await RunStepAsync("background 暂停期间不排新工作", async () =>
        {
            // 真实入口触发的邻居轮次必须被活跃闸拦下并记账，而不是靠内存压力挡
            main.FileVM.ThumbnailList.TriggerPrefetchAroundCurrent();
            Check("background 暂停时邻居轮次被拦下",
                BitmapPrefetcher.SuspendedScheduleCount == suspendedBefore + 1,
                $"before={suspendedBefore} now={BitmapPrefetcher.SuspendedScheduleCount}");
            await WaitAsync(() => DecodeScheduler.GetQueuedCount(DecodePriority.P2_Prefetch) == 0,
                "暂停期间 P2 预取队列排空", 5000);
            Check("background 暂停期间 P2 预取队列为空",
                DecodeScheduler.GetQueuedCount(DecodePriority.P2_Prefetch) == 0,
                $"queued={DecodeScheduler.GetQueuedCount(DecodePriority.P2_Prefetch)}");
        });

        await RunStepAsync("background 重复暂停幂等", () =>
        {
            bool changed = AndroidNativeShell.SetApplicationActive(false);
            Check("background 重复暂停返回 false", !changed, $"changed={changed}");
            Check("background 重复暂停不重复计数", BitmapPrefetcher.BackgroundPauseCount == pausesBefore + 1,
                $"before={pausesBefore} now={BitmapPrefetcher.BackgroundPauseCount}");
            Check("background 重复暂停不改内存压力级别", MemoryGovernor.CurrentLevel == levelBefore,
                $"level={levelBefore}→{MemoryGovernor.CurrentLevel}");
            return Task.CompletedTask;
        });

        await RunStepAsync("background 回到前台", () =>
        {
            bool changed = AndroidNativeShell.SetApplicationActive(true);
            Check("background 回前台报告状态变化", changed, $"changed={changed}");
            Check("background 回前台后状态活跃", BitmapPrefetcher.IsApplicationActive,
                $"active={BitmapPrefetcher.IsApplicationActive}");
            Check("background 回前台换新代际令牌",
                !BitmapPrefetcher.ApplicationLifetimeToken.IsCancellationRequested && baselineToken.IsCancellationRequested,
                $"newCancelled={BitmapPrefetcher.ApplicationLifetimeToken.IsCancellationRequested}"
                + $" oldCancelled={baselineToken.IsCancellationRequested}");
            Check("background 回前台恢复计数 +1", BitmapPrefetcher.ForegroundResumeCount == resumesBefore + 1,
                $"before={resumesBefore} now={BitmapPrefetcher.ForegroundResumeCount}");
            Check("background 回前台按存活实例重新计划",
                BitmapPrefetcher.ForegroundReplanCount >= replanBefore + 1,
                $"before={replanBefore} now={BitmapPrefetcher.ForegroundReplanCount}");
            Check("background 回前台不改内存压力级别", MemoryGovernor.CurrentLevel == levelBefore,
                $"level={levelBefore}→{MemoryGovernor.CurrentLevel}");
            return Task.CompletedTask;
        });

        await RunStepAsync("background 恢复后预取照常", async () =>
        {
            int suspendedNow = BitmapPrefetcher.SuspendedScheduleCount;
            main.FileVM.ThumbnailList.TriggerPrefetchAroundCurrent();
            Check("background 恢复后不再被拦下", BitmapPrefetcher.SuspendedScheduleCount == suspendedNow,
                $"before={suspendedNow} now={BitmapPrefetcher.SuspendedScheduleCount}");
            await WaitAsync(() => NeighborProxyCached(main), "恢复后邻图代理进缓存", 20000);
            Check("background 恢复后邻图代理进缓存", NeighborProxyCached(main), DescribeNeighborCache(main));
        });
    }

    /// <summary>当前图任一侧邻居的代理档是否已在缓存里（只查缓存，不借租约、不触发解码）。</summary>
    /// <param name="main">共享主 VM。</param>
    /// <returns>至少一侧邻居命中代理档返回 true。</returns>
    private static bool NeighborProxyCached(MainViewModel main)
    {
        var files = main.FileVM.ThumbnailList.FilteredFiles;
        var current = main.CurrentFile;
        if (current == null || files.Count < 2) return false;
        int index = files.IndexOf(current);
        if (index < 0) return false;
        for (int offset = -1; offset <= 1; offset += 2)
        {
            int neighbour = index + offset;
            if (neighbour < 0 || neighbour >= files.Count) continue;
            if (BitmapLoader.TryGetCached(files[neighbour].File.StorageId.Value, ImageSizeClass.Proxy) != null) return true;
        }

        return false;
    }

    /// <summary>邻居缓存状态的报告文本（当前图下标与各档命中情况）。</summary>
    /// <param name="main">共享主 VM。</param>
    /// <returns>可读摘要。</returns>
    private static string DescribeNeighborCache(MainViewModel main)
    {
        var files = main.FileVM.ThumbnailList.FilteredFiles;
        var current = main.CurrentFile;
        int index = current == null ? -1 : files.IndexOf(current);
        var parts = new List<string>();
        for (int i = 0; i < files.Count && i < 6; i++)
        {
            bool proxy = BitmapLoader.TryGetCached(files[i].File.StorageId.Value, ImageSizeClass.Proxy) != null;
            parts.Add($"{files[i].Name}{(ReferenceEquals(files[i], current) ? "*" : string.Empty)}={(proxy ? "proxy" : "-")}");
        }

        return $"index={index} count={files.Count} {string.Join(" ", parts)}";
    }
}
