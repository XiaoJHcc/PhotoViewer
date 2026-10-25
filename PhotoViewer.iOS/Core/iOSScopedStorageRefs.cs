using Foundation;
using PhotoViewer.Core.Storage;

namespace PhotoViewer.iOS.Core;

/// <summary>
/// 携带系统授予原 URL 的本地存储引用。
/// 引用本身只保留 URL 实例（授权仍需按次 Start/Stop），不长期持有系统作用域；
/// 打开会话的 <see cref="iOSStorageAccessSession"/> 优先用这里的 URL 建立租约，
/// 避免按路径重建 URL 后系统作用域启动失败。
/// </summary>
internal interface IIOSScopedStorageItem : IStorageItemRef
{
    /// <summary>系统交回的原 URL，顺序即授予顺序；Start/Stop 必须用这些实例。</summary>
    IReadOnlyList<NSUrl> SecurityScopedUrls { get; }
}

/// <summary>系统 URL 在作用域内解析出的本地目标。</summary>
/// <param name="LocalPath">规范化的绝对路径。</param>
/// <param name="IsDirectory">目标是目录。</param>
/// <param name="SecurityScoped">解析时系统授予了安全作用域（引用需保留原 URL）。</param>
internal readonly record struct iOSSystemUrlTarget(string LocalPath, bool IsDirectory, bool SecurityScoped);

/// <summary>系统 URL → 本地存储引用：外部目标保留原 URL，沙盒目标继续走共享 Local 引用。</summary>
internal static class iOSStorageRefs
{
    /// <summary>按选择器结果建立目录引用（沙盒内的目录仍返回共享 <see cref="LocalFolderRef"/>）。</summary>
    /// <param name="url">文档选择器返回的原 URL。</param>
    /// <param name="scope">系统作用域调用面；产品路径留空即用平台实现。</param>
    /// <returns>目录引用；URL 不是本地文件时返回 null。</returns>
    internal static IFolderRef? CreateFolderRef(NSUrl? url, ISecurityScopeAccess? scope = null)
    {
        if (url == null || !TryGetFullPath(url, out var path)) return null;
        return HasSecurityScope(url, scope) ? new iOSScopedFolderRef(path, [url]) : new LocalFolderRef(path);
    }

    /// <summary>按选择器结果建立文件引用（沙盒内的文件仍返回共享 <see cref="LocalFileRef"/>）。</summary>
    /// <param name="url">文档选择器返回的原 URL。</param>
    /// <param name="scope">系统作用域调用面；产品路径留空即用平台实现。</param>
    /// <returns>文件引用；URL 不是本地文件时返回 null。</returns>
    internal static IFileRef? CreateFileRef(NSUrl? url, ISecurityScopeAccess? scope = null)
    {
        if (url == null || !TryGetFullPath(url, out var path)) return null;
        return HasSecurityScope(url, scope) ? new iOSScopedFileRef(path, [url], scope) : new LocalFileRef(path);
    }

    /// <summary>按作用域内的解析结果建立引用（<see cref="iOSExternalOpenBridge"/> 使用）。</summary>
    /// <param name="url">系统传入的原 URL。</param>
    /// <param name="target">作用域内解析出的本地目标。</param>
    /// <param name="scope">系统作用域调用面；产品路径留空即用平台实现。</param>
    /// <returns>目录走文件夹引用，其余走文件引用。</returns>
    internal static IStorageItemRef Create(NSUrl url, iOSSystemUrlTarget target, ISecurityScopeAccess? scope = null)
    {
        if (!target.SecurityScoped)
        {
            return target.IsDirectory ? new LocalFolderRef(target.LocalPath) : new LocalFileRef(target.LocalPath);
        }

        return target.IsDirectory
            ? new iOSScopedFolderRef(target.LocalPath, [url])
            : new iOSScopedFileRef(target.LocalPath, [url], scope);
    }

