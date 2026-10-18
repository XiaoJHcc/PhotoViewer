using System;
using System.Runtime.InteropServices;
using Android.App;
using Android.Graphics;
using Android.Opengl;
using Android.Views;
using Java.Nio;
using PhotoViewer.Android.Core;
using PhotoViewer.Core.Image;
using PhotoViewer.Shared;

namespace PhotoViewer.Android.Native;

/// <summary>
/// Android 主图 GL 渲染核心（A2）：自有 <see cref="EGL14"/> 窗口表面（不用 GLSurfaceView，
/// 因为其配置接口不暴露 <c>EGL_EXT_gl_colorspace_display_p3</c>）、GLES 3 纹理上传与 mipmap、
/// 八方向 UV 置换、视口矩形绘制，以及导航图的小目标离屏渲染。
/// 色彩：EGL 协商到 Display P3 时表面为 P3，否则回退普通 sRGB 表面；像素值不做任何隐式转换。
/// </summary>
internal sealed class AndroidPhotoRenderer : IDisposable
{
    /// <summary>EGL_KHR_gl_colorspace 的属性名。</summary>
    private const int EglGlColorspaceKhr = 0x309D;
    /// <summary>EGL_EXT_gl_colorspace_display_p3 的 P3 值。</summary>
    private const int EglGlColorspaceDisplayP3Ext = 0x3363;
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
        precision mediump float;
        uniform sampler2D uTexture;
        varying vec2 vTexCoord;
        void main() {
            gl_FragColor = texture2D(uTexture, vTexCoord);
        }
        """;

    private readonly object _sync = new();
    private EGLDisplay? _display;
    private EGLSurface? _surface;
    private EGLContext? _context;
    private EGLConfig? _config;
    private int _width;
    private int _height;
    private int _program;
    private int _positionHandle = -1;
    private int _texCoordHandle = -1;
    private int[] _texture = [];
    private int[] _framebuffer = [];
    private int _textureWidth;
    private int _textureHeight;
    private DecodedImage? _textureSource;
    private bool _disposed;
    private bool _surfaceP3;

    /// <summary>当前纹理是否已建立。</summary>
    internal bool HasTexture => _textureWidth > 0 && _textureHeight > 0;
    /// <summary>纹理尺寸（未应用方向）。</summary>
    internal int TextureWidth => _textureWidth;
    /// <summary>纹理尺寸（未应用方向）。</summary>
    internal int TextureHeight => _textureHeight;
    /// <summary>窗口表面是否以 Display P3 创建。</summary>
    internal bool SurfaceIsP3 => _surfaceP3;
    /// <summary>纹理估算的 GPU 字节。</summary>
    internal long TextureBytes => (long)_textureWidth * _textureHeight * 4;

    /// <summary>创建 EGL 上下文并绑定 SurfaceView 表面。</summary>
    /// <param name="surface">SurfaceView 的窗口表面。</param>
    /// <param name="density">屏幕密度（仅用于诊断）。</param>
    internal void Create(ISurfaceHolder surface, double density)
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

            var windowSurface = CreateWindowSurface(surface, out bool p3);
            if (windowSurface == null || windowSurface == EGL14.EglNoSurface)
            {
                // P3 属性被拒绝时回退普通表面，绝不把 P3 编码值当 sRGB 显示。
                windowSurface = CreateWindowSurface(surface, out p3, allowP3: false);
            }
            if (windowSurface == null || windowSurface == EGL14.EglNoSurface)
                throw new InvalidOperationException($"EglCreateWindowSurface 失败：0x{EGL14.EglGetError():X}");
            _surface = windowSurface;
            _surfaceP3 = p3;
            AndroidDisplayCapabilities.SurfaceIsP3 = p3;

            if (!EGL14.EglMakeCurrent(display, _surface, _surface, _context))
                throw new InvalidOperationException($"EglMakeCurrent 失败：0x{EGL14.EglGetError():X}");
            BuildProgram();
            Console.WriteLine($"[Native] EGL 就绪：GLES={GLES20.GlGetString(GLES20.GlVersion)}，P3表面={p3}，density={density:0.###}");
        }
    }

    /// <summary>尝试创建 P3 窗口表面；P3 不可用或不允许时按默认（sRGB）创建。</summary>
    /// <param name="surface">窗口表面。</param>
    /// <param name="p3">实际是否 P3。</param>
    /// <param name="allowP3">是否允许尝试 P3。</param>
    /// <returns>EGL 表面。</returns>
    private EGLSurface? CreateWindowSurface(ISurfaceHolder surface, out bool p3, bool allowP3 = true)
    {
        p3 = false;
        if (_display == null || _config == null) return null;
        if (allowP3 && AndroidDisplayCapabilities.DisplaySupportsP3 && SupportsP3Colorspace(_display))
        {
            int[] attribs = [EglGlColorspaceKhr, EglGlColorspaceDisplayP3Ext, EglNone];
            var candidate = EGL14.EglCreateWindowSurface(_display, _config, surface.Surface!, attribs, 0);
            if (candidate != null && candidate != EGL14.EglNoSurface)
            {
                p3 = true;
                return candidate;
            }
            Console.WriteLine($"[Native] P3 窗口表面创建失败，回退 sRGB（EGL error=0x{EGL14.EglGetError():X}）");
        }
        else if (allowP3)
        {
            Console.WriteLine($"[Native] P3 不可用：display={AndroidDisplayCapabilities.DisplaySupportsP3} extension={SupportsP3Colorspace(_display)}，回退 sRGB");
        }
        return EGL14.EglCreateWindowSurface(_display, _config, surface.Surface!, [EglNone], 0);
    }

    /// <summary>查询 EGL 扩展串判断 P3 色彩空间是否可用。</summary>
    /// <param name="display">EGL 显示。</param>
    /// <returns>支持时 true。</returns>
    private static bool SupportsP3Colorspace(EGLDisplay display)
    {
        try
        {
            string? extensions = EGL14.EglQueryString(display, EGL14.EglExtensions);
            bool supported = extensions != null
                && extensions.Contains("EGL_EXT_gl_colorspace_display_p3", StringComparison.Ordinal)
                && extensions.Contains("EGL_KHR_gl_colorspace", StringComparison.Ordinal);
            if (!supported)
                Console.WriteLine($"[Native] EGL 无 P3 色彩空间扩展：EGL_EXT_gl_colorspace_display_p3={(extensions?.Contains("EGL_EXT_gl_colorspace_display_p3", StringComparison.Ordinal) == true)} EGL_KHR_gl_colorspace={(extensions?.Contains("EGL_KHR_gl_colorspace", StringComparison.Ordinal) == true)}");
            return supported;
        }
        catch { return false; }
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

    /// <summary>上传/替换纹理；同一 <see cref="DecodedImage"/> 引用只上传一次。</summary>
    /// <param name="image">展示图像（所有权在调用方，上传在调用期间同步完成）。</param>
    internal void SetImage(DecodedImage? image)
    {
        lock (_sync)
        {
            if (_disposed) return;
            if (image == null)
            {
                _textureSource = null;
                _textureWidth = 0;
                _textureHeight = 0;
                return;
            }
            if (ReferenceEquals(image, _textureSource)
                && image.Width == _textureWidth && image.Height == _textureHeight)
                return;
            _textureSource = image;
            _textureWidth = image.Width;
            _textureHeight = image.Height;
            if (_display == null || _surface == null || _context == null) return;
            EGL14.EglMakeCurrent(_display, _surface, _surface, _context);
            if (_texture.Length == 0)
            {
                _texture = new int[1];
                GLES20.GlGenTextures(1, _texture, 0);
            }
            GLES20.GlBindTexture(GLES20.GlTexture2d, _texture[0]);
            GLES20.GlTexParameteri(GLES20.GlTexture2d, GLES20.GlTextureMinFilter, GLES20.GlLinearMipmapLinear);
            GLES20.GlTexParameteri(GLES20.GlTexture2d, GLES20.GlTextureMagFilter, GLES20.GlLinear);
            GLES20.GlTexParameteri(GLES20.GlTexture2d, GLES20.GlTextureWrapS, GLES20.GlClampToEdge);
            GLES20.GlTexParameteri(GLES20.GlTexture2d, GLES20.GlTextureWrapT, GLES20.GlClampToEdge);
            // 分块上传：整图 direct buffer 在 24MP 上会额外占约 96MB，按 4MB 分块走 TexSubImage2D。
            int rowBytes = checked(image.Width * 4);
            int chunkBytes = Math.Min(checked(rowBytes * image.Height), 4 * 1024 * 1024);
            int chunkRows = Math.Max(1, chunkBytes / rowBytes);
            var buffer = ByteBuffer.AllocateDirect(chunkRows * rowBytes) as ByteBuffer;
            GLES20.GlTexImage2D(GLES20.GlTexture2d, 0, GLES20.GlRgba, image.Width, image.Height, 0,
                GLES20.GlRgba, GLES20.GlUnsignedByte, (ByteBuffer?)null);
            if (buffer != null)
            {
                for (int y = 0; y < image.Height; y += chunkRows)
                {
                    int rows = Math.Min(chunkRows, image.Height - y);
                    buffer.Clear();
                    buffer.Put(image.Pixels, y * rowBytes, rows * rowBytes);
                    buffer.Position(0);
                    GLES20.GlTexSubImage2D(GLES20.GlTexture2d, 0, 0, y, image.Width, rows,
                        GLES20.GlRgba, GLES20.GlUnsignedByte, buffer);
                }
            }
            GLES20.GlGenerateMipmap(GLES20.GlTexture2d);
            GLES20.GlBindTexture(GLES20.GlTexture2d, 0);
        }
    }

    /// <summary>绘制一帧：清屏后按共享视口矩形绘制照片（方向由 UV 置换表达）。</summary>
    /// <param name="viewport">共享视口。</param>
    /// <param name="density">屏幕密度。</param>
    internal void DrawFrame(PhotoViewport viewport, double density)
    {
        lock (_sync)
        {
            if (_disposed || _display == null || _surface == null || _context == null) return;
            if (_width < 1 || _height < 1) return;
            if (!EGL14.EglMakeCurrent(_display, _surface, _surface, _context)) return;
            GLES20.GlViewport(0, 0, _width, _height);
            GLES20.GlDisable(GLES20.GlBlend);
            GLES20.GlDisable(GLES20.GlDepthTest);
            GLES20.GlClearColor(0f, 0f, 0f, 1f);
            GLES20.GlClear(GLES20.GlColorBufferBit);
            if (HasTexture && _program != 0 && viewport.ImageWidth > 0 && viewport.ImageHeight > 0)
            {
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
            EGL14.EglSwapBuffers(_display, _surface);
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
        float right = x + width, bottom = y - height;
        var (u0, v0) = MapUv(orientation, 0, 0);
        var (u1, v1) = MapUv(orientation, 1, 0);
        var (u2, v2) = MapUv(orientation, 0, 1);
        var (u3, v3) = MapUv(orientation, 1, 1);
        float[] vertices =
        [
            x, y, u0, v0,
            right, y, u1, v1,
            x, bottom, u2, v2,
            right, bottom, u3, v3,
        ];
        GLES20.GlUseProgram(_program);
        GLES20.GlActiveTexture(GLES20.GlTexture0);
        GLES20.GlBindTexture(GLES20.GlTexture2d, _texture[0]);
        GLES20.GlEnableVertexAttribArray(_positionHandle);
        GLES20.GlEnableVertexAttribArray(_texCoordHandle);
        var buffer = ByteBuffer.AllocateDirect(vertices.Length * 4).Order(ByteOrder.NativeOrder()!) as ByteBuffer;
        if (buffer == null) return;
        buffer.AsFloatBuffer()!.Put(vertices);
        buffer.Position(0);
        GLES20.GlVertexAttribPointer(_positionHandle, 2, GLES20.GlFloat, false, 16, buffer);
        buffer.Position(8);
        GLES20.GlVertexAttribPointer(_texCoordHandle, 2, GLES20.GlFloat, false, 16, buffer);
        GLES20.GlDrawArrays(GLES20.GlTriangleStrip, 0, 4);
        GLES20.GlDisableVertexAttribArray(_positionHandle);
        GLES20.GlDisableVertexAttribArray(_texCoordHandle);
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
    /// 离屏渲染导航图（长边 150 点、按显示朝向比例），供缩放指示器使用；只在展示图变化时调用。
    /// </summary>
    /// <param name="viewport">共享视口。</param>
    /// <param name="density">屏幕密度。</param>
    /// <returns>导航图位图；无纹理或失败时 null。</returns>
    internal Bitmap? CaptureNavigationBitmap(PhotoViewport viewport, double density)
    {
        lock (_sync)
        {
            if (_disposed || _display == null || _surface == null || _context == null || !HasTexture) return null;
            if (!EGL14.EglMakeCurrent(_display, _surface, _surface, _context)) return null;
            var (widthDp, heightDp) = viewport.NavigationSize;
            int width = Math.Max(1, (int)Math.Round(widthDp * density));
            int height = Math.Max(1, (int)Math.Round(heightDp * density));
            // 限制在合理尺寸内，避免离屏目标意外过大。
            double shrink = Math.Min(1, 768.0 / Math.Max(width, height));
            if (shrink < 1)
            {
                width = Math.Max(1, (int)Math.Round(width * shrink));
                height = Math.Max(1, (int)Math.Round(height * shrink));
            }
            int previousFramebuffer = 0;
            GLES20.GlGetIntegerv(GLES20.GlFramebufferBinding, new[] { previousFramebuffer }, 0);
            int[] framebuffer = new int[1];
            int[] texture = new int[1];
            GLES20.GlGenFramebuffers(1, framebuffer, 0);
            GLES20.GlGenTextures(1, texture, 0);
            GLES20.GlBindTexture(GLES20.GlTexture2d, texture[0]);
            GLES20.GlTexParameteri(GLES20.GlTexture2d, GLES20.GlTextureMinFilter, GLES20.GlLinear);
            GLES20.GlTexParameteri(GLES20.GlTexture2d, GLES20.GlTextureMagFilter, GLES20.GlLinear);
            GLES20.GlTexParameteri(GLES20.GlTexture2d, GLES20.GlTextureWrapS, GLES20.GlClampToEdge);
            GLES20.GlTexParameteri(GLES20.GlTexture2d, GLES20.GlTextureWrapT, GLES20.GlClampToEdge);
            GLES20.GlTexImage2D(GLES20.GlTexture2d, 0, GLES20.GlRgba, width, height, 0, GLES20.GlRgba, GLES20.GlUnsignedByte, (ByteBuffer?)null);
            GLES20.GlBindFramebuffer(GLES20.GlFramebuffer, framebuffer[0]);
            GLES20.GlFramebufferTexture2D(GLES20.GlFramebuffer, GLES20.GlColorAttachment0, GLES20.GlTexture2d, texture[0], 0);
            if (GLES20.GlCheckFramebufferStatus(GLES20.GlFramebuffer) != GLES20.GlFramebufferComplete)
            {
                GLES20.GlBindFramebuffer(GLES20.GlFramebuffer, previousFramebuffer);
                GLES20.GlDeleteFramebuffers(1, framebuffer, 0);
                GLES20.GlDeleteTextures(1, texture, 0);
                return null;
            }
            GLES20.GlViewport(0, 0, width, height);
            GLES20.GlClearColor(0f, 0f, 0f, 1f);
            GLES20.GlClear(GLES20.GlColorBufferBit);
            float aspect = (float)(viewport.ImageWidth / Math.Max(1e-9, viewport.ImageHeight));
            float quadWidth = 2f, quadHeight = 2f;
            if (aspect > (float)width / height) quadHeight = quadWidth / aspect;
            else quadWidth = quadHeight * aspect;
            DrawQuad(viewport.Orientation, -quadWidth / 2, quadHeight / 2, quadWidth, quadHeight);

            var pixels = ByteBuffer.AllocateDirect(width * height * 4).Order(ByteOrder.NativeOrder()!) as ByteBuffer;
            Bitmap? bitmap = null;
            if (pixels != null)
            {
                GLES20.GlReadPixels(0, 0, width, height, GLES20.GlRgba, GLES20.GlUnsignedByte, pixels);
                pixels.Position(0);
                int[] colors = new int[width * height];
                for (int y = 0; y < height; y++)
                {
                    // GL 原点在左下，Bitmap 原点在左上。
                    int sourceRow = (height - 1 - y) * width * 4;
                    for (int x = 0; x < width; x++)
                    {
                        int index = sourceRow + x * 4;
                        colors[y * width + x] = Color.Argb(pixels.Get(index + 3),
                            pixels.Get(index), pixels.Get(index + 1), pixels.Get(index + 2));
                    }
                }
                bitmap = OperatingSystem.IsAndroidVersionAtLeast(26)
                    ? Bitmap.CreateBitmap(width, height, Bitmap.Config.Argb8888!, false,
                        ColorSpace.Get((_surfaceP3 ? ColorSpace.Named.DisplayP3 : ColorSpace.Named.Srgb)!)!)
                    : Bitmap.CreateBitmap(width, height, Bitmap.Config.Argb8888!);
                bitmap.SetPixels(colors, 0, width, 0, 0, width, height);
            }
            GLES20.GlBindFramebuffer(GLES20.GlFramebuffer, previousFramebuffer);
            GLES20.GlDeleteFramebuffers(1, framebuffer, 0);
            GLES20.GlDeleteTextures(1, texture, 0);
            GLES20.GlViewport(0, 0, _width, _height);
            return bitmap;
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

    /// <summary>释放 EGL 与 GL 资源。</summary>
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
        _textureSource = null;
        _textureWidth = 0;
        _textureHeight = 0;
        _program = 0;
        _positionHandle = _texCoordHandle = -1;
        _texture = [];
        _framebuffer = [];
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
                if (_texture.Length > 0) GLES20.GlDeleteTextures(1, _texture, 0);
                if (_program != 0) GLES20.GlDeleteProgram(_program);
            }
            ReleaseContext();
        }
    }
}
