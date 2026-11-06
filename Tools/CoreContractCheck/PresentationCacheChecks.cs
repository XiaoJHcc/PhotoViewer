using PhotoViewer.Core.Color;
using PhotoViewer.Core.Image;
using PhotoViewer.Core.Memory;
using PhotoViewer.Core.Platform;
using PhotoViewer.Core.Storage;

namespace CoreContractCheck;

/// <summary>
/// 展示缓存跨档复用：Full 请求在 Full 档未命中时复用代理档"实际已达源分辨率"的产物，
/// 并覆盖小代理、单维不足、编码布局与保真度不符等边界。
/// 只在进程早期运行：注入自己的展示解码器/编码策略，结束时清缓存并恢复注入前的状态。
/// </summary>
internal static class PresentationCacheChecks
{
    private static readonly byte[] IccProfile = [0x01, 0x02, 0x03, 0x04];

    public static async Task RunAsync(Checks c, string root)
    {
        c.Section("展示缓存跨档复用");
        UiDispatcher.Initialize(new InlineDispatcher());
        var injection = PresentationInjection.Capture();
        var decoder = new SyntheticPresentationDecoder();
        var policy = new SwitchableEncodingPolicy(PixelEncoding.Rgba8, PixelEncoding.Rgba8);
        BitmapLoader.ClearCache();
        try
        {
            BitmapLoader.InitializePresentationDecoder(decoder, policy);
            await CheckAsyncFullReuseAsync(c, root, decoder);
            await CheckSyncFullReuseAsync(c, root, decoder);
            await CheckRejectionsAsync(c, root, decoder, policy);
            await CheckLoadSessionAsync(c, root, decoder);
            await CheckUninjectedCompatAsync(c, root, injection);
        }
        finally
        {
            BitmapLoader.ClearCache();
            injection.Restore();
        }
    }

    /// <summary>异步 Full 请求复用已达源分辨率的代理：实例同一、不再解码、不建 Full 条目、元数据与像素原样。</summary>
    private static async Task CheckAsyncFullReuseAsync(Checks c, string root, SyntheticPresentationDecoder decoder)
    {
        const string name = "async-reuse.jpg";
        decoder.Sources[name] = (1200, 800);
        var file = new LocalFileRef(Path.Combine(root, name));

        var proxy = await BitmapLoader.GetBitmapAsync(
            file, DecodeSizeHint.ProxyTo(2000), DecodePriority.P0_Current, CancellationToken.None);
        var callsAfterProxy = decoder.Calls;
        c.True("代理请求按源尺寸产出（解码器不放大）", proxy is { Width: 1200, Height: 800 });
        c.True("代理产物已入 Proxy 档", BitmapLoader.IsInCache(file.StorageId.Value, ImageSizeClass.Proxy));
        if (proxy == null) return;

        var pixels = proxy.Pixels.ToArray();
        var full = await BitmapLoader.GetBitmapAsync(file, DecodePriority.P0_Current, CancellationToken.None);
        c.True("Full 请求复用同一代理实例", ReferenceEquals(proxy, full));
        c.Equal("Full 请求未再解码", callsAfterProxy, decoder.Calls);
        c.True("未额外创建 Full 档条目", !BitmapLoader.IsInCache(file.StorageId.Value, ImageSizeClass.Full));
        c.True("复用产物保留源尺寸", full is { SourceWidth: 1200, SourceHeight: 800 });
        c.True("复用产物保留色彩标签与 ICC",
            full != null && full.ColorSpace == ImageColorSpace.DisplayP3 && full.IccProfile.Span.SequenceEqual(IccProfile));
        if (full != null) c.Bytes("复用产物有效像素未被复制/改写", pixels, full.Pixels);

        var again = await BitmapLoader.GetBitmapAsync(file, DecodePriority.P0_Current, CancellationToken.None);
        c.True("后续 Full 请求继续复用同一实例", ReferenceEquals(proxy, again));
        c.Equal("后续 Full 请求仍不重解", callsAfterProxy, decoder.Calls);

        again?.Release();
        full?.Release();
        proxy.Release();
        c.True("归还调用方租约后缓存仍持有像素", proxy.Pixels.Length > 0);
        BitmapLoader.RemoveCached(file.StorageId.Value, ImageSizeClass.Proxy);
        c.True("代理条目淘汰后像素归还", proxy.Pixels.Length == 0);
    }

