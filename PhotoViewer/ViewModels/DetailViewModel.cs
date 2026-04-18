using System.Collections.ObjectModel;
using System.Reactive;
using System.Reactive.Linq;
using Avalonia;
using Avalonia.Media.Imaging;
using PhotoViewer.Core;
using ReactiveUI;

namespace PhotoViewer.ViewModels;

public sealed class DetailPreviewItem : ReactiveObject
{
    private string _label;
    public string Label
    {
        get => _label;
        set => this.RaiseAndSetIfChanged(ref _label, value);
    }

    private Point _center;
    public Point Center
    {
        get => _center;
        set => this.RaiseAndSetIfChanged(ref _center, value);
    }

    /// <summary>对焦框尺寸（原始图像像素），非 null 时在预览上叠加绿色对焦框。</summary>
    private Size? _focusFrame;
    public Size? FocusFrame
    {
        get => _focusFrame;
        set => this.RaiseAndSetIfChanged(ref _focusFrame, value);
    }

    public DetailPreviewItem(string label, Point center, Size? focusFrame = null)
    {
        _label = label;
        _center = center;
        _focusFrame = focusFrame;
    }
}

public class DetailViewModel : ReactiveObject
{
    private readonly MainViewModel _main;

    public bool IsVerticalLayout => _main.IsHorizontalLayout;

    public bool IsDetailViewVisible => _main.IsDetailViewVisible;

    public ImageViewModel ImageVM => _main.ImageVM;

    public Bitmap? SourceBitmap => _main.ImageVM.SourceBitmap;

    private double _previewSize = 300;
    public double PreviewSize
    {
        get => _previewSize;
        set => this.RaiseAndSetIfChanged(ref _previewSize, value);
    }

    private int _cropSize = 300;
    public int CropSize
    {
        get => _cropSize;
        set => this.RaiseAndSetIfChanged(ref _cropSize, value);
    }

    public ObservableCollection<DetailPreviewItem> Items { get; }

    public DetailViewModel(MainViewModel main)
    {
        _main = main;

        Items = new ObservableCollection<DetailPreviewItem>
        {
            new("中心", new Point(0.5, 0.5)),
            new("左上", new Point(0.25, 0.25)),
            new("右上", new Point(0.75, 0.25)),
            new("左下", new Point(0.25, 0.75)),
            new("右下", new Point(0.75, 0.75))
        };

        _main.WhenAnyValue(vm => vm.IsHorizontalLayout)
            .Subscribe(Observer.Create<bool>(_ => this.RaisePropertyChanged(nameof(IsVerticalLayout))));

        _main.WhenAnyValue(vm => vm.IsDetailViewVisible)
            .Subscribe(Observer.Create<bool>(_ => this.RaisePropertyChanged(nameof(IsDetailViewVisible))));

        _main.ImageVM.WhenAnyValue(vm => vm.SourceBitmap)
            .Subscribe(Observer.Create<Bitmap?>(_ => this.RaisePropertyChanged(nameof(SourceBitmap))));

        // 跟踪当前图片的 ExifData，有 Sony 对焦数据时动态插入"对焦点"预览项
        _main.WhenAnyValue(vm => vm.CurrentFile)
            .Select(file => file?.WhenAnyValue(f => f.ExifData) ?? Observable.Return<ExifData?>(null))
            .Switch()
            .Subscribe(Observer.Create<ExifData?>(exif => UpdateFocusPointItem(exif)));
    }

    /// <summary>
    /// 根据 ExifData 中的 Sony 对焦信息动态增删"对焦点"预览项。
    /// 有有效数据时插入到列表首位，否则移除。
    /// </summary>
    private void UpdateFocusPointItem(ExifData? exif)
    {
        var existing = Items.FirstOrDefault(i => i.Label == "对焦点");

        if (exif?.SonyFocusPosition == null)
        {
            if (existing != null) Items.Remove(existing);
            return;
        }

        var pos = exif.SonyFocusPosition.Value;
        if (pos.ImageWidth <= 0 || pos.ImageHeight <= 0) return;

        var cx = pos.FocusX / (double)pos.ImageWidth;
        var cy = pos.FocusY / (double)pos.ImageHeight;

        Size? focusFrame = null;
        if (exif.SonyFocusFrameSize.HasValue)
        {
            var fs = exif.SonyFocusFrameSize.Value;
            // 归一化对焦框大小（相对于图像宽高，0~1），使控件能以等比方式渲染，
            // 无论对焦框是否大于裁剪窗口都能正确显示。
            focusFrame = new Size(
                fs.Width / (double)pos.ImageWidth,
                fs.Height / (double)pos.ImageHeight);
        }

        if (existing != null)
        {
            existing.Center = new Point(cx, cy);
            existing.FocusFrame = focusFrame;
        }
        else
        {
            Items.Insert(0, new DetailPreviewItem("对焦点", new Point(cx, cy), focusFrame));
        }
    }
}
