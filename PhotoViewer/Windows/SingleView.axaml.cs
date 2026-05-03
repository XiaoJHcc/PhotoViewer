using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Platform;
using Avalonia.Interactivity;
using PhotoViewer.ViewModels;

namespace PhotoViewer.Windows;

public partial class SingleView : UserControl
{
    private TopLevel? _attachedTopLevel;
    private IInsetsManager? _insetsManager;

    public SingleView()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    /// <summary>
    /// 附加到可视树后绑定 InsetsManager，并立即应用移动端系统栏偏好。
    /// </summary>
    /// <param name="e">附加事件参数。</param>
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);

        _attachedTopLevel = TopLevel.GetTopLevel(this);
        _insetsManager = _attachedTopLevel?.InsetsManager;

        if (_insetsManager != null)
        {
            _insetsManager.SafeAreaChanged += OnSafeAreaChanged;
        }

        ApplyMobileInsetsPreferences();
    }

    /// <summary>
    /// 从可视树移除时注销 InsetsManager 事件，避免重复订阅。
    /// </summary>
    /// <param name="e">分离事件参数。</param>
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        if (_insetsManager != null)
        {
            _insetsManager.SafeAreaChanged -= OnSafeAreaChanged;
            _insetsManager = null;
        }

        _attachedTopLevel = null;
        base.OnDetachedFromVisualTree(e);
    }

    /// <summary>
    /// 在控件首次完成加载后再次应用移动端系统栏偏好，覆盖 iOS 场景激活后的状态栏回弹。
    /// </summary>
    private void OnLoaded(object? sender, RoutedEventArgs e)
    {
        ApplyMobileInsetsPreferences();
    }

    /// <summary>
    /// 在安全区变化后重申 edge-to-edge 与隐藏状态栏设置，确保旋转和 iPad 场景切换后仍保持全屏。
    /// </summary>
    private void OnSafeAreaChanged(object? sender, SafeAreaChangedArgs e)
    {
        ApplyMobileInsetsPreferences();
    }

    /// <summary>
    /// 统一应用移动端全屏渲染和隐藏系统状态栏设置。
    /// </summary>
    private void ApplyMobileInsetsPreferences()
    {
        if (_attachedTopLevel?.InsetsManager is not { } insetsManager)
        {
            return;
        }

        insetsManager.DisplayEdgeToEdgePreference = true;
        insetsManager.IsSystemBarVisible = false;
    }
}