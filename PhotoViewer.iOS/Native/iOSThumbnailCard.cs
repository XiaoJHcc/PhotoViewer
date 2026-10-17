using CoreGraphics;
using Foundation;
using PhotoViewer.Apple;
using PhotoViewer.Core.Image;
using PhotoViewer.Core.Platform;
using System.ComponentModel;
using UIKit;

namespace PhotoViewer.iOS.Native;

/// <summary>
/// 胶片栏卡片（[FILM-05]）：96×144 外框 + 四周 3 点占位、80 方形照片区、文件名、第二行（拍摄时间）、
/// 6 个星级、抖动徽标与占位/加载状态；几何与 Mac 原生壳同口径（<see cref="FilmstripGeometry"/> 与固定内部 frame），
/// 便于程序化对拍。星级是独立 UIButton，选择是覆盖整卡的独立 UIButton，命中测试天然分离，点星不会切图。
/// </summary>
[Register("PViOSThumbnailCard")]
internal sealed class iOSThumbnailCard : UICollectionViewCell
{
    /// <summary>外框四周占位与本卡内部尺寸（90×138 占位区、80 方形图、星级 14 点）。</summary>
    private const double Inset = 3, SurfaceWidth = 90, SurfaceHeight = 138, ImageSize = 80, StarSize = 14;
    /// <summary>第二行柱状条宽度（与第二行文字同宽）。</summary>
    private const double SecondLineWidth = 80;
    /// <summary>托管常驻集合：复用池里的卡片由 UIKit 原生持有，托管实例被回收会导致身份与订阅丢失。</summary>
    private static readonly HashSet<iOSThumbnailCard> LiveCells = [];
    private UIView _surface = null!;
    private UIImageView _thumbnail = null!;
    private UIImageView _placeholder = null!;
    private UIActivityIndicatorView _progress = null!;
    private UILabel _name = null!;
    private UILabel _date = null!;
    private UIView _secondLineBar = null!;
    private UIView _secondLineFill = null!;
    private UILabel _shake = null!;
    private UIButton _select = null!;
    private UIButton[] _stars = null!;
    private ImageFile? _file;
    private DecodedImage? _lastThumbnail;
    private UIImage? _nativeThumbnail;
    private Action<ImageFile>? _selectFile;
    private Action<ImageFile, int>? _rate;

    /// <summary>卡片当前绑定的业务身份（验收核对视图与顺序不错位）。</summary>
    internal ImageFile? BoundFile => _file;
    /// <summary>是否处于当前项高亮态（身份来自共享 <see cref="ImageFile.IsCurrent"/>）。</summary>
    internal bool IsHighlighted { get; private set; }
    /// <summary>整卡选择按钮（点选只改当前照片，由调用方保证不主动居中）。</summary>
    internal UIButton SelectButton => _select;
    /// <summary>0–5 星级按钮，索引即星级。</summary>
    internal IReadOnlyList<UIButton> StarButtons => _stars;
    /// <summary>卡片外框占位区（96×144 内部 90×138）。</summary>
    internal UIView Surface => _surface;
    /// <summary>照片区（80 方形）。</summary>
    internal UIView ThumbnailView => _thumbnail;

    /// <summary>原生复用池按 frame 构造卡片。</summary>
    /// <param name="frame">卡片外框。</param>
    [Export("initWithFrame:")]
    public iOSThumbnailCard(CGRect frame) : base(frame) => InitializeView();

    /// <summary>平台创建原生实例后由运行时按句柄构造托管包装。</summary>
    /// <param name="handle">原生句柄。</param>
    public iOSThumbnailCard(ObjCRuntime.NativeHandle handle) : base(handle) => InitializeView();

