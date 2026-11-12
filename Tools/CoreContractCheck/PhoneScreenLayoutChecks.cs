using PhotoViewer.Core.Design;
using PhotoViewer.Shared;

namespace CoreContractCheck;

/// <summary>
/// 手机形态槽位几何不变量（SHELL-08/09/10/11/12）：在手机竖横屏尺寸网格上核对元素落位与安全区例外、
/// 与主图适应区互不侵占、槽位互不重叠、胶片条贯穿窗口且两端内容留白正确、主图避让的栏缘口径、
/// 贴角元素带的隔离、横屏控制栏的桌面式次序与 EXIF 整项裁剪，以及刘海双向镜像。
/// </summary>
internal static class PhoneScreenLayoutChecks
{
    private const double ButtonSize = DesignTokens.ShellGlassButtonSize;
    private const double EdgeMargin = DesignTokens.ShellPhoneEdgeMargin;
    private const double RowInset = DesignTokens.ShellPhoneRowInset;
    private const double GroupGap = DesignTokens.ShellGlassGroupGap;

    private const double FilterWidth = 120;
    private const double AnalysisCross = 96;
    private const int ExifCount = 4;

    /// <summary>竖屏胶片条为横向小档卡片（带厚 92）。</summary>
    private static readonly double FilmBandPortrait = FilmCardGeometry.BandThickness(FilmCardTier.Small, true);

    /// <summary>横屏胶片条为纵向小档卡片（带厚 80）。</summary>
    private static readonly double FilmBandLandscape = FilmCardGeometry.BandThickness(FilmCardTier.Small, false);

    /// <summary>横屏卡片靠刘海一侧的交叉轴内边距（小档 8）。</summary>
    private static readonly double SmallCardPad = FilmCardGeometry.CrossLead(FilmCardTier.Small, false);

    /// <summary>窄胶片列：带厚窄于「边距 + 两段宽按钮 + 行内间距」，列缘改由按钮决定。</summary>
    private static readonly double NarrowFilmBand = 56;

    /// <summary>横屏刘海侧按钮列到窗口上下缘的贴角距离。</summary>
    private const double LandscapeLeadTop = 18;

    /// <summary>横屏下巴侧控制栏列到窗口上下缘的贴角距离。</summary>
    private const double LandscapeControlTop = 18;

    /// <summary>横屏下巴侧（Home 指示条）系统安全区。</summary>
    private const double LandscapeChinSafe = 20;

    private const double Epsilon = 1e-6;

    private static readonly bool[] Flags = [false, true];

    private static readonly SizeCase[] PortraitSizes =
    [
        PortraitCase("390×844 刘海", 390, 844, 47, 34, 6.33),
        PortraitCase("402×874", 402, 874, 62, 34, 6.33),
        PortraitCase("440×956", 440, 956, 62, 34, 6.33),
        PortraitCase("430×932 圆角窗", 430, 932, 59, 34, 18.33, sideSafe: 12),
        PortraitCase("320×1024", 320, 1024, 24, 20, 8.33),
        PortraitCase("375×1024", 375, 1024, 24, 20, 8.33),
        PortraitCase("507×1024", 507, 1024, 24, 20, 8.33),
    ];

    private static readonly SizeCase[] LandscapeSizes =
    [
        LandscapeCase("844×390", 844, 390, 62, 20),
        LandscapeCase("874×402", 874, 402, 62, 20),
        LandscapeCase("956×440", 956, 440, 62, 20),
        LandscapeCase("1024×320", 1024, 320, 24, 20),
        LandscapeCase("1024×375", 1024, 375, 24, 20),
        LandscapeCase("1024×507", 1024, 507, 24, 20),
    ];

    public static void Run(Checks c)
    {
        c.Section("手机形态槽位几何");

        foreach (var size in PortraitSizes)
            RunGrid(c, size, landscape: false);

        foreach (var size in LandscapeSizes)
            RunGrid(c, size, landscape: true);

        RunRatingChecks(c);
        RunPortraitBottomRowChecks(c);
        RunLandscapeControlBarChecks(c);
        RunExifTrimmingChecks(c);
        RunProbeChecks(c);
        RunIphone17Checks(c);
        RunLeadBandIsolationChecks(c, landscape: false);
        RunLeadBandIsolationChecks(c, landscape: true);
        RunNarrowCardChecks(c);
    }

    private static void RunGrid(Checks c, SizeCase size, bool landscape)
    {
        var containment = new List<string>();
        var complement = new List<string>();
        var bare = new List<string>();
        var avoidance = new List<string>();
        var filmstrip = new List<string>();
        var overlaps = new List<string>();
        var structure = new List<string>();
        var cases = 0;

        foreach (var film in Flags)
        foreach (var control in Flags)
        foreach (var exif in Flags)
        foreach (var analysis in Flags)
        foreach (var navigation in Flags)
        foreach (var hasExif in Flags)
        {
            cases++;
            var input = Input(size, landscape, notchOnLeft: true, film, control, exif, analysis, navigation, hasExif);
            var layout = PhoneScreenLayout.Compute(input);
            var label = $"胶片{(film ? "显" : "隐")}/控制{(control ? "显" : "隐")}"
                + $"/EXIF{(exif ? "开" : "关")}/分析{(analysis ? "显" : "隐")}"
                + $"/上下张{(navigation ? "显" : "隐")}/显示项{(hasExif ? "有" : "无")}：";

            ValidateContainment(input, layout, containment, label);
            ValidateComplement(input, layout, complement, label);
            ValidateBareInset(input, layout, bare, label);
            ValidateAvoidance(input, layout, avoidance, label);
            ValidateFilmstrip(input, layout, filmstrip, label);
            ValidateOverlap(input, layout, overlaps, label);
            ValidateStructure(input, layout, structure, label);
            ValidateMirror(input, layout, structure, label);
        }

        var name = (landscape ? "横屏 " : "竖屏 ") + size.Name;
        c.True($"{name} 的 {cases} 种开关组合：元素在窗口与安全区内（耳区、刘海列与控制列例外）",
            containment.Count == 0, Detail(containment));
        c.True($"{name} 的 {cases} 种开关组合：元素不进入主图适应区（胶片条按内容区判定）",
            complement.Count == 0, Detail(complement));
        c.True($"{name} 的 {cases} 种开关组合：无栏背景的裸元素外扩 RowInset 后仍不进入适应区",
            bare.Count == 0, Detail(bare));
        c.True($"{name} 的 {cases} 种开关组合：主图避让等于栏缘公式", avoidance.Count == 0, Detail(avoidance));
        c.True($"{name} 的 {cases} 种开关组合：胶片条贯穿窗口主轴且两端留白正确",
            filmstrip.Count == 0, Detail(filmstrip));
        c.True($"{name} 的 {cases} 种开关组合：槽位互不相交（横屏胶片条自按钮下方穿过）",
            overlaps.Count == 0, Detail(overlaps));
        c.True($"{name} 的 {cases} 种开关组合：显隐存在性、栏背景与刘海双向镜像",
            structure.Count == 0, Detail(structure));
    }

    private static void ValidateContainment(
        in PhoneLayoutInput input,
        in PhoneLayout layout,
        List<string> problems,
        string label)
    {
        foreach (var (name, rect, isButton) in Buttons(layout))
        {
            if (!Has(rect)) continue;

            if (rect.X < -Epsilon || rect.Y < -Epsilon
                || rect.MaxX > input.Width + Epsilon || rect.MaxY > input.Height + Epsilon)
            {
                problems.Add(label + name + " 越出窗口 " + RectText(rect));
                continue;
            }

            if (isButton && (rect.Width < ButtonSize - Epsilon || rect.Height < ButtonSize - Epsilon))
                problems.Add(label + name + " 命中区小于 " + ButtonSize + " " + RectText(rect));

            if (InsideSafe(rect, input.Safe, input.Width, input.Height)) continue;
            if (AllowedOutsideSafe(input, name, rect)) continue;

            problems.Add(label + name + " 越出安全区 " + RectText(rect));
        }

        if (!Has(layout.Filmstrip)) return;

        var zone = FilmstripCardZone(input, layout);
        if (!Has(zone))
        {
            problems.Add(label + "胶片条内容区为空 " + EdgeInsetsText(layout.FilmstripPadding));
            return;
        }

        if (!CardZoneInsideSafe(input, zone))
            problems.Add(label + "胶片条内容区越出安全区 " + RectText(zone));
    }

