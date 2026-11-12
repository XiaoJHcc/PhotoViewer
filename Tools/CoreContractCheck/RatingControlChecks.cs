using PhotoViewer.Core.Design;
using PhotoViewer.Shared;

namespace CoreContractCheck;

/// <summary>
/// RATING-07：星级控件的形态判定、槽位内矩形、六格星排的槽位与反向映射、紧凑星簇、
/// 弹窗方向表与落位、一次手势最多一次提交，以及手机竖屏槽位的端到端一致性。
/// </summary>
internal static class RatingControlChecks
{
    private const double Epsilon = 1e-6;

    public static void Run(Checks c)
    {
        RunControlGeometry(c);
        RunSlotMapping(c);
        RunCompactCluster(c);
        RunPopupDirection(c);
        RunPopupPlacement(c);
        RunExternalInBand(c);
        RunGesture(c);
        RunPopupLayout(c);
        RunIntegration(c);
    }

    private static void RunControlGeometry(Checks c)
    {
        c.Section("星级控件形态与槽位矩形");
        c.Equal("槽位数为六格", 6, RatingControlGeometry.SlotCount);
        c.Equal("控件交叉轴取玻璃按钮边长", 44d, RatingControlGeometry.Thickness);
        c.Equal("展开态每格步长 36", 36d, RatingControlGeometry.Slot);
        c.Equal("星排端留白 4", 4d, RatingControlGeometry.EndInset);
        c.Equal("展开态固有长度 224", 224d, RatingControlGeometry.ExpandedLength);
        c.Equal("紧凑态最短长度 88", 88d, RatingControlGeometry.CompactMinLength);
        c.Equal("紧凑星字号 18", 18d, RatingControlGeometry.CompactGlyph);
        c.Equal("点按阈值 8", 8d, RatingControlGeometry.TapSlop);
        c.Equal("取消阈值 44", 44d, RatingControlGeometry.CancelSlop);

        c.True("可用主轴 0 为紧凑态", RatingControlGeometry.ResolveForm(0) == RatingControlForm.Compact);
        c.True("可用主轴 223.99 为紧凑态", RatingControlGeometry.ResolveForm(223.99) == RatingControlForm.Compact);
        c.True("可用主轴 224 为展开态", RatingControlGeometry.ResolveForm(224) == RatingControlForm.Expanded);
        c.True("可用主轴 500 为展开态", RatingControlGeometry.ResolveForm(500) == RatingControlForm.Expanded);

        var expanded = RatingControlGeometry.ControlFrame(new LayoutRect(10, 100, 229, 44), true);
        c.True("横排 229 槽位内展开态宽 224 且主轴居中",
            Near(expanded.X, 12.5) && Near(expanded.Y, 100) && Near(expanded.Width, 224) && Near(expanded.Height, 44),
            RectText(expanded));

        var compact = RatingControlGeometry.ControlFrame(new LayoutRect(0, 0, 133, 44), true);
        c.True("横排 133 槽位内紧凑态铺满主轴",
            Near(compact.X, 0) && Near(compact.Y, 0) && Near(compact.Width, 133) && Near(compact.Height, 44),
            RectText(compact));

        var verticalExpanded = RatingControlGeometry.ControlFrame(new LayoutRect(3, 7, 44, 229), false);
        c.True("竖排 229 槽位内展开态高 224 且主轴居中",
            Near(verticalExpanded.X, 3) && Near(verticalExpanded.Y, 9.5)
            && Near(verticalExpanded.Width, 44) && Near(verticalExpanded.Height, 224),
            RectText(verticalExpanded));

        var verticalCompact = RatingControlGeometry.ControlFrame(new LayoutRect(3, 7, 44, 133), false);
        c.True("竖排 133 槽位内紧凑态铺满主轴",
            Near(verticalCompact.X, 3) && Near(verticalCompact.Y, 7)
            && Near(verticalCompact.Width, 44) && Near(verticalCompact.Height, 133),
            RectText(verticalCompact));

        var crossCentered = RatingControlGeometry.ControlFrame(new LayoutRect(0, 0, 229, 60), true);
        c.True("槽位交叉轴高于控件时交叉轴居中", Near(crossCentered.Y, 8) && Near(crossCentered.Height, 44),
            RectText(crossCentered));
    }

