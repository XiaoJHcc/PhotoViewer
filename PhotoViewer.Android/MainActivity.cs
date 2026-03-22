using Android;
using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using AndroidX.Core.App;
using AndroidX.Core.Content;
using Avalonia;
using Avalonia.Android;
using Avalonia.ReactiveUI;
using PhotoViewer.Android.Core;
using PhotoViewer.Core;
using PhotoViewer.Core.Settings;

namespace PhotoViewer.Android;

[Activity(
    Label = "PhotoViewer",
    Theme = "@style/MyTheme.NoActionBar",
    Icon = "@drawable/icon",
    MainLauncher = true,
    LaunchMode = LaunchMode.SingleTask,
    ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize | ConfigChanges.UiMode)]
[IntentFilter(
    [Intent.ActionView],
    Categories = [Intent.CategoryDefault, Intent.CategoryBrowsable],
    DataMimeType = "image/*")]
[IntentFilter(
    [Intent.ActionSend, Intent.ActionSendMultiple],
    Categories = [Intent.CategoryDefault],
    DataMimeType = "image/*")]
public class MainActivity : AvaloniaMainActivity<App>
{
    private const int StoragePermissionRequestCode = 1;
    
    /// <summary>
    /// 自定义 Avalonia AppBuilder，并注入 Android 平台能力实现。
    /// </summary>
    protected override AppBuilder CustomizeAppBuilder(AppBuilder builder)
    {
        return base.CustomizeAppBuilder(builder)
            .WithInterFont()
            .UseReactiveUI()
            .AfterSetup(_ =>
            {
                // 注册 Android 平台的 HeifDecoder
                HeifLoader.Initialize(new AndroidHeifDecoder());
                MemoryBudget.Initialize(new AndroidMemoryBudget());
                SettingsService.ConfigureStorage(new AndroidSettingsStorage());
            });
    }
    
    /// <summary>
    /// Android 入口。
    /// 这里会先初始化 Avalonia，再处理运行时权限与冷启动外部打开 Intent。
    /// </summary>
    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);

        RequestStoragePermissions();
        HandleIncomingIntent(Intent);
    }

    /// <summary>
    /// 处理已运行应用再次收到的新 Intent。
    /// </summary>
    /// <param name="intent">新的系统 Intent</param>
    protected override void OnNewIntent(Intent? intent)
    {
        base.OnNewIntent(intent);

        if (intent != null)
        {
            Intent = intent;
        }

        HandleIncomingIntent(intent);
    }
    
    /// <summary>
    /// 请求 Android 运行时存储权限。
    /// </summary>
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
                ActivityCompat.RequestPermissions(this, permissions, StoragePermissionRequestCode);
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
                ActivityCompat.RequestPermissions(this, permissions, StoragePermissionRequestCode);
            }
        }
    }
    
    /// <summary>
    /// 处理权限请求结果，仅做日志记录。
    /// </summary>
    /// <param name="requestCode">请求编号</param>
    /// <param name="permissions">权限数组</param>
    /// <param name="grantResults">授权结果</param>
    public override void OnRequestPermissionsResult(int requestCode, string[] permissions, Permission[] grantResults)
    {
        base.OnRequestPermissionsResult(requestCode, permissions, grantResults);
        
        if (requestCode == StoragePermissionRequestCode)
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

    /// <summary>
    /// 将 Android Intent 交给外部打开桥接层统一解析。
    /// </summary>
    /// <param name="intent">系统传入的 Intent</param>
    private void HandleIncomingIntent(Intent? intent)
    {
        AndroidExternalOpenBridge.PublishFromIntent(this, intent);
    }
}