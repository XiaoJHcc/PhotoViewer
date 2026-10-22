using System;
using System.Runtime.InteropServices;

namespace PhotoViewer.Core.Platform;

/// <summary>
/// 提供当前平台的性能预算信息。
/// </summary>
public interface IPerformanceBudget
{
    /// <summary>
    /// 获取应用可用的内存上限（MB）。
    /// </summary>
    int GetAppMemoryLimitMB();

    /// <summary>
    /// 获取当前平台可用于解码任务的 CPU 并行能力。
    /// </summary>
    int GetCpuCoreCount();

    /// <summary>
    /// 获取原生解码路径建议的最大并行预载线程数。
    /// </summary>
    int GetNativePreloadThreadLimit();

    /// <summary>
    /// 获取 CPU 解码路径建议的最大并行预载线程数。
    /// </summary>
    int GetCpuPreloadThreadLimit();

    // ---- 设置页默认值建议 ----
    // 以下四项都有默认实现（跨平台旧口径），平台只在实测口径不同时覆盖；
    // 设置页的默认值只从这里取，不再各自维护一份公式。

    /// <summary>缓存内存上限的默认推荐值（MB）：设备内存上限的 50%，夹在 512–4096 之间。</summary>
    /// <returns>推荐缓存内存上限（MB）。</returns>
    int GetRecommendedCacheMemoryMB()
    {
        return Math.Max(512, Math.Min(GetAppMemoryLimitMB() / 2, DefaultCacheMemoryCeilingMB));
    }

    /// <summary>缓存数量上限的默认推荐值（张）：内存不足 4 GB 时按每张 33 MP / 132 MB 折算，否则 30 张。</summary>
    /// <param name="cacheMemoryMB">缓存内存上限（MB）。</param>
    /// <returns>推荐缓存数量上限（张）。</returns>
    int GetRecommendedCacheCount(int cacheMemoryMB)
        => cacheMemoryMB < DefaultCacheMemoryCeilingMB
            ? Math.Min(cacheMemoryMB / LegacyPhotoMegabytes, DefaultCacheCount)
            : DefaultCacheCount;

    /// <summary>原生解码路径的默认推荐预载线程数：内存不足 4 GB 时按比例缩放设备上限。</summary>
    /// <param name="cacheMemoryMB">缓存内存上限（MB）。</param>
    /// <returns>推荐原生解码线程数。</returns>
    int GetRecommendedNativePreloadParallelism(int cacheMemoryMB)
        => ScalePreloadThreadLimit(cacheMemoryMB, GetNativePreloadThreadLimit());

    /// <summary>CPU 解码路径的默认推荐预载线程数：内存不足 4 GB 时按比例缩放设备上限。</summary>
    /// <param name="cacheMemoryMB">缓存内存上限（MB）。</param>
    /// <returns>推荐 CPU 解码线程数。</returns>
    int GetRecommendedCpuPreloadParallelism(int cacheMemoryMB)
        => ScalePreloadThreadLimit(cacheMemoryMB, GetCpuPreloadThreadLimit());

    /// <summary>默认口径按 4 GB 缓存内存对齐设备线程上限。</summary>
    private const int DefaultCacheMemoryCeilingMB = 4096;

    /// <summary>默认口径按 33 MP 照片占 132 MB 估算张数。</summary>
    private const int LegacyPhotoMegabytes = 132;

    /// <summary>默认口径的缓存数量上限。</summary>
    private const int DefaultCacheCount = 30;

    /// <summary>按内存占 4 GB 的比例缩放设备线程上限，结果夹在 1 与设备上限之间。</summary>
    /// <param name="cacheMemoryMB">缓存内存上限（MB）。</param>
    /// <param name="threadLimit">设备给出的线程上限。</param>
    /// <returns>推荐线程数。</returns>
    private static int ScalePreloadThreadLimit(int cacheMemoryMB, int threadLimit)
    {
        if (threadLimit <= 1 || cacheMemoryMB >= DefaultCacheMemoryCeilingMB)
        {
            return Math.Max(1, threadLimit);
        }

        var scaled = (int)Math.Round(cacheMemoryMB / (double)DefaultCacheMemoryCeilingMB * threadLimit);
        return Math.Clamp(scaled, 1, threadLimit);
    }
}

