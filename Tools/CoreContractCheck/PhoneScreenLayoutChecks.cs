using PhotoViewer.Core.Design;
using PhotoViewer.Shared;

namespace CoreContractCheck;

/// <summary>
/// 手机形态槽位几何不变量：在手机竖横屏与 iPad 窄窗的尺寸网格上核对命中区、互不重叠、
/// 主图适应区避让、刘海双向镜像、横屏控制列与栏背景出现规则。
/// </summary>
internal static class PhoneScreenLayoutChecks
{
    private const double ButtonSize = DesignTokens.ShellGlassButtonSize;
    private const double EdgeMargin = DesignTokens.ShellPhoneEdgeMargin;
    private const double RowInset = DesignTokens.ShellPhoneRowInset;
    private const double GroupGap = DesignTokens.ShellGlassGroupGap;
    private const double BareGap = DesignTokens.ShellPhotoBareGap;

    private const double FilterWidth = 120;
    private const double FilmBand = 116;
    private const double AnalysisCross = 96;
    private const double ExifWidthPortrait = 120;
    private const double ExifHeightPortrait = 44;
    private const double ExifWidthLandscape = 96;
    private const double ExifHeightLandscape = 160;

    private const double Epsilon = 1e-6;

    private static readonly bool[] Flags = [false, true];

    private static readonly SizeCase[] PortraitSizes =
    [
        new("390×844", 390, 844, new EdgeInsets(0, 62, 0, 34)),
        new("402×874", 402, 874, new EdgeInsets(0, 62, 0, 34)),
        new("440×956", 440, 956, new EdgeInsets(0, 62, 0, 34)),
        new("320×1024", 320, 1024, new EdgeInsets(0, 24, 0, 20)),
        new("375×1024", 375, 1024, new EdgeInsets(0, 24, 0, 20)),
        new("507×1024", 507, 1024, new EdgeInsets(0, 24, 0, 20)),
    ];

    private static readonly SizeCase[] LandscapeSizes =
    [
        new("844×390", 844, 390, new EdgeInsets(62, 0, 62, 21)),
        new("874×402", 874, 402, new EdgeInsets(62, 0, 62, 21)),
        new("956×440", 956, 440, new EdgeInsets(62, 0, 62, 21)),
    ];

    public static void Run(Checks c)
    {
        c.Section("手机形态槽位几何");

        foreach (var size in PortraitSizes)
            RunGrid(c, size, landscape: false);

        foreach (var size in LandscapeSizes)
            RunGrid(c, size, landscape: true);

        RunRatingChecks(c);
        RunLandscapeColumnChecks(c);
    }

    private static void RunGrid(Checks c, SizeCase size, bool landscape)
    {
        var problems = new List<string>();
        var cases = 0;

        foreach (var film in Flags)
        foreach (var control in Flags)
        foreach (var analysis in Flags)
        foreach (var navigation in Flags)
        foreach (var exif in Flags)
        {
            cases++;
            var input = Input(size, landscape, notchOnLeft: true, film, control, analysis, navigation, exif);
            var layout = PhoneScreenLayout.Compute(input);
            var label = $"胶片{(film ? "显" : "隐")}/控制{(control ? "显" : "隐")}"
                + $"/分析{(analysis ? "显" : "隐")}/上下张{(navigation ? "显" : "隐")}"
                + $"/EXIF{(exif ? "有" : "无")}：";
            Validate(input, layout, problems, label);
            ValidateMirror(input, layout, problems, label);
        }

        c.True($"{(landscape ? "横屏" : "竖屏")} {size.Name} 的 {cases} 种显隐与 EXIF 组合不变量",
            problems.Count == 0, string.Join(" | ", problems.Take(12)));
    }

