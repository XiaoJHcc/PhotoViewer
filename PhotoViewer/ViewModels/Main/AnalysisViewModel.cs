using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Reactive;
using Avalonia;
using Avalonia.Media.Imaging;
using PhotoViewer.Core.AI;
using PhotoViewer.Core.Image;
using PhotoViewer.Core.Platform;
using PhotoViewer.Platform;
using PhotoViewer.Views.Tools;
using ReactiveUI;

namespace PhotoViewer.ViewModels.Main;

/// <summary>
/// 分析栏 VM(旧壳)。编排逻辑全部下沉到平台中立的 <see cref="AnalysisPresenter"/>,本类只做两件事:
/// 1. 把宿主状态(可见性 / 布局 / 当前图 / 展示位图 / 即时计算)上报给 presenter 并在变化时触发重路由;
/// 2. 把 presenter 的中性产物(<see cref="DecodedImage"/> / 判定文字 / 准星)适配成 Avalonia 的
///    <see cref="AnalysisItem"/> 列表 — 位图经 <see cref="DecodedImageAdapter"/> 转换(同实例复用同一位图)。
/// macOS 原生分析栏直接消费同一个 presenter,两条展示层不复制任何数据来源与缓存策略。
/// </summary>
public sealed class AnalysisViewModel : ReactiveObject
{
    /// <summary>DINO patch 图像素边长(32);也用于 cosine 参考点坐标系。</summary>
    public const int PatchGridPixels = AnalysisPresenter.PatchGridPixels;

    private readonly MainViewModel _main;
    private readonly AvaloniaAnalysisHost _host;
    private readonly AnalysisPresenter _presenter;

    private readonly AnalysisDetailItem _focusItem;
    private readonly AnalysisDetailItem _centerItem;
    private readonly AnalysisHistogramItem _histogramItem;
    private readonly AnalysisDiagnosticItem _pcaItem;
    private readonly AnalysisDiagnosticItem _cosineItem;
    private readonly AnalysisDiagnosticItem _sharpnessItem;
    private readonly AnalysisDiagnosticItem _shakeItem;
    private readonly ShakeFieldView _shakeOverlay = new();

    /// <summary>是否处于行布局(影响 ItemsControl 的 StackPanel 朝向)。</summary>
    public bool IsRowLayout => _main.IsRowLayout;

    /// <summary>分析栏当前是否可见 — DetailPreview 的 IsActive 等都吃这个。</summary>
    public bool IsAnalysisViewVisible => _main.IsAnalysisViewVisible;

    /// <summary>主图引用,DetailPreview 联动主图绿框时使用。</summary>
    public ImageViewModel ImageVM => _main.ImageVM;

    /// <summary>主图位图,DetailPreview 项裁剪时使用。代理图显示期返回 null(裁剪必须基于全分辨率源,Full 热替换后恢复)。</summary>
    public Bitmap? SourceBitmap => _main.ImageVM.CurrentIsProxy ? null : _main.ImageVM.SourceBitmap;

    private double _previewSize = 300;
    /// <summary>瓦片显示尺寸(像素正方形外框宽度)。</summary>
    public double PreviewSize
    {
        get => _previewSize;
        set => this.RaiseAndSetIfChanged(ref _previewSize, value);
    }

    private int _cropSize = 300;
    /// <summary>DetailPreview 的裁剪窗口像素大小。</summary>
    public int CropSize
    {
        get => _cropSize;
        set => this.RaiseAndSetIfChanged(ref _cropSize, value);
    }

    /// <summary>诊断瓦片的图像长宽比(宽/高);从 cv_image_width/height 推。</summary>
    public double AspectRatio => _presenter.AspectRatio;

    /// <summary>诊断瓦片共享的归一化准星;null 时不显示。</summary>
    public Point? Crosshair => _presenter.CrosshairU is { } u && _presenter.CrosshairV is { } v ? new Point(u, v) : null;

    /// <summary>cosine 参考点 x(0..31)。</summary>
    public int RefGridX => _presenter.RefGridX;

    /// <summary>cosine 参考点 y(0..31)。</summary>
    public int RefGridY => _presenter.RefGridY;

    /// <summary>分析栏列表项;按需插入对焦点。</summary>
    public ObservableCollection<AnalysisItem> Items { get; }

