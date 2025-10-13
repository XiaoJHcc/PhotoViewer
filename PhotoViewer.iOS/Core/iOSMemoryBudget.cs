using System;
using System.Linq;
using System.Runtime.InteropServices;
using Foundation;
using PhotoViewer.Core;

namespace PhotoViewer.iOS.Core;

public sealed class iOSMemoryBudget : IMemoryBudget
{
    // 新增：记录上次系统内存告警前缓存大小（MB）
    public static int LastMemoryWarningCacheMB { get; private set; }
    public static void RecordMemoryWarningCacheMB(long bytes)
        => LastMemoryWarningCacheMB = (int)Math.Max(0, bytes / (1024 * 1024));

    private class DeviceBudget
    {
        public string[] Keys;            // 型号标识或营销名关键字(小写)
        public int MemoryCapacityMB;     // 内存容量列 (例如 8GB -> 8192)
        public int CrashLimitMB;         // 崩溃量 (表中“崩溃量”列)
        public DeviceBudget(int memoryCapacityMB, int crashLimitMB, params string[] keys)
        {
            MemoryCapacityMB = memoryCapacityMB;
            CrashLimitMB = crashLimitMB;
            Keys = keys.Select(k => k.Trim().ToLowerInvariant()).ToArray();
        }
    }

    // 说明：
    // 1. CrashLimitMB 选取表中的“崩溃量”值（未含提升权限版本除外）。
    // 2. 设备标识数组包含可能的 hw.machine 型号（如 iPhone15,2）与营销名关键字（如 "iphone 15 pro"）。
    // 3. 可持续补全更多表中机型。
    private static readonly DeviceBudget[] Budgets =
    {
        // iPhone 系列 (节选)
        new(12288, 3356, "iphone17,1", "iphone 17 pro"),              // 12GB 标准权限
        new(8192, 3341, "iphone16,2", "iphone16,1", "iphone 15 pro"), // 8GB
        new(6144, 3064, "iphone13,3", "iphone12,3", "iphone 12 pro", "iphone 13 pro max", "iphone 12 pro max"),
        new(4096, 2091, "iphone14,5", "iphone 13", "iphone 12", "iphone 11", "iphone 11 pro", "iphone 11 pro max"),
        new(3072, 1843, "iphone11,8", "iphone xr"),
        new(3072, 1392, "iphone10,3", "iphone10,6", "iphone x"),
        new(2048, 1395, "iphone9,1", "iphone9,3", "iphone7,2", "iphone 7", "iphone se (1st)"),
        new(2048, 1444, "iphone se"), // 2GB SE 特例
        new(1024, 646, "iphone6,1", "iphone6,2", "iphone5s"),
        new(512, 325, "iphone4,1", "iphone 4"),
        // iPad / iPad Pro (节选)
        new(8192, 5089, "ipad16,3", "ipad pro 11 (m4)", "ipad pro 11 英寸 (第 5 代)"),
        new(8192, 5089, "ipad14,3", "ipad14,4", "ipad pro 11 英寸 (第 4 代)"),
        new(6144, 4597, "ipad13,4", "ipad13,5", "ipad13,6", "ipad13,7", "ipad pro 11 英寸 (第 2 代)"),
        new(6144, 4593, "ipad8,11", "ipad8,12", "ipad pro 12.9 英寸 (第 4 代)"),
        new(6144, 4598, "ipad8,7", "ipad8,8", "ipad pro 12.9 英寸 (第 3 代)"),
        new(4096, 3058, "ipad6,7", "ipad6,8", "ipad pro 12.9 英寸"),
        new(4096, 3057, "ipad7,3", "ipad7,4", "ipad pro 10.5 英寸"),
        new(4096, 2858, "ipad8,1", "ipad8,2", "ipad pro 11 英寸 (第 1 代)"),
        new(2048, 1395, "ipad6,3", "ipad6,4", "ipad pro 9.7 英寸"),
        new(6144, 3052, "ipad a16", "ipad (a16)"),
        new(3072, 1844, "ipad7,5", "ipad7,6", "ipad (第 7 代)"),
        new(2048, 1383, "ipad5,4", "ipad air 2"),
        new(1024, 697, "ipad4,1", "ipad air"),
        new(1024, 696, "ipad mini 2"),
        new(512, 297, "ipad2,5", "ipad mini"),
    };

    [DllImport("libc")]
    private static extern int sysctlbyname(string name, IntPtr oldp, ref nuint oldlenp, IntPtr newp, nuint newlen);

    private static string GetMachineIdentifier()
    {
        try
        {
            nuint len = 0;
            sysctlbyname("hw.machine", IntPtr.Zero, ref len, IntPtr.Zero, 0);
            if (len == 0) return string.Empty;
            var p = Marshal.AllocHGlobal((int)len);
            try
            {
                sysctlbyname("hw.machine", p, ref len, IntPtr.Zero, 0);
                return Marshal.PtrToStringAnsi(p) ?? string.Empty;
            }
            finally { Marshal.FreeHGlobal(p); }
        }
        catch { return string.Empty; }
    }

    private static int GetPhysicalMemoryMB()
    {
        try
        {
            var bytes = (long)NSProcessInfo.ProcessInfo.PhysicalMemory;
            return (int)(bytes / (1024 * 1024));
        }
        catch { return 0; }
    }

    public int GetAppMemoryLimitMB()
    {
        var machine = GetMachineIdentifier().ToLowerInvariant();
        var physical = GetPhysicalMemoryMB();

        // 1. 直接型号匹配
        var match = Budgets.FirstOrDefault(b => b.Keys.Any(k => k == machine));
        if (match != null) return match.CrashLimitMB;

        // 2. 营销名(粗略)匹配：用 hw.machine 前缀映射简单猜测系列（只要 Keys 里包含 product family 关键字）
        //    例如未能列出的未来型号仍可能包含 "iphone" / "ipad"
        if (!string.IsNullOrWhiteSpace(machine))
        {
            var byContains = Budgets.FirstOrDefault(b => b.Keys.Any(k => machine.Contains(k)));
            if (byContains != null) return byContains.CrashLimitMB;
        }

        // 3. 物理内存 ±500MB 搜索
        if (physical > 0)
        {
            var close = Budgets
                .Where(b => Math.Abs(b.MemoryCapacityMB - physical) <= 500)
                .ToList();
            if (close.Count > 0)
                return close.Min(b => b.CrashLimitMB);
        }

        // 4. 回退：取物理内存 50% (若可得)；限制 <=4096
        if (physical > 0)
        {
            var half = (int)(physical * 0.5);
            return Math.Min(4096, Math.Max(256, half));
        }

        // 5. 最终兜底：2GB *50% = 1024 (不少于 256)
        return 1024;
    }
}