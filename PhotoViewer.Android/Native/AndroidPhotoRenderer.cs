using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using Android.Opengl;
using Android.Views;
using Java.Nio;
using PhotoViewer.Android.Core;
using PhotoViewer.Core.Color;
using PhotoViewer.Core.Image;
using PhotoViewer.Shared;

namespace PhotoViewer.Android.Native;

internal sealed class AndroidPhotoRenderer : IDisposable
{
    private const int EglGlColorspaceKhr = 0x309D;
    private const int EglGlColorspaceSrgbKhr = 0x3089;
    private const int EglGlColorspaceDisplayP3Ext = 0x3363;
    private const int EglGlColorspaceDisplayP3PassthroughExt = 0x3490;
    private const int GlFramebufferAttachmentColorEncoding = 0x8210;
    private const int GlSrgb = 0x8C40;
    private const int GlLinear = 0x2601;
    private const int EglOpenGles3Bit = 0x40;
    private const int EglNone = 0x3038;

    private const string VertexShader = """
        attribute vec2 aPosition;
        attribute vec2 aTexCoord;
        varying vec2 vTexCoord;
        void main() {
            gl_Position = vec4(aPosition, 0.0, 1.0);
            vTexCoord = aTexCoord;
        }
        """;

    private const string FragmentShader = """
        precision highp float;
        uniform sampler2D uTexture;
        uniform bool uLinearize;
        varying vec2 vTexCoord;
        vec3 toLinear(vec3 value) {
            return mix(value / 12.92, pow((value + 0.055) / 1.055, vec3(2.4)),
                step(vec3(0.04045), value));
        }
        void main() {
            vec4 color = texture2D(uTexture, vTexCoord);
            gl_FragColor = vec4(uLinearize ? toLinear(color.rgb) : color.rgb, color.a);
        }
        """;

    private const int RecentTextureCapacity = 2;
    private const long SpareTextureByteLimit = 32L * 1024 * 1024;
    private const int MipChainNumerator = 4;
    private const int MipChainDenominator = 3;

    private const int VertexFloatCount = 16;
    private const int VertexBufferBytes = VertexFloatCount * sizeof(float);
    /// <summary>顶点属性字节步长；纹理坐标从第 3 个 float 起（字节偏移 8）。</summary>
    private const int VertexStrideBytes = 16;
    private const int VertexTexCoordByteOffset = 8;

    private readonly object _sync = new();
    private readonly List<TextureEntry> _recentTextures = new(RecentTextureCapacity);
    private readonly int[] _handleBuffer = new int[1];
    private EGLDisplay? _display;
    private EGLSurface? _surface;
    private EGLContext? _context;
    private EGLConfig? _config;
    private int _width;
    private int _height;
    private int _program;
    private int _positionHandle = -1;
    private int _texCoordHandle = -1;
    private int _linearizeHandle = -1;
    private bool _bypassOutputTransfer;
    private TextureEntry? _current;
    private TextureEntry? _spareTexture;
    private long _textureBytes;
    private ByteBuffer? _uploadBuffer;
    private ByteBuffer? _vertexBuffer;
    private FloatBuffer? _vertexFloats;
    private readonly float[] _vertices = new float[VertexFloatCount];
    private int _vertexAllocations;
    private bool _disposed;
    private bool _surfaceP3;

    internal bool HasTexture => _current is { Width: > 0, Height: > 0 };
    internal int TextureWidth => _current?.Width ?? 0;
    internal int TextureHeight => _current?.Height ?? 0;
    internal bool SurfaceIsP3 => _surfaceP3;
    internal int SurfaceColorspace { get; private set; }
    internal int FramebufferEncoding { get; private set; }
    internal long TextureBytes => Interlocked.Read(ref _textureBytes);

