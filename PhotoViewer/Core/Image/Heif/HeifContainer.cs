using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace PhotoViewer.Core.Image.Heif;

/// <summary>HEIF 图像项的类型。</summary>
public enum HeifItemKind
{
    /// <summary>单个 HEVC 图像项(<c>hvc1</c>)。</summary>
    CodedImage,

    /// <summary>派生网格项(<c>grid</c>),由多块 <c>hvc1</c> 瓦片拼成。</summary>
    Grid,
}

/// <summary>变换种类。</summary>
public enum HeifTransformKind
{
    /// <summary><c>irot</c>:显示前按 90° 的整数倍逆时针旋转。</summary>
    Rotation,

    /// <summary><c>imir</c>:显示前按指定轴镜像。</summary>
    Mirror,
}

/// <summary>
/// 一项显示变换,按 <c>iprp/ipma</c> 关联顺序依次作用(先出现先作用)。
/// </summary>
public readonly struct HeifTransform
{
    /// <summary>构造一项变换。</summary>
    /// <param name="kind">变换种类。</param>
    /// <param name="value">旋转为 90° 逆时针的整数倍(0–3);镜像为 0=垂直轴(左右翻转)、1=水平轴(上下翻转)。</param>
    public HeifTransform(HeifTransformKind kind, int value)
    {
        Kind = kind;
        Value = value;
    }

    /// <summary>变换种类。</summary>
    public HeifTransformKind Kind { get; }

    /// <summary>旋转角度单位数或镜像轴。</summary>
    public int Value { get; }

    /// <inheritdoc/>
    public override string ToString()
        => Kind == HeifTransformKind.Rotation
            ? "irot " + (Value * 90).ToString(CultureInfo.InvariantCulture) + "°CCW"
            : "imir axis=" + Value.ToString(CultureInfo.InvariantCulture);
}

/// <summary>
/// <c>colr</c> 色彩描述:<c>nclx</c> 三元组(可选)与 ICC 配置文件(可选)。
/// </summary>
public sealed class HeifColorInfo
{
    /// <summary>是否存在 <c>nclx</c> 描述。</summary>
    public bool HasNclx { get; init; }

    /// <summary>nclx 原色代码(ISO/IEC 23001-8);1=BT.709,2=未指定,12=Display P3,9=BT.2020。</summary>
    public ushort Primaries { get; init; }

    /// <summary>nclx 传输特性代码;1/6/13/2 为 sRGB 曲线族,16=PQ,18=HLG。</summary>
    public ushort Transfer { get; init; }

    /// <summary>nclx 矩阵系数代码;1=BT.709,5/6=BT.601,9=BT.2020 非恒定亮度。</summary>
    public ushort Matrix { get; init; }

    /// <summary>nclx 全范围标志。</summary>
    public bool FullRange { get; init; }

    /// <summary>ICC 配置文件字节(<c>prof</c>/<c>rICC</c>);无则 null。</summary>
    public byte[]? IccProfile { get; init; }

    /// <summary>无任何色彩信息时的空描述。</summary>
    public static HeifColorInfo Empty { get; } = new();
}

/// <summary><c>pixi</c> 各通道位深。</summary>
public sealed class HeifPixelInfo
{
    /// <summary>各通道位深(按通道顺序)。</summary>
    public IReadOnlyList<int> BitsPerChannel { get; init; } = Array.Empty<int>();

    /// <summary>亮度位深;无 <c>pixi</c> 时回落到 <c>hvcC</c>。</summary>
    public int LumaBits { get; init; }

    /// <summary>色度位深;无 <c>pixi</c> 时回落到 <c>hvcC</c>。</summary>
    public int ChromaBits { get; init; }

    /// <summary>无像素信息时的空描述。</summary>
    public static HeifPixelInfo Empty { get; } = new() { LumaBits = 8, ChromaBits = 8 };
}

/// <summary>
/// 一块编码瓦片:在网格中的位置、编码尺寸与可直接送进解码器的 Annex-B 码流。
/// </summary>
public sealed class HeifTile
{
    /// <summary>构造瓦片描述。</summary>
    public HeifTile(int column, int row, int width, int height, byte[] annexB)
    {
        Column = column;
        Row = row;
        Width = width;
        Height = height;
        AnnexB = annexB;
    }

    /// <summary>列序号(从 0 起,行优先)。</summary>
    public int Column { get; }

    /// <summary>行序号(从 0 起)。</summary>
    public int Row { get; }

    /// <summary>瓦片左边界在输出画布中的 x(像素,= <see cref="Column"/> × <see cref="Width"/>)。</summary>
    public int PixelX => Column * Width;

    /// <summary>瓦片上边界在输出画布中的 y(像素,= <see cref="Row"/> × <see cref="Height"/>)。</summary>
    public int PixelY => Row * Height;

    /// <summary>瓦片编码宽(<c>ispe</c>),解码平面的有效宽度取此值。</summary>
    public int Width { get; }

    /// <summary>瓦片编码高(<c>ispe</c>)。</summary>
    public int Height { get; }

    /// <summary>
    /// Annex-B 码流:该瓦片 <c>hvcC</c> 的 VPS/SPS/PPS/SEI 依次带
    /// <c>00 00 00 01</c> 起始码,随后是项数据中按 <c>lengthSizeMinusOne</c>
    /// 长度前缀的 NAL(前缀同样改写为起始码)。
    /// </summary>
    public byte[] AnnexB { get; }
}

/// <summary>
/// 一项可独立解码的 HEVC 图像(单瓦片主图或缩略图)。
/// </summary>
public sealed class HeifCodedImage
{
    /// <summary>构造编码图像描述。</summary>
    public HeifCodedImage(int itemId, int width, int height, byte[] annexB)
    {
        ItemId = itemId;
        Width = width;
        Height = height;
        AnnexB = annexB;
    }

    /// <summary>HEIF 项 id。</summary>
    public int ItemId { get; }

    /// <summary>显示宽(应用变换之前)。</summary>
    public int Width { get; }

    /// <summary>显示高(应用变换之前)。</summary>
    public int Height { get; }

    /// <summary>Annex-B 码流,格式与 <see cref="HeifTile.AnnexB"/> 相同。</summary>
    public byte[] AnnexB { get; }

    /// <summary>该项自身的显示变换(按 ipma 顺序)。</summary>
    public IReadOnlyList<HeifTransform> Transforms { get; init; } = Array.Empty<HeifTransform>();

    /// <summary>该项的变换折算出的 EXIF 方向(1–8)。</summary>
    public int ExifOrientation { get; init; } = 1;

    /// <summary>该项生效的色彩描述。</summary>
    public HeifColorInfo Color { get; init; } = HeifColorInfo.Empty;

    /// <summary>该项生效的像素位深。</summary>
    public HeifPixelInfo PixelInfo { get; init; } = HeifPixelInfo.Empty;

    /// <summary>该项 <c>hvcC</c> 声明的色度采样格式(0=单色,1=4:2:0,2=4:2:2,3=4:4:4)。</summary>
    public int ChromaFormat { get; init; } = 1;

    /// <summary>短边(便于挑选缩略图)。</summary>
    public int ShortSide => Math.Min(Width, Height);
}

