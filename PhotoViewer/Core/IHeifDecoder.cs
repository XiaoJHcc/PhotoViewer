using System.IO;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;

namespace PhotoViewer.Core;

public interface IHeifDecoder
{
    bool IsSupported { get; }
    
    Task<Bitmap?> LoadBitmapAsync(IStorageFile file);
    Task<Bitmap?> LoadThumbnailAsync(IStorageFile file, int maxSize);

}