    /// <summary>同步只查缓存路径与异步同规则：命中同一实例，未命中不解码。</summary>
    private static async Task CheckSyncFullReuseAsync(Checks c, string root, SyntheticPresentationDecoder decoder)
    {
        const string name = "sync-reuse.jpg";
        decoder.Sources[name] = (1600, 1000);
        var file = new LocalFileRef(Path.Combine(root, name));
        var proxy = await BitmapLoader.GetBitmapAsync(
            file, DecodeSizeHint.ProxyTo(4000), DecodePriority.P0_Current, CancellationToken.None);
        var calls = decoder.Calls;
        c.True("同步复用：代理已达源分辨率", proxy is { Width: 1600, Height: 1000 });

        var cached = BitmapLoader.TryAcquireCached(file, DecodeSizeHint.Full);
        c.True("同步 Full 查询复用代理实例", ReferenceEquals(proxy, cached));
        c.Equal("同步复用不触发解码", calls, decoder.Calls);
        cached?.Release();

        const string smallName = "sync-small.jpg";
        decoder.Sources[smallName] = (1600, 1000);
        var smallFile = new LocalFileRef(Path.Combine(root, smallName));
        var smallProxy = await BitmapLoader.GetBitmapAsync(
            smallFile, DecodeSizeHint.ProxyTo(400), DecodePriority.P0_Current, CancellationToken.None);
        var smallCalls = decoder.Calls;
        c.True("小代理不满足同步 Full 查询", BitmapLoader.TryAcquireCached(smallFile, DecodeSizeHint.Full) == null);
        c.Equal("同步未命中不触发解码", smallCalls, decoder.Calls);

        var emptyFile = new LocalFileRef(Path.Combine(root, "sync-empty.jpg"));
        c.True("无任何缓存条目时同步 Full 查询为 null", BitmapLoader.TryAcquireCached(emptyFile, DecodeSizeHint.Full) == null);

        smallProxy?.Release();
        proxy?.Release();
    }

