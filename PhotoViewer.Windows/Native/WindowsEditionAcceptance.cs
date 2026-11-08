using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using PhotoViewer.Core.AI;
using PhotoViewer.Core.Database;
using PhotoViewer.Core.Settings;
using SkiaSharp;
using Windows.Foundation;

namespace PhotoViewer.Windows.Native;

/// <summary>
/// 发行版验收(EDITION-07)：<c>--pv-native-edition</c> 按当前实际构建变体核对入口与构成；
/// <c>--pv-native-edition-roundtrip=prepare|standard|ai|all</c> 跑 AI → 标准 → AI 的真实跨进程往返。
/// 所有断言都读实际窗口、实际视图模型与实际数据库，不以源码字符串代替。
/// </summary>
internal sealed partial class WindowsMainWindow
{
    /// <summary>往返基线的共享文件名(保存在隔离输出目录)。</summary>
    private const string RoundtripStateFileName = "edition-roundtrip-state.json";

    /// <summary>往返素材:仓库自带的小尺寸 JPEG 目录(相对素材根，只读使用)。</summary>
    private const string RoundtripFixtureFolderName = "jpeg";

    /// <summary>往返使用的当前照片文件名(三阶段一致,保证指纹与瓦片可比)。</summary>
    private const string RoundtripCurrentFileName = "color-srgb.jpg";

    /// <summary>往返哨兵:相似度阈值。</summary>
    private const double RoundtripThreshold = 0.88;

    /// <summary>往返哨兵:相似聚类最多数量。</summary>
    private const int RoundtripMaxResults = 7;

    /// <summary>发行版专项:核对编译能力、筛选条与实际瓦片、诊断入口、设置页、独立开关与库边界。</summary>
    /// <param name="toolsPresenter">工具窗口展示器(打开真实诊断页)。</param>
    /// <param name="settingsPresenter">设置窗口展示器(打开真实设置页)。</param>
    /// <returns>检查完成后的任务。</returns>
    internal async Task RunEditionChecksAsync(
        WindowsNativeToolsPresenter toolsPresenter,
        WindowsNativeSettingsPresenter settingsPresenter)
    {
        Directory.CreateDirectory(SmokeOutputDirectory);
        var checks = new List<string>();
        var reportPath = Path.Combine(SmokeOutputDirectory, $"edition-checks-{WindowsProductInfo.EditionName}.json");
        File.WriteAllText(reportPath, "[\"RUNNING\"]");
        try
        {
            checks.Add(CheckEditionCapability());
            checks.Add(CheckEditionIdentity());

            var folder = Path.Combine(SmokeOutputDirectory, "fixtures", "edition");
            await Task.Run(() =>
            {
                Directory.CreateDirectory(folder);
                for (var index = 0; index < 3; index++)
                {
                    CreateEditionFixture(Path.Combine(folder, $"edition-{index}.jpg"), index);
                }
            });
            await OpenPathsAsync(new[] { folder });
            await WaitForSmokeAsync(() => _main.CurrentFile != null && _session.Image != null, "发行版素材上屏", 30);
            await WaitForUploadAsync("发行版素材上屏");
            checks.Add($"PASS edition material: opened {_main.FileVM.ThumbnailList.FilteredFiles.Count} local fixtures with product decode");

            checks.Add(CheckEditionFilterBar());
            checks.Add(await CheckEditionAnalysisAsync());
            checks.Add(await CheckEditionToolPageAsync(toolsPresenter));
            checks.Add(await CheckEditionSettingsPageAsync(settingsPresenter));
            checks.AddRange(await CheckEditionSwitchBoundaryAsync());
            checks.Add(CheckEditionTitle());

            Environment.ExitCode = 0;
        }
        catch (Exception exception)
        {
            Environment.ExitCode = 1;
            checks.Add("FAIL " + exception);
            NativeStartupLog.Fail("native edition acceptance", exception);
        }
        finally
        {
            File.WriteAllText(reportPath, JsonSerializer.Serialize(checks, new JsonSerializerOptions { WriteIndented = true }));
        }
    }

    /// <summary>
    /// AI → 标准 → AI 往返:<c>all</c> 在本进程跑 prepare 再依次拉起标准版与 AI 版真实进程，
    /// 其余取值只跑当前阶段。三阶段共用同一隔离输出目录的设置、界面状态与数据库。
    /// </summary>
    /// <param name="stage">prepare / standard / ai / all。</param>
    /// <param name="toolsPresenter">工具窗口展示器(标准版阶段打开 CV 诊断页)。</param>
    /// <returns>检查完成后的任务。</returns>
    internal async Task RunEditionRoundtripAsync(string stage, WindowsNativeToolsPresenter toolsPresenter)
    {
        Directory.CreateDirectory(SmokeOutputDirectory);
        if (string.Equals(stage, "all", StringComparison.Ordinal))
        {
            await RunEditionRoundtripOrchestratorAsync(toolsPresenter);
            return;
        }

        var passed = 0;
        var failed = 0;
        var lines = new List<string>
        {
            $"== STAGE START {stage} edition={WindowsProductInfo.EditionName} {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}",
        };
        void Check(string name, bool condition, string detail)
        {
            if (condition)
            {
                passed++;
                lines.Add("PASS " + name + (detail.Length > 0 ? ": " + detail : string.Empty));
            }
            else
            {
                failed++;
                lines.Add("FAIL " + name + ": " + detail);
            }
        }

        try
        {
            string expected = stage switch
            {
                "prepare" => "ai",
                "standard" => "standard",
                "ai" => "ai",
                _ => throw new InvalidOperationException("--pv-native-edition-roundtrip 必须是 prepare|standard|ai|all"),
            };
            Check("阶段与发行版匹配", WindowsProductInfo.EditionName == expected,
                $"stage={stage} edition={WindowsProductInfo.EditionName} product={WindowsProductInfo.DisplayName}");
            if (WindowsProductInfo.EditionName != expected)
            {
                throw new InvalidOperationException($"{stage} 阶段不能在 {WindowsProductInfo.DisplayName} 构建上运行");
            }

            switch (stage)
            {
                case "prepare":
                    await PrepareRoundtripAsync(Check);
                    break;
                case "standard":
                    await VerifyStandardRoundtripAsync(Check, toolsPresenter);
                    break;
                default:
                    await VerifyAiRoundtripAsync(Check);
                    break;
            }
        }
        catch (Exception exception)
        {
            failed++;
            lines.Add("FAIL stage flow: " + exception.GetType().Name + ": " + exception.Message);
            NativeStartupLog.Fail("native edition roundtrip " + stage, exception);
        }

        lines.Add($"== STAGE {(failed == 0 ? "PASS" : "FAIL")} {stage} passed={passed} failed={failed}");
        File.AppendAllText(
            Path.Combine(SmokeOutputDirectory, $"edition-roundtrip-{stage}.txt"),
            string.Join(Environment.NewLine, lines) + Environment.NewLine);
        Environment.ExitCode = failed == 0 ? 0 : 1;
    }

#if !PHOTOVIEWER_DINO
    /// <summary>往返编排只能在 AI 版构建上启动。</summary>
    /// <param name="toolsPresenter">工具窗口展示器。</param>
    /// <returns>失败的编排任务。</returns>
    private Task RunEditionRoundtripOrchestratorAsync(WindowsNativeToolsPresenter toolsPresenter)
        => Task.FromException(new InvalidOperationException("往返编排(--pv-native-edition-roundtrip=all)必须在 AI 版构建上启动"));
#else
    /// <summary>往返编排:本进程(AI 版)跑 prepare,再拉起标准版与 AI 版真进程各跑一段。</summary>
    /// <param name="toolsPresenter">工具窗口展示器。</param>
    /// <returns>编排完成后的任务。</returns>
    private async Task RunEditionRoundtripOrchestratorAsync(WindowsNativeToolsPresenter toolsPresenter)
    {
        var lines = new List<string>
        {
            $"== ORCHESTRATOR START edition={WindowsProductInfo.EditionName} {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}",
        };
        var failed = 0;
        try
        {
            var standardExe = WindowsNativeApplication.EditionStandardExe
                ?? throw new InvalidOperationException("缺少 --pv-edition-standard-exe=<标准版 EXE>");
            var aiExe = WindowsNativeApplication.EditionAiExe
                ?? throw new InvalidOperationException("缺少 --pv-edition-ai-exe=<AI 版 EXE>");

            await RunEditionRoundtripAsync("prepare", toolsPresenter);
            failed += Environment.ExitCode == 0 ? 0 : 1;
            lines.Add($"prepare exit={Environment.ExitCode}");
            Environment.ExitCode = 0;

            failed += RunRoundtripPeer(standardExe, "standard", lines);
            failed += RunRoundtripPeer(aiExe, "ai", lines);
        }
        catch (Exception exception)
        {
            failed++;
            lines.Add("FAIL orchestrator: " + exception.GetType().Name + ": " + exception.Message);
            NativeStartupLog.Fail("native edition roundtrip orchestrator", exception);
        }

        lines.Add($"== ORCHESTRATOR {(failed == 0 ? "PASS" : "FAIL")}");
        File.AppendAllText(
            Path.Combine(SmokeOutputDirectory, "edition-roundtrip-all.txt"),
            string.Join(Environment.NewLine, lines) + Environment.NewLine);
        Environment.ExitCode = failed == 0 ? 0 : 1;
    }
#endif

