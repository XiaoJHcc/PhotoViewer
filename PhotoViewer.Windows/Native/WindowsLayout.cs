using PhotoViewer.Core.Design;
using PhotoViewer.Shared;
using Windows.UI;

namespace PhotoViewer.Windows.Native;

/// <summary>
/// 原生壳几何与配色常量(唯一来源)。
/// 数值优先引用共享几何(<see cref="MainScreenLayout"/>/<see cref="FilmstripGeometry"/>/
/// <see cref="ControlBarLayout"/>/<see cref="SimilarityPanelGeometry"/>/<see cref="ExifTableGeometry"/>/
/// <see cref="AnalysisPanelGeometry"/>)与 <see cref="DesignTokens"/>；Windows 专属口径(标题栏、设置/工具窗口)
/// 仍留在这里。harness 与实现都从这里取值，避免断言与实现一起漂移。
/// </summary>
internal static class WindowsLayout
{
    // ===== 五分区 =====

    /// <summary>左右布局文件栏内容宽(分隔线另计 1)。</summary>
    public const double RailWidth = MainScreenLayout.FileBarContentWidth;

    /// <summary>分区之间的分隔线厚度。</summary>
    public const double DividerThickness = MainScreenLayout.Divider;

    // ===== 筛选条 =====

    /// <summary>下拉框占位宽。</summary>
    public const double FilterItemWidth = MainScreenLayout.FilterControlWidth;

    /// <summary>下拉框占位高。</summary>
    public const double FilterItemHeight = MainScreenLayout.FilterControlHeight;

    /// <summary>筛选控件间距。</summary>
    public const double FilterSpacing = MainScreenLayout.FilterGap;

    /// <summary>筛选条内边距(四周 10,与旧壳 <c>Margin="10"</c> 一致)。</summary>
    public const double FilterMargin = MainScreenLayout.FilterMargin;

    /// <summary>相似聚类 toggle 宽(与下拉同宽)。</summary>
    public const double FilterToggleWidth = DesignTokens.FilterToggleWidth;

    /// <summary>相似聚类 toggle 高(FILTER-01:27,不得借用 32 高的下拉)。</summary>
    public const double FilterToggleHeight = MainScreenLayout.FilterToggleHeight;

    // ===== 胶片条 =====

    /// <summary>胶片条滚动区最小高度(FILM-04 带宽:卡片 144 + 顶 5 + 底 10)。</summary>
    public const double FilmstripMinHeight = DesignTokens.FilmBandHeight;

    /// <summary>胶片条列表左内边距。</summary>
    public const double FilmstripPadLeft = FilmstripGeometry.HorizontalPadding;

    /// <summary>胶片条列表上内边距。</summary>
    public const double FilmstripPadTop = FilmstripGeometry.TopPadding;

    /// <summary>胶片条列表右内边距。</summary>
    public const double FilmstripPadRight = FilmstripGeometry.HorizontalPadding;

    /// <summary>胶片条列表下内边距。</summary>
    public const double FilmstripPadBottom = FilmstripGeometry.BottomPadding;

    /// <summary>卡片外框宽(内层占位面;外框步进见 <see cref="CardPitchHorizontal"/>)。</summary>
    public const double CardWidth = DesignTokens.FilmCardSurfaceWidth;

    /// <summary>卡片外框高(内层占位面;外框步进见 <see cref="CardPitchVertical"/>)。</summary>
    public const double CardHeight = DesignTokens.FilmCardSurfaceHeight;

    /// <summary>卡片四周占位。</summary>
    public const double CardMargin = DesignTokens.FilmCardInset;

    /// <summary>卡片外框圆角。</summary>
    public const double CardCornerRadius = DesignTokens.FilmCardCornerRadius;

    /// <summary>卡片外框描边厚度(当前项/缓存态)。</summary>
    public const double CardBorderThickness = DesignTokens.FilmCardBorderWidth;

    /// <summary>缩略图方形边长。</summary>
    public const double ThumbnailSize = DesignTokens.FilmThumbSize;

    /// <summary>缩略图圆角。</summary>
    public const double ThumbnailCornerRadius = DesignTokens.FilmThumbCornerRadius;

