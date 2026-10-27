using System;
using System.Collections.Generic;
using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using PhotoViewer.Core;
using PhotoViewer.Core.Design;
using PhotoViewer.Core.Image;
using PhotoViewer.ViewModels.Main;
using Windows.Foundation;

namespace PhotoViewer.Windows.Native;

/// <summary>RATING-04 来源浮窗：WinUI 原生轻量弹层，条目与整组写入状态来自共享 VM。</summary>
internal sealed class WindowsRatingSourcesFlyout : IDisposable
{
    private const int StarCount = 6;
    private const double InfoHeight = 16;
    private static readonly double StarSize = DesignTokens.ToolbarRatingButtonSize;
    private static readonly double EntryWidth = StarSize * StarCount + DesignTokens.PopupRatingSourcesEntryPadding * 2;
    internal static double ContentWidth => EntryWidth + DesignTokens.PopupPadding * 2;

    private readonly MainViewModel _main;
    private readonly FrameworkElement _root;
    private readonly Flyout _flyout;
    private readonly StackPanel _body;
    private readonly List<EntryView> _entries = [];
    private FrameworkElement? _anchor;
    private ImageFile? _target;
    private ImageFile? _displayedWhenOpened;
    private Rect _anchorRect;
    private bool _presenting;
    private bool _disposed;

    internal WindowsRatingSourcesFlyout(MainViewModel main, FrameworkElement root)
    {
        _main = main;
        _root = root;
        _body = new StackPanel { Width = EntryWidth };
        var presenterStyle = new Style(typeof(FlyoutPresenter))
        {
            BasedOn = (Style)Application.Current.Resources["DefaultFlyoutPresenterStyle"],
        };
        presenterStyle.Setters.Add(new Setter(FlyoutPresenter.BackgroundProperty,
            Application.Current.Resources["DesktopAcrylicTransparentBrush"]));
        presenterStyle.Setters.Add(new Setter(FlyoutPresenter.PaddingProperty, new Thickness(DesignTokens.PopupPadding)));
        presenterStyle.Setters.Add(new Setter(FlyoutPresenter.CornerRadiusProperty,
            new CornerRadius(DesignTokens.PopupRatingSourcesCornerRadius)));
        _flyout = new Flyout
        {
            Content = _body,
            FlyoutPresenterStyle = presenterStyle,
            SystemBackdrop = (SystemBackdrop)Application.Current.Resources["AcrylicBackgroundFillColorDefaultBackdrop"],
            ShouldConstrainToRootBounds = false,
        };
        _flyout.Closed += OnClosed;
        _main.RatingSources.PropertyChanged += OnSourcesChanged;
    }

    internal bool IsOpen => _anchor != null && _flyout.IsOpen;
    internal bool UsesDesktopAcrylic => !_flyout.IsConstrainedToRootBounds && _flyout.SystemBackdrop is DesktopAcrylicBackdrop;
    internal FlyoutPlacementMode? LastPlacement { get; private set; }
    internal IReadOnlyList<EntryView> EntryViews => _entries;
    internal void DismissNativeForAcceptance() => HideNative();
    internal Rect AnchorRectForAcceptance => _anchorRect;

    internal bool TryGetPresenterBounds(out Rect bounds)
    {
        bounds = default;
        DependencyObject? current = _body;
        while (current != null && current is not FlyoutPresenter) current = VisualTreeHelper.GetParent(current);
        if (current is not FlyoutPresenter presenter || presenter.ActualWidth <= 0) return false;
        bounds = presenter.TransformToVisual(_root).TransformBounds(
            new Rect(0, 0, presenter.ActualWidth, presenter.ActualHeight));
        return true;
    }

