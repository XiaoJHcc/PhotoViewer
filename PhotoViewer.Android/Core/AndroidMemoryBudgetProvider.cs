using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using Android.App;
using Android.Content;
using PhotoViewer.Core.Memory;

namespace PhotoViewer.Android.Core;

/// <summary>
/// Android 内存预算提供者：预算 = 前台可见应用的匿名内存上限，占用 = 本进程 <c>RssAnon + VmSwap</c> + 外部占用（GL 纹理）。
/// </summary>
/// <remarks>
/// <para>Android 没有给前台应用的按字节配额 API：Android 10–16 由 PSI 驱动的 lmkd 在系统级回收，
/// 前台进程最后被杀；Android 17 起平台 MemoryLimiter 按 <c>MemTotal</c> 分档给可见进程设 cgroup <c>memory.high</c>，
/// 计量口径正是匿名驻留 + swap（不含 GPU 私有内存与文件页）。预算取该分档的可见上限，
/// 两类系统上都是"前台不被回收"的已知安全上限；<c>largeHeap</c> 只影响 ART Java 堆，与此无关。
/// 厂商系统可能另设更低的前台红线（小米 Sentinel 的 PSS 红线），存在时预算取两者较小者。</para>
/// <para>GPU 纹理在独立显存/驱动内存里，不在 <c>RssAnon</c> 中；主图宿主通过 <see cref="ExternalFootprint"/>
/// 把这份占用加进来，预算源给出的 footprint 才是"真的占了多少"。系统整机水位另作压力源：<c>availMem</c>
/// 逼近 <c>threshold</c>（系统开始杀后台服务的水位）时按比例收窄可用量，让缓存先退让。
/// Android 14 起前台收不到 RUNNING_* trim 回调，压力只能靠这里的周期探测发现。</para>
/// <para>读数有短时缓存：Governor 探测、准入与缓存容量推导在同一轮会连续读多次。</para>
/// </remarks>
public sealed class AndroidMemoryBudgetProvider : IMemoryBudgetProvider
{
    private const long MiB = 1024L * 1024L;

    /// <summary>
    /// 额外外部占用读数（当前实现为主图纹理字节）。GPU 内存不在 <c>RssAnon</c> 里，需单独记账；
    /// 探测线程随时会读，实现必须无阻塞（主图宿主写入原子快照）。
    /// </summary>
    public static Func<long>? ExternalFootprint
    {
        get => Volatile.Read(ref _externalFootprint);
        set => Volatile.Write(ref _externalFootprint, value);
    }
    private static Func<long>? _externalFootprint;

    /// <summary>读数缓存时长：2s 探测周期内的多次读取共用一份快照。</summary>
    private static readonly long SampleTtlTicks = Stopwatch.Frequency / 5;

    /// <summary>
    /// AOSP MemoryLimiter 可见进程 <c>memory.high</c> 分档（MiB）：<c>[MemTotal 下界, 上界) → 上限</c>。
    /// 档外（小于 3200 或不小于 18432）平台不设限，按相邻档的比例外推。
    /// </summary>
    private static readonly (long LowerMiB, long UpperMiB, long LimitMiB)[] VisibleTiers =
    [
        (3200, 4800, 2048),
        (4800, 6800, 4096),
        (6800, 9216, 5120),
        (9216, 14336, 8192),
        (14336, 18432, 10240),
    ];

    /// <summary>小内存设备（低于最低档）的预算比例。</summary>
    private const double SmallDeviceRatio = 0.45;

    /// <summary>大内存设备（高于最高档）的预算比例，与 8/16 GB 档的 62.5% 一致。</summary>
    private const double LargeDeviceRatio = 0.625;

    /// <summary>系统余量（<c>availMem − threshold</c>）达到 threshold 的几倍时不再收窄可用量。</summary>
    private const double SystemComfortThresholds = 2.0;

    private readonly object _gate = new();
    private readonly long _budgetBytes;
    private long _sampledAt;
    private long _footprintBytes;
    private long _systemAvailBytes = -1;
    private long _systemThresholdBytes;

