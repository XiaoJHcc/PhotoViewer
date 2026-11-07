using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Android.App;
using Android.Content;
using Android.OS;
using PhotoViewer.Android.Core;
using PhotoViewer.Core.AI;
using PhotoViewer.Core.Database;
using PhotoViewer.Core.Image;
using PhotoViewer.Core.Settings;
using PhotoViewer.ViewModels.Main;
using IOPath = System.IO.Path;

namespace PhotoViewer.Android.Native;

/// <summary>
/// <c>--es pv-check roundtrip --es pv-roundtrip prepare|standard|ai</c>（验收专用）：
/// AI → 标准 → AI 的跨进程 / 覆盖安装往返，核对设置哨兵、相似面板展开态与特征库（CLS / patch / CV）逐字节不变。
/// </summary>
/// <remarks>
/// 三阶段共用隔离设置档 <c>edition-roundtrip</c> 与隔离数据库 <c>files/PhotoViewer/edition-roundtrip/photos.db</c>
/// （由 <see cref="AndroidNativeShell"/> 在 <see cref="MainViewModel"/> 建立前配置），夹具、状态文件与报告也只写 App 内部目录，
/// 不读也不写用户真实设置与照片库。数据库不清理：重复 prepare 走「已入库」快路径，哈希应保持一致。
/// </remarks>
internal static partial class AndroidNativeChecks
{
    private const string RoundtripProfileName = "edition-roundtrip";
    private const string RoundtripFolderName = "edition-roundtrip";
    private const string RoundtripStateFileName = "edition-roundtrip-state.json";
    private const string RoundtripFixtureFolderName = "fixture";
    private const string RoundtripFixtureFileName = "roundtrip-fixture.jpg";
    private const string SettingsDocumentKey = "settings_json";

    /// <summary>标准版必须不存在的 DINO / 相似聚类类型；按程序集实际类型判定，不做源码或行数检查。</summary>
    private static readonly string[] DinoOnlyTypeNames =
    [
        "PhotoViewer.Core.AI.DinoDiagnosticPresenter",
        "PhotoViewer.Core.AI.DinoFeatureCache",
        "PhotoViewer.Core.AI.DinoFeatureExtractor",
        "PhotoViewer.Core.AI.DinoModelResources",
        "PhotoViewer.Core.AI.FolderFeatureIndexer",
        "PhotoViewer.Core.AI.PatchHeatmap",
        "PhotoViewer.Core.AI.SimilarityService",
        "PhotoViewer.ViewModels.Main.File.SimilarityPanelViewModel",
        "PhotoViewer.ViewModels.Tools.DinoDebugViewModel",
    ];

    private static bool _roundtripStarted;

    /// <summary>注入 pv-check=roundtrip 时接管检查：记录宿主、清空报告缓冲，并在主屏建立后跑对应阶段。</summary>
    /// <param name="activity">宿主 Activity。</param>
    /// <param name="main">共享主 VM。</param>
    /// <param name="session">展示会话。</param>
    /// <param name="root">主屏根视图。</param>
    /// <param name="stage">阶段（prepare / standard / ai）。</param>
    private static void StartRoundtrip(Activity activity, MainViewModel main, PhotoLoadSession session,
        AndroidNativeRootView root, string? stage)
    {
        // Activity 重建会再次走到这里；同一进程只跑一轮，避免重复写 STAGE 区块。
        if (_roundtripStarted) return;
        _roundtripStarted = true;
        _activity = activity;
        _main = main;
        _session = session;
        _root = root;
        _mode = "roundtrip";
        Lines.Clear();
        _passed = 0;
        _failed = 0;
        root.KeepScreenOn = true;
        Console.WriteLine($"[NativeCheck] 启动往返检查 stage={stage ?? "null"} edition={AndroidAppCapabilities.EditionName}");
        var handler = new Handler(Looper.MainLooper!);
        handler.PostDelayed(() => _ = RunRoundtripStageAsync(stage), 800);
    }

    /// <summary>执行一个往返阶段：先落 STAGE START，再逐条 Check，最后落 STAGE PASS/FAIL（不覆盖历史阶段区块）。</summary>
    /// <param name="rawStage">Intent 里的阶段参数。</param>
    private static async Task RunRoundtripStageAsync(string? rawStage)
    {
        string stage = NormalizeRoundtripStage(rawStage);
        int passedAtStart = _passed, failedAtStart = _failed;
        WriteRoundtripStageStart(stage);
        try
        {
            if (stage == "unknown")
                throw new InvalidOperationException(
                    $"pv-roundtrip 必须是 prepare|standard|ai，实际={(string.IsNullOrWhiteSpace(rawStage) ? "缺失" : rawStage)}");
            Check("阶段与发行版匹配", EditionMatchesStage(stage),
                $"stage={stage} edition={AndroidAppCapabilities.EditionName} hasDino={AndroidAppCapabilities.HasDino}");
            if (!EditionMatchesStage(stage))
                throw new InvalidOperationException($"{stage} 阶段不能在 {AndroidAppCapabilities.EditionName} 版构建上运行");
            await WaitForScreenAsync();
            switch (stage)
            {
#if PHOTOVIEWER_DINO
                case "prepare":
                    await PrepareRoundtripAsync(_activity!, _main!, _session!);
                    break;
#else
                case "prepare":
                    throw new InvalidOperationException("prepare 阶段只能在 AI 版构建上运行");
#endif
                case "standard":
                    await VerifyStandardRoundtripAsync(_activity!, _main!, _session!);
                    break;
                default:
                    await VerifyAiRoundtripAsync(_activity!, _main!, _session!);
                    break;
            }
        }
        catch (Exception exception)
        {
            Check($"阶段流程（{stage}）", false, exception.GetType().Name + "：" + exception.Message);
        }
        finally
        {
            WriteRoundtripStageResult(stage, _passed - passedAtStart, _failed - failedAtStart);
            try
            {
                if (_root != null) _root.KeepScreenOn = false;
            }
            catch (Exception exception)
            {
                Console.WriteLine($"[NativeCheck] 复位屏幕常亮失败：{exception.Message}");
            }
        }
    }

