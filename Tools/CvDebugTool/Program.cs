using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using LibHeifSharp;
using PhotoViewer.Core.AI;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Drawing.Processing;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace CvDebugTool;

/// <summary>
/// CV 网格 v5 离线诊断工具。读入 HIF/HEIC/JPG，调
/// CvGridExtractor.ExtractFromLuma + CvHeatmap（v5 R_local + R_global），
/// 输出锐度图、抖动矢量场 PNG 与文本报告（含 R_global / R_local 分布 + 判定标签）。
/// </summary>
internal static class Program
{
    private static int Main(string[] args)
    {
        if (args.Length == 0)
        {
            Console.WriteLine("usage: dotnet run -- <file1> [file2] [...]");
            Console.WriteLine("outputs to <input_dir>/outputs/:  <name>_sharpness.png  <name>_shake.png  <name>_report.txt");
            return 1;
        }

        foreach (var path in args)
        {
            try
            {
                ProcessOne(path);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[FAIL] {path}: {ex.Message}");
                Console.WriteLine(ex.StackTrace);
            }
        }
        return 0;
    }

    private static void ProcessOne(string path)
    {
        if (!File.Exists(path)) { Console.WriteLine($"not found: {path}"); return; }

        var name = Path.GetFileNameWithoutExtension(path);
        var inputDir = Path.GetDirectoryName(Path.GetFullPath(path)) ?? ".";
        // 输出统一去 <input_dir>/outputs/，避免样本目录被 PNG/TXT 污染。
        var outDir = Path.Combine(inputDir, "outputs");
        Directory.CreateDirectory(outDir);
        Console.WriteLine($"\n=== {name} ===");

        var ext = Path.GetExtension(path).ToLowerInvariant();
        (byte[] rgb, int w, int h) decoded = ext is ".heif" or ".heic" or ".hif" or ".avif"
            ? DecodeHeif(path)
            : DecodeRaster(path);

        Console.WriteLine($"image: {decoded.w} × {decoded.h}");

        // RGB → luma (Rec.709)
        var luma = new float[decoded.w * decoded.h];
        for (int i = 0; i < luma.Length; i++)
        {
            byte r = decoded.rgb[i * 3 + 0];
            byte g = decoded.rgb[i * 3 + 1];
            byte b = decoded.rgb[i * 3 + 2];
            luma[i] = 0.2126f * r + 0.7152f * g + 0.0722f * b;
        }

        var cv = CvGridExtractor.ExtractFromLuma(luma, decoded.w, decoded.h);
        float diagonal = MathF.Sqrt((float)decoded.w * decoded.w + (float)decoded.h * decoded.h);
        var sharpness = CvHeatmap.BuildSharpness(cv);
        var shake = CvHeatmap.BuildShakeField(cv, diagonal);
        var rigid = CvHeatmap.FitRigidMotion(shake);

        // 文本报告
        var report = BuildReport(name, decoded.w, decoded.h, diagonal, cv, shake, rigid);
        var reportPath = Path.Combine(outDir, $"{name}_report.txt");
        File.WriteAllText(reportPath, report);
        Console.Write(report);
        Console.WriteLine($"→ {reportPath}");

        // 锐度 PNG
        var sharpPath = Path.Combine(outDir, $"{name}_sharpness.png");
        SaveSharpnessPng(sharpness, sharpPath);
        Console.WriteLine($"→ {sharpPath}");

        // 抖动矢量场 PNG（带原图缩略叠底）
        var shakePath = Path.Combine(outDir, $"{name}_shake.png");
        SaveShakePng(shake, decoded.rgb, decoded.w, decoded.h, shakePath);
        Console.WriteLine($"→ {shakePath}");
    }

    private static (byte[] rgb, int w, int h) DecodeHeif(string path)
    {
        var data = File.ReadAllBytes(path);
        using var ctx = new HeifContext(data);
        using var handle = ctx.GetPrimaryImageHandle();
        if (handle == null) throw new Exception("HEIF: no primary handle");
        using var image = handle.Decode(HeifColorspace.Rgb, HeifChroma.InterleavedRgb24);
        if (image == null) throw new Exception("HEIF: decode null");

        int reportedW = (int)image.Width;
        int reportedH = (int)image.Height;
        var plane = image.GetPlane(HeifChannel.Interleaved);
        int stride = (int)plane.Stride;
        int pixelsPerRow = stride / 3;
        // 与桌面端解码器一致的"竖拍 stride 检测"逻辑。
        bool swap = pixelsPerRow < reportedW;
        int w = swap ? reportedH : reportedW;
        int h = swap ? reportedW : reportedH;

        var rgb = new byte[w * h * 3];
        unsafe
        {
            var src = (byte*)plane.Scan0;
            for (int y = 0; y < h; y++)
            {
                var srcRow = src + y * stride;
                int dstRow = y * w * 3;
                for (int x = 0; x < w * 3; x++) rgb[dstRow + x] = srcRow[x];
            }
        }
        return (rgb, w, h);
    }

