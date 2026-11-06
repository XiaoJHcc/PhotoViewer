using System;
using System.Runtime.InteropServices;

namespace PhotoViewer.Apple;

/// <summary>
/// Apple 平台的内存实测:进程侧 task_info(TASK_VM_INFO) 的 phys_footprint 与 os_proc_available_memory,
/// 系统侧 host_statistics64(HOST_VM_INFO64) 的 free/inactive 页数。
/// 只做 P/Invoke,不含 AppKit/UIKit;macOS 与 iOS 的预算源与系统内存读数共用同一份读法,
/// footprint 含 Metal 纹理与 IOSurface,故"footprint + 可用"即 jetsam 上限;
/// 系统页统计是整机口径,与进程预算无关,不得互相冒充。
/// </summary>
public static class AppleMemoryFootprint
{
    // os_proc_available_memory(iOS 13+/macOS 13+):本进程还能分配多少字节而不触发 jetsam
    [DllImport("/usr/lib/libSystem.dylib")]
    private static extern long os_proc_available_memory();

    [DllImport("/usr/lib/libSystem.dylib")]
    private static extern IntPtr mach_task_self();

    // mach_host_self():返回 host 端口的 send right,用后必须 mach_port_deallocate 归还,否则每次调用泄漏一个端口名
    [DllImport("/usr/lib/libSystem.dylib")]
    private static extern IntPtr mach_host_self();

    [DllImport("/usr/lib/libSystem.dylib")]
    private static extern int mach_port_deallocate(IntPtr task, IntPtr name);

    // task_info(TASK_VM_INFO):取 phys_footprint(偏移 144,ulong)
    // host_statistics64(HOST_VM_INFO64):取 vm_statistics64(xnu osfmk/mach/vm_statistics.h)
    // host_page_size:取页大小(vm_size_t,原生字长)
    // P/Invoke 全部显式指针化:mono 解释器对 ref/out 封送存在已证实的间歇崩溃风险(真机 SIGSEGV)
    [DllImport("/usr/lib/libSystem.dylib")]
    private static extern int task_info(IntPtr task, int flavor, IntPtr info, IntPtr count);

    [DllImport("/usr/lib/libSystem.dylib")]
    private static extern int host_statistics64(IntPtr host, int flavor, IntPtr info, IntPtr count);

    [DllImport("/usr/lib/libSystem.dylib")]
    private static extern int host_page_size(IntPtr host, IntPtr pageSize);

    private const int TASK_VM_INFO = 22;
    private const int PhysFootprintOffset = 144;

    // HOST_VM_INFO64 = 4(xnu osfmk/mach/host_info.h);计数单位仍是 4 字节 natural_t,
    // HOST_VM_INFO64_COUNT = sizeof(vm_statistics64_data_t) / sizeof(integer_t)
    private const int HOST_VM_INFO64 = 4;
    private const int HostVmInfo64Count = 40;

    // vm_statistics64 前四个字段是 natural_t:free_count@0 / active_count@4 / inactive_count@8 / wire_count@12;
    // speculative 页已计入 free_count,故可用量只取 free + inactive,不重复计数。
    private const int FreeCountOffset = 0;
    private const int InactiveCountOffset = 8;
    private const int VmStatisticsBufferBytes = 256;

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

    /// <summary>
    /// 系统物理可用内存估算:页统计的 free + inactive 页 × 页大小(整机口径,与进程预算无关)。
    /// 不可得时返回 -1,由调用方按"未知"呈现;不得回退成进程预算或 os_proc_available_memory。
    /// </summary>
    public static long GetSystemAvailableBytes()
    {
        IntPtr host = IntPtr.Zero;
        IntPtr info = IntPtr.Zero;
        try
        {
            host = mach_host_self();
            if (host == IntPtr.Zero) return -1;
            long pageSize = ReadPageSize(host);
            if (pageSize <= 0) return -1;

            info = Marshal.AllocHGlobal(VmStatisticsBufferBytes);
            int kr;
            unsafe
            {
                uint count = HostVmInfo64Count;
                kr = host_statistics64(host, HOST_VM_INFO64, info, (IntPtr)(&count));
                if (kr != 0 || count * 4u < InactiveCountOffset + 4) return -1;
            }

            long freePages = (uint)Marshal.ReadInt32(info, FreeCountOffset);
            long inactivePages = (uint)Marshal.ReadInt32(info, InactiveCountOffset);
            return (freePages + inactivePages) * pageSize;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[AppleMemoryFootprint] host_statistics64 failed: {ex.Message}");
            return -1;
        }
        finally
        {
            if (info != IntPtr.Zero) Marshal.FreeHGlobal(info);
            if (host != IntPtr.Zero) mach_port_deallocate(mach_task_self(), host);
        }
    }

    /// <summary>页大小:优先 host_page_size,入口缺失时退回托管口径(同一 sysconf(_SC_PAGESIZE))。</summary>
    private static long ReadPageSize(IntPtr host)
    {
        IntPtr buffer = Marshal.AllocHGlobal(8);
        try
        {
            Marshal.WriteInt64(buffer, 0);
            if (host_page_size(host, buffer) == 0)
            {
                long size = IntPtr.Size == 8 ? Marshal.ReadInt64(buffer) : Marshal.ReadInt32(buffer);
                if (size > 0) return size;
            }
        }
        catch (EntryPointNotFoundException)
        {
            // host_page_size 由 C 库提供;缺失时不影响页统计读数
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }

        return Environment.SystemPageSize;
    }
}