    /// <summary>构造并订阅宿主状态,把 presenter 变化映射回 Avalonia 列表项。</summary>
    /// <param name="main">主 VM。</param>
    public AnalysisViewModel(MainViewModel main)
    {
        _main = main;

        _focusItem = new AnalysisDetailItem("对焦点", new Point(0.5, 0.5));
        _centerItem = new AnalysisDetailItem("中心", new Point(0.5, 0.5));
        _histogramItem = new AnalysisHistogramItem("直方图");
        _pcaItem = new AnalysisDiagnosticItem("DINO PCA");
        _cosineItem = new AnalysisDiagnosticItem($"Cosine {PatchGridPixels / 2},{PatchGridPixels / 2}");
        _sharpnessItem = new AnalysisDiagnosticItem("锐度");
        _shakeItem = new AnalysisDiagnosticItem("抖动拖影") { Overlay = _shakeOverlay };
        Items = [_centerItem, _histogramItem, _pcaItem, _cosineItem, _sharpnessItem, _shakeItem];

        _host = new AvaloniaAnalysisHost(main);
        _presenter = new AnalysisPresenter(_host);
        _presenter.TilesChanged += OnTilesChanged;
        _presenter.CrosshairChanged += () => this.RaisePropertyChanged(nameof(Crosshair));

        _main.WhenAnyValue(vm => vm.IsRowLayout)
            .Subscribe(Observer.Create<bool>(_ => this.RaisePropertyChanged(nameof(IsRowLayout))));

        _main.WhenAnyValue(vm => vm.IsAnalysisViewVisible)
            .Subscribe(Observer.Create<bool>(_ =>
            {
                this.RaisePropertyChanged(nameof(IsAnalysisViewVisible));
                RaiseHostChanged();
            }));

        _main.WhenAnyValue(vm => vm.UsesNativePresentation)
            .Subscribe(Observer.Create<bool>(_ => RaiseHostChanged()));

        // 主图位图变化(含原片↔增强切换):刷新 DetailPreview 绑定。
        _main.ImageVM.WhenAnyValue(vm => vm.SourceBitmap)
            .Subscribe(Observer.Create<Bitmap?>(_ =>
            {
                this.RaisePropertyChanged(nameof(SourceBitmap));
                RaiseHostChanged();
            }));

        // 代理↔Full 热替换:DetailPreview 裁剪源在 Full 就绪后恢复(代理期占位)。
        _main.ImageVM.WhenAnyValue(vm => vm.CurrentIsProxy)
            .Subscribe(Observer.Create<bool>(_ => this.RaisePropertyChanged(nameof(SourceBitmap))));

        // 增强开关 / 增强图就绪 → 重新路由诊断瓦片(增强时对增强图实时重算,关闭时回落 DB)。
        _main.ImageVM.WhenAnyValue(vm => vm.IsEnhanced)
            .Subscribe(Observer.Create<bool>(_ => RaiseHostChanged()));
        _main.ImageVM.WhenAnyValue(vm => vm.EnhancedBitmap)
            .Subscribe(Observer.Create<Bitmap?>(_ => RaiseHostChanged()));

        // 即时计算开关变化 → 重新路由(开:增强重算 / 未提取回退;关:只读库,未提取保持占位)。
        _main.Settings.WhenAnyValue(s => s.AnalysisImmediateCompute)
            .Subscribe(Observer.Create<bool>(_ => RaiseHostChanged()));

        RaiseHostChanged();
    }

    /// <summary>外部入口:换图时刷新整个分析栏。实际路由由 presenter 依据宿主状态决定。</summary>
    /// <param name="file">当前照片;与宿主状态不一致时以宿主为准。</param>
    public void SetSource(ImageFile? file) => _presenter.Refresh();

    /// <summary>
    /// 诊断瓦片点击:落准星 + 把点击位置作 cosine 参考点(映射到 32×32);上半 DetailPreview 的点击不会到这。
    /// </summary>
    /// <param name="u">归一化横坐标。</param>
    /// <param name="v">归一化纵坐标。</param>
    public void OnTileClicked(double u, double v) => _presenter.OnTileClicked(u, v);

    /// <summary>空白处点击:清空准星。</summary>
    public void ClearCrosshair() => _presenter.ClearCrosshair();