    /// <summary>卡片文件名行高(字号 10)。</summary>
    public const double CardNameFontSize = DesignTokens.FilmCardNameFontSize;

    /// <summary>卡片第二行宽度(日期/相似度条)。</summary>
    public const double CardSecondLineWidth = DesignTokens.FilmCardSecondLineWidth;

    /// <summary>卡片第二行字号。</summary>
    public const double CardSecondLineFontSize = DesignTokens.FilmCardSecondLineFontSize;

    /// <summary>卡片星级按钮边长。</summary>
    public const double CardStarSize = DesignTokens.FilmCardStarSize;

    /// <summary>卡片星级字号。</summary>
    public const double CardStarFontSize = DesignTokens.FilmCardStarFontSize;

    /// <summary>抖动徽标边长。</summary>
    public const double ShakeBadgeSize = DesignTokens.FilmCardBadgeSize;

    /// <summary>抖动徽标字号。</summary>
    public const double ShakeBadgeFontSize = DesignTokens.FilmCardBadgeFontSize;

    /// <summary>横向卡片步进(外框 + 两侧占位;共享几何的卡片间距为 0)。</summary>
    public const double CardPitchHorizontal = FilmstripGeometry.CardWidth;

    /// <summary>纵向卡片步进(外框 + 上下占位;共享几何的卡片间距为 0)。</summary>
    public const double CardPitchVertical = FilmstripGeometry.CardHeight;

    // ===== 控制栏 =====

    /// <summary>控制栏内边距。</summary>
    public const double ControlBarPadding = ControlBarLayout.Padding;

    /// <summary>控制栏分组间距(换行后的行距与组间距)。</summary>
    public const double ControlBarSpacing = ControlBarLayout.GroupSpacing;

    /// <summary>EXIF 信息块宽。</summary>
    public const double ExifBlockWidth = DesignTokens.ToolbarExifBlockWidth;

    /// <summary>EXIF 信息块高。</summary>
    public const double ExifBlockHeight = DesignTokens.ToolbarExifBlockHeight;

    /// <summary>EXIF 信息块主轴两端留白与块间距(旧壳 WebMarginConverter '0,4|4,0')。</summary>
    public const double ExifBlockMargin = DesignTokens.ToolbarExifInset;

    /// <summary>EXIF 块之间的额外间距(旧壳 StackPanel Spacing=4;与两侧余白叠加成 12)。</summary>
    public const double ExifStackSpacing = DesignTokens.ToolbarExifSpacing;

    /// <summary>EXIF 标签字号。</summary>
    public const double ExifLabelFontSize = DesignTokens.ToolbarExifLabelFontSize;

    /// <summary>EXIF 数值字号。</summary>
    public const double ExifValueFontSize = DesignTokens.ToolbarExifValueFontSize;

    /// <summary>评分按钮边长。</summary>
    public const double RatingButtonSize = DesignTokens.ToolbarRatingButtonSize;

    /// <summary>评分字形字号。</summary>
    public const double RatingGlyphFontSize = DesignTokens.ToolbarRatingGlyphFontSize;

    /// <summary>评分分组主轴两端留白(旧壳 WebMarginConverter '0,8|8,0',加在星条自身而非分组边框上)。</summary>
    public const double RatingGroupMargin = DesignTokens.ToolbarRatingGroupInset;

    /// <summary>命令按钮边长。</summary>
    public const double CommandButtonSize = DesignTokens.ToolbarCommandButtonSize;

    /// <summary>命令按钮字形字号。</summary>
    public const double CommandGlyphFontSize = DesignTokens.ToolbarCommandGlyphFontSize;

    /// <summary>命令按钮间距。</summary>
    public const double CommandSpacing = DesignTokens.ToolbarCommandSpacing;

    // ===== 主图区提示 =====

    /// <summary>空首屏提示最大宽度。</summary>
    public const double HintMaxWidth = DesignTokens.ImageHintMaxWidth;

    /// <summary>空首屏提示标题字号。</summary>
    public const double HintTitleFontSize = DesignTokens.ImageHintTitleFontSize;

