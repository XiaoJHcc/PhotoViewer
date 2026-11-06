using PhotoViewer.Core.Design;
using PhotoViewer.Shared;

namespace CoreContractCheck;

/// <summary>
/// RATING-04：来源浮窗宽度、单行/双行条目、信息行靠右、展开方向、边界夹取与中部省略。
/// </summary>
internal static class RatingSourcesLayoutChecks
{
    public static void Run(Checks c)
    {
        c.Section("评分来源浮窗几何");
        c.Equal("槽位数为六个", 6, RatingSourcesLayout.SlotCount);
        c.Equal("星槽取控制栏评分组宽", DesignTokens.ToolbarRatingButtonSize, RatingSourcesLayout.SlotSize);
        c.Equal("条目宽 196", 196d, RatingSourcesLayout.EntryWidth);
        c.Equal("条目内容宽 180", 180d, RatingSourcesLayout.EntryContentWidth);
        c.Equal("浮窗内容宽 204", 204d, RatingSourcesLayout.ContentWidth);

        // 短条目：文件名 + 来源标签(含两侧内边距) + 状态同行。
        var shortEntry = RatingSourcesLayout.MeasureEntry(80, 12 + DesignTokens.PopupRatingSourcesBadgePaddingX * 2, 20);
        c.True("短文件名保持单行", !shortEntry.TwoLines);
        c.Equal("单行条目高", 68d, shortEntry.Height);
        c.Equal("单行文件名让出右侧信息宽", 124d, shortEntry.NameWidth);
        c.Equal("状态贴右内边距", 196 - DesignTokens.PopupRatingSourcesEntryPadding, shortEntry.StatusX + shortEntry.StatusWidth);
        c.Equal("来源标签在状态左侧留标签间距",
            shortEntry.StatusX - DesignTokens.PopupRatingSourcesLabelGap, shortEntry.BadgeX + shortEntry.BadgeWidth);
        c.Equal("星排顶边", 30d, shortEntry.StarsY);

        var longEntry = RatingSourcesLayout.MeasureEntry(400, 30, 20);
        c.True("长文件名改为双行", longEntry.TwoLines);
        c.Equal("双行文件名占满内容宽", RatingSourcesLayout.EntryContentWidth, longEntry.NameWidth);
        c.Equal("双行条目高", 88d, longEntry.Height);
        c.Equal("双行信息行下移", 8 + RatingSourcesLayout.InfoHeight + DesignTokens.PopupRatingSourcesMetaLineGap, longEntry.MetaY);
        c.Equal("双行星排顶边", 50d, longEntry.StarsY);

        var clamped = RatingSourcesLayout.MeasureEntry(10, 150, 100);
        c.Equal("状态宽超出时夹进可用宽度", 24d, clamped.StatusWidth);
        c.True("标签与状态仍靠右且不越界",
            clamped.StatusX + clamped.StatusWidth <= 196 - DesignTokens.PopupRatingSourcesEntryPadding + .001
            && clamped.BadgeX >= DesignTokens.PopupRatingSourcesEntryPadding - .001);

        c.Equal("两条目内容高", 172d,
            RatingSourcesLayout.ContentHeight([shortEntry, longEntry]));
        c.Equal("空条目不撑高", 8d, RatingSourcesLayout.ContentHeight([]));

        c.Section("评分来源浮窗展开方向与落位");
        c.True("横排控制栏向上", RatingSourcesLayout.ResolveDirection(
            RatingSourcesAnchorKind.Control, true, 500, 500, 100) == RatingSourcesDirection.Up);
        c.True("竖排控制栏向左", RatingSourcesLayout.ResolveDirection(
            RatingSourcesAnchorKind.Control, false, 500, 500, 100) == RatingSourcesDirection.Left);
        c.True("卡片下方够放时向下", RatingSourcesLayout.ResolveDirection(
            RatingSourcesAnchorKind.Card, true, 500, 500, 100) == RatingSourcesDirection.Down);
        c.True("卡片下方不足且上方够放时向上", RatingSourcesLayout.ResolveDirection(
            RatingSourcesAnchorKind.Card, true, 500, 100, 100) == RatingSourcesDirection.Up);
        c.True("上下都不足时保留向下", RatingSourcesLayout.ResolveDirection(
            RatingSourcesAnchorKind.Card, true, 50, 50, 100) == RatingSourcesDirection.Down);

        var anchor = new LayoutRect(100, 100, 20, 20);
        var bounds = new LayoutRect(0, 0, 500, 500);
        var down = RatingSourcesLayout.Place(anchor, bounds, 200, 100, RatingSourcesDirection.Down);
        c.True("向下居中并对齐箭头余量", Math.Abs(down.X - 10) < .001 && Math.Abs(down.Y - 132) < .001);
        var up = RatingSourcesLayout.Place(anchor, bounds, 200, 100, RatingSourcesDirection.Up);
        c.True("向上时越界夹到边界起点", Math.Abs(up.X - 10) < .001 && Math.Abs(up.Y) < .001);
        var left = RatingSourcesLayout.Place(anchor, bounds, 200, 100, RatingSourcesDirection.Left);
        c.True("向左时越界夹到边界起点", Math.Abs(left.X) < .001 && Math.Abs(left.Y - 60) < .001);
        var clampedRight = RatingSourcesLayout.Place(new LayoutRect(490, 480, 10, 10), bounds, 200, 100,
            RatingSourcesDirection.Down);
        c.True("右下越界时贴住边界", Math.Abs(clampedRight.X - 300) < .001 && Math.Abs(clampedRight.Y - 400) < .001);

        c.Section("评分来源浮窗中部省略");
        double Measure(string text) => text.Length * 10;
        c.Equal("放得下时原样返回", "abcdef", RatingSourcesLayout.EllipsizeMiddle("abcdef", 60, Measure));
        var ellipsized = RatingSourcesLayout.EllipsizeMiddle("abcdefghij.jpg", 70, Measure);
        c.True("超宽时保留首尾并插入省略号",
            ellipsized == "abc…jpg" && ellipsized.StartsWith("abc", StringComparison.Ordinal)
            && ellipsized.EndsWith("jpg", StringComparison.Ordinal), ellipsized);
        c.True("保留字数二分取最大可容纳值", Measure(ellipsized) <= 70);
    }
}
