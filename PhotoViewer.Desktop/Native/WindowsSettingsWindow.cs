using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using PhotoViewer.ViewModels.Settings;
using Windows.Graphics;
using Windows.UI;

namespace PhotoViewer.Desktop.Native;

/// <summary>
/// 设置窗口壳:左侧 ListView 分类栏 + 右侧五页卡片内容,系统标题栏深色着色。
/// 关闭即应用(工作副本由 presenter 回写),没有 Apply/Cancel。
/// 本工程的非 XAML 资源加载下 <c>NavigationView</c>/<c>TitleBar</c>/<c>NumberBox</c>/<c>InfoBar</c>/<c>TextBox</c>
/// 模板不可用(实测缺 TabView 资源或 E_FAIL),因此外壳用 ListView + 系统 caption,数值项用 Slider。
/// </summary>
internal sealed class WindowsSettingsWindow
{
    private static readonly string[] CategoryTitles = ["文件", "预览", "控制", "EXIF", "AI"];
    private static readonly string[] CategoryKeys = ["File", "Preview", "Control", "Exif", "Ai"];

    private readonly Window _window;
    private readonly SettingsViewModel _settings;
    private readonly IntPtr _ownerHandle;
    private readonly WindowsHotkeyRecorder _recorder;
    private readonly WindowsSettingsPages _pages;
    private readonly Grid _root;
    private readonly Grid _pageHost;
    private readonly ListView _nav;
    private bool _sized;
    private bool _attached;

