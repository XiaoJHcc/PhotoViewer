using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;
using PhotoViewer.Core.Color;
using PhotoViewer.Core.Database;
using PhotoViewer.Core.Image;
using PhotoViewer.Core.Platform;

namespace PhotoViewer.Core.AI;

/// <summary>
/// 平台中立的分析栏状态机:把"细节预览"(对焦点 / 中心)与"DINO/CV 诊断"(PCA / Cosine / 锐度 / 抖动)
/// 合并成一个常驻侧栏,由 macOS / iOS / Android / Windows 原生分析栏共用同一份编排逻辑。
/// 诊断瓦片有三条数据来源路径:
/// 1. 只读库:指纹 → <see cref="AnalysisResultCache"/> → DB,不触发解码 / ONNX 推理 / CV 重算;
/// 2. 增强预览联动:先取当前照片的只读基线,再按独立开关重算 CV 和/或 DINO,不回写 DB;
/// 3. 未提取回退:库里缺少已启用能力的结果时,对当前主图位图计算缺失部分 —
///    先延迟再等主图 / 缩略图通道空闲,绝不抢主图浏览资源,期间只在对应瓦片显示"计算中…"。
/// 路径 2/3 由「即时计算 DINO」「即时计算 CV」分别门控;标准版仅编译 CV 路径。
///
/// 派生层按指纹走 <see cref="AnalysisResultCache"/>,与邻居预取同步预热,命中时切图变成纯 UI swap。
/// 4 张诊断瓦片的位图(PCA / 中心 cosine / 锐度 / 抖动)归 cache 所有 — presenter 仅引用、不 Dispose;
/// 用户点击诊断瓦片重算 cosine 的位图归 presenter 所有,替换时显式释放。
/// 直方图位图同样归 presenter 所有,并按"源位图实例"保留最近 2 份:源用弱引用记录(不延长像素租约),
/// 同一实例不重复计算,旧实例被挤出缓存且不在显示时才释放。
/// 细节预览瓦片不携带位图:裁剪源由各平台展示层持有,本类只提供归一化中心与对焦框。
/// </summary>
public sealed class AnalysisPresenter : IDisposable
{
    /// <summary>DINO patch 图像素边长(32);也用于 cosine 参考点坐标系。</summary>
    public const int PatchGridPixels = AnalysisComputer.PatchGridPixels;

    /// <summary>未提取回退的启动延迟(ms):快速翻页时在延迟内即被取消,零 CV/推理开销。</summary>
    private const int FallbackDelayMs = 500;

    /// <summary>直方图缓存容量(A/B 对比与增强开关来回切时各留一份)。</summary>
    private const int HistogramCacheCapacity = 2;

    private readonly IAnalysisHost _host;
    private readonly List<AnalysisTile> _tiles;
    private readonly AnalysisTile _focusTile;
    private readonly AnalysisTile _centerTile;
    private readonly AnalysisTile _histogramTile;
    private readonly AnalysisTile _pcaTile;
    private readonly AnalysisTile _cosineTile;
    private readonly AnalysisTile _sharpnessTile;
    private readonly AnalysisTile _shakeTile;
    private readonly List<HistogramEntry> _histogramCache = new(HistogramCacheCapacity);

    private CancellationTokenSource? _cts;
    private ImageFile? _loadingFile;
    private ImageFile? _observedFile;
    private float[]? _patchTokens;
    private DecodedImage? _customCosine;   // 用户点击重算的 cosine 图(presenter 所有)
    private DecodedImage? _histogram;      // 当前显示的直方图(presenter 所有,通常也在缓存里)
    private DecodedImage? _histogramComputingSource; // 正在算直方图的源实例;仅用于去重
    private CancellationTokenSource? _histogramCts;
    private int _histogramToken;
    private bool _disposed;
    private bool _focusInserted;

    /// <summary>瓦片列表:对焦点(仅有 Sony 对焦数据时存在)/ 中心 / 直方图 / DINO PCA / Cosine / 锐度 / 抖动拖影。</summary>
    public IReadOnlyList<AnalysisTile> Tiles => _tiles;

    /// <summary>对焦点细节预览瓦片;是否在列表内取决于当前照片的 Sony 对焦数据。</summary>
    public AnalysisTile FocusTile => _focusTile;

    /// <summary>中心细节预览瓦片(恒在列表内)。</summary>
    public AnalysisTile CenterTile => _centerTile;