    /// <summary>空首屏提示副标题字号。</summary>
    public const double HintDetailFontSize = DesignTokens.ImageHintDetailFontSize;

    // ===== 增强预览计算中(IMAGE-07) =====

    /// <summary>“计算中…”药丸宽。</summary>
    public const double BusyPillWidth = DesignTokens.ImageBusyWidth;

    /// <summary>“计算中…”药丸高。</summary>
    public const double BusyPillHeight = DesignTokens.ImageBusyHeight;

    /// <summary>“计算中…”药丸圆角。</summary>
    public const double BusyPillCornerRadius = DesignTokens.ImageBusyCornerRadius;

    /// <summary>“计算中…”药丸距主图区底边距离。</summary>
    public const double BusyPillBottomOffset = DesignTokens.ImageBusyBottomOffset;

    // ===== 缩放指示器(导航图) =====

    /// <summary>导航图长边(旧壳 ImageView 缩放指示器的 maxThumbnailSize)。</summary>
    public const double NavigatorLongEdge = DesignTokens.NavLongEdge;

    /// <summary>导航图距主图区右下角的边距。</summary>
    public const double NavigatorMargin = DesignTokens.NavMargin;

    /// <summary>导航图外框内边距(旧壳 Border Padding=8)。</summary>
    public const double NavigatorPadding = DesignTokens.NavPadding;

    /// <summary>导航图外框圆角。</summary>
    public const double NavigatorCornerRadius = DesignTokens.NavCornerRadius;

    /// <summary>导航图可见框描边厚度。</summary>
    public const double NavigatorFrameThickness = DesignTokens.NavFrameThickness;

    /// <summary>导航图百分比字号。</summary>
    public const double NavigatorPercentFontSize = DesignTokens.NavPercentFontSize;

    /// <summary>导航图百分比与缩略图的上间距(旧壳 TextBlock Margin="0,8,0,2")。</summary>
    public const double NavigatorPercentTopMargin = DesignTokens.NavPercentTopGap;

    /// <summary>导航图百分比的下间距。</summary>
    public const double NavigatorPercentBottomMargin = DesignTokens.NavPercentBottomGap;

    /// <summary>导航图外框底色(IMAGE-01:#222222 按 60% 不透明度叠加)。</summary>
    public static readonly Color NavigatorBackground = Token(DesignTokens.ColorNavBackground, DesignTokens.ColorNavBackgroundAlpha);

    /// <summary>导航图可见框与百分比文字色(IMAGE-01:白色)。</summary>
    public static readonly Color NavigatorFrameForeground = Token(DesignTokens.ColorNavFrame);

    // ===== 胶片条空筛选结果 =====

    /// <summary>空筛选结果提示字号。</summary>
    public const double FilmstripEmptyFontSize = 11;

    /// <summary>空筛选结果提示文字色。</summary>
    public static readonly Color FilmstripEmptyForeground = Color.FromArgb(0xFF, 0x88, 0x88, 0x88);

    // ===== 窗口内状态提示(星级写回失败等可见反馈,FILM-06) =====

    /// <summary>状态提示字号。</summary>
    public const double StatusToastFontSize = DesignTokens.StatusToastFontSize;

    /// <summary>状态提示水平内边距。</summary>
    public const double StatusToastPaddingX = DesignTokens.StatusToastPaddingX;

    /// <summary>状态提示垂直内边距。</summary>
    public const double StatusToastPaddingY = DesignTokens.StatusToastPaddingY;

    /// <summary>状态提示圆角。</summary>
    public const double StatusToastCornerRadius = DesignTokens.StatusToastCornerRadius;

    /// <summary>状态提示距主图区底边的距离。</summary>
    public const double StatusToastBottomMargin = DesignTokens.StatusToastBottomMargin;

    /// <summary>状态提示最大宽(FILM-06:min(500, 可用宽))。</summary>
    public const double StatusToastMaxWidth = DesignTokens.StatusToastMaxWidth;

    /// <summary>状态提示停留时长(毫秒)。</summary>
    public const double StatusToastMilliseconds = DesignTokens.MotionStatusToastDuration;

