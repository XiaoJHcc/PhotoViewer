using CoreGraphics;
using Foundation;
using PhotoViewer.Core;
using PhotoViewer.Core.Design;
using PhotoViewer.Core.Platform;
using PhotoViewer.ViewModels.Main;
using System.ComponentModel;
using UIKit;

namespace PhotoViewer.iOS.Native;

internal sealed class iOSRatingSourcesPopover : IDisposable
{
    private const int StarCount = 6;
    private const double LabelHeight = 16;
    private static readonly ControlBarLayout.Metrics RatingMetrics = ControlBarLayout.GetMetrics(ControlBarGroupKind.Rating);
    private static readonly double StarSize = RatingMetrics.Width;
    private static readonly double StarSpacing = RatingMetrics.Spacing;
    private static readonly double StarsWidth = StarCount * StarSize + (StarCount - 1) * StarSpacing;
    private static readonly double EntryWidth = StarsWidth + DesignTokens.PopupRatingSourcesEntryPadding * 2;
    private static readonly double ContentWidth = EntryWidth + DesignTokens.PopupPadding * 2;
    private static readonly UIFont NameFont = UIFont.SystemFontOfSize((nfloat)DesignTokens.PopupRatingSourcesNameFontSize)
        ?? throw new InvalidOperationException("系统字体不可用");
    private static readonly UIFont MetaFont = UIFont.SystemFontOfSize((nfloat)DesignTokens.PopupRatingSourcesMetaFontSize)
        ?? throw new InvalidOperationException("系统字体不可用");
    private static readonly double NameFieldHeight = NameFont.LineHeight;
    private static readonly double MetaFieldHeight = MetaFont.LineHeight;

    private readonly MainViewModel _main;
    private readonly UIViewController _owner;
    private UIViewController _controller = null!;
    private UIView _content = null!;
    private UIStackView _stack = null!;
    private readonly PresentationDelegate _presentationDelegate;
    private readonly List<EntryView> _entries = [];
    private readonly List<UIView> _dividers = [];
    private Task _presentationTask = Task.CompletedTask;
    private Task _dismissalTask = Task.CompletedTask;
    private bool _presentationPending;
    private CGRect? _sourceRect;
    private bool _disposed;

    internal iOSRatingSourcesPopover(MainViewModel main, UIViewController owner)
    {
        _main = main;
        _owner = owner;
        _controller = CreateController();
        _presentationDelegate = new PresentationDelegate(this);
        _main.RatingSources.PropertyChanged += OnSourcesChanged;
    }

    internal bool IsOpen => _controller.PresentingViewController != null;
    internal bool IsOpening => _presentationPending;
    internal Task PresentationTask => _presentationTask;
    internal Task DismissalTask => _dismissalTask;
    internal bool IsAnchoredToControlBar { get; private set; }
    internal CGRect? SourceRect => IsOpen || _presentationPending ? _sourceRect : null;
    internal IReadOnlyList<EntryView> EntryViews => _entries;
    internal IReadOnlyList<UIView> DividerViews => _dividers;
    internal UIPopoverPresentationController? Presentation => IsOpen ? _controller.PopoverPresentationController : null;
    internal CGSize ContentSize => _controller.PreferredContentSize;
    internal CGSize PresentedContentSize => Presentation?.FrameOfPresentedViewInContainerView.Size ?? CGSize.Empty;
    internal static double EntryContentWidth => EntryWidth - DesignTokens.PopupRatingSourcesEntryPadding * 2;

    private UIViewController CreateController()
    {
        _content = new UIView { BackgroundColor = UIColor.Clear };
        _stack = new UIStackView
        {
            Axis = UILayoutConstraintAxis.Vertical,
            Distribution = UIStackViewDistribution.Fill,
            Alignment = UIStackViewAlignment.Fill,
            Spacing = (nfloat)DesignTokens.PopupRatingSourcesEntrySpacing,
            TranslatesAutoresizingMaskIntoConstraints = false,
        };
        var guide = new UILayoutGuide();
        _content.AddLayoutGuide(guide);
        _content.AddSubview(_stack);
        NSLayoutConstraint.ActivateConstraints([
            guide.LeadingAnchor.ConstraintEqualTo(_content.LeadingAnchor, (nfloat)DesignTokens.PopupPadding),
            guide.TrailingAnchor.ConstraintEqualTo(_content.TrailingAnchor, (nfloat)(-DesignTokens.PopupPadding)),
            guide.TopAnchor.ConstraintEqualTo(_content.SafeAreaLayoutGuide.TopAnchor, (nfloat)DesignTokens.PopupPadding),
            guide.BottomAnchor.ConstraintEqualTo(_content.SafeAreaLayoutGuide.BottomAnchor, (nfloat)(-DesignTokens.PopupPadding)),
            _stack.LeadingAnchor.ConstraintEqualTo(guide.LeadingAnchor),
            _stack.TrailingAnchor.ConstraintEqualTo(guide.TrailingAnchor),
            _stack.TopAnchor.ConstraintEqualTo(guide.TopAnchor),
            _stack.BottomAnchor.ConstraintEqualTo(guide.BottomAnchor),
        ]);
        return new UIViewController
        {
            View = _content,
            ModalPresentationStyle = UIModalPresentationStyle.Popover,
            OverrideUserInterfaceStyle = UIUserInterfaceStyle.Dark,
        };
    }

