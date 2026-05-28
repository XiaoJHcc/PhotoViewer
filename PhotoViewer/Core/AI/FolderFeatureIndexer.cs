using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using PhotoViewer.Core.Database;
using PhotoViewer.Core.Image;

namespace PhotoViewer.Core.AI;

/// <summary>
/// 一组同指纹的文件(同次曝光的 RAW/HEIF/JPG 共存),代表文件按解码代价从低到高排序。
/// </summary>
public sealed class FingerprintGroup
{
    /// <summary>组指纹;无法计算指纹的文件该字段为空字符串(每个文件独占一组)。</summary>
    public string Fingerprint { get; init; } = "";

    /// <summary>用于写库的指纹输入(取自代表文件)。</summary>
    public PhotoFingerprintInput Input { get; init; } = null!;

    /// <summary>组内文件,按解码代价升序排列(代表文件位于索引 0)。</summary>
    public IReadOnlyList<ImageFile> Files { get; init; } = Array.Empty<ImageFile>();

    /// <summary>代表文件:实际喂给 DINO 推理 + CV 提取的那一张。</summary>
    public ImageFile Representative => Files[0];
}

/// <summary>
/// 全文件夹批量特征提取调度器。
/// 对 <see cref="ImageFile"/> 列表按指纹聚合后,**一轮扫描同时提取 DINO CLS + patch token + CV grid 三类原始数据**
/// 并写入 <see cref="PhotoDatabase"/>。同次曝光的 RAW+HEIF/JPG 共享同一指纹,只跑一次。
///
/// 解码两路:DINO 走 560 短边 <see cref="ThumbnailService"/>,CV 走原始分辨率 <see cref="BitmapLoader"/>(共享 LRU)。
/// 桌面端半核并行解码 + 单线程 ONNX 推理;移动端单线程。
/// 单组失败跳过,不中断整批。本期不支持取消(切文件夹后任务继续后台跑完)。
/// </summary>
public sealed class FolderFeatureIndexer
{
    /// <summary>喂给 DINO 的图片短边像素,与 <see cref="DinoFeatureCache"/> 保持一致。</summary>
    private const int FeaturingShortSide = 560;

    private static readonly SemaphoreSlim _inferSemaphore = new(1, 1);

    private int _completed;
    private int _failed;
    private int _total;
    private string? _lastError;

    /// <summary>是否正在运行。</summary>
    public bool IsRunning { get; private set; }

    /// <summary>首个失败组的错误信息;无失败时为 null。</summary>
    public string? LastError => _lastError;

    /// <summary>进度事件:每组完成(成功或跳过)后触发。</summary>
    public event Action<IndexProgress>? ProgressChanged;

    /// <summary>
    /// 把文件列表按指纹聚合:同指纹的多个文件合并为一组,代表文件按解码代价升序(HEIF/HIF → JPG → 其他 → RAW),
    /// 优先用解码最便宜的那张做推理。无法计算指纹的文件每个独占一组,<see cref="FingerprintGroup.Fingerprint"/> 为空字符串。
    /// 单文件读取异常视同"无指纹",不阻断整批。
    /// </summary>
    /// <param name="files">原始文件列表(通常为 AllFiles)。</param>
    /// <returns>按指纹聚合后的组列表(顺序对最终结果不重要)。</returns>
    public static async Task<IReadOnlyList<FingerprintGroup>> GroupByFingerprintAsync(IReadOnlyList<ImageFile> files)
    {
        var indexed = new Dictionary<string, List<ImageFile>>();
        var indexedInputs = new Dictionary<string, PhotoFingerprintInput>();
        var orphans = new List<FingerprintGroup>();

        foreach (var file in files)
        {
            try
            {
                var exif = await file.LoadExifDataAsync().ConfigureAwait(false);
                if (!file.ModifiedDate.HasValue)
                    await file.LoadBasicPropertiesAsync().ConfigureAwait(false);

                var input = PhotoFingerprint.BuildInput(file.Name, exif, file.ModifiedDate?.UtcDateTime);
                if (!input.CaptureTime.HasValue)
                {
                    orphans.Add(new FingerprintGroup { Fingerprint = "", Input = input, Files = new[] { file } });
                    continue;
                }

                var fp = PhotoFingerprint.Compute(input);
                if (!indexed.TryGetValue(fp, out var list))
                {
                    list = new List<ImageFile>();
                    indexed[fp] = list;
                    indexedInputs[fp] = input;
                }
                list.Add(file);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[FolderFeatureIndexer] group skip {file.Name}: {ex.Message}");
                var fallbackInput = PhotoFingerprint.BuildInput(file.Name, null, null);
                orphans.Add(new FingerprintGroup { Fingerprint = "", Input = fallbackInput, Files = new[] { file } });
            }
        }

        var result = new List<FingerprintGroup>(indexed.Count + orphans.Count);
        foreach (var (fp, list) in indexed)
        {
            list.Sort((a, b) => DecodeCostScore(a.Name).CompareTo(DecodeCostScore(b.Name)));
            result.Add(new FingerprintGroup { Fingerprint = fp, Input = indexedInputs[fp], Files = list });
        }
        result.AddRange(orphans);
        return result;
    }

