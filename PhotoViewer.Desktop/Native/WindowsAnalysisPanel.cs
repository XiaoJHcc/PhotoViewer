using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Shapes;
using PhotoViewer.Core;
using PhotoViewer.Core.AI;
using PhotoViewer.Core.Color;
using PhotoViewer.Core.Database;
using PhotoViewer.Core.Image;
using PhotoViewer.Core.Platform;
using PhotoViewer.ViewModels.Main;
using PhotoViewer.ViewModels.Settings;
using SkiaSharp;
using Windows.Foundation;
using Windows.UI;

namespace PhotoViewer.Desktop.Native;

/// <summary>
/// 原生分析栏:细节预览(中心 / 索尼对焦点)+ 直方图 + DINO PCA + Cosine + 锐度 + 抖动矢量场。
///
/// 数据链路与旧壳 <c>AnalysisViewModel</c> 同源但由本类自己编排(旧 VM 绑 Avalonia 位图):
/// 指纹 → <see cref="AnalysisResultCache"/> 命中即贴;miss 走 <see cref="AnalysisDataReader"/> 读库 +
/// <see cref="AnalysisComputer"/> 现算后回填。开启"即时计算"时:增强态对增强图实时重算,未提取照片回退到主图现算。
/// 细节预览是源像素 1:1 的方形裁剪(旧壳 CropSize 语义);抖动瓦片把 <see cref="ShakeField"/> 光栅化成矢量箭头位图
/// (旧壳是自绘控件,信息一致)。
/// </summary>
internal sealed class WindowsAnalysisPanel : Grid
{
    private readonly MainViewModel _main;
    private readonly PhotoLoadSession _session;
    private readonly Func<DecodedImage?> _currentDisplay;
    private readonly Action<double, double> _focusImagePoint;
    private readonly Action<Rect?> _setHighlight;
    private readonly Border _frame;
    private readonly WindowsAnalysisTilePanel _tiles;

    private readonly AnalysisPreviewTile _focusTile;
    private readonly AnalysisPreviewTile _centerTile;
    private readonly AnalysisDiagnosticTile _histogramTile;
    private readonly AnalysisDiagnosticTile _pcaTile;
    private readonly AnalysisDiagnosticTile _cosineTile;
    private readonly AnalysisDiagnosticTile _sharpnessTile;
    private readonly AnalysisDiagnosticTile _shakeTile;

    /// <summary>推理闸:同一时刻只允许一次 CV+DINO 现算(两个并发 DirectML 推理会崩进程)。</summary>
    private static readonly SemaphoreSlim InferenceGate = new(1, 1);

    private CancellationTokenSource? _cts;
    private ImageFile? _file;
    private float[]? _patches;
    private int _refGridX = AnalysisComputer.PatchGridPixels / 2;
    private int _refGridY = AnalysisComputer.PatchGridPixels / 2;
    private int _histogramToken;
    private bool _focusTileAttached;

    /// <summary>当前诊断瓦片的内容纵横比(主图宽/高;诊断网格按它铺回原比例)。</summary>
    private double _aspect = 1;

