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
using PhotoViewer.Core.AI;
using PhotoViewer.Core.Color;
using PhotoViewer.Core.Image;
using PhotoViewer.Core.Storage;
using PhotoViewer.Windows.Core;

namespace PhotoViewer.Windows.Native;

/// <summary>
/// 验收专用(非产品行为,测试钩子):<c>--pv-perf-probe=&lt;目录&gt;</c> 按文件类型(HIF / ARW / JPG)
/// 各取前 6 张(首张作预热,不计入统计)分段计时,写纯文本报告并退出。
/// 与 <c>PhotoViewer.Apple/ApplePerfProbe.cs</c> 同结构、同报告口径,便于三端横向对比:
/// 展示解码器直连(绕过缓存)的代理/全图解、libheif 子阶段、直方图、增强三步、分析用 sRGB8、
/// 缩略图(560 / 120)与 DINO 推理,并记录私有字节峰值与托管堆。产品路径不调用。
/// </summary>
internal static class WindowsPerfProbe
{
    /// <summary>代理长边目标:覆盖 2560×1440@150% 视口 2× 超采样之上与 4K 级视口。</summary>
    private const int TargetLongEdgeA = 5120;

    /// <summary>第二个代理长边目标(接近 33MP 原生长边)。</summary>
    private const int TargetLongEdgeB = 7680;

    /// <summary>每种文件类型参与探针的文件数(含首张预热)。</summary>
    private const int FilesPerType = 6;

    /// <summary>DINO / 胶片栏缩略图短边。</summary>
    private const int ThumbLarge = 560;

    /// <summary>胶片栏小缩略图短边。</summary>
    private const int ThumbSmall = 120;

    /// <summary>DINO 推理重复次数(第一次含 DirectML 会话建立,单列 first)。</summary>
    private const int DinoRuns = 3;

    /// <summary>文件类型分组(顺序即报告顺序)。</summary>
    private static readonly (string Name, string[] Extensions)[] Groups =
    [
        ("HIF", [".hif", ".heic", ".heif"]),
        ("ARW", [".arw"]),
        ("JPG", [".jpg", ".jpeg"]),
    ];

    /// <summary>
    /// 解析 <c>--pv-perf-probe=&lt;目录&gt;</c> 或 <c>--pv-perf-probe &lt;目录&gt;</c>;
    /// 未指定返回 null。
    /// </summary>
    /// <param name="args">进程启动参数。</param>
    /// <returns>探针目录(原样,相对路径由调用方按工作目录解析)。</returns>
    internal static string? ParseFolder(IEnumerable<string> args)
    {
        var list = args as IList<string> ?? args.ToList();
        for (var index = 0; index < list.Count; index++)
        {
            var arg = list[index];
            if (arg.StartsWith("--pv-perf-probe=", StringComparison.Ordinal))
            {
                var value = arg["--pv-perf-probe=".Length..];
                if (!string.IsNullOrWhiteSpace(value)) return value;
            }
            else if (string.Equals(arg, "--pv-perf-probe", StringComparison.Ordinal) && index + 1 < list.Count)
            {
                return list[index + 1];
            }
        }

        return null;
    }

    /// <summary>对目录内图片执行分段计时并写报告(后台线程,不占 UI 线程)。</summary>
    /// <param name="directory">只读素材目录。</param>
    /// <param name="reportPath">报告文本路径。</param>
    /// <returns>探针完成的任务。</returns>
    internal static Task RunAsync(string directory, string reportPath)
        => Task.Run(() => Run(directory, reportPath), CancellationToken.None);

    /// <summary>探针主体:按类型逐张测量,边测边落盘(便于外部轮询)。</summary>
    private static void Run(string directory, string reportPath)
    {
        var report = new StringBuilder();
        Report(report, "PhotoViewer perf probe platform=Windows"
            + " machine=" + Environment.MachineName
            + " cpus=" + Environment.ProcessorCount
            + " os=" + RuntimeInformation.OSDescription
            + " arch=" + RuntimeInformation.ProcessArchitecture
            + " T=" + TargetLongEdgeA + "/" + TargetLongEdgeB
            + " serverGC=" + System.Runtime.GCSettings.IsServerGC);

        var process = Process.GetCurrentProcess();
        Report(report, $"process private={Megabytes(process.PrivateMemorySize64)} peakWorkingSet={Megabytes(process.PeakWorkingSet64)}");

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
            Report(report, $"directory ERROR {exception.GetType().Name}: {exception.Message}");
            WriteReport(reportPath, report);
            return;
        }

