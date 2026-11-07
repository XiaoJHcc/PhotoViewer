using System;
using System.Linq;
using System.Threading.Tasks;
using Android.Views;
using PhotoViewer.Android.Core;
using PhotoViewer.Core.AI;
using PhotoViewer.Shared;
using PhotoViewer.ViewModels.Main;

namespace PhotoViewer.Android.Native;

/// <summary>
/// <c>--es pv-check edition</c>（验收专用）：不依赖完整构建矩阵，对当前实际构建变体做一次发行版 smoke——
/// 编译能力（标准版产物不含 DINO/相似聚类类型）、筛选条末项、分析栏瓦片数、诊断入口与瓦片数、
/// AI 页控件。断言都读真实视图与程序集，不做源码行数或文案清单式检查。
/// </summary>
internal static partial class AndroidNativeChecks
{
    /// <summary>发行版 smoke：加载自有素材后逐项核对入口与构成；每步独立失败不阻塞后续。</summary>
    private static async Task RunEditionChecksAsync()
    {
        await WaitForScreenAsync();
        var activity = _activity!;
        var main = _main!;
        var session = _session!;
        var root = _root!;
        await RunStepAsync("编译能力", () =>
        {
            VerifyEditionCompilation();
            return Task.CompletedTask;
        });
        await RunStepAsync("发行版素材", async () =>
        {
            string folder = EnsureLayoutFolder(activity);
            await main.FolderVM.OpenFolderAsync(new AndroidFolderRef(folder));
            await WaitAsync(() => main.CurrentFile != null && session.Image != null, "发行版素材上屏", 25000);
            await Task.Delay(250);
        });
        await RunStepAsync("筛选入口", () => VerifyEditionFilterBarAsync(root, main));
        await RunStepAsync("分析瓦片", () => VerifyEditionAnalysisAsync(root, main));
        await RunStepAsync("诊断入口", () => VerifyEditionDiagnosticAsync(main));
        await RunStepAsync("设置页", () => VerifyEditionSettingsAsync(root, main));
    }

    /// <summary>编译期能力：能力常量、指纹分组可用性，以及 DINO/相似聚类类型是否存在于当前产物。</summary>
    private static void VerifyEditionCompilation()
    {
        var android = typeof(AndroidNativeRootView).Assembly;
        var core = typeof(FingerprintGrouping).Assembly;
        string expectedName = AndroidAppCapabilities.HasDino ? "DINO 诊断" : "CV 诊断";
        string expectedEdition = AndroidAppCapabilities.HasDino ? "ai" : "standard";
        Check("发行版诊断页名与能力一致",
            AndroidAppCapabilities.DiagnosticName == expectedName && AndroidAppCapabilities.EditionName == expectedEdition,
            $"hasDino={AndroidAppCapabilities.HasDino} name={AndroidAppCapabilities.DiagnosticName}"
                + $" edition={AndroidAppCapabilities.EditionName}");
        Check("指纹分组在发行版可用",
            core.GetType("PhotoViewer.Core.AI.FingerprintGrouping") != null,
            "FingerprintGrouping 缺失");
#if PHOTOVIEWER_DINO
        string[] aiTypes =
        [
            "PhotoViewer.Core.AI.DinoFeatureExtractor",
            "PhotoViewer.Core.AI.DinoModelResources",
            "PhotoViewer.Core.AI.FolderFeatureIndexer",
            "PhotoViewer.Core.AI.SimilarityService",
            "PhotoViewer.ViewModels.Main.File.SimilarityPanelViewModel",
            "PhotoViewer.ViewModels.Tools.DinoDebugViewModel",
        ];
        var missing = aiTypes.Where(name => core.GetType(name) == null).ToList();
        bool androidPanel = android.GetType("PhotoViewer.Android.Native.AndroidSimilarityPanel") != null;
        Check("AI 版编译产物含 DINO/相似聚类类型",
            AndroidAppCapabilities.HasDino && missing.Count == 0 && androidPanel,
            $"missing=[{string.Join(",", missing)}] androidPanel={androidPanel}");
#else
        string[] aiTypes =
        [
            "PhotoViewer.Core.AI.DinoFeatureExtractor",
            "PhotoViewer.Core.AI.DinoModelResources",
            "PhotoViewer.Core.AI.FolderFeatureIndexer",
            "PhotoViewer.Core.AI.SimilarityService",
            "PhotoViewer.ViewModels.Main.File.SimilarityPanelViewModel",
            "PhotoViewer.ViewModels.Tools.DinoDebugViewModel",
        ];
        var present = aiTypes.Where(name => core.GetType(name) != null).ToList();
        bool androidPanel = android.GetType("PhotoViewer.Android.Native.AndroidSimilarityPanel") != null;
        Check("标准版编译产物不含 DINO/相似聚类类型",
            !AndroidAppCapabilities.HasDino && present.Count == 0 && !androidPanel,
            $"present=[{string.Join(",", present)}] androidPanel={androidPanel}");
#endif
    }

