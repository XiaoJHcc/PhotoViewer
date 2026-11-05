using System.Runtime.InteropServices;
using PhotoViewer.Core.AI;
using PhotoViewer.Core.Color;
using PhotoViewer.Core.Image;
using PhotoViewer.Core.Storage;
using SkiaSharp;

int checks = 0;
double maxError = 0;
foreach (var space in new[] { ImageColorSpace.Srgb, ImageColorSpace.DisplayP3, ImageColorSpace.AdobeRgb })
{
    foreach (var color in new[] { new byte[] { 0, 0, 0 }, new byte[] { 255, 255, 255 }, new byte[] { 128, 128, 128 },
        new byte[] { 0, 255, 0 }, new byte[] { 0, 0, 255 }, new byte[] { 255, 0, 0 }, new byte[] { 64, 192, 128 } })
    {
        using var source = new DecodedImage([color[0], color[1], color[2], 255], 1, 1, 4, PixelEncoding.Bgra32, space);
        using var converted = ColorEngine.ToScRgb16(source);
        var values = MemoryMarshal.Cast<byte, Half>(converted.Pixels.AsSpan(0, 8));
        double red = Decode(color[2] / 255.0, space), green = Decode(color[1] / 255.0, space), blue = Decode(color[0] / 255.0, space);
        double[] expected = space switch
        {
            ImageColorSpace.DisplayP3 => [1.224745 * red - .224904 * green, -.042058 * red + 1.042081 * green, -.019642 * red - .078655 * green + 1.098537 * blue],
            ImageColorSpace.AdobeRgb => [1.398283 * red - .398283 * green, green, -.042938 * green + 1.042938 * blue],
            _ => [red, green, blue],
        };
        for (int channel = 0; channel < 3; channel++)
        {
            double error = Math.Abs((double)values[channel] - expected[channel]);
            maxError = Math.Max(maxError, error);
            if (error > .0025) throw new InvalidOperationException($"{space} 转换误差 {error}");
        }
        if ((float)values[3] != 1) throw new InvalidOperationException("alpha 错误");
        if (space == ImageColorSpace.DisplayP3 && color[1] == 255 && color[0] == 0 && color[2] == 0 && (float)values[0] >= 0)
            throw new InvalidOperationException("广色域负分量被裁剪");
        checks++;
    }
}
using (var padded = new DecodedImage([0, 0, 255, 255, 0, 255, 0, 255, 99, 99, 255, 0, 0, 255, 128, 128, 128, 255], 2, 2, 10, PixelEncoding.Bgra32, ImageColorSpace.Srgb))
using (var floating = ColorEngine.ToScRgb16(padded))
using (var roundTrip = ColorEngine.ToSrgb8(floating))
{
    for (int row = 0; row < 2; row++)
        for (int column = 0; column < 8; column++)
            if (Math.Abs(roundTrip.Pixels[row * roundTrip.Stride + column] - padded.Pixels[row * 10 + column]) > 1) throw new InvalidOperationException("行距/8 位往返错误");
    checks++;
}
using (var p3Pixels = new DecodedImage(Enumerable.Repeat(new byte[] { 0, 0, 255, 255 }, 16).SelectMany(pixel => pixel).ToArray(),
    4, 4, 16, PixelEncoding.Bgra32, ImageColorSpace.DisplayP3))
using (var extended = ColorEngine.ToScRgb16(p3Pixels))
{
    foreach (var transformed in new[]
    {
        SkiaImageCodec.ScaleToLongEdge(extended, 2),
        SkiaImageCodec.ApplyOrientation(extended, 90, false),
    })
    {
        using var result = transformed ?? throw new InvalidOperationException("F16 几何转换失败");
        var components = MemoryMarshal.Cast<byte, Half>(result.Pixels.AsSpan(0, 8));
        if ((float)components[0] < 1.2f || (float)components[1] > -0.03f || (float)components[2] > -0.01f)
            throw new InvalidOperationException($"F16 几何转换裁剪了扩展色域: {components[0]}/{components[1]}/{components[2]}");
        checks++;
    }
}
if (ColorEngine.InferJpegColorSpace(2) != ImageColorSpace.AdobeRgb || ColorEngine.InferJpegColorSpace(null, "R03") != ImageColorSpace.AdobeRgb || ColorEngine.InferJpegColorSpace(null) != ImageColorSpace.Srgb)
    throw new InvalidOperationException("JPEG 推断规则错误");
