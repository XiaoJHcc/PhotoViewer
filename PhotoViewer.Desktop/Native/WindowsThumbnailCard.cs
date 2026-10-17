using System;
using System.ComponentModel;
using System.Threading.Tasks;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using PhotoViewer.Core.Image;
using PhotoViewer.Core.Platform;
using Windows.UI;

namespace PhotoViewer.Desktop.Native;

/// <summary>
/// 胶片条卡片:照片 + 文件名 + 第二行 + 0-5 星 + 抖动徽标(契约 FILM-05)。
/// 照片区保持正方形,图片等比适应留边,容器不被拉成矩形。
/// </summary>
internal sealed class WindowsThumbnailCard : Grid
{
    private readonly Border _surface;
    private readonly Border _photoWell;
    private readonly Border _photoFrame;
    private readonly Image _photo;
    private readonly Border _loading;
    private readonly TextBlock _placeholder;
    private readonly Border _shakeBadge;
    private readonly TextBlock _name;
    private readonly TextBlock _secondLine;
    private readonly Border _secondLineTrack;
    private readonly Border _secondLineFill;
    private readonly StackPanel _stars;
    private readonly Button[] _starButtons = new Button[6];
    private readonly Action<ImageFile> _select;
    private readonly Func<ImageFile, int, Task> _rate;

    private ImageFile? _file;
    private bool _showRating = true;
    private bool _highlightMainImage;
    private string? _secondLineText;
    private double? _secondLineProgress;
    private DecodedImage? _lastThumbnail;