    internal void Create(ISurfaceHolder surface, double density, bool allowP3 = true, bool allowPassthrough = true)
    {
        lock (_sync)
        {
            if (_disposed) return;
            ReleaseContext();
            var display = EGL14.EglGetDisplay(EGL14.EglDefaultDisplay);
            if (display == null) throw new InvalidOperationException("EglGetDisplay 失败");
            int[] version = new int[2];
            if (!EGL14.EglInitialize(display, version, 0, version, 1))
                throw new InvalidOperationException($"EglInitialize 失败：0x{EGL14.EglGetError():X}");
            _display = display;

            int[] configAttribs =
            [
                EGL14.EglRenderableType, EglOpenGles3Bit,
                EGL14.EglSurfaceType, EGL14.EglWindowBit,
                EGL14.EglRedSize, 8,
                EGL14.EglGreenSize, 8,
                EGL14.EglBlueSize, 8,
                EGL14.EglAlphaSize, 0,
                EGL14.EglDepthSize, 0,
                EGL14.EglStencilSize, 0,
                EglNone,
            ];
            var configs = new EGLConfig[1];
            int[] count = new int[1];
            if (!EGL14.EglChooseConfig(display, configAttribs, 0, configs, 0, 1, count, 0) || count[0] < 1)
                throw new InvalidOperationException("EglChooseConfig 失败");
            _config = configs[0];

            int[] contextAttribs = [EGL14.EglContextClientVersion, 3, EglNone];
            var context = EGL14.EglCreateContext(display, _config, EGL14.EglNoContext, contextAttribs, 0);
            if (context == null || context == EGL14.EglNoContext)
                throw new InvalidOperationException($"EglCreateContext 失败：0x{EGL14.EglGetError():X}");
            _context = context;

            _surface = CreateWindowSurface(surface, allowP3, allowPassthrough);
            try { BindOutput(); }
            catch (InvalidOperationException exception) when (_surfaceP3)
            {
                Console.WriteLine($"[Native] P3 输出无法确认，回退 sRGB：{exception.Message}");
                EGL14.EglMakeCurrent(display, EGL14.EglNoSurface, EGL14.EglNoSurface, EGL14.EglNoContext);
                if (_surface != null && _surface != EGL14.EglNoSurface) EGL14.EglDestroySurface(display, _surface);
                _surface = CreateWindowSurface(surface, allowP3: false, allowPassthrough: false);
                BindOutput();
            }
            AndroidDisplayCapabilities.SurfaceIsP3 = _surfaceP3;
            BuildProgram();
            Console.WriteLine($"[Native] EGL 就绪：GLES={GLES20.GlGetString(GLES20.GlVersion)}，P3表面={_surfaceP3}，"
                + $"colorspace=0x{SurfaceColorspace:X} framebuffer=0x{FramebufferEncoding:X}，density={density:0.###}");

            void BindOutput()
            {
                if (_surface == null || _surface == EGL14.EglNoSurface)
                    throw new InvalidOperationException($"EglCreateWindowSurface 失败：0x{EGL14.EglGetError():X}");
                if (!EGL14.EglMakeCurrent(display, _surface, _surface, _context))
                    throw new InvalidOperationException($"EglMakeCurrent 失败：0x{EGL14.EglGetError():X}");
                int[] actual = new int[1];
                if (EGL14.EglQuerySurface(display, _surface, EglGlColorspaceKhr, actual, 0))
                    SurfaceColorspace = actual[0];
                else
                {
                    _ = EGL14.EglGetError();
                    SurfaceColorspace = 0;
                    if (_surfaceP3) throw new InvalidOperationException("无法确认 P3 表面的实际色彩空间");
                }
                if (_surfaceP3 && SurfaceColorspace is not (EglGlColorspaceDisplayP3Ext or EglGlColorspaceDisplayP3PassthroughExt))
                    throw new InvalidOperationException($"P3 表面返回未知色彩空间 0x{SurfaceColorspace:X}");
                GLES30.GlGetFramebufferAttachmentParameteriv(GLES20.GlFramebuffer, 0x0405,
                    GlFramebufferAttachmentColorEncoding, actual, 0);
                int error = GLES20.GlGetError();
                if (error != GLES20.GlNoError || actual[0] is not (GlSrgb or GlLinear))
                    throw new InvalidOperationException($"无法确认输出帧缓冲编码：0x{actual[0]:X}, GL=0x{error:X}");
                FramebufferEncoding = actual[0];
            }
        }
    }

    private EGLSurface? CreateWindowSurface(ISurfaceHolder surface, bool allowP3, bool allowPassthrough)
    {
        if (_display == null || _config == null) return null;
        string extensions = " " + EGL14.EglQueryString(_display, EGL14.EglExtensions) + " ";
        bool Has(string name) => extensions.Contains(" " + name + " ", StringComparison.Ordinal);
        if (allowP3 && AndroidDisplayCapabilities.DisplaySupportsP3 && Has("EGL_KHR_gl_colorspace"))
        {
            if (allowPassthrough && Has("EGL_EXT_gl_colorspace_display_p3_passthrough")
                && TryCreate(EglGlColorspaceDisplayP3PassthroughExt) is { } passthrough)
            {
                _surfaceP3 = true;
                return passthrough;
            }
            if (Has("EGL_EXT_gl_colorspace_display_p3") && TryCreate(EglGlColorspaceDisplayP3Ext) is { } p3)
            {
                _surfaceP3 = true;
                return p3;
            }
        }
        _surfaceP3 = false;
        if (Has("EGL_KHR_gl_colorspace") && TryCreate(EglGlColorspaceSrgbKhr) is { } srgb)
            return srgb;
        return EGL14.EglCreateWindowSurface(_display, _config, surface.Surface!, [EglNone], 0);

        EGLSurface? TryCreate(int colorspace)
        {
            var candidate = EGL14.EglCreateWindowSurface(_display, _config, surface.Surface!,
                [EglGlColorspaceKhr, colorspace, EglNone], 0);
            if (candidate != null && candidate != EGL14.EglNoSurface) return candidate;
            Console.WriteLine($"[Native] EGL 色彩空间 0x{colorspace:X} 被拒绝，尝试下一输出（0x{EGL14.EglGetError():X}）");
            return null;
        }
    }

    /// <summary>窗口尺寸变化：更新视口像素并重绘。</summary>
    /// <param name="width">宽（像素）。</param>
    /// <param name="height">高（像素）。</param>
    internal void Resize(int width, int height)
    {
        lock (_sync)
        {
            if (_disposed) return;
            if (width < 1 || height < 1) return;
            _width = width;
            _height = height;
            if (_display != null && _surface != null && _context != null)
                EGL14.EglMakeCurrent(_display, _surface, _surface, _context);
        }
    }

    /// <summary>
    /// 上传/切换纹理：当前帧与最近两张按 <see cref="DecodedImage"/> 实例身份缓存（只持弱引用，不延长像素租约）。
    /// 同一实例直接复用已有纹理、不重传；新建纹理优先复用同尺寸的空纹理，避免驱动在 <c>glGenTextures</c> 分配时停顿。
    /// </summary>
    /// <param name="image">展示图像（所有权在调用方，上传在调用期间同步完成）。</param>
    internal void SetImage(DecodedImage? image, DecodedImage? prepared = null)
    {
        long submitStart = Stopwatch.GetTimestamp();
        lock (_sync)
        {
            SubmitLocked(image, prepared, submitStart);
        }
    }