/// <summary>
/// HEIF 容器解析器:把 Sony/通用 HEIF 文件的元数据解成可直接喂给 HEVC 解码器的瓦片码流,
/// 以及显示所需的几何、变换与色彩信息。纯托管实现,不依赖原生库。
/// </summary>
/// <remarks>
/// 只覆盖本产品需要的子集:<c>ftyp</c>、<c>meta</c>(<c>hdlr</c>/<c>pitm</c>/<c>iinf</c>/<c>iloc</c>/
/// <c>idat</c>/<c>iref</c>/<c>iprp</c>),属性 <c>hvcC</c>/<c>ispe</c>/<c>colr</c>/<c>irot</c>/<c>imir</c>/
/// <c>clap</c>/<c>pixi</c>,主图类型 <c>hvc1</c> 与 <c>grid</c>。其余主图类型
/// (<c>av01</c>/<c>iovl</c>/<c>iden</c>/<c>unci</c> 等)、真正裁剪的 <c>clap</c>、尺寸不一的网格
/// 一律返回 null 并给出原因,由调用方回落 libheif。
/// </remarks>
public sealed class HeifContainer
{
    private const int MaxTilesPerGrid = 64;
    private const int MaxAnnexBBytes = 256 << 20;

    private readonly HeifColorInfo _color = HeifColorInfo.Empty;
    private readonly HeifPixelInfo _pixelInfo = HeifPixelInfo.Empty;

    private HeifContainer()
    {
    }

    /// <summary>输出画布宽(应用变换之前的像素宽)。</summary>
    public int Width { get; private init; }

    /// <summary>输出画布高(应用变换之前的像素高)。</summary>
    public int Height { get; private init; }

    /// <summary>网格列数(单瓦片主图为 1)。</summary>
    public int Columns { get; private init; }

    /// <summary>网格行数(单瓦片主图为 1)。</summary>
    public int Rows { get; private init; }

    /// <summary>单块瓦片的编码宽(<c>ispe</c>);单瓦片主图等于 <see cref="Width"/>。</summary>
    public int TileWidth { get; private init; }

    /// <summary>单块瓦片的编码高(<c>ispe</c>);单瓦片主图等于 <see cref="Height"/>。</summary>
    public int TileHeight { get; private init; }

    /// <summary>主图种类。</summary>
    public HeifItemKind Kind { get; private init; }

    /// <summary>按行优先 <c>dimg</c> 引用顺序排列的编码瓦片。</summary>
    public IReadOnlyList<HeifTile> Tiles { get; private init; } = Array.Empty<HeifTile>();

    /// <summary>主图的显示变换(按 ipma 顺序,先出现先作用)。</summary>
    public IReadOnlyList<HeifTransform> Transforms { get; private init; } = Array.Empty<HeifTransform>();

    /// <summary>
    /// 主图变换折算出的 EXIF 方向(1–8):6 = 显示前需顺时针转 90°,8 = 逆时针转 90°,
    /// 与 <c>irot</c> 角度(逆时针)及 <c>imir</c> 轴按 ipma 顺序复合得到。
    /// </summary>
    public int ExifOrientation { get; private init; } = 1;

    /// <summary>主图生效的色彩描述(网格项自身的 <c>colr</c> 优先,否则取首块瓦片)。</summary>
    public HeifColorInfo Color
    {
        get => _color;
        private init => _color = value;
    }

    /// <summary>主图生效的像素位深(优先 <c>pixi</c>,回落到 <c>hvcC</c>)。</summary>
    public HeifPixelInfo PixelInfo
    {
        get => _pixelInfo;
        private init => _pixelInfo = value;
    }

    /// <summary>主图 <c>hvcC</c> 声明的色度采样格式(0=单色,1=4:2:0,2=4:2:2,3=4:4:4)。</summary>
    public int ChromaFormat { get; private init; } = 1;

    /// <summary>主图项 id。</summary>
    public int PrimaryItemId { get; private init; }

    /// <summary>
    /// 文件中是否存在指向主图的 <c>auxl</c> 辅助图像(通常为 alpha 平面)。
    /// 解析器忽略辅助图像,不参与解码。
    /// </summary>
    public bool HasAuxiliaryImages { get; private init; }

    /// <summary>
    /// 通过 <c>thmb</c> 引用主图的缩略图项(<c>hvc1</c>),按短边升序。
    /// </summary>
    public IReadOnlyList<HeifCodedImage> Thumbnails { get; private init; } = Array.Empty<HeifCodedImage>();

    /// <summary>
    /// 挑选短边不小于 <paramref name="minShortSide"/> 的缩略图中最接近的一项;
    /// 若无满足项则退回最大缩略图;没有缩略图时返回 null。
    /// </summary>
    /// <param name="minShortSide">所需短边下限。</param>
    public HeifCodedImage? FindThumbnail(int minShortSide)
    {
        HeifCodedImage? best = null;
        HeifCodedImage? largest = null;
        for (var i = 0; i < Thumbnails.Count; i++)
        {
            var item = Thumbnails[i];
            if (largest is null || item.ShortSide > largest.ShortSide) largest = item;
            if (item.ShortSide >= minShortSide && (best is null || item.ShortSide < best.ShortSide)) best = item;
        }
        return best ?? largest;
    }

    /// <summary>
    /// 解析整个 HEIF 文件的字节。
    /// </summary>
    /// <param name="file">完整文件字节。</param>
    /// <param name="unsupportedReason">解析失败或不受支持时的原因;成功时为 null。</param>
    /// <returns>解析结果;不支持或数据损坏时返回 null。</returns>
    public static HeifContainer? TryParse(ReadOnlySpan<byte> file, out string? unsupportedReason)
    {
        unsupportedReason = null;
        if (file.Length < 16)
        {
            unsupportedReason = "文件长度不足,不是 ISOBMFF 容器";
            return null;
        }

        try
        {
            var result = Parse(file, out var reason);
            if (result is null)
            {
                unsupportedReason = reason ?? "不支持的 HEIF 结构";
                return null;
            }
            return result;
        }
        catch (HeifParseException ex)
        {
            unsupportedReason = "容器结构损坏:" + ex.Message;
            return null;
        }
    }

