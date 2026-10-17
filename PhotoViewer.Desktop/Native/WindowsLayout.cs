using Windows.UI;

namespace PhotoViewer.Desktop.Native;

/// <summary>
/// 原生壳几何与配色常量(唯一来源)。
/// 数值取自旧 Avalonia 设计的实测口径,harness 与实现都从这里取值,避免断言与实现一起漂移。
/// </summary>
internal static class WindowsLayout
{
    // ===== 四分区 =====

    /// <summary>左右布局文件栏内容宽(分隔线另计 1)。</summary>
    public const double RailWidth = 116;

    /// <summary>分区之间的分隔线厚度。</summary>
    public const double DividerThickness = 1;

    /// <summary>
    /// 文件栏外框占位宽 = 内容 116 + 右侧 1 px 分隔线。
    /// WinUI 的 <c>Border.Width</c> 含 <c>BorderThickness</c>,直接写 116 会让内容只剩 115,
    /// 胶片条可用宽因此变成 95 &lt; 卡片 96,卡片被压窄 4 DIP。
    /// </summary>
    public const double RailFrameWidth = RailWidth + DividerThickness;

    // ===== 筛选条 =====

    /// <summary>下拉框占位宽。</summary>
    public const double FilterItemWidth = 90;

    /// <summary>下拉框占位高。</summary>
    public const double FilterItemHeight = 32;

    /// <summary>筛选控件间距。</summary>
    public const double FilterSpacing = 5;

    /// <summary>筛选条内边距(四周 10,与旧壳 <c>Margin="10"</c> 一致)。</summary>
    public const double FilterMargin = 10;

    // ===== 胶片条 =====

    /// <summary>胶片条滚动区最小高度。</summary>
    public const double FilmstripMinHeight = 148;

    /// <summary>胶片条列表左内边距。</summary>
    public const double FilmstripPadLeft = 10;

    /// <summary>胶片条列表上内边距。</summary>
    public const double FilmstripPadTop = 5;

    /// <summary>胶片条列表右内边距。</summary>
    public const double FilmstripPadRight = 10;

    /// <summary>胶片条列表下内边距。</summary>
    public const double FilmstripPadBottom = 10;

    /// <summary>卡片外框宽。</summary>
    public const double CardWidth = 90;

    /// <summary>卡片外框高。</summary>
    public const double CardHeight = 138;

    /// <summary>卡片四周占位。</summary>
    public const double CardMargin = 3;

    /// <summary>卡片外框圆角。</summary>
    public const double CardCornerRadius = 7;

    /// <summary>卡片外框描边厚度(当前项/缓存态)。</summary>
    public const double CardBorderThickness = 2;

    /// <summary>缩略图方形边长。</summary>
    public const double ThumbnailSize = 80;

    /// <summary>缩略图圆角。</summary>
    public const double ThumbnailCornerRadius = 4;

    /// <summary>卡片文件名行高(字号 10)。</summary>
    public const double CardNameFontSize = 10;

    /// <summary>卡片第二行宽度(日期/相似度条)。</summary>
    public const double CardSecondLineWidth = 80;

    /// <summary>卡片第二行字号。</summary>
    public const double CardSecondLineFontSize = 9;

    /// <summary>卡片星级按钮边长。</summary>
    public const double CardStarSize = 14;

    /// <summary>卡片星级字号。</summary>
    public const double CardStarFontSize = 10;

    /// <summary>抖动徽标边长。</summary>
    public const double ShakeBadgeSize = 16;

    /// <summary>抖动徽标字号。</summary>
    public const double ShakeBadgeFontSize = 11;

    /// <summary>横向卡片步进(外框 + 两侧占位)。</summary>
    public const double CardPitchHorizontal = CardWidth + (CardMargin * 2);

    /// <summary>纵向卡片步进(外框 + 上下占位)。</summary>
    public const double CardPitchVertical = CardHeight + (CardMargin * 2);

    // ===== 控制栏 =====

    /// <summary>控制栏内边距。</summary>
    public const double ControlBarPadding = 10;

