using System;
using System.Buffers.Binary;
using System.Threading;
using System.Threading.Tasks;
using PhotoViewer.Core.Database;
using PhotoViewer.Core.Image;

namespace PhotoViewer.Core.AI;

/// <summary>
/// 分析栏只读门面:从 <see cref="ImageFile"/> 出发计算指纹,并行读 photo_patches + photos.cv_grid,
/// 返回反序列化后的 patch token 数组与 <see cref="CvGridResult"/>(含 CV 实际解码尺寸)。
///
/// 抽自 <see cref="PhotoViewer.ViewModels.Tools.DinoDebugViewModel"/>.TryReadCachedAsync,差别在于:
/// 仅做读库,不触发任何解码 / ONNX 推理 / CV 重算 — 缺失即为缺失,由调用方决定如何呈现(显示"未提取"占位)。
///
/// 切分两步:<see cref="ComputeFingerprintAsync"/> 同步算指纹给 <see cref="AnalysisResultCache"/> 命中检查用,
/// <see cref="ReadByFingerprintAsync"/> 给 cache miss 时的实际读库路径用。
/// </summary>
public static class AnalysisDataReader
{
    /// <summary>
    /// 已成功读到的部分缓存。任一字段可能为 null,代表对应数据不在库或 schema 不匹配。
    /// </summary>
    public sealed class Result
    {
        /// <summary>DINO patch token(1024 × 384 floats),null 表示未入库。</summary>
        public float[]? Patches { get; init; }

        /// <summary>CV v5 七标量结果,null 表示未入库或版本不匹配。</summary>
        public CvGridResult? Cv { get; init; }

        /// <summary>CV 实际解码用的原图宽度(像素),Cv 为 null 时为 0。</summary>
        public int CvImageWidth { get; init; }

        /// <summary>CV 实际解码用的原图高度(像素),Cv 为 null 时为 0。</summary>
        public int CvImageHeight { get; init; }

        /// <summary>所有数据都缺失。</summary>
        public bool IsEmpty => Patches == null && Cv == null;
    }

    /// <summary>
    /// 计算指纹:依赖 EXIF + 修改时间;无法取到稳定时间戳时返回 null。
    /// 与 <see cref="DinoFeatureCache"/> 内部使用的指纹算法一致(同次曝光的 RAW/HEIF/JPG 共享指纹)。
    /// </summary>
    public static async Task<string?> ComputeFingerprintAsync(ImageFile file, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var exif = await file.LoadExifDataAsync().ConfigureAwait(false);
        if (!file.ModifiedDate.HasValue)
            await file.LoadBasicPropertiesAsync().ConfigureAwait(false);

        var input = PhotoFingerprint.BuildInput(file.Name, exif, file.ModifiedDate?.UtcDateTime);
        if (!input.CaptureTime.HasValue) return null;
        return PhotoFingerprint.Compute(input);
    }

    /// <summary>
    /// 给定指纹,并行读 photo_patches + photos.cv_grid 并反序列化。
    /// </summary>
    public static async Task<Result> ReadByFingerprintAsync(string fingerprint, CancellationToken ct)
    {
        try
        {
            ct.ThrowIfCancellationRequested();

            var patchTask = PhotoDatabase.ReadPatchesAsync(fingerprint, DinoModelResources.ModelId);
            var cvTask = PhotoDatabase.ReadCvGridAsync(fingerprint);
            await Task.WhenAll(patchTask, cvTask).ConfigureAwait(false);

            float[]? patches = null;
            if (patchTask.Result is byte[] pBlob)
            {
                int total = DinoModelResources.PatchTokenCount * DinoModelResources.FeatureDim;
                if (pBlob.Length == total * sizeof(float))
                {
                    patches = new float[total];
                    for (int i = 0; i < total; i++)
                        patches[i] = BinaryPrimitives.ReadSingleLittleEndian(pBlob.AsSpan(i * sizeof(float), sizeof(float)));
                }
            }

            CvGridResult? cv = null;
            int cvW = 0, cvH = 0;
            if (cvTask.Result is { Blob: var cvBlob, Spec: var cvSpec, ImageWidth: var w, ImageHeight: var h }
                && cvSpec == CvGridResult.CurrentVersion)
            {
                cv = CvGridResult.Decode(cvBlob, cvSpec);
                if (cv != null)
                {
                    cvW = w;
                    cvH = h;
                }
            }

            return new Result { Patches = patches, Cv = cv, CvImageWidth = cvW, CvImageHeight = cvH };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[AnalysisDataReader] read failed for {fingerprint}: {ex.Message}");
            return new Result();
        }
    }

    /// <summary>
    /// 两步合一:计算指纹 → 读库。指纹失败时返回空 <see cref="Result"/>。供仅需读一次的旧路径使用。
    /// </summary>
    public static async Task<Result> TryReadAsync(ImageFile file, CancellationToken ct)
    {
        var fp = await ComputeFingerprintAsync(file, ct).ConfigureAwait(false);
        if (fp == null) return new Result();
        return await ReadByFingerprintAsync(fp, ct).ConfigureAwait(false);
    }
}