    /// <summary>状态提示底色(深灰,略高于卡片底色以便在照片上可读)。</summary>
    public static readonly Color StatusToastBackground = Token(DesignTokens.ColorStatusToastBackground, DesignTokens.ColorStatusToastBackgroundAlpha);

    /// <summary>状态提示文字色(FILM-06:白字,灰字在 #222 上不足 AA)。</summary>
    public static readonly Color StatusToastForeground = Token(DesignTokens.ColorStatusToastForeground);

    // ===== 窗口 =====

    /// <summary>默认窗口宽。</summary>
    public const double DefaultWindowWidth = DesignTokens.ShellWindowDefaultWidth;

    /// <summary>默认窗口高。</summary>
    public const double DefaultWindowHeight = DesignTokens.ShellWindowDefaultHeight;

    /// <summary>最小窗口宽。</summary>
    public const double MinWindowWidth = DesignTokens.ShellWindowMinWidth;

    /// <summary>最小窗口高。</summary>
    public const double MinWindowHeight = DesignTokens.ShellWindowMinHeight;

    /// <summary>隐藏式标题栏拖动区高度(上下布局整体避让)。</summary>
    public const double CaptionHeight = DesignTokens.ShellTitlebarHeight;

    /// <summary>标题栏自动显示的热区高度(旧壳 HotZoneHeight,指针 Y ≤ 50 才显示)。</summary>
    public const double CaptionHotZoneHeight = DesignTokens.ShellTitlebarHotzoneHeight;

    /// <summary>标题栏按钮外框宽(旧壳 Button.TitleBtn)。</summary>
    public const double CaptionButtonWidth = DesignTokens.ShellTitlebarButtonWidth;

    /// <summary>标题栏按钮外框高。</summary>
    public const double CaptionButtonHeight = DesignTokens.ShellTitlebarButtonHeight;

    /// <summary>标题栏按钮左右外边距(旧壳 Margin="4,8,4,8")。</summary>
    public const double CaptionButtonMarginX = 4;

    /// <summary>标题栏按钮上下外边距。</summary>
    public const double CaptionButtonMarginY = 8;

    /// <summary>标题栏按钮字形字号。</summary>
    public const double CaptionButtonFontSize = 12;

    /// <summary>标题栏按钮组距窗口右边距(旧壳 RightButtonsHost Margin="0,0,4,0")。</summary>
    public const double CaptionButtonsRightInset = DesignTokens.ShellTitlebarButtonsRightInset;

    /// <summary>标题文本距其宿主左边缘(旧壳 Grid Margin="8,0,4,0" + TextBlock Margin="4,0,0,0")。</summary>
    public const double TitleTextLeftInset = DesignTokens.ShellTitlebarTitleLeftInset;

    /// <summary>标题文本字号(旧 Avalonia 文本默认 12)。</summary>
    public const double TitleTextFontSize = DesignTokens.ShellTitlebarTitleFontSize;

    // ===== 相似聚类面板 =====

    /// <summary>相似聚类面板外框底色(旧壳 #1E1E1E)。</summary>
    public static readonly Color SimilarityPanelBackground = Token(DesignTokens.ColorSimPanelBackground);

    /// <summary>相似聚类面板外框描边(旧壳 #404040)。</summary>
    public static readonly Color SimilarityPanelBorder = Token(DesignTokens.ColorSimPanelBorder);

    /// <summary>相似聚类面板最小宽度。</summary>
    public const double SimilarityMinWidth = SimilarityPanelGeometry.MinWidth;

    /// <summary>相似聚类面板最小高度。</summary>
    public const double SimilarityMinHeight = SimilarityPanelGeometry.MinHeight;

    /// <summary>相似聚类列布局固定面板宽。</summary>
    public const double SimilarityPanelWidth = SimilarityPanelGeometry.PanelWidth;

    /// <summary>相似聚类面板外边距。</summary>
    public const double SimilarityPanelMargin = SimilarityPanelGeometry.Margin;

    /// <summary>面板四周描边厚度。</summary>
    public const double SimilarityPanelBorderWidth = SimilarityPanelGeometry.BorderWidth;

