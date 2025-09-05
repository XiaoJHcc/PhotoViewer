using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.VisualTree;
using PhotoViewer.ViewModels;

namespace PhotoViewer.Views;

public partial class ControlView : UserControl
{
    public ControlView()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private void OnLoaded(object? sender, RoutedEventArgs e)
    {
        // 监听全局键盘事件
        if (this.GetVisualRoot() is TopLevel topLevel)
        {
            topLevel.KeyDown += OnGlobalKeyDown;
        }
    }

    private void OnControlButtonClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button button && 
            button.Tag is string commandName && 
            DataContext is ControlViewModel viewModel)
        {
            var command = viewModel.GetCommandByName(commandName);
            if (command?.CanExecute(null) == true)
            {
                command.Execute(null);
            }
        }
    }

    private void OnGlobalKeyDown(object? sender, KeyEventArgs e)
    {
        if (DataContext is not ControlViewModel viewModel) return;

        var pressedGesture = new KeyGesture(e.Key, e.KeyModifiers);
        
        // 查找匹配的快捷键
        var matchedHotkey = viewModel.AllHotkeys?.FirstOrDefault(h => 
            h.IsEnabled && (
                (h.PrimaryHotkey?.Equals(pressedGesture) == true) ||
                (h.SecondaryHotkey?.Equals(pressedGesture) == true)
            ));

        if (matchedHotkey != null)
        {
            // 执行对应的命令
            var command = viewModel.GetCommandByName(matchedHotkey.Command);
            if (command?.CanExecute(null) == true)
            {
                command.Execute(null);
                e.Handled = true;
            }
        }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        // 移除全局键盘事件监听
        if (this.GetVisualRoot() is TopLevel topLevel)
        {
            topLevel.KeyDown -= OnGlobalKeyDown;
        }
        base.OnDetachedFromVisualTree(e);
    }
}