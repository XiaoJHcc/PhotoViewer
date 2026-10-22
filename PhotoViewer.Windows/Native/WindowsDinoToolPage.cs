using System;
using CommunityToolkit.WinUI.Controls;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using PhotoViewer.Core.AI;
using PhotoViewer.Core.Image;

namespace PhotoViewer.Windows.Native;

/// <summary>
/// 工具窗口的 DINO 诊断页:原图缩略图 / PCA-RGB / Cosine 三张 DINO patch 瓦片,
/// 锐度 / 抖动拖影矢量两张 CV 瓦片与刚体拟合文本面板。
/// 数据全部来自共享 <see cref="DinoDiagnosticPresenter"/>(读库与推理都在那里完成),
/// 本页只做位图适配、瓦片布局与点击回传,绝不自行驱动推理。
/// </summary>
internal sealed class WindowsDinoToolPage : Grid
{
    /// <summary>DINO 诊断瓦片数量。</summary>
    private const int TileCountValue = 5;

    private readonly DinoDiagnosticPresenter _presenter;
    private readonly TextBlock _status;
    private readonly TextBlock _rigidText;
    private readonly WindowsDiagnosticTile _thumbnailTile;
    private readonly WindowsDiagnosticTile _pcaTile;
    private readonly WindowsDiagnosticTile _cosineTile;
    private readonly WindowsDiagnosticTile _sharpnessTile;
    private readonly WindowsDiagnosticTile _shakeTile;
    private readonly WindowsShakeFieldView _shakeOverlay = new();

    private DecodedImage? _thumbnailSource;
    private DecodedImage? _pcaSource;
    private DecodedImage? _cosineSource;
    private DecodedImage? _sharpnessSource;
    private DecodedImage? _shakeSource;
    private double _lastAspect = double.NaN;
    private string _rigidTextValue = string.Empty;
    private bool _disposed;

    /// <summary>创建 DINO 诊断页并订阅共享 presenter。</summary>
    /// <param name="presenter">共享 DINO 诊断 presenter</param>
    public WindowsDinoToolPage(DinoDiagnosticPresenter presenter)
    {
        _presenter = presenter;

        _status = new TextBlock
        {
            Style = WindowsSettingsCard.TextStyle("CaptionTextBlockStyle"),
            Foreground = WindowsSettingsCard.ThemeBrush("TextFillColorSecondaryBrush"),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 4),
        };

        _thumbnailTile = CreateTile("原图缩略图");
        _pcaTile = CreateTile("DINO PCA");
        _cosineTile = CreateTile("Cosine 16,16");
        _sharpnessTile = CreateTile("锐度");
        _shakeTile = CreateTile("抖动拖影");
        _shakeTile.SetOverlay(_shakeOverlay);

        var patchRow = new Grid
        {
            ColumnSpacing = WindowsLayout.ToolsTileSpacing,
            RowSpacing = WindowsLayout.ToolsTileSpacing,
        };
        AddColumns(patchRow, 3);
        AddTile(patchRow, _thumbnailTile, 0, 0);
        AddTile(patchRow, _pcaTile, 1, 0);
        AddTile(patchRow, _cosineTile, 2, 0);

        _rigidText = new TextBlock
        {
            FontFamily = new FontFamily("Consolas"),
            FontSize = 12,
            LineHeight = 20,
            Foreground = WindowsSettingsCard.ThemeBrush("TextFillColorPrimaryBrush"),
            TextWrapping = TextWrapping.Wrap,
            IsTextSelectionEnabled = true,
        };
        var rigidCard = new SettingsCard
        {
            Header = "抖动分析（刚体拟合）",
            Description = "|T| / |ω| / residual",
            ContentAlignment = ContentAlignment.Vertical,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Top,
            Content = _rigidText,
        };

        // 与 DINO patch 行同为三列:五张瓦片按 3+2 排列,第六格留空,保证两行瓦片同宽同高。
        var cvRow = new Grid
        {
            ColumnSpacing = WindowsLayout.ToolsTileSpacing,
            RowSpacing = WindowsLayout.ToolsTileSpacing,
        };
        AddColumns(cvRow, 3);
        AddTile(cvRow, _sharpnessTile, 0, 0);
        AddTile(cvRow, _shakeTile, 1, 0);