    /// <summary>幂等建卡：多构造入口与托管包装重建都只建一次子视图。</summary>
    private void InitializeView()
    {
        if (_surface != null) return;
        LiveCells.Add(this);
        ContentView.BackgroundColor = UIColor.Clear;
        _surface = new UIView(new CGRect(Inset, Inset, SurfaceWidth, SurfaceHeight));
        _surface.BackgroundColor = UIColor.FromWhiteAlpha(2f / 15, 1);
        _surface.Layer.BorderWidth = 2;
        _surface.Layer.CornerRadius = 7;
        _surface.Layer.MasksToBounds = true;
        ContentView.AddSubview(_surface);
        _thumbnail = new UIImageView(new CGRect(5, 5, ImageSize, ImageSize));
        _thumbnail.ContentMode = UIViewContentMode.ScaleAspectFit;
        _thumbnail.BackgroundColor = UIColor.FromWhiteAlpha(.2f, 1);
        _thumbnail.Layer.CornerRadius = 4;
        _thumbnail.Layer.BorderWidth = 1;
        _thumbnail.Layer.MasksToBounds = true;
        _placeholder = new UIImageView(new CGRect((SurfaceWidth - 24) / 2, (SurfaceHeight - 72) / 2, 24, 24))
        {
            Image = iOSSymbols.Symbol("photo"),
            TintColor = UIColor.TertiaryLabel,
        };
        _progress = new UIActivityIndicatorView(new CGRect(SurfaceWidth / 2 - 8, 37, 16, 16)) { HidesWhenStopped = true };
        _name = Label(new CGRect(2, 89, 86, 12), 10, UIFontWeight.Semibold);
        _date = Label(new CGRect(5, 103, SecondLineWidth, 11), 9, UIFontWeight.Bold);
        _secondLineBar = new UIView(new CGRect(5, 103, SecondLineWidth, 11));
        _secondLineBar.BackgroundColor = UIColor.FromRGB(0x44, 0x44, 0x44);
        _secondLineBar.Layer.CornerRadius = 4;
        _secondLineBar.Layer.MasksToBounds = true;
        _secondLineFill = new UIView(new CGRect(0, 0, 0, 11)) { BackgroundColor = UIColor.FromRGBA(0x1E, 0x90, 0xFF, 0x99) };
        _secondLineBar.AddSubview(_secondLineFill);
        _secondLineBar.Hidden = true;
        _shake = Label(new CGRect(67, 67, 16, 16), 11, UIFontWeight.Bold);
        _shake.Text = "抖";
        _shake.TextColor = UIColor.White;
        _shake.BackgroundColor = UIColor.FromRGB(255, 85, 0);
        _shake.Layer.CornerRadius = 2;
        _shake.Layer.MasksToBounds = true;
        _select = new UIButton(new CGRect(Inset, Inset, SurfaceWidth, SurfaceHeight));
        _select.BackgroundColor = UIColor.Clear;
        _select.AccessibilityLabel = "选择照片";
        _stars = new UIButton[6];
        _surface.AddSubview(_thumbnail);
        _surface.AddSubview(_placeholder);
        _surface.AddSubview(_progress);
        _surface.AddSubview(_name);
        _surface.AddSubview(_secondLineBar);
        _surface.AddSubview(_date);
        _surface.AddSubview(_shake);
        // 选择按钮覆盖整卡占位区，再叠加 6 个星级按钮：命中测试先到星级，点星不会落回整卡选择。
        _surface.AddSubview(_select);
        for (int rating = 0; rating < 6; rating++)
        {
            int value = rating;
            var button = new UIButton(new CGRect(Inset + rating * StarSize, 116, StarSize, StarSize));
            button.SetImage(iOSSymbols.Symbol(rating == 0 ? "xmark" : "star", rating == 0 ? 8 : 10), UIControlState.Normal);
            button.TintColor = rating == 0 ? UIColor.FromRGB(0x66, 0x66, 0x66) : UIColor.Gray;
            button.AccessibilityLabel = rating == 0 ? "无星级" : $"{rating} 星";
            button.TouchUpInside += (_, _) => { if (_file != null) _rate?.Invoke(_file, value); };
            _stars[rating] = button;
            _surface.AddSubview(button);
        }
        _select.TouchUpInside += (_, _) => { if (_file != null) _selectFile?.Invoke(_file); };
    }

    /// <summary>居中原生标签（尾部截断，超宽文件名先截断显示，完整名走 I4 的上下文菜单）。</summary>
    /// <param name="frame">标签 frame。</param>
    /// <param name="fontSize">字号。</param>
    /// <param name="weight">字重。</param>
    /// <returns>配置完成的标签。</returns>
    private static UILabel Label(CGRect frame, double fontSize, UIFontWeight weight)
        => new(frame)
        {
            Font = UIFont.SystemFontOfSize((nfloat)fontSize, weight),
            TextColor = UIColor.FromRGB(0xEE, 0xEE, 0xEE),
            TextAlignment = UITextAlignment.Center,
            LineBreakMode = UILineBreakMode.TailTruncation,
            BackgroundColor = UIColor.Clear,
        };

