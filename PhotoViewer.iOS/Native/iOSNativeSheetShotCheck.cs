using CoreGraphics;
using Foundation;
using PhotoViewer.Core.Image;
using PhotoViewer.Core.Platform;
using PhotoViewer.iOS.Core;
using PhotoViewer.ViewModels.Main;
using UIKit;

namespace PhotoViewer.iOS.Native;

/// <summary>
/// 验收专用（测试钩子）：<c>--pv-native-sheet-shots &lt;目录&gt;</c> 在同一张照片上依次打开设置根页、
/// 设置子页（文件 / 预览 / 控制）与工具页（列表 / EXIF / DINO），把每个页面停在 <c>--pv-native-ui-snapshots</c> 检查点，
/// 供外部截图比对 sheet 背景。只读素材，不写用户照片。
/// </summary>
internal static class iOSNativeSheetShotCheck
{
    /// <summary>验收开关。</summary>
    private const string Switch = "--pv-native-sheet-shots";

    /// <summary>页面停留时间：覆盖外部截图与系统动画。</summary>
    private static readonly TimeSpan StageHold = TimeSpan.FromMilliseconds(3000);

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
        _ = RunAsync(root, session, Path.IsPathRooted(directory)
            ? directory
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), directory));
    }

    /// <summary>逐页打开并以固定检查点名称停留。</summary>
    /// <param name="root">原生根控制器。</param>
    /// <param name="session">原生展示会话。</param>
    /// <param name="directory">自有夹具目录（绝对路径）。</param>
    private static async Task RunAsync(iOSNativeRootViewController root, PhotoLoadSession session, string directory)
    {
        string output = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "PhotoViewer");
        Directory.CreateDirectory(output);
        string report = Path.Combine(output, "native-sheet-shots.txt");
        var lines = new List<string>();
        try
        {
            var main = iOSNativeShell.Main ?? throw new InvalidOperationException("原生壳主 VM 未就绪");
            await WaitAsync(() => root.ViewIfLoaded?.Window != null && root.View!.Bounds.Width > 100, "主屏上屏");
            await main.FolderVM.OpenFolderAsync(new iOSFolderRef(directory));
            await WaitAsync(() => main.CurrentFile != null && session.Image != null, "首图解码");
            await Task.Delay(600);

            main.OpenSettingWindow();
            await WaitAsync(() =>
                root.PresentedViewController is UINavigationController navigation
                && navigation.ViewControllers?.FirstOrDefault() is iOSNativeSettingsRootViewController, "设置页展示");
            var settingsNavigation = (UINavigationController)root.PresentedViewController!;
            var settingsRoot = (iOSNativeSettingsRootViewController)settingsNavigation.ViewControllers![0];
            await HoldAsync("settings-root", lines);

            await UiDispatcher.InvokeAsync(() => settingsRoot.RowSelected(settingsRoot.TableView, NSIndexPath.FromRowSection(0, 0)));
            await WaitAsync(() => settingsNavigation.TopViewController is iOSNativeFileSettingsViewController, "设置文件页");
            await Task.Delay(400);
            // 滚到页尾，让「缓存与预加载」分组与两条预算提示完整进画面
            await UiDispatcher.InvokeAsync(() =>
            {
                var scroll = settingsNavigation.TopViewController!.View!.Subviews.OfType<UIScrollView>().Single();
                double bottom = Math.Max(0, (double)scroll.ContentSize.Height - (double)scroll.Bounds.Height);
                scroll.SetContentOffset(new CGPoint(0, bottom), false);
            });
            await Task.Delay(300);
            await HoldAsync("settings-file", lines);
            await UiDispatcher.InvokeAsync(() => settingsNavigation.PopViewController(false));

            await UiDispatcher.InvokeAsync(() => settingsRoot.RowSelected(settingsRoot.TableView, NSIndexPath.FromRowSection(1, 0)));
            await WaitAsync(() => settingsNavigation.TopViewController is iOSNativePreviewSettingsViewController, "设置预览页");
            await Task.Delay(400);
            await HoldAsync("settings-preview", lines);
            await UiDispatcher.InvokeAsync(() =>
            {
                var scroll = settingsNavigation.TopViewController!.View!.Subviews.OfType<UIScrollView>().Single();
                double bottom = Math.Max(0, (double)scroll.ContentSize.Height - (double)scroll.Bounds.Height);
                scroll.SetContentOffset(new CoreGraphics.CGPoint(0, bottom), false);
            });
            await Task.Delay(300);
            await HoldAsync("settings-presets", lines);
            await UiDispatcher.InvokeAsync(() => settingsNavigation.PopViewController(false));

            await UiDispatcher.InvokeAsync(() => settingsRoot.RowSelected(settingsRoot.TableView, NSIndexPath.FromRowSection(2, 0)));
            await WaitAsync(() => settingsNavigation.TopViewController is iOSNativeControlSettingsViewController, "控制设置页");
            var control = (iOSNativeControlSettingsViewController)settingsNavigation.TopViewController!;
            foreach (var (keyboard, mouse, stage) in new (bool, bool, string)[]
            {
                (false, false, "control-touch"), (true, false, "control-keyboard"), (false, true, "control-mouse"),
            })
            {
                await UiDispatcher.InvokeAsync(() =>
                {
                    control.Capabilities.Inject(keyboard, mouse);
                    control.View!.LayoutIfNeeded();
                });
                await Task.Delay(600);
                await HoldAsync(stage, lines);
            }
            await UiDispatcher.InvokeAsync(() =>
            {
                var scroll = control.View!.Subviews.OfType<UIScrollView>().Single();
                double bottom = Math.Max(0, (double)scroll.ContentSize.Height - (double)scroll.Bounds.Height);
                scroll.SetContentOffset(new CoreGraphics.CGPoint(0, bottom), false);
            });
            await Task.Delay(300);
            await HoldAsync("control-mapping", lines);
            await UiDispatcher.InvokeAsync(() =>
            {
                control.Capabilities.Inject(true, false);
                control.View!.LayoutIfNeeded();
                // 回到顶部，让录制态按钮出现在截图里。
                control.View.Subviews.OfType<UIScrollView>().Single().SetContentOffset(new CGPoint(0, 0), false);
                control.BeginRecording(settingsRoot.Session.WorkingSettings.Hotkeys[0], false);
            });
            await Task.Delay(400);
            await HoldAsync("control-recording", lines);
            await UiDispatcher.InvokeAsync(() => control.Recorder.CancelRecording());
            await Task.Delay(300);

            await UiDispatcher.InvokeAsync(() => root.DismissViewController(true, null));
            await WaitAsync(() => root.PresentedViewController == null, "设置页关闭");
            await Task.Delay(400);

            await UiDispatcher.InvokeAsync(root.ShowTools);
            var presenter = iOSNativeToolsPresenter.Current ?? throw new InvalidOperationException("工具页宿主未初始化");
            await WaitAsync(() => presenter.IsPresented && presenter.Navigation?.TopViewController is iOSNativeToolsListViewController, "工具列表页");
            await Task.Delay(400);
            await HoldAsync("tools-list", lines);

            await UiDispatcher.InvokeAsync(presenter.OpenExif);
            await WaitAsync(() => presenter.Navigation?.TopViewController is iOSExifDetailView view && view.GroupCount > 0, "EXIF 详情页");
            await Task.Delay(400);
            await HoldAsync("tools-exif", lines);
            var list = presenter.ListPage!;
            await UiDispatcher.InvokeAsync(() => presenter.Navigation!.PopViewController(true));
            await WaitAsync(() => presenter.Navigation?.TopViewController is iOSNativeToolsListViewController && !main.Tools.CanGoBack, "返回工具列表");
            await Task.Delay(400);

            await UiDispatcher.InvokeAsync(() => list.RowSelected(list.TableView, NSIndexPath.FromRowSection(1, 0)));
            await WaitAsync(() => presenter.Navigation?.TopViewController is iOSDinoDiagnosticView,
                $"DINO 诊断页压栈（当前 {presenter.Navigation?.TopViewController?.GetType().Name ?? "null"}）");
            await WaitAsync(() => main.Tools.DinoPresenter.Status == main.CurrentFile!.Name,
                $"DINO 推理就绪（状态 {main.Tools.DinoPresenter.Status}，页 {presenter.Navigation?.TopViewController?.GetType().Name}）");
            await WaitAsync(() => presenter.Navigation?.TopViewController is iOSDinoDiagnosticView view && view.Tiles[0].HasContent, "DINO 缩略图瓦片出图");
            await Task.Delay(400);
            await HoldAsync("tools-dino", lines);
            await UiDispatcher.InvokeAsync(presenter.Dismiss);
            await WaitAsync(() => !presenter.IsPresented, "工具页关闭");

            await File.WriteAllLinesAsync(report, lines);
            Console.WriteLine($"[NativeSmoke] PASS sheet-shots ({lines.Count})");
        }
        catch (Exception exception)
        {
            lines.Add($"FAIL {exception}");
            await File.WriteAllLinesAsync(report, lines);
            Console.Error.WriteLine("[NativeSmoke] FAIL sheet-shots " + exception);
        }
    }

    /// <summary>记录一个检查点并停留，供外部截图。</summary>
    /// <param name="stage">检查点名（与截图文件名一致）。</param>
    /// <param name="lines">报告行。</param>
    private static async Task HoldAsync(string stage, List<string> lines)
    {
        lines.Add($"STAGE {stage}");
        string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "PhotoViewer", "native-ui-snapshot-stage.txt");
        await File.WriteAllTextAsync(path, stage);
        await Task.Delay(StageHold);
    }

    /// <summary>轮询等待条件成立，超时即失败。</summary>
    /// <param name="condition">条件。</param>
    /// <param name="label">等待标签。</param>
    private static async Task WaitAsync(Func<bool> condition, string label)
    {
        for (int attempt = 0; attempt < 2400; attempt++)
        {
            if (condition()) return;
            await Task.Delay(25);
        }
        throw new InvalidOperationException($"等待超时：{label}");
    }
}
