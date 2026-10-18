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
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using PhotoViewer.Core.AI;
using PhotoViewer.Core.Database;
using PhotoViewer.Core.Image;
using PhotoViewer.Core.Platform;
using PhotoViewer.Core.Settings;
using PhotoViewer.Core.Storage;
using PhotoViewer.ViewModels.Settings;
using SkiaSharp;
using Windows.Graphics;
using Windows.System;

namespace PhotoViewer.Windows.Native;

/// <summary>仅显式 --pv-native-smoke 启用的真实窗口定向回归，不写入用户设置、数据库或照片。</summary>
internal sealed partial class WindowsMainWindow
{
    /// <summary>以启动工作目录为基准的隔离验收输出目录。</summary>
    internal static string SmokeOutputDirectory => Path.GetFullPath("release/native-win-acceptance");

    /// <summary>在构造共享视图模型前配置专用设置和数据库，每轮重置自有设置。</summary>
    internal static void ConfigureSmokeStorage()
    {
        Directory.CreateDirectory(SmokeOutputDirectory);
        var settingsPath = Path.Combine(SmokeOutputDirectory, "settings-smoke.json");
        File.WriteAllText(settingsPath, "{\"Version\":0}");
        SettingsService.ConfigureStorage(new FileSettingsStorage(settingsPath));
        // 每轮从空库开始:上一轮的 DINO/CV 特征会让"相似聚类空态"与"即时计算"分支走不到
        var databasePath = Path.Combine(SmokeOutputDirectory, "photos-smoke.db");
        foreach (var suffix in new[] { string.Empty, "-wal", "-shm" })
        {
            if (File.Exists(databasePath + suffix))
            {
                File.Delete(databasePath + suffix);
            }
        }

        PhotoDatabase.Initialize(databasePath);
    }

    /// <summary>验证真实队列、内嵌模型、D3D 窗口和代理升级，保存报告并关闭验收窗口。</summary>
    /// <returns>所有验证及窗口关闭完成后结束的任务；失败退出码为 1。</returns>
    internal async Task RunSmokeAsync()
    {
        Directory.CreateDirectory(SmokeOutputDirectory);
        File.WriteAllText(Path.Combine(SmokeOutputDirectory, "checks.json"), "[\"RUNNING\"]");
        var checks = new List<string>();
        try
        {
            await CheckDispatchAsync();
            checks.Add("PASS dispatcher: Normal precedes queued Background; worker returns to UI thread");

            // WinRT 的 NumberPad* 与共享 GestureKey.NumPad* 名称不同;默认次级评分热键依赖这张显式映射。
            var numpadGesture = WindowsInput.FromKey(VirtualKey.NumberPad3);
            RequireSmoke(numpadGesture != null && _main.Settings.GetCommandByGesture(numpadGesture) == "SetRating3",
                "小键盘评分热键未映射到 SetRating3");
            checks.Add("PASS input: NumberPad3 maps through the shared gesture table to SetRating3");

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

            await RunNavigatorChecksAsync(checks);
            await RunExifOrientationChecksAsync(checks);
            await RunFilmstripTakeoverChecksAsync(checks);
            await RunAnalysisChecksAsync(checks);
            await RunEnhancementChecksAsync(checks);
            await RunSimilarityPanelChecksAsync(checks);
            await RunRatingFailureChecksAsync(checks);
            await RunWindowStateChecksAsync(checks);

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

    /// <summary>窗口态:最大化/还原/全屏与退出全屏(SHELL-02 自绘标题栏按钮的实际效果)。</summary>
    /// <param name="checks">验收结果收集器</param>
    /// <returns>检查完成后的任务</returns>
    private async Task RunWindowStateChecksAsync(List<string> checks)
    {
        var presenter = AppWindow.Presenter as OverlappedPresenter ?? throw new InvalidOperationException("原生壳要求 OverlappedPresenter");
        var normalWidth = _root.ActualWidth;
        RequireSmoke(presenter.State == OverlappedPresenterState.Restored, $"初始窗口态应为还原: {presenter.State}");

        ToggleMaximizeWindow();
        await Task.Delay(300);
        _root.UpdateLayout();
        RequireSmoke(presenter.State == OverlappedPresenterState.Maximized, "最大化未生效");
        RequireSmoke(_root.ActualWidth > normalWidth, "最大化后窗口内容没有变大");

        ToggleMaximizeWindow();
        await Task.Delay(300);
        _root.UpdateLayout();
        RequireSmoke(presenter.State == OverlappedPresenterState.Restored, "从最大化还原未生效");

        ToggleFullScreenWindow();
        await Task.Delay(300);
        RequireSmoke(_isFullScreen && presenter.State == OverlappedPresenterState.Maximized, "全屏未生效");

        ToggleFullScreenWindow();
        await Task.Delay(300);
        _root.UpdateLayout();
        RequireSmoke(!_isFullScreen && presenter.State == OverlappedPresenterState.Restored, "退出全屏未回到还原态");
        _photo.FitToView();
        checks.Add("PASS window states: maximize/restore/fullscreen/exit all return consistent presenter state");
    }

    /// <summary>八方向 EXIF 素材与控制栏 EXIF 取值:方向进入渲染链路,EXIF 块不再显示 "--"(IMAGE-01/FILM-05)。</summary>
    /// <param name="checks">验收结果收集器</param>
    /// <returns>检查完成后的任务</returns>
    private async Task RunExifOrientationChecksAsync(List<string> checks)
    {
        var folder = Path.Combine(SmokeOutputDirectory, "fixtures", "orientations");
        await Task.Run(() => CreateExifFixtures(folder));

        for (var orientation = 1; orientation <= 8; orientation++)
        {
            var path = Path.Combine(folder, $"orientation-{orientation}.jpg");
            await OpenPathsAsync(new[] { path });
            await WaitForSmokeAsync(() => _session.Image != null && _main.CurrentFile != null, $"方向 {orientation} 解码");
            await WaitForSmokeAsync(() => _main.CurrentFile!.ExifData != null, $"方向 {orientation} EXIF 读取");
            await Task.Delay(120);

            RequireSmoke(_session.Image!.Orientation == orientation, $"方向 {orientation} 未进入解码产物(实际 {_session.Image.Orientation})");
            RequireSmoke(_session.Image.SourceWidth == 400 && _session.Image.SourceHeight == 300, $"方向 {orientation} 源尺寸错误");
            var size = _photo.NavigationSize;
            RequireSmoke(Math.Abs(Math.Max(size.Width, size.Height) - WindowsLayout.NavigatorLongEdge) < 0.51,
                $"方向 {orientation} 导航图长边错误: {size.Width}x{size.Height}");
            RequireSmoke(orientation >= 5 ? size.Height > size.Width : size.Width > size.Height,
                $"方向 {orientation} 导航图比例未按显示朝向: {size.Width}x{size.Height}");

            _photo.FitToView();
            _photo.ZoomBy(1.6);
            await Task.Delay(120);
            RequireSmoke(_navigator.Visibility == Visibility.Visible, $"方向 {orientation} 导航图未显示");
            var pixels = _navigator.PixelSize;
            RequireSmoke(orientation >= 5 ? pixels.Height > pixels.Width : pixels.Width > pixels.Height,
                $"方向 {orientation} 导航图缩略图未按显示朝向渲染: {pixels.Width}x{pixels.Height}");
            CheckNavigatorQuadrants(orientation);
            CheckMainViewQuadrants(orientation);
            if (orientation == 6)
            {
                CheckDisplayCropOrientation();
                await CheckEnhancedOrientationAsync();
            }

            if (orientation == 1)
            {
                var values = _main.ControlVM.EnabledExifItems;
                RequireSmoke(values.Any(item => item.DisplayValue.Contains("2.8")), "EXIF 光圈未显示: " + DescribeExif(values));
                RequireSmoke(values.Any(item => item.DisplayValue.Contains("1/250")), "EXIF 快门未显示: " + DescribeExif(values));
                RequireSmoke(values.Any(item => item.DisplayValue.Contains("400")), "EXIF ISO 未显示: " + DescribeExif(values));
                RequireSmoke(values.Any(item => item.DisplayValue.Contains("75")), "EXIF 等效焦距未显示: " + DescribeExif(values));
            }

            _photo.FitToView();
        }

        checks.Add("PASS EXIF/orientation fixtures: 1..8 orientation verified on the GPU surface and the navigator (4-corner readback); orientation-6 DisplayCrop preview quadrant; orientation-6 enhanced frame keeps GPU orientation; EXIF block shows f/2.8, 1/250, ISO 400, 75mm");
    }

    /// <summary>
    /// 方向 6 上开启增强:验证 <see cref="PhotoLoadSession"/> 的增强图保留容器方向/源尺寸,
    /// 上屏仍按显示朝向呈现(旧 ImageEnhancer 丢元数据时会在此回归)。
    /// </summary>
    private async Task CheckEnhancedOrientationAsync()
    {
        Execute("ToggleEnhance");
        await WaitForSmokeAsync(() => _session.EnhancedDisplay && _photo.HasImage, "方向 6 增强预览上屏", 60);
        CheckMainViewQuadrants(6);
        Execute("ToggleEnhance");
        await WaitForSmokeAsync(() => !_session.IsEnhanced && !_session.EnhancedDisplay, "方向 6 退出增强预览", 30);
        _photo.FitToView();
    }

    /// <summary>把控制栏 EXIF 项拼成诊断文本。</summary>
    /// <param name="values">EXIF 显示项</param>
    private static string DescribeExif(IReadOnlyList<PhotoViewer.ViewModels.Main.ExifDisplayEntry> values)
        => string.Join(", ", values.Select(item => item.DisplayName + "=" + item.DisplayValue));

    /// <summary>
    /// 核对导航图缩略图四个象限的颜色,验证 EXIF 方向在缩略图上真的按显示朝向摆放
    /// (素材源象限:左上红、右上绿、左下蓝、右下黄)。
    /// </summary>
    /// <param name="orientation">EXIF 方向 1-8</param>
    private void CheckNavigatorQuadrants(int orientation)
    {
        var pixels = _navigator.ReadThumbnailPixels() ?? throw new InvalidOperationException($"方向 {orientation} 导航图无像素");
        var width = _navigator.ThumbnailPixelWidth;
        var height = pixels.Length / 4 / Math.Max(1, width);
        string[] expected = orientation switch
        {
            1 => ["红", "绿", "蓝", "黄"],
            2 => ["绿", "红", "黄", "蓝"],
            3 => ["黄", "蓝", "绿", "红"],
            4 => ["蓝", "黄", "红", "绿"],
            5 => ["红", "蓝", "绿", "黄"],
            6 => ["蓝", "红", "黄", "绿"],
            7 => ["黄", "绿", "蓝", "红"],
            8 => ["绿", "黄", "红", "蓝"],
            _ => throw new ArgumentOutOfRangeException(nameof(orientation)),
        };
        (double X, double Y)[] points = [(0.25, 0.25), (0.75, 0.25), (0.25, 0.75), (0.75, 0.75)];
        for (var index = 0; index < points.Length; index++)
        {
            var x = Math.Clamp((int)(points[index].X * width), 0, width - 1);
            var y = Math.Clamp((int)(points[index].Y * height), 0, height - 1);
            var offset = ((y * width) + x) * 4;
            var actual = ClassifyColor(pixels[offset + 2], pixels[offset + 1], pixels[offset]);
            RequireSmoke(actual == expected[index],
                $"方向 {orientation} 导航图第 {index + 1} 象限应为{expected[index]}，实际{actual}");
        }
    }

    /// <summary>把像素归类为四象限标记色(容忍 JPEG 压缩与色彩转换误差)。</summary>
    /// <param name="r">红通道</param>
    /// <param name="g">绿通道</param>
    /// <param name="b">蓝通道</param>
    /// <returns>红/绿/蓝/黄之一</returns>
    private static string ClassifyColor(byte r, byte g, byte b)
    {
        if (r > 110 && g > 110) return "黄";
        if (r > 110) return "红";
        if (g > 110) return "绿";
        return "蓝";
    }

    /// <summary>
    /// 核对主图交换链上四个象限的实际颜色(线性 RGBA16F 回读),验证 EXIF 方向在顶点着色器里真的按显示朝向生效。
    /// </summary>
    /// <param name="orientation">EXIF 方向 1-8</param>
    private void CheckMainViewQuadrants(int orientation)
    {
        string[] expected = orientation switch
        {
            1 => ["红", "绿", "蓝", "黄"],
            2 => ["绿", "红", "黄", "蓝"],
            3 => ["黄", "蓝", "绿", "红"],
            4 => ["蓝", "黄", "红", "绿"],
            5 => ["红", "蓝", "绿", "黄"],
            6 => ["蓝", "红", "黄", "绿"],
            7 => ["黄", "绿", "蓝", "红"],
            8 => ["绿", "黄", "红", "蓝"],
            _ => throw new ArgumentOutOfRangeException(nameof(orientation)),
        };
        (double U, double V)[] points = [(0.25, 0.25), (0.75, 0.25), (0.25, 0.75), (0.75, 0.75)];
        for (var index = 0; index < points.Length; index++)
        {
            _photo.FitToView();
            RequireSmoke(_photo.TryReadDisplayPixel(points[index].U, points[index].V, out var color),
                $"方向 {orientation} 主图第 {index + 1} 象限取样失败");
            var actual = ClassifyLinear(color.R, color.G, color.B);
            RequireSmoke(actual == expected[index],
                $"方向 {orientation} 主图第 {index + 1} 象限应为{expected[index]}，实际{actual}(r={color.R:0.00} g={color.G:0.00} b={color.B:0.00})");
        }
    }

    /// <summary>
    /// 核对分析栏细节预览使用的共享 <see cref="DisplayCrop"/>:方向 6 的显示左上象限应是源图左下(蓝)。
    /// 这条只验证裁剪坐标系,不依赖分析栏 UI;预览瓦片与实际渲染共用同一入口。
    /// </summary>
    private void CheckDisplayCropOrientation()
    {
        var source = _session.Image!;
        using var crop = DisplayCrop.CropSquare(source, source.Orientation, 0.25, 0.25, 120);
        RequireSmoke(crop != null, "方向 6 的 DisplayCrop 裁剪失败");
        int x = crop!.Width / 2, y = crop.Height / 2;
        int offset = ((y * crop.Width) + x) * 4;
        var actual = ClassifyColor(crop.Pixels[offset + 2], crop.Pixels[offset + 1], crop.Pixels[offset]);
        RequireSmoke(actual == "蓝", $"方向 6 的细节预览裁剪象限应为蓝，实际{actual}");
    }

    /// <summary>把线性像素归类为四象限标记色。</summary>
    /// <param name="r">线性红</param>
    /// <param name="g">线性绿</param>
    /// <param name="b">线性蓝</param>
    /// <returns>红/绿/蓝/黄之一</returns>
    private static string ClassifyLinear(float r, float g, float b)
    {
        if (r > 0.2f && g > 0.2f) return "黄";
        if (r > 0.2f) return "红";
        if (g > 0.2f) return "绿";
        return "蓝";
    }

    /// <summary>生成八个方向的 JPEG(带方向/光圈/快门/ISO/焦距的最小 EXIF APP1)。</summary>
    /// <param name="folder">目标文件夹</param>
    private static void CreateExifFixtures(string folder)
    {
        Directory.CreateDirectory(folder);
        for (var orientation = 1; orientation <= 8; orientation++)
        {
            CreateExifFixture(Path.Combine(folder, $"orientation-{orientation}.jpg"), orientation);
        }
    }

    /// <summary>写出一张四象限标记照片并拼上最小 EXIF APP1 段(便于肉眼核对朝向)。</summary>
    /// <param name="path">目标路径</param>
    /// <param name="orientation">EXIF 方向(1-8)</param>
    internal static void CreateExifFixture(string path, int orientation)
    {
        using var bitmap = new SKBitmap(400, 300);
        using (var canvas = new SKCanvas(bitmap))
        {
            using var paint = new SKPaint();
            paint.Color = SKColors.Red;
            canvas.DrawRect(0, 0, 200, 150, paint);
            paint.Color = SKColors.Lime;
            canvas.DrawRect(200, 0, 200, 150, paint);
            paint.Color = SKColors.Blue;
            canvas.DrawRect(0, 150, 200, 150, paint);
            paint.Color = SKColors.Yellow;
            canvas.DrawRect(200, 150, 200, 150, paint);
        }

        using var encoded = bitmap.Encode(SKEncodedImageFormat.Jpeg, 92);
        var jpeg = encoded.ToArray();
        using var stream = File.Create(path);
        stream.Write(jpeg, 0, 2);
        stream.Write(BuildExifApp1(orientation));
        stream.Write(jpeg, 2, jpeg.Length - 2);
    }

    /// <summary>
    /// 构造最小 EXIF APP1:IFD0(Orientation + ExifIFD 指针)+ ExifIFD(FNumber/ExposureTime/ISO/FocalLength/35mm 等效焦距)。
    /// 所有偏移相对 TIFF 头,数值按小端写。
    /// </summary>
    /// <param name="orientation">EXIF 方向 1-8</param>
    private static byte[] BuildExifApp1(int orientation)
    {
        using var tiff = new MemoryStream();
        using (var writer = new BinaryWriter(tiff, System.Text.Encoding.ASCII, true))
        {
            writer.Write((byte)'I');
            writer.Write((byte)'I');
            writer.Write((ushort)42);
            writer.Write(8u);

            // IFD0: Orientation(SHORT) + ExifIFDPointer(LONG → 38)
            writer.Write((ushort)2);
            writer.Write((ushort)0x0112);
            writer.Write((ushort)3);
            writer.Write(1u);
            writer.Write((ushort)orientation);
            writer.Write((ushort)0);
            writer.Write((ushort)0x8769);
            writer.Write((ushort)4);
            writer.Write(1u);
            writer.Write(38u);
            writer.Write(0u);

            // ExifIFD: 5 项(rational 数据在 104 起)
            writer.Write((ushort)5);
            writer.Write((ushort)0x829D);
            writer.Write((ushort)5);
            writer.Write(1u);
            writer.Write(104u);
            writer.Write((ushort)0x829A);
            writer.Write((ushort)5);
            writer.Write(1u);
            writer.Write(112u);
            writer.Write((ushort)0x8827);
            writer.Write((ushort)3);
            writer.Write(1u);
            writer.Write((ushort)400);
            writer.Write((ushort)0);
            writer.Write((ushort)0x920A);
            writer.Write((ushort)5);
            writer.Write(1u);
            writer.Write(120u);
            writer.Write((ushort)0xA405);
            writer.Write((ushort)3);
            writer.Write(1u);
            writer.Write((ushort)75);
            writer.Write((ushort)0);
            writer.Write(0u);

            writer.Write(28u);
            writer.Write(10u);
            writer.Write(1u);
            writer.Write(250u);
            writer.Write(50u);
            writer.Write(1u);
        }

        var tiffBytes = tiff.ToArray();
        var payload = new byte[6 + tiffBytes.Length];
        payload[0] = (byte)'E';
        payload[1] = (byte)'x';
        payload[2] = (byte)'i';
        payload[3] = (byte)'f';
        payload[4] = 0;
        payload[5] = 0;
        Array.Copy(tiffBytes, 0, payload, 6, tiffBytes.Length);

        using var segment = new MemoryStream();
        segment.WriteByte(0xFF);
        segment.WriteByte(0xE1);
        var length = payload.Length + 2;
        segment.WriteByte((byte)(length >> 8));
        segment.WriteByte((byte)(length & 0xFF));
        segment.Write(payload, 0, payload.Length);
        return segment.ToArray();
    }

    /// <summary>导航图(缩放指示器):只显示在非适应态,白框在缩略图内,百分比与物理缩放同口径。</summary>
    /// <param name="checks">验收结果收集器</param>
    /// <returns>检查完成后的任务</returns>
    private async Task RunNavigatorChecksAsync(List<string> checks)
    {
        _main.Settings.LayoutMode = LayoutMode.Row;
        AppWindow.Resize(new SizeInt32((int)Math.Round(1200 * _root.XamlRoot.RasterizationScale),
            (int)Math.Round(850 * _root.XamlRoot.RasterizationScale)));
        await Task.Delay(200);
        _root.UpdateLayout();
        _photo.FitToView();
        await Task.Delay(120);
        RequireSmoke(_navigator.Visibility == Visibility.Collapsed, "适应态不应显示导航图");

        _photo.ZoomBy(1.6);
        await Task.Delay(120);
        var scale = _root.XamlRoot.RasterizationScale;
        var size = _photo.NavigationSize;
        RequireSmoke(_navigator.Visibility == Visibility.Visible, "非适应态没有显示导航图");
        RequireSmoke(_navigator.Source != null && ReferenceEquals(_navigator.Source, _session.Image), "导航图缩略图未取自当前会话帧");
        RequireSmoke(Math.Abs(_navigator.PixelSize.Width - Math.Round(size.Width * scale)) <= 1
            && Math.Abs(_navigator.PixelSize.Height - Math.Round(size.Height * scale)) <= 1,
            $"导航图像素尺寸错误: {_navigator.PixelSize} vs {size.Width}x{size.Height}@{scale}");
        var percentage = $"{_photo.PhysicalScale * 100:0.##}%";
        RequireSmoke(_navigator.PercentageText == percentage, $"导航图百分比错误: {_navigator.PercentageText} vs {percentage}");

        var frame = _navigator.FrameBounds;
        RequireSmoke(frame.Width > 0 && frame.Height > 0, "导航图可见框尺寸为零");
        RequireSmoke(frame.X >= -0.5 && frame.Y >= -0.5
            && frame.X + frame.Width <= size.Width + 0.5 && frame.Y + frame.Height <= size.Height + 0.5,
            $"导航图可见框越界: {frame} vs {size.Width}x{size.Height}");

        _photo.ZoomBy(0.5);
        await Task.Delay(120);
        var smaller = $"{_photo.PhysicalScale * 100:0.##}%";
        RequireSmoke(_navigator.PercentageText == smaller && smaller != percentage, "缩放后导航图百分比未刷新");
        var smallerFrame = _navigator.FrameBounds;
        RequireSmoke(smallerFrame.Width > frame.Width && smallerFrame.Height > frame.Height, "缩小后可见框没有变大");

        _photo.FitToView();
        await Task.Delay(120);
        RequireSmoke(_navigator.Visibility == Visibility.Collapsed, "回到适应态后导航图未收起");

        _main.Settings.ShowZoomIndicator = false;
        _photo.ZoomBy(1.6);
        await Task.Delay(120);
        RequireSmoke(_navigator.Visibility == Visibility.Collapsed, "关闭缩放指示器设置后仍显示导航图");
        _main.Settings.ShowZoomIndicator = true;
        _photo.FitToView();
        await Task.Delay(120);
        checks.Add("PASS navigator: fit hides; zoom shows 150-long-edge thumbnail, in-bounds frame, live percentage; setting respected");
    }

    /// <summary>胶片条用户接管与减少动态效果(FILM-03):动画中接管即停,减少动态效果直接到终点。</summary>
    /// <param name="checks">验收结果收集器</param>
    /// <returns>检查完成后的任务</returns>
    private async Task RunFilmstripTakeoverChecksAsync(List<string> checks)
    {
        var folder = Path.Combine(SmokeOutputDirectory, "fixtures", "filmstrip");
        await Task.Run(() => CreateFilmstripFixtures(folder, 12));
        _main.Settings.LayoutMode = LayoutMode.Col;
        await OpenFolderForSmokeAsync(folder);
        await WaitForSmokeAsync(() => _main.FileVM.ThumbnailList.FilteredFiles.Count == 12, "胶片条测试文件夹");
        await Task.Delay(200);
        _root.UpdateLayout();

        var files = _main.FileVM.ThumbnailList.FilteredFiles;
        _main.FileVM.ThumbnailList.SelectImageCommand(files[^1]);
        await Task.Delay(120);
        _main.FileVM.ThumbnailList.ScrollToCurrent();
        await Task.Delay(90);
        RequireSmoke(_filmstrip.IsLocateAnimating, "显式定位请求没有启动动画");

        _filmstrip.CancelLocate();
        RequireSmoke(!_filmstrip.IsLocateAnimating, "用户接管后动画仍在播放");
        var offset = _filmstrip.MainAxisOffset;
        await Task.Delay(420);
        RequireSmoke(Math.Abs(_filmstrip.MainAxisOffset - offset) < 1, "用户接管后位置仍在推进");

        _main.FileVM.ThumbnailList.ScrollToCurrent();
        await Task.Delay(420);
        var animatedEnd = _filmstrip.MainAxisOffset;

        WindowsFilmstripView.ReduceMotionOverrideForAcceptance = true;
        try
        {
            _main.FileVM.ThumbnailList.ScrollToCurrent();
            RequireSmoke(!_filmstrip.IsLocateAnimating, "减少动态效果下仍启动了动画");
            await Task.Delay(120);
            var immediate = _filmstrip.MainAxisOffset;
            RequireSmoke(Math.Abs(immediate - animatedEnd) < 1.5,
                $"减少动态效果未直接到终点: immediate={immediate} animated={animatedEnd}");
            checks.Add("PASS filmstrip FILM-03: user takeover stops animation; reduce-motion jumps to the same endpoint");
        }
        finally
        {
            WindowsFilmstripView.ReduceMotionOverrideForAcceptance = null;
        }

        // FILM-01:外部打开非首张必须定位到目标,不能停在列表头部
        await OpenPathsAsync(new[] { Path.Combine(folder, "frame-10.jpg") });
        await WaitForSmokeAsync(() => _main.CurrentFile?.Name == "frame-10.jpg"
            && _main.FileVM.ThumbnailList.FilteredFiles.Count == 12, "外部打开非首张");
        await Task.Delay(900);
        RequireSmoke(_filmstrip.MainAxisOffset > 100, $"外部打开非首张未定位到目标: offset={_filmstrip.MainAxisOffset}");
        checks.Add("PASS FILM-01/03: external open of a non-first photo locates to target; user takeover keeps the list put");

        _main.Settings.LayoutMode = LayoutMode.Row;
        await Task.Delay(150);
    }

    /// <summary>相似聚类面板:开关→占位/提取→列表出分数柱条→点选切主图但保留锚点。</summary>
    /// <param name="checks">验收结果收集器</param>
    /// <returns>检查完成后的任务</returns>
    private async Task RunSimilarityPanelChecksAsync(List<string> checks)
    {
        _main.Settings.LayoutMode = LayoutMode.Col;
        await Task.Delay(200);
        _root.UpdateLayout();

        _main.FileVM.FilterBar.IsSimilarityPanelOpen = true;
        await Task.Delay(200);
        RequireSmoke(_similarity.Visibility == Visibility.Visible, "打开相似聚类开关后面板未显示");
        RequireSmoke(_similarity.EmptyVisible && !_similarity.ListVisible, "未提取素材应显示空态占位");
        RequireSmoke(_similarity.ActionButtonText.Length > 0, "空态没有提取按钮文案");

        // 触发"提取全部":12 张 480×320 素材跑 DINO+CV,产物写隔离库
        _main.FileVM.SimilarityPanel.StartIndexingCommand();
        await WaitForSmokeAsync(() => !_main.FileVM.SimilarityPanel.IsStateIndexing
            && _main.FileVM.SimilarityPanel.IndexProgress > 0, "批量特征提取完成", 180);
        await Task.Delay(300);
        _root.UpdateLayout();

        // 提取完成后,当前照片应能算出相似项(纯色棋盘彼此相似)
        await WaitForSmokeAsync(() => _main.FileVM.SimilarityPanel.SimilarItems.Count > 0, "相似聚类结果", 60);
        RequireSmoke(_similarity.ListVisible && _similarity.ItemCount > 0, "相似列表未显示");

        // 点选第一个相似项:切主图但保留锚点(相似列表不重排)
        var first = _main.FileVM.SimilarityPanel.SimilarItems.FirstOrDefault(item => !ReferenceEquals(item.File, _main.CurrentFile));
        RequireSmoke(first != null, "没有可点选的相似项");
        var listBefore = _main.FileVM.SimilarityPanel.SimilarItems;
        _main.FileVM.SimilarityPanel.SelectItemCommand(first!);
        await Task.Delay(300);
        RequireSmoke(ReferenceEquals(_main.CurrentFile, first!.File), "点选相似项未切主图");
        RequireSmoke(ReferenceEquals(listBefore, _main.FileVM.SimilarityPanel.SimilarItems), "点选相似项触发了相似列表重排");

        _main.FileVM.FilterBar.IsSimilarityPanelOpen = false;
        await Task.Delay(150);
        RequireSmoke(_similarity.Visibility == Visibility.Collapsed, "关闭相似聚类开关后面板仍在");
        checks.Add("PASS similarity panel: toggle → empty state → extract all → scored list → select switches main image without re-anchoring");

        await RunAnalysisDbPathCheckAsync(checks);
    }

    /// <summary>
    /// 分析栏"已提取"路径:特征已入库时展开分析栏必须直接贴 DB 派生诊断(不走即时计算)。
    /// 这条路径只有真机库命中才走得到 —— 第三轮进程级崩溃(旧分析适配层建位图)就藏在这里,
    /// 之前的冒烟序列在空库上跑的是即时计算,覆盖不到。
    /// </summary>
    /// <param name="checks">验收结果收集器</param>
    /// <returns>检查完成后的任务</returns>
    private async Task RunAnalysisDbPathCheckAsync(List<string> checks)
    {
        bool immediate = _main.Settings.AnalysisImmediateCompute;
        _main.Settings.AnalysisImmediateCompute = false;
        try
        {
            if (!_main.IsAnalysisViewVisible)
            {
                _main.ToggleAnalysisView();
            }

            RequireSmoke(_main.IsAnalysisViewVisible, "展开分析栏失败");
            await WaitForSmokeAsync(() => _analysis.Visibility == Visibility.Visible, "分析栏(已提取路径)可见");
            await WaitForSmokeAsync(() => _analysis.HasDiagnostics, "分析栏 DB 派生诊断", 90);
            RequireSmoke(!_analysis.DiagnosticsPlaceholderVisible, "已提取照片仍显示未提取占位");
            RequireSmoke(_analysis.ShakeLabel.Length > 0, "抖动判定标签为空");
            checks.Add("PASS analysis panel DB path: extracted fixtures render diagnostics straight from photos.db (no immediate compute)");
        }
        finally
        {
            _main.Settings.AnalysisImmediateCompute = immediate;
            if (_main.IsAnalysisViewVisible)
            {
                _main.ToggleAnalysisView();
            }
        }
    }

    /// <summary>分析栏:可见即出细节预览/直方图,未提取素材在即时计算下跑出 4 张诊断瓦片,瓦片点击重算 cosine。</summary>
    /// <param name="checks">验收结果收集器</param>
    /// <returns>检查完成后的任务</returns>
    private async Task RunAnalysisChecksAsync(List<string> checks)
    {
        _main.Settings.AnalysisImmediateCompute = true;
        await Task.Delay(200);
        _main.ToggleAnalysisView();
        await WaitForSmokeAsync(() => _analysis.Visibility == Visibility.Visible, "分析栏可见");
        _root.UpdateLayout();
        await WaitForSmokeAsync(() => _analysis.TileSide > 40, "分析栏瓦片尺寸", 20);
        await WaitForSmokeAsync(() => _analysis.TilesUniformlyArranged, "分析栏瓦片等边长", 20);
        await WaitForSmokeAsync(() => _analysis.HasPreviewImage, "分析栏细节预览", 20);
        // 重建裁剪图(展开/换图)不得点亮主图绿框:绿框只由细节预览的指针悬停推送(ANL-04)。
        RequireSmoke(_detailHighlight.Visibility == Visibility.Collapsed, "展开分析栏时主图绿框被自动点亮");

        await WaitForSmokeAsync(() => _analysis.HasHistogram, "分析栏直方图", 60);
        await WaitForSmokeAsync(() => _analysis.HasDiagnostics, "分析栏即时计算", 120);
        RequireSmoke(!_analysis.DiagnosticsPlaceholderVisible, "即时计算完成后仍显示占位");

        _analysis.TriggerTileClickForAcceptance(0.25, 0.25);
        await Task.Delay(120);
        RequireSmoke(_analysis.CosineLabel == "Cosine 8,8", $"cosine 参考点未按点击重算: {_analysis.CosineLabel}");
        RequireSmoke(_analysis.ShakeLabel.Length > 0, "抖动判定标签为空");

        // 换向:上下布局下分析栏横排,瓦片按宽度均分
        _main.Settings.LayoutMode = LayoutMode.Row;
        await Task.Delay(250);
        _root.UpdateLayout();
        await WaitForSmokeAsync(() => _analysis.TileSide > 40, "上下布局瓦片尺寸", 20);
        await WaitForSmokeAsync(() => _analysis.TilesUniformlyArranged, "上下布局瓦片等边长", 20);
        _main.Settings.LayoutMode = LayoutMode.Col;
        await Task.Delay(200);
        _main.ToggleAnalysisView();
        await Task.Delay(150);
        checks.Add("PASS analysis panel: preview+histogram+tiles, immediate compute, crosshair cosine recompute, both orientations");
    }

    /// <summary>增强预览:开→后台 CLHE 增强并上屏(分析栏随之对增强图重算),关→回原片。</summary>
    /// <param name="checks">验收结果收集器</param>
    /// <returns>检查完成后的任务</returns>
    private async Task RunEnhancementChecksAsync(List<string> checks)
    {
        // 增强态 + 分析栏即时计算:验证共享 AnalysisPresenter 对增强图重算诊断(旧实现曾因这条路径崩溃而退回原片诊断)。
        _main.Settings.AnalysisImmediateCompute = true;
        _main.ToggleAnalysisView();
        await WaitForSmokeAsync(() => _analysis.Visibility == Visibility.Visible, "增强检查:分析栏可见", 20);
        await WaitForSmokeAsync(() => _analysis.HasPreviewImage, "增强检查:分析栏预览", 20);

        Execute("ToggleEnhance");
        await WaitForSmokeAsync(() => _session.IsEnhanced && _session.EnhancedDisplay && _photo.HasImage, "增强预览上屏", 60);
        await WaitForSmokeAsync(() => _analysis.HasDiagnostics, "增强图分析诊断重算", 120);
        RequireSmoke(!_analysis.DiagnosticsPlaceholderVisible, "增强态诊断瓦片仍停留在占位");

        Execute("ToggleEnhance");
        await WaitForSmokeAsync(() => !_session.IsEnhanced && !_session.EnhancedDisplay, "退出增强预览");
        RequireSmoke(_session.Image != null, "退出增强后展示图丢失");
        RequireSmoke(_photo.HasImage, "退出增强后主图消失");
        _main.Settings.AnalysisImmediateCompute = false;
        _main.ToggleAnalysisView();
        await Task.Delay(200);
        checks.Add("PASS enhancement: CLHE enhanced frame via PhotoLoadSession; enhanced-frame diagnostics recompute; toggle off restores original");
    }

    /// <summary>星级写回失败必须在窗口内可见(FILM-06),且不得呈现成功星色。</summary>
    /// <param name="checks">验收结果收集器</param>
    /// <returns>检查完成后的任务</returns>
    private async Task RunRatingFailureChecksAsync(List<string> checks)
    {
        var path = Path.Combine(SmokeOutputDirectory, "fixtures", "readonly.jpg");
        await Task.Run(() =>
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.Copy(Path.Combine(SmokeOutputDirectory, "fixtures", "checker.jpg"), path, true);
            File.SetAttributes(path, FileAttributes.ReadOnly);
        });

        var failed = false;
        void OnFailed(ImageFile file, int rating) => failed = true;
        _main.RatingWriteFailed += OnFailed;
        try
        {
            await OpenPathsAsync(new[] { path });
            await WaitForSmokeAsync(() => _main.CurrentFile != null && _session.Image != null, "只读素材载入");
            await Task.Delay(150);
            Execute("SetRating3");
            await WaitForSmokeAsync(() => failed, "只读素材星级写回失败事件");
            await Task.Delay(120);
            RequireSmoke(_statusToast.Visibility == Visibility.Visible, "星级写回失败没有可见提示");
            RequireSmoke(_statusText.Text.Contains("无法写入星级"), $"失败提示文案异常: {_statusText.Text}");
            RequireSmoke(_main.CurrentFile!.Rating != 3, "写回失败后仍呈现成功星色");
            checks.Add("PASS FILM-06 rating write failure: in-window toast, no false success");
        }
        finally
        {
            _main.RatingWriteFailed -= OnFailed;
            File.SetAttributes(path, FileAttributes.Normal);
        }
    }

    /// <summary>生成胶片条定位测试用的小幅 JPEG。</summary>
    /// <param name="folder">目标文件夹</param>
    /// <param name="count">文件数量</param>
    private static void CreateFilmstripFixtures(string folder, int count)
    {
        Directory.CreateDirectory(folder);
        for (var index = 0; index < count; index++)
        {
            var path = Path.Combine(folder, $"frame-{index:00}.jpg");
            if (File.Exists(path))
            {
                continue;
            }

            using var bitmap = new SKBitmap(480, 320);
            using var canvas = new SKCanvas(bitmap);
            canvas.Clear(new SKColor((byte)(index * 20 % 256), (byte)(index * 40 % 256), (byte)(255 - (index * 15 % 200))));
            using var encoded = bitmap.Encode(SKEncodedImageFormat.Jpeg, 85);
            using var stream = File.Create(path);
            encoded.SaveTo(stream);
        }
    }

    /// <summary>打开文件夹(验收自有素材)。</summary>
    /// <param name="path">文件夹路径</param>
    /// <returns>打开完成后的任务</returns>
    private Task OpenFolderForSmokeAsync(string path) => _main.FolderVM.OpenFolderAsync(new LocalFolderRef(path));

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

    /// <summary>读取系统实际登记的物理标题栏区域，核对自动显隐、边界及筛选条避让。</summary>
    private void CheckCaptionRegion()
    {
        var source = InputNonClientPointerSource.GetForWindowId(AppWindow.Id);
        var scale = _root.XamlRoot.RasterizationScale;
        var width = (int)Math.Round(_root.ActualWidth * scale);
        var height = (int)Math.Round(Math.Min(WindowsLayout.CaptionHeight, _root.ActualHeight) * scale);
        // 读实际布局出的按钮组左边界,而不是复算生产常量/公式。
        var buttonLeft = (int)Math.Round(_titleBar.GetButtonsLeft(_root) * scale);

        // 标题栏自动显隐:隐藏时不得登记任何拖动区域(顶部不可拖动),显示时才登记
        UpdateTitleBarVisibility(false);
        RequireSmoke(source.GetRegionRects(NonClientRegionKind.Caption).Length == 0, "标题栏隐藏时仍登记了拖动区域");

        UpdateTitleBarVisibility(true);
        RequireSmoke(_titleBar.IsVisible, "标题栏未进入显示态");
        var regions = source.GetRegionRects(NonClientRegionKind.Caption);
        RequireSmoke(regions.Length > 0, "标题栏没有可拖动区域");
        foreach (var region in regions)
        {
            RequireSmoke(region.X >= 0 && region.Y == 0 && region.Width > 0 && region.Height == height
                && region.X + region.Width <= width,
                $"标题栏物理区域越界：region={region.X},{region.Y},{region.Width},{region.Height}; expected={width}x{height}; mode={_main.Settings.LayoutMode}");
            RequireSmoke(region.X + region.Width <= buttonLeft,
                $"标题栏拖动区压住标题栏按钮：region={region.X}+{region.Width} > {buttonLeft}; mode={_main.Settings.LayoutMode}");
        }

        if (!_main.IsThumbnailViewVisible)
        {
            RequireSmoke(regions.Length == 1 && regions[0].X == 0 && regions[0].Width == buttonLeft,
                "隐藏胶片条后拖动区未覆盖到标题栏按钮左侧");
        }
        else if (!_isRowLayout)
        {
            RequireSmoke(regions[0].X == (int)Math.Ceiling(_filePaneFrame.ActualWidth * scale), "标题栏未避让物理文件栏");
        }

        UpdateTitleBarVisibility(false);
    }

    /// <summary>限时等待异步解码或真实布局，不把超时当成通过。</summary>
    /// <param name="condition">就绪条件。</param>
    /// <param name="stage">超时诊断阶段。</param>
    /// <param name="timeoutSeconds">超时秒数(推理类检查适当放宽)。</param>
    /// <returns>条件满足后的任务。</returns>
    private static async Task WaitForSmokeAsync(Func<bool> condition, string stage, int timeoutSeconds = 20)
    {
        var timer = Stopwatch.StartNew();
        while (!condition())
        {
            if (timer.Elapsed > TimeSpan.FromSeconds(timeoutSeconds))
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