    /// <summary>绑定业务身份：重挂模型订阅、设置星级显隐并立即刷新一次。</summary>
    /// <param name="file">卡片对应的照片。</param>
    /// <param name="showRating">是否显示星级按钮。</param>
    /// <param name="select">整卡点选回调。</param>
    /// <param name="rate">星级点击回调。</param>
    public void Bind(ImageFile file, bool showRating, Action<ImageFile> select, Action<ImageFile, int> rate)
    {
        if (_file != null) _file.PropertyChanged -= OnFileChanged;
        _file = file;
        _selectFile = select;
        _rate = rate;
        _lastThumbnail = null;
        file.PropertyChanged += OnFileChanged;
        foreach (var star in _stars) star.Hidden = !showRating;
        Refresh();
    }

    /// <summary>模型事件封送到主线程，消费前再次核对复用后的绑定。</summary>
    /// <param name="sender">事件源（照片）。</param>
    /// <param name="args">属性变化参数。</param>
    private void OnFileChanged(object? sender, PropertyChangedEventArgs args)
        => UiDispatcher.Post(() => { if (ReferenceEquals(sender, _file)) Refresh(); });

    /// <summary>更新高亮/缓存描边、文字、星级与已有抖动结果，不触发任何推理。</summary>
    private void Refresh()
    {
        if (_file == null) return;
        _name.Text = _file.DisplayName;
        _date.Text = _file.PhotoDate?.ToString("MM-dd HH:mm") ?? "";
        IsHighlighted = _file.IsCurrent;
        _surface.Layer.BorderColor = IsHighlighted ? UIColor.SystemBlue.CGColor : UIColor.Clear.CGColor;
        _thumbnail.Layer.BorderColor = _file.IsInCache ? UIColor.FromRGBA(70, 130, 180, 180).CGColor : UIColor.Clear.CGColor;
        _placeholder.Hidden = !_file.ShowThumbnailPlaceholder;
        bool loading = _file.IsThumbnailLoading && _file.Thumbnail == null;
        if (loading) _progress.StartAnimating();
        else _progress.StopAnimating();
        _shake.Hidden = _file.IsShake != true;
        for (int index = 1; index < _stars.Length; index++)
        {
            _stars[index].SetImage(iOSSymbols.Symbol(index <= _file.Rating ? "star.fill" : "star", 10), UIControlState.Normal);
            _stars[index].TintColor = index <= _file.Rating ? UIColor.FromRGB(255, 210, 20) : UIColor.Gray;
        }
        var thumbnail = _file.Thumbnail;
        if (ReferenceEquals(thumbnail, _lastThumbnail)) return;
        var previous = _nativeThumbnail;
        _nativeThumbnail = null;
        if (thumbnail != null && thumbnail.TryAcquire())
        {
            try { _nativeThumbnail = iOSSymbols.CreateThumbnail(thumbnail); }
            finally { thumbnail.Release(); }
        }
        _thumbnail.Image = _nativeThumbnail;
        _lastThumbnail = thumbnail;
        previous?.Dispose();
    }

    /// <summary>复用前解除模型订阅与独立 UIImage（下一次绑定会重建）。</summary>
    public override void PrepareForReuse()
    {
        base.PrepareForReuse();
        if (_file != null) _file.PropertyChanged -= OnFileChanged;
        _file = null;
        _selectFile = null;
        _rate = null;
        _thumbnail.Image = null;
        _nativeThumbnail?.Dispose();
        _nativeThumbnail = null;
        _lastThumbnail = null;
        _progress.StopAnimating();
        _secondLineBar.Hidden = true;
    }

    /// <summary>销毁时解除订阅并从常驻集合移除。</summary>
    /// <param name="disposing">是否释放托管资源。</param>
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            LiveCells.Remove(this);
            if (_file != null) _file.PropertyChanged -= OnFileChanged;
            _file = null;
            _nativeThumbnail?.Dispose();
            _nativeThumbnail = null;
            _lastThumbnail = null;
        }
        base.Dispose(disposing);
    }
}
