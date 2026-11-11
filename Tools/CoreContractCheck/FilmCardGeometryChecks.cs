using PhotoViewer.Shared;

namespace CoreContractCheck;

/// <summary>
/// FILM-02 / FILM-05：胶片卡片两档的尺寸与内边距、带厚、选档、可见容量，以及大档与旧
/// <see cref="FilmstripGeometry"/> 在步长、首项起点、文档尺寸与卡片矩形上的逐位一致。
/// </summary>
internal static class FilmCardGeometryChecks
{
    private const double Epsilon = 1e-6;

    public static void Run(Checks c)
    {
        RunTierMetrics(c);
        RunLargeMatchesLegacy(c);
        RunTierResolution(c);
        RunCapacity(c);
        RunSmallFrames(c);
    }

    private static void RunTierMetrics(Checks c)
    {
        c.Section("胶片卡片两档尺寸与带厚");
        c.Equal("最少可见卡片数为 5", 5, FilmCardGeometry.MinVisible);
        c.True("大档卡片 96×144",
            Near(FilmCardGeometry.Width(FilmCardTier.Large), 96)
            && Near(FilmCardGeometry.Height(FilmCardTier.Large), 144),
            $"{FilmCardGeometry.Width(FilmCardTier.Large)}×{FilmCardGeometry.Height(FilmCardTier.Large)}");
        c.True("小档卡片 64×76",
            Near(FilmCardGeometry.Width(FilmCardTier.Small), 64)
            && Near(FilmCardGeometry.Height(FilmCardTier.Small), 76),
            $"{FilmCardGeometry.Width(FilmCardTier.Small)}×{FilmCardGeometry.Height(FilmCardTier.Small)}");
        c.True("两档卡片间距均为 0",
            Near(FilmCardGeometry.Spacing(FilmCardTier.Large), 0)
            && Near(FilmCardGeometry.Spacing(FilmCardTier.Small), 0));
        c.True("大档主轴内边距横向 10/10、纵向 5/10",
            Near(FilmCardGeometry.MainLead(FilmCardTier.Large, true), 10)
            && Near(FilmCardGeometry.MainTrail(FilmCardTier.Large, true), 10)
            && Near(FilmCardGeometry.MainLead(FilmCardTier.Large, false), 5)
            && Near(FilmCardGeometry.MainTrail(FilmCardTier.Large, false), 10));
        c.True("小档主轴内边距两向两端均为 6",
            Near(FilmCardGeometry.MainLead(FilmCardTier.Small, true), 6)
            && Near(FilmCardGeometry.MainTrail(FilmCardTier.Small, true), 6)
            && Near(FilmCardGeometry.MainLead(FilmCardTier.Small, false), 6)
            && Near(FilmCardGeometry.MainTrail(FilmCardTier.Small, false), 6));
        c.True("大档交叉轴内边距横向 5/10、纵向 10/10",
            Near(FilmCardGeometry.CrossLead(FilmCardTier.Large, true), 5)
            && Near(FilmCardGeometry.CrossTrail(FilmCardTier.Large, true), 10)
            && Near(FilmCardGeometry.CrossLead(FilmCardTier.Large, false), 10)
            && Near(FilmCardGeometry.CrossTrail(FilmCardTier.Large, false), 10));
        c.True("小档交叉轴内边距两向两侧均为 8",
            Near(FilmCardGeometry.CrossLead(FilmCardTier.Small, true), 8)
            && Near(FilmCardGeometry.CrossTrail(FilmCardTier.Small, true), 8)
            && Near(FilmCardGeometry.CrossLead(FilmCardTier.Small, false), 8)
            && Near(FilmCardGeometry.CrossTrail(FilmCardTier.Small, false), 8));

        double largeHorizontal = FilmCardGeometry.BandThickness(FilmCardTier.Large, true);
        double largeVertical = FilmCardGeometry.BandThickness(FilmCardTier.Large, false);
        c.True("大档带厚横 159 / 纵 116 且与主屏分区一致",
            Near(largeHorizontal, 159) && Near(largeVertical, 116)
            && Near(largeHorizontal, MainScreenLayout.FilmstripBand)
            && Near(largeVertical, MainScreenLayout.FileBarContentWidth),
            $"横 {largeHorizontal}，纵 {largeVertical}");
        c.True("小档带厚横 92 / 纵 80",
            Near(FilmCardGeometry.BandThickness(FilmCardTier.Small, true), 92)
            && Near(FilmCardGeometry.BandThickness(FilmCardTier.Small, false), 80),
            $"横 {FilmCardGeometry.BandThickness(FilmCardTier.Small, true)}，"
            + $"纵 {FilmCardGeometry.BandThickness(FilmCardTier.Small, false)}");

        c.True("步长横向取卡片宽、纵向取卡片高",
            Near(FilmCardGeometry.Step(FilmCardTier.Large, true), 96)
            && Near(FilmCardGeometry.Step(FilmCardTier.Large, false), 144)
            && Near(FilmCardGeometry.Step(FilmCardTier.Small, true), 64)
            && Near(FilmCardGeometry.Step(FilmCardTier.Small, false), 76));
    }

