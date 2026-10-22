using AppKit;
using PhotoViewer.Apple;
using PhotoViewer.Core;
using PhotoViewer.Core.AI;
using PhotoViewer.Core.Image;
using PhotoViewer.Core.Platform;
using PhotoViewer.Core.Settings;
using PhotoViewer.Core.Storage;
using PhotoViewer.Mac.Core;
using PhotoViewer.ViewModels.Settings;

namespace PhotoViewer.Mac.Native;

/// <summary>
/// 失败注入回归：用自有副本与测试替身触发打开失败、评分写回失败、Full 升级失败、
/// 损坏设置与批量提取失败，断言共享状态与原生提示条，不修改用户照片。
/// </summary>
public static class NativeFaultAcceptance
{
    /// <summary>执行失败注入套件并写出报告。</summary>
    public static async Task RunAsync(MacMainWindow window, string output)
    {
        var checks = new List<string>();
        string root = Path.Combine(output, "fault-fixtures");
        Directory.CreateDirectory(root);
        string small = Path.Combine(root, "fault-good.jpg");
        string large = Path.Combine(root, "fault-full-fail.jpg");
        File.Copy(Path.GetFullPath("release/native-p2-fixtures/color-srgb.jpg"), small, true);
        File.Copy(Path.GetFullPath("release/native-p2-large-fixtures/color-p3-large.jpg"), large, true);

        bool decoderInstalled = false;
        try
        {
            BitmapLoader.InitializePresentationDecoder(new FullFailingDecoder(new ApplePresentationDecoder()), new MacPresentationEncodingPolicy());
            decoderInstalled = true;
        }
        catch (InvalidOperationException)
        {
            // 启动时已载入照片（例如命令行带路径）时不换解码器；其余失败项仍可运行。
        }

        try
        {
            if (decoderInstalled) await VerifyFullFailureAsync(window, large, checks);
            else checks.Add("NOTE: image03 skipped because the presentation decoder was already in use at startup");

            await VerifyOpenFailureAsync(window, small, checks);
            await VerifyRatingFailureAsync(window, small, checks);
            await VerifyCorruptSettingsAsync(root, checks);
            await VerifySimilarityFailureAsync(window, output, checks);
            await VerifyAnalysisFailureAsync(window, root, checks);
        }
        finally
        {
            try { File.SetUnixFileMode(small, UnixFileMode.UserRead | UnixFileMode.UserWrite); } catch { }
            try { File.SetUnixFileMode(root, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute); } catch { }
        }

        await File.WriteAllLinesAsync(Path.Combine(output, "native-fault-checks.txt"), checks);
        Console.WriteLine($"[NativeSmoke] PASS faults ({checks.Count})");
    }

    /// <summary>IMAGE-03：代理已上屏时 Full 解码失败只报非阻塞提示，代理保持显示。</summary>
    private static async Task VerifyFullFailureAsync(MacMainWindow window, string path, List<string> checks)
    {
        await window.Main.FolderVM.OpenImageAsync(new MacFileRef(path));
        await WaitAsync(() => window.Main.CurrentFile?.Name == Path.GetFileName(path) && window.Session.Image != null, "Full 失败素材代理上屏");
        if (!window.Session.IsProxy) throw new InvalidOperationException("Full 失败素材没有先上代理");

        window.Photo.ZoomToPhysical(1);
        await WaitAsync(() => window.Session.FullError.Length > 0, "Full 失败提示");
        if (window.Session.Image == null || !window.Session.IsProxy)
            throw new InvalidOperationException("Full 失败后代理被清空");
        if (!window.StatusToast.IsVisible || !window.StatusToast.Text.Contains("全分辨率", StringComparison.Ordinal))
            throw new InvalidOperationException($"Full 失败未显示提示条：visible={window.StatusToast.IsVisible}, text={window.StatusToast.Text}");
        checks.Add("image03: full decode failure keeps the proxy and shows the non-blocking FullError toast");
    }

