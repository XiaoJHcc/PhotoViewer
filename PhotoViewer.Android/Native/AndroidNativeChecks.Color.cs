using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using Android.Graphics;
using Java.Nio;
using PhotoViewer.Android.Core;
using PhotoViewer.Core.Color;
using PhotoViewer.Core.Image;
using IOPath = System.IO.Path;
using AndroidColorSpace = Android.Graphics.ColorSpace;

namespace PhotoViewer.Android.Native;

/// <summary>
/// <c>--es pv-check color</c>（验收专用）：Android 主图色彩管理的持久回归，全部断言读真实渲染结果
/// （重建过的 EGL 窗口表面 + 默认帧缓冲回读）或真实解码产物，不靠截图目测。
/// 覆盖：passthrough 优先路径字节一致、强制标准 P3（sRGB 输出帧缓冲编码，128 灰绕过输出线性化 = 188）、
/// 强制 sRGB 表面的显式转换、sRGB 源在 P3 表面必须转换、P3↔sRGB 重复重建与预乘数据保持、
/// Android Bitmap 原始 P3 存储直读、导航图 sRGB 出口与方向、真实夹具（JPEG 四类 + HEIF + 大图）
/// 对照共享 <see cref="SkiaImageCodec"/> 参考解码，以及增强输出的标签与像素。
/// </summary>
internal static partial class AndroidNativeChecks
{
    private sealed class CountedHeifFile(PhotoViewer.Core.Storage.IFileRef inner) : PhotoViewer.Core.Storage.IFileRef
    {
        internal int Opens { get; private set; }
        public string Name => inner.Name;
        public Uri Path => inner.Path;
        public PhotoViewer.Core.Storage.StorageItemId StorageId => inner.StorageId;
        public string? TryGetLocalPath() => inner.TryGetLocalPath();
        public Task<Stream> OpenReadAsync(CancellationToken cancellationToken = default)
        {
            Opens++;
            return inner.OpenReadAsync(cancellationToken);
        }
        public Task<PhotoViewer.Core.Storage.StorageItemBasicProperties> GetBasicPropertiesAsync(CancellationToken cancellationToken = default)
            => inner.GetBasicPropertiesAsync(cancellationToken);
        public Task<PhotoViewer.Core.Storage.IFolderRef?> GetParentAsync(CancellationToken cancellationToken = default)
            => inner.GetParentAsync(cancellationToken);
    }

    /// <summary>输出帧缓冲的 sRGB 编码（<c>GL_SRGB</c>）：写入时 GL 负责线性→编码。</summary>
    private const int GlFramebufferSrgb = 0x8C40;
    /// <summary>输出帧缓冲的线性编码（<c>GL_LINEAR</c>）：像素值原样落盘，surfacebook 直通。</summary>
    private const int GlFramebufferLinear = 0x2601;
    /// <summary>EGL 表面的 sRGB 色彩空间（<c>EGL_GL_COLORSPACE_SRGB_KHR</c>）。</summary>
    private const int EglColorspaceSrgb = 0x3089;
    /// <summary>EGL 表面的 Display P3 色彩空间（<c>EGL_EXT_gl_colorspace_display_p3</c>）。</summary>
    private const int EglColorspaceDisplayP3 = 0x3363;
    /// <summary>EGL 表面的 Display P3 passthrough 色彩空间（<c>EGL_EXT_gl_colorspace_display_p3_passthrough</c>）。</summary>
    private const int EglColorspaceDisplayP3Passthrough = 0x3490;

    /// <summary>合成色块图边长（2×2 均匀象限，每象限 128×128，适应态下完整落在主图分区内）。</summary>
    private const int SyntheticImageSize = 256;
    /// <summary>源编码与表面编码一致时的回读容差（逐字节直通，只允许驱动抖动带来的 1）。</summary>
    private const int ByteExactTolerance = 1;
    /// <summary>经过一次色域转换后的回读容差。</summary>
    private const int ConvertTolerance = 2;
    /// <summary>真实夹具色块与共享参考解码的对照容差。</summary>
    private const int FixtureTolerance = 3;
    /// <summary>代理档长边（与展示解码器代理口径一致）。</summary>
    private const int FixtureProxyLongEdge = 600;
    /// <summary>128 灰绕过输出线性化后的预期帧缓冲字节（sRGB 传递函数对 128/255 的编码 ≈ 188）。</summary>
    private const int GrayDoubleEncoded = 188;

    /// <summary>合成色块图的四个象限（RGBA，预乘 alpha，标签由调用方给定）。</summary>
    private static readonly (byte R, byte G, byte B, byte A)[] QuadColors =
    [
        (128, 128, 128, 255),   // 128 灰：输出传递函数是否重复编码的唯一判据
        (200, 80, 40, 255),
        (40, 160, 90, 255),
        (100, 50, 20, 128),     // 半透明（预乘）：转换/上传不得还原成非预乘
    ];

    /// <summary>合成图代际基数：与 <see cref="PhotoLoadSession.Generation"/> 不重叠，保证走切图语义。</summary>
    private static int _colorGeneration = 900000;

    /// <summary>
    /// 仍挂在主图上的合成图：本流程为它额外持有一份租约。主图只持实例引用、不持缓冲，
    /// 各步骤自己那份 <c>using</c> 租约一旦归还，主图就会指向已释放的实例（下一步重建表面重传即读空缓冲），
    /// 所以「上屏」与「释放」必须经这里配对：新图接管后再归还前一份，收尾先切回会话图再归还最后一份。
    /// </summary>
    private static DecodedImage? _colorAttached;

    /// <summary>色彩回归入口：先等主屏与真实表面就绪，再逐组重建表面并回读；结束时无论成败都还原设备原生表面、会话图与租约。</summary>
    private static async Task RunColorChecksAsync()
    {
        var root = _root!;
        var session = _session!;
        await WaitForScreenAsync();
        await WaitAsync(() => root.Photo.SurfaceReadyForChecks, "主图 EGL 表面就绪", 20000);

        var photo = root.Photo;
        var restoreImage = session.Image;
        int restoreGeneration = session.Generation;
        Check("color 设备声明 Display P3", AndroidDisplayCapabilities.DisplaySupportsP3,
            $"displayWide={AndroidDisplayCapabilities.DisplaySupportsP3} surface={photo.SurfaceIsP3} forceSrgb={AndroidDisplayCapabilities.ForceSrgb}");

        try
        {
            await RunStepAsync("color passthrough", CheckPassthroughAsync);
            await RunStepAsync("color 强制标准 P3", CheckForcedStandardP3Async);
            await RunStepAsync("color 强制 sRGB 表面", CheckForcedSrgbAsync);
            await RunStepAsync("color sRGB 源在 P3 表面", CheckSrgbSourceOnP3Async);
            await RunStepAsync("color 重复重建与预乘", CheckRepeatedRecreateAsync);
            await RunStepAsync("color Bitmap 原始 P3 存储", CheckBitmapRawP3Async);
            await RunStepAsync("color 导航图", CheckNavigationAsync);
            await RunStepAsync("color 夹具解码", CheckColorFixturesAsync);
            await RunStepAsync("color HEIF 回落色彩", CheckHeifFallbackColorAsync);
            await RunStepAsync("color 增强输出", CheckEnhanceAsync);
        }
        finally
        {
            // 表面还原与会话图还原分开兜底：表面重建失败也要先把主图切回会话图，否则合成图的租约无法归还。
            try
            {
                await RecreatePhotoSurfaceAsync(true);
            }
            catch (Exception exception)
            {
                Check("color 表面还原", false, exception.GetType().Name + "：" + exception.Message);
            }
            try
            {
                await RestoreColorSessionImageAsync(restoreImage, restoreGeneration);
                Check("color 会话图还原", ReferenceEquals(session.Image, restoreImage),
                    $"restored={(restoreImage == null ? "无图" : $"{restoreImage.Width}x{restoreImage.Height}")} gen={session.Generation}");
            }
            catch (Exception exception)
            {
                Check("color 会话图还原", false, exception.GetType().Name + "：" + exception.Message);
            }
        }
    }

    // ── 合成色块图与表面 ──────────────────────────────────────────────────────

    /// <summary>
    /// passthrough 优先路径：源编码与表面编码相同，像素值必须原样落屏——128 灰仍是 128、彩色与半透明预乘不变。
    /// 非 P3 设备（模拟器）拿到的是 sRGB 表面，断言按 sRGB 源执行，同时单独记录 P3 表面未达成的失败。
    /// </summary>
    private static async Task CheckPassthroughAsync()
    {
        var photo = _root!.Photo;
        await RecreatePhotoSurfaceAsync(true, allowPassthrough: true);
        Check("color passthrough 表面为 Display P3", photo.SurfaceIsP3,
            $"P3={photo.SurfaceIsP3} colorspace={ColorspaceName(photo)} framebuffer={FramebufferName(photo)}");

        var target = SurfaceImageColorSpace();
        using var source = CreateColorQuadImage(target) ?? throw new InvalidOperationException("合成色块图创建失败");
        await ShowColorSyntheticAsync(source);
        var actual = ReadColorQuadrants();
        Check("color passthrough 128 灰回读 = 128", ColorDeltaRgb(actual[0], QuadColors[0]) <= ByteExactTolerance,
            $"实测={DescribeColor(actual[0])} 源={DescribeColor(QuadColors[0])} 表面={target}");
        Check("color passthrough 彩色回读一致",
            ColorDeltaRgb(actual[1], QuadColors[1]) <= ByteExactTolerance && ColorDeltaRgb(actual[2], QuadColors[2]) <= ByteExactTolerance,
            $"Q1 实测={DescribeColor(actual[1])} 源={DescribeColor(QuadColors[1])}；Q2 实测={DescribeColor(actual[2])} 源={DescribeColor(QuadColors[2])}");
        Check("color passthrough 半透明预乘一致", ColorDeltaRgb(actual[3], QuadColors[3]) <= ByteExactTolerance,
            $"实测={DescribeColor(actual[3])} 源={DescribeColor(QuadColors[3])}（RGB 必须是预乘值，不得被还原；窗口表面无 alpha 位，alpha 不参与判定）");
    }

