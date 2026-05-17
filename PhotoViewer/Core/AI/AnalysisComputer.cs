using System;

namespace PhotoViewer.Core.AI;

/// <summary>
/// 派生层现算器:把 <see cref="AnalysisDataReader.Result"/> 转成 <see cref="AnalysisResultCache.Entry"/> —
/// 执行 PCA-RGB / 默认中心 cosine / 锐度图 / 抖动矢量场 / 抖动判定文字。线程安全(纯函数 + 内部不持有状态)。
///
/// 抽出来给两条调用路径共享:
/// 1. <see cref="ViewModels.Main.AnalysisViewModel"/> 切图 cache miss 后落 cache。
/// 2. <see cref="PhotoViewer.Core.Image.BitmapPrefetcher"/> 后台预热邻居指纹。
///
/// PCA SVD 是切图卡顿的主因(几十 ms),全部塞进 cache 后切图变成纯 UI 线程 swap。
/// </summary>
public static class AnalysisComputer
{
    /// <summary>DINO patch 网格边长(32),与 <see cref="PatchHeatmap.Grid"/> 一致。</summary>
    public const int PatchGridPixels = PatchHeatmap.Grid;

    /// <summary>
    /// 把读库结果转成可缓存的派生数据。包含 4 张诊断位图 + ShakeField + 判定文字 + patch tokens。
    /// 缺数据的部分对应字段留 null,UI 端以"未提取"占位。
    /// </summary>
    /// <param name="data">读库返回的原始 patch / cv 数据。</param>
    /// <returns>派生数据快照;位图所有权随返回项移交给调用方(通常立刻塞进 cache)。</returns>
    public static AnalysisResultCache.Entry Compute(AnalysisDataReader.Result data)
    {
        ArgumentNullException.ThrowIfNull(data);

        // CV 派生层(锐度图 / ShakeField / 刚体拟合 + 判定文字)
        Avalonia.Media.Imaging.Bitmap? sharpnessBmp = null;
        ShakeField? shakeField = null;
        string shakeLabel = "抖动拖影";
        double aspect = 1.0;
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

        // DINO 派生层(PCA-RGB + 默认中心 cosine 参考点 16,16)
        Avalonia.Media.Imaging.Bitmap? pcaBmp = null;
        Avalonia.Media.Imaging.Bitmap? cosineBmp = null;
        if (data.Patches != null)
        {
            var pcaRgb = PatchHeatmap.ComputePcaRgb(data.Patches);
            pcaBmp = HeatmapBitmapBuilder.BuildRgb(pcaRgb, PatchGridPixels, PatchGridPixels);

            int rx = PatchGridPixels / 2;
            int ry = PatchGridPixels / 2;
            var cos = PatchHeatmap.ComputeRefCosine(data.Patches, rx, ry);
            cosineBmp = HeatmapBitmapBuilder.BuildViridis(cos, PatchGridPixels, PatchGridPixels);
        }

        return new AnalysisResultCache.Entry
        {
            AspectRatio = aspect > 0 ? aspect : 1.0,
            PcaBmp = pcaBmp,
            CenterCosineBmp = cosineBmp,
            SharpnessBmp = sharpnessBmp,
            ShakeField = shakeField,
            ShakeLabel = shakeLabel,
            Patches = data.Patches,
        };
    }
}
