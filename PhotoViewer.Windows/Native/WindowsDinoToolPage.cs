using System;
using CommunityToolkit.WinUI.Controls;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using PhotoViewer.Core.AI;
using PhotoViewer.Core.Image;

namespace PhotoViewer.Windows.Native;

/// <summary>
/// 工具窗口的诊断页。AI 版是原图缩略图 / PCA-RGB / Cosine 三张 DINO patch 瓦片加锐度 / 抖动拖影矢量两张 CV 瓦片,
/// 标准版是原图缩略图 / 锐度 / 抖动拖影三张 CV 瓦片(同排三列),两版都带刚体拟合整宽文本(EDITION-04)。
/// 数据全部来自共享 presenter(AI 版 <see cref="DinoDiagnosticPresenter"/>,标准版 <see cref="CvDiagnosticPresenter"/>;
/// 读库与现算都在那里完成),本页只做位图适配、瓦片布局与点击回传,绝不自行驱动推理。
/// </summary>
internal sealed class WindowsDinoToolPage : Grid
{
#if PHOTOVIEWER_DINO
    /// <summary>DINO 诊断瓦片数量。</summary>
    private const int TileCountValue = 5;
#else
    /// <summary>CV 诊断瓦片数量(原图缩略图 / 锐度 / 抖动拖影)。</summary>
    private const int TileCountValue = 3;
#endif

    private readonly TextBlock _status;
    private readonly TextBlock _rigidText;
    private readonly WindowsDiagnosticTile _thumbnailTile;
    private readonly WindowsDiagnosticTile _sharpnessTile;
    private readonly WindowsDiagnosticTile _shakeTile;
    private readonly WindowsShakeFieldView _shakeOverlay = new();
#if PHOTOVIEWER_DINO
    private readonly DinoDiagnosticPresenter _presenter;
    private readonly WindowsDiagnosticTile _pcaTile;
    private readonly WindowsDiagnosticTile _cosineTile;
    private DecodedImage? _pcaSource;
    private DecodedImage? _cosineSource;
#else
    private readonly CvDiagnosticPresenter _presenter;
#endif

    private DecodedImage? _thumbnailSource;
    private DecodedImage? _sharpnessSource;
    private DecodedImage? _shakeSource;
    private double _lastAspect = double.NaN;
    private string _rigidTextValue = string.Empty;
    private bool _disposed;

#if PHOTOVIEWER_DINO
    /// <summary>创建 DINO 诊断页并订阅共享 presenter。</summary>
    /// <param name="presenter">共享 DINO 诊断 presenter</param>
    public WindowsDinoToolPage(DinoDiagnosticPresenter presenter)
#else
    /// <summary>创建 CV 诊断页并订阅共享 presenter。</summary>
    /// <param name="presenter">共享 CV 诊断 presenter</param>
    public WindowsDinoToolPage(CvDiagnosticPresenter presenter)
#endif
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
        _sharpnessTile = CreateTile("锐度");
        _shakeTile = CreateTile("抖动拖影");
        _shakeTile.SetOverlay(_shakeOverlay);

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
        rigidCard.Margin = new Thickness(0, WindowsLayout.ToolsTileSpacing, 0, 0);

        var body = new StackPanel();
        body.Children.Add(_status);
#if PHOTOVIEWER_DINO
        _pcaTile = CreateTile("DINO PCA");
        _cosineTile = CreateTile("Cosine 16,16");

        var patchRow = new Grid
        {
            ColumnSpacing = WindowsLayout.ToolsTileSpacing,
            RowSpacing = WindowsLayout.ToolsTileSpacing,
        };
        AddColumns(patchRow, 3);
        AddTile(patchRow, _thumbnailTile, 0, 0);
        AddTile(patchRow, _pcaTile, 1, 0);
        AddTile(patchRow, _cosineTile, 2, 0);

        // 与 DINO patch 行同为三列:五张瓦片按 3+2 排列,第六格留空,保证两行瓦片同宽同高。
        var cvRow = new Grid
        {
            ColumnSpacing = WindowsLayout.ToolsTileSpacing,
            RowSpacing = WindowsLayout.ToolsTileSpacing,
        };
        AddColumns(cvRow, 3);
        AddTile(cvRow, _sharpnessTile, 0, 0);
        AddTile(cvRow, _shakeTile, 1, 0);

        body.Children.Add(WindowsSettingsCard.SectionHeader("DINO patch (32×32，点击任意图设定参考点)"));
        body.Children.Add(patchRow);
        body.Children.Add(WindowsSettingsCard.SectionHeader("CV 网格诊断（32×32 中心采样，像素单位）"));
        body.Children.Add(cvRow);
#else
        var cvRow = new Grid
        {
            ColumnSpacing = WindowsLayout.ToolsTileSpacing,
            RowSpacing = WindowsLayout.ToolsTileSpacing,
        };
        AddColumns(cvRow, 3);
        AddTile(cvRow, _thumbnailTile, 0, 0);
        AddTile(cvRow, _sharpnessTile, 1, 0);
        AddTile(cvRow, _shakeTile, 2, 0);

        body.Children.Add(WindowsSettingsCard.SectionHeader("CV 网格诊断（32×32 中心采样，像素单位）"));
        body.Children.Add(cvRow);
#endif
        body.Children.Add(rigidCard);

