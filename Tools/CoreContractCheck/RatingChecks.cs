using System.Reactive.Concurrency;
using System.Text;
using PhotoViewer.Core;
using PhotoViewer.Core.AI;
using PhotoViewer.Core.Image;
using PhotoViewer.Core.Platform;
using PhotoViewer.Core.Storage;
using PhotoViewer.ViewModels.Main;
using PhotoViewer.ViewModels.Main.File;
using PhotoViewer.ViewModels.Settings;
using XmpCore;
using XmpCore.Options;

namespace CoreContractCheck;

/// <summary>评分来源、区间定位、整组写入与筛选语义的契约自检。</summary>
internal static class RatingChecks
{
    public static async Task RunAsync(Checks c, string root)
    {
        await CheckLocatorAsync(c);
        await CheckArwEmbeddedAsync(c, root);
        await CheckSourcesAndDedupeAsync(c, root);
        await CheckStaleIndexWriteAsync(c, root);
        await CheckSidecarStatesAsync(c, root);
        await CheckInsertionAsync(c, root);
        await CheckUnreadableSourcesAsync(c, root);
        await CheckNegativeRatingAsync(c, root);
        await CheckExternalChangeAsync(c, root);
        await CheckNoChangeAsync(c, root);
        await CheckPrecheckZeroModificationAsync(c, root);
        await CheckRollbackAsync(c, root);
        await CheckCommitFailureAsync(c, root);
        await CheckMultiXmpAsync(c, root);
        await CheckFolderIndexAsync(c, root);
        await CheckRefreshSequenceAsync(c, root);
        await CheckTrainingRatingAsync(c);
        await CheckFiltersAsync(c, root);
    }

    // ---------------------------------------------------------------- 定位器

    private static Task CheckLocatorAsync(Checks c)
    {
        c.Section("XMP Rating 结构化区间定位与等长改写");

        var attribute = Packet("<rdf:Description rdf:about=\"\" xmlns:xmp=\"http://ns.adobe.com/xap/1.0/\" xmp:Rating=\"3\"/>");
        c.True("定位属性式 Rating",
            XmpRatingSpanLocator.TryLocate(attribute, out var attributeSpan, out _)
            && attributeSpan.Form == XmpRatingForm.Attribute
            && attributeSpan.ValueLength == 1);
        c.True("属性式等长改写成功",
            XmpRatingSpanLocator.TryCreateEdit(attribute, 5, out var attributeEdit, out var attributeUpdated, out var attributeReason),
            attributeReason ?? "无原因");
        c.Equal("属性式改写保持包长度", attribute.Length, attributeUpdated!.Length);
        c.Equal("属性式只覆盖值区间", 1, attributeEdit.Length);
        c.Equal("属性式改写起点就是值起点", attributeSpan.ValueOffset, attributeEdit.Offset);
        c.Equal("属性式写后语义", 5, XmpRatingReader.ReadPacket(attributeUpdated, XmpRatingSource.Embedded).Value);

        var element = Packet("<rdf:Description rdf:about=\"\" xmlns:xmp=\"http://ns.adobe.com/xap/1.0/\"><xmp:Rating>4</xmp:Rating></rdf:Description>");
        c.True("定位元素式 Rating",
            XmpRatingSpanLocator.TryLocate(element, out var elementSpan, out _)
            && elementSpan.Form == XmpRatingForm.Element);
        c.True("元素式等长改写成功",
            XmpRatingSpanLocator.TryCreateEdit(element, 1, out _, out var elementUpdated, out var elementReason),
            elementReason ?? "无原因");
        c.Equal("元素式改写保持包长度", element.Length, elementUpdated!.Length);
        c.Equal("元素式写后语义", 1, XmpRatingReader.ReadPacket(elementUpdated, XmpRatingSource.Embedded).Value);

        var singleQuoted = Packet("<rdf:Description rdf:about='' xmlns:xmp='http://ns.adobe.com/xap/1.0/' xmp:Rating = '2'/>");
        c.True("单引号与空白属性可定位",
            XmpRatingSpanLocator.TryCreateEdit(singleQuoted, 0, out _, out var singleUpdated, out _));
        c.Equal("单引号属性写后语义", 0,
            XmpRatingReader.ReadPacket(singleUpdated!, XmpRatingSource.Embedded).Value);

        var customPrefix = Packet("<rdf:Description rdf:about=\"\" xmlns:foo=\"http://ns.adobe.com/xap/1.0/\" foo:Rating=\"5\"/>");
        c.True("按命名空间而不是前缀识别",
            XmpRatingSpanLocator.TryCreateEdit(customPrefix, 1, out _, out var prefixUpdated, out _));
        c.Equal("自定义前缀写后语义", 1,
            XmpRatingReader.ReadPacket(prefixUpdated!, XmpRatingSource.Embedded).Value);

        var defaultNamespace = Packet(
            "<rdf:Description rdf:about=\"\"><Rating xmlns=\"http://ns.adobe.com/xap/1.0/\">2</Rating></rdf:Description>");
        c.True("元素可使用默认命名空间",
            XmpRatingSpanLocator.TryCreateEdit(defaultNamespace, 3, out _, out var defaultUpdated, out _));
        c.Equal("默认命名空间写后语义", 3,
            XmpRatingReader.ReadPacket(defaultUpdated!, XmpRatingSource.Embedded).Value);

        var defaultNamespaceAttribute = Packet(
            "<rdf:Description rdf:about=\"\" xmlns=\"http://ns.adobe.com/xap/1.0/\" Rating=\"3\"/>");
        c.True("默认命名空间不作用于属性",
            !XmpRatingSpanLocator.TryLocate(defaultNamespaceAttribute, out _, out _));

        var otherNamespace = Packet("<rdf:Description rdf:about=\"\" xmlns:dc=\"http://purl.org/dc/elements/1.1/\" dc:Rating=\"3\"/>");
        c.True("其它命名空间的 Rating 不被当作 xap:Rating",
            !XmpRatingSpanLocator.TryLocate(otherNamespace, out _, out _));

        var otherNamespaceElement = Packet(
            "<rdf:Description rdf:about=\"\" xmlns:dc=\"http://purl.org/dc/elements/1.1/\"><dc:Rating>3</dc:Rating></rdf:Description>");
        c.True("元素式 Rating 必须属于 xap 命名空间",
            !XmpRatingSpanLocator.TryLocate(otherNamespaceElement, out _, out _));

        var noRating = Packet("<rdf:Description rdf:about=\"\" xmlns:dc=\"http://purl.org/dc/elements/1.1/\"><dc:title>照片</dc:title></rdf:Description>");
        c.True("没有 Rating 时定位失败",
            !XmpRatingSpanLocator.TryLocate(noRating, out _, out var missingReason)
            && missingReason != null);

        var duplicate = Packet(
            "<rdf:Description rdf:about=\"\" xmlns:xmp=\"http://ns.adobe.com/xap/1.0/\" xmp:Rating=\"1\">"
            + "<xmp:Rating>2</xmp:Rating></rdf:Description>");
        c.True("多个 Rating 拒绝改写",
            !XmpRatingSpanLocator.TryCreateEdit(duplicate, 3, out _, out _, out var duplicateReason)
            && duplicateReason != null);

        var cdata = Packet("<rdf:Description rdf:about=\"\" xmlns:xmp=\"http://ns.adobe.com/xap/1.0/\"><xmp:Rating><![CDATA[3]]></xmp:Rating></rdf:Description>");
        c.True("CDATA 值拒绝改写",
            !XmpRatingSpanLocator.TryCreateEdit(cdata, 4, out _, out _, out _));

        // 值变长：属性后仍有合法空白，可在局部吸收，包长不变。
        var grow = Packet("<rdf:Description rdf:about=\"\" xmlns:xmp=\"http://ns.adobe.com/xap/1.0/\" xmp:Rating=\"0\"   />", padding: 0);
        c.True("-1 变长改写成功",
            XmpRatingSpanLocator.TryCreateEdit(grow, -1, out var growEdit, out var growUpdated, out var growReason),
            growReason ?? "无原因");
        c.Equal("-1 改写保持包长度", grow.Length, growUpdated!.Length);
        c.True("-1 改写区间仍在值附近", growEdit.Offset <= growUpdated.Length - 3);
        var growSnapshot = XmpRatingReader.ReadPacket(growUpdated, XmpRatingSource.Embedded);
        c.Equal("-1 写后是 Rejected", XmpRatingState.Rejected, growSnapshot.State);
        c.Equal("-1 数值保持 -1", -1, growSnapshot.NumericValue);

        // 值变短：结束标记后补空白，属性值本身保持精确。
        var shrink = Packet("<rdf:Description rdf:about=\"\" xmlns:xmp=\"http://ns.adobe.com/xap/1.0/\" xmp:Rating=\"-1\" />", padding: 0);
        c.True("-1 缩短为 5 星成功",
            XmpRatingSpanLocator.TryCreateEdit(shrink, 5, out _, out var shrinkUpdated, out var shrinkReason),
            shrinkReason ?? "无原因");
        c.Equal("缩短后包长度不变", shrink.Length, shrinkUpdated!.Length);
        c.True("缩短后属性值精确为 5",
            XmpRatingSpanLocator.TryLocate(shrinkUpdated!, out var shrinkSpan, out _)
            && shrinkUpdated.AsSpan(shrinkSpan.ValueOffset, 1).SequenceEqual("5"u8));
        c.Equal("缩短后语义为 5",
            5, XmpRatingReader.ReadPacket(shrinkUpdated!, XmpRatingSource.Embedded).Value);

        // 元素式变长：结束标签后的空白被吸收。
        var elementGrow = Packet(
            "<rdf:Description rdf:about=\"\" xmlns:xmp=\"http://ns.adobe.com/xap/1.0/\">\n  "
            + "<xmp:Rating>0</xmp:Rating></rdf:Description>",
            padding: 0);
        c.True("元素式 -1 变长改写成功",
            XmpRatingSpanLocator.TryCreateEdit(elementGrow, -1, out var elementGrowEdit, out var elementGrowUpdated, out var elementGrowReason),
            elementGrowReason ?? "无原因");
        c.Equal("元素式变长保持包长度", elementGrow.Length, elementGrowUpdated!.Length);
        c.Equal("元素式变长写后是 Rejected", XmpRatingState.Rejected,
            XmpRatingReader.ReadPacket(elementGrowUpdated!, XmpRatingSource.Embedded).State);
        c.True("元素式变长只改局部区间（不吃包尾）",
            elementGrowEdit.Offset + elementGrowEdit.Length < elementGrow.Length);

        // 属性后没有空白：用属性前的可消费空白吸收，仍需保留至少一个分隔空格。
        var attributeGrow = Packet(
            "<rdf:Description rdf:about=\"\" xmlns:xmp=\"http://ns.adobe.com/xap/1.0/\"  xmp:Rating=\"0\"/>",
            padding: 0);
        c.True("属性式 -1 用前置空白变长成功",
            XmpRatingSpanLocator.TryCreateEdit(attributeGrow, -1, out var attributeGrowEdit, out var attributeGrowUpdated, out var attributeGrowReason),
            attributeGrowReason ?? "无原因");
        c.Equal("属性式前置空白改写保持包长度", attributeGrow.Length, attributeGrowUpdated!.Length);
        c.True("属性式前置空白改写只改局部区间",
            attributeGrowEdit.Offset + attributeGrowEdit.Length < attributeGrow.Length);
        c.Equal("属性式前置空白写后是 Rejected", XmpRatingState.Rejected,
            XmpRatingReader.ReadPacket(attributeGrowUpdated!, XmpRatingSource.Embedded).State);

        // 值后的单个空格同时是下一个属性的分隔：不能吃掉，应改用属性前的空白。
        var separator = Packet(
            "<rdf:Description rdf:about=\"\" xmlns:xmp=\"http://ns.adobe.com/xap/1.0/\"  xmp:Rating=\"0\" xmlns:dc=\"http://purl.org/dc/elements/1.1/\" dc:format=\"image/jpeg\"/>",
            padding: 0);
        c.True("相邻属性唯一分隔空格改用其它局部空白",
            XmpRatingSpanLocator.TryCreateEdit(separator, -1, out _, out var separatorUpdated, out var separatorReason),
            separatorReason ?? "无原因");
        c.True("改写后仍保留属性分隔",
            Encoding.UTF8.GetString(separatorUpdated!).Contains("\"-1\" xmlns:dc=", StringComparison.Ordinal));
        c.Equal("保留分隔后语义为 -1", XmpRatingState.Rejected,
            XmpRatingReader.ReadPacket(separatorUpdated!, XmpRatingSource.Embedded).State);

        var noLocalSlack = Packet(
            "<rdf:Description rdf:about=\"\" xmlns:xmp=\"http://ns.adobe.com/xap/1.0/\" xmlns:dc=\"http://purl.org/dc/elements/1.1/\" xmp:Rating=\"0\" dc:format=\"image/jpeg\"/>",
            padding: 0);
        c.True("没有多余局部空白时拒绝变长",
            !XmpRatingSpanLocator.TryCreateEdit(noLocalSlack, -1, out _, out _, out _));

        var remoteOnly = Packet(
            "<rdf:Description rdf:about=\"\" xmlns:xmp=\"http://ns.adobe.com/xap/1.0/\" xmp:Rating=\"0\"/>",
            padding: 4096);
        c.True("远端包尾 padding 不能用于变长",
            !XmpRatingSpanLocator.TryCreateEdit(remoteOnly, -1, out _, out _, out _));

        var invalidRating = Packet("<rdf:Description rdf:about=\"\" xmlns:xmp=\"http://ns.adobe.com/xap/1.0/\" xmp:Rating=\"9\"/>");
        c.True("非法值仍可被定位（读取语义另判 Invalid）",
            XmpRatingSpanLocator.TryLocate(invalidRating, out _, out _));

        return Task.CompletedTask;
    }