    private static void RunLargeMatchesLegacy(Checks c)
    {
        c.Section("大档与旧胶片几何逐位一致");
        int[] counts = [0, 1, 3, 40];
        double[] viewports = [300, 1024, 2000];
        foreach (bool horizontal in new[] { false, true })
        {
            string axis = horizontal ? "横排" : "竖排";
            var steps = new List<string>();
            var starts = new List<string>();
            var docs = new List<string>();
            var frames = new List<string>();
            foreach (int count in counts)
            foreach (double viewport in viewports)
            {
                string label = $"{count} 项 / 视口 {viewport}";

                double legacyStep = FilmstripGeometry.Step(horizontal);
                double step = FilmCardGeometry.Step(FilmCardTier.Large, horizontal);
                if (!Near(step, legacyStep)) steps.Add($"{label}：{step} ≠ {legacyStep}");

                double legacyDocWidth = FilmstripGeometry.ContentSize(horizontal, viewport, 400, count).Width;
                double legacyStart = FilmstripGeometry.ItemStart(horizontal, legacyDocWidth, count);
                double start = FilmCardGeometry.ItemStart(FilmCardTier.Large, horizontal, legacyDocWidth, count);
                if (!Near(start, legacyStart)) starts.Add($"{label}：{start} ≠ {legacyStart}");

                var legacyDoc = FilmstripGeometry.ContentSize(horizontal, viewport, 400, count);
                var doc = FilmCardGeometry.ContentSize(FilmCardTier.Large, horizontal, viewport, 400, count);
                if (!Near(doc.Width, legacyDoc.Width) || !Near(doc.Height, legacyDoc.Height))
                    docs.Add($"{label}：{doc.Width}×{doc.Height} ≠ {legacyDoc.Width}×{legacyDoc.Height}");

                for (int index = 0; index < Math.Max(1, count); index++)
                {
                    var legacyFrame = FilmstripGeometry.ItemFrame(horizontal, index, count, legacyDoc.Width);
                    var frame = FilmCardGeometry.ItemFrame(FilmCardTier.Large, horizontal, index, count, legacyDoc.Width);
                    if (!Near(frame.X, legacyFrame.X) || !Near(frame.Y, legacyFrame.Y)
                        || !Near(frame.Width, legacyFrame.Width) || !Near(frame.Height, legacyFrame.Height))
                    {
                        frames.Add($"{label} #{index}：{Rect(frame)} ≠ {Rect(legacyFrame)}");
                        break;
                    }
                }
            }

            string detail = string.Join("；", steps.Concat(starts).Concat(docs).Concat(frames));
            c.True($"{axis}大档步长与旧几何一致（0/1/3/40 项 × 300/1024/2000）", steps.Count == 0, detail);
            c.True($"{axis}大档首项起点与旧几何一致（0/1/3/40 项 × 300/1024/2000）", starts.Count == 0, detail);
            c.True($"{axis}大档文档尺寸与旧几何一致（0/1/3/40 项 × 300/1024/2000）", docs.Count == 0, detail);
            c.True($"{axis}大档全部卡片矩形与旧几何一致（0/1/3/40 项 × 300/1024/2000）", frames.Count == 0, detail);
        }

        var wideDoc = FilmCardGeometry.ContentSize(FilmCardTier.Large, true, 300, 159, 8);
        c.True("大档 8 项文档只在主轴延伸",
            Near(wideDoc.Width, 8 * 96 + 20) && Near(wideDoc.Height, 159),
            $"{wideDoc.Width}×{wideDoc.Height}");
        c.True("大档横向首项居中下限为 10",
            Near(FilmCardGeometry.ItemStart(FilmCardTier.Large, true, 92, 4), 10),
            FilmCardGeometry.ItemStart(FilmCardTier.Large, true, 92, 4).ToString("0.##"));
    }

