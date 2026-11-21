using PhotoViewer.Core.Settings;

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
/// <param name="Safe">系统安全区（刘海、圆角与 Home 指示条的避让），不含任何按元素带适配的额外距离；
/// 横屏主屏不为底部手势条保留安全区，宿主在宽大于高时把 <c>Bottom</c> 传 0。</param>
/// <param name="LeadBand">贴角元素带必须保持的绝对窗口缘距离：竖屏为顶行（用左右两侧，须在 <see cref="PhoneScreenLayout.TopRowY"/> 给出的行位置探测），横屏为入口合体的实际范围贯穿窗口全高（用上下两侧）；数值已含安全区与该带的圆角/窗口控件适配。</param>
/// <param name="ControlBand">控制栏带的绝对窗口缘距离：竖屏为底行（用左右两侧），横屏为实际控制内容列（用上下两侧，探测范围不含背景留白）；横屏下缘同样取该带位置的原生圆角适应值，不从安全区里减 Home 指示条。</param>
/// <param name="FilmVisible">胶片栏开关：胶片条，横屏另含刘海侧列的上一张/下一张。</param>
/// <param name="ControlVisible">控制栏开关：星级控件与同栏按钮（竖屏底行、横屏下巴侧控制栏）。</param>
/// <param name="ExifVisible">手机形态 EXIF 开关：竖屏单独决定 EXIF 块，横屏还要控制栏可见。</param>
/// <param name="AnalysisVisible">分析栏是否显示。</param>
/// <param name="ShowNavigation">是否显示上一张/下一张合体按钮。</param>
/// <param name="FilterWidth">竖屏筛选控件的固有宽度；横屏筛选与更多每段按 <paramref name="ButtonSize"/>。</param>
/// <param name="FilmBand">胶片条交叉轴厚度：竖屏为横向胶片条高度，横屏为刘海侧列内容宽。</param>
/// <param name="FilmOuterPad">横屏纵向胶片条交叉轴两侧的卡片内边距（两侧同值）：靠刘海一侧让进安全区，靠主图一侧把卡片外框收进入口锚点。</param>
/// <param name="AnalysisCross">分析栏交叉轴：竖屏为高，横屏为区域宽（已含按邻接测出的内边距与接缝间距）。</param>
/// <param name="ExifCount">启用的 EXIF 项数；0 表示没有 EXIF 显示项。</param>
/// <param name="ExifWidth">EXIF 固有宽：竖屏为带衬底的横排块；横屏的无衬底纵排列使用固有项宽居中，最大不超过按钮与两侧边距之和。</param>
/// <param name="ExifHeight">EXIF 固有高（同上，横屏为全部项纵排的总高）。</param>
/// <param name="ExifItemSpacing">EXIF 相邻项间距；横屏按整项裁掉放不下的末尾项时使用。</param>
/// <param name="RatingMinLength">横屏控制栏里星级控件至少保留的主轴长度（紧凑态最短长度）。</param>
/// <param name="ButtonSize">圆玻璃按钮与合体按钮每段的边长。</param>
/// <param name="EdgeMargin">竖屏行沿带方向到窗口缘（或贴角带）的边距；手机横屏两侧工具区改用一份工具区内边距。</param>
/// <param name="RowInset">行内间距：无实物遮挡边上行与窗口缘的最小距离、栏背景超出元素的余量与主图距栏缘的避让口径；横屏刘海侧列不使用它。</param>
/// <param name="GroupGap">竖屏底行相邻按钮组之间的间距。</param>
/// <param name="ShowRating">是否显示星级控件。</param>
/// <param name="CommandCount">实际生效的可选命令数。</param>
/// <param name="CommandButtonSize">可选命令每段边长；为 0 时使用 <paramref name="ButtonSize"/>。</param>
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
    double GroupGap,
    bool ShowRating = true,
    int CommandCount = 0,
    double CommandButtonSize = 0);

