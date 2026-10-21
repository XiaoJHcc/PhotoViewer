using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using PhotoViewer.Core.Image;
using PhotoViewer.Core.Memory;
using PhotoViewer.Core.Settings;
using PhotoViewer.Core.Platform;
using PhotoViewer.Core.Storage;
using PhotoViewer.ViewModels.Main;

namespace PhotoViewer;

/// <summary>
/// 真机压测翻页驱动器:仅当头工程解析到 --pv-stress 启动参数时激活,常规运行完全惰性。
/// 走真实 CurrentFile 切换路径(与按键/手势翻页同源,覆盖解码/缓存/Governor 全链路);
/// 上屏判定经 <see cref="IDisplayLoadSignal"/> 取当前壳的展示会话(各原生壳均为 PhotoLoadSession),
/// 覆盖真实 P0 解码链路而非空跑。
/// 逐张记录 P0 上屏延迟、压力级别与在途内存,JSONL 手写落盘(规避 AOT 裁剪下反射序列化),
/// 日志位于 settings 同目录 stress-flip.jsonl,供真机 42MP 连翻验收回放。
/// 配套参数:--pv-stress=&lt;文件夹路径&gt;(相对路径由头工程解析)、--pv-stress-count=N。
/// </summary>
public static class StressFlipDriver
{
    /// <summary>待压测文件夹绝对路径(头工程启动参数注入;null = 不激活)。</summary>
    public static string? PendingFolderPath;

    /// <summary>翻页上限;0 = 整个文件夹翻一遍。</summary>
    public static int MaxFlips;

    /// <summary>在该 seq 翻页完成后注入一次 OS 内存告警(0 起;-1 = 不注入),验证修剪级联与恢复观察期。</summary>
    public static int WarnAtSeq = -1;

    /// <summary>单张上屏等待超时(ms),超时记 -1 并继续(卡死是数据而非必然崩溃)。</summary>
    private const int PerFlipTimeoutMs = 15000;

    /// <summary>
    /// 解析压测启动参数(头工程在平台注入点调用)。
    /// </summary>
    /// <param name="args">进程启动参数。</param>
    /// <param name="resolveRelativePath">相对路径 → 绝对路径的平台解析(如拼接沙盒 Documents)。</param>
    /// <returns>是否激活压测模式。</returns>
    public static bool TryParseArgs(IEnumerable<string> args, Func<string, string> resolveRelativePath)
    {
        foreach (var arg in args)
        {
            if (arg.StartsWith("--pv-stress=", StringComparison.Ordinal))
            {
                var raw = arg["--pv-stress=".Length..];
                PendingFolderPath = Path.IsPathRooted(raw) ? raw : resolveRelativePath(raw);
            }
            else if (arg.StartsWith("--pv-stress-count=", StringComparison.Ordinal)
                     && int.TryParse(arg["--pv-stress-count=".Length..], out var count))
            {
                MaxFlips = count;
            }
            else if (arg.StartsWith("--pv-stress-warn=", StringComparison.Ordinal)
                     && int.TryParse(arg["--pv-stress-warn=".Length..], out var warnAt))
            {
                WarnAtSeq = warnAt;
            }
        }

        return PendingFolderPath != null;
    }

    /// <summary>
    /// 框架初始化完成后调用:存在注入路径则异步执行压测,否则立即返回。
    /// </summary>
    /// <param name="vm">主窗口 VM。</param>
    /// <param name="signal">当前壳的主图上屏信号源（PhotoLoadSession）。</param>
    public static void MaybeStart(MainViewModel vm, IDisplayLoadSignal signal)
    {
        var path = PendingFolderPath;
        if (string.IsNullOrWhiteSpace(path)) return;
        _ = RunAsync(vm, signal, path);
    }

