using AppKit;
using CoreGraphics;
using Foundation;
using PhotoViewer.Core.Design;
using PhotoViewer.ViewModels.Main;
using System.ComponentModel;

namespace PhotoViewer.Mac.Native;

/// <summary>
/// RATING-04：纵向列出各来源真值，任一条目的评分都统一写入全部来源。
/// 浮窗宽度由星级控件（0 号清零叉 + 5 星）决定；三信息默认同行，文件名放不下时改为双行。
/// 浮窗自身不绘制背景或描边，毛玻璃材质由 <see cref="NSPopover"/> 提供。
/// </summary>
internal sealed class MacRatingSourcesPopover : IDisposable
{
    /// <summary>条目左右内边距；星级控件宽度加上两侧内边距即条目宽度。</summary>
    private const double EntryPadding = 8;
    private const double EntrySpacing = 8;
    private const double LabelHeight = 16;
    private const double LabelGap = 6;
    private const double MetaLineGap = 4;
    private const double DividerHeight = 1;
    private const double BadgeCornerRadius = 4;
    private const double BadgePaddingX = 6;
    private const int StarCount = 6;

    /// <summary>控制栏横排评分组的槽位、间距与字形口径，浮窗星排直接复用。</summary>
    private static readonly ControlBarLayout.Metrics RatingMetrics =
        ControlBarLayout.GetMetrics(ControlBarGroupKind.Rating);
    private static readonly double StarSize = RatingMetrics.Width;
    private static readonly double StarSpacing = RatingMetrics.Spacing;
    private static readonly double StarsWidth = StarCount * StarSize + (StarCount - 1) * StarSpacing;
    private static readonly double EntryWidth = StarsWidth + EntryPadding * 2;
    private static readonly double ContentWidth = EntryWidth + DesignTokens.PopupPadding * 2;
    private static readonly NSFont NameFont = NSFont.SystemFontOfSize(11)
        ?? throw new InvalidOperationException("系统字体不可用");
    private static readonly NSFont MetaFont = NSFont.SystemFontOfSize(10)
        ?? throw new InvalidOperationException("系统字体不可用");
    private static readonly double NameFieldHeight = CellHeight(NameFont);
    private static readonly double MetaFieldHeight = CellHeight(MetaFont);

    private readonly MainViewModel _main;
    private readonly List<Presentation> _retired = [];
    private Presentation? _presentation;
    private NSView? _positioningView;
    private NSObject? _escapeMonitor;
    private bool _disposed;

    internal bool IsOpen => _presentation?.Popover.Shown == true;
    internal bool IsAnchoredToControlBar { get; private set; }
    internal NSView Content => _presentation?.Content ?? throw new InvalidOperationException("来源浮窗未展开");
    internal IReadOnlyList<EntryView> EntryViews => _presentation?.Entries ?? [];
    internal IReadOnlyList<NSBox> DividerViews => _presentation?.Dividers ?? [];
    internal NSRectEdge? LastPreferredEdge { get; private set; }
    /// <summary>条目内可用的信息宽度，等于星级控件整体宽度。</summary>
    internal static double EntryContentWidth => EntryWidth - EntryPadding * 2;

    internal bool OwnsWindow(NSWindow? window)
        => window != null && (ReferenceEquals(window, _presentation?.Content.Window)
            || ReferenceEquals(window, _positioningView?.Window));

    internal MacRatingSourcesPopover(MainViewModel main)
    {
        _main = main;
        _main.RatingSources.PropertyChanged += OnSourcesChanged;
    }

