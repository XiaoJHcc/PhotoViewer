using System;
using System.Collections.Generic;
using System.ComponentModel;
using Android.Content;
using Android.Graphics;
using Android.Views;
using Android.Widget;
using AndroidX.RecyclerView.Widget;
using PhotoViewer.Core.AI;
using PhotoViewer.Core.Image;
using PhotoViewer.Shared;
using PhotoViewer.ViewModels.Main;
using PhotoViewer.ViewModels.Main.File;

namespace PhotoViewer.Android.Native;

/// <summary>
/// Android 相似聚类面板（A5）：复用胶片栏卡片与单轴几何，第二行显示相似度百分比 + 75% 起柱状条；
/// 三态（列表 / 未提取 / 无相似）与动作区读共享 <see cref="SimilarityPanelViewModel"/>。
/// 行布局从胶片栏右侧切出一列，列布局接在文件栏下方；点击卡片走共享 `SelectItemCommand`（保留锚点）。
/// </summary>
internal sealed class AndroidSimilarityPanel : LinearLayout
{
    private const double ActionWidth = 40;
    private const double ActionHeight = 28;
    private readonly MainViewModel _main;
    private readonly SimilarityPanelViewModel _panel;
    private readonly RecyclerView _list;
    private readonly SimilarityAdapter _adapter;
    private readonly LinearLayout _footer;
    private readonly TextView _empty;
    private readonly TextView _error;
    private readonly TextView _status;
    private readonly TextView _action;
    private readonly TextView _emptyAction;
    private readonly LinearLayout _emptyHost;
    private bool _horizontal = true;
    private bool _disposed;

    /// <summary>行布局交叉轴高度（列表 154；空态/动作区同高）。</summary>
    internal double CrossSize => SimilarityPanelGeometry.ListCross;
    /// <summary>当前是否横向列表。</summary>
    internal bool Horizontal => _horizontal;
    /// <summary>列表是否可见（与空态互斥）。</summary>
    internal bool IsListVisible => _list.Visibility == ViewStates.Visible;
    /// <summary>当前列表项数量。</summary>
    internal int ItemCount => _panel.SimilarItems.Count;