    /// <summary>
    /// 强制标准 P3 表面（不允许 passthrough）走 sRGB 输出帧缓冲编码：着色器负责把源编码线性化，
    /// 故绕过输出线性化回读得到重复编码后的 188，正常回读仍是 128——原先「主图整体发白」的故障在此明确可复现。
    /// </summary>
    private static async Task CheckForcedStandardP3Async()
    {
        var photo = _root!.Photo;
        await RecreatePhotoSurfaceAsync(true, allowPassthrough: false);
        Check("color 强制标准 P3 表面", photo.SurfaceIsP3,
            $"P3={photo.SurfaceIsP3} 声明={AndroidDisplayCapabilities.DisplaySupportsP3} colorspace={ColorspaceName(photo)}");
        Check("color 强制标准 P3 使用 sRGB 输出帧缓冲", photo.FramebufferEncodingForChecks(GlFramebufferSrgb),
            $"framebuffer={FramebufferName(photo)}（期望 GL_SRGB=0x8C40）");
        Check("color 强制标准 P3 的 EGL 色彩空间", photo.EglColorspaceForChecks(EglColorspaceDisplayP3),
            $"colorspace={ColorspaceName(photo)}（期望 0x{EglColorspaceDisplayP3:X}）");

        var target = SurfaceImageColorSpace();
        using var source = CreateColorQuadImage(target) ?? throw new InvalidOperationException("合成色块图创建失败");
        await ShowColorSyntheticAsync(source);
        var raw = ReadColorQuadrants(bypassOutputTransfer: true);
        Check($"color 强制标准 P3 128 灰绕过输出线性化 = {GrayDoubleEncoded}",
            Math.Abs(raw[0].R - GrayDoubleEncoded) <= ConvertTolerance
            && Math.Abs(raw[0].G - GrayDoubleEncoded) <= ConvertTolerance
            && Math.Abs(raw[0].B - GrayDoubleEncoded) <= ConvertTolerance,
            $"实测={DescribeColor(raw[0])} 帧缓冲={FramebufferName(photo)}（正常回读=128 表示没有重复编码）");
        var normalized = ReadColorQuadrants();
        Check("color 强制标准 P3 128 灰正常回读 = 128", ColorDeltaRgb(normalized[0], QuadColors[0]) <= ByteExactTolerance,
            $"实测={DescribeColor(normalized[0])} 源={DescribeColor(QuadColors[0])}");
        Check("color 强制标准 P3 彩色回读一致",
            ColorDeltaRgb(normalized[1], QuadColors[1]) <= ConvertTolerance && ColorDeltaRgb(normalized[2], QuadColors[2]) <= ConvertTolerance,
            $"Q1 实测={DescribeColor(normalized[1])} 源={DescribeColor(QuadColors[1])}；Q2 实测={DescribeColor(normalized[2])} 源={DescribeColor(QuadColors[2])}");
    }

    /// <summary>
    /// 强制 sRGB 表面：Display P3 源上传时必须显式转换到 sRGB（回读 = <see cref="ColorEngine"/> 的转换结果），
    /// 且转换只发生在上传产物上——源图像的字节、色彩标签与待应用方向都不许被改。
    /// </summary>
    private static async Task CheckForcedSrgbAsync()
    {
        var photo = _root!.Photo;
        await RecreatePhotoSurfaceAsync(false);
        Check("color 强制 sRGB 表面", !photo.SurfaceIsP3, $"P3={photo.SurfaceIsP3} colorspace={ColorspaceName(photo)}");

        using var source = CreateColorQuadImage(ImageColorSpace.DisplayP3) ?? throw new InvalidOperationException("合成色块图创建失败");
        var sourceBefore = (byte[])source.Pixels.Clone();
        int orientationBefore = source.Orientation;
        await ShowColorSyntheticAsync(source);

        var actual = ReadColorQuadrants();
        var expected = ExpectedColorQuadrants(source, ImageColorSpace.Srgb);
        Check("color P3 源上传 sRGB 表面显式转换", QuadrantsWithin(actual, expected, ConvertTolerance),
            $"实测=[{DescribeColor(actual[0])},{DescribeColor(actual[1])},{DescribeColor(actual[2])}] "
            + $"期望=[{DescribeColor(expected[0])},{DescribeColor(expected[1])},{DescribeColor(expected[2])}]");
        Check("color P3 源上传后源字节与标签未改",
            ColorBytesEqual(sourceBefore, source.Pixels) && source.ColorSpace == ImageColorSpace.DisplayP3 && source.Orientation == orientationBefore,
            $"标签={source.ColorSpace} 方向={source.Orientation} 字节一致={ColorBytesEqual(sourceBefore, source.Pixels)}");

        AndroidUploadStats.Reset();
        using var statsP3 = CreateColorQuadImage(ImageColorSpace.DisplayP3) ?? throw new InvalidOperationException("合成色块图创建失败");
        await ShowColorSyntheticAsync(statsP3);
        var convertedUpload = AndroidUploadStats.LatestForChecks;
        Check("color P3→sRGB 上传记录真实转换",
            convertedUpload is { Converted: true, CacheHit: false, ConvertMs: >= 0 }, DescribeUpload(convertedUpload));
        Check("color P3→sRGB 提交墙钟覆盖分段",
            convertedUpload is { } uploaded && SubmitCoversSegments(uploaded), DescribeUpload(convertedUpload));

        using var statsSrgb = CreateColorQuadImage(ImageColorSpace.Srgb) ?? throw new InvalidOperationException("合成色块图创建失败");
        await ShowColorSyntheticAsync(statsSrgb);
        var directUpload = AndroidUploadStats.LatestForChecks;
        Check("color sRGB 源上传 sRGB 表面零转换",
            directUpload is { Converted: false, ConvertMs: 0, CacheHit: false }, DescribeUpload(directUpload));

        await ShowColorSyntheticAsync(statsP3);
        var hitUpload = AndroidUploadStats.LatestForChecks;
        Check("color 跨色域源回切命中缓存不重转",
            hitUpload is { CacheHit: true, TotalMs: 0, Converted: false, SubmitMs: >= 0 }, DescribeUpload(hitUpload));
    }

    /// <summary>反过来：sRGB 源在 P3 表面上必须转换到 P3，不能把 sRGB 码值直接喂给 P3 表面。</summary>
    private static async Task CheckSrgbSourceOnP3Async()
    {
        var photo = _root!.Photo;
        await RecreatePhotoSurfaceAsync(true, allowPassthrough: false);
        if (!photo.SurfaceIsP3)
        {
            Check("color sRGB 源在 P3 表面必须转换", false,
                $"设备/窗口未提供 P3 表面（P3={photo.SurfaceIsP3} 声明={AndroidDisplayCapabilities.DisplaySupportsP3}），无法验证反向转换");
            return;
        }

        using var source = CreateColorQuadImage(ImageColorSpace.Srgb) ?? throw new InvalidOperationException("合成色块图创建失败");
        await ShowColorSyntheticAsync(source);
        var actual = ReadColorQuadrants();
        var expected = ExpectedColorQuadrants(source, ImageColorSpace.DisplayP3);
        Check("color sRGB 源在 P3 表面必须转换", QuadrantsWithin(actual, expected, ConvertTolerance),
            $"实测=[{DescribeColor(actual[0])},{DescribeColor(actual[1])},{DescribeColor(actual[2])}] "
            + $"期望=[{DescribeColor(expected[0])},{DescribeColor(expected[1])},{DescribeColor(expected[2])}]");
        Check("color sRGB 源在 P3 表面确实改变码值",
            ColorDeltaRgb(actual[1], QuadColors[1]) > ByteExactTolerance || ColorDeltaRgb(actual[2], QuadColors[2]) > ByteExactTolerance,
            $"Q1 实测={DescribeColor(actual[1])} 源={DescribeColor(QuadColors[1])}；未转换说明仍在直通 sRGB 码值");
    }

    /// <summary>
    /// P3 → sRGB → P3 重复重建：每次重建后表面色域都与请求一致、同一组源码值回读一致，
    /// 末次 P3 回读必须与首次相同；半透明象限全程保持预乘值（不还原、不与背景混合）。
    /// </summary>
    private static async Task CheckRepeatedRecreateAsync()
    {
        var photo = _root!.Photo;
        var plan = new (bool AllowP3, bool ExpectP3, string Name)[]
        {
            (true, true, "P3"),
            (false, false, "sRGB"),
            (true, true, "P3"),
        };
        (byte R, byte G, byte B, byte A) firstP3 = default, lastP3 = default;
        bool haveP3 = false;
        foreach (var step in plan)
        {
            await RecreatePhotoSurfaceAsync(step.AllowP3, allowPassthrough: false);
            bool matches = photo.SurfaceIsP3 == step.ExpectP3;
            // 非 P3 设备无法提供 P3 表面：记录为失败，但仍按实际表面核对码值直通性。
            Check($"color 重复重建 {step.Name} 表面色域", matches,
                $"期望P3={step.ExpectP3} 实测P3={photo.SurfaceIsP3} colorspace={ColorspaceName(photo)}");

            var target = SurfaceImageColorSpace();
            using var source = CreateColorQuadImage(target) ?? throw new InvalidOperationException("合成色块图创建失败");
            await ShowColorSyntheticAsync(source);
            var actual = ReadColorQuadrants();
            Check($"color 重复重建 {step.Name} 色块直通", QuadrantsWithin(actual, QuadColorTuples(), ByteExactTolerance),
                $"实测=[{DescribeColor(actual[0])},{DescribeColor(actual[1])},{DescribeColor(actual[2])}]");
            Check($"color 重复重建 {step.Name} 预乘保持", ColorDeltaRgb(actual[3], QuadColors[3]) <= ByteExactTolerance,
                $"实测={DescribeColor(actual[3])} 源={DescribeColor(QuadColors[3])}");

            if (photo.SurfaceIsP3)
            {
                if (!haveP3) { firstP3 = actual[1]; haveP3 = true; }
                lastP3 = actual[1];
            }
        }
        if (haveP3)
            Check("color P3→sRGB→P3 往返后码值一致", ColorDeltaRgb(firstP3, lastP3) <= ByteExactTolerance,
                $"首次={DescribeColor(firstP3)} 末次={DescribeColor(lastP3)}");
        else
            Check("color P3→sRGB→P3 往返后码值一致", false, "设备未提供 P3 表面，往返未发生");

        CheckPremultipliedConversion();
    }

    /// <summary>预乘 alpha 在展示编码转换中的口径：alpha 不被改写，RGB 仍是预乘值（各通道 ≤ alpha）。</summary>
    private static void CheckPremultipliedConversion()
    {
        using var source = CreateColorQuadImage(ImageColorSpace.Srgb) ?? throw new InvalidOperationException("合成色块图创建失败");
        using var toP3 = ColorEngine.ToDisplayEncoding(source, PixelEncoding.Rgba8, ImageColorSpace.DisplayP3);
        using var toSrgb = ColorEngine.ToDisplayEncoding(toP3, PixelEncoding.Rgba8, ImageColorSpace.Srgb);
        var premulP3 = ColorPixelAt(toP3, SyntheticImageSize * 3 / 4, SyntheticImageSize * 3 / 4);
        var premulSrgb = ColorPixelAt(toSrgb, SyntheticImageSize * 3 / 4, SyntheticImageSize * 3 / 4);
        bool ok = premulP3.A == 128 && premulSrgb.A == 128
            && premulP3.R <= premulP3.A && premulP3.G <= premulP3.A && premulP3.B <= premulP3.A
            && premulSrgb.R <= premulSrgb.A && premulSrgb.G <= premulSrgb.A && premulSrgb.B <= premulSrgb.A;
        Check("color 预乘 alpha 在色域转换中保持", ok,
            $"P3={DescribeColor(premulP3)} sRGB={DescribeColor(premulSrgb)} 源={DescribeColor(QuadColors[3])}");
    }

    // ── Bitmap 原始 P3 存储 ───────────────────────────────────────────────────