    /// <summary>读取本地文件 URL 的规范化路径；非文件 URL 或路径为空时返回 false。</summary>
    /// <param name="url">候选 URL。</param>
    /// <param name="path">规范化绝对路径。</param>
    /// <returns>可用于本地存储引用时返回 true。</returns>
    private static bool TryGetFullPath(NSUrl? url, out string path)
    {
        path = string.Empty;
        if (url == null || !url.IsFileUrl || string.IsNullOrWhiteSpace(url.Path))
        {
            return false;
        }

        try
        {
            path = Path.GetFullPath(url.Path);
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[StorageAccess] iOS 忽略无效路径：{ex.Message}");
            return false;
        }
    }

    /// <summary>判断系统是否为该 URL 授予了安全作用域（成对启动/结束一次后立即归还）。</summary>
    /// <param name="url">系统交回的 URL。</param>
    /// <param name="scope">系统作用域调用面。</param>
    /// <returns>URL 携带系统授予的作用域时返回 true。</returns>
    private static bool HasSecurityScope(NSUrl url, ISecurityScopeAccess? scope)
        => iOSSecurityScope.TryProbe(scope ?? PlatformSecurityScopeAccess.Instance, url, static () => true, out bool started)
            && started;
}

/// <summary>
/// 外部文件引用：委托共享 <see cref="LocalFileRef"/>，并保留系统交回的原 URL。
/// 沙盒目录里的文件不需要本类型。
/// </summary>
internal sealed class iOSScopedFileRef : IFileRef, IIOSScopedStorageItem
{
    private readonly LocalFileRef _inner;
    private readonly ISecurityScopeAccess _scope;

    /// <summary>创建文件引用。</summary>
    /// <param name="localPath">规范化绝对路径。</param>
    /// <param name="scopedUrls">系统授予的原 URL（至少一个）。</param>
    /// <param name="scope">系统作用域调用面；产品路径留空即用平台实现。</param>
    internal iOSScopedFileRef(string localPath, IReadOnlyList<NSUrl> scopedUrls, ISecurityScopeAccess? scope = null)
    {
        _inner = new LocalFileRef(localPath);
        SecurityScopedUrls = [.. scopedUrls];
        _scope = scope ?? PlatformSecurityScopeAccess.Instance;
    }

    /// <summary>系统交回的原 URL。</summary>
    public IReadOnlyList<NSUrl> SecurityScopedUrls { get; }

    /// <summary>文件名。</summary>
    public string Name => _inner.Name;

    /// <summary>存储地址（本地绝对路径的 URI）。</summary>
    public Uri Path => _inner.Path;

    /// <summary>存储身份。</summary>
    public StorageItemId StorageId => _inner.StorageId;

    /// <summary>本地能力集。</summary>
    public StorageCapabilities Capabilities => _inner.Capabilities;

    /// <summary>本地绝对路径。</summary>
    /// <returns>绝对路径。</returns>
    public string? TryGetLocalPath() => _inner.TryGetLocalPath();

    /// <summary>读取文件（调用方须在打开会话租约内调用）。</summary>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>文件流。</returns>
    public Task<Stream> OpenReadAsync(CancellationToken cancellationToken = default)
        => _inner.OpenReadAsync(cancellationToken);

    /// <summary>读取基本属性。</summary>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>基本属性。</returns>
    public Task<StorageItemBasicProperties> GetBasicPropertiesAsync(CancellationToken cancellationToken = default)
        => _inner.GetBasicPropertiesAsync(cancellationToken);

