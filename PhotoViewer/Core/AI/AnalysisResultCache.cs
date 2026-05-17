using System.Collections.Generic;
using Avalonia.Media.Imaging;

namespace PhotoViewer.Core.AI;

/// <summary>
/// 分析栏派生结果缓存:按指纹 LRU 缓存"读库 + 派生层现算"的全部产物 — 4 张诊断位图 + ShakeField + 判定文字 +
/// patch tokens(供 cosine 切参考点)+ aspect/CV 尺寸。命中即可让 <see cref="ViewModels.Main.AnalysisViewModel"/>
/// 直接 UI swap,不再走 SVD/锐度/抖动场重算。
///
/// 由两条路径填充:
/// 1. <see cref="ViewModels.Main.AnalysisViewModel"/> 切图 miss 后现算入缓存(主路径)。
/// 2. <see cref="PhotoViewer.Core.Image.BitmapPrefetcher"/> 预取邻居位图时顺手预热(预热路径)。
///
/// 容量按指纹计算,LRU 淘汰。**淘汰不主动 Dispose 位图** —— 32×32 BGRA 位图本体仅几 KB,native 资源由
/// <see cref="WriteableBitmap"/> finalizer 兜底回收;相对地,我们换来"VM/Prefetcher 并发 TryGet/Put 不会出现
/// use-after-dispose"的简单语义。
///
/// AI 设置页"清除特征数据库"按钮通过 <see cref="InvalidateAll"/> 整体清空(仍只清引用,不 Dispose)。
/// </summary>
public static class AnalysisResultCache
{
    /// <summary>缓存项最大数量;每项约 16 KB(派生位图) + 1.5 MB(patch tokens) ≈ 1.5 MB,32 项 ≈ 50 MB。</summary>
    private const int Capacity = 32;

    /// <summary>
    /// 一次切图后,分析栏需要消费的全部派生数据(只读快照)。位图归本 Entry 持有 — 调用方仅作展示用,不要 Dispose。
    /// </summary>
    public sealed class Entry
    {
        /// <summary>主图原始长宽比(宽/高);未入库视 1。</summary>
        public double AspectRatio { get; init; } = 1.0;

        /// <summary>PCA-RGB(DINO patch token 前 3 主成分),null 表示未提取 patch。</summary>
        public Bitmap? PcaBmp { get; init; }

        /// <summary>默认中心参考点(16,16)的 cosine 热力图,null 表示未提取 patch。</summary>
        public Bitmap? CenterCosineBmp { get; init; }

        /// <summary>锐度图,null 表示未提取 CV。</summary>
        public Bitmap? SharpnessBmp { get; init; }

        /// <summary>抖动矢量场结构,null 表示未提取 CV。</summary>
        public ShakeField? ShakeField { get; init; }

        /// <summary>抖动判定文字("抖动拖影 / 平移抖动 / …"),始终非空。</summary>
        public string ShakeLabel { get; init; } = "抖动拖影";

        /// <summary>patch token(1024×384),null 表示未提取;用于 cosine 切参考点时重算。</summary>
        public float[]? Patches { get; init; }
    }

    private static readonly object _lock = new();
    private static readonly LinkedList<KeyValuePair<string, Entry>> _order = new();
    private static readonly Dictionary<string, LinkedListNode<KeyValuePair<string, Entry>>> _index = new();

    /// <summary>
    /// 查询缓存;命中则把项提到 LRU 头部并返回。
    /// </summary>
    /// <param name="fingerprint">指纹,空字符串视为 miss。</param>
    /// <returns>命中的项;miss 返回 null。</returns>
    public static Entry? TryGet(string? fingerprint)
    {
        if (string.IsNullOrEmpty(fingerprint)) return null;
        lock (_lock)
        {
            if (!_index.TryGetValue(fingerprint, out var node)) return null;
            _order.Remove(node);
            _order.AddFirst(node);
            return node.Value.Value;
        }
    }

    /// <summary>
    /// 写入缓存;同 key 已存在则覆盖,超出容量则淘汰最久未访问项。位图引用沉默丢弃,GC 兜底回收。
    /// </summary>
    public static void Put(string fingerprint, Entry entry)
    {
        if (string.IsNullOrEmpty(fingerprint) || entry == null) return;

        lock (_lock)
        {
            if (_index.TryGetValue(fingerprint, out var existing))
            {
                _order.Remove(existing);
                _index.Remove(fingerprint);
            }

            var node = new LinkedListNode<KeyValuePair<string, Entry>>(
                new KeyValuePair<string, Entry>(fingerprint, entry));
            _order.AddFirst(node);
            _index[fingerprint] = node;

            while (_order.Count > Capacity)
            {
                var tail = _order.Last!;
                _order.RemoveLast();
                _index.Remove(tail.Value.Key);
            }
        }
    }

    /// <summary>
    /// 清空全部缓存项;由 AI 设置页"清除特征数据库"按钮调用。仅清引用,不 Dispose。
    /// </summary>
    public static void InvalidateAll()
    {
        lock (_lock)
        {
            _order.Clear();
            _index.Clear();
        }
    }
}