    /// <summary>EDITION-02：筛选条按版本实际项数构成；标准版末项为计数，AI 版末项为相似聚类开关。</summary>
    private static async Task VerifyEditionFilterBarAsync(AndroidNativeRootView root, MainViewModel main)
    {
        var bar = root.FilterBar;
        var content = root.Panes.FileContent;
        bool row = main.IsRowLayout;
        var tail = AndroidFilterBar.ShowsSimilarityToggle ? bar.SimilarityFrame : bar.CountFrame;
        Check("发行版筛选条末项与入口匹配",
            AndroidFilterBar.ShowsSimilarityToggle == AndroidAppCapabilities.HasDino
            && bar.CountFrame.Width > 0 && tail.Width > 0
            && (row ? tail.MaxX <= content.Width + .6 : tail.MaxY <= content.Height + .6)
            && (row
                ? (AndroidFilterBar.ShowsSimilarityToggle
                    ? bar.SimilarityFrame.X > bar.CountFrame.MaxX - .6
                    : bar.CountFrame.X > bar.RatingFrame.MaxX - .6)
                : bar.CountFrame.Y > bar.RatingFrame.MaxY - .6),
            $"toggle={AndroidFilterBar.ShowsSimilarityToggle} row={row} sort={bar.SortFrame} count={bar.CountFrame}"
                + $" tail={tail} content={content}");
#if PHOTOVIEWER_DINO
        bool wasOpen = main.FileVM.FilterBar.IsSimilarityPanelOpen;
        try
        {
            main.FileVM.FilterBar.IsSimilarityPanelOpen = true;
            await Task.Delay(600);
            Check("AI 版相似聚类入口可用",
                root.SimilarityPane.Visibility == ViewStates.Visible,
                $"{root.SimilarityPane.Visibility}");
        }
        finally
        {
            main.FileVM.FilterBar.IsSimilarityPanelOpen = wasOpen;
            await Task.Delay(300);
        }
#else
        bool standardTail = row
            ? bar.CountFrame.X > bar.RatingFrame.MaxX - .6
                && Math.Abs(bar.RequiredWidthDp
                    - (Math.Max(bar.CountFrame.MaxX, bar.RatingFrame.MaxX) + MainScreenLayout.FilterMargin)) < .6
            : bar.CountFrame.Y > bar.RatingFrame.MaxY - .6;
        Check("标准版筛选条末项为计数且无 toggle 空槽",
            !AndroidFilterBar.ShowsSimilarityToggle && bar.CountFrame.Width > 0 && standardTail,
            $"toggle={AndroidFilterBar.ShowsSimilarityToggle} count={bar.CountFrame} required={bar.RequiredWidthDp:0.#} row={row}");
#endif
    }

    /// <summary>EDITION-02：标准版分析栏 4/5 张、AI 版 6/7 张；DINO 瓦片只属于 AI 版。</summary>
    private static async Task VerifyEditionAnalysisAsync(AndroidNativeRootView root, MainViewModel main)
    {
        bool previous = main.IsAnalysisViewVisible;
        try
        {
            main.IsAnalysisViewVisible = true;
            await WaitAsync(() => root.AnalysisPane.TileCount >= 4, "分析栏瓦片", 10000);
            await Task.Delay(300);
            var pane = root.AnalysisPane;
            int diagnostics = pane.Tiles.Count(tile => tile.Kind == AnalysisTileKind.Diagnostic);
#if PHOTOVIEWER_DINO
            bool hasDinoTiles = pane.Tiles.Any(tile => tile.ShortLabel == "DINO PCA")
                && pane.Tiles.Any(tile => tile.ShortLabel.StartsWith("Cosine", StringComparison.Ordinal));
            Check("AI 版分析栏 6/7 瓦片且含 PCA/Cosine",
                pane.TileCount is >= 6 and <= 7 && diagnostics == 4 && hasDinoTiles,
                $"tiles={pane.TileCount} diagnostics={diagnostics} dino={hasDinoTiles}"
                    + $" labels=[{string.Join("/", pane.Tiles.Select(tile => tile.ShortLabel))}]");
#else
            bool hasDinoTiles = pane.Tiles.Any(tile => tile.ShortLabel.Contains("DINO", StringComparison.Ordinal)
                || tile.ShortLabel.StartsWith("Cosine", StringComparison.Ordinal));
            Check("标准版分析栏 4/5 瓦片且无 DINO",
                pane.TileCount is >= 4 and <= 5 && diagnostics == 2 && !hasDinoTiles,
                $"tiles={pane.TileCount} diagnostics={diagnostics} dino={hasDinoTiles}"
                    + $" labels=[{string.Join("/", pane.Tiles.Select(tile => tile.ShortLabel))}]");
#endif
        }
        finally
        {
            main.IsAnalysisViewVisible = previous;
            await Task.Delay(200);
        }
    }

