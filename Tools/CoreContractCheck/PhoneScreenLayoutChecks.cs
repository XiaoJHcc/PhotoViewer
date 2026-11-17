using PhotoViewer.Core.Design;
using PhotoViewer.Shared;

namespace CoreContractCheck;

/// <summary>
/// 手机形态槽位几何不变量（SHELL-08/09/10/11/12）：在手机竖横屏尺寸网格上核对元素落位与安全区例外、
/// 与主图适应区互不侵占、槽位互不重叠、胶片条贯穿窗口且两端内容留白正确、主图避让的栏缘口径、
/// 贴角元素带的隔离、横屏左右工具区（控制区独立 64 / 相邻分析区 54、接缝间距由分析区末端持有、按钮列 X 不随开关移动）、
/// 横屏控制栏的桌面式次序与 EXIF 整项裁剪（无衬底容器与按钮等宽同轴、开关不改变列宽）、
/// 横屏控制栏整组在可用纵向区间居中且星级槽位封顶展开态固有长度（末端按钮不贴底、圆角读数不论大小都保留）、
/// 星级开关（竖屏清空评分槽、横屏释放星级占位把剩余长度让给 EXIF 整项），
/// 刘海侧列靠主图侧右对齐（入口组 88、上下张与卡片外框同落 ContentTrailing，列宽再加一份卡片尾侧内边距，
/// 胶片栏与上下张开关都不移动入口组或改变列宽），以及刘海双向镜像。
/// <para>栏与栏之间只有一份 8 点外层间距，不许在共享层叠加第二份：竖屏胶片条下缘直接落在底行上缘
/// （视觉间距来自小档卡片自身的交叉轴内边距 8），分析栏紧贴顶行下缘（瓦片的 8 点上留白在分析栏交叉轴内，
/// 由分析面板自己排），胶片条两端滚动留白取行内间距而非底行按钮的圆角缘距；底栏背景自胶片条上缘铺到窗口下缘。
/// 横屏下巴侧栏背景由分析栏或 EXIF 列决定：分析栏在时从分析栏左缘铺到窗口右缘并纵贯整窗，
/// 控制栏关闭时分析栏贴窗口外缘（栏背景仍整块铺满），只有纯控制栏时完全透明。</para>
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

    /// <summary>竖屏横向小档卡片的交叉轴尾随内边距（小档 8）：胶片条与底行之间唯一的视觉间距。</summary>
    private static readonly double PortraitCardTrail = FilmCardGeometry.CrossTrail(FilmCardTier.Small, true);

    /// <summary>窄胶片列：带末窄于入口宽（10 + 88），列缘改由入口决定。</summary>
    private static readonly double NarrowFilmBand = 44;

    /// <summary>横屏刘海侧入口锚点距窗口缘的内边距（设计值 10，与卡片尾侧内边距同源）。</summary>
    private const double FilmSidePadding = 10;

    /// <summary>横屏刘海侧按钮列到窗口上下缘的贴角距离。</summary>
    private const double LandscapeLeadTop = 18;

    /// <summary>横屏下巴侧控制栏列到窗口上下缘的贴角距离。</summary>
    private const double LandscapeControlTop = 18;

    /// <summary>横屏下巴侧（Home 指示条）系统安全区。</summary>
    private const double LandscapeChinSafe = 20;

    private const double Epsilon = 1e-6;

    /// <summary>工具区内边距（设计值 10，独立运行，不复用生产常量）。</summary>
    private const double ToolPadding = 10;

    /// <summary>命令按钮间距 / 分析栏接缝（设计值 8）。</summary>
    private const double ToolSpacing = 8;

    /// <summary>相邻分组间距（设计值 10）。</summary>
    private const double ToolGroupSpacing = 10;

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
        RunPortraitOuterInsetChecks(c);
        RunLandscapeControlBarChecks(c);
        RunLandscapeControlColumnChecks(c);
        RunLandscapeTrailingBarChecks(c);
        RunExifTrimmingChecks(c);
        RunToolRegionChecks(c);
        RunShowRatingChecks(c);
        RunProbeChecks(c);
        RunIphone17Checks(c);
        RunLeadBandIsolationChecks(c, landscape: false);
        RunLeadBandIsolationChecks(c, landscape: true);
        RunNarrowCardChecks(c);
        RunFilmSideColumnChecks(c);
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
        c.True($"{name} 的 {cases} 种开关组合：元素在窗口与安全区内（竖屏耳区、横屏入口组伸进刘海带与控制列例外）",
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
    /// 横屏刘海侧入口组伸进刘海带（只受入口锚点内边距约束）与下巴侧控制列不避让对称安全内缩。
    /// 均只放宽跨安全区的那一条边，其余三边仍按安全区或窗口核对。
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
                && rect.X >= FilmSidePadding - Epsilon
                && rect.MaxX <= input.Width - safe.Right + Epsilon,
            "星级" or "缩放增强" => verticallyInside
                && rect.X >= safe.Left - Epsilon
                && rect.MaxX <= input.Width - ToolPadding + Epsilon,
            "EXIF" => verticallyInside && rect.X >= safe.Left - Epsilon,
            _ => false,
        };
    }

    /// <summary>耳区按钮的上缘下限：贴角带位置与边距中较小者，即耳区公式允许的最贴窗口缘位置。</summary>
    private static double EarFloor(double safeEdge) => Math.Min(EdgeMargin, Math.Max(safeEdge, RowInset));

    /// <summary>底行到窗口下缘的最小距离：Home 指示条安全内缩自带一份行内间距的视觉余量。</summary>
    private static double BottomGapFloor(in PhoneLayoutInput input)
        => Math.Max(input.Safe.Bottom - RowInset, RowInset);

    /// <summary>竖屏底行到窗口下缘的间距：Home 指示条余量与底行两端贴边取大，夹在一份行内间距之上。</summary>
    private static double BottomGap(in PhoneLayoutInput input)
    {
        double controlLeft = Math.Max(input.Safe.Left, input.ControlBand.Left) + EdgeMargin;
        double controlRight = input.Width - Math.Max(input.Safe.Right, input.ControlBand.Right) - EdgeMargin;
        return Math.Max(
            Math.Max(input.Safe.Bottom - RowInset, Math.Min(controlLeft, input.Width - controlRight)), RowInset);
    }

    private static bool CardZoneInsideSafe(in PhoneLayoutInput input, LayoutRect zone)
    {
        var safe = input.Safe;
        // 竖屏主轴留白取行内间距（8），比圆角侧安全区（如 12）更贴窗口缘：卡片带的侧安全区只为角部圆角给出，
        // 卡片带位于屏幕中段，因此下限取两者较小者，同时不得越出窗口。横屏卡片贴物理刘海，仍按安全区核对。
        double lateralFloor = input.Landscape ? safe.Left : Math.Min(safe.Left, input.RowInset);
        double lateralCeiling = input.Landscape ? safe.Right : Math.Min(safe.Right, input.RowInset);
        bool lateralInside = zone.X >= lateralFloor - Epsilon
            && zone.MaxX <= input.Width - lateralCeiling + Epsilon;
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
            // 刘海侧：到测量列宽（入口内侧 + 卡片尾侧内边距），与胶片栏、上下张开关无关。
            expectedLeft = PhoneScreenLayout.LeadStripWidth(input);
            expectedTop = input.Safe.Top;
            // 下巴侧：完整组合区宽（分析区 + 控制区），与栏背景是否绘制无关；控制栏关闭时只剩分析区。
            expectedRight = (input.ControlVisible ? ControlRegionWidth(input) : 0)
                + (Has(layout.Analysis) ? input.AnalysisCross : 0);
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

    /// <summary>横屏胶片条两端绝对停靠线：首卡在合体按钮下缘 + 一份内边距，末卡在上张上缘上方一份内边距（无上下张时贴底部圆角）。</summary>
    private static double FilmDockTop(in PhoneLayout layout) => layout.FilterMoreGroup.MaxY + FilmSidePadding;

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
            double leadBottomEdge = input.Height
                - Math.Max(input.Safe.Bottom, input.LeadBand.Bottom) - FilmSidePadding;

            if (!Near(padding.Left, 0) || !Near(padding.Right, 0))
                problems.Add(label + "横屏胶片条交叉轴留白应为 0 " + EdgeInsetsText(padding));

            double expectedTop = FilmDockTop(layout);
            if (!Near(padding.Top, expectedTop))
                problems.Add(label + $"横屏胶片条上留白 {padding.Top:0.##}，应为 {expectedTop:0.##}");

            double expectedBottom = Has(layout.Navigation)
                ? input.Height - layout.Navigation.Y + FilmSidePadding
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

        if (!Near(padding.Top, 0) || !Near(padding.Bottom, 0))
            problems.Add(label + "竖屏胶片条交叉轴留白应为 0 " + EdgeInsetsText(padding));

        // 两端内容留白取行内间距，不再跟随底行按钮的内缘或圆角缘距：底行显隐、控制带加宽都不改变它。
        if (!Near(padding.Left, input.RowInset) || !Near(padding.Right, input.RowInset))
            problems.Add(label + "竖屏胶片条两端留白应为行内间距 " + EdgeInsetsText(padding));

        if (!Has(filmstrip)) return;

        if (!Near(filmstrip.X, 0) || !Near(filmstrip.MaxX, input.Width))
            problems.Add(label + "竖屏胶片条未贯穿窗口宽度 " + RectText(filmstrip));
        if (!Near(filmstrip.Height, input.FilmBand))
            problems.Add(label + "竖屏胶片条高度不等于 FilmBand " + RectText(filmstrip));

        // 底行可见时胶片条下缘直接落在底行上缘，共享层不再让出第二份行内间距；
        // 底行隐藏时贴 Home 指示条余量给出的下缘。
        double expectedFilmBottom = input.ControlVisible
            ? layout.FitEnhance.Y
            : input.Height - BottomGap(input);
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

            var firstRect = ToolContent(rects[first].Name, rects[first].Rect, rects[second].Rect);
            var secondRect = ToolContent(rects[second].Name, rects[second].Rect, rects[first].Rect);
            if (Intersects(firstRect, secondRect))
            {
                problems.Add(label + rects[first].Name + " 与 " + rects[second].Name + " 相交 "
                    + RectText(rects[first].Rect) + " / " + RectText(rects[second].Rect));
            }
        }
    }

    /// <summary>
    /// SHELL-12：与控制栏相邻的分析栏交叉轴含朝控制栏的接缝；无衬底 EXIF 透明容器按固有项宽居中，
    /// 可以伸进接缝但不得压到瓦片，故与其他元素比较时去掉朝对方一侧的接缝。
    /// </summary>
    private static LayoutRect ToolContent(string name, LayoutRect rect, LayoutRect other)
    {
        if (name != "分析栏") return rect;
        double seam = DesignTokens.ShellToolRegionSpacing;
        bool otherAfter = other.X + other.Width / 2 > rect.X + rect.Width / 2;
        return otherAfter
            ? new LayoutRect(rect.X, rect.Y, Math.Max(0, rect.Width - seam), rect.Height)
            : new LayoutRect(rect.X + seam, rect.Y, Math.Max(0, rect.Width - seam), rect.Height);
    }

    /// <summary>横屏无衬底 EXIF 列宽：固有项宽，最大不超过按钮与两侧边距之和（TOOLBAR-05）。</summary>
    private static double LandscapeExifWidth(in PhoneLayoutInput input)
        => Math.Min(input.ExifWidth, ButtonSize + ToolPadding * 2);

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

        if (Has(layout.Rating) != (input.ControlVisible && input.ShowRating))
            problems.Add(label + "星级存在性与控制栏开关/评分显示不一致");

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

        if (!Near(layout.Filter.Y, PhoneScreenLayout.TopRowY(input))
            || !Near(layout.More.Y, PhoneScreenLayout.TopRowY(input)))
        {
            problems.Add(label + $"顶行上缘 {layout.Filter.Y:0.##}，应为 {PhoneScreenLayout.TopRowY(input):0.##}");
        }

        // 分析栏直接贴顶行下缘（瓦片自己的 8 点上留白在分析栏交叉轴内，由分析面板排），
        // 顶栏背景覆盖顶行与分析栏并铺满整窗宽；没有分析栏时不画顶栏。
        double rowBottom = layout.Filter.MaxY;
        if (input.AnalysisVisible)
        {
            if (!Near(layout.Analysis.X, 0) || !Near(layout.Analysis.Width, input.Width)
                || !Near(layout.Analysis.Y, rowBottom)
                || !Near(layout.Analysis.Height, input.AnalysisCross))
            {
                problems.Add(label + "竖屏分析栏应铺满窗口宽并紧贴顶行下缘 " + RectText(layout.Analysis));
            }

            if (!Has(layout.TopBar) || !Near(layout.TopBar.X, 0) || !Near(layout.TopBar.Y, 0)
                || !Near(layout.TopBar.Width, input.Width)
                || !Near(layout.TopBar.MaxY, layout.Analysis.MaxY))
            {
                problems.Add(label + "顶栏背景应覆盖顶行与分析栏并铺满窗口宽 " + RectText(layout.TopBar));
            }
        }
        else if (Has(layout.TopBar))
        {
            problems.Add(label + "分析栏隐藏时不应有顶栏背景");
        }

        // 底栏背景自胶片条上缘铺到窗口下缘（不再向下多让一份行内间距）。
        if (Has(layout.Filmstrip))
        {
            if (!Has(layout.BottomBar) || !Near(layout.BottomBar.X, 0)
                || !Near(layout.BottomBar.Width, input.Width)
                || !Near(layout.BottomBar.Y, layout.Filmstrip.Y)
                || !Near(layout.BottomBar.MaxY, input.Height))
            {
                problems.Add(label + "底栏背景应自胶片条上缘铺到窗口下缘 " + RectText(layout.BottomBar));
            }
        }
        else if (Has(layout.BottomBar))
        {
            problems.Add(label + "胶片条隐藏时不应有底栏背景");
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

        // 下巴侧栏背景由分析栏或 EXIF 列决定，与控制栏自身的显示无关，须在控制栏提前返回之前核对。
        ValidateTrailingBar(input, layout, problems, label);

        if (Has(layout.LeadingBar)
            && (!Near(layout.LeadingBar.X, 0) || !Near(layout.LeadingBar.Y, 0)
                || !Near(layout.LeadingBar.MaxY, input.Height)
                || !Near(layout.LeadingBar.MaxX, layout.Filmstrip.MaxX)))
        {
            problems.Add(label + "刘海侧栏背景未覆盖按钮列并与胶片条同宽 " + RectText(layout.LeadingBar));
        }

        double leadTop = Math.Max(input.Safe.Top, input.LeadBand.Top) + FilmSidePadding;
        double leadBottomEdge = input.Height
            - Math.Max(input.Safe.Bottom, input.LeadBand.Bottom) - FilmSidePadding;
        var column = ColumnMeasure(input);

        // 入口组（筛选 + 更多，88）常驻且不随胶片栏开关拆分，右缘与卡片外框、上下张同落 ContentTrailing。
        if (!Has(layout.FilterMoreGroup))
        {
            problems.Add(label + "横屏筛选与更多应合成 88 宽按钮组（不随胶片栏开关拆分）");
        }
        else
        {
            if (!Near(layout.FilterMoreGroup.MaxX, column.ContentTrailing))
                problems.Add(label + "合体按钮右缘未贴入口锚点 " + RectText(layout.FilterMoreGroup));
            if (!Near(layout.FilterMoreGroup.X, layout.Filter.X)
                || !Near(layout.FilterMoreGroup.MaxX, layout.More.MaxX)
                || !Near(layout.FilterMoreGroup.Y, layout.Filter.Y)
                || !Near(layout.FilterMoreGroup.MaxY, layout.More.MaxY))
            {
                problems.Add(label + "合体按钮外接矩形不等于筛选与更多两段 " + RectText(layout.FilterMoreGroup));
            }

            if (!Near(layout.FilterMoreGroup.Y, leadTop))
                problems.Add(label + "入口组未贴刘海侧贴角带 " + RectText(layout.FilterMoreGroup));

            if (Has(layout.Navigation)
                && (!Near(layout.Navigation.MaxX, column.ContentTrailing)
                    || !Near(layout.Navigation.MaxY, leadBottomEdge)))
            {
                problems.Add(label + "上下张未与入口组右对齐同一列边或未贴下侧贴角带 "
                    + RectText(layout.Navigation));
            }
        }

        if (Has(layout.Filmstrip))
        {
            double cardTrailing = layout.Filmstrip.X + input.FilmBand - input.FilmOuterPad;
            double cardLeading = layout.Filmstrip.X + input.FilmOuterPad;
            if (!Near(cardTrailing, column.ContentTrailing))
                problems.Add(label + "卡片外框右缘未与入口组同落 ContentTrailing " + RectText(layout.Filmstrip));
            if (cardLeading < input.Safe.Left - Epsilon)
                problems.Add(label + "卡片外框越进刘海侧安全区 " + RectText(layout.Filmstrip));
        }

        if (!input.ControlVisible) return;

        // 控制区（EXIF 可见项 + 星级 + 两按钮）同轴、整组在可用纵向区间居中，星级槽位封顶展开态固有长度。
        double columnTop = Math.Max(input.Safe.Top, input.ControlBand.Top) + ToolPadding;
        double columnBottom = input.Height - Math.Max(input.Safe.Bottom, input.ControlBand.Bottom) - ToolPadding;
        if (Has(layout.Rating) && Has(layout.FitEnhance))
        {
            double groupTop = Has(layout.Exif) ? layout.Exif.Y : layout.Rating.Y;
            if (!Near(groupTop - columnTop, columnBottom - layout.FitEnhance.MaxY)
                || groupTop < columnTop - Epsilon || layout.FitEnhance.MaxY > columnBottom + Epsilon)
            {
                problems.Add(label + "横屏控制栏整组未在可用纵向区间居中 " + RectText(layout.FitEnhance));
            }

            if ((Has(layout.Exif) && layout.Exif.MaxY > layout.Rating.Y + Epsilon)
                || layout.Rating.MaxY > layout.FitEnhance.Y + Epsilon)
                problems.Add(label + "横屏控制列次序应为 EXIF → 星级 → 缩放增强 " + RectText(layout.Rating));
            if (layout.Rating.Height > RatingControlGeometry.ExpandedLength + Epsilon)
                problems.Add(label + $"横屏星级槽位 {layout.Rating.Height:0.##} 超过展开态固有长度");
            if (!Has(layout.Exif)
                && !Near(layout.Rating.Height, Math.Min(RatingControlGeometry.ExpandedLength,
                    columnBottom - columnTop - ToolGroupSpacing - ButtonSize * 2)))
                problems.Add(label + $"横屏无 EXIF 时星级槽位 {layout.Rating.Height:0.##} 不等于剩余长度封顶");
        }

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

        if (!Near(layout.Exif.Width, LandscapeExifWidth(input)))
            problems.Add(label + "横屏 EXIF 无衬底列宽应取固有项宽且不超过按钮与两侧边距之和 " + RectText(layout.Exif));
        if (!Near(layout.Exif.X + layout.Exif.Width / 2, layout.FitEnhance.X + ButtonSize / 2))
            problems.Add(label + "横屏 EXIF 列未与按钮同中轴");
        if (Has(layout.Rating) && layout.Exif.MaxY > layout.Rating.Y + Epsilon)
            problems.Add(label + "横屏 EXIF 列压到星级 " + RectText(layout.Exif));

        // 控制按钮列外缘距窗口缘一份工具区内边距，且不避让对称安全内缩。
        if (!Near(layout.FitEnhance.MaxX, input.Width - ToolPadding)
            || (Has(layout.Rating) && !Near(layout.Rating.MaxX, layout.FitEnhance.MaxX)))
        {
            problems.Add(label + "控制列外缘应距窗口缘工具区内边距 " + RectText(layout.FitEnhance));
        }

        if (layout.FitEnhance.X < safe.Left - Epsilon)
            problems.Add(label + "控制列越出刘海侧安全区 " + RectText(layout.FitEnhance));
    }

    /// <summary>
    /// 横屏下巴侧栏背景：分析栏或 EXIF 列任一存在即绘制——有分析栏时从分析栏左缘起，只有 EXIF 列时从控制列缘起，
    /// 一律铺到窗口右缘并纵贯整窗；只有纯控制栏（无分析栏、无 EXIF）时完全透明。
    /// </summary>
    private static void ValidateTrailingBar(
        in PhoneLayoutInput input,
        in PhoneLayout layout,
        List<string> problems,
        string label)
    {
        bool expected = input.AnalysisVisible || Has(layout.Exif);
        if (Has(layout.TrailingBar) != expected)
        {
            problems.Add(label + "下巴侧栏背景应由分析栏或 EXIF 列决定 " + RectText(layout.TrailingBar));
            return;
        }

        if (!expected) return;

        double expectedLeft = Has(layout.Analysis)
            ? layout.Analysis.X
            : input.Width - ControlRegionWidth(input);

        if (!Near(layout.TrailingBar.X, expectedLeft) || !Near(layout.TrailingBar.MaxX, input.Width)
            || !Near(layout.TrailingBar.Y, 0) || !Near(layout.TrailingBar.MaxY, input.Height))
        {
            problems.Add(label + $"控制栏背景应从 {expectedLeft:0.##} 铺到窗口右缘并纵贯整窗 "
                + RectText(layout.TrailingBar));
        }

        foreach (var (name, rect) in ControlColumn(layout))
        {
            if (Has(rect) && !Contains(layout.TrailingBar, rect))
                problems.Add(label + name + " 未被控制栏背景覆盖 " + RectText(rect));
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

            c.True(size.Name + " 横屏分析栏紧贴控制区栏缘并铺满控制栏背景左缘",
                Near(layout.Analysis.X, layout.TrailingBar.X)
                    && Near(layout.Analysis.MaxX, layout.FitEnhance.X)
                    && Near(layout.TrailingBar.MaxX, size.Width)
                    && Near(layout.TrailingBar.Y, 0)
                    && Near(layout.TrailingBar.MaxY, size.Height),
                $"分析 {RectText(layout.Analysis)}，栏缘 {layout.TrailingBar.X:0.##}，"
                + $"背景 {RectText(layout.TrailingBar)}，EXIF {RectText(layout.Exif)}");

            c.True(size.Name + " 横屏控制列外缘距窗口缘一份工具区内边距且星级不短于紧凑态最短长度",
                Near(layout.FitEnhance.MaxX, size.Width - ToolPadding)
                    && Near(layout.Rating.MaxX, size.Width - ToolPadding)
                    && layout.Rating.Height >= RatingControlGeometry.CompactMinLength - Epsilon,
                $"增强 {RectText(layout.FitEnhance)}，星级 {RectText(layout.Rating)}");

            var noExif = PhoneScreenLayout.Compute(input with { ExifVisible = false });
            c.True(size.Name + " 横屏无 EXIF 时控制栏背景改从分析栏左缘起画、分析栏贴控制按钮列缘",
                !Has(noExif.Exif)
                    && Near(noExif.Analysis.MaxX, noExif.FitEnhance.X)
                    && Near(noExif.TrailingBar.X, noExif.Analysis.X)
                    && Near(noExif.TrailingBar.MaxX, size.Width)
                    && Near(noExif.TrailingBar.MaxY, size.Height),
                $"栏背景 {RectText(noExif.TrailingBar)}，分析 {RectText(noExif.Analysis)}");

            // EXIF 开关不改变控制列宽：无衬底容器按固有项宽与按钮同轴，只收起那一列并让整组重新居中。
            c.True(size.Name + " 横屏 EXIF 开关不改变控制列宽（分析栏、星级列、右栏背景与主图避让）",
                Near(layout.Exif.Width, LandscapeExifWidth(input))
                    && Near(layout.Exif.X + layout.Exif.Width / 2, layout.FitEnhance.X + ButtonSize / 2)
                    && Near(layout.FitEnhance.X, noExif.FitEnhance.X)
                    && Near(layout.FitEnhance.Width, noExif.FitEnhance.Width)
                    && SameRectQuiet(layout.Analysis, noExif.Analysis)
                    && SameRectQuiet(layout.TrailingBar, noExif.TrailingBar)
                    && Near(layout.Rating.X, noExif.Rating.X) && Near(layout.Rating.MaxX, noExif.Rating.MaxX)
                    && Near(layout.PhotoAvoidance.Right, noExif.PhotoAvoidance.Right),
                $"EXIF {RectText(layout.Exif)} / {RectText(noExif.Exif)}，"
                + $"分析 {RectText(layout.Analysis)} / {RectText(noExif.Analysis)}，避让右 {layout.PhotoAvoidance.Right:0.##} / {noExif.PhotoAvoidance.Right:0.##}");

            var noAnalysis = PhoneScreenLayout.Compute(input with { AnalysisVisible = false });
            c.True(size.Name + " 横屏分析开关只改变控制区前导内边距，不移动按钮列或避让口径",
                Near(noAnalysis.FitEnhance.X, layout.FitEnhance.X)
                    && Near(noAnalysis.Rating.X, layout.Rating.X)
                    && Near(noAnalysis.Exif.X, layout.Exif.X)
                    && Near(noAnalysis.TrailingBar.X,
                        size.Width - ControlRegionWidth(input with { AnalysisVisible = false }))
                    && Near(noAnalysis.PhotoAvoidance.Right,
                        ControlRegionWidth(input with { AnalysisVisible = false })),
                $"按钮列 {layout.FitEnhance.X:0.##}/{noAnalysis.FitEnhance.X:0.##}，"
                + $"栏背景 {RectText(layout.TrailingBar)} / {RectText(noAnalysis.TrailingBar)}，"
                + $"避让右 {layout.PhotoAvoidance.Right:0.##} / {noAnalysis.PhotoAvoidance.Right:0.##}");

            var noControl = PhoneScreenLayout.Compute(input with { ControlVisible = false });
            c.True(size.Name + " 横屏控制栏关闭时星级与 EXIF 一起消失、分析栏贴窗口外缘且右栏背景仍铺满",
                !Has(noControl.Rating) && !Has(noControl.Exif) && !Has(noControl.FitEnhance)
                    && Near(noControl.Analysis.MaxX, size.Width)
                    && Near(noControl.Analysis.X, size.Width - AnalysisCross)
                    && Near(noControl.TrailingBar.X, noControl.Analysis.X)
                    && Near(noControl.TrailingBar.MaxX, size.Width)
                    && Near(noControl.TrailingBar.MaxY, size.Height)
                    && Near(noControl.PhotoAvoidance.Right, AnalysisCross),
                $"分析 {RectText(noControl.Analysis)}，栏背景 {RectText(noControl.TrailingBar)}");
        }
    }

    /// <summary>
    /// 横屏控制栏的纵向口径（SHELL-09）：EXIF 可见项 + 星级实际长度 + 两按钮同轴成组，整组在「该列原生圆角适应后的
    /// 可用纵向区间」内各留一份工具区内边距并居中；星级槽位取剩余长度并封顶展开态固有长度（224），
    /// 余量留在整组两端，末端按钮不贴底。圆角读数不论大小（含小于 Home 手势条余量的值）都原样收紧区间，
    /// 不做减法或阈值归零；刘海左右两向镜像同口径。
    /// </summary>
    private static void RunLandscapeControlColumnChecks(Checks c)
    {
        foreach (var size in LandscapeSizes)
        {
            // 横屏主屏把底部手势条安全区归一为 0（宿主口径）：可用区间下缘只由该带圆角读数与工具区内边距决定。
            var baseInput = Input(size, landscape: true, notchOnLeft: true,
                filmVisible: true, controlVisible: true, exifVisible: false, analysisVisible: true,
                showNavigation: true, hasExifItems: true);
            var input = baseInput with { Safe = baseInput.Safe with { Bottom = 0 } };
            var layout = PhoneScreenLayout.Compute(input);
            var mirrored = PhoneScreenLayout.Compute(input with { NotchOnLeft = false });
            double columnTop = Math.Max(input.Safe.Top, input.ControlBand.Top) + ToolPadding;
            double columnBottom = size.Height - input.ControlBand.Bottom - ToolPadding;
            double expected = Math.Min(
                RatingControlGeometry.ExpandedLength,
                Math.Max(0, columnBottom - columnTop - ToolGroupSpacing - ButtonSize * 2));
            double topMargin = layout.Rating.Y - columnTop;
            double bottomMargin = columnBottom - layout.FitEnhance.MaxY;

            c.True(size.Name + " 横屏无 EXIF 长列：星级封顶 224、整组居中，封顶后末端不贴底",
                Near(layout.Rating.Height, expected)
                    && layout.Rating.Height <= RatingControlGeometry.ExpandedLength + Epsilon
                    && Near(topMargin, bottomMargin) && topMargin >= -Epsilon
                    && (expected < RatingControlGeometry.ExpandedLength - Epsilon || bottomMargin > Epsilon),
                $"星级 {RectText(layout.Rating)}，增强 {RectText(layout.FitEnhance)}，"
                + $"上/下余量 {topMargin:0.##}/{bottomMargin:0.##}，期望槽位 {expected:0.##}");

            c.True(size.Name + " 横屏无 EXIF 长列：刘海两向镜像纵向口径一致",
                Near(layout.FitEnhance.Y, mirrored.FitEnhance.Y)
                    && Near(layout.Rating.Height, mirrored.Rating.Height)
                    && Near(size.Width - mirrored.Rating.MaxX, layout.Rating.X)
                    && Near(size.Width - mirrored.Rating.X, layout.Rating.MaxX),
                $"左 {RectText(layout.Rating)} / 增强 {RectText(layout.FitEnhance)}，"
                + $"右 {RectText(mirrored.Rating)} / 增强 {RectText(mirrored.FitEnhance)}");

            // EXIF 开关：无衬底列按固有项宽与按钮同轴，整组随 EXIF 出现与否重新居中。
            var withExif = PhoneScreenLayout.Compute(input with { ExifVisible = true });
            double exifTopMargin = withExif.Exif.Y - columnTop;
            double exifBottomMargin = columnBottom - withExif.FitEnhance.MaxY;
            c.True(size.Name + " 横屏 EXIF 开关保持同轴、整组重新居中",
                Has(withExif.Exif)
                    && Near(withExif.Exif.Width, LandscapeExifWidth(input))
                    && Near(withExif.Exif.X + withExif.Exif.Width / 2, withExif.FitEnhance.X + ButtonSize / 2)
                    && Near(withExif.Rating.X, layout.Rating.X)
                    && Near(withExif.Rating.MaxX, layout.Rating.MaxX)
                    && Near(exifTopMargin, exifBottomMargin),
                $"EXIF {RectText(withExif.Exif)}，星级 {RectText(withExif.Rating)}，"
                + $"增强 {RectText(withExif.FitEnhance)}，上/下余量 {exifTopMargin:0.##}/{exifBottomMargin:0.##}");

            var cornerProblems = new List<string>();
            foreach (double corner in new[] { 6d, 20d, 26d, 34d })
            {
                var cornered = PhoneScreenLayout.Compute(input with
                {
                    ControlBand = input.ControlBand with { Bottom = corner },
                });
                double bottom = size.Height - corner - ToolPadding;
                double top = cornered.Rating.Y - columnTop;
                double margin = bottom - cornered.FitEnhance.MaxY;
                if (cornered.FitEnhance.MaxY > bottom + Epsilon)
                    cornerProblems.Add($"圆角 {corner:0.##} 时末端 {cornered.FitEnhance.MaxY:0.##} 越出区间 {bottom:0.##}");
                if (!Near(top, margin))
                    cornerProblems.Add($"圆角 {corner:0.##} 时上/下余量 {top:0.##}/{margin:0.##} 不居中");
            }

            c.True(size.Name + " 横屏控制列圆角读数不论大小都保留（含小于手势条余量的读数）",
                cornerProblems.Count == 0, Detail(cornerProblems));
        }
    }

    /// <summary>
    /// 竖屏收尾口径：栏与栏之间只有一份 8 点外层间距——胶片条下缘落在底行上缘（视觉间距来自小档卡片自身的
    /// 交叉轴内边距）、分析栏紧贴顶行下缘（瓦片的 8 点上留白在分析栏交叉轴内）、胶片条两端留白取行内间距；
    /// 底栏背景自胶片条上缘铺到窗口下缘；控制栏关闭时胶片条落到 Home 余量而分析栏与顶栏不动；
    /// 无 EXIF 时顶行回到耳区但分析栏仍贴行下缘；纯控制栏不画顶栏；底行贴角带加宽只移动底行与胶片条。
    /// </summary>
    private static void RunPortraitOuterInsetChecks(Checks c)
    {
        foreach (var size in PortraitSizes)
        {
            var input = Input(size, landscape: false, notchOnLeft: true,
                filmVisible: true, controlVisible: true, exifVisible: true, analysisVisible: true,
                showNavigation: true, hasExifItems: true);
            var layout = PhoneScreenLayout.Compute(input);
            double rowBottom = layout.Filter.MaxY;
            var problems = new List<string>();

            if (!Near(layout.Filmstrip.MaxY, layout.FitEnhance.Y))
            {
                problems.Add($"胶片条下缘 {layout.Filmstrip.MaxY:0.##} 应等于底行上缘 {layout.FitEnhance.Y:0.##}");
            }

            if (!Near(layout.FitEnhance.Y - (layout.Filmstrip.MaxY - PortraitCardTrail), PortraitCardTrail))
                problems.Add("胶片条与底行之间应只隔卡片交叉轴内边距一份");
            if (!Near(PortraitCardTrail, RowInset))
                problems.Add($"卡片交叉轴内边距 {PortraitCardTrail} 与行内间距 {RowInset} 不同");

            if (!Near(layout.FilmstripPadding.Left, RowInset) || !Near(layout.FilmstripPadding.Right, RowInset)
                || !Near(layout.FilmstripPadding.Top, 0) || !Near(layout.FilmstripPadding.Bottom, 0))
            {
                problems.Add("胶片条两端留白应等于行内间距 " + EdgeInsetsText(layout.FilmstripPadding));
            }

            if (layout.Navigation.X <= RowInset + Epsilon)
            {
                problems.Add($"底行起点 {layout.Navigation.X:0.##}"
                    + $" 不应与行内间距 {RowInset} 同值（留白不再跟随底行缘距）");
            }

            if (!Near(layout.BottomBar.X, 0) || !Near(layout.BottomBar.Width, size.Width)
                || !Near(layout.BottomBar.Y, layout.Filmstrip.Y)
                || !Near(layout.BottomBar.MaxY, size.Height))
            {
                problems.Add("底栏背景应自胶片条上缘铺到窗口下缘 " + RectText(layout.BottomBar));
            }

            if (!Near(layout.Analysis.Y, rowBottom) || !Near(layout.Analysis.Y, layout.Exif.MaxY))
                problems.Add($"分析栏上缘 {layout.Analysis.Y:0.##} 应贴顶行下缘 {rowBottom:0.##}"
                    + "（瓦片 8 点上留白在交叉轴内，共享层不再叠加）");
            if (!Near(layout.Analysis.X, 0) || !Near(layout.Analysis.Width, size.Width)
                || !Near(layout.Analysis.Height, AnalysisCross))
            {
                problems.Add("分析栏应铺满整窗宽且交叉轴等于 AnalysisCross " + RectText(layout.Analysis));
            }

            if (!Near(layout.TopBar.X, 0) || !Near(layout.TopBar.Y, 0)
                || !Near(layout.TopBar.Width, size.Width) || !Near(layout.TopBar.MaxY, layout.Analysis.MaxY))
            {
                problems.Add("顶栏背景应覆盖顶行与分析栏 " + RectText(layout.TopBar));
            }

            c.True(size.Name + " 竖屏栏与栏之间只有一份 8 点外层间距（胶片条贴底行、分析栏贴顶行、留白取行内间距）",
                problems.Count == 0, Detail(problems));

            var noControl = PhoneScreenLayout.Compute(input with { ControlVisible = false });
            var noExif = PhoneScreenLayout.Compute(input with { ExifVisible = false });
            var pureControl = PhoneScreenLayout.Compute(input with { AnalysisVisible = false, ExifVisible = false });
            var widened = PhoneScreenLayout.Compute(input with
            {
                ControlBand = new EdgeInsets(40, 0, 40, input.Safe.Bottom),
            });
            var matrix = new List<string>();

            if (!Near(noControl.Filmstrip.MaxY, size.Height - BottomGap(input)))
            {
                matrix.Add($"控制栏关闭时胶片条下缘 {noControl.Filmstrip.MaxY:0.##}，"
                    + $"应为 {size.Height - BottomGap(input):0.##}");
            }

            if (!Near(noControl.Analysis.Y, rowBottom) || !Near(noControl.TopBar.MaxY, layout.TopBar.MaxY))
                matrix.Add("控制栏关闭不应移动分析栏与顶栏背景");
            if (!Near(noControl.FilmstripPadding.Left, RowInset)
                || !Near(noControl.FilmstripPadding.Right, RowInset))
                matrix.Add("控制栏关闭不应改变胶片条两端留白");
            if (Has(noControl.Navigation) || Has(noControl.Rating) || Has(noControl.FitEnhance))
                matrix.Add("控制栏关闭时底行元素应全部消失");
            if (!Has(noControl.BottomBar) || !Near(noControl.BottomBar.Y, noControl.Filmstrip.Y)
                || !Near(noControl.BottomBar.MaxY, size.Height))
                matrix.Add("控制栏关闭时底栏背景应仍从胶片条上缘铺到窗口下缘");

            if (Has(noExif.Exif))
                matrix.Add("EXIF 关闭时不应有 EXIF 块");
            if (!Near(noExif.Filter.Y, PhoneScreenLayout.TopRowY(input with { ExifVisible = false })))
                matrix.Add("无 EXIF 时顶行应回到耳区");
            if (!Near(noExif.Analysis.Y, noExif.Filter.MaxY)
                || !Near(noExif.TopBar.MaxY, noExif.Analysis.MaxY))
                matrix.Add("无 EXIF 时分析栏仍应紧贴顶行下缘且顶栏覆盖到分析栏下缘");

            if (!Has(pureControl.Rating) || !Has(pureControl.FitEnhance)
                || Has(pureControl.Analysis) || Has(pureControl.TopBar))
                matrix.Add("纯控制栏不应有分析栏与顶栏背景");
            if (!Near(pureControl.PhotoAvoidance.Top, pureControl.Filter.MaxY + RowInset))
                matrix.Add("纯控制栏时主图上避让应取顶行下缘 + 行内间距");

            if (!Near(widened.Filmstrip.MaxY, widened.FitEnhance.Y)
                || !Near(widened.FilmstripPadding.Left, RowInset)
                || !Near(widened.FilmstripPadding.Right, RowInset))
                matrix.Add("底行贴角带加宽后胶片条应贴行移动而两端留白不变");
            if (widened.FitEnhance.Y >= layout.FitEnhance.Y - Epsilon)
                matrix.Add("底行贴角带加宽后底行上缘应随之上移");
            if (!Near(widened.Analysis.Y, rowBottom)
                || !Near(widened.Analysis.Y, widened.Filter.MaxY))
                matrix.Add("底行贴角带加宽不应移动分析栏");

            c.True(size.Name + " 竖屏开关矩阵：控制隐藏/无 EXIF/纯控制/底行加宽都保持单份 8 点口径",
                matrix.Count == 0, Detail(matrix));
        }
    }

    /// <summary>
    /// 横屏下巴侧栏背景的三条件矩阵：分析栏或 EXIF 列任一存在即从最左那一列铺到窗口右缘并纵贯整窗；
    /// 控制栏关闭时分析栏贴窗口外缘、栏背景仍整块铺满；只有纯控制栏（无分析栏、无 EXIF）时完全透明；
    /// 刘海在右时整块贴窗口左缘，与刘海在左时镜像。
    /// </summary>
    private static void RunLandscapeTrailingBarChecks(Checks c)
    {
        foreach (var size in LandscapeSizes)
        {
            var problems = new List<string>();
            var cases = 0;
            var drawn = 0;

            foreach (var control in Flags)
            foreach (var exif in Flags)
            foreach (var analysis in Flags)
            {
                cases++;
                var input = Input(size, landscape: true, notchOnLeft: true,
                    filmVisible: true, controlVisible: control, exifVisible: exif,
                    analysisVisible: analysis, showNavigation: true, hasExifItems: true);
                var layout = PhoneScreenLayout.Compute(input);
                var label = $"控制{(control ? "显" : "隐")}/EXIF{(exif ? "开" : "关")}"
                    + $"/分析{(analysis ? "显" : "隐")}：";

                bool exifShown = control && exif;
                if (Has(layout.Exif) && !exifShown)
                    problems.Add(label + "控制栏隐藏或 EXIF 关闭时不应有 EXIF 列");

                bool barExpected = analysis || Has(layout.Exif);
                if (Has(layout.TrailingBar)) drawn++;

                if (Has(layout.TrailingBar) != barExpected)
                {
                    problems.Add(label + "右栏背景应由分析栏或 EXIF 列决定 " + RectText(layout.TrailingBar));
                    continue;
                }

                if (!barExpected)
                {
                    foreach (var (name, rect) in ControlColumn(layout))
                    {
                        if (!Has(rect)) continue;
                        if (Bars(layout).Any(bar => Intersects(bar, rect)))
                            problems.Add(label + name + " 在纯控制栏时不应被任何栏背景覆盖");
                    }

                    if (control && !Near(layout.PhotoAvoidance.Right, ControlRegionWidth(input)))
                    {
                        problems.Add(label + "纯控制栏的主图右避让应到控制区缘 " + EdgeInsetsText(layout.PhotoAvoidance));
                    }

                    continue;
                }

                double expectedLeft = analysis
                    ? layout.Analysis.X
                    : size.Width - ControlRegionWidth(input);
                if (!Near(layout.TrailingBar.X, expectedLeft)
                    || !Near(layout.TrailingBar.MaxX, size.Width)
                    || !Near(layout.TrailingBar.Y, 0)
                    || !Near(layout.TrailingBar.MaxY, size.Height))
                {
                    problems.Add(label + $"右栏背景应从 {expectedLeft:0.##} 铺到窗口右缘并纵贯整窗 "
                        + RectText(layout.TrailingBar));
                }

                foreach (var (name, rect) in ControlColumn(layout))
                {
                    if (Has(rect) && !Contains(layout.TrailingBar, rect))
                        problems.Add(label + name + " 未被右栏背景覆盖 " + RectText(rect));
                }

                if (analysis && !control
                    && (!Near(layout.Analysis.MaxX, size.Width)
                        || !Near(layout.Analysis.X, size.Width - AnalysisCross)))
                {
                    problems.Add(label + "控制栏关闭时分析栏应贴窗口外缘 " + RectText(layout.Analysis));
                }

                if (analysis)
                {
                    var mirrored = PhoneScreenLayout.Compute(input with { NotchOnLeft = false });
                    if (!Near(mirrored.TrailingBar.X, 0)
                        || !Near(mirrored.TrailingBar.MaxX, size.Width - layout.Analysis.X)
                        || !Near(mirrored.TrailingBar.MaxX, mirrored.Analysis.MaxX)
                        || !Near(mirrored.TrailingBar.MaxY, size.Height))
                    {
                        problems.Add(label + "刘海在右时右栏背景应贴窗口左缘并与分析栏右缘相接 "
                            + RectText(mirrored.TrailingBar));
                    }
                }
            }

            if (cases != 8 || drawn != 5)
                problems.Add($"矩阵应覆盖 8 种组合、其中 5 种画右栏背景，实际 {cases}/{drawn}");

            c.True(size.Name + " 横屏右栏背景的三条件矩阵：分析栏或 EXIF 铺满右缘、纯控制透明、刘海双向一致",
                problems.Count == 0, Detail(problems));
        }
    }

    /// <summary>
    /// 手机横屏 EXIF 整项裁剪：星级可见时先保住紧凑态最短长度与命令组，EXIF 用整项占满剩余长度；
    /// 星级隐藏时释放最短长度与那一组间距，EXIF 可以显示更多整项。
    /// </summary>
    private static void RunExifTrimmingChecks(Checks c)
    {
        var size = LandscapeSizes.Single(caseSize => caseSize.Name == "874×402");
        var four = ExifLayout(size, 4);
        var six = ExifLayout(size, 6);
        var nine = ExifLayout(size, 9);

        c.True("横屏 874×402 四项 EXIF 按整项裁到三项、星级 124",
            four.ExifItems == 3 && Near(four.Exif.Height, 112) && Near(four.Rating.Height, 124),
            $"显示项 {four.ExifItems}，EXIF {RectText(four.Exif)}，星级 {RectText(four.Rating)}");

        c.True("横屏 874×402 六项 EXIF 按整项裁到三项",
            six.ExifItems == 3 && Near(six.Exif.Height, 112),
            $"显示项 {six.ExifItems}，EXIF {RectText(six.Exif)}");

        c.True("横屏 874×402 九项仍裁到三项且 EXIF 列不变",
            nine.ExifItems == 3 && NearRect(nine.Exif, six.Exif),
            $"显示项 {nine.ExifItems}，EXIF {RectText(nine.Exif)}");

        c.True("横屏 874×402 裁剪后星级仍保住紧凑态最短长度且整组刚好填满可用区间",
            six.Rating.Height >= RatingControlGeometry.CompactMinLength - Epsilon
                && Near(six.Exif.Y, 28)
                && Near(six.FitEnhance.MaxY, size.Height - size.ControlBand.Bottom - ToolPadding),
            $"星级高 {six.Rating.Height:0.##}，EXIF 上缘 {six.Exif.Y:0.##}，结束 {six.FitEnhance.MaxY:0.##}");

        var shortSize = LandscapeSizes.Single(caseSize => caseSize.Name == "1024×320");
        var shortLayout = ExifLayout(shortSize, 4);
        c.True("横屏 1024×320 四项 EXIF 裁到一项且星级不短于紧凑态最短长度",
            shortLayout.ExifItems == 1
                && Near(shortLayout.Exif.Height, ExifBlockGeometry.Size(1, false, bare: true).Height)
                && shortLayout.Rating.Height >= RatingControlGeometry.CompactMinLength - Epsilon,
            $"显示项 {shortLayout.ExifItems}，EXIF {RectText(shortLayout.Exif)}，"
            + $"星级 {RectText(shortLayout.Rating)}");
    }

    private static PhoneLayout ExifLayout(SizeCase size, int exifCount)
        => PhoneScreenLayout.Compute(Input(size, landscape: true, notchOnLeft: true,
            filmVisible: true, controlVisible: true, exifVisible: true, analysisVisible: true,
            showNavigation: true, hasExifItems: true, exifCount: exifCount));

    /// <summary>
    /// 左右工具区几何（手机横屏与 iOS 宽形态共用）：控制区独立 64、相邻分析栏 54，接缝间距由分析区末端持有；
    /// 分析区按邻接取 114/116，内容居中在空间不足时停在区间起点。
    /// </summary>
    private static void RunToolRegionChecks(Checks c)
    {
        c.True("工具区常量：内边距 10 / 命令间距 8 / 组间距 10",
            Near(ToolRegionGeometry.Padding, ToolPadding)
                && Near(ToolRegionGeometry.Spacing, ToolSpacing)
                && Near(ToolRegionGeometry.GroupSpacing, ToolGroupSpacing),
            $"{ToolRegionGeometry.Padding}/{ToolRegionGeometry.Spacing}/{ToolRegionGeometry.GroupSpacing}");

        c.True("控制区：独立 64、相邻分析栏 54（内容 44），前导内边距 10 → 0",
            Near(ToolRegionGeometry.ControlLeadingInset(false), ToolPadding)
                && Near(ToolRegionGeometry.ControlLeadingInset(true), 0)
                && Near(ToolRegionGeometry.ControlWidth(ButtonSize, false), 64)
                && Near(ToolRegionGeometry.ControlWidth(ButtonSize, true), 54),
            $"{ToolRegionGeometry.ControlLeadingInset(false)}/{ToolRegionGeometry.ControlLeadingInset(true)}，"
            + $"{ToolRegionGeometry.ControlWidth(ButtonSize, false)}/{ToolRegionGeometry.ControlWidth(ButtonSize, true)}");

        c.True("分析区：相邻控制区 114、独立 116（瓦片 96，接缝 8/10）",
            Near(ToolRegionGeometry.AnalysisWidth(96, true), 114)
                && Near(ToolRegionGeometry.AnalysisWidth(96, false), 116),
            $"{ToolRegionGeometry.AnalysisWidth(96, true)}/{ToolRegionGeometry.AnalysisWidth(96, false)}");

        c.True("居中起点均分余量且空间不足时停在区间起点",
            Near(ToolRegionGeometry.CenteredStart(20, 100, 60), 40)
                && Near(ToolRegionGeometry.CenteredStart(20, 40, 60), 20),
            $"{ToolRegionGeometry.CenteredStart(20, 100, 60)}/{ToolRegionGeometry.CenteredStart(20, 40, 60)}");
    }

    /// <summary>
    /// 星级开关：关闭时竖屏不再保留评分槽、横屏释放星级占位（含最短长度与组间距）并把剩余长度全部让给 EXIF 整项，
    /// 控制区与分析区的横向几何、栏背景与主图避让都不受影响，刘海两向仍镜像。
    /// </summary>
    private static void RunShowRatingChecks(Checks c)
    {
        foreach (var size in PortraitSizes)
        {
            var input = Input(size, landscape: false, notchOnLeft: true,
                filmVisible: true, controlVisible: true, exifVisible: true, analysisVisible: true,
                showNavigation: true, hasExifItems: true);
            var shown = PhoneScreenLayout.Compute(input);
            var hidden = PhoneScreenLayout.Compute(input with { ShowRating = false });
            var problems = new List<string>();

            if (!Has(shown.Rating)) problems.Add("评分开启时应有星级槽");
            if (Has(hidden.Rating)) problems.Add("评分关闭时不应保留星级槽 " + RectText(hidden.Rating));
            SameRect(problems, "上下张（评分关闭）", shown.Navigation, hidden.Navigation);
            SameRect(problems, "缩放增强（评分关闭）", shown.FitEnhance, hidden.FitEnhance);
            SameRect(problems, "EXIF（评分关闭）", shown.Exif, hidden.Exif);
            SameRect(problems, "胶片条（评分关闭）", shown.Filmstrip, hidden.Filmstrip);
            SameRect(problems, "分析栏（评分关闭）", shown.Analysis, hidden.Analysis);
            SameRect(problems, "顶栏（评分关闭）", shown.TopBar, hidden.TopBar);
            SameRect(problems, "底栏（评分关闭）", shown.BottomBar, hidden.BottomBar);
            SameAvoidance(problems, shown.PhotoAvoidance, hidden.PhotoAvoidance);

            c.True(size.Name + " 竖屏关闭评分只清空星级槽、不做其他重排",
                problems.Count == 0, Detail(problems));
        }

        foreach (var size in LandscapeSizes)
        {
            var input = Input(size, landscape: true, notchOnLeft: true,
                filmVisible: true, controlVisible: true, exifVisible: true, analysisVisible: true,
                showNavigation: true, hasExifItems: true, exifCount: 6);
            var shown = PhoneScreenLayout.Compute(input);
            var hidden = PhoneScreenLayout.Compute(input with { ShowRating = false });
            var mirrored = PhoneScreenLayout.Compute(input with { NotchOnLeft = false, ShowRating = false });
            var problems = new List<string>();

            if (Has(hidden.Rating))
                problems.Add("评分关闭时不应保留星级槽 " + RectText(hidden.Rating));

            double columnTop = Math.Max(input.Safe.Top, input.ControlBand.Top) + ToolPadding;
            double columnBottom = size.Height - Math.Max(input.Safe.Bottom, input.ControlBand.Bottom) - ToolPadding;

            if (Has(hidden.Exif))
            {
                // EXIF 与命令之间只剩一组间距，整组仍在可用区间居中。
                if (!Near(hidden.Exif.MaxY + ToolGroupSpacing, hidden.FitEnhance.Y))
                    problems.Add($"EXIF 与命令之间应只有一组间距 {ToolGroupSpacing}，"
                        + $"实际 {(hidden.FitEnhance.Y - hidden.Exif.MaxY):0.##}");
                if (!Near(hidden.Exif.Y - columnTop, columnBottom - hidden.FitEnhance.MaxY))
                    problems.Add("评分关闭后 EXIF 与命令整组未在可用区间居中");
            }

            if (!Near(hidden.FitEnhance.X, shown.FitEnhance.X)
                || !Near(hidden.Exif.X, shown.Exif.X)
                || !Near(hidden.Analysis.X, shown.Analysis.X)
                || !Near(hidden.Analysis.Width, shown.Analysis.Width)
                || !SameRectQuiet(hidden.TrailingBar, shown.TrailingBar)
                || !Near(hidden.PhotoAvoidance.Right, shown.PhotoAvoidance.Right))
            {
                problems.Add("评分关闭不应改变控制区/分析区/栏背景/主图避让的横向几何");
            }

            if (hidden.ExifItems < shown.ExifItems)
                problems.Add($"评分关闭不应减少 EXIF 整项（{shown.ExifItems} → {hidden.ExifItems}）");

            if (Has(mirrored.Rating))
                problems.Add("刘海在右且评分关闭时不应有星级槽");
            var expectedMirror = new LayoutRect(size.Width - hidden.FitEnhance.X - hidden.FitEnhance.Width,
                hidden.FitEnhance.Y, hidden.FitEnhance.Width, hidden.FitEnhance.Height);
            if (!NearRect(expectedMirror, mirrored.FitEnhance))
                problems.Add("刘海在右且评分关闭时命令组未镜像");
            if (!Near(mirrored.PhotoAvoidance.Right, hidden.PhotoAvoidance.Left)
                || !Near(mirrored.Analysis.X, size.Width - hidden.Analysis.MaxX))
                problems.Add("刘海在右且评分关闭时分析区/避让未镜像");

            c.True(size.Name + " 横屏关闭评分释放星级占位：EXIF 占满剩余整项、栏横向几何不变且镜像",
                problems.Count == 0, Detail(problems));
        }

        var releaseSize = LandscapeSizes.Single(caseSize => caseSize.Name == "874×402");
        var releaseInput = Input(releaseSize, landscape: true, notchOnLeft: true,
            filmVisible: true, controlVisible: true, exifVisible: true, analysisVisible: true,
            showNavigation: true, hasExifItems: true, exifCount: 6);
        var releaseShown = PhoneScreenLayout.Compute(releaseInput);
        var releaseHidden = PhoneScreenLayout.Compute(releaseInput with { ShowRating = false });

        c.True("横屏 874×402 六项 EXIF：关闭评分后从三项升到六项、只剩一组间距且星级槽消失",
            releaseShown.ExifItems == 3 && releaseHidden.ExifItems == 6
                && !Has(releaseHidden.Rating)
                && Near(releaseHidden.Exif.MaxY + ToolGroupSpacing, releaseHidden.FitEnhance.Y)
                && Near(releaseHidden.Exif.X, releaseShown.Exif.X),
            $"显示项 {releaseShown.ExifItems} → {releaseHidden.ExifItems}，"
            + $"EXIF {RectText(releaseHidden.Exif)}，增强 {RectText(releaseHidden.FitEnhance)}");
    }

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
            var standalone = PhoneScreenLayout.Compute(input with { AnalysisVisible = false });
            double strip = PhoneScreenLayout.ControlStripWidth(input);
            double standaloneStrip = PhoneScreenLayout.ControlStripWidth(input with { AnalysisVisible = false });

            c.True(size.Name + " 横屏刘海列探测宽度与实际列缘一致、控制列探测宽等于控制区宽（相邻 54 / 独立 64）",
                Near(PhoneScreenLayout.LeadStripWidth(input), layout.Filmstrip.MaxX)
                    && Near(PhoneScreenLayout.LeadStripWidth(input with { FilmVisible = false }),
                        layout.Filmstrip.MaxX)
                    && Near(layout.FilterMoreGroup.MaxX + input.FilmOuterPad,
                        PhoneScreenLayout.LeadStripWidth(input))
                    && Near(strip, 54)
                    && Near(PhoneScreenLayout.ControlStripWidth(input with { ExifVisible = false }), strip)
                    && Near(size.Width - strip, layout.FitEnhance.X)
                    && (!Has(layout.Exif)
                        || Near(layout.Exif.X + layout.Exif.Width / 2, layout.FitEnhance.X + ButtonSize / 2))
                    && Near(standaloneStrip, 64)
                    && Near(size.Width - standaloneStrip, standalone.TrailingBar.X)
                    && Near(standalone.TrailingBar.X + ToolPadding, standalone.FitEnhance.X),
                $"刘海列 {PhoneScreenLayout.LeadStripWidth(input):0.##}，"
                + $"胶片栏关闭后 {PhoneScreenLayout.LeadStripWidth(input with { FilmVisible = false }):0.##}，"
                + $"关闭胶片栏后入口右缘 {ear.FilterMoreGroup.MaxX:0.##}，"
                + $"控制区 {strip:0.##}/{standaloneStrip:0.##}（EXIF {(Has(layout.Exif) ? "开" : "关")}），"
                + $"独立栏背景 {RectText(standalone.TrailingBar)}");
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

        c.True("iPhone 17 竖屏 402×874：分析栏 106..202 铺满宽度、EXIF 下缘与瓦片之间只差面板内的 8",
            Near(portraitLayout.Analysis.Y, 106)
                && Near(portraitLayout.Analysis.Height, AnalysisCross)
                && Near(portraitLayout.Analysis.MaxY, 106 + AnalysisCross)
                && Near(portraitLayout.Analysis.X, 0) && Near(portraitLayout.Analysis.Width, 402)
                && Near(portraitLayout.Analysis.Y, portraitLayout.Exif.MaxY)
                && Near(portraitLayout.TopBar.MaxY, 106 + AnalysisCross),
            $"分析 {RectText(portraitLayout.Analysis)}，顶栏 {RectText(portraitLayout.TopBar)}，"
            + $"EXIF {RectText(portraitLayout.Exif)}");

        c.True("iPhone 17 竖屏 402×874：胶片条 712..804 贴底行、两端留白 8、底栏自 712 铺到窗口下缘",
            Near(portraitLayout.Filmstrip.Y, 712) && Near(portraitLayout.Filmstrip.MaxY, 804)
                && Near(portraitLayout.Filmstrip.MaxY, portraitLayout.FitEnhance.Y)
                && Near(portraitLayout.FilmstripPadding.Left, RowInset)
                && Near(portraitLayout.FilmstripPadding.Right, RowInset)
                && Near(portraitLayout.BottomBar.Y, 712)
                && Near(portraitLayout.BottomBar.MaxY, 874),
            $"胶片条 {RectText(portraitLayout.Filmstrip)}，底栏 {RectText(portraitLayout.BottomBar)}，"
            + EdgeInsetsText(portraitLayout.FilmstripPadding));

        var landscape = LandscapeSizes.Single(size => size.Name == "874×402");
        var landscapeInput = Input(landscape, landscape: true, notchOnLeft: true,
            filmVisible: true, controlVisible: true, exifVisible: true, analysisVisible: true,
            showNavigation: true, hasExifItems: true);
        var landscapeLayout = PhoneScreenLayout.Compute(landscapeInput);

        c.True("iPhone 17 横屏 874×402：分析栏 724..820 贴控制区栏缘、右栏背景 724..874 纵贯整窗",
            Near(landscapeLayout.Analysis.X, 724)
                && Near(landscapeLayout.Analysis.MaxX, 820)
                && Near(landscapeLayout.Analysis.Y, 0)
                && Near(landscapeLayout.Analysis.Height, 402)
                && Near(landscapeLayout.TrailingBar.X, 724)
                && Near(landscapeLayout.TrailingBar.MaxX, 874)
                && Near(landscapeLayout.TrailingBar.Height, 402),
            $"分析 {RectText(landscapeLayout.Analysis)}，栏背景 {RectText(landscapeLayout.TrailingBar)}");

        c.True("iPhone 17 横屏 874×402：控制列外缘距窗口右缘一份工具区内边距（864）",
            Near(landscapeLayout.FitEnhance.MaxX, 864)
                && Near(landscapeLayout.Rating.MaxX, 864),
            $"增强 {RectText(landscapeLayout.FitEnhance)}");

        c.True("iPhone 17 横屏 874×402：控制区自上而下 EXIF 28/星级 150/增强 284，星级 124",
            Near(landscapeLayout.Exif.Y, 28) && Near(landscapeLayout.Rating.Y, 150)
                && Near(landscapeLayout.Rating.Height, 124) && Near(landscapeLayout.FitEnhance.Y, 284)
                && Near(landscapeLayout.FitEnhance.Height, 88),
            $"EXIF {RectText(landscapeLayout.Exif)}，星级 {RectText(landscapeLayout.Rating)}，"
            + $"增强 {RectText(landscapeLayout.FitEnhance)}");

        c.True("iPhone 17 横屏 874×402：EXIF 无衬底列取固有项宽 52 并与按钮同轴、无额外内外边距",
            Near(landscapeLayout.Exif.Width, DesignTokens.ToolbarExifItemWidth)
                && Near(landscapeLayout.Exif.X, 816)
                && Near(landscapeLayout.Exif.X + landscapeLayout.Exif.Width / 2, 842)
                && landscapeLayout.ExifItems == 3,
            $"EXIF {RectText(landscapeLayout.Exif)}，显示项 {landscapeLayout.ExifItems}");

        c.True("iPhone 17 横屏 874×402：分析栏与控制区共边（接缝 8 在分析区末端内）",
            Near(landscapeLayout.Analysis.MaxX, landscapeLayout.FitEnhance.X)
                && Near(landscapeLayout.Analysis.Width, AnalysisCross)
                && Near(landscapeLayout.Analysis.MaxX, 874 - 54),
            $"分析 {RectText(landscapeLayout.Analysis)}，增强 {RectText(landscapeLayout.FitEnhance)}");

        c.True("iPhone 17 横屏 874×402：卡片贴刘海安全区 62（胶片条 54 + 内边距 8）",
            Near(landscapeLayout.Filmstrip.X, 54)
                && Near(landscapeLayout.Filmstrip.X + SmallCardPad, 62),
            $"胶片条 {RectText(landscapeLayout.Filmstrip)}");

        c.True("iPhone 17 横屏 874×402：合体按钮与上下张右对齐 ContentTrailing 126、入口上缘 28",
            Near(landscapeLayout.FilterMoreGroup.MaxX, 126)
                && Near(landscapeLayout.Navigation.MaxX, 126)
                && Near(landscapeLayout.FilterMoreGroup.Y, 28)
                && Near(landscapeLayout.FilterMoreGroup.X, 38),
            $"合体 {RectText(landscapeLayout.FilterMoreGroup)}，上下张 {RectText(landscapeLayout.Navigation)}");

        c.True("iPhone 17 横屏 874×402：胶片条上下停靠留白 82 / 84",
            Near(landscapeLayout.FilmstripPadding.Top, 82)
                && Near(landscapeLayout.FilmstripPadding.Bottom, 84),
            EdgeInsetsText(landscapeLayout.FilmstripPadding));

        var landscapeNoFilm = PhoneScreenLayout.Compute(landscapeInput with { FilmVisible = false });
        c.True("iPhone 17 横屏 874×402：关闭胶片栏不移入口组、不拆耳区，列宽与主图避让左仍是 134",
            SameRectQuiet(landscapeNoFilm.FilterMoreGroup, landscapeLayout.FilterMoreGroup)
                && !Has(landscapeNoFilm.Filmstrip) && !Has(landscapeNoFilm.LeadingBar)
                && !Has(landscapeNoFilm.Navigation)
                && Near(landscapeNoFilm.PhotoAvoidance.Left, 134),
            $"合体 {RectText(landscapeNoFilm.FilterMoreGroup)}，"
            + $"避让 {EdgeInsetsText(landscapeNoFilm.PhotoAvoidance)}");
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

                if (!Near(layout.FilterMoreGroup.Y, 50 + FilmSidePadding))
                {
                    problems.Add($"合体按钮上缘 {layout.FilterMoreGroup.Y:0.##}，应为 {50 + FilmSidePadding:0.##}");
                }

                if (Near(layout.FilterMoreGroup.Y, baseline.FilterMoreGroup.Y))
                    problems.Add("合体按钮未随贴角带下移");

                if (!Near(layout.FilmstripPadding.Top, layout.FilterMoreGroup.MaxY + FilmSidePadding))
                    problems.Add("胶片条上留白未跟随合体按钮下缘");

                if (!Near(PhoneScreenLayout.LeadStripWidth(widened), PhoneScreenLayout.LeadStripWidth(input)))
                    problems.Add("贴角带加大不应改变刘海侧列宽");

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
                SameRect(problems, "分析栏", baseline.Analysis, layout.Analysis);
                SameRect(problems, "顶栏", baseline.TopBar, layout.TopBar);
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
            var column = ColumnMeasure(narrow);
            double entryTrailing = FilmSidePadding + ButtonSize * 2;
            var problems = new List<string>();

            if (!Near(column.ContentTrailing, entryTrailing))
                problems.Add($"窄带列缘 {column.ContentTrailing:0.##} 应由入口宽决定 {entryTrailing:0.##}");

            if (!(layout.FilterMoreGroup.X < narrow.Safe.Left - Epsilon))
            {
                problems.Add($"合体按钮左缘 {layout.FilterMoreGroup.X:0.##}"
                    + $" 未进入刘海侧安全区 {narrow.Safe.Left:0.##}");
            }

            if (layout.FilterMoreGroup.X < FilmSidePadding - Epsilon)
                problems.Add($"合体按钮左缘 {layout.FilterMoreGroup.X:0.##} 距窗口缘小于 {FilmSidePadding}");

            if (!Near(layout.FilterMoreGroup.MaxX, entryTrailing))
            {
                problems.Add($"合体按钮右缘 {layout.FilterMoreGroup.MaxX:0.##}"
                    + $" ≠ 入口锚点 {entryTrailing:0.##}");
            }

            if (!Near(layout.Navigation.MaxX, entryTrailing))
            {
                problems.Add($"上下张右缘 {layout.Navigation.MaxX:0.##}"
                    + $" ≠ 入口锚点 {entryTrailing:0.##}");
            }

            if (!Near(layout.Filmstrip.MaxX, column.Width))
                problems.Add($"胶片条外缘 {layout.Filmstrip.MaxX:0.##} ≠ 列宽 {column.Width:0.##}");

            if (layout.Filmstrip.X + narrow.FilmOuterPad < narrow.Safe.Left - Epsilon)
                problems.Add($"卡片左缘 {layout.Filmstrip.X + narrow.FilmOuterPad:0.##}"
                    + $" 越进刘海侧安全区 {narrow.Safe.Left:0.##}");

            if (!Near(layout.PhotoAvoidance.Left, column.Width))
                problems.Add($"主图避让左 {layout.PhotoAvoidance.Left:0.##} ≠ 列宽 {column.Width:0.##}");

            c.True($"横屏 {size.Name} 窄胶片列（{NarrowFilmBand}）：入口 88 撑出列缘但卡片不越刘海安全区",
                problems.Count == 0, Detail(problems));
        }

        foreach (var size in LandscapeSizes)
        {
            var wide = Input(size, landscape: true, notchOnLeft: true,
                filmVisible: true, controlVisible: true, exifVisible: true, analysisVisible: true,
                showNavigation: true, hasExifItems: true);
            var layout = PhoneScreenLayout.Compute(wide);
            var column = ColumnMeasure(wide);
            double cardLead = column.FilmstripX + wide.FilmOuterPad;
            double cardTrail = column.FilmstripX + wide.FilmBand - wide.FilmOuterPad;
            bool cardDriven = column.ContentTrailing > FilmSidePadding + ButtonSize * 2 + Epsilon;
            var problems = new List<string>();

            if (!Near(layout.Filmstrip.MaxX, column.Width)
                || !Near(layout.FilterMoreGroup.MaxX, column.ContentTrailing)
                || !Near(layout.Navigation.MaxX, column.ContentTrailing)
                || !Near(layout.PhotoAvoidance.Left, column.Width))
            {
                problems.Add($"胶片条 {RectText(layout.Filmstrip)}，合体 {RectText(layout.FilterMoreGroup)}，"
                    + $"避让左 {layout.PhotoAvoidance.Left:0.##}，锚点 {column.ContentTrailing:0.##}/{column.Width:0.##}");
            }

            if (cardLead < wide.Safe.Left - Epsilon)
                problems.Add($"卡片左缘 {cardLead:0.##} 越进刘海侧安全区 {wide.Safe.Left:0.##}");

            if (cardDriven && !Near(cardLead, wide.Safe.Left))
                problems.Add($"卡片左缘 {cardLead:0.##} 未贴刘海安全区 {wide.Safe.Left:0.##}");

            if (!Near(cardTrail, column.ContentTrailing))
                problems.Add($"卡片外框右缘 {cardTrail:0.##} ≠ 入口锚点 {column.ContentTrailing:0.##}");

            c.True($"横屏 {size.Name} 标准胶片列（{FilmBandLandscape}）：列宽取卡片外框与入口的较大者 + 尾侧内边距",
                problems.Count == 0, Detail(problems));

            var thickPad = wide with { FilmOuterPad = 20 };
            var thick = PhoneScreenLayout.Compute(thickPad);
            var thickColumn = ColumnMeasure(thickPad);

            c.True($"横屏 {size.Name} 加大卡片内边距后卡片仍不越刘海安全区",
                thick.Filmstrip.X + 20 >= thickPad.Safe.Left - Epsilon
                    && Near(thick.Filmstrip.X, thickColumn.FilmstripX)
                    && Near(thick.Filmstrip.MaxX, thickColumn.Width)
                    && Near(thick.Filmstrip.X + thickPad.FilmBand - 20, thickColumn.ContentTrailing),
                $"胶片条 {RectText(thick.Filmstrip)}，卡片左缘 {thick.Filmstrip.X + 20:0.##}，"
                + $"安全区 {thickPad.Safe.Left:0.##}");
        }
    }

    /// <summary>
    /// 横屏刘海侧列靠主图侧右对齐（数值对拍）：入口组 88、上下张与卡片外框同落 ContentTrailing，列宽再加一份
    /// 卡片尾侧内边距；列表的刘海避让只约束卡片。大/小档与有无刘海分别核对宽度、列表 X 与卡片外框。
    /// </summary>
    private static void RunFilmSideColumnChecks(Checks c)
    {
        var cases = new (string Name, double Safe, double Band, double Pad, double Width, double Trailing,
            double ListX, double CardLead)[]
        {
            ("小档无刘海", 0, 80, 8, 106, 98, 26, 34),
            ("大档无刘海", 0, 116, 10, 116, 106, 0, 10),
            ("小档刘海 62", 62, 80, 8, 134, 126, 54, 62),
            ("大档刘海 62", 62, 116, 10, 168, 158, 52, 62),
        };

        foreach (var (name, safe, band, pad, width, trailing, listX, cardLead) in cases)
        {
            var size = LandscapeCase($"{name} 844×390", 844, 390, safe, 20);
            var input = Input(size, landscape: true, notchOnLeft: true,
                filmVisible: true, controlVisible: true, exifVisible: true, analysisVisible: true,
                showNavigation: true, hasExifItems: true, filmBand: band, filmOuterPad: pad);
            var layout = PhoneScreenLayout.Compute(input);
            var column = FilmSideGeometry.Measure(safe, band, pad, pad, ButtonSize * 2);
            var problems = new List<string>();

            if (!Near(column.Width, width) || !Near(column.ContentTrailing, trailing)
                || !Near(column.FilmstripX, listX))
            {
                problems.Add($"测量 {column.Width:0.##}/{column.ContentTrailing:0.##}/{column.FilmstripX:0.##}，"
                    + $"应为 {width}/{trailing}/{listX}");
            }

            if (!Near(layout.Filmstrip.X, listX) || !Near(layout.Filmstrip.MaxX, width))
                problems.Add("胶片条未靠主图侧收进列尾 " + RectText(layout.Filmstrip));

            if (!Near(layout.Filmstrip.X + pad, cardLead) || cardLead < safe - Epsilon
                || !Near(layout.Filmstrip.X + band - pad, trailing))
            {
                problems.Add($"卡片外框 {layout.Filmstrip.X + pad:0.##}..{layout.Filmstrip.X + band - pad:0.##}，"
                    + $"应为 {cardLead}..{trailing}");
            }

            if (!Near(layout.FilterMoreGroup.MaxX, trailing)
                || !Near(layout.Navigation.MaxX, trailing)
                || !Near(layout.FilterMoreGroup.X, trailing - ButtonSize * 2))
            {
                problems.Add("入口组与上下张未右对齐 ContentTrailing "
                    + RectText(layout.FilterMoreGroup) + " / " + RectText(layout.Navigation));
            }

            if (!Near(layout.PhotoAvoidance.Left, width) || !Near(layout.LeadingBar.MaxX, width))
                problems.Add("列宽/主图避让左未取测量宽度 " + EdgeInsetsText(layout.PhotoAvoidance));

            if (!Near(layout.FilterMoreGroup.Y, Math.Max(size.Safe.Top, size.LeadBand.Top) + FilmSidePadding))
                problems.Add("入口组未按 safe/corner 带 + 内边距落位 " + RectText(layout.FilterMoreGroup));

            c.True($"横屏 {name}：入口组、上下张与卡片外框同落 ContentTrailing {trailing}，列宽 {width}",
                problems.Count == 0, Detail(problems));
        }

        // 入口组恒贴 ContentTrailing：手机横屏不居中、也不因刘海把整列让出。小档刘海 62 时入口左缘进入刘海带。
        var notchSize = LandscapeCase("小档刘海 62 844×390", 844, 390, 62, 20);
        var notchInput = Input(notchSize, landscape: true, notchOnLeft: true,
            filmVisible: true, controlVisible: true, exifVisible: true, analysisVisible: true,
            showNavigation: true, hasExifItems: true);
        var open = PhoneScreenLayout.Compute(notchInput);

        c.True("横屏小档刘海 62：入口组右对齐 126、左缘 38 进入刘海带（不居中、不整列让出）",
            Near(open.FilterMoreGroup.MaxX, 126) && Near(open.FilterMoreGroup.X, 38)
                && open.FilterMoreGroup.X < notchSize.Safe.Left - Epsilon
                && !Near(open.FilterMoreGroup.X, (844 - open.FilterMoreGroup.Width) / 2),
            RectText(open.FilterMoreGroup));

        var noFilm = PhoneScreenLayout.Compute(notchInput with { FilmVisible = false });
        var noNavigation = PhoneScreenLayout.Compute(notchInput with { ShowNavigation = false });
        var toggled = new List<string>();

        SameRect(toggled, "入口组（胶片栏关闭）", open.FilterMoreGroup, noFilm.FilterMoreGroup);
        SameRect(toggled, "入口组（上下张关闭）", open.FilterMoreGroup, noNavigation.FilterMoreGroup);
        if (!Near(PhoneScreenLayout.LeadStripWidth(notchInput with { FilmVisible = false }),
                PhoneScreenLayout.LeadStripWidth(notchInput))
            || !Near(PhoneScreenLayout.LeadStripWidth(notchInput with { ShowNavigation = false }),
                PhoneScreenLayout.LeadStripWidth(notchInput)))
        {
            toggled.Add("列宽随胶片栏/上下张开关改变");
        }

        if (Has(noFilm.Filmstrip) || Has(noFilm.LeadingBar) || Has(noFilm.Navigation))
            toggled.Add("胶片栏关闭时列表槽、栏背景与上下张应全空");
        if (!Near(noFilm.PhotoAvoidance.Left, open.PhotoAvoidance.Left))
            toggled.Add($"胶片栏关闭后主图避让左被改动 "
                + $"{noFilm.PhotoAvoidance.Left:0.##} / {open.PhotoAvoidance.Left:0.##}");
        if (Has(noNavigation.Navigation)
            || !Near(noNavigation.FilmstripPadding.Top, open.FilmstripPadding.Top))
            toggled.Add("上下张关闭不应留空导航或移动首卡停靠线");

        c.True("横屏刘海侧列：胶片栏与上下张开关不移入口组、不改栏宽、不制造额外空列",
            toggled.Count == 0, Detail(toggled));

        var asymmetric = notchInput with { Safe = new EdgeInsets(62, 0, 0, 20) };
        var leftward = PhoneScreenLayout.Compute(asymmetric);
        var mirrored = PhoneScreenLayout.Compute(asymmetric with { NotchOnLeft = false });

        c.True("横屏刘海侧列：刘海在右时按右侧安全区镜像（入口列 106、入口距右缘 10）",
            Near(PhoneScreenLayout.LeadStripWidth(asymmetric with { NotchOnLeft = false }), 106)
                && Near(PhoneScreenLayout.LeadStripWidth(asymmetric), 134)
                && Near(844 - mirrored.FilterMoreGroup.MaxX, FilmSidePadding)
                && Near(mirrored.FilterMoreGroup.X, 844 - FilmSidePadding - ButtonSize * 2)
                && Near(mirrored.Filmstrip.MaxX, 844 - (106 - 80))
                && Near(mirrored.PhotoAvoidance.Right, 106)
                && Near(mirrored.PhotoAvoidance.Left, leftward.PhotoAvoidance.Right),
            $"右向合体 {RectText(mirrored.FilterMoreGroup)}，胶片条 {RectText(mirrored.Filmstrip)}，"
            + $"避让 {EdgeInsetsText(mirrored.PhotoAvoidance)}");
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

    /// <summary>横屏控制区宽：内容 + 尾随内边距 + 独立时的一份前导内边距（相邻分析栏时省略）。</summary>
    private static double ControlRegionWidth(in PhoneLayoutInput input)
        => input.ButtonSize + ToolPadding + (input.AnalysisVisible ? 0 : ToolPadding);

    /// <summary>横屏刘海侧列测量（按刘海在左的输入取左安全区，卡片交叉轴两侧内边距同值）。</summary>
    private static FilmSideMeasure ColumnMeasure(in PhoneLayoutInput input)
        => FilmSideGeometry.Measure(input.Safe.Left, input.FilmBand, input.FilmOuterPad, input.FilmOuterPad,
            input.ButtonSize * 2);

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

    /// <summary>横屏下巴侧控制栏的三段（自上而下 EXIF、星级、缩放增强），用于核对栏背景覆盖。</summary>
    private static (string Name, LayoutRect Rect)[] ControlColumn(in PhoneLayout layout) =>
    [
        ("EXIF", layout.Exif),
        ("星级", layout.Rating),
        ("缩放增强", layout.FitEnhance),
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

    /// <summary>两个矩形是否同一（空槽位要求同时为空），不写问题列表。</summary>
    private static bool SameRectQuiet(LayoutRect first, LayoutRect second)
        => Has(first) == Has(second) && (!Has(first) || NearRect(first, second));

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
