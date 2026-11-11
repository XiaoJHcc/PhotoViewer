using PhotoViewer.Core.Platform;
using PhotoViewer.Core.Settings;

namespace CoreContractCheck;

internal static class SettingsChecks
{
    public static async Task RunAsync(Checks c, string root)
    {
        c.Section("设置仓储与损坏备份");
        var oversized = SettingsDocumentCodec.Normalize(new AppSettings
        {
            BitmapCacheMaxCount = int.MaxValue,
            BitmapCacheMaxMemory = int.MaxValue,
            PreloadForwardCount = int.MaxValue,
            VisibleCenterDelayMs = int.MaxValue,
        });
        c.Equal("缓存张数上界归一", 400, oversized.BitmapCacheMaxCount);
        c.Equal("缓存内存上界归一", 32768, oversized.BitmapCacheMaxMemory);
        c.Equal("预载按归一后缓存张数限制", 133, oversized.PreloadForwardCount);
        c.Equal("滚动预载延时上界归一", SettingsDocumentCodec.MaximumVisibleCenterDelayMs, oversized.VisibleCenterDelayMs);
        var offGridDelay = SettingsDocumentCodec.Normalize(new AppSettings { VisibleCenterDelayMs = 1549 });
        c.Equal("滚动预载延时按最近刻度归一", 1500, offGridDelay.VisibleCenterDelayMs);
        var halfDelay = SettingsDocumentCodec.Normalize(new AppSettings { VisibleCenterDelayMs = 1550 });
        c.Equal("滚动预载延时正中间向上取整", 1600, halfDelay.VisibleCenterDelayMs);
        var undersized = SettingsDocumentCodec.Normalize(new AppSettings
        {
            BitmapCacheMaxCount = int.MinValue,
            BitmapCacheMaxMemory = int.MinValue,
            VisibleCenterDelayMs = int.MinValue,
        });
        c.Equal("缓存张数下界归一", 1, undersized.BitmapCacheMaxCount);
        c.Equal("缓存内存下界归一", 512, undersized.BitmapCacheMaxMemory);
        c.Equal("滚动预载延时下界归一", SettingsDocumentCodec.MinimumVisibleCenterDelayMs, undersized.VisibleCenterDelayMs);

        var defaults = new AppSettings();
        var inherited = SettingsDocumentCodec.Read(
            "{\"schemaVersion\":2,\"analysisImmediateCompute\":true}", defaults).Settings!;
        c.True("旧组合开关继承到 CV", inherited.AnalysisImmediateCompute && inherited.AnalysisImmediateCvCompute);
        var independent = SettingsDocumentCodec.Read(
            "{\"schemaVersion\":2,\"analysisImmediateCompute\":true,\"analysisImmediateCvCompute\":false}", defaults).Settings!;
        c.True("独立 CV 字段优先于继承", independent.AnalysisImmediateCompute && !independent.AnalysisImmediateCvCompute);
        var independentJson = SettingsDocumentCodec.Serialize(independent);
        var independentRoundtrip = SettingsDocumentCodec.Read(independentJson, defaults).Settings!;
        c.True("DINO/CV 开关分别持久化", independentRoundtrip.AnalysisImmediateCompute
            && !independentRoundtrip.AnalysisImmediateCvCompute
            && independentJson.Contains("\"analysisImmediateCompute\": true", StringComparison.Ordinal)
            && independentJson.Contains("\"analysisImmediateCvCompute\": false", StringComparison.Ordinal));

        c.True("默认显示上下张按钮", defaults.ShowNavigationButtons);
        var navigationJson = SettingsDocumentCodec.Serialize(defaults with { ShowNavigationButtons = false });
        var navigationRoundtrip = SettingsDocumentCodec.Read(navigationJson, defaults).Settings!;
        c.True("显示上下张按钮往返", !navigationRoundtrip.ShowNavigationButtons
            && navigationJson.Contains("\"showNavigationButtons\": false", StringComparison.Ordinal));

        var phoneSettings = defaults with { LayoutMode = LayoutMode.Phone };
        var phoneRoundtrip = SettingsDocumentCodec.Read(SettingsDocumentCodec.Serialize(phoneSettings), defaults).Settings!;
        c.Equal("手机布局模式文档往返保留", LayoutMode.Phone, phoneRoundtrip.LayoutMode);
        c.Equal("平台未实现手机布局时保留原值", LayoutMode.Phone,
            SettingsDocumentCodec.Normalize(phoneSettings).LayoutMode);

        var path = Path.Combine(root, "settings.json");
        for (var index = 0; index < 5; index++)
        {
            await File.WriteAllTextAsync(path, "{ broken" + index);
            var store = new FileSettingsStore(path);
            var backup = await store.BackupCorruptAsync();
            var backupExists = backup != null && File.Exists(backup);
            var backupContent = backupExists ? await File.ReadAllTextAsync(backup!) : null;
            var backups = Directory.GetFiles(Path.GetDirectoryName(path)!, Path.GetFileName(path) + ".corrupt-*");
            c.True($"损坏备份 {index + 1} 保留原文", backupExists && backupContent == "{ broken" + index,
                $"backup={backup ?? "null"} exists={backupExists} content={backupContent ?? "null"} remaining="
                + string.Join(" | ", backups.Select(candidate =>
                    $"{candidate} @ {new FileInfo(candidate).CreationTimeUtc:O}")));
            c.True($"损坏备份 {index + 1} 最多三份", backups.Length <= 3, $"实际 {backups.Length}");
        }

        await File.WriteAllTextAsync(path, "{\"schemaVersion\":99}");
        var futureStore = new FileSettingsStore(path);
        var futureRepository = new SettingsRepository(futureStore, new DefaultPerformanceBudget(), false);
        await futureRepository.InitializeAsync();
        c.Equal("未来版本不可写", SettingsLoadOutcome.UnsupportedVersion, futureRepository.LoadOutcome);
        c.True("未来版本禁止提交", !futureRepository.CanCommit);
        c.True("未来版本提交失败", !await futureRepository.CommitAsync(futureRepository.Current));
        c.Equal("未来版本原字节不变", "{\"schemaVersion\":99}", await File.ReadAllTextAsync(path));

        var failing = new FailingStore();
        var failingRepository = new SettingsRepository(failing, new DefaultPerformanceBudget(), false);
        await failingRepository.InitializeAsync();
        c.Equal("普通读取失败不可写", SettingsLoadOutcome.Unreadable, failingRepository.LoadOutcome);
        c.True("普通读取失败不写原文", failing.SaveCalls == 0);
        c.True("不可写提交被拒绝", !await failingRepository.CommitAsync(failingRepository.Current));
        c.True("不可写提交仍未调用保存", failing.SaveCalls == 0);

        var saveFailure = new FailingStore { Content = null, LoadFailure = false, SaveFailure = true };
        var saveFailureRepository = new SettingsRepository(saveFailure, new DefaultPerformanceBudget(), false);
        await saveFailureRepository.InitializeAsync();
        var before = saveFailureRepository.Current;
        var changed = before with { SimilarityMaxResults = before.SimilarityMaxResults + 1 };
        c.True("保存失败返回 false", !await saveFailureRepository.CommitAsync(changed));
        c.True("保存失败保留 last good", before.ContentEquals(saveFailureRepository.Current));
        c.True("保存失败状态可见", saveFailureRepository.LastSaveError != null);
    }

    private sealed class FailingStore : ISettingsStore
    {
        public string? Content { get; init; }
        public bool LoadFailure { get; init; } = true;
        public bool SaveFailure { get; init; }
        public int SaveCalls;

        public Task<SettingsStoreLoadResult> LoadAsync(CancellationToken cancellationToken = default)
        {
            if (LoadFailure) throw new IOException("injected read failure");
            return Task.FromResult(Content == null
                ? SettingsStoreLoadResult.Absent
                : SettingsStoreLoadResult.FromContent(Content));
        }

        public Task SaveAsync(string json, CancellationToken cancellationToken = default)
        {
            SaveCalls++;
            if (SaveFailure) throw new IOException("injected save failure");
            return Task.CompletedTask;
        }

        public Task<string?> BackupCorruptAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<string?>("memory-backup");
    }
}