    internal bool NeedsPreparation(DecodedImage image)
    {
        lock (_sync)
        {
            if (_current?.Matches(image) == true || _recentTextures.Exists(entry => entry.Matches(image))) return false;
            return image.ColorSpace != (_surfaceP3 ? ImageColorSpace.DisplayP3 : ImageColorSpace.Srgb);
        }
    }

    internal long RecentTextureByteLimit { get; set; } = 384L << 20;

    private void SubmitLocked(DecodedImage? image, DecodedImage? prepared, long submitStart)
    {
        if (_disposed) return;
        if (image == null)
        {
            Bind(null);
            return;
        }
        if (image.Encoding != PixelEncoding.Rgba8)
            throw new NotSupportedException($"Android 主图纹理不支持 {image.Encoding}");
        if (_current?.Matches(image) == true)
        {
            // 同一产物已在显存：切图零上传，GL 四段记 0 ms 缓存命中
            AndroidUploadStats.RecordCacheHit(image.Width, image.Height, ElapsedMs(submitStart));
            return;
        }
        if (TakeCached(image) is { } cached)
        {
            Bind(cached);
            AndroidUploadStats.RecordCacheHit(image.Width, image.Height, ElapsedMs(submitStart));
            return;
        }
        if (_display == null || _surface == null || _context == null) return;
        if (image.Pixels.LongLength < (long)(image.Height - 1) * image.Stride
            + (long)image.Width * DecodedImage.BytesPerPixel(image.Encoding))
            throw new ObjectDisposedException(nameof(image));

        var makeCurrentStart = Stopwatch.GetTimestamp();
        EGL14.EglMakeCurrent(_display, _surface, _surface, _context);
        double makeCurrentMs = ElapsedMs(makeCurrentStart);

        var source = image;
        var targetSpace = _surfaceP3 ? ImageColorSpace.DisplayP3 : ImageColorSpace.Srgb;
        bool converted = prepared == null && (image.Encoding != PixelEncoding.Rgba8 || image.ColorSpace != targetSpace);
        double convertMs = 0;
        DecodedImage? conversionLease = null;
        TextureEntry? uploading = null;
        try
        {
            if (prepared != null)
            {
                if (prepared.Encoding != PixelEncoding.Rgba8 || prepared.ColorSpace != targetSpace
                    || prepared.Width != image.Width || prepared.Height != image.Height
                    || prepared.Orientation != image.Orientation || prepared.SourceWidth != image.SourceWidth
                    || prepared.SourceHeight != image.SourceHeight || prepared.Pixels.Length == 0)
                    throw new InvalidOperationException("后台准备产物与当前输出不匹配");
                image = prepared;
            }
            else if (converted)
            {
                var convertStart = Stopwatch.GetTimestamp();
                conversionLease = ColorEngine.ToDisplayEncoding(image, PixelEncoding.Rgba8, targetSpace);
                convertMs = ElapsedMs(convertStart);
                image = conversionLease;
            }
            var mark = Stopwatch.GetTimestamp();
            var entry = TakeSpare(image.Width, image.Height);
            bool reusedTexture = entry != null;
            double createMs;
            if (entry == null)
            {
                GLES20.GlGenTextures(1, _handleBuffer, 0);
                entry = new TextureEntry(new WeakReference<DecodedImage>(source), _handleBuffer[0], image.Width, image.Height);
                uploading = entry;
                GLES20.GlBindTexture(GLES20.GlTexture2d, entry.Handle);
                GLES20.GlTexParameteri(GLES20.GlTexture2d, GLES20.GlTextureMinFilter, GLES20.GlLinearMipmapLinear);
                GLES20.GlTexParameteri(GLES20.GlTexture2d, GLES20.GlTextureMagFilter, GLES20.GlLinear);
                GLES20.GlTexParameteri(GLES20.GlTexture2d, GLES20.GlTextureWrapS, GLES20.GlClampToEdge);
                GLES20.GlTexParameteri(GLES20.GlTexture2d, GLES20.GlTextureWrapT, GLES20.GlClampToEdge);
                createMs = ElapsedMs(mark);
            }
            else
            {
                uploading = entry;
                entry.Rebind(new WeakReference<DecodedImage>(source));
                GLES20.GlBindTexture(GLES20.GlTexture2d, entry.Handle);
                createMs = 0;
            }
            mark = Stopwatch.GetTimestamp();
            int rowBytes = checked(image.Width * 4);
            GLES20.GlTexImage2D(GLES20.GlTexture2d, 0, GLES20.GlRgba, image.Width, image.Height, 0,
                GLES20.GlRgba, GLES20.GlUnsignedByte, (ByteBuffer?)null);
            var allocMs = ElapsedMs(mark);
            mark = Stopwatch.GetTimestamp();
            var pixels = image.Pixels;
            long copyBytes = 0;
            int chunks = 0;
            double copyMs = 0;
            if (image.Stride == rowBytes)
            {
                UploadPacked(image, pixels);
                chunks = 1;
            }
            else
            {
                int chunkRows = Math.Max(1, Math.Min(checked(rowBytes * image.Height), 4 * 1024 * 1024) / rowBytes);
                var buffer = UploadBuffer(checked(chunkRows * rowBytes));
                var address = buffer.GetDirectBufferAddress();
                if (address == IntPtr.Zero) throw new InvalidOperationException("无法访问纹理上传缓冲");
                for (int y = 0; y < image.Height; y += chunkRows)
                {
                    int rows = Math.Min(chunkRows, image.Height - y);
                    long copyStart = Stopwatch.GetTimestamp();
                    for (int row = 0; row < rows; row++)
                        Marshal.Copy(pixels, (y + row) * image.Stride, IntPtr.Add(address, row * rowBytes), rowBytes);
                    copyMs += Stopwatch.GetElapsedTime(copyStart).TotalMilliseconds;
                    copyBytes += (long)rows * rowBytes;
                    chunks++;
                    buffer.Position(0);
                    GLES20.GlTexSubImage2D(GLES20.GlTexture2d, 0, 0, y, image.Width, rows,
                        GLES20.GlRgba, GLES20.GlUnsignedByte, buffer);
                }
            }
            var updateMs = ElapsedMs(mark);
            mark = Stopwatch.GetTimestamp();
            GLES20.GlGenerateMipmap(GLES20.GlTexture2d);
            var mipsMs = ElapsedMs(mark);
            var finishStart = Stopwatch.GetTimestamp();
            GLES20.GlBindTexture(GLES20.GlTexture2d, 0);
            Bind(entry);
            uploading = null;
            conversionLease?.Dispose();
            conversionLease = null;
            var finishMs = ElapsedMs(finishStart);
            AndroidUploadStats.Record(source.Width, source.Height, ElapsedMs(submitStart), converted, reusedTexture,
                makeCurrentMs, convertMs, createMs, allocMs, updateMs, mipsMs, finishMs, copyBytes, chunks, copyMs);
        }
        finally
        {
            if (uploading != null && !ReferenceEquals(uploading, _current)) DeleteTexture(uploading);
            conversionLease?.Dispose();
        }
    }

