using AppKit;
using PhotoViewer.Apple;
using PhotoViewer.Core;
using PhotoViewer.Core.AI;
using PhotoViewer.Core.Design;
using PhotoViewer.Core.Image;
using PhotoViewer.Core.Memory;
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
        File.Copy(FixturePaths.Resolve("jpeg", "color-srgb.jpg"), small, true);
        File.Copy(FixturePaths.Resolve("jpeg-large", "color-p3-large.jpg"), large, true);
        window.Window.SetFrame(new CGRect(100, 100, 1180, 800), true);

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
            await VerifyFolderUnavailableAsync(window, small, root, checks);
            await VerifyRatingFailureAsync(window, small, checks);
            await VerifyCorruptSettingsAsync(root, checks);
#if PHOTOVIEWER_DINO
            await VerifySimilarityFailureAsync(window, output, checks);
            await VerifyAnalysisFailureAsync(window, root, checks);
#endif
            await VerifyRawCapabilityAsync(root, checks);
            await VerifyEnhanceClosedByMemoryPressureAsync(window, small, checks);
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
        await window.Main.FolderVM.OpenImageAsync(new LocalFileRef(path));
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
            var target = new LocalFileRef(path);
            var failing = new FailingFolderRef("fault-folder", Path.Combine(Path.GetDirectoryName(path)!, "fault-folder"));
            await window.Main.FolderVM.OpenImageAsync(new ParentFailingFileRef(target, failing));
            await WaitAsync(() => message != null, "打开失败事件");
            if (message == null || !message.Contains("fault-folder", StringComparison.Ordinal))
                throw new InvalidOperationException($"打开失败文案缺少对象：{message}");
            if (!window.StatusToast.IsVisible || window.StatusToast.Text != message)
                throw new InvalidOperationException($"打开失败未显示提示条：{window.StatusToast.Text}");
            if (window.Main.CurrentFile?.File.StorageId != target.StorageId)
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
        if (file.File.StorageId != new LocalFileRef(path).StorageId)
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

    /// <summary>SETTINGS-14：损坏设置先备份再以默认值启动，共享诊断状态可观察。</summary>
    private static async Task VerifyCorruptSettingsAsync(string root, List<string> checks)
    {
        string path = Path.Combine(root, "settings-corrupt.json");
        string serviceDirectory = Path.GetDirectoryName(path)!;
        foreach (string stale in Directory.GetFiles(serviceDirectory, "settings-corrupt.json.corrupt-*"))
            File.Delete(stale);
        await File.WriteAllTextAsync(path, "{ this is not valid json");
        var repository = new SettingsRepository(new FileSettingsStore(path), new DefaultPerformanceBudget(), appleKeyboard: true);
        await repository.InitializeAsync();
        using var viewModel = new SettingsViewModel(repository, new InMemorySettingsStore());
        await WaitAsync(() => viewModel.PersistenceStatus != null, "损坏设置诊断状态");
        if (string.IsNullOrWhiteSpace(repository.LastLoadError) ||
            (!repository.LastLoadError.Contains("损坏", StringComparison.Ordinal) && !repository.LastLoadError.Contains("读取设置失败", StringComparison.Ordinal)))
            throw new InvalidOperationException($"损坏设置未形成读取错误：{repository.LastLoadError}");
        if (!viewModel.PersistenceStatus!.Contains("损坏", StringComparison.Ordinal) &&
            !viewModel.PersistenceStatus.Contains("读取设置失败", StringComparison.Ordinal))
            throw new InvalidOperationException($"诊断状态未记录读取失败：{viewModel.PersistenceStatus}");
        if (repository.LoadOutcome != SettingsLoadOutcome.Recovered)
            throw new InvalidOperationException($"损坏设置未进入恢复态：{repository.LoadOutcome}");
        if (repository.CorruptBackupPath is not { } backupPath || !File.Exists(backupPath))
            throw new InvalidOperationException($"损坏设置没有生成备份文件：{repository.CorruptBackupPath}");
        if (await File.ReadAllTextAsync(backupPath) != "{ this is not valid json")
            throw new InvalidOperationException("备份未保留损坏原文");
        string[] leftovers = Directory.GetFiles(serviceDirectory, "settings-corrupt.json.corrupt-*");
        if (leftovers.Length != 1) throw new InvalidOperationException($"损坏设置备份份数异常：{leftovers.Length}");
        checks.Add("settings14: corrupt JSON is backed up before defaults load and the diagnostic status reports it");
    }

