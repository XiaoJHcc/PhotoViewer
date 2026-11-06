using System.Globalization;
using PhotoViewer.Core.Memory;

namespace PhotoViewer.Core.Image;

/// <summary>设备缓存能力提示的语义级别（设置页据此取语义色）。</summary>
public enum CacheCapabilityLevel
{
    Normal,
    Warning,
    Error,
}

/// <summary>
/// 设备缓存能力估算的纯计算结果。未知哨兵：总内存与估算容量 0、可用内存 -1、张数 null；
/// 容量已知但不足一张时张数为 0。
/// </summary>
public readonly record struct DeviceCacheEstimate(
    long TotalBytes,
    long AvailableBytes,
    long CapacityBytes,
    long? ScreenPhotoCount,
    long? FullPhotoCount);

/// <summary>用户缓存内存上限的超限判定：级别 + 简短原因文本（未超限为 Normal 级别与 null）。</summary>
public readonly record struct CacheLimitWarning(CacheCapabilityLevel Level, string? Reason)
{
    public static CacheLimitWarning Normal => new(CacheCapabilityLevel.Normal, null);
}

/// <summary>
/// 设备缓存能力提示的唯一计算出口。估算容量由调用方给出：可编辑缓存上限的平台取用户设置值，
/// 自动管理缓存的平台取 <see cref="DeviceSafeCapacity"/>；两种口径都不扣进程占用、不套 256MB 下限、
/// 不按 400 条截断，也不看缓存数量、解码线程与预载设置。两档各按 Normal 压力的展示编码字节数换算，
/// 只表示全部容量用于该档时的张数，不相加。超限判定的设备阈值始终是设备最大安全缓存容量。
/// </summary>
public static class DeviceCacheCapability
{
    public const long FullPhotoPixels = 33_000_000;

    public const int SafetyMarginPercent = 5;

    private const string TotalMemoryWarning = "（当前设置超过系统总内存上限）";
    private const string BudgetWarning = "（当前设置超过应用内存上限）";
    private const string AvailableWarning = "（当前设置超过系统可用内存上限）";
    private const string DeviceCapacityWarning = "（当前设置超过设备缓存上限）";

    // 33MP 按 3:2 折算所需长边（≈7036）：达到它直接取封顶值，也避免 int 长边平方后溢出
    private static readonly int CapLongEdge = (int)Math.Ceiling(Math.Sqrt(FullPhotoPixels * 3.0 / 2));

    public static double SafeRatio(double cautionEnterPercent)
        => Math.Clamp(1 - (cautionEnterPercent + SafetyMarginPercent) / 100, 0, 1);

    /// <summary>设备最大安全缓存容量 = 平台应用内存预算 × 安全比例；预算未知返回 null。</summary>
    public static long? DeviceSafeCapacity(long budgetBytes, double cautionEnterPercent)
        => budgetBytes > 0 ? (long)(budgetBytes * SafeRatio(cautionEnterPercent)) : null;

    public static long ScreenPhotoPixels(int proxyLongEdge)
    {
        if (proxyLongEdge <= 0) return 0;
        if (proxyLongEdge >= CapLongEdge) return FullPhotoPixels;
        return Math.Min((long)proxyLongEdge * proxyLongEdge * 2 / 3, FullPhotoPixels);
    }

    public static long? PhotoCount(long? capacityBytes, long pixels, int bytesPerPixel)
    {
        if (capacityBytes is not { } capacity || capacity < 0 || pixels <= 0 || bytesPerPixel <= 0) return null;
        return capacity / (pixels * bytesPerPixel);
    }

    public static DeviceCacheEstimate Compute(
        SystemMemoryInfo system,
        long? capacityBytes,
        int proxyLongEdge,
        int screenBytesPerPixel,
        int fullBytesPerPixel)
    {
        return new DeviceCacheEstimate(
            system.TotalBytes,
            system.AvailableBytes,
            capacityBytes ?? 0,
            PhotoCount(capacityBytes, ScreenPhotoPixels(proxyLongEdge), screenBytesPerPixel),
            PhotoCount(capacityBytes, FullPhotoPixels, fullBytesPerPixel));
    }

    /// <summary>
    /// 用户缓存内存上限的超限判定。设备阈值取设备最大安全缓存容量（不是按用户上限算出的估算容量，
    /// 否则永远与自身相等而不会警告）；未知读数不参与，等于阈值不算超过。
    /// </summary>
    public static CacheLimitWarning EvaluateLimit(
        long userLimitBytes,
        long totalBytes,
        long availableBytes,
        long budgetBytes,
        long deviceSafeCapacityBytes)
    {
        if (userLimitBytes <= 0) return CacheLimitWarning.Normal;

        if (totalBytes > 0 && userLimitBytes > totalBytes)
            return new(CacheCapabilityLevel.Error, TotalMemoryWarning);

        if (budgetBytes > 0 && userLimitBytes > budgetBytes)
            return new(CacheCapabilityLevel.Error, BudgetWarning);

        if (availableBytes >= 0 && userLimitBytes > availableBytes)
            return new(CacheCapabilityLevel.Warning, AvailableWarning);

        if (deviceSafeCapacityBytes > 0 && userLimitBytes > deviceSafeCapacityBytes)
            return new(CacheCapabilityLevel.Warning, DeviceCapacityWarning);

        return CacheLimitWarning.Normal;
    }

    public static string DescribeSystemMemory(DeviceCacheEstimate estimate)
        => $"系统内存: 总 {DescribeTotalBytes(estimate.TotalBytes)} / 当前可用 {DescribeAvailableBytes(estimate.AvailableBytes)}";

    public static string DescribeCapacity(DeviceCacheEstimate estimate)
    {
        if (estimate is { ScreenPhotoCount: { } screen, FullPhotoCount: { } full })
            return $"可缓存 {screen} 张适屏照片 / {full} 张 3300 万像素照片";

        var screenText = estimate.ScreenPhotoCount is { } s ? $"{s} 张适屏照片" : "适屏照片：未知";
        var fullText = estimate.FullPhotoCount is { } f ? $"{f} 张 3300 万像素照片" : "3300 万像素照片：未知";
        return $"{screenText} / {fullText}";
    }

    public static string DescribeTotalBytes(long totalBytes) => totalBytes > 0 ? FormatBytes(totalBytes) : "未知";

    public static string DescribeAvailableBytes(long availableBytes) => availableBytes >= 0 ? FormatBytes(availableBytes) : "未知";

    public static string FormatBytes(long bytes)
    {
        const long gib = 1024L * 1024 * 1024;
        if (bytes >= gib)
            return (bytes / (double)gib).ToString("0.#", CultureInfo.InvariantCulture) + " GB";
        return (bytes / (1024L * 1024)).ToString(CultureInfo.InvariantCulture) + " MB";
    }
}