    /// <summary>
    /// 查询某指纹三路数据齐备情况;调用方据此决定是否跳过解码。
    /// </summary>
    public static Task<MissingParts> EvaluateMissingPartsAsync(string fingerprint)
    {
        return PhotoDatabase.EvaluateMissingPartsAsync(
            fingerprint, DinoModelResources.ModelId, CvGridResult.CurrentVersion);
    }

    /// <summary>解码代价评分:数字越小代表解码越快;同指纹组取分数最低的文件作代表。</summary>
    private static int DecodeCostScore(string name)
    {
        var ext = Path.GetExtension(name).ToLowerInvariant();
        return ext switch
        {
            ".heif" or ".heic" or ".hif" => 0,
            ".jpg" or ".jpeg" => 1,
            ".png" or ".webp" or ".bmp" or ".gif" or ".tiff" or ".tif" => 2,
            _ => 3, // RAW 等
        };
    }

    /// <summary>
    /// 对 <paramref name="files"/> 中尚未入库的照片批量提取三类特征。
    /// 同指纹的 RAW/HEIF/JPG 只算一次,进度按"指纹组"推进而非按文件数。
    /// </summary>
    /// <param name="files">待处理文件列表(按 AllFiles 顺序)。</param>
    public async Task RunAsync(IReadOnlyList<ImageFile> files)
    {
        if (IsRunning) return;
        IsRunning = true;

        var groups = await GroupByFingerprintAsync(files).ConfigureAwait(false);

        _completed = 0;
        _failed = 0;
        _total = groups.Count;
        _lastError = null;

        // 桌面端半核并行解码;移动端单线程
        int decodeConcurrency = OperatingSystem.IsAndroid() || OperatingSystem.IsIOS()
            ? 1
            : Math.Max(1, Environment.ProcessorCount / 2);

        var decodeSemaphore = new SemaphoreSlim(decodeConcurrency, decodeConcurrency);
        var tasks = new List<Task>(groups.Count);

        foreach (var group in groups)
        {
            var g = group;
            tasks.Add(Task.Run(async () =>
            {
                await decodeSemaphore.WaitAsync().ConfigureAwait(false);
                try
                {
                    await ProcessGroupAsync(g).ConfigureAwait(false);
                }
                finally
                {
                    decodeSemaphore.Release();
                }
            }));
        }

        await Task.WhenAll(tasks).ConfigureAwait(false);
        IsRunning = false;
    }