checks++;
using (var bitmap = new SKBitmap(320, 200))
{
    bitmap.Erase(SKColors.Green);
    using var encoded = bitmap.Encode(SKEncodedImageFormat.Jpeg, 95);
    foreach (var target in new[] { 160, 139 })
    {
        using var stream = encoded.AsStream();
        using var proxy = SkiaImageCodec.DecodeToLongEdge(stream, target) ?? throw new InvalidOperationException("无 EXIF JPEG 代理解码失败");
        if (proxy.Width != target || proxy.SourceWidth != 320 || proxy.SourceHeight != 200)
            throw new InvalidOperationException("codec 子采样或精缩丢失源尺寸");
        checks++;
        using var smaller = SkiaImageCodec.ScaleToLongEdge(proxy, 80) ?? throw new InvalidOperationException("代理再次缩放失败");
        if (smaller.Width != 80 || smaller.SourceWidth != 320 || smaller.SourceHeight != 200)
            throw new InvalidOperationException("内存缩放丢失源尺寸");
        checks++;
    }
}
// ---- 真实 JPEG 三色域：解码保留源色域（有效 ICC 优先 / EXIF 声明兜底），转 F16 与独立参考矩阵一致 ----
string jpegDir = Path.Combine(FindFixturesDirectory(), "jpeg");
using (var decodedSrgb = DecodeFile(Path.Combine(jpegDir, "color-srgb.jpg")))
using (var decodedP3 = DecodeFile(Path.Combine(jpegDir, "color-p3.jpg")))
using (var decodedAdobe = DecodeFile(Path.Combine(jpegDir, "color-adobe.jpg")))
using (var decodedUntaggedAdobe = DecodeFile(Path.Combine(jpegDir, "color-untagged-adobe.jpg")))
{
    RequireSourceColor(decodedSrgb, ImageColorSpace.Srgb, "color-srgb.jpg");
    RequireSourceColor(decodedP3, ImageColorSpace.Icc, "color-p3.jpg");
    RequireSourceColor(decodedAdobe, ImageColorSpace.Icc, "color-adobe.jpg");
    RequireSourceColor(decodedUntaggedAdobe, ImageColorSpace.AdobeRgb, "color-untagged-adobe.jpg");
    if (decodedP3.IccProfile.Length < 128 || decodedAdobe.IccProfile.Length < 128)
        throw new InvalidOperationException("ICC 色彩标签未携带 profile 字节");
    if (!decodedUntaggedAdobe.IccProfile.IsEmpty)
        throw new InvalidOperationException("无 ICC 的 JPEG 不应凭空造出 profile");
    checks++;

    foreach (var (image, space, name) in new (DecodedImage, ImageColorSpace, string)[]
    {
        (decodedSrgb, ImageColorSpace.Srgb, "color-srgb.jpg"),
        (decodedP3, ImageColorSpace.DisplayP3, "color-p3.jpg"),
        (decodedAdobe, ImageColorSpace.AdobeRgb, "color-adobe.jpg"),
        (decodedUntaggedAdobe, ImageColorSpace.AdobeRgb, "color-untagged-adobe.jpg"),
    })
    {
        using var linear = ColorEngine.ToScRgb16(image);
        if (linear.Encoding != PixelEncoding.RgbaF16 || linear.ColorSpace != ImageColorSpace.LinearSrgb)
            throw new InvalidOperationException($"{name} 转 F16 后色彩标签错误: {linear.Encoding}/{linear.ColorSpace}");
        foreach (var (u, v, label) in ChartPatchCenters())
        {
            int x = Math.Clamp((int)Math.Round(u * image.Width), 0, image.Width - 1);
            int y = Math.Clamp((int)Math.Round(v * image.Height), 0, image.Height - 1);
            var (r, g, b) = BgraAt(image, x, y);
            var expected = ReferenceLinear(r, g, b, space);
            var actual = LinearAt(linear, x, y);
            for (int channel = 0; channel < 3; channel++)
            {
                double error = Math.Abs(actual[channel] - expected[channel]);
                maxError = Math.Max(maxError, error);
                if (error > 0.006)
                    throw new InvalidOperationException(
                        $"{name} {label}块线性参考误差 {error:F5}（通道 {channel}：实际 {actual[channel]:F4} 参考 {expected[channel]:F4}）");
            }
        }
        checks++;
    }

    // 三色域转 F16 必须真正不同：P3 与 Adobe 的红都落在 sRGB 色域外（>1），且越靠广色域越大。
    var srgbRed = RedLinear(decodedSrgb);
    var p3Red = RedLinear(decodedP3);
    var adobeRed = RedLinear(decodedAdobe);
    var untaggedRed = RedLinear(decodedUntaggedAdobe);
    if (!(p3Red[0] > srgbRed[0] + 0.1 && adobeRed[0] > p3Red[0] + 0.1))
        throw new InvalidOperationException(
            $"三色域转 F16 未体现广色域差异: sRGB {srgbRed[0]:F3} / P3 {p3Red[0]:F3} / Adobe {adobeRed[0]:F3}");
    if (p3Red[2] >= 0 || p3Red[1] >= 0) throw new InvalidOperationException("P3 红转线性 sRGB 的负分量被裁剪");
    if (p3Red[0] <= 1.05 || adobeRed[0] <= 1.05) throw new InvalidOperationException("广色域超一（>1）分量被裁剪");
    for (int channel = 0; channel < 3; channel++)
        if (Math.Abs(untaggedRed[channel] - adobeRed[channel]) > 0.004)
            throw new InvalidOperationException($"EXIF ColorSpace=2 推断与 ICC Adobe RGB 不一致（通道 {channel}）");
    checks++;
}

