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
/// 走真实 CurrentFile 切换路径(与按键/手势翻页同源,覆盖解码/缓存/Governor 全链路),
/// **翻页顺序取展示列表顺序**(与上一张/下一张命令看到的是同一份筛选/排序列表,
/// 预取按该列表的翻页方向选邻居;用文件夹枚举顺序翻会让预取的邻居与下一张无关);
/// 完成判定经 <see cref="IDisplayLoadSignal"/> 取平台信号，CompletionStage 标明像素就绪或提交返回，
/// 覆盖真实 P0 解码链路而非空跑。
/// 逐张记录 P0 上屏延迟、压力级别、在途内存、进程 footprint、展示缓存与缓冲池空闲字节,
/// JSONL 手写落盘(规避 AOT 裁剪下反射序列化),
/// 日志默认位于 settings 同目录 stress-flip.jsonl(头工程可改到隔离目录),供真机 42MP 连翻验收回放。
/// 内存读数在计时结束后采样,不进上屏延迟;done 行给这些逐翻采样的最大值(字段名带 SampledPeak,不声称真实瞬时峰值)。
/// 配套参数:--pv-stress=&lt;文件夹路径&gt;(相对路径由头工程解析)、--pv-stress-count=N、
/// --pv-stress-warn=N、--pv-stress-mode=ab(最近两张对比切换,默认 sequential)。
/// </summary>
public static class StressFlipDriver
{
    /// <summary>待压测文件夹绝对路径(头工程启动参数注入;null = 不激活)。</summary>
    public static string? PendingFolderPath;

    /// <summary>翻页次数;0 = 整个文件夹翻一遍，超过列表长度时循环。</summary>
    public static int MaxFlips;

    /// <summary>在该 seq 翻页完成后注入一次 OS 内存告警(0 起;-1 = 不注入),验证修剪级联与恢复观察期。</summary>
    public static int WarnAtSeq = -1;

    /// <summary>
    /// 对比切换模式(<c>--pv-stress-mode=ab</c>):在第 k 与第 k+1 张展示照片之间来回翻,
    /// 测"最近两张对比"的切换延迟;默认顺序模式不变,Mac/iOS 不传该参数时行为完全一致。
    /// </summary>
    public static bool AbMode;

    /// <summary>日志目录覆盖(头工程给隔离目录);null = settings 同目录。</summary>
    public static string? LogDirectory;

    /// <summary>
    /// 汇总行附加指标(可选,头工程注入,例如纹理上传计时);返回纯文本,写进 done 行的 extra 字段。
    /// 未注入时不写该字段,JSONL 保持向后兼容。
    /// </summary>
    public static Func<string>? ExtraSummary;

    /// <summary>
    /// 平台纹理字节采样(可选,头工程仅压测激活时注入);未注入时逐翻行与 done 行都不写纹理字段。
    /// </summary>
    public static Func<long>? TextureBytesProvider;

