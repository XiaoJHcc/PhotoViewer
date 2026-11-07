using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using PhotoViewer.Core.Color;
using PhotoViewer.Core.Image.Heif;

namespace HeifFastDecodeTest;

/// <summary>
/// 转换调度自检:平台注入共享有界调度器(<c>ConcurrentExclusiveSchedulerPair.ConcurrentScheduler</c>)后,
/// 逐像素直通与代理重采样的产物必须与未配置的串行 reference(<c>MaxDegreeOfParallelism = 1</c>、无调度器,
/// 直通走串行直通分支)逐有效像素字节一致;另覆盖多幅转换并发共用同一有界调度器,以及"度 1 + 非默认调度器"
/// 确实经过调度器而不退回串行直通分支。任何等待超时都说明转换可能仍在写合成平面,此时打印失败并立即结束进程,
/// 既不继续释放平面也不永久等待。
/// </summary>
internal static class SchedulingChecks
{
    private const int CanvasWidth = 1920;
    private const int CanvasHeight = 1440;
    private const int ProxyWidth = 960;
    private const int ProxyHeight = 720;

    /// <summary>有界调度器并发上限(等价 Android 上 cores-1 的小值)。</summary>
    private const int Concurrency = 2;

    /// <summary>同一配置重复次数:按行带的竞态会表现为两次结果不同。</summary>
    private const int Repeats = 2;

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(120);

    private static readonly Case[] Cases =
    [
        new("RGBA8 sRGB 直通", OutputKind.Rgba8Srgb, Downscale: false, TenBit: false),
        new("RGBA8 P3 直通(sRGB 源)", OutputKind.Rgba8P3, Downscale: false, TenBit: false),
        new("RGBA8 P3 直通(P3 10 位源)", OutputKind.Rgba8P3, Downscale: false, TenBit: true),
        new("BGRA32 直通", OutputKind.Bgra32, Downscale: false, TenBit: false),
        new("RGBA F16 直通", OutputKind.RgbaF16, Downscale: false, TenBit: true),
        new("RGBA8 sRGB 代理重采样", OutputKind.Rgba8Srgb, Downscale: true, TenBit: false),
        new("RGBA8 P3 代理重采样(sRGB 源)", OutputKind.Rgba8P3, Downscale: true, TenBit: false),
        new("RGBA8 P3 代理重采样(P3 10 位源)", OutputKind.Rgba8P3, Downscale: true, TenBit: true),
        new("BGRA32 代理重采样", OutputKind.Bgra32, Downscale: true, TenBit: false),
        new("RGBA F16 代理重采样", OutputKind.RgbaF16, Downscale: true, TenBit: true),
    ];

    /// <summary>运行全部调度用例。</summary>
    /// <returns>失败数。</returns>
    public static int Run()
    {
        var failures = 0;
        var handles = new List<GCHandle>(6);
        try
        {
            var srgb = BuildSource(CanvasWidth, CanvasHeight, bitDepth: 8, primaries: 1, handles);
            var p3 = BuildSource(CanvasWidth, CanvasHeight, bitDepth: 10, primaries: 12, handles);
            failures += Report("serial reference cases", WithScheduler(Concurrency,
                counting => VerifyCases("共享调度器", Cases, srgb, p3, counting, requireScheduling: false)));
            failures += Report("concurrent scheduler cases", ConcurrentConversions(srgb, p3));
            failures += Report("degree-1 scheduling cases", WithScheduler(1,
                counting => VerifyCases("度1", [Cases[0], Cases[7]], srgb, p3, counting, requireScheduling: true)));
        }
        catch (Exception exception)
        {
            failures += Fail($"调度用例异常 {exception.GetType().Name}: {exception.Message}");
        }
        finally
        {
            foreach (var handle in handles) handle.Free();
        }

        Console.WriteLine($"conversion scheduling: {(failures == 0 ? "ok" : "FAILED")}");
        return failures;
    }

