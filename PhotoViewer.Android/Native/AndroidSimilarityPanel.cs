using System;
using System.Collections.Generic;
using System.ComponentModel;
using Android.Content;
using Android.Graphics;
using Android.Views;
using Android.Widget;
using AndroidX.RecyclerView.Widget;
using PhotoViewer.Core.AI;
using PhotoViewer.Core.Design;
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
    private readonly MainViewModel _main;
    private readonly SimilarityPanelViewModel _panel;
    private readonly RecyclerView _list;
    private readonly SimilarityAdapter _adapter;
    private readonly LinearLayout _footer;
    private readonly LinearLayout _footerRow;
    private readonly TextView _footerError;
    private readonly TextView _empty;
    private readonly TextView _error;
    private readonly TextView _status;
    private readonly TextView _action;
    private readonly TextView _emptyAction;
    private readonly View _actionSlot;
    private readonly View _emptyActionSlot;
    private readonly LinearLayout _emptyHost;
    private bool _horizontal = true;
    private bool _disposed;

    /// <summary>行布局交叉轴高度（列表 154；空态/动作区同高）。</summary>
    internal double CrossSize => _horizontal ? SimilarityPanelGeometry.ListCross : SimilarityPanelGeometry.PanelWidth;
    /// <summary>当前是否横向列表。</summary>
    internal bool Horizontal => _horizontal;
    /// <summary>列表是否可见（与空态互斥）。</summary>
    internal bool IsListVisible => _list.Visibility == ViewStates.Visible;
    /// <summary>当前列表项数量。</summary>
    internal int ItemCount => _panel.SimilarItems.Count;
    /// <summary>列表态停靠区错误文案（验收用）。</summary>
    internal string? DockErrorTextForChecks => _footerError.Text;
    /// <summary>列表态停靠区错误标签是否可见（验收用）。</summary>
    internal bool DockErrorVisibleForChecks => _footerError.Visibility == ViewStates.Visible;

    /// <summary>构建面板视图树并订阅共享 VM。</summary>
    /// <param name="context">宿主上下文。</param>
    /// <param name="main">共享主 VM。</param>
    public AndroidSimilarityPanel(Context context, MainViewModel main) : base(context)
    {
        _main = main;
        _panel = main.FileVM.SimilarityPanel;
        Orientation = Orientation.Vertical;
        var border = new global::Android.Graphics.Drawables.GradientDrawable();
        border.SetColor(AndroidUiTheme.SimPanelBackground);
        border.SetStroke((int)Math.Max(1, AndroidUiTheme.Dp(context, DesignTokens.SimPanelBorderWidth)),
            AndroidUiTheme.SimPanelBorder);
        border.SetCornerRadius((float)AndroidUiTheme.Dp(context, DesignTokens.SimPanelCornerRadius));
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

        _footer = new LinearLayout(context) { Orientation = Orientation.Vertical };
        _footer.SetGravity(GravityFlags.CenterHorizontal);
        // SIM-10：列表态动作区同样承载提取错误，不只在空态显示。
        _footerError = Text("", 10, AndroidUiTheme.SimError);
        _footerError.Gravity = GravityFlags.Center;
        _footerError.SetMaxWidth(AndroidUiTheme.DpPx(this, DesignTokens.SimErrorMaxWidth));
        _footerError.Visibility = ViewStates.Gone;
        _status = Text("", 10, AndroidUiTheme.SimCaption);
        _action = ActionLabel("提取全部");
        _actionSlot = ActionSlot(_action, () =>
        {
            Console.WriteLine("[Native] 相似聚类：开始提取全部");
            _panel.StartIndexingCommand();
        });
        _footerRow = new LinearLayout(context);
        ArrangeFooter(true);
        _footer.AddView(_footerError, new LayoutParams(LayoutParams.MatchParent, LayoutParams.WrapContent));
        _footer.AddView(_footerRow, new LayoutParams(LayoutParams.MatchParent, AndroidUiTheme.DpPx(this, 44)));
        _footer.Visibility = ViewStates.Gone;

        _emptyHost = new LinearLayout(context) { Orientation = Orientation.Vertical };
        _emptyHost.SetGravity(GravityFlags.Center);
        int emptyMargin = AndroidUiTheme.DpPx(this, DesignTokens.SimEmptyMargin);
        _emptyHost.SetPadding(emptyMargin, emptyMargin, emptyMargin, emptyMargin);
        _empty = Text("无相似照片", 11, AndroidUiTheme.SimPlaceholder);
        _empty.Gravity = GravityFlags.Center;
        _error = Text("", 10, AndroidUiTheme.SimError);
        _error.Gravity = GravityFlags.Center;
        _emptyAction = ActionLabel("提取全部");
        _emptyActionSlot = ActionSlot(_emptyAction, () =>
        {
            Console.WriteLine("[Native] 相似聚类：开始提取全部（空态）");
            _panel.StartIndexingCommand();
        });
        int spacing = AndroidUiTheme.DpPx(this, DesignTokens.SimEmptySpacing);
        _emptyHost.AddView(_empty, new LayoutParams(LayoutParams.MatchParent, LayoutParams.WrapContent)
        {
            BottomMargin = spacing,
        });
        _emptyHost.AddView(_error, new LayoutParams(LayoutParams.MatchParent, LayoutParams.WrapContent)
        {
            BottomMargin = spacing,
        });
        _emptyHost.AddView(_emptyActionSlot, new LayoutParams(
            AndroidUiTheme.DpPx(this, DesignTokens.SimActionWidth),
            AndroidUiTheme.DpPx(this, 44))
        {
            Gravity = GravityFlags.CenterHorizontal,
        });
        AddView(_emptyHost, new LayoutParams(LayoutParams.MatchParent, 0) { Weight = 1 });
        AddView(_footer, new LayoutParams(LayoutParams.MatchParent, LayoutParams.WrapContent));

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
        // 行布局面板较宽，计数与按钮同排；列布局面板仅 110 宽，计数放在按钮上方。
        ArrangeFooter(horizontal);
        _adapter.NotifyDataSetChanged();
    }

    /// <summary>按面板方向重排行布局停靠区：横排为「计数 + 按钮」，竖排为「计数 / 按钮」。</summary>
    /// <param name="horizontal">是否行布局。</param>
    private void ArrangeFooter(bool horizontal)
    {
        _footerRow.Orientation = horizontal ? Orientation.Horizontal : Orientation.Vertical;
        _footerRow.SetGravity(horizontal ? GravityFlags.CenterVertical : GravityFlags.CenterHorizontal);
        _footerRow.RemoveAllViews();
        if (horizontal)
        {
            _footerRow.AddView(_status, new LayoutParams(0, LayoutParams.MatchParent) { Weight = 1 });
            _footerRow.AddView(_actionSlot, new LayoutParams(
                AndroidUiTheme.DpPx(this, DesignTokens.SimActionWidth),
                AndroidUiTheme.DpPx(this, 44)));
        }
        else
        {
            _footerRow.AddView(_status, new LayoutParams(LayoutParams.MatchParent, LayoutParams.WrapContent));
            _footerRow.AddView(_actionSlot, new LayoutParams(
                AndroidUiTheme.DpPx(this, DesignTokens.SimActionWidth),
                AndroidUiTheme.DpPx(this, 44))
            {
                Gravity = GravityFlags.CenterHorizontal,
                TopMargin = AndroidUiTheme.DpPx(this, DesignTokens.SimDockSpacing),
            });
        }
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
        _footerError.Text = _panel.IndexError ?? "";
        _footerError.Visibility = listState && _panel.ShowActionAreaError ? ViewStates.Visible : ViewStates.Gone;
        _actionSlot.Visibility = listState && _panel.ShowActionButton ? ViewStates.Visible : ViewStates.Gone;
        _action.Text = _panel.ActionButtonText;
        _actionSlot.ContentDescription = _panel.ActionButtonText;
        _emptyActionSlot.Visibility = !listState && _panel.ShowActionButton ? ViewStates.Visible : ViewStates.Gone;
        _emptyAction.Text = _panel.ActionButtonText;
        _emptyActionSlot.ContentDescription = _panel.ActionButtonText;
        // SIM-03：文本进度保留，提取中用进度色，未提取计数用说明色。
        _status.SetTextColor(_panel.IsStateIndexing ? AndroidUiTheme.SimProgress : AndroidUiTheme.SimCaption);
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

    /// <summary>
    /// 行布局列表态停靠区的自然宽度（SIM-07）：动作按钮、未提取计数与错误文本较宽者加左右外边距。
    /// 主窗口用它给共享 <see cref="SimilarityPanelGeometry.RowWidth"/> 预留停靠区，否则 1–2 项列表会被动作区挤成零宽。
    /// </summary>
    /// <returns>停靠区宽度（dp）；面板未显示动作区时仍返回自然宽，由调用方按状态决定是否计入。</returns>
    internal double DockMeasureWidth()
    {
        double text = Math.Max(DesignTokens.SimActionWidth, MeasureTextWidthDp(_status));
        double error = _footerError.Visibility == ViewStates.Visible
            ? Math.Min(DesignTokens.SimErrorMaxWidth, MeasureTextWidthDp(_footerError))
            : 0;
        return Math.Max(text, error) + DesignTokens.SimDockMargin * 2;
    }

    /// <summary>文本控件的自然宽度（dp，不换行）。</summary>
    /// <param name="view">文本控件。</param>
    /// <returns>自然宽度（dp）。</returns>
    private double MeasureTextWidthDp(TextView view)
    {
        string text = view.Text ?? "";
        if (text.Length == 0) return 0;
        var paint = view.Paint;
        return paint == null ? 0 : paint.MeasureText(text) / AndroidUiTheme.Density(Context!);
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

    /// <summary>动作按钮文字外观（SIM-03：90×28、#2A2A2A 底、#555555 描边、#CCCCCC 字）。</summary>
    /// <param name="text">按钮文案。</param>
    /// <returns>按钮文本。</returns>
    private TextView ActionLabel(string text)
    {
        var view = Text(text, 11, AndroidUiTheme.SimActionForeground);
        var background = new global::Android.Graphics.Drawables.GradientDrawable();
        background.SetColor(AndroidUiTheme.SimActionBackground);
        background.SetStroke(
            (int)Math.Max(1, AndroidUiTheme.Dp(Context!, DesignTokens.SimPanelBorderWidth)),
            AndroidUiTheme.SimActionBorder);
        background.SetCornerRadius((float)AndroidUiTheme.Dp(Context!, DesignTokens.SimActionCornerRadius));
        view.Background = background;
        return view;
    }

    /// <summary>动作按钮的 44dp 点按宿主，内部保持 90×28 的可见几何。</summary>
    /// <param name="label">按钮文本。</param>
    /// <param name="action">点击动作。</param>
    /// <returns>可点击宿主。</returns>
    private View ActionSlot(TextView label, Action action)
    {
        var slot = new FrameLayout(Context!) { Clickable = true, Focusable = true };
        slot.AddView(label, new FrameLayout.LayoutParams(
            AndroidUiTheme.DpPx(this, DesignTokens.SimActionWidth),
            AndroidUiTheme.DpPx(this, DesignTokens.SimActionHeight))
        {
            Gravity = GravityFlags.Center,
        });
        slot.Click += (_, _) => action();
        slot.ContentDescription = label.Text;
        return slot;
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