    /// <summary>
    /// 一轮压测结束(写完 done 行)后的回调(可选,头工程注入)。
    /// 自动化压测靠它退出进程,否则窗口留在前台、隔离数据库被占用,下一轮无法重置。
    /// </summary>
    public static Action? Completed;

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
            else if (arg.StartsWith("--pv-stress-mode=", StringComparison.Ordinal))
            {
                // 只认明确写出的顺序/对比模式;未知取值保持默认,避免拼错参数变成静默的另一种压测
                AbMode = string.Equals(arg["--pv-stress-mode=".Length..], "ab", StringComparison.OrdinalIgnoreCase);
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
        var logDirectory = LogDirectory
            ?? Path.GetDirectoryName(SettingsPathHelper.GetDefaultPath("PhotoViewer"))!;
        Directory.CreateDirectory(logDirectory);
        var logPath = Path.Combine(logDirectory, "stress-flip.jsonl");

        void Log(string line)
        {
            try { File.AppendAllText(logPath, line + "\n"); }
            catch (Exception ex) { Console.WriteLine($"[StressFlip] log failed: {ex.Message}"); }
        }

        var runSw = Stopwatch.StartNew();
        File.WriteAllText(logPath, string.Empty);
        Log($"{{\"type\":\"begin\",\"folder\":\"{Esc(folderPath)}\",\"maxFlips\":{MaxFlips},\"warnAt\":{WarnAtSeq}"
            + ",\"mode\":\"" + (AbMode ? "ab" : "sequential") + "\",\"completion\":\"" + Esc(signal.CompletionStage) + "\"}");

        try
        {
            if (!Directory.Exists(folderPath))
            {
                Log($"{{\"type\":\"error\",\"reason\":\"folder not found: {Esc(folderPath)}\"}}");
                return;
            }

            Log("{\"type\":\"folder\",\"entries\":" + Directory.EnumerateFileSystemEntries(folderPath).Count()
                + ",\"formats\":\"" + Esc(string.Join(",", vm.Settings.SelectedFormats)) + "\"}");
            void OnOpenFailed(string message) => Log("{\"type\":\"error\",\"reason\":\"" + Esc(message) + "\"}");
            vm.OpenFailed += OnOpenFailed;
            try
            {
                var folder = new LocalFolderRef(folderPath);
                await UiDispatcher.InvokeAsync(() => vm.FolderVM.OpenFolderAsync(folder, scrollToCurrent: false)).Unwrap();
            }
            finally
            {
                vm.OpenFailed -= OnOpenFailed;
            }

            // 连翻顺序取"展示列表顺序"（与键盘/轻扫的上一张/下一张同源），而不是文件夹枚举顺序：
            // 预取按展示列表的翻页方向选邻居，枚举顺序与之无关，用枚举顺序测不出预取效果。
            var files = await WaitForFilesAsync(vm);
            if (files.Count == 0)
            {
                Log("{\"type\":\"error\",\"reason\":\"no image files enumerated\"}");
                return;
            }

            // 开文件夹自动选中了首图,先置空保证第 0 翻是真实切换(其解码可能已在途,延迟略偏乐观)
            await UiDispatcher.InvokeAsync(() => { vm.CurrentFile = null; });

            // 对比模式需要两张相邻照片来回切;不足两张时退回顺序模式,避免原地重设同一文件空等超时
            var abMode = AbMode && files.Count >= 2;
            if (AbMode && !abMode)
            {
                Log("{\"type\":\"note\",\"reason\":\"ab mode requires >=2 displayed files, fallback to sequential\"}");
            }

            var flips = files.Count == 1 ? 1 : MaxFlips > 0 ? MaxFlips : files.Count;
            Log($"{{\"type\":\"plan\",\"files\":{files.Count},\"flips\":{flips}"
                + (abMode ? ",\"abPair\":[0,1]" : "") + "}");

            var latencies = new List<double>(flips);
            var timeouts = 0;
            var proxyShown = 0;
            var fullShown = 0;
            long? peakFootprint = null, peakCache = null, peakPoolIdle = null, peakTexture = null;

            // 逐翻采样取最大值:只代表采样点上的最高值,不是真实瞬时峰值
            static long? Max(long? current, long? value)
                => value is { } next && (current is null || next > current.Value) ? next : current;

            for (var i = 0; i < flips; i++)
            {
                // 顺序模式按展示列表逐张;对比模式在第 0/1 张之间来回(最近两张对比切换)
                var target = files[abMode ? i % 2 : i % files.Count];
                var loaded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var flipSw = new Stopwatch();
                double pixelsReadyMs = -1;
                double submittedMs = -1;
                using var readySubscription = signal.SubscribePixelsReady(file =>
                {
                    if (ReferenceEquals(file, target.File) && pixelsReadyMs < 0) pixelsReadyMs = flipSw.Elapsed.TotalMilliseconds;
                });
                using var subscription = signal.Subscribe(file =>
                {
                    if (!ReferenceEquals(file, target.File) || loaded.Task.IsCompleted) return;
                    submittedMs = flipSw.Elapsed.TotalMilliseconds;
                    loaded.TrySetResult();
                });
                flipSw.Start();
                await UiDispatcher.InvokeAsync(() => { vm.CurrentFile = target; });
                var winner = await Task.WhenAny(loaded.Task, Task.Delay(PerFlipTimeoutMs));
                flipSw.Stop();
                // 读数在计时结束后采样:采样开销不进入上屏延迟
                long footprintBytes = MemoryGovernor.FootprintBytes;
                long cacheBytes = BitmapLoader.CurrentCacheSize;
                long poolIdleBytes = ImageBufferPool.IdleBytes;
                long? textureBytes = TextureBytesProvider?.Invoke();
                peakFootprint = Max(peakFootprint, footprintBytes);
                peakCache = Max(peakCache, cacheBytes);
                peakPoolIdle = Max(peakPoolIdle, poolIdleBytes);
                peakTexture = Max(peakTexture, textureBytes);

                var timedOut = winner != loaded.Task;
                double ms;
                if (timedOut)
                {
                    timeouts++;
                    ms = -1;
                }
                else
                {
                    ms = submittedMs;
                    latencies.Add(ms);
                    if (signal.CurrentIsProxy) proxyShown++;
                    else fullShown++;
                }

                Log("{\"seq\":" + i
                    + ",\"file\":\"" + Esc(target.Name) + "\""
                    + ",\"ms\":" + F(ms)
                    + ",\"pixelsReadyMs\":" + F(pixelsReadyMs)
                    + ",\"submitAfterReadyMs\":" + F(ms >= 0 && pixelsReadyMs >= 0 ? ms - pixelsReadyMs : -1)
                    + ",\"timeout\":" + B(timedOut)
                    + ",\"proxy\":" + B(signal.CurrentIsProxy)
                    + ",\"enc\":\"" + (signal.CurrentEncoding?.ToString() ?? "none") + "\""
                    + ",\"level\":\"" + MemoryGovernor.CurrentLevel + "\""
                    + ",\"hold\":" + B(MemoryGovernor.InRecoveryHold)
                    + ",\"reservedMB\":" + MemoryGovernor.ReservedBytes / 1048576
                    + ",\"gcMB\":" + GC.GetTotalMemory(false) / 1048576
                    + ",\"tSec\":" + F(runSw.Elapsed.TotalSeconds)
                    + ",\"footprintMB\":" + FootprintMb(footprintBytes)
                    + ",\"cacheMB\":" + cacheBytes / 1048576
                    + ",\"poolIdleMB\":" + poolIdleBytes / 1048576
                    + (textureBytes is { } texture ? ",\"textureMB\":" + texture / 1048576 : "")
                    + "}");

                // 注入 OS 内存告警:验证修剪级联(memory-events.jsonl)与 30s 恢复观察期(后续 flip 的 hold 字段)
                if (i == WarnAtSeq)
                {
                    MemoryGovernor.ReportOsEvent(PressureLevel.Warning);
                    Log("{\"type\":\"warn-injected\",\"seq\":" + i + ",\"tSec\":" + F(runSw.Elapsed.TotalSeconds) + "}");
                }
            }

            latencies.Sort();
            var miss = PerfTrace.RecentMissStats(Math.Min(latencies.Count, 200));
            var extra = ExtraSummary?.Invoke();
            Log("{\"type\":\"done\""
                + ",\"flips\":" + flips
                + ",\"timeouts\":" + timeouts
                + ",\"p50Ms\":" + F(Pct(latencies, 0.50))
                + ",\"p95Ms\":" + F(Pct(latencies, 0.95))
                + ",\"maxMs\":" + F(latencies.Count > 0 ? latencies[^1] : -1)
                + ",\"minMs\":" + F(latencies.Count > 0 ? latencies[0] : -1)
                + ",\"proxyShown\":" + proxyShown
                + ",\"fullShown\":" + fullShown
                + ",\"decodeMissP50Ms\":" + F(miss.P50)
                + ",\"decodeMissMeanMs\":" + F(miss.Mean)
                + ",\"elapsedSec\":" + F(runSw.Elapsed.TotalSeconds)
                + (string.IsNullOrEmpty(extra) ? "" : ",\"extra\":\"" + Esc(extra) + "\"")
                + ",\"footprintSampledPeakMB\":" + FootprintMb(peakFootprint ?? 0)
                + ",\"cacheSampledPeakMB\":" + (peakCache ?? 0) / 1048576
                + ",\"poolIdleSampledPeakMB\":" + (peakPoolIdle ?? 0) / 1048576
                + (TextureBytesProvider is null ? "" : ",\"textureSampledPeakMB\":" + (peakTexture ?? 0) / 1048576)
                + "}");
            if (timeouts > 0) Environment.ExitCode = 1;
            Console.WriteLine($"[StressFlip] done: {flips} flips, {timeouts} timeouts");
        }
        catch (Exception ex)
        {
            Environment.ExitCode = 1;
            Log($"{{\"type\":\"error\",\"reason\":\"{Esc(ex.ToString())}\"}}");
        }
        finally
        {
            try { Completed?.Invoke(); }
            catch (Exception ex) { Console.WriteLine($"[StressFlip] completed hook failed: {ex.Message}"); }
        }
    }

