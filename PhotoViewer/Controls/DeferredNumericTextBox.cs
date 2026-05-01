using System;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.TextInput;
using Avalonia.Interactivity;

namespace PhotoViewer.Controls;

/// <summary>
/// 提供延迟提交的整数输入框，避免用户输入到一半时被范围裁剪打断。
/// </summary>
public class DeferredNumericTextBox : TextBox
{
    private const double TouchFocusDragThreshold = 10;

    /// <summary>
    /// 复用 TextBox 默认样式模板，避免自定义派生控件丢失可视外观。
    /// </summary>
    protected override Type StyleKeyOverride => typeof(TextBox);

    /// <summary>绑定整数值。</summary>
    public static readonly StyledProperty<int> ValueProperty =
        AvaloniaProperty.Register<DeferredNumericTextBox, int>(nameof(Value), defaultBindingMode: Avalonia.Data.BindingMode.TwoWay);

    /// <summary>允许输入的最小值。</summary>
    public static readonly StyledProperty<int> MinimumProperty =
        AvaloniaProperty.Register<DeferredNumericTextBox, int>(nameof(Minimum), 0);

    /// <summary>允许输入的最大值。</summary>
    public static readonly StyledProperty<int> MaximumProperty =
        AvaloniaProperty.Register<DeferredNumericTextBox, int>(nameof(Maximum), int.MaxValue);

    private bool _isSynchronizingText;
    private bool _isPendingTouchFocus;
    private bool _touchMovedBeyondThreshold;
    private Point _touchStartPoint;

    /// <summary>
    /// 当前触摸手势是否仍在等待确认点击，用于阻止按下瞬间的误聚焦副作用。
    /// </summary>
    public bool IsWaitingForTouchFocusConfirmation => _isPendingTouchFocus;

