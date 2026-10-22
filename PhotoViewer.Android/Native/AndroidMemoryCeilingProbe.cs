using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using Android.App;
using Android.Content;
using Android.OS;
using Android.Views;
using PhotoViewer.Android.Core;
using PhotoViewer.Core.Memory;

namespace PhotoViewer.Android.Native;

/// <summary>
/// 真机内存上限探针（验收用，不参与产品界面）：前台保持一个空视图，按 128 MB 步长分配并逐页写实，
/// 每步把 <c>RssAnon</c>/<c>VmSwap</c>/系统 <c>availMem</c> 与预算源读数落盘，直到进程被杀或到 14 GB 上限。
/// 另提供 <c>pv-memory-probe-report</c>：只读取系统记录的本包历史退出原因并落盘，用来确认是 lmkd 还是 OEM 回收。
/// </summary>
/// <remarks>
/// 分配用 <see cref="GC.AllocateUninitializedArray{T}(int, bool)"/> 的非固定大对象，写页保证匿名页真的驻留；
/// 读数与产品口径一致（预算源同一份 <see cref="AndroidMemoryBudgetProvider"/>），事后可直接与压测 footprint 对照。
/// </remarks>
internal static class AndroidMemoryCeilingProbe
{
    private const long MiB = 1024L * 1024L;

    /// <summary>每次分配的步长：128 MB。</summary>
    private const long StepBytes = 128L * MiB;

    /// <summary>停止分配的总量上限：14 GB（低于 15 GB 设备可用的物理内存，便于看到被杀前的读数）。</summary>
    private const long CapBytes = 14L * 1024L * MiB;

    /// <summary>每步之间留出的观察窗（毫秒）：让 lmkd/Governor 的周期探测跑一轮。</summary>
    private const int StepPauseMs = 300;

    /// <summary>启动后等界面真正可见再开始分配（毫秒）。</summary>
    private const int StartupDelayMs = 2000;

    /// <summary>报告文件名：逐步读数。</summary>
    private const string CeilingFileName = "memory-ceiling.txt";

    /// <summary>报告文件名：系统记录的历史退出原因。</summary>
    private const string ExitReasonsFileName = "exit-reasons.txt";

    /// <summary>写页粒度：与内核页同尺寸的 4 KB，保证每个物理页都接触到。</summary>
    private const int PageBytes = 4096;

    /// <summary>
    /// 启动上限探针：命中 <c>pv-memory-probe</c> 时接管界面（返回占位视图），分配循环在后台线程跑。
    /// </summary>
    /// <param name="activity">宿主 Activity（提供文件目录、窗口与 ActivityManager）。</param>
    /// <param name="intent">启动 Intent。</param>
    /// <returns>非 null 表示已接管，调用方把它交给 <c>SetContentView</c> 后不要再建主界面。</returns>
    internal static View? MaybeStart(Activity activity, Intent? intent)
    {
        if (intent?.HasExtra("pv-memory-probe") != true) return null;

        var reportDirectory = Path.Combine(activity.FilesDir!.AbsolutePath, "PhotoViewer");
        Directory.CreateDirectory(reportDirectory);
        var reportPath = Path.Combine(reportDirectory, CeilingFileName);
        // 屏幕常亮：分配循环要跑十几秒到几十秒，锁屏会让系统先降频/冻结再判后台。
        activity.Window?.AddFlags(WindowManagerFlags.KeepScreenOn);

        Console.WriteLine($"[MemoryCeiling] start -> {reportPath}");
        var thread = new Thread(() => Run(activity, reportPath))
        {
            IsBackground = false,
            Name = "pv-memory-ceiling",
        };
        thread.Start();
        return new View(activity);
    }

