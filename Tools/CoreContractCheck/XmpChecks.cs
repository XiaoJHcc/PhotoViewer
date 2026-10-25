using System.Text;
using PhotoViewer.Core;
using XmpCore;

namespace CoreContractCheck;

internal static class XmpChecks
{
    public static void Run(Checks c, string root)
    {
        c.Section("XMP Rating 语义与包编辑");

        var attribute = Packet("<rdf:Description rdf:about=\"\" xmlns:xmp=\"http://ns.adobe.com/xap/1.0/\" xmp:Rating=\"3\"/>");
        var attributeResult = XmpRatingReader.ReadPacket(attribute, XmpRatingSource.Embedded);
        c.Equal("属性式 Rating 状态", XmpRatingState.Rated, attributeResult.State);
        c.Equal("属性式 Rating 值", 3, attributeResult.Value);

        var element = Packet("<rdf:Description rdf:about=\"\" xmlns:xmp=\"http://ns.adobe.com/xap/1.0/\"><xmp:Rating>4</xmp:Rating></rdf:Description>");
        c.Equal("元素式 Rating", 4, XmpRatingReader.ReadPacket(element, XmpRatingSource.Embedded).Value);

        var singleQuoted = Packet("<rdf:Description rdf:about='' xmlns:xmp='http://ns.adobe.com/xap/1.0/' xmp:Rating = '2'/>");
        c.Equal("单引号与空白 Rating", 2, XmpRatingReader.ReadPacket(singleQuoted, XmpRatingSource.Embedded).Value);

        var customPrefix = Packet("<rdf:Description rdf:about=\"\" xmlns:foo=\"http://ns.adobe.com/xap/1.0/\" foo:Rating=\"5\"/>");
        c.Equal("命名空间优先于前缀", 5, XmpRatingReader.ReadPacket(customPrefix, XmpRatingSource.Embedded).Value);

        var rejected = Packet("<rdf:Description rdf:about=\"\" xmlns:xmp=\"http://ns.adobe.com/xap/1.0/\" xmp:Rating=\"-1\"/>");
        c.Equal("-1 保留 rejected 语义", XmpRatingState.Rejected,
            XmpRatingReader.ReadPacket(rejected, XmpRatingSource.Embedded).State);

        var invalid = Packet("<rdf:Description rdf:about=\"\" xmlns:xmp=\"http://ns.adobe.com/xap/1.0/\" xmp:Rating=\"9\"/>");
        c.Equal("越界 Rating 明确无效", XmpRatingState.Invalid,
            XmpRatingReader.ReadPacket(invalid, XmpRatingSource.Embedded).State);

        var missing = Packet("<rdf:Description rdf:about=\"\" xmlns:dc=\"http://purl.org/dc/elements/1.1/\"><dc:title>照片</dc:title></rdf:Description>");
        c.Equal("无 Rating 是 Missing", XmpRatingState.Missing,
            XmpRatingReader.ReadPacket(missing, XmpRatingSource.Embedded).State);

        foreach (var (name, original, target) in new[]
        {
            ("属性式", attribute, 5),
            ("元素式", element, 1),
            ("单引号", singleQuoted, 4),
            ("无 Rating", missing, 3),
            ("-1", rejected, 0),
        })
        {
            var updated = XmpPacketEditor.SetRating(original, target, preserveLength: true);
            c.Equal($"{name} 保持包长度", original.Length, updated.Length);
            var readback = XmpRatingReader.ReadPacket(updated, XmpRatingSource.Embedded);
            c.Equal($"{name} 写后语义回读", target, readback.Value);
        }

        var withTitle = XmpPacketEditor.SetRating(missing, 4, preserveLength: false);
        var meta = XmpMetaFactory.ParseFromBuffer(withTitle);
        c.Equal("新增包保留既有 dc:title", "照片",
            meta.GetLocalizedText("http://purl.org/dc/elements/1.1/", "title", null, "x-default")?.Value);
        c.Equal("新 sidecar 可创建 Rating", 4,
            XmpRatingReader.ReadPacket(withTitle, XmpRatingSource.Sidecar).Value);

        var tooSmall = Packet("<rdf:Description rdf:about=\"\"/>", padding: 0);
        c.ThrowsExact<XmpException>("空间不足不在原包内硬写",
            () => XmpPacketEditor.SetRating(tooSmall, 5, preserveLength: true));
        // Missing 与 0 的区分由状态承担，数值只用于旧调用方。
        c.Equal("Rejected 数值为 -1", -1,
            XmpRatingReader.ReadPacket(rejected, XmpRatingSource.Embedded).NumericValue);
        c.Equal("Missing 没有可比较数值", null,
            XmpRatingReader.ReadPacket(missing, XmpRatingSource.Embedded).ValueOrNull);

        var nullDirectory = XmpRatingReader.Read(null);
        c.Equal("XmpDirectory.XmpMeta 为 null 映射 Invalid", XmpRatingState.Invalid, nullDirectory.State);
        c.True("空 XMP 目录带明确错误", nullDirectory.Error != null);

        c.Equal("同名 sidecar 规则", "A7C01489.xmp", XmpRatingReader.SidecarName("A7C01489.ARW"));
    }

    private static byte[] Packet(string description, int padding = 4096)
    {
        var text = "<x:xmpmeta xmlns:x=\"adobe:ns:meta/\"><rdf:RDF xmlns:rdf=\"http://www.w3.org/1999/02/22-rdf-syntax-ns#\">"
            + description
            + "</rdf:RDF></x:xmpmeta>"
            + new string(' ', padding);
        return Encoding.UTF8.GetBytes(text);
    }
}