    private static string NormalizeRoundtripStage(string? rawStage)
        => rawStage?.Trim().ToLowerInvariant() switch
        {
            "prepare" => "prepare",
            "standard" => "standard",
            "ai" => "ai",
            _ => "unknown",
        };

    private static bool EditionMatchesStage(string stage)
        => stage == "standard" ? !AndroidAppCapabilities.HasDino : AndroidAppCapabilities.HasDino;

    // ── prepare ──────────────────────────────────────────────────────────────

#if PHOTOVIEWER_DINO
    /// <summary>AI 版：生成隔离夹具、真实入库 CLS + patch + CV，写入设置哨兵与 DOM/相似参数，落盘往返状态。</summary>
    private static async Task PrepareRoundtripAsync(Activity activity, MainViewModel main, PhotoLoadSession session)
    {
        bool isolated = false;
        await RunStepAsync("隔离设置与数据库", () =>
        {
            isolated = VerifyRoundtripIsolation(activity);
            return Task.CompletedTask;
        });
        if (!isolated) return;

        string fixtureFolder = RoundtripFixtureFolder(activity);
        string fixturePath = RoundtripFixturePath(activity);
        string statePath = RoundtripStatePath(activity);
        Directory.CreateDirectory(fixtureFolder);
        // 重跑 prepare 先丢弃旧状态文件（旧 PASS 不能冒充本轮），夹具与数据库保留（数据库不清理）。
        if (File.Exists(statePath)) File.Delete(statePath);

        await RunStepAsync("准备夹具", () =>
        {
            if (!File.Exists(fixturePath) || new FileInfo(fixturePath).Length == 0)
                WriteCheckJpeg(fixturePath, 1600, 1067);
            long bytes = File.Exists(fixturePath) ? new FileInfo(fixturePath).Length : 0;
            Check("夹具文件存在", bytes > 0, $"{fixturePath} bytes={bytes}");
            return Task.CompletedTask;
        });

        await RunStepAsync("打开真实照片", async () =>
        {
            await OpenFolderAndWaitAsync(fixtureFolder, 30000);
            Check("夹具照片上屏", main.CurrentFile?.Name == RoundtripFixtureFileName && session.Image != null,
                $"current={main.CurrentFile?.Name ?? "null"} image={DescribeImage(session.Image)}");
        });
        if (main.CurrentFile == null || session.Image == null)
        {
            Check("prepare 前置条件", false, "夹具未上屏，特征与设置检查全部跳过");
            return;
        }

        string? fingerprint = null;
        await RunStepAsync("计算指纹", async () =>
        {
            fingerprint = await AnalysisDataReader.ComputeFingerprintAsync(main.CurrentFile!, CancellationToken.None);
            Check("指纹可计算", !string.IsNullOrWhiteSpace(fingerprint), $"fingerprint={fingerprint ?? "null"}");
        });
        if (string.IsNullOrWhiteSpace(fingerprint)) return;

        await RunStepAsync("特征索引", async () =>
        {
            var indexer = new FolderFeatureIndexer();
            await indexer.RunAsync([main.CurrentFile!]);
            Check("索引完成且无错误", indexer.LastError == null, $"error={indexer.LastError ?? "none"}");
        });

        var state = new RoundtripState
        {
            FixtureFolder = fixtureFolder,
            FixtureFileName = RoundtripFixtureFileName,
            Fingerprint = fingerprint!,
        };
        await RunStepAsync("特征入库完整性", async () =>
        {
            var snapshot = await CaptureRoundtripDbAsync(fingerprint!);
            state.Features = snapshot.Features;
            state.Patches = snapshot.Patches;
            state.Cv = snapshot.Cv;
            Check("CLS 特征已入库", snapshot.Features.Count > 0, DescribeFeatures(snapshot.Features));
            Check("patch 特征已入库", snapshot.Patches.Count > 0, DescribeFeatures(snapshot.Patches));
            Check("CV 网格已入库", snapshot.Cv != null, DescribeCv(snapshot.Cv));
            Check("CLS 含当前模型",
                snapshot.Features.Any(entry => entry.ModelId == DinoModelResources.ModelId),
                $"models=[{string.Join(",", snapshot.Features.Select(entry => entry.ModelId))}] current={DinoModelResources.ModelId}");
            Check("patch 含当前模型",
                snapshot.Patches.Any(entry => entry.ModelId == DinoModelResources.ModelId),
                $"models=[{string.Join(",", snapshot.Patches.Select(entry => entry.ModelId))}] current={DinoModelResources.ModelId}");
            Check("CV 版本为当前 schema", snapshot.Cv?.Spec == CvGridResult.CurrentVersion,
                $"spec={snapshot.Cv?.Spec ?? "null"} current={CvGridResult.CurrentVersion} {DescribeCv(snapshot.Cv)}");
        });

        await RunStepAsync("写入设置与展开态", async () =>
        {
            var settings = main.Settings;
            // 记录 prepare 之前的用户展开态：AI 阶段末尾据此还原，隔离检查不改动用户真实界面偏好。
            state.PreviousViewState = new AndroidViewStateStore().GetBool(SettingsViewStateKeys.SimilarityPanelExpanded);
            settings.SimilarityThreshold = 0.91;
            settings.SimilarityMaxResults = 13;
            settings.AnalysisImmediateCompute = true;
            settings.AnalysisImmediateCvCompute = false;
            settings.SimilarityPanelExpanded = true;
            bool viewStateCommitted = CommitRoundtripViewState(SettingsViewStateKeys.SimilarityPanelExpanded, true);
            bool committed = await main.SettingsRepository.CommitAsync(settings.CreateSnapshot());
            await main.SettingsRepository.FlushAsync();
            Check("设置提交成功", committed,
                $"committed={committed} outcome={main.SettingsRepository.LoadOutcome}"
                    + $" canCommit={main.SettingsRepository.CanCommit} error={main.SettingsRepository.LastSaveError ?? "none"}");
            await WaitAsync(() => Math.Abs(settings.SimilarityThreshold - 0.91) < 1e-6
                && settings.SimilarityMaxResults == 13
                && settings.AnalysisImmediateCompute && !settings.AnalysisImmediateCvCompute,
                "设置值生效");
            Check("设置值生效", true, DescribeSettings(settings));
            bool? storedViewState = new AndroidViewStateStore().GetBool(SettingsViewStateKeys.SimilarityPanelExpanded);
            Check("展开态写入存储", viewStateCommitted && storedViewState == true,
                $"committed={viewStateCommitted} stored={DescribeBool(storedViewState)}");
            string? difference = await AwaitPersistedSentinelsAsync(0.91, 13, true, false, settings.ShowZoomIndicator);
            Check("隔离设置档已落盘", difference == null,
                difference ?? DescribePersistedSettings(ReadIsolatedSettingsJson()));
        });

        await RunStepAsync("写入往返状态", async () =>
        {
            var settings = main.Settings;
            state.Zoom = settings.ShowZoomIndicator;
            state.StandardZoom = null;
            state.SimilarityThreshold = settings.SimilarityThreshold;
            state.SimilarityMaxResults = settings.SimilarityMaxResults;
            state.ImmediateDino = settings.AnalysisImmediateCompute;
            state.ImmediateCv = settings.AnalysisImmediateCvCompute;
            state.SimilarityPanelExpanded = settings.SimilarityPanelExpanded;
            state.PreparedAt = DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss zzz");
            await SaveRoundtripStateAsync(activity, state);
            var reread = await LoadRoundtripStateAsync(activity);
            Check("状态文件可回读",
                reread.Fingerprint == state.Fingerprint && reread.FixtureFileName == state.FixtureFileName
                    && reread.Features.Count == state.Features.Count && reread.Patches.Count == state.Patches.Count
                    && reread.Cv?.Sha256 == state.Cv?.Sha256 && reread.StandardZoom == null,
                $"{statePath} fixture={state.FixtureFileName} fingerprint={state.Fingerprint}"
                    + $" features={state.Features.Count} patches={state.Patches.Count} {DescribeCv(state.Cv)}");
        });
    }
#endif

