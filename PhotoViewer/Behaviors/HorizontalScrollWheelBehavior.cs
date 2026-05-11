using System;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Xaml.Interactivity;

namespace PhotoViewer.Behaviors;

public class HorizontalScrollWheelBehavior : Behavior<ScrollViewer>
{
    public HorizontalScrollWheelBehavior()
    {
    }

    /// <summary>
    /// 用 AddHandler(handledEventsToo:true) 订阅,确保即使 ScrollViewer 自身的默认处理器
    /// 把 PointerWheelChanged 标记为 Handled(例如行布局下 V=Disabled、H=Auto 时部分容器会先吃掉事件),
    /// 我们的横向滚动逻辑依然能拿到事件。
    /// </summary>
    protected override void OnAttached()
    {
        base.OnAttached();
        if (AssociatedObject is ScrollViewer scrollViewer)
        {
            scrollViewer.AddHandler(InputElement.PointerWheelChangedEvent,
                OnPointerWheelChanged, RoutingStrategies.Bubble, handledEventsToo: true);
        }
    }

    protected override void OnDetaching()
    {
        if (AssociatedObject is ScrollViewer scrollViewer)
        {
            scrollViewer.RemoveHandler(InputElement.PointerWheelChangedEvent, OnPointerWheelChanged);
        }
        base.OnDetaching();
    }

    private void OnPointerWheelChanged(object? sender, PointerWheelEventArgs e)
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