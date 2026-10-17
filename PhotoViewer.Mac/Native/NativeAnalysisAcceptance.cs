using AppKit;
using CoreGraphics;
using PhotoViewer.Apple;
using PhotoViewer.Core.AI;
using PhotoViewer.Core.Color;
using PhotoViewer.Core.Image;
using PhotoViewer.Mac.Core;
using PhotoViewer.ViewModels.Main;
using PhotoViewer.ViewModels.Settings;

namespace PhotoViewer.Mac.Native;

/// <summary>
/// 分析栏与增强预览的原生回归，仅在隔离冒烟会话中以
/// <c>--pv-native-smoke --pv-native-analysis &lt;绝对图片路径&gt;</c> 运行。
/// 覆盖：ToggleAnalysisView 命令入口与布局合入（行/列两向的瓦片边长、间距、内边距、交叉轴尺寸）、
/// 瓦片顺序与角标、诊断瓦片点击落准星、直方图与细节预览在照片就绪后出现、
/// 增强预览开关经共享 PhotoLoadSession 生效并还原、关闭后主图恢复占满剩余区域。
/// 只读自有测试素材，不写用户照片。
/// </summary>
internal static class NativeAnalysisAcceptance
{
    /// <summary>执行分析栏回归并写出检查清单。</summary>
    /// <param name="window">真实原生主窗口。</param>
    /// <param name="output">报告输出目录。</param>
    internal static async Task RunAsync(MacMainWindow window, string output)
    {
        var checks = new List<string>();
        var main = window.Main;
        var panel = window.Analysis;
        var settings = main.Settings;
        var snapshot = settings.CreateSnapshot();
        bool filmVisible = main.IsThumbnailViewVisible, controlVisible = main.IsControlViewVisible;
        try
        {
            settings.LayoutMode = LayoutMode.Row;
            await SettleAsync(window);

            // 1) 初始隐藏，且不参与布局。
            if (main.IsAnalysisViewVisible || !panel.Hidden) throw new InvalidOperationException("分析栏初始应为隐藏");
            var root = window.Window.ContentView ?? throw new InvalidOperationException("原生内容视图缺失");
            var photoPane = root.Subviews.Single(view => view.Subviews.Contains(window.Photo));
            double baselinePhotoHeight = (double)photoPane.Frame.Height;
            checks.Add("initial: analysis panel hidden and excluded from layout");

            // 2) 命令入口：ToggleAnalysisView 打开分析栏并使主图让出空间。
            window.Execute("ToggleAnalysisView");
            await SettleAsync(window);
            if (!main.IsAnalysisViewVisible || panel.Hidden) throw new InvalidOperationException("ToggleAnalysisView 未显示分析栏");
            if ((double)photoPane.Frame.Height >= baselinePhotoHeight) throw new InvalidOperationException("分析栏显示后主图未让出空间");
            VerifyGeometry(window, panel, row: true, checks);
            VerifyTiles(panel, checks);

            // 3) 诊断瓦片点击 → 归一化准星（与旧壳 AnalysisView 同一入口）。
            panel.Presenter.OnTileClicked(0.25, 0.75);
            await SettleAsync(window);
            if (panel.Presenter.CrosshairU is not { } u || Math.Abs(u - 0.25) > 0.001
                || panel.Presenter.CrosshairV is not { } v || Math.Abs(v - 0.75) > 0.001)
                throw new InvalidOperationException($"诊断瓦片点击未落准星：{panel.Presenter.CrosshairU},{panel.Presenter.CrosshairV}");
            panel.Presenter.ClearCrosshair();
            checks.Add("tiles: click sets normalised crosshair and clear resets it");

            // 4) 直方图与细节预览在照片就绪后出内容（直方图来自当前展示图，细节来自全分辨率裁剪）。
            await WaitAsync(() => panel.Presenter.HistogramTile.Source != null, "直方图出图");
            if (panel.TileViews.OfType<MacDetailPreview>().Any() is false)
                throw new InvalidOperationException("分析栏缺少细节预览瓦片");
            await WaitAsync(() => DetailHasContent(panel), "细节预览出图");
            checks.Add($"ready: histogram and detail preview rendered (proxy={window.Session.IsProxy})");

            // 5) 增强预览：命令经共享展示会话生效 → 低对比度素材的 GPU 帧像素明显变化 → 再关一次还原原片。
            string lowContrast = Path.Combine(output, "enhance-lowcontrast.jpg");
            EnhancementAcceptance.CreateLowContrastJpeg(lowContrast);
            await main.FolderVM.OpenImageAsync(new MacFileRef(lowContrast));
            await WaitAsync(() => main.CurrentFile?.Name == Path.GetFileName(lowContrast), "低对比度素材成为当前照片");
            // Mac 展示编码恒为 F16 线性 scRGB；这里只要求已是全分辨率（代理档不参与增强回归）。
            await WaitAsync(() => window.Session.Image != null && !window.Session.IsProxy, "低对比度素材 Full 档");
            await SettleAsync(window);
            (double Mean, int Max) difference;
            using (var before = CaptureSrgbFrame(window))
            {
                window.Execute("ToggleEnhance");
                if (!window.Session.IsEnhanced) throw new InvalidOperationException("ToggleEnhance 未开启增强预览");
                await WaitAsync(() => window.Session.EnhancedDisplay, "增强图就绪");
                var enhancedBytes = window.Session.Image?.ByteCount ?? 0;
                if (enhancedBytes <= 0) throw new InvalidOperationException("增强图缺少像素数据");
                await SettleAsync(window);
                using var after = CaptureSrgbFrame(window);
                difference = EnhancementAcceptance.MeasureDifference(before, after);
                if (difference.Mean < 6 || difference.Max < 30)
                    throw new InvalidOperationException(
                        $"增强预览未改变上屏像素：平均差 {difference.Mean:0.##}、最大差 {difference.Max}（要求 ≥6 / ≥30）");
                window.Execute("ToggleEnhance");
                await SettleAsync(window);
                if (window.Session.IsEnhanced || window.Session.EnhancedDisplay) throw new InvalidOperationException("再次 ToggleEnhance 未还原原片");
                using var restored = CaptureSrgbFrame(window);
                var restoreDifference = EnhancementAcceptance.MeasureDifference(before, restored);
                if (restoreDifference.Mean > 2 || restoreDifference.Max > 12)
                    throw new InvalidOperationException(
                        $"关闭增强后未还原原片：平均差 {restoreDifference.Mean:0.##}、最大差 {restoreDifference.Max}");
                checks.Add($"enhance: GPU frame changed (mean {difference.Mean:0.##}, max {difference.Max}, {enhancedBytes} bytes) and restored exactly");
            }
            NativeSnapshots.SaveMain(window, Path.Combine(output, "native-analysis-row.png"));

            // 6) 列布局：瓦片纵向排列，交叉轴尺寸 = 边长 + 20。
            settings.LayoutMode = LayoutMode.Col;
            await SettleAsync(window);
            VerifyGeometry(window, panel, row: false, checks);
            NativeSnapshots.SaveMain(window, Path.Combine(output, "native-analysis-col.png"));

            // 8) 特征入库后分析栏即时刷新（回归"点『提取全部』后诊断瓦片仍停在未提取"）。
            //    用一个本次会话才出现的固定名副本保证"尚未提取"，跑一次真实索引后断言瓦片自行出图且未切图。
            await VerifyIndexRefreshAsync(main, window, output, checks);

            // 7) 关闭分析栏后主图恢复占满剩余区域。
            window.Execute("ToggleAnalysisView");
            await SettleAsync(window);
            if (main.IsAnalysisViewVisible || !panel.Hidden) throw new InvalidOperationException("ToggleAnalysisView 未关闭分析栏");
            checks.Add("close: analysis panel hidden and photo pane restored");
        }
        finally
        {
            window.Main.IsAnalysisViewVisible = false;
            settings.ApplySnapshot(snapshot);
            main.IsThumbnailViewVisible = filmVisible;
            main.IsControlViewVisible = controlVisible;
            await SettleAsync(window);
            await File.WriteAllLinesAsync(Path.Combine(output, "native-analysis-checks.txt"), checks);
        }
        Console.WriteLine("[NativeSmoke] PASS analysis");
    }