/// <summary>
/// 跨平台性能预算入口。
/// </summary>
public static class PerformanceBudget
{
    private static IPerformanceBudget _budget = new DefaultPerformanceBudget();

    /// <summary>
    /// 由各平台启动时注入具体实现。
    /// </summary>
    public static void Initialize(IPerformanceBudget budget)
    {
        _budget = budget ?? new DefaultPerformanceBudget();
    }

    /// <summary>
    /// 获取应用可用的内存上限（MB）。
    /// </summary>
    public static int AppMemoryLimitMB => _budget.GetAppMemoryLimitMB();

    /// <summary>
    /// 获取当前平台可用于解码任务的 CPU 并行能力。
    /// </summary>
    public static int CpuCoreCount => _budget.GetCpuCoreCount();

    /// <summary>
    /// 获取原生解码路径建议的最大并行预载线程数。
    /// </summary>
    public static int NativePreloadThreadLimit => _budget.GetNativePreloadThreadLimit();

    /// <summary>
    /// 获取 CPU 解码路径建议的最大并行预载线程数。
    /// </summary>
    public static int CpuPreloadThreadLimit => _budget.GetCpuPreloadThreadLimit();

    /// <summary>缓存内存上限的默认推荐值（MB）。</summary>
    public static int RecommendedCacheMemoryMB => _budget.GetRecommendedCacheMemoryMB();

    /// <summary>缓存数量上限的默认推荐值（张）。</summary>
    /// <param name="cacheMemoryMB">缓存内存上限（MB）。</param>
    /// <returns>推荐缓存数量上限（张）。</returns>
    public static int RecommendedCacheCount(int cacheMemoryMB) => _budget.GetRecommendedCacheCount(cacheMemoryMB);

    /// <summary>原生解码路径的默认推荐预载线程数。</summary>
    /// <param name="cacheMemoryMB">缓存内存上限（MB）。</param>
    /// <returns>推荐原生解码线程数。</returns>
    public static int RecommendedNativePreloadParallelism(int cacheMemoryMB)
        => _budget.GetRecommendedNativePreloadParallelism(cacheMemoryMB);

    /// <summary>CPU 解码路径的默认推荐预载线程数。</summary>
    /// <param name="cacheMemoryMB">缓存内存上限（MB）。</param>
    /// <returns>推荐 CPU 解码线程数。</returns>
    public static int RecommendedCpuPreloadParallelism(int cacheMemoryMB)
        => _budget.GetRecommendedCpuPreloadParallelism(cacheMemoryMB);
}

/// <summary>
/// 默认性能预算实现（适用于 Windows, macOS 平台）。
/// </summary>
public sealed class DefaultPerformanceBudget : IPerformanceBudget
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct MemoryStatusEx
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx lpBuffer);

    /// <summary>
    /// 获取应用可用的内存上限（MB）。
    /// </summary>
    public int GetAppMemoryLimitMB()
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                var memoryStatus = new MemoryStatusEx
                {
                    dwLength = (uint)Marshal.SizeOf<MemoryStatusEx>()
                };

                if (GlobalMemoryStatusEx(ref memoryStatus))
                {
                    return (int)(memoryStatus.ullTotalPhys / (1024 * 1024));
                }
            }

            var memoryInfo = GC.GetGCMemoryInfo();
            var totalAvailableMemoryBytes = memoryInfo.TotalAvailableMemoryBytes;
            return (int)(totalAvailableMemoryBytes / (1024 * 1024));
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to get performance budget memory limit: {ex.Message}");
            return 0;
        }
    }

    /// <summary>
    /// 获取当前平台可用于解码任务的 CPU 并行能力。
    /// </summary>
    public int GetCpuCoreCount()
        => Math.Max(1, Environment.ProcessorCount);

    /// <summary>
    /// 获取原生解码路径建议的最大并行预载线程数。
    /// </summary>
    public int GetNativePreloadThreadLimit()
        => Math.Min(GetCpuCoreCount(), 8);

    /// <summary>
    /// 获取 CPU 解码路径建议的最大并行预载线程数。
    /// </summary>
    public int GetCpuPreloadThreadLimit()
        => Math.Min(GetCpuCoreCount(), 8);
}