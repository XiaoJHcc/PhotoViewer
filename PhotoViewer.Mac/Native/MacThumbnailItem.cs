using AppKit;
using CoreGraphics;
using Foundation;
using PhotoViewer.Core.Image;
using System.ComponentModel;

namespace PhotoViewer.Mac.Native;

/// <summary>虚拟化胶片栏卡片，正方形缩略图区与独立星级按钮保持原有几何和语义。</summary>
[Register("PVNativeThumbnailItem")]
public sealed class MacThumbnailItem : NSCollectionViewItem
{
    private NSImageView _thumbnail = null!;
    private NSImageView _placeholder = null!;
    private NSProgressIndicator _progress = null!;
    private NSView _surface = null!;
    private NSTextField _name = null!;
    private NSTextField _date = null!;
    private NSTextField _shake = null!;
    private static readonly HashSet<MacThumbnailItem> LiveItems = [];
    private NSButton[] _stars = null!;
    private CardView? _card;
    private ImageFile? _file;
    private DecodedImage? _lastThumbnail;
    private NSImage? _nativeThumbnail;
    private Action<ImageFile>? _select;
    private Action<ImageFile, int>? _rate;
    /// <summary>当前绑定身份供原生复用回归核对，避免视图索引与文件顺序错位。</summary>
    internal ImageFile? BoundFile => _file;

    /// <summary>原生 CollectionView 通过注册类构造可复用卡片。</summary>
    [Export("init")]
    public MacThumbnailItem() => InitializeView();

    /// <summary>AppKit NSViewController 指定构造入口，确保 makeItem 内部即完成视图装载。</summary>
    [Export("initWithNibName:bundle:")]
    public MacThumbnailItem(NSString? nibNameOrNil, NSBundle? nibBundleOrNil) : base(nibNameOrNil, nibBundleOrNil) => InitializeView();

    /// <summary>平台 makeItem 创建原生实例后，由运行时通过句柄构造托管包装。</summary>
    public MacThumbnailItem(ObjCRuntime.NativeHandle handle) : base(handle) => InitializeView();

    /// <summary>原生视图懒加载入口，保证 AppKit 访问 item.view 时已完成子视图初始化。</summary>
    public override void LoadView() => InitializeView();

    /// <summary>多个原生构造入口共用幂等卡片视图与按钮接线，常驻托管引用防止复用池内被 GC 回收重建 View。</summary>
    private void InitializeView()
    {
        LiveItems.Add(this);
        if (_card != null) return;
        _card = new CardView(new CGRect(0, 0, MacFilmstripLayout.CardWidth, MacFilmstripLayout.CardHeight));
        View = _card;
        _surface = new FlippedView(new CGRect(3, 3, 90, 138)) { WantsLayer = true };
        _surface.Layer!.BackgroundColor = NSColor.FromWhite(2f / 15, 1).CGColor;
        _surface.Layer.BorderWidth = 2;
        _surface.Layer.CornerRadius = 7;
        _card.AddSubview(_surface);
        _thumbnail = new NSImageView(new CGRect(5, 5, 80, 80)) { ImageScaling = NSImageScale.ProportionallyUpOrDown, WantsLayer = true };
        _thumbnail.Layer!.CornerRadius = 4;
        _thumbnail.Layer.MasksToBounds = true;
        _thumbnail.Layer.BackgroundColor = NSColor.FromWhite(.2f, 1).CGColor;
        _thumbnail.Layer.BorderWidth = 1;
        _placeholder = new NSImageView(new CGRect(33, 33, 24, 24)) { Image = MacSymbols.Get("photo"), ContentTintColor = NSColor.TertiaryLabel };
        _progress = new NSProgressIndicator(new CGRect(37, 37, 16, 16)) { Style = NSProgressIndicatorStyle.Spinning, Indeterminate = true };
        _name = Label(new CGRect(2, 89, 86, 12), 10);
        _date = Label(new CGRect(5, 103, 80, 11), 9);
        _name.Font = NSFont.SystemFontOfSize(10, NSFontWeight.Semibold) ?? throw new InvalidOperationException("系统字体不可用");
        _date.Font = NSFont.SystemFontOfSize(9, NSFontWeight.Bold) ?? throw new InvalidOperationException("系统字体不可用");
        _shake = Label(new CGRect(67, 67, 16, 16), 11);
        _shake.StringValue = "抖";
        _shake.WantsLayer = true;
        _shake.Layer!.BackgroundColor = NSColor.FromRgb(255, 85, 0).CGColor;
        _shake.Layer.CornerRadius = 2;
        _stars = new NSButton[6];
        for (int rating = 0; rating < 6; rating++)
        {
            int value = rating;
            var button = MacSymbols.Button(rating == 0 ? "xmark" : "star", rating == 0 ? "无星级" : rating + " 星", () => { if (_file != null) _rate?.Invoke(_file, value); }, 14, false);
            button.Frame = new CGRect(3 + rating * 14, 116, 14, 14);
            button.ContentTintColor = NSColor.SecondaryLabel;
            _stars[rating] = button;
            _surface.AddSubview(button);
        }
        _surface.AddSubview(_thumbnail); _surface.AddSubview(_placeholder); _surface.AddSubview(_progress);
        _surface.AddSubview(_name); _surface.AddSubview(_date); _surface.AddSubview(_shake);
        _card.SelectedByUser = () => { if (_file != null) _select?.Invoke(_file); };
    }

