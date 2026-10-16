using CoreGraphics;
using PhotoViewer.Core.Image;
using PhotoViewer.Core.Platform;
using PhotoViewer.ViewModels.Main;
using System.ComponentModel;
using UIKit;

namespace PhotoViewer.iOS.Native;

/// <summary>
/// I2 原生主屏：主图占满屏幕（安全区与栏位布局留待 I3），共享 <see cref="PhotoLoadSession"/> 提供展示产物，
/// <see cref="iOSPhotoView"/> 负责 Metal 上屏与 UIKit 手势。状态标签与「打开文件夹 / 设置」按钮暂留为临时浮层（I3 换成真实主屏）。
/// </summary>
internal sealed class iOSNativeRootViewController : UIViewController
{
    private readonly MainViewModel _main;
    private readonly PhotoLoadSession _session;
    private readonly iOSPhotoView _photo = new(CGRect.Empty);
    private readonly UILabel _statusLabel = new();
    private readonly UILabel _hintLabel = new();
    private ImageFile? _observedFile;
    private bool _sessionStarted;

    /// <summary>主图宿主（验收读取视口与抓帧）。</summary>
    internal iOSPhotoView Photo => _photo;

    /// <summary>用共享主 VM 与展示会话构建原生主屏。</summary>
    /// <param name="main">共享主 VM。</param>
    /// <param name="session">共享展示加载会话（所有权在壳层，生命周期与根控制器一致）。</param>
    public iOSNativeRootViewController(MainViewModel main, PhotoLoadSession session)
    {
        _main = main;
        _session = session;
        // 订阅在构造期完成：启动参数（--pv-open / 外部打开）可能在根控制器上屏前就选中了首图，
        // 若等到 ViewWillAppear 再订阅会漏掉那次 CurrentFile 变化而永不加载。
        _session.Changed += OnSessionChanged;
        _photo.ViewportChanged += ReportViewport;
        _main.FolderVM.AllFilesChanged += OnAllFilesChanged;
        _main.PropertyChanged += OnMainPropertyChanged;
        _observedFile = _main.CurrentFile;
        if (_observedFile != null) _observedFile.PropertyChanged += OnCurrentFileChanged;
    }

    /// <summary>布局完成后视口才有效；此前的切图只登记当前文件，等首次有效视口再发起解码。</summary>
    public override void ViewDidLayoutSubviews()
    {
        base.ViewDidLayoutSubviews();
        StartSession();
    }

    /// <summary>搭建主图层、临时状态浮层与空态提示。</summary>
    public override void ViewDidLoad()
    {
        base.ViewDidLoad();

        var root = View!;
        root.BackgroundColor = UIColor.FromRGB(18, 18, 18);
        _photo.AutoresizingMask = UIViewAutoresizing.FlexibleWidth | UIViewAutoresizing.FlexibleHeight;
        _photo.Frame = root.Bounds;
        root.AddSubview(_photo);

        _hintLabel.Text = "打开文件夹后在这里查看照片";
        _hintLabel.Font = UIFont.SystemFontOfSize(15);
        _hintLabel.TextColor = UIColor.Gray;
        _hintLabel.TextAlignment = UITextAlignment.Center;
        _hintLabel.Lines = 0;
        _hintLabel.TranslatesAutoresizingMaskIntoConstraints = false;
        root.AddSubview(_hintLabel);

        _statusLabel.Font = UIFont.SystemFontOfSize(13);
        _statusLabel.TextColor = UIColor.White;
        _statusLabel.BackgroundColor = UIColor.FromRGBA(0, 0, 0, 140);
        _statusLabel.TextAlignment = UITextAlignment.Center;
        _statusLabel.Lines = 0;
        _statusLabel.TranslatesAutoresizingMaskIntoConstraints = false;
        root.AddSubview(_statusLabel);

        var buttons = new UIStackView
        {
            Axis = UILayoutConstraintAxis.Horizontal,
            Spacing = 12,
            TranslatesAutoresizingMaskIntoConstraints = false,
        };
        buttons.AddArrangedSubview(CreateButton("打开文件夹", () => _ = _main.FolderVM.OpenFilePickerAsync()));
        buttons.AddArrangedSubview(CreateButton("设置", () => _main.OpenSettingWindow()));
        root.AddSubview(buttons);

        NSLayoutConstraint.ActivateConstraints(
        [
            _hintLabel.CenterXAnchor.ConstraintEqualTo(root.CenterXAnchor),
            _hintLabel.CenterYAnchor.ConstraintEqualTo(root.CenterYAnchor),
            _hintLabel.LeadingAnchor.ConstraintGreaterThanOrEqualTo(root.LeadingAnchor, 24),
            buttons.TopAnchor.ConstraintEqualTo(root.SafeAreaLayoutGuide.TopAnchor, 12),
            buttons.CenterXAnchor.ConstraintEqualTo(root.CenterXAnchor),
            _statusLabel.TopAnchor.ConstraintEqualTo(buttons.BottomAnchor, 8),
            _statusLabel.CenterXAnchor.ConstraintEqualTo(root.CenterXAnchor),
        ]);

        OnSessionChanged();
        UpdateStatus();
        BootTrace.Mark("native shell: photo view loaded");
    }

