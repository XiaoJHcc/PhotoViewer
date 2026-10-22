namespace PhotoViewer.Core.Color;

/// <summary>像素缓冲的色彩解释；旧壳未标记产物与原生明确标记产物分别描述。</summary>
public enum ImageColorSpace
{
    /// <summary>旧解码路径的未标记像素，不改变旧壳的显示解释。</summary>
    Legacy,
    /// <summary>标准 sRGB 原色与非线性传递函数。</summary>
    Srgb,
    /// <summary>Display P3 原色与 sRGB 传递函数。</summary>
    DisplayP3,
    /// <summary>Adobe RGB (1998)。</summary>
    AdobeRgb,
    /// <summary>扩展线性 sRGB，允许负值和大于一的分量，即 scRGB。</summary>
    LinearSrgb,
    /// <summary>由图像携带的 ICC profile 描述。</summary>
    Icc,
}