    /// <summary>直方图瓦片。</summary>
    public AnalysisTile HistogramTile => _histogramTile;

    /// <summary>DINO PCA-RGB 瓦片。</summary>
    public AnalysisTile PcaTile => _pcaTile;

    /// <summary>Cosine 相似度瓦片(参考点随用户点击变化)。</summary>
    public AnalysisTile CosineTile => _cosineTile;

    /// <summary>锐度热力图瓦片。</summary>
    public AnalysisTile SharpnessTile => _sharpnessTile;

    /// <summary>抖动拖影瓦片(承载 <see cref="AnalysisTile.ShakeField"/> 矢量场)。</summary>
    public AnalysisTile ShakeTile => _shakeTile;

    /// <summary>诊断瓦片的图像长宽比(宽/高);从 cv_image_width/height 推。</summary>
    public double AspectRatio { get; private set; } = 1.0;

    /// <summary>诊断瓦片共享的归一化准星横坐标;null 时不显示。</summary>
    public double? CrosshairU { get; private set; }

    /// <summary>诊断瓦片共享的归一化准星纵坐标;null 时不显示。</summary>
    public double? CrosshairV { get; private set; }

    /// <summary>cosine 参考点 x(0..31)。</summary>
    public int RefGridX { get; private set; } = PatchGridPixels / 2;

    /// <summary>cosine 参考点 y(0..31)。</summary>
    public int RefGridY { get; private set; } = PatchGridPixels / 2;

    /// <summary>任一瓦片字段变化后触发;展示层据此重新同步全部瓦片(引用未变时可跳过重编码)。</summary>
    public event Action? TilesChanged;

    /// <summary>准星变化后触发。</summary>
    public event Action? CrosshairChanged;

    /// <summary>构造并订阅宿主状态变化。</summary>
    /// <param name="host">平台宿主状态。</param>
    public AnalysisPresenter(IAnalysisHost host)
    {
        _host = host;
        _focusTile = new AnalysisTile(AnalysisTileKind.Detail, "对焦点") { Placeholder = null };
        _centerTile = new AnalysisTile(AnalysisTileKind.Detail, "中心") { Placeholder = null };
        _histogramTile = new AnalysisTile(AnalysisTileKind.Histogram, "直方图") { Placeholder = null };
        _pcaTile = new AnalysisTile(AnalysisTileKind.Diagnostic, "DINO PCA");
        _cosineTile = new AnalysisTile(AnalysisTileKind.Diagnostic, FormatCosineLabel(RefGridX, RefGridY));
        _sharpnessTile = new AnalysisTile(AnalysisTileKind.Diagnostic, "锐度");
        _shakeTile = new AnalysisTile(AnalysisTileKind.Diagnostic, "抖动拖影");
#if PHOTOVIEWER_DINO
        _tiles = [_centerTile, _histogramTile, _pcaTile, _cosineTile, _sharpnessTile, _shakeTile];
        FolderFeatureIndexer.FeaturesWritten += Refresh;
#else
        _tiles = [_centerTile, _histogramTile, _sharpnessTile, _shakeTile];
#endif
        _host.HostChanged += Refresh;
        ShakeFlagService.RecheckRequested += Refresh;
    }

    /// <summary>
    /// 重新路由当前照片:可见性与当前文件都就绪时按"增强联动 / 只读库 / 未提取回退"三条路径择一,
    /// 否则取消在途任务并清空诊断瓦片。所有实际工作推迟到 Background 优先级,绝不阻塞主图加载。
    /// </summary>
    public void Refresh()
    {
        if (_disposed) return;
        // 特征入库信号来自后台线程,先归位到 UI 线程再触碰瓦片状态与宿主视图。
        if (!UiDispatcher.CheckAccess())
        {
            UiDispatcher.Post(Refresh);
            return;
        }
        var file = _host.CurrentFile;
        ObserveFile(file);

        if (!_host.AnalysisVisible || file == null)
        {
            CancelPending();
            ClearDiagnostics();
            RefreshHistogram();
            return;
        }

        _cts?.Cancel();
        _cts?.Dispose();
        if (!ReferenceEquals(_loadingFile, file)) ClearDiagnostics();
        _loadingFile = file;
        var cts = new CancellationTokenSource();
        _cts = cts;
        UiDispatcher.Post(() =>
        {
            if (cts.IsCancellationRequested) return;
            LoadDeferred(file, cts.Token);
        }, UiPriority.Background);
        RefreshHistogram();
    }

