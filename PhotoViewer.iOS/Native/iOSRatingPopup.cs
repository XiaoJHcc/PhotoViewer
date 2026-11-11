using System.ComponentModel;
using CoreGraphics;
using Foundation;
using PhotoViewer.Core;
using PhotoViewer.Core.Design;
using PhotoViewer.Core.Image;
using PhotoViewer.Core.Platform;
using PhotoViewer.Shared;
using PhotoViewer.ViewModels.Main;
using UIKit;

namespace PhotoViewer.iOS.Native;

/// <summary>
/// 星级弹窗（RATING-07）：窗口内的玻璃面板，不用系统浮窗——紧凑态与卡片入口要把同一次触摸
/// 从入口延续到面板星排上。非冲突显示单条目（文件名 + 可选拍摄时间 + 星排），冲突按来源逐条纵排
/// 并用分隔线分开（RATING-04）；所有排版取共享 <see cref="RatingPopupLayout"/>，落位取
/// <see cref="RatingControlGeometry.PlacePopup"/>。面板下方铺一层全窗透明拦截层，点按外部即关闭并吞掉该次触摸。
/// 数据由共享 <see cref="RatingSourcesViewModel"/> 驱动：打开即 Show，写入走来源整组写回，
/// 成功且冲突解除时该 VM 关闭、弹窗随之收起。
/// </summary>
internal sealed class iOSRatingPopup : IDisposable
{
    /// <summary>弹窗内容形态：单条目或按来源逐条。</summary>
    internal enum RatingPopupMode
    {
        /// <summary>非冲突：一个文件名/时间行加一条星排。</summary>
        Single,

        /// <summary>冲突：每个来源一条，任一条目点值整组写回。</summary>
        Sources,
    }

    private readonly MainViewModel _main;
    private readonly UIView _host;
    private readonly Func<ImageFile, int, Task> _commit;
    private readonly List<iOSRatingRow> _rows = [];
    private readonly List<UILabel> _names = [];
    private readonly List<UILabel> _times = [];
    private readonly List<UIView> _badges = [];
    private readonly List<UILabel> _statuses = [];
    private readonly List<UIView> _dividers = [];
    private readonly UIFont _nameFont = UIFont.SystemFontOfSize((nfloat)DesignTokens.PopupRatingSourcesNameFontSize,
        UIFontWeight.Semibold) ?? throw new InvalidOperationException("系统字体不可用");
    private readonly UIFont _metaFont = UIFont.SystemFontOfSize((nfloat)DesignTokens.PopupRatingSourcesMetaFontSize)
        ?? throw new InvalidOperationException("系统字体不可用");
    private UIView? _catcher;
    private bool _horizontalRow = true;
    private bool _busy;
    private bool _refreshing;
    private bool _disposed;

    /// <summary>建立弹窗控制器；面板与拦截层在 <see cref="Open"/> 时才进入视图树。</summary>
    /// <param name="main">共享主 VM（来源条目与整组写回都走它）。</param>
    /// <param name="host">承载面板的宿主视图（主屏根视图，面板浮在所有主屏内容之上）。</param>
    /// <param name="commit">实际执行写回的入口（由根控制器提供，成功后由共享真值驱动收起）。</param>
    internal iOSRatingPopup(MainViewModel main, UIView host, Func<ImageFile, int, Task> commit)
    {
        _main = main;
        _host = host;
        _commit = commit;
    }

    /// <summary>弹窗是否打开。</summary>
    internal bool IsOpen { get; private set; }

    /// <summary>当前内容形态。</summary>
    internal RatingPopupMode Mode { get; private set; }

    /// <summary>展开方向（朝主图内侧）。</summary>
    internal RatingPopupDirection Direction { get; private set; }

    /// <summary>入口类型：控件入口与卡片入口的方向表相反。</summary>
    internal RatingPopupAnchor AnchorKind { get; private set; }

    /// <summary>入口视图（控件胶囊或胶片卡片）。</summary>
    internal UIView? AnchorView { get; private set; }

    /// <summary>打开时入口在宿主坐标中的矩形（闭环复检与重排后关闭的判据）。</summary>
    internal LayoutRect AnchorRect { get; private set; }

    /// <summary>目标照片。</summary>
    internal ImageFile? Target { get; private set; }

    /// <summary>玻璃面板；关闭后为 null。</summary>
    internal UIVisualEffectView? Panel { get; private set; }

