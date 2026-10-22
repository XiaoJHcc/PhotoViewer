using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using PhotoViewer.Core.Image.Heif;

namespace HeifFastDecodeTest;

/// <summary>
/// HEIF 快速解码验证工具入口:容器解析自检、解析结果转储、YUV 转换数值/性能验收。
/// </summary>
internal static partial class Program
{
    private static int Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        if (args.Length == 0)
        {
            Console.Error.WriteLine("usage: HeifFastDecodeTest <selftest|smoke|parse|extract|validate|bench> [args]");
            return 2;
        }

        var command = args[0];
        var rest = args[1..];
        try
        {
            switch (command)
            {
                case "selftest":
                    return SelfTest.Run();
                case "smoke":
                    return Smoke.Run();
                case "probe":
                    return Probe.Run(rest);
                case "parse":
                    return ParseCommand(rest);
                case "extract":
                    return ExtractCommand(rest);
                case "validate":
                    return Validation.Run(rest);
                case "bench":
                    return Bench.Run(rest);
                default:
                    Console.Error.WriteLine("unknown command: " + command);
                    return 2;
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.ToString());
            return 1;
        }
    }

    private static int ParseCommand(string[] paths)
    {
        var files = Expand(paths);
        Console.WriteLine($"files: {files.Count}");
        var ok = 0;
        var unsupported = 0;
        var failed = 0;
        foreach (var file in files)
        {
            var data = File.ReadAllBytes(file);
            var container = HeifContainer.TryParse(data, out var reason);
            var name = Path.GetFileName(file);
            if (container is null)
            {
                if (reason is not null && reason.Contains("不受支持", StringComparison.Ordinal))
                {
                    unsupported++;
                    Console.WriteLine($"{name,-24} UNSUPPORTED  {reason}");
                }
                else
                {
                    failed++;
                    Console.WriteLine($"{name,-24} FAILED       {reason}");
                }
                continue;
            }

            ok++;
            var anneBytes = 0L;
            for (var i = 0; i < container.Tiles.Count; i++) anneBytes += container.Tiles[i].AnnexB.Length;
            Console.WriteLine(
                $"{name,-24} ok {container.Kind,-11} {container.Width}x{container.Height} "
                + $"grid={container.Rows}x{container.Columns} tile={container.TileWidth}x{container.TileHeight} "
                + $"tiles={container.Tiles.Count} annexb={anneBytes / 1024}KB "
                + $"rot={container.ExifOrientation} "
                + $"nclx=({container.Color.Primaries},{container.Color.Transfer},{container.Color.Matrix},"
                + $"{(container.Color.FullRange ? "full" : "limited")}) "
                + $"bits={container.PixelInfo.LumaBits}/{container.PixelInfo.ChromaBits} "
                + $"chroma={container.ChromaFormat} thmbs={container.Thumbnails.Count} "
                + $"aux={container.HasAuxiliaryImages} "
                + $"thumb>=560:{(container.FindThumbnail(560) is { } t ? t.Width + "x" + t.Height : "none")}"
                + $" thumb>=1664:{(container.FindThumbnail(1664) is { } u ? u.Width + "x" + u.Height : "none")}");
        }

        Console.WriteLine($"ok={ok} unsupported={unsupported} failed={failed}");
        return failed == 0 ? 0 : 1;
    }

    private static int ExtractCommand(string[] args)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine("usage: extract <outdir> <paths...>");
            return 2;
        }

        var outDir = Path.GetFullPath(args[0]);
        Directory.CreateDirectory(outDir);
        var files = Expand(args[1..]);

        foreach (var file in files)
        {
            var stem = Path.GetFileNameWithoutExtension(file);
            var data = File.ReadAllBytes(file);
            var container = HeifContainer.TryParse(data, out var reason);
            if (container is null)
            {
                Console.WriteLine($"{stem}: {reason}");
                continue;
            }

            var dir = Path.Combine(outDir, stem + "_" + ShortHash(data));
            Directory.CreateDirectory(dir);
            var tiles = new List<object>();
            for (var i = 0; i < container.Tiles.Count; i++)
            {
                var tile = container.Tiles[i];
                var fileName = $"tile{i}.hevc";
                File.WriteAllBytes(Path.Combine(dir, fileName), tile.AnnexB);
                tiles.Add(new
                {
                    index = i,
                    column = tile.Column,
                    row = tile.Row,
                    width = tile.Width,
                    height = tile.Height,
                    bytes = tile.AnnexB.Length,
                    sha256 = Convert.ToHexString(SHA256.HashData(tile.AnnexB)),
                });
            }

            var info = new
            {
                source = file,
                primaryItemId = container.PrimaryItemId,
                kind = container.Kind.ToString(),
                width = container.Width,
                height = container.Height,
                columns = container.Columns,
                rows = container.Rows,
                tileWidth = container.TileWidth,
                tileHeight = container.TileHeight,
                exifOrientation = container.ExifOrientation,
                transforms = container.Transforms.Select(static t => t.ToString()).ToArray(),
                primaries = container.Color.Primaries,
                transfer = container.Color.Transfer,
                matrix = container.Color.Matrix,
                fullRange = container.Color.FullRange,
                lumaBits = container.PixelInfo.LumaBits,
                chromaBits = container.PixelInfo.ChromaBits,
                chromaFormat = container.ChromaFormat,
                thumbnails = container.Thumbnails.Select(static t => new { t.ItemId, t.Width, t.Height, bytes = t.AnnexB.Length }).ToArray(),
                tiles,
            };
            File.WriteAllText(
                Path.Combine(dir, "info.json"),
                JsonSerializer.Serialize(info, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine($"{stem}: {container.Width}x{container.Height} tiles={container.Tiles.Count} -> {dir}");
        }

        return 0;
    }

    private static string ShortHash(byte[] data)
        => Convert.ToHexString(SHA256.HashData(data))[..8].ToLowerInvariant();

    private static List<string> Expand(string[] paths)
    {
        var result = new List<string>();
        foreach (var path in paths)
        {
            var full = Path.GetFullPath(path);
            if (Directory.Exists(full))
            {
                foreach (var candidate in Directory.EnumerateFiles(full))
                {
                    var extension = Path.GetExtension(candidate);
                    if (extension.Equals(".hif", StringComparison.OrdinalIgnoreCase)
                        || extension.Equals(".heic", StringComparison.OrdinalIgnoreCase)
                        || extension.Equals(".heif", StringComparison.OrdinalIgnoreCase)
                        || extension.Equals(".avif", StringComparison.OrdinalIgnoreCase))
                    {
                        result.Add(candidate);
                    }
                }
            }
            else if (File.Exists(full))
            {
                result.Add(full);
            }
            else
            {
                Console.Error.WriteLine("missing path: " + path);
            }
        }

        result.Sort(StringComparer.OrdinalIgnoreCase);
        return result;
    }
}
