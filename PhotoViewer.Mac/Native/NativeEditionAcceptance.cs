using Foundation;
using PhotoViewer.Core.AI;
using PhotoViewer.Core.Database;
using PhotoViewer.Core.Settings;
using PhotoViewer.ViewModels.Settings;

namespace PhotoViewer.Mac.Native;

internal static class NativeEditionAcceptance
{
    internal const string ViewStatePrefix = "cc.xiaojh.PhotoViewer.Acceptance.ViewState.";
    private const string ReportPath = "artifacts/acceptance/mac-editions/edition-roundtrip.txt";
    private const string StatePath = "artifacts/acceptance/mac-editions/edition-roundtrip-state.txt";

    internal static async Task RunAsync(MacMainWindow window, string[] args)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(ReportPath)!);
#if PHOTOVIEWER_DINO
        if (args.Contains("--pv-native-edition-prepare")) await PrepareAsync(window);
        else if (args.Contains("--pv-native-edition-ai")) await VerifyAiAsync(window);
        else throw new InvalidOperationException("AI 往返阶段参数无效");
#else
        if (args.Contains("--pv-native-edition-standard")) await VerifyStandardAsync(window);
        else throw new InvalidOperationException("标准版往返阶段参数无效");
#endif
    }

#if PHOTOVIEWER_DINO
    private static async Task PrepareAsync(MacMainWindow window)
    {
        ClearViewState();
        var main = window.Main;
        var file = main.CurrentFile ?? throw new InvalidOperationException("版本往返准备缺少当前照片");
        main.Settings.SimilarityThreshold = 0.91;
        main.Settings.SimilarityMaxResults = 13;
        main.Settings.AnalysisImmediateCompute = true;
        main.Settings.AnalysisImmediateCvCompute = false;
        main.FileVM.FilterBar.IsSimilarityPanelOpen = true;
        if (!main.FileVM.IsSimilarityPanelOpen) throw new InvalidOperationException("AI 版相似展开态未生效");
        NSUserDefaults.StandardUserDefaults.Synchronize();
        if (!await main.Settings.Repository!.CommitAsync(main.Settings.CreateSnapshot()))
            throw new InvalidOperationException("AI 版设置保存失败");

        var indexer = new FolderFeatureIndexer();
        await indexer.RunAsync([file]);
        if (indexer.LastError != null) throw new InvalidOperationException(indexer.LastError);
        string fingerprint = await AnalysisDataReader.ComputeFingerprintAsync(file, CancellationToken.None)
            ?? throw new InvalidOperationException("版本往返素材无法计算指纹");
        if (await CountFeatureRows(fingerprint) != 2) throw new InvalidOperationException("AI 版特征准备不完整");

        await File.WriteAllTextAsync(StatePath, main.Settings.ShowZoomIndicator ? "zoom=true" : "zoom=false");
        await File.WriteAllTextAsync(ReportPath,
            "prepare-ai: threshold=0.91 maxResults=13 immediateDINO=true immediateCV=false panel=true; settings/features persisted\n");
    }

    private static async Task VerifyAiAsync(MacMainWindow window)
    {
        var main = window.Main;
        string originalZoom = await File.ReadAllTextAsync(StatePath);
        string expectedZoom = originalZoom.Contains("true", StringComparison.Ordinal) ? "zoom=false" : "zoom=true";
        if (main.Settings.ShowZoomIndicator != expectedZoom.Contains("true", StringComparison.Ordinal))
            throw new InvalidOperationException("标准版保存的通用设置未恢复");
        AssertDinoSettings(main.Settings);
        if (!main.FileVM.IsSimilarityPanelOpen || !window.SimilarityToggleIsOn)
            throw new InvalidOperationException("AI 版相似展开态未恢复");
        string fingerprint = await AnalysisDataReader.ComputeFingerprintAsync(main.CurrentFile!, CancellationToken.None)
            ?? throw new InvalidOperationException("往返后无法计算指纹");
        if (await CountFeatureRows(fingerprint) != 2) throw new InvalidOperationException("往返后 DINO/CV 特征被清除");
        await File.AppendAllTextAsync(ReportPath,
            "verify-ai: common edit, DINO/CV switches, similarity parameters and expanded state restored; feature rows intact\n");
        File.Delete(StatePath);
        ClearViewState();
    }
#endif

    private static async Task VerifyStandardAsync(MacMainWindow window)
    {
        var main = window.Main;
        AssertDinoSettings(main.Settings);
        if (main.FileVM.IsSimilarityPanelOpen || main.FileVM.FilterBar.IsSimilarityPanelOpen)
            throw new InvalidOperationException("标准版恢复了相似面板展开态");
        main.Settings.ShowZoomIndicator = !main.Settings.ShowZoomIndicator;
        if (!await main.Settings.Repository!.CommitAsync(main.Settings.CreateSnapshot()))
            throw new InvalidOperationException("标准版设置保存失败");
        AssertDinoSettings(main.Settings);
        string fingerprint = await AnalysisDataReader.ComputeFingerprintAsync(main.CurrentFile!, CancellationToken.None)
            ?? throw new InvalidOperationException("标准版无法计算指纹");
        if (await CountFeatureRows(fingerprint) != 2) throw new InvalidOperationException("标准版启动或保存清除了既有特征");
        await File.AppendAllTextAsync(ReportPath,
            "verify-standard: persisted DINO values and feature rows preserved; expanded state ignored; unrelated setting saved\n");
    }

    private static void AssertDinoSettings(SettingsViewModel settings)
    {
        if (Math.Abs(settings.SimilarityThreshold - 0.91) > 0.0001
            || settings.SimilarityMaxResults != 13
            || !settings.AnalysisImmediateCompute
            || settings.AnalysisImmediateCvCompute)
            throw new InvalidOperationException("DINO/相似/CV 已存设置被改写");
    }

    private static async Task<int> CountFeatureRows(string fingerprint)
    {
        using var connection = PhotoDatabase.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT (SELECT count(*) FROM photo_patches WHERE fingerprint = $fingerprint) + (SELECT count(*) FROM photos WHERE fingerprint = $fingerprint AND cv_grid IS NOT NULL)";
        command.Parameters.AddWithValue("$fingerprint", fingerprint);
        return Convert.ToInt32(await command.ExecuteScalarAsync());
    }

    private static void ClearViewState()
    {
        NSUserDefaults.StandardUserDefaults.RemoveObject(ViewStatePrefix + SettingsViewStateKeys.SimilarityPanelExpanded);
        NSUserDefaults.StandardUserDefaults.Synchronize();
    }
}