    /// <summary>EDITION-02/EDITION-04：工具页诊断入口与瓦片数；标准版为三瓦片 CV 诊断且可现算。</summary>
    private static async Task VerifyEditionDiagnosticAsync(MainViewModel main)
    {
        main.OpenToolsWindow();
        await Task.Delay(700);
        try
        {
            Check("发行版工具页打开", AndroidToolsPresenter.IsOpen, "IsOpen=false");
            main.Tools.OpenDiagnostic();
            await Task.Delay(300);
            Check("发行版诊断入口进入诊断页",
                ReferenceEquals(main.Tools.CurrentTool, main.Tools.DiagnosticDebug)
                && AndroidToolsPresenter.CurrentPageForChecks == "diagnostic",
                $"tool={main.Tools.CurrentTool?.GetType().Name ?? "null"}"
                    + $" page={AndroidToolsPresenter.CurrentPageForChecks}");
#if PHOTOVIEWER_DINO
            var dino = main.Tools.DinoPresenter;
            Check("AI 版诊断五瓦片",
                dino.Tiles.Count == 5,
                $"{dino.Tiles.Count} labels=[{string.Join("/", dino.Tiles.Select(tile => tile.ShortLabel))}]");
#else
            var cv = main.Tools.CvPresenter;
            Check("标准版诊断三瓦片",
                cv.Tiles.Count == 3,
                $"{cv.Tiles.Count} labels=[{string.Join("/", cv.Tiles.Select(tile => tile.ShortLabel))}]");
            await WaitAsync(() => cv.ThumbnailTile.Source != null && cv.SharpnessTile.Source != null
                && cv.ShakeTile.ShakeField != null, "CV 诊断三瓦片现算", 90000);
            Check("标准版 CV 诊断可现算",
                cv.Status == main.CurrentFile?.Name && !cv.IsBusy,
                $"status={cv.Status} busy={cv.IsBusy}");
#endif
        }
        finally
        {
            main.Tools.ShowList();
            await Task.Delay(200);
            AndroidToolsPresenter.HandleBack();
            await Task.Delay(300);
        }
    }

    /// <summary>EDITION-02/EDITION-03：AI 页控件按版本裁剪，分析宿主分别读取 DINO/CV 两个独立开关。</summary>
    private static async Task VerifyEditionSettingsAsync(AndroidNativeRootView root, MainViewModel main)
    {
        main.OpenSettingWindow();
        await Task.Delay(700);
        var sheet = AndroidNativeSettingsPresenter.CurrentSheet;
        Check("发行版设置页打开", sheet != null, "CurrentSheet=null");
        if (sheet == null) return;
        try
        {
            sheet.ShowPageForChecks("ai");
            await Task.Delay(350);
            bool hasCv = HasText(sheet, "即时计算 CV");
            bool hasDino = HasText(sheet, "即时计算 DINO");
            bool hasSimilarity = HasText(sheet, "相似聚类");
            bool hasThreshold = HasText(sheet, "相似度阈值");
            bool hasMax = HasText(sheet, "最多数量");
#if PHOTOVIEWER_DINO
            Check("AI 版设置页含相似聚类参数与 DINO/CV 两个开关",
                hasCv && hasDino && hasSimilarity && hasThreshold && hasMax,
                $"cv={hasCv} dino={hasDino} similarity={hasSimilarity} threshold={hasThreshold} max={hasMax}");
#else
            Check("标准版设置页只有即时计算 CV",
                hasCv && !hasDino && !hasSimilarity && !hasThreshold && !hasMax,
                $"cv={hasCv} dino={hasDino} similarity={hasSimilarity} threshold={hasThreshold} max={hasMax}");
#endif
            var host = root.AnalysisHostForChecks;
#if PHOTOVIEWER_DINO
            bool previousDino = main.Settings.AnalysisImmediateCompute;
#endif
            bool previousCv = main.Settings.AnalysisImmediateCvCompute;
            bool previousVisible = main.IsAnalysisViewVisible;
            try
            {
                main.IsAnalysisViewVisible = false;
#if PHOTOVIEWER_DINO
                main.Settings.AnalysisImmediateCompute = true;
                main.Settings.AnalysisImmediateCvCompute = false;
                bool dinoOnly = host.ImmediateCompute && !host.ImmediateCvCompute;
                main.Settings.AnalysisImmediateCompute = false;
                main.Settings.AnalysisImmediateCvCompute = true;
                bool cvOnly = !host.ImmediateCompute && host.ImmediateCvCompute;
                Check("分析宿主分别接线即时计算 DINO/CV", dinoOnly && cvOnly,
                    $"dinoOnly={dinoOnly} cvOnly={cvOnly}");
#else
                main.Settings.AnalysisImmediateCvCompute = true;
                bool cvOn = host.ImmediateCvCompute;
                main.Settings.AnalysisImmediateCvCompute = false;
                bool cvOff = !host.ImmediateCvCompute;
                Check("标准版分析宿主接独立 CV 开关", cvOn && cvOff,
                    $"on={cvOn} off={cvOff}");
#endif
            }
            finally
            {
#if PHOTOVIEWER_DINO
                main.Settings.AnalysisImmediateCompute = previousDino;
#endif
                main.Settings.AnalysisImmediateCvCompute = previousCv;
                main.IsAnalysisViewVisible = previousVisible;
            }
        }
        finally
        {
            sheet.RequestDismiss();
            await Task.Delay(400);
        }
    }
}