    private static HeifContainer? Parse(ReadOnlySpan<byte> file, out string? reason)
    {
        reason = null;

        var top = ReadBoxes(file, 0, file.Length);
        Box? meta = null;
        var hasFtyp = false;
        for (var i = 0; i < top.Count; i++)
        {
            var box = top[i];
            if (box.Type == BoxTypes.Ftyp) hasFtyp = true;
            else if (box.Type == BoxTypes.Meta && meta is null) meta = box;
        }

        if (meta is null)
        {
            reason = hasFtyp ? "缺少 meta 盒,不是 HEIF 图像" : "既无 ftyp 也无 meta 盒,不是 ISOBMFF 文件";
            return null;
        }

        var metaPayload = file.Slice(meta.Value.PayloadOffset, meta.Value.PayloadLength);
        if (metaPayload.Length < 4) throw new HeifParseException("meta 盒长度不足");

        // meta 是 FullBox,子盒从 4 字节版本/标志之后开始。
        var children = ReadBoxes(file, meta.Value.PayloadOffset + 4, meta.Value.PayloadOffset + meta.Value.PayloadLength);

        var primaryItemId = -1;
        var itemTypes = new Dictionary<int, uint>();
        var itemLocations = new Dictionary<int, ItemLocation>();
        var references = new Dictionary<uint, List<ItemReference>>();
        var properties = new List<ItemProperty>();
        var associations = new Dictionary<int, List<int>>();
        var hasIdat = false;
        var idatOffset = 0;
        var idatLength = 0;

        for (var i = 0; i < children.Count; i++)
        {
            var box = children[i];
            switch (box.Type)
            {
                case BoxTypes.Pitm:
                    primaryItemId = ParsePitm(file, box);
                    break;
                case BoxTypes.Iinf:
                    ParseIinf(file, box, itemTypes);
                    break;
                case BoxTypes.Iloc:
                    ParseIloc(file, box, itemLocations);
                    break;
                case BoxTypes.Iref:
                    ParseIref(file, box, references);
                    break;
                case BoxTypes.Iprp:
                    ParseIprp(file, box, properties, associations);
                    break;
                case BoxTypes.Idat:
                    if (!hasIdat)
                    {
                        hasIdat = true;
                        idatOffset = box.PayloadOffset;
                        idatLength = box.PayloadLength;
                    }
                    break;
            }
        }

        if (primaryItemId < 0)
        {
            reason = "缺少 pitm 主图声明";
            return null;
        }

        if (!itemTypes.TryGetValue(primaryItemId, out var primaryType))
        {
            reason = "主图项 " + primaryItemId.ToString(CultureInfo.InvariantCulture) + " 在 iinf 中无定义";
            return null;
        }

        var idat = hasIdat ? file.Slice(idatOffset, idatLength) : ReadOnlySpan<byte>.Empty;

        HeifItemKind kind;
        if (primaryType == BoxTypes.Grid) kind = HeifItemKind.Grid;
        else if (primaryType == BoxTypes.Hvc1) kind = HeifItemKind.CodedImage;
        else
        {
            reason = "主图类型 " + BoxTypes.Name(primaryType) + " 不受支持(仅 hvc1 与 grid)";
            return null;
        }

        if (!itemLocations.ContainsKey(primaryItemId))
        {
            reason = "主图项 " + primaryItemId.ToString(CultureInfo.InvariantCulture) + " 缺少 iloc 位置信息";
            return null;
        }

        var primaryProps = CollectProperties(associations, primaryItemId, properties);

        // ---- 网格布局 ----
        int width, height, columns, rows, tileWidth, tileHeight;
        var tiles = new List<HeifTile>();
        var tileIds = new List<int>();

        if (kind == HeifItemKind.Grid)
        {
            var payload = GetItemData(file, idat, itemLocations[primaryItemId], "网格项");
            if (payload.Length < 8) throw new HeifParseException("网格项数据不足 8 字节");
            var version = payload[0];
            if (version != 0)
            {
                reason = "网格项版本 " + version.ToString(CultureInfo.InvariantCulture) + " 不受支持";
                return null;
            }

            var flags = payload[1];
            rows = payload[2] + 1;
            columns = payload[3] + 1;
            var p = 4;
            if ((flags & 1) != 0)
            {
                if (p + 8 > payload.Length) throw new HeifParseException("网格项输出尺寸被截断");
                width = (int)BinaryPrimitives.ReadUInt32BigEndian(payload.Slice(p, 4));
                height = (int)BinaryPrimitives.ReadUInt32BigEndian(payload.Slice(p + 4, 4));
            }
            else
            {
                width = BinaryPrimitives.ReadUInt16BigEndian(payload.Slice(p, 2));
                height = BinaryPrimitives.ReadUInt16BigEndian(payload.Slice(p + 2, 2));
            }

            if (width <= 0 || height <= 0) throw new HeifParseException("网格项输出尺寸非法");
            if ((long)rows * columns > MaxTilesPerGrid)
            {
                reason = "网格规模 " + rows + "x" + columns.ToString(CultureInfo.InvariantCulture) + " 超出上限";
                return null;
            }

            tileIds = FindReferenceTargets(references, BoxTypes.Dimg, primaryItemId);
            if (tileIds.Count == 0)
            {
                reason = "网格主图缺少 dimg 瓦片引用";
                return null;
            }
            if (tileIds.Count != rows * columns)
            {
                reason = "网格声明 " + rows.ToString(CultureInfo.InvariantCulture) + "x"
                    + columns.ToString(CultureInfo.InvariantCulture) + " 但 dimg 引用 "
                    + tileIds.Count.ToString(CultureInfo.InvariantCulture) + " 块瓦片";
                return null;
            }

            tileWidth = 0;
            tileHeight = 0;
            for (var i = 0; i < tileIds.Count; i++)
            {
                var tileId = tileIds[i];
                if (!itemTypes.TryGetValue(tileId, out var type) || type != BoxTypes.Hvc1)
                {
                    reason = "瓦片项 " + tileId.ToString(CultureInfo.InvariantCulture) + " 不是 hvc1";
                    return null;
                }
                if (!itemLocations.ContainsKey(tileId))
                {
                    reason = "瓦片项 " + tileId.ToString(CultureInfo.InvariantCulture) + " 缺少 iloc 位置信息";
                    return null;
                }

                var props = CollectProperties(associations, tileId, properties);
                var ispe = FindProperty(props, BoxTypes.Ispe);
                if (ispe is null || ispe.IspeWidth <= 0 || ispe.IspeHeight <= 0)
                {
                    reason = "瓦片项 " + tileId.ToString(CultureInfo.InvariantCulture) + " 缺少 ispe 尺寸";
                    return null;
                }

                if (i == 0)
                {
                    tileWidth = ispe.IspeWidth;
                    tileHeight = ispe.IspeHeight;
                }
                else if (ispe.IspeWidth != tileWidth || ispe.IspeHeight != tileHeight)
                {
                    reason = "网格瓦片尺寸不一致(首块 " + tileWidth + "x" + tileHeight.ToString(CultureInfo.InvariantCulture)
                        + ",瓦片 " + tileId.ToString(CultureInfo.InvariantCulture) + " 为 "
                        + ispe.IspeWidth + "x" + ispe.IspeHeight.ToString(CultureInfo.InvariantCulture) + ")";
                    return null;
                }

                var hvcc = FindProperty(props, BoxTypes.HvcC)?.HvcC;
                if (hvcc is null)
                {
                    reason = "瓦片项 " + tileId.ToString(CultureInfo.InvariantCulture) + " 缺少 hvcC 参数集";
                    return null;
                }

                var tilePayload = GetItemData(file, idat, itemLocations[tileId], "瓦片项");
                tiles.Add(new HeifTile(
                    i % columns,
                    i / columns,
                    ispe.IspeWidth,
                    ispe.IspeHeight,
                    BuildAnnexB(file, hvcc, tilePayload)));
            }

            if ((long)columns * tileWidth < width || (long)rows * tileHeight < height)
            {
                reason = "网格画布 " + width + "x" + height.ToString(CultureInfo.InvariantCulture)
                    + " 超出瓦片拼合尺寸 " + (long)columns * tileWidth + "x" + ((long)rows * tileHeight).ToString(CultureInfo.InvariantCulture);
                return null;
            }
        }
        else
        {
            var ispe = FindProperty(primaryProps, BoxTypes.Ispe);
            if (ispe is null || ispe.IspeWidth <= 0 || ispe.IspeHeight <= 0)
            {
                reason = "主图项缺少 ispe 尺寸";
                return null;
            }
            var hvcc = FindProperty(primaryProps, BoxTypes.HvcC)?.HvcC;
            if (hvcc is null)
            {
                reason = "主图项缺少 hvcC 参数集";
                return null;
            }
            var payload = GetItemData(file, idat, itemLocations[primaryItemId], "主图项");
            width = ispe.IspeWidth;
            height = ispe.IspeHeight;
            columns = 1;
            rows = 1;
            tileWidth = width;
            tileHeight = height;
            tiles.Add(new HeifTile(0, 0, width, height, BuildAnnexB(file, hvcc, payload)));
        }

        // ---- 变换(同时校验 clap)----
        var transforms = new List<HeifTransform>(4);
        for (var i = 0; i < primaryProps.Count; i++)
        {
            var prop = primaryProps[i];
            switch (prop.Type)
            {
                case BoxTypes.Irot:
                    transforms.Add(new HeifTransform(HeifTransformKind.Rotation, prop.Rotation));
                    break;
                case BoxTypes.Imir:
                    transforms.Add(new HeifTransform(HeifTransformKind.Mirror, prop.MirrorAxis));
                    break;
                case BoxTypes.Clap:
                    if (prop.Clap is { } clap && !clap.IsIdentity(width, height))
                    {
                        reason = "clap 清洁光圈实际裁剪(" + clap.ToString() + "),需要 libheif 回落";
                        return null;
                    }
                    break;
            }
        }

        // ---- 色彩与位深:网格项自身的属性优先,其次取首块瓦片 ----
        List<ItemProperty> colorProps = primaryProps;
        if (FindProperty(colorProps, BoxTypes.Colr) is null && kind == HeifItemKind.Grid)
        {
            colorProps = CollectProperties(associations, tileIds[0], properties);
        }

        var color = FindProperty(colorProps, BoxTypes.Colr)?.Color ?? HeifColorInfo.Empty;
        var primaryHvcc = FindProperty(primaryProps, BoxTypes.HvcC)?.HvcC
            ?? (kind == HeifItemKind.Grid
                ? FindProperty(CollectProperties(associations, tileIds[0], properties), BoxTypes.HvcC)?.HvcC
                : null);

        var pixi = FindProperty(primaryProps, BoxTypes.Pixi)?.BitsPerChannel;
        if (pixi is null && kind == HeifItemKind.Grid)
        {
            pixi = FindProperty(CollectProperties(associations, tileIds[0], properties), BoxTypes.Pixi)?.BitsPerChannel;
        }

        var lumaBits = pixi is { Length: > 0 } ? pixi[0] : primaryHvcc?.BitDepthLuma ?? 8;
        var chromaBits = pixi is { Length: > 1 } ? pixi[1] : primaryHvcc?.BitDepthChroma ?? lumaBits;
        var pixelInfo = new HeifPixelInfo
        {
            BitsPerChannel = pixi ?? Array.Empty<int>(),
            LumaBits = lumaBits,
            ChromaBits = chromaBits,
        };

        var chromaFormat = primaryHvcc?.ChromaFormat ?? 1;

        // ---- 缩略图(thmb 引用主图的项)----
        var thumbnails = new List<HeifCodedImage>();
        foreach (var pair in references)
        {
            if (pair.Key != BoxTypes.Thmb) continue;
            for (var i = 0; i < pair.Value.Count; i++)
            {
                var reference = pair.Value[i];
                if (Array.IndexOf(reference.Targets, primaryItemId) < 0) continue;
                var thumbId = reference.FromItemId;
                if (!itemTypes.TryGetValue(thumbId, out var thumbType) || thumbType != BoxTypes.Hvc1) continue;
                if (!itemLocations.ContainsKey(thumbId)) continue;

                var props = CollectProperties(associations, thumbId, properties);
                var ispe = FindProperty(props, BoxTypes.Ispe);
                var hvcc = FindProperty(props, BoxTypes.HvcC)?.HvcC;
                if (ispe is null || hvcc is null) continue;

                var payload = GetItemData(file, idat, itemLocations[thumbId], "缩略图项");
                var thumbTransforms = BuildTransforms(props);
                var thumbPixi = FindProperty(props, BoxTypes.Pixi)?.BitsPerChannel;
                thumbnails.Add(new HeifCodedImage(thumbId, ispe.IspeWidth, ispe.IspeHeight, BuildAnnexB(file, hvcc, payload))
                {
                    Transforms = thumbTransforms,
                    ExifOrientation = ToExifOrientation(thumbTransforms),
                    Color = FindProperty(props, BoxTypes.Colr)?.Color ?? color,
                    PixelInfo = new HeifPixelInfo
                    {
                        BitsPerChannel = thumbPixi ?? Array.Empty<int>(),
                        LumaBits = thumbPixi is { Length: > 0 } ? thumbPixi[0] : hvcc.BitDepthLuma,
                        ChromaBits = thumbPixi is { Length: > 1 } ? thumbPixi[1] : hvcc.BitDepthChroma,
                    },
                    ChromaFormat = hvcc.ChromaFormat,
                });
            }
        }
        thumbnails.Sort(static (a, b) => a.ShortSide.CompareTo(b.ShortSide));

        var hasAuxiliary = false;
        if (references.TryGetValue(BoxTypes.Auxl, out var auxl))
        {
            for (var i = 0; i < auxl.Count; i++)
            {
                if (Array.IndexOf(auxl[i].Targets, primaryItemId) >= 0)
                {
                    hasAuxiliary = true;
                    break;
                }
            }
        }

        return new HeifContainer
        {
            Width = width,
            Height = height,
            Columns = columns,
            Rows = rows,
            TileWidth = tileWidth,
            TileHeight = tileHeight,
            Kind = kind,
            PrimaryItemId = primaryItemId,
            Tiles = tiles,
            Transforms = transforms,
            ExifOrientation = ToExifOrientation(transforms),
            Color = color,
            PixelInfo = pixelInfo,
            ChromaFormat = chromaFormat,
            HasAuxiliaryImages = hasAuxiliary,
            Thumbnails = thumbnails,
        };
    }

