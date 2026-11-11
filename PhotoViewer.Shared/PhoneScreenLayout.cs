namespace PhotoViewer.Shared;

/// <summary>四边内边距（逻辑点）：避开栏位与按钮后的主图适应区相对窗口四边的间距。</summary>
/// <param name="Left">左内边距。</param>
/// <param name="Top">上内边距。</param>
/// <param name="Right">右内边距。</param>
/// <param name="Bottom">下内边距。</param>
public readonly record struct EdgeInsets(double Left, double Top, double Right, double Bottom);

/// <summary>手机形态槽位几何的输入：窗口、系统安全区、两条贴边元素带的窗口缘距离、显隐状态与各元素固有尺寸。</summary>
/// <param name="Landscape">横屏排布（刘海侧左栏在下巴侧控制列的相对边）；否则竖屏排布。</param>
/// <param name="NotchOnLeft">横屏时刘海在左；false 时整体水平镜像。</param>
/// <param name="Width">窗口宽。</param>
/// <param name="Height">窗口高。</param>
/// <param name="Safe">系统安全区（刘海、圆角与 Home 指示条的避让），不含任何按元素带适配的额外距离。</param>
/// <param name="LeadBand">贴角元素带必须保持的绝对窗口缘距离：竖屏为顶行（用左右两侧，上边取安全区），横屏为刘海侧按钮列（用上下两侧，刘海侧取安全区）；数值已含安全区与该带的圆角/窗口控件适配。</param>
/// <param name="ControlBand">竖屏底行在设备竖屏口径下的绝对窗口缘距离：左右为行两端，下为到设备底边的距离（上边不用）。横屏传入同一份竖屏数值，控制列才不随旋转移动。</param>
/// <param name="FilmVisible">胶片条是否显示。</param>
/// <param name="ControlVisible">控制栏是否显示。</param>
/// <param name="AnalysisVisible">分析栏是否显示。</param>
/// <param name="ShowNavigation">是否显示上一张/下一张合体按钮。</param>
/// <param name="FilterWidth">竖屏筛选胶囊的固有宽度；横屏筛选与更多合体时每段按 <paramref name="ButtonSize"/>。</param>
/// <param name="FilmBand">胶片条交叉轴厚度：竖屏为横向胶片条高度，横屏为刘海侧左栏内容宽。</param>
/// <param name="AnalysisCross">分析栏交叉轴（竖屏为高，横屏为宽）。</param>
/// <param name="ExifWidth">EXIF 块固有宽（竖屏为横排块宽，横屏为纵排列宽）；0 表示没有 EXIF 显示项。</param>
/// <param name="ExifHeight">EXIF 块固有高（竖屏为横排块高，横屏为纵排列高）。</param>
/// <param name="ButtonSize">圆玻璃按钮与合体按钮每段的边长。</param>
/// <param name="EdgeMargin">行/列元素到安全区（或贴角带）的横向边距。</param>
/// <param name="RowInset">行到安全区/栏缘的间距，也是栏背景超出元素的余量，同时是主图距栏缘的避让口径。</param>
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
    bool AnalysisVisible,
    bool ShowNavigation,
    double FilterWidth,
    double FilmBand,
    double AnalysisCross,
    double ExifWidth,
    double ExifHeight,
    double ButtonSize,
    double EdgeMargin,
    double RowInset,
    double GroupGap);

