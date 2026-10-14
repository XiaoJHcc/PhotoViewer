using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Media.Imaging;
using PhotoViewer.Core;
using PhotoViewer.Core.Image;
using PhotoViewer.Core.Memory;
using PhotoViewer.Core.Platform;
using PhotoViewer.Core.Storage;
using PhotoViewer.Platform;
using ReactiveUI;

namespace PhotoViewer.ViewModels.Main;

public class ImageViewModel : ReactiveObject
{
    private readonly MainViewModel _main;
    public MainViewModel Main => _main;

    private Bitmap? _sourceBitmap;
    public Bitmap? SourceBitmap
    {
        get => _sourceBitmap;
        set => this.RaiseAndSetIfChanged(ref _sourceBitmap, value);
    }

    // 当前原片图像（BitmapLoader 缓存持有其租约,本 VM 另持一份调用方租约,DropEnhancement 时归还）
    private DecodedImage? _originalBitmap;

    // ---- P1-M4b 分级代理解码:fit/浏览代理先行,Full 按需热替换 ----
    /// <summary>代理图相对屏幕物理像素的过采样倍率(缩放余量 + 抗双重降采样:2× 超采样让上屏重采样近乎无损,守画质红线)。</summary>
    private const double ProxyOverscan = 2.0;
    /// <summary>视图渲染缩放(物理像素/DIP,由 View 下发;Retina = 2)。</summary>
    private double _viewRenderScale = 1.0;
    /// <summary>全分辨率逻辑尺寸(代理显示期的几何基准;default 表示未设置)。</summary>
    private Vector _logicalImageSize;
    /// <summary>当前在屏代理的实际长边像素(0 = 非代理)。</summary>
    private int _currentProxyLongEdge;
    /// <summary>Full 升级在途去重(0/1)。</summary>
    private int _fullUpgradeInFlight;

    private bool _currentIsProxy;
    /// <summary>当前主图是否代理图(100% 语义锚定全分辨率逻辑尺寸;细节预览/分析需要 Full 源时判读)。</summary>
    public bool CurrentIsProxy
    {
        get => _currentIsProxy;
        private set => this.RaiseAndSetIfChanged(ref _currentIsProxy, value);
    }

    /// <summary>当前渲染表面的代理目标长边:max(ViewSize) × 渲染缩放 × overscan;视图尺寸未知返回 0。</summary>
    private int ComputeProxyTargetLongEdge()
    {
        if (ViewSize.X <= 0 || ViewSize.Y <= 0) return 0;
        double dip = Math.Max(ViewSize.X, ViewSize.Y);
        return (int)Math.Ceiling(dip * _viewRenderScale * ProxyOverscan);
    }

    /// <summary>是否需要 Full 分辨率:非代理图不需要;分析栏可见(细节预览要 100% 源)或缩放超出代理物理能力时需要。</summary>
    private bool NeedsFullDecode()
    {
        if (!CurrentIsProxy) return false;
        if (Main.IsAnalysisViewVisible) return true;
        if (Fit) return false;
        double logicalLong = Math.Max(ImageSize.X, ImageSize.Y);
        if (logicalLong <= 0 || _currentProxyLongEdge <= 0) return true;
        double proxyCoverage = _currentProxyLongEdge / logicalLong; // 代理可支撑的最大物理放大
        return Scale * _viewRenderScale > proxyCoverage;
    }

