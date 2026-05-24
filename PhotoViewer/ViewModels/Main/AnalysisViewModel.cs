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
using PhotoViewer.Core.Image;
using PhotoViewer.Views.Tools;
using ReactiveUI;

namespace PhotoViewer.ViewModels.Main;

/// <summary>
/// 分析栏 VM。把"细节预览"(对焦点 / 中心)与"DINO/CV 诊断"(PCA / Cosine / 锐度 / 抖动)
/// 合并到一个常驻侧栏,只读库 — 不触发解码、不触发 ONNX 推理、不触发 CV 重算;
/// 缓存缺失时诊断瓦片显示"未提取"占位,引导用户去相似聚类面板"提取全部"。
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

    private readonly MainViewModel _main;
    private CancellationTokenSource? _cts;
    private ImageFile? _loadingFile; // 当前正在加载的文件,用于去重 ExifData 回调
    private float[]? _patchTokens;
    private Bitmap? _customCosineBmp; // 用户点击重算的 cosine 位图(VM 所有);中心 cosine 位图归 cache 所有,不在此持有。

    // 6 项的固定引用,避免每次切图重建集合(列表项作为 DataContext 不会被回收)。
    private readonly AnalysisDetailItem _focusItem;          // 动态:有 Sony 对焦数据时存在
    private readonly AnalysisDetailItem _centerItem;
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
            }));

        _main.ImageVM.WhenAnyValue(vm => vm.SourceBitmap)
            .Subscribe(Observer.Create<Bitmap?>(_ => this.RaisePropertyChanged(nameof(SourceBitmap))));

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

    /// <summary>外部入口:换图时刷新整个分析栏。可见性关闭时早退,避免后台读库泄漏。</summary>
    public void SetSource(ImageFile? file)
    {
        _cts?.Cancel();
        _loadingFile = file;

        if (!_main.IsAnalysisViewVisible || file == null)
        {
            ClearDiagnosticsSync();
            return;
        }

        // 立即置空诊断瓦片,让用户看到即时响应;实际数据异步填充,不阻塞主图加载。
        ClearDiagnosticsSync();

        var cts = new CancellationTokenSource();
        _cts = cts;
        _ = LoadAsync(file, cts.Token);
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
    /// 切图主路径:让出 UI 线程 → 算指纹 → 查 <see cref="AnalysisResultCache"/> → 命中即 UI swap;miss 才走读库 + 派生层现算 + 落 cache。
    /// 开头 yield 确保主图加载优先获得 I/O 和 CPU 资源。
    /// </summary>
    private async Task LoadAsync(ImageFile file, CancellationToken ct)
    {
        try
        {
            // 让出执行权,确保主图加载先行;分析栏允许延后填充。
            await Task.Yield();
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
            //    直接喂空 Result,所有诊断瓦片回落"未提取"。
            var data = string.IsNullOrEmpty(fingerprint)
                ? new AnalysisDataReader.Result()
                : await AnalysisDataReader.ReadByFingerprintAsync(fingerprint, ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();

            var entry = AnalysisComputer.Compute(data);
            if (!string.IsNullOrEmpty(fingerprint))
                AnalysisResultCache.Put(fingerprint, entry);

            ct.ThrowIfCancellationRequested();
            ApplyEntry(entry, ct);
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
        }, DispatcherPriority.Background);
    }

    /// <summary>同步清空所有诊断瓦片(调用方已在 UI 线程)。细节预览项不动。
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

    /// <summary>cosine 角标格式:省略"参考点"前缀,只留坐标,与"中心"/"对焦点"风格对齐。</summary>
    private static string FormatCosineLabel(int x, int y) => $"Cosine {x},{y}";
}
