using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using PhotoViewer.Core.AI;
using PhotoViewer.Core.Design;
using PhotoViewer.Core.Image;
using PhotoViewer.Core.Platform;
using PhotoViewer.Shared;
using PhotoViewer.ViewModels.Main;
using PhotoViewer.ViewModels.Main.File;

namespace PhotoViewer.Windows.Native;

/// <summary>
/// 原生相似聚类面板:相似项列表(分数柱条)、空态/未提取占位、"提取全部 / 补齐全部"按钮与进度条。
/// 三态由 <see cref="SimilarityPanelViewModel"/> 的 HasItems / HasUnindexed / IsStateIndexing 驱动,
/// 点选相似项走 <see cref="SimilarityPanelViewModel.SelectItemCommand"/> 切主图但保留主列表锚点。
/// </summary>
internal sealed class WindowsSimilarityPanel : Grid
{
    private readonly MainViewModel _main;
    private readonly SimilarityPanelViewModel _panel;
    private readonly Func<ImageFile, int, Task> _rate;
    private readonly Border _frame;
    private readonly Grid _content;
    private readonly ScrollViewer _scroll;
    private readonly ItemsRepeater _repeater;
    private readonly StackLayout _stackLayout;
    private readonly Stack<WindowsThumbnailCard> _pool = new();

    private readonly StackPanel _emptyPanel;
    private readonly TextBlock _unindexedPlaceholder;
    private readonly TextBlock _fullPlaceholder;
    private readonly TextBlock _errorText;
    private readonly TextBlock _dockedError;
    private readonly SimilarityActionView _emptyActions;
    private readonly SimilarityActionView _dockedActions;

    private bool _isRowLayout = true;
    private bool _refreshPending;
    private IReadOnlyList<SimilarityItem> _items = Array.Empty<SimilarityItem>();

    /// <summary>
    /// 创建相似聚类面板。
    /// </summary>
    /// <param name="main">主视图模型</param>
    /// <param name="rate">评分回调</param>
    public WindowsSimilarityPanel(MainViewModel main, Func<ImageFile, int, Task> rate)
    {
        _main = main;
        _panel = main.FileVM.SimilarityPanel;
        _rate = rate;

        _emptyActions = new SimilarityActionView(StartIndexing);
        _dockedActions = new SimilarityActionView(StartIndexing);

        _unindexedPlaceholder = Placeholder("尚未提取特征");
        _fullPlaceholder = Placeholder("无相似照片");
        _errorText = new TextBlock
        {
            FontSize = WindowsLayout.SimilarityErrorFontSize,
            Foreground = new SolidColorBrush(WindowsLayout.SimilarityErrorForeground),
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = WindowsLayout.SimilarityErrorMaxWidth,
            HorizontalAlignment = HorizontalAlignment.Center,
            Visibility = Visibility.Collapsed,
        };
        // SIM-10：列表态的动作区同样承载提取错误，错误不再只挂空态栈。
        _dockedError = new TextBlock
        {
            FontSize = WindowsLayout.SimilarityErrorFontSize,
            Foreground = new SolidColorBrush(WindowsLayout.SimilarityErrorForeground),
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = WindowsLayout.SimilarityErrorMaxWidth,
            HorizontalAlignment = HorizontalAlignment.Center,
            Visibility = Visibility.Collapsed,
        };
        _dockedActions.Children.Add(_dockedError);
        _emptyPanel = new StackPanel
        {
            Spacing = DesignTokens.SimEmptySpacing,
            Margin = new Thickness(DesignTokens.SimEmptyMargin),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Visibility = Visibility.Collapsed,
        };
        _emptyPanel.Children.Add(_unindexedPlaceholder);
        _emptyPanel.Children.Add(_fullPlaceholder);
        _emptyPanel.Children.Add(_emptyActions);
        _emptyPanel.Children.Add(_errorText);

        _stackLayout = new StackLayout { Orientation = Orientation.Vertical, Spacing = 0 };
        _repeater = new ItemsRepeater
        {
            Layout = _stackLayout,
            ItemTemplate = new ItemFactory(this),
            Margin = new Thickness(DesignTokens.SimEmptyMargin),
            VerticalAlignment = VerticalAlignment.Top,
        };
        _scroll = new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollMode = ScrollMode.Disabled,
            VerticalScrollMode = ScrollMode.Enabled,
            ZoomMode = ZoomMode.Disabled,
            Content = _repeater,
            Visibility = Visibility.Collapsed,
        };

