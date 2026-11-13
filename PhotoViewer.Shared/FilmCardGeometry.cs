using PhotoViewer.Core.Design;

namespace PhotoViewer.Shared;

/// <summary>胶片卡片档位（FILM-05）：大档沿用旧卡片的 96×144，小档为 64×76 的紧凑卡片。</summary>
public enum FilmCardTier
{
    /// <summary>大档：96×144 外框，含 80 方形照片区、文件名、拍摄时间、星级与抖动徽标。</summary>
    Large,

    /// <summary>小档：64×76 外框，含 54 方形照片区、一行文件名、星级角标与抖动点。</summary>
    Small,
}

/// <summary>
/// 胶片卡片两档的平台中立几何（FILM-02 / FILM-05）：卡片尺寸、主轴与交叉轴内边距、带厚、
/// 步长、选档与可见容量，以及单轴列表的文档尺寸与卡片矩形。大档逐位等于
/// <see cref="FilmstripGeometry"/>（Mac / Windows / Android 继续用旧类型）；可见范围与居中偏移
/// 直接复用 <see cref="FilmstripGeometry.VisibleRange"/> 与 <see cref="FilmstripGeometry.CenterOffset"/>。
/// </summary>
public static class FilmCardGeometry
{
    /// <summary>胶片条主轴至少要放下的卡片数；放不下 5 张大档即改用小档。</summary>
    public const int MinVisible = 5;

    /// <summary>卡片外框宽度：大档 96，小档 64。</summary>
    /// <param name="tier">卡片档位。</param>
    /// <returns>外框宽。</returns>
    public static double Width(FilmCardTier tier)
        => tier == FilmCardTier.Large ? FilmstripGeometry.CardWidth : DesignTokens.FilmCardSmallWidth;

    /// <summary>卡片外框高度：大档 144，小档 76。</summary>
    /// <param name="tier">卡片档位。</param>
    /// <returns>外框高。</returns>
    public static double Height(FilmCardTier tier)
        => tier == FilmCardTier.Large ? FilmstripGeometry.CardHeight : DesignTokens.FilmCardSmallHeight;

    /// <summary>相邻卡片间距（两档均为 0：占位与外框贴合，步长即卡片尺寸）。</summary>
    /// <param name="tier">卡片档位。</param>
    /// <returns>卡片间距。</returns>
    public static double Spacing(FilmCardTier tier) => 0;

    /// <summary>
    /// 主轴前导内边距：大档横向 10、纵向 5；小档两向两端都是 6（卡片紧贴屏幕边滚到按钮下方）。
    /// </summary>
    /// <param name="tier">卡片档位。</param>
    /// <param name="horizontal">列表是否横向。</param>
    /// <returns>首项之前的主轴内边距，也是横向首项居中的下限。</returns>
    public static double MainLead(FilmCardTier tier, bool horizontal)
        => tier == FilmCardTier.Large
            ? (horizontal ? FilmstripGeometry.HorizontalPadding : FilmstripGeometry.TopPadding)
            : DesignTokens.FilmListSmallPadMain;

    /// <summary>主轴尾随内边距：大档横向 10、纵向 10；小档两向两端都是 6。</summary>
    /// <param name="tier">卡片档位。</param>
    /// <param name="horizontal">列表是否横向。</param>
    /// <returns>末项之后的主轴内边距。</returns>
    public static double MainTrail(FilmCardTier tier, bool horizontal)
        => tier == FilmCardTier.Large
            ? FilmstripGeometry.BottomPadding
            : DesignTokens.FilmListSmallPadMain;

    /// <summary>
    /// 交叉轴前导内边距：大档横向 5、纵向 10（卡片在 116 栏内居中）；小档两向都是 8
    /// （与手机形态行距同值，横屏卡片缘与按钮列缘对齐）。
    /// </summary>
    /// <param name="tier">卡片档位。</param>
    /// <param name="horizontal">列表是否横向。</param>
    /// <returns>卡片之前的交叉轴内边距。</returns>
    public static double CrossLead(FilmCardTier tier, bool horizontal)
        => tier == FilmCardTier.Large
            ? (horizontal ? FilmstripGeometry.TopPadding : CenteredInset)
            : DesignTokens.FilmListSmallPadCross;

    /// <summary>交叉轴尾随内边距：大档横向 10、纵向 10；小档两向都是 8。</summary>
    /// <param name="tier">卡片档位。</param>
    /// <param name="horizontal">列表是否横向。</param>
    /// <returns>卡片之后的交叉轴内边距。</returns>
    public static double CrossTrail(FilmCardTier tier, bool horizontal)
        => tier == FilmCardTier.Large
            ? (horizontal ? FilmstripGeometry.BottomPadding : CenteredInset)
            : DesignTokens.FilmListSmallPadCross;

    /// <summary>
    /// 胶片条带厚（交叉轴总长）：大档横向 159、纵向 116（与 <see cref="MainScreenLayout.FilmstripBand"/>
    /// 和 <see cref="MainScreenLayout.FileBarContentWidth"/> 一致）；小档横向 92、纵向 80。
    /// </summary>
    /// <param name="tier">卡片档位。</param>
    /// <param name="horizontal">列表是否横向。</param>
    /// <returns>带厚。</returns>
    public static double BandThickness(FilmCardTier tier, bool horizontal)
        => (horizontal ? Height(tier) : Width(tier)) + CrossLead(tier, horizontal) + CrossTrail(tier, horizontal);

