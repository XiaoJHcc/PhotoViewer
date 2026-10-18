using System.ComponentModel;
using AppKit;
using CoreGraphics;
using Foundation;
using PhotoViewer.Apple;
using PhotoViewer.Core.AI;
using PhotoViewer.Core.Image;
using PhotoViewer.ViewModels.Main;
using PhotoViewer.ViewModels.Settings;

namespace PhotoViewer.Mac.Native;

/// <summary>
/// 原生分析栏：常驻侧栏，按共享 <see cref="AnalysisPresenter"/> 的瓦片顺序渲染
/// 细节预览（中心 / 对焦点）、直方图与 4 张 DINO/CV 诊断图。
/// 几何与旧壳 <c>AnalysisView</c> 同口径：正方形瓦片边长 = <c>floor((可用主轴长度 - 20 - 8×(n-1)) / n)</c>，
/// 行布局排在主图下方、列布局排在主图右侧，交叉轴尺寸 = 瓦片边长 + 20。
/// 背景 <c>#111</c>，朝主图一侧 1 点 <c>#333</c> 分隔线；空白处点击清空准星。
/// </summary>
internal sealed class MacAnalysisPanel : NSView
{
    /// <summary>瓦片几何（内边距、间距、边长与交叉轴）由共享 <see cref="AnalysisPanelGeometry"/> 提供。</summary>
    private readonly MainViewModel _main;
    private readonly PhotoLoadSession _session;
    private readonly MacAnalysisHost _host;
    private readonly AnalysisPresenter _presenter;
    private readonly NSView _divider = new();
    private readonly Dictionary<AnalysisTile, NSView> _views = [];
    private readonly List<AnalysisTile> _order = [];
    private readonly Dictionary<AnalysisTile, DecodedImage?> _imageKeys = [];
    private readonly Dictionary<AnalysisTile, NSImage?> _images = [];
    private readonly Dictionary<AnalysisTile, (DecodedImage? Source, double U, double V, int Pixels)> _cropKeys = [];
    private bool _disposed;
    private double _tileSize;

    /// <summary>细节预览高亮变化：是否高亮、归一化中心、对焦框归一化宽高（无对焦框为 null）。</summary>
    public Action<bool, double, double, double?, double?>? DetailHighlight { get; set; }

    /// <summary>细节预览双击：请求主图定位到该归一化点并放大。</summary>
    public Action<double, double>? DetailFocusRequested { get; set; }

    /// <summary>顶边为原点的布局坐标。</summary>
    public override bool IsFlipped => true;

    /// <summary>当前瓦片数（决定边长分配的 n）。</summary>
    public int TileCount => _order.Count;

    /// <summary>细节预览的裁剪窗口边长（源像素），与旧壳 <c>CropSize</c> 同口径。</summary>
    public int CropPixels => Math.Max(1, (int)Math.Round(_tileSize));

    /// <summary>共享分析状态机，供验收读取瓦片数据与准星。</summary>
    internal AnalysisPresenter Presenter => _presenter;

    /// <summary>按 presenter 顺序排列的原生瓦片视图，供几何验收读取。</summary>
    internal IReadOnlyList<NSView> TileViews => _order.Select(tile => _views[tile]).ToArray();

    /// <summary>构造：建立宿主、presenter 与全部状态订阅。</summary>
    /// <param name="main">共享主 VM。</param>
    /// <param name="session">原生展示加载会话。</param>
    public MacAnalysisPanel(MainViewModel main, PhotoLoadSession session)
    {
        _main = main;
        _session = session;
        WantsLayer = true;
        Layer!.BackgroundColor = NSColor.FromRgb(0x11, 0x11, 0x11).CGColor;
        _divider.WantsLayer = true;
        _divider.Layer!.BackgroundColor = NSColor.FromRgb(0x33, 0x33, 0x33).CGColor;
        AddSubview(_divider);

        _host = new MacAnalysisHost(main, session);
        _presenter = new AnalysisPresenter(_host);
        _presenter.TilesChanged += Sync;
        _presenter.CrosshairChanged += Sync;

        _main.PropertyChanged += OnMainChanged;
        _main.Settings.PropertyChanged += OnSettingsChanged;
        _session.Changed += OnSessionChanged;
        Sync();
    }