        _content = new Grid();
        _content.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        _content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        _content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _content.Children.Add(_scroll);
        _content.Children.Add(_dockedActions);
        _content.Children.Add(_emptyPanel);

        _frame = new Border
        {
            Background = new SolidColorBrush(WindowsLayout.SimilarityPanelBackground),
            BorderBrush = new SolidColorBrush(WindowsLayout.SimilarityPanelBorder),
            BorderThickness = new Thickness(WindowsLayout.SimilarityPanelBorderWidth),
            CornerRadius = new CornerRadius(DesignTokens.SimPanelCornerRadius),
            Margin = new Thickness(0, 0, WindowsLayout.SimilarityPanelMargin, 0),
            MinWidth = WindowsLayout.SimilarityMinWidth,
            MinHeight = WindowsLayout.SimilarityMinHeight,
            Child = _content,
        };
        Children.Add(_frame);

        _main.PropertyChanged += OnMainChanged;
        _panel.PropertyChanged += OnPanelChanged;
        _isRowLayout = _main.IsRowLayout;
        ApplyOrientation();
        ReloadItems();
        Refresh();
    }

    /// <summary>释放订阅。</summary>
    public void Detach()
    {
        _main.PropertyChanged -= OnMainChanged;
        _panel.PropertyChanged -= OnPanelChanged;
    }

    /// <summary>验收专用:列表是否可见。</summary>
    internal bool ListVisible => _scroll.Visibility == Visibility.Visible;

    /// <summary>验收专用:列表项数。</summary>
    internal int ItemCount => _items.Count;

    /// <summary>验收专用:空态占位是否可见。</summary>
    internal bool EmptyVisible => _emptyPanel.Visibility == Visibility.Visible;

    /// <summary>验收专用:动作按钮文案。</summary>
    internal string ActionButtonText => _emptyActions.ButtonText;

    /// <summary>切换单行/单列布局:上下布局列表横排、动作区贴右;左右布局列表竖排、动作区贴底。</summary>
    /// <param name="isRowLayout">是否为上下布局</param>
    public void SetOrientation(bool isRowLayout)
    {
        _isRowLayout = isRowLayout;
        ApplyOrientation();
        ReloadItems();
    }

    /// <summary>
    /// 按可用宽度落面板宽度(SIM-01/SIM-07):行布局取共享 <see cref="SimilarityPanelGeometry.RowWidth"/>
    /// （已含“可用宽一半”上限与 4 点外边距）;列布局固定 <see cref="SimilarityPanelGeometry.PanelWidth"/>。
    /// </summary>
    /// <param name="availableWidth">文件栏/窗口可用宽度。</param>
    /// <param name="hasItems">是否有相似项。</param>
    /// <param name="itemCount">相似项数量。</param>
    internal void SetAvailableWidth(double availableWidth, bool hasItems, int itemCount)
    {
        if (availableWidth <= 1)
        {
            // 首帧布局前宽度未知：交给内容自然宽度,布局完成后再夹到一半。
            Width = double.NaN;
            MaxWidth = double.PositiveInfinity;
            return;
        }

        if (_isRowLayout)
        {
            double width = Math.Max(0, SimilarityPanelGeometry.RowWidth(hasItems, itemCount, availableWidth));
            Width = width;
            MaxWidth = width;
            // RowWidth 已含 4 点外边距；面板占满该宽度，外框再让出右边距，卡片才不会被裁掉 4 点。
            _frame.Margin = new Thickness(0, 0, WindowsLayout.SimilarityPanelMargin, 0);
            return;
        }

        Width = SimilarityPanelGeometry.PanelWidth;
        MaxWidth = SimilarityPanelGeometry.PanelWidth;
        _frame.Margin = new Thickness(0, 0, WindowsLayout.SimilarityPanelMargin, 0);
    }

    /// <summary>应用当前方向的列表轴与动作区停靠位置。</summary>
    private void ApplyOrientation()
    {
        _stackLayout.Orientation = _isRowLayout ? Orientation.Horizontal : Orientation.Vertical;
        _scroll.HorizontalScrollMode = _isRowLayout ? ScrollMode.Enabled : ScrollMode.Disabled;
        _scroll.VerticalScrollMode = _isRowLayout ? ScrollMode.Enabled : ScrollMode.Disabled;
        _scroll.HorizontalScrollBarVisibility = _isRowLayout ? ScrollBarVisibility.Auto : ScrollBarVisibility.Disabled;
        _scroll.VerticalScrollBarVisibility = _isRowLayout ? ScrollBarVisibility.Disabled : ScrollBarVisibility.Auto;
        _frame.VerticalAlignment = _isRowLayout ? VerticalAlignment.Top : VerticalAlignment.Stretch;

        Grid.SetRow(_dockedActions, _isRowLayout ? 0 : 1);
        Grid.SetColumn(_dockedActions, _isRowLayout ? 1 : 0);
        _dockedActions.Margin = _isRowLayout
            ? new Thickness(8, 3, 8, 3)
            : new Thickness(3, 3, 3, 8);
        _dockedActions.VerticalAlignment = _isRowLayout ? VerticalAlignment.Center : VerticalAlignment.Bottom;
    }

    /// <summary>面板状态变化:合并到一轮刷新。</summary>
    private void OnPanelChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e) => ScheduleRefresh();

    /// <summary>布局方向变化时重排。</summary>
    private void OnMainChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.IsRowLayout))
        {
            SetOrientation(_main.IsRowLayout);
        }
    }

    /// <summary>合并刷新:一轮内多次变化只重建一次。</summary>
    private void ScheduleRefresh()
    {
        if (_refreshPending)
        {
            return;
        }

        _refreshPending = true;
        UiDispatcher.Post(() =>
        {
            _refreshPending = false;
            Refresh();
        }, UiPriority.Background);
    }

    /// <summary>按视图模型状态刷新列表与动作区。</summary>
    private void Refresh()
    {
        try
        {
            if (!ReferenceEquals(_items, _panel.SimilarItems))
            {
                ReloadItems();
            }

            bool hasItems = _panel.HasItems;
            _scroll.Visibility = hasItems ? Visibility.Visible : Visibility.Collapsed;
            _dockedActions.Visibility = hasItems && _panel.ShowActionArea ? Visibility.Visible : Visibility.Collapsed;
            _emptyPanel.Visibility = hasItems ? Visibility.Collapsed : Visibility.Visible;
            _unindexedPlaceholder.Visibility = _panel.ShowUnindexedPlaceholder ? Visibility.Visible : Visibility.Collapsed;
            _fullPlaceholder.Visibility = _panel.ShowFullPlaceholder ? Visibility.Visible : Visibility.Collapsed;
            _errorText.Text = _panel.IndexError ?? string.Empty;
            _errorText.Visibility = _panel.HasIndexError ? Visibility.Visible : Visibility.Collapsed;
            _dockedError.Text = _panel.IndexError ?? string.Empty;
            _dockedError.Visibility = _panel.ShowActionAreaError ? Visibility.Visible : Visibility.Collapsed;

            _emptyActions.Refresh(_panel);
            _dockedActions.Refresh(_panel);
        }
        catch (Exception ex)
        {
            NativeStartupLog.Fail("similarity panel refresh", ex);
        }
    }

    /// <summary>重建相似项列表(引用变化时)。</summary>
    private void ReloadItems()
    {
        _items = _panel.SimilarItems;
        _repeater.ItemsSource = null;
        _pool.Clear();
        _repeater.ItemsSource = _items;
    }

    /// <summary>取一张卡片:回收池优先,否则新建。</summary>
    private WindowsThumbnailCard RentCard(SimilarityItem item)
    {
        var card = _pool.Count > 0 ? _pool.Pop() : new WindowsThumbnailCard(SelectItem, _rate);
        card.Bind(item.File, _main.Settings.ShowRating,
            secondLineText: $"{Math.Clamp(item.Score, 0, 1) * 100:0}%",
            secondLineProgress: SimilarityProgressFraction(item.Score),
            highlightMainImage: true);
        _main.FileVM.ThumbnailList.QueueThumbnailLoad(item.File, true);
        return card;
    }

    /// <summary>归还卡片。</summary>
    private void ReturnCard(WindowsThumbnailCard card)
    {
        card.Unbind();
        _pool.Push(card);
    }

    /// <summary>触发批量特征提取。</summary>
    private void StartIndexing() => _panel.StartIndexingCommand();

    /// <summary>点选相似项:切主图但保留主列表锚点,不触发相似面板重排。</summary>
    private void SelectItem(ImageFile file)
    {
        var item = _items.FirstOrDefault(candidate => ReferenceEquals(candidate.File, file));
        if (item != null)
        {
            _panel.SelectItemCommand(item);
        }
    }

    /// <summary>分数 → 柱条占比(75% 以下为 0,100% 为 1),与旧壳 SimilarityScoreToProgressFractionConverter 一致。</summary>
    private static double SimilarityProgressFraction(double score)
    {
        double lowerBound = DesignTokens.SimScoreLowerBound;
        return (Math.Clamp(score, lowerBound, 1) - lowerBound) / (1 - lowerBound);
    }

    /// <summary>占位文本。</summary>
    private static TextBlock Placeholder(string text) => new()
    {
        Text = text,
        FontSize = WindowsLayout.SimilarityCaptionFontSize,
        Foreground = new SolidColorBrush(WindowsLayout.SimilarityPlaceholderForeground),
        HorizontalAlignment = HorizontalAlignment.Center,
        TextAlignment = TextAlignment.Center,
        TextWrapping = TextWrapping.Wrap,
        Visibility = Visibility.Collapsed,
    };

    /// <summary>
    /// 动作区(未提取张数 + "提取全部/补齐全部"按钮,提取中原地换成进度条)。
    /// 空态与有列表两处各用一个实例(旧壳同样是两份控件)。
    /// </summary>
    private sealed class SimilarityActionView : StackPanel
    {
        private readonly TextBlock _caption;
        private readonly Button _button;
        private readonly Border _progressTrack;
        private readonly Border _progressFill;
        private readonly TextBlock _progressText;
        private readonly Grid _progressHost;

        /// <summary>
        /// 创建动作区。
        /// </summary>
        /// <param name="start">开始提取回调</param>
        public SimilarityActionView(Action start)
        {
            Spacing = WindowsLayout.SimilarityActionSpacing;
            HorizontalAlignment = HorizontalAlignment.Center;
            _caption = new TextBlock
            {
                FontSize = WindowsLayout.SimilarityCaptionFontSize,
                Foreground = new SolidColorBrush(WindowsLayout.SimilarityCaptionForeground),
                TextAlignment = TextAlignment.Center,
                TextWrapping = TextWrapping.Wrap,
                HorizontalAlignment = HorizontalAlignment.Center,
                Visibility = Visibility.Collapsed,
            };
            _button = new Button
            {
                Width = WindowsLayout.SimilarityActionWidth,
                Height = WindowsLayout.SimilarityActionHeight,
                FontSize = WindowsLayout.SimilarityActionFontSize,
                CornerRadius = new CornerRadius(WindowsLayout.SimilarityActionCornerRadius),
                BorderThickness = new Thickness(1),
                BorderBrush = new SolidColorBrush(WindowsLayout.SimilarityActionBorder),
                Background = new SolidColorBrush(WindowsLayout.SimilarityActionBackground),
                Foreground = new SolidColorBrush(WindowsLayout.SimilarityActionForeground),
                HorizontalAlignment = HorizontalAlignment.Center,
                HorizontalContentAlignment = HorizontalAlignment.Center,
            };
            WindowsControlStyles.ApplyActionButton(_button);
            _button.Click += (_, _) => start();
            // WinUI 的 ProgressBar 模板在本工程(非 XAML 工程)加载即抛 XamlParseException,
            // 这里用两条 Border 自绘进度:底槽 + 按比例填充。
            _progressTrack = new Border
            {
                Background = new SolidColorBrush(WindowsLayout.SimilarityActionBackground),
                CornerRadius = new CornerRadius(WindowsLayout.SimilarityActionCornerRadius),
                BorderBrush = new SolidColorBrush(WindowsLayout.SimilarityActionBorder),
                BorderThickness = new Thickness(1),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch,
            };
            _progressFill = new Border
            {
                Background = new SolidColorBrush(WindowsLayout.SimilarityProgressForeground),
                CornerRadius = new CornerRadius(5),
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Stretch,
                Width = 0,
            };
            _progressText = new TextBlock
            {
                FontSize = WindowsLayout.SimilarityProgressFontSize,
                Foreground = new SolidColorBrush(WindowsLayout.CardNameForeground),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
            _progressHost = new Grid
            {
                Width = WindowsLayout.SimilarityActionWidth,
                Height = WindowsLayout.SimilarityActionHeight,
                HorizontalAlignment = HorizontalAlignment.Center,
                Visibility = Visibility.Collapsed,
            };
            _progressHost.Children.Add(_progressTrack);
            _progressHost.Children.Add(_progressFill);
            _progressHost.Children.Add(_progressText);

            var buttons = new Grid { HorizontalAlignment = HorizontalAlignment.Center };
            buttons.Children.Add(_button);
            buttons.Children.Add(_progressHost);
            Children.Add(_caption);
            Children.Add(buttons);
        }

        /// <summary>验收专用:按钮当前文案。</summary>
        public string ButtonText => _button.Content as string ?? string.Empty;

        /// <summary>
        /// 按面板状态刷新:按钮文案/显隐、进度条或不定态、未提取张数。
        /// </summary>
        /// <param name="panel">相似聚类面板视图模型</param>
        public void Refresh(SimilarityPanelViewModel panel)
        {
            _caption.Text = $"{panel.UnindexedCount} 张未提取";
            _caption.Visibility = panel.HasUnindexed ? Visibility.Visible : Visibility.Collapsed;
            _button.Content = panel.ActionButtonText;
            _button.Visibility = panel.ShowActionButton ? Visibility.Visible : Visibility.Collapsed;
            _progressHost.Visibility = panel.IsStateIndexing ? Visibility.Visible : Visibility.Collapsed;
            double total = Math.Max(1, panel.IndexTotal);
            double fraction = panel.IsIndexTotalUnknown ? 1 : Math.Clamp(panel.IndexProgress / total, 0, 1);
            _progressFill.Width = Math.Max(0, (WindowsLayout.SimilarityActionWidth - 2) * fraction);
            _progressText.Text = $"{panel.IndexProgress} / {panel.IndexTotal}";
        }
    }

    /// <summary>卡片工厂:虚拟化容器由 ItemsRepeater 负责,这里只做取用/回收与绑定。</summary>
    private sealed class ItemFactory : IElementFactory
    {
        private readonly WindowsSimilarityPanel _owner;

        /// <summary>
        /// 创建工厂。
        /// </summary>
        /// <param name="owner">所属面板</param>
        public ItemFactory(WindowsSimilarityPanel owner) => _owner = owner;

        /// <inheritdoc/>
        public UIElement GetElement(ElementFactoryGetArgs args)
        {
            // 这里是从原生 ItemsRepeater 调进来的 COM 边界:抛出去会变成 stowed exception 直接杀进程,
            // 所以必须自己兜住并给一个占位元素。
            try
            {
                if (args.Data is not SimilarityItem item)
                {
                    return new Border();
                }

                return _owner.RentCard(item);
            }
            catch (Exception ex)
            {
                NativeStartupLog.Fail("similarity card create", ex);
                return new Border();
            }
        }

        /// <inheritdoc/>
        public void RecycleElement(ElementFactoryRecycleArgs args)
        {
            try
            {
                if (args.Element is WindowsThumbnailCard card)
                {
                    _owner.ReturnCard(card);
                }
            }
            catch (Exception ex)
            {
                NativeStartupLog.Fail("similarity card recycle", ex);
            }
        }
    }
}