    /// <summary>
    /// 三处刻意的安全区例外：竖屏耳区按钮上移进刘海两旁、竖屏底行压进 Home 指示条自带余量、
    /// 横屏刘海侧按钮伸进刘海带与下巴侧控制列不避让对称安全内缩。均只放宽跨安全区的那一条边，
    /// 其余三边仍按安全区或窗口核对。
    /// </summary>
    private static bool AllowedOutsideSafe(in PhoneLayoutInput input, string name, LayoutRect rect)
    {
        var safe = input.Safe;
        bool lateralInside = rect.X >= safe.Left - Epsilon
            && rect.MaxX <= input.Width - safe.Right + Epsilon;

        if (!input.Landscape)
        {
            if (!lateralInside) return false;

            if (name is "筛选" or "更多" && !PhoneScreenLayout.PortraitExifShown(input))
                return rect.Y >= EarFloor(safe.Top) - Epsilon;

            return name is "上下张" or "星级" or "缩放增强"
                && rect.MaxY <= input.Height - BottomGapFloor(input) + Epsilon;
        }

        bool verticallyInside = rect.Y >= safe.Top - Epsilon
            && rect.MaxY <= input.Height - safe.Bottom + Epsilon;

        return name switch
        {
            "筛选" or "更多" or "上下张" => verticallyInside
                && rect.X >= EdgeMargin - Epsilon
                && rect.MaxX <= input.Width - safe.Right + Epsilon,
            "星级" or "缩放增强" => verticallyInside
                && rect.X >= safe.Left - Epsilon
                && rect.MaxX <= input.Width - EdgeMargin + Epsilon,
            "EXIF" => verticallyInside && rect.X >= safe.Left - Epsilon,
            _ => false,
        };
    }

    /// <summary>耳区按钮的上缘下限：贴角带位置与边距中较小者，即耳区公式允许的最贴窗口缘位置。</summary>
    private static double EarFloor(double safeEdge) => Math.Min(EdgeMargin, Math.Max(safeEdge, RowInset));

    /// <summary>底行到窗口下缘的最小距离：Home 指示条安全内缩自带一份行内间距的视觉余量。</summary>
    private static double BottomGapFloor(in PhoneLayoutInput input)
        => Math.Max(input.Safe.Bottom - RowInset, RowInset);

    private static bool CardZoneInsideSafe(in PhoneLayoutInput input, LayoutRect zone)
    {
        var safe = input.Safe;
        bool lateralInside = zone.X >= safe.Left - Epsilon
            && zone.MaxX <= input.Width - safe.Right + Epsilon;
        if (!lateralInside || zone.Y < safe.Top - Epsilon) return false;

        // 胶片条落在最后一行时与底行同一口径：压进 Home 指示条余量，横屏胶片条纵贯整窗。
        return input.Landscape
            ? zone.MaxY <= input.Height - safe.Bottom + Epsilon
            : zone.MaxY <= input.Height - BottomGapFloor(input) + Epsilon;
    }

    private static void ValidateComplement(
        in PhoneLayoutInput input,
        in PhoneLayout layout,
        List<string> problems,
        string label)
    {
        var adapted = AdaptedArea(input, layout);
        if (!Has(adapted))
        {
            problems.Add(label + "适应区为空");
            return;
        }

        if (adapted.X < input.Safe.Left - Epsilon || adapted.Y < input.Safe.Top - Epsilon
            || adapted.MaxX > input.Width + Epsilon || adapted.MaxY > input.Height + Epsilon)
        {
            problems.Add(label + "适应区越过窗口或安全区上/左缘 " + RectText(adapted));
        }

        foreach (var (name, rect, _) in Elements(layout))
        {
            if (Has(rect) && Intersects(rect, adapted))
                problems.Add(label + name + " 与适应区相交");
        }

        if (Has(layout.Filmstrip) && Intersects(FilmstripCardZone(input, layout), adapted))
            problems.Add(label + "胶片条内容区与适应区相交");

        foreach (var bar in Bars(layout))
        {
            if (Intersects(bar, adapted))
                problems.Add(label + "栏背景与适应区相交 " + RectText(bar));
        }
    }

    private static void ValidateBareInset(
        in PhoneLayoutInput input,
        in PhoneLayout layout,
        List<string> problems,
        string label)
    {
        var adapted = AdaptedArea(input, layout);
        if (!Has(adapted)) return;

        var bars = Bars(layout);
        foreach (var (name, rect, _) in Buttons(layout))
        {
            if (!Has(rect)) continue;
            if (bars.Any(bar => Contains(bar, rect))) continue;

            if (Intersects(Inflate(rect, RowInset), adapted))
                problems.Add(label + name + " 外扩 " + RowInset + " 后与适应区相交");
        }
    }

    private static void ValidateAvoidance(
        in PhoneLayoutInput input,
        in PhoneLayout layout,
        List<string> problems,
        string label)
    {
        var avoid = layout.PhotoAvoidance;
        double expectedLeft;
        double expectedTop;
        double expectedRight;
        double expectedBottom;

        if (input.Landscape)
        {
            // 刘海侧：有胶片栏时到列缘，否则到耳区按钮内缘 + 行内间距。
            expectedLeft = Has(layout.Filmstrip)
                ? layout.Filmstrip.MaxX
                : Math.Max(input.Safe.Left, PhoneScreenLayout.LeadStripWidth(input) + RowInset);
            expectedTop = input.Safe.Top;
            // 下巴侧：分析栏 > 控制栏栏缘（最宽一列元素内缘 − 行内间距）> 不避让。
            double columnLeft = Has(layout.Exif) ? Math.Min(layout.Exif.X, layout.FitEnhance.X)
                : Has(layout.FitEnhance) ? layout.FitEnhance.X
                : double.NaN;
            expectedRight = Has(layout.Analysis) ? input.Width - layout.Analysis.X
                : double.IsNaN(columnLeft) ? 0
                : input.Width - (columnLeft - RowInset);
            expectedBottom = 0;
        }
        else
        {
            double rowBottom = Math.Max(layout.Filter.MaxY, layout.More.MaxY);
            expectedLeft = input.Safe.Left;
            expectedTop = Has(layout.TopBar) ? layout.TopBar.MaxY : rowBottom + RowInset;
            expectedRight = input.Safe.Right;
            expectedBottom = Has(layout.BottomBar) ? input.Height - layout.BottomBar.Y
                : input.ControlVisible ? input.Height - (layout.FitEnhance.Y - RowInset)
                : 0;
        }

        if (!Near(avoid.Left, expectedLeft) || !Near(avoid.Top, expectedTop)
            || !Near(avoid.Right, expectedRight) || !Near(avoid.Bottom, expectedBottom))
        {
            problems.Add(label + $"避让 {EdgeInsetsText(avoid)}，应为 "
                + EdgeInsetsText(new EdgeInsets(expectedLeft, expectedTop, expectedRight, expectedBottom)));
        }
    }

    /// <summary>横屏无胶片栏时筛选与更多所在的耳区列起点（与竖屏顶行耳区同一口径）。</summary>
    private static double EarX(in PhoneLayoutInput input)
        => Math.Min(Math.Max(input.Safe.Left, RowInset), Math.Max(input.Safe.Left - ButtonSize, EdgeMargin));