    /// <summary>宿主状态变化后重新路由;presenter 内部会做取消与 Background 优先级投递。</summary>
    private void RaiseHostChanged() => _host.Raise();

    /// <summary>presenter 瓦片变化后同步列表项内容与顺序(实例保持稳定,避免重建视觉树)。</summary>
    private void OnTilesChanged()
    {
        _histogramItem.Source = DecodedImageAdapter.ToBitmap(_presenter.HistogramTile.Source);

        _pcaItem.Source = DecodedImageAdapter.ToBitmap(_presenter.PcaTile.Source);
        _pcaItem.PlaceholderText = _presenter.PcaTile.Placeholder;

        _cosineItem.Source = DecodedImageAdapter.ToBitmap(_presenter.CosineTile.Source);
        _cosineItem.PlaceholderText = _presenter.CosineTile.Placeholder;
        _cosineItem.ShortLabel = _presenter.CosineTile.ShortLabel;

        _sharpnessItem.Source = DecodedImageAdapter.ToBitmap(_presenter.SharpnessTile.Source);
        _sharpnessItem.PlaceholderText = _presenter.SharpnessTile.Placeholder;

        _shakeOverlay.ShakeField = _presenter.ShakeTile.ShakeField;
        _shakeItem.ShortLabel = _presenter.ShakeTile.ShortLabel;
        _shakeItem.PlaceholderText = _presenter.ShakeTile.Placeholder;

        _centerItem.Center = new Point(_presenter.CenterTile.CenterU, _presenter.CenterTile.CenterV);
        _focusItem.Center = new Point(_presenter.FocusTile.CenterU, _presenter.FocusTile.CenterV);
        _focusItem.FocusFrame = _presenter.FocusTile.FocusFrameWidth is { } width
            ? new Size(width, _presenter.FocusTile.FocusFrameHeight ?? width)
            : null;

        var expected = _presenter.Tiles.Select(ItemFor).ToArray();
        if (Items.Count != expected.Length || !Items.SequenceEqual(expected))
        {
            Items.Clear();
            foreach (var item in expected) Items.Add(item);
        }

        this.RaisePropertyChanged(nameof(AspectRatio));
        this.RaisePropertyChanged(nameof(RefGridX));
        this.RaisePropertyChanged(nameof(RefGridY));
    }

    /// <summary>把 presenter 瓦片映射到固定的列表项实例。</summary>
    private AnalysisItem ItemFor(AnalysisTile tile)
    {
        if (ReferenceEquals(tile, _presenter.FocusTile)) return _focusItem;
        if (ReferenceEquals(tile, _presenter.PcaTile)) return _pcaItem;
        if (ReferenceEquals(tile, _presenter.CosineTile)) return _cosineItem;
        if (ReferenceEquals(tile, _presenter.SharpnessTile)) return _sharpnessItem;
        if (ReferenceEquals(tile, _presenter.ShakeTile)) return _shakeItem;
        if (ReferenceEquals(tile, _presenter.HistogramTile)) return _histogramItem;
        return _centerItem;
    }

    /// <summary>旧壳宿主状态:把主 VM 的可见性、布局、展示位图与设置上报给 presenter。</summary>
    private sealed class AvaloniaAnalysisHost : IAnalysisHost
    {
        private readonly MainViewModel _main;

        /// <summary>构造。</summary>
        /// <param name="main">主 VM。</param>
        public AvaloniaAnalysisHost(MainViewModel main) => _main = main;

        /// <summary>原生展示时旧壳分析栏不工作,避免两条展示层重复编排。</summary>
        public bool AnalysisVisible => !_main.UsesNativePresentation && _main.IsAnalysisViewVisible;

        public bool ImmediateCompute => _main.Settings.AnalysisImmediateCompute;

        public ImageFile? CurrentFile => _main.CurrentFile;

        public DecodedImage? DisplayImage => _main.ImageVM.CurrentDecodedImage;

        public bool EnhancedDisplay => _main.ImageVM.IsEnhanced && _main.ImageVM.EnhancedBitmap != null;

        public bool ThumbnailsBusy => _main.FileVM.ThumbnailList.IsThumbnailLoadingBusy();

        public event Action? HostChanged;

        /// <summary>上报宿主状态变化。</summary>
        public void Raise() => HostChanged?.Invoke();
    }
}
