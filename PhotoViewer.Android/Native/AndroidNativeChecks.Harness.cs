using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using Android.App;
using Android.Graphics;
using Android.OS;
using Android.Views;
using PhotoViewer.Android.Core;
using PhotoViewer.Core.Image;
using IOPath = System.IO.Path;

namespace PhotoViewer.Android.Native;

/// <summary>
/// 程序化检查的独立模式调度与共用工具（验收专用）：统一等待、逐条夹具生成（EXIF 方向 / XMP 星级）、
/// 合成滚动事件与展示解码器替换。只写 App 内部目录与 <c>GetExternalFilesDir</c>，不触碰用户照片。
/// </summary>
internal static partial class AndroidNativeChecks
{
    /// <summary>反射访问 <c>BitmapLoader</c> 的私有静态注入点（验收替换展示解码器用）。</summary>
    private const BindingFlags StaticNonPublic = BindingFlags.NonPublic | BindingFlags.Static;

    /// <summary>独立模式的统一入口：跑对应流程后落盘报告，异常不吞成静默。</summary>
    private static async Task RunStandaloneAsync()
    {
        Console.WriteLine($"[NativeCheck] 独立模式开始：{_mode}");
        try
        {
            switch (_mode)
            {
                case "faults":
                    await RunFaultsChecksAsync();
                    break;
                case "layout":
                    await RunLayoutChecksAsync();
                    break;
                case "input":
                    await RunInputChecksAsync();
                    break;
            }
        }
        catch (Exception exception)
        {
            Check("检查流程异常", false, exception.ToString());
        }
        finally
        {
            WriteReport();
        }
    }

