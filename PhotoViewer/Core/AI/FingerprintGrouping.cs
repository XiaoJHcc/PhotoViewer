using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using PhotoViewer.Core.Database;
using PhotoViewer.Core.Image;

namespace PhotoViewer.Core.AI;

/// <summary>一组同指纹的文件，代表文件按解码代价从低到高排序。</summary>
public sealed class FingerprintGroup
{
    public string Fingerprint { get; init; } = "";
    public PhotoFingerprintInput Input { get; init; } = null!;
    public IReadOnlyList<ImageFile> Files { get; init; } = Array.Empty<ImageFile>();
    public ImageFile Representative => Files[0];
}

/// <summary>只读指纹分组，供批量索引与抖动徽标共用。</summary>
public static class FingerprintGrouping
{
    public static async Task<IReadOnlyList<FingerprintGroup>> GroupByFingerprintAsync(
        IReadOnlyList<ImageFile> files, CancellationToken cancellationToken = default)
    {
        var indexed = new Dictionary<string, List<ImageFile>>();
        var indexedInputs = new Dictionary<string, PhotoFingerprintInput>();
        var orphans = new List<FingerprintGroup>();

        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var exif = await file.LoadExifDataAsync().ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                if (!file.ModifiedDate.HasValue)
                    await file.LoadBasicPropertiesAsync(cancellationToken).ConfigureAwait(false);

                var input = PhotoFingerprint.BuildInput(file.Name, exif, file.ModifiedDate?.UtcDateTime);
                if (!input.CaptureTime.HasValue)
                {
                    orphans.Add(new FingerprintGroup { Fingerprint = "", Input = input, Files = new[] { file } });
                    continue;
                }

                var fingerprint = PhotoFingerprint.Compute(input);
                if (!indexed.TryGetValue(fingerprint, out var list))
                {
                    list = new List<ImageFile>();
                    indexed[fingerprint] = list;
                    indexedInputs[fingerprint] = input;
                }
                list.Add(file);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[FingerprintGrouping] group skip {file.Name}: {ex.Message}");
                var fallbackInput = PhotoFingerprint.BuildInput(file.Name, null, null);
                orphans.Add(new FingerprintGroup { Fingerprint = "", Input = fallbackInput, Files = new[] { file } });
            }
        }
        cancellationToken.ThrowIfCancellationRequested();

        var result = new List<FingerprintGroup>(indexed.Count + orphans.Count);
        foreach (var (fingerprint, list) in indexed)
        {
            list.Sort((a, b) => DecodeCostScore(a.Name).CompareTo(DecodeCostScore(b.Name)));
            result.Add(new FingerprintGroup { Fingerprint = fingerprint, Input = indexedInputs[fingerprint], Files = list });
        }
        result.AddRange(orphans);
        return result;
    }

    private static int DecodeCostScore(string name)
    {
        var ext = Path.GetExtension(name).ToLowerInvariant();
        return ext switch
        {
            ".heif" or ".heic" or ".hif" => 0,
            ".jpg" or ".jpeg" => 1,
            ".png" or ".webp" or ".bmp" or ".gif" or ".tiff" or ".tif" => 2,
            _ => 3,
        };
    }
}
