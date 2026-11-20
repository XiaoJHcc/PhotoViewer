using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using PhotoViewer.Core.Design;
using PhotoViewer.Core.Settings;
using SkiaSharp;
using Windows.Foundation;
using Windows.Graphics;

namespace PhotoViewer.Windows.Native;

/// <summary>
/// 宽形态外壳的静态几何验收(SHELL-01、SHELL-11、SHELL-12、TOOLBAR-02/03、FILTER-07):主图视图铺满窗口、
/// 适应区让到文件栏与工具区栏缘、交换链内亚克力区段与工具区背景出现条件、控制栏按钮与星级胶囊外形、筛选控件形态;
/// 每个组合另存一张真实窗口截图供目视复核(屏幕取样,包含交换链与 XAML 合成后的结果)。
/// 另在真实照片上回读交换链内栏底的灰度响应,并按屏幕读数量标题栏合成材质的亮度,作为亚克力配方的数值回归。
/// </summary>
internal sealed partial class WindowsMainWindow
{
    /// <summary>行/列 × 分析栏关/开四个组合逐一核对外壳几何并截图。</summary>
    /// <param name="checks">通过项收集器。</param>
    /// <returns>检查完成后的任务。</returns>
    private async Task RunShellLayoutChecksAsync(List<string> checks)
    {
        var originalMode = _main.Settings.LayoutMode;
        var originalAnalysis = _main.IsAnalysisViewVisible;
        try
        {
            var scale = _root.XamlRoot!.RasterizationScale;
            AppWindow.Resize(new SizeInt32((int)Math.Round(WindowsLayout.DefaultWindowWidth * scale),
                (int)Math.Round(WindowsLayout.DefaultWindowHeight * scale)));
            foreach (var mode in new[] { LayoutMode.Row, LayoutMode.Col })
            {
                foreach (var analysis in new[] { false, true })
                {
                    _main.Settings.LayoutMode = mode;
                    if (_main.IsAnalysisViewVisible != analysis)
                    {
                        _main.ToggleAnalysisView();
                    }

                    await Task.Delay(300);
                    _root.UpdateLayout();
                    await Task.Delay(150);
                    _photo.FitToView();
                    SimulateTitleBarPointerForAcceptance(false, WindowsLayout.TitleBarHotZoneHeight + 40);
                    _root.UpdateLayout();
                    await Task.Delay(150);
                    var name = $"shell-{(mode == LayoutMode.Row ? "row" : "col")}-{(analysis ? "analysis" : "plain")}.png";
                    SaveWindowScreenshot(name);
                    var summary = CheckShellGeometry();
                    checks.Add($"PASS SHELL-01/11/12 static shell {mode} analysis={(analysis ? "on" : "off")}: {summary} → screens/{name}");
                }
            }

            await CheckBareToolRegionAsync(checks);
        }
        finally
        {
            if (_main.IsAnalysisViewVisible != originalAnalysis)
            {
                _main.ToggleAnalysisView();
            }

            _main.Settings.LayoutMode = originalMode;
        }
    }

