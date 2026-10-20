using System.Diagnostics;
using CoreGraphics;
using Foundation;
using PhotoViewer.Apple;
using PhotoViewer.Core;
using PhotoViewer.Core.AI;
using PhotoViewer.Core.Color;
using PhotoViewer.Core.Image;
using PhotoViewer.Core.Platform;
using PhotoViewer.iOS.Core;
using PhotoViewer.ViewModels.Main;
using PhotoViewer.ViewModels.Settings;
using UIKit;

namespace PhotoViewer.iOS.Native;

/// <summary>
/// I5 验收专用（非产品行为，测试钩子）：<c>--pv-native-analysis-check &lt;目录&gt;</c> 在模拟器/真机上程序化核对
/// 分析栏与增强预览——入口（主图菜单 + 控制栏命令）、两向瓦片几何（边长/间距 8/内边距 10/交叉轴）、
/// 瓦片顺序与角标、诊断瓦片点击落准星、F16 代理与 8 位 P3 Full 两种展示编码下的直方图/细节裁剪
/// （细节裁剪中心像素与主图帧读回对比）、"未提取"→ 真实批量索引后瓦片自行出图且不切图、
/// 增强预览经 <see cref="PhotoLoadSession"/> 生效并还原、关闭后主图恢复占满、细节活动目标的触控序列与过期事件隔离。
/// 素材要求：目录内至少一张 jpg 与 <c>color-p3-large.jpg</c>（长边 ≥ 4000，用于制造真实代理档与 8 位 P3 Full），
/// 且必须位于 App 沙盒内 —— 不写用户照片或 TestCase。报告写 Documents/PhotoViewer/native-analysis-checks.txt。
/// </summary>
internal static class iOSNativeAnalysisCheck
{
    /// <summary>验收开关。</summary>
    private const string Switch = "--pv-native-analysis-check";
    /// <summary>布局/动画沉淀等待。</summary>
    private static readonly TimeSpan Settle = TimeSpan.FromMilliseconds(200);
    /// <summary>细节裁剪中心与主图帧读回的每通道容差（色卡均匀色块，允许 mipmap 与 P3 量化差异）。</summary>
    private const int PixelTolerance = 18;

    /// <summary>解析验收开关；命中即以给定目录启动异步核对。</summary>
    /// <param name="args">进程启动参数。</param>
    /// <param name="root">原生根控制器。</param>
    /// <param name="session">原生展示会话。</param>
    public static void MaybeStart(string[] args, iOSNativeRootViewController root, PhotoLoadSession session)
    {
        string? directory = null;
        for (int index = 0; index + 1 < args.Length; index++)
        {
            if (string.Equals(args[index], Switch, StringComparison.Ordinal)) directory = args[index + 1];
        }
        if (directory == null) return;
        _ = RunAsync(root, session, ResolveDocumentsPath(directory));
    }