    /// <summary>缩放/可见性驱动的 Full 升级(幂等去重,代际闸保护,快速翻页自动作废)。</summary>
    private void MaybeUpgradeToFull()
    {
        if (!CurrentIsProxy || !NeedsFullDecode()) return;
        var file = Main.CurrentFile?.File;
        if (file == null) return;
        if (Interlocked.Exchange(ref _fullUpgradeInFlight, 1) != 0) return;

        var generation = Volatile.Read(ref _loadGeneration);
        _ = Task.Run(async () =>
        {
            try
            {
                var full = await BitmapLoader.GetBitmapAsync(file, DecodePriority.P0_Current, default);
                if (full != null && generation == Volatile.Read(ref _loadGeneration)
                    && ReferenceEquals(file, Main.CurrentFile?.File))
                {
                    // EXIF 缺失时代理期的几何基准是代理尺寸,Full 到达时纠正(有 EXIF 时同值,无跳变)
                    _logicalImageSize = new Vector(full.Width, full.Height);
                    ShowDecodedImage(file, full, isProxy: false);
                }
                else
                {
                    full?.Release();
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Full upgrade failed ({file.Name}): {ex.Message}");
            }
            finally
            {
                Interlocked.Exchange(ref _fullUpgradeInFlight, 0);
            }
        });
    }

    /// <summary>代理显示期设置几何基准为全分辨率逻辑尺寸(EXIF 推导,不解码);Full 直接用图像尺寸。</summary>
    private async Task UpdateLogicalImageSizeAsync(IFileRef file, DecodedImage image, bool isProxy)
    {
        if (!isProxy)
        {
            _logicalImageSize = new Vector(image.Width, image.Height);
            return;
        }
        var dims = await BitmapLoader.GetLogicalDisplaySizeAsync(file);
        _logicalImageSize = dims.HasValue
            ? new Vector(dims.Value.Width, dims.Value.Height)
            : new Vector(image.Width, image.Height); // EXIF 不可得:自洽回退(Full 到达时纠正)
    }

    /// <summary>展示一张已解码图像(代理或全分辨率):接管租约、上屏、按需触发增强;几何由 _logicalImageSize 锚定。</summary>
    private void ShowDecodedImage(IFileRef file, DecodedImage image, bool isProxy)
    {
        DropEnhancement();
        _originalBitmap = image;
        CurrentIsProxy = isProxy;
        _currentProxyLongEdge = isProxy ? Math.Max(image.Width, image.Height) : 0;
        Console.WriteLine($"[ImageVM] show {(isProxy ? "proxy" : "full")} {image.Width}x{image.Height} for {file.Name}");
        var token = ++_enhanceToken;
        SourceBitmap = DecodedImageAdapter.ToBitmap(image);
        ImageLoaded?.Invoke(this, file);

        // 增强模式下：后台算好增强版本再原地切换，不阻塞主图显示
        if (IsEnhanced)
            _ = ApplyEnhancementAsync(image, token);
    }

    // 当前增强图像（本 VM 所有,随增强关闭/切图释放）;分析栏/直方图的像素源
    private DecodedImage? _enhancedDecoded;

    /// <summary>当前应显示的已解码图像(增强态为增强图,否则为原片;外部只读,不 Dispose)。</summary>
    internal DecodedImage? CurrentDecodedImage => _enhancedDecoded ?? _originalBitmap;
    // 增强任务竞态闸：每次切图 / 切换增强都自增，作废上一张的在途增强
    private int _enhanceToken;
    // 主图解码代际闸：每次切图自增，作废上一张的在途解码，过期结果不再改写主图
    private int _loadGeneration;

    private Bitmap? _enhancedBitmap;
    /// <summary>
    /// 当前增强位图（本 VM 所有，替换 / 清理时显式 Dispose）；非增强或在途时为 null。
    /// 公开为可观察属性，供分析栏在增强就绪后对增强图重算诊断瓦片（见 <see cref="AnalysisViewModel"/>）。
    /// </summary>
    public Bitmap? EnhancedBitmap
    {
        get => _enhancedBitmap;
        private set => this.RaiseAndSetIfChanged(ref _enhancedBitmap, value);
    }

    private bool _isEnhanced;
    /// <summary>
    /// 增强预览模式：开启后主图原地替换为确定性增强版本，并在切图时对每张持续生效。
    /// 详见 plan-3-1 §1.2 产品化落地（控制栏 toggle，算法走 <see cref="ImageEnhancer"/>）。
    /// </summary>
    public bool IsEnhanced
    {
        get => _isEnhanced;
        private set => this.RaiseAndSetIfChanged(ref _isEnhanced, value);
    }

    private string _HintText = "点击打开文件 或 拖拽图片到此处";
    public string HintText
    {
        get => _HintText;
        set => this.RaiseAndSetIfChanged(ref _HintText, value);
    }

    private string _HintDetail = string.Empty;
    /// <summary>
    /// 错误详情，显示在 HintText 下方，字号较小。
    /// </summary>
    public string HintDetail
    {
        get => _HintDetail;
        set => this.RaiseAndSetIfChanged(ref _HintDetail, value);
    }

    public event EventHandler<IFileRef?>? ImageLoaded;
    
    private Rect _detailHighlightRect;
    public Rect DetailHighlightRect
    {
        get => _detailHighlightRect;
        private set => this.RaiseAndSetIfChanged(ref _detailHighlightRect, value);
    }

    private bool _isDetailHighlightVisible;
    public bool IsDetailHighlightVisible
    {
        get => _isDetailHighlightVisible;
        private set => this.RaiseAndSetIfChanged(ref _isDetailHighlightVisible, value);
    }

    private Rect? _detailHighlightImageRect;

    /// <summary>
    /// 右键菜单"打开文件/打开文件夹"标签文本，根据平台自动切换：
    /// 移动端（Android/iOS）显示"打开文件夹"，桌面端显示"打开文件"
    /// </summary>
    public string OpenFileMenuLabel =>
        OperatingSystem.IsAndroid() || OperatingSystem.IsIOS() ? "打开文件夹" : "打开文件";

    public ImageViewModel(MainViewModel mainViewModel)
    {
        _main = mainViewModel;

        // 统一内存管控(P1-M2):增强位图登记为内存客户端,压力下由 Governor 级联释放
        MemoryGovernor.Register(new EnhancedMemoryClient(this));

        Main.WhenAnyValue(vm => vm.CurrentFile)
            .Subscribe(currentFile =>
            {
                // 每次切图递增代际，作废上一张的在途解码
                var generation = Interlocked.Increment(ref _loadGeneration);
                if (currentFile == null) ClearImage();
                else
                {
                    // 新图未命中缓存时立即清屏（显示空白），旧图不留守；命中(Full 或代理)则走缓存快路径直接上屏
                    if (!BitmapLoader.IsInCache(currentFile.File.PathKey)
                        && !BitmapLoader.IsInCache(currentFile.File.PathKey, ImageSizeClass.Proxy))
                        SourceBitmap = null;
                    _ = LoadImageAsync(currentFile.File, generation);
                }
            });
        
        // 图片加载完成时
        this.WhenAnyValue(vm => vm.SourceBitmap)
            .Subscribe(_ =>
            {
                OnBitmapChanged();
            });

        // 监听影响缩略图显示的属性变化
        this.WhenAnyValue(
            vm => vm.Main.Settings.ShowZoomIndicator,
            vm => vm.Fit,
            vm => vm.Scale,
            vm => vm.Translate,
            vm => vm.ViewSize,
            vm => vm.ImageSize,
            vm => vm.SourceBitmap)
            .Subscribe(_ => UpdateZoomIndicator());

        this.WhenAnyValue(
            vm => vm.Scale,
            vm => vm.Translate,
            vm => vm.ViewSize,
            vm => vm.ImageSize)
            .Subscribe(_ => UpdateDetailHighlightRect());

        // P1-M4b:缩放超出代理能力 / 分析栏打开时,代理图后台升级 Full(同变换热替换)
        // 须同时监听 Fit:ZoomTo/FocusOnImagePoint 先改 Scale、后落 Fit=false,仅听 Scale 时双击/键盘放大永不触发升级
        this.WhenAnyValue(vm => vm.Scale, vm => vm.Fit).Subscribe(_ => MaybeUpgradeToFull());
        Main.WhenAnyValue(vm => vm.IsAnalysisViewVisible).Subscribe(_ => MaybeUpgradeToFull());
    }

    /*
     * 打开图片
     */
    
    public async Task LoadImageAsync(IFileRef file, int generation)
    {
        try
        {
            HintText = string.Empty;
            HintDetail = string.Empty;

            // P1-M4b 分级解码:代理先行上屏,Full 按需(缩放超代理能力/分析栏可见)后台热替换
            // P1-M5 压测对照组(--pv-stress-full):跳过代理档,每翻直接全尺寸解码
            int proxyTarget = StressFlipDriver.ForceFullDecode ? 0 : ComputeProxyTargetLongEdge();
            DecodedImage? proxy = null;

            if (proxyTarget > 0)
            {
                using (DecodeScheduler.TrackP0())
                    proxy = await BitmapLoader.GetBitmapAsync(file, DecodeSizeHint.ProxyTo(proxyTarget), DecodePriority.P0_Current, default);

                // 代际闸：解码期间已切图则丢弃结果并归还本次租约，过期任务不再改写主图状态
                if (generation != Volatile.Read(ref _loadGeneration) || !ReferenceEquals(file, Main.CurrentFile?.File))
                {
                    proxy?.Release();
                    return;
                }
            }

            if (proxy != null && proxy.Width > 0 && proxy.Height > 0)
            {
                await UpdateLogicalImageSizeAsync(file, proxy, isProxy: true);
                ShowDecodedImage(file, proxy, isProxy: true);

                // fit 浏览:代理覆盖屏幕,Full 不解码;否则后台升级 Full,同变换热替换
                MaybeUpgradeToFull();
                return;
            }
            proxy?.Release();

            // 全分辨率路径(代理不可得或视图尺寸未知)
            DecodedImage? image;
            using (DecodeScheduler.TrackP0())
            {
                image = await BitmapLoader.GetBitmapAsync(file, DecodePriority.P0_Current, default);
            }

            // 代际闸：解码期间已切图则丢弃结果并归还本次租约，过期任务不再改写主图状态（位图留在缓存无害）
            if (generation != Volatile.Read(ref _loadGeneration) || !ReferenceEquals(file, Main.CurrentFile?.File))
            {
                image?.Release();
                return;
            }

            if (image == null)
            {
                Console.WriteLine($"Failed to load bitmap for file: {file.Name}");
                DropEnhancement();
                SourceBitmap = null;
                var hint = "无法打开该图片";
                var detail = string.Empty;
                if (HeifLoader.IsHeifFile(file))
                {
                    hint += "（HEIF 格式）";
                    var decodeError = HeifLoader.LastDecodeError;
                    if (!string.IsNullOrEmpty(decodeError))
                        detail = decodeError;
                }
                else if (RawLoader.IsRawFile(file))
                {
                    hint += "（RAW 格式）";
                    var decodeError = RawLoader.LastDecodeError;
                    if (!string.IsNullOrEmpty(decodeError))
                        detail = decodeError;
                }
                HintText = hint;
                HintDetail = detail;
                return;
            }

            // 验证图像有效性
            if (image.Width <= 0 || image.Height <= 0)
            {
                Console.WriteLine($"Invalid image dimensions: {image.Width}x{image.Height} for file: {file.Name}");
                image.Release();
                DropEnhancement();
                SourceBitmap = null;
                HintText = "图片解码无效";
                HintDetail = string.Empty;
                return;
            }

            await UpdateLogicalImageSizeAsync(file, image, isProxy: false);
            ShowDecodedImage(file, image, isProxy: false);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to load image in ImageViewModel ({file.Name}): {ex.Message}");
            // 代际闸：过期任务的失败不应清掉新当前图
            if (generation != Volatile.Read(ref _loadGeneration) || !ReferenceEquals(file, Main.CurrentFile?.File))
                return;
            DropEnhancement();
            SourceBitmap = null;
            HintText = "无法打开该图片";
            HintDetail = $"{ex.GetType().Name}: {ex.Message}";
        }
    }

    public void ClearImage()
    {
        try
        {
            DropEnhancement();
            _logicalImageSize = default;
            CurrentIsProxy = false;
            _currentProxyLongEdge = 0;
            SourceBitmap = null;
            ImageLoaded?.Invoke(this, null);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to clear image: {ex.Message}");
        }
    }

    /// <summary>
    /// 切换增强预览模式：开启则后台算增强版本并原地替换，关闭则还原原片。模式跨切图持续生效。
    /// </summary>
    public async Task ToggleEnhanceAsync()
    {
        IsEnhanced = !IsEnhanced;
        var token = ++_enhanceToken;

        if (IsEnhanced)
        {
            if (_originalBitmap != null)
                await ApplyEnhancementAsync(_originalBitmap, token);
        }
        else
        {
            // 还原原片后再释放增强位图，避免释放仍被 Image.Source 引用的位图
            if (_originalBitmap != null) SourceBitmap = DecodedImageAdapter.ToBitmap(_originalBitmap);
            ReleaseEnhanced();
        }
    }

    /// <summary>
    /// 后台计算增强位图并在校验竞态闸后原地替换主图；过期或已退出增强模式则丢弃结果。
    /// </summary>
    private async Task ApplyEnhancementAsync(DecodedImage original, int token)
    {
        try
        {
            var enhancedImage = await Task.Run(() => ImageEnhancer.Enhance(original));
            if (token != _enhanceToken || !IsEnhanced)
            {
                enhancedImage.Dispose();
                return;
            }
            var enhanced = DecodedImageAdapter.ToBitmap(enhancedImage);
            if (enhanced == null)
            {
                enhancedImage.Dispose();
                return;
            }
            var oldDecoded = _enhancedDecoded;
            _enhancedDecoded = enhancedImage;
            var old = EnhancedBitmap;
            EnhancedBitmap = enhanced;
            SourceBitmap = enhanced;
            if (old != null) UiDispatcher.Post(old.Dispose);
            if (oldDecoded != null) UiDispatcher.Post(oldDecoded.Dispose);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"图片增强失败: {ex.Message}");
        }
    }

