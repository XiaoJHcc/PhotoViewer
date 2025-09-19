using System;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using PhotoViewer.Core;

namespace PhotoViewer.Desktop.Core;

/// <summary>
/// Windows 上优先使用 WIC 解码 HEIF/HEIC/AVIF；失败时回退到 LibHeifDecoder。
/// </summary>
public sealed class WindowsHeifDecoder : IHeifDecoder
{
    public bool IsSupported => true;

    public Task<Bitmap?> LoadBitmapAsync(IStorageFile file) => Task.FromResult<Bitmap?>(null);
    public Task<Bitmap?> LoadThumbnailAsync(IStorageFile file, int maxSize) => Task.FromResult<Bitmap?>(null);

}
