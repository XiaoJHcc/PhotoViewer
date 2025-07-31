using Avalonia.Controls;
using Avalonia.Threading;
using ReactiveUI;
using System;
using System.Threading.Tasks;
using System.Globalization;
using System.Linq;
using System.Reactive.Linq;
using Avalonia;
using Avalonia.Data.Converters;
using Avalonia.Media;
using PhotoViewer.ViewModels;

namespace PhotoViewer.Views;

public partial class ThumbnailView : UserControl
{
    private readonly DispatcherTimer _scrollTimer = new DispatcherTimer();
    public ThumbnailViewModel? ViewModel => DataContext as ThumbnailViewModel;

    public ThumbnailView()
    {
        InitializeComponent();

        // 设置滚动计时器（300ms延迟）
        _scrollTimer.Interval = TimeSpan.FromMilliseconds(300);
        _scrollTimer.Tick += async (s, e) =>
        {
            _scrollTimer.Stop();
            await LoadVisibleThumbnailsAsync();
        };

        // 滚动事件处理
        ThumbnailScrollViewer.ScrollChanged += OnScrollChanged;

        // 排序选项变化时刷新
        SortByComboBox.SelectionChanged += (s, e) => LoadVisibleThumbnailsAsync();
        OrderComboBox.SelectionChanged += (s, e) => LoadVisibleThumbnailsAsync();

        // 监听数据上下文变化
        // this.WhenAnyValue(x => x.DataContext)
        //     .Where(dc => dc is ThumbnailViewModel)
        //     .Subscribe(_ => InitializeViewModel());

        // 在 View 中监听 ViewModel 传来的 ScrollToIndex 更新
        this.WhenAnyValue(x => x.ViewModel.ScrollToIndex)
            .Where(index => index.HasValue)
            .Subscribe(index =>
            {
                if (index.Value < ThumbnailItemsControl.ItemCount)
                {
                    ThumbnailItemsControl.ScrollIntoView(index.Value);
                }
            });
        
        // 添加命令绑定
        // this.WhenActivated(disposables =>
        // {
        //     this.BindCommand(
        //             ViewModel, 
        //             vm => vm.SelectImageCommand, 
        //             v => v.ThumbnailItemsControl,
        //             nameof(ThumbnailItemsControl.SelectionChanged))
        //         .DisposeWith(disposables);
        // });
    }

    // private void InitializeViewModel()
    // {
    //     if (ViewModel != null)
    //     {
    //         // 初始加载可见缩略图
    //         Dispatcher.UIThread.Post(async () => 
    //         {
    //             await Task.Delay(100); // 等待布局完成
    //             await LoadVisibleThumbnailsAsync();
    //         });
    //     }
    // }

    private void OnScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        // 使用计时器延迟加载，避免频繁滚动时重复加载
        _scrollTimer.Stop();
        _scrollTimer.Start();
    }

    private async Task LoadVisibleThumbnailsAsync()
    {
        if (ViewModel == null) return;

        try
        {
            var scrollViewer = ThumbnailScrollViewer;
            if (scrollViewer == null) return;

            // 计算可见区域
            double startX = scrollViewer.Offset.X;
            double endX = startX + scrollViewer.Viewport.Width;

            // 加载可见区域及附近区域的缩略图
            for (int i = 0; i < ViewModel.Main.FilteredFiles.Count; i++)
            {
                var item = ViewModel.Main.FilteredFiles[i];
                if (item.Thumbnail != null) continue; // 已经加载过

                // 获取该项在列表中的位置
                var container = ThumbnailItemsControl.ItemContainerGenerator.ContainerFromIndex(i) as Control;
                if (container == null) continue;

                // 计算该项的位置
                var position = container.TranslatePoint(new Point(), ThumbnailItemsControl) ?? new Point();
                double itemStartX = position.X;
                double itemEndX = itemStartX + container.Bounds.Width;

                // 检查是否在可见区域附近（左右各加200像素缓冲区）
                if (itemEndX >= startX - 200 && itemStartX <= endX + 200)
                {
                    await item.LoadThumbnailAsync();
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"加载可见缩略图失败: {ex.Message}");
        }
    }

    private void CenterButton_Click(object sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (ViewModel?.Main?.CurrentFile != null)
        {
            var currentItem = ViewModel.Main.FilteredFiles.FirstOrDefault(f => f.File == ViewModel.Main.CurrentFile);

            if (currentItem != null)
            {
                ThumbnailItemsControl.ScrollIntoView(currentItem);
            }
        }
    }
}

// 布尔到边框颜色转换器：当前项边框高亮
public class BoolToCurrentBorderConverter : IValueConverter
{
    public static readonly BoolToCurrentBorderConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is bool isCurrent && isCurrent)
        {
            return new SolidColorBrush(Colors.DodgerBlue);
        }

        return new SolidColorBrush(Colors.Transparent);
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}

// 文件大小转换器
public class FileSizeConverter : IValueConverter
{
    public static readonly FileSizeConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is ulong bytes)
        {
            string[] sizes = { "B", "KB", "MB", "GB" };
            int order = 0;
            double size = bytes;

            while (size >= 1024 && order < sizes.Length - 1)
            {
                order++;
                size /= 1024;
            }

            return $"{size:0.##} {sizes[order]}";
        }

        return "0 B";
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}

// 日期格式转换器
public class DateTimeConverter : IValueConverter
{
    public static readonly DateTimeConverter Instance = new();
        
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is DateTimeOffset dateTime)
        {
            return dateTime.ToString("MM-dd HH:mm");
        }
        return string.Empty;
    }
        
    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}