    /// <summary>
    /// 获取或设置绑定整数值。
    /// </summary>
    public int Value
    {
        get => GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    /// <summary>
    /// 获取或设置允许输入的最小值。
    /// </summary>
    public int Minimum
    {
        get => GetValue(MinimumProperty);
        set => SetValue(MinimumProperty, value);
    }

    /// <summary>
    /// 获取或设置允许输入的最大值。
    /// </summary>
    public int Maximum
    {
        get => GetValue(MaximumProperty);
        set => SetValue(MaximumProperty, value);
    }

    /// <summary>
    /// 初始化控件并同步初始文本。
    /// </summary>
    public DeferredNumericTextBox()
    {
        SyncTextFromValue();
        UpdateMobileFocusableState();

        AddHandler(InputElement.PointerPressedEvent, OnPointerPressedTunnel, RoutingStrategies.Tunnel, handledEventsToo: true);
    }

    /// <summary>
    /// 在属性变化时保持显示文本与绑定值同步。
    /// </summary>
    /// <param name="change">属性变化参数。</param>
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == ValueProperty || change.Property == MinimumProperty || change.Property == MaximumProperty)
        {
            CoerceValueIntoRange();
            if (!IsFocused)
            {
                SyncTextFromValue();
            }
        }
    }

    /// <summary>
    /// 附加到可视树后为移动端设置数字键盘选项。
    /// </summary>
    /// <param name="e">附加事件参数。</param>
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        ApplyMobileTextInputOptions();
        UpdateMobileFocusableState();
        SyncTextFromValue();
    }

    /// <summary>
    /// 聚焦时选中全部文本，便于直接改写数值。
    /// </summary>
    /// <param name="e">焦点事件参数。</param>
    protected override void OnGotFocus(FocusChangedEventArgs e)
    {
        base.OnGotFocus(e);

        if (_isPendingTouchFocus)
        {
            DismissKeyboardFocus();
            return;
        }

        SelectAll();
    }

    /// <summary>
    /// 在触摸移动超过阈值后取消待定聚焦，将该手势视为页面滚动。
    /// </summary>
    /// <param name="e">指针移动事件参数。</param>
    protected override void OnPointerMoved(PointerEventArgs e)
    {
        if (_isPendingTouchFocus)
        {
            var currentPoint = e.GetPosition(this);
            var deltaX = currentPoint.X - _touchStartPoint.X;
            var deltaY = currentPoint.Y - _touchStartPoint.Y;
            var distanceSquared = deltaX * deltaX + deltaY * deltaY;
            if (distanceSquared > TouchFocusDragThreshold * TouchFocusDragThreshold)
            {
                _touchMovedBeyondThreshold = true;
                if (IsFocused)
                {
                    DismissKeyboardFocus();
                }
            }
        }

        base.OnPointerMoved(e);
    }

    /// <summary>
    /// 仅在触摸手势被判定为点击时执行聚焦；滑动则保持不聚焦。
    /// </summary>
    /// <param name="e">指针释放事件参数。</param>
    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        if (_isPendingTouchFocus)
        {
            var shouldFocus = !_touchMovedBeyondThreshold && IsPointInside(e.GetPosition(this));
            ResetPendingTouchFocus();

            if (shouldFocus)
            {
                Focusable = true;
                Focus();
                e.Handled = true;
                return;
            }

            if (IsFocused)
            {
                DismissKeyboardFocus();
            }
        }

        base.OnPointerReleased(e);
    }

    /// <summary>
    /// 失焦时提交编辑结果，并恢复为合法范围内的最终值。
    /// </summary>
    /// <param name="e">路由事件参数。</param>
    protected override void OnLostFocus(FocusChangedEventArgs e)
    {
        CommitText();
        base.OnLostFocus(e);
        UpdateMobileFocusableState();
    }

    /// <summary>
    /// 在按下回车时提交编辑结果。
    /// </summary>
    /// <param name="e">键盘事件参数。</param>
    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            CommitText();
            DismissKeyboardFocus();
            e.Handled = true;
        }

        base.OnKeyDown(e);
    }

    /// <summary>
    /// 将当前文本提交为整数值；无效输入会回退到上次有效值。
    /// </summary>
    private void CommitText()
    {
        if (_isSynchronizingText)
        {
            return;
        }

        var rawText = Text?.Trim();
        if (string.IsNullOrEmpty(rawText))
        {
            SyncTextFromValue();
            return;
        }

        if (!int.TryParse(rawText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedValue))
        {
            SyncTextFromValue();
            return;
        }

        var coercedValue = Math.Clamp(parsedValue, Math.Min(Minimum, Maximum), Math.Max(Minimum, Maximum));
        if (Value != coercedValue)
        {
            Value = coercedValue;
        }

        SyncTextFromValue();
    }

    /// <summary>
    /// 确保绑定值始终位于当前上下限范围内。
    /// </summary>
    private void CoerceValueIntoRange()
    {
        var minimum = Math.Min(Minimum, Maximum);
        var maximum = Math.Max(Minimum, Maximum);
        var coercedValue = Math.Clamp(Value, minimum, maximum);
        if (Value != coercedValue)
        {
            Value = coercedValue;
        }
    }

    /// <summary>
    /// 使用当前整数值刷新文本显示。
    /// </summary>
    private void SyncTextFromValue()
    {
        _isSynchronizingText = true;
        Text = Value.ToString(CultureInfo.InvariantCulture);
        _isSynchronizingText = false;
    }

    /// <summary>
    /// 为移动端输入法设置数字键盘和完成键。
    /// </summary>
    private void ApplyMobileTextInputOptions()
    {
        if (!OperatingSystem.IsAndroid() && !OperatingSystem.IsIOS())
        {
            return;
        }

        TextInputOptions.SetContentType(this, TextInputContentType.Digits);
        TextInputOptions.SetReturnKeyType(this, TextInputReturnKeyType.Done);
        TextInputOptions.SetMultiline(this, false);
        TextInputOptions.SetAutoCapitalization(this, false);
    }

    /// <summary>
    /// 在移动端完成编辑后清除焦点，以便收起键盘并触发页面恢复布局。
    /// </summary>
    private void DismissKeyboardFocus()
    {
        if (!OperatingSystem.IsAndroid() && !OperatingSystem.IsIOS())
        {
            return;
        }

        TopLevel.GetTopLevel(this)?.FocusManager?.Focus(null);
        UpdateMobileFocusableState();
    }

    /// <summary>
    /// 在隧道阶段拦截移动端触摸按下，阻止 TextBox 默认在 touch-down 时聚焦。
    /// </summary>
    /// <param name="sender">事件发送方。</param>
    /// <param name="e">指针按下事件参数。</param>
    private void OnPointerPressedTunnel(object? sender, PointerPressedEventArgs e)
    {
        if (!ShouldDeferTouchFocus(e))
        {
            return;
        }

        _isPendingTouchFocus = true;
        _touchMovedBeyondThreshold = false;
        _touchStartPoint = e.GetPosition(this);
        e.Handled = true;
    }

    /// <summary>
    /// 判断当前指针按下是否需要延迟到抬起后再聚焦。
    /// </summary>
    /// <param name="e">指针按下事件参数。</param>
    /// <returns>仅移动端触摸且当前未聚焦时返回 true。</returns>
    private bool ShouldDeferTouchFocus(PointerPressedEventArgs e)
        => (OperatingSystem.IsAndroid() || OperatingSystem.IsIOS())
           && !IsFocused
           && e.Pointer.Type == PointerType.Touch;

    /// <summary>
    /// 在移动端仅在编辑态允许控件参与焦点竞争，避免触摸按下瞬间被系统默认聚焦。
    /// </summary>
    private void UpdateMobileFocusableState()
    {
        if (!OperatingSystem.IsAndroid() && !OperatingSystem.IsIOS())
        {
            return;
        }

        Focusable = IsFocused;
    }

    /// <summary>
    /// 判断给定坐标是否仍位于输入框内部。
    /// </summary>
    /// <param name="point">相对于当前控件的点。</param>
    /// <returns>位于控件边界内返回 true。</returns>
    private bool IsPointInside(Point point)
        => point.X >= 0 && point.Y >= 0 && point.X <= Bounds.Width && point.Y <= Bounds.Height;

    /// <summary>
    /// 清理待定触摸聚焦状态。
    /// </summary>
    private void ResetPendingTouchFocus()
    {
        _isPendingTouchFocus = false;
        _touchMovedBeyondThreshold = false;
    }
}