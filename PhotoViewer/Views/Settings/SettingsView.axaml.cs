using Avalonia.Controls;
using Avalonia.Controls.Platform;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia;
using Avalonia.VisualTree;
using PhotoViewer.Controls;

namespace PhotoViewer.Views;

public partial class SettingsView : UserControl
{
    private const double MobileBottomSpacerBaselineHeight = 50;
    private const double KeyboardGapHeight = 20;
    private const double ScrollTopPadding = 12;

    private Border[] _bottomSpacers = [];
    private TopLevel? _attachedTopLevel;
    private ScrollViewer? _keyboardAvoidanceScrollViewer;
    private Vector? _keyboardAvoidanceRestoreOffset;

    /// <summary>
    /// 初始化设置页，并注册移动端键盘避让所需的焦点事件。
    /// </summary>
    public SettingsView()
    {
        InitializeComponent();

        _bottomSpacers =
        [
            FileSettingsBottomSpacer,
            ImageSettingsBottomSpacer,
            ControlSettingsBottomSpacer,
            ExifSettingsBottomSpacer,
        ];

        SetBottomSpacerHeight(GetCollapsedBottomSpacerHeight());
        ConfigureIosScrollViewerFocusBehavior();
        AddHandler(InputElement.GotFocusEvent, OnInputGotFocus, RoutingStrategies.Bubble);
        AddHandler(InputElement.LostFocusEvent, OnInputLostFocus, RoutingStrategies.Bubble);
    }

    /// <summary>
    /// 在 iOS 设置页关闭 ScrollViewer 的自动聚焦滚动，避免与手动键盘避让逻辑叠加后触发错误定位。
    /// </summary>
    private void ConfigureIosScrollViewerFocusBehavior()
    {
        if (!OperatingSystem.IsIOS())
        {
            return;
        }

        FileSettingsScrollViewer.SetValue(ScrollViewer.BringIntoViewOnFocusChangeProperty, false);
        ImageSettingsScrollViewer.SetValue(ScrollViewer.BringIntoViewOnFocusChangeProperty, false);
        ControlSettingsScrollViewer.SetValue(ScrollViewer.BringIntoViewOnFocusChangeProperty, false);
        ExifSettingsScrollViewer.SetValue(ScrollViewer.BringIntoViewOnFocusChangeProperty, false);
    }