    // ---------------------------------------------------------------- ARW 内嵌

    private static async Task CheckArwEmbeddedAsync(Checks c, string root)
    {
        c.Section("ARW 内嵌写入、等长与不建 sidecar");

        var path = CopyFixture("TestCase/A7C01489.ARW", root);
        var original = await File.ReadAllBytesAsync(path);
        var file = new LocalFileRef(path);
        var store = XmpRatingStoreFactory.Create(file);

        var before = await store.ReadAsync();
        c.Equal("ARW 内嵌初始状态", XmpRatingState.Rated, before.State);
        c.Equal("ARW 内嵌初始值", 0, before.Value);

        var preparation = await store.PrepareAsync(4, safeMode: true);
        var afterPrepare = await File.ReadAllBytesAsync(path);
        c.True("ARW 预检不修改文件", original.AsSpan().SequenceEqual(afterPrepare));
        c.True("ARW 预检不是同值", !preparation.NoChange);

        var editStart = checked((int)(preparation.PacketOffset + preparation.EditOffset));
        c.Equal("ARW 最小改写区间长度", 1, preparation.EditReplacementBytes!.Length);
        c.Bytes("ARW 预检记录原区间字节",
            original.AsSpan(editStart, preparation.EditOriginalBytes!.Length).ToArray(),
            preparation.EditOriginalBytes);

        var receipt = await store.CommitAsync(preparation);
        var written = await File.ReadAllBytesAsync(path);
        c.Equal("ARW 内嵌写后值", 4, (await store.ReadAsync()).Value);
        var readBack = await RatingSourceReader.ReadEmbeddedAsync(file);
        c.Equal("ARW 写后 EXIF 读取路径同值", 4, readBack.Value);
        c.Equal("ARW 写后 EXIF 读取状态", XmpRatingState.Rated, readBack.State);
        c.Equal("ARW 写入保持文件长度", original.Length, written.Length);
        var changed = ChangedIndexes(original, written);
        c.True("ARW 实际写入只落在最小区间",
            changed.Count > 0 && changed.All(index =>
                index >= editStart && index < editStart + preparation.EditReplacementBytes.Length),
            "差异位置 " + string.Join(",", changed.Take(10)));
        c.Equal("ARW 回执记录同一写入区间", editStart, receipt.EditOffset);
        c.Bytes("ARW 回执记录写入字节", preparation.EditReplacementBytes, receipt.EditWrittenBytes!);

        var sidecarName = XmpRatingReader.SidecarName(Path.GetFileName(path));
        c.True("ARW 写入不创建 sidecar", !File.Exists(Path.Combine(root, sidecarName)));

        await store.RestoreAsync(receipt);
        var restoredBytes = await File.ReadAllBytesAsync(path);
        c.Bytes("ARW 补偿恢复原字节", original, restoredBytes);
        c.Equal("ARW 补偿后值回到 0", 0, (await store.ReadAsync()).Value);
        var restoreDiff = ChangedIndexes(written, restoredBytes);
        c.True("ARW 补偿也只写回同一区间",
            restoreDiff.Count > 0 && restoreDiff.All(index =>
                index >= editStart && index < editStart + preparation.EditReplacementBytes.Length),
            "差异位置 " + string.Join(",", restoreDiff.Take(10)));

        var restoredPacket = await new TiffXmpContainerAdapter().ReadCanonicalPacketAsync(file);
        byte[]? negativePacket = null;
        string? negativeReason = null;
        var negativeOk = restoredPacket is not null
            && XmpRatingSpanLocator.TryCreateEdit(
                restoredPacket.Data, -1, out _, out negativePacket, out negativeReason);
        c.True("ARW 真实包可用合法空白吸收 -1", negativeOk, negativeReason ?? "无原因");
        c.Equal("ARW -1 改写保持包长度", restoredPacket!.Data.Length, negativePacket!.Length);
        c.Equal("ARW -1 改写语义", XmpRatingState.Rejected,
            XmpRatingReader.ReadPacket(negativePacket, XmpRatingSource.Embedded).State);

        var heifPath = CopyFixture("release/native-p2-heif/color-srgb.heic", root, "embedded.heic");
        var heifOriginal = await File.ReadAllBytesAsync(heifPath);
        var heifStore = XmpRatingStoreFactory.Create(new LocalFileRef(heifPath));
        c.Equal("HEIF 内嵌初始值", 3, (await heifStore.ReadAsync()).Value);
        var heifReceipt = await heifStore.WriteAsync(1, safeMode: true);
        c.Equal("HEIF 内嵌写后值", 1, heifReceipt.After.Value);
        c.Equal("HEIF 内嵌写入保持文件长度", heifOriginal.Length, (await File.ReadAllBytesAsync(heifPath)).Length);
        c.True("HEIF 写入不创建 sidecar",
            !File.Exists(Path.Combine(root, XmpRatingReader.SidecarName("embedded.heic"))));
        await heifStore.RestoreAsync(heifReceipt);
        c.Bytes("HEIF 补偿恢复原字节", heifOriginal, await File.ReadAllBytesAsync(heifPath));
    }

    // ---------------------------------------------------------------- 来源与去重

    private static async Task CheckSourcesAndDedupeAsync(Checks c, string root)
    {
        c.Section("内嵌/sidecar 独立来源与组内去重");

        var jpgPath = CopyFixture("release/native-p2-fixtures/color-srgb.jpg", root, "pair.jpg");
        var arwPath = CopyFixture("TestCase/A7C01489.ARW", root, "pair.ARW");
        var sidecarPath = Path.Combine(root, "pair.xmp");
        await File.WriteAllBytesAsync(sidecarPath, Sidecar("5"));

        var jpg = new ImageFile(new LocalFileRef(jpgPath));
        var arw = new ImageFile(new LocalFileRef(arwPath));
        var rules = Rules(("jpg", "JPG", ".jpg"), ("raw", "RAW", ".arw"));
        var group = PhotoGroupMap.Build([jpg, arw], true, rules).GroupFor(jpg)!;

        var (embedded, sources) = await RatingSourceReader.ReadAsync(new LocalFileRef(jpgPath), null);
        c.Equal("JPG 内嵌不被 sidecar 遮蔽", 3, embedded.Value);
        c.Equal("JPG 有两个独立来源", 2, sources.Count);
        c.Equal("sidecar 来源值", 5,
            sources.Single(source => source.Source == XmpRatingSource.Sidecar).NumericValue);

        var index = new RatingSidecarIndex();
        c.True("sidecar 索引收录 .xmp", index.Add(new LocalFileRef(sidecarPath)));
        c.True("sidecar 索引忽略非 .xmp", !index.Add(new LocalFileRef(jpgPath)));
        c.Equal("sidecar 索引计数", 1, index.Count);
        var indexed = await RatingSourceReader.ReadAsync(new LocalFileRef(jpgPath), index);
        c.Equal("索引命中同一个 sidecar", 2, indexed.Sources.Count);
        var knownAbsent = await RatingSourceReader.ReadAsync(
            new LocalFileRef(jpgPath), RatingSidecarIndex.Empty);
        c.Equal("空索引表示目录确实没有 sidecar（不再查目录）", 1, knownAbsent.Sources.Count);
        c.True("共享空索引不可被写入", !RatingSidecarIndex.Empty.Add(new LocalFileRef(sidecarPath)));

        await jpg.LoadRatingOnlyAsync();
        await arw.LoadRatingOnlyAsync();
        c.Equal("ImageFile.Rating 取内嵌值", 3, jpg.Rating);
        c.Equal("sidecar 不改变 ImageFile.Rating", 3, jpg.Rating);
        c.Equal("组内识别评分冲突", true, group.HasRatingConflict);

        var result = await new RatingService().SetRatingAsync(group, 2, safeMode: true);
        c.Equal("整组写入成功", RatingWriteOutcome.Applied, result.Outcome);
        var sidecarOutcomes = result.Files
            .SelectMany(file => file.Sources)
            .Where(source => source.Source == XmpRatingSource.Sidecar)
            .ToArray();
        c.Equal("共享 sidecar 只出现一次", 1, sidecarOutcomes.Length);
        c.True("共享 sidecar 只写一次", sidecarOutcomes[0].Written);

        c.Equal("JPG 内嵌写后", 2,
            (await XmpRatingStoreFactory.Create(new LocalFileRef(jpgPath)).ReadAsync()).Value);
        c.Equal("ARW 内嵌写后", 2,
            (await XmpRatingStoreFactory.Create(new LocalFileRef(arwPath)).ReadAsync()).Value);
        c.Equal("sidecar 写后", 2,
            (await RatingSourceReader.ReadSidecarAsync(new LocalFileRef(sidecarPath))).NumericValue);

        foreach (var outcome in result.Files)
        {
            var target = new[] { jpg, arw }.First(item => item.File.StorageId == outcome.File.StorageId);
            target.ApplyRatingSources(outcome.Sources
                .Where(source => source.After is { })
                .Select(source => new RatingSourceState(source.Source, source.File, source.After!.Value))
                .ToArray());
        }

        c.True("组内三方一致后无冲突", !group.HasRatingConflict);

        var noSidecarPath = CopyFixture("TestCase/A7C01489.ARW", root, "solo.ARW");
        var solo = new ImageFile(new LocalFileRef(noSidecarPath));
        var soloGroup = PhotoGroupMap.Build([solo], false, rules).GroupFor(solo)!;
        var soloResult = await new RatingService().SetRatingAsync(soloGroup, 5, safeMode: true);
        c.Equal("单文件写入成功", RatingWriteOutcome.Applied, soloResult.Outcome);
        c.True("不存在 sidecar 时绝不创建",
            !File.Exists(Path.Combine(root, XmpRatingReader.SidecarName(Path.GetFileName(noSidecarPath)))));
        c.Equal("无 sidecar 的 ARW 写回内嵌", 5,
            (await XmpRatingStoreFactory.Create(new LocalFileRef(noSidecarPath)).ReadAsync()).Value);

        var noXmpPath = CopyFixture(
            "release/native-p2-acceptance/toolbar-rating-commands.png", root, "noxmp.png");
        var noXmp = await RatingSourceReader.ReadEmbeddedAsync(new LocalFileRef(noXmpPath));
        c.Equal("没有 XMP 的内嵌来源是 Missing", XmpRatingState.Missing, noXmp.State);
        c.Equal("Missing 不冒充 0 星数值", null, noXmp.ValueOrNull);

        var noCanonicalPath = Path.Combine(root, "no-canonical.jpg");
        var noCanonicalBytes = new byte[] { 0xFF, 0xD8, 0xFF, 0xD9 };
        await File.WriteAllBytesAsync(noCanonicalPath, noCanonicalBytes);
        await c.StorageFailsAsync(
            "没有 canonical XMP 的来源预检 Unsupported",
            StorageError.Unsupported,
            () => XmpRatingStoreFactory.Create(new LocalFileRef(noCanonicalPath)).PrepareAsync(3, true));
        c.Bytes("没有 canonical XMP 时零修改", noCanonicalBytes, await File.ReadAllBytesAsync(noCanonicalPath));

        await CheckPngUnsupportedAsync(c, root);
    }

    // ---------------------------------------------------------------- PNG 事务边界

    private static async Task CheckPngUnsupportedAsync(Checks c, string root)
    {
        c.Section("PNG 事务入口明确 Unsupported 且零修改");

        var withXmpPath = CopyFixture(
            "release/native-p2-acceptance/toolbar-rating-commands.png", root, "canonical-xmp.png");
        var withXmpFile = new LocalFileRef(withXmpPath);
        var canonicalBytes = Packet(
            "<rdf:Description rdf:about=\"\" xmlns:xmp=\"http://ns.adobe.com/xap/1.0/\" xmp:Rating=\"3\"/>");
        await File.WriteAllBytesAsync(withXmpPath, AddPngXmp(await File.ReadAllBytesAsync(withXmpPath), canonicalBytes));
        var canonicalPacket = await new PngXmpContainerAdapter().ReadCanonicalPacketAsync(withXmpFile);
        c.True("PNG 测试样本已有 canonical XMP", canonicalPacket is not null);
        var withXmpBytes = await File.ReadAllBytesAsync(withXmpPath);

        await c.StorageFailsAsync(
            "有 canonical XMP 的 PNG 事务入口仍 Unsupported",
            StorageError.Unsupported,
            () =>
            {
                _ = XmpRatingStoreFactory.Create(withXmpFile);
                return Task.CompletedTask;
            });
        c.Bytes("PNG 事务入口失败零修改", withXmpBytes, await File.ReadAllBytesAsync(withXmpPath));

        var image = new ImageFile(withXmpFile);
        var rules = Rules(("png", "PNG", ".png"));
        var group = PhotoGroupMap.Build([image], false, rules).GroupFor(image)!;
        var result = await new RatingService().SetRatingAsync(group, 2, safeMode: true);
        c.Equal("PNG 组写入预检失败", RatingWriteOutcome.RolledBack, result.Outcome);
        c.True("PNG 失败原因明确", result.FailureReason?.Contains("PNG", StringComparison.Ordinal) == true);
        c.Bytes("PNG 组写入零修改", withXmpBytes, await File.ReadAllBytesAsync(withXmpPath));
        c.True("PNG 不创建 sidecar",
            !File.Exists(Path.Combine(root, XmpRatingReader.SidecarName("canonical-xmp.png"))));
    }

    // ---------------------------------------------------------------- sidecar 异常状态

