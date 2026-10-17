using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Shapes;
using PhotoViewer.Core.AI;
using PhotoViewer.Core.Color;
using PhotoViewer.Core.Image;
using PhotoViewer.ViewModels.Main;
using PhotoViewer.ViewModels.Settings;
using SkiaSharp;
using Windows.Foundation;
using Windows.UI;

namespace PhotoViewer.Desktop.Native;

/// <summary>
/// 原生分析栏:细节预览(中心 / 对焦点)+ 直方图 + DINO PCA + Cosine + 锐度 + 抖动矢量场。
///
/// 数据编排全部交给平台中立的 <see cref="AnalysisPresenter"/>(与旧壳、macOS/iPad 原生分析栏同一份):
/// 只读库快路径、增强联动实时重算、未提取回退、租约、直方图与 cosine 参考点都在 presenter;
/// 本类只做 Windows 侧的瓦片渲染、布局、准星与 hover 联动。细节预览用共享 <see cref="DisplayCrop"/>
/// 从容器朝向的展示帧裁出显示朝向的 1:1 方块,不再自己复制方向表。
/// </summary>
internal sealed class WindowsAnalysisPanel : Grid, IAnalysisHost
{
    private readonly MainViewModel _main;
    private readonly PhotoLoadSession _session;
    private readonly Action<Rect?> _setHighlight;
    private readonly Border _frame;
    private readonly WindowsAnalysisTilePanel _tiles;
    private readonly AnalysisPresenter _presenter;

    private readonly AnalysisPreviewTile _focusTile;
    private readonly AnalysisPreviewTile _centerTile;
    private readonly AnalysisDiagnosticTile _histogramTile;
    private readonly AnalysisDiagnosticTile _pcaTile;
    private readonly AnalysisDiagnosticTile _cosineTile;
    private readonly AnalysisDiagnosticTile _sharpnessTile;
    private readonly AnalysisDiagnosticTile _shakeTile;

    private DecodedImage? _lastHistogramSource;
    private DecodedImage? _lastPcaSource;
    private DecodedImage? _lastCosineSource;
    private DecodedImage? _lastSharpnessSource;
    private ShakeField? _lastShakeField;
    private double _lastDiagnosticAspect = double.NaN;
    private double _lastShakeAspect = double.NaN;

    /// <summary>
    /// 创建分析栏。宿主状态(可见性/当前图/展示帧/即时计算)由本类直接上报给共享 presenter。
    /// </summary>
    /// <param name="main">主视图模型。</param>
    /// <param name="session">主图加载会话(展示帧与增强状态)。</param>
    /// <param name="focusImagePoint">双击细节预览时把主图对准该显示朝向图像点(uv)。</param>
    /// <param name="setHighlight">细节预览 hover 联动主图绿框(显示朝向 uv;null 清除)。</param>
    public WindowsAnalysisPanel(MainViewModel main, PhotoLoadSession session,
        Action<double, double> focusImagePoint, Action<Rect?> setHighlight)
    {
        _main = main;
        _session = session;
        _setHighlight = setHighlight;

        _focusTile = new AnalysisPreviewTile("对焦点", focusImagePoint, Highlight);
        _centerTile = new AnalysisPreviewTile("中心", focusImagePoint, Highlight);
        _histogramTile = new AnalysisDiagnosticTile("直方图", null, null);
        _pcaTile = new AnalysisDiagnosticTile("DINO PCA", OnTileClicked, ClearCrosshair);
        _cosineTile = new AnalysisDiagnosticTile("Cosine 16,16", OnTileClicked, ClearCrosshair);
        _sharpnessTile = new AnalysisDiagnosticTile("锐度", OnTileClicked, ClearCrosshair);
        _shakeTile = new AnalysisDiagnosticTile("抖动拖影", OnTileClicked, ClearCrosshair);

        _tiles = new WindowsAnalysisTilePanel
        {
            Orientation = Orientation.Horizontal,
            ItemSpacing = WindowsLayout.AnalysisSpacing,
        };
        // 边长由布局系统给出;变化后只重建细节预览裁剪图,绝不在布局回调里碰位图。
        _tiles.SideChanged = _ => UpdatePreviewTiles();

        _frame = new Border
        {
            Background = new SolidColorBrush(WindowsLayout.BarBackground),
            BorderBrush = new SolidColorBrush(WindowsLayout.Divider),
            BorderThickness = new Thickness(0, WindowsLayout.DividerThickness, 0, 0),
            Padding = new Thickness(WindowsLayout.AnalysisPadding),
            Child = _tiles,
        };
        Children.Add(_frame);

        _presenter = new AnalysisPresenter(this);
        _presenter.TilesChanged += SyncTiles;
        _presenter.CrosshairChanged += SyncCrosshair;

        _main.PropertyChanged += OnMainChanged;
        _main.Settings.PropertyChanged += OnSettingsChanged;
        _session.Changed += OnSessionChanged;

        SetOrientation(_main.IsRowLayout);
        SyncTiles();
        UpdatePreviewTiles();
        _presenter.Refresh();
    }

