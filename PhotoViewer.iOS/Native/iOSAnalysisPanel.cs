using System.ComponentModel;
using CoreGraphics;
using Foundation;
using PhotoViewer.Apple;
using PhotoViewer.Core.AI;
using PhotoViewer.Core.Image;
using PhotoViewer.ViewModels.Main;
using PhotoViewer.ViewModels.Settings;
using UIKit;

namespace PhotoViewer.iOS.Native;

/// <summary>
/// 原生分析栏（UIKit 版）：常驻侧栏，按共享 <see cref="AnalysisPresenter"/> 的瓦片顺序渲染
/// 细节预览（中心 / 对焦点）、直方图与 4 张 DINO/CV 诊断图。
/// 几何与旧壳 <c>AnalysisView</c>/Mac 原生分析栏同口径：正方形瓦片边长 =
/// <c>floor((可用主轴长度 − 20 − 8×(n−1)) / n)</c>，行布局排在主图下方、列布局排在主图右侧，
/// 交叉轴尺寸 = 瓦片边长 + 20。
/// 背景 <c>#111</c>，朝主图一侧 1 点 <c>#333</c> 分隔线；空白处点击清空准星。
/// 细节裁剪从展示会话取源图：F16 代理与 8 位 P3 Full 都经共享 <see cref="DisplayCrop"/> 转成显示朝向 sRGB8。
/// </summary>
internal sealed class iOSAnalysisPanel : UIView
{
    private readonly MainViewModel _main;
    private readonly PhotoLoadSession _session;
    private readonly iOSAnalysisHost _host;
    private readonly AnalysisPresenter _presenter;
    private readonly UIView _divider = new();
    private readonly UITapGestureRecognizer _blankTap;
    private readonly Dictionary<AnalysisTile, UIView> _views = [];
    private readonly List<AnalysisTile> _order = [];
    private readonly Dictionary<AnalysisTile, DecodedImage?> _imageKeys = [];
    private readonly Dictionary<AnalysisTile, UIImage?> _images = [];
    private readonly Dictionary<AnalysisTile, (DecodedImage? Source, double U, double V, int Pixels)> _cropKeys = [];
    private bool _disposed;
    private double _tileSize;
    private bool _horizontal = true;

    /// <summary>细节预览高亮变化：是否高亮、归一化中心、对焦框归一化宽高（无对焦框为 null）。</summary>
    internal Action<bool, double, double, double?, double?>? DetailHighlight { get; set; }

    /// <summary>细节预览双击/点按：请求主图定位到该归一化点并放大。</summary>
    internal Action<double, double>? DetailFocusRequested { get; set; }

    /// <summary>当前瓦片数（决定边长分配的 n）。</summary>
    internal int TileCount => _order.Count;

    /// <summary>细节预览的裁剪窗口边长（源像素），与旧壳 <c>CropSize</c> 同口径。</summary>
    internal int CropPixels => Math.Max(1, (int)Math.Round(_tileSize));

    /// <summary>共享分析状态机，供验收读取瓦片数据与准星。</summary>
    internal AnalysisPresenter Presenter => _presenter;

    /// <summary>按 presenter 顺序排列的原生瓦片视图，供几何验收读取。</summary>
    internal IReadOnlyList<UIView> TileViews => _order.Select(tile => _views[tile]).ToArray();

    /// <summary>细节预览瓦片（供验收核对定格/高亮与内容）。</summary>
    internal IReadOnlyList<iOSDetailPreview> DetailPreviews
        => _order.Where(tile => tile.Kind == AnalysisTileKind.Detail).Select(tile => (iOSDetailPreview)_views[tile]).ToArray();

