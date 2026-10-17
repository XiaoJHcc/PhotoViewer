using CoreGraphics;
using Foundation;
using PhotoViewer.Apple;
using PhotoViewer.Core.Image;
using PhotoViewer.Core.Platform;
using PhotoViewer.iOS.Core;
using PhotoViewer.ViewModels.Main;
using PhotoViewer.ViewModels.Settings;
using System.Diagnostics;
using UIKit;

namespace PhotoViewer.iOS.Native;

/// <summary>
/// 主图缩放/平移验收专用（非产品行为，测试钩子）：<c>--pv-native-gesture-check &lt;目录&gt;</c>。
///
/// <para>与旧检查的区别：不再只"调视口 API 看视口对不对"，而是按 UIKit 捏合/平移的真实序列驱动
/// <see cref="UIScrollView"/>（SetZoomScale 后按 UIKit 语义调整 contentOffset、再 SetContentOffset 平移），
/// 断言**屏幕上**的图像点（由视口映射给出）确实留在手指下，并覆盖真实使用中必然发生的后续事件：
/// 捏合触发的 Full 热替换、布局/旋转、双击、适应态轻扫、放大态拖动、增强开关，以及导航图的可见性与几何。</para>
///
/// 素材要求：目录内必须有 <c>color-p3-large.jpg</c>（长边 ≥ 4000，制造真实代理档与 100% 物理 Full），
/// 且位于 App 沙盒内（不写用户照片 / TestCase）。报告写 Documents/PhotoViewer/native-gesture-checks.txt，
/// 打印 <c>[NativeSmoke] PASS gesture (n)</c> / FAIL。
/// </summary>
internal static class iOSNativeGestureCheck
{
    /// <summary>验收开关。</summary>
    private const string Switch = "--pv-native-gesture-check";
    /// <summary>滚动/动画沉淀等待。</summary>
    private static readonly TimeSpan Settle = TimeSpan.FromMilliseconds(120);
    /// <summary>图像点在屏幕上的允许漂移（点）：捏合锚点与热替换都必须保持手指下的图像点不动。</summary>
    private const double AnchorTolerance = 1.0;
    /// <summary>内容尺寸与 zoomScale 的口径容差（点）。</summary>
    private const double SizeTolerance = 1.0;

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

