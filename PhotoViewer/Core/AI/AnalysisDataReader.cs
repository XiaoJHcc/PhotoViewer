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
    /// 只读路径:计算指纹 → 并行读两表 → 反序列化。任一异常或指纹计算失败返回空 <see cref="Result"/>。
    /// </summary>
    /// <param name="file">目标图片;需要其 Name + ExifData(会按需触发懒加载,但不会解码图像)。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>读到的部分缓存;调用方按字段是否为 null 决定占位与否。</returns>
    public static async Task<Result> TryReadAsync(ImageFile file, CancellationToken ct)
    {
        try
        {
            ct.ThrowIfCancellationRequested();
            var exif = await file.LoadExifDataAsync().ConfigureAwait(false);
            if (!file.ModifiedDate.HasValue)
                await file.LoadBasicPropertiesAsync().ConfigureAwait(false);

            var input = PhotoFingerprint.BuildInput(file.Name, exif, file.ModifiedDate?.UtcDateTime);
            if (!input.CaptureTime.HasValue) return new Result();

            var fingerprint = PhotoFingerprint.Compute(input);
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
            Console.WriteLine($"[AnalysisDataReader] read failed for {file.Name}: {ex.Message}");
            return new Result();
        }
    }
}