    private static void ValidateFilmstrip(
        in PhoneLayoutInput input,
        in PhoneLayout layout,
        List<string> problems,
        string label)
    {
        var filmstrip = layout.Filmstrip;
        var padding = layout.FilmstripPadding;

        if (input.Landscape)
        {
            double leadTop = Math.Max(input.Safe.Top, input.LeadBand.Top) + RowInset;
            double leadBottomEdge = input.Height
                - Math.Max(Math.Max(input.Safe.Bottom, input.LeadBand.Bottom), RowInset);

            if (!Near(padding.Left, 0) || !Near(padding.Right, 0))
                problems.Add(label + "横屏胶片条交叉轴留白应为 0 " + EdgeInsetsText(padding));

            double expectedTop = Has(layout.FilterMoreGroup)
                ? layout.FilterMoreGroup.MaxY + RowInset
                : leadTop;
            if (!Near(padding.Top, expectedTop))
                problems.Add(label + $"横屏胶片条上留白 {padding.Top:0.##}，应为 {expectedTop:0.##}");

            double expectedBottom = Has(layout.Navigation)
                ? input.Height - layout.Navigation.Y + RowInset
                : input.Height - leadBottomEdge;
            if (!Near(padding.Bottom, expectedBottom))
                problems.Add(label + $"横屏胶片条下留白 {padding.Bottom:0.##}，应为 {expectedBottom:0.##}");

            if (!Has(filmstrip)) return;

            if (!Near(filmstrip.Y, 0) || !Near(filmstrip.MaxY, input.Height))
                problems.Add(label + "横屏胶片条未贯穿窗口高度 " + RectText(filmstrip));
            if (!Near(filmstrip.Width, input.FilmBand))
                problems.Add(label + "横屏胶片条厚度不等于 FilmBand " + RectText(filmstrip));
            if (Has(layout.LeadingBar) && !Near(filmstrip.MaxX, layout.LeadingBar.MaxX))
                problems.Add(label + "横屏胶片条外缘应等于刘海侧栏缘 " + RectText(filmstrip));

            return;
        }

        double controlLeft = Math.Max(input.Safe.Left, input.ControlBand.Left) + EdgeMargin;
        double controlRight = input.Width - Math.Max(input.Safe.Right, input.ControlBand.Right) - EdgeMargin;

        if (!Near(padding.Top, 0) || !Near(padding.Bottom, 0))
            problems.Add(label + "竖屏胶片条交叉轴留白应为 0 " + EdgeInsetsText(padding));

        if (!Near(padding.Left, controlLeft))
            problems.Add(label + $"竖屏胶片条左留白 {padding.Left:0.##}，应为 {controlLeft:0.##}");

        if (!Near(padding.Right, input.Width - controlRight))
            problems.Add(label + $"竖屏胶片条右留白 {padding.Right:0.##}，应为 {input.Width - controlRight:0.##}");

        if (Has(layout.Navigation) && !Near(padding.Left, layout.Navigation.X))
            problems.Add(label + "竖屏胶片条左留白应等于上下张起点");

        if (Has(layout.FitEnhance) && !Near(padding.Right, input.Width - layout.FitEnhance.MaxX))
            problems.Add(label + "竖屏胶片条右留白应等于缩放增强到窗口末端的距离");

        if (!Has(filmstrip)) return;

        if (!Near(filmstrip.X, 0) || !Near(filmstrip.MaxX, input.Width))
            problems.Add(label + "竖屏胶片条未贯穿窗口宽度 " + RectText(filmstrip));
        if (!Near(filmstrip.Height, input.FilmBand))
            problems.Add(label + "竖屏胶片条高度不等于 FilmBand " + RectText(filmstrip));

        double bottomGap = Math.Max(
            Math.Max(input.Safe.Bottom - RowInset, Math.Min(controlLeft, input.Width - controlRight)), RowInset);
        double expectedFilmBottom = input.ControlVisible
            ? layout.FitEnhance.Y - RowInset
            : input.Height - bottomGap;
        if (!Near(filmstrip.MaxY, expectedFilmBottom))
            problems.Add(label + $"竖屏胶片条下缘 {filmstrip.MaxY:0.##}，应为 {expectedFilmBottom:0.##}");
    }

    private static void ValidateOverlap(
        in PhoneLayoutInput input,
        in PhoneLayout layout,
        List<string> problems,
        string label)
    {
        var rects = new List<(string Name, LayoutRect Rect)>();
        foreach (var (name, rect, _) in Elements(layout))
        {
            if (Has(rect)) rects.Add((name, rect));
        }

        if (Has(layout.Filmstrip)) rects.Add(("胶片条", layout.Filmstrip));

        for (var first = 0; first < rects.Count; first++)
        for (var second = first + 1; second < rects.Count; second++)
        {
            if (ScrollsUnderButtons(input, rects[first].Name, rects[second].Name)) continue;

            if (Intersects(rects[first].Rect, rects[second].Rect))
            {
                problems.Add(label + rects[first].Name + " 与 " + rects[second].Name + " 相交 "
                    + RectText(rects[first].Rect) + " / " + RectText(rects[second].Rect));
            }
        }
    }

    /// <summary>横屏胶片条纵贯整窗并自刘海侧按钮下方穿过，按设计不与这两类按钮相斥。</summary>
    private static bool ScrollsUnderButtons(in PhoneLayoutInput input, string first, string second)
    {
        if (!input.Landscape) return false;

        var other = first == "胶片条" ? second : second == "胶片条" ? first : null;
        return other is "筛选" or "更多" or "上下张";
    }

    private static void ValidateStructure(
        in PhoneLayoutInput input,
        in PhoneLayout layout,
        List<string> problems,
        string label)
    {
        var safe = input.Safe;

        // 筛选与更多常驻：不再随胶片栏、控制栏或 EXIF 开关隐藏。
        if (!Has(layout.Filter) || !Has(layout.More))
            problems.Add(label + "筛选与更多应常驻");

        if (Has(layout.Filmstrip) != input.FilmVisible)
            problems.Add(label + "胶片条存在性与可见性不一致");

        if (Has(layout.Analysis) != input.AnalysisVisible)
            problems.Add(label + "分析栏存在性与可见性不一致");

        bool navigationExpected = input.Landscape
            ? input.FilmVisible && input.ShowNavigation
            : input.ControlVisible && input.ShowNavigation;
        if (Has(layout.Navigation) != navigationExpected)
            problems.Add(label + (input.Landscape ? "横屏上下张应只跟胶片栏开关" : "竖屏上下张应只跟控制栏开关"));

        if (Has(layout.Rating) != input.ControlVisible)
            problems.Add(label + "星级存在性与控制栏开关不一致");

        if (Has(layout.FitEnhance) != input.ControlVisible)
            problems.Add(label + "缩放增强存在性与控制栏开关不一致");

        if (input.Landscape)
            ValidateLandscapeStructure(input, layout, problems, label, safe);
        else
            ValidatePortraitStructure(input, layout, problems, label);

        if (Buttons(layout).Any(slot => Has(slot.Rect))
            && layout.PhotoAvoidance.Left <= Epsilon && layout.PhotoAvoidance.Top <= Epsilon
            && layout.PhotoAvoidance.Right <= Epsilon && layout.PhotoAvoidance.Bottom <= Epsilon)
        {
            problems.Add(label + "有元素时适应区没有任何避让");
        }
    }

    private static void ValidatePortraitStructure(
        in PhoneLayoutInput input,
        in PhoneLayout layout,
        List<string> problems,
        string label)
    {
        if (Has(layout.LeadingBar) || Has(layout.TrailingBar))
            problems.Add(label + "竖屏不应有横屏栏背景");

        if (Has(layout.FilterMoreGroup))
            problems.Add(label + "竖屏不应有刘海侧合体按钮");

        if (Has(layout.TopBar) != input.AnalysisVisible)
            problems.Add(label + "顶栏背景与分析栏不一致");

        if (Has(layout.BottomBar) != Has(layout.Filmstrip))
            problems.Add(label + "底栏背景与胶片条不一致");

        if (!Near(layout.Filter.Y, PhoneScreenLayout.TopRowY(input))
            || !Near(layout.More.Y, PhoneScreenLayout.TopRowY(input)))
        {
            problems.Add(label + $"顶行上缘 {layout.Filter.Y:0.##}，应为 {PhoneScreenLayout.TopRowY(input):0.##}");
        }

        if (!Has(layout.Exif))
        {
            if (layout.ExifItems != 0) problems.Add(label + "无 EXIF 块时显示项应为 0");
            return;
        }

        // 竖屏单条件：EXIF 开关打开且有显示项，与控制栏开关无关。
        if (!PhoneScreenLayout.PortraitExifShown(input))
            problems.Add(label + "EXIF 块存在性与竖屏单条件不一致");
        if (layout.ExifItems != input.ExifCount)
            problems.Add(label + $"竖屏 EXIF 显示项 {layout.ExifItems}，应为 {input.ExifCount}");

        double intervalLeft = layout.Filter.MaxX + GroupGap;
        double intervalRight = layout.More.X - GroupGap;
        double expectedWidth = Math.Min(input.ExifWidth, intervalRight - intervalLeft);
        if (!Near(layout.Exif.Width, expectedWidth))
            problems.Add(label + $"横向 EXIF 宽 {layout.Exif.Width:0.##}，应为 {expectedWidth:0.##}");
        if (layout.Exif.X < intervalLeft - Epsilon || layout.Exif.MaxX > intervalRight + Epsilon)
            problems.Add(label + "横向 EXIF 越出筛选与更多之间的区间 " + RectText(layout.Exif));
        if (!Near(layout.Exif.Y + layout.Exif.Height / 2, layout.Filter.Y + ButtonSize / 2))
            problems.Add(label + "横向 EXIF 未与顶行同轴");
    }