    // ── standard ─────────────────────────────────────────────────────────────

    /// <summary>标准版：同一夹具与库不被改写，AI 版设置的存储值保持，通用设置可写，且不残留任何 DINO 能力/入口。</summary>
    private static async Task VerifyStandardRoundtripAsync(Activity activity, MainViewModel main, PhotoLoadSession session)
    {
        bool isolated = false;
        await RunStepAsync("隔离设置与数据库", () =>
        {
            isolated = VerifyRoundtripIsolation(activity);
            return Task.CompletedTask;
        });
        if (!isolated) return;

        RoundtripState? state = null;
        await RunStepAsync("加载往返状态", async () =>
        {
            state = await LoadRoundtripStateAsync(activity);
            Check("状态可解析",
                state.Fingerprint.Length > 0 && state.Features.Count > 0 && state.Patches.Count > 0 && state.Cv != null,
                $"state={RoundtripStatePath(activity)} fingerprint={state.Fingerprint}"
                    + $" features={state.Features.Count} patches={state.Patches.Count} {DescribeCv(state.Cv)}");
        });
        if (state == null) return;

        await RunStepAsync("打开同一真实照片", async () =>
        {
            string fixturePath = IOPath.Combine(state.FixtureFolder, state.FixtureFileName);
            if (!File.Exists(fixturePath))
                throw new InvalidOperationException($"跨进程夹具不存在（请先跑 prepare）：{fixturePath}");
            await OpenFolderAndWaitAsync(state.FixtureFolder, 30000);
            Check("夹具与 state 一致",
                main.CurrentFile?.Name == state.FixtureFileName && session.Image != null,
                $"current={main.CurrentFile?.Name ?? "null"} image={DescribeImage(session.Image)}");
        });
        if (main.CurrentFile == null || session.Image == null) return;

        string? fingerprint = null;
        await RunStepAsync("指纹一致", async () =>
        {
            fingerprint = await AnalysisDataReader.ComputeFingerprintAsync(main.CurrentFile!, CancellationToken.None);
            Check("指纹与 prepare 一致", fingerprint == state.Fingerprint,
                $"prepare={state.Fingerprint} standard={fingerprint ?? "null"}");
        });

        await RunStepAsync("AI 设置与展开态保持", () =>
        {
            var settings = main.Settings;
            Check("相似/即时计算设置保持",
                Math.Abs(settings.SimilarityThreshold - state.SimilarityThreshold) < 1e-6
                    && settings.SimilarityMaxResults == state.SimilarityMaxResults
                    && settings.AnalysisImmediateCompute == state.ImmediateDino
                    && settings.AnalysisImmediateCvCompute == state.ImmediateCv,
                $"{DescribeSettings(settings)} expected threshold={state.SimilarityThreshold:0.###}"
                    + $" max={state.SimilarityMaxResults} dino={state.ImmediateDino} cv={state.ImmediateCv}");
            Check("相似面板展开态存储值保持", settings.SimilarityPanelExpanded == state.SimilarityPanelExpanded,
                $"actual={settings.SimilarityPanelExpanded} expected={state.SimilarityPanelExpanded}");
            Check("标准版筛选条不展开相似面板", !main.FileVM.FilterBar.IsSimilarityPanelOpen,
                $"IsSimilarityPanelOpen={main.FileVM.FilterBar.IsSimilarityPanelOpen}");
            Check("通用设置保持 prepare 值", settings.ShowZoomIndicator == state.Zoom,
                $"zoom={settings.ShowZoomIndicator} expected={state.Zoom}");
            string? difference = ComparePersistedSentinels(ReadIsolatedSettingsJson(), state.SimilarityThreshold,
                state.SimilarityMaxResults, state.ImmediateDino, state.ImmediateCv, state.Zoom);
            Check("隔离设置档落盘值保持", difference == null,
                difference ?? DescribePersistedSettings(ReadIsolatedSettingsJson()));
            return Task.CompletedTask;
        });

        var beforeSave = main.Settings.ShowZoomIndicator;
        await RunStepAsync("AI 设置未被改写（保存前）", async () =>
        {
            var snapshot = await CaptureRoundtripDbAsync(state.Fingerprint);
            string? features = DiffFeatures("photo_features", state.Features, snapshot.Features);
            string? patches = DiffFeatures("photo_patches", state.Patches, snapshot.Patches);
            string? cv = DiffCv(state.Cv, snapshot.Cv);
            Check("CLS 哈希与 model_id 不变", features == null, features ?? DescribeFeatures(snapshot.Features));
            Check("patch 哈希与 model_id 不变", patches == null, patches ?? DescribeFeatures(snapshot.Patches));
            Check("CV 哈希、版本与尺寸不变", cv == null, cv ?? DescribeCv(snapshot.Cv));
        });

        await RunStepAsync("无 DINO 能力与入口", () =>
        {
            var android = typeof(AndroidNativeRootView).Assembly;
            var core = typeof(FingerprintGrouping).Assembly;
            var present = DinoOnlyTypeNames.Where(name => core.GetType(name) != null).ToList();
            Check("标准版无 DINO/相似聚类类型", present.Count == 0, $"present=[{string.Join(",", present)}]");
            Check("标准版无相似面板视图",
                android.GetType("PhotoViewer.Android.Native.AndroidSimilarityPanel") == null,
                "AndroidSimilarityPanel 存在");
            Check("标准版根视图无相似面板字段",
                typeof(AndroidNativeRootView).GetField("_similarityPane",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic) == null,
                "_similarityPane 字段存在");
            Check("标准版文件栏无相似面板 VM",
                main.FileVM.GetType().GetProperty("SimilarityPanel") == null, "FileViewModel.SimilarityPanel 存在");
            Check("标准版工具 VM 无 DINO 任务",
                main.Tools.GetType().GetProperty("DinoPresenter") == null
                    && main.Tools.GetType().GetProperty("DinoDebug") == null,
                $"dinoPresenter={main.Tools.GetType().GetProperty("DinoPresenter") != null}"
                    + $" dinoDebug={main.Tools.GetType().GetProperty("DinoDebug") != null}");
            Check("标准版诊断入口为 CV",
                AndroidAppCapabilities.DiagnosticName == "CV 诊断" && !AndroidAppCapabilities.HasDino
                    && main.Tools.DiagnosticDebug.GetType().Name == "CvDebugViewModel",
                $"name={AndroidAppCapabilities.DiagnosticName} hasDino={AndroidAppCapabilities.HasDino}"
                    + $" debug={main.Tools.DiagnosticDebug.GetType().Name}");
            Check("标准版筛选条无相似 toggle", !AndroidFilterBar.ShowsSimilarityToggle,
                $"ShowsSimilarityToggle={AndroidFilterBar.ShowsSimilarityToggle}");
            return Task.CompletedTask;
        });

        await RunStepAsync("通用设置可写", async () =>
        {
            bool expected = !state.Zoom;
            main.Settings.ShowZoomIndicator = expected;
            bool committed = await main.SettingsRepository.CommitAsync(main.Settings.CreateSnapshot());
            await main.SettingsRepository.FlushAsync();
            Check("通用设置提交成功", committed,
                $"committed={committed} outcome={main.SettingsRepository.LoadOutcome}"
                    + $" canCommit={main.SettingsRepository.CanCommit} error={main.SettingsRepository.LastSaveError ?? "none"}");
            Check("通用设置内存值生效", main.Settings.ShowZoomIndicator == expected,
                $"actual={main.Settings.ShowZoomIndicator} expected={expected}");
            string? difference = await AwaitPersistedSentinelsAsync(state.SimilarityThreshold, state.SimilarityMaxResults,
                state.ImmediateDino, state.ImmediateCv, expected);
            Check("通用设置已落盘且 AI 设置未改写", difference == null,
                difference ?? DescribePersistedSettings(ReadIsolatedSettingsJson()));
            state.StandardZoom = main.Settings.ShowZoomIndicator;
            await SaveRoundtripStateAsync(activity, state);
            Check("标准阶段结果写回 state", (await LoadRoundtripStateAsync(activity)).StandardZoom == state.StandardZoom,
                $"StandardZoom={DescribeBool(state.StandardZoom)} (prepare zoom={beforeSave})");
        });

        await RunStepAsync("AI 设置未被改写（保存后）", async () =>
        {
            var snapshot = await CaptureRoundtripDbAsync(state.Fingerprint);
            string? features = DiffFeatures("photo_features", state.Features, snapshot.Features);
            string? patches = DiffFeatures("photo_patches", state.Patches, snapshot.Patches);
            string? cv = DiffCv(state.Cv, snapshot.Cv);
            Check("CLS 哈希与 model_id 不变", features == null, features ?? DescribeFeatures(snapshot.Features));
            Check("patch 哈希与 model_id 不变", patches == null, patches ?? DescribeFeatures(snapshot.Patches));
            Check("CV 哈希、版本与尺寸不变", cv == null, cv ?? DescribeCv(snapshot.Cv));
        });
    }