    /// <summary>
    /// 诊断瓦片点击:落准星 + 把点击位置作 cosine 参考点(映射到 32×32);细节预览的点击不会到这里。
    /// </summary>
    /// <param name="u">归一化横坐标。</param>
    /// <param name="v">归一化纵坐标。</param>
    public void OnTileClicked(double u, double v)
    {
        u = Math.Clamp(u, 0, 1);
        v = Math.Clamp(v, 0, 1);
        CrosshairU = u;
        CrosshairV = v;
        CrosshairChanged?.Invoke();

#if PHOTOVIEWER_DINO
        if (_patchTokens == null) return;
        int gx = Math.Clamp((int)(u * PatchGridPixels), 0, PatchGridPixels - 1);
        int gy = Math.Clamp((int)(v * PatchGridPixels), 0, PatchGridPixels - 1);
        if (gx == RefGridX && gy == RefGridY && _cosineTile.Source != null) return;

        RefGridX = gx;
        RefGridY = gy;

        var cos = PatchHeatmap.ComputeRefCosine(_patchTokens, gx, gy);
        var old = _customCosine;
        _customCosine = HeatmapBitmapBuilder.BuildViridis(cos, PatchGridPixels, PatchGridPixels);
        _cosineTile.Source = _customCosine;
        _cosineTile.ShortLabel = FormatCosineLabel(gx, gy);
        old?.Dispose();
        TilesChanged?.Invoke();
#endif
    }

    /// <summary>空白处点击:清空准星。</summary>
    public void ClearCrosshair()
    {
        if (CrosshairU == null && CrosshairV == null) return;
        CrosshairU = null;
        CrosshairV = null;
        CrosshairChanged?.Invoke();
    }

    private bool DinoImmediate
    {
#if PHOTOVIEWER_DINO
        get => _host.ImmediateCompute;
#else
        get => false;
#endif
    }

    private bool CvImmediate => _host.ImmediateCvCompute;

    private void LoadDeferred(ImageFile file, CancellationToken ct)
    {
        if (ct.IsCancellationRequested) return;
        if (file.IsExifLoaded)
        {
            var fingerprint = TryComputeFingerprintSync(file);
            var hit = AnalysisResultCache.TryGet(fingerprint);
            if (hit != null)
            {
                RouteEntry(file, hit, ct);
                return;
            }
        }
        _ = LoadAsync(file, ct);
    }

    private void RouteEntry(ImageFile file, AnalysisResultCache.Entry baseline, CancellationToken ct)
    {
        if (ct.IsCancellationRequested) return;
        bool dino = DinoImmediate;
        bool cv = CvImmediate;
        bool enhanced = _host.EnhancedDisplay;
        string key = AnalysisResultCache.ImmediateKey(file.File.StorageId.Value, enhanced, dino, cv);
        var immediate = AnalysisResultCache.TryGetImmediate(key) ?? new AnalysisResultCache.Entry();
        double aspect = DisplayAspectRatio(_host.DisplayImage, baseline.AspectRatio);
        var merged = MergeEntries(baseline, immediate, aspect);
        ApplyEntry(merged);
        bool computeDino = dino && (enhanced ? immediate.Patches == null : merged.Patches == null);
        bool computeCv = cv && (enhanced ? immediate.SharpnessBmp == null : merged.SharpnessBmp == null);
        if (!computeDino && !computeCv) return;
        SetPlaceholder("计算中…", computeDino, computeCv);
        _ = RunImmediateAsync(file, key, baseline, immediate, enhanced, computeDino, computeCv, ct);
    }

