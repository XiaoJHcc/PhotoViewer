using ReactiveUI;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive;
using PhotoViewer.Core;

namespace PhotoViewer.ViewModels;

// 排序方式
public enum SortMode { Name, Date, Star, Size }
public enum SortOrder { Ascending, Descending }

public class ThumbnailViewModel : ReactiveObject
{
    public MainViewModel Main { get; }
    
    // 布局方向（从主视图模型获取）
    public bool IsVerticalLayout => Main.Settings.HorizontalLayoutMode;
    
    // 排序方式
    private SortMode _sortMode = SortMode.Name;
    private SortOrder _sortOrder = SortOrder.Ascending;
    
    public List<ComboBoxItem> SortModes { get; } =
    [
        new() { DisplayName = "名称", Value = SortMode.Name },
        new() { DisplayName = "修改日期", Value = SortMode.Date },
        new() { DisplayName = "文件大小", Value = SortMode.Size }
    ];

    public List<ComboBoxItem> SortOrders { get; } =
    [
        new() { DisplayName = "升序 ↑", Value = SortOrder.Ascending },
        new() { DisplayName = "降序 ↓", Value = SortOrder.Descending }
    ];
        
    public SortMode SortMode
    {
        get => _sortMode;
        set => this.RaiseAndSetIfChanged(ref _sortMode, value);
    }
        
    public SortOrder SortOrder
    {
        get => _sortOrder;
        set => this.RaiseAndSetIfChanged(ref _sortOrder, value);
    }
        
    public ThumbnailViewModel(MainViewModel main)
    {
        Main = main;
        
        // 监听布局变化
        Main.WhenAnyValue(x => x.Settings.HorizontalLayoutMode)
            .Subscribe(_ => this.RaisePropertyChanged(nameof(IsVerticalLayout)));
    }

    public void SelectImageCommand(ImageFile file)
    {
        Console.WriteLine("SelectImageCommand");
        Main.CurrentFile = file;
    }
    
    // public void CenterCommand()
    // {
    //     Console.WriteLine("CenterCommand");
    //     if (Main.CurrentFile != null)
    //     {
    //         var index = Main.FilteredFiles.IndexOf(Main.CurrentFile);
    //         if (index >= 0)
    //         {
    //             // 实际滚动逻辑在视图层实现
    //             this.RaisePropertyChanged(nameof(ScrollToIndex));
    //         }
    //     }
    // }
    
    // 用于触发视图层滚动
    public int? ScrollToIndex { get; private set; }
        
    public void ScrollToCurrent()
    {
        if (Main.CurrentFile != null)
        {
            // var index = Main.FilteredFiles.IndexOf(Main.CurrentFile);
            var index = Main.FilteredFiles.IndexOf(
                Main.FilteredFiles.FirstOrDefault(f => f.File == Main.CurrentFile));
            
            if (index >= 0)
            {
                ScrollToIndex = index;
                this.RaisePropertyChanged(nameof(ScrollToIndex));
                ScrollToIndex = null; // 重置
            }
        }
    }
}