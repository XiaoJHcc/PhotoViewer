using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using PhotoViewer.Core.Color;
using PhotoViewer.Core.Image;
using PhotoViewer.Core.Memory;

namespace PhotoViewer.Apple;

/// <summary>
/// 纹理缓存验收：真实 Metal 上传 + 线性帧读回核对命中、淘汰、清空、压力修剪、并发与 A/B 切换计时。
/// 像素全部是本进程合成的内存图，不读写用户照片、不写文件；结果以文本报告返回。
/// </summary>
public static class AppleTextureCacheChecks
{
    private const int TimingRounds = 9;

    /// <summary>跑完整套纹理缓存验收；单项失败只记入报告，不中断后续检查。</summary>
    public static string Run()
    {
        var lines = new List<string>
        {
            "Apple 纹理缓存验收",
            $"压力级别 {MemoryGovernor.CurrentLevel} / 恢复观察期 {MemoryGovernor.InRecoveryHold}",
        };
        int passed = 0, failed = 0;

        void Check(string name, Action<List<string>> body)
        {
            var detail = new List<string>();
            try
            {
                body(detail);
                passed++;
                lines.Add($"PASS {name}");
            }
            catch (Exception ex)
            {
                failed++;
                lines.Add($"FAIL {name}: {ex.Message}");
            }
            lines.AddRange(detail);
        }

        Check("F16 扩展色域（负值与 >1）1:1 读回", detail =>
        {
            using var renderer = NewRenderer(out var viewport);
            using var image = CreateLinearImage(32, 24, 0, (x, y, w, h) => (
                -0.25f + 1.75f * x / (w - 1),
                1.5f * y / (h - 1) - 0.1f,
                -0.5f + 2f * x / (w - 1)));
            Present(renderer, viewport, image);
            using var frame = renderer.CaptureLinearFrame(false);
            Assert(frame.Encoding == PixelEncoding.RgbaF16, $"读回编码 {frame.Encoding}");
            Assert(frame.Width == image.Width && frame.Height == image.Height, $"读回尺寸 {frame.Width}×{frame.Height}");
            foreach (var (x, y) in new[] { (0, 0), (31, 0), (0, 23), (31, 23), (16, 12) })
            {
                var expected = ReadLinear(image, x, y);
                var actual = ReadLinear(frame, x, y);
                Near($"像素({x},{y}) R", expected.R, actual.R);
                Near($"像素({x},{y}) G", expected.G, actual.G);
                Near($"像素({x},{y}) B", expected.B, actual.B);
            }
            Assert(renderer.UploadCount == 1, $"首次上屏应上传一次，实得 {renderer.UploadCount}");
            long expectedBytes = (long)image.Width * image.Height * 8 * 4 / 3;
            Assert(renderer.TextureBytes == expectedBytes, $"纹理字节 {renderer.TextureBytes} 未按含 mipmap 的 {expectedBytes} 记账");
            detail.Add($"  uploads={renderer.UploadCount} bytes={renderer.TextureBytes}");
        });

        Check("行距填充不串行（F16 与 8 位 P3）", detail =>
        {
            using var renderer = NewRenderer(out var viewport);
            using var linear = CreateLinearImage(7, 5, 24, (x, y, w, h) => (0.1f * x - 0.3f, 0.2f * y - 0.1f, 0.15f * (x - y)));
            using var packed = CreateRgba8Image(5, 4, 12, (x, y) =>
            {
                byte level = (byte)(32 + 16 * (x + y));
                return (level, level, level);
            });
            Present(renderer, viewport, linear);
            using (var frame = renderer.CaptureLinearFrame(false))
                for (int y = 0; y < linear.Height; y++)
                    for (int x = 0; x < linear.Width; x++)
                    {
                        var expected = ReadLinear(linear, x, y);
                        var actual = ReadLinear(frame, x, y);
                        Near($"F16 行距({x},{y}) R", expected.R, actual.R);
                        Near($"F16 行距({x},{y}) G", expected.G, actual.G);
                        Near($"F16 行距({x},{y}) B", expected.B, actual.B);
                    }
            Present(renderer, viewport, packed);
            using (var frame = renderer.CaptureLinearFrame(false))
                for (int y = 0; y < packed.Height; y++)
                    for (int x = 0; x < packed.Width; x++)
                    {
                        float expected = SrgbToLinear((32 + 16 * (x + y)) / 255f);
                        var actual = ReadLinear(frame, x, y);
                        Near($"P3 行距({x},{y}) R", expected, actual.R, 4e-3f);
                        Near($"P3 行距({x},{y}) G", expected, actual.G, 4e-3f);
                        Near($"P3 行距({x},{y}) B", expected, actual.B, 4e-3f);
                    }
            detail.Add("  两份产物的 stride 都大于宽 × 每像素字节，行尾填充未进入画面");
        });

        Check("8 位 Display P3 与编码切换", detail =>
        {
            using var renderer = NewRenderer(out var viewport);
            using var linear = CreateLinearImage(16, 16, 0, (x, y, w, h) => (0.4f, 0.6f, 0.2f));
            using var p3 = CreateRgba8Image(16, 16, 0, (x, y) => x < 8 ? ((byte)255, (byte)0, (byte)0) : ((byte)128, (byte)128, (byte)128));
            Present(renderer, viewport, linear);
            using (var frame = renderer.CaptureLinearFrame(false))
            {
                var value = ReadLinear(frame, 8, 8);
                Near("F16 灰 R", .4f, value.R);
                Near("F16 灰 G", .6f, value.G);
                Near("F16 灰 B", .2f, value.B);
            }
            long uploads = renderer.UploadCount;
            Present(renderer, viewport, p3);
            using (var frame = renderer.CaptureLinearFrame(false))
            {
                var red = ReadLinear(frame, 4, 8);
                Near("P3 红 R", MetalPhotoRenderer.P3ToExtendedLinearSrgb[0], red.R, 1e-2f);
                Near("P3 红 G", MetalPhotoRenderer.P3ToExtendedLinearSrgb[3], red.G, 1e-2f);
                Near("P3 红 B", MetalPhotoRenderer.P3ToExtendedLinearSrgb[6], red.B, 1e-2f);
                float gray = SrgbToLinear(128 / 255f);
                var neutral = ReadLinear(frame, 12, 8);
                Near("P3 灰 R", gray, neutral.R, 1e-2f);
                Near("P3 灰 G", gray, neutral.G, 1e-2f);
                Near("P3 灰 B", gray, neutral.B, 1e-2f);
            }
            Assert(renderer.UploadCount == uploads + 1, $"P3 产物应上传一次，实增 {renderer.UploadCount - uploads}");
            Present(renderer, viewport, linear);
            Assert(renderer.UploadCount == uploads + 1, $"切回同一 F16 产物应命中缓存，实增上传 {renderer.UploadCount - uploads} 次");
            using (var frame = renderer.CaptureLinearFrame(false))
            {
                var value = ReadLinear(frame, 8, 8);
                Near("切回 F16 R", .4f, value.R);
                Near("切回 F16 G", .6f, value.G);
                Near("切回 F16 B", .2f, value.B);
            }
            detail.Add("  8 位 P3 经片段矩阵换算；缓存命中切回不重复上传且片段管线正确切回线性");
        });

        Check("八方向 GPU 变换", detail =>
        {
            using var renderer = NewRenderer(out var viewport);
            using var quad = CreateQuadrantImage(128);
            for (int orientation = 1; orientation <= 8; orientation++)
            {
                Present(renderer, viewport, quad, orientation);
                AppleAcceptanceChecks.VerifyOrientation(navigation => renderer.CaptureLinearFrame(navigation), orientation);
            }
            Assert(renderer.UploadCount == 1, $"同一产物换方向应复用纹理，实得上传 {renderer.UploadCount} 次");
            detail.Add("  1–8 方向角点一致；方向变化只改顶点参数，不重新上传");
        });

        Check("A-B-A 与同实例热通知命中", detail =>
        {
            RequireCacheable();
            using var renderer = NewRenderer(out _);
            using var a = SolidLinear(64, 48, .5f, 0, 0);
            using var b = SolidLinear(64, 48, 0, .5f, 0);
            renderer.SetTexture(a);
            renderer.SetTexture(b);
            Assert(renderer.UploadCount == 2, $"两张产物应上传两次，实得 {renderer.UploadCount}");
            long perImage = (long)a.Width * a.Height * 8 * 4 / 3;
            Assert(renderer.TextureBytes == 2 * perImage, $"两份纹理应记 {2 * perImage} 字节，实得 {renderer.TextureBytes}");
            renderer.SetTexture(a);
            Assert(renderer.UploadCount == 2, $"A-B-A 第三张应命中缓存，实得上传 {renderer.UploadCount}");
            Assert(renderer.CacheHitCount == 1, $"命中计数应为 1，实得 {renderer.CacheHitCount}");
            Assert(renderer.TextureWidth == a.Width, $"当前纹理宽 {renderer.TextureWidth}");
            renderer.SetTexture(a);
            Assert(renderer.UploadCount == 2, $"同实例重复上屏不应重复上传，实得 {renderer.UploadCount}");
            Assert(renderer.CacheHitCount == 2, $"同实例重复上屏应计命中，实得 {renderer.CacheHitCount}");
            detail.Add($"  uploads={renderer.UploadCount} hits={renderer.CacheHitCount} bytes={renderer.TextureBytes}");
        });

        Check("第三/第四张按 LRU 淘汰", detail =>
        {
            RequireCacheable();
            using var renderer = NewRenderer(out _);
            using var a = SolidLinear(64, 48, .5f, 0, 0);
            using var b = SolidLinear(64, 48, 0, .5f, 0);
            using var c = SolidLinear(64, 48, 0, 0, .5f);
            using var d = SolidLinear(64, 48, .5f, .5f, 0);
            renderer.SetTexture(a);
            renderer.SetTexture(b);
            renderer.SetTexture(c);
            Assert(renderer.CachedTextureCount == 2, $"最近缓存应有 2 份，实得 {renderer.CachedTextureCount}");
            renderer.SetTexture(d);
            Assert(renderer.UploadCount == 4, $"第四张应上传，实得 {renderer.UploadCount}");
            Assert(renderer.CachedTextureCount == 2, $"容量应恒为 2，实得 {renderer.CachedTextureCount}");
            long uploads = renderer.UploadCount;
            renderer.SetTexture(a);
            Assert(renderer.UploadCount == uploads + 1, "最旧的 A 应已被淘汰，需要重新上传");
            renderer.SetTexture(d);
            Assert(renderer.UploadCount == uploads + 1, $"D 仍在缓存，应命中，实增上传 {renderer.UploadCount - uploads}");
            detail.Add($"  容量恒为 2；uploads={renderer.UploadCount} hits={renderer.CacheHitCount}");
        });

        Check("最近缓存字节上限边界", detail =>
        {
            RequireCacheable();
            long imageBytes = 512L * 512 * 8 * 4 / 3;
            using (var renderer = NewRenderer(out _, 2L << 20))
            {
                using var a = SolidLinear(512, 512, .5f, 0, 0);
                using var b = SolidLinear(512, 512, 0, .5f, 0);
                renderer.SetTexture(a);
                renderer.SetTexture(b);
                Assert(renderer.CachedTextureCount == 0, $"单张 ({imageBytes}) 已超 2MB 上限时不得缓存，实得 {renderer.CachedTextureCount}");
                Assert(renderer.TextureBytes == imageBytes, $"应只记当前纹理 {imageBytes}，实得 {renderer.TextureBytes}");
                long uploads = renderer.UploadCount;
                renderer.SetTexture(a);
                Assert(renderer.UploadCount == uploads + 1, "超限纹理被复用了");
            }
            using (var renderer = NewRenderer(out _, 4L << 20))
            {
                using var a = SolidLinear(512, 512, .5f, 0, 0);
                using var b = SolidLinear(512, 512, 0, .5f, 0);
                using var c = SolidLinear(512, 512, 0, 0, .5f);
                renderer.SetTexture(a);
                renderer.SetTexture(b);
                Assert(renderer.CachedTextureCount == 1, $"单张 ({imageBytes}) 可缓存，实得 {renderer.CachedTextureCount}");
                renderer.SetTexture(c);
                Assert(renderer.CachedTextureCount == 1, $"两张合计超 4MB 上限，应只留 1 份，实得 {renderer.CachedTextureCount}");
                Assert(renderer.TextureBytes == 2 * imageBytes, $"应记当前 + 1 份缓存，实得 {renderer.TextureBytes}");
            }
            detail.Add($"  单张 {imageBytes} 字节：2MB 上限不缓存，4MB 上限只留 1 份");
        });

        Check("null 清空、黑屏与回切命中", detail =>
        {
            RequireCacheable();
            using var renderer = NewRenderer(out var viewport);
            using var image = CreateLinearImage(32, 24, 0, (x, y, w, h) => (0.3f, 0.7f, 0.1f));
            Present(renderer, viewport, image);
            long uploads = renderer.UploadCount;
            long bytes = renderer.TextureBytes;
            renderer.SetTexture(null);
            Assert(renderer.TextureWidth == 0, $"清空后纹理宽应归零，实得 {renderer.TextureWidth}");
            Assert(renderer.TextureBytes == bytes, "清空应短期保留最近纹理供回切");
            using (var frame = renderer.CaptureLinearFrame(false))
            {
                var value = ReadLinear(frame, 16, 12);
                Near("清空后 R", 0, value.R, 1e-4f);
                Near("清空后 G", 0, value.G, 1e-4f);
                Near("清空后 B", 0, value.B, 1e-4f);
            }
            renderer.SetTexture(image);
            Assert(renderer.UploadCount == uploads, $"清空后同一产物回来应命中缓存，实增上传 {renderer.UploadCount - uploads}");
            using (var frame = renderer.CaptureLinearFrame(false))
            {
                var value = ReadLinear(frame, 16, 12);
                Near("回切 R", .3f, value.R);
                Near("回切 G", .7f, value.G);
                Near("回切 B", .1f, value.B);
            }
            detail.Add($"  黑屏不复活旧图；回切命中，uploads={renderer.UploadCount}");
        });

        Check("失败输入保留旧帧", detail =>
        {
            using var renderer = NewRenderer(out var viewport);
            using var good = CreateLinearImage(24, 16, 0, (x, y, w, h) => (0.2f, 0.4f, 0.6f));
            Present(renderer, viewport, good);
            long uploads = renderer.UploadCount;
            long bytes = renderer.TextureBytes;
            using (var unsupported = new DecodedImage(new byte[24 * 16 * 4], 24, 16, 24 * 4, PixelEncoding.Rgba8, ImageColorSpace.Srgb))
            {
                bool threw = false;
                try { renderer.SetTexture(unsupported); }
                catch (NotSupportedException) { threw = true; }
                Assert(threw, "不支持的展示编码未被拒绝");
            }
            Assert(renderer.TextureWidth == good.Width && renderer.UploadCount == uploads && renderer.TextureBytes == bytes,
                "上传失败改变了当前纹理或记账");
            using (var frame = renderer.CaptureLinearFrame(false))
            {
                var value = ReadLinear(frame, 12, 8);
                Near("失败后 R", .2f, value.R);
                Near("失败后 G", .4f, value.G);
                Near("失败后 B", .6f, value.B);
            }
            var released = CreateLinearImage(24, 16, 0, (x, y, w, h) => (0.9f, 0.1f, 0.1f));
            renderer.SetTexture(released);
            released.Release();
            bool rejected = false;
            try { renderer.SetTexture(released); }
            catch (InvalidOperationException) { rejected = true; }
            Assert(rejected, "已释放像素的实例未被拒绝");
            using (var frame = renderer.CaptureLinearFrame(false))
            {
                var value = ReadLinear(frame, 12, 8);
                Near("像素释放后 R", .9f, value.R);
                Near("像素释放后 G", .1f, value.G);
            }
            detail.Add("  不支持编码与已释放像素都只拒绝本次上传，屏上旧帧保持可读");
        });

        Check("Trim 丢弃非当前纹理且不清屏", detail =>
        {
            RequireCacheable();
            using var renderer = NewRenderer(out var viewport);
            using var a = SolidLinear(32, 24, .25f, 0, 0);
            using var b = SolidLinear(32, 24, 0, .25f, 0);
            Present(renderer, viewport, a);
            Present(renderer, viewport, b);
            Assert(renderer.CachedTextureCount == 1, $"应缓存 1 份非当前纹理，实得 {renderer.CachedTextureCount}");
            long total = renderer.TextureBytes;
            renderer.RequestTrim(PressureLevel.Caution);
            renderer.ApplyPendingTrim();
            Assert(renderer.CachedTextureCount == 0, $"Caution 应清空非当前纹理，实得 {renderer.CachedTextureCount}");
            Assert(renderer.TextureBytes < total, $"Trim 后字节应减少（{renderer.TextureBytes} / {total}）");
            Assert(renderer.TextureWidth == b.Width, "当前纹理必须保留");
            using (var frame = renderer.CaptureLinearFrame(false))
            {
                var value = ReadLinear(frame, 16, 12);
                Near("Trim 后 G", .25f, value.G);
            }
            renderer.SetTexture(null);
            renderer.RequestTrim(PressureLevel.Critical);
            renderer.ApplyPendingTrim();
            Assert(renderer.TextureWidth == 0 && renderer.TextureBytes == 0, "空图不得被 Trim 复活");
            using (var frame = renderer.CaptureLinearFrame(false))
            {
                var value = ReadLinear(frame, 16, 12);
                Near("空图 Trim 后 G", 0, value.G, 1e-4f);
            }
            long uploads = renderer.UploadCount;
            renderer.SetTexture(b);
            Assert(renderer.UploadCount == uploads + 1, "被 Trim 掉的纹理不得复用");
            detail.Add("  当前纹理豁免；空图被 Trim 后仍是黑屏，旧纹理不会被拼回");
        });

        Check("缓存策略随压力（Normal 两份 / 否则零份）", detail =>
        {
            using var renderer = NewRenderer(out _);
            using var a = SolidLinear(64, 48, .5f, 0, 0);
            using var b = SolidLinear(64, 48, 0, .5f, 0);
            using var c = SolidLinear(64, 48, 0, 0, .5f);
            bool cacheable = MemoryGovernor.CurrentLevel == PressureLevel.Normal && !MemoryGovernor.InRecoveryHold;
            renderer.SetTexture(a);
            renderer.SetTexture(b);
            renderer.SetTexture(c);
            int expected = cacheable ? 2 : 0;
            Assert(renderer.CachedTextureCount == expected,
                $"压力 {MemoryGovernor.CurrentLevel} 下期望缓存 {expected} 份，实得 {renderer.CachedTextureCount}");
            detail.Add($"  可缓存={cacheable}（压力 {MemoryGovernor.CurrentLevel}，观察期 {MemoryGovernor.InRecoveryHold}）");
        });

        Check("并发 Trim 压力下 API 串行", detail =>
        {
            using var renderer = NewRenderer(out var viewport);
            using var a = CreateLinearImage(96, 64, 0, (x, y, w, h) => (0.3f, 0.5f, 0.1f));
            using var b = CreateLinearImage(96, 64, 0, (x, y, w, h) => (0.1f, 0.2f, 0.6f));
            Present(renderer, viewport, a);
            int failures = 0;
            var workers = new List<Thread>();
            for (int t = 0; t < 3; t++)
            {
                var worker = new Thread(() =>
                {
                    try
                    {
                        for (int i = 0; i < 150; i++)
                        {
                            renderer.RequestTrim(PressureLevel.Caution);
                            renderer.ApplyPendingTrim();
                            renderer.ApplyPendingTrim();
                        }
                    }
                    catch
                    {
                        Interlocked.Increment(ref failures);
                    }
                })
                { IsBackground = true };
                worker.Start();
                workers.Add(worker);
            }
            for (int i = 0; i < 60; i++)
            {
                renderer.SetTexture(i % 2 == 0 ? b : a);
                using var frame = renderer.CaptureLinearFrame(false);
                if (frame.Width != 96 || frame.Height != 64) throw new InvalidOperationException($"并发期间读回尺寸异常 {frame.Width}×{frame.Height}");
            }
            foreach (var worker in workers) worker.Join();
            renderer.RequestTrim(PressureLevel.Critical);
            renderer.ApplyPendingTrim();
            Assert(failures == 0, $"并发修剪抛出 {failures} 次异常");
            Assert(renderer.CachedTextureCount == 0, $"最终仍有 {renderer.CachedTextureCount} 份缓存");
            long currentBytes = (long)a.Width * a.Height * 8 * 4 / 3;
            Assert(renderer.TextureBytes == currentBytes, $"最终纹理字节 {renderer.TextureBytes}，应只剩当前纹理");
            using (var frame = renderer.CaptureLinearFrame(false))
            {
                var value = ReadLinear(frame, 48, 32);
                Near("并发后 R", .3f, value.R);
                Near("并发后 G", .5f, value.G);
            }
            detail.Add("  3 线程各 150 轮修剪与主线程切换/读回交错，最终只剩当前纹理且画面未损");
        });

        Check("弱引用不延长 CPU 租约", detail =>
        {
            using var renderer = NewRenderer(out _);
            var (weak, pixelsEmpty, uploads) = UploadThenDrop(renderer);
            Assert(pixelsEmpty, "上传后释放创建者租约，像素仍非空：渲染器持有了 CPU 租约");
            Assert(uploads == 1, $"上传次数应为 1，实得 {uploads}");
            ForceGc();
            Assert(!weak.IsAlive, "纹理缓存仍强引用已释放的展示产物");
            detail.Add($"  租约归零后像素立即可回收；纹理字节仍为 {renderer.TextureBytes}");
        });

        Check("Dispose 释放全部纹理且幂等", detail =>
        {
            var renderer = NewRenderer(out _);
            using var a = SolidLinear(64, 48, .5f, 0, 0);
            using var b = SolidLinear(64, 48, 0, .5f, 0);
            renderer.SetTexture(a);
            renderer.SetTexture(b);
            Assert(renderer.TextureBytes > 0, "释放前应有纹理字节");
            renderer.Dispose();
            Assert(renderer.TextureBytes == 0 && renderer.TextureWidth == 0 && renderer.CachedTextureCount == 0,
                $"释放后仍有 {renderer.TextureBytes} 字节 / {renderer.CachedTextureCount} 份缓存");
            renderer.Dispose();
            renderer.RequestTrim(PressureLevel.Critical);
            renderer.ApplyPendingTrim();
            renderer.DrawFrame();
            bool threw = false;
            try
            {
                renderer.SetTexture(a);
            }
            catch (ObjectDisposedException)
            {
                threw = true;
            }
            Assert(threw, "释放后仍接受纹理上传");
            detail.Add("  重复 Dispose、修剪、绘制都安全，上传显式拒绝");
        });

        Check("A/B 切换计时（强制 Trim 对比缓存）", detail =>
        {
            RequireCacheable();
            using var renderer = NewRenderer(out var viewport);
            viewport.ResizeView(1600, 1200, 1);
            renderer.ResizeDrawable(1600, 1200, 1);
            using var a = CreateLinearImage(1600, 1200, 0, (x, y, w, h) => (x / (float)w, y / (float)h, .5f));
            using var b = CreateLinearImage(1600, 1200, 0, (x, y, w, h) => (y / (float)h, .5f, x / (float)w));
            var uncached = new double[TimingRounds];
            var cached = new double[TimingRounds];
            for (int i = 0; i < TimingRounds; i++)
            {
                renderer.RequestTrim(PressureLevel.Caution);
                renderer.ApplyPendingTrim();
                uncached[i] = MeasureSwitch(renderer, i % 2 == 0 ? a : b);
            }
            Assert(renderer.UploadCount == TimingRounds, $"强制修剪路径每次都应上传，实得 {renderer.UploadCount} 次");
            renderer.SetTexture(a);
            renderer.SetTexture(b);
            long uploads = renderer.UploadCount;
            for (int i = 0; i < TimingRounds; i++) cached[i] = MeasureSwitch(renderer, i % 2 == 0 ? a : b);
            Assert(renderer.UploadCount == uploads, $"缓存路径仍上传了 {renderer.UploadCount - uploads} 次");
            detail.Add($"  强制修剪：首次 {uncached[0]:0.00} ms / 中位 {Median(uncached):0.00} ms / p95 {P95(uncached):0.00} ms");
            detail.Add($"  缓存命中：首次 {cached[0]:0.00} ms / 中位 {Median(cached):0.00} ms / p95 {P95(cached):0.00} ms");
            detail.Add("  单次计时只含上传与 mipmap blit 编码，不含 GPU 完成与绘制");
        });

        lines.Add($"结果 {(failed == 0 ? "PASS" : "FAIL")} ({passed} passed, {failed} failed)");
        return string.Join(Environment.NewLine, lines);
    }

