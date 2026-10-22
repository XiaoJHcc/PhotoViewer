using System;
using System.Collections.Generic;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using PhotoViewer.ViewModels.Settings;
using Windows.UI;

namespace PhotoViewer.Windows.Native;

/// <summary>
/// 设置窗口壳:Win11 设置页结构(Mica + TitleBar + NavigationView),五页内容在 C# 中构建。
/// 关闭即应用(工作副本由 presenter 回写),没有 Apply/Cancel。
/// </summary>
internal sealed class WindowsSettingsWindow
{
    private static readonly (string Title, string Key)[] Categories =
    [
        ("文件", "File"),
        ("预览", "Preview"),
        ("控制", "Control"),
        ("EXIF", "Exif"),
        ("AI", "Ai"),
    ];

    private readonly Window _window;
    private readonly SettingsViewModel _settings;
    private readonly SettingsViewModel _sourceSettings;
    private readonly IntPtr _ownerHandle;
    private readonly WindowsHotkeyRecorder _recorder;
    private readonly WindowsSettingsPages _pages;
    private readonly Grid _root;
    private readonly TitleBar _titleBar;
    private readonly NavigationView _nav;
    private readonly Grid _pageHost;
    private readonly InfoBar _persistenceBar;
    private bool _sized;
    private bool _attached;

