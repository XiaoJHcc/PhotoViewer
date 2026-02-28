using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Threading;

namespace PhotoViewer.Controls;

public partial class DetailPreview : UserControl
{
    public static readonly StyledProperty<Bitmap?> SourceProperty =
        AvaloniaProperty.Register<DetailPreview, Bitmap?>(nameof(Source));

    public Bitmap? Source
    {
        get => GetValue(SourceProperty);
        set => SetValue(SourceProperty, value);
    }

    public static readonly StyledProperty<Point> CenterProperty =
        AvaloniaProperty.Register<DetailPreview, Point>(nameof(Center), new Point(0.5, 0.5));

    public Point Center
    {
        get => GetValue(CenterProperty);
        set => SetValue(CenterProperty, value);
    }

    public static readonly StyledProperty<string> LabelProperty =
        AvaloniaProperty.Register<DetailPreview, string>(nameof(Label), string.Empty);

    public string Label
    {
        get => GetValue(LabelProperty);
        set => SetValue(LabelProperty, value);
    }

    public static readonly StyledProperty<int> CropSizeProperty =
        AvaloniaProperty.Register<DetailPreview, int>(nameof(CropSize), 300);

    public int CropSize
    {
        get => GetValue(CropSizeProperty);
        set => SetValue(CropSizeProperty, value);
    }

    private Image? _previewImage;
    private bool _updateQueued;

    static DetailPreview()
    {
        SourceProperty.Changed.AddClassHandler<DetailPreview>((x, _) => x.QueueUpdatePreview());
        CenterProperty.Changed.AddClassHandler<DetailPreview>((x, _) => x.QueueUpdatePreview());
        CropSizeProperty.Changed.AddClassHandler<DetailPreview>((x, _) => x.QueueUpdatePreview());
    }

    public DetailPreview()
    {
        InitializeComponent();
        _previewImage = this.FindControl<Image>("PreviewImage");
        AttachedToVisualTree += (_, _) => QueueUpdatePreview();
        QueueUpdatePreview();
    }

    private void QueueUpdatePreview()
    {
        if (_updateQueued)
        {
            return;
        }

        _updateQueued = true;
        Dispatcher.UIThread.Post(() =>
        {
            _updateQueued = false;
            UpdatePreview();
        }, DispatcherPriority.Render);
    }

    private void UpdatePreview()
    {
        if (_previewImage == null)
        {
            return;
        }

        if (Source == null)
        {
            _previewImage.IsVisible = false;
            _previewImage.Source = null;
            return;
        }

        var pixelWidth = Source.PixelSize.Width;
        var pixelHeight = Source.PixelSize.Height;
        if (pixelWidth <= 0 || pixelHeight <= 0)
        {
            _previewImage.IsVisible = false;
            _previewImage.Source = null;
            return;
        }

        var desiredSize = Math.Max(1, CropSize);
        var cropWidth = Math.Min(desiredSize, pixelWidth);
        var cropHeight = Math.Min(desiredSize, pixelHeight);

        var centerX = Math.Clamp(Center.X, 0, 1) * pixelWidth;
        var centerY = Math.Clamp(Center.Y, 0, 1) * pixelHeight;

        var left = (int)Math.Round(centerX - cropWidth / 2.0);
        var top = (int)Math.Round(centerY - cropHeight / 2.0);

        left = Math.Clamp(left, 0, Math.Max(0, pixelWidth - cropWidth));
        top = Math.Clamp(top, 0, Math.Max(0, pixelHeight - cropHeight));

        var rect = new PixelRect(left, top, cropWidth, cropHeight);
        _previewImage.Source = new CroppedBitmap(Source, rect);
        _previewImage.IsVisible = true;
    }
}
