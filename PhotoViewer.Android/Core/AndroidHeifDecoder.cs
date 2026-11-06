using System.Threading;
using System.Threading.Tasks;
using PhotoViewer.Core.Image;
using PhotoViewer.Core.Image.Heif;
using PhotoViewer.Core.Storage;

namespace PhotoViewer.Android.Core;

public sealed class AndroidHeifDecoder : IHeifDecoder
{
    public bool IsSupported => OperatingSystem.IsAndroid();
    public bool OutputPreRotated => true;

    public async Task<DecodedImage?> DecodeAsync(IFileRef file, int? maxSize)
    {
        if (!IsSupported || file is null) return null;
        var bytes = await FastHeifDecoder.ReadAllBytesAsync(file).ConfigureAwait(false);
        return await HeifLoader.RunCpuDecodeAsync(() => Task.Run(() =>
            LibHeifColorDecoder.Decode(bytes, maxSize, LibHeifOutput.Bgra32Srgb,
                CancellationToken.None, out _))).ConfigureAwait(false);
    }
}