    /// <inheritdoc/>
    public bool AnalysisVisible => _main.IsAnalysisViewVisible;

    /// <inheritdoc/>
    public bool ImmediateCompute => _main.Settings.AnalysisImmediateCompute;

    /// <inheritdoc/>
    public ImageFile? CurrentFile => _main.CurrentFile;

    /// <inheritdoc/>
    public DecodedImage? DisplayImage => _session.Image;

    /// <inheritdoc/>
    public bool EnhancedDisplay => _session.EnhancedDisplay;

    /// <inheritdoc/>
    public bool ThumbnailsBusy => _main.FileVM.ThumbnailList.IsThumbnailLoadingBusy();

    /// <inheritdoc/>
    public event Action? HostChanged;

    /// <summary>验收专用:细节预览是否已贴上裁剪图。</summary>
    internal bool HasPreviewImage => _centerTile.HasImage;

    /// <summary>验收专用:直方图瓦片是否已出图。</summary>
    internal bool HasHistogram => _histogramTile.HasBitmap;

    /// <summary>验收专用:四张诊断瓦片是否都已出图(即时计算完成)。</summary>
    internal bool HasDiagnostics => _pcaTile.HasBitmap && _cosineTile.HasBitmap && _sharpnessTile.HasBitmap && _shakeTile.HasBitmap;

    /// <summary>验收专用:诊断瓦片是否仍处于占位状态。</summary>
    internal bool DiagnosticsPlaceholderVisible => _pcaTile.PlaceholderVisible || _sharpnessTile.PlaceholderVisible;

    /// <summary>验收专用:current cosine 角标文本。</summary>
    internal string CosineLabel => _cosineTile.LabelText;

    /// <summary>验收专用:抖动瓦片角标(判定文字)。</summary>
    internal string ShakeLabel => _shakeTile.LabelText;

    /// <summary>验收专用:瓦片边长(逻辑单位,由布局系统给出)。</summary>
    internal double TileSide => _tiles.Side;

    /// <summary>验收专用:全部瓦片是否等边长,且与布局算出的边长一致(防"挤成一坨/大小错位"回归)。</summary>
    internal bool TilesUniformlyArranged
    {
        get
        {
            double side = _tiles.Side;
            if (side <= 0 || _tiles.Children.Count == 0)
            {
                return false;
            }

            foreach (var child in _tiles.Children)
            {
                if (child is not FrameworkElement element)
                {
                    return false;
                }

                if (Math.Abs(element.ActualWidth - side) > 0.5 || Math.Abs(element.ActualHeight - side) > 0.5)
                {
                    return false;
                }
            }

            return true;
        }
    }

    /// <summary>验收专用:模拟诊断瓦片点击(与真实点击同一路径)。</summary>
    /// <param name="u">横向 0-1。</param>
    /// <param name="v">纵向 0-1。</param>
    internal void TriggerTileClickForAcceptance(double u, double v) => _presenter.OnTileClicked(u, v);

    /// <summary>切换上下/左右布局:上下布局瓦片横排(顶部分隔线),左右布局竖排(左侧分隔线)。</summary>
    /// <param name="isRowLayout">是否为上下布局。</param>
    public void SetOrientation(bool isRowLayout)
    {
        _tiles.Orientation = isRowLayout ? Orientation.Horizontal : Orientation.Vertical;
        _frame.BorderThickness = isRowLayout
            ? new Thickness(0, WindowsLayout.DividerThickness, 0, 0)
            : new Thickness(WindowsLayout.DividerThickness, 0, 0, 0);
    }

    /// <summary>释放 presenter、订阅与自持位图。</summary>
    public void Detach()
    {
        _presenter.TilesChanged -= SyncTiles;
        _presenter.CrosshairChanged -= SyncCrosshair;
        _presenter.Dispose();
        _tiles.SideChanged = null;

        _main.PropertyChanged -= OnMainChanged;
        _main.Settings.PropertyChanged -= OnSettingsChanged;
        _session.Changed -= OnSessionChanged;

        _focusTile.Dispose();
        _centerTile.Dispose();
        _pcaTile.Dispose();
        _cosineTile.Dispose();
        _sharpnessTile.Dispose();
        _shakeTile.Dispose();
        _histogramTile.Dispose();
        _setHighlight(null);
    }

