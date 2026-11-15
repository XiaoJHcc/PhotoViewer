using System;
using Android.Animation;
using Android.Content;
using Android.Graphics;
using Android.Provider;
using Android.Views;
using AndroidX.RecyclerView.Widget;
using PhotoViewer.Core.Image;
using PhotoViewer.Core.Platform;
using PhotoViewer.Shared;
using PhotoViewer.ViewModels.Main;
using PhotoViewer.ViewModels.Main.File;

namespace PhotoViewer.Android.Native;

/// <summary>
/// 胶片栏（FILM-01–05、FILM-07）：RecyclerView + 共享 <see cref="FilmCardGeometry"/> 的两档卡片几何与
/// 350ms 三次缓出显式定位（用户触摸即接管、减少动态效果直达）、可见范围上报共享缩略图调度器。
/// 宽形态固定大档；手机形态按整条主轴长度选档（竖屏横向、横屏纵向），两端留白由宿主给出的停靠线决定。
/// 点选只改当前照片，不主动居中。
/// </summary>
internal sealed class AndroidFilmstripView : RecyclerView
{
    private readonly MainViewModel _main;
    private readonly FilmstripAdapter _adapter;
    private readonly int _touchSlop;
    private ValueAnimator? _animation;
    private int _scrollRequestVersion;
    private bool _centerPending;
    private bool _axisInitialized;
    private bool _horizontal = true;
    private FilmCardTier _tier = FilmCardTier.Large;
    private bool _userTouching;
    private int _lastReportedFirst = -1;
    private int _lastReportedLast = -1;
    private double _extraLead;
    private double _extraTrail;
    private int _padLeft = -1, _padTop = -1, _padRight = -1, _padBottom = -1;

    /// <summary>星级点击（写回交给宿主统一走共享评分与错误提示）。</summary>
    internal event Action<ImageFile, int>? RateRequested;

    /// <summary>冲突三角点击：打开该卡片的来源浮窗，不写值、不切图（RATING-04）。</summary>
    internal event Action<AndroidThumbnailCard>? SourcesRequested;

    /// <summary>是否横向单行。</summary>
    internal bool IsHorizontal => _horizontal;
    /// <summary>定位动画是否仍在播放（验收区分自动定位与用户滚动）。</summary>
    internal bool IsScrollAnimating => _animation != null;
    /// <summary>当前主轴滚动偏移（px）。</summary>
    internal int MainOffset => _horizontal ? ComputeHorizontalScrollOffset() : ComputeVerticalScrollOffset();
    /// <summary>文档主轴长度（px，验收读取）。</summary>
    internal int DocumentMain => _horizontal ? ComputeHorizontalScrollRange() : ComputeVerticalScrollRange();
    /// <summary>业务列表（验收读取顺序）。</summary>
    internal System.Collections.Generic.IReadOnlyList<ImageFile> Files => _main.FileVM.ThumbnailList.FilteredFiles;
    /// <summary>当前卡片档位（验收读取）。</summary>
    internal FilmCardTier Tier => _tier;
    /// <summary>当前可见容量（按整条主轴长度除以步长，验收读取）。</summary>
    internal int VisibleCapacity => FilmCardGeometry.VisibleCapacity(
        (_horizontal ? Width : Height) / AndroidUiTheme.Density(Context!), _tier, _horizontal);

    /// <summary>取某一项已创建的卡片（验收核对卡片几何与绑定身份）；未创建返回 null。</summary>
    /// <param name="index">业务索引。</param>
    /// <returns>卡片或 null。</returns>
    internal AndroidThumbnailCard? CardAt(int index)
    {
        var holder = FindViewHolderForAdapterPosition(index);
        if (holder is CardHolder cardHolder) return cardHolder.Card;
        // 部分布局切换后 RecyclerView 的 holder 位置未刷新，按绑定身份回退找可见卡片。
        var files = Files;
        if (index < 0 || index >= files.Count) return null;
        for (int i = 0; i < ChildCount; i++)
        {
            if (GetChildAt(i) is AndroidThumbnailCard card && ReferenceEquals(card.BoundFile, files[index]))
                return card;
        }
        return null;
    }

