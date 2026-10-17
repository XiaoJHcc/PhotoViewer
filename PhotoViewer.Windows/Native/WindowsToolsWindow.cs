using System;
using System.Collections.Generic;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using PhotoViewer.ViewModels.Tools;
using Windows.UI;

namespace PhotoViewer.Windows.Native;

/// <summary>
/// 工具窗口壳:Win11 设置页结构(Mica + TitleBar + NavigationView)承载 EXIF 详情 / 照片数据统计 / DINO 诊断。
/// 导航状态与工具数据全部复用共享 <see cref="ToolsViewModel"/> 与 <see cref="DinoDiagnosticPresenter"/>;
/// 本类只做分类选择、页面显隐与窗口生命周期,不自行驱动推理。
/// </summary>
internal sealed class WindowsToolsWindow
{
    private static readonly (string Title, string Key, ToolsPage Page)[] Categories =
    [
        ("EXIF 详情", "Exif", ToolsPage.Exif),
        ("照片数据统计", "Stats", ToolsPage.Stats),
        ("DINO 诊断", "Dino", ToolsPage.Dino),
    ];

    private readonly Window _window;
    private readonly ToolsViewModel _tools;
    private readonly IntPtr _ownerHandle;
    private readonly Grid _root;
    private readonly TitleBar _titleBar;
    private readonly NavigationView _nav;
    private readonly Grid _pageHost;
    private readonly WindowsExifToolPage _exifPage;
    private readonly WindowsStatsToolPage _statsPage;
    private readonly WindowsDinoToolPage _dinoPage;
    private readonly List<UIElement> _pages = [];
    private readonly List<NavigationViewItem> _navItems = [];
    private bool _sized;
    private bool _suppressSelection;
    private ToolsPage _selected = ToolsPage.Exif;