    /// <summary>
    /// Android Bitmap 的 Display P3 原始存储：用 <c>CopyPixelsFromBuffer</c> 写入原始字节（不走 SetPixels 的非预乘口径），
    /// 共享 <see cref="AndroidBitmapPixels.ToRgba8"/> 必须字节恒等——P3 码值不得在 Bitmap↔RGBA8 之间被隐式转换或反预乘。
    /// </summary>
    private static Task CheckBitmapRawP3Async()
    {
        // DisplayP3 位图标签与其读取 API 都是 API 26+：低版本没有 ColorSpace，如实记失败而不是跳过。
        if (!OperatingSystem.IsAndroidVersionAtLeast(26))
        {
            Check("color Bitmap P3 原始存储", false, "系统低于 API 26，没有 ColorSpace");
            return Task.CompletedTask;
        }

        var colorSpace = AndroidColorSpace.Get(AndroidColorSpace.Named.DisplayP3!)!;
        CheckBitmapRawCase(colorSpace: colorSpace, hasAlpha: false, value: (200, 80, 40, 255), label: "不透明");
        CheckBitmapRawCase(colorSpace, hasAlpha: true, value: (100, 50, 20, 128), label: "半透明预乘");
        return Task.CompletedTask;
    }

    /// <summary>单个 Bitmap 用例：原始 int 往返 → Bitmap 标签确为 P3 → 共享 ToRgba8 字节恒等。</summary>
    /// <param name="colorSpace">位图色彩空间（Display P3）。</param>
    /// <param name="hasAlpha">是否带 alpha 通道。</param>
    /// <param name="value">写入的 RGBA 字节（半透明用例为预乘值）。</param>
    /// <param name="label">报告用标签。</param>
    [SupportedOSPlatform("android26.0")]
    private static void CheckBitmapRawCase(AndroidColorSpace colorSpace, bool hasAlpha, (byte R, byte G, byte B, byte A) value, string label)
    {
        const int width = 2, height = 2;
        int packed = PackRawRgba(value.R, value.G, value.B, value.A);
        using var bitmap = Bitmap.CreateBitmap(width, height, Bitmap.Config.Argb8888!, hasAlpha, colorSpace)
            ?? throw new InvalidOperationException("CreateBitmap 失败");
        using (var input = IntBuffer.Allocate(width * height) ?? throw new InvalidOperationException("IntBuffer 分配失败"))
        {
            input.Put([packed, packed, packed, packed]);
            input.Rewind();
            bitmap.CopyPixelsFromBuffer(input);
        }

        var raw = new int[width * height];
        using (var output = IntBuffer.Allocate(raw.Length) ?? throw new InvalidOperationException("IntBuffer 分配失败"))
        {
            bitmap.CopyPixelsToBuffer(output);
            output.Rewind();
            output.Get(raw);
        }
        Check($"color Bitmap P3 {label} 原始寄存器值", raw[0] == packed && raw[3] == packed,
            $"写入=0x{packed:X8} 回读=0x{raw[0]:X8}/0x{raw[3]:X8} 标签={(bitmap.ColorSpace == null ? "null" : bitmap.ColorSpace.Name)}");
        Check($"color Bitmap P3 {label} 携带 P3 标签", bitmap.ColorSpace != null && !bitmap.ColorSpace.IsSrgb,
            $"colorspace={(bitmap.ColorSpace == null ? "null" : bitmap.ColorSpace.Name)}");

        using var decoded = AndroidBitmapPixels.ToRgba8(bitmap, ImageColorSpace.DisplayP3, width, height, 1);
        (byte R, byte G, byte B, byte A) actual = decoded == null ? default : ColorPixelAt(decoded, 0, 0);
        Check($"color Bitmap P3 {label} 直读字节恒等", decoded != null && ColorDelta(actual, value) == 0,
            decoded == null ? "ToRgba8 返回 null" : $"实测={DescribeColor(actual)} 期望={DescribeColor(value)}");
    }

    // ── 导航图 ────────────────────────────────────────────────────────────────

    /// <summary>
    /// 缩放指示器出口：先经真实展示解码器异步解一个 P3 源（无夹具时用合成 P3 图），
    /// 导航位图必须是 sRGB 口径且色块与共享 <see cref="ColorEngine.ToAnalysisSrgb8(DecodedImage)"/> 参考一致；
    /// 另用 120×60 + 方向 6 的图核对导航图已转成显示朝向（长宽互换）。
    /// </summary>
    private static async Task CheckNavigationAsync()
    {
        var root = _root!;
        var decoder = CurrentPresentationDecoder();
        Check("color 导航图展示解码器可用", decoder != null, decoder?.GetType().Name ?? "未注入");

        DecodedImage? decoded = null;
        string sourceName;
        string? path = ResolveColorFixture(ColorFixtureNames.P3Jpeg);
        try
        {
            if (decoder != null && path != null)
            {
                decoded = await decoder.DecodeAsync(new AndroidFileRef(path), FixtureProxyLongEdge, PixelEncoding.Rgba8, CancellationToken.None);
                sourceName = IOPath.GetFileName(path);
            }
            else
            {
                sourceName = "合成 P3 色块图";
            }
            decoded ??= CreateColorQuadImage(ImageColorSpace.DisplayP3);
            sourceName = decoded == null ? sourceName : $"{sourceName}({decoded.Width}x{decoded.Height}, {decoded.ColorSpace})";
            if (decoded == null)
            {
                Check("color 导航图源", false, "既无 P3 夹具也无合成图");
                return;
            }

            double density = AndroidUiTheme.Density(root.Context!);
            using var bitmap = await AndroidNavigationDecoder.CreateFromDisplayAsync(decoded, density, CancellationToken.None);
            Check("color 导航图出图", bitmap != null, $"源={sourceName} 长边={Math.Max(decoded.Width, decoded.Height)}");
            if (bitmap != null)
            {
                bool srgbTag;
                string spaceName;
                if (OperatingSystem.IsAndroidVersionAtLeast(26))
                {
                    srgbTag = BitmapIsSrgbTagged(bitmap);
                    spaceName = BitmapColorSpaceLabel(bitmap);
                }
                else
                {
                    // 低版本位图没有 ColorSpace，系统按 sRGB 解释码值。
                    srgbTag = true;
                    spaceName = "API<26（无 ColorSpace，按 sRGB）";
                }
                Check("color 导航图位图为 sRGB 口径", srgbTag,
                    $"colorspace={spaceName} {bitmap.Width}x{bitmap.Height}");
                using var analysis = ColorEngine.ToAnalysisSrgb8(decoded);
                var samples = FindFlatSamples(analysis);
                int worst = 0;
                string worstAt = "无平坦色块";
                var packed = ReadBitmapPacked(bitmap);
                foreach (var sample in samples)
                {
                    int x = Math.Clamp((int)Math.Round(sample.U * (bitmap.Width - 1)), 0, bitmap.Width - 1);
                    int y = Math.Clamp((int)Math.Round(sample.V * (bitmap.Height - 1)), 0, bitmap.Height - 1);
                    var actual = UnpackRawRgba(packed[y * bitmap.Width + x]);
                    var want = ColorPixelAt(analysis, sample.U, sample.V);
                    int delta = ColorDelta(actual, want);
                    if (delta <= worst) continue;
                    worst = delta;
                    worstAt = $"({sample.U:0.###},{sample.V:0.###}) 实测={DescribeColor(actual)} 参考={DescribeColor(want)}";
                }
                Check("color 导航图像素与 sRGB 参考一致", samples.Count > 0 && worst <= FixtureTolerance,
                    samples.Count == 0 ? "参考图中找不到平坦色块" : $"平坦色块={samples.Count} 最大偏差={worst} 位置={worstAt}");
            }
        }
        finally
        {
            decoded?.Dispose();
        }

        using var rotated = CreateColorQuadImage(ImageColorSpace.Srgb, 120, 60, 6);
        if (rotated == null)
        {
            Check("color 导航图方向", false, "带方向的合成图创建失败");
            return;
        }
        using var oriented = await AndroidNavigationDecoder.CreateFromDisplayAsync(rotated, AndroidUiTheme.Density(root.Context!), CancellationToken.None);
        Check("color 导航图方向", oriented != null && oriented.Height > oriented.Width,
            oriented == null ? "导航位图为空" : $"源 120x60 方向6 → 导航 {oriented.Width}x{oriented.Height}（应为竖图）");
    }

    // ── 真实夹具 ──────────────────────────────────────────────────────────────

    /// <summary>夹具名候选（按顺序匹配，忽略大小写；父代理 push 到 App 内部 <c>files/pv-color-fixtures</c>）。</summary>
    private static class ColorFixtureNames
    {
        /// <summary>sRGB 标记的 JPEG。</summary>
        public static readonly string[] SrgbJpeg = ["color-srgb.jpg", "jpeg-srgb.jpg", "srgb.jpg"];
        /// <summary>Display P3 标记的 JPEG。</summary>
        public static readonly string[] P3Jpeg = ["color-p3.jpg", "jpeg-p3.jpg", "p3.jpg"];
        /// <summary>Adobe RGB（ICC）JPEG。</summary>
        public static readonly string[] AdobeJpeg = ["color-adobe.jpg", "jpeg-adobe.jpg", "adobe.jpg"];
        /// <summary>无 ICC、只靠 EXIF 声明 Adobe RGB 的 JPEG（最容易把 Adobe 码值当 sRGB 用）。</summary>
        public static readonly string[] UntaggedAdobeJpeg =
            ["color-untagged-adobe.jpg", "untagged-adobe.jpg", "jpeg-untagged-adobe.jpg", "color-adobe-untagged.jpg"];
        /// <summary>大图 Display P3 JPEG（6000×4000 级，核对代理与全分辨率几何）。</summary>
        public static readonly string[] P3LargeJpeg = ["color-p3-large.jpg", "p3-large.jpg"];
        /// <summary>sRGB HEIF。</summary>
        public static readonly string[] SrgbHeif = ["color-srgb.heic", "heif-srgb.heic", "srgb.heic", "color-srgb.hif"];
        /// <summary>Display P3 HEIF。</summary>
        public static readonly string[] P3Heif = ["color-p3.heic", "heif-p3.heic", "p3.heic", "color-p3.hif"];
        /// <summary>可选灰阶 HEIF（存在就核对中性保持）。</summary>
        public static readonly string[] GrayHeif = ["color-gray.heic", "heif-gray.heic", "gray.heic", "color-gray.hif"];
    }

    /// <summary>
    /// 真实夹具：四类 JPEG 与 HEIF 经真实展示解码器解出后，与共享 <see cref="SkiaImageCodec"/> 参考解码
    /// （再由 <see cref="ColorEngine"/> 转成解码产物声明的目标色域）在平坦色块上比对。
    /// 未标 ICC 只带 EXIF 的 Adobe 素材必须和参考一致——这是「Adobe 被当成 sRGB」的唯一程序化门。
    /// </summary>
    private static async Task CheckColorFixturesAsync()
    {
        // 解码目标由设备能力决定（有广色域就按 P3 解，不再跟随表面），先把表面恢复成设备原生状态再解夹具。
        await RecreatePhotoSurfaceAsync(true);
        var decoder = CurrentPresentationDecoder();
        Check("color 夹具展示解码器可用", decoder != null, decoder?.GetType().Name ?? "未注入");
        if (decoder == null) return;
        Check("color 夹具目录存在", Directory.Exists(ColorFixtureFolder()), ColorFixtureFolder());

        foreach (var (label, candidates, compareFull) in new (string, string[], bool)[]
        {
            ("JPEG sRGB", ColorFixtureNames.SrgbJpeg, true),
            ("JPEG P3", ColorFixtureNames.P3Jpeg, true),
            ("JPEG Adobe", ColorFixtureNames.AdobeJpeg, true),
            ("JPEG 未标 Adobe", ColorFixtureNames.UntaggedAdobeJpeg, true),
        })
        {
            await CheckJpegFixtureAsync(decoder, label, candidates, compareFull);
        }
        await CheckLargeJpegFixtureAsync(decoder);
        await CheckHeifFixtureAsync(decoder, "HEIF sRGB", ColorFixtureNames.SrgbHeif);
        await CheckHeifFixtureAsync(decoder, "HEIF P3", ColorFixtureNames.P3Heif);
        await CheckHeifFixtureAsync(decoder, "HEIF 灰阶", ColorFixtureNames.GrayHeif, optional: true);
    }