    private static void RunTierResolution(Checks c)
    {
        c.Section("胶片卡片选档");
        c.True("横向正好 500（20 + 5×96）用大档",
            FilmCardGeometry.ResolveTier(500, true) == FilmCardTier.Large);
        c.True("横向 499.99 用小档", FilmCardGeometry.ResolveTier(499.99, true) == FilmCardTier.Small);
        c.True("横向 500.01 用大档", FilmCardGeometry.ResolveTier(500.01, true) == FilmCardTier.Large);
        c.True("纵向正好 735（15 + 5×144）用大档",
            FilmCardGeometry.ResolveTier(735, false) == FilmCardTier.Large);
        c.True("纵向 734.99 用小档", FilmCardGeometry.ResolveTier(734.99, false) == FilmCardTier.Small);
        c.True("纵向 735.01 用大档", FilmCardGeometry.ResolveTier(735.01, false) == FilmCardTier.Large);
        c.True("手机竖屏 402 宽用小档", FilmCardGeometry.ResolveTier(402, true) == FilmCardTier.Small);
        c.True("iPad 1024 宽横排用大档", FilmCardGeometry.ResolveTier(1024, true) == FilmCardTier.Large);
        c.True("iPad 768 竖排用大档", FilmCardGeometry.ResolveTier(768, false) == FilmCardTier.Large);
        c.True("长度为 0 用小档", FilmCardGeometry.ResolveTier(0, true) == FilmCardTier.Small);
    }

    private static void RunCapacity(Checks c)
    {
        c.Section("胶片条可见容量");
        var portrait = new[] { 390d, 402d, 440d };
        var capacities = new List<string>();
        bool atLeastFive = true;
        foreach (double width in portrait)
        {
            int capacity = FilmCardGeometry.VisibleCapacity(width, FilmCardTier.Small, true);
            atLeastFive &= capacity >= FilmCardGeometry.MinVisible;
            capacities.Add($"{width}→{capacity}");
        }
        c.True("小档横排在 390/402/440 宽下端仍可见至少 5 张", atLeastFive, string.Join("，", capacities));

        capacities.Clear();
        atLeastFive = true;
        foreach (double height in portrait)
        {
            int capacity = FilmCardGeometry.VisibleCapacity(height, FilmCardTier.Small, false);
            atLeastFive &= capacity >= FilmCardGeometry.MinVisible;
            capacities.Add($"{height}→{capacity}");
        }
        c.True("小档竖排在 390/402/440 高下端仍可见至少 5 张", atLeastFive, string.Join("，", capacities));

        int narrow = FilmCardGeometry.VisibleCapacity(375, FilmCardTier.Small, false);
        c.True("小档竖排在 375 高端的实际容量低于 5（iPhone SE 横屏，记录实值）",
            narrow == 4, "实际 " + narrow);
        c.True("小档横排在 375 宽端可见 5 张",
            FilmCardGeometry.VisibleCapacity(375, FilmCardTier.Small, true) == 5,
            "实际 " + FilmCardGeometry.VisibleCapacity(375, FilmCardTier.Small, true));

        c.True("大档横向 500 正好可见 5 张",
            FilmCardGeometry.VisibleCapacity(500, FilmCardTier.Large, true) == 5);
        c.True("大档纵向 735 正好可见 5 张",
            FilmCardGeometry.VisibleCapacity(735, FilmCardTier.Large, false) == 5);
        c.True("小档横排 402 可见 6 张",
            FilmCardGeometry.VisibleCapacity(402, FilmCardTier.Small, true) == 6,
            "实际 " + FilmCardGeometry.VisibleCapacity(402, FilmCardTier.Small, true));
        c.Equal("长度为 0 时容量为 0", 0, FilmCardGeometry.VisibleCapacity(0, FilmCardTier.Small, true));
    }

