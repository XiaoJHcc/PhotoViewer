using Foundation;
using PhotoViewer.Core.Storage;

namespace PhotoViewer.iOS.Core;

/// <summary>
/// 打开会话的访问租约工厂。
/// 系统交回的原 URL（文档选择器、openURL、拖入）随存储引用保留，租约直接用这些实例成对
/// Start/Stop；沙盒自有路径继续走共享 <see cref="LocalStorageAccessSessionFactory"/>，不占用系统作用域。
/// 沙盒边界取数据容器根（Documents 的父目录，含 Documents / Library / tmp）与 app bundle 自身，
/// 判定只做词法绝对化与 /private/var ⇄ /var 别名归一，不探测文件系统。
/// 从不按路径重建 URL —— 重建出的 URL 不带沙盒扩展，启动作用域必然失败。
/// </summary>
public sealed class iOSStorageAccessSessionFactory : IStorageAccessSessionFactory
{
    private static readonly LocalStorageAccessSessionFactory Local = new();
    private readonly ISecurityScopeAccess _scope;

    /// <summary>创建使用平台安全作用域实现的工厂。</summary>
    public iOSStorageAccessSessionFactory()
        : this(PlatformSecurityScopeAccess.Instance)
    {
    }

    /// <summary>创建工厂并注入系统作用域调用面（验收替身用）。</summary>
    /// <param name="scope">系统作用域调用面。</param>
    internal iOSStorageAccessSessionFactory(ISecurityScopeAccess scope)
        => _scope = scope ?? throw new ArgumentNullException(nameof(scope));

    /// <summary>
    /// 为一次打开建立访问租约：外部目标用原 URL 启动系统作用域，沙盒目标交给本地会话。
    /// </summary>
    /// <param name="root">本次打开的根项（文件夹或单图文件）。</param>
    /// <returns>随打开会话释放的访问租约。</returns>
    /// <exception cref="StorageException">非本地项，或外部目标的系统授权已失效。</exception>
    public IStorageAccessSession Create(IStorageItemRef root)
    {
        ArgumentNullException.ThrowIfNull(root);
        var path = RequireLocalPath(root);

        if (root is IIOSScopedStorageItem { SecurityScopedUrls.Count: > 0 } scoped)
        {
            var session = new iOSStorageAccessSession(_scope);
            foreach (var url in scoped.SecurityScopedUrls)
            {
                if (session.TryAcquire(url)) return new ScopedSession(session);
            }

            session.Dispose();
            // 作用域启动失败但路径位于沙盒内：系统本就不需要作用域（例如选择器选中了 App 自己的目录）。
            if (!RequiresSecurityScope(path)) return Local.Create(root);
            throw new StorageException(StorageError.AccessDenied, $"{root.Name} 的系统访问授权已失效，请重新选择");
        }

        if (!RequiresSecurityScope(path)) return Local.Create(root);

        // 外部路径却没有系统交回的 URL：不猜一条重建 URL，直接按未授权上报（重新选择即可恢复）。
        throw new StorageException(
            StorageError.AccessDenied,
            $"{root.Name} 缺少系统授予的文件 URL，无法启动安全作用域，请重新选择");
    }

    /// <summary>判断路径是否在 App 沙盒之外（沙盒内不需要系统作用域）。</summary>
    /// <param name="path">绝对路径。</param>
    /// <returns>需要系统作用域时返回 true。</returns>
    private static bool RequiresSecurityScope(string path)
    {
        var dataRoot = SandboxDataRoot();
        if (dataRoot != null && IsWithin(path, dataRoot)) return false;
        var bundle = NSBundle.MainBundle.BundlePath;
        return !IsWithin(path, bundle);
    }

    /// <summary>
    /// App 沙盒数据根（真机 = 数据容器根，含 Documents / Library / tmp）。
    /// 由 <see cref="Environment.SpecialFolder.MyDocuments"/> 的父目录推导；取不到时返回 null，按外部路径处理（fail closed）。
    /// </summary>
    /// <returns>沙盒数据根绝对路径；无法确定时为 null。</returns>
    private static string? SandboxDataRoot()
    {
        var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        if (string.IsNullOrWhiteSpace(documents)) return null;
        var root = Path.GetDirectoryName(Path.GetFullPath(documents));
        return string.IsNullOrEmpty(root) ? null : root;
    }

    /// <summary>
    /// 词法绝对化并归一 Apple 的 /private/var 与 /var 别名。只做路径字符串运算，不探测文件系统
    /// （作用域外探测外部目标必然失败）。
    /// </summary>
    /// <param name="path">绝对或相对路径。</param>
    /// <returns>归一后的绝对路径。</returns>
    private static string NormalizeAbsolute(string path)
    {
        var full = Path.GetFullPath(path);
        return full == PrivateVar || full.StartsWith(PrivateVar + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            ? full[PrivatePrefix.Length..]
            : full;
    }

    /// <summary>判断路径等于根目录本身或位于其下（按目录分隔符比较，前缀相似的兄弟目录不算在内）。</summary>
    /// <param name="path">待判断路径。</param>
    /// <param name="root">根目录；为空表示无法判断。</param>
    /// <returns>位于根目录内时返回 true。</returns>
    private static bool IsWithin(string path, string? root)
    {
        if (string.IsNullOrEmpty(root)) return false;
        var normalizedPath = NormalizeAbsolute(path);
        var normalizedRoot = NormalizeAbsolute(root).TrimEnd(Path.DirectorySeparatorChar);
        if (normalizedRoot.Length == 0) return false;
        return normalizedPath == normalizedRoot
            || normalizedPath.StartsWith(normalizedRoot + Path.DirectorySeparatorChar, StringComparison.Ordinal);
    }

    /// <summary>Apple 卷别名：/private/var 与 /var 指向同一目录，比较前统一剥掉 /private。</summary>
    private const string PrivateVar = "/private/var";

    /// <summary>别名前缀，用于剥掉 /private。</summary>
    private const string PrivatePrefix = "/private";

    /// <summary>取存储项的本地绝对路径；非本地项不支持。</summary>
    /// <param name="item">文件或目录引用。</param>
    /// <returns>规范化绝对路径。</returns>
    private static string RequireLocalPath(IStorageItemRef item)
    {
        var path = item.TryGetLocalPath();
        if (string.IsNullOrWhiteSpace(path))
            throw new StorageException(StorageError.Unsupported, $"{item.Name} 不是 iOS 本地文件 URL");
        return Path.GetFullPath(path);
    }

    /// <summary>持有系统作用域的会话外观：租约释放时成对结束全部作用域。</summary>
    private sealed class ScopedSession(iOSStorageAccessSession session) : IStorageAccessSession
    {
        /// <summary>
        /// 附加一项：外部项用其原 URL 追加作用域；同目录内的本地子项已在根作用域内，不新增系统作用域。
        /// </summary>
        /// <param name="item">要附加的存储项。</param>
        public void Attach(IStorageItemRef item)
        {
            ArgumentNullException.ThrowIfNull(item);
            if (item is IIOSScopedStorageItem { SecurityScopedUrls.Count: > 0 } scoped)
            {
                foreach (var url in scoped.SecurityScopedUrls)
                {
                    if (session.TryAcquire(url)) return;
                }

                throw new StorageException(StorageError.AccessDenied, $"{item.Name} 的系统访问授权已失效，请重新选择");
            }

            if (item.TryGetLocalPath() == null)
                throw new StorageException(StorageError.Unsupported, "iOS 存储会话只支持本地项");
        }

        /// <summary>释放租约并结束本会话启动的全部系统作用域。</summary>
        public void Dispose() => session.Dispose();
    }
}