    /// <summary>
    /// 父目录：只有在同一系统作用域内能真正枚举时才返回（外部单文件授权通常不含父目录），
    /// 否则返回 null 让共享流程按 OPEN-03 直接以单图模式打开，不产生一次注定失败的枚举。
    /// </summary>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>可枚举的父目录引用；不可枚举或没有父目录时为 null。</returns>
    public Task<IFolderRef?> GetParentAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_inner.TryGetLocalPath() is not { } localPath ||
            System.IO.Path.GetDirectoryName(localPath) is not { } parent)
        {
            return Task.FromResult<IFolderRef?>(null);
        }

        return Task.FromResult<IFolderRef?>(CanEnumerate(parent) ? new iOSScopedFolderRef(parent, SecurityScopedUrls) : null);
    }

    /// <summary>随机访问（本地文件直接支持）。</summary>
    /// <param name="mode">访问模式。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>随机访问会话。</returns>
    public Task<IStorageRandomAccessSession> OpenRandomAccessAsync(
        StorageAccessMode mode,
        CancellationToken cancellationToken = default)
        => _inner.OpenRandomAccessAsync(mode, cancellationToken);

    /// <summary>原子替换写回（评分等）。</summary>
    /// <param name="writeAsync">写入委托。</param>
    /// <param name="expectedRevision">期望版本。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>写入完成的任务。</returns>
    public Task AtomicWriteAsync(
        Func<Stream, CancellationToken, Task> writeAsync,
        StorageRevision? expectedRevision = null,
        CancellationToken cancellationToken = default)
        => _inner.AtomicWriteAsync(writeAsync, expectedRevision, cancellationToken);

    /// <summary>删除文件。</summary>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>删除完成的任务。</returns>
    public Task DeleteAsync(CancellationToken cancellationToken = default) => _inner.DeleteAsync(cancellationToken);

    /// <summary>在系统授予的作用域内试枚举父目录：作用域不覆盖父目录时返回 false。</summary>
    /// <param name="directory">父目录绝对路径。</param>
    /// <returns>可枚举时返回 true。</returns>
    private bool CanEnumerate(string directory)
    {
        foreach (var url in SecurityScopedUrls)
        {
            bool accessible = iOSSecurityScope.TryProbe(_scope, url, () =>
            {
                using var entries = Directory.EnumerateFileSystemEntries(directory).GetEnumerator();
                entries.MoveNext();
                return true;
            }, out _);
            if (accessible)
            {
                return true;
            }
        }

        return false;
    }
}

/// <summary>
/// 外部目录引用：委托共享 <see cref="LocalFolderRef"/>，并保留系统交回的原 URL
/// （由外部单文件派生的父目录沿用该文件的 URL，租约仍持有已授予的那一份访问权限）。
/// </summary>
internal sealed class iOSScopedFolderRef : IFolderRef, IIOSScopedStorageItem
{
    private readonly LocalFolderRef _inner;

    /// <summary>创建目录引用。</summary>
    /// <param name="localPath">规范化绝对路径。</param>
    /// <param name="scopedUrls">系统授予的原 URL（至少一个）。</param>
    internal iOSScopedFolderRef(string localPath, IReadOnlyList<NSUrl> scopedUrls)
    {
        _inner = new LocalFolderRef(localPath);
        SecurityScopedUrls = [.. scopedUrls];
    }

    /// <summary>系统交回的原 URL。</summary>
    public IReadOnlyList<NSUrl> SecurityScopedUrls { get; }

    /// <summary>目录名。</summary>
    public string Name => _inner.Name;

    /// <summary>存储地址（带尾分隔符的本地目录 URI）。</summary>
    public Uri Path => _inner.Path;

    /// <summary>存储身份。</summary>
    public StorageItemId StorageId => _inner.StorageId;

    /// <summary>本地能力集。</summary>
    public StorageCapabilities Capabilities => _inner.Capabilities;

    /// <summary>本地绝对路径。</summary>
    /// <returns>绝对路径。</returns>
    public string? TryGetLocalPath() => _inner.TryGetLocalPath();

    /// <summary>枚举目录内容（调用方须在打开会话租约内调用）。</summary>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>子项流。</returns>
    public IAsyncEnumerable<IStorageItemRef> GetItemsAsync(CancellationToken cancellationToken = default)
        => _inner.GetItemsAsync(cancellationToken);

    /// <summary>按名称查找子文件。</summary>
    /// <param name="name">文件名。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>子文件引用；不存在时为 null。</returns>
    public Task<IFileRef?> FindFileAsync(string name, CancellationToken cancellationToken = default)
        => _inner.FindFileAsync(name, cancellationToken);

    /// <summary>新建子文件（同目录同名评分 sidecar 与结构写回使用）。</summary>
    /// <param name="name">文件名。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>子文件引用。</returns>
    public Task<IFileRef> CreateFileAsync(string name, CancellationToken cancellationToken = default)
        => _inner.CreateFileAsync(name, cancellationToken);
}