    /// <summary>不满足的边界：小代理、单维不足、低保真度的代理都不许当 Full。</summary>
    private static async Task CheckRejectionsAsync(Checks c, string root, SyntheticPresentationDecoder decoder, SwitchableEncodingPolicy policy)
    {
        const string shortName = "short-side.jpg";
        decoder.Sources[shortName] = (1200, 800);
        // 长边与源相同、短边不足：只按长边判断会误当成源分辨率
        decoder.DisplayOverrides[shortName] = (1200, 700);
        var shortFile = new LocalFileRef(Path.Combine(root, shortName));
        var shortProxy = await BitmapLoader.GetBitmapAsync(
            shortFile, DecodeSizeHint.ProxyTo(4000), DecodePriority.P0_Current, CancellationToken.None);
        var shortCalls = decoder.Calls;
        decoder.DisplayOverrides.Remove(shortName);
        var shortFull = await BitmapLoader.GetBitmapAsync(shortFile, DecodePriority.P0_Current, CancellationToken.None);
        c.True("单维不足的代理不复用为 Full", shortFull != null && !ReferenceEquals(shortProxy, shortFull));
        c.Equal("单维不足时确实重新解码", shortCalls + 1, decoder.Calls);
        c.True("重解产物为源尺寸", shortFull is { Width: 1200, Height: 800 });

        policy.Proxy = PixelEncoding.RgbaF16;
        policy.Full = PixelEncoding.Rgba8;
        const string f16Name = "f16-proxy.jpg";
        decoder.Sources[f16Name] = (1200, 800);
        var f16File = new LocalFileRef(Path.Combine(root, f16Name));
        var f16Proxy = await BitmapLoader.GetBitmapAsync(
            f16File, DecodeSizeHint.ProxyTo(4000), DecodePriority.P0_Current, CancellationToken.None);
        var f16Calls = decoder.Calls;
        var f16Full = await BitmapLoader.GetBitmapAsync(f16File, DecodePriority.P0_Current, CancellationToken.None);
        c.True("F16 代理满足 8 位 Full 请求", ReferenceEquals(f16Proxy, f16Full));
        c.Equal("F16 满足时不重解", f16Calls, decoder.Calls);

        policy.Proxy = PixelEncoding.Rgba8;
        policy.Full = PixelEncoding.RgbaF16;
        const string rgba8Name = "rgba8-proxy.jpg";
        decoder.Sources[rgba8Name] = (1200, 800);
        var rgba8File = new LocalFileRef(Path.Combine(root, rgba8Name));
        var rgba8Proxy = await BitmapLoader.GetBitmapAsync(
            rgba8File, DecodeSizeHint.ProxyTo(4000), DecodePriority.P0_Current, CancellationToken.None);
        var rgba8Calls = decoder.Calls;
        var rgba8Full = await BitmapLoader.GetBitmapAsync(rgba8File, DecodePriority.P0_Current, CancellationToken.None);
        c.True("8 位代理不满足 F16 Full 请求", rgba8Full != null && !ReferenceEquals(rgba8Proxy, rgba8Full));
        c.Equal("低保真代理不挡 Full 重解", rgba8Calls + 1, decoder.Calls);
        c.Equal("重解产物按 Full 档编码", PixelEncoding.RgbaF16, rgba8Full?.Encoding);

        policy.Proxy = PixelEncoding.Rgba8;
        policy.Full = PixelEncoding.Rgba8;

        // 通道布局不符：缓存里的代理是 Bgra32，本次 Full 要求 Rgba8 展示编码 → 不复用，按 Rgba8 重解
        const string layoutName = "layout-bgra32.jpg";
        decoder.Sources[layoutName] = (1200, 800);
        var layoutFile = new LocalFileRef(Path.Combine(root, layoutName));
        var wrongLayout = CreateImage(1200, 800, 1200, 800, PixelEncoding.Bgra32);
        InjectCacheItem(layoutFile, wrongLayout, ImageSizeClass.Proxy);
        var layoutCalls = decoder.Calls;
        var layoutFull = await BitmapLoader.GetBitmapAsync(layoutFile, DecodePriority.P0_Current, CancellationToken.None);
        c.True("Bgra32 布局的代理不满足 Rgba8 Full",
            layoutFull != null && !ReferenceEquals(wrongLayout, layoutFull));
        c.Equal("布局不符时重新解码", layoutCalls + 1, decoder.Calls);
        c.Equal("重解产物为请求的 Rgba8", PixelEncoding.Rgba8, layoutFull?.Encoding);

        // 未知编码不默许：沿用 PresentationFidelity 的抛出契约
        const string unknownName = "unknown-encoding.jpg";
        decoder.Sources[unknownName] = (1200, 800);
        var unknownFile = new LocalFileRef(Path.Combine(root, unknownName));
        var unknownProxy = await BitmapLoader.GetBitmapAsync(
            unknownFile, DecodeSizeHint.ProxyTo(4000), DecodePriority.P0_Current, CancellationToken.None);
        policy.Full = (PixelEncoding)99;
        c.ThrowsExact<ArgumentOutOfRangeException>("未知 Full 编码不被默许", () =>
        {
            BitmapLoader.TryAcquireCached(unknownFile, DecodeSizeHint.Full);
        });
        policy.Full = PixelEncoding.Rgba8;

        shortProxy?.Release();
        shortFull?.Release();
        f16Proxy?.Release();
        f16Full?.Release();
        rgba8Proxy?.Release();
        rgba8Full?.Release();
        layoutFull?.Release();
        unknownProxy?.Release();
    }

    /// <summary>展示会话：屏幕代理未达源分辨率时放大仍要解码；已达源分辨率时 Full 直接复用且不再换代。</summary>
    private static async Task CheckLoadSessionAsync(Checks c, string root, SyntheticPresentationDecoder decoder)
    {
        const string proxyName = "session-proxy.jpg";
        decoder.Sources[proxyName] = (1600, 1200);
        var proxyFile = new LocalFileRef(Path.Combine(root, proxyName));
        using (var session = new PhotoLoadSession())
        {
            session.UpdateViewport(300, 200, needsFull: false);   // 代理目标 600
            session.SetFile(proxyFile);
            var proxyShown = await WaitUntilAsync(() => session.Image != null, TimeSpan.FromSeconds(5));
            c.True("会话代理上屏", proxyShown && session.IsProxy, "5s 内未上屏代理");
            var calls = decoder.Calls;
            session.UpdateViewport(300, 200, needsFull: true);
            var fullShown = await WaitUntilAsync(
                () => session.Image != null && !session.IsProxy, TimeSpan.FromSeconds(5));
            c.True("放大后 Full 上屏", fullShown, "5s 内未升级到 Full");
            c.Equal("屏幕代理未达源分辨率时 Full 仍要解码", calls + 1, decoder.Calls);
            c.True("Full 产物为源尺寸", session.Image is { Width: 1600, Height: 1200 });
        }

        const string reuseName = "session-reuse.jpg";
        decoder.Sources[reuseName] = (1200, 800);
        var reuseFile = new LocalFileRef(Path.Combine(root, reuseName));
        var warmed = await BitmapLoader.GetBitmapAsync(
            reuseFile, DecodeSizeHint.ProxyTo(2000), DecodePriority.P0_Current, CancellationToken.None);
        warmed?.Release();
        var reuseCalls = decoder.Calls;
        using (var fullSession = new PhotoLoadSession())
        {
            fullSession.UpdateViewport(600, 400, needsFull: true);
            fullSession.SetFile(reuseFile);
            c.True("会话直接以代理产物当 Full",
                ReferenceEquals(warmed, fullSession.Image) && !fullSession.IsProxy && fullSession.Error.Length == 0);
            c.Equal("会话复用不再解码", reuseCalls, decoder.Calls);
            c.Equal("会话切图换代一次", 1, fullSession.Generation);
            fullSession.UpdateViewport(600, 400, needsFull: true);
            c.Equal("已在源分辨率时不发起 Full", reuseCalls, decoder.Calls);
        }
    }

