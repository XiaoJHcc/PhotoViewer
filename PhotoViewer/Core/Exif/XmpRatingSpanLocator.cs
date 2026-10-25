using System.Globalization;
using System.Text;

namespace PhotoViewer.Core;

/// <summary>XMP Rating 在包内的书写形态。</summary>
public enum XmpRatingForm
{
    /// <summary>作为元素属性，例如 <c>xmp:Rating="3"</c>。</summary>
    Attribute,

    /// <summary>作为简单属性元素，例如 <c>&lt;xmp:Rating&gt;3&lt;/xmp:Rating&gt;</c>。</summary>
    Element,
}

/// <summary>
/// 结构化定位到的 Rating 值区间。值本身已去掉元素文本/属性值两侧的空白，
/// <see cref="MarkupStart"/> 是 Rating 所在标记的起点（属性名或元素开始标签），
/// <see cref="MarkupEnd"/> 是值之后必须原样保留的结束标记（属性为收尾引号，元素为结束标签）末尾偏移。
/// </summary>
public readonly record struct XmpRatingSpan(
    XmpRatingForm Form,
    int ValueOffset,
    int ValueLength,
    int MarkupStart,
    int MarkupEnd)
{
    public int ValueEnd => ValueOffset + ValueLength;
}

/// <summary>
/// 包内等长改写区间：从 <see cref="Offset"/> 起用 <see cref="Replacement"/> 覆盖等长的
/// <see cref="Original"/>。原字节一并携带，供预检/提交/回执做安全检查与最小范围恢复。
/// </summary>
public sealed record XmpRatingEdit(int Offset, byte[] Original, byte[] Replacement)
{
    public int Length => Replacement.Length;
}

/// <summary>
/// UTF-8 XMP 包中 <c>xap:Rating</c> 的字节区间定位与等长改写。
/// </summary>
/// <remarks>
/// <para>这是评分写入的正常路径：按 XML 结构扫描起止标签、属性、命名空间声明与注释，
/// 只按命名空间 URI（而不是前缀）识别 <c>xap:Rating</c>，不做全文搜索或正则启发式。</para>
/// <para>改写保持整包长度不变：值等长时只改值本身；值变短时在结束标记之后补空格；
/// 值变长时只消耗 Rating 标记附近可证明合法的局部空白（结束标记之后，或属性/元素之前的
/// 多余分隔空白），属性后面的唯一分隔空格会保留。局部空白不足即拒绝，绝不把写区间
/// 延伸到包尾 padding 或其它 XMP 内容。无法证明安全的结构（多个 Rating、CDATA、DOCTYPE/实体、
/// 无法解析的嵌套）一律返回失败原因，由预检报 <c>Unsupported</c>。</para>
/// <para>只接受 UTF-8（可带 BOM）。UTF-16 或非 XML 字节流明确失败。</para>
/// </remarks>
public static class XmpRatingSpanLocator
{
    private const string XmpNamespace = "http://ns.adobe.com/xap/1.0/";
    private const string RdfNamespace = "http://www.w3.org/1999/02/22-rdf-syntax-ns#";
    private const string MetaNamespace = "adobe:ns:meta/";
    private const string NoRatingReason = "XMP 中没有 xap:Rating";

    /// <summary>按结构定位唯一的 <c>xap:Rating</c> 值区间。</summary>
    /// <returns>找到返回 true；结构复杂或不存在返回 false 并给出原因。</returns>
    public static bool TryLocate(
        ReadOnlySpan<byte> packet,
        out XmpRatingSpan span,
        out string? reason) =>
        Scan(packet, out span, out reason);