    private static async Task CheckSidecarStatesAsync(Checks c, string root)
    {
        c.Section("已有 sidecar 的空/损坏状态与预检");

        var jpgPath = CopyFixture("release/native-p2-fixtures/color-srgb.jpg", root, "side.jpg");
        var jpgOriginal = await File.ReadAllBytesAsync(jpgPath);
        var sidecarPath = Path.Combine(root, "side.xmp");
        var rules = Rules(("jpg", "JPG", ".jpg"));

        var image = new ImageFile(new LocalFileRef(jpgPath));
        var group = PhotoGroupMap.Build([image], false, rules).GroupFor(image)!;

        await File.WriteAllBytesAsync(sidecarPath, []);
        var empty = await RatingSourceReader.ReadSidecarAsync(new LocalFileRef(sidecarPath));
        c.Equal("空 sidecar 是 Missing", XmpRatingState.Missing, empty.State);
        c.True("空 sidecar 有明确错误", empty.Error != null);
        var emptyResult = await new RatingService().SetRatingAsync(group, 4, safeMode: true);
        c.Equal("空 sidecar 预检失败", RatingWriteOutcome.RolledBack, emptyResult.Outcome);
        c.Equal("空 sidecar 失败后仍为空", 0L, (long)new FileInfo(sidecarPath).Length);
        c.Bytes("空 sidecar 失败后照片不变", jpgOriginal, await File.ReadAllBytesAsync(jpgPath));

        var corrupt = Encoding.UTF8.GetBytes("<x:xmpmeta><rdf:RDF>");
        await File.WriteAllBytesAsync(sidecarPath, corrupt);
        var corruptState = await RatingSourceReader.ReadSidecarAsync(new LocalFileRef(sidecarPath));
        c.Equal("损坏 sidecar 是 Invalid", XmpRatingState.Invalid, corruptState.State);
        c.True("损坏 sidecar 有明确错误", corruptState.Error != null);
        var corruptSources = await RatingSourceReader.ReadAsync(new LocalFileRef(jpgPath), null);
        c.Equal("损坏 sidecar 不遮蔽内嵌", 3, corruptSources.Embedded.Value);
        c.Equal("损坏 sidecar 仍作为来源列出", 2, corruptSources.Sources.Count);
        var corruptResult = await new RatingService().SetRatingAsync(group, 4, safeMode: true);
        c.Equal("损坏 sidecar 预检失败", RatingWriteOutcome.RolledBack, corruptResult.Outcome);
        c.Bytes("损坏 sidecar 失败后字节不变", corrupt, await File.ReadAllBytesAsync(sidecarPath));
        c.Bytes("损坏 sidecar 失败后照片不变", jpgOriginal, await File.ReadAllBytesAsync(jpgPath));
    }

    // ---------------------------------------------------------------- 缺 Rating 的结构化插入

    private static async Task CheckInsertionAsync(Checks c, string root)
    {
        c.Section("已有 canonical XMP 缺 Rating 的结构化插入");

        var (arwPath, arwBefore, arwPacketOffset, arwPacketLength) = await CreateNoRatingCopyAsync(
            "TestCase/A7C01489.ARW", root, "insert.ARW", new TiffXmpContainerAdapter());
        var arwFile = new LocalFileRef(arwPath);
        var arwStore = XmpRatingStoreFactory.Create(arwFile);
        c.Equal("ARW 缺 Rating 读为 Missing", XmpRatingState.Missing, (await arwStore.ReadAsync()).State);
        var arwPacketBefore = await new TiffXmpContainerAdapter().ReadCanonicalPacketAsync(arwFile);

        var arwPreparation = await arwStore.PrepareAsync(5, safeMode: true);
        c.True("ARW 缺 Rating 走结构化插入", arwPreparation.InsertedRating);
        c.True("ARW 插入区间非空", arwPreparation.EditReplacementBytes is { Length: > 0 });
        var arwBeforeCommit = await File.ReadAllBytesAsync(arwPath);
        c.Bytes("ARW 插入预检不改文件", arwBefore, arwBeforeCommit);

        var arwReceipt = await arwStore.CommitAsync(arwPreparation);
        c.Equal("ARW 插入后 Rating", 5, arwReceipt.After.Value);
        var arwInserted = await File.ReadAllBytesAsync(arwPath);
        c.Equal("ARW 插入保持文件长度", arwBefore.Length, arwInserted.Length);
        c.True("ARW 插入不改 XMP 包外字节",
            OutsideRangeEquals(arwBefore, arwInserted, arwPacketOffset, arwPacketLength));
        var arwPacket = await new TiffXmpContainerAdapter().ReadCanonicalPacketAsync(arwFile);
        c.True("ARW 插入只改包内最小插入区间",
            OutsideRangeEquals(
                arwPacketBefore!.Data,
                arwPacket!.Data,
                arwPreparation.EditOffset,
                arwPreparation.EditReplacementBytes!.Length));
        var arwMeta = XmpMetaFactory.ParseFromBuffer(arwPacket!.Data);
        c.Equal("ARW 插入保留其它 XMP 属性",
            "urn:uuid:e342971a-5835-4d56-b3ff-f434089d2e00",
            arwMeta.GetProperty("http://ns.adobe.com/xap/1.0/mm/", "DocumentID")?.Value);

        foreach (var rating in new[] { 0, 1, 2, 3, 4, 5, -1 })
        {
            var roundTrip = await arwStore.WriteAsync(rating, safeMode: true);
            var expectedState = rating == -1 ? XmpRatingState.Rejected : XmpRatingState.Rated;
            c.Equal($"ARW 插入后往返 {rating} 状态", expectedState, roundTrip.After.State);
            c.Equal($"ARW 插入后往返 {rating} 值", rating, roundTrip.After.Value);
        }

        var (hifPath, hifBefore, hifPacketOffset, hifPacketLength) = await CreateNoRatingCopyAsync(
            "release/native-p2-heif/color-srgb.heic", root, "insert.hif", new HeifXmpContainerAdapter());
        var hifFile = new LocalFileRef(hifPath);
        var hifStore = XmpRatingStoreFactory.Create(hifFile);
        c.Equal("HEIF 缺 Rating 读为 Missing", XmpRatingState.Missing, (await hifStore.ReadAsync()).State);
        var hifPacketBefore = await new HeifXmpContainerAdapter().ReadCanonicalPacketAsync(hifFile);
        var hifPreparation = await hifStore.PrepareAsync(2, safeMode: true);
        c.True("HEIF 缺 Rating 走结构化插入", hifPreparation.InsertedRating);
        var hifReceipt = await hifStore.CommitAsync(hifPreparation);
        c.Equal("HEIF 插入后 Rating", 2, hifReceipt.After.Value);
        var hifInserted = await File.ReadAllBytesAsync(hifPath);
        c.Equal("HEIF 插入保持文件长度", hifBefore.Length, hifInserted.Length);
        c.True("HEIF 插入不改 XMP 包外字节",
            OutsideRangeEquals(hifBefore, hifInserted, hifPacketOffset, hifPacketLength));
        var hifPacketAfter = await new HeifXmpContainerAdapter().ReadCanonicalPacketAsync(hifFile);
        c.True("HEIF 插入只改包内最小插入区间",
            OutsideRangeEquals(
                hifPacketBefore!.Data,
                hifPacketAfter!.Data,
                hifPreparation.EditOffset,
                hifPreparation.EditReplacementBytes!.Length));
        c.Equal("HEIF 插入后 -1 往返", XmpRatingState.Rejected,
            (await hifStore.WriteAsync(-1, safeMode: true)).After.State);
        c.Equal("HEIF 插入后 0 往返", 0,
            (await hifStore.WriteAsync(0, safeMode: true)).After.Value);

        var sidecarPath = Path.Combine(root, "insert.xmp");
        var sidecarBytes = Packet(
            "<rdf:Description rdf:about=\"\" xmlns:xmp=\"http://ns.adobe.com/xap/1.0/\">\n"
            + new string(' ', 40)
            + "<dc:title xmlns:dc=\"http://purl.org/dc/elements/1.1/\">照片</dc:title></rdf:Description>");
        await File.WriteAllBytesAsync(sidecarPath, sidecarBytes);
        var sidecarStore = XmpRatingStoreFactory.CreateSidecar(new LocalFileRef(sidecarPath));
        c.Equal("sidecar 缺 Rating 读为 Missing", XmpRatingState.Missing, (await sidecarStore.ReadAsync()).State);
        var sidecarPreparation = await sidecarStore.PrepareAsync(4, safeMode: true);
        c.True("sidecar 缺 Rating 走结构化插入", sidecarPreparation.InsertedRating);
        var sidecarReceipt = await sidecarStore.CommitAsync(sidecarPreparation);
        c.Equal("sidecar 插入后 Rating", 4, sidecarReceipt.After.Value);
        c.Equal("sidecar 插入保持文件长度", sidecarBytes.Length, (long)new FileInfo(sidecarPath).Length);
        var sidecarMeta = XmpMetaFactory.ParseFromBuffer(await File.ReadAllBytesAsync(sidecarPath));
        c.Equal("sidecar 插入保留其它属性", "照片",
            sidecarMeta.GetLocalizedText("http://purl.org/dc/elements/1.1/", "title", null, "x-default")?.Value);
        c.Equal("sidecar 插入后 -1 往返", XmpRatingState.Rejected,
            (await sidecarStore.WriteAsync(-1, safeMode: true)).After.State);
        c.Equal("sidecar 插入后 5 往返", 5,
            (await sidecarStore.WriteAsync(5, safeMode: true)).After.Value);

        var bareSidecarPath = Path.Combine(root, "insert-bare.xmp");
        var bareSidecarBytes = Packet(
            "<rdf:Description rdf:about=\"\"><dc:title xmlns:dc=\"http://purl.org/dc/elements/1.1/\">x</dc:title></rdf:Description>\n"
            + new string(' ', 220));
        await File.WriteAllBytesAsync(bareSidecarPath, bareSidecarBytes);
        var bareStore = XmpRatingStoreFactory.CreateSidecar(new LocalFileRef(bareSidecarPath));
        var barePreparation = await bareStore.PrepareAsync(1, safeMode: true);
        c.True("无 xap 前缀的包改用自包含描述插入", barePreparation.InsertedRating);
        c.Equal("自包含描述插入后 Rating", 1, (await bareStore.CommitAsync(barePreparation)).After.Value);
        c.Equal("自包含描述插入保持文件长度", bareSidecarBytes.Length, (long)new FileInfo(bareSidecarPath).Length);

        // 没有任何可吸收空白：插入无法保持包长，预检必须失败且不写盘。
        var tightPath = Path.Combine(root, "tight.xmp");
        var tightBytes = Encoding.UTF8.GetBytes(
            "<x:xmpmeta xmlns:x=\"adobe:ns:meta/\"><rdf:RDF xmlns:rdf=\"http://www.w3.org/1999/02/22-rdf-syntax-ns#\">"
            + "<rdf:Description rdf:about=\"\" xmlns:xmp=\"http://ns.adobe.com/xap/1.0/\">"
            + "<dc:title xmlns:dc=\"http://purl.org/dc/elements/1.1/\">x</dc:title></rdf:Description></rdf:RDF></x:xmpmeta>");
        await File.WriteAllBytesAsync(tightPath, tightBytes);
        await c.StorageFailsAsync(
            "空白不足的插入预检 Unsupported",
            StorageError.Unsupported,
            () => XmpRatingStoreFactory.CreateSidecar(new LocalFileRef(tightPath)).PrepareAsync(3, true));
        c.Bytes("空白不足的插入预检不改文件", tightBytes, await File.ReadAllBytesAsync(tightPath));

        // 只有包尾 padding、没有紧邻插入点的局部空白：不能把整段其它 XMP 纳入写区间。
        var remotePath = Path.Combine(root, "remote-padding.xmp");
        var remoteBytes = Packet(
            "<rdf:Description rdf:about=\"\" xmlns:xmp=\"http://ns.adobe.com/xap/1.0/\"></rdf:Description>");
        await File.WriteAllBytesAsync(remotePath, remoteBytes);
        await c.StorageFailsAsync(
            "只有远端包尾 padding 时拒绝插入",
            StorageError.Unsupported,
            () => XmpRatingStoreFactory.CreateSidecar(new LocalFileRef(remotePath)).PrepareAsync(3, true));
        c.Bytes("远端 padding 插入失败零修改", remoteBytes, await File.ReadAllBytesAsync(remotePath));

        var remoteGrowPath = Path.Combine(root, "remote-grow.xmp");
        var remoteGrowBytes = Packet(
            "<rdf:Description rdf:about=\"\" xmlns:xmp=\"http://ns.adobe.com/xap/1.0/\" xmp:Rating=\"0\"/>");
        await File.WriteAllBytesAsync(remoteGrowPath, remoteGrowBytes);
        await c.StorageFailsAsync(
            "只有远端包尾 padding 时拒绝变长",
            StorageError.Unsupported,
            () => XmpRatingStoreFactory.CreateSidecar(new LocalFileRef(remoteGrowPath)).PrepareAsync(-1, true));
        c.Bytes("远端 padding 变长失败零修改", remoteGrowBytes, await File.ReadAllBytesAsync(remoteGrowPath));

        var duplicatePath = Path.Combine(root, "duplicate.xmp");
        var duplicateBytes = Packet(
            "<rdf:Description rdf:about=\"\" xmlns:xmp=\"http://ns.adobe.com/xap/1.0/\" xmp:Rating=\"1\">"
            + "<xmp:Rating>2</xmp:Rating></rdf:Description>");
        await File.WriteAllBytesAsync(duplicatePath, duplicateBytes);
        await c.StorageFailsAsync(
            "已有多个 Rating 时不再插入第二个",
            StorageError.Unsupported,
            () => XmpRatingStoreFactory.CreateSidecar(new LocalFileRef(duplicatePath)).PrepareAsync(3, true));
        c.Bytes("多 Rating 预检不改文件", duplicateBytes, await File.ReadAllBytesAsync(duplicatePath));

        var duplicateSamePath = Path.Combine(root, "duplicate-same.xmp");
        var duplicateSameBytes = Packet(
            "<rdf:Description rdf:about=\"\" xmlns:xmp=\"http://ns.adobe.com/xap/1.0/\" xmp:Rating=\"3\">"
            + "<xmp:Rating>3</xmp:Rating></rdf:Description>");
        await File.WriteAllBytesAsync(duplicateSamePath, duplicateSameBytes);
        await c.StorageFailsAsync(
            "重复同值 Rating 不可读时不算 NoChange",
            StorageError.Unsupported,
            () => XmpRatingStoreFactory.CreateSidecar(new LocalFileRef(duplicateSamePath)).PrepareAsync(3, true));
        c.Bytes("重复同值 Rating 文件不变", duplicateSameBytes, await File.ReadAllBytesAsync(duplicateSamePath));

        var invalidPath = Path.Combine(root, "invalid-value.xmp");
        await File.WriteAllBytesAsync(invalidPath, Sidecar("9"));
        var invalidStore = XmpRatingStoreFactory.CreateSidecar(new LocalFileRef(invalidPath));
        c.Equal("越界 Rating 读为 Invalid", XmpRatingState.Invalid, (await invalidStore.ReadAsync()).State);
        var invalidPreparation = await invalidStore.PrepareAsync(4, safeMode: true);
        c.True("无效的单一 Rating 允许被纠正", !invalidPreparation.NoChange && !invalidPreparation.InsertedRating);
        c.Equal("纠正非法 Rating 后的值", 4, (await invalidStore.CommitAsync(invalidPreparation)).After.Value);
    }

