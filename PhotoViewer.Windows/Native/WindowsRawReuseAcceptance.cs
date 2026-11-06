using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using PhotoViewer.Core.Color;
using PhotoViewer.Core.Image;
using PhotoViewer.Core.Memory;
using PhotoViewer.Core.Storage;
using PhotoViewer.Windows.Core;

namespace PhotoViewer.Windows.Native;

internal sealed partial class WindowsMainWindow
{
    /// <summary>WIC RAW 一次开发的有界复用与保留闸门验收(<c>--pv-native-color</c>),只读夹具。</summary>
    private static async Task CheckRawSourceReuseAsync(string path, List<string> checks)
    {
        var file = new LocalFileRef(path);
        var decoder = new WindowsPresentationDecoder();
        if (WindowsRawSourceCache.TryCreateKey(file, path) is not { } identity)
        {
            throw new InvalidOperationException("RAW 复用键建立失败");
        }

        var previousSnapshot = WindowsRawSourceCache.SnapshotForChecks;
        // 注入宽松预算快照:复用与闸门口径不随宿主预算档位、系统可用量漂移。
        WindowsRawSourceCache.SnapshotForChecks = Injected();
        try
        {
            WindowsRawSourceCache.ResetForChecks();

            long sourceBytes;
            string reuseSummary;
            using (var proxy = await decoder.DecodeAsync(file, 2560, PixelEncoding.RgbaF16, CancellationToken.None)
                ?? throw new InvalidOperationException("RAW 代理解码失败"))
            using (var full = await decoder.DecodeAsync(file, null, PixelEncoding.RgbaF16, CancellationToken.None)
                ?? throw new InvalidOperationException("RAW Full 解码失败"))
            {
                var developments = WindowsRawSourceCache.DevelopmentCount;
                RequireSmoke(developments == 1, $"RAW 冷代理 + 紧接 Full 的开发次数不是 1:{developments}");
                RequireSmoke(WindowsRawSourceCache.RetainedCountForChecks == 1, "RAW 源像素未进入有界复用");
                RequireSmoke(full.Width == full.SourceWidth && full.Height == full.SourceHeight, "RAW Full 未取得源分辨率");

                int? declared = null;
                using var reference = await Task.Run(() => WindowsRawDecoder.DecodeFull(path, CancellationToken.None, out declared))
                    ?? throw new InvalidOperationException("独立 WIC 参考解码失败");
                RequireSmoke(declared is null || full.Orientation == declared.Value, "RAW 方向口径与 WIC 声明不一致");
                using (var referenceProxy = await Task.Run(() => SkiaImageCodec.ScaleToLongEdge(reference, 2560))
                    ?? throw new InvalidOperationException("独立 WIC 参考代理精缩失败"))
                using (var expectedProxy = ColorEngine.ToScRgb16(referenceProxy,
                    reference.SourceWidth, reference.SourceHeight, full.Orientation))
                {
                    RequireSmoke(SameImageContent(proxy, expectedProxy),
                        $"RAW 代理与独立 WIC 派生不一致:{proxy.Width}x{proxy.Height} vs {expectedProxy.Width}x{expectedProxy.Height}");
                }

                using (var expectedFull = ColorEngine.ToScRgb16(reference,
                    reference.SourceWidth, reference.SourceHeight, full.Orientation))
                {
                    RequireSmoke(SameImageContent(full, expectedFull), "RAW Full 与独立 WIC 派生不一致");
                }

                sourceBytes = (long)full.SourceWidth * full.SourceHeight * DecodedImage.BytesPerPixel(PixelEncoding.Bgra32);
                var estimate = WindowsPresentationDecoder.EstimateScratchBytes(
                    (full.SourceWidth, full.SourceHeight), 2560);
                RequireSmoke(estimate.SourceBytes == sourceBytes
                    && estimate.ScaledBytes == (long)proxy.Width * proxy.Height * DecodedImage.BytesPerPixel(PixelEncoding.Bgra32),
                    $"RAW 临时占用估算与真实源几何/精缩尺寸不符:{estimate} vs {proxy.Width}x{proxy.Height}");
                reuseSummary = $"cold proxy={proxy.Width}x{proxy.Height} -> immediate Full={full.Width}x{full.Height}, "
                    + $"source BGRA {sourceBytes / 1048576}MB, scratch estimate "
                    + $"{(estimate.SourceBytes + estimate.ScaledBytes) / 1048576}MB";
            }

            checks.Add("PASS WIC RAW one-development reuse: developments=1, byte-exact/metadata-exact against an independent "
                + "WIC decode (" + reuseSummary + ")");

            // 并发同文件:先到的那条在开发,后到的那条等闸门时被取消;取消不打断开发、不触发第二次开发。
            WindowsRawSourceCache.Drop();
            var beforeConcurrent = WindowsRawSourceCache.DevelopmentCount;
            var first = decoder.DecodeAsync(file, null, PixelEncoding.RgbaF16, CancellationToken.None);
            var cancelledObserved = false;
            using (var cancelled = new CancellationTokenSource())
            {
                var second = decoder.DecodeAsync(file, null, PixelEncoding.RgbaF16, cancelled.Token);
                cancelled.CancelAfter(TimeSpan.FromMilliseconds(100));
                try
                {
                    using var ignored = await second;
                }
                catch (OperationCanceledException)
                {
                    cancelledObserved = true;
                }
            }

            RequireSmoke(cancelledObserved, "等待同文件开发的取消请求没有立即结束");
            using (var firstImage = await first ?? throw new InvalidOperationException("并发首请求解码失败"))
            {
                RequireSmoke(WindowsRawSourceCache.DevelopmentCount == beforeConcurrent + 1,
                    $"并发取消的请求触发了重复开发:{WindowsRawSourceCache.DevelopmentCount - beforeConcurrent} 次");
                RequireSmoke(WindowsRawSourceCache.RetainedCountForChecks == 1, "并发取消的请求把保留源一起释放了");
            }

            if (WindowsRawSourceCache.TryAcquire(identity) is not { } lease)
            {
                throw new InvalidOperationException("保留源无法再借用");
            }

            WindowsRawSourceCache.Drop();
            RequireSmoke(WindowsRawSourceCache.RetainedCountForChecks == 0, "清场未移除保留项");
            RequireSmoke(lease.Image.Pixels.Length > 0, "清场释放了别处仍在借用的源像素");
            lease.Image.Release();
            checks.Add("PASS WIC RAW reuse concurrency: same-file requests serialized, cancelling waiter abandons only its wait, "
                + "trim drops only the retained lease");

            // 保留 ≤ 3 秒:独立定时器到期释放,之后再请求必须重新开发。
            using (var warm = await decoder.DecodeAsync(file, null, PixelEncoding.RgbaF16, CancellationToken.None)
                ?? throw new InvalidOperationException("RAW 重新解码失败"))
            {
                RequireSmoke(WindowsRawSourceCache.RetainedCountForChecks == 1, "RAW 源像素未重新进入复用");
            }

            var beforeExpiry = WindowsRawSourceCache.DevelopmentCount;
            var expiryDeadline = DateTime.UtcNow + WindowsRawSourceCache.RetainDuration + TimeSpan.FromSeconds(3);
            while (WindowsRawSourceCache.RetainedCountForChecks != 0 && DateTime.UtcNow < expiryDeadline)
            {
                await Task.Delay(100);
            }

            RequireSmoke(WindowsRawSourceCache.RetainedCountForChecks == 0, "RAW 保留项超过保留时长仍未释放");
            using (var expired = await decoder.DecodeAsync(file, null, PixelEncoding.RgbaF16, CancellationToken.None)
                ?? throw new InvalidOperationException("RAW 过期后重新解码失败"))
            {
                RequireSmoke(WindowsRawSourceCache.DevelopmentCount == beforeExpiry + 1, "过期后仍命中了保留项");
            }

            checks.Add($"PASS WIC RAW bounded reuse: one retained source, expires after "
                + $"{WindowsRawSourceCache.RetainDuration.TotalSeconds:F0}s and re-develops");

            // 持续 Caution / 恢复观察期 / 预算不足:真实缓存不得保留,每次都重新开发。
            WindowsRawSourceCache.SnapshotForChecks = Injected(level: PressureLevel.Caution);
            var beforeCaution = WindowsRawSourceCache.DevelopmentCount;
            for (var round = 0; round < 2; round++)
            {
                await DecodeOnceAsync(decoder, file, null, "Caution 下的 RAW 解码失败");
            }

            RequireSmoke(WindowsRawSourceCache.RetainedCountForChecks == 0, "Caution 持续期间仍保留了 RAW 源像素");
            RequireSmoke(WindowsRawSourceCache.DevelopmentCount == beforeCaution + 2,
                $"Caution 持续期间命中了保留项:{WindowsRawSourceCache.DevelopmentCount - beforeCaution} 次开发");

            WindowsRawSourceCache.SnapshotForChecks = Injected(recoveryHold: true);
            await DecodeOnceAsync(decoder, file, null, "恢复观察期下的 RAW 解码失败");
            RequireSmoke(WindowsRawSourceCache.RetainedCountForChecks == 0, "恢复观察期仍保留了 RAW 源像素");

            WindowsRawSourceCache.SnapshotForChecks = Injected(budgetBytes: 1L << 30);
            await DecodeOnceAsync(decoder, file, null, "预算不足时的 RAW 解码失败");
            RequireSmoke(WindowsRawSourceCache.RetainedCountForChecks == 0, "预算 1/32 上限未拦住 RAW 保留");
            checks.Add("PASS WIC RAW retention gates on the real cache: sustained Caution, recovery hold and budget/32 ceiling "
                + "all keep the source out of the cache");

            WindowsRawSourceCache.SnapshotForChecks = Injected();

            // revision:修改时间与长度变化都不命中;副本改动,不动夹具。
            var copyDirectory = Path.Combine(SmokeOutputDirectory, "raw-reuse");
            Directory.CreateDirectory(copyDirectory);
            var copyPath = Path.Combine(copyDirectory, Path.GetFileName(path));
            File.Copy(path, copyPath, true);
            File.SetLastWriteTimeUtc(copyPath, DateTime.UtcNow.AddMinutes(-5));
            var copyFile = new LocalFileRef(copyPath);
            await DecodeOnceAsync(decoder, copyFile, null, "RAW 副本首次解码失败");

            RequireSmoke(WindowsRawSourceCache.RetainedCountForChecks == 1, "RAW 副本未进入复用");
            var copyDevelopments = WindowsRawSourceCache.DevelopmentCount;
            File.SetLastWriteTimeUtc(copyPath, DateTime.UtcNow);
            await DecodeOnceAsync(decoder, copyFile, null, "RAW 副本修改时间后解码失败");
            RequireSmoke(WindowsRawSourceCache.DevelopmentCount == copyDevelopments + 1, "修改时间变化后仍命中了保留项");

            if (WindowsRawSourceCache.TryCreateKey(copyFile, copyPath) is not { } copyKey)
            {
                throw new InvalidOperationException("RAW 副本键建立失败");
            }

            if (WindowsRawSourceCache.TryAcquire(copyKey) is not { } sameRevision)
            {
                throw new InvalidOperationException("未改动的 revision 未能命中保留项");
            }

            sameRevision.Image.Release();
            var forged = copyKey with
            {
                Revision = new StorageRevision((copyKey.Revision.Size ?? 0) + 1, copyKey.Revision.LastModified),
            };
            RequireSmoke(WindowsRawSourceCache.TryAcquire(forged) is null, "长度变化后仍命中了保留项");

            var otherPath = Path.GetFullPath(Path.Combine("Fixtures", "raw", "A7C01495.ARW"));
            if (File.Exists(otherPath))
            {
                var other = new LocalFileRef(otherPath);
                var beforeOther = WindowsRawSourceCache.DevelopmentCount;
                using var otherFull = await decoder.DecodeAsync(other, null, PixelEncoding.RgbaF16, CancellationToken.None)
                    ?? throw new InvalidOperationException("第二份 RAW 解码失败");
                RequireSmoke(WindowsRawSourceCache.DevelopmentCount == beforeOther + 1, "不同文件命中了别的文件的保留项");
            }
            else
            {
                checks.Add("SKIP WIC RAW reuse across files: 缺少第二份 RAW 夹具");
            }

            // 纯闸门口径:预算未知 / 超封顶 / 可用余量不足。
            long budget = Math.Max(1L << 30, sourceBytes * 64);
            RequireSmoke(WindowsRawSourceCache.ShouldRetain(sourceBytes, budget, PressureLevel.Normal, false, long.MaxValue),
                "正常态应保留 RAW 源像素");
            RequireSmoke(!WindowsRawSourceCache.ShouldRetain(sourceBytes, 0, PressureLevel.Normal, false, long.MaxValue),
                "预算未知仍保留 RAW 源像素");
            RequireSmoke(!WindowsRawSourceCache.ShouldRetain(WindowsRawSourceCache.RetainCeilingBytes + 1, budget,
                PressureLevel.Normal, false, long.MaxValue), "超过封顶仍保留 RAW 源像素");
            RequireSmoke(!WindowsRawSourceCache.ShouldRetain(sourceBytes, budget, PressureLevel.Normal, false, sourceBytes),
                "可用余量不足仍保留 RAW 源像素");
            checks.Add("PASS WIC RAW retention thresholds: capacity=min(256MiB, budget/32), unknown budget and "
                + "available-margin");
        }
        finally
        {
            WindowsRawSourceCache.SnapshotForChecks = previousSnapshot;
            WindowsRawSourceCache.ResetForChecks();
        }
    }

