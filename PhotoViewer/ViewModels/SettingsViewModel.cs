using System;
using System.Linq;
using System.Reactive;
using PhotoViewer.Controls;
using ReactiveUI;
using PhotoViewer.Core;

namespace PhotoViewer.ViewModels;

public partial class SettingsViewModel : ReactiveObject
{
    // 检查是否为安卓平台
    public static bool IsAndroid => OperatingSystem.IsAndroid();
    
    // 检查是否为 iOS 平台
    public static bool IsIOS => OperatingSystem.IsIOS();
    
    // 检查是否为 Apple 平台
    public static bool IsApple => OperatingSystem.IsIOS() || OperatingSystem.IsMacOS();
    
    public SettingsViewModel()
    {
        // 设置 UI 初始化
        SortScalePreset();
        InitializeLayoutModes();
        
        // 设置排序列表数据初始化
        InitializeFileFormats();
        InitializeHotkeys();
        InitializeExifDisplay();
        
        // 设置缓存数据初始化
        InitializeBitMapCache();
    }
}