// ---- ICC / 色域跨缩放、方向、裁剪与代理档全程不丢：编码、标签、profile、源尺寸、方向契约 ----
using (var full = DecodeFile(Path.Combine(jpegDir, "color-p3.jpg")))
{
    var profile = full.IccProfile.ToArray();
    using var proxy = DecodeToLongEdgeFile(Path.Combine(jpegDir, "color-p3.jpg"), 300)
        ?? throw new InvalidOperationException("ICC JPEG 代理解码失败");
    RequireCarriedColor(proxy, full.ColorSpace, profile, "代理解码");
    if (proxy.SourceWidth != full.Width || proxy.SourceHeight != full.Height)
        throw new InvalidOperationException("代理档丢失全分辨率源尺寸");
    checks++;

    using var scaled = SkiaImageCodec.ScaleToLongEdge(proxy, 150) ?? throw new InvalidOperationException("ICC 代理再缩放失败");
    RequireCarriedColor(scaled, full.ColorSpace, profile, "Mitchell 缩放");
    if (scaled.SourceWidth != full.Width || scaled.SourceHeight != full.Height)
        throw new InvalidOperationException("内存缩放丢失全分辨率源尺寸");
    checks++;

    using var rotated = SkiaImageCodec.ApplyOrientation(scaled, 90, false) ?? throw new InvalidOperationException("ICC 图像方向变换失败");
    RequireCarriedColor(rotated, full.ColorSpace, profile, "方向变换");
    if (rotated.Width != scaled.Height || rotated.Height != scaled.Width)
        throw new InvalidOperationException("方向变换尺寸错误");
    if (rotated.Orientation != 1) throw new InvalidOperationException("方向变换后像素已摆正，方向契约应为 1");
    if (rotated.SourceWidth != scaled.SourceHeight || rotated.SourceHeight != scaled.SourceWidth)
        throw new InvalidOperationException("方向变换后的源几何未随 90° 互换");
    checks++;

    using var cropped = SkiaImageCodec.Crop(rotated, 8, 8, 40, 40) ?? throw new InvalidOperationException("ICC 图像裁剪失败");
    RequireCarriedColor(cropped, full.ColorSpace, profile, "裁剪");
    if (cropped.Width != 40 || cropped.Height != 40) throw new InvalidOperationException("裁剪尺寸错误");
    checks++;

    // 缩略代理的中心色块经缩放后仍与参考一致（缩放不改变色彩语义）
    using var proxyLinear = ColorEngine.ToScRgb16(proxy);
    int px = Math.Clamp((int)Math.Round(ChartPatchCenters()[0].U * proxy.Width), 0, proxy.Width - 1);
    int py = Math.Clamp((int)Math.Round(ChartPatchCenters()[0].V * proxy.Height), 0, proxy.Height - 1);
    var (pr, pg, pb) = BgraAt(proxy, px, py);
    var proxyExpected = ReferenceLinear(pr, pg, pb, ImageColorSpace.DisplayP3);
    var proxyActual = LinearAt(proxyLinear, px, py);
    for (int channel = 0; channel < 3; channel++)
        if (Math.Abs(proxyActual[channel] - proxyExpected[channel]) > 0.01)
            throw new InvalidOperationException($"ICC 代理缩放后色域语义漂移（通道 {channel}）");
    checks++;

    // 通道布局转换（Rgb24 → Bgra32）同样保持色彩语义
    using (var rgb = new DecodedImage([0, 255, 0], 1, 1, 3, PixelEncoding.Rgb24,
               ImageColorSpace.Icc, iccProfile: profile))
    using (var bgra = SkiaImageCodec.ToBgra32(rgb) ?? throw new InvalidOperationException("Rgb24 → Bgra32 失败"))
    {
        RequireCarriedColor(bgra, ImageColorSpace.Icc, profile, "Rgb24 → Bgra32");
        if (bgra.Pixels[0] != 0 || bgra.Pixels[1] != 255 || bgra.Pixels[2] != 0 || bgra.Pixels[3] != 255)
            throw new InvalidOperationException("Rgb24 → Bgra32 通道顺序错误");
    }
    checks++;
}

// ---- 6000×4000 Display P3 大图的代理档同样保留源色域与源几何 ----
using (var large = DecodeToLongEdgeFile(Path.Combine(FindFixturesDirectory(), "jpeg-large", "color-p3-large.jpg"), 1200)
       ?? throw new InvalidOperationException("P3 大图代理解码失败"))
{
    if (large.SourceWidth != 6000 || large.SourceHeight != 4000)
        throw new InvalidOperationException($"P3 大图代理源尺寸错误: {large.SourceWidth}×{large.SourceHeight}");
    if (large.ColorSpace != ImageColorSpace.Icc || large.IccProfile.Length < 128)
        throw new InvalidOperationException($"P3 大图代理丢失 ICC 色域: {large.ColorSpace}");
    using var linear = ColorEngine.ToScRgb16(large);
    int x = Math.Clamp((int)Math.Round(ChartPatchCenters()[0].U * large.Width), 0, large.Width - 1);
    int y = Math.Clamp((int)Math.Round(ChartPatchCenters()[0].V * large.Height), 0, large.Height - 1);
    var (r, g, b) = BgraAt(large, x, y);
    var expected = ReferenceLinear(r, g, b, ImageColorSpace.DisplayP3);
    var actual = LinearAt(linear, x, y);
    for (int channel = 0; channel < 3; channel++)
        if (Math.Abs(actual[channel] - expected[channel]) > 0.01)
            throw new InvalidOperationException($"P3 大图代理色域语义漂移（通道 {channel}）");
    checks++;
}

// ---- 增强预览回到源的展示色域：8 位档保 Display P3 / sRGB / ICC，F16 档保线性 scRGB ----
using (var p3Source = DecodeFile(Path.Combine(jpegDir, "color-p3.jpg")))
using (var p3Srgb8 = ColorEngine.ToSrgb8(p3Source))
{
    foreach (var (encoding, colorSpace, icc, label) in new (PixelEncoding, ImageColorSpace, ReadOnlyMemory<byte>, string)[]
    {
        (PixelEncoding.Rgba8, ImageColorSpace.DisplayP3, default, "8 位 Display P3"),
        (PixelEncoding.Rgba8, ImageColorSpace.Srgb, default, "8 位 sRGB"),
        (PixelEncoding.Rgba8, ImageColorSpace.Icc, p3Source.IccProfile, "8 位 ICC(P3)"),
        (PixelEncoding.RgbaF16, ImageColorSpace.LinearSrgb, default, "F16 线性 scRGB"),
    })
    {
        using var display = ColorEngine.ToDisplayEncoding(p3Srgb8, encoding, colorSpace, icc);
        if (display.Encoding != encoding || display.ColorSpace != colorSpace)
            throw new InvalidOperationException($"展示档构造错误: {display.Encoding}/{display.ColorSpace}");
        using var enhanced = ImageEnhancer.EnhanceToDisplay(display);
        if (enhanced.Encoding != encoding || enhanced.ColorSpace != colorSpace)
            throw new InvalidOperationException($"增强后展示色域漂移（{label}）: {enhanced.Encoding}/{enhanced.ColorSpace}");
        if (colorSpace == ImageColorSpace.Icc && !enhanced.IccProfile.Span.SequenceEqual(p3Source.IccProfile.Span))
            throw new InvalidOperationException("增强后 ICC profile 丢失或改变");
        if (enhanced.Width != display.Width || enhanced.Height != display.Height)
            throw new InvalidOperationException("增强图尺寸改变");
        if (PixelsEqual(enhanced, display))
            throw new InvalidOperationException($"增强未改变像素（{label}）");
        checks++;
    }
}

