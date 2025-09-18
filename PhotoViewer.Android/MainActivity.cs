using Android;
using Android.App;
using Android.Content.PM;
using Android.OS;
using AndroidX.Core.App;
using AndroidX.Core.Content;
using Avalonia;
using Avalonia.Android;
using Avalonia.ReactiveUI;
using PhotoViewer.Android.Core;
using PhotoViewer.Core;

namespace PhotoViewer.Android;

[Activity(
    Label = "PhotoViewer.Android",
    Theme = "@style/MyTheme.NoActionBar",
    Icon = "@drawable/icon",
    MainLauncher = true,
    ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize | ConfigChanges.UiMode)]
public class MainActivity : AvaloniaMainActivity<App>
{
    private const int STORAGE_PERMISSION_REQUEST_CODE = 1;
    
    protected override AppBuilder CustomizeAppBuilder(AppBuilder builder)
    {
        return base.CustomizeAppBuilder(builder)
            .WithInterFont()
            .UseReactiveUI();
    }
    
    // Android 运行时权限请求
    protected override void OnCreate(Bundle savedInstanceState)
    {
        base.OnCreate(savedInstanceState);

        // 注册 Android 平台的 HeifDecoder
        HeifLoader.Initialize(new AndroidHeifDecoder());

        RequestStoragePermissions();
    }
    
    private void RequestStoragePermissions()
    {
        // Android 11+ 需要特殊处理
        if (Build.VERSION.SdkInt >= BuildVersionCodes.R)
        {
            // Android 11+ 使用 Storage Access Framework
            var permissions = new[]
            {
                Manifest.Permission.ReadExternalStorage
            };
            
            bool needsPermission = false;
            foreach (var permission in permissions)
            {
                if (ContextCompat.CheckSelfPermission(this, permission) != Permission.Granted)
                {
                    needsPermission = true;
                    break;
                }
            }
            
            if (needsPermission)
            {
                ActivityCompat.RequestPermissions(this, permissions, STORAGE_PERMISSION_REQUEST_CODE);
            }
        }
        else
        {
            // Android 10 及以下版本
            var permissions = new[]
            {
                Manifest.Permission.ReadExternalStorage,
                Manifest.Permission.WriteExternalStorage
            };
            
            bool needsPermission = false;
            foreach (var permission in permissions)
            {
                if (ContextCompat.CheckSelfPermission(this, permission) != Permission.Granted)
                {
                    needsPermission = true;
                    break;
                }
            }
            
            if (needsPermission)
            {
                ActivityCompat.RequestPermissions(this, permissions, STORAGE_PERMISSION_REQUEST_CODE);
            }
        }
    }
    
    public override void OnRequestPermissionsResult(int requestCode, string[] permissions, Permission[] grantResults)
    {
        base.OnRequestPermissionsResult(requestCode, permissions, grantResults);
        
        if (requestCode == STORAGE_PERMISSION_REQUEST_CODE)
        {
            for (int i = 0; i < permissions.Length; i++)
            {
                if (grantResults[i] == Permission.Granted)
                {
                    System.Console.WriteLine($"Permission granted: {permissions[i]}");
                }
                else
                {
                    System.Console.WriteLine($"Permission denied: {permissions[i]}");
                }
            }
        }
    }
}