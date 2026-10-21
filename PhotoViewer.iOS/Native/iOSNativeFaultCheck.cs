using Foundation;
using System.Diagnostics.CodeAnalysis;
using PhotoViewer.Apple;
using PhotoViewer.Core;
using PhotoViewer.Core.AI;
using PhotoViewer.Core.Image;
using PhotoViewer.Core.Platform;
using PhotoViewer.Core.Settings;
using PhotoViewer.Core.Storage;
using PhotoViewer.iOS.Core;
using PhotoViewer.ViewModels.Main.File;
using PhotoViewer.ViewModels.Settings;

namespace PhotoViewer.iOS.Native;

/// <summary>
/// 失败注入回归（测试钩子）：<c>--pv-native-faults-check &lt;目录&gt;</c> 用自有副本触发
/// 打开失败、评分写回失败、Full 升级失败、损坏设置与相似面板错误标签，只写 App 沙盒。
/// </summary>
internal static class iOSNativeFaultCheck
{
    private const string Switch = "--pv-native-faults-check";
    private const string SettingsKey = "cc.xiaojh.PhotoViewer.Settings.json";

    /// <summary>解析开关；命中即以给定目录启动异步核对。</summary>
    public static void MaybeStart(string[] args, iOSNativeRootViewController root, PhotoLoadSession session)
    {
        string? directory = null;
        for (int index = 0; index + 1 < args.Length; index++)
            if (string.Equals(args[index], Switch, StringComparison.Ordinal)) directory = args[index + 1];
        if (directory == null) return;
        _ = RunAsync(root, session, ResolveDocumentsPath(directory));
    }

