using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.VisualTree;
using PhotoViewer.ViewModels;
using System.Collections.Generic;
using Avalonia.Collections;
using Avalonia.Threading;
using Avalonia.Animation;

namespace PhotoViewer.Views;

public partial class FileSettingsView : UserControl
{
    private SettingsViewModel? ViewModel => DataContext as SettingsViewModel;
    private Border? _draggedItem;
    private Panel? _dragLayer;
    private Border? _dragGhost;
    private int _draggedIndex = -1;
    private int _currentDropIndex = -1;
    private Point _startPoint;
    private Point _dragOffset;
    private bool _isDragging;
    private List<Border> _itemElements = new(); // 缓存的UI元素列表
    private int _originalDraggedIndex = -1; // 原始拖拽索引
    private const double ITEM_HEIGHT = 44; // 固定项目高度
    private IPointer? _capturedPointer; // 缓存捕获的指针
    private TopLevel? _topLevel; // 缓存顶层控件用于全局事件监听
    private bool _preventCaptureLoss = false; // 防止重排时丢失捕获

    public FileSettingsView()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private void OnLoaded(object? sender, RoutedEventArgs e)
    {
        if (FileFormatList != null)
        {
            AttachDragHandlers();
            // 监听布局更新以重新附加事件处理器
            FileFormatList.LayoutUpdated += OnLayoutUpdated;
        }
        
        // 缓存顶层控件
        _topLevel = this.GetVisualRoot() as TopLevel;
    }

    private void OnLayoutUpdated(object? sender, EventArgs e)
    {
        AttachDragHandlers();
    }

    private void AttachDragHandlers()
    {
        // 为所有拖拽把手附加处理器
        foreach (var dragHandle in GetAllDragHandles())
        {
            // 移除旧的事件处理器避免重复
            dragHandle.PointerPressed -= OnDragHandlePressed;
            
            // 添加新的事件处理器
            dragHandle.PointerPressed += OnDragHandlePressed;
        }
    }