    /// <summary>面板在宿主坐标中的矩形（紧凑态取消带按它与胶囊求并集）。</summary>
    internal LayoutRect PanelFrame { get; private set; }

    /// <summary>面板承载的宿主视图（坐标换算用）。</summary>
    internal UIView Host => _host;

    /// <summary>当前内容的星排，顺序与条目一致。</summary>
    internal IReadOnlyList<iOSRatingRow> Rows => _rows;

    /// <summary>单条目模式下可继续承接手势的星排；来源模式没有。</summary>
    internal iOSRatingRow? TrackingRow
        => Mode == RatingPopupMode.Single && _rows.Count > 0 ? _rows[0] : null;

    /// <summary>条目文件名标签。</summary>
    internal IReadOnlyList<UILabel> NameLabels => _names;

    /// <summary>条目拍摄时间标签（仅单条目模式）。</summary>
    internal IReadOnlyList<UILabel> TimeLabels => _times;

    /// <summary>来源标签底衬。</summary>
    internal IReadOnlyList<UIView> BadgeViews => _badges;

    /// <summary>来源状态文本。</summary>
    internal IReadOnlyList<UILabel> StatusLabels => _statuses;

    /// <summary>来源条目之间的分隔线。</summary>
    internal IReadOnlyList<UIView> DividerViews => _dividers;

    /// <summary>全窗透明拦截层（点按外部关闭）。</summary>
    internal UIView? Catcher => _catcher;

    /// <summary>验收缝：减少动态效果时只淡入淡出。</summary>
    internal Func<bool> ReduceMotionProvider { get; set; } = () => UIAccessibility.IsReduceMotionEnabled;

    /// <summary>
    /// 打开弹窗：关掉可能还在的旧面板，登记入口与目标，让来源 VM 进入打开态，按当前真值建内容并落位，
    /// 最后铺拦截层并做入场动效。
    /// </summary>
    /// <param name="file">目标照片。</param>
    /// <param name="anchor">入口视图（控件胶囊或胶片卡片）。</param>
    /// <param name="anchorKind">入口类型。</param>
    /// <param name="direction">展开方向。</param>
    /// <param name="horizontalRow">星排是否横排（竖排控件入口为 false）。</param>
    internal void Open(ImageFile file, UIView anchor, RatingPopupAnchor anchorKind, RatingPopupDirection direction,
        bool horizontalRow)
    {
        if (_disposed) return;
        CloseImmediately();
        Target = file;
        AnchorView = anchor;
        AnchorKind = anchorKind;
        Direction = direction;
        _horizontalRow = horizontalRow;
        AnchorRect = RectOf(anchor);
        Mode = file.HasRatingConflict ? RatingPopupMode.Sources : RatingPopupMode.Single;
        _main.ShowRatingSources(file);
        _main.RatingSources.PropertyChanged += OnSourcesChanged;
        file.PropertyChanged += OnTargetChanged;
        IsOpen = true;
        Build();
        if (Panel is not { } panel) return;
        _catcher = new DismissCatcher(this)
        {
            Frame = _host.Bounds,
            BackgroundColor = UIColor.Clear,
            AutoresizingMask = UIViewAutoresizing.FlexibleWidth | UIViewAutoresizing.FlexibleHeight,
        };
        _host.AddSubview(_catcher);
        _host.AddSubview(panel);
        iOSRatingHaptics.Prepare();
        AnimateIn(panel);
    }

    /// <summary>
    /// 关闭弹窗（幂等）：立即标记收起、退订、移走拦截层，把来源 VM 带回关闭态，再淡出面板。
    /// </summary>
    internal void Close()
    {
        if (!IsOpen && Panel == null) return;
        IsOpen = false;
        Unsubscribe();
        if (_main.RatingSources.IsOpen) _main.RatingSources.Close();
        RemoveCatcher();
        if (Panel is not { } panel) return;
        Panel = null;
        double duration = ReduceMotionProvider() ? .15 : DesignTokens.MotionRatingPopupClose / 1000.0;
        UIView.AnimateNotify(duration, () => panel.Alpha = 0, _ => panel.RemoveFromSuperview());
    }

    /// <summary>立即关闭并移走面板（不做退场动效，切图/重开前使用）。</summary>
    internal void CloseImmediately()
    {
        IsOpen = false;
        Unsubscribe();
        if (_main.RatingSources.IsOpen) _main.RatingSources.Close();
        RemoveCatcher();
        Panel?.RemoveFromSuperview();
        Panel = null;
        ClearContent();
    }

