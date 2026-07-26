using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reactive;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using PhotoViewer.Core;
using PhotoViewer.Core.AI;
using PhotoViewer.Core.Database;
using PhotoViewer.Core.Image;
using PhotoViewer.Views.Tools;
using ReactiveUI;

namespace PhotoViewer.ViewModels.Main;

/// <summary>
/// 分析栏 VM。把"细节预览"(对焦点 / 中心)与"DINO/CV 诊断"(PCA / Cosine / 锐度 / 抖动)
/// 合并到一个常驻侧栏。诊断瓦片有三条数据来源路径:
/// 1. 只读库:指纹 → <see cref="AnalysisResultCache"/> → DB,不触发解码 / ONNX 推理 / CV 重算;
/// 2. 增强预览联动:增强图就绪时对增强位图立即重算(CV + DINO 推理),不走 DB;
/// 3. 未提取回退:库里没有提取结果时,不再显示"未提取",改为对当前主图位图立即计算 —
///    先延迟再等主图 / 缩略图通道空闲,绝不抢主图浏览资源,期间占位显示"计算中…"。
/// 立即计算(路径 2/3)的产物按文件路径落 <see cref="AnalysisResultCache"/> 的即时缓存
/// (增强变体单独 key;随主图缓存清理一起失效,也被"清除特征数据库"一并清空),不写库。
///
/// 性能优化:派生数据按指纹走 <see cref="AnalysisResultCache"/>,与 <see cref="BitmapPrefetcher"/> 邻居预取
/// 同步预热;命中时切图变成纯 UI 线程 swap,避免每次切图重跑 PCA SVD(几十 ms 主因)与抖动场重算。
/// 4 张诊断瓦片的位图(PCA / 中心 cosine / 锐度 / 抖动)归 cache 所有 — VM 仅引用、不 Dispose;
/// 用户点击诊断瓦片重算 cosine 时产生的位图归 VM 所有(<see cref="_customCosineBmp"/>),切图或还原时显式释放。
///
/// 准星与 cosine 参考点的语义复制自 <see cref="PhotoViewer.ViewModels.Tools.DinoDebugViewModel"/>:
/// 点击诊断瓦片 → 落归一化准星 → 映射到 32×32 patch 网格 → 重算 cosine 项 Source。
/// 上半的细节预览不参与准星(其点击被 DetailPreview 自身吃掉)。
/// </summary>
public sealed class AnalysisViewModel : ReactiveObject
{
    /// <summary>DINO patch 图像素边长(32);也用于 cosine 参考点坐标系。</summary>
    public const int PatchGridPixels = AnalysisComputer.PatchGridPixels;

    /// <summary>未提取回退的启动延迟(ms):快速翻页时在延迟内即被 _cts 取消,零 CV/推理开销。</summary>
    private const int FallbackDelayMs = 500;

    private readonly MainViewModel _main;
    private CancellationTokenSource? _cts;
    private ImageFile? _loadingFile; // 当前正在加载的文件,用于去重 ExifData 回调
    private float[]? _patchTokens;
    private Bitmap? _customCosineBmp; // 用户点击重算的 cosine 位图(VM 所有);中心 cosine 位图归 cache 所有,不在此持有。

    private Bitmap? _histogramBmp;     // 直方图位图(VM 所有),随主图切换重算
    private int _histogramToken;       // 直方图竞态闸

    // 固定项引用,避免每次切图重建集合(列表项作为 DataContext 不会被回收)。
    private readonly AnalysisDetailItem _focusItem;          // 动态:有 Sony 对焦数据时存在
    private readonly AnalysisDetailItem _centerItem;
    private readonly AnalysisHistogramItem _histogramItem;
    private readonly AnalysisDiagnosticItem _pcaItem;
    private readonly AnalysisDiagnosticItem _cosineItem;
    private readonly AnalysisDiagnosticItem _sharpnessItem;
    private readonly AnalysisDiagnosticItem _shakeItem;
    private readonly ShakeFieldView _shakeOverlay = new();

    /// <summary>是否处于行布局(影响 ItemsControl 的 StackPanel 朝向)。</summary>
    public bool IsRowLayout => _main.IsRowLayout;

    /// <summary>分析栏当前是否可见 — DetailPreview 的 IsActive 等都吃这个。</summary>
    public bool IsAnalysisViewVisible => _main.IsAnalysisViewVisible;

    /// <summary>主图引用,DetailPreview 联动主图绿框时使用。</summary>
    public ImageViewModel ImageVM => _main.ImageVM;

