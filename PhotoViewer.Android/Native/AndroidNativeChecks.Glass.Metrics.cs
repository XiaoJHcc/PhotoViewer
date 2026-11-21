using System;
using System.Collections.Generic;
using System.Diagnostics;
using Android.App;
using Android.Views;
using AndroidActivity = Android.App.Activity;

namespace PhotoViewer.Android.Native;

/// <summary>
/// 阶段 0 技术验证的刷新率与帧间隔度量（验收专用，<c>pv-check glass</c> 宿主组使用）：
/// 只给读数与统计口径，不含任何 PASS/FAIL 门槛——门槛由检查按裁决后的口径施加。
/// 帧间隔既可喂心跳（Looper 循环）也可喂「输入 → 提交」的单次延迟样本，统计方式相同。
/// </summary>
internal static partial class AndroidNativeChecks
{
    /// <summary>屏幕刷新率与一帧的时长（毫秒）。</summary>
    /// <param name="Hertz">刷新率（Hz）。</param>
    /// <param name="VsyncIntervalMs">一帧时长（ms）。</param>
    /// <param name="Source">读数来源（便于报告里区分实测与兜底）。</param>
    internal readonly record struct GlassRefreshRate(double Hertz, double VsyncIntervalMs, string Source)
    {
        /// <summary>读数是否可用（非零、非 NaN）。</summary>
        internal bool IsUsable => Hertz > 0 && VsyncIntervalMs > 0 && !double.IsNaN(VsyncIntervalMs);

        /// <summary>把毫秒换算成帧数；读数不可用时为 NaN，不做猜测。</summary>
        /// <param name="milliseconds">时长（ms）。</param>
        /// <returns>帧数。</returns>
        internal double ToVsyncs(double milliseconds) => IsUsable ? milliseconds / VsyncIntervalMs : double.NaN;

        /// <summary>
        /// 心跳循环的投递间隔（ms）：按一帧时长向上取整。固定 16ms 在 120Hz（8.33ms 一帧）上等于每个 tick
        /// 跨两帧，会被掉帧口径误算成 50% 掉帧；读数不可用时退回 16ms。
        /// </summary>
        internal int HeartbeatTickMs => IsUsable ? Math.Max(1, (int)Math.Ceiling(VsyncIntervalMs)) : 16;

        /// <summary>报告用描述。</summary>
        /// <returns>可读描述。</returns>
        internal string Describe() => $"{Hertz:F2}Hz 一帧={VsyncIntervalMs:F2}ms 来源={Source}";
    }

    /// <summary>
    /// 读当前刷新率：优先 <see cref="Display.Mode"/>（API 23，未被弃用），读不到时按 60Hz 兜底并如实标注来源。
    /// 小米等支持动态刷新率的机型必须用当前模式的读数做阈值换算，不能假设 60Hz。
    /// </summary>
    /// <param name="activity">宿主 Activity（用于拿当前显示）。</param>
    /// <returns>刷新率读数。</returns>
    internal static GlassRefreshRate ReadGlassRefreshRate(AndroidActivity activity)
    {
        try
        {
            var display = activity.Window?.DecorView?.Display;
            if (display != null)
            {
                var mode = display.GetMode();
                float hertz = mode?.RefreshRate ?? 0;
                if (hertz > 0 && !float.IsNaN(hertz) && !float.IsInfinity(hertz))
                    return new GlassRefreshRate(hertz, 1000.0 / hertz, "Display.GetMode().RefreshRate");
            }
        }
        catch (Exception exception)
        {
            Console.WriteLine($"[NativeCheck] 刷新率读取失败：{exception.GetType().Name}：{exception.Message}");
        }
        return new GlassRefreshRate(60, 1000.0 / 60.0, "兜底 60Hz（未读到当前模式）");
    }