    /// <summary>
    /// 释放当前增强位图（本 VM 所有），延后到 UI 线程下一拍执行，避免释放仍在渲染的位图。
    /// </summary>
    private void ReleaseEnhanced()
    {
        var old = EnhancedBitmap;
        EnhancedBitmap = null;
        if (old != null) UiDispatcher.Post(old.Dispose);
        var oldDecoded = _enhancedDecoded;
        _enhancedDecoded = null;
        if (oldDecoded != null) UiDispatcher.Post(oldDecoded.Dispose);
    }

    /// <summary>
    /// 切图 / 出错时丢弃上一张的增强状态：作废在途任务、归还原片租约、释放增强位图。
    /// </summary>
    private void DropEnhancement()
    {
        ++_enhanceToken;
        var oldOriginal = _originalBitmap;
        _originalBitmap = null;
        oldOriginal?.Release();   // P1-M3 租约:原片归还(缓存仍持有自己那份)
        ReleaseEnhanced();
    }

    // ---- 统一内存管控(P1-M2)----

    /// <summary>
    /// 增强位图的内存客户端:Warning 起在 UI 线程关闭增强并释放增强位图(等同用户手动关 toggle 的还原路径)。
    /// 嵌套类可访问宿主私有成员;释放延后一拍,避免释放仍在渲染的位图。
    /// </summary>
    private sealed class EnhancedMemoryClient : IMemoryClient
    {
        private readonly ImageViewModel _vm;
        public EnhancedMemoryClient(ImageViewModel vm) => _vm = vm;
        public string Name => "EnhancedPreview";
        public long FootprintBytes => _vm._enhancedDecoded?.ByteCount ?? 0;
        public void Trim(PressureLevel level)
        {
            if (level < PressureLevel.Warning || _vm._enhancedDecoded == null) return;
            UiDispatcher.Post(() =>
            {
                if (!_vm.IsEnhanced) return;
                _vm.IsEnhanced = false;
                if (_vm._originalBitmap != null)
                    _vm.SourceBitmap = DecodedImageAdapter.ToBitmap(_vm._originalBitmap);
                _vm.ReleaseEnhanced();
            });
        }
    }
    
