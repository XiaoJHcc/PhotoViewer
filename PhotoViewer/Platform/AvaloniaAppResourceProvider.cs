using System;
using System.IO;
using Avalonia.Platform;
using PhotoViewer.Core.Platform;

namespace PhotoViewer.Platform;

/// <summary>
/// Avalonia 实现的应用资源访问,包装 <see cref="AssetLoader"/>。
/// </summary>
public sealed class AvaloniaAppResourceProvider : IAppResourceProvider
{
    /// <summary>
    /// 打开资源流;不存在返回 null。
    /// </summary>
    /// <param name="uri">avares:// 资源 URI。</param>
    /// <returns>资源流或 null。</returns>
    public Stream? OpenAsset(Uri uri)
    {
        try
        {
            return AssetLoader.Open(uri);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"AvaloniaAppResourceProvider.OpenAsset failed ({uri}): {ex.Message}");
            return null;
        }
    }
}