    /// <summary>
    /// 一组帧间隔的统计量（ms）：均值/中位/95 分位/最大、按刷新率折算的掉帧数与占比，以及由样本反推的帧率。
    /// </summary>
    /// <param name="Count">样本数。</param>
    /// <param name="MeanMs">均值。</param>
    /// <param name="MedianMs">中位。</param>
    /// <param name="P95Ms">95 分位。</param>
    /// <param name="MaxMs">最大。</param>
    /// <param name="SumMs">样本总和。</param>
    /// <param name="VsyncIntervalMs">换算用的帧时长。</param>
    /// <param name="DroppedFrames">按帧时长折算的掉帧数之和。</param>
    /// <param name="DroppedRatio">掉帧数 / 样本数。</param>
    internal readonly record struct GlassFrameStats(int Count, double MeanMs, double MedianMs, double P95Ms,
        double MaxMs, double SumMs, double VsyncIntervalMs, int DroppedFrames, double DroppedRatio)
    {
        /// <summary>是否有样本（没有样本时各项统计都不可解释）。</summary>
        internal bool HasSamples => Count > 0;

        /// <summary>中位值折合的帧数。</summary>
        internal double MedianVsyncs => VsyncIntervalMs > 0 ? MedianMs / VsyncIntervalMs : double.NaN;

        /// <summary>95 分位折合的帧数。</summary>
        internal double P95Vsyncs => VsyncIntervalMs > 0 ? P95Ms / VsyncIntervalMs : double.NaN;

        /// <summary>由样本反推的帧率（样本是均匀心跳时才有意义）。</summary>
        internal double FramesPerSecond => SumMs > 0 ? Count * 1000.0 / SumMs : double.NaN;

        /// <summary>报告用描述。</summary>
        /// <returns>可读描述。</returns>
        internal string Describe()
            => Count == 0
                ? "无样本"
                : $"n={Count} 帧={FramesPerSecond:F1}fps 均值={MeanMs:F2} 中位={MedianMs:F2}"
                    + $"（{MedianVsyncs:F2}帧） p95={P95Ms:F2}（{P95Vsyncs:F2}帧） 最大={MaxMs:F2}"
                    + $" 掉帧={DroppedFrames}（{DroppedRatio:P1}）";
    }

    /// <summary>
    /// 由样本算统计量。掉帧口径：单样本按帧时长四舍五入折成整数帧，超出的一帧起算作掉帧
    /// （间隔 1.4 帧记 0，1.6 帧记 1）；不做跨样本补偿，也不剔除异常样本——异常值直接体现在最大值与 p95 上。
    /// </summary>
    /// <param name="samplesMs">样本（ms），顺序无关。</param>
    /// <param name="vsyncIntervalMs">一帧时长（ms）。</param>
    /// <returns>统计量。</returns>
    internal static GlassFrameStats ComputeGlassFrameStats(IReadOnlyList<double>? samplesMs, double vsyncIntervalMs)
    {
        if (samplesMs == null || samplesMs.Count == 0)
            return new GlassFrameStats(0, double.NaN, double.NaN, double.NaN, double.NaN, 0, vsyncIntervalMs, 0, 0);

        int count = samplesMs.Count;
        var sorted = new double[count];
        double sum = 0, max = double.NegativeInfinity;
        int dropped = 0;
        for (int index = 0; index < count; index++)
        {
            double sample = samplesMs[index];
            sorted[index] = sample;
            sum += sample;
            if (sample > max) max = sample;
            if (vsyncIntervalMs > 0)
                dropped += Math.Max(0, (int)Math.Round(sample / vsyncIntervalMs) - 1);
        }
        Array.Sort(sorted);
        double median = count % 2 == 1
            ? sorted[count / 2]
            : (sorted[count / 2 - 1] + sorted[count / 2]) / 2.0;
        int p95Index = Math.Clamp((int)Math.Ceiling(count * 0.95) - 1, 0, count - 1);
        return new GlassFrameStats(count, sum / count, median, sorted[p95Index], max, sum, vsyncIntervalMs,
            dropped, (double)dropped / count);
    }

    /// <summary>
    /// 帧间隔记录器：<see cref="Mark"/> 记心跳间隔，<see cref="AddSample"/> 直接喂单次延迟样本
    /// （例如「输入 → 提交」的墙钟）。两者共用同一套统计，便于基线/候选同口径对照。
    /// </summary>
    internal sealed class GlassFrameIntervalRecorder
    {
        private readonly List<double> _samples = [];
        private readonly double _vsyncIntervalMs;
        private long _previous;
        private bool _started;

        /// <summary>按刷新率构造。</summary>
        /// <param name="rate">刷新率读数。</param>
        internal GlassFrameIntervalRecorder(GlassRefreshRate rate) : this(rate.VsyncIntervalMs)
        {
        }

        /// <summary>按帧时长构造。</summary>
        /// <param name="vsyncIntervalMs">一帧时长（ms）。</param>
        internal GlassFrameIntervalRecorder(double vsyncIntervalMs) => _vsyncIntervalMs = vsyncIntervalMs;

        /// <summary>已记录样本（顺序即记录顺序）。</summary>
        internal IReadOnlyList<double> SamplesMs => _samples;

        /// <summary>清空并重新开始计时。</summary>
        internal void Restart()
        {
            _samples.Clear();
            _previous = Stopwatch.GetTimestamp();
            _started = true;
        }