    private static void RunSlotMapping(Checks c)
    {
        c.Section("星级槽位与主轴反向映射");

        c.Equal("恰好六格时行起点为 0", 0d, RatingControlGeometry.RowStart(216, 36));
        c.Equal("行宽 300 时行起点居中", 42d, RatingControlGeometry.RowStart(300, 36));

        foreach (bool horizontal in new[] { false, true })
        {
            string axis = horizontal ? "横排" : "竖排";
            c.Equal(axis + " 0 号格主轴边长 36", 36d,
                horizontal
                    ? RatingControlGeometry.SlotRect(0, 216, 44, true, 36).Width
                    : RatingControlGeometry.SlotRect(0, 216, 44, false, 36).Height);

            var contiguous = true;
            for (int value = 0; value < RatingControlGeometry.SlotCount - 1; value++)
            {
                var current = RatingControlGeometry.SlotRect(value, 216, 44, horizontal, 36);
                var next = RatingControlGeometry.SlotRect(value + 1, 216, 44, horizontal, 36);
                contiguous &= horizontal ? Near(current.X + current.Width, next.X) : Near(next.Y + next.Height, current.Y);
            }
            c.True(axis + " 相邻格无缝相接", contiguous);

            var roundTrip = true;
            for (int value = 0; value < RatingControlGeometry.SlotCount; value++)
            {
                var rect = RatingControlGeometry.SlotRect(value, 216, 44, horizontal, 36);
                double main = horizontal ? rect.X + rect.Width / 2 : rect.Y + rect.Height / 2;
                roundTrip &= RatingControlGeometry.ValueAt(main, 216, horizontal, 36) == value;
            }
            c.True(axis + " 六格中点反向映射回到原值", roundTrip);

            var centeredRow = true;
            for (int value = 0; value < RatingControlGeometry.SlotCount; value++)
            {
                var rect = RatingControlGeometry.SlotRect(value, 300, 44, horizontal, 36);
                double main = horizontal ? rect.X + rect.Width / 2 : rect.Y + rect.Height / 2;
                centeredRow &= RatingControlGeometry.ValueAt(main, 300, horizontal, 36) == value;
            }
            c.True(axis + " 行宽 300 的居中星排往返一致", centeredRow);

            c.True(axis + " 越过远端夹到 5", RatingControlGeometry.ValueAt(1000, 216, horizontal, 36) == (horizontal ? 5 : 0));
            c.True(axis + " 越过近端夹到 0", RatingControlGeometry.ValueAt(-1000, 216, horizontal, 36) == (horizontal ? 0 : 5));
        }

        c.True("横排 0 号格在主轴上最左",
            Near(RatingControlGeometry.SlotRect(0, 216, 44, true, 36).X, 0)
            && Near(RatingControlGeometry.SlotRect(5, 216, 44, true, 36).X, 180));
        c.True("竖排 5 号格在顶部、0 号格在底部",
            Near(RatingControlGeometry.SlotRect(5, 216, 44, false, 36).Y, 0)
            && Near(RatingControlGeometry.SlotRect(0, 216, 44, false, 36).Y, 180));
        c.Equal("横排格子在交叉轴占满整行", 44d, RatingControlGeometry.SlotRect(3, 216, 44, true, 36).Height);

        c.Equal("步长 44 的第三格落在 132", 132d, RatingControlGeometry.SlotRect(3, 264, 44, true, 44).X);
        c.Equal("步长 44 的 132 映射回 3", 3, RatingControlGeometry.ValueAt(132, 264, true, 44));

        foreach (int pitch in new[] { 36, 44 })
        foreach (bool horizontal in new[] { false, true })
        {
            double rowLength = RatingControlGeometry.SlotCount * pitch + 40;
            var roundTrip = true;
            for (int value = 0; value < RatingControlGeometry.SlotCount; value++)
            {
                var rect = RatingControlGeometry.SlotRect(value, rowLength, 44, horizontal, pitch);
                double main = horizontal ? rect.X + rect.Width / 2 : rect.Y + rect.Height / 2;
                roundTrip &= RatingControlGeometry.ValueAt(main, rowLength, horizontal, pitch) == value;
            }
            c.True($"步长 {pitch} 的{(horizontal ? "横排" : "竖排")}居中星排在更宽行内往返一致", roundTrip);
        }
    }