    /// <summary>拉起另一个构建变体的真实进程执行一个往返阶段,并等待它自行退出。</summary>
    /// <param name="exe">目标 EXE。</param>
    /// <param name="stage">阶段。</param>
    /// <param name="lines">编排日志。</param>
    /// <returns>失败计数(0 或 1)。</returns>
    private static int RunRoundtripPeer(string exe, string stage, List<string> lines)
    {
        if (!File.Exists(exe))
        {
            lines.Add($"FAIL peer {stage}: 找不到 EXE {exe}");
            return 1;
        }

        var startInfo = new System.Diagnostics.ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            WorkingDirectory = AppContext.BaseDirectory,
        };
        startInfo.ArgumentList.Add($"--pv-native-edition-roundtrip={stage}");
        startInfo.ArgumentList.Add($"--pv-acceptance-output={SmokeOutputDirectory}");
        // 素材根显式传给对端进程:对端不搜祖先,也不依赖本进程的工作目录或解包位置。
        startInfo.ArgumentList.Add($"--pv-acceptance-fixtures={WindowsAcceptanceFixtures.RootDirectory
            ?? throw new InvalidOperationException("往返编排缺少 --pv-acceptance-fixtures 素材根")}");
        using var process = System.Diagnostics.Process.Start(startInfo)
            ?? throw new InvalidOperationException($"无法启动 {exe}");
        if (!process.WaitForExit(600_000))
        {
            lines.Add($"FAIL peer {stage}: 超时未退出");
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // 进程已自行退出。
            }

            return 1;
        }

        lines.Add($"peer {stage} exit={process.ExitCode} exe={exe}");
        return process.ExitCode == 0 ? 0 : 1;
    }

    /// <summary>prepare 阶段(AI 版):写入哨兵设置与真实特征库,并记录哈希基线。</summary>
    /// <param name="check">断言函数。</param>
    /// <returns>阶段完成后的任务。</returns>
    private async Task PrepareRoundtripAsync(Action<string, bool, string> check)
    {
#if PHOTOVIEWER_DINO
        var folder = ResolveRoundtripFixtureFolder();
        await OpenPathsAsync(new[] { folder });
        await WaitForSmokeAsync(() => _main.CurrentFile != null && _session.Image != null, "往返素材上屏", 30);

        _main.FileVM.SimilarityPanel.StartIndexingCommand();
        await WaitForSmokeAsync(() => !_main.FileVM.SimilarityPanel.IsStateIndexing
            && _main.FileVM.SimilarityPanel.IndexProgress > 0, "真实批量特征提取", 300);
        await WaitForSmokeAsync(() => !_main.FileVM.SimilarityPanel.IsStateIndexing, "批量特征提取收尾", 60);

        await OpenRoundtripCurrentFileAsync();
        var digest = await ComputeFeatureDigestAsync();
        check("特征库已写入", digest.Rows.Count > 0, digest.Summary);

        _main.Settings.SimilarityThreshold = RoundtripThreshold;
        _main.Settings.SimilarityMaxResults = RoundtripMaxResults;
        SetDinoImmediateForSmoke(true);
        _main.Settings.AnalysisImmediateCvCompute = false;
        _main.Settings.SameNameAsOnePhoto = false;
        _main.FileVM.FilterBar.IsSimilarityPanelOpen = true;
        await WaitForRoundtripSettingsAsync(RoundtripThreshold, RoundtripMaxResults);

        var state = new RoundtripState
        {
            Stage = "prepare",
            Edition = WindowsProductInfo.EditionName,
            Product = WindowsProductInfo.DisplayName,
            ExePath = Environment.ProcessPath ?? string.Empty,
            CurrentFile = RoundtripCurrentFileName,
            Fingerprint = await CurrentFingerprintAsync(),
            Threshold = _main.Settings.SimilarityThreshold,
            MaxResults = _main.Settings.SimilarityMaxResults,
            PanelExpanded = ReadViewStateBool(SettingsViewStateKeys.SimilarityPanelExpanded),
            DinoImmediate = _main.Settings.AnalysisImmediateCompute,
            CvImmediate = _main.Settings.AnalysisImmediateCvCompute,
            SameNameAsOnePhoto = _main.Settings.SameNameAsOnePhoto,
            Features = digest,
        };
        WriteRoundtripState(state);
        check("prepare 记录基线", state.Features.Rows.Count > 0 && state.PanelExpanded && state.DinoImmediate,
            $"rows={state.Features.Rows.Count} threshold={state.Threshold} max={state.MaxResults}"
                + $" panelExpanded={state.PanelExpanded} dino={state.DinoImmediate} cv={state.CvImmediate} file={state.CurrentFile}");
#else
        await Task.Yield();
        throw new InvalidOperationException("prepare 阶段只能在 AI 版构建上运行");
#endif
    }