    /// <summary>相对路径按沙盒 Documents 解析（iOS 无启动工作目录语义）。</summary>
    /// <param name="path">绝对或相对路径。</param>
    /// <returns>绝对路径。</returns>
    private static string ResolveDocumentsPath(string path)
        => Path.IsPathRooted(path) ? path : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), path);

    /// <summary>核对主流程：等主屏与素材就绪 → 逐项断言 → 落盘并打印结论。</summary>
    /// <param name="root">原生根控制器。</param>
    /// <param name="session">原生展示会话。</param>
    /// <param name="directory">自有夹具目录（绝对路径）。</param>
    private static async Task RunAsync(iOSNativeRootViewController root, PhotoLoadSession session, string directory)
    {
        var lines = new List<string>();
        string output = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "PhotoViewer");
        Directory.CreateDirectory(output);
        string report = Path.Combine(output, "native-analysis-checks.txt");
        try
        {
            var main = iOSNativeShell.Main ?? throw new InvalidOperationException("原生壳主 VM 未就绪");
            if (!Path.GetFullPath(directory).StartsWith(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), StringComparison.Ordinal))
                throw new InvalidOperationException($"分析验收只允许使用 App 沙盒内的自有夹具目录：{directory}");
            string[] fixtures = Directory.GetFiles(directory, "*.jpg");
            if (fixtures.Length == 0) throw new InvalidOperationException($"分析验收需要自有 jpg 夹具：{directory}");
            string largeCard = fixtures.FirstOrDefault(path => string.Equals(Path.GetFileName(path), "color-p3-large.jpg", StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException($"分析验收需要 color-p3-large.jpg（真实代理档与 8 位 P3 Full）：{directory}");
            await WaitAsync(() => root.View!.Bounds.Width > 100 && root.Photo.Bounds.Width > 100, "主屏布局");
            var snapshot = main.Settings.CreateSnapshot();
            var originalFile = main.CurrentFile;
            bool entryAnalysis = main.IsAnalysisViewVisible, entryFilm = main.IsThumbnailViewVisible, entryControl = main.IsControlViewVisible;
            bool entrySimilarity = main.FileVM.FilterBar.IsSimilarityPanelOpen;
            var analysisHotkey = main.Settings.Hotkeys.First(hotkey => hotkey.Command == "ToggleAnalysisView");
            var enhanceHotkey = main.Settings.Hotkeys.First(hotkey => hotkey.Command == "ToggleEnhance");
            bool entryAnalysisDisplay = analysisHotkey.IsDisplay;
            bool entryEnhanceDisplay = enhanceHotkey.IsDisplay;
            try
            {
                await UiDispatcher.InvokeAsync(() =>
                {
                    main.Settings.ShowRating = true;
                    main.Settings.LayoutMode = LayoutMode.Row;
                    main.Settings.AnalysisImmediateCompute = false;
                    main.IsThumbnailViewVisible = true;
                    main.IsControlViewVisible = true;
                    main.IsAnalysisViewVisible = false;
                    main.FileVM.FilterBar.IsSimilarityPanelOpen = false;
                    main.FileVM.FilterBar.SelectedRatingFilter = "All";
                    EnsureJpegEnabled(main);
                });
                await main.FolderVM.OpenFolderAsync(new iOSFolderRef(directory));
                await WaitAsync(() => main.FolderVM.AllFiles.Count > 0, "夹具目录枚举");
                await WaitAsync(() => main.CurrentFile != null && session.Image != null, "首图解码");
                await SettleAsync(root);
                lines.Add($"PASS 素材：{fixtures.Length} 张自有夹具（{directory}），主屏 {Size(root.View!.Bounds.Size)} 安全区 {Insets(root.View!.SafeAreaInsets)}");

                // 一、入口：主图菜单项 + 控制栏命令（真实按钮点击）。
                var menu = root.BuildPhotoMenu();
                var ids = Flatten(menu.Menu).Select(action => action.Id).ToArray();
                if (!ids.Contains("photo.analysis"))
                    throw new InvalidOperationException($"主图菜单缺少分析栏入口：{string.Join(",", ids)}");
                await UiDispatcher.InvokeAsync(() =>
                {
                    analysisHotkey.IsDisplay = true;
                    root.BuildControls();
                });
                await SettleAsync(root);
                if (!root.ControlBar.Commands.TryGetValue("ToggleAnalysisView", out var analysisButton))
                    throw new InvalidOperationException("控制栏未生成 ToggleAnalysisView 命令入口");
                double baselinePhoto = root.PhotoMainLength(true);
                await UiDispatcher.InvokeAsync(() => analysisButton.SendActionForControlEvents(UIControlEvent.TouchUpInside));
                await WaitAsync(() => main.IsAnalysisViewVisible && !root.AnalysisPane.Hidden, "控制栏命令打开分析栏");
                await SettleAsync(root);
                if (root.PhotoMainLength(true) >= baselinePhoto)
                    throw new InvalidOperationException("分析栏显示后主图未让出空间");
                lines.Add("PASS 入口：主图菜单含分析栏，控制栏 ToggleAnalysisView 真实点击后分析栏显示且主图让位");

                // 二、瓦片顺序与角标。
                var labels = root.AnalysisPane.Presenter.Tiles.Select(tile => tile.ShortLabel).ToArray();
                string[] expected = ["中心", "直方图", "DINO PCA", "Cosine 16,16", "锐度"];
                if (!labels.Take(5).SequenceEqual(expected))
                    throw new InvalidOperationException($"分析栏瓦片顺序/角标不符：{string.Join(" | ", labels)}");
                lines.Add($"PASS 瓦片顺序与角标：{string.Join(" | ", labels)}");
                var diagnostics = root.AnalysisPane.TileViews.OfType<iOSDiagnosticTile>().ToArray();
                if (diagnostics.Length == 0 || diagnostics.Any(tile => !tile.UsesNearestNeighbour))
                    throw new InvalidOperationException("诊断瓦片主图未使用最近邻放大（热力图会糊）");
                if (diagnostics.Any(tile => !tile.BackgroundDimmed))
                    throw new InvalidOperationException("诊断瓦片背景层未压暗到 alpha 0.30");
                lines.Add($"PASS 瓦片绘制：{diagnostics.Length} 张诊断瓦片最近邻放大 + 背景 alpha 0.30");

                // 三、两向几何（显式布局模式）。
                VerifyGeometry(root, row: true, lines);
                await UiDispatcher.InvokeAsync(() => main.Settings.LayoutMode = LayoutMode.Col);
                await SettleAsync(root);
                VerifyGeometry(root, row: false, lines);
                await UiDispatcher.InvokeAsync(() => main.Settings.LayoutMode = LayoutMode.Row);
                await SettleAsync(root);

                await UiDispatcher.InvokeAsync(() =>
                {
                    var diagnostics = root.AnalysisPane.TileViews.OfType<iOSDiagnosticTile>().Skip(1).ToArray();
                    foreach (var diagnostic in diagnostics)
                    {
                        iOSNativeTapProbe.Tap(diagnostic, .25, .75);
                        if (root.AnalysisPane.Presenter.CrosshairU is not { } u || Math.Abs(u - .25) > .001
                            || root.AnalysisPane.Presenter.CrosshairV is not { } v || Math.Abs(v - .75) > .001)
                            throw new InvalidOperationException("诊断瓦片命中链路没有落共享准星");
                        diagnostic.TapAt(CGPoint.Empty);
                        if (root.AnalysisPane.Presenter.CrosshairU != null) throw new InvalidOperationException("边框未清空准星");
                    }
                });
                lines.Add("PASS 诊断四瓦片：UIKit Window.HitTest逐层命中+瓦片事件处理注入，落共享准星/边框清空（非系统触摸注入）");

                // 五、F16 代理（分析栏关闭，避免其 needsFull 直接升级 Full）：
                //     直方图渲染器与 DisplayCrop 按 F16 口径直接出图。
                await UiDispatcher.InvokeAsync(() => main.IsAnalysisViewVisible = false);
                await SettleAsync(root);
                await SelectFileAsync(main, largeCard);
                await WaitAsync(() => session.Image is { Encoding: PixelEncoding.RgbaF16 } && session.IsProxy, "大色卡代理档（F16）");
                VerifyProxyHistogram(session.Image!, lines);
                VerifyCropPixel(root, session.Image!, "F16 代理", lines);
                if (root.AnalysisPane.DetailPreviews.Any(preview => preview.HasContent))
                    throw new InvalidOperationException("代理期细节预览应按旧壳口径留空");

                // 六、打开分析栏（真实菜单入口）后主图升 Full（8 位 P3）：直方图瓦片与细节预览出图，
                //     细节裁剪中心像素与主图帧读回一致。
                var reopen = root.BuildPhotoMenu();
                reopen.Handlers["photo.analysis"]();
                await WaitAsync(() => main.IsAnalysisViewVisible && !root.AnalysisPane.Hidden, "菜单重开分析栏");
                await WaitAsync(() => session.Image is { } image && !session.IsProxy && image.Encoding == PixelEncoding.Rgba8,
                    "Full 档（8 位 P3）");
                await WaitAsync(() => root.AnalysisPane.Presenter.HistogramTile.Source != null, "Full 直方图出图");
                await WaitAsync(() => root.AnalysisPane.DetailPreviews.All(preview => preview.HasContent), "Full 细节预览出图");
                VerifyCropPixel(root, session.Image!, "8 位 P3 Full", lines);
                await UiDispatcher.InvokeAsync(() => root.Photo.FitToView());
                await SettleAsync(root);
                lines.Add("PASS 展示编码：F16 代理（HistogramRenderer + DisplayCrop）与 8 位 P3 Full（直方图/细节预览瓦片）均可用，裁剪中心与帧读回一致");

                // 七、增强预览：控制栏命令 → 会话增强图就绪 → **GPU 帧像素明显变化** → 再关一次还原。
                // 用户设置可能隐藏了增强按钮；验收按控制栏命令路径执行，临时打开显隐（收尾还原）。
                await UiDispatcher.InvokeAsync(() =>
                {
                    enhanceHotkey.IsDisplay = true;
                    root.BuildControls();
                });
                await SettleAsync(root);
                if (!root.ControlBar.Commands.TryGetValue("ToggleEnhance", out var enhanceButton))
                    throw new InvalidOperationException("控制栏未生成 ToggleEnhance 命令入口");
                // 低对比度素材（确定性生成、写自有沙盒）才能证明"像素真的变了"：
                // 高对比色卡的直方图接近端点饱和，增强前后可能几乎一致，无法回归"点了没反应"。
                string workspace = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "analysis-fixtures");
                Directory.CreateDirectory(workspace);
                string lowContrast = Path.Combine(workspace, "enhance-lowcontrast.jpg");
                EnhancementAcceptance.CreateLowContrastJpeg(lowContrast);
                await main.FolderVM.OpenImageAsync(new iOSFileRef(lowContrast));
                await WaitAsync(() => main.CurrentFile?.Name == Path.GetFileName(lowContrast), "低对比度素材成为当前照片");
                await WaitAsync(() => session.Image is { Encoding: PixelEncoding.Rgba8 } && !session.IsProxy, "低对比度素材 Full 档（8 位 P3）");
                await SettleAsync(root);
                (double Mean, int Max) difference;
                using (var before = CaptureSrgbFrame(root))
                {
                    await UiDispatcher.InvokeAsync(() => enhanceButton.SendActionForControlEvents(UIControlEvent.TouchUpInside));
                    if (!session.IsEnhanced) throw new InvalidOperationException("ToggleEnhance 未开启增强预览");
                    await WaitAsync(() => session.EnhancedDisplay, "增强图就绪（不静默回退）");
                    long enhancedBytes = session.Image?.ByteCount ?? 0;
                    if (enhancedBytes <= 0) throw new InvalidOperationException("增强图缺少像素数据");
                    await SettleAsync(root);
                    using var after = CaptureSrgbFrame(root);
                    difference = EnhancementAcceptance.MeasureDifference(before, after);
                    if (difference.Mean < 6 || difference.Max < 30)
                        throw new InvalidOperationException(
                            $"增强预览未改变上屏像素：平均差 {difference.Mean:0.##}、最大差 {difference.Max}（要求 ≥6 / ≥30）");
                    await UiDispatcher.InvokeAsync(() => enhanceButton.SendActionForControlEvents(UIControlEvent.TouchUpInside));
                    await SettleAsync(root);
                    if (session.IsEnhanced || session.EnhancedDisplay) throw new InvalidOperationException("再次 ToggleEnhance 未还原原片");
                    using var restored = CaptureSrgbFrame(root);
                    var restoreDifference = EnhancementAcceptance.MeasureDifference(before, restored);
                    if (restoreDifference.Mean > 2 || restoreDifference.Max > 12)
                        throw new InvalidOperationException(
                            $"关闭增强后未还原原片：平均差 {restoreDifference.Mean:0.##}、最大差 {restoreDifference.Max}");
                    lines.Add($"PASS 增强预览：低对比度素材上屏像素明显变化（平均差 {difference.Mean:0.##}、最大差 {difference.Max}，{enhancedBytes} 字节），关闭后完全还原");
                }

                // 七之二、增强不得改变方向：orientation-6/8 真实夹具（F16 代理）与一张 4000×3000 的
                // EXIF 6 竖拍素材（8 位 P3 Full）都要保持"源尺寸/方向 + 导航帧四角分类"不变。
                await VerifyEnhancedOrientationAsync(root, main, session, directory, workspace, lines);

                string focusFixture = Path.Combine(directory, "focus-src.ARW");
                string detailTrace = Path.Combine(output, "native-analysis-race-diagnostics.txt");
                File.WriteAllText(detailTrace, string.Empty);
                string? displayedFileKey = null;
                using var displaySignal = await UiDispatcher.InvokeAsync(() => session.Subscribe(file => displayedFileKey = file.PathKey));
                void TraceDetail(string stage, iOSDetailPreview? selected = null)
                {
                    string text = stage + " " + DescribeDetailProbe(root, main, session, displayedFileKey, selected);
                    File.AppendAllText(detailTrace, text + Environment.NewLine);
                    Console.WriteLine("[NativeRace] " + text);
                }
                bool hasFocusFixture = File.Exists(focusFixture);
                string expectedFileKey;
                if (hasFocusFixture)
                {
                    var focusFile = new iOSFileRef(focusFixture);
                    expectedFileKey = focusFile.PathKey;
                    await main.FolderVM.OpenImageAsync(focusFile);
                }
                else expectedFileKey = await UiDispatcher.InvokeAsync(() =>
                {
                    displayedFileKey = main.CurrentFile!.File.PathKey;
                    return displayedFileKey;
                });
                int detailCount = await VerifyDetailActivityAsync(root, main, session, expectedFileKey,
                    hasFocusFixture ? 2 : 1, () => displayedFileKey, TraceDetail);
                lines.Add($"PASS 细节预览事件注入（{detailCount}瓦片）：当前文件/Full/纹理/当前瓦片/布局就绪后在同一UI调用获取并测试；Down/滑入切换/移出移入/Up/多指旧Cancel隔离/触控优先/抬手无粘框/后续hover/失活（非硬件实测）");

                // 九、未提取 → 真实索引：探针副本入库后诊断瓦片自行出图，期间不切图。
                var indexing = await VerifyIndexRefreshAsync(root, main, directory, lines);
                lines.Add($"PASS 入库刷新：真实索引 {indexing} 后瓦片出图且未切图");

                // 十、关闭分析栏后主图恢复占满。
                menu = root.BuildPhotoMenu();
                menu.Handlers["photo.analysis"]();
                await WaitAsync(() => !main.IsAnalysisViewVisible && root.AnalysisPane.Hidden, "菜单关闭分析栏");
                await SettleAsync(root);
                if (Math.Abs(root.PhotoMainLength(true) - baselinePhoto) > 1)
                    throw new InvalidOperationException($"关闭后主图未恢复占满：{root.PhotoMainLength(true)} 基线 {baselinePhoto}");
                if (root.DetailHighlightVisible) throw new InvalidOperationException("关闭分析栏后主图绿框未收起");
                lines.Add("PASS 关闭：主图恢复原始高度，绿框收起");

                await RestoreAsync(root, main, snapshot, entryAnalysis, entryFilm, entryControl, entrySimilarity, entryAnalysisDisplay, entryEnhanceDisplay, originalFile);
                await File.WriteAllLinesAsync(report, lines);
                Console.WriteLine($"[NativeSmoke] PASS analysis ({lines.Count} checks)");
            }
            finally
            {
                var settings = main.Settings;
                settings.ApplySnapshot(snapshot);
                analysisHotkey.IsDisplay = entryAnalysisDisplay;
                enhanceHotkey.IsDisplay = entryEnhanceDisplay;
                main.IsAnalysisViewVisible = entryAnalysis;
                main.IsThumbnailViewVisible = entryFilm;
                main.IsControlViewVisible = entryControl;
                main.FileVM.FilterBar.IsSimilarityPanelOpen = entrySimilarity;
                if (originalFile != null) main.CurrentFile = originalFile;
                await SettleAsync(root);
                FlushSettings(main);
                if (!File.Exists(report)) await File.WriteAllLinesAsync(report, lines);
            }
        }
        catch (Exception exception)
        {
            lines.Add($"FAIL {exception}");
            await File.WriteAllLinesAsync(report, lines);
            Console.Error.WriteLine("[NativeSmoke] FAIL analysis " + exception);
        }
    }


    /// <summary>
    /// 增强预览不得旋转照片：对 EXIF 方向 6/8 的夹具分别核对增强前后
    /// ① 展示产物元数据（Orientation / SourceWidth / SourceHeight）一致；
    /// ② 导航帧四角颜色分类（红/蓝/白/灰）排列一致——方向丢失或被转置时排列必变；
    /// ③ 增强确实生效（帧像素有变化）。再补一张 4000×3000、EXIF 6 的竖拍素材走 8 位 P3 Full 档。
    /// </summary>
    /// <param name="root">原生根控制器。</param>
    /// <param name="main">共享主 VM。</param>
    /// <param name="session">展示会话。</param>
    /// <param name="directory">夹具目录（含 orientation-6/8.jpg）。</param>
    /// <param name="workspace">App 自建工作目录（生成大竖拍素材用）。</param>
    /// <param name="lines">报告行。</param>
    private static async Task VerifyEnhancedOrientationAsync(iOSNativeRootViewController root, MainViewModel main,
        PhotoLoadSession session, string directory, string workspace, List<string> lines)
    {
        foreach (string name in new[] { "orientation-6.jpg", "orientation-8.jpg" })
        {
            string path = Path.Combine(directory, name);
            if (!File.Exists(path)) throw new FileNotFoundException($"增强方向回归需要 {name}", path);
            await main.FolderVM.OpenImageAsync(new iOSFileRef(path));
            await WaitAsync(() => main.CurrentFile?.Name == name && session.Image != null, $"{name} 上屏");
            await UiDispatcher.InvokeAsync(() => root.Photo.FitToView());
            await SettleAsync(root);
            var original = session.Image ?? throw new InvalidOperationException($"{name} 展示图为空");
            string[] before;
            using (var frame = CaptureNavigationSrgb(root)) before = EnhancementAcceptance.CornerSignatures(frame);
            await UiDispatcher.InvokeAsync(() => { if (!session.IsEnhanced) session.ToggleEnhanced(); });
            await WaitAsync(() => session.EnhancedDisplay, $"{name} 增强图就绪");
            await SettleAsync(root);
            var enhanced = session.Image ?? throw new InvalidOperationException($"{name} 增强图为空");
            if (enhanced.Orientation != original.Orientation || enhanced.SourceWidth != original.SourceWidth || enhanced.SourceHeight != original.SourceHeight)
                throw new InvalidOperationException(
                    $"{name} 增强改写了照片几何：方向 {original.Orientation}→{enhanced.Orientation}、源 {original.SourceWidth}×{original.SourceHeight}→{enhanced.SourceWidth}×{enhanced.SourceHeight}");
            string[] after;
            using (var frame = CaptureNavigationSrgb(root)) after = EnhancementAcceptance.CornerSignatures(frame);
            if (!before.SequenceEqual(after))
                throw new InvalidOperationException($"{name} 增强后四角分类改变（画面被旋转/转置）：{string.Join(",", before)} → {string.Join(",", after)}");
            await UiDispatcher.InvokeAsync(() => { if (session.IsEnhanced) session.ToggleEnhanced(); });
            await WaitAsync(() => !session.IsEnhanced, $"{name} 增强关闭");
            lines.Add($"PASS 增强方向 {name}：方向 {enhanced.Orientation}、源 {enhanced.SourceWidth}×{enhanced.SourceHeight}，四角 {string.Join("/", after)} 与增强前一致");
        }

        // 8 位 P3 Full 档：生成 4000×3000（容器横向）的 EXIF 6 竖拍素材，放大到物理 100% 触发 Full 后再增强。
        string portrait = Path.Combine(workspace, "enhance-portrait-6.jpg");
        EnhancementAcceptance.CreateOrientationJpeg(portrait, 6, 4000, 3000);
        await main.FolderVM.OpenImageAsync(new iOSFileRef(portrait));
        await WaitAsync(() => main.CurrentFile?.Name == Path.GetFileName(portrait) && session.Image != null, "竖拍大素材上屏");
        await UiDispatcher.InvokeAsync(() => root.Photo.ZoomToPhysical(1.1));
        await WaitAsync(() => !session.IsProxy && session.Image is { Encoding: PixelEncoding.Rgba8 }, "竖拍素材 Full 8 位 P3");
        await SettleAsync(root);
        var fullOriginal = session.Image ?? throw new InvalidOperationException("竖拍素材展示图为空");
        string[] fullBefore;
        using (var frame = CaptureNavigationSrgb(root)) fullBefore = EnhancementAcceptance.CornerSignatures(frame);
        await UiDispatcher.InvokeAsync(() => { if (!session.IsEnhanced) session.ToggleEnhanced(); });
        await WaitAsync(() => session.EnhancedDisplay, "竖拍 Full 档增强图就绪");
        await SettleAsync(root);
        var fullEnhanced = session.Image ?? throw new InvalidOperationException("竖拍增强图为空");
        if (fullEnhanced.Encoding != PixelEncoding.Rgba8)
            throw new InvalidOperationException($"8 位 P3 Full 档增强后应保持 8 位展示编码，实际 {fullEnhanced.Encoding}");
        if (fullEnhanced.Orientation != 6 || fullEnhanced.SourceWidth != 4000 || fullEnhanced.SourceHeight != 3000)
            throw new InvalidOperationException(
                $"竖拍 Full 档增强改写了几何：方向 {fullEnhanced.Orientation}、源 {fullEnhanced.SourceWidth}×{fullEnhanced.SourceHeight}");
        string[] fullAfter;
        using (var frame = CaptureNavigationSrgb(root)) fullAfter = EnhancementAcceptance.CornerSignatures(frame);
        if (!fullBefore.SequenceEqual(fullAfter))
            throw new InvalidOperationException($"竖拍 Full 档增强后四角分类改变：{string.Join(",", fullBefore)} → {string.Join(",", fullAfter)}");
        await UiDispatcher.InvokeAsync(() => { if (session.IsEnhanced) session.ToggleEnhanced(); });
        await WaitAsync(() => !session.IsEnhanced, "竖拍 Full 档增强关闭");
        lines.Add($"PASS 增强方向（Full 8 位 P3）：方向 6、源 4000×3000 保持，四角 {string.Join("/", fullAfter)} 不变，编码 {fullEnhanced.Encoding}");
    }

    /// <summary>抓取导航图尺寸的 GPU 帧并归一为 BGRA8 sRGB（照片铺满小图，四角就是照片四角）。</summary>
    /// <param name="root">原生根控制器。</param>
    /// <returns>由调用方释放的 BGRA8 导航帧。</returns>
    private static DecodedImage CaptureNavigationSrgb(iOSNativeRootViewController root)
    {
        using var linear = root.Photo.CaptureLinearFrame(navigation: true);
        return ColorEngine.ToSrgb8(linear);
    }

    /// <summary>核对两向瓦片几何：交叉轴 = 边长 + 20，瓦片正方形、间距 8、内边距 10、主轴起点含安全区让位。</summary>
    /// <param name="root">原生根控制器。</param>
    /// <param name="row">是否行布局。</param>
    /// <param name="lines">报告行。</param>
    private static void VerifyGeometry(iOSNativeRootViewController root, bool row, List<string> lines)
    {
        var tiles = root.AnalysisPane.TileViews;
        if (tiles.Count < 6) throw new InvalidOperationException($"分析栏瓦片数不足：{tiles.Count}");
        var bounds = root.View!.Bounds;
        var safe = root.View.SafeAreaInsets;
        double available = row
            ? (double)bounds.Width - (double)safe.Left - (double)safe.Right
            : (double)bounds.Height - (double)safe.Top - (double)safe.Bottom;
        double expected = AnalysisPanelGeometry.TileSize(available, tiles.Count);
        var panes = root.Panes;
        double cross = row ? (double)panes.Analysis.Height : (double)panes.Analysis.Width;
        if (Math.Abs(cross - (expected + AnalysisPanelGeometry.Padding * 2)) > 1.5)
            throw new InvalidOperationException($"{(row ? "行" : "列")}布局分析栏交叉轴 {cross} 与边长 {expected} + 20 不符");
        double startInset = row ? (double)safe.Left : (double)safe.Top;
        for (int index = 0; index < tiles.Count; index++)
        {
            var frame = tiles[index].Frame;
            if (Math.Abs((double)frame.Width - expected) > 1.5 || Math.Abs((double)frame.Height - expected) > 1.5)
                throw new InvalidOperationException($"瓦片 {index} 不是 {expected} 的正方形：{frame}");
            double offset = row ? (double)frame.X : (double)frame.Y;
            double expectOffset = startInset + AnalysisPanelGeometry.TileOffset(index, expected);
            if (Math.Abs(offset - expectOffset) > 1.5)
                throw new InvalidOperationException($"瓦片 {index} 主轴起点 {offset} 与 {expectOffset} 不符");
            double crossOffset = row ? (double)frame.Y : (double)frame.X;
            if (Math.Abs(crossOffset - AnalysisPanelGeometry.Padding) > 1.5)
                throw new InvalidOperationException($"瓦片 {index} 交叉轴内边距 {crossOffset} 应为 10");
        }
        lines.Add($"PASS 几何 {(row ? "row" : "col")}：{tiles.Count} 张正方形瓦片 side={expected} gap=8 padding=10 cross={cross}");
    }

    /// <summary>验收环境自愈：确保 JPEG 格式启用（历史验收会话被强杀可能遗留全格式关闭）。</summary>
    private static void EnsureJpegEnabled(MainViewModel main)
    {
        foreach (var format in main.Settings.FileFormats)
            if (format.DisplayName == "JPG") format.IsEnabled = true;
    }

    /// <summary>收尾时把设置同步落盘，避免验收进程被强杀导致回写丢失。</summary>
    private static void FlushSettings(MainViewModel main)
        => PhotoViewer.Core.Settings.SettingsService.Instance.SaveAsync(main.Settings.CreateSnapshot()).GetAwaiter().GetResult();

    /// <summary>F16 代理直方图：直接用瓦片共用的 <see cref="HistogramRenderer"/> 渲染，验证线性表面可被接受。</summary>
    /// <param name="source">F16 代理展示图。</param>
    /// <param name="lines">报告行。</param>
    private static void VerifyProxyHistogram(DecodedImage source, List<string> lines)
    {
        if (!source.TryAcquire()) throw new InvalidOperationException("F16 代理无法取租约");
        DecodedImage? histogram = null;
        try { histogram = HistogramRenderer.Render(source, 256, 256); }
        finally { source.Release(); }
        if (histogram == null) throw new InvalidOperationException("F16 代理直方图渲染为 null");
        try
        {
            if (histogram.Width != 256 || histogram.Height != 256)
                throw new InvalidOperationException($"F16 代理直方图尺寸异常：{histogram.Width}×{histogram.Height}");
            lines.Add($"PASS F16 代理直方图：HistogramRenderer({source.Encoding}) → {histogram.Width}×{histogram.Height}");
        }
        finally { histogram.Dispose(); }
    }

    /// <summary>用共享显示裁剪取中心 1:1 方块，并把中心像素与主图 GPU 帧读回的中心像素对比。</summary>
    /// <param name="root">原生根控制器。</param>
    /// <param name="source">当前展示图（F16 线性或 8 位 P3）。</param>
    /// <param name="label">编码标签（报告与报错用）。</param>
    /// <param name="lines">报告行。</param>
    private static void VerifyCropPixel(iOSNativeRootViewController root, DecodedImage source, string label, List<string> lines)
    {
        if (!source.TryAcquire()) throw new InvalidOperationException($"{label} 展示图无法取租约");
        DecodedImage? crop = null;
        try { crop = DisplayCrop.CropSquare(source, source.Orientation, .5, .5, 128); }
        finally { source.Release(); }
        if (crop == null) throw new InvalidOperationException($"{label} 细节裁剪失败");
        try
        {
            using var frame = root.Photo.CaptureLinearFrame(navigation: true);
            using var frameSrgb = ColorEngine.ToSrgb8(frame);
            var cropPixel = CenterPixel(crop);
            var framePixel = CenterPixel(frameSrgb);
            int diff = Math.Max(Math.Abs(cropPixel.R - framePixel.R),
                Math.Max(Math.Abs(cropPixel.G - framePixel.G), Math.Abs(cropPixel.B - framePixel.B)));
            if (diff > PixelTolerance)
                throw new InvalidOperationException($"{label} 细节裁剪中心 {cropPixel} 与主图帧 {framePixel} 相差 {diff}（容差 {PixelTolerance}）");
            lines.Add($"PASS 裁剪像素 {label}：crop={cropPixel} frame={framePixel} Δ={diff}（{crop.Width}×{crop.Height}）");
        }
        finally { crop.Dispose(); }
    }

    /// <summary>抓取主图当前 GPU 帧并归一为 BGRA8 sRGB，供增强前后逐像素比较。</summary>
    /// <param name="root">原生根控制器。</param>
    /// <returns>由调用方释放的 BGRA8 帧。</returns>
    private static DecodedImage CaptureSrgbFrame(iOSNativeRootViewController root)
    {
        using var linear = root.Photo.CaptureLinearFrame();
        return ColorEngine.ToSrgb8(linear);
    }

    /// <summary>读取 BGRA8 图像的中心像素（R/G/B）。</summary>
    /// <param name="image">BGRA32 图像。</param>
    /// <returns>中心像素。</returns>
    private static (int R, int G, int B) CenterPixel(DecodedImage image)
    {
        if (image.Encoding != PixelEncoding.Bgra32) throw new InvalidOperationException($"像素读取需要 BGRA8：{image.Encoding}");
        var pixels = image.Pixels;
        int x = image.Width / 2, y = image.Height / 2;
        int offset = y * image.Stride + x * 4;
        return (pixels[offset + 2], pixels[offset + 1], pixels[offset]);
    }

    /// <summary>
    /// 未提取 → 真实索引：复制一份唯一文件名的素材（指纹必然未入库）→ 打开并确认四张诊断瓦片为"未提取"
    /// → 跑一次真实批量索引 → 断言瓦片自行出图，且期间没有切换当前照片。返回索引耗时描述。
    /// </summary>
    /// <param name="root">原生根控制器。</param>
    /// <param name="main">共享主 VM。</param>
    /// <param name="directory">夹具目录。</param>
    /// <param name="lines">报告行。</param>
    /// <returns>索引耗时（秒，含每张）。</returns>
    private static async Task<string> VerifyIndexRefreshAsync(iOSNativeRootViewController root, MainViewModel main, string directory, List<string> lines)
    {
        string workspace = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "analysis-fixtures");
        Directory.CreateDirectory(workspace);
        string smallest = Directory.GetFiles(directory, "*.jpg")
            .Where(path => !string.Equals(Path.GetFileName(path), "color-p3-large.jpg", StringComparison.OrdinalIgnoreCase))
            .OrderBy(path => new FileInfo(path).Length)
            .FirstOrDefault() ?? throw new InvalidOperationException("分析验收缺少用于索引探针的小图夹具");
        string probe = Path.Combine(workspace, $"analysis-probe-{DateTime.UtcNow.Ticks}.jpg");
        File.Copy(smallest, probe, true);
        try
        {
            await main.FolderVM.OpenImageAsync(new iOSFileRef(probe));
            await WaitAsync(() => main.CurrentFile?.Name == Path.GetFileName(probe), "探针图成为当前照片");
            var presenter = root.AnalysisPane.Presenter;
            await WaitAsync(() => presenter.PcaTile.Placeholder == "未提取" && presenter.SharpnessTile.Placeholder == "未提取",
                "探针图初始为未提取");
            var indexer = new FolderFeatureIndexer();
            var stopwatch = Stopwatch.StartNew();
            await indexer.RunAsync([main.CurrentFile!]);
            stopwatch.Stop();
            await WaitAsync(() => presenter.PcaTile.Source != null && presenter.SharpnessTile.Source != null
                && presenter.CosineTile.Source != null && presenter.ShakeTile.ShakeField != null, "入库后诊断瓦片自行出图");
            if (main.CurrentFile?.Name != Path.GetFileName(probe))
                throw new InvalidOperationException("回归期间当前照片被切换，无法证明是入库信号触发的刷新");
            string? error = indexer.LastError;
            if (!string.IsNullOrEmpty(error)) lines.Add($"NOTE 索引错误文本：{error}");
            return $"{stopwatch.Elapsed.TotalSeconds:0.0}s（1 张，{Path.GetFileName(smallest)}）";
        }
        finally
        {
            File.Delete(probe);
        }
    }

    /// <summary>收尾：恢复进入检查时的设置快照、布局与当前照片。</summary>
    /// <param name="root">原生根控制器。</param>
    /// <param name="main">共享主 VM。</param>
    /// <param name="snapshot">设置快照。</param>
    /// <param name="analysis">进入时的分析栏可见性。</param>
    /// <param name="film">进入时的胶片栏可见性。</param>
    /// <param name="control">进入时的控制栏可见性。</param>
    /// <param name="similarity">进入时的相似面板状态。</param>
    /// <param name="analysisDisplay">进入时的分析栏命令显隐。</param>
    /// <param name="enhanceDisplay">进入时的增强预览命令显隐。</param>
    /// <param name="originalFile">进入时的当前照片。</param>
    private static async Task RestoreAsync(iOSNativeRootViewController root, MainViewModel main, PhotoViewer.Core.Settings.SettingsModel snapshot,
        bool analysis, bool film, bool control, bool similarity, bool analysisDisplay, bool enhanceDisplay, ImageFile? originalFile)
    {
        await UiDispatcher.InvokeAsync(() =>
        {
            main.Settings.ApplySnapshot(snapshot);
            main.Settings.Hotkeys.First(hotkey => hotkey.Command == "ToggleAnalysisView").IsDisplay = analysisDisplay;
            main.Settings.Hotkeys.First(hotkey => hotkey.Command == "ToggleEnhance").IsDisplay = enhanceDisplay;
            main.IsAnalysisViewVisible = analysis;
            main.IsThumbnailViewVisible = film;
            main.IsControlViewVisible = control;
            main.FileVM.FilterBar.IsSimilarityPanelOpen = similarity;
            if (originalFile != null) main.CurrentFile = originalFile;
        });
        await SettleAsync(root);
    }

    /// <summary>选中指定路径的照片并等待会话上屏。</summary>
    /// <param name="main">共享主 VM。</param>
    /// <param name="path">目标路径。</param>
    private static async Task SelectFileAsync(MainViewModel main, string path)
    {
        string name = Path.GetFileName(path);
        await UiDispatcher.InvokeAsync(() =>
        {
            var target = main.FolderVM.AllFiles.FirstOrDefault(file => string.Equals(file.Name, name, StringComparison.Ordinal));
            if (target == null) throw new InvalidOperationException($"夹具目录中找不到 {name}");
            main.CurrentFile = target;
        });
        await WaitAsync(() => main.CurrentFile?.Name == name, $"{name} 成为当前照片");
    }

    /// <summary>展平菜单为按显示顺序的 action 列表（含内联分组）。</summary>
    /// <param name="menu">菜单。</param>
    /// <returns>action 列表。</returns>
    private static List<(string Id, string Title)> Flatten(UIMenu menu)
    {
        var result = new List<(string, string)>();
        foreach (var child in menu.Children)
        {
            if (child is UIMenu submenu)
            {
                result.AddRange(Flatten(submenu));
                continue;
            }
            if (child is UIAction action) result.Add((action.Identifier ?? "", action.Title));
        }
        return result;
    }

    private static async Task<int> VerifyDetailActivityAsync(iOSNativeRootViewController root, MainViewModel main,
        PhotoLoadSession session, string expectedFileKey, int expectedCount, Func<string?> displayedFileKey,
        Action<string, iOSDetailPreview?> trace)
    {
        bool clearStateProved = false;
        string? previousWait = null;
        for (int attempt = 0; attempt < 2400; attempt++)
        {
            int result = await UiDispatcher.InvokeAsync(() =>
            {
                root.View!.LayoutIfNeeded();
                var pane = root.AnalysisPane;
                var previews = pane.DetailPreviews;
                bool ready = main.CurrentFile?.File.PathKey == expectedFileKey && displayedFileKey() == expectedFileKey
                    && session.Image != null && !session.IsProxy && ReferenceEquals(root.Photo.DisplayedImage, session.Image)
                    && root.Photo.PresentationReady && main.IsAnalysisViewVisible && !pane.Hidden && previews.Count == expectedCount
                    && previews.All(value => (nint)value.Handle != 0 && ReferenceEquals(value.Superview, pane)
                        && value.Window != null && value.Frame.Width > 0 && value.Frame.Height > 0 && value.HasContent);
                if (!ready)
                {
                    string state = DescribeDetailProbe(root, main, session, displayedFileKey(), previews.LastOrDefault());
                    if (state != previousWait) { trace("waiting-ready", previews.LastOrDefault()); previousWait = state; }
                    return 0;
                }
                if (!clearStateProved)
                {
                    bool observed = false;
                    void OnReload()
                    {
                        if (observed || session.Image != null) return;
                        observed = true;
                        var current = pane.DetailPreviews;
                        if (current.Count != expectedCount) throw new InvalidOperationException("清空态未保留EXIF细节瓦片，需重新核对旧前置");
                        var target = current[^1];
                        trace("controlled-clear-before-down", target);
                        var point = new CGPoint(target.Frame.GetMidX(), target.Frame.GetMidY());
                        pane.RouteDetailTouch(101, point, UITouchPhase.Began);
                        trace("controlled-clear-after-down", target);
                        if (!target.IsHighlighted || root.DetailHighlightVisible || root.Photo.PresentationReady)
                            throw new InvalidOperationException("无展示图时的细节活动状态不符合预期");
                        pane.RouteDetailTouch(101, point, UITouchPhase.Ended);
                    }
                    session.Changed += OnReload;
                    try { session.SetFile(main.CurrentFile!.File); }
                    finally { session.Changed -= OnReload; }
                    if (!observed) throw new InvalidOperationException("未观察到真实SetFile清空态");
                    clearStateProved = true;
                    return 0;
                }
                root.Photo.FitToView();
                var preview = pane.DetailPreviews[^1];
                trace("atomic-ready-before-down", preview);
                var location = new CGPoint(preview.Frame.GetMidX(), preview.Frame.GetMidY());
                pane.RouteDetailTouch(1, location, UITouchPhase.Began);
                trace("atomic-ready-after-down", preview);
                if (!preview.IsHighlighted || !root.DetailHighlightVisible) throw new InvalidOperationException("已就绪视图Down未立即点亮：" + DescribeDetailProbe(root, main, session, displayedFileKey(), preview));
                if (pane.DetailPreviews.Count == 2)
                {
                    var other = pane.DetailPreviews[0];
                    pane.RouteDetailTouch(1, new CGPoint(other.Frame.GetMidX(), other.Frame.GetMidY()), UITouchPhase.Moved);
                    if (!other.IsHighlighted || preview.IsHighlighted || pane.DetailPreviews.Count(value => value.IsHighlighted) != 1)
                        throw new InvalidOperationException("滑入另一细节瓦片未切换唯一目标");
                    pane.RouteDetailTouch(1, location, UITouchPhase.Moved);
                }
                pane.RouteDetailTouch(2, location, UITouchPhase.Began);
                pane.RouteDetailTouch(1, location, UITouchPhase.Cancelled);
                if (!preview.IsHighlighted) throw new InvalidOperationException("旧Cancel清除了新目标");
                pane.RouteDetailTouch(2, CGPoint.Empty, UITouchPhase.Moved);
                if (preview.IsHighlighted || root.DetailHighlightVisible) throw new InvalidOperationException("移出未清除");
                pane.RouteDetailTouch(2, location, UITouchPhase.Moved);
                pane.RouteDetailTouch(2, location, UITouchPhase.Ended);
                if (preview.IsHighlighted || root.DetailHighlightVisible) throw new InvalidOperationException("Up未立即清除");
                pane.SetDetailActivity(preview, true, -3);
                pane.SetDetailActivity(preview, false, -3);
                if (preview.IsHighlighted) throw new InvalidOperationException("Hover离开未清除");
                pane.RouteDetailTouch(4, location, UITouchPhase.Began);
                var hoverTarget = pane.DetailPreviews[0];
                pane.SetDetailActivity(hoverTarget, true, -5);
                pane.SetDetailActivity(preview, false, -3);
                if (!preview.IsHighlighted || pane.DetailPreviews.Any(value => !ReferenceEquals(value, preview) && value.IsHighlighted))
                    throw new InvalidOperationException("触控按住时hover抢走了活动目标");
                pane.RouteDetailTouch(4, location, UITouchPhase.Ended);
                if (pane.DetailPreviews.Any(value => value.IsHighlighted) || root.DetailHighlightVisible)
                    throw new InvalidOperationException("触控抬起后因hover混合事件残留绿框");
                pane.SetDetailActivity(hoverTarget, true, -6);
                if (!hoverTarget.IsHighlighted) throw new InvalidOperationException("触控结束后新的hover无法恢复");
                NSNotificationCenter.DefaultCenter.PostNotificationName(UIApplication.WillResignActiveNotification, null);
                if (pane.DetailPreviews.Any(value => value.IsHighlighted) || root.DetailHighlightVisible)
                    throw new InvalidOperationException("失活通知未清除");
                return previews.Count;
            });
            if (result > 0) return result;
            await Task.Delay(25);
        }
        throw new InvalidOperationException("等待当前细节与主图就绪超时：" + previousWait);
    }

    private static string DescribeDetailProbe(iOSNativeRootViewController root, MainViewModel main, PhotoLoadSession session,
        string? displayedFileKey, iOSDetailPreview? preview = null)
    {
        static string Id(object? value) => value == null ? "null" : System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(value).ToString();
        var pane = root.AnalysisPane;
        var image = session.Image;
        bool live = preview != null && (nint)preview.Handle != 0;
        return $"current={main.CurrentFile?.File.PathKey} signal={displayedFileKey ?? "none"} image={Id(image)} "
            + $"imageSize={image?.Width}x{image?.Height} proxy={session.IsProxy} error={session.Error} texture={Id(root.Photo.DisplayedImage)} ready={root.Photo.PresentationReady} "
            + $"photo={root.Photo.Bounds} display={root.Photo.DisplayWidth}x{root.Photo.DisplayHeight} analysis={main.IsAnalysisViewVisible}/{!pane.Hidden} count={pane.DetailPreviews.Count} "
            + $"preview={Id(preview)} currentPreview={pane.DetailPreviews.Any(value => ReferenceEquals(value, preview))} live={live} "
            + $"attached={live && ReferenceEquals(preview!.Superview, pane) && preview.Window != null} frame={(live ? preview!.Frame.ToString() : "none")} "
            + $"content={live && preview!.HasContent} tileHighlight={live && preview!.IsHighlighted} mainHighlight={root.DetailHighlightVisible}";
    }

    private static async Task WaitAsync(Func<bool> condition, string label)
    {
        for (int attempt = 0; attempt < 2400; attempt++)
        {
            if (condition()) return;
            await Task.Delay(25);
        }
        throw new InvalidOperationException($"等待超时：{label}");
    }

    /// <summary>等待 UIKit 一轮布局与共享 VM 通知沉淀。</summary>
    /// <param name="root">原生根控制器。</param>
    private static async Task SettleAsync(iOSNativeRootViewController root)
    {
        await Task.Delay(Settle);
        await UiDispatcher.InvokeAsync(() => root.View!.LayoutIfNeeded());
        await Task.Delay(Settle);
    }

    /// <summary>尺寸格式化（报告用）。</summary>
    /// <param name="size">尺寸。</param>
    /// <returns>文本。</returns>
    private static string Size(CGSize size) => $"{size.Width:0}×{size.Height:0}";

    /// <summary>安全区格式化（报告用）。</summary>
    /// <param name="insets">安全区。</param>
    /// <returns>文本。</returns>
    private static string Insets(UIEdgeInsets insets) => $"T{insets.Top:0.##} L{insets.Left:0.##} B{insets.Bottom:0.##} R{insets.Right:0.##}";
}
