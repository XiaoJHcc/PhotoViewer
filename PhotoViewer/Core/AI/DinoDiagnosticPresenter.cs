using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using PhotoViewer.Core.Database;
using PhotoViewer.Core.Image;
using PhotoViewer.Core.Platform;

namespace PhotoViewer.Core.AI;

/// <summary>
/// 平台中立的 DINO 诊断页状态机(工具窗口的"DINO 诊断"页)。一次性对当前图片并行跑 CV 网格与 DINO 双输出推理,
/// 暴露缩略图、锐度热力图、拖影矢量场、加权刚体文本、PCA-RGB 与点击参考点 cosine 图。
/// CV 走 <see cref="BitmapLoader"/> 原始分辨率解码(与主图共用 LRU),DINO 仍走 560 短边缩略图。
/// 优先读库快路径(同时取 patch token 与 cv_grid 7 标量),命中即跳过推理与 CV 计算;诊断页不回写库。
/// 旧壳 <c>DinoDebugViewModel</c> 与 macOS 原生工具窗口共用本类,两者只做位图格式适配。
/// </summary>
public sealed class DinoDiagnosticPresenter : IDisposable
{
    /// <summary>DINO 推理输入短边(与 <see cref="FolderFeatureIndexer"/> 一致)。</summary>
    private const int DinoShortSide = 560;

    /// <summary>CV 网格可视化像素边长(16),展示层放大显示。</summary>
    public const int CvGridPixels = CvGridResult.GridSize;

    /// <summary>DINO patch 图像素边长(32)。</summary>
    public const int PatchGridPixels = PatchHeatmap.Grid;

    private readonly List<AnalysisTile> _tiles;
    private readonly AnalysisTile _thumbnailTile;
    private readonly AnalysisTile _pcaTile;
    private readonly AnalysisTile _cosineTile;
    private readonly AnalysisTile _sharpnessTile;
    private readonly AnalysisTile _shakeTile;

    private CancellationTokenSource? _cts;
    private float[]? _patchTokens;
    private DecodedImage? _thumbnail;     // presenter 所有(抖动瓦片背景复用同一实例)
    private DecodedImage? _sharpness;
    private DecodedImage? _pca;
    private DecodedImage? _cosine;
    private bool _disposed;

    /// <summary>瓦片列表:原图缩略图 / PCA-RGB / Cosine 相似度 / 锐度 / 抖动拖影矢量。</summary>
    public IReadOnlyList<AnalysisTile> Tiles => _tiles;

    /// <summary>原图缩略图瓦片(DINO 一致的 560 短边输入)。</summary>
    public AnalysisTile ThumbnailTile => _thumbnailTile;

    /// <summary>PCA-RGB 瓦片。</summary>
    public AnalysisTile PcaTile => _pcaTile;

    /// <summary>Cosine 相似度瓦片(说明文字含当前参考点坐标)。</summary>
    public AnalysisTile CosineTile => _cosineTile;

    /// <summary>锐度热力图瓦片。</summary>
    public AnalysisTile SharpnessTile => _sharpnessTile;

    /// <summary>抖动拖影矢量瓦片(压暗原图 + <see cref="AnalysisTile.ShakeField"/> 矢量场)。</summary>
    public AnalysisTile ShakeTile => _shakeTile;

    /// <summary>状态文本。</summary>
    public string Status { get; private set; } = "未加载";

    /// <summary>是否正在后台计算。</summary>
    public bool IsBusy { get; private set; }

    /// <summary>当前图片长宽比(宽/高)。</summary>
    public double AspectRatio { get; private set; } = 1.0;

    /// <summary>全局十字准星位置(归一化 [0,1];null 隐藏)。</summary>
    public double? CrosshairU { get; private set; }

    /// <summary>全局十字准星纵坐标(归一化 [0,1])。</summary>
    public double? CrosshairV { get; private set; }

    /// <summary>cosine 参考 patch 的 x(0..31)。</summary>
    public int RefGridX { get; private set; } = PatchGridPixels / 2;

    /// <summary>cosine 参考 patch 的 y(0..31)。</summary>
    public int RefGridY { get; private set; } = PatchGridPixels / 2;

    /// <summary>刚体拟合的多行文本结果。</summary>
    public string RigidMotionText { get; private set; } = "未加载";

    /// <summary>任一瓦片或文本变化后触发。</summary>
    public event Action? Changed;