    /// <summary>构建面板视图树并订阅共享 VM。</summary>
    /// <param name="context">宿主上下文。</param>
    /// <param name="main">共享主 VM。</param>
    public AndroidSimilarityPanel(Context context, MainViewModel main) : base(context)
    {
        _main = main;
        _panel = main.FileVM.SimilarityPanel;
        Orientation = Orientation.Vertical;
        SetBackgroundColor(Color.ParseColor("#1E1E1E")!);
        var border = new global::Android.Graphics.Drawables.GradientDrawable();
        border.SetColor(Color.ParseColor("#1E1E1E"));
        border.SetStroke((int)Math.Max(1, AndroidUiTheme.Dp(context, 1)), Color.ParseColor("#404040")!);
        border.SetCornerRadius((float)AndroidUiTheme.Dp(context, 6));
        Background = border;
        SetClipToPadding(false);

        _list = new RecyclerView(context) { OverScrollMode = OverScrollMode.Never };
        _list.SetBackgroundColor(Color.Transparent);
        _list.SetPadding(0, 0, 0, 0);
        _adapter = new SimilarityAdapter(this);
        _list.SetAdapter(_adapter);
        _list.SetLayoutManager(new LinearLayoutManager(context, LinearLayoutManager.Horizontal, false));
        _list.SetItemAnimator(null);
        AddView(_list, new LayoutParams(LayoutParams.MatchParent, 0) { Weight = 1 });

        _footer = new LinearLayout(context) { Orientation = Orientation.Horizontal };
        _footer.SetGravity(GravityFlags.Center);
        _status = Text("", 10, Color.ParseColor("#BBBBBB")!);
        _action = Text("提取全部", 11, AndroidUiTheme.Accent);
        _action.Gravity = GravityFlags.Center;
        _action.Clickable = true;
        _action.Click += (_, _) => { Console.WriteLine("[Native] 相似聚类：开始提取全部"); _panel.StartIndexingCommand(); };
        _footer.AddView(_status, new LayoutParams(0, LayoutParams.WrapContent) { Weight = 1 });
        _footer.AddView(_action, new LayoutParams(AndroidUiTheme.DpPx(this, 64), AndroidUiTheme.DpPx(this, ActionHeight)));
        _footer.Visibility = ViewStates.Gone;

        _emptyHost = new LinearLayout(context) { Orientation = Orientation.Vertical };
        _emptyHost.SetGravity(GravityFlags.Center);
        _empty = Text("无相似照片", 11, Color.ParseColor("#777777")!);
        _empty.Gravity = GravityFlags.Center;
        _error = Text("", 10, Color.ParseColor("#CC4444")!);
        _error.Gravity = GravityFlags.Center;
        _emptyAction = Text("提取全部", 11, AndroidUiTheme.Accent);
        _emptyAction.Gravity = GravityFlags.Center;
        _emptyAction.Clickable = true;
        _emptyAction.Click += (_, _) => { Console.WriteLine("[Native] 相似聚类：开始提取全部（空态）"); _panel.StartIndexingCommand(); };
        _emptyHost.AddView(_empty, new LayoutParams(LayoutParams.MatchParent, LayoutParams.WrapContent));
        _emptyHost.AddView(_error, new LayoutParams(LayoutParams.MatchParent, LayoutParams.WrapContent));
        _emptyHost.AddView(_emptyAction, new LayoutParams(LayoutParams.MatchParent, AndroidUiTheme.DpPx(this, 36)));
        AddView(_emptyHost, new LayoutParams(LayoutParams.MatchParent, 0) { Weight = 1 });
        AddView(_footer, new LayoutParams(LayoutParams.MatchParent, AndroidUiTheme.DpPx(this, (int)ActionHeight + 10)));

        _panel.PropertyChanged += OnPanelChanged;
        _main.Settings.PropertyChanged += OnSettingsChanged;
        Refresh();
    }

    /// <summary>设置列表方向（行布局横向、列布局纵向）并重载。</summary>
    /// <param name="horizontal">是否横向。</param>
    internal void SetLayout(bool horizontal)
    {
        _horizontal = horizontal;
        _list.SetLayoutManager(new LinearLayoutManager(Context,
            horizontal ? LinearLayoutManager.Horizontal : LinearLayoutManager.Vertical, false));
        _adapter.NotifyDataSetChanged();
    }

    /// <summary>同步共享 VM 到列表/三态/动作区。</summary>
    internal void Refresh()
    {
        bool hasItems = _panel.HasItems;
        _list.Visibility = hasItems ? ViewStates.Visible : ViewStates.Gone;
        _emptyHost.Visibility = hasItems ? ViewStates.Gone : ViewStates.Visible;
        _empty.Text = _panel.ShowFullPlaceholder ? "无相似照片" : "尚未提取特征";
        _error.Text = _panel.IndexError ?? "";
        _error.Visibility = _panel.HasIndexError ? ViewStates.Visible : ViewStates.Gone;
        bool listState = hasItems;
        _footer.Visibility = listState && _panel.ShowActionArea ? ViewStates.Visible : ViewStates.Gone;
        _action.Visibility = listState && _panel.ShowActionButton ? ViewStates.Visible : ViewStates.Gone;
        _action.Text = _panel.ActionButtonText;
        _emptyAction.Visibility = !listState && _panel.ShowActionButton ? ViewStates.Visible : ViewStates.Gone;
        _emptyAction.Text = _panel.ActionButtonText;
        _status.Text = _panel.IsStateIndexing
            ? (_panel.IsIndexTotalUnknown ? "提取中…" : $"{_panel.IndexProgress}/{_panel.IndexTotal}")
            : (_panel.HasUnindexed ? $"未提取 {_panel.UnindexedCount} 张" : "");
        if (!listState && _panel.IsStateIndexing)
            _empty.Text = _panel.IsIndexTotalUnknown ? "提取中…" : $"提取中 {_panel.IndexProgress}/{_panel.IndexTotal}";
        else if (!listState)
            _empty.Text = _panel.ShowFullPlaceholder ? "无相似照片" : "尚未提取特征";
        _adapter.NotifyDataSetChanged();
        RequestLayout();
    }