    /// <summary>主视图模型变化:切图/显隐交 presenter 重路由,换向只重排瓦片。</summary>
    private void OnMainChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(MainViewModel.IsRowLayout):
                SetOrientation(_main.IsRowLayout);
                return;
            case nameof(MainViewModel.CurrentFile):
            case nameof(MainViewModel.IsAnalysisViewVisible):
                UpdatePreviewTiles();
                HostChanged?.Invoke();
                return;
        }
    }

    /// <summary>设置变化:即时计算开关切回 DB 只读路径或实时重算。</summary>
    private void OnSettingsChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SettingsViewModel.AnalysisImmediateCompute))
        {
            HostChanged?.Invoke();
        }
    }

    /// <summary>会话帧变化:细节预览与直方图跟随(换图、代理→Full、增强);诊断由 presenter 重路由。</summary>
    private void OnSessionChanged()
    {
        UpdatePreviewTiles();
        HostChanged?.Invoke();
    }

    /// <summary>把 presenter 的瓦片状态同步到 Windows 控件,并在对焦点瓦片增删时重排容器。</summary>
    private void SyncTiles()
    {
        var views = _presenter.Tiles.Select(ViewFor).OfType<UIElement>().ToList();
        bool orderChanged = _tiles.Children.Count != views.Count
            || !views.SequenceEqual(_tiles.Children.Cast<UIElement>());
        if (orderChanged)
        {
            _tiles.Children.Clear();
            foreach (var view in views)
            {
                _tiles.Children.Add(view);
            }
        }

        bool previewChanged = ApplyDetail(_centerTile, _presenter.CenterTile);
        if (_presenter.Tiles.Contains(_presenter.FocusTile))
        {
            previewChanged |= ApplyDetail(_focusTile, _presenter.FocusTile);
        }
        else
        {
            _focusTile.Clear();
        }

        var aspect = _presenter.AspectRatio;
        _histogramTile.SetLabel(_presenter.HistogramTile.ShortLabel);
        if (!ReferenceEquals(_presenter.HistogramTile.Source, _lastHistogramSource))
        {
            _lastHistogramSource = _presenter.HistogramTile.Source;
            _histogramTile.SetBitmap(_presenter.HistogramTile.Source == null
                ? null
                : WindowsImageAdapter.ToSource(_presenter.HistogramTile.Source), _presenter.HistogramTile.Placeholder);
        }
        else
        {
            _histogramTile.SetPlaceholder(_presenter.HistogramTile.Placeholder);
        }

        SyncDiagnostic(_pcaTile, _presenter.PcaTile, ref _lastPcaSource, aspect);
        SyncDiagnostic(_cosineTile, _presenter.CosineTile, ref _lastCosineSource, aspect);
        SyncDiagnostic(_sharpnessTile, _presenter.SharpnessTile, ref _lastSharpnessSource, aspect);
        _sharpnessTile.SetLabel(_presenter.SharpnessTile.ShortLabel);

        _shakeTile.SetAspect(aspect);
        _shakeTile.SetLabel(_presenter.ShakeTile.ShortLabel);
        if (!ReferenceEquals(_presenter.ShakeTile.ShakeField, _lastShakeField) || aspect != _lastShakeAspect)
        {
            _lastShakeField = _presenter.ShakeTile.ShakeField;
            _lastShakeAspect = aspect;
            _shakeTile.SetBitmap(_lastShakeField == null ? null : BuildShakeBitmap(_lastShakeField, aspect),
                _presenter.ShakeTile.Placeholder);
        }
        else
        {
            _shakeTile.SetPlaceholder(_presenter.ShakeTile.Placeholder);
        }

        SyncCrosshair();
        // presenter 的 ApplyEntry 会同步改 aspect/位图引用;细节中心/对焦点瓦片增删时才重裁,
        // 避免诊断刷新反复做 CPU 裁剪。
        if (previewChanged || orderChanged)
        {
            UpdatePreviewTiles();
        }
    }

    /// <summary>取 presenter 瓦片对应的 Windows 控件。</summary>
    private UIElement? ViewFor(AnalysisTile tile)
    {
        if (ReferenceEquals(tile, _presenter.CenterTile)) return _centerTile;
        if (ReferenceEquals(tile, _presenter.FocusTile)) return _focusTile;
        if (ReferenceEquals(tile, _presenter.HistogramTile)) return _histogramTile;
        if (ReferenceEquals(tile, _presenter.PcaTile)) return _pcaTile;
        if (ReferenceEquals(tile, _presenter.CosineTile)) return _cosineTile;
        if (ReferenceEquals(tile, _presenter.SharpnessTile)) return _sharpnessTile;
        if (ReferenceEquals(tile, _presenter.ShakeTile)) return _shakeTile;
        return null;
    }

    /// <summary>把一个细节预览瓦片的中心/对焦框同步到控件;返回中心是否变化。</summary>
    private static bool ApplyDetail(AnalysisPreviewTile view, AnalysisTile tile)
    {
        Rect? frame = null;
        if (tile.FocusFrameWidth is { } width && tile.FocusFrameHeight is { } height)
        {
            frame = new Rect(
                Math.Clamp(tile.CenterU - (width / 2), 0, Math.Max(0, 1 - width)),
                Math.Clamp(tile.CenterV - (height / 2), 0, Math.Max(0, 1 - height)),
                width,
                height);
        }

        return view.SetCenter(tile.CenterU, tile.CenterV, frame);
    }

    /// <summary>同步一张诊断瓦片:aspect/标签变化时重建,位图引用未变时只更新占位文字。</summary>
    private void SyncDiagnostic(AnalysisDiagnosticTile view, AnalysisTile tile, ref DecodedImage? lastSource, double aspect)
    {
        if (aspect != _lastDiagnosticAspect)
        {
            _lastDiagnosticAspect = aspect;
            _lastPcaSource = null;
            _lastCosineSource = null;
            _lastSharpnessSource = null;
        }

        view.SetAspect(aspect);
        view.SetLabel(tile.ShortLabel);
        if (!ReferenceEquals(tile.Source, lastSource))
        {
            lastSource = tile.Source;
            view.SetBitmap(tile.Source == null ? null : ToSourceUpscaled(tile.Source, aspect), tile.Placeholder);
        }
        else
        {
            view.SetPlaceholder(tile.Placeholder);
        }
    }

    /// <summary>把 presenter 的准星同步到全部诊断瓦片。</summary>
    private void SyncCrosshair()
    {
        double? u = _presenter.CrosshairU;
        double? v = _presenter.CrosshairV;
        foreach (var tile in new[] { _histogramTile, _pcaTile, _cosineTile, _sharpnessTile, _shakeTile })
        {
            tile.SetCrosshair(u, v);
        }
    }

    /// <summary>刷新细节预览:显示朝向的中心/对焦点裁剪,按瓦片物理尺寸输出。</summary>
    private void UpdatePreviewTiles()
    {
        var image = _main.IsAnalysisViewVisible ? _session.Image : null;
        int cropPixels = (int)Math.Round(_tiles.Side);
        if (image == null || cropPixels <= 0)
        {
            _centerTile.Clear();
            _focusTile.Clear();
            return;
        }

        UpdatePreviewTile(_centerTile, _presenter.CenterTile, image, cropPixels);
        if (_presenter.Tiles.Contains(_presenter.FocusTile))
        {
            UpdatePreviewTile(_focusTile, _presenter.FocusTile, image, cropPixels);
        }
    }

    /// <summary>裁剪单张细节预览并转成 XAML 位图;裁剪源是容器朝向的展示帧。</summary>
    private void UpdatePreviewTile(AnalysisPreviewTile view, AnalysisTile tile, DecodedImage image, int cropPixels)
    {
        DecodedImage? crop = null;
        DecodedImage? scaled = null;
        try
        {
            crop = DisplayCrop.CropSquare(image, image.Orientation, tile.CenterU, tile.CenterV, cropPixels);
            if (crop == null)
            {
                view.Clear();
                return;
            }

            double raster = XamlRoot?.RasterizationScale ?? 1;
            int output = Math.Max(1, (int)Math.Round((view.ActualWidth > 0 ? view.ActualWidth : cropPixels) * raster));
            var display = crop;
            if (crop.Width != output || crop.Height != output)
            {
                scaled = SkiaImageCodec.ScaleToExact(crop, output, output);
                if (scaled == null)
                {
                    view.Clear();
                    return;
                }

                display = scaled;
            }

            double displayWidth = image.Orientation >= 5 ? image.Height : image.Width;
            double displayHeight = image.Orientation >= 5 ? image.Width : image.Height;
            view.SetBitmap(WindowsImageAdapter.ToSource(display), cropPixels, displayWidth, displayHeight);
        }
        finally
        {
            scaled?.Release();
            crop?.Release();
        }
    }

    /// <summary>诊断瓦片点击:落准星 + 以点击点为 cosine 参考点重算。</summary>
    private void OnTileClicked(double u, double v) => _presenter.OnTileClicked(u, v);

    /// <summary>空白处点击:清空准星。</summary>
    private void ClearCrosshair() => _presenter.ClearCrosshair();

    /// <summary>细节预览联动:悬停/离开时向主图推送绿框(显示朝向 uv)。</summary>
    private void Highlight(Rect? uv) => _setHighlight(_main.IsAnalysisViewVisible ? uv : null);

    /// <summary>把 8 位派生位图放大到最近邻可读尺寸后交 XAML(诊断网格只有 32×32)。</summary>
    /// <param name="image">派生位图(方形网格)。</param>
    /// <param name="aspect">主图纵横比(宽/高);诊断网格按它铺开,瓦片里才与原片比例一致。</param>
    /// <returns>XAML 位图;源为空时为 null。</returns>
    private static unsafe WriteableBitmap? ToSourceUpscaled(DecodedImage? image, double aspect)
    {
        if (image == null) return null;
        if (!image.TryAcquire()) return null;
        try
        {
            ResolveDiagnosticSize(aspect, WindowsLayout.AnalysisDiagnosticLongEdge, out int width, out int height);
            using var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Opaque, SKColorSpace.CreateSrgb()));
            var info = new SKImageInfo(image.Width, image.Height, SKColorType.Bgra8888, SKAlphaType.Opaque, SKColorSpace.CreateSrgb());
            fixed (byte* pixels = image.Pixels)
            {
                using var source = SKImage.FromPixels(info, (IntPtr)pixels, image.Stride)
                    ?? throw new InvalidOperationException("诊断位图无法包装为 Skia 图像");
                using var canvas = new SKCanvas(bitmap);
                canvas.DrawImage(source, SKRect.Create(0, 0, image.Width, image.Height), SKRect.Create(0, 0, width, height),
                    new SKSamplingOptions(SKFilterMode.Nearest), new SKPaint());
            }

            return WindowsImageAdapter.ToSource(width, height, bitmap.GetPixelSpan());
        }
        finally
        {
            image.Release();
        }
    }

    /// <summary>按主图纵横比求诊断位图尺寸:长边固定,另一向按比例收,方形网格铺回原片比例。</summary>
    /// <param name="aspect">主图纵横比(宽/高)。</param>
    /// <param name="longEdge">长边像素。</param>
    /// <param name="width">输出宽。</param>
    /// <param name="height">输出高。</param>
    private static void ResolveDiagnosticSize(double aspect, int longEdge, out int width, out int height)
    {
        double ratio = aspect > 0 ? aspect : 1;
        if (ratio >= 1)
        {
            width = longEdge;
            height = Math.Max(1, (int)Math.Round(longEdge / ratio));
        }
        else
        {
            width = Math.Max(1, (int)Math.Round(longEdge * ratio));
            height = longEdge;
        }
    }

    /// <summary>把抖动矢量场光栅化成箭头位图(颜色/方向/边宽口径同旧壳自绘)。</summary>
    /// <param name="field">抖动矢量场。</param>
    /// <param name="aspect">主图纵横比(宽/高);网格按它铺开,箭头不被压成正方。</param>
    /// <returns>XAML 位图。</returns>
    private static WriteableBitmap? BuildShakeBitmap(ShakeField field, double aspect)
    {
        int grid = PatchHeatmap.Grid;
        ResolveDiagnosticSize(aspect, grid * WindowsLayout.AnalysisShakeCellPixels, out int width, out int height);
        using var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Opaque, SKColorSpace.CreateSrgb()));
        float cellX = width / (float)grid;
        float cellY = height / (float)grid;
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(new SKColor(0x22, 0x22, 0x22));
            using var paint = new SKPaint { IsAntialias = true, StrokeCap = SKStrokeCap.Round };
            for (var index = 0; index < grid * grid && index < field.Direction.Length; index++)
            {
                if (index >= field.Mask.Length || !field.Mask[index]) continue;
                float direction = field.Direction[index];
                if (float.IsNaN(direction)) continue;
                float contrast = index < field.Contrast.Length ? CvHeatmap.ContrastFactor(field.Contrast[index]) : 1f;
                float consistency = index < field.LocalConsistency.Length && !float.IsNaN(field.LocalConsistency[index])
                    ? field.LocalConsistency[index] : 0f;
                var color = CvHeatmap.ColorForShake(field.Width[index] / Math.Max(1f, field.Diagonal), consistency, contrast);
                paint.Color = new SKColor(color.R, color.G, color.B);
                paint.StrokeWidth = Math.Max(1.5f, Math.Min(cellX, cellY) * 0.35f);
                int gx = index % grid, gy = index / grid;
                float cx = (gx + 0.5f) * cellX, cy = (gy + 0.5f) * cellY;
                float dx = MathF.Cos(direction) * cellX * 0.35f, dy = MathF.Sin(direction) * cellY * 0.35f;
                canvas.DrawLine(cx - dx, cy - dy, cx + dx, cy + dy, paint);
            }
        }

        var stride = width * 4;
        var bytes = new byte[stride * height];
        Marshal.Copy(bitmap.GetPixels(), bytes, 0, bytes.Length);
        using var decoded = new DecodedImage(bytes, width, height, stride, PixelEncoding.Bgra32, ImageColorSpace.Srgb);
        return WindowsImageAdapter.ToSource(decoded);
    }

    /// <summary>
    /// 细节预览瓦片:源像素 1:1 方形裁剪、左上药丸标签、悬停联动主图绿框、双击把主图对准预览中心。
    /// </summary>
    private sealed class AnalysisPreviewTile : Grid
    {
        private readonly Image _image;
        private readonly Action<double, double> _focus;
        private readonly Action<Rect?> _highlight;
        private double _centerU = 0.5;
        private double _centerV = 0.5;
        private Rect? _frameRect;
        private int _cropPixels;
        private double _displayWidth;
        private double _displayHeight;
        private bool _isHovered;

        /// <summary>
        /// 创建细节预览瓦片。
        /// </summary>
        /// <param name="label">药丸标签文字。</param>
        /// <param name="focus">双击回调(对准该图像点)。</param>
        /// <param name="highlight">悬停联动回调(显示朝向 uv 矩形)。</param>
        public AnalysisPreviewTile(string label, Action<double, double> focus, Action<Rect?> highlight)
        {
            _focus = focus;
            _highlight = highlight;
            var labelText = new TextBlock
            {
                Text = label,
                FontSize = WindowsLayout.AnalysisLabelFontSize,
                Foreground = new SolidColorBrush(WindowsLayout.AnalysisLabelForeground),
            };
            var labelHost = new Border
            {
                Background = new SolidColorBrush(WindowsLayout.AnalysisLabelBackground),
                CornerRadius = new CornerRadius(5, 0, 5, 0),
                Padding = new Thickness(8, 4, 8, 4),
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top,
                Child = labelText,
                IsHitTestVisible = false,
            };
            _image = new Image
            {
                Stretch = Stretch.Fill,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch,
            };
            var clip = new Border
            {
                Background = new SolidColorBrush(WindowsLayout.AnalysisTileBackground),
                Child = _image,
            };
            var outer = new Border
            {
                Background = new SolidColorBrush(WindowsLayout.AnalysisTileBackground),
                BorderBrush = new SolidColorBrush(WindowsLayout.AnalysisTileBorder),
                BorderThickness = new Thickness(WindowsLayout.AnalysisTileBorderThickness),
                CornerRadius = new CornerRadius(WindowsLayout.AnalysisTileCornerRadius),
                Child = clip,
            };
            Children.Add(outer);
            Children.Add(labelHost);

            // 绿框只在指针悬停预览瓦片时出现:换图/换帧会重建裁剪图,但重建本身不得点亮绿框。
            PointerEntered += (_, _) =>
            {
                _isHovered = true;
                PushHighlight();
            };
            PointerExited += (_, _) =>
            {
                _isHovered = false;
                _highlight(null);
            };
            DoubleTapped += (_, e) =>
            {
                _focus(_centerU, _centerV);
                e.Handled = true;
            };
        }

        /// <summary>是否已贴上裁剪图。</summary>
        public bool HasImage => _image.Source != null;

        /// <summary>设置中心与可选的对焦框(显示朝向 uv);返回中心/框是否变化。</summary>
        /// <param name="u">中心 u。</param>
        /// <param name="v">中心 v。</param>
        /// <param name="frame">对焦框 uv 矩形;null 表示联动裁剪区。</param>
        public bool SetCenter(double u, double v, Rect? frame)
        {
            bool changed = Math.Abs(_centerU - u) > 1e-9 || Math.Abs(_centerV - v) > 1e-9 || _frameRect != frame;
            _centerU = u;
            _centerV = v;
            _frameRect = frame;
            PushHighlight();
            return changed;
        }

        /// <summary>设置已裁好的显示朝向裁剪图与几何(裁剪边长、展示帧显示宽高)。</summary>
        /// <param name="bitmap">XAML 位图;null 清空。</param>
        /// <param name="cropPixels">裁剪边长(源/代理像素)。</param>
        /// <param name="displayWidth">展示帧显示朝向宽(代理像素)。</param>
        /// <param name="displayHeight">展示帧显示朝向高(代理像素)。</param>
        public void SetBitmap(WriteableBitmap? bitmap, int cropPixels, double displayWidth, double displayHeight)
        {
            _image.Source = bitmap;
            _cropPixels = cropPixels;
            _displayWidth = displayWidth;
            _displayHeight = displayHeight;
            PushHighlight();
        }

        /// <summary>清空显示(无帧时保留 #222 底色,与旧壳同口径)。</summary>
        public void Clear()
        {
            _image.Source = null;
            _cropPixels = 0;
            _displayWidth = 0;
            _displayHeight = 0;
            if (_isHovered)
            {
                _highlight(null);
            }
        }

        /// <summary>释放自持位图引用。</summary>
        public void Dispose()
        {
            _image.Source = null;
        }

        /// <summary>丢掉悬停态并撤掉绿框(面板收起等拿不到 PointerExited 的场合用)。</summary>
        public void ResetHover()
        {
            if (!_isHovered)
            {
                return;
            }

            _isHovered = false;
            _highlight(null);
        }

        /// <summary>计算裁剪区/对焦框在主图上的 uv 矩形并推给主图(仅悬停时;重建裁剪图不点亮绿框)。</summary>
        private void PushHighlight()
        {
            if (!_isHovered)
            {
                return;
            }

            if (_image.Source == null || _displayWidth <= 0 || _displayHeight <= 0)
            {
                _highlight(null);
                return;
            }

            if (_frameRect is { } frame)
            {
                _highlight(frame);
                return;
            }

            double widthUv = Math.Min(1, _cropPixels / _displayWidth);
            double heightUv = Math.Min(1, _cropPixels / _displayHeight);
            var rect = new Rect(
                Math.Clamp(_centerU - (widthUv / 2), 0, Math.Max(0, 1 - widthUv)),
                Math.Clamp(_centerV - (heightUv / 2), 0, Math.Max(0, 1 - heightUv)),
                widthUv,
                heightUv);
            _highlight(rect);
        }
    }

    /// <summary>
    /// 诊断瓦片:方形外框 + 按纵横比 letterbox 的内容 + 占位文字 + 准星 + 药丸标签。
    /// </summary>
    private sealed class AnalysisDiagnosticTile : Grid
    {
        private readonly Grid _contentFrame;
        private readonly Image _image;
        private readonly TextBlock _placeholder;
        private readonly Canvas _crosshair;
        private readonly Line _crosshairHorizontal;
        private readonly Line _crosshairVertical;
        private readonly TextBlock _label;
        private readonly Action<double, double>? _clicked;
        private readonly Action? _backgroundClicked;
        private double _aspect = 1;
        private double? _crosshairU;
        private double? _crosshairV;

        /// <summary>
        /// 创建诊断瓦片。
        /// </summary>
        /// <param name="label">药丸标签文字。</param>
        /// <param name="clicked">内容区点击回调(uv);null 表示不响应。</param>
        /// <param name="backgroundClicked">瓦片空白处点击回调。</param>
        public AnalysisDiagnosticTile(string label, Action<double, double>? clicked, Action? backgroundClicked)
        {
            _clicked = clicked;
            _backgroundClicked = backgroundClicked;

            _image = new Image { Stretch = Stretch.Uniform };
            var imageHost = new Grid { IsHitTestVisible = false };
            imageHost.Children.Add(_image);
            _placeholder = new TextBlock
            {
                FontSize = WindowsLayout.AnalysisPlaceholderFontSize,
                Foreground = new SolidColorBrush(WindowsLayout.AnalysisPlaceholderForeground),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                TextAlignment = TextAlignment.Center,
                Visibility = Visibility.Collapsed,
            };
            imageHost.Children.Add(_placeholder);

            _crosshairHorizontal = new Line
            {
                Stroke = new SolidColorBrush(WindowsLayout.AnalysisCrosshairStroke),
                StrokeThickness = 1,
                Visibility = Visibility.Collapsed,
            };
            _crosshairVertical = new Line
            {
                Stroke = new SolidColorBrush(WindowsLayout.AnalysisCrosshairStroke),
                StrokeThickness = 1,
                Visibility = Visibility.Collapsed,
            };
            _crosshair = new Canvas { IsHitTestVisible = false };
            _crosshair.Children.Add(_crosshairHorizontal);
            _crosshair.Children.Add(_crosshairVertical);

            _contentFrame = new Grid();
            _contentFrame.Children.Add(imageHost);
            _contentFrame.Children.Add(_crosshair);

            _label = new TextBlock
            {
                Text = label,
                FontSize = WindowsLayout.AnalysisLabelFontSize,
                Foreground = new SolidColorBrush(WindowsLayout.AnalysisLabelForeground),
            };
            var labelHost = new Border
            {
                Background = new SolidColorBrush(WindowsLayout.AnalysisLabelBackground),
                CornerRadius = new CornerRadius(5, 0, 5, 0),
                Padding = new Thickness(8, 4, 8, 4),
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top,
                Child = _label,
                IsHitTestVisible = false,
            };

            var outer = new Border
            {
                Background = new SolidColorBrush(WindowsLayout.AnalysisTileBackground),
                BorderBrush = new SolidColorBrush(WindowsLayout.AnalysisTileBorder),
                BorderThickness = new Thickness(WindowsLayout.AnalysisTileBorderThickness),
                CornerRadius = new CornerRadius(WindowsLayout.AnalysisTileCornerRadius),
                Child = _contentFrame,
            };
            Children.Add(outer);
            Children.Add(labelHost);

            // 只重画准星(Shape 坐标不进布局),尺寸与位置仍交给布局系统与新面板。
            SizeChanged += (_, _) => RenderCrosshair();
            PointerPressed += OnPressed;
        }

        /// <summary>当前是否显示位图(供 cosine 早退判断)。</summary>
        public bool HasBitmap => _image.Source != null;

        /// <summary>占位文字是否可见。</summary>
        public bool PlaceholderVisible => _placeholder.Visibility == Visibility.Visible;

        /// <summary>药丸标签文本。</summary>
        public string LabelText => _label.Text;

        /// <summary>设置内容纵横比(宽/高),用于 letterbox(位图自身按 Uniform 留边,这里只影响准星与 uv 换算)。</summary>
        /// <param name="aspect">宽高比。</param>
        public void SetAspect(double aspect)
        {
            _aspect = aspect > 0 ? aspect : 1;
            RenderCrosshair();
        }

        /// <summary>设置位图与占位文字。</summary>
        /// <param name="bitmap">位图(可为 null)。</param>
        /// <param name="placeholder">占位文字。</param>
        public void SetBitmap(WriteableBitmap? bitmap, string? placeholder)
        {
            _image.Source = bitmap;
            SetPlaceholder(placeholder);
        }

        /// <summary>只更新占位文字(位图引用未变时用,避免重复放大/上传)。</summary>
        /// <param name="placeholder">占位文字。</param>
        public void SetPlaceholder(string? placeholder)
        {
            _placeholder.Text = placeholder ?? string.Empty;
            _placeholder.Visibility = _image.Source == null && !string.IsNullOrEmpty(placeholder)
                ? Visibility.Visible
                : Visibility.Collapsed;
        }

        /// <summary>设置药丸标签文字。</summary>
        /// <param name="label">标签。</param>
        public void SetLabel(string label) => _label.Text = label;

        /// <summary>设置准星位置(uv;null 清空)。</summary>
        /// <param name="u">横向 0-1。</param>
        /// <param name="v">纵向 0-1。</param>
        public void SetCrosshair(double? u, double? v)
        {
            _crosshairU = u;
            _crosshairV = v;
            RenderCrosshair();
        }

        /// <summary>释放位图引用。</summary>
        public void Dispose() => _image.Source = null;

        /// <summary>内容(位图)在内容区坐标系里的实际矩形:按纵横比 letterbox 居中。</summary>
        /// <returns>位图矩形;尺寸未就绪时为空矩形。</returns>
        private Rect ContentRect()
        {
            double innerWidth = _contentFrame.ActualWidth;
            double innerHeight = _contentFrame.ActualHeight;
            if (innerWidth <= 0 || innerHeight <= 0)
            {
                return default;
            }

            double ratio = _aspect > 0 ? _aspect : 1;
            double width = innerWidth;
            double height = innerWidth / ratio;
            if (height > innerHeight)
            {
                height = innerHeight;
                width = innerHeight * ratio;
            }

            return new Rect((innerWidth - width) / 2, (innerHeight - height) / 2, width, height);
        }

        /// <summary>按 uv 绘制准星臂(坐标换算到 letterbox 后的位图区)。</summary>
        private void RenderCrosshair()
        {
            var rect = ContentRect();
            if (_crosshairU is not { } u || _crosshairV is not { } v || rect.Width <= 0 || rect.Height <= 0)
            {
                _crosshairHorizontal.Visibility = Visibility.Collapsed;
                _crosshairVertical.Visibility = Visibility.Collapsed;
                return;
            }

            double cx = Math.Round(rect.X + (Math.Clamp(u, 0, 1) * rect.Width));
            double cy = Math.Round(rect.Y + (Math.Clamp(v, 0, 1) * rect.Height));
            double arm = WindowsLayout.AnalysisCrosshairArm;
            double left = rect.X, right = rect.X + rect.Width, top = rect.Y, bottom = rect.Y + rect.Height;
            _crosshairHorizontal.X1 = Math.Max(left, cx - arm);
            _crosshairHorizontal.X2 = Math.Min(right, cx + arm);
            _crosshairHorizontal.Y1 = cy;
            _crosshairHorizontal.Y2 = cy;
            _crosshairVertical.X1 = cx;
            _crosshairVertical.X2 = cx;
            _crosshairVertical.Y1 = Math.Max(top, cy - arm);
            _crosshairVertical.Y2 = Math.Min(bottom, cy + arm);
            _crosshairHorizontal.Visibility = Visibility.Visible;
            _crosshairVertical.Visibility = Visibility.Visible;
        }

        /// <summary>瓦片一律吞掉点击:位图区内换算 uv 回调,留边与空白处交给清空回调。</summary>
        private void OnPressed(object sender, PointerRoutedEventArgs e)
        {
            e.Handled = true;
            var rect = ContentRect();
            var point = e.GetCurrentPoint(_contentFrame).Position;
            bool inside = rect.Width > 0 && rect.Height > 0
                && point.X >= rect.X && point.X <= rect.X + rect.Width
                && point.Y >= rect.Y && point.Y <= rect.Y + rect.Height;
            if (inside && _clicked != null)
            {
                _clicked(Math.Clamp((point.X - rect.X) / rect.Width, 0, 1), Math.Clamp((point.Y - rect.Y) / rect.Height, 0, 1));
            }
            else
            {
                _backgroundClicked?.Invoke();
            }
        }
    }
}
