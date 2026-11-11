using PhotoViewer.Core.Design;
using PhotoViewer.Shared;

namespace CoreContractCheck;

/// <summary>
/// 手机形态槽位几何不变量：在手机竖横屏尺寸网格上核对元素落位与安全区例外、与主图适应区互不侵占、
/// 槽位互不重叠、胶片条贯穿窗口且两端内容留白正确、主图避让的栏缘口径、贴角元素带的隔离、
/// 旋转不变的下巴侧控制列与底行，以及刘海双向镜像。
/// </summary>
internal static class PhoneScreenLayoutChecks
{
    private const double ButtonSize = DesignTokens.ShellGlassButtonSize;
    private const double EdgeMargin = DesignTokens.ShellPhoneEdgeMargin;
    private const double RowInset = DesignTokens.ShellPhoneRowInset;
    private const double GroupGap = DesignTokens.ShellGlassGroupGap;

    private const double FilterWidth = 120;
    private const double FilmBand = 116;
    private const double NarrowFilmBand = 60;
    private const double AnalysisCross = 96;
    private const double ExifWidthPortrait = 120;
    private const double ExifHeightPortrait = 44;
    private const double ExifWidthLandscape = 96;
    private const double ExifHeightLandscape = 160;

    /// <summary>横屏下巴侧（Home 指示条）系统安全区。</summary>
    private const double LandscapeChinSafe = 20;

    /// <summary>横屏刘海侧按钮列到窗口上下缘的贴角距离。</summary>
    private const double LandscapeLeadTop = 18;

    /// <summary>竖屏底行两端到窗口缘的距离（圆角适配后，已含安全区）。</summary>
    private const double PortraitRowEnd = 13.67;

    /// <summary>紧凑竖屏底行两端到窗口缘的距离。</summary>
    private const double CompactRowEnd = 8.33;

    private const double Epsilon = 1e-6;

    private static readonly bool[] Flags = [false, true];

    private static readonly SizeCase[] PortraitSizes =
    [
        PortraitCase("390×844", 390, 844, 62, 34, PortraitRowEnd),
        PortraitCase("402×874", 402, 874, 62, 34, PortraitRowEnd),
        PortraitCase("440×956", 440, 956, 62, 34, PortraitRowEnd),
        PortraitCase("430×932 圆角窗", 430, 932, 59, 34, PortraitRowEnd, sideSafe: 12),
        PortraitCase("320×1024", 320, 1024, 24, 20, CompactRowEnd),
        PortraitCase("375×1024", 375, 1024, 24, 20, CompactRowEnd),
        PortraitCase("507×1024", 507, 1024, 24, 20, CompactRowEnd),
    ];

    private static readonly SizeCase[] LandscapeSizes =
    [
        LandscapeCase("844×390", 844, 390, 62, 34, PortraitRowEnd),
        LandscapeCase("874×402", 874, 402, 62, 34, PortraitRowEnd),
        LandscapeCase("956×440", 956, 440, 62, 34, PortraitRowEnd),
        LandscapeCase("1024×320", 1024, 320, 24, 20, CompactRowEnd),
        LandscapeCase("1024×375", 1024, 375, 24, 20, CompactRowEnd),
        LandscapeCase("1024×507", 1024, 507, 24, 20, CompactRowEnd),
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
        RunRotationChecks(c);
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
        c.True($"{name} 的 {cases} 种显隐与 EXIF 组合：元素在窗口与安全区内（横屏刘海/下巴侧按钮例外）",
            containment.Count == 0, Detail(containment));
        c.True($"{name} 的 {cases} 种显隐与 EXIF 组合：元素不进入主图适应区（胶片条按内容区判定）",
            complement.Count == 0, Detail(complement));
        c.True($"{name} 的 {cases} 种显隐与 EXIF 组合：无栏背景的裸元素外扩 RowInset 后仍不进入适应区",
            bare.Count == 0, Detail(bare));
        c.True($"{name} 的 {cases} 种显隐与 EXIF 组合：主图避让等于栏缘公式", avoidance.Count == 0, Detail(avoidance));
        c.True($"{name} 的 {cases} 种显隐与 EXIF 组合：胶片条贯穿窗口主轴且两端留白正确",
            filmstrip.Count == 0, Detail(filmstrip));
        c.True($"{name} 的 {cases} 种显隐与 EXIF 组合：槽位互不相交（横屏胶片条自按钮下方穿过）",
            overlaps.Count == 0, Detail(overlaps));
        c.True($"{name} 的 {cases} 种显隐与 EXIF 组合：显隐存在性、栏背景与刘海双向镜像",
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
            if (CrossesNotchOrChinEdge(input, name, rect)) continue;

            problems.Add(label + name + " 越出安全区 " + RectText(rect));
        }

        if (!Has(layout.Filmstrip)) return;

        var zone = FilmstripCardZone(input, layout);
        if (!Has(zone))
        {
            problems.Add(label + "胶片条内容区为空 " + EdgeInsetsText(layout.FilmstripPadding));
            return;
        }

        if (!InsideSafe(zone, input.Safe, input.Width, input.Height))
            problems.Add(label + "胶片条内容区越出安全区 " + RectText(zone));

        if (input.Landscape && zone.X < input.Safe.Left - Epsilon)
            problems.Add(label + "胶片条越过刘海侧安全区 " + RectText(zone));
    }