#if PHOTOVIEWER_DINO
    /// <summary>standard 阶段在 AI 版构建上没有意义。</summary>
    /// <param name="check">断言函数。</param>
    /// <param name="toolsPresenter">工具窗口展示器。</param>
    /// <returns>失败的阶段任务。</returns>
    private Task VerifyStandardRoundtripAsync(Action<string, bool, string> check, WindowsNativeToolsPresenter toolsPresenter)
        => Task.FromException(new InvalidOperationException("standard 阶段只能在标准版构建上运行"));
#else
    /// <summary>standard 阶段:核对 DINO/相似入口不存在、设置与展开态保留、CV 库路径可用且不写库。</summary>
    /// <param name="check">断言函数。</param>
    /// <param name="toolsPresenter">工具窗口展示器。</param>
    /// <returns>阶段完成后的任务。</returns>
    private async Task VerifyStandardRoundtripAsync(Action<string, bool, string> check, WindowsNativeToolsPresenter toolsPresenter)
    {
        var state = ReadRoundtripState();
        await OpenRoundtripCurrentFileAsync();

        check("标准版编译能力", !WindowsProductInfo.HasDino
            && !AssemblyHasType(typeof(AnalysisPresenter).Assembly, "PhotoViewer.Core.AI.DinoFeatureExtractor")
            && !AssemblyHasType(typeof(WindowsMainWindow).Assembly, "PhotoViewer.Windows.Native.WindowsSimilarityPanel"),
            $"hasDino={WindowsProductInfo.HasDino} product={WindowsProductInfo.DisplayName}");

        var document = ReadSettingsDocument();
        check("设置文档保留 DINO 与相似聚类参数", SettingsBoolEquals(document, "analysisImmediateCompute", state.DinoImmediate)
            && SettingsNumberEquals(document, "similarityThreshold", state.Threshold)
            && SettingsNumberEquals(document, "similarityMaxResults", state.MaxResults),
            SummarizeSettings(document));
        check("相似展开态保留但不可见", ReadViewStateBool(SettingsViewStateKeys.SimilarityPanelExpanded)
            && !_main.FileVM.IsSimilarityPanelOpen && _filterBar.Children.Count == 4,
            $"stored={ReadViewStateBool(SettingsViewStateKeys.SimilarityPanelExpanded)}"
                + $" open={_main.FileVM.IsSimilarityPanelOpen} filterItems={_filterBar.Children.Count}");

        _main.ToggleAnalysisView();
        await WaitForSmokeAsync(() => _analysis.Visibility == Visibility.Visible, "标准版分析栏可见", 20);
        _root.UpdateLayout();
        RequireSmoke(!_main.Settings.AnalysisImmediateCvCompute, "标准版阶段开始时 CV 开关应保持 prepare 的值");
        await WaitForSmokeAsync(() => _analysis.CvDiagnosticsReady, "标准版 CV 瓦片读库出图(开关关闭)", 90);
        check("标准版分析栏无 DINO 瓦片", _analysis.DiagnosticTileCount == 2
            && !_analysis.TileLabels.Any(label => label.Contains("DINO", StringComparison.Ordinal)
                || label.StartsWith("Cosine", StringComparison.Ordinal)),
            $"tiles={_analysis.TileCount} diagnostics={_analysis.DiagnosticTileCount} labels=[{string.Join("/", _analysis.TileLabels)}]");

        // 「即时计算 CV」打开后仍复用同一份基线,不因开关变化改写库。
        _main.Settings.AnalysisImmediateCvCompute = true;
        await WaitForSmokeAsync(() => _analysis.CvDiagnosticsReady, "标准版 CV 开关开启后仍出图", 90);
        var digestAfter = await ComputeFeatureDigestAsync();
        check("标准版现算不写特征库", digestAfter.Equals(state.Features), digestAfter.Summary);
        _main.Settings.AnalysisImmediateCvCompute = false;
        _main.ToggleAnalysisView();

        var cvPage = await VerifyCvToolPageAsync(toolsPresenter);
        check("标准版 CV 诊断页", cvPage.Ok, cvPage.Detail);

        // 标准版只改通用设置,DINO 值与相似聚类参数原样保留(EDITION-06)。
        _main.Settings.SameNameAsOnePhoto = true;
        await WaitForSettingsFileAsync(document => SettingsBoolEquals(document, "sameNameAsOnePhoto", true),
            "通用设置落盘", 30);
        var after = ReadSettingsDocument();
        check("标准版保存保留不可见字段", SettingsBoolEquals(after, "analysisImmediateCompute", state.DinoImmediate)
            && SettingsNumberEquals(after, "similarityThreshold", state.Threshold)
            && SettingsNumberEquals(after, "similarityMaxResults", state.MaxResults)
            && ReadViewStateBool(SettingsViewStateKeys.SimilarityPanelExpanded),
            SummarizeSettings(after));
        check("标准版未清理既有特征", (await ComputeFeatureDigestAsync()).Equals(state.Features), "特征表逐行不变");
    }

    /// <summary>CV 诊断页(标准版):三瓦片现算、状态文本与不写库。</summary>
    /// <param name="presenter">工具窗口展示器。</param>
    /// <returns>结果与说明。</returns>
    private async Task<(bool Ok, string Detail)> VerifyCvToolPageAsync(WindowsNativeToolsPresenter presenter)
    {
        var before = await ComputeFeatureDigestAsync();
        _main.OpenToolsWindow();
        await WaitForSmokeAsync(() => presenter.Window?.Root.XamlRoot != null, "CV 诊断页:工具窗口", 30);
        var window = presenter.Window!;
        try
        {
            window.SelectPage(ToolsPage.Diagnostic, openShared: true);
            await WaitForSmokeAsync(() => _main.Tools.CvPresenter.Status == _main.CurrentFile?.Name, "CV 诊断页现算", 120);
            RequireSmoke(_main.Tools.CvPresenter.Tiles.Count == 3,
                $"CV 诊断页瓦片数不是 3: {_main.Tools.CvPresenter.Tiles.Count}");
            _main.Tools.CvPresenter.OnTileClicked(0.4, 0.6);
            RequireSmoke(Math.Abs((_main.Tools.CvPresenter.CrosshairU ?? -1) - 0.4) < 0.01,
                "CV 诊断页点按未移动准星");
            RequireSmoke((await ComputeFeatureDigestAsync()).Equals(before), "CV 诊断页写入了特征数据库");
            return (true, $"status={_main.Tools.CvPresenter.Status} tiles=3 shared crosshair in-memory only");
        }
        catch (Exception exception)
        {
            return (false, exception.Message);
        }
        finally
        {
            window.Close();
            await WaitForSmokeAsync(() => !presenter.IsOpen, "CV 诊断页:工具窗口关闭", 20);
        }
    }
