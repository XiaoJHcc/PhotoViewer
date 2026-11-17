using PhotoViewer.Core.Design;
using PhotoViewer.Shared;

namespace CoreContractCheck;

/// <summary>
/// 宽形态控制栏流式排布：命令/星级/EXIF 分组尺寸（含无衬底变体）、整组换行、行长与交叉轴、
/// 文档尺寸、行内居中与完整分组矩形。
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

        var bareColumn = ToolbarFlowLayout.Exif(3, false, bare: true);
        c.True("无衬底纵排三项主轴 112、交叉轴取固有项宽 52",
            Near(bareColumn.Main, 112) && Near(bareColumn.Cross, DesignTokens.ToolbarExifItemWidth)
            && Near(bareColumn.Cross, 52),
            $"{bareColumn.Main}×{bareColumn.Cross}");
        var bareRow = ToolbarFlowLayout.Exif(3, true, bare: true);
        c.True("无衬底横排三项主轴 160、交叉轴 44",
            Near(bareRow.Main, 160) && Near(bareRow.Cross, 44),
            $"{bareRow.Main}×{bareRow.Cross}");
        var paddedColumn = ToolbarFlowLayout.Exif(3, false);
        c.True("默认有衬底纵排三项保持主轴 124、交叉轴 64",
            Near(paddedColumn.Main, 124) && Near(paddedColumn.Cross, 64),
            $"{paddedColumn.Main}×{paddedColumn.Cross}");
        var emptyBare = ToolbarFlowLayout.Exif(0, false, bare: true);
        c.True("无衬底变体无项时尺寸为零",
            Near(emptyBare.Main, 0) && Near(emptyBare.Cross, 0),
            $"{emptyBare.Main}×{emptyBare.Cross}");

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
        var joinedDoc = ToolbarFlowLayout.DocumentSize(
            [groups[0], groups[1]], true, [[0, 1]], crossLeadingPadding: 0);
        c.True("交叉轴前导内边距可与相邻工具区共边（44 + 0 + 10），主轴不变",
            Near(joinedDoc.Width, 402) && Near(joinedDoc.Height, 54),
            $"{joinedDoc.Width}×{joinedDoc.Height}");

        c.Equal("行在可用区内居中", 85d, ToolbarFlowLayout.LineStart(500, 330));
        c.Equal("可用区不足时退回外边距", 10d, ToolbarFlowLayout.LineStart(300, 330));
        c.Equal("内容主轴扣两端外边距", 80d, ToolbarFlowLayout.ContentMain(100));
        c.Equal("内容主轴在可用长度 0 时取 1", 1d, ToolbarFlowLayout.ContentMain(0));

        c.Section("控制栏完整流式排布");
        Arrangement(c);
    }

    private static void Arrangement(Checks c)
    {
        var lineGroups = new[]
        {
            ToolbarFlowLayout.Commands(3),
            ToolbarFlowLayout.Rating(500),
            ToolbarFlowLayout.Exif(3, true),
        };

        var exact = ToolbarFlowLayout.Arrange(lineGroups, true, 584);
        c.True("横排单行：文档 584×64、分组从外边距起排",
            Near(exact.Width, 584) && Near(exact.Height, 64)
            && FramesEqual(exact.Groups,
                new LayoutRect(10, 10, 148, 44),
                new LayoutRect(168, 10, 224, 44),
                new LayoutRect(402, 10, 172, 44)),
            Describe(exact));

        var centered = ToolbarFlowLayout.Arrange(lineGroups, true, 620);
        c.True("横排单行在可用区内居中、文档尺寸仍取内容",
            Near(centered.Width, 584) && Near(centered.Height, 64)
            && FramesEqual(centered.Groups,
                new LayoutRect(28, 10, 148, 44),
                new LayoutRect(186, 10, 224, 44),
                new LayoutRect(420, 10, 172, 44)),
            Describe(centered));

        var wrapped = ToolbarFlowLayout.Arrange(lineGroups, true, 420);
        c.True("横排两行：第二行另起并各自居中、行距 10",
            Near(wrapped.Width, 402) && Near(wrapped.Height, 118)
            && FramesEqual(wrapped.Groups,
                new LayoutRect(19, 10, 148, 44),
                new LayoutRect(177, 10, 224, 44),
                new LayoutRect(124, 64, 172, 44)),
            Describe(wrapped));

        var joined = ToolbarFlowLayout.Arrange(
            [ToolbarFlowLayout.Commands(2), ToolbarFlowLayout.Exif(2, false)], true, 212, crossLeadingPadding: 0);
        c.True("交叉轴前导为 0 时分组贴交叉轴起点、矩形拉伸到行高",
            Near(joined.Width, 212) && Near(joined.Height, 74)
            && FramesEqual(joined.Groups,
                new LayoutRect(10, 0, 96, 64),
                new LayoutRect(116, 0, 86, 64)),
            Describe(joined));

        var vertical = ToolbarFlowLayout.Arrange(lineGroups, false, 584);
        c.True("纵排单列：文档 64×584、分组自上而下",
            Near(vertical.Width, 64) && Near(vertical.Height, 584)
            && FramesEqual(vertical.Groups,
                new LayoutRect(10, 10, 44, 148),
                new LayoutRect(10, 168, 44, 224),
                new LayoutRect(10, 402, 44, 172)),
            Describe(vertical));

        var verticalWrapped = ToolbarFlowLayout.Arrange(
            [ToolbarFlowLayout.Commands(2), ToolbarFlowLayout.Exif(2, false)], false, 200);
        c.True("纵排两列：列宽取各自行高并逐列后移",
            Near(verticalWrapped.Width, 138) && Near(verticalWrapped.Height, 116)
            && FramesEqual(verticalWrapped.Groups,
                new LayoutRect(10, 52, 44, 96),
                new LayoutRect(64, 57, 64, 86)),
            Describe(verticalWrapped));

        var overflow = ToolbarFlowLayout.Arrange(
            [ToolbarFlowLayout.Commands(5), ToolbarFlowLayout.Commands(1)], true, 100);
        c.True("超长分组保持完整尺寸、文档按内容溢出、每行从外边距起排",
            Near(overflow.Width, 272) && Near(overflow.Height, 118)
            && FramesEqual(overflow.Groups,
                new LayoutRect(10, 10, 252, 44),
                new LayoutRect(28, 64, 44, 44)),
            Describe(overflow));

        var empty = ToolbarFlowLayout.Arrange([], true, 500);
        c.True("无分组时文档为纯外边距且无矩形",
            Near(empty.Width, 20) && Near(empty.Height, 20) && empty.Groups.Count == 0,
            Describe(empty));

        var commandsOnly = ToolbarFlowLayout.Arrange([ToolbarFlowLayout.Commands(2)], true, 116);
        c.True("仅命令组（无评分、无 EXIF）",
            Near(commandsOnly.Width, 116) && Near(commandsOnly.Height, 64)
            && FramesEqual(commandsOnly.Groups, new LayoutRect(10, 10, 96, 44)),
            Describe(commandsOnly));

        var ratingOnly = ToolbarFlowLayout.Arrange(
            [ToolbarFlowLayout.Rating(ToolbarFlowLayout.ContentMain(244))], true, 244);
        c.True("仅评分组（无 EXIF、无命令）",
            Near(ratingOnly.Width, 244) && Near(ratingOnly.Height, 64)
            && FramesEqual(ratingOnly.Groups, new LayoutRect(10, 10, 224, 44)),
            Describe(ratingOnly));

        var exifOnly = ToolbarFlowLayout.Arrange([ToolbarFlowLayout.Exif(2, false)], true, 106);
        c.True("仅 EXIF 组（无评分、无命令）",
            Near(exifOnly.Width, 106) && Near(exifOnly.Height, 84)
            && FramesEqual(exifOnly.Groups, new LayoutRect(10, 10, 86, 64)),
            Describe(exifOnly));
    }

    private static bool FramesEqual(IReadOnlyList<LayoutRect> actual, params LayoutRect[] expected)
    {
        if (actual.Count != expected.Length) return false;
        for (int index = 0; index < expected.Length; index++)
        {
            var frame = actual[index];
            var want = expected[index];
            if (!Near(frame.X, want.X) || !Near(frame.Y, want.Y)
                || !Near(frame.Width, want.Width) || !Near(frame.Height, want.Height)) return false;
        }
        return true;
    }

    private static string Describe(ToolbarFlowDocument document)
        => $"{document.Width}×{document.Height} "
            + string.Join(" ", document.Groups.Select(frame => $"({frame.X},{frame.Y},{frame.Width},{frame.Height})"));

    private static bool Near(double first, double second) => Math.Abs(first - second) <= Epsilon;

    private static string Describe(List<List<int>> lines)
        => string.Join(" | ", lines.Select(line => "[" + string.Join(",", line) + "]"));
}