    private static void RunCompactCluster(Checks c)
    {
        c.Section("紧凑星簇");
        c.Equal("无冲突五个条目", 5, RatingControlGeometry.CompactItemCount(false));
        c.Equal("冲突时加三角共六个条目", 6, RatingControlGeometry.CompactItemCount(true));

        c.True("165 胶囊无冲突步长 = (165 − 44) / 4", Near(RatingControlGeometry.CompactStep(165, false), 30.25));
        c.True("165 胶囊冲突步长 = (165 − 44) / 5", Near(RatingControlGeometry.CompactStep(165, true), 24.2));
        c.True("88 胶囊无冲突步长 11（字形互相叠压）", Near(RatingControlGeometry.CompactStep(88, false), 11));
        c.True("步长不超过展开态格宽 36", Near(RatingControlGeometry.CompactStep(223, false), 36));
        c.True("胶囊短于控件厚度时步长为 0", Near(RatingControlGeometry.CompactStep(30, false), 0));

        c.True("165 无冲突星 1 中心落在左端半圆圆心",
            Near(165 / 2.0 + RatingControlGeometry.CompactOffset(1, false, true, 165), 22));
        c.True("165 无冲突星 5 中心落在右端半圆圆心",
            Near(165 / 2.0 + RatingControlGeometry.CompactOffset(5, false, true, 165), 165 - 22));
        c.True("无冲突星 3 在胶囊中心", Near(RatingControlGeometry.CompactOffset(3, false, true, 165), 0));
        var even = true;
        for (int item = 1; item < 5; item++)
            even &= Near(RatingControlGeometry.CompactOffset(item + 1, false, true, 120)
                - RatingControlGeometry.CompactOffset(item, false, true, 120), RatingControlGeometry.CompactStep(120, false));
        c.True("无冲突相邻星等距", even);
        c.True("无冲突时 0 号条目偏移为 0", Near(RatingControlGeometry.CompactOffset(0, false, true, 165), 0));

        c.True("165 冲突时三角在左端半圆圆心",
            Near(165 / 2.0 + RatingControlGeometry.CompactOffset(0, true, true, 165), 22));
        c.True("冲突时星 1 紧随三角一个步长",
            Near(RatingControlGeometry.CompactOffset(1, true, true, 165) - RatingControlGeometry.CompactOffset(0, true, true, 165),
                RatingControlGeometry.CompactStep(165, true)));
        c.True("165 冲突时星 5 在右端半圆圆心",
            Near(165 / 2.0 + RatingControlGeometry.CompactOffset(5, true, true, 165), 165 - 22));
        c.True("223 胶囊步长封顶后整簇居中",
            Near(RatingControlGeometry.CompactOffset(1, false, true, 223) + RatingControlGeometry.CompactOffset(5, false, true, 223), 0)
            && Near(RatingControlGeometry.CompactOffset(5, false, true, 223), 72));
        c.True("竖排偏移取反（星 1 在底部）",
            Near(RatingControlGeometry.CompactOffset(1, false, false, 165), 165 / 2.0 - 22));
        c.True("竖排冲突时三角在最底部",
            Near(RatingControlGeometry.CompactOffset(0, true, false, 165), 165 / 2.0 - 22));
    }

    private static void RunPopupDirection(Checks c)
    {
        c.Section("星级弹窗方向表");
        var table = new (ScreenForm Form, bool RowLayout, bool NotchOnLeft, RatingPopupAnchor Anchor, RatingPopupDirection Expected)[]
        {
            (ScreenForm.Wide, true, false, RatingPopupAnchor.Control, RatingPopupDirection.Up),
            (ScreenForm.Wide, true, true, RatingPopupAnchor.Control, RatingPopupDirection.Up),
            (ScreenForm.Wide, true, false, RatingPopupAnchor.Card, RatingPopupDirection.Down),
            (ScreenForm.Wide, true, true, RatingPopupAnchor.Card, RatingPopupDirection.Down),
            (ScreenForm.Wide, false, false, RatingPopupAnchor.Control, RatingPopupDirection.Left),
            (ScreenForm.Wide, false, true, RatingPopupAnchor.Control, RatingPopupDirection.Left),
            (ScreenForm.Wide, false, false, RatingPopupAnchor.Card, RatingPopupDirection.Right),
            (ScreenForm.Wide, false, true, RatingPopupAnchor.Card, RatingPopupDirection.Right),
            (ScreenForm.PhonePortrait, true, false, RatingPopupAnchor.Control, RatingPopupDirection.Up),
            (ScreenForm.PhonePortrait, false, true, RatingPopupAnchor.Control, RatingPopupDirection.Up),
            (ScreenForm.PhonePortrait, true, false, RatingPopupAnchor.Card, RatingPopupDirection.Up),
            (ScreenForm.PhonePortrait, false, true, RatingPopupAnchor.Card, RatingPopupDirection.Up),
            (ScreenForm.PhoneLandscape, true, true, RatingPopupAnchor.Control, RatingPopupDirection.Left),
            (ScreenForm.PhoneLandscape, false, true, RatingPopupAnchor.Control, RatingPopupDirection.Left),
            (ScreenForm.PhoneLandscape, true, false, RatingPopupAnchor.Control, RatingPopupDirection.Right),
            (ScreenForm.PhoneLandscape, false, false, RatingPopupAnchor.Control, RatingPopupDirection.Right),
            (ScreenForm.PhoneLandscape, true, true, RatingPopupAnchor.Card, RatingPopupDirection.Right),
            (ScreenForm.PhoneLandscape, false, true, RatingPopupAnchor.Card, RatingPopupDirection.Right),
            (ScreenForm.PhoneLandscape, true, false, RatingPopupAnchor.Card, RatingPopupDirection.Left),
            (ScreenForm.PhoneLandscape, false, false, RatingPopupAnchor.Card, RatingPopupDirection.Left),
        };

        foreach (var (form, rowLayout, notchOnLeft, anchor, expected) in table)
        {
            var actual = RatingControlGeometry.PopupDirection(form, rowLayout, notchOnLeft, anchor);
            c.True($"{form}/{(rowLayout ? "行" : "列")}/刘海{(notchOnLeft ? "左" : "右")}/"
                + $"{(anchor == RatingPopupAnchor.Control ? "控件" : "卡片")} 向 {expected}",
                actual == expected, "实际 " + actual);
        }
    }