    private static string ResolveDocumentsPath(string path)
        => Path.IsPathRooted(path) ? path : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), path);

    private static async Task RunAsync(iOSNativeRootViewController root, PhotoLoadSession session, string directory)
    {
        var lines = new List<string>();
        string output = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "PhotoViewer");
        Directory.CreateDirectory(output);
        string report = Path.Combine(output, "native-fault-checks.txt");
        string work = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "fault-fixtures");
        Directory.CreateDirectory(work);
        string small = Path.Combine(work, "fault-good.jpg");
        string large = Path.Combine(work, "fault-full-fail.jpg");
        File.Copy(Path.Combine(directory, "color-srgb.jpg"), small, true);
        File.Copy(Path.Combine(directory, "color-p3-large.jpg"), large, true);

        bool decoderInstalled = false;
        try
        {
            BitmapLoader.InitializePresentationDecoder(new FullFailingDecoder(new ApplePresentationDecoder()), new iOSPresentationEncodingPolicy());
            decoderInstalled = true;
        }
        catch (InvalidOperationException)
        {
            // 启动时已经载入照片时不换解码器，其余失败项仍可运行。
        }

        try
        {
            await WaitAsync(() => root.View!.Bounds.Width > 100 && root.Photo.Bounds.Width > 100, "主屏布局");
            var main = iOSNativeShell.Main ?? throw new InvalidOperationException("原生壳主 VM 未就绪");

            if (decoderInstalled) await VerifyFullFailureAsync(main, root, session, large, lines);
            else lines.Add("NOTE image03 skipped: presentation decoder already in use at startup");

            await VerifyOpenFailureAsync(main, root, small, lines);
            await VerifyRatingFailureAsync(main, root, small, lines);
            await VerifyCorruptSettingsAsync(lines);
            await VerifySimilarityFailureAsync(main, root, work, lines);
            await VerifyAnalysisFailureAsync(main, root, session, work, lines);
        }
        catch (Exception ex)
        {
            lines.Add("FAIL " + ex);
            Console.WriteLine("[NativeSmoke] FAIL faults " + ex);
            await File.WriteAllLinesAsync(report, lines);
            return;
        }

        await File.WriteAllLinesAsync(report, lines);
        Console.WriteLine($"[NativeSmoke] PASS faults ({lines.Count})");
    }

    private static async Task VerifyFullFailureAsync(
        PhotoViewer.ViewModels.Main.MainViewModel main,
        iOSNativeRootViewController root,
        PhotoLoadSession session,
        string path,
        List<string> lines)
    {
        await main.FolderVM.OpenImageAsync(new iOSFileRef(path));
        await WaitAsync(() => main.CurrentFile?.Name == Path.GetFileName(path) && session.Image != null, "Full 失败素材代理上屏");
        if (!session.IsProxy) throw new InvalidOperationException("Full 失败素材没有先上代理");
        root.Photo.ZoomToPhysical(1);
        await WaitAsync(() => session.FullError.Length > 0, "Full 失败提示");
        if (session.Image == null || !session.IsProxy) throw new InvalidOperationException("Full 失败后代理被清空");
        if (!root.Toast.IsVisible || string.IsNullOrEmpty(root.Toast.Message) || !root.Toast.Message.Contains("全分辨率", StringComparison.Ordinal))
            throw new InvalidOperationException($"Full 失败未显示提示条：visible={root.Toast.IsVisible}, text={root.Toast.Message}");
        lines.Add("PASS image03：Full 解码失败保留代理并显示非阻塞提示");
    }

    private static async Task VerifyOpenFailureAsync(
        PhotoViewer.ViewModels.Main.MainViewModel main,
        iOSNativeRootViewController root,
        string path,
        List<string> lines)
    {
        string? message = null;
        void OnOpenFailed(string text) => message = text;
        main.OpenFailed += OnOpenFailed;
        try
        {
            var target = new iOSFileRef(path);
            var failing = new FailingFolderRef("fault-folder", Path.Combine(Path.GetDirectoryName(path)!, "fault-folder"));
            await main.FolderVM.OpenImageAsync(new ParentFailingFileRef(target, failing));
            await WaitAsync(() => message != null, "打开失败事件");
            if (message == null || !message.Contains("fault-folder", StringComparison.Ordinal))
                throw new InvalidOperationException($"打开失败文案缺少对象：{message}");
            if (!root.Toast.IsVisible || root.Toast.Message != message)
                throw new InvalidOperationException($"打开失败未显示提示条：{root.Toast.Message}");
            if (main.CurrentFile?.File.PathKey != target.PathKey)
                throw new InvalidOperationException("枚举失败后没有回退到目标图片");
            if (main.FolderVM.AllFiles.Count != 1)
                throw new InvalidOperationException($"枚举失败后列表应为单图，实际 {main.FolderVM.AllFiles.Count}");
            lines.Add("PASS state02/open03：枚举失败显示提示并回退单图");
        }
        finally
        {
            main.OpenFailed -= OnOpenFailed;
        }
    }

    private static async Task VerifyRatingFailureAsync(
        PhotoViewer.ViewModels.Main.MainViewModel main,
        iOSNativeRootViewController root,
        string path,
        List<string> lines)
    {
        var file = main.CurrentFile ?? throw new InvalidOperationException("评分失败回归缺少当前照片");
        if (file.File.PathKey != new iOSFileRef(path).PathKey) throw new InvalidOperationException("评分失败回归应使用自有副本");
        int before = file.Rating;
        int target = before == 3 ? 2 : 3;
        string directory = Path.GetDirectoryName(path)!;
        SetReadOnly(path, true);
        SetDirectoryReadOnly(directory, true);
        try
        {
            await root.RateAsync(file, target);
            await WaitAsync(() => root.Toast.IsVisible && (root.Toast.Message ?? "").Contains(file.Name, StringComparison.Ordinal), "评分失败提示");
            if (file.Rating != before) throw new InvalidOperationException($"评分失败后星级被改动：before={before}, after={file.Rating}");
            lines.Add($"PASS state03：只读副本保持 {before} 星并显示评分失败提示");
        }
        finally
        {
            SetReadOnly(path, false);
            SetDirectoryReadOnly(directory, false);
        }
    }

    private static async Task VerifyCorruptSettingsAsync(List<string> lines)
    {
        var defaults = NSUserDefaults.StandardUserDefaults;
        string? original = defaults.StringForKey(SettingsKey);
        defaults.SetString("{ this is not valid json", SettingsKey);
        defaults.Synchronize();
        var service = new SettingsService(new iOSSettingsStorage());
        using var viewModel = new SettingsViewModel(service, initialModel: null, applyRuntimeSettings: false);
        try
        {
            await WaitAsync(() => viewModel.PersistenceStatus != null, "损坏设置状态行");
            if (string.IsNullOrEmpty(service.LastLoadError) || !service.LastLoadError.Contains(".corrupt-", StringComparison.Ordinal))
                throw new InvalidOperationException($"损坏设置没有形成备份：{service.LastLoadError}");
            if (string.IsNullOrEmpty(viewModel.PersistenceStatus) || !viewModel.PersistenceStatus.Contains("损坏", StringComparison.Ordinal))
                throw new InvalidOperationException($"设置状态行未显示读取失败：{viewModel.PersistenceStatus}");

            string backupKey = service.LastLoadError[(service.LastLoadError.IndexOf("已备份为 ", StringComparison.Ordinal) + 5)..].Split(' ')[0];
            if (defaults.StringForKey(backupKey) == null) throw new InvalidOperationException($"备份键不存在：{backupKey}");
            defaults.RemoveObject(backupKey);
            defaults.Synchronize();
            lines.Add("PASS settings14：损坏 JSON 先备份到偏好键，状态行可见");
        }
        finally
        {
            if (original == null) defaults.RemoveObject(SettingsKey);
            else defaults.SetString(original, SettingsKey);
            defaults.Synchronize();
        }
    }

    [DynamicDependency(DynamicallyAccessedMemberTypes.PublicProperties | DynamicallyAccessedMemberTypes.NonPublicProperties, typeof(SimilarityPanelViewModel))]
    private static async Task VerifySimilarityFailureAsync(
        PhotoViewer.ViewModels.Main.MainViewModel main,
        iOSNativeRootViewController root,
        string work,
        List<string> lines)
    {
        string folder = Path.Combine(work, $"similarity-fault-{DateTime.UtcNow.Ticks}");
        Directory.CreateDirectory(folder);
        await File.WriteAllTextAsync(Path.Combine(folder, "undecodable.jpg"), "not an image");
        bool wasOpen = main.FileVM.FilterBar.IsSimilarityPanelOpen;
        try
        {
            await main.FolderVM.OpenFolderAsync(new iOSFolderRef(folder));
            await WaitAsync(() => main.CurrentFile != null, "失败提取素材成为当前照片");
            main.FileVM.FilterBar.IsSimilarityPanelOpen = true;
            var panel = main.FileVM.SimilarityPanel;
            await WaitAsync(() => panel.ShowActionArea && !root.SimilarityPane.Hidden, "相似面板动作区出现");
            typeof(SimilarityPanelViewModel).GetProperty(nameof(panel.IndexError))!.SetValue(panel, "注入的提取失败");
            await WaitAsync(() => panel.ShowActionAreaError && !root.SimilarityPane.DockErrorLabel.Hidden, "列表态错误标签生效");
            if (root.SimilarityPane.DockErrorLabel.Text != panel.IndexError)
                throw new InvalidOperationException($"停靠区错误标签未绑定：{root.SimilarityPane.DockErrorLabel.Text}");
            lines.Add("PASS sim10：注入的提取失败文案显示在列表停靠区");
        }
        finally
        {
            main.FileVM.FilterBar.IsSimilarityPanelOpen = wasOpen;
            try { Directory.Delete(folder, true); } catch { }
        }
    }

    private static async Task VerifyAnalysisFailureAsync(
        PhotoViewer.ViewModels.Main.MainViewModel main,
        iOSNativeRootViewController root,
        PhotoLoadSession session,
        string work,
        List<string> lines)
    {
        string path = Path.Combine(work, $"analysis-fail-{DateTime.UtcNow.Ticks}.jpg");
        File.Copy(Path.Combine(work, "fault-good.jpg"), path, true);
        byte[]? originalModel = DinoModelResources.TryLoadModelBytes();
        bool previousImmediate = main.Settings.AnalysisImmediateCompute;
        bool previousVisible = main.IsAnalysisViewVisible;
        try
        {
            DinoFeatureExtractor.ConfigureModelOverride([1, 2, 3]);
            main.Settings.AnalysisImmediateCompute = true;
            main.IsAnalysisViewVisible = true;
            await main.FolderVM.OpenImageAsync(new iOSFileRef(path));
            await WaitAsync(() => main.CurrentFile?.Name == Path.GetFileName(path) && session.Image != null, "分析失败素材上屏");
            await WaitAsync(() => root.AnalysisPane.Presenter.Tiles
                .Where(tile => tile.Kind == AnalysisTileKind.Diagnostic)
                .All(tile => tile.Placeholder == "分析失败"), "分析失败占位");
            if (root.AnalysisPane.Presenter.Tiles.Any(tile => tile.Kind == AnalysisTileKind.Diagnostic && tile.Source != null))
                throw new InvalidOperationException("分析失败后仍保留旧诊断图");
            lines.Add("PASS analysis10：注入 DINO 失败后清空诊断图并显示失败文案");
        }
        finally
        {
            main.IsAnalysisViewVisible = previousVisible;
            main.Settings.AnalysisImmediateCompute = previousImmediate;
            if (originalModel != null) DinoFeatureExtractor.ConfigureModelOverride(originalModel);
        }
    }

    private static void SetReadOnly(string path, bool readOnly)
        => File.SetUnixFileMode(path, readOnly
            ? UnixFileMode.UserRead | UnixFileMode.GroupRead | UnixFileMode.OtherRead
            : UnixFileMode.UserRead | UnixFileMode.UserWrite);

    private static void SetDirectoryReadOnly(string path, bool readOnly)
        => File.SetUnixFileMode(path, readOnly
            ? UnixFileMode.UserRead | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute
            : UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

    private static async Task WaitAsync(Func<bool> condition, string label, int attempts = 2400)
    {
        for (int attempt = 0; attempt < attempts; attempt++)
        {
            if (condition()) return;
            await Task.Delay(25);
        }
        throw new InvalidOperationException($"等待超时：{label}");
    }

    private sealed class FullFailingDecoder(IPresentationImageDecoder inner) : IPresentationImageDecoder
    {
        public Task<DecodedImage?> DecodeAsync(IFileRef file, int? maxLongEdge, PixelEncoding encoding, CancellationToken cancellationToken)
            => maxLongEdge == null && file.Name.Contains("fault-full-fail", StringComparison.Ordinal)
                ? Task.FromException<DecodedImage?>(new InvalidOperationException("injected full decode failure"))
                : inner.DecodeAsync(file, maxLongEdge, encoding, cancellationToken);

        public Task<DecodedImage?> DecodeThumbnailAsync(IFileRef file, int minShortEdge, CancellationToken cancellationToken)
            => inner.DecodeThumbnailAsync(file, minShortEdge, cancellationToken);
    }

    private sealed class ParentFailingFileRef(IFileRef inner, IFolderRef parent) : IFileRef
    {
        public string Name => inner.Name;
        public Uri Path => inner.Path;
        public string PathKey => inner.PathKey;
        public string? TryGetLocalPath() => inner.TryGetLocalPath();
        public Task<Stream> OpenReadAsync() => inner.OpenReadAsync();
        public Task<StorageItemBasicProperties> GetBasicPropertiesAsync() => inner.GetBasicPropertiesAsync();
        public Task<IFolderRef?> GetParentAsync() => Task.FromResult<IFolderRef?>(parent);
    }

    private sealed class FailingFolderRef(string name, string path) : IFolderRef
    {
        public string Name => name;
        public Uri Path { get; } = new(path);
        public string PathKey => path;
        public string? TryGetLocalPath() => path;

        public async IAsyncEnumerable<IStorageItemRef> GetItemsAsync()
        {
            await Task.Yield();
            throw new UnauthorizedAccessException("injected folder enumeration failure");
#pragma warning disable CS0162
            yield break;
#pragma warning restore CS0162
        }
    }
}