    /// <summary>多幅转换并发共用同一有界调度器:产物仍与各自 reference 一致,并发不越过调度器上限。</summary>
    private static int ConcurrentConversions(YuvImage srgb, YuvImage p3)
    {
        var jobs = new[] { Cases[0], Cases[4], Cases[5], Cases[7] };
        return WithScheduler(Concurrency, counting =>
        {
            var references = new byte[jobs.Length][];
            var local = 0;
            for (var index = 0; index < jobs.Length; index++)
            {
                var reference = Reference(jobs[index], jobs[index].TenBit ? p3 : srgb);
                if (reference is null) local++;
                else references[index] = reference;
            }
            if (local > 0) return local;

            var results = new int[jobs.Length];
            var errors = new string?[jobs.Length];
            var tasks = jobs.Select((testCase, slot) => Task.Run(() =>
            {
                try
                {
                    results[slot] = ConvertAndCompare("并发", testCase, references[slot], testCase.TenBit ? p3 : srgb);
                }
                catch (Exception exception)
                {
                    errors[slot] = exception.GetType().Name + ": " + exception.Message;
                    results[slot] = 1;
                }
            })).ToArray();

            if (!Task.WhenAll(tasks).Wait(Timeout))
                FailHard($"{jobs.Length} 幅并发转换在 {Timeout.TotalSeconds:0}s 内未完成(疑似死锁)");

            local += results.Sum();
            foreach (var error in errors)
                if (error is not null) local += Fail($"并发转换失败 {error}");
            if (counting.Queued < jobs.Length)
                local += Fail($"调度器排队 {counting.Queued} 次 < 转换 {jobs.Length} 幅,未真正共用调度器");
            if (counting.Peak > Concurrency) local += Fail($"调度器并发峰值 {counting.Peak} 超过上限 {Concurrency}");
            if (local == 0)
                Console.WriteLine($"  ok 并发共用调度器: {jobs.Length} 幅转换 queued={counting.Queued} peak={counting.Peak}");
            return local;
        });
    }

    /// <summary>逐例算 reference 再在已套用的配置下比对;requireScheduling 时还要求该例确实经过调度器。</summary>
    private static int VerifyCases(string tag, Case[] jobs, YuvImage srgb, YuvImage p3, CountingScheduler counting,
        bool requireScheduling)
    {
        var local = 0;
        foreach (var testCase in jobs)
        {
            var source = testCase.TenBit ? p3 : srgb;
            var reference = Reference(testCase, source);
            if (reference is null)
            {
                local++;
                continue;
            }

            var before = counting.Queued;
            var mismatches = ConvertAndCompare(tag, testCase, reference, source);
            var queued = counting.Queued - before;
            if (requireScheduling && queued == 0) mismatches += Fail($"{tag} {testCase.Label}: 未经过 TaskScheduler,疑似走了串行直通分支");
            if (mismatches == 0)
            {
                Console.WriteLine($"  ok {tag} {testCase.Label}: {testCase.Width}×{testCase.Height} 与串行 reference 逐像素一致"
                    + (requireScheduling ? $",经调度器 queued={queued}" : string.Empty));
            }
            local += mismatches;
        }
        return local;
    }

    private static int ConvertAndCompare(string tag, Case testCase, byte[] reference, YuvImage source)
    {
        var mismatches = 0;
        for (var run = 1; run <= Repeats; run++)
        {
            var (pixels, error) = ConvertTimed(source, testCase);
            if (pixels is null) return mismatches + Fail($"{tag} {testCase.Label}: 第 {run} 次转换失败 {error}");
            mismatches += CheckPixels(testCase.Label, $"{tag}run{run}", reference, pixels, testCase);
        }
        return mismatches;
    }

    /// <summary>包一层并发上限固定的有界调度器并套用配置;body 同步等待结束后还原配置、Complete 并等 Completion。</summary>
    private static int WithScheduler(int maxDegree, Func<CountingScheduler, int> body)
    {
        var pair = new ConcurrentExclusiveSchedulerPair(TaskScheduler.Default, Concurrency);
        var counting = new CountingScheduler(pair.ConcurrentScheduler);
        try
        {
            return WithConfig(maxDegree, counting, () => body(counting));
        }
        finally
        {
            pair.Complete();
            if (!pair.Completion.Wait(Timeout)) FailHard($"调度器 Completion 超时(疑似死锁,度={maxDegree})");
        }
    }

