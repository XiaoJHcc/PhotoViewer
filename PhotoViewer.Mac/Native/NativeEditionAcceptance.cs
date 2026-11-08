using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
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
    private const string StatePath = "artifacts/acceptance/mac-editions/edition-roundtrip-state.json";

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
        var state = await CaptureStateAsync(fingerprint, main.Settings.ShowZoomIndicator);
        if (state.Features.Count == 0 || state.Patches.Count == 0 || state.Cv == null)
            throw new InvalidOperationException("AI 版特征准备不完整");
        if (state.Features.All(entry => entry.ModelId != DinoModelResources.ModelId))
            throw new InvalidOperationException($"特征库缺少当前模型 {DinoModelResources.ModelId}");
        if (state.Cv.Spec != CvGridResult.CurrentVersion)
            throw new InvalidOperationException(
                $"CV 版本不是当前 schema：{state.Cv.Spec} ≠ {CvGridResult.CurrentVersion}");
        await SaveStateAsync(state);
        await File.WriteAllTextAsync(ReportPath,
            "prepare-ai: threshold=0.91 maxResults=13 immediateDINO=true immediateCV=false panel=true; " +
            $"settings/features persisted; zoom={state.Zoom}; {Describe(state)}\n");
    }

    private static async Task VerifyAiAsync(MacMainWindow window)
    {
        var main = window.Main;
        var state = await LoadStateAsync();
        if (main.Settings.ShowZoomIndicator == state.Zoom)
            throw new InvalidOperationException("标准版保存的通用设置未生效（缩放指示器未翻转）");
        AssertDinoSettings(main.Settings);
        if (!main.FileVM.IsSimilarityPanelOpen || !window.SimilarityToggleIsOn)
            throw new InvalidOperationException("AI 版相似展开态未恢复");
        string fingerprint = await AnalysisDataReader.ComputeFingerprintAsync(main.CurrentFile!, CancellationToken.None)
            ?? throw new InvalidOperationException("往返后无法计算指纹");
        var actual = await CaptureStateAsync(fingerprint, main.Settings.ShowZoomIndicator);
        if (Diff("photo_features", state.Features, actual.Features) is { } featuresDiff)
            throw new InvalidOperationException("往返后 CLS 特征被改写或丢失：" + featuresDiff);
        if (Diff("photo_patches", state.Patches, actual.Patches) is { } patchesDiff)
            throw new InvalidOperationException("往返后 patch token 被改写或丢失：" + patchesDiff);
        if (Diff("photos.cv_grid", state.Cv, actual.Cv) is { } cvDiff)
            throw new InvalidOperationException("往返后 CV blob/版本/尺寸被改写或丢失：" + cvDiff);
        if (actual.Features.All(entry => entry.ModelId != DinoModelResources.ModelId))
            throw new InvalidOperationException($"往返后特征库缺少当前模型 {DinoModelResources.ModelId}");
        await File.AppendAllTextAsync(ReportPath,
            "verify-ai: common edit, DINO/CV switches, similarity parameters and expanded state restored; " +
            $"CLS/patch/CV SHA256 intact; zoom={actual.Zoom}; {Describe(actual)}\n");
        File.Delete(StatePath);
        ClearViewState();
    }