    // ---------------------------------------------------------------- 不可读来源整组失败

    private static async Task CheckUnreadableSourcesAsync(Checks c, string root)
    {
        c.Section("裸实体/重复同值来源整组不成功");

        var rules = Rules(("jpg", "JPG", ".jpg"));

        var brokenPath = CopyFixture("release/native-p2-fixtures/color-srgb.jpg", root, "broken-group.jpg");
        var brokenOriginal = await File.ReadAllBytesAsync(brokenPath);
        var brokenSidecarPath = Path.Combine(root, "broken-group.xmp");
        var brokenSidecarBytes = Packet(
            "<rdf:Description rdf:about=\"\" xmlns:xmp=\"http://ns.adobe.com/xap/1.0/\" xmp:Rating=\"3\">"
            + "<dc:title xmlns:dc=\"http://purl.org/dc/elements/1.1/\">a & b</dc:title>"
            + "</rdf:Description>");
        await File.WriteAllBytesAsync(brokenSidecarPath, brokenSidecarBytes);
        var brokenImage = new ImageFile(new LocalFileRef(brokenPath));
        var brokenGroup = PhotoGroupMap.Build([brokenImage], false, rules).GroupFor(brokenImage)!;
        var brokenResult = await new RatingService().SetRatingAsync(brokenGroup, 4, safeMode: true);
        c.Equal("裸实体来源整组失败", RatingWriteOutcome.RolledBack, brokenResult.Outcome);
        c.Bytes("裸实体失败不改照片", brokenOriginal, await File.ReadAllBytesAsync(brokenPath));
        c.Bytes("裸实体失败不改 sidecar", brokenSidecarBytes, await File.ReadAllBytesAsync(brokenSidecarPath));

        var duplicatePath = CopyFixture("release/native-p2-fixtures/color-srgb.jpg", root, "dup-group.jpg");
        var duplicateOriginal = await File.ReadAllBytesAsync(duplicatePath);
        var duplicateSidecarPath = Path.Combine(root, "dup-group.xmp");
        var duplicateSidecarBytes = Packet(
            "<rdf:Description rdf:about=\"\" xmlns:xmp=\"http://ns.adobe.com/xap/1.0/\" xmp:Rating=\"3\">"
            + "<xmp:Rating>3</xmp:Rating></rdf:Description>");
        await File.WriteAllBytesAsync(duplicateSidecarPath, duplicateSidecarBytes);
        var duplicateImage = new ImageFile(new LocalFileRef(duplicatePath));
        var duplicateGroup = PhotoGroupMap.Build([duplicateImage], false, rules).GroupFor(duplicateImage)!;
        var duplicateResult = await new RatingService().SetRatingAsync(duplicateGroup, 3, safeMode: true);
        c.Equal("重复同值来源整组不成功", RatingWriteOutcome.RolledBack, duplicateResult.Outcome);
        c.Bytes("重复同值失败不改照片", duplicateOriginal, await File.ReadAllBytesAsync(duplicatePath));
        c.Bytes("重复同值失败不改 sidecar", duplicateSidecarBytes, await File.ReadAllBytesAsync(duplicateSidecarPath));
    }

    // ---------------------------------------------------------------- -1 / Rejected 事务往返

    private static async Task CheckNegativeRatingAsync(Checks c, string root)
    {
        c.Section("核心 -1/Rejected 事务往返");

        var rules = Rules(("arw", "RAW", ".arw"), ("hif", "HEIF", ".hif"));

        var arwPath = CopyFixture("TestCase/A7C01489.ARW", root, "negative.ARW");
        var arw = new ImageFile(new LocalFileRef(arwPath));
        var arwGroup = PhotoGroupMap.Build([arw], false, rules).GroupFor(arw)!;
        var arwNegative = await new RatingService().SetRatingAsync(arwGroup, -1, safeMode: true);
        c.Equal("ARW 事务设置 -1 成功", RatingWriteOutcome.Applied, arwNegative.Outcome);
        var arwStore = XmpRatingStoreFactory.Create(new LocalFileRef(arwPath));
        c.Equal("ARW 事务 -1 回读状态", XmpRatingState.Rejected, (await arwStore.ReadAsync()).State);
        c.Equal("ARW 事务 -1 回读数值", -1, (await arwStore.ReadAsync()).NumericValue);
        var arwRepeated = await new RatingService().SetRatingAsync(arwGroup, -1, safeMode: true);
        c.Equal("ARW 重复 -1 返回 NoChange", RatingWriteOutcome.NoChange, arwRepeated.Outcome);
        var arwPositive = await new RatingService().SetRatingAsync(arwGroup, 4, safeMode: true);
        c.Equal("ARW 事务从 -1 恢复到 4", 4, arwPositive.Files
            .Single(file => file.File.StorageId == arw.File.StorageId)
            .EmbeddedAfter?.Value);

        var hifPath = CopyFixture("release/native-p2-heif/color-srgb.heic", root, "negative.hif");
        var hif = new ImageFile(new LocalFileRef(hifPath));
        var hifGroup = PhotoGroupMap.Build([hif], false, rules).GroupFor(hif)!;
        var hifNegative = await new RatingService().SetRatingAsync(hifGroup, -1, safeMode: true);
        c.Equal("HEIF 事务设置 -1 成功", RatingWriteOutcome.Applied, hifNegative.Outcome);
        c.Equal("HEIF 事务 -1 回读", XmpRatingState.Rejected,
            (await XmpRatingStoreFactory.Create(new LocalFileRef(hifPath)).ReadAsync()).State);
        c.Equal("HEIF 事务从 -1 恢复到 5", 5, (await new RatingService()
            .SetRatingAsync(hifGroup, 5, safeMode: true)).Files
            .Single(file => file.File.StorageId == hif.File.StorageId)
            .EmbeddedAfter?.Value);

        var pairPath = CopyFixture("TestCase/A7C01489.ARW", root, "negative-pair.ARW");
        var sidecarPath = Path.Combine(root, "negative-pair.xmp");
        var sidecarBytes = Sidecar("5");
        await File.WriteAllBytesAsync(sidecarPath, sidecarBytes);
        var pair = new ImageFile(new LocalFileRef(pairPath));
        var pairGroup = PhotoGroupMap.Build([pair], false, rules).GroupFor(pair)!;
        var pairNegative = await new RatingService().SetRatingAsync(pairGroup, -1, safeMode: true);
        c.Equal("ARW + sidecar 设置 -1 成功", RatingWriteOutcome.Applied, pairNegative.Outcome);
        c.Equal("ARW + sidecar 内嵌 -1", XmpRatingState.Rejected,
            (await XmpRatingStoreFactory.Create(new LocalFileRef(pairPath)).ReadAsync()).State);
        c.Equal("ARW + sidecar 的 sidecar -1", XmpRatingState.Rejected,
            (await RatingSourceReader.ReadSidecarAsync(new LocalFileRef(sidecarPath))).State);
        c.Equal("ARW + sidecar 只有一份 sidecar", true, File.Exists(sidecarPath));

        var pairPositive = await new RatingService().SetRatingAsync(pairGroup, 3, safeMode: true);
        c.Equal("ARW + sidecar 从 -1 恢复到 3", RatingWriteOutcome.Applied, pairPositive.Outcome);
        c.Equal("ARW + sidecar 内嵌恢复", 3,
            (await XmpRatingStoreFactory.Create(new LocalFileRef(pairPath)).ReadAsync()).Value);
        c.Equal("ARW + sidecar 的 sidecar 恢复", 3,
            (await RatingSourceReader.ReadSidecarAsync(new LocalFileRef(sidecarPath))).NumericValue);
    }

    // ---------------------------------------------------------------- 安全模式与外部修改

    private static async Task CheckExternalChangeAsync(Checks c, string root)
    {
        c.Section("安全模式与 revision 的外部修改比对");

        var path = CopyFixture("release/native-p2-fixtures/color-srgb.jpg", root, "external.jpg");
        var file = new LocalFileRef(path);

        var safeStore = XmpRatingStoreFactory.Create(file);
        var safePreparation = await safeStore.PrepareAsync(4, safeMode: true);
        await XmpRatingStoreFactory.Create(file).WriteAsync(2, safeMode: true);

        await c.StorageFailsAsync(
            "安全模式：预检后外部修改阻止覆盖",
            StorageError.ChangedExternally,
            () => safeStore.CommitAsync(safePreparation));
        c.Equal("安全模式失败后保留外部值", 2,
            (await XmpRatingStoreFactory.Create(file).ReadAsync()).Value);

        var looseStore = XmpRatingStoreFactory.Create(file);
        var loosePreparation = await looseStore.PrepareAsync(4, safeMode: false);
        await XmpRatingStoreFactory.Create(file).WriteAsync(1, safeMode: true);
        await c.StorageFailsAsync(
            "安全模式关闭仍按 revision 拒绝外部修改",
            StorageError.ChangedExternally,
            () => looseStore.CommitAsync(loosePreparation));
        c.Equal("安全模式关闭失败后保留外部值", 1,
            (await XmpRatingStoreFactory.Create(file).ReadAsync()).Value);

        // 仅 mtime 变化（内容不变）也必须被 revision 拦截。
        var mtimePath = CopyFixture("release/native-p2-fixtures/color-srgb.jpg", root, "mtime.jpg");
        var mtimeFile = new LocalFileRef(mtimePath);
        var mtimeStore = XmpRatingStoreFactory.Create(mtimeFile);
        var mtimePreparation = await mtimeStore.PrepareAsync(4, safeMode: true);
        var touched = new DateTimeOffset(File.GetLastWriteTimeUtc(mtimePath), TimeSpan.Zero).AddSeconds(2);
        File.SetLastWriteTimeUtc(mtimePath, touched.UtcDateTime);
        await c.StorageFailsAsync(
            "外部仅修改 mtime 也阻止覆盖",
            StorageError.ChangedExternally,
            () => mtimeStore.CommitAsync(mtimePreparation));
        c.Equal("mtime 拦截后内容保持原值", 3,
            (await XmpRatingStoreFactory.Create(mtimeFile).ReadAsync()).Value);

        var sizePath = CopyFixture("release/native-p2-fixtures/color-srgb.jpg", root, "size.jpg");
        var sizeFile = new LocalFileRef(sizePath);
        var sizeStore = XmpRatingStoreFactory.Create(sizeFile);
        var sizePreparation = await sizeStore.PrepareAsync(4, safeMode: false);
        using (var append = new FileStream(sizePath, FileMode.Append, FileAccess.Write))
        {
            append.WriteByte(0);
        }

        await c.StorageFailsAsync(
            "外部修改文件大小也阻止覆盖",
            StorageError.ChangedExternally,
            () => sizeStore.CommitAsync(sizePreparation));
        c.Equal("size 拦截后仍可读取原评分", 3,
            (await XmpRatingStoreFactory.Create(sizeFile).ReadAsync()).Value);

        // revision 完全不变（size/mtime 相同）但包内其它位置被外部修改：安全模式必须逐字节发现。
        var packetPath = CopyFixture("release/native-p2-fixtures/color-srgb.jpg", root, "packet-change.jpg");
        var packetFile = new LocalFileRef(packetPath);
        var packetStore = XmpRatingStoreFactory.Create(packetFile);
        var packetPreparation = await packetStore.PrepareAsync(4, safeMode: true);
        var packet = await new JpegXmpContainerAdapter().ReadCanonicalPacketAsync(packetFile)
            ?? throw new InvalidOperationException("JPEG 夹具缺少 canonical XMP");
        var toolkit = packet.Data.AsSpan().IndexOf("XMP Core"u8);
        if (toolkit < 0) throw new InvalidOperationException("夹具 XMP 缺少 XMPToolkit 文本");
        var packetBytes = await File.ReadAllBytesAsync(packetPath);
        var patchOffset = checked((int)packet.Offset + toolkit);
        packetBytes[patchOffset] ^= 0x01;
        await File.WriteAllBytesAsync(packetPath, packetBytes);
        File.SetLastWriteTimeUtc(
            packetPath,
            packetPreparation.Revision!.Value.LastModified!.Value.UtcDateTime);
        await c.StorageFailsAsync(
            "安全模式检测包内其它位置的外部修改",
            StorageError.ChangedExternally,
            () => packetStore.CommitAsync(packetPreparation));
        c.Equal("包内其它位置修改后评分仍原值", 3,
            (await XmpRatingStoreFactory.Create(packetFile).ReadAsync()).Value);

        // 安全模式关闭、revision 未变时仍按预检结果提交并做写后校验。
        var quietPath = CopyFixture("release/native-p2-fixtures/color-srgb.jpg", root, "quiet.jpg");
        var quietStore = XmpRatingStoreFactory.Create(new LocalFileRef(quietPath));
        var quietPreparation = await quietStore.PrepareAsync(1, safeMode: false);
        var quietReceipt = await quietStore.CommitAsync(quietPreparation);
        c.Equal("安全模式关闭按预检值提交", 1, quietReceipt.After.Value);
        c.Equal("安全模式关闭仍做写后校验", 1,
            (await XmpRatingStoreFactory.Create(new LocalFileRef(quietPath)).ReadAsync()).Value);
    }

