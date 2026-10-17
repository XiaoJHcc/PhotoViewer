using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using PhotoViewer.Core.Storage;

namespace PhotoViewer.Android.Core;

/// <summary>
/// SAF 文件/文件夹选择器（A1b 占位）。
/// A2/A3 接入 <c>ACTION_OPEN_DOCUMENT_TREE</c> / <c>ACTION_OPEN_DOCUMENT</c>
/// 与原生 <see cref="IFileRef"/> / <see cref="IFolderRef"/> 后再替换本实现；
/// 现在明确日志，避免“点了没反应”被误认为原生壳缺陷。
/// </summary>
public sealed class AndroidFilePicker : IFilePickerService
{
    /// <summary>选择文件夹（暂未实现）。</summary>
    /// <param name="title">标题。</param>
    /// <returns>始终 null。</returns>
    public Task<IFolderRef?> PickFolderAsync(string title)
    {
        Console.WriteLine("[Native] SAF 文件夹选择将在 A2/A3 接入");
        return Task.FromResult<IFolderRef?>(null);
    }

    /// <summary>选择单文件（暂未实现）。</summary>
    /// <param name="title">标题。</param>
    /// <param name="filters">类型过滤项。</param>
    /// <returns>始终 null。</returns>
    public Task<IFileRef?> PickFileAsync(string title, IReadOnlyList<FilePickFilter> filters)
    {
        Console.WriteLine("[Native] SAF 单文件选择将在 A2/A3 接入");
        return Task.FromResult<IFileRef?>(null);
    }
}
