using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MetadataExtractor;
using PhotoViewer.Core.Memory;
using PhotoViewer.Core.Platform;
using PhotoViewer.Core.Storage;

namespace PhotoViewer.Core.Image;

/// <summary>
/// 缓存项
/// </summary>
public class BitmapCacheItem
{
    /// <summary>已解码图像。</summary>
    public DecodedImage Image { get; set; }
    public long Size { get; set; }
    public DateTime LastAccessTime { get; set; }
    public string FilePath { get; set; }

    /// <summary>尺寸档(缓存键的一维)。</summary>
    public ImageSizeClass SizeClass { get; }

    public BitmapCacheItem(DecodedImage image, string filePath, ImageSizeClass sizeClass = ImageSizeClass.Full)
    {
        Image = image;
        FilePath = filePath;
        SizeClass = sizeClass;
        Size = EstimateImageSize(image);
        LastAccessTime = DateTime.Now;
    }

    private static long EstimateImageSize(DecodedImage image)
    {
        return image.ByteCount;
    }

    public void UpdateAccessTime()
    {
        LastAccessTime = DateTime.Now;
    }
}

/// <summary>
/// 图片缓存服务（静态类）。
/// 缓存键 = (路径, 尺寸档):缩略图/代理图/全分辨率同驻统一 LRU,
/// 缩略图分区有独立张数/字节上限,淘汰保留排名按路径对所有档生效。主分区上限由实测预算推导
/// (<see cref="EffectiveCacheCapacityBytes"/>),排名外的条目先走,当前图不因容量被淘汰。
/// 每档至多一条条目：**像素编码不进缓存键**——同一 (路径, 尺寸档) 只保留最新一次解码产物，
/// 编码变化按"长边足够且保真度不低于需求"(<see cref="Reusable"/>)复用，否则原地替换，
/// 因此不会出现同档多份编码副本，也不会因压力升降泄漏内存。
/// </summary>
public static class BitmapLoader
{
    private static IPresentationImageDecoder? _presentationDecoder;
    /// <summary>
    /// 邻居预取是否允许 ±1 档取 Full（默认 true，Mac/iPad/Windows 口径）。
    /// 实际还要看当前展示是否需要 Full（<see cref="PrefetchFullWanted"/>）：适应态只预取代理，
    /// 放大/分析栏可见时才为 ±1 邻居解 Full。Android 手机内存档位更低，预取统一走代理档。
    /// </summary>
    public static bool PrefetchFullNeighbors { get; set; } = true;

    /// <summary>
    /// 当前展示是否需要全分辨率（由 <see cref="PhotoLoadSession.UpdateViewport"/> 按放大/分析栏可见写入）。
    /// 适应态为 false 时预取不为 ±1 邻居解 Full，避免连翻时白解整幅。
    /// </summary>
    public static bool PrefetchFullWanted { get; set; }
    private static IPresentationEncodingPolicy? _encodingPolicy;

    /// <summary>
    /// 当前展示编码（按 Full 档在当下压力下协商）的每像素字节数，用于准入估算与设置页预算提示；
    /// 旧壳（未注入展示解码器）保留原透明度口径。
    /// </summary>
    public static int PresentationBytesPerPixel => _presentationDecoder == null
        ? (IgnoreAlpha ? 3 : 4)
        : DecodedImage.BytesPerPixel(PresentationEncoding(ImageSizeClass.Full));

    /// <summary>
    /// 代理档在 Normal 压力下的展示编码每像素字节数，用于设置页按适屏尺寸估算可缓存张数；
    /// 旧壳（未注入展示解码器）保留原透明度口径。
    /// </summary>
    public static int ProxyBytesPerPixel => _presentationDecoder == null
        ? (IgnoreAlpha ? 3 : 4)
        : DecodedImage.BytesPerPixel(
            (_encodingPolicy ?? throw new InvalidOperationException("展示编码策略未注入"))
                .Select(ImageSizeClass.Proxy, PressureLevel.Normal));

    /// <summary>启动期注入原生主图解码器与编码协商策略；禁止在已开始解码后更换解释口径。</summary>
    /// <param name="decoder">平台展示解码器。</param>
    /// <param name="policy">展示编码协商策略。</param>
    public static void InitializePresentationDecoder(IPresentationImageDecoder decoder, IPresentationEncodingPolicy policy)
    {
        if (!_cache.IsEmpty || !_inFlight.IsEmpty)
            throw new InvalidOperationException("主图解码器必须在加载前注入");
        _presentationDecoder = decoder ?? throw new ArgumentNullException(nameof(decoder));
        _encodingPolicy = policy ?? throw new ArgumentNullException(nameof(policy));
        ThumbnailService.InitializeMissingPreviewDecoder(decoder);
    }

    /// <summary>
    /// 注入的展示解码器若支持低分辨率顶替预览（STATE-01）则返回该能力，否则为 null。
    /// 未注入解码器或平台不实现时为 null，加载会话保持黑屏加载，行为与引入顶替前一致。
    /// </summary>
    public static IQuickPreviewDecoder? QuickPreviewDecoder => _presentationDecoder as IQuickPreviewDecoder;

    /// <summary>
    /// 本次展示解码应产出的编码：按尺寸档与当前压力向注入的策略协商（平台无关，Core 不含平台判断）。
    /// </summary>
    /// <param name="sizeClass">尺寸档。</param>
    /// <returns>编码策略给出的编码。</returns>
    private static PixelEncoding PresentationEncoding(ImageSizeClass sizeClass)
        => (_encodingPolicy ?? throw new InvalidOperationException("展示编码策略未注入")).Select(sizeClass, MemoryGovernor.CurrentLevel);

    /// <summary>
    /// 已有产物是否足以满足本次请求：长边不小于需求且编码保真度不低于需求（F16 可覆盖 8 位请求，反之不可）。
    /// </summary>
    /// <param name="candidate">缓存中的产物。</param>
    /// <param name="requiredLongEdge">本次请求的长边上限（0 表示全分辨率请求）。</param>
    /// <param name="requiredEncoding">本次请求的编码。</param>
    private static bool Reusable(DecodedImage candidate, int requiredLongEdge, PixelEncoding requiredEncoding)
        => (requiredLongEdge <= 0
                || Math.Max(candidate.Width, candidate.Height) >= requiredLongEdge
                // 解码器不放大：已是源分辨率的产物即满足任何代理目标，否则小图每次切图都要重解。
                || candidate.SourceWidth > 0 && candidate.SourceHeight > 0
                    && Math.Max(candidate.Width, candidate.Height) >= Math.Max(candidate.SourceWidth, candidate.SourceHeight)
                    && Math.Min(candidate.Width, candidate.Height) >= Math.Min(candidate.SourceWidth, candidate.SourceHeight))
            && FidelityAtLeast(candidate.Encoding, requiredEncoding);

    /// <summary>
    /// 编码保真度是否达标：解码器不放大，产物长边由源决定，故**不复核长边**——只复核编码，
    /// 免得源本身小于代理目标时把正确产物误判为不合格并反复重解码。
    /// </summary>
    /// <param name="actual">产物编码。</param>
    /// <param name="required">本次请求的编码。</param>
    private static bool FidelityAtLeast(PixelEncoding actual, PixelEncoding required)
        => DecodedImage.PresentationFidelity(actual) >= DecodedImage.PresentationFidelity(required);