    /// <summary>一个 JPEG 夹具：代理（600）与全分辨率各解一次，代理与参考同缩比对照，全分辨率只补几何与标签。</summary>
    /// <param name="decoder">展示解码器。</param>
    /// <param name="label">报告用标签。</param>
    /// <param name="candidates">夹具名候选。</param>
    /// <param name="compareFull">是否对全分辨率产物也做参考对照（大图跳过，避免参考解码再占一份全幅内存）。</param>
    private static async Task CheckJpegFixtureAsync(IPresentationImageDecoder decoder, string label, string[] candidates, bool compareFull)
    {
        string? path = ResolveColorFixture(candidates);
        if (path == null)
        {
            Check($"color 夹具 {label}", false, $"缺少 {string.Join("/", candidates)}（push 到 files/pv-color-fixtures）");
            return;
        }

        var target = AndroidDisplayCapabilities.PresentationColorSpace;
        using (var proxy = await decoder.DecodeAsync(new AndroidFileRef(path), FixtureProxyLongEdge, PixelEncoding.Rgba8, CancellationToken.None))
        {
            Check($"color 夹具 {label} 代理解码", proxy != null, proxy == null ? "解码返回 null" : $"{proxy.Width}x{proxy.Height} 标签={proxy.ColorSpace}");
            if (proxy != null)
            {
                int longEdge = Math.Max(proxy.Width, proxy.Height);
                Check($"color 夹具 {label} 代理长边", longEdge <= FixtureProxyLongEdge + 1,
                    $"长边={longEdge}（上限 {FixtureProxyLongEdge}，源 {proxy.SourceWidth}x{proxy.SourceHeight}）");
                Check($"color 夹具 {label} 标签=解码目标色域", proxy.ColorSpace == target,
                    $"产物={proxy.ColorSpace} 目标={target} 设备P3={AndroidDisplayCapabilities.DisplaySupportsP3} 表面P3={_root!.Photo.SurfaceIsP3}");
                var failure = CompareWithSkiaReference(path, proxy, longEdge);
                Check($"color 夹具 {label} 代理色块匹配参考", failure == null,
                    failure ?? $"最大偏差 ≤ {FixtureTolerance}（参考=SkiaImageCodec+ColorEngine→{target}）");
            }
        }

        using var full = await decoder.DecodeAsync(new AndroidFileRef(path), null, PixelEncoding.Rgba8, CancellationToken.None);
        Check($"color 夹具 {label} 全分辨率解码", full != null,
            full == null ? "解码返回 null" : $"{full.Width}x{full.Height} 源={full.SourceWidth}x{full.SourceHeight} 方向={full.Orientation}");
        if (full == null) return;
        Check($"color 夹具 {label} 全分辨率几何",
            full.Width == full.SourceWidth && full.Height == full.SourceHeight,
            $"产物={full.Width}x{full.Height} 源={full.SourceWidth}x{full.SourceHeight}");
        if (!compareFull) return;
        var fullFailure = CompareWithSkiaReference(path, full, null);
        Check($"color 夹具 {label} 全分辨率色块匹配参考", fullFailure == null,
            fullFailure ?? $"最大偏差 ≤ {FixtureTolerance}（参考=SkiaImageCodec+ColorEngine→{full.ColorSpace}）");
    }

    /// <summary>大图夹具：代理档必须缩到 600 长边，全分辨率必须回到源尺寸，代理色块与同缩比参考一致。</summary>
    /// <param name="decoder">展示解码器。</param>
    private static async Task CheckLargeJpegFixtureAsync(IPresentationImageDecoder decoder)
    {
        string? path = ResolveColorFixture(ColorFixtureNames.P3LargeJpeg);
        if (path == null)
        {
            Check("color 夹具 大图 P3", false, $"缺少 {string.Join("/", ColorFixtureNames.P3LargeJpeg)}（push 到 files/pv-color-fixtures）");
            return;
        }

        using (var proxy = await decoder.DecodeAsync(new AndroidFileRef(path), FixtureProxyLongEdge, PixelEncoding.Rgba8, CancellationToken.None))
        {
            Check("color 夹具 大图代理解码", proxy != null, proxy == null ? "解码返回 null" : $"{proxy.Width}x{proxy.Height}");
            if (proxy != null)
            {
                Check("color 夹具 大图代理长边", Math.Max(proxy.Width, proxy.Height) <= FixtureProxyLongEdge + 1,
                    $"长边={Math.Max(proxy.Width, proxy.Height)} 源={proxy.SourceWidth}x{proxy.SourceHeight}");
                var failure = CompareWithSkiaReference(path, proxy, Math.Max(proxy.Width, proxy.Height));
                Check("color 夹具 大图代理色块匹配参考", failure == null, failure ?? $"最大偏差 ≤ {FixtureTolerance}");
            }
        }

        using var full = await decoder.DecodeAsync(new AndroidFileRef(path), null, PixelEncoding.Rgba8, CancellationToken.None);
        Check("color 夹具 大图全分辨率几何",
            full != null && full.Width == full.SourceWidth && full.Height == full.SourceHeight,
            full == null ? "解码返回 null" : $"{full.Width}x{full.Height} 源={full.SourceWidth}x{full.SourceHeight} 标签={full.ColorSpace}");
        if (full == null) return;
        Check("color 夹具 大图全分辨率长边", Math.Max(full.Width, full.Height) >= 3000,
            $"长边={Math.Max(full.Width, full.Height)}（大图夹具应远大于代理档）");
        await CheckLargeUploadStatsAsync(full);
    }

    /// <summary>大图全幅上传对照：sRGB 表面必须记一次真实色域转换，重建 P3 表面后同一 P3 源完整重传记零转换。</summary>
    private static async Task CheckLargeUploadStatsAsync(DecodedImage full)
    {
        var photo = _root!.Photo;
        using var p3Source = ColorEngine.ToDisplayEncoding(full, PixelEncoding.Rgba8, ImageColorSpace.DisplayP3);
        try
        {
            photo.RecreateSurfaceForChecks(false);
            AndroidUploadStats.Reset();
            await ShowColorSyntheticAsync(p3Source);
            var convertedUpload = AndroidUploadStats.LatestForChecks;
            var convertedPixels = PhotoRendererForChecks(photo).CheckTexturePixelsForChecks(p3Source);
            Check("color 大图全幅 sRGB 表面上传记为已转换",
                convertedUpload is { Converted: true, CacheHit: false }, DescribeUpload(convertedUpload));
            Check("color 大图全幅 sRGB 表面提交墙钟覆盖分段",
                convertedUpload is { } first && SubmitCoversSegments(first), DescribeUpload(convertedUpload));
            Check("color 大图全幅转换纹理像素一致", convertedPixels.Matches, convertedPixels.Details);

            AndroidUploadStats.Reset();
            photo.RecreateSurfaceForChecks(true);
            var repeatedUpload = AndroidUploadStats.LatestForChecks;
            var repeatedPixels = PhotoRendererForChecks(photo).CheckTexturePixelsForChecks(p3Source);
            Check("color 大图全幅 P3 表面重建", photo.SurfaceIsP3,
                $"P3={photo.SurfaceIsP3} 声明={AndroidDisplayCapabilities.DisplaySupportsP3} colorspace={ColorspaceName(photo)}");
            Check("color 大图全幅同源重传零转换",
                photo.SurfaceIsP3 && repeatedUpload is { Converted: false, CacheHit: false }, DescribeUpload(repeatedUpload));
            Check("color 大图全幅同源重传提交墙钟覆盖分段",
                repeatedUpload is { } second && SubmitCoversSegments(second), DescribeUpload(repeatedUpload));
            Check("color 大图全幅同源重传纹理像素一致", repeatedPixels.Matches, repeatedPixels.Details);
        }
        finally
        {
            if (!photo.SurfaceIsP3) await RecreatePhotoSurfaceAsync(true);
        }

        // 换回小图，释放整幅产物的租约
        using var restore = CreateColorQuadImage(SurfaceImageColorSpace()) ?? throw new InvalidOperationException("合成色块图创建失败");
        await ShowColorSyntheticAsync(restore);
    }

    /// <summary>
    /// 一个 HEIF 夹具：代理与全分辨率各解一次，在同一批平坦色块上互相比对（HEIF 无独立参考解码器），
    /// 并核对中性（灰阶）色块的通道一致性——P3 码值被当成 sRGB 解读时灰阶不漂移，但通道混色会暴露。
    /// </summary>
    /// <param name="decoder">展示解码器。</param>
    /// <param name="label">报告用标签。</param>
    /// <param name="candidates">夹具名候选。</param>
    /// <param name="optional">为 true 时缺失不记 FAIL。</param>
    private static async Task CheckHeifFixtureAsync(IPresentationImageDecoder decoder, string label, string[] candidates, bool optional = false)
    {
        string? path = ResolveColorFixture(candidates);
        if (path == null)
        {
            if (!optional) Check($"color 夹具 {label}", false, $"缺少 {string.Join("/", candidates)}（push 到 files/pv-color-fixtures）");
            return;
        }

        var target = AndroidDisplayCapabilities.PresentationColorSpace;
        using var proxy = await decoder.DecodeAsync(new AndroidFileRef(path), FixtureProxyLongEdge, PixelEncoding.Rgba8, CancellationToken.None);
        using var full = await decoder.DecodeAsync(new AndroidFileRef(path), null, PixelEncoding.Rgba8, CancellationToken.None);
        Check($"color 夹具 {label} proxy/full 解码", proxy != null && full != null,
            $"proxy={(proxy == null ? "null" : $"{proxy.Width}x{proxy.Height}")} full={(full == null ? "null" : $"{full.Width}x{full.Height}")}");
        if (proxy == null || full == null) return;
        Check($"color 夹具 {label} 标签=解码目标色域", proxy.ColorSpace == target && full.ColorSpace == target,
            $"proxy={proxy.ColorSpace} full={full.ColorSpace} 目标={target} 设备P3={AndroidDisplayCapabilities.DisplaySupportsP3}");

        var samples = FindFlatSamples(full);
        int worst = 0;
        string worstAt = "无平坦色块";
        var neutrals = new List<(double U, double V)>();
        foreach (var sample in samples)
        {
            var want = ColorPixelAt(full, sample.U, sample.V);
            var actual = ColorPixelAt(proxy, sample.U, sample.V);
            int delta = ColorDelta(actual, want);
            if (delta > worst)
            {
                worst = delta;
                worstAt = $"({sample.U:0.###},{sample.V:0.###}) proxy={DescribeColor(actual)} full={DescribeColor(want)}";
            }
            if (Spread(want) <= 3) neutrals.Add(sample);
        }
        Check($"color 夹具 {label} proxy/full 色块一致", samples.Count > 0 && worst <= FixtureTolerance,
            samples.Count == 0 ? "全分辨率产物找不到平坦色块" : $"平坦色块={samples.Count} 最大偏差={worst} 位置={worstAt}");

        int neutralDrift = 0;
        string neutralAt = neutrals.Count == 0 ? "无中性色块" : "-";
        foreach (var sample in neutrals)
        {
            var want = ColorPixelAt(full, sample.U, sample.V);
            var actual = ColorPixelAt(proxy, sample.U, sample.V);
            int drift = Spread(actual) - Spread(want);
            if (drift <= neutralDrift) continue;
            neutralDrift = drift;
            neutralAt = $"({sample.U:0.###},{sample.V:0.###}) proxy={DescribeColor(actual)} full={DescribeColor(want)}";
        }
        Check($"color 夹具 {label} 灰阶中性保持", neutrals.Count == 0 || neutralDrift <= 1,
            $"{neutralAt} 中性色块={neutrals.Count} 通道扩散增量={neutralDrift}");
    }

