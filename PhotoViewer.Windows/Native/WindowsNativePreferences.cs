using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace PhotoViewer.Windows.Native;

/// <summary>
/// 主窗口栏位显隐的平台偏好存储(SHELL-04)：键名沿用 <c>PhotoViewer.Native.ShowFilm</c> /
/// <c>PhotoViewer.Native.ShowControls</c>，与设置 JSON 分开，不改持久化契约。
/// 存放于 <c>%LOCALAPPDATA%\PhotoViewer\native-ui.json</c>。
/// </summary>
internal static class WindowsNativePreferences
{
    private const string ShowFilmKey = "PhotoViewer.Native.ShowFilm";
    private const string ShowControlsKey = "PhotoViewer.Native.ShowControls";

    /// <summary>读取栏位显隐；缺失或损坏时返回 null，由调用方保留默认值。</summary>
    /// <returns>胶片栏与控制栏显隐，缺失项为 null。</returns>
    public static (bool? ShowFilm, bool? ShowControls) Load()
    {
        try
        {
            var path = FilePath();
            if (!File.Exists(path))
            {
                return (null, null);
            }

            var values = JsonSerializer.Deserialize<Dictionary<string, bool>>(File.ReadAllText(path));
            if (values == null)
            {
                return (null, null);
            }

            return (values.TryGetValue(ShowFilmKey, out var film) ? film : null,
                values.TryGetValue(ShowControlsKey, out var controls) ? controls : null);
        }
        catch (Exception ex)
        {
            NativeStartupLog.Fail("读取原生栏位偏好", ex);
            return (null, null);
        }
    }

    /// <summary>写入栏位显隐；失败只记录日志，不影响主流程。</summary>
    /// <param name="showFilm">文件栏是否可见。</param>
    /// <param name="showControls">控制栏是否可见。</param>
    public static void Save(bool showFilm, bool showControls)
    {
        try
        {
            var path = FilePath();
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var values = new Dictionary<string, bool>
            {
                [ShowFilmKey] = showFilm,
                [ShowControlsKey] = showControls,
            };
            File.WriteAllText(path, JsonSerializer.Serialize(values));
        }
        catch (Exception ex)
        {
            NativeStartupLog.Fail("写入原生栏位偏好", ex);
        }
    }

    /// <summary>偏好文件路径。</summary>
    /// <returns>JSON 路径。</returns>
    private static string FilePath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "PhotoViewer",
        "native-ui.json");
}