    /// <summary>
    /// 新产物是否应当取代缓存中的旧产物：长边更大，或长边相同而编码保真度更高。
    /// 任一项都不占优时保留旧产物（含"旧产物保真度更高"的情形），避免压力反复时来回重解码。
    /// </summary>
    /// <param name="candidate">刚解码出的产物。</param>
    /// <param name="existing">缓存中同档的旧产物。</param>
    private static bool Supersedes(DecodedImage candidate, DecodedImage existing)
    {
        int candidateLongEdge = Math.Max(candidate.Width, candidate.Height);
        int existingLongEdge = Math.Max(existing.Width, existing.Height);
        if (candidateLongEdge != existingLongEdge) return candidateLongEdge > existingLongEdge;
        return DecodedImage.PresentationFidelity(candidate.Encoding) > DecodedImage.PresentationFidelity(existing.Encoding);
    }

    /// <summary>统一缓存键:路径(忽略大小写)+ 尺寸档。</summary>
    private readonly struct BitmapCacheKey : IEquatable<BitmapCacheKey>
    {
        public readonly string Path;
        public readonly ImageSizeClass Class;
        public BitmapCacheKey(string path, ImageSizeClass cls) { Path = path; Class = cls; }
        public bool Equals(BitmapCacheKey other) => Class == other.Class && string.Equals(Path, other.Path, StringComparison.OrdinalIgnoreCase);
        public override bool Equals(object? obj) => obj is BitmapCacheKey other && Equals(other);
        public override int GetHashCode() => HashCode.Combine(StringComparer.OrdinalIgnoreCase.GetHashCode(Path), (int)Class);
    }

    private static readonly ConcurrentDictionary<BitmapCacheKey, BitmapCacheItem> _cache = new();
    // 在途解码去重：同路径请求共享同一任务，完成后移除
    private static readonly ConcurrentDictionary<string, InFlightDecode> _inFlight = new();
    private static readonly object _cleanupLock = new();

    // 淘汰保留排名：越靠前的路径越重要（0 = 当前图）。淘汰选择时先淘汰未入排名的 LRU，
    // 再按排名从最不重要到最重要淘汰；volatile 整体替换，读侧无锁。
    private static volatile string[] _retentionRanking = [];
    private static volatile Dictionary<string, int> _retentionRank = new(StringComparer.OrdinalIgnoreCase);

    // 新增：忽略透明度（默认 false）
    public static bool IgnoreAlpha { get; set; } = false;

    // 缓存配置(主分区:Full/Proxy)
    private static int _maxCacheCount = 30;
    private static long _maxCacheSize = 2048L * 1024 * 1024; // 2048 MB

    // 缩略图分区配置(Thumb):张数 + 字节双限
    private static int _thumbMaxCount = 2000;
    private static long _thumbMaxBytes = 256L * 1024 * 1024; // 256 MB

    // 有效主分区上限(由实测预算推导,每轮 MemoryGovernor 探测后重算)
    private const long MinimumCacheBytes = 256L * 1024 * 1024;
    private const int AutomaticCacheCount = 400;
    private static long _effectiveCapacityBytes = _maxCacheSize;
    private static int _effectiveCacheCount = _maxCacheCount;

    // 缓存状态变化事件:(路径, 尺寸档, 是否在缓存中)
    public static event Action<string, ImageSizeClass, bool>? CacheStatusChanged;

    /// <summary>
    /// 最大缓存图片数量（自动口径下不生效，见 <see cref="EffectiveCacheCount"/>）
    /// </summary>
    public static int MaxCacheCount
    {
        get => _maxCacheCount;
        set
        {
            _maxCacheCount = Math.Max(1, value);
            RecomputeEffectiveCapacity();
            _ = Task.Run(CleanupCache);
        }
    }
    
    /// <summary>
    /// 最大缓存大小（字节；自动口径下不生效，见 <see cref="EffectiveCacheCapacityBytes"/>）
    /// </summary>
    public static long MaxCacheSize
    {
        get => _maxCacheSize;
        set
        {
            _maxCacheSize = Math.Max(MinimumCacheBytes, value); // 最小256MB
            RecomputeEffectiveCapacity();
            _ = Task.Run(CleanupCache);
        }
    }
    
    /// <summary>
    /// 当前缓存数量(全部分区合计)
    /// </summary>
    public static int CurrentCacheCount => _cache.Count;

    /// <summary>
    /// 当前缓存大小（字节,全部分区合计）
    /// </summary>
    public static long CurrentCacheSize => _cache.Values.Sum(item => item.Size);

    /// <summary>主分区(Full/Proxy)当前张数。</summary>
    private static int FullCacheCount => _cache.Count(kv => kv.Value.SizeClass != ImageSizeClass.Thumb);

    /// <summary>主分区(Full/Proxy)当前字节数。</summary>
    private static long FullCacheSize => _cache.Where(kv => kv.Value.SizeClass != ImageSizeClass.Thumb).Sum(kv => kv.Value.Size);

    /// <summary>缩略图分区当前张数。</summary>
    private static int ThumbCacheCount => _cache.Count(kv => kv.Value.SizeClass == ImageSizeClass.Thumb);

    /// <summary>缩略图分区当前字节数。</summary>
    private static long ThumbCacheSize => _cache.Where(kv => kv.Value.SizeClass == ImageSizeClass.Thumb).Sum(kv => kv.Value.Size);

    /// <summary>
    /// 检查文件是否在缓存中
    /// </summary>
    public static bool IsInCache(string filePath, ImageSizeClass sizeClass = ImageSizeClass.Full)
    {
        return _cache.ContainsKey(new BitmapCacheKey(filePath, sizeClass));
    }

    /// <summary>
    /// 仅查缓存(不触发解码);命中更新 LRU 访问时间。
    /// </summary>
    public static DecodedImage? TryGetCached(string filePath, ImageSizeClass sizeClass)
    {
        if (_cache.TryGetValue(new BitmapCacheKey(filePath, sizeClass), out var item))
        {
            item.UpdateAccessTime();
            return item.Image;
        }
        return null;
    }

    /// <summary>
    /// 只查缓存的同步取图：按 <see cref="GetBitmapAsync(IFileRef, DecodeSizeHint, DecodePriority, CancellationToken)"/>
    /// 的复用规则（代理档含跨档复用 Full）命中即携调用方租约返回；未命中返回 null，不解码、不排队、不并入在途任务。
    /// </summary>
    /// <param name="file">文件引用。</param>
    /// <param name="hint">尺寸档提示。</param>
    /// <returns>命中的展示产物（用完 <see cref="DecodedImage.Release"/>），未命中为 null。</returns>
    public static DecodedImage? TryAcquireCached(IFileRef file, DecodeSizeHint hint)
    {
        var filePath = file.PathKey;
        var fullKey = new BitmapCacheKey(filePath, ImageSizeClass.Full);
        DecodedImage? image;
        if (hint.Class == ImageSizeClass.Proxy && hint.MaxLongEdge > 0)
        {
            var encoding = PresentationEncoding(ImageSizeClass.Proxy);
            image = TryAcquireReusable(new BitmapCacheKey(filePath, ImageSizeClass.Proxy), hint.MaxLongEdge, encoding)
                ?? TryAcquireReusable(fullKey, hint.MaxLongEdge, encoding);
        }
        else if (_cache.TryGetValue(fullKey, out var fullItem) && fullItem.Image.TryAcquire())
        {
            fullItem.UpdateAccessTime();
            image = fullItem.Image;
        }
        else image = null;
        if (image != null) PerfTrace.RecordDecode(file.Name, true, 0, 0, image.Width, image.Height);
        return image;
    }

    /// <summary>缓存条目满足长边与保真度需求时携租约取出并刷新访问时间，否则返回 null。</summary>
    private static DecodedImage? TryAcquireReusable(BitmapCacheKey key, int requiredLongEdge, PixelEncoding requiredEncoding)
    {
        if (!_cache.TryGetValue(key, out var item) || !Reusable(item.Image, requiredLongEdge, requiredEncoding) || !item.Image.TryAcquire())
            return null;
        item.UpdateAccessTime();
        return item.Image;
    }

