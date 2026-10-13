using System;
using System.Runtime.InteropServices;
using PhotoViewer.Core.Memory;
using PhotoViewer.Core.Platform;

namespace PhotoViewer.iOS.Core;

/// <summary>
/// iOS 内存预算提供者:以系统 jetsam 口径实测。
/// 预算 = phys_footprint(当前驻留) + os_proc_available_memory(距杀进程余量);
/// 可用 = os_proc_available_memory。API 失效时回退机型表(<see cref="iOSPerformanceBudget"/>)。
/// </summary>
public sealed class iOSMemoryBudgetProvider : IMemoryBudgetProvider
{
    // os_proc_available_memory(iOS 13+):本进程还能分配多少字节而不触发 jetsam
    [DllImport("/usr/lib/libSystem.dylib")]
    private static extern long os_proc_available_memory();

    [DllImport("/usr/lib/libSystem.dylib")]
    private static extern IntPtr mach_task_self();

    // task_info(TASK_VM_INFO):取 phys_footprint(偏移 144,ulong)
    // P/Invoke 全部显式指针化:mono 解释器对 ref/out 封送存在已证实的间歇崩溃风险(2026-10-13 真机 SIGSEGV)
    [DllImport("/usr/lib/libSystem.dylib")]
    private static extern int task_info(IntPtr task, int flavor, IntPtr info, IntPtr count);

    private const int TASK_VM_INFO = 22;
    private const int PhysFootprintOffset = 144;

    /// <summary>距 jetsam 上限的余量字节数;失效时回退"机型表预算 − 当前驻留"。</summary>
    public long GetAvailableBytes()
    {
        try
        {
            long avail = os_proc_available_memory();
            if (avail > 0) return avail;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[iOSMemoryBudget] os_proc_available_memory failed: {ex.Message}");
        }

        // 回退:机型表静态预算 − 当前驻留(phase-1 前旧口径)
        long budgetMb = new iOSPerformanceBudget().GetAppMemoryLimitMB();
        long footprint = GetPhysFootprint();
        return Math.Max(0, budgetMb * 1024L * 1024 - footprint);
    }

    /// <summary>预算 = 当前驻留 + 距 jetsam 余量(实测动态值,替代静态机型表)。</summary>
    public long GetBudgetBytes()
    {
        long footprint = GetPhysFootprint();
        long avail = GetAvailableBytes();
        long budget = footprint + avail;
        if (budget > 0) return budget;
        // 双路皆失效:机型表静态预算兜底
        return (long)new iOSPerformanceBudget().GetAppMemoryLimitMB() * 1024 * 1024;
    }

    /// <summary>当前进程驻留内存(phys_footprint,字节);失败返回 0。</summary>
    private static long GetPhysFootprint()
    {
        IntPtr buf = IntPtr.Zero;
        try
        {
            buf = Marshal.AllocHGlobal(256);
            int kr;
            unsafe
            {
                uint count = 256 / 4; // task_info 的 count 以 4 字节为单位
                kr = task_info(mach_task_self(), TASK_VM_INFO, buf, (IntPtr)(&count));
                if (kr != 0 || count * 4u < PhysFootprintOffset + 8) return 0;
            }
            return Marshal.ReadInt64(buf, PhysFootprintOffset);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[iOSMemoryBudget] task_info failed: {ex.Message}");
            return 0;
        }
        finally
        {
            if (buf != IntPtr.Zero) Marshal.FreeHGlobal(buf);
        }
    }
}