    /// <summary>创建设置窗口(不激活)。</summary>
    /// <param name="settings">设置工作副本</param>
    /// <param name="sourceSettings">源设置(读取 <see cref="SettingsViewModel.PersistenceStatus"/>)</param>
    /// <param name="ownerHandle">主窗口句柄(取其显示器 DPI 与工作区)</param>
    public WindowsSettingsWindow(SettingsViewModel settings, SettingsViewModel sourceSettings, IntPtr ownerHandle)
    {
        _settings = settings;
        _sourceSettings = sourceSettings;
        _ownerHandle = ownerHandle;
        _recorder = new WindowsHotkeyRecorder(settings);

        var transparent = new SolidColorBrush(Color.FromArgb(0x00, 0x00, 0x00, 0x00));
        _root = new Grid
        {
            Background = transparent,
            RequestedTheme = ElementTheme.Dark,
            IsTabStop = true,
        };
        _root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(WindowsLayout.SettingsTitleBarHeight) });
        _root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        _window = new Window
        {
            Title = "设置",
            SystemBackdrop = new MicaBackdrop(),
            ExtendsContentIntoTitleBar = true,
        };
        _window.Content = _root;

        _titleBar = new TitleBar
        {
            Title = "设置",
            IsBackButtonVisible = false,
            IsPaneToggleButtonVisible = false,
            IconSource = new FontIconSource
            {
                Glyph = "\uE713",
                FontFamily = new FontFamily(WindowsIcons.FluentFontFamily),
                FontSize = 16,
            },
        };
        _root.Children.Add(_titleBar);
        _window.SetTitleBar(_titleBar);

        // SETTINGS-14：设置页顶部显示源设置读取失败/损坏备份状态；工作副本自身不落盘。
        _persistenceBar = new InfoBar
        {
            Severity = InfoBarSeverity.Warning,
            IsClosable = false,
            IsOpen = !string.IsNullOrEmpty(_sourceSettings.PersistenceStatus),
            Message = _sourceSettings.PersistenceStatus ?? string.Empty,
            Margin = new Thickness(WindowsLayout.SettingsPagePadding, 0, WindowsLayout.SettingsPagePadding, 0),
        };
        Grid.SetRow(_persistenceBar, 1);
        _root.Children.Add(_persistenceBar);
        _sourceSettings.PropertyChanged += OnSourceSettingsChanged;

        _pages = new WindowsSettingsPages(settings, _recorder, _window);
        _pageHost = new Grid { Background = transparent };
        foreach (var page in _pages.Pages)
        {
            _pageHost.Children.Add(page);
        }

        _nav = new NavigationView
        {
            PaneDisplayMode = NavigationViewPaneDisplayMode.Left,
            IsPaneOpen = true,
            OpenPaneLength = WindowsLayout.SettingsNavWidth,
            IsSettingsVisible = false,
            IsBackButtonVisible = NavigationViewBackButtonVisible.Collapsed,
            IsPaneToggleButtonVisible = false,
            CompactModeThresholdWidth = 0,
            ExpandedModeThresholdWidth = 0,
            IsTitleBarAutoPaddingEnabled = false,
            AlwaysShowHeader = false,
            Header = null,
            Background = transparent,
            Content = _pageHost,
        };
        // 设置页不使用 NavigationView 的“内容岛”圆角边框,让 Mica 平铺到整块内容区。
        _nav.Resources["NavigationViewContentGridBorderThickness"] = new Thickness(0);
        _nav.Resources["NavigationViewContentGridCornerRadius"] = new CornerRadius(0);
        _nav.Resources["NavigationViewContentBackground"] = transparent;

        for (var index = 0; index < Categories.Length; index++)
        {
            _nav.MenuItems.Add(new NavigationViewItem
            {
                Content = Categories[index].Title,
                Icon = new FontIcon { Glyph = WindowsIcons.ForCategory(Categories[index].Key) },
                Tag = index,
            });
        }

        _nav.SelectionChanged += OnNavigationSelectionChanged;
        Grid.SetRow(_nav, 2);
        _root.Children.Add(_nav);

        _nav.SelectedItem = _nav.MenuItems[0];
        ShowPage(0);

        _window.Closed += OnClosed;
        _window.AppWindow.Closing += OnClosing;
        _window.Activated += OnActivated;
        _root.Loaded += OnLoaded;
        WindowsWindowChrome.Configure(_window);
    }

    /// <summary>窗口关闭事件(由 presenter 触发工作副本回写)。</summary>
    public event EventHandler? Closed;

    /// <summary>底层窗口。</summary>
    public Window Window => _window;

    /// <summary>窗口根元素(验收读尺寸/主题)。</summary>
    internal Grid Root => _root;

    /// <summary>左侧分类导航(验收读分类数与切换)。</summary>
    internal NavigationView Navigation => _nav;

    /// <summary>窗口标题栏控件(验收确认 TitleBar 可用)。</summary>
    internal TitleBar TitleBarControl => _titleBar;

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

    /// <summary>按窗口 DPI 设定物理尺寸、最小尺寸与居中位置(激活前调用)。</summary>
    private void ApplyWindowMetrics()
    {
        if (_sized)
        {
            return;
        }

        _sized = true;
        WindowsWindowChrome.SizeBeforeShow(
            _window,
            _ownerHandle,
            WindowsLayout.SettingsWindowWidth,
            WindowsLayout.SettingsWindowHeight,
            WindowsLayout.SettingsWindowMinWidth,
            WindowsLayout.SettingsWindowMinHeight);
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
        _pages.CommitForClose();
        _sourceSettings.PropertyChanged -= OnSourceSettingsChanged;
        _recorder.Detach();
        _pages.Detach();
        Closed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>关闭开始前提交未失焦的预设文本;之后不再触发布局或焦点。</summary>
    /// <param name="sender">AppWindow</param>
    /// <param name="args">关闭参数</param>
    private void OnClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        _pages.CommitForClose();
    }

    /// <summary>源设置的持久化状态变化时刷新顶部状态行(SETTINGS-14)。</summary>
    /// <param name="sender">源设置。</param>
    /// <param name="args">属性变化参数。</param>
    private void OnSourceSettingsChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(SettingsViewModel.PersistenceStatus))
        {
            _persistenceBar.Message = _sourceSettings.PersistenceStatus ?? string.Empty;
            _persistenceBar.IsOpen = !string.IsNullOrEmpty(_sourceSettings.PersistenceStatus);
        }
    }

    /// <summary>分类切换:选中项 Tag 即页索引。</summary>
    /// <param name="sender">导航</param>
    /// <param name="args">选择参数</param>
    private void OnNavigationSelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItem is NavigationViewItem { Tag: int index })
        {
            _pages.CommitPendingEdits();
            ShowPage(index);
        }
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
}