    /// <summary>临时套用两静态配置,body 返回或抛出后还原原值。</summary>
    private static T WithConfig<T>(int maxDegree, TaskScheduler? scheduler, Func<T> body)
    {
        var previousDegree = YuvImageConverter.MaxDegreeOfParallelism;
        var previousScheduler = YuvImageConverter.ConversionTaskScheduler;
        YuvImageConverter.MaxDegreeOfParallelism = maxDegree;
        YuvImageConverter.ConversionTaskScheduler = scheduler;
        try
        {
            return body();
        }
        finally
        {
            YuvImageConverter.MaxDegreeOfParallelism = previousDegree;
            YuvImageConverter.ConversionTaskScheduler = previousScheduler;
        }
    }

    /// <summary>按当前静态配置转换一次;超时即终止进程(转换仍在写平面),失败返回原因。</summary>
    private static (byte[]? Pixels, string? Error) ConvertTimed(YuvImage source, Case testCase)
    {
        var task = Task.Run(() => ConvertRaw(source, testCase));
        try
        {
            if (!task.Wait(Timeout)) FailHard($"{testCase.Label}: 转换超时 {Timeout.TotalSeconds:0}s");
            return task.Result;
        }
        catch (AggregateException exception)
        {
            var inner = exception.GetBaseException();
            return (null, inner.GetType().Name + ": " + inner.Message);
        }
    }

    /// <summary>串行 reference(度 1、无调度器),并要求像素有足够变化以免比对失去意义。</summary>
    private static byte[]? Reference(Case testCase, YuvImage source)
    {
        var (pixels, error) = WithConfig(1, null, () => ConvertTimed(source, testCase));
        if (pixels is null)
        {
            Console.WriteLine($"  ! {testCase.Label}: 串行 reference 失败 {error}");
            return null;
        }

        var seen = new HashSet<byte>();
        for (var index = 0; index < pixels.Length && seen.Count < 8; index += 997) seen.Add(pixels[index]);
        if (seen.Count >= 8) return pixels;
        Console.WriteLine($"  ! {testCase.Label}: reference 像素变化不足,比对无意义");
        return null;
    }

    private static (byte[]? Pixels, string? Error) ConvertRaw(YuvImage source, Case testCase)
    {
        var width = testCase.Width;
        var height = testCase.Height;
        var stride = width * testCase.BytesPerPixel;
        var destination = new byte[stride * height];
        bool converted;
        string? reason;
        if (testCase.Output == OutputKind.Rgba8Srgb)
            converted = YuvImageConverter.TryConvertToRgba8(source, width, height, destination, stride, ImageColorSpace.Srgb, out reason);
        else if (testCase.Output == OutputKind.Rgba8P3)
            converted = YuvImageConverter.TryConvertToRgba8(source, width, height, destination, stride, ImageColorSpace.DisplayP3, out reason);
        else if (testCase.Output == OutputKind.Bgra32)
            converted = YuvImageConverter.TryConvertToBgra32(source, width, height, destination, stride, out reason);
        else
            converted = YuvImageConverter.TryConvertToRgbaLinearF16(source, width, height, destination, stride, out reason);
        return converted ? (destination, null) : (null, reason ?? "转换失败");
    }

    private static int CheckPixels(string label, string runLabel, byte[] reference, byte[] actual, Case testCase)
    {
        var rowBytes = testCase.Width * testCase.BytesPerPixel;
        for (var row = 0; row < testCase.Height; row++)
        {
            var offset = row * rowBytes;
            var referenceRow = reference.AsSpan(offset, rowBytes);
            var actualRow = actual.AsSpan(offset, rowBytes);
            var index = referenceRow.CommonPrefixLength(actualRow);
            if (index == rowBytes) continue;
            var pixel = index / testCase.BytesPerPixel;
            var channel = index % testCase.BytesPerPixel;
            return Fail($"{label} {runLabel}: 行 {row} 像素 {pixel} 通道 {channel} "
                + $"调度输出 {actualRow[index]} ≠ reference {referenceRow[index]}");
        }
        return 0;
    }

    /// <summary>超时转换可能仍在写合成平面:打印失败并结束进程,避免父作用域释放平面后再被访问。</summary>
    [DoesNotReturn]
    private static void FailHard(string message)
    {
        Console.WriteLine($"  ! {message}(超时,转换可能仍在写平面)");
        Console.WriteLine("conversion scheduling: FAILED(超时终止)");
        Console.Out.Flush();
        Environment.Exit(1);
    }

    private static int Fail(string message)
    {
        Console.WriteLine($"  ! {message}");
        return 1;
    }

    private static int Report(string label, int failures)
    {
        Console.WriteLine($"{label}: {(failures == 0 ? "ok" : "FAILED")}");
        return failures;
    }

