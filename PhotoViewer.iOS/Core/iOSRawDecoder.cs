using System;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Foundation;
using PhotoViewer.Core.Image;

namespace PhotoViewer.iOS.Core;

/// <summary>
/// iOS RAW 解码器：复用 <see cref="iOSHeifDecoder"/> 的 ImageIO 解码路径。
/// ImageIO 对 RAW 文件透明地走系统 RAW 引擎渲染（含 EXIF 方向），
/// 支持的机型/压缩格式以当前 iOS 版本为准；解码失败时记录原因到 <see cref="RawLoader.LastDecodeError"/>。
/// </summary>
[Preserve(AllMembers = true)]
public sealed class iOSRawDecoder : IRawDecoder
{
    public bool IsSupported => OperatingSystem.IsIOS();

    public async Task<Bitmap?> LoadBitmapAsync(IStorageFile file)
    {
        var (path, data) = await iOSHeifDecoder.PreparePathOrDataAsync(file);
        var bmp = iOSHeifDecoder.DecodeWithImageIO(path, null, data);
        if (bmp == null)
            RawLoader.SetLastDecodeError("ImageIO 无法解码该 RAW 文件（当前 iOS 版本可能不支持该机型或压缩格式）");
        return bmp;
    }

    public async Task<Bitmap?> LoadThumbnailAsync(IStorageFile file, int maxSize)
    {
        var (path, data) = await iOSHeifDecoder.PreparePathOrDataAsync(file);
        return iOSHeifDecoder.DecodeWithImageIO(path, maxSize, data);
    }
}