    /// <summary>准星变化后触发。</summary>
    public event Action? CrosshairChanged;

    /// <summary>构造瓦片列表。</summary>
    public DinoDiagnosticPresenter()
    {
        _thumbnailTile = new AnalysisTile(AnalysisTileKind.Diagnostic, "原图缩略图") { Caption = "原图缩略图", Placeholder = null };
        _pcaTile = new AnalysisTile(AnalysisTileKind.Diagnostic, "PCA-RGB") { Caption = "PCA-RGB (前 3 主成分)", Placeholder = null };
        _cosineTile = new AnalysisTile(AnalysisTileKind.Diagnostic, "Cosine") { Caption = FormatCosineCaption(RefGridX, RefGridY), Placeholder = null };
        _sharpnessTile = new AnalysisTile(AnalysisTileKind.Diagnostic, "锐度") { Caption = "锐度 (edge_width_p20, 1.5-10 px)", Placeholder = null };
        _shakeTile = new AnalysisTile(AnalysisTileKind.Diagnostic, "抖动拖影") { Caption = "抖动拖影矢量 (drag_width / direction)", Placeholder = null };
        _tiles = [_thumbnailTile, _pcaTile, _cosineTile, _sharpnessTile, _shakeTile];
    }

    /// <summary>切换源图片:取消旧任务 → 并行解码 DINO/CV 两路 → 跑推理与提取 → 渲染。</summary>
    /// <param name="file">目标照片;null 时清空。</param>
    public void SetSource(ImageFile? file)
    {
        _cts?.Cancel();
        var cts = new CancellationTokenSource();
        _cts = cts;
        _ = LoadAsync(file, cts.Token);
    }

    /// <summary>
    /// 取消在途推理/诊断任务并回到「未加载」:用于离开诊断页或关闭工具窗口(TOOLS-06)。
    /// 过期结果由取消令牌丢弃,不会把上一张照片的结果挂在新文件名下。
    /// </summary>
    public void Cancel()
    {
        var cts = _cts;
        _cts = null;
        cts?.Cancel();
        cts?.Dispose();

        if (UiDispatcher.CheckAccess())
            ClearAll("未加载");
        else
            UiDispatcher.Post(() => ClearAll("未加载"));
    }

