using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using LibHeifSharp;
using PhotoViewer.Core.Image;
using PhotoViewer.Core.Storage;
using PhotoViewer.Windows.Core;

namespace PhotoViewer.Windows.Native;

internal sealed partial class WindowsMainWindow
{
    private async Task RunDeploymentChecksAsync(List<string> checks)
    {
        var fixture = Path.Combine(SmokeOutputDirectory, "fixtures", "heif", "quadrants.heic");
        await Task.Run(() => CreateHeifFixture(fixture));
        var previousImage = _session.Image;
        await OpenPathsAsync(new[] { fixture });
        await WaitForSmokeAsync(() => _main.CurrentFile?.Name == "quadrants.heic"
            && _session.Image != null && !ReferenceEquals(_session.Image, previousImage)
            && _photo.HasImage, "HEVC HEIF 主图解码并上屏");
        RequireSmoke(_session.Error.Length == 0, "HEIF 主图解码报错：" + _session.Error);
        RequireSmoke(_session.Image!.SourceWidth == 256 && _session.Image.SourceHeight == 128
            && _session.Image.Width == 256 && _session.Image.Height == 128,
            $"HEIF 源尺寸或解码尺寸错误：{_session.Image.SourceWidth}x{_session.Image.SourceHeight}, {_session.Image.Width}x{_session.Image.Height}");
        RequireSmoke(_session.Image.Orientation == 1, "HEIF 自有素材方向应为 1");
        await WaitForSmokeAsync(() => _photo.PhysicalViewport.Width > 1 && _photo.PhysicalViewport.Height > 1, "HEIF 物理视口");
        _photo.FitToView();
        RequireSmoke(_photo.IsFit && _photo.PhysicalScale > 0, "HEIF 适应视口不可用");
        CheckMainViewQuadrants(1);

        _photo.FocusOnImagePoint(0.5, 0.5, 1.0);
        await WaitForSmokeAsync(() => _session.Image != null && !_session.IsProxy && _photo.HasImage, "HEIF 物理 100%");
        RequireSmoke(!_photo.IsFit && Math.Abs(_photo.PhysicalScale - 1) < 1e-6, "HEIF 物理 100% 不可用");
        string[] expected = ["红", "绿", "蓝", "黄"];
        (double U, double V)[] points = [(0.25, 0.25), (0.75, 0.25), (0.25, 0.75), (0.75, 0.75)];
        for (var index = 0; index < points.Length; index++)
        {
            RequireSmoke(_photo.TryReadDisplayPixel(points[index].U, points[index].V, out var color),
                $"HEIF 100% 第 {index + 1} 象限 GPU 回读失败");
            RequireSmoke(ClassifyLinear(color.R, color.G, color.B) == expected[index],
                $"HEIF 100% 第 {index + 1} 象限颜色错误：{color}");
        }
        _photo.FitToView();
        RequireSmoke(_photo.IsFit, "HEIF 100% 后无法回到适应态");

        using var thumbnail = await new WindowsPresentationDecoder().DecodeThumbnailAsync(new LocalFileRef(fixture), 64, CancellationToken.None)
            ?? throw new InvalidOperationException("HEIF 生产缩略图解码失败");
        RequireSmoke(thumbnail.Width == 128 && thumbnail.Height == 64 && thumbnail.Encoding == PixelEncoding.Bgra32,
            $"HEIF 缩略图尺寸或编码错误：{thumbnail.Width}x{thumbnail.Height}, {thumbnail.Encoding}");
        for (var index = 0; index < points.Length; index++)
        {
            var x = (int)(points[index].U * thumbnail.Width);
            var y = (int)(points[index].V * thumbnail.Height);
            var offset = y * thumbnail.Stride + x * 4;
            var actual = ClassifyColor(thumbnail.Pixels[offset + 2], thumbnail.Pixels[offset + 1], thumbnail.Pixels[offset]);
            RequireSmoke(actual == expected[index], $"HEIF 缩略图第 {index + 1} 象限应为{expected[index]}，实际{actual}");
        }
        checks.Add($"PASS HEIF: native libheif={LibHeifInfo.Version}; self-generated HEVC RGB24 256x128 red/green/blue/yellow quadrants (not camera material); fixture={fixture}; production session source/full size, Fit/physical 100% GPU readback, production 128x64 thumbnail quadrants");

        if (Environment.GetCommandLineArgs().Contains("--pv-native-portable", StringComparer.Ordinal))
        {
            CheckPortableModuleOrigins(checks);
        }
    }

    private static void CreateHeifFixture(string path)
    {
        const int width = 256, height = 128;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var context = new HeifContext();
        using var encoder = context.GetEncoder(HeifCompressionFormat.Hevc);
        encoder.SetLossless(false);
        encoder.SetLossyQuality(95);
        using var image = new HeifImage(width, height, HeifColorspace.Rgb, HeifChroma.InterleavedRgb24);
        image.AddPlane(HeifChannel.Interleaved, width, height, 8);
        var plane = image.GetPlane(HeifChannel.Interleaved);
        RequireSmoke(plane.Scan0 != IntPtr.Zero && plane.Stride >= width * 3, "HEIF 编码 RGB24 平面无效");
        var row = new byte[width * 3];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var offset = x * 3;
                var left = x < width / 2;
                var top = y < height / 2;
                row[offset] = (byte)((top && left || !top && !left) ? 255 : 0);
                row[offset + 1] = (byte)(!left ? 255 : 0);
                row[offset + 2] = (byte)((!top && left) ? 255 : 0);
            }
            Marshal.Copy(row, 0, IntPtr.Add(plane.Scan0, y * plane.Stride), row.Length);
        }
        context.EncodeImage(image, encoder, new HeifEncodingOptions { ImageOrientation = HeifOrientation.Normal });
        context.WriteToFile(path);
    }

    private static void CheckPortableModuleOrigins(List<string> checks)
    {
        var applicationDirectory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(AppContext.BaseDirectory));
        using var process = Process.GetCurrentProcess();
        var modules = process.Modules.Cast<ProcessModule>().ToArray();
        try
        {
            foreach (var name in new[] { "msvcp140.dll", "msvcp140_1.dll", "vcruntime140.dll", "vcruntime140_1.dll", "libheif.dll", "onnxruntime.dll", "DirectML.dll" })
            {
                var matches = modules.Where(module => string.Equals(module.ModuleName, name, StringComparison.OrdinalIgnoreCase)).ToArray();
                RequireSmoke(matches.Length > 0, $"portable 原生模块未实际加载：{name}; base={applicationDirectory}");
                foreach (var module in matches)
                {
                    var path = Path.GetFullPath(module.FileName);
                    var directory = Path.TrimEndingDirectorySeparator(Path.GetDirectoryName(path)!);
                    RequireSmoke(string.Equals(directory, applicationDirectory, StringComparison.OrdinalIgnoreCase),
                        $"portable 原生模块不是应用本地来源：{name}; actual={path}; expected directory={applicationDirectory}");
                    var version = module.FileVersionInfo;
                    checks.Add($"PASS portable loaded module: {name}; path={path}; fileVersion={version.FileVersion}; productVersion={version.ProductVersion}");
                }
            }
        }
        finally
        {
            foreach (var module in modules) module.Dispose();
        }
    }
}
