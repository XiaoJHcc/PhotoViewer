using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Android.OS;
using PhotoViewer.Android.Core;
using PhotoViewer.Core.Color;
using PhotoViewer.Core.Image;
using PhotoViewer.Core.Storage;

namespace PhotoViewer.Android.Native;

/// <summary>
/// 验收专用（非产品行为，测试钩子）：<c>--es pv-perf-probe &lt;目录&gt;</c> 按文件类型（HIF / ARW / JPG）
/// 各取前 6 张（首张作预热，不计入统计）分段计时，写纯文本报告并结束进程。
/// 与 <c>WindowsPerfProbe</c> / <c>ApplePerfProbe</c> 同结构、同报告口径：展示解码器直连（绕过缓存）的
/// 代理/全图解、直方图（代理与全图）、代理增强（转 sRGB8 → EnhanceInPlace）、分析用 sRGB8、
/// ThumbnailService 小缩略图，并记录 RssAnon / VmSwap / 峰值 RSS 与失败项
/// （解码返回 null 照实记为 null result，不吞成 0 ms）。
/// 解码只走壳注入的 <see cref="IPresentationImageDecoder"/> 公共入口：后续换解码器（HEIF 快速路径 / RAW）
/// 时探针无需改动。GL 上传无法在此测量（无 EGL 上下文），跳过。
/// </summary>
internal static class AndroidPerfProbe
{
    /// <summary>每种文件类型参与探针的文件数（含首张预热）。</summary>
    private const int FilesPerType = 6;

    /// <summary>胶片栏小缩略图短边（与 Windows 探针同一口径）。</summary>
    private const int ThumbShortSide = 120;

    /// <summary>直方图输出尺寸（与产品分析栏一致）。</summary>
    private const int HistogramWidth = 512;

    /// <summary>直方图输出高度。</summary>
    private const int HistogramHeight = 512;

    /// <summary>文件类型分组（顺序即报告顺序）。</summary>
    private static readonly (string Name, string[] Extensions)[] Groups =
    [
        ("HIF", [".hif", ".heic", ".heif"]),
        ("ARW", [".arw"]),
        ("JPG", [".jpg", ".jpeg"]),
    ];

    /// <summary>
    /// 在后台线程跑完整探针；调用方负责在任务完成后结束进程。
    /// </summary>
    /// <param name="directory">只读素材目录。</param>
    /// <param name="reportPath">报告文本路径。</param>
    /// <param name="decoder">壳注入的展示解码器（与产品路径同一实现）。</param>
    /// <param name="proxyLongEdge">代理长边目标（= 视口物理长边 × 2；无界面时取 2× 屏幕物理长边）。</param>
    /// <returns>探针完成的任务。</returns>
    internal static Task RunAsync(string directory, string reportPath, IPresentationImageDecoder decoder, int proxyLongEdge)
        => Task.Run(() => Run(directory, reportPath, decoder, proxyLongEdge), CancellationToken.None);