    // ---------------------------------------------------------------- 同值

    private static async Task CheckNoChangeAsync(Checks c, string root)
    {
        c.Section("同值 NoChange");

        var path = CopyFixture("release/native-p2-fixtures/color-srgb.jpg", root, "same.jpg");
        var original = await File.ReadAllBytesAsync(path);
        var image = new ImageFile(new LocalFileRef(path));
        var rules = Rules(("jpg", "JPG", ".jpg"));
        var group = PhotoGroupMap.Build([image], false, rules).GroupFor(image)!;

        var result = await new RatingService().SetRatingAsync(group, 3, safeMode: true);
        c.Equal("同值返回 NoChange", RatingWriteOutcome.NoChange, result.Outcome);
        c.True("同值按成功处理", result.IsApplied);
        c.True("同值没有物理修改", !result.AnyFileModified);
        c.Bytes("同值文件字节不变", original, await File.ReadAllBytesAsync(path));

        var store = XmpRatingStoreFactory.Create(image.File);
        var preparation = await store.PrepareAsync(3, safeMode: true);
        c.True("同值预检标记 NoChange", preparation.NoChange);
        var receipt = await store.CommitAsync(preparation);
        c.True("同值回执不标记物理修改", receipt.NoChange);
    }

    // ---------------------------------------------------------------- 预检

    private static async Task CheckPrecheckZeroModificationAsync(Checks c, string root)
    {
        c.Section("预检失败零修改");

        var jpgPath = CopyFixture("release/native-p2-fixtures/color-srgb.jpg", root, "precheck.jpg");
        var tiffPath = Path.Combine(root, "precheck.tif");
        var tiffBytes = Encoding.ASCII.GetBytes("not-a-tiff");
        await File.WriteAllBytesAsync(tiffPath, tiffBytes);
        var jpgOriginal = await File.ReadAllBytesAsync(jpgPath);

        var jpg = new ImageFile(new LocalFileRef(jpgPath));
        var tiff = new ImageFile(new LocalFileRef(tiffPath));
        var rules = Rules(("jpg", "JPG", ".jpg"), ("tiff", "TIFF", ".tif"));
        var group = PhotoGroupMap.Build([jpg, tiff], true, rules).GroupFor(jpg)!;

        var result = await new RatingService().SetRatingAsync(group, 4, safeMode: true);
        c.Equal("预检不支持时整组失败", RatingWriteOutcome.RolledBack, result.Outcome);
        c.True("失败原因指明预检", result.FailureReason?.Contains("预检", StringComparison.Ordinal) == true);
        c.True("预检失败不产生物理修改", !result.AnyFileModified);
        c.Bytes("预检失败时 JPG 未改", jpgOriginal, await File.ReadAllBytesAsync(jpgPath));
        c.Bytes("预检失败时伴侣未改", tiffBytes, await File.ReadAllBytesAsync(tiffPath));
        c.True("预检失败不创建 sidecar",
            !File.Exists(Path.Combine(root, XmpRatingReader.SidecarName("precheck.jpg"))));
        c.NoTempArtifacts("预检失败无临时文件", root);
    }

    // ---------------------------------------------------------------- 回滚

    private static async Task CheckRollbackAsync(Checks c, string root)
    {
        c.Section("写入失败逆序回滚与 Partial 真值");

        var jpgPath = CopyFixture("release/native-p2-fixtures/color-srgb.jpg", root, "roll.jpg");
        var heifPath = CopyFixture("release/native-p2-heif/color-srgb.heic", root, "roll.hif");
        var jpgOriginal = await File.ReadAllBytesAsync(jpgPath);
        var heifOriginal = await File.ReadAllBytesAsync(heifPath);
        var rules = Rules(("jpg", "JPG", ".jpg"), ("heif", "HEIF", ".hif"));

        var jpg = new ImageFile(new LocalFileRef(jpgPath));
        var heif = new ImageFile(new LocalFileRef(heifPath));
        var group = PhotoGroupMap.Build([jpg, heif], true, rules).GroupFor(jpg)!;

        var rollback = new RatingService(
            file => new FaultStore(
                XmpRatingStoreFactory.Create(file),
                failCommit: IsHeif(file.Name),
                failRestore: false),
            XmpRatingStoreFactory.CreateSidecar);
        var rolled = await rollback.SetRatingAsync(group, 4, safeMode: true);
        c.Equal("伴侣提交失败返回 RolledBack", RatingWriteOutcome.RolledBack, rolled.Outcome);
        c.Bytes("回滚后代表文件恢复原字节", jpgOriginal, await File.ReadAllBytesAsync(jpgPath));
        c.Bytes("回滚后伴侣文件保持原字节", heifOriginal, await File.ReadAllBytesAsync(heifPath));
        c.Equal("回滚后值回到写入前", 3,
            (await XmpRatingStoreFactory.Create(new LocalFileRef(jpgPath)).ReadAsync()).Value);

        var partial = new RatingService(
            file => new FaultStore(
                XmpRatingStoreFactory.Create(file),
                failCommit: IsHeif(file.Name),
                failRestore: !IsHeif(file.Name)),
            XmpRatingStoreFactory.CreateSidecar);
        var partially = await partial.SetRatingAsync(group, 5, safeMode: true);
        c.Equal("补偿失败返回 Partial", RatingWriteOutcome.Partial, partially.Outcome);
        var primary = partially.Files.Single(file => file.File.StorageId == jpg.File.StorageId);
        c.Equal("Partial 主来源按真实写入值读取", 5, primary.EmbeddedAfter?.Value);
        c.True("Partial 明示补偿错误",
            partially.Files.SelectMany(file => file.Sources).Any(source => source.CompensationError != null));
        c.Equal("Partial 后磁盘真值可读", 5,
            (await XmpRatingStoreFactory.Create(new LocalFileRef(jpgPath)).ReadAsync()).Value);
        c.NoTempArtifacts("回滚路径无临时文件", root);

        // 安全模式关闭：停止后续写入，已写来源保持磁盘真值，不假装整组一致。
        var noSafeJpgPath = CopyFixture("release/native-p2-fixtures/color-srgb.jpg", root, "nosafe.jpg");
        var noSafeHeifPath = CopyFixture("release/native-p2-heif/color-srgb.heic", root, "nosafe.hif");
        var noSafeHeifOriginal = await File.ReadAllBytesAsync(noSafeHeifPath);
        var noSafeJpg = new ImageFile(new LocalFileRef(noSafeJpgPath));
        var noSafeHeif = new ImageFile(new LocalFileRef(noSafeHeifPath));
        var noSafeGroup = PhotoGroupMap.Build([noSafeJpg, noSafeHeif], true, rules).GroupFor(noSafeJpg)!;
        var noSafe = new RatingService(
            file => new FaultStore(
                XmpRatingStoreFactory.Create(file),
                failCommit: IsHeif(file.Name),
                failRestore: false),
            XmpRatingStoreFactory.CreateSidecar);
        var noSafeResult = await noSafe.SetRatingAsync(noSafeGroup, 2, safeMode: false);
        c.Equal("安全模式关闭时不回滚已写来源", RatingWriteOutcome.Partial, noSafeResult.Outcome);
        c.Equal("安全模式关闭时已写来源保持新值", 2,
            (await XmpRatingStoreFactory.Create(new LocalFileRef(noSafeJpgPath)).ReadAsync()).Value);
        c.Bytes("安全模式关闭时失败来源保持原字节", noSafeHeifOriginal, await File.ReadAllBytesAsync(noSafeHeifPath));
        c.True("安全模式关闭时结果标明整组可能不一致",
            noSafeResult.FailureReason?.Contains("安全模式关闭", StringComparison.Ordinal) == true);
    }

    // ---------------------------------------------------------------- 多 XMP / extended

    private static async Task CheckMultiXmpAsync(Checks c, string root)
    {
        c.Section("多份 XMP / extended 写入预检");

        var sourcePath = CopyFixture("release/native-p2-fixtures/color-srgb.jpg", root, "multi-source.jpg");
        var sourceBytes = await File.ReadAllBytesAsync(sourcePath);
        var segment = FindCanonicalXmpSegment(sourceBytes);

        var doubleBytes = new byte[sourceBytes.Length + segment.Length];
        sourceBytes.AsSpan(0, segment.Offset + segment.Length).CopyTo(doubleBytes);
        sourceBytes.AsSpan(segment.Offset, segment.Length)
            .CopyTo(doubleBytes.AsSpan(segment.Offset + segment.Length));
        sourceBytes.AsSpan(segment.Offset + segment.Length)
            .CopyTo(doubleBytes.AsSpan(segment.Offset + 2 * segment.Length));

        var doublePath = Path.Combine(root, "multi-xmp.jpg");
        await File.WriteAllBytesAsync(doublePath, doubleBytes);
        var doubleFile = new LocalFileRef(doublePath);
        var doubleProbe = await XmpRatingReader.ProbeEmbeddedAsync(doubleFile);
        c.True("多份 canonical XMP 被探测到", doubleProbe.XmpDirectoryCount > 1);
        var doubleImage = new ImageFile(doubleFile);
        var doubleGroup = PhotoGroupMap.Build([doubleImage], false, Rules(("jpg", "JPG", ".jpg")))
            .GroupFor(doubleImage)!;
        var doubleResult = await new RatingService().SetRatingAsync(doubleGroup, 2, safeMode: true);
        c.Equal("多份 XMP 写入预检失败", RatingWriteOutcome.RolledBack, doubleResult.Outcome);
        c.True("多份 XMP 失败原因明确",
            doubleResult.FailureReason?.Contains("XMP", StringComparison.Ordinal) == true);
        c.Bytes("多份 XMP 预检零修改", doubleBytes, await File.ReadAllBytesAsync(doublePath));

        var extendedBytes = BuildExtendedXmpJpeg(sourceBytes, segment);
        var extendedPath = Path.Combine(root, "extended-xmp.jpg");
        await File.WriteAllBytesAsync(extendedPath, extendedBytes);
        var extendedFile = new LocalFileRef(extendedPath);
        var extendedProbe = await XmpRatingReader.ProbeEmbeddedAsync(extendedFile);
        c.True("canonical 包声明 extended XMP 被探测到", extendedProbe.HasExtendedXmp);
        var extendedImage = new ImageFile(extendedFile);
        var extendedGroup = PhotoGroupMap.Build([extendedImage], false, Rules(("jpg", "JPG", ".jpg")))
            .GroupFor(extendedImage)!;
        var extendedResult = await new RatingService().SetRatingAsync(extendedGroup, 2, safeMode: true);
        c.Equal("extended XMP 写入预检失败", RatingWriteOutcome.RolledBack, extendedResult.Outcome);
        c.True("extended XMP 失败原因明确",
            extendedResult.FailureReason?.Contains("extended", StringComparison.OrdinalIgnoreCase) == true);
        c.Bytes("extended XMP 预检零修改", extendedBytes, await File.ReadAllBytesAsync(extendedPath));
    }

    // ---------------------------------------------------------------- 陈旧索引写入

    private static async Task CheckStaleIndexWriteAsync(Checks c, string root)
    {
        c.Section("写入不使用陈旧 sidecar 索引");

        var rules = Rules(("jpg", "JPG", ".jpg"), ("raw", "RAW", ".arw"));
        var staleIndex = new RatingSidecarIndex();
        staleIndex.Add(new LocalFileRef(Path.Combine(root, "unrelated.xmp")));

        var jpgPath = CopyFixture("release/native-p2-fixtures/color-srgb.jpg", root, "stale-write.jpg");
        var arwPath = CopyFixture("TestCase/A7C01489.ARW", root, "stale-write.ARW");
        var sidecarPath = Path.Combine(root, "stale-write.xmp");
        var sidecarBytes = Sidecar("5");
        await File.WriteAllBytesAsync(sidecarPath, sidecarBytes);

        var jpg = new ImageFile(new LocalFileRef(jpgPath))
        {
            RatingSidecarIndex = staleIndex,
        };
        var singleGroup = PhotoGroupMap.Build([jpg], false, rules).GroupFor(jpg)!;
        var singleResult = await new RatingService()
            .SetRatingAsync(singleGroup, 2, safeMode: true, sidecarIndex: staleIndex);
        c.Equal("旧索引缺新 sidecar 仍写入成功", RatingWriteOutcome.Applied, singleResult.Outcome);
        c.True("旧索引缺新 sidecar 仍作为来源写入",
            singleResult.Files.SelectMany(file => file.Sources)
                .Any(source => source.Source == XmpRatingSource.Sidecar && source.Written));
        c.Equal("旧索引缺新 sidecar 后磁盘值", 2,
            (await RatingSourceReader.ReadSidecarAsync(new LocalFileRef(sidecarPath))).NumericValue);

        var arw = new ImageFile(new LocalFileRef(arwPath));
        var group = PhotoGroupMap.Build([jpg, arw], true, rules).GroupFor(jpg)!;
        var groupResult = await new RatingService()
            .SetRatingAsync(group, 4, safeMode: true, sidecarIndex: staleIndex);
        c.Equal("混合索引/新增组员仍写入成功", RatingWriteOutcome.Applied, groupResult.Outcome);
        var sidecarOutcomes = groupResult.Files
            .SelectMany(file => file.Sources)
            .Where(source => source.Source == XmpRatingSource.Sidecar)
            .ToArray();
        c.Equal("共享 sidecar 仍只出现一次", 1, sidecarOutcomes.Length);
        c.Equal("混合索引/新增组员后 sidecar 值", 4,
            (await RatingSourceReader.ReadSidecarAsync(new LocalFileRef(sidecarPath))).NumericValue);
    }