    private enum OutputKind
    {
        Rgba8Srgb,
        Rgba8P3,
        Bgra32,
        RgbaF16,
    }

    private readonly record struct Case(string Label, OutputKind Output, bool Downscale, bool TenBit)
    {
        public int BytesPerPixel => Output == OutputKind.RgbaF16 ? 8 : 4;

        public int Width => Downscale ? ProxyWidth : CanvasWidth;

        public int Height => Downscale ? ProxyHeight : CanvasHeight;
    }

    /// <summary>合成 4:2:0 单瓦片输入:亮度斜向梯度、色度横纵满量程;平面句柄记进 handles,由 <see cref="Run"/> 释放。</summary>
    private static YuvImage BuildSource(int width, int height, int bitDepth, int primaries, List<GCHandle> handles)
    {
        var maxValue = (1 << bitDepth) - 1;
        var chromaWidth = (width + 1) / 2;
        var chromaHeight = (height + 1) / 2;
        var planes = new[]
        {
            Fill(width, height, bitDepth, (column, row) => ((column * 7) + (row * 13) + ((column >> 4) ^ (row >> 3))) & maxValue),
            Fill(chromaWidth, chromaHeight, bitDepth, (column, _) => (column * maxValue) / (chromaWidth - 1)),
            Fill(chromaWidth, chromaHeight, bitDepth, (_, row) => maxValue - ((row * maxValue) / (chromaHeight - 1))),
        };
        var pinned = new IntPtr[planes.Length];
        for (var index = 0; index < planes.Length; index++)
        {
            handles.Add(GCHandle.Alloc(planes[index], GCHandleType.Pinned));
            pinned[index] = handles[^1].AddrOfPinnedObject();
        }

        var sampleBytes = bitDepth > 8 ? 2 : 1;
        var chromaStride = Math.Max(4, chromaWidth) * sampleBytes;
        var tile = new YuvTile
        {
            Y = pinned[0], Cb = pinned[1], Cr = pinned[2],
            StrideY = width * sampleBytes, StrideCb = chromaStride, StrideCr = chromaStride,
            Width = width, Height = height,
        };
        return new YuvImage
        {
            Tiles = new[] { tile }, CanvasWidth = width, CanvasHeight = height, BitDepth = bitDepth,
            Chroma = HeifChromaSampling.Yuv420, MatrixCode = 1, PrimariesCode = primaries, TransferCode = 1,
        };
    }

    private static byte[] Fill(int width, int height, int bitDepth, Func<int, int, int> valueAt)
    {
        var sampleBytes = bitDepth > 8 ? 2 : 1;
        var stride = Math.Max(4, width) * sampleBytes;
        var plane = new byte[stride * height];
        for (var row = 0; row < height; row++)
        for (var column = 0; column < width; column++)
        {
            var value = valueAt(column, row);
            var index = (row * stride) + (column * sampleBytes);
            plane[index] = (byte)value;
            if (bitDepth > 8) plane[index + 1] = (byte)(value >> 8);
        }
        return plane;
    }

    /// <summary>转发到内层调度器并计数:证明转换确实经过所配置的调度器,且内层并发不越过上限。</summary>
    private sealed class CountingScheduler : TaskScheduler
    {
        private readonly TaskScheduler _inner;
        private int _queued, _running, _peak;

        public CountingScheduler(TaskScheduler inner) => _inner = inner;

        public int Queued => Volatile.Read(ref _queued);

        public int Peak => Volatile.Read(ref _peak);

        protected override void QueueTask(Task task)
        {
            Interlocked.Increment(ref _queued);
            Task.Factory.StartNew(() => Run(task), CancellationToken.None,
                TaskCreationOptions.DenyChildAttach, _inner);
        }

        protected override bool TryExecuteTaskInline(Task task, bool taskWasPreviouslyQueued) => false;

        protected override IEnumerable<Task>? GetScheduledTasks() => null;

        private void Run(Task task)
        {
            var current = Interlocked.Increment(ref _running);
            int peak;
            while (current > (peak = Volatile.Read(ref _peak))
                && Interlocked.CompareExchange(ref _peak, current, peak) != peak)
            {
            }
            try { TryExecuteTask(task); }
            finally { Interlocked.Decrement(ref _running); }
        }
    }
}