    private static void ValidateLandscapeStructure(
        in PhoneLayoutInput input,
        in PhoneLayout layout,
        List<string> problems,
        string label,
        EdgeInsets safe)
    {
        if (Has(layout.TopBar) || Has(layout.BottomBar))
            problems.Add(label + "横屏不应有竖屏顶/底栏背景");

        if (Has(layout.LeadingBar) != Has(layout.Filmstrip))
            problems.Add(label + "刘海侧栏背景与胶片条不一致");
        if (Has(layout.TrailingBar) != Has(layout.Exif))
            problems.Add(label + "下巴侧栏背景与 EXIF 列不一致");

        if (Has(layout.LeadingBar)
            && (!Near(layout.LeadingBar.X, 0) || !Near(layout.LeadingBar.Y, 0)
                || !Near(layout.LeadingBar.MaxY, input.Height)
                || !Near(layout.LeadingBar.MaxX, layout.Filmstrip.MaxX)))
        {
            problems.Add(label + "刘海侧栏背景未覆盖按钮列并与胶片条同宽 " + RectText(layout.LeadingBar));
        }

        double leadTop = Math.Max(input.Safe.Top, input.LeadBand.Top) + RowInset;
        double leadBottomEdge = input.Height
            - Math.Max(Math.Max(input.Safe.Bottom, input.LeadBand.Bottom), RowInset);

        if (input.FilmVisible)
        {
            double columnRight = PhoneScreenLayout.LeadStripWidth(input);
            if (!Has(layout.FilterMoreGroup))
            {
                problems.Add(label + "有胶片栏时筛选与更多应合成宽按钮组");
            }
            else
            {
                if (!Near(layout.FilterMoreGroup.MaxX, columnRight - RowInset))
                    problems.Add(label + "合体按钮右缘未贴列缘 " + RectText(layout.FilterMoreGroup));
                if (!Near(layout.FilterMoreGroup.X, layout.Filter.X)
                    || !Near(layout.FilterMoreGroup.MaxX, layout.More.MaxX)
                    || !Near(layout.FilterMoreGroup.Y, layout.Filter.Y)
                    || !Near(layout.FilterMoreGroup.MaxY, layout.More.MaxY))
                {
                    problems.Add(label + "合体按钮外接矩形不等于筛选与更多两段 " + RectText(layout.FilterMoreGroup));
                }

                if (Has(layout.Navigation) && !Near(layout.Navigation.MaxX, layout.FilterMoreGroup.MaxX))
                    problems.Add(label + "刘海侧合体按钮与上下张未右对齐同一列边");
            }
        }
        else
        {
            if (Has(layout.FilterMoreGroup))
                problems.Add(label + "无胶片栏时不应有合体按钮");

            double earX = EarX(input);
            if (!Near(layout.Filter.X, earX) || !Near(layout.More.X, earX))
                problems.Add(label + $"耳区筛选与更多同一列，当前 {layout.Filter.X:0.##}/{layout.More.X:0.##}，应为 {earX:0.##}");
            if (!Near(layout.Filter.Y, leadTop))
                problems.Add(label + "耳区筛选应贴刘海上方 " + RectText(layout.Filter));
            if (!Near(layout.More.MaxY, leadBottomEdge))
                problems.Add(label + "耳区更多应贴刘海下方 " + RectText(layout.More));
        }

        if (!input.ControlVisible) return;

        // 横屏 EXIF 是控制栏内的双条件：控制栏可见、EXIF 开关打开且有显示项。
        bool exifExpected = PhoneScreenLayout.LandscapeExifShown(input);
        if (!Has(layout.Exif))
        {
            if (layout.ExifItems != 0) problems.Add(label + "无 EXIF 列时显示项应为 0");
            if (exifExpected) problems.Add(label + "控制栏可见且有显示项时应有 EXIF 列");
            return;
        }

        if (!exifExpected)
            problems.Add(label + "EXIF 列存在性与横屏双条件不一致");
        if (layout.ExifItems <= 0 || layout.ExifItems > input.ExifCount)
            problems.Add(label + $"横屏 EXIF 显示项 {layout.ExifItems} 越出 1..{input.ExifCount}");

        if (!Near(layout.Exif.Width, input.ExifWidth))
            problems.Add(label + "横屏 EXIF 列宽应取无衬底固有宽 " + RectText(layout.Exif));
        if (!Near(layout.Exif.X + layout.Exif.Width / 2, layout.FitEnhance.X + ButtonSize / 2))
            problems.Add(label + "横屏 EXIF 列未与按钮同中轴");
        if (!Near(layout.Exif.Y, Math.Max(input.Safe.Top, input.ControlBand.Top) + RowInset)
            || layout.Exif.MaxY > layout.Rating.Y + Epsilon)
        {
            problems.Add(label + "横屏 EXIF 列应在控制栏顶端且不压星级 " + RectText(layout.Exif));
        }

        if (!Near(layout.TrailingBar.X, Math.Min(layout.Exif.X, layout.FitEnhance.X) - RowInset)
            || !Near(layout.TrailingBar.MaxX, input.Width))
        {
            problems.Add(label + "控制栏背景应在最宽一列元素内缘外扩前起画 " + RectText(layout.TrailingBar));
        }

        // 控制列外缘距窗口缘 EdgeMargin，且不避让对称安全内缩。
        if (!Near(layout.FitEnhance.MaxX, input.Width - EdgeMargin)
            || !Near(layout.Rating.MaxX, layout.FitEnhance.MaxX))
        {
            problems.Add(label + "控制列外缘应距窗口缘 EdgeMargin " + RectText(layout.FitEnhance));
        }

        if (layout.FitEnhance.X < safe.Left - Epsilon)
            problems.Add(label + "控制列越出刘海侧安全区 " + RectText(layout.FitEnhance));
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
            ("合体按钮", layout.FilterMoreGroup, mirrored.FilterMoreGroup),
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

        if (layout.ExifItems != mirrored.ExifItems)
            problems.Add(label + "刘海左右两向 EXIF 显示项不同");

        if (!Near(layout.PhotoAvoidance.Left, mirrored.PhotoAvoidance.Right)
            || !Near(layout.PhotoAvoidance.Right, mirrored.PhotoAvoidance.Left)
            || !Near(layout.PhotoAvoidance.Top, mirrored.PhotoAvoidance.Top)
            || !Near(layout.PhotoAvoidance.Bottom, mirrored.PhotoAvoidance.Bottom))
        {
            problems.Add(label + "刘海左右两向避让未交换左右");
        }

        if (!Near(layout.FilmstripPadding.Left, mirrored.FilmstripPadding.Right)
            || !Near(layout.FilmstripPadding.Right, mirrored.FilmstripPadding.Left)
            || !Near(layout.FilmstripPadding.Top, mirrored.FilmstripPadding.Top)
            || !Near(layout.FilmstripPadding.Bottom, mirrored.FilmstripPadding.Bottom))
        {
            problems.Add(label + "刘海左右两向胶片条留白未交换左右");
        }
    }