    /// <summary>控制栏分组间距(换行后的行距与组间距)。</summary>
    public const double ControlBarSpacing = 10;

    /// <summary>EXIF 信息块宽。</summary>
    public const double ExifBlockWidth = 50;

    /// <summary>EXIF 信息块高。</summary>
    public const double ExifBlockHeight = 40;

    /// <summary>EXIF 信息块主轴两端留白与块间距(旧壳 WebMarginConverter '0,4|4,0')。</summary>
    public const double ExifBlockMargin = 4;

    /// <summary>EXIF 块之间的额外间距(旧壳 StackPanel Spacing=4;与两侧余白叠加成 12)。</summary>
    public const double ExifStackSpacing = 4;

    /// <summary>EXIF 标签字号。</summary>
    public const double ExifLabelFontSize = 10;

    /// <summary>EXIF 数值字号。</summary>
    public const double ExifValueFontSize = 12;

    /// <summary>评分按钮边长。</summary>
    public const double RatingButtonSize = 30;

    /// <summary>评分字形字号。</summary>
    public const double RatingGlyphFontSize = 16;

    /// <summary>评分分组主轴两端留白(旧壳 WebMarginConverter '0,8|8,0',加在星条自身而非分组边框上)。</summary>
    public const double RatingGroupMargin = 8;

    /// <summary>命令按钮边长。</summary>
    public const double CommandButtonSize = 40;

    /// <summary>命令按钮字形字号。</summary>
    public const double CommandGlyphFontSize = 18;

    /// <summary>命令按钮间距。</summary>
    public const double CommandSpacing = 8;

    // ===== 主图区提示 =====

    /// <summary>空首屏提示最大宽度。</summary>
    public const double HintMaxWidth = 500;

    /// <summary>空首屏提示标题字号。</summary>
    public const double HintTitleFontSize = 16;

    /// <summary>空首屏提示副标题字号。</summary>
    public const double HintDetailFontSize = 11;

    // ===== 窗口 =====

    /// <summary>默认窗口宽。</summary>
    public const double DefaultWindowWidth = 1200;

    /// <summary>默认窗口高。</summary>
    public const double DefaultWindowHeight = 850;

    /// <summary>最小窗口宽。</summary>
    public const double MinWindowWidth = 560;

    /// <summary>最小窗口高。</summary>
    public const double MinWindowHeight = 480;

    /// <summary>隐藏式标题栏拖动区高度(上下布局整体避让)。</summary>
    public const double CaptionHeight = 48;

    /// <summary>标题栏自动显示的热区高度(旧壳 HotZoneHeight,指针 Y ≤ 50 才显示)。</summary>
    public const double CaptionHotZoneHeight = 50;

    /// <summary>标题栏按钮外框宽(旧壳 Button.TitleBtn)。</summary>
    public const double CaptionButtonWidth = 36;

    /// <summary>标题栏按钮外框高。</summary>
    public const double CaptionButtonHeight = 32;

    /// <summary>标题栏按钮左右外边距(旧壳 Margin="4,8,4,8")。</summary>
    public const double CaptionButtonMarginX = 4;

    /// <summary>标题栏按钮上下外边距。</summary>
    public const double CaptionButtonMarginY = 8;

    /// <summary>标题栏按钮字形字号。</summary>
    public const double CaptionButtonFontSize = 12;

    /// <summary>标题栏按钮组距窗口右边距(旧壳 RightButtonsHost Margin="0,0,4,0")。</summary>
    public const double CaptionButtonsRightInset = 4;

    /// <summary>标题文本距其宿主左边缘(旧壳 Grid Margin="8,0,4,0" + TextBlock Margin="4,0,0,0")。</summary>
    public const double TitleTextLeftInset = 12;

    /// <summary>标题文本字号(旧 Avalonia 文本默认 12)。</summary>
    public const double TitleTextFontSize = 12;

    // ===== 配色 =====

    /// <summary>栏位底色(胶片条/控制栏)。</summary>
    public static readonly Color BarBackground = Color.FromArgb(0xFF, 0x11, 0x11, 0x11);