    /// <summary>
    /// 生成把 Rating 改成 <paramref name="rating"/>（-1..5）的等长包与最小改写区间。
    /// </summary>
    /// <param name="packet">原始 XMP 包字节（UTF-8）。</param>
    /// <param name="rating">目标值：0..5，或 -1（拒绝）。</param>
    /// <param name="edit">需要原位覆盖的字节区间。</param>
    /// <param name="updated">改写后的整包字节，长度与 <paramref name="packet"/> 相同。</param>
    /// <param name="reason">失败原因。</param>
    public static bool TryCreateEdit(
        ReadOnlySpan<byte> packet,
        int rating,
        out XmpRatingEdit edit,
        out byte[]? updated,
        out string? reason)
    {
        edit = null!;
        updated = null;
        if (rating is < -1 or > 5)
        {
            reason = $"Rating 超出 -1..5：{rating}";
            return false;
        }

        if (!Scan(packet, out var span, out reason)) return false;

        var valueBytes = Encoding.ASCII.GetBytes(rating.ToString(CultureInfo.InvariantCulture));
        var markupEnd = span.MarkupEnd;
        if (markupEnd < span.ValueEnd || markupEnd > packet.Length)
        {
            reason = "Rating 结束标记偏移无效";
            return false;
        }

        if (valueBytes.Length == span.ValueLength)
        {
            edit = CreateEdit(packet, span.ValueOffset, valueBytes);
        }
        else if (valueBytes.Length < span.ValueLength)
        {
            var markupLength = markupEnd - span.ValueEnd;
            var padding = span.ValueLength - valueBytes.Length;
            var replacement = new byte[span.ValueLength + markupLength];
            valueBytes.CopyTo(replacement, 0);
            packet[span.ValueEnd..markupEnd].CopyTo(replacement.AsSpan(valueBytes.Length));
            replacement.AsSpan(replacement.Length - padding).Fill((byte)' ');
            if (!IsLegalPadding(replacement.AsSpan(replacement.Length - padding)))
            {
                reason = "内部错误：补齐字节不是合法空白";
                return false;
            }

            edit = CreateEdit(packet, span.ValueOffset, replacement);
        }
        else
        {
            var delta = valueBytes.Length - span.ValueLength;
            var markupLength = markupEnd - span.ValueEnd;
            var whitespace = CountWhitespace(packet, markupEnd);
            // 属性值后面的空白可能同时是下一个属性的唯一分隔；消耗后必须至少留下一个。
            var next = markupEnd + whitespace;
            var needsSeparator =
                span.Form == XmpRatingForm.Attribute &&
                next < packet.Length &&
                packet[next] != (byte)'>' &&
                packet[next] != (byte)'/';
            var available = whitespace - (needsSeparator ? 1 : 0);
            if (available >= delta)
            {
                var replacement = new byte[span.ValueLength + markupLength + delta];
                valueBytes.CopyTo(replacement, 0);
                packet[span.ValueEnd..markupEnd].CopyTo(replacement.AsSpan(valueBytes.Length));
                edit = CreateEdit(packet, span.ValueOffset, replacement);
            }
            else if (TryBuildLeadingWhitespaceGrow(packet, span, valueBytes, delta, markupEnd, out var leadingEdit))
            {
                edit = leadingEdit;
            }
            else
            {
                reason = $"Rating 值需要增加 {delta} 字节，但 Rating 附近没有可安全吸收的局部空白";
                return false;
            }
        }

        var candidate = packet.ToArray();
        edit.Replacement.CopyTo(candidate.AsSpan(edit.Offset));
        if (candidate.Length != packet.Length)
        {
            reason = "内部错误：改写后包长度变化";
            return false;
        }

        if (!Scan(candidate, out var verifySpan, out var verifyReason) ||
            !candidate.AsSpan(verifySpan.ValueOffset, verifySpan.ValueLength).SequenceEqual(valueBytes))
        {
            reason = "改写后的 Rating 结构回读不符：" + (verifyReason ?? "值不一致");
            return false;
        }

        try
        {
            var verify = XmpRatingReader.ReadPacket(candidate, XmpRatingSource.Embedded);
            var expectedState = rating == -1 ? XmpRatingState.Rejected : XmpRatingState.Rated;
            if (verify.State != expectedState || verify.Value != rating)
            {
                reason = $"改写后的 XMP 语义回读为 {verify.State}/{verify.Value?.ToString(CultureInfo.InvariantCulture) ?? "-"}";
                return false;
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            reason = "改写后的 XMP 无法解析：" + exception.Message;
            return false;
        }

        updated = candidate;
        return true;
    }

    /// <summary>生成改写后的整包字节（等长）；失败返回 false。</summary>
    public static bool TrySetValue(ReadOnlySpan<byte> packet, int rating, out byte[]? updated, out string? reason) =>
        TryCreateEdit(packet, rating, out _, out updated, out reason);

    /// <summary>
    /// 已有 canonical XMP 包但没有 <c>xap:Rating</c> 时，按结构插入一个 Rating，并保持整包长度：
    /// 优先在唯一的 <c>rdf:Description</c> 开始标签里加属性（复用已声明的 xap 前缀），
    /// 否则在 <c>rdf:RDF</c> 结束前插入自包含的 <c>rdf:Description</c>；
    /// 插入所需的字节由包内合法空白吸收。无法唯一确定位置或空白不足返回 false。
    /// </summary>
    public static bool TryCreateInsertion(
        ReadOnlySpan<byte> packet,
        int rating,
        out XmpRatingEdit edit,
        out byte[]? updated,
        out string? reason)
    {
        edit = null!;
        updated = null;
        if (rating is < -1 or > 5)
        {
            reason = $"Rating 超出 -1..5：{rating}";
            return false;
        }

        var anchors = new ScanAnchors();
        if (Scan(packet, out _, out var scanReason, anchors))
        {
            reason = "XMP 中已存在 xap:Rating，不能通过插入再写一个";
            return false;
        }

        if (scanReason != NoRatingReason)
        {
            reason = scanReason ?? "XMP 结构无法安全解析";
            return false;
        }

        var valueText = rating.ToString(CultureInfo.InvariantCulture);
        var valueBytes = Encoding.ASCII.GetBytes(valueText);
        string? attributeReason = null;

        var candidates = anchors.Descriptions.Where(item => item.XapPrefix != null).ToArray();
        if (candidates.Length == 1)
        {
            var tag = candidates[0];
            var insertAt = tag.TagEnd - (tag.SelfClosing ? 2 : 1);
            var attribute = Encoding.ASCII.GetBytes($" {tag.XapPrefix}:Rating=\"{valueText}\" ");
            var closeWhitespace = new WhitespaceRun(
                tag.CloseWhitespaceStart,
                tag.CloseWhitespaceStart + tag.CloseWhitespaceLength);
            if (TryApplyInsertion(
                    packet,
                    insertAt,
                    tag.TagEnd,
                    attribute,
                    closeWhitespace,
                    anchors,
                    out edit,
                    out attributeReason) &&
                VerifyInsertion(packet, edit, rating, valueBytes, out updated, out reason))
            {
                return true;
            }
        }

        if (anchors.RdfCount != 1 || anchors.RdfEndTagOffset < 0 || anchors.RdfSelfClosing)
        {
            reason = anchors.RdfCount switch
            {
                0 => "XMP 中没有可插入 Rating 的 rdf:RDF 容器",
                > 1 => "XMP 中有多个 rdf:RDF，无法唯一确定插入位置",
                _ => "rdf:RDF 没有可插入内容的结束标签",
            };
            return false;
        }

        var descriptionName = string.IsNullOrEmpty(anchors.RdfPrefix)
            ? "Description"
            : anchors.RdfPrefix + ":Description";
        var markup = anchors.RdfXapPrefix is { } prefix
            ? $"<{descriptionName} {prefix}:Rating=\"{valueText}\" />"
            : $"<{descriptionName} xmlns:xmp=\"{XmpNamespace}\" xmp:Rating=\"{valueText}\" />";
        var inserted = Encoding.ASCII.GetBytes(markup);
        if (!TryApplyInsertion(
                packet,
                anchors.RdfEndTagOffset,
                anchors.RdfEndTagEnd,
                inserted,
                default,
                anchors,
                out edit,
                out var containerReason))
        {
            reason = containerReason
                ?? attributeReason
                ?? "没有足够合法空白容纳插入内容";
            return false;
        }

        return VerifyInsertion(packet, edit, rating, valueBytes, out updated, out reason);
    }

    private static bool TryApplyInsertion(
        ReadOnlySpan<byte> packet,
        int insertAt,
        int afterStart,
        byte[] insertion,
        WhitespaceRun closeRun,
        ScanAnchors anchors,
        out XmpRatingEdit edit,
        out string? reason)
    {
        edit = null!;
        reason = null;
        if (insertAt < 0 || insertAt > packet.Length)
        {
            reason = "Rating 插入位置无效";
            return false;
        }

        var delta = insertion.Length;
        var runs = new List<WhitespaceRun>(3);

        var closeWhitespace = closeRun.End == insertAt && closeRun.Length >= delta
            ? closeRun
            : default;
        if (closeWhitespace.Length > 0) runs.Add(closeWhitespace);

        // 只允许紧邻插入点的局部空白：标签闭合后的文本空白，或 Rating 结束标签前的空白。
        var afterRun = anchors.Whitespace.FirstOrDefault(
            run => afterStart >= 0 && run.Start == afterStart && run.Length >= delta);
        if (afterRun.Length > 0) runs.Add(afterRun);

        var beforeRun = anchors.Whitespace.FirstOrDefault(
            run => run.End == insertAt && run.Length >= delta);
        if (beforeRun.Length > 0) runs.Add(beforeRun);

        if (runs.Count == 0)
        {
            reason = "Rating 插入点附近没有可安全吸收的局部空白";
            return false;
        }

        var chosen = runs[0];
        if (chosen.End <= insertAt)
        {
            var kept = chosen.Length - delta;
            var replacement = new byte[insertAt - chosen.Start];
            packet.Slice(chosen.Start, kept).CopyTo(replacement);
            var position = kept;
            packet[chosen.End..insertAt].CopyTo(replacement.AsSpan(position));
            position += insertAt - chosen.End;
            insertion.CopyTo(replacement.AsSpan(position));
            edit = CreateEdit(packet, chosen.Start, replacement);
            return true;
        }

        var after = new byte[chosen.End - insertAt];
        var offset = 0;
        insertion.CopyTo(after, offset);
        offset += insertion.Length;
        packet[insertAt..chosen.Start].CopyTo(after.AsSpan(offset));
        offset += chosen.Start - insertAt;
        packet[(chosen.Start + delta)..chosen.End].CopyTo(after.AsSpan(offset));
        edit = CreateEdit(packet, insertAt, after);
        return true;
    }

    private static bool VerifyInsertion(
        ReadOnlySpan<byte> packet,
        XmpRatingEdit edit,
        int rating,
        byte[] valueBytes,
        out byte[]? updated,
        out string? reason)
    {
        updated = null;
        var candidate = packet.ToArray();
        edit.Replacement.CopyTo(candidate.AsSpan(edit.Offset));
        if (candidate.Length != packet.Length)
        {
            reason = "内部错误：插入后包长度变化";
            return false;
        }

        if (!Scan(candidate, out var verifySpan, out var verifyReason) ||
            !candidate.AsSpan(verifySpan.ValueOffset, verifySpan.ValueLength).SequenceEqual(valueBytes))
        {
            reason = "插入后的 Rating 结构回读不符：" + (verifyReason ?? "值不一致");
            return false;
        }

        try
        {
            var verify = XmpRatingReader.ReadPacket(candidate, XmpRatingSource.Embedded);
            var expectedState = rating == -1 ? XmpRatingState.Rejected : XmpRatingState.Rated;
            if (verify.State != expectedState || verify.Value != rating)
            {
                reason = "插入后的 XMP 语义回读不符";
                return false;
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            reason = "插入后的 XMP 无法解析：" + exception.Message;
            return false;
        }

        updated = candidate;
        reason = null;
        return true;
    }

    /// <summary>
    /// 值变长时用 Rating 标记之前的合法空白吸收差值：整段标记左移，其它属性字节保持原样。
    /// 元素只有在空白紧跟在 <c>&gt;</c> 之后（标记间空白）时才使用；
    /// 属性必须保留至少一个空白作为与标签名/前一个属性的分隔。
    /// </summary>
    private static bool TryBuildLeadingWhitespaceGrow(
        ReadOnlySpan<byte> packet,
        XmpRatingSpan span,
        byte[] valueBytes,
        int delta,
        int markupEnd,
        out XmpRatingEdit edit)
    {
        edit = null!;
        var leadingStart = span.MarkupStart;
        while (leadingStart > 0 && IsWhitespace(packet[leadingStart - 1])) leadingStart--;
        var leadingCount = span.MarkupStart - leadingStart;
        if (leadingCount == 0) return false;

        if (span.Form == XmpRatingForm.Element &&
            (leadingStart == 0 || packet[leadingStart - 1] != (byte)'>'))
        {
            return false;
        }

        var consumable = span.Form == XmpRatingForm.Attribute ? leadingCount - 1 : leadingCount;
        if (consumable < delta) return false;

        var replacement = new byte[markupEnd - leadingStart];
        var position = 0;
        var keptWhitespace = leadingCount - delta;
        packet.Slice(leadingStart, keptWhitespace).CopyTo(replacement);
        position += keptWhitespace;
        packet[span.MarkupStart..span.ValueOffset].CopyTo(replacement.AsSpan(position));
        position += span.ValueOffset - span.MarkupStart;
        valueBytes.CopyTo(replacement.AsSpan(position));
        position += valueBytes.Length;
        packet[span.ValueEnd..markupEnd].CopyTo(replacement.AsSpan(position));
        edit = CreateEdit(packet, leadingStart, replacement);
        return true;
    }

    private static XmpRatingEdit CreateEdit(ReadOnlySpan<byte> packet, int offset, byte[] replacement)
    {
        if (offset < 0 || replacement.Length == 0 || offset + replacement.Length > packet.Length)
            throw new InvalidOperationException("Rating 改写区间超出包范围");
        return new XmpRatingEdit(offset, packet.Slice(offset, replacement.Length).ToArray(), replacement);
    }

    private static bool Scan(
        ReadOnlySpan<byte> packet,
        out XmpRatingSpan span,
        out string? reason,
        ScanAnchors? anchors = null)
    {
        span = default;
        reason = null;

        if (packet.Length == 0)
        {
            reason = "XMP 包为空";
            return false;
        }

        if (packet.Length >= 2 &&
            ((packet[0] == 0xFF && packet[1] == 0xFE) || (packet[0] == 0xFE && packet[1] == 0xFF)))
        {
            reason = "XMP 不是 UTF-8（检测到 UTF-16 字节序标记）";
            return false;
        }

        var position = packet.Length >= 3 && packet[0] == 0xEF && packet[1] == 0xBB && packet[2] == 0xBF
            ? 3
            : 0;

        var scopes = new List<Dictionary<string, string>>();
        var elements = new List<string>();
        var elementNamespaces = new List<string?>();
        var spans = new List<XmpRatingSpan>();

        string? pendingName = null;
        var pendingDepth = -1;
        var pendingMarkupStart = -1;
        var pendingValueStart = -1;
        var pendingValueEnd = -1;
        var pendingComplex = false;

        while (position < packet.Length)
        {
            if (packet[position] != (byte)'<')
            {
                var next = packet[position..].IndexOf((byte)'<');
                var textEnd = next < 0 ? packet.Length : position + next;
                if (pendingName != null && !HandleValueText(packet, position, textEnd, ref pendingValueStart, ref pendingValueEnd, ref pendingComplex))
                {
                    reason = "Rating 元素内容无法安全解析（包含实体引用或重复文本）";
                    return false;
                }

                if (anchors != null &&
                    pendingName == null &&
                    IsAllWhitespace(packet[position..textEnd]) &&
                    IsIgnorableWhitespaceContext(elementNamespaces))
                {
                    anchors.Whitespace.Add(new WhitespaceRun(position, textEnd));
                }

                position = textEnd;
                continue;
            }

            if (StartsWith(packet, position, "<!--"u8))
            {
                var end = packet[(position + 4)..].IndexOf("-->"u8);
                if (end < 0)
                {
                    reason = "XMP 注释未闭合";
                    return false;
                }

                if (pendingName != null) pendingComplex = true;
                position += 4 + end + 3;
                continue;
            }

            if (StartsWith(packet, position, "<?"u8))
            {
                var end = packet[(position + 2)..].IndexOf("?>"u8);
                if (end < 0)
                {
                    reason = "XMP 处理指令未闭合";
                    return false;
                }

                if (pendingName != null) pendingComplex = true;
                position += 2 + end + 2;
                continue;
            }

            if (StartsWith(packet, position, "<![CDATA["u8))
            {
                if (pendingName != null) pendingComplex = true;
                var end = packet[(position + 9)..].IndexOf("]]>"u8);
                if (end < 0)
                {
                    reason = "XMP CDATA 未闭合";
                    return false;
                }

                position += 9 + end + 3;
                continue;
            }

            if (StartsWith(packet, position, "<!"u8))
            {
                reason = "XMP 含 DOCTYPE/ENTITY 声明，拒绝改写";
                return false;
            }

            if (StartsWith(packet, position, "</"u8))
            {
                var nameStart = position + 2;
                var nameEnd = SkipName(packet, nameStart);
                if (nameEnd == nameStart)
                {
                    reason = "XMP 结束标签缺少名称";
                    return false;
                }

                var name = Encoding.UTF8.GetString(packet[nameStart..nameEnd]);
                var close = packet[nameEnd..].IndexOf((byte)'>');
                if (close < 0)
                {
                    reason = "XMP 结束标签未闭合";
                    return false;
                }

                var tagEnd = nameEnd + close + 1;
                if (elements.Count == 0 || !string.Equals(elements[^1], name, StringComparison.Ordinal))
                {
                    reason = "XMP 标签嵌套不匹配：" + name;
                    return false;
                }

                if (pendingName != null)
                {
                    if (string.Equals(name, pendingName, StringComparison.Ordinal) && elements.Count == pendingDepth)
                    {
                        if (pendingComplex || pendingValueStart < 0)
                        {
                            reason = "Rating 元素内容不是可安全改写的简单文本";
                            return false;
                        }

                        spans.Add(new XmpRatingSpan(
                            XmpRatingForm.Element,
                            pendingValueStart,
                            pendingValueEnd - pendingValueStart,
                            pendingMarkupStart,
                            tagEnd));
                        pendingName = null;
                        pendingDepth = -1;
                        pendingMarkupStart = -1;
                        pendingComplex = false;
                    }
                    else
                    {
                        pendingComplex = true;
                    }
                }

                elements.RemoveAt(elements.Count - 1);
                var closingNamespace = elementNamespaces.Count > 0 ? elementNamespaces[^1] : null;
                if (elementNamespaces.Count > 0) elementNamespaces.RemoveAt(elementNamespaces.Count - 1);
                scopes.RemoveAt(scopes.Count - 1);

                if (anchors != null &&
                    closingNamespace == RdfNamespace &&
                    LocalName(name) == "RDF")
                {
                    anchors.RdfEndTagOffset = position;
                    anchors.RdfEndTagEnd = tagEnd;
                }

                position = tagEnd;
                continue;
            }

            // 开始标签
            if (pendingName != null) pendingComplex = true;

            var cursor = position + 1;
            var startNameEnd = SkipName(packet, cursor);
            if (startNameEnd == cursor)
            {
                reason = "XMP 开始标签缺少名称";
                return false;
            }

            var elementName = Encoding.UTF8.GetString(packet[cursor..startNameEnd]);
            cursor = startNameEnd;

            var attributes = new List<AttributeRef>();
            var selfClosing = false;
            while (true)
            {
                cursor = SkipWhitespace(packet, cursor);
                if (cursor >= packet.Length)
                {
                    reason = "XMP 开始标签未闭合";
                    return false;
                }

                if (packet[cursor] == (byte)'/' && cursor + 1 < packet.Length && packet[cursor + 1] == (byte)'>')
                {
                    selfClosing = true;
                    cursor += 2;
                    break;
                }

                if (packet[cursor] == (byte)'>')
                {
                    cursor++;
                    break;
                }

                var attrNameStart = cursor;
                var attrNameEnd = SkipName(packet, cursor);
                if (attrNameEnd == attrNameStart)
                {
                    reason = "XMP 属性名无效";
                    return false;
                }

                cursor = SkipWhitespace(packet, attrNameEnd);
                if (cursor >= packet.Length || packet[cursor] != (byte)'=')
                {
                    reason = "XMP 属性缺少等号";
                    return false;
                }

                cursor = SkipWhitespace(packet, cursor + 1);
                if (cursor >= packet.Length || (packet[cursor] != (byte)'"' && packet[cursor] != (byte)'\''))
                {
                    reason = "XMP 属性值缺少引号";
                    return false;
                }

                var quote = packet[cursor];
                cursor++;
                var valueStart = cursor;
                while (cursor < packet.Length && packet[cursor] != quote)
                {
                    if (packet[cursor] == (byte)'<')
                    {
                        reason = "XMP 属性值包含非法字符";
                        return false;
                    }

                    cursor++;
                }

                if (cursor >= packet.Length)
                {
                    reason = "XMP 属性值未闭合";
                    return false;
                }

                attributes.Add(new AttributeRef(
                    Encoding.UTF8.GetString(packet[attrNameStart..attrNameEnd]),
                    attrNameStart,
                    valueStart,
                    cursor - valueStart));
                cursor++;
            }

            var scope = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var attribute in attributes)
            {
                if (attribute.Name == "xmlns")
                {
                    if (!TryReadNamespaceValue(packet, attribute, out var uri, out reason)) return false;
                    scope[""] = uri;
                }
                else if (attribute.Name.StartsWith("xmlns:", StringComparison.Ordinal))
                {
                    if (!TryReadNamespaceValue(packet, attribute, out var uri, out reason)) return false;
                    scope[attribute.Name[6..]] = uri;
                }
            }

            scopes.Add(scope);

            var elementResolved = TryResolveName(
                scopes,
                elementName,
                allowDefaultNamespace: true,
                out var elementLocal,
                out var elementUri);

            foreach (var attribute in attributes)
            {
                if (attribute.Name.StartsWith("xmlns", StringComparison.Ordinal)) continue;
                if (!TryResolveName(scopes, attribute.Name, allowDefaultNamespace: false, out var local, out var uri))
                    continue;
                if (local != "Rating" || uri != XmpNamespace) continue;

                var raw = packet.Slice(attribute.ValueStart, attribute.ValueLength);
                if (raw.IndexOf((byte)'&') >= 0)
                {
                    reason = "Rating 属性值包含实体引用，拒绝改写";
                    return false;
                }

                var (valueStart, valueLength) = Trim(raw, attribute.ValueStart);
                if (valueLength == 0)
                {
                    reason = "Rating 属性值为空";
                    return false;
                }

                spans.Add(new XmpRatingSpan(
                    XmpRatingForm.Attribute,
                    valueStart,
                    valueLength,
                    attribute.NameStart,
                    attribute.ValueStart + attribute.ValueLength + 1));
            }

            if (!selfClosing)
            {
                elements.Add(elementName);
                elementNamespaces.Add(elementUri);
            }

            if (anchors != null && elementResolved)
            {
                if (elementUri == RdfNamespace && elementLocal == "Description")
                {
                    var closeLength = selfClosing ? 2 : 1;
                    var contentEnd = cursor - closeLength;
                    var whitespaceStart = contentEnd;
                    while (whitespaceStart > position + 1 && IsWhitespace(packet[whitespaceStart - 1]))
                        whitespaceStart--;
                    anchors.Descriptions.Add(new DescriptionAnchor(
                        position,
                        cursor,
                        selfClosing,
                        FindXapPrefix(scopes),
                        whitespaceStart,
                        contentEnd - whitespaceStart));
                }
                else if (elementUri == RdfNamespace && elementLocal == "RDF")
                {
                    anchors.RdfCount++;
                    if (anchors.RdfCount == 1)
                    {
                        anchors.RdfPrefix = PrefixPart(elementName);
                        anchors.RdfXapPrefix = FindXapPrefix(scopes);
                        anchors.RdfSelfClosing = selfClosing;
                        anchors.RdfStartTagEnd = cursor;
                    }
                }
            }

            var isRatingElement = elementResolved &&
                elementUri == XmpNamespace &&
                elementLocal == "Rating";
            if (isRatingElement)
            {
                if (selfClosing)
                {
                    reason = "Rating 是空元素，无法安全写入值";
                    return false;
                }

                pendingName = elementName;
                pendingDepth = elements.Count;
                pendingMarkupStart = position;
                pendingValueStart = -1;
                pendingValueEnd = -1;
                pendingComplex = false;
            }

            if (selfClosing) scopes.RemoveAt(scopes.Count - 1);

            position = cursor;
        }

        if (pendingName != null)
        {
            reason = "Rating 元素未闭合";
            return false;
        }

        if (spans.Count == 0)
        {
            reason = NoRatingReason;
            return false;
        }

        if (spans.Count > 1)
        {
            reason = "XMP 中存在多个 xap:Rating，无法确定改写目标";
            return false;
        }

        span = spans[0];
        return true;
    }

    private static bool IsAllWhitespace(ReadOnlySpan<byte> value)
    {
        foreach (var item in value)
        {
            if (!IsWhitespace(item)) return false;
        }

        return true;
    }

    private static bool IsIgnorableWhitespaceContext(List<string?> namespaces)
    {
        if (namespaces.Count == 0) return true;
        var current = namespaces[^1];
        return current == RdfNamespace || current == MetaNamespace;
    }

    private static string? FindXapPrefix(List<Dictionary<string, string>> scopes)
    {
        string? fallback = null;
        for (var index = scopes.Count - 1; index >= 0; index--)
        {
            foreach (var pair in scopes[index])
            {
                if (pair.Value != XmpNamespace || pair.Key.Length == 0) continue;
                if (pair.Key == "xmp") return pair.Key;
                fallback ??= pair.Key;
            }
        }

        return fallback;
    }

    private static string LocalName(string qualifiedName)
    {
        var separator = qualifiedName.IndexOf(':');
        return separator < 0 ? qualifiedName : qualifiedName[(separator + 1)..];
    }

    private static string PrefixPart(string qualifiedName)
    {
        var separator = qualifiedName.IndexOf(':');
        return separator < 0 ? string.Empty : qualifiedName[..separator];
    }

    private static bool HandleValueText(
        ReadOnlySpan<byte> packet,
        int start,
        int end,
        ref int valueStart,
        ref int valueEnd,
        ref bool complex)
    {
        if (end <= start) return true;
        var text = packet[start..end];
        if (text.IndexOf((byte)'&') >= 0)
        {
            complex = true;
            return false;
        }

        var (trimmedStart, trimmedLength) = Trim(text, start);
        if (trimmedLength == 0) return true;
        if (valueStart >= 0)
        {
            complex = true;
            return false;
        }

        valueStart = trimmedStart;
        valueEnd = trimmedStart + trimmedLength;
        return true;
    }

    private static bool TryReadNamespaceValue(
        ReadOnlySpan<byte> packet,
        AttributeRef attribute,
        out string uri,
        out string? reason)
    {
        uri = string.Empty;
        reason = null;
        var raw = packet.Slice(attribute.ValueStart, attribute.ValueLength);
        if (raw.IndexOf((byte)'&') >= 0)
        {
            reason = "命名空间声明包含实体引用，无法解析";
            return false;
        }

        uri = Encoding.UTF8.GetString(raw);
        return true;
    }

    private static bool TryResolveName(
        List<Dictionary<string, string>> scopes,
        string qualifiedName,
        bool allowDefaultNamespace,
        out string localName,
        out string? uri)
    {
        localName = string.Empty;
        uri = null;
        var separator = qualifiedName.IndexOf(':');
        if (separator >= 0)
        {
            var prefix = qualifiedName[..separator];
            localName = qualifiedName[(separator + 1)..];
            for (var index = scopes.Count - 1; index >= 0; index--)
            {
                if (scopes[index].TryGetValue(prefix, out var found))
                {
                    uri = found;
                    return true;
                }
            }

            return false;
        }

        localName = qualifiedName;
        if (!allowDefaultNamespace) return false;
        for (var index = scopes.Count - 1; index >= 0; index--)
        {
            if (scopes[index].TryGetValue(string.Empty, out var found))
            {
                uri = found;
                return true;
            }
        }

        return false;
    }

    private static (int Start, int Length) Trim(ReadOnlySpan<byte> value, int absoluteStart)
    {
        var start = 0;
        var end = value.Length;
        while (start < end && IsWhitespace(value[start])) start++;
        while (end > start && IsWhitespace(value[end - 1])) end--;
        return (absoluteStart + start, end - start);
    }

    private static int SkipName(ReadOnlySpan<byte> packet, int position)
    {
        var start = position;
        while (position < packet.Length && IsNameByte(packet[position])) position++;
        return position == start ? start : position;
    }

    private static bool IsNameByte(byte value) =>
        value is >= (byte)'a' and <= (byte)'z'
            or >= (byte)'A' and <= (byte)'Z'
            or >= (byte)'0' and <= (byte)'9'
            or (byte)'_'
            or (byte)'-'
            or (byte)'.'
            or (byte)':';

    private static int SkipWhitespace(ReadOnlySpan<byte> packet, int position)
    {
        while (position < packet.Length && IsWhitespace(packet[position])) position++;
        return position;
    }

    private static int CountWhitespace(ReadOnlySpan<byte> packet, int position)
    {
        var start = position;
        while (position < packet.Length && IsWhitespace(packet[position])) position++;
        return position - start;
    }

    private static bool IsLegalPadding(ReadOnlySpan<byte> bytes)
    {
        foreach (var value in bytes)
        {
            if (!IsWhitespace(value)) return false;
        }

        return true;
    }

    private static bool IsWhitespace(byte value) =>
        value is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n';

    private static bool StartsWith(ReadOnlySpan<byte> packet, int position, ReadOnlySpan<byte> prefix) =>
        position + prefix.Length <= packet.Length && packet.Slice(position, prefix.Length).SequenceEqual(prefix);

    private readonly record struct AttributeRef(string Name, int NameStart, int ValueStart, int ValueLength);

    private readonly record struct WhitespaceRun(int Start, int End)
    {
        public int Length => End - Start;
    }

    private sealed record DescriptionAnchor(
        int TagStart,
        int TagEnd,
        bool SelfClosing,
        string? XapPrefix,
        int CloseWhitespaceStart,
        int CloseWhitespaceLength);

    private sealed class ScanAnchors
    {
        public List<DescriptionAnchor> Descriptions { get; } = new();

        public List<WhitespaceRun> Whitespace { get; } = new();

        public int RdfCount { get; set; }

        public string? RdfPrefix { get; set; }

        public string? RdfXapPrefix { get; set; }

        public int RdfStartTagEnd { get; set; } = -1;

        public int RdfEndTagOffset { get; set; } = -1;

        public int RdfEndTagEnd { get; set; } = -1;

        public bool RdfSelfClosing { get; set; }
    }
}