    /// <summary>STATE-02 / OPEN-03：枚举失败显示打开失败提示，并在有目标图时回退单图模式。</summary>
    private static async Task VerifyOpenFailureAsync(MacMainWindow window, string path, List<string> checks)
    {
        string? message = null;
        void OnOpenFailed(string text) => message = text;
        window.Main.OpenFailed += OnOpenFailed;
        try
        {
            var target = new MacFileRef(path);
            var failing = new FailingFolderRef("fault-folder", Path.Combine(Path.GetDirectoryName(path)!, "fault-folder"));
            await window.Main.FolderVM.OpenImageAsync(new ParentFailingFileRef(target, failing));
            await WaitAsync(() => message != null, "打开失败事件");
            if (message == null || !message.Contains("fault-folder", StringComparison.Ordinal))
                throw new InvalidOperationException($"打开失败文案缺少对象：{message}");
            if (!window.StatusToast.IsVisible || window.StatusToast.Text != message)
                throw new InvalidOperationException($"打开失败未显示提示条：{window.StatusToast.Text}");
            if (window.Main.CurrentFile?.File.PathKey != target.PathKey)
                throw new InvalidOperationException("枚举失败后没有回退到目标图片");
            if (window.Main.FolderVM.AllFiles.Count != 1)
                throw new InvalidOperationException($"枚举失败后列表应为单图，实际 {window.Main.FolderVM.AllFiles.Count}");
            checks.Add("state02/open03: folder enumeration failure shows STATE-02 toast and falls back to the single preferred image");
        }
        finally
        {
            window.Main.OpenFailed -= OnOpenFailed;
        }
    }

    /// <summary>STATE-03：文件与目录不可写时评分写回失败，星级不变并显示提示条。</summary>
    private static async Task VerifyRatingFailureAsync(MacMainWindow window, string path, List<string> checks)
    {
        var file = window.Main.CurrentFile ?? throw new InvalidOperationException("评分失败回归缺少当前照片");
        if (file.File.PathKey != new MacFileRef(path).PathKey)
            throw new InvalidOperationException("评分失败回归应使用自有副本");

        int before = file.Rating;
        int target = before == 3 ? 2 : 3;
        string directory = Path.GetDirectoryName(path)!;
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
        File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute);

