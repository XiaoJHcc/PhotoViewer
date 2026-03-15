using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using PhotoViewer.ViewModels;

namespace PhotoViewer.Controls;

/// <summary>
/// 管理同组 <see cref="DetailPreview"/> 的互斥高亮（绿框）状态。
/// 确保同一时刻至多一个预览组件处于高亮状态：
/// 点击新组件时，自动清除上一个高亮；再次点击同一组件则取消高亮。
/// </summary>
public class DetailPreviewHighlightGroup
{
    private DetailPreview? _current;

    /// <summary>
    /// 请求切换指定预览组件的高亮状态。
    /// 若该组件已高亮则取消；否则高亮该组件并清除上一个高亮。
    /// </summary>
    /// <param name="preview">发起请求的预览组件。</param>
    internal void RequestToggle(DetailPreview preview)
    {
        if (_current == preview)
        {
            _current = null;
            preview.SetHighlighted(false);
        }
        else
        {
            var previous = _current;
            _current = preview;
            previous?.SetHighlighted(false);
            preview.SetHighlighted(true);
        }
    }
}

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

    /// <summary>
    /// 互斥高亮分组。设置后，同组内的预览组件在触摸点击时只允许一个显示高亮绿框。
    /// </summary>
    public static readonly StyledProperty<DetailPreviewHighlightGroup?> HighlightGroupProperty =
        AvaloniaProperty.Register<DetailPreview, DetailPreviewHighlightGroup?>(nameof(HighlightGroup));

    /// <summary>
    /// 获取或设置所属的互斥高亮分组。
    /// </summary>
    public DetailPreviewHighlightGroup? HighlightGroup
    {
        get => GetValue(HighlightGroupProperty);
        set => SetValue(HighlightGroupProperty, value);
    }

    private static readonly IBrush HighlightBrush = new SolidColorBrush(Color.FromRgb(0, 255, 0));
    private static readonly IBrush DefaultBorderBrush = new SolidColorBrush(Color.FromRgb(68, 68, 68));

    private Image? _previewImage;
    private Border? _rootBorder;
    private bool _updateQueued;
    private bool _isHighlighted;
    private bool _wasHighlightedBeforeDeactivate; // 记录隐藏细节栏前的高亮状态，以便重新显示时恢复
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
            // 细节栏隐藏：保存高亮状态后清除视觉效果（组的 _current 指针保持不变）
            _wasHighlightedBeforeDeactivate = _isHighlighted;
            SetHighlighted(false);
            return;
        }

        // 细节栏重新显示：先触发预览更新（重新计算 _lastImageRect）
        QueueUpdatePreview();

        // 恢复之前的高亮状态，使绿框和主照片标记一并重现
        if (_wasHighlightedBeforeDeactivate)
        {
            _wasHighlightedBeforeDeactivate = false;
            SetHighlighted(true);
        }
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

        // 触摸点击时通过互斥分组协调高亮状态，确保同一时刻只有一个预览显示绿框
        if (HighlightGroup != null)
        {
            HighlightGroup.RequestToggle(this);
        }
        else
        {
            SetHighlighted(!_isHighlighted);
        }
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

    /// <summary>
    /// 设置当前组件的高亮状态，更新边框颜色并同步到 <see cref="HighlightTarget"/>。
    /// </summary>
    /// <param name="isHighlighted">是否高亮。</param>
    internal void SetHighlighted(bool isHighlighted)
    {
        var wasHighlighted = _isHighlighted;
        _isHighlighted = isHighlighted;
        UpdateBorderHighlight();

        // 由高亮切换为非高亮时，明确清除主照片绿框标记
        if (wasHighlighted && !isHighlighted)
        {
            HighlightTarget?.SetDetailHighlight(null);
            return;
        }

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
        if (HighlightTarget == null || !_isHighlighted)
        {
            // 非高亮状态下不操作 HighlightTarget，避免覆盖其他预览组件的高亮设置
            return;
        }

        if (!IsActive || _lastImageRect == null)
        {
            HighlightTarget.SetDetailHighlight(null);
            return;
        }

        HighlightTarget.SetDetailHighlight(_lastImageRect);
    }
}

