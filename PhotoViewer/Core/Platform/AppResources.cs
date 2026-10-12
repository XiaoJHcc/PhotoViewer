using System;
using System.IO;

namespace PhotoViewer.Core.Platform;

/// <summary>
/// 应用打包资源访问接口(如内嵌 ONNX 模型)。由 UI 层实现,Core 经 <see cref="AppResources"/> 门面调用。
/// </summary>
public interface IAppResourceProvider
{
    /// <summary>
    /// 打开资源流。调用方负责释放。
    /// </summary>
    /// <param name="uri">资源 URI。</param>
    /// <returns>资源流;不存在返回 null。</returns>
    Stream? OpenAsset(Uri uri);
}

/// <summary>
/// 应用资源访问门面。启动期由 UI 层经 <see cref="Initialize"/> 注入实现。
/// </summary>
public static class AppResources
{
    private static IAppResourceProvider? _impl;

    /// <summary>
    /// 注入 UI 层的资源访问实现。
    /// </summary>
    /// <param name="impl">资源访问实现。</param>
    public static void Initialize(IAppResourceProvider impl)
    {
        _impl = impl ?? throw new ArgumentNullException(nameof(impl));
    }

    /// <summary>
    /// 打开资源流;未初始化或资源不存在时返回 null。
    /// </summary>
    /// <param name="uri">资源 URI。</param>
    /// <returns>资源流或 null。</returns>
    public static Stream? OpenAsset(Uri uri)
    {
        if (_impl == null)
        {
            Console.WriteLine("AppResources 未初始化:请在应用启动时调用 AppResources.Initialize。");
            return null;
        }
        return _impl.OpenAsset(uri);
    }
}