    /// <summary>
    /// 写入缩略图分区(统一 LRU 的 Thumb 档);超出分区上限时按淘汰序列修剪非保护项。
    /// </summary>
    public static void StoreThumbnail(string filePath, DecodedImage image)
    {
        var key = new BitmapCacheKey(filePath, ImageSizeClass.Thumb);
        if (_cache.TryRemove(key, out var old))
            old.Image.Dispose();
        _cache[key] = new BitmapCacheItem(image, filePath, ImageSizeClass.Thumb);
        UiDispatcher.Post(() => CacheStatusChanged?.Invoke(filePath, ImageSizeClass.Thumb, true));
        EvictThumbsToFit();
    }

    /// <summary>
    /// 移除指定路径某一档的缓存项(不存在则空操作)。
    /// </summary>
    public static void RemoveCached(string filePath, ImageSizeClass sizeClass)
    {
        var key = new BitmapCacheKey(filePath, sizeClass);
        if (_cache.TryRemove(key, out var item))
        {
            UiDispatcher.Post(() => CacheStatusChanged?.Invoke(filePath, sizeClass, false));
            item.Image.Release();
        }
    }

    /// <summary>
    /// 清空缩略图分区(文件夹切换时整批释放,不依赖 LRU 淘汰与 finalizer)。
    /// </summary>
    public static void ClearThumbnailClass()
    {
        EvictRange(_cache.ToList().Where(kv => kv.Key.Class == ImageSizeClass.Thumb));
    }

    /// <summary>缩略图分区张数上限。</summary>
    public static int ThumbMaxCount
    {
        get => _thumbMaxCount;
        set { _thumbMaxCount = Math.Max(32, value); EvictThumbsToFit(); }
    }

    /// <summary>缩略图分区字节上限。</summary>
    public static long ThumbMaxBytes
    {
        get => _thumbMaxBytes;
        set { _thumbMaxBytes = Math.Max(16L * 1024 * 1024, value); EvictThumbsToFit(); }
    }

    /// <summary>缩略图分区超限修剪:非保护项 LRU 在前,保护项垫底。</summary>
    private static void EvictThumbsToFit()
    {
        List<KeyValuePair<BitmapCacheKey, BitmapCacheItem>>? toRemove = null;
        int count = ThumbCacheCount;
        long bytes = ThumbCacheSize;
        if (count <= _thumbMaxCount && bytes <= _thumbMaxBytes) return;

        foreach (var kv in OrderForEviction(thumbScope: true))
        {
            if (count <= _thumbMaxCount && bytes <= _thumbMaxBytes) break;
            if (_cache.TryRemove(kv.Key, out var item))
            {
                (toRemove ??= new()).Add(new KeyValuePair<BitmapCacheKey, BitmapCacheItem>(kv.Key, item));
                count--; bytes -= item.Size;
            }
        }
        if (toRemove == null) return;
        UiDispatcher.Post(() =>
        {
            foreach (var kv in toRemove)
                CacheStatusChanged?.Invoke(kv.Key.Path, ImageSizeClass.Thumb, false);
        });
        foreach (var kv in toRemove)
            kv.Value.Image.Release();
    }

    /// <summary>
    /// 设置淘汰保留排名（最重要在前）：当前图、上一张浏览过的图、翻页方向 ±1、其余邻居、其余最近浏览。
    /// 所有淘汰路径（EnsureCapacity / Cleanup / LruRemoveToSize / 压力修剪）统一按该顺序决定先淘汰谁：
    /// 未入排名的按 LRU 先走，排名内从最不重要到最重要；只有当前图（排名 0）不会被容量淘汰动到。
    /// </summary>
    /// <param name="paths">按重要性排列的路径（下标越小越重要，0 = 当前图）。</param>
    public static void SetRetentionRanking(IReadOnlyList<string> paths)
    {
        var rank = new Dictionary<string, int>(paths.Count, StringComparer.OrdinalIgnoreCase);
        var ordered = new string[paths.Count];
        int index = 0;
        foreach (var path in paths)
        {
            if (string.IsNullOrEmpty(path)) continue;
            ordered[index] = path;
            rank.TryAdd(path, index);
            index++;
        }
        if (index != ordered.Length) Array.Resize(ref ordered, index);
        _retentionRank = rank;
        _retentionRanking = ordered;
    }

    /// <summary>路径在保留排名中的位置；未入排名返回 <see cref="int.MaxValue"/>（最不重要）。</summary>
    private static int RetentionRankOf(string path)
        => _retentionRank.TryGetValue(path, out int rank) ? rank : int.MaxValue;

    /// <summary>路径是否在保留排名内（缩略图分区 Caution 修剪只看这个）。</summary>
    private static bool IsRetained(string path) => _retentionRank.ContainsKey(path);

    /// <summary>
    /// 有效主分区字节上限：按实测预算推导并缓存（每轮 MemoryGovernor 探测后重算）。
    /// 用户上限、256MB 下限、<c>预算 × (1 − (Caution 进入阈值 + 5)/100) − 非缓存占用</c> 三者取夹取结果；
    /// 非缓存占用 = max(0, 实测 footprint − 主分区缓存 − 缩略图缓存 − 池空闲字节)。
    /// <see cref="AutomaticCacheLimit"/> 为 true 时忽略用户上限，只按预算推导（iPad 自动口径）。
    /// </summary>
    public static long EffectiveCacheCapacityBytes => Interlocked.Read(ref _effectiveCapacityBytes);

    /// <summary>有效主分区张数上限：自动口径视为 400，否则取用户设置。</summary>
    public static int EffectiveCacheCount => Volatile.Read(ref _effectiveCacheCount);

    /// <summary>
    /// 缓存容量是否由实测预算自动决定（iPad 壳启动时置 true）：忽略用户的缓存内存/数量上限，
    /// 只按预算推导；设置页的旧提示与开关由后续任务改为只读口径。
    /// </summary>
    public static bool AutomaticCacheLimit
    {
        get => _automaticCacheLimit;
        set
        {
            _automaticCacheLimit = value;
            RecomputeEffectiveCapacity();
        }
    }
    private static volatile bool _automaticCacheLimit;

    /// <summary>
    /// 按最新预算与实测占用重算有效上限。footprint 未知（如 Android 系统水位口径）时不按预算推导，
    /// 只用用户上限；自动口径下退回当前用户值/默认值。
    /// </summary>
    public static void RecomputeEffectiveCapacity()
    {
        long userLimit = _maxCacheSize;
        bool automatic = _automaticCacheLimit;
        long budget = MemoryGovernor.BudgetBytes;
        long footprint = MemoryGovernor.FootprintBytes;
        long limit;
        if (budget > 0 && footprint > 0)
        {
            long nonCache = Math.Max(0, footprint - FullCacheSize - ThumbCacheSize - ImageBufferPool.IdleBytes);
            double ratio = 1 - (Math.Clamp(MemoryGovernor.CautionEnterPercent, 0, 90) + 5) / 100.0;
            long derived = (long)(budget * ratio) - nonCache;
            limit = automatic ? derived : Math.Min(userLimit, derived);
        }
        else
        {
            limit = userLimit;
        }
        Interlocked.Exchange(ref _effectiveCapacityBytes, Math.Max(MinimumCacheBytes, limit));
        Volatile.Write(ref _effectiveCacheCount, automatic ? AutomaticCacheCount : _maxCacheCount);
    }

