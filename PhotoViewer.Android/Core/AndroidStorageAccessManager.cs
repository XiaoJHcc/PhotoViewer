using System;
using PhotoViewer.Core.Platform;
using PhotoViewer.Core.Storage;

namespace PhotoViewer.Android.Core;

/// <summary>
/// Android 存储访问实现：SAF 的持久授权在选择器返回时已由 <see cref="AndroidFilePicker"/> 取得，
/// 这里只保留共享接口语义（Retain 无额外动作，作用域不额外建立）。
/// </summary>
public sealed class AndroidStorageAccessManager : IPlatformStorageAccessManager
{
    /// <summary>保留访问权限（SAF 使用选择器返回时取得的持久授权，无需重复动作）。</summary>
    /// <param name="item">目标存储项。</param>
    public void Retain(IStorageItemRef item) { }

    /// <summary>不需要额外作用域。</summary>
    /// <param name="item">目标存储项。</param>
    /// <returns>始终 null。</returns>
    public IDisposable? TryAcquireScope(IStorageItemRef item) => null;
}
