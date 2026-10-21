using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using PhotoViewer.Windows.Core.Heif;

namespace HeifFastDecodeTest;

/// <summary>解码后的一块瓦片平面(ffmpeg 或 pvhevc 产出)。</summary>
internal sealed unsafe class DecodedTile : IDisposable
{
    private GCHandle _handle;

    private DecodedTile(byte[] data, int width, int height, int bitDepth, HeifChromaSampling chroma)
    {
        _handle = GCHandle.Alloc(data, GCHandleType.Pinned);
        Width = width;
        Height = height;
        BitDepth = bitDepth;
        Chroma = chroma;
    }

    public int Width { get; }

    public int Height { get; }

    public int BitDepth { get; }

    public HeifChromaSampling Chroma { get; }

    public static DecodedTile FromRaw(byte[] data, int width, int height, int bitDepth, HeifChromaSampling chroma)
        => new(data, width, height, bitDepth, chroma);

    /// <summary>把平面转成转换器需要的瓦片视图(ispe 尺寸用于画布裁剪,CodedWidth 用于跨距)。</summary>
    public PhotoViewer.Windows.Core.Heif.YuvTile ToTile(int column, int row, int ispeWidth, int ispeHeight)
    {
        var bytesPerSample = BitDepth > 8 ? 2 : 1;
        var basePointer = (byte*)_handle.AddrOfPinnedObject();
        long ySize = (long)Width * Height * bytesPerSample;
        long chromaWidth = Chroma switch
        {
            HeifChromaSampling.Monochrome => 0,
            HeifChromaSampling.Yuv420 => (Width + 1) / 2,
            HeifChromaSampling.Yuv422 => (Width + 1) / 2,
            _ => Width,
        };
        long chromaHeight = Chroma == HeifChromaSampling.Yuv420 ? (Height + 1) / 2 : Height;
        var cb = basePointer + ySize;
        var cr = cb + (chromaWidth * chromaHeight * bytesPerSample);

        return new PhotoViewer.Windows.Core.Heif.YuvTile
        {
            Y = (IntPtr)basePointer,
            Cb = Chroma == HeifChromaSampling.Monochrome ? IntPtr.Zero : (IntPtr)cb,
            Cr = Chroma == HeifChromaSampling.Monochrome ? IntPtr.Zero : (IntPtr)cr,
            StrideY = (int)(Width * bytesPerSample),
            StrideCb = (int)(chromaWidth * bytesPerSample),
            StrideCr = (int)(chromaWidth * bytesPerSample),
            Column = column,
            Row = row,
            Width = ispeWidth,
            Height = ispeHeight,
        };
    }

    public void Dispose()
    {
        if (_handle.IsAllocated) _handle.Free();
    }
}

/// <summary>用 ffmpeg 把 Annex-B 码流解成原始 YUV 平面。</summary>
internal static class FfmpegDecoder
{
    public static (int Width, int Height, string PixelFormat)? Probe(string hevcPath)
    {
        var start = new ProcessStartInfo("ffprobe")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        start.ArgumentList.Add("-v");
        start.ArgumentList.Add("error");
        start.ArgumentList.Add("-select_streams");
        start.ArgumentList.Add("v:0");
        start.ArgumentList.Add("-show_entries");
        start.ArgumentList.Add("frame=width,height,pix_fmt");
        start.ArgumentList.Add("-of");
        start.ArgumentList.Add("csv=p=0");
        start.ArgumentList.Add(hevcPath);

        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEnd();
        process.StandardError.ReadToEnd();
        process.WaitForExit();
        var line = output.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        if (line is null) return null;
        var parts = line.Trim().Split(',');
        if (parts.Length < 3) return null;
        if (!int.TryParse(parts[0], out var width) || !int.TryParse(parts[1], out var height)) return null;
        return (width, height, parts[2]);
    }

    public static DecodedTile? Decode(string hevcPath, int threads)
    {
        var probe = Probe(hevcPath);
        if (probe is null)
        {
            Console.Error.WriteLine($"  ! ffprobe failed for {hevcPath}");
            return null;
        }

        var (width, height, pixFmt) = probe.Value;
        var (bitDepth, chroma) = ParsePixelFormat(pixFmt);

        var start = new ProcessStartInfo("ffmpeg")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        start.ArgumentList.Add("-v");
        start.ArgumentList.Add("error");
        start.ArgumentList.Add("-threads");
        start.ArgumentList.Add(threads.ToString());
        start.ArgumentList.Add("-i");
        start.ArgumentList.Add(hevcPath);
        start.ArgumentList.Add("-frames:v");
        start.ArgumentList.Add("1");
        start.ArgumentList.Add("-f");
        start.ArgumentList.Add("rawvideo");
        start.ArgumentList.Add("-pix_fmt");
        start.ArgumentList.Add(pixFmt);
        start.ArgumentList.Add("-");

        using var process = Process.Start(start)!;
        using var buffer = new MemoryStream();
        var readTask = process.StandardOutput.BaseStream.CopyToAsync(buffer);
        var error = process.StandardError.ReadToEndAsync();
        readTask.Wait();
        process.WaitForExit();
        if (process.ExitCode != 0)
        {
            Console.Error.WriteLine($"  ! ffmpeg failed for {hevcPath}: {error.Result}");
            return null;
        }

        return DecodedTile.FromRaw(buffer.ToArray(), width, height, bitDepth, chroma);
    }