        Report(report, $"directory={directory} files={files.Length}");
        WriteReport(reportPath, report);

        var peakPrivate = process.PrivateMemorySize64;
        var measuredAny = false;
        var dinoMeasured = false;

        foreach (var (groupName, extensions) in Groups)
        {
            var selected = files
                .Where(path => extensions.Any(extension => path.EndsWith(extension, StringComparison.OrdinalIgnoreCase)))
                .Take(FilesPerType)
                .ToArray();
            if (selected.Length == 0)
            {
                Report(report, $"== {groupName}: no files in directory");
                WriteReport(reportPath, report);
                continue;
            }

            measuredAny = true;
            Report(report, $"== {groupName} files={selected.Length} warmup={Path.GetFileName(selected[0])}");
            var collector = new Collector();
            for (var index = 0; index < selected.Length; index++)
            {
                var record = index > 0;
                var path = selected[index];
                // DINO 只测一次,且落在第一张"计入统计"的图上(预热张的样本会被丢弃)
                var measureDino = !dinoMeasured && record;
                try
                {
                    MeasureFile(collector, record, path, groupName == "HIF", measureDino);
                    if (measureDino) dinoMeasured = true;
                }
                catch (Exception exception)
                {
                    collector.Fail("file", exception);
                    Report(report, $"-- {Path.GetFileName(path)} ERROR {exception.GetType().Name}: {exception.Message}");
                }

                // 每张之后强制回收并采样私有字节,让峰值反映"单张解码的真实占用"而不是 GC 滞后
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
                var current = Process.GetCurrentProcess().PrivateMemorySize64;
                if (current > peakPrivate) peakPrivate = current;
                Report(report, $"   mem private={Megabytes(current)} managed={Megabytes(GC.GetTotalMemory(false))}");
                WriteReport(reportPath, report);
            }

            collector.WriteTo(report);
            WriteReport(reportPath, report);
        }

