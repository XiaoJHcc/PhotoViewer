using System;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using PhotoViewer.Core.Database;
using PhotoViewer.Core.Image;

namespace PhotoViewer.Core.AI;

/// <summary>
/// DINOv3 特征向量缓存门面：按 <see cref="PhotoFingerprint"/> 命中 <see cref="PhotoDatabase"/>，
/// 缓存未命中时解码缩略图 → 走 <see cref="DinoFeatureExtractor"/> 推理 → 写回数据库。
/// 同次曝光的 RAW/JPG/HEIF 共享同一指纹，只会算一次。
/// </summary>
public static class DinoFeatureCache
{
    /// <summary>喂给 DINO 的图片短边像素（大于模型输入边长即可，留余量给中心裁剪/缩放）。</summary>
    private const int FeaturingShortSide = 560;

    /// <summary>
    /// 进程内指纹 → 向量缓存，避免同一会话内重复查数据库。
    /// 值为长度 <see cref="DinoModelResources.FeatureDim"/> 的只读向量引用。
    /// </summary>
    private static readonly ConcurrentDictionary<string, float[]> _memoryCache = new();

    /// <summary>
    /// 按指纹互斥的计算闸门：同一指纹并发进来时，只跑一次推理，其余等结果。
    /// </summary>
    private static readonly ConcurrentDictionary<string, Lazy<Task<float[]?>>> _inflight = new();

    /// <summary>
    /// 取得 <paramref name="file"/> 的 [CLS] 特征向量（L2 归一化）。
    /// 流程：EXIF → 指纹 → 进程缓存 → 数据库缓存 → 推理并写回。
    /// 任一阶段失败（缺 EXIF、缩略图失败、模型缺失）返回 null。
    /// </summary>
    public static async Task<float[]?> GetOrComputeAsync(ImageFile file, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(file);

        var fingerprint = await ComputeFingerprintAsync(file, ct).ConfigureAwait(false);
        if (fingerprint == null) return null;

        if (_memoryCache.TryGetValue(fingerprint, out var cached)) return cached;

        // 用 Lazy 实现同一指纹的"只跑一次"语义；共用同一 Task 的 awaiter 自然并发等待。
        var lazy = _inflight.GetOrAdd(fingerprint, fp => new Lazy<Task<float[]?>>(
            () => ComputeAndStoreAsync(fp, file, ct),
            LazyThreadSafetyMode.ExecutionAndPublication));

        try
        {
            return await lazy.Value.ConfigureAwait(false);
        }
        finally
        {
            _inflight.TryRemove(fingerprint, out _);
        }
    }

    /// <summary>
    /// 将外部计算好的向量写入进程内存缓存（不写数据库）。
    /// 供 <see cref="FolderFeatureIndexer"/> 在批量写库后同步内存缓存，避免下次查询再走数据库。
    /// </summary>
    public static void PutMemoryCache(string fingerprint, float[] vector)
    {
        _memoryCache[fingerprint] = vector;
    }

    /// <summary>
    /// 不触发推理，仅从进程缓存或数据库里读。用于"只用已有特征做聚类"的快速路径。
    /// </summary>
    public static async Task<float[]?> TryReadAsync(ImageFile file, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(file);

        var fingerprint = await ComputeFingerprintAsync(file, ct).ConfigureAwait(false);
        if (fingerprint == null) return null;
        if (_memoryCache.TryGetValue(fingerprint, out var cached)) return cached;

        var vector = await ReadFromDatabaseAsync(fingerprint).ConfigureAwait(false);
        if (vector != null) _memoryCache[fingerprint] = vector;
        return vector;
    }

