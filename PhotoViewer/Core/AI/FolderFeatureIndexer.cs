using System;
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

    /// <summary>代表文件:实际喂给 DINO 推理的那一张。</summary>
    public ImageFile Representative => Files[0];
}

/// <summary>
/// 全文件夹批量特征提取调度器。
/// 对 <see cref="ImageFile"/> 列表按指纹聚合后逐组计算 DINOv3 [CLS] 特征向量并写入数据库——
/// 同次曝光的 RAW+HEIF/JPG 共享同一指纹,只跑一次推理。
/// 桌面端半核并行解码 + 单线程 ONNX 推理;移动端维持单线程。
/// 单组失败跳过,不中断整批。本期不支持取消(切文件夹后任务继续后台跑完)。
/// </summary>
public sealed class FolderFeatureIndexer
{
    /// <summary>喂给 DINO 的图片短边像素，与 <see cref="DinoFeatureCache"/> 保持一致。</summary>
    private const int FeaturingShortSide = 560;

    private static readonly SemaphoreSlim _inferSemaphore = new(1, 1);

    private int _completed;
    private int _failed;
    private int _total;

    /// <summary>是否正在运行。</summary>
    public bool IsRunning { get; private set; }

    /// <summary>进度事件：每组完成（成功或跳过）后触发。</summary>
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
    /// 对 <paramref name="files"/> 中尚未入库的照片批量提取特征向量。
    /// 同指纹的 RAW/HEIF/JPG 只算一次,进度按"指纹组"推进而非按文件数。
    /// </summary>
    /// <param name="files">待处理文件列表（按 AllFiles 顺序）。</param>
    public async Task RunAsync(IReadOnlyList<ImageFile> files)
    {
        if (IsRunning) return;
        IsRunning = true;

        var groups = await GroupByFingerprintAsync(files).ConfigureAwait(false);

        _completed = 0;
        _failed = 0;
        _total = groups.Count;

        // 桌面端半核并行解码；移动端单线程
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
    /// 处理一个指纹组:无指纹组直接跳过;已入库则跳过;否则用代表文件解码 + 推理 + 写库,
    /// 同指纹下其他文件后续通过 <see cref="DinoFeatureCache"/> 按指纹命中。
    /// </summary>
    private async Task ProcessGroupAsync(FingerprintGroup group)
    {
        try
        {
            if (string.IsNullOrEmpty(group.Fingerprint))
            {
                ReportProgress(skipped: true);
                return;
            }

            // 已入库则跳过
            if (await IsAlreadyIndexedAsync(group.Fingerprint).ConfigureAwait(false))
            {
                ReportProgress(skipped: false);
                return;
            }

            var representative = group.Representative;

            // 解码缩略图(代表文件,通常是 HEIF/JPG,RAW 仅在没有伴侣时才会被选中)
            Bitmap? bitmap = null;
            try
            {
                bitmap = await ThumbnailService.GetThumbnailAsync(representative.File, FeaturingShortSide).ConfigureAwait(false);
                if (bitmap == null)
                {
                    ReportProgress(skipped: true);
                    return;
                }

                // 单线程 ONNX 推理
                float[] vector;
                await _inferSemaphore.WaitAsync().ConfigureAwait(false);
                try
                {
                    vector = await DinoFeatureExtractor.ExtractAsync(bitmap).ConfigureAwait(false);
                }
                finally
                {
                    _inferSemaphore.Release();
                }

                // 写库
                await PhotoDatabase.WriteFeatureVectorAsync(
                    group.Input, group.Fingerprint,
                    EncodeVector(vector),
                    DinoModelResources.ModelId).ConfigureAwait(false);

                // 同步到进程内存缓存(同指纹下所有文件后续 TryReadAsync 都能命中同一个值)
                DinoFeatureCache.PutMemoryCache(group.Fingerprint, vector);

                ReportProgress(skipped: false);
            }
            finally
            {
                bitmap?.Dispose();
            }
        }
        catch (FileNotFoundException)
        {
            // 模型文件缺失,整批都会失败,但仍逐组推进进度
            Interlocked.Increment(ref _failed);
            ReportProgress(skipped: false);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[FolderFeatureIndexer] failed for {group.Representative.Name}: {ex.Message}");
            Interlocked.Increment(ref _failed);
            ReportProgress(skipped: false);
        }
    }

    /// <summary>
    /// 检查指纹是否已在数据库中存有当前模型的特征向量。
    /// </summary>
    private static async Task<bool> IsAlreadyIndexedAsync(string fingerprint)
    {
        try
        {
            var record = await PhotoDatabase.GetAsync(fingerprint).ConfigureAwait(false);
            return record?.FeatureVector != null
                && record.FeatureModel == DinoModelResources.ModelId;
        }
        catch
        {
            return false;
        }
    }

    private void ReportProgress(bool skipped)
    {
        int done = Interlocked.Increment(ref _completed);
        ProgressChanged?.Invoke(new IndexProgress(done, _total, _failed));
    }

    private static byte[] EncodeVector(float[] vector)
    {
        var bytes = new byte[vector.Length * sizeof(float)];
        for (int i = 0; i < vector.Length; i++)
            System.Buffers.Binary.BinaryPrimitives.WriteSingleLittleEndian(
                bytes.AsSpan(i * sizeof(float), sizeof(float)), vector[i]);
        return bytes;
    }
}

/// <summary>批量提取进度快照。</summary>
public readonly struct IndexProgress
{
    /// <summary>已处理张数（含跳过）。</summary>
    public int Completed { get; }

    /// <summary>总张数。</summary>
    public int Total { get; }

    /// <summary>失败张数（不含跳过）。</summary>
    public int Failed { get; }

    public IndexProgress(int completed, int total, int failed)
    {
        Completed = completed;
        Total = total;
        Failed = failed;
    }
}
