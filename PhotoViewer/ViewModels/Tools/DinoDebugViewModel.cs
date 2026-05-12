using ReactiveUI;
using System;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using PhotoViewer.Core.AI;
using PhotoViewer.Core.Image;

namespace PhotoViewer.ViewModels.Tools;

/// <summary>
/// DINO 诊断工具页 VM。一次性对当前图片同时跑 CV 网格提取 + DINO 双输出推理，
/// 暴露原始缩略图 + 3 张 CV 诊断图(失焦 / 抖动 / 金字塔一致性) + PCA-RGB + 点击 cosine 热力图。
/// 本期不入库:每次 <see cref="SetSource(ImageFile)"/> 现算,切图即释放。
/// τ 与归一化档当前固定默认值,后续阶段再补交互。
/// </summary>
public sealed class DinoDebugViewModel : ReactiveObject
{
    /// <summary>喂给 DINO/CV 的短边像素(与 FolderFeatureIndexer 一致)。</summary>
    private const int FeaturingShortSide = 560;

    /// <summary>CV 诊断图像素边长(16),渲染时 XAML 端放大显示。</summary>
    public const int CvGridPixels = 16;

    /// <summary>DINO patch 图像素边长(32)。</summary>
    public const int PatchGridPixels = PatchHeatmap.Grid;

    private CancellationTokenSource? _cts;
    private float[]? _patchTokens;

    // ── 状态 ────────────────────────────────────────────────────────────────

    private bool _isBusy;
    /// <summary>是否正在后台计算。界面用于显示加载指示并禁用参考点击。</summary>
    public bool IsBusy
    {
        get => _isBusy;
        private set => this.RaiseAndSetIfChanged(ref _isBusy, value);
    }

    private string _status = "未加载";
    /// <summary>状态文本。</summary>
    public string Status
    {
        get => _status;
        private set => this.RaiseAndSetIfChanged(ref _status, value);
    }

    private Bitmap? _sourceThumbnail;
    /// <summary>原图缩略图(短边 560,已应用 EXIF 旋转)。瓦片第一张用它,其他瓦片共享其长宽比。</summary>
    public Bitmap? SourceThumbnail
    {
        get => _sourceThumbnail;
        private set => this.RaiseAndSetIfChanged(ref _sourceThumbnail, value);
    }

    private double _aspectRatio = 1.0;
    /// <summary>当前图片长宽比(宽/高)。所有诊断瓦片按此比值 Stretch=Fill,保证与缩略图视觉对齐。</summary>
    public double AspectRatio
    {
        get => _aspectRatio;
        private set => this.RaiseAndSetIfChanged(ref _aspectRatio, value);
    }

    private Point? _crosshair;
    /// <summary>
    /// 全局十字准星位置(归一化 [0,1],null 隐藏)。点击任意瓦片设为该点坐标,点击空闲区域清空。
    /// 诊断瓦片共享此值,作为跨图的对应关系指示。
    /// </summary>
    public Point? Crosshair
    {
        get => _crosshair;
        private set => this.RaiseAndSetIfChanged(ref _crosshair, value);
    }

    // ── 三张 CV 诊断图 ──────────────────────────────────────────────────────

    private Bitmap? _defocusMap;
    /// <summary>失焦诊断图(laplacian_var × sobel_mean,viridis)。</summary>
    public Bitmap? DefocusMap
    {
        get => _defocusMap;
        private set => this.RaiseAndSetIfChanged(ref _defocusMap, value);
    }

    private Bitmap? _motionBlurMap;
    /// <summary>抖动诊断图(低熵掩膜,grayscale)。</summary>
    public Bitmap? MotionBlurMap
    {
        get => _motionBlurMap;
        private set => this.RaiseAndSetIfChanged(ref _motionBlurMap, value);
    }

    private Bitmap? _pyramidMap;
    /// <summary>金字塔一致性图(lap 变异系数,viridis)。</summary>
    public Bitmap? PyramidMap
    {
        get => _pyramidMap;
        private set => this.RaiseAndSetIfChanged(ref _pyramidMap, value);
    }

    // ── DINO 两张图 ─────────────────────────────────────────────────────────

    private Bitmap? _pcaRgbMap;
    /// <summary>patch token 的前 3 主成分 RGB 预览。</summary>
    public Bitmap? PcaRgbMap
    {
        get => _pcaRgbMap;
        private set => this.RaiseAndSetIfChanged(ref _pcaRgbMap, value);
    }