    /// <summary>
    /// 创建卡片。
    /// </summary>
    /// <param name="select">选中回调</param>
    /// <param name="rate">评分回调</param>
    public WindowsThumbnailCard(Action<ImageFile> select, Func<ImageFile, int, Task> rate)
    {
        _select = select;
        _rate = rate;

        // 96×144 = 旧壳卡片 90×138 加四周 3 占位;这里整格就是占位后的尺寸,
        // 不能再叠一层 Margin(旧壳的 3 是 Button.Margin,与卡片外框的 90 相加才是 96)。
        Width = WindowsLayout.CardPitchHorizontal;
        Height = WindowsLayout.CardPitchVertical;

        var content = new StackPanel
        {
            Margin = new Thickness(0, 3, 0, 0),
            Spacing = 4,
        };

        _photo = new Image
        {
            Stretch = Stretch.Uniform,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        // 加载态用静态细条而不是 ProgressBar:WinUI 的 ProgressBar 放进虚拟化卡片会让布局测量
        // 抛 E_FAIL(0x80004005)整窗崩溃,且每张卡片一份动画对上百张缩略图也是纯开销。
        _loading = new Border
        {
            Width = 60,
            Height = 4,
            Background = new SolidColorBrush(Color.FromArgb(0x60, 0xFF, 0xFF, 0xFF)),
            CornerRadius = new CornerRadius(2),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Visibility = Visibility.Collapsed,
        };
        _placeholder = new TextBlock
        {
            Text = "📷",
            FontSize = 24,
            Foreground = new SolidColorBrush(Color.FromArgb(0xFF, 0x66, 0x66, 0x66)),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        _shakeBadge = new Border
        {
            Width = WindowsLayout.ShakeBadgeSize,
            Height = WindowsLayout.ShakeBadgeSize,
            Background = new SolidColorBrush(WindowsLayout.ShakeBadgeBackground),
            CornerRadius = new CornerRadius(2),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(0, 0, 2, 2),
            Visibility = Visibility.Collapsed,
            Child = new TextBlock
            {
                Text = "抖",
                FontSize = WindowsLayout.ShakeBadgeFontSize,
                FontWeight = FontWeights.Bold,
                Foreground = new SolidColorBrush(Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF)),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
        };

        // 缩略图槽的尺寸就是照片的可见边界:Image 不设尺寸时会按位图自然大小铺开(实测 83×58 DIP),
        // 撑出 80×80 的槽位。给容器定死尺寸,Image 与槽、抖动徽标共用同一坐标系。
        var photoHost = new Grid
        {
            Width = WindowsLayout.ThumbnailSize,
            Height = WindowsLayout.ThumbnailSize,
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        _photoWell = new Border
        {
            Background = new SolidColorBrush(WindowsLayout.ThumbnailWell),
            CornerRadius = new CornerRadius(WindowsLayout.ThumbnailCornerRadius),
        };
        // 描边单独一层压在图片之上:缩略图按 Uniform 铺满 80 宽,画在槽位边框里的描边四边会被图片盖掉(FILM-05)。
        _photoFrame = new Border
        {
            CornerRadius = new CornerRadius(WindowsLayout.ThumbnailCornerRadius),
            BorderThickness = new Thickness(1),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0, 0, 0, 0)),
            IsHitTestVisible = false,
        };
        photoHost.Children.Add(_photoWell);
        photoHost.Children.Add(_photo);
        photoHost.Children.Add(_photoFrame);
        photoHost.Children.Add(_loading);
        photoHost.Children.Add(_placeholder);
        photoHost.Children.Add(_shakeBadge);
        content.Children.Add(photoHost);

        _name = new TextBlock
        {
            FontSize = WindowsLayout.CardNameFontSize,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(WindowsLayout.CardNameForeground),
            TextTrimming = TextTrimming.CharacterEllipsis,
            TextAlignment = TextAlignment.Center,
            MaxWidth = WindowsLayout.CardWidth,
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        content.Children.Add(_name);

        _secondLine = new TextBlock
        {
            Width = WindowsLayout.CardSecondLineWidth,
            FontSize = WindowsLayout.CardSecondLineFontSize,
            FontWeight = FontWeights.Bold,
            Foreground = new SolidColorBrush(WindowsLayout.CardNameForeground),
            TextAlignment = TextAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        // 相似聚类用:第二行文本叠在 80 宽的柱状进度条上(Excel 风格);主缩略图列表不显示条。
        _secondLineTrack = new Border
        {
            Background = new SolidColorBrush(WindowsLayout.SecondLineTrack),
            CornerRadius = new CornerRadius(4),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            Visibility = Visibility.Collapsed,
        };
        _secondLineFill = new Border
        {
            Background = new SolidColorBrush(WindowsLayout.SecondLineFill),
            CornerRadius = new CornerRadius(4),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Stretch,
            Width = 0,
            Visibility = Visibility.Collapsed,
        };
        var secondLineHost = new Grid
        {
            Width = WindowsLayout.CardSecondLineWidth,
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        secondLineHost.Children.Add(_secondLineTrack);
        secondLineHost.Children.Add(_secondLineFill);
        secondLineHost.Children.Add(_secondLine);
        content.Children.Add(secondLineHost);

        _stars = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 0,
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        for (var star = 0; star <= 5; star++)
        {
            var value = star;
            var button = new Button
            {
                Width = WindowsLayout.CardStarSize,
                Height = WindowsLayout.CardStarSize,
                Padding = new Thickness(0),
                Background = new SolidColorBrush(Color.FromArgb(0, 0, 0, 0)),
                BorderThickness = new Thickness(0),
            };
            // 星级点击只改评分,不冒泡成"切换主图"(FILM-05)
            button.PointerPressed += (_, e) => e.Handled = true;
            button.Click += (_, _) => RateAsync(value);
            _starButtons[star] = button;
            _stars.Children.Add(button);
        }

        content.Children.Add(_stars);

        _surface = new Border
        {
            Width = WindowsLayout.CardWidth,
            Height = WindowsLayout.CardHeight,
            Background = new SolidColorBrush(WindowsLayout.BlockBackground),
            CornerRadius = new CornerRadius(WindowsLayout.CardCornerRadius),
            BorderThickness = new Thickness(WindowsLayout.CardBorderThickness),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0, 0, 0, 0)),
            Child = content,
        };

        Children.Add(_surface);
        PointerPressed += OnCardPressed;
    }

    /// <summary>
    /// 绑定数据并刷新。
    /// </summary>
    /// <param name="file">文件</param>
    /// <param name="showRating">是否显示星级</param>
    /// <param name="secondLineText">第二行文本(默认显示拍摄时间;相似聚类传分数)</param>
    /// <param name="secondLineProgress">第二行柱状进度 0-1;null 不显示柱条</param>
    /// <param name="highlightMainImage">选中描边是否按"当前主图"而不是"主列表锚点"</param>
    public void Bind(ImageFile file, bool showRating, string? secondLineText = null, double? secondLineProgress = null,
        bool highlightMainImage = false)
    {
        if (!ReferenceEquals(_file, file))
        {
            if (_file != null)
            {
                _file.PropertyChanged -= OnFileChanged;
            }

            _file = file;
            _file.PropertyChanged += OnFileChanged;
            _lastThumbnail = null;
        }

        _showRating = showRating;
        _secondLineText = secondLineText;
        _secondLineProgress = secondLineProgress;
        _highlightMainImage = highlightMainImage;
        Refresh();
    }

    /// <summary>
    /// 解绑(回收时调用)。
    /// </summary>
    public void Unbind()
    {
        if (_file != null)
        {
            _file.PropertyChanged -= OnFileChanged;
            _file = null;
        }

        _photo.Source = null;
        _lastThumbnail = null;
    }

    /// <summary>刷新卡片全部状态。</summary>
    private void Refresh()
    {
        if (_file == null)
        {
            return;
        }

        var file = _file;
        _name.Text = file.DisplayName;
        ToolTipService.SetToolTip(_name, file.DisplayName);
        var secondLine = _secondLineText ?? file.PhotoDate?.ToString("MM-dd HH:mm") ?? string.Empty;
        _secondLine.Text = secondLine;
        ToolTipService.SetToolTip(_secondLine, _secondLineText == null ? file.PhotoDate?.ToString("yyyy-MM-dd HH:mm:ss") : null);
        bool hasProgress = _secondLineProgress.HasValue;
        _secondLineTrack.Visibility = hasProgress ? Visibility.Visible : Visibility.Collapsed;
        _secondLineFill.Visibility = hasProgress ? Visibility.Visible : Visibility.Collapsed;
        _secondLineFill.Width = hasProgress
            ? Math.Clamp(_secondLineProgress!.Value, 0, 1) * WindowsLayout.CardSecondLineWidth
            : 0;

        var isCurrent = _highlightMainImage ? file.IsCurrentImage : file.IsCurrent;
        _surface.BorderBrush = new SolidColorBrush(isCurrent
            ? WindowsLayout.CurrentBorder
            : Color.FromArgb(0, 0, 0, 0));
        _photoFrame.BorderBrush = new SolidColorBrush(file.IsInCache
            ? WindowsLayout.CachedBorder
            : Color.FromArgb(0, 0, 0, 0));

        _shakeBadge.Visibility = file.IsShake == true ? Visibility.Visible : Visibility.Collapsed;
        _loading.Visibility = file.IsThumbnailLoading && file.Thumbnail == null
            ? Visibility.Visible
            : Visibility.Collapsed;

        var thumbnail = file.Thumbnail;
        if (!ReferenceEquals(thumbnail, _lastThumbnail))
        {
            _lastThumbnail = thumbnail;
            _photo.Source = ConvertThumbnail(thumbnail);
        }

        _placeholder.Visibility = file.Thumbnail == null && !file.IsThumbnailLoading
            ? Visibility.Visible
            : Visibility.Collapsed;

        _stars.Visibility = _showRating ? Visibility.Visible : Visibility.Collapsed;
        var rating = file.Rating;
        for (var star = 0; star <= 5; star++)
        {
            var on = star > 0 && rating >= star;
            var glyph = star == 0 ? WindowsIcons.ClearGlyph : on ? WindowsIcons.StarGlyph : "\uE734";
            var size = star == 0 ? 8 : WindowsLayout.CardStarFontSize;
            _starButtons[star].Content = new TextBlock
            {
                Text = glyph,
                FontFamily = new FontFamily(WindowsIcons.FluentFontFamily),
                FontSize = size,
                Foreground = new SolidColorBrush(star == 0
                    ? WindowsLayout.StarClear
                    : on ? WindowsLayout.StarOn : WindowsLayout.StarOff),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
        }
    }

    /// <summary>把缩略图转成 XAML 位图(读取期间持租约,避免与缓存淘汰竞态)。</summary>
    private static ImageSource? ConvertThumbnail(DecodedImage? thumbnail)
    {
        if (thumbnail == null)
        {
            return null;
        }

        if (!thumbnail.TryAcquire())
        {
            return null;
        }

        try
        {
            return WindowsImageAdapter.ToSource(thumbnail);
        }
        finally
        {
            thumbnail.Release();
        }
    }

    /// <summary>点击卡片切换主图(星级点击已在按钮上标记 Handled)。</summary>
    private void OnCardPressed(object sender, PointerRoutedEventArgs e)
    {
        if (_file != null)
        {
            _select(_file);
        }
    }

    /// <summary>评分并刷新。</summary>
    private async void RateAsync(int rating)
    {
        if (_file == null)
        {
            return;
        }

        try
        {
            await _rate(_file, rating);
        }
        catch (Exception ex)
        {
            NativeStartupLog.Fail($"胶片条评分失败({_file.Name})", ex);
        }
    }

    /// <summary>文件属性变化:一轮内合并刷新。</summary>
    private void OnFileChanged(object? sender, PropertyChangedEventArgs e)
    {
        var changed = sender as ImageFile;
        if (changed == null || !ReferenceEquals(changed, _file))
        {
            return;
        }

        UiDispatcher.Post(() =>
        {
            if (ReferenceEquals(changed, _file))
            {
                Refresh();
            }
        }, UiPriority.Background);
    }
}