    // ---------------------------------------------------------------- sidecar 索引阶段 / 新增目标

    private static async Task CheckFolderIndexAsync(Checks c, string root)
    {
        c.Section("sidecar 索引阶段与新增目标");

        var folderPath = Path.Combine(root, "folder-index");
        Directory.CreateDirectory(folderPath);
        var stagePath = CopyFixture(
            "release/native-p2-fixtures/color-srgb.jpg", folderPath, "stage.jpg");
        await File.WriteAllBytesAsync(Path.Combine(folderPath, "stage.xmp"), Sidecar("5"));
        CopyFixture("release/native-p2-fixtures/color-srgb.jpg", folderPath, "other.jpg");
        CopyFixture("release/native-p2-fixtures/color-srgb.jpg", folderPath, "shared.jpg");
        CopyFixture("TestCase/A7C01489.ARW", folderPath, "shared.ARW");
        await File.WriteAllBytesAsync(Path.Combine(folderPath, "shared.xmp"), Sidecar("5"));

        using var main = new MainViewModel();
        main.Settings.SameNameAsOnePhoto = true;

        // 阶段 1：显式目标在枚举之前读取评分，此时索引必须回退到逐文件查找。
        await main.FolderVM.OpenImageAsync(new LocalFileRef(stagePath));
        await WaitUntilAsync(
            () => main.FolderVM.AllFiles.Any(file => file.Name == "stage.jpg"),
            "阶段1 目标进入列表");
        var stage = main.FolderVM.AllFiles.Single(file => file.Name == "stage.jpg");
        c.True("阶段1 目标读取不漏已有 sidecar",
            stage.RatingSources.Any(source =>
                source.Source == XmpRatingSource.Sidecar && source.Value == 5));

        await WaitUntilAsync(
            () =>
            {
                var jpg = main.FolderVM.AllFiles.FirstOrDefault(file => file.Name == "shared.jpg");
                var raw = main.FolderVM.AllFiles.FirstOrDefault(
                    file => file.Name.Equals("shared.ARW", StringComparison.OrdinalIgnoreCase));
                return jpg != null && raw != null &&
                    jpg.RatingSources.Any(source =>
                        source.Source == XmpRatingSource.Sidecar && source.Value == 5) &&
                    raw.RatingSources.Any(source =>
                        source.Source == XmpRatingSource.Sidecar && source.Value == 5);
            },
            "共享 sidecar 进入两个文件的来源");
        var sharedJpg = main.FolderVM.AllFiles.Single(file => file.Name == "shared.jpg");
        var sharedArw = main.FolderVM.AllFiles.Single(file => file.Name.Equals("shared.ARW", StringComparison.OrdinalIgnoreCase));
        c.True("同名组两个文件都看到共享 sidecar",
            sharedJpg.RatingSources.Any(source => source.Source == XmpRatingSource.Sidecar && source.Value == 5)
            && sharedArw.RatingSources.Any(source => source.Source == XmpRatingSource.Sidecar && source.Value == 5));

        var sharedOutcome = await main.SetRatingWithResultAsync(sharedJpg, 2);
        c.True("设置星级返回明确结果",
            sharedOutcome is RatingWriteOutcome.Applied or RatingWriteOutcome.NoChange);
        c.True("成功后共享 sidecar 回填到组内每个文件",
            sharedJpg.RatingSources.Any(source => source.Source == XmpRatingSource.Sidecar && source.Value == 2)
            && sharedArw.RatingSources.Any(source => source.Source == XmpRatingSource.Sidecar && source.Value == 2));
        c.Equal("共享 sidecar 磁盘值", 2,
            (await RatingSourceReader.ReadSidecarAsync(
                new LocalFileRef(Path.Combine(folderPath, "shared.xmp")))).NumericValue);

        // 新增目标：目录已打开但目标未参与枚举，副作用读取必须仍能找到它的 sidecar。
        var addedPath = CopyFixture(
            "release/native-p2-fixtures/color-srgb.jpg", folderPath, "added.jpg");
        await File.WriteAllBytesAsync(Path.Combine(folderPath, "added.xmp"), Sidecar("3"));
        await main.FolderVM.OpenImageAsync(new LocalFileRef(addedPath));
        await WaitUntilAsync(
            () => main.FolderVM.AllFiles.Any(file => file.Name == "added.jpg"),
            "新增目标进入列表");
        var added = main.FolderVM.AllFiles.Single(file => file.Name == "added.jpg");
        c.True("新增目标未枚举也不漏 sidecar",
            added.RatingSources.Any(source =>
                source.Source == XmpRatingSource.Sidecar && source.Value == 3));

        var addedOutcome = await main.SetRatingWithResultAsync(added, 4);
        c.True("新增目标写回成功",
            addedOutcome is RatingWriteOutcome.Applied or RatingWriteOutcome.NoChange);
        c.Equal("新增目标 sidecar 一并写回", 4,
            (await RatingSourceReader.ReadSidecarAsync(
                new LocalFileRef(Path.Combine(folderPath, "added.xmp")))).NumericValue);

        // 非 Applied 失败也必须按磁盘真值重读，而不是沿用内存旧值。
        var stalePath = CopyFixture(
            "release/native-p2-fixtures/color-srgb.jpg", folderPath, "stale.jpg");
        await File.WriteAllBytesAsync(
            Path.Combine(folderPath, "stale.xmp"), Encoding.UTF8.GetBytes("<x:xmpmeta><rdf:RDF>"));
        await main.FolderVM.OpenImageAsync(new LocalFileRef(stalePath));
        await WaitUntilAsync(
            () => main.FolderVM.AllFiles.Any(file => file.Name == "stale.jpg"),
            "stale 目标进入列表");
        var stale = main.FolderVM.AllFiles.Single(file => file.Name == "stale.jpg");
        c.Equal("stale 初始内存评分", 3, stale.Rating);

        // 绕过 Main 直接改磁盘，制造内存与磁盘不一致。
        await XmpRatingStoreFactory.Create(new LocalFileRef(stalePath)).WriteAsync(1, safeMode: true);
        c.Equal("外部写入后内存仍旧", 3, stale.Rating);
        RatingWriteFailure? detailedFailure = null;
        var legacyFailureFired = false;
        main.RatingWriteFailedDetailed += failure => detailedFailure = failure;
        main.RatingWriteFailed += (_, _) => legacyFailureFired = true;
        var staleOutcome = await main.SetRatingWithResultAsync(stale, 4);
        c.Equal("损坏 sidecar 组写入失败", RatingWriteOutcome.RolledBack, staleOutcome);
        c.Equal("非 Applied 后内存按磁盘真值重读", 1, stale.Rating);
        c.True("详细失败事件携带本次结果",
            detailedFailure is { Outcome: RatingWriteOutcome.RolledBack, IsPartial: false });
        c.True("详细失败事件携带原因", !string.IsNullOrEmpty(detailedFailure?.Reason));
        c.True("旧失败事件仍保持兼容", legacyFailureFired);
        c.True("失败后损坏 sidecar 仍作为来源列出",
            stale.RatingSources.Any(source =>
                source.Source == XmpRatingSource.Sidecar && source.State == XmpRatingState.Invalid));
    }

    private static async Task WaitUntilAsync(Func<bool> condition, string what)
    {
        var deadline = Environment.TickCount64 + 8000;
        while (!condition())
        {
            if (Environment.TickCount64 > deadline)
                throw new TimeoutException("等待超时：" + what);
            await Task.Delay(25);
        }
    }

    // ---------------------------------------------------------------- 刷新序号

    private static async Task CheckRefreshSequenceAsync(Checks c, string root)
    {
        c.Section("评分刷新序号：旧刷新不覆盖新状态");

        var path = CopyFixture("release/native-p2-fixtures/color-srgb.jpg", root, "refresh-seq.jpg");
        await XmpRatingStoreFactory.Create(new LocalFileRef(path)).WriteAsync(1, safeMode: true);

        var image = new ImageFile(new LocalFileRef(path));
        image.UpdateCachedRating(5);
        var group = PhotoGroupMap.Build([image], false, Rules(("jpg", "JPG", ".jpg"))).GroupFor(image)!;

        using var main = new MainViewModel();
        var method = typeof(MainViewModel).GetMethod(
            "RefreshRatingsFromDiskAsync",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("找不到 RefreshRatingsFromDiskAsync");

        var dispatcher = new DelayedFirstDispatcher();
        UiDispatcher.Initialize(dispatcher);
        try
        {
            var oldRefresh = (Task)method.Invoke(main, [group])!;
            await dispatcher.WaitForDelayAsync();
            c.True("旧刷新快照尚未应用", image.Rating == 5);

            await XmpRatingStoreFactory.Create(new LocalFileRef(path)).WriteAsync(2, safeMode: true);
            var newRefresh = (Task)method.Invoke(main, [group])!;
            await newRefresh;
            c.Equal("新刷新把内存更新为磁盘 2", 2, image.Rating);

            await oldRefresh;
            c.Equal("旧刷新完成后不覆盖新状态", 2, image.Rating);
        }
        finally
        {
            UiDispatcher.Initialize(new InlineDispatcher());
        }
    }

    /// <summary>第一次 Apply 人为延迟，模拟旧刷新在读取完成后才回到 UI 线程。</summary>
    private sealed class DelayedFirstDispatcher : IUiDispatcher
    {
        private readonly TaskCompletionSource _delayStarted =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _used;

        public IScheduler MainThreadScheduler { get; } = ImmediateScheduler.Instance;

        public bool CheckAccess() => true;

        public void Post(Action action, UiPriority priority = UiPriority.Normal) => action();

        public async Task InvokeAsync(Action action, UiPriority priority = UiPriority.Normal)
        {
            if (Interlocked.Exchange(ref _used, 1) == 0)
            {
                _delayStarted.TrySetResult();
                await Task.Delay(700).ConfigureAwait(false);
            }

            action();
        }

        public Task<T> InvokeAsync<T>(Func<T> func, UiPriority priority = UiPriority.Normal) =>
            Task.FromResult(func());

        public Task WaitForDelayAsync() => _delayStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    private sealed class InlineDispatcher : IUiDispatcher
    {
        public IScheduler MainThreadScheduler { get; } = ImmediateScheduler.Instance;
        public bool CheckAccess() => true;
        public void Post(Action action, UiPriority priority = UiPriority.Normal) => action();
        public Task InvokeAsync(Action action, UiPriority priority = UiPriority.Normal)
        {
            action();
            return Task.CompletedTask;
        }
        public Task<T> InvokeAsync<T>(Func<T> func, UiPriority priority = UiPriority.Normal) => Task.FromResult(func());
    }

    private sealed class SlowEofFileRef : IFileRef
    {
        private readonly IFileRef _inner;
        private readonly TimeSpan _delay;
        private readonly TaskCompletionSource _eof =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _slowStreams;

        public SlowEofFileRef(IFileRef inner, TimeSpan delay)
        {
            _inner = inner;
            _delay = delay;
        }

        public string Name => _inner.Name;
        public Uri Path => _inner.Path;
        public StorageItemId StorageId => _inner.StorageId;
        public StorageCapabilities Capabilities => _inner.Capabilities;
        public string? TryGetLocalPath() => _inner.TryGetLocalPath();

        public async Task<Stream> OpenReadAsync(CancellationToken cancellationToken = default)
        {
            var stream = await _inner.OpenReadAsync(cancellationToken).ConfigureAwait(false);
            var slow = Interlocked.Increment(ref _slowStreams) == 1;
            return new SlowEofStream(stream, slow ? _delay : TimeSpan.Zero, slow ? _eof : null);
        }

        public Task<StorageItemBasicProperties> GetBasicPropertiesAsync(CancellationToken cancellationToken = default) =>
            _inner.GetBasicPropertiesAsync(cancellationToken);

        public Task<IFolderRef?> GetParentAsync(CancellationToken cancellationToken = default) =>
            _inner.GetParentAsync(cancellationToken);

        public Task<IStorageRandomAccessSession> OpenRandomAccessAsync(
            StorageAccessMode mode,
            CancellationToken cancellationToken = default) =>
            _inner.OpenRandomAccessAsync(mode, cancellationToken);

        public Task AtomicWriteAsync(
            Func<Stream, CancellationToken, Task> writeAsync,
            StorageRevision? expectedRevision = null,
            CancellationToken cancellationToken = default) =>
            _inner.AtomicWriteAsync(writeAsync, expectedRevision, cancellationToken);

        public Task DeleteAsync(CancellationToken cancellationToken = default) =>
            _inner.DeleteAsync(cancellationToken);

        public Task WaitForEofAsync() => _eof.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    private sealed class SlowEofStream : Stream
    {
        private readonly Stream _inner;
        private readonly TimeSpan _delay;
        private readonly TaskCompletionSource? _eof;
        private int _delayed;

        public SlowEofStream(Stream inner, TimeSpan delay, TaskCompletionSource? eof)
        {
            _inner = inner;
            _delay = delay;
            _eof = eof;
        }

        public override bool CanRead => _inner.CanRead;
        public override bool CanSeek => _inner.CanSeek;
        public override bool CanWrite => false;
        public override long Length => _inner.Length;
        public override long Position { get => _inner.Position; set => _inner.Position = value; }

        public override void Flush() => _inner.Flush();
        public override int Read(byte[] buffer, int offset, int count) =>
            Slow(_inner.Read(buffer, offset, count));
        public override int Read(Span<byte> buffer) => Slow(_inner.Read(buffer));
        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default) =>
            Slow(await _inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false));

        private int Slow(int read)
        {
            if (read == 0 && Interlocked.Exchange(ref _delayed, 1) == 0)
            {
                _eof?.TrySetResult();
                if (_delay > TimeSpan.Zero) Thread.Sleep(_delay);
            }

            return read;
        }

        public override long Seek(long offset, SeekOrigin origin) => _inner.Seek(offset, origin);
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing) _inner.Dispose();
            base.Dispose(disposing);
        }

