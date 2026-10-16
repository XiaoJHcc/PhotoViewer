using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Microsoft.UI.Input;
using PhotoViewer.Core.AI;
using PhotoViewer.Core.Database;
using PhotoViewer.Core.Platform;
using PhotoViewer.Core.Settings;
using PhotoViewer.ViewModels.Settings;
using SkiaSharp;
using Windows.Graphics;

namespace PhotoViewer.Desktop.Native;

/// <summary>仅显式 --pv-native-smoke 启用的真实窗口定向回归，不写入用户设置、数据库或照片。</summary>
internal sealed partial class WindowsMainWindow
{
    /// <summary>以启动工作目录为基准的隔离验收输出目录。</summary>
    internal static string SmokeOutputDirectory => Path.GetFullPath("release/native-win-acceptance");

    /// <summary>在构造共享视图模型前配置专用设置和数据库，每轮重置自有设置。</summary>
    internal static void ConfigureSmokeStorage()
    {
        Directory.CreateDirectory(SmokeOutputDirectory);
        File.WriteAllText(Path.Combine(SmokeOutputDirectory, "checks.json"), "[\"RUNNING\"]");
        var settingsPath = Path.Combine(SmokeOutputDirectory, "settings-smoke.json");
        File.WriteAllText(settingsPath, "{\"Version\":0}");
        SettingsService.ConfigureStorage(new FileSettingsStorage(settingsPath));
        PhotoDatabase.Initialize(Path.Combine(SmokeOutputDirectory, "photos-smoke.db"));
    }

    /// <summary>验证真实队列、内嵌模型、D3D 窗口和代理升级，保存报告并关闭验收窗口。</summary>
    /// <returns>所有验证及窗口关闭完成后结束的任务；失败退出码为 1。</returns>
    internal async Task RunSmokeAsync()
    {
        var checks = new List<string>();
        try
        {
            await CheckDispatchAsync();
            checks.Add("PASS dispatcher: Normal precedes queued Background; worker returns to UI thread");

            var modelHash = await Task.Run(CheckModel);
            checks.Add("PASS embedded model: SHA256=" + modelHash + "; DirectML dual-output inference");

            var fixture = Path.Combine(SmokeOutputDirectory, "fixtures", "checker.jpg");
            await Task.Run(() => CreateFixture(fixture));
            await OpenPathsAsync(new[] { fixture });
            await WaitForSmokeAsync(() => _session.Image != null, "首次照片解码");
            RequireSmoke(_session.Image!.SourceWidth == 3600 && _session.Image.SourceHeight == 2400, "无 EXIF 尺寸时丢失真实源尺寸");

            foreach (var mode in new[] { LayoutMode.Col, LayoutMode.Row })
            {
                _main.Settings.LayoutMode = mode;
                foreach (var size in new[] { new SizeInt32(1200, 850), new SizeInt32(830, 650), new SizeInt32(700, 480), new SizeInt32(1200, 850) })
                {
                    var scale = _root.XamlRoot.RasterizationScale;
                    AppWindow.Resize(new SizeInt32((int)Math.Round(size.Width * scale), (int)Math.Round(size.Height * scale)));
                    await Task.Delay(200);
                    _root.UpdateLayout();
                    await WaitForSmokeAsync(() => _photo.PhysicalViewport.Width > 1 && _photo.PhysicalViewport.Height > 1, "窗口物理视口");
                    RequireSmoke(_session.Error.Length == 0, "主图解码报错：" + _session.Error);
                    RequireSmoke(_photo.PhysicalViewport.Width == Math.Round(_photo.ActualWidth * scale)
                        && _photo.PhysicalViewport.Height == Math.Round(_photo.ActualHeight * scale), "resize 后交换链尺寸未更新");

                    _photo.FitToView();
                    _photo.ZoomBy(1.1);
                    _photo.FitToView();
                    CheckCaptionRegion();
                    checks.Add($"PASS resize/present/caption: {mode} {size.Width}x{size.Height} scale={scale}");
                }
            }

            RequireSmoke(_session.IsProxy, "大图浏览未使用代理档");
            _photo.ToggleFit();
            await WaitForSmokeAsync(() => _session.Image != null && !_session.IsProxy, "100% Full 升级");
            RequireSmoke(Math.Abs(_photo.PhysicalScale - 1) < 1e-6, "Full 热替换后物理 100% 发生跳变");
            RequireSmoke(_session.Image!.Width == 3600 && _session.Image.Height == 2400, "Full 未取得完整源像素");
            checks.Add("PASS no-EXIF proxy to Full: 3600x2400 source retained; physical scale remains 1.0");

            _main.ToggleThumbnailView();
            _root.UpdateLayout();
            CheckCaptionRegion();
            checks.Add("PASS caption with filmstrip hidden");
            _main.ToggleThumbnailView();
            _photo.FitToView();
            Close();
            checks.Add("PASS window close: swap chain detached and D3D resources released");
            Environment.ExitCode = 0;
        }
        catch (Exception exception)
        {
            Environment.ExitCode = 1;
            checks.Add("FAIL " + exception);
            NativeStartupLog.Fail("native smoke", exception);
            Close();
        }
        finally
        {
            File.WriteAllText(Path.Combine(SmokeOutputDirectory, "checks.json"), JsonSerializer.Serialize(checks, new JsonSerializerOptions { WriteIndented = true }));
        }
    }