    /// <summary>自给定时间戳起的毫秒数（不建计时器对象）。</summary>
    private static double ElapsedMs(long startTimestamp)
        => Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds;

    private static unsafe void UploadPacked(DecodedImage image, byte[] pixels)
    {
        // GL 在调用返回前消费客户端像素；只在同步上传期间固定租约内的数组，不另建整图副本。
        fixed (byte* address = pixels)
        {
            var handle = global::Android.Runtime.JNIEnv.NewDirectByteBuffer((IntPtr)address,
                checked((long)image.Width * image.Height * 4));
            using var buffer = Java.Lang.Object.GetObject<ByteBuffer>(handle,
                global::Android.Runtime.JniHandleOwnership.TransferLocalRef)
                ?? throw new InvalidOperationException("无法创建纹理像素视图");
            GLES20.GlTexSubImage2D(GLES20.GlTexture2d, 0, 0, 0, image.Width, image.Height,
                GLES20.GlRgba, GLES20.GlUnsignedByte, buffer);
        }
    }

    private ByteBuffer UploadBuffer(int capacity)
    {
        if (_uploadBuffer != null && _uploadBuffer.Capacity() >= capacity) return _uploadBuffer;
        var replacement = ByteBuffer.AllocateDirect(capacity)
            ?? throw new InvalidOperationException("无法分配纹理上传缓冲");
        _uploadBuffer?.Dispose();
        _uploadBuffer = replacement;
        return replacement;
    }

    /// <summary>切换当前纹理：退下的旧纹理进最近缓存（实例已回收或超出容量则释放）。</summary>
    /// <param name="entry">新当前纹理；null 表示清空画面。</param>
    private void Bind(TextureEntry? entry)
    {
        var previous = _current;
        if (ReferenceEquals(previous, entry))
        {
            UpdateTextureBytes();
            return;
        }
        _current = entry;
        if (previous != null) Remember(previous);
        UpdateTextureBytes();
    }

    /// <summary>取走与目标尺寸一致的复用空纹理；没有或尺寸不符返回 null。</summary>
    /// <param name="width">纹理宽（像素）。</param>
    /// <param name="height">纹理高（像素）。</param>
    /// <returns>可复用的纹理条目。</returns>
    private TextureEntry? TakeSpare(int width, int height)
    {
        var spare = _spareTexture;
        if (spare == null || spare.Width != width || spare.Height != height) return null;
        _spareTexture = null;
        return spare;
    }

    /// <summary>取走最近缓存里与给定实例同一对象的纹理（命中即从缓存摘下）。</summary>
    /// <param name="image">目标展示图像。</param>
    /// <returns>命中的纹理；未命中为 null。</returns>
    private TextureEntry? TakeCached(DecodedImage image)
    {
        for (int index = _recentTextures.Count - 1; index >= 0; index--)
        {
            var candidate = _recentTextures[index];
            if (!candidate.Matches(image)) continue;
            _recentTextures.RemoveAt(index);
            return candidate;
        }
        return null;
    }

    /// <summary>把退下的纹理收进最近缓存：源图已回收的条目不再有命中机会，超出容量淘汰最久的一张。</summary>
    /// <param name="entry">退下的纹理。</param>
    private void Remember(TextureEntry entry)
    {
        for (int index = _recentTextures.Count - 1; index >= 0; index--)
        {
            if (_recentTextures[index].IsAlive) continue;
            var dead = _recentTextures[index];
            _recentTextures.RemoveAt(index);
            Retire(dead);
        }
        if (!entry.IsAlive)
        {
            Retire(entry);
            return;
        }
        _recentTextures.Add(entry);
        while (_recentTextures.Count > RecentTextureCapacity)
        {
            var oldest = _recentTextures[0];
            _recentTextures.RemoveAt(0);
            Retire(oldest);
        }
    }