    private static void RunPopupPlacement(Checks c)
    {
        c.Section("星级弹窗落位");
        var anchor = new LayoutRect(250, 300, 20, 20);
        var bounds = new LayoutRect(0, 0, 500, 500);
        double gap = 8;

        var up = RatingControlGeometry.PlacePopup(anchor, bounds, 200, 100, RatingPopupDirection.Up, gap);
        c.True("向上：主轴居中、底边距入口 8", Near(up.X, 160) && Near(up.Y, 192), RectText(up));
        var down = RatingControlGeometry.PlacePopup(anchor, bounds, 200, 100, RatingPopupDirection.Down, gap);
        c.True("向下：主轴居中、顶边距入口 8", Near(down.X, 160) && Near(down.Y, 328), RectText(down));
        var left = RatingControlGeometry.PlacePopup(anchor, bounds, 200, 100, RatingPopupDirection.Left, gap);
        c.True("向左：交叉轴居中、右边距入口 8", Near(left.X, 42) && Near(left.Y, 260), RectText(left));
        var right = RatingControlGeometry.PlacePopup(anchor, bounds, 200, 100, RatingPopupDirection.Right, gap);
        c.True("向右：交叉轴居中、左边距入口 8", Near(right.X, 278) && Near(right.Y, 260), RectText(right));

        var topClamp = RatingControlGeometry.PlacePopup(
            new LayoutRect(300, 50, 20, 20), bounds, 200, 100, RatingPopupDirection.Up, gap);
        c.True("向上越界夹到边界顶边", Near(topClamp.Y, 0), RectText(topClamp));
        var bottomClamp = RatingControlGeometry.PlacePopup(
            new LayoutRect(300, 450, 20, 20), bounds, 200, 100, RatingPopupDirection.Down, gap);
        c.True("向下越界夹到边界底边", Near(bottomClamp.Y, 400), RectText(bottomClamp));
        var leftClamp = RatingControlGeometry.PlacePopup(
            new LayoutRect(100, 300, 20, 20), bounds, 200, 100, RatingPopupDirection.Left, gap);
        c.True("向左越界夹到边界左边", Near(leftClamp.X, 0), RectText(leftClamp));
        var rightClamp = RatingControlGeometry.PlacePopup(
            new LayoutRect(450, 300, 20, 20), bounds, 200, 100, RatingPopupDirection.Right, gap);
        c.True("向右越界夹到边界右边", Near(rightClamp.X, 300), RectText(rightClamp));

        var inside = true;
        foreach (var direction in Enum.GetValues<RatingPopupDirection>())
        {
            var rect = RatingControlGeometry.PlacePopup(anchor, bounds, 200, 100, direction, gap);
            inside &= rect.X >= bounds.X - Epsilon && rect.Y >= bounds.Y - Epsilon
                && rect.MaxX <= bounds.MaxX + Epsilon && rect.MaxY <= bounds.MaxY + Epsilon;
        }
        c.True("四向落位在边界容得下时始终留在边界内", inside);
    }

