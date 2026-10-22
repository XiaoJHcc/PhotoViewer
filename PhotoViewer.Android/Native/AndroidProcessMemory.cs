using System;
using System.Globalization;
using System.IO;

namespace PhotoViewer.Android.Native;

/// <summary>
/// 进程内存读数：直接读 <c>/proc/self/status</c> 的 <c>RssAnon</c> / <c>VmSwap</c> / <c>VmHWM</c>（峰值 RSS）。
/// 压测汇总行与性能探针共用同一口径，避免两处各写一套解析；读不到时各项为 0。
/// </summary>
internal static class AndroidProcessMemory
{
    /// <summary>一次读数（字节）。</summary>
    internal readonly struct Snapshot
    {
        /// <summary>匿名驻留（托管堆 + 原生匿名页）。</summary>
        public readonly long RssAnonBytes;

        /// <summary>换出到 swap 的匿名页。</summary>
        public readonly long VmSwapBytes;

        /// <summary>进程生命周期内的峰值 RSS（VmHWM）。</summary>
        public readonly long VmHwmBytes;

        /// <summary>文件映射驻留（APK / .so / 资源）。</summary>
        public readonly long RssFileBytes;

        /// <summary>构造读数。</summary>
        public Snapshot(long rssAnonBytes, long vmSwapBytes, long vmHwmBytes, long rssFileBytes)
        {
            RssAnonBytes = rssAnonBytes;
            VmSwapBytes = vmSwapBytes;
            VmHwmBytes = vmHwmBytes;
            RssFileBytes = rssFileBytes;
        }

        /// <summary>Android 侧真正的内存压力口径：匿名驻留 + 换出。</summary>
        public long FootprintBytes => RssAnonBytes + VmSwapBytes;
    }

    /// <summary>读取当前进程内存；<c>/proc</c> 不可用时返回全 0。</summary>
    internal static Snapshot Read()
    {
        long rssAnon = 0, vmSwap = 0, hwm = 0, rssFile = 0;
        try
        {
            foreach (var line in File.ReadLines("/proc/self/status"))
            {
                if (line.StartsWith("RssAnon:", StringComparison.Ordinal)) rssAnon = ParseKilobytes(line);
                else if (line.StartsWith("VmSwap:", StringComparison.Ordinal)) vmSwap = ParseKilobytes(line);
                else if (line.StartsWith("VmHWM:", StringComparison.Ordinal)) hwm = ParseKilobytes(line);
                else if (line.StartsWith("RssFile:", StringComparison.Ordinal)) rssFile = ParseKilobytes(line);
            }
        }
        catch (Exception exception)
        {
            Console.WriteLine($"[ProcessMemory] 读取 /proc/self/status 失败：{exception.Message}");
        }

        return new Snapshot(rssAnon, vmSwap, hwm, rssFile);
    }

    /// <summary>一行纯文本：当前匿名驻留/换出/峰值 RSS/文件映射驻留。</summary>
    internal static string Summary()
    {
        var snapshot = Read();
        return $"rssAnonMB={Megabytes(snapshot.RssAnonBytes)}"
            + $" swapMB={Megabytes(snapshot.VmSwapBytes)}"
            + $" peakRssMB={Megabytes(snapshot.VmHwmBytes)}"
            + $" rssFileMB={Megabytes(snapshot.RssFileBytes)}";
    }

    /// <summary>字节数 → MB（整数，压测行保持短）。</summary>
    internal static string Megabytes(long bytes)
        => (bytes >> 20).ToString(CultureInfo.InvariantCulture);

    /// <summary>解析 <c>Key:   123 kB</c> 的数值并换算成字节。</summary>
    private static long ParseKilobytes(string line)
    {
        var colon = line.IndexOf(':');
        if (colon < 0) return 0;
        var value = line[(colon + 1)..].Trim();
        if (value.EndsWith("kB", StringComparison.Ordinal)) value = value[..^2].Trim();
        return long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var kilobytes)
            ? kilobytes * 1024
            : 0;
    }
}