    /// <summary>主轴步长：横向为卡片宽，纵向为卡片高（间距为 0）。</summary>
    /// <param name="tier">卡片档位。</param>
    /// <param name="horizontal">列表是否横向。</param>
    /// <returns>相邻卡片的中心距。</returns>
    public static double Step(FilmCardTier tier, bool horizontal)
        => (horizontal ? Width(tier) : Height(tier)) + Spacing(tier);

    /// <summary>
    /// 手机形态按胶片条主轴长度选档：扣掉大档的主轴两端内边距后仍放得下 <see cref="MinVisible"/> 张大档卡片就用大档，
    /// 否则用小档。宽形态由宿主固定使用大档。比较带 1e-6 容差，正好放下时取大档。
    /// </summary>
    /// <param name="mainLength">胶片条沿主轴的整条长度。</param>
    /// <param name="horizontal">胶片条是否横向。</param>
    /// <returns>卡片档位。</returns>
    public static FilmCardTier ResolveTier(double mainLength, bool horizontal)
    {
        double usable = mainLength - MainLead(FilmCardTier.Large, horizontal) - MainTrail(FilmCardTier.Large, horizontal);
        return usable >= MinVisible * Step(FilmCardTier.Large, horizontal) - 1e-6
            ? FilmCardTier.Large
            : FilmCardTier.Small;
    }

    /// <summary>
    /// 整条胶片条能同时容纳的卡片数：按整条主轴长度除以步长向下取整。手机胶片条贯穿屏幕、
    /// 卡片可滚到按钮下方，因此按整条长度计而不扣主轴内边距。
    /// </summary>
    /// <param name="mainLength">胶片条沿主轴的整条长度。</param>
    /// <param name="tier">卡片档位。</param>
    /// <param name="horizontal">胶片条是否横向。</param>
    /// <returns>可见卡片数（不为负）。</returns>
    public static int VisibleCapacity(double mainLength, FilmCardTier tier, bool horizontal)
    {
        double step = Step(tier, horizontal);
        return step <= 0 ? 0 : Math.Max(0, (int)Math.Floor(mainLength / step));
    }

    /// <summary>首项沿主轴的起点：横向在内容宽度内居中（至少保留前导内边距），纵向固定前导内边距。</summary>
    /// <param name="tier">卡片档位。</param>
    /// <param name="horizontal">列表是否横向。</param>
    /// <param name="contentMain">文档沿主轴的长度。</param>
    /// <param name="count">项目数。</param>
    /// <returns>第一项的主轴起点。</returns>
    public static double ItemStart(FilmCardTier tier, bool horizontal, double contentMain, int count)
        => horizontal
            ? Math.Max(MainLead(tier, true), (contentMain - count * Step(tier, true) + Spacing(tier)) / 2)
            : MainLead(tier, false);

    /// <summary>文档尺寸：只在主轴延伸，空列表与短列表仍至少覆盖原生裁剪视口。</summary>
    /// <param name="tier">卡片档位。</param>
    /// <param name="horizontal">列表是否横向。</param>
    /// <param name="viewportWidth">裁剪视口宽度。</param>
    /// <param name="viewportHeight">裁剪视口高度。</param>
    /// <param name="count">项目数。</param>
    /// <returns>文档尺寸。</returns>
    public static (double Width, double Height) ContentSize(FilmCardTier tier, bool horizontal,
        double viewportWidth, double viewportHeight, int count)
    {
        double length = count == 0 ? 0 : count * Step(tier, horizontal) - Spacing(tier)
            + MainLead(tier, horizontal) + MainTrail(tier, horizontal);
        return horizontal
            ? (Math.Max(viewportWidth, length), viewportHeight)
            : (viewportWidth, Math.Max(viewportHeight, length));
    }

    /// <summary>单个卡片在文档中的矩形。</summary>
    /// <param name="tier">卡片档位。</param>
    /// <param name="horizontal">列表是否横向。</param>
    /// <param name="index">项目索引。</param>
    /// <param name="count">项目数。</param>
    /// <param name="contentWidth">文档宽度（横向项起点与纵向跨轴居中都要用）。</param>
    /// <returns>卡片矩形。</returns>
    public static (double X, double Y, double Width, double Height) ItemFrame(FilmCardTier tier, bool horizontal,
        int index, int count, double contentWidth)
        => horizontal
            ? (ItemStart(tier, true, contentWidth, count) + index * Step(tier, true),
                CrossLead(tier, true), Width(tier), Height(tier))
            : (Math.Max(0, (contentWidth - Width(tier)) / 2),
                MainLead(tier, false) + index * Step(tier, false), Width(tier), Height(tier));

    /// <summary>大档卡片在 116 点文件栏内纵向居中的两侧内边距（116 − 96 的一半）。</summary>
    private static double CenteredInset => (MainScreenLayout.FileBarContentWidth - FilmstripGeometry.CardWidth) / 2;
}
