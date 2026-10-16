using PhotoViewer.Core.Platform;
using PhotoViewer.ViewModels.Main;
using System.ComponentModel;
using UIKit;

namespace PhotoViewer.iOS.Native;

/// <summary>
/// I1 占位主屏（脚手架，I2/I3 由真实主屏替换）：深色底 + 状态文本 + 打开文件夹/设置入口。
/// 只读共享 <see cref="MainViewModel"/> 的状态，不承载任何主图渲染。
/// </summary>
internal sealed class iOSNativeRootViewController : UIViewController
{
    private readonly MainViewModel _main;
    private readonly UILabel _statusLabel = new();
    private bool _subscribed;

    /// <summary>用共享主 VM 构建占位主屏。</summary>
    /// <param name="main">共享主 VM。</param>
    public iOSNativeRootViewController(MainViewModel main)
    {
        _main = main;
    }

    /// <summary>搭建深色背景、状态文本与占位按钮。</summary>
    public override void ViewDidLoad()
    {
        base.ViewDidLoad();

        var root = View!;
        root.BackgroundColor = UIColor.FromRGB(18, 18, 18);

        var titleLabel = new UILabel
        {
            Text = "PhotoViewer（iOS 原生壳 I1 占位）",
            Font = UIFont.SystemFontOfSize(20, UIFontWeight.Semibold),
            TextColor = UIColor.White,
            TextAlignment = UITextAlignment.Center,
            Lines = 0,
            TranslatesAutoresizingMaskIntoConstraints = false,
        };

        _statusLabel.Font = UIFont.SystemFontOfSize(16);
        _statusLabel.TextColor = UIColor.LightGray;
        _statusLabel.TextAlignment = UITextAlignment.Center;
        _statusLabel.Lines = 0;
        _statusLabel.TranslatesAutoresizingMaskIntoConstraints = false;

        var stack = new UIStackView
        {
            Axis = UILayoutConstraintAxis.Vertical,
            Alignment = UIStackViewAlignment.Center,
            Spacing = 20,
            TranslatesAutoresizingMaskIntoConstraints = false,
        };
        stack.AddArrangedSubview(titleLabel);
        stack.AddArrangedSubview(_statusLabel);
        stack.AddArrangedSubview(CreateButton("打开文件夹", onTap: () => _ = _main.FolderVM.OpenFilePickerAsync()));
        stack.AddArrangedSubview(CreateButton("设置", onTap: () => _main.OpenSettingWindow()));

        root.AddSubview(stack);
        NSLayoutConstraint.ActivateConstraints(
        [
            stack.CenterXAnchor.ConstraintEqualTo(root.CenterXAnchor),
            stack.CenterYAnchor.ConstraintEqualTo(root.CenterYAnchor),
            stack.LeadingAnchor.ConstraintGreaterThanOrEqualTo(root.SafeAreaLayoutGuide.LeadingAnchor, 24),
            stack.TrailingAnchor.ConstraintLessThanOrEqualTo(root.SafeAreaLayoutGuide.TrailingAnchor, -24),
        ]);

        BootTrace.Mark("native shell: placeholder root view loaded");
    }

    /// <summary>进入前台时订阅共享状态（占位屏通常不消失，被全屏内容覆盖时按 UIKit 生命周期重订阅）。</summary>
    /// <param name="animated">系统动画标记。</param>
    public override void ViewWillAppear(bool animated)
    {
        base.ViewWillAppear(animated);
        if (_subscribed)
        {
            return;
        }

        _subscribed = true;
        _main.FolderVM.AllFilesChanged += OnAllFilesChanged;
        _main.PropertyChanged += OnMainPropertyChanged;
        UpdateStatus();
    }

    /// <summary>离开前台时解除订阅，避免隐藏期间无谓刷新。</summary>
    /// <param name="animated">系统动画标记。</param>
    public override void ViewDidDisappear(bool animated)
    {
        base.ViewDidDisappear(animated);
        if (!_subscribed)
        {
            return;
        }

        _subscribed = false;
        _main.FolderVM.AllFilesChanged -= OnAllFilesChanged;
        _main.PropertyChanged -= OnMainPropertyChanged;
    }

    /// <summary>文件夹加载完成后刷新计数（共享层在 UI 线程调用，这里再兜一次线程）。</summary>
    private void OnAllFilesChanged() => UiDispatcher.Post(UpdateStatus);

    /// <summary>主图切换后刷新状态文本。</summary>
    /// <param name="sender">事件源（主 VM）。</param>
    /// <param name="args">属性变化参数。</param>
    private void OnMainPropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(MainViewModel.CurrentFile))
        {
            UiDispatcher.Post(UpdateStatus);
        }
    }

    /// <summary>把共享状态写进占位文本：当前文件名与文件夹张数。</summary>
    private void UpdateStatus()
    {
        var fileCount = _main.FolderVM.AllFiles.Count;
        var currentName = _main.CurrentFile?.Name ?? "无";
        _statusLabel.Text = $"文件夹：{fileCount} 张\n当前：{currentName}";
    }

    /// <summary>创建占位主屏的系统风格按钮。</summary>
    /// <param name="title">按钮标题。</param>
    /// <param name="onTap">点击回调。</param>
    /// <returns>配置完成的按钮。</returns>
    private static UIButton CreateButton(string title, Action onTap)
    {
        var button = UIButton.FromType(UIButtonType.System);
        button.SetTitle(title, UIControlState.Normal);
        button.TitleLabel!.Font = UIFont.SystemFontOfSize(17, UIFontWeight.Medium);
        button.TranslatesAutoresizingMaskIntoConstraints = false;
        button.TouchUpInside += (_, _) => onTap();
        button.WidthAnchor.ConstraintEqualTo(220).Active = true;
        button.HeightAnchor.ConstraintEqualTo(44).Active = true;
        return button;
    }
}
