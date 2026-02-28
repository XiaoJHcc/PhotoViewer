using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using PhotoViewer.ViewModels;

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

    public static readonly StyledProperty<bool> IsActiveProperty =
        AvaloniaProperty.Register<DetailPreview, bool>(nameof(IsActive), true);

    public bool IsActive
    {
        get => GetValue(IsActiveProperty);
        set => SetValue(IsActiveProperty, value);
    }

    public static readonly StyledProperty<ImageViewModel?> HighlightTargetProperty =
        AvaloniaProperty.Register<DetailPreview, ImageViewModel?>(nameof(HighlightTarget));

    public ImageViewModel? HighlightTarget
    {
        get => GetValue(HighlightTargetProperty);
        set => SetValue(HighlightTargetProperty, value);
    }

    private static readonly IBrush HighlightBrush = new SolidColorBrush(Color.FromRgb(0, 255, 0));
    private static readonly IBrush DefaultBorderBrush = new SolidColorBrush(Color.FromRgb(68, 68, 68));

    private Image? _previewImage;
    private Border? _rootBorder;
    private bool _updateQueued;
    private bool _isHighlighted;
    private Rect? _lastImageRect;

    static DetailPreview()
    {
        SourceProperty.Changed.AddClassHandler<DetailPreview>((x, _) => x.QueueUpdatePreview());
        CenterProperty.Changed.AddClassHandler<DetailPreview>((x, _) => x.QueueUpdatePreview());
        CropSizeProperty.Changed.AddClassHandler<DetailPreview>((x, _) => x.QueueUpdatePreview());
        IsActiveProperty.Changed.AddClassHandler<DetailPreview>((x, _) => x.OnActiveChanged());
    }

    public DetailPreview()
    {
        InitializeComponent();
        _previewImage = this.FindControl<Image>("PreviewImage");
        _rootBorder = this.FindControl<Border>("RootBorder");
        AttachedToVisualTree += (_, _) => QueueUpdatePreview();
        PointerEntered += OnPointerEntered;
        PointerExited += OnPointerExited;
        PointerPressed += OnPointerPressed;
        DoubleTapped += OnDoubleTapped;
        QueueUpdatePreview();
    }

    private void OnActiveChanged()
    {
        if (!IsActive)
        {
            SetHighlighted(false);
            return;
        }

        QueueUpdatePreview();
    }

    private void QueueUpdatePreview()
    {
        if (!IsActive || _updateQueued)
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
        if (!IsActive || _previewImage == null)
        {
            return;
        }

        if (Source == null)
        {
            _previewImage.IsVisible = false;
            _previewImage.Source = null;
            _lastImageRect = null;
            UpdateHighlightTarget();
            return;
        }

        var pixelWidth = Source.PixelSize.Width;
        var pixelHeight = Source.PixelSize.Height;
        if (pixelWidth <= 0 || pixelHeight <= 0)
        {
            _previewImage.IsVisible = false;
            _previewImage.Source = null;
            _lastImageRect = null;
            UpdateHighlightTarget();
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

        _lastImageRect = new Rect(left, top, cropWidth, cropHeight);

        var rect = new PixelRect(left, top, cropWidth, cropHeight);
        _previewImage.Source = new CroppedBitmap(Source, rect);
        _previewImage.IsVisible = true;
        UpdateHighlightTarget();
    }

    private void OnPointerEntered(object? sender, PointerEventArgs e)
    {
        if (!IsActive || e.Pointer.Type != PointerType.Mouse)
        {
            return;
        }

        SetHighlighted(true);
    }

    private void OnPointerExited(object? sender, PointerEventArgs e)
    {
        if (!IsActive || e.Pointer.Type != PointerType.Mouse)
        {
            return;
        }

        SetHighlighted(false);
    }

    private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!IsActive || e.Pointer.Type == PointerType.Mouse)
        {
            return;
        }

        SetHighlighted(!_isHighlighted);
        e.Handled = true;
    }

    private void OnDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (!IsActive || HighlightTarget == null || _lastImageRect == null)
        {
            return;
        }

        var center = _lastImageRect.Value.Center;
        HighlightTarget.FocusOnImagePoint(new Vector(center.X, center.Y), 1.0);
        e.Handled = true;
    }

    private void SetHighlighted(bool isHighlighted)
    {
        _isHighlighted = isHighlighted;
        UpdateBorderHighlight();
        UpdateHighlightTarget();
    }

    private void UpdateBorderHighlight()
    {
        if (_rootBorder == null)
        {
            return;
        }

        if (_isHighlighted)
        {
            _rootBorder.BorderBrush = HighlightBrush;;
        }
        else
        {
            _rootBorder.BorderBrush = DefaultBorderBrush;
        }
    }

    private void UpdateHighlightTarget()
    {
        if (HighlightTarget == null)
        {
            return;
        }

        if (!IsActive || !_isHighlighted || _lastImageRect == null)
        {
            HighlightTarget.SetDetailHighlight(null);
            return;
        }

        HighlightTarget.SetDetailHighlight(_lastImageRect);
    }
}