    /// <summary>
    /// 把 ipma 顺序的变换复合折算成 EXIF 方向 1–8。
    /// </summary>
    /// <remarks>
    /// 复合元表示为 (k, m),对应显示映射 <c>Flip^m ∘ Rot^k</c>(先逆时针转 k×90°,再按需左右翻转)。
    /// 后加入的变换 A 作用在当前结果上,由 <c>Rot^k ∘ Flip = Flip ∘ Rot^(−k)</c> 得
    /// <c>k' = k ± kA</c>(当前已含左右翻转取减号)、<c>m' = m ⊕ mA</c>;
    /// 其中 <c>irot a = (a,0)</c>、<c>imir 0 = (0,1)</c>、<c>imir 1 = (2,1)</c>。
    /// </remarks>
    internal static int ToExifOrientation(IReadOnlyList<HeifTransform> transforms)
    {
        var k = 0;
        var m = 0;
        for (var i = 0; i < transforms.Count; i++)
        {
            var t = transforms[i];
            if (t.Kind == HeifTransformKind.Rotation)
            {
                k = m == 0 ? (k + t.Value) & 3 : (k - t.Value) & 3;
            }
            else
            {
                // 左右翻转是生成元 FlipLR;上下翻转 = FlipLR∘Rot180,故 k2 取 2。
                var mirrorRotation = t.Value == 0 ? 0 : 2;
                k = m == 0 ? (k + mirrorRotation) & 3 : (k - mirrorRotation) & 3;
                m ^= 1;
            }
        }

        return (k, m) switch
        {
            (0, 0) => 1,
            (0, 1) => 2,
            (1, 0) => 8,
            (1, 1) => 7,
            (2, 0) => 3,
            (2, 1) => 4,
            (3, 0) => 6,
            _ => 5,
        };
    }

