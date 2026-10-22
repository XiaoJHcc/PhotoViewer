using System;
using System.Text;

namespace PhotoViewer.Windows.Native;

/// <summary>
/// 主图纹理上传的常驻轻量统计(仅内存环形缓冲,无 UI、无文件、无常驻采样线程)。
/// <see cref="WindowsPhotoView.SetImage"/> 每次换图记一条"纹理尺寸 + 建纹理/上传/mipmap 的 CPU 耗时":
/// 同步档是 UI 线程的建纹理 + 整幅写入 + mip 链;大图的后台上传另记一段后台拷贝耗时;
/// 命中最近纹理缓存记 0 ms 并单独累计命中次数。压测汇总行(<c>StressFlipDriver.ExtraSummary</c>)据此报 p50/p95/max:
/// 上传同步发生在 UI 线程,直接计入切换延迟,但没有单独测量就分不清"解码慢"与"上传慢"。
/// </summary>
internal static class WindowsUploadStats
{
    /// <summary>环形缓冲容量(足够覆盖一轮连翻,且不占内存)。</summary>
    private const int Capacity = 256;

    private static readonly object Gate = new();
    private static readonly Entry[] Ring = new Entry[Capacity];
    private static int _next;
    private static int _count;
    private static int _hits;

    /// <summary>一次上传的分段 CPU 耗时(ms);缓存命中记 0。</summary>
    internal readonly struct Entry
    {
        /// <summary>纹理尺寸(像素)。</summary>
        public readonly int Width;
        public readonly int Height;
        /// <summary>CreateTexture2D。</summary>
        public readonly double CreateMs;
        /// <summary>UpdateSubresource(整幅上传);后台上传路径上是 mip0 的 GPU 内拷贝。</summary>
        public readonly double UpdateMs;
        /// <summary>CreateShaderResourceView + GenerateMips。</summary>
        public readonly double MipsMs;
        /// <summary>后台线程完成的 CPU→GPU 拷贝耗时(不计入 UI 线程切换延迟)。</summary>
        public readonly double BackgroundMs;

        /// <summary>构造一条记录。</summary>
        public Entry(int width, int height, double createMs, double updateMs, double mipsMs, double backgroundMs)
        {
            Width = width;
            Height = height;
            CreateMs = createMs;
            UpdateMs = updateMs;
            MipsMs = mipsMs;
            BackgroundMs = backgroundMs;
        }

        /// <summary>三段 UI 线程合计。</summary>
        public double TotalMs => CreateMs + UpdateMs + MipsMs;
    }

    /// <summary>记一次上传(UI 线程调用)。</summary>
    /// <param name="width">纹理宽</param>
    /// <param name="height">纹理高</param>
    /// <param name="createMs">CreateTexture2D 耗时</param>
    /// <param name="updateMs">UpdateSubresource 耗时</param>
    /// <param name="mipsMs">着色器资源视图 + GenerateMips 耗时</param>
    /// <param name="backgroundMs">后台线程拷贝耗时(同步上传为 0)</param>
    internal static void Record(int width, int height, double createMs, double updateMs, double mipsMs, double backgroundMs = 0)
    {
        lock (Gate)
        {
            Ring[_next] = new Entry(width, height, createMs, updateMs, mipsMs, backgroundMs);
            _next = (_next + 1) % Capacity;
            if (_count < Capacity) _count++;
        }
    }

    /// <summary>
    /// 记一次缓存命中:GPU 纹理已在显存里,UI 线程零成本。
    /// 记 0 ms 让"上传耗时的 p50/p95"保持是真实上传样本的口径,命中次数单独累计。
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

    /// <summary>清空(压测开始前调用,避免上一轮样本混进来)。</summary>
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
    /// 汇总成一行纯文本(空样本返回空串):总次数与其中缓存命中数、UI 线程总耗时的 p50/p95/max、
    /// 三段各自的中位数、后台上传拷贝的中位数,以及最后一次的纹理尺寸。
    /// 命中样本记 0 ms,所以 p50/p95 是"每次切图在 UI 线程上的上传成本"。
    /// </summary>
    internal static string Summary()
    {
        Entry[] samples;
        int hits;
        lock (Gate)
        {
            samples = new Entry[_count];
            // 环形缓冲按写入顺序还原,便于取"最后一次"
            for (var index = 0; index < _count; index++)
            {
                samples[index] = Ring[(_next - _count + index + Capacity) % Capacity];
            }

            hits = _hits;
        }

        if (samples.Length == 0) return string.Empty;

        var totals = new double[samples.Length];
        var creates = new double[samples.Length];
        var updates = new double[samples.Length];
        var mips = new double[samples.Length];
        var backgrounds = new double[samples.Length];
        for (var index = 0; index < samples.Length; index++)
        {
            totals[index] = samples[index].TotalMs;
            creates[index] = samples[index].CreateMs;
            updates[index] = samples[index].UpdateMs;
            mips[index] = samples[index].MipsMs;
            backgrounds[index] = samples[index].BackgroundMs;
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
            .Append(" up_update_p50=").Append(F(Median(updates)))
            .Append(" up_mips_p50=").Append(F(Median(mips)))
            .Append(" up_bg_p50=").Append(F(Median(backgrounds)))
            .Append(" up_last=").Append(last.Width).Append('x').Append(last.Height);
        return text.ToString();
    }

    /// <summary>最近邻秩 percentile(调用方保证已排序且非空)。</summary>
    private static double Pct(double[] sorted, double q)
        => sorted[Math.Min(sorted.Length - 1, (int)(q * sorted.Length))];

    /// <summary>中位数(不改入参)。</summary>
    private static double Median(double[] values)
    {
        var sorted = (double[])values.Clone();
        Array.Sort(sorted);
        return sorted[sorted.Length / 2];
    }

    /// <summary>毫秒保留一位小数(不变文化)。</summary>
    private static string F(double value)
        => value.ToString("F1", System.Globalization.CultureInfo.InvariantCulture);
}
