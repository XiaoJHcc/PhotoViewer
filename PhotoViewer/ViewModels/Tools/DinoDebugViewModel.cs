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
/// DINO 诊断工具页 VM。一次性对当前图片并行跑 CV 网格（v3 中心采样 + 边宽量化 + 32 网格 + log 映射）
/// 与 DINO 双输出推理，暴露缩略图 + 锐度热力图 + 抖动矢量场 + 刚体拟合文本 + PCA-RGB + cosine 图。
/// CV 走 BitmapLoader 原始分辨率解码（与 ImageView 共用 LRU 缓存），DINO 仍走 560 短边缩略图。
/// 切图即释放，不入库。
/// </summary>
public sealed class DinoDebugViewModel : ReactiveObject
{
    /// <summary>DINO 推理输入短边（与 FolderFeatureIndexer 一致）。</summary>
    private const int DinoShortSide = 560;

    /// <summary>CV 网格可视化像素边长（16），XAML 端放大显示。</summary>
    public const int CvGridPixels = CvGridResult.GridSize;

    /// <summary>DINO patch 图像素边长（32）。</summary>
    public const int PatchGridPixels = PatchHeatmap.Grid;

    private CancellationTokenSource? _cts;
    private float[]? _patchTokens;

    private bool _isBusy;
    /// <summary>是否正在后台计算。</summary>
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
    /// <summary>原图缩略图（短边 560，DINO 一致的输入）。</summary>
    public Bitmap? SourceThumbnail
    {
        get => _sourceThumbnail;
        private set => this.RaiseAndSetIfChanged(ref _sourceThumbnail, value);
    }

    private double _aspectRatio = 1.0;
    /// <summary>当前图片长宽比（宽/高）。</summary>
    public double AspectRatio
    {
        get => _aspectRatio;
        private set => this.RaiseAndSetIfChanged(ref _aspectRatio, value);
    }

    private Point? _crosshair;
    /// <summary>全局十字准星位置（归一化 [0,1]，null 隐藏）。</summary>
    public Point? Crosshair
    {
        get => _crosshair;
        private set => this.RaiseAndSetIfChanged(ref _crosshair, value);
    }

    private Bitmap? _sharpnessMap;
    /// <summary>锐度热力图（edge_width_p20 → viridis，NaN 显灰）。</summary>
    public Bitmap? SharpnessMap
    {
        get => _sharpnessMap;
        private set => this.RaiseAndSetIfChanged(ref _sharpnessMap, value);
    }

    private ShakeField? _shakeField;
    /// <summary>抖动矢量场（16×16 方向/位移/掩膜）；View 消费它绘制 Canvas 线段。</summary>
    public ShakeField? ShakeField
    {
        get => _shakeField;
        private set => this.RaiseAndSetIfChanged(ref _shakeField, value);
    }

    private string _rigidMotionText = "未加载";
    /// <summary>刚体拟合的多行文本结果。</summary>
    public string RigidMotionText
    {
        get => _rigidMotionText;
        private set => this.RaiseAndSetIfChanged(ref _rigidMotionText, value);
    }

    private Bitmap? _pcaRgbMap;
    /// <summary>patch token 的前 3 主成分 RGB 预览。</summary>
    public Bitmap? PcaRgbMap
    {
        get => _pcaRgbMap;
        private set => this.RaiseAndSetIfChanged(ref _pcaRgbMap, value);
    }

    private Bitmap? _cosineMap;
    /// <summary>以 RefGridX/Y 为参考的 cosine 相似度图。</summary>
    public Bitmap? CosineMap
    {
        get => _cosineMap;
        private set => this.RaiseAndSetIfChanged(ref _cosineMap, value);
    }

    private int _refGridX = 16;
    /// <summary>cosine 参考 patch 的 x（0..31）。</summary>
    public int RefGridX
    {
        get => _refGridX;
        private set => this.RaiseAndSetIfChanged(ref _refGridX, value);
    }

    private int _refGridY = 16;
    /// <summary>cosine 参考 patch 的 y（0..31）。</summary>
    public int RefGridY
    {
        get => _refGridY;
        private set => this.RaiseAndSetIfChanged(ref _refGridY, value);
    }

    /// <summary>切换源图片：取消旧任务 → 并行解码 DINO/CV 两路 → 跑推理与提取 → 渲染。</summary>
    public void SetSource(ImageFile? file)
    {
        _cts?.Cancel();
        var cts = new CancellationTokenSource();
        _cts = cts;
        _ = LoadAsync(file, cts.Token);
    }

    /// <summary>点击任意瓦片：落十字准星 + 以点击位置作 cosine 参考点（映射到 32×32）。</summary>
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