    private static MetalPhotoRenderer NewRenderer(out PhotoViewport viewport)
    {
        viewport = new PhotoViewport();
        return new MetalPhotoRenderer(viewport);
    }

    private static MetalPhotoRenderer NewRenderer(out PhotoViewport viewport, long recentByteLimit)
    {
        viewport = new PhotoViewport();
        return new MetalPhotoRenderer(viewport, recentByteLimit);
    }

    private static void RequireCacheable()
    {
        if (MemoryGovernor.CurrentLevel != PressureLevel.Normal || MemoryGovernor.InRecoveryHold)
            throw new InvalidOperationException(
                $"压力 {MemoryGovernor.CurrentLevel}（恢复观察期 {MemoryGovernor.InRecoveryHold}）下缓存按设计停用，本次结果不可比");
    }

    private static void Present(MetalPhotoRenderer renderer, PhotoViewport viewport, DecodedImage image, int orientation = 1)
    {
        int width = orientation >= 5 ? image.SourceHeight : image.SourceWidth;
        int height = orientation >= 5 ? image.SourceWidth : image.SourceHeight;
        viewport.ResizeView(width, height, 1);
        renderer.ResizeDrawable(width, height, 1);
        renderer.SetTexture(image);
        viewport.BeginImageChange();
        viewport.ApplyImage(image.SourceWidth, image.SourceHeight, orientation, renderer.TextureWidth);
    }