    /// <summary>退掉一张不再需要的纹理：小纹理留作同尺寸复用，已有空纹理或尺寸过大时直接释放。</summary>
    /// <param name="entry">退掉的纹理。</param>
    private void Retire(TextureEntry entry)
    {
        if (_spareTexture == null && entry.Bytes <= SpareTextureByteLimit)
        {
            entry.Rebind(null);
            _spareTexture = entry;
            return;
        }
        DeleteTexture(entry);
    }

    /// <summary>删除一张纹理（GL 调用，必须在持有 EGL 上下文的线程执行）。</summary>
    /// <param name="entry">要删除的纹理。</param>
    private void DeleteTexture(TextureEntry entry)
    {
        if (entry.Handle == 0) return;
        _handleBuffer[0] = entry.Handle;
        GLES20.GlDeleteTextures(1, _handleBuffer, 0);
        _handleBuffer[0] = 0;
    }

    /// <summary>
    /// 压力修剪：丢掉全部非当前纹理与复用空纹理。GL 调用必须在持有 EGL 上下文的线程执行；
    /// 内存管家在探测线程调用宿主，宿主再投递到这里。
    /// </summary>
    internal void TrimCachedTextures()
    {
        lock (_sync)
        {
            if (_disposed) return;
            // 上下文已销毁时纹理随之释放，只剩丢弃句柄。
            if (_display != null && _context != null)
            {
                EGL14.EglMakeCurrent(_display, _surface ?? EGL14.EglNoSurface, _surface ?? EGL14.EglNoSurface, _context);
                foreach (var entry in _recentTextures) DeleteTexture(entry);
                if (_spareTexture != null) DeleteTexture(_spareTexture);
            }
            _recentTextures.Clear();
            _spareTexture = null;
            UpdateTextureBytes();
        }
    }

    /// <summary>刷新纹理占用快照（当前 + 最近缓存 + 空纹理，含 mip 链）。</summary>
    private void UpdateTextureBytes()
    {
        long recentBytes = _spareTexture?.Bytes ?? 0;
        foreach (var entry in _recentTextures) recentBytes += entry.Bytes;
        long limit = Math.Max(0, RecentTextureByteLimit);
        if (recentBytes > limit && _spareTexture != null)
        {
            recentBytes -= _spareTexture.Bytes;
            DeleteTexture(_spareTexture);
            _spareTexture = null;
        }
        while (recentBytes > limit && _recentTextures.Count > 0)
        {
            var oldest = _recentTextures[0];
            _recentTextures.RemoveAt(0);
            recentBytes -= oldest.Bytes;
            DeleteTexture(oldest);
        }
        Interlocked.Exchange(ref _textureBytes, (_current?.Bytes ?? 0) + recentBytes);
    }

    internal bool DrawFrame(PhotoViewport viewport, double density, bool present = true)
    {
        lock (_sync)
        {
            if (_disposed || _display == null || _surface == null || _context == null) return false;
            if (_width < 1 || _height < 1) return false;
            if (!EGL14.EglMakeCurrent(_display, _surface, _surface, _context)) return false;
            GLES20.GlBindFramebuffer(GLES20.GlFramebuffer, 0);
            GLES20.GlViewport(0, 0, _width, _height);
            GLES20.GlDisable(GLES20.GlBlend);
            GLES20.GlDisable(GLES20.GlDepthTest);
            GLES20.GlClearColor(0f, 0f, 0f, 1f);
            GLES20.GlClear(GLES20.GlColorBufferBit);
            if (HasTexture && _program != 0 && viewport.ImageWidth > 0 && viewport.ImageHeight > 0)
            {
                GLES20.GlUseProgram(_program);
                if (_linearizeHandle < 0) _linearizeHandle = GLES20.GlGetUniformLocation(_program, "uLinearize");
                GLES20.GlUniform1i(_linearizeHandle, FramebufferEncoding == GlSrgb && !_bypassOutputTransfer ? 1 : 0);
                double scale = Math.Max(1e-9, viewport.Scale) * density;
                double drawWidth = viewport.ImageWidth * scale;
                double drawHeight = viewport.ImageHeight * scale;
                double centerX = _width / 2.0 + viewport.PanX * density;
                double centerY = _height / 2.0 + viewport.PanY * density;
                double left = centerX - drawWidth / 2;
                double top = centerY - drawHeight / 2;
                DrawQuad(viewport.Orientation, (float)(left / _width * 2 - 1), (float)(1 - top / _height * 2),
                    (float)(drawWidth / _width * 2), (float)(drawHeight / _height * 2));
            }
            return present && EGL14.EglSwapBuffers(_display, _surface);
        }
    }