    private static void RunRatingChecks(Checks c)
    {
        foreach (var size in PortraitSizes)
        {
            var input = Input(size, landscape: false, notchOnLeft: true,
                filmVisible: true, controlVisible: true, exifVisible: true, analysisVisible: true,
                showNavigation: true, hasExifItems: true);
            var withNavigation = PhoneScreenLayout.Compute(input);
            var withoutNavigation = PhoneScreenLayout.Compute(input with { ShowNavigation = false });
            double controlLeft = ControlLeft(size);

            var filled = Has(withNavigation.Rating)
                && Near(withNavigation.Rating.X, withNavigation.Navigation.MaxX + GroupGap)
                && Near(withNavigation.Rating.MaxX, withNavigation.FitEnhance.X - GroupGap)
                && Has(withoutNavigation.Rating)
                && Near(withoutNavigation.Rating.X, controlLeft)
                && Near(withoutNavigation.Rating.MaxX, withoutNavigation.FitEnhance.X - GroupGap);

            c.True(size.Name + " 竖屏星级填满两侧按钮之间", filled,
                $"有上下张 {RectText(withNavigation.Rating)}，无上下张 {RectText(withoutNavigation.Rating)}");

            c.True(size.Name + " 竖屏隐藏上下张后星级变宽",
                withoutNavigation.Rating.Width > withNavigation.Rating.Width,
                $"有 {withNavigation.Rating.Width}，无 {withoutNavigation.Rating.Width}");
        }
    }

    private static void RunPortraitBottomRowChecks(Checks c)
    {
        foreach (var size in PortraitSizes)
        {
            var baseline = Input(size, landscape: false, notchOnLeft: true,
                filmVisible: true, controlVisible: true, exifVisible: true, analysisVisible: true,
                showNavigation: true, hasExifItems: true);
            var layout = PhoneScreenLayout.Compute(baseline);
            var noFilm = PhoneScreenLayout.Compute(baseline with { FilmVisible = false });
            var noExif = PhoneScreenLayout.Compute(baseline with { ExifVisible = false });
            var problems = new List<string>();

            SameRect(problems, "上下张（胶片栏关闭）", layout.Navigation, noFilm.Navigation);
            SameRect(problems, "星级（胶片栏关闭）", layout.Rating, noFilm.Rating);
            SameRect(problems, "缩放增强（胶片栏关闭）", layout.FitEnhance, noFilm.FitEnhance);
            SameRect(problems, "上下张（EXIF 关闭）", layout.Navigation, noExif.Navigation);
            SameRect(problems, "星级（EXIF 关闭）", layout.Rating, noExif.Rating);
            SameRect(problems, "缩放增强（EXIF 关闭）", layout.FitEnhance, noExif.FitEnhance);

            c.True(size.Name + " 竖屏底行几何不随胶片栏与 EXIF 开关变化",
                problems.Count == 0, Detail(problems));
        }
    }

    private static void RunLandscapeControlBarChecks(Checks c)
    {
        foreach (var size in LandscapeSizes)
        {
            var input = Input(size, landscape: true, notchOnLeft: true,
                filmVisible: true, controlVisible: true, exifVisible: true, analysisVisible: true,
                showNavigation: true, hasExifItems: true);
            var layout = PhoneScreenLayout.Compute(input);
            var noFilm = PhoneScreenLayout.Compute(input with { FilmVisible = false });
            var noNavigation = PhoneScreenLayout.Compute(input with { ShowNavigation = false });
            var noBoth = PhoneScreenLayout.Compute(input with { FilmVisible = false, ShowNavigation = false });
            var problems = new List<string>();

            foreach (var (name, other) in new[] { ("胶片栏关闭", noFilm), ("上下张关闭", noNavigation), ("两者都关闭", noBoth) })
            {
                SameRect(problems, "缩放增强（" + name + "）", layout.FitEnhance, other.FitEnhance);
                SameRect(problems, "星级（" + name + "）", layout.Rating, other.Rating);
                SameRect(problems, "EXIF（" + name + "）", layout.Exif, other.Exif);
                SameRect(problems, "控制栏背景（" + name + "）", layout.TrailingBar, other.TrailingBar);
                SameRect(problems, "分析栏（" + name + "）", layout.Analysis, other.Analysis);
                if (other.ExifItems != layout.ExifItems)
                    problems.Add($"EXIF 显示项（{name}）{other.ExifItems} ≠ {layout.ExifItems}");
            }

            c.True(size.Name + " 横屏控制栏几何不随胶片栏与上下张开关变化",
                problems.Count == 0, Detail(problems));

            c.True(size.Name + " 横屏分析栏紧贴控制栏栏缘且在控制列内侧",
                Near(layout.Analysis.MaxX, layout.TrailingBar.X)
                    && layout.Analysis.MaxX <= layout.Exif.X + Epsilon
                    && layout.Analysis.MaxX <= layout.FitEnhance.X + Epsilon,
                $"分析 {RectText(layout.Analysis)}，栏缘 {layout.TrailingBar.X:0.##}，EXIF {RectText(layout.Exif)}");

            c.True(size.Name + " 横屏控制列外缘贴 EdgeMargin 且星级不短于紧凑态最短长度",
                Near(layout.FitEnhance.MaxX, size.Width - EdgeMargin)
                    && Near(layout.Rating.MaxX, size.Width - EdgeMargin)
                    && layout.Rating.Height >= RatingControlGeometry.CompactMinLength - Epsilon,
                $"增强 {RectText(layout.FitEnhance)}，星级 {RectText(layout.Rating)}");

            var noExif = PhoneScreenLayout.Compute(input with { ExifVisible = false });
            c.True(size.Name + " 横屏无 EXIF 时不画控制栏背景且分析栏贴按钮内缘",
                !Has(noExif.TrailingBar) && !Has(noExif.Exif)
                    && Near(noExif.Analysis.MaxX, noExif.FitEnhance.X - RowInset),
                $"栏背景 {RectText(noExif.TrailingBar)}，分析 {RectText(noExif.Analysis)}");

            var noControl = PhoneScreenLayout.Compute(input with { ControlVisible = false });
            c.True(size.Name + " 横屏控制栏关闭时星级与 EXIF 一起消失且分析栏止于下巴侧安全区",
                !Has(noControl.Rating) && !Has(noControl.Exif) && !Has(noControl.TrailingBar)
                    && Near(noControl.Analysis.MaxX, size.Width - size.Safe.Right),
                $"分析 {RectText(noControl.Analysis)}");
        }
    }

    private static void RunExifTrimmingChecks(Checks c)
    {
        var size = LandscapeSizes.Single(caseSize => caseSize.Name == "874×402");
        var four = ExifLayout(size, 4);
        var six = ExifLayout(size, 6);
        var nine = ExifLayout(size, 9);
        double pitch = ExifPitch(6);

        c.True("横屏 874×402 四项 EXIF 全部显示且星级 102",
            four.ExifItems == 4 && Near(four.Rating.Height, 102),
            $"显示项 {four.ExifItems}，星级 {RectText(four.Rating)}");

        c.True("横屏 874×402 六项 EXIF 按整项裁到四项",
            six.ExifItems == 4 && six.ExifItems < 6,
            $"显示项 {six.ExifItems}，EXIF {RectText(six.Exif)}");

        c.True("横屏 874×402 裁剪后 EXIF 高 = 4 × 38 − 2",
            Near(six.Exif.Height, 4 * pitch - ExifBlockGeometry.ItemSpacing),
            $"高 {six.Exif.Height:0.##}");

        c.True("横屏 874×402 九项仍裁到四项且 EXIF 列不变",
            nine.ExifItems == 4 && NearRect(nine.Exif, six.Exif),
            $"显示项 {nine.ExifItems}，EXIF {RectText(nine.Exif)}");

        c.True("横屏 874×402 裁剪后星级仍保住紧凑态最短长度",
            six.Rating.Height >= RatingControlGeometry.CompactMinLength - Epsilon,
            $"星级高 {six.Rating.Height:0.##}");

        var shortSize = LandscapeSizes.Single(caseSize => caseSize.Name == "1024×320");
        var shortLayout = ExifLayout(shortSize, 4);
        c.True("横屏 1024×320 四项 EXIF 裁到两项且星级不短于紧凑态最短长度",
            shortLayout.ExifItems == 2
                && Near(shortLayout.Exif.Height, 2 * ExifPitch(4) - ExifBlockGeometry.ItemSpacing)
                && shortLayout.Rating.Height >= RatingControlGeometry.CompactMinLength - Epsilon,
            $"显示项 {shortLayout.ExifItems}，EXIF {RectText(shortLayout.Exif)}，"
            + $"星级 {RectText(shortLayout.Rating)}");
    }

