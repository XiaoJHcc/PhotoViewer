using System.Diagnostics;
using System.Runtime.InteropServices;
using PhotoViewer.Core.AI;
using PhotoViewer.Core.Color;
using PhotoViewer.Core.Image;
using PhotoViewer.iOS.Core;

namespace PhotoViewer.iOS.Native;

/// <summary>
/// 验收专用（非产品行为，测试钩子）：<c>--pv-dino-check &lt;文件&gt;</c> 对给定图片执行一次
/// <see cref="DinoFeatureExtractor.ExtractAsync"/>，打印 CLS 维度、实际生效的 Execution Provider
/// （CoreML 注册成功 / 回退 CPU）与推理耗时，并写 Documents/PhotoViewer/dino-check.txt。
/// 用于 iOS 原生库打包（onnxruntime 静态链接）与 CoreML EP 的真机/模拟器核验；产品路径不调用。
/// </summary>
internal static class iOSDinoCheck
{
    /// <summary>验收开关。</summary>
    private const string Switch = "--pv-dino-check";

    /// <summary>解析验收开关；命中即以给定文件启动异步推理核验。</summary>
    /// <param name="args">进程启动参数。</param>
    public static void MaybeStart(string[] args)
    {
        string? path = null;
        for (int index = 0; index + 1 < args.Length; index++)
        {
            if (string.Equals(args[index], Switch, StringComparison.Ordinal))
            {
                path = args[index + 1];
            }
        }

        if (path == null) return;
        _ = RunAsync(ResolveDocumentsPath(path));
    }

    /// <summary>相对路径按沙盒 Documents 解析（iOS 无启动工作目录语义，与壳层 --pv-open 同一口径）。</summary>
    /// <param name="path">绝对或相对路径。</param>
    /// <returns>绝对路径。</returns>
    private static string ResolveDocumentsPath(string path)
        => Path.IsPathRooted(path)
            ? path
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), path);

    /// <summary>解码一次、推理一次并记录结果；失败时明确记录异常类型与消息，不静默回退。</summary>
    /// <param name="path">图片绝对路径。</param>
    private static async Task RunAsync(string path)
    {
        string report;
        try
        {
            if (!File.Exists(path)) throw new FileNotFoundException("图片不存在", path);

            using var image = await BitmapLoader.GetBitmapAsync(
                new iOSFileRef(path), DecodePriority.P0_Current, CancellationToken.None).ConfigureAwait(false)
                ?? throw new InvalidOperationException("解码失败");

            var mainProgram = NativeLibrary.GetMainProgramHandle();
            bool export = NativeLibrary.TryGetExport(mainProgram, "OrtGetApiBase", out _);
            Console.WriteLine($"[DinoCheck] mainProgram={mainProgram} OrtGetApiBase={export}");

            // 主图链路的产品解码产物是 RGBA16F；DINO 输入契约是 8 位 sRGB（索引器走缩略图 BGRA8），
            // 这里按同一口径先转 sRGB8 再做 518 拉伸。
            using var srgb = ColorEngine.ToSrgb8(image);
            var stopwatch = Stopwatch.StartNew();
            var cls = await DinoFeatureExtractor.ExtractAsync(srgb).ConfigureAwait(false);
            stopwatch.Stop();

            var provider = DinoFeatureExtractor.UsesGpuExecutionProvider ? "CoreML" : "CPU";
            report = $"PASS cls={cls.Length} ep={provider} ms={stopwatch.Elapsed.TotalMilliseconds:0.0} " +
                     $"size={image.Width}x{image.Height} file={Path.GetFileName(path)}";
        }
        catch (Exception exception)
        {
            report = $"FAIL {exception}";
        }

        Console.WriteLine($"[DinoCheck] {report}");
        try
        {
            var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "PhotoViewer");
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, "dino-check.txt"), report + Environment.NewLine);
        }
        catch (Exception exception)
        {
            Console.WriteLine($"[DinoCheck] 报告写入失败: {exception.Message}");
        }
    }
}