/// <summary>手机形态槽位结果；空槽位宽高为 0，栏背景宽或高为 0 表示不绘制。</summary>
/// <param name="Filter">筛选控件：竖屏在顶行前导侧，横屏为刘海侧合体按钮的筛选段。</param>
/// <param name="More">更多菜单：竖屏在顶行末端，横屏为刘海侧合体按钮的更多段。</param>
/// <param name="FilterMoreGroup">横屏刘海侧合体按钮的外接矩形；竖屏为空。</param>
/// <param name="Exif">EXIF 块。</param>
/// <param name="Navigation">上一张/下一张合体按钮。</param>
/// <param name="Rating">星级控件。</param>
/// <param name="FitEnhance">缩放适应/雾天增强合体按钮。</param>
/// <param name="Filmstrip">胶片条：竖屏横贯整窗宽、横屏纵贯整窗高，卡片在末端留白处停靠。</param>
/// <param name="Analysis">分析栏。</param>
/// <param name="TopBar">竖屏顶栏背景。</param>
/// <param name="BottomBar">竖屏底栏背景。</param>
/// <param name="LeadingBar">横屏刘海侧栏背景。</param>
/// <param name="TrailingBar">横屏下巴侧栏背景。</param>
/// <param name="PhotoAvoidance">主图适应区相对窗口四边的避让内边距（栏缘口径，与栏背景是否绘制无关）。</param>
/// <param name="FilmstripPadding">胶片条滚动两端的内容留白：各边到卡片停靠线的距离，主轴两侧有效、交叉轴恒为 0。</param>
public readonly record struct PhoneLayout(
    LayoutRect Filter,
    LayoutRect More,
    LayoutRect FilterMoreGroup,
    LayoutRect Exif,
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
/// 手机形态槽位几何（平台中立纯函数）：竖屏为上下两行加胶片条，横屏为刘海侧按钮列、下巴侧控制列，
/// 分析栏与 EXIF 列在控制列内侧；胶片条贯穿窗口主干轴并让卡片在其内停靠，结果同时给出每个槽位矩形、
/// 胶片条内容留白与主图适应区的四边避让。
/// </summary>
public static class PhoneScreenLayout
{
    /// <summary>计算手机形态槽位；横屏刘海在右时交换安全区与贴角带的左右后把结果整体水平镜像。</summary>
    /// <param name="input">窗口、安全区、贴角带、显隐状态与固有尺寸。</param>
    /// <returns>槽位矩形、胶片条内容留白与主图避让。</returns>
    public static PhoneLayout Compute(in PhoneLayoutInput input)
    {
        if (!input.Landscape) return ComputePortrait(input);

        if (input.NotchOnLeft) return ComputeLandscape(input);

        var safe = input.Safe;
        var lead = input.LeadBand;
        var mirrored = input with
        {
            Safe = new EdgeInsets(safe.Right, safe.Top, safe.Left, safe.Bottom),
            LeadBand = new EdgeInsets(lead.Right, lead.Top, lead.Left, lead.Bottom),
        };
        return Mirror(ComputeLandscape(mirrored), input.Width);
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

        double rowTop = safe.Top + inset;
        double rowBottom = rowTop + button;
        double leadLeft = Math.Max(safe.Left, lead.Left) + edge;
        double leadRight = input.Width - Math.Max(safe.Right, lead.Right) - edge;

        var filter = input.FilmVisible
            ? Rect(leadLeft, rowTop, input.FilterWidth, button)
            : default;
        var more = input.ControlVisible
            ? Rect(leadRight - button, rowTop, button, button)
            : default;

        var exif = default(LayoutRect);
        if (input.ControlVisible && input.ExifWidth > 0 && input.ExifHeight > 0)
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

        bool topRowVisible = Has(filter) || Has(more);
        double analysisTop = topRowVisible ? rowBottom + inset : safe.Top + inset;
        var analysis = input.AnalysisVisible
            ? Rect(0, analysisTop, input.Width, input.AnalysisCross)
            : default;
        var topBar = input.AnalysisVisible && Has(analysis)
            ? Rect(0, 0, input.Width, analysis.MaxY)
            : default;

        double bottomEdge = input.Height - Math.Max(Math.Max(safe.Bottom, control.Bottom), inset);
        double bottomTop = bottomEdge - button;
        double controlLeft = Math.Max(safe.Left, control.Left) + edge;
        double controlRight = input.Width - Math.Max(safe.Right, control.Right) - edge;

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

        double avoidTop = Has(topBar)
            ? topBar.MaxY
            : topRowVisible ? rowBottom + inset : safe.Top;

        double avoidBottom = Has(bottomBar)
            ? input.Height - bottomBar.Y
            : input.ControlVisible ? input.Height - (bottomTop - inset) : 0;

        return new PhoneLayout(
            filter, more, default, exif, navigation, rating, fitEnhance, filmstrip, analysis,
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
        double columnRight = Math.Max(safe.Left + input.FilmBand, edge + button * 2 + inset);

        int segments = (input.FilmVisible ? 1 : 0) + (input.ControlVisible ? 1 : 0);
        double groupWidth = segments * button;
        double groupX = columnRight - inset - groupWidth;
        var group = Rect(groupX, leadTop, groupWidth, button);
        var filter = default(LayoutRect);
        var more = default(LayoutRect);
        if (input.FilmVisible)
        {
            filter = Rect(groupX, leadTop, button, button);
            groupX += button;
        }

        if (input.ControlVisible)
            more = Rect(groupX, leadTop, button, button);

        var navigation = input.ControlVisible && input.ShowNavigation
            ? Rect(columnRight - inset - button * 2, leadBottomEdge - button, button * 2, button)
            : default;

        var filmstrip = input.FilmVisible
            ? Rect(columnRight - input.FilmBand, 0, input.FilmBand, input.Height)
            : default;

        var filmstripPadding = new EdgeInsets(
            0,
            Has(group) ? group.MaxY + inset : leadTop,
            0,
            Has(navigation) ? input.Height - navigation.Y + inset : input.Height - leadBottomEdge);

        var leadingBar = Has(filmstrip) ? Rect(0, 0, columnRight, input.Height) : default;

        double chin = Math.Max(control.Bottom, inset);
        double controlX = input.Width - chin - button;
        double columnTop = control.Right + edge;
        double columnBottom = input.Height - (control.Left + edge);

        var fitEnhance = input.ControlVisible ? Rect(controlX, columnTop, button, button * 2) : default;
        var rating = default(LayoutRect);
        if (input.ControlVisible)
        {
            double ratingTop = columnTop + button * 2 + gap;
            rating = Rect(controlX, ratingTop, button, columnBottom - ratingTop);
        }

        var exif = default(LayoutRect);
        var trailingBar = default(LayoutRect);
        if (input.ControlVisible && input.ExifWidth > 0 && input.ExifHeight > 0)
        {
            double exifX = controlX - gap - input.ExifWidth;
            double exifHeight = Math.Min(input.ExifHeight, columnBottom - columnTop);
            if (exifX > columnRight + gap && exifHeight > 0)
            {
                exif = Rect(exifX, columnTop, input.ExifWidth, exifHeight);
                trailingBar = Rect(exifX - inset, 0, input.Width - (exifX - inset), input.Height);
            }
        }

        var analysis = default(LayoutRect);
        if (input.AnalysisVisible)
        {
            double analysisRight = Has(trailingBar)
                ? trailingBar.X
                : input.ControlVisible ? controlX - gap : input.Width - safe.Right;
            analysis = Rect(analysisRight - input.AnalysisCross, 0, input.AnalysisCross, input.Height);
        }

        double avoidLeft = Has(group) || Has(navigation) || Has(filmstrip) ? columnRight : safe.Left;
        double avoidRight = Has(analysis)
            ? input.Width - analysis.X
            : Has(trailingBar) ? input.Width - trailingBar.X
            : input.ControlVisible ? input.Width - (controlX - inset)
            : 0;

        return new PhoneLayout(
            filter, more, group, exif, navigation, rating, fitEnhance, filmstrip, analysis,
            default, default, leadingBar, trailingBar,
            new EdgeInsets(avoidLeft, safe.Top, avoidRight, 0),
            filmstripPadding);
    }

    private static PhoneLayout Mirror(in PhoneLayout layout, double width) => new(
        MirrorRect(layout.Filter, width),
        MirrorRect(layout.More, width),
        MirrorRect(layout.FilterMoreGroup, width),
        MirrorRect(layout.Exif, width),
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