    /// <summary>
    /// 重排后复检入口：入口离开窗口、自身或祖先隐藏、或矩形相对打开时移动超过半点即关闭，
    /// 避免面板停在一个已经消失的入口旁边。
    /// </summary>
    internal void RevalidateAnchor()
    {
        if (!IsOpen) return;
        if (AnchorView is not { } anchor || anchor.Window == null || _host.Window == null)
        {
            Close();
            return;
        }
        for (UIView? view = anchor; view != null && !ReferenceEquals(view, _host); view = view.Superview)
        {
            if (!view.Hidden) continue;
            Close();
            return;
        }
        var rect = RectOf(anchor);
        if (Math.Abs(rect.X - AnchorRect.X) > .5 || Math.Abs(rect.Y - AnchorRect.Y) > .5
            || Math.Abs(rect.Width - AnchorRect.Width) > .5 || Math.Abs(rect.Height - AnchorRect.Height) > .5)
            Close();
    }

    /// <summary>只在入口正是指定类型时关闭（列表滚动关卡片入口、控制栏滚动关控件入口）。</summary>
    /// <param name="kind">入口类型。</param>
    internal void CloseIfAnchoredTo(RatingPopupAnchor kind)
    {
        if (IsOpen && AnchorKind == kind) Close();
    }