    private Bitmap? _cosineMap;
    /// <summary>以 <see cref="RefGridX"/>/<see cref="RefGridY"/> 为参考的 cosine 相似度图。</summary>
    public Bitmap? CosineMap
    {
        get => _cosineMap;
        private set => this.RaiseAndSetIfChanged(ref _cosineMap, value);
    }

    private int _refGridX = 16;
    /// <summary>cosine 参考 patch 的 x(0..31)。</summary>
    public int RefGridX
    {
        get => _refGridX;
        private set => this.RaiseAndSetIfChanged(ref _refGridX, value);
    }

    private int _refGridY = 16;
    /// <summary>cosine 参考 patch 的 y(0..31)。</summary>
    public int RefGridY
    {
        get => _refGridY;
        private set => this.RaiseAndSetIfChanged(ref _refGridY, value);
    }

    // ── 对外 API ────────────────────────────────────────────────────────────

    /// <summary>
    /// 切换源图片。取消旧任务 → 加载缩略图 → 并行跑 CV + DINO 双输出 → 渲染 5 张图。
    /// 所有位图在 UI 线程赋值,旧值同步 Dispose 避免泄漏。
    /// </summary>
    public void SetSource(ImageFile? file)
    {
        _cts?.Cancel();
        var cts = new CancellationTokenSource();
        _cts = cts;
        _ = LoadAsync(file, cts.Token);
    }

    /// <summary>
    /// 用户点击任意诊断瓦片时:落十字准星 + 以点击位置作 cosine 参考点(映射到 32×32)。
    /// </summary>
    /// <param name="u">归一化横坐标 [0,1]。</param>
    /// <param name="v">归一化纵坐标 [0,1]。</param>
    public void OnTileClicked(double u, double v)
    {
        u = Math.Clamp(u, 0, 1);
        v = Math.Clamp(v, 0, 1);
        Crosshair = new Point(u, v);

        if (_patchTokens == null) return;
        int gx = Math.Clamp((int)(u * PatchGridPixels), 0, PatchGridPixels - 1);
        int gy = Math.Clamp((int)(v * PatchGridPixels), 0, PatchGridPixels - 1);
        if (gx == RefGridX && gy == RefGridY && CosineMap != null) return;

        RefGridX = gx;
        RefGridY = gy;

        var cos = PatchHeatmap.ComputeRefCosine(_patchTokens, gx, gy);
        var bmp = HeatmapBitmapBuilder.BuildViridis(cos, PatchGridPixels, PatchGridPixels);
        SwapBitmap(ref _cosineMap, bmp, nameof(CosineMap));
    }

    /// <summary>用户在瓦片外空闲区域点击:清空十字准星(cosine 参考点保持最近一次)。</summary>
    public void ClearCrosshair()
    {
        Crosshair = null;
    }

    // ── 内部流程 ────────────────────────────────────────────────────────────

