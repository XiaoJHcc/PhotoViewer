using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Media.Imaging;
using PhotoViewer.Core.Color;
using PhotoViewer.Core.Image;

namespace PhotoViewer.Windows.Native;

/// <summary>
/// 原生壳的位图适配层:把 <see cref="DecodedImage"/> 或裸 BGRA 像素包成 XAML 位图,并在显示器 ICC
/// 变化时把这些位图重新烘焙到当前显示器色彩空间。
/// </summary>
/// <remarks>
/// 像素契约不变:源快照始终是**紧凑行距的 sRGB BGRA(同 XAML 位图布局)**,
/// <see cref="ToSource(DecodedImage?)"/> 先经 <see cref="ColorEngine.ToSrgb8"/> 规范化,
/// 裸字节重载按原样接收;烘焙过程按预乘语义解/回乘 alpha,与原实现逐像素一致。
///
/// 线程契约:显示器烘焙(逐像素 pow + 三维 LUT)一律在后台做,UI 线程只做"写 PixelBuffer + Invalidate"。
/// 每轮刷新用版本号 + 变换身份双守卫:旧的后台结果既不会覆盖更新的显示器状态,也不会写进已被替换的源。
/// </remarks>
internal static class WindowsImageAdapter
{
    private static readonly ConditionalWeakTable<WriteableBitmap, SourcePixels> Sources = new();
    private static readonly List<WeakReference<WriteableBitmap>> Bitmaps = [];

    /// <summary>显示器烘焙版本:每轮 <see cref="UpdateDisplayColor"/> 递增,旧结果据此丢弃。</summary>
    private static long s_displayVersion;

    /// <summary>创建期允许在 UI 线程同步烘焙的像素上限(约 512²);更大的图(细节裁剪)转后台。</summary>
    private const int SyncBakePixels = 512 * 512;

    /// <summary>上一次写进日志的变换(策略切换时记一条,不按像素/刷新刷屏)。</summary>
    private static WindowsDisplayTransform? s_loggedTransform;

    public static WriteableBitmap? ToSource(DecodedImage? image)
    {
        if (image == null) return null;
        using var srgb = ColorEngine.ToSrgb8(image);
        var bytes = new byte[checked(srgb.Width * srgb.Height * 4)];
        for (int y = 0; y < srgb.Height; y++)
            srgb.Pixels.AsSpan(y * srgb.Stride, srgb.Width * 4).CopyTo(bytes.AsSpan(y * srgb.Width * 4));
        return Create(srgb.Width, srgb.Height, bytes);
    }

    public static WriteableBitmap ToSource(int width, int height, ReadOnlySpan<byte> bgra)
        => Create(width, height, bgra[..checked(width * height * 4)].ToArray());

    private static WriteableBitmap Create(int width, int height, byte[] bgra)
    {
        var bitmap = new WriteableBitmap(width, height);
        var source = new SourcePixels(bgra);
        Sources.Add(bitmap, source);
        if (Bitmaps.Count > 512) Bitmaps.RemoveAll(entry => !entry.TryGetTarget(out _));
        Bitmaps.Add(new WeakReference<WriteableBitmap>(bitmap));

        var transform = WindowsDisplayColor.Current;
        LogPolicy(transform);
        if (transform.Lut == null)
        {
            Apply(bitmap, source, bgra, transform);
            return bitmap;
        }

        // 小图允许在 UI 线程同步烘焙:新缩略图第一帧就是正确颜色,不必等下一轮刷新纠正。
        if (width * height <= SyncBakePixels)
        {
            try
            {
                Apply(bitmap, source, ConvertToDevice(bgra, transform), transform);
                return bitmap;
            }
            catch (Exception ex)
            {
                // 烘焙失败如实留痕:退回未烘焙快照并保持 Applied 未命中,下一轮刷新会重试。
                NativeStartupLog.Fail("display color bitmap create (" + width + "x" + height + ")", ex);
            }
        }

        // 大图(细节裁剪等)不在 UI 线程做逐像素换算:先上 sRGB 快照,后台算完回写。
        WritePixels(bitmap, bgra);
        QueueBakes([new Pending(bitmap, source, bitmap.DispatcherQueue)], transform, 0);
        return bitmap;
    }

    /// <summary>
    /// 显示器色彩变化后刷新所有在位图:UI 线程只做快照与排队,逐像素换算在后台完成后回 UI 线程写回。
    /// </summary>
    internal static void UpdateDisplayColor()
    {
        var transform = WindowsDisplayColor.Current;
        var version = Interlocked.Increment(ref s_displayVersion);
        LogPolicy(transform);

        var pending = new List<Pending>(Bitmaps.Count);
        for (int i = Bitmaps.Count - 1; i >= 0; i--)
        {
            if (!Bitmaps[i].TryGetTarget(out var bitmap)) { Bitmaps.RemoveAt(i); continue; }
            if (!Sources.TryGetValue(bitmap, out var source)) continue;
            if (ReferenceEquals(source.Applied, transform)) continue; // 已是这台显示器的结果
            pending.Add(new Pending(bitmap, source, bitmap.DispatcherQueue));
        }

        if (pending.Count == 0) return;

        if (transform.Lut == null)
        {
            // 未烘焙(Advanced Color / 无显示器 ICC):只回写 sRGB 快照,不做任何颜色运算。
            foreach (var item in pending) Apply(item.Bitmap, item.Source, item.Source.Bytes, transform);
            return;
        }

        QueueBakes(pending, transform, version);
    }

