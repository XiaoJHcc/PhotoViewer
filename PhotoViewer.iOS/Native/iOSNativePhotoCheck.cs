using PhotoViewer.Apple;
using PhotoViewer.Core.Color;
using PhotoViewer.Core.Image;
using PhotoViewer.Core.Memory;
using PhotoViewer.Core.Platform;
using PhotoViewer.iOS.Core;
using PhotoViewer.ViewModels.Main;

namespace PhotoViewer.iOS.Native;

/// <summary>
/// 验收专用（非产品行为，测试钩子）：`--pv-native-photo-check &lt;目录&gt;` 在真机/模拟器上程序化核对主图链路——
/// 代理先行与 Full 热替换、编码协商（代理 F16 / Full 8 位 Display P3）、热替换变换不变、八方向 GPU 角点、
/// 四张色卡的线性值，以及 Proxy↔Full 同一文件的颜色一致性。结果写 Documents/PhotoViewer/native-photo-checks.txt。
/// 必需素材（缺一即失败，不做静默降级）：orientation-1..8.jpg、color-{srgb,p3,adobe,untagged-adobe}.jpg、
/// color-p3-large.jpg（长边 ≥ 4000 的 Display P3 色卡，用于制造真实代理档）。
/// </summary>
internal static class iOSNativePhotoCheck
{
    /// <summary>验收开关。</summary>
    private const string Switch = "--pv-native-photo-check";
    /// <summary>色卡采样点（容器朝向归一化；与 Mac 的 (10,10) 像素同口径由调用处以尺寸换算）。</summary>
    private const double ColorSamplePixels = 10;
    /// <summary>代理与 Full 同一像素的线性值允许差异（8 位 P3 量化经 P3→scRGB 矩阵放大后的上界）。</summary>
    private const float HotSwapTolerance = 0.03f;

    /// <summary>解析验收开关；命中即以给定目录启动异步核对。</summary>
    /// <param name="args">进程启动参数。</param>
    /// <param name="root">原生根控制器。</param>
    /// <param name="session">原生展示会话。</param>
    public static void MaybeStart(string[] args, iOSNativeRootViewController root, PhotoLoadSession session)
    {
        string? directory = null;
        for (int index = 0; index < args.Length; index++)
        {
            if (string.Equals(args[index], Switch, StringComparison.Ordinal) && index + 1 < args.Length)
            {
                directory = args[index + 1];
            }
        }

        if (directory == null) return;
        _ = RunAsync(root, session, ResolveDocumentsPath(directory));
    }