    // ── HEIF 最终回落色彩 ─────────────────────────────────────────────────────

    /// <summary>
    /// HEIF 最终回落色彩回归（全部走真实解码入口，不只测 helper）：
    /// 强制共享 libheif 回落（跳过快速路径）在设备目标色域与 Display P3 / sRGB 三个出口上逐平坦色块对照主图解码入口；
    /// <c>AndroidHeifDecoder</c> 兼容入口（现为共享 libheif wrapper，不再走系统 <c>BitmapFactory</c>）必须出 Bgra32Srgb 且色块与共享出口一致；
    /// 容器声明改成 PQ/HLG 时展示解码入口必须返回 null 并记 HDR 原因；
    /// 容器 <c>irot</c> 非 0 时回落产物必须是显示朝向（方向 1）且源几何同步互换，像素方向按色卡四角核对。
    /// </summary>
    private static async Task CheckHeifFallbackColorAsync()
    {
        var decoder = CurrentPresentationDecoder();
        Check("color HEIF 回落展示解码器可用", decoder is AndroidPresentationDecoder,
            decoder?.GetType().Name ?? "未注入");
        if (decoder is null) return;

        foreach (var (label, candidates) in new (string, string[])[]
        {
            ("P3", ColorFixtureNames.P3Heif),
            ("sRGB", ColorFixtureNames.SrgbHeif),
        })
        {
            await CheckHeifFallbackExitsAsync(decoder, label, candidates);
        }

        await CheckHeifCompatEntryAsync("sRGB", ColorFixtureNames.SrgbHeif);
        await CheckHeifCompatEntryAsync("P3", ColorFixtureNames.P3Heif);
        await CheckHeifHdrDeclarationsAsync(decoder);
        await CheckHeifRotatedFallbackAsync();
        await CheckHeifOrientationContractAsync();
    }

    /// <summary>
    /// 一个 HEIF 夹具的回落出口对照：主图解码入口（快速路径，设备目标色域）作参照，
    /// 强制共享 libheif 回落分别在设备目标色域、Display P3、sRGB 三个出口解一次，逐平坦色块比对。
    /// </summary>
    /// <param name="decoder">展示解码器。</param>
    /// <param name="label">报告标签。</param>
    /// <param name="candidates">夹具名候选。</param>
    private static async Task CheckHeifFallbackExitsAsync(IPresentationImageDecoder decoder, string label, string[] candidates)
    {
        string? path = ResolveColorFixture(candidates);
        if (path == null)
        {
            Check($"color HEIF 回落 {label}", false, $"缺少 {string.Join("/", candidates)}（push 到 files/pv-color-fixtures）");
            return;
        }

        var file = new AndroidFileRef(path);
        var deviceTarget = AndroidDisplayCapabilities.PresentationColorSpace;
        using var reference = await decoder.DecodeAsync(file, FixtureProxyLongEdge, PixelEncoding.Rgba8, CancellationToken.None);
        if (reference == null)
        {
            Check($"color HEIF 回落 {label} 参照解码", false, "主图解入口返回 null");
            return;
        }

        foreach (var (exitLabel, target) in new (string, ImageColorSpace)[]
        {
            ("设备目标", deviceTarget),
            ("Display P3", ImageColorSpace.DisplayP3),
            ("sRGB", ImageColorSpace.Srgb),
        })
        {
            using var fallback = await AndroidPresentationDecoder.DecodeHeifFallbackForChecksAsync(
                file, FixtureProxyLongEdge, target, CancellationToken.None);
            if (fallback == null)
            {
                Check($"color HEIF 回落 {label} {exitLabel} 出口", false, $"共享 libheif 返回 null（{HeifLoader.LastDecodeError}）");
                continue;
            }

            var labelOk = fallback.ColorSpace == target && fallback.Orientation == 1
                && Math.Max(fallback.Width, fallback.Height) <= FixtureProxyLongEdge + 1;
            using var expected = ColorEngine.ToDisplayEncoding(reference, PixelEncoding.Rgba8, target);
            var failure = CompareFlatBlocks(expected, fallback);
            Check($"color HEIF 回落 {label} {exitLabel} 出口", labelOk && failure == null,
                (labelOk ? "" : $"形态不符：{fallback.ColorSpace}/方向{fallback.Orientation}/{fallback.Width}x{fallback.Height}；")
                + (failure ?? $"与快速路径逐平坦色块一致（容差 {FixtureTolerance}）")
                + $" 源={fallback.SourceWidth}x{fallback.SourceHeight}");
        }
    }

    private static async Task CheckHeifCompatEntryAsync(string label, string[] candidates)
    {
        string? path = ResolveColorFixture(candidates);
        if (path == null)
        {
            Check($"color HEIF 兼容入口 {label} 出口", false, "缺少 HEIF 夹具");
            return;
        }

        var file = new AndroidFileRef(path);
        using var compat = await new AndroidHeifDecoder().DecodeAsync(file, FixtureProxyLongEdge);
        if (compat == null)
        {
            Check($"color HEIF 兼容入口 {label} 出口", false, "回落解码返回 null");
            return;
        }

        var shapeOk = compat.ColorSpace == ImageColorSpace.Srgb && compat.Encoding == PixelEncoding.Bgra32
            && compat.Orientation == 1 && Math.Max(compat.Width, compat.Height) <= FixtureProxyLongEdge + 1;
        Check($"color HEIF 兼容入口 {label} sRGB 出口", shapeOk,
            $"{compat.Encoding}/{compat.ColorSpace}/{compat.Width}x{compat.Height} 方向={compat.Orientation}");

        using var reference = await AndroidPresentationDecoder.DecodeHeifFallbackForChecksAsync(
            file, Math.Max(compat.Width, compat.Height), ImageColorSpace.Srgb, CancellationToken.None);
        if (reference == null)
        {
            Check($"color HEIF 兼容入口 {label} 色块参考", false, $"共享 libheif sRGB 出口返回 null（{HeifLoader.LastDecodeError}）");
            return;
        }

        var failure = CompareFlatBlocks(reference, compat);
        Check($"color HEIF 兼容入口 {label} 色块匹配参考", failure == null,
            failure ?? $"与共享 libheif sRGB 出口逐平坦色块一致（容差 {FixtureTolerance}）");
    }

    /// <summary>
    /// 容器声明改成 PQ/HLG 时必须拒绝：走真实展示解码入口（快速路径→共享 libheif），
    /// 返回 null 且 <see cref="HeifLoader.LastDecodeError"/> 明确是 HDR —— 任何一段都不许按 SDR 输出。
    /// </summary>
    /// <param name="decoder">展示解码器。</param>
    private static async Task CheckHeifHdrDeclarationsAsync(IPresentationImageDecoder decoder)
    {
        string? path = ResolveColorFixture(ColorFixtureNames.SrgbHeif);
        if (path == null)
        {
            Check("color HEIF HDR 声明拒绝", false, "缺少 sRGB HEIF 夹具");
            return;
        }

        var source = File.ReadAllBytes(path);
        foreach (var (transfer, label) in new[] { ((ushort)16, "PQ"), ((ushort)18, "HLG") })
        {
            var patchedPath = await WriteTempHeifAsync($"hdr-{transfer}.heic", PatchNclxTransfer(source, transfer));
            if (patchedPath == null)
            {
                Check($"color HEIF {label} 声明拒绝", false, "临时夹具写入失败");
                continue;
            }

            var counted = new CountedHeifFile(new AndroidFileRef(patchedPath));
            using var decoded = await decoder.DecodeAsync(counted, FixtureProxyLongEdge,
                PixelEncoding.Rgba8, CancellationToken.None);
            Check($"color HEIF {label} 回落复用文件字节", counted.Opens == 1, $"opens={counted.Opens}");
            var lastError = HeifLoader.LastDecodeError ?? string.Empty;
            Check($"color HEIF {label} 声明拒绝",
                decoded == null && lastError.Contains("HDR", StringComparison.Ordinal),
                decoded == null
                    ? $"入口拒绝，原因={lastError}"
                    : $"入口返回了产物 {decoded.Width}x{decoded.Height}（HDR 被当 SDR 输出）");
        }
    }