    // ── ai ───────────────────────────────────────────────────────────────────

    /// <summary>AI 版：设置、展开态与通用设置按哨兵/标准阶段结果恢复，特征库全部标识与哈希不变。</summary>
    private static async Task VerifyAiRoundtripAsync(Activity activity, MainViewModel main, PhotoLoadSession session)
    {
        bool isolated = false;
        await RunStepAsync("隔离设置与数据库", () =>
        {
            isolated = VerifyRoundtripIsolation(activity);
            return Task.CompletedTask;
        });
        if (!isolated) return;

        RoundtripState? state = null;
        await RunStepAsync("加载往返状态", async () =>
        {
            state = await LoadRoundtripStateAsync(activity);
            Check("状态可解析",
                state.Fingerprint.Length > 0 && state.Features.Count > 0 && state.Patches.Count > 0 && state.Cv != null,
                $"state={RoundtripStatePath(activity)} fingerprint={state.Fingerprint}"
                    + $" features={state.Features.Count} patches={state.Patches.Count} {DescribeCv(state.Cv)}");
        });
        if (state == null) return;

        await RunStepAsync("打开同一真实照片", async () =>
        {
            string fixturePath = IOPath.Combine(state.FixtureFolder, state.FixtureFileName);
            if (!File.Exists(fixturePath))
                throw new InvalidOperationException($"跨进程夹具不存在（请先跑 prepare）：{fixturePath}");
            await OpenFolderAndWaitAsync(state.FixtureFolder, 30000);
            Check("夹具与 state 一致",
                main.CurrentFile?.Name == state.FixtureFileName && session.Image != null,
                $"current={main.CurrentFile?.Name ?? "null"} image={DescribeImage(session.Image)}");
        });
        if (main.CurrentFile == null || session.Image == null) return;

        string? fingerprint = null;
        await RunStepAsync("指纹一致", async () =>
        {
            fingerprint = await AnalysisDataReader.ComputeFingerprintAsync(main.CurrentFile!, CancellationToken.None);
            Check("指纹与 prepare 一致", fingerprint == state.Fingerprint,
                $"prepare={state.Fingerprint} ai={fingerprint ?? "null"}");
        });

        await RunStepAsync("设置与展开态按哨兵恢复", () =>
        {
            var settings = main.Settings;
            Check("相似/即时计算设置恢复",
                Math.Abs(settings.SimilarityThreshold - state.SimilarityThreshold) < 1e-6
                    && settings.SimilarityMaxResults == state.SimilarityMaxResults
                    && settings.AnalysisImmediateCompute == state.ImmediateDino
                    && settings.AnalysisImmediateCvCompute == state.ImmediateCv,
                $"{DescribeSettings(settings)} expected threshold={state.SimilarityThreshold:0.###}"
                    + $" max={state.SimilarityMaxResults} dino={state.ImmediateDino} cv={state.ImmediateCv}");
            Check("相似面板展开态恢复", main.FileVM.FilterBar.IsSimilarityPanelOpen == state.SimilarityPanelExpanded,
                $"IsSimilarityPanelOpen={main.FileVM.FilterBar.IsSimilarityPanelOpen} expected={state.SimilarityPanelExpanded}");
            bool? storedViewState = new AndroidViewStateStore().GetBool(SettingsViewStateKeys.SimilarityPanelExpanded);
            Check("展开态存储值与哨兵一致", storedViewState == state.SimilarityPanelExpanded,
                $"stored={DescribeBool(storedViewState)} expected={state.SimilarityPanelExpanded}");
            bool expectedZoom = state.StandardZoom ?? !state.Zoom;
            Check("通用设置保持标准阶段修改", settings.ShowZoomIndicator == expectedZoom,
                $"zoom={settings.ShowZoomIndicator} expected={expectedZoom}"
                    + $" (prepare={state.Zoom} standard={DescribeBool(state.StandardZoom)})");
            string? difference = ComparePersistedSentinels(ReadIsolatedSettingsJson(), state.SimilarityThreshold,
                state.SimilarityMaxResults, state.ImmediateDino, state.ImmediateCv, expectedZoom);
            Check("隔离设置档按哨兵恢复", difference == null,
                difference ?? DescribePersistedSettings(ReadIsolatedSettingsJson()));
            return Task.CompletedTask;
        });

        await RunStepAsync("特征库不变", async () =>
        {
            var snapshot = await CaptureRoundtripDbAsync(state.Fingerprint);
            string? features = DiffFeatures("photo_features", state.Features, snapshot.Features);
            string? patches = DiffFeatures("photo_patches", state.Patches, snapshot.Patches);
            string? cv = DiffCv(state.Cv, snapshot.Cv);
            Check("CLS 哈希与 model_id 不变", features == null, features ?? DescribeFeatures(snapshot.Features));
            Check("patch 哈希与 model_id 不变", patches == null, patches ?? DescribeFeatures(snapshot.Patches));
            Check("CV 哈希、版本与尺寸不变", cv == null, cv ?? DescribeCv(snapshot.Cv));
#if PHOTOVIEWER_DINO
            Check("CLS 含当前模型",
                snapshot.Features.Any(entry => entry.ModelId == DinoModelResources.ModelId),
                $"models=[{string.Join(",", snapshot.Features.Select(entry => entry.ModelId))}]"
                    + $" current={DinoModelResources.ModelId}");
#endif
        });

        await RunStepAsync("恢复用户展开态", () =>
        {
            bool restored = RestoreRoundtripViewState(state.PreviousViewState);
            bool? stored = new AndroidViewStateStore().GetBool(SettingsViewStateKeys.SimilarityPanelExpanded);
            Check("还原 prepare 前的用户展开态", stored == (state.PreviousViewState ?? false),
                $"previous={DescribeBool(state.PreviousViewState)} stored={DescribeBool(stored)} written={restored}");
            return Task.CompletedTask;
        });
    }

