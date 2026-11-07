using System.Reactive.Concurrency;
using PhotoViewer.Core.Image;
using PhotoViewer.Core.Memory;
using PhotoViewer.Core.Platform;
using PhotoViewer.ViewModels.Main;

namespace CoreContractCheck;

/// <summary>
/// 预取后台活跃闸的受控契约：默认活跃（未上报生命周期的宿主行为不变）、暂停幂等、
/// 暂停只走预取自己的活跃闸（不改内存压力级别、不进恢复观察期）、后台不排新工作、
/// 回前台换新代际令牌并按存活实例重新计划、旧代际令牌永久已取消。
/// 全程只操作静态状态与一个空文件夹的主 VM（不读照片、不解码），结束时恢复活跃状态。
/// </summary>
internal static class PrefetchLifecycleChecks
{
    public static void Run(Checks c)
    {
        c.Section("预取后台活跃闸");
        try
        {
            RunChecks(c);
        }
        finally
        {
            // 不把暂停态留给后续用例
            BitmapPrefetcher.SetApplicationActive(true);
        }
    }

    private static void RunChecks(Checks c)
    {
        c.True("默认活跃，未上报生命周期的宿主行为不变", BitmapPrefetcher.IsApplicationActive);
        c.True("默认代际令牌未取消", !BitmapPrefetcher.ApplicationLifetimeToken.IsCancellationRequested);

        var baselineToken = BitmapPrefetcher.ApplicationLifetimeToken;
        int pauses = BitmapPrefetcher.BackgroundPauseCount;
        int resumes = BitmapPrefetcher.ForegroundResumeCount;
        var levelBefore = MemoryGovernor.CurrentLevel;
        bool holdBefore = MemoryGovernor.InRecoveryHold;

        // 活跃 → 后台
        c.True("首次进入后台报告状态变化", BitmapPrefetcher.SetApplicationActive(false));
        c.True("后台状态生效", !BitmapPrefetcher.IsApplicationActive);
        c.Equal("暂停计数 +1", pauses + 1, BitmapPrefetcher.BackgroundPauseCount);
        c.True("后台时代际令牌已取消", baselineToken.IsCancellationRequested);
        c.True("后台期间没有可用代际令牌", BitmapPrefetcher.ApplicationLifetimeToken.IsCancellationRequested);
        c.Equal("暂停不改内存压力级别（不谎报系统压力）", levelBefore, MemoryGovernor.CurrentLevel);
        c.Equal("暂停不进恢复观察期（回前台不会多等 30s）", holdBefore, MemoryGovernor.InRecoveryHold);

        // 幂等
        c.True("重复进入后台返回 false（幂等）", !BitmapPrefetcher.SetApplicationActive(false));
        c.Equal("重复上报不重复计数", pauses + 1, BitmapPrefetcher.BackgroundPauseCount);
        c.Equal("重复上报不再改压力级别", levelBefore, MemoryGovernor.CurrentLevel);

        UiDispatcher.Initialize(new InlineDispatcher());
        var reactiveBuilder = ReactiveUI.Builder.RxAppBuilder.CreateReactiveUIBuilder();
        reactiveBuilder.WithCoreServices();
        reactiveBuilder.WithMainThreadScheduler(ImmediateScheduler.Instance);
        reactiveBuilder.BuildApp();
        using (var main = new MainViewModel())
        {
            // 后台：真实实例的公开入口不排新工作（拦下并记账），而不是靠内存压力去挡
            int suspendedBefore = BitmapPrefetcher.SuspendedScheduleCount;
            main.FileVM.ThumbnailList.TriggerPrefetchAroundCurrent();
            c.Equal("后台时邻居预取轮次被拦下", suspendedBefore + 1, BitmapPrefetcher.SuspendedScheduleCount);

            // 回到前台：换新代际令牌并按存活实例重新计划
            int replanBefore = BitmapPrefetcher.ForegroundReplanCount;
            c.True("回到前台报告状态变化", BitmapPrefetcher.SetApplicationActive(true));
            c.True("回前台后状态活跃", BitmapPrefetcher.IsApplicationActive);
            c.Equal("恢复计数 +1", resumes + 1, BitmapPrefetcher.ForegroundResumeCount);
            c.True("回前台按存活实例重新计划（邻居 + 当前图 Full）",
                BitmapPrefetcher.ForegroundReplanCount >= replanBefore + 1,
                $"期望至少 +1，实际 {BitmapPrefetcher.ForegroundReplanCount - replanBefore}");
            c.True("旧代际令牌保持已取消，迟到的旧轮次不复活", baselineToken.IsCancellationRequested);
            c.True("新代际令牌可用", !BitmapPrefetcher.ApplicationLifetimeToken.IsCancellationRequested);

            // 恢复后同一入口不再被活跃闸拦下
            int suspendedAfter = BitmapPrefetcher.SuspendedScheduleCount;
            main.FileVM.ThumbnailList.TriggerPrefetchAroundCurrent();
            c.Equal("恢复后正常排轮次不再计入拦下", suspendedAfter, BitmapPrefetcher.SuspendedScheduleCount);
        }

        c.Equal("整轮暂停/恢复不改内存压力级别", levelBefore, MemoryGovernor.CurrentLevel);
        c.True("回前台后仍活跃", BitmapPrefetcher.IsApplicationActive);
    }

    private sealed class InlineDispatcher : IUiDispatcher
    {
        public IScheduler MainThreadScheduler { get; } = ImmediateScheduler.Instance;
        public bool CheckAccess() => true;
        public void Post(Action action, UiPriority priority = UiPriority.Normal) => action();
        public Task InvokeAsync(Action action, UiPriority priority = UiPriority.Normal)
        {
            action();
            return Task.CompletedTask;
        }
        public Task<T> InvokeAsync<T>(Func<T> func, UiPriority priority = UiPriority.Normal) => Task.FromResult(func());
    }
}