    /// <summary>
    /// 容器 <c>irot</c> 非 0 的强制回落：产物必须是显示朝向（方向 1）、画布宽高互换、源几何同步互换，
    /// 代理缩放保留全幅几何；色卡四角轮转核对变换确实作用在像素上（不按 EXIF 猜方向）。
    /// </summary>
    private static async Task CheckHeifRotatedFallbackAsync()
    {
        string? path = ResolveColorFixture(ColorFixtureNames.SrgbHeif);
        if (path == null)
        {
            Check("color HEIF irot 回落方向", false, "缺少 sRGB HEIF 夹具");
            return;
        }

        var source = File.ReadAllBytes(path);
        using var original = await AndroidPresentationDecoder.DecodeHeifFallbackForChecksAsync(
            new AndroidFileRef(path), null, ImageColorSpace.Srgb, CancellationToken.None);
        if (original == null)
        {
            Check("color HEIF irot 回落方向", false, "未旋转基线解码失败");
            return;
        }

        var patchedPath = await WriteTempHeifAsync("irot-1.heic", PatchIrot(source, 1));
        if (patchedPath == null)
        {
            Check("color HEIF irot 回落方向", false, "临时夹具写入失败");
            return;
        }

        using var rotated = await AndroidPresentationDecoder.DecodeHeifFallbackForChecksAsync(
            new AndroidFileRef(patchedPath), null, ImageColorSpace.Srgb, CancellationToken.None);
        using var proxy = await AndroidPresentationDecoder.DecodeHeifFallbackForChecksAsync(
            new AndroidFileRef(patchedPath), FixtureProxyLongEdge, ImageColorSpace.Srgb, CancellationToken.None);
        if (rotated == null || proxy == null)
        {
            Check("color HEIF irot 回落方向", false, $"回落解码失败（{HeifLoader.LastDecodeError}）");
            return;
        }

        Check("color HEIF irot 回落几何",
            rotated.Orientation == 1
            && rotated.Width == original.Height && rotated.Height == original.Width
            && rotated.SourceWidth == original.Height && rotated.SourceHeight == original.Width
            && proxy.SourceWidth == original.Height && proxy.SourceHeight == original.Width
            && proxy.Orientation == 1 && Math.Max(proxy.Width, proxy.Height) <= FixtureProxyLongEdge + 1,
            $"源 {original.Width}x{original.Height} → 回落 {rotated.Width}x{rotated.Height}"
            + $"(方向 {rotated.Orientation},源几何 {rotated.SourceWidth}x{rotated.SourceHeight}),"
            + $"代理 {proxy.Width}x{proxy.Height}(源几何 {proxy.SourceWidth}x{proxy.SourceHeight})");

        // 色卡四角(1200x800):左上红、右上蓝、左下白块、右下背景灰。逆时针 90° 后整体轮转:
        // 新左上=旧右上(蓝)、新右上=旧右下(背景灰)、新左下=旧左上(红)、新右下=旧左下(白块)。
        if (rotated.Width < 200 || rotated.Height < 200)
        {
            Check("color HEIF irot 回落四角", false, $"产物过小 {rotated.Width}x{rotated.Height}，无法取四角样本");
            return;
        }

        var topLeft = ColorPixelAt(rotated, 40, 60);
        var topRight = ColorPixelAt(rotated, rotated.Width - 40, 60);
        var bottomLeft = ColorPixelAt(rotated, 40, rotated.Height - 100);
        var bottomRight = ColorPixelAt(rotated, rotated.Width - 40, rotated.Height - 100);
        Check("color HEIF irot 回落四角",
            (bottomRight.R, bottomRight.G, bottomRight.B) is ( >= 240, >= 240, >= 240)
            && (topLeft.R, topLeft.G, topLeft.B) is ( <= 16, <= 16, >= 240)
            && (bottomLeft.R, bottomLeft.G, bottomLeft.B) is ( >= 240, <= 16, <= 16)
            && Math.Abs(topRight.R - 64) <= 12 && Math.Abs(topRight.G - 64) <= 12 && Math.Abs(topRight.B - 64) <= 12,
            $"左上={DescribeColor(topLeft)}(期望蓝) 右上={DescribeColor(topRight)}(期望背景灰) "
            + $"左下={DescribeColor(bottomLeft)}(期望红) 右下={DescribeColor(bottomRight)}(期望白块)");
    }

    /// <summary>
    /// HEIF 方向契约（真实 IHeifDecoder 入口）：产物一律显示朝向（方向 1）。容器 <c>irot</c> = 1/2/3
    /// （逆时针 90/180/270）各验一次代理几何与色卡四角；另经真实 <see cref="ThumbnailService"/> 核 180 一例
    /// （该角度没有横竖启发式保护，二次旋转会直接暴露）。
    /// </summary>
    private static async Task CheckHeifOrientationContractAsync()
    {
        string? path = ResolveColorFixture(ColorFixtureNames.SrgbHeif);
        if (path == null)
        {
            Check("color HEIF 方向契约", false, "缺少 sRGB HEIF 夹具");
            return;
        }

        Check("color HEIF 产物已预旋转", HeifLoader.OutputPreRotated,
            $"HeifLoader.OutputPreRotated={HeifLoader.OutputPreRotated}（IHeifDecoder 契约：产物已摆正）");

        var source = File.ReadAllBytes(path);
        var decoder = new AndroidLibHeifDecoder();
        string? path180 = null;
        foreach (var angle in new byte[] { 1, 2, 3 })
        {
            var patched = await WriteTempHeifAsync($"irot-{angle}.heic", PatchIrot(source, angle));
            if (patched == null)
            {
                Check($"color HEIF irot={angle}", false, "临时夹具写入失败");
                continue;
            }
            if (angle == 2) path180 = patched;

            using var proxy = await decoder.DecodeAsync(new AndroidFileRef(patched), FixtureProxyLongEdge);
            if (proxy == null)
            {
                Check($"color HEIF irot={angle}", false, $"IHeif 入口返回 null（{HeifLoader.LastDecodeError}）");
                continue;
            }

            // 90/270 是竖图，180 保持横图；产物方向一律 1（已摆正）。
            var expectPortrait = angle != 2;
            var geometryOk = proxy.Orientation == 1
                && (proxy.Height > proxy.Width) == expectPortrait
                && Math.Max(proxy.Width, proxy.Height) <= FixtureProxyLongEdge + 1;
            var cornersOk = HeifCornersMatch(proxy, ExpectedCorners(angle));
            Check($"color HEIF irot={angle}", geometryOk && cornersOk,
                $"{proxy.Width}x{proxy.Height} 方向={proxy.Orientation} "
                + (cornersOk ? "四角正确" : "四角不符:" + DescribeCorners(proxy)));
        }

        if (path180 == null) return;
        using var thumbnail = await ThumbnailService.GetThumbnailAsync(new AndroidFileRef(path180), 260);
        var thumbOk = thumbnail != null && HeifCornersMatch(thumbnail, ExpectedCorners(2));
        Check("color HEIF ThumbnailService 不再二次 180", thumbOk,
            thumbnail == null
                ? "ThumbnailService 返回 null"
                : $"{thumbnail.Width}x{thumbnail.Height} "
                  + (thumbOk ? "白块在右上（未二次旋转）" : "四角不符:" + DescribeCorners(thumbnail)));
    }

    /// <summary>色卡四角（左上红、右上蓝、左下白块、右下背景灰）逆时针 angle×90° 后各角应有的颜色。</summary>
    private static ((int R, int G, int B) TL, (int R, int G, int B) TR, (int R, int G, int B) BL, (int R, int G, int B) BR)
        ExpectedCorners(int angle) => angle switch
        {
            1 => ((0, 0, 255), (64, 64, 64), (255, 0, 0), (255, 255, 255)),
            2 => ((64, 64, 64), (255, 255, 255), (0, 0, 255), (255, 0, 0)),
            3 => ((255, 255, 255), (255, 0, 0), (64, 64, 64), (0, 0, 255)),
            _ => ((255, 0, 0), (0, 0, 255), (255, 255, 255), (64, 64, 64)),
        };

    /// <summary>按归一化四角取样核对显示朝向（避开色卡文字，缩略图尺寸同样适用）。</summary>
    private static bool HeifCornersMatch(DecodedImage image,
        ((int R, int G, int B) TL, (int R, int G, int B) TR, (int R, int G, int B) BL, (int R, int G, int B) BR) corners)
    {
        if (image.Width < 150 || image.Height < 100) return false;
        return CornerNear(image, 0.06, 0.06, corners.TL)
            && CornerNear(image, 0.94, 0.06, corners.TR)
            && CornerNear(image, 0.06, 0.95, corners.BL)
            && CornerNear(image, 0.94, 0.95, corners.BR);
    }

    /// <summary>归一化取样点是否接近期望颜色。</summary>
    private static bool CornerNear(DecodedImage image, double u, double v, (int R, int G, int B) expected)
    {
        var color = ColorPixelAt(image, u, v);
        return Math.Abs(color.R - expected.R) <= 12
            && Math.Abs(color.G - expected.G) <= 12
            && Math.Abs(color.B - expected.B) <= 12;
    }

    /// <summary>四角实测值的报告文本。</summary>
    private static string DescribeCorners(DecodedImage image)
        => $"左上={DescribeColor(ColorPixelAt(image, 0.06, 0.06))} "
            + $"右上={DescribeColor(ColorPixelAt(image, 0.94, 0.06))} "
            + $"左下={DescribeColor(ColorPixelAt(image, 0.06, 0.95))} "
            + $"右下={DescribeColor(ColorPixelAt(image, 0.94, 0.95))}";

    /// <summary>把改过的夹具字节写进 App 内部临时目录（产品路径不使用）。</summary>
    private static Task<string?> WriteTempHeifAsync(string name, byte[] bytes)
    {
        try
        {
            var folder = IOPath.Combine(_activity!.FilesDir!.AbsolutePath, "pv-color-fixtures-generated");
            Directory.CreateDirectory(folder);
            var full = IOPath.Combine(folder, name);
            File.WriteAllBytes(full, bytes);
            return Task.FromResult<string?>(full);
        }
        catch (Exception exception)
        {
            Console.WriteLine($"[NativeCheck] 临时夹具写入失败：{exception.Message}");
            return Task.FromResult<string?>(null);
        }
    }

    /// <summary>把每个 <c>nclx</c> 的传输特性改成给定代码(其余字节不动),用于构造 HDR 声明。</summary>
    /// <param name="data">原始文件字节。</param>
    /// <param name="transfer">目标传输特性代码。</param>
    /// <returns>改过的副本。</returns>
    private static byte[] PatchNclxTransfer(byte[] data, ushort transfer)
    {
        var patched = (byte[])data.Clone();
        for (int i = 0; i + 8 <= patched.Length; i++)
        {
            if (patched[i] != (byte)'n' || patched[i + 1] != (byte)'c'
                || patched[i + 2] != (byte)'l' || patched[i + 3] != (byte)'x') continue;
            // 'nclx' 之后依次是 primaries(2)/transfer(2)/matrix(2)/full_range(1)。
            patched[i + 6] = (byte)(transfer >> 8);
            patched[i + 7] = (byte)transfer;
        }
        return patched;
    }

    /// <summary>把每个 <c>irot</c> 的角度改成给定值(单位 90°,其余字节不动)。</summary>
    /// <param name="data">原始文件字节。</param>
    /// <param name="angle">角度码(0-3)。</param>
    /// <returns>改过的副本。</returns>
    private static byte[] PatchIrot(byte[] data, byte angle)
    {
        var patched = (byte[])data.Clone();
        for (int i = 0; i + 5 <= patched.Length; i++)
        {
            if (patched[i] != (byte)'i' || patched[i + 1] != (byte)'r'
                || patched[i + 2] != (byte)'o' || patched[i + 3] != (byte)'t') continue;
            patched[i + 4] = angle;
        }
        return patched;
    }

    /// <summary>
    /// 在两幅同尺寸图像上按参考图的平坦色块逐点比对（8 位 RGBA/BGRA）,返回 null 表示一致。
    /// </summary>
    /// <param name="reference">参考图（决定取样点）。</param>
    /// <param name="actual">实测图。</param>
    /// <returns>null 或偏差描述。</returns>
    private static string? CompareFlatBlocks(DecodedImage reference, DecodedImage actual)
    {
        if (reference.Width != actual.Width || reference.Height != actual.Height)
            return $"尺寸不一致：参考 {reference.Width}x{reference.Height} 实测 {actual.Width}x{actual.Height}";
        var samples = FindFlatSamples(reference);
        if (samples.Count == 0) return "参考图中找不到平坦色块";
        int worst = 0;
        string worstAt = "无";
        foreach (var sample in samples)
        {
            var want = ColorPixelAt(reference, sample.U, sample.V);
            var mine = ColorPixelAt(actual, sample.U, sample.V);
            int delta = ColorDelta(mine, want);
            if (delta <= worst) continue;
            worst = delta;
            worstAt = $"({sample.U:0.###},{sample.V:0.###}) 实测={DescribeColor(mine)} 参考={DescribeColor(want)}";
        }
        return worst <= FixtureTolerance
            ? null
            : $"最大偏差={worst} 位置={worstAt} 平坦色块={samples.Count}";
    }

