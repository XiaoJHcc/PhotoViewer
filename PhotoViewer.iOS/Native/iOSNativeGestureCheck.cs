using CoreGraphics;
using Foundation;
using PhotoViewer.Apple;
using PhotoViewer.Core;
using PhotoViewer.Core.Design;
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
/// 捏合触发的 Full 热替换、布局/旋转、双击、Fit 命令切换、放大态切图（保持倍率与归一化中心，含对焦跟随）、
/// 适应态轻扫、放大态拖动、增强开关，以及导航图的可见性与几何。</para>
///
/// <para>带 <c>--pv-open</c> 启动时先核对**冷启动首图**：尚未发生任何缩放的双击必须精确到物理 100%
/// （滚动视图 zoomScale == 1/(BackingScale×FitScale)），这是作者真机"首图双击只放大一点点"的复现口径。</para>
///
/// 素材要求：目录内必须有 <c>color-p3-large.jpg</c>（长边 ≥ 4000，制造真实代理档与 100% 物理 Full）与
/// <c>focus-src.ARW</c>（Sony MakerNote 0x2027 提供对焦位置）；验收会把大色卡复制成三份同分辨率副本、
/// 并把 ARW 复制两份分别改写到不同对焦点，全部写入 App 自建的 <c>Documents/gesture-fixtures-work</c>
/// （输入目录、用户照片与 TestCase 一律只读）。报告写 Documents/PhotoViewer/native-gesture-checks.txt，
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
                // 冷启动首图：启动参数（--pv-open）把照片直接上屏时，必须在任何 Fit/缩放之前核对
                // "首图双击到物理 100%"，这正是作者在真机上遇到"只放大一点点"的现场。
                if (main.CurrentFile != null)
                {
                    await WaitAsync(() => session.Image != null, "冷启动首图上屏", TimeSpan.FromSeconds(30));
                    lines.Add($"冷启动首图：{main.CurrentFile.Name}");
                    await VerifyColdStartFirstPhotoAsync(root, session, main, lines);
                }
                await PrepareFixturesAsync(directory, lines);
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
                await main.FolderVM.OpenFolderAsync(new iOSFolderRef(WorkDirectory));
                var largeFile = await UiDispatcher.InvokeAsync(() =>
                    main.FolderVM.AllFiles.FirstOrDefault(item => item.Name == Path.GetFileName(large)));
                if (largeFile == null) throw new InvalidOperationException("打开夹具目录后找不到大色卡（color-p3-large.jpg）");
                await SelectFileAsync(main, session, largeFile, "大色卡上屏");
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

                // 七之二、Fit 命令：原 Avalonia 语义是"适应 ⇄ 物理 100%"切换（不是无条件回适应）。
                await UiDispatcher.InvokeAsync(() => root.Photo.FitToView());
                await SettleAsync(root);
                await UiDispatcher.InvokeAsync(() => root.Photo.ToggleFit());
                await WaitAsync(() => !root.Photo.IsFit && Math.Abs(root.Photo.PhysicalScale - 1) < .01, "Fit 命令放大到物理 100%");
                await SettleAsync(root);
                var fitCommand = await UiDispatcher.InvokeAsync(() => (Fit: root.Photo.IsFit, Physical: root.Photo.PhysicalScale,
                    Center: root.Photo.Viewport.CenterUV, HostHidden: root.NavigationHost.Hidden));
                if (fitCommand.Fit) throw new InvalidOperationException("Fit 命令在适应态没有放大（仍是适应态）");
                if (fitCommand.HostHidden) throw new InvalidOperationException("Fit 命令放大后导航图未显示");
                if (Math.Abs(fitCommand.Center.U - .5) > .02 || Math.Abs(fitCommand.Center.V - .5) > .02)
                    throw new InvalidOperationException($"无对焦点时 Fit 命令应从视口中心放大，实际中心 ({fitCommand.Center.U:0.###},{fitCommand.Center.V:0.###})");
                await UiDispatcher.InvokeAsync(() => root.Photo.ToggleFit());
                await WaitAsync(() => root.Photo.IsFit, "Fit 命令回适应态");
                await SettleAsync(root);
                VerifyNavigationIndicator(root, visible: false, lines);
                lines.Add($"PASS Fit 命令：适应 ⇄ 物理 {fitCommand.Physical * 100:0.#}% 双向切换（无对焦点以视口中心放大，导航图随态显隐）");

                // 七之三、放大态切图：保持倍率与同一归一化中心点（原 Avalonia OnBitmapChanged / Mac ApplyImage 同语义）；
                //       切图首帧若是代理、随后升 Full，热替换不得改动滚动状态。
                var sameLarge = await UiDispatcher.InvokeAsync(() => main.FolderVM.AllFiles.First(item => item.Name == "gesture-b-large.jpg"));
                var secondLarge = await UiDispatcher.InvokeAsync(() => main.FolderVM.AllFiles.First(item => item.Name == "gesture-c-large.jpg"));
                await UiDispatcher.InvokeAsync(() => root.Photo.FitToView());
                await SettleAsync(root);
                await UiDispatcher.InvokeAsync(() => root.Photo.ZoomToPhysical(1));
                await WaitAsync(() => Math.Abs(root.Photo.PhysicalScale - 1) < .02, "切图前 100%");
                await SettleAsync(root);
                await UiDispatcher.InvokeAsync(() => root.Photo.CenterOnNormalizedPoint(.82, .22));
                await SettleAsync(root);
                var beforeFlip = await UiDispatcher.InvokeAsync(() => (Scale: root.Photo.PhysicalScale,
                    Center: root.Photo.Viewport.CenterUV, Zoom: (double)root.Photo.ScrollView.ZoomScale));
                await SelectFileAsync(main, session, sameLarge, "切到同分辨率副本");
                await SettleAsync(root);
                var afterFlip = await UiDispatcher.InvokeAsync(() => (Scale: root.Photo.PhysicalScale,
                    Center: root.Photo.Viewport.CenterUV, Zoom: (double)root.Photo.ScrollView.ZoomScale,
                    Proxy: session.IsProxy));
                if (Math.Abs(afterFlip.Scale - beforeFlip.Scale) > .01)
                    throw new InvalidOperationException($"放大态切图未保持倍率：{beforeFlip.Scale:0.###} → {afterFlip.Scale:0.###}");
                if (Math.Abs(afterFlip.Center.U - beforeFlip.Center.U) > .01 || Math.Abs(afterFlip.Center.V - beforeFlip.Center.V) > .01)
                    throw new InvalidOperationException(
                        $"放大态切图未保持归一化中心：({beforeFlip.Center.U:0.####},{beforeFlip.Center.V:0.####}) → ({afterFlip.Center.U:0.####},{afterFlip.Center.V:0.####})");
                await AssertScrollInvariantsAsync(root, "切图后");
                var landed = await SnapshotAsync(root);
                await WaitAsync(() => !session.IsProxy, "切图后升 Full");
                await SettleAsync(root);
                AssertSnapshot(landed, await SnapshotAsync(root), "切图后代理→Full");
                lines.Add($"PASS 放大态切图：{beforeFlip.Scale * 100:0.#}% 与中心 ({afterFlip.Center.U:0.###},{afterFlip.Center.V:0.###}) 保持" +
                    (afterFlip.Proxy ? "，代理→Full 热替换不改变滚动状态" : "，直接落 Full 且无跳变"));

                // 半倍率切图：新照片先代理上屏（未达 Full 门槛），再放大到 100% 触发 Full；
                // 热替换只换纹理，视口中心对应的图像点不得移动。
                await UiDispatcher.InvokeAsync(() => root.Photo.FitToView());
                await SettleAsync(root);
                await UiDispatcher.InvokeAsync(() => root.Photo.ZoomToPhysical(.5));
                await WaitAsync(() => Math.Abs(root.Photo.PhysicalScale - .5) < .02, "切图前 50%");
                await SettleAsync(root);
                await UiDispatcher.InvokeAsync(() => root.Photo.CenterOnNormalizedPoint(.6, .58));
                await SettleAsync(root);
                await SelectFileAsync(main, session, secondLarge, "切到第三张大色卡");
                await SettleAsync(root);
                var half = await UiDispatcher.InvokeAsync(() => (Scale: root.Photo.PhysicalScale, Proxy: session.IsProxy,
                    Center: root.Photo.Viewport.CenterUV));
                if (Math.Abs(half.Scale - .5) > .02) throw new InvalidOperationException($"半倍率切图未保持倍率：{half.Scale:0.###}");
                if (Math.Abs(half.Center.U - .6) > .01 || Math.Abs(half.Center.V - .58) > .01)
                    throw new InvalidOperationException($"半倍率切图未保持归一化中心：({half.Center.U:0.####},{half.Center.V:0.####})");
                if (!half.Proxy) throw new InvalidOperationException($"半倍率切图应落代理档，实际已是非代理（{session.Image?.Width}×{session.Image?.Height}）");
                await UiDispatcher.InvokeAsync(() => root.Photo.ZoomToPhysical(1));
                await WaitAsync(() => !session.IsProxy && Math.Abs(root.Photo.PhysicalScale - 1) < .02, "切图后放大触发 Full");
                await SettleAsync(root);
                var upgraded = await UiDispatcher.InvokeAsync(() => root.Photo.Viewport.CenterUV);
                if (Math.Abs(upgraded.U - .6) > .01 || Math.Abs(upgraded.V - .58) > .01)
                    throw new InvalidOperationException($"代理→Full 热替换移动了视口中心：({upgraded.U:0.####},{upgraded.V:0.####})");
                await AssertScrollInvariantsAsync(root, "切图后升 Full");
                lines.Add($"PASS 半倍率切图：代理档落地并保持中心 ({upgraded.U:0.###},{upgraded.V:0.###})，升 Full 不移动视口");

                // 七之四、对焦跟随切图（真实 Sony EXIF 0x2027）：视口中心在 1600% 下对准对焦点后切图，
                //       新照片必须保持倍率并居中到它自己的对焦点（原 Avalonia _isFocusedOnFocusPoint 语义）。
                var focusA = await UiDispatcher.InvokeAsync(() => main.FolderVM.AllFiles.First(item => item.Name == "focus-a.ARW"));
                var focusB = await UiDispatcher.InvokeAsync(() => main.FolderVM.AllFiles.First(item => item.Name == "focus-b.ARW"));
                await focusA.LoadExifDataAsync();
                await focusB.LoadExifDataAsync();
                await SelectFileAsync(main, session, focusA, "对焦素材 A 上屏");
                await SettleAsync(root);
                var focusPoint = await UiDispatcher.InvokeAsync(() => root.Photo.FocusPoint);
                var focusExpected = await FocusUVAsync(focusA);
                if (focusPoint is not { } actualFocus
                    || Math.Abs((double)actualFocus.X - focusExpected.U) > .01 || Math.Abs((double)actualFocus.Y - focusExpected.V) > .01)
                    throw new InvalidOperationException($"对焦点未按 Sony EXIF 解析：期望 ({focusExpected.U:0.####},{focusExpected.V:0.####})，实际 {focusPoint}");
                // Fit 命令在有对焦点时必须优先居中到对焦点（无锚点），而不是视口中心。
                await UiDispatcher.InvokeAsync(() => root.Photo.FitToView());
                await SettleAsync(root);
                await UiDispatcher.InvokeAsync(() => root.Photo.ToggleFit());
                await WaitAsync(() => !root.Photo.IsFit && Math.Abs(root.Photo.PhysicalScale - 1) < .02, "对焦素材 Fit 到物理 100%");
                var focusFit = await UiDispatcher.InvokeAsync(() => (Center: root.Photo.Viewport.CenterUV,
                    Follows: root.Photo.Viewport.FollowsFocus));
                if (!focusFit.Follows) throw new InvalidOperationException("有对焦点时 Fit 命令应开启跟随对焦");
                if (Math.Abs(focusFit.Center.U - focusExpected.U) > Math.Abs(.5 - focusExpected.U) + 1e-6
                    || Math.Abs(focusFit.Center.V - focusExpected.V) > Math.Abs(.5 - focusExpected.V) + 1e-6)
                    throw new InvalidOperationException(
                        $"Fit 命令未把视口中心移向对焦点：中心 ({focusFit.Center.U:0.###},{focusFit.Center.V:0.###})，对焦点 ({focusExpected.U:0.###},{focusExpected.V:0.###})");
                await UiDispatcher.InvokeAsync(() => root.Photo.ToggleFit());
                await WaitAsync(() => root.Photo.IsFit, "对焦素材 Fit 回适应");
                await SettleAsync(root);
                lines.Add($"PASS Fit 命令（有对焦点）：居中到对焦点 ({focusExpected.U:0.###},{focusExpected.V:0.###}) 并开启跟随，双向切换正常");

                await UiDispatcher.InvokeAsync(() => root.Photo.ZoomToPhysical(16));
                await WaitAsync(() => Math.Abs(root.Photo.PhysicalScale - 16) < .1, "对焦素材放大到 1600%");
                await UiDispatcher.InvokeAsync(() => root.Photo.CenterOnNormalizedPoint(focusExpected.U, focusExpected.V));
                await SettleAsync(root);
                var focusBefore = await UiDispatcher.InvokeAsync(() => (Center: root.Photo.Viewport.CenterUV,
                    Follows: root.Photo.Viewport.FollowsFocus, Scale: root.Photo.PhysicalScale));
                if (!focusBefore.Follows) throw new InvalidOperationException("视口中心落在对焦点上时 FollowsFocus 应为 true");
                var nextFocus = await FocusUVAsync(focusB);
                await SelectFileAsync(main, session, focusB, "对焦素材 B 上屏");
                await SettleAsync(root);
                var focusAfter = await UiDispatcher.InvokeAsync(() => (Center: root.Photo.Viewport.CenterUV,
                    Follows: root.Photo.Viewport.FollowsFocus, Scale: root.Photo.PhysicalScale));
                if (Math.Abs(focusAfter.Scale - focusBefore.Scale) > .05)
                    throw new InvalidOperationException($"对焦跟随切图未保持倍率：{focusBefore.Scale:0.##} → {focusAfter.Scale:0.##}");
                if (Math.Abs(focusAfter.Center.U - nextFocus.U) > .02 || Math.Abs(focusAfter.Center.V - nextFocus.V) > .02)
                    throw new InvalidOperationException(
                        $"对焦跟随切图未居中到新照片对焦点：期望 ({nextFocus.U:0.####},{nextFocus.V:0.####})，实际 ({focusAfter.Center.U:0.####},{focusAfter.Center.V:0.####})");
                lines.Add($"PASS 对焦跟随切图：EXIF 对焦点 ({focusExpected.U:0.###},{focusExpected.V:0.###})，切图保持 {focusAfter.Scale * 100:0}% 并居中到新对焦点 ({nextFocus.U:0.###},{nextFocus.V:0.###})");

                // 回到大色卡与适应态，后续轻扫/增强用例沿用原有前提。
                await SelectFileAsync(main, session, largeFile, "回到大色卡（适应态）");
                await UiDispatcher.InvokeAsync(() => root.Photo.FitToView());
                await SettleAsync(root);

                // 八、轻扫翻页只在适应态生效；放大态拖动仍是平移。
                bool swipeFired = false;
                void OnSwipe(int _) => swipeFired = true;
                root.Photo.SwipeRequested += OnSwipe;
                try
                {
                    await UiDispatcher.InvokeAsync(() => root.Photo.ApplySwipe(-60, 4, -900));
                    if (!swipeFired)
                        throw new InvalidOperationException($"适应态横向轻扫未触发翻页（fit={root.Photo.IsFit}，" +
                            $"scale={root.Photo.PhysicalScale:0.####}，file={main.CurrentFile?.Name}）");
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
                await SelectFileAsync(main, session, largeFile, "回到大色卡");
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

    /// <summary>
    /// 选择当前照片并等待会话真正把新产物上屏（会话重置换图经 UI 线程排队，切到与已有产物相同的引用时只等非空）。
    /// </summary>
    /// <param name="main">共享主 VM。</param>
    /// <param name="session">展示会话。</param>
    /// <param name="file">目标文件。</param>
    /// <param name="stage">阶段名（等待超时提示）。</param>
    private static async Task SelectFileAsync(MainViewModel main, PhotoLoadSession session, ImageFile file, string stage)
    {
        bool shown = false;
        using var subscription = session.Subscribe(source => { if (source.PathKey == file.File.PathKey) shown = true; });
        bool same = ReferenceEquals(main.CurrentFile, file);
        await UiDispatcher.InvokeAsync(() => main.CurrentFile = file);
        if (!same) await WaitAsync(() => ReferenceEquals(main.CurrentFile, file) && shown, stage);
        // 切图请求与原生壳的会话重置都经 UI 线程排队；留出一轮排空后再确认产物仍在（避免读到旧图或重置窗口）。
        await Task.Delay(200);
        await WaitAsync(() => session.Image != null, stage);
    }

    /// <summary>等待主图分区尺寸连续 500ms 不变（启动期控制栏/文件栏定型会改变主图可用高度）。</summary>
    /// <param name="root">原生根控制器。</param>
    private static async Task WaitStablePhotoBoundsAsync(iOSNativeRootViewController root)
    {
        var stopwatch = Stopwatch.StartNew();
        CGSize last = CGSize.Empty;
        TimeSpan stableSince = stopwatch.Elapsed;
        while (stopwatch.Elapsed < TimeSpan.FromSeconds(20))
        {
            var size = await UiDispatcher.InvokeAsync(() => root.Photo.Bounds.Size);
            if (Math.Abs((double)size.Width - (double)last.Width) > .5 || Math.Abs((double)size.Height - (double)last.Height) > .5)
            {
                last = size;
                stableSince = stopwatch.Elapsed;
            }
            else if (stopwatch.Elapsed - stableSince > TimeSpan.FromMilliseconds(500)) return;
            await Task.Delay(50);
        }
        throw new TimeoutException("主图分区尺寸未稳定");
    }

    /// <summary>夹具工作副本目录（App 自建；设备上的输入目录由 devicectl 拷入、属 root，App 不可写）。</summary>
    private static string WorkDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "gesture-fixtures-work");

    /// <summary>
    /// 冷启动首图（启动参数直接上屏、尚未发生任何缩放）双击必须精确到物理 100%：
    /// 断言滚动视图的 zoomScale 恰为 1/(BackingScale×FitScale)、触点下的图像点保持、滚动基准全部合规。
    /// 这是作者真机"首图双击只放大一点点"的现场复现口径。
    /// </summary>
    /// <param name="root">原生根控制器。</param>
    /// <param name="session">展示会话。</param>
    /// <param name="main">共享主 VM（读取当前照片的 EXIF 就绪状态）。</param>
    /// <param name="lines">报告行。</param>
    private static async Task VerifyColdStartFirstPhotoAsync(iOSNativeRootViewController root, PhotoLoadSession session,
        MainViewModel main, List<string> lines)
    {
        // 启动后控制栏要等当前照片的 EXIF 组就绪才定型（主图分区会先高后矮）；必须等它定型再双击，
        // 否则量到的是过渡布局的适应倍率，和用户真正看到的画面不是一回事。
        await WaitAsync(() => main.CurrentFile?.IsExifLoaded == true, "冷启动首图 EXIF 就绪", TimeSpan.FromSeconds(20));
        await WaitStablePhotoBoundsAsync(root);
        await SettleAsync(root);
        if (!root.Photo.IsFit)
            throw new InvalidOperationException("冷启动首图不是适应态（启动后不应带任何缩放）");
        var start = await UiDispatcher.InvokeAsync(() => (Physical: root.Photo.PhysicalScale,
            Image: session.Image!, Bounds: root.Photo.Bounds.Size));
        var tap = new CGPoint((double)start.Bounds.Width * .62, (double)start.Bounds.Height * .58);
        if (start.Physical >= .99)
        {
            // 预览尺寸素材（iOS RAW 走 ImageIO 内嵌预览，1616×1080 在 iPad 上适应态就已 ≥ 物理 100%）：
            // 物理 100% 不高于适应态，双击不得产生任何放大或跳动——作者真机"首图双击只放大一点点"即此口径。
            await UiDispatcher.InvokeAsync(() => root.Photo.DoubleTapAt(tap));
            await SettleAsync(root);
            var unchanged = await UiDispatcher.InvokeAsync(() => (Fit: root.Photo.IsFit, Physical: root.Photo.PhysicalScale));
            if (!unchanged.Fit || Math.Abs(unchanged.Physical - start.Physical) > .01)
                throw new InvalidOperationException(
                    $"无放大余量的首图双击不应改变视图：适应 {start.Physical * 100:0.#}% → fit={unchanged.Fit} {unchanged.Physical * 100:0.#}%");
            lines.Add($"PASS 冷启动首图双击：{start.Image.Width}×{start.Image.Height} 适应 {start.Physical * 100:0.#}% " +
                "已不低于物理 100%（iOS RAW 内嵌预览，无放大余量），双击保持适应态不跳动");
            return;
        }
        var anchor = await UiDispatcher.InvokeAsync(() => root.Photo.ImagePointAt(tap));
        await UiDispatcher.InvokeAsync(() => root.Photo.DoubleTapAt(tap));
        await WaitAsync(() => Math.Abs(root.Photo.PhysicalScale - 1) < .01, "冷启动首图双击到物理 100%");
        await SettleAsync(root);
        var state = await UiDispatcher.InvokeAsync(() => (Zoom: (double)root.Photo.ScrollView.ZoomScale,
            Max: (double)root.Photo.ScrollView.MaximumZoomScale, Fit: root.Photo.IsFit,
            Bs: root.Photo.Viewport.BackingScale, FitScale: root.Photo.Viewport.FitScale,
            Physical: root.Photo.PhysicalScale));
        if (state.Fit) throw new InvalidOperationException("冷启动首图双击后仍判定为适应态");
        double expectedZoom = 1 / (state.Bs * state.FitScale);
        if (Math.Abs(state.Zoom - expectedZoom) > expectedZoom * .01)
            throw new InvalidOperationException(
                $"冷启动首图双击后 zoomScale {state.Zoom:0.####} 不是物理 100% 的 {expectedZoom:0.####}" +
                $"（max={state.Max:0.####}，fit={state.FitScale:0.####}，bs={state.Bs:0.##}）");
        if (state.Max < expectedZoom - 1e-3)
            throw new InvalidOperationException($"滚动视图最大缩放 {state.Max:0.####} 小于物理 100% 目标 {expectedZoom:0.####}");
        var after = await UiDispatcher.InvokeAsync(() => root.Photo.ImagePointAt(tap));
        AssertImagePoint(anchor, after, 2.5, root.Photo, "冷启动首图双击后触点下的图像点");
        await AssertScrollInvariantsAsync(root, "冷启动首图双击后");
        if (root.NavigationHost.Hidden) throw new InvalidOperationException("冷启动首图双击到 100% 后导航图未显示");
        lines.Add($"PASS 冷启动首图双击：{start.Image.Width}×{start.Image.Height} 适应 {start.Physical * 100:0.#}% → " +
            $"物理 {state.Physical * 100:0.##}%（zoomScale {state.Zoom:0.####} = 1/({state.Bs:0.##}×{state.FitScale:0.####})），触点图像点保持");
        await UiDispatcher.InvokeAsync(() => root.Photo.DoubleTapAt(tap));
        await WaitAsync(() => root.Photo.IsFit, "冷启动首图双击回适应态");
        await SettleAsync(root);
    }

    /// <summary>
    /// 在工作副本目录里准备手势验收夹具：3 张同分辨率大色卡（切图保持倍率）与 2 张改过对焦点的 Sony ARW 副本。
    /// 只写 App 自建目录，输入目录与 TestCase/用户照片一律只读；ARW 副本仅改 MakerNote 0x2027 的 8 字节对焦位置。
    /// </summary>
    /// <param name="directory">输入夹具目录（只读）。</param>
    /// <param name="lines">报告行。</param>
    private static async Task PrepareFixturesAsync(string directory, List<string> lines)
    {
        string large = Path.Combine(directory, "color-p3-large.jpg");
        string sourceRaw = Path.Combine(directory, "focus-src.ARW");
        if (!File.Exists(sourceRaw))
            throw new FileNotFoundException("手势验收需要 focus-src.ARW（Sony MakerNote 0x2027 提供对焦位置）", sourceRaw);
        Directory.CreateDirectory(WorkDirectory);
        string workLarge = Path.Combine(WorkDirectory, "color-p3-large.jpg");
        CopyIfNeeded(large, workLarge);
        CopyIfNeeded(large, Path.Combine(WorkDirectory, "gesture-b-large.jpg"));
        CopyIfNeeded(large, Path.Combine(WorkDirectory, "gesture-c-large.jpg"));
        var exif = await ExifLoader.LoadExifDataAsync(new iOSFileRef(sourceRaw));
        if (exif?.SonyFocusPosition is not { } focus || focus.ImageWidth <= 0 || focus.ImageHeight <= 0)
            throw new InvalidOperationException("focus-src.ARW 没有 Sony 对焦位置（MakerNote 0x2027），无法核对对焦跟随");
        WriteFocusCopy(sourceRaw, Path.Combine(WorkDirectory, "focus-a.ARW"), focus, 1500, 3400);
        WriteFocusCopy(sourceRaw, Path.Combine(WorkDirectory, "focus-b.ARW"), focus, 5200, 1200);
        lines.Add($"夹具工作副本：{WorkDirectory}（3 张大色卡 + 2 张对焦点 ARW 副本，对焦点改写仅在副本内）");
    }

    /// <summary>文件内容不同（或目标不存在）时才复制，避免每轮验收重写 44MB 夹具。</summary>
    /// <param name="source">源文件。</param>
    /// <param name="destination">目标文件。</param>
    private static void CopyIfNeeded(string source, string destination)
    {
        if (File.Exists(destination) && new FileInfo(destination).Length == new FileInfo(source).Length) return;
        File.Copy(source, destination, true);
    }

    /// <summary>
    /// 复制 Sony ARW 副本并改写 MakerNote 0x2027 的 8 字节对焦位置（图像宽高 + 对焦 XY，均为 int16 小端）。
    /// 先按源文件的真实取值定位唯一字节序列，再替换 XY；找不到即显式失败（不接受静默跳过）。
    /// </summary>
    /// <param name="source">源 ARW（只读）。</param>
    /// <param name="destination">副本路径。</param>
    /// <param name="focus">源文件的 MakerNote 对焦数据（用于定位字节序列）。</param>
    /// <param name="focusX">新的对焦横坐标（源图像素）。</param>
    /// <param name="focusY">新的对焦纵坐标（源图像素）。</param>
    private static void WriteFocusCopy(string source, string destination,
        (int ImageWidth, int ImageHeight, int FocusX, int FocusY) focus, int focusX, int focusY)
    {
        byte[] data = File.ReadAllBytes(source);
        byte[] pattern =
        [
            (byte)focus.ImageWidth, (byte)(focus.ImageWidth >> 8),
            (byte)focus.ImageHeight, (byte)(focus.ImageHeight >> 8),
            (byte)focus.FocusX, (byte)(focus.FocusX >> 8),
            (byte)focus.FocusY, (byte)(focus.FocusY >> 8),
        ];
        int index = data.AsSpan().IndexOf(pattern);
        if (index < 0) throw new InvalidOperationException("focus-src.ARW 未找到 MakerNote 0x2027 原始字节，无法生成对焦夹具");
        data[index + 4] = (byte)focusX;
        data[index + 5] = (byte)(focusX >> 8);
        data[index + 6] = (byte)focusY;
        data[index + 7] = (byte)(focusY >> 8);
        File.WriteAllBytes(destination, data);
    }

    /// <summary>读取文件 EXIF 的归一化对焦点（显示朝向 0–1）；缺失时显式失败。</summary>
    /// <param name="file">图像文件（EXIF 已加载）。</param>
    private static Task<(double U, double V)> FocusUVAsync(ImageFile file)
    {
        return UiDispatcher.InvokeAsync(() =>
        {
            if (file.ExifData?.SonyFocusPosition is not { } position || position.ImageWidth <= 0 || position.ImageHeight <= 0)
                throw new InvalidOperationException($"{file.Name} 缺少 Sony 对焦点");
            return ((double)position.FocusX / position.ImageWidth, (double)position.FocusY / position.ImageHeight);
        });
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
            // IMAGE-07：药丸接共享 500ms 门槛，按下当刻不得显示。
            bool labelShownImmediately = await UiDispatcher.InvokeAsync(() => !root.EnhanceBusyLabel.Hidden);
            if (labelShownImmediately) throw new InvalidOperationException("增强按下当刻即显示\"计算中\"（未走 500ms 门槛）");
            await Task.Delay(DesignTokens.ImageBusyDelay + 200);
            bool stillEnhancing = await UiDispatcher.InvokeAsync(() => session.IsEnhancing);
            if (stillEnhancing)
            {
                bool labelShown = await UiDispatcher.InvokeAsync(() => session.IsEnhancingVisible && !root.EnhanceBusyLabel.Hidden);
                if (!labelShown) throw new InvalidOperationException("增强计算超过 500ms 仍未显示\"计算中\"");
            }
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
    /// <param name="timeout">超时；null 表示默认 60 秒。</param>
    private static async Task WaitAsync(Func<bool> condition, string name, TimeSpan? timeout = null)
    {
        var stopwatch = Stopwatch.StartNew();
        TimeSpan limit = timeout ?? TimeSpan.FromSeconds(60);
        while (!condition())
        {
            if (stopwatch.Elapsed > limit) throw new TimeoutException(name);
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