    /// <summary>探针主体：按类型逐张测量，边测边落盘（便于外部轮询）。</summary>
    private static void Run(string directory, string reportPath, IPresentationImageDecoder decoder, int proxyLongEdge)
    {
        var report = new StringBuilder();
        var process = AndroidProcessMemory.Read();
        report.Append("PhotoViewer perf probe platform=Android")
            .Append(" device=").Append(Build.Device ?? "?")
            .Append(" model=").Append(Build.Model ?? "?")
            .Append(" sdk=").Append((int)Build.VERSION.SdkInt)
            .Append(" cpus=").Append(System.Environment.ProcessorCount)
            .Append(" arch=").Append(RuntimeInformation.ProcessArchitecture)
            .Append(" T=").Append(proxyLongEdge)
            .Append(" forceSrgb=").Append(AndroidDisplayCapabilities.ForceSrgb)
            .Append(" surfaceP3=").Append(AndroidDisplayCapabilities.SurfaceIsP3)
            .Append(" decoder=").Append(decoder.GetType().Name)
            .AppendLine();
        report.Append("process rssAnon=").Append(Megabytes(process.RssAnonBytes))
            .Append(" swap=").Append(Megabytes(process.VmSwapBytes))
            .Append(" peakRss=").Append(Megabytes(process.VmHwmBytes))
            .Append(" managed=").Append(Megabytes(GC.GetTotalMemory(false)))
            .AppendLine();

        string[] files;
        try
        {
            files = Directory.EnumerateFiles(directory)
                .Where(IsSupported)
                .OrderBy(path => Path.GetFileName(path), StringComparer.Ordinal)
                .ToArray();
        }
        catch (Exception exception)
        {
            report.Append("directory ERROR ").Append(exception.GetType().Name).Append(": ").AppendLine(exception.Message);
            WriteReport(reportPath, report);
            return;
        }

        report.Append("directory=").Append(directory).Append(" files=").Append(files.Length).AppendLine();
        WriteReport(reportPath, report);

        var peakAnon = process.RssAnonBytes;
        foreach (var (groupName, extensions) in Groups)
        {
            var selected = files
                .Where(path => extensions.Any(extension => path.EndsWith(extension, StringComparison.OrdinalIgnoreCase)))
                .Take(FilesPerType)
                .ToArray();
            if (selected.Length == 0)
            {
                report.Append("== ").Append(groupName).AppendLine(": no files in directory");
                WriteReport(reportPath, report);
                continue;
            }

            var groupStart = AndroidProcessMemory.Read();
            report.Append("== ").Append(groupName).Append(" files=").Append(selected.Length)
                .Append(" warmup=").Append(Path.GetFileName(selected[0])).AppendLine();
            report.Append("   memBefore ").AppendLine(MemoryLine(groupStart));
            WriteReport(reportPath, report);

            var collector = new Collector();
            for (var index = 0; index < selected.Length; index++)
            {
                var record = index > 0;
                var path = selected[index];
                try
                {
                    MeasureFile(collector, record, path, decoder, proxyLongEdge);
                }
                catch (Exception exception)
                {
                    collector.Fail("file", exception);
                    report.Append("-- ").Append(Path.GetFileName(path))
                        .Append(" ERROR ").Append(exception.GetType().Name).Append(": ").AppendLine(exception.Message);
                }

                // 每张之后强制回收并采样，让峰值反映"单张解码的真实占用"而不是 GC 滞后
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
                var current = AndroidProcessMemory.Read();
                if (current.RssAnonBytes > peakAnon) peakAnon = current.RssAnonBytes;
                report.Append("   mem ").AppendLine(MemoryLine(current));
                WriteReport(reportPath, report);
            }

            var groupEnd = AndroidProcessMemory.Read();
            report.Append("== ").Append(groupName).Append(" results").AppendLine();
            collector.WriteTo(report);
            report.Append("   memAfter ").AppendLine(MemoryLine(groupEnd));
            WriteReport(reportPath, report);
        }

        report.Append("== totals")
            .Append(" rssAnonPeak=").Append(Megabytes(peakAnon))
            .Append(" peakRss=").Append(Megabytes(AndroidProcessMemory.Read().VmHwmBytes))
            .Append(" managedFinal=").Append(Megabytes(GC.GetTotalMemory(false)))
            .Append(" allocatedTotal=").Append(Megabytes(GC.GetTotalAllocatedBytes()))
            .Append(" gen0=").Append(GC.CollectionCount(0))
            .Append(" gen1=").Append(GC.CollectionCount(1))
            .Append(" gen2=").Append(GC.CollectionCount(2))
            .Append(" glUpload=not-measured")
            .AppendLine();
        WriteReport(reportPath, report);
    }

