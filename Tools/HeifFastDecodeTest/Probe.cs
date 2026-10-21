using PhotoViewer.Windows.Core.Heif;

namespace HeifFastDecodeTest;

/// <summary>诊断:逐点对照自有转换与 libheif 参考,并按平移量搜索最佳对齐。</summary>
internal static class Probe
{
    public static int Run(string[] args)
    {
        if (args.Length < 1)
        {
            Console.Error.WriteLine("usage: probe <file>");
            return 2;
        }

        var data = File.ReadAllBytes(args[0]);
        var container = HeifContainer.TryParse(data, out var reason);
        if (container is null)
        {
            Console.Error.WriteLine("parse failed: " + reason);
            return 1;
        }
        Console.WriteLine($"ours {container.Width}x{container.Height} rot={container.ExifOrientation} "
            + $"nclx=({container.Color.Primaries},{container.Color.Transfer},{container.Color.Matrix},"
            + $"{(container.Color.FullRange ? "full" : "limited")})");

        var reference = Reference.DecodeRgb24(data, true);
        if (reference is null)
        {
            Console.Error.WriteLine("libheif failed");
            return 1;
        }
        Console.WriteLine($"libheif {reference.Width}x{reference.Height} stride={reference.Stride}");
        var referenceBgra = Reference.RgbToBgra(reference);

        var tiles = TileDecodeHarness.DecodeAll(container, Path.Combine(Path.GetTempPath(), "HeifProbe"), true, TileDecodeHarness.Preferred);
        if (tiles is null) return 1;
        var yuvTiles = new YuvTile[tiles.Length];
        for (var i = 0; i < tiles.Length; i++)
        {
            var tile = container.Tiles[i];
            yuvTiles[i] = tiles[i].ToTile(tile.PixelX, tile.PixelY, tile.Width, tile.Height);
        }
        Console.WriteLine("tiles: " + TileDecodeHarness.Describe(tiles));

        var source = new YuvImage
        {
            Tiles = yuvTiles,
            CanvasWidth = container.Width,
            CanvasHeight = container.Height,
            BitDepth = tiles[0].BitDepth,
            Chroma = tiles[0].Chroma,
            MatrixCode = container.Color.HasNclx ? container.Color.Matrix : 5,
            PrimariesCode = container.Color.HasNclx ? container.Color.Primaries : 1,
            TransferCode = container.Color.HasNclx ? container.Color.Transfer : 13,
            FullRange = !container.Color.HasNclx || container.Color.FullRange,
        };

        var mine = new byte[container.Width * container.Height * 4];
        if (!YuvImageConverter.TryConvertToBgra32(source, container.Width, container.Height, mine, container.Width * 4, out var rejected))
        {
            Console.Error.WriteLine("rejected: " + rejected);
            return 1;
        }

        // 采样点对照
        var halfWidth = container.Width / 2;
        var points = new (int X, int Y)[]
        {
            (10, 10),
            (Math.Min(1000, container.Width - 1), Math.Min(100, container.Height - 1)),
            (halfWidth, container.Height / 4),
            (Math.Min(halfWidth + 1, container.Width - 1), container.Height / 4),
            (Math.Min(halfWidth + 30, container.Width - 1), container.Height / 4),
            (container.Width - 8, container.Height - 72),
            (Math.Min(3500, container.Width - 1), Math.Min(1590, container.Height - 1)),
        };
        foreach (var (x, y) in points)
        {
            var i = ((y * container.Width) + x) * 4;
            Console.WriteLine(
                $"  ({x},{y}) mine BGR=({mine[i]},{mine[i + 1]},{mine[i + 2]}) "
                + $"ref=({referenceBgra[i]},{referenceBgra[i + 1]},{referenceBgra[i + 2]})");
        }

        // 平移搜索
        var best = (Dx: 0, Dy: 0, Mean: double.MaxValue);
        for (var dy = -2; dy <= 2; dy++)
        {
            for (var dx = -2; dx <= 2; dx++)
            {
                double sum = 0;
                long count = 0;
                for (var y = 100; y < 300; y++)
                {
                    for (var x = 1000; x < 1200; x++)
                    {
                        var sx = x + dx;
                        var sy = y + dy;
                        if (sx < 0 || sy < 0 || sx >= container.Width || sy >= container.Height) continue;
                        var a = ((y * container.Width) + x) * 4;
                        var b = ((sy * container.Width) + sx) * 4;
                        for (var c = 0; c < 3; c++) sum += Math.Abs(mine[a + c] - referenceBgra[b + c]);
                        count += 3;
                    }
                }
                var mean = sum / count;
                if (mean < best.Mean) best = (dx, dy, mean);
            }
        }
        Console.WriteLine($"best shift dx={best.Dx} dy={best.Dy} mean={best.Mean:F3}");

        foreach (var tile in tiles) tile.Dispose();
        var probeTemp = Path.Combine(Path.GetTempPath(), "HeifProbe");
        if (Directory.Exists(probeTemp)) Directory.Delete(probeTemp, true);
        return 0;
    }
}