    /// <summary>释放会话与视图订阅（壳退出路径）。</summary>
    /// <param name="disposing">是否释放托管资源。</param>
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _session.Changed -= OnSessionChanged;
            _photo.ViewportChanged -= ReportViewport;
            _main.FolderVM.AllFilesChanged -= OnAllFilesChanged;
            _main.PropertyChanged -= OnMainPropertyChanged;
            if (_observedFile != null) _observedFile.PropertyChanged -= OnCurrentFileChanged;
            _photo.Dispose();
            _session.Dispose();
        }
        base.Dispose(disposing);
    }

    /// <summary>文件夹加载完成后刷新计数（共享层在 UI 线程调用，这里再兜一次线程）。</summary>
    private void OnAllFilesChanged() => UiDispatcher.Post(UpdateStatus);

    /// <summary>当前照片变更：切换会话文件、跟踪 EXIF 到达以更新对焦点并刷新状态文本。</summary>
    /// <param name="sender">事件源（主 VM）。</param>
    /// <param name="args">属性变化参数。</param>
    private void OnMainPropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName != nameof(MainViewModel.CurrentFile)) return;
        UiDispatcher.Post(() =>
        {
            if (_observedFile != null) _observedFile.PropertyChanged -= OnCurrentFileChanged;
            _observedFile = _main.CurrentFile;
            if (_observedFile != null) _observedFile.PropertyChanged += OnCurrentFileChanged;
            _session.SetFile(_main.CurrentFile?.File);
            UpdateStatus();
        });
    }

    /// <summary>当前文件 EXIF/星级就绪后同步对焦点与状态（对焦点供后续定位复用，本里程碑双击仍按点按位置）。</summary>
    /// <param name="sender">事件源（当前文件）。</param>
    /// <param name="args">属性变化参数。</param>
    private void OnCurrentFileChanged(object? sender, PropertyChangedEventArgs args) => UiDispatcher.Post(UpdateStatus);

    /// <summary>
    /// 视口首次有效后启动展示会话（先上报视口再发起解码：代理目标必须来自真实视口，
    /// 否则会话会把目标钳成 1 像素而解码出 1×1 的"合格代理"）。
    /// </summary>
    private void StartSession()
    {
        if (_sessionStarted || _photo.Bounds.Width < 1 || _photo.Bounds.Height < 1) return;
        _sessionStarted = true;
        ReportViewport();
        _session.SetFile(_main.CurrentFile?.File);
    }

    /// <summary>会话产物变化：产物直接交 Metal 上传（不建第二份主图缓冲），并刷新空态/错误提示。</summary>
    private void OnSessionChanged()
    {
        UpdateFocusPoint();
        _photo.SetImage(_session.Image);
        _hintLabel.Hidden = _session.Image != null;
        if (_session.Error.Length > 0) _statusLabel.Text = $"无法打开：{_session.Error}";
        else UpdateStatus();
    }

    /// <summary>EXIF 异步到达后同步对焦位置，避免主图先完成时对焦点仍是空值。</summary>
    private void UpdateFocusPoint()
    {
        var focus = _main.CurrentFile?.ExifData?.SonyFocusPosition;
        _photo.FocusPoint = focus is { ImageWidth: > 0, ImageHeight: > 0 } position
            ? new CGPoint(position.FocusX / (double)position.ImageWidth, position.FocusY / (double)position.ImageHeight) : null;
    }

    /// <summary>把主图物理视口与细节需求上报共享会话（代理目标 = 2× 物理视口，物理 100% 触发 Full 热替换）。</summary>
    private void ReportViewport()
        => _session.UpdateViewport(
            (double)_photo.Bounds.Width * _photo.TraitCollection.DisplayScale,
            (double)_photo.Bounds.Height * _photo.TraitCollection.DisplayScale,
            _photo.NeedsFull);

    /// <summary>把共享状态写进临时浮层：文件夹张数与当前文件名（代理档附带标记）。</summary>
    private void UpdateStatus()
    {
        var fileCount = _main.FolderVM.AllFiles.Count;
        var currentName = _main.CurrentFile?.Name ?? "无";
        _statusLabel.Text = $"文件夹：{fileCount} 张 · 当前：{currentName}{(_session.IsProxy ? " · 代理" : string.Empty)}";
    }

    /// <summary>创建临时浮层按钮（I3 换真实主屏控件后删除）。</summary>
    /// <param name="title">按钮标题。</param>
    /// <param name="onTap">点击回调。</param>
    /// <returns>配置完成的按钮。</returns>
    private static UIButton CreateButton(string title, Action onTap)
    {
        var button = UIButton.FromType(UIButtonType.System);
        button.SetTitle(title, UIControlState.Normal);
        button.TitleLabel!.Font = UIFont.SystemFontOfSize(15, UIFontWeight.Medium);
        button.TranslatesAutoresizingMaskIntoConstraints = false;
        button.TouchUpInside += (_, _) => onTap();
        button.HeightAnchor.ConstraintEqualTo(32).Active = true;
        return button;
    }
}