    private static void RunExternalInBand(Checks c)
    {
        c.Section("弹窗外起手的取消带");

        // 行布局控制栏的紧凑胶囊：弹窗在其上方，星排横排。
        var anchor = new LayoutRect(100, 300, 88, 44);
        var panel = new LayoutRect(40, 200, 200, 92);
        var row = new LayoutRect(48, 230, 264, 44);
        c.True("横排星排：带内起点可继续预览",
            RatingControlGeometry.ExternalInBand(150, 250, anchor, panel, row, true));
        c.True("横排星排：交叉轴越出取消阈值即取消",
            !RatingControlGeometry.ExternalInBand(150, 389, anchor, panel, row, true)
            && !RatingControlGeometry.ExternalInBand(150, 155, anchor, panel, row, true));
        c.True("横排星排：主轴恰好落在星排端点外半格仍可预览",
            RatingControlGeometry.ExternalInBand(334, 250, anchor, panel, row, true)
            && RatingControlGeometry.ExternalInBand(26, 250, anchor, panel, row, true));
        c.True("横排星排：主轴越出星排两端半格即无预览",
            !RatingControlGeometry.ExternalInBand(334.01, 250, anchor, panel, row, true)
            && !RatingControlGeometry.ExternalInBand(25.99, 250, anchor, panel, row, true));

        // 列布局控制栏：弹窗在控件左侧，星排竖排。
        var columnAnchor = new LayoutRect(300, 100, 44, 224);
        var columnPanel = new LayoutRect(180, 51, 112, 322);
        var columnRow = new LayoutRect(214, 101, 44, 264);
        c.True("竖排星排向左弹窗：带内起点可继续预览",
            RatingControlGeometry.ExternalInBand(320, 200, columnAnchor, columnPanel, columnRow, false));
        c.True("竖排星排向左弹窗：交叉轴两侧越界即取消",
            !RatingControlGeometry.ExternalInBand(100, 200, columnAnchor, columnPanel, columnRow, false)
            && !RatingControlGeometry.ExternalInBand(400, 200, columnAnchor, columnPanel, columnRow, false));
        c.True("竖排星排向左弹窗：主轴越出星列两端半格即无预览",
            !RatingControlGeometry.ExternalInBand(320, 78.9, columnAnchor, columnPanel, columnRow, false)
            && !RatingControlGeometry.ExternalInBand(320, 387.5, columnAnchor, columnPanel, columnRow, false)
            && RatingControlGeometry.ExternalInBand(320, 79, columnAnchor, columnPanel, columnRow, false));

        // 卡片长按：卡片在胶片栏内，弹窗在其右侧且被边界夹到上缘，星排因此比卡片更靠下。
        var cardAnchor = new LayoutRect(400, 10, 96, 144);
        var cardPanel = new LayoutRect(504, 0, 112, 322);
        var cardRow = new LayoutRect(538, 50, 44, 264);
        c.True("卡片上缘的手指不再被夹成 5 星预览",
            !RatingControlGeometry.ExternalInBand(450, 25, cardAnchor, cardPanel, cardRow, false));
        c.True("横向落到星列范围内的手指照常预览",
            RatingControlGeometry.ExternalInBand(450, 100, cardAnchor, cardPanel, cardRow, false));
        c.True("侧向弹窗远端越界即取消",
            !RatingControlGeometry.ExternalInBand(700, 100, cardAnchor, cardPanel, cardRow, false));
    }