    /// <summary>创建工具窗口(不激活)。</summary>
    /// <param name="tools">共享工具壳视图模型</param>
    /// <param name="ownerHandle">主窗口句柄(取显示器 DPI 与工作区)</param>
    public WindowsToolsWindow(ToolsViewModel tools, IntPtr ownerHandle)
    {
        _tools = tools;
        _ownerHandle = ownerHandle;

        var transparent = new SolidColorBrush(Color.FromArgb(0x00, 0x00, 0x00, 0x00));
        _root = new Grid
        {
            Background = transparent,
            RequestedTheme = ElementTheme.Dark,
            IsTabStop = true,
        };
        _root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(WindowsLayout.SettingsTitleBarHeight) });
        _root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        _window = new Window
        {
            Title = "工具",
            SystemBackdrop = new MicaBackdrop(),
            ExtendsContentIntoTitleBar = true,
        };
        _window.Content = _root;

        _titleBar = new TitleBar
        {
            Title = "工具",
            IsBackButtonVisible = false,
            IsPaneToggleButtonVisible = false,
            IconSource = new FontIconSource
            {
                Glyph = WindowsLayout.ToolsWindowGlyph,
                FontFamily = new FontFamily(WindowsIcons.FluentFontFamily),
                FontSize = 16,
            },
        };
        _root.Children.Add(_titleBar);
        _window.SetTitleBar(_titleBar);

        // 选择器需要绑定工具窗口自身,但窗口句柄要在窗口构造后才可读。
        Func<IntPtr> handle = () => WinRT.Interop.WindowNative.GetWindowHandle(_window);
        _exifPage = new WindowsExifToolPage(tools);
        _statsPage = new WindowsStatsToolPage(tools.PhotoStats, handle);
        _dinoPage = new WindowsDinoToolPage(tools.DinoPresenter);
        _pages.Add(_exifPage);
        _pages.Add(_statsPage);
        _pages.Add(_dinoPage);

        _pageHost = new Grid { Background = transparent };
        foreach (var page in _pages)
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
        _nav.Resources["NavigationViewContentGridBorderThickness"] = new Thickness(0);
        _nav.Resources["NavigationViewContentGridCornerRadius"] = new CornerRadius(0);
        _nav.Resources["NavigationViewContentBackground"] = transparent;

        for (var index = 0; index < Categories.Length; index++)
        {
            var item = new NavigationViewItem
            {
                Content = Categories[index].Title,
                Icon = new FontIcon { Glyph = WindowsIcons.ForToolCategory(Categories[index].Key) },
                Tag = Categories[index].Page,
            };
            _navItems.Add(item);
            _nav.MenuItems.Add(item);
        }

        _nav.SelectionChanged += OnNavigationSelectionChanged;
        Grid.SetRow(_nav, 1);
        _root.Children.Add(_nav);

        _tools.PropertyChanged += OnToolsChanged;
        _window.Closed += OnClosed;
        _root.Loaded += OnLoaded;
        WindowsWindowChrome.Configure(_window);

        UpdateAvailability();
        SelectInitialPage();
    }

    /// <summary>窗口关闭事件(由 presenter 释放页面订阅)。</summary>
    public event EventHandler? Closed;

    /// <summary>底层窗口。</summary>
    public Window Window => _window;

    /// <summary>窗口根元素(验收读尺寸/主题)。</summary>
    internal Grid Root => _root;

    /// <summary>左侧分类导航(验收读分类数与切换)。</summary>
    internal NavigationView Navigation => _nav;

    /// <summary>窗口标题栏控件(验收确认 TitleBar 可用)。</summary>
    internal TitleBar TitleBarControl => _titleBar;

    /// <summary>三页内容。</summary>
    internal IReadOnlyList<UIElement> Pages => _pages;

    /// <summary>EXIF 详情页。</summary>
    internal WindowsExifToolPage ExifPage => _exifPage;

    /// <summary>照片数据统计页。</summary>
    internal WindowsStatsToolPage StatsPage => _statsPage;

    /// <summary>DINO 诊断页。</summary>
    internal WindowsDinoToolPage DinoPage => _dinoPage;

    /// <summary>共享工具壳视图模型。</summary>
    internal ToolsViewModel Tools => _tools;

    /// <summary>当前选中分类。</summary>
    internal ToolsPage SelectedPage => _selected;

    /// <summary>显示并激活;首帧布局前先按 DPI 定好物理尺寸。</summary>
    public void Show()
    {
        ApplyWindowMetrics();
        _window.Activate();
    }

    /// <summary>激活已存在的窗口。</summary>
    public void Activate()
    {
        EnsureSelection();
        _window.Activate();
    }

    /// <summary>关闭窗口。</summary>
    public void Close() => _window.Close();

    /// <summary>
    /// 首次打开时按共享导航状态选页:EXIF 可用则进 EXIF,否则进照片统计(无照片时唯一可用的页)。
    /// </summary>
    private void SelectInitialPage()
    {
        var page = _tools.CurrentTool switch
        {
            ExifDetailViewModel => ToolsPage.Exif,
            PhotoStatsViewModel => ToolsPage.Stats,
            DinoDebugViewModel => ToolsPage.Dino,
            _ => _tools.IsExifDetailEnabled ? ToolsPage.Exif : ToolsPage.Stats,
        };
        SelectPage(page, openShared: true);
    }

    /// <summary>已打开的窗口再次被请求时恢复上次分类;若上次分类已不可用则退回可用页。</summary>
    private void EnsureSelection()
    {
        if (_tools.CurrentTool is ExifDetailViewModel or PhotoStatsViewModel or DinoDebugViewModel)
        {
            return;
        }

        var page = _selected;
        if (page == ToolsPage.Exif && !_tools.IsExifDetailEnabled)
        {
            page = ToolsPage.Stats;
        }

        SelectPage(page, openShared: true);
    }

    /// <summary>选择分类:写回共享导航状态并只显示一页。</summary>
    /// <param name="page">目标页</param>
    /// <param name="openShared">是否同时调用共享 Open 方法</param>
    internal void SelectPage(ToolsPage page, bool openShared)
    {
        if (openShared && page == ToolsPage.Exif && !_tools.IsExifDetailEnabled)
        {
            page = ToolsPage.Stats;
        }

        // 先落 UI 再调用共享 Open:共享状态变化会同步回来,此时 _selected 已一致,不会递归重排。
        _selected = page;
        for (var index = 0; index < _pages.Count; index++)
        {
            _pages[index].Visibility = index == (int)page ? Visibility.Visible : Visibility.Collapsed;
        }

        if (page == ToolsPage.Exif)
        {
            _exifPage.SyncWhenVisible();
        }

        _suppressSelection = true;
        _nav.SelectedItem = _navItems[(int)page];
        _suppressSelection = false;

        if (!openShared)
        {
            return;
        }

        switch (page)
        {
            case ToolsPage.Exif:
                _tools.OpenExifDetail();
                break;
            case ToolsPage.Stats:
                _tools.OpenPhotoStats();
                break;
            case ToolsPage.Dino:
                _tools.OpenDinoDebug();
                break;
        }
    }

    /// <summary>按共享状态刷新分类可用性(EXIF 需要当前照片已有 EXIF 数据)。</summary>
    private void UpdateAvailability()
    {
        _navItems[(int)ToolsPage.Exif].IsEnabled = _tools.IsExifDetailEnabled;
    }

    /// <summary>共享工具状态变化:分类可用性与当前页跟随。</summary>
    private void OnToolsChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs args)
    {
        switch (args.PropertyName)
        {
            case nameof(ToolsViewModel.IsExifDetailEnabled):
                UpdateAvailability();
                break;
            case nameof(ToolsViewModel.CurrentTool):
                switch (_tools.CurrentTool)
                {
                    case ExifDetailViewModel:
                        ShowSharedPage(ToolsPage.Exif);
                        break;
                    case PhotoStatsViewModel:
                        ShowSharedPage(ToolsPage.Stats);
                        break;
                    case DinoDebugViewModel:
                        ShowSharedPage(ToolsPage.Dino);
                        break;
                }

                break;
        }
    }

    /// <summary>共享导航切到某页时同步选框与页面显隐,不重复调用 Open。</summary>
    /// <param name="page">目标页</param>
    private void ShowSharedPage(ToolsPage page)
    {
        if (_selected == page)
        {
            return;
        }

        SelectPage(page, openShared: false);
    }

    /// <summary>分类切换:选中项 Tag 即页枚举。</summary>
    private void OnNavigationSelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (_suppressSelection || args.SelectedItem is not NavigationViewItem { Tag: ToolsPage page })
        {
            return;
        }

        SelectPage(page, openShared: true);
    }

    /// <summary>首次布局后把焦点给根元素,让键盘滚动/切换生效。</summary>
    private void OnLoaded(object sender, RoutedEventArgs args) => _root.Focus(FocusState.Programmatic);

    /// <summary>关闭:退订共享状态与页面订阅,再通知 presenter 释放窗口。</summary>
    private void OnClosed(object sender, WindowEventArgs args)
    {
        NativeStartupLog.Write("tools window closed");
        _tools.PropertyChanged -= OnToolsChanged;
        _exifPage.Detach();
        _statsPage.Detach();
        _dinoPage.Detach();
        Closed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>按主窗口 DPI 设定物理尺寸、最小尺寸并居中(激活前调用)。</summary>
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
            WindowsLayout.ToolsWindowWidth,
            WindowsLayout.ToolsWindowHeight,
            WindowsLayout.ToolsWindowMinWidth,
            WindowsLayout.ToolsWindowMinHeight);
    }
}

/// <summary>工具窗口的分类页。</summary>
internal enum ToolsPage
{
    /// <summary>EXIF 详情。</summary>
    Exif = 0,

    /// <summary>照片数据统计(Windows 专属)。</summary>
    Stats = 1,

    /// <summary>DINO 诊断。</summary>
    Dino = 2,
}