    /// <summary>
    /// 厂商前台进程占用红线（系统属性，单位 kB）：超过即被系统强杀，不经 lmkd。
    /// 小米 HyperOS/MIUI 的 Sentinel 按进程 PSS（含 swap 部分）比对 <c>persist.sys.stability.pss_highest_line</c>，
    /// 小米 17 Ultra 实测 6291456 kB 时在 PSS 14.8 GB 处被 <c>used too many pss resource</c> 强杀。
    /// </summary>
    private static readonly string[] VendorPssLineProperties =
    [
        "persist.sys.stability.pss_highest_line",
    ];

    /// <summary>预算占厂商红线的比例。</summary>
    private const double VendorLineRatio = 0.9;

    /// <summary>
    /// 预算 = min(AOSP 可见档, 厂商红线)，并记录一次启动日志（真机据此核对档位）。
    /// 厂商红线只在 Sentinel 开关未关闭时生效。
    /// </summary>
    public AndroidMemoryBudgetProvider()
    {
        var info = ReadMemoryInfo();
        long total = info?.TotalMem ?? 0;
        long tier = BudgetForTotal(total);
        long vendorLine = ReadVendorPssLineBytes();
        // 红线按 PSS 计，含文件页按比例分摊与驱动内存，本类 footprint 只近似它（真机两者差约 2%），留 10% 余量。
        long vendorBudget = (long)(vendorLine * VendorLineRatio);
        _budgetBytes = vendorBudget > 0 && tier > 0 ? Math.Min(tier, vendorBudget) : tier;
        Console.WriteLine($"[AndroidMemoryBudget] MemTotal={total / MiB}MB tier={tier / MiB}MB " +
                          $"vendorLine={(vendorLine > 0 ? vendorLine / MiB + "MB" : "无")} budget={_budgetBytes / MiB}MB " +
                          $"availMem={(info?.AvailMem ?? 0) / MiB}MB threshold={(info?.Threshold ?? 0) / MiB}MB");
    }

    /// <summary>读厂商 PSS 红线（字节）；属性不存在、为 0 或监控被关闭时返回 0。</summary>
    private static long ReadVendorPssLineBytes()
    {
        if (string.Equals(ReadSystemProperty("persist.sys.stability.enable_sentinel_resource_monitor"), "false",
                StringComparison.OrdinalIgnoreCase))
            return 0;
        foreach (var key in VendorPssLineProperties)
        {
            if (long.TryParse(ReadSystemProperty(key), out long kb) && kb > 0) return kb * 1024L;
        }
        return 0;
    }

    /// <summary>经 <c>/system/bin/getprop</c> 读系统属性（SDK 无公开读取接口）；失败返回空串。</summary>
    private static string ReadSystemProperty(string key)
    {
        try
        {
            using var process = Java.Lang.Runtime.GetRuntime()!.Exec(["/system/bin/getprop", key])!;
            using var reader = new Java.IO.BufferedReader(new Java.IO.InputStreamReader(process.InputStream!));
            var value = reader.ReadLine()?.Trim() ?? string.Empty;
            process.WaitFor();
            return value;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[AndroidMemoryBudget] getprop {key} failed: {ex.Message}");
            return string.Empty;
        }
    }

    /// <summary>按 <c>MemTotal</c> 查可见上限分档；读不到总内存时为 0（Governor 退化为恒 Normal）。</summary>
    /// <param name="totalBytes">内核可见总内存。</param>
    /// <returns>预算字节数。</returns>
    internal static long BudgetForTotal(long totalBytes)
    {
        if (totalBytes <= 0) return 0;
        long totalMiB = totalBytes / MiB;
        foreach (var (lower, upper, limit) in VisibleTiers)
        {
            if (totalMiB >= lower && totalMiB < upper) return limit * MiB;
        }
        return (long)(totalBytes * (totalMiB < VisibleTiers[0].LowerMiB ? SmallDeviceRatio : LargeDeviceRatio));
    }