    /// <summary>主图位图,DetailPreview 项裁剪时使用。</summary>
    public Bitmap? SourceBitmap => _main.ImageVM.SourceBitmap;

    private double _previewSize = 300;
    /// <summary>瓦片显示尺寸(像素正方形外框宽度)。</summary>
    public double PreviewSize
    {
        get => _previewSize;
        set => this.RaiseAndSetIfChanged(ref _previewSize, value);
    }

    private int _cropSize = 300;
    /// <summary>DetailPreview 的裁剪窗口像素大小。</summary>
    public int CropSize
    {
        get => _cropSize;
        set => this.RaiseAndSetIfChanged(ref _cropSize, value);
    }

    private double _aspectRatio = 1.0;
    /// <summary>诊断瓦片的图像长宽比(宽/高);从 cv_image_width/height 推。</summary>
    public double AspectRatio
    {
        get => _aspectRatio;
        private set => this.RaiseAndSetIfChanged(ref _aspectRatio, value);
    }

    private Point? _crosshair;
    /// <summary>诊断瓦片共享的归一化准星;null 时不显示。</summary>
    public Point? Crosshair
    {
        get => _crosshair;
        private set => this.RaiseAndSetIfChanged(ref _crosshair, value);
    }

    private int _refGridX = PatchGridPixels / 2;
    /// <summary>cosine 参考点 x(0..31)。</summary>
    public int RefGridX
    {
        get => _refGridX;
        private set => this.RaiseAndSetIfChanged(ref _refGridX, value);
    }

    private int _refGridY = PatchGridPixels / 2;
    /// <summary>cosine 参考点 y(0..31)。</summary>
    public int RefGridY
    {
        get => _refGridY;
        private set => this.RaiseAndSetIfChanged(ref _refGridY, value);
    }

    /// <summary>分析栏列表项;按需插入对焦点。</summary>
    public ObservableCollection<AnalysisItem> Items { get; }

    /// <summary>构造,订阅主 VM 的可见性、当前图、布局、主位图等变化。</summary>
    public AnalysisViewModel(MainViewModel main)
    {
        _main = main;

        _focusItem = new AnalysisDetailItem("对焦点", new Point(0.5, 0.5));
        _centerItem = new AnalysisDetailItem("中心", new Point(0.5, 0.5));
        _histogramItem = new AnalysisHistogramItem("直方图");
        _pcaItem = new AnalysisDiagnosticItem("DINO PCA");
        _cosineItem = new AnalysisDiagnosticItem(FormatCosineLabel(_refGridX, _refGridY));
        _sharpnessItem = new AnalysisDiagnosticItem("锐度");
        _shakeItem = new AnalysisDiagnosticItem("抖动拖影")
        {
            Overlay = _shakeOverlay
        };

        Items = new ObservableCollection<AnalysisItem>
        {
            _centerItem,
            _histogramItem,
            _pcaItem,
            _cosineItem,
            _sharpnessItem,
            _shakeItem,
        };

        _main.WhenAnyValue(vm => vm.IsRowLayout)
            .Subscribe(Observer.Create<bool>(_ => this.RaisePropertyChanged(nameof(IsRowLayout))));

        _main.WhenAnyValue(vm => vm.IsAnalysisViewVisible)
            .Subscribe(Observer.Create<bool>(visible =>
            {
                this.RaisePropertyChanged(nameof(IsAnalysisViewVisible));
                if (visible) SetSource(_main.CurrentFile);
                else _cts?.Cancel();
                RefreshHistogram();
            }));

        // 主图位图变化(含原片↔增强切换):刷新 DetailPreview 绑定 + 重算直方图。
        _main.ImageVM.WhenAnyValue(vm => vm.SourceBitmap)
            .Subscribe(Observer.Create<Bitmap?>(_ =>
            {
                this.RaisePropertyChanged(nameof(SourceBitmap));
                RefreshHistogram();
            }));

        // 增强开关 / 增强图就绪 → 重新路由诊断瓦片(增强时对增强图实时重算,关闭时回落 DB)。
        _main.ImageVM.WhenAnyValue(vm => vm.IsEnhanced)
            .Subscribe(Observer.Create<bool>(_ => SetSource(_main.CurrentFile)));
        _main.ImageVM.WhenAnyValue(vm => vm.EnhancedBitmap)
            .Subscribe(Observer.Create<Bitmap?>(_ =>
            {
                if (_main.ImageVM.IsEnhanced) SetSource(_main.CurrentFile);
            }));

        _main.WhenAnyValue(vm => vm.CurrentFile)
            .Subscribe(Observer.Create<ImageFile?>(SetSource));

        // 跟踪 ExifData:驱动对焦点项的增删;仅当指纹尚未算出时才重新触发分析加载
        // (首次切图时 ExifData 可能延后到位,此时指纹依赖 EXIF 时间戳)。
        _main.WhenAnyValue(vm => vm.CurrentFile)
            .Select(f => f?.WhenAnyValue(x => x.ExifData) ?? Observable.Return<ExifData?>(null))
            .Switch()
            .Subscribe(Observer.Create<ExifData?>(exif =>
            {
                // ExifData 变 null 是 ClearExifData 的瞬态(如星级写入后重载),不动对焦点项,避免闪烁。
                if (exif != null)
                    UpdateFocusPointItem(exif);
                // 仅当 LoadAsync 尚未为当前文件启动时才重新触发,避免重复加载
                if (_main.IsAnalysisViewVisible && _main.CurrentFile != null && _main.CurrentFile != _loadingFile)
                    SetSource(_main.CurrentFile);
            }));
    }

