using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using PhotoViewer.ViewModels.Settings;

namespace PhotoViewer.Desktop.Native;

/// <summary>
/// 设置窗口五页的构建与订阅。页面一次建好、切换 Visibility;每页自带滚动,
/// 行原语与 token 统一走 <see cref="WindowsSettingsCard"/> / <see cref="WindowsLayout"/>。
/// </summary>
internal sealed partial class WindowsSettingsPages
{
    private readonly SettingsViewModel _settings;
    private readonly WindowsHotkeyRecorder _recorder;
    private readonly Window _window;
    private readonly List<Action> _detach = [];
    private readonly List<UIElement> _pages = [];

    /// <summary>构建五页。</summary>
    /// <param name="settings">设置工作副本</param>
    /// <param name="recorder">快捷键录制器</param>
    /// <param name="window">宿主窗口(取 XamlRoot 给 ContentDialog)</param>
    public WindowsSettingsPages(SettingsViewModel settings, WindowsHotkeyRecorder recorder, Window window)
    {
        _settings = settings;
        _recorder = recorder;
        _window = window;

        _pages.Add(BuildFilePage());
        _pages.Add(BuildPreviewPage());
        _pages.Add(BuildControlPage());
        _pages.Add(BuildExifPage());
        _pages.Add(BuildAiPage());
    }

    /// <summary>五页内容,顺序与导航项一致。</summary>
    public IReadOnlyList<UIElement> Pages => _pages;

    /// <summary>退订设置/集合订阅。</summary>
    public void Detach()
    {
        foreach (var action in _detach)
        {
            action();
        }

        _detach.Clear();
    }

    /// <summary>构建一页:页面标题 + 内容,自带滚动。</summary>
    /// <param name="title">页面标题</param>
    /// <param name="children">页面内容</param>
    /// <returns>滚动容器</returns>
    private static ScrollViewer Page(string title, params UIElement[] children)
        => WindowsSettingsCard.Page(title, children);

    /// <summary>一组卡片(卡间距 4)。</summary>
    /// <returns>容器</returns>
    private static StackPanel CardGroup() => new() { Spacing = WindowsLayout.SettingsCardGap };

    /// <summary>开关;初始化值不触发回调。</summary>
    /// <param name="value">初始值</param>
    /// <param name="changed">变化回调</param>
    /// <returns>开关</returns>
    private static ToggleSwitch NewToggle(bool value, Action<bool> changed)
    {
        var toggle = new ToggleSwitch
        {
            IsOn = value,
            OnContent = string.Empty,
            OffContent = string.Empty,
            MinWidth = 0,
        };
        toggle.Toggled += (_, _) => changed(toggle.IsOn);
        return toggle;
    }

    /// <summary>拉条。</summary>
    /// <param name="minimum">最小值</param>
    /// <param name="maximum">最大值</param>
    /// <param name="step">步进</param>
    /// <param name="value">初始值</param>
    /// <param name="changed">变化回调</param>
    /// <returns>拉条</returns>
    private static Slider NewSlider(double minimum, double maximum, double step, double value, Action<double> changed)
    {
        var slider = new Slider
        {
            Minimum = minimum,
            Maximum = maximum,
            StepFrequency = step,
            Value = Math.Clamp(value, minimum, maximum),
            VerticalAlignment = VerticalAlignment.Center,
        };
        slider.ValueChanged += (_, args) => changed(args.NewValue);
        return slider;
    }

    /// <summary>文本按钮。</summary>
    /// <param name="text">按钮文本</param>
    /// <param name="action">点击动作</param>
    /// <returns>按钮</returns>
    private static Button NewButton(string text, Action action)
    {
        var button = new Button
        {
            Content = text,
            VerticalAlignment = VerticalAlignment.Center,
        };
        button.Click += (_, _) => action();
        return button;
    }

