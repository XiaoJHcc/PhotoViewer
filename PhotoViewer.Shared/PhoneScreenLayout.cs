namespace PhotoViewer.Shared;

/// <summary>四边内边距（逻辑点）：避开栏位与按钮后的主图适应区相对窗口四边的间距。</summary>
/// <param name="Left">左内边距。</param>
/// <param name="Top">上内边距。</param>
/// <param name="Right">右内边距。</param>
/// <param name="Bottom">下内边距。</param>
public readonly record struct EdgeInsets(double Left, double Top, double Right, double Bottom);

/// <summary>手机形态槽位几何的输入：窗口、系统安全区、贴边元素带的窗口缘距离、显隐状态与各元素固有尺寸。</summary>
/// <param name="Landscape">横屏排布（刘海侧列在左、控制栏在右，刘海在右时镜像）；否则竖屏排布。</param>
/// <param name="NotchOnLeft">横屏时刘海在左；false 时整体水平镜像。</param>
/// <param name="Width">窗口宽。</param>
/// <param name="Height">窗口高。</param>
/// <param name="Safe">系统安全区（刘海、圆角与 Home 指示条的避让），不含任何按元素带适配的额外距离。</param>
/// <param name="LeadBand">贴角元素带必须保持的绝对窗口缘距离：竖屏为顶行（用左右两侧，须在 <see cref="PhoneScreenLayout.TopRowY"/> 给出的行位置探测），横屏为刘海侧列（用上下两侧）；数值已含安全区与该带的圆角/窗口控件适配。</param>
/// <param name="ControlBand">控制栏带的绝对窗口缘距离：竖屏为底行（用左右两侧），横屏为下巴侧控制栏整列（用上下两侧，在 <see cref="PhoneScreenLayout.ControlStripWidth"/> 给出的列宽上探测）。</param>
/// <param name="FilmVisible">胶片栏开关：胶片条，横屏另含刘海侧列的上一张/下一张。</param>
/// <param name="ControlVisible">控制栏开关：星级控件与同栏按钮（竖屏底行、横屏下巴侧控制栏）。</param>
/// <param name="ExifVisible">手机形态 EXIF 开关：竖屏单独决定 EXIF 块，横屏还要控制栏可见。</param>
/// <param name="AnalysisVisible">分析栏是否显示。</param>
/// <param name="ShowNavigation">是否显示上一张/下一张合体按钮。</param>
/// <param name="FilterWidth">竖屏筛选控件的固有宽度；横屏筛选与更多每段按 <paramref name="ButtonSize"/>。</param>
/// <param name="FilmBand">胶片条交叉轴厚度：竖屏为横向胶片条高度，横屏为刘海侧列内容宽。</param>
/// <param name="FilmOuterPad">横屏胶片条靠刘海一侧的卡片交叉轴内边距；卡片贴刘海安全区时把它让进安全区。</param>
/// <param name="AnalysisCross">分析栏交叉轴（竖屏为高，横屏为宽）。</param>
/// <param name="ExifCount">启用的 EXIF 项数；0 表示没有 EXIF 显示项。</param>
/// <param name="ExifWidth">EXIF 固有宽：竖屏为带衬底的横排块，横屏为无衬底的纵排列。</param>
/// <param name="ExifHeight">EXIF 固有高（同上，横屏为全部项纵排的总高）。</param>
/// <param name="ExifItemSpacing">EXIF 相邻项间距；横屏按整项裁掉放不下的末尾项时使用。</param>
/// <param name="RatingMinLength">横屏控制栏里星级控件至少保留的主轴长度（紧凑态最短长度）。</param>
/// <param name="ButtonSize">圆玻璃按钮与合体按钮每段的边长。</param>
/// <param name="EdgeMargin">行/列元素沿带方向到窗口缘（或贴角带）的边距。</param>
/// <param name="RowInset">行内间距：无实物遮挡边上行与窗口缘的最小距离、栏背景超出元素的余量与主图距栏缘的避让口径。</param>
/// <param name="GroupGap">相邻按钮组之间的间距。</param>
public readonly record struct PhoneLayoutInput(
    bool Landscape,
    bool NotchOnLeft,
    double Width,
    double Height,
    EdgeInsets Safe,
    EdgeInsets LeadBand,
    EdgeInsets ControlBand,
    bool FilmVisible,
    bool ControlVisible,
    bool ExifVisible,
    bool AnalysisVisible,
    bool ShowNavigation,
    double FilterWidth,
    double FilmBand,
    double FilmOuterPad,
    double AnalysisCross,
    int ExifCount,
    double ExifWidth,
    double ExifHeight,
    double ExifItemSpacing,
    double RatingMinLength,
    double ButtonSize,
    double EdgeMargin,
    double RowInset,
    double GroupGap);

