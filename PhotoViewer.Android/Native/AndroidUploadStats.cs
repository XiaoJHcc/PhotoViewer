using System;
using System.Text;

namespace PhotoViewer.Android.Native;

/// <summary>
/// 主图纹理上传的常驻轻量统计（仅内存环形缓冲，无 UI、无文件、无采样线程）。
/// <see cref="AndroidPhotoRenderer.SetImage"/> 每次换图记一条"纹理尺寸 + 建纹理/分配/分块上传/mipmap 的 CPU 耗时"：
/// 上传同步发生在 UI 线程，直接计入切换延迟；未单独测量就分不清"解码慢"与"上传慢"。
/// 同一 <see cref="PhotoViewer.Core.Image.DecodedImage"/> 实例再次上屏视为缓存命中，记 0 ms 并单独累计命中次数。
/// 压测汇总行（<c>StressFlipDriver.ExtraSummary</c>）据此报 p50/p95/max，字段名与 Windows 口径一致。
/// </summary>
internal static class AndroidUploadStats
{
    /// <summary>环形缓冲容量（足够覆盖一轮连翻，且不占内存）。</summary>
    private const int Capacity = 256;

    private static readonly object Gate = new();
    private static readonly Entry[] Ring = new Entry[Capacity];
    private static int _next;
    private static int _count;
    private static int _hits;

    /// <summary>一次上传的分段 CPU 耗时（ms）；缓存命中记 0。</summary>
    internal readonly struct Entry
    {
        /// <summary>纹理宽（像素）。</summary>
        public readonly int Width;

        /// <summary>纹理高（像素）。</summary>
        public readonly int Height;

        /// <summary>glGenTextures + 纹理参数。</summary>
        public readonly double CreateMs;

        /// <summary>glTexImage2D 分配纹理存储。</summary>
        public readonly double AllocMs;

        /// <summary>分块 glTexSubImage2D 像素上传。</summary>
        public readonly double UpdateMs;

        /// <summary>glGenerateMipmap。</summary>
        public readonly double MipsMs;

        /// <summary>构造一条记录。</summary>
        public Entry(int width, int height, double createMs, double allocMs, double updateMs, double mipsMs)
        {
            Width = width;
            Height = height;
            CreateMs = createMs;
            AllocMs = allocMs;
            UpdateMs = updateMs;
            MipsMs = mipsMs;
        }

        /// <summary>UI 线程四段合计。</summary>
        public double TotalMs => CreateMs + AllocMs + UpdateMs + MipsMs;
    }

    /// <summary>记一次上传（UI 线程调用）。</summary>
    /// <param name="width">纹理宽</param>
    /// <param name="height">纹理高</param>
    /// <param name="createMs">建纹理 + 参数耗时</param>
    /// <param name="allocMs">glTexImage2D 耗时</param>
    /// <param name="updateMs">分块上传耗时</param>
    /// <param name="mipsMs">mipmap 耗时</param>
    internal static void Record(int width, int height, double createMs, double allocMs, double updateMs, double mipsMs)
    {
        lock (Gate)
        {
            Ring[_next] = new Entry(width, height, createMs, allocMs, updateMs, mipsMs);
            _next = (_next + 1) % Capacity;
            if (_count < Capacity) _count++;
        }
    }

    /// <summary>
    /// 记一次缓存命中：同一展示产物已在显存里，UI 线程零上传。
    /// 记 0 ms 让 p50/p95 保持"每次切图的上传成本"口径，命中次数单独累计。
    /// </summary>
    /// <param name="width">纹理宽</param>
    /// <param name="height">纹理高</param>
    internal static void RecordCacheHit(int width, int height)
    {
        lock (Gate)
        {
            _hits++;
            Ring[_next] = new Entry(width, height, 0, 0, 0, 0);
            _next = (_next + 1) % Capacity;
            if (_count < Capacity) _count++;
        }
    }

    /// <summary>清空（压测开始前调用，避免上一轮样本混进来）。</summary>
    internal static void Reset()
    {
        lock (Gate)
        {
            _next = 0;
            _count = 0;
            _hits = 0;
        }
    }

    /// <summary>
    /// 汇总成一行纯文本（空样本返回空串）：总次数与其中缓存命中数、上传总耗时的 p50/p95/max、
    /// 四段各自的中位数，以及最后一次的纹理尺寸。
    /// </summary>
    internal static string Summary()
    {
        Entry[] samples;
        int hits;
        lock (Gate)
        {
            samples = new Entry[_count];
            // 环形缓冲按写入顺序还原，便于取"最后一次"
            for (var index = 0; index < _count; index++)
            {
                samples[index] = Ring[(_next - _count + index + Capacity) % Capacity];
            }

            hits = _hits;
        }

        if (samples.Length == 0) return string.Empty;

        var totals = new double[samples.Length];
        var creates = new double[samples.Length];
        var allocs = new double[samples.Length];
        var updates = new double[samples.Length];
        var mips = new double[samples.Length];
        for (var index = 0; index < samples.Length; index++)
        {
            totals[index] = samples[index].TotalMs;
            creates[index] = samples[index].CreateMs;
            allocs[index] = samples[index].AllocMs;
            updates[index] = samples[index].UpdateMs;
            mips[index] = samples[index].MipsMs;
        }

        Array.Sort(totals);
        var last = samples[^1];
        var text = new StringBuilder(192);
        text.Append("uploads=").Append(samples.Length)
            .Append(" up_hits=").Append(hits)
            .Append(" up_p50=").Append(F(Pct(totals, 0.50)))
            .Append(" up_p95=").Append(F(Pct(totals, 0.95)))
            .Append(" up_max=").Append(F(totals[^1]))
            .Append(" up_create_p50=").Append(F(Median(creates)))
            .Append(" up_alloc_p50=").Append(F(Median(allocs)))
            .Append(" up_update_p50=").Append(F(Median(updates)))
            .Append(" up_mips_p50=").Append(F(Median(mips)))
            .Append(" up_last=").Append(last.Width).Append('x').Append(last.Height);
        return text.ToString();
    }

    /// <summary>最近邻秩 percentile（调用方保证已排序且非空）。</summary>
    private static double Pct(double[] sorted, double q)
        => sorted[Math.Min(sorted.Length - 1, (int)(q * sorted.Length))];

    /// <summary>中位数（不改入参）。</summary>
    private static double Median(double[] values)
    {
        var sorted = (double[])values.Clone();
        Array.Sort(sorted);
        return sorted[sorted.Length / 2];
    }

    /// <summary>毫秒保留一位小数（不变文化）。</summary>
    private static string F(double value)
        => value.ToString("F1", System.Globalization.CultureInfo.InvariantCulture);
}