    private static void Validate(
        in PhoneLayoutInput input,
        in PhoneLayout layout,
        List<string> problems,
        string label)
    {
        var slots = new (string Name, LayoutRect Rect, bool IsButton)[]
        {
            ("筛选", layout.Filter, true),
            ("更多", layout.More, true),
            ("EXIF", layout.Exif, false),
            ("上下张", layout.Navigation, true),
            ("星级", layout.Rating, false),
            ("缩放增强", layout.FitEnhance, true),
        };

        for (var first = 0; first < slots.Length; first++)
        {
            var (name, rect, isButton) = slots[first];
            if (!Has(rect)) continue;

            if (!InsideSafe(rect, input.Safe, input.Width, input.Height))
                problems.Add(label + name + " 越出安全区 " + RectText(rect));

            if (isButton && (rect.Width < ButtonSize - Epsilon || rect.Height < ButtonSize - Epsilon))
                problems.Add(label + name + " 命中区小于 " + ButtonSize + " " + RectText(rect));

            for (var second = first + 1; second < slots.Length; second++)
            {
                var (otherName, otherRect, _) = slots[second];
                if (Has(otherRect) && Intersects(rect, otherRect))
                    problems.Add(label + name + " 与 " + otherName + " 相交");
            }
        }

        var bars = new[] { layout.TopBar, layout.BottomBar, layout.LeadingBar, layout.TrailingBar }
            .Where(Has)
            .ToArray();

        var avoid = layout.PhotoAvoidance;
        if (avoid.Left < input.Safe.Left - Epsilon
            || avoid.Top < input.Safe.Top - Epsilon
            || avoid.Right < input.Safe.Right - Epsilon
            || avoid.Bottom < input.Safe.Bottom - Epsilon)
        {
            problems.Add(label + "避让小于安全区");
        }

        var adapted = new LayoutRect(
            avoid.Left,
            avoid.Top,
            input.Width - avoid.Left - avoid.Right,
            input.Height - avoid.Top - avoid.Bottom);

        if (!Has(adapted))
            problems.Add(label + "适应区为空");

        if (adapted.X < input.Safe.Left - Epsilon || adapted.Y < input.Safe.Top - Epsilon
            || adapted.MaxX > input.Width - input.Safe.Right + Epsilon
            || adapted.MaxY > input.Height - input.Safe.Bottom + Epsilon)
        {
            problems.Add(label + "适应区越出安全区 " + RectText(adapted));
        }

        var elements = new (string Name, LayoutRect Rect)[]
        {
            ("筛选", layout.Filter),
            ("更多", layout.More),
            ("EXIF", layout.Exif),
            ("上下张", layout.Navigation),
            ("星级", layout.Rating),
            ("缩放增强", layout.FitEnhance),
            ("胶片条", layout.Filmstrip),
            ("分析栏", layout.Analysis),
        };

        foreach (var (name, rect) in elements)
        {
            if (!Has(rect)) continue;

            if (Intersects(rect, adapted))
                problems.Add(label + name + " 与适应区相交");
        }

        foreach (var (name, rect, _) in slots)
        {
            if (!Has(rect)) continue;

            var bare = !bars.Any(bar => Contains(bar, rect));
            if (bare && Intersects(Inflate(rect, BareGap), adapted))
                problems.Add(label + name + " 外扩 " + BareGap + " 后与适应区相交");
        }

        foreach (var bar in bars)
        {
            if (Intersects(bar, adapted))
                problems.Add(label + "栏背景与适应区相交 " + RectText(bar));
        }

        if (slots.Any(slot => Has(slot.Rect))
            && avoid.Left <= Epsilon && avoid.Top <= Epsilon && avoid.Right <= Epsilon && avoid.Bottom <= Epsilon)
        {
            problems.Add(label + "有元素时适应区没有任何避让");
        }

        if (input.Landscape)
        {
            if (Has(layout.TopBar) || Has(layout.BottomBar))
                problems.Add(label + "横屏不应有竖屏顶/底栏背景");
            if (Has(layout.FilterMoreGroup) != (input.FilmVisible || input.ControlVisible))
                problems.Add(label + "左上合体存在性不符");
            if (Has(layout.LeadingBar) != Has(layout.Filmstrip))
                problems.Add(label + "刘海侧栏背景与胶片条不一致");
            if (Has(layout.TrailingBar) != Has(layout.Exif))
                problems.Add(label + "下巴侧栏背景与 EXIF 列不一致");
        }
        else
        {
            if (Has(layout.LeadingBar) || Has(layout.TrailingBar))
                problems.Add(label + "竖屏不应有横屏栏背景");
            if (Has(layout.FilterMoreGroup))
                problems.Add(label + "竖屏不应有左上合体");
            if (Has(layout.TopBar) != input.AnalysisVisible)
                problems.Add(label + "顶栏背景与分析栏不一致");
            if (Has(layout.BottomBar) != Has(layout.Filmstrip))
                problems.Add(label + "底栏背景与胶片条不一致");
        }

        if (Has(layout.Filmstrip) != input.FilmVisible)
            problems.Add(label + "胶片条存在性与可见性不一致");

        if (Has(layout.Analysis) != input.AnalysisVisible)
            problems.Add(label + "分析栏存在性与可见性不一致");

        var exifExpected = input.ControlVisible && input.ExifWidth > 0 && input.ExifHeight > 0;
        if (Has(layout.Exif) != exifExpected)
            problems.Add(label + "EXIF 存在性与显隐/固有尺寸不一致");

        if (Has(layout.Navigation) != (input.ControlVisible && input.ShowNavigation))
            problems.Add(label + "上下张存在性与开关不一致");

        if (Has(layout.Rating) && !input.ControlVisible)
            problems.Add(label + "控制栏隐藏时仍有星级槽位");
    }