    /// <summary>横屏两处刻意的安全区例外：刘海侧宽按钮组可伸入刘海带，下巴侧控制列可贴到设备底边。</summary>
    private static bool CrossesNotchOrChinEdge(in PhoneLayoutInput input, string name, LayoutRect rect)
    {
        if (!input.Landscape) return false;

        var safe = input.Safe;
        bool verticallyInside = rect.Y >= safe.Top - Epsilon
            && rect.MaxY <= input.Height - safe.Bottom + Epsilon;

        return name switch
        {
            "筛选" or "更多" or "上下张" => verticallyInside
                && rect.X >= EdgeMargin - Epsilon
                && rect.MaxX <= input.Width - safe.Right + Epsilon,
            "缩放增强" or "星级" => rect.X >= safe.Left - Epsilon
                && verticallyInside
                && rect.MaxX <= input.Width - RowInset + Epsilon,
            _ => false,
        };
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

        // 横屏下巴侧与无栏一侧不避让 Home 指示条，主图可延伸到窗口底/右缘；左侧与上侧始终不越过安全区。
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
            double columnRight = Has(layout.FilterMoreGroup) ? layout.FilterMoreGroup.MaxX + RowInset
                : Has(layout.Navigation) ? layout.Navigation.MaxX + RowInset
                : Has(layout.Filmstrip) ? layout.Filmstrip.MaxX
                : double.NaN;

            expectedLeft = double.IsNaN(columnRight) ? input.Safe.Left : columnRight;
            expectedTop = input.Safe.Top;
            expectedRight = Has(layout.Analysis) ? input.Width - layout.Analysis.X
                : Has(layout.TrailingBar) ? input.Width - layout.TrailingBar.X
                : input.ControlVisible ? input.Width - (layout.FitEnhance.X - RowInset)
                : 0;
            expectedBottom = 0;
        }
        else
        {
            bool topRow = Has(layout.Filter) || Has(layout.More);
            double rowBottom = Has(layout.Filter) ? layout.Filter.MaxY
                : Has(layout.More) ? layout.More.MaxY
                : input.Safe.Top;

            expectedLeft = input.Safe.Left;
            expectedTop = Has(layout.TopBar) ? layout.TopBar.MaxY
                : topRow ? rowBottom + RowInset
                : input.Safe.Top;
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

            if (filmstrip.X < input.Safe.Left - Epsilon)
                problems.Add(label + "横屏胶片条越过刘海侧安全区 " + RectText(filmstrip));

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
        if (Has(layout.FitEnhance) && !Near(filmstrip.MaxY, layout.FitEnhance.Y - RowInset))
            problems.Add(label + "竖屏胶片条下缘未与底行保持 RowInset");
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
        if (Has(layout.Filmstrip) != input.FilmVisible)
            problems.Add(label + "胶片条存在性与可见性不一致");

        if (Has(layout.Analysis) != input.AnalysisVisible)
            problems.Add(label + "分析栏存在性与可见性不一致");

        var exifExpected = input.ControlVisible && input.ExifWidth > 0 && input.ExifHeight > 0;
        if (Has(layout.Exif) != exifExpected)
            problems.Add(label + "EXIF 存在性与显隐/固有尺寸不一致");

        if (Has(layout.Navigation) != (input.ControlVisible && input.ShowNavigation))
            problems.Add(label + "上下张存在性与开关不一致");

        if (Has(layout.Rating) != input.ControlVisible)
            problems.Add(label + "星级存在性与控制栏开关不一致");

        if (input.Landscape)
        {
            if (Has(layout.TopBar) || Has(layout.BottomBar))
                problems.Add(label + "横屏不应有竖屏顶/底栏背景");
            if (Has(layout.FilterMoreGroup) != (input.FilmVisible || input.ControlVisible))
                problems.Add(label + "刘海侧合体按钮存在性不符");
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

            if (Has(layout.FilterMoreGroup) && Has(layout.Navigation)
                && !Near(layout.FilterMoreGroup.MaxX, layout.Navigation.MaxX))
            {
                problems.Add(label + "刘海侧合体按钮与上下张未右对齐同一列边");
            }
        }
        else
        {
            if (Has(layout.LeadingBar) || Has(layout.TrailingBar))
                problems.Add(label + "竖屏不应有横屏栏背景");
            if (Has(layout.FilterMoreGroup))
                problems.Add(label + "竖屏不应有刘海侧合体按钮");
            if (Has(layout.TopBar) != input.AnalysisVisible)
                problems.Add(label + "顶栏背景与分析栏不一致");
            if (Has(layout.BottomBar) != Has(layout.Filmstrip))
                problems.Add(label + "底栏背景与胶片条不一致");
        }

        if (Buttons(layout).Any(slot => Has(slot.Rect))
            && layout.PhotoAvoidance.Left <= Epsilon && layout.PhotoAvoidance.Top <= Epsilon
            && layout.PhotoAvoidance.Right <= Epsilon && layout.PhotoAvoidance.Bottom <= Epsilon)
        {
            problems.Add(label + "有元素时适应区没有任何避让");
        }
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
                filmVisible: true, controlVisible: true, analysisVisible: true,
                showNavigation: true, hasExif: true);
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