    private static List<HeifTransform> BuildTransforms(List<ItemProperty> props)
    {
        var list = new List<HeifTransform>(2);
        for (var i = 0; i < props.Count; i++)
        {
            var prop = props[i];
            if (prop.Type == BoxTypes.Irot) list.Add(new HeifTransform(HeifTransformKind.Rotation, prop.Rotation));
            else if (prop.Type == BoxTypes.Imir) list.Add(new HeifTransform(HeifTransformKind.Mirror, prop.MirrorAxis));
        }
        return list;
    }

    private static List<ItemProperty> CollectProperties(
        Dictionary<int, List<int>> associations,
        int itemId,
        List<ItemProperty> properties)
    {
        var result = new List<ItemProperty>(4);
        if (!associations.TryGetValue(itemId, out var indices)) return result;
        for (var i = 0; i < indices.Count; i++)
        {
            var index = indices[i];
            if (index > 0 && index <= properties.Count) result.Add(properties[index - 1]);
        }
        return result;
    }

    private static ItemProperty? FindProperty(List<ItemProperty> props, uint type)
    {
        for (var i = 0; i < props.Count; i++)
        {
            if (props[i].Type == type) return props[i];
        }
        return null;
    }

    private static List<int> FindReferenceTargets(Dictionary<uint, List<ItemReference>> references, uint type, int fromItemId)
    {
        var result = new List<int>(8);
        if (!references.TryGetValue(type, out var list)) return result;
        for (var i = 0; i < list.Count; i++)
        {
            if (list[i].FromItemId == fromItemId)
            {
                for (var j = 0; j < list[i].Targets.Length; j++) result.Add(list[i].Targets[j]);
            }
        }
        return result;
    }

    // ------------------------------------------------------------------
    // 具体盒解析
    // ------------------------------------------------------------------

    private static int ParsePitm(ReadOnlySpan<byte> file, Box box)
    {
        var reader = new Reader(file, box.PayloadOffset, box.PayloadLength);
        var version = reader.U8();
        reader.Skip(3);
        return version == 0 ? reader.U16() : (int)reader.U32();
    }

    private static void ParseIinf(ReadOnlySpan<byte> file, Box box, Dictionary<int, uint> itemTypes)
    {
        var version = file[box.PayloadOffset];
        var payloadEnd = box.PayloadOffset + box.PayloadLength;
        var start = box.PayloadOffset + 4;
        if (version == 0) start += 2;
        else start += 4;

        var entries = ReadBoxes(file, start, payloadEnd);
        for (var i = 0; i < entries.Count; i++)
        {
            var entry = entries[i];
            if (entry.Type != BoxTypes.Infe) continue;
            var reader = new Reader(file, entry.PayloadOffset, entry.PayloadLength);
            var infeVersion = reader.U8();
            reader.Skip(3);
            var itemId = infeVersion >= 3 ? (int)reader.U32() : reader.U16();
            reader.Skip(2); // item_protection_index
            if (reader.Remaining < 4) continue;
            itemTypes[itemId] = reader.U32();
        }
    }

    private static void ParseIloc(ReadOnlySpan<byte> file, Box box, Dictionary<int, ItemLocation> locations)
    {
        var reader = new Reader(file, box.PayloadOffset, box.PayloadLength);
        var version = reader.U8();
        reader.Skip(3);
        var sizes = reader.U16();
        var offsetSize = (sizes >> 12) & 0xF;
        var lengthSize = (sizes >> 8) & 0xF;
        var baseOffsetSize = (sizes >> 4) & 0xF;
        var indexSize = (version == 1 || version == 2) ? sizes & 0xF : 0;
        var itemCount = version < 2 ? reader.U16() : (int)reader.U32();

        for (var i = 0; i < itemCount; i++)
        {
            var itemId = version < 2 ? reader.U16() : (int)reader.U32();
            var constructionMethod = 0;
            if (version == 1 || version == 2) constructionMethod = reader.U16() & 0xF;
            reader.Skip(2); // data_reference_index
            var baseOffset = reader.USize(baseOffsetSize);
            var extentCount = reader.U16();
            var extents = new Extent[extentCount];
            for (var j = 0; j < extentCount; j++)
            {
                if (indexSize > 0) reader.Skip(indexSize);
                var offset = reader.USize(offsetSize);
                var length = reader.USize(lengthSize);
                extents[j] = new Extent(offset, length);
            }
            locations[itemId] = new ItemLocation(itemId, constructionMethod, baseOffset, extents);
        }
    }

    private static void ParseIref(ReadOnlySpan<byte> file, Box box, Dictionary<uint, List<ItemReference>> references)
    {
        var version = file[box.PayloadOffset];
        var idSize = version == 0 ? 2 : 4;
        var payloadEnd = box.PayloadOffset + box.PayloadLength;
        var offset = box.PayloadOffset + 4;

        while (offset + 8 <= payloadEnd)
        {
            var size = (int)BinaryPrimitives.ReadUInt32BigEndian(file.Slice(offset, 4));
            var type = BinaryPrimitives.ReadUInt32BigEndian(file.Slice(offset + 4, 4));
            if (size < 8 || offset + size > payloadEnd) break;

            var reader = new Reader(file, offset + 8, size - 8);
            var fromItemId = idSize == 2 ? reader.U16() : (int)reader.U32();
            var count = reader.U16();
            var targets = new int[count];
            var read = 0;
            for (var i = 0; i < count; i++)
            {
                if (reader.Remaining < idSize) break;
                targets[i] = idSize == 2 ? reader.U16() : (int)reader.U32();
                read++;
            }

            if (read != count) Array.Resize(ref targets, read);
            if (!references.TryGetValue(type, out var list))
            {
                list = new List<ItemReference>(4);
                references[type] = list;
            }
            list.Add(new ItemReference(fromItemId, targets));
            offset += size;
        }
    }

