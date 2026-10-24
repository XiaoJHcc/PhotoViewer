using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Windows.AppLifecycle;
using PhotoViewer.Core.Storage;
using Windows.ApplicationModel.Activation;

namespace PhotoViewer.Windows.Native;

/// <summary>
/// 原生路径与系统激活参数到共享存储引用的转换。
/// 命令行、拖放、文件关联与二次激活都先落到 <see cref="IStorageItemRef"/>，再投进同一个打开请求通道，
/// 平台壳不再自己解析"打开文件还是打开文件夹"。
/// </summary>
internal static class WindowsOpenPaths
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
    /// 把系统激活参数转成本地引用：文件激活取每个实际存在的文件/文件夹，
    /// Launch 激活取命令行里的路径。其他激活类型（协议、共享目标等）不在此处理。
    /// </summary>
    /// <param name="args">Windows App SDK 激活参数。</param>
    /// <returns>按激活顺序排列的引用；无可打开项时为空。</returns>
    internal static IReadOnlyList<IStorageItemRef> FromActivation(AppActivationArguments args)
    {
        ArgumentNullException.ThrowIfNull(args);

        switch (args.Kind)
        {
            case ExtendedActivationKind.File:
                if (args.Data is IFileActivatedEventArgs fileArgs)
                {
                    var paths = new List<string>();
                    foreach (var item in fileArgs.Files)
                    {
                        paths.Add(item.Path);
                    }

                    return FromPaths(paths);
                }

                return [];

            case ExtendedActivationKind.Launch:
                if (args.Data is ILaunchActivatedEventArgs launchArgs
                    && !string.IsNullOrWhiteSpace(launchArgs.Arguments))
                {
                    return FromPaths(SplitArguments(launchArgs.Arguments));
                }

                return [];

            default:
                return [];
        }
    }

    /// <summary>拆命令行：双引号内的空格保留，多余引号丢弃（Windows 命令行口径）。</summary>
    /// <param name="arguments">命令行字符串。</param>
    /// <returns>参数片段。</returns>
    private static IEnumerable<string> SplitArguments(string arguments)
    {
        var current = new System.Text.StringBuilder();
        var quoted = false;
        foreach (var character in arguments)
        {
            if (character == '"')
            {
                quoted = !quoted;
                continue;
            }

            if (!quoted && char.IsWhiteSpace(character))
            {
                if (current.Length > 0)
                {
                    yield return current.ToString();
                    current.Clear();
                }

                continue;
            }

            current.Append(character);
        }

        if (current.Length > 0)
        {
            yield return current.ToString();
        }
    }
}
