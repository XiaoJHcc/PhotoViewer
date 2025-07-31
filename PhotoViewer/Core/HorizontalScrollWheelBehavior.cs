using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Xaml.Interactivity;

namespace PhotoViewer.Core;

public class HorizontalScrollWheelBehavior : Behavior<ScrollViewer>
{
    // 添加公共无参数构造函数
    public HorizontalScrollWheelBehavior()
    {
    }
    
    protected override void OnAttached()
    {
        base.OnAttached();
        if (AssociatedObject is ScrollViewer scrollViewer)
        {
            scrollViewer.PointerWheelChanged += OnPointerWheelChanged;
        }
    }

    protected override void OnDetaching()
    {
        if (AssociatedObject is ScrollViewer scrollViewer)
        {
            scrollViewer.PointerWheelChanged -= OnPointerWheelChanged;
        }
        base.OnDetaching();
    }

    private void OnPointerWheelChanged(object sender, PointerWheelEventArgs e)
    {
        if (AssociatedObject is ScrollViewer scrollViewer && 
            scrollViewer.Extent.Width > scrollViewer.Viewport.Width)
        {
            // 获取滚轮增量值
            double delta = e.Delta.Y;
            
            // 计算新的水平偏移量
            double newOffset = scrollViewer.Offset.X - delta * 100;
            
            // 计算最大允许的偏移量
            double maxOffset = scrollViewer.Extent.Width - scrollViewer.Viewport.Width;
            
            // 确保偏移量在有效范围内
            newOffset = Math.Max(0, Math.Min(newOffset, maxOffset));
            
            // 应用新的偏移量
            scrollViewer.Offset = new Avalonia.Vector(newOffset, scrollViewer.Offset.Y);
            
            // 标记事件已处理，阻止默认滚动行为
            e.Handled = true;
        }
    }
}