    /// <summary>创建设置窗口(不激活)。</summary>
    /// <param name="settings">设置工作副本</param>
    /// <param name="ownerHandle">主窗口句柄(取其显示器 DPI 与工作区)</param>
    public WindowsSettingsWindow(SettingsViewModel settings, IntPtr ownerHandle)
    {
        _window = new Window { Title = "设置" };
        _settings = settings;
        _ownerHandle = ownerHandle;
        _recorder = new WindowsHotkeyRecorder(settings);

        _root = new Grid
        {
            Background = new SolidColorBrush(WindowsLayout.BarBackground),
            RequestedTheme = ElementTheme.Dark,
            IsTabStop = true,
        };
        _root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(WindowsLayout.SettingsNavWidth) });
        _root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1) });
        _root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        _nav = new ListView
        {
            SelectionMode = ListViewSelectionMode.Single,
            IsItemClickEnabled = false,
            Background = new SolidColorBrush(WindowsLayout.BarBackground),
            BorderThickness = new Thickness(0),
            Padding = new Thickness(0),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
        };
        ScrollViewer.SetVerticalScrollMode(_nav, ScrollMode.Disabled);
        ScrollViewer.SetVerticalScrollBarVisibility(_nav, ScrollBarVisibility.Hidden);
        ScrollViewer.SetHorizontalScrollMode(_nav, ScrollMode.Disabled);
        ScrollViewer.SetHorizontalScrollBarVisibility(_nav, ScrollBarVisibility.Hidden);
        _nav.ContainerContentChanging += OnNavContainerContentChanging;
        _nav.SelectionChanged += (_, _) =>
        {
            if (_nav.SelectedIndex >= 0)
            {
                ShowPage(_nav.SelectedIndex);
            }
        };

        var navRows = new List<UIElement>();
        for (var index = 0; index < CategoryTitles.Length; index++)
        {
            navRows.Add(BuildNavRow(CategoryTitles[index], CategoryKeys[index]));
        }

        _nav.ItemsSource = navRows;
        _root.Children.Add(_nav);

        var divider = new Border
        {
            Width = 1,
            Background = new SolidColorBrush(WindowsLayout.Divider),
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        Grid.SetColumn(divider, 1);
        _root.Children.Add(divider);

        _pageHost = new Grid
        {
            Background = new SolidColorBrush(WindowsLayout.BarBackground),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
        };
        _pages = new WindowsSettingsPages(settings, _recorder, _window);
        foreach (var page in _pages.Pages)
        {
            _pageHost.Children.Add(page);
        }

        Grid.SetColumn(_pageHost, 2);
        _root.Children.Add(_pageHost);
        _nav.SelectedIndex = 0;
        ShowPage(0);

        _window.Content = _root;
        _window.Closed += OnClosed;
        _window.Activated += OnActivated;
        _root.Loaded += OnLoaded;

        _window.AppWindow.IsShownInSwitchers = false;
        if (_window.AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsResizable = true;
            presenter.IsMaximizable = false;
        }

        ConfigureCaption();
    }

    /// <summary>窗口关闭事件(由 presenter 触发工作副本回写)。</summary>
    public event EventHandler? Closed;

    /// <summary>底层窗口。</summary>
    public Window Window => _window;

    /// <summary>窗口根元素(验收读尺寸/主题)。</summary>
    internal Grid Root => _root;

    /// <summary>左侧分类列表(验收读分类数与切换)。</summary>
    internal ListView Navigation => _nav;

    /// <summary>五页内容。</summary>
    internal IReadOnlyList<UIElement> Pages => _pages.Pages;

    /// <summary>设置工作副本。</summary>
    internal SettingsViewModel Settings => _settings;

    /// <summary>快捷键录制器(验收驱动录制态)。</summary>
    internal WindowsHotkeyRecorder Recorder => _recorder;

    /// <summary>显示并激活;首帧布局前先按 DPI 定好物理尺寸。</summary>
    public void Show()
    {
        ApplyWindowMetrics();
        _window.Activate();
    }

    /// <summary>激活已存在的窗口。</summary>
    public void Activate() => _window.Activate();

    /// <summary>关闭窗口。</summary>
    public void Close() => _window.Close();

    /// <summary>深色系统标题栏:背景与按钮跟随应用深色,不改高度。</summary>
    private void ConfigureCaption()
    {
        if (!AppWindowTitleBar.IsCustomizationSupported())
        {
            return;
        }

        var titleBar = _window.AppWindow.TitleBar;
        titleBar.BackgroundColor = Color.FromArgb(0xFF, 0x11, 0x11, 0x11);
        titleBar.InactiveBackgroundColor = Color.FromArgb(0xFF, 0x11, 0x11, 0x11);
        titleBar.ButtonBackgroundColor = Color.FromArgb(0xFF, 0x11, 0x11, 0x11);
        titleBar.ButtonInactiveBackgroundColor = Color.FromArgb(0xFF, 0x11, 0x11, 0x11);
        titleBar.ButtonForegroundColor = Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF);
        titleBar.ButtonInactiveForegroundColor = Color.FromArgb(0xFF, 0xAA, 0xAA, 0xAA);
        titleBar.ButtonHoverBackgroundColor = Color.FromArgb(0xFF, 0x1F, 0x1F, 0x1F);
        titleBar.ButtonHoverForegroundColor = Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF);
        titleBar.ButtonPressedBackgroundColor = Color.FromArgb(0xFF, 0x15, 0x15, 0x15);
        titleBar.ButtonPressedForegroundColor = Color.FromArgb(0xFF, 0xEE, 0xEE, 0xEE);
    }

    /// <summary>按窗口 DPI 设定物理尺寸、最小尺寸与居中位置(激活前调用)。</summary>
    private void ApplyWindowMetrics()
    {
        if (_sized)
        {
            return;
        }

        _sized = true;
        var ownerDpi = _ownerHandle != IntPtr.Zero ? GetDpiForWindow(_ownerHandle) : 0u;
        var systemDpi = GetDpiForSystem();
        var dpi = ownerDpi > 0 ? ownerDpi : systemDpi;
        var scale = dpi > 0 ? dpi / 96.0 : 1.0;
        var width = (int)Math.Round(WindowsLayout.SettingsWindowWidth * scale);
        var height = (int)Math.Round(WindowsLayout.SettingsWindowHeight * scale);
        _window.AppWindow.Resize(new SizeInt32(width, height));
        if (_window.AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.PreferredMinimumWidth =
                (int)Math.Round(WindowsLayout.SettingsWindowMinWidth * scale);
            presenter.PreferredMinimumHeight =
                (int)Math.Round(WindowsLayout.SettingsWindowMinHeight * scale);
        }

        var area = _ownerHandle != IntPtr.Zero
            ? DisplayArea.GetFromWindowId(
                Win32Interop.GetWindowIdFromWindow(_ownerHandle), DisplayAreaFallback.Nearest)
            : DisplayArea.GetFromWindowId(_window.AppWindow.Id, DisplayAreaFallback.Primary);
        var work = area.WorkArea;
        _window.AppWindow.Move(new PointInt32(
            work.X + Math.Max(0, (work.Width - width) / 2),
            work.Y + Math.Max(0, (work.Height - height) / 2)));
    }

    /// <summary>分类行容器:去掉默认内边距,内容由 UIElement 直接呈现。</summary>
    /// <param name="sender">列表</param>
    /// <param name="args">容器参数</param>
    private static void OnNavContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        if (args.InRecycleQueue)
        {
            return;
        }

        args.ItemContainer.Padding = new Thickness(0);
        args.ItemContainer.MinHeight = 44;
        args.ItemContainer.HorizontalContentAlignment = HorizontalAlignment.Stretch;
        args.ItemContainer.VerticalContentAlignment = VerticalAlignment.Center;
    }

    /// <summary>构建一个分类行(图标 + 文本)。</summary>
    /// <param name="title">标题</param>
    /// <param name="key">图标键</param>
    /// <returns>行元素</returns>
    private static Grid BuildNavRow(string title, string key)
    {
        var row = new Grid
        {
            Height = 44,
            Padding = new Thickness(14, 0, 12, 0),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Center,
        };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.Children.Add(new FontIcon
        {
            Glyph = WindowsIcons.ForCategory(key),
            FontSize = 16,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = WindowsSettingsCard.Brush(WindowsLayout.ExifLabelForeground),
        });
        var label = new TextBlock
        {
            Text = title,
            FontSize = WindowsLayout.SettingsRowTitleFontSize,
            Foreground = WindowsSettingsCard.Brush(WindowsLayout.CardNameForeground),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(12, 0, 0, 0),
        };
        Grid.SetColumn(label, 1);
        row.Children.Add(label);
        return row;
    }

    /// <summary>首次布局后挂录制捕获并把焦点给根元素。</summary>
    /// <param name="sender">根元素</param>
    /// <param name="args">加载参数</param>
    private void OnLoaded(object sender, RoutedEventArgs args)
    {
        if (_attached)
        {
            return;
        }

        _attached = true;
        _recorder.Attach(_root);
        _root.Focus(FocusState.Programmatic);
    }

    /// <summary>窗口失去焦点时取消录制,避免把后台按键吞进工作副本。</summary>
    /// <param name="sender">窗口</param>
    /// <param name="args">激活参数</param>
    private void OnActivated(object sender, WindowActivatedEventArgs args)
    {
        if (args.WindowActivationState == WindowActivationState.Deactivated)
        {
            _recorder.Cancel();
        }
    }

    /// <summary>关闭:退订捕获与页面订阅,再通知 presenter 回写。</summary>
    /// <param name="sender">窗口</param>
    /// <param name="args">关闭参数</param>
    private void OnClosed(object sender, WindowEventArgs args)
    {
        _recorder.Detach();
        _pages.Detach();
        Closed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>只显示指定页。</summary>
    /// <param name="index">页索引</param>
    private void ShowPage(int index)
    {
        for (var page = 0; page < _pages.Pages.Count; page++)
        {
            _pages.Pages[page].Visibility = page == index ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    /// <summary>读取系统 DPI(无宿主窗口时的兜底)。</summary>
    /// <returns>DPI</returns>
    [DllImport("user32.dll")]
    private static extern uint GetDpiForSystem();

    /// <summary>读取窗口 DPI。</summary>
    /// <param name="hwnd">窗口句柄</param>
    /// <returns>DPI</returns>
    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);
}
