using Foundation;
using PhotoViewer.Core.Settings;

namespace PhotoViewer.Apple;

/// <summary>
/// macOS / iOS 轻量界面状态：与设置文档分开的独立偏好键，不参与设置文档版本与备份。
/// 缺失键返回 null，与显式 false 区分开。
/// </summary>
public sealed class AppleViewStateStore : IViewStateStore
{
    /// <summary>界面状态键前缀；每个状态一个独立偏好键。</summary>
    public const string DefaultKeyPrefix = "cc.xiaojh.PhotoViewer.ViewState.";

    private readonly string _keyPrefix;

    public AppleViewStateStore(string keyPrefix = DefaultKeyPrefix)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(keyPrefix);
        _keyPrefix = keyPrefix;
    }

    public bool? GetBool(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        return NSUserDefaults.StandardUserDefaults[_keyPrefix + key] switch
        {
            NSNumber number => number.BoolValue,
            NSString text when bool.TryParse(text, out bool parsed) => parsed,
            _ => null,
        };
    }

    public void SetBool(string key, bool value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        NSUserDefaults.StandardUserDefaults.SetBool(value, _keyPrefix + key);
    }
}