    private void OnDragHandlePressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is Border handle)
        {
            var point = e.GetCurrentPoint(this);
            // 支持左键点击和触摸
            if (point.Properties.IsLeftButtonPressed || point.Pointer.Type == PointerType.Touch)
            {
                _startPoint = point.Position;
                _draggedItem = handle.FindAncestorOfType<Border>();
                _draggedIndex = GetItemIndex(_draggedItem);
                _capturedPointer = e.Pointer;
                
                // 计算鼠标相对于把手的偏移
                var handlePosition = handle.TranslatePoint(new Point(0, 0), this) ?? new Point(0, 0);
                _dragOffset = new Point(_startPoint.X - handlePosition.X, _startPoint.Y - handlePosition.Y);
                
                // 对于触摸设备，直接捕获到TopLevel以获得更稳定的事件处理
                if (e.Pointer.Type == PointerType.Touch && _topLevel != null)
                {
                    e.Pointer.Capture(_topLevel);
                }
                else
                {
                    // 桌面设备使用原有逻辑
                    e.Pointer.Capture(handle);
                }
                
                // 在顶层控件上监听全局事件，确保即使离开把手区域也能继续接收事件
                if (_topLevel != null)
                {
                    _topLevel.PointerMoved += OnGlobalPointerMoved;
                    _topLevel.PointerReleased += OnGlobalPointerReleased;
                    _topLevel.PointerCaptureLost += OnGlobalPointerCaptureLost;
                }
                
                e.Handled = true;
            }
        }
    }

    private void OnGlobalPointerMoved(object? sender, PointerEventArgs e)
    {
        // 只处理被捕获的指针
        if (_capturedPointer == null || e.Pointer != _capturedPointer || _draggedItem == null)
            return;

        var currentPoint = e.GetCurrentPoint(this);
        
        // 对于触摸设备，检查指针是否仍然按下
        if (currentPoint.Pointer.Type == PointerType.Touch)
        {
            // 触摸设备可能没有 IsLeftButtonPressed 状态，所以在拖拽开始后继续处理
            if (!_isDragging)
            {
                var distance = Math.Abs(currentPoint.Position.X - _startPoint.X) + 
                              Math.Abs(currentPoint.Position.Y - _startPoint.Y);
                if (distance > 5)
                {
                    StartDrag(currentPoint);
                }
            }
        }
        else
        {
            // 鼠标设备检查按钮状态
            if (!currentPoint.Properties.IsLeftButtonPressed)
            {
                // 如果鼠标按钮已松开，结束拖拽
                CompleteDragAndReset();
                return;
            }
            
            if (!_isDragging)
            {
                var distance = Math.Abs(currentPoint.Position.X - _startPoint.X) + 
                              Math.Abs(currentPoint.Position.Y - _startPoint.Y);
                if (distance > 5)
                {
                    StartDrag(currentPoint);
                }
            }
        }

        if (_isDragging)
        {
            UpdateDragPosition(currentPoint);
            UpdateDropIndicator(currentPoint);
        }
    }

    private void OnGlobalPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        Console.WriteLine("OnGlobalPointerReleased");
        
        // 只处理被捕获的指针
        if (_capturedPointer == null || e.Pointer != _capturedPointer)
            return;
        
        Console.WriteLine("OnGlobalPointerReleased -> CompleteDragAndReset");
        
        CompleteDragAndReset();
        e.Handled = true;
    }

    private void OnGlobalPointerCaptureLost(object? sender, PointerCaptureLostEventArgs e)
    {
        Console.WriteLine("OnGlobalPointerCaptureLost");
        
        // 如果是在防止捕获丢失期间，忽略这个事件
        if (_preventCaptureLoss)
        {
            Console.WriteLine("OnGlobalPointerCaptureLost -> Ignored (preventCaptureLoss)");
            return;
        }
        
        // 指针捕获丢失时清理拖拽状态
        if (_capturedPointer != null && e.Pointer == _capturedPointer)
        {
            Console.WriteLine("OnGlobalPointerCaptureLost -> CompleteDragAndReset");
            
            CompleteDragAndReset();
        }
    }

    private void CompleteDragAndReset()
    {
        Console.WriteLine("CompleteDragAndReset" + (_isDragging ? " (was dragging)" : " (was not dragging)"));
        
        if (_isDragging)
        {
            CompleteDrag();
        }
        
        ResetDrag();
    }

    private void StartDrag(PointerPoint point)
    {
        _isDragging = true;
        
        if (_draggedItem == null || ViewModel == null) return;

        // 缓存当前的UI元素列表
        _itemElements = GetAllFormatItems().ToList();
        _originalDraggedIndex = _draggedIndex;
        _currentDropIndex = _draggedIndex;
        
        // 将原始项转换为空位显示（这个项将始终保持空位状态）
        ShowAsDropPlaceholder(_draggedItem);
        
        // 创建拖拽层和幽灵
        CreateDragLayer();
        CreateDragGhost(point);
    }

    private void CreateDragLayer()
    {
        // 在当前控件上创建覆盖层
        if (this.GetVisualRoot() is TopLevel topLevel)
        {
            _dragLayer = new Canvas
            {
                Background = Brushes.Transparent,
                IsHitTestVisible = false,
                ZIndex = 1000
            };

            // 添加到窗口的 OverlayLayer
            var overlayLayer = OverlayLayer.GetOverlayLayer(topLevel);
            if (overlayLayer != null)
            {
                overlayLayer.Children.Add(_dragLayer);
            }
        }
    }

    private void CreateDragGhost(PointerPoint point)
    {
        if (_draggedItem == null || _dragLayer == null || ViewModel == null) return;

        var formatItem = ViewModel.FileFormats[_draggedIndex];
        
        // 获取原始元素的尺寸
        var bounds = _draggedItem.Bounds;
        
        // 创建拖拽幽灵
        _dragGhost = new Border
        {
            Classes = { "DragItem", "Dragging" },
            Width = bounds.Width,
            Height = bounds.Height,
            Child = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"),
                Children =
                {
                    new CheckBox 
                    { 
                        IsChecked = formatItem.IsEnabled, 
                        IsEnabled = false, 
                        Margin = new Thickness(0,0,8,0),
                        [Grid.ColumnProperty] = 0
                    },
                    new TextBlock 
                    { 
                        Text = formatItem.Name, 
                        VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center, 
                        FontWeight = FontWeight.Medium,
                        [Grid.ColumnProperty] = 1
                    },
                    new Border 
                    { 
                        Width = 20, 
                        Padding = new Thickness(4),
                        [Grid.ColumnProperty] = 2,
                        Child = new Path
                        {
                            Fill = Brushes.Gray,
                            Data = Geometry.Parse("M3,15H21V13H3V15M3,19H21V17H3V19M3,11H21V9H3V11M3,7H21V5H3V7Z"),
                            Stretch = Stretch.Uniform,
                            Width = 12,
                            Height = 12
                        }
                    }
                }
            }
        };

        // 设置初始位置，让把手对准鼠标
        UpdateDragGhostPosition(point);
        
        _dragLayer.Children.Add(_dragGhost);
    }

    private void UpdateDragPosition(PointerPoint point)
    {
        UpdateDragGhostPosition(point);
    }

    private void UpdateDragGhostPosition(PointerPoint point)
    {
        if (_dragGhost == null || _draggedItem == null) return;

        // 获取鼠标相对于根视觉的位置
        var rootPosition = point.Position;
        if (this.GetVisualRoot() is Visual root)
        {
            var transform = this.TransformToVisual(root);
            if (transform != null)
            {
                rootPosition = point.Position.Transform(transform.Value);
            }
        }

        // 计算把手应该在的位置（让把手对准鼠标）
        var handleOffsetX = _draggedItem.Bounds.Width - 20; // 把手在右侧
        var handleOffsetY = _draggedItem.Bounds.Height / 2; // 把手在中间

        var ghostX = rootPosition.X - handleOffsetX - _dragOffset.X;
        var ghostY = rootPosition.Y - handleOffsetY - _dragOffset.Y;

        Canvas.SetLeft(_dragGhost, ghostX);
        Canvas.SetTop(_dragGhost, ghostY);
    }

    private void UpdateDropIndicator(PointerPoint point)
    {
        var targetIndex = GetDropTargetIndex(point.Position);
        
        if (targetIndex >= 0 && targetIndex != _currentDropIndex)
        {
            // 直接同步执行，避免异步问题
            ReorderItemsVisually(targetIndex);
            _currentDropIndex = targetIndex;
        }
    }

    private void ReorderItemsVisually(int targetIndex)
    {
        if (_originalDraggedIndex < 0 || _itemElements.Count == 0) return;

        // 设置标志防止重排期间的捕获丢失
        _preventCaptureLoss = true;

        try
        {
            // 批量更新Transform，避免中间状态闪烁
            var transforms = new Dictionary<Border, TranslateTransform>();
            var placeholderStates = new Dictionary<Border, bool>();
            
            for (int i = 0; i < _itemElements.Count; i++)
            {
                var item = _itemElements[i];
                
                if (i == _originalDraggedIndex)
                {
                    // 被拖拽的项始终保持空位状态，移动到目标位置
                    var newPosition = (targetIndex - _originalDraggedIndex) * ITEM_HEIGHT;
                    transforms[item] = new TranslateTransform(0, newPosition);
                    placeholderStates[item] = true; // 标记为占位符
                }
                else
                {
                    // 其他项的重排逻辑
                    var newPosition = CalculateNewPosition(i, targetIndex);
                    transforms[item] = new TranslateTransform(0, newPosition);
                    placeholderStates[item] = false; // 标记为正常显示
                }
            }
            
            // 先更新所有Transform
            foreach (var kvp in transforms)
            {
                kvp.Key.RenderTransform = kvp.Value;
            }
            
            // 然后更新所有状态
            foreach (var kvp in placeholderStates)
            {
                if (kvp.Value)
                {
                    ShowAsDropPlaceholder(kvp.Key);
                }
                else
                {
                    RestoreNormalAppearance(kvp.Key);
                }
            }

            // 在Android上，重新确保指针捕获
            if (_capturedPointer != null && _capturedPointer.Type == PointerType.Touch)
            {
                // 延迟重新捕获，确保UI更新完成
                Dispatcher.UIThread.Post(() =>
                {
                    try
                    {
                        if (_capturedPointer != null && _isDragging)
                        {
                            // 尝试重新捕获到TopLevel，确保不会丢失
                            if (_topLevel != null)
                            {
                                _capturedPointer.Capture(_topLevel);
                            }
                        }
                    }
                    finally
                    {
                        _preventCaptureLoss = false;
                    }
                }, DispatcherPriority.Background);
            }
            else
            {
                _preventCaptureLoss = false;
            }
        }
        catch
        {
            _preventCaptureLoss = false;
            throw;
        }
    }

    private double CalculateNewPosition(int currentIndex, int targetIndex)
    {
        var originalIndex = _originalDraggedIndex;
        
        // 其他项的重排逻辑
        if (originalIndex < targetIndex)
        {
            // 向下拖拽：原位置之后到目标位置之间的项向上移动
            if (currentIndex > originalIndex && currentIndex <= targetIndex)
            {
                return -ITEM_HEIGHT;
            }
        }
        else if (originalIndex > targetIndex)
        {
            // 向上拖拽：目标位置到原位置之前的项向下移动
            if (currentIndex >= targetIndex && currentIndex < originalIndex)
            {
                return ITEM_HEIGHT;
            }
        }
        
        // 其他项保持原位
        return 0;
    }

    private void ShowAsDropPlaceholder(Border item)
    {
        // 只有在还没有设置为占位符时才设置
        if (item.Tag?.ToString() == "placeholder") return;
        
        // 批处理样式更改以减少重绘
        item.BeginInit();
        
        // 将项目变为虚线框显示
        item.Opacity = 1.0;
        item.Background = Brushes.Transparent;
        
        // 设置虚线边框
        if (Application.Current?.TryGetResource("SystemControlForegroundAccentBrush", out var resource) == true 
            && resource is IBrush brush)
        {
            item.BorderBrush = brush;
        }
        else
        {
            item.BorderBrush = new SolidColorBrush(Colors.Gray);
        }
        
        item.BorderThickness = new Thickness(2);
        
        // 隐藏原始内容并添加虚线矩形
        if (item.Child is Grid originalGrid)
        {
            originalGrid.Opacity = 0;
            
            // 检查是否已经添加了虚线矩形
            var existingRect = originalGrid.Children.OfType<Rectangle>().FirstOrDefault();
            if (existingRect == null)
            {
                // 创建虚线边框效果
                var rect = new Rectangle
                {
                    Stroke = item.BorderBrush,
                    StrokeThickness = 2,
                    StrokeDashArray = new AvaloniaList<double> { 4, 4 },
                    Fill = Brushes.Transparent,
                    HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
                    VerticalAlignment = Avalonia.Layout.VerticalAlignment.Stretch
                };
                
                originalGrid.Children.Add(rect);
            }
            
            item.Tag = "placeholder";
        }
        
        item.EndInit();
    }

    private void RestoreNormalAppearance(Border item)
    {
        // 只有当前是占位符状态时才恢复
        if (item.Tag?.ToString() != "placeholder") return;
        
        // 批处理样式更改以减少重绘
        item.BeginInit();
        
        // 恢复正常显示
        item.Opacity = 1.0;
        item.ClearValue(Border.BackgroundProperty);
        item.ClearValue(Border.BorderBrushProperty);
        item.ClearValue(Border.BorderThicknessProperty);
        
        // 恢复原始内容显示
        if (item.Child is Grid grid)
        {
            grid.Opacity = 1.0;
            
            // 移除虚线矩形（如果存在）
            var rect = grid.Children.OfType<Rectangle>().FirstOrDefault();
            if (rect != null)
            {
                grid.Children.Remove(rect);
            }
            
            item.Tag = null;
        }
        
        item.EndInit();
    }

    private void CompleteDrag()
    {
        // 拖拽完成，现在更新数据模型
        if (_currentDropIndex >= 0 && _currentDropIndex != _originalDraggedIndex && ViewModel != null)
        {
            ViewModel.MoveFileFormat(_originalDraggedIndex, _currentDropIndex);
        }
        
        // 清除所有Transform，让数据绑定接管
        ResetAllTransforms();
    }

    private void ResetAllTransforms()
    {
        foreach (var item in _itemElements)
        {
            item.RenderTransform = null;
            RestoreNormalAppearance(item);
        }
    }

    private void ResetDrag()
    {
        // 重置防止捕获丢失标志
        _preventCaptureLoss = false;

        // 移除全局事件监听
        if (_topLevel != null)
        {
            _topLevel.PointerMoved -= OnGlobalPointerMoved;
            _topLevel.PointerReleased -= OnGlobalPointerReleased;
            _topLevel.PointerCaptureLost -= OnGlobalPointerCaptureLost;
        }

        // 恢复所有项目的正常显示和位置
        if (_itemElements.Count > 0)
        {
            ResetAllTransforms();
        }
        else
        {
            // 如果缓存的元素列表为空，尝试获取当前所有元素
            var allItems = GetAllFormatItems().ToList();
            foreach (var item in allItems)
            {
                item.RenderTransform = null;
                RestoreNormalAppearance(item);
            }
        }
        
        // 移除拖拽层
        if (_dragLayer != null)
        {
            if (this.GetVisualRoot() is TopLevel topLevel)
            {
                var overlayLayer = OverlayLayer.GetOverlayLayer(topLevel);
                overlayLayer?.Children.Remove(_dragLayer);
            }
        }
        
        // 在 Avalonia 11.x 中释放指针捕获
        if (_capturedPointer != null)
        {
            _capturedPointer.Capture(null);
        }
        
        _draggedItem = null;
        _dragLayer = null;
        _dragGhost = null;
        _draggedIndex = -1;
        _currentDropIndex = -1;
        _originalDraggedIndex = -1;
        _isDragging = false;
        _dragOffset = new Point(0, 0);
        _capturedPointer = null;
        _itemElements.Clear();
    }

    private int GetItemIndex(Border? item)
    {
        if (item == null) return -1;
        
        // 在拖拽过程中使用缓存的元素列表
        if (_isDragging && _itemElements.Count > 0)
        {
            return _itemElements.IndexOf(item);
        }
        
        // 正常情况下获取索引
        var items = GetAllFormatItems().ToList();
        return items.IndexOf(item);
    }

    private int GetDropTargetIndex(Point position)
    {
        if (ViewModel == null || _itemElements.Count == 0) return -1;
        
        // 将屏幕坐标转换为列表内的相对坐标
        var listPosition = position;
        if (FileFormatList != null)
        {
            var transform = this.TransformToVisual(FileFormatList);
            if (transform != null)
            {
                listPosition = position.Transform(transform.Value);
            }
        }
        
        // 考虑滚动偏移
        var scrollViewer = this.FindDescendantOfType<ScrollViewer>();
        if (scrollViewer != null)
        {
            listPosition = new Point(listPosition.X, listPosition.Y + scrollViewer.Offset.Y);
        }
        
        // 获取列表容器的起始位置
        var firstItem = _itemElements.FirstOrDefault();
        if (firstItem == null) return -1;
        
        var listStartY = firstItem.Bounds.Y;
        
        // 计算相对于列表起始位置的偏移
        var relativeY = listPosition.Y - listStartY;
        
        var targetIndex = (int)Math.Floor(relativeY / ITEM_HEIGHT);
        
        // 确保索引在有效范围内
        targetIndex = Math.Max(0, Math.Min(targetIndex, _itemElements.Count - 1));
        
        return targetIndex;
    }

    private System.Collections.Generic.IEnumerable<Border> GetAllFormatItems()
    {
        return FileFormatList?.GetVisualDescendants()
            .OfType<Border>()
            .Where(b => b.Name == "FormatItem") ?? Enumerable.Empty<Border>();
    }

    private System.Collections.Generic.IEnumerable<Border> GetAllDragHandles()
    {
        return FileFormatList?.GetVisualDescendants()
            .OfType<Border>()
            .Where(b => b.Name == "DragHandle") ?? Enumerable.Empty<Border>();
    }
}