/// <summary>手机形态槽位结果；空槽位宽高为 0，栏背景宽或高为 0 表示不绘制。</summary>
/// <param name="Filter">筛选控件：竖屏在顶行前导侧；横屏有胶片栏时为合体按钮的筛选段，否则独立落在刘海旁的上耳区。</param>
/// <param name="More">更多菜单：竖屏在顶行末端；横屏有胶片栏时为合体按钮的更多段，否则独立落在刘海旁的下耳区。</param>
/// <param name="FilterMoreGroup">横屏有胶片栏时刘海侧合体按钮的外接矩形；其余情况为空。</param>
/// <param name="Exif">EXIF：竖屏为顶行中间的块，横屏为控制栏顶端的无衬底纵排列（只含 <paramref name="ExifItems"/> 项）。</param>
/// <param name="ExifItems">实际显示的 EXIF 项数（横屏放不下时裁掉末尾项）；不显示时为 0。</param>
/// <param name="Navigation">上一张/下一张合体按钮。</param>
/// <param name="Rating">星级控件。</param>
/// <param name="FitEnhance">缩放适应/雾天增强合体按钮。</param>
/// <param name="Filmstrip">胶片条：竖屏横贯整窗宽、横屏纵贯整窗高，卡片在末端留白处停靠。</param>
/// <param name="Analysis">分析栏。</param>
/// <param name="TopBar">竖屏顶栏背景。</param>
/// <param name="BottomBar">竖屏底栏背景。</param>
/// <param name="LeadingBar">横屏刘海侧栏背景。</param>
/// <param name="TrailingBar">横屏控制栏背景（有 EXIF 时）。</param>
/// <param name="PhotoAvoidance">主图适应区相对窗口四边的避让内边距（栏缘口径，与栏背景是否绘制无关）。</param>
/// <param name="FilmstripPadding">胶片条滚动两端的内容留白：各边到卡片停靠线的距离，主轴两侧有效、交叉轴恒为 0。</param>
public readonly record struct PhoneLayout(
    LayoutRect Filter,
    LayoutRect More,
    LayoutRect FilterMoreGroup,
    LayoutRect Exif,
    int ExifItems,
    LayoutRect Navigation,
    LayoutRect Rating,
    LayoutRect FitEnhance,
    LayoutRect Filmstrip,
    LayoutRect Analysis,
    LayoutRect TopBar,
    LayoutRect BottomBar,
    LayoutRect LeadingBar,
    LayoutRect TrailingBar,
    EdgeInsets PhotoAvoidance,
    EdgeInsets FilmstripPadding);