    /// <summary>点击任意瓦片:落十字准星 + 以点击位置作 cosine 参考点(映射到 32×32)。</summary>
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
        var old = _cosine;
        _cosine = HeatmapBitmapBuilder.BuildViridis(cos, PatchGridPixels, PatchGridPixels);
        _cosineTile.Source = _cosine;
        _cosineTile.Caption = FormatCosineCaption(gx, gy);
        old?.Dispose();
        Changed?.Invoke();
    }

    /// <summary>空闲区域点击:清空十字准星。</summary>
    public void ClearCrosshair()
    {
        if (CrosshairU == null && CrosshairV == null) return;
        CrosshairU = null;
        CrosshairV = null;
        CrosshairChanged?.Invoke();
    }

    /// <summary>后台加载:读库快路径 → 解码两路 → CV/DINO 计算 → UI 贴图。</summary>
    private async Task LoadAsync(ImageFile? file, CancellationToken ct)
    {
        if (file == null)
        {
            Post(() => ClearAll("未加载"), ct);
            return;
        }

        UiDispatcher.Post(() =>
        {
            if (ct.IsCancellationRequested) return;
            IsBusy = true;
            Status = $"计算中…  {file.Name}";
            Changed?.Invoke();
        });

        DecodedImage? dinoBitmap = null;
        DecodedImage? cvBitmap = null;
        DecodedImage? sharpness = null;
        DecodedImage? pca = null;
        DecodedImage? cosine = null;
        bool handedOff = false;
        try
        {
            // 先用指纹试读 patch token / cv_grid 缓存。
            var (cachedPatches, cachedCv) = await TryReadCachedAsync(file, ct).ConfigureAwait(false);

            // DINO 缩略图始终需要解码用于显示;CV 大图命中缓存时仍要解码以取得图像尺寸(算 diagonal)。
            var dinoTaskDecode = ThumbnailService.GetThumbnailAsync(file.File, DinoShortSide);
            var cvTaskDecode = BitmapLoader.GetBitmapAsync(file.File);
            await Task.WhenAll(dinoTaskDecode, cvTaskDecode).ConfigureAwait(false);
            dinoBitmap = dinoTaskDecode.Result;
            cvBitmap = cvTaskDecode.Result;

            if (dinoBitmap == null)
            {
                Post(() => ClearAll("无法解码图片"), ct);
                return;
            }

            ct.ThrowIfCancellationRequested();

            int bw = dinoBitmap.Width;
            int bh = dinoBitmap.Height;
            double ratio = bh > 0 ? (double)bw / bh : 1.0;

            // CV 源图像:大图优先,失败降级到 DINO 缩略图;所有 diagonal 都从这里推。
            var cvSource = cvBitmap ?? dinoBitmap;
            int cvW = cvSource.Width;
            int cvH = cvSource.Height;

            var cv = cachedCv ?? await CvGridExtractor.ExtractAsync(cvSource, ct).ConfigureAwait(false);

            float[]? patches = cachedPatches;
            if (patches == null)
            {
                var (_, extracted) = await DinoFeatureExtractor.ExtractDualAsync(dinoBitmap, includePatches: true, ct).ConfigureAwait(false);
                patches = extracted;
            }

            ct.ThrowIfCancellationRequested();

            float diagonal = MathF.Sqrt((float)cvW * cvW + (float)cvH * cvH);
            sharpness = HeatmapBitmapBuilder.BuildViridis(CvHeatmap.BuildSharpness(cv), CvGridPixels, CvGridPixels);
            var shakeField = CvHeatmap.BuildShakeField(cv, diagonal);
            var rigidText = FormatRigidMotion(CvHeatmap.FitRigidMotion(shakeField), diagonal);

            int rx = PatchGridPixels / 2;
            int ry = PatchGridPixels / 2;
            if (patches != null)
            {
                pca = HeatmapBitmapBuilder.BuildRgb(PatchHeatmap.ComputePcaRgb(patches), PatchGridPixels, PatchGridPixels);
                cosine = HeatmapBitmapBuilder.BuildViridis(PatchHeatmap.ComputeRefCosine(patches, rx, ry), PatchGridPixels, PatchGridPixels);
            }

            var thumbnail = dinoBitmap;
            _patchTokens = patches;
            handedOff = true;

            Post(() =>
            {
                if (ct.IsCancellationRequested)
                {
                    // 结果作废:本 presenter 尚未接管这些位图,直接释放
                    thumbnail.Dispose();
                    sharpness?.Dispose();
                    pca?.Dispose();
                    cosine?.Dispose();
                    return;
                }
                SwapThumbnail(thumbnail);
                AspectRatio = ratio > 0 ? ratio : 1.0;
                CrosshairU = null;
                CrosshairV = null;
                RefGridX = rx;
                RefGridY = ry;
                SwapSharpness(sharpness);
                SwapPca(pca);
                SwapCosine(cosine);
                _shakeTile.ShakeField = shakeField;
                _cosineTile.Caption = FormatCosineCaption(rx, ry);
                RigidMotionText = rigidText;
                Status = file.Name;
                IsBusy = false;
                Changed?.Invoke();
                CrosshairChanged?.Invoke();
            }, CancellationToken.None);
        }
        catch (OperationCanceledException)
        {
            // 被新任务覆盖,保持原样
        }
        catch (Exception ex)
        {
            Post(() => ClearAll($"失败: {ex.Message}"), ct);
        }
        finally
        {
            if (!handedOff) dinoBitmap?.Dispose();
            // CV 大图经 GetBitmapAsync 取得调用方租约,用完归还(缓存仍持有自己那份)
            cvBitmap?.Release();
        }
    }

    /// <summary>UI 线程贴图:交换缩略图(本 presenter 所有),旧图延后一拍释放。</summary>
    private void SwapThumbnail(DecodedImage next)
    {
        var old = _thumbnail;
        _thumbnail = next;
        _thumbnailTile.Source = next;
        _shakeTile.Background = next;
        if (old != null) UiDispatcher.Post(old.Dispose);
    }

    /// <summary>UI 线程贴图:交换锐度图。</summary>
    private void SwapSharpness(DecodedImage? next)
    {
        var old = _sharpness;
        _sharpness = next;
        _sharpnessTile.Source = next;
        _sharpnessTile.Placeholder = next == null ? "未提取" : null;
        if (old != null) UiDispatcher.Post(old.Dispose);
    }

    /// <summary>UI 线程贴图:交换 PCA-RGB 图。</summary>
    private void SwapPca(DecodedImage? next)
    {
        var old = _pca;
        _pca = next;
        _pcaTile.Source = next;
        _pcaTile.Placeholder = next == null ? "未提取" : null;
        if (old != null) UiDispatcher.Post(old.Dispose);
    }

    /// <summary>UI 线程贴图:交换 cosine 图。</summary>
    private void SwapCosine(DecodedImage? next)
    {
        var old = _cosine;
        _cosine = next;
        _cosineTile.Source = next;
        _cosineTile.Placeholder = next == null ? "未提取" : null;
        if (old != null) UiDispatcher.Post(old.Dispose);
    }

    /// <summary>UI 线程清空全部瓦片与文本;presenter 拥有的位图显式归还。</summary>
    private void ClearAll(string status)
    {
        SwapThumbnailClear();
        SwapSharpness(null);
        SwapPca(null);
        SwapCosine(null);
        _shakeTile.ShakeField = null;
        RigidMotionText = "未加载";
        _patchTokens = null;
        CrosshairU = null;
        CrosshairV = null;
        Status = status;
        IsBusy = false;
        Changed?.Invoke();
        CrosshairChanged?.Invoke();
    }

    /// <summary>清空缩略图与其背景引用。</summary>
    private void SwapThumbnailClear()
    {
        var old = _thumbnail;
        _thumbnail = null;
        _thumbnailTile.Source = null;
        _shakeTile.Background = null;
        old?.Dispose();
    }

    /// <summary>UI 线程投递,过期 token 丢弃。</summary>
    private void Post(Action action, CancellationToken ct) => UiDispatcher.Post(() =>
    {
        if (ct.IsCancellationRequested) return;
        action();
    });

    /// <summary>
    /// 读库快路径:用 EXIF 算指纹 → 同时尝试读 DINO patch token 与 CV grid 缓存。
    /// 任一项失败返回该项 null,调用方走对应的现算路径;诊断页不回写库。
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
            if (patchTask.Result is byte[] patchBlob)
            {
                int total = DinoModelResources.PatchTokenCount * DinoModelResources.FeatureDim;
                if (patchBlob.Length == total * sizeof(float))
                {
                    patches = new float[total];
                    for (int i = 0; i < total; i++)
                        patches[i] = BinaryPrimitives.ReadSingleLittleEndian(patchBlob.AsSpan(i * sizeof(float), sizeof(float)));
                }
            }

            CvGridResult? cv = null;
            if (cvTask.Result is { Blob: var cvBlob, Spec: var cvSpec } && cvSpec == CvGridResult.CurrentVersion)
                cv = CvGridResult.Decode(cvBlob, cvSpec);

            return (patches, cv);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[DinoDiagnostic] cache read failed for {file.Name}: {ex.Message}");
            return (null, null);
        }
    }

    /// <summary>
    /// 把 v5 加权刚体拟合结果格式化为多行文本(研发判断用)。判定逻辑走 <see cref="ShakeClassifier"/>,
    /// 与缩略图卡片的"抖"徽标同一份判定。
    /// </summary>
    private static string FormatRigidMotion(RigidMotionResult rigid, float diagonal)
    {
        float translateR = diagonal > 0 ? rigid.TranslationMagnitude / diagonal : 0f;
        string label = ShakeClassifier.FormatLabel(ShakeClassifier.Classify(rigid, diagonal));

        return $"样本 {rigid.SampleCount} 格 ({rigid.MaskRatio * 100f:F1}%)\n" +
               $"Σw   = {rigid.WeightSum:F1}\n" +
               $"|T|  = {rigid.TranslationMagnitude:F2} px  ({translateR * 100f:F3}% D)\n" +
               $"|ω|  = {rigid.RotationMagnitude:F3} rad\n" +
               $"残差 = {rigid.ResidualRms:F2} px\n" +
               $"R_global = {rigid.DirectionalConsistency:F2}  R_local p10 = {rigid.RLocalP10:F2}\n" +
               $"判定:{label}";
    }

    /// <summary>cosine 瓦片说明文字(含当前参考点)。</summary>
    private static string FormatCosineCaption(int x, int y) => $"Cosine 相似度  参考点: {x}, {y}";

    /// <summary>取消在途任务并释放本 presenter 持有的全部位图。</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
        SwapThumbnailClear();
        _sharpness?.Dispose();
        _sharpness = null;
        _pca?.Dispose();
        _pca = null;
        _cosine?.Dispose();
        _cosine = null;
    }
}