    /*
     * 预览缩放
     */

    private double _scale;
    public double Scale
    {
        get => _scale;
        set => this.RaiseAndSetIfChanged(ref _scale, value);
    }

    private Vector _translate;
    public Vector Translate
    {
        get => _translate;
        set => this.RaiseAndSetIfChanged(ref _translate, value);
    }
    
    private Vector _imageSize;  // 图片原始大小
    public Vector ImageSize
    {
        get => _imageSize;
        set => this.RaiseAndSetIfChanged(ref _imageSize, value);
    }
    
    private Vector _viewSize;   // 显示区域大小
    public Vector ViewSize
    {
        get => _viewSize;
        set => this.RaiseAndSetIfChanged(ref _viewSize, value);
    }
    
    private bool _fit = true; // 适配屏幕
    public bool Fit
    {
        get => _fit;
        set => this.RaiseAndSetIfChanged(ref _fit, value);
    }

    /// <summary>
    /// 当前视图中心是否处于对焦点附近（由每次移动/缩放后动态计算，或切换到对焦点后显式置 true）。
    /// 切换照片时若为 true，则以新照片的对焦点为中心并保持当前缩放倍率，而非按 UV 坐标定位。
    /// </summary>
    private bool _isFocusedOnFocusPoint;

    private double _fitScale;   // 适配缩放值
    public double FitScale
    {
        get => _fitScale;
        set => this.RaiseAndSetIfChanged(ref _fitScale, value);
    }

