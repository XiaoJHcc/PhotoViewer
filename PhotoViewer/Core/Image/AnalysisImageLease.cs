using System;
using System.Threading;
using PhotoViewer.Core.Memory;
using AdmissionPermit = PhotoViewer.Core.Memory.MemoryGovernor.AdmissionPermit;

namespace PhotoViewer.Core.Image;

/// <summary>
/// 分析取图的产品租约：一张短命分析图（全分辨率 sRGB8 或推理缩略图，显示朝向、不进展示缓存）
/// 加上整幅分析的准入许可（工作集预留在整个推理期间保持）。
/// <see cref="Dispose"/> 一次归还两者；CV/DINO 计算必须在此之前完成。
/// </summary>
public sealed class AnalysisImageLease : IDisposable
{
    private readonly AnalysisAdmission? _admission;
    private int _disposed;

    internal AnalysisImageLease(DecodedImage image, AnalysisAdmission? admission)
    {
        Image = image;
        _admission = admission;
    }

    /// <summary>分析产物（sRGB8、已转正显示朝向）。释放后像素缓冲已归还池，不得再读取。</summary>
    public DecodedImage Image { get; }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        Image.Release();
        _admission?.Dispose();
    }
}

/// <summary>
/// 一份分析工作集的准入许可：在途账记在 <see cref="MemoryGovernor"/>，同时计入"本进程分析 root"，
/// 其他分析取图可以等它释放（背压）。实际尺寸超出估算时按差额申请扩充，扩充失败即按拒绝处理。
/// </summary>
internal sealed class AnalysisAdmission : IDisposable
{
    private AdmissionPermit? _permit;
    private long _bytes;
    private int _disposed;

    private AnalysisAdmission(AdmissionPermit permit, long bytes)
    {
        _permit = permit;
        _bytes = bytes;
    }

    /// <summary>申请一份分析工作集许可；被拒返回 null（拒绝原因已记到当前操作作用域）。</summary>
    internal static AnalysisAdmission? TryReserve(long bytes)
    {
        long request = Math.Max(1, bytes);
        if (!MemoryGovernor.TryAcquire(request, DecodePriority.P3_Background, out var permit) || permit == null)
            return null;
        BatchAnalysisContext.RegisterAnalysisRoot();
        return new AnalysisAdmission(permit, request);
    }

    /// <summary>已预留字节数。</summary>
    internal long ReservedBytes => Interlocked.Read(ref _bytes);

    /// <summary>按差额扩充许可（实际源尺寸大于估算时用）；被拒返回 false。</summary>
    internal bool TryGrow(long extraBytes)
    {
        if (extraBytes <= 0) return true;
        if (Volatile.Read(ref _disposed) != 0) return false;
        var permit = _permit;
        if (permit == null || !MemoryGovernor.TryGrowAnalysisPermit(permit, extraBytes)) return false;
        Interlocked.Add(ref _bytes, extraBytes);
        return true;
    }

    /// <summary>按实际产物字节复核账本（须先经 <see cref="TryGrow"/> 保证实际量不超预留）。</summary>
    internal void SetActual(long bytes)
    {
        if (bytes <= 0 || Volatile.Read(ref _disposed) != 0) return;
        Interlocked.Exchange(ref _bytes, bytes);
        _permit?.SetActual(bytes);
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        Interlocked.Exchange(ref _permit, null)?.Dispose();
        BatchAnalysisContext.ReleaseAnalysisRoot();
    }
}
