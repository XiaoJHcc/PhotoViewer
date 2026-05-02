using System;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;

namespace PhotoViewer.Controls;

/// <summary>
/// 提供延迟提交的整数输入框，避免用户输入到一半时被范围裁剪打断。
/// </summary>
public class DeferredNumericTextBox : TextBox
{
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
    /// 附加到可视树后同步一次当前文本显示。
    /// </summary>
    /// <param name="e">附加事件参数。</param>
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        SyncTextFromValue();
    }

    /// <summary>
    /// 聚焦时选中全部文本，便于直接改写数值。
    /// </summary>
    /// <param name="e">焦点事件参数。</param>
    protected override void OnGotFocus(FocusChangedEventArgs e)
    {
        base.OnGotFocus(e);

        SelectAll();
    }

    /// <summary>
    /// 失焦时提交编辑结果，并恢复为合法范围内的最终值。
    /// </summary>
    /// <param name="e">路由事件参数。</param>
    protected override void OnLostFocus(FocusChangedEventArgs e)
    {
        CommitText();
        base.OnLostFocus(e);
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
    /// 在移动端完成编辑后清除焦点，以便收起键盘并触发页面恢复布局。
    /// </summary>
    private void DismissKeyboardFocus()
    {
        if (!OperatingSystem.IsAndroid() && !OperatingSystem.IsIOS())
        {
            return;
        }

        TopLevel.GetTopLevel(this)?.FocusManager?.Focus(null);
    }
}