    /// <summary>绘制四角 UV 已按 EXIF 方向置换的矩形（NDC 左上角 + 宽高）。</summary>
    /// <param name="orientation">EXIF 方向 1–8。</param>
    /// <param name="x">左边缘（NDC）。</param>
    /// <param name="y">上边缘（NDC）。</param>
    /// <param name="width">宽度（NDC）。</param>
    /// <param name="height">高度（NDC）。</param>
    private void DrawQuad(int orientation, float x, float y, float width, float height)
    {
        if (!EnsureVertexBuffer()) return;
        float right = x + width, bottom = y - height;
        var (u0, v0) = MapUv(orientation, 0, 0);
        var (u1, v1) = MapUv(orientation, 1, 0);
        var (u2, v2) = MapUv(orientation, 0, 1);
        var (u3, v3) = MapUv(orientation, 1, 1);
        var vertices = _vertices;
        vertices[0] = x;
        vertices[1] = y;
        vertices[2] = u0;
        vertices[3] = v0;
        vertices[4] = right;
        vertices[5] = y;
        vertices[6] = u1;
        vertices[7] = v1;
        vertices[8] = x;
        vertices[9] = bottom;
        vertices[10] = u2;
        vertices[11] = v2;
        vertices[12] = right;
        vertices[13] = bottom;
        vertices[14] = u3;
        vertices[15] = v3;
        var buffer = _vertexBuffer!;
        var floats = _vertexFloats!;
        floats.Position(0);
        floats.Put(vertices);
        GLES20.GlUseProgram(_program);
        GLES20.GlActiveTexture(GLES20.GlTexture0);
        GLES20.GlBindTexture(GLES20.GlTexture2d, _current?.Handle ?? 0);
        GLES20.GlEnableVertexAttribArray(_positionHandle);
        GLES20.GlEnableVertexAttribArray(_texCoordHandle);
        buffer.Position(0);
        GLES20.GlVertexAttribPointer(_positionHandle, 2, GLES20.GlFloat, false, VertexStrideBytes, buffer);
        buffer.Position(VertexTexCoordByteOffset);
        GLES20.GlVertexAttribPointer(_texCoordHandle, 2, GLES20.GlFloat, false, VertexStrideBytes, buffer);
        GLES20.GlDrawArrays(GLES20.GlTriangleStrip, 0, 4);
        GLES20.GlDisableVertexAttribArray(_positionHandle);
        GLES20.GlDisableVertexAttribArray(_texCoordHandle);
    }

    private bool EnsureVertexBuffer()
    {
        if (_vertexBuffer != null && _vertexFloats != null) return true;
        ByteBuffer? buffer = null;
        FloatBuffer? floats = null;
        try
        {
            buffer = ByteBuffer.AllocateDirect(VertexBufferBytes);
            floats = buffer?.Order(ByteOrder.NativeOrder()!)?.AsFloatBuffer();
            if (floats == null || floats.Capacity() < VertexFloatCount) return false;
            _vertexBuffer = buffer;
            _vertexFloats = floats;
            _vertexAllocations++;
            buffer = null;
            floats = null;
            return true;
        }
        finally
        {
            floats?.Dispose();
            buffer?.Dispose();
        }
    }

    /// <summary>验收用：顶点缓冲对象身份、累计分配次数与当前 16 个 float。</summary>
    internal (object? Buffer, object? Floats, int Allocations, float[] Values) VertexStateForChecks()
    {
        lock (_sync)
        {
            if (_vertexFloats == null) return (_vertexBuffer, null, _vertexAllocations, Array.Empty<float>());
            var values = new float[VertexFloatCount];
            _vertexFloats.Position(0);
            _vertexFloats.Get(values);
            return (_vertexBuffer, _vertexFloats, _vertexAllocations, values);
        }
    }

    /// <summary>验收用：是否已有可复用的空纹理。</summary>
    internal bool HasSpareTextureForChecks
    {
        get { lock (_sync) return _spareTexture != null; }
    }

    /// <summary>验收用：最近缓存里的纹理张数。</summary>
    internal int RecentTextureCountForChecks
    {
        get { lock (_sync) return _recentTextures.Count; }
    }

    /// <summary>显示朝向归一化坐标 → 源纹理坐标（与 <see cref="PhotoViewport"/> 的显示朝向一致）。</summary>
    /// <param name="orientation">EXIF 方向 1–8。</param>
    /// <param name="x">显示横向归一化坐标。</param>
    /// <param name="y">显示纵向归一化坐标。</param>
    /// <returns>源纹理坐标。</returns>
    private static (float U, float V) MapUv(int orientation, float x, float y) => orientation switch
    {
        2 => (1 - x, y),
        3 => (1 - x, 1 - y),
        4 => (x, 1 - y),
        5 => (y, x),
        6 => (y, 1 - x),
        7 => (1 - y, 1 - x),
        8 => (1 - y, x),
        _ => (x, y),
    };