// ---- 缩略图公共出口：内嵌 JPEG 按真实色域解码、出口统一 sRGB8 ----
var rawRef = new LocalFileRef(Path.Combine(FindFixturesDirectory(), "raw", "A7C01489.ARW"));
using (var thumbnail = await ThumbnailService.GetThumbnailAsync(rawRef, 160))
{
    if (thumbnail == null) throw new InvalidOperationException("RAW 内嵌预览缩略图解码失败");
    if (thumbnail.Encoding != PixelEncoding.Bgra32 || thumbnail.ColorSpace != ImageColorSpace.Srgb)
        throw new InvalidOperationException($"缩略图出口应为 sRGB8，实际 {thumbnail.Encoding}/{thumbnail.ColorSpace}");
    if (Math.Max(thumbnail.Width, thumbnail.Height) < 160)
        throw new InvalidOperationException($"缩略图长边不足: {thumbnail.Width}×{thumbnail.Height}");
    checks++;
}

// ---- 广色域 → sRGB8 出口必须真的做色彩管理（暗部灰阶按源传递函数换算，忽略色域会明显偏亮）----
using (var adobeSource = DecodeFile(Path.Combine(jpegDir, "color-untagged-adobe.jpg")))
using (var adobeSrgb = ColorEngine.ToSrgb8(adobeSource))
{
    if (adobeSrgb.Encoding != PixelEncoding.Bgra32 || adobeSrgb.ColorSpace != ImageColorSpace.Srgb)
        throw new InvalidOperationException($"sRGB 出口应为 Bgra32/sRGB，实际 {adobeSrgb.Encoding}/{adobeSrgb.ColorSpace}");
    int x = (int)Math.Round(0.05 * adobeSource.Width);
    int y = (int)Math.Round(0.8 * adobeSource.Height);
    var source = BgraAt(adobeSource, x, y);
    var exit = BgraAt(adobeSrgb, x, y);
    int expected = ReferenceSrgb8(source.R, ImageColorSpace.AdobeRgb);
    if (Math.Abs(exit.R - expected) > 3)
        throw new InvalidOperationException($"广色域 → sRGB 出口未按源传递函数换算: 实际 {exit.R} 参考 {expected}（源 {source.R}）");
    if (exit.R >= source.R - 3)
        throw new InvalidOperationException($"广色域 → sRGB 出口疑似忽略色域: 源 {source.R} 出口 {exit.R}");
    checks++;
}

// ---- EXIF 互操作标识 R03（无 ICC 的 Adobe RGB）：容器元数据解析与色域推断 ----
var interopMetadata = JpegColorMetadata.Read(BuildInteropJpeg());
if (interopMetadata.IccProfile != null) throw new InvalidOperationException("无 ICC 的 JPEG 不应解出 profile");
if (interopMetadata.InteroperabilityIndex != "R03")
    throw new InvalidOperationException($"InteroperabilityIndex 解析错误: {interopMetadata.InteroperabilityIndex ?? "null"}");
if (interopMetadata.ResolveColorSpace() != ImageColorSpace.AdobeRgb)
    throw new InvalidOperationException("R03 互操作标识应推断为 Adobe RGB");
checks++;
// 无 EXIF、无 ICC 的 JPEG 按 sRGB；非 JPEG 字节不产生色彩元数据
if (JpegColorMetadata.Read([0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x04, 0x00, 0x00, 0xFF, 0xD9]).ResolveColorSpace() != ImageColorSpace.Srgb)
    throw new InvalidOperationException("无标记 JPEG 应默认 sRGB");
if (JpegColorMetadata.Read([0x89, 0x50, 0x4E, 0x47]).HasIccProfile || JpegColorMetadata.Read([0x89, 0x50, 0x4E, 0x47]).ExifColorSpace != null)
    throw new InvalidOperationException("非 JPEG 字节不应解出 JPEG 色彩元数据");
checks++;

// ---- Legacy 未标记 8 位 → sRGB8 必须逐字节等价（DINO/CV 的旧口径不因显式规范化漂移）----
var ramp = new byte[256 * 3 * 4];
for (int i = 0; i < 256; i++)
{
    int grey = i * 4;
    ramp[grey] = (byte)i; ramp[grey + 1] = (byte)i; ramp[grey + 2] = (byte)i; ramp[grey + 3] = 255;
    int mixed = (256 + i) * 4;
    ramp[mixed] = 0; ramp[mixed + 1] = (byte)(255 - i); ramp[mixed + 2] = (byte)i; ramp[mixed + 3] = 255;
    int skewed = (512 + i) * 4;
    ramp[skewed] = (byte)((i * 7) & 0xFF); ramp[skewed + 1] = 0; ramp[skewed + 2] = (byte)(255 - i); ramp[skewed + 3] = 255;
}
using (var legacyRamp = new DecodedImage(ramp, 256, 3, 256 * 4, PixelEncoding.Bgra32, ImageColorSpace.Legacy))
using (var srgbRamp = ColorEngine.ToSrgb8(legacyRamp))
{
    if (srgbRamp.ColorSpace != ImageColorSpace.Srgb || srgbRamp.Encoding != PixelEncoding.Bgra32)
        throw new InvalidOperationException($"Legacy → sRGB8 标签错误: {srgbRamp.Encoding}/{srgbRamp.ColorSpace}");
    if (!PixelsEqual(legacyRamp, srgbRamp))
        throw new InvalidOperationException("Legacy 8 位经 ToSrgb8 后像素发生变化（DINO/CV 旧口径会漂移）");
    checks++;
}

// ---- DINO 模型输入：同一份像素从 Legacy 标成 sRGB 后仍逐字节等价（旧灰阶口径不漂移）----
using (var legacyForModel = new DecodedImage(ramp, 256, 3, 256 * 4, PixelEncoding.Bgra32, ImageColorSpace.Legacy))
using (var srgbForModel = new DecodedImage(ramp, 256, 3, 256 * 4, PixelEncoding.Bgra32, ImageColorSpace.Srgb))
using (var legacyInput = DinoFeatureExtractor.RenderInputImage(legacyForModel))
using (var srgbInput = DinoFeatureExtractor.RenderInputImage(srgbForModel))
{
    if (legacyInput.Encoding != PixelEncoding.Bgra32 || srgbInput.Encoding != PixelEncoding.Bgra32
        || legacyInput.Width != srgbInput.Width || legacyInput.Height != srgbInput.Height)
        throw new InvalidOperationException($"DINO 模型输入形态错误: {legacyInput.Encoding}/{legacyInput.Width} vs {srgbInput.Encoding}/{srgbInput.Width}");
    if (!PixelsEqual(legacyInput, srgbInput))
        throw new InvalidOperationException("DINO 模型输入随色彩标签变化（旧灰阶口径漂移）");
    checks++;
}