    /// <summary>分隔线与控件描边。</summary>
    public static readonly Color Divider = Color.FromArgb(0xFF, 0x33, 0x33, 0x33);

    /// <summary>观片区底色。</summary>
    public static readonly Color ViewerBackground = Color.FromArgb(0xFF, 0x00, 0x00, 0x00);

    /// <summary>卡片与信息块底色。</summary>
    public static readonly Color BlockBackground = Color.FromArgb(0xFF, 0x22, 0x22, 0x22);

    /// <summary>卡片缩略图槽底色。</summary>
    public static readonly Color ThumbnailWell = Color.FromArgb(0xFF, 0x33, 0x33, 0x33);

    /// <summary>当前项描边(清晰蓝)。</summary>
    public static readonly Color CurrentBorder = Color.FromArgb(0xFF, 0x1E, 0x90, 0xFF);

    /// <summary>主图已缓存描边(淡蓝)。</summary>
    public static readonly Color CachedBorder = Color.FromArgb(0xB4, 0x46, 0x82, 0xB4);

    /// <summary>抖动徽标底色(橙)。</summary>
    public static readonly Color ShakeBadgeBackground = Color.FromArgb(0xFF, 0xFF, 0x55, 0x00);

    /// <summary>评分金色(已评分)。</summary>
    public static readonly Color StarOn = Color.FromArgb(0xFF, 0xFF, 0xD7, 0x00);

    /// <summary>评分灰色(未评分)。</summary>
    public static readonly Color StarOff = Color.FromArgb(0xFF, 0x80, 0x80, 0x80);

    /// <summary>清零评分字形颜色(旧壳 CardStar/StarButton 的 0 星按钮为 #666)。</summary>
    public static readonly Color StarClear = Color.FromArgb(0xFF, 0x66, 0x66, 0x66);

    /// <summary>命令按钮底色。</summary>
    public static readonly Color CommandButtonBackground = Color.FromArgb(0xFF, 0x33, 0x33, 0x33);

    /// <summary>命令按钮描边。</summary>
    public static readonly Color CommandButtonBorder = Color.FromArgb(0xFF, 0x55, 0x55, 0x55);

    /// <summary>标题栏按钮底色(旧壳默认按钮底色实测 #393939)。</summary>
    public static readonly Color CaptionButtonBackground = Color.FromArgb(0xFF, 0x39, 0x39, 0x39);

    /// <summary>标题栏按钮悬停底色。</summary>
    public static readonly Color CaptionButtonHover = Color.FromArgb(0xFF, 0x4A, 0x4A, 0x4A);

    /// <summary>关闭按钮悬停底色(旧壳 #E81123)。</summary>
    public static readonly Color CaptionCloseHover = Color.FromArgb(0xFF, 0xE8, 0x11, 0x23);

    /// <summary>标题栏按钮字形颜色。</summary>
    public static readonly Color CaptionButtonForeground = Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF);

    /// <summary>标题文本颜色(旧壳 Foreground=White,Opacity 0.85)。</summary>
    public static readonly Color TitleTextForeground = Color.FromArgb(0xD9, 0xFF, 0xFF, 0xFF);

    /// <summary>提示标题文字色。</summary>
    public static readonly Color HintTitleForeground = Color.FromArgb(0xFF, 0x80, 0x80, 0x80);

    /// <summary>提示副标题文字色。</summary>
    public static readonly Color HintDetailForeground = Color.FromArgb(0xFF, 0x88, 0x88, 0x88);

    /// <summary>文件名文字色。</summary>
    public static readonly Color CardNameForeground = Color.FromArgb(0xFF, 0xEE, 0xEE, 0xEE);

    /// <summary>EXIF 标签文字色。</summary>
    public static readonly Color ExifLabelForeground = Color.FromArgb(0xFF, 0xAA, 0xAA, 0xAA);

    /// <summary>EXIF 数值文字色。</summary>
    public static readonly Color ExifValueForeground = Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF);
}