    /// <summary>构建 RecyclerView 与卡片适配器。</summary>
    /// <param name="context">宿主上下文。</param>
    /// <param name="main">共享主 VM。</param>
    public AndroidFilmstripView(Context context, MainViewModel main) : base(context)
    {
        _main = main;
        _touchSlop = ViewConfiguration.Get(context)?.ScaledTouchSlop ?? 8;
        // 栏背景由宿主 scrim 承载；列表自身透明，避免盖住圆角/半透明材质。
        SetBackgroundColor(Color.Transparent);
        SetLayoutManager(new LinearLayoutManager(context, LinearLayoutManager.Horizontal, false));
        _adapter = new FilmstripAdapter(this);
        SetAdapter(_adapter);
        OverScrollMode = OverScrollMode.Never;
        SetClipToPadding(false);
        SetItemAnimator(null);
        SetOnTouchListener(new TouchObserver(this));
        AddOnScrollListener(new OnScrollObserver(this));
        UpdatePadding(notify: false);
    }

    /// <summary>设置主轴方向（行布局横向单行、列布局纵向单列；手机形态只由形态决定）并让几何立即生效。</summary>
    /// <param name="horizontal">是否横向单行。</param>
    internal void SetAxis(bool horizontal)
    {
        if (_axisInitialized && _horizontal == horizontal) return;
        _axisInitialized = true;
        _horizontal = horizontal;
        var manager = new LinearLayoutManager(Context!, horizontal ? LinearLayoutManager.Horizontal : LinearLayoutManager.Vertical, false);
        SetLayoutManager(manager);
        HorizontalScrollBarEnabled = horizontal;
        VerticalScrollBarEnabled = !horizontal;
        UpdatePadding(notify: false);
        _adapter.NotifyCardSizes();
    }

    /// <summary>设置卡片档位（宽形态恒为大档；手机形态按主轴长度选档），换档后重算卡槽与内边距。</summary>
    /// <param name="tier">卡片档位。</param>
    internal void SetTier(FilmCardTier tier)
    {
        if (_tier == tier && _adapter.Tier == tier) return;
        _tier = tier;
        _adapter.SetTier(tier);
        UpdatePadding(notify: false);
    }

    /// <summary>
    /// 额外的主轴两端留白：手机形态把共享槽位给出的停靠线换成列表内容留白
    /// （停靠线减去该档自身主轴内边距），宽形态保持 0。
    /// </summary>
    /// <param name="lead">前导额外留白（dp）。</param>
    /// <param name="trail">尾随额外留白（dp）。</param>
    internal void SetEdgePadding(double lead, double trail)
    {
        double safeLead = Math.Max(0, lead), safeTrail = Math.Max(0, trail);
        if (Math.Abs(safeLead - _extraLead) < .01 && Math.Abs(safeTrail - _extraTrail) < .01) return;
        _extraLead = safeLead;
        _extraTrail = safeTrail;
        UpdatePadding(notify: true);
    }

    /// <summary>可见卡片重绘：后台星级/来源补齐后重读 <see cref="ImageFile.Rating"/> 与冲突态。</summary>
    internal void RefreshCardStates()
    {
        for (int index = 0; index < ChildCount; index++)
            if (GetChildAt(index) is AndroidThumbnailCard card) card.Invalidate();
    }

    /// <summary>业务筛选结果原子替换后重载列表（不主动居中，[FILM-01]）。</summary>
    internal void ReloadData()
    {
        UpdatePadding(notify: false);
        _adapter.NotifyDataSetChanged();
        Post(ReportVisibleRange);
    }

    /// <summary>取消尚未执行的定位请求与在途动画，保留当前可见位置（[FILM-03]）。</summary>
    internal void CancelScroll()
    {
        _scrollRequestVersion++;
        CancelAnimation();
    }

    /// <summary>切图同步通知时取消"此刻之前"的定位请求，不波及随后的显式定位。</summary>
    internal void CancelScrollUpToNow()
    {
        int version = _scrollRequestVersion;
        Post(() => { if (_scrollRequestVersion == version) CancelScroll(); });
    }

    /// <summary>显式定位当前项：沿主轴把卡片中心移到视口中心，用共享 350ms 三次缓出。</summary>
    internal void ScrollToCurrent()
    {
        int request = ++_scrollRequestVersion;
        Post(() =>
        {
            if (request != _scrollRequestVersion) return;
            if (Width < 1 || Height < 1)
            {
                _centerPending = true;
                return;
            }
            CenterOnCurrent(request);
        });
    }