    private static void RunRotationChecks(Checks c)
    {
        foreach (var size in PortraitSizes)
        {
            var problems = new List<string>();

            var portraitInput = Input(size, landscape: false, notchOnLeft: true,
                filmVisible: true, controlVisible: true, analysisVisible: true,
                showNavigation: false, hasExif: true);
            var portrait = PhoneScreenLayout.Compute(portraitInput);
            double portraitBottomGap = portraitInput.Height - portrait.FitEnhance.MaxY;

            foreach (var notchOnLeft in Flags)
            {
                var landscapeInput = Rotated(size, notchOnLeft,
                    filmVisible: true, controlVisible: true, analysisVisible: true,
                    showNavigation: false, hasExif: true, filmBand: FilmBand);
                var landscape = PhoneScreenLayout.Compute(landscapeInput);
                var side = notchOnLeft ? "刘海在左" : "刘海在右";

                double chinGap = notchOnLeft
                    ? landscapeInput.Width - landscape.FitEnhance.MaxX
                    : landscape.FitEnhance.X;
                if (!Near(chinGap, portraitBottomGap))
                    problems.Add($"{side} 下巴侧留白 {chinGap:0.##} ≠ 竖屏底行留白 {portraitBottomGap:0.##}");

                double columnTop = portraitInput.Width - portrait.FitEnhance.MaxX;
                if (!Near(landscape.FitEnhance.Y, columnTop))
                    problems.Add($"{side} 缩放增强上缘 {landscape.FitEnhance.Y:0.##} ≠ 竖屏行末留白 {columnTop:0.##}");

                if (!Near(landscapeInput.Height - landscape.Rating.MaxY, portrait.Rating.X))
                {
                    problems.Add($"{side} 星级下缘留白 {landscapeInput.Height - landscape.Rating.MaxY:0.##}"
                        + $" ≠ 竖屏星级起点 {portrait.Rating.X:0.##}");
                }

                if (!Near(landscape.Rating.Height, portrait.Rating.Width))
                {
                    problems.Add($"{side} 横屏星级长 {landscape.Rating.Height:0.##}"
                        + $" ≠ 竖屏星级长 {portrait.Rating.Width:0.##}");
                }
            }

            c.True(size.Name + " 旋转后下巴侧控制列与底行位置不变（刘海在左/右）",
                problems.Count == 0, Detail(problems));
        }
    }