/// <summary>
/// 手机形态槽位几何（平台中立纯函数）：竖屏为顶行（筛选 / EXIF / 更多）、胶片条与底行；横屏为刘海侧列
/// （筛选更多、胶片条、上下张）与下巴侧控制栏（EXIF、星级、缩放增强自上而下），分析栏在控制栏内侧。
/// 结果同时给出每个槽位矩形、胶片条内容留白与主图适应区的四边避让。
/// <para>贴实物遮挡（灵动岛、刘海）的系统安全内缩已含视觉边距：元素直接贴安全区边，不再叠加行内间距；
/// Home 指示条的安全内缩同样含一份行内间距的余量，底行压进这份余量但不比本行两端更贴边；
/// 横屏下巴侧的系统对称安全内缩不是实物遮挡，控制栏不避让它。</para>
/// </summary>
public static class PhoneScreenLayout
{
    /// <summary>计算手机形态槽位；横屏刘海在右时交换左右输入后把结果整体水平镜像。</summary>
    /// <param name="input">窗口、安全区、贴角带、显隐状态与固有尺寸。</param>
    /// <returns>槽位矩形、胶片条内容留白与主图避让。</returns>
    public static PhoneLayout Compute(in PhoneLayoutInput input)
    {
        if (!input.Landscape) return ComputePortrait(input);

        if (input.NotchOnLeft) return ComputeLandscape(input);

        var safe = input.Safe;
        var lead = input.LeadBand;
        var control = input.ControlBand;
        var mirrored = input with
        {
            Safe = new EdgeInsets(safe.Right, safe.Top, safe.Left, safe.Bottom),
            LeadBand = new EdgeInsets(lead.Right, lead.Top, lead.Left, lead.Bottom),
            ControlBand = new EdgeInsets(control.Right, control.Top, control.Left, control.Bottom),
        };
        return Mirror(ComputeLandscape(mirrored), input.Width);
    }

    /// <summary>竖屏顶行是否带 EXIF 块（手机 EXIF 开关打开且有显示项）；不带时筛选与更多上移到刘海两旁。</summary>
    /// <param name="input">槽位输入。</param>
    /// <returns>顶行带 EXIF 块时为 true。</returns>
    public static bool PortraitExifShown(in PhoneLayoutInput input)
        => input.ExifVisible && input.ExifCount > 0 && input.ExifWidth > 0 && input.ExifHeight > 0;

    /// <summary>横屏控制栏是否带 EXIF 列：控制栏可见、EXIF 开关打开且有显示项（双条件）。</summary>
    /// <param name="input">槽位输入。</param>
    /// <returns>控制栏带 EXIF 列时为 true（放不下任何一项时实际仍可能不显示）。</returns>
    public static bool LandscapeExifShown(in PhoneLayoutInput input)
        => input.ControlVisible && PortraitExifShown(input);

    /// <summary>
    /// 竖屏顶行上缘：带 EXIF 时贴灵动岛/刘海安全区下缘（无遮挡时距窗口缘 <c>RowInset</c>）；
    /// 不带 EXIF 时上移进刘海两旁的显示区，按钮下缘贴安全区下缘、上缘距窗口缘不少于 <c>EdgeMargin</c>，
    /// 且不低于带 EXIF 时的位置。宿主据此在同一位置探测顶行的贴角带。
    /// </summary>
    /// <param name="input">槽位输入。</param>
    /// <returns>顶行上缘到窗口上缘的距离。</returns>
    public static double TopRowY(in PhoneLayoutInput input)
        => PortraitExifShown(input)
            ? RowOffset(input.Safe.Top, input.RowInset)
            : EarOffset(input.Safe.Top, input.ButtonSize, input.EdgeMargin, input.RowInset);

    /// <summary>
    /// 横屏刘海侧列从刘海侧窗口缘起算的宽度（列缘）：有胶片栏时为胶片条与两段宽按钮共同决定的列缘，
    /// 否则为耳区按钮的内缘。宿主在这条列上探测刘海侧贴角带。
    /// </summary>
    /// <param name="input">槽位输入（按刘海在左理解左右）。</param>
    /// <returns>列宽。</returns>
    public static double LeadStripWidth(in PhoneLayoutInput input)
    {
        double lead = input.NotchOnLeft ? input.Safe.Left : input.Safe.Right;
        return input.FilmVisible
            ? LeadColumnRight(lead, input)
                : EarOffset(lead, input.ButtonSize, input.EdgeMargin, input.RowInset) + input.ButtonSize;
    }