    // ── 隔离、设置存储 ────────────────────────────────────────────────────────

    /// <summary>核对隔离设置档名与数据库路径；两者都由 Shell 在构造主 VM 前配置。返回 false 表示未隔离。</summary>
    private static bool VerifyRoundtripIsolation(Activity activity)
    {
        string? preferenceName = typeof(AndroidSettingsStorage)
            .GetField("_preferenceName", StaticNonPublic)?.GetValue(null) as string;
        string expectedPreference = "cc.xiaojh.PhotoViewer.Settings." + RoundtripProfileName;
        bool preferenceOk = string.Equals(preferenceName, expectedPreference, StringComparison.Ordinal);
        Check("隔离设置档生效", preferenceOk, $"preference={preferenceName ?? "null"} expected={expectedPreference}");
        // 只读静态路径字段，避免为了断言而触发「按默认路径初始化」。
        string? configured = typeof(PhotoDatabase).GetField("_dbPath", StaticNonPublic)?.GetValue(null) as string;
        string expectedDatabase = Path.GetFullPath(RoundtripDatabasePath(activity));
        bool databaseOk = configured != null
            && string.Equals(Path.GetFullPath(configured), expectedDatabase, StringComparison.Ordinal);
        Check("隔离数据库路径", databaseOk, $"db={configured ?? "未初始化"} expected={expectedDatabase}");
        return preferenceOk && databaseOk;
    }