    /// <summary>用户触摸/拖动接管列表（[FILM-03]）：撤销定位请求与在途动画。</summary>
    internal void UserTookOver() => CancelScroll();

    /// <summary>可见卡片交共享缩略图调度器按 P1 加载，并上报可见范围。</summary>
    internal void ReportVisibleRange()
    {
        var files = _main.FileVM.ThumbnailList.FilteredFiles;
        if (files.Count == 0) return;
        var manager = GetLayoutManager() as LinearLayoutManager;
        if (manager == null) return;
        int first = manager.FindFirstVisibleItemPosition();
        int last = manager.FindLastVisibleItemPosition();
        if (first < 0 || last < first) return;
        if (first == _lastReportedFirst && last == _lastReportedLast) return;
        _lastReportedFirst = first;
        _lastReportedLast = last;
        var visible = new System.Collections.Generic.List<ImageFile>(last - first + 1);
        for (int index = first; index <= last && index < files.Count; index++)
            if (index >= 0) visible.Add(files[index]);
        if (visible.Count > 0) _main.FileVM.ThumbnailList.LoadVisibleThumbnails(visible);
        _main.FileVM.ThumbnailList.ReportVisibleRange(Math.Max(0, first), Math.Min(files.Count - 1, last));
    }

    /// <summary>布局尺寸变化：重算内边距、约束偏移并补执行待定定位。</summary>
    /// <param name="changed">宽高变化标记。</param>
    /// <param name="l">左。</param>
    /// <param name="t">上。</param>
    /// <param name="r">右。</param>
    /// <param name="b">下。</param>
    protected override void OnLayout(bool changed, int l, int t, int r, int b)
    {
        base.OnLayout(changed, l, t, r, b);
        UpdatePadding(notify: true);
        if (_centerPending && Width >= 1 && Height >= 1)
        {
            _centerPending = false;
            ScrollToCurrent();
        }
        ReportVisibleRange();
    }

    /// <summary>
    /// 更新卡片几何与首项居中的内边距（共享 <see cref="FilmCardGeometry"/>）。
    /// 只在轴/档位/实际 padding 变化时通知子项，避免 OnLayout 期间整表重绑与二次 RequestLayout。
    /// </summary>
    /// <param name="notify">padding 真正变化时是否通知子项重绑。</param>
    private void UpdatePadding(bool notify)
    {
        int count = _main.FileVM.ThumbnailList.FilteredFiles.Count;
        double density = AndroidUiTheme.Density(Context!);
        double crossLead = FilmCardGeometry.CrossLead(_tier, _horizontal);
        double crossTrail = FilmCardGeometry.CrossTrail(_tier, _horizontal);
        double mainLead = FilmCardGeometry.MainLead(_tier, _horizontal) + _extraLead;
        double mainTrail = FilmCardGeometry.MainTrail(_tier, _horizontal) + _extraTrail;
        int left, top, right, bottom;
        if (_horizontal)
        {
            double contentWidth = Width / density;
            double start = Math.Max(mainLead, FilmCardGeometry.ItemStart(_tier, true, contentWidth, count) - FilmCardGeometry.MainLead(_tier, true) + mainLead);
            left = (int)Math.Round(start * density);
            top = (int)Math.Round(crossLead * density);
            right = (int)Math.Round(mainTrail * density);
            bottom = (int)Math.Round(crossTrail * density);
        }
        else
        {
            left = (int)Math.Round(crossLead * density);
            top = (int)Math.Round(mainLead * density);
            right = (int)Math.Round(crossTrail * density);
            bottom = (int)Math.Round(mainTrail * density);
        }
        if (left == _padLeft && top == _padTop && right == _padRight && bottom == _padBottom) return;
        _padLeft = left;
        _padTop = top;
        _padRight = right;
        _padBottom = bottom;
        SetPadding(left, top, right, bottom);
        if (notify) _adapter.NotifyCardSizes();
    }