        Children.Add(WindowsSettingsCard.Page(WindowsProductInfo.DiagnosticName, body));
        _presenter.Changed += OnPresenterChanged;
        _presenter.CrosshairChanged += OnCrosshairChanged;
        Sync();
    }

    /// <summary>诊断瓦片数量(验收用)。</summary>
    internal int TileCount => TileCountValue;

#if PHOTOVIEWER_DINO
    /// <summary>五张瓦片,顺序为 DINO patch 三张 + CV 两张(验收读几何用)。</summary>
    internal WindowsDiagnosticTile[] Tiles =>
        [_thumbnailTile, _pcaTile, _cosineTile, _sharpnessTile, _shakeTile];
#else
    /// <summary>三张 CV 瓦片,顺序为原图缩略图 + 锐度 + 抖动拖影(验收读几何用)。</summary>
    internal WindowsDiagnosticTile[] Tiles => [_thumbnailTile, _sharpnessTile, _shakeTile];
#endif

    /// <summary>缩略图瓦片是否已出图(验收用)。</summary>
    internal bool HasThumbnail => _thumbnailTile.HasBitmap;

    /// <summary>状态文本(验收用)。</summary>
    internal string StatusText => _status.Text;

    /// <summary>全部瓦片是否都已有内容(验收用)。</summary>
    internal bool HasAllTiles
    {
        get
        {
            bool core = _thumbnailTile.HasBitmap && _sharpnessTile.HasContent && _shakeTile.HasContent;
#if PHOTOVIEWER_DINO
            return core && _pcaTile.HasContent && _cosineTile.HasContent;
#else
            return core;
#endif
        }
    }

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
#if PHOTOVIEWER_DINO
            _pcaSource = null;
            _cosineSource = null;
#endif
            _sharpnessSource = null;
            _shakeSource = null;
        }

        _lastAspect = aspect;
        SyncBitmap(_thumbnailTile, _presenter.ThumbnailTile, ref _thumbnailSource, aspect, upscale: false);
#if PHOTOVIEWER_DINO
        SyncBitmap(_pcaTile, _presenter.PcaTile, ref _pcaSource, aspect, upscale: true);
        SyncBitmap(_cosineTile, _presenter.CosineTile, ref _cosineSource, aspect, upscale: true);
#endif
        SyncBitmap(_sharpnessTile, _presenter.SharpnessTile, ref _sharpnessSource, aspect, upscale: true);

        _shakeTile.SetAspect(aspect);
        _shakeTile.SetLabel(_presenter.ShakeTile.ShortLabel);
        _shakeTile.SetCaption(CaptionFor(_presenter.ShakeTile));
        var background = _presenter.ShakeTile.Background;
        var shakeField = _presenter.ShakeTile.ShakeField;
        if (background == null && shakeField == null)
        {
            // 未提取/失败/换图代际:压暗背景、矢量场与旧主图一起清掉,只留占位文字。
            _shakeSource = null;
            _shakeTile.ClearContent(_presenter.ShakeTile.Placeholder);
        }
        else
        {
            _shakeTile.SetPlaceholder(_presenter.ShakeTile.Placeholder);
            if (!ReferenceEquals(background, _shakeSource))
            {
                _shakeSource = background;
                _shakeTile.SetBackground(background == null ? null : WindowsImageAdapter.ToSource(background));
            }
        }

        _shakeOverlay.ShakeField = shakeField;
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

    /// <summary>
    /// 同步单张瓦片:aspect/标签/说明/占位与位图引用。源为空时无条件清掉旧图与缓存引用,
    /// 只比较引用会漏掉"代际重置后源与缓存都为 null"的情形,旧图会留在瓦片上(与分析栏同一修正)。
    /// </summary>
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
        if (tile.Source == null)
        {
            source = null;
            view.ClearContent(tile.Placeholder);
            return;
        }

        if (!ReferenceEquals(tile.Source, source))
        {
            source = tile.Source;
            view.SetBitmap(
                upscale ? WindowsDiagnosticBitmap.ToUpscaled(tile.Source, aspect) : WindowsImageAdapter.ToSource(tile.Source),
                tile.Placeholder);
            return;
        }

        view.SetPlaceholder(tile.Placeholder);
    }

    /// <summary>说明文字与角标重复时不再重复占一行。</summary>
    /// <param name="tile">瓦片状态</param>
    /// <returns>说明文字;null 表示隐藏</returns>
    private static string? CaptionFor(AnalysisTile tile)
        => string.Equals(tile.Caption, tile.ShortLabel, StringComparison.Ordinal) ? null : tile.Caption;

    /// <summary>把 presenter 的准星同步到本页全部瓦片(标准版三张瓦片共享同一准星,EDITION-04)。</summary>
    private void SyncCrosshair()
    {
        double? u = _presenter.CrosshairU;
        double? v = _presenter.CrosshairV;
        foreach (var tile in Tiles)
        {
            tile.SetCrosshair(u, v);
        }
    }

    /// <summary>瓦片内容点击:落准星;AI 版同时设为 cosine 参考点重算。</summary>
    private void OnTileClicked(double u, double v) => _presenter.OnTileClicked(u, v);

    /// <summary>瓦片空白点击:清空准星。</summary>
    private void ClearCrosshair() => _presenter.ClearCrosshair();

    /// <summary>清空全部瓦片引用。</summary>
    private void ClearTiles()
    {
        foreach (var tile in Tiles)
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
