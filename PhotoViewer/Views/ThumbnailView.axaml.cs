using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Data.Converters;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Interactivity;
using Avalonia.Threading;
using PhotoViewer.Service;
using PhotoViewer.ViewModels;

namespace PhotoViewer.Views;

    public partial class ThumbnailView : UserControl
    {
        public event EventHandler<IStorageFile>? ThumbnailSelected;
        
        private readonly DispatcherTimer _scrollTimer = new DispatcherTimer();
        public ThumbnailViewModel? ViewModel => DataContext as ThumbnailViewModel;
        
        public ThumbnailView()
        {
            InitializeComponent();
            
            // 设置数据上下文
            DataContext = new ThumbnailViewModel();
            
            // 排序选项变化时刷新
            SortByComboBox.SelectionChanged += (s, e) => RefreshThumbnails();
            OrderComboBox.SelectionChanged += (s, e) => RefreshThumbnails();
            
            // 复位按钮点击
            CenterButton.Click += (s, e) => ScrollToCurrent();
            
            // 滚动事件处理
            ThumbnailScrollViewer.ScrollChanged += OnScrollChanged;
            
            // 设置滚动计时器（300ms延迟）
            _scrollTimer.Interval = TimeSpan.FromMilliseconds(300);
            _scrollTimer.Tick += async (s, e) => {
                _scrollTimer.Stop();
                if (ViewModel != null)
                {
                    await ViewModel.LoadVisibleThumbnails();
                }
            };
        }
        
        public async Task SetFiles(IEnumerable<IStorageFile> files, IStorageFile? currentFile = null)
        {
            if (ViewModel != null)
            {
                await ViewModel.SetFiles(files, currentFile);
            }
        }
        
        private void OnScrollChanged(object? sender, ScrollChangedEventArgs e)
        {
            // 使用计时器延迟加载，避免频繁滚动时重复加载
            _scrollTimer.Stop();
            _scrollTimer.Start();
        }
        
        private void RefreshThumbnails()
        {
            if (ViewModel != null)
            {
                ViewModel.RefreshThumbnails(
                    SortByComboBox.SelectedIndex,
                    OrderComboBox.SelectedIndex
                );
            }
        }
        
        public void ScrollToCurrent()
        {
            if (ViewModel != null && ViewModel.CurrentFile != null)
            {
                ThumbnailItemsControl.ScrollIntoView(
                    ViewModel.ThumbnailItems.FirstOrDefault(t => 
                        t.File.Path.AbsolutePath == ViewModel.CurrentFile.Path.AbsolutePath
                    )
                );
            }
        }
        
        private void Thumbnail_PointerPressed(object sender, PointerPressedEventArgs e)
        {
            if (e.Source is TextBlock textBlock && textBlock.Tag is IStorageFile file)
            {
                ThumbnailSelected?.Invoke(this, file);
                e.Handled = true;
            }
        }
        
        public void SetCurrentFile(IStorageFile file)
        {
            if (ViewModel != null)
            {
                ViewModel.SetCurrentFile(file);
            }
        }
        
        private void CenterButton_Click(object sender, RoutedEventArgs e)
        {
            ScrollToCurrent();
        }
        
    }

    // 转换器：当前项边框高亮
    public class BoolToCurrentBorderConverter : IValueConverter
    {
        public static readonly BoolToCurrentBorderConverter Instance = new();
            
        public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            return value is true ? Brushes.DodgerBlue : Brushes.Transparent;
        }
            
        public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            throw new NotSupportedException();
        }
    }
