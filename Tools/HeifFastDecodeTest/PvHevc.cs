using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using PhotoViewer.Core.Image.Heif;

namespace HeifFastDecodeTest;

/// <summary>
/// <c>pvhevc.dll</c>(精简 FFmpeg HEVC 解码器)的 P/Invoke 封装。
/// 每个线程一个上下文:上下文之间互不影响,平面指针在下一次 decode/close 之前有效。
/// </summary>
internal static partial class PvHevc
{
    private const string LibraryName = "pvhevc";
    private static readonly string? s_libraryPath = Resolve();
    private static readonly object s_initializationLock = new();
    private static int s_resolverInstalled;
    private static volatile bool s_initialized;

    /// <summary>DLL 是否可用(找不到时全部走 ffmpeg)。</summary>
    public static bool IsAvailable => s_libraryPath is not null;

    /// <summary>DLL 路径(诊断用)。</summary>
    public static string? LibraryPath => s_libraryPath;

    [StructLayout(LayoutKind.Sequential)]
    public struct Frame
    {
        public int Width;
        public int Height;
        public int BitDepth;
        public int ChromaFormat;
        public IntPtr Y;
        public IntPtr Cb;
        public IntPtr Cr;
        public int StrideY;
        public int StrideCb;
        public int StrideCr;
        public int FullRange;
        public int Matrix;
        public int Primaries;
        public int Transfer;
    }

    [LibraryImport(LibraryName)]
    private static partial int pv_hevc_version();

    [LibraryImport(LibraryName)]
    private static partial IntPtr pv_hevc_open(int threads);

    [LibraryImport(LibraryName)]
    private static unsafe partial int pv_hevc_decode(IntPtr context, byte* annexB, int length, Frame* frame);

    [LibraryImport(LibraryName)]
    private static partial void pv_hevc_close(IntPtr context);

    [LibraryImport(LibraryName)]
    private static partial IntPtr pv_hevc_last_error(IntPtr context);

    public static int Version()
    {
        EnsureInitialized();
        return pv_hevc_version();
    }

    /// <summary>
    /// 串行完成一次性初始化(解析器 + 首次 open/close)。
    /// </summary>
    /// <remarks>
    /// 并行首用会在多个线程上同时触发解码器库的全局初始化,现场遇到过一次并行首解抛异常,
    /// 因此这里先单线程打开再关闭一个上下文,后续并行解码不受影响。
    /// </remarks>
    private static void EnsureInitialized()
    {
        if (s_initialized) return;
        lock (s_initializationLock)
        {
            if (s_initialized) return;
            EnsureResolver();
            if (s_libraryPath is not null)
            {
                var context = pv_hevc_open(1);
                if (context != IntPtr.Zero) pv_hevc_close(context);
            }
            s_initialized = true;
        }
    }

    /// <summary>解码一段 Annex-B 码流;返回平面副本(平铺为 Y/Cb/Cr 连续布局)。</summary>
    public static DecodedTile? Decode(byte[] annexB)
    {
        if (s_libraryPath is null) return null;
        EnsureInitialized();

        var context = pv_hevc_open(1);
        if (context == IntPtr.Zero) return null;
        try
        {
            unsafe
            {
                Frame frame;
                int status;
                fixed (byte* stream = annexB)
                {
                    status = pv_hevc_decode(context, stream, annexB.Length, &frame);
                }
                if (status != 0)
                {
                    var message = pv_hevc_last_error(context);
                    Console.Error.WriteLine($"  ! pvhevc decode failed ({status}): {Marshal.PtrToStringUTF8(message)}");
                    return null;
                }

                var bytesPerSample = frame.BitDepth > 8 ? 2 : 1;
                var chroma = frame.ChromaFormat switch
                {
                    0 => HeifChromaSampling.Monochrome,
                    2 => HeifChromaSampling.Yuv422,
                    3 => HeifChromaSampling.Yuv444,
                    _ => HeifChromaSampling.Yuv420,
                };
                var chromaWidth = chroma switch
                {
                    HeifChromaSampling.Monochrome => 0,
                    HeifChromaSampling.Yuv420 => (frame.Width + 1) / 2,
                    HeifChromaSampling.Yuv422 => (frame.Width + 1) / 2,
                    _ => frame.Width,
                };
                var chromaHeight = chroma == HeifChromaSampling.Yuv420 ? (frame.Height + 1) / 2 : frame.Height;

                // 平面按各自跨距拷贝成紧密排布,便于与 ffmpeg 路径共用同一套瓦片视图。
                var data = new byte[((long)frame.Width * frame.Height * bytesPerSample)
                    + (2L * chromaWidth * chromaHeight * bytesPerSample)];
                CopyPlane(frame.Y, frame.StrideY, frame.Width, frame.Height, bytesPerSample, data, 0);
                var offset = (int)((long)frame.Width * frame.Height * bytesPerSample);
                if (chroma != HeifChromaSampling.Monochrome)
                {
                    CopyPlane(frame.Cb, frame.StrideCb, chromaWidth, chromaHeight, bytesPerSample, data, offset);
                    offset += (int)((long)chromaWidth * chromaHeight * bytesPerSample);
                    CopyPlane(frame.Cr, frame.StrideCr, chromaWidth, chromaHeight, bytesPerSample, data, offset);
                }

                return DecodedTile.FromRaw(data, frame.Width, frame.Height, frame.BitDepth, chroma);
            }
        }
        finally
        {
            pv_hevc_close(context);
        }
    }

    private static unsafe void CopyPlane(IntPtr source, int stride, int width, int height, int bytesPerSample, byte[] target, int offset)
    {
        var rowBytes = (long)width * bytesPerSample;
        var sourcePointer = (byte*)source;
        for (var y = 0; y < height; y++)
        {
            fixed (byte* destination = &target[offset + (y * rowBytes)])
            {
                Buffer.MemoryCopy(sourcePointer + ((long)y * stride), destination, rowBytes, rowBytes);
            }
        }
    }

    private static void EnsureResolver()
    {
        if (Interlocked.Exchange(ref s_resolverInstalled, 1) != 0) return;
        NativeLibrary.SetDllImportResolver(typeof(PvHevc).Assembly, static (name, assembly, path) =>
        {
            if (!name.Equals(LibraryName, StringComparison.OrdinalIgnoreCase)) return IntPtr.Zero;
            return s_libraryPath is not null && File.Exists(s_libraryPath)
                ? NativeLibrary.Load(s_libraryPath)
                : IntPtr.Zero;
        });
    }

    private static string? Resolve()
    {
        var explicitPath = Environment.GetEnvironmentVariable("PVHEVC_PATH");
        var candidates = new List<string>();
        if (!string.IsNullOrEmpty(explicitPath)) candidates.Add(explicitPath);
        candidates.Add(Path.Combine(AppContext.BaseDirectory, "pvhevc.dll"));
        // 工具目录:Tools/HeifFastDecodeTest/bin/<cfg>/<tfm>/<rid>/
        var anchor = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 6 && anchor is not null; i++) anchor = anchor.Parent;
        if (anchor is not null)
        {
            candidates.Add(Path.Combine(anchor.FullName, "PhotoViewer.Windows", "NativeLibs", "win-x64", "pvhevc.dll"));
        }
        foreach (var candidate in candidates)
        {
            if (File.Exists(candidate)) return Path.GetFullPath(candidate);
        }
        return null;
    }
}
