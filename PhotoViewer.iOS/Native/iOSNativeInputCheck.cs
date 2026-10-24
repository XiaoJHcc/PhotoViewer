using CoreGraphics;
using Foundation;
using PhotoViewer.Apple;
using PhotoViewer.Core;
using PhotoViewer.Core.Image;
using PhotoViewer.Core.Input;
using PhotoViewer.Core.Platform;
using PhotoViewer.Core.Settings;
using PhotoViewer.Core.Storage;
using PhotoViewer.iOS.Core;
using PhotoViewer.ViewModels.Main;
using PhotoViewer.ViewModels.Settings;
using System.Diagnostics;
using UIKit;

namespace PhotoViewer.iOS.Native;

/// <summary>
/// 验收专用（非产品行为，测试钩子）：<c>--pv-native-input-check &lt;目录&gt;</c> 在模拟器/真机上程序化核对 iPad 输入层——
/// 主图与卡片上下文菜单内容与回调、卡片评分菜单写回且不切主图、适应态轻扫翻页与放大态不翻页、HID 键码映射表与
/// 合成派发（UIPress 不可合成，直接注入与 <see cref="iOSNativeRootViewController.PressesBegan"/> 共用的派发入口）、
/// INPUT-01 守卫、拖入走共享外部打开路径，以及设置页关闭回写后各分类在主屏的即时传播。
/// 素材要求：目录内 ≥2 张自有 jpg 夹具（含可写 XMP），且目录必须位于本 App 沙盒内 —— 不写用户照片或 TestCase。
/// 报告写 Documents/PhotoViewer/native-input-checks.txt，打印 <c>[NativeSmoke] PASS input (n)</c> / FAIL。
/// </summary>
internal static class iOSNativeInputCheck
{
    /// <summary>验收开关。</summary>
    private const string Switch = "--pv-native-input-check";
    /// <summary>布局/动画沉淀等待。</summary>
    private static readonly TimeSpan Settle = TimeSpan.FromMilliseconds(160);
    /// <summary>胶片栏定位到中心的允许误差（点）。</summary>
    private const double CenterTolerance = 1.5;
    /// <summary>控制栏结构核对的几何容差（点）：只允许贴边与取整误差，真实重叠必须被拦下。</summary>
    private const double Exact = .5;

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

