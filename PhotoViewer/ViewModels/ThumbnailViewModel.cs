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
    
    // 布局方向（从主视图模型获取实际布局状态）
    public bool IsVerticalLayout => Main.IsHorizontalLayout;
    
    // 排序方式
    private SortMode _sortMode = SortMode.Name;
    private SortOrder _sortOrder = SortOrder.Ascending;
    
    // // 滚动到当前图片的命令
    // public ReactiveCommand<Unit, Unit> ScrollToCurrentCommand { get; }
    
    // 滚动到当前图片的事件
    public event Action? ScrollToCurrentRequested;
    
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
        
        // 初始化滚动命令
        // ScrollToCurrentCommand = ReactiveCommand.Create(ScrollToCurrent);
        
        // 监听布局变化
        Main.WhenAnyValue(x => x.IsHorizontalLayout)
            .Subscribe(_ => this.RaisePropertyChanged(nameof(IsVerticalLayout)));
    }

    public void SelectImageCommand(ImageFile file)
    {
        Main.CurrentFile = file;
    }
    
    public void ScrollToCurrent()
    {
        ScrollToCurrentRequested?.Invoke();
    }
}