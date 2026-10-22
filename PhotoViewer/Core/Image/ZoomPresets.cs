using System;
using System.Collections.Generic;
using System.Linq;

namespace PhotoViewer.Core.Image;

/// <summary>
/// 预设缩放档位查询（IMAGE-04）：从设置中的档位里取当前值之上/之下最近的一档。
/// 档位值与当前缩放同为物理倍率（设置页的百分比除以 100），比较容差 0.001。
/// </summary>
public static class ZoomPresets
{
    /// <summary>比较当前倍率与档位时的容差。</summary>
    public const double Tolerance = 0.001;

    /// <summary>
    /// 查询当前倍率之上/之下最近的一档；没有更远档时返回 null，调用方不动作。
    /// </summary>
    /// <param name="presets">预设档位（物理倍率，允许乱序）。</param>
    /// <param name="currentScale">当前物理倍率。</param>
    /// <param name="direction">正数放大、负数缩小；0 返回 null。</param>
    /// <returns>目标档位；没有符合方向的档位时返回 null。</returns>
    public static double? Next(IEnumerable<double> presets, double currentScale, int direction)
    {
        if (direction == 0) return null;

        var ordered = presets.OrderBy(value => value).ToList();
        if (direction > 0)
        {
            foreach (var value in ordered)
            {
                if (value > currentScale + Tolerance) return value;
            }
        }
        else
        {
            for (int i = ordered.Count - 1; i >= 0; i--)
            {
                if (ordered[i] < currentScale - Tolerance) return ordered[i];
            }
        }

        return null;
    }
}