    // ── 增强 ──────────────────────────────────────────────────────────────────

    /// <summary>增强预览：8 位展示档上算完必须回到源的编码与色彩标签，输出有变化，源像素一个字节都不许改。</summary>
    private static async Task CheckEnhanceAsync()
    {
        var decoder = CurrentPresentationDecoder();
        DecodedImage? source = null;
        string name = "合成色块图";
        try
        {
            foreach (var candidates in new[] { ColorFixtureNames.P3Jpeg, ColorFixtureNames.SrgbJpeg, ColorFixtureNames.AdobeJpeg })
            {
                string? path = ResolveColorFixture(candidates);
                if (decoder == null || path == null) continue;
                source = await decoder.DecodeAsync(new AndroidFileRef(path), FixtureProxyLongEdge, PixelEncoding.Rgba8, CancellationToken.None);
                if (source == null) continue;
                name = IOPath.GetFileName(path);
                break;
            }
            source ??= CreateColorQuadImage(SurfaceImageColorSpace());
            if (source == null)
            {
                Check("color 增强输出", false, "没有可用的增强源");
                return;
            }

            var before = (byte[])source.Pixels.Clone();
            using var enhanced = ImageEnhancer.EnhanceToDisplay(source);
            Check("color 增强输出标签与口径",
                enhanced.ColorSpace == source.ColorSpace && enhanced.Encoding == source.Encoding
                && enhanced.Width == source.Width && enhanced.Height == source.Height
                && enhanced.Orientation == source.Orientation,
                $"源={source.ColorSpace}/{source.Encoding}/{source.Width}x{source.Height} "
                + $"增强={enhanced.ColorSpace}/{enhanced.Encoding}/{enhanced.Width}x{enhanced.Height}（{name}）");
            Check("color 增强输出像素有变化", !ColorBytesEqual(enhanced.Pixels, before),
                $"源={name} 字节一致={ColorBytesEqual(enhanced.Pixels, before)}");
            Check("color 增强不改源像素", ColorBytesEqual(source.Pixels, before),
                $"源={name} 字节一致={ColorBytesEqual(source.Pixels, before)}");
        }
        finally
        {
            source?.Dispose();
        }
    }

    // ── 共用助手 ──────────────────────────────────────────────────────────────

    /// <summary>销毁重建真实 EGL 表面并等它重新就绪（重建会把当前图重新上传一次）。</summary>
    /// <param name="allowP3">是否允许 P3 表面。</param>
    /// <param name="allowPassthrough">是否允许 P3 passthrough 表面。</param>
    private static async Task RecreatePhotoSurfaceAsync(bool allowP3, bool allowPassthrough = true)
    {
        var photo = _root!.Photo;
        photo.RecreateSurfaceForChecks(allowP3, allowPassthrough);
        await WaitAsync(() => photo.SurfaceReadyForChecks,
            $"表面重建（P3={allowP3} passthrough={allowPassthrough}）", 20000);
        await Task.Delay(120);
    }

    /// <summary>
    /// 把合成图挂到主图、强制适应态并等一帧（代际每次都变，保证走切图语义并重新上传纹理）。
    /// 上屏时本流程额外持一份租约（<see cref="_colorAttached"/>）：主图只持实例引用不持缓冲，
    /// 调用方自己的租约随后归还也不会让主图指向已释放的实例；上一张的租约在新图接管之后才归还。
    /// </summary>
    /// <param name="image">合成展示图（调用方仍持有创建者那份租约）。</param>
    private static async Task ShowColorSyntheticAsync(DecodedImage image)
    {
        var photo = _root!.Photo;
        if (!image.TryAcquire()) throw new InvalidOperationException("合成图租约已全部归还，不能上屏");
        var previous = _colorAttached;
        try
        {
            photo.SetImage(image, ++_colorGeneration);
        }
        catch
        {
            // 切图抛异常时无法确认主图是否已接管引用：保留这份租约由收尾归还，绝不先释放缓冲。
            _colorAttached = image;
            throw;
        }
        // 主图已经接管引用：从这一刻起由 _colorAttached 记账，随后才归还上一张的租约。
        _colorAttached = image;
        previous?.Release();
        photo.FitToView();
        await Task.Delay(150);
        if (!photo.HasImage) throw new InvalidOperationException("合成图未上屏");
        if (!photo.IsFit) throw new InvalidOperationException("强制适应态失败");
    }

    /// <summary>
    /// 收尾：先把主图切回会话图（或清空），确认主图不再引用合成图之后才归还本流程那份租约。
    /// 切图失败时保留租约（下次恢复仍可用），绝不先释放缓冲。
    /// </summary>
    /// <param name="image">会话当前展示图；null 表示清空。</param>
    /// <param name="generation">会话代际。</param>
    private static async Task RestoreColorSessionImageAsync(DecodedImage? image, int generation)
    {
        var photo = _root!.Photo;
        bool replaced = false;
        try
        {
            photo.SetImage(image, generation);
            // 到这里主图已经不再引用合成图，租约可以安全归还；之后的失败也不该再留着这份缓冲。
            replaced = true;
            photo.FitToView();
            await Task.Delay(150);
        }
        finally
        {
            if (replaced)
            {
                var attached = _colorAttached;
                _colorAttached = null;
                attached?.Release();
            }
        }
    }

    /// <summary>
    /// 生成 2×2 均匀色块图（每象限量 128×128、RGBA8、预乘 alpha），标签由调用方给定；
    /// 缩放到适应态后四个象限中心完整落在主图分区内，回读点稳定。
    /// </summary>
    /// <param name="colorSpace">像素色彩标签。</param>
    /// <param name="width">宽（像素）。</param>
    /// <param name="height">高（像素）。</param>
    /// <param name="orientation">待渲染的 EXIF 方向。</param>
    /// <returns>合成图；失败为 null。</returns>
    private static DecodedImage? CreateColorQuadImage(ImageColorSpace colorSpace, int width = SyntheticImageSize,
        int height = SyntheticImageSize, int orientation = 1)
    {
        int stride = checked(width * 4);
        var pixels = DecodedImage.RentPixels(checked(stride * height));
        try
        {
            for (int y = 0; y < height; y++)
            {
                int row = y * stride;
                int lower = y >= height / 2 ? 2 : 0;
                for (int x = 0; x < width; x++)
                {
                    var color = QuadColors[lower + (x >= width / 2 ? 1 : 0)];
                    int at = row + x * 4;
                    pixels[at] = color.R;
                    pixels[at + 1] = color.G;
                    pixels[at + 2] = color.B;
                    pixels[at + 3] = color.A;
                }
            }
            return DecodedImage.CreatePooled(pixels, width, height, stride, PixelEncoding.Rgba8, colorSpace,
                width, height, orientation);
        }
        catch
        {
            ImageBufferPool.Return(pixels);
            throw;
        }
    }

    /// <summary>当前表面要求的源编码：P3 表面用 P3，否则 sRGB。</summary>
    private static ImageColorSpace SurfaceImageColorSpace()
        => _root!.Photo.SurfaceIsP3 ? ImageColorSpace.DisplayP3 : ImageColorSpace.Srgb;

    /// <summary>四个象限中心的归一化坐标（左上、右上、左下、右下）。</summary>
    /// <param name="quadrant">象限序号 0–3。</param>
    private static (double U, double V) QuadrantCenter(int quadrant)
        => ((quadrant & 1) == 0 ? 0.25 : 0.75, quadrant >= 2 ? 0.75 : 0.25);

    /// <summary>回读四个象限中心（真实默认帧缓冲，非屏幕截图）。</summary>
    /// <param name="bypassOutputTransfer">为 true 时跳过输出传递函数的逆运算，拿原始帧缓冲字节。</param>
    private static (byte R, byte G, byte B, byte A)[] ReadColorQuadrants(bool bypassOutputTransfer = false)
    {
        var photo = _root!.Photo;
        var result = new (byte R, byte G, byte B, byte A)[4];
        for (int quadrant = 0; quadrant < 4; quadrant++)
        {
            var (u, v) = QuadrantCenter(quadrant);
            result[quadrant] = photo.ReadPixelForChecks(u, v, bypassOutputTransfer);
        }
        return result;
    }

    /// <summary>与 <see cref="ReadColorQuadrants"/> 同一批坐标上的理想值（经共享 <see cref="ColorEngine"/> 转换）。</summary>
    /// <param name="source">源合成图。</param>
    /// <param name="target">目标色彩标签。</param>
    private static (byte R, byte G, byte B, byte A)[] ExpectedColorQuadrants(DecodedImage source, ImageColorSpace target)
    {
        using var expected = ColorEngine.ToDisplayEncoding(source, PixelEncoding.Rgba8, target);
        var result = new (byte R, byte G, byte B, byte A)[4];
        for (int quadrant = 0; quadrant < 4; quadrant++)
        {
            var (u, v) = QuadrantCenter(quadrant);
            result[quadrant] = ColorPixelAt(expected, u, v);
        }
        return result;
    }

    /// <summary>合成图的理想四象限（源编码即表面编码时直接取源字节）。</summary>
    private static (byte R, byte G, byte B, byte A)[] QuadColorTuples() => [.. QuadColors];

    /// <summary>逐象限对照（只比颜色通道），判断最大偏差是否在容差内。</summary>
    /// <param name="actual">回读值。</param>
    /// <param name="expected">理想值。</param>
    /// <param name="tolerance">允许的最大通道偏差。</param>
    private static bool QuadrantsWithin((byte R, byte G, byte B, byte A)[] actual,
        (byte R, byte G, byte B, byte A)[] expected, int tolerance)
    {
        for (int i = 0; i < Math.Min(actual.Length, expected.Length); i++)
            if (ColorDeltaRgb(actual[i], expected[i]) > tolerance) return false;
        return true;
    }

    /// <summary>按归一化坐标取一个像素（RGBA8 / Bgra32，钳到边界）。</summary>
    /// <param name="image">图像。</param>
    /// <param name="u">归一化横坐标。</param>
    /// <param name="v">归一化纵坐标。</param>
    private static (byte R, byte G, byte B, byte A) ColorPixelAt(DecodedImage image, double u, double v)
    {
        int x = Math.Clamp((int)Math.Round(u * (image.Width - 1)), 0, image.Width - 1);
        int y = Math.Clamp((int)Math.Round(v * (image.Height - 1)), 0, image.Height - 1);
        return ColorPixelAt(image, x, y);
    }

    /// <summary>取指定像素（RGBA8 / Bgra32；其它编码显式抛出，不做字节猜测）。</summary>
    /// <param name="image">图像。</param>
    /// <param name="x">横坐标。</param>
    /// <param name="y">纵坐标。</param>
    private static (byte R, byte G, byte B, byte A) ColorPixelAt(DecodedImage image, int x, int y)
    {
        if (image.Encoding is not (PixelEncoding.Rgba8 or PixelEncoding.Bgra32))
            throw new NotSupportedException($"色彩检查只按 8 位 RGBA/BGRA 读像素，实际 {image.Encoding}");
        long at = (long)y * image.Stride + (long)x * DecodedImage.BytesPerPixel(image.Encoding);
        var pixels = image.Pixels;
        return image.Encoding == PixelEncoding.Rgba8
            ? (pixels[at], pixels[at + 1], pixels[at + 2], pixels[at + 3])
            : (pixels[at + 2], pixels[at + 1], pixels[at], pixels[at + 3]);
    }