    private static (byte[] rgb, int w, int h) DecodeRaster(string path)
    {
        using var img = SixLabors.ImageSharp.Image.Load<Rgb24>(path);
        var rgb = new byte[img.Width * img.Height * 3];
        img.CopyPixelDataTo(rgb);
        return (rgb, img.Width, img.Height);
    }

    private static string BuildReport(string name, int w, int h, float diagonal,
        CvGridResult cv, ShakeField shake, RigidMotionResult rigid)
    {
        int n = CvGridResult.PlaneLength;

        // 全图统计
        int validCells = 0;
        int anisotropicCells = 0;
        var dragWidthsPx = new List<float>();
        var anisotropies = new List<float>();
        var localR = new List<float>();
        var contrasts = new List<float>();
        for (int i = 0; i < n; i++)
        {
            float wpx = cv.Data[3 * n + i];
            float a = cv.Data[5 * n + i];
            if (!float.IsNaN(a))
            {
                anisotropies.Add(a);
                if (a >= CvHeatmap.AnisotropyMin) anisotropicCells++;
            }
            if (shake.Mask[i])
            {
                validCells++;
                dragWidthsPx.Add(wpx);
            }
            float r = shake.LocalConsistency[i];
            if (!float.IsNaN(r)) localR.Add(r);
            float c = shake.Contrast.Length > i ? shake.Contrast[i] : float.NaN;
            if (!float.IsNaN(c)) contrasts.Add(c);
        }
        dragWidthsPx.Sort();
        float medianDrag = dragWidthsPx.Count > 0 ? dragWidthsPx[dragWidthsPx.Count / 2] : float.NaN;
        float p10Drag = dragWidthsPx.Count > 0 ? dragWidthsPx[(int)(dragWidthsPx.Count * 0.1)] : float.NaN;
        float p90Drag = dragWidthsPx.Count > 0 ? dragWidthsPx[(int)(dragWidthsPx.Count * 0.9)] : float.NaN;
        anisotropies.Sort();
        float medianA = anisotropies.Count > 0 ? anisotropies[anisotropies.Count / 2] : 0f;
        localR.Sort();
        float p10R = localR.Count > 0 ? localR[(int)(localR.Count * 0.1)] : float.NaN;
        float p50R = localR.Count > 0 ? localR[localR.Count / 2] : float.NaN;
        float p90R = localR.Count > 0 ? localR[(int)(localR.Count * 0.9)] : float.NaN;
        contrasts.Sort();
        float p10C = contrasts.Count > 0 ? contrasts[(int)(contrasts.Count * 0.1)] : float.NaN;
        float p50C = contrasts.Count > 0 ? contrasts[contrasts.Count / 2] : float.NaN;
        float p90C = contrasts.Count > 0 ? contrasts[(int)(contrasts.Count * 0.9)] : float.NaN;

        // 方向直方图（8 桶，限于矢量场掩膜内）
        var dirHist = new int[8];
        for (int i = 0; i < n; i++)
        {
            if (!shake.Mask[i]) continue;
            float d = shake.Direction[i];
            if (float.IsNaN(d)) continue;
            int bin = (int)(d / (MathF.PI / 8));
            if (bin >= 8) bin = 7;
            if (bin < 0) bin = 0;
            dirHist[bin]++;
        }
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"file        : {name}");
        sb.AppendLine($"dimensions  : {w} × {h}  diag={diagonal:F1} px");
        sb.AppendLine($"cv schema   : {cv.Version}");
        sb.AppendLine();
        sb.AppendLine("─── shake field ───");
        sb.AppendLine($"valid cells       : {validCells} / 1024  (mask_ratio={rigid.MaskRatio * 100:F1}%)");
        sb.AppendLine($"anisotropic cells : {anisotropicCells} / 1024  (A ≥ {CvHeatmap.AnisotropyMin:F2})");
        sb.AppendLine($"median A          : {medianA:F3}");
        sb.AppendLine($"drag_width p10/50/p90 : {p10Drag:F2} / {medianDrag:F2} / {p90Drag:F2} px");
        sb.AppendLine($"drag_r  p10/50/p90    : {p10Drag / diagonal * 100:F3}% / {medianDrag / diagonal * 100:F3}% / {p90Drag / diagonal * 100:F3}%");
        sb.AppendLine($"R_local p10/50/p90    : {p10R:F3} / {p50R:F3} / {p90R:F3}   (n={localR.Count})");
        sb.AppendLine($"contrast p10/50/p90   : {p10C:F1} / {p50C:F1} / {p90C:F1}   (n={contrasts.Count})");
        sb.AppendLine();
        sb.AppendLine("direction histogram (drag-line angle, 8 bins of π/8):");
        string[] arrows = { "→",  "↗",  "↑",  "↖",  "←",  "↙",  "↓",  "↘"  };
        // bin 0 = 0..22.5°（拖影线水平→），bin 2 = 45..67.5° 等。 ↑ 对应竖直拖影。
        for (int b = 0; b < 8; b++)
        {
            double lo = b * 22.5;
            double hi = (b + 1) * 22.5;
            sb.AppendLine($"  [{lo,5:F1}°,{hi,5:F1}°) {arrows[b]}  count = {dirHist[b]}");
        }
        sb.AppendLine();
        sb.AppendLine("─── rigid motion fit ───");
        sb.AppendLine($"samples : {rigid.SampleCount}");
        sb.AppendLine($"Σw      : {rigid.WeightSum:F1}");
        sb.AppendLine($"|T|     : {rigid.TranslationMagnitude:F2} px");
        sb.AppendLine($"|ω|     : {rigid.RotationMagnitude:F4} rad   ratio={rigid.OmegaPxRatio:F3}");
        sb.AppendLine($"residual: {rigid.ResidualRms:F2} px");
        sb.AppendLine($"R_global: {rigid.DirectionalConsistency:F3}");
        sb.AppendLine($"R_local p10 (fit): {rigid.RLocalP10:F3}");
        sb.AppendLine($"verdict : {VerdictLabel(rigid, diagonal)}");
        sb.AppendLine();
        return sb.ToString();
    }

    /// <summary>v5 r2 判定（与 DinoDebugViewModel.FormatRigidMotion 同优先级，调 CvHeatmap 常量同步）。</summary>
    private static string VerdictLabel(RigidMotionResult r, float diagonal)
    {
        float halfDiag = diagonal * 0.5f;
        float omegaPx = r.RotationMagnitude * halfDiag;
        float motionScale = MathF.Max(MathF.Sqrt(r.TranslationMagnitude * r.TranslationMagnitude + omegaPx * omegaPx), 1e-3f);
        float translateR = diagonal > 0 ? r.TranslationMagnitude / diagonal : 0f;
        if (r.WeightSum < CvHeatmap.WeightSumMin || r.MaskRatio < CvHeatmap.MaskRatioMin) return "信息不足";
        if (r.RotationMagnitude >= CvHeatmap.OmegaStrongRot
            && r.DirectionalConsistency >= CvHeatmap.RGlobalStrongRotAbove) return "强旋转抖动";
        if (r.DirectionalConsistency < CvHeatmap.RGlobalQuietBelow) return "静止纹理";
        if (r.RotationMagnitude >= CvHeatmap.OmegaRot
            && r.DirectionalConsistency >= CvHeatmap.RGlobalMotionAbove
            && r.RLocalP10 >= CvHeatmap.RLocalP10RotMin) return "旋转抖动";
        if (translateR >= CvHeatmap.TranslateMinDragR
            && r.DirectionalConsistency >= CvHeatmap.RGlobalMotionAbove) return "平移抖动";
        if (r.ResidualRms > CvHeatmap.ResidualMotionRatio * motionScale) return "混乱场景";
        if (translateR < CvHeatmap.TranslateMinDragR && r.RotationMagnitude < CvHeatmap.OmegaRot) return "静止纹理";
        return "弱信号 / 难判";
    }

    private static void SaveSharpnessPng(float[] plane, string outPath)
    {
        int n = CvGridResult.GridSize;
        int scale = 16;
        using var img = new SixLabors.ImageSharp.Image<Rgba32>(n * scale, n * scale);
        img.ProcessPixelRows(rows =>
        {
            for (int y = 0; y < n * scale; y++)
            {
                var row = rows.GetRowSpan(y);
                int gy = y / scale;
                for (int x = 0; x < n * scale; x++)
                {
                    int gx = x / scale;
                    float t = plane[gy * n + gx];
                    if (t < 0) t = 0;
                    if (t > 1) t = 1;
                    var c = Viridis(t);
                    row[x] = new Rgba32(c.R, c.G, c.B, 255);
                }
            }
        });
        img.SaveAsPng(outPath);
    }

    private static void SaveShakePng(ShakeField field, byte[] rgb, int srcW, int srcH, string outPath)
    {
        int n = CvGridResult.GridSize;
        int outW = 2048;
        int outH = (int)Math.Round(outW * (double)srcH / srcW);
        using var img = new SixLabors.ImageSharp.Image<Rgba32>(outW, outH);

        // 1) 暗底叠原图缩略：方便核对拖影方向是否真的与图中纹理对应。
        //    用 0.20 brightness 不到全黑，既能看出原图轮廓，又让"黑色 = 无信号"的线段可读。
        using (var src = new SixLabors.ImageSharp.Image<Rgb24>(srcW, srcH))
        {
            src.CopyPixelDataFromBytes(rgb);
            src.Mutate(c => c.Resize(outW, outH));
            img.Mutate(c => c.DrawImage(src.CloneAs<Rgba32>(), new GraphicsOptions { ColorBlendingMode = PixelColorBlendingMode.Normal, BlendPercentage = 1f }));
        }
        img.Mutate(c => c.Brightness(0.20f));

        if (field.Diagonal <= 0) { img.SaveAsPng(outPath); return; }

        // 2) 每格中心画一条短线段：长度固定，颜色按 drag_r 6 段。
        double cellW = (double)outW / n;
        double cellH = (double)outH / n;
        double half = Math.Min(cellW, cellH) * 0.5 * 0.85;

        img.Mutate(ctx =>
        {
            for (int gy = 0; gy < n; gy++)
            {
                for (int gx = 0; gx < n; gx++)
                {
                    int i = gy * n + gx;
                    if (!field.Mask[i]) continue;
                    float wpx = field.Width[i];
                    float dir = field.Direction[i];
                    if (float.IsNaN(wpx) || float.IsNaN(dir)) continue;
                    float dragR = wpx / field.Diagonal;
                    if (dragR < CvHeatmap.DragRMinDisplay) continue;
                    float rLocal = field.LocalConsistency[i];
                    float cf = field.Contrast.Length > i ? CvHeatmap.ContrastFactor(field.Contrast[i]) : 1f;
                    var color = CvHeatmap.ColorForShake(dragR, rLocal, cf);
                    double cx = (gx + 0.5) * cellW;
                    double cy = (gy + 0.5) * cellH;
                    double hx = Math.Cos(dir) * half;
                    double hy = Math.Sin(dir) * half;
                    ctx.DrawLine(new Rgba32(color.R, color.G, color.B, 255), 4.0f,
                        new PointF((float)(cx - hx), (float)(cy - hy)),
                        new PointF((float)(cx + hx), (float)(cy + hy)));
                }
            }
        });

        img.SaveAsPng(outPath);
    }

    private static (byte R, byte G, byte B) Viridis(float t)
    {
        // 5-stop viridis approximation
        (float r, float g, float b)[] s =
        {
            (0.267f, 0.005f, 0.329f),
            (0.229f, 0.322f, 0.546f),
            (0.127f, 0.566f, 0.551f),
            (0.369f, 0.788f, 0.383f),
            (0.993f, 0.906f, 0.144f),
        };
        if (t <= 0) return ((byte)(s[0].r * 255), (byte)(s[0].g * 255), (byte)(s[0].b * 255));
        if (t >= 1) return ((byte)(s[4].r * 255), (byte)(s[4].g * 255), (byte)(s[4].b * 255));
        float pos = t * 4;
        int idx = (int)pos;
        float k = pos - idx;
        float r = s[idx].r + (s[idx + 1].r - s[idx].r) * k;
        float g = s[idx].g + (s[idx + 1].g - s[idx].g) * k;
        float b = s[idx].b + (s[idx + 1].b - s[idx].b) * k;
        return ((byte)(r * 255), (byte)(g * 255), (byte)(b * 255));
    }
}

internal static class Rgb24Extensions
{
    public static void CopyPixelDataFromBytes(this SixLabors.ImageSharp.Image<Rgb24> img, byte[] src)
    {
        img.ProcessPixelRows(rows =>
        {
            int idx = 0;
            for (int y = 0; y < img.Height; y++)
            {
                var row = rows.GetRowSpan(y);
                for (int x = 0; x < img.Width; x++)
                {
                    row[x] = new Rgb24(src[idx], src[idx + 1], src[idx + 2]);
                    idx += 3;
                }
            }
        });
    }
}