    /*
     * 缩放指示器相关属性
     */
    
    private bool _showZoomIndicator;
    public bool ShowZoomIndicator
    {
        get => _showZoomIndicator;
        private set => this.RaiseAndSetIfChanged(ref _showZoomIndicator, value);
    }

    private Vector _thumbnailDisplaySize;
    public Vector ThumbnailDisplaySize
    {
        get => _thumbnailDisplaySize;
        private set => this.RaiseAndSetIfChanged(ref _thumbnailDisplaySize, value);
    }

    private Vector _thumbnailImageSize;
    public Vector ThumbnailImageSize
    {
        get => _thumbnailImageSize;
        private set => this.RaiseAndSetIfChanged(ref _thumbnailImageSize, value);
    }

    private Vector _thumbnailImageOffset;
    public Vector ThumbnailImageOffset
    {
        get => _thumbnailImageOffset;
        private set => this.RaiseAndSetIfChanged(ref _thumbnailImageOffset, value);
    }

    private Vector _viewportFrameSize;
    public Vector ViewportFrameSize
    {
        get => _viewportFrameSize;
        private set => this.RaiseAndSetIfChanged(ref _viewportFrameSize, value);
    }

    private Vector _viewportFramePosition;
    public Vector ViewportFramePosition
    {
        get => _viewportFramePosition;
        private set => this.RaiseAndSetIfChanged(ref _viewportFramePosition, value);
    }

    private string _zoomPercentageText = "100%";
    public string ZoomPercentageText
    {
        get => _zoomPercentageText;
        private set => this.RaiseAndSetIfChanged(ref _zoomPercentageText, value);
    }