    /// <summary>
    /// 按 ARGB_8888 原始通道读出整幅位图（等价于对 sRGB 位图逐点 <c>GetPixel</c>，
    /// 但走 <c>CopyPixelsToBuffer</c>：广色域标签不会被隐式转成 sRGB，标签错了会如实暴露）。
    /// </summary>
    /// <param name="bitmap">ARGB_8888 位图。</param>
    /// <returns>每像素一个打包 int（内存字节序 R,G,B,A 按小端读出）。</returns>
    private static int[] ReadBitmapPacked(Bitmap bitmap)
    {
        var pixels = new int[checked(bitmap.Width * bitmap.Height)];
        using var buffer = IntBuffer.Allocate(pixels.Length) ?? throw new InvalidOperationException("IntBuffer 分配失败");
        bitmap.CopyPixelsToBuffer(buffer);
        buffer.Rewind();
        buffer.Get(pixels);
        return pixels;
    }

    /// <summary>位图是否按 sRGB 解释：没有 ColorSpace（旧接口产物）本身就按 sRGB 显示，故 null 视为通过。</summary>
    /// <param name="bitmap">位图。</param>
    [SupportedOSPlatform("android26.0")]
    private static bool BitmapIsSrgbTagged(Bitmap bitmap) => bitmap.ColorSpace is null || bitmap.ColorSpace.IsSrgb;

    /// <summary>位图色彩空间的报告名（null 表示旧接口按 sRGB 解释）。</summary>
    /// <param name="bitmap">位图。</param>
    [SupportedOSPlatform("android26.0")]
    private static string BitmapColorSpaceLabel(Bitmap bitmap) => bitmap.ColorSpace?.Name ?? "null(legacy=sRGB)";

    /// <summary>两个像素之间的最大通道偏差。</summary>
    /// <param name="a">像素 A。</param>
    /// <param name="b">像素 B。</param>
    private static int ColorDelta((byte R, byte G, byte B, byte A) a, (byte R, byte G, byte B, byte A) b)
        => Math.Max(Math.Max(Math.Abs(a.R - b.R), Math.Abs(a.G - b.G)),
            Math.Max(Math.Abs(a.B - b.B), Math.Abs(a.A - b.A)));

    /// <summary>
    /// 只比三个颜色通道：窗口表面配置没有 alpha 位（<c>EGL_ALPHA_SIZE=0</c>），
    /// 帧缓冲回读的 alpha 不承载画面信息，拿它参与判定只会放大驱动差异。
    /// </summary>
    /// <param name="a">像素 A。</param>
    /// <param name="b">像素 B。</param>
    private static int ColorDeltaRgb((byte R, byte G, byte B, byte A) a, (byte R, byte G, byte B, byte A) b)
        => Math.Max(Math.Abs(a.R - b.R), Math.Max(Math.Abs(a.G - b.G), Math.Abs(a.B - b.B)));

    /// <summary>像素三通道的最大差值（判中性色块用）。</summary>
    /// <param name="color">像素。</param>
    private static int Spread((byte R, byte G, byte B, byte A) color)
        => Math.Max(color.R, Math.Max(color.G, color.B)) - Math.Min(color.R, Math.Min(color.G, color.B));

    /// <summary>像素的可读描述（报告用）。</summary>
    /// <param name="color">像素。</param>
    private static string DescribeColor((byte R, byte G, byte B, byte A) color)
        => $"({color.R},{color.G},{color.B},{color.A})";

    /// <summary>逐字节比较两个缓冲（长度不同直接判不同）。</summary>
    /// <param name="a">缓冲 A。</param>
    /// <param name="b">缓冲 B。</param>
    private static bool ColorBytesEqual(byte[] a, byte[] b)
    {
        if (a.Length != b.Length) return false;
        for (int i = 0; i < a.Length; i++)
            if (a[i] != b[i]) return false;
        return true;
    }

    /// <summary>表面 EGL 色彩空间的报告名。</summary>
    /// <param name="photo">主图视图。</param>
    private static string ColorspaceName(AndroidPhotoView photo)
        => photo.EglColorspaceForChecks(EglColorspaceDisplayP3) ? "0x3363(P3)"
            : photo.EglColorspaceForChecks(EglColorspaceDisplayP3Passthrough) ? "0x3490(P3 passthrough)"
            : photo.EglColorspaceForChecks(EglColorspaceSrgb) ? "0x3089(sRGB)"
            : "未知";

    /// <summary>输出帧缓冲编码的报告名。</summary>
    /// <param name="photo">主图视图。</param>
    private static string FramebufferName(AndroidPhotoView photo)
        => photo.FramebufferEncodingForChecks(GlFramebufferSrgb) ? "GL_SRGB(0x8C40)"
            : photo.FramebufferEncodingForChecks(GlFramebufferLinear) ? "GL_LINEAR(0x2601)"
            : "未知";

    /// <summary>
    /// 参考图里的平坦色块中心：在归一化网格上取点，要求 7×7 邻域与中心相差不超过 2（三通道），
    /// 这样缩放/重采样差异不会混进色块对照，也不需要预先知道夹具的色块布局。
    /// </summary>
    /// <param name="image">参考图（RGBA8）。</param>
    /// <param name="grid">每轴采样点数。</param>
    /// <param name="half">邻域半径（像素）。</param>
    /// <param name="tolerance">邻域允许的最大通道差。</param>
    private static List<(double U, double V)> FindFlatSamples(DecodedImage image, int grid = 13, int half = 3, int tolerance = 2)
    {
        var samples = new List<(double U, double V)>();
        if (image.Width <= 2 * half || image.Height <= 2 * half) return samples;
        for (int row = 0; row < grid; row++)
        {
            for (int column = 0; column < grid; column++)
            {
                double u = 0.05 + 0.9 * column / (grid - 1);
                double v = 0.05 + 0.9 * row / (grid - 1);
                int x = Math.Clamp((int)Math.Round(u * (image.Width - 1)), half, image.Width - 1 - half);
                int y = Math.Clamp((int)Math.Round(v * (image.Height - 1)), half, image.Height - 1 - half);
                var center = ColorPixelAt(image, x, y);
                bool flat = true;
                for (int dy = -half; dy <= half && flat; dy++)
                {
                    for (int dx = -half; dx <= half; dx++)
                    {
                        if (ColorDelta(ColorPixelAt(image, x + dx, y + dy), center) <= tolerance) continue;
                        flat = false;
                        break;
                    }
                }
                if (flat) samples.Add((u, v));
            }
        }
        return samples;
    }

    /// <summary>
    /// 用共享 <see cref="SkiaImageCodec"/> 解同一文件的参考值（真实源色域由 <see cref="JpegColorMetadata"/> 决定，
    /// 含「无 ICC 只有 EXIF Adobe」的推断），再经 <see cref="ColorEngine"/> 转到解码产物声明的目标色域，
    /// 在平坦色块上与真实解码产物对照。
    /// </summary>
    /// <param name="path">夹具路径。</param>
    /// <param name="decoded">真实解码产物。</param>
    /// <param name="referenceLongEdge">参考解码长边（与代理档同缩比）；null 表示全分辨率。</param>
    /// <returns>null 表示一致；否则是偏差描述。</returns>
    private static string? CompareWithSkiaReference(string path, DecodedImage decoded, int? referenceLongEdge)
    {
        var target = decoded.ColorSpace;
        if (target is not (ImageColorSpace.Srgb or ImageColorSpace.DisplayP3))
            return $"解码产物色彩标签不是展示口径：{target}";
        if (decoded.Orientation != 1)
            return $"解码产物带待应用方向 {decoded.Orientation}，无法按源朝向对照";

        DecodedImage? reference = null;
        try
        {
            using var stream = new MemoryStream(File.ReadAllBytes(path), writable: false);
            reference = referenceLongEdge is { } longEdge
                ? SkiaImageCodec.DecodeToLongEdge(stream, longEdge)
                : SkiaImageCodec.Decode(stream);
            if (reference == null) return "共享 SkiaImageCodec 参考解码失败";
            using var expected = ColorEngine.ToDisplayEncoding(reference, PixelEncoding.Rgba8, target);
            var samples = FindFlatSamples(expected);
            if (samples.Count == 0) return "参考图中找不到平坦色块";
            int worst = 0;
            string worstAt = "无";
            foreach (var sample in samples)
            {
                var want = ColorPixelAt(expected, sample.U, sample.V);
                var actual = ColorPixelAt(decoded, sample.U, sample.V);
                int delta = ColorDelta(actual, want);
                if (delta <= worst) continue;
                worst = delta;
                worstAt = $"({sample.U:0.###},{sample.V:0.###}) 实测={DescribeColor(actual)} 参考={DescribeColor(want)}";
            }
            return worst <= FixtureTolerance
                ? null
                : $"最大偏差={worst} 位置={worstAt} 平坦色块={samples.Count} 目标={target} 参考源={reference.ColorSpace}";
        }
        catch (Exception exception)
        {
            return $"参考对照异常：{exception.GetType().Name}：{exception.Message}";
        }
        finally
        {
            reference?.Dispose();
        }
    }

    /// <summary>色彩夹具目录（App 内部 <c>files/pv-color-fixtures</c>，由验收前 push）。</summary>
    private static string ColorFixtureFolder() => IOPath.Combine(_activity!.FilesDir!.AbsolutePath, "pv-color-fixtures");

    /// <summary>按候选名解析夹具（忽略大小写，按候选顺序取第一个存在的）。</summary>
    /// <param name="candidates">候选文件名。</param>
    /// <returns>完整路径；都不存在为 null。</returns>
    private static string? ResolveColorFixture(string[] candidates)
    {
        string folder = ColorFixtureFolder();
        if (!Directory.Exists(folder)) return null;
        var files = Directory.GetFiles(folder);
        foreach (var candidate in candidates)
        {
            foreach (var file in files)
                if (string.Equals(IOPath.GetFileName(file), candidate, StringComparison.OrdinalIgnoreCase)) return file;
        }
        return null;
    }

    /// <summary>ARGB_8888 的内存字节序是 R,G,B,A；<see cref="IntBuffer"/> 按小端读出，故原始 int 值为 A&lt;&lt;24|B&lt;&lt;16|G&lt;&lt;8|R。</summary>
    /// <param name="r">红。</param>
    /// <param name="g">绿。</param>
    /// <param name="b">蓝。</param>
    /// <param name="a">alpha。</param>
    private static int PackRawRgba(byte r, byte g, byte b, byte a) => unchecked((a << 24) | (b << 16) | (g << 8) | r);

    /// <summary><see cref="PackRawRgba"/> 的逆运算。</summary>
    /// <param name="value">打包值。</param>
    private static (byte R, byte G, byte B, byte A) UnpackRawRgba(int value)
        => ((byte)value, (byte)(value >> 8), (byte)(value >> 16), (byte)(value >> 24));
}