    /// <summary>
    /// 处理一个指纹组:三路按需补齐:
    /// 1. 评估缺失项(<see cref="EvaluateMissingPartsAsync"/>)
    /// 2. 若需要 DINO(CLS 或 patch):解码 560 缩略图 → ONNX 推理(同时拿 CLS+patch)
    /// 3. 若需要 CV:解码原图 → CvGridExtractor 提取
    /// 4. 单事务写入 photos / photo_features / photo_patches
    /// </summary>
    private async Task ProcessGroupAsync(FingerprintGroup group)
    {
        try
        {
            if (string.IsNullOrEmpty(group.Fingerprint))
            {
                ReportProgress();
                return;
            }

            var missing = await EvaluateMissingPartsAsync(group.Fingerprint).ConfigureAwait(false);
            if (!missing.AnyMissing)
            {
                ReportProgress();
                return;
            }

            var representative = group.Representative;
            byte[]? clsBlob = null;
            byte[]? patchBlob = null;
            byte[]? cvBlob = null;
            string? cvSpec = null;
            int cvWidth = 0;
            int cvHeight = 0;

            Bitmap? thumbnail = null;
            try
            {
                bool needDino = missing.NeedCls || missing.NeedPatches;
                if (needDino)
                {
                    thumbnail = await ThumbnailService.GetThumbnailAsync(representative.File, FeaturingShortSide).ConfigureAwait(false);
                    if (thumbnail == null)
                    {
                        // 缩略图都拿不到,直接放弃整组(CV 大图通常更难解码)
                        ReportProgress();
                        return;
                    }

                    await _inferSemaphore.WaitAsync().ConfigureAwait(false);
                    try
                    {
                        var (cls, patches) = await DinoFeatureExtractor.ExtractDualAsync(
                            thumbnail, includePatches: missing.NeedPatches).ConfigureAwait(false);

                        if (missing.NeedCls) clsBlob = EncodeFloatArray(cls);
                        if (missing.NeedPatches && patches != null) patchBlob = EncodeFloatArray(patches);

                        if (missing.NeedCls)
                            DinoFeatureCache.PutMemoryCache(group.Fingerprint, cls);
                    }
                    finally
                    {
                        _inferSemaphore.Release();
                    }
                }

                if (missing.NeedCv)
                {
                    Bitmap? cvBitmap = null;
                    try
                    {
                        cvBitmap = await BitmapLoader.GetBitmapAsync(representative.File).ConfigureAwait(false);
                        if (cvBitmap != null)
                        {
                            var cvResult = await CvGridExtractor.ExtractAsync(cvBitmap).ConfigureAwait(false);
                            cvBlob = cvResult.Encode();
                            cvSpec = CvGridResult.CurrentVersion;
                            cvWidth = cvBitmap.PixelSize.Width;
                            cvHeight = cvBitmap.PixelSize.Height;
                        }
                    }
                    finally
                    {
                        // CV 大图来自 BitmapLoader 的 LRU 缓存,由 ImageView 共用,这里不 Dispose
                    }
                }

                // 顺手带 EXIF + rating 快照(Plan-2-4):代表文件 ExifData 在 GroupByFingerprintAsync 已加载
                var exifSnapshot = BuildExifSnapshot(representative);

                // 单事务写入三表
                await PhotoDatabase.WriteIndexedAsync(
                    group.Input, group.Fingerprint, DinoModelResources.ModelId,
                    clsBlob, patchBlob, cvBlob, cvSpec, cvWidth, cvHeight, exifSnapshot).ConfigureAwait(false);

                ReportProgress();
            }
            finally
            {
                thumbnail?.Dispose();
            }
        }
        catch (FileNotFoundException ex)
        {
            Interlocked.CompareExchange(ref _lastError, ex.Message, null);
            Interlocked.Increment(ref _failed);
            ReportProgress();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[FolderFeatureIndexer] failed for {group.Representative.Name}: {ex.Message}");
            Interlocked.CompareExchange(ref _lastError, ex.Message, null);
            Interlocked.Increment(ref _failed);
            ReportProgress();
        }
    }

    private void ReportProgress()
    {
        int done = Interlocked.Increment(ref _completed);
        ProgressChanged?.Invoke(new IndexProgress(done, _total, _failed));
    }

    private static byte[] EncodeFloatArray(float[] data)
    {
        var bytes = new byte[data.Length * sizeof(float)];
        for (int i = 0; i < data.Length; i++)
            BinaryPrimitives.WriteSingleLittleEndian(
                bytes.AsSpan(i * sizeof(float), sizeof(float)), data[i]);
        return bytes;
    }

    /// <summary>
    /// 从代表文件的 EXIF 抽出训练用拍摄参数快照(实际焦距/光圈/快门/CMOS 倍率)+ 当前 rating。
    /// `crop_factor` = EquivFocalLength / FocalLength,两者任一缺失则 NULL(训练脚本按 1.0 兜底)。
    /// rating 0(未评)统一存 NULL — 让训练查询直接 `WHERE rating IS NOT NULL` 排除未评样本。
    /// </summary>
    private static ExifSnapshot BuildExifSnapshot(ImageFile file)
    {
        var exif = file.ExifData;
        double? focal = ToDoubleOrNull(exif?.FocalLength);
        double? aperture = ToDoubleOrNull(exif?.Aperture);
        double? shutter = ToDoubleOrNull(exif?.ExposureTime);
        double? equivFocal = ToDoubleOrNull(exif?.EquivFocalLength);

        double? cropFactor = null;
        if (focal.HasValue && focal.Value > 0 && equivFocal.HasValue && equivFocal.Value > 0)
            cropFactor = equivFocal.Value / focal.Value;

        int rating = file.Rating;
        return new ExifSnapshot
        {
            FocalLength = focal,
            Aperture = aperture,
            ShutterSpeed = shutter,
            CropFactor = cropFactor,
            Rating = rating > 0 ? rating : (int?)null,
        };
    }

    private static double? ToDoubleOrNull(MetadataExtractor.Rational? r)
    {
        if (!r.HasValue) return null;
        var v = r.Value;
        if (v.Denominator == 0) return null;
        return (double)v.Numerator / v.Denominator;
    }
}

/// <summary>批量提取进度快照。</summary>
public readonly struct IndexProgress
{
    /// <summary>已处理张数(含跳过)。</summary>
    public int Completed { get; }

    /// <summary>总张数。</summary>
    public int Total { get; }

    /// <summary>失败张数(不含跳过)。</summary>
    public int Failed { get; }

    public IndexProgress(int completed, int total, int failed)
    {
        Completed = completed;
        Total = total;
        Failed = failed;
    }
}
