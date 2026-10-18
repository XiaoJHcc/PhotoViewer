using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Android.App;
using Android.OS;
using PhotoViewer.Core.Image;
using PhotoViewer.Shared;
using PhotoViewer.ViewModels.Main;

namespace PhotoViewer.Android.Native;

/// <summary>
/// Android 原生壳程序化自检（A6 起步，验收专用）：<c>--es pv-check main</c> 在首图上屏后核对主屏分区、
/// 安全区、筛选条/卡片/控制栏几何、Fit↔100% 往返、分析栏/相似面板/设置/工具页开合，并把报告写到
/// App 内部 <c>files/PhotoViewer/android-checks.txt</c>，供 <c>adb exec-out run-as ... cat</c> 取回。
/// 仅测试路径调用，不改变产品行为。
/// </summary>
internal static class AndroidNativeChecks
{
    private static int _passed;
    private static int _failed;
    private static readonly List<string> Lines = [];
    private static AndroidNativeRootView? _root;
    private static MainViewModel? _main;
    private static PhotoLoadSession? _session;
    private static Activity? _activity;
    private static int _attempt;
    private static string _mode = "main";

    /// <summary>按启动参数决定是否启动检查。</summary>
    /// <param name="activity">宿主 Activity。</param>
    /// <param name="main">共享主 VM。</param>
    /// <param name="session">展示会话。</param>
    /// <param name="root">主屏根视图。</param>
    /// <param name="mode">检查模式；null/空不启动。</param>
    public static void MaybeStart(Activity activity, MainViewModel main, PhotoLoadSession session, AndroidNativeRootView root, string? mode)
    {
        if (string.IsNullOrWhiteSpace(mode)) return;
        _activity = activity;
        _main = main;
        _session = session;
        _root = root;
        _mode = mode.Trim().ToLowerInvariant();
        Lines.Clear();
        _passed = 0;
        _failed = 0;
        Console.WriteLine($"[NativeCheck] 启动检查 mode={mode}");
        var handler = new Handler(Looper.MainLooper!);
        handler.PostDelayed(Tick, 800);
    }

    /// <summary>等待首图上屏，然后按顺序跑检查；每一步之间留出布局/动画时间。</summary>
    private static void Tick()
    {
        if (_root == null || _main == null || _session == null || _activity == null) return;
        if (_session.Image == null && _main.CurrentFile != null && _attempt++ < 60)
        {
            new Handler(Looper.MainLooper!).PostDelayed(Tick, 200);
            return;
        }
        _ = RunAsync();
    }