#if PHOTOVIEWER_DINO
    /// <summary>SIM-10：批量提取失败时列表态动作区显示错误标签。</summary>
    private static async Task VerifySimilarityFailureAsync(MacMainWindow window, string output, List<string> checks)
    {
        string folder = Path.Combine(output, $"similarity-fault-{DateTime.UtcNow.Ticks}");
        Directory.CreateDirectory(folder);
        await File.WriteAllTextAsync(Path.Combine(folder, "undecodable.jpg"), "not an image");
        bool wasOpen = window.Main.FileVM.FilterBar.IsSimilarityPanelOpen;
        try
        {
            await window.Main.FolderVM.OpenFolderAsync(new LocalFolderRef(folder));
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
        File.Copy(FixturePaths.Resolve("jpeg", "color-srgb.jpg"), path, true);
        byte[]? originalModel = DinoModelResources.TryLoadModelBytes();
        bool previousImmediate = window.Main.Settings.AnalysisImmediateCompute;
        bool previousCvImmediate = window.Main.Settings.AnalysisImmediateCvCompute;
        bool previousVisible = window.Main.IsAnalysisViewVisible;
        try
        {
            DinoFeatureExtractor.ConfigureModelOverride([1, 2, 3]);
            window.Main.Settings.AnalysisImmediateCvCompute = false;
            window.Main.Settings.AnalysisImmediateCompute = true;
            window.Main.IsAnalysisViewVisible = true;
            await window.Main.FolderVM.OpenImageAsync(new LocalFileRef(path));
            await WaitAsync(() => window.Main.CurrentFile?.Name == Path.GetFileName(path) && window.Session.Image != null, "分析失败素材上屏");
            var presenter = window.Analysis.Presenter;
            await WaitAsync(() => presenter.PcaTile.Placeholder == "分析失败" && presenter.CosineTile.Placeholder == "分析失败",
                "DINO 独立失败占位");
            if (presenter.PcaTile.Source != null || presenter.CosineTile.Source != null)
                throw new InvalidOperationException("DINO 失败后仍保留旧诊断图");
            if (presenter.SharpnessTile.Placeholder != "未提取" || presenter.ShakeTile.Placeholder != "未提取")
                throw new InvalidOperationException("DINO 失败错误地覆盖了未启用 CV 的状态");
            checks.Add("analysis10: injected DINO failure affects only DINO tiles; disabled CV remains 未提取");
        }
        finally
        {
            window.Main.IsAnalysisViewVisible = previousVisible;
            window.Main.Settings.AnalysisImmediateCompute = previousImmediate;
            window.Main.Settings.AnalysisImmediateCvCompute = previousCvImmediate;
            if (originalModel != null) DinoFeatureExtractor.ConfigureModelOverride(originalModel);
        }
    }
#endif

    /// <summary>
    /// SCOPE-04：RAW 走平台全分辨率引擎；引擎不可用时回退 ImageIO，并如实报出容器实际像素尺寸，
    /// 不把内嵌预览或回退产物冒充全分辨率。
    /// </summary>
    private static async Task VerifyRawCapabilityAsync(string root, List<string> checks)
    {
        var decoder = new ApplePresentationDecoder();
        string arw = FixturePaths.Resolve("raw", "A7C01489.ARW");
        if (File.Exists(arw))
        {
            using var full = await decoder.DecodeAsync(new LocalFileRef(arw), null, PixelEncoding.Rgba8, CancellationToken.None)
                ?? throw new InvalidOperationException("ARW 未能解码");
            int longEdge = Math.Max(full.SourceWidth, full.SourceHeight);
            if (longEdge < 6000)
                throw new InvalidOperationException($"ARW 未走全分辨率 RAW 引擎：{full.SourceWidth}x{full.SourceHeight}");
            checks.Add($"scope04: the platform RAW engine decodes ARW at sensor resolution {full.SourceWidth}x{full.SourceHeight}");
        }
        else
        {
            checks.Add("NOTE scope04: Fixtures/raw/A7C01489.ARW missing; full-resolution RAW size not asserted");
        }

        // 伪造 .arw（JPEG 负载）让 RAW 引擎创建/渲染失败，核对回退路径报出的是容器真实尺寸。
        string jpeg = FixturePaths.Resolve("jpeg", "color-srgb.jpg");
        string fallbackPath = Path.Combine(root, "fault-raw-fallback.arw");
        File.Copy(jpeg, fallbackPath, true);
        using var expected = await decoder.DecodeAsync(new LocalFileRef(jpeg), null, PixelEncoding.Rgba8, CancellationToken.None)
            ?? throw new InvalidOperationException("RAW 回退对拍基准未能解码");
        using var fallback = await decoder.DecodeAsync(new LocalFileRef(fallbackPath), null, PixelEncoding.Rgba8, CancellationToken.None)
            ?? throw new InvalidOperationException("RAW 引擎失败后没有回退结果");
        if (fallback.SourceWidth != expected.SourceWidth || fallback.SourceHeight != expected.SourceHeight)
            throw new InvalidOperationException(
                $"RAW 回退尺寸不实：{fallback.SourceWidth}x{fallback.SourceHeight}，容器实际 {expected.SourceWidth}x{expected.SourceHeight}");
        checks.Add("scope04: RAW engine failure falls back to ImageIO and reports the actual container size");
    }

    /// <summary>SCOPE-02 / STATE-03：OS 内存告警关闭增强并发出一次性提示条。</summary>
    private static async Task VerifyEnhanceClosedByMemoryPressureAsync(MacMainWindow window, string path, List<string> checks)
    {
        await window.Main.FolderVM.OpenImageAsync(new LocalFileRef(path));
        await WaitAsync(() => window.Main.CurrentFile?.Name == Path.GetFileName(path) && window.Session.Image != null, "内存告警素材上屏");
        if (!window.Session.IsEnhanced) window.Session.ToggleEnhanced();
        await WaitAsync(() => window.Session.IsEnhanced && window.Session.EnhancedDisplay, "增强图就绪");

        int notices = 0;
        void OnNotice(string _) => notices++;
        window.Session.Notice += OnNotice;
        try
        {
            MemoryGovernor.ReportOsEvent(PressureLevel.Warning);
            await WaitAsync(() => !window.Session.IsEnhanced && !window.Session.EnhancedDisplay, "内存告警关闭增强");
            await WaitAsync(
                () => window.StatusToast.IsVisible && window.StatusToast.Text == StatusMessages.EnhanceClosedForMemory,
                "内存告警提示条");
            if (notices != 1)
                throw new InvalidOperationException($"内存关闭增强应只发一次提示，实际 {notices}");
            checks.Add("scope02: OS warning pressure closes the enhancement and raises one EnhanceClosedForMemory toast");
        }
        finally
        {
            window.Session.Notice -= OnNotice;
        }
    }

    /// <summary>
    /// STATE-05：已打开目录自身的访问检查失败时，保留主图与列表并显示持续的「文件夹不可用」状态条，
    /// 短时提示叠放在状态之上，星级入口禁用；重新打开同一目录恢复后状态条收起。
    /// </summary>
    private static async Task VerifyFolderUnavailableAsync(MacMainWindow window, string small, string root, List<string> checks)
    {
        string folder = Path.Combine(root, $"folder-unavailable-{DateTime.UtcNow.Ticks}");
        Directory.CreateDirectory(folder);
        File.Copy(FixturePaths.Resolve("jpeg", "color-srgb.jpg"), Path.Combine(folder, "availability-a.jpg"), true);
        var real = new LocalFolderRef(folder);
        var monitored = new AccessRevokingFolderRef(real);
        try
        {
            await window.Main.FolderVM.OpenFolderAsync(monitored);
            await WaitAsync(() => window.Main.FolderVM.AllFiles.Count == 1 && window.Main.CurrentFile != null, "文件夹不可用素材就绪");
            if (window.FolderUnavailableBar.IsVisible)
                throw new InvalidOperationException("可用文件夹不应显示不可用状态条");

            monitored.Revoked = true;
            // 同目录刷新枚举失败会走共享访问检查；失败即进入不可用状态，同时保留已显示的快照。
            await window.Main.FolderVM.OpenFolderAsync(monitored);
            await WaitAsync(() => window.Main.FolderVM.IsFolderUnavailable, "文件夹不可用状态");
            await WaitAsync(() => window.FolderUnavailableBar.IsVisible, "不可用状态条出现");

            var bar = window.FolderUnavailableBar;
            if (bar.Text != StatusMessages.FolderUnavailable)
                throw new InvalidOperationException($"状态条文案不符：{bar.Text}");
            if (bar.ActionButton.AttributedTitle?.Value != StatusMessages.ReopenFolder)
                throw new InvalidOperationException($"文字按钮文案不符：{bar.ActionButton.AttributedTitle?.Value}");
            if (window.Main.CurrentFile == null || window.Main.FileVM.ThumbnailList.FilteredFiles.Count != 1)
                throw new InvalidOperationException("不可用状态改变了已显示的照片或列表");
            if ((double)bar.Frame.Height < DesignTokens.StatusToastFontSize + DesignTokens.StatusToastPaddingY * 2
                || (double)bar.ActionButton.Frame.Width <= 0)
                throw new InvalidOperationException($"状态条尺寸异常：{bar.Frame.Size}, button={bar.ActionButton.Frame.Size}");
            // 窗口根视图与主图分区都以顶部为原点：状态条底边距适应区底部 status.toast.bottomMargin。
            double expectedY = window.FitArea.Y + window.FitArea.Height
                - DesignTokens.StatusToastBottomMargin - (double)bar.Frame.Height;
            if (Math.Abs((double)bar.Frame.Y - expectedY) > 0.5)
                throw new InvalidOperationException($"状态条未落在适应区底部距底 24：y={bar.Frame.Y}, expected={expectedY}");
            if (!window.StatusToast.IsVisible)
                throw new InvalidOperationException("打开失败提示应与状态条同时可见");
            double toastBottom = (double)(window.StatusToast.Frame.Y + window.StatusToast.Frame.Height);
            if (Math.Abs(toastBottom - ((double)bar.Frame.Y - DesignTokens.StatusToastStackGap)) > 0.5)
                throw new InvalidOperationException(
                    $"提示条未叠放在状态条之上：toastBottom={toastBottom}, barTop={bar.Frame.Y}");
            if (ReferenceEquals(window.Window.FirstResponder, bar.ActionButton))
                throw new InvalidOperationException("不可用状态条抢占了键盘焦点");
            if (window.ControlBar.Ratings.Count == 0)
                throw new InvalidOperationException("评分组未显示，无法核对禁用态");
            if (window.ControlBar.Ratings.Any(button => button.Enabled))
                throw new InvalidOperationException("文件夹不可用时星级入口仍可用");
            // 状态条只有文字按钮接收命中，空白处与主图照常（AppKit 命中点取父视图坐标）。
            if (bar.HitTest(new CGPoint((double)bar.Frame.X + 2, (double)bar.Frame.Y + 2)) != null)
                throw new InvalidOperationException("状态条空白处拦截了鼠标");
            var buttonCenter = new CGPoint(
                (double)bar.Frame.X + (double)bar.ActionButton.Frame.GetMidX(),
                (double)bar.Frame.Y + (double)bar.ActionButton.Frame.GetMidY());
            if (!ReferenceEquals(bar.HitTest(buttonCenter), bar.ActionButton))
                throw new InvalidOperationException("「重新打开…」没有接收命中");

            await window.Main.FolderVM.OpenFolderAsync(real);
            await WaitAsync(() => !window.Main.FolderVM.IsFolderUnavailable, "文件夹可用性恢复");
            await WaitAsync(() => !bar.IsVisible, "不可用状态条收起");
            if (window.ControlBar.Ratings.Any(button => !button.Enabled))
                throw new InvalidOperationException("恢复后星级入口没有回到可用态");

            // 收尾回到套件原素材，临时目录随后删除时不留下指向它的当前照片。
            await window.Main.FolderVM.OpenImageAsync(new LocalFileRef(small));
            await WaitAsync(() => window.Main.CurrentFile?.Name == Path.GetFileName(small), "文件夹不可用回归收尾");
            checks.Add("state05: revoked folder access keeps the open snapshot, shows the persistent status with reopen action, stacks toasts above it and gates ratings");
        }
        finally
        {
            try { Directory.Delete(folder, true); } catch { }
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
        public string? TryGetLocalPath() => inner.TryGetLocalPath();
        public Task<Stream> OpenReadAsync(CancellationToken cancellationToken = default) => inner.OpenReadAsync(cancellationToken);
        public Task<StorageItemBasicProperties> GetBasicPropertiesAsync(CancellationToken cancellationToken = default) => inner.GetBasicPropertiesAsync(cancellationToken);
        public Task<IFolderRef?> GetParentAsync(CancellationToken cancellationToken = default) => Task.FromResult<IFolderRef?>(parent);
    }

    /// <summary>枚举必定失败的目录引用。</summary>
    private sealed class FailingFolderRef(string name, string path) : IFolderRef
    {
        public string Name => name;
        public Uri Path { get; } = new(path);
        public string? TryGetLocalPath() => path;

        public async IAsyncEnumerable<IStorageItemRef> GetItemsAsync(
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            throw new UnauthorizedAccessException("injected folder enumeration failure");
#pragma warning disable CS0162
            yield break;
#pragma warning restore CS0162
        }
    }

    /// <summary>可撤销访问的目录引用：撤销后枚举与目录自身访问检查都按授权失效失败，其余委托真实目录。</summary>
    private sealed class AccessRevokingFolderRef(IFolderRef inner) : IFolderRef
    {
        /// <summary>是否已撤销目录访问。</summary>
        internal bool Revoked { get; set; }

        public string Name => inner.Name;
        public Uri Path => inner.Path;
        public string? TryGetLocalPath() => inner.TryGetLocalPath();

        public async IAsyncEnumerable<IStorageItemRef> GetItemsAsync(
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            if (Revoked) throw new UnauthorizedAccessException("injected folder access loss");
            await foreach (var item in inner.GetItemsAsync(cancellationToken).ConfigureAwait(false)) yield return item;
        }

        public Task<IFileRef?> FindFileAsync(string name, CancellationToken cancellationToken = default)
            => inner.FindFileAsync(name, cancellationToken);

        public Task<IFileRef> CreateFileAsync(string name, CancellationToken cancellationToken = default)
            => inner.CreateFileAsync(name, cancellationToken);

        public Task CheckAccessAsync(CancellationToken cancellationToken = default)
            => Revoked
                ? Task.FromException(new UnauthorizedAccessException("injected folder access loss"))
                : inner.CheckAccessAsync(cancellationToken);
    }
}
