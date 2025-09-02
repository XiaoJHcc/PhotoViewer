using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using PhotoViewer.ViewModels;

namespace PhotoViewer.Views;

public partial class FileSettingsView : UserControl
{
    private SettingsViewModel? ViewModel => DataContext as SettingsViewModel;
    
    public FileSettingsView()
    {
        InitializeComponent();
        var listBox = this.FindControl<ListBox>("SortableFormats");
        AddDragDropHandlers(listBox);
    }

    private Point _startPoint;
    private bool _isDragging;
        
    private void OnPointerPressed(object sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(null).Properties.IsLeftButtonPressed)
        {
            _startPoint = e.GetPosition(null);
            _isDragging = false;
                
            var grid = (Grid)sender;
            grid.PointerMoved += OnPointerMoved;
            grid.PointerReleased += OnPointerReleased;
        }
    }
    
    private void OnPointerMoved(object sender, PointerEventArgs e)
    {
        if (!_isDragging)
        {
            var point = e.GetPosition(null);
            var diff = point - _startPoint;
                
            if (Math.Abs(diff.X) > 3 || Math.Abs(diff.Y) > 3)
            {
                _isDragging = true;
                var grid = (Grid)sender;
                var data = new DataObject();
                data.Set("formatItem", grid.DataContext);
                    
                DragDrop.DoDragDrop(e, data, DragDropEffects.Move);
            }
        }
    }
    
    private void OnPointerReleased(object sender, PointerReleasedEventArgs e)
    {
        var grid = (Grid)sender;
        grid.PointerMoved -= OnPointerMoved;
        grid.PointerReleased -= OnPointerReleased;
    }
        
    
    private void AddDragDropHandlers(ListBox listBox)
    {
        listBox.AddHandler(DragDrop.DragOverEvent, (sender, e) =>
        {
            var targetItem = e.Data.Get("formatItem") as SettingsViewModel.FormatItem;
            if (targetItem != null)
            {
                var position = e.GetPosition(listBox);
                var targetIndex = GetInsertIndex(listBox, position);

                if (targetIndex >= 0)
                {
                    e.DragEffects = DragDropEffects.Move;
                    e.Handled = true;
                }
            }
        });

        listBox.AddHandler(DragDrop.DropEvent, (sender, e) =>
        {
            var sourceItem = e.Data.Get("formatItem") as SettingsViewModel.FormatItem;
            if (sourceItem != null)
            {
                var position = e.GetPosition(listBox);
                var targetIndex = GetInsertIndex(listBox, position);

                if (targetIndex >= 0)
                {
                    var items = ViewModel?.FormatItems;
                    if (items != null)
                    {
                        var sourceIndex = items.IndexOf(sourceItem);
                        if (sourceIndex != targetIndex)
                        {
                            items.Move(sourceIndex, targetIndex);
                        }
                    }
                }
            }
        });
    }
    
    private int GetInsertIndex(ListBox listBox, Point position)
    {
        var items = listBox.GetVisualDescendants().OfType<ListBoxItem>();
        foreach (var item in items)
        {
            var bounds = item.Bounds;
            if (position.Y >= bounds.Top && position.Y <= bounds.Bottom)
            {
                var formatItem = item.DataContext as SettingsViewModel.FormatItem;
                if (formatItem != null)
                {
                    var index = ViewModel?.FormatItems.IndexOf(formatItem) ?? -1;
                    if (position.Y > bounds.Top + bounds.Height / 2)
                    {
                        index++;
                    }
                    return index;
                }
            }
        }
        return -1;
    }
}