    /// <summary>相似聚类面板说明/占位字号。</summary>
    public const double SimilarityCaptionFontSize = 11;

    /// <summary>相似聚类"未提取"等说明文字色(旧壳 #777)。</summary>
    public static readonly Color SimilarityCaptionForeground = Token(DesignTokens.ColorSimCaption);

    /// <summary>相似聚类占位文字色(旧壳 #555)。</summary>
    public static readonly Color SimilarityPlaceholderForeground = Token(DesignTokens.ColorSimPlaceholder);

    /// <summary>提取按钮宽(与卡片 90 对齐)。</summary>
    public const double SimilarityActionWidth = DesignTokens.SimActionWidth;

    /// <summary>提取按钮/进度条高。</summary>
    public const double SimilarityActionHeight = DesignTokens.SimActionHeight;

    /// <summary>提取按钮/进度条圆角。</summary>
    public const double SimilarityActionCornerRadius = DesignTokens.SimActionCornerRadius;

    /// <summary>提取按钮字号。</summary>
    public const double SimilarityActionFontSize = 11;

    /// <summary>动作区两行内容的间距。</summary>
    public const double SimilarityActionSpacing = DesignTokens.SimDockSpacing;

    /// <summary>提取按钮底色(旧壳 #2A2A2A)。</summary>
    public static readonly Color SimilarityActionBackground = Token(DesignTokens.ColorSimActionBackground);

    /// <summary>提取按钮描边(旧壳 #555)。</summary>
    public static readonly Color SimilarityActionBorder = Token(DesignTokens.ColorSimActionBorder);

    /// <summary>提取按钮文字色(旧壳 #CCC)。</summary>
    public static readonly Color SimilarityActionForeground = Token(DesignTokens.ColorSimActionForeground);

    /// <summary>提取进度条前景(旧壳 #4A7A4A)。</summary>
    public static readonly Color SimilarityProgressForeground = Token(DesignTokens.ColorSimProgress);

    /// <summary>提取进度文字字号。</summary>
    public const double SimilarityProgressFontSize = 10;

    /// <summary>相似聚类错误文字字号。</summary>
    public const double SimilarityErrorFontSize = 10;

    /// <summary>相似聚类错误文字色(旧壳 #CC4444)。</summary>
    public static readonly Color SimilarityErrorForeground = Token(DesignTokens.ColorSimError);

    /// <summary>相似聚类错误文字最大宽度。</summary>
    public const double SimilarityErrorMaxWidth = DesignTokens.SimErrorMaxWidth;

    // ===== 分析栏 =====

    /// <summary>分析栏内边距(旧壳 AnalysisView Border Padding=10)。</summary>
    public const double AnalysisPadding = AnalysisPanelGeometry.Padding;

    /// <summary>分析栏瓦片间距(旧壳 StackPanel Spacing=8)。</summary>
    public const double AnalysisSpacing = AnalysisPanelGeometry.Spacing;

    /// <summary>瓦片外框描边厚度。</summary>
    public const double AnalysisTileBorderThickness = DesignTokens.AnalysisTileBorderWidth;

    /// <summary>瓦片外框圆角(内容不带自己的圆角,圆角只来自外框裁切)。</summary>
    public const double AnalysisTileCornerRadius = DesignTokens.AnalysisTileCornerRadius;

    /// <summary>瓦片药丸标签字号。</summary>
    public const double AnalysisLabelFontSize = 12;

    /// <summary>瓦片占位文字字号。</summary>
    public const double AnalysisPlaceholderFontSize = 12;

    /// <summary>瓦片底部说明文字字号(工具页使用;分析栏不设说明)。</summary>
    public const double AnalysisCaptionFontSize = DesignTokens.AnalysisCaptionFontSize;

    /// <summary>瓦片底部说明文字色(旧壳 #888)。</summary>
    public static readonly Color AnalysisCaptionForeground = Token(DesignTokens.ColorAnalysisCaption);

    /// <summary>准星单臂长度。</summary>
    public const double AnalysisCrosshairArm = DesignTokens.AnalysisCrosshairArm;

