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
/// 全文件夹批量特征提取调度器。
/// 对 <see cref="ImageFile"/> 列表逐张计算 DINOv3 [CLS] 特征向量并写入数据库。
/// 桌面端半核并行解码 + 单线程 ONNX 推理；移动端维持单线程。
/// 单张失败跳过，不中断整批。本期不支持取消（切文件夹后任务继续后台跑完）。
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

    /// <summary>进度事件：每张完成（成功或跳过）后触发。</summary>
    public event Action<IndexProgress>? ProgressChanged;

    /// <summary>
    /// 对 <paramref name="files"/> 中尚未入库的照片批量提取特征向量。
    /// 已有当前模型特征的照片直接跳过，不重复计算。
    /// </summary>
    /// <param name="files">待处理文件列表（按 AllFiles 顺序）。</param>
    public async Task RunAsync(IReadOnlyList<ImageFile> files)
    {
        if (IsRunning) return;
        IsRunning = true;

        _completed = 0;
        _failed = 0;
        _total = files.Count;

        // 桌面端半核并行解码；移动端单线程
        int decodeConcurrency = OperatingSystem.IsAndroid() || OperatingSystem.IsIOS()
            ? 1
            : Math.Max(1, Environment.ProcessorCount / 2);

        var decodeSemaphore = new SemaphoreSlim(decodeConcurrency, decodeConcurrency);
        var tasks = new List<Task>(files.Count);

        foreach (var file in files)
        {
            var f = file;
            tasks.Add(Task.Run(async () =>
            {
                await decodeSemaphore.WaitAsync().ConfigureAwait(false);
                try
                {
                    await ProcessOneAsync(f).ConfigureAwait(false);
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
    /// 处理单张照片：计算指纹 → 检查是否已入库 → 解码缩略图 → 推理 → 写库。
    /// </summary>
    private async Task ProcessOneAsync(ImageFile file)
    {
        try
        {
            // 确保 EXIF 已加载（指纹依赖 DateTimeOriginal）
            var exif = await file.LoadExifDataAsync().ConfigureAwait(false);
            if (!file.ModifiedDate.HasValue)
                await file.LoadBasicPropertiesAsync().ConfigureAwait(false);

            var fallback = file.ModifiedDate?.UtcDateTime;
            var input = PhotoFingerprint.BuildInput(file.Name, exif, fallback);
            if (!input.CaptureTime.HasValue)
            {
                // 无法计算指纹，跳过（不计入分母，但仍推进进度）
                ReportProgress(skipped: true);
                return;
            }

            var fingerprint = PhotoFingerprint.Compute(input);

            // 已入库则跳过
            if (await IsAlreadyIndexedAsync(fingerprint).ConfigureAwait(false))
            {
                ReportProgress(skipped: false);
                return;
            }

            // 解码缩略图
            Bitmap? bitmap = null;
            try
            {
                bitmap = await ThumbnailService.GetThumbnailAsync(file.File, FeaturingShortSide).ConfigureAwait(false);
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
                    input, fingerprint,
                    EncodeVector(vector),
                    DinoModelResources.ModelId).ConfigureAwait(false);

                // 同步到进程内存缓存（让后续 TryReadAsync 直接命中）
                DinoFeatureCache.PutMemoryCache(fingerprint, vector);

                ReportProgress(skipped: false);
            }
            finally
            {
                bitmap?.Dispose();
            }
        }
        catch (FileNotFoundException)
        {
            // 模型文件缺失，整批都会失败，但仍逐张推进进度
            Interlocked.Increment(ref _failed);
            ReportProgress(skipped: false);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[FolderFeatureIndexer] failed for {file.Name}: {ex.Message}");
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