    /// <summary>
    /// 淘汰选择序列（第一个最先淘汰）：
    /// 未入排名的按 LRU 在前 → 排名内从最不重要到最重要（排名数值降序）；
    /// 同一排名内先淘汰 Full 再淘汰 Proxy（缩略图档不参与主分区淘汰），当前图与排名 ② 的条目同排名内保持 LRU。
    /// </summary>
    /// <param name="thumbScope">true = 只在 Thumb 分区选;false = 只在主分区(Full/Proxy)选。</param>
    private static List<KeyValuePair<BitmapCacheKey, BitmapCacheItem>> OrderForEviction(bool thumbScope)
    {
        var list = _cache.ToList()
            .Where(kv => (kv.Key.Class == ImageSizeClass.Thumb) == thumbScope)
            .ToList();
        list.Sort(new EvictionOrderComparer(_retentionRank));
        return list;
    }

    /// <summary>排名淘汰序比较器（契约见 <see cref="OrderForEviction"/>）。</summary>
    private sealed class EvictionOrderComparer(Dictionary<string, int> rank) : IComparer<KeyValuePair<BitmapCacheKey, BitmapCacheItem>>
    {
        public int Compare(KeyValuePair<BitmapCacheKey, BitmapCacheItem> x, KeyValuePair<BitmapCacheKey, BitmapCacheItem> y)
        {
            bool xRanked = rank.TryGetValue(x.Key.Path, out int xRank);
            bool yRanked = rank.TryGetValue(y.Key.Path, out int yRank);
            if (xRanked != yRanked) return xRanked ? 1 : -1;
            if (xRanked && xRank != yRank) return yRank.CompareTo(xRank);
            // 同排名：当前图(0)与上一张(1)保持 LRU，其余先淘汰 Full
            if (xRanked && xRank > 1)
            {
                int byClass = ClassEvictionOrder(x.Key.Class).CompareTo(ClassEvictionOrder(y.Key.Class));
                if (byClass != 0) return byClass;
            }
            return x.Value.LastAccessTime.CompareTo(y.Value.LastAccessTime);
        }

        private static int ClassEvictionOrder(ImageSizeClass sizeClass) => sizeClass switch
        {
            ImageSizeClass.Full => 0,
            ImageSizeClass.Proxy => 1,
            _ => 2,
        };
    }
    
    // 容量序列化(在途预留账归 MemoryGovernor,不再本类自记)
    private static readonly SemaphoreSlim _capacitySemaphore = new(1, 1);

    // 新增：平均缓存项大小（字节,仅主分区——缩略图不参与全分辨率估算）
    private static long GetAverageCacheItemSizeBytes()
    {
        var count = FullCacheCount;
        if (count <= 0) return 0;
        var total = FullCacheSize;
        if (total <= 0) return 0;
        return total / count;
    }

    /// <summary>当前渲染表面的代理目标长边(物理像素 × overscan;由展示会话下发,0 = 未知)。</summary>
    public static int ProxyTargetLongEdge { get; set; }