    /// <summary>数值输入框(整数或小数),提交时写回。</summary>
    /// <param name="value">初始值</param>
    /// <param name="minimum">最小值</param>
    /// <param name="maximum">最大值</param>
    /// <param name="step">步进</param>
    /// <param name="changed">变化回调</param>
    /// <returns>数值框</returns>
    private static NumberBox NewNumberBox(
        double value, double minimum, double maximum, double step, Action<double> changed)
    {
        var box = new NumberBox
        {
            Value = Math.Clamp(value, minimum, maximum),
            Minimum = minimum,
            Maximum = maximum,
            SmallChange = step,
            LargeChange = step * 5,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline,
            Width = WindowsLayout.SettingsNumberBoxWidth,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
        };
        box.ValueChanged += (_, args) =>
        {
            if (!double.IsNaN(args.NewValue))
            {
                changed(args.NewValue);
            }
        };
        return box;
    }

    /// <summary>给数值框加单位文本(如“线程”“张”)。</summary>
    /// <param name="box">数值框</param>
    /// <param name="unit">单位</param>
    /// <returns>水平排列的控件</returns>
    private static StackPanel NumberBoxWithUnit(NumberBox box, string unit)
    {
        var panel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            VerticalAlignment = VerticalAlignment.Center,
        };
        panel.Children.Add(box);
        panel.Children.Add(new TextBlock
        {
            Text = unit,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = WindowsSettingsCard.ThemeBrush("TextFillColorSecondaryBrush"),
        });
        return panel;
    }

    /// <summary>订阅设置属性变化,窗口关闭时自动退订。</summary>
    /// <param name="propertyName">属性名</param>
    /// <param name="action">回调</param>
    private void OnSetting(string propertyName, Action action)
    {
        PropertyChangedEventHandler handler = (_, args) =>
        {
            if (args.PropertyName == propertyName)
            {
                action();
            }
        };
        _settings.PropertyChanged += handler;
        _detach.Add(() => _settings.PropertyChanged -= handler);
    }

    /// <summary>当前窗口内容的 XamlRoot(ContentDialog 必需)。</summary>
    private XamlRoot? Root => _window.Content?.XamlRoot;

    /// <summary>数值转显示文本(与设置 JSON 同口径)。</summary>
    /// <param name="value">数值</param>
    /// <returns>文本</returns>
    private static string FormatNumber(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);

    /// <summary>线性值 → 指数滑条位置(0~1)。</summary>
    /// <param name="value">值</param>
    /// <param name="minimum">下限</param>
    /// <param name="maximum">上限</param>
    /// <returns>滑条位置</returns>
    private static double ToExp(double value, double minimum, double maximum)
    {
        value = Math.Clamp(value, minimum, maximum);
        var denominator = Math.Log(maximum / minimum, 2);
        return denominator <= 0
            ? 0
            : Math.Clamp(Math.Log(value / minimum, 2) / denominator, 0, 1);
    }

    /// <summary>指数滑条位置(0~1)→ 线性值。</summary>
    /// <param name="position">滑条位置</param>
    /// <param name="minimum">下限</param>
    /// <param name="maximum">上限</param>
    /// <returns>值</returns>
    private static double FromExp(double position, double minimum, double maximum) =>
        minimum * Math.Pow(maximum / minimum, Math.Clamp(position, 0, 1));
}

/// <summary>设置列表行容器:承载行内容,并在回收/关闭时退订行内事件。</summary>
internal sealed class WindowsSettingsRowElement : Grid, IDisposable
{
    private readonly Action _dispose;
    private bool _disposed;

    /// <summary>创建行容器。</summary>
    /// <param name="dispose">退订动作</param>
    public WindowsSettingsRowElement(Action dispose)
    {
        _dispose = dispose;
        HorizontalAlignment = HorizontalAlignment.Stretch;
    }

    /// <summary>退订行内事件;重复调用无副作用。</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _dispose();
    }

    /// <summary>行对应的共享设置项(重排时回写用)。</summary>
    public object? Item { get; set; }
}