        /// <summary>记一次心跳：把距上次 <see cref="Mark"/>/<see cref="Restart"/> 的间隔记成一个样本；首次调用只起算。</summary>
        internal void Mark()
        {
            if (!_started)
            {
                Restart();
                return;
            }
            long now = Stopwatch.GetTimestamp();
            AddSample(Stopwatch.GetElapsedTime(_previous, now).TotalMilliseconds);
            _previous = now;
        }

        /// <summary>直接记一个样本（负数、NaN、无穷大一律丢弃，不把它们算成 250ms 阻塞）。</summary>
        /// <param name="milliseconds">时长（ms）。</param>
        internal void AddSample(double milliseconds)
        {
            if (double.IsNaN(milliseconds) || double.IsInfinity(milliseconds) || milliseconds < 0) return;
            _samples.Add(milliseconds);
        }

        /// <summary>算当前样本的统计量。</summary>
        /// <returns>统计量。</returns>
        internal GlassFrameStats Compute() => ComputeGlassFrameStats(_samples, _vsyncIntervalMs);
    }

    /// <summary>
    /// 基线（SurfaceView）与候选（TextureView）在同一脚本下的帧间隔/延迟对照。
    /// 只有中位与 p95 的差值，以及折合的帧数；阈值判定是**提议值**，是否据此判 PASS 由检查按裁决口径决定。
    /// </summary>
    /// <param name="BaselineMedianMs">基线中位。</param>
    /// <param name="CandidateMedianMs">候选中位。</param>
    /// <param name="MedianDeltaMs">候选中位减去基线中位。</param>
    /// <param name="MedianDeltaVsyncs">中位差折合的帧数。</param>
    /// <param name="P95DeltaMs">p95 差。</param>
    /// <param name="P95DeltaVsyncs">p95 差折合的帧数。</param>
    /// <param name="MedianDeltaWithinOneFrame">中位差是否不超过一帧（提议阈值，仅诊断）。</param>
    /// <param name="P95DeltaWithinTwoFrames">p95 差是否不超过两帧（提议阈值，仅诊断）。</param>
    internal readonly record struct GlassLatencyComparison(double BaselineMedianMs, double CandidateMedianMs,
        double MedianDeltaMs, double MedianDeltaVsyncs, double P95DeltaMs, double P95DeltaVsyncs,
        bool MedianDeltaWithinOneFrame, bool P95DeltaWithinTwoFrames)
    {
        /// <summary>报告用描述：正数表示候选更慢。</summary>
        /// <returns>可读描述。</returns>
        internal string Describe()
            => $"基线中位={BaselineMedianMs:F2}ms 候选中位={CandidateMedianMs:F2}ms"
                + $" 差={MedianDeltaMs:F2}ms（{MedianDeltaVsyncs:F2}帧，≤1帧={MedianDeltaWithinOneFrame}）"
                + $" p95差={P95DeltaMs:F2}ms（{P95DeltaVsyncs:F2}帧，≤2帧={P95DeltaWithinTwoFrames}）";
    }

    /// <summary>
    /// 对照两组帧间隔统计：差值为「候选 − 基线」，正数表示候选更慢。
    /// 两组的帧时长都不可解释时折合帧数为 NaN，且两个建议阈值一律为 false，绝不把缺读数当成通过。
    /// </summary>
    /// <param name="baseline">基线统计（例如 SurfaceView）。</param>
    /// <param name="candidate">候选统计（例如 TextureView）。</param>
    /// <param name="rate">刷新率读数（与两组样本同一轮测量）。</param>
    /// <returns>对照结果。</returns>
    internal static GlassLatencyComparison CompareGlassLatency(GlassFrameStats baseline, GlassFrameStats candidate,
        GlassRefreshRate rate)
    {
        double medianDeltaMs = candidate.MedianMs - baseline.MedianMs;
        double p95DeltaMs = candidate.P95Ms - baseline.P95Ms;
        double medianDeltaVsyncs = rate.ToVsyncs(medianDeltaMs);
        double p95DeltaVsyncs = rate.ToVsyncs(p95DeltaMs);
        bool bothUsable = baseline.HasSamples && candidate.HasSamples && rate.IsUsable;
        return new GlassLatencyComparison(baseline.MedianMs, candidate.MedianMs, medianDeltaMs, medianDeltaVsyncs,
            p95DeltaMs, p95DeltaVsyncs, bothUsable && medianDeltaVsyncs <= 1.0, bothUsable && p95DeltaVsyncs <= 2.0);
    }
}
