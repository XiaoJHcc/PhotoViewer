using Windows.UI;

namespace PhotoViewer.Desktop.Native;

/// <summary>
/// 原生壳几何与配色常量(唯一来源)。
/// 数值取自旧 Avalonia 设计的实测口径,harness 与实现都从这里取值,避免断言与实现一起漂移。
/// </summary>
internal static class WindowsLayout
{
    // ===== 五分区 =====

    /// <summary>左右布局文件栏内容宽(分隔线另计 1)。</summary>
    public const double RailWidth = 116;

    /// <summary>分区之间的分隔线厚度。</summary>
    public const double DividerThickness = 1;

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

    // ===== 缩放指示器(导航图) =====

    /// <summary>导航图长边(旧壳 ImageView 缩放指示器的 maxThumbnailSize)。</summary>
    public const double NavigatorLongEdge = 150;

    /// <summary>导航图距主图区右下角的边距。</summary>
    public const double NavigatorMargin = 12;

    /// <summary>导航图外框内边距(旧壳 Border Padding=8)。</summary>
    public const double NavigatorPadding = 8;

    /// <summary>导航图外框圆角。</summary>
    public const double NavigatorCornerRadius = 8;

    /// <summary>导航图可见框描边厚度。</summary>
    public const double NavigatorFrameThickness = 2;

    /// <summary>导航图百分比字号。</summary>
    public const double NavigatorPercentFontSize = 12;

    /// <summary>导航图百分比与缩略图的上间距(旧壳 TextBlock Margin="0,8,0,2")。</summary>
    public const double NavigatorPercentTopMargin = 8;

    /// <summary>导航图百分比的下间距。</summary>
    public const double NavigatorPercentBottomMargin = 2;

    /// <summary>导航图外框底色(旧壳 Border Background="#9222":57% 不透明的 #222222)。</summary>
    public static readonly Color NavigatorBackground = Color.FromArgb(0x92, 0x22, 0x22, 0x22);

    // ===== 胶片条空筛选结果 =====

    /// <summary>空筛选结果提示字号。</summary>
    public const double FilmstripEmptyFontSize = 11;

    /// <summary>空筛选结果提示文字色。</summary>
    public static readonly Color FilmstripEmptyForeground = Color.FromArgb(0xFF, 0x88, 0x88, 0x88);

    // ===== 窗口内状态提示(星级写回失败等可见反馈,FILM-06) =====

    /// <summary>状态提示字号。</summary>
    public const double StatusToastFontSize = 12;

    /// <summary>状态提示水平内边距。</summary>
    public const double StatusToastPaddingX = 12;

    /// <summary>状态提示垂直内边距。</summary>
    public const double StatusToastPaddingY = 6;

    /// <summary>状态提示圆角。</summary>
    public const double StatusToastCornerRadius = 6;

    /// <summary>状态提示距主图区底边的距离。</summary>
    public const double StatusToastBottomMargin = 24;

    /// <summary>状态提示停留时长(毫秒)。</summary>
    public const double StatusToastMilliseconds = 4000;

    /// <summary>状态提示底色(深灰,略高于卡片底色以便在照片上可读)。</summary>
    public static readonly Color StatusToastBackground = Color.FromArgb(0xF0, 0x22, 0x22, 0x22);

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

    // ===== 相似聚类面板 =====

    /// <summary>相似聚类面板外框底色(旧壳 #1E1E1E)。</summary>
    public static readonly Color SimilarityPanelBackground = Color.FromArgb(0xFF, 0x1E, 0x1E, 0x1E);

    /// <summary>相似聚类面板外框描边(旧壳 #404040)。</summary>
    public static readonly Color SimilarityPanelBorder = Color.FromArgb(0xFF, 0x40, 0x40, 0x40);

    /// <summary>相似聚类面板最小宽度。</summary>
    public const double SimilarityMinWidth = 106;

    /// <summary>相似聚类面板最小高度。</summary>
    public const double SimilarityMinHeight = 154;

    /// <summary>相似聚类面板说明/占位字号。</summary>
    public const double SimilarityCaptionFontSize = 11;

