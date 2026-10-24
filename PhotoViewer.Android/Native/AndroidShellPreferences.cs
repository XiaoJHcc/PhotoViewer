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

    /// <summary>读取文件栏显隐。</summary>
    /// <returns>已存值；未存过时为 true（默认显示）。</returns>
    internal static bool LoadShowFilm() => Load(ShowFilmKey);

    /// <summary>读取控制栏显隐。</summary>
    /// <returns>已存值；未存过时为 true（默认显示）。</returns>
    internal static bool LoadShowControls() => Load(ShowControlsKey);

    /// <summary>保存文件栏显隐。</summary>
    /// <param name="value">是否显示。</param>
    internal static void SaveShowFilm(bool value) => Save(ShowFilmKey, value);

    /// <summary>保存控制栏显隐。</summary>
    /// <param name="value">是否显示。</param>
    internal static void SaveShowControls(bool value) => Save(ShowControlsKey, value);

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
        => Application.Context.GetSharedPreferences(PreferenceName, FileCreationMode.Private);
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