    /// <summary>销毁：关闭并退订。</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        CloseImmediately();
    }

    /// <summary>
    /// 写回一次星级：写回期间忽略后续请求，完成后若弹窗还在就按模型真值重画
    /// （清掉待确认态）。成功且冲突解除时来源 VM 会自行关闭，弹窗随之收起。
    /// </summary>
    /// <param name="value">星级。</param>
    private async void CommitAsync(int value)
    {
        if (_busy || !IsOpen || Target is not { } target) return;
        _busy = true;
        try
        {
            await _commit(target, value);
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"[Native] 星级弹窗写回失败：{exception.Message}");
        }
        finally
        {
            _busy = false;
        }
        if (IsOpen) Refresh();
    }

    /// <summary>来源条目变化：条目重建；来源 VM 收起（写入成功且冲突解除）即关闭弹窗。</summary>
    /// <param name="sender">事件源（来源 VM）。</param>
    /// <param name="args">属性变化参数。</param>
    private void OnSourcesChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (!IsOpen) return;
        if (!UiDispatcher.CheckAccess())
        {
            UiDispatcher.Post(() => OnSourcesChanged(sender, args));
            return;
        }
        if (args.PropertyName == nameof(RatingSourcesViewModel.Items))
        {
            Refresh();
            return;
        }
        if (args.PropertyName == nameof(RatingSourcesViewModel.IsOpen) && !_main.RatingSources.IsOpen) Close();
    }

    /// <summary>目标照片的评分/冲突/文件名/拍摄时间变化后按真值重画（封送到主线程）。</summary>
    /// <param name="sender">事件源（目标照片）。</param>
    /// <param name="args">属性变化参数。</param>
    private void OnTargetChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (!IsOpen || !ReferenceEquals(sender, Target)) return;
        if (args.PropertyName is not (nameof(ImageFile.Rating) or nameof(ImageFile.HasRatingConflict)
            or nameof(ImageFile.RatingSources) or nameof(ImageFile.DisplayName) or nameof(ImageFile.PhotoDate))) return;
        if (!UiDispatcher.CheckAccess())
        {
            UiDispatcher.Post(() => OnTargetChanged(sender, args));
            return;
        }
        Refresh();
    }

    /// <summary>按共享真值重建内容；星排正在手势中时跳过，避免把手指下的格子换掉。</summary>
    private void Refresh()
    {
        if (!IsOpen || _refreshing || Target is not { } file) return;
        if (_rows.Any(row => row.IsTracking)) return;
        _refreshing = true;
        try
        {
            Mode = file.HasRatingConflict && _main.RatingSources.Items.Count > 0
                ? RatingPopupMode.Sources
                : RatingPopupMode.Single;
            Build();
        }
        finally
        {
            _refreshing = false;
        }
    }

    /// <summary>按当前形态与真值重建面板内容并重新落位。</summary>
    private void Build()
    {
        if (Target is not { } file) return;
        ClearContent();
        Panel ??= CreatePanel();
        if (Panel is not { } panel) return;
        var content = panel.ContentView;
        var bounds = EdgeBounds();
        var items = Mode == RatingPopupMode.Sources ? _main.RatingSources.Items : [];
        bool sources = Mode == RatingPopupMode.Sources && items.Count > 0;
        if (Mode == RatingPopupMode.Sources && !sources) Mode = RatingPopupMode.Single;
        string? time = sources ? null : file.PhotoDate?.ToString("MM-dd HH:mm");
        // 来源条目恒为横排星排，只有非冲突单条目才随竖排控件改成竖排星列。
        bool horizontal = sources || _horizontalRow;
        double availableMain = horizontal ? bounds.Width : bounds.Height;
        double chrome = horizontal
            ? RatingPopupLayout.HorizontalChrome
            : RatingPopupLayout.ColumnChrome(time != null);
        double pitch = RatingPopupLayout.Pitch(availableMain, chrome);

        var entries = new List<RatingPopupEntryLayout>();
        if (sources)
        {
            foreach (var item in items)
            {
                entries.Add(RatingPopupLayout.Source(Measure(item.Name, _nameFont), BadgeWidth(item.Kind),
                    Measure(item.Status, _metaFont) + 4, pitch));
            }
        }
        else
        {
            double timeWidth = time == null ? 0 : Measure(time, _metaFont) + 4;
            entries.Add(horizontal
                ? RatingPopupLayout.SingleRow(Measure(file.DisplayName, _nameFont), timeWidth, pitch)
                : RatingPopupLayout.SingleColumn(Measure(file.DisplayName, _nameFont), timeWidth, pitch));
        }

        for (int index = 0; index < entries.Count; index++)
        {
            var entry = entries[index];
            double top = RatingPopupLayout.EntryTop(entries, index);
            double x = RatingPopupLayout.Padding;
            if (sources)
            {
                var item = items[index];
                AddName(content, item.Name, x + entry.Name.X, top + entry.Name.Y, entry.Name.Width);
                AddBadge(content, item.Kind, x + entry.Badge.X, top + entry.Badge.Y, entry.Badge.Width);
                AddStatus(content, item.Status, x + entry.Status.X, top + entry.Status.Y, entry.Status.Width);
                AddRow(content, entry, top, pitch, item.State.State == XmpRatingState.Rated ? item.State.Value ?? 0 : 0, true);
            }
            else
            {
                AddName(content, file.DisplayName, x + entry.Name.X, top + entry.Name.Y, entry.Name.Width);
                if (time != null && entry.Time.Width > 0) AddTime(content, time, x + entry.Time.X, top + entry.Time.Y, entry.Time.Width);
                AddRow(content, entry, top, pitch, Math.Max(0, file.Rating), false);
            }
            if (index < entries.Count - 1)
            {
                var divider = new UIView(new CGRect(x, RatingPopupLayout.DividerY(entries, index),
                    entry.Width, RatingPopupLayout.DividerHeight))
                {
                    BackgroundColor = UIColor.Separator,
                    UserInteractionEnabled = false,
                };
                content.AddSubview(divider);
                _dividers.Add(divider);
            }
        }

        var size = RatingPopupLayout.PanelSize(entries);
        PanelFrame = RatingControlGeometry.PlacePopup(AnchorRect, bounds, size.Width, size.Height, Direction,
            DesignTokens.RatingPopupGap);
        panel.Transform = CGAffineTransform.MakeIdentity();
        panel.Alpha = 1;
        panel.Frame = new CGRect(PanelFrame.X, PanelFrame.Y, PanelFrame.Width, PanelFrame.Height);
    }

    /// <summary>新建玻璃面板：系统玻璃材质 + 令牌圆角，固定深色外观。</summary>
    /// <returns>面板视图。</returns>
    private static UIVisualEffectView CreatePanel() => new(new UIGlassEffect())
    {
        CornerConfiguration = UICornerConfiguration.CreateUniformCorners(
            UICornerRadius.CreateFixed((nfloat)DesignTokens.RatingPopupCornerRadius)),
        OverrideUserInterfaceStyle = UIUserInterfaceStyle.Dark,
        ClipsToBounds = true,
    };

    /// <summary>可放置边界：宿主安全区再内缩来源弹窗的贴边余量。</summary>
    /// <returns>边界矩形（宿主坐标）。</returns>
    private LayoutRect EdgeBounds()
    {
        var safe = _host.SafeAreaInsets;
        double edge = DesignTokens.PopupRatingSourcesEdgeMargin;
        double width = Math.Max(1, (double)_host.Bounds.Width - (double)safe.Left - (double)safe.Right - edge * 2);
        double height = Math.Max(1, (double)_host.Bounds.Height - (double)safe.Top - (double)safe.Bottom - edge * 2);
        return new LayoutRect((double)safe.Left + edge, (double)safe.Top + edge, width, height);
    }

    /// <summary>入口在宿主坐标中的矩形（用完整 bounds，入口内部子视图位置不影响锚定）。</summary>
    /// <param name="view">入口视图。</param>
    /// <returns>宿主坐标矩形。</returns>
    private LayoutRect RectOf(UIView view)
    {
        var rect = view.ConvertRectToView(view.Bounds, _host);
        return new LayoutRect(rect.X, rect.Y, rect.Width, rect.Height);
    }

    /// <summary>文件名标签：中间省略、宽度已让出同行的时间或来源信息、在信息行带内垂直居中。</summary>
    private void AddName(UIView content, string text, double x, double y, double width)
    {
        var label = Label(text, _nameFont, UIColor.Label, UILineBreakMode.MiddleTruncation, UITextAlignment.Left);
        label.Frame = new CGRect(x, y + Vertically(_nameFont), Math.Max(0, width), _nameFont.LineHeight);
        _names.Add(label);
        content.AddSubview(label);
    }

    /// <summary>拍摄时间标签：尾部截断、在其矩形内右对齐。</summary>
    private void AddTime(UIView content, string text, double x, double y, double width)
    {
        var label = Label(text, _metaFont, UIColor.SecondaryLabel, UILineBreakMode.TailTruncation, UITextAlignment.Right);
        label.Frame = new CGRect(x, y + Vertically(_metaFont), width, _metaFont.LineHeight);
        _times.Add(label);
        content.AddSubview(label);
    }

    /// <summary>来源标签：系统四级填充底衬 + 圆角，内文尾部截断。</summary>
    private void AddBadge(UIView content, string kind, double x, double y, double width)
    {
        var badge = new UIView(new CGRect(x, y, width, RatingPopupLayout.InfoHeight))
        {
            BackgroundColor = UIColor.QuaternarySystemFill,
            UserInteractionEnabled = false,
        };
        badge.Layer.CornerRadius = (nfloat)DesignTokens.PopupRatingSourcesBadgeCornerRadius;
        double padding = DesignTokens.PopupRatingSourcesBadgePaddingX;
        var label = Label(kind, _metaFont, UIColor.SecondaryLabel, UILineBreakMode.TailTruncation, UITextAlignment.Left);
        label.Frame = new CGRect(padding, Vertically(_metaFont), Math.Max(0, width - padding * 2), _metaFont.LineHeight);
        badge.AddSubview(label);
        _badges.Add(badge);
        content.AddSubview(badge);
    }

    /// <summary>来源状态文本：尾部截断、右对齐。</summary>
    private void AddStatus(UIView content, string status, double x, double y, double width)
    {
        var label = Label(status, _metaFont, UIColor.SecondaryLabel, UILineBreakMode.TailTruncation, UITextAlignment.Right);
        label.Frame = new CGRect(x, y + Vertically(_metaFont), width, _metaFont.LineHeight);
        _statuses.Add(label);
        content.AddSubview(label);
    }

    /// <summary>条目星排：几何取共享排版结果，点值即整组写回。</summary>
    private void AddRow(UIView content, RatingPopupEntryLayout entry, double top, double pitch, int value, bool forceCommit)
    {
        var row = new iOSRatingRow(pitch, DesignTokens.RatingPopupGlyphFontSize, DesignTokens.RatingPopupClearGlyphFontSize)
        {
            Horizontal = entry.StarsHorizontal,
            Value = value,
            Conflict = false,
            ZeroOpensPopup = false,
            ForceCommit = forceCommit,
            Frame = new CGRect(RatingPopupLayout.Padding + entry.Stars.X, top + entry.Stars.Y,
                entry.Stars.Width, entry.Stars.Height),
        };
        row.Committed += committed => CommitAsync(committed);
        _rows.Add(row);
        content.AddSubview(row);
    }

    /// <summary>标签在信息行带内的垂直居中偏移。</summary>
    /// <param name="font">标签字体。</param>
    /// <returns>相对行带顶边的偏移。</returns>
    private static double Vertically(UIFont font)
        => (RatingPopupLayout.InfoHeight - (double)font.LineHeight) / 2;

    /// <summary>系统字体下的单行文本宽度。</summary>
    /// <param name="text">文本。</param>
    /// <param name="font">字体。</param>
    /// <returns>宽度。</returns>
    private static double Measure(string? text, UIFont font)
        => string.IsNullOrEmpty(text)
            ? 0
            : new NSString(text).GetSizeUsingAttributes(new UIStringAttributes { Font = font }).Width;

    /// <summary>来源标签总宽：文字宽 + 两侧内边距 + UILabel 自带的约 4 点控件内边距。</summary>
    /// <param name="kind">来源标签文本。</param>
    /// <returns>底衬宽度。</returns>
    private double BadgeWidth(string? kind)
        => Math.Ceiling(Measure(kind, _metaFont)) + DesignTokens.PopupRatingSourcesBadgePaddingX * 2 + 4;

    /// <summary>单行只读标签。</summary>
    private static UILabel Label(string? text, UIFont font, UIColor color, UILineBreakMode breakMode,
        UITextAlignment alignment) => new(CGRect.Empty)
    {
        Text = text,
        Font = font,
        TextColor = color,
        LineBreakMode = breakMode,
        TextAlignment = alignment,
        BackgroundColor = UIColor.Clear,
        UserInteractionEnabled = false,
        Lines = 1,
    };

    /// <summary>移除上一轮内容；面板实例保留（刷新时只换内容与几何）。</summary>
    private void ClearContent()
    {
        if (Panel is { } panel)
        {
            foreach (var subview in panel.ContentView.Subviews)
            {
                subview.RemoveFromSuperview();
                subview.Dispose();
            }
        }
        _names.Clear();
        _times.Clear();
        _badges.Clear();
        _statuses.Clear();
        _dividers.Clear();
        _rows.Clear();
    }

    /// <summary>退订来源 VM 与目标照片。</summary>
    private void Unsubscribe()
    {
        _main.RatingSources.PropertyChanged -= OnSourcesChanged;
        if (Target is { } target) target.PropertyChanged -= OnTargetChanged;
    }

    /// <summary>移走全窗拦截层。</summary>
    private void RemoveCatcher()
    {
        _catcher?.RemoveFromSuperview();
        _catcher = null;
    }

    /// <summary>入场：面板从入口一侧略小并偏向入口，弹簧放大到原位；减少动态效果时只淡入。</summary>
    /// <param name="panel">面板视图。</param>
    private void AnimateIn(UIVisualEffectView panel)
    {
        panel.Alpha = 0;
        if (ReduceMotionProvider())
        {
            UIView.Animate(.15, () => panel.Alpha = 1);
            return;
        }
        double dx = (double)(AnchorRect.X + AnchorRect.Width / 2) - (double)panel.Center.X;
        double dy = (double)(AnchorRect.Y + AnchorRect.Height / 2) - (double)panel.Center.Y;
        double length = Math.Sqrt(dx * dx + dy * dy);
        if (length > .5)
        {
            double shift = Math.Min(24, length * .25);
            dx = dx / length * shift;
            dy = dy / length * shift;
        }
        else
        {
            dx = dy = 0;
        }
        var transform = CGAffineTransform.MakeTranslation((nfloat)dx, (nfloat)dy);
        transform.Scale((nfloat).85f, (nfloat).85f);
        panel.Transform = transform;
        UIView.AnimateNotify(DesignTokens.MotionRatingPopupOpen / 1000.0, 0, (nfloat).82, 0,
            UIViewAnimationOptions.BeginFromCurrentState,
            () =>
            {
                panel.Alpha = 1;
                panel.Transform = CGAffineTransform.MakeIdentity();
            }, null);
    }

    /// <summary>全窗透明拦截层：吞掉外部点按并关闭弹窗。</summary>
    private sealed class DismissCatcher(iOSRatingPopup owner) : UIView
    {
        /// <summary>点按外部：关闭弹窗，不把这次触摸继续交给下面的主图。</summary>
        /// <param name="touches">触摸集合。</param>
        /// <param name="evt">事件。</param>
        public override void TouchesBegan(NSSet touches, UIEvent? evt) => owner.Close();
    }
}