    private void OnSourcesChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (_disposed) return;
        if (args.PropertyName == nameof(RatingSourcesViewModel.Items))
        {
            if (IsOpen) Refresh();
            return;
        }
        if (args.PropertyName == nameof(RatingSourcesViewModel.IsOpen) && !_main.RatingSources.IsOpen) Close();
    }

    internal void Show(NSView positioningView, CGRect positioningRect, NSRectEdge preferredEdge, bool controlBarAnchor)
    {
        if (_disposed) return;
        if (_main.RatingSources.Items.Count == 0)
        {
            _main.RatingSources.Close();
            return;
        }
        ClosePresentation();
        _positioningView = positioningView;
        IsAnchoredToControlBar = controlBarAnchor;
        LastPreferredEdge = preferredEdge;
        var presentation = new Presentation();
        _presentation = presentation;
        presentation.CloseObserver = NSPopover.Notifications.ObserveDidClose(presentation.Popover, (_, _) =>
        {
            if (presentation.Popover.Shown) return;
            if (!ReferenceEquals(_presentation, presentation)) return;
            _presentation = null;
            if (_main.RatingSources.IsOpen) _main.RatingSources.Close();
        });
        Refresh();
        presentation.Popover.Show(positioningRect, positioningView, preferredEdge);
        InstallEscapeMonitor();
    }

    /// <summary>内容视图保持透明，让 NSPopover 自己的毛玻璃与圆角成为唯一一层外观。</summary>
    private static PopupContentView CreateContent() => new();

    internal void Refresh()
    {
        if (_presentation is not { } presentation) return;
        ClearEntries(presentation);
        var items = _main.RatingSources.Items;
        double y = DesignTokens.PopupPadding;
        for (int index = 0; index < items.Count; index++)
        {
            var entry = CreateEntry(items[index]);
            entry.Container.Frame = new CGRect(DesignTokens.PopupPadding, y, EntryWidth, entry.Container.Frame.Height);
            presentation.Content.AddSubview(entry.Container);
            presentation.Entries.Add(entry);
            y += entry.Container.Frame.Height;
            if (index < items.Count - 1)
            {
                var divider = new NSBox
                {
                    Frame = new CGRect(DesignTokens.PopupPadding, y + (EntrySpacing - DividerHeight) / 2,
                        EntryWidth, DividerHeight),
                    BoxType = NSBoxType.NSBoxSeparator,
                    Title = "",
                    TitlePosition = NSTitlePosition.NoTitle,
                };
                presentation.Content.AddSubview(divider);
                presentation.Dividers.Add(divider);
            }
            y += EntrySpacing;
        }
        double height = presentation.Entries.Count == 0
            ? DesignTokens.PopupPadding * 2
            : y - EntrySpacing + DesignTokens.PopupPadding;
        var size = new CGSize(ContentWidth, height);
        presentation.Popover.ContentSize = size;
        presentation.Content.Frame = new CGRect(CGPoint.Empty, size);
        if (IsOpen) presentation.Content.LayoutSubtreeIfNeeded();
    }

    /// <summary>条目布局：默认三信息同行；文件名放不下时文件名独占首行，来源与状态仍居右。</summary>
    /// <param name="name">来源文件名。</param>
    /// <param name="kind">来源类型标签（内嵌格式或 XMP）。</param>
    /// <param name="status">该来源的当前值/状态。</param>
    internal static EntryLayout MeasureEntryLayout(string name, string kind, string status)
    {
        double available = EntryWidth - EntryPadding * 2;
        double nameWidth = Measure(name, NameFont);
        double badgeWidth = Math.Ceiling(Measure(kind, MetaFont)) + BadgePaddingX * 2 + 4;
        double statusWidth = Math.Ceiling(Measure(status, MetaFont)) + 4;
        double metaWidth = badgeWidth + LabelGap + statusWidth;
        if (metaWidth > available)
        {
            statusWidth = Math.Max(0, available - badgeWidth - LabelGap);
            metaWidth = badgeWidth + LabelGap + statusWidth;
        }
        bool twoLines = nameWidth + LabelGap + metaWidth > available + .5;
        double nameFit = twoLines ? available : Math.Max(0, available - LabelGap - metaWidth);
        double metaBandY = twoLines ? EntryPadding + LabelHeight + MetaLineGap : EntryPadding;
        double nameY = EntryPadding + (LabelHeight - NameFieldHeight) / 2;
        double metaY = metaBandY + (LabelHeight - MetaFieldHeight) / 2;
        double statusX = EntryWidth - EntryPadding - statusWidth;
        double badgeX = statusX - LabelGap - badgeWidth;
        double starsY = metaBandY + LabelHeight + LabelGap;
        double height = starsY + StarSize + EntryPadding;
        return new EntryLayout(
            twoLines,
            height,
            new CGRect(EntryPadding, nameY, nameFit, NameFieldHeight),
            new CGRect(badgeX, metaBandY, badgeWidth, LabelHeight),
            new CGRect(statusX, metaY, statusWidth, MetaFieldHeight),
            starsY);
    }

    private EntryView CreateEntry(RatingSourceItem item)
    {
        var layout = MeasureEntryLayout(item.Name, item.Kind, item.Status);
        var name = Label(item.Name, layout.NameFrame, NameFont, DesignTokens.ColorTextPrimary,
            NSLineBreakMode.TruncatingMiddle, NSTextAlignment.Left);
        var kind = Label(item.Kind, new CGRect(BadgePaddingX, (LabelHeight - MetaFieldHeight) / 2,
            Math.Max(0, layout.BadgeFrame.Width - BadgePaddingX * 2), MetaFieldHeight), MetaFont,
            DesignTokens.ColorTextSecondary, NSLineBreakMode.TruncatingTail, NSTextAlignment.Left);
        var badge = new NSBox
        {
            Frame = layout.BadgeFrame,
            BoxType = NSBoxType.NSBoxCustom,
            Transparent = false,
            FillColor = NSColor.QuaternaryLabel,
            BorderWidth = 0,
            BorderColor = NSColor.Clear,
            CornerRadius = (nfloat)BadgeCornerRadius,
            Title = "",
            TitlePosition = NSTitlePosition.NoTitle,
            ContentViewMargins = new CGSize(BadgePaddingX, (LabelHeight - MetaFieldHeight) / 2),
            ContentView = kind,
        };
        var status = Label(item.Status, layout.StatusFrame, MetaFont, DesignTokens.ColorTextSecondary,
            NSLineBreakMode.TruncatingTail, NSTextAlignment.Right);
        var stars = new List<NSButton>(StarCount);
        for (int rating = 0; rating < StarCount; rating++)
        {
            int target = rating;
            var button = MacControlBar.ToolbarButton.Create(
                rating == 0 ? "xmark" : rating <= (item.State.Value ?? 0) ? "star.fill" : "star",
                rating == 0 ? "无星级" : rating + " 星", () => _ = _main.RatingSources.SetRatingAsync(target), StarSize, false);
            button.Frame = new CGRect(EntryPadding + rating * (StarSize + StarSpacing), layout.StarsY, StarSize, StarSize);
            button.RefusesFirstResponder = true;
            button.ContentTintColor = MacDesignColors.Argb(rating == 0 ? DesignTokens.ColorStarClear
                : rating <= (item.State.Value ?? 0) ? DesignTokens.ColorStarOn : DesignTokens.ColorStarOff);
            stars.Add(button);
        }

        var container = new PopupContentView { Frame = new CGRect(0, 0, EntryWidth, layout.Height) };
        container.AddSubview(name);
        container.AddSubview(badge);
        container.AddSubview(status);
        foreach (var star in stars) container.AddSubview(star);
        return new EntryView(container, name, badge, kind, status, stars, layout.TwoLines);
    }

    private static NSTextField Label(string text, CGRect frame, NSFont font, uint color,
        NSLineBreakMode breakMode, NSTextAlignment alignment)
        => new(frame)
        {
            StringValue = text,
            Editable = false,
            Selectable = false,
            Bordered = false,
            DrawsBackground = false,
            Alignment = alignment,
            LineBreakMode = breakMode,
            MaximumNumberOfLines = 1,
            Font = font,
            TextColor = MacDesignColors.Argb(color),
        };

    private static double Measure(string text, NSFont font)
    {
        if (string.IsNullOrEmpty(text)) return 0;
        using var attributes = new NSDictionary(NSStringAttributeKey.Font, font);
        using var attributed = new NSAttributedString(text, attributes);
        return (double)attributed.Size.Width;
    }

    /// <summary>标准文本控件的实际字高，用于在同一信息行内垂直居中。</summary>
    private static double CellHeight(NSFont font)
    {
        using var field = new NSTextField
        {
            StringValue = "Ag",
            Font = font,
            Editable = false,
            Selectable = false,
            Bordered = false,
            DrawsBackground = false,
            MaximumNumberOfLines = 1,
        };
        return (double)field.Cell!.CellSize.Height;
    }

    private void InstallEscapeMonitor()
    {
        _escapeMonitor ??= NSEvent.AddLocalMonitorForEventsMatchingMask(NSEventMask.KeyDown, nativeEvent =>
        {
            if (!IsOpen || nativeEvent.KeyCode != 53) return nativeEvent;
            if (nativeEvent.Window != _presentation?.Content.Window && nativeEvent.Window != _positioningView?.Window) return nativeEvent;
            Close();
            return null!;
        });
    }

    private void RemoveEscapeMonitor()
    {
        if (_escapeMonitor == null) return;
        NSEvent.RemoveMonitor(_escapeMonitor);
        _escapeMonitor.Dispose();
        _escapeMonitor = null;
    }

    private static void ClearEntries(Presentation presentation)
    {
        foreach (var entry in presentation.Entries)
        {
            entry.Container.RemoveFromSuperview();
            entry.Container.Dispose();
        }
        presentation.Entries.Clear();
        foreach (var divider in presentation.Dividers)
        {
            divider.RemoveFromSuperview();
            divider.Dispose();
        }
        presentation.Dividers.Clear();
    }

    private void ClosePresentation()
    {
        RemoveEscapeMonitor();
        _positioningView = null;
        IsAnchoredToControlBar = false;
        var presentation = _presentation;
        _presentation = null;
        if (presentation == null) return;
        _retired.Add(presentation);
        if (presentation.Popover.Shown) presentation.Popover.Close();
    }

    internal void Close()
    {
        ClosePresentation();
        if (_main.RatingSources.IsOpen) _main.RatingSources.Close();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _main.RatingSources.PropertyChanged -= OnSourcesChanged;
        ClosePresentation();
        foreach (var presentation in _retired) presentation.CloseObserver?.Dispose();
        _retired.Clear();
        _disposed = true;
    }

    /// <summary>条目布局结果：是否双行、条目高、三块文本与星排的 frame。</summary>
    internal readonly record struct EntryLayout(
        bool TwoLines,
        double Height,
        CGRect NameFrame,
        CGRect BadgeFrame,
        CGRect StatusFrame,
        double StarsY);

    /// <summary>单个来源条目：普通容器 + 标准文本/标签控件与 0–5 按钮。</summary>
    internal sealed record EntryView(
        NSView Container,
        NSTextField Name,
        NSBox Badge,
        NSTextField Kind,
        NSTextField Status,
        IReadOnlyList<NSButton> Stars,
        bool TwoLines)
    {
        internal CGRect Frame => Container.Frame;
    }

    private sealed class Presentation
    {
        internal PopupContentView Content { get; } = CreateContent();
        internal NSViewController Controller { get; }
        internal NSPopover Popover { get; }
        internal List<EntryView> Entries { get; } = [];
        internal List<NSBox> Dividers { get; } = [];
        internal NSObject? CloseObserver { get; set; }

        internal Presentation()
        {
            Controller = new NSViewController { View = Content };
            Popover = new NSPopover
            {
                Behavior = NSPopoverBehavior.Transient,
                Animates = false,
                Appearance = NSAppearance.GetAppearance(NSAppearance.NameDarkAqua),
                ContentViewController = Controller,
            };
        }
    }

    private sealed class PopupContentView : NSView
    {
        public override bool IsFlipped => true;
        public override bool AcceptsFirstResponder() => false;
    }
}
