namespace PhotoViewer.Core.Memory;

/// <summary>
/// 平台注入的内存预算来源。两个数值必须同口径:预算 = 本进程在被系统回收/杀死前可用的总量,
/// 可用 = 距该上限的余量。iOS 用 os_proc_available_memory + phys_footprint;Android 用系统水位;
/// 桌面用 CLR 可见总量的比例。
/// </summary>
public interface IMemoryBudgetProvider
{
    /// <summary>
    /// 本进程可用内存预算(字节)。
    /// </summary>
    /// <returns>预算字节数;不可得时返回 0(Governor 退化为恒 Normal)。</returns>
    long GetBudgetBytes();

    /// <summary>
    /// 当前距预算上限的剩余字节数(本进程还能安全分配的量)。
    /// </summary>
    /// <returns>可用字节数;不可得时返回 <see cref="long.MaxValue"/>(视为无压力)。</returns>
    long GetAvailableBytes();
}