    /// <summary>
    /// SHELL-12：只剩评分与命令(无分析栏、无 EXIF 项)时工具区透明、不画分隔线,控件自身保留材质——
    /// 交换链按星级胶囊与圆按钮的外形逐个画圆角亚克力衬底。核对后恢复 EXIF 显示项。
    /// </summary>
    /// <param name="checks">通过项收集器。</param>
    /// <returns>检查完成后的任务。</returns>
    private async Task CheckBareToolRegionAsync(List<string> checks)
    {
        // 设置集合可能整体换成新实例,按属性名记住原启用项,恢复时对当前集合逐项打开。
        var enabled = _main.Settings.ExifDisplayItems.Where(item => item.IsEnabled).Select(item => item.PropertyName).ToList();
        _main.Settings.LayoutMode = LayoutMode.Col;
        if (_main.IsAnalysisViewVisible)
        {
            _main.ToggleAnalysisView();
        }

        try
        {
            foreach (var item in _main.Settings.ExifDisplayItems.Where(item => item.IsEnabled).ToList())
            {
                item.IsEnabled = false;
            }

            await WaitForSmokeAsync(() => _controlBar.ExifItemCount == 0, "关闭 EXIF 显示项后控制栏未移除 EXIF 组", 10);
            await Task.Delay(300);
            _root.UpdateLayout();
            await Task.Delay(150);
            SaveWindowScreenshot("shell-col-bare.png");
            var summary = CheckShellGeometry();
            var shapes = _controlBar.GetMaterialShapes(_root);
            var regions = _photo.BackdropRegions;
            RequireSmoke(shapes.Count > 0 && shapes.All(shape => regions.Any(region =>
                    SameRect(region.Bounds, shape.Bounds) && Math.Abs(region.CornerRadius - shape.CornerRadius) < 0.01)),
                $"透明工具区没有为控件按外形铺衬底: shapes={shapes.Count} regions=[{string.Join("; ", regions)}]");
            checks.Add($"PASS SHELL-12 bare tool region (rating + commands only): transparent, no divider, {shapes.Count} rounded acrylic backings; {summary} → screens/shell-col-bare.png");
        }
        finally
        {
            foreach (var item in _main.Settings.ExifDisplayItems.Where(item => enabled.Contains(item.PropertyName)).ToList())
            {
                item.IsEnabled = true;
            }
        }

        await WaitForSmokeAsync(() => _controlBar.ExifItemCount == enabled.Count,
            $"恢复 EXIF 显示项后控制栏未刷新: bar={_controlBar.ExifItemCount} control={_main.ControlVM.EnabledExifItems.Count}"
                + $" settings={_main.Settings.EnabledExifItems.Count} expected={enabled.Count}", 10);
    }

