using System;
using System.Runtime.InteropServices;
using System.Threading;
using PhotoViewer.Core.Memory;

namespace PhotoViewer.Windows.Core;

/// <summary>
/// Windows 预算源:以**物理内存 × 50%** 为预算,以**进程私有提交**为足迹,并把系统低内存通知接成 OS 压力源。
/// </summary>
/// <remarks>
/// 与 <see cref="DefaultMemoryBudgetProvider"/> 的区别正是口径:后者只看 CLR 托管堆,
/// 而本平台的大头在托管之外 —— 原生 libheif/FFmpeg 的解码平面、D3D 纹理的系统内存副本都不进 GC 账本,
/// 用托管堆记账会把"已经吃掉 2 GB 原生缓冲"的进程显示成毫无压力。
///
/// 可用余量取两个约束的最小值:
/// <list type="bullet">
/// <item>预算 − 本进程私有提交(本进程自己还能涨多少);</item>
/// <item>系统可用物理 − 1 GiB 保留(别把整机推到换页;其它应用占满内存时这一项会先变紧)。</item>
/// </list>
/// 低内存通知句柄在系统内存紧张期间会持续处于 signaled,后台线程因此按 5 秒限流上报,
/// 只在升级方向起作用(<see cref="MemoryGovernor.ReportOsEvent"/> 只升不降),降级仍由周期探测完成。
/// </remarks>
internal sealed partial class WindowsMemoryBudgetProvider : IMemoryBudgetProvider, IDisposable
{
    /// <summary>预算占物理内存的比例(与 macOS 同口径)。</summary>
    private const double BudgetRatio = 0.5;

    /// <summary>留给系统与其应用的物理内存下限(1 GiB)。</summary>
    private const long SystemReserveBytes = 1L << 30;

    /// <summary>低内存通知的最小上报间隔(句柄在紧张期间保持 signaled,不限流会空转)。</summary>
    private const int OsReportIntervalMs = 5000;

    /// <summary><c>CreateMemoryResourceNotification</c> 的低内存通知类型。</summary>
    private const int LowMemoryResourceNotification = 0;

    private const uint WaitObject0 = 0;

    private readonly IntPtr _notification;
    private readonly Thread? _watcher;
    private volatile bool _stopped;

    /// <summary>创建预算源;低内存通知不可用时退化为纯周期探测(不影响预算读数)。</summary>
    public WindowsMemoryBudgetProvider()
    {
        _notification = CreateMemoryResourceNotification(LowMemoryResourceNotification);
        if (_notification == IntPtr.Zero) return;

        _watcher = new Thread(WatchLowMemory)
        {
            IsBackground = true,
            Name = "MemoryPressureMonitor",
        };
        _watcher.Start();
    }

    /// <inheritdoc/>
    public long GetBudgetBytes()
    {
        var status = ReadStatus();
        if (status is not { } value || value.TotalPhys == 0) return 0;
        return (long)(value.TotalPhys * BudgetRatio);
    }

    /// <inheritdoc/>
    public long GetAvailableBytes()
    {
        var status = ReadStatus();
        if (status is not { } value || value.TotalPhys == 0) return long.MaxValue;

        var budget = (long)(value.TotalPhys * BudgetRatio);
        var fromBudget = budget - GetFootprintBytes();
        var fromSystem = (long)value.AvailPhys - SystemReserveBytes;
        var available = Math.Min(fromBudget, fromSystem);
        return available > 0 ? available : 0;
    }

    /// <inheritdoc/>
    public long GetFootprintBytes()
    {
        if (!GetProcessMemoryInfo(GetCurrentProcess(), out var counters, (uint)Marshal.SizeOf<ProcessMemoryCountersEx>()))
        {
            return 0;
        }

        var usage = (ulong)counters.PrivateUsage;
        return usage > long.MaxValue ? long.MaxValue : (long)usage;
    }

    /// <summary>停止低内存监视线程并关闭通知句柄。</summary>
    public void Dispose()
    {
        _stopped = true;
        if (_notification != IntPtr.Zero) CloseHandle(_notification);
    }

    /// <summary>后台等待低内存通知:只在 signaled 时刻上报 Warning,并按 5 秒限流。</summary>
    private void WatchLowMemory()
    {
        while (!_stopped)
        {
            // 超时值同时充当停止标志的轮询粒度,并保证线程几乎不占 CPU。
            if (WaitForSingleObject(_notification, OsReportIntervalMs) != WaitObject0) continue;
            MemoryGovernor.ReportOsEvent(PressureLevel.Warning);
            Thread.Sleep(OsReportIntervalMs);
        }
    }

    private static (ulong TotalPhys, ulong AvailPhys)? ReadStatus()
    {
        var status = new MemoryStatusEx { Length = (uint)Marshal.SizeOf<MemoryStatusEx>() };
        if (!GlobalMemoryStatusEx(ref status)) return null;
        return (status.TotalPhys, status.AvailPhys);
    }

    // ------------------------------------------------------------------
    // Win32
    // ------------------------------------------------------------------

    /// <summary><c>MEMORYSTATUSEX</c>:只取本类需要的物理内存总量与当前可用量。</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatusEx
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhys;
        public ulong AvailPhys;
        public ulong TotalPageFile;
        public ulong AvailPageFile;
        public ulong TotalVirtual;
        public ulong AvailVirtual;
        public ulong AvailExtendedVirtual;
    }

    /// <summary><c>PROCESS_MEMORY_COUNTERS_EX</c>:<see cref="PrivateUsage"/> 即进程私有提交(含原生缓冲)。</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessMemoryCountersEx
    {
        public uint Size;
        public uint PageFaultCount;
        public nuint PeakWorkingSetSize;
        public nuint WorkingSetSize;
        public nuint QuotaPeakPagedPoolUsage;
        public nuint QuotaPagedPoolUsage;
        public nuint QuotaPeakNonPagedPoolUsage;
        public nuint QuotaNonPagedPoolUsage;
        public nuint PagefileUsage;
        public nuint PeakPagefileUsage;
        public nuint PrivateUsage;
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GlobalMemoryStatusEx(ref MemoryStatusEx status);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial IntPtr GetCurrentProcess();

    [LibraryImport("psapi.dll", SetLastError = true, EntryPoint = "GetProcessMemoryInfo")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetProcessMemoryInfo(IntPtr process, out ProcessMemoryCountersEx counters, uint size);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial IntPtr CreateMemoryResourceNotification(int notificationType);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial uint WaitForSingleObject(IntPtr handle, uint milliseconds);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(IntPtr handle);
}