    private static void ParseIprp(
        ReadOnlySpan<byte> file,
        Box box,
        List<ItemProperty> properties,
        Dictionary<int, List<int>> associations)
    {
        var children = ReadBoxes(file, box.PayloadOffset, box.PayloadOffset + box.PayloadLength);
        for (var i = 0; i < children.Count; i++)
        {
            var child = children[i];
            if (child.Type == BoxTypes.Ipco)
            {
                var propertyBoxes = ReadBoxes(file, child.PayloadOffset, child.PayloadOffset + child.PayloadLength);
                for (var j = 0; j < propertyBoxes.Count; j++)
                {
                    properties.Add(ParseProperty(file, propertyBoxes[j]));
                }
            }
            else if (child.Type == BoxTypes.Ipma)
            {
                ParseIpma(file, child, associations);
            }
        }
    }

    private static void ParseIpma(ReadOnlySpan<byte> file, Box box, Dictionary<int, List<int>> associations)
    {
        var reader = new Reader(file, box.PayloadOffset, box.PayloadLength);
        var version = reader.U8();
        var flags = (int)reader.U24();
        var entryCount = (int)reader.U32();
        var wide = (flags & 1) != 0;

        for (var i = 0; i < entryCount; i++)
        {
            var itemId = version < 1 ? reader.U16() : (int)reader.U32();
            var count = reader.U8();
            var list = new List<int>(count);
            for (var j = 0; j < count; j++)
            {
                int index;
                if (wide)
                {
                    index = reader.U16() & 0x7FFF;
                }
                else
                {
                    index = reader.U8() & 0x7F;
                }
                list.Add(index);
            }
            if (!associations.TryGetValue(itemId, out var existing))
            {
                associations[itemId] = list;
            }
            else
            {
                existing.AddRange(list);
            }
        }
    }

    private static ItemProperty ParseProperty(ReadOnlySpan<byte> file, Box box)
    {
        var payload = file.Slice(box.PayloadOffset, box.PayloadLength);
        switch (box.Type)
        {
            case BoxTypes.HvcC:
                return ItemProperty.CreateHvcC(ParseHvcC(payload, box.PayloadOffset));

            case BoxTypes.Ispe:
            {
                if (payload.Length < 12) throw new HeifParseException("ispe 长度不足");
                return ItemProperty.CreateIspe(
                    (int)BinaryPrimitives.ReadUInt32BigEndian(payload.Slice(4, 4)),
                    (int)BinaryPrimitives.ReadUInt32BigEndian(payload.Slice(8, 4)));
            }

            case BoxTypes.Colr:
                return ItemProperty.CreateColor(ParseColr(payload));

            case BoxTypes.Irot:
            {
                var angle = ReadTransformValue(payload, "irot");
                return ItemProperty.CreateIrot(angle & 0x03);
            }

            case BoxTypes.Imir:
            {
                var axis = ReadTransformValue(payload, "imir");
                return ItemProperty.CreateImir(axis & 0x01);
            }

            case BoxTypes.Clap:
            {
                if (payload.Length < 32) throw new HeifParseException("clap 长度不足");
                return ItemProperty.CreateClap(new CleanAperture(
                    (int)BinaryPrimitives.ReadUInt32BigEndian(payload.Slice(0, 4)),
                    (int)BinaryPrimitives.ReadUInt32BigEndian(payload.Slice(4, 4)),
                    (int)BinaryPrimitives.ReadUInt32BigEndian(payload.Slice(8, 4)),
                    (int)BinaryPrimitives.ReadUInt32BigEndian(payload.Slice(12, 4)),
                    (int)BinaryPrimitives.ReadUInt32BigEndian(payload.Slice(16, 4)),
                    (int)BinaryPrimitives.ReadUInt32BigEndian(payload.Slice(20, 4)),
                    (int)BinaryPrimitives.ReadUInt32BigEndian(payload.Slice(24, 4)),
                    (int)BinaryPrimitives.ReadUInt32BigEndian(payload.Slice(28, 4))));
            }

            case BoxTypes.Pixi:
            {
                if (payload.Length < 5) throw new HeifParseException("pixi 长度不足");
                var channelCount = payload[4];
                var bits = new int[channelCount];
                for (var i = 0; i < channelCount; i++)
                {
                    if (5 + i >= payload.Length) break;
                    bits[i] = payload[5 + i];
                }
                return ItemProperty.CreatePixi(bits);
            }

            default:
                return ItemProperty.CreateOther(box.Type);
        }
    }

    /// <summary>
    /// 读取 <c>irot</c>/<c>imir</c> 的取值字节。
    /// </summary>
    /// <remarks>
    /// 规范里两者都是普通 <c>ItemProperty</c>(载荷只有 1 字节,无版本/标志),
    /// Sony 的 .HIF 与 libheif 都按 1 字节写入/读取;个别写入器按 FullBox 写成 5 字节,这里一并兼容。
    /// </remarks>
    private static byte ReadTransformValue(ReadOnlySpan<byte> payload, string name)
    {
        if (payload.Length >= 5) return payload[4];
        if (payload.Length >= 1) return payload[0];
        throw new HeifParseException(name + " 长度为 0");
    }

    private static HeifColorInfo ParseColr(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < 4) throw new HeifParseException("colr 长度不足");
        var type = BinaryPrimitives.ReadUInt32BigEndian(payload.Slice(0, 4));
        if (type == BoxTypes.Nclx)
        {
            if (payload.Length < 11) throw new HeifParseException("colr/nclx 长度不足");
            return new HeifColorInfo
            {
                HasNclx = true,
                Primaries = BinaryPrimitives.ReadUInt16BigEndian(payload.Slice(4, 2)),
                Transfer = BinaryPrimitives.ReadUInt16BigEndian(payload.Slice(6, 2)),
                Matrix = BinaryPrimitives.ReadUInt16BigEndian(payload.Slice(8, 2)),
                FullRange = (payload[10] & 0x80) != 0,
            };
        }

        if (type == BoxTypes.Prof || type == BoxTypes.Ricc)
        {
            return new HeifColorInfo { IccProfile = payload.Slice(4).ToArray() };
        }