    /// <summary>按当前布局独立核对外壳几何;返回写进报告的摘要。</summary>
    /// <returns>摘要。</returns>
    private string CheckShellGeometry()
    {
        double width = _root.ActualWidth;
        double height = _root.ActualHeight;
        bool row = _isRowLayout;
        bool analysis = _main.IsAnalysisViewVisible;

        // SHELL-11：主图视图恒铺满窗口,适应区让到文件栏分隔线外缘与工具区栏缘。
        RequireSmoke(Math.Abs(_photo.ActualWidth - width) < 0.5 && Math.Abs(_photo.ActualHeight - height) < 0.5,
            $"主图视图未铺满窗口: {_photo.ActualWidth}x{_photo.ActualHeight} vs {width}x{height}");
        var insets = _photo.FitInsets;
        double fileEdge = row
            ? _fileDivider.Margin.Top + _fileDivider.Height
            : _fileDivider.Margin.Left + _fileDivider.Width;
        double toolEdge = row ? height - _toolHitArea.Margin.Top : width - _toolHitArea.Margin.Left;
        double fitLead = row ? insets.Top : insets.Left;
        double fitTrail = row ? insets.Bottom : insets.Right;
        RequireSmoke(Math.Abs(fitLead - fileEdge) < 0.5 && Math.Abs(fitTrail - toolEdge) < 0.5,
            $"适应区没有让到栏缘: insets={insets} 文件栏缘={fileEdge} 工具区缘={toolEdge}");
        RequireSmoke((row ? insets.Left + insets.Right : insets.Top + insets.Bottom) < 0.5,
            $"适应区在交叉轴上不应有避让: {insets}");

        // SHELL-12：文件栏与(有分析栏或 EXIF 时)工具区由交换链亚克力铺满,工具区只在朝主图一侧画一条分隔线。
        var regions = _photo.BackdropRegions;
        var fileRect = row ? new Rect(0, 0, width, fileEdge) : new Rect(0, 0, fileEdge, height);
        RequireSmoke(regions.Count > 0 && SameRect(regions[0].Bounds, fileRect) && regions[0].CornerRadius == 0,
            $"文件栏背景区段不符: [{string.Join("; ", regions)}] 期望 {fileRect}");
        var toolRect = new Rect(_toolHitArea.Margin.Left, _toolHitArea.Margin.Top, _toolHitArea.Width, _toolHitArea.Height);
        bool toolBackground = analysis || _controlBar.ExifItemCount > 0;
        RequireSmoke(HasBackdropRegion(toolRect) == toolBackground,
            $"工具区背景出现条件不符: analysis={analysis} exif={_controlBar.ExifItemCount} regions=[{string.Join("; ", regions)}]");
        RequireSmoke((_controlDivider.Visibility == Visibility.Visible) == toolBackground,
            "工具区分隔线出现条件与背景不一致");
        if (toolBackground)
        {
            double dividerPos = row ? _controlDivider.Margin.Top : _controlDivider.Margin.Left;
            RequireSmoke(Math.Abs(dividerPos - (row ? toolRect.Y : toolRect.X)) < 0.5,
                $"工具区分隔线不在朝主图一侧外边界: {dividerPos} vs {toolRect}");
        }

        // TOOLBAR-03/04/10：星级胶囊 224×44 半圆端、命令为 44 见方圆按钮。
        var shapes = _controlBar.GetMaterialShapes(_root);
        double expandedMain = RatingControlGeometry.ExpandedLength;
        double thickness = RatingControlGeometry.Thickness;
        int capsules = shapes.Count(shape => Math.Abs((row ? shape.Bounds.Width : shape.Bounds.Height) - expandedMain) < 0.5
            && Math.Abs((row ? shape.Bounds.Height : shape.Bounds.Width) - thickness) < 0.5
            && Math.Abs(shape.CornerRadius - thickness / 2) < 0.01);
        RequireSmoke(capsules == (_controlBar.RatingGroupVisible ? 1 : 0),
            $"星级胶囊外形不符: [{string.Join("; ", shapes.Select(shape => $"{shape.Bounds} r={shape.CornerRadius}"))}]");
        foreach (var button in _controlBar.CommandButtons)
        {
            RequireSmoke(Math.Abs(button.ActualWidth - ToolbarFlowLayout.CommandSize) < 0.5
                && Math.Abs(button.ActualHeight - ToolbarFlowLayout.CommandSize) < 0.5
                && Math.Abs(button.CornerRadius.TopLeft - ToolbarFlowLayout.CommandSize / 2) < 0.01,
                $"命令按钮不是 44 见方圆按钮: {button.Tag} {button.ActualWidth}x{button.ActualHeight} r={button.CornerRadius.TopLeft}");
        }

        RequireSmoke(shapes.Count == _controlBar.CommandButtons.Count + (_controlBar.RatingGroupVisible ? 1 : 0),
            $"控制栏材质外形数与按钮数不一致: shapes={shapes.Count} commands={_controlBar.CommandButtons.Count}"
                + $" bar={_controlBar.Margin.Left:0},{_controlBar.Margin.Top:0} {_controlBar.Width:0}x{_controlBar.Height:0}"
                + $" [{string.Join("; ", shapes.Select(shape => shape.Bounds))}]");

        // FILTER-07：桌面宽形态筛选控件高 44;列布局与卡片列同宽(96)、三段放不下时为单胶囊。
        var filter = _filterBar.GetInteractiveBounds(_root);
        double filterCross = WindowsFilterBar.ShowsSimilarityToggle && !row
            ? WideFilterLayout.Height * 2 + WideFilterLayout.Gap
            : WideFilterLayout.Height;
        RequireSmoke(Math.Abs(filter.Height - filterCross) < 0.5,
            $"筛选控件高度不符: {filter.Height} vs {filterCross}");
        if (!row)
        {
            double card = FilmCardGeometry.Width(FilmCardTier.Large);
            RequireSmoke(Math.Abs(filter.Width - card) < 0.5 && Math.Abs(filter.X - (MainScreenLayout.FileBarContentWidth - card) / 2) < 0.5,
                $"列布局筛选控件未与卡片列同宽居中: {filter}");
        }

        // SHELL-12：分析栏与控制栏相邻时交叉轴 = 瓦片 + 朝主图一侧内边距 + 接缝。
        string analysisSummary = "off";
        if (analysis)
        {
            double cross = row ? _analysis.Height : _analysis.Width;
            double expected = ToolRegionGeometry.AnalysisWidth(_analysis.TileSide, _main.IsControlViewVisible);
            RequireSmoke(Math.Abs(cross - expected) < 0.5,
                $"分析栏交叉轴不符: {cross} vs {expected} (tile={_analysis.TileSide})");
            analysisSummary = $"tile={_analysis.TileSide:0} cross={cross:0}";
        }

        return $"fitInsets=({insets.Left:0},{insets.Top:0},{insets.Right:0},{insets.Bottom:0}) regions={regions.Count}"
            + $" filter={_filterBar.Mode} {filter.Width:0}x{filter.Height:0} lines={_controlBar.GroupLineCount}"
            + $" shapes={shapes.Count} toolBackground={toolBackground} analysis={analysisSummary}";
    }