    internal void Show(ImageFile target, Button anchor, bool controlBarAnchor, bool controlHorizontal)
    {
        if (_disposed || target.HasRatingConflict != true) return;
        Close();
        if (!TryGetAnchorRect(anchor, out var anchorRect)) return;

        _main.ShowRatingSources(target);
        if (_main.RatingSources.Items.Count == 0)
        {
            _main.RatingSources.Close();
            return;
        }

        _anchor = anchor;
        _target = target;
        _displayedWhenOpened = _main.CurrentFile;
        _anchorRect = anchorRect;
        RefreshEntries();
        _body.Measure(new Size(EntryWidth, double.PositiveInfinity));
        double border = ((Thickness)Application.Current.Resources["FlyoutBorderThemeThickness"]).Left;
        double width = ContentWidth + border * 2;
        double height = _body.DesiredSize.Height + DesignTokens.PopupPadding * 2 + border * 2;
        double gap = DesignTokens.PopupRatingSourcesArrowMargin;
        double required = height + gap;
        var placement = controlBarAnchor
            ? controlHorizontal ? FlyoutPlacementMode.Top : FlyoutPlacementMode.Left
            : _root.ActualHeight - anchorRect.Bottom < required && anchorRect.Top >= required
                ? FlyoutPlacementMode.Top
                : FlyoutPlacementMode.Bottom;

        // 窗口化弹层只按显示器夹取；RATING-04 要求主体留在窗口内，故按令牌自行定位后交给原生弹层。
        double centerX = (anchorRect.Left + anchorRect.Right) / 2;
        double centerY = (anchorRect.Top + anchorRect.Bottom) / 2;
        var (x, y) = placement switch
        {
            FlyoutPlacementMode.Top => (centerX - width / 2, anchorRect.Top - gap - height),
            FlyoutPlacementMode.Left => (anchorRect.Left - gap - width, centerY - height / 2),
            _ => (centerX - width / 2, anchorRect.Bottom + gap),
        };
        x = Clamp(x, width, _root.ActualWidth);
        y = Clamp(y, height, _root.ActualHeight);

        _presenting = true;
        try
        {
            _flyout.ShowAt(anchor, new FlyoutShowOptions
            {
                Position = new Point(x - anchorRect.X, y - anchorRect.Y),
                Placement = FlyoutPlacementMode.BottomEdgeAlignedLeft,
            });
            LastPlacement = placement;
            CompositionTarget.Rendering += OnRendering;
        }
        catch (Exception ex)
        {
            NativeStartupLog.Fail("显示评分来源浮窗", ex);
            ResetPresentation();
            _main.RatingSources.Close();
        }
        finally
        {
            _presenting = false;
        }
    }

    internal void Close()
    {
        HideNative();
        ResetPresentation();
        if (_main.RatingSources.IsOpen) _main.RatingSources.Close();
    }

    private void HideNative()
    {
        if (_flyout.IsOpen) _flyout.Hide();
    }

    private void ResetPresentation()
    {
        CompositionTarget.Rendering -= OnRendering;
        _anchor = null;
        _target = null;
        _displayedWhenOpened = null;
        LastPlacement = null;
    }

    private void OnClosed(object? sender, object e)
    {
        if (_disposed || _presenting || _flyout.IsOpen) return;
        ResetPresentation();
        if (_main.RatingSources.IsOpen) _main.RatingSources.Close();
    }