    /// <summary>用原生标签显示卡片文字，超宽文件名通过 tooltip 查看。</summary>
    private static NSTextField Label(CGRect frame, double fontSize)
        => new(frame) { Editable = false, Selectable = false, Bordered = false, DrawsBackground = false,
            Alignment = NSTextAlignment.Center, Font = NSFont.SystemFontOfSize((nfloat)fontSize) ?? throw new InvalidOperationException("系统字体不可用"), TextColor = NSColor.White, LineBreakMode = NSLineBreakMode.TruncatingTail };

    /// <summary>绑定新文件，取消旧模型订阅，防止虚拟化复用时评分/缩略图串位。</summary>
    public void Bind(ImageFile file, bool showRating, Action<ImageFile> select, Action<ImageFile, int> rate)
    {
        if (_file != null) _file.PropertyChanged -= OnFileChanged;
        _file = file; _select = select; _rate = rate;
        _lastThumbnail = null;
        file.PropertyChanged += OnFileChanged;
        foreach (var star in _stars) star.Hidden = !showRating;
        Refresh();
    }

    /// <summary>模型事件封送到 AppKit，消费前再次检查复用后的绑定。</summary>
    private void OnFileChanged(object? sender, PropertyChangedEventArgs args)
        => PhotoViewer.Core.Platform.UiDispatcher.Post(() => { if (ReferenceEquals(sender, _file)) Refresh(); });

    /// <summary>更新当前/缓存描边、文字、评级和已有抖动结果，不触发任何推理。</summary>
    private void Refresh()
    {
        if (_file == null) return;
        _name.StringValue = _file.DisplayName;
        _name.ToolTip = _file.Name;
        _date.StringValue = _file.PhotoDate?.ToString("MM-dd HH:mm") ?? "";
        _date.ToolTip = _file.PhotoDate?.ToString("yyyy-MM-dd HH:mm:ss zzz");
        _surface.Layer!.BorderColor = _file.IsCurrent ? NSColor.SystemBlue.CGColor : NSColor.Clear.CGColor;
        _thumbnail.Layer!.BorderColor = _file.IsInCache ? NSColor.FromRgba(70, 130, 180, 180).CGColor : NSColor.Clear.CGColor;
        _placeholder.Hidden = !_file.ShowThumbnailPlaceholder;
        bool loading = _file.IsThumbnailLoading && _file.Thumbnail == null;
        _progress.Hidden = !loading;
        if (loading) _progress.StartAnimation(null);
        else _progress.StopAnimation(null);
        _shake.Hidden = _file.IsShake != true;
        for (int index = 1; index < _stars.Length; index++)
        {
            _stars[index].Image = MacSymbols.Get(index <= _file.Rating ? "star.fill" : "star");
            _stars[index].ContentTintColor = index <= _file.Rating ? NSColor.FromRgb(255, 210, 20) : NSColor.Gray;
        }
        var thumbnail = _file.Thumbnail;
        if (!ReferenceEquals(thumbnail, _lastThumbnail))
        {
            var previous = _nativeThumbnail;
            _nativeThumbnail = null;
            if (thumbnail != null && thumbnail.TryAcquire())
            {
                try { _nativeThumbnail = MacSymbols.CreateThumbnail(thumbnail); }
                finally { thumbnail.Release(); }
            }
            _thumbnail.Image = _nativeThumbnail;
            _lastThumbnail = thumbnail;
            previous?.Dispose();
        }
    }

    /// <summary>卡片回收时取消模型订阅与独立 NSImage。</summary>
    [Export("prepareForReuse")]
    public void PrepareForReuse()
    {
        if (_file != null) _file.PropertyChanged -= OnFileChanged;
        _file = null; _thumbnail.Image = null; _nativeThumbnail?.Dispose(); _nativeThumbnail = null; _lastThumbnail = null;
        _progress.StopAnimation(null);
        _progress.Hidden = true;
    }

    /// <summary>销毁卡片时解除模型订阅并从常驻集合移除。</summary>
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            LiveItems.Remove(this);
            if (_file != null) _file.PropertyChanged -= OnFileChanged;
            _file = null;
            _nativeThumbnail?.Dispose();
            _nativeThumbnail = null;
            _lastThumbnail = null;
        }
        base.Dispose(disposing);
    }

    private sealed class FlippedView(CGRect frame) : NSView(frame) { public override bool IsFlipped => true; }

    /// <summary>卡片背景与选择命中；原生按钮自行消费点击，改星不会冒泡切图。</summary>
    private sealed class CardView : NSView
    {
        public Action? SelectedByUser { get; set; }
        public override bool IsFlipped => true;
        /// <summary>建立卡片背景。</summary>
        public CardView(CGRect frame) : base(frame) { }
        /// <summary>照片/文字背景区域点击才改变当前照片。</summary>
        public override void MouseDown(NSEvent theEvent) => SelectedByUser?.Invoke();
        /// <summary>图片/静态标签点击归卡片选择，星级按钮保持独立命中，避免误切照片。</summary>
        public override NSView? HitTest(CGPoint point)
        {
            var hit = base.HitTest(point);
            return hit == null || hit is NSButton ? hit : this;
        }
    }
}