    /// <summary>
    /// 单张图片的全部测量项；每项完成后立刻释放产物，避免多项大图同时驻留。
    /// 单项失败（含解码返回 null）只记该测量项，后续不依赖它的项照常跑。
    /// </summary>
    /// <param name="collector">聚合器。</param>
    /// <param name="record">false = 预热张，执行但不计入统计。</param>
    /// <param name="path">图片路径。</param>
    /// <param name="decoder">展示解码器。</param>
    /// <param name="proxyLongEdge">代理长边。</param>
    private static void MeasureFile(Collector collector, bool record, string path,
        IPresentationImageDecoder decoder, int proxyLongEdge)
    {
        // 探针量的是"当前图"的解码成本：按 P0 声明票面（内存准入豁免、不与后台排队），
        // 否则 RAW 这类重解码会被后台准入规则直接拒掉，测不出真实耗时。
        using var ticket = DecodeContext.Enter(DecodePriority.P0_Current);
        var file = new AndroidFileRef(path);
        DecodedImage? proxy = null;
        DecodedImage? full = null;
        try
        {
            Time(collector, record, "proxy" + proxyLongEdge, () =>
            {
                var image = decoder.DecodeAsync(file, proxyLongEdge, PixelEncoding.Rgba8, CancellationToken.None)
                    .GetAwaiter().GetResult() ?? throw new InvalidOperationException("展示解码返回空（null result）");
                proxy?.Dispose();
                proxy = image;
                return (image.Width, image.Height,
                    $"src={image.SourceWidth}x{image.SourceHeight} orient={image.Orientation}"
                    + $" enc={image.Encoding} cs={image.ColorSpace}");
            });

            Time(collector, record, "full", () =>
            {
                var image = decoder.DecodeAsync(file, null, PixelEncoding.Rgba8, CancellationToken.None)
                    .GetAwaiter().GetResult() ?? throw new InvalidOperationException("展示解码返回空（null result）");
                full?.Dispose();
                full = image;
                return (image.Width, image.Height,
                    $"src={image.SourceWidth}x{image.SourceHeight} orient={image.Orientation} enc={image.Encoding}");
            });

            MeasureHistogram(collector, record, "histogramProxy", proxy);
            MeasureHistogram(collector, record, "histogramFull", full);
            MeasureEnhance(collector, record, "enhanceProxy", proxy);

            if (full == null)
            {
                collector.Skip("analysisSrgbFull", "no full image");
            }
            else
            {
                Time(collector, record, "analysisSrgbFull", () =>
                {
                    using var srgb = ColorEngine.ToAnalysisSrgb8(full);
                    return (srgb.Width, srgb.Height, srgb.Encoding.ToString());
                });
            }

            Time(collector, record, "thumb" + ThumbShortSide, () =>
            {
                using var thumbnail = ThumbnailService.GetThumbnailAsync(file, ThumbShortSide, DecodePriority.P1_HighValue)
                    .GetAwaiter().GetResult() ?? throw new InvalidOperationException("缩略图返回空（来源缺失或准入被拒）");
                return (thumbnail.Width, thumbnail.Height, thumbnail.Encoding.ToString());
            });
        }
        finally
        {
            proxy?.Dispose();
            full?.Dispose();
        }
    }

    /// <summary>直方图渲染（512×512，与产品分析栏同尺寸）。</summary>
    private static void MeasureHistogram(Collector collector, bool record, string name, DecodedImage? image)
    {
        if (image == null)
        {
            collector.Skip(name, "no image");
            return;
        }

        Time(collector, record, name, () =>
        {
            using var histogram = HistogramRenderer.Render(image, HistogramWidth, HistogramHeight);
            return (histogram.Width, histogram.Height, image.Encoding.ToString());
        });
    }

    /// <summary>
    /// 代理增强：按 <c>PhotoLoadSession.EnhanceForDisplay</c> 的前两步计时（转 sRGB8 副本 → CLHE 原地增强），
    /// 合计即代理增强产物在 UI 之外的计算成本；回展示编码一步由渲染前会话完成，探针不重复。
    /// </summary>
    private static void MeasureEnhance(Collector collector, bool record, string prefix, DecodedImage? source)
    {
        if (source == null)
        {
            collector.Skip(prefix, "no image");
            return;
        }

        var stopwatch = Stopwatch.StartNew();
        DecodedImage? srgb = null;
        try
        {
            srgb = ColorEngine.ToSrgb8(source);
            var toSrgbMs = stopwatch.Elapsed.TotalMilliseconds;
            stopwatch.Restart();
            ImageEnhancer.EnhanceInPlace(srgb);
            var clheMs = stopwatch.Elapsed.TotalMilliseconds;
            collector.Add(prefix + "_toSrgb8", toSrgbMs, srgb.Width, srgb.Height, srgb.Encoding.ToString(), record);
            collector.Add(prefix + "_clhe", clheMs, srgb.Width, srgb.Height, "inPlace", record);
            collector.Add(prefix, toSrgbMs + clheMs, srgb.Width, srgb.Height, "srgb8+clhe", record);
        }
        catch (Exception exception)
        {
            collector.Fail(prefix, exception);
        }
        finally
        {
            srgb?.Dispose();
        }
    }