    private static void RunGesture(Checks c)
    {
        c.Section("星级手势状态机");

        var tap = new RatingGesture(2);
        tap.Begin(0, 0, 4);
        var tapResult = tap.End(0, 0, 4, true);
        c.True("点按异值一次提交",
            tapResult.Outcome == RatingGestureOutcome.Commit && tapResult.Value == 4, ResultText(tapResult));

        var sameTap = new RatingGesture(2);
        sameTap.Begin(0, 0, 2);
        c.True("点按原值不写",
            sameTap.End(0, 0, 2, true).Outcome == RatingGestureOutcome.None);

        var forced = new RatingGesture(2, forceCommit: true);
        forced.Begin(0, 0, 2);
        var forcedResult = forced.End(0, 0, 2, true);
        c.True("冲突时同值也提交",
            forcedResult.Outcome == RatingGestureOutcome.Commit && forcedResult.Value == 2, ResultText(forcedResult));

        var slide = new RatingGesture(0);
        slide.Begin(0, 0, 1);
        bool changed = slide.Move(36, 0, 2, true) && slide.Move(72, 0, 3, true) && slide.Move(108, 0, 4, true);
        c.True("滑动 1→2→3→4 每档都刷新预览", changed && slide.Preview == 4, "预览 " + slide.Preview);
        var slideResult = slide.End(144, 0, 4, true);
        c.True("抬手只提交最后值",
            slideResult.Outcome == RatingGestureOutcome.Commit && slideResult.Value == 4, ResultText(slideResult));

        var back = new RatingGesture(3);
        back.Begin(0, 0, 3);
        back.Move(36, 0, 4, true);
        c.True("滑回原值不写", back.End(36, 0, 3, true).Outcome == RatingGestureOutcome.None);

        var outOfBand = new RatingGesture(3);
        outOfBand.Begin(0, 0, 4);
        c.True("越出取消带预览清空", outOfBand.Move(0, 100, 4, false) && outOfBand.Preview is null);
        c.True("抬起仍在取消带内不写", outOfBand.End(0, 100, 4, false).Outcome == RatingGestureOutcome.None);

        var reenter = new RatingGesture(3);
        reenter.Begin(0, 0, 2);
        reenter.Move(0, 100, 2, false);
        reenter.Move(0, 0, 4, true);
        var reenterResult = reenter.End(0, 0, 4, true);
        c.True("回到带内后抬手提交",
            reenterResult.Outcome == RatingGestureOutcome.Commit && reenterResult.Value == 4, ResultText(reenterResult));

        var cancelled = new RatingGesture(2);
        cancelled.Begin(0, 0, 5);
        cancelled.Cancel();
        c.True("取消后抬手不写", cancelled.End(0, 0, 5, true).Outcome == RatingGestureOutcome.None);
        c.True("第二次抬手同样不写（一次手势最多一次提交）",
            cancelled.End(0, 0, 5, true).Outcome == RatingGestureOutcome.None);

        var deferredTap = new RatingGesture(2, deferPreview: true);
        bool firstPreview = deferredTap.Begin(0, 0, 4);
        bool smallMove = deferredTap.Move(3, 3, 4, true);
        c.True("延迟预览在阈值内不产生预览",
            !firstPreview && !smallMove && deferredTap.Preview is null);
        var deferredTapResult = deferredTap.End(3, 3, 4, true);
        c.True("延迟预览未越阈值抬手保持弹窗",
            deferredTapResult.Outcome == RatingGestureOutcome.KeepPopup && deferredTapResult.Value == 2,
            ResultText(deferredTapResult));

        var deferredSlide = new RatingGesture(2, deferPreview: true);
        deferredSlide.Begin(0, 0, 4);
        c.True("延迟预览越阈值后出现预览", deferredSlide.Move(10, 0, 4, true) && deferredSlide.Preview == 4);
        var deferredSlideResult = deferredSlide.End(10, 0, 4, true);
        c.True("延迟预览越阈值后抬手提交",
            deferredSlideResult.Outcome == RatingGestureOutcome.Commit && deferredSlideResult.Value == 4,
            ResultText(deferredSlideResult));

        var zeroPopup = new RatingGesture(3, zeroOpensPopup: true);
        zeroPopup.Begin(0, 0, 0);
        var zeroPopupResult = zeroPopup.End(0, 0, 0, true);
        c.True("0 号格改为打开弹窗",
            zeroPopupResult.Outcome == RatingGestureOutcome.OpenPopup && zeroPopupResult.Value == 0,
            ResultText(zeroPopupResult));

        var zeroValue = new RatingGesture(2, zeroOpensPopup: true);
        zeroValue.Begin(0, 0, 3);
        var zeroValueResult = zeroValue.End(0, 0, 3, true);
        c.True("非 0 号格仍正常提交",
            zeroValueResult.Outcome == RatingGestureOutcome.Commit && zeroValueResult.Value == 3,
            ResultText(zeroValueResult));
    }