    /// <summary>
    /// 读取逻辑显示尺寸(不解码):经 <see cref="ImageOrientationInfo"/> 统一取数——
    /// HEIF 读容器 ispe box + Default Rotation,JPEG/RAW 读 EXIF;失败返回 null。
    /// 供代理显示期的几何基准(100% 缩放语义锚定全分辨率)。
    /// </summary>
    public static async Task<(int Width, int Height)?> GetLogicalDisplaySizeAsync(IFileRef file)
    {
        try
        {
            await using var stream = await file.OpenReadAsync().ConfigureAwait(false);
            var directories = ImageMetadataReader.ReadMetadata(stream);
            var info = ImageOrientationInfo.FromDirectories(directories, HeifLoader.IsHeifFile(file));
            if (info.SensorWidth <= 0 || info.SensorHeight <= 0) return null;
            return info.SwapsWidthHeight
                ? (info.SensorHeight, info.SensorWidth)
                : (info.SensorWidth, info.SensorHeight);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 估算文件解码后的内存大小：
    /// 1) EXIF 尺寸（width*height*4）
    /// 2) 缓存平均占用
    /// 3) 兜底 100MB
    /// </summary>
    public static async Task<long> EstimateDecodedSizeAsync(IFileRef file, CancellationToken ct = default)
    {
        try
        {
            // 1) EXIF 尺寸
            var dims = await ExifLoader.TryGetDimensionsAsync(file).ConfigureAwait(false);
            if (ct.IsCancellationRequested) return 0;
            if (dims.HasValue && dims.Value.width > 0 && dims.Value.height > 0)
            {
                // 修改：按设置计算 BPP
                int bpp = PresentationBytesPerPixel;
                return (long)dims.Value.width * dims.Value.height * bpp;
            }
        }
        catch
        {
            // 忽略，走回退
        }

        // 2) 回退到缓存平均占用
        var avg = GetAverageCacheItemSizeBytes();
        if (avg > 0) return avg;

        // 3) 最终兜底为 100MB
        return 100L * 1024 * 1024;
    }

    /// <summary>
    /// 在解码前确保容量：主分区(缓存 + 在途预留 + 需求) 不超过有效上限；必要时先释放空闲大缓冲，
    /// 再按排名淘汰到安全水位。缩略图分区不参与此账(上限自管,见 <see cref="EvictThumbsToFit"/>)。
    /// 在途预留账归 <see cref="MemoryGovernor"/>;淘汰只做租约归还(纯托管,无 UI 线程同步 Dispose、无阻塞 GC)。
    /// </summary>
    /// <param name="needBytes">本次解码预计字节数。</param>
    /// <param name="priority">解码优先级：P2/P3 只允许淘汰比目标排名差的条目，腾不出就跳过。</param>
    /// <param name="filePath">本次解码的目标路径（用于比较保留排名）。</param>
    /// <returns>是否已腾出足够容量。</returns>
    private static async Task<bool> EnsureCapacityAsync(long needBytes, DecodePriority priority, string filePath)
    {
        if (needBytes <= 0) return true;

        await _capacitySemaphore.WaitAsync().ConfigureAwait(false);
        try
        {
            // 本次解码的估值已在 AdmissionPermit 里计入 ReservedBytes,这里不再重复加 needBytes
            long limit = EffectiveCacheCapacityBytes;
            if (FullCacheSize + MemoryGovernor.ReservedBytes <= limit) return true;

            // 先释放空闲池(同样计入 footprint,大数组要等 major GC 才归还系统):缓冲是纯富余,比淘汰缓存条目的代价低
            long deficit = FullCacheSize + MemoryGovernor.ReservedBytes - limit;
            ImageBufferPool.ReleaseIdle(deficit);

            // 大需求按 80% 安全水位修剪，避免刚好卡在边界反复淘汰
            bool tooLarge = needBytes > (long)(limit * 0.6);
            long stopAt = tooLarge ? (long)(limit * 0.8) : limit;
            int targetRank = RetentionRankOf(filePath);
            // P0/P1 可淘汰任意非当前条目；预取/后台只淘汰比目标更差的条目，绝不挤掉更重要条目
            bool canEvictAny = priority <= DecodePriority.P1_HighValue;

            while (FullCacheSize + MemoryGovernor.ReservedBytes > stopAt)
            {
                var victim = FindEvictable(thumbScope: false, targetRank, canEvictAny);
                if (victim == null) break;
                EvictRange([victim.Value]);
            }

            return FullCacheSize + MemoryGovernor.ReservedBytes <= limit;
        }
        finally
        {
            _capacitySemaphore.Release();
        }
    }

    /// <summary>按淘汰序找第一个允许淘汰的条目（null = 没有可淘汰项）。</summary>
    private static KeyValuePair<BitmapCacheKey, BitmapCacheItem>? FindEvictable(
        bool thumbScope, int targetRank, bool canEvictAny)
    {
        foreach (var kv in OrderForEviction(thumbScope))
        {
            int rank = RetentionRankOf(kv.Key.Path);
            if (rank == 0) continue;                        // 当前图不因容量被淘汰
            if (canEvictAny || rank > targetRank || (targetRank == int.MaxValue && rank == int.MaxValue))
                return kv;
        }
        return null;
    }

    /// <summary>
    /// 异步获取图片(分级解码):hint = Full 走全分辨率;Proxy 走代理(缓存已有足够大且保真度足够的代理即复用,否则重解码替换)。
    /// 本次编码由 <see cref="IPresentationEncodingPolicy"/> 按尺寸档与当前压力协商。
    /// 代理失败自动回退 Full(代理是性能优化,正确性优先)。返回图像携带调用方一份租约,用完 <see cref="DecodedImage.Release"/>。
    /// </summary>
    /// <param name="file">文件引用。</param>
    /// <param name="hint">尺寸档提示。</param>
    /// <param name="priority">解码优先级。</param>
    /// <param name="ct">取消令牌。</param>
    public static async Task<DecodedImage?> GetBitmapAsync(IFileRef file, DecodeSizeHint hint, DecodePriority priority, CancellationToken ct)
    {
        if (hint.Class != ImageSizeClass.Proxy || hint.MaxLongEdge <= 0)
            return await GetBitmapAsync(file, priority, ct);

        var filePath = file.PathKey;
        var key = new BitmapCacheKey(filePath, ImageSizeClass.Proxy);

        for (int attempt = 0; attempt < 3; attempt++)
        {
            // 本次请求的编码按当下压力协商；缓存复用要求长边足够且编码保真度不低于需求
            var encoding = PresentationEncoding(ImageSizeClass.Proxy);

            // 代理命中规则:已有代理长边 ≥ 需求且编码保真度不低于需求即复用(更大代理向下兼容,显示侧 GPU 缩放)
            if (_cache.TryGetValue(key, out var cachedItem)
                && Reusable(cachedItem.Image, hint.MaxLongEdge, encoding)
                && cachedItem.Image.TryAcquire())
            {
                cachedItem.UpdateAccessTime();
                PerfTrace.RecordDecode(file.Name, true, 0, 0, cachedItem.Image.Width, cachedItem.Image.Height);
                return cachedItem.Image;
            }

            // 跨档复用:同路径 Full 已够大且保真度不低于本次需求时直接返回 Full,不再解代理
            // (Mac F16 Full 满足 F16 代理请求;iPad 8 位 Full 不满足 F16 代理请求,不得降保真度)。
            var fullKey = new BitmapCacheKey(filePath, ImageSizeClass.Full);
            if (_cache.TryGetValue(fullKey, out var fullItem)
                && Reusable(fullItem.Image, hint.MaxLongEdge, encoding)
                && fullItem.Image.TryAcquire())
            {
                fullItem.UpdateAccessTime();
                PerfTrace.RecordDecode(file.Name, true, 0, 0, fullItem.Image.Width, fullItem.Image.Height);
                return fullItem.Image;
            }

            var flightKey = filePath + "#proxy" + hint.MaxLongEdge;
            var decode = _inFlight.GetOrAdd(flightKey, _ => StartProxyDecode(file, filePath, hint.MaxLongEdge, encoding, ct, priority));
            // 合并到在途解码的请求提升票面:翻到"预取已经在解的那一张"时,这条解码就地按 P0 排队,
            // 不会因发起它的是后台预取而继续被后台通道的让位规则挡住。
            decode.Ticket.Promote(priority, DecodeContext.Alongside);
            DecodedImage? img;
            try { img = await AwaitInFlightAsync(flightKey, decode, decode.Task.Value).ConfigureAwait(false); }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // 合并任务的原请求已取消，当前请求仍有效；重发代理，不把取消误判为解码失败升级 Full。
                continue;
            }

            if (img == null)
            {
                // 代理解码失败(含准入被拒之外的解码错误):P0 回退 Full 保证正确性
                if (priority == DecodePriority.P0_Current && !ct.IsCancellationRequested)
                    return await GetBitmapAsync(file, priority, ct);
                return null;
            }
            // 与已有在途任务合并时该任务的编码可能低于本次需求（压力刚抬高），重走协商
            if (FidelityAtLeast(img.Encoding, PresentationEncoding(ImageSizeClass.Proxy)) && img.TryAcquire()) return img;
        }
        return null;
    }

    /// <summary>
    /// 在途解码的登记项:票面(<see cref="DecodeTicket"/>,可被合并进来的请求提升)+ 惰性任务。
    /// 同路径(同尺寸档)的请求合并到同一条解码,票面共享。
    /// </summary>
    private sealed class InFlightDecode
    {
        public required DecodeTicket Ticket { get; init; }
        public required Lazy<Task<DecodedImage?>> Task { get; init; }
    }

    /// <summary>登记一条在途代理解码:先建票面再包任务,保证合并进来的请求拿到的就是这条解码的票面。</summary>
    private static InFlightDecode StartProxyDecode(
        IFileRef file, string filePath, int targetLongEdge, PixelEncoding encoding, CancellationToken ct, DecodePriority priority)
    {
        var ticket = DecodeContext.CreateTicket(priority);
        return new InFlightDecode
        {
            Ticket = ticket,
            Task = new Lazy<Task<DecodedImage?>>(
                () => LoadProxyAndCacheAsync(file, filePath, targetLongEdge, encoding, ticket, ct),
                LazyThreadSafetyMode.ExecutionAndPublication),
        };
    }

    /// <summary>登记一条在途 Full 解码(票面语义同 <see cref="StartProxyDecode"/>)。</summary>
    private static InFlightDecode StartFullDecode(
        IFileRef file, string filePath, PixelEncoding encoding, CancellationToken ct, DecodePriority priority)
    {
        var ticket = DecodeContext.CreateTicket(priority);
        return new InFlightDecode
        {
            Ticket = ticket,
            Task = new Lazy<Task<DecodedImage?>>(
                () => LoadAndCacheAsync(file, filePath, encoding, ticket, ct),
                LazyThreadSafetyMode.ExecutionAndPublication),
        };
    }

    /// <summary>
    /// 代理解码路径:准入(按代理估值)→ 平台差异化缩解码 → EXIF 旋转(平台未预旋转时)→ 入 Proxy 档缓存。
    /// 解码产物长边 ≤ targetLongEdge;源已更小时按源尺寸(不放大)。
    /// </summary>
    private static async Task<DecodedImage?> LoadProxyAndCacheAsync(IFileRef file, string filePath, int targetLongEdge, PixelEncoding encoding, DecodeTicket ticket, CancellationToken ct)
    {
        var totalSw = System.Diagnostics.Stopwatch.StartNew();

        // 按正方形上界与本次协商编码估算，避免 F16/方形用户素材低估准入预算。
        long estimate = (long)targetLongEdge * targetLongEdge * DecodedImage.BytesPerPixel(encoding);
        if (!MemoryGovernor.TryAcquire(estimate, ticket.Priority, out var permit))
            return null;

        using (permit)
        {
            try
            {
                // 预取/后台在容量不足且腾不出比自己更差的条目时直接跳过(不解码、不记错误)
                if (!await EnsureCapacityAsync(estimate, ticket.Priority, filePath).ConfigureAwait(false))
                    return null;

                var decodeSw = System.Diagnostics.Stopwatch.StartNew();
                var image = await Task.Run(() => LoadProxyWithExifRotationAsync(file, targetLongEdge, encoding, ticket, ct), ct).ConfigureAwait(false);
                decodeSw.Stop();
                if (image == null) return null;

                permit!.SetActual(image.ByteCount);

                var key = new BitmapCacheKey(filePath, ImageSizeClass.Proxy);
                var replacement = new BitmapCacheItem(image, filePath, ImageSizeClass.Proxy);
                while (true)
                {
                    if (_cache.TryGetValue(key, out var old))
                    {
                        // 同路径不同目标尺寸/编码可并发：小的或低保真度的代理不得降级已有产物，也不能覆盖时遗失缓存租约。
                        if (!Supersedes(image, old.Image))
                        {
                            image.Release();
                            return old.Image;
                        }
                        if (!_cache.TryUpdate(key, replacement, old)) continue;
                        old.Image.Release();
                        break;
                    }
                    if (_cache.TryAdd(key, replacement)) break;
                }

                PerfTrace.RecordDecode(file.Name, false, totalSw.Elapsed.TotalMilliseconds, decodeSw.Elapsed.TotalMilliseconds, image.Width, image.Height);
                UiDispatcher.Post(() => CacheStatusChanged?.Invoke(filePath, ImageSizeClass.Proxy, true));
                _ = Task.Run(CleanupCache);
                return image;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to decode proxy ({file.Name}): {ex.Message}");
                return null;   // 代理失败不抛出,调用方回退 Full
            }
        }
    }

    /// <summary>
    /// 代理缩解码(平台差异化口径):JPEG/PNG → Skia codec 子采样+Mitchell;HEIF/RAW → 平台解码器
    /// (Apple 编解码器级子采样;Windows libheif 全解码+Mitchell;Android InSampleSize)。平台未预旋转的再应用 EXIF 旋转。
    /// </summary>
    private static async Task<DecodedImage?> LoadProxyWithExifRotationAsync(IFileRef file, int targetLongEdge, PixelEncoding encoding, DecodeTicket ticket, CancellationToken ct)
    {
        if (_presentationDecoder != null)
        {
            // 把本请求的优先级带进平台解码器的排队(Windows HEIF 快速路径据此让 P0 越过后台预取)。
            using var _ = DecodeContext.Enter(ticket);
            return await _presentationDecoder.DecodeAsync(file, targetLongEdge, encoding, ct).ConfigureAwait(false);
        }
        DecodedImage? proxy = null;
        bool preRotated;

        if (HeifLoader.IsHeifFile(file))
        {
            proxy = await HeifLoader.DecodeAsync(file, targetLongEdge).ConfigureAwait(false);
            preRotated = HeifLoader.OutputPreRotated;
        }
        else if (RawLoader.IsRawFile(file))
        {
            proxy = await RawLoader.DecodeAsync(file, targetLongEdge).ConfigureAwait(false);
            preRotated = RawLoader.OutputPreRotated;
        }
        else
        {
            ct.ThrowIfCancellationRequested();
            await using var stream = await file.OpenReadAsync().ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            proxy = SkiaImageCodec.DecodeToLongEdge(stream, targetLongEdge);
            preRotated = false;
        }

        if (proxy == null || proxy.Width <= 0 || proxy.Height <= 0)
        {
            proxy?.Release();
            return null;
        }

        if (preRotated) return proxy;

        var orientation = await GetExifOrientationAsync(file).ConfigureAwait(false);
        if (orientation != 3 && orientation != 6 && orientation != 8)
            return proxy;

        var rotated = ApplyExifRotation(proxy, orientation);
        proxy.Release();
        return rotated;   // null(旋转失败)由调用方回退 Full
    }

    /// <summary>
    /// 异步获取图片（带缓存和EXIF旋转,Full 档,P0 优先级）；同路径并发请求在途去重，共享同一解码任务。
    /// 返回的图像携带调用方一份租约,用完必须 <see cref="DecodedImage.Release"/>。
    /// </summary>
    public static Task<DecodedImage?> GetBitmapAsync(IFileRef file, CancellationToken ct = default)
        => GetBitmapAsync(file, DecodePriority.P0_Current, ct);

    /// <summary>
    /// 异步获取图片（带缓存和EXIF旋转,Full 档,指定优先级准入）；同路径并发请求在途去重，共享同一解码任务。
    /// 返回的图像携带调用方一份租约,用完必须 <see cref="DecodedImage.Release"/>。
    /// 准入被拒(非 P0)或解码失败返回 null。
    /// </summary>
    /// <param name="file">文件引用。</param>
    /// <param name="priority">解码优先级(准入语义见 <see cref="MemoryGovernor.TryAcquire"/>)。</param>
    /// <param name="ct">取消令牌。</param>
    public static async Task<DecodedImage?> GetBitmapAsync(IFileRef file, DecodePriority priority, CancellationToken ct)
    {
        var filePath = file.PathKey;
        var key = new BitmapCacheKey(filePath, ImageSizeClass.Full);

        // 尝试上限防循环:命中后被淘汰释放 / 加入了被拒的低优先级在途任务,都重走本流程
        for (int attempt = 0; attempt < 3; attempt++)
        {
            // 检查缓存(命中即携租约返回;恰好被淘汰释放则按 miss 重走)
            if (_cache.TryGetValue(key, out var cachedItem) && cachedItem.Image.TryAcquire())
            {
                cachedItem.UpdateAccessTime();
                PerfTrace.RecordDecode(file.Name, true, 0, 0, cachedItem.Image.Width, cachedItem.Image.Height);
                return cachedItem.Image;
            }

            // 缓存未命中：合并同路径的在途请求（首个请求的 ct 生效于共享任务）
            var decode = _inFlight.GetOrAdd(filePath, _ => StartFullDecode(file, filePath, PresentationEncoding(ImageSizeClass.Full), ct, priority));
            // 合并到在途解码的请求提升票面(P0 翻到预取已在解的那一张时,该解码就地按 P0 排队)
            decode.Ticket.Promote(priority, DecodeContext.Alongside);
            DecodedImage? img;
            try { img = await AwaitInFlightAsync(filePath, decode, decode.Task.Value).ConfigureAwait(false); }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // 合并任务的原请求已取消，当前请求仍有效：重走本流程，不把取消误判为解码失败
                continue;
            }

            if (img == null)
            {
                // P0 加入了被准入拒绝的低优先级在途任务:豁免准入,自行重发一次
                if (priority == DecodePriority.P0_Current && !ct.IsCancellationRequested) continue;
                return null;
            }
            if (img.TryAcquire()) return img;
            // 完成即被淘汰释放的极端竞态:重走(此时多半命中缓存)
        }
        return null;
    }