    /// <summary>核对分析栏在主图与相邻栏位之间的实际几何：交叉轴尺寸、边长、间距与内边距。</summary>
    private static void VerifyGeometry(MacMainWindow window, MacAnalysisPanel panel, bool row, List<string> checks)
    {
        var tiles = panel.TileViews;
        if (tiles.Count < 6) throw new InvalidOperationException($"分析栏瓦片数不足：{tiles.Count}");
        int count = tiles.Count;
        double main = row ? (double)panel.Bounds.Width : (double)panel.Bounds.Height;
        double cross = row ? (double)panel.Bounds.Height : (double)panel.Bounds.Width;
        double expectedTile = AnalysisPanelGeometry.TileSize(main, count);
        if (Math.Abs(cross - (expectedTile + 20)) > 1.5)
            throw new InvalidOperationException($"{(row ? "行" : "列")}布局分析栏交叉轴尺寸 {cross} 与边长 {expectedTile} + 20 不符");
        for (int i = 0; i < count; i++)
        {
            var frame = tiles[i].Frame;
            if (Math.Abs((double)frame.Width - expectedTile) > 1.5 || Math.Abs((double)frame.Height - expectedTile) > 1.5)
                throw new InvalidOperationException($"瓦片 {i} 不是 {expectedTile} 的正方形：{frame}");
            double offset = row ? (double)frame.X : (double)frame.Y;
            double expectedOffset = 10 + i * (expectedTile + 8);
            if (Math.Abs(offset - expectedOffset) > 1.5)
                throw new InvalidOperationException($"瓦片 {i} 主轴起点 {offset} 与 10 + {i}×(边长+8) 不符");
            double crossOffset = row ? (double)frame.Y : (double)frame.X;
            if (Math.Abs(crossOffset - 10) > 1.5)
                throw new InvalidOperationException($"瓦片 {i} 交叉轴内边距 {crossOffset} 应为 10");
        }
        checks.Add($"geometry {(row ? "row" : "col")}: {count} square tiles side={expectedTile} gap=8 padding=10 cross={cross}");
    }