    /// <summary>
    /// 更新缩略图相关属性
    /// </summary>
    private void UpdateZoomIndicator()
    {
        // 只在非适配状态且有图片时显示
        ShowZoomIndicator = Main.Settings.ShowZoomIndicator && !Fit && SourceBitmap != null && ImageSize.X > 0 && ImageSize.Y > 0 && ViewSize.X > 0 && ViewSize.Y > 0;

        if (!ShowZoomIndicator) return;

        const double maxThumbnailSize = 150;

        // 计算缩略图显示尺寸（保持原图比例，长边为150）
        var imageAspect = ImageSize.X / ImageSize.Y;
        if (imageAspect > 1) // 横图
        {
            ThumbnailDisplaySize = new Vector(maxThumbnailSize, maxThumbnailSize / imageAspect);
        }
        else // 竖图
        {
            ThumbnailDisplaySize = new Vector(maxThumbnailSize * imageAspect, maxThumbnailSize);
        }

        // 缩略图内的图片尺寸和偏移（居中显示）
        ThumbnailImageSize = ThumbnailDisplaySize;
        ThumbnailImageOffset = new Vector(0, 0);

        // 计算可见区域白框
        UpdateZoomViewportFrame();

        // 计算缩放百分比（保留两位小数）
        ZoomPercentageText = $"{Scale * 100:0.##}%";
    }

    /// <summary>
    /// 计算可见区域白框的位置和大小
    /// </summary>
    private void UpdateZoomViewportFrame()
    {
        if (ImageSize.X <= 0 || ImageSize.Y <= 0 || ViewSize.X <= 0 || ViewSize.Y <= 0) return;

        // 白框尺寸
        // 显示大小 / 显示缩放 = 缩略大小 / 缩略缩放
        var thumbnailScale = ThumbnailDisplaySize.X / ImageSize.X;
        ViewportFrameSize = ViewSize / Scale * thumbnailScale;
        
        // 白框位置
        var frameCenter = Vector.Multiply(GetCenterUV(), ThumbnailDisplaySize) + ThumbnailDisplaySize * 0.5;
        ViewportFramePosition = frameCenter - ViewportFrameSize * 0.5;
    }
    
    /// <summary>
    /// 窗口大小变更时 更新图片框视图
    /// </summary>
    /// <param name="viewSize">视图尺寸(DIP)</param>
    /// <param name="renderScale">渲染缩放(物理像素/DIP,Retina=2;由 View 下发)</param>
    public void UpdateView(Vector viewSize, double renderScale = 1.0)
    {
        _viewRenderScale = renderScale > 0 ? renderScale : 1.0;
        var deltaSize = viewSize - ViewSize;
        ViewSize = viewSize;

        // 代理目标随视图物理像素更新;变大且当前为代理图时重载当前图(缓存规则复用或重解码,守"不放大代理"红线)
        int newTarget = ComputeProxyTargetLongEdge();
        if (newTarget != BitmapLoader.ProxyTargetLongEdge)
        {
            BitmapLoader.ProxyTargetLongEdge = newTarget;
            if (CurrentIsProxy && newTarget > _currentProxyLongEdge && Main.CurrentFile != null)
                _ = LoadImageAsync(Main.CurrentFile.File, Volatile.Read(ref _loadGeneration));
        }

        if (ViewSize.X <= 0 || ViewSize.Y <= 0) return;
        if (Fit) FitToScreen();
        else Translate += deltaSize * 0.5;
    }
    
    /// <summary>
    /// 适应屏幕
    /// </summary>
    public void FitToScreen()
    {
        if (ImageSize == default) return;
        _isFocusedOnFocusPoint = false;
        FitScale = Math.Min(ViewSize.X / ImageSize.X, ViewSize.Y / ImageSize.Y);
        Scale = FitScale;
        var moveToLeft = - ((1.0 - FitScale) * 0.5 * ImageSize);
        var moveLeftToCenter = (ViewSize - FitScale * ImageSize) * 0.5;
        Translate = moveToLeft + moveLeftToCenter;
        Fit = true;
    }

    public void ToggleFit(Vector center)
    {
        _isFocusedOnFocusPoint = false;
        if (!Fit) FitToScreen();
        else ZoomTo(1, center);
    }

    /// <summary>
    /// 切换适配/放大：放大时优先以当前图片对焦点为中心做 100% 放大，
    /// 无对焦数据时回退到视图中心。进入对焦点模式后设置标记，供切换照片时使用。
    /// </summary>
    public void ToggleFit()
    {
        if (!Fit)
        {
            FitToScreen();
            return;
        }
        var focusPoint = GetFocusImagePoint();
        if (focusPoint.HasValue)
        {
            FocusOnImagePoint(focusPoint.Value, 1.0);
            _isFocusedOnFocusPoint = true;
        }
        else
        {
            ZoomTo(1, ViewSize * 0.5);
        }
    }

