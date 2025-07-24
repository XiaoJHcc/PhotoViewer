using System.Diagnostics;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Microsoft.Extensions.DependencyInjection;
using Avalonia.Data.Core.Plugins;
using System.Linq;
using Avalonia.Markup.Xaml;
using PhotoViewer.Core;
using PhotoViewer.ViewModels;
using PhotoViewer.Views;

namespace PhotoViewer;

public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        // 设置依赖注入
        var services = new ServiceCollection();
        
        // 添加核心状态管理
        services.AddSingleton<AppState>();
        
        // 添加视图模型
        services.AddTransient<MainViewModel>();
        services.AddTransient<ThumbnailViewModel>();
        services.AddTransient<ControlViewModel>();
        services.AddTransient<ImageViewModel>();
        services.AddTransient<SettingsViewModel>();
        
        // 添加视图
        services.AddTransient<MainView>();
        services.AddTransient<ThumbnailView>();
        services.AddTransient<ControlView>();
        services.AddTransient<ImageView>();
        services.AddTransient<SettingsWindow>();
        
        var provider = services.BuildServiceProvider();
        
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // Avoid duplicate validations from both Avalonia and the CommunityToolkit. 
            // More info: https://docs.avaloniaui.net/docs/guides/development-guides/data-validation#manage-validationplugins
            // DisableAvaloniaDataAnnotationValidation();
            
            // 创建主窗口和主视图模型
            var mainVM = provider.GetRequiredService<MainViewModel>();
            var mainView = provider.GetRequiredService<MainView>();
            mainView.DataContext = mainVM;
            
            desktop.MainWindow = new MainWindow
            {
                Content = mainView,
                DataContext = mainVM
            };
        }
        else if (ApplicationLifetime is ISingleViewApplicationLifetime singleViewPlatform)
        {
            singleViewPlatform.MainView = new MainView
            {
                DataContext = provider.GetRequiredService<MainViewModel>()
            };
        }

        base.OnFrameworkInitializationCompleted();
    }

    private void DisableAvaloniaDataAnnotationValidation()
    {
        // Get an array of plugins to remove
        var dataValidationPluginsToRemove =
            BindingPlugins.DataValidators.OfType<DataAnnotationsValidationPlugin>().ToArray();

        // remove each entry found
        foreach (var plugin in dataValidationPluginsToRemove)
        {
            BindingPlugins.DataValidators.Remove(plugin);
        }
    }
}