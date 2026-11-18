using CoreGraphics;
using PhotoViewer.Core.Design;
using UIKit;

namespace PhotoViewer.iOS.Native;

internal sealed class iOSMenuButton : UIButton
{
    internal iOSMenuButton() : base(CGRect.Empty)
    {
        ContextMenuInteractionEnabled = true;
        ShowsMenuAsPrimaryAction = true;
        PreferredMenuElementOrder = UIContextMenuConfigurationElementOrder.Fixed;
    }

    private iOSMenuButton? _peer;
    private bool _menuActive;
    private (bool Emphasized, string Symbol)? _glassState;
    internal bool UsesSharedGlass { get; init; }
    internal bool CentersDismissalPreview { get; init; }
    internal Func<UIMenu>? MenuProvider { get; set; }
    internal UIMenu? MenuSnapshot => MenuProvider?.Invoke() ?? Menu;

    /// <summary>
    /// 只重写按钮配置：激活态使用系统强调玻璃并按当前筛选换符号；玻璃背景与描边通过系统背景内缩表达，
    /// 不改变按钮 bounds、内容区或命中区。
    /// </summary>
    internal void SetGlassState(bool emphasized, string symbolName)
    {
        if (_glassState == (emphasized, symbolName)) return;
        var configuration = emphasized
            ? UIButtonConfiguration.ProminentGlassButtonConfiguration
            : UsesSharedGlass
                ? UIButtonConfiguration.PlainButtonConfiguration
                : UIButtonConfiguration.GlassButtonConfiguration;
        configuration.Image = iOSSymbols.Symbol(symbolName, DesignTokens.ToolbarCommandGlyphFontSize);
        configuration.BaseForegroundColor = emphasized ? TintColor : UIColor.Label;
        if (emphasized)
        {
            var background = configuration.Background;
            background.BackgroundInsets = new NSDirectionalEdgeInsets(4, 4, 4, 4);
            configuration.Background = background;
        }
        Configuration = configuration;
        _glassState = (emphasized, symbolName);
        if (CentersDismissalPreview)
        {
            SetNeedsLayout();
            LayoutIfNeeded();
        }
    }

    internal static void Pair(iOSMenuButton first, iOSMenuButton second)
    {
        first._peer = second;
        second._peer = first;
    }

    public override UIContextMenuConfiguration? GetConfigurationForMenu(UIContextMenuInteraction interaction, CGPoint location)
    {
        if (_peer is { _menuActive: true } peer)
        {
            peer.ContextMenuInteraction?.DismissMenu();
            return null;
        }
        if (MenuProvider is not { } provider) return base.GetConfigurationForMenu(interaction, location);
        var configuration = UIContextMenuConfiguration.Create(null, null!, _ => provider());
        configuration.PreferredMenuElementOrder = UIContextMenuConfigurationElementOrder.Fixed;
        return configuration;
    }

    /// <summary>
    /// 散场目标只修正系统当前的目标几何：先同步按钮自身布局，再让 UIKit 把菜单动画回当前按钮中心。
    /// 不改变强调态配置的写入时机，也不替换系统散场动画。
    /// </summary>
    public override UITargetedPreview? GetPreviewForDismissingMenu(UIContextMenuInteraction interaction,
        UIContextMenuConfiguration configuration)
    {
        if (!CentersDismissalPreview) return base.GetPreviewForDismissingMenu(interaction, configuration);
        if (Window is null || Superview is not { } container) return null;
        LayoutIfNeeded();
        return new UITargetedPreview(this, new UIPreviewParameters(), new UIPreviewTarget(container, Center));
    }

    public override void WillDisplayMenu(UIContextMenuInteraction interaction, UIContextMenuConfiguration configuration,
        IUIContextMenuInteractionAnimating? animator)
    {
        base.WillDisplayMenu(interaction, configuration, animator);
        _menuActive = true;
    }

    public override void WillEnd(UIContextMenuInteraction interaction, UIContextMenuConfiguration configuration,
        IUIContextMenuInteractionAnimating? animator)
    {
        base.WillEnd(interaction, configuration, animator);
        if (animator is null) _menuActive = false;
        else animator.AddCompletion(() => _menuActive = false);
    }
}
