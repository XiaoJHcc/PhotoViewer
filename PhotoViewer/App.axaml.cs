using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using PhotoViewer.ViewModels;
using PhotoViewer.Views;
using PhotoViewer.Windows;

namespace PhotoViewer;

public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        var vm = new MainViewModel();

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            if (OperatingSystem.IsWindows())
            {
                // Windows: 使用带自定义标题栏的 MainWindow
                desktop.MainWindow = new MainWindowForWindows
                {
                    DataContext = vm
                };
            }
            else
            {
                // 其他桌面 (Mac): 使用系统原生 MainWindow
                desktop.MainWindow = new MainWindowForMac
                {
                    DataContext = vm
                };
            }
        }
        else if (ApplicationLifetime is ISingleViewApplicationLifetime singleViewPlatform)
        {
            // 移动端 (Android / iOS): 使用新建的 SingleView
            singleViewPlatform.MainView = new SingleView
            {
                DataContext = vm
            };
        }

        base.OnFrameworkInitializationCompleted();
    }
}