    /// <summary>外部入口:换图时刷新整个分析栏。可见性关闭时早退,避免后台读库泄漏。
    /// 所有实际工作推迟到 Background 优先级,绝不阻塞主图加载。</summary>
    public void SetSource(ImageFile? file)
    {
        _cts?.Cancel();
        _loadingFile = file;

        if (!_main.IsAnalysisViewVisible || file == null)
        {
            ClearDiagnosticsSync();
            return;
        }

        var cts = new CancellationTokenSource();
        _cts = cts;
        Dispatcher.UIThread.Post(() => LoadDeferred(file, cts.Token), DispatcherPriority.Background);
    }

    /// <summary>Background 优先级回调:主图已渲染后才执行。快路径同步 swap;慢路径启动异步加载。</summary>
    private void LoadDeferred(ImageFile file, CancellationToken ct)
    {
        if (ct.IsCancellationRequested) return;

        // 增强模式且增强图就绪 → 对增强图实时重算诊断瓦片(ONNX 推理 + CV),不走 DB。
        if (_main.ImageVM.IsEnhanced && _main.ImageVM.EnhancedBitmap is Bitmap enhanced)
        {
            var enhancedKey = AnalysisResultCache.ImmediateKey(file.File.Path.LocalPath, enhanced: true);
            var enhancedHit = AnalysisResultCache.TryGetImmediate(enhancedKey);
            if (enhancedHit != null) ApplyEntrySync(enhancedHit);
            else _ = ComputeDiagnosticsAsync(enhanced, enhancedKey, ct);
            return;
        }

        // 快路径:EXIF 已加载 + 缓存命中 → 直接 swap。
        if (file.IsExifLoaded)
        {
            var fp = TryComputeFingerprintSync(file);
            var hit = fp != null ? AnalysisResultCache.TryGet(fp) : null;
            if (hit != null)
            {
                ApplyEntrySync(hit);
                // 命中空 entry(未提取,慢路径 / 预热都会把空结果落 cache)→ 回退到立即计算;
                // 先查即时缓存,已有结果则直接 swap 覆盖空 entry,不再重算。
                if (IsEntryEmpty(hit))
                {
                    var imm = AnalysisResultCache.TryGetImmediate(file.File.Path.LocalPath);
                    if (imm != null) ApplyEntrySync(imm);
                    else
                    {
                        SetDiagnosticPlaceholder("计算中…");
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
    /// 诊断瓦片点击:落准星 + 把点击位置作 cosine 参考点(映射到 32×32);上半 DetailPreview 的点击不会到这。
    /// </summary>
    public void OnTileClicked(double u, double v)
    {
        u = Math.Clamp(u, 0, 1);
        v = Math.Clamp(v, 0, 1);
        Crosshair = new Point(u, v);

        if (_patchTokens == null) return;
        int gx = Math.Clamp((int)(u * PatchGridPixels), 0, PatchGridPixels - 1);
        int gy = Math.Clamp((int)(v * PatchGridPixels), 0, PatchGridPixels - 1);
        if (gx == RefGridX && gy == RefGridY && _cosineItem.Source != null) return;

        RefGridX = gx;
        RefGridY = gy;

        var cos = PatchHeatmap.ComputeRefCosine(_patchTokens, gx, gy);
        var bmp = HeatmapBitmapBuilder.BuildViridis(cos, PatchGridPixels, PatchGridPixels);

        // 用户点击重算的 cosine 位图归 VM 所有;旧的同样归 VM 才 dispose,中心位图(cache 所有)不动。
        var old = _customCosineBmp;
        _customCosineBmp = bmp;
        _cosineItem.Source = bmp;
        _cosineItem.ShortLabel = FormatCosineLabel(gx, gy);
        old?.Dispose();
    }

    /// <summary>空白处点击:清空准星(列表外的 PointerPressed 路由进来)。</summary>
    public void ClearCrosshair()
    {
        Crosshair = null;
    }

    /// <summary>有 Sony 对焦数据时把对焦点项插到第一位,根据 ExifData 动态增删。</summary>
    private void UpdateFocusPointItem(ExifData? exif)
    {
        var existingIndex = Items.IndexOf(_focusItem);

        if (exif?.SonyFocusPosition == null)
        {
            if (existingIndex >= 0) Items.Remove(_focusItem);
            return;
        }

        var pos = exif.SonyFocusPosition.Value;
        if (pos.ImageWidth <= 0 || pos.ImageHeight <= 0) return;

        var cx = pos.FocusX / (double)pos.ImageWidth;
        var cy = pos.FocusY / (double)pos.ImageHeight;

        Size? focusFrame = null;
        if (exif.SonyFocusFrameSize.HasValue)
        {
            var fs = exif.SonyFocusFrameSize.Value;
            focusFrame = new Size(
                fs.Width / (double)pos.ImageWidth,
                fs.Height / (double)pos.ImageHeight);
        }

        _focusItem.Center = new Point(cx, cy);
        _focusItem.FocusFrame = focusFrame;
        if (existingIndex < 0) Items.Insert(0, _focusItem);
    }

    /// <summary>
    /// 慢路径异步加载:算指纹 → 查缓存 → miss 则读库 + 派生层现算 + 落 cache → UI swap。
    /// 调用方已在 Background 优先级,无需再 yield。
    /// </summary>
    private async Task LoadAsync(ImageFile file, CancellationToken ct)
    {
        try
        {
            ct.ThrowIfCancellationRequested();

            var fingerprint = await AnalysisDataReader.ComputeFingerprintAsync(file, ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();

            // 1) 命中 cache:不读库、不重算,直接 UI swap;
            //    注意:用户点击 cosine 留下的自定义位图必须先释放,因为接下来要把 Source 切回 cache 拥有的中心位图。
            var hit = AnalysisResultCache.TryGet(fingerprint);
            if (hit != null)
            {
                ApplyEntry(hit, ct);
                return;
            }

            // 2) miss:读库 → 派生层现算 → 落 cache,然后再 UI swap。指纹缺失(无 EXIF 时间戳)
            //    直接喂空 Result。
            var data = string.IsNullOrEmpty(fingerprint)
                ? new AnalysisDataReader.Result()
                : await AnalysisDataReader.ReadByFingerprintAsync(fingerprint, ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();

            var entry = AnalysisComputer.Compute(data);
            if (!string.IsNullOrEmpty(fingerprint))
                AnalysisResultCache.Put(fingerprint, entry);

            ct.ThrowIfCancellationRequested();
            ApplyEntry(entry, ct);

            // 3) 库里没有提取结果 → 回退到立即计算。先查即时缓存(翻回已算过的照片直接 swap);
            //    miss 则占位改"计算中…",延迟 + 让位后对当前主图跑 CV+DINO。
            if (data.IsEmpty)
            {
                var imm = AnalysisResultCache.TryGetImmediate(file.File.Path.LocalPath);
                if (imm != null)
                {
                    ApplyEntry(imm, ct);
                    return;
                }
                PostDiagnosticPlaceholder("计算中…", ct);
                await LoadFallbackAsync(file, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // 被覆盖,忽略
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[AnalysisVM] load failed: {ex.Message}");
        }
    }

    /// <summary>把 cache 项内容贴到 UI:全部位图引用归 cache,VM 只引用,不 Dispose。中心 cosine 还原时释放历史用户位图。
    /// 使用 Background 优先级,确保主图渲染优先完成。</summary>
    private void ApplyEntry(AnalysisResultCache.Entry entry, CancellationToken ct)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (ct.IsCancellationRequested) return;
            ApplyEntryCore(entry);
        }, DispatcherPriority.Background);
    }

    /// <summary>
    /// 把 Entry 贴到诊断瓦片。位图归 cache 所有(含即时缓存),VM 只引用不释放。
    /// </summary>
    private void ApplyEntryCore(AnalysisResultCache.Entry entry)
    {
        _patchTokens = entry.Patches;
        AspectRatio = entry.AspectRatio;

        _sharpnessItem.Source = entry.SharpnessBmp;
        _sharpnessItem.PlaceholderText = entry.SharpnessBmp == null ? "未提取" : null;

        _pcaItem.Source = entry.PcaBmp;
        _pcaItem.PlaceholderText = entry.PcaBmp == null ? "未提取" : null;

        // 切图时还原到中心参考点;若有历史用户点击位图,这里释放(它由 VM 拥有)。
        var oldCustom = _customCosineBmp;
        _customCosineBmp = null;
        int rx = PatchGridPixels / 2;
        int ry = PatchGridPixels / 2;
        RefGridX = rx;
        RefGridY = ry;
        _cosineItem.Source = entry.CenterCosineBmp;
        _cosineItem.PlaceholderText = entry.CenterCosineBmp == null ? "未提取" : null;
        _cosineItem.ShortLabel = FormatCosineLabel(rx, ry);
        oldCustom?.Dispose();

        _shakeOverlay.ShakeField = entry.ShakeField;
        _shakeItem.ShortLabel = entry.ShakeLabel;
        _shakeItem.PlaceholderText = entry.ShakeField == null ? "未提取" : null;
    }

    /// <summary>
    /// 立即计算:对指定位图跑 CV 7 标量 + DINO patch 推理 → 复用 <see cref="AnalysisComputer.Compute"/> 出诊断瓦片。
    /// 增强预览联动与未提取回退共用;不读库、不写库。产物按 <paramref name="cacheKey"/> 落
    /// <see cref="AnalysisResultCache"/> 的即时缓存(随主图缓存清理失效),位图归 cache 所有。
    /// 返回是否成功贴出结果(被取消或失败均返回 false)。
    /// </summary>
    private async Task<bool> ComputeDiagnosticsAsync(Bitmap bitmap, string cacheKey, CancellationToken ct)
    {
        try
        {
            ct.ThrowIfCancellationRequested();
            int cvW = bitmap.PixelSize.Width;
            int cvH = bitmap.PixelSize.Height;

            // CV 走位图原始分辨率;DINO 直接喂入(内部缩放到 518)。
            var cv = await CvGridExtractor.ExtractAsync(bitmap, ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            var (_, patches) = await DinoFeatureExtractor.ExtractDualAsync(bitmap, includePatches: true, ct).ConfigureAwait(false);
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
            ApplyEntry(entry, ct);
            return true;
        }
        catch (OperationCanceledException)
        {
            // 被新任务覆盖,忽略
            return false;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[AnalysisVM] immediate compute failed: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// 未提取回退:库 / cache 都没有该照片结果时,对当前主图位图立即计算诊断瓦片。
    /// 为保护主图浏览性能:先延迟 <see cref="FallbackDelayMs"/>(快速翻页在此即被 _cts 取消,零开销),
    /// 再让位等待主图解码完成且缩略图通道空闲(策略同 BitmapPrefetcher.WaitForHighPriorityIdleAsync);
    /// 产物落即时缓存(按文件路径,随主图缓存清理失效)、不写库。
    /// </summary>
    private async Task LoadFallbackAsync(ImageFile file, CancellationToken ct)
    {
        try
        {
            await Task.Delay(FallbackDelayMs, ct).ConfigureAwait(false);

            // 让位:主图未解码完(SourceBitmap 为空)或缩略图通道忙时退避,上限 5s 后放行。
            int waited = 0;
            while (!ct.IsCancellationRequested && waited < 5000)
            {
                if (_main.ImageVM.SourceBitmap != null && !_main.FileVM.ThumbnailList.IsThumbnailLoadingBusy())
                    break;
                await Task.Delay(120, ct).ConfigureAwait(false);
                waited += 120;
            }
            ct.ThrowIfCancellationRequested();

            // 延迟 / 让位期间可能已被其他路径算好(如来回翻页),命中则直接贴,不再重算。
            var cacheKey = AnalysisResultCache.ImmediateKey(file.File.Path.LocalPath, enhanced: false);
            var imm = AnalysisResultCache.TryGetImmediate(cacheKey);
            if (imm != null)
            {
                ApplyEntry(imm, ct);
                return;
            }

            // 非增强时 SourceBitmap 即原片(归 BitmapLoader 缓存所有,VM 不 Dispose),后台读取安全。
            var source = _main.ImageVM.SourceBitmap;
            if (source == null)
            {
                PostDiagnosticPlaceholder("未提取", ct);
                return;
            }

            bool ok = await ComputeDiagnosticsAsync(source, cacheKey, ct).ConfigureAwait(false);
            if (!ok && !ct.IsCancellationRequested)
                PostDiagnosticPlaceholder("未提取", ct);
        }
        catch (OperationCanceledException)
        {
            // 被新任务覆盖,忽略
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[AnalysisVM] fallback compute failed: {ex.Message}");
            PostDiagnosticPlaceholder("未提取", ct);
        }
    }

    /// <summary>cache entry 是否为空(未提取):DINO patch 与 CV 锐度均缺失,对应 <see cref="AnalysisDataReader.Result.IsEmpty"/>。</summary>
    private static bool IsEntryEmpty(AnalysisResultCache.Entry entry) =>
        entry.Patches == null && entry.SharpnessBmp == null;

    /// <summary>设置 4 个诊断瓦片的占位文本(调用方须在 UI 线程;在 ApplyEntryCore 之后调用以覆盖"未提取")。</summary>
    private void SetDiagnosticPlaceholder(string text)
    {
        _pcaItem.PlaceholderText = text;
        _cosineItem.PlaceholderText = text;
        _sharpnessItem.PlaceholderText = text;
        _shakeItem.PlaceholderText = text;
    }

    /// <summary>Background 优先级设置占位文本;供后台路径在 ApplyEntry 之后调用(同优先级 FIFO,保证顺序)。</summary>
    private void PostDiagnosticPlaceholder(string text, CancellationToken ct)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (ct.IsCancellationRequested) return;
            SetDiagnosticPlaceholder(text);
        }, DispatcherPriority.Background);
    }

    /// <summary>从当前主图(原片或增强图)现算 RGB 直方图,随主图 / 可见性变化触发;不可见或无图时清空。</summary>
    private async void RefreshHistogram()
    {
        int token = ++_histogramToken;
        var source = _main.ImageVM.SourceBitmap;
        if (!_main.IsAnalysisViewVisible || source == null)
        {
            SwapHistogram(null, token);
            return;
        }
        try
        {
            var bmp = await Task.Run(() => HistogramRenderer.Render(source, 512, 512));
            SwapHistogram(bmp, token);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[AnalysisVM] histogram failed: {ex.Message}");
        }
    }

    /// <summary>替换直方图位图(VM 所有),过期 token 直接丢弃;旧位图延后一拍释放。</summary>
    private void SwapHistogram(Bitmap? next, int token)
    {
        if (token != _histogramToken)
        {
            next?.Dispose();
            return;
        }
        var old = _histogramBmp;
        _histogramBmp = next;
        _histogramItem.Source = next;
        if (old != null) Dispatcher.UIThread.Post(old.Dispose);
    }

    /// <summary>同步清空所有诊断瓦片(面板隐藏或文件为 null 时使用)。细节预览项不动。
    /// 位图引用全部归 cache 所有,这里只清引用、不 Dispose;用户 cosine 位图归 VM 所有,显式释放。</summary>
    private void ClearDiagnosticsSync()
    {
        _patchTokens = null;
        Crosshair = null;
        _pcaItem.Source = null;
        _cosineItem.Source = null;
        _sharpnessItem.Source = null;
        _pcaItem.PlaceholderText = "未提取";
        _cosineItem.PlaceholderText = "未提取";
        _cosineItem.ShortLabel = FormatCosineLabel(RefGridX, RefGridY);
        _sharpnessItem.PlaceholderText = "未提取";
        _shakeOverlay.ShakeField = null;
        _shakeItem.ShortLabel = "抖动拖影";
        _shakeItem.PlaceholderText = "未提取";

        var old = _customCosineBmp;
        _customCosineBmp = null;
        old?.Dispose();
    }

    /// <summary>快路径同步应用 cache 项(调用方已在 UI 线程,EXIF 已加载 + 缓存命中)。
    /// 走 <see cref="ApplyEntryCore"/>,不经 Dispatcher.Post,零帧延迟。</summary>
    private void ApplyEntrySync(AnalysisResultCache.Entry entry) => ApplyEntryCore(entry);

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
}
