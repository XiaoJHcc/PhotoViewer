using XmpCore;
using XmpCore.Options;

namespace PhotoViewer.Core;

public static class XmpPacketEditor
{
    public static byte[] SetRating(byte[]? templatePacket, int rating, bool preserveLength)
    {
        if (rating is < 0 or > 5) throw new ArgumentOutOfRangeException(nameof(rating));
        var xmp = templatePacket == null
            ? XmpMetaFactory.Create()
            : XmpMetaFactory.ParseFromBuffer(templatePacket);
        xmp.SetPropertyInteger(XmpRatingReader.XmpNamespace, "Rating", rating);
        var options = new SerializeOptions
        {
            ExactPacketLength = preserveLength,
            Padding = preserveLength ? templatePacket!.Length : 2048,
        };
        var packet = XmpMetaFactory.SerializeToBuffer(xmp, options);
        var verification = XmpRatingReader.ReadPacket(packet, XmpRatingSource.Embedded);
        if (verification.State != XmpRatingState.Rated || verification.Value != rating)
            throw new InvalidOperationException("XMP 序列化后的 Rating 校验失败");
        if (preserveLength && packet.Length != templatePacket!.Length)
            throw new InvalidOperationException("XMP 无法保持原包长度");
        return packet;
    }
}