    /// <summary>
    /// 计算指纹（依赖 EXIF；为此确保 EXIF 已加载）。无法取到稳定时间戳时返回 null。
    /// </summary>
    private static async Task<string?> ComputeFingerprintAsync(ImageFile file, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        // ImageFile.LoadExifDataAsync 内部做了"已加载/加载中"去抖，可安全重复调用。
        var exif = await file.LoadExifDataAsync().ConfigureAwait(false);
        if (!file.ModifiedDate.HasValue)
        {
            // 基础属性可能还没加载：补一次
            await file.LoadBasicPropertiesAsync().ConfigureAwait(false);
        }

        var fallback = file.ModifiedDate?.UtcDateTime;
        var input = PhotoFingerprint.BuildInput(file.Name, exif, fallback);
        if (!input.CaptureTime.HasValue) return null;

        return PhotoFingerprint.Compute(input);
    }

    /// <summary>
    /// 核心计算路径：先查数据库，miss 则解码 + 推理 + 写回。
    /// </summary>
    private static async Task<float[]?> ComputeAndStoreAsync(string fingerprint, ImageFile file, CancellationToken ct)
    {
        var dbVector = await ReadFromDatabaseAsync(fingerprint).ConfigureAwait(false);
        if (dbVector != null)
        {
            _memoryCache[fingerprint] = dbVector;
            return dbVector;
        }

        Bitmap? bitmap = null;
        try
        {
            bitmap = await ThumbnailService.GetThumbnailAsync(file.File, FeaturingShortSide).ConfigureAwait(false);
            if (bitmap == null)
            {
                Console.WriteLine($"[DinoFeatureCache] no thumbnail for {file.Name}, skip");
                return null;
            }

            var vector = await DinoFeatureExtractor.ExtractAsync(bitmap, ct).ConfigureAwait(false);
            _memoryCache[fingerprint] = vector;

            // 写库不阻塞调用方（异步写入出错只记日志，不影响当次聚类）。
            _ = Task.Run(async () =>
            {
                try
                {
                    var input = PhotoFingerprint.BuildInput(file.Name, file.ExifData, file.ModifiedDate?.UtcDateTime);
                    await PhotoDatabase.WriteFeatureVectorAsync(input, fingerprint, EncodeVector(vector), DinoModelResources.ModelId).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[DinoFeatureCache] persist failed for {file.Name}: {ex.Message}");
                }
            });

            return vector;
        }
        catch (FileNotFoundException fnf)
        {
            Console.WriteLine($"[DinoFeatureCache] model missing: {fnf.Message}");
            return null;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[DinoFeatureCache] extract failed for {file.Name}: {ex.Message}");
            return null;
        }
        finally
        {
            bitmap?.Dispose();
        }
    }

    /// <summary>
    /// 从 <see cref="PhotoDatabase"/> 读取并解码向量；模型标识不匹配时视为 miss，避免用老模型向量做相似度。
    /// </summary>
    private static async Task<float[]?> ReadFromDatabaseAsync(string fingerprint)
    {
        try
        {
            var record = await PhotoDatabase.GetAsync(fingerprint).ConfigureAwait(false);
            if (record?.FeatureVector == null) return null;
            if (record.FeatureModel != DinoModelResources.ModelId) return null;
            return DecodeVector(record.FeatureVector);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[DinoFeatureCache] db read failed ({fingerprint}): {ex.Message}");
            return null;
        }
    }

    /// <summary>按小端 float32 连续编码为 BLOB。</summary>
    private static byte[] EncodeVector(float[] vector)
    {
        var bytes = new byte[vector.Length * sizeof(float)];
        for (int i = 0; i < vector.Length; i++)
        {
            BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(i * sizeof(float), sizeof(float)), vector[i]);
        }
        return bytes;
    }

    /// <summary>按小端 float32 解码；长度非整数倍或与期望维度不符时返回 null。</summary>
    private static float[]? DecodeVector(byte[] blob)
    {
        if (blob.Length == 0 || blob.Length % sizeof(float) != 0) return null;
        int len = blob.Length / sizeof(float);
        if (len != DinoModelResources.FeatureDim) return null;

        var vector = new float[len];
        for (int i = 0; i < len; i++)
        {
            vector[i] = BinaryPrimitives.ReadSingleLittleEndian(blob.AsSpan(i * sizeof(float), sizeof(float)));
        }
        return vector;
    }
}
