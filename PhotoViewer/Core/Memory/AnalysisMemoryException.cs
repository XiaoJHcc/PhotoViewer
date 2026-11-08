using System;

namespace PhotoViewer.Core.Memory;

/// <summary>
/// 分析批次的准入拒批：内存不足使分析没能拿到工作集许可。
/// 与解码失败区分开——这是"内存受限、应稍后或降并发再试"，不是照片损坏或格式不支持。
/// </summary>
public sealed class AnalysisMemoryException : Exception
{
    /// <summary>用短原因与被拒字节数构造（消息即可直接展示给用户）。</summary>
    /// <param name="reason">用户可读的短原因。</param>
    /// <param name="bytes">本次分析尝试的工作集字节数。</param>
    /// <param name="inner">原始异常（如解码器内部的分配失败），仅作诊断。</param>
    public AnalysisMemoryException(string reason, long bytes, Exception? inner = null)
        : base($"{reason}（需要约 {ToMegabytes(bytes)}MB）", inner)
    {
        Reason = reason;
        Bytes = bytes;
    }

    /// <summary>用户可读的短原因（如"系统可用内存不足"）。</summary>
    public string Reason { get; }

    /// <summary>本次分析尝试的工作集字节数。</summary>
    public long Bytes { get; }

    private static long ToMegabytes(long bytes) => Math.Max(1, (bytes + (1 << 20) - 1) >> 20);
}
