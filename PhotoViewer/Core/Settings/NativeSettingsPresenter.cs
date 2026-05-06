using System;
using PhotoViewer.ViewModels;

namespace PhotoViewer.Core.Settings;

/// <summary>
/// 原生设置页展示器接口。
/// 由平台层实现具体的原生弹窗展示逻辑，共享层只负责发起调用。
/// </summary>
public interface INativeSettingsPresenter
{
    /// <summary>
    /// 尝试展示原生设置页。
    /// </summary>
    /// <param name="settings">当前共享设置 ViewModel。</param>
    /// <returns>成功接管展示时返回 true，否则返回 false。</returns>
    bool TryPresent(SettingsViewModel settings);
}

/// <summary>
/// 原生设置页展示门面。
/// 用于让共享层在不依赖 UIKit 的前提下调用平台原生设置页面。
/// </summary>
public static class NativeSettingsPresenter
{
    private static INativeSettingsPresenter? _presenter;

    /// <summary>
    /// 注入平台层的原生设置展示实现。
    /// </summary>
    /// <param name="presenter">平台原生设置展示器。</param>
    public static void Initialize(INativeSettingsPresenter presenter)
    {
        _presenter = presenter;
    }

    /// <summary>
    /// 尝试展示平台原生设置页。
    /// </summary>
    /// <param name="settings">当前共享设置 ViewModel。</param>
    /// <returns>成功交由平台接管时返回 true，否则返回 false。</returns>
    public static bool TryPresent(SettingsViewModel settings)
    {
        try
        {
            return _presenter?.TryPresent(settings) ?? false;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"NativeSettingsPresenter.TryPresent failed: {ex.Message}");
            return false;
        }
    }
}
