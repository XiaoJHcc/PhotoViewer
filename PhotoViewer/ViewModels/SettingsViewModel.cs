using System;
using System.Linq;
using System.Reactive;
using PhotoViewer.Controls;
using ReactiveUI;
using PhotoViewer.Core;

namespace PhotoViewer.ViewModels;

// 布局模式枚举
public enum LayoutMode
{
    Vertical,    // 上中下
    Horizontal,  // 左中右
    Auto         // 智能（根据屏幕方向）
}

public partial class SettingsViewModel : ReactiveObject
{
    // 检查是否为安卓平台
    public static bool IsAndroid => OperatingSystem.IsAndroid();
    
    // 检查是否为 iOS 平台
    public static bool IsIOS => OperatingSystem.IsIOS();
    
    public SettingsViewModel()
    {
        SortScalePreset();
        InitializeFileFormats();
        InitializeHotkeys();
        InitializeLayoutModes();
        InitializeExifDisplayItems();
        InitializeMemoryBudget();
        
        // 初始化移动命令
        MoveFileFormatCommand = ReactiveCommand.Create<MoveCommandParameter>(OnMoveFileFormat);
        MoveHotkeyCommand = ReactiveCommand.Create<MoveCommandParameter>(OnMoveHotkey);
        MoveExifDisplayCommand = ReactiveCommand.Create<MoveCommandParameter>(OnMoveExifDisplay);
        
        // 初始化三个预载滑条的 Exp backing 字段，确保 UI 初始位置正确
        _preloadForwardCountExp = _preloadForwardCount <= 0 ? 0 : ToExp(_preloadForwardCount, 1, Math.Max(1, _preloadMaximum));
        _preloadBackwardCountExp = _preloadBackwardCount <= 0 ? 0 : ToExp(_preloadBackwardCount, 1, Math.Max(1, _preloadMaximum));
        _visibleCenterPreloadCountExp = _visibleCenterPreloadCount <= 0 ? 0 : ToExp(_visibleCenterPreloadCount, 1, Math.Max(1, _preloadMaximum));

        // 监听缓存最大数量变化：保持预载滑条位置（Exp）不变，仅在新域内重算整数
        this.WhenAnyValue(v => v.BitmapCacheMaxCount)
            .Subscribe(v =>
            {
                BitmapLoader.MaxCacheCount = v;

                _freezePreloadExp = true;
                try
                {
                    var newMax = Math.Max(0, v / 3);
                    PreloadMaximum = newMax;

                    PreloadForwardCount = FromExp(PreloadForwardCountExp, 1, Math.Max(1, newMax), allowZero: true);
                    PreloadBackwardCount = FromExp(PreloadBackwardCountExp, 1, Math.Max(1, newMax), allowZero: true);
                    VisibleCenterPreloadCount = FromExp(VisibleCenterPreloadCountExp, 1, Math.Max(1, newMax), allowZero: true);
                }
                finally
                {
                    _freezePreloadExp = false;
                }
            });
        
        // 监听内存上限变化同步到 BitmapLoader
        this.WhenAnyValue(v => v.BitmapCacheMaxMemory)
            .Subscribe(v =>
            {
                BitmapLoader.MaxCacheSize = v * 1024L * 1024L;
                
                // 计算当前内存能够满足多少张照片
                BitmapCacheCountInfo = "当前内存设置下可缓存的照片数量上限: \n24MP < " + v/(24*4) + " 张，33MP < " + v/(33*4) + " 张，42MP < " + v/(42*4) + " 张，61MP < " + v/(61*4) + " 张";
            });
    }
}
