using System;
using System.IO;
using PhotoViewer.Core.Platform;

namespace PhotoViewer.Android.Core;

/// <summary>
/// 从 APK 的 <c>Assets/</c> 读取共享打包资源（DINO 模型等）。
/// csproj 用 <c>AndroidAsset</c> 把 <c>PhotoViewer/Assets/Models/*.onnx</c> 链到 <c>Assets/Models/</c>。
/// </summary>
public sealed class AndroidAppResourceProvider : IAppResourceProvider
{
    /// <summary>
    /// 按 <c>avares://</c> URI 的路径部分打开 Asset 流。
    /// </summary>
    /// <param name="uri">共享资源 URI（路径部分即 APK 内相对路径）。</param>
    /// <returns>资源流；不存在返回 null。</returns>
    public Stream? OpenAsset(Uri uri)
    {
        string relative = uri.AbsolutePath.TrimStart('/');
        // avares://PhotoViewer/Assets/Models/x.onnx → AssetManager 根是 assets/，
        // 打包后的路径是 assets/Models/x.onnx，因此去掉开头的 Assets/。
        if (relative.StartsWith("Assets/", StringComparison.Ordinal))
        {
            relative = relative["Assets/".Length..];
        }

        try
        {
            return global::Android.App.Application.Context.Assets?.Open(relative);
        }
        catch (Exception exception)
        {
            Console.WriteLine($"[Native] AndroidAppResourceProvider.OpenAsset failed ({relative}): {exception.Message}");
            return null;
        }
    }
}
