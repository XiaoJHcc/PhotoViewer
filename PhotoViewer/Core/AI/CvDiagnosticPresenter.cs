using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using PhotoViewer.Core.Image;
using PhotoViewer.Core.Platform;

namespace PhotoViewer.Core.AI;

/// <summary>标准版 CV 诊断页：只读既有 CV 或对当前照片显式现算，不写数据库。</summary>
public sealed class CvDiagnosticPresenter : IDisposable
{
    private const int ThumbnailShortSide = 560;
    public const int CvGridPixels = CvGridResult.GridSize;

    private readonly List<AnalysisTile> _tiles;
    private readonly AnalysisTile _thumbnailTile;
    private readonly AnalysisTile _sharpnessTile;
    private readonly AnalysisTile _shakeTile;
    private CancellationTokenSource? _cts;
    private DecodedImage? _thumbnail;
    private DecodedImage? _sharpness;
    private bool _disposed;

    public CvDiagnosticPresenter()
    {
        _thumbnailTile = new AnalysisTile(AnalysisTileKind.Diagnostic, "原图缩略图") { Caption = "原图缩略图", Placeholder = null };
        _sharpnessTile = new AnalysisTile(AnalysisTileKind.Diagnostic, "锐度") { Caption = "锐度 (edge_width_p20, 1.5-10 px)", Placeholder = null };
        _shakeTile = new AnalysisTile(AnalysisTileKind.Diagnostic, "抖动拖影") { Caption = "抖动拖影矢量 (drag_width / direction)", Placeholder = null };
        _tiles = [_thumbnailTile, _sharpnessTile, _shakeTile];
    }

    public IReadOnlyList<AnalysisTile> Tiles => _tiles;
    public AnalysisTile ThumbnailTile => _thumbnailTile;
    public AnalysisTile SharpnessTile => _sharpnessTile;
    public AnalysisTile ShakeTile => _shakeTile;
    public string Status { get; private set; } = "未加载";
    public bool IsBusy { get; private set; }
    public double AspectRatio { get; private set; } = 1.0;
    public double? CrosshairU { get; private set; }
    public double? CrosshairV { get; private set; }
    public string RigidMotionText { get; private set; } = "未加载";
    public event Action? Changed;
    public event Action? CrosshairChanged;

    public void SetSource(ImageFile? file)
    {
        _cts?.Cancel();
        var cts = new CancellationTokenSource();
        _cts = cts;
        _ = LoadAsync(file, cts.Token);
    }

    public void Cancel()
    {
        var cts = _cts;
        _cts = null;
        cts?.Cancel();
        cts?.Dispose();
        if (UiDispatcher.CheckAccess()) ClearAll("未加载");
        else UiDispatcher.Post(() => ClearAll("未加载"));
    }

    public void OnTileClicked(double u, double v)
    {
        CrosshairU = Math.Clamp(u, 0, 1);
        CrosshairV = Math.Clamp(v, 0, 1);
        CrosshairChanged?.Invoke();
    }

    public void ClearCrosshair()
    {
        if (CrosshairU == null && CrosshairV == null) return;
        CrosshairU = null;
        CrosshairV = null;
        CrosshairChanged?.Invoke();
    }

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

        DecodedImage? thumbnail = null;
        DecodedImage? cvBitmap = null;
        DecodedImage? sharpness = null;
        bool handedOff = false;
        try
        {
            var cached = await AnalysisDataReader.TryReadAsync(file, ct).ConfigureAwait(false);
            var thumbnailTask = ThumbnailService.GetThumbnailAsync(file.File, ThumbnailShortSide);
            var cvTask = BitmapLoader.GetBitmapAsync(file.File);
            await Task.WhenAll(thumbnailTask, cvTask).ConfigureAwait(false);
            thumbnail = thumbnailTask.Result;
            cvBitmap = cvTask.Result;
            if (thumbnail == null)
            {
                Post(() => ClearAll("无法解码图片"), ct);
                return;
            }

            ct.ThrowIfCancellationRequested();
            var cvSource = cvBitmap ?? thumbnail;
            var cv = cached.Cv ?? await CvGridExtractor.ExtractAsync(cvSource, ct).ConfigureAwait(false);
            int cvW = cached.Cv != null && cached.CvImageWidth > 0 ? cached.CvImageWidth : cvSource.Width;
            int cvH = cached.Cv != null && cached.CvImageHeight > 0 ? cached.CvImageHeight : cvSource.Height;
            float diagonal = MathF.Sqrt((float)cvW * cvW + (float)cvH * cvH);
            sharpness = HeatmapBitmapBuilder.BuildViridis(CvHeatmap.BuildSharpness(cv), CvGridPixels, CvGridPixels);
            var shakeField = CvHeatmap.BuildShakeField(cv, diagonal);
            var rigidText = FormatRigidMotion(CvHeatmap.FitRigidMotion(shakeField), diagonal);
            double ratio = thumbnail.Height > 0 ? (double)thumbnail.Width / thumbnail.Height : 1.0;

            var ownedThumbnail = thumbnail;
            handedOff = true;
            Post(() =>
            {
                if (ct.IsCancellationRequested)
                {
                    ownedThumbnail.Dispose();
                    sharpness?.Dispose();
                    return;
                }
                SwapThumbnail(ownedThumbnail);
                AspectRatio = ratio > 0 ? ratio : 1.0;
                CrosshairU = null;
                CrosshairV = null;
                SwapSharpness(sharpness);
                _shakeTile.ShakeField = shakeField;
                RigidMotionText = rigidText;
                Status = file.Name;
                IsBusy = false;
                Changed?.Invoke();
                CrosshairChanged?.Invoke();
            }, CancellationToken.None);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Post(() => ClearAll($"失败: {ex.Message}"), ct);
        }
        finally
        {
            if (!handedOff) thumbnail?.Dispose();
            cvBitmap?.Release();
        }
    }

    private void SwapThumbnail(DecodedImage next)
    {
        var old = _thumbnail;
        _thumbnail = next;
        _thumbnailTile.Source = next;
        _shakeTile.Background = next;
        if (old != null) UiDispatcher.Post(old.Dispose);
    }

    private void SwapSharpness(DecodedImage? next)
    {
        var old = _sharpness;
        _sharpness = next;
        _sharpnessTile.Source = next;
        _sharpnessTile.Placeholder = next == null ? "未提取" : null;
        if (old != null) UiDispatcher.Post(old.Dispose);
    }

    private void ClearAll(string status)
    {
        var oldThumbnail = _thumbnail;
        _thumbnail = null;
        _thumbnailTile.Source = null;
        _shakeTile.Background = null;
        oldThumbnail?.Dispose();
        SwapSharpness(null);
        _shakeTile.ShakeField = null;
        RigidMotionText = "未加载";
        CrosshairU = null;
        CrosshairV = null;
        Status = status;
        IsBusy = false;
        Changed?.Invoke();
        CrosshairChanged?.Invoke();
    }

    private static void Post(Action action, CancellationToken ct) => UiDispatcher.Post(() =>
    {
        if (ct.IsCancellationRequested) return;
        action();
    });

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

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
        ClearAll("未加载");
    }
}