    /// <summary>把当前项居中（不含动画决策）。</summary>
    /// <param name="request">定位请求版本。</param>
    private void CenterOnCurrent(int request)
    {
        var files = _main.FileVM.ThumbnailList.FilteredFiles;
        int index = _main.CurrentFile == null ? -1 : files.IndexOf(_main.CurrentFile);
        if (index < 0) return;
        double density = AndroidUiTheme.Density(Context!);
        double viewport = _horizontal ? Width / density : Height / density;
        double document = _horizontal ? ComputeHorizontalScrollRange() / density : ComputeVerticalScrollRange() / density;
        double contentWidth = viewport;
        double itemStart = FilmCardGeometry.ItemStart(_tier, _horizontal, contentWidth, files.Count) + _extraLead;
        double step = FilmCardGeometry.Step(_tier, _horizontal);
        double itemLength = _horizontal ? FilmCardGeometry.Width(_tier) : FilmCardGeometry.Height(_tier);
        double target = FilmstripGeometry.CenterOffset(itemStart + index * step, itemLength, viewport, document);
        int targetPx = (int)Math.Round(target * density);
        int startPx = MainOffset;
        if (targetPx == startPx) return;
        bool reduceMotion = false;
        try
        {
            reduceMotion = Settings.Global.GetFloat(Context!.ContentResolver!, Settings.Global.AnimatorDurationScale, 1f) == 0f;
        }
        catch { /* 系统设置不可读时按正常动画处理 */ }
        if (reduceMotion || Math.Abs(targetPx - startPx) < 1)
        {
            ScrollBy(_horizontal ? targetPx - startPx : 0, _horizontal ? 0 : targetPx - startPx);
            return;
        }
        CancelAnimation();
        var animator = ValueAnimator.OfFloat(0f, 1f)!;
        animator.SetDuration((long)(FilmstripAnimation.DurationSeconds * 1000));
        animator.Update += (_, args) =>
        {
            if (request != _scrollRequestVersion || _userTouching)
            {
                CancelAnimation();
                return;
            }
            float linear = (float)(args.Animation?.AnimatedValue ?? 0f);
            double eased = FilmstripAnimation.EaseOutCubic(linear);
            int desired = (int)Math.Round(startPx + (targetPx - startPx) * eased);
            int delta = desired - MainOffset;
            if (delta != 0) ScrollBy(_horizontal ? delta : 0, _horizontal ? 0 : delta);
        };
        animator.AnimationEnd += (_, _) =>
        {
            if (ReferenceEquals(_animation, animator)) _animation = null;
            ReportVisibleRange();
        };
        _animation = animator;
        animator.Start();
    }

    /// <summary>终止动画并保留当前帧位置。</summary>
    private void CancelAnimation()
    {
        var animation = _animation;
        _animation = null;
        animation?.Cancel();
    }