    /// <summary>细节预览与主图联动绿框的描边厚度(旧壳 StrokeThickness=3)。</summary>
    public const double AnalysisHighlightThickness = DesignTokens.AnalysisHighlightThickness;

    /// <summary>抖动矢量场位图的每格像素(32 格 × 8 = 256,便于看清箭头)。</summary>
    public const int AnalysisShakeCellPixels = 8;

    /// <summary>分析栏隐藏回退计算前的延迟(旧壳 FallbackDelayMs=500)。</summary>
    public const int AnalysisFallbackDelayMilliseconds = DesignTokens.MotionAnalysisFallbackDelay;

    /// <summary>分析栏直方图取样的长边上限(直方图形状与分辨率无关,避免整幅转换)。</summary>
    public const int AnalysisHistogramSampleLongEdge = (int)DesignTokens.AnalysisHistogramSampleLongEdge;

    /// <summary>诊断瓦片位图的长边像素(网格最近邻放大到可读尺寸,另一向按照片比例收)。</summary>
    public const int AnalysisDiagnosticLongEdge = (int)DesignTokens.AnalysisDiagnosticLongEdge;

    /// <summary>分析栏瓦片外框描边色(旧壳 #444)。</summary>
    public static readonly Color AnalysisTileBorder = Token(DesignTokens.ColorAnalysisTileBorder);

    /// <summary>分析栏瓦片底色(旧壳 #222)。</summary>
    public static readonly Color AnalysisTileBackground = Token(DesignTokens.ColorAnalysisTileBackground);

    /// <summary>瓦片药丸标签底色(旧壳 #88000000)。</summary>
    public static readonly Color AnalysisLabelBackground = Token(DesignTokens.ColorAnalysisBadgeBackground);

    /// <summary>瓦片药丸标签文字色(旧壳 #aaffffff)。</summary>
    public static readonly Color AnalysisLabelForeground = Token(DesignTokens.ColorAnalysisBadgeForeground);

    /// <summary>瓦片占位文字色(旧壳 #777)。</summary>
    public static readonly Color AnalysisPlaceholderForeground = Token(DesignTokens.ColorAnalysisPlaceholder);

    /// <summary>细节预览联动绿框色(旧壳 #00FF00)。</summary>
    public static readonly Color AnalysisHighlightStroke = Token(DesignTokens.ColorAnalysisHighlight);

    /// <summary>准星线条色。</summary>
    public static readonly Color AnalysisCrosshairStroke = Token(DesignTokens.ColorAnalysisCrosshair);

    /// <summary>相似聚类开关字号(旧壳 ToggleButton FontSize=12)。</summary>
    public const double SimilarityToggleFontSize = 12;

    /// <summary>相似聚类开关未选中悬停底色(旧壳 #333)。</summary>
    public static readonly Color SimilarityToggleHover = Token(DesignTokens.ColorFilterToggleHover);

    /// <summary>相似聚类开关选中底色(旧壳 #1F3A5F)。</summary>
    public static readonly Color SimilarityToggleCheckedBackground = Token(DesignTokens.ColorFilterToggleCheckedBackground);

    /// <summary>相似聚类开关选中悬停底色(旧壳 #26477A)。</summary>
    public static readonly Color SimilarityToggleCheckedHover = Color.FromArgb(0xFF, 0x26, 0x47, 0x7A);

    /// <summary>相似聚类开关描边(旧壳 #555)。</summary>
    public static readonly Color SimilarityToggleBorder = Token(DesignTokens.ColorFilterToggleBorder);

    /// <summary>相似聚类开关选中描边(旧壳 #3A6FA8)。</summary>
    public static readonly Color SimilarityToggleCheckedBorder = Token(DesignTokens.ColorFilterToggleCheckedBorder);

    /// <summary>相似聚类开关文字色(旧壳 #AAA)。</summary>
    public static readonly Color SimilarityToggleForeground = Token(DesignTokens.ColorFilterToggleForeground);