    /// <summary>
    /// 旧壳（未注入展示解码器）：Full 所需编码按旧格式规则给出，不因缺少策略抛异常，
    /// 仍复用编码完全一致且已达源分辨率的代理产物。
    /// </summary>
    private static async Task CheckUninjectedCompatAsync(Checks c, string root, PresentationInjection injection)
    {
        const string name = "legacy-proxy.jpg";
        var file = new LocalFileRef(Path.Combine(root, name));
        var injected = CreateImage(1200, 800, 1200, 800, PixelEncoding.Bgra32);
        InjectCacheItem(file, injected, ImageSizeClass.Proxy);

        injection.Restore();
        DecodedImage? reused = null;
        string? failure = null;
        try
        {
            reused = BitmapLoader.TryAcquireCached(file, DecodeSizeHint.Full);
        }
        catch (Exception exception)
        {
            failure = exception.GetType().Name + "：" + exception.Message;
        }
        c.True("未注入策略时同步 Full 查询不抛异常", failure == null, failure ?? string.Empty);
        c.True("未注入策略时复用编码一致的源分辨率代理", ReferenceEquals(injected, reused));
        reused?.Release();

        var fixture = Path.Combine("Fixtures", "jpeg", "color-srgb.jpg");
        if (!File.Exists(fixture))
        {
            c.Skip("未注入解码器的 Full 解码沿用旧编码口径", "缺少夹具 " + fixture);
            return;
        }
        var legacy = await BitmapLoader.GetBitmapAsync(
            new LocalFileRef(fixture), DecodePriority.P0_Current, CancellationToken.None);
        c.True("未注入解码器时 Full 解码可用", legacy is not null, "返回 null");
        c.True("旧口径产物为源尺寸",
            legacy is { } image && Math.Max(image.Width, image.Height) == 1200 && Math.Min(image.Width, image.Height) == 800);
        c.Equal("旧口径产物编码为 Bgra32", PixelEncoding.Bgra32, legacy?.Encoding);
        legacy?.Release();
    }