    private static DecodedImage CreateLinearImage(int width, int height, int padding,
        Func<int, int, int, int, (float R, float G, float B)> color)
    {
        int stride = width * 8 + padding;
        var pixels = new byte[stride * height];
        Array.Fill(pixels, (byte)0x7E);
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
                WriteHalf(pixels, y * stride + x * 8, color(x, y, width, height));
        return new DecodedImage(pixels, width, height, stride, PixelEncoding.RgbaF16, ImageColorSpace.LinearSrgb);
    }

    private static DecodedImage CreateRgba8Image(int width, int height, int padding,
        Func<int, int, (byte R, byte G, byte B)> color)
    {
        int stride = width * 4 + padding;
        var pixels = new byte[stride * height];
        Array.Fill(pixels, (byte)0x5A);
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                var value = color(x, y);
                int offset = y * stride + x * 4;
                pixels[offset] = value.R;
                pixels[offset + 1] = value.G;
                pixels[offset + 2] = value.B;
                pixels[offset + 3] = 255;
            }
        return new DecodedImage(pixels, width, height, stride, PixelEncoding.Rgba8, ImageColorSpace.DisplayP3);
    }

    private static DecodedImage CreateQuadrantImage(int size)
        => CreateLinearImage(size, size, 0, (x, y, w, h) => (x < w / 2, y < h / 2) switch
        {
            (true, true) => (1f, 0f, 0f),
            (false, true) => (0f, 0f, 1f),
            (true, false) => (1f, 1f, 1f),
            _ => (.05f, .05f, .05f),
        });

    private static DecodedImage SolidLinear(int width, int height, float red, float green, float blue)
        => CreateLinearImage(width, height, 0, (x, y, w, h) => (red, green, blue));

    // 方法返回即销毁唯一强引用：缓存若持强引用，调用方 GC 后弱引用仍会存活。
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (WeakReference Weak, bool PixelsEmpty, long Uploads) UploadThenDrop(MetalPhotoRenderer renderer)
    {
        var image = DecodedImage.CreatePooled(DecodedImage.RentPixels(64 * 48 * 8), 64, 48, 64 * 8,
            PixelEncoding.RgbaF16, ImageColorSpace.LinearSrgb);
        renderer.SetTexture(image);
        image.Release();
        return (new WeakReference(image), image.Pixels.Length == 0, renderer.UploadCount);
    }

    private static void WriteHalf(byte[] pixels, int offset, (float R, float G, float B) color)
    {
        var span = MemoryMarshal.Cast<byte, Half>(pixels.AsSpan(offset, 8));
        span[0] = (Half)color.R;
        span[1] = (Half)color.G;
        span[2] = (Half)color.B;
        span[3] = (Half)1f;
    }

    private static (float R, float G, float B) ReadLinear(DecodedImage image, int x, int y)
    {
        Assert(image.Encoding == PixelEncoding.RgbaF16, $"读回编码 {image.Encoding}");
        Assert(x >= 0 && y >= 0 && x < image.Width && y < image.Height, $"越界采样({x},{y}) {image.Width}×{image.Height}");
        var span = MemoryMarshal.Cast<byte, Half>(image.Pixels.AsSpan(y * image.Stride + x * 8, 8));
        return ((float)span[0], (float)span[1], (float)span[2]);
    }

    private static float SrgbToLinear(float value)
        => value <= .04045f ? value / 12.92f : MathF.Pow((value + .055f) / 1.055f, 2.4f);

    private static void ForceGc()
    {
        for (int i = 0; i < 3; i++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }
    }

    private static double MeasureSwitch(MetalPhotoRenderer renderer, DecodedImage image)
    {
        long start = Stopwatch.GetTimestamp();
        renderer.SetTexture(image);
        return Stopwatch.GetElapsedTime(start).TotalMilliseconds;
    }

    private static double Median(double[] samples)
    {
        var sorted = (double[])samples.Clone();
        Array.Sort(sorted);
        return sorted[sorted.Length / 2];
    }

    private static double P95(double[] samples)
    {
        var sorted = (double[])samples.Clone();
        Array.Sort(sorted);
        return sorted[Math.Min(sorted.Length - 1, (int)Math.Ceiling(sorted.Length * .95) - 1)];
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static void Near(string what, float expected, float actual, float tolerance = 3e-3f)
    {
        if (float.IsNaN(actual) || Math.Abs(expected - actual) > tolerance)
            throw new InvalidOperationException($"{what} 期望 {expected:0.####} 实得 {actual:0.####}");
    }
}