    /// <summary>预算 = 前台可见进程的匿名内存上限（按设备总内存分档）。</summary>
    public long GetBudgetBytes() => _budgetBytes;

    /// <summary>
    /// 可用 = 预算 − 本进程占用；系统余量不足 <see cref="SystemComfortThresholds"/> 倍 threshold 时
    /// 再按余量比例收窄（到达 threshold 即为 0），整机吃紧时缓存先于 lmkd 退让。
    /// </summary>
    public long GetAvailableBytes()
    {
        if (_budgetBytes <= 0) return long.MaxValue;
        Sample(out long anon, out long systemAvail, out long threshold);
        long footprint = anon + ExternalBytes();
        long available = footprint > 0 ? Math.Max(0, _budgetBytes - footprint) : long.MaxValue;
        if (systemAvail >= 0 && threshold > 0)
        {
            double fraction = Math.Clamp((systemAvail - threshold) / (SystemComfortThresholds * threshold), 0, 1);
            if (fraction < 1) available = Math.Min(available, (long)(_budgetBytes * fraction));
        }
        return available;
    }

    /// <summary>
    /// 本进程占用 = <c>RssAnon + VmSwap</c>（与 MemoryLimiter 同口径；含 Mono 托管堆与原生解码缓冲）
    /// + <see cref="ExternalFootprint"/>（GL 纹理，独立显存不在匿名页里）。
    /// </summary>
    public long GetFootprintBytes()
    {
        Sample(out long anon, out _, out _);
        return anon + ExternalBytes();
    }

    /// <summary>读外部占用；未挂读数或读数自身失败一律按 0（预算源不得抛给探测线程）。</summary>
    private static long ExternalBytes()
    {
        var external = _externalFootprint;
        if (external == null) return 0;
        try { return Math.Max(0, external()); }
        catch { return 0; }
    }

    /// <summary>取（必要时刷新）读数快照。</summary>
    private void Sample(out long footprint, out long systemAvail, out long threshold)
    {
        lock (_gate)
        {
            long now = Stopwatch.GetTimestamp();
            if (_sampledAt == 0 || now - _sampledAt > SampleTtlTicks)
            {
                _footprintBytes = ReadAnonFootprintBytes();
                var info = ReadMemoryInfo();
                _systemAvailBytes = info?.AvailMem ?? -1;
                _systemThresholdBytes = info?.Threshold ?? 0;
                _sampledAt = now;
            }
            footprint = _footprintBytes;
            systemAvail = _systemAvailBytes;
            threshold = _systemThresholdBytes;
        }
    }

    /// <summary>读 <c>/proc/self/status</c> 的 <c>RssAnon + VmSwap</c>（kB 行）；读不到时为 0。</summary>
    internal static long ReadAnonFootprintBytes()
    {
        try
        {
            long anonKb = 0, swapKb = 0;
            foreach (var line in File.ReadLines("/proc/self/status"))
            {
                if (line.StartsWith("RssAnon:", StringComparison.Ordinal)) anonKb = ParseKb(line);
                else if (line.StartsWith("VmSwap:", StringComparison.Ordinal)) swapKb = ParseKb(line);
            }
            return (anonKb + swapKb) * 1024L;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[AndroidMemoryBudget] /proc/self/status failed: {ex.Message}");
            return 0;
        }
    }

    /// <summary>解析 <c>Key:   12345 kB</c> 行的数值。</summary>
    private static long ParseKb(string line)
    {
        int start = line.IndexOf(':') + 1;
        var span = line.AsSpan(start).Trim();
        int space = span.IndexOf(' ');
        if (space > 0) span = span[..space];
        return long.TryParse(span, out long value) ? value : 0;
    }

    private static ActivityManager.MemoryInfo? ReadMemoryInfo()
    {
        try
        {
            var am = (ActivityManager?)Application.Context.GetSystemService(Context.ActivityService);
            if (am == null) return null;
            var info = new ActivityManager.MemoryInfo();
            am.GetMemoryInfo(info);
            return info;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[AndroidMemoryBudget] GetMemoryInfo failed: {ex.Message}");
            return null;
        }
    }
}