    /// <summary>
    /// 缩放图片：以窗口指定点为中心，缩放至指定倍率
    /// </summary>
    /// <param name="scale">目标倍率</param>
    /// <param name="center">缩放中心（窗口坐标系）</param>
    public void ZoomTo(double scale, Vector center)
    {
        var imageCenter = ImageSize * 0.5 + Translate;
        Translate += (Scale - scale) / Scale * (center - imageCenter);
        Scale = scale;
        ClampCenter();
        Fit = false;
        _isFocusedOnFocusPoint = IsViewCenteredOnFocusPoint();
    }

    public void ZoomTo(double scale)
    {
        ZoomTo(scale, ViewSize * 0.5);
    }

    /// <summary>
    /// 缩放至预设
    /// </summary>
    /// <param name="levelOffset">放大或缩小几挡</param>
    public void ZoomPreset(int levelOffset)
    {
        ZoomPreset(levelOffset, ViewSize * 0.5);
    }

    /// <summary>
    /// 缩放至预设（带中心）
    /// </summary>
    /// <param name="levelOffset">放大或缩小几挡</param>
    /// <param name="center">缩放中心（窗口坐标系）</param>
    public void ZoomPreset(int levelOffset, Vector center)
    {
        var presets = Main.Settings.ScalePresets;
        var offset = levelOffset;
        if (offset > 0)
        {
            for (int i = 0; i < presets.Count && offset != 0; i++)
            {
                if (presets[i].Value > Scale) offset--;
                if (offset == 0) ZoomTo(presets[i].Value, center);
            }
        }
        else if (offset < 0)
        {
            for (int i = presets.Count - 1; i >= 0 && offset != 0; i--)
            {
                if (presets[i].Value < Scale) offset++;
                if (offset == 0) ZoomTo(presets[i].Value, center);
            }
        }
    }

    /// <summary>
    /// 缩放等比
    /// </summary>
    public void ZoomScale(double scale, Vector center)
    {
        ZoomTo(Scale * scale, center);
    }
    
    public void ZoomScale(double scale)
    {
        ZoomTo(Scale * scale);
    }

    /// <summary>
    /// 移动图片
    /// </summary>
    /// <param name="delta">增量</param>
    public void Move(Vector delta)
    {
        if (Fit) return; // 适应屏幕时不响应移动
        Translate += delta;
        ClampCenter();
        _isFocusedOnFocusPoint = IsViewCenteredOnFocusPoint();
    }