    /// <summary>等待文件夹枚举出图片(最长 15s),返回当前文件列表快照。</summary>
    private static async Task<IReadOnlyList<PhotoViewer.Core.Image.ImageFile>> WaitForFilesAsync(MainViewModel vm)
    {
        for (var attempt = 0; attempt < 150; attempt++)
        {
            var count = await UiDispatcher.InvokeAsync(() => vm.FolderVM.AllFiles.Count);
            if (count > 0)
            {
                var displayed = await UiDispatcher.InvokeAsync(() => vm.FileVM.ThumbnailList.FilteredFiles.ToList());
                if (displayed.Count > 0) return displayed;
                return vm.FolderVM.AllFiles.ToList();
            }

            await Task.Delay(100);
        }

        return Array.Empty<PhotoViewer.Core.Image.ImageFile>();
    }

    /// <summary>最近邻秩 percentile;空序列返回 -1。</summary>
    private static double Pct(IReadOnlyList<double> sorted, double q)
        => sorted.Count == 0 ? -1 : sorted[Math.Min(sorted.Count - 1, (int)(q * sorted.Count))];

    private static string Esc(string s)
        => System.Text.Json.JsonEncodedText.Encode(s).ToString();

    /// <summary>字节 → 整 MB 文本;读不到(0)写 null,避免把未知 footprint 当成有效读数或托管堆大小。</summary>
    private static string FootprintMb(long bytes)
        => bytes > 0 ? (bytes / 1048576).ToString(CultureInfo.InvariantCulture) : "null";

    /// <summary>不变文化数值格式化(保留 1 位小数)。</summary>
    private static string F(double v) => v.ToString("F1", CultureInfo.InvariantCulture);

    /// <summary>布尔字面量。</summary>
    private static string B(bool v) => v ? "true" : "false";
}