    /// <summary>
    /// 校验当前纹理的原始存储是否等于源图按实际输出表面色域规范后的像素（逐通道精确比较）。
    /// 上传前按表面色域转换，因此预期值取 <see cref="ColorEngine.ToDisplayEncoding"/> 的结果而不是源图码值；
    /// 纹理身份仍按原源图实例核对（缓存键是源图，不是转换产物）。
    /// </summary>
    /// <param name="image">已上传的展示图像（源图实例）。</param>
    /// <returns>是否逐通道一致，以及边界行/像素与 GL 错误信息。</returns>
    internal (bool Matches, string Details) CheckTexturePixelsForChecks(DecodedImage image)
    {
        lock (_sync)
        {
            if (_current?.Matches(image) != true || _display == null || _surface == null || _context == null)
                return (false, "texture unavailable");
            var targetSpace = _surfaceP3 ? ImageColorSpace.DisplayP3 : ImageColorSpace.Srgb;
            using var expected = image.Encoding != PixelEncoding.Rgba8 || image.ColorSpace != targetSpace
                ? ColorEngine.ToDisplayEncoding(image, PixelEncoding.Rgba8, targetSpace) : null;
            var reference = expected ?? image;
            if (reference.Width != image.Width || reference.Height != image.Height)
                return (false, $"expected {reference.Width}x{reference.Height} vs {image.Width}x{image.Height}");
            EGL14.EglMakeCurrent(_display, _surface, _surface, _context);
            int[] previous = new int[1], framebuffer = new int[1];
            GLES20.GlGetIntegerv(GLES20.GlFramebufferBinding, previous, 0);
            GLES20.GlGenFramebuffers(1, framebuffer, 0);
            using var pixel = ByteBuffer.AllocateDirect(4)!;
            int checkedPixels = 0;
            try
            {
                GLES20.GlBindFramebuffer(GLES20.GlFramebuffer, framebuffer[0]);
                GLES20.GlFramebufferTexture2D(GLES20.GlFramebuffer, GLES20.GlColorAttachment0,
                    GLES20.GlTexture2d, _current.Handle, 0);
                if (GLES20.GlCheckFramebufferStatus(GLES20.GlFramebuffer) != GLES20.GlFramebufferComplete)
                    return (false, "incomplete framebuffer");
                int chunkRows = Math.Max(1, 4 * 1024 * 1024 / checked(image.Width * 4));
                var rows = new SortedSet<int> { 0, image.Height - 1 };
                for (int y = chunkRows; y < image.Height; y += chunkRows)
                {
                    rows.Add(y - 1);
                    rows.Add(y);
                }
                foreach (int y in rows)
                foreach (int x in new[] { 0, image.Width / 2, image.Width - 1 })
                {
                    pixel.Position(0);
                    GLES20.GlReadPixels(x, y, 1, 1, GLES20.GlRgba, GLES20.GlUnsignedByte, pixel);
                    int offset = y * reference.Stride + x * 4;
                    for (int channel = 0; channel < 4; channel++)
                    {
                        byte actual = unchecked((byte)pixel.Get(channel));
                        if (actual != reference.Pixels[offset + channel])
                            return (false, $"pixel({x},{y}) channel={channel} gpu={actual} expected={reference.Pixels[offset + channel]}");
                    }
                    checkedPixels++;
                }
                int error = GLES20.GlGetError();
                return (error == GLES20.GlNoError, $"pixels={checkedPixels} rows={rows.Count} glError={error}");
            }
            finally
            {
                GLES20.GlBindFramebuffer(GLES20.GlFramebuffer, previous[0]);
                GLES20.GlDeleteFramebuffers(1, framebuffer, 0);
            }
        }
    }

    /// <summary>
    /// 回读当前照片在视口归一化坐标处的真实输出像素（默认帧缓冲，非屏幕截图），供色彩与像素验收使用。
    /// </summary>
    /// <param name="viewport">共享视口。</param>
    /// <param name="density">屏幕密度。</param>
    /// <param name="u">显示横向归一化坐标。</param>
    /// <param name="v">显示纵向归一化坐标。</param>
    /// <param name="bypassOutputTransfer">为 true 时跳过着色器对 sRGB 帧缓冲的逆传递，取原始纹理编码值。</param>
    /// <returns>回读到的 RGBA 通道。</returns>
    internal (byte R, byte G, byte B, byte A) ReadPixelForChecks(PhotoViewport viewport, double density,
        double u, double v, bool bypassOutputTransfer)
    {
        lock (_sync)
        {
            if (!HasTexture || _width < 1 || _height < 1)
                throw new InvalidOperationException("无可回读的照片表面");
            var point = viewport.ViewPoint(u, v);
            int x = (int)Math.Floor(point.X * density);
            int y = _height - 1 - (int)Math.Floor(point.Y * density);
            if (x < 0 || x >= _width || y < 0 || y >= _height)
                throw new ArgumentOutOfRangeException(nameof(u), "采样点不在当前视口内");
            try
            {
                _bypassOutputTransfer = bypassOutputTransfer;
                DrawFrame(viewport, density, present: false);
                using var pixel = ByteBuffer.AllocateDirect(4)!;
                GLES20.GlReadPixels(x, y, 1, 1, GLES20.GlRgba, GLES20.GlUnsignedByte, pixel);
                int error = GLES20.GlGetError();
                if (error != GLES20.GlNoError)
                    throw new InvalidOperationException($"照片回读失败：0x{error:X}");
                return ((byte)pixel.Get(0), (byte)pixel.Get(1), (byte)pixel.Get(2), (byte)pixel.Get(3));
            }
            finally
            {
                _bypassOutputTransfer = false;
                DrawFrame(viewport, density);
            }
        }
    }

    /// <summary>编译并链接照片着色器。</summary>
    private void BuildProgram()
    {
        int vertex = Compile(GLES20.GlVertexShader, VertexShader);
        int fragment = Compile(GLES20.GlFragmentShader, FragmentShader);
        _program = GLES20.GlCreateProgram();
        GLES20.GlAttachShader(_program, vertex);
        GLES20.GlAttachShader(_program, fragment);
        GLES20.GlLinkProgram(_program);
        int[] linked = new int[1];
        GLES20.GlGetProgramiv(_program, GLES20.GlLinkStatus, linked, 0);
        if (linked[0] == 0)
            throw new InvalidOperationException($"着色器链接失败：{GLES20.GlGetProgramInfoLog(_program)}");
        GLES20.GlDeleteShader(vertex);
        GLES20.GlDeleteShader(fragment);
        _positionHandle = GLES20.GlGetAttribLocation(_program, "aPosition");
        _texCoordHandle = GLES20.GlGetAttribLocation(_program, "aTexCoord");
        GLES20.GlUseProgram(_program);
        int textureHandle = GLES20.GlGetUniformLocation(_program, "uTexture");
        if (textureHandle >= 0) GLES20.GlUniform1i(textureHandle, 0);
    }