    private static WindowsRawSourceCache.Snapshot Injected(
        PressureLevel level = PressureLevel.Normal, bool recoveryHold = false, long budgetBytes = 8L << 30)
        => new(budgetBytes, level, recoveryHold, long.MaxValue);

    private static async Task DecodeOnceAsync(WindowsPresentationDecoder decoder, IFileRef file, int? maxLongEdge, string stage)
    {
        var image = await decoder.DecodeAsync(file, maxLongEdge, PixelEncoding.RgbaF16, CancellationToken.None)
            ?? throw new InvalidOperationException(stage);
        image.Release();
    }

    /// <summary>元数据逐项相等且有效像素逐行逐字节相同(跳过 stride 填充)。</summary>
    private static bool SameImageContent(DecodedImage actual, DecodedImage expected)
    {
        if (actual.Width != expected.Width || actual.Height != expected.Height
            || actual.SourceWidth != expected.SourceWidth || actual.SourceHeight != expected.SourceHeight
            || actual.Orientation != expected.Orientation || actual.ColorSpace != expected.ColorSpace
            || actual.Encoding != expected.Encoding
            || !actual.IccProfile.Span.SequenceEqual(expected.IccProfile.Span))
        {
            return false;
        }

        var rowBytes = actual.Width * DecodedImage.BytesPerPixel(actual.Encoding);
        for (var row = 0; row < actual.Height; row++)
        {
            if (!actual.Pixels.AsSpan(row * actual.Stride, rowBytes)
                .SequenceEqual(expected.Pixels.AsSpan(row * expected.Stride, rowBytes)))
            {
                return false;
            }
        }

        return true;
    }
}
