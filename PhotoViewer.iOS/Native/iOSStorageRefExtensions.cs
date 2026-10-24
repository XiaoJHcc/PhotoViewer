using PhotoViewer.Core.Storage;

namespace PhotoViewer.iOS.Native;

/// <summary>验收代码的存储引用辅助：Apple 端正常只有本地来源，缺失真实路径即为接线错误。</summary>
internal static class iOSStorageRefExtensions
{
    /// <summary>取存储项的真实本地路径；非本地来源直接失败，不把 StorageId 当路径用。</summary>
    /// <param name="item">文件或目录引用。</param>
    /// <returns>本地绝对路径。</returns>
    internal static string RequireLocalPath(this IStorageItemRef item)
        => item.TryGetLocalPath() ?? throw new InvalidOperationException($"验收要求本地存储项：{item.Name}");
}
