using System;
using System.Text;

namespace PhotoViewer.Android.Native;

/// <summary>
/// 主图纹理上传的常驻轻量统计（仅内存环形缓冲，无 UI、无文件、无采样线程）。
/// GL 四段旧口径合计为 <see cref="Entry.TotalMs"/>；另记互不重叠的各段与一次提交的外层墙钟 <see cref="Entry.SubmitMs"/>。
/// 缓存命中四段记 0 ms、命中数单独累计，墙钟照记；<see cref="Entry.CopyMs"/> 是 <see cref="Entry.UpdateMs"/> 的嵌套拷贝，不重复相加。
/// 汇总行（<c>StressFlipDriver.ExtraSummary</c>）分别报上传与提交两组 p50/p95/max，转换耗时只按真实转换的样本统计。
/// </summary>
internal static class AndroidUploadStats
{
    /// <summary>环形缓冲容量（足够覆盖一轮连翻，固定占用）。</summary>
    private const int Capacity = 256;

    private static readonly object Gate = new();
    private static readonly Entry[] Ring = new Entry[Capacity];
    private static int _next;
    private static int _count;
    private static int _hits;

    /// <summary>一次换图的提交记录（ms）；缓存命中记 0 ms 上传但保留真实提交墙钟。</summary>
    internal readonly struct Entry
    {
        /// <summary>纹理宽（像素）。</summary>
        public readonly int Width;

        /// <summary>纹理高（像素）。</summary>
        public readonly int Height;

        /// <summary>EglMakeCurrent。</summary>
        public readonly double MakeCurrentMs;

        /// <summary>ColorEngine 转换；没有转换时为 0。</summary>
        public readonly double ConvertMs;

        /// <summary>glGenTextures + 纹理参数；复用空纹理时为 0。</summary>
        public readonly double CreateMs;

        /// <summary>glTexImage2D 分配纹理存储。</summary>
        public readonly double AllocMs;

        /// <summary>像素 glTexSubImage2D 上传（含嵌套的 <see cref="CopyMs"/>）。</summary>
        public readonly double UpdateMs;

        /// <summary>glGenerateMipmap。</summary>
        public readonly double MipsMs;

        /// <summary>收尾：解绑、Bind 与转换产物租约释放。</summary>
        public readonly double FinishMs;

        /// <summary>SetImage 的外层同步墙钟（不含统计写入与绘制/交换）。</summary>
        public readonly double SubmitMs;

        public readonly long CopyBytes;
        public readonly int Chunks;

        /// <summary>有额外行距时的逐行中间拷贝；已含在 <see cref="UpdateMs"/> 内。</summary>
        public readonly double CopyMs;

        /// <summary>是否真的做了色域/编码转换。</summary>
        public readonly bool Converted;

        /// <summary>是否复用了一张空纹理（只有 TakeSpare 命中才为 true）。</summary>
        public readonly bool ReusedTexture;

        /// <summary>缓存命中（同一实例或最近缓存回切）：GL 四段为 0。</summary>
        public readonly bool CacheHit;

        /// <summary>构造一条记录。</summary>
        public Entry(int width, int height, double submitMs, bool converted, bool reusedTexture,
            double makeCurrentMs, double convertMs, double createMs, double allocMs, double updateMs, double mipsMs,
            double finishMs, long copyBytes, int chunks, double copyMs, bool cacheHit = false)
        {
            Width = width;
            Height = height;
            SubmitMs = submitMs;
            Converted = converted;
            ReusedTexture = reusedTexture;
            MakeCurrentMs = makeCurrentMs;
            ConvertMs = convertMs;
            CreateMs = createMs;
            AllocMs = allocMs;
            UpdateMs = updateMs;
            MipsMs = mipsMs;
            FinishMs = finishMs;
            CopyBytes = copyBytes;
            Chunks = chunks;
            CopyMs = copyMs;
            CacheHit = cacheHit;
        }

        /// <summary>旧口径：GL 四段合计（建纹理/分配/像素上传/mipmap），缓存命中为 0。不是完整 UI 成本。</summary>
        public double TotalMs => CreateMs + AllocMs + UpdateMs + MipsMs;

        /// <summary>互不重叠的分段合计（不含嵌套的 <see cref="CopyMs"/>）；<see cref="SubmitMs"/> 覆盖它但不等。</summary>
        public double SegmentsMs => MakeCurrentMs + ConvertMs + CreateMs + AllocMs + UpdateMs + MipsMs + FinishMs;
    }

    /// <summary>记一次真实上传（UI 线程调用）；copyMs 已包含在 updateMs 中。</summary>
    internal static void Record(int width, int height, double submitMs, bool converted, bool reusedTexture,
        double makeCurrentMs, double convertMs, double createMs, double allocMs, double updateMs, double mipsMs,
        double finishMs, long copyBytes, int chunks, double copyMs)
    {
        lock (Gate)
        {
            Ring[_next] = new Entry(width, height, submitMs, converted, reusedTexture, makeCurrentMs, convertMs,
                createMs, allocMs, updateMs, mipsMs, finishMs, copyBytes, chunks, copyMs);
            _next = (_next + 1) % Capacity;
            if (_count < Capacity) _count++;
        }
    }