    private static PhoneLayout ExifLayout(SizeCase size, int exifCount)
        => PhoneScreenLayout.Compute(Input(size, landscape: true, notchOnLeft: true,
            filmVisible: true, controlVisible: true, exifVisible: true, analysisVisible: true,
            showNavigation: true, hasExifItems: true, exifCount: exifCount));

    private static double ExifPitch(int exifCount)
        => (ExifBlockGeometry.Size(exifCount, false, bare: true).Height + ExifBlockGeometry.ItemSpacing) / exifCount;

    private static void RunProbeChecks(Checks c)
    {
        foreach (var size in PortraitSizes)
        {
            var input = Input(size, landscape: false, notchOnLeft: true,
                filmVisible: true, controlVisible: true, exifVisible: true, analysisVisible: true,
                showNavigation: true, hasExifItems: true);
            var withExif = PhoneScreenLayout.Compute(input);
            var withoutExif = PhoneScreenLayout.Compute(input with { ExifVisible = false });

            c.True(size.Name + " 竖屏顶行探测位置与实际行位置一致",
                Near(PhoneScreenLayout.TopRowY(input), withExif.Filter.Y)
                    && Near(PhoneScreenLayout.TopRowY(input with { ExifVisible = false }), withoutExif.Filter.Y),
                $"带 EXIF {withExif.Filter.Y:0.##}，不带 {withoutExif.Filter.Y:0.##}");
        }

        foreach (var size in LandscapeSizes)
        {
            var input = Input(size, landscape: true, notchOnLeft: true,
                filmVisible: true, controlVisible: true, exifVisible: true, analysisVisible: true,
                showNavigation: true, hasExifItems: true);
            var layout = PhoneScreenLayout.Compute(input);
            var ear = PhoneScreenLayout.Compute(input with { FilmVisible = false });

            c.True(size.Name + " 横屏刘海列与控制列探测宽度与实际列缘一致",
                Near(PhoneScreenLayout.LeadStripWidth(input), layout.Filmstrip.MaxX)
                    && Near(PhoneScreenLayout.LeadStripWidth(input with { FilmVisible = false }),
                        ear.Filter.MaxX)
                    && Near(size.Width - PhoneScreenLayout.ControlStripWidth(input), layout.Exif.X),
                $"刘海列 {PhoneScreenLayout.LeadStripWidth(input):0.##}，"
                + $"耳区列 {PhoneScreenLayout.LeadStripWidth(input with { FilmVisible = false }):0.##}，"
                + $"控制列 {PhoneScreenLayout.ControlStripWidth(input):0.##}");
        }
    }

    private static void RunIphone17Checks(Checks c)
    {
        var portrait = PortraitSizes.Single(size => size.Name == "402×874");
        var portraitInput = Input(portrait, landscape: false, notchOnLeft: true,
            filmVisible: true, controlVisible: true, exifVisible: true, analysisVisible: true,
            showNavigation: true, hasExifItems: true);
        var portraitLayout = PhoneScreenLayout.Compute(portraitInput);
        var portraitEar = PhoneScreenLayout.Compute(portraitInput with { ExifVisible = false });

        c.True("iPhone 17 竖屏 402×874：带 EXIF 时顶行贴灵动岛下缘 62",
            Near(portraitLayout.Filter.Y, 62) && Near(portraitLayout.More.Y, 62)
                && Near(portraitLayout.Exif.Y, 62),
            $"筛选 {RectText(portraitLayout.Filter)}，EXIF {RectText(portraitLayout.Exif)}");

        c.True("iPhone 17 竖屏 402×874：不带 EXIF 时筛选与更多上移到耳区 18、下缘 62",
            Near(portraitEar.Filter.Y, 18) && Near(portraitEar.More.Y, 18)
                && Near(portraitEar.Filter.MaxY, 62),
            $"筛选 {RectText(portraitEar.Filter)}");

        c.True("iPhone 17 竖屏 402×874：底行压进 Home 指示条余量、底距 26",
            Near(portraitLayout.Navigation.Y, 804)
                && Near(portrait.Height - portraitLayout.FitEnhance.MaxY, 26),
            $"上下张 {RectText(portraitLayout.Navigation)}，增强 {RectText(portraitLayout.FitEnhance)}");

        c.True("iPhone 17 竖屏 402×874：底行两端距窗口缘 22.33（贴角带 6.33 + 边距 16）",
            Near(portraitLayout.Navigation.X, 22.33)
                && Near(portrait.Width - portraitLayout.FitEnhance.MaxX, 22.33),
            $"上下张 {RectText(portraitLayout.Navigation)}，增强 {RectText(portraitLayout.FitEnhance)}");

        c.True("iPhone 17 竖屏 402×874：顶行 EXIF 在筛选与更多之间等分压缩到 190",
            Near(portraitLayout.Exif.X, 144) && Near(portraitLayout.Exif.Width, 190)
                && Near(portraitLayout.Exif.Height, 44) && portraitLayout.ExifItems == ExifCount,
            $"EXIF {RectText(portraitLayout.Exif)}，显示项 {portraitLayout.ExifItems}");

        var landscape = LandscapeSizes.Single(size => size.Name == "874×402");
        var landscapeInput = Input(landscape, landscape: true, notchOnLeft: true,
            filmVisible: true, controlVisible: true, exifVisible: true, analysisVisible: true,
            showNavigation: true, hasExifItems: true);
        var landscapeLayout = PhoneScreenLayout.Compute(landscapeInput);

        c.True("iPhone 17 横屏 874×402：控制列外缘距窗口右缘 16（858）",
            Near(landscapeLayout.FitEnhance.MaxX, 858)
                && Near(landscapeLayout.Rating.MaxX, 858),
            $"增强 {RectText(landscapeLayout.FitEnhance)}");

        c.True("iPhone 17 横屏 874×402：控制列自上而下 EXIF 26/星级 184/增强 294，星级 102",
            Near(landscapeLayout.Exif.Y, 26) && Near(landscapeLayout.Rating.Y, 184)
                && Near(landscapeLayout.Rating.Height, 102) && Near(landscapeLayout.FitEnhance.Y, 294)
                && Near(landscapeLayout.FitEnhance.Height, 88),
            $"EXIF {RectText(landscapeLayout.Exif)}，星级 {RectText(landscapeLayout.Rating)}，"
            + $"增强 {RectText(landscapeLayout.FitEnhance)}");

        c.True("iPhone 17 横屏 874×402：EXIF 无衬底列 52 宽、与按钮同中轴 836",
            Near(landscapeLayout.Exif.Width, 52)
                && Near(landscapeLayout.Exif.X + landscapeLayout.Exif.Width / 2, 836)
                && landscapeLayout.ExifItems == ExifCount,
            $"EXIF {RectText(landscapeLayout.Exif)}，显示项 {landscapeLayout.ExifItems}");

        c.True("iPhone 17 横屏 874×402：卡片贴刘海安全区 62（胶片条 54 + 内边距 8）",
            Near(landscapeLayout.Filmstrip.X, 54)
                && Near(landscapeLayout.Filmstrip.X + SmallCardPad, 62),
            $"胶片条 {RectText(landscapeLayout.Filmstrip)}");

        c.True("iPhone 17 横屏 874×402：合体按钮与上下张右对齐列缘 126、上缘 26",
            Near(landscapeLayout.FilterMoreGroup.MaxX, 126)
                && Near(landscapeLayout.Navigation.MaxX, 126)
                && Near(landscapeLayout.FilterMoreGroup.Y, 26),
            $"合体 {RectText(landscapeLayout.FilterMoreGroup)}，上下张 {RectText(landscapeLayout.Navigation)}");

        c.True("iPhone 17 横屏 874×402：胶片条上下留白 78 / 72",
            Near(landscapeLayout.FilmstripPadding.Top, 78)
                && Near(landscapeLayout.FilmstripPadding.Bottom, 72),
            EdgeInsetsText(landscapeLayout.FilmstripPadding));

        var landscapeEar = PhoneScreenLayout.Compute(landscapeInput with { FilmVisible = false });
        c.True("iPhone 17 横屏 874×402：无胶片栏时耳区按钮在 18、主图避让左 70",
            Near(landscapeEar.Filter.X, 18) && Near(landscapeEar.More.X, 18)
                && Near(landscapeEar.PhotoAvoidance.Left, 70),
            $"筛选 {RectText(landscapeEar.Filter)}，避让 {EdgeInsetsText(landscapeEar.PhotoAvoidance)}");
    }