    /// <summary>
    /// 创建分析栏。
    /// </summary>
    /// <param name="main">主视图模型</param>
    /// <param name="session">主图加载会话(细节预览 / 直方图 / 回退计算的图像源)</param>
    /// <param name="currentDisplay">当前显示帧(原片或增强,线性 RGBA16F)</param>
    /// <param name="focusImagePoint">双击细节预览时把主图对准该图像点(uv)</param>
    /// <param name="setHighlight">细节预览联动主图绿框(uv 矩形;null 清除)</param>
    public WindowsAnalysisPanel(MainViewModel main, PhotoLoadSession session,
        Func<DecodedImage?> currentDisplay,
        Action<double, double> focusImagePoint, Action<Rect?> setHighlight)
    {
        _main = main;
        _session = session;
        _currentDisplay = currentDisplay;
        _focusImagePoint = focusImagePoint;
        _setHighlight = setHighlight;

        _focusTile = new AnalysisPreviewTile("对焦点", _focusImagePoint, Highlight);
        _centerTile = new AnalysisPreviewTile("中心", _focusImagePoint, Highlight);
        _histogramTile = new AnalysisDiagnosticTile("直方图", null, null);
        _pcaTile = new AnalysisDiagnosticTile("DINO PCA", OnTileClicked, ClearCrosshair);
        _cosineTile = new AnalysisDiagnosticTile(FormatCosineLabel(_refGridX, _refGridY), OnTileClicked, ClearCrosshair);
        _sharpnessTile = new AnalysisDiagnosticTile("锐度", OnTileClicked, ClearCrosshair);
        _shakeTile = new AnalysisDiagnosticTile("抖动拖影", OnTileClicked, ClearCrosshair);

        _tiles = new WindowsAnalysisTilePanel
        {
            Orientation = Orientation.Horizontal,
            ItemSpacing = WindowsLayout.AnalysisSpacing,
        };
        // 边长由布局系统给出;变化后只重建细节预览的裁剪图,绝不在布局回调里碰位图。
        _tiles.SideChanged = _ => UpdatePreviewTiles();
        _tiles.Children.Add(_centerTile);
        _tiles.Children.Add(_histogramTile);
        _tiles.Children.Add(_pcaTile);
        _tiles.Children.Add(_cosineTile);
        _tiles.Children.Add(_sharpnessTile);
        _tiles.Children.Add(_shakeTile);

        _frame = new Border
        {
            Background = new SolidColorBrush(WindowsLayout.BarBackground),
            BorderBrush = new SolidColorBrush(WindowsLayout.Divider),
            BorderThickness = new Thickness(0, WindowsLayout.DividerThickness, 0, 0),
            Padding = new Thickness(WindowsLayout.AnalysisPadding),
            Child = _tiles,
        };
        Children.Add(_frame);

        _main.PropertyChanged += OnMainChanged;
        _main.Settings.PropertyChanged += OnSettingsChanged;
        _session.Changed += OnSessionChanged;

        SetOrientation(_main.IsRowLayout);
        SetDiagnosticsPlaceholder("未提取");
        UpdatePreviewTiles();
    }

    /// <summary>释放订阅与自持位图。</summary>
    public void Detach()
    {
        _cts?.Cancel();
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
    }

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
    /// <param name="u">横向 0-1</param>
    /// <param name="v">纵向 0-1</param>
    internal void TriggerTileClickForAcceptance(double u, double v) => OnTileClicked(u, v);

    /// <summary>切换上下/左右布局:上下布局瓦片横排(顶部分隔线),左右布局竖排(左侧分隔线)。</summary>
    /// <param name="isRowLayout">是否为上下布局</param>
    public void SetOrientation(bool isRowLayout)
    {
        _tiles.Orientation = isRowLayout ? Orientation.Horizontal : Orientation.Vertical;
        _frame.BorderThickness = isRowLayout
            ? new Thickness(0, WindowsLayout.DividerThickness, 0, 0)
            : new Thickness(WindowsLayout.DividerThickness, 0, 0, 0);
    }

    /// <summary>主图帧或增强状态变化:刷新细节预览、对焦点项、直方图并重新路由诊断数据。</summary>
    public void Refresh()
    {
        UpdateFocusTile();
        UpdatePreviewTiles();
        RefreshHistogram();
        SetSource(_main.CurrentFile);
    }

    /// <summary>分析栏显隐变化(可见时才读库/重算,隐藏即撤销)。</summary>
    public void OnVisibilityChanged()
    {
        // 从 MenuFlyoutItem.Click 直接进来,异常跨 COM 边界就是进程级崩溃,必须在这里兜住并留痕。
        try
        {
            if (!_main.IsAnalysisViewVisible)
            {
                // 收起时指针不会再有 PointerExited,悬停态必须自己丢掉,否则重新展开会补画绿框。
                _centerTile.ResetHover();
                _focusTile.ResetHover();
            }

            UpdateFocusTile();
            UpdatePreviewTiles();
            RefreshHistogram();
            SetSource(_main.CurrentFile);
        }
        catch (Exception ex)
        {
            NativeStartupLog.Fail("analysis visibility", ex);
        }
    }