    /// <summary>相似聚类"未提取"等说明文字色(旧壳 #777)。</summary>
    public static readonly Color SimilarityCaptionForeground = Color.FromArgb(0xFF, 0x77, 0x77, 0x77);

    /// <summary>相似聚类占位文字色(旧壳 #555)。</summary>
    public static readonly Color SimilarityPlaceholderForeground = Color.FromArgb(0xFF, 0x55, 0x55, 0x55);

    /// <summary>提取按钮宽(与卡片 90 对齐)。</summary>
    public const double SimilarityActionWidth = 90;

    /// <summary>提取按钮/进度条高。</summary>
    public const double SimilarityActionHeight = 28;

    /// <summary>提取按钮字号。</summary>
    public const double SimilarityActionFontSize = 11;

    /// <summary>提取按钮底色(旧壳 #2A2A2A)。</summary>
    public static readonly Color SimilarityActionBackground = Color.FromArgb(0xFF, 0x2A, 0x2A, 0x2A);

    /// <summary>提取按钮描边(旧壳 #555)。</summary>
    public static readonly Color SimilarityActionBorder = Color.FromArgb(0xFF, 0x55, 0x55, 0x55);

    /// <summary>提取按钮文字色(旧壳 #CCC)。</summary>
    public static readonly Color SimilarityActionForeground = Color.FromArgb(0xFF, 0xCC, 0xCC, 0xCC);

    /// <summary>提取进度条前景(旧壳 #4A7A4A)。</summary>
    public static readonly Color SimilarityProgressForeground = Color.FromArgb(0xFF, 0x4A, 0x7A, 0x4A);

    /// <summary>提取进度文字字号。</summary>
    public const double SimilarityProgressFontSize = 10;

    /// <summary>相似聚类错误文字字号。</summary>
    public const double SimilarityErrorFontSize = 10;

    /// <summary>相似聚类错误文字色(旧壳 #CC4444)。</summary>
    public static readonly Color SimilarityErrorForeground = Color.FromArgb(0xFF, 0xCC, 0x44, 0x44);

    /// <summary>相似聚类错误文字最大宽度。</summary>
    public const double SimilarityErrorMaxWidth = 120;

    // ===== 分析栏 =====

    /// <summary>分析栏内边距(旧壳 AnalysisView Border Padding=10)。</summary>
    public const double AnalysisPadding = 10;

    /// <summary>分析栏瓦片间距(旧壳 StackPanel Spacing=8)。</summary>
    public const double AnalysisSpacing = 8;

    /// <summary>瓦片外框描边厚度。</summary>
    public const double AnalysisTileBorderThickness = 1;

    /// <summary>瓦片外框圆角(内容不带自己的圆角,圆角只来自外框裁切)。</summary>
    public const double AnalysisTileCornerRadius = 6;

    /// <summary>瓦片药丸标签字号。</summary>
    public const double AnalysisLabelFontSize = 12;

    /// <summary>瓦片占位文字字号。</summary>
    public const double AnalysisPlaceholderFontSize = 12;

    /// <summary>准星单臂长度。</summary>
    public const double AnalysisCrosshairArm = 12;

    /// <summary>细节预览与主图联动绿框的描边厚度(旧壳 StrokeThickness=3)。</summary>
    public const double AnalysisHighlightThickness = 3;

    /// <summary>抖动矢量场位图的每格像素(32 格 × 8 = 256,便于看清箭头)。</summary>
    public const int AnalysisShakeCellPixels = 8;

    /// <summary>分析栏隐藏回退计算前的延迟(旧壳 FallbackDelayMs=500)。</summary>
    public const int AnalysisFallbackDelayMilliseconds = 500;

    /// <summary>分析栏直方图取样的长边上限(直方图形状与分辨率无关,避免整幅转换)。</summary>
    public const int AnalysisHistogramSampleLongEdge = 1024;

    /// <summary>诊断瓦片位图的长边像素(网格最近邻放大到可读尺寸,另一向按照片比例收)。</summary>
    public const int AnalysisDiagnosticLongEdge = 512;