        // 其余(如 nclc 老式三字节描述)暂不解析,回落为无色彩描述。
        return HeifColorInfo.Empty;
    }

    private static HvcCInfo ParseHvcC(ReadOnlySpan<byte> payload, int payloadAbsoluteOffset)
    {
        if (payload.Length < 23) throw new HeifParseException("hvcC 长度不足");

        var info = new HvcCInfo
        {
            ChromaFormat = payload[16] & 0x03,
            BitDepthLuma = (payload[17] & 0x07) + 8,
            BitDepthChroma = (payload[18] & 0x07) + 8,
            LengthSize = (payload[21] & 0x03) + 1,
        };

        var arrayCount = payload[22];
        var offset = 23;
        for (var i = 0; i < arrayCount; i++)
        {
            if (offset + 3 > payload.Length) throw new HeifParseException("hvcC NAL 数组被截断");
            var header = payload[offset];
            var nalType = header & 0x3F;
            var nalCount = BinaryPrimitives.ReadUInt16BigEndian(payload.Slice(offset + 1, 2));
            offset += 3;

            var offsets = new int[nalCount];
            var lengths = new int[nalCount];
            for (var j = 0; j < nalCount; j++)
            {
                if (offset + 2 > payload.Length) throw new HeifParseException("hvcC NAL 长度被截断");
                var length = BinaryPrimitives.ReadUInt16BigEndian(payload.Slice(offset, 2));
                offset += 2;
                if (offset + length > payload.Length) throw new HeifParseException("hvcC NAL 数据被截断");
                offsets[j] = payloadAbsoluteOffset + offset;
                lengths[j] = length;
                offset += length;
            }
            info.Arrays.Add(new HvcCNalArray(nalType, offsets, lengths));
        }

        return info;
    }

    /// <summary>
    /// 把 <c>hvcC</c> 参数集与项数据拼成 Annex-B 码流,长度前缀改写为 4 字节起始码。
    /// </summary>
    /// <remarks><paramref name="payload"/> 与 <c>hvcC</c> 的 NAL 偏移都相对同一个文件缓冲。</remarks>
    private static byte[] BuildAnnexB(ReadOnlySpan<byte> file, HvcCInfo hvcc, ReadOnlySpan<byte> payload)
    {
        // 先算长度,避免中间拼接。
        long total = 0;
        for (var i = 0; i < hvcc.Arrays.Count; i++)
        {
            var array = hvcc.Arrays[i];
            for (var j = 0; j < array.Lengths.Length; j++) total += 4 + array.Lengths[j];
        }

        var lengthSize = hvcc.LengthSize;
        var spans = new List<(int Offset, int Length)>(16);
        var position = 0;
        while (position + lengthSize <= payload.Length)
        {
            var nalLength = ReadBigEndian(payload.Slice(position, lengthSize));
            position += lengthSize;
            if (nalLength <= 0 || position + nalLength > payload.Length) break;
            total += 4 + nalLength;
            spans.Add((position, nalLength));
            position += nalLength;
        }

        if (total > MaxAnnexBBytes) throw new HeifParseException("瓦片码流超过 256 MB 上限");

        var result = new byte[total];
        var write = 0;
        for (var i = 0; i < hvcc.Arrays.Count; i++)
        {
            var array = hvcc.Arrays[i];
            for (var j = 0; j < array.Lengths.Length; j++)
            {
                write += WriteStartCode(result, write);
                file.Slice(array.Offsets[j], array.Lengths[j]).CopyTo(result.AsSpan(write));
                write += array.Lengths[j];
            }
        }

        for (var i = 0; i < spans.Count; i++)
        {
            write += WriteStartCode(result, write);
            payload.Slice(spans[i].Offset, spans[i].Length).CopyTo(result.AsSpan(write));
            write += spans[i].Length;
        }

        return result;
    }

    private static int WriteStartCode(byte[] destination, int offset)
    {
        destination[offset] = 0;
        destination[offset + 1] = 0;
        destination[offset + 2] = 0;
        destination[offset + 3] = 1;
        return 4;
    }

    private static int ReadBigEndian(ReadOnlySpan<byte> data)
    {
        var value = 0;
        for (var i = 0; i < data.Length; i++) value = (value << 8) | data[i];
        return value;
    }

    /// <summary>取出项数据(construction_method 0=文件偏移,1=idat 内偏移)。</summary>
    private static ReadOnlySpan<byte> GetItemData(
        ReadOnlySpan<byte> file,
        ReadOnlySpan<byte> idat,
        ItemLocation location,
        string what)
    {
        if (location.ConstructionMethod == 2)
        {
            throw new HeifParseException(what + "使用 construction_method=2(项间引用),不受支持");
        }

        var source = location.ConstructionMethod == 1 ? idat : file;
        if (location.ConstructionMethod == 1 && idat.IsEmpty)
        {
            throw new HeifParseException(what + "引用 idat 但文件缺少 idat 盒");
        }

        if (location.Extents.Length == 1)
        {
            var start = location.BaseOffset + location.Extents[0].Offset;
            var length = location.Extents[0].Length;
            if (start < 0 || length < 0 || start + length > source.Length)
            {
                throw new HeifParseException(what + "数据范围越界");
            }
            return source.Slice((int)start, (int)length);
        }

        long totalLength = 0;
        for (var i = 0; i < location.Extents.Length; i++) totalLength += location.Extents[i].Length;
        if (totalLength > MaxAnnexBBytes) throw new HeifParseException(what + "数据超过 256 MB 上限");

        var buffer = new byte[totalLength];
        var written = 0;
        for (var i = 0; i < location.Extents.Length; i++)
        {
            var start = location.BaseOffset + location.Extents[i].Offset;
            var length = location.Extents[i].Length;
            if (start < 0 || length < 0 || start + length > source.Length)
            {
                throw new HeifParseException(what + "数据范围越界");
            }
            source.Slice((int)start, (int)length).CopyTo(buffer.AsSpan(written));
            written += (int)length;
        }
        return buffer;
    }

    // ------------------------------------------------------------------
    // 盒遍历与基础类型
    // ------------------------------------------------------------------

    private static List<Box> ReadBoxes(ReadOnlySpan<byte> data, int start, int end)
    {
        var boxes = new List<Box>(8);
        var offset = start;
        while (offset + 8 <= end)
        {
            long size = BinaryPrimitives.ReadUInt32BigEndian(data.Slice(offset, 4));
            var type = BinaryPrimitives.ReadUInt32BigEndian(data.Slice(offset + 4, 4));
            var header = 8;
            if (size == 1)
            {
                if (offset + 16 > end) break;
                size = (long)BinaryPrimitives.ReadUInt64BigEndian(data.Slice(offset + 8, 8));
                header = 16;
            }
            else if (size == 0)
            {
                size = end - offset;
            }

            if (size < header || offset + size > end) break;
            boxes.Add(new Box(type, offset + header, (int)size - header));
            offset += (int)size;
        }
        return boxes;
    }

    private readonly record struct Box(uint Type, int PayloadOffset, int PayloadLength);

    private readonly record struct Extent(long Offset, long Length);

    private sealed class ItemLocation
    {
        public ItemLocation(int itemId, int constructionMethod, long baseOffset, Extent[] extents)
        {
            ItemId = itemId;
            ConstructionMethod = constructionMethod;
            BaseOffset = baseOffset;
            Extents = extents;
        }

        public int ItemId { get; }

        public int ConstructionMethod { get; }

        public long BaseOffset { get; }

        public Extent[] Extents { get; }
    }

    private sealed class ItemReference
    {
        public ItemReference(int fromItemId, int[] targets)
        {
            FromItemId = fromItemId;
            Targets = targets;
        }

        public int FromItemId { get; }

        public int[] Targets { get; }
    }

    private sealed class HvcCInfo
    {
        public int LengthSize { get; set; } = 4;

        public int ChromaFormat { get; set; } = 1;

        public int BitDepthLuma { get; set; } = 8;

        public int BitDepthChroma { get; set; } = 8;

        public List<HvcCNalArray> Arrays { get; } = new(4);
    }

    private sealed class HvcCNalArray
    {
        public HvcCNalArray(int type, int[] offsets, int[] lengths)
        {
            Type = type;
            Offsets = offsets;
            Lengths = lengths;
        }

        public int Type { get; }

        public int[] Offsets { get; }

        public int[] Lengths { get; }
    }

    private readonly record struct CleanAperture(
        int WidthN,
        int WidthD,
        int HeightN,
        int HeightD,
        int HorizOffN,
        int HorizOffD,
        int VertOffN,
        int VertOffD)
    {
        /// <summary>尺寸等于编码尺寸且偏移为 0 才算恒等(不裁剪)。</summary>
        public bool IsIdentity(int codedWidth, int codedHeight)
            => HorizOffN == 0 && VertOffN == 0
                && WidthD > 0 && HeightD > 0
                && (long)WidthN == (long)WidthD * codedWidth
                && (long)HeightN == (long)HeightD * codedHeight;

        public override string ToString()
            => WidthN.ToString(CultureInfo.InvariantCulture) + "/" + WidthD.ToString(CultureInfo.InvariantCulture)
                + "x" + HeightN.ToString(CultureInfo.InvariantCulture) + "/" + HeightD.ToString(CultureInfo.InvariantCulture)
                + " off " + HorizOffN.ToString(CultureInfo.InvariantCulture) + "/" + HorizOffD.ToString(CultureInfo.InvariantCulture)
                + "," + VertOffN.ToString(CultureInfo.InvariantCulture) + "/" + VertOffD.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>ipco 中的一项属性(已解析出需要的内容)。</summary>
    private sealed class ItemProperty
    {
        public uint Type { get; private init; }

        public HvcCInfo? HvcC { get; private init; }

        public int IspeWidth { get; private init; }

        public int IspeHeight { get; private init; }

        public HeifColorInfo? Color { get; private init; }

        public int[]? BitsPerChannel { get; private init; }

        public int Rotation { get; private init; }

        public int MirrorAxis { get; private init; }

        public CleanAperture? Clap { get; private init; }

        public static ItemProperty CreateHvcC(HvcCInfo hvcc) => new() { Type = BoxTypes.HvcC, HvcC = hvcc };

        public static ItemProperty CreateIspe(int width, int height)
            => new() { Type = BoxTypes.Ispe, IspeWidth = width, IspeHeight = height };

        public static ItemProperty CreateColor(HeifColorInfo color) => new() { Type = BoxTypes.Colr, Color = color };

        public static ItemProperty CreateIrot(int angle) => new() { Type = BoxTypes.Irot, Rotation = angle };

        public static ItemProperty CreateImir(int axis) => new() { Type = BoxTypes.Imir, MirrorAxis = axis };

        public static ItemProperty CreateClap(CleanAperture clap) => new() { Type = BoxTypes.Clap, Clap = clap };

        public static ItemProperty CreatePixi(int[] bits) => new() { Type = BoxTypes.Pixi, BitsPerChannel = bits };

        public static ItemProperty CreateOther(uint type) => new() { Type = type };
    }

    /// <summary>顺序读取器,越界一律抛 <see cref="HeifParseException"/>。</summary>
    private ref struct Reader
    {
        private readonly ReadOnlySpan<byte> _data;
        private readonly int _end;
        private int _position;

        public Reader(ReadOnlySpan<byte> data, int start, int length)
        {
            if (start < 0 || length < 0 || start + length > data.Length)
            {
                throw new HeifParseException("盒载荷越界");
            }
            _data = data;
            _position = start;
            _end = start + length;
        }

        public int Remaining => _end - _position;

        public byte U8()
        {
            Need(1);
            return _data[_position++];
        }

        public ushort U16()
        {
            Need(2);
            var value = BinaryPrimitives.ReadUInt16BigEndian(_data.Slice(_position, 2));
            _position += 2;
            return value;
        }

        public uint U24()
        {
            Need(3);
            var value = ((uint)_data[_position] << 16) | ((uint)_data[_position + 1] << 8) | _data[_position + 2];
            _position += 3;
            return value;
        }

        public uint U32()
        {
            Need(4);
            var value = BinaryPrimitives.ReadUInt32BigEndian(_data.Slice(_position, 4));
            _position += 4;
            return value;
        }

        public long USize(int bytes)
        {
            Need(bytes);
            long value = 0;
            for (var i = 0; i < bytes; i++) value = (value << 8) | _data[_position + i];
            _position += bytes;
            return value;
        }

        public void Skip(int count)
        {
            Need(count);
            _position += count;
        }

        private void Need(int count)
        {
            if (count < 0 || _position + count > _end) throw new HeifParseException("盒载荷不完整");
        }
    }

    /// <summary>四字符码常量(以十六进制字面量写成,便于在 <c>case</c> 标签中使用)。</summary>
    private static class BoxTypes
    {
        public const uint Ftyp = 0x66747970; // 'ftyp'
        public const uint Meta = 0x6D657461; // 'meta'
        public const uint Hdlr = 0x68646C72; // 'hdlr'
        public const uint Pitm = 0x7069746D; // 'pitm'
        public const uint Iinf = 0x69696E66; // 'iinf'
        public const uint Infe = 0x696E6665; // 'infe'
        public const uint Iloc = 0x696C6F63; // 'iloc'
        public const uint Idat = 0x69646174; // 'idat'
        public const uint Iref = 0x69726566; // 'iref'
        public const uint Iprp = 0x69707270; // 'iprp'
        public const uint Ipco = 0x6970636F; // 'ipco'
        public const uint Ipma = 0x69706D61; // 'ipma'
        public const uint HvcC = 0x68766343; // 'hvcC'
        public const uint Ispe = 0x69737065; // 'ispe'
        public const uint Colr = 0x636F6C72; // 'colr'
        public const uint Nclx = 0x6E636C78; // 'nclx'
        public const uint Prof = 0x70726F66; // 'prof'
        public const uint Ricc = 0x72494343; // 'rICC'
        public const uint Irot = 0x69726F74; // 'irot'
        public const uint Imir = 0x696D6972; // 'imir'
        public const uint Clap = 0x636C6170; // 'clap'
        public const uint Pixi = 0x70697869; // 'pixi'
        public const uint Grid = 0x67726964; // 'grid'
        public const uint Hvc1 = 0x68766331; // 'hvc1'
        public const uint Dimg = 0x64696D67; // 'dimg'
        public const uint Thmb = 0x74686D62; // 'thmb'
        public const uint Auxl = 0x6175786C; // 'auxl'
        public const uint Cdsc = 0x63647363; // 'cdsc'

        public static string Name(uint type)
        {
            Span<char> chars = stackalloc char[4];
            chars[0] = (char)((type >> 24) & 0xFF);
            chars[1] = (char)((type >> 16) & 0xFF);
            chars[2] = (char)((type >> 8) & 0xFF);
            chars[3] = (char)(type & 0xFF);
            var builder = new StringBuilder(4);
            for (var i = 0; i < 4; i++)
            {
                var c = chars[i];
                builder.Append(c is >= ' ' and <= '~' ? c : '?');
            }
            return builder.ToString();
        }
    }

    private sealed class HeifParseException : Exception
    {
        public HeifParseException(string message)
            : base(message)
        {
        }
    }
}
