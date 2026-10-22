using System;
using System.Runtime.InteropServices;

namespace PhotoViewer.Apple;

/// <summary>
/// Apple 平台的进程内存实测:task_info(TASK_VM_INFO) 的 phys_footprint 与 os_proc_available_memory。
/// 只做 P/Invoke,不含 AppKit/UIKit;macOS 与 iOS 的预算源共用同一份读法,
/// footprint 含 Metal 纹理与 IOSurface,故"footprint + 可用"即 jetsam 上限。
/// </summary>
public static class AppleMemoryFootprint
{
    // os_proc_available_memory(iOS 13+/macOS 13+):本进程还能分配多少字节而不触发 jetsam
    [DllImport("/usr/lib/libSystem.dylib")]
    private static extern long os_proc_available_memory();

    [DllImport("/usr/lib/libSystem.dylib")]
    private static extern IntPtr mach_task_self();

    // task_info(TASK_VM_INFO):取 phys_footprint(偏移 144,ulong)
    // P/Invoke 全部显式指针化:mono 解释器对 ref/out 封送存在已证实的间歇崩溃风险(真机 SIGSEGV)
    [DllImport("/usr/lib/libSystem.dylib")]
    private static extern int task_info(IntPtr task, int flavor, IntPtr info, IntPtr count);

    private const int TASK_VM_INFO = 22;
    private const int PhysFootprintOffset = 144;

    /// <summary>当前进程驻留内存(phys_footprint,字节);读取失败返回 0。</summary>
    public static long GetPhysFootprintBytes()
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
            Console.WriteLine($"[AppleMemoryFootprint] task_info failed: {ex.Message}");
            return 0;
        }
        finally
        {
            if (buf != IntPtr.Zero) Marshal.FreeHGlobal(buf);
        }
    }

    /// <summary>距 jetsam 上限的余量字节数;API 不可用或不支持时返回 0。</summary>
    public static long GetAvailableBytes()
    {
        try
        {
            long available = os_proc_available_memory();
            return available > 0 ? available : 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[AppleMemoryFootprint] os_proc_available_memory failed: {ex.Message}");
            return 0;
        }
    }
}
