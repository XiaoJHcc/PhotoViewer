using PhotoViewer.Core.Image;

namespace PhotoViewer.Core.AI;

/// <summary>诊断瓦片类型:细节预览 / 直方图 / DINO-CV 诊断图。</summary>
public enum AnalysisTileKind
{
    /// <summary>细节预览(中心 / 对焦点):由各平台展示层按归一化中心裁剪,瓦片本身不携带位图。</summary>
    Detail,
    /// <summary>直方图:位图由 presenter 现算并持有。</summary>
    Histogram,
    /// <summary>DINO/CV 诊断图:位图来自只读库派生层、即时计算缓存或 presenter 现算。</summary>
    Diagnostic,
}

/// <summary>
/// 平台中立的分析/诊断瓦片状态,供分析栏与 DINO 诊断工具页共用。
/// 位图(<see cref="Source"/> / <see cref="Background"/>)归 <see cref="AnalysisResultCache"/>、presenter 或平台展示层所有,
/// 消费者只读取、不释放;<see cref="AnalysisTileKind.Detail"/> 瓦片不携带位图,裁剪源由各平台展示层持有。
/// </summary>
public sealed class AnalysisTile
{
    /// <summary>瓦片类型,决定展示层用哪种视图渲染。</summary>
    public AnalysisTileKind Kind { get; }

    /// <summary>左上角药丸角标;诊断瓦片会把参考点 / 判定文字折进此字段。</summary>
    public string ShortLabel { get; set; }

    /// <summary>底部说明文字(DINO 诊断工具页使用;分析栏为 null)。</summary>
    public string? Caption { get; set; }

    /// <summary>占位文本;<see cref="Source"/> 为 null 时由瓦片居中显示,细节预览与直方图可另行覆盖。</summary>
    public string? Placeholder { get; set; }

    /// <summary>主图(诊断热力图、PCA-RGB 或直方图);null 表示尚未就绪。</summary>
    public DecodedImage? Source { get; set; }

    /// <summary>背景图(压暗的原图缩略图;抖动瓦片使用)。</summary>
    public DecodedImage? Background { get; set; }

    /// <summary>抖动矢量场数据;非 null 时由展示层在内容区叠加拖影线。</summary>
    public ShakeField? ShakeField { get; set; }

    /// <summary>细节预览的裁剪中心(归一化 [0,1])。</summary>
    public double CenterU { get; set; } = 0.5;

    /// <summary>细节预览的裁剪中心纵坐标(归一化 [0,1])。</summary>
    public double CenterV { get; set; } = 0.5;

    /// <summary>对焦框宽度(归一化),非 null 时叠加绿色对焦框;仅对焦点瓦片使用。</summary>
    public double? FocusFrameWidth { get; set; }

    /// <summary>对焦框高度(归一化);仅对焦点瓦片使用。</summary>
    public double? FocusFrameHeight { get; set; }

    /// <summary>构造瓦片并给定初始角标。</summary>
    /// <param name="kind">瓦片类型。</param>
    /// <param name="shortLabel">左上角药丸角标。</param>
    public AnalysisTile(AnalysisTileKind kind, string shortLabel)
    {
        Kind = kind;
        ShortLabel = shortLabel;
        Placeholder = kind == AnalysisTileKind.Detail ? null : "未提取";
    }
}