        var failed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnRatingFailed(ImageFile _, int __) => failed.TrySetResult();
        window.Main.RatingWriteFailed += OnRatingFailed;
        try
        {
            await window.RateAsync(file, target);
            await WaitAsync(() => failed.Task.IsCompleted, "评分写回失败事件");
            if (file.Rating != before)
                throw new InvalidOperationException($"评分写回失败后星级被改动：before={before}, after={file.Rating}");
            if (!window.StatusToast.IsVisible || !window.StatusToast.Text.Contains(file.Name, StringComparison.Ordinal))
                throw new InvalidOperationException($"评分失败未显示提示条：{window.StatusToast.Text}");
            checks.Add($"state03: read-only fixture keeps {before} stars and shows the rating failure toast");
        }
        finally
        {
            window.Main.RatingWriteFailed -= OnRatingFailed;
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    /// <summary>SETTINGS-14：损坏设置先备份再以默认值启动，状态行可观察。</summary>
    private static async Task VerifyCorruptSettingsAsync(string root, List<string> checks)
    {
        string path = Path.Combine(root, "settings-corrupt.json");
        await File.WriteAllTextAsync(path, "{ this is not valid json");
        string serviceDirectory = Path.GetDirectoryName(path)!;
        var service = new SettingsService(new FileSettingsStorage(path));
        using var viewModel = new SettingsViewModel(service, initialModel: null, applyRuntimeSettings: false);
        await WaitAsync(() => viewModel.PersistenceStatus != null, "损坏设置状态行");
        if (string.IsNullOrWhiteSpace(service.LastLoadError) ||
            (!service.LastLoadError.Contains("损坏", StringComparison.Ordinal) && !service.LastLoadError.Contains("读取设置失败", StringComparison.Ordinal)))
            throw new InvalidOperationException($"损坏设置未形成读取错误：{service.LastLoadError}");
        if (!viewModel.PersistenceStatus!.Contains("损坏", StringComparison.Ordinal) &&
            !viewModel.PersistenceStatus.Contains("读取设置失败", StringComparison.Ordinal))
            throw new InvalidOperationException($"设置状态行未显示读取失败：{viewModel.PersistenceStatus}");
        string[] backups = Directory.GetFiles(serviceDirectory, "settings-corrupt.json.corrupt-*");
        if (backups.Length == 0) throw new InvalidOperationException("损坏设置没有生成备份文件");
        checks.Add("settings14: corrupt JSON is backed up before defaults load and the status line reports it");
    }

    /// <summary>SIM-10：批量提取失败时列表态动作区显示错误标签。</summary>
    private static async Task VerifySimilarityFailureAsync(MacMainWindow window, string output, List<string> checks)
    {
        string folder = Path.Combine(output, $"similarity-fault-{DateTime.UtcNow.Ticks}");
        Directory.CreateDirectory(folder);
        await File.WriteAllTextAsync(Path.Combine(folder, "undecodable.jpg"), "not an image");
        bool wasOpen = window.Main.FileVM.FilterBar.IsSimilarityPanelOpen;
        try
        {
            await window.Main.FolderVM.OpenFolderAsync(new MacFolderRef(folder));
            await WaitAsync(() => window.Main.CurrentFile != null, "失败提取素材成为当前照片");
            window.Main.FileVM.FilterBar.IsSimilarityPanelOpen = true;
            var panel = window.Main.FileVM.SimilarityPanel;
            await WaitAsync(() => panel.ShowActionArea, "相似面板动作区出现");
            // IndexError 只由后台索引失败写入；此处注入同一属性的失败文案，核对列表态停靠区绑定。
            panel.GetType().GetProperty(nameof(panel.IndexError))!.SetValue(panel, "注入的提取失败");
            await WaitAsync(() => panel.ShowActionAreaError, "列表态错误标签生效");
            if (!window.Similarity.DockErrorVisible || window.Similarity.DockErrorText != panel.IndexError)
                throw new InvalidOperationException($"停靠区错误标签未绑定：visible={window.Similarity.DockErrorVisible}, text={window.Similarity.DockErrorText}");
            checks.Add("sim10: injected index failure text appears in the list dock error label");
        }
        finally
        {
            window.Main.FileVM.FilterBar.IsSimilarityPanelOpen = wasOpen;
            try { Directory.Delete(folder, true); } catch { }
        }
    }

    /// <summary>ANALYSIS-10：即时计算失败后清空旧诊断图并显示失败文案，不停在“计算中”。</summary>
    private static async Task VerifyAnalysisFailureAsync(MacMainWindow window, string root, List<string> checks)
    {
        string path = Path.Combine(root, $"analysis-fail-{DateTime.UtcNow.Ticks}.jpg");
        File.Copy(Path.GetFullPath("release/native-p2-fixtures/color-srgb.jpg"), path, true);
        byte[]? originalModel = DinoModelResources.TryLoadModelBytes();
        bool previousImmediate = window.Main.Settings.AnalysisImmediateCompute;
        bool previousVisible = window.Main.IsAnalysisViewVisible;
        try
        {
            DinoFeatureExtractor.ConfigureModelOverride([1, 2, 3]);
            window.Main.Settings.AnalysisImmediateCompute = true;
            window.Main.IsAnalysisViewVisible = true;
            await window.Main.FolderVM.OpenImageAsync(new MacFileRef(path));
            await WaitAsync(() => window.Main.CurrentFile?.Name == Path.GetFileName(path) && window.Session.Image != null, "分析失败素材上屏");
            await WaitAsync(() => window.Analysis.Presenter.Tiles
                .Where(tile => tile.Kind == AnalysisTileKind.Diagnostic)
                .All(tile => tile.Placeholder == "分析失败"), "分析失败占位");
            if (window.Analysis.Presenter.Tiles.Any(tile => tile.Kind == AnalysisTileKind.Diagnostic && tile.Source != null))
                throw new InvalidOperationException("分析失败后仍保留旧诊断图");
            checks.Add("analysis10: injected DINO failure clears diagnostics and shows 分析失败");
        }
        finally
        {
            window.Main.IsAnalysisViewVisible = previousVisible;
            window.Main.Settings.AnalysisImmediateCompute = previousImmediate;
            if (originalModel != null) DinoFeatureExtractor.ConfigureModelOverride(originalModel);
        }
    }

    /// <summary>轮询真实状态并设定有限超时，失败不输出虚假的通过报告。</summary>
    private static async Task WaitAsync(Func<bool> condition, string name)
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        while (!condition())
        {
            if (stopwatch.Elapsed > TimeSpan.FromSeconds(30)) throw new TimeoutException(name);
            await Task.Delay(15);
        }
    }

    /// <summary>代理成功、Full 失败的解码器；用于 IMAGE-03 注入。</summary>
    private sealed class FullFailingDecoder(IPresentationImageDecoder inner) : IPresentationImageDecoder
    {
        public Task<DecodedImage?> DecodeAsync(IFileRef file, int? maxLongEdge, PixelEncoding encoding, CancellationToken cancellationToken)
            => maxLongEdge == null && file.Name.Contains("fault-full-fail", StringComparison.Ordinal)
                ? Task.FromException<DecodedImage?>(new InvalidOperationException("injected full decode failure"))
                : inner.DecodeAsync(file, maxLongEdge, encoding, cancellationToken);

        public Task<DecodedImage?> DecodeThumbnailAsync(IFileRef file, int minShortEdge, CancellationToken cancellationToken)
            => inner.DecodeThumbnailAsync(file, minShortEdge, cancellationToken);
    }

    /// <summary>父目录固定返回注入目录的文件引用。</summary>
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

    /// <summary>枚举必定失败的目录引用。</summary>
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
