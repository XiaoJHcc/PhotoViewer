using Android;
using Android.App;
using Android.Content.PM;
using Android.OS;
using AndroidX.Core.App;
using AndroidX.Core.Content;
using Avalonia;
using Avalonia.Android;

namespace PhotoViewer.Android;

[Activity(
    Label = "PhotoViewer.Android",
    Theme = "@style/MyTheme.NoActionBar",
    Icon = "@drawable/icon",
    MainLauncher = true,
    ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize | ConfigChanges.UiMode)]
public class MainActivity : AvaloniaMainActivity<App>
{
    protected override AppBuilder CustomizeAppBuilder(AppBuilder builder)
    {
        return base.CustomizeAppBuilder(builder)
            .WithInterFont();
    }
    
    // Android 运行时权限请求
    protected override async void OnCreate(Bundle savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        
        // 请求存储权限
        if (ContextCompat.CheckSelfPermission(this, Manifest.Permission.ReadExternalStorage) 
            != Permission.Granted)
        {
            ActivityCompat.RequestPermissions(this, 
                new[] { Manifest.Permission.ReadExternalStorage }, 1);
        }
    }
}