    /// <summary>空闲区域点击：清空十字准星。</summary>
    public void ClearCrosshair()
    {
        Crosshair = null;
    }

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

        Bitmap? dinoBitmap = null;
        Bitmap? cvBitmap = null;
        bool thumbnailHandedOff = false;
        try
        {
            // DINO 560 缩略图与 CV 原始分辨率两路解码并行。
            // CV 必须在原始像素上计算，任何下采样都会损害 Marziliano 边宽的测量精度。
            var dinoTaskDecode = ThumbnailService.GetThumbnailAsync(file.File, DinoShortSide);
            var cvTaskDecode = BitmapLoader.GetBitmapAsync(file.File);
            await Task.WhenAll(dinoTaskDecode, cvTaskDecode).ConfigureAwait(false);
            dinoBitmap = dinoTaskDecode.Result;
            cvBitmap = cvTaskDecode.Result;

            if (dinoBitmap == null)
            {
                ClearAllOnUi("无法解码图片");
                return;
            }

            ct.ThrowIfCancellationRequested();

            int bw = dinoBitmap.PixelSize.Width;
            int bh = dinoBitmap.PixelSize.Height;
            double ratio = bh > 0 ? (double)bw / bh : 1.0;

            // 并行跑 CV（大图）与 DINO（小图）；若 CV 大图解码失败，降级用 DINO 小图（短边可能不足，CvGridExtractor 会抛异常由 catch 兜底）。
            var cvSourceBitmap = cvBitmap ?? dinoBitmap;
            var cvTask = CvGridExtractor.ExtractAsync(cvSourceBitmap, ct);
            var dinoTask = DinoFeatureExtractor.ExtractDualAsync(dinoBitmap, includePatches: true, ct);
            await Task.WhenAll(cvTask, dinoTask).ConfigureAwait(false);

            var cv = cvTask.Result;
            var (_, patches) = dinoTask.Result;

            ct.ThrowIfCancellationRequested();

            // CV：锐度图 + 抖动矢量场 + 刚体拟合
            var sharpness = CvHeatmap.BuildSharpness(cv);
            var sharpnessBmp = HeatmapBitmapBuilder.BuildViridis(sharpness, CvGridPixels, CvGridPixels);
            var shakeField = CvHeatmap.BuildShakeField(cv);
            var rigid = CvHeatmap.FitRigidMotion(shakeField);
            var rigidText = FormatRigidMotion(rigid);

            // DINO：PCA-RGB + 初始 cosine（中心参考点）
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

            var thumbnailToShow = dinoBitmap;
            _patchTokens = patches;
            thumbnailHandedOff = true;

            Dispatcher.UIThread.Post(() =>
            {
                if (ct.IsCancellationRequested)
                {
                    sharpnessBmp?.Dispose();
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
                SwapBitmap(ref _sharpnessMap, sharpnessBmp, nameof(SharpnessMap));
                ShakeField = shakeField;
                RigidMotionText = rigidText;
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
            if (!thumbnailHandedOff) dinoBitmap?.Dispose();
            // CV 大图来自 BitmapLoader 的 LRU 缓存，由 ImageView 共用，不在此处 Dispose。
        }
    }

    /// <summary>
    /// 把刚体拟合结果格式化为三行文本：样本数 / 平移 / 旋转 / 残差 + 语义标签。
    /// </summary>
    private static string FormatRigidMotion(RigidMotionResult rigid)
    {
        string label;
        if (rigid.SampleCount < 6)
        {
            label = "静止或信号不足";
        }
        else if (rigid.ResidualRms > Math.Max(rigid.TranslationMagnitude, 4f))
        {
            label = "混乱场景（车流 / 树叶）";
        }
        else if (rigid.TranslationMagnitude >= 3f)
        {
            label = "疑似平移手抖";
        }
        else if (rigid.RotationMagnitude >= 0.02f)
        {
            label = "疑似旋转手抖";
        }
        else
        {
            label = "静止纹理";
        }
        return $"样本 {rigid.SampleCount} 格\n" +
               $"|T| = {rigid.TranslationMagnitude:F2} px\n" +
               $"|ω| = {rigid.RotationMagnitude:F3} rad\n" +
               $"残差 = {rigid.ResidualRms:F2} px\n" +
               $"判定:{label}";
    }

    private void ClearAllOnUi(string status)
    {
        Dispatcher.UIThread.Post(() =>
        {
            SwapBitmap(ref _sourceThumbnail, null, nameof(SourceThumbnail));
            SwapBitmap(ref _sharpnessMap, null, nameof(SharpnessMap));
            ShakeField = null;
            RigidMotionText = "未加载";
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