    /// <summary>
    /// 附加到可视树后，监听移动端输入面板状态变化。
    /// </summary>
    /// <param name="e">附加事件参数。</param>
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);

        _attachedTopLevel = TopLevel.GetTopLevel(this);
        if (IsMobilePlatform())
        {
            if (_attachedTopLevel?.InsetsManager is { } insetsManager)
            {
                insetsManager.SafeAreaChanged += OnSafeAreaChanged;
            }

            _attachedTopLevel?.InputPane?.StateChanged += OnInputPaneStateChanged;
            SetBottomSpacerHeight(GetBottomSpacerHeight(GetCurrentInputPaneHeight()));
            Dispatcher.Post(() => SetBottomSpacerHeight(GetBottomSpacerHeight(GetCurrentInputPaneHeight())), DispatcherPriority.Loaded);
        }
    }

    /// <summary>
    /// 从可视树移除时取消输入面板状态监听。
    /// </summary>
    /// <param name="e">分离事件参数。</param>
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        if (_attachedTopLevel?.InsetsManager is { } insetsManager)
        {
            insetsManager.SafeAreaChanged -= OnSafeAreaChanged;
        }

        if (_attachedTopLevel?.InputPane is { } inputPane)
        {
            inputPane.StateChanged -= OnInputPaneStateChanged;
        }

        RestoreKeyboardAvoidanceLayout();
        _attachedTopLevel = null;
        base.OnDetachedFromVisualTree(e);
    }

    /// <summary>
    /// 处理输入框获得焦点事件，在移动端为数字输入框启用键盘避让。
    /// </summary>
    /// <param name="sender">事件发送方。</param>
    /// <param name="e">焦点事件参数。</param>
    private void OnInputGotFocus(object? sender, FocusChangedEventArgs e)
    {
        if (!IsMobilePlatform() || e.Source is not DeferredNumericTextBox textBox)
        {
            return;
        }

        var inputPaneHeight = GetCurrentInputPaneHeight();
        if (inputPaneHeight > 0)
        {
            ActivateKeyboardAvoidance(textBox, inputPaneHeight);
        }
    }

    /// <summary>
    /// 处理输入框失焦事件，在焦点切换完成后恢复或保持底部占位。
    /// </summary>
    /// <param name="sender">事件发送方。</param>
    /// <param name="e">路由事件参数。</param>
    private void OnInputLostFocus(object? sender, FocusChangedEventArgs e)
    {
        if (!IsMobilePlatform() || e.Source is not DeferredNumericTextBox)
        {
            return;
        }

        Dispatcher.Post(() => UpdateKeyboardAvoidanceFromFocusedElement(GetCurrentInputPaneHeight()), DispatcherPriority.Background);
    }

    /// <summary>
    /// 根据当前焦点状态更新底部占位高度。
    /// </summary>
    private void UpdateKeyboardAvoidanceFromFocusedElement(double inputPaneHeight)
    {
        if (inputPaneHeight <= 0)
        {
            RestoreKeyboardAvoidanceLayout();
            return;
        }

        var focusedElement = _attachedTopLevel?.FocusManager?.GetFocusedElement();
        if (focusedElement is DeferredNumericTextBox textBox && textBox.FindAncestorOfType<SettingsView>() == this)
        {
            ActivateKeyboardAvoidance(textBox, inputPaneHeight);
            return;
        }

        RestoreKeyboardAvoidanceLayout();
    }

    /// <summary>
    /// 在系统输入面板关闭时恢复默认底部占位。
    /// </summary>
    /// <param name="sender">输入面板。</param>
    /// <param name="e">状态变化参数。</param>
    private void OnInputPaneStateChanged(object? sender, InputPaneStateEventArgs e)
    {
        var inputPaneHeight = Math.Max(0, e.EndRect.Height);
        Dispatcher.Post(() => UpdateKeyboardAvoidanceFromFocusedElement(inputPaneHeight), DispatcherPriority.Background);
    }

    /// <summary>
    /// 在安全区变化时刷新设置页底部占位，确保初始态和旋转后的底部留白正确。
    /// </summary>
    private void OnSafeAreaChanged(object? sender, SafeAreaChangedArgs e)
    {
        Dispatcher.Post(() => UpdateKeyboardAvoidanceFromFocusedElement(GetCurrentInputPaneHeight()), DispatcherPriority.Background);
    }

    /// <summary>
    /// 激活底部占位并将目标输入框滚动到键盘安全区以上。
    /// </summary>
    /// <param name="textBox">当前聚焦的数字输入框。</param>
    private void ActivateKeyboardAvoidance(DeferredNumericTextBox textBox, double inputPaneHeight)
    {
        SetBottomSpacerHeight(GetBottomSpacerHeight(inputPaneHeight));
        if (inputPaneHeight > 0)
        {
            Dispatcher.Post(() => ScrollTextBoxAboveKeyboard(textBox, inputPaneHeight), DispatcherPriority.Loaded);
        }
    }

    /// <summary>
    /// 将目标输入框滚动到键盘遮挡区上方，并尽量保留少量可读间距。
    /// </summary>
    /// <param name="textBox">当前聚焦的数字输入框。</param>
    private void ScrollTextBoxAboveKeyboard(DeferredNumericTextBox textBox, double inputPaneHeight)
    {
        var scrollViewer = textBox.FindAncestorOfType<ScrollViewer>();
        if (scrollViewer is null)
        {
            return;
        }

        CaptureKeyboardAvoidanceScrollOffset(scrollViewer);

        var topLeft = textBox.TranslatePoint(new Point(0, 0), scrollViewer);
        if (topLeft is null)
        {
            return;
        }

        var currentOffset = scrollViewer.Offset;
        var viewportHeight = scrollViewer.Viewport.Height;
        if (viewportHeight <= 0)
        {
            return;
        }

        var visibleBottom = Math.Max(ScrollTopPadding, viewportHeight - inputPaneHeight - KeyboardGapHeight);
        var textBoxTop = topLeft.Value.Y;
        var textBoxBottom = textBoxTop + textBox.Bounds.Height;
        var targetOffsetY = currentOffset.Y;

        if (textBoxBottom > visibleBottom)
        {
            targetOffsetY += textBoxBottom - visibleBottom;
        }

        if (textBoxTop < ScrollTopPadding)
        {
            targetOffsetY += textBoxTop - ScrollTopPadding;
        }

        targetOffsetY = Math.Max(0, targetOffsetY);
        if (Math.Abs(targetOffsetY - currentOffset.Y) < 0.5)
        {
            return;
        }

        scrollViewer.Offset = new Vector(currentOffset.X, targetOffsetY);
    }

    /// <summary>
    /// 恢复键盘收起后的底部占位和滚动位置。
    /// </summary>
    private void RestoreKeyboardAvoidanceLayout()
    {
        SetBottomSpacerHeight(GetCollapsedBottomSpacerHeight());
        RestoreKeyboardAvoidanceScrollOffset();
    }

    /// <summary>
    /// 记录当前滚动容器在键盘避让开始前的位置，便于关闭键盘后还原。
    /// </summary>
    /// <param name="scrollViewer">当前使用的滚动容器。</param>
    private void CaptureKeyboardAvoidanceScrollOffset(ScrollViewer scrollViewer)
    {
        if (_keyboardAvoidanceScrollViewer == scrollViewer)
        {
            return;
        }

        RestoreKeyboardAvoidanceScrollOffset();
        _keyboardAvoidanceScrollViewer = scrollViewer;
        _keyboardAvoidanceRestoreOffset = scrollViewer.Offset;
    }

    /// <summary>
    /// 将滚动容器恢复到键盘避让之前的位置。
    /// </summary>
    private void RestoreKeyboardAvoidanceScrollOffset()
    {
        if (_keyboardAvoidanceScrollViewer is not { } scrollViewer || _keyboardAvoidanceRestoreOffset is not { } restoreOffset)
        {
            _keyboardAvoidanceScrollViewer = null;
            _keyboardAvoidanceRestoreOffset = null;
            return;
        }

        var maxOffsetX = Math.Max(0, scrollViewer.Extent.Width - scrollViewer.Viewport.Width);
        var maxOffsetY = Math.Max(0, scrollViewer.Extent.Height - scrollViewer.Viewport.Height);
        var restoredOffset = new Vector(
            Math.Clamp(restoreOffset.X, 0, maxOffsetX),
            Math.Clamp(restoreOffset.Y, 0, maxOffsetY));

        scrollViewer.Offset = restoredOffset;
        _keyboardAvoidanceScrollViewer = null;
        _keyboardAvoidanceRestoreOffset = null;
    }

    /// <summary>
    /// 批量设置各设置页底部占位高度。
    /// </summary>
    /// <param name="height">新的底部占位高度。</param>
    private void SetBottomSpacerHeight(double height)
    {
        foreach (var spacer in _bottomSpacers)
        {
            spacer.Height = height;
        }
    }

    /// <summary>
    /// 获取当前输入面板实际遮挡高度。
    /// </summary>
    private double GetCurrentInputPaneHeight()
        => Math.Max(0, _attachedTopLevel?.InputPane?.OccludedRect.Height ?? 0);

    /// <summary>
    /// 获取当前移动端底部安全区高度。
    /// </summary>
    private double GetCurrentSafeAreaBottomHeight()
        => IsMobilePlatform() ? Math.Max(0, _attachedTopLevel?.InsetsManager?.SafeAreaPadding.Bottom ?? 0) : 0;

    /// <summary>
    /// 根据当前输入面板高度计算应保留的底部占位。
    /// </summary>
    private double GetBottomSpacerHeight(double inputPaneHeight)
        => inputPaneHeight > 0 ? inputPaneHeight : GetCollapsedBottomSpacerHeight();

    /// <summary>
    /// 获取收起状态下的底部占位高度。
    /// </summary>
    /// <returns>移动端安全区占位高度；非移动端返回 0。</returns>
    private double GetCollapsedBottomSpacerHeight()
        => IsMobilePlatform()
            ? Math.Max(MobileBottomSpacerBaselineHeight, GetCurrentSafeAreaBottomHeight())
            : 0;

    /// <summary>
    /// 判断当前是否为移动端平台。
    /// </summary>
    /// <returns>Android 或 iOS 返回 true。</returns>
    private static bool IsMobilePlatform()
        => OperatingSystem.IsAndroid() || OperatingSystem.IsIOS();
}
