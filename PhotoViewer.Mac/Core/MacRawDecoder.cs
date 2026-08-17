using System;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using PhotoViewer.Core.Image;

namespace PhotoViewer.Mac.Core;

/// <summary>
/// macOS RAW 解码器：复用 <see cref="MacHeifDecoder"/> 的 ImageIO 解码路径。
/// ImageIO 对 RAW 文件透明地走系统 RAW 引擎渲染（含 EXIF 方向），
/// 支持的机型/压缩格式以当前 macOS 版本为准；解码失败时记录原因到 <see cref="RawLoader.LastDecodeError"/>。
/// </summary>
public sealed class MacRawDecoder : IRawDecoder
{
    public bool IsSupported => OperatingSystem.IsMacOS();

    public Task<Bitmap?> LoadBitmapAsync(IStorageFile file)
        => Task.Run(() => Decode(file.Path.LocalPath, null));

    public Task<Bitmap?> LoadThumbnailAsync(IStorageFile file, int maxSize)
        => Task.Run(() => Decode(file.Path.LocalPath, maxSize));

    private static Bitmap? Decode(string path, int? maxSize)
    {
        var bmp = MacHeifDecoder.DecodeWithImageIO(path, maxSize);
        if (bmp == null)
            RawLoader.SetLastDecodeError("ImageIO 无法解码该 RAW 文件（当前 macOS 版本可能不支持该机型或压缩格式）");
        return bmp;
    }
}