    private static void RunLeadBandIsolationChecks(Checks c, bool landscape)
    {
        foreach (var size in landscape ? LandscapeSizes : PortraitSizes)
        {
            var input = Input(size, landscape, notchOnLeft: true,
                filmVisible: true, controlVisible: true, analysisVisible: true,
                showNavigation: true, hasExif: true);
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
                name = "横屏 " + size.Name + " 刘海贴角带加大后只移动左栏按钮组与内容留白";

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
                name = "竖屏 " + size.Name + " 顶行贴角带加大后只移动顶行";

                if (!Near(layout.Filter.X, 80 + EdgeMargin))
                    problems.Add($"筛选左缘 {layout.Filter.X:0.##}，应为 {80 + EdgeMargin:0.##}");

                if (Near(layout.Filter.X, baseline.Filter.X))
                    problems.Add("筛选未随顶行贴角带内移");

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
                filmVisible: true, controlVisible: true, analysisVisible: true,
                showNavigation: true, hasExif: true, filmBand: NarrowFilmBand);
            var layout = PhoneScreenLayout.Compute(narrow);
            double columnRight = Math.Max(narrow.Safe.Left + NarrowFilmBand, EdgeMargin + ButtonSize * 2 + RowInset);
            var problems = new List<string>();

            if (!(layout.FilterMoreGroup.X < narrow.Safe.Left - Epsilon))
            {
                problems.Add($"合体按钮左缘 {layout.FilterMoreGroup.X:0.##}"
                    + $" 未进入刘海侧安全区 {narrow.Safe.Left:0.##}");
            }

            if (layout.FilterMoreGroup.X < EdgeMargin - Epsilon)
            {
                problems.Add($"合体按钮左缘 {layout.FilterMoreGroup.X:0.##} 距窗口缘小于 {EdgeMargin}");
            }

            if (!Near(layout.FilterMoreGroup.MaxX, columnRight - RowInset))
            {
                problems.Add($"合体按钮右缘 {layout.FilterMoreGroup.MaxX:0.##}"
                    + $" ≠ 列边 {columnRight - RowInset:0.##}");
            }

            if (!Near(layout.Navigation.MaxX, columnRight - RowInset))
            {
                problems.Add($"上下张右缘 {layout.Navigation.MaxX:0.##}"
                    + $" ≠ 列边 {columnRight - RowInset:0.##}");
            }

            if (layout.Filmstrip.X < narrow.Safe.Left - Epsilon)
                problems.Add($"胶片条左缘 {layout.Filmstrip.X:0.##} 越过刘海侧安全区 {narrow.Safe.Left:0.##}");

            if (!Near(layout.PhotoAvoidance.Left, columnRight))
            {
                problems.Add($"主图避让左 {layout.PhotoAvoidance.Left:0.##} ≠ 按钮列右缘 {columnRight:0.##}");
            }

            c.True($"横屏 {size.Name} 窄胶片列（{NarrowFilmBand}）：宽按钮进入刘海侧但卡片不越界",
                problems.Count == 0, Detail(problems));

            var wide = Input(size, landscape: true, notchOnLeft: true,
                filmVisible: true, controlVisible: true, analysisVisible: true,
                showNavigation: true, hasExif: true, filmBand: FilmBand);
            var wideLayout = PhoneScreenLayout.Compute(wide);

            c.True($"横屏 {size.Name} 宽胶片列（{FilmBand}）：按钮列右缘 = 安全区 + FilmBand",
                Near(wideLayout.FilterMoreGroup.MaxX, wide.Safe.Left + FilmBand - RowInset)
                    && Near(wideLayout.Filmstrip.X, wide.Safe.Left)
                    && Near(wideLayout.PhotoAvoidance.Left, wide.Safe.Left + FilmBand),
                $"合体右缘 {wideLayout.FilterMoreGroup.MaxX:0.##}，胶片条 {RectText(wideLayout.Filmstrip)}，"
                + $"避让左 {wideLayout.PhotoAvoidance.Left:0.##}");
        }
    }

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
        string name, double width, double height, double notchSide, double homeIndicator, double rowEnd) => new(
        name,
        width,
        height,
        Safe: new EdgeInsets(notchSide, 0, notchSide, LandscapeChinSafe),
        LeadBand: new EdgeInsets(notchSide, LandscapeLeadTop, 0, LandscapeChinSafe),
        ControlBand: new EdgeInsets(rowEnd, 0, rowEnd, homeIndicator));

    private static PhoneLayoutInput Rotated(
        SizeCase portrait,
        bool notchOnLeft,
        bool filmVisible,
        bool controlVisible,
        bool analysisVisible,
        bool showNavigation,
        bool hasExif,
        double filmBand) => new(
        Landscape: true,
        NotchOnLeft: notchOnLeft,
        Width: portrait.Height,
        Height: portrait.Width,
        Safe: new EdgeInsets(portrait.Safe.Top, 0, portrait.Safe.Top, LandscapeChinSafe),
        LeadBand: new EdgeInsets(portrait.Safe.Top, LandscapeLeadTop, 0, LandscapeChinSafe),
        ControlBand: portrait.ControlBand,
        FilmVisible: filmVisible,
        ControlVisible: controlVisible,
        AnalysisVisible: analysisVisible,
        ShowNavigation: showNavigation,
        FilterWidth: FilterWidth,
        FilmBand: filmBand,
        AnalysisCross: AnalysisCross,
        ExifWidth: hasExif ? ExifWidthLandscape : 0,
        ExifHeight: hasExif ? ExifHeightLandscape : 0,
        ButtonSize: ButtonSize,
        EdgeMargin: EdgeMargin,
        RowInset: RowInset,
        GroupGap: GroupGap);

    private static PhoneLayoutInput Input(
        SizeCase size,
        bool landscape,
        bool notchOnLeft,
        bool filmVisible,
        bool controlVisible,
        bool analysisVisible,
        bool showNavigation,
        bool hasExif,
        double filmBand = FilmBand) => new(
        Landscape: landscape,
        NotchOnLeft: notchOnLeft,
        Width: size.Width,
        Height: size.Height,
        Safe: size.Safe,
        LeadBand: size.LeadBand,
        ControlBand: size.ControlBand,
        FilmVisible: filmVisible,
        ControlVisible: controlVisible,
        AnalysisVisible: analysisVisible,
        ShowNavigation: showNavigation,
        FilterWidth: FilterWidth,
        FilmBand: filmBand,
        AnalysisCross: AnalysisCross,
        ExifWidth: hasExif ? (landscape ? ExifWidthLandscape : ExifWidthPortrait) : 0,
        ExifHeight: hasExif ? (landscape ? ExifHeightLandscape : ExifHeightPortrait) : 0,
        ButtonSize: ButtonSize,
        EdgeMargin: EdgeMargin,
        RowInset: RowInset,
        GroupGap: GroupGap);

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

    private static LayoutRect FilmstripCardZone(in PhoneLayoutInput input, in PhoneLayout layout)
    {
        var filmstrip = layout.Filmstrip;
        var padding = layout.FilmstripPadding;
        return input.Landscape
            ? new LayoutRect(
                filmstrip.X,
                filmstrip.Y + padding.Top,
                filmstrip.Width,
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
            problems.Add($"{name} 被贴角带改动：{RectText(expected)} → {RectText(actual)}");
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