    internal void Show(UIButton anchor, bool controlBarAnchor, bool rowLayout)
    {
        if (_disposed || _main.RatingSources.Items.Count == 0)
        {
            Close();
            return;
        }
        bool wasOpen = IsOpen;
        var previous = _controller;
        if (!wasOpen) _controller = CreateController();
        IsAnchoredToControlBar = controlBarAnchor;
        Refresh();
        var popover = _controller.PopoverPresentationController;
        if (popover == null) return;
        var ownerView = _owner.View!;
        ownerView.LayoutIfNeeded();
        var directionAnchor = anchor.ConvertRectToView(anchor.Bounds, ownerView);
        popover.SourceView = ownerView;
        popover.SourceRect = directionAnchor;
        _sourceRect = directionAnchor;
        popover.PermittedArrowDirections = controlBarAnchor
            ? rowLayout ? UIPopoverArrowDirection.Down : UIPopoverArrowDirection.Right
            : ResolveCardArrow(directionAnchor, ownerView.Bounds, _controller.PreferredContentSize);
        popover.Delegate = _presentationDelegate;
        var safe = ownerView.SafeAreaInsets;
        var edge = (nfloat)DesignTokens.PopupRatingSourcesEdgeMargin;
        popover.PopoverLayoutMargins = new UIEdgeInsets(safe.Top + edge, safe.Left + edge, safe.Bottom + edge, safe.Right + edge);
        if (!wasOpen)
        {
            _presentationPending = true;
            _presentationTask = PresentWhenClearAsync(_controller, previous);
        }
    }

    internal static UIPopoverArrowDirection ResolveCardArrow(CGRect anchor, CGRect viewport, CGSize content)
    {
        double below = (double)viewport.Height - (double)(anchor.Y + anchor.Height);
        double above = (double)anchor.Y;
        double required = (double)content.Height + DesignTokens.PopupRatingSourcesArrowMargin;
        return below < required && above >= required ? UIPopoverArrowDirection.Down : UIPopoverArrowDirection.Up;
    }

    private async Task PresentWhenClearAsync(UIViewController controller, UIViewController previous)
    {
        var dismissal = _dismissalTask;
        try
        {
            await dismissal;
            for (int attempt = 0; attempt < 60; attempt++)
            {
                await Task.Delay(16);
                if (_disposed || !_main.RatingSources.IsOpen || !ReferenceEquals(controller, _controller)) return;
                if (ReferenceEquals(_owner.PresentedViewController, previous)) continue;
                var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                _owner.PresentViewController(controller, OperatingSystem.IsIOSVersionAtLeast(26), () => completion.TrySetResult(true));
                await completion.Task;
                return;
            }
            if (ReferenceEquals(controller, _controller) && _main.RatingSources.IsOpen) _main.RatingSources.Close();
        }
        finally
        {
            if (ReferenceEquals(controller, _controller)) _presentationPending = false;
        }
    }