    /// <summary>分析栏瓦片外框描边色(旧壳 #444)。</summary>
    public static readonly Color AnalysisTileBorder = Color.FromArgb(0xFF, 0x44, 0x44, 0x44);

    /// <summary>分析栏瓦片底色(旧壳 #222)。</summary>
    public static readonly Color AnalysisTileBackground = Color.FromArgb(0xFF, 0x22, 0x22, 0x22);

    /// <summary>瓦片药丸标签底色(旧壳 #88000000)。</summary>
    public static readonly Color AnalysisLabelBackground = Color.FromArgb(0x88, 0x00, 0x00, 0x00);

    /// <summary>瓦片药丸标签文字色(旧壳 #aaffffff)。</summary>
    public static readonly Color AnalysisLabelForeground = Color.FromArgb(0xAA, 0xFF, 0xFF, 0xFF);

    /// <summary>瓦片占位文字色(旧壳 #777)。</summary>
    public static readonly Color AnalysisPlaceholderForeground = Color.FromArgb(0xFF, 0x77, 0x77, 0x77);

    /// <summary>细节预览联动绿框色(旧壳 #00FF00)。</summary>
    public static readonly Color AnalysisHighlightStroke = Color.FromArgb(0xFF, 0x00, 0xFF, 0x00);

    /// <summary>准星线条色。</summary>
    public static readonly Color AnalysisCrosshairStroke = Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF);

    /// <summary>相似聚类开关字号(旧壳 ToggleButton FontSize=12)。</summary>
    public const double SimilarityToggleFontSize = 12;

    /// <summary>相似聚类开关未选中悬停底色(旧壳 #333)。</summary>
    public static readonly Color SimilarityToggleHover = Color.FromArgb(0xFF, 0x33, 0x33, 0x33);

    /// <summary>相似聚类开关选中底色(旧壳 #1F3A5F)。</summary>
    public static readonly Color SimilarityToggleCheckedBackground = Color.FromArgb(0xFF, 0x1F, 0x3A, 0x5F);

    /// <summary>相似聚类开关选中悬停底色(旧壳 #26477A)。</summary>
    public static readonly Color SimilarityToggleCheckedHover = Color.FromArgb(0xFF, 0x26, 0x47, 0x7A);

    /// <summary>相似聚类开关描边(旧壳 #555)。</summary>
    public static readonly Color SimilarityToggleBorder = Color.FromArgb(0xFF, 0x55, 0x55, 0x55);

    /// <summary>相似聚类开关选中描边(旧壳 #3A6FA8)。</summary>
    public static readonly Color SimilarityToggleCheckedBorder = Color.FromArgb(0xFF, 0x3A, 0x6F, 0xA8);

    /// <summary>相似聚类开关文字色(旧壳 #AAA)。</summary>
    public static readonly Color SimilarityToggleForeground = Color.FromArgb(0xFF, 0xAA, 0xAA, 0xAA);

    /// <summary>相似聚类开关选中文字色(旧壳 #AECBEE)。</summary>
    public static readonly Color SimilarityToggleCheckedForeground = Color.FromArgb(0xFF, 0xAE, 0xCB, 0xEE);

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

    /// <summary>卡片第二行柱状进度槽底色(旧壳 #444444)。</summary>
    public static readonly Color SecondLineTrack = Color.FromArgb(0xFF, 0x44, 0x44, 0x44);

    /// <summary>卡片第二行柱状进度填充(旧壳 #991e90ff)。</summary>
    public static readonly Color SecondLineFill = Color.FromArgb(0x99, 0x1E, 0x90, 0xFF);

    /// <summary>文件名文字色。</summary>
    public static readonly Color CardNameForeground = Color.FromArgb(0xFF, 0xEE, 0xEE, 0xEE);

    /// <summary>EXIF 标签文字色。</summary>
    public static readonly Color ExifLabelForeground = Color.FromArgb(0xFF, 0xAA, 0xAA, 0xAA);

    /// <summary>EXIF 数值文字色。</summary>
    public static readonly Color ExifValueForeground = Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF);
}
