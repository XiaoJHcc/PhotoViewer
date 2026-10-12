using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Avalonia.Platform.Storage;
using PhotoViewer.Core.Storage;

namespace PhotoViewer.Platform;

/// <summary>
/// <see cref="IFileRef"/> 的 Avalonia 适配器,包装 <see cref="IStorageFile"/>。
/// 平台头工程(如 iOS 安全作用域书签)可经 <see cref="Inner"/> 解包原始对象。
/// </summary>
public sealed class AvaloniaFileRef : IFileRef
{
    /// <summary>
    /// 创建包装。
    /// </summary>
    /// <param name="inner">被包装的 Avalonia 存储文件。</param>
    public AvaloniaFileRef(IStorageFile inner)
    {
        Inner = inner;
    }

    /// <summary>被包装的 Avalonia 存储文件。</summary>
    public IStorageFile Inner { get; }

    /// <inheritdoc/>
    public string Name => Inner.Name;

    /// <inheritdoc/>
    public Uri Path => Inner.Path;

    /// <inheritdoc/>
    public string PathKey => Inner.Path.LocalPath;

    /// <inheritdoc/>
    public string? TryGetLocalPath() => Inner.TryGetLocalPath();

    /// <inheritdoc/>
    public async Task<Stream> OpenReadAsync() => await Inner.OpenReadAsync();

    /// <inheritdoc/>
    public async Task<StorageItemBasicProperties> GetBasicPropertiesAsync()
    {
        var props = await Inner.GetBasicPropertiesAsync();
        return new StorageItemBasicProperties(props.Size, props.DateModified);
    }

    /// <inheritdoc/>
    public async Task<IFolderRef?> GetParentAsync()
    {
        var parent = await Inner.GetParentAsync();
        return parent == null ? null : new AvaloniaFolderRef(parent);
    }
}

/// <summary>
/// <see cref="IFolderRef"/> 的 Avalonia 适配器,包装 <see cref="IStorageFolder"/>。
/// </summary>
public sealed class AvaloniaFolderRef : IFolderRef
{
    /// <summary>
    /// 创建包装。
    /// </summary>
    /// <param name="inner">被包装的 Avalonia 存储文件夹。</param>
    public AvaloniaFolderRef(IStorageFolder inner)
    {
        Inner = inner;
    }

    /// <summary>被包装的 Avalonia 存储文件夹。</summary>
    public IStorageFolder Inner { get; }

    /// <inheritdoc/>
    public string Name => Inner.Name;

    /// <inheritdoc/>
    public Uri Path => Inner.Path;

    /// <inheritdoc/>
    public string PathKey => Inner.Path.LocalPath;

    /// <inheritdoc/>
    public string? TryGetLocalPath() => Inner.TryGetLocalPath();

    /// <inheritdoc/>
    public async IAsyncEnumerable<IStorageItemRef> GetItemsAsync()
    {
        await foreach (var item in Inner.GetItemsAsync())
        {
            yield return AvaloniaStorageRefs.Wrap(item);
        }
    }
}

/// <summary>
/// Avalonia 存储项与平台中立引用的互转入口。
/// </summary>
public static class AvaloniaStorageRefs
{
    /// <summary>
    /// 包装任意存储项(文件或文件夹)。
    /// </summary>
    /// <param name="item">Avalonia 存储项。</param>
    /// <returns>平台中立引用。</returns>
    /// <exception cref="NotSupportedException">未知的存储项类型。</exception>
    public static IStorageItemRef Wrap(IStorageItem item) => item switch
    {
        IStorageFile file => new AvaloniaFileRef(file),
        IStorageFolder folder => new AvaloniaFolderRef(folder),
        _ => throw new NotSupportedException($"不支持的存储项类型:{item.GetType().FullName}"),
    };

    /// <summary>
    /// 包装文件。
    /// </summary>
    /// <param name="file">Avalonia 存储文件。</param>
    /// <returns>平台中立文件引用。</returns>
    public static IFileRef WrapFile(IStorageFile file) => new AvaloniaFileRef(file);

    /// <summary>
    /// 包装文件夹。
    /// </summary>
    /// <param name="folder">Avalonia 存储文件夹。</param>
    /// <returns>平台中立文件夹引用。</returns>
    public static IFolderRef WrapFolder(IStorageFolder folder) => new AvaloniaFolderRef(folder);
}