    private static async System.Threading.Tasks.Task RunAsync()
    {
        var root = _root!;
        var main = _main!;
        var session = _session!;
        Check("首图上屏", session.Image != null, session.Image == null ? "无图像" : $"{session.Image.Width}x{session.Image.Height}");
        Check("安全区非负", root.SafeInsets.Left >= 0 && root.SafeInsets.Top >= 0 && root.SafeInsets.Right >= 0 && root.SafeInsets.Bottom >= 0,
            $"{root.SafeInsets}");
        var panes = root.Panes;
        bool layoutRow = main.IsRowLayout;
        double screenWidthDp = root.Width / AndroidUiTheme.Density(root.Context!);
        double screenHeightDp = root.Height / AndroidUiTheme.Density(root.Context!);
        Check("文件栏背景铺边",
            Math.Abs(panes.FileBar.X) < .5 && Math.Abs(panes.FileBar.Y) < .5
            && (layoutRow
                ? Math.Abs(panes.FileBar.Width - screenWidthDp) < .5
                : Math.Abs(panes.FileBar.Height - screenHeightDp) < .5),
            $"{panes.FileBar} row={layoutRow}");
        if (layoutRow)
        {
            Check("分区无重叠且顺序正确",
                Near(panes.FileBar.MaxY, panes.FileDivider.Y) && Near(panes.FileDivider.MaxY, panes.Photo.Y)
                && Near(panes.Photo.MaxY, panes.ControlDivider.Y) && Near(panes.ControlDivider.Y, panes.ControlBackground.Y),
                $"file={panes.FileBar.MaxY:0.#} div={panes.FileDivider.Y:0.#} photo={panes.Photo.Y:0.#}..{panes.Photo.MaxY:0.#} controlDiv={panes.ControlDivider.Y:0.#}");
        }
        else
        {
            Check("分区无重叠且顺序正确",
                Near(panes.FileBar.MaxX, panes.FileDivider.X) && Near(panes.FileDivider.MaxX, panes.Photo.X)
                && Near(panes.Photo.MaxX, panes.ControlDivider.X) && Near(panes.ControlDivider.MaxX, panes.ControlBackground.X),
                $"file={panes.FileBar.MaxX:0.#} div={panes.FileDivider.X:0.#} photo={panes.Photo.X:0.#}..{panes.Photo.MaxX:0.#} controlDiv={panes.ControlDivider.X:0.#}");
        }

        var content = panes.FileContent;
        var filter = root.FilterBar;
        Check("筛选条在文件栏内容区内",
            (layoutRow
                ? filter.SortFrame.X >= 0 && filter.SimilarityFrame.MaxX <= content.Width + .5
                : filter.SortFrame.Y >= 0 && filter.SimilarityFrame.MaxY <= content.Height + .5)
            && filter.SortFrame.Height is >= 31 and <= 33 && filter.SimilarityFrame.Height is >= 26 and <= 28,
            $"sort={filter.SortFrame} toggle={filter.SimilarityFrame} content={content}");
        Check("筛选条计数宽度有限", filter.CountWidthDp is > 8 and < 200, $"{filter.CountWidthDp:0.#}dp");

        var files = main.FileVM.ThumbnailList.FilteredFiles;
        Check("胶片栏项数=筛选列表", root.Filmstrip.Files.Count == files.Count && files.Count > 0, $"{files.Count}");
        var card = root.Filmstrip.CardAt(0);
        double density = AndroidUiTheme.Density(root.Context!);
        double expectedCardWidth = layoutRow ? 96 : 116;
        Check($"卡片 {expectedCardWidth:0}×144dp", card != null
            && Math.Abs(card.Width / density - expectedCardWidth) < 1.5 && Math.Abs(card.Height / density - 144) < 1.5,
            card == null ? "首卡未创建" : $"{card.Width / density:0.#}×{card.Height / density:0.#}dp");
        Check("首卡绑定筛选首项", card != null && ReferenceEquals(card.BoundFile, files[0]), card?.BoundFile?.Name ?? "null");

        Check("控制栏三分组", root.ControlBar.GroupCount == 3, $"{root.ControlBar.GroupCount}");
        Check("控制栏命令含 Next/Fit", root.ControlBar.Commands.Contains("Next") && root.ControlBar.Commands.Contains("Fit"),
            string.Join(",", root.ControlBar.Commands));

        // Fit ↔ 物理 100% 往返（大素材才具备向上空间；小素材按契约夹回适应态也算通过）。
        bool canZoom = session.Image!.SourceWidth > 0 && root.Photo.Viewport.FitScale < 1 / density - 1e-9;
        root.Photo.ToggleFit();
        await System.Threading.Tasks.Task.Delay(700);
        if (canZoom)
        {
            Check("Fit→物理 100%", !root.Photo.IsFit && Math.Abs(root.Photo.PhysicalScale - 1) < .03,
                $"fit={root.Photo.IsFit} physical={root.Photo.PhysicalScale:0.###}");
            Check("100% 触发 Full", !session.IsProxy, $"proxy={session.IsProxy} {session.Image.Width}x{session.Image.Height}");
        }
        else
        {
            Check("小素材 100% 夹回适应", root.Photo.IsFit, $"fit={root.Photo.IsFit} physical={root.Photo.PhysicalScale:0.###}");
        }
        root.Photo.ToggleFit();
        await System.Threading.Tasks.Task.Delay(500);
        Check("回到适应态", root.Photo.IsFit, $"fit={root.Photo.IsFit}");

        if (Wants("analysis") || Wants("all"))
        {
        // 分析栏开合与瓦片几何。
        main.IsAnalysisViewVisible = true;
        await System.Threading.Tasks.Task.Delay(900);
        Check("分析栏可见", root.AnalysisPane.Visibility == global::Android.Views.ViewStates.Visible && root.Panes.Analysis.Height > 0,
            $"{root.AnalysisPane.Visibility} {root.Panes.Analysis}");
        Check("分析栏瓦片≥6", root.AnalysisPane.TileCount >= 6, $"{root.AnalysisPane.TileCount}");
        Check("分析栏瓦片边长合理", root.AnalysisPane.TileSize > 20, $"{root.AnalysisPane.TileSize:0.#}dp");
        main.IsAnalysisViewVisible = false;
        await System.Threading.Tasks.Task.Delay(400);
        Check("关闭分析栏释放空间", root.AnalysisPane.Visibility != global::Android.Views.ViewStates.Visible, $"{root.AnalysisPane.Visibility}");
        }

        if (Wants("similarity") || Wants("all"))
        {
        // 相似面板开合。
        main.FileVM.FilterBar.IsSimilarityPanelOpen = true;
        await System.Threading.Tasks.Task.Delay(900);
        Check("相似聚类面板可见", root.SimilarityPane.Visibility == global::Android.Views.ViewStates.Visible,
            $"{root.SimilarityPane.Visibility}");
        main.FileVM.FilterBar.IsSimilarityPanelOpen = false;
        await System.Threading.Tasks.Task.Delay(400);
        }

        if (Wants("settings") || Wants("all"))
        {
        // 设置半屏与工具页开合（真实展示路径）。
        main.OpenSettingWindow();
        await System.Threading.Tasks.Task.Delay(700);
        Check("设置 sheet 打开", AndroidNativeSettingsPresenter.IsOpen, "IsOpen=false");
        AndroidNativeSettingsPresenter.HandleBack();
        await System.Threading.Tasks.Task.Delay(500);
        Check("设置 sheet 关闭", !AndroidNativeSettingsPresenter.IsOpen, "IsOpen=true");
        }

        if (Wants("tools") || Wants("all"))
        {
        main.OpenToolsWindow();
        await System.Threading.Tasks.Task.Delay(800);
        Check("工具 sheet 打开", AndroidToolsPresenter.IsOpen, "IsOpen=false");
        AndroidToolsPresenter.HandleBack();
        await System.Threading.Tasks.Task.Delay(500);
        Check("工具 sheet 关闭", !AndroidToolsPresenter.IsOpen, "IsOpen=true");
        }

        var report = new StringBuilder();
        report.AppendLine($"Android native checks mode={_mode} {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}");
        report.AppendLine($"PASS {_passed} / FAIL {_failed}");
        foreach (var line in Lines) report.AppendLine(line);
        var directory = Path.Combine(_activity!.FilesDir!.AbsolutePath, "PhotoViewer");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, $"android-checks-{_mode}.txt"), report.ToString());
        Console.WriteLine(report.ToString());
        Console.WriteLine(_failed == 0 ? $"[NativeCheck] PASS {_mode} ({_passed} checks)" : $"[NativeCheck] FAIL {_mode} ({_failed} failed / {_passed + _failed})");
    }

    private static void Check(string name, bool condition, string detail)
    {
        if (condition) _passed++;
        else _failed++;
        Lines.Add($"{(condition ? "PASS" : "FAIL")} {name} — {detail}");
    }

    private static bool Near(double a, double b) => Math.Abs(a - b) < .6;

    /// <summary>当前检查模式是否包含某一组检查。</summary>
    private static bool Wants(string group) => _mode == group || _mode == "all";
}