    /// <summary>
    /// 等待在途任务完成（无论成败）后将其移出去重表；只移除自己那一轮登记，避免误删新一轮请求。
    /// </summary>
    private static async Task<DecodedImage?> AwaitInFlightAsync(string filePath, InFlightDecode decode, Task<DecodedImage?> task)
    {
        try
        {
            return await task.ConfigureAwait(false);
        }
        finally
        {
            _inFlight.TryRemove(new KeyValuePair<string, InFlightDecode>(filePath, decode));
        }
    }

    /// <summary>
    /// 缓存未命中路径：解码准入(Governor) → 容量保障 → 线程池解码 → 入缓存。
    /// 准入被拒(非 P0)返回 null;在途账预留于解码期,解码完成按实际字节复核,出方法归还。
    /// </summary>
    private static async Task<DecodedImage?> LoadAndCacheAsync(IFileRef file, string filePath, PixelEncoding encoding, DecodeTicket ticket, CancellationToken ct)
    {
        var totalSw = System.Diagnostics.Stopwatch.StartNew();

        long estimate = 0;
        try
        {
            estimate = await EstimateDecodedSizeAsync(file, ct).ConfigureAwait(false);
        }
        catch { /* 估算失败按 0(仅记账)走,不阻断解码 */ }

        // 解码准入:P0 豁免放行;非 P0 预算不足直接拒绝(不排队)
        if (!MemoryGovernor.TryAcquire(estimate, ticket.Priority, out var permit))
            return null;

        using (permit)
        {
            try
            {
                // 预取/后台在容量不足且腾不出比自己更差的条目时直接跳过(不解码、不记错误)
                if (!await EnsureCapacityAsync(estimate, ticket.Priority, filePath).ConfigureAwait(false))
                    return null;
            }
            catch { /* 容量保障失败不致命，继续尝试加载 */ }

            try
            {
                // 加载图片（解码本体在线程池执行，续体不回 UI 线程）
                var decodeSw = System.Diagnostics.Stopwatch.StartNew();
                var image = await Task.Run(() => LoadBitmapWithExifRotationAsync(file, encoding, ticket, ct), ct).ConfigureAwait(false);
                decodeSw.Stop();
                if (image == null) return null;

                // 按实际(缓冲池分档对齐后)字节复核在途账本
                permit!.SetActual(image.ByteCount);

                // 添加到缓存(Full 档;创建者租约归缓存持有)
                var cacheItem = new BitmapCacheItem(image, filePath, ImageSizeClass.Full);
                _cache[new BitmapCacheKey(filePath, ImageSizeClass.Full)] = cacheItem;

                // P0 链路计时埋点:供压力快照与验收门判读
                PerfTrace.RecordDecode(file.Name, false, totalSw.Elapsed.TotalMilliseconds, decodeSw.Elapsed.TotalMilliseconds, image.Width, image.Height);

                // 通知缓存状态变化（在UI线程中触发）
                UiDispatcher.Post(() => CacheStatusChanged?.Invoke(filePath, ImageSizeClass.Full, true));

                // 异步清理缓存
                _ = Task.Run(CleanupCache);

                return image;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to load image ({file.Name}): {ex.Message}");
                throw;
            }
        }
    }
    
