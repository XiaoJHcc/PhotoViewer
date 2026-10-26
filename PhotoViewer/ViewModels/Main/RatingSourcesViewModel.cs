using System.ComponentModel;
using PhotoViewer.Core;
using PhotoViewer.Core.Image;
using ReactiveUI;

namespace PhotoViewer.ViewModels.Main;

public sealed record RatingSourceItem(
    string Name,
    string Kind,
    string Status,
    RatingSourceState State);

/// <summary>
/// RATING-04 来源浮窗状态：按目标照片所属组的全部实际来源生成条目。
/// 打开期间订阅目标与全部组内文件的来源/分组变化，磁盘被外部整批刷新后条目即时跟随；
/// 关闭即退订，不把订阅留到下一次展示。
/// </summary>
public sealed class RatingSourcesViewModel : ReactiveObject, IDisposable
{
    private readonly MainViewModel _main;
    private readonly List<ImageFile> _watched = [];
    private IReadOnlyList<RatingSourceItem> _items = [];
    private bool _isOpen;
    private bool _refreshing;
    private bool _disposed;

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
        if (_disposed) return;
        Target = target;
        IsOpen = true;
        Apply(group);
    }

    /// <summary>收起浮窗并退订全部来源；再次 <see cref="Show"/> 时按最新真值重新订阅。</summary>
    public void Close()
    {
        IsOpen = false;
        Unwatch();
    }

    /// <summary>
    /// 按共享层当前真值重建条目。组对象被整批替换（目录重开/同名组合并变化）时
    /// 重新订阅新组成员，因此外部刷新后不需要再次 Show 或写评分。
    /// </summary>
    public void Refresh()
    {
        if (_disposed || _refreshing || Target is not { } target) return;
        _refreshing = true;
        try
        {
            Apply(_main.GetRatingGroup(target));
        }
        finally
        {
            _refreshing = false;
        }
    }

    /// <summary>用给定组重建条目并订阅其成员；条目内容未变时不触发通知，避免浮窗无谓重建。</summary>
    private void Apply(PhotoGroup group)
    {
        if (Target is not { } target) return;
        Watch(target, group);
        var items = group.RatingSources
            .Select(source => new RatingSourceItem(
                source.DisplayName,
                source.Source == XmpRatingSource.Sidecar
                    ? "XMP"
                    : Path.GetExtension(source.File.Name).TrimStart('.').ToUpperInvariant(),
                StatusText(source),
                source))
            .ToArray();
        if (!items.SequenceEqual(_items)) Items = items;
    }

    public async Task SetRatingAsync(int rating)
    {
        if (_disposed || Target is not { } target) return;
        var outcome = await _main.SetRatingWithResultAsync(target, rating);
        // 写入失败按磁盘真值刷新后保留；只有成功且刷新后冲突确实解除才收起（RATING-04）。
        if (_disposed || !IsOpen || !ReferenceEquals(Target, target)) return;
        Refresh();
        if (outcome is RatingWriteOutcome.Applied or RatingWriteOutcome.NoChange
            && target.HasRatingConflict == false)
            Close();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        IsOpen = false;
        Unwatch();
    }

    /// <summary>订阅目标与组内全部文件；组成员变化时重订，避免外部刷新后停在旧组。</summary>
    private void Watch(ImageFile target, PhotoGroup group)
    {
        var desired = new HashSet<ImageFile> { target };
        foreach (var member in group.Files) desired.Add(member);
        for (int index = _watched.Count - 1; index >= 0; index--)
        {
            var file = _watched[index];
            if (desired.Contains(file)) continue;
            file.PropertyChanged -= OnWatchedFileChanged;
            _watched.RemoveAt(index);
        }

        foreach (var file in desired)
        {
            if (_watched.Contains(file)) continue;
            file.PropertyChanged += OnWatchedFileChanged;
            _watched.Add(file);
        }
    }

    private void Unwatch()
    {
        foreach (var file in _watched) file.PropertyChanged -= OnWatchedFileChanged;
        _watched.Clear();
    }

    private void OnWatchedFileChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (_disposed || !IsOpen) return;
        if (args.PropertyName == nameof(ImageFile.RatingSources))
        {
            Refresh();
            return;
        }
        // PhotoGroupMap.Build 先把所有文件的 Group 清空再逐个回填：只有目标自己的新组已回填时才刷新，
        // 否则 Refresh 会在 Build 内部再经 GetRatingGroup 触发一次 Build。目标不再属于任何组时
        // 列表重载会关闭浮窗，这里不必为过渡态重建。
        if (args.PropertyName == nameof(ImageFile.Group)
            && Target?.Group is not null
            && sender is ImageFile { Group: not null })
            Refresh();
    }

    /// <summary>
    /// 来源状态的业务文案：Rated 0 与 Missing / Invalid / Unreadable 各不相同，
    /// 不把"无星级"和"未设置""无效""不可读"混成同一个词。
    /// </summary>
    private static string StatusText(RatingSourceState source) => source.State switch
    {
        XmpRatingState.Rated when source.Value == 0 => "无星级",
        XmpRatingState.Rated => $"{source.Value} 星",
        XmpRatingState.Rejected => "已拒绝",
        XmpRatingState.Missing => "未设置",
        XmpRatingState.Conflict => "多份评分冲突",
        XmpRatingState.Invalid => "无效",
        XmpRatingState.Unreadable => "无法读取",
        _ => "无法读取",
    };
}
