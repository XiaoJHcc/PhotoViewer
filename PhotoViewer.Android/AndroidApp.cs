using System;
using Android.App;
using Android.Runtime;
using Avalonia;
using Avalonia.Android;
using Avalonia.Media;
using ReactiveUI.Avalonia;
using PhotoViewer.Android.Core;
using PhotoViewer.Core;
using PhotoViewer.Core.Settings;

namespace PhotoViewer.Android;

[Application]
public class AndroidApp : AvaloniaAndroidApplication<App>
{
    /// <summary>
    /// 初始化 Android Avalonia 应用宿主。
    /// </summary>
    public AndroidApp(IntPtr javaReference, JniHandleOwnership transfer)
        : base(javaReference, transfer)
    {
    }

    /// <summary>
    /// 构建 Avalonia AppBuilder，并注入 Android 平台能力实现。
    /// </summary>
    protected override AppBuilder CustomizeAppBuilder(AppBuilder builder)
    {
        return base.CustomizeAppBuilder(builder)
            .With(new FontManagerOptions
            {
                DefaultFamilyName = "sans-serif"
            })
            .UseReactiveUI(_ => { })
            .AfterSetup(_ =>
            {
                HeifLoader.Initialize(new AndroidLibHeifDecoder());
                PerformanceBudget.Initialize(new AndroidPerformanceBudget());
                XmpWriter.Initialize(new AndroidXmpWriter(ContentResolver!));
                SettingsService.ConfigureStorage(new AndroidSettingsStorage());
            });
    }
}