    /// <summary>主视图模型变化:切图与布局。</summary>
    private void OnMainChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(MainViewModel.CurrentFile):
                UpdateFocusTile();
                UpdatePreviewTiles();
                SetSource(_main.CurrentFile);
                break;
            case nameof(MainViewModel.IsRowLayout):
                SetOrientation(_main.IsRowLayout);
                break;
        }
    }

    /// <summary>设置变化:即时计算开关切回 DB 只读路径或实时重算。</summary>
    private void OnSettingsChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SettingsViewModel.AnalysisImmediateCompute))
        {
            SetSource(_main.CurrentFile);
        }
    }

    /// <summary>会话帧变化:细节预览与直方图跟随(换图、代理→Full、增强)。</summary>
    private void OnSessionChanged()
    {
        try
        {
            UpdatePreviewTiles();
            RefreshHistogram();
        }
        catch (Exception ex)
        {
            NativeStartupLog.Fail("analysis session changed", ex);
        }
    }

    /// <summary>有索尼对焦数据时把对焦点瓦片挂到最前(旧壳同口径)。</summary>
    private void UpdateFocusTile()
    {
        var exif = _main.CurrentFile?.ExifData;
        var position = exif?.SonyFocusPosition;
        bool show = position is { ImageWidth: > 0, ImageHeight: > 0 };
        if (show)
        {
            var value = position!.Value;
            double u = Math.Clamp(value.FocusX / (double)value.ImageWidth, 0, 1);
            double v = Math.Clamp(value.FocusY / (double)value.ImageHeight, 0, 1);
            Rect? frame = null;
            if (exif!.SonyFocusFrameSize is { } size)
            {
                double width = Math.Min(1, size.Width / (double)value.ImageWidth);
                double height = Math.Min(1, size.Height / (double)value.ImageHeight);
                frame = new Rect(Math.Clamp(u - (width / 2), 0, Math.Max(0, 1 - width)),
                    Math.Clamp(v - (height / 2), 0, Math.Max(0, 1 - height)), width, height);
            }

            _focusTile.SetCenter(u, v, frame);
        }

        if (show && !_focusTileAttached)
        {
            _tiles.Children.Insert(0, _focusTile);
            _focusTileAttached = true;
        }
        else if (!show && _focusTileAttached)
        {
            _tiles.Children.Remove(_focusTile);
            _focusTileAttached = false;
            _focusTile.Clear();
        }
    }

    /// <summary>刷新细节预览瓦片(中心 + 对焦点)的裁剪图。</summary>
    private void UpdatePreviewTiles()
    {
        var image = _main.IsAnalysisViewVisible ? _currentDisplay() : null;
        if (image == null)
        {
            _centerTile.Clear();
            _focusTile.Clear();
            return;
        }

        int cropPixels = Math.Max(1, (int)Math.Round(_tiles.Side));
        _centerTile.SetImage(image, 0.5, 0.5, cropPixels, null);
        if (_focusTileAttached)
        {
            _focusTile.Rebuild(image, cropPixels);
        }
    }

    /// <summary>从当前显示帧现算直方图(降采样到 1024 长边,形状与全分辨率一致)。</summary>
    private async void RefreshHistogram()
    {
        int token = ++_histogramToken;
        var source = _currentDisplay();
        if (!_main.IsAnalysisViewVisible || source == null)
        {
            SwapHistogram(null, token);
            return;
        }

        if (!source.TryAcquire())
        {
            SwapHistogram(null, token);
            return;
        }

        try
        {
            // 位图只能在 UI 线程创建:后台只做降采样 + 直方图渲染
            using var decoded = await Task.Run(() =>
            {
                using var srgb = ToSrgb8Downscaled(source, WindowsLayout.AnalysisHistogramSampleLongEdge);
                return HistogramRenderer.Render(srgb, 512, 512);
            });
            SwapHistogram(WindowsImageAdapter.ToSource(decoded), token);
        }
        catch (Exception ex)
        {
            NativeStartupLog.Fail("analysis histogram", ex);
        }
        finally
        {
            source.Release();
        }
    }

    /// <summary>替换直方图瓦片内容;过期 token 丢弃。</summary>
    private void SwapHistogram(WriteableBitmap? bitmap, int token)
    {
        if (token != _histogramToken)
        {
            return;
        }

        _histogramTile.SetBitmap(bitmap, bitmap == null ? "计算中" : null);
    }

    /// <summary>切图/显隐/设置变化:按旧壳同源的分支顺序决定诊断瓦片内容。</summary>
    /// <param name="file">当前文件</param>
    private void SetSource(ImageFile? file)
    {
        _cts?.Cancel();
        _file = file;

        if (!_main.IsAnalysisViewVisible || file == null)
        {
            ClearDiagnosticsSync();
            return;
        }

        var cts = new CancellationTokenSource();
        _cts = cts;
        UiDispatcher.Post(() =>
        {
            try
            {
                LoadDeferred(file, cts.Token);
            }
            catch (Exception ex)
            {
                NativeStartupLog.Fail("analysis deferred", ex);
            }
        }, UiPriority.Background);
    }

    /// <summary>Background 优先级回调:快路径同步贴,慢路径异步读库。</summary>
    private void LoadDeferred(ImageFile file, CancellationToken ct)
    {
        if (ct.IsCancellationRequested) return;

        // 诊断一律基于原片(readByFingerprint / 即时计算都吃原片):增强只影响主图与细节预览/直方图。
        // 快路径:EXIF 已加载 + 缓存命中。
        if (file.IsExifLoaded)
        {
            var fingerprint = TryComputeFingerprintSync(file);
            var hit = fingerprint != null ? AnalysisResultCache.TryGet(fingerprint) : null;
            if (hit != null)
            {
                ApplyEntry(hit, ct);
                if (IsEntryEmpty(hit) && _main.Settings.AnalysisImmediateCompute)
                {
                    var immediate = AnalysisResultCache.TryGetImmediate(file.File.Path.LocalPath);
                    if (immediate != null)
                    {
                        ApplyEntry(immediate, ct);
                    }
                    else
                    {
                        SetDiagnosticsPlaceholder("计算中…");
                        _ = LoadFallbackAsync(file, ct);
                    }
                }

                return;
            }
        }

        // 慢路径:算指纹 → 查库 → 派生层现算 → 落 cache。
        _ = LoadAsync(file, ct);
    }

    /// <summary>慢路径:读库 + 现算,空结果且开即时计算时回退主图现算。</summary>
    private async Task LoadAsync(ImageFile file, CancellationToken ct)
    {
        try
        {
            var fingerprint = await AnalysisDataReader.ComputeFingerprintAsync(file, ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();

            var hit = AnalysisResultCache.TryGet(fingerprint);
            if (hit != null)
            {
                ApplyEntry(hit, ct);
                return;
            }

            var data = string.IsNullOrEmpty(fingerprint)
                ? new AnalysisDataReader.Result()
                : await AnalysisDataReader.ReadByFingerprintAsync(fingerprint, ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            var entry = AnalysisComputer.Compute(data);
            if (!string.IsNullOrEmpty(fingerprint))
            {
                AnalysisResultCache.Put(fingerprint, entry);
            }

            ApplyEntry(entry, ct);

            if (data.IsEmpty && _main.Settings.AnalysisImmediateCompute)
            {
                var immediate = AnalysisResultCache.TryGetImmediate(file.File.Path.LocalPath);
                if (immediate != null)
                {
                    ApplyEntry(immediate, ct);
                    return;
                }

                SetDiagnosticsPlaceholder("计算中…");
                await LoadFallbackAsync(file, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            NativeStartupLog.Fail("analysis load", ex);
        }
    }

    /// <summary>
    /// 未提取回退:延迟 500 ms + 等主图就绪后对当前主图现算(产物落即时缓存,不写库)。
    /// 快速翻页在延迟期即被取消,零推理开销。
    /// </summary>
    private async Task LoadFallbackAsync(ImageFile file, CancellationToken ct)
    {
        try
        {
            await Task.Delay(WindowsLayout.AnalysisFallbackDelayMilliseconds, ct).ConfigureAwait(false);
            var waited = 0;
            while (!ct.IsCancellationRequested && waited < 5000)
            {
                if (_session.Image != null) break;
                await Task.Delay(120, ct).ConfigureAwait(false);
                waited += 120;
            }

            ct.ThrowIfCancellationRequested();

            var key = AnalysisResultCache.ImmediateKey(file.File.Path.LocalPath, enhanced: false);
            var hit = AnalysisResultCache.TryGetImmediate(key);
            if (hit != null)
            {
                ApplyEntry(hit, ct);
                return;
            }

            var frame = _session.Image;
            if (frame == null)
            {
                PostPlaceholder("未提取", ct);
                return;
            }

            DecodedImage? srgb = null;
            try
            {
                srgb = await Task.Run(() => ColorEngine.ToSrgb8(frame), ct).ConfigureAwait(false);
                bool ok = await ComputeDiagnosticsAsync(srgb, key, ct).ConfigureAwait(false);
                if (!ok && !ct.IsCancellationRequested)
                {
                    PostPlaceholder("未提取", ct);
                }
            }
            finally
            {
                srgb?.Dispose();
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            NativeStartupLog.Fail("analysis fallback", ex);
            PostPlaceholder("未提取", ct);
        }
    }

    /// <summary>对给定 8 位图跑 CV + DINO 推理并复用派生层,产物落即时缓存。</summary>
    private async Task<bool> ComputeDiagnosticsAsync(DecodedImage image, string key, CancellationToken ct)
    {
        // 增强图归主窗口所有:现算期间持一份租约,避免主窗口热替换时把像素缓冲归还进池
        if (!image.TryAcquire())
        {
            return false;
        }

        await InferenceGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ct.ThrowIfCancellationRequested();
            var cv = await CvGridExtractor.ExtractAsync(image, ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            var (_, patches) = await DinoFeatureExtractor.ExtractDualAsync(image, includePatches: true, ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();

            var data = new AnalysisDataReader.Result
            {
                Patches = patches,
                Cv = cv,
                CvImageWidth = image.Width,
                CvImageHeight = image.Height,
            };
            var entry = AnalysisComputer.Compute(data);
            AnalysisResultCache.PutImmediate(key, entry);
            ApplyEntry(entry, ct);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (Exception ex)
        {
            NativeStartupLog.Fail("analysis immediate compute", ex);
            return false;
        }
        finally
        {
            InferenceGate.Release();
            image.Release();
        }
    }

    /// <summary>把 cache 项贴到瓦片(UI 线程 Background;位图归 cache 所有,本类只引用)。</summary>
    private void ApplyEntry(AnalysisResultCache.Entry entry, CancellationToken ct)
    {
        UiDispatcher.Post(() =>
        {
            if (ct.IsCancellationRequested) return;
            try
            {
                ApplyEntryCore(entry);
            }
            catch (Exception ex)
            {
                NativeStartupLog.Fail("analysis applyEntryCore", ex);
            }
        }, UiPriority.Background);
    }

    /// <summary>应用 cache 项(同步,UI 线程)。</summary>
    private void ApplyEntryCore(AnalysisResultCache.Entry entry)
    {
        _patches = entry.Patches;
        var aspect = entry.AspectRatio > 0 ? entry.AspectRatio : 1;
        _aspect = aspect;
        _sharpnessTile.SetAspect(aspect);
        _sharpnessTile.SetBitmap(ToSourceUpscaled(entry.SharpnessBmp, aspect), entry.SharpnessBmp == null ? "未提取" : null);

        _pcaTile.SetAspect(aspect);
        _pcaTile.SetBitmap(ToSourceUpscaled(entry.PcaBmp, aspect), entry.PcaBmp == null ? "未提取" : null);
        _refGridX = AnalysisComputer.PatchGridPixels / 2;
        _refGridY = AnalysisComputer.PatchGridPixels / 2;
        _cosineTile.SetAspect(aspect);
        var cosineBitmap = ToSourceUpscaled(entry.CenterCosineBmp, aspect);
        _cosineTile.SetBitmap(cosineBitmap, entry.CenterCosineBmp == null ? "未提取" : null);
        _cosineTile.SetLabel(FormatCosineLabel(_refGridX, _refGridY));
        _shakeTile.SetAspect(aspect);
        _shakeTile.SetBitmap(entry.ShakeField == null ? null : BuildShakeBitmap(entry.ShakeField, aspect),
            entry.ShakeField == null ? "未提取" : null);
        _shakeTile.SetLabel(entry.ShakeLabel);
        ApplyCrosshair();
    }

    /// <summary>设置 4 个诊断瓦片占位文本(在 ApplyEntryCore 之后调用以覆盖)。</summary>
    private void SetDiagnosticsPlaceholder(string text)
    {
        _pcaTile.SetBitmap(null, text);
        _cosineTile.SetBitmap(null, text);
        _sharpnessTile.SetBitmap(null, text);
        _shakeTile.SetBitmap(null, text);
        ApplyCrosshair();
    }

    /// <summary>Background 优先级设置占位文本。</summary>
    private void PostPlaceholder(string text, CancellationToken ct)
    {
        UiDispatcher.Post(() =>
        {
            if (ct.IsCancellationRequested) return;
            SetDiagnosticsPlaceholder(text);
        }, UiPriority.Background);
    }

    /// <summary>诊断瓦片点击:落准星 + 以点击点为 cosine 参考点重算。</summary>
    private void OnTileClicked(double u, double v)
    {
        u = Math.Clamp(u, 0, 1);
        v = Math.Clamp(v, 0, 1);
        ApplyCrosshair(u, v);

        if (_patches == null)
        {
            return;
        }

        int gx = Math.Clamp((int)(u * AnalysisComputer.PatchGridPixels), 0, AnalysisComputer.PatchGridPixels - 1);
        int gy = Math.Clamp((int)(v * AnalysisComputer.PatchGridPixels), 0, AnalysisComputer.PatchGridPixels - 1);
        if (gx == _refGridX && gy == _refGridY && _cosineTile.HasBitmap)
        {
            return;
        }

        _refGridX = gx;
        _refGridY = gy;
        var cos = PatchHeatmap.ComputeRefCosine(_patches, gx, gy);
        var decoded = HeatmapBitmapBuilder.BuildViridis(cos, AnalysisComputer.PatchGridPixels, AnalysisComputer.PatchGridPixels);
        try
        {
            _cosineTile.SetBitmap(ToSourceUpscaled(decoded, _aspect), null);
        }
        finally
        {
            decoded.Dispose();
        }

        _cosineTile.SetLabel(FormatCosineLabel(gx, gy));
    }

    /// <summary>把准星同步到全部诊断瓦片(不传坐标即清空)。</summary>
    private void ApplyCrosshair(double? u = null, double? v = null)
    {
        foreach (var tile in new[] { _histogramTile, _pcaTile, _cosineTile, _sharpnessTile, _shakeTile })
        {
            tile.SetCrosshair(u, v);
        }
    }

    /// <summary>空白处点击:清空准星。</summary>
    private void ClearCrosshair() => ApplyCrosshair();

    /// <summary>细节预览联动:悬停/离开时向主图推送绿框(uv 矩形)。</summary>
    private void Highlight(Rect? uv) => _setHighlight(_main.IsAnalysisViewVisible ? uv : null);

    /// <summary>同步清空全部诊断瓦片(面板隐藏或无图)。</summary>
    private void ClearDiagnosticsSync()
    {
        _patches = null;
        _refGridX = AnalysisComputer.PatchGridPixels / 2;
        _refGridY = AnalysisComputer.PatchGridPixels / 2;
        _cosineTile.SetLabel(FormatCosineLabel(_refGridX, _refGridY));
        _shakeTile.SetLabel("抖动拖影");
        SetDiagnosticsPlaceholder("未提取");
        _setHighlight(null);
    }

    /// <summary>cache entry 是否为空(未提取)。</summary>
    private static bool IsEntryEmpty(AnalysisResultCache.Entry entry) => entry.Patches == null && entry.SharpnessBmp == null;

    /// <summary>把 8 位派生位图放大到最近邻可读尺寸后交 XAML(诊断网格只有 32×32)。</summary>
    /// <param name="image">派生位图(方形网格)</param>
    /// <param name="aspect">主图纵横比(宽/高);诊断网格按它铺开,瓦片里才与原片比例一致</param>
    /// <returns>XAML 位图;源为空时为 null</returns>
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
    /// <param name="aspect">主图纵横比(宽/高)</param>
    /// <param name="longEdge">长边像素</param>
    /// <param name="width">输出宽</param>
    /// <param name="height">输出高</param>
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

    /// <summary>同步算指纹(EXIF 已加载时可用)。</summary>
    private static string? TryComputeFingerprintSync(ImageFile file)
    {
        var exif = file.ExifData;
        if (!file.ModifiedDate.HasValue && exif?.DateTimeOriginal == null) return null;
        var input = PhotoFingerprint.BuildInput(file.Name, exif, file.ModifiedDate?.UtcDateTime);
        return input.CaptureTime.HasValue ? PhotoFingerprint.Compute(input) : null;
    }

    /// <summary>cosine 角标格式(旧壳同款:省略"参考点"前缀)。</summary>
    private static string FormatCosineLabel(int x, int y) => $"Cosine {x},{y}";

    /// <summary>把会话 F16 帧降采样成 sRGB8 BGRA(只处理目标尺寸像素),供直方图统计。</summary>
    private static unsafe DecodedImage ToSrgb8Downscaled(DecodedImage source, int longEdge)
    {
        int width = source.Width, height = source.Height;
        double factor = Math.Min(1.0, longEdge / (double)Math.Max(width, height));
        int targetWidth = Math.Max(1, (int)Math.Round(width * factor));
        int targetHeight = Math.Max(1, (int)Math.Round(height * factor));

        using var target = new SKBitmap(new SKImageInfo(targetWidth, targetHeight, SKColorType.Bgra8888, SKAlphaType.Opaque, SKColorSpace.CreateSrgb()));
        var info = new SKImageInfo(width, height, SKColorType.RgbaF16, SKAlphaType.Opaque, SKColorSpace.CreateSrgbLinear());
        fixed (byte* sourcePixels = source.Pixels)
        {
            using var image = SKImage.FromPixels(info, (IntPtr)sourcePixels, source.Stride)
                ?? throw new InvalidOperationException("分析像素无法包装为 Skia 图像");
            using var canvas = new SKCanvas(target);
            canvas.Clear(SKColors.Black);
            canvas.DrawImage(image, SKRect.Create(0, 0, width, height), SKRect.Create(0, 0, targetWidth, targetHeight),
                new SKSamplingOptions(SKCubicResampler.Mitchell), new SKPaint());
        }

        var stride = targetWidth * 4;
        var pixels = new byte[stride * targetHeight];
        Marshal.Copy(target.GetPixels(), pixels, 0, pixels.Length);
        return new DecodedImage(pixels, targetWidth, targetHeight, stride, PixelEncoding.Bgra32, ImageColorSpace.Srgb);
    }

    /// <summary>把抖动矢量场光栅化成箭头位图(颜色/方向/边宽口径同旧壳自绘)。</summary>
    /// <param name="field">抖动矢量场</param>
    /// <param name="aspect">主图纵横比(宽/高);网格按它铺开,箭头不被压成正方</param>
    /// <returns>XAML 位图</returns>
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
        private DecodedImage? _source;
        private WriteableBitmap? _bitmap;
        private double _centerU = 0.5;
        private double _centerV = 0.5;
        private Rect? _frameRect;
        private int _cropPixels = 300;
        private bool _isHovered;

        /// <summary>
        /// 创建细节预览瓦片。
        /// </summary>
        /// <param name="label">药丸标签文字</param>
        /// <param name="focus">双击回调(对准该图像点)</param>
        /// <param name="highlight">悬停联动回调(uv 矩形)</param>
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

        /// <summary>设置中心与可选的对焦框(uv)。</summary>
        /// <param name="u">中心 u</param>
        /// <param name="v">中心 v</param>
        /// <param name="frame">对焦框 uv 矩形;null 表示联动裁剪区</param>
        public void SetCenter(double u, double v, Rect? frame)
        {
            _centerU = u;
            _centerV = v;
            _frameRect = frame;
            PushHighlight();
        }

        /// <summary>沿用当前中心与裁剪尺寸重新裁剪(换图 / 代理→Full / 瓦片尺寸变化)。</summary>
        /// <param name="image">会话帧</param>
        /// <param name="cropPixels">裁剪边长(源像素)</param>
        public void Rebuild(DecodedImage image, int cropPixels)
        {
            SetImage(image, _centerU, _centerV, cropPixels, _frameRect);
        }

        /// <summary>裁剪并显示:方形取源像素中心区域,输出到瓦片物理像素。</summary>
        /// <param name="image">会话帧(线性 F16,未旋转)</param>
        /// <param name="u">中心 u</param>
        /// <param name="v">中心 v</param>
        /// <param name="cropPixels">裁剪边长(源像素)</param>
        /// <param name="frame">对焦框(uv)</param>
        public unsafe void SetImage(DecodedImage image, double u, double v, int cropPixels, Rect? frame)
        {
            _centerU = u;
            _centerV = v;
            _frameRect = frame;
            _cropPixels = cropPixels;

            if (!image.TryAcquire())
            {
                Clear();
                return;
            }

            try
            {
                int crop = Math.Min(cropPixels, Math.Min(image.Width, image.Height));
                if (crop <= 0)
                {
                    Clear();
                    return;
                }

                int centerX = (int)Math.Clamp(Math.Round(_centerU * image.Width), 0, image.Width);
                int centerY = (int)Math.Clamp(Math.Round(_centerV * image.Height), 0, image.Height);
                int left = Math.Clamp(centerX - (crop / 2), 0, Math.Max(0, image.Width - crop));
                int top = Math.Clamp(centerY - (crop / 2), 0, Math.Max(0, image.Height - crop));

                double raster = XamlRoot?.RasterizationScale ?? 1;
                int output = (int)Math.Round((ActualWidth > 0 ? ActualWidth : cropPixels) * raster);
                output = Math.Max(1, output);
                var info = new SKImageInfo(image.Width, image.Height, SKColorType.RgbaF16, SKAlphaType.Opaque, SKColorSpace.CreateSrgbLinear());
                using var target = new SKBitmap(new SKImageInfo(output, output, SKColorType.Bgra8888, SKAlphaType.Opaque, SKColorSpace.CreateSrgb()));
                fixed (byte* sourcePixels = image.Pixels)
                {
                    using var source = SKImage.FromPixels(info, (IntPtr)sourcePixels, image.Stride)
                        ?? throw new InvalidOperationException("细节预览源帧无法包装为 Skia 图像");
                    using var canvas = new SKCanvas(target);
                    canvas.Clear(SKColors.Black);
                    canvas.DrawImage(source, SKRect.Create(left, top, crop, crop), SKRect.Create(0, 0, output, output),
                        new SKSamplingOptions(SKCubicResampler.Mitchell), new SKPaint());
                }

                _bitmap = WindowsImageAdapter.ToSource(output, output, target.GetPixelSpan());
                _image.Source = _bitmap;
                _source = image;
                PushHighlight();
            }
            finally
            {
                image.Release();
            }
        }

        /// <summary>清空显示(无帧时保留 #222 底色,与旧壳同口径)。</summary>
        public void Clear()
        {
            _image.Source = null;
            _bitmap = null;
            _source = null;
            if (_isHovered)
            {
                _highlight(null);
            }
        }

        /// <summary>释放自持位图。</summary>
        public void Dispose()
        {
            _image.Source = null;
            _bitmap = null;
            _source = null;
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

            if (_source == null)
            {
                _highlight(null);
                return;
            }

            if (_frameRect is { } frame)
            {
                _highlight(frame);
                return;
            }

            int crop = Math.Min(_cropPixels, Math.Min(_source.Width, _source.Height));
            double widthUv = Math.Min(1, crop / (double)Math.Max(1, _source.Width));
            double heightUv = Math.Min(1, crop / (double)Math.Max(1, _source.Height));
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
        /// <param name="label">药丸标签文字</param>
        /// <param name="clicked">内容区点击回调(uv);null 表示不响应</param>
        /// <param name="backgroundClicked">瓦片空白处点击回调</param>
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
        /// <param name="aspect">宽高比</param>
        public void SetAspect(double aspect)
        {
            _aspect = aspect > 0 ? aspect : 1;
            RenderCrosshair();
        }

        /// <summary>设置位图与占位文字。</summary>
        /// <param name="bitmap">位图(可为 null)</param>
        /// <param name="placeholder">占位文字</param>
        public void SetBitmap(WriteableBitmap? bitmap, string? placeholder)
        {
            _image.Source = bitmap;
            _placeholder.Text = placeholder ?? string.Empty;
            _placeholder.Visibility = bitmap == null && !string.IsNullOrEmpty(placeholder)
                ? Visibility.Visible
                : Visibility.Collapsed;
        }

        /// <summary>设置药丸标签文字。</summary>
        /// <param name="label">标签</param>
        public void SetLabel(string label) => _label.Text = label;

        /// <summary>设置准星位置(uv;null 清空)。</summary>
        /// <param name="u">横向 0-1</param>
        /// <param name="v">纵向 0-1</param>
        public void SetCrosshair(double? u, double? v)
        {
            _crosshairU = u;
            _crosshairV = v;
            RenderCrosshair();
        }

        /// <summary>释放位图引用。</summary>
        public void Dispose() => _image.Source = null;

        /// <summary>内容(位图)在内容区坐标系里的实际矩形:按纵横比 letterbox 居中。</summary>
        /// <returns>位图矩形;尺寸未就绪时为空矩形</returns>
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
