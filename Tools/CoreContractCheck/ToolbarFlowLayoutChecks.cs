using PhotoViewer.Shared;

namespace CoreContractCheck;

/// <summary>
/// 宽形态控制栏流式排布：命令/星级/EXIF 分组尺寸、整组换行、行长与交叉轴、文档尺寸与行内居中。
/// </summary>
internal static class ToolbarFlowLayoutChecks
{
    private const double Epsilon = 1e-6;

    public static void Run(Checks c)
    {
        c.Section("控制栏流式排布");
        c.Equal("文档外边距 10", 10d, ToolbarFlowLayout.Padding);
        c.Equal("分组间距 10", 10d, ToolbarFlowLayout.GroupSpacing);
        c.Equal("命令按钮 44", 44d, ToolbarFlowLayout.CommandSize);
        c.Equal("命令按钮间距 8", 8d, ToolbarFlowLayout.CommandSpacing);

        var commandLengths = new[] { 1, 2, 3, 4, 5 };
        var expectedLengths = new[] { 44d, 96d, 148d, 200d, 252d };
        var lengthsMatch = true;
        for (int index = 0; index < commandLengths.Length; index++)
        {
            var group = ToolbarFlowLayout.Commands(commandLengths[index]);
            lengthsMatch &= Near(group.Main, expectedLengths[index])
                && Near(group.Cross, 44)
                && group.Kind == ControlBarGroupKind.Command;
        }
        c.True("命令分组 1–5 个按钮的主轴长度与交叉轴", lengthsMatch);
        c.Equal("空命令分组长度为 0", 0d, ToolbarFlowLayout.Commands(0).Main);
        c.Equal("首个按钮偏移 0", 0d, ToolbarFlowLayout.CommandOffset(0));
        c.Equal("第三个按钮偏移 104", 104d, ToolbarFlowLayout.CommandOffset(2));

        var expandedRating = ToolbarFlowLayout.Rating(224);
        c.True("可用主轴 224 时星级组按展开态占位",
            Near(expandedRating.Main, 224) && Near(expandedRating.Cross, 44)
            && expandedRating.Kind == ControlBarGroupKind.Rating);
        var compactRating = ToolbarFlowLayout.Rating(223.99);
        c.True("可用主轴不足时星级组按紧凑态最短长度占位",
            Near(compactRating.Main, 88) && Near(compactRating.Cross, 44));
        c.True("可用主轴 229 时星级组展开", Near(ToolbarFlowLayout.Rating(229).Main, 224));
        c.True("可用主轴 133 时星级组紧凑", Near(ToolbarFlowLayout.Rating(133).Main, 88));

        var exifHorizontal = ToolbarFlowLayout.Exif(3, true);
        c.True("横排 EXIF 三项主轴 172、交叉轴 44",
            Near(exifHorizontal.Main, 172) && Near(exifHorizontal.Cross, 44)
            && exifHorizontal.Kind == ControlBarGroupKind.Exif,
            $"{exifHorizontal.Main}×{exifHorizontal.Cross}");
        var exifVertical = ToolbarFlowLayout.Exif(2, false);
        c.True("竖排 EXIF 两项主轴 86、交叉轴 64",
            Near(exifVertical.Main, 86) && Near(exifVertical.Cross, 64),
            $"{exifVertical.Main}×{exifVertical.Cross}");

        var exifMatchesBlock = true;
        for (int count = 0; count <= 7; count++)
        foreach (bool horizontal in new[] { false, true })
        {
            var block = ExifBlockGeometry.Size(count, horizontal);
            var group = ToolbarFlowLayout.Exif(count, horizontal);
            exifMatchesBlock &= Near(group.Main, horizontal ? block.Width : block.Height)
                && Near(group.Cross, horizontal ? block.Height : block.Width);
        }
        c.True("EXIF 分组尺寸与 EXIF 块几何一致（0–7 项、两向）", exifMatchesBlock);

        var groups = new[]
        {
            ToolbarFlowLayout.Commands(3),
            ToolbarFlowLayout.Rating(500),
            ToolbarFlowLayout.Exif(3, true),
        };
        var lines = ToolbarFlowLayout.PackLines(groups, 500);
        c.True("整组换行：第三组放不下时另起一行",
            lines.Count == 2 && lines[0].SequenceEqual([0, 1]) && lines[1].SequenceEqual([2]),
            Describe(lines));

        var exactFit = ToolbarFlowLayout.PackLines([groups[0], groups[1]], 382);
        c.True("恰好等宽时不换行", exactFit.Count == 1 && exactFit[0].Count == 2, Describe(exactFit));

        var overLong = ToolbarFlowLayout.PackLines(
            [ToolbarFlowLayout.Commands(5), ToolbarFlowLayout.Commands(1)], 100);
        c.True("超长分组独占一行且不被拆开",
            overLong.Count == 2 && overLong[0].SequenceEqual([0]) && overLong[1].SequenceEqual([1]),
            Describe(overLong));

        c.Equal("单行主轴长度为组宽加间距", 382d, ToolbarFlowLayout.LineLength(groups, [0, 1]));
        c.Equal("单行交叉轴取最高组", 44d, ToolbarFlowLayout.LineCross(groups, [0, 1]));

        var horizontalDoc = ToolbarFlowLayout.DocumentSize([groups[0], groups[1]], true, [[0, 1]]);
        c.True("横排文档尺寸含四周外边距",
            Near(horizontalDoc.Width, 402) && Near(horizontalDoc.Height, 64),
            $"{horizontalDoc.Width}×{horizontalDoc.Height}");
        var verticalDoc = ToolbarFlowLayout.DocumentSize([groups[0], groups[1]], false, [[0, 1]]);
        c.True("竖排文档尺寸交换宽高",
            Near(verticalDoc.Width, 64) && Near(verticalDoc.Height, 402),
            $"{verticalDoc.Width}×{verticalDoc.Height}");
        var emptyDoc = ToolbarFlowLayout.DocumentSize([], true, []);
        c.True("空文档保留纯外边距", Near(emptyDoc.Width, 20) && Near(emptyDoc.Height, 20),
            $"{emptyDoc.Width}×{emptyDoc.Height}");

        c.Equal("行在可用区内居中", 85d, ToolbarFlowLayout.LineStart(500, 330));
        c.Equal("可用区不足时退回外边距", 10d, ToolbarFlowLayout.LineStart(300, 330));
    }

    private static bool Near(double first, double second) => Math.Abs(first - second) <= Epsilon;

    private static string Describe(List<List<int>> lines)
        => string.Join(" | ", lines.Select(line => "[" + string.Join(",", line) + "]"));
}