    private static string? ReadIsolatedSettingsJson()
    {
        try
        {
            using var preferences = Application.Context.GetSharedPreferences(
                IsolatedSettingsPreferenceName, FileCreationMode.Private);
            return preferences?.GetString(SettingsDocumentKey, null);
        }
        catch (Exception exception)
        {
            Console.WriteLine($"[NativeCheck] 读取隔离设置失败：{exception.Message}");
            return null;
        }
    }

    private static string IsolatedSettingsPreferenceName
        => "cc.xiaojh.PhotoViewer.Settings." + RoundtripProfileName;

    /// <summary>轮询隔离设置档直到哨兵值全部落盘；返回 null 表示一致，否则返回最后一次差异。</summary>
    private static async Task<string?> AwaitPersistedSentinelsAsync(double threshold, int maxResults,
        bool dino, bool cv, bool? zoom, int timeoutMs = 5000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        string? difference = "未读取隔离设置档";
        while (DateTime.UtcNow < deadline)
        {
            difference = ComparePersistedSentinels(ReadIsolatedSettingsJson(), threshold, maxResults, dino, cv, zoom);
            if (difference == null) return null;
            await Task.Delay(100);
        }
        return difference;
    }

    /// <summary>逐字段核对设置文档（camelCase，与共享 <c>SettingsDocumentCodec</c> 一致）。</summary>
    private static string? ComparePersistedSentinels(string? json, double threshold, int maxResults,
        bool dino, bool cv, bool? zoom)
    {
        if (string.IsNullOrWhiteSpace(json)) return "隔离设置档 settings_json 缺失";
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (!root.TryGetProperty("similarityThreshold", out var thresholdElement))
                return "settings_json 缺少 similarityThreshold";
            double actualThreshold = thresholdElement.GetDouble();
            if (Math.Abs(actualThreshold - threshold) > 1e-6)
                return $"similarityThreshold={actualThreshold} 期望 {threshold}";
            if (!root.TryGetProperty("similarityMaxResults", out var maxElement))
                return "settings_json 缺少 similarityMaxResults";
            int actualMax = maxElement.GetInt32();
            if (actualMax != maxResults) return $"similarityMaxResults={actualMax} 期望 {maxResults}";
            if (!root.TryGetProperty("analysisImmediateCompute", out var dinoElement))
                return "settings_json 缺少 analysisImmediateCompute";
            bool actualDino = dinoElement.GetBoolean();
            if (actualDino != dino) return $"analysisImmediateCompute={actualDino} 期望 {dino}";
            if (!root.TryGetProperty("analysisImmediateCvCompute", out var cvElement))
                return "settings_json 缺少 analysisImmediateCvCompute";
            bool actualCv = cvElement.GetBoolean();
            if (actualCv != cv) return $"analysisImmediateCvCompute={actualCv} 期望 {cv}";
            if (zoom.HasValue)
            {
                if (!root.TryGetProperty("showZoomIndicator", out var zoomElement))
                    return "settings_json 缺少 showZoomIndicator";
                bool actualZoom = zoomElement.GetBoolean();
                if (actualZoom != zoom.Value) return $"showZoomIndicator={actualZoom} 期望 {zoom.Value}";
            }
            return null;
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or FormatException)
        {
            return "settings_json 解析失败：" + exception.Message;
        }
    }

    private static string DescribePersistedSettings(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return "settings_json=null";
        string trimmed = json.Length > 240 ? json[..240] + "…" : json;
        return "settings_json=" + trimmed.Replace("\r", " ").Replace("\n", " ");
    }

    private static string DescribeSettings(PhotoViewer.ViewModels.Settings.SettingsViewModel settings)
        => $"threshold={settings.SimilarityThreshold:0.###} max={settings.SimilarityMaxResults}"
            + $" dino={settings.AnalysisImmediateCompute} cv={settings.AnalysisImmediateCvCompute}"
            + $" panel={settings.SimilarityPanelExpanded} zoom={settings.ShowZoomIndicator}";

    /// <summary>写展开态：共享 store 负责常规写入，再同步提交一次，保证进程被杀前已落盘。</summary>
    private static bool CommitRoundtripViewState(string key, bool value)
    {
        try
        {
            new AndroidViewStateStore().SetBool(key, value);
            string? preferenceName = typeof(AndroidShellPreferences)
                .GetField("PreferenceName", StaticNonPublic)?.GetValue(null) as string;
            if (string.IsNullOrEmpty(preferenceName)) return false;
            using var preferences = Application.Context.GetSharedPreferences(preferenceName, FileCreationMode.Private);
            using var editor = preferences?.Edit();
            if (editor == null) return false;
            editor.PutBoolean(key, value);
            return editor.Commit();
        }
        catch (Exception exception)
        {
            Console.WriteLine($"[NativeCheck] 展开态提交失败：{exception.Message}");
            return false;
        }
    }

    /// <summary>把 AI 阶段之前的用户展开态写回（含「原来没有这个键」的删除路径）。</summary>
    private static bool RestoreRoundtripViewState(bool? previous)
    {
        try
        {
            string? preferenceName = typeof(AndroidShellPreferences)
                .GetField("PreferenceName", StaticNonPublic)?.GetValue(null) as string;
            if (string.IsNullOrEmpty(preferenceName)) return false;
            using var preferences = Application.Context.GetSharedPreferences(preferenceName, FileCreationMode.Private);
            using var editor = preferences?.Edit();
            if (editor == null) return false;
            if (previous.HasValue)
            {
                editor.PutBoolean(SettingsViewStateKeys.SimilarityPanelExpanded, previous.Value);
                new AndroidViewStateStore().SetBool(SettingsViewStateKeys.SimilarityPanelExpanded, previous.Value);
            }
            else
            {
                editor.Remove(SettingsViewStateKeys.SimilarityPanelExpanded);
            }
            return editor.Commit();
        }
        catch (Exception exception)
        {
            Console.WriteLine($"[NativeCheck] 用户展开态还原失败：{exception.Message}");
            return false;
        }
    }

    // ── 数据库快照 ────────────────────────────────────────────────────────────

    private static async Task<RoundtripDbSnapshot> CaptureRoundtripDbAsync(string fingerprint)
    {
        var snapshot = new RoundtripDbSnapshot();
        await using var connection = PhotoDatabase.OpenConnection();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText =
                "SELECT model_id, cls_vector FROM photo_features WHERE fingerprint = $fp ORDER BY model_id;";
            command.Parameters.AddWithValue("$fp", fingerprint);
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                var blob = (byte[])reader.GetValue(1);
                snapshot.Features.Add(new RoundtripFeatureSnapshot
                {
                    ModelId = reader.GetString(0),
                    Bytes = blob.Length,
                    Sha256 = Sha256Hex(blob),
                });
            }
        }
        await using (var command = connection.CreateCommand())
        {
            command.CommandText =
                "SELECT model_id, patch_tokens FROM photo_patches WHERE fingerprint = $fp ORDER BY model_id;";
            command.Parameters.AddWithValue("$fp", fingerprint);
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                var blob = (byte[])reader.GetValue(1);
                snapshot.Patches.Add(new RoundtripFeatureSnapshot
                {
                    ModelId = reader.GetString(0),
                    Bytes = blob.Length,
                    Sha256 = Sha256Hex(blob),
                });
            }
        }
        await using (var command = connection.CreateCommand())
        {
            command.CommandText =
                "SELECT cv_grid, cv_grid_spec, cv_image_width, cv_image_height FROM photos WHERE fingerprint = $fp;";
            command.Parameters.AddWithValue("$fp", fingerprint);
            await using var reader = await command.ExecuteReaderAsync();
            if (await reader.ReadAsync() && !reader.IsDBNull(0))
            {
                var blob = (byte[])reader.GetValue(0);
                snapshot.Cv = new RoundtripCvSnapshot
                {
                    Spec = reader.IsDBNull(1) ? null : reader.GetString(1),
                    Width = reader.IsDBNull(2) ? 0 : reader.GetInt32(2),
                    Height = reader.IsDBNull(3) ? 0 : reader.GetInt32(3),
                    Bytes = blob.Length,
                    Sha256 = Sha256Hex(blob),
                };
            }
        }
        return snapshot;
    }

    private static string? DiffFeatures(string table, List<RoundtripFeatureSnapshot> expected,
        List<RoundtripFeatureSnapshot> actual)
    {
        if (expected.Count != actual.Count) return $"{table} 行数 {expected.Count}→{actual.Count}";
        for (int index = 0; index < expected.Count; index++)
        {
            if (expected[index].ModelId != actual[index].ModelId)
                return $"{table}[{index}] model_id {expected[index].ModelId}→{actual[index].ModelId}";
            if (expected[index].Bytes != actual[index].Bytes)
                return $"{table} model_id={expected[index].ModelId} 字节 {expected[index].Bytes}→{actual[index].Bytes}";
            if (expected[index].Sha256 != actual[index].Sha256)
                return $"{table} model_id={expected[index].ModelId} SHA256 {Short(expected[index].Sha256)}→{Short(actual[index].Sha256)}";
        }
        return null;
    }

    private static string? DiffCv(RoundtripCvSnapshot? expected, RoundtripCvSnapshot? actual)
    {
        if (expected == null && actual == null) return null;
        if (expected == null) return "多出 CV 行";
        if (actual == null) return "CV 行丢失";
        if (expected.Spec != actual.Spec) return $"spec {expected.Spec ?? "null"}→{actual.Spec ?? "null"}";
        if (expected.Width != actual.Width || expected.Height != actual.Height)
            return $"尺寸 {expected.Width}x{expected.Height}→{actual.Width}x{actual.Height}";
        if (expected.Bytes != actual.Bytes) return $"字节 {expected.Bytes}→{actual.Bytes}";
        if (expected.Sha256 != actual.Sha256) return $"SHA256 {Short(expected.Sha256)}→{Short(actual.Sha256)}";
        return null;
    }

    private static string DescribeFeatures(List<RoundtripFeatureSnapshot> entries)
        => entries.Count == 0
            ? "无行"
            : string.Join(",", entries.Select(entry =>
                $"{entry.ModelId}:{entry.Bytes}B:{Short(entry.Sha256)}"));

    private static string DescribeCv(RoundtripCvSnapshot? cv)
        => cv == null
            ? "cv=absent"
            : $"cv spec={cv.Spec ?? "null"} size={cv.Width}x{cv.Height} bytes={cv.Bytes} sha={Short(cv.Sha256)}";

    private static string DescribeImage(DecodedImage? image)
        => image == null ? "null" : $"{image.Width}x{image.Height}";

    private static string DescribeBool(bool? value) => value.HasValue ? value.Value.ToString() : "null";

    private static string Short(string hash) => hash.Length > 12 ? hash[..12] : hash;

    private static string Sha256Hex(byte[] bytes)
        => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    // ── 路径与状态文件 ────────────────────────────────────────────────────────

    private static string RoundtripRoot(Activity activity)
        => IOPath.Combine(activity.FilesDir!.AbsolutePath, "PhotoViewer", RoundtripFolderName);

    private static string RoundtripDatabasePath(Activity activity)
        => IOPath.Combine(RoundtripRoot(activity), "photos.db");

    private static string RoundtripStatePath(Activity activity)
        => IOPath.Combine(RoundtripRoot(activity), RoundtripStateFileName);

    private static string RoundtripFixtureFolder(Activity activity)
        => IOPath.Combine(RoundtripRoot(activity), RoundtripFixtureFolderName);

    private static string RoundtripFixturePath(Activity activity)
        => IOPath.Combine(RoundtripFixtureFolder(activity), RoundtripFixtureFileName);

    private static async Task SaveRoundtripStateAsync(Activity activity, RoundtripState state)
    {
        string path = RoundtripStatePath(activity);
        Directory.CreateDirectory(IOPath.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path,
            JsonSerializer.Serialize(state, RoundtripStateJsonContext.Default.RoundtripState));
    }

    private static async Task<RoundtripState> LoadRoundtripStateAsync(Activity activity)
    {
        string path = RoundtripStatePath(activity);
        if (!File.Exists(path))
            throw new InvalidOperationException($"跨进程往返状态缺失（请先跑 prepare）：{path}");
        var state = JsonSerializer.Deserialize(await File.ReadAllTextAsync(path),
            RoundtripStateJsonContext.Default.RoundtripState);
        return state ?? throw new InvalidOperationException($"跨进程往返状态无法解析：{path}");
    }

    // ── 阶段报告 ─────────────────────────────────────────────────────────────

    private static string RoundtripReportPath(Activity activity)
        => IOPath.Combine(activity.FilesDir!.AbsolutePath, "PhotoViewer",
            $"android-checks-roundtrip-{AndroidAppCapabilities.EditionName}.txt");

    /// <summary>阶段开始时先落 STAGE START；prepare 重开报告，standard/ai 追加独立区块（旧阶段 PASS 不会被当成本次结果）。</summary>
    private static void WriteRoundtripStageStart(string stage)
    {
        try
        {
            string path = RoundtripReportPath(_activity!);
            Directory.CreateDirectory(IOPath.GetDirectoryName(path)!);
            string header = $"Android native checks mode=roundtrip edition={AndroidAppCapabilities.EditionName}"
                + $" stage={stage} started={DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}";
            string marker = $"===== STAGE {stage} START =====";
            bool append = stage is "standard" or "ai";
            if (append && File.Exists(path))
                File.AppendAllText(path, System.Environment.NewLine + header + System.Environment.NewLine + marker + System.Environment.NewLine);
            else
                File.WriteAllText(path, header + System.Environment.NewLine + marker + System.Environment.NewLine);
            global::Android.Util.Log.Info("PhotoViewerChecks",
                $"roundtrip {stage} START edition={AndroidAppCapabilities.EditionName}");
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"[NativeCheck] 报告写入失败：{exception}");
        }
    }

    /// <summary>阶段结束追加逐条结果与 STAGE PASS/FAIL 汇总行。</summary>
    private static void WriteRoundtripStageResult(string stage, int passed, int failed)
    {
        try
        {
            string path = RoundtripReportPath(_activity!);
            var lines = new List<string>(Lines)
            {
                $"STAGE {stage} {(failed == 0 ? "PASS" : "FAIL")} — PASS {passed} / FAIL {failed}",
                $"===== STAGE {stage} END {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz} =====",
            };
            File.AppendAllLines(path, lines);
            foreach (string line in lines)
                global::Android.Util.Log.Info("PhotoViewerChecks", "roundtrip " + line);
            Console.WriteLine(string.Join(System.Environment.NewLine, lines));
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"[NativeCheck] 报告写入失败：{exception}");
        }
    }
}

