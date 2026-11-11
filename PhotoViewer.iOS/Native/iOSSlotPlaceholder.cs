using CoreGraphics;
using PhotoViewer.Core.Design;
using UIKit;

namespace PhotoViewer.iOS.Native;

/// <summary>
/// 手机形态 EXIF 槽位的占位视图：只用于布局骨架的几何核对，EXIF 块换新样式时随该组件替换并删除。
/// 占位是深色半透明底加统一圆角，不放文字、不响应点击。星级槽位已由真实控件
/// <see cref="iOSRatingControl"/> 承担，不再经过本类。
/// </summary>
internal sealed class iOSSlotPlaceholder : UIView
{
    /// <summary>建立占位视图：深色半透明底与统一圆角。</summary>
    private iOSSlotPlaceholder() : base(CGRect.Empty)
    {
        UserInteractionEnabled = false;
        BackgroundColor = UIColor.FromWhiteAlpha(0, .55f);
        CornerConfiguration = UICornerConfiguration.CreateUniformCorners(
            UICornerRadius.CreateFixed((nfloat)DesignTokens.ToolbarCornerRadius));
    }

    /// <summary>EXIF 槽位（深色半透明底、统一圆角），读屏标识 <c>slot.exif</c>。</summary>
    /// <returns>占位视图。</returns>
    internal static iOSSlotPlaceholder Exif() => new()
    {
        AccessibilityIdentifier = "slot.exif",
    };
}