/// <summary>手机形态槽位结果；空槽位宽高为 0，栏背景宽或高为 0 表示不绘制。</summary>
/// <param name="Filter">筛选控件：竖屏在顶行前导侧；横屏为刘海侧 88 合体按钮的筛选段（常驻，不随胶片栏开关拆分）。</param>
/// <param name="More">更多菜单：竖屏在顶行末端；横屏为合体按钮的更多段（常驻）。</param>
/// <param name="FilterMoreGroup">横屏刘海侧 88 合体按钮的外接矩形；竖屏为空。</param>
/// <param name="Exif">EXIF：竖屏为顶行中间的块，横屏为控制栏顶端的无衬底纵排列（只含 <paramref name="ExifItems"/> 项）。</param>
/// <param name="ExifItems">实际显示的 EXIF 项数（横屏放不下时裁掉末尾项）；不显示时为 0。</param>
/// <param name="Navigation">上一张/下一张合体按钮。</param>
/// <param name="Rating">星级控件。</param>
/// <param name="Commands">可选命令组（零至三个命令，零项为空）。</param>
/// <param name="Filmstrip">胶片条：竖屏横贯整窗宽、横屏纵贯整窗高并靠主图侧收进列尾，卡片在末端留白处停靠。</param>
/// <param name="Analysis">分析栏。</param>
/// <param name="TopBar">竖屏顶栏背景。</param>
/// <param name="BottomBar">竖屏底栏背景。</param>
/// <param name="LeadingBar">横屏刘海侧栏背景。</param>
/// <param name="TrailingBar">横屏连续右栏背景（有分析栏或 EXIF 时，从内侧栏缘铺到窗口外缘）。</param>
/// <param name="PhotoAvoidance">主图适应区相对窗口四边的避让内边距（栏缘口径，与栏背景是否绘制无关）。</param>
/// <param name="FilmstripPadding">胶片条滚动两端的内容留白：各边到卡片停靠线的距离，主轴两侧有效、交叉轴恒为 0。
/// 横屏按绝对停靠线给出：首卡停在合体按钮下缘 + 10，末卡停在上下张上缘上方 10（无上下张时距底部圆角 10）。</param>
public readonly record struct PhoneLayout(
    LayoutRect Filter,
    LayoutRect More,
    LayoutRect FilterMoreGroup,
    LayoutRect Exif,
    int ExifItems,
    LayoutRect Navigation,
    LayoutRect Rating,
    LayoutRect Commands,
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
/// （筛选更多合体按钮、胶片条、上下张）与下巴侧控制栏（EXIF、星级、可选命令自上而下），分析栏在控制栏内侧。
/// 结果同时给出每个槽位矩形、胶片条内容留白与主图适应区的四边避让。
/// <para>横屏刘海侧列靠主图侧右对齐：筛选 + 更多的 88 合体按钮、上下张与卡片外框的右缘统一落在由
/// <see cref="FilmSideGeometry.Measure"/> 给出的 ContentTrailing 上，列宽再往外带一份卡片尾侧内边距。
/// 入口组常驻且不随胶片栏开关移动，胶片栏关闭时只收起栏背景、列表与上下张，主图避让仍到同一列缘。</para>
/// <para>贴实物遮挡（灵动岛、刘海）的系统安全内缩已含视觉边距：元素直接贴安全区边，不再叠加行内间距；
/// 竖屏底行按产品规则最多压入 Home 安全区一份行内间距，且不比本行两端更贴边；
/// 横屏下巴侧的系统对称安全内缩不是实物遮挡，控制栏不避让它，横屏主屏也不为底部手势条保留安全区
/// （调用方在横屏传 <c>Safe.Bottom = 0</c>；真实的屏幕圆角与刘海由 <c>LeadBand</c>／<c>ControlBand</c>
/// 的贴角距离给出，不受这条规则影响）。横屏控制栏的 EXIF、星级与可选命令同轴，整组在可用纵向区间居中；
/// 星级槽位封顶展开态固有长度，多出的余量留在整组两端，末端按钮不固定在底边。</para>
/// <para>横屏控制区与分析区共用一列工具区内边距（<see cref="ToolRegionGeometry"/>）：控制按钮外缘距窗口缘一份
/// 工具区内边距，分析栏相邻时控制区省略前导内边距、接缝由分析栏末端持有；主图避让取完整组合区宽，
/// 与栏背景是否绘制无关。星级隐藏时评分槽连同最短长度与组间距一起释放，独立控制区仍是透明玻璃。</para>
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
    /// 横屏刘海侧列从刘海侧窗口缘起算的宽度：入口组与卡片带共同决定的列宽（见 <see cref="FilmSideGeometry.Measure"/>），
    /// 胶片栏开关不改变它；刘海在右时按右侧安全区取同一口径。贴角探测取入口合体的实际范围。
    /// </summary>
    /// <param name="input">槽位输入（按刘海在左理解左右）。</param>
    /// <returns>列宽。</returns>
    public static double LeadStripWidth(in PhoneLayoutInput input)
        => FilmColumnRight(input.NotchOnLeft ? input.Safe.Left : input.Safe.Right, input).Width;

    /// <summary>横屏刘海侧列测量：卡片交叉轴内边距两侧同值，入口组宽为筛选 + 更多两段按钮。</summary>
    private static FilmSideMeasure FilmColumnRight(double leadingSafe, in PhoneLayoutInput input)
        => FilmSideGeometry.Measure(leadingSafe, input.FilmBand, input.FilmOuterPad, input.FilmOuterPad,
            input.ButtonSize * 2);

    private static int CommandCount(in PhoneLayoutInput input)
        => Math.Clamp(input.CommandCount, 0, PhoneControlCommands.MaxCount);

    private static double CommandSize(in PhoneLayoutInput input)
        => input.CommandButtonSize > 0 ? input.CommandButtonSize : input.ButtonSize;

    /// <summary>横屏控制区的内容宽度（不含工具区内边距），用于贴角探测与子控件落位。</summary>
    public static double ControlContentWidth(in PhoneLayoutInput input)
        => CommandCount(input) > 0 ? Math.Max(input.ButtonSize, CommandSize(input)) : input.ButtonSize;

    /// <summary>横屏控制区从窗口缘起算的宽度，含工具区内边距。</summary>
    public static double ControlStripWidth(in PhoneLayoutInput input)
        => ToolRegionGeometry.ControlWidth(ControlContentWidth(input), input.AnalysisVisible);

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
            ? Rect(0, rowBottom, input.Width, input.AnalysisCross)
            : default;
        var topBar = Has(analysis) ? Rect(0, 0, input.Width, analysis.MaxY) : default;

        double controlLeft = Math.Max(safe.Left, control.Left) + edge;
        double controlRight = input.Width - Math.Max(safe.Right, control.Right) - edge;
        // SHELL-08：底行最多压入 Home 安全区一份行内间距，且不比本行两端更贴窗口缘。
        double bottomGap = Math.Max(
            Math.Max(safe.Bottom - inset, Math.Min(controlLeft, input.Width - controlRight)), inset);
        double bottomEdge = input.Height - bottomGap;
        int count = CommandCount(input);
        double commandSize = CommandSize(input);
        bool hasControls = input.ControlVisible && (input.ShowNavigation || input.ShowRating || count > 0);
        double rowHeight = count > 0 ? Math.Max(button, commandSize) : button;
        double bottomTop = bottomEdge - rowHeight;
        double buttonY = bottomTop + (rowHeight - button) / 2;

        var navigation = default(LayoutRect);
        var rating = default(LayoutRect);
        var commandsFrame = default(LayoutRect);
        if (hasControls)
        {
            if (input.ShowNavigation)
                navigation = Rect(controlLeft, buttonY, button * 2, button);

            if (count > 0)
                commandsFrame = Rect(controlRight - commandSize * count, bottomTop + (rowHeight - commandSize) / 2,
                    commandSize * count, commandSize);

            if (input.ShowRating)
            {
                double ratingLeft = Has(navigation) ? navigation.MaxX + gap : controlLeft;
                double ratingRight = Has(commandsFrame) ? commandsFrame.X - gap : controlRight;
                rating = Rect(ratingLeft, buttonY, ratingRight - ratingLeft, button);
            }
        }

        var filmstrip = default(LayoutRect);
        double filmBottom = hasControls ? bottomTop : bottomEdge;
        if (input.FilmVisible)
            filmstrip = Rect(0, filmBottom - input.FilmBand, input.Width, input.FilmBand);

        var filmstripPadding = new EdgeInsets(inset, 0, inset, 0);

        var bottomBar = Has(filmstrip)
            ? Rect(0, filmstrip.Y, input.Width, input.Height - filmstrip.Y)
            : default;

        double avoidTop = Has(topBar) ? topBar.MaxY : rowBottom + inset;

        double avoidBottom = Has(bottomBar)
            ? input.Height - bottomBar.Y
            : hasControls ? input.Height - (bottomTop - inset) : 0;

        return new PhoneLayout(
            filter, more, default, exif, Has(exif) ? input.ExifCount : 0,
            navigation, rating, commandsFrame, filmstrip, analysis,
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
        double gap = ToolRegionGeometry.GroupSpacing;
        double padding = FilmSideGeometry.Padding;

        // 刘海侧列的贴角距离统一取 safe/corner 带读数各加一份入口与停靠间距，首末卡与入口组共用。
        double leadTop = Math.Max(safe.Top, lead.Top) + padding;
        double leadBottomEdge = input.Height - Math.Max(safe.Bottom, lead.Bottom) - padding;

        // 入口组（筛选 + 更多）常驻且不随胶片栏开关移动；列表的刘海避让只约束卡片外框，
        // 列宽 = max(卡片外框右缘, 入口宽 88 + 一份内边距) + 卡片尾侧内边距，列表靠主图侧收进列尾。
        var column = FilmColumnRight(safe.Left, input);
        var group = FilmSideGeometry.AlignTrailing(column.ContentTrailing, leadTop, button * 2, button);
        var filter = new LayoutRect(group.X, leadTop, button, button);
        var more = new LayoutRect(group.MaxX - button, leadTop, button, button);

        var navigation = input.FilmVisible && input.ShowNavigation
            ? FilmSideGeometry.AlignTrailing(column.ContentTrailing, leadBottomEdge - button, button * 2, button)
            : default;

        var filmstrip = input.FilmVisible ? Rect(column.FilmstripX, 0, input.FilmBand, input.Height) : default;
        var filmstripPadding = new EdgeInsets(
            0,
            group.MaxY + padding,
            0,
            Has(navigation) ? input.Height - navigation.Y + padding : input.Height - leadBottomEdge);
        var leadingBar = input.FilmVisible ? Rect(0, 0, column.Width, input.Height) : default;
        double avoidLeft = column.Width;

        var exif = default(LayoutRect);
        int exifItems = 0;
        var rating = default(LayoutRect);
        var commandsFrame = default(LayoutRect);
        var trailingBar = default(LayoutRect);
        double controlEdge = input.Width - ControlStripWidth(input);
        if (input.ControlVisible)
        {
            double axis = input.Width - ToolRegionGeometry.Padding - ControlContentWidth(input) / 2;
            double columnTop = Math.Max(safe.Top, control.Top) + ToolRegionGeometry.Padding;
            double columnBottom = input.Height - Math.Max(safe.Bottom, control.Bottom) - ToolRegionGeometry.Padding;
            double columnLength = Math.Max(0, columnBottom - columnTop);
            double commandSize = CommandSize(input);
            double commands = commandSize * CommandCount(input);
            bool hasCommands = commands > 0;
            double ratingMin = input.ShowRating ? input.RatingMinLength : 0;
            double gaps = (input.ShowRating ? gap : 0) + (hasCommands ? gap : 0);
            double pitch = input.ExifCount > 0
                ? (input.ExifHeight + input.ExifItemSpacing) / input.ExifCount
                : 0;

            if (LandscapeExifShown(input))
            {
                // EXIF 只按整项显示：先给可见星级保住紧凑态最短长度与命令组，剩余长度能放几项放几项，裁掉的是末尾项。
                double room = columnLength - commands - ratingMin - gaps;
                exifItems = Math.Clamp(
                    (int)Math.Floor((room + input.ExifItemSpacing) / pitch + 1e-9), 0, input.ExifCount);
            }

            double exifHeight = exifItems > 0 ? exifItems * pitch - input.ExifItemSpacing : 0;
            // 星级使用剩余长度并封顶展开态固有长度，只有可见的组之间留间距。
            double ratingLength = input.ShowRating
                ? Math.Min(RatingControlGeometry.ExpandedLength,
                    Math.Max(0, columnLength - exifHeight - (exifHeight > 0 ? gap : 0)
                        - commands - (hasCommands ? gap : 0)))
                : 0;
            bool ratingShown = ratingLength > 0;
            double exifGap = exifHeight > 0 && (ratingShown || hasCommands) ? gap : 0;
            double ratingGap = ratingShown && hasCommands ? gap : 0;
            double total = exifHeight + exifGap + ratingLength + ratingGap + commands;
            double groupTop = ToolRegionGeometry.CenteredStart(columnTop, columnLength, total);

            if (exifHeight > 0)
            {
                // 无衬底列按固有项宽居中，最大不超过按钮与两侧边距之和；开关不改变控制列宽。
                double exifWidth = Math.Min(input.ExifWidth, ControlContentWidth(input) + ToolRegionGeometry.Padding * 2);
                exif = Rect(axis - exifWidth / 2, groupTop, exifWidth, exifHeight);
            }

            double ratingTop = groupTop + exifHeight + exifGap;
            if (ratingShown) rating = Rect(axis - button / 2, ratingTop, button, ratingLength);
            commandsFrame = Rect(axis - commandSize / 2, ratingTop + ratingLength + ratingGap, commandSize, commands);
        }

        var analysis = default(LayoutRect);
        if (input.AnalysisVisible)
        {
            double analysisRight = input.ControlVisible ? controlEdge : input.Width;
            analysis = Rect(analysisRight - input.AnalysisCross, 0, input.AnalysisCross, input.Height);
        }

        if (Has(analysis) || Has(exif))
        {
            double backgroundLeft = Has(analysis) ? analysis.X : controlEdge;
            trailingBar = Rect(backgroundLeft, 0, input.Width - backgroundLeft, input.Height);
        }

        double avoidRight = Has(analysis)
            ? input.Width - analysis.X
            : input.ControlVisible ? input.Width - controlEdge
            : 0;

        return new PhoneLayout(
            filter, more, group, exif, exifItems, navigation, rating, commandsFrame, filmstrip, analysis,
                default, default, leadingBar, trailingBar,
                new EdgeInsets(avoidLeft, safe.Top, avoidRight, 0),
                filmstripPadding);
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
        MirrorRect(layout.Commands, width),
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
