using System;
using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace PhotoViewer.Android.Native;

/// <summary>长尾诊断的分段类别；报告里每类各自 max/sum/count，跨类不相加。</summary>
internal enum FrameStatsKind
{
    Total,
    LockWait,
    MakeCurrent,
    Commands,
    Swap,
    Session,
    Viewport,
    Layout,
    Post,
    Invoke,
    Context,
    Queue,
    LooperFrame,
    LooperMessage,
    Heartbeat,
}

/// <summary>
/// 主图 UI 长尾诊断采样器：默认关闭，只在 <c>zoom</c> 检查的真实 HIF 放大段开启（安装主 Looper 消息日志）。
/// 固定容量环形缓冲（写满淘汰最旧，热路径无分配、无文件与 UI 输出）；每条样本带自己的起止时间戳，
/// 汇总按最大心跳间隙反查与之间隔重叠的样本，各类别单独统计——<see cref="FrameStatsKind.Total"/>
/// 已含四个 frame 分段，嵌套段不相加。
/// </summary>
internal static class AndroidFrameStats
{
    private const int Capacity = 4096;

    private static readonly object Gate = new();
    private static readonly Entry[] Ring = new Entry[Capacity];

    /// <summary>报告里的类别短名，顺序与 <see cref="FrameStatsKind"/> 一致。</summary>
    private static readonly string[] Tokens =
    [
        "frame_total", "frame_lock", "frame_makecurrent", "frame_commands", "frame_swap",
        "root_session", "root_viewport", "root_layout",
        "disp_post", "disp_invoke", "disp_context", "disp_queue",
        "looper_frame", "looper_msg", "heartbeat",
    ];

    private static int _next;
    private static int _count;
    private static int _evicted;
    private static long _origin;
    private static volatile bool _enabled;

    // 以下只在主线程读写（Looper 消息日志与检查都在主线程）
    private static global::Android.Util.IPrinter? _printer;
    private static long _messageStart;
    private static bool _messageIsFrame;
    private static int _messageDepth;

    /// <summary>一条采样：类别与自身的起止时间戳（<see cref="Stopwatch.GetTimestamp"/>）。</summary>
    internal readonly record struct Entry(FrameStatsKind Kind, long Start, long End)
    {
        internal double Ms => Stopwatch.GetElapsedTime(Start, End).TotalMilliseconds;

        /// <summary>区间内部相交即算重叠，首尾只是相接不算。</summary>
        internal bool Overlaps(long windowStart, long windowEnd) => Start < windowEnd && End > windowStart;

        internal bool Covers(long windowStart, long windowEnd) => Start <= windowStart && End >= windowEnd;
    }

    internal static bool Enabled => _enabled;

    /// <summary>开始采样并安装主 Looper 消息日志（须在主线程调用）。</summary>
    internal static void Start()
    {
        lock (Gate)
        {
            _next = 0;
            _count = 0;
            _evicted = 0;
            _origin = Stopwatch.GetTimestamp();
        }
        _messageStart = 0;
        _messageDepth = 0;
        _enabled = true;
        _printer = new LooperPrinter();
        global::Android.OS.Looper.MyLooper()?.SetMessageLogging(_printer);
    }

    /// <summary>停止采样并清除消息日志；落在 dispatch 中间时把能对上起点的那段截断记下。</summary>
    internal static void Stop()
    {
        long pending = _messageStart;
        _messageStart = 0;
        _messageDepth = 0;
        if (pending != 0)
            Span(_messageIsFrame ? FrameStatsKind.LooperFrame : FrameStatsKind.LooperMessage, pending, Stopwatch.GetTimestamp());
        _enabled = false;
        var looper = global::Android.OS.Looper.MyLooper();
        looper?.SetMessageLogging(null);
        var printer = _printer;
        _printer = null;
        if (printer != null && looper != null)
            new global::Android.OS.Handler(looper).Post(() => printer.Dispose());
    }

    /// <summary>分段起点时间戳；关闭时返回 0，采样点不必自己判断。</summary>
    internal static long Mark() => _enabled ? Stopwatch.GetTimestamp() : 0;

    /// <summary>记一段 [startAt, endAt]；关闭态或起点为 0 时丢弃。</summary>
    internal static void Span(FrameStatsKind kind, long startAt, long endAt)
    {
        if (!_enabled || startAt == 0 || endAt < startAt) return;
        lock (Gate)
        {
            Ring[_next] = new Entry(kind, startAt, endAt);
            _next = (_next + 1) % Capacity;
            if (_count < Capacity) _count++;
            else _evicted++;
        }
    }