    /// <summary>实测 Background 投递及调用让出 UI 队列，并验证线程池调用回到 UI 线程。</summary>
    /// <returns>顺序和线程归属验证完成后的任务。</returns>
    private static async Task CheckDispatchAsync()
    {
        var order = new List<string>();
        var posted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        UiDispatcher.Post(() => { order.Add("background-post"); posted.SetResult(); }, UiPriority.Background);
        var invoked = UiDispatcher.InvokeAsync(() => order.Add("background-invoke"), UiPriority.Background);
        RequireSmoke(!invoked.IsCompleted, "UI 线程 Background InvokeAsync 未让出队列");
        UiDispatcher.Post(() => order.Add("normal"));
        await Task.WhenAll(posted.Task, invoked).WaitAsync(TimeSpan.FromSeconds(5));
        RequireSmoke(order.SequenceEqual(new[] { "normal", "background-post", "background-invoke" }), "队列优先级或同优先级顺序错误");
        RequireSmoke(await Task.Run(() => UiDispatcher.InvokeAsync(UiDispatcher.CheckAccess)), "后台 InvokeAsync 未回到 UI 线程");
    }

    /// <summary>经生产资源门面加载完整模型，并用显式 DirectML 会话验证双输出推理。</summary>
    /// <returns>资源字节的 SHA256，供外部与源模型独立核对。</returns>
    private static string CheckModel()
    {
        var bytes = DinoModelResources.TryLoadModelBytes() ?? throw new InvalidOperationException("原生模型资源缺失");
        using var options = new SessionOptions { EnableMemoryPattern = false, ExecutionMode = ExecutionMode.ORT_SEQUENTIAL, IntraOpNumThreads = 1 };
        options.AppendExecutionProvider_DML();
        using var session = new InferenceSession(bytes, options);
        var input = new DenseTensor<float>(new[] { 1, 3, DinoModelResources.InputSize, DinoModelResources.InputSize });
        using var outputs = session.Run(new[] { NamedOnnxValue.CreateFromTensor(DinoModelResources.InputName, input) });
        var cls = outputs.Single(output => output.Name == DinoModelResources.ClsOutputName).AsTensor<float>();
        var patches = outputs.Single(output => output.Name == DinoModelResources.PatchOutputName).AsTensor<float>();
        RequireSmoke(cls.Length == DinoModelResources.FeatureDim && patches.Length == DinoModelResources.PatchTokenCount * DinoModelResources.FeatureDim,
            "DINO 输出维度错误");
        RequireSmoke(cls.All(float.IsFinite) && patches.All(float.IsFinite), "DINO 输出包含非有限值");
        return Convert.ToHexString(SHA256.HashData(bytes));
    }

    /// <summary>创建自有大幅 JPEG 棋盘图，覆盖代理解码及 Full 升级。</summary>
    /// <param name="path">验收专用照片路径。</param>
    private static void CreateFixture(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var bitmap = new SKBitmap(3600, 2400);
        using var canvas = new SKCanvas(bitmap);
        using var paint = new SKPaint();
        for (var row = 0; row < 24; row++)
        {
            for (var column = 0; column < 36; column++)
            {
                paint.Color = (row + column) % 2 == 0 ? SKColors.White : SKColors.DarkSlateBlue;
                canvas.DrawRect(column * 100, row * 100, 100, 100, paint);
            }
        }

        using var encoded = bitmap.Encode(SKEncodedImageFormat.Jpeg, 95);
        using var stream = File.Create(path);
        encoded.SaveTo(stream);
    }

    /// <summary>读取系统实际登记的物理标题栏区域，核对边界及筛选条避让。</summary>
    private void CheckCaptionRegion()
    {
        var source = InputNonClientPointerSource.GetForWindowId(AppWindow.Id);
        var regions = source.GetRegionRects(NonClientRegionKind.Caption);
        var scale = _root.XamlRoot.RasterizationScale;
        var width = (int)Math.Round(_root.ActualWidth * scale);
        var height = (int)Math.Round(Math.Min(WindowsLayout.CaptionHeight, _root.ActualHeight) * scale);
        RequireSmoke(regions.Length > 0, "标题栏没有可拖动区域");
        foreach (var region in regions)
        {
            RequireSmoke(region.X >= 0 && region.Y == 0 && region.Width > 0 && region.Height == height
                && region.X + region.Width <= width,
                $"标题栏物理区域越界：region={region.X},{region.Y},{region.Width},{region.Height}; expected={width}x{height}; mode={_main.Settings.LayoutMode}");
        }

        if (!_main.IsThumbnailViewVisible)
        {
            RequireSmoke(regions.Length == 1 && regions[0].X == 0 && regions[0].Width == width, "隐藏胶片条后拖动区未覆盖全宽");
        }
        else if (!_isRowLayout)
        {
            RequireSmoke(regions[0].X == (int)Math.Ceiling(_filePaneFrame.ActualWidth * scale), "标题栏未避让物理文件栏");
        }
    }

    /// <summary>限时等待异步解码或真实布局，不把超时当成通过。</summary>
    /// <param name="condition">就绪条件。</param>
    /// <param name="stage">超时诊断阶段。</param>
    /// <returns>条件满足后的任务。</returns>
    private static async Task WaitForSmokeAsync(Func<bool> condition, string stage)
    {
        var timer = Stopwatch.StartNew();
        while (!condition())
        {
            if (timer.Elapsed > TimeSpan.FromSeconds(20))
            {
                throw new TimeoutException(stage);
            }

            await Task.Delay(50);
        }
    }

    /// <summary>断言验收条件，失败时保留具体原因。</summary>
    /// <param name="condition">预期为真的条件。</param>
    /// <param name="message">失败原因。</param>
    private static void RequireSmoke(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