// ---- 非 JPEG：sRGB 原色只换曲线不下采样；广色域无法用 ICC 表达时保全到 F16，不先裁 sRGB ----
using (var plainPng = DecodeBytes(BuildPng(64, 48, (200, 60, 40))))
{
    if (plainPng.Encoding != PixelEncoding.Bgra32 || plainPng.ColorSpace != ImageColorSpace.Srgb)
        throw new InvalidOperationException($"无色彩标记的 PNG 应为 8 位 sRGB，实际 {plainPng.Encoding}/{plainPng.ColorSpace}");
    var (r, g, b) = BgraAt(plainPng, 32, 24);
    if (r != 200 || g != 60 || b != 40)
        throw new InvalidOperationException($"无色彩标记的 PNG 像素被改写: ({r},{g},{b})");
    checks++;
}
using (var legacyPng = DecodeBytes(BuildPng(64, 48, (128, 128, 128), legacySrgbPrimariesGamma: true)))
{
    if (legacyPng.Encoding != PixelEncoding.Bgra32 || legacyPng.ColorSpace != ImageColorSpace.Srgb)
        throw new InvalidOperationException($"仅 gAMA/cHRM 的 PNG 应按 8 位 sRGB 交付，实际 {legacyPng.Encoding}/{legacyPng.ColorSpace}");
    int value = BgraAt(legacyPng, 32, 24).R;
    // 2.2 曲线下的 128 编码值换到 sRGB 曲线后是 129；若未做曲线换算会原样留在 128。
    int expected = ReferenceSrgb8FromGamma(128, 2.2);
    if (value != expected)
        throw new InvalidOperationException($"gAMA 2.2 PNG 未换到 sRGB 曲线: 实际 {value} 参考 {expected}");
    checks++;
}
var wideProfile = JpegColorMetadata.Read(File.ReadAllBytes(Path.Combine(jpegDir, "color-adobe.jpg"))).IccProfile;
using (var widePng = DecodeBytes(BuildPng(64, 48, (255, 0, 0), iccProfile: wideProfile)))
{
    if (widePng.Encoding != PixelEncoding.RgbaF16 || widePng.ColorSpace != ImageColorSpace.LinearSrgb)
        throw new InvalidOperationException($"广色域 PNG 应保全到 F16 线性 scRGB，实际 {widePng.Encoding}/{widePng.ColorSpace}");
    var linear = LinearAt(widePng, 32, 24);
    if (linear[0] < 1.35 || Math.Abs(linear[1]) > 0.01)
        throw new InvalidOperationException($"广色域 PNG 被先裁到 sRGB: 线性 R={linear[0]:F3} G={linear[1]:F3}");
    using var rotated = SkiaImageCodec.ApplyOrientation(widePng, 90, false)
        ?? throw new InvalidOperationException("F16 广色域产物方向变换失败");
    if (rotated.Encoding != PixelEncoding.RgbaF16 || rotated.ColorSpace != ImageColorSpace.LinearSrgb
        || rotated.Width != widePng.Height || rotated.Orientation != 1)
        throw new InvalidOperationException($"F16 广色域产物方向变换后契约漂移: {rotated.Encoding}/{rotated.ColorSpace}");
    checks++;
}

// ---- F16 方向变换走整数置换:几何必须与 8 位画布路径逐像素一致(否则细节预览/缩略图方向会错) ----
var pattern = new byte[3 * 2 * 4];
for (int i = 0; i < 6; i++)
{
    pattern[i * 4] = (byte)(i * 30);       // B
    pattern[i * 4 + 1] = (byte)(i * 20);   // G
    pattern[i * 4 + 2] = (byte)(i * 10);   // R
    pattern[i * 4 + 3] = 255;
}
foreach (var (rotation, mirror) in Orientations())
{
    using (var bgraPattern = new DecodedImage(pattern, 3, 2, 12, PixelEncoding.Bgra32, ImageColorSpace.Srgb))
    using (var expectedCanvas = SkiaImageCodec.ApplyOrientation(bgraPattern, rotation, mirror)
               ?? throw new InvalidOperationException($"8 位方向变换失败 r{rotation} m{mirror}"))
    using (var expectedLinear = ColorEngine.ToScRgb16(expectedCanvas))
    using (var extendedPattern = ColorEngine.ToScRgb16(bgraPattern))
    using (var actual = SkiaImageCodec.ApplyOrientation(extendedPattern, rotation, mirror)
               ?? throw new InvalidOperationException($"F16 方向变换失败 r{rotation} m{mirror}"))
    {
        if (actual.Encoding != PixelEncoding.RgbaF16 || actual.Width != expectedLinear.Width || actual.Height != expectedLinear.Height)
            throw new InvalidOperationException($"F16 方向变换形态与 8 位不一致 r{rotation} m{mirror}");
        for (int y = 0; y < actual.Height; y++)
            for (int x = 0; x < actual.Width; x++)
            {
                var got = LinearAt(actual, x, y);
                var want = LinearAt(expectedLinear, x, y);
                for (int channel = 0; channel < 3; channel++)
                    if (Math.Abs(got[channel] - want[channel]) > 0.002)
                        throw new InvalidOperationException(
                            $"F16 方向变换与 8 位几何不一致 r{rotation} m{mirror} ({x},{y}) 通道{channel}: {got[channel]:F4} vs {want[channel]:F4}");
            }
        checks++;
    }
}

