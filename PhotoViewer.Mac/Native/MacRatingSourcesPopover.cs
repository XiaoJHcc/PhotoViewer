using AppKit;
using CoreGraphics;
using Foundation;
using PhotoViewer.Core;
using PhotoViewer.Core.Design;
using PhotoViewer.ViewModels.Main;
using System.ComponentModel;

namespace PhotoViewer.Mac.Native;

/// <summary>
/// RATING-04：纵向列出各来源真值，任一条目的评分都统一写入全部来源。
/// 浮窗宽度由星级控件（0 号清零叉 + 5 星）决定；三信息默认同行，文件名放不下时改为双行。
/// 浮窗自身不绘制背景或描边，毛玻璃材质由 <see cref="NSPopover"/> 提供。
/// 条目随共享 <see cref="RatingSourcesViewModel.Items"/> 的实时变化重建；popover/controller/content
/// 在主窗口生命周期内复用同一套实例，关闭后 AppKit 的迟后访问仍落到存活对象上。
/// </summary>
internal sealed class MacRatingSourcesPopover : IDisposable
{
    /// <summary>0 号清零叉 + 1–5 星，共六个槽位。</summary>
    private const int StarCount = 6;
    /// <summary>三块信息共用的行带高，用于同一行内垂直居中；纯布局值，不对应设计令牌。</summary>
    private const double LabelHeight = 16;

    /// <summary>控制栏横排评分组的槽位、间距与字形口径，浮窗星排直接复用。</summary>
    private static readonly ControlBarLayout.Metrics RatingMetrics =
        ControlBarLayout.GetMetrics(ControlBarGroupKind.Rating);
    private static readonly double StarSize = RatingMetrics.Width;
    private static readonly double StarSpacing = RatingMetrics.Spacing;
    private static readonly double StarsWidth = StarCount * StarSize + (StarCount - 1) * StarSpacing;
    private static readonly double EntryWidth = StarsWidth + DesignTokens.PopupRatingSourcesEntryPadding * 2;
    private static readonly double ContentWidth = EntryWidth + DesignTokens.PopupPadding * 2;
    private static readonly NSFont NameFont =
        NSFont.SystemFontOfSize((nfloat)DesignTokens.PopupRatingSourcesNameFontSize)
        ?? throw new InvalidOperationException("系统字体不可用");
    private static readonly NSFont MetaFont =
        NSFont.SystemFontOfSize((nfloat)DesignTokens.PopupRatingSourcesMetaFontSize)
        ?? throw new InvalidOperationException("系统字体不可用");
    private static readonly double NameFieldHeight = CellHeight(NameFont);
    private static readonly double MetaFieldHeight = CellHeight(MetaFont);