    public static (int BitDepth, HeifChromaSampling Chroma) ParsePixelFormat(string pixelFormat)
    {
        var bits = pixelFormat.Contains("10le", StringComparison.Ordinal) ? 10
            : pixelFormat.Contains("12le", StringComparison.Ordinal) ? 12
            : 8;
        var chroma = pixelFormat.StartsWith("gray", StringComparison.Ordinal) ? HeifChromaSampling.Monochrome
            : pixelFormat.Contains("444", StringComparison.Ordinal) ? HeifChromaSampling.Yuv444
            : pixelFormat.Contains("422", StringComparison.Ordinal) ? HeifChromaSampling.Yuv422
            : HeifChromaSampling.Yuv420;
        return (bits, chroma);
    }
}

/// <summary>解码整张图:把每块瓦片写到临时目录,再并行调用 ffmpeg。</summary>
internal static class TileDecodeHarness
{
    /// <summary>用哪个解码器取得瓦片平面。</summary>
    public enum Backend
    {
        /// <summary>ffmpeg 命令行(带 .hevc 临时文件)。</summary>
        Ffmpeg,

        /// <summary><c>pvhevc.dll</c>(内存解码,一上下文一线程)。</summary>
        PvHevc,
    }

    /// <summary>选择可用后端:优先 pvhevc,缺失时回落 ffmpeg。</summary>
    public static Backend Preferred => PvHevc.IsAvailable ? Backend.PvHevc : Backend.Ffmpeg;

    /// <summary>解码全部瓦片;失败返回 null。临时 .hevc 文件保留给调用方清理。</summary>
    public static DecodedTile[]? DecodeAll(
        PhotoViewer.Windows.Core.Heif.HeifContainer container,
        string tempDirectory,
        bool parallel,
        Backend backend = Backend.Ffmpeg)
    {
        DecodedTile?[] results;
        if (backend == Backend.PvHevc && PvHevc.IsAvailable)
        {
            results = new DecodedTile?[container.Tiles.Count];
            if (parallel)
            {
                Parallel.For(0, results.Length, i => results[i] = PvHevc.Decode(container.Tiles[i].AnnexB));
            }
            else
            {
                for (var i = 0; i < results.Length; i++) results[i] = PvHevc.Decode(container.Tiles[i].AnnexB);
            }
        }
        else
        {
            Directory.CreateDirectory(tempDirectory);
            var paths = new string[container.Tiles.Count];
            for (var i = 0; i < container.Tiles.Count; i++)
            {
                paths[i] = Path.Combine(tempDirectory, $"tile{i}.hevc");
                File.WriteAllBytes(paths[i], container.Tiles[i].AnnexB);
            }

            results = new DecodedTile?[paths.Length];
            if (parallel)
            {
                Parallel.For(0, paths.Length, i => results[i] = FfmpegDecoder.Decode(paths[i], 1));
            }
            else
            {
                for (var i = 0; i < paths.Length; i++) results[i] = FfmpegDecoder.Decode(paths[i], 1);
            }
        }

        var output = new DecodedTile[container.Tiles.Count];
        for (var i = 0; i < output.Length; i++)
        {
            if (results[i] is null)
            {
                foreach (var tile in results) tile?.Dispose();
                return null;
            }
            output[i] = results[i]!;
        }
        return output;
    }

    public static string Describe(DecodedTile[] tiles)
    {
        var builder = new StringBuilder();
        for (var i = 0; i < tiles.Length; i++)
        {
            if (i > 0) builder.Append(", ");
            builder.Append($"{tiles[i].Width}x{tiles[i].Height}/{tiles[i].BitDepth}bit/{tiles[i].Chroma}");
        }
        return builder.ToString();
    }
}

/// <summary>数值比较工具。</summary>
internal static class Metrics
{
    /// <summary>逐通道平均绝对差与最大差(BGRA 缓冲,忽略 A)。</summary>
    public static (double Mean, int Max, double MeanB, double MeanG, double MeanR) CompareBgra(
        byte[] a,
        byte[] b,
        int width,
        int height)
    {
        long sumB = 0, sumG = 0, sumR = 0;
        var max = 0;
        long count = 0;
        for (var y = 0; y < height; y++)
        {
            var row = y * width * 4;
            for (var x = 0; x < width; x++)
            {
                var index = row + (x * 4);
                var db = Math.Abs(a[index] - b[index]);
                var dg = Math.Abs(a[index + 1] - b[index + 1]);
                var dr = Math.Abs(a[index + 2] - b[index + 2]);
                sumB += db;
                sumG += dg;
                sumR += dr;
                var localMax = Math.Max(db, Math.Max(dg, dr));
                if (localMax > max) max = localMax;
                count += 3;
            }
        }
        var mean = (sumB + sumG + sumR) / (double)count;
        return (mean, max, sumB / (double)(width * height), sumG / (double)(width * height), sumR / (double)(width * height));
    }

    /// <summary>绝对值误差的平均与 99.9 分位(0–255 直方图)。</summary>
    public static (double Mean, int Percentile999, int Max) Statistics(byte[] a, byte[] b, int width, int height)
    {
        var histogram = new long[256];
        long sum = 0;
        long count = 0;
        for (var y = 0; y < height; y++)
        {
            var row = y * width * 4;
            for (var x = 0; x < width; x++)
            {
                var index = row + (x * 4);
                for (var channel = 0; channel < 3; channel++)
                {
                    var difference = Math.Abs(a[index + channel] - b[index + channel]);
                    histogram[difference]++;
                    sum += difference;
                    count++;
                }
            }
        }

        var target = (long)(count * 0.999);
        long running = 0;
        var percentile = 0;
        var max = 0;
        for (var value = 0; value < 256; value++)
        {
            if (histogram[value] == 0) continue;
            running += histogram[value];
            max = value;
            if (running >= target && percentile == 0 && value > 0) percentile = value;
        }
        return (sum / (double)count, percentile, max);
    }
}
