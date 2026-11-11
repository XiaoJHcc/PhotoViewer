using CoreGraphics;
using PhotoViewer.Core.Design;
using UIKit;

namespace PhotoViewer.iOS.Native;

/// <summary>
/// 手机形态的固定尺寸槽位占位视图：只用于布局骨架的几何核对，后续阶段会被真实控件替换并删除。
/// 星级槽位是玻璃胶囊（系统 <see cref="UIGlassEffect"/> + 胶囊外形），EXIF 槽位是深色半透明底
/// 加统一圆角；两者都不放文字、不响应点击。
/// </summary>
internal sealed class iOSSlotPlaceholder : UIView
{
    private readonly UIView? _glass;

    /// <summary>建立占位视图；星级槽位内嵌一个铺满的玻璃胶囊。</summary>
    /// <param name="glass">是否带玻璃胶囊（星级槽位）；false 时只保留自身底色与圆角。</param>
    private iOSSlotPlaceholder(bool glass) : base(CGRect.Empty)
    {
        UserInteractionEnabled = false;
        if (glass)
        {
            var effect = new UIVisualEffectView(new UIGlassEffect())
            {
                CornerConfiguration = UICornerConfiguration.CreateCapsule(),
            };
            effect.UserInteractionEnabled = false;
            _glass = effect;
            AddSubview(effect);
        }
        else
        {
            BackgroundColor = UIColor.FromWhiteAlpha(0, .55f);
            CornerConfiguration = UICornerConfiguration.CreateUniformCorners(
                UICornerRadius.CreateFixed((nfloat)DesignTokens.ToolbarCornerRadius));
        }
    }

    /// <summary>星级槽位（玻璃胶囊），读屏标识 <c>slot.rating</c>。</summary>
    /// <returns>占位视图。</returns>
    internal static iOSSlotPlaceholder Rating() => new(true)
    {
        BackgroundColor = UIColor.Clear,
        AccessibilityIdentifier = "slot.rating",
    };

    /// <summary>EXIF 槽位（深色半透明底、统一圆角），读屏标识 <c>slot.exif</c>。</summary>
    /// <returns>占位视图。</returns>
    internal static iOSSlotPlaceholder Exif() => new(false)
    {
        AccessibilityIdentifier = "slot.exif",
    };

    /// <summary>玻璃胶囊铺满槽位矩形。</summary>
    public override void LayoutSubviews()
    {
        base.LayoutSubviews();
        if (_glass != null) _glass.Frame = Bounds;
    }
}
