using System;
using Android.App;
using Android.Content;
using PhotoViewer.Core.Settings;

namespace PhotoViewer.Android.Native;

/// <summary>
/// 原生壳平台偏好（SHELL-04）：文件栏/控制栏显隐写入 SharedPreferences，不进设置 JSON；
/// 分析栏仍为会话态。另承载共享 <see cref="IViewStateStore"/> 的轻量界面状态（如相似面板展开）。
/// </summary>
internal static class AndroidShellPreferences
{
    private const string PreferenceName = "cc.xiaojh.PhotoViewer.Native";
    private const string ShowFilmKey = "PhotoViewer.Native.ShowFilm";
    private const string ShowControlsKey = "PhotoViewer.Native.ShowControls";
    private const string ShowExifKey = "PhotoViewer.Native.ShowExif";

    /// <summary>当前偏好存储名（默认为用户档；验收模式可切到隔离档）。</summary>
    private static string _preferenceName = PreferenceName;

    /// <summary>
    /// 切到隔离档：验收/压测换用独立的 SharedPreferences，栏位显隐等界面偏好不读也不覆盖用户档，
    /// 每轮从默认值（全部显示）起跑。必须在根视图读取栏位显隐之前调用。
    /// </summary>
    /// <param name="profile">档名；空值恢复用户档。</param>
    internal static void UseIsolatedProfile(string profile)
        => _preferenceName = string.IsNullOrWhiteSpace(profile) ? PreferenceName : PreferenceName + "." + profile;

    /// <summary>读取文件栏显隐。</summary>
    /// <returns>已存值；未存过时为 true（默认显示）。</returns>
    internal static bool LoadShowFilm() => Load(ShowFilmKey);

    /// <summary>读取控制栏显隐。</summary>
    /// <returns>已存值；未存过时为 true（默认显示）。</returns>
    internal static bool LoadShowControls() => Load(ShowControlsKey);

    /// <summary>读取手机形态 EXIF 开关。</summary>
    /// <returns>已存值；未存过时为 true（默认开）。</returns>
    internal static bool LoadShowExif() => Load(ShowExifKey);

    /// <summary>保存文件栏显隐。</summary>
    /// <param name="value">是否显示。</param>
    internal static void SaveShowFilm(bool value) => Save(ShowFilmKey, value);

    /// <summary>保存控制栏显隐。</summary>
    /// <param name="value">是否显示。</param>
    internal static void SaveShowControls(bool value) => Save(ShowControlsKey, value);

    /// <summary>保存手机形态 EXIF 开关。</summary>
    /// <param name="value">是否显示。</param>
    internal static void SaveShowExif(bool value) => Save(ShowExifKey, value);

    /// <summary>读取通用布尔界面状态；未存过时返回 null（区别于显式 false）。</summary>
    /// <param name="key">偏好键。</param>
    /// <returns>已存值或 null。</returns>
    internal static bool? LoadBool(string key)
    {
        try
        {
            using var prefs = Preferences();
            if (prefs == null || !prefs.Contains(key)) return null;
            return prefs.GetBoolean(key, false);
        }
        catch (Exception exception)
        {
            Console.WriteLine($"[Native] 读取平台偏好失败（{key}）：{exception.Message}");
            return null;
        }
    }

    /// <summary>保存通用布尔界面状态。</summary>
    /// <param name="key">偏好键。</param>
    /// <param name="value">值。</param>
    internal static void SaveBool(string key, bool value)
    {
        try
        {
            using var prefs = Preferences();
            using var editor = prefs?.Edit();
            editor?.PutBoolean(key, value);
            editor?.Apply();
        }
        catch (Exception exception)
        {
            Console.WriteLine($"[Native] 写入平台偏好失败（{key}）：{exception.Message}");
        }
    }

    private static bool Load(string key) => LoadBool(key) ?? true;

    private static void Save(string key, bool value) => SaveBool(key, value);

    private static ISharedPreferences? Preferences()
        => Application.Context.GetSharedPreferences(_preferenceName, FileCreationMode.Private);
}

/// <summary>
/// 共享 <see cref="IViewStateStore"/> 的 Android 实现：复用原生壳 SharedPreferences（非设置 JSON），
/// 未存过的键返回 null，写入即时更新；相似面板展开等轻量界面状态跨会话保留。
/// </summary>
internal sealed class AndroidViewStateStore : IViewStateStore
{
    /// <summary>读取布尔界面状态；未存过时为 null。</summary>
    /// <param name="key">状态键。</param>
    /// <returns>已存值或 null。</returns>
    public bool? GetBool(string key) => AndroidShellPreferences.LoadBool(key);

    /// <summary>写入布尔界面状态。</summary>
    /// <param name="key">状态键。</param>
    /// <param name="value">值。</param>
    public void SetBool(string key, bool value) => AndroidShellPreferences.SaveBool(key, value);
}
