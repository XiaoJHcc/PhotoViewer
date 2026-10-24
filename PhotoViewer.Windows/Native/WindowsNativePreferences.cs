using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using PhotoViewer.Core.Settings;

namespace PhotoViewer.Windows.Native;

/// <summary>
/// 主窗口栏位显隐的键名(SHELL-04)：与设置文档分开，普通运行存于
/// <c>%LOCALAPPDATA%\PhotoViewer\native-ui.json</c>；隔离运行改存验收输出目录，不碰用户档。
/// </summary>
internal static class WindowsNativePreferences
{
    internal const string ShowFilmKey = "PhotoViewer.Native.ShowFilm";
    internal const string ShowControlsKey = "PhotoViewer.Native.ShowControls";

    /// <summary>普通运行的界面状态文件：<c>%LOCALAPPDATA%\PhotoViewer\native-ui.json</c>。</summary>
    internal static string DefaultFilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "PhotoViewer",
        "native-ui.json");

    /// <summary>隔离运行的界面状态文件：与设置、数据库同在本轮验收输出目录。</summary>
    internal static string IsolatedFilePath => Path.Combine(
        WindowsMainWindow.SmokeOutputDirectory,
        "native-ui-smoke.json");

    /// <summary>按运行模式创建界面状态存储。</summary>
    /// <param name="isolated">是否使用隔离目录。</param>
    /// <returns>界面状态存储。</returns>
    internal static WindowsNativeViewStateStore CreateStore(bool isolated)
        => new(isolated ? IsolatedFilePath : DefaultFilePath);
}

/// <summary>
/// 平台界面状态存储：一个以字符串为键的 JSON 对象，只解释布尔值。
/// 读写整份文档，未知键(如 <see cref="SettingsViewStateKeys.SimilarityPanelExpanded"/>)原样保留；
/// 缺失键返回 null 由调用方保留默认值，与设置文档互不影响。
/// </summary>
internal sealed class WindowsNativeViewStateStore : IViewStateStore
{
    private readonly object _gate = new();
    private readonly string _path;

    /// <summary>创建存储。</summary>
    /// <param name="path">状态文件路径。</param>
    internal WindowsNativeViewStateStore(string path) => _path = Path.GetFullPath(path);

    /// <summary>状态文件路径。</summary>
    internal string FilePath => _path;

    /// <inheritdoc/>
    public bool? GetBool(string key)
    {
        lock (_gate)
        {
            return Read().TryGetValue(key, out var element)
                && element.ValueKind is JsonValueKind.True or JsonValueKind.False
                ? element.GetBoolean()
                : null;
        }
    }

    /// <inheritdoc/>
    public void SetBool(string key, bool value)
    {
        lock (_gate)
        {
            var values = Read();
            values[key] = JsonSerializer.SerializeToElement(value);
            Write(values);
        }
    }

    private Dictionary<string, JsonElement> Read()
    {
        var values = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        try
        {
            if (!File.Exists(_path))
            {
                return values;
            }

            var parsed = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(File.ReadAllText(_path));
            if (parsed is null)
            {
                return values;
            }

            foreach (var pair in parsed)
            {
                values[pair.Key] = pair.Value;
            }
        }
        catch (Exception ex)
        {
            NativeStartupLog.Fail("读取原生界面状态", ex);
        }

        return values;
    }

    private void Write(Dictionary<string, JsonElement> values)
    {
        try
        {
            var directory = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllText(_path, JsonSerializer.Serialize(values));
        }
        catch (Exception ex)
        {
            NativeStartupLog.Fail("写入原生界面状态", ex);
        }
    }
}
