using System;
using System.Globalization;
using System.Text;
using System.Threading;

namespace PhotoViewer.Core.Image.Heif;

/// <summary>
/// HEIF 整幅解码的分段耗时常驻统计(内存环形缓冲,无文件、无采样线程):压测汇总行据此区分
/// "等图像闸门 / 等解码上下文 / 解码瓦片 / 转换编码"四段,以及解码时同时在途的图像数。
/// </summary>
/// <remarks>
/// <see cref="FastHeifDecoder"/> 只在整幅解码路径上调用本类(缩略图项路径不记),
/// 因为切图延迟的长尾只由整幅解码决定。分段含义:
/// <list type="bullet">
/// <item>gate:等"同时在转换的图像数"名额;</item>
/// <item>contexts:等"全局解码上下文"预留下来(整幅图需要一次性预留全部瓦片的上下文,
/// 多幅图各自持有一部分许可会互相等待,因此这里是全有或全无);</item>
/// <item>tiles:各瓦片在 pvhevc 里解码(受 CPU 与同时在途图像数影响);</item>
/// <item>convert:YUV→展示编码(按行带并行)。</item>
/// </list>
/// </remarks>
public static class HeifDecodeStats
{
    /// <summary>环形缓冲容量(足够覆盖一轮连翻)。</summary>
    private const int Capacity = 256;

    private static readonly object Gate = new();
    private static readonly Entry[] Ring = new Entry[Capacity];
    private static int _next;
    private static int _count;
    private static int _inFlight;
    private static int _peakInFlight;

    /// <summary>一次整幅解码的分段耗时(ms)与并发快照。</summary>
    internal readonly struct Entry
    {
        /// <summary>开始解码时已在途的图像数(含自己)。</summary>
        public readonly int InFlight;
        /// <summary>是否当前照片(P0)通道的解码。</summary>
        public readonly bool Current;
        public readonly double GateMs;
        public readonly double ContextsMs;
        public readonly double TilesMs;
        public readonly double ConvertMs;

        /// <summary>构造一条记录。</summary>
        public Entry(int inFlight, bool current, double gateMs, double contextsMs, double tilesMs, double convertMs)
        {
            InFlight = inFlight;
            Current = current;
            GateMs = gateMs;
            ContextsMs = contextsMs;
            TilesMs = tilesMs;
            ConvertMs = convertMs;
        }

        /// <summary>四段合计。</summary>
        public double TotalMs => GateMs + ContextsMs + TilesMs + ConvertMs;
    }

    /// <summary>记一条解码分段耗时(解码结束、产物已就绪时调用)。</summary>
    /// <param name="inFlight">开始解码时在途图像数</param>
    /// <param name="current">是否当前照片(P0)通道的解码</param>
    /// <param name="gateMs">等图像闸门</param>
    /// <param name="contextsMs">等解码上下文预留</param>
    /// <param name="tilesMs">瓦片解码</param>
    /// <param name="convertMs">YUV 转换</param>
    internal static void Record(int inFlight, bool current, double gateMs, double contextsMs, double tilesMs, double convertMs)
    {
        lock (Gate)
        {
            Ring[_next] = new Entry(inFlight, current, gateMs, contextsMs, tilesMs, convertMs);
            _next = (_next + 1) % Capacity;
            if (_count < Capacity) _count++;
        }
    }

    /// <summary>一幅整幅解码开始(用于统计同时在途数)。</summary>
    /// <returns>当前在途数(含自己)</returns>
    internal static int Enter()
    {
        var current = Interlocked.Increment(ref _inFlight);
        int peak;
        while (current > (peak = Volatile.Read(ref _peakInFlight))
            && Interlocked.CompareExchange(ref _peakInFlight, current, peak) != peak)
        {
        }

        return current;
    }

    /// <summary>一幅整幅解码结束。</summary>
    internal static void Leave() => Interlocked.Decrement(ref _inFlight);

    /// <summary>清空(压测开始前调用)。</summary>
    public static void Reset()
    {
        lock (Gate)
        {
            _next = 0;
            _count = 0;
            Volatile.Write(ref _peakInFlight, Math.Max(0, Volatile.Read(ref _inFlight)));
        }
    }

    /// <summary>
    /// 汇总成一行纯文本(无样本返回空串):各段 p50/p95/max、总耗时 p95、峰值同时在途图像数。
    /// </summary>
    public static string Summary()
    {
        Entry[] samples;
        lock (Gate)
        {
            samples = new Entry[_count];
            for (var index = 0; index < _count; index++)
            {
                samples[index] = Ring[(_next - _count + index + Capacity) % Capacity];
            }
        }

        if (samples.Length == 0) return string.Empty;

        var builder = new StringBuilder(320);
        AppendStats(builder, "hf_", samples);
        var current = Array.FindAll(samples, static sample => sample.Current);
        if (current.Length > 0) AppendStats(builder, "hf_p0_", current);
        return builder
            .Append(" hf_peakInFlight=").Append(Volatile.Read(ref _peakInFlight))
            .ToString();
    }

    /// <summary>把一组样本的 gate/ctx/tiles/convert/total 分布按前缀追加到汇总行。</summary>
    private static void AppendStats(StringBuilder builder, string prefix, Entry[] samples)
    {
        var gate = new double[samples.Length];
        var contexts = new double[samples.Length];
        var tiles = new double[samples.Length];
        var convert = new double[samples.Length];
        var totals = new double[samples.Length];
        for (var index = 0; index < samples.Length; index++)
        {
            gate[index] = samples[index].GateMs;
            contexts[index] = samples[index].ContextsMs;
            tiles[index] = samples[index].TilesMs;
            convert[index] = samples[index].ConvertMs;
            totals[index] = samples[index].TotalMs;
        }

        Array.Sort(gate);
        Array.Sort(contexts);
        Array.Sort(tiles);
        Array.Sort(convert);
        Array.Sort(totals);
        builder
            .Append(' ').Append(prefix).Append("n=").Append(samples.Length)
            .Append(' ').Append(prefix).Append("gate_p50=").Append(F(Pct(gate, 0.50)))
            .Append(' ').Append(prefix).Append("gate_max=").Append(F(gate[^1]))
            .Append(' ').Append(prefix).Append("ctx_p95=").Append(F(Pct(contexts, 0.95)))
            .Append(' ').Append(prefix).Append("ctx_max=").Append(F(contexts[^1]))
            .Append(' ').Append(prefix).Append("tiles_p50=").Append(F(Pct(tiles, 0.50)))
            .Append(' ').Append(prefix).Append("tiles_p95=").Append(F(Pct(tiles, 0.95)))
            .Append(' ').Append(prefix).Append("tiles_max=").Append(F(tiles[^1]))
            .Append(' ').Append(prefix).Append("conv_p50=").Append(F(Pct(convert, 0.50)))
            .Append(' ').Append(prefix).Append("total_p50=").Append(F(Pct(totals, 0.50)))
            .Append(' ').Append(prefix).Append("total_p95=").Append(F(Pct(totals, 0.95)))
            .Append(' ').Append(prefix).Append("total_max=").Append(F(totals[^1]));
    }

    private static double Pct(double[] sorted, double q)
        => sorted[Math.Min(sorted.Length - 1, (int)(q * sorted.Length))];

    private static string F(double value) => value.ToString("F1", CultureInfo.InvariantCulture);
}
