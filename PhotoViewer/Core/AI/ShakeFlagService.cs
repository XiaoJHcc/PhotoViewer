using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using PhotoViewer.Core.Database;
using PhotoViewer.Core.Image;

namespace PhotoViewer.Core.AI;

/// <summary>
/// 抖动判定服务:读 photos.cv_grid + cv_image_width/height,跑
/// <see cref="CvHeatmap.BuildShakeField"/> + <see cref="CvHeatmap.FitRigidMotion"/> + <see cref="ShakeClassifier.Classify"/>,
/// 把结果回填到 <see cref="ImageFile.IsShake"/>,驱动缩略图卡片的"抖"徽标。
///
/// 数据完全来自数据库 — 不再触发任何图像解码或 ONNX 推理。未入库的指纹组对应文件留 null(不挂徽标)。
/// 进程内按指纹缓存,避免同一文件夹反复扫描时重复读 DB。
/// </summary>
public static class ShakeFlagService
{
    /// <summary>指纹 → 判定结果。指纹相同的 RAW/HEIF/JPG 共享同一判定。</summary>
    private static readonly ConcurrentDictionary<string, ShakeVerdict> _cache = new();

    /// <summary>
    /// 通知订阅者(FolderViewModel)对当前 AllFiles 重新评估。
    /// 库被清空后触发,让"抖"徽标在不重开文件夹的情况下立即消失。
    /// </summary>
    public static event Action? RecheckRequested;

    /// <summary>
    /// 批量评估文件列表:按指纹分组、查 DB、回填 <see cref="ImageFile.IsShake"/>。
    /// 主要触发点:文件夹加载完成、批量索引结束。多次调用安全(幂等)。
    /// </summary>
    public static async Task EvaluateAsync(IReadOnlyList<ImageFile> files, CancellationToken ct = default)
    {
        if (files == null || files.Count == 0) return;

        IReadOnlyList<FingerprintGroup> groups;
        try
        {
            groups = await FolderFeatureIndexer.GroupByFingerprintAsync(files).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ShakeFlagService] group failed: {ex.Message}");
            return;
        }

        foreach (var group in groups)
        {
            ct.ThrowIfCancellationRequested();

            if (string.IsNullOrEmpty(group.Fingerprint))
            {
                ApplyVerdict(group.Files, null);
                continue;
            }

            try
            {
                ShakeVerdict? verdict = null;
                if (_cache.TryGetValue(group.Fingerprint, out var cached))
                {
                    verdict = cached;
                }
                else
                {
                    var record = await PhotoDatabase.ReadCvGridAsync(group.Fingerprint).ConfigureAwait(false);
                    if (record is { } rec && rec.Spec == CvGridResult.CurrentVersion
                        && rec.ImageWidth > 0 && rec.ImageHeight > 0)
                    {
                        var cv = CvGridResult.Decode(rec.Blob, rec.Spec);
                        if (cv != null)
                        {
                            float diagonal = MathF.Sqrt(
                                (float)rec.ImageWidth * rec.ImageWidth
                                + (float)rec.ImageHeight * rec.ImageHeight);
                            var field = CvHeatmap.BuildShakeField(cv, diagonal);
                            var rigid = CvHeatmap.FitRigidMotion(field);
                            verdict = ShakeClassifier.Classify(rigid, diagonal);
                            _cache[group.Fingerprint] = verdict.Value;
                        }
                    }
                }

                ApplyVerdict(group.Files, verdict);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ShakeFlagService] evaluate failed for {group.Representative.Name}: {ex.Message}");
                ApplyVerdict(group.Files, null);
            }
        }
    }

    /// <summary>清空进程缓存,并广播重新评估请求(订阅者通常是 FolderViewModel,会带上当前 AllFiles 调回 <see cref="EvaluateAsync"/>)。
    /// AI 设置页"清除特征数据库"按钮调用 — 库被清空后,所有 IsShake 会被重置为 null,"抖"徽标即时消失。</summary>
    public static void InvalidateAll()
    {
        _cache.Clear();
        RecheckRequested?.Invoke();
    }

    /// <summary>把同一指纹组内所有文件的 IsShake 设为同一值。null 表示"未判定"(无 cv_grid 或解码失败)。</summary>
    private static void ApplyVerdict(IReadOnlyList<ImageFile> files, ShakeVerdict? verdict)
    {
        bool? isShake = verdict.HasValue ? ShakeClassifier.IsShake(verdict.Value) : (bool?)null;
        foreach (var file in files)
        {
            file.IsShake = isShake;
        }
    }
}
