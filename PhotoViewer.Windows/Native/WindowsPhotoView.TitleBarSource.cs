using System;
using System.Runtime.InteropServices;
using DirectN;
using Microsoft.Graphics.DirectX;
using Microsoft.UI.Composition;
using Windows.Graphics;
using WinRT;

namespace PhotoViewer.Windows.Native;

/// <summary>
/// WinUI 合成器的 <c>ICompositorInterop</c>(IID 见 <c>Microsoft.UI.Composition.Interop.h</c>):
/// 桌面合成器只有在用应用自己的 D3D11 设备建出 <c>CompositionGraphicsDevice</c> 之后才能创建绘制表面,
/// 而这份设备必须与主交换链是同一个,GPU→GPU 拷贝才不需要跨设备搬运。
/// WinRT 投影不暴露这个入口;Windows.UI.Composition 的同名接口是另一套 IID,不能混用。
/// </summary>
[ComImport]
[Guid("FAB19398-6D19-4D8A-B752-8F096C396069")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface ICompositorInteropWinUI
{
    /// <summary>用给定 D3D11 设备建立合成图形设备。</summary>
    /// <param name="renderingDevice">D3D11 设备的 <c>IUnknown</c>(传引用由调用方释放)。</param>
    /// <param name="graphicsDevice">返回的 <c>ICompositionGraphicsDevice</c>(调用方持有一份引用)。</param>
    /// <returns>HRESULT;设备不受支持时失败。</returns>
    [PreserveSig]
    HRESULT CreateGraphicsDevice(IntPtr renderingDevice, out IntPtr graphicsDevice);
}

/// <summary>
/// <c>ICompositionDrawingSurfaceInterop</c>(IID 见 <c>Microsoft.UI.Composition.Interop.h</c>,WinUI 版本;
/// Windows.UI.DirectComposition 的同名接口 IID 不同)。只声明本文件用到的前两个槽位:绘制期取一块
/// 可直接写入的纹理,写完提交。
/// </summary>
[ComImport]
[Guid("2D6355C2-AD57-4EAE-92E4-4C3EFF65D578")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface ICompositionDrawingSurfaceInteropWinUI
{
    /// <summary>
    /// 开始一次绘制:返回可写入的资源(<paramref name="iid"/> 指定类型)与它在表面内的落点。
    /// 合成器把多块表面放进同一张图集纹理,返回的纹理通常比表面大,必须按落点写。
    /// </summary>
    /// <param name="updateRect">更新矩形;传 <see cref="IntPtr.Zero"/> 表示整块表面。</param>
    /// <param name="iid">想要的资源接口,这里用 <c>ID3D11Texture2D</c>。</param>
    /// <param name="updateObject">返回的资源(调用方持有一份引用)。</param>
    /// <param name="updateOffset">表面左上角在该资源里的像素落点。</param>
    /// <returns>HRESULT。</returns>
    [PreserveSig]
    HRESULT BeginDraw(IntPtr updateRect, in Guid iid, out IntPtr updateObject, out CompositionSurfacePoint updateOffset);

    /// <summary>结束绘制并提交这一块内容给合成器。</summary>
    /// <returns>HRESULT。</returns>
    [PreserveSig]
    HRESULT EndDraw();
}

/// <summary>Win32 <c>POINT</c>:绘制表面在合成图集里的像素落点。</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct CompositionSurfacePoint
{
    /// <summary>横向像素偏移。</summary>
    public int X;

    /// <summary>纵向像素偏移。</summary>
    public int Y;
}

/// <summary>
/// <c>Windows.Foundation.IClosable</c> 的 ABI 形态(IID <c>30d5a829-7fa4-4026-83bb-d75bae4ea99e</c>):
/// WinAppSDK 的投影不给合成图形设备暴露 <c>Close</c>,只能自己 QI —— 这是归还绘制表面显存的唯一入口。
/// WinRT 接口在 <c>IInspectable</c> 三个槽位之后才是自己的方法。
/// </summary>
[ComImport]
[Guid("30D5A829-7FA4-4026-83BB-D75BAE4EA99E")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IClosableWinUI
{
    /// <summary>占位:<c>IInspectable.GetIids</c>。</summary>
    /// <param name="iidCount">接口数。</param>
    /// <param name="iids">接口表。</param>
    /// <returns>HRESULT。</returns>
    [PreserveSig]
    HRESULT GetIids(out uint iidCount, out IntPtr iids);

    /// <summary>占位:<c>IInspectable.GetRuntimeClassName</c>。</summary>
    /// <param name="className">运行时类名。</param>
    /// <returns>HRESULT。</returns>
    [PreserveSig]
    HRESULT GetRuntimeClassName(out IntPtr className);

    /// <summary>占位:<c>IInspectable.GetTrustLevel</c>。</summary>
    /// <param name="trustLevel">信任级别。</param>
    /// <returns>HRESULT。</returns>
    [PreserveSig]
    HRESULT GetTrustLevel(out int trustLevel);

    /// <summary>关闭对象并释放它名下的 GPU 资源。</summary>
    /// <returns>HRESULT。</returns>
    [PreserveSig]
    HRESULT Close();
}

/// <summary>
/// 标题栏取样源的 GPU 桥接(SHELL-02):<c>CompositionVisualSurface</c> 实测取不到 <c>SwapChainPanel</c> 里的
/// 交换链内容(采样源只含 XAML 自己画的元素),所以把交换链缓冲顶部条带以 GPU→GPU 方式复制进一块
/// <c>CompositionDrawingSurface</c>,再让 XAML 采样源取这块表面 —— 采样源里因此同时有照片与标题栏栏底。
///
/// 口径:与主交换链同一个 D3D11 设备、同一种像素格式(<c>R16G16B16A16_FLOAT</c>,线性 scRGB 值域,alpha 忽略),
/// 尺寸为视口宽 × 需要的高度(物理像素);不换也不重绘主交换链,不做 CPU 回读。
/// 生命周期:外壳隐藏/关闭标题栏材质时调 <see cref="ReleaseTitleBarSource"/>;创建或拷贝失败只记一次诊断并置
/// <see cref="TitleBarSourceFailed"/>,渲染循环照常跑,外壳据此回退到交换链内标题栏区段。
/// 全部方法只在 UI 线程调用。
/// </summary>
internal sealed partial class WindowsPhotoView
{
    /// <summary>取样源一个像素的字节数(F16 四通道),用于显存记账。</summary>
    private const long TitleBarSourceBytesPerPixel = 8;

    /// <summary><c>ID3D11Texture2D</c> 的 IID:绘制期直接要一块与本设备同享的纹理,不经过 DXGI 表面包装。</summary>
    private static readonly Guid IID_ID3D11Texture2D = new("6f15aaf2-d208-4e89-9ab4-489535d34f9c");

    /// <summary>交给 XAML 取样源的合成图形设备(建在 <see cref="_device"/> 上)。</summary>
    private CompositionGraphicsDevice? _titleBarGraphicsDevice;

    /// <summary>标题栏取样表面;尺寸变化即换新实例,外壳按实例变化判断是否需要立刻提交一帧。</summary>
    private CompositionDrawingSurface? _titleBarSurface;

    /// <summary>取样表面的绘制入口;只在表面存在期间有效。</summary>
    private ICompositionDrawingSurfaceInteropWinUI? _titleBarSurfaceInterop;

    /// <summary>建立取样表面所用的合成器;换了合成器(重开窗口等)必须重建图形设备。</summary>
    private Compositor? _titleBarCompositor;

    /// <summary>取样表面的像素尺寸。</summary>
    private (uint Width, uint Height) _titleBarSurfaceSize;

    /// <summary>取样源创建或拷贝失败(不再重试):外壳据此回退到交换链内标题栏区段。</summary>
    private bool _titleBarSourceFailed;

    /// <summary>是否已记过一条取样源就位诊断(只记一次,避免每帧刷日志)。</summary>
    private bool _titleBarSourceLogged;

    /// <summary>标题栏取样源占用的显存字节;总记账在 <c>UpdateGpuBytes</c> 里合并。</summary>
    private long TitleBarSourceBytes;

    /// <summary>标题栏取样源是否已失败:失败后 <see cref="PrepareTitleBarSource"/> 一直返回 null,不回退重试。</summary>
    internal bool TitleBarSourceFailed => _titleBarSourceFailed;

    /// <summary>
    /// 准备(或复用)标题栏取样表面:宽度取视口物理宽 <see cref="_pixelWidth"/>,高度按 <paramref name="heightPx"/>
    /// 钳到视口物理高。尺寸或合成器变化时换一块新表面 —— 外壳是按表面实例判断取样源是否变化的,原地 resize
    /// 不会触发提交一帧。面板尚未布局、已失败或已释放时返回 null(外壳据此不显示材质,布局后需再次调用)。
    /// </summary>
    /// <param name="compositor">承载标题栏材质的合成器(必须与可视树同一个)。</param>
    /// <param name="heightPx">条带高度(物理像素,含模糊要取样的外扩量)。</param>
    /// <returns>可交给 <c>CompositionSurfaceBrush</c> 的表面;不可用时为 null。</returns>
    internal ICompositionSurface? PrepareTitleBarSource(Compositor compositor, int heightPx)
    {
        if (_disposed || _titleBarSourceFailed || compositor is null) return null;
        if (!_laidOut || _pixelWidth == 0 || _pixelHeight == 0) return null;

        uint width = _pixelWidth;
        uint height = (uint)Math.Clamp(heightPx, 1, (int)_pixelHeight);
        try
        {
            if (_titleBarSurface is null
                || !ReferenceEquals(_titleBarCompositor, compositor)
                || _titleBarSurfaceSize != (width, height))
            {
                CreateTitleBarSource(compositor, width, height);
            }

            return _titleBarSurface;
        }
        catch (Exception ex)
        {
            FailTitleBarSource(ex);
            return null;
        }
    }

    /// <summary>
    /// 建立取样源:先用本视图的 D3D11 设备在给定合成器上建图形设备,再由它建 F16 绘制表面。
    /// 设备与主交换链相同是「同设备直拷」的前提;表面 alpha 忽略(条带恒不透明,alpha 只由交换链决定)。
    /// 只是尺寸变化时复用图形设备、只换表面(合成器变了才重建设备)。
    /// </summary>
    /// <param name="compositor">合成器。</param>
    /// <param name="width">表面宽(物理像素)。</param>
    /// <param name="height">表面高(物理像素)。</param>
    private void CreateTitleBarSource(Compositor compositor, uint width, uint height)
    {
        if (_titleBarGraphicsDevice is null || !ReferenceEquals(_titleBarCompositor, compositor))
        {
            ReleaseTitleBarSource();
            CreateTitleBarGraphicsDevice(compositor);
        }

        try
        {
            _titleBarSurface = _titleBarGraphicsDevice!.CreateDrawingSurface2(
                new SizeInt32((int)width, (int)height), DirectXPixelFormat.R16G16B16A16Float, DirectXAlphaMode.Ignore);
            if (_titleBarSurface.PixelFormat != DirectXPixelFormat.R16G16B16A16Float)
            {
                throw new InvalidOperationException(
                    $"标题栏取样表面的像素格式是 {_titleBarSurface.PixelFormat},不是 R16G16B16A16Float");
            }

            _titleBarSurfaceInterop = CastExtensions.As<ICompositionDrawingSurfaceInteropWinUI>(_titleBarSurface);
            _titleBarSurfaceSize = (width, height);
            TitleBarSourceBytes = (long)width * height * TitleBarSourceBytesPerPixel;
            UpdateGpuBytes();
        }
        catch (Exception)
        {
            // 表面没建起来不能留半成品:连图形设备一起放掉,交给失败回退。
            _titleBarSurfaceInterop = null;
            _titleBarSurface = null;
            _titleBarSurfaceSize = default;
            TitleBarSourceBytes = 0;
            if (_titleBarGraphicsDevice is { } graphicsDevice)
            {
                _titleBarGraphicsDevice = null;
                _titleBarCompositor = null;
                CloseGraphicsDevice(graphicsDevice);
            }

            throw;
        }
    }

    /// <summary>在给定合成器上用本视图的 D3D11 设备建合成图形设备(同一设备才能直拷)。</summary>
    /// <param name="compositor">合成器。</param>
    private void CreateTitleBarGraphicsDevice(Compositor compositor)
    {
        var interop = CastExtensions.As<ICompositorInteropWinUI>(compositor);
        IntPtr deviceUnknown = Marshal.GetIUnknownForObject(_device.Object);
        try
        {
            interop.CreateGraphicsDevice(deviceUnknown, out var devicePointer).ThrowOnError();
            try
            {
                _titleBarGraphicsDevice = MarshalInspectable<CompositionGraphicsDevice>.FromAbi(devicePointer);
            }
            finally
            {
                // FromAbi 只取托管包装,ABI 那份引用仍归我们,必须自己释放。
                MarshalInspectable<CompositionGraphicsDevice>.DisposeAbi(devicePointer);
            }
        }
        finally
        {
            Marshal.Release(deviceUnknown);
        }

        _titleBarCompositor = compositor;
    }

    /// <summary>
    /// 把交换链缓冲顶部条带复制进取样表面:照片、栏底与输出色彩转换都已画完、<c>Present</c> 之前调用,
    /// 因此拷走的就是这一帧真正上屏的像素(颜色口径与主图完全一致,不需要再做一次转换)。
    /// 先解绑渲染目标再拷;目标纹理由合成器给出,必须按它返回的落点写(表面通常落在图集的一角)。
    /// 失败只记一次诊断并置 <see cref="TitleBarSourceFailed"/>,不再抛进渲染循环。
    /// </summary>
    private unsafe void CopyTitleBarSource()
    {
        if (_disposed || _titleBarSourceFailed) return;
        if (_titleBarSurfaceInterop is null || _titleBarSurfaceSize.Width == 0 || _titleBarSurfaceSize.Height == 0) return;

        // 表面还没跟上新的视口尺寸时只拷重叠部分,不越界也不重建(重建归 PrepareTitleBarSource)。
        uint width = Math.Min(_titleBarSurfaceSize.Width, _pixelWidth);
        uint height = Math.Min(_titleBarSurfaceSize.Height, _pixelHeight);
        if (width == 0 || height == 0) return;

        try
        {
            // 后台缓冲此刻是渲染目标,先解绑再当拷贝源。
            _context.Object.OMSetRenderTargets(0, null, null);
            var iid = IID_ID3D11Texture2D;
            _titleBarSurfaceInterop.BeginDraw(IntPtr.Zero, in iid, out var updateObject, out var updateOffset).ThrowOnError();
            try
            {
                CopySwapChainStrip(updateObject, updateOffset.X, updateOffset.Y, width, height);
            }
            finally
            {
                // BeginDraw 给出的那份引用归我们;提交这一块内容给合成器。
                Marshal.Release(updateObject);
                _titleBarSurfaceInterop.EndDraw().ThrowOnError();
            }
        }
        catch (Exception ex)
        {
            FailTitleBarSource(ex);
        }
    }

    /// <summary>在 <paramref name="updateObject"/> 上按落点做一次同格式的子资源拷贝(纯 GPU,不回读)。</summary>
    /// <param name="updateObject">BeginDraw 返回的资源指针(<c>ID3D11Texture2D</c>)。</param>
    /// <param name="offsetX">表面左上角在资源内的横向落点。</param>
    /// <param name="offsetY">表面左上角在资源内的纵向落点。</param>
    /// <param name="width">拷贝宽(物理像素)。</param>
    /// <param name="height">拷贝高(物理像素)。</param>
    private unsafe void CopySwapChainStrip(IntPtr updateObject, int offsetX, int offsetY, uint width, uint height)
    {
        var target = (ID3D11Texture2D)Marshal.GetObjectForIUnknown(updateObject);
        try
        {
            using var backBuffer = _swapChain.GetBuffer<ID3D11Texture2D>(0);
            var box = new Box3D
            {
                Left = 0,
                Top = 0,
                Front = 0,
                Right = width,
                Bottom = height,
                Back = 1,
            };
            _context.Object.CopySubresourceRegion(target, 0, (uint)offsetX, (uint)offsetY, 0,
                backBuffer.Object, 0, (IntPtr)(&box));
        }
        finally
        {
            // 只放掉 GetObjectForIUnknown 加的那份;BeginDraw 的引用由调用方释放。
            Marshal.ReleaseComObject(target);
        }

        if (!_titleBarSourceLogged)
        {
            _titleBarSourceLogged = true;
            NativeStartupLog.Write(
                $"title bar source: {width}x{height}px 条带已进入取样表面,图集落点={offsetX},{offsetY}");
        }
    }

    /// <summary>
    /// 释放取样源(标题栏隐藏、材质关闭与视图销毁时调用,可多次调用):放掉表面与图形设备,
    /// 记账同步清零。渲染循环随后的 <see cref="CopyTitleBarSource"/> 直接空转。
    /// </summary>
    internal void ReleaseTitleBarSource()
    {
        _titleBarSurfaceInterop = null;
        _titleBarSurface = null;
        _titleBarCompositor = null;
        _titleBarSurfaceSize = default;
        if (_titleBarGraphicsDevice is { } graphicsDevice)
        {
            _titleBarGraphicsDevice = null;
            // 关掉图形设备才真正归还它名下的绘制表面显存;表面本身没有自己的关闭入口。
            CloseGraphicsDevice(graphicsDevice);
        }

        if (TitleBarSourceBytes != 0)
        {
            TitleBarSourceBytes = 0;
            // 视图销毁时总记账随后整体清零,不必在这里回写。
            if (!_disposed) UpdateGpuBytes();
        }
    }

    /// <summary>
    /// 关掉合成图形设备:它名下所有绘制表面的显存随之归还。WinAppSDK 投影不给这个类型暴露 <c>Close</c>,
    /// 只能自己 QI <see cref="IClosableWinUI"/>;取不到接口时只丢引用交给运行时回收,不向上抛。
    /// </summary>
    /// <param name="device">合成图形设备。</param>
    private static void CloseGraphicsDevice(CompositionGraphicsDevice device)
    {
        try
        {
            CastExtensions.As<IClosableWinUI>(device).Close().ThrowOnError();
        }
        catch (Exception ex)
        {
            NativeStartupLog.Fail("title bar source close", ex);
        }
    }

    /// <summary>
    /// 取样源不可用:释放已建资源、置失败标志并记一条诊断。之后 <see cref="PrepareTitleBarSource"/> 一直返回
    /// null,外壳回退到交换链内标题栏区段;不再重试,避免每帧失败。
    /// </summary>
    /// <param name="error">失败原因。</param>
    private void FailTitleBarSource(Exception error)
    {
        if (_titleBarSourceFailed) return;
        _titleBarSourceFailed = true;
        try
        {
            ReleaseTitleBarSource();
        }
        catch (Exception)
        {
            // 收尾失败不影响失败标志本身。
        }

        NativeStartupLog.Fail("title bar source (Microsoft.UI.Composition.Interop)", error);
    }
}
