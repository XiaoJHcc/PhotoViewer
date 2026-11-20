using System;
using System.Numerics;
using Microsoft.Graphics.Canvas.Effects;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Hosting;
using Windows.Graphics.Effects;
using Windows.UI;

namespace PhotoViewer.Windows.Native;

/// <summary>SHELL-02：在 XAML 内容之上合成真实标题栏背板，标题文字和按钮不进入取样树。</summary>
internal sealed class WindowsTitleBarBackdrop : IDisposable
{
    private readonly UIElement _photoSourceHost;
    private readonly UIElement _backdropHost;
    private readonly CompositionVisualSurface _visualSurface;
    private readonly CompositionSurfaceBrush _photoBrush;
    private readonly CompositionSurfaceBrush _captureBrush;
    private readonly CompositionEffectBrush _acrylic;
    private readonly CompositionColorBrush _luminosityBrush;
    private readonly CompositionColorBrush _tintBrush;
    private readonly SpriteVisual _photoVisual;
    private readonly SpriteVisual _backdropVisual;
    private readonly SpriteVisual _luminosityVisual;
    private readonly SpriteVisual _tintVisual;
    private readonly ContainerVisual _backdropContainer;

    public WindowsTitleBarBackdrop(UIElement content, UIElement photoSourceHost, UIElement backdropHost,
        AcrylicBackdropRecipe themeRecipe)
    {
        _photoSourceHost = photoSourceHost;
        _backdropHost = backdropHost;
        var contentVisual = ElementCompositionPreview.GetElementVisual(content);
        Compositor = contentVisual.Compositor;
        using var factory = Compositor.CreateEffectFactory(BuildEffect());
        _acrylic = factory.CreateBrush();
        _visualSurface = Compositor.CreateVisualSurface();
        _visualSurface.SourceVisual = contentVisual;
        _captureBrush = Compositor.CreateSurfaceBrush(_visualSurface);
        _captureBrush.Stretch = CompositionStretch.None;
        _captureBrush.HorizontalAlignmentRatio = 0;
        _captureBrush.VerticalAlignmentRatio = 0;
        _acrylic.SetSourceParameter("content", _captureBrush);
        _photoBrush = Compositor.CreateSurfaceBrush();
        _photoBrush.Stretch = CompositionStretch.Fill;
        _photoVisual = Compositor.CreateSpriteVisual();
        _photoVisual.Brush = _photoBrush;
        _photoVisual.IsVisible = false;
        _backdropVisual = Compositor.CreateSpriteVisual();
        _backdropVisual.Brush = _acrylic;
        _luminosityBrush = Compositor.CreateColorBrush(ToColor(1, WindowsTitleBar.LuminosityTint,
            WindowsTitleBar.LuminosityTint, WindowsTitleBar.LuminosityTint));
        _tintBrush = Compositor.CreateColorBrush(ToColor(1, themeRecipe.Tint.R, themeRecipe.Tint.G, themeRecipe.Tint.B));
        _luminosityVisual = Compositor.CreateSpriteVisual();
        _luminosityVisual.Brush = _luminosityBrush;
        _luminosityVisual.Opacity = WindowsTitleBar.LuminosityOpacity;
        _tintVisual = Compositor.CreateSpriteVisual();
        _tintVisual.Brush = _tintBrush;
        _tintVisual.Opacity = themeRecipe.TintOpacity;
        _backdropContainer = Compositor.CreateContainerVisual();
        _backdropContainer.Children.InsertAtTop(_backdropVisual);
        _backdropContainer.Children.InsertAtTop(_luminosityVisual);
        _backdropContainer.Children.InsertAtTop(_tintVisual);
        _backdropVisual.IsVisible = false;
        _luminosityVisual.IsVisible = false;
        _tintVisual.IsVisible = false;
        ElementCompositionPreview.SetElementChildVisual(_photoSourceHost, _photoVisual);
        ElementCompositionPreview.SetElementChildVisual(_backdropHost, _backdropContainer);
    }

    public Compositor Compositor { get; }

    public bool IsVisible => _backdropVisual.IsVisible;

    public bool SetSource(ICompositionSurface surface, float width, float sampleHeight, float barHeight)
    {
        // 尺寸变化会换新取样表面，需要马上提交一帧，不等下一次照片操作。
        var changed = _photoBrush.Surface != surface;
        if (changed) _photoBrush.Surface = surface;
        var sampleSize = new Vector2(width, sampleHeight);
        if (_photoVisual.Size != sampleSize) _photoVisual.Size = sampleSize;
        if (_visualSurface.SourceSize != sampleSize) _visualSurface.SourceSize = sampleSize;
        var barSize = new Vector2(width, barHeight);
        if (_backdropVisual.Size != barSize) _backdropVisual.Size = barSize;
        if (_luminosityVisual.Size != barSize) _luminosityVisual.Size = barSize;
        if (_tintVisual.Size != barSize) _tintVisual.Size = barSize;
        if (!_photoVisual.IsVisible) _photoVisual.IsVisible = true;
        if (!_backdropVisual.IsVisible) _backdropVisual.IsVisible = true;
        if (!_luminosityVisual.IsVisible) _luminosityVisual.IsVisible = true;
        if (!_tintVisual.IsVisible) _tintVisual.IsVisible = true;
        return changed;
    }

    public void Hide()
    {
        _backdropVisual.IsVisible = false;
        _luminosityVisual.IsVisible = false;
        _tintVisual.IsVisible = false;
        _photoVisual.IsVisible = false;
        _photoBrush.Surface = null;
        _visualSurface.SourceSize = Vector2.Zero;
    }

    public void Dispose()
    {
        Hide();
        ElementCompositionPreview.SetElementChildVisual(_backdropHost, null);
        ElementCompositionPreview.SetElementChildVisual(_photoSourceHost, null);
        _visualSurface.SourceVisual = null;
        _backdropContainer.Dispose();
        _tintVisual.Dispose();
        _luminosityVisual.Dispose();
        _backdropVisual.Dispose();
        _photoVisual.Dispose();
        _tintBrush.Dispose();
        _luminosityBrush.Dispose();
        _acrylic.Dispose();
        _captureBrush.Dispose();
        _photoBrush.Dispose();
        _visualSurface.Dispose();
    }

    private static IGraphicsEffect BuildEffect()
        => new GaussianBlurEffect
        {
            Source = new CompositionEffectSourceParameter("content"),
            BlurAmount = (float)WindowsLayout.TitleBarBackdropBlurAmount,
            BorderMode = EffectBorderMode.Hard,
            Optimization = EffectOptimization.Balanced,
        };

    private static Color ToColor(float a, float r, float g, float b)
        => Color.FromArgb(Byte(a), Byte(r), Byte(g), Byte(b));

    private static byte Byte(float value) => (byte)Math.Round(255 * value);
}