    private void OnSourcesChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (_disposed) return;
        if (!UiDispatcher.CheckAccess())
        {
            UiDispatcher.Post(() => OnSourcesChanged(sender, args));
            return;
        }
        if (args.PropertyName == nameof(RatingSourcesViewModel.Items))
        {
            if (IsOpen) Refresh();
            return;
        }
        if (args.PropertyName == nameof(RatingSourcesViewModel.IsOpen) && !_main.RatingSources.IsOpen) ClosePresentation();
    }

    internal void Refresh()
    {
        ClearEntries();
        var items = _main.RatingSources.Items;
        double y = DesignTokens.PopupPadding;
        for (int index = 0; index < items.Count; index++)
        {
            var entry = CreateEntry(items[index]);
            entry.Container.TranslatesAutoresizingMaskIntoConstraints = false;
            entry.Container.HeightAnchor.ConstraintEqualTo(entry.Container.Frame.Height).Active = true;
            _stack.AddArrangedSubview(entry.Container);
            _entries.Add(entry);
            y += entry.Container.Frame.Height;
            if (index < items.Count - 1)
            {
                var divider = new UIView
                {
                    BackgroundColor = UIColor.Separator,
                    UserInteractionEnabled = false,
                    TranslatesAutoresizingMaskIntoConstraints = false,
                };
                entry.Container.AddSubview(divider);
                NSLayoutConstraint.ActivateConstraints([
                    divider.LeadingAnchor.ConstraintEqualTo(entry.Container.LeadingAnchor),
                    divider.TrailingAnchor.ConstraintEqualTo(entry.Container.TrailingAnchor),
                    divider.TopAnchor.ConstraintEqualTo(entry.Container.BottomAnchor,
                        (nfloat)((DesignTokens.PopupRatingSourcesEntrySpacing - DesignTokens.PopupRatingSourcesDividerHeight) / 2)),
                    divider.HeightAnchor.ConstraintEqualTo((nfloat)DesignTokens.PopupRatingSourcesDividerHeight),
                ]);
                _dividers.Add(divider);
            }
            y += DesignTokens.PopupRatingSourcesEntrySpacing;
        }
        double height = _entries.Count == 0
            ? DesignTokens.PopupPadding * 2
            : y - DesignTokens.PopupRatingSourcesEntrySpacing + DesignTokens.PopupPadding;
        _controller.PreferredContentSize = new CGSize(ContentWidth, height);
        _content.SetNeedsLayout();
    }

    internal static EntryLayout MeasureEntryLayout(string name, string kind, string status)
    {
        double padding = DesignTokens.PopupRatingSourcesEntryPadding;
        double labelGap = DesignTokens.PopupRatingSourcesLabelGap;
        double available = EntryContentWidth;
        double nameWidth = Measure(name, NameFont);
        double badgeWidth = Math.Ceiling(Measure(kind, MetaFont)) + DesignTokens.PopupRatingSourcesBadgePaddingX * 2 + 4;
        double statusWidth = Math.Ceiling(Measure(status, MetaFont)) + 4;
        double metaWidth = badgeWidth + labelGap + statusWidth;
        if (metaWidth > available)
        {
            statusWidth = Math.Max(0, available - badgeWidth - labelGap);
            metaWidth = badgeWidth + labelGap + statusWidth;
        }
        bool twoLines = nameWidth + labelGap + metaWidth > available + .5;
        double nameFit = twoLines ? available : Math.Max(0, available - labelGap - metaWidth);
        double metaBandY = twoLines ? padding + LabelHeight + DesignTokens.PopupRatingSourcesMetaLineGap : padding;
        double nameY = padding + (LabelHeight - NameFieldHeight) / 2;
        double metaY = metaBandY + (LabelHeight - MetaFieldHeight) / 2;
        double statusX = EntryWidth - padding - statusWidth;
        double badgeX = statusX - labelGap - badgeWidth;
        double starsY = metaBandY + LabelHeight + labelGap;
        double height = starsY + StarSize + padding;
        return new EntryLayout(twoLines, height,
            new CGRect(padding, nameY, nameFit, NameFieldHeight),
            new CGRect(badgeX, metaBandY, badgeWidth, LabelHeight),
            new CGRect(statusX, metaY, statusWidth, MetaFieldHeight), starsY);
    }

    private EntryView CreateEntry(RatingSourceItem item)
    {
        var layout = MeasureEntryLayout(item.Name, item.Kind, item.Status);
        double padding = DesignTokens.PopupRatingSourcesEntryPadding;
        double badgePadding = DesignTokens.PopupRatingSourcesBadgePaddingX;
        var name = Label(item.Name, layout.NameFrame, NameFont, UIColor.Label, UILineBreakMode.MiddleTruncation, UITextAlignment.Left);
        var kind = Label(item.Kind, new CGRect(badgePadding, (LabelHeight - MetaFieldHeight) / 2,
            Math.Max(0, layout.BadgeFrame.Width - badgePadding * 2), MetaFieldHeight), MetaFont,
            UIColor.SecondaryLabel, UILineBreakMode.TailTruncation, UITextAlignment.Left);
        var badge = new UIView(layout.BadgeFrame)
        {
            BackgroundColor = UIColor.QuaternarySystemFill,
            UserInteractionEnabled = false,
        };
        badge.Layer.CornerRadius = (nfloat)DesignTokens.PopupRatingSourcesBadgeCornerRadius;
        badge.AddSubview(kind);
        var status = Label(item.Status, layout.StatusFrame, MetaFont, UIColor.SecondaryLabel,
            UILineBreakMode.TailTruncation, UITextAlignment.Right);
        int? rated = item.State.State == XmpRatingState.Rated ? item.State.Value : null;
        var stars = new List<UIButton>(StarCount);
        for (int rating = 0; rating < StarCount; rating++)
        {
            int target = rating;
            var button = new UIButton(new CGRect(padding + rating * (StarSize + StarSpacing), layout.StarsY, StarSize, StarSize));
            button.SetImage(iOSSymbols.Symbol(rating == 0 ? "xmark" : rating <= (rated ?? 0) ? "star.fill" : "star",
                rating == 0 ? DesignTokens.ToolbarRatingClearGlyphFontSize : DesignTokens.ToolbarRatingGlyphFontSize), UIControlState.Normal);
            button.TintColor = rating == 0 ? iOSColors.FromToken(DesignTokens.ColorStarClear)
                : rating <= (rated ?? 0) ? iOSColors.FromToken(DesignTokens.ColorStarOn) : UIColor.SecondaryLabel;
            button.AccessibilityLabel = rating == 0 ? "无星级" : $"{rating} 星";
            button.TouchUpInside += async (_, _) => await _main.RatingSources.SetRatingAsync(target);
            stars.Add(button);
        }
        var container = new UIView(new CGRect(0, 0, EntryWidth, layout.Height)) { BackgroundColor = UIColor.Clear };
        container.AddSubview(name);
        container.AddSubview(badge);
        container.AddSubview(status);
        foreach (var star in stars) container.AddSubview(star);
        return new EntryView(container, name, badge, kind, status, stars, layout.TwoLines);
    }

    private static UILabel Label(string text, CGRect frame, UIFont font, UIColor color,
        UILineBreakMode breakMode, UITextAlignment alignment) => new(frame)
    {
        Text = text,
        Font = font,
        TextColor = color,
        LineBreakMode = breakMode,
        TextAlignment = alignment,
        BackgroundColor = UIColor.Clear,
        UserInteractionEnabled = false,
    };

    private static double Measure(string text, UIFont font)
        => string.IsNullOrEmpty(text) ? 0 : new NSString(text).GetSizeUsingAttributes(new UIStringAttributes { Font = font }).Width;

    private void ClearEntries()
    {
        foreach (var entry in _entries)
        {
            _stack.RemoveArrangedSubview(entry.Container);
            entry.Container.RemoveFromSuperview();
        }
        _entries.Clear();
        foreach (var divider in _dividers) divider.RemoveFromSuperview();
        _dividers.Clear();
    }

    private void ClosePresentation()
    {
        _presentationPending = false;
        IsAnchoredToControlBar = false;
        if (!_dismissalTask.IsCompleted) return;
        if (_owner.PresentedViewController != _controller && !IsOpen) return;
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _dismissalTask = completion.Task;
        _controller.DismissViewController(OperatingSystem.IsIOSVersionAtLeast(26), () => completion.TrySetResult(true));
    }

    internal void Close()
    {
        ClosePresentation();
        if (_main.RatingSources.IsOpen) _main.RatingSources.Close();
    }

    private void OnSystemDismissed()
    {
        IsAnchoredToControlBar = false;
        if (_main.RatingSources.IsOpen) _main.RatingSources.Close();
    }

    public void Dispose()
    {
        if (_disposed) return;
        Close();
        _main.RatingSources.PropertyChanged -= OnSourcesChanged;
        ClearEntries();
        _disposed = true;
    }

    internal readonly record struct EntryLayout(
        bool TwoLines,
        double Height,
        CGRect NameFrame,
        CGRect BadgeFrame,
        CGRect StatusFrame,
        double StarsY);

    internal sealed record EntryView(
        UIView Container,
        UILabel Name,
        UIView Badge,
        UILabel Kind,
        UILabel Status,
        IReadOnlyList<UIButton> Stars,
        bool TwoLines)
    {
        internal CGRect Frame => Container.Frame;
    }

    private sealed class PresentationDelegate(iOSRatingSourcesPopover owner) : UIPopoverPresentationControllerDelegate
    {
        public override UIModalPresentationStyle GetAdaptivePresentationStyle(UIPresentationController controller)
            => UIModalPresentationStyle.None;

        public override UIModalPresentationStyle GetAdaptivePresentationStyle(UIPresentationController controller, UITraitCollection traitCollection)
            => UIModalPresentationStyle.None;

        public override void DidDismiss(UIPresentationController presentationController)
        {
            if (ReferenceEquals(presentationController, owner._controller.PopoverPresentationController)) owner.OnSystemDismissed();
        }
    }
}