    /// <summary>
    /// 横屏控制栏从下巴侧窗口缘起算的宽度：按钮外缘距窗口缘 <c>EdgeMargin</c>，EXIF 列与按钮同一中轴。
    /// 宿主在这条列上探测控制栏的上下贴角距离。
    /// </summary>
    /// <param name="input">槽位输入。</param>
    /// <returns>列宽（不含栏背景外扩）。</returns>
    public static double ControlStripWidth(in PhoneLayoutInput input)
    {
        double column = LandscapeExifShown(input) ? Math.Max(input.ButtonSize, input.ExifWidth) : input.ButtonSize;
        return input.EdgeMargin + input.ButtonSize / 2 + column / 2;
    }

    private static PhoneLayout ComputePortrait(in PhoneLayoutInput input)
    {
        var safe = input.Safe;
        var lead = input.LeadBand;
        var control = input.ControlBand;
        double button = input.ButtonSize;
        double edge = input.EdgeMargin;
        double inset = input.RowInset;
        double gap = input.GroupGap;

        double rowTop = TopRowY(input);
        double rowBottom = rowTop + button;
        double leadLeft = Math.Max(safe.Left, lead.Left) + edge;
        double leadRight = input.Width - Math.Max(safe.Right, lead.Right) - edge;

        var filter = Rect(leadLeft, rowTop, input.FilterWidth, button);
        var more = Rect(leadRight - button, rowTop, button, button);

        var exif = default(LayoutRect);
        if (PortraitExifShown(input))
        {
            double intervalLeft = Has(filter) ? filter.MaxX + gap : leadLeft;
            double intervalRight = Has(more) ? more.X - gap : leadRight;
            double exifWidth = Math.Min(input.ExifWidth, intervalRight - intervalLeft);
            if (exifWidth > 0)
            {
                // 区间可由浮点误差在「恰好等宽」时比宽度小一个 ulp，上界先与左界取大再夹取。
                double x = Math.Clamp(
                    (input.Width - exifWidth) / 2, intervalLeft, Math.Max(intervalLeft, intervalRight - exifWidth));
                exif = Rect(x, rowTop + (button - input.ExifHeight) / 2, exifWidth, input.ExifHeight);
            }
        }

        var analysis = input.AnalysisVisible
            ? Rect(0, rowBottom + inset, input.Width, input.AnalysisCross)
            : default;
        var topBar = Has(analysis) ? Rect(0, 0, input.Width, analysis.MaxY) : default;

        double controlLeft = Math.Max(safe.Left, control.Left) + edge;
        double controlRight = input.Width - Math.Max(safe.Right, control.Right) - edge;
        // Home 指示条的安全内缩自带一份行内间距的视觉余量：底行压进这份余量，但不比本行两端更贴窗口缘。
        double bottomGap = Math.Max(
            Math.Max(safe.Bottom - inset, Math.Min(controlLeft, input.Width - controlRight)), inset);
        double bottomEdge = input.Height - bottomGap;
        double bottomTop = bottomEdge - button;

        var navigation = default(LayoutRect);
        var rating = default(LayoutRect);
        var fitEnhance = default(LayoutRect);
        if (input.ControlVisible)
        {
            if (input.ShowNavigation)
                navigation = Rect(controlLeft, bottomTop, button * 2, button);

            fitEnhance = Rect(controlRight - button * 2, bottomTop, button * 2, button);

            double ratingLeft = Has(navigation) ? navigation.MaxX + gap : controlLeft;
            double ratingRight = fitEnhance.X - gap;
            rating = Rect(ratingLeft, bottomTop, ratingRight - ratingLeft, button);
        }

        var filmstrip = default(LayoutRect);
        double filmBottom = input.ControlVisible ? bottomTop - inset : bottomEdge;
        if (input.FilmVisible)
            filmstrip = Rect(0, filmBottom - input.FilmBand, input.Width, input.FilmBand);

        var filmstripPadding = new EdgeInsets(controlLeft, 0, input.Width - controlRight, 0);

        var bottomBar = Has(filmstrip)
            ? Rect(0, filmstrip.Y - inset, input.Width, input.Height - (filmstrip.Y - inset))
            : default;

        double avoidTop = Has(topBar) ? topBar.MaxY : rowBottom + inset;

        double avoidBottom = Has(bottomBar)
            ? input.Height - bottomBar.Y
            : input.ControlVisible ? input.Height - (bottomTop - inset) : 0;

        return new PhoneLayout(
            filter, more, default, exif, Has(exif) ? input.ExifCount : 0,
            navigation, rating, fitEnhance, filmstrip, analysis,
            topBar, bottomBar, default, default,
            new EdgeInsets(safe.Left, avoidTop, safe.Right, avoidBottom),
            filmstripPadding);
    }