/// <summary>往返状态（App 内部 JSON）：夹具、指纹、三路特征哈希与设置哨兵。</summary>
internal sealed class RoundtripState
{
    public int SchemaVersion { get; set; } = 1;
    public string FixtureFolder { get; set; } = "";
    public string FixtureFileName { get; set; } = "";
    public string Fingerprint { get; set; } = "";
    public bool Zoom { get; set; }
    public bool? StandardZoom { get; set; }
    public bool? PreviousViewState { get; set; }
    public double SimilarityThreshold { get; set; }
    public int SimilarityMaxResults { get; set; }
    public bool ImmediateDino { get; set; }
    public bool ImmediateCv { get; set; }
    public bool SimilarityPanelExpanded { get; set; }
    public List<RoundtripFeatureSnapshot> Features { get; set; } = [];
    public List<RoundtripFeatureSnapshot> Patches { get; set; } = [];
    public RoundtripCvSnapshot? Cv { get; set; }
    public string PreparedAt { get; set; } = "";
}

/// <summary>一次特征库快照：CLS 行、patch 行与 CV 网格。</summary>
internal sealed class RoundtripDbSnapshot
{
    public List<RoundtripFeatureSnapshot> Features { get; set; } = [];
    public List<RoundtripFeatureSnapshot> Patches { get; set; } = [];
    public RoundtripCvSnapshot? Cv { get; set; }
}

/// <summary>一行的 model_id + 字节数 + SHA-256。</summary>
internal sealed class RoundtripFeatureSnapshot
{
    public string ModelId { get; set; } = "";
    public int Bytes { get; set; }
    public string Sha256 { get; set; } = "";
}

/// <summary>CV 网格标识：版本、实际解码尺寸、字节数与 SHA-256。</summary>
internal sealed class RoundtripCvSnapshot
{
    public string? Spec { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public int Bytes { get; set; }
    public string Sha256 { get; set; } = "";
}

/// <summary>往返状态的源生成序列化上下文（避免依赖运行时反射）。</summary>
[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(RoundtripState))]
internal partial class RoundtripStateJsonContext : JsonSerializerContext
{
}