    /// <summary>等待条件成立；超时抛异常，由该步的 try/catch 记 FAIL 并继续。</summary>
    /// <param name="condition">条件。</param>
    /// <param name="label">超时说明（写进报告）。</param>
    /// <param name="timeoutMs">最长等待（ms）。</param>
    private static async Task WaitAsync(Func<bool> condition, string label, int timeoutMs = 10000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return;
            await Task.Delay(50);
        }
        throw new TimeoutException($"等待超时：{label}");
    }

    /// <summary>跑一个检查步骤：抛出即记一条 FAIL（带步骤名与异常），不影响后续步骤。</summary>
    /// <param name="label">步骤名。</param>
    /// <param name="step">步骤实现。</param>
    private static async Task RunStepAsync(string label, Func<Task> step)
    {
        // 崩溃类问题（该模拟器历史上 pv-check all 偶发 SIGSEGV）靠这条日志定位到具体步骤。
        Console.WriteLine($"[NativeCheck] 步骤：{label}（{_mode}）");
        try
        {
            await step();
        }
        catch (Exception exception)
        {
            Check($"{label}（中止）", false, exception.GetType().Name + "：" + exception.Message);
        }
    }

    /// <summary>等待主屏完成首次布局（独立模式不依赖 <c>pv-open</c> 的夹具）。</summary>
    private static Task WaitForScreenAsync()
        => WaitAsync(() => _root != null && _root.Width > 0 && _root.Height > 0, "主屏布局");

    // ── 夹具 ──────────────────────────────────────────────────────────────────

    /// <summary>方向素材：<c>files/pv-check-orientation/orientation-1..8.jpg</c>（320×200，各带 EXIF 方向码）。</summary>
    /// <param name="activity">宿主 Activity（提供内部目录）。</param>
    /// <returns>素材目录。</returns>
    private static string EnsureOrientationFolder(Activity activity)
    {
        var folder = IOPath.Combine(activity.FilesDir!.AbsolutePath, "pv-check-orientation");
        Directory.CreateDirectory(folder);
        for (int orientation = 1; orientation <= 8; orientation++)
        {
            var path = IOPath.Combine(folder, $"orientation-{orientation}.jpg");
            if (File.Exists(path) && new FileInfo(path).Length > 0) continue;
            WriteCheckJpeg(path, 320, 200, orientation);
        }
        return folder;
    }

    /// <summary>布局/输入素材：<c>files/pv-check-layout/layout-1..3.jpg</c>（1200×800，带 XMP 星级 0）。</summary>
    /// <param name="activity">宿主 Activity。</param>
    /// <param name="refresh">为 true 时重写（输入模式的星级断言要从 0 起跑）。</param>
    /// <returns>素材目录。</returns>
    private static string EnsureLayoutFolder(Activity activity, bool refresh = false)
    {
        var folder = IOPath.Combine(activity.FilesDir!.AbsolutePath, "pv-check-layout");
        Directory.CreateDirectory(folder);
        for (int index = 1; index <= 3; index++)
        {
            var path = IOPath.Combine(folder, $"layout-{index}.jpg");
            if (!refresh && File.Exists(path) && new FileInfo(path).Length > 0) continue;
            WriteCheckJpeg(path, 1200, 800, rating: 0);
        }
        return folder;
    }

    /// <summary>失败注入素材目录 <c>files/pv-check-faults</c>。</summary>
    /// <param name="activity">宿主 Activity。</param>
    /// <returns>目录路径。</returns>
    private static string EnsureFaultsFolder(Activity activity)
    {
        var folder = IOPath.Combine(activity.FilesDir!.AbsolutePath, "pv-check-faults");
        Directory.CreateDirectory(folder);
        return folder;
    }

    /// <summary>一张可正常写回星级的 JPEG 副本（带 XMP 星级 0）。</summary>
    /// <param name="activity">宿主 Activity。</param>
    /// <returns>文件路径。</returns>
    private static string EnsureFaultGoodJpeg(Activity activity)
    {
        var path = IOPath.Combine(EnsureFaultsFolder(activity), "fault-good.jpg");
        if (!File.Exists(path) || new FileInfo(path).Length == 0) WriteCheckJpeg(path, 1200, 800, rating: 0);
        return path;
    }

    /// <summary>
    /// 足够触发代理档的 JPEG：长边必须大于当前代理目标（视口长边 ×2），否则一次解码就是源分辨率、不会出现代理。
    /// </summary>
    /// <param name="activity">宿主 Activity。</param>
    /// <returns>文件路径。</returns>
    private static string EnsureFaultFullFailJpeg(Activity activity)
    {
        var path = IOPath.Combine(EnsureFaultsFolder(activity), "fault-full-fail.jpg");
        int longEdge = Math.Max(4200, BitmapLoader.ProxyTargetLongEdge + 800);
        int width = longEdge, height = Math.Max(1, longEdge * 2 / 3);
        if (File.Exists(path) && new FileInfo(path).Length > 0)
        {
            // 视口变大（横屏/高密度）后旧素材可能已不再触发代理，按需重写。
            var bounds = new BitmapFactory.Options { InJustDecodeBounds = true };
            BitmapFactory.DecodeFile(path, bounds);
            if (bounds.OutWidth > 0 && Math.Max(bounds.OutWidth, bounds.OutHeight) > BitmapLoader.ProxyTargetLongEdge)
                return path;
        }
        WriteCheckJpeg(path, width, height);
        return path;
    }

    /// <summary>写一张四角色块 JPEG（可选插入 EXIF 方向 APP1 与 XMP 星级 APP1）。</summary>
    /// <param name="path">目标路径。</param>
    /// <param name="width">宽（像素）。</param>
    /// <param name="height">高（像素）。</param>
    /// <param name="orientation">EXIF 方向码（2–8 才插入；1 表示不写）。</param>
    /// <param name="rating">XMP 星级（0–5 才插入）。</param>
    private static void WriteCheckJpeg(string path, int width, int height, int orientation = 1, int? rating = null)
    {
        Directory.CreateDirectory(IOPath.GetDirectoryName(path)!);
        byte[] jpeg;
        using (var bitmap = Bitmap.CreateBitmap(width, height, Bitmap.Config.Argb8888!)!)
        {
            using (var canvas = new Canvas(bitmap))
            using (var paint = new Paint())
            {
                int halfWidth = Math.Max(1, width / 2), halfHeight = Math.Max(1, height / 2);
                paint.Color = Color.Rgb(204, 51, 51);
                canvas.DrawRect(0, 0, halfWidth, halfHeight, paint);
                paint.Color = Color.Rgb(51, 153, 76);
                canvas.DrawRect(halfWidth, 0, width, halfHeight, paint);
                paint.Color = Color.Rgb(51, 90, 204);
                canvas.DrawRect(0, halfHeight, halfWidth, height, paint);
                paint.Color = Color.Rgb(230, 204, 64);
                canvas.DrawRect(halfWidth, halfHeight, width, height, paint);
                // 左上角深色标记块：人工核对方向时看它落在哪一角。
                paint.Color = Color.Rgb(16, 16, 16);
                canvas.DrawRect(0, 0, Math.Max(2, width / 12), Math.Max(2, height / 12), paint);
            }
            using var encoded = new MemoryStream();
            bitmap.Compress(Bitmap.CompressFormat.Jpeg!, 92, encoded);
            jpeg = encoded.ToArray();
        }

        using var stream = File.Create(path);
        stream.Write(jpeg, 0, 2);
        if (orientation is >= 2 and <= 8) stream.Write(BuildOrientationApp1(orientation));
        if (rating is >= 0 and <= 5) stream.Write(BuildXmpApp1(rating.Value));
        stream.Write(jpeg, 2, jpeg.Length - 2);
    }

    /// <summary>EXIF APP1：IFD0 单条 <c>0x0112</c>（type 3、count 1）承载方向码，与解码器 <c>ExifInterface</c> 读取口径一致。</summary>
    /// <param name="orientation">方向码 2–8。</param>
    /// <returns>完整 APP1 段。</returns>
    private static byte[] BuildOrientationApp1(int orientation)
    {
        using var tiff = new MemoryStream();
        using (var writer = new BinaryWriter(tiff, Encoding.ASCII, true))
        {
            writer.Write((byte)'I');
            writer.Write((byte)'I');
            writer.Write((ushort)42);
            writer.Write(8u);
            writer.Write((ushort)1);
            writer.Write((ushort)0x0112);
            writer.Write((ushort)3);
            writer.Write(1u);
            writer.Write((ushort)orientation);
            writer.Write((ushort)0);
            writer.Write(0u);
        }

        var tiffBytes = tiff.ToArray();
        var payload = new byte[6 + tiffBytes.Length];
        Encoding.ASCII.GetBytes("Exif").CopyTo(payload, 0);
        tiffBytes.CopyTo(payload, 6);
        return FrameApp1(payload);
    }

    /// <summary>XMP APP1：<c>xmp:Rating</c> 是共享写回器定位星级的锚点。</summary>
    /// <param name="rating">星级 0–5。</param>
    /// <returns>完整 APP1 段。</returns>
    private static byte[] BuildXmpApp1(int rating)
    {
        const string header = "http://ns.adobe.com/xap/1.0/\0";
        string xml = "<?xpacket begin=\"\uFEFF\" id=\"W5M0MpCehiHzreSzNTczkc9d\"?>\n"
            + "<x:xmpmeta xmlns:x=\"adobe:ns:meta/\"><rdf:RDF xmlns:rdf=\"http://www.w3.org/1999/02/22-rdf-syntax-ns#\">"
            + "<rdf:Description rdf:about=\"\" xmlns:xmp=\"http://ns.adobe.com/xap/1.0/\" xmp:Rating=\"" + rating + "\"/>"
            + "</rdf:RDF></x:xmpmeta>\n<?xpacket end=\"w\"?>";
        return FrameApp1(Encoding.UTF8.GetBytes(header + xml));
    }

    /// <summary>把载荷包成 <c>FF E1 len payload</c> 的 APP1 段。</summary>
    /// <param name="payload">段载荷。</param>
    /// <returns>完整段字节。</returns>
    private static byte[] FrameApp1(byte[] payload)
    {
        var segment = new byte[4 + payload.Length];
        segment[0] = 0xFF;
        segment[1] = 0xE1;
        int length = payload.Length + 2;
        segment[2] = (byte)(length >> 8);
        segment[3] = (byte)(length & 0xFF);
        payload.CopyTo(segment, 4);
        return segment;
    }

    // ── 合成事件与反射 ────────────────────────────────────────────────────────

    /// <summary>合成一次滚动事件（<see cref="MotionEventActions.Scroll"/>），用于精确/离散滚轮分流断言。</summary>
    /// <param name="vertical">纵向轴增量。</param>
    /// <param name="source">输入源（触控板 / 鼠标）。</param>
    /// <param name="horizontal">横向轴增量。</param>
    /// <returns>合成事件。</returns>
    private static MotionEvent ScrollEvent(float vertical, InputSourceType source, float horizontal = 0)
    {
        var properties = new[] { new MotionEvent.PointerProperties { Id = 0, ToolType = MotionEventToolType.Mouse } };
        var coordinates = new[] { new MotionEvent.PointerCoords { X = 8, Y = 8, Pressure = 1, Size = 1 } };
        coordinates[0].SetAxisValue(Axis.Vscroll, vertical);
        if (Math.Abs(horizontal) > .0001f) coordinates[0].SetAxisValue(Axis.Hscroll, horizontal);
        long now = SystemClock.UptimeMillis();
        return MotionEvent.Obtain(now, now, MotionEventActions.Scroll, 1, properties, coordinates,
            MetaKeyStates.None, (MotionEventButtonState)0, 1, 1, 0, (Edge)0, source, MotionEventFlags.None)!;
    }

    /// <summary>读取 <c>BitmapLoader</c> 当前的展示解码器。</summary>
    /// <returns>当前解码器；未注入为 null。</returns>
    private static IPresentationImageDecoder? CurrentPresentationDecoder()
        => typeof(BitmapLoader).GetField("_presentationDecoder", StaticNonPublic)?.GetValue(null) as IPresentationImageDecoder;

    /// <summary>读取 <c>BitmapLoader</c> 当前的展示编码策略。</summary>
    /// <returns>当前策略；未注入为 null。</returns>
    private static IPresentationEncodingPolicy? CurrentEncodingPolicy()
        => typeof(BitmapLoader).GetField("_encodingPolicy", StaticNonPublic)?.GetValue(null) as IPresentationEncodingPolicy;

    /// <summary>临时替换展示解码器与编码策略（返回原始值供 finally 还原）。</summary>
    /// <param name="decoder">新解码器。</param>
    /// <param name="policy">新编码策略；null 表示沿用原策略。</param>
    /// <returns>用于还原的原始值。</returns>
    private static (IPresentationImageDecoder? Decoder, IPresentationEncodingPolicy? Policy) SwapPresentationDecoder(
        IPresentationImageDecoder decoder, IPresentationEncodingPolicy? policy = null)
    {
        var decoderField = typeof(BitmapLoader).GetField("_presentationDecoder", StaticNonPublic)
            ?? throw new InvalidOperationException("BitmapLoader 缺少 _presentationDecoder 字段");
        var policyField = typeof(BitmapLoader).GetField("_encodingPolicy", StaticNonPublic)
            ?? throw new InvalidOperationException("BitmapLoader 缺少 _encodingPolicy 字段");
        var original = (decoderField.GetValue(null) as IPresentationImageDecoder, policyField.GetValue(null) as IPresentationEncodingPolicy);
        decoderField.SetValue(null, decoder);
        if (policy != null) policyField.SetValue(null, policy);
        return original;
    }

    /// <summary>还原展示解码器与编码策略。</summary>
    /// <param name="original"><see cref="SwapPresentationDecoder"/> 返回的原始值。</param>
    private static void RestorePresentationDecoder((IPresentationImageDecoder? Decoder, IPresentationEncodingPolicy? Policy) original)
    {
        typeof(BitmapLoader).GetField("_presentationDecoder", StaticNonPublic)?.SetValue(null, original.Decoder);
        typeof(BitmapLoader).GetField("_encodingPolicy", StaticNonPublic)?.SetValue(null, original.Policy);
    }

    /// <summary>
    /// 打开一个文件夹并等首图上屏：独立模式的素材准备工作统一走这里，
    /// 避免各步各写一份「等文件夹 + 等图」的轮询。
    /// </summary>
    /// <param name="folder">素材目录。</param>
    /// <param name="timeoutMs">最长等待（ms）。</param>
    private static async Task OpenFolderAndWaitAsync(string folder, int timeoutMs = 20000)
    {
        var main = _main!;
        var session = _session!;
        await main.FolderVM.OpenFolderAsync(new AndroidFolderRef(folder));
        await WaitAsync(() => main.CurrentFile != null && session.Image != null, $"素材上屏：{IOPath.GetFileName(folder)}", timeoutMs);
        await Task.Delay(200);
    }
}