    private void OnPanelChanged(object? sender, PropertyChangedEventArgs args) => Post(Refresh);

    private void OnSettingsChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(PhotoViewer.ViewModels.Settings.SettingsViewModel.ShowRating)) Post(Refresh);
    }

    /// <summary>以当前面板宽度/高度重排列表内边距（行布局横向、列布局纵向）。</summary>
    /// <param name="changed">尺寸变化标记。</param>
    /// <param name="l">左。</param>
    /// <param name="t">上。</param>
    /// <param name="r">右。</param>
    /// <param name="b">下。</param>
    protected override void OnLayout(bool changed, int l, int t, int r, int b)
    {
        base.OnLayout(changed, l, t, r, b);
        int padding = AndroidUiTheme.DpPx(this, 4);
        _list.SetPadding(padding, padding, padding, padding);
    }

    private TextView Text(string text, double size, Color color)
    {
        var view = new TextView(Context)
        {
            Text = text,
            TextSize = (float)size,
        };
        view.SetTextColor(color);
        view.Gravity = GravityFlags.Center;
        return view;
    }

    /// <summary>相似项适配器：复用胶片栏卡片，第二行换成百分比 + 柱状条。</summary>
    private sealed class SimilarityAdapter : RecyclerView.Adapter
    {
        private readonly AndroidSimilarityPanel _owner;

        public SimilarityAdapter(AndroidSimilarityPanel owner) => _owner = owner;

        public override int ItemCount => _owner._panel.SimilarItems.Count;

        public override RecyclerView.ViewHolder OnCreateViewHolder(ViewGroup parent, int viewType)
        {
            var card = new AndroidThumbnailCard(parent.Context!);
            var density = AndroidUiTheme.Density(parent.Context!);
            card.LayoutParameters = new RecyclerView.LayoutParams(
                (int)Math.Round(AndroidThumbnailCard.CardWidthDp * density),
                (int)Math.Round(AndroidThumbnailCard.CardHeightDp * density));
            return new CardHolder(card);
        }

        public override void OnBindViewHolder(RecyclerView.ViewHolder holder, int position)
        {
            if (holder is not CardHolder cardHolder) return;
            var items = _owner._panel.SimilarItems;
            if (position < 0 || position >= items.Count) return;
            var item = items[position];
            double progress = Math.Clamp((item.Score - 0.75) / 0.25, 0, 1);
            cardHolder.Card.Bind(item.File, _owner._main.Settings.ShowRating,
                selected => _owner._panel.SelectItemCommand(item),
                (selected, rating) => { if (_owner._main.CurrentFile != null) _ = _owner._main.SetRatingAsync(selected, rating); },
                secondLineText: $"{item.Score * 100:0}%", secondLineProgress: progress, selectByDisplayedImage: true);
            _owner._main.FileVM.ThumbnailList.QueueThumbnailLoad(item.File, true);
        }
    }

    /// <summary>卡片 ViewHolder。</summary>
    private sealed class CardHolder : RecyclerView.ViewHolder
    {
        internal AndroidThumbnailCard Card { get; }
        public CardHolder(AndroidThumbnailCard view) : base(view) => Card = view;
    }

    /// <summary>释放订阅。</summary>
    /// <param name="disposing">是否释放托管资源。</param>
    protected override void Dispose(bool disposing)
    {
        if (disposing && !_disposed)
        {
            _disposed = true;
            _panel.PropertyChanged -= OnPanelChanged;
            _main.Settings.PropertyChanged -= OnSettingsChanged;
            _adapter.Dispose();
        }
        base.Dispose(disposing);
    }
}
