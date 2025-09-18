using System.IO;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using PhotoViewer.Core;

namespace PhotoViewer.Mac.Core;

public sealed class MacHeifDecoder : IHeifDecoder
{
    public bool IsSupported => false; // 后续接入 macOS 原生解码后改为 true

    public Task<Bitmap?> LoadBitmapAsync(IStorageFile file) => Task.FromResult<Bitmap?>(null);
    public Task<Bitmap?> LoadThumbnailAsync(IStorageFile file, int maxSize) => Task.FromResult<Bitmap?>(null);
}