    /// <summary>相对路径按沙盒 Documents 解析（iOS 无启动工作目录语义，与壳层 --pv-open 同一口径）。</summary>
    /// <param name="path">绝对或相对路径。</param>
    /// <returns>绝对路径。</returns>
    private static string ResolveDocumentsPath(string path)
        => Path.IsPathRooted(path)
            ? path
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), path);

    /// <summary>核对主流程：等布局与素材就绪 → 逐项断言 → 落盘并打印结论。</summary>
    /// <param name="root">原生根控制器。</param>
    /// <param name="session">原生展示会话。</param>
    /// <param name="directory">素材目录（绝对路径）。</param>
    private static async Task RunAsync(iOSNativeRootViewController root, PhotoLoadSession session, string directory)
    {
        var lines = new List<string>();
        string output = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "PhotoViewer");
        Directory.CreateDirectory(output);
        string report = Path.Combine(output, "native-photo-checks.txt");
        try
        {
            var main = iOSNativeShell.Main ?? throw new InvalidOperationException("原生壳主 VM 未就绪");
            await WaitAsync(() => (double)root.Photo.Bounds.Width > 100 && (double)root.Photo.Bounds.Height > 100, "主图视图布局");
            await WaitAsync(() => main.FolderVM.AllFiles.Count > 0, "素材目录枚举");
            string[] required =
            [
                .. Enumerable.Range(1, 8).Select(index => $"orientation-{index}.jpg"),
                "color-srgb.jpg", "color-p3.jpg", "color-adobe.jpg", "color-untagged-adobe.jpg", "color-p3-large.jpg",
            ];
            var missing = required.Where(name => !File.Exists(Path.Combine(directory, name))).ToArray();
            if (missing.Length > 0) throw new FileNotFoundException($"素材缺失：{string.Join(", ", missing)}（目录 {directory}）");
            if (new iOSPresentationEncodingPolicy().Select(ImageSizeClass.Proxy, PressureLevel.Warning) != PixelEncoding.Rgba8)
                throw new InvalidOperationException("压力 ≥ Warning 时代理档未降为 8 位 P3");
            lines.Add($"PASS 编码策略：Proxy 正常档 F16、Warning 起 8 位 P3、Full 恒 8 位 P3；准入每像素字节 = {BitmapLoader.PresentationBytesPerPixel}");

            using var awaiter = new DisplayAwaiter(session);

            // 一、代理先行 + 编码协商：大色卡 → 代理 F16，物理 100% 后 Full 8 位 Display P3，且热替换不改变视口变换
            await SetCurrentAsync(main, awaiter, "color-p3-large.jpg");
            var proxyState = session.Image ?? throw new InvalidOperationException("大色卡未上屏");
            if (!session.IsProxy) throw new InvalidOperationException("大色卡首次加载未走代理档（素材长边需 ≥ 4000）");
            if (proxyState.Encoding != PixelEncoding.RgbaF16) throw new InvalidOperationException($"代理档编码应为 F16，实际 {proxyState.Encoding}");
            if (BitmapLoader.PresentationBytesPerPixel != 4)
                throw new InvalidOperationException($"iOS Full 档准入字节应为 8 位口径（4），实际 {BitmapLoader.PresentationBytesPerPixel}");
            var (proxyRed, proxyGreen, proxyBlue) = await CaptureCenterAsync(root);
            lines.Add($"PASS 代理档：{proxyState.Width}×{proxyState.Height} {proxyState.Encoding}/{proxyState.ColorSpace} 中心线性 (R{proxyRed:0.###} G{proxyGreen:0.###} B{proxyBlue:0.###})");

            // 变换基准与缩放请求同处一个 UI 线程块：请求提交后到该块结束之间不会有解码续体落位，
            // 故这里拿到的就是"Full 尚未热替换"时的视口变换。
            var before = await UiDispatcher.InvokeAsync(() =>
            {
                root.Photo.ZoomToPhysical(1);
                var viewport = root.Photo.Viewport;
                return (viewport.Scale, viewport.PanX, viewport.PanY);
            });
            await WaitAsync(() => !session.IsProxy && session.Image is { } image && image.Encoding == PixelEncoding.Rgba8, "Full 档 8 位 P3 热替换");
            var fullState = session.Image!;
            if (fullState.ColorSpace != ImageColorSpace.DisplayP3) throw new InvalidOperationException($"Full 档色彩空间应为 Display P3，实际 {fullState.ColorSpace}");
            var after = await ViewportStateAsync(root);
            if (Math.Abs(before.Scale - after.Scale) > 1e-6 || Math.Abs(before.PanX - after.PanX) > 0.5 || Math.Abs(before.PanY - after.PanY) > 0.5)
                throw new InvalidOperationException($"热替换改变了视口变换：{before} → {after}");
            lines.Add($"PASS Full 档：{fullState.Width}×{fullState.Height} {fullState.Encoding}/{fullState.ColorSpace}；物理 100% 热替换后变换保持 scale={after.Scale:0.####} pan=({after.PanX:0.##},{after.PanY:0.##})");

            var (fullRed, fullGreen, fullBlue) = await CaptureCenterAsync(root);
            var expectation = AppleAcceptanceChecks.ColorExpectation("color-p3", displayP3: true);
            if (Math.Max(fullRed, Math.Max(fullGreen, fullBlue)) < expectation.RedFloor)
                throw new InvalidOperationException($"Full 档色卡过暗：R{fullRed:0.###} G{fullGreen:0.###} B{fullBlue:0.###}");
            float shift = Math.Max(Math.Abs(proxyRed - fullRed), Math.Max(Math.Abs(proxyGreen - fullGreen), Math.Abs(proxyBlue - fullBlue)));
            if (shift > HotSwapTolerance)
                throw new InvalidOperationException($"代理(F16)↔Full(P3) 存在色偏：Δ={shift:0.####}（{(proxyRed, proxyGreen, proxyBlue)} vs {(fullRed, fullGreen, fullBlue)}）");
            lines.Add($"PASS 热替换无色偏：代理↔Full 中心线性差 Δ={shift:0.####} ≤ {HotSwapTolerance}");
            await RunOnUiThreadAsync(() => root.Photo.FitToView());

            // 二、八方向 GPU 角点（导航图线性帧）
            foreach (int orientation in Enumerable.Range(1, 8))
            {
                string name = $"orientation-{orientation}.jpg";
                await SetCurrentAsync(main, awaiter, name);
                var image = session.Image ?? throw new InvalidOperationException($"{name} 未上屏");
                if (image.Orientation != orientation) throw new InvalidOperationException($"{name} 方向码应为 {orientation}，实际 {image.Orientation}");
                await RunOnUiThreadAsync(() => AppleAcceptanceChecks.VerifyOrientation(root.Photo.CaptureLinearFrame, orientation));
                await RunOnUiThreadAsync(() => VerifyAnalysisOrientation(root, image));
            }
            lines.Add("PASS 八方向：GPU 角点方向与 EXIF 一致，且分析输入与 GPU 显示朝向一致");

            // 三、四张色卡（F16 展示；Adobe 红原色不能被提前裁剪）
            foreach (string name in new[] { "color-srgb.jpg", "color-p3.jpg", "color-adobe.jpg", "color-untagged-adobe.jpg" })
            {
                await SetCurrentAsync(main, awaiter, name);
                var image = session.Image ?? throw new InvalidOperationException($"{name} 未上屏");
                await RunOnUiThreadAsync(() =>
                    AppleAcceptanceChecks.VerifyColor(image, name, ColorSamplePixels / image.Width, ColorSamplePixels / image.Height));
                var (red, green, blue) = AppleAcceptanceChecks.ReadLinearPixel(image, ColorSamplePixels / image.Width, ColorSamplePixels / image.Height);
                lines.Add($"PASS {name}：{image.Width}×{image.Height} {image.Encoding} 中心线性 (R{red:0.###} G{green:0.###} B{blue:0.###})");
            }

            lines.Add($"PASS 压力级别：{MemoryGovernor.CurrentLevel}");
            await File.WriteAllLinesAsync(report, lines);
            Console.WriteLine($"[NativeSmoke] PASS photo ({lines.Count} checks)");
        }
        catch (Exception exception)
        {
            lines.Add($"FAIL {exception}");
            await File.WriteAllLinesAsync(report, lines);
            Console.Error.WriteLine("[NativeSmoke] FAIL photo " + exception);
        }
    }

    /// <summary>对拍分析输入与 GPU 显示朝向，防止镜像转置让 CV/DINO 坐标与主图相反（与 Mac 同口径）。</summary>
    /// <param name="root">原生根控制器（提供 GPU 帧读回）。</param>
    /// <param name="source">展示会话产物。</param>
    private static void VerifyAnalysisOrientation(iOSNativeRootViewController root, DecodedImage source)
    {
        using var analysis = ColorEngine.ToAnalysisSrgb8(source);
        using var gpu = root.Photo.CaptureLinearFrame(true);
        using var rendered = ColorEngine.ToSrgb8(gpu);
        foreach (var point in new[] { (.05, .05), (.95, .05), (.05, .95), (.95, .95) })
        {
            int sourceOffset = (int)(analysis.Height * point.Item2) * analysis.Stride + (int)(analysis.Width * point.Item1) * 4;
            int targetOffset = (int)(rendered.Height * point.Item2) * rendered.Stride + (int)(rendered.Width * point.Item1) * 4;
            for (int channel = 0; channel < 3; channel++)
                if (Math.Abs(analysis.Pixels[sourceOffset + channel] - rendered.Pixels[targetOffset + channel]) > 20)
                    throw new InvalidOperationException($"分析输入与 GPU 朝向不同：EXIF {source.Orientation}");
        }
    }

    /// <summary>切到指定文件并等待该文件上屏（按展示会话上报的文件名匹配，避免读到上一张的旧图）。</summary>
    /// <param name="main">共享主 VM。</param>
    /// <param name="awaiter">上屏等待器。</param>
    /// <param name="name">文件名。</param>
    private static async Task SetCurrentAsync(MainViewModel main, DisplayAwaiter awaiter, string name)
    {
        var file = await UiDispatcher.InvokeAsync(() => main.FolderVM.AllFiles.FirstOrDefault(item => item.Name == name))
            ?? throw new InvalidOperationException($"目录中没有 {name}");
        var pending = awaiter.Prepare(name);
        await UiDispatcher.InvokeAsync(() => { main.CurrentFile = file; });
        await pending;
    }

    /// <summary>在当前视口状态下抓取线性帧并读回中心像素的三通道值（UI 线程执行）。</summary>
    /// <param name="root">原生根控制器。</param>
    private static async Task<(float Red, float Green, float Blue)> CaptureCenterAsync(iOSNativeRootViewController root)
        => await UiDispatcher.InvokeAsync(() =>
        {
            using var frame = root.Photo.CaptureLinearFrame();
            return AppleAcceptanceChecks.ReadLinearPixel(frame, .5, .5);
        });

    /// <summary>读取当前视口变换（缩放与平移），用于验证热替换不改变它。</summary>
    /// <param name="root">原生根控制器。</param>
    private static async Task<(double Scale, double PanX, double PanY)> ViewportStateAsync(iOSNativeRootViewController root)
        => await UiDispatcher.InvokeAsync(() =>
        {
            var viewport = root.Photo.Viewport;
            return (viewport.Scale, viewport.PanX, viewport.PanY);
        });

    /// <summary>把一段 UI 操作封送到主线程执行（Metal 读回与视口状态都只在主线程触碰）。</summary>
    /// <param name="action">要执行的操作。</param>
    private static Task RunOnUiThreadAsync(Action action) => UiDispatcher.InvokeAsync(action);

    /// <summary>轮询条件并设有限超时，失败即抛出不输出虚假通过。</summary>
    /// <param name="condition">条件。</param>
    /// <param name="name">条件名称（超时信息用）。</param>
    private static async Task WaitAsync(Func<bool> condition, string name)
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        while (!condition())
        {
            if (stopwatch.Elapsed > TimeSpan.FromSeconds(30)) throw new TimeoutException(name);
            await Task.Delay(15);
        }
    }

    /// <summary>上屏信号等待器：一次订阅、按文件名分发，避免每张图重复挂事件。</summary>
    private sealed class DisplayAwaiter : IDisposable
    {
        private readonly IDisposable _subscription;
        private string? _waitingFor;
        private TaskCompletionSource? _completion;

        /// <summary>订阅展示会话的上屏信号。</summary>
        /// <param name="signal">上屏信号源。</param>
        public DisplayAwaiter(IDisplayLoadSignal signal)
            => _subscription = signal.Subscribe(file =>
            {
                if (_waitingFor == file.Name) _completion?.TrySetResult();
            });

        /// <summary>登记下一个等待的文件名，返回其完成任务（须先登记再切图，避免错过事件）。</summary>
        /// <param name="name">文件名。</param>
        /// <returns>该文件上屏完成的任务。</returns>
        public Task Prepare(string name)
        {
            _waitingFor = name;
            _completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            return _completion.Task;
        }

        /// <summary>退订。</summary>
        public void Dispose() => _subscription.Dispose();
    }
}