    /// <summary>释放动画。</summary>
    /// <param name="disposing">是否释放托管资源。</param>
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            CancelAnimation();
            _adapter.Dispose();
        }
        base.Dispose(disposing);
    }

    /// <summary>卡片适配器。</summary>
    private sealed class FilmstripAdapter : RecyclerView.Adapter
    {
        private readonly AndroidFilmstripView _owner;
        private FilmCardTier _tier;

        /// <summary>绑定宿主。</summary>
        /// <param name="owner">胶片栏。</param>
        public FilmstripAdapter(AndroidFilmstripView owner)
        {
            _owner = owner;
            _tier = owner._tier;
        }

        /// <summary>当前档位（供创建/复用卡片时设置尺寸）。</summary>
        internal FilmCardTier Tier => _tier;

        /// <summary>切换档位并让已绑定的卡片重新测量。</summary>
        /// <param name="tier">新档位。</param>
        internal void SetTier(FilmCardTier tier)
        {
            if (_tier == tier) return;
            _tier = tier;
            NotifyDataSetChanged();
        }

        /// <summary>尺寸变化（换档/窗口重排）后刷新所有卡槽。</summary>
        internal void NotifyCardSizes() => NotifyDataSetChanged();

        /// <summary>项目数取自共享筛选列表。</summary>
        public override int ItemCount => _owner._main.FileVM.ThumbnailList.FilteredFiles.Count;

        /// <summary>创建/复用卡片。</summary>
        /// <param name="parent">父视图。</param>
        /// <param name="viewType">类型。</param>
        /// <returns>卡片持有者。</returns>
        public override RecyclerView.ViewHolder OnCreateViewHolder(ViewGroup parent, int viewType)
        {
            var context = parent.Context!;
            var card = new AndroidThumbnailCard(context);
            double density = AndroidUiTheme.Density(context);
            int width = (int)Math.Round(FilmCardGeometry.Width(_tier) * density);
            int height = (int)Math.Round(FilmCardGeometry.Height(_tier) * density);
            card.LayoutParameters = new RecyclerView.LayoutParams(width, height);
            return new CardHolder(card);
        }

        /// <summary>绑定业务身份并排队加载缩略图。</summary>
        /// <param name="holder">卡片持有者。</param>
        /// <param name="position">项目索引。</param>
        public override void OnBindViewHolder(RecyclerView.ViewHolder holder, int position)
        {
            var files = _owner._main.FileVM.ThumbnailList.FilteredFiles;
            if (position < 0 || position >= files.Count || holder is not CardHolder cardHolder) return;
            var file = files[position];
            double density = AndroidUiTheme.Density(_owner.Context!);
            int expectedWidth = (int)Math.Round(FilmCardGeometry.Width(_tier) * density);
            int expectedHeight = (int)Math.Round(FilmCardGeometry.Height(_tier) * density);
            var parameters = cardHolder.Card.LayoutParameters;
            if (parameters != null && (parameters.Width != expectedWidth || parameters.Height != expectedHeight))
            {
                parameters.Width = expectedWidth;
                parameters.Height = expectedHeight;
                cardHolder.Card.LayoutParameters = parameters;
            }
            cardHolder.Card.Bind(file, _owner._main.Settings.ShowRating,
                selected => _owner._main.FileVM.ThumbnailList.SelectImageCommand(selected),
                (selected, rating) => _owner.RateRequested?.Invoke(selected, rating),
                showSources: _ => _owner.SourcesRequested?.Invoke(cardHolder.Card));
            cardHolder.Card.SetTier(_tier);
            _owner._main.FileVM.ThumbnailList.QueueThumbnailLoad(file, true);
        }

        /// <summary>释放适配器持有的卡片。</summary>
        /// <param name="disposing">是否释放托管资源。</param>
        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
        }
    }

    /// <summary>卡片 ViewHolder。</summary>
    private sealed class CardHolder : RecyclerView.ViewHolder
    {
        /// <summary>卡片视图。</summary>
        internal AndroidThumbnailCard Card { get; }

        /// <summary>绑定卡片视图。</summary>
        /// <param name="view">卡片。</param>
        public CardHolder(AndroidThumbnailCard view) : base(view) => Card = view;
    }

    /// <summary>用户按下即接管自动定位。</summary>
    private sealed class TouchObserver : Java.Lang.Object, View.IOnTouchListener
    {
        private readonly AndroidFilmstripView _owner;

        /// <summary>绑定宿主。</summary>
        /// <param name="owner">胶片栏。</param>
        public TouchObserver(AndroidFilmstripView owner) => _owner = owner;

        /// <summary>触摸到列表即撤销自动定位与动画。</summary>
        /// <param name="view">列表。</param>
        /// <param name="e">事件。</param>
        /// <returns>不消费，交给 RecyclerView 滚动。</returns>
        public bool OnTouch(View? view, MotionEvent? e)
        {
            if (e == null) return false;
            if (e.ActionMasked is MotionEventActions.Down or MotionEventActions.Move)
            {
                _owner._userTouching = true;
                _owner.CancelScroll();
            }
            else if (e.ActionMasked is MotionEventActions.Up or MotionEventActions.Cancel)
            {
                _owner._userTouching = false;
            }
            return false;
        }
    }

    /// <summary>滚动后上报可见范围。</summary>
    private sealed class OnScrollObserver : RecyclerView.OnScrollListener
    {
        private readonly AndroidFilmstripView _owner;

        /// <summary>绑定宿主。</summary>
        /// <param name="owner">胶片栏。</param>
        public OnScrollObserver(AndroidFilmstripView owner) => _owner = owner;

        /// <summary>滚动中（程序化与手势）都上报。</summary>
        /// <param name="recyclerView">列表。</param>
        /// <param name="dx">横向位移。</param>
        /// <param name="dy">纵向位移。</param>
        public override void OnScrolled(RecyclerView recyclerView, int dx, int dy) => _owner.ReportVisibleRange();
    }
}