        rigidCard.Margin = new Thickness(0, WindowsLayout.ToolsTileSpacing, 0, 0);

        var body = new StackPanel();
        body.Children.Add(_status);
        body.Children.Add(WindowsSettingsCard.SectionHeader("DINO patch (32×32，点击任意图设定参考点)"));
        body.Children.Add(patchRow);
        body.Children.Add(WindowsSettingsCard.SectionHeader("CV 网格诊断（32×32 中心采样，像素单位）"));
        body.Children.Add(cvRow);
        body.Children.Add(rigidCard);

        Children.Add(WindowsSettingsCard.Page("DINO 诊断", body));
        _presenter.Changed += OnPresenterChanged;
        _presenter.CrosshairChanged += OnCrosshairChanged;
        Sync();
    }

    /// <summary>DINO 瓦片数量(验收用)。</summary>
    internal int TileCount => TileCountValue;

    /// <summary>五张瓦片,顺序为 DINO patch 三张 + CV 两张(验收读几何用)。</summary>
    internal WindowsDiagnosticTile[] Tiles =>
        [_thumbnailTile, _pcaTile, _cosineTile, _sharpnessTile, _shakeTile];

    /// <summary>缩略图瓦片是否已出图(验收用)。</summary>
    internal bool HasThumbnail => _thumbnailTile.HasBitmap;

    /// <summary>状态文本(验收用)。</summary>
    internal string StatusText => _status.Text;

    /// <summary>全部瓦片是否都已有内容(验收用)。</summary>
    internal bool HasAllTiles => _thumbnailTile.HasBitmap && _pcaTile.HasContent
        && _cosineTile.HasContent && _sharpnessTile.HasContent && _shakeTile.HasContent;

    /// <summary>退订 presenter,窗口关闭时调用。</summary>
    internal void Detach()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _presenter.Changed -= OnPresenterChanged;
        _presenter.CrosshairChanged -= OnCrosshairChanged;
        ClearTiles();
    }

    /// <summary>把 presenter 的瓦片状态同步到原生控件;位图按引用比较,未变化不重复上传。</summary>
    internal void Sync()
    {
        if (_disposed)
        {
            return;
        }

        _status.Text = _presenter.Status;
        if (!string.Equals(_rigidTextValue, _presenter.RigidMotionText, StringComparison.Ordinal))
        {
            _rigidTextValue = _presenter.RigidMotionText;
            _rigidText.Text = _rigidTextValue;
        }

        double aspect = _presenter.AspectRatio;
        if (!double.IsNaN(_lastAspect) && Math.Abs(aspect - _lastAspect) > 1e-9)
        {
            _pcaSource = null;
            _cosineSource = null;
            _sharpnessSource = null;
        }

        _lastAspect = aspect;
        SyncBitmap(_thumbnailTile, _presenter.ThumbnailTile, ref _thumbnailSource, aspect, upscale: false);
        SyncBitmap(_pcaTile, _presenter.PcaTile, ref _pcaSource, aspect, upscale: true);
        SyncBitmap(_cosineTile, _presenter.CosineTile, ref _cosineSource, aspect, upscale: true);
        SyncBitmap(_sharpnessTile, _presenter.SharpnessTile, ref _sharpnessSource, aspect, upscale: true);

        _shakeTile.SetAspect(aspect);
        _shakeTile.SetLabel(_presenter.ShakeTile.ShortLabel);
        _shakeTile.SetCaption(CaptionFor(_presenter.ShakeTile));
        _shakeTile.SetPlaceholder(_presenter.ShakeTile.Placeholder);
        var background = _presenter.ShakeTile.Background;
        if (!ReferenceEquals(background, _shakeSource))
        {
            _shakeSource = background;
            _shakeTile.SetBackground(background == null ? null : WindowsImageAdapter.ToSource(background));
        }

        _shakeOverlay.ShakeField = _presenter.ShakeTile.ShakeField;
        SyncCrosshair();
    }

    /// <summary>presenter 的瓦片/文本变化后整体同步。</summary>
    private void OnPresenterChanged()
    {
        if (!_disposed)
        {
            Sync();
        }
    }

    /// <summary>准星变化只重画准星,不重复上传位图。</summary>
    private void OnCrosshairChanged()
    {
        if (!_disposed)
        {
            SyncCrosshair();
        }
    }

    /// <summary>同步单张瓦片:aspect/标签/说明/占位与位图引用。</summary>
    /// <param name="view">目标瓦片</param>
    /// <param name="tile">presenter 侧状态</param>
    /// <param name="source">最近一次处理的源位图</param>
    /// <param name="aspect">照片纵横比</param>
    /// <param name="upscale">是否按诊断网格放大</param>
    private static void SyncBitmap(
        WindowsDiagnosticTile view,
        AnalysisTile tile,
        ref DecodedImage? source,
        double aspect,
        bool upscale)
    {
        view.SetAspect(aspect);
        view.SetLabel(tile.ShortLabel);
        view.SetCaption(CaptionFor(tile));
        if (!ReferenceEquals(tile.Source, source))
        {
            source = tile.Source;
            var bitmap = tile.Source == null
                ? null
                : upscale
                    ? WindowsDiagnosticBitmap.ToUpscaled(tile.Source, aspect)
                    : WindowsImageAdapter.ToSource(tile.Source);
            view.SetBitmap(bitmap, tile.Placeholder);
        }
        else
        {
            view.SetPlaceholder(tile.Placeholder);
        }
    }

    /// <summary>说明文字与角标重复时不再重复占一行。</summary>
    /// <param name="tile">瓦片状态</param>
    /// <returns>说明文字;null 表示隐藏</returns>
    private static string? CaptionFor(AnalysisTile tile)
        => string.Equals(tile.Caption, tile.ShortLabel, StringComparison.Ordinal) ? null : tile.Caption;

    /// <summary>把 presenter 的准星同步到全部瓦片。</summary>
    private void SyncCrosshair()
    {
        double? u = _presenter.CrosshairU;
        double? v = _presenter.CrosshairV;
        foreach (var tile in new[] { _thumbnailTile, _pcaTile, _cosineTile, _sharpnessTile, _shakeTile })
        {
            tile.SetCrosshair(u, v);
        }
    }

    /// <summary>瓦片内容点击:落准星并以点击点为 cosine 参考点重算。</summary>
    private void OnTileClicked(double u, double v) => _presenter.OnTileClicked(u, v);

    /// <summary>瓦片空白点击:清空准星。</summary>
    private void ClearCrosshair() => _presenter.ClearCrosshair();

    /// <summary>清空全部瓦片引用。</summary>
    private void ClearTiles()
    {
        foreach (var tile in new[] { _thumbnailTile, _pcaTile, _cosineTile, _sharpnessTile, _shakeTile })
        {
            tile.Dispose();
        }
    }

    /// <summary>创建统一点击语义的诊断瓦片。</summary>
    /// <param name="label">初始药丸标签</param>
    /// <returns>瓦片</returns>
    private WindowsDiagnosticTile CreateTile(string label)
    {
        var tile = new WindowsDiagnosticTile
        {
            TileClicked = OnTileClicked,
            BackgroundClicked = ClearCrosshair,
        };
        tile.SetLabel(label);
        return tile;
    }

    /// <summary>给三列网格加等宽列。</summary>
    /// <param name="grid">目标网格</param>
    /// <param name="count">列数</param>
    private static void AddColumns(Grid grid, int count)
    {
        for (var index = 0; index < count; index++)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        }
    }

    /// <summary>把瓦片放到网格指定列。</summary>
    /// <param name="grid">目标网格</param>
    /// <param name="tile">瓦片</param>
    /// <param name="column">列索引</param>
    /// <param name="row">行索引</param>
    private static void AddTile(Grid grid, WindowsDiagnosticTile tile, int column, int row)
    {
        // 无说明文字的缩略图瓦片比同排其它瓦片矮;Top 对齐保证所有正方形上沿齐平。
        tile.VerticalAlignment = VerticalAlignment.Top;
        tile.HorizontalAlignment = HorizontalAlignment.Stretch;
        Grid.SetColumn(tile, column);
        Grid.SetRow(tile, row);
        grid.Children.Add(tile);
    }
}