    /// <summary>单次计时：执行并记录耗时/输出尺寸/附加信息，异常记为该测量项的失败。</summary>
    private static void Time(Collector collector, bool record, string name,
        Func<(int Width, int Height, string Extra)> body)
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            var (width, height, extra) = body();
            collector.Add(name, stopwatch.Elapsed.TotalMilliseconds, width, height, extra, record);
        }
        catch (Exception exception)
        {
            collector.Fail(name, exception);
        }
    }

    /// <summary>参与探针的扩展名。</summary>
    private static bool IsSupported(string path)
        => Groups.Any(group => group.Extensions.Any(extension => path.EndsWith(extension, StringComparison.OrdinalIgnoreCase)));

    /// <summary>一行内存读数（探针内统一口径）。</summary>
    private static string MemoryLine(AndroidProcessMemory.Snapshot snapshot)
        => "rssAnon=" + Megabytes(snapshot.RssAnonBytes)
           + " swap=" + Megabytes(snapshot.VmSwapBytes)
           + " peakRss=" + Megabytes(snapshot.VmHwmBytes)
           + " managed=" + Megabytes(GC.GetTotalMemory(false));

    /// <summary>字节数按 MB 保留一位小数。</summary>
    private static string Megabytes(long bytes) => (bytes / 1048576.0).ToString("0.0", CultureInfo.InvariantCulture) + "MB";

    /// <summary>整份报告落盘；失败只记控制台，不打断测量。</summary>
    private static void WriteReport(string reportPath, StringBuilder report)
    {
        try
        {
            var directory = Path.GetDirectoryName(reportPath);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            File.WriteAllText(reportPath, report.ToString());
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine("[PerfProbe] 报告写入失败: " + exception.Message);
        }
    }

    /// <summary>逐项样本聚合：每项输出 n / median / p95 / min / max 与最后一次的输出尺寸与附加信息。</summary>
    private sealed class Collector
    {
        private readonly List<string> _order = new();
        private readonly Dictionary<string, List<double>> _times = new();
        private readonly Dictionary<string, string> _output = new();
        private readonly Dictionary<string, string> _failure = new();

        /// <summary>记录一次样本；record=false 只保留输出形状，不计入统计（预热张）。</summary>
        public void Add(string name, double ms, int width, int height, string extra, bool record)
        {
            if (!_times.TryGetValue(name, out var list))
            {
                list = new List<double>();
                _times[name] = list;
                _order.Add(name);
            }

            _output[name] = (width > 0 ? $"{width}x{height}" : "-") + " extra=" + extra;
            if (record) list.Add(ms);
        }

        /// <summary>记录失败（该项在报告里恒为 ERROR，不会因其他张成功而被掩盖）。</summary>
        public void Fail(string name, Exception exception)
        {
            _failure[name] = exception.GetType().Name + ": " + exception.Message;
            if (!_times.ContainsKey(name))
            {
                _order.Add(name);
                _times[name] = new List<double>();
            }
        }

        /// <summary>记录"本项不适用"（例如该图没有对应产物）。</summary>
        public void Skip(string name, string reason)
        {
            if (_times.ContainsKey(name)) return;
            _failure[name] = "skipped: " + reason;
            _times[name] = new List<double>();
            _order.Add(name);
        }

        /// <summary>把聚合结果写进报告，保持测量顺序。</summary>
        public void WriteTo(StringBuilder report)
        {
            foreach (var name in _order.OrderBy(NameSortKey, StringComparer.Ordinal))
            {
                var times = _times[name];
                if (_failure.TryGetValue(name, out var failure))
                {
                    report.AppendLine($"{Pad(name)} ERROR {failure}");
                    continue;
                }

                if (times.Count == 0)
                {
                    report.AppendLine($"{Pad(name)} warmup-only out={_output[name]}");
                    continue;
                }

                var sorted = times.ToArray();
                Array.Sort(sorted);
                report.AppendLine($"{Pad(name)} n={times.Count}"
                    + " median=" + F(sorted[sorted.Length / 2])
                    + " p95=" + F(sorted[Math.Min(sorted.Length - 1, (int)(0.95 * sorted.Length))])
                    + " min=" + F(sorted[0])
                    + " max=" + F(sorted[^1])
                    + " out=" + _output[name]);
            }
        }

        /// <summary>固定宽度的项名列。</summary>
        private static string Pad(string name) => name.PadRight(26);

        /// <summary>排序键：基名优先、_first 排在同名之后，保持可读分组。</summary>
        private static string NameSortKey(string name) => name.Replace("_first", "~", StringComparison.Ordinal);

        /// <summary>毫秒保留一位小数（不变文化）。</summary>
        private static string F(double value) => value.ToString("F1", CultureInfo.InvariantCulture);
    }
}