    /// <summary>
    /// 记一次缓存命中：GL 四段记 0 ms（命中数单独累计），提交墙钟仍记这次切图的真实同步耗时。
    /// </summary>
    /// <param name="width">纹理宽</param>
    /// <param name="height">纹理高</param>
    /// <param name="submitMs">提交墙钟（ms）。</param>
    internal static void RecordCacheHit(int width, int height, double submitMs)
    {
        lock (Gate)
        {
            _hits++;
            Ring[_next] = new Entry(width, height, submitMs, false, false, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, cacheHit: true);
            _next = (_next + 1) % Capacity;
            if (_count < Capacity) _count++;
        }
    }

    internal static Entry? LatestForChecks
    {
        get
        {
            lock (Gate) return _count == 0 ? null : Ring[(_next - 1 + Capacity) % Capacity];
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
    /// 汇总成一行纯文本（空样本返回空串）：旧口径上传 p50/p95/max 与四段中位数、提交墙钟 p50/p95/max（分上传/命中）、
    /// 绑定与收尾中位数、只按真实转换样本统计的转换 p50/max 与空纹理复用次数。
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

        int uploadCount = 0, hitCount = 0, convertedCount = 0, reusedCount = 0;
        foreach (var sample in samples)
        {
            if (sample.CacheHit) hitCount++;
            else uploadCount++;
            if (sample.Converted) convertedCount++;
            if (sample.ReusedTexture) reusedCount++;
        }

        var totals = new double[samples.Length];
        var submits = new double[samples.Length];
        var uploadSubmits = new double[uploadCount];
        var hitSubmits = new double[hitCount];
        var convertedTimes = new double[convertedCount];
        var creates = new double[samples.Length];
        var allocs = new double[samples.Length];
        var updates = new double[samples.Length];
        var mips = new double[samples.Length];
        var makeCurrents = new double[samples.Length];
        var finishes = new double[samples.Length];
        int uploadIndex = 0, hitIndex = 0, convertedIndex = 0;
        for (var index = 0; index < samples.Length; index++)
        {
            var sample = samples[index];
            totals[index] = sample.TotalMs;
            submits[index] = sample.SubmitMs;
            if (sample.CacheHit) hitSubmits[hitIndex++] = sample.SubmitMs;
            else uploadSubmits[uploadIndex++] = sample.SubmitMs;
            if (sample.Converted) convertedTimes[convertedIndex++] = sample.ConvertMs;
            creates[index] = sample.CreateMs;
            allocs[index] = sample.AllocMs;
            updates[index] = sample.UpdateMs;
            mips[index] = sample.MipsMs;
            makeCurrents[index] = sample.MakeCurrentMs;
            finishes[index] = sample.FinishMs;
        }

        Array.Sort(totals);
        Array.Sort(submits);
        Array.Sort(uploadSubmits);
        Array.Sort(hitSubmits);
        Array.Sort(convertedTimes);
        var last = samples[^1];
        var text = new StringBuilder(384);
        text.Append("uploads=").Append(samples.Length)
            .Append(" up_hits=").Append(hits)
            .Append(" up_p50=").Append(F(Pct(totals, 0.50)))
            .Append(" up_p95=").Append(F(Pct(totals, 0.95)))
            .Append(" up_max=").Append(F(totals[^1]))
            .Append(" up_create_p50=").Append(F(Median(creates)))
            .Append(" up_alloc_p50=").Append(F(Median(allocs)))
            .Append(" up_update_p50=").Append(F(Median(updates)))
            .Append(" up_mips_p50=").Append(F(Median(mips)))
            .Append(" up_last=").Append(last.Width).Append('x').Append(last.Height)
            .Append(" submit_p50=").Append(F2(Pct(submits, 0.50)))
            .Append(" submit_p95=").Append(F2(Pct(submits, 0.95)))
            .Append(" submit_max=").Append(F2(submits[^1]))
            .Append(" submit_upload_p50=").Append(Optional(uploadSubmits, 0.50))
            .Append(" submit_hit_p50=").Append(Optional(hitSubmits, 0.50))
            .Append(" submit_makecurrent_p50=").Append(F2(Median(makeCurrents)))
            .Append(" submit_finish_p50=").Append(F2(Median(finishes)))
            .Append(" submit_converted=").Append(convertedCount)
            .Append(" submit_convert_p50=").Append(Optional(convertedTimes, 0.50))
            .Append(" submit_convert_max=").Append(convertedTimes.Length == 0 ? "-" : F2(convertedTimes[^1]))
            .Append(" submit_reused=").Append(reusedCount);
        return text.ToString();
    }

    /// <summary>最近邻秩 percentile（调用方保证已排序）。</summary>
    private static double Pct(double[] sorted, double q)
        => sorted.Length == 0 ? 0 : sorted[Math.Min(sorted.Length - 1, (int)(q * sorted.Length))];

    /// <summary>中位数（不改入参）。</summary>
    private static double Median(double[] values)
    {
        var sorted = (double[])values.Clone();
        Array.Sort(sorted);
        return sorted[sorted.Length / 2];
    }

    /// <summary>percentile 文本；空样本写 <c>-</c>（例如这一轮一次都没有转换）。</summary>
    private static string Optional(double[] sorted, double q)
        => sorted.Length == 0 ? "-" : F2(Pct(sorted, q));

    /// <summary>毫秒保留一位小数（不变文化）。</summary>
    private static string F(double value)
        => value.ToString("F1", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>毫秒保留两位小数：提交墙钟经常落在亚毫秒级，一位小数会被舍入成 0。</summary>
    private static string F2(double value)
        => value.ToString("F2", System.Globalization.CultureInfo.InvariantCulture);
}
