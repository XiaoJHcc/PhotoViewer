using ReactiveUI;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using PhotoViewer.Core;
using PhotoViewer.Core.AI;
using PhotoViewer.Core.Image;
using PhotoViewer.ViewModels.Settings;

namespace PhotoViewer.ViewModels.Main.File;

/// <summary>
/// 相似聚类面板的视图模型。
/// 订阅 <see cref="MainViewModel.CurrentFile"/> 切换,异步调用 <see cref="SimilarityService"/>
/// 计算相似项并暴露给 <c>SimilarityListView</c>;同时通过 <see cref="ThumbnailListViewModel"/>
/// 的加载队列,触发相似项的缩略图加载。
/// </summary>
public class SimilarityPanelViewModel : ReactiveObject
{
    private readonly MainViewModel _main;
    private readonly ThumbnailListViewModel _thumbnailList;

    private CancellationTokenSource? _computeCts;

    /// <summary>
    /// 一次性抑制位:由 <see cref="SelectItemCommand"/> 置位,
    /// 目的是让"点击相似项切换主图"这一动作不反过来刷新相似面板本身(避免锚点漂移)。
    /// 只对"由自身触发的那一次 <see cref="MainViewModel.CurrentFile"/> 变更"生效,随即复位。
    /// </summary>
    private bool _suppressNextRecompute;

    private IReadOnlyList<SimilarityItem> _similarItems = Array.Empty<SimilarityItem>();
    /// <summary>当前相似项列表(已按分数降序),绑定到 SimilarityListView 的 ItemsSource。</summary>
    public IReadOnlyList<SimilarityItem> SimilarItems
    {
        get => _similarItems;
        private set
        {
            this.RaiseAndSetIfChanged(ref _similarItems, value);
            this.RaisePropertyChanged(nameof(IsEmpty));
            this.RaisePropertyChanged(nameof(HasItems));
        }
    }

    /// <summary>是否无相似项,用于显示占位文案。</summary>
    public bool IsEmpty => _similarItems.Count == 0;

    /// <summary>是否有相似项(便于直接绑定 IsVisible)。</summary>
    public bool HasItems => _similarItems.Count > 0;

    /// <summary>当前布局是否为竖向(决定列表方向、对齐)。</summary>
    public bool IsVerticalLayout => _main.IsHorizontalLayout;

    /// <summary>主视图模型引用,模板内绑定 IsCurrent / Settings 用。</summary>
    public MainViewModel Main => _main;

    /// <summary>设置引用,供模板绑定 ShowRating 等。</summary>
    public SettingsViewModel Settings => _main.Settings;

    /// <summary>
    /// 构造相似聚类面板视图模型。
    /// </summary>
    /// <param name="main">主视图模型</param>
    /// <param name="thumbnailList">主缩略图列表 VM,候选池取自其 <see cref="ThumbnailListViewModel.FilteredFiles"/></param>
    public SimilarityPanelViewModel(MainViewModel main, ThumbnailListViewModel thumbnailList)
    {
        _main = main;
        _thumbnailList = thumbnailList;

        _main.WhenAnyValue(x => x.IsHorizontalLayout)
            .Subscribe(_ => this.RaisePropertyChanged(nameof(IsVerticalLayout)));

        _main.WhenAnyValue(x => x.CurrentFile)
            .Subscribe(file =>
            {
                if (_suppressNextRecompute)
                {
                    _suppressNextRecompute = false;
                    return;
                }
                _ = RecomputeAsync(file);
            });

        // 候选池来自主缩略图列表的 FilteredFiles(已应用同名分组与筛选);
        // 列表整体替换时(开关分组、改筛选、切文件夹)同步重算,避免相似面板出现"被合并掉的伴侣文件"或"自身的另一格式"。
        _thumbnailList.WhenAnyValue(x => x.FilteredFiles)
            .Skip(1)
            .Subscribe(__ => { _ = RecomputeAsync(_main.CurrentFile); });
    }

    /// <summary>
    /// 选中相似项(命令绑定),切换主视图当前图片但保留主缩略图列表的锚点高亮。
    /// 同时抑制本次 <see cref="MainViewModel.CurrentFile"/> 变更对相似面板的反向刷新,
    /// 保持原锚点对应的相似列表不被重排,避免操作混乱。
    /// </summary>
    /// <param name="item">被点击的相似项</param>
    public void SelectItemCommand(SimilarityItem item)
    {
        if (item?.File == null) return;
        if (ReferenceEquals(item.File, _main.CurrentFile)) return;

        _suppressNextRecompute = true;
        _main.SetCurrentImageKeepAnchor(item.File);
    }

    /// <summary>
    /// 重新计算当前图片的相似项;旧的计算被取消。
    /// </summary>
    private async Task RecomputeAsync(ImageFile? current)
    {
        _computeCts?.Cancel();
        var cts = new CancellationTokenSource();
        _computeCts = cts;
        var token = cts.Token;

        if (current == null)
        {
            UpdateOnUi(Array.Empty<SimilarityItem>(), token);
            return;
        }

        var pool = _thumbnailList.FilteredFiles;
        try
        {
            var results = await SimilarityService.FindSimilarAsync(current, pool, ct: token)
                .ConfigureAwait(false);

            if (token.IsCancellationRequested) return;

            UpdateOnUi(results, token);
        }
        catch (OperationCanceledException)
        {
            // 已被新的计算覆盖,忽略
        }
        catch (Exception ex)
        {
            Console.WriteLine("相似度计算失败: " + ex.Message);
        }
    }

    /// <summary>
    /// 在 UI 线程提交计算结果,并触发缩略图加载。
    /// </summary>
    private void UpdateOnUi(IReadOnlyList<SimilarityItem> results, CancellationToken token)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (token.IsCancellationRequested) return;
            SimilarItems = results;

            foreach (var item in results)
            {
                _thumbnailList.QueueThumbnailLoad(item.File, priority: true);
            }
        });
    }
}