    private readonly MainViewModel _main;
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
    /// <summary>本次锚点在主窗口根视图内的矩形；卡片入口只复制矩形，不持有虚拟化按钮。</summary>
    internal CGRect? AnchorRect { get; private set; }
    /// <summary>当前持有的原生浮窗数；创建后恒为 1，供反复开合验收。</summary>
    internal int PresentationCount => _presentation == null ? 0 : 1;
    internal IntPtr PopoverHandle => _presentation?.Popover.Handle ?? IntPtr.Zero;
    internal IntPtr ControllerHandle => _presentation?.Controller.Handle ?? IntPtr.Zero;
    internal CGSize ContentSize => _presentation?.Popover.ContentSize ?? CGSize.Empty;
    /// <summary>条目内可用的信息宽度，等于星级控件整体宽度。</summary>
    internal static double EntryContentWidth => EntryWidth - DesignTokens.PopupRatingSourcesEntryPadding * 2;

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
        AnchorRect = positioningRect;
        var presentation = GetOrCreatePresentation();
        Refresh();
        // RATING-04：横排卡片入口默认向下；底部卡片在屏幕下方放不下时改向上。
        var edge = ResolveEdge(preferredEdge, positioningRect, presentation.Content.Frame.Size, controlBarAnchor);
        LastPreferredEdge = edge;
        presentation.Popover.Show(positioningRect, positioningView, edge);
        InstallEscapeMonitor();
    }

    private Presentation GetOrCreatePresentation()
    {
        if (_presentation is { } existing) return existing;
        var presentation = new Presentation();
        presentation.CloseObserver = NSPopover.Notifications.ObserveDidClose(presentation.Popover, (_, _) =>
        {
            // 快速重开时，旧 DidClose 可以晚于新 Show 到达；此时它不属于当前展示。
            if (presentation.Popover.Shown || _disposed) return;
            ClosePresentation();
            if (_main.RatingSources.IsOpen) _main.RatingSources.Close();
        });
        _presentation = presentation;
        return presentation;
    }

    /// <summary>控制栏入口按令牌方向不变；卡片入口仅在屏幕下方放不下时改向上，两侧都不够时保留默认向下。</summary>
    private NSRectEdge ResolveEdge(NSRectEdge preferredEdge, CGRect positioningRect, CGSize contentSize, bool controlBarAnchor)
    {
        if (controlBarAnchor || preferredEdge != NSRectEdge.MaxYEdge || _positioningView is not { } view) return preferredEdge;
        var window = view.Window;
        var screen = window?.Screen ?? NSScreen.MainScreen;
        if (window == null || screen == null) return preferredEdge;
        var screenRect = window.ConvertRectToScreen(view.ConvertRectToView(positioningRect, null));
        var visible = screen.VisibleFrame;
        double below = (double)(screenRect.Y - visible.Y);
        double above = (double)(visible.Y + visible.Height - (screenRect.Y + screenRect.Height));
        double required = (double)contentSize.Height + DesignTokens.PopupRatingSourcesArrowMargin;
        return below < required && above >= required ? NSRectEdge.MinYEdge : preferredEdge;
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
                    Frame = new CGRect(
                        DesignTokens.PopupPadding,
                        y + (DesignTokens.PopupRatingSourcesEntrySpacing - DesignTokens.PopupRatingSourcesDividerHeight) / 2,
                        EntryWidth,
                        DesignTokens.PopupRatingSourcesDividerHeight),
                    BoxType = NSBoxType.NSBoxSeparator,
                    Title = "",
                    TitlePosition = NSTitlePosition.NoTitle,
                };
                presentation.Content.AddSubview(divider);
                presentation.Dividers.Add(divider);
            }
            y += DesignTokens.PopupRatingSourcesEntrySpacing;
        }
        double height = presentation.Entries.Count == 0
            ? DesignTokens.PopupPadding * 2
            : y - DesignTokens.PopupRatingSourcesEntrySpacing + DesignTokens.PopupPadding;
        var size = new CGSize(ContentWidth, height);
        presentation.Popover.ContentSize = size;
        if (IsOpen) presentation.Content.LayoutSubtreeIfNeeded();
    }

    /// <summary>条目布局：默认三信息同行；文件名放不下时文件名独占首行，来源与状态仍居右。</summary>
    /// <param name="name">来源文件名。</param>
    /// <param name="kind">来源类型标签（内嵌格式或 XMP）。</param>
    /// <param name="status">该来源的当前值/状态。</param>
    internal static EntryLayout MeasureEntryLayout(string name, string kind, string status)
    {
        double padding = DesignTokens.PopupRatingSourcesEntryPadding;
        double labelGap = DesignTokens.PopupRatingSourcesLabelGap;
        double available = EntryWidth - padding * 2;
        double nameWidth = Measure(name, NameFont);
        double badgeWidth = Math.Ceiling(Measure(kind, MetaFont))
            + DesignTokens.PopupRatingSourcesBadgePaddingX * 2 + 4;
        double statusWidth = Math.Ceiling(Measure(status, MetaFont)) + 4;
        double metaWidth = badgeWidth + labelGap + statusWidth;
        if (metaWidth > available)
        {
            statusWidth = Math.Max(0, available - badgeWidth - labelGap);
            metaWidth = badgeWidth + labelGap + statusWidth;
        }
        bool twoLines = nameWidth + labelGap + metaWidth > available + .5;
        double nameFit = twoLines ? available : Math.Max(0, available - labelGap - metaWidth);
        double metaBandY = twoLines
            ? padding + LabelHeight + DesignTokens.PopupRatingSourcesMetaLineGap
            : padding;
        double nameY = padding + (LabelHeight - NameFieldHeight) / 2;
        double metaY = metaBandY + (LabelHeight - MetaFieldHeight) / 2;
        double statusX = EntryWidth - padding - statusWidth;
        double badgeX = statusX - labelGap - badgeWidth;
        double starsY = metaBandY + LabelHeight + labelGap;
        double height = starsY + StarSize + padding;
        return new EntryLayout(
            twoLines,
            height,
            new CGRect(padding, nameY, nameFit, NameFieldHeight),
            new CGRect(badgeX, metaBandY, badgeWidth, LabelHeight),
            new CGRect(statusX, metaY, statusWidth, MetaFieldHeight),
            starsY);
    }

    private EntryView CreateEntry(RatingSourceItem item)
    {
        var layout = MeasureEntryLayout(item.Name, item.Kind, item.Status);
        double padding = DesignTokens.PopupRatingSourcesEntryPadding;
        double badgePadding = DesignTokens.PopupRatingSourcesBadgePaddingX;
        var name = Label(item.Name, layout.NameFrame, NameFont, DesignTokens.ColorTextPrimary,
            NSLineBreakMode.TruncatingMiddle, NSTextAlignment.Left);
        var kind = Label(item.Kind, new CGRect(badgePadding, (LabelHeight - MetaFieldHeight) / 2,
            Math.Max(0, layout.BadgeFrame.Width - badgePadding * 2), MetaFieldHeight), MetaFont,
            DesignTokens.ColorTextSecondary, NSLineBreakMode.TruncatingTail, NSTextAlignment.Left);
        var badge = new NSBox
        {
            Frame = layout.BadgeFrame,
            BoxType = NSBoxType.NSBoxCustom,
            Transparent = false,
            FillColor = NSColor.QuaternaryLabel,
            BorderWidth = 0,
            BorderColor = NSColor.Clear,
            CornerRadius = (nfloat)DesignTokens.PopupRatingSourcesBadgeCornerRadius,
            Title = "",
            TitlePosition = NSTitlePosition.NoTitle,
            ContentViewMargins = new CGSize(badgePadding, (LabelHeight - MetaFieldHeight) / 2),
            ContentView = kind,
        };
        var status = Label(item.Status, layout.StatusFrame, MetaFont, DesignTokens.ColorTextSecondary,
            NSLineBreakMode.TruncatingTail, NSTextAlignment.Right);
        // 只有 Rated 才点亮星；Missing / Invalid / 不可读 / 冲突 / 已拒绝都不冒充 0 星。
        int? rated = item.State.State == XmpRatingState.Rated ? item.State.Value : null;
        var stars = new List<NSButton>(StarCount);
        for (int rating = 0; rating < StarCount; rating++)
        {
            int target = rating;
            var button = MacControlBar.ToolbarButton.Create(
                rating == 0 ? "xmark" : rating <= (rated ?? 0) ? "star.fill" : "star",
                rating == 0 ? "无星级" : rating + " 星", () => _ = _main.RatingSources.SetRatingAsync(target), StarSize, false);
            button.Frame = new CGRect(padding + rating * (StarSize + StarSpacing), layout.StarsY, StarSize, StarSize);
            button.RefusesFirstResponder = true;
            button.ContentTintColor = MacDesignColors.Argb(rating == 0 ? DesignTokens.ColorStarClear
                : rating <= (rated ?? 0) ? DesignTokens.ColorStarOn : DesignTokens.ColorStarOff);
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
        AnchorRect = null;
        if (_presentation is { } presentation && presentation.Popover.Shown) presentation.Popover.Close();
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
        _presentation?.CloseObserver?.Dispose();
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