    /// <summary>
    /// 把夹具复制到 App 自建的工作目录再运行：评分写回会在同目录新建 .xmp_backup，
    /// devicectl 拷入的目录属 root（0755），App 无法在其中创建文件。
    /// 源目录本身就是工作目录时直接复用，避免删除自身。
    /// </summary>
    /// <param name="source">操作者提供的夹具目录（沙盒内）。</param>
    /// <param name="fixtures">源目录内的 jpg 列表。</param>
    /// <returns>可写的工作目录绝对路径。</returns>
    private static string StageFixtures(string source, string[] fixtures)
    {
        string workspace = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "input-fixtures");
        if (string.Equals(Path.GetFullPath(source), Path.GetFullPath(workspace), StringComparison.Ordinal)) return workspace;
        if (Directory.Exists(workspace)) Directory.Delete(workspace, true);
        Directory.CreateDirectory(workspace);
        foreach (string fixture in fixtures) File.Copy(fixture, Path.Combine(workspace, Path.GetFileName(fixture)), true);
        return workspace;
    }

    /// <summary>相对路径按沙盒 Documents 解析（与壳层 --pv-open 同一口径）。</summary>
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
        string report = Path.Combine(output, "native-input-checks.txt");
        try
        {
            var main = iOSNativeShell.Main ?? throw new InvalidOperationException("原生壳主 VM 未就绪");
            if (!Path.GetFullPath(directory).StartsWith(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), StringComparison.Ordinal))
                throw new InvalidOperationException($"输入验收只允许使用 App 沙盒内的自有夹具目录：{directory}");
            await WaitAsync(() => root.View!.Bounds.Width > 100 && root.Photo.Bounds.Width > 100, "主屏布局");
            string[] fixtures = Directory.GetFiles(directory, "*.jpg");
            if (fixtures.Length < 2) throw new InvalidOperationException($"输入验收需要 ≥2 张自有夹具，实际 {fixtures.Length}（{directory}）");
            // 评分写回需要同目录内新建 .xmp_backup：真机用 devicectl 拷贝的目录属 root，App 无目录写权限。
            // 与 Mac 各验收套件同口径，一律先在工作副本上操作（App 自建目录，属 App UID）。
            string workspace = StageFixtures(directory, fixtures);
            var entrySnapshot = main.Settings.CreateSnapshot();
            try
            {
                await UiDispatcher.InvokeAsync(() =>
                {
                    main.Settings.ShowRating = true;
                    main.Settings.LayoutMode = LayoutMode.Row;
                    main.IsThumbnailViewVisible = true;
                    main.IsControlViewVisible = true;
                    main.FileVM.FilterBar.SelectedRatingFilter = "All";
                });
                await main.FolderVM.OpenFolderAsync(new iOSFolderRef(workspace));
                await WaitAsync(() => main.FolderVM.AllFiles.Count >= 2, "夹具目录枚举");
                await WaitAsync(() => main.CurrentFile != null && session.Image != null, "首图解码");
                await SettleAsync(root);
                lines.Add($"PASS 素材：{fixtures.Length} 张自有夹具（{directory}）→ 工作副本 {workspace}");

                await VerifyPhotoContextMenuAsync(root, main, session, lines);
                await VerifyStatusToastAsync(root, lines);
                await VerifyCardContextMenuAsync(root, main, lines, workspace);
                await VerifySwipeAsync(root, main, lines);
                await VerifyKeyboardAsync(root, main, lines);
                await VerifyDropAsync(root, main, lines, workspace);
                await VerifySettingsPropagationAsync(root, main, lines);
            }
            finally
            {
                await RestoreAsync(root, main, entrySnapshot);
            }
            lines.Add("PASS 收尾还原：设置快照、栏位显隐、筛选、当前照片与评级已回到进入本检查时的状态");
            await File.WriteAllLinesAsync(report, lines);
            Console.WriteLine($"[NativeSmoke] PASS input ({lines.Count} checks)");
        }
        catch (Exception exception)
        {
            lines.Add($"FAIL {exception}");
            await File.WriteAllLinesAsync(report, lines);
            Console.Error.WriteLine("[NativeSmoke] FAIL input " + exception);
        }
    }

    /// <summary>
    /// 主图上下文菜单：经系统交互委托的配置入口读取菜单，核对项集合（只含 iOS 当前可用功能）、
    /// 勾选态与栏位显隐一致，并调用同一份回调核对实际状态变化。
    /// </summary>
    /// <param name="root">原生根控制器。</param>
    /// <param name="main">共享主 VM。</param>
    /// <param name="session">原生展示会话（增强预览勾选态）。</param>
    /// <param name="lines">报告行。</param>
    private static async Task VerifyPhotoContextMenuAsync(iOSNativeRootViewController root, MainViewModel main, PhotoLoadSession session, List<string> lines)
    {
        var built = RequirePhotoMenu(root);
        var actions = Flatten(built.Menu);
        string[] expected = ["photo.open-folder", "photo.film", "photo.analysis", "photo.control", "photo.enhance", "photo.tools", "photo.settings.open"];
        if (!actions.Select(action => action.Id).SequenceEqual(expected))
            throw new InvalidOperationException($"主图菜单项与约定不符：{string.Join(",", actions.Select(action => action.Id))}");
        if (actions.Any(action => !action.HasImage)) throw new InvalidOperationException("主图菜单存在无 SF Symbols 图标的项");
        if (actions[1].State != State(main.IsThumbnailViewVisible) || actions[2].State != State(main.IsAnalysisViewVisible)
            || actions[3].State != State(main.IsControlViewVisible) || actions[4].State != State(session.IsEnhanced))
            throw new InvalidOperationException("主图菜单勾选态与栏位显隐/增强开关不一致");
        lines.Add($"PASS 主图上下文菜单：{actions.Count} 项（{string.Join(" / ", actions.Select(action => action.Title))}），图标与勾选态正确（I5 入口均可用）");

        // MENU-07：无照片时只置灰"雾天增强"，其余项照常可用（工具仍可打开空态）。
        var noPhoto = iOSContextMenus.Photo(true, true, false, false, hasPhoto: false,
            () => { }, () => { }, () => { }, () => { }, () => { }, () => { }, () => { });
        var noPhotoActions = Flatten(noPhoto.Menu);
        if (!noPhotoActions[4].Attributes.HasFlag(UIMenuElementAttributes.Disabled))
            throw new InvalidOperationException("MENU-07：无照片时「雾天增强」仍可用");
        if (noPhotoActions.Where((_, index) => index != 4).Any(action => action.Attributes.HasFlag(UIMenuElementAttributes.Disabled)))
            throw new InvalidOperationException("MENU-07：无照片时除「雾天增强」外的项不应禁用");
        if (noPhotoActions.Single(action => action.Id == "photo.tools").Attributes.HasFlag(UIMenuElementAttributes.Disabled))
            throw new InvalidOperationException("MENU-07：无照片时「工具…」应保持可用（打开空态）");
        var withPhoto = iOSContextMenus.Photo(true, true, false, false, hasPhoto: true,
            () => { }, () => { }, () => { }, () => { }, () => { }, () => { }, () => { });
        if (Flatten(withPhoto.Menu)[4].Attributes.HasFlag(UIMenuElementAttributes.Disabled))
            throw new InvalidOperationException("MENU-07：有照片时「雾天增强」不应禁用");
        lines.Add("PASS MENU-07：无照片时仅「雾天增强」置灰，「工具…」保持可用；有照片时雾天增强恢复可用");

        bool film = main.IsThumbnailViewVisible;
        built.Handlers["photo.film"]();
        await WaitAsync(() => main.IsThumbnailViewVisible != film && root.FileBarView.Hidden == !main.IsThumbnailViewVisible, "胶片栏显隐切换");
        var rebuilt = RequirePhotoMenu(root);
        if (Flatten(rebuilt.Menu)[1].State != State(main.IsThumbnailViewVisible))
            throw new InvalidOperationException("重新弹出菜单后胶片栏勾选态未更新");
        built.Handlers["photo.film"]();
        await WaitAsync(() => main.IsThumbnailViewVisible == film, "胶片栏恢复");

        bool control = main.IsControlViewVisible;
        built.Handlers["photo.control"]();
        await WaitAsync(() => main.IsControlViewVisible != control && root.ControlScroll.Hidden == !main.IsControlViewVisible, "控制栏显隐切换");
        built.Handlers["photo.control"]();
        await WaitAsync(() => main.IsControlViewVisible == control, "控制栏恢复");
        // 分析栏与雾天增强：走菜单同一份回调，共享状态与会话开关随之变化。
        bool analysis = main.IsAnalysisViewVisible;
        built.Handlers["photo.analysis"]();
        await WaitAsync(() => main.IsAnalysisViewVisible != analysis && !root.AnalysisPane.Hidden, "分析栏显隐切换");
        built.Handlers["photo.analysis"]();
        await WaitAsync(() => main.IsAnalysisViewVisible == analysis && root.AnalysisPane.Hidden, "分析栏恢复");
        bool enhanced = session.IsEnhanced;
        built.Handlers["photo.enhance"]();
        await WaitAsync(() => session.IsEnhanced != enhanced, "雾天增强切换");
        built.Handlers["photo.enhance"]();
        await WaitAsync(() => session.IsEnhanced == enhanced, "雾天增强恢复");
        lines.Add("PASS 主图菜单动作：胶片栏/分析栏/控制栏真实切换共享状态与原生视图，雾天增强经展示会话生效并还原，重新弹出时勾选态同步");

        var stub = new RecordingFilePicker();
        FilePicker.Initialize(stub);
        try
        {
            built.Handlers["photo.open-folder"]();
            await WaitAsync(() => stub.FolderRequested, "打开文件夹菜单动作");
        }
        finally
        {
            FilePicker.Initialize(new iOSFilePicker());
        }
        lines.Add("PASS 主图菜单动作：打开文件夹命中原生文件夹选择器（桩实现，免模态）");
    }

    /// <summary>
    /// STATE-03 提示条：评分写回失败/Full 升级失败/内存提示共用同一控件；新提示替换旧提示且不拦触摸。
    /// </summary>
    /// <param name="root">原生根控制器。</param>
    /// <param name="lines">报告行。</param>
    private static async Task VerifyStatusToastAsync(iOSNativeRootViewController root, List<string> lines)
    {
        await UiDispatcher.InvokeAsync(() => root.ShowToast("验收提示：评分写回失败"));
        if (!root.Toast.IsVisible || root.Toast.Message != "验收提示：评分写回失败")
            throw new InvalidOperationException("STATE-03：提示条未显示或文本不正确");
        await UiDispatcher.InvokeAsync(() => root.ShowToast("验收提示：新提示替换旧提示"));
        if (root.Toast.Message != "验收提示：新提示替换旧提示")
            throw new InvalidOperationException("STATE-03：新提示未替换旧提示");
        if (root.Toast.UserInteractionEnabled)
            throw new InvalidOperationException("STATE-03：提示条不应拦截手势");
        await UiDispatcher.InvokeAsync(() => root.Toast.Hide());
        lines.Add("PASS STATE-03：提示条显示、新替旧且不拦触摸（4 s 自动消失由 NSTimer 驱动）");
    }

    /// <summary>
    /// 卡片上下文菜单：菜单标题承载完整文件名与拍摄时间，评分子菜单 0–5 且勾选当前星级；
    /// 调用评分回调后文件读回一致、主图身份不变（[FILM-06]）。
    /// </summary>
    /// <param name="root">原生根控制器。</param>
    /// <param name="main">共享主 VM。</param>
    /// <param name="lines">报告行。</param>
    /// <param name="directory">自有夹具目录。</param>
    private static async Task VerifyCardContextMenuAsync(iOSNativeRootViewController root, MainViewModel main, List<string> lines, string directory)
    {
        var list = main.FileVM.ThumbnailList;
        var current = main.CurrentFile ?? throw new InvalidOperationException("卡片菜单核对缺少当前照片");
        if (!Path.GetFullPath(current.File.PathKey).StartsWith(Path.GetFullPath(directory), StringComparison.Ordinal))
            throw new InvalidOperationException($"卡片菜单核对不在自有夹具目录内：{current.File.PathKey}");
        var entryRatings = main.FolderVM.AllFiles.ToDictionary(file => file, file => file.Rating);
        try
        {            int index = Math.Min(2, list.FilteredFiles.Count - 1);
            var file = list.FilteredFiles[index];
            var card = await WaitForCardAsync(root, list.FilteredFiles, index, "卡片菜单");
            var interaction = card.Surface.Interactions.OfType<UIContextMenuInteraction>().SingleOrDefault()
                ?? throw new InvalidOperationException("卡片未挂上下文菜单交互");
            var delegateForCard = card.MenuDelegate ?? throw new InvalidOperationException("卡片缺少上下文菜单委托");
            var configuration = delegateForCard.GetConfigurationForMenu(interaction, new CGPoint(45, 69));
            if (configuration == null) throw new InvalidOperationException("卡片上下文菜单未给出配置");
            var built = delegateForCard.Menu ?? throw new InvalidOperationException("卡片上下文菜单未构建菜单");
            if (!built.Menu.Title.Contains(file.Name, StringComparison.Ordinal))
                throw new InvalidOperationException($"卡片菜单标题未包含完整文件名：{built.Menu.Title} / {file.Name}");
            if (file.PhotoDate is { } date && !built.Menu.Title.Contains(date.ToString("yyyy-MM-dd HH:mm:ss"), StringComparison.Ordinal))
                throw new InvalidOperationException($"卡片菜单标题未包含拍摄时间：{built.Menu.Title}");
            var ratingMenu = built.Menu.Children.OfType<UIMenu>().SingleOrDefault(menu => menu.Title == "评分")
                ?? throw new InvalidOperationException("卡片菜单缺少评分分组");
            var ratings = ratingMenu.Children.OfType<UIAction>().ToArray();
            if (ratings.Length != 6) throw new InvalidOperationException($"评分子菜单应为 0–5 六项，实际 {ratings.Length}");
            int checkedIndex = Array.FindIndex(ratings, action => action.State == UIMenuElementState.On);
            if (checkedIndex != file.Rating) throw new InvalidOperationException($"评分子菜单勾选 {checkedIndex} 星与卡片 {file.Rating} 星不一致");
            lines.Add($"PASS 卡片上下文菜单：标题「{built.Menu.Title}」含完整文件名与拍摄时间，评分子菜单 0–5 且勾选当前 {file.Rating} 星");

            int target = file.Rating == 3 ? 2 : 3;
            string id = $"card.rating.{target}";
            built.Handlers[id]();
            await WaitAsync(() => file.Rating == target, $"卡片菜单写回 {target} 星");
            var written = await ExifLoader.LoadExifDataAsync(file.File);
            if (written?.Rating != target) throw new InvalidOperationException($"卡片菜单星级未写回文件：memory={file.Rating}, file={written?.Rating}");
            if (!ReferenceEquals(main.CurrentFile, current)) throw new InvalidOperationException("卡片菜单评分改变了当前主图身份");
            delegateForCard.GetConfigurationForMenu(interaction, new CGPoint(45, 69));
            var again = delegateForCard.Menu ?? throw new InvalidOperationException("写回后菜单未重建");
            var againRatings = again.Menu.Children.OfType<UIMenu>().Single(menu => menu.Title == "评分").Children.OfType<UIAction>().ToArray();
            if (againRatings[target].State != UIMenuElementState.On)
                throw new InvalidOperationException("重新弹出卡片菜单后星级勾选未更新");
            lines.Add($"PASS 卡片菜单评分：自有副本读回 {target} 星、主图身份不变、重新弹出时勾选同步（[FILM-06] 暂留由共享列表负责）");
        }
        finally
        {
            foreach (var pair in entryRatings)
            {
                if (pair.Key.Rating != pair.Value) await main.SetRatingAsync(pair.Key, pair.Value);
            }
            if (main.CurrentFile?.File.PathKey != current.File.PathKey) await main.FolderVM.OpenImageAsync(current.File);
        }
    }

    /// <summary>
    /// 捏合/平移驱动回归（作者报告"只有双击有效、捏合与拖动无反应"）：
    /// 滚动视图必须有缩放内容视图（没有它 UIKit 不响应捏合、zoomScale 恒为 1），
    /// 程序化改 zoomScale 后视口缩放必须跟随、改 contentOffset 后视口平移必须跟随，回适应态时缩放回到 1。
    /// </summary>
    /// <param name="root">原生根控制器。</param>
    /// <param name="main">共享主 VM。</param>
    /// <param name="lines">报告行。</param>
    private static async Task VerifyScrollGestureDriverAsync(iOSNativeRootViewController root, MainViewModel main, List<string> lines)
    {
        var photo = root.Photo;
        var scroll = photo.ScrollView;
        await UiDispatcher.InvokeAsync(photo.FitToView);
        await WaitAsync(() => photo.IsFit && HasDisplayedImage(main), "捏合驱动回归的适应态");
        await SettleAsync(root);
        var zoomTarget = scroll.Delegate?.ViewForZoomingInScrollView(scroll)
            ?? throw new InvalidOperationException("滚动视图没有缩放内容视图：UIKit 不会响应捏合，zoomScale 永远不会变化");
        if (zoomTarget.Superview != scroll)
            throw new InvalidOperationException("缩放内容视图不在滚动视图内");
        double fitContentWidth = photo.DisplayWidth * photo.PointScale;
        double fitScale = photo.PointScale;
        if (Math.Abs((double)zoomTarget.Frame.Width - fitContentWidth) > 1)
            throw new InvalidOperationException($"缩放内容视图不是适应态显示尺寸：{zoomTarget.Frame.Width} vs {fitContentWidth:0.##}");
        await UiDispatcher.InvokeAsync(() => scroll.SetZoomScale(2, false));
        await WaitAsync(() => !photo.IsFit && Math.Abs((double)scroll.ZoomScale - 2) < .02, "程序化捏合缩放生效");
        await SettleAsync(root);
        if (Math.Abs(photo.PointScale - fitScale * 2) > fitScale * .05)
            throw new InvalidOperationException($"zoomScale 2 未映射到视口：{photo.PointScale:0.####} vs {fitScale * 2:0.####}");
        double panBefore = photo.Viewport.PanX;
        await UiDispatcher.InvokeAsync(() => scroll.SetContentOffset(new CGPoint((double)scroll.ContentOffset.X + 120, scroll.ContentOffset.Y), false));
        await SettleAsync(root);
        if (Math.Abs(photo.Viewport.PanX - panBefore) < 20)
            throw new InvalidOperationException($"拖动 contentOffset 后视口平移未跟随：{panBefore:0.##} → {photo.Viewport.PanX:0.##}");
        await UiDispatcher.InvokeAsync(photo.FitToView);
        await WaitAsync(() => photo.IsFit && Math.Abs((double)scroll.ZoomScale - 1) < .02, "回适应态后 zoomScale 归 1");
        await SettleAsync(root);
        lines.Add($"PASS 捏合/平移驱动：缩放内容视图 {fitContentWidth:0.##}×{(double)zoomTarget.Frame.Height:0.##}、zoomScale 2 → 视口跟随、contentOffset 拖动 → 平移跟随、回适应归 1（缩放后 {photo.PointScale:0.###}）");
    }

    /// <summary>
    /// 适应态横向轻扫：上一张/下一张经共享命令表并触发显式定位；放大态与不足阈值时不翻页；
    /// 识别器为单指且与滚动视图平移同时识别（捏合起手不会翻页）。
    /// </summary>
    /// <param name="root">原生根控制器。</param>
    /// <param name="main">共享主 VM。</param>
    /// <param name="lines">报告行。</param>
    private static async Task VerifySwipeAsync(iOSNativeRootViewController root, MainViewModel main, List<string> lines)
    {
        var photo = root.Photo;
        var list = main.FileVM.ThumbnailList;
        await VerifyScrollGestureDriverAsync(root, main, lines);
        if (photo.SwipeRecognizer.MinimumNumberOfTouches != 1 || photo.SwipeRecognizer.MaximumNumberOfTouches != 1)
            throw new InvalidOperationException("轻扫识别器不是单指，捏合起手可能误翻页");
        if (photo.DoubleTapRecognizer.NumberOfTapsRequired != 2) throw new InvalidOperationException("主图双击识别器未保持两击");
        if (photo.ScrollView.PinchGestureRecognizer is not { Enabled: true }) throw new InvalidOperationException("滚动视图捏合识别器被禁用");
        if (photo.SwipeRecognizer.Delegate?.ShouldRecognizeSimultaneously(photo.SwipeRecognizer, photo.ScrollView.PanGestureRecognizer) != true)
            throw new InvalidOperationException("轻扫识别器未与滚动视图平移同时识别");
        lines.Add("PASS 轻扫配置：单指上限、与滚动平移同时识别、双击与系统捏合保持可用");

        await UiDispatcher.InvokeAsync(() => main.CurrentFile = list.FilteredFiles[3]);
        await WaitAsync(() => HasDisplayedImage(main), "轻扫目标就绪");
        await UiDispatcher.InvokeAsync(() =>
        {
            photo.FitToView();
            list.ScrollToCurrent();
        });
        await WaitAsync(() => photo.IsFit, "适应态");
        await SettleAsync(root);
        await UiDispatcher.InvokeAsync(() => root.Filmstrip.Collection.SetContentOffset(CGPoint.Empty, false));
        await Task.Delay(80);
        int index = list.FilteredFiles.IndexOf(main.CurrentFile!);
        if (index < 0 || index + 1 >= list.FilteredFiles.Count) throw new InvalidOperationException("轻扫核对需要中间位置的照片");
        double before = MainOffset(root.Filmstrip);

        if (!photo.ApplySwipe(-120, 2, -800)) throw new InvalidOperationException("适应态横向轻扫未触发翻页");
        await WaitAsync(() => list.FilteredFiles.IndexOf(main.CurrentFile!) == index + 1, "轻扫下一张");
        await WaitAsync(() => MainOffsetMatchesCenter(root, main, index + 1), "轻扫后显式定位");
        lines.Add($"PASS 轻扫下一张：{index} → {index + 1}，胶片栏偏移 {before:0.##} → {MainOffset(root.Filmstrip):0.##}（共享 Previous/Next 显式定位）");

        await UiDispatcher.InvokeAsync(() => root.Filmstrip.Collection.SetContentOffset(CGPoint.Empty, false));
        await Task.Delay(80);
        if (!photo.ApplySwipe(150, 0, 900)) throw new InvalidOperationException("反向轻扫未触发上一张");
        await WaitAsync(() => list.FilteredFiles.IndexOf(main.CurrentFile!) == index, "轻扫上一张");
        await WaitAsync(() => MainOffsetMatchesCenter(root, main, index), "反向轻扫后显式定位");
        lines.Add("PASS 轻扫上一张：回到原索引并再次显式定位");

        if (photo.ApplySwipe(8, 0, 200)) throw new InvalidOperationException("低于方向阈值的位移仍触发了翻页");
        if (photo.ApplySwipe(30, 60, 200)) throw new InvalidOperationException("纵向占优的拖动仍触发了翻页");
        if (list.FilteredFiles.IndexOf(main.CurrentFile!) != index) throw new InvalidOperationException("阈值判定后当前照片被意外切换");
        lines.Add("PASS 轻扫阈值：水平位移不足或纵向占优时不翻页");

        // 小夹具在适应态的物理倍率已大于 1，物理 100% 反而在适应态之下；这里用相对放大进入放大态。
        await UiDispatcher.InvokeAsync(() => photo.ZoomBy(2));
        await WaitAsync(() => !photo.IsFit && (double)photo.ScrollView.ZoomScale > 1.5, "放大态");
        if (photo.ApplySwipe(-160, 0, -900)) throw new InvalidOperationException("放大态横向拖动仍触发了翻页");
        if (list.FilteredFiles.IndexOf(main.CurrentFile!) != index) throw new InvalidOperationException("放大态拖动切换了当前照片");
        lines.Add("PASS 放大态：横向拖动保持平移语义，不翻页");
        await UiDispatcher.InvokeAsync(() => photo.FitToView());
        await WaitAsync(() => photo.IsFit, "恢复适应态");
    }

    /// <summary>
    /// 硬件键盘：HID usage 映射表（方向/字母/数字/±/修饰键/数字键盘）与默认绑定往返一致；
    /// 合成派发走向共享命令表并触发显式定位；苹果键盘映射按用户设置生效；INPUT-01 守卫拦截文本输入与模态。
    /// </summary>
    /// <param name="root">原生根控制器。</param>
    /// <param name="main">共享主 VM。</param>
    /// <param name="lines">报告行。</param>
    private static async Task VerifyKeyboardAsync(iOSNativeRootViewController root, MainViewModel main, List<string> lines)
    {
        (int Usage, GestureKey Key)[] table =
        [
            (79, GestureKey.Right), (80, GestureKey.Left), (81, GestureKey.Down), (82, GestureKey.Up),
            (4, GestureKey.A), (29, GestureKey.Z), (30, GestureKey.D1), (38, GestureKey.D9), (39, GestureKey.D0),
            (45, GestureKey.OemMinus), (46, GestureKey.OemPlus), (53, GestureKey.OemTilde),
            (51, GestureKey.OemSemicolon), (52, GestureKey.OemQuotes), (54, GestureKey.OemComma),
            (55, GestureKey.OemPeriod), (56, GestureKey.OemQuestion),
            (40, GestureKey.Return), (41, GestureKey.Escape), (42, GestureKey.Back), (43, GestureKey.Tab), (44, GestureKey.Space),
            (58, GestureKey.F1), (69, GestureKey.F12), (62, GestureKey.F5),
            (74, GestureKey.Home), (75, GestureKey.PageUp), (76, GestureKey.Delete), (77, GestureKey.End), (78, GestureKey.PageDown),
            (87, GestureKey.Add), (86, GestureKey.Subtract), (84, GestureKey.Divide), (85, GestureKey.Multiply), (99, GestureKey.Decimal),
            (89, GestureKey.NumPad1), (97, GestureKey.NumPad9), (98, GestureKey.NumPad0), (88, GestureKey.Return),
            (224, GestureKey.LeftCtrl), (225, GestureKey.LeftShift), (226, GestureKey.LeftAlt), (227, GestureKey.LWin),
            (228, GestureKey.RightCtrl), (229, GestureKey.RightShift), (230, GestureKey.RightAlt), (231, GestureKey.RWin),
        ];
        foreach (var (usage, key) in table)
        {
            if (AppleInputMapping.KeyFromHidUsage(usage) != key)
                throw new InvalidOperationException($"HID {usage} 未映射到 {key}（实际 {AppleInputMapping.KeyFromHidUsage(usage)?.ToString() ?? "null"}）");
        }
        if (AppleInputMapping.KeyFromHidUsage(50) != null) throw new InvalidOperationException("未登记的 HID 50 不应映射到共享键");
        lines.Add($"PASS HID 映射表：{table.Length} 个键位（方向/字母/数字/±/符号/功能键/编辑键/数字键盘/修饰键）与共享键一一对应，未登记 usage 返回 null");

        (int Usage, string Command)[] bindings = [(80, "Previous"), (79, "Next"), (9, "Fit"), (29, "Exchange"), (30, "SetRating1")];
        foreach (var (usage, command) in bindings)
        {
            var resolved = InputCommandMap.CommandForGesture(AppleInputMapping.FromHidUsage(usage, 0, main.Settings), main.Settings);
            if (resolved != command) throw new InvalidOperationException($"HID {usage} 往返应解析到 {command}，实际 {resolved ?? "null"}");
        }
        lines.Add("PASS 键映射往返：方向键/F/Z/数字键经共享手势查用户绑定，与默认命令表一致");

        bool appleKeyboard = main.Settings.UseAppleKeyboard;
        try
        {
            await UiDispatcher.InvokeAsync(() => main.Settings.UseAppleKeyboard = true);
            var mapped = AppleInputMapping.FromHidUsage(80, AppleInputMapping.CommandMask, main.Settings);
            var expected = AppleKeyboardMapping.ApplyForRuntime(GestureModifiers.Meta, main.Settings.UseAppleKeyboard,
                main.Settings.MapCommandTarget, main.Settings.MapOptionTarget, main.Settings.MapControlTarget);
            if (mapped?.Key?.Modifiers != expected) throw new InvalidOperationException($"Cmd 映射未按用户设置生效：{mapped?.Key?.Modifiers} != {expected}");
            if (InputCommandMap.CommandForGesture(mapped, main.Settings) != null)
                throw new InvalidOperationException("映射后的 Cmd+← 不应命中默认无修饰的上一张绑定");
            await UiDispatcher.InvokeAsync(() => main.Settings.UseAppleKeyboard = false);
            var raw = AppleInputMapping.FromHidUsage(80, AppleInputMapping.CommandMask, main.Settings);
            if (raw?.Key?.Modifiers != GestureModifiers.Meta) throw new InvalidOperationException("关闭苹果键盘映射后 Cmd 未保持 Meta");
        }
        finally
        {
            await UiDispatcher.InvokeAsync(() => main.Settings.UseAppleKeyboard = appleKeyboard);
        }
        lines.Add("PASS 苹果键盘映射：Cmd 按 MapCommandTarget 映射后不再误命中无修饰绑定，关闭映射后恢复原修饰键");

        var list = main.FileVM.ThumbnailList;
        await UiDispatcher.InvokeAsync(() => main.CurrentFile = list.FilteredFiles[1]);
        await WaitAsync(() => HasDisplayedImage(main), "键盘目标就绪");
        await UiDispatcher.InvokeAsync(() => { root.Photo.FitToView(); list.ScrollToCurrent(); });
        await WaitAsync(() => root.Photo.IsFit, "键盘核对适应态");
        await SettleAsync(root);
        await UiDispatcher.InvokeAsync(() => root.Filmstrip.Collection.SetContentOffset(CGPoint.Empty, false));
        await Task.Delay(80);
        int index = list.FilteredFiles.IndexOf(main.CurrentFile!);
        if (!root.TryExecuteKey(79, 0)) throw new InvalidOperationException("合成派发：→ 未命中共享命令表");
        await WaitAsync(() => list.FilteredFiles.IndexOf(main.CurrentFile!) == index + 1, "键盘下一张");
        await WaitAsync(() => MainOffsetMatchesCenter(root, main, index + 1), "键盘显式定位");
        lines.Add($"PASS 合成派发：HID 79（→）执行共享 Next，当前照片 {index} → {index + 1} 且胶片栏显式定位");
        if (!root.TryExecuteKey(80, 0)) throw new InvalidOperationException("合成派发：← 未命中共享命令表");
        await WaitAsync(() => list.FilteredFiles.IndexOf(main.CurrentFile!) == index, "键盘上一张");
        lines.Add("PASS 合成派发：HID 80（←）执行共享 Previous");

        var field = new UITextField(new CGRect(0, 0, 10, 10));
        root.View!.AddSubview(field);
        try
        {
            field.BecomeFirstResponder();
            await WaitAsync(() => root.KeyboardInputBlocked, "文本输入获焦");
            if (root.TryExecuteKey(79, 0)) throw new InvalidOperationException("INPUT-01：文本输入获焦时仍派发了照片快捷键");
            lines.Add("PASS INPUT-01：文本输入获焦时不派发照片快捷键");
        }
        finally
        {
            field.ResignFirstResponder();
            field.RemoveFromSuperview();
            field.Dispose();
        }
        await WaitAsync(() => !root.KeyboardInputBlocked, "文本输入失焦");

        var modal = new UIViewController();
        root.PresentViewController(modal, true, null);
        try
        {
            await WaitAsync(() => root.PresentedViewController == modal, "模态展示");
            if (root.TryExecuteKey(79, 0)) throw new InvalidOperationException("INPUT-01：模态展示期间仍派发了照片快捷键");
            lines.Add("PASS INPUT-01：模态（设置页/选择器等）展示期间不派发照片快捷键");
        }
        finally
        {
            root.DismissViewController(true, null);
            await WaitAsync(() => root.PresentedViewController == null, "模态关闭");
        }

        if (root.Photo.WheelRecognizer.AllowedScrollTypesMask != UIScrollTypeMask.Discrete ||
            root.Photo.WheelRecognizer.MinimumNumberOfTouches != 0 || root.Photo.WheelRecognizer.MaximumNumberOfTouches != 0)
            throw new InvalidOperationException("滚轮识别器未限定为离散滚动");
        var wheel = AppleInputMapping.FromRaw(AppleInputMapping.ScrollWheel, 0, 0, 0, 1, false, null, main.Settings);
        if (InputCommandMap.CommandForGesture(wheel, main.Settings) != "Previous")
            throw new InvalidOperationException("滚轮向上的默认绑定未解析到上一张");
        var wheelDown = AppleInputMapping.FromRaw(AppleInputMapping.ScrollWheel, 0, 0, 0, -1, false, null, main.Settings);
        if (InputCommandMap.CommandForGesture(wheelDown, main.Settings) != "Next")
            throw new InvalidOperationException("滚轮向下的默认绑定未解析到下一张");
        lines.Add("PASS 离散滚轮：识别器只收离散滚动（触控板连续滚动/捏合仍归滚动视图），默认绑定解析为上一张/下一张");
        // INPUT-05：只有固定节奏有意义的命令才重复；节奏取设计令牌（350ms/60ms）。
        string[] repeatable = ["Previous", "Next", "ZoomInPreset", "ZoomOutPreset"];
        foreach (string command in repeatable)
            if (!InputCommandMap.IsRepeatable(command)) throw new InvalidOperationException($"INPUT-05：{command} 应可重复");
        foreach (string command in new[] { "Fit", "Exchange", "ToggleEnhance", "SetRating1", "Open" })
            if (InputCommandMap.IsRepeatable(command)) throw new InvalidOperationException($"INPUT-05：{command} 不应进入按住重复");
        lines.Add("PASS INPUT-05：仅 Previous/Next/ZoomInPreset/ZoomOutPreset 进入固定节奏重复（350ms/60ms 令牌）");
        lines.Add("NOTE UIPress 无法合成，本检查未直接测量重复定时器的实际触发节奏");
    }

    /// <summary>
    /// 拖入：拖放交互挂在根视图；文件 URL 经共享 <c>ExternalOpenService</c> 打开并切换当前照片
    /// （拖入会话本身无法合成，直接调用同一落地入口）。
    /// </summary>
    /// <param name="root">原生根控制器。</param>
    /// <param name="main">共享主 VM。</param>
    /// <param name="lines">报告行。</param>
    /// <param name="directory">自有夹具目录。</param>
    private static async Task VerifyDropAsync(iOSNativeRootViewController root, MainViewModel main, List<string> lines, string directory)
    {
        if (!root.View!.Interactions.Contains(root.DropInteraction))
            throw new InvalidOperationException("拖放交互未挂在主屏根视图");
        var current = main.CurrentFile ?? throw new InvalidOperationException("拖入核对缺少当前照片");
        var target = main.FolderVM.AllFiles.First(file => !ReferenceEquals(file, current) && file.File.PathKey.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase));
        using var url = NSUrl.CreateFileUrl(target.File.PathKey, false, null);
        if (url == null) throw new InvalidOperationException("拖入用文件 URL 创建失败");
        if (!root.PublishDroppedUrlsAsync([url])) throw new InvalidOperationException("拖入文件 URL 未被外部打开链路接收");
        await WaitAsync(() => main.CurrentFile?.File.PathKey == target.File.PathKey, "拖入文件切图");
        lines.Add($"PASS 拖入：{target.Name} 经共享 ExternalOpenService 打开并成为当前照片（拖放会话本身不可合成，走同一落地入口）");
    }

    /// <summary>
    /// 设置写回传播：从主图菜单真实弹出设置页，改写工作副本后经关闭回写路径应用，
    /// 逐项核对布局模式、控制栏命令显隐/顺序、快捷键重绑、评分与 EXIF 分组，以及文件格式筛选反映到胶片栏。
    /// </summary>
    /// <param name="root">原生根控制器。</param>
    /// <param name="main">共享主 VM。</param>
    /// <param name="lines">报告行。</param>
    private static async Task VerifySettingsPropagationAsync(iOSNativeRootViewController root, MainViewModel main, List<string> lines)
    {
        // 第一轮：布局模式 / 控制栏命令显隐与顺序 / 快捷键重绑 / 评分与 EXIF 显隐。
        var first = await OpenSettingsAsync(root, lines);
        await iOSNativeSettingsCheck.RunAsync(first, lines);
        bool expectedRow = !main.IsRowLayout;
        var working = first.Session.WorkingSettings;
        var next = working.Hotkeys.First(hotkey => hotkey.Command == "Next");
        var zoomScale = working.Hotkeys.First(hotkey => hotkey.Command == "ZoomInScale");
        await UiDispatcher.InvokeAsync(() =>
        {
            working.LayoutMode = expectedRow ? LayoutMode.Row : LayoutMode.Col;
            zoomScale.IsDisplay = true;
            working.MoveHotkey(working.Hotkeys.IndexOf(zoomScale), 0);
            next.PrimaryHotkey = AppGesture.FromKey(new AppKeyGesture(GestureKey.F5));
            working.ShowRating = false;
            foreach (var item in working.ExifDisplayItems) item.IsEnabled = false;
        });
        if (root.TryExecuteKey(79, 0)) throw new InvalidOperationException("INPUT-01：设置页展示期间仍派发了照片快捷键");
        await ApplyAndDismissSettingsAsync(root, first);

        await WaitAsync(() => main.IsRowLayout == expectedRow, "布局模式传播");
        await WaitAsync(() => root.ControlBar.Commands.ContainsKey("ZoomInScale")
            && root.ControlBar.Commands.Keys.First() == "ZoomInScale", "控制栏命令显隐与顺序传播");
        await WaitAsync(() => main.Settings.Hotkeys.First(hotkey => hotkey.Command == "Next").PrimaryHotkey?.Key?.Key == GestureKey.F5, "快捷键绑定传播");
        await WaitAsync(() => root.ControlBar.Ratings.Count == 0 && main.ControlVM.EnabledExifItems.Count == 0, "评分与 EXIF 分组传播");
        lines.Add($"PASS 设置写回①：布局模式→{(expectedRow ? "行" : "列")}、控制栏命令显隐/顺序（ZoomInScale 置顶）、Next 重绑 F5、评分与 EXIF 分组即时消失");
        RequireControlBarStructure(root, main, "设置写回①");

        // 控制栏重建幂等性：反复改变 EXIF 项与命令显隐（正是作者报告的"多块 EXIF/按钮重叠"触发路径），
        // 每次重建后核对子视图数 == 分组数、无旧分组残留、分组与组内控件都不重叠。
        var fit = main.Settings.Hotkeys.First(hotkey => hotkey.Command == "Fit");
        for (int round = 0; round < 3; round++)
        {
            int exifCount = round == 1 ? 2 : round == 2 ? 7 : 11;
            bool rating = round != 1;
            bool fitVisible = round == 2;
            await UiDispatcher.InvokeAsync(() =>
            {
                for (int index = 0; index < main.Settings.ExifDisplayItems.Count; index++)
                    main.Settings.ExifDisplayItems[index].IsEnabled = index < exifCount;
                main.Settings.ShowRating = rating;
                fit.IsDisplay = fitVisible;
            });
            await WaitAsync(() => main.ControlVM.EnabledExifItems.Count == exifCount
                && root.ControlBar.Subviews.Length == root.ControlBar.Groups.Count, $"控制栏第 {round + 1} 轮重建");
            await SettleAsync(root);
            RequireControlBarStructure(root, main, $"控制栏重建第 {round + 1} 轮");
        }
        lines.Add("PASS 控制栏重建：EXIF 项/评分/命令显隐连续三轮切换后子视图数始终等于分组数，无旧分组残留、分组与组内控件零重叠");

        var list = main.FileVM.ThumbnailList;
        int index = list.FilteredFiles.IndexOf(main.CurrentFile!);
        if (index < 0 || index + 1 >= list.FilteredFiles.Count) throw new InvalidOperationException("快捷键重绑核对需要中间位置的照片");
        if (root.TryExecuteKey(79, 0)) throw new InvalidOperationException("旧绑定（→）在重绑后仍然命中");
        if (!root.TryExecuteKey(62, 0)) throw new InvalidOperationException("重绑后的 F5 未执行下一张");
        await WaitAsync(() => list.FilteredFiles.IndexOf(main.CurrentFile!) == index + 1, "重绑快捷键执行");
        lines.Add("PASS 设置写回①：重绑立即生效——→ 不再命中，F5 执行下一张");

        // 第二轮：文件格式筛选反映到胶片栏列表与计数。
        var second = await OpenSettingsAsync(root, lines);
        var jpg = second.Session.WorkingSettings.FileFormats.FirstOrDefault(format =>
            format.Extensions.Contains(".jpg", StringComparer.OrdinalIgnoreCase)) ?? throw new InvalidOperationException("设置里找不到 JPG 格式项");
        await UiDispatcher.InvokeAsync(() => jpg.IsEnabled = false);
        await ApplyAndDismissSettingsAsync(root, second);
        await WaitAsync(() => list.FilteredFiles.Count == 0, "文件格式筛选传播");
        if (main.Settings.SelectedFormats.Contains(".jpg"))
            throw new InvalidOperationException("JPG 关闭后仍出现在已选格式中");
        lines.Add($"PASS 设置写回②：关闭 JPG 后胶片栏筛选列表即时清空（计数 {list.FilteredFiles.Count}），已选格式同步");
    }

    /// <summary>
    /// 控制栏重建后的结构核对（回归"设置回写后多块 EXIF/按钮重叠"）：
    /// 视图树里只能有一套分组（子视图数 == 分组数）、分组顺序与共享设置一致、分组之间与组内控件之间零重叠。
    /// </summary>
    /// <param name="root">原生根控制器。</param>
    /// <param name="main">共享主 VM。</param>
    /// <param name="label">报告/报错标签。</param>
    private static void RequireControlBarStructure(iOSNativeRootViewController root, MainViewModel main, string label)
    {
        var bar = root.ControlBar;
        var groups = bar.Groups.ToArray();
        int expected = (main.ControlVM.EnabledExifItems.Count > 0 ? 1 : 0)
            + (main.Settings.ShowRating ? 1 : 0)
            + (main.ControlVM.EnabledControls.Count(hotkey => AppleSymbolNames.ForCommand(hotkey.Command) != null) > 0 ? 1 : 0);
        if (groups.Length != expected)
            throw new InvalidOperationException($"{label} 控制栏分组数 {groups.Length} != 期望 {expected}");
        if (bar.Subviews.Length != groups.Length)
            throw new InvalidOperationException($"{label} 控制栏残留旧分组：子视图 {bar.Subviews.Length} 个 / 分组 {groups.Length} 个");
        foreach (var group in groups)
        {
            if (!ReferenceEquals(group.Superview, bar))
                throw new InvalidOperationException($"{label} 分组不在控制栏视图树内：{group.Frame}");
            var children = group.Subviews;
            foreach (var child in children)
                if ((double)child.Frame.X < -Exact || (double)child.Frame.Y < -Exact
                    || (double)child.Frame.Right > (double)group.Bounds.Width + Exact
                    || (double)child.Frame.Bottom > (double)group.Bounds.Height + Exact)
                    throw new InvalidOperationException($"{label} 组内控件越出分组：{child.Frame} / {group.Bounds}");
            for (int first = 0; first < children.Length; first++)
                for (int second = first + 1; second < children.Length; second++)
                    if (Overlaps(children[first].Frame, children[second].Frame))
                        throw new InvalidOperationException($"{label} 组内控件重叠：{children[first].Frame} × {children[second].Frame}");
        }
        for (int first = 0; first < groups.Length; first++)
            for (int second = first + 1; second < groups.Length; second++)
                if (Overlaps(groups[first].Frame, groups[second].Frame))
                    throw new InvalidOperationException($"{label} 控制栏分组重叠：{groups[first].Frame} × {groups[second].Frame}");
    }

    /// <summary>两个矩形是否真实相交（忽略零尺寸与 0.5 点以内的贴边）。</summary>
    /// <param name="first">第一个矩形。</param>
    /// <param name="second">第二个矩形。</param>
    /// <returns>重叠面积大于零时为 true。</returns>
    private static bool Overlaps(CGRect first, CGRect second)
        => (double)first.Width > Exact && (double)first.Height > Exact && (double)second.Width > Exact && (double)second.Height > Exact
            && (double)first.X < (double)second.Right - Exact && (double)second.X < (double)first.Right - Exact
            && (double)first.Y < (double)second.Bottom - Exact && (double)second.Y < (double)first.Bottom - Exact;

    /// <summary>
    /// 从主图菜单动作打开设置页并定位到编辑会话；返回设置根控制器（其 <c>Session</c> 是测试缝）。
    /// </summary>
    /// <param name="root">原生根控制器。</param>
    /// <param name="lines">报告行。</param>
    /// <returns>设置根控制器。</returns>
    private static async Task<iOSNativeSettingsRootViewController> OpenSettingsAsync(iOSNativeRootViewController root, List<string> lines)
    {
        RequirePhotoMenu(root).Handlers["photo.settings.open"]();
        await WaitAsync(() => root.PresentedViewController is UINavigationController, "设置页展示");
        var navigation = (UINavigationController)root.PresentedViewController!;
        var settingsRoot = (navigation.ViewControllers ?? []).OfType<iOSNativeSettingsRootViewController>().FirstOrDefault()
            ?? throw new InvalidOperationException("设置页根控制器类型不符");
        await WaitAsync(() => navigation.PresentationController?.Delegate is iOSNativeSettingsDismissDelegate,
            $"设置页关闭回写代理挂接（nav={navigation.PresentationController != null}，" +
            $"root={settingsRoot.PresentationController != null}，" +
            $"delegate={navigation.PresentationController?.Delegate?.GetType().Name ?? "null"}）");
        lines.Add("PASS 设置入口：主图菜单动作弹出原生设置页，关闭回写代理已挂接");
        return settingsRoot;
    }

    /// <summary>经关闭回写代理应用工作副本（与系统下拉关闭同一回调），再关闭设置页并等待主屏沉淀。</summary>
    /// <param name="root">原生根控制器。</param>
    /// <param name="settingsRoot">设置根控制器。</param>
    private static async Task ApplyAndDismissSettingsAsync(iOSNativeRootViewController root, iOSNativeSettingsRootViewController settingsRoot)
    {
        var presentation = settingsRoot.NavigationController?.PresentationController
            ?? throw new InvalidOperationException("设置页缺少 presentation controller");
        if (presentation.Delegate is iOSNativeSettingsDismissDelegate dismiss) dismiss.DidDismiss(presentation);
        else throw new InvalidOperationException("设置页关闭代理类型不符");
        await UiDispatcher.InvokeAsync(() => root.DismissViewController(true, null));
        await WaitAsync(() => root.PresentedViewController == null, "设置页关闭");
        await SettleAsync(root);
    }

    /// <summary>收尾：恢复进入检查时的设置快照、栏位显隐、筛选与当前照片。</summary>
    /// <param name="root">原生根控制器。</param>
    /// <param name="main">共享主 VM。</param>
    /// <param name="snapshot">进入检查时的设置快照。</param>
    private static async Task RestoreAsync(iOSNativeRootViewController root, MainViewModel main, SettingsModel snapshot)
    {
        await UiDispatcher.InvokeAsync(() =>
        {
            main.Settings.ApplySnapshot(snapshot);
            main.IsThumbnailViewVisible = true;
            main.IsControlViewVisible = true;
            main.FileVM.FilterBar.SelectedRatingFilter = "All";
        });
        await SettleAsync(root);
        // 验收进程随后会被强杀；同步落盘避免"测试中临时关闭文件格式"被遗留为持久状态。
        PhotoViewer.Core.Settings.SettingsService.Instance.SaveAsync(main.Settings.CreateSnapshot()).GetAwaiter().GetResult();
    }

    /// <summary>经主图交互委托重建一次菜单（等价于一次新的长按弹出），返回菜单与回调。</summary>
    /// <param name="root">原生根控制器。</param>
    /// <returns>菜单构建产物。</returns>
    private static iOSContextMenus.MenuResult RequirePhotoMenu(iOSNativeRootViewController root)
    {
        var interaction = root.PhotoPane.Interactions.OfType<UIContextMenuInteraction>().SingleOrDefault()
            ?? throw new InvalidOperationException("主图区未挂上下文菜单交互");
        var configuration = root.PhotoMenuDelegate.GetConfigurationForMenu(interaction, new CGPoint(
            (double)root.PhotoPane.Bounds.Width / 2, (double)root.PhotoPane.Bounds.Height / 2));
        if (configuration == null) throw new InvalidOperationException("主图上下文菜单未给出配置");
        return root.PhotoMenuDelegate.Menu ?? throw new InvalidOperationException("主图上下文菜单未构建菜单");
    }

    /// <summary>展平菜单为可核对的 action 列表（含内联分组）。</summary>
    /// <param name="menu">菜单。</param>
    /// <returns>按显示顺序的 action 列表。</returns>
    private static List<(string Id, string Title, UIMenuElementState? State, bool HasImage, UIMenuElementAttributes Attributes)> Flatten(UIMenu menu)
    {
        var result = new List<(string, string, UIMenuElementState?, bool, UIMenuElementAttributes)>();
        foreach (var child in menu.Children)
        {
            if (child is UIMenu submenu)
            {
                result.AddRange(Flatten(submenu));
                continue;
            }
            if (child is not UIAction action) continue;
            result.Add((action.Identifier ?? "", action.Title, action.State, action.Image != null, action.Attributes));
        }
        return result;
    }

    /// <summary>布尔勾选态 → 菜单元素状态。</summary>
    /// <param name="value">是否勾选。</param>
    /// <returns>系统状态值。</returns>
    private static UIMenuElementState State(bool value) => value ? UIMenuElementState.On : UIMenuElementState.Off;

    /// <summary>胶片栏当前主轴偏移。</summary>
    /// <param name="filmstrip">胶片栏。</param>
    /// <returns>主轴偏移。</returns>
    private static double MainOffset(iOSFilmstripView filmstrip)
        => filmstrip.Horizontal ? (double)filmstrip.ContentOffset.X : (double)filmstrip.ContentOffset.Y;

    /// <summary>胶片栏偏移是否已落在目标索引的居中位置（与共享 <see cref="FilmstripGeometry.CenterOffset"/> 同口径）。</summary>
    /// <param name="root">原生根控制器。</param>
    /// <param name="main">共享主 VM。</param>
    /// <param name="index">业务索引。</param>
    /// <returns>是否已居中（允许 1.5 点误差）。</returns>
    private static bool MainOffsetMatchesCenter(iOSNativeRootViewController root, MainViewModel main, int index)
    {
        if (index < 0 || index >= main.FileVM.ThumbnailList.FilteredFiles.Count) return false;
        if (root.Filmstrip.FrameAt(index) is not { } frame) return false;
        var document = root.Filmstrip.DocumentSize;
        var viewport = root.Filmstrip.Bounds.Size;
        bool horizontal = root.Filmstrip.Horizontal;
        double expected = FilmstripGeometry.CenterOffset(
            horizontal ? (double)frame.X : (double)frame.Y,
            horizontal ? (double)frame.Width : (double)frame.Height,
            horizontal ? (double)viewport.Width : (double)viewport.Height,
            horizontal ? (double)document.Width : (double)document.Height);
        return Math.Abs(MainOffset(root.Filmstrip) - expected) <= CenterTolerance;
    }

    /// <summary>当前照片是否已解码上屏。</summary>
    /// <param name="main">共享主 VM。</param>
    /// <returns>会话产物可用时为 true。</returns>
    private static bool HasDisplayedImage(MainViewModel main) => iOSNativeShell.Session?.Image != null && main.CurrentFile != null;

    /// <summary>等待主屏与列表沉淀一轮布局与共享通知。</summary>
    /// <param name="root">原生根控制器。</param>
    private static async Task SettleAsync(iOSNativeRootViewController root)
    {
        await Task.Delay(Settle);
        await UiDispatcher.InvokeAsync(() =>
        {
            root.View!.LayoutIfNeeded();
            root.Filmstrip.Collection.LayoutIfNeeded();
        });
        await Task.Delay(60);
    }

    /// <summary>等待目标索引的卡片被真实创建并绑定到该业务身份。</summary>
    /// <param name="root">原生根控制器。</param>
    /// <param name="files">当前筛选列表。</param>
    /// <param name="index">目标业务索引。</param>
    /// <param name="scope">报告范围标签。</param>
    /// <returns>已绑定的卡片。</returns>
    private static async Task<iOSThumbnailCard> WaitForCardAsync(iOSNativeRootViewController root, IReadOnlyList<ImageFile> files, int index, string scope)
    {
        if (index < 0 || index >= files.Count) throw new InvalidOperationException($"{scope} 目标索引越界：{index}/{files.Count}");
        var deadline = DateTime.UtcNow.AddSeconds(5);
        if (!root.Filmstrip.Collection.IndexPathsForVisibleItems.Any(path => (int)path.Item == index))
        {
            using var path = NSIndexPath.FromItemSection(index, 0);
            root.Filmstrip.Collection.ScrollToItem(path, UICollectionViewScrollPosition.None, false);
        }
        while (true)
        {
            root.Filmstrip.Collection.LayoutIfNeeded();
            if (root.Filmstrip.CardAt(index) is { } card && ReferenceEquals(card.BoundFile, files[index])) return card;
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException($"{scope} 胶片栏卡片未创建（索引 {index}）");
            await Task.Delay(25);
        }
    }

    /// <summary>轮询条件并设有限超时，失败即抛出不输出虚假通过。</summary>
    /// <param name="condition">条件。</param>
    /// <param name="name">条件名称（超时信息用）。</param>
    private static async Task WaitAsync(Func<bool> condition, string name)
    {
        var watch = Stopwatch.StartNew();
        while (!condition())
        {
            if (watch.Elapsed > TimeSpan.FromSeconds(20)) throw new TimeoutException(name);
            await Task.Delay(20);
        }
    }

    /// <summary>记录文件夹选择器是否被调用的桩实现（避免验收弹出模态选择器）。</summary>
    private sealed class RecordingFilePicker : IFilePickerService
    {
        /// <summary>是否请求过文件夹选择。</summary>
        internal bool FolderRequested { get; private set; }

        /// <summary>记录文件夹请求并立即取消。</summary>
        /// <param name="title">标题。</param>
        /// <returns>null（用户取消）。</returns>
        public Task<IFolderRef?> PickFolderAsync(string title)
        {
            FolderRequested = true;
            return Task.FromResult<IFolderRef?>(null);
        }

        /// <summary>记录文件请求并立即取消。</summary>
        /// <param name="title">标题。</param>
        /// <param name="filters">过滤项。</param>
        /// <returns>null（用户取消）。</returns>
        public Task<IFileRef?> PickFileAsync(string title, IReadOnlyList<FilePickFilter> filters)
        {
            FolderRequested = true;
            return Task.FromResult<IFileRef?>(null);
        }
    }
}
