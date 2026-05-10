using System;
using System.Buffers.Binary;

namespace PhotoViewer.Core.AI;

/// <summary>
/// CV 网格一期提取结果：16×16 格 × 5 标量 × 3 层金字塔。
/// 数据布局为 [pyramid][scalar][grid_y][grid_x]，便于"每尺度每标量一张 16×16 热力图"的可视化与聚合。
/// </summary>
public sealed class CvGridResult
{
    /// <summary>当前版本的 schema 标识。升级标量集合或金字塔层数时改此值，避免旧 cache 误用。</summary>
    public const string CurrentVersion = "cv_grid_v0_5scalar";

    /// <summary>固定 16×16 网格，与 DINO patch 下采样目标对齐（详见 §A3.2）。</summary>
    public const int GridSize = 16;

    /// <summary>金字塔层数（level 0 = 原图，level 1 = 1/2，level 2 = 1/4）。</summary>
    public const int PyramidLevels = 3;

    /// <summary>一期保留的 5 个标量；索引必须与 Data 布局一致，不可调换顺序。</summary>
    public static readonly string[] ScalarNames =
    {
        "laplacian_var",
        "sobel_mean",
        "grad_dir_entropy",
        "luma_mean",
        "luma_std",
    };

    /// <summary>标量数量。</summary>
    public static int ScalarCount => ScalarNames.Length;

    /// <summary>每层每标量的 16×16 平面长度。</summary>
    public static int PlaneLength => GridSize * GridSize;

    /// <summary>每层所有标量的元素总数。</summary>
    public static int LevelStride => ScalarCount * PlaneLength;

    /// <summary>整个数据块的元素总数 = PyramidLevels × ScalarCount × 16×16 = 3840。</summary>
    public static int DataLength => PyramidLevels * LevelStride;

    /// <summary>Schema 版本；与 <see cref="CurrentVersion"/> 不一致时视为 cache miss。</summary>
    public string Version { get; init; } = CurrentVersion;

    /// <summary>扁平化数据：Data[p * LevelStride + s * PlaneLength + y * GridSize + x]。</summary>
    public float[] Data { get; init; } = Array.Empty<float>();

    /// <summary>按 (pyramid, scalar, y, x) 读取单元值。</summary>
    public float GetCell(int pyramid, int scalar, int y, int x)
    {
        return Data[pyramid * LevelStride + scalar * PlaneLength + y * GridSize + x];
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
