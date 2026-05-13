using System;
using System.Buffers.Binary;

namespace PhotoViewer.Core.AI;

/// <summary>
/// CV 网格 v2 提取结果：16×16 格 × 6 标量 × 1 层（无金字塔）。
/// 每格采用"中心 128×128 定尺寸采样 + Marziliano 边宽"量化，直接输出像素单位。
/// 数据布局为 [scalar][grid_y][grid_x]。
/// </summary>
public sealed class CvGridResult
{
    /// <summary>当前版本的 schema 标识。升级标量集合或粒度时改此值，避免旧 cache 误用。</summary>
    public const string CurrentVersion = "cv_grid_v3_32grid";

    /// <summary>固定 32×32 网格，与 DINO patch 网格对齐（518/16=32）。</summary>
    public const int GridSize = 32;

    /// <summary>每格中心采样块边长（像素）的目标值，实际由短边/Grid 自适应到 [64, 192]。</summary>
    public const int TargetBlockSize = 128;

    /// <summary>采样块最小边长（像素）。低于此判 NaN。</summary>
    public const int MinBlockSize = 64;

    /// <summary>采样块最大边长（像素）。过大时算力膨胀，没有边际收益。</summary>
    public const int MaxBlockSize = 192;

    /// <summary>v2 保留的 6 个标量；索引必须与 Data 布局一致，不可调换顺序。</summary>
    public static readonly string[] ScalarNames =
    {
        "edge_count",         // 0：块内强梯度像素数（NaN 判据）
        "edge_width_p20",     // 1：最锐 20% 边的平均跨像素宽度（px）
        "edge_width_median",  // 2：所有强边宽度中位数（px）
        "shake_spread",       // 3：方向 8-bin 边宽 max−min（px）
        "shake_direction",    // 4：边宽最大桶的中心方向（rad，[0,π)）
        "luma_mean",          // 5：采样块亮度均值（[0,255]，调试用）
    };

    /// <summary>标量数量。</summary>
    public static int ScalarCount => ScalarNames.Length;

    /// <summary>每标量的 16×16 平面长度。</summary>
    public static int PlaneLength => GridSize * GridSize;

    /// <summary>全部标量的元素总数 = 6 × 256 = 1536。</summary>
    public static int DataLength => ScalarCount * PlaneLength;

    /// <summary>NaN 哨兵值（float.NaN）。用于标识"边数不足，无有效读数"的格子；热力图需画灰。</summary>
    public static float Nan => float.NaN;

    /// <summary>Schema 版本；与 <see cref="CurrentVersion"/> 不一致时视为 cache miss。</summary>
    public string Version { get; init; } = CurrentVersion;

    /// <summary>扁平化数据：Data[s * PlaneLength + y * GridSize + x]。</summary>
    public float[] Data { get; init; } = Array.Empty<float>();

    /// <summary>按 (scalar, y, x) 读取单元值。</summary>
    public float GetCell(int scalar, int y, int x)
    {
        return Data[scalar * PlaneLength + y * GridSize + x];
    }

    /// <summary>按小端 float32 连续编码为 BLOB（无标量名 / 版本头，由 cv_grid_spec 列单独记录）。</summary>
    public byte[] Encode()
    {
        var blob = new byte[Data.Length * sizeof(float)];
        var span = blob.AsSpan();
        for (int i = 0; i < Data.Length; i++)
        {
            BinaryPrimitives.WriteSingleLittleEndian(span.Slice(i * sizeof(float), sizeof(float)), Data[i]);
        }
        return blob;
    }

    /// <summary>按小端 float32 解码；长度不符返回 null，调用方视为 cache miss。</summary>
    public static CvGridResult? Decode(byte[] blob, string version)
    {
        if (blob.Length != DataLength * sizeof(float)) return null;
        var data = new float[DataLength];
        for (int i = 0; i < DataLength; i++)
        {
            data[i] = BinaryPrimitives.ReadSingleLittleEndian(blob.AsSpan(i * sizeof(float), sizeof(float)));
        }
        return new CvGridResult { Version = version, Data = data };
    }
}