    private async Task LoadAsync(ImageFile file, CancellationToken ct)
    {
        try
        {
            var fingerprint = await AnalysisDataReader.ComputeFingerprintAsync(file, ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            var entry = AnalysisResultCache.TryGet(fingerprint);
            if (entry == null)
            {
                var data = string.IsNullOrEmpty(fingerprint)
                    ? new AnalysisDataReader.Result()
                    : await AnalysisDataReader.ReadByFingerprintAsync(fingerprint, ct).ConfigureAwait(false);
                ct.ThrowIfCancellationRequested();
                entry = await Task.Run(() => AnalysisComputer.Compute(data), ct).ConfigureAwait(false);
                ct.ThrowIfCancellationRequested();
                if (!string.IsNullOrEmpty(fingerprint)) AnalysisResultCache.Put(fingerprint, entry);
            }
            var baseline = entry;
            UiDispatcher.Post(() => RouteEntry(file, baseline, ct), UiPriority.Background);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Console.WriteLine($"[Analysis] load failed: {ex.Message}");
            PostFailure("分析失败", ct);
        }
    }

    private static double DisplayAspectRatio(DecodedImage? image, double fallback)
    {
        if (image == null) return fallback;
        return image.Orientation >= 5 ? (double)image.Height / image.Width : (double)image.Width / image.Height;
    }

    /// <summary>把 Entry 贴到诊断瓦片。位图归 cache 所有(含即时缓存),presenter 只引用不释放。</summary>
    private void ApplyEntry(AnalysisResultCache.Entry entry)
    {
        AspectRatio = entry.AspectRatio;
        _sharpnessTile.Source = entry.SharpnessBmp;
        _sharpnessTile.Placeholder = entry.SharpnessBmp == null ? "未提取" : null;
#if PHOTOVIEWER_DINO
        _patchTokens = entry.Patches;
        _pcaTile.Source = entry.PcaBmp;
        _pcaTile.Placeholder = entry.PcaBmp == null ? "未提取" : null;
        var oldCustom = _customCosine;
        _customCosine = null;
        int rx = PatchGridPixels / 2;
        int ry = PatchGridPixels / 2;
        RefGridX = rx;
        RefGridY = ry;
        _cosineTile.Source = entry.CenterCosineBmp;
        _cosineTile.Placeholder = entry.CenterCosineBmp == null ? "未提取" : null;
        _cosineTile.ShortLabel = FormatCosineLabel(rx, ry);
        oldCustom?.Dispose();
#endif
        _shakeTile.ShakeField = entry.ShakeField;
        _shakeTile.ShortLabel = entry.ShakeLabel;
        _shakeTile.Placeholder = entry.ShakeField == null ? "未提取" : null;
        TilesChanged?.Invoke();
    }

    private static AnalysisResultCache.Entry MergeEntries(
        AnalysisResultCache.Entry baseline, AnalysisResultCache.Entry computed, double aspect) => new()
    {
        AspectRatio = aspect,
        PcaBmp = computed.Patches != null ? computed.PcaBmp : baseline.PcaBmp,
        CenterCosineBmp = computed.Patches != null ? computed.CenterCosineBmp : baseline.CenterCosineBmp,
        SharpnessBmp = computed.SharpnessBmp ?? baseline.SharpnessBmp,
        ShakeField = computed.ShakeField ?? baseline.ShakeField,
        ShakeLabel = computed.ShakeField != null ? computed.ShakeLabel : baseline.ShakeLabel,
        Patches = computed.Patches ?? baseline.Patches,
    };

    private async Task RunImmediateAsync(ImageFile file, string cacheKey,
        AnalysisResultCache.Entry baseline, AnalysisResultCache.Entry immediate,
        bool enhanced, bool dino, bool cv, CancellationToken ct)
    {
        DecodedImage? image = null;
        try
        {
            if (!enhanced)
            {
                await Task.Delay(FallbackDelayMs, ct).ConfigureAwait(false);
                for (int waited = 0; waited < 5000; waited += 120)
                {
                    bool ready = await UiDispatcher.InvokeAsync(() =>
                        _host.DisplayImage != null && !_host.ThumbnailsBusy).ConfigureAwait(false);
                    if (ready) break;
                    await Task.Delay(120, ct).ConfigureAwait(false);
                }
            }
            image = await UiDispatcher.InvokeAsync(() =>
            {
                ct.ThrowIfCancellationRequested();
                if (!ReferenceEquals(file, _host.CurrentFile) || !_host.AnalysisVisible
                    || enhanced != _host.EnhancedDisplay) return null;
                var source = _host.DisplayImage;
                return source?.TryAcquire() == true ? source : null;
            }).ConfigureAwait(false);
            if (image == null)
            {
                PostPlaceholder("未提取", ct, dino, cv);
                return;
            }
            var source = image;
            await Task.Run(() => ComputeDiagnosticsAsync(source, cacheKey, baseline, immediate, dino, cv, ct), ct)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Console.WriteLine($"[Analysis] immediate compute failed: {ex.Message}");
            PostFailure("分析失败", ct, dino, cv);
        }
        finally { image?.Release(); }
    }

    private async Task ComputeDiagnosticsAsync(DecodedImage image, string cacheKey,
        AnalysisResultCache.Entry baseline, AnalysisResultCache.Entry immediate,
        bool dino, bool cv, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        using var analysis = ColorEngine.ToAnalysisSrgb8(image);
        double aspect = (double)analysis.Width / analysis.Height;
        var successful = immediate;
        bool cvFailed = false;
        bool dinoFailed = false;
        if (cv)
        {
            try
            {
                var grid = await CvGridExtractor.ExtractAsync(analysis, ct).ConfigureAwait(false);
                var computed = AnalysisComputer.Compute(new AnalysisDataReader.Result
                {
                    Cv = grid, CvImageWidth = analysis.Width, CvImageHeight = analysis.Height,
                });
                successful = MergeEntries(successful, computed, aspect);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                cvFailed = true;
                Console.WriteLine($"[Analysis] immediate CV failed: {ex.Message}");
            }
        }
#if PHOTOVIEWER_DINO
        if (dino)
        {
            try
            {
                var (_, patches) = await DinoFeatureExtractor.ExtractDualAsync(analysis, includePatches: true, ct).ConfigureAwait(false);
                if (patches == null) throw new InvalidOperationException("DINO patch 输出为空");
                var computed = AnalysisComputer.Compute(new AnalysisDataReader.Result
                {
                    Patches = patches, CvImageWidth = analysis.Width, CvImageHeight = analysis.Height,
                });
                successful = MergeEntries(successful, computed, aspect);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                dinoFailed = true;
                Console.WriteLine($"[Analysis] immediate DINO failed: {ex.Message}");
            }
        }
#endif
        ct.ThrowIfCancellationRequested();
        if (successful.Patches != null || successful.SharpnessBmp != null)
            AnalysisResultCache.PutImmediate(cacheKey, successful);
        var result = MergeEntries(baseline, successful, aspect);
        UiDispatcher.Post(() =>
        {
            if (ct.IsCancellationRequested) return;
            ApplyEntry(result);
            if (cvFailed || dinoFailed) SetFailure("分析失败", dinoFailed, cvFailed);
        }, UiPriority.Background);
    }

    /// <summary>
    /// 从当前展示图现算 RGB 直方图。同一展示位图实例只算一次:已出结果直接换图,正在计算则不重复投递;
    /// 新实例接管时取消上一个在途计算。面板隐藏或无展示图时清空图形与「计算中」占位。
    /// </summary>
    private void RefreshHistogram()
    {
        var source = _host.DisplayImage;
        if (!_host.AnalysisVisible || _host.CurrentFile == null || source == null)
        {
            // ANALYSIS-07：无展示图或面板隐藏时清空直方图，不残留上一张的图形或“计算中”。
            CancelHistogramCompute();
            _histogramToken++;
            ClearHistogram(disposeCache: true);
            return;
        }

        // A/B 对比、增强开关来回切:同一展示位图实例已有结果就直接复用,不重算也不闪“计算中”。
        if (TryGetCachedHistogram(source, out var cached) && cached != null)
        {
            CancelHistogramCompute();
            _histogramToken++;
            if (!ReferenceEquals(_histogramTile.Source, cached) || _histogramTile.Placeholder != null)
                ShowHistogram(cached);
            return;
        }

        // 同一实例正在计算:保持“计算中”,不重复投递。
        if (ReferenceEquals(Volatile.Read(ref _histogramComputingSource), source)
            && Volatile.Read(ref _histogramCts) is { IsCancellationRequested: false })
            return;

        // 新请求接管:取消上一个在途计算,过期结果按 token 丢弃(仍会留在缓存里供回切复用)。
        CancelHistogramCompute();
        int token = ++_histogramToken;
        if (!source.TryAcquire())
        {
            ClearHistogram(disposeCache: false);
            return;
        }
        if (_histogramTile.Placeholder != "计算中")
        {
            _histogramTile.Placeholder = "计算中";
            TilesChanged?.Invoke();
        }

        var cts = new CancellationTokenSource();
        Interlocked.Exchange(ref _histogramCts, cts);
        Interlocked.Exchange(ref _histogramComputingSource, source);
        var ct = cts.Token;
        _ = Task.Run(() =>
        {
            try { return HistogramRenderer.Render(source, 512, 512, ct); }
            finally { source.Release(); }
        }, CancellationToken.None).ContinueWith(task =>
        {
            FinishHistogramCompute(source, cts);
            cts.Dispose();
            if (task.IsCanceled) return;
            if (task.IsFaulted)
            {
                var error = task.Exception?.GetBaseException();
                if (error is OperationCanceledException) return;
                Console.WriteLine($"[Analysis] histogram failed: {error?.Message}");
                UiDispatcher.Post(() => FailHistogram(token));
                return;
            }
            var decoded = task.Result;
            UiDispatcher.Post(() => CompleteHistogram(source, decoded, token));
        }, TaskScheduler.Default);
    }

    /// <summary>取消在途直方图计算;结果由任务续体按取消状态丢弃。</summary>
    private void CancelHistogramCompute()
    {
        var cts = Interlocked.Exchange(ref _histogramCts, null);
        Interlocked.Exchange(ref _histogramComputingSource, null);
        if (cts == null) return;
        try { cts.Cancel(); }
        catch (ObjectDisposedException) { /* 任务已结束并释放,无需再取消 */ }
    }

    /// <summary>任务结束(成功/取消/失败)后清掉在途标记;不匹配时说明已被新请求接管。</summary>
    private void FinishHistogramCompute(DecodedImage source, CancellationTokenSource cts)
    {
        Interlocked.CompareExchange(ref _histogramComputingSource, null, source);
        Interlocked.CompareExchange(ref _histogramCts, null, cts);
    }

    /// <summary>把源实例结果放入 2 项缓存;仍是当前请求时立即上屏。</summary>
    private void CompleteHistogram(DecodedImage source, DecodedImage bitmap, int token)
    {
        if (_disposed)
        {
            bitmap.Dispose();
            return;
        }
        AddHistogramEntry(source, bitmap);
        if (token != _histogramToken) return; // 过期结果留缓存,不覆盖当前上屏内容
        ShowHistogram(bitmap);
    }

    /// <summary>显示直方图位图(归 presenter/缓存所有);非缓存所有的旧图在视图更新后释放。</summary>
    private void ShowHistogram(DecodedImage bitmap)
    {
        var old = _histogram;
        _histogram = bitmap;
        _histogramTile.Source = bitmap;
        _histogramTile.Placeholder = null;
        TilesChanged?.Invoke();
        if (old != null && !ReferenceEquals(old, bitmap) && !IsHistogramCached(old)) UiDispatcher.Post(old.Dispose);
    }

    /// <summary>源实例弱引用 → 直方图位图;按最近使用顺序保留最近 2 份。</summary>
    private void AddHistogramEntry(DecodedImage source, DecodedImage bitmap)
    {
        for (int i = 0; i < _histogramCache.Count; i++)
        {
            if (ReferenceEquals(_histogramCache[i].Bitmap, bitmap)) return;
            if (_histogramCache[i].Source.TryGetTarget(out var target) && ReferenceEquals(target, source))
            {
                var replaced = _histogramCache[i];
                _histogramCache.RemoveAt(i);
                // 正在显示的旧图先留着,等 ShowHistogram 换掉后再按“非缓存所有”释放。
                if (!ReferenceEquals(replaced.Bitmap, _histogram)) replaced.Bitmap.Dispose();
                break;
            }
        }

        _histogramCache.Add(new HistogramEntry(source, bitmap));
        while (_histogramCache.Count > HistogramCacheCapacity)
        {
            int evict = _histogramCache.FindIndex(entry => !ReferenceEquals(entry.Bitmap, _histogram));
            if (evict < 0) break;
            var entry = _histogramCache[evict];
            _histogramCache.RemoveAt(evict);
            entry.Bitmap.Dispose();
        }
    }

    private bool TryGetCachedHistogram(DecodedImage source, out DecodedImage? bitmap)
    {
        for (int i = 0; i < _histogramCache.Count; i++)
        {
            if (_histogramCache[i].Source.TryGetTarget(out var target) && ReferenceEquals(target, source))
            {
                bitmap = _histogramCache[i].Bitmap;
                return true;
            }
        }
        bitmap = null;
        return false;
    }

    private bool IsHistogramCached(DecodedImage bitmap)
    {
        for (int i = 0; i < _histogramCache.Count; i++)
            if (ReferenceEquals(_histogramCache[i].Bitmap, bitmap)) return true;
        return false;
    }

    /// <summary>清空直方图显示;面板隐藏/no-image 时一并释放缓存位图,保留缓存时只清显示。</summary>
    private void ClearHistogram(bool disposeCache)
    {
        var old = _histogram;
        _histogram = null;
        _histogramTile.Source = null;
        _histogramTile.Placeholder = null;
        TilesChanged?.Invoke();
        if (disposeCache) ReleaseHistogramCache(old);
        else if (old != null && !IsHistogramCached(old)) UiDispatcher.Post(old.Dispose);
    }

    /// <summary>释放直方图缓存与当前显示位图(面板隐藏、presenter 释放时调用)。</summary>
    private void ReleaseHistogramCache(DecodedImage? displayed)
    {
        bool displayedInCache = displayed != null && IsHistogramCached(displayed);
        foreach (var entry in _histogramCache) entry.Bitmap.Dispose();
        _histogramCache.Clear();
        if (displayed != null && !displayedInCache) displayed.Dispose();
    }

    /// <summary>直方图计算失败:清空图形并显示独立失败文案,不停在“计算中”(ANALYSIS-07/10)。</summary>
    private void FailHistogram(int token)
    {
        if (token != _histogramToken) return;
        var old = _histogram;
        _histogram = null;
        _histogramTile.Source = null;
        _histogramTile.Placeholder = "分析失败";
        TilesChanged?.Invoke();
        if (old != null && !IsHistogramCached(old)) UiDispatcher.Post(old.Dispose);
    }

    /// <summary>
    /// 直方图结果缓存项:源位图实例 + 由 presenter 持有的直方图位图。
    /// </summary>
    private sealed class HistogramEntry
    {
        public HistogramEntry(DecodedImage source, DecodedImage bitmap)
        {
            Source = new WeakReference<DecodedImage>(source);
            Bitmap = bitmap;
        }

        /// <summary>源位图弱引用:不延长像素租约;源被回收后该结果只能等待淘汰。</summary>
        public WeakReference<DecodedImage> Source { get; }

        /// <summary>直方图位图(presenter 所有)。</summary>
        public DecodedImage Bitmap { get; }
    }

    /// <summary>
    /// 同步清空所有诊断瓦片(面板隐藏或当前照片为空时使用)。位图引用全部归 cache 所有,这里只清引用;
    /// 用户 cosine 图与直方图归 presenter 所有,显式释放。
    /// </summary>
    private void ClearDiagnostics()
    {
        _patchTokens = null;
        CrosshairU = null;
        CrosshairV = null;
        _sharpnessTile.Source = null;
        _sharpnessTile.Placeholder = "未提取";
        _shakeTile.ShakeField = null;
        _shakeTile.ShortLabel = "抖动拖影";
        _shakeTile.Placeholder = "未提取";
        AspectRatio = 1.0;
#if PHOTOVIEWER_DINO
        _pcaTile.Source = null;
        _cosineTile.Source = null;
        _pcaTile.Placeholder = "未提取";
        _cosineTile.Placeholder = "未提取";
        _cosineTile.ShortLabel = FormatCosineLabel(RefGridX, RefGridY);
        var old = _customCosine;
        _customCosine = null;
        old?.Dispose();
#endif
        TilesChanged?.Invoke();
        CrosshairChanged?.Invoke();
    }

    private void SetPlaceholder(string text, bool dino, bool cv)
    {
#if PHOTOVIEWER_DINO
        if (dino)
        {
            _pcaTile.Placeholder = text;
            _cosineTile.Placeholder = text;
        }
#endif
        if (cv)
        {
            _sharpnessTile.Placeholder = text;
            _shakeTile.Placeholder = text;
        }
        TilesChanged?.Invoke();
    }

    private void PostPlaceholder(string text, CancellationToken ct, bool dino, bool cv)
        => UiDispatcher.Post(() =>
        {
            if (ct.IsCancellationRequested) return;
            SetPlaceholder(text, dino, cv);
        }, UiPriority.Background);

    private void PostFailure(string text, CancellationToken ct)
        => UiDispatcher.Post(() =>
        {
            if (ct.IsCancellationRequested) return;
            ClearDiagnostics();
#if PHOTOVIEWER_DINO
            SetPlaceholder(text, true, true);
#else
            SetPlaceholder(text, false, true);
#endif
        }, UiPriority.Background);

    private void SetFailure(string text, bool dino, bool cv)
    {
#if PHOTOVIEWER_DINO
        if (dino)
        {
            _patchTokens = null;
            _pcaTile.Source = null;
            _cosineTile.Source = null;
            _customCosine?.Dispose();
            _customCosine = null;
        }
#endif
        if (cv)
        {
            _sharpnessTile.Source = null;
            _shakeTile.ShakeField = null;
            _shakeTile.ShortLabel = "抖动拖影";
        }
        SetPlaceholder(text, dino, cv);
    }

    private void PostFailure(string text, CancellationToken ct, bool dino, bool cv)
        => UiDispatcher.Post(() =>
        {
            if (ct.IsCancellationRequested) return;
            SetFailure(text, dino, cv);
        }, UiPriority.Background);

    /// <summary>跟踪当前文件的 EXIF 到达,驱动对焦点瓦片的增删。</summary>
    private void ObserveFile(ImageFile? file)
    {
        if (ReferenceEquals(file, _observedFile)) return;
        if (_observedFile != null) _observedFile.PropertyChanged -= OnObservedFileChanged;
        _observedFile = file;
        if (_observedFile != null) _observedFile.PropertyChanged += OnObservedFileChanged;
        UpdateFocusTile(_observedFile?.ExifData);
    }

    /// <summary>EXIF 到达或变化后更新对焦点瓦片;清空 EXIF 的瞬态不动瓦片,避免闪烁。</summary>
    private void OnObservedFileChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName != nameof(ImageFile.ExifData)) return;
        UiDispatcher.Post(() =>
        {
            if (_disposed || !ReferenceEquals(sender, _observedFile)) return;
            var exif = _observedFile?.ExifData;
            if (exif != null) UpdateFocusTile(exif);
        });
    }

    /// <summary>有 Sony 对焦数据时把对焦点瓦片插到第一位,否则移除。</summary>
    private void UpdateFocusTile(ExifData? exif)
    {
        if (exif?.SonyFocusPosition == null)
        {
            if (_focusInserted) { _tiles.Remove(_focusTile); _focusInserted = false; TilesChanged?.Invoke(); }
            return;
        }

        var pos = exif.SonyFocusPosition.Value;
        if (pos.ImageWidth <= 0 || pos.ImageHeight <= 0) return;

        _focusTile.CenterU = pos.FocusX / (double)pos.ImageWidth;
        _focusTile.CenterV = pos.FocusY / (double)pos.ImageHeight;
        if (exif.SonyFocusFrameSize is { } frame)
        {
            _focusTile.FocusFrameWidth = frame.Width / (double)pos.ImageWidth;
            _focusTile.FocusFrameHeight = frame.Height / (double)pos.ImageHeight;
        }
        else
        {
            _focusTile.FocusFrameWidth = null;
            _focusTile.FocusFrameHeight = null;
        }

        if (!_focusInserted) { _tiles.Insert(0, _focusTile); _focusInserted = true; }
        TilesChanged?.Invoke();
    }

    /// <summary>取消在途加载,保留已贴出的瓦片与准星。</summary>
    private void CancelPending()
    {
        _cts?.Cancel();
        _loadingFile = null;
    }

    /// <summary>同步计算指纹(仅当 EXIF 已加载时可用);失败返回 null。</summary>
    private static string? TryComputeFingerprintSync(ImageFile file)
    {
        var exif = file.ExifData;
        if (!file.ModifiedDate.HasValue && exif?.DateTimeOriginal == null) return null;
        var fallback = file.ModifiedDate?.UtcDateTime;
        var input = PhotoFingerprint.BuildInput(file.Name, exif, fallback);
        if (!input.CaptureTime.HasValue) return null;
        return PhotoFingerprint.Compute(input);
    }

    /// <summary>cosine 角标格式:省略"参考点"前缀,只留坐标,与"中心"/"对焦点"风格对齐。</summary>
    private static string FormatCosineLabel(int x, int y) => $"Cosine {x},{y}";

    /// <summary>取消在途任务并释放 presenter 持有的位图(含直方图缓存)。</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _host.HostChanged -= Refresh;
#if PHOTOVIEWER_DINO
        FolderFeatureIndexer.FeaturesWritten -= Refresh;
#endif
        ShakeFlagService.RecheckRequested -= Refresh;
        ObserveFile(null);
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
        _histogramToken++;
        CancelHistogramCompute();
        var displayed = _histogram;
        _histogram = null;
        _histogramTile.Source = null;
        ReleaseHistogramCache(displayed);
        _customCosine?.Dispose();
        _customCosine = null;
    }
}