#endif

    private static async Task VerifyStandardAsync(MacMainWindow window)
    {
        var main = window.Main;
        var state = await LoadStateAsync();
        if (main.Settings.ShowZoomIndicator != state.Zoom)
            throw new InvalidOperationException("AI 版保存的通用设置未恢复");
        AssertDinoSettings(main.Settings);
        if (main.FileVM.IsSimilarityPanelOpen || main.FileVM.FilterBar.IsSimilarityPanelOpen)
            throw new InvalidOperationException("标准版恢复了相似面板展开态");
        string fingerprint = await AnalysisDataReader.ComputeFingerprintAsync(main.CurrentFile!, CancellationToken.None)
            ?? throw new InvalidOperationException("标准版无法计算指纹");

        var before = await CaptureStateAsync(fingerprint, main.Settings.ShowZoomIndicator);
        if (Diff("photo_features", state.Features, before.Features) is { } featuresDiff)
            throw new InvalidOperationException("标准版启动清除了既有 CLS 特征：" + featuresDiff);
        if (Diff("photo_patches", state.Patches, before.Patches) is { } patchesDiff)
            throw new InvalidOperationException("标准版启动清除了既有 patch token：" + patchesDiff);
        if (Diff("photos.cv_grid", state.Cv, before.Cv) is { } cvDiff)
            throw new InvalidOperationException("标准版启动清除了既有 CV 特征：" + cvDiff);
        foreach (var expected in state.Features)
        {
            if (await VerifyModelRowAsync("photo_features", "cls_vector", fingerprint, expected) is { } rowDiff)
                throw new InvalidOperationException("标准版按 model_id 只读核对失败（CLS）：" + rowDiff);
        }
        foreach (var expected in state.Patches)
        {
            if (await VerifyModelRowAsync("photo_patches", "patch_tokens", fingerprint, expected) is { } rowDiff)
                throw new InvalidOperationException("标准版按 model_id 只读核对失败（patch）：" + rowDiff);
        }

        main.Settings.ShowZoomIndicator = !main.Settings.ShowZoomIndicator;
        if (!await main.Settings.Repository!.CommitAsync(main.Settings.CreateSnapshot()))
            throw new InvalidOperationException("标准版设置保存失败");
        AssertDinoSettings(main.Settings);

        var after = await CaptureStateAsync(fingerprint, main.Settings.ShowZoomIndicator);
        if (Diff("photo_features", state.Features, after.Features) is { } afterFeatures)
            throw new InvalidOperationException("标准版保存设置改写了 CLS 特征：" + afterFeatures);
        if (Diff("photo_patches", state.Patches, after.Patches) is { } afterPatches)
            throw new InvalidOperationException("标准版保存设置改写了 patch token：" + afterPatches);
        if (Diff("photos.cv_grid", state.Cv, after.Cv) is { } afterCv)
            throw new InvalidOperationException("标准版保存设置改写了 CV 特征：" + afterCv);
        await File.AppendAllTextAsync(ReportPath,
            "verify-standard: persisted DINO values and feature rows preserved (CLS/patch/CV SHA256 compared read-only); " +
            $"expanded state ignored; unrelated setting saved; zoom={after.Zoom}\n");
    }

    private static void AssertDinoSettings(SettingsViewModel settings)
    {
        if (Math.Abs(settings.SimilarityThreshold - 0.91) > 0.0001
            || settings.SimilarityMaxResults != 13
            || !settings.AnalysisImmediateCompute
            || settings.AnalysisImmediateCvCompute)
            throw new InvalidOperationException("DINO/相似/CV 已存设置被改写");
    }

    // ---------------------------------------------------------------
    // 特征快照：CLS / patch / CV 三类的 model_id、字节数与 SHA256
    // ---------------------------------------------------------------

    private static async Task<EditionState> CaptureStateAsync(string fingerprint, bool zoom)
    {
        var state = new EditionState { Zoom = zoom };
        await using var connection = PhotoDatabase.OpenConnection();

        await using (var command = connection.CreateCommand())
        {
            command.CommandText =
                "SELECT model_id, cls_vector FROM photo_features WHERE fingerprint = $fp ORDER BY model_id;";
            command.Parameters.AddWithValue("$fp", fingerprint);
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                byte[] blob = (byte[])reader.GetValue(1);
                state.Features.Add(new EditionFeatureSnapshot
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
                byte[] blob = (byte[])reader.GetValue(1);
                state.Patches.Add(new EditionFeatureSnapshot
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
                byte[] blob = (byte[])reader.GetValue(0);
                state.Cv = new EditionCvSnapshot
                {
                    Spec = reader.IsDBNull(1) ? null : reader.GetString(1),
                    Width = reader.IsDBNull(2) ? 0 : reader.GetInt32(2),
                    Height = reader.IsDBNull(3) ? 0 : reader.GetInt32(3),
                    Bytes = blob.Length,
                    Sha256 = Sha256Hex(blob),
                };
            }
        }

        return state;
    }

    private static async Task<string?> VerifyModelRowAsync(
        string table, string column, string fingerprint, EditionFeatureSnapshot expected)
    {
        await using var connection = PhotoDatabase.OpenConnection();
        await using var command = connection.CreateCommand();
        command.CommandText =
            $"SELECT {column} FROM {table} WHERE fingerprint = $fp AND model_id = $m LIMIT 1;";
        command.Parameters.AddWithValue("$fp", fingerprint);
        command.Parameters.AddWithValue("$m", expected.ModelId);
        object? value = await command.ExecuteScalarAsync();
        if (value is not byte[] blob) return $"缺少 model_id={expected.ModelId} 的行";
        if (blob.Length != expected.Bytes) return $"model_id={expected.ModelId} 字节 {expected.Bytes}→{blob.Length}";
        if (Sha256Hex(blob) != expected.Sha256) return $"model_id={expected.ModelId} SHA256 变化";
        return null;
    }

    private static string? Diff(string label, IReadOnlyList<EditionFeatureSnapshot> expected, IReadOnlyList<EditionFeatureSnapshot> actual)
    {
        if (expected.Count != actual.Count) return $"{label} 行数 {expected.Count}→{actual.Count}";
        for (int i = 0; i < expected.Count; i++)
        {
            if (expected[i].ModelId != actual[i].ModelId)
                return $"{label} 第 {i} 行 model_id {expected[i].ModelId}→{actual[i].ModelId}";
            if (expected[i].Bytes != actual[i].Bytes)
                return $"{label} model_id={expected[i].ModelId} 字节 {expected[i].Bytes}→{actual[i].Bytes}";
            if (expected[i].Sha256 != actual[i].Sha256)
                return $"{label} model_id={expected[i].ModelId} SHA256 变化";
        }
        return null;
    }

    private static string? Diff(string label, EditionCvSnapshot? expected, EditionCvSnapshot? actual)
    {
        if (expected == null && actual == null) return null;
        if (expected == null) return $"{label} 多出 CV 行";
        if (actual == null) return $"{label} CV 行丢失";
        if (expected.Spec != actual.Spec) return $"{label} 版本 {expected.Spec}→{actual.Spec}";
        if (expected.Width != actual.Width || expected.Height != actual.Height)
            return $"{label} 尺寸 {expected.Width}×{expected.Height}→{actual.Width}×{actual.Height}";
        if (expected.Bytes != actual.Bytes) return $"{label} 字节 {expected.Bytes}→{actual.Bytes}";
        if (expected.Sha256 != actual.Sha256) return $"{label} SHA256 变化";
        return null;
    }

    private static string Describe(EditionState state)
    {
        string features = string.Join(",", state.Features.Select(entry => $"{entry.ModelId}:{entry.Sha256[..12]}"));
        string patches = string.Join(",", state.Patches.Select(entry => $"{entry.ModelId}:{entry.Sha256[..12]}"));
        string cv = state.Cv == null
            ? "absent"
            : $"{state.Cv.Spec} {state.Cv.Width}x{state.Cv.Height} {state.Cv.Sha256[..12]}";
        return $"features=[{features}] patches=[{patches}] cv={cv}";
    }

    private static string Sha256Hex(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static async Task SaveStateAsync(EditionState state)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(StatePath)!);
        await File.WriteAllTextAsync(StatePath, JsonSerializer.Serialize(state, EditionStateJsonContext.Default.EditionState));
    }

    private static async Task<EditionState> LoadStateAsync()
    {
        if (!File.Exists(StatePath)) throw new InvalidOperationException("版本往返状态文件缺失：" + StatePath);
        var state = JsonSerializer.Deserialize(await File.ReadAllTextAsync(StatePath), EditionStateJsonContext.Default.EditionState)
            ?? throw new InvalidOperationException("版本往返状态文件无法解析");
        return state;
    }

    private static void ClearViewState()
    {
        NSUserDefaults.StandardUserDefaults.RemoveObject(ViewStatePrefix + SettingsViewStateKeys.SimilarityPanelExpanded);
        NSUserDefaults.StandardUserDefaults.Synchronize();
    }
}

/// <summary>跨进程版本往返状态：三类特征快照的 SHA256 + 通用设置原始值。</summary>
internal sealed class EditionState
{
    public bool Zoom { get; set; }
    public List<EditionFeatureSnapshot> Features { get; set; } = [];
    public List<EditionFeatureSnapshot> Patches { get; set; } = [];
    public EditionCvSnapshot? Cv { get; set; }
}

/// <summary>单个 (model_id, blob) 快照。</summary>
internal sealed class EditionFeatureSnapshot
{
    public string ModelId { get; set; } = "";
    public int Bytes { get; set; }
    public string Sha256 { get; set; } = "";
}

/// <summary>photos 行上的 CV blob、版本与解码尺寸快照。</summary>
internal sealed class EditionCvSnapshot
{
    public string? Spec { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public int Bytes { get; set; }
    public string Sha256 { get; set; } = "";
}

/// <summary>版本往返状态 JSON 的裁剪安全序列化上下文。</summary>
[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(EditionState))]
internal partial class EditionStateJsonContext : JsonSerializerContext;
