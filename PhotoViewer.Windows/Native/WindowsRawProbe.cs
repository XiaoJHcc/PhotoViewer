using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using PhotoViewer.Core;
using PhotoViewer.Core.Color;
using PhotoViewer.Core.Image;
using PhotoViewer.Core.Storage;
using PhotoViewer.Windows.Core;

namespace PhotoViewer.Windows.Native;

/// <summary>
/// 验收专用(非产品行为,测试钩子):RAW 受控对照测量,由 <see cref="WindowsPerfProbe"/> 在 ARW 分组内调用。
/// 对同一份 WIC 单次开发(Bgra32)分解现有代理路径(5120 Mitchell 精缩 → 线性 F16)与 Full 直出(源直接转 F16)的耗时,
/// 并把两份产物与产品解码器同请求的产物逐字节比对。产物全部在本方法内释放,探针之外无调用点。
/// </summary>
internal static class WindowsRawProbe
{
    internal static bool? Measure(WindowsPerfProbe.Collector collector, bool record, IFileRef file,
        DecodedImage? decoderProxy, DecodedImage? decoderFull, StringBuilder report)
    {
        var path = file.TryGetLocalPath();
        if (string.IsNullOrEmpty(path) || !WindowsRawDecoder.IsAvailable(Path.GetExtension(path)))
        {
            collector.Skip("rawWicDecode", "无可用 WIC RAW 解码器");
            return null;
        }

        var name = Path.GetFileName(path);
        DecodedImage? source = null;
        DecodedImage? proxy = null;
        DecodedImage? proxyF16 = null;
        DecodedImage? fullF16 = null;
        var stage = "rawWicDecode";
        try
        {
            // 真实 WIC 单次开发:整幅 Bgra32,后续阶段共用这一份。
            int? declaredOrientation = null;
            var watch = Stopwatch.StartNew();
            source = WindowsRawDecoder.DecodeFull(path, CancellationToken.None, out declaredOrientation)
                ?? throw new InvalidOperationException("WIC RAW 解码返回空");
            watch.Stop();
            var wicMs = watch.Elapsed.TotalMilliseconds;
            collector.Add(stage, wicMs, source.Width, source.Height,
                $"enc={source.Encoding} cs={source.ColorSpace} icc={source.IccProfile.Length}"
                + " bytes=" + WindowsPerfProbe.Megabytes(source.ByteCount), record);

            // 方向口径与产品解码器一致:WIC 缺元数据时回退自有 EXIF。
            var orientation = declaredOrientation
                ?? ExifLoader.LoadExifDataAsync(file).GetAwaiter().GetResult()?.OrientationValue
                ?? 1;

            var proxySource = source;
            var scaleMs = 0.0;
            if (Math.Max(source.Width, source.Height) > WindowsPerfProbe.TargetLongEdgeA)
            {
                stage = "rawProxy5120Scale";
                watch.Restart();
                proxy = SkiaImageCodec.ScaleToLongEdge(source, WindowsPerfProbe.TargetLongEdgeA)
                    ?? throw new InvalidOperationException("Mitchell 精缩返回空");
                watch.Stop();
                scaleMs = watch.Elapsed.TotalMilliseconds;
                collector.Add(stage, scaleMs, proxy.Width, proxy.Height,
                    $"enc={proxy.Encoding} bytes={WindowsPerfProbe.Megabytes(proxy.ByteCount)}", record);
                proxySource = proxy;
            }
            else
            {
                collector.Skip("rawProxy5120Scale", "源长边 <= " + Num(WindowsPerfProbe.TargetLongEdgeA));
            }

            stage = "rawProxy5120ToScRgb16";
            watch.Restart();
            proxyF16 = ColorEngine.ToScRgb16(proxySource, source.SourceWidth, source.SourceHeight, orientation);
            watch.Stop();
            var proxyToScRgbMs = watch.Elapsed.TotalMilliseconds;
            collector.Add(stage, proxyToScRgbMs, proxyF16.Width, proxyF16.Height,
                $"enc={proxyF16.Encoding} bytes={WindowsPerfProbe.Megabytes(proxyF16.ByteCount)}", record);

            // Full 直出候选:源直接转 F16,Bgra 源与代理此刻都仍在驻留。
            stage = "rawFullToScRgb16";
            watch.Restart();
            fullF16 = ColorEngine.ToScRgb16(source, source.SourceWidth, source.SourceHeight, orientation);
            watch.Stop();
            var fullMs = watch.Elapsed.TotalMilliseconds;
            collector.Add(stage, fullMs, fullF16.Width, fullF16.Height,
                $"enc={fullF16.Encoding} bytes={WindowsPerfProbe.Megabytes(fullF16.ByteCount)}", record);

            var proxyPathMs = wicMs + scaleMs + proxyToScRgbMs;
            var fullPathMs = wicMs + fullMs;
            report.AppendLine($"   raw {name} wic={Num(wicMs)}ms src={source.Width}x{source.Height}"
                + "/" + source.Encoding + "/cs=" + source.ColorSpace + "/icc=" + source.IccProfile.Length
                + " bytes=" + WindowsPerfProbe.Megabytes(source.ByteCount));
            report.AppendLine($"   raw {name} proxyPath={Num(proxyPathMs)}ms = wic " + Num(wicMs)
                + " + scale " + Num(scaleMs) + " + f16 " + Num(proxyToScRgbMs)
                + " out=" + proxyF16.Width + "x" + proxyF16.Height
                + " f16=" + WindowsPerfProbe.Megabytes(proxyF16.ByteCount));
            report.AppendLine($"   raw {name} fullPath={Num(fullPathMs)}ms = wic " + Num(wicMs)
                + " + f16 " + Num(fullMs)
                + " out=" + fullF16.Width + "x" + fullF16.Height
                + " f16=" + WindowsPerfProbe.Megabytes(fullF16.ByteCount)
                + " delta=" + (scaleMs > 0 ? Num(fullPathMs - proxyPathMs) : "n/a") + "ms(full-proxy)");
            // 四份像素产物此刻确实并存;这只是像素产物账,不是进程峰值。
            var peakBytes = checked(source.ByteCount + (proxy?.ByteCount ?? 0) + proxyF16.ByteCount + fullF16.ByteCount);
            report.AppendLine("   raw " + name + " pixelProducts peak=" + WindowsPerfProbe.Megabytes(peakBytes)
                + "(src=" + WindowsPerfProbe.Megabytes(source.ByteCount)
                + " proxy8=" + WindowsPerfProbe.Megabytes(proxy?.ByteCount ?? 0)
                + " proxyF16=" + WindowsPerfProbe.Megabytes(proxyF16.ByteCount)
                + " fullF16=" + WindowsPerfProbe.Megabytes(fullF16.ByteCount)
                + "; 像素产物账,非进程峰值)");

            stage = "rawMatch";
            var proxyExact = Match("proxy", name, decoderProxy, proxyF16, report);
            var fullExact = Match("full", name, decoderFull, fullF16, report);
            return proxyExact && fullExact;
        }
        catch (Exception exception)
        {
            collector.Fail(stage, exception);
            report.AppendLine($"   raw {name} ERROR {stage} {exception.GetType().Name}: {exception.Message}");
            return false;
        }
        finally
        {
            fullF16?.Release();
            proxyF16?.Release();
            proxy?.Release();
            source?.Release();
        }
    }