// ---- F16 缩放的两条分支(面积平均 / Mitchell)都不许裁掉扩展色域 ----
using (var redSource = BuildP3Red(8, 5))
using (var extendedRed = ColorEngine.ToScRgb16(redSource))
using (var byArea = SkiaImageCodec.ScaleToLongEdge(extendedRed, 4) ?? throw new InvalidOperationException("F16 面积平均缩放失败"))
using (var byMitchell = SkiaImageCodec.ScaleToLongEdge(extendedRed, 6) ?? throw new InvalidOperationException("F16 Mitchell 缩放失败"))
{
    foreach (var (result, label) in new[] { (byArea, "面积平均 8x5→4x2"), (byMitchell, "Mitchell 8x5→6x3") })
    {
        var value = LinearAt(result, result.Width / 2, result.Height / 2);
        if (result.Encoding != PixelEncoding.RgbaF16 || value[0] < 1.2 || value[1] > -0.03 || value[2] > -0.01)
            throw new InvalidOperationException($"F16 缩放({label})裁剪了扩展色域: {value[0]:F4}/{value[1]:F4}/{value[2]:F4}");
        checks++;
    }
}
using (var mixedSource = BuildP3Blocks(8, 4))
using (var mixed = ColorEngine.ToScRgb16(mixedSource))
using (var mixedScaled = SkiaImageCodec.ScaleToLongEdge(mixed, 2) ?? throw new InvalidOperationException("F16 混色缩放失败"))
{
    float min = float.MaxValue, max = float.MinValue;
    for (int y = 0; y < mixedScaled.Height; y++)
        for (int x = 0; x < mixedScaled.Width; x++)
            foreach (var component in LinearAt(mixedScaled, x, y))
            {
                min = Math.Min(min, (float)component);
                max = Math.Max(max, (float)component);
            }
    if (min > -0.005f || max < 1.05f)
        throw new InvalidOperationException($"F16 混色缩放后扩展色域丢失: min={min:F4} max={max:F4}");
    checks++;
}

// ---- 非 JPEG 广色域代理:codec 子采样/精缩之后仍保留 >1(与 JPEG 的 ScaleToLongEdge 同一约束) ----
foreach (var target in new[] { 30, 32 })
{
    using var stream = new MemoryStream(BuildPng(64, 48, (255, 0, 0), iccProfile: wideProfile), writable: false);
    using var proxy = SkiaImageCodec.DecodeToLongEdge(stream, target)
        ?? throw new InvalidOperationException($"非 JPEG 广色域代理({target})解码失败");
    if (proxy.Encoding != PixelEncoding.RgbaF16 || proxy.ColorSpace != ImageColorSpace.LinearSrgb)
        throw new InvalidOperationException($"非 JPEG 代理应为 F16 线性 scRGB，实际 {proxy.Encoding}/{proxy.ColorSpace}");
    if (proxy.SourceWidth != 64 || proxy.SourceHeight != 48)
        throw new InvalidOperationException($"非 JPEG 代理丢失全分辨率源尺寸: {proxy.SourceWidth}×{proxy.SourceHeight}");
    var proxyValue = LinearAt(proxy, proxy.Width / 2, proxy.Height / 2);
    if (proxyValue[0] < 1.35)
        throw new InvalidOperationException($"非 JPEG 广色域代理({target})被裁到 sRGB: R={proxyValue[0]:F3}");
    checks++;
}

Console.WriteLine($"PASS: {checks} color/stride/profile/dimension checks; max linear component error={maxError:F6}; "
    + "真实 JPEG 源色域（sRGB/P3/Adobe/未标记 Adobe）与 ICC 跨代理/缩放/方向/裁剪/缩略图出口全链路保留；"
    + "非 JPEG 同原色换曲线、广色域保全到 F16 线性；Legacy 8 位 → sRGB8 逐字节等价；P3 negative and >1 components preserved.");
return 0;

/// <summary>独立参考传递函数，用于验证 skcms 输出而非重复调用转换实现。</summary>
static double Decode(double value, ImageColorSpace colorSpace)    => colorSpace == ImageColorSpace.AdobeRgb ? Math.Pow(value, 563.0 / 256) : value <= .04045 ? value / 12.92 : Math.Pow((value + .055) / 1.055, 2.4);

/// <summary>独立参考 sRGB 编码值：源色域 → 线性 sRGB → 编码 → 8 位。</summary>
static int ReferenceSrgb8(int encoded, ImageColorSpace space)
{
    double linear = Decode(encoded / 255.0, space);
    double srgb = linear <= 0.0031308 ? 12.92 * linear : 1.055 * Math.Pow(linear, 1 / 2.4) - .055;
    return (int)Math.Round(Math.Clamp(srgb, 0, 1) * 255);
}

/// <summary>独立参考 sRGB 编码值：给定某个纯 gamma 曲线的编码值。</summary>
static int ReferenceSrgb8FromGamma(int encoded, double gamma)
{
    double linear = Math.Pow(encoded / 255.0, gamma);
    double srgb = linear <= 0.0031308 ? 12.92 * linear : 1.055 * Math.Pow(linear, 1 / 2.4) - .055;
    return (int)Math.Round(Math.Clamp(srgb, 0, 1) * 255);
}

static DecodedImage DecodeBytes(byte[] bytes)
{
    using var stream = new MemoryStream(bytes, writable: false);
    return SkiaImageCodec.Decode(stream) ?? throw new InvalidOperationException("内存字节解码失败");
}