    /// <summary>构造：建立宿主、presenter 与全部状态订阅。</summary>
    /// <param name="main">共享主 VM。</param>
    /// <param name="session">原生展示加载会话。</param>
    internal iOSAnalysisPanel(MainViewModel main, PhotoLoadSession session) : base(CGRect.Empty)
    {
        _main = main;
        _session = session;
        BackgroundColor = UIColor.FromRGB(0x11, 0x11, 0x11);
        _divider.BackgroundColor = UIColor.FromRGB(0x33, 0x33, 0x33);
        AddSubview(_divider);

        _host = new iOSAnalysisHost(main, session);
        _presenter = new AnalysisPresenter(_host);

        // 空白点击清空准星：瓦片与细节预览自身的点按优先（委托按命中视图排除）。
        _blankTap = new UITapGestureRecognizer(_ => _presenter.ClearCrosshair());
        _blankTap.Delegate = new BlankTapDelegate();
        AddGestureRecognizer(_blankTap);
        _presenter.TilesChanged += Sync;
        _presenter.CrosshairChanged += Sync;

        _main.PropertyChanged += OnMainChanged;
        _main.Settings.PropertyChanged += OnSettingsChanged;
        _session.Changed += OnSessionChanged;
        Sync();
    }

    /// <summary>交叉轴尺寸（瓦片边长 + 两侧 10 点内边距）。</summary>
    /// <param name="availableMain">主轴可用长度（不含安全区让位）。</param>
    /// <returns>需为面板分配的交叉轴长度。</returns>
    internal double CrossSize(double availableMain) => AnalysisPanelGeometry.CrossSize(availableMain, _order.Count);

    /// <summary>
    /// 按交叉轴可用长度摆放瓦片，并刷新朝主图一侧的分隔线。
    /// 行布局瓦片横排、分隔线在顶边；列布局瓦片纵排、分隔线在左边。
    /// </summary>
    /// <param name="horizontal">行布局（瓦片横排）为 true。</param>
    /// <param name="availableMain">主轴可用长度（已扣除安全区）。</param>
    /// <param name="startInset">主轴起点让位（行布局为安全区左内边距，列布局为安全区顶内边距）。</param>
    internal void Arrange(bool horizontal, double availableMain, double startInset)
    {
        _horizontal = horizontal;
        double tile = AnalysisPanelGeometry.TileSize(availableMain, _order.Count);
        _tileSize = tile;
        // 与旧壳一致：瓦片从主轴内边距处起排，剩余不足一枚的零头留在末端。
        for (int i = 0; i < _order.Count; i++)
        {
            double offset = startInset + AnalysisPanelGeometry.TileOffset(i, tile);
            _views[_order[i]].Frame = horizontal
                ? new CGRect(offset, AnalysisPanelGeometry.Padding, tile, tile)
                : new CGRect(AnalysisPanelGeometry.Padding, offset, tile, tile);
        }
        _divider.Frame = horizontal ? new CGRect(0, 0, Bounds.Width, 1) : new CGRect(0, 0, 1, Bounds.Height);
        foreach (var detail in _order)
            if (detail.Kind == AnalysisTileKind.Detail) UpdateDetailCrop(detail);
    }