        Report(report, "== totals"
            + " privatePeak=" + Megabytes(peakPrivate)
            + " managedFinal=" + Megabytes(GC.GetTotalMemory(false))
            + " allocatedTotal=" + Megabytes(GC.GetTotalAllocatedBytes())
            + " gen0=" + GC.CollectionCount(0) + " gen1=" + GC.CollectionCount(1) + " gen2=" + GC.CollectionCount(2));
        if (!measuredAny) Report(report, "no supported files measured");
        WriteReport(reportPath, report);
    }

    /// <summary>单张图片的全部测量项;每项完成后立刻释放该产物,避免多项大图同时驻留。</summary>
    /// <param name="collector">聚合器。</param>
    /// <param name="record">false = 预热张,执行但不计入统计。</param>
    /// <param name="path">图片路径。</param>
    /// <param name="isHeif">是否 HEIF(额外测 libheif 子阶段)。</param>
    /// <param name="measureDino">是否测一次 DINO 推理(整个探针只测一次)。</param>
    private static void MeasureFile(Collector collector, bool record, string path, bool isHeif, bool measureDino)
    {
        var file = new LocalFileRef(path);
        var decoder = new WindowsPresentationDecoder();
        DecodedImage? proxy = null;
        DecodedImage? full = null;
        try
        {
            // 展示解码器直连(与壳注入的 WindowsPresentationDecoder 同一实现,绕过 BitmapLoader 缓存与准入)
            Time(collector, record, "proxy" + TargetLongEdgeA, () =>
            {
                var image = decoder.DecodeAsync(file, TargetLongEdgeA, PixelEncoding.RgbaF16, CancellationToken.None)
                    .GetAwaiter().GetResult() ?? throw new InvalidOperationException("解码返回空");
                proxy?.Dispose();
                proxy = image;
                return (image.Width, image.Height,
                    $"src={image.SourceWidth}x{image.SourceHeight} orient={image.Orientation} enc={image.Encoding} cs={image.ColorSpace}");
            });

            // 第二个代理目标只测时间,不留产物
            Time(collector, record, "proxy" + TargetLongEdgeB, () =>
            {
                using var image = decoder.DecodeAsync(file, TargetLongEdgeB, PixelEncoding.RgbaF16, CancellationToken.None)
                    .GetAwaiter().GetResult() ?? throw new InvalidOperationException("解码返回空");
                return (image.Width, image.Height, $"src={image.SourceWidth}x{image.SourceHeight}");
            });

            Time(collector, record, "full", () =>
            {
                var image = decoder.DecodeAsync(file, null, PixelEncoding.RgbaF16, CancellationToken.None)
                    .GetAwaiter().GetResult() ?? throw new InvalidOperationException("解码返回空");
                full?.Dispose();
                full = image;
                return (image.Width, image.Height,
                    $"src={image.SourceWidth}x{image.SourceHeight} orient={image.Orientation} enc={image.Encoding}");
            });

            if (isHeif) MeasureHeifStages(collector, record, file);

            MeasureHistogram(collector, record, "histogramProxy5120", proxy);
            MeasureHistogram(collector, record, "histogramFull", full);
            MeasureEnhance(collector, record, "enhanceProxy5120", proxy);
            MeasureEnhance(collector, record, "enhanceFull", full);

            if (full != null)
            {
                Time(collector, record, "analysisSrgbFull", () =>
                {
                    using var srgb = ColorEngine.ToAnalysisSrgb8(full);
                    return (srgb.Width, srgb.Height, srgb.Encoding.ToString());
                });
            }

            MeasureThumbnail(collector, record, file, ThumbLarge);
            MeasureThumbnail(collector, record, file, ThumbSmall);

            if (measureDino) MeasureDino(collector, record, file);
        }
        finally
        {
            proxy?.Dispose();
            full?.Dispose();
        }
    }

    /// <summary>
    /// HEIF 子阶段:libheif 全解码 + Mitchell 精缩(8 位未旋转产物)与随后的 ToScRgb16 分开计时,
    /// 两者之和即代理解码的绝大部分(差值 = EXIF 读取与租约搬运)。
    /// </summary>
    private static void MeasureHeifStages(Collector collector, bool record, IFileRef file)
    {
        DecodedImage? eight = null;
        Time(collector, record, "heifDecode" + TargetLongEdgeA, () =>
        {
            var image = HeifLoader.DecodeAsync(file, TargetLongEdgeA).GetAwaiter().GetResult()
                ?? throw new InvalidOperationException("libheif 解码返回空: " + HeifLoader.LastDecodeError);
            eight?.Dispose();
            eight = image;
            return (image.Width, image.Height, $"enc={image.Encoding} preRotated={HeifLoader.OutputPreRotated}");
        });

        if (eight != null)
        {
            var source = eight;
            Time(collector, record, "heifToScRgb16", () =>
            {
                using var f16 = ColorEngine.ToScRgb16(source, source.SourceWidth, source.SourceHeight, 1);
                return (f16.Width, f16.Height, "linearSrgb16f");
            });
            eight.Dispose();
            eight = null;
        }
    }

    /// <summary>直方图渲染(512×512,与产品分析栏同尺寸)。</summary>
    private static void MeasureHistogram(Collector collector, bool record, string name, DecodedImage? image)
    {
        if (image == null)
        {
            collector.Skip(name, "no image");
            return;
        }

        Time(collector, record, name, () =>
        {
            using var histogram = HistogramRenderer.Render(image, 512, 512);
            return (histogram.Width, histogram.Height, image.Encoding.ToString());
        });
    }

    /// <summary>按 <c>PhotoLoadSession.EnhanceForDisplay</c> 的三步分别计时:转 sRGB8 / CLHE / 回展示编码。</summary>
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
            collector.Add(prefix + "_toSrgb8", stopwatch.Elapsed.TotalMilliseconds, srgb.Width, srgb.Height,
                srgb.Encoding.ToString(), record);
            stopwatch.Restart();
            ImageEnhancer.EnhanceInPlace(srgb);
            collector.Add(prefix + "_clhe", stopwatch.Elapsed.TotalMilliseconds, srgb.Width, srgb.Height, "inPlace", record);
            stopwatch.Restart();
            using var restored = ColorEngine.ToScRgb16(srgb);
            collector.Add(prefix + "_back", stopwatch.Elapsed.TotalMilliseconds, restored.Width, restored.Height,
                restored.Encoding.ToString(), record);
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

    /// <summary>
    /// 缩略图服务(内嵌预览 → 平台兜底),入口带解码准入;
    /// 560 是 DINO/批量特征输入,120 是胶片栏小图口径。
    /// </summary>
    private static void MeasureThumbnail(Collector collector, bool record, IFileRef file, int shortSide)
    {
        Time(collector, record, "thumb" + shortSide, () =>
        {
            using var thumbnail = ThumbnailService.GetThumbnailAsync(file, shortSide, DecodePriority.P1_HighValue)
                .GetAwaiter().GetResult() ?? throw new InvalidOperationException("缩略图返回空(来源缺失或准入被拒)");
            return (thumbnail.Width, thumbnail.Height, thumbnail.Encoding.ToString());
        });
    }

    /// <summary>DINO 推理(第一次含 DirectML 会话建立;产品里它只用于批量特征索引)。</summary>
    private static void MeasureDino(Collector collector, bool record, IFileRef file)
    {
        using var thumbnail = ThumbnailService.GetThumbnailAsync(file, ThumbLarge, DecodePriority.P1_HighValue)
            .GetAwaiter().GetResult();
        if (thumbnail == null)
        {
            collector.Skip("dinoInference" + ThumbLarge, "no thumbnail");
            return;
        }

        for (var run = 0; run < DinoRuns; run++)
        {
            var name = run == 0 ? $"dinoInference{ThumbLarge}_first" : $"dinoInference{ThumbLarge}";
            var stopwatch = Stopwatch.StartNew();
            try
            {
                var (cls, patches) = DinoFeatureExtractor.ExtractDualAsync(thumbnail, includePatches: true)
                    .GetAwaiter().GetResult();
                collector.Add(name, stopwatch.Elapsed.TotalMilliseconds, thumbnail.Width, thumbnail.Height,
                    $"cls={cls.Length} patches={patches?.Length ?? 0} gpu={DinoFeatureExtractor.UsesGpuExecutionProvider}", record);
            }
            catch (Exception exception)
            {
                collector.Fail(name, exception);
                return;
            }
        }
    }

    /// <summary>单次计时:执行并记录耗时/输出尺寸/附加信息,异常记为该测量项的失败。</summary>
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

    /// <summary>报告追加一行(统一换行)。</summary>
    private static void Report(StringBuilder report, string line) => report.AppendLine(line);

    /// <summary>字节数按 MB 保留一位小数。</summary>
    private static string Megabytes(long bytes) => (bytes / 1048576.0).ToString("0.0", CultureInfo.InvariantCulture) + "MB";

    /// <summary>整份报告落盘;失败只记控制台,不打断测量。</summary>
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

    /// <summary>逐项样本聚合:每项输出 n / median / min / max 与最后一次的输出尺寸与附加信息。</summary>
    private sealed class Collector
    {
        private readonly List<string> _order = new();
        private readonly Dictionary<string, List<double>> _times = new();
        private readonly Dictionary<string, string> _output = new();
        private readonly Dictionary<string, string> _failure = new();

        /// <summary>记录一次样本;record=false 只保留输出形状,不计入统计(预热张)。</summary>
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

        /// <summary>记录失败(同项后续成功不会覆盖失败信息,先清失败再写结果)。</summary>
        public void Fail(string name, Exception exception)
        {
            _failure[name] = exception.GetType().Name + ": " + exception.Message;
            if (!_times.ContainsKey(name)) _order.Add(name);
            if (!_times.ContainsKey(name)) _times[name] = new List<double>();
        }

        /// <summary>记录"本项不适用"(例如该图没有对应产物)。</summary>
        public void Skip(string name, string reason)
        {
            if (_times.ContainsKey(name)) return;
            _failure[name] = "skipped: " + reason;
            _times[name] = new List<double>();
            _order.Add(name);
        }

        /// <summary>把聚合结果写进报告,保持测量顺序。</summary>
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
                    + " min=" + F(sorted[0])
                    + " max=" + F(sorted[^1])
                    + " out=" + _output[name]);
            }
        }

        /// <summary>固定宽度的项名列。</summary>
        private static string Pad(string name) => name.PadRight(26);

        /// <summary>排序键:基名优先、_first 排在同名之后,保持可读分组。</summary>
        private static string NameSortKey(string name) => name.Replace("_first", "~", StringComparison.Ordinal);

        /// <summary>毫秒保留一位小数(不变文化)。</summary>
        private static string F(double value) => value.ToString("F1", CultureInfo.InvariantCulture);
    }
}