    private void OnSourcesChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_disposed) return;
        if (e.PropertyName == nameof(RatingSourcesViewModel.Items) && _anchor != null)
        {
            RefreshEntries();
        }
        else if (e.PropertyName == nameof(RatingSourcesViewModel.IsOpen) && !_main.RatingSources.IsOpen)
        {
            HideNative();
            ResetPresentation();
        }
    }

    private void OnRendering(object? sender, object e)
    {
        if (_anchor is not { } anchor || _target is not { } target)
        {
            CompositionTarget.Rendering -= OnRendering;
            return;
        }

        if (!ReferenceEquals(_main.CurrentFile, _displayedWhenOpened)
            || target.HasRatingConflict != true
            || !IsAnchorUsable(anchor, target)
            || !TryGetAnchorRect(anchor, out var current)
            || !NearlyEqual(current, _anchorRect))
        {
            Close();
        }
    }

    private static double Clamp(double start, double size, double extent)
    {
        double margin = DesignTokens.PopupRatingSourcesEdgeMargin;
        return Math.Max(margin, Math.Min(start, extent - margin - size));
    }

    private static bool NearlyEqual(Rect left, Rect right)
        => Math.Abs(left.X - right.X) <= .5 && Math.Abs(left.Y - right.Y) <= .5
            && Math.Abs(left.Width - right.Width) <= .5 && Math.Abs(left.Height - right.Height) <= .5;

    private bool IsAnchorUsable(FrameworkElement anchor, ImageFile target)
    {
        if (!ReferenceEquals(anchor.XamlRoot, _root.XamlRoot) || anchor.XamlRoot == null) return false;
        DependencyObject? current = anchor;
        while (current != null)
        {
            if (current is WindowsThumbnailCard card && !ReferenceEquals(card.BoundFile, target)) return false;
            if (current is UIElement { Visibility: Visibility.Collapsed }) return false;
            if (current is ScrollViewer scroll)
            {
                var bounds = anchor.TransformToVisual(scroll).TransformBounds(
                    new Rect(0, 0, anchor.ActualWidth, anchor.ActualHeight));
                var viewport = new Rect(0, 0, scroll.ViewportWidth, scroll.ViewportHeight);
                viewport.Intersect(bounds);
                if (viewport.IsEmpty) return false;
            }
            if (ReferenceEquals(current, _root)) break;
            current = VisualTreeHelper.GetParent(current);
        }
        return ReferenceEquals(current, _root);
    }

    private bool TryGetAnchorRect(FrameworkElement anchor, out Rect rect)
    {
        rect = default;
        if (anchor.ActualWidth <= 0 || anchor.ActualHeight <= 0
            || _root.ActualWidth <= 0 || _root.ActualHeight <= 0) return false;
        try
        {
            rect = anchor.TransformToVisual(_root).TransformBounds(
                new Rect(0, 0, anchor.ActualWidth, anchor.ActualHeight));
            var visible = new Rect(0, 0, _root.ActualWidth, _root.ActualHeight);
            visible.Intersect(rect);
            return !visible.IsEmpty;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private void RefreshEntries()
    {
        _body.Children.Clear();
        _entries.Clear();
        var items = _main.RatingSources.Items;
        for (int index = 0; index < items.Count; index++)
        {
            if (index > 0)
            {
                double margin = (DesignTokens.PopupRatingSourcesEntrySpacing
                    - DesignTokens.PopupRatingSourcesDividerHeight) / 2;
                _body.Children.Add(new Border
                {
                    Height = DesignTokens.PopupRatingSourcesDividerHeight,
                    Margin = new Thickness(0, margin, 0, margin),
                    Background = new SolidColorBrush(WindowsLayout.Token(DesignTokens.ColorMenuSeparator)),
                });
            }

            var entry = CreateEntry(items[index]);
            _entries.Add(entry);
            _body.Children.Add(entry.Container);
        }
    }

    private EntryView CreateEntry(RatingSourceItem item)
    {
        var layout = MeasureEntryLayout(item);
        var name = new TextBlock
        {
            Text = layout.TwoLines ? EllipsizeMiddle(item.Name, layout.AvailableNameWidth) : item.Name,
            Width = layout.AvailableNameWidth,
            Height = InfoHeight,
            Margin = new Thickness(DesignTokens.PopupRatingSourcesEntryPadding, layout.NameY, 0, 0),
            FontSize = DesignTokens.PopupRatingSourcesNameFontSize,
            Foreground = new SolidColorBrush(WindowsLayout.ExifValueForeground),
            VerticalAlignment = VerticalAlignment.Top,
            HorizontalAlignment = HorizontalAlignment.Left,
            IsHitTestVisible = false,
        };
        ToolTipService.SetToolTip(name, item.Name);
        AutomationProperties.SetName(name, item.Name);

        var kind = new TextBlock
        {
            Text = item.Kind,
            FontSize = DesignTokens.PopupRatingSourcesMetaFontSize,
            Foreground = new SolidColorBrush(WindowsLayout.ExifLabelForeground),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            IsHitTestVisible = false,
        };
        var badge = new Border
        {
            Width = layout.BadgeWidth,
            Height = InfoHeight,
            Margin = new Thickness(layout.BadgeX, layout.MetaY, 0, 0),
            Padding = new Thickness(DesignTokens.PopupRatingSourcesBadgePaddingX, 0,
                DesignTokens.PopupRatingSourcesBadgePaddingX, 0),
            Background = new SolidColorBrush(WindowsLayout.Token(DesignTokens.ColorMenuItemPressed)),
            CornerRadius = new CornerRadius(DesignTokens.PopupRatingSourcesBadgeCornerRadius),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            Child = kind,
            IsHitTestVisible = false,
        };
        var status = new TextBlock
        {
            Text = item.Status,
            Width = layout.StatusWidth,
            Height = InfoHeight,
            Margin = new Thickness(layout.StatusX, layout.MetaY, 0, 0),
            FontSize = DesignTokens.PopupRatingSourcesMetaFontSize,
            Foreground = new SolidColorBrush(WindowsLayout.ExifLabelForeground),
            TextAlignment = TextAlignment.Right,
            TextTrimming = TextTrimming.CharacterEllipsis,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            IsHitTestVisible = false,
        };

        var stars = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Height = StarSize,
            Margin = new Thickness(DesignTokens.PopupRatingSourcesEntryPadding, layout.StarsY, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
        };
        var buttons = new List<Button>(StarCount);
        int? rated = item.State.State == XmpRatingState.Rated ? item.State.Value : null;
        for (int rating = 0; rating < StarCount; rating++)
        {
            int value = rating;
            bool on = rating > 0 && rating <= (rated ?? 0);
            string glyph = rating == 0 ? WindowsIcons.ClearGlyph : on ? WindowsIcons.StarGlyph : "\uE734";
            double fontSize = rating == 0
                ? DesignTokens.ToolbarRatingClearGlyphFontSize
                : DesignTokens.ToolbarRatingGlyphFontSize;
            var content = new TextBlock
            {
                Text = glyph,
                FontFamily = new FontFamily(WindowsIcons.FluentFontFamily),
                FontSize = fontSize,
                Foreground = new SolidColorBrush(WindowsLayout.Token(rating == 0
                    ? DesignTokens.ColorStarClear
                    : on ? DesignTokens.ColorStarOn : DesignTokens.ColorStarOff)),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
            var button = new Button
            {
                Width = StarSize,
                Height = StarSize,
                Padding = new Thickness(0),
                Background = new SolidColorBrush(global::Windows.UI.Color.FromArgb(0, 0, 0, 0)),
                BorderThickness = new Thickness(0),
                CornerRadius = new CornerRadius(DesignTokens.ToolbarCornerRadius),
                Content = content,
            };
            WindowsControlStyles.ApplyTransparentSquareButton(button);
            string label = rating == 0 ? "无星级" : $"{rating} 星";
            ToolTipService.SetToolTip(button, label);
            AutomationProperties.SetName(button, label);
            button.Click += (_, _) => SetRating(value);
            stars.Children.Add(button);
            buttons.Add(button);
        }

        var container = new Grid
        {
            Width = EntryWidth,
            Height = layout.Height,
            Children = { name, badge, status, stars },
        };
        AutomationProperties.SetName(container, item.Name);
        return new EntryView(container, name, badge, status, buttons, layout.TwoLines);
    }

    private async void SetRating(int rating)
    {
        try
        {
            await _main.RatingSources.SetRatingAsync(rating);
        }
        catch (Exception ex)
        {
            NativeStartupLog.Fail($"来源浮窗评分失败(rating={rating})", ex);
        }
    }

    internal static EntryLayout MeasureEntryLayout(RatingSourceItem item)
    {
        double available = EntryWidth - DesignTokens.PopupRatingSourcesEntryPadding * 2;
        double badgeWidth = Math.Ceiling(MeasureText(item.Kind, DesignTokens.PopupRatingSourcesMetaFontSize))
            + DesignTokens.PopupRatingSourcesBadgePaddingX * 2;
        double statusWidth = Math.Ceiling(MeasureText(item.Status, DesignTokens.PopupRatingSourcesMetaFontSize));
        double metaWidth = badgeWidth + DesignTokens.PopupRatingSourcesLabelGap + statusWidth;
        if (metaWidth > available)
        {
            statusWidth = Math.Max(0, available - badgeWidth - DesignTokens.PopupRatingSourcesLabelGap);
            metaWidth = badgeWidth + DesignTokens.PopupRatingSourcesLabelGap + statusWidth;
        }
        bool twoLines = MeasureText(item.Name, DesignTokens.PopupRatingSourcesNameFontSize)
            + DesignTokens.PopupRatingSourcesLabelGap + metaWidth > available + .5;
        double nameWidth = twoLines
            ? available
            : Math.Max(0, available - DesignTokens.PopupRatingSourcesLabelGap - metaWidth);
        double metaY = DesignTokens.PopupRatingSourcesEntryPadding
            + (twoLines ? InfoHeight + DesignTokens.PopupRatingSourcesMetaLineGap : 0);
        double starsY = metaY + InfoHeight + DesignTokens.PopupRatingSourcesLabelGap;
        double height = starsY + StarSize + DesignTokens.PopupRatingSourcesEntryPadding;
        return new EntryLayout(twoLines, height, nameWidth,
            DesignTokens.PopupRatingSourcesEntryPadding,
            metaY,
            EntryWidth - DesignTokens.PopupRatingSourcesEntryPadding - statusWidth,
            statusWidth,
            EntryWidth - DesignTokens.PopupRatingSourcesEntryPadding - statusWidth
                - DesignTokens.PopupRatingSourcesLabelGap - badgeWidth,
            badgeWidth,
            starsY);
    }

    private static string EllipsizeMiddle(string text, double maxWidth)
    {
        if (MeasureText(text, DesignTokens.PopupRatingSourcesNameFontSize) <= maxWidth) return text;
        string Candidate(int kept)
        {
            int head = (kept + 1) / 2;
            int tail = kept - head;
            return text[..head] + "…" + text[(text.Length - tail)..];
        }
        int low = 0;
        int high = Math.Max(0, text.Length - 1);
        while (low < high)
        {
            int middle = (low + high + 1) / 2;
            if (MeasureText(Candidate(middle), DesignTokens.PopupRatingSourcesNameFontSize) <= maxWidth) low = middle;
            else high = middle - 1;
        }
        return Candidate(low);
    }

    private static double MeasureText(string text, double fontSize)
    {
        if (string.IsNullOrEmpty(text)) return 0;
        var block = new TextBlock { Text = text, FontSize = fontSize };
        block.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        return block.DesiredSize.Width;
    }

    public void Dispose()
    {
        if (_disposed) return;
        Close();
        _main.RatingSources.PropertyChanged -= OnSourcesChanged;
        _flyout.Closed -= OnClosed;
        _disposed = true;
    }

    internal readonly record struct EntryLayout(
        bool TwoLines,
        double Height,
        double AvailableNameWidth,
        double NameY,
        double MetaY,
        double StatusX,
        double StatusWidth,
        double BadgeX,
        double BadgeWidth,
        double StarsY);

    internal sealed record EntryView(
        Grid Container,
        TextBlock Name,
        Border Badge,
        TextBlock Status,
        IReadOnlyList<Button> Stars,
        bool TwoLines);
}