    /// <summary>清除全部细节预览的绿框（分析栏隐藏或切图时由宿主调用）。</summary>
    internal void ClearDetailHighlight()
    {
        foreach (var detail in DetailPreviews) detail.ClearHighlight();
    }

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
                var preview = (iOSDetailPreview)_views[tile];
                preview.CornerLabel = tile.ShortLabel;
                preview.SetCenter(tile.CenterU, tile.CenterV, tile.FocusFrameWidth, tile.FocusFrameHeight);
                UpdateDetailCrop(tile);
                continue;
            }

            bool histogram = tile.Kind == AnalysisTileKind.Histogram;
            var view = (iOSDiagnosticTile)_views[tile];
            view.AspectRatio = histogram ? 1.0 : _presenter.AspectRatio;
            view.CornerLabel = tile.ShortLabel;
            view.Label = tile.Caption;
            view.PlaceholderText = tile.Placeholder;
            view.Source = ImageFor(tile);
            view.SetCrosshair(histogram ? null : _presenter.CrosshairU, histogram ? null : _presenter.CrosshairV);
            if (ReferenceEquals(tile, _presenter.ShakeTile) && view.Overlay is iOSShakeFieldView shake)
                shake.ShakeField = tile.ShakeField;
        }
    }

    /// <summary>瓦片集合顺序或数量变化（对焦点出现/消失）时重建原生视图。</summary>
    /// <param name="tiles">presenter 当前的瓦片顺序。</param>
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
            UIView view;
            if (tile.Kind == AnalysisTileKind.Detail)
            {
                var preview = new iOSDetailPreview();
                preview.HighlightChanged = (on, u, v, frameWidth, frameHeight) => DetailHighlight?.Invoke(on, u, v, frameWidth, frameHeight);
                preview.FocusRequested = (u, v) => DetailFocusRequested?.Invoke(u, v);
                view = preview;
            }
            else
            {
                var diagnostic = new iOSDiagnosticTile();
                if (tile.Kind == AnalysisTileKind.Diagnostic)
                    diagnostic.TileClicked = (u, v) => _presenter.OnTileClicked(u, v);
                if (ReferenceEquals(tile, _presenter.ShakeTile))
                    diagnostic.Overlay = new iOSShakeFieldView();
                view = diagnostic;
            }
            AddSubview(view);
            _views[tile] = view;
            _order.Add(tile);
        }
        // 尺寸可能还没分到；用当前面板尺寸先排一次，宿主布局会再次调用。
        Arrange(_horizontal, _horizontal ? (double)Bounds.Width : (double)Bounds.Height, 0);
    }

    /// <summary>按瓦片位图引用取（并按需创建）原生位图；同一 <see cref="DecodedImage"/> 实例只编码一次。</summary>
    /// <param name="tile">目标瓦片。</param>
    /// <returns>由本面板缓存持有的位图。</returns>
    private UIImage? ImageFor(AnalysisTile tile)
    {
        var key = tile.Source;
        if (_imageKeys.TryGetValue(tile, out var cached) && ReferenceEquals(cached, key))
            return _images[tile];
        var image = key == null ? null : iOSSymbols.CreateThumbnail(key);
        _imageKeys[tile] = key;
        _images[tile] = image;
        return image;
    }

    /// <summary>细节预览的裁剪内容：从展示会话的全分辨率图按显示朝向裁 1:1 方块；代理期留空。</summary>
    /// <param name="tile">细节预览瓦片。</param>
    private void UpdateDetailCrop(AnalysisTile tile)
    {
        var preview = (iOSDetailPreview)_views[tile];
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
        try { preview.SetContent(iOSSymbols.CreateThumbnail(crop)); }
        finally { crop.Dispose(); }
    }

    /// <summary>主 VM 状态变化：可见性 / 布局 / 当前图 / 原生展示切换都会重新路由。</summary>
    /// <param name="sender">事件源（主 VM）。</param>
    /// <param name="args">属性变化参数。</param>
    private void OnMainChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName is nameof(MainViewModel.CurrentFile) or nameof(MainViewModel.IsRowLayout)
            or nameof(MainViewModel.IsAnalysisViewVisible) or nameof(MainViewModel.UsesNativePresentation))
            _host.Raise();
    }

    /// <summary>「即时计算DINO/CV」开关变化后重新路由。</summary>
    /// <param name="sender">事件源（设置 VM）。</param>
    /// <param name="args">属性变化参数。</param>
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
    /// <param name="disposing">是否释放托管资源。</param>
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

    /// <summary>空白点击委托：命中诊断瓦片或细节预览时不接收，保证瓦片点按与预览定格优先。</summary>
    private sealed class BlankTapDelegate : UIGestureRecognizerDelegate
    {
        /// <summary>只在空白处接收触摸。</summary>
        /// <param name="gestureRecognizer">本识别器。</param>
        /// <param name="touch">触点。</param>
        /// <returns>不落在瓦片上时为 true。</returns>
        public override bool ShouldReceiveTouch(UIGestureRecognizer gestureRecognizer, UITouch touch)
        {
            for (var view = touch.View; view != null; view = view.Superview)
                if (view is iOSDiagnosticTile or iOSDetailPreview) return false;
            return true;
        }
    }
}