    private static void ValidateMirror(
        in PhoneLayoutInput input,
        in PhoneLayout layout,
        List<string> problems,
        string label)
    {
        if (!input.Landscape) return;

        var mirrored = PhoneScreenLayout.Compute(input with { NotchOnLeft = false });
        var pairs = new (string Name, LayoutRect Left, LayoutRect Right)[]
        {
            ("筛选", layout.Filter, mirrored.Filter),
            ("更多", layout.More, mirrored.More),
            ("左上合体", layout.FilterMoreGroup, mirrored.FilterMoreGroup),
            ("EXIF", layout.Exif, mirrored.Exif),
            ("上下张", layout.Navigation, mirrored.Navigation),
            ("星级", layout.Rating, mirrored.Rating),
            ("缩放增强", layout.FitEnhance, mirrored.FitEnhance),
            ("胶片条", layout.Filmstrip, mirrored.Filmstrip),
            ("分析栏", layout.Analysis, mirrored.Analysis),
            ("刘海侧栏", layout.LeadingBar, mirrored.LeadingBar),
            ("下巴侧栏", layout.TrailingBar, mirrored.TrailingBar),
        };

        foreach (var (name, left, right) in pairs)
        {
            if (Has(left) != Has(right))
            {
                problems.Add(label + name + " 刘海左右两向存在性不同");
                continue;
            }

            if (!Has(left)) continue;

            var expected = new LayoutRect(input.Width - left.X - left.Width, left.Y, left.Width, left.Height);
            if (!NearRect(expected, right))
                problems.Add(label + name + " 刘海左右两向不镜像");
        }

        if (!Near(layout.PhotoAvoidance.Left, mirrored.PhotoAvoidance.Right)
            || !Near(layout.PhotoAvoidance.Right, mirrored.PhotoAvoidance.Left)
            || !Near(layout.PhotoAvoidance.Top, mirrored.PhotoAvoidance.Top)
            || !Near(layout.PhotoAvoidance.Bottom, mirrored.PhotoAvoidance.Bottom))
        {
            problems.Add(label + "刘海左右两向避让未交换左右");
        }
    }

    private static void RunRatingChecks(Checks c)
    {
        foreach (var size in PortraitSizes)
        {
            var input = Input(size, landscape: false, notchOnLeft: true,
                filmVisible: true, controlVisible: true, analysisVisible: true,
                showNavigation: true, hasExif: true);
            var withNavigation = PhoneScreenLayout.Compute(input);
            var withoutNavigation = PhoneScreenLayout.Compute(input with { ShowNavigation = false });

            var filled = Has(withNavigation.Rating)
                && Near(withNavigation.Rating.X, withNavigation.Navigation.MaxX + GroupGap)
                && Near(withNavigation.Rating.MaxX, withNavigation.FitEnhance.X - GroupGap)
                && Has(withoutNavigation.Rating)
                && Near(withoutNavigation.Rating.X, size.Safe.Left + EdgeMargin)
                && Near(withoutNavigation.Rating.MaxX, withoutNavigation.FitEnhance.X - GroupGap);

            c.True(size.Name + " 竖屏星级填满两侧按钮之间", filled,
                $"有上下张 {RectText(withNavigation.Rating)}，无上下张 {RectText(withoutNavigation.Rating)}");

            c.True(size.Name + " 竖屏隐藏上下张后星级变宽",
                withoutNavigation.Rating.Width > withNavigation.Rating.Width,
                $"有 {withNavigation.Rating.Width}，无 {withoutNavigation.Rating.Width}");
        }
    }