    /// <summary>编译单个着色器。</summary>
    /// <param name="type">着色器类型。</param>
    /// <param name="source">源文本。</param>
    /// <returns>着色器句柄。</returns>
    private static int Compile(int type, string source)
    {
        int shader = GLES20.GlCreateShader(type);
        GLES20.GlShaderSource(shader, source);
        GLES20.GlCompileShader(shader);
        int[] compiled = new int[1];
        GLES20.GlGetShaderiv(shader, GLES20.GlCompileStatus, compiled, 0);
        if (compiled[0] == 0)
            throw new InvalidOperationException($"着色器编译失败：{GLES20.GlGetShaderInfoLog(shader)}");
        return shader;
    }

    /// <summary>释放 EGL 与 GL 资源（上下文销毁即释放其纹理，这里只丢弃句柄）。</summary>
    private void ReleaseContext()
    {
        if (_display != null)
        {
            EGL14.EglMakeCurrent(_display, EGL14.EglNoSurface, EGL14.EglNoSurface, EGL14.EglNoContext);
            if (_surface != null && _surface != EGL14.EglNoSurface) EGL14.EglDestroySurface(_display, _surface);
            if (_context != null && _context != EGL14.EglNoContext) EGL14.EglDestroyContext(_display, _context);
            EGL14.EglTerminate(_display);
        }
        _display = null;
        _surface = null;
        _context = null;
        _config = null;
        _current = null;
        _recentTextures.Clear();
        _spareTexture = null;
        UpdateTextureBytes();
        _program = 0;
        _positionHandle = _texCoordHandle = _linearizeHandle = -1;
        _bypassOutputTransfer = false;
        _surfaceP3 = false;
        SurfaceColorspace = FramebufferEncoding = 0;
        AndroidDisplayCapabilities.SurfaceIsP3 = false;
    }

    /// <summary>删除全部纹理（GL 调用，上下文仍有效时执行）。</summary>
    private void DeleteAllTextures()
    {
        if (_current != null)
        {
            DeleteTexture(_current);
            _current = null;
        }
        foreach (var entry in _recentTextures) DeleteTexture(entry);
        _recentTextures.Clear();
        if (_spareTexture != null)
        {
            DeleteTexture(_spareTexture);
            _spareTexture = null;
        }
        UpdateTextureBytes();
    }

    /// <summary>释放全部资源。</summary>
    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            if (_display != null)
            {
                EGL14.EglMakeCurrent(_display, _surface ?? EGL14.EglNoSurface, _surface ?? EGL14.EglNoSurface, _context ?? EGL14.EglNoContext);
                DeleteAllTextures();
                if (_program != 0) GLES20.GlDeleteProgram(_program);
            }
            ReleaseContext();
            _uploadBuffer?.Dispose();
            _uploadBuffer = null;
            _vertexFloats?.Dispose();
            _vertexFloats = null;
            _vertexBuffer?.Dispose();
            _vertexBuffer = null;
        }
    }

    /// <summary>
    /// 一张已上传的主图纹理，按 <see cref="DecodedImage"/> 实例身份缓存。
    /// 只持实例的弱引用：缓存的是独立的 GPU 副本，不延长像素租约，也不阻止源图回收。
    /// </summary>
    private sealed class TextureEntry
    {
        /// <summary>构造纹理条目。</summary>
        /// <param name="key">源图实例的弱引用（引用相等即命中）；null 表示空纹理。</param>
        /// <param name="handle">GL 纹理名。</param>
        /// <param name="width">纹理宽（像素）。</param>
        /// <param name="height">纹理高（像素）。</param>
        internal TextureEntry(WeakReference<DecodedImage>? key, int handle, int width, int height)
        {
            Key = key;
            Handle = handle;
            Width = width;
            Height = height;
        }

        /// <summary>源图实例的弱引用；null 表示已退成等待复用的空纹理。</summary>
        internal WeakReference<DecodedImage>? Key { get; private set; }

        /// <summary>GL 纹理名。</summary>
        internal int Handle { get; }

        /// <summary>纹理宽（像素）。</summary>
        internal int Width { get; }

        /// <summary>纹理高（像素）。</summary>
        internal int Height { get; }

        /// <summary>含 mip 链的显存占用估算（字节）：RGBA8 每像素 4 字节，完整 mip 链多出约 1/3。</summary>
        internal long Bytes => (long)Width * Height * 4 * MipChainNumerator / MipChainDenominator;

        /// <summary>源图实例是否仍存活（已回收的条目不会再命中）。</summary>
        internal bool IsAlive => Key is not null && Key.TryGetTarget(out _);

        /// <summary>是否就是给定实例上传的那一帧。</summary>
        /// <param name="image">待比较的已解码图像。</param>
        /// <returns>引用相等时为 true。</returns>
        internal bool Matches(DecodedImage image)
            => Key is not null && Key.TryGetTarget(out var target) && ReferenceEquals(target, image);

        /// <summary>把条目改挂到另一帧（复用空纹理时）或退成空纹理（<paramref name="key"/> 为 null）。</summary>
        /// <param name="key">新的源图弱引用；null 表示退成空纹理。</param>
        internal void Rebind(WeakReference<DecodedImage>? key) => Key = key;
    }
}
