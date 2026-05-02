using System;
using Avalonia.Platform.Storage;

namespace PhotoViewer.Core;

/// <summary>
/// 平台存储访问协调器。
/// 用于在共享层触发平台特定的长期授权保留与短时访问作用域。
/// </summary>
public interface IPlatformStorageAccessManager
{
    /// <summary>
    /// 尝试为存储项保留长期访问权限。
    /// 适用于文件打开后需要持续读取或后续写入的场景。
    /// </summary>
    /// <param name="item">目标存储项</param>
    void Retain(IStorageItem item);

    /// <summary>
    /// 尝试获取一次性的访问作用域。
    /// 调用方应在作用域结束后释放返回值。
    /// </summary>
    /// <param name="item">目标存储项</param>
    /// <returns>访问作用域；平台不需要时返回 null</returns>
    IDisposable? TryAcquireScope(IStorageItem item);
}

/// <summary>
/// 存储访问门面。
/// 共享层通过它协调平台侧的安全作用域或其他访问授权。
/// </summary>
public static class StorageAccessManager
{
    private static IPlatformStorageAccessManager _manager = new NoopStorageAccessManager();

    /// <summary>
    /// 注入平台存储访问实现。
    /// </summary>
    /// <param name="manager">平台实现</param>
    public static void Initialize(IPlatformStorageAccessManager manager)
    {
        _manager = manager ?? new NoopStorageAccessManager();
    }

    /// <summary>
    /// 尝试为存储项保留长期访问权限。
    /// </summary>
    /// <param name="item">目标存储项</param>
    public static void Retain(IStorageItem? item)
    {
        if (item == null)
        {
            return;
        }

        _manager.Retain(item);
    }

    /// <summary>
    /// 尝试获取一次性的访问作用域。
    /// </summary>
    /// <param name="item">目标存储项</param>
    /// <returns>访问作用域；平台不需要时返回 null</returns>
    public static IDisposable? TryAcquireScope(IStorageItem? item)
    {
        return item == null ? null : _manager.TryAcquireScope(item);
    }

    /// <summary>
    /// 默认空实现。
    /// </summary>
    private sealed class NoopStorageAccessManager : IPlatformStorageAccessManager
    {
        /// <summary>
        /// 空实现，不保留任何访问权限。
        /// </summary>
        /// <param name="item">目标存储项</param>
        public void Retain(IStorageItem item)
        {
        }

        /// <summary>
        /// 空实现，不创建访问作用域。
        /// </summary>
        /// <param name="item">目标存储项</param>
        /// <returns>始终返回 null</returns>
        public IDisposable? TryAcquireScope(IStorageItem item)
        {
            return null;
        }
    }
}