    /// <summary>相似聚类开关选中文字色(旧壳 #AECBEE)。</summary>
    public static readonly Color SimilarityToggleCheckedForeground = Token(DesignTokens.ColorFilterToggleCheckedForeground);

    // ===== 配色 =====

    /// <summary>栏位底色(胶片条/控制栏)。</summary>
    public static readonly Color BarBackground = Token(DesignTokens.ColorBarBackground);

    /// <summary>分隔线与控件描边。</summary>
    public static readonly Color Divider = Token(DesignTokens.ColorDivider);

    /// <summary>观片区底色。</summary>
    public static readonly Color ViewerBackground = Token(DesignTokens.ColorViewerBackground);

    /// <summary>卡片与信息块底色。</summary>
    public static readonly Color BlockBackground = Token(DesignTokens.ColorFilmCardBackground);

    /// <summary>卡片缩略图槽底色。</summary>
    public static readonly Color ThumbnailWell = Token(DesignTokens.ColorFilmThumbnailWell);

    /// <summary>当前项描边(清晰蓝)。</summary>
    public static readonly Color CurrentBorder = Token(DesignTokens.ColorFilmCardCurrentBorder);

    /// <summary>主图已缓存描边(淡蓝)。</summary>
    public static readonly Color CachedBorder = Token(DesignTokens.ColorFilmCardCachedBorder);

    /// <summary>抖动徽标底色(橙)。</summary>
    public static readonly Color ShakeBadgeBackground = Token(DesignTokens.ColorFilmShakeBadge);

    /// <summary>评分金色(已评分)。</summary>
    public static readonly Color StarOn = Token(DesignTokens.ColorStarOn);

    /// <summary>评分灰色(未评分)。</summary>
    public static readonly Color StarOff = Token(DesignTokens.ColorStarOff);

    /// <summary>清零评分字形颜色(旧壳 CardStar/StarButton 的 0 星按钮为 #666)。</summary>
    public static readonly Color StarClear = Token(DesignTokens.ColorStarClear);

    /// <summary>命令按钮底色。</summary>
    public static readonly Color CommandButtonBackground = Token(DesignTokens.ColorToolbarCommandBackground);

    /// <summary>命令按钮描边。</summary>
    public static readonly Color CommandButtonBorder = Token(DesignTokens.ColorToolbarCommandBorder);

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
    public static readonly Color SecondLineTrack = Token(DesignTokens.ColorFilmCardSecondLineTrack);

    /// <summary>卡片第二行柱状进度填充(旧壳 #991e90ff)。</summary>
    public static readonly Color SecondLineFill = Token(DesignTokens.ColorFilmCardSecondLineFill);

    /// <summary>文件名文字色。</summary>
    public static readonly Color CardNameForeground = Token(DesignTokens.ColorFilmCardName);

    /// <summary>EXIF 标签文字色。</summary>
    public static readonly Color ExifLabelForeground = Token(DesignTokens.ColorTextSecondary);

    /// <summary>EXIF 数值文字色。</summary>
    public static readonly Color ExifValueForeground = Token(DesignTokens.ColorTextPrimary);

    // ===== 设置窗口(W-M5) =====

    /// <summary>设置窗口默认宽。</summary>
    public const double SettingsWindowWidth = 960;

    /// <summary>设置窗口默认高。</summary>
    public const double SettingsWindowHeight = 720;

    /// <summary>设置窗口最小宽。</summary>
    public const double SettingsWindowMinWidth = 720;

    /// <summary>设置窗口最小高。</summary>
    public const double SettingsWindowMinHeight = 560;

    /// <summary>设置窗口左侧分类栏宽(Win11 设置页左栏口径,窗口 960 DIP 下留足内容宽)。</summary>
    public const double SettingsNavWidth = 240;

    /// <summary>设置窗口标题栏高度。</summary>
    public const double SettingsTitleBarHeight = 48;

    /// <summary>设置页内容最大宽(窗口变宽时不铺满)。</summary>
    public const double SettingsPageMaxWidth = 800;

    /// <summary>设置页内容左右留白。</summary>
    public const double SettingsPagePadding = 36;

    /// <summary>设置页内容顶部留白。</summary>
    public const double SettingsPageTopPadding = 28;