    /// <summary>后台烘焙一批位图,逐张回 UI 线程写回(每张都过版本/变换身份/源身份三重守卫)。</summary>
    /// <param name="pending">UI 线程取好的快照列表。</param>
    /// <param name="transform">本轮显示器变换。</param>
    /// <param name="version">刷新版本;0 表示创建期烘焙,不参与版本守卫(仍受变换身份守卫)。</param>
    private static void QueueBakes(List<Pending> pending, WindowsDisplayTransform transform, long version)
    {
        Task.Run(() =>
        {
            foreach (var item in pending)
            {
                byte[] converted;
                try
                {
                    converted = ConvertToDevice(item.Source.Bytes, transform);
                }
                catch (Exception ex)
                {
                    NativeStartupLog.Fail("display color bitmap bake", ex);
                    continue;
                }

                item.Queue.TryEnqueue(() => Apply(item.Bitmap, item.Source, converted, transform, version));
            }
        });
    }

    /// <summary>统一写回 + 记账:成功即记下这张位图当前上屏的变换,失败如实留痕。</summary>
    private static void Apply(
        WriteableBitmap bitmap,
        SourcePixels source,
        byte[] pixels,
        WindowsDisplayTransform transform,
        long version = 0)
    {
        // 版本守卫:更新的显示器状态已经排上,旧结果直接丢弃(不覆盖新显示器结果)。
        if (version != 0 && version != Volatile.Read(ref s_displayVersion)) return;
        if (!ReferenceEquals(transform, WindowsDisplayColor.Current)) return;
        if (!Sources.TryGetValue(bitmap, out var current) || !ReferenceEquals(current, source)) return;
        if (!WritePixels(bitmap, pixels)) return;
        source.Applied = transform;
    }

    private static bool WritePixels(WriteableBitmap bitmap, byte[] pixels)
    {
        try
        {
            using var buffer = bitmap.PixelBuffer.AsStream();
            buffer.Write(pixels);
            bitmap.Invalidate();
            return true;
        }
        catch (Exception ex)
        {
            NativeStartupLog.Fail("display color bitmap write", ex);
            return false;
        }
    }

    /// <summary>
    /// sRGB BGRA(预乘 alpha)→ 当前显示器编码值。逐像素语义与原实现一致:按预乘解出线性光、
    /// 经显示器变换到设备编码值,再乘回 alpha 量化;alpha 通道原样保留。
    /// </summary>
    private static byte[] ConvertToDevice(byte[] srgb, WindowsDisplayTransform transform)
    {
        var pixels = (byte[])srgb.Clone(); // alpha 与越界像素(alpha=0)保持原值
        for (int at = 0; at < pixels.Length; at += 4)
        {
            float alpha = srgb[at + 3] / 255f;
            if (alpha == 0) continue;
            var color = transform.MapToDevice(
                WindowsDisplayTransform.Decode(Math.Clamp(srgb[at + 2] / (255f * alpha), 0, 1)),
                WindowsDisplayTransform.Decode(Math.Clamp(srgb[at + 1] / (255f * alpha), 0, 1)),
                WindowsDisplayTransform.Decode(Math.Clamp(srgb[at] / (255f * alpha), 0, 1)));
            pixels[at] = Quantize(color.B * alpha);
            pixels[at + 1] = Quantize(color.G * alpha);
            pixels[at + 2] = Quantize(color.R * alpha);
        }

        return pixels;
    }

    /// <summary>变换策略切换时记一条日志:烘焙到显示器 ICC,还是按未烘焙的 sRGB 上屏。</summary>
    private static void LogPolicy(WindowsDisplayTransform transform)
    {
        if (ReferenceEquals(s_loggedTransform, transform)) return;
        s_loggedTransform = transform;
        NativeStartupLog.Write(transform.Lut == null
            ? "display color: 位图不烘焙,按 sRGB 直接上屏(" + transform.Description + ")"
            : "display color: 位图按显示器 ICC 烘焙(" + transform.Description + ")");
    }

    private static byte Quantize(float value) => (byte)Math.Clamp(MathF.Round(value * 255), 0, 255);

    /// <summary>等待烘焙的位图快照(全部字段都在 UI 线程取好,后台只读)。</summary>
    private readonly record struct Pending(WriteableBitmap Bitmap, SourcePixels Source, DispatcherQueue Queue);

    /// <summary>源 sRGB 快照与"当前上屏用的变换";<see cref="Bytes"/> 只读,后台线程直接引用它。</summary>
    private sealed class SourcePixels(byte[] bytes)
    {
        /// <summary>紧凑行距的 sRGB BGRA 快照(不可变)。</summary>
        public byte[] Bytes { get; } = bytes;

        /// <summary>这张位图当前上屏所用的变换;只在 UI 线程读写。</summary>
        public WindowsDisplayTransform? Applied { get; set; }
    }
}