    /// <summary>元数据逐项相等且有效像素逐行逐字节相同时判为一致。</summary>
    private static bool Match(string kind, string file, DecodedImage? expected, DecodedImage? actual, StringBuilder report)
    {
        if (expected is null || actual is null)
        {
            report.AppendLine($"   raw {file} match {kind}=MISSING(decoder={expected is null} probe={actual is null})");
            return false;
        }

        var meta = actual.Width == expected.Width && actual.Height == expected.Height
            && actual.SourceWidth == expected.SourceWidth && actual.SourceHeight == expected.SourceHeight
            && actual.Orientation == expected.Orientation && actual.ColorSpace == expected.ColorSpace
            && actual.Encoding == expected.Encoding
            && actual.IccProfile.Span.SequenceEqual(expected.IccProfile.Span);
        var byteExact = meta && SamePixels(actual, expected);
        report.AppendLine($"   raw {file} match {kind} meta=" + meta + " byteExact=" + byteExact
            + " size=" + actual.Width + "x" + actual.Height
            + " src=" + actual.SourceWidth + "x" + actual.SourceHeight
            + " orient=" + actual.Orientation + " cs=" + actual.ColorSpace
            + " icc=" + actual.IccProfile.Length);
        if (!meta)
        {
            report.AppendLine($"   raw {file} decoder {kind}"
                + " size=" + expected.Width + "x" + expected.Height
                + " src=" + expected.SourceWidth + "x" + expected.SourceHeight
                + " orient=" + expected.Orientation + " cs=" + expected.ColorSpace
                + " icc=" + expected.IccProfile.Length);
        }

        return byteExact;
    }

    /// <summary>只比有效像素(Width × 每像素字节),跳过 stride 对齐填充。</summary>
    private static bool SamePixels(DecodedImage a, DecodedImage b)
    {
        if (a.Width != b.Width || a.Height != b.Height || a.Encoding != b.Encoding) return false;

        var rowBytes = a.Width * DecodedImage.BytesPerPixel(a.Encoding);
        for (var row = 0; row < a.Height; row++)
        {
            if (!a.Pixels.AsSpan(row * a.Stride, rowBytes).SequenceEqual(b.Pixels.AsSpan(row * b.Stride, rowBytes)))
            {
                return false;
            }
        }

        return true;
    }

    private static string Num(double value) => value.ToString("F1", CultureInfo.InvariantCulture);
}