    /// <summary>
    /// 图片切换时保持缩放位置一致
    /// </summary>
    private void OnBitmapChanged()
    {
        try
        {
            if (SourceBitmap == null) return;
            
            // 验证位图尺寸
            if (SourceBitmap.PixelSize.Width <= 0 || SourceBitmap.PixelSize.Height <= 0)
            {
                Console.WriteLine($"Invalid bitmap size in OnBitmapChanged: {SourceBitmap.PixelSize.Width}x{SourceBitmap.PixelSize.Height}");
                return;
            }
            
            // P1-M4b:几何基准用全分辨率逻辑尺寸(代理显示期 _logicalImageSize;Full 期 = 图像尺寸)
            var newImageSize = _logicalImageSize != default
                ? _logicalImageSize
                : new Vector(SourceBitmap.PixelSize.Width, SourceBitmap.PixelSize.Height);
            
            // 对焦点模式下即使尺寸不变也需重新定位到新照片对焦点，其余情况尺寸未变则无需处理
            if (ImageSize == newImageSize && !_isFocusedOnFocusPoint) return;
            
            // 图片尺寸即将变化，提前清空细节高亮矩形，避免旧坐标在新布局下显示到错误位置。
            // DetailPreview.UpdatePreview() 将在下一帧以新图片坐标重新计算并恢复高亮。
            SetDetailHighlight(null);
            
            // 提前保存当前视图状态，ImageSize 变更后这些值将失效
            var uv = GetCenterUV();
            var currentScale = Scale;
            
            if (Fit)
            {
                ImageSize = newImageSize;
                FitToScreen();
            }
            else if (_isFocusedOnFocusPoint)
            {
                // 对焦点模式：定位到新照片的对焦点，保持缩放倍率
                ImageSize = newImageSize;
                var newFocusPoint = GetFocusImagePoint(newImageSize);
                if (newFocusPoint.HasValue)
                {
                    FocusOnImagePoint(newFocusPoint.Value, currentScale);
                    _isFocusedOnFocusPoint = true;
                }
                else
                {
                    SetUVToCenter(uv);
                    _isFocusedOnFocusPoint = false;
                }
            }
            else
            {
                ImageSize = newImageSize;
                SetUVToCenter(uv);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed in OnBitmapChanged: {ex.Message}");
        }
    }

    /// <summary>
    /// 获取当前图片对焦点的像素坐标（使用当前 ImageSize）。
    /// </summary>
    private Vector? GetFocusImagePoint() => GetFocusImagePoint(ImageSize);

    /// <summary>
    /// 检查当前视图中心是否处于对焦点附近（UV 偏差各轴小于 5% 即为附近）。
    /// 每次移动/缩放后调用，动态更新 _isFocusedOnFocusPoint 标记。
    /// </summary>
    private bool IsViewCenteredOnFocusPoint()
    {
        if (Fit || ImageSize.X <= 0 || ImageSize.Y <= 0) return false;
        var pos = _main.CurrentFile?.ExifData?.SonyFocusPosition;
        if (pos == null || pos.Value.ImageWidth <= 0 || pos.Value.ImageHeight <= 0) return false;
        var focusUV = new Vector(
            pos.Value.FocusX / (double)pos.Value.ImageWidth - 0.5,
            pos.Value.FocusY / (double)pos.Value.ImageHeight - 0.5);
        var currentUV = GetCenterUV();
        const double tolerance = 0.05;
        return Math.Abs(currentUV.X - focusUV.X) < tolerance &&
               Math.Abs(currentUV.Y - focusUV.Y) < tolerance;
    }

    /// <summary>
    /// 获取对焦点在指定图像尺寸下的像素坐标。
    /// 从当前文件的 ExifData.SonyFocusPosition 读取，映射到 imageSize。
    /// </summary>
    private Vector? GetFocusImagePoint(Vector imageSize)
    {
        var pos = _main.CurrentFile?.ExifData?.SonyFocusPosition;
        if (pos == null || imageSize.X <= 0 || imageSize.Y <= 0) return null;
        var p = pos.Value;
        if (p.ImageWidth <= 0 || p.ImageHeight <= 0) return null;
        return new Vector(
            p.FocusX / (double)p.ImageWidth * imageSize.X,
            p.FocusY / (double)p.ImageHeight * imageSize.Y);
    }

    /// <summary>
    /// 获取屏幕中间点在图片上的相对位置（左上角 -0.5,-0.5，右下角 0.5,0.5）
    /// </summary>
    /// <returns></returns>
    private Vector GetCenterUV()
    {
        var imageCenterPoint = ImageSize * 0.5 + Translate;
        var viewCenterOffset = ViewSize * 0.5 - imageCenterPoint;
        return Vector.Divide(viewCenterOffset, Scale * ImageSize);
    }
    
    private void SetUVToCenter(Vector uv)
    {
        var viewCenterOffset = Vector.Multiply(uv, ImageSize * Scale);
        var imageCenterPoint = ViewSize * 0.5 - viewCenterOffset;
        Translate = imageCenterPoint - 0.5 * ImageSize;
    }

    private void ClampCenter()
    {
        var clamp1 = ViewSize * 0.5 - ImageSize * 0.5;
        var clamp2 = ImageSize * Scale * 0.5;
        Translate = Vector.Clamp(Translate, clamp1 - clamp2, clamp1 + clamp2);
    }

    public void SetDetailHighlight(Rect? imageRect)
    {
        _detailHighlightImageRect = imageRect;
        UpdateDetailHighlightRect();
    }

    private void UpdateDetailHighlightRect()
    {
        if (_detailHighlightImageRect == null)
        {
            IsDetailHighlightVisible = false;
            return;
        }

        if (Scale <= 0 || ImageSize.X <= 0 || ImageSize.Y <= 0)
        {
            IsDetailHighlightVisible = false;
            return;
        }

        var rect = _detailHighlightImageRect.Value;
        
        var viewRect = new Rect(
            (rect.X - ImageSize.X * 0.5 ) * Scale + ImageSize.X * 0.5 + Translate.X,
            (rect.Y - ImageSize.Y * 0.5 ) * Scale + ImageSize.Y * 0.5 + Translate.Y,
            rect.Width * Scale,
            rect.Height * Scale);

        DetailHighlightRect = viewRect;
        IsDetailHighlightVisible = true;
    }

    public void FocusOnImagePoint(Vector imagePoint, double scale)
    {
        if (ViewSize.X <= 0 || ViewSize.Y <= 0) return;
        Scale = scale;
        // 渲染坐标系：视图位置 = (px - W/2)*Scale + W/2 + Translate
        // 令 imagePoint 出现在视图中心，逆推 Translate：
        // Translate = ViewSize/2 - ImageSize/2*(1-scale) - imagePoint*scale
        Translate = ViewSize * 0.5 - ImageSize * 0.5 * (1 - scale) - imagePoint * scale;
        ClampCenter();
        Fit = false;
    }
}