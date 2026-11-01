namespace PhotoViewer.Mac.Native;

/// <summary>
/// 验收素材定位：从当前目录与程序目录向上找到仓库根的 <c>Fixtures/</c>，
/// 让原生验收不依赖启动目录（报告与截图仍按当前目录落在 release/）。
/// </summary>
internal static class FixturePaths
{
    /// <summary>仓库内素材根目录的绝对路径；找不到时退回当前目录下的 Fixtures/。</summary>
    public static string Root { get; } = Locate();

    /// <summary>素材路径（文件或子目录）的绝对路径。</summary>
    public static string Resolve(params string[] parts) => Path.Combine([Root, .. parts]);

    /// <summary>判断路径是否位于指定素材子目录下；相对路径与绝对路径均适用。</summary>
    public static bool LocatedIn(string path, params string[] parts)
    {
        var normalized = "/" + path.Replace('\\', '/');
        return normalized.Contains("/" + string.Join('/', parts) + "/", StringComparison.Ordinal);
    }

    private static string Locate()
    {
        string[] starts = [Directory.GetCurrentDirectory(), AppContext.BaseDirectory];
        foreach (var start in starts)
        {
            for (var directory = new DirectoryInfo(start); directory is not null; directory = directory.Parent)
            {
                var candidate = Path.Combine(directory.FullName, "Fixtures");
                if (Directory.Exists(candidate)) return candidate;
            }
        }

        return Path.Combine(Directory.GetCurrentDirectory(), "Fixtures");
    }
}
