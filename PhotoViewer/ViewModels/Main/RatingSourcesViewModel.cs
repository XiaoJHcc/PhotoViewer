using PhotoViewer.Core;
using PhotoViewer.Core.Image;
using ReactiveUI;

namespace PhotoViewer.ViewModels.Main;

public sealed record RatingSourceItem(
    string Name,
    string Kind,
    string Status,
    RatingSourceState State);

public sealed class RatingSourcesViewModel : ReactiveObject
{
    private readonly MainViewModel _main;
    private PhotoGroup? _group;
    private IReadOnlyList<RatingSourceItem> _items = [];
    private bool _isOpen;

    public RatingSourcesViewModel(MainViewModel main) => _main = main;

    public ImageFile? Target { get; private set; }

    public IReadOnlyList<RatingSourceItem> Items
    {
        get => _items;
        private set => this.RaiseAndSetIfChanged(ref _items, value);
    }

    public bool IsOpen
    {
        get => _isOpen;
        private set => this.RaiseAndSetIfChanged(ref _isOpen, value);
    }

    public void Show(ImageFile target, PhotoGroup group)
    {
        Target = target;
        _group = group;
        Refresh();
        IsOpen = true;
    }

    public void Close() => IsOpen = false;

    public void Refresh()
    {
        if (Target != null) _group = _main.GetRatingGroup(Target);
        Items = _group?.RatingSources.Select(source => new RatingSourceItem(
            source.DisplayName,
            source.Source == XmpRatingSource.Sidecar ? "XMP" : Path.GetExtension(source.File.Name).TrimStart('.').ToUpperInvariant(),
            StatusText(source),
            source)).ToArray() ?? [];
    }

    public async Task SetRatingAsync(int rating)
    {
        if (Target == null) return;
        var outcome = await _main.SetRatingWithResultAsync(Target, rating);
        Refresh();
        if ((outcome is RatingWriteOutcome.Applied or RatingWriteOutcome.NoChange) && Target?.HasRatingConflict == false) Close();
    }

    private static string StatusText(RatingSourceState source) => source.State switch
    {
        XmpRatingState.Rated when source.Value == 0 => "无星级",
        XmpRatingState.Rated => $"{source.Value} 星",
        XmpRatingState.Rejected => "已拒绝",
        XmpRatingState.Missing => "未设置",
        XmpRatingState.Conflict => "多份评分冲突",
        _ => "无法读取",
    };
}