    private static PhoneLayout ComputeLandscape(in PhoneLayoutInput input)
    {
        var safe = input.Safe;
        var lead = input.LeadBand;
        var control = input.ControlBand;
        double button = input.ButtonSize;
        double edge = input.EdgeMargin;
        double inset = input.RowInset;
        double gap = input.GroupGap;

        double leadTop = Math.Max(safe.Top, lead.Top) + inset;
        double leadBottomEdge = input.Height - Math.Max(Math.Max(safe.Bottom, lead.Bottom), inset);

        LayoutRect filter, more;
        var group = default(LayoutRect);
        var navigation = default(LayoutRect);
        var filmstrip = default(LayoutRect);
        var leadingBar = default(LayoutRect);
        EdgeInsets filmstripPadding;
        double avoidLeft;
        if (input.FilmVisible)
        {
            double columnRight = LeadColumnRight(safe.Left, input);
            double groupX = columnRight - inset - button * 2;
            group = Rect(groupX, leadTop, button * 2, button);
            filter = Rect(groupX, leadTop, button, button);
            more = Rect(groupX + button, leadTop, button, button);

            if (input.ShowNavigation)
                navigation = Rect(columnRight - inset - button * 2, leadBottomEdge - button, button * 2, button);

            filmstrip = Rect(columnRight - input.FilmBand, 0, input.FilmBand, input.Height);
            filmstripPadding = new EdgeInsets(
                0,
                group.MaxY + inset,
                0,
                Has(navigation) ? input.Height - navigation.Y + inset : input.Height - leadBottomEdge);
            leadingBar = Rect(0, 0, columnRight, input.Height);
            avoidLeft = columnRight;
        }
        else
        {
            // 没有胶片栏时筛选与更多各占刘海上下两旁的显示区，与竖屏顶行的耳区同一口径。
            double earX = EarOffset(safe.Left, button, edge, inset);
            filter = Rect(earX, leadTop, button, button);
            more = Rect(earX, leadBottomEdge - button, button, button);
            filmstripPadding = new EdgeInsets(0, leadTop, 0, input.Height - leadBottomEdge);
            avoidLeft = Math.Max(safe.Left, earX + button + inset);
        }

        var exif = default(LayoutRect);
        int exifItems = 0;
        var rating = default(LayoutRect);
        var fitEnhance = default(LayoutRect);
        var trailingBar = default(LayoutRect);
        double controlEdge = input.Width;
        if (input.ControlVisible)
        {
            double buttonX = input.Width - edge - button;
            double axis = buttonX + button / 2;
            double columnTop = Math.Max(safe.Top, control.Top) + inset;
            double columnBottom = input.Height - Math.Max(Math.Max(safe.Bottom, control.Bottom), inset);

            fitEnhance = Rect(buttonX, columnBottom - button * 2, button, button * 2);

            if (LandscapeExifShown(input))
            {
                // EXIF 只按整项显示：星级控件先保住紧凑态最短长度，剩余长度能放几项放几项，裁掉的是末尾项。
                double room = fitEnhance.Y - gap - input.RatingMinLength - gap - columnTop;
                double pitch = (input.ExifHeight + input.ExifItemSpacing) / input.ExifCount;
                exifItems = Math.Clamp(
                    (int)Math.Floor((room + input.ExifItemSpacing) / pitch + 1e-9), 0, input.ExifCount);
                if (exifItems > 0)
                {
                    exif = Rect(axis - input.ExifWidth / 2, columnTop, input.ExifWidth,
                        exifItems * pitch - input.ExifItemSpacing);
                }

                if (!Has(exif)) exifItems = 0;
            }

            double ratingTop = Has(exif) ? exif.MaxY + gap : columnTop;
            rating = Rect(buttonX, ratingTop, button, fitEnhance.Y - gap - ratingTop);

            double columnLeft = Has(exif) ? Math.Min(exif.X, buttonX) : buttonX;
            controlEdge = columnLeft - inset;
            if (Has(exif))
                trailingBar = Rect(controlEdge, 0, input.Width - controlEdge, input.Height);
        }

        var analysis = default(LayoutRect);
        if (input.AnalysisVisible)
        {
            double analysisRight = input.ControlVisible ? controlEdge : input.Width - safe.Right;
            analysis = Rect(analysisRight - input.AnalysisCross, 0, input.AnalysisCross, input.Height);
        }

        double avoidRight = Has(analysis)
            ? input.Width - analysis.X
            : input.ControlVisible ? input.Width - controlEdge
            : 0;

        return new PhoneLayout(
            filter, more, group, exif, exifItems, navigation, rating, fitEnhance, filmstrip, analysis,
                default, default, leadingBar, trailingBar,
                new EdgeInsets(avoidLeft, safe.Top, avoidRight, 0),
                filmstripPadding);
    }