/// <summary>
/// 构造 8 位 RGB PNG（无外部素材依赖）：可选 gAMA/cHRM（sRGB 原色、gamma 2.2）或 iCCP 内嵌 ICC，
/// 用于验证非 JPEG 路径的色域判定（Skia 对 PNG/JPEG 只暴露数值色彩空间，拿不到 ICC 原始字节）。
/// </summary>
static byte[] BuildPng(int width, int height, (byte R, byte G, byte B) color, byte[]? iccProfile = null,
    bool legacySrgbPrimariesGamma = false)
{
    var scanlines = new byte[(width * 3 + 1) * height];
    for (int y = 0; y < height; y++)
    {
        int row = y * (width * 3 + 1);
        scanlines[row] = 0; // filter: None
        for (int x = 0; x < width; x++)
        {
            int at = row + 1 + x * 3;
            scanlines[at] = color.R;
            scanlines[at + 1] = color.G;
            scanlines[at + 2] = color.B;
        }
    }

    var png = new List<byte> { 0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A };
    AddPngChunk(png, "IHDR",
    [
        (byte)(width >> 24), (byte)(width >> 16), (byte)(width >> 8), (byte)width,
        (byte)(height >> 24), (byte)(height >> 16), (byte)(height >> 8), (byte)height,
        8, 2, 0, 0, 0, // 8 位、真彩色、deflate、自适应滤波、非隔行
    ]);
    if (legacySrgbPrimariesGamma)
    {
        AddPngChunk(png, "gAMA", [0x00, 0x00, 0xB1, 0x8F]); // 0.45455 → gamma 2.2
        AddPngChunk(png, "cHRM",
        [
            0x00, 0x00, 0x7A, 0x26, 0x00, 0x00, 0x80, 0x84, // 白点 31270,32900
            0x00, 0x00, 0xFA, 0x00, 0x00, 0x00, 0x80, 0xE8, // 红 64000,33000
            0x00, 0x00, 0x75, 0x30, 0x00, 0x00, 0xEA, 0x60, // 绿 30000,60000
            0x00, 0x00, 0x3A, 0x98, 0x00, 0x00, 0x17, 0x70, // 蓝 15000,6000
        ]);
    }
    if (iccProfile != null)
    {
        var body = new List<byte> { (byte)'I', (byte)'C', (byte)'C', 0, 0 }; // 名称 + 压缩方法 deflate
        body.AddRange(ZlibCompress(iccProfile));
        AddPngChunk(png, "iCCP", body.ToArray());
    }
    AddPngChunk(png, "IDAT", ZlibCompress(scanlines));
    AddPngChunk(png, "IEND", []);
    return png.ToArray();
}

static byte[] ZlibCompress(byte[] data)
{
    using var output = new MemoryStream();
    using (var zlib = new System.IO.Compression.ZLibStream(output, System.IO.Compression.CompressionLevel.Optimal, leaveOpen: true))
        zlib.Write(data, 0, data.Length);
    return output.ToArray();
}

static void AddPngChunk(List<byte> png, string type, byte[] body)
{
    png.AddRange([(byte)(body.Length >> 24), (byte)(body.Length >> 16), (byte)(body.Length >> 8), (byte)body.Length]);
    var typeBytes = System.Text.Encoding.ASCII.GetBytes(type);
    png.AddRange(typeBytes);
    png.AddRange(body);
    uint crc = 0xFFFFFFFF;
    foreach (var value in typeBytes) crc = Crc32Step(crc, value);
    foreach (var value in body) crc = Crc32Step(crc, value);
    crc ^= 0xFFFFFFFF;
    png.AddRange([(byte)(crc >> 24), (byte)(crc >> 16), (byte)(crc >> 8), (byte)crc]);
}

static uint Crc32Step(uint crc, byte value)
{
    crc ^= value;
    for (int bit = 0; bit < 8; bit++) crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320u : crc >> 1;
    return crc;
}

/// <summary>构造只含 EXIF 互操作标识 R03 的最小 JPEG（标记段 + EOI），用于校验容器元数据解析。</summary>
static byte[] BuildInteropJpeg()
{
    // TIFF little-endian：IFD0 → Exif IFD(0x8769) → Interop IFD(0xA005) → InteropIndex(0x0001) = "R03"
    const int ifd0 = 8;
    const int exifIfd = ifd0 + 2 + 12 + 4;
    const int interopIfd = exifIfd + 2 + 12 + 4;
    var tiff = new List<byte> { (byte)'I', (byte)'I', 42, 0, (byte)ifd0, 0, 0, 0 };
    // IFD0：单个子 IFD 指针
    tiff.AddRange([0x01, 0x00]);
    tiff.AddRange([0x69, 0x87, 0x04, 0x00, 0x01, 0x00, 0x00, 0x00]);
    tiff.AddRange([(byte)exifIfd, 0, 0, 0]);
    tiff.AddRange([0, 0, 0, 0]);
    // Exif IFD：单个 Interop 指针
    tiff.AddRange([0x01, 0x00]);
    tiff.AddRange([0x05, 0xA0, 0x04, 0x00, 0x01, 0x00, 0x00, 0x00]);
    tiff.AddRange([(byte)interopIfd, 0, 0, 0]);
    tiff.AddRange([0, 0, 0, 0]);
    // Interop IFD：InteroperabilityIndex = "R03\0"（4 字节内联）
    tiff.AddRange([0x01, 0x00]);
    tiff.AddRange([0x01, 0x00, 0x02, 0x00, 0x04, 0x00, 0x00, 0x00]);
    tiff.AddRange([(byte)'R', (byte)'0', (byte)'3', 0]);
    tiff.AddRange([0, 0, 0, 0]);

    var payload = new List<byte> { (byte)'E', (byte)'x', (byte)'i', (byte)'f', 0, 0 };
    payload.AddRange(tiff);
    int length = payload.Count + 2;
    var jpeg = new List<byte> { 0xFF, 0xD8, 0xFF, 0xE1, (byte)(length >> 8), (byte)length };
    jpeg.AddRange(payload);
    jpeg.AddRange([0xFF, 0xD9]);
    return jpeg.ToArray();
}

/// <summary>再靠后的参考矩阵：把某色域的非线性分量换成线性 sRGB（独立于 skcms 的常量）。</summary>
static double[] ReferenceLinear(int r, int g, int b, ImageColorSpace space)
{
    double red = Decode(r / 255.0, space), green = Decode(g / 255.0, space), blue = Decode(b / 255.0, space);
    return space switch
    {
        ImageColorSpace.DisplayP3 =>
        [
            1.224745 * red - .224904 * green,
            -.042058 * red + 1.042081 * green,
            -.019642 * red - .078655 * green + 1.098537 * blue,
        ],
        ImageColorSpace.AdobeRgb =>
        [
            1.398283 * red - .398283 * green,
            green,
            -.042938 * green + 1.042938 * blue,
        ],
        _ => [red, green, blue],
    };
}

