namespace PhotoViewer.Core.Memory;

/// <summary>
/// 纳入 <see cref="MemoryGovernor"/> 统一管控的内存客户端(缓存/缓冲池/推理会话等)。
/// 实现必须线程安全;<see cref="Trim"/> 不得阻塞(可能在探测线程上被调用)。
/// </summary>
public interface IMemoryClient
{
    /// <summary>客户端名称(压力快照日志中的标识)。</summary>
    string Name { get; }

    /// <summary>当前占用的字节数估算。</summary>
    long FootprintBytes { get; }

    /// <summary>
    /// 按压力级别修剪自身占用。
    /// </summary>
    /// <param name="level">当前压力级别(至少为 Caution,不会在 Normal 时调用)。</param>
    void Trim(PressureLevel level);
}
