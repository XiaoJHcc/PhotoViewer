using System;
using Android.App;
using Android.Content;

namespace PhotoViewer.Android.Native;

/// <summary>
/// 原生壳平台偏好（SHELL-04）：文件栏/控制栏显隐写入 SharedPreferences，不进设置 JSON；
/// 分析栏仍为会话态。
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

    private static bool Load(string key)
    {
        try
        {
            using var prefs = Preferences();
            return prefs?.GetBoolean(key, true) ?? true;
        }
        catch (Exception exception)
        {
            Console.WriteLine($"[Native] 读取平台偏好失败（{key}）：{exception.Message}");
            return true;
        }
    }

    private static void Save(string key, bool value)
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

    private static ISharedPreferences? Preferences()
        => Application.Context.GetSharedPreferences(PreferenceName, FileCreationMode.Private);
}