    /// <summary>
    /// 读取系统记录的本包历史退出原因（最近 5 条）并落盘；命中 <c>pv-memory-probe-report</c> 时使用。
    /// </summary>
    /// <param name="activity">宿主 Activity。</param>
    /// <param name="intent">启动 Intent。</param>
    /// <returns>写了报告返回 true（调用方随后结束进程）。</returns>
    internal static bool MaybeReportExitReasons(Activity activity, Intent? intent)
    {
        if (intent?.HasExtra("pv-memory-probe-report") != true) return false;

        var reportDirectory = Path.Combine(activity.FilesDir!.AbsolutePath, "PhotoViewer");
        Directory.CreateDirectory(reportDirectory);
        var reportPath = Path.Combine(reportDirectory, ExitReasonsFileName);
        var builder = new StringBuilder();
        builder.Append("# Android 进程退出原因（ActivityManager.GetHistoricalProcessExitReasons）\n");
        builder.Append("# now=").Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)).Append('\n');
        try
        {
            var manager = (ActivityManager?)activity.GetSystemService(Context.ActivityService);
            var records = manager?.GetHistoricalProcessExitReasons(activity.PackageName, 0, 5);
            if (records == null || records.Count == 0)
            {
                builder.Append("# 无历史记录\n");
            }
            else
            {
                int index = 0;
                foreach (var record in records)
                {
                    index++;
                    builder.Append("record=").Append(index)
                        .Append(" reason=").Append(record.Reason)
                        .Append(" reasonName=").Append(ReasonName(record.Reason))
                        .Append(" status=").Append(record.Status)
                        .Append(" importance=").Append(record.Importance)
                        .Append(" pssMB=").Append(record.Pss >> 10)
                        .Append(" rssMB=").Append(record.Rss >> 10)
                        .Append(" pid=").Append(record.Pid)
                        .Append(" process=").Append(record.ProcessName)
                        .Append(" timestamp=").Append(
                            DateTimeOffset.FromUnixTimeMilliseconds(record.Timestamp)
                                .ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture))
                        .Append(" description=").Append(record.Description?.Replace('\n', ' '))
                        .Append('\n');
                }
            }
        }
        catch (Exception exception)
        {
            builder.Append("# 读取失败：").Append(exception).Append('\n');
            Console.Error.WriteLine($"[MemoryCeiling] 退出原因读取失败：{exception.Message}");
        }

        WriteReport(reportPath, builder.ToString());
        Console.WriteLine($"[MemoryCeiling] exit reasons -> {reportPath}");
        return true;
    }

    /// <summary>分配循环：每步分配 → 逐页写实 → 采样 → 落盘，直到异常、进程被杀或到上限。</summary>
    /// <param name="activity">宿主 Activity。</param>
    /// <param name="reportPath">逐步读数文件。</param>
    private static void Run(Activity activity, string reportPath)
    {
        var provider = new AndroidMemoryBudgetProvider();
        var blocks = new List<byte[]>();
        long total = 0;
        int step = 0;
        var startedAt = DateTime.Now;

        try
        {
            // 等界面真正可见：被系统判成后台时，前台可见档的预算与 cgroup 都不成立。
            Thread.Sleep(StartupDelayMs);
            using var stream = new FileStream(reportPath, FileMode.Create, FileAccess.Write, FileShare.ReadWrite);
            var header = new StringBuilder();
            header.Append("# Android 内存上限探针\n");
            header.Append("# start=").Append(startedAt.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture))
                .Append(" package=").Append(activity.PackageName)
                .Append(" stepMB=").Append(StepBytes / MiB)
                .Append(" capMB=").Append(CapBytes / MiB)
                .Append('\n');
            header.Append("# ").Append(StartupLine(activity, provider)).Append('\n');
            AppendAndFlush(stream, header.ToString());
            Console.WriteLine("[MemoryCeiling] " + StartupLine(activity, provider));

            while (total + StepBytes <= CapBytes)
            {
                step++;
                byte[] block;
                try
                {
                    block = GC.AllocateUninitializedArray<byte>((int)StepBytes, pinned: false);
                }
                catch (Exception exception)
                {
                    AppendAndFlush(stream, $"step={step} totalAllocatedMB={total >> 20} allocationFailed="
                        + $"{exception.GetType().Name}:{exception.Message}\n");
                    Console.Error.WriteLine($"[MemoryCeiling] 分配失败（{total >> 20}MB）：{exception.Message}");
                    break;
                }

                TouchPages(block);
                blocks.Add(block);
                total += StepBytes;
                var line = SampleLine(activity, provider, step, total, startedAt, blocks.Count);
                AppendAndFlush(stream, line);
                Console.WriteLine("[MemoryCeiling] " + line.TrimEnd('\n'));
                Thread.Sleep(StepPauseMs);
            }

            AppendAndFlush(stream, $"done totalAllocatedMB={total >> 20} steps={step} blocks={blocks.Count}\n");
        }
        catch (Exception exception)
        {
            Console.WriteLine($"[MemoryCeiling] 循环异常：{exception}");
        }

        // 到这里说明没有被杀（到达上限或分配被拒）：把占用留住，供外部 dumpsys meminfo 对照。
        Console.WriteLine($"[MemoryCeiling] 完成，保持占用 {total >> 20}MB；等待退出");
    }

    /// <summary>启动读数：预算档位、总内存与系统水位。</summary>
    /// <param name="activity">宿主 Activity。</param>
    /// <param name="provider">预算源实例（与产品同一实现）。</param>
    /// <returns>一行键值对。</returns>
    private static string StartupLine(Activity activity, AndroidMemoryBudgetProvider provider)
    {
        var snapshot = AndroidProcessMemory.Read();
        var info = ReadMemoryInfo(activity);
        return $"budgetMB={provider.GetBudgetBytes() >> 20}"
            + $" footprintMB={provider.GetFootprintBytes() >> 20}"
            + $" availableMB={SafeAvailable(provider) >> 20}"
            + $" level={MemoryGovernor.CurrentLevel}"
            + $" rssAnonMB={snapshot.RssAnonBytes >> 20}"
            + $" swapMB={snapshot.VmSwapBytes >> 20}"
            + $" amTotalMB={(info?.TotalMem ?? 0) >> 20}"
            + $" amAvailMB={(info?.AvailMem ?? 0) >> 20}"
            + $" amThresholdMB={(info?.Threshold ?? 0) >> 20}"
            + $" amLowMemory={info?.LowMemory}";
    }

    /// <summary>单步读数：分配总量、匿名驻留、换出、系统水位与预算源余量。</summary>
    /// <param name="activity">宿主 Activity。</param>
    /// <param name="provider">预算源实例。</param>
    /// <param name="step">步序号（1 起）。</param>
    /// <param name="total">累计分配字节。</param>
    /// <param name="startedAt">循环开始时间（算耗时）。</param>
    /// <param name="blocks">当前持有的分配块数。</param>
    /// <returns>一行键值对。</returns>
    private static string SampleLine(
        Activity activity,
        AndroidMemoryBudgetProvider provider,
        int step,
        long total,
        DateTime startedAt,
        int blocks)
    {
        var snapshot = AndroidProcessMemory.Read();
        var info = ReadMemoryInfo(activity);
        return $"step={step}"
            + $" totalAllocatedMB={total >> 20}"
            + $" blocks={blocks}"
            + $" rssAnonMB={snapshot.RssAnonBytes >> 20}"
            + $" swapMB={snapshot.VmSwapBytes >> 20}"
            + $" peakRssMB={snapshot.VmHwmBytes >> 20}"
            + $" amAvailMB={(info?.AvailMem ?? 0) >> 20}"
            + $" amThresholdMB={(info?.Threshold ?? 0) >> 20}"
            + $" amLowMemory={info?.LowMemory}"
            + $" availableMB={SafeAvailable(provider) >> 20}"
            + $" footprintMB={provider.GetFootprintBytes() >> 20}"
            + $" level={MemoryGovernor.CurrentLevel}"
            + $" elapsedMs={(long)(DateTime.Now - startedAt).TotalMilliseconds}"
            + '\n';
    }

    /// <summary>逐页写入一个字节，把匿名页真的落到物理内存（只分配不写时页仍是零页映射）。</summary>
    /// <param name="block">刚分配的块。</param>
    private static void TouchPages(byte[] block)
    {
        for (int offset = 0; offset < block.Length; offset += PageBytes)
            block[offset] = (byte)(offset >> 12);
        if (block.Length > 0) block[^1] = 0x5A;
    }

    /// <summary>预算源读数；未初始化或读数异常时按 0。</summary>
    /// <param name="provider">预算源实例。</param>
    /// <returns>可用字节。</returns>
    private static long SafeAvailable(AndroidMemoryBudgetProvider provider)
    {
        try { return provider.GetAvailableBytes(); }
        catch { return 0; }
    }

    /// <summary>系统内存水位读数。</summary>
    /// <param name="activity">宿主 Activity。</param>
    /// <returns>读数；不可用时为 null。</returns>
    private static ActivityManager.MemoryInfo? ReadMemoryInfo(Activity activity)
    {
        try
        {
            var manager = (ActivityManager?)activity.GetSystemService(Context.ActivityService);
            if (manager == null) return null;
            var info = new ActivityManager.MemoryInfo();
            manager.GetMemoryInfo(info);
            return info;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"[MemoryCeiling] GetMemoryInfo 失败：{exception.Message}");
            return null;
        }
    }

    /// <summary>系统记录的退出原因码 → 可读名字（取值见 <c>ApplicationExitInfo.REASON_*</c>）。</summary>
    private static readonly string[] ReasonNames =
    [
        "UNKNOWN", "EXIT_SELF", "SIGNALED", "LOW_MEMORY", "CRASH", "CRASH_NATIVE",
        "ANR", "INITIALIZATION_FAILURE", "PERMISSION_CHANGE", "EXCESSIVE_RESOURCE_USAGE",
        "USER_REQUESTED", "USER_STOPPED", "DEPENDENCY_DIED", "OTHER", "FREEZER",
        "PACKAGE_STATE_CHANGE", "PACKAGE_UPDATED",
    ];

    /// <summary>退出原因枚举名；系统新增码值回退为数字。</summary>
    /// <param name="reason">Android 的 reason 码。</param>
    /// <returns>可读名字。</returns>
    private static string ReasonName(int reason)
        => reason >= 0 && reason < ReasonNames.Length
            ? ReasonNames[reason]
            : reason.ToString(CultureInfo.InvariantCulture);

    /// <summary>追加一行并 fsync：进程被杀时报告里必须留下最后一次读数。</summary>
    /// <param name="stream">报告文件流。</param>
    /// <param name="text">要写入的文本。</param>
    private static void AppendAndFlush(FileStream stream, string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        stream.Write(bytes, 0, bytes.Length);
        stream.Flush(true);
    }

    /// <summary>整份报告一次写入并 fsync。</summary>
    /// <param name="path">报告路径。</param>
    /// <param name="text">报告内容。</param>
    private static void WriteReport(string path, string text)
    {
        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.ReadWrite);
        var bytes = Encoding.UTF8.GetBytes(text);
        stream.Write(bytes, 0, bytes.Length);
        stream.Flush(true);
    }
}