    /// <summary>
    /// SHELL-12:让实际打开的黑、灰、白照片覆盖整个视口,回读文件栏的亚克力亮度(GPU 回读,区段在交换链里);
    /// 标题栏亚克力改由合成材质绘制,交换链里没有区段,亮度取真实窗口空白处的屏幕读数。
    /// </summary>
    /// <param name="checks">通过项收集器。</param>
    /// <returns>检查完成后的任务。</returns>
    private async Task RunBarLuminanceChecksAsync(List<string> checks)
    {
        var recipe = _titleBar.BackdropRecipe;
        RequireSmoke(recipe.HasLuminosityLayer
            && recipe.LuminosityOpacity <= DesignTokens.ShellBarAcrylicLuminosityOpacityMax + 1e-4,
            $"亚克力亮度层不符合上限: {recipe.LuminosityOpacity:0.###}");
        var layout = _main.Settings.LayoutMode;
        var filmstrip = _main.IsThumbnailViewVisible;
        var windowSize = AppWindow.Size;
        var samples = new List<string>(3);
        var titleSamples = new List<string>(3);
        try
        {
            _main.Settings.LayoutMode = LayoutMode.Col;
            _main.IsThumbnailViewVisible = true;
            var scale = _root.XamlRoot!.RasterizationScale;
            AppWindow.Resize(new SizeInt32((int)Math.Round(1200 * scale), (int)Math.Round(850 * scale)));
            await Task.Delay(200);
            _root.UpdateLayout();

            var insets = _photo.FitInsets;
            int width = (int)Math.Ceiling((_root.ActualWidth + insets.Left + insets.Right + 200) * scale);
            int height = (int)Math.Ceiling((_root.ActualHeight + insets.Top + insets.Bottom + 200) * scale);
            foreach (byte level in new byte[] { 0, 128, 255 })
            {
                var name = $"sidebar-{level}.jpg";
                var path = Path.Combine(SmokeOutputDirectory, "fixtures", "bar-luminance", name);
                await Task.Run(() => CreateFlatFixture(path, width, height, level));
                await OpenPathsAsync([path]);
                await WaitForSmokeAsync(() => _main.CurrentFile?.Name == name
                    && _session.Image is { } image && image.SourceWidth == width && image.SourceHeight == height,
                    $"栏底灰度素材 {level} 载入");
                _photo.FitToView();
                _photo.ToggleFit();
                await WaitForSmokeAsync(() => _session.Image != null && !_session.IsProxy, $"栏底灰度素材 {level} Full");
                await WaitForUploadAsync($"栏底灰度素材 {level} 上传");
                SimulateTitleBarPointerForAcceptance(false, WindowsLayout.TitleBarHotZoneHeight + 40);
                _root.UpdateLayout();
                RequireSmoke(_photo.TryGetImageRect(new Rect(0, 0, 1, 1), out var photo)
                    && photo.Left <= 0 && photo.Top <= 0 && photo.Right >= _root.ActualWidth && photo.Bottom >= _root.ActualHeight,
                    $"灰度素材 {level} 未覆盖整个窗口: {photo}");
                var regions = _photo.BackdropRegions;
                RequireSmoke(regions.Count > 0 && SameRect(regions[0].Bounds,
                    new Rect(0, 0, _fileBarRight, _root.ActualHeight)), "交换链没有铺文件栏亚克力");
                var file = regions[0].Bounds;
                RequireSmoke(_photo.TryReadViewportPixel(file.Width / 2, file.Height / 2, out var pixel), "文件栏 GPU 回读失败");

                // 中性衬底的编码亮度只取决于亮度层;忽略 2% 噪点,由取样容差覆盖。
                var white = WindowsDisplayColor.Current.SdrWhiteScale;
                var actual = (SrgbEncode(pixel.R / white), SrgbEncode(pixel.G / white), SrgbEncode(pixel.B / white));
                var tint = 0.3f * recipe.Tint.R + 0.59f * recipe.Tint.G + 0.11f * recipe.Tint.B;
                var expected = level / 255f * (1 - recipe.LuminosityOpacity) + tint * recipe.LuminosityOpacity;
                RequireSmoke(Math.Abs(actual.Item1 - expected) < 0.02 && Math.Abs(actual.Item2 - expected) < 0.02
                    && Math.Abs(actual.Item3 - expected) < 0.02, $"文件栏在灰度 {level} 上的亮度偏离配方: {actual} vs {expected:0.###}");
                var gray = (int)Math.Round(actual.Item2 * 255);
                samples.Add($"{level}=#{gray:X2}{gray:X2}{gray:X2}");

                SimulateTitleBarPointerForAcceptance(true, 4);
                _root.UpdateLayout();
                await Task.Delay(ScreenSampleSettleMs);
                RequireSmoke(_titleBarMaterial is { IsVisible: true },
                    $"灰度 {level}:标题栏没有走合成材质路径(WindowsTitleBarBackdrop 不可见)");
                RequireSmoke(!HasTitleBarSwapChainRegion(),
                    $"灰度 {level}:标题栏背景仍画在主图交换链上: [{string.Join("; ", _photo.BackdropRegions)}]");
                // 标题栏由合成材质绘制,交换链里没有区段可回读;取窗口内标题栏空白处(65% 宽、栏高中线,
                // 避开贴左缘的标题文字与最右 4 × 46 的窗口按钮)的屏幕读数。
                var titleSample = new Rect((_root.ActualWidth * 0.65) - (ScreenSampleSize / 2),
                    (WindowsLayout.TitleBarHeight / 2) - (ScreenSampleSize / 2), ScreenSampleSize, ScreenSampleSize);
                RequireSmoke(TrySampleScreenAverage(titleSample, out var titleColor),
                    $"灰度 {level}:标题栏屏幕取样失败: {titleSample}");
                var red = (int)Math.Round(titleColor.R * 255);
                var green = (int)Math.Round(titleColor.G * 255);
                var blue = (int)Math.Round(titleColor.B * 255);
                int target = level switch { 0 => 0x32, 128 => 0x4C, _ => 0x65 };
                var hex = $"#{red:X2}{green:X2}{blue:X2}";
                RequireSmoke(Math.Abs(red - target) <= 3 && Math.Abs(green - target) <= 3 && Math.Abs(blue - target) <= 3,
                    $"SHELL-02 标题栏在灰度 {level} 上偏离中性灰目标: {hex} vs #{target:X2}{target:X2}{target:X2}");
                titleSamples.Add($"{level}={hex}");
            }

            checks.Add($"PASS SHELL-12 file sidebar luminance (sRGB GPU readback): {string.Join(" ", samples)};"
                + $" luminosityOpacity={recipe.LuminosityOpacity:0.###}");
            checks.Add($"PASS SHELL-02 title bar luminance (screen readback of the composed acrylic): {string.Join(" ", titleSamples)}");
        }
        finally
        {
            _main.Settings.LayoutMode = layout;
            _main.IsThumbnailViewVisible = filmstrip;
            SimulateTitleBarPointerForAcceptance(false, WindowsLayout.TitleBarHotZoneHeight + 40);
            AppWindow.Resize(windowSize);
            _root.UpdateLayout();
            _photo.FitToView();
        }
    }

