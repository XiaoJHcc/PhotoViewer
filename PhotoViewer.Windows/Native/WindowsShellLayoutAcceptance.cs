using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using PhotoViewer.Core.Settings;
using SkiaSharp;
using Windows.Foundation;
using Windows.Graphics;

namespace PhotoViewer.Windows.Native;

/// <summary>
/// 宽形态外壳的静态几何验收(SHELL-01、SHELL-11、SHELL-12、TOOLBAR-02/03、FILTER-07):主图视图铺满窗口、
/// 适应区让到文件栏与工具区栏缘、交换链内亚克力区段与工具区背景出现条件、控制栏按钮与星级胶囊外形、筛选控件形态;
/// 每个组合另存一张真实窗口截图供目视复核(屏幕取样,包含交换链与 XAML 合成后的结果)。
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
