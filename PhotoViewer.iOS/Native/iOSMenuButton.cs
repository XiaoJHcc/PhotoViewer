using CoreGraphics;
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
    internal Func<UIMenu>? MenuProvider { get; set; }
    internal UIMenu? MenuSnapshot => MenuProvider?.Invoke() ?? Menu;

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
