using Foundation;
using PhotoViewer.Core.Storage;

namespace PhotoViewer.Mac.Native;

/// <summary>
/// macOS 路径与系统文件事件到共享存储引用的转换。
/// 命令行、Finder「打开方式」、Dock 文件拖放与窗口内拖入都先落成本地引用，
/// 再投进同一个打开请求通道，壳层不再自己判断"打开文件还是打开文件夹"。
/// </summary>
internal static class MacOpenPaths
{
    /// <summary>
    /// 把路径转成本地引用：存在的目录建 <see cref="LocalFolderRef"/>，其余存在项建 <see cref="LocalFileRef"/>；
    /// 空串与不存在的路径忽略（是否受支持由共享协调器按当前启用格式判断）。
    /// </summary>
    /// <param name="paths">候选路径。</param>
    /// <returns>按入参顺序排列的引用；无可打开项时为空。</returns>
    internal static IReadOnlyList<IStorageItemRef> FromPaths(IEnumerable<string> paths)
    {
        var items = new List<IStorageItemRef>();
        foreach (var path in paths)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                continue;
            }

            string fullPath;
            try
            {
                fullPath = Path.GetFullPath(path);
            }
            catch (Exception)
            {
                continue;
            }

            if (Directory.Exists(fullPath))
            {
                items.Add(new LocalFolderRef(fullPath));
            }
            else if (File.Exists(fullPath))
            {
                items.Add(new LocalFileRef(fullPath));
            }
        }

        return items;
    }

    /// <summary>
    /// 把系统文件 URL 转成本地引用；非文件 URL 与不存在的目标忽略，顺序保持不变。
    /// </summary>
    /// <param name="urls">系统传入的文件 URL。</param>
    /// <returns>按入参顺序排列的引用；无可打开项时为空。</returns>
    internal static IReadOnlyList<IStorageItemRef> FromUrls(IEnumerable<NSUrl> urls)
    {
        var paths = new List<string>();
        foreach (var url in urls)
        {
            if (url.IsFileUrl && url.Path is { Length: > 0 } path)
            {
                paths.Add(path);
            }
        }

        return FromPaths(paths);
    }
}