/// <summary>色卡色块中心（归一化，见 Tools/generate-native-color-fixtures.py 的布局）。</summary>
static (double U, double V, string Label)[] ChartPatchCenters()
    => [(0.1667, 0.175, "红"), (0.5, 0.175, "绿"), (0.8333, 0.175, "蓝"), (0.1, 0.975, "白")];

/// <summary>自仓库根向上找 Fixtures/（验收素材），找不到时显式失败而不是静默跳过。</summary>
static string FindFixturesDirectory()
{
    var directory = new DirectoryInfo(Directory.GetCurrentDirectory());
    while (directory != null)
    {
        var candidate = Path.Combine(directory.FullName, "Fixtures", "jpeg", "color-srgb.jpg");
        if (File.Exists(candidate)) return Path.Combine(directory.FullName, "Fixtures");
        directory = directory.Parent;
    }
    throw new InvalidOperationException("找不到 Fixtures/ 验收素材，请在仓库内运行该工具");
}

static DecodedImage DecodeFile(string path)
{
    using var stream = File.OpenRead(path);
    return SkiaImageCodec.Decode(stream) ?? throw new InvalidOperationException($"解码失败: {path}");
}

static DecodedImage? DecodeToLongEdgeFile(string path, int target)
{
    using var stream = File.OpenRead(path);
    return SkiaImageCodec.DecodeToLongEdge(stream, target);
}

static void RequireSourceColor(DecodedImage image, ImageColorSpace expected, string name)
{
    if (image.Encoding != PixelEncoding.Bgra32)
        throw new InvalidOperationException($"{name} 解码编码应为 Bgra32，实际 {image.Encoding}");
    if (image.ColorSpace != expected)
        throw new InvalidOperationException($"{name} 源色域应为 {expected}，实际 {image.ColorSpace}");
}

static void RequireCarriedColor(DecodedImage image, ImageColorSpace expected, byte[] profile, string step)
{
    if (image.Encoding != PixelEncoding.Bgra32)
        throw new InvalidOperationException($"{step} 后编码漂移: {image.Encoding}");
    if (image.ColorSpace != expected)
        throw new InvalidOperationException($"{step} 后色彩标签漂移: {image.ColorSpace}");
    if (expected == ImageColorSpace.Icc && !image.IccProfile.Span.SequenceEqual(profile))
        throw new InvalidOperationException($"{step} 后 ICC profile 丢失或改变");
}

static (int R, int G, int B) BgraAt(DecodedImage image, int x, int y)
{
    if (image.Encoding != PixelEncoding.Bgra32) throw new InvalidOperationException($"BgraAt 需要 Bgra32，实际 {image.Encoding}");
    int at = y * image.Stride + x * 4;
    return (image.Pixels[at + 2], image.Pixels[at + 1], image.Pixels[at]);
}

static double[] LinearAt(DecodedImage image, int x, int y)
{
    var halves = MemoryMarshal.Cast<byte, Half>(image.Pixels.AsSpan(y * image.Stride + x * 8, 8));
    return [(double)halves[0], (double)halves[1], (double)halves[2]];
}

/// <summary>取放大/缩放后色块中心（红块）的线性值，供三色域差异断言使用。</summary>
static double[] RedLinear(DecodedImage image)
{
    using var linear = ColorEngine.ToScRgb16(image);
    int x = Math.Clamp((int)Math.Round(ChartPatchCenters()[0].U * image.Width), 0, image.Width - 1);
    int y = Math.Clamp((int)Math.Round(ChartPatchCenters()[0].V * image.Height), 0, image.Height - 1);
    return LinearAt(linear, x, y);
}

static bool PixelsEqual(DecodedImage left, DecodedImage right)
{
    if (left.Encoding != right.Encoding || left.Width != right.Width || left.Height != right.Height) return false;
    int stride = left.Width * DecodedImage.BytesPerPixel(left.Encoding);
    for (int y = 0; y < left.Height; y++)
    {
        var leftRow = left.Pixels.AsSpan(y * left.Stride, stride);
        var rightRow = right.Pixels.AsSpan(y * right.Stride, stride);
        if (!leftRow.SequenceEqual(rightRow)) return false;
    }
    return true;
}

/// <summary>八种方向组合(顺时针旋转 × 水平镜像),与 <see cref="SkiaImageCodec.ApplyOrientation"/> 的参数域一致。</summary>
static (int Rotation, bool Mirror)[] Orientations()
    => [(0, false), (0, true), (90, false), (90, true), (180, false), (180, true), (270, false), (270, true)];

/// <summary>整幅 Display P3 红(8 位码值):转 F16 线性后应同时有 &gt;1 与负分量。</summary>
static DecodedImage BuildP3Red(int width, int height)
    => new(Enumerable.Repeat(new byte[] { 0, 0, 255, 255 }, width * height).SelectMany(pixel => pixel).ToArray(),
        width, height, width * 4, PixelEncoding.Bgra32, ImageColorSpace.DisplayP3);

/// <summary>左半 P3 红、右半 P3 绿:整块缩放后应同时存在负分量与 &gt;1 分量(证明没被钳到 [0,1])。</summary>
static DecodedImage BuildP3Blocks(int width, int height)
{
    var pixels = new byte[width * height * 4];
    for (int y = 0; y < height; y++)
        for (int x = 0; x < width; x++)
        {
            int at = (y * width + x) * 4;
            bool red = x < width / 2;
            pixels[at] = (byte)(red ? 0 : 255);      // B
            pixels[at + 1] = (byte)(red ? 0 : 255);  // G
            pixels[at + 2] = (byte)(red ? 255 : 0);  // R
            pixels[at + 3] = 255;
        }
    return new DecodedImage(pixels, width, height, width * 4, PixelEncoding.Bgra32, ImageColorSpace.DisplayP3);
}