    /// <summary>
    /// 加载图片并应用EXIF旋转；平台解码器调用处带上本请求的票面(P0 不排在后台预取之后)。
    /// </summary>
    private static async Task<DecodedImage?> LoadBitmapWithExifRotationAsync(IFileRef file, PixelEncoding encoding, DecodeTicket ticket, CancellationToken ct = default)
    {
        if (_presentationDecoder != null)
        {
            using var _ = DecodeContext.Enter(ticket);
            return await _presentationDecoder.DecodeAsync(file, null, encoding, ct).ConfigureAwait(false);
        }
        try
        {
            DecodedImage? originalImage = null;
            // RAW 平台解码器（macOS ImageIO）内部已应用 EXIF 方向，下方不再二次旋转
            bool decoderPreRotated = false;

            // 检查是否为 HEIF 格式
            if (HeifLoader.IsHeifFile(file))
            {
                originalImage = await HeifLoader.DecodeAsync(file, null).ConfigureAwait(false);
                // 按解码器能力标记(Apple ImageIO 预旋转;libheif/BitmapFactory 不旋转)
                decoderPreRotated = HeifLoader.OutputPreRotated;
            }
            else if (RawLoader.IsRawFile(file))
            {
                originalImage = await RawLoader.DecodeAsync(file, null).ConfigureAwait(false);
                decoderPreRotated = originalImage != null && RawLoader.OutputPreRotated;
            }
            else
            {
                ct.ThrowIfCancellationRequested();
                await using var stream = await file.OpenReadAsync().ConfigureAwait(false);
                ct.ThrowIfCancellationRequested();
                originalImage = SkiaImageCodec.Decode(stream);
            }
            
            // 验证原始图像是否有效
            if (originalImage == null || originalImage.Width <= 0 || originalImage.Height <= 0)
            {
                Console.WriteLine($"Invalid image loaded from file: {file.Name}");
                originalImage?.Dispose();
                return null;
            }
            
            // 获取EXIF方向信息（RAW 解码器已预旋转的不再重复读取）
            var orientation = decoderPreRotated ? 1 : await GetExifOrientationAsync(file).ConfigureAwait(false);
            
            // 只有 3/6/8 需要旋转，其余（包括 0、1 及未知值）均视为无旋转
            if (orientation != 3 && orientation != 6 && orientation != 8)
            {
                return ConvertToDesiredFormat(originalImage);
            }
            
            // 应用EXIF旋转，此过程会直接生成最终格式的图像
            var finalImage = ApplyExifRotation(originalImage, orientation);
            
            // 释放原图内存
            originalImage.Dispose();
            
            if (finalImage == null)
                throw new InvalidOperationException($"EXIF 旋转失败（方向值 {orientation}）");
            
            return finalImage;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to load and rotate image ({file.Name}): {ex.Message}");
            throw;
        }
    }

    // 将图像按设置转为 Rgb24（忽略透明度）或原样返回；成功转换时释放传入图像
    private static DecodedImage ConvertToDesiredFormat(DecodedImage source)
    {
        // 未开启忽略透明度或源格式已符合要求，则原样返回
        if (!IgnoreAlpha || source.Encoding == PixelEncoding.Rgb24)
        {
            return source;
        }

        // 仅处理 Bgra32 来源
        if (source.Encoding != PixelEncoding.Bgra32)
        {
            return source;
        }

        try
        {
            var w = source.Width;
            var h = source.Height;
            // 缓冲池租用:归还时回池而非丢 GC
            var rgb = DecodedImage.RentPixels(w * h * 3);
            for (int y = 0; y < h; y++)
            {
                var srcRow = (long)y * source.Stride;
                var destRow = (long)y * w * 3;
                for (int x = 0; x < w; x++)
                {
                    var si = srcRow + x * 4;
                    var di = destRow + x * 3;
                    // BGRA8888: B=p[0], G=p[1], R=p[2]
                    rgb[di] = source.Pixels[si + 2];     // R
                    rgb[di + 1] = source.Pixels[si + 1]; // G
                    rgb[di + 2] = source.Pixels[si];     // B
                }
            }

            source.Dispose();
            return DecodedImage.CreatePooled(rgb, w, h, w * 3, PixelEncoding.Rgb24);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to convert image to Rgb24: {ex.Message}");
            return source;
        }
    }
    
    /// <summary>
    /// 获取EXIF方向值
    /// </summary>
    private static async Task<int> GetExifOrientationAsync(IFileRef file)
    {
        try
        {
            var exifData = await ExifLoader.LoadExifDataAsync(file).ConfigureAwait(false);
            return exifData?.OrientationValue ?? 1;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to read EXIF orientation for {file.Name}: {ex.Message}");
            return 1;
        }
    }
    
    /// <summary>
    /// 应用EXIF旋转到图像
    /// </summary>
    private static DecodedImage? ApplyExifRotation(DecodedImage originalImage, int orientation)
    {
        try
        {
            DecodedImage? rotatedImage = orientation switch
            {
                3 => SkiaImageCodec.ApplyOrientation(originalImage, 180, false),
                6 => SkiaImageCodec.ApplyOrientation(originalImage, 90, false),
                8 => SkiaImageCodec.ApplyOrientation(originalImage, 270, false),
                _ => null
            };

            if (rotatedImage == null)
            {
                // 旋转失败时返回 null，由调用方处理
                return null;
            }

            // 检查是否需要移除 Alpha 通道
            bool needsConversion = IgnoreAlpha && rotatedImage.Encoding == PixelEncoding.Bgra32;

            if (needsConversion)
            {
                // ConvertToDesiredFormat 会在成功后 Dispose 传入的 rotatedImage
                return ConvertToDesiredFormat(rotatedImage);
            }

            return rotatedImage;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to apply EXIF rotation (orientation: {orientation}): {ex.Message}");
            return null; // 旋转失败时返回null，由调用方处理
        }
    }
    
