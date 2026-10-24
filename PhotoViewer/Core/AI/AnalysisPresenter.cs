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
/// 2. 增强预览联动:增强图就绪时对增强位图立即重算(CV + DINO 推理),不走 DB;
/// 3. 未提取回退:库里没有提取结果时,不再显示"未提取",改为对当前主图位图立即计算 —
///    先延迟再等主图 / 缩略图通道空闲,绝不抢主图浏览资源,期间占位显示"计算中…"。
/// 路径 2/3 由设置"分析诊断 / 即时计算DINO/CV"(默认关)门控;关闭时只走路径 1。
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
        _tiles = [_centerTile, _histogramTile, _pcaTile, _cosineTile, _sharpnessTile, _shakeTile];
        _host.HostChanged += Refresh;
        // 特征入库 / 清库后库内容变化：对当前照片重新路由，否则诊断瓦片会停在旧结果（含"未提取"空 entry）。
        FolderFeatureIndexer.FeaturesWritten += Refresh;
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
    }

    /// <summary>空白处点击:清空准星。</summary>
    public void ClearCrosshair()
    {
        if (CrosshairU == null && CrosshairV == null) return;
        CrosshairU = null;
        CrosshairV = null;
        CrosshairChanged?.Invoke();
    }

    /// <summary>Background 优先级回调:主图已渲染后才执行。快路径同步 swap;慢路径启动异步加载。</summary>
    private void LoadDeferred(ImageFile file, CancellationToken ct)
    {
        if (ct.IsCancellationRequested) return;

        // 增强模式且增强图就绪且开启即时计算 → 对增强图实时重算诊断瓦片(ONNX 推理 + CV),不走 DB。
        if (_host.ImmediateCompute && _host.EnhancedDisplay && _host.DisplayImage is { } enhanced)
        {
            var enhancedKey = AnalysisResultCache.ImmediateKey(file.File.StorageId.Value, enhanced: true);
            var enhancedHit = AnalysisResultCache.TryGetImmediate(enhancedKey);
            if (enhancedHit != null) ApplyEntry(enhancedHit);
            else
            {
                SetPlaceholder("计算中…");
                _ = RunImmediateAsync(enhanced, enhancedKey, ct);
            }
            return;
        }

        // 快路径:EXIF 已加载 + 缓存命中 → 直接 swap。
        if (file.IsExifLoaded)
        {
            var fp = TryComputeFingerprintSync(file);
            var hit = fp != null ? AnalysisResultCache.TryGet(fp) : null;
            if (hit != null)
            {
                ApplyEntry(hit);
                // 命中空 entry(未提取)且开启即时计算 → 回退到立即计算;已有即时结果则直接 swap。
                if (IsEntryEmpty(hit) && _host.ImmediateCompute)
                {
                    var imm = AnalysisResultCache.TryGetImmediate(file.File.StorageId.Value);
                    if (imm != null) ApplyEntry(imm);
                    else
                    {
                        SetPlaceholder("计算中…");
                        _ = LoadFallbackAsync(file, ct);
                    }
                }
                return;
            }
        }

        // 慢路径:异步加载,不清空旧图(避免 layout storm)。
        _ = LoadAsync(file, ct);
    }

    /// <summary>
    /// 慢路径异步加载:算指纹 → 查缓存 → miss 则读库 + 派生层现算 + 落 cache → UI swap。
    /// </summary>
    private async Task LoadAsync(ImageFile file, CancellationToken ct)
    {
        try
        {
            ct.ThrowIfCancellationRequested();

            var fingerprint = await AnalysisDataReader.ComputeFingerprintAsync(file, ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();

            // 命中 cache:不读库、不重算,直接 UI swap。
            var hit = AnalysisResultCache.TryGet(fingerprint);
            if (hit != null)
            {
                PostEntry(hit, ct);
                return;
            }

            // miss:读库 → 派生层现算 → 落 cache。指纹缺失(无 EXIF 时间戳)直接喂空 Result。
            var data = string.IsNullOrEmpty(fingerprint)
                ? new AnalysisDataReader.Result()
                : await AnalysisDataReader.ReadByFingerprintAsync(fingerprint, ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();

            var entry = AnalysisComputer.Compute(data);
            if (!string.IsNullOrEmpty(fingerprint))
                AnalysisResultCache.Put(fingerprint, entry);

            ct.ThrowIfCancellationRequested();
            PostEntry(entry, ct);

            // 库里没有提取结果且开启即时计算 → 回退到立即计算。
            if (data.IsEmpty && _host.ImmediateCompute)
            {
                var imm = AnalysisResultCache.TryGetImmediate(file.File.StorageId.Value);
                if (imm != null)
                {
                    PostEntry(imm, ct);
                    return;
                }
                PostPlaceholder("计算中…", ct);
                await LoadFallbackAsync(file, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // 被覆盖,忽略
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Analysis] load failed: {ex.Message}");
            // ANALYSIS-10：失败不得停在“计算中”，清掉旧瓦片并进入独立失败态。
            PostFailure("分析失败", ct);
        }
    }

    /// <summary>把 cache 项内容贴到 UI 线程;过期 token 直接丢弃。</summary>
    private void PostEntry(AnalysisResultCache.Entry entry, CancellationToken ct)
        => UiDispatcher.Post(() =>
        {
            if (ct.IsCancellationRequested) return;
            ApplyEntry(entry);
        }, UiPriority.Background);

    /// <summary>把 Entry 贴到诊断瓦片。位图归 cache 所有(含即时缓存),presenter 只引用不释放。</summary>
    private void ApplyEntry(AnalysisResultCache.Entry entry)
    {
        _patchTokens = entry.Patches;
        AspectRatio = entry.AspectRatio;

        _sharpnessTile.Source = entry.SharpnessBmp;
        _sharpnessTile.Placeholder = entry.SharpnessBmp == null ? "未提取" : null;

        _pcaTile.Source = entry.PcaBmp;
        _pcaTile.Placeholder = entry.PcaBmp == null ? "未提取" : null;

        // 切图时还原到中心参考点;用户点击产生的位图归 presenter,这里释放。
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

        _shakeTile.ShakeField = entry.ShakeField;
        _shakeTile.ShortLabel = entry.ShakeLabel;
        _shakeTile.Placeholder = entry.ShakeField == null ? "未提取" : null;

        TilesChanged?.Invoke();
    }

    /// <summary>立即计算的三种结果:成功 / 被取消 / 失败(用于把失败与“未提取”区分开,ANALYSIS-10)。</summary>
    private enum ComputeOutcome
    {
        Success,
        Canceled,
        Failed,
    }

    /// <summary>
    /// 立即计算:对指定位图跑 CV 7 标量 + DINO patch 推理 → 复用 <see cref="AnalysisComputer.Compute"/> 出诊断瓦片。
    /// 增强预览联动与未提取回退共用;不读库、不写库。产物按 <paramref name="cacheKey"/> 落即时缓存。
    /// 展示图先转成 CV/DINO 需要的 8 位显示朝向口径(已符合时复用同一对象),后台读取前先取一份租约,
    /// 避免宿主在计算期间释放展示图。
    /// </summary>
    private async Task<ComputeOutcome> ComputeDiagnosticsAsync(DecodedImage image, string cacheKey, CancellationToken ct)
    {
        if (!image.TryAcquire()) return ComputeOutcome.Failed;
        DecodedImage? analysis = null;
        try
        {
            ct.ThrowIfCancellationRequested();
            analysis = ColorEngine.ToAnalysisSrgb8(image);
            int cvW = analysis.Width;
            int cvH = analysis.Height;

            var cv = await CvGridExtractor.ExtractAsync(analysis, ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            var (_, patches) = await DinoFeatureExtractor.ExtractDualAsync(analysis, includePatches: true, ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();

            var data = new AnalysisDataReader.Result
            {
                Patches = patches,
                Cv = cv,
                CvImageWidth = cvW,
                CvImageHeight = cvH,
            };
            var entry = AnalysisComputer.Compute(data);
            ct.ThrowIfCancellationRequested();
            AnalysisResultCache.PutImmediate(cacheKey, entry);
            PostEntry(entry, ct);
            return ComputeOutcome.Success;
        }
        catch (OperationCanceledException)
        {
            return ComputeOutcome.Canceled;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Analysis] immediate compute failed: {ex.Message}");
            return ComputeOutcome.Failed;
        }
        finally
        {
            analysis?.Release();
            image.Release();
        }
    }

    /// <summary>增强图即时计算:失败时进入独立失败态,不把“计算中…”留在界面上(ANALYSIS-10)。</summary>
    private async Task RunImmediateAsync(DecodedImage image, string cacheKey, CancellationToken ct)
    {
        var outcome = await ComputeDiagnosticsAsync(image, cacheKey, ct).ConfigureAwait(false);
        if (outcome == ComputeOutcome.Failed && !ct.IsCancellationRequested)
            PostFailure("分析失败", ct);
    }

    /// <summary>
    /// 未提取回退:库 / cache 都没有该照片结果时,对当前主图位图立即计算诊断瓦片。
    /// 为保护主图浏览性能:先延迟 <see cref="FallbackDelayMs"/>,再让位等待主图解码完成且缩略图通道空闲。
    /// </summary>
    private async Task LoadFallbackAsync(ImageFile file, CancellationToken ct)
    {
        try
        {
            await Task.Delay(FallbackDelayMs, ct).ConfigureAwait(false);

            // 让位:主图未解码完或缩略图通道忙时退避,上限 5s 后放行。
            int waited = 0;
            while (!ct.IsCancellationRequested && waited < 5000)
            {
                if (_host.DisplayImage != null && !_host.ThumbnailsBusy) break;
                await Task.Delay(120, ct).ConfigureAwait(false);
                waited += 120;
            }
            ct.ThrowIfCancellationRequested();

            // 延迟 / 让位期间可能已被其他路径算好,命中则直接贴,不再重算。
            var cacheKey = AnalysisResultCache.ImmediateKey(file.File.StorageId.Value, enhanced: false);
            var imm = AnalysisResultCache.TryGetImmediate(cacheKey);
            if (imm != null)
            {
                PostEntry(imm, ct);
                return;
            }

            var source = _host.DisplayImage;
            if (source == null)
            {
                PostPlaceholder("未提取", ct);
                return;
            }

            var outcome = await ComputeDiagnosticsAsync(source, cacheKey, ct).ConfigureAwait(false);
            if (outcome == ComputeOutcome.Failed && !ct.IsCancellationRequested)
                PostFailure("分析失败", ct);
        }
        catch (OperationCanceledException)
        {
            // 被新任务覆盖,忽略
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Analysis] fallback compute failed: {ex.Message}");
            PostFailure("分析失败", ct);
        }
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
        _pcaTile.Source = null;
        _cosineTile.Source = null;
        _sharpnessTile.Source = null;
        _pcaTile.Placeholder = "未提取";
        _cosineTile.Placeholder = "未提取";
        _cosineTile.ShortLabel = FormatCosineLabel(RefGridX, RefGridY);
        _sharpnessTile.Placeholder = "未提取";
        _shakeTile.ShakeField = null;
        _shakeTile.ShortLabel = "抖动拖影";
        _shakeTile.Placeholder = "未提取";
        AspectRatio = 1.0;

        var old = _customCosine;
        _customCosine = null;
        old?.Dispose();
        TilesChanged?.Invoke();
        CrosshairChanged?.Invoke();
    }

    /// <summary>设置 4 个诊断瓦片的占位文本(调用方须在 UI 线程;在 <see cref="ApplyEntry"/> 之后调用以覆盖"未提取")。</summary>
    private void SetPlaceholder(string text)
    {
        _pcaTile.Placeholder = text;
        _cosineTile.Placeholder = text;
        _sharpnessTile.Placeholder = text;
        _shakeTile.Placeholder = text;
        TilesChanged?.Invoke();
    }

    /// <summary>Background 优先级设置占位文本;供后台路径在 <see cref="PostEntry"/> 之后调用(同优先级 FIFO,保证顺序)。</summary>
    private void PostPlaceholder(string text, CancellationToken ct)
        => UiDispatcher.Post(() =>
        {
            if (ct.IsCancellationRequested) return;
            SetPlaceholder(text);
        }, UiPriority.Background);

    /// <summary>
    /// 后台失败态:清空诊断瓦片与旧结果后显示失败文案,避免把上一张的图挂在当前照片下、
    /// 也避免无限停在“计算中”(ANALYSIS-10)。
    /// </summary>
    private void PostFailure(string text, CancellationToken ct)
        => UiDispatcher.Post(() =>
        {
            if (ct.IsCancellationRequested) return;
            ClearDiagnostics();
            SetPlaceholder(text);
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

    /// <summary>cache entry 是否为空(未提取):DINO patch 与 CV 锐度均缺失。</summary>
    private static bool IsEntryEmpty(AnalysisResultCache.Entry entry) => entry.Patches == null && entry.SharpnessBmp == null;

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
        FolderFeatureIndexer.FeaturesWritten -= Refresh;
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