    /// <summary>核对主流程：等主屏布局 → 打开素材 → 逐项驱动真实滚动路径 → 落盘并打印结论。</summary>
    /// <param name="root">原生根控制器。</param>
    /// <param name="session">原生展示会话。</param>
    /// <param name="directory">自有夹具目录（绝对路径）。</param>
    private static async Task RunAsync(iOSNativeRootViewController root, PhotoLoadSession session, string directory)
    {
        var lines = new List<string>();
        string output = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "PhotoViewer");
        Directory.CreateDirectory(output);
        string report = Path.Combine(output, "native-gesture-checks.txt");
        try
        {
            var main = iOSNativeShell.Main ?? throw new InvalidOperationException("原生壳主 VM 未就绪");
            if (!Path.GetFullPath(directory).StartsWith(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), StringComparison.Ordinal))
                throw new InvalidOperationException($"手势验收只允许使用 App 沙盒内的自有夹具目录：{directory}");
            string large = Path.Combine(directory, "color-p3-large.jpg");
            if (!File.Exists(large)) throw new FileNotFoundException($"手势验收需要 color-p3-large.jpg（真实代理档与 Full）", large);
            await WaitAsync(() => root.View!.Bounds.Width > 100 && root.Photo.Bounds.Width > 100, "主屏布局");
            var snapshot = main.Settings.CreateSnapshot();
            var originalFile = main.CurrentFile;
            try
            {
                await UiDispatcher.InvokeAsync(() =>
                {
                    main.Settings.ShowRating = true;
                    main.Settings.ShowZoomIndicator = true;
                    main.Settings.LayoutMode = LayoutMode.Row;
                    main.IsThumbnailViewVisible = true;
                    main.IsControlViewVisible = true;
                    main.IsAnalysisViewVisible = false;
                    main.FileVM.FilterBar.IsSimilarityPanelOpen = false;
                    EnsureJpegEnabled(main);
                });
                await main.FolderVM.OpenImageAsync(new iOSFileRef(large));
                await WaitAsync(() => main.CurrentFile?.Name == Path.GetFileName(large) && session.Image != null, "大色卡上屏");
                await SettleAsync(root);
                var proxy = session.Image ?? throw new InvalidOperationException("大色卡未上屏");
                if (!session.IsProxy) throw new InvalidOperationException($"大色卡未走代理档（{proxy.Width}×{proxy.Height}，长边需 ≥ 4000）");
                lines.Add($"PASS 素材：{proxy.Width}×{proxy.Height} {proxy.Encoding} 代理档，视口 {Size(root.Photo.Bounds.Size)}");

                await UiDispatcher.InvokeAsync(() => root.Photo.FitToView());
                await SettleAsync(root);
                VerifyNavigationIndicator(root, visible: false, lines);

                // 一、真实捏合路径：SetZoomScale + 按 UIKit 语义调整 contentOffset，再逐帧断言屏幕锚点。
                var pinchCenter = new CGPoint((double)root.Photo.Bounds.Width * .68, (double)root.Photo.Bounds.Height * .62);
                var anchorBefore = await UiDispatcher.InvokeAsync(() => root.Photo.ImagePointAt(pinchCenter));
                // 目标 zoom 必须越过大屏/小屏布局下各自的"物理 100%"门槛，否则不会触发 Full 热替换（横屏真机视口更扁）。
                double zoomTarget = await UiDispatcher.InvokeAsync(() =>
                    Math.Max(3.2, 1.25 / Math.Max(1e-6, root.Photo.PhysicalScale)));
                double zoomAfterPinch = await PinchAsync(root, pinchCenter, zoomTarget);
                var anchorAfter = await UiDispatcher.InvokeAsync(() => root.Photo.ImagePointAt(pinchCenter));
                AssertImagePoint(anchorBefore, anchorAfter, AnchorTolerance, root.Photo, "捏合后手指下的图像点");
                await AssertScrollInvariantsAsync(root, "捏合后");
                double centerDrift = await UiDispatcher.InvokeAsync(() => Distance(root.Photo.ViewPoint(anchorBefore.U, anchorBefore.V), pinchCenter));
                if (centerDrift > AnchorTolerance)
                    throw new InvalidOperationException($"捏合后锚点图像点漂到 {centerDrift:0.##} 点外（不是标准锚点缩放）");
                var (panX, panY) = await UiDispatcher.InvokeAsync(() => (root.Photo.Viewport.PanX, root.Photo.Viewport.PanY));
                // 左上角回退的回归判据：捏在右下区域后平移必须为负（照片向右下延伸），且中心不在内容原点。
                if (panX >= 0 || panY >= 0)
                    throw new InvalidOperationException($"捏合后被拽向内容原点（pan=({panX:0.##},{panY:0.##})），这是旧实现的左上角回退故障");
                lines.Add($"PASS 真实捏合：zoomScale {zoomAfterPinch:0.###}，手指下图像点保持 ({anchorBefore.U:0.####},{anchorBefore.V:0.####})，pan=({panX:0.##},{panY:0.##})，contentSize/offset 合规");

                // 二、捏合触发的 Full 热替换：滚动状态与屏幕锚点都不得变化。
                await WaitAsync(() => !session.IsProxy && session.Image is { Encoding: PixelEncoding.Rgba8 }, "捏合后 Full 8 位 P3 热替换");
                await SettleAsync(root);
                var anchorAfterFull = await UiDispatcher.InvokeAsync(() => root.Photo.ImagePointAt(pinchCenter));
                AssertImagePoint(anchorBefore, anchorAfterFull, AnchorTolerance, root.Photo, "Full 热替换后手指下的图像点");
                await AssertScrollInvariantsAsync(root, "Full 热替换后");
                if (Math.Abs((double)root.Photo.ScrollView.ZoomScale - zoomAfterPinch) > .01)
                    throw new InvalidOperationException($"Full 热替换改变了 zoomScale：{zoomAfterPinch:0.###} → {(double)root.Photo.ScrollView.ZoomScale:0.###}");
                lines.Add("PASS Full 热替换：zoomScale/contentOffset/屏幕锚点全部不变（ImageSize 恒定，滚动状态不参与）");

                // 三、导航图：非适应态出现、几何与共享 NavigationViewport 一致、百分比为物理倍率。
                await UiDispatcher.InvokeAsync(() => root.View!.LayoutIfNeeded());
                var navigation = await UiDispatcher.InvokeAsync(() =>
                {
                    var size = root.Photo.NavigationSize;
                    var viewport = root.Photo.NavigationViewport;
                    var label = root.ZoomLabel.Text ?? string.Empty;
                    var imageSize = root.NavigationImageView.Frame.Size;
                    var frame = root.NavigationFrameView.Frame;
                    return (Host: !root.NavigationHost.Hidden, Size: size, Viewport: viewport, Label: label,
                        ImageSize: new CGSize(imageSize.Width, imageSize.Height), Frame: new CGRect(frame.X, frame.Y, frame.Width, frame.Height),
                        Physical: root.Photo.PhysicalScale);
                });
                if (!navigation.Host) throw new InvalidOperationException("放大后导航图未显示");
                if (Math.Abs((double)navigation.ImageSize.Width - (double)navigation.Size.Width) > SizeTolerance
                    || Math.Abs((double)navigation.ImageSize.Height - (double)navigation.Size.Height) > SizeTolerance)
                    throw new InvalidOperationException($"导航图显示尺寸 {navigation.ImageSize} 与共享 NavigationSize {navigation.Size} 不符");
                if (Math.Abs((double)navigation.Frame.X - (8 + (double)navigation.Viewport.X)) > SizeTolerance
                    || Math.Abs((double)navigation.Frame.Y - (8 + (double)navigation.Viewport.Y)) > SizeTolerance
                    || Math.Abs((double)navigation.Frame.Width - (double)navigation.Viewport.Width) > SizeTolerance
                    || Math.Abs((double)navigation.Frame.Height - (double)navigation.Viewport.Height) > SizeTolerance)
                    throw new InvalidOperationException($"导航图可见框 {navigation.Frame} 与共享 NavigationViewport {navigation.Viewport}（+8 内边距）不符");
                string expectedLabel = $"{navigation.Physical * 100:0.##}%";
                if (!string.Equals(navigation.Label, expectedLabel, StringComparison.Ordinal))
                    throw new InvalidOperationException($"缩放百分比标签 '{navigation.Label}' 应为 '{expectedLabel}'");
                if (root.NavigationImageView.Image == null) throw new InvalidOperationException("导航图小图为空（GPU 小目标渲染未出图）");
                lines.Add($"PASS 导航图：可见，{navigation.Size.Width:0}×{navigation.Size.Height:0} 点，可见框 ({navigation.Viewport.X:0.#},{navigation.Viewport.Y:0.#},{navigation.Viewport.Width:0.#},{navigation.Viewport.Height:0.#})，标签 {navigation.Label}");

                // 四、拖动平移：视口跟随、导航框跟随、可见框随平移移动。
                var beforePan = navigation.Viewport;
                await PanByAsync(root, new CGPoint(-40, -30));
                await SettleAsync(root);
                var (panAfterX, panAfterY) = await UiDispatcher.InvokeAsync(() => (root.Photo.Viewport.PanX, root.Photo.Viewport.PanY));
                if (Math.Abs(panAfterX - panX) < 10 || Math.Abs(panAfterY - panY) < 8)
                    throw new InvalidOperationException($"拖动 contentOffset 后视口平移未跟随：({panX:0.##},{panY:0.##}) → ({panAfterX:0.##},{panAfterY:0.##})");
                var movedViewport = await UiDispatcher.InvokeAsync(() => root.Photo.NavigationViewport);
                if (Math.Abs((double)movedViewport.X - (double)beforePan.X) < 1 && Math.Abs((double)movedViewport.Y - (double)beforePan.Y) < 1)
                    throw new InvalidOperationException("平移后导航图可见框没有移动");
                await AssertScrollInvariantsAsync(root, "平移后");
                lines.Add($"PASS 拖动平移：pan=({panAfterX:0.##},{panAfterY:0.##})，导航框随动到 ({movedViewport.X:0.#},{movedViewport.Y:0.#})");

                // 五、导航图拖动定位：把某归一化图像点移到视口中心（触屏增强，Mac 无此交互）。
                await UiDispatcher.InvokeAsync(() => root.Photo.CenterOnNormalizedPoint(.6, .55));
                await SettleAsync(root);
                var centered = await UiDispatcher.InvokeAsync(() =>
                {
                    var viewport = root.Photo.Viewport;
                    return (U: .5 - viewport.PanX / (viewport.ImageWidth * viewport.Scale), V: .5 - viewport.PanY / (viewport.ImageHeight * viewport.Scale));
                });
                if (Math.Abs(centered.U - .6) > .05 || Math.Abs(centered.V - .55) > .05)
                    throw new InvalidOperationException($"导航图定位未把 (.6,.55) 移到中心：实际 ({centered.U:0.###},{centered.V:0.###})");
                lines.Add($"PASS 导航图定位：(.6,.55) → 视口中心 ({centered.U:0.###},{centered.V:0.###})");

                // 六、布局/旋转：同一图像点留在中心、相对缩放不变；恢复后仍可继续交互。
                var layout = await UiDispatcher.InvokeAsync(() =>
                {
                    var bounds = root.Photo.Bounds;
                    var center = root.Photo.ImagePointAt(new CGPoint((double)bounds.Width / 2, (double)bounds.Height / 2));
                    double zoom = (double)root.Photo.ScrollView.ZoomScale;
                    root.Photo.Frame = new CGRect(0, 0, Math.Max(200, (double)bounds.Width * .72), Math.Max(200, (double)bounds.Height * .8));
                    root.Photo.LayoutSubviews();
                    return (Center: center, Zoom: zoom, Width: (double)bounds.Width, Height: (double)bounds.Height);
                });
                await SettleAsync(root);
                var afterLayout = await UiDispatcher.InvokeAsync(() =>
                {
                    var bounds = root.Photo.Bounds;
                    return (Center: root.Photo.ImagePointAt(new CGPoint((double)bounds.Width / 2, (double)bounds.Height / 2)),
                        Zoom: (double)root.Photo.ScrollView.ZoomScale);
                });
                AssertImagePoint(layout.Center, afterLayout.Center, AnchorTolerance, root.Photo, "布局变化后视口中心的图像点");
                if (Math.Abs(afterLayout.Zoom - layout.Zoom) > .02)
                    throw new InvalidOperationException($"布局变化后相对缩放改变：{layout.Zoom:0.###} → {afterLayout.Zoom:0.###}");
                await UiDispatcher.InvokeAsync(() =>
                {
                    root.Photo.Frame = new CGRect(0, 0, layout.Width, layout.Height);
                    root.Photo.LayoutSubviews();
                    root.View!.SetNeedsLayout();
                    root.View.LayoutIfNeeded();
                });
                await SettleAsync(root);
                await AssertScrollInvariantsAsync(root, "恢复布局后");
                lines.Add($"PASS 布局变化：zoom {layout.Zoom:0.###} 保持、中心图像点 ({afterLayout.Center.U:0.####},{afterLayout.Center.V:0.####}) 不动，恢复后 contentSize/offset 合规");

                // 七、双击：适应 → 以触点物理 100%，触点下的图像点保持；再双击回适应并隐藏导航图。
                await UiDispatcher.InvokeAsync(() => root.Photo.FitToView());
                await SettleAsync(root);
                var tapPoint = new CGPoint((double)root.Photo.Bounds.Width * .62, (double)root.Photo.Bounds.Height * .58);
                var tapAnchor = await UiDispatcher.InvokeAsync(() => root.Photo.ImagePointAt(tapPoint));
                await UiDispatcher.InvokeAsync(() => root.Photo.DoubleTapAt(tapPoint));
                await WaitAsync(() => Math.Abs(root.Photo.PhysicalScale - 1) < .03, "双击放大到物理 100%");
                await SettleAsync(root);
                var tapAnchorAfter = await UiDispatcher.InvokeAsync(() => root.Photo.ImagePointAt(tapPoint));
                AssertImagePoint(tapAnchor, tapAnchorAfter, 2.5, root.Photo, "双击后触点下的图像点");
                await AssertScrollInvariantsAsync(root, "双击后");
                await UiDispatcher.InvokeAsync(() => root.Photo.DoubleTapAt(tapPoint));
                await WaitAsync(() => root.Photo.IsFit, "再次双击回到适应态");
                await SettleAsync(root);
                VerifyNavigationIndicator(root, visible: false, lines);
                lines.Add($"PASS 双击：物理 100% 锚点保持 ({tapAnchor.U:0.####},{tapAnchor.V:0.####})，回适应后导航图隐藏");

                // 八、轻扫翻页只在适应态生效；放大态拖动仍是平移。
                bool swipeFired = false;
                void OnSwipe(int _) => swipeFired = true;
                root.Photo.SwipeRequested += OnSwipe;
                try
                {
                    await UiDispatcher.InvokeAsync(() => root.Photo.ApplySwipe(-60, 4, -900));
                    if (!swipeFired) throw new InvalidOperationException("适应态横向轻扫未触发翻页");
                    swipeFired = false;
                    await PinchAsync(root, new CGPoint((double)root.Photo.Bounds.Width * .5, (double)root.Photo.Bounds.Height * .5), 2.5);
                    await UiDispatcher.InvokeAsync(() => root.Photo.ApplySwipe(-60, 4, -900));
                    if (swipeFired) throw new InvalidOperationException("放大态横向轻扫不应翻页（应保持平移）");
                }
                finally
                {
                    root.Photo.SwipeRequested -= OnSwipe;
                }
                lines.Add("PASS 轻扫：适应态触发上一张/下一张，放大态不翻页");

                // 九、增强开关不得移动照片（旧实现会经 SyncScrollFromViewport 把内容拽回左上角）；
                //     顺手测量 Full 与代理两档的切换时延。轻扫会切图，这里先回到大色卡。
                var largeFile = await UiDispatcher.InvokeAsync(() => main.FolderVM.AllFiles.First(item => item.Name == Path.GetFileName(large)));
                await UiDispatcher.InvokeAsync(() => main.CurrentFile = largeFile);
                await WaitAsync(() => main.CurrentFile?.Name == Path.GetFileName(large) && session.Image != null, "回到大色卡");
                await Task.Delay(500);
                await UiDispatcher.InvokeAsync(() => root.Photo.ZoomToPhysical(1.2));
                await SettleAsync(root);
                await WaitAsync(() => !session.IsProxy && session.Image is { Encoding: PixelEncoding.Rgba8 }, "增强计时前 Full 档");
                await SettleAsync(root);
                var beforeEnhance = await SnapshotAsync(root);
                double fullMs = await MeasureEnhanceAsync(root, session, enabled: true, expectedSource: PixelEncoding.Rgba8);
                await SettleAsync(root);
                var afterEnhance = await SnapshotAsync(root);
                AssertSnapshot(beforeEnhance, afterEnhance, "增强开启（Full 档）");
                if (session.Image is not { Encoding: PixelEncoding.Rgba8 })
                    throw new InvalidOperationException($"8 位 Full 档增强后应保持 8 位展示编码，实际 {session.Image?.Encoding}");
                lines.Add($"PASS 增强不改滚动状态（Full 24MP）：开启耗时 {fullMs:0} ms，输出 {session.Image!.Width}×{session.Image.Height} {session.Image.Encoding}");
                double fullOffMs = await MeasureEnhanceAsync(root, session, enabled: false);
                await SettleAsync(root);
                AssertSnapshot(beforeEnhance, await SnapshotAsync(root), "增强关闭（Full 档）");
                lines.Add($"PASS 增强关闭还原（Full 24MP）：{fullOffMs:0} ms");

                // 代理档：回到适应态后重新装载同一文件（会话只升不降，重新 SetFile 才会重新解出代理档）。
                await UiDispatcher.InvokeAsync(() =>
                {
                    root.Photo.FitToView();
                    session.SetFile(largeFile.File);
                });
                await WaitAsync(() => session.IsProxy && session.Image is { Encoding: PixelEncoding.RgbaF16 }, "回适应态代理档");
                await SettleAsync(root);
                var beforeProxyEnhance = await SnapshotAsync(root);
                double proxyMs = await MeasureEnhanceAsync(root, session, enabled: true, expectedSource: PixelEncoding.RgbaF16);
                await SettleAsync(root);
                AssertSnapshot(beforeProxyEnhance, await SnapshotAsync(root), "增强开启（代理档）");
                if (session.Image is not { Encoding: PixelEncoding.RgbaF16 })
                    throw new InvalidOperationException($"F16 代理增强后应保持 F16，实际 {session.Image?.Encoding}");
                lines.Add($"PASS 增强不改滚动状态（F16 代理 {session.Image!.Width}×{session.Image.Height}）：开启耗时 {proxyMs:0} ms");
                double proxyOffMs = await MeasureEnhanceAsync(root, session, enabled: false);
                await SettleAsync(root);
                AssertSnapshot(beforeProxyEnhance, await SnapshotAsync(root), "增强关闭（代理档）");
                lines.Add($"PASS 增强关闭还原（F16 代理）：{proxyOffMs:0} ms");

                await File.WriteAllLinesAsync(report, lines);
                Console.WriteLine($"[NativeSmoke] PASS gesture ({lines.Count} checks)");
            }
            finally
            {
                main.Settings.ApplySnapshot(snapshot);
                if (originalFile != null) main.CurrentFile = originalFile;
                await SettleAsync(root);
            }
        }
        catch (Exception exception)
        {
            lines.Add($"FAIL {exception}");
            await File.WriteAllLinesAsync(report, lines);
            Console.Error.WriteLine("[NativeSmoke] FAIL gesture " + exception);
        }
    }

    /// <summary>验收环境自愈：确保 JPEG 格式启用（历史验收会话被强杀可能遗留全格式关闭）。</summary>
    /// <param name="main">共享主 VM。</param>
    private static void EnsureJpegEnabled(MainViewModel main)
    {
        foreach (var format in main.Settings.FileFormats)
            if (format.DisplayName == "JPG") format.IsEnabled = true;
    }

    /// <summary>
    /// 按 UIKit 捏合语义一步缩放：先 SetZoomScale（触发 DidZoom），再按"手指下的内容点不动"调整 contentOffset
    /// （触发 Scrolled）。这正是真实捏合过程中 UIKit 对滚动视图做的事，用来替代旧检查里只写 zoomScale 的假捏合。
    /// </summary>
    /// <param name="root">原生根控制器。</param>
    /// <param name="pinchCenter">捏合中心（主图视图坐标）。</param>
    /// <param name="targetZoom">目标 zoomScale（相对适应态）。</param>
    /// <returns>实际生效的 zoomScale。</returns>
    private static async Task<double> PinchAsync(iOSNativeRootViewController root, CGPoint pinchCenter, double targetZoom)
    {
        return await UiDispatcher.InvokeAsync(() =>
        {
            var scroll = root.Photo.ScrollView;
            double from = Math.Max(1e-6, (double)scroll.ZoomScale);
            // 手指下的内容点（zoomScale=from 时的内容坐标）。
            var content = new CGPoint(
                ((double)pinchCenter.X + (double)scroll.ContentOffset.X) / from,
                ((double)pinchCenter.Y + (double)scroll.ContentOffset.Y) / from);
            scroll.SetZoomScale((nfloat)targetZoom, false);
            scroll.SetContentOffset(new CGPoint(
                (double)content.X * targetZoom - (double)pinchCenter.X,
                (double)content.Y * targetZoom - (double)pinchCenter.Y), false);
            return (double)scroll.ZoomScale;
        });
    }

    /// <summary>按 UIKit 平移语义移动 contentOffset（拖动/惯性都走这一条），触发 Scrolled。</summary>
    /// <param name="root">原生根控制器。</param>
    /// <param name="delta">位移（点）。</param>
    private static Task PanByAsync(iOSNativeRootViewController root, CGPoint delta)
        => UiDispatcher.InvokeAsync(() =>
        {
            var scroll = root.Photo.ScrollView;
            double x = (double)scroll.ContentOffset.X + (double)delta.X;
            double y = (double)scroll.ContentOffset.Y + (double)delta.Y;
            // 与 UIScrollView 的合法范围一致：内容边距之外不允许越界（这里模拟用户拖动后的落点）。
            var insets = scroll.ContentInset;
            double maxX = Math.Max(-(double)insets.Left, (double)scroll.ContentSize.Width - (double)scroll.Bounds.Width + (double)insets.Right);
            double maxY = Math.Max(-(double)insets.Top, (double)scroll.ContentSize.Height - (double)scroll.Bounds.Height + (double)insets.Bottom);
            scroll.SetContentOffset(new CGPoint(
                Math.Clamp(x, -(double)insets.Left, maxX),
                Math.Clamp(y, -(double)insets.Top, maxY)), false);
        });

    /// <summary>校验滚动视图的核心不变量：contentSize = 缩放内容视图基准尺寸 × zoomScale，偏移在合法范围内。</summary>
    /// <param name="root">原生根控制器。</param>
    /// <param name="stage">阶段名（报错用）。</param>
    private static Task AssertScrollInvariantsAsync(iOSNativeRootViewController root, string stage)
        => UiDispatcher.InvokeAsync(() =>
        {
            var scroll = root.Photo.ScrollView;
            var content = root.Photo.ZoomContent;
            double zoom = (double)scroll.ZoomScale;
            double expectedWidth = (double)content.Bounds.Width * zoom;
            double expectedHeight = (double)content.Bounds.Height * zoom;
            if (Math.Abs((double)scroll.ContentSize.Width - expectedWidth) > SizeTolerance
                || Math.Abs((double)scroll.ContentSize.Height - expectedHeight) > SizeTolerance)
                throw new InvalidOperationException(
                    $"{stage} contentSize {scroll.ContentSize} ≠ 缩放内容视图 {content.Bounds.Size} × zoom {zoom:0.###}");
            var insets = scroll.ContentInset;
            double minX = -(double)insets.Left, maxX = Math.Max(minX, expectedWidth - (double)scroll.Bounds.Width + (double)insets.Right);
            double minY = -(double)insets.Top, maxY = Math.Max(minY, expectedHeight - (double)scroll.Bounds.Height + (double)insets.Bottom);
            double x = (double)scroll.ContentOffset.X, y = (double)scroll.ContentOffset.Y;
            if (x < minX - SizeTolerance || x > maxX + SizeTolerance || y < minY - SizeTolerance || y > maxY + SizeTolerance)
                throw new InvalidOperationException(
                    $"{stage} contentOffset ({x:0.##},{y:0.##}) 超出合法范围 [{minX:0.##},{maxX:0.##}]×[{minY:0.##},{maxY:0.##}]");
            if ((double)content.Frame.Width <= 0 || (double)content.Frame.Height <= 0)
                throw new InvalidOperationException($"{stage} 缩放内容视图尺寸非法：{content.Frame}");
        });

    /// <summary>导航图可见性断言（适应态隐藏；放大态的几何/标签在第三节逐项核对）。</summary>
    /// <param name="root">原生根控制器。</param>
    /// <param name="visible">期望是否可见。</param>
    /// <param name="lines">报告行。</param>
    private static void VerifyNavigationIndicator(iOSNativeRootViewController root, bool visible, List<string> lines)
    {
        if (root.NavigationHost.Hidden == visible)
            throw new InvalidOperationException($"导航图可见性错误：期望 {(visible ? "显示" : "隐藏")}，实际 {(root.NavigationHost.Hidden ? "隐藏" : "显示")}");
        if (!visible)
        {
            if (root.Photo.IsFit != true) throw new InvalidOperationException("适应态判定异常：导航图隐藏但不是适应态");
            lines.Add("PASS 导航图：适应态隐藏（与原 Avalonia 缩放指示器一致）");
        }
    }

    /// <summary>读取滚动状态与屏幕锚点的快照（增强开关前后逐项对比）。</summary>
    /// <param name="root">原生根控制器。</param>
    private static Task<(CGSize ContentSize, CGPoint Offset, double Zoom, double PanX, double PanY, double Scale)> SnapshotAsync(iOSNativeRootViewController root)
        => UiDispatcher.InvokeAsync(() =>
        {
            var scroll = root.Photo.ScrollView;
            var viewport = root.Photo.Viewport;
            return (new CGSize(scroll.ContentSize.Width, scroll.ContentSize.Height),
                new CGPoint(scroll.ContentOffset.X, scroll.ContentOffset.Y),
                (double)scroll.ZoomScale, viewport.PanX, viewport.PanY, viewport.Scale);
        });

    /// <summary>断言增强开关前后滚动状态与视口变换逐项一致（不做任何"回正"或重排）。</summary>
    /// <param name="before">增强前快照。</param>
    /// <param name="after">增强后快照。</param>
    /// <param name="stage">阶段名。</param>
    private static void AssertSnapshot(
        (CGSize ContentSize, CGPoint Offset, double Zoom, double PanX, double PanY, double Scale) before,
        (CGSize ContentSize, CGPoint Offset, double Zoom, double PanX, double PanY, double Scale) after, string stage)
    {
        if (Math.Abs((double)before.ContentSize.Width - (double)after.ContentSize.Width) > SizeTolerance
            || Math.Abs((double)before.ContentSize.Height - (double)after.ContentSize.Height) > SizeTolerance)
            throw new InvalidOperationException($"{stage} 改变了 contentSize：{before.ContentSize} → {after.ContentSize}");
        if (Math.Abs((double)before.Offset.X - (double)after.Offset.X) > SizeTolerance
            || Math.Abs((double)before.Offset.Y - (double)after.Offset.Y) > SizeTolerance)
            throw new InvalidOperationException($"{stage} 改变了 contentOffset：{before.Offset} → {after.Offset}");
        if (Math.Abs(before.Zoom - after.Zoom) > 1e-6 || Math.Abs(before.Scale - after.Scale) > 1e-6
            || Math.Abs(before.PanX - after.PanX) > SizeTolerance || Math.Abs(before.PanY - after.PanY) > SizeTolerance)
            throw new InvalidOperationException(
                $"{stage} 改变了视口变换：zoom {before.Zoom:0.###}→{after.Zoom:0.###} scale {before.Scale:0.####}→{after.Scale:0.####} pan ({before.PanX:0.##},{before.PanY:0.##})→({after.PanX:0.##},{after.PanY:0.##})");
    }

    /// <summary>
    /// 切换增强并测时：从发起开关到增强图真正上屏（EnhancedDisplay）的墙钟时间。
    /// 开启方向同时断言"计算中"反馈：开关一按即进入计算态、提示标签可见，增强图就绪后提示收起（真机 24MP 档约 1–2 秒，不能无反馈）。
    /// </summary>
    /// <param name="root">原生根控制器（提供计算中提示标签）。</param>
    /// <param name="session">展示会话。</param>
    /// <param name="enabled">true 开启、false 关闭。</param>
    /// <param name="expectedSource">开启时要求当前展示档的编码（null 表示不校验）。</param>
    /// <returns>耗时（毫秒）。</returns>
    private static async Task<double> MeasureEnhanceAsync(iOSNativeRootViewController root, PhotoLoadSession session,
        bool enabled, PixelEncoding? expectedSource = null)
    {
        var stopwatch = Stopwatch.StartNew();
        bool busy = await UiDispatcher.InvokeAsync(() =>
        {
            if (enabled && expectedSource is { } expected && session.Image?.Encoding != expected)
                throw new InvalidOperationException(
                    $"增强发起时展示档编码 {session.Image?.Encoding} 不是期望的 {expected}（proxy={session.IsProxy}）");
            session.ToggleEnhanced();
            return session.IsEnhancing;
        });
        if (enabled)
        {
            if (!busy) throw new InvalidOperationException("增强开关按下后未进入计算态（IsEnhancing=false）");
            bool labelShown = await UiDispatcher.InvokeAsync(() => !root.EnhanceBusyLabel.Hidden);
            if (!labelShown) throw new InvalidOperationException("增强计算期间未显示\"计算中\"提示");
            await WaitAsync(() => session.EnhancedDisplay, "增强图就绪");
            await WaitAsync(() => !session.IsEnhancing && root.EnhanceBusyLabel.Hidden, "计算中提示收起");
        }
        else await WaitAsync(() => !session.EnhancedDisplay && !session.IsEnhanced, "增强图还原");
        stopwatch.Stop();
        return stopwatch.Elapsed.TotalMilliseconds;
    }

    /// <summary>归一化图像点漂移换算成屏幕点后与容差比较（"手指下的图像点还在手指下"的判定口径）。</summary>
    /// <param name="before">变化前。</param>
    /// <param name="after">变化后。</param>
    /// <param name="tolerancePoints">屏幕容差（点）。</param>
    /// <param name="photo">主图宿主（提供当前显示尺寸）。</param>
    /// <param name="stage">阶段名。</param>
    private static void AssertImagePoint((double U, double V) before, (double U, double V) after,
        double tolerancePoints, iOSPhotoView photo, string stage)
    {
        double displayedWidth = photo.DisplayWidth * photo.PointScale;
        double displayedHeight = photo.DisplayHeight * photo.PointScale;
        double dx = Math.Abs(after.U - before.U) * displayedWidth;
        double dy = Math.Abs(after.V - before.V) * displayedHeight;
        double drift = Math.Sqrt(dx * dx + dy * dy);
        if (drift > tolerancePoints)
            throw new InvalidOperationException($"{stage}漂移 {drift:0.##} 点（容差 {tolerancePoints:0.#}）：({before.U:0.#####},{before.V:0.#####}) → ({after.U:0.#####},{after.V:0.#####})");
    }

    /// <summary>两点距离（点）。</summary>
    /// <param name="a">点 a。</param>
    /// <param name="b">点 b。</param>
    private static double Distance(CGPoint a, CGPoint b)
        => Math.Sqrt(Math.Pow((double)a.X - (double)b.X, 2) + Math.Pow((double)a.Y - (double)b.Y, 2));

    /// <summary>尺寸文本。</summary>
    /// <param name="size">尺寸。</param>
    private static string Size(CGSize size) => $"{size.Width:0.#}×{size.Height:0.#}";

    /// <summary>等待条件成立并设有限超时。</summary>
    /// <param name="condition">条件。</param>
    /// <param name="name">条件名称。</param>
    private static async Task WaitAsync(Func<bool> condition, string name)
    {
        var stopwatch = Stopwatch.StartNew();
        while (!condition())
        {
            if (stopwatch.Elapsed > TimeSpan.FromSeconds(60)) throw new TimeoutException(name);
            await Task.Delay(20);
        }
    }

    /// <summary>等待一轮布局与渲染沉淀。</summary>
    /// <param name="root">原生根控制器。</param>
    private static async Task SettleAsync(iOSNativeRootViewController root)
    {
        await UiDispatcher.InvokeAsync(() => { root.View!.SetNeedsLayout(); root.View.LayoutIfNeeded(); });
        await Task.Delay(Settle);
    }
}
