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
/// 准星与 cosine 参考点的语义复制自 <see cref="PhotoViewer.ViewModels.Tools.DinoDebugViewModel"/>:
/// 点击诊断瓦片 → 落归一化准星 → 映射到 32×32 patch 网格 → 重算 cosine 项 Source。
/// 上半的细节预览不参与准星(其点击被 DetailPreview 自身吃掉)。
/// </summary>
public sealed class AnalysisViewModel : ReactiveObject
{
    /// <summary>DINO patch 图像素边长(32);也用于 cosine 参考点坐标系。</summary>
    public const int PatchGridPixels = PatchHeatmap.Grid;

    private readonly MainViewModel _main;
    private CancellationTokenSource? _cts;
    private float[]? _patchTokens;
    private ImageFile? _lastFile;

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

        // 跟踪 ExifData:既驱动对焦点项的增删,也是分析栏本身重新读库的入口
        // (LoadExifDataAsync 是 ImageFile 内部异步,首次切图时 ExifData 可能延后到位)。
        _main.WhenAnyValue(vm => vm.CurrentFile)
            .Select(f => f?.WhenAnyValue(x => x.ExifData) ?? Observable.Return<ExifData?>(null))
            .Switch()
            .Subscribe(Observer.Create<ExifData?>(exif =>
            {
                UpdateFocusPointItem(exif);
                if (_main.IsAnalysisViewVisible) SetSource(_main.CurrentFile);
            }));
    }

    /// <summary>外部入口:换图时刷新整个分析栏。可见性关闭时早退,避免后台读库泄漏。</summary>
    public void SetSource(ImageFile? file)
    {
        _cts?.Cancel();
        _lastFile = file;

        if (!_main.IsAnalysisViewVisible || file == null)
        {
            ClearDiagnostics();
            return;
        }

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
        SwapItemBitmap(_cosineItem, b => b.Source, (it, b) => it.Source = b, bmp);
        _cosineItem.ShortLabel = FormatCosineLabel(gx, gy);
    }

    /// <summary>空白处点击:清空准星(列表外的 PointerPressed 路由进来)。</summary>
    public void ClearCrosshair()
    {
        Crosshair = null;
    }

    /// <summary>抽自 <see cref="DetailViewModel.UpdateFocusPointItem"/>:有 Sony 对焦数据时把对焦点项插到第一位。</summary>
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

    /// <summary>后台只读路径:读库 → 派生层现算 4 张图与判定文字 → 在 UI 线程整体 swap。</summary>
    private async Task LoadAsync(ImageFile file, CancellationToken ct)
    {
        try
        {
            var data = await AnalysisDataReader.TryReadAsync(file, ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();

            // CV 派生层(锐度图 / ShakeField / 刚体拟合 + 判定文字)
            Bitmap? sharpnessBmp = null;
            ShakeField? shakeField = null;
            string shakeLabel = "抖动拖影";
            double aspect = _aspectRatio;
            int cvW = data.CvImageWidth;
            int cvH = data.CvImageHeight;
            if (data.Cv != null && cvW > 0 && cvH > 0)
            {
                aspect = (double)cvW / cvH;
                float diagonal = MathF.Sqrt((float)cvW * cvW + (float)cvH * cvH);
                var sharpness = CvHeatmap.BuildSharpness(data.Cv);
                sharpnessBmp = HeatmapBitmapBuilder.BuildViridis(sharpness, CvGridResult.GridSize, CvGridResult.GridSize);
                shakeField = CvHeatmap.BuildShakeField(data.Cv, diagonal);
                var rigid = CvHeatmap.FitRigidMotion(shakeField);
                var verdict = ShakeClassifier.Classify(rigid, diagonal);
                shakeLabel = ShakeClassifier.FormatLabel(verdict);
            }

            // DINO 派生层(PCA-RGB + cosine 中心参考点)
            Bitmap? pcaBmp = null;
            Bitmap? cosineBmp = null;
            int rx = RefGridX;
            int ry = RefGridY;
            if (data.Patches != null)
            {
                var pcaRgb = PatchHeatmap.ComputePcaRgb(data.Patches);
                pcaBmp = HeatmapBitmapBuilder.BuildRgb(pcaRgb, PatchGridPixels, PatchGridPixels);

                // 同一指纹组切图时保留旧参考点;首次或缓存清空则重置到中心。
                rx = Math.Clamp(rx, 0, PatchGridPixels - 1);
                ry = Math.Clamp(ry, 0, PatchGridPixels - 1);
                var cos = PatchHeatmap.ComputeRefCosine(data.Patches, rx, ry);
                cosineBmp = HeatmapBitmapBuilder.BuildViridis(cos, PatchGridPixels, PatchGridPixels);
            }

            ct.ThrowIfCancellationRequested();

            Dispatcher.UIThread.Post(() =>
            {
                if (ct.IsCancellationRequested)
                {
                    sharpnessBmp?.Dispose();
                    pcaBmp?.Dispose();
                    cosineBmp?.Dispose();
                    return;
                }

                _patchTokens = data.Patches;
                AspectRatio = aspect > 0 ? aspect : 1.0;

                SwapItemBitmap(_sharpnessItem, it => it.Source, (it, b) => it.Source = b, sharpnessBmp);
                _sharpnessItem.PlaceholderText = sharpnessBmp == null ? "未提取" : null;

                SwapItemBitmap(_pcaItem, it => it.Source, (it, b) => it.Source = b, pcaBmp);
                _pcaItem.PlaceholderText = pcaBmp == null ? "未提取" : null;

                SwapItemBitmap(_cosineItem, it => it.Source, (it, b) => it.Source = b, cosineBmp);
                _cosineItem.PlaceholderText = cosineBmp == null ? "未提取" : null;
                RefGridX = rx;
                RefGridY = ry;
                _cosineItem.ShortLabel = FormatCosineLabel(rx, ry);

                _shakeOverlay.ShakeField = shakeField;
                _shakeItem.ShortLabel = shakeLabel;
                _shakeItem.PlaceholderText = shakeField == null ? "未提取" : null;
            });
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

    /// <summary>清空所有诊断瓦片(可见性切到 false 或当前文件为 null 时)。细节预览项不动。</summary>
    private void ClearDiagnostics()
    {
        Dispatcher.UIThread.Post(() =>
        {
            _patchTokens = null;
            Crosshair = null;
            SwapItemBitmap(_pcaItem, it => it.Source, (it, b) => it.Source = b, null);
            SwapItemBitmap(_cosineItem, it => it.Source, (it, b) => it.Source = b, null);
            SwapItemBitmap(_sharpnessItem, it => it.Source, (it, b) => it.Source = b, null);
            _pcaItem.PlaceholderText = "未提取";
            _cosineItem.PlaceholderText = "未提取";
            _cosineItem.ShortLabel = FormatCosineLabel(RefGridX, RefGridY);
            _sharpnessItem.PlaceholderText = "未提取";
            _shakeOverlay.ShakeField = null;
            _shakeItem.ShortLabel = "抖动拖影";
            _shakeItem.PlaceholderText = "未提取";
        });
    }

    /// <summary>统一的位图赋值 + 旧位图释放。</summary>
    private static void SwapItemBitmap(AnalysisDiagnosticItem item, Func<AnalysisDiagnosticItem, Bitmap?> get, Action<AnalysisDiagnosticItem, Bitmap?> set, Bitmap? next)
    {
        var old = get(item);
        set(item, next);
        old?.Dispose();
    }

    /// <summary>cosine 角标格式:省略"参考点"前缀,只留坐标,与"中心"/"对焦点"风格对齐。</summary>
    private static string FormatCosineLabel(int x, int y) => $"Cosine {x},{y}";
}
