using System.IO;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using PhotoViewer.Core;

namespace PhotoViewer.Mac.Core;

public sealed class MacHeifDecoder : IHeifDecoder
{
    public bool IsSupported => false; // 后续接入 macOS 原生解码后改为 true

    public bool IsHeifFile(string filePath)
        => !string.IsNullOrEmpty(filePath) &&
           new[] { ".heif", ".heic", ".avif", ".hif" }
               .Contains(Path.GetExtension(filePath).ToLowerInvariant());

    public Task<Bitmap?> LoadBitmapAsync(IStorageFile file) => Task.FromResult<Bitmap?>(null);
    public Task<Bitmap?> LoadThumbnailAsync(IStorageFile file, int maxSize) => Task.FromResult<Bitmap?>(null);
    public Task<Bitmap?> LoadBitmapFromStreamAsync(Stream stream) => Task.FromResult<Bitmap?>(null);
    public Task<Bitmap?> LoadThumbnailFromStreamAsync(Stream stream, int maxSize) => Task.FromResult<Bitmap?>(null);
}