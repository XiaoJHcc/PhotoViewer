using ReactiveUI;
using System;
using System.Buffers.Binary;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using PhotoViewer.Core.AI;
using PhotoViewer.Core.Database;
using PhotoViewer.Core.Image;

namespace PhotoViewer.ViewModels.Tools;

/// <summary>
/// DINO 诊断工具页 VM。一次性对当前图片并行跑 CV 网格（v4 中心采样 + Marziliano 绝对边宽 + 32 网格
/// + 对数锐度 + 对角线归一化抖动量级 + 加权刚体拟合）与 DINO 双输出推理，
/// 暴露缩略图 + 锐度热力图 + 拖影矢量场 + 加权刚体文本 + PCA-RGB + cosine 图。
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
    /// <summary>抖动矢量场（32×32 拖影线方向 / 绝对边宽 / 掩膜 + 图像对角线 D）；View 消费它绘制 Canvas 线段。</summary>
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
            // 先用指纹试读 patch token / cv_grid 缓存。
            // v5 起 contrast 已是 result 第 7 标量,UI 看到的派生层(锐度/抖动图)与 indexer 写库时算出的完全等价 —
            // 改算法只需 bump CvGridResult.CurrentVersion,阈值常量从不入库。
            var (cachedPatches, cachedCv) = await TryReadCachedAsync(file, ct).ConfigureAwait(false);

            // DINO 缩略图始终需要解码用于显示 SourceThumbnail。
            // CV 大图:无论缓存命中与否都要解码 — 命中时跳过 100-300ms 的 CV 计算但仍需图像尺寸算 diagonal,
            // 且 BitmapLoader.GetBitmapAsync 共享 LRU,这张图通常已在主视图里解码过,实际开销很低。
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

            // CV 源图像:大图优先,失败降级到 DINO 缩略图。所有 diagonal 都从这里推。
            var cvSourceBitmap = cvBitmap ?? dinoBitmap;
            int cvW = cvSourceBitmap.PixelSize.Width;
            int cvH = cvSourceBitmap.PixelSize.Height;

            // CV 7 标量:命中缓存直接用,否则现算。
            CvGridResult cv = cachedCv ?? await CvGridExtractor.ExtractAsync(cvSourceBitmap, ct).ConfigureAwait(false);

            // DINO patch:命中缓存直接用,否则现算 ExtractDualAsync。
            float[]? patches = cachedPatches;
            if (patches == null)
            {
                var (_, p) = await DinoFeatureExtractor.ExtractDualAsync(dinoBitmap, includePatches: true, ct).ConfigureAwait(false);
                patches = p;
            }

            ct.ThrowIfCancellationRequested();

            // CV:锐度图 + 拖影矢量场 + 加权刚体拟合(全部从 cv 7 标量现算)
            float diagonal = MathF.Sqrt((float)cvW * cvW + (float)cvH * cvH);
            var sharpness = CvHeatmap.BuildSharpness(cv);
            var sharpnessBmp = HeatmapBitmapBuilder.BuildViridis(sharpness, CvGridPixels, CvGridPixels);
            var shakeField = CvHeatmap.BuildShakeField(cv, diagonal);
            var rigid = CvHeatmap.FitRigidMotion(shakeField);
            var rigidText = FormatRigidMotion(rigid, diagonal);

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
    /// 读库快路径:用 EXIF 算指纹 → 同时尝试读 DINO patch token 与 CV grid 缓存。
    /// 任一项失败返回该项 null,调用方走对应的现算路径。诊断页不回写库 — 入库由 <see cref="FolderFeatureIndexer"/> 主路径独占。
    /// </summary>
    private static async Task<(float[]? Patches, CvGridResult? Cv)> TryReadCachedAsync(ImageFile file, CancellationToken ct)
    {
        try
        {
            ct.ThrowIfCancellationRequested();
            var exif = await file.LoadExifDataAsync().ConfigureAwait(false);
            if (!file.ModifiedDate.HasValue)
                await file.LoadBasicPropertiesAsync().ConfigureAwait(false);
            var input = PhotoFingerprint.BuildInput(file.Name, exif, file.ModifiedDate?.UtcDateTime);
            if (!input.CaptureTime.HasValue) return (null, null);

            var fingerprint = PhotoFingerprint.Compute(input);
            ct.ThrowIfCancellationRequested();

            var patchTask = PhotoDatabase.ReadPatchesAsync(fingerprint, DinoModelResources.ModelId);
            var cvTask = PhotoDatabase.ReadCvGridAsync(fingerprint);
            await Task.WhenAll(patchTask, cvTask).ConfigureAwait(false);

            float[]? patches = null;
            if (patchTask.Result is byte[] pBlob)
            {
                int total = DinoModelResources.PatchTokenCount * DinoModelResources.FeatureDim;
                if (pBlob.Length == total * sizeof(float))
                {
                    patches = new float[total];
                    for (int i = 0; i < total; i++)
                        patches[i] = BinaryPrimitives.ReadSingleLittleEndian(pBlob.AsSpan(i * sizeof(float), sizeof(float)));
                }
            }

            CvGridResult? cv = null;
            if (cvTask.Result is { Blob: var cvBlob, Spec: var cvSpec } && cvSpec == CvGridResult.CurrentVersion)
            {
                cv = CvGridResult.Decode(cvBlob, cvSpec);
            }

            return (patches, cv);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[DinoDebug] cache read failed for {file.Name}: {ex.Message}");
            return (null, null);
        }
    }

    /// <summary>
    /// 把 v5 加权刚体拟合结果格式化为多行文本(研发判断用)。判定逻辑已抽到 <see cref="ShakeClassifier"/>,
    /// 缩略图卡片的"抖"徽标走同一份判定,改阈值两端同步。
    /// </summary>
    private static string FormatRigidMotion(RigidMotionResult rigid, float diagonal)
    {
        float translateR = diagonal > 0 ? rigid.TranslationMagnitude / diagonal : 0f;
        var verdict = ShakeClassifier.Classify(rigid, diagonal);
        string label = ShakeClassifier.FormatLabel(verdict);

        return $"样本 {rigid.SampleCount} 格 ({rigid.MaskRatio * 100f:F1}%)\n" +
               $"Σw   = {rigid.WeightSum:F1}\n" +
               $"|T|  = {rigid.TranslationMagnitude:F2} px  ({translateR * 100f:F3}% D)\n" +
               $"|ω|  = {rigid.RotationMagnitude:F3} rad\n" +
               $"残差 = {rigid.ResidualRms:F2} px\n" +
               $"R_global = {rigid.DirectionalConsistency:F2}  R_local p10 = {rigid.RLocalP10:F2}\n" +
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