    private static void RunPopupLayout(Checks c)
    {
        c.Section("星级弹窗条目排版");
        c.Equal("横排弹窗星排外占用 24", 24d, RatingPopupLayout.HorizontalChrome);
        c.Equal("竖排弹窗（含时间）星排外占用 66", 66d, RatingPopupLayout.ColumnChrome(true));
        c.Equal("竖排弹窗（无时间）星排外占用 46", 46d, RatingPopupLayout.ColumnChrome(false));
        c.Equal("步长 44 时条目宽 280", 280d, RatingPopupLayout.EntryWidth(44));

        c.Equal("空间充裕时步长取上限 44", 44d, RatingPopupLayout.Pitch(1000, RatingPopupLayout.HorizontalChrome));
        c.Equal("空间不足时步长取下限 28", 28d, RatingPopupLayout.Pitch(24, RatingPopupLayout.HorizontalChrome));
        c.Equal("中间空间向下取整", 36d, RatingPopupLayout.Pitch(240, RatingPopupLayout.HorizontalChrome));

        var row = RatingPopupLayout.SingleRow(80, 60, 44);
        c.True("单行条目放得下时间时显示时间", !row.TwoLines && row.Time.Width > 0);
        c.Equal("单行条目宽 280", 280d, row.Width);
        c.Equal("单行条目高 82", 82d, row.Height);
        c.Equal("单行文件名让出右侧时间", 198d, row.Name.Width);
        c.Equal("时间贴右内边距", 272d, row.Time.X + row.Time.Width);
        c.Equal("单行星排顶边 30", 30d, row.Stars.Y);
        c.Equal("单行星排宽 264", 264d, row.Stars.Width);
        c.True("单行条目星排横排", row.StarsHorizontal);

        var noTime = RatingPopupLayout.SingleRow(250, 60, 44);
        c.True("文件名放不下时不显示时间", noTime.Time.Width == 0 && noTime.Name.Width == 264,
            $"时间宽 {noTime.Time.Width}，文件名宽 {noTime.Name.Width}");

        var column = RatingPopupLayout.SingleColumn(80, 80, 44);
        c.Equal("竖排条目宽 112", 112d, column.Width);
        c.True("竖排条目显示时间时独占一行", column.TwoLines && column.Time.Width > 0);
        c.Equal("竖排条目（含时间）高 322", 322d, column.Height);
        c.Equal("竖排星列在条目宽内居中", 34d, column.Stars.X);
        c.Equal("竖排星列在时间行下方", 50d, column.Stars.Y);
        c.Equal("竖排星列长 264", 264d, column.Stars.Height);
        c.True("竖排条目星列竖排", !column.StarsHorizontal);

        var columnNoTime = RatingPopupLayout.SingleColumn(80, 0, 44);
        c.True("竖排条目无时间时只有一行", !columnNoTime.TwoLines && columnNoTime.Time.Width == 0);
        c.Equal("竖排条目（无时间）高 302", 302d, columnNoTime.Height);
        c.Equal("竖排条目无时间时星列上移", 30d, columnNoTime.Stars.Y);

        var source = RatingPopupLayout.Source(100, 40, 50, 44);
        c.True("来源条目短文件名保持单行", !source.TwoLines);
        c.Equal("来源条目文件名让出信息宽", 162d, source.Name.Width);
        c.Equal("来源条目星排顶边 30", 30d, source.Stars.Y);
        c.Equal("来源条目高 82", 82d, source.Height);
        c.Equal("来源状态贴右内边距", 272d, source.Status.X + source.Status.Width);
        c.Equal("来源标签在状态左侧留标签间距", 176d, source.Badge.X);
        c.Equal("来源信息行与文件名同高", 8d, source.Status.Y);

        var longSource = RatingPopupLayout.Source(200, 40, 50, 44);
        c.True("来源条目长文件名改为双行", longSource.TwoLines);
        c.Equal("双行文件名占满星排宽", 264d, longSource.Name.Width);
        c.Equal("双行信息行下移", 28d, longSource.Status.Y);
        c.Equal("双行星排顶边 50", 50d, longSource.Stars.Y);
        c.Equal("双行来源条目高 102", 102d, longSource.Height);

        var clampedSource = RatingPopupLayout.Source(100, 40, 300, 44);
        c.Equal("状态宽超限时夹进星排宽", 218d, clampedSource.Status.Width);
        c.Equal("夹取后状态仍贴右内边距", 272d, clampedSource.Status.X + clampedSource.Status.Width);
        c.True("夹取后标签与状态仍靠右且不越界",
            clampedSource.Badge.X >= RatingPopupLayout.EntryPadding - Epsilon
            && clampedSource.Status.X >= clampedSource.Badge.X + clampedSource.Badge.Width - Epsilon);

        var entries = new[] { row, longSource, row };
        var panel = RatingPopupLayout.PanelSize(entries);
        c.True("三条目面板宽 288", Near(panel.Width, 288), panel.Width.ToString("0.##"));
        c.True("三条目面板高 290", Near(panel.Height, 290), panel.Height.ToString("0.##"));
        c.Equal("首条目顶边为面板内边距", 4d, RatingPopupLayout.EntryTop(entries, 0));
        c.Equal("第二条目跨过首条目与间距", 94d, RatingPopupLayout.EntryTop(entries, 1));
        c.Equal("第三条目累计前两条", 204d, RatingPopupLayout.EntryTop(entries, 2));
        c.Equal("首条分隔线居中于间距", 89.5d, RatingPopupLayout.DividerY(entries, 0));
        c.Equal("次条分隔线居中于间距", 199.5d, RatingPopupLayout.DividerY(entries, 1));

        var single = RatingPopupLayout.PanelSize([row]);
        c.True("单条目面板高为纯内边距加条目高", Near(single.Width, 288) && Near(single.Height, 90),
            $"{single.Width}×{single.Height}");
        c.Equal("单条目顶边为面板内边距", 4d, RatingPopupLayout.EntryTop([row], 0));
        c.Equal("单条目分隔线居中于其后间距", 89.5d, RatingPopupLayout.DividerY([row], 0));
        var empty = RatingPopupLayout.PanelSize([]);
        c.True("空面板为纯内边距", Near(empty.Width, 8) && Near(empty.Height, 8),
            $"{empty.Width}×{empty.Height}");
    }

