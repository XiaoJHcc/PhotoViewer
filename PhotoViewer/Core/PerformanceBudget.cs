using System;
using System.Runtime.InteropServices;

namespace PhotoViewer.Core;

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