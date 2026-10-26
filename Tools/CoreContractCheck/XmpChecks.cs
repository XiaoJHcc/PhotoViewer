using System.Text;
using PhotoViewer.Core;
using XmpCore;

namespace CoreContractCheck;

internal static class XmpChecks
{
    public static void Run(Checks c, string root)
    {
        c.Section("XMP Rating 语义");

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

        // 测试包生成只用 XmpCore：产品侧没有“整包重新序列化”的写入入口。
        var generated = GeneratePacket(missing, 4);
        c.Equal("生成的包可读回 Rating", 4,
            XmpRatingReader.ReadPacket(generated, XmpRatingSource.Sidecar).Value);
        var meta = XmpMetaFactory.ParseFromBuffer(generated);
        c.Equal("生成的包保留既有 dc:title", "照片",
            meta.GetLocalizedText("http://purl.org/dc/elements/1.1/", "title", null, "x-default")?.Value);

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

    /// <summary>测试夹具生成：用 XmpCore 重新序列化一个带 Rating 的包，仅用于构造输入。</summary>
    private static byte[] GeneratePacket(byte[] template, int rating)
    {
        var xmp = XmpMetaFactory.ParseFromBuffer(template);
        xmp.SetPropertyInteger(XmpRatingReader.XmpNamespace, "Rating", rating);
        return XmpMetaFactory.SerializeToBuffer(xmp, new XmpCore.Options.SerializeOptions { Padding = 2048 });
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