    private static void RunIntegration(Checks c)
    {
        c.Section("星级控件与手机竖屏槽位");
        var input = new PhoneLayoutInput(
            Landscape: false,
            NotchOnLeft: true,
            Width: 402,
            Height: 874,
            Safe: new EdgeInsets(0, 62, 0, 34),
            LeadBand: new EdgeInsets(0, 62, 0, 0),
            ControlBand: new EdgeInsets(13.67, 0, 13.67, 34),
            FilmVisible: true,
            ControlVisible: true,
            AnalysisVisible: true,
            ShowNavigation: true,
            FilterWidth: 120,
            FilmBand: 116,
            AnalysisCross: 96,
            ExifWidth: 120,
            ExifHeight: 44,
            ButtonSize: DesignTokens.ShellGlassButtonSize,
            EdgeMargin: DesignTokens.ShellPhoneEdgeMargin,
            RowInset: DesignTokens.ShellPhoneRowInset,
            GroupGap: DesignTokens.ShellGlassGroupGap);

        foreach (bool showNavigation in new[] { true, false })
        {
            var layout = PhoneScreenLayout.Compute(input with { ShowNavigation = showNavigation });
            var slot = layout.Rating;
            var frame = RatingControlGeometry.ControlFrame(slot, true);
            string label = showNavigation ? "显示上下张" : "隐藏上下张";
            bool inside = frame.X >= slot.X - Epsilon && frame.MaxX <= slot.MaxX + Epsilon
                && frame.Y >= slot.Y - Epsilon && frame.MaxY <= slot.MaxY + Epsilon;

            c.True($"{label}时控件不越出星级槽位", inside,
                $"槽位 {RectText(slot)}，控件 {RectText(frame)}");
            c.True($"{label}时槽位宽 {slot.Width:0.##} 与形态判定一致",
                (frame.Width >= RatingControlGeometry.ExpandedLength - Epsilon)
                    == (slot.Width >= RatingControlGeometry.ExpandedLength - Epsilon),
                $"槽位宽 {slot.Width:0.##}，控件宽 {frame.Width:0.##}");
        }

        var withNavigationLayout = PhoneScreenLayout.Compute(input);
        var withoutNavigationLayout = PhoneScreenLayout.Compute(input with { ShowNavigation = false });
        c.True("隐藏上下张后槽位达到展开态阈值",
            withNavigationLayout.Rating.Width < RatingControlGeometry.ExpandedLength
            && withoutNavigationLayout.Rating.Width >= RatingControlGeometry.ExpandedLength,
            $"有上下张 {withNavigationLayout.Rating.Width:0.##}，无上下张 {withoutNavigationLayout.Rating.Width:0.##}");
    }

    private static bool Near(double first, double second) => Math.Abs(first - second) <= Epsilon;

    private static string RectText(LayoutRect rect)
        => $"({rect.X:0.##},{rect.Y:0.##},{rect.Width:0.##}×{rect.Height:0.##})";

    private static string ResultText(RatingGestureResult result) => $"{result.Outcome} {result.Value}";
}
