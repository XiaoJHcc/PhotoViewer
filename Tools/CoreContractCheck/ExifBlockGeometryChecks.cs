using PhotoViewer.Shared;

namespace CoreContractCheck;

/// <summary>
/// TOOLBAR-05：EXIF 块的项尺寸、内边距、整块尺寸，以及固有长度与槽位挤压下的逐项矩形。
/// </summary>
internal static class ExifBlockGeometryChecks
{
    private const double Epsilon = 1e-6;

    public static void Run(Checks c)
    {
        c.Section("EXIF 块尺寸与逐项矩形");
        c.True("EXIF 项 52 宽 / 36 高 / 2 间距 / 6 内边距",
            Near(ExifBlockGeometry.ItemWidth, 52) && Near(ExifBlockGeometry.ItemHeight, 36)
            && Near(ExifBlockGeometry.ItemSpacing, 2) && Near(ExifBlockGeometry.Padding, 6),
            $"{ExifBlockGeometry.ItemWidth}×{ExifBlockGeometry.ItemHeight}，"
            + $"间距 {ExifBlockGeometry.ItemSpacing}，内边距 {ExifBlockGeometry.Padding}");
        c.Equal("EXIF 块交叉轴取玻璃按钮边长 44", 44d, ExifBlockGeometry.Thickness);
        c.Equal("EXIF 块圆角 12", 12d, ExifBlockGeometry.CornerRadius);

        var emptyRow = ExifBlockGeometry.Size(0, true);
        var emptyColumn = ExifBlockGeometry.Size(0, false);
        c.True("无项时两向尺寸都为零",
            Near(emptyRow.Width, 0) && Near(emptyRow.Height, 0)
            && Near(emptyColumn.Width, 0) && Near(emptyColumn.Height, 0),
            $"{emptyRow.Width}×{emptyRow.Height} / {emptyColumn.Width}×{emptyColumn.Height}");

        var horizontalSizes = new (int Count, double Width, double Height)[] { (1, 64, 44), (4, 226, 44), (7, 388, 44) };
        bool horizontalOk = true;
        foreach (var expected in horizontalSizes)
        {
            var size = ExifBlockGeometry.Size(expected.Count, true);
            horizontalOk &= Near(size.Width, expected.Width) && Near(size.Height, expected.Height);
        }
        c.True("横排 1/4/7 项尺寸 64/226/388 × 44", horizontalOk);

        var verticalSizes = new (int Count, double Width, double Height)[] { (1, 64, 48), (4, 64, 162), (7, 64, 276) };
        bool verticalOk = true;
        foreach (var expected in verticalSizes)
        {
            var size = ExifBlockGeometry.Size(expected.Count, false);
            verticalOk &= Near(size.Width, expected.Width) && Near(size.Height, expected.Height);
        }
        c.True("竖排 1/4/7 项尺寸 64 × 48/162/276", verticalOk);

        var rowRects = new List<string>();
        bool rowIntrinsic = true;
        for (int index = 0; index < 4; index++)
        {
            var rect = ExifBlockGeometry.ItemRect(index, 4, true, 226);
            rowIntrinsic &= Near(rect.X, 6 + index * 54) && Near(rect.Width, 52)
                && Near(rect.Y, 0) && Near(rect.Height, 44);
            rowRects.Add(Rect(rect));
        }
        c.True("横排固有长度 226 时 4 项从内边距起排、间距 2、贴底内边距", rowIntrinsic, string.Join(" ", rowRects));
        var rowLast = ExifBlockGeometry.ItemRect(3, 4, true, 226);
        c.True("横排末项右边贴住 226 − 6", Near(rowLast.MaxX, 220), rowLast.MaxX.ToString("0.##"));

        var columnRects = new List<string>();
        bool columnIntrinsic = true;
        for (int index = 0; index < 4; index++)
        {
            var rect = ExifBlockGeometry.ItemRect(index, 4, false, 162);
            columnIntrinsic &= Near(rect.Y, 6 + index * 38) && Near(rect.Height, 36)
                && Near(rect.X, 6) && Near(rect.Width, 52);
            columnRects.Add(Rect(rect));
        }
        c.True("竖排固有长度 162 时 4 项从内边距起排、间距 2、贴底内边距", columnIntrinsic,
            string.Join(" ", columnRects));
        var columnLast = ExifBlockGeometry.ItemRect(3, 4, false, 162);
        c.True("竖排末项底边贴住 162 − 6", Near(columnLast.MaxY, 156), columnLast.MaxY.ToString("0.##"));

        var squeezed = ExifBlockGeometry.ItemRect(1, 4, true, 150);
        var squeezedLast = ExifBlockGeometry.ItemRect(3, 4, true, 150);
        c.True("横排挤压到 150 时每项等量收窄到 33",
            Near(squeezed.X, 41) && Near(squeezed.Width, 33), Rect(squeezed));
        c.True("横排挤压后末项仍贴住 150 − 6", Near(squeezedLast.MaxX, 144), squeezedLast.MaxX.ToString("0.##"));

        var squeezedColumn = ExifBlockGeometry.ItemRect(0, 4, false, 100);
        var squeezedColumnLast = ExifBlockGeometry.ItemRect(3, 4, false, 100);
        c.True("竖排挤压到 100 时每项等量收窄到 20.5",
            Near(squeezedColumn.Y, 6) && Near(squeezedColumn.Height, 20.5), Rect(squeezedColumn));
        c.True("竖排挤压后末项仍贴住 100 − 6", Near(squeezedColumnLast.MaxY, 94),
            squeezedColumnLast.MaxY.ToString("0.##"));

        bool neverNegative = true;
        for (int index = 0; index < 3; index++)
        {
            var rect = ExifBlockGeometry.ItemRect(index, 3, true, 10);
            neverNegative &= rect.Width >= 0 && rect.X >= 0;
        }
        c.True("空间不足时项目宽不为负且不退到内边距之前", neverNegative);

        var capped = ExifBlockGeometry.ItemRect(2, 4, true, 400);
        c.True("块长于固有长度时项目不拉伸、保持 52 宽并留在内边距内",
            Near(capped.Width, 52) && Near(capped.X, 114), Rect(capped));

        var outOfRange = ExifBlockGeometry.ItemRect(4, 4, true, 226);
        var negative = ExifBlockGeometry.ItemRect(-1, 4, true, 226);
        var noItems = ExifBlockGeometry.ItemRect(0, 0, true, 226);
        c.True("索引越界或无项时返回零矩形",
            Near(outOfRange.Width, 0) && Near(negative.Width, 0) && Near(noItems.Width, 0),
            $"{Rect(outOfRange)} {Rect(negative)} {Rect(noItems)}");

        var groupHorizontal = ToolbarFlowLayout.Exif(4, true);
        var groupVertical = ToolbarFlowLayout.Exif(4, false);
        c.True("工具栏 EXIF 分组取 226×44 / 64×162",
            Near(groupHorizontal.Main, 226) && Near(groupHorizontal.Cross, 44)
            && Near(groupVertical.Main, 162) && Near(groupVertical.Cross, 64),
            $"{groupHorizontal.Main}×{groupHorizontal.Cross} / {groupVertical.Main}×{groupVertical.Cross}");
    }

    private static bool Near(double first, double second) => Math.Abs(first - second) <= Epsilon;

    private static string Rect(LayoutRect rect)
        => $"({rect.X:0.##},{rect.Y:0.##},{rect.Width:0.##}×{rect.Height:0.##})";
}