        public override async ValueTask DisposeAsync()
        {
            await _inner.DisposeAsync().ConfigureAwait(false);
            GC.SuppressFinalize(this);
        }
    }

    private static byte[] AddPngXmp(byte[] png, byte[] xmp)
    {
        var keyword = Encoding.ASCII.GetBytes(PngXmpContainerAdapter.XmpKeyword);
        var data = new byte[keyword.Length + 5 + xmp.Length];
        keyword.CopyTo(data, 0);
        var position = keyword.Length;
        data[position++] = 0; // keyword terminator
        data[position++] = 0; // compression flag: uncompressed
        data[position++] = 0; // compression method
        data[position++] = 0; // language tag terminator
        data[position++] = 0; // translated keyword terminator
        xmp.CopyTo(data, position);

        var chunkType = "iTXt"u8.ToArray();
        var chunk = new byte[4 + 4 + data.Length + 4];
        WriteBigEndian(chunk, 0, (uint)data.Length);
        chunkType.CopyTo(chunk, 4);
        data.CopyTo(chunk, 8);
        var crcInput = new byte[4 + data.Length];
        chunkType.CopyTo(crcInput, 0);
        data.CopyTo(crcInput, 4);
        WriteBigEndian(chunk, 8 + data.Length, Crc32(crcInput));

        var iend = png.AsSpan().IndexOf("IEND"u8);
        if (iend < 4) throw new InvalidOperationException("PNG 夹具缺少 IEND");
        var insertAt = iend - 4;
        var result = new byte[png.Length + chunk.Length];
        png.AsSpan(0, insertAt).CopyTo(result);
        chunk.CopyTo(result.AsSpan(insertAt));
        png.AsSpan(insertAt).CopyTo(result.AsSpan(insertAt + chunk.Length));
        return result;
    }

    private static void WriteBigEndian(byte[] target, int offset, uint value)
    {
        target[offset] = (byte)(value >> 24);
        target[offset + 1] = (byte)(value >> 16);
        target[offset + 2] = (byte)(value >> 8);
        target[offset + 3] = (byte)value;
    }

    private static uint Crc32(byte[] data)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var value in data)
        {
            crc ^= value;
            for (var bit = 0; bit < 8; bit++)
                crc = (crc & 1) != 0 ? 0xEDB88320u ^ (crc >> 1) : crc >> 1;
        }

        return crc ^ 0xFFFFFFFFu;
    }

    private static (int Offset, int Length) FindCanonicalXmpSegment(byte[] jpeg)
    {
        var identifier = "http://ns.adobe.com/xap/1.0/\0"u8;
        var index = 2;
        while (index + 4 <= jpeg.Length)
        {
            if (jpeg[index] != 0xFF)
            {
                index++;
                continue;
            }

            var marker = jpeg[index + 1];
            if (marker is 0xD8 or 0xD9)
            {
                index += 2;
                continue;
            }

            if (marker == 0xDA) break;
            var length = (jpeg[index + 2] << 8) | jpeg[index + 3];
            if (length < 2 || index + 2 + length > jpeg.Length) break;
            if (marker == 0xE1 &&
                length >= 2 + identifier.Length &&
                jpeg.AsSpan(index + 4, identifier.Length).SequenceEqual(identifier))
            {
                return (index, length + 2);
            }

            index += 2 + length;
        }

        throw new InvalidOperationException("JPEG 中没有 canonical XMP APP1");
    }

    private static byte[] BuildExtendedXmpJpeg(byte[] jpeg, (int Offset, int Length) segment)
    {
        var identifier = "http://ns.adobe.com/xap/1.0/\0"u8;
        var packet = jpeg.AsSpan(segment.Offset + 4 + identifier.Length,
            segment.Length - 4 - identifier.Length).ToArray();
        var meta = XmpMetaFactory.ParseFromBuffer(packet);
        meta.SetProperty(XmpRatingReader.XmpNoteNamespace, "HasExtendedXMP", "0123456789ABCDEF");
        var serialized = XmpMetaFactory.SerializeToBuffer(meta, new SerializeOptions
        {
            ExactPacketLength = true,
            Padding = packet.Length,
        });
        if (serialized.Length != packet.Length)
            throw new InvalidOperationException("扩展标记未能保持 XMP 包长度");

        var segmentLength = 2 + identifier.Length + serialized.Length;
        var replacement = new byte[2 + segmentLength];
        replacement[0] = 0xFF;
        replacement[1] = 0xE1;
        replacement[2] = (byte)(segmentLength >> 8);
        replacement[3] = (byte)segmentLength;
        identifier.CopyTo(replacement.AsSpan(4));
        serialized.CopyTo(replacement.AsSpan(4 + identifier.Length));

        var result = new byte[jpeg.Length - segment.Length + replacement.Length];
        jpeg.AsSpan(0, segment.Offset).CopyTo(result);
        replacement.CopyTo(result.AsSpan(segment.Offset));
        jpeg.AsSpan(segment.Offset + segment.Length)
            .CopyTo(result.AsSpan(segment.Offset + replacement.Length));
        return result;
    }

    private static bool IsHeif(string name) =>
        string.Equals(Path.GetExtension(name), ".hif", StringComparison.OrdinalIgnoreCase);

    // ---------------------------------------------------------------- 提交失败与能力预检

    private static async Task CheckCommitFailureAsync(Checks c, string root)
    {
        c.Section("提交失败自愈与失败回执");

        var rules = Rules(("jpg", "JPG", ".jpg"));

        var oneShotPath = CopyFixture("release/native-p2-fixtures/color-srgb.jpg", root, "flush-once.jpg");
        var oneShotOriginal = await File.ReadAllBytesAsync(oneShotPath);
        var oneShotImage = new ImageFile(new LocalFileRef(oneShotPath));
        var oneShotGroup = PhotoGroupMap.Build([oneShotImage], false, rules).GroupFor(oneShotImage)!;
        var oneShotStore = new FlushFailingFileRef(new LocalFileRef(oneShotPath), failAll: false);
        var oneShotService = new RatingService(
            _ => new ContainerRatingStore(oneShotStore, new JpegXmpContainerAdapter()));
        var oneShotResult = await oneShotService.SetRatingAsync(oneShotGroup, 4, safeMode: true);
        c.Equal("已写后抛错且自愈成功返回 RolledBack", RatingWriteOutcome.RolledBack, oneShotResult.Outcome);
        c.Bytes("自愈成功后原字节恢复", oneShotOriginal, await File.ReadAllBytesAsync(oneShotPath));

        var persistentPath = CopyFixture("release/native-p2-fixtures/color-srgb.jpg", root, "flush-all.jpg");
        var persistentImage = new ImageFile(new LocalFileRef(persistentPath));
        var persistentGroup = PhotoGroupMap.Build([persistentImage], false, rules).GroupFor(persistentImage)!;
        var persistentStore = new FlushFailingFileRef(new LocalFileRef(persistentPath), failAll: true);
        var persistentService = new RatingService(
            _ => new ContainerRatingStore(persistentStore, new JpegXmpContainerAdapter()));
        var persistentResult = await persistentService.SetRatingAsync(persistentGroup, 4, safeMode: true);
        c.Equal("自愈失败进入 Partial", RatingWriteOutcome.Partial, persistentResult.Outcome);
        c.True("自愈失败有补偿错误",
            persistentResult.Files.SelectMany(file => file.Sources)
                .Any(source => source.CompensationError != null));
        c.True("自愈失败不声称未修改任何文件",
            persistentResult.FailureReason?.Contains("未修改任何文件", StringComparison.Ordinal) != true);
        c.True("自愈失败仍标记来源被写过",
            persistentResult.Files.SelectMany(file => file.Sources).Any(source => source.Written));

        var capabilityPath = CopyFixture("release/native-p2-fixtures/color-srgb.jpg", root, "no-seek.jpg");
        var capabilityOriginal = await File.ReadAllBytesAsync(capabilityPath);
        var capabilityFile = new CapabilityFileRef(
            new LocalFileRef(capabilityPath),
            StorageCapability.Unsupported);
        await c.StorageFailsAsync(
            "随机访问不可写时预检提前失败",
            StorageError.NotSeekable,
            () => new ContainerRatingStore(capabilityFile, new JpegXmpContainerAdapter())
                .PrepareAsync(4, true));
        c.Bytes("能力预检失败零修改", capabilityOriginal, await File.ReadAllBytesAsync(capabilityPath));
    }

    // ---------------------------------------------------------------- 训练

    private static Task CheckTrainingRatingAsync(Checks c)
    {
        c.Section("训练 Rating 只取本文件内嵌语义");

        var file = new ImageFile(new LocalFileRef(Path.Combine(Path.GetTempPath(), "training-placeholder.jpg")));
        file.UpdateCachedRating(4);
        c.Equal("Rated 1..5 进入训练", 4, FolderFeatureIndexer.ResolveTrainingRating(file));

        file.UpdateCachedRating(0);
        c.Equal("0 星不进入训练", null, FolderFeatureIndexer.ResolveTrainingRating(file));

        file.UpdateCachedRating(-1);
        c.Equal("Rejected 不进入训练", null, FolderFeatureIndexer.ResolveTrainingRating(file));

        file.ApplyRatingSources(
        [
            new RatingSourceState(
                XmpRatingSource.Embedded,
                file.File,
                new XmpRatingSnapshot(XmpRatingState.Conflict, null, XmpRatingSource.Embedded, "冲突")),
        ]);
        c.Equal("Conflict 不进入训练", null, FolderFeatureIndexer.ResolveTrainingRating(file));

        file.ApplyRatingSources(
        [
            new RatingSourceState(
                XmpRatingSource.Embedded,
                file.File,
                new XmpRatingSnapshot(XmpRatingState.Invalid, null, XmpRatingSource.Embedded, "损坏")),
        ]);
        c.Equal("Invalid 不进入训练", null, FolderFeatureIndexer.ResolveTrainingRating(file));

        file.ApplyRatingSources(
        [
            new RatingSourceState(
                XmpRatingSource.Embedded,
                file.File,
                XmpRatingSnapshot.Missing(XmpRatingSource.Embedded)),
        ]);
        c.Equal("Missing 不进入训练", null, FolderFeatureIndexer.ResolveTrainingRating(file));

        return Task.CompletedTask;
    }

    // ---------------------------------------------------------------- 筛选

    private static async Task CheckFiltersAsync(Checks c, string root)
    {
        c.Section("代表内嵌数字筛选、已拒绝与冲突筛选");

        var jpgPath = CopyFixture("release/native-p2-fixtures/color-srgb.jpg", root, "filter.jpg");
        var rawPath = CopyFixture("TestCase/A7C01489.ARW", root, "filter.ARW");
        await File.WriteAllBytesAsync(Path.Combine(root, "filter.xmp"), Sidecar("1"));

        var jpg = new ImageFile(new LocalFileRef(jpgPath));
        var raw = new ImageFile(new LocalFileRef(rawPath));
        await jpg.LoadRatingOnlyAsync();
        await raw.LoadRatingOnlyAsync();

        var rules = Rules(("jpg", "JPG", ".jpg"), ("raw", "RAW", ".arw"));
        var group = PhotoGroupMap.Build([jpg, raw], true, rules).GroupFor(jpg)!;
        c.Equal("代表内嵌值用于数字筛选", 3, jpg.Rating);
        c.True("代表与 sidecar/伴侣冲突", group.HasRatingConflict);

        using var settings = new SettingsViewModel();
        settings.SameNameAsOnePhoto = true;

        var none = PhotoListProjection.Project([jpg, raw], settings, "None");
        c.Equal("数字筛选不因冲突而排除代表", 0, none.Items.Count);
        var eq3 = PhotoListProjection.Project([jpg, raw], settings, "Eq3");
        c.Equal("Eq3 命中的是代表文件", jpg, eq3.Items.SingleOrDefault());
        var conflict = PhotoListProjection.Project([jpg, raw], settings, "Conflict");
        c.Equal("冲突筛选命中代表文件", jpg, conflict.Items.SingleOrDefault());
        c.True("冲突组同时保留数字命中", eq3.Items.Contains(jpg));

        var rejected = new ImageFile(new LocalFileRef(Path.Combine(root, "rejected.jpg")));
        rejected.UpdateCachedRating(-1);
        var rejectedOnly = PhotoGroupMap.Build([rejected], false, rules).GroupFor(rejected)!;
        c.Equal("Rejected 代表数值为 -1", -1, rejected.Rating);
        var rejectedProjection = PhotoListProjection.Project([jpg, raw, rejected], settings, "Rejected");
        c.Equal("已拒绝筛选命中 -1", rejected, rejectedProjection.Items.SingleOrDefault());
        var noneProjection = PhotoListProjection.Project([jpg, raw, rejected], settings, "None");
        c.True("已拒绝不算无星级", !noneProjection.Items.Contains(rejected));
        c.True("Rejected 组自身不冲突", !rejectedOnly.HasRatingConflict);

        // Missing 与 Rated 0 在状态上必须区分，即使数值都是 0。
        var missingFile = new ImageFile(new LocalFileRef(Path.Combine(root, "missing.jpg")));
        var zeroFile = new ImageFile(new LocalFileRef(Path.Combine(root, "zero.jpg")));
        missingFile.ApplyRatingSources(
        [
            new RatingSourceState(
                XmpRatingSource.Embedded,
                missingFile.File,
                XmpRatingSnapshot.Missing(XmpRatingSource.Embedded)),
        ]);
        zeroFile.ApplyRatingSources(
        [
            new RatingSourceState(
                XmpRatingSource.Embedded,
                zeroFile.File,
                new XmpRatingSnapshot(XmpRatingState.Rated, 0, XmpRatingSource.Embedded)),
        ]);
        c.True("Missing 与 0 的数值相同但状态不同",
            missingFile.Rating == 0 && zeroFile.Rating == 0
            && missingFile.RatingState == XmpRatingState.Missing
            && zeroFile.RatingState == XmpRatingState.Rated);
        var missingGroup = PhotoGroupMap.Build([missingFile], false, rules).GroupFor(missingFile)!;
        var zeroGroup = PhotoGroupMap.Build([zeroFile], false, rules).GroupFor(zeroFile)!;
        c.True("单个 Missing 来源不算冲突", !missingGroup.HasRatingConflict);
        c.True("单个 Rated 0 来源不算冲突", !zeroGroup.HasRatingConflict);

        var invalidFile = new ImageFile(new LocalFileRef(Path.Combine(root, "invalid.jpg")));
        invalidFile.ApplyRatingSources(
        [
            new RatingSourceState(
                XmpRatingSource.Embedded,
                invalidFile.File,
                new XmpRatingSnapshot(XmpRatingState.Invalid, null, XmpRatingSource.Embedded, "损坏")),
        ]);
        var invalidGroup = PhotoGroupMap.Build([invalidFile], false, rules).GroupFor(invalidFile)!;
        c.True("Invalid 代表视为冲突", invalidGroup.HasRatingConflict);
        c.Equal("Invalid 代表不命中无星级", 0,
            PhotoListProjection.Project([invalidFile], settings, "None").Items.Count);
        c.Equal("Invalid 代表不命中一星以上", 0,
            PhotoListProjection.Project([invalidFile], settings, "Gt1").Items.Count);
        c.Equal("Invalid 代表命中星级冲突", invalidFile,
            PhotoListProjection.Project([invalidFile], settings, "Conflict").Items.SingleOrDefault());

        // 多来源中有 Missing 也视为冲突（不挑一个带值来源冒充一致）。
        var multiJpg = new ImageFile(new LocalFileRef(Path.Combine(root, "multi.jpg")));
        var multiRaw = new ImageFile(new LocalFileRef(Path.Combine(root, "multi.arw")));
        multiJpg.ApplyRatingSources(
        [
            new RatingSourceState(
                XmpRatingSource.Embedded,
                multiJpg.File,
                XmpRatingSnapshot.Missing(XmpRatingSource.Embedded)),
        ]);
        multiRaw.ApplyRatingSources(
        [
            new RatingSourceState(
                XmpRatingSource.Embedded,
                multiRaw.File,
                XmpRatingSnapshot.Missing(XmpRatingSource.Embedded)),
        ]);
        var multiGroup = PhotoGroupMap.Build([multiJpg, multiRaw], true, rules).GroupFor(multiJpg)!;
        c.True("多来源存在 Missing 视为冲突", multiGroup.HasRatingConflict);
    }

    // ---------------------------------------------------------------- 工具

    /// <summary>复制真实文件并把内嵌 XMP 里的 Rating 元素整段替换成空格（保持包与文件长度），得到缺 Rating 的样本。</summary>
    private static async Task<(string Path, byte[] Blanked, int PacketOffset, int PacketLength)> CreateNoRatingCopyAsync(
        string fixture,
        string root,
        string name,
        IXmpContainerAdapter adapter)
    {
        var path = CopyFixture(fixture, root, name);
        var file = new LocalFileRef(path);
        var packet = await adapter.ReadCanonicalPacketAsync(file)
            ?? throw new InvalidOperationException($"{name} 缺少 canonical XMP");
        var bytes = await File.ReadAllBytesAsync(path);

        var open = packet.Data.AsSpan().IndexOf("<xmp:Rating>"u8);
        var close = packet.Data.AsSpan().IndexOf("</xmp:Rating>"u8);
        if (open < 0 || close < open)
            throw new InvalidOperationException($"{name} 的 XMP 没有可移除的元素式 Rating");

        var elementEnd = close + "</xmp:Rating>".Length;
        var blank = new byte[elementEnd - open];
        Array.Fill(blank, (byte)' ');
        blank.CopyTo(bytes.AsSpan(checked((int)packet.Offset) + open));
        await File.WriteAllBytesAsync(path, bytes);
        return (path, bytes, checked((int)packet.Offset), packet.Length);
    }

    private static bool OutsideRangeEquals(byte[] before, byte[] after, int offset, int length)
    {
        if (before.Length != after.Length ||
            offset < 0 ||
            length < 0 ||
            offset + length > before.Length)
        {
            return false;
        }

        return before.AsSpan(0, offset).SequenceEqual(after.AsSpan(0, offset)) &&
            before.AsSpan(offset + length).SequenceEqual(after.AsSpan(offset + length));
    }

    private static byte[] Packet(string description, int padding = 4096)
    {
        var text = "<x:xmpmeta xmlns:x=\"adobe:ns:meta/\"><rdf:RDF xmlns:rdf=\"http://www.w3.org/1999/02/22-rdf-syntax-ns#\">"
            + description
            + "</rdf:RDF></x:xmpmeta>"
            + new string(' ', padding);
        return Encoding.UTF8.GetBytes(text);
    }

    private static byte[] Sidecar(string rating) =>
        Packet($"<rdf:Description rdf:about=\"\" xmlns:xmp=\"http://ns.adobe.com/xap/1.0/\" xmp:Rating=\"{rating}\" />");

    private static PhotoFormatRule[] Rules(params (string Id, string Name, string Extension)[] formats) =>
        formats
            .Select(item => new PhotoFormatRule(item.Id, item.Name, [item.Extension]))
            .ToArray();

    private static string CopyFixture(string relative, string root, string? name = null)
    {
        if (!File.Exists(relative)) throw new FileNotFoundException("缺少验收夹具", relative);
        var path = Path.Combine(root, name ?? Path.GetFileName(relative));
        File.Copy(relative, path, overwrite: true);
        return path;
    }

    private static List<int> ChangedIndexes(byte[] left, byte[] right)
    {
        var changed = new List<int>();
        var length = Math.Min(left.Length, right.Length);
        for (var index = 0; index < length; index++)
        {
            if (left[index] != right[index]) changed.Add(index);
        }

        if (left.Length != right.Length) changed.Add(-1);
        return changed;
    }

    /// <summary>委托本地文件，但在 ReadWrite 会话的 Flush 时注入失败（可选一次性或持续）。</summary>
    private sealed class FlushFailingFileRef : IFileRef
    {
        private readonly IFileRef _inner;
        private readonly bool _failAll;
        private int _flushFailures;

        public FlushFailingFileRef(IFileRef inner, bool failAll)
        {
            _inner = inner;
            _failAll = failAll;
        }

        public string Name => _inner.Name;
        public Uri Path => _inner.Path;
        public StorageItemId StorageId => _inner.StorageId;
        public StorageCapabilities Capabilities => _inner.Capabilities;
        public string? TryGetLocalPath() => _inner.TryGetLocalPath();

        public Task<Stream> OpenReadAsync(CancellationToken cancellationToken = default) =>
            _inner.OpenReadAsync(cancellationToken);

        public Task<StorageItemBasicProperties> GetBasicPropertiesAsync(CancellationToken cancellationToken = default) =>
            _inner.GetBasicPropertiesAsync(cancellationToken);

        public Task<IFolderRef?> GetParentAsync(CancellationToken cancellationToken = default) =>
            _inner.GetParentAsync(cancellationToken);

        public async Task<IStorageRandomAccessSession> OpenRandomAccessAsync(
            StorageAccessMode mode,
            CancellationToken cancellationToken = default)
        {
            var session = await _inner.OpenRandomAccessAsync(mode, cancellationToken).ConfigureAwait(false);
            return mode == StorageAccessMode.ReadWrite
                ? new FlushFailingSession(session, this)
                : session;
        }

        public Task AtomicWriteAsync(
            Func<Stream, CancellationToken, Task> writeAsync,
            StorageRevision? expectedRevision = null,
            CancellationToken cancellationToken = default) =>
            _inner.AtomicWriteAsync(writeAsync, expectedRevision, cancellationToken);

        public Task DeleteAsync(CancellationToken cancellationToken = default) =>
            _inner.DeleteAsync(cancellationToken);

        public bool ShouldFailFlush()
        {
            if (_failAll) return true;
            return Interlocked.Increment(ref _flushFailures) == 1;
        }
    }

    private sealed class FlushFailingSession : IStorageRandomAccessSession
    {
        private readonly IStorageRandomAccessSession _inner;
        private readonly FlushFailingFileRef _owner;

        public FlushFailingSession(IStorageRandomAccessSession inner, FlushFailingFileRef owner)
        {
            _inner = inner;
            _owner = owner;
        }

        public long Length => _inner.Length;
        public StorageRevision? Revision => _inner.Revision;

        public ValueTask<int> ReadAsync(long offset, Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            _inner.ReadAsync(offset, buffer, cancellationToken);

        public ValueTask WriteAsync(long offset, ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) =>
            _inner.WriteAsync(offset, buffer, cancellationToken);

        public ValueTask SetLengthAsync(long length, CancellationToken cancellationToken = default) =>
            _inner.SetLengthAsync(length, cancellationToken);

        public async ValueTask FlushAsync(CancellationToken cancellationToken = default)
        {
            // 先持久化再抛错，模拟“已经写下去但提交阶段报错”。
            await _inner.FlushAsync(cancellationToken).ConfigureAwait(false);
            if (_owner.ShouldFailFlush())
                throw new IOException("injected flush failure");
        }

        public ValueTask DisposeAsync() => _inner.DisposeAsync();
    }

    /// <summary>只替换 Capabilities 的本地文件包装，用于预检能力测试。</summary>
    private sealed class CapabilityFileRef : IFileRef
    {
        private readonly IFileRef _inner;

        public CapabilityFileRef(IFileRef inner, StorageCapability randomAccessWrite)
        {
            _inner = inner;
            Capabilities = new StorageCapabilities
            {
                RandomAccessWrite = randomAccessWrite,
                AtomicReplace = StorageCapability.Supported,
                CreateChild = StorageCapability.Supported,
                Delete = StorageCapability.Supported,
            };
        }

        public string Name => _inner.Name;
        public Uri Path => _inner.Path;
        public StorageItemId StorageId => _inner.StorageId;
        public StorageCapabilities Capabilities { get; }
        public string? TryGetLocalPath() => _inner.TryGetLocalPath();

        public Task<Stream> OpenReadAsync(CancellationToken cancellationToken = default) =>
            _inner.OpenReadAsync(cancellationToken);

        public Task<StorageItemBasicProperties> GetBasicPropertiesAsync(CancellationToken cancellationToken = default) =>
            _inner.GetBasicPropertiesAsync(cancellationToken);

        public Task<IFolderRef?> GetParentAsync(CancellationToken cancellationToken = default) =>
            _inner.GetParentAsync(cancellationToken);

        public Task<IStorageRandomAccessSession> OpenRandomAccessAsync(
            StorageAccessMode mode,
            CancellationToken cancellationToken = default) =>
            _inner.OpenRandomAccessAsync(mode, cancellationToken);

        public Task AtomicWriteAsync(
            Func<Stream, CancellationToken, Task> writeAsync,
            StorageRevision? expectedRevision = null,
            CancellationToken cancellationToken = default) =>
            _inner.AtomicWriteAsync(writeAsync, expectedRevision, cancellationToken);

        public Task DeleteAsync(CancellationToken cancellationToken = default) =>
            _inner.DeleteAsync(cancellationToken);
    }

    private sealed class FaultStore : IXmpRatingStore
    {
        private readonly IXmpRatingStore _inner;
        private readonly bool _failCommit;
        private readonly bool _failRestore;

        public FaultStore(IXmpRatingStore inner, bool failCommit, bool failRestore)
        {
            _inner = inner;
            _failCommit = failCommit;
            _failRestore = failRestore;
        }

        public Task<XmpRatingSnapshot> ReadAsync(CancellationToken cancellationToken = default) =>
            _inner.ReadAsync(cancellationToken);

        public Task<XmpRatingPreparation> PrepareAsync(
            int rating,
            bool safeMode,
            CancellationToken cancellationToken = default) =>
            _inner.PrepareAsync(rating, safeMode, cancellationToken);

        public Task<XmpRatingWriteReceipt> CommitAsync(
            XmpRatingPreparation preparation,
            CancellationToken cancellationToken = default)
        {
            if (_failCommit) throw new StorageException(StorageError.IoError, "注入写失败");
            return _inner.CommitAsync(preparation, cancellationToken);
        }

        public Task RestoreAsync(XmpRatingWriteReceipt receipt, CancellationToken cancellationToken = default)
        {
            if (_failRestore) throw new StorageException(StorageError.IoError, "注入补偿失败");
            return _inner.RestoreAsync(receipt, cancellationToken);
        }
    }
}