    /// <summary>交叉轴尺寸（瓦片边长 + 两侧 10 点内边距）。</summary>
    /// <param name="availableMain">主轴可用长度。</param>
    /// <returns>需为面板分配的交叉轴长度。</returns>
    public double CrossSize(double availableMain) => AnalysisPanelGeometry.CrossSize(availableMain, _order.Count);

    /// <summary>按交叉轴可用长度摆放瓦片，并刷新朝主图一侧的分隔线。</summary>
    /// <param name="horizontal">行布局（瓦片横排）为 true。</param>
    /// <param name="availableMain">主轴可用长度。</param>
    public void Arrange(bool horizontal, double availableMain)
    {
        double tile = AnalysisPanelGeometry.TileSize(availableMain, _order.Count);
        _tileSize = tile;
        // 与旧壳一致：瓦片从主轴内边距处起排（ItemsControl 内 StackPanel 左/上对齐），剩余不足一枚的零头留在末端。
        for (int i = 0; i < _order.Count; i++)
        {
            double offset = AnalysisPanelGeometry.TileOffset(i, tile);
            _views[_order[i]].Frame = horizontal
                ? new CGRect(offset, AnalysisPanelGeometry.Padding, tile, tile)
                : new CGRect(AnalysisPanelGeometry.Padding, offset, tile, tile);
        }
        _divider.Frame = horizontal ? new CGRect(0, 0, Bounds.Width, 1) : new CGRect(0, 0, 1, Bounds.Height);
        foreach (var detail in _order)
            if (detail.Kind == AnalysisTileKind.Detail) UpdateDetailCrop(detail);
    }

    /// <summary>空白处点击：清空诊断瓦片准星。</summary>
    public override void MouseDown(NSEvent theEvent) => _presenter.ClearCrosshair();

    /// <summary>把 presenter 的瓦片状态同步到原生视图（位图按引用比较，未变化不重编码）。</summary>
    private void Sync()
    {
        if (_disposed) return;
        var tiles = _presenter.Tiles;
        if (!_order.SequenceEqual(tiles)) RebuildViews(tiles);

        foreach (var tile in tiles)
        {
            if (tile.Kind == AnalysisTileKind.Detail)
            {
                var preview = (MacDetailPreview)_views[tile];
                preview.CornerLabel = tile.ShortLabel;
                preview.SetCenter(tile.CenterU, tile.CenterV, tile.FocusFrameWidth, tile.FocusFrameHeight);
                UpdateDetailCrop(tile);
                continue;
            }

            bool histogram = tile.Kind == AnalysisTileKind.Histogram;
            var view = (MacDiagnosticTile)_views[tile];
            view.AspectRatio = histogram ? 1.0 : _presenter.AspectRatio;
            view.CornerLabel = tile.ShortLabel;
            view.Label = tile.Caption;
            view.PlaceholderText = tile.Placeholder;
            view.Source = ImageFor(tile);
            view.SetCrosshair(histogram ? null : _presenter.CrosshairU, histogram ? null : _presenter.CrosshairV);
            if (ReferenceEquals(tile, _presenter.ShakeTile) && view.Overlay is MacShakeFieldView shake)
                shake.ShakeField = tile.ShakeField;
        }
    }

