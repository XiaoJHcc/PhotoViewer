namespace PhotoViewer.Core.Memory;

/// <summary>
/// 平台注入的内存预算来源。预算/可用/占用三个数值必须同口径:预算 = 本进程在被系统回收/杀死前可用的总量,
/// 可用 = 距该上限的余量。iOS 用 os_proc_available_memory + phys_footprint;Android 用系统水位;
/// 桌面用物理内存比例(Windows 见 WindowsMemoryBudgetProvider,macOS 见 MacMemoryBudgetProvider)。
/// </summary>
public interface IMemoryBudgetProvider
{
    /// <summary>
    /// 操作系统物理内存总量与当前可用量,只供设置页设备能力提示展示;
    /// 不参与预算、准入与缓存容量推导(那些仍只取下面三个数值)。未实现时默认未知。
    /// </summary>
    SystemMemoryInfo GetSystemMemoryInfo() => SystemMemoryInfo.Unknown;

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

    /// <summary>
    /// 当前进程占用(与预算同口径的驻留量),供缓存把"非缓存占用"从预算里扣掉。
    /// </summary>
    /// <returns>占用字节数;口径不适用或不可得时返回 0(缓存只按用户上限推导,不按预算)。</returns>
    long GetFootprintBytes();
}
