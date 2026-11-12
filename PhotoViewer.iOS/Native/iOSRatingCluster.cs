using CoreGraphics;
using PhotoViewer.Core.Design;
using PhotoViewer.Shared;
using UIKit;

namespace PhotoViewer.iOS.Native;

/// <summary>
/// 星级控件紧凑态的星簇（RATING-07）：五颗星（冲突时最前加警示三角）沿胶囊主轴均匀分布，
/// 位置取共享 <see cref="RatingControlGeometry.CompactOffset"/>。胶囊变短时字形互相叠压，
/// 叠压次序为警示三角在最上、1 号星其次、5 号星在最下；下层字形被上层字形实心轮廓盖住的部分
/// 降为半透明并向外羽化，空心星的线条因此不会在上层星内部交叉，不叠压时没有任何附加效果。
/// 由所在玻璃胶囊的内容区承载，按压时随交互式玻璃一起缩放。
/// </summary>
internal sealed class iOSRatingCluster : UIView
{
    /// <summary>上层字形轮廓对下层的减淡：由外到内逐圈放大轮廓并按不透明度擦除，合成后轮廓内约剩 30%、外缘渐变到 0。</summary>
    private static readonly (double Grow, double Alpha)[] OcclusionPasses = [(1.36, 0.18), (1.24, 0.25), (1.12, 0.45)];

    private int _value;
    private bool _conflict;
    private bool _horizontal = true;

    /// <summary>建立透明、不拦截触摸、尺寸变化即重画的星簇视图。</summary>
    internal iOSRatingCluster() : base(CGRect.Empty)
    {
        BackgroundColor = UIColor.Clear;
        Opaque = false;
        UserInteractionEnabled = false;
        ContentMode = UIViewContentMode.Redraw;
    }

    /// <summary>当前显示星级（负值按无星级显示）。</summary>
    internal int Value => _value;

    /// <summary>是否显示冲突三角。</summary>
    internal bool Conflict => _conflict;

    /// <summary>主轴是否为水平方向。</summary>
    internal bool Horizontal => _horizontal;

    /// <summary>刷新显示值、冲突与主轴方向；有变化才重画。</summary>
    /// <param name="value">当前星级。</param>
    /// <param name="conflict">是否冲突。</param>
    /// <param name="horizontal">主轴是否水平。</param>
    internal void Update(int value, bool conflict, bool horizontal)
    {
        if (_value == value && _conflict == conflict && _horizontal == horizontal) return;
        _value = value;
        _conflict = conflict;
        _horizontal = horizontal;
        SetNeedsDisplay();
    }

    /// <summary>条目中心（本视图坐标）：1–5 为星，0 为冲突三角。</summary>
    /// <param name="item">簇内条目。</param>
    /// <returns>字形中心点。</returns>
    internal CGPoint ItemCenter(int item)
    {
        double width = (double)Bounds.Width;
        double height = (double)Bounds.Height;
        double offset = RatingControlGeometry.CompactOffset(item, _conflict, _horizontal, _horizontal ? width : height);
        return _horizontal ? new CGPoint(width / 2 + offset, height / 2) : new CGPoint(width / 2, height / 2 + offset);
    }

    /// <summary>自下而上逐个画字形：先按本字形实心轮廓减淡已画的下层，再画本字形。</summary>
    /// <param name="rect">重画区域。</param>
    public override void Draw(CGRect rect)
    {
        double size = RatingControlGeometry.CompactGlyph;
        var silhouette = iOSSymbols.Symbol("star.fill", size);
        bool bottom = true;
        for (int item = RatingControlGeometry.SlotCount - 1; item >= (_conflict ? 0 : 1); item--)
        {
            bool triangle = item == 0;
            bool lit = !triangle && item <= Math.Max(0, _value);
            var glyph = triangle ? iOSSymbols.Symbol("exclamationmark.triangle.fill", size) : lit ? silhouette : iOSSymbols.Symbol("star", size);
            var color = triangle
                ? iOSColors.FromToken(DesignTokens.ColorStarConflict)
                : lit ? iOSColors.FromToken(DesignTokens.ColorStarOn) : UIColor.TertiaryLabel;
            var center = ItemCenter(item);
            if (!bottom)
            {
                var mask = triangle ? glyph : silhouette;
                foreach (var (grow, alpha) in OcclusionPasses)
                    mask.Draw(GlyphRect(mask, center, grow), CGBlendMode.DestinationOut, (nfloat)alpha);
            }
            glyph.ApplyTintColor(color).Draw(GlyphRect(glyph, center, 1));
            bottom = false;
        }
    }

    /// <summary>以中心点与放大倍数求字形绘制矩形（保持符号自身尺寸比例）。</summary>
    /// <param name="image">符号图。</param>
    /// <param name="center">中心点。</param>
    /// <param name="grow">相对符号自身尺寸的放大倍数。</param>
    /// <returns>绘制矩形。</returns>
    private static CGRect GlyphRect(UIImage image, CGPoint center, double grow)
    {
        double width = (double)image.Size.Width * grow;
        double height = (double)image.Size.Height * grow;
        return new CGRect((double)center.X - width / 2, (double)center.Y - height / 2, width, height);
    }
}