    /// <summary>压测主流程:开文件夹 → 清空当前选中 → 逐张翻页计时 → 写汇总行。</summary>
    private static async Task RunAsync(MainViewModel vm, IDisplayLoadSignal signal, string folderPath)
    {
        var logPath = Path.Combine(
            Path.GetDirectoryName(SettingsPathHelper.GetDefaultPath("PhotoViewer"))!,
            "stress-flip.jsonl");

        void Log(string line)
        {
            try { File.AppendAllText(logPath, line + "\n"); }
            catch (Exception ex) { Console.WriteLine($"[StressFlip] log failed: {ex.Message}"); }
        }

        var runSw = Stopwatch.StartNew();
        File.WriteAllText(logPath, string.Empty);
        Log($"{{\"type\":\"begin\",\"folder\":\"{Esc(folderPath)}\",\"maxFlips\":{MaxFlips},\"warnAt\":{WarnAtSeq}}}");

        try
        {
            if (!Directory.Exists(folderPath))
            {
                Log($"{{\"type\":\"error\",\"reason\":\"folder not found: {Esc(folderPath)}\"}}");
                return;
            }

            var folder = new LocalFolderRef(folderPath);
            await UiDispatcher.InvokeAsync(() => vm.FolderVM.OpenFolderAsync(folder, scrollToCurrent: false)).Unwrap();

            var files = await WaitForFilesAsync(vm);
            if (files.Count == 0)
            {
                Log("{\"type\":\"error\",\"reason\":\"no image files enumerated\"}");
                return;
            }

            // 开文件夹自动选中了首图,先置空保证第 0 翻是真实切换(其解码可能已在途,延迟略偏乐观)
            await UiDispatcher.InvokeAsync(() => { vm.CurrentFile = null; });

            var flips = MaxFlips > 0 ? Math.Min(MaxFlips, files.Count) : files.Count;
            Log($"{{\"type\":\"plan\",\"files\":{files.Count},\"flips\":{flips}}}");

            var latencies = new List<double>(flips);
            var timeouts = 0;

            for (var i = 0; i < flips; i++)
            {
                var target = files[i];
                var loaded = new TaskCompletionSource();
                using var subscription = signal.Subscribe(file =>
                {
                    if (ReferenceEquals(file, target.File)) loaded.TrySetResult();
                });
                var flipSw = Stopwatch.StartNew();
                await UiDispatcher.InvokeAsync(() => { vm.CurrentFile = target; });
                var winner = await Task.WhenAny(loaded.Task, Task.Delay(PerFlipTimeoutMs));
                flipSw.Stop();

                var timedOut = winner != loaded.Task;
                double ms;
                if (timedOut)
                {
                    timeouts++;
                    ms = -1;
                }
                else
                {
                    ms = flipSw.Elapsed.TotalMilliseconds;
                    latencies.Add(ms);
                }

                Log("{\"seq\":" + i
                    + ",\"file\":\"" + Esc(target.Name) + "\""
                    + ",\"ms\":" + F(ms)
                    + ",\"timeout\":" + B(timedOut)
                    + ",\"proxy\":" + B(signal.CurrentIsProxy)
                    + ",\"enc\":\"" + (signal.CurrentEncoding?.ToString() ?? "none") + "\""
                    + ",\"level\":\"" + MemoryGovernor.CurrentLevel + "\""
                    + ",\"hold\":" + B(MemoryGovernor.InRecoveryHold)
                    + ",\"reservedMB\":" + MemoryGovernor.ReservedBytes / 1048576
                    + ",\"gcMB\":" + GC.GetTotalMemory(false) / 1048576
                    + ",\"tSec\":" + F(runSw.Elapsed.TotalSeconds) + "}");

                // 注入 OS 内存告警:验证修剪级联(memory-events.jsonl)与 30s 恢复观察期(后续 flip 的 hold 字段)
                if (i == WarnAtSeq)
                {
                    MemoryGovernor.ReportOsEvent(PressureLevel.Warning);
                    Log("{\"type\":\"warn-injected\",\"seq\":" + i + ",\"tSec\":" + F(runSw.Elapsed.TotalSeconds) + "}");
                }
            }

            latencies.Sort();
            var miss = PerfTrace.RecentMissStats(Math.Min(latencies.Count, 200));
            Log("{\"type\":\"done\""
                + ",\"flips\":" + flips
                + ",\"timeouts\":" + timeouts
                + ",\"p50Ms\":" + F(Pct(latencies, 0.50))
                + ",\"p95Ms\":" + F(Pct(latencies, 0.95))
                + ",\"maxMs\":" + F(latencies.Count > 0 ? latencies[^1] : -1)
                + ",\"decodeMissP50Ms\":" + F(miss.P50)
                + ",\"decodeMissMeanMs\":" + F(miss.Mean)
                + ",\"elapsedSec\":" + F(runSw.Elapsed.TotalSeconds) + "}");
            Console.WriteLine($"[StressFlip] done: {flips} flips, {timeouts} timeouts");
        }
        catch (Exception ex)
        {
            Log($"{{\"type\":\"error\",\"reason\":\"{Esc(ex.ToString())}\"}}");
        }
    }

    /// <summary>等待文件夹枚举出图片(最长 15s),返回当前文件列表快照。</summary>
    private static async Task<IReadOnlyList<PhotoViewer.Core.Image.ImageFile>> WaitForFilesAsync(MainViewModel vm)
    {
        for (var attempt = 0; attempt < 150; attempt++)
        {
            var count = await UiDispatcher.InvokeAsync(() => vm.FolderVM.AllFiles.Count);
            if (count > 0) return vm.FolderVM.AllFiles.ToList();
            await Task.Delay(100);
        }

        return Array.Empty<PhotoViewer.Core.Image.ImageFile>();
    }

    /// <summary>最近邻秩 percentile;空序列返回 -1。</summary>
    private static double Pct(IReadOnlyList<double> sorted, double q)
        => sorted.Count == 0 ? -1 : sorted[Math.Min(sorted.Count - 1, (int)(q * sorted.Count))];

    /// <summary>JSONL 字段转义(仅引号与反斜杠)。</summary>
    private static string Esc(string s) => s.Replace("\\", "\\\\").Replace("\"", "\\\"");

    /// <summary>不变文化数值格式化(保留 1 位小数)。</summary>
    private static string F(double v) => v.ToString("F1", CultureInfo.InvariantCulture);

    /// <summary>布尔字面量。</summary>
    private static string B(bool v) => v ? "true" : "false";
}