    private static async Task<bool> WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return true;
            await Task.Delay(25);
        }
        return condition();
    }

    /// <summary>确定性合成展示图：源尺寸与显示尺寸可不同，用于构造布局与几何边界。</summary>
    private static DecodedImage CreateImage(
        int width, int height, int sourceWidth, int sourceHeight, PixelEncoding encoding)
    {
        int stride = width * DecodedImage.BytesPerPixel(encoding);
        var pixels = DecodedImage.RentPixels(stride * height);
        for (int i = 0; i < stride * height; i++) pixels[i] = (byte)(i * 7 + 11);
        return DecodedImage.CreatePooled(pixels, width, height, stride, encoding,
            ImageColorSpace.DisplayP3, sourceWidth, sourceHeight, orientation: 1, iccProfile: IccProfile);
    }

    /// <summary>反射把条目放进统一缓存：旧壳口径的 Bgra32 产物无法经策略协商路径产出。</summary>
    private static void InjectCacheItem(IFileRef file, DecodedImage image, ImageSizeClass sizeClass)
    {
        const System.Reflection.BindingFlags Flags =
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static;
        var cacheField = typeof(BitmapLoader).GetField("_cache", Flags)!;
        var cache = cacheField.GetValue(null)!;
        var keyType = cacheField.FieldType.GetGenericArguments()[0];
        var key = Activator.CreateInstance(keyType, [file.StorageId.Value, sizeClass])!;
        var indexer = cacheField.FieldType.GetProperty("Item", [keyType])!;
        indexer.SetValue(cache, new BitmapCacheItem(image, file.StorageId.Value, sizeClass), [key]);
    }

    /// <summary>BitmapLoader / ThumbnailService 的注入状态快照：测试注入后按原样恢复，不给后续用例留残留。</summary>
    private sealed class PresentationInjection
    {
        private static readonly System.Reflection.FieldInfo PresentationDecoderField =
            typeof(BitmapLoader).GetField("_presentationDecoder", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
        private static readonly System.Reflection.FieldInfo EncodingPolicyField =
            typeof(BitmapLoader).GetField("_encodingPolicy", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
        private static readonly System.Reflection.FieldInfo MissingPreviewDecoderField =
            typeof(ThumbnailService).GetField("_missingPreviewDecoder", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;

        private readonly object? _presentationDecoder = PresentationDecoderField.GetValue(null);
        private readonly object? _encodingPolicy = EncodingPolicyField.GetValue(null);
        private readonly object? _missingPreviewDecoder = MissingPreviewDecoderField.GetValue(null);

        public static PresentationInjection Capture() => new();

        public void Restore()
        {
            PresentationDecoderField.SetValue(null, _presentationDecoder);
            EncodingPolicyField.SetValue(null, _encodingPolicy);
            MissingPreviewDecoderField.SetValue(null, _missingPreviewDecoder);
        }
    }

    /// <summary>按源几何产出合成展示图的解码器：代理请求按目标长边收小（不放大），并统计调用次数。</summary>
    private sealed class SyntheticPresentationDecoder : IPresentationImageDecoder
    {
        private int _calls;

        public Dictionary<string, (int Width, int Height)> Sources { get; } = new(StringComparer.Ordinal);

        /// <summary>强制产物显示尺寸（模拟单维不足等非等比产物）。</summary>
        public Dictionary<string, (int Width, int Height)> DisplayOverrides { get; } = new(StringComparer.Ordinal);

        public int Calls => Volatile.Read(ref _calls);

        public Task<DecodedImage?> DecodeAsync(
            IFileRef file, int? maxLongEdge, PixelEncoding encoding, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _calls);
            var (sourceWidth, sourceHeight) = Sources.TryGetValue(file.Name, out var source) ? source : (1200, 800);
            var (width, height) = DisplayOverrides.TryGetValue(file.Name, out var display)
                ? display
                : ScaleToTarget(sourceWidth, sourceHeight, maxLongEdge);
            return Task.FromResult<DecodedImage?>(CreateImage(width, height, sourceWidth, sourceHeight, encoding));
        }

        public Task<DecodedImage?> DecodeThumbnailAsync(IFileRef file, int minShortEdge, CancellationToken cancellationToken)
            => Task.FromResult<DecodedImage?>(null);

        private static (int Width, int Height) ScaleToTarget(int sourceWidth, int sourceHeight, int? maxLongEdge)
        {
            int sourceLongEdge = Math.Max(sourceWidth, sourceHeight);
            if (maxLongEdge is not { } edge || sourceLongEdge <= edge) return (sourceWidth, sourceHeight);
            double scale = (double)edge / sourceLongEdge;
            return (Math.Max(1, (int)Math.Round(sourceWidth * scale)),
                Math.Max(1, (int)Math.Round(sourceHeight * scale)));
        }
    }

    private sealed class SwitchableEncodingPolicy(PixelEncoding proxy, PixelEncoding full) : IPresentationEncodingPolicy
    {
        public PixelEncoding Proxy { get; set; } = proxy;

        public PixelEncoding Full { get; set; } = full;

        public PixelEncoding Select(ImageSizeClass sizeClass, PressureLevel pressure)
            => sizeClass == ImageSizeClass.Full ? Full : Proxy;
    }

    private sealed class InlineDispatcher : IUiDispatcher
    {
        public System.Reactive.Concurrency.IScheduler MainThreadScheduler { get; } =
            System.Reactive.Concurrency.ImmediateScheduler.Instance;

        public bool CheckAccess() => true;

        public void Post(Action action, UiPriority priority = UiPriority.Normal) => action();

        public Task InvokeAsync(Action action, UiPriority priority = UiPriority.Normal)
        {
            action();
            return Task.CompletedTask;
        }

        public Task<T> InvokeAsync<T>(Func<T> func, UiPriority priority = UiPriority.Normal) =>
            Task.FromResult(func());
    }
}