    private static void RunLandscapeColumnChecks(Checks c)
    {
        foreach (var size in LandscapeSizes)
        {
            var input = Input(size, landscape: true, notchOnLeft: true,
                filmVisible: true, controlVisible: true, analysisVisible: true,
                showNavigation: true, hasExif: true);
            var withNavigation = PhoneScreenLayout.Compute(input);
            var withoutNavigation = PhoneScreenLayout.Compute(input with { ShowNavigation = false });

            var columnStable = NearRect(withNavigation.FitEnhance, withoutNavigation.FitEnhance)
                && NearRect(withNavigation.Rating, withoutNavigation.Rating)
                && NearRect(withNavigation.Exif, withoutNavigation.Exif)
                && NearRect(withNavigation.TrailingBar, withoutNavigation.TrailingBar)
                && NearRect(withNavigation.Analysis, withoutNavigation.Analysis);

            c.True(size.Name + " 横屏控制列几何不随上下张开关变化", columnStable,
                $"有上下张 增强 {RectText(withNavigation.FitEnhance)} 星级 {RectText(withNavigation.Rating)}；"
                + $"无上下张 增强 {RectText(withoutNavigation.FitEnhance)} 星级 {RectText(withoutNavigation.Rating)}");

            var navigationOutside = !Has(withNavigation.Navigation)
                || (!Intersects(withNavigation.Navigation, withNavigation.FitEnhance)
                    && !Intersects(withNavigation.Navigation, withNavigation.Rating));

            c.True(size.Name + " 横屏上下张不在控制列内", navigationOutside,
                $"上下张 {RectText(withNavigation.Navigation)} 控制列 {RectText(withNavigation.FitEnhance)}");

            c.True(size.Name + " 横屏次序 分析栏 < EXIF 列 < 控制列",
                withNavigation.Analysis.X < withNavigation.Exif.X
                    && withNavigation.Exif.X < withNavigation.FitEnhance.X,
                $"分析 {withNavigation.Analysis.X}，EXIF {withNavigation.Exif.X}，控制列 {withNavigation.FitEnhance.X}");
        }
    }

    private static PhoneLayoutInput Input(
        SizeCase size,
        bool landscape,
        bool notchOnLeft,
        bool filmVisible,
        bool controlVisible,
        bool analysisVisible,
        bool showNavigation,
        bool hasExif) => new(
        Landscape: landscape,
        NotchOnLeft: notchOnLeft,
        Width: size.Width,
        Height: size.Height,
        Safe: size.Safe,
        FilmVisible: filmVisible,
        ControlVisible: controlVisible,
        AnalysisVisible: analysisVisible,
        ShowNavigation: showNavigation,
        FilterWidth: FilterWidth,
        FilmBand: FilmBand,
        AnalysisCross: AnalysisCross,
        ExifWidth: hasExif ? (landscape ? ExifWidthLandscape : ExifWidthPortrait) : 0,
        ExifHeight: hasExif ? (landscape ? ExifHeightLandscape : ExifHeightPortrait) : 0,
        ButtonSize: ButtonSize,
        EdgeMargin: EdgeMargin,
        RowInset: RowInset,
        GroupGap: GroupGap,
        BareGap: BareGap);

    private static bool Has(LayoutRect rect) => rect.Width > 0 && rect.Height > 0;

    private static bool Intersects(LayoutRect first, LayoutRect second)
        => first.X < second.MaxX - Epsilon && second.X < first.MaxX - Epsilon
            && first.Y < second.MaxY - Epsilon && second.Y < first.MaxY - Epsilon;

    private static bool Contains(LayoutRect outer, LayoutRect inner)
        => inner.X >= outer.X - Epsilon && inner.Y >= outer.Y - Epsilon
            && inner.MaxX <= outer.MaxX + Epsilon && inner.MaxY <= outer.MaxY + Epsilon;

    private static bool InsideSafe(LayoutRect rect, EdgeInsets safe, double width, double height)
        => rect.X >= safe.Left - Epsilon && rect.Y >= safe.Top - Epsilon
            && rect.MaxX <= width - safe.Right + Epsilon && rect.MaxY <= height - safe.Bottom + Epsilon;

    private static LayoutRect Inflate(LayoutRect rect, double amount)
        => new(rect.X - amount, rect.Y - amount, rect.Width + amount * 2, rect.Height + amount * 2);

    private static bool Near(double first, double second) => Math.Abs(first - second) <= Epsilon;

    private static bool NearRect(LayoutRect first, LayoutRect second)
        => Near(first.X, second.X) && Near(first.Y, second.Y)
            && Near(first.Width, second.Width) && Near(first.Height, second.Height);

    private static string RectText(LayoutRect rect)
        => $"({rect.X:0.##},{rect.Y:0.##},{rect.Width:0.##}×{rect.Height:0.##})";

    private readonly record struct SizeCase(string Name, double Width, double Height, EdgeInsets Safe);
}