    /// <summary>设置卡片圆角(Win11 control radius)。</summary>
    public const double SettingsCardCornerRadius = 4;

    /// <summary>相邻卡片间距。</summary>
    public const double SettingsCardGap = 4;

    /// <summary>分组之间的间距。</summary>
    public const double SettingsSectionGap = 24;

    /// <summary>可排序列表单行行高。</summary>
    public const double SettingsListRowHeight = 48;

    /// <summary>可排序列表含副标题行高。</summary>
    public const double SettingsListRowHeightTall = 60;

    /// <summary>快捷键录制按钮宽度。</summary>
    public const double SettingsHotkeyButtonWidth = 120;

    /// <summary>快捷键录制按钮高度(Win11 控件高度)。</summary>
    public const double SettingsHotkeyButtonHeight = 32;

    /// <summary>数值输入框宽度。</summary>
    public const double SettingsNumberBoxWidth = 140;

    /// <summary>行标题与描述之间的间距。</summary>
    public const double SettingsCardDescriptionGap = 2;

    /// <summary>滑块行右侧数值文本宽度。</summary>
    public const double SettingsSliderValueWidth = 72;

    // ===== 工具窗口 =====

    /// <summary>工具窗口默认宽(与设置窗口同口径)。</summary>
    public const double ToolsWindowWidth = DesignTokens.ToolsWindowDefaultWidthWindows;

    /// <summary>工具窗口默认高(与设置窗口同口径)。</summary>
    public const double ToolsWindowHeight = DesignTokens.ToolsWindowDefaultHeightWindows;

    /// <summary>工具窗口最小宽(EXIF 7 列表格在 720 下会折断英文单词,抬到 800)。</summary>
    public const double ToolsWindowMinWidth = DesignTokens.ToolsWindowMinWidthWindows;

    /// <summary>工具窗口最小高。</summary>
    public const double ToolsWindowMinHeight = DesignTokens.ToolsWindowMinHeightWindows;

    /// <summary>工具页 7 列表格的 Tag ID 列宽(旧壳 60)。</summary>
    public const double ToolsTableIdWidth = ExifTableGeometry.IdColumnWidth;

    /// <summary>工具页表格的单元格左右内边距。</summary>
    public const double ToolsTableCellPaddingX = ExifTableGeometry.CellPaddingX;

    /// <summary>工具页表格的单元格上下内边距。</summary>
    public const double ToolsTableCellPaddingY = ExifTableGeometry.CellPaddingY;

    /// <summary>工具页表格值列最多显示行数(旧壳 MaxLines=3)。</summary>
    public const int ToolsValueMaxLines = ExifTableGeometry.ValueMaxLines;

    /// <summary>DINO 诊断瓦片网格的列间距与行间距。</summary>
    public const double ToolsTileSpacing = AnalysisPanelGeometry.Spacing;

    /// <summary>照片统计页文件夹列表高度。</summary>
    public const double ToolsFolderListHeight = 180;

    /// <summary>照片统计页通配符输入框高度(Win11 控件高度)。</summary>
    public const double ToolsPatternBoxHeight = 32;

    /// <summary>工具窗口标题栏图标(Segoe Fluent Icons 扳手)。</summary>
    public const string ToolsWindowGlyph = "\uE90F";

    /// <summary>把 <c>0xAARRGGBB</c> 设计令牌转换为 WinUI 颜色。</summary>
    /// <param name="argb">令牌颜色。</param>
    /// <returns>WinUI 颜色。</returns>
    private static Color Token(uint argb) => Color.FromArgb(
        (byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb);

    /// <summary>把设计令牌颜色按不透明度叠加为自己(替代令牌里的独立 alpha 值)。</summary>
    /// <param name="argb">令牌颜色。</param>
    /// <param name="alpha">0–1 的不透明度。</param>
    /// <returns>带目标不透明度的 WinUI 颜色。</returns>
    private static Color Token(uint argb, double alpha) => Color.FromArgb(
        (byte)Math.Round(Math.Clamp(alpha, 0, 1) * 255),
        (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb);
}
