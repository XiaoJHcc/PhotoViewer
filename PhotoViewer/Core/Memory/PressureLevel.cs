namespace PhotoViewer.Core.Memory;

/// <summary>
/// 内存压力级别。阈值语义见 <see cref="MemoryGovernor"/>;各级响应矩阵见 master plan §4.3。
/// </summary>
public enum PressureLevel
{
    /// <summary>正常。</summary>
    Normal,

    /// <summary>注意:可用内存低于预算 40%。停 P2/P3 预取、缩略图收窄、批量索引暂停。</summary>
    Caution,

    /// <summary>警告:低于 25% 或收到 OS 告警。主缓存收窄、分析/增强清空、ONNX 卸载、解码并发降 1。</summary>
    Warning,

    /// <summary>危急:低于 15% 或连续告警。只保留当前纹理,一切后台工作取消。</summary>
    Critical,
}