    private static void RunSmallFrames(Checks c)
    {
        c.Section("小档列表几何");
        var verticalFrame = FilmCardGeometry.ItemFrame(FilmCardTier.Small, false, 0, 4, 80);
        c.True("小档竖排在 80 带宽内交叉轴居中",
            Near(verticalFrame.X, 8) && Near(verticalFrame.Width, 64) && Near(verticalFrame.Height, 76),
            Rect(verticalFrame));
        c.True("小档竖排首项顶边为主轴内边距 6", Near(verticalFrame.Y, 6), verticalFrame.Y.ToString("0.##"));

        var second = FilmCardGeometry.ItemFrame(FilmCardTier.Small, false, 1, 4, 80);
        c.True("小档竖排步长 76", Near(second.Y, 6 + 76), second.Y.ToString("0.##"));

        var horizontalFrame = FilmCardGeometry.ItemFrame(FilmCardTier.Small, true, 0, 4, 92);
        c.True("小档横排在 92 带宽内交叉轴起点为 8",
            Near(horizontalFrame.Y, 8) && Near(horizontalFrame.Height, 76), Rect(horizontalFrame));
        var centeredFrame = FilmCardGeometry.ItemFrame(FilmCardTier.Small, true, 0, 4, 400);
        c.True("小档横排 4 项在 400 宽内整组居中",
            Near(centeredFrame.X, (400 - 4 * 64) / 2), centeredFrame.X.ToString("0.##"));
        var clampedFrame = FilmCardGeometry.ItemFrame(FilmCardTier.Small, true, 0, 6, 92);
        c.True("小档横排放不下时首项退回主轴内边距 6", Near(clampedFrame.X, 6),
            clampedFrame.X.ToString("0.##"));

        var smallDoc = FilmCardGeometry.ContentSize(FilmCardTier.Small, true, 402, 92, 6);
        c.True("小档 6 项横排文档覆盖视口宽且高为带厚",
            Near(smallDoc.Width, 402) && Near(smallDoc.Height, 92),
            $"{smallDoc.Width}×{smallDoc.Height}");
        var longDoc = FilmCardGeometry.ContentSize(FilmCardTier.Small, true, 402, 92, 10);
        c.True("小档 10 项横排文档长 10×64 + 12",
            Near(longDoc.Width, 652), longDoc.Width.ToString("0.##"));

        var (first, last) = FilmstripGeometry.VisibleRange(
            10, FilmCardGeometry.ItemStart(FilmCardTier.Small, true, longDoc.Width, 10),
            FilmCardGeometry.Step(FilmCardTier.Small, true), 0, 402);
        c.True("小档复用旧可见范围：402 宽露出 0–6 项",
            first == 0 && last == 6, $"{first}–{last}");
    }

    private static bool Near(double first, double second) => Math.Abs(first - second) <= Epsilon;

    private static string Rect((double X, double Y, double Width, double Height) rect)
        => $"({rect.X:0.##},{rect.Y:0.##},{rect.Width:0.##}×{rect.Height:0.##})";
}