    /// <summary>
    /// 清理缓存(仅主分区 Full/Proxy;缩略图分区由 <see cref="EvictThumbsToFit"/> 自管)。
    /// 按排名淘汰序修剪到有效张数/字节上限内（字节超限时收到 80% 安全水位）。
    /// </summary>
    private static void CleanupCache()
    {
        lock (_cleanupLock)
        {
            try
            {
                int count = FullCacheCount;
                long size = FullCacheSize;
                int maxCount = EffectiveCacheCount;
                long limit = EffectiveCacheCapacityBytes;
                if (count <= maxCount && size <= limit) return;

                long targetSize = (long)(limit * 0.8);
                var removed = new List<(BitmapCacheKey key, BitmapCacheItem item)>();
                foreach (var kv in OrderForEviction(thumbScope: false))
                {
                    if (count <= maxCount && size <= targetSize) break;
                    if (RetentionRankOf(kv.Key.Path) == 0) continue;   // 当前图不因容量被淘汰
                    if (_cache.TryRemove(kv.Key, out var item))
                    {
                        removed.Add((kv.Key, item));
                        count--;
                        size -= item.Size;
                    }
                }
                if (removed.Count == 0) return;

                UiDispatcher.Post(() =>
                {
                    foreach (var (key, _) in removed)
                        CacheStatusChanged?.Invoke(key.Path, key.Class, false);
                });
                foreach (var (_, item) in removed)
                    item.Image.Release();

                Console.WriteLine($"Cache cleanup completed: removed {removed.Count} items, " +
                                  $"current cache: {FullCacheCount} items, {FullCacheSize / (1024 * 1024)} MB");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Cache cleanup failed: {ex.Message}");
            }
        }
    }
    
    /// <summary>
    /// 清空所有缓存(全部尺寸档)
    /// </summary>
    public static void ClearCache()
    {
        lock (_cleanupLock)
        {
            try
            {
                var items = _cache.ToList();
                _cache.Clear();
                
                // 通知所有文件缓存状态变化（在UI线程中触发）
                UiDispatcher.Post(() =>
                {
                    foreach (var kv in items)
                    {
                        CacheStatusChanged?.Invoke(kv.Key.Path, kv.Key.Class, false);
                    }
                });

                // 租约归还直接执行(纯托管,不再回 UI 线程)
                foreach (var kv in items)
                {
                    kv.Value.Image.Release();
                }

                Console.WriteLine("Cache cleared");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to clear cache: {ex.Message}");
            }
        }
    }
    
    /// <summary>
    /// 移除特定文件的全部尺寸档缓存
    /// </summary>
    public static void RemoveFromCache(string filePath)
    {
        foreach (var cls in Enum.GetValues<ImageSizeClass>())
            RemoveCached(filePath, cls);
    }
    
    /// <summary>
    /// 预加载图片（不会阻塞UI;P2 优先级准入,被拒即跳过）。只填缓存不使用图像,取到即归还租约。
    /// </summary>
    public static async Task PreloadBitmapAsync(IFileRef file, CancellationToken ct = default)
    {
        try
        {
            var img = await GetBitmapAsync(file, DecodePriority.P2_Prefetch, ct).ConfigureAwait(false);
            img?.Release();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to preload image ({file.Name}): {ex.Message}");
        }
    }

    /// <summary>
    /// 批量顺序预取（如某张失败不中断，支持取消）
    /// </summary>
    public static async Task PreloadBitmapsSequentiallyAsync(IEnumerable<IFileRef> files, CancellationToken token)
    {
        foreach (var f in files)
        {
            if (token.IsCancellationRequested) break;
            try
            {
                if (!IsInCache(f.PathKey))
                    await PreloadBitmapAsync(f, token);
            }
            catch { /* 单个失败忽略 */ }
        }
    }
    
    /// <summary>
    /// 获取缓存统计信息(主分区 + 缩略图分区分列)
    /// </summary>
    public static (int count, long sizeBytes, string info) GetCacheStats()
    {
        var count = FullCacheCount;
        var size = FullCacheSize;
        long capacity = EffectiveCacheCapacityBytes;
        var info = $"Cache: {count}/{EffectiveCacheCount} items, {size / (1024 * 1024)}/{capacity / (1024 * 1024)} MB"
            + $"; thumbs {ThumbCacheCount}/{_thumbMaxCount}, {ThumbCacheSize / (1024 * 1024)}/{_thumbMaxBytes / (1024 * 1024)} MB";
        return (count, size, info);
    }

    // 内存告警事件载体（UI 仅展示触发时的大小、数量与时间）
    public readonly record struct MemoryWarningEvent(long sizeMB, int count, DateTimeOffset time);

    // ---- 统一内存管控 ----

    /// <summary>主缓存 + 缩略图分区的内存客户端适配器。</summary>
    private sealed class CacheMemoryClient : IMemoryClient
    {
        public string Name => "BitmapLoader";
        public long FootprintBytes => CurrentCacheSize;
        public void Trim(PressureLevel level) => TrimForPressure(level);
    }

    /// <summary>向 MemoryGovernor 登记本缓存(幂等;静态构造即登记,首次访问本类时生效),并按最新预算算一次有效上限。</summary>
    static BitmapLoader()
    {
        MemoryGovernor.Register(new CacheMemoryClient());
        MemoryGovernor.Probed += RecomputeEffectiveCapacity;
        RecomputeEffectiveCapacity();
    }

    /// <summary>
    /// 压力修剪(响应矩阵，按保留排名):
    /// Caution = 缩略图只留排名内(可视区 ± 预载窗口);
    /// Warning = 主分区只留排名 ①②③，缩略图只留排名内;
    /// Critical = 全库只留排名 ①(当前图)；无排名时退回"只留最近访问的 1 张 Full"。
    /// </summary>
    private static void TrimForPressure(PressureLevel level)
    {
        lock (_cleanupLock)
        {
            try
            {
                switch (level)
                {
                    case PressureLevel.Caution:
                        EvictRange(OrderForEviction(thumbScope: true)
                            .Where(kv => !IsRetained(kv.Key.Path))
                            .ToList());
                        break;
                    case PressureLevel.Warning:
                    {
                        var victims = OrderForEviction(thumbScope: false)
                            .Where(kv => RetentionRankOf(kv.Key.Path) > 2)
                            .ToList();
                        victims.AddRange(OrderForEviction(thumbScope: true)
                            .Where(kv => !IsRetained(kv.Key.Path)));
                        EvictRange(victims);
                        break;
                    }
                    case PressureLevel.Critical:
                    {
                        var all = _cache.ToList();
                        if (all.Count == 0) break;
                        if (_retentionRanking.Length > 0)
                        {
                            EvictRange(all.Where(kv => RetentionRankOf(kv.Key.Path) != 0).ToList());
                        }
                        else
                        {
                            // 没有排名(工具/测试进程):只留最近访问的 1 张 Full(当前纹理)
                            var newest = all
                                .Where(kv => kv.Key.Class == ImageSizeClass.Full)
                                .OrderByDescending(kv => kv.Value.LastAccessTime)
                                .Select(kv => (BitmapCacheKey?)kv.Key)
                                .FirstOrDefault();
                            EvictRange(all.Where(kv => !newest.HasValue || !kv.Key.Equals(newest.Value)).ToList());
                        }
                        break;
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[BitmapLoader] Trim({level}) failed: {ex.Message}");
            }
        }
    }

    /// <summary>按淘汰键移除:UI 线程发事件,租约直接归还。</summary>
    private static void EvictRange(IEnumerable<KeyValuePair<BitmapCacheKey, BitmapCacheItem>> range)
    {
        var removed = new List<(BitmapCacheKey key, BitmapCacheItem item)>();
        foreach (var kv in range)
        {
            if (_cache.TryRemove(kv.Key, out var item))
                removed.Add((kv.Key, item));
        }
        if (removed.Count == 0) return;
        UiDispatcher.Post(() =>
        {
            foreach (var (key, _) in removed)
                CacheStatusChanged?.Invoke(key.Path, key.Class, false);
        });
        foreach (var (_, item) in removed)
            item.Image.Release();
    }
}