    /// <summary>
    /// 整轮各类别 max/sum/count，加上与 [windowStart, windowEnd] 重叠的各类别同类统计（ms）；
    /// 每类另附 <c>/coverN</c>，是整段包住该窗口的样本数（心跳自身不计）。
    /// </summary>
    internal static string Summary(long windowStart, long windowEnd)
    {
        Entry[] samples;
        int evicted;
        long origin;
        lock (Gate)
        {
            samples = new Entry[_count];
            for (int index = 0; index < _count; index++)
                samples[index] = Ring[(_next - _count + index + Capacity) % Capacity];
            evicted = _evicted;
            origin = _origin;
        }
        if (samples.Length == 0) return "diag=无样本";

        var text = new StringBuilder(384);
        text.Append("diag_n=").Append(samples.Length);
        if (evicted > 0) text.Append(" diag_evicted=").Append(evicted);
        text.Append(" diag_all[").Append(Describe(samples, 0, 0, false)).Append(']');
        if (windowEnd > windowStart)
        {
            text.Append(" diag_gap=").Append(F1(Stopwatch.GetElapsedTime(windowStart, windowEnd).TotalMilliseconds))
                .Append("ms@").Append(F1(Offset(origin, windowStart)))
                .Append("..").Append(F1(Offset(origin, windowEnd)))
                .Append("ms diag_gap_kinds[").Append(Describe(samples, windowStart, windowEnd, true)).Append(']');
        }
        return text.ToString();
    }

    /// <summary>各类别输出 <c>短名=sum/max/count[/coverN]</c>；<paramref name="window"/> 时只统计与窗口重叠的样本。</summary>
    private static string Describe(Entry[] samples, long windowStart, long windowEnd, bool window)
    {
        var text = new StringBuilder(192);
        for (int kind = 0; kind < Tokens.Length; kind++)
        {
            double sum = 0, max = 0;
            int count = 0, cover = 0;
            foreach (var sample in samples)
            {
                if ((int)sample.Kind != kind) continue;
                if (window && !sample.Overlaps(windowStart, windowEnd)) continue;
                double ms = sample.Ms;
                sum += ms;
                if (count == 0 || ms > max) max = ms;
                count++;
                if (window && sample.Kind != FrameStatsKind.Heartbeat && sample.Covers(windowStart, windowEnd)) cover++;
            }
            if (count == 0) continue;
            if (text.Length > 0) text.Append(' ');
            text.Append(Tokens[kind]).Append('=').Append(F1(sum)).Append('/').Append(F1(max))
                .Append('/').Append(count);
            if (cover > 0) text.Append("/cover").Append(cover);
        }
        return text.Length == 0 ? "-" : text.ToString();
    }

    /// <summary>主 Looper 消息日志：只记最外层信封，嵌套 dispatch 只计深度；未见开始就忽略结束。</summary>
    private static void OnLooperLine(string? line)
    {
        if (!_enabled || line == null) return;
        if (line.StartsWith(">>>>> Dispatching", StringComparison.Ordinal))
        {
            if (_messageStart != 0)
            {
                _messageDepth++;
                return;
            }
            _messageStart = Stopwatch.GetTimestamp();
            _messageIsFrame = line.Contains("Choreographer", StringComparison.Ordinal);
            return;
        }
        if (!line.StartsWith("<<<<< Finished", StringComparison.Ordinal) || _messageStart == 0) return;
        if (_messageDepth > 0)
        {
            _messageDepth--;
            return;
        }
        long start = _messageStart;
        _messageStart = 0;
        Span(_messageIsFrame ? FrameStatsKind.LooperFrame : FrameStatsKind.LooperMessage, start, Stopwatch.GetTimestamp());
    }

    /// <summary>仅诊断期间存在的 Looper 打印器。</summary>
    private sealed class LooperPrinter : Java.Lang.Object, global::Android.Util.IPrinter
    {
        public void Println(string? message) => OnLooperLine(message);
    }

    /// <summary>相对采样起点的偏移（ms，不小于 0：首条心跳可能早于 <see cref="Start"/> 的时间戳）。</summary>
    private static double Offset(long origin, long timestamp)
        => Math.Max(0, Stopwatch.GetElapsedTime(origin, timestamp).TotalMilliseconds);

    private static string F1(double value) => value.ToString("F1", CultureInfo.InvariantCulture);
}
