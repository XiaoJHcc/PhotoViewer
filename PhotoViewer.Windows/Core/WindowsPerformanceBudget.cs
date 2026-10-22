using System;
using PhotoViewer.Core.Image;
using PhotoViewer.Core.Platform;

namespace PhotoViewer.Windows.Core;

/// <summary>
/// Windows 预算源：设备能力沿用 <see cref="DefaultPerformanceBudget"/>（物理内存、核数、线程上限），
/// 设置页的默认值按本平台的实测口径给出，不再用跨平台的旧口径。
/// </summary>
/// <remarks>
/// 口径依据（见 <see cref="WindowsMemoryBudgetProvider"/>）：
/// <list type="bullet">
/// <item>预算 = 物理内存 × 50%，足迹 = 进程私有提交，所以缓存默认值取物理内存的 1/4 ——
/// 正好是预算的一半，落在推导上限（预算 × 55% − 非缓存占用）之内，不因默认值就自我截断。</item>
/// <item>缓存张数按一张照片 2400 万像素 × 展示编码每像素字节（线性 RGBA16F = 8 B）估算，
/// 与设置页提示行的口径一致；张数只作参考上限，实际淘汰按字节容量与保留排名。</item>
/// <item>后台解码并行度：每个 HEIF 解码内部已用满瓦片线程，实测 20 线程机器上 6 与 8 的 p95 同级、
/// 峰值私有内存低 15%–20%，所以按核数 / 3 收敛到 2–6，并保证每 1 GB 缓存内存至少 1 路。</item>
/// </list>
/// CPU 解码并行度不在此覆盖：它只门控 libheif 回落路径，沿用跨平台默认口径。
/// </remarks>
internal sealed class WindowsPerformanceBudget : IPerformanceBudget
{
    /// <summary>缓存内存上限的推荐下限（MB）。</summary>
    private const int MinCacheMemoryMB = 1024;

    /// <summary>缓存内存上限的推荐上限（MB）。</summary>
    private const int MaxCacheMemoryMB = 8192;

    /// <summary>缓存内存上限占物理内存的比例：预算的一半。</summary>
    private const int CacheMemoryDivisor = 4;

    /// <summary>单张照片按 2400 万像素估算（与设置页提示同一封顶口径）。</summary>
    private const long EstimatedPhotoPixelCap = 24_000_000L;

    /// <summary>缓存张数的推荐下限（张）：少于 3 张时预载与对比无法成立。</summary>
    private const int MinCacheCount = 3;

    /// <summary>缓存张数的推荐上限（张）。</summary>
    private const int MaxCacheCount = 30;

    /// <summary>后台解码并行度的推荐区间与核数分母。</summary>
    private const int MinPreloadParallelism = 2;
    private const int MaxPreloadParallelism = 6;
    private const int PreloadCoreDivisor = 3;

    /// <summary>每 GB 缓存内存至少保底的并行路数。</summary>
    private const int PreloadMemoryUnitMB = 1024;

    private readonly DefaultPerformanceBudget _platform = new();

    /// <inheritdoc/>
    public int GetAppMemoryLimitMB() => _platform.GetAppMemoryLimitMB();

    /// <inheritdoc/>
    public int GetCpuCoreCount() => _platform.GetCpuCoreCount();

    /// <inheritdoc/>
    public int GetNativePreloadThreadLimit() => _platform.GetNativePreloadThreadLimit();

    /// <inheritdoc/>
    public int GetCpuPreloadThreadLimit() => _platform.GetCpuPreloadThreadLimit();

    /// <inheritdoc/>
    public int GetRecommendedCacheMemoryMB()
        => Math.Clamp(GetAppMemoryLimitMB() / CacheMemoryDivisor, MinCacheMemoryMB, MaxCacheMemoryMB);

    /// <inheritdoc/>
    public int GetRecommendedCacheCount(int cacheMemoryMB)
    {
        long perPhotoBytes = EstimatedPhotoPixelCap * BitmapLoader.ProxyBytesPerPixel;
        int perPhotoMB = (int)Math.Max(1, (perPhotoBytes + (1024 * 1024) - 1) / (1024 * 1024));
        return Math.Clamp(cacheMemoryMB / perPhotoMB, MinCacheCount, MaxCacheCount);
    }

    /// <inheritdoc/>
    public int GetRecommendedNativePreloadParallelism(int cacheMemoryMB)
    {
        int byCores = Math.Clamp(
            GetCpuCoreCount() / PreloadCoreDivisor, MinPreloadParallelism, MaxPreloadParallelism);
        int byMemory = Math.Max(1, cacheMemoryMB / PreloadMemoryUnitMB);
        return Math.Min(byCores, byMemory);
    }
}