#endif

#if !PHOTOVIEWER_DINO
    /// <summary>ai 阶段在标准版构建上没有意义。</summary>
    /// <param name="check">断言函数。</param>
    /// <returns>失败的阶段任务。</returns>
    private Task VerifyAiRoundtripAsync(Action<string, bool, string> check)
        => Task.FromException(new InvalidOperationException("ai 阶段只能在 AI 版构建上运行"));
#else
    /// <summary>ai 阶段:核对设置哨兵、展开态、通用设置修改与特征库全部恢复,DINO 结果可直接显示。</summary>
    /// <param name="check">断言函数。</param>
    /// <returns>阶段完成后的任务。</returns>
    private async Task VerifyAiRoundtripAsync(Action<string, bool, string> check)
    {
        var state = ReadRoundtripState();
        await OpenRoundtripCurrentFileAsync();

        check("AI 版编译能力", WindowsProductInfo.HasDino
            && AssemblyHasType(typeof(AnalysisPresenter).Assembly, "PhotoViewer.Core.AI.DinoFeatureExtractor")
            && AssemblyHasType(typeof(WindowsMainWindow).Assembly, "PhotoViewer.Windows.Native.WindowsSimilarityPanel"),
            $"hasDino={WindowsProductInfo.HasDino} product={WindowsProductInfo.DisplayName}");
        check("设置哨兵恢复", Math.Abs(_main.Settings.SimilarityThreshold - state.Threshold) < 1e-9
            && _main.Settings.SimilarityMaxResults == state.MaxResults
            && _main.Settings.AnalysisImmediateCompute == state.DinoImmediate
            && _main.Settings.AnalysisImmediateCvCompute == state.CvImmediate,
            $"threshold={_main.Settings.SimilarityThreshold} max={_main.Settings.SimilarityMaxResults}"
                + $" dino={_main.Settings.AnalysisImmediateCompute} cv={_main.Settings.AnalysisImmediateCvCompute}");
        check("标准版修改的通用设置生效", _main.Settings.SameNameAsOnePhoto,
            $"sameNameAsOnePhoto={_main.Settings.SameNameAsOnePhoto}");
        check("相似面板展开态恢复", _main.FileVM.IsSimilarityPanelOpen && _filterBar.Children.Count == 5,
            $"open={_main.FileVM.IsSimilarityPanelOpen} filterItems={_filterBar.Children.Count}");

        var digest = await ComputeFeatureDigestAsync();
        check("既有特征逐行不变", digest.Equals(state.Features), digest.Summary);

        _main.ToggleAnalysisView();
        await WaitForSmokeAsync(() => _analysis.Visibility == Visibility.Visible, "AI 版分析栏可见", 20);
        _root.UpdateLayout();
        await WaitForSmokeAsync(() => _analysis.DinoDiagnosticsReady, "AI 版 DINO 瓦片读库出图", 120);
        check("AI 版瓦片与 DINO 结果", _analysis.DiagnosticTileCount == 4
            && _analysis.TileLabels.Any(label => label == "DINO PCA")
            && _analysis.TileLabels.Any(label => label.StartsWith("Cosine", StringComparison.Ordinal)),
            $"tiles={_analysis.TileCount} labels=[{string.Join("/", _analysis.TileLabels)}]");
        var after = await ComputeFeatureDigestAsync();
        check("AI 版读库不改写特征", after.Equals(state.Features), after.Summary);
        _main.ToggleAnalysisView();
    }