    /// <summary>
    /// 横屏刘海侧列缘：卡片贴刘海安全区（靠刘海一侧的卡片内边距让进安全区，无刘海时不越过窗口缘），
    /// 且列宽不小于「边距 + 两段宽按钮 + 行内间距」。
    /// </summary>
    private static double LeadColumnRight(double leadSafe, in PhoneLayoutInput input)
    {
        double filmX = Math.Max(0, leadSafe - input.FilmOuterPad);
        return Math.Max(filmX + input.FilmBand, input.EdgeMargin + input.ButtonSize * 2 + input.RowInset);
    }

    /// <summary>贴一条窗口边排布的行：有实物遮挡时贴安全区边，否则距窗口缘一份行内间距。</summary>
    private static double RowOffset(double safeEdge, double inset) => Math.Max(safeEdge, inset);

    /// <summary>
    /// 伸进刘海两旁显示区的按钮：内缘贴安全区边、外缘距窗口缘不少于边距；安全区太窄时不比普通行更远离窗口缘。
    /// </summary>
    private static double EarOffset(double safeEdge, double button, double edge, double inset)
        => Math.Min(RowOffset(safeEdge, inset), Math.Max(safeEdge - button, edge));

    private static PhoneLayout Mirror(in PhoneLayout layout, double width) => new(
        MirrorRect(layout.Filter, width),
            MirrorRect(layout.More, width),
        MirrorRect(layout.FilterMoreGroup, width),
        MirrorRect(layout.Exif, width),
            layout.ExifItems,
            MirrorRect(layout.Navigation, width),
        MirrorRect(layout.Rating, width),
        MirrorRect(layout.FitEnhance, width),
        MirrorRect(layout.Filmstrip, width),
            MirrorRect(layout.Analysis, width),
        MirrorRect(layout.TopBar, width),
        MirrorRect(layout.BottomBar, width),
        MirrorRect(layout.LeadingBar, width),
            MirrorRect(layout.TrailingBar, width),
        new EdgeInsets(
            layout.PhotoAvoidance.Right,
            layout.PhotoAvoidance.Top,
            layout.PhotoAvoidance.Left,
            layout.PhotoAvoidance.Bottom),
        new EdgeInsets(
            layout.FilmstripPadding.Right,
            layout.FilmstripPadding.Top,
            layout.FilmstripPadding.Left,
            layout.FilmstripPadding.Bottom));

    private static LayoutRect MirrorRect(LayoutRect rect, double width)
        => Has(rect) ? new LayoutRect(width - rect.X - rect.Width, rect.Y, rect.Width, rect.Height) : default;

    private static bool Has(LayoutRect rect) => rect.Width > 0 && rect.Height > 0;

    private static LayoutRect Rect(double x, double y, double width, double height)
        => width > 0 && height > 0 ? new LayoutRect(x, y, width, height) : default;
}