    private static void RunLeadBandIsolationChecks(Checks c, bool landscape)
    {
        foreach (var size in landscape ? LandscapeSizes : PortraitSizes)
        {
            var input = Input(size, landscape, notchOnLeft: true,
                filmVisible: true, controlVisible: true, exifVisible: true, analysisVisible: true,
                showNavigation: true, hasExifItems: true);
            var baseline = PhoneScreenLayout.Compute(input);
            var problems = new List<string>();
            string name;

            if (landscape)
            {
                var widened = input with
                {
                    LeadBand = new EdgeInsets(input.LeadBand.Left, 50, input.LeadBand.Right, input.LeadBand.Bottom),
                };
                var layout = PhoneScreenLayout.Compute(widened);
                name = "横屏 " + size.Name + " 刘海贴角带加大后只移动刘海侧按钮组与内容留白";

                if (!Near(layout.FilterMoreGroup.Y, 50 + RowInset))
                {
                    problems.Add($"合体按钮上缘 {layout.FilterMoreGroup.Y:0.##}，应为 {50 + RowInset:0.##}");
                }

                if (Near(layout.FilterMoreGroup.Y, baseline.FilterMoreGroup.Y))
                    problems.Add("合体按钮未随贴角带下移");

                if (!Near(layout.FilmstripPadding.Top, layout.FilterMoreGroup.MaxY + RowInset))
                    problems.Add("胶片条上留白未跟随合体按钮下缘");

                SameRect(problems, "缩放增强", baseline.FitEnhance, layout.FitEnhance);
                SameRect(problems, "星级", baseline.Rating, layout.Rating);
                SameRect(problems, "EXIF", baseline.Exif, layout.Exif);
                SameRect(problems, "分析栏", baseline.Analysis, layout.Analysis);
                SameRect(problems, "下巴侧栏", baseline.TrailingBar, layout.TrailingBar);
                SameRect(problems, "上下张", baseline.Navigation, layout.Navigation);
                SameRect(problems, "胶片条", baseline.Filmstrip, layout.Filmstrip);
                SameAvoidance(problems, baseline.PhotoAvoidance, layout.PhotoAvoidance);
            }
            else
            {
                var widened = input with
                {
                    LeadBand = new EdgeInsets(80, input.LeadBand.Top, input.LeadBand.Right, input.LeadBand.Bottom),
                };
                var layout = PhoneScreenLayout.Compute(widened);
                name = "竖屏 " + size.Name + " 顶行贴角带加大后只移动筛选";

                if (!Near(layout.Filter.X, 80 + EdgeMargin))
                    problems.Add($"筛选左缘 {layout.Filter.X:0.##}，应为 {80 + EdgeMargin:0.##}");

                if (Near(layout.Filter.X, baseline.Filter.X))
                    problems.Add("筛选未随顶行贴角带内移");

                SameRect(problems, "更多", baseline.More, layout.More);
                SameRect(problems, "上下张", baseline.Navigation, layout.Navigation);
                SameRect(problems, "星级", baseline.Rating, layout.Rating);
                SameRect(problems, "缩放增强", baseline.FitEnhance, layout.FitEnhance);
                SameRect(problems, "胶片条", baseline.Filmstrip, layout.Filmstrip);
                SamePadding(problems, baseline.FilmstripPadding, layout.FilmstripPadding);
                SameAvoidance(problems, baseline.PhotoAvoidance, layout.PhotoAvoidance);
            }

            c.True(name, problems.Count == 0, Detail(problems));
        }
    }

    private static void RunNarrowCardChecks(Checks c)
    {
        foreach (var size in LandscapeSizes)
        {
            var narrow = Input(size, landscape: true, notchOnLeft: true,
                filmVisible: true, controlVisible: true, exifVisible: true, analysisVisible: true,
                showNavigation: true, hasExifItems: true, filmBand: NarrowFilmBand);
            var layout = PhoneScreenLayout.Compute(narrow);
            double narrowRight = LeadColumnRight(narrow);
            double buttonRight = EdgeMargin + ButtonSize * 2 + RowInset;
            var problems = new List<string>();

            if (!Near(narrowRight, buttonRight))
            {
                problems.Add($"窄带列缘 {narrowRight:0.##} 应由按钮决定 {buttonRight:0.##}");
            }

            if (!(layout.FilterMoreGroup.X < narrow.Safe.Left - Epsilon))
            {
                problems.Add($"合体按钮左缘 {layout.FilterMoreGroup.X:0.##}"
                    + $" 未进入刘海侧安全区 {narrow.Safe.Left:0.##}");
            }

            if (layout.FilterMoreGroup.X < EdgeMargin - Epsilon)
            {
                problems.Add($"合体按钮左缘 {layout.FilterMoreGroup.X:0.##} 距窗口缘小于 {EdgeMargin}");
            }

            if (!Near(layout.FilterMoreGroup.MaxX, narrowRight - RowInset))
            {
                problems.Add($"合体按钮右缘 {layout.FilterMoreGroup.MaxX:0.##}"
                    + $" ≠ 列缘 {narrowRight - RowInset:0.##}");
            }

            if (!Near(layout.Navigation.MaxX, narrowRight - RowInset))
            {
                problems.Add($"上下张右缘 {layout.Navigation.MaxX:0.##}"
                    + $" ≠ 列缘 {narrowRight - RowInset:0.##}");
            }

            if (!Near(layout.Filmstrip.MaxX, narrowRight))
                problems.Add($"胶片条外缘 {layout.Filmstrip.MaxX:0.##} ≠ 列缘 {narrowRight:0.##}");

            if (layout.Filmstrip.X + SmallCardPad < narrow.Safe.Left - Epsilon)
                problems.Add($"卡片左缘 {layout.Filmstrip.X + SmallCardPad:0.##}"
                    + $" 越进刘海侧安全区 {narrow.Safe.Left:0.##}");

            if (!Near(layout.PhotoAvoidance.Left, narrowRight))
            {
                problems.Add($"主图避让左 {layout.PhotoAvoidance.Left:0.##} ≠ 按钮列缘 {narrowRight:0.##}");
            }

            c.True($"横屏 {size.Name} 窄胶片列（{NarrowFilmBand}）：宽按钮撑出列缘但卡片不越刘海安全区",
                problems.Count == 0, Detail(problems));
        }

        foreach (var size in LandscapeSizes)
        {
            var wide = Input(size, landscape: true, notchOnLeft: true,
                filmVisible: true, controlVisible: true, exifVisible: true, analysisVisible: true,
                showNavigation: true, hasExifItems: true);
            var layout = PhoneScreenLayout.Compute(wide);
            double columnRight = LeadColumnRight(wide);
            bool filmDriven = Near(columnRight, Math.Max(0, wide.Safe.Left - SmallCardPad) + wide.FilmBand);
            var problems = new List<string>();

            if (!Near(layout.Filmstrip.MaxX, columnRight)
                || !Near(layout.FilterMoreGroup.MaxX, columnRight - RowInset)
                || !Near(layout.PhotoAvoidance.Left, columnRight))
            {
                problems.Add($"胶片条 {RectText(layout.Filmstrip)}，合体 {RectText(layout.FilterMoreGroup)}，"
                    + $"避让左 {layout.PhotoAvoidance.Left:0.##}，列缘 {columnRight:0.##}");
            }

            if (layout.Filmstrip.X + SmallCardPad < wide.Safe.Left - Epsilon)
                problems.Add($"卡片左缘 {layout.Filmstrip.X + SmallCardPad:0.##}"
                    + $" 越进刘海侧安全区 {wide.Safe.Left:0.##}");

            if (filmDriven && !Near(layout.Filmstrip.X + SmallCardPad, wide.Safe.Left))
                problems.Add($"卡片左缘 {layout.Filmstrip.X + SmallCardPad:0.##}"
                    + $" 未贴刘海安全区 {wide.Safe.Left:0.##}");

            c.True($"横屏 {size.Name} 标准胶片列（{FilmBandLandscape}）：列缘取带末与按钮的较大者、卡片不越安全区",
                problems.Count == 0, Detail(problems));

            var thickPad = wide with { FilmOuterPad = 20 };
            var thick = PhoneScreenLayout.Compute(thickPad);
            double thickRight = LeadColumnRight(thickPad);
            bool thickFilmDriven = Near(thickRight, Math.Max(0, thickPad.Safe.Left - 20) + thickPad.FilmBand);

            c.True($"横屏 {size.Name} 加大卡片内边距后卡片仍不越刘海安全区",
                thick.Filmstrip.X + 20 >= thickPad.Safe.Left - Epsilon
                    && (!thickFilmDriven || Near(thick.Filmstrip.X + 20, thickPad.Safe.Left))
                    && Near(thick.Filmstrip.MaxX, thickRight),
                $"胶片条 {RectText(thick.Filmstrip)}，卡片左缘 {thick.Filmstrip.X + 20:0.##}，"
                + $"安全区 {thickPad.Safe.Left:0.##}");
        }
    }

