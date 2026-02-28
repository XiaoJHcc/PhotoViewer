using System.Collections.ObjectModel;
using System.Reactive;
using Avalonia;
using Avalonia.Media.Imaging;
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

    public DetailPreviewItem(string label, Point center)
    {
        _label = label;
        _center = center;
    }
}

public class DetailViewModel : ReactiveObject
{
    private readonly MainViewModel _main;

    public bool IsVerticalLayout => _main.IsHorizontalLayout;

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

        _main.ImageVM.WhenAnyValue(vm => vm.SourceBitmap)
            .Subscribe(Observer.Create<Bitmap?>(_ => this.RaisePropertyChanged(nameof(SourceBitmap))));
    }
}
