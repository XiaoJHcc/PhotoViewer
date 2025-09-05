using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Input;
using Avalonia;
using Avalonia.Collections;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace PhotoViewer.Controls;

public partial class SortableList : UserControl
{
    // 依赖属性：数据源
    public static readonly StyledProperty<IEnumerable?> ItemsSourceProperty =
        AvaloniaProperty.Register<SortableList, IEnumerable?>(nameof(ItemsSource));

    public IEnumerable? ItemsSource
    {
        get => GetValue(ItemsSourceProperty);
        set => SetValue(ItemsSourceProperty, value);
    }

    // 依赖属性：内容模板
    public static readonly StyledProperty<IDataTemplate?> ItemTemplateProperty =
        AvaloniaProperty.Register<SortableList, IDataTemplate?>(nameof(ItemTemplate));

    public IDataTemplate? ItemTemplate
    {
        get => GetValue(ItemTemplateProperty);
        set => SetValue(ItemTemplateProperty, value);
    }

    // 依赖属性：移动命令
    public static readonly StyledProperty<ICommand?> MoveCommandProperty =
        AvaloniaProperty.Register<SortableList, ICommand?>(nameof(MoveCommand));

    public ICommand? MoveCommand
    {
        get => GetValue(MoveCommandProperty);
        set => SetValue(MoveCommandProperty, value);
    }

    // 拖拽相关私有字段
    private Border? _draggedItem;
    private Panel? _dragLayer;
    private Border? _dragGhost;
    private int _draggedIndex = -1;
    private int _currentDropIndex = -1;
    private Point _startPoint;
    private Point _dragOffset;
    private bool _isDragging;
    private List<Border> _itemElements = new();
    private int _originalDraggedIndex = -1;
    private const double ITEM_HEIGHT = 44;
    private IPointer? _capturedPointer;
    private TopLevel? _topLevel;
    private bool _preventCaptureLoss = false;

    public SortableList()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private void OnLoaded(object? sender, RoutedEventArgs e)
    {
        if (ItemsList != null)
        {
            AttachDragHandlers();
            ItemsList.LayoutUpdated += OnLayoutUpdated;
        }
        
        _topLevel = this.GetVisualRoot() as TopLevel;
    }

    private void OnLayoutUpdated(object? sender, EventArgs e)
    {
        AttachDragHandlers();
    }

    private void AttachDragHandlers()
    {
        foreach (var dragHandle in GetAllDragHandles())
        {
            dragHandle.PointerPressed -= OnDragHandlePressed;
            dragHandle.PointerPressed += OnDragHandlePressed;
        }
    }