    /// <summary>横屏刘海侧列缘：卡片贴安全区后的带末与「边距 + 两段宽按钮 + 行内间距」取大。</summary>
    private static double LeadColumnRight(in PhoneLayoutInput input)
        => Math.Max(
            Math.Max(0, input.Safe.Left - input.FilmOuterPad) + input.FilmBand,
            input.EdgeMargin + input.ButtonSize * 2 + input.RowInset);

    private static SizeCase PortraitCase(
        string name, double width, double height, double cornerTop, double homeIndicator, double rowEnd,
        double sideSafe = 0) => new(
        name,
        width,
        height,
        Safe: new EdgeInsets(sideSafe, cornerTop, sideSafe, homeIndicator),
        LeadBand: new EdgeInsets(sideSafe, cornerTop, sideSafe, 0),
        ControlBand: new EdgeInsets(rowEnd, 0, rowEnd, homeIndicator));

    private static SizeCase LandscapeCase(
        string name, double width, double height, double notchSide, double homeIndicator) => new(
        name,
        width,
        height,
        Safe: new EdgeInsets(notchSide, 0, notchSide, LandscapeChinSafe),
        LeadBand: new EdgeInsets(notchSide, LandscapeLeadTop, 0, LandscapeChinSafe),
        ControlBand: new EdgeInsets(0, LandscapeControlTop, 0, homeIndicator));

    private static PhoneLayoutInput Input(
        SizeCase size,
        bool landscape,
        bool notchOnLeft,
        bool filmVisible,
        bool controlVisible,
        bool exifVisible,
        bool analysisVisible,
        bool showNavigation,
        bool hasExifItems,
        int exifCount = ExifCount,
        double? filmBand = null,
        double? filmOuterPad = null)
    {
        // 竖屏为带衬底的横排块，横屏为直接放在控制栏背景上的无衬底纵排列。
        var exif = hasExifItems
            ? ExifBlockGeometry.Size(exifCount, !landscape, bare: landscape)
            : (Width: 0d, Height: 0d);

        return new(
            Landscape: landscape,
            NotchOnLeft: notchOnLeft,
            Width: size.Width,
            Height: size.Height,
            Safe: size.Safe,
            LeadBand: size.LeadBand,
            ControlBand: size.ControlBand,
            FilmVisible: filmVisible,
            ControlVisible: controlVisible,
            ExifVisible: exifVisible,
            AnalysisVisible: analysisVisible,
            ShowNavigation: showNavigation,
            FilterWidth: FilterWidth,
            FilmBand: filmBand ?? (landscape ? FilmBandLandscape : FilmBandPortrait),
            FilmOuterPad: filmOuterPad ?? SmallCardPad,
            AnalysisCross: AnalysisCross,
            ExifCount: hasExifItems ? exifCount : 0,
            ExifWidth: exif.Width,
            ExifHeight: exif.Height,
            ExifItemSpacing: ExifBlockGeometry.ItemSpacing,
            RatingMinLength: RatingControlGeometry.CompactMinLength,
            ButtonSize: ButtonSize,
            EdgeMargin: EdgeMargin,
            RowInset: RowInset,
            GroupGap: GroupGap);
    }

    private static double ControlLeft(SizeCase size)
        => Math.Max(size.Safe.Left, size.ControlBand.Left) + EdgeMargin;

    private static LayoutRect AdaptedArea(in PhoneLayoutInput input, in PhoneLayout layout)
    {
        var avoid = layout.PhotoAvoidance;
        return new LayoutRect(
            avoid.Left,
            avoid.Top,
            input.Width - avoid.Left - avoid.Right,
            input.Height - avoid.Top - avoid.Bottom);
    }

    /// <summary>胶片条中卡片实际可占的区域：竖屏按主轴留白、横屏按刘海侧卡片内边距各让一侧。</summary>
    private static LayoutRect FilmstripCardZone(in PhoneLayoutInput input, in PhoneLayout layout)
    {
        var filmstrip = layout.Filmstrip;
        var padding = layout.FilmstripPadding;
        return input.Landscape
            ? new LayoutRect(
                filmstrip.X + input.FilmOuterPad,
                filmstrip.Y + padding.Top,
                filmstrip.Width - input.FilmOuterPad * 2,
                filmstrip.Height - padding.Top - padding.Bottom)
            : new LayoutRect(
                filmstrip.X + padding.Left,
                filmstrip.Y,
                filmstrip.Width - padding.Left - padding.Right,
                filmstrip.Height);
    }

    private static (string Name, LayoutRect Rect, bool IsButton)[] Buttons(in PhoneLayout layout) =>
    [
        ("筛选", layout.Filter, true),
        ("更多", layout.More, true),
        ("EXIF", layout.Exif, false),
        ("上下张", layout.Navigation, true),
        ("星级", layout.Rating, false),
        ("缩放增强", layout.FitEnhance, true),
    ];

    private static (string Name, LayoutRect Rect, bool IsButton)[] Elements(in PhoneLayout layout) =>
    [
        ("筛选", layout.Filter, true),
        ("更多", layout.More, true),
        ("EXIF", layout.Exif, false),
        ("上下张", layout.Navigation, true),
        ("星级", layout.Rating, false),
        ("缩放增强", layout.FitEnhance, true),
        ("分析栏", layout.Analysis, false),
    ];

    private static LayoutRect[] Bars(in PhoneLayout layout) =>
        new[] { layout.TopBar, layout.BottomBar, layout.LeadingBar, layout.TrailingBar }
            .Where(Has)
            .ToArray();

    private static void SameRect(List<string> problems, string name, LayoutRect expected, LayoutRect actual)
    {
        if (Has(expected) != Has(actual) || (Has(expected) && !NearRect(expected, actual)))
            problems.Add($"{name} 被开关改动：{RectText(expected)} → {RectText(actual)}");
    }

    private static void SamePadding(List<string> problems, EdgeInsets expected, EdgeInsets actual)
    {
        if (!Near(expected.Left, actual.Left) || !Near(expected.Right, actual.Right)
            || !Near(expected.Top, actual.Top) || !Near(expected.Bottom, actual.Bottom))
        {
            problems.Add($"胶片条留白被贴角带改动：{EdgeInsetsText(expected)} → {EdgeInsetsText(actual)}");
        }
    }

    private static void SameAvoidance(List<string> problems, EdgeInsets expected, EdgeInsets actual)
    {
        if (!Near(expected.Left, actual.Left) || !Near(expected.Right, actual.Right)
            || !Near(expected.Top, actual.Top) || !Near(expected.Bottom, actual.Bottom))
        {
            problems.Add($"主图避让被贴角带改动：{EdgeInsetsText(expected)} → {EdgeInsetsText(actual)}");
        }
    }

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

    private static string Detail(List<string> problems) => string.Join(" | ", problems.Take(12));

    private static string RectText(LayoutRect rect)
        => $"({rect.X:0.##},{rect.Y:0.##},{rect.Width:0.##}×{rect.Height:0.##})";

    private static string EdgeInsetsText(EdgeInsets insets)
        => $"(左 {insets.Left:0.##}，上 {insets.Top:0.##}，右 {insets.Right:0.##}，下 {insets.Bottom:0.##})";

    private readonly record struct SizeCase(
        string Name,
        double Width,
        double Height,
        EdgeInsets Safe,
        EdgeInsets LeadBand,
        EdgeInsets ControlBand);
}