    /// <summary>核对瓦片顺序与角标与旧壳分析栏一致；抖动瓦片在库里有判定时角标改为判定文字。</summary>
    private static void VerifyTiles(MacAnalysisPanel panel, List<string> checks)
    {
        var labels = panel.Presenter.Tiles.Select(tile => tile.ShortLabel).ToArray();
        string[] expected = ["中心", "直方图", "DINO PCA", "Cosine 16,16", "锐度"];
        if (!labels.Take(5).SequenceEqual(expected))
            throw new InvalidOperationException($"分析栏瓦片顺序/角标不符：{string.Join(" | ", labels)}");
        string[] verdicts = ["抖动拖影", "信息不足", "静止纹理", "强旋转抖动", "旋转抖动", "平移抖动",
            "混乱场景（车流 / 树叶 / 各向异性纹理）", "弱信号 / 难判"];
        if (!verdicts.Contains(labels[5], StringComparer.Ordinal))
            throw new InvalidOperationException($"抖动瓦片角标既不是占位也不是判定文字：{labels[5]}");
        if (panel.Presenter.Tiles.Any(tile => tile.Kind == AnalysisTileKind.Detail) is false)
            throw new InvalidOperationException("分析栏缺少细节预览瓦片");
        checks.Add($"tiles: order and labels match the Avalonia analysis panel (shake label = {labels[5]})");
    }

    /// <summary>抓取主图当前 GPU 帧并归一为 BGRA8 sRGB，供增强前后逐像素比较。</summary>
    /// <param name="window">原生主窗口。</param>
    /// <returns>由调用方释放的 BGRA8 帧。</returns>
    private static DecodedImage CaptureSrgbFrame(MacMainWindow window)
    {
        using var linear = window.Photo.CaptureLinearFrame();
        return ColorEngine.ToSrgb8(linear);
    }

    /// <summary>细节预览是否已贴上裁剪内容（代理期按旧壳口径留空）。</summary>
    private static bool DetailHasContent(MacAnalysisPanel panel)
        => panel.TileViews.OfType<MacDetailPreview>().All(preview => preview.HasContent);

    /// <summary>
    /// 特征入库后分析栏即时刷新：复制一份唯一文件名的素材（指纹必然未入库）→ 打开它并确认四张诊断瓦片为
    /// "未提取" → 跑一次真实批量索引 → 断言瓦片自行出图，且期间没有切换当前照片。
    /// 产物写在验收输出目录并在结束时删除，不碰用户照片。
    /// </summary>
    private static async Task VerifyIndexRefreshAsync(MainViewModel main, MacMainWindow window, string output, List<string> checks)
    {
        var fixture = main.CurrentFile ?? throw new InvalidOperationException("缺少当前照片");
        string probe = Path.Combine(output, $"refresh-probe-{DateTime.UtcNow.Ticks}.jpg");
        File.Copy(fixture.File.Path.LocalPath, probe, true);
        try
        {
            await main.FolderVM.OpenImageAsync(new MacFileRef(probe));
            await WaitAsync(() => main.CurrentFile?.Name == Path.GetFileName(probe), "探针图成为当前照片");
            var presenter = window.Analysis.Presenter;
            await WaitAsync(() => presenter.PcaTile.Placeholder == "未提取" && presenter.SharpnessTile.Placeholder == "未提取",
                "探针图初始为未提取");
            if (main.Settings.AnalysisImmediateCompute)
                throw new InvalidOperationException("本回归要求关闭「即时计算DINO/CV」，否则无法区分库路径与现算路径");

            var indexer = new FolderFeatureIndexer();
            await indexer.RunAsync([main.CurrentFile!]);
            await WaitAsync(() => presenter.PcaTile.Source != null && presenter.SharpnessTile.Source != null
                && presenter.CosineTile.Source != null && presenter.ShakeTile.ShakeField != null, "入库后诊断瓦片自行出图");
            if (main.CurrentFile?.Name != Path.GetFileName(probe))
                throw new InvalidOperationException("回归期间当前照片被切换，无法证明是入库信号触发的刷新");
            checks.Add("index refresh: diagnostic tiles populate right after features are written, without switching photo");
        }
        finally
        {
            File.Delete(probe);
        }
    }

    /// <summary>切换布局后等待根视图重排完成。</summary>
    private static async Task SettleAsync(MacMainWindow window)
    {
        await Task.Delay(220);
        window.Window.ContentView?.LayoutSubtreeIfNeeded();
        await Task.Delay(120);
    }

    /// <summary>轮询等待条件成立，超时即失败（不放行假通过）。</summary>
    private static async Task WaitAsync(Func<bool> condition, string label)
    {
        for (int attempt = 0; attempt < 400; attempt++)
        {
            if (condition()) return;
            await Task.Delay(25);
        }
        throw new InvalidOperationException($"等待超时：{label}");
    }
}