    private async Task LoadAsync(ImageFile? file, CancellationToken ct)
    {
        if (file == null)
        {
            ClearAllOnUi("未加载");
            return;
        }

        Dispatcher.UIThread.Post(() =>
        {
            IsBusy = true;
            Status = $"计算中…  {file.Name}";
        });

        Bitmap? bitmap = null;
        bool thumbnailHandedOff = false;
        try
        {
            bitmap = await ThumbnailService.GetThumbnailAsync(file.File, FeaturingShortSide).ConfigureAwait(false);
            if (bitmap == null)
            {
                ClearAllOnUi("无法解码图片");
                return;
            }

            ct.ThrowIfCancellationRequested();

            int bw = bitmap.PixelSize.Width;
            int bh = bitmap.PixelSize.Height;
            double ratio = bh > 0 ? (double)bw / bh : 1.0;

            // 并行跑 CV + DINO(DINO 内部自带互斥 gate,这里只需要并行触发)
            var cvTask = CvGridExtractor.ExtractAsync(bitmap, ct);
            var dinoTask = DinoFeatureExtractor.ExtractDualAsync(bitmap, includePatches: true, ct);
            await Task.WhenAll(cvTask, dinoTask).ConfigureAwait(false);

            var cv = cvTask.Result;
            var (_, patches) = dinoTask.Result;

            ct.ThrowIfCancellationRequested();

            // CV 三张:默认 PerPlane 归一化
            var defocusRaw = CvHeatmap.BuildDefocus(cv);
            var motionRaw = CvHeatmap.BuildMotionBlur(cv); // 已经是 0/1,归一化为恒等
            var pyramidRaw = CvHeatmap.BuildPyramidConsistency(cv);

            var defocusNorm = CvHeatmap.Normalize(defocusRaw, CvHeatmapNormalize.PerPlane);
            var pyramidNorm = CvHeatmap.Normalize(pyramidRaw, CvHeatmapNormalize.PerPlane);

            var defocusBmp = HeatmapBitmapBuilder.BuildViridis(defocusNorm, CvGridPixels, CvGridPixels);
            var motionBmp = HeatmapBitmapBuilder.BuildGrayscale(motionRaw, CvGridPixels, CvGridPixels);
            var pyramidBmp = HeatmapBitmapBuilder.BuildViridis(pyramidNorm, CvGridPixels, CvGridPixels);

            // DINO 两张:PCA-RGB + 初始 cosine(中心参考点)
            Bitmap? pcaBmp = null;
            Bitmap? cosineBmp = null;
            int rx = PatchGridPixels / 2;
            int ry = PatchGridPixels / 2;
            if (patches != null)
            {
                var pcaRgb = PatchHeatmap.ComputePcaRgb(patches);
                pcaBmp = HeatmapBitmapBuilder.BuildRgb(pcaRgb, PatchGridPixels, PatchGridPixels);

                var cos = PatchHeatmap.ComputeRefCosine(patches, rx, ry);
                cosineBmp = HeatmapBitmapBuilder.BuildViridis(cos, PatchGridPixels, PatchGridPixels);
            }

            var thumbnailToShow = bitmap;
            _patchTokens = patches;
            thumbnailHandedOff = true;

            Dispatcher.UIThread.Post(() =>
            {
                if (ct.IsCancellationRequested)
                {
                    defocusBmp?.Dispose();
                    motionBmp?.Dispose();
                    pyramidBmp?.Dispose();
                    pcaBmp?.Dispose();
                    cosineBmp?.Dispose();
                    thumbnailToShow.Dispose();
                    return;
                }
                SwapBitmap(ref _sourceThumbnail, thumbnailToShow, nameof(SourceThumbnail));
                AspectRatio = ratio > 0 ? ratio : 1.0;
                Crosshair = null;
                RefGridX = rx;
                RefGridY = ry;
                SwapBitmap(ref _defocusMap, defocusBmp, nameof(DefocusMap));
                SwapBitmap(ref _motionBlurMap, motionBmp, nameof(MotionBlurMap));
                SwapBitmap(ref _pyramidMap, pyramidBmp, nameof(PyramidMap));
                SwapBitmap(ref _pcaRgbMap, pcaBmp, nameof(PcaRgbMap));
                SwapBitmap(ref _cosineMap, cosineBmp, nameof(CosineMap));
                Status = file.Name;
                IsBusy = false;
            });
        }
        catch (OperationCanceledException)
        {
            // 被新任务覆盖,保持原样
        }
        catch (Exception ex)
        {
            ClearAllOnUi($"失败: {ex.Message}");
        }
        finally
        {
            if (!thumbnailHandedOff) bitmap?.Dispose();
        }
    }

    private void ClearAllOnUi(string status)
    {
        Dispatcher.UIThread.Post(() =>
        {
            SwapBitmap(ref _sourceThumbnail, null, nameof(SourceThumbnail));
            SwapBitmap(ref _defocusMap, null, nameof(DefocusMap));
            SwapBitmap(ref _motionBlurMap, null, nameof(MotionBlurMap));
            SwapBitmap(ref _pyramidMap, null, nameof(PyramidMap));
            SwapBitmap(ref _pcaRgbMap, null, nameof(PcaRgbMap));
            SwapBitmap(ref _cosineMap, null, nameof(CosineMap));
            _patchTokens = null;
            Crosshair = null;
            Status = status;
            IsBusy = false;
        });
    }

    private void SwapBitmap(ref Bitmap? field, Bitmap? next, string propertyName)
    {
        var old = field;
        field = next;
        this.RaisePropertyChanged(propertyName);
        old?.Dispose();
    }
}
