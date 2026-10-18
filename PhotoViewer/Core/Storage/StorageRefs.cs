using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;

namespace PhotoViewer.Core.Storage;

/// <summary>
/// 平台中立的存储项引用(文件或文件夹)。
/// 语义逐项对应通用文件选择器的存储项,UI 层经适配器包装得到。
/// </summary>
public interface IStorageItemRef
{
    /// <summary>显示名(文件名/文件夹名)。</summary>
    string Name { get; }

    /// <summary>平台路径 URI(日志与相等性比较用,与原 IStorageItem.Path 同值)。</summary>
    Uri Path { get; }

    /// <summary>路径键(与原 Path.LocalPath 同值),用作缓存键/指纹等标识场景。</summary>
    string PathKey { get; }

    /// <summary>
    /// 尝试获取真实文件系统路径。
    /// </summary>
    /// <returns>本地路径;非本地项(SAF 内容 URI 等)返回 null。</returns>
    string? TryGetLocalPath();
}

/// <summary>
/// 平台中立的文件引用。
/// </summary>
public interface IFileRef : IStorageItemRef
{
    /// <summary>
    /// 打开只读流。调用方负责释放。
    /// </summary>
    /// <returns>只读流。</returns>
    Task<Stream> OpenReadAsync();

    /// <summary>
    /// 读取基本属性(大小、修改日期)。
    /// </summary>
    /// <returns>基本属性。</returns>
    Task<StorageItemBasicProperties> GetBasicPropertiesAsync();

    /// <summary>
    /// 获取父文件夹;平台权限不支持时返回 null。
    /// </summary>
    /// <returns>父文件夹引用或 null。</returns>
    Task<IFolderRef?> GetParentAsync();
}

/// <summary>
/// 平台中立的文件夹引用。
/// </summary>
public interface IFolderRef : IStorageItemRef
{
    /// <summary>
    /// 枚举文件夹直接子项(文件与文件夹混合,不递归)。
    /// </summary>
    /// <returns>子项异步序列。</returns>
    IAsyncEnumerable<IStorageItemRef> GetItemsAsync();
}

/// <summary>
/// 存储项基本属性(与原 StorageItemProperties 的可空语义一致)。
/// </summary>
public sealed class StorageItemBasicProperties
{
    /// <summary>
    /// 创建基本属性。
    /// </summary>
    /// <param name="size">字节数;未知为 null。</param>
    /// <param name="dateModified">修改时间;未知为 null。</param>
    public StorageItemBasicProperties(ulong? size, DateTimeOffset? dateModified)
    {
        Size = size;
        DateModified = dateModified;
    }

    /// <summary>字节数;未知为 null。</summary>
    public ulong? Size { get; }

    /// <summary>修改时间;未知为 null。</summary>
    public DateTimeOffset? DateModified { get; }
}