#endif

    /// <summary>发行版编译能力:报告产品名与 DINO/相似聚类类型是否真实存在。</summary>
    /// <returns>验收文案。</returns>
    private static string CheckEditionCapability()
    {
        var core = typeof(AnalysisPresenter).Assembly;
        var shell = typeof(WindowsMainWindow).Assembly;
        string[] dinoTypes =
        [
            "PhotoViewer.Core.AI.DinoDiagnosticPresenter",
            "PhotoViewer.Core.AI.DinoFeatureExtractor",
            "PhotoViewer.Core.AI.FolderFeatureIndexer",
            "PhotoViewer.Core.AI.SimilarityService",
            "PhotoViewer.ViewModels.Main.File.SimilarityPanelViewModel",
            "PhotoViewer.ViewModels.Tools.DinoDebugViewModel",
        ];
        var present = dinoTypes.Where(name => core.GetType(name) != null).ToArray();
        bool panel = shell.GetType("PhotoViewer.Windows.Native.WindowsSimilarityPanel") != null;
        RequireSmoke(present.Length == (WindowsProductInfo.HasDino ? dinoTypes.Length : 0),
            $"发行版与 DINO 类型不一致: edition={WindowsProductInfo.EditionName} present=[{string.Join(",", present)}]");
        RequireSmoke(panel == WindowsProductInfo.HasDino, $"原生相似面板类型与发行版不一致: panel={panel}");
        RequireSmoke(WindowsProductInfo.DiagnosticName == (WindowsProductInfo.HasDino ? "DINO 诊断" : "CV 诊断"),
            $"诊断页名与发行版不一致: {WindowsProductInfo.DiagnosticName}");
        return $"PASS edition capability: edition={WindowsProductInfo.EditionName} product={WindowsProductInfo.DisplayName}"
            + $" hasDino={WindowsProductInfo.HasDino} diagnostic={WindowsProductInfo.DiagnosticName}"
            + $" dinoTypes={present.Length} nativePanel={panel}";
    }

    /// <summary>产品身份:实际编译进产物的包 ID/产品键与 edition 一致，单实例键按产品分离。</summary>
    /// <returns>验收文案。</returns>
    private static string CheckEditionIdentity()
    {
        RequireSmoke(
            WindowsProductInfo.PackageId.StartsWith("cc.xiaojh.PhotoViewer", StringComparison.Ordinal)
                && (WindowsProductInfo.HasDino
                    == WindowsProductInfo.PackageId.EndsWith(
                        WindowsProductInfo.AiPackageIdSuffix, StringComparison.Ordinal)),
            $"包 ID 与发行版本不一致: edition={WindowsProductInfo.EditionName} packageId={WindowsProductInfo.PackageId}");
        RequireSmoke(WindowsProductInfo.ProductId == $"photoviewer.{WindowsProductInfo.EditionName}",
            $"内部产品键与发行版本不一致: edition={WindowsProductInfo.EditionName} productId={WindowsProductInfo.ProductId}");
        RequireSmoke(WindowsProductInfo.SiblingPackageId != WindowsProductInfo.PackageId,
            $"标准版与 AI 版包 ID 相同: {WindowsProductInfo.PackageId}");
        RequireSmoke(WindowsSingleInstance.InstanceKey == WindowsProductInfo.PackageId + WindowsSingleInstance.InstanceKeySuffix,
            $"单实例键未按包 ID 派生: key={WindowsSingleInstance.InstanceKey}");
        RequireSmoke(WindowsSingleInstance.InstanceKey != WindowsSingleInstance.SiblingInstanceKey,
            $"单实例键跨版相同: {WindowsSingleInstance.InstanceKey}");
        return $"PASS edition identity: packageId={WindowsProductInfo.PackageId} productId={WindowsProductInfo.ProductId}"
            + $" distribution={WindowsProductInfo.DistributionName} instanceKey={WindowsSingleInstance.InstanceKey}"
            + $" siblingKey={WindowsSingleInstance.SiblingInstanceKey}";
    }

    /// <summary>筛选条按版本实际项数构成:标准版四项、AI 版五项,且末项真实排在前一项右侧。</summary>
    /// <returns>验收文案。</returns>
    private string CheckEditionFilterBar()
    {
        RequireSmoke(_filterBar.Children.Count == (WindowsProductInfo.HasDino ? 5 : 4),
            $"筛选条项数与发行版不一致: {_filterBar.Children.Count}");
        RequireSmoke(WindowsFilterBar.ShowsSimilarityToggle == WindowsProductInfo.HasDino,
            $"筛选条开关声明与发行版不一致: {WindowsFilterBar.ShowsSimilarityToggle}");
        _root.UpdateLayout();
        // 行布局按横轴、列布局按纵轴排列:只比主轴,
        // 交叉轴上的居中偏移(计数与开关在 116 栏内居中)不参与顺序判定。
        bool horizontal = _main.IsRowLayout;
        var axis = _filterBar.Children
            .OfType<FrameworkElement>()
            .Select(child =>
            {
                var origin = child.TransformToVisual(_filterBar).TransformPoint(new Point(0, 0));
                return horizontal ? origin.X : origin.Y;
            })
            .ToArray();
        RequireSmoke(axis.Length >= 4 && axis.SequenceEqual(axis.OrderBy(value => value)),
            $"筛选条控件未按主轴顺序排布: [{string.Join(",", axis.Select(value => value.ToString("0")))}]");
        var labels = _filterBar.Children
            .Select(child => child switch
            {
                ComboBox => "combo",
                ToggleButton => "toggle",
                TextBlock => "count",
                _ => "other",
            })
            .ToArray();
        RequireSmoke(labels.Length > 0 && labels[labels.Length - 1] == (WindowsProductInfo.HasDino ? "toggle" : "count"),
            $"筛选条末项不是{(WindowsProductInfo.HasDino ? "相似聚类开关" : "计数")}: [{string.Join(",", labels)}]");
        return $"PASS edition filter bar: {_filterBar.Children.Count} actual items [{string.Join(",", labels)}]"
            + $" {(horizontal ? "row" : "column")} axis=[{string.Join(",", axis.Select(value => value.ToString("0")))}]";
    }

    /// <summary>分析栏实际瓦片数:AI 版 6/7 张含 PCA/Cosine,标准版 4/5 张且无 DINO。</summary>
    /// <returns>验收文案。</returns>
    private async Task<string> CheckEditionAnalysisAsync()
    {
        bool previous = _main.IsAnalysisViewVisible;
        try
        {
            _main.IsAnalysisViewVisible = true;
            await WaitForSmokeAsync(() => _analysis.Visibility == Visibility.Visible, "发行版分析栏可见", 20);
            _root.UpdateLayout();
            await WaitForSmokeAsync(() => _analysis.TileSide > 40, "发行版分析栏瓦片尺寸", 20);
            await WaitForSmokeAsync(() => _analysis.TilesUniformlyArranged, "发行版分析栏瓦片等边长", 20);
            var labels = _analysis.TileLabels;
#if PHOTOVIEWER_DINO
            RequireSmoke(_analysis.TileCount is 6 or 7 && _analysis.DiagnosticTileCount == 4
                && labels.Any(label => label == "DINO PCA")
                && labels.Any(label => label.StartsWith("Cosine", StringComparison.Ordinal)),
                $"AI 版分析栏不是 6/7 瓦片含 DINO: tiles={_analysis.TileCount}"
                    + $" diagnostics={_analysis.DiagnosticTileCount} labels=[{string.Join("/", labels)}]");
#else
            RequireSmoke(_analysis.TileCount is 4 or 5 && _analysis.DiagnosticTileCount == 2
                && !labels.Any(label => label.Contains("DINO", StringComparison.Ordinal)
                    || label.StartsWith("Cosine", StringComparison.Ordinal)),
                $"标准版分析栏不是 4/5 瓦片无 DINO: tiles={_analysis.TileCount}"
                    + $" diagnostics={_analysis.DiagnosticTileCount} labels=[{string.Join("/", labels)}]");
#endif

            return $"PASS edition analysis panel: {_analysis.TileCount} tiles ({_analysis.DiagnosticTileCount} diagnostics)"
                + $" side={_analysis.TileSide:0.#} labels=[{string.Join("/", labels)}]";
        }
        finally
        {
            _main.IsAnalysisViewVisible = previous;
            await Task.Delay(150);
        }
    }

    /// <summary>诊断工具页:分类名、瓦片数与现算(标准版另核对不写库)。</summary>
    /// <param name="presenter">工具窗口展示器。</param>
    /// <returns>验收文案。</returns>
    private async Task<string> CheckEditionToolPageAsync(WindowsNativeToolsPresenter presenter)
    {
        _main.OpenToolsWindow();
        await WaitForSmokeAsync(() => presenter.Window?.Root.XamlRoot != null, "发行版工具窗口", 30);
        var window = presenter.Window ?? throw new InvalidOperationException("工具窗口未创建");
        try
        {
            var categories = window.Navigation.MenuItems.OfType<NavigationViewItem>()
                .Select(item => item.Content?.ToString()).ToArray();
            RequireSmoke(categories.SequenceEqual(new[] { "EXIF 详情", WindowsProductInfo.DiagnosticName, "照片数据统计" }),
                $"工具分类不是 EXIF/{WindowsProductInfo.DiagnosticName}/照片数据统计: [{string.Join(",", categories)}]");

            window.SelectPage(ToolsPage.Diagnostic, openShared: true);
            await WaitForSmokeAsync(() => window.SelectedPage == ToolsPage.Diagnostic, "发行版诊断页选中", 20);
            await WaitForSmokeAsync(() => window.DiagnosticPage.HasAllTiles, "发行版诊断瓦片出图", 120);
            window.Root.UpdateLayout();
            var tiles = window.DiagnosticPage.Tiles;
            RequireSmoke(tiles.Length == (WindowsProductInfo.HasDino ? 5 : 3),
                $"诊断页瓦片数不是 {(WindowsProductInfo.HasDino ? 5 : 3)}: {tiles.Length}");
            RequireSmoke(tiles.All(tile => tile.Side > 0), "诊断瓦片没有实际尺寸");
            var rows = tiles
                .Select(tile => tile.TransformToVisual(window.DiagnosticPage).TransformPoint(new Point(0, 0)).Y)
                .Select(value => Math.Round(value))
                .Distinct()
                .Count();
            RequireSmoke(rows <= 2, $"诊断瓦片行错位: {rows} 行");
#if PHOTOVIEWER_DINO
            var dino = _main.Tools.DinoPresenter;
            dino.OnTileClicked(0.25, 0.25);
            await WaitForSmokeAsync(() => dino.RefGridX == 8 && dino.RefGridY == 8, "DINO 诊断 cosine 参考点", 20);
            string detail = $"reference={dino.RefGridX},{dino.RefGridY}";
#else
            var before = await ComputeFeatureDigestAsync();
            var cv = _main.Tools.CvPresenter;
            cv.OnTileClicked(0.25, 0.25);
            await WaitForSmokeAsync(() => Math.Abs((cv.CrosshairU ?? -1) - 0.25) < 0.01, "CV 诊断准星", 20);
            RequireSmoke((await ComputeFeatureDigestAsync()).Equals(before), "CV 诊断页现算写入了特征数据库");
            string detail = "crosshair=0.25,0.25 in-memory only";
#endif

            return $"PASS edition tool page: category={WindowsProductInfo.DiagnosticName} tiles={tiles.Length}"
                + $" side={tiles[0].Side:0.#} rows={rows} {detail} status={window.DiagnosticPage.StatusText}";
        }
        finally
        {
            window.Close();
            await WaitForSmokeAsync(() => !presenter.IsOpen, "发行版工具窗口关闭", 20);
        }
    }

    /// <summary>设置 AI 页按版本裁剪:标准版只有即时计算 CV,AI 版有相似聚类参数与两个独立开关。</summary>
    /// <param name="presenter">设置窗口展示器。</param>
    /// <returns>验收文案。</returns>
    private async Task<string> CheckEditionSettingsPageAsync(WindowsNativeSettingsPresenter presenter)
    {
        presenter.TryPresent(_main.Settings);
        await WaitForSmokeAsync(() => presenter.Window?.Root.XamlRoot != null, "发行版设置窗口", 30);
        var window = presenter.Window ?? throw new InvalidOperationException("设置窗口未创建");
        try
        {
            window.Navigation.SelectedItem = window.Navigation.MenuItems[4];
            await Task.Delay(150);
            var page = window.Pages[4];
            page.UpdateLayout();
            var texts = CollectTexts(page).ToList();
            var toggles = CollectOfType<ToggleSwitch>(page).ToList();
            bool hasCv = texts.Any(text => text == "即时计算 CV");
            bool hasDino = texts.Any(text => text.Contains("DINO", StringComparison.Ordinal));
            bool hasSimilarity = texts.Any(text => text.Contains("相似", StringComparison.Ordinal));
#if PHOTOVIEWER_DINO
            RequireSmoke(hasCv && hasDino && hasSimilarity && toggles.Count == 2,
                $"AI 版设置页控件不完整: cv={hasCv} dino={hasDino} similarity={hasSimilarity} toggles={toggles.Count}");
#else
            RequireSmoke(hasCv && !hasDino && !hasSimilarity && toggles.Count == 1,
                $"标准版设置页不只有即时计算 CV: cv={hasCv} dino={hasDino} similarity={hasSimilarity} toggles={toggles.Count}");
#endif

            return $"PASS edition settings AI page: toggles={toggles.Count} cv={hasCv} dino={hasDino} similarity={hasSimilarity}";
        }
        finally
        {
            await presenter.CloseIfOpenAsync();
            await Task.Delay(120);
        }
    }

    /// <summary>
    /// 独立即时计算开关与库边界:标准版验证 CV 开关;AI 版验证四种组合互不牵连,
    /// 两种版本都确认现算不写特征数据库。
    /// </summary>
    /// <returns>验收文案。</returns>
    private async Task<List<string>> CheckEditionSwitchBoundaryAsync()
    {
        var checks = new List<string>();
        bool previousVisible = _main.IsAnalysisViewVisible;
        bool previousDino = _main.Settings.AnalysisImmediateCompute;
        bool previousCv = _main.Settings.AnalysisImmediateCvCompute;
        var before = await ComputeFeatureDigestAsync();
        try
        {
            _main.IsAnalysisViewVisible = true;
            await WaitForSmokeAsync(() => _analysis.Visibility == Visibility.Visible, "开关检查:分析栏可见", 20);
#if PHOTOVIEWER_DINO
            (bool Dino, bool Cv)[] combinations = [(false, false), (false, true), (true, false), (true, true)];
#else
            (bool Dino, bool Cv)[] combinations = [(false, false), (false, true)];
#endif
            foreach (var (dino, cv) in combinations)
            {
                AnalysisResultCache.InvalidateAll();
                _main.Settings.AnalysisImmediateCompute = dino;
                _main.Settings.AnalysisImmediateCvCompute = cv;
                await WaitForSmokeAsync(() =>
                {
                    bool cvReady = cv
                        ? _analysis.CvDiagnosticsReady
                        : !_analysis.CvDiagnosticsReady && _analysis.CvDiagnosticsPlaceholder;
#if PHOTOVIEWER_DINO
                    bool dinoReady = dino
                        ? _analysis.DinoDiagnosticsReady
                        : !_analysis.DinoDiagnosticsReady && _analysis.DinoDiagnosticsPlaceholder;
                    return cvReady && dinoReady;
#else
                    return cvReady;
#endif
                }, $"即时计算 DINO={dino} CV={cv}", 120);
                checks.Add($"PASS edition immediate switch: DINO={dino} CV={cv} computes only the enabled capability");
            }

            // 成功图 -> 关闭两侧现算:视图必须清掉旧图并显示「未提取」,不能只看 presenter 状态。
            _main.Settings.AnalysisImmediateCompute = false;
            _main.Settings.AnalysisImmediateCvCompute = false;
            AnalysisResultCache.InvalidateAll();
            await WaitForSmokeAsync(() => _analysis.SharpnessPlaceholder == "未提取", "关闭现算后回到未提取", 90);
            await Task.Delay(250);
            RequireSmoke(_analysis.DiagnosticViewsCleared,
                $"关闭现算后诊断瓦片仍显示旧内容: {_analysis.DiagnosticViewStaleContent}");
            checks.Add($"PASS edition tile clear: after a successful frame, turning the immediate toggles off empties every diagnostic tile and shows 未提取"
                + $"{(WindowsProductInfo.HasDino ? " (incl. PCA/Cosine)" : string.Empty)}");

            RequireSmoke((await ComputeFeatureDigestAsync()).Equals(before), "即时计算改写了特征数据库");
            checks.Add("PASS edition compute boundary: analysis-panel compute stays in memory and does not write photos.cv_grid / photo_features / photo_patches");
        }
        finally
        {
            _main.Settings.AnalysisImmediateCompute = previousDino;
            _main.Settings.AnalysisImmediateCvCompute = previousCv;
            _main.IsAnalysisViewVisible = previousVisible;
            await Task.Delay(150);
        }

        return checks;
    }

    /// <summary>窗口标题使用本发行版产品名。</summary>
    /// <returns>验收文案。</returns>
    private string CheckEditionTitle()
    {
        RequireSmoke(Title.EndsWith(WindowsProductInfo.DisplayName, StringComparison.Ordinal),
            $"窗口标题未以产品名结尾: {Title}");
        return $"PASS edition title: {Title}";
    }

    /// <summary>打开往返用的当前照片(三阶段固定同一张)。</summary>
    /// <returns>打开完成后的任务。</returns>
    private async Task OpenRoundtripCurrentFileAsync()
    {
        var path = Path.Combine(ResolveRoundtripFixtureFolder(), RoundtripCurrentFileName);
        await OpenPathsAsync(new[] { path });
        await WaitForSmokeAsync(() => _main.CurrentFile?.Name == RoundtripCurrentFileName && _session.Image != null,
            "往返当前照片上屏", 40);
    }

    /// <summary>取往返素材目录(素材根下的 jpeg 目录，只读)。</summary>
    /// <returns>绝对路径。</returns>
    private static string ResolveRoundtripFixtureFolder()
        => WindowsAcceptanceFixtures.GetDirectory(RoundtripFixtureFolderName);

    /// <summary>等待设置文档真实落盘到 prepare 哨兵值。</summary>
    /// <param name="threshold">阈值哨兵。</param>
    /// <param name="maxResults">最多数量哨兵。</param>
    /// <returns>落盘完成后的任务。</returns>
    private static Task WaitForRoundtripSettingsAsync(double threshold, int maxResults)
        => WaitForSettingsFileAsync(
            document => SettingsNumberEquals(document, "similarityThreshold", threshold)
                && SettingsNumberEquals(document, "similarityMaxResults", maxResults),
            "往返设置落盘",
            30);

    /// <summary>等待设置文档满足条件(真实读文件,不使用内存值)。</summary>
    /// <param name="predicate">文档判定。</param>
    /// <param name="stage">超时描述。</param>
    /// <param name="timeoutSeconds">超时秒数。</param>
    /// <returns>落盘完成后的任务。</returns>
    private static async Task WaitForSettingsFileAsync(Func<JsonDocument, bool> predicate, string stage, int timeoutSeconds)
    {
        var path = WindowsNativeApplication.IsolatedSettingsFilePath;
        await WaitForSmokeAsync(() =>
        {
            if (!File.Exists(path)) return false;
            try
            {
                using var document = JsonDocument.Parse(File.ReadAllText(path));
                return predicate(document);
            }
            catch (JsonException)
            {
                return false;
            }
        }, stage, timeoutSeconds);
    }

    /// <summary>读取当前照片指纹。</summary>
    /// <returns>指纹;无法计算时抛错。</returns>
    private async Task<string> CurrentFingerprintAsync()
    {
        var file = _main.CurrentFile ?? throw new InvalidOperationException("没有当前照片");
        return await AnalysisDataReader.ComputeFingerprintAsync(file, CancellationToken.None)
            ?? throw new InvalidOperationException("当前照片无法计算指纹");
    }

    /// <summary>
    /// 特征库逐行摘要:photo_features(CLS)、photo_patches(patch token)与 photos.cv_grid 三个 blob 列的
    /// 指纹与 SHA-256。标准版没有 DINO 类型,因此直接用 SQL 读表,不依赖任何 DINO 解析器。
    /// </summary>
    /// <returns>可比对的摘要。</returns>
    private static async Task<FeatureDigest> ComputeFeatureDigestAsync()
    {
        var rows = new List<FeatureRow>();
        await using var connection = PhotoDatabase.OpenConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT 'cls', fingerprint, model_id, cls_vector, length(cls_vector) FROM photo_features
            UNION ALL
            SELECT 'patch', fingerprint, model_id, patch_tokens, length(patch_tokens) FROM photo_patches
            UNION ALL
            SELECT 'cv', fingerprint, coalesce(cv_grid_spec,''), cv_grid, length(cv_grid) FROM photos WHERE cv_grid IS NOT NULL
            ORDER BY 1, 2
            """;
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var kind = reader.GetString(0);
            var fingerprint = reader.GetString(1);
            var modelId = reader.GetString(2);
            var bytes = reader.IsDBNull(3) ? Array.Empty<byte>() : (byte[])reader.GetValue(3);
            var length = reader.IsDBNull(4) ? 0 : reader.GetInt32(4);
            rows.Add(new FeatureRow(kind, fingerprint, modelId, length, Convert.ToHexString(SHA256.HashData(bytes))));
        }

        return new FeatureDigest { Rows = rows };
    }

    /// <summary>按类型名检索程序集里的实际类型(标准版应为 null)。</summary>
    /// <param name="assembly">目标程序集。</param>
    /// <param name="typeName">完整类型名。</param>
    /// <returns>类型是否存在。</returns>
    private static bool AssemblyHasType(System.Reflection.Assembly assembly, string typeName)
    {
        try
        {
            return assembly.GetType(typeName, throwOnError: false) != null;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>读取设置文档(原始 JSON)。</summary>
    /// <returns>文档;文件不存在时抛错。</returns>
    private static JsonDocument ReadSettingsDocument()
    {
        var path = WindowsNativeApplication.IsolatedSettingsFilePath;
        if (!File.Exists(path)) throw new InvalidOperationException($"设置文档不存在: {path}");
        return JsonDocument.Parse(File.ReadAllText(path));
    }

    /// <summary>读取布尔设置并比对。</summary>
    /// <param name="document">设置文档。</param>
    /// <param name="name">属性名(大小写不敏感)。</param>
    /// <param name="expected">期望值。</param>
    /// <returns>是否一致。</returns>
    private static bool SettingsBoolEquals(JsonDocument document, string name, bool expected)
    {
        if (!TryGetSetting(document, name, out var element)) return false;
        return element.ValueKind switch
        {
            JsonValueKind.True => expected,
            JsonValueKind.False => !expected,
            _ => false,
        };
    }

    /// <summary>读取数值设置并比对(容差 1e-9)。</summary>
    /// <param name="document">设置文档。</param>
    /// <param name="name">属性名(大小写不敏感)。</param>
    /// <param name="expected">期望值。</param>
    /// <returns>是否一致。</returns>
    private static bool SettingsNumberEquals(JsonDocument document, string name, double expected)
    {
        if (!TryGetSetting(document, name, out var element)) return false;
        return element.ValueKind == JsonValueKind.Number && Math.Abs(element.GetDouble() - expected) < 1e-9;
    }

    /// <summary>大小写不敏感地取一个设置项。</summary>
    /// <param name="document">设置文档。</param>
    /// <param name="name">属性名。</param>
    /// <param name="element">取值。</param>
    /// <returns>是否存在。</returns>
    private static bool TryGetSetting(JsonDocument document, string name, out JsonElement element)
    {
        element = default;
        if (document.RootElement.ValueKind != JsonValueKind.Object) return false;
        foreach (var property in document.RootElement.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                element = property.Value;
                return true;
            }
        }

        return false;
    }

    /// <summary>设置文档里与本验收相关的键值摘要。</summary>
    /// <param name="document">设置文档。</param>
    /// <returns>可读摘要。</returns>
    private static string SummarizeSettings(JsonDocument document)
    {
        var parts = new List<string>();
        foreach (var name in new[]
        {
            "similarityThreshold", "similarityMaxResults", "analysisImmediateCompute",
            "analysisImmediateCvCompute", "sameNameAsOnePhoto",
        })
        {
            parts.Add(name + "=" + (TryGetSetting(document, name, out var element) ? element.ToString() : "<missing>"));
        }

        return string.Join(" ", parts);
    }

    /// <summary>读取隔离界面状态里的布尔值。</summary>
    /// <param name="key">键名。</param>
    /// <returns>取值;缺失或非法为 false。</returns>
    private static bool ReadViewStateBool(string key)
    {
        var path = WindowsNativePreferences.IsolatedFilePath;
        if (!File.Exists(path)) return false;
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            return document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty(key, out var element)
                && element.ValueKind == JsonValueKind.True;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>写往返基线状态。</summary>
    /// <param name="state">状态。</param>
    private static void WriteRoundtripState(RoundtripState state)
        => File.WriteAllText(
            Path.Combine(SmokeOutputDirectory, RoundtripStateFileName),
            JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true }));

    /// <summary>读往返基线状态。</summary>
    /// <returns>状态。</returns>
    private static RoundtripState ReadRoundtripState()
    {
        var path = Path.Combine(SmokeOutputDirectory, RoundtripStateFileName);
        if (!File.Exists(path)) throw new InvalidOperationException($"缺少往返基线: {path}(先跑 prepare 阶段)");
        return JsonSerializer.Deserialize<RoundtripState>(File.ReadAllText(path))
            ?? throw new InvalidOperationException("往返基线无法解析");
    }

    /// <summary>收集子树里的文本。</summary>
    /// <param name="root">根元素。</param>
    /// <returns>文本序列。</returns>
    private static IEnumerable<string> CollectTexts(DependencyObject root)
        => CollectOfType<TextBlock>(root).Select(text => text.Text);

    /// <summary>收集子树里指定类型的元素。</summary>
    /// <typeparam name="T">目标类型。</typeparam>
    /// <param name="root">根元素。</param>
    /// <returns>元素序列。</returns>
    private static IEnumerable<T> CollectOfType<T>(DependencyObject root) where T : DependencyObject
    {
        var count = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(root);
        for (var index = 0; index < count; index++)
        {
            var child = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(root, index);
            if (child is T match)
            {
                yield return match;
            }

            foreach (var nested in CollectOfType<T>(child))
            {
                yield return nested;
            }
        }
    }

    /// <summary>发行版验收素材:小尺寸彩色 JPEG(每张颜色不同,便于区分相似度)。</summary>
    /// <param name="path">目标路径。</param>
    /// <param name="index">序号。</param>
    private static void CreateEditionFixture(string path, int index)
    {
        using var bitmap = new SKBitmap(480, 320);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(new SKColor((byte)(index * 40 % 256), (byte)(index * 70 % 256), (byte)(255 - (index * 25 % 200))));
        using var encoded = bitmap.Encode(SKEncodedImageFormat.Jpeg, 88);
        using var stream = File.Create(path);
        encoded.SaveTo(stream);
    }

    /// <summary>往返基线:阶段、设置哨兵与特征摘要。</summary>
    private sealed class RoundtripState
    {
        /// <summary>产出该基线的阶段。</summary>
        public string Stage { get; set; } = string.Empty;

        /// <summary>产出该基线的发行版。</summary>
        public string Edition { get; set; } = string.Empty;

        /// <summary>产物显示名。</summary>
        public string Product { get; set; } = string.Empty;

        /// <summary>产物 EXE 路径。</summary>
        public string ExePath { get; set; } = string.Empty;

        /// <summary>当前照片文件名。</summary>
        public string CurrentFile { get; set; } = string.Empty;

        /// <summary>当前照片指纹。</summary>
        public string Fingerprint { get; set; } = string.Empty;

        /// <summary>相似度阈值哨兵。</summary>
        public double Threshold { get; set; }

        /// <summary>相似聚类最多数量哨兵。</summary>
        public int MaxResults { get; set; }

        /// <summary>相似面板展开态哨兵。</summary>
        public bool PanelExpanded { get; set; }

        /// <summary>即时计算 DINO 哨兵。</summary>
        public bool DinoImmediate { get; set; }

        /// <summary>即时计算 CV 哨兵。</summary>
        public bool CvImmediate { get; set; }

        /// <summary>通用设置哨兵(标准版会改写)。</summary>
        public bool SameNameAsOnePhoto { get; set; }

        /// <summary>特征库摘要。</summary>
        public FeatureDigest Features { get; set; } = new();
    }

    /// <summary>特征库摘要。</summary>
    private sealed class FeatureDigest : IEquatable<FeatureDigest>
    {
        /// <summary>逐行结果(已按类型/指纹排序)。</summary>
        public List<FeatureRow> Rows { get; set; } = [];

        /// <summary>可读摘要。</summary>
        public string Summary => $"rows={Rows.Count}"
            + (Rows.Count == 0
                ? string.Empty
                : " first=" + Rows[0].Kind + ":" + Rows[0].Fingerprint[..Math.Min(8, Rows[0].Fingerprint.Length)]);

        /// <inheritdoc/>
        public bool Equals(FeatureDigest? other)
            => other != null && Rows.Count == other.Rows.Count && Rows.SequenceEqual(other.Rows);

        /// <inheritdoc/>
        public override bool Equals(object? obj) => Equals(obj as FeatureDigest);

        /// <inheritdoc/>
        public override int GetHashCode()
        {
            var hash = new HashCode();
            foreach (var row in Rows) hash.Add(row);
            return hash.ToHashCode();
        }
    }

    /// <summary>特征库单行(表、指纹、模型、字节数与 SHA-256)。</summary>
    /// <param name="Kind">表类型(cls/patch/cv)。</param>
    /// <param name="Fingerprint">照片指纹。</param>
    /// <param name="ModelId">模型标识(cv 行为 spec)。</param>
    /// <param name="Length">字节数。</param>
    /// <param name="Sha256">内容摘要。</param>
    private sealed record FeatureRow(string Kind, string Fingerprint, string ModelId, int Length, string Sha256);
}
