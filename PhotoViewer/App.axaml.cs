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
        
        // 核心 ViewModel
        services.AddSingleton<SettingsViewModel>();
        services.AddSingleton<MainViewModel>();
        
        // 子 ViewModel（由 MainViewModel 创建，这里不需要注册）
        
        // 视图
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
            
            // 获取设置和主 ViewModel
            var settings = provider.GetRequiredService<SettingsViewModel>();
            var mainVM = provider.GetRequiredService<MainViewModel>();
            
            // 创建主窗口
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