    /// <summary>写出一张纯色 JPEG(自生成灰度源,只写隔离输出目录)。</summary>
    /// <param name="path">目标文件。</param>
    /// <param name="width">宽(源像素)。</param>
    /// <param name="height">高(源像素)。</param>
    /// <param name="value">灰度(0-255)。</param>
    private static void CreateFlatFixture(string path, int width, int height, byte value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var bitmap = new SKBitmap(Math.Max(16, width), Math.Max(16, height));
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(new SKColor(value, value, value));
        using var encoded = bitmap.Encode(SKEncodedImageFormat.Jpeg, 95);
        using var stream = File.Create(path);
        encoded.SaveTo(stream);
    }

    /// <summary>
    /// 把窗口客户区从屏幕取样保存为 PNG(含交换链与 XAML 合成后的实际画面);取样失败只记诊断,不判失败。
    /// </summary>
    /// <param name="fileName">文件名。</param>
    private void SaveWindowScreenshot(string fileName)
    {
        try
        {
            var hwnd = Microsoft.UI.Win32Interop.GetWindowFromWindowId(AppWindow.Id);
            var scale = _root.XamlRoot?.RasterizationScale ?? 1.0;
            var origin = new CapturePoint();
            if (hwnd == IntPtr.Zero || !ClientToScreen(hwnd, ref origin))
            {
                NativeStartupLog.Write($"shell screenshot: 取不到窗口原点,跳过 {fileName}");
                return;
            }

            int width = (int)Math.Round(_root.ActualWidth * scale);
            int height = (int)Math.Round(_root.ActualHeight * scale);
            var pixels = CaptureScreen(origin.X, origin.Y, width, height);
            if (pixels == null)
            {
                NativeStartupLog.Write($"shell screenshot: 屏幕取样失败,跳过 {fileName}");
                return;
            }

            var directory = Path.Combine(SmokeOutputDirectory, "screens");
            Directory.CreateDirectory(directory);
            using var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Opaque));
            Marshal.Copy(pixels, 0, bitmap.GetPixels(), pixels.Length);
            using var image = SKImage.FromBitmap(bitmap);
            using var data = image.Encode(SKEncodedImageFormat.Png, 100);
            using var stream = File.Create(Path.Combine(directory, fileName));
            data.SaveTo(stream);
        }
        catch (Exception ex)
        {
            NativeStartupLog.Fail("shell screenshot", ex);
        }
    }

    /// <summary>GDI 屏幕取样(32 位 BGRA,自上而下)。</summary>
    /// <param name="x">屏幕左缘(物理像素)。</param>
    /// <param name="y">屏幕上缘(物理像素)。</param>
    /// <param name="width">宽。</param>
    /// <param name="height">高。</param>
    /// <returns>像素;失败为 null。</returns>
    private static byte[]? CaptureScreen(int x, int y, int width, int height)
    {
        if (width <= 0 || height <= 0)
        {
            return null;
        }

        IntPtr screen = GetDC(IntPtr.Zero);
        IntPtr memory = CreateCompatibleDC(screen);
        IntPtr bitmap = CreateCompatibleBitmap(screen, width, height);
        IntPtr previous = SelectObject(memory, bitmap);
        try
        {
            const uint SourceCopy = 0x00CC0020;
            const uint CaptureLayered = 0x40000000;
            if (!BitBlt(memory, 0, 0, width, height, screen, x, y, SourceCopy | CaptureLayered))
            {
                return null;
            }

            var info = new BitmapInfoHeader
            {
                Size = (uint)Marshal.SizeOf<BitmapInfoHeader>(),
                Width = width,
                Height = -height,
                Planes = 1,
                BitCount = 32,
            };
            var pixels = new byte[width * height * 4];
            SelectObject(memory, previous);
            return GetDIBits(memory, bitmap, 0, (uint)height, pixels, ref info, 0) == height ? pixels : null;
        }
        finally
        {
            SelectObject(memory, previous);
            DeleteObject(bitmap);
            DeleteDC(memory);
            ReleaseDC(IntPtr.Zero, screen);
        }
    }

    /// <summary>屏幕坐标点(物理像素)。</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct CapturePoint
    {
        /// <summary>X。</summary>
        public int X;

        /// <summary>Y。</summary>
        public int Y;
    }

    /// <summary>GDI 位图信息头(只用 32 位自上而下 BI_RGB)。</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfoHeader
    {
        public uint Size;
        public int Width;
        public int Height;
        public ushort Planes;
        public ushort BitCount;
        public uint Compression;
        public uint SizeImage;
        public int XPelsPerMeter;
        public int YPelsPerMeter;
        public uint ClrUsed;
        public uint ClrImportant;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ClientToScreen(IntPtr hwnd, ref CapturePoint point);

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleDC(IntPtr dc);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleBitmap(IntPtr dc, int width, int height);

    [DllImport("gdi32.dll")]
    private static extern IntPtr SelectObject(IntPtr dc, IntPtr gdiObject);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool BitBlt(IntPtr destination, int x, int y, int width, int height, IntPtr source, int sourceX,
        int sourceY, uint operation);

    [DllImport("gdi32.dll")]
    private static extern int GetDIBits(IntPtr dc, IntPtr bitmap, uint start, uint lines, byte[] bits,
        ref BitmapInfoHeader info, uint usage);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(IntPtr gdiObject);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteDC(IntPtr dc);
}