    /// <summary>瓦片集合顺序或数量变化（对焦点出现/消失）时重建原生视图。</summary>
    private void RebuildViews(IReadOnlyList<AnalysisTile> tiles)
    {
        foreach (var view in _views.Values)
        {
            view.RemoveFromSuperview();
            view.Dispose();
        }
        _views.Clear();
        _order.Clear();
        _imageKeys.Clear();
        _images.Clear();
        _cropKeys.Clear();

        foreach (var tile in tiles)
        {
            NSView view;
            if (tile.Kind == AnalysisTileKind.Detail)
            {
                var preview = new MacDetailPreview();
                preview.HighlightChanged = (on, u, v, frameWidth, frameHeight) => DetailHighlight?.Invoke(on, u, v, frameWidth, frameHeight);
                preview.FocusRequested = (u, v) => DetailFocusRequested?.Invoke(u, v);
                view = preview;
            }
            else
            {
                var diagnostic = new MacDiagnosticTile();
                if (tile.Kind == AnalysisTileKind.Diagnostic)
                {
                    diagnostic.TileClicked = (u, v) => _presenter.OnTileClicked(u, v);
                    // ANALYSIS-08：瓦片内 letterbox/角标点击等同面板空白，只清准星并保留 cosine 参考点。
                    diagnostic.BlankClicked = _presenter.ClearCrosshair;
                }
                if (ReferenceEquals(tile, _presenter.ShakeTile))
                    diagnostic.Overlay = new MacShakeFieldView();
                view = diagnostic;
            }
            AddSubview(view);
            _views[tile] = view;
            _order.Add(tile);
        }
        Arrange(_main.IsRowLayout, _main.IsRowLayout ? (double)Bounds.Width : (double)Bounds.Height);
    }

    /// <summary>按瓦片位图引用取（并按需创建）原生位图；同一 <see cref="DecodedImage"/> 实例只编码一次。</summary>
    /// <param name="tile">目标瓦片。</param>
    /// <returns>由本面板缓存持有的位图。</returns>
    private NSImage? ImageFor(AnalysisTile tile)
    {
        var key = tile.Source;
        if (_imageKeys.TryGetValue(tile, out var cached) && ReferenceEquals(cached, key))
            return _images[tile];
        var image = key == null ? null : MacSymbols.CreateThumbnail(key);
        _imageKeys[tile] = key;
        _images[tile] = image;
        return image;
    }

    /// <summary>细节预览的裁剪内容：从展示会话的全分辨率图按显示朝向裁 1:1 方块；代理期留空。</summary>
    private void UpdateDetailCrop(AnalysisTile tile)
    {
        var preview = (MacDetailPreview)_views[tile];
        var source = _session.IsProxy ? null : _session.Image;
        int pixels = Math.Max(1, (int)Math.Round(_tileSize));
        var key = (source, tile.CenterU, tile.CenterV, pixels);
        if (_cropKeys.TryGetValue(tile, out var previous) && previous == key) return;
        _cropKeys[tile] = key;

        if (source == null || !source.TryAcquire())
        {
            preview.SetContent(null);
            return;
        }
        DecodedImage? crop = null;
        try { crop = DisplayCrop.CropSquare(source, source.Orientation, tile.CenterU, tile.CenterV, pixels); }
        finally { source.Release(); }

        if (crop == null)
        {
            preview.SetContent(null);
            return;
        }
        try { preview.SetContent(MacSymbols.CreateThumbnail(crop)); }
        finally { crop.Dispose(); }
    }

    /// <summary>主 VM 状态变化：可见性 / 布局 / 当前图都会重新路由。</summary>
    private void OnMainChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName is nameof(MainViewModel.CurrentFile) or nameof(MainViewModel.IsRowLayout)
            or nameof(MainViewModel.IsAnalysisViewVisible))
            _host.Raise();
    }

    /// <summary>「即时计算DINO/CV」开关变化后重新路由。</summary>
    private void OnSettingsChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(SettingsViewModel.AnalysisImmediateCompute)) _host.Raise();
    }

    /// <summary>展示图（代理 ↔ Full、增强切换）变化后刷新细节裁剪并重新路由。</summary>
    private void OnSessionChanged()
    {
        if (_disposed) return;
        foreach (var tile in _order)
            if (tile.Kind == AnalysisTileKind.Detail) UpdateDetailCrop(tile);
        _host.Raise();
    }

    /// <summary>注销订阅并释放 presenter 持有的位图。</summary>
    /// <param name="disposing">是否由显式释放触发。</param>
    protected override void Dispose(bool disposing)
    {
        if (disposing && !_disposed)
        {
            _disposed = true;
            _main.PropertyChanged -= OnMainChanged;
            _main.Settings.PropertyChanged -= OnSettingsChanged;
            _session.Changed -= OnSessionChanged;
            _presenter.TilesChanged -= Sync;
            _presenter.CrosshairChanged -= Sync;
            _presenter.Dispose();
        }
        base.Dispose(disposing);
    }
}