    private void OnDragHandlePressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is Border handle)
        {
            var point = e.GetCurrentPoint(this);
            if (point.Properties.IsLeftButtonPressed || point.Pointer.Type == PointerType.Touch)
            {
                _startPoint = point.Position;
                _draggedItem = handle.FindAncestorOfType<Border>();
                _draggedIndex = GetItemIndex(_draggedItem);
                _capturedPointer = e.Pointer;
                
                var handlePosition = handle.TranslatePoint(new Point(0, 0), this) ?? new Point(0, 0);
                _dragOffset = new Point(_startPoint.X - handlePosition.X, _startPoint.Y - handlePosition.Y);
                
                if (e.Pointer.Type == PointerType.Touch && _topLevel != null)
                {
                    e.Pointer.Capture(_topLevel);
                }
                else
                {
                    e.Pointer.Capture(handle);
                }
                
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
        if (_capturedPointer == null || e.Pointer != _capturedPointer || _draggedItem == null)
            return;

        var currentPoint = e.GetCurrentPoint(this);
        
        if (currentPoint.Pointer.Type == PointerType.Touch)
        {
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
            if (!currentPoint.Properties.IsLeftButtonPressed)
            {
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
        if (_capturedPointer == null || e.Pointer != _capturedPointer)
            return;
        
        CompleteDragAndReset();
        e.Handled = true;
    }

    private void OnGlobalPointerCaptureLost(object? sender, PointerCaptureLostEventArgs e)
    {
        if (_preventCaptureLoss)
        {
            return;
        }
        
        if (_capturedPointer != null && e.Pointer == _capturedPointer)
        {
            CompleteDragAndReset();
        }
    }

    private void CompleteDragAndReset()
    {
        if (_isDragging)
        {
            CompleteDrag();
        }
        
        ResetDrag();
    }

    private void StartDrag(PointerPoint point)
    {
        _isDragging = true;
        
        if (_draggedItem == null) return;

        _itemElements = GetAllSortableItems().ToList();
        _originalDraggedIndex = _draggedIndex;
        _currentDropIndex = _draggedIndex;
        
        ShowAsDropPlaceholder(_draggedItem);
        
        CreateDragLayer();
        CreateDragGhost(point);
    }

    private void CreateDragLayer()
    {
        if (this.GetVisualRoot() is TopLevel topLevel)
        {
            _dragLayer = new Canvas
            {
                Background = Brushes.Transparent,
                IsHitTestVisible = false,
                ZIndex = 1000
            };

            var overlayLayer = OverlayLayer.GetOverlayLayer(topLevel);
            if (overlayLayer != null)
            {
                overlayLayer.Children.Add(_dragLayer);
            }
        }
    }

    private void CreateDragGhost(PointerPoint point)
    {
        if (_draggedItem == null || _dragLayer == null || ItemsSource == null) return;

        var items = ItemsSource.Cast<object>().ToList();
        if (_draggedIndex < 0 || _draggedIndex >= items.Count) return;
        
        var dataItem = items[_draggedIndex];
        var bounds = _draggedItem.Bounds;
        
        _dragGhost = new Border
        {
            Classes = { "DragItem", "Dragging" },
            Width = bounds.Width - 20,
            Height = bounds.Height,
            Child = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("*,Auto"),
                Children =
                {
                    new ContentPresenter
                    {
                        Content = dataItem,
                        ContentTemplate = ItemTemplate,
                        VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                        [Grid.ColumnProperty] = 0
                    },
                    new Border 
                    { 
                        Width = 20, 
                        Padding = new Thickness(4),
                        [Grid.ColumnProperty] = 1,
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

        var rootPosition = point.Position;
        if (this.GetVisualRoot() is Visual root)
        {
            var transform = this.TransformToVisual(root);
            if (transform != null)
            {
                rootPosition = point.Position.Transform(transform.Value);
            }
        }

        var handleOffsetX = _draggedItem.Bounds.Width - 40;
        var handleOffsetY = 0; //_draggedItem.Bounds.Height / 2;

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
            ReorderItemsVisually(targetIndex);
            _currentDropIndex = targetIndex;
        }
    }

    // ...existing drag visual methods (ReorderItemsVisually, ShowAsDropPlaceholder, etc.)...
    private void ReorderItemsVisually(int targetIndex)
    {
        if (_originalDraggedIndex < 0 || _itemElements.Count == 0) return;

        _preventCaptureLoss = true;

        try
        {
            var transforms = new Dictionary<Border, TranslateTransform>();
            var placeholderStates = new Dictionary<Border, bool>();
            
            for (int i = 0; i < _itemElements.Count; i++)
            {
                var item = _itemElements[i];
                
                if (i == _originalDraggedIndex)
                {
                    var newPosition = (targetIndex - _originalDraggedIndex) * ITEM_HEIGHT;
                    transforms[item] = new TranslateTransform(0, newPosition);
                    placeholderStates[item] = true;
                }
                else
                {
                    var newPosition = CalculateNewPosition(i, targetIndex);
                    transforms[item] = new TranslateTransform(0, newPosition);
                    placeholderStates[item] = false;
                }
            }
            
            foreach (var kvp in transforms)
            {
                kvp.Key.RenderTransform = kvp.Value;
            }
            
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

            if (_capturedPointer != null && _capturedPointer.Type == PointerType.Touch)
            {
                Dispatcher.UIThread.Post(() =>
                {
                    try
                    {
                        if (_capturedPointer != null && _isDragging && _topLevel != null)
                        {
                            _capturedPointer.Capture(_topLevel);
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
        
        if (originalIndex < targetIndex)
        {
            if (currentIndex > originalIndex && currentIndex <= targetIndex)
            {
                return -ITEM_HEIGHT;
            }
        }
        else if (originalIndex > targetIndex)
        {
            if (currentIndex >= targetIndex && currentIndex < originalIndex)
            {
                return ITEM_HEIGHT;
            }
        }
        
        return 0;
    }

    private void ShowAsDropPlaceholder(Border item)
    {
        if (item.Tag?.ToString() == "placeholder") return;
        
        item.BeginInit();
        
        item.Opacity = 1.0;
        item.Background = Brushes.Transparent;
        
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
        
        if (item.Child is Grid originalGrid)
        {
            originalGrid.Opacity = 0;
            
            var existingRect = originalGrid.Children.OfType<Rectangle>().FirstOrDefault();
            if (existingRect == null)
            {
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
        if (item.Tag?.ToString() != "placeholder") return;
        
        item.BeginInit();
        
        item.Opacity = 1.0;
        item.ClearValue(Border.BackgroundProperty);
        item.ClearValue(Border.BorderBrushProperty);
        item.ClearValue(Border.BorderThicknessProperty);
        
        if (item.Child is Grid grid)
        {
            grid.Opacity = 1.0;
            
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
        if (_currentDropIndex >= 0 && _currentDropIndex != _originalDraggedIndex)
        {
            // 执行外部的移动命令
            if (MoveCommand?.CanExecute(new MoveCommandParameter(_originalDraggedIndex, _currentDropIndex)) == true)
            {
                MoveCommand.Execute(new MoveCommandParameter(_originalDraggedIndex, _currentDropIndex));
            }
        }
        
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
        _preventCaptureLoss = false;

        if (_topLevel != null)
        {
            _topLevel.PointerMoved -= OnGlobalPointerMoved;
            _topLevel.PointerReleased -= OnGlobalPointerReleased;
            _topLevel.PointerCaptureLost -= OnGlobalPointerCaptureLost;
        }

        if (_itemElements.Count > 0)
        {
            ResetAllTransforms();
        }
        else
        {
            var allItems = GetAllSortableItems().ToList();
            foreach (var item in allItems)
            {
                item.RenderTransform = null;
                RestoreNormalAppearance(item);
            }
        }
        
        if (_dragLayer != null)
        {
            if (this.GetVisualRoot() is TopLevel topLevel)
            {
                var overlayLayer = OverlayLayer.GetOverlayLayer(topLevel);
                overlayLayer?.Children.Remove(_dragLayer);
            }
        }
        
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
        
        if (_isDragging && _itemElements.Count > 0)
        {
            return _itemElements.IndexOf(item);
        }
        
        var items = GetAllSortableItems().ToList();
        return items.IndexOf(item);
    }

    private int GetDropTargetIndex(Point position)
    {
        if (ItemsSource == null || _itemElements.Count == 0) return -1;
        
        var listPosition = position;
        if (ItemsList != null)
        {
            var transform = this.TransformToVisual(ItemsList);
            if (transform != null)
            {
                listPosition = position.Transform(transform.Value);
            }
        }
        
        var scrollViewer = this.FindDescendantOfType<ScrollViewer>();
        if (scrollViewer != null)
        {
            listPosition = new Point(listPosition.X, listPosition.Y + scrollViewer.Offset.Y);
        }
        
        var firstItem = _itemElements.FirstOrDefault();
        if (firstItem == null) return -1;
        
        var listStartY = firstItem.Bounds.Y;
        var relativeY = listPosition.Y - listStartY;
        var targetIndex = (int)Math.Floor(relativeY / ITEM_HEIGHT);
        
        targetIndex = Math.Max(0, Math.Min(targetIndex, _itemElements.Count - 1));
        
        return targetIndex;
    }

    private IEnumerable<Border> GetAllSortableItems()
    {
        return ItemsList?.GetVisualDescendants()
            .OfType<Border>()
            .Where(b => b.Name == "SortableItem") ?? Enumerable.Empty<Border>();
    }

    private IEnumerable<Border> GetAllDragHandles()
    {
        return ItemsList?.GetVisualDescendants()
            .OfType<Border>()
            .Where(b => b.Name == "DragHandle") ?? Enumerable.Empty<Border>();
    }
}

// 移动命令参数类
public class MoveCommandParameter
{
    public int FromIndex { get; }
    public int ToIndex { get; }

    public MoveCommandParameter(int fromIndex, int toIndex)
    {
        FromIndex = fromIndex;
        ToIndex = toIndex;
    }
}