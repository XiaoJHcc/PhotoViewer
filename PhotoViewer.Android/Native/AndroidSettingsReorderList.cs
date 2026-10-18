using System;
using System.Collections.Generic;
using Android.Content;
using Android.Graphics;
using Android.Graphics.Drawables;
using Android.Views;
using Android.Widget;
using AndroidX.RecyclerView.Widget;
using Google.Android.Material.CheckBox;
using PhotoViewer.Core.Design;

namespace PhotoViewer.Android.Native;

/// <summary>
/// 设置页可勾选可排序表（SETTINGS-15）：RecyclerView + ItemTouchHelper 拖拽，
/// 行尾把手是拖动载体；把手轻点打开「上移/下移」菜单，作为不拖拽的等价入口（SETTINGS-13）。
/// 表自身不滚动，高度由行数决定，滚动交给设置页。
/// </summary>
internal sealed class AndroidSettingsReorderList : RecyclerView
{
    private readonly Func<int> _count;
    private readonly Func<int, SettingsReorderItem> _itemAt;
    private readonly Action<int, int> _move;
    private readonly RowAdapter _adapter;
    private readonly ItemTouchHelper _touchHelper;

    /// <summary>建立可排序表。</summary>
    /// <param name="context">宿主上下文。</param>
    /// <param name="count">当前行数。</param>
    /// <param name="itemAt">按索引生成行模型（每次绑定时读取工作副本）。</param>
    /// <param name="move">把 from 行移动到 to 行（写回工作副本）。</param>
    public AndroidSettingsReorderList(Context context, Func<int> count,
        Func<int, SettingsReorderItem> itemAt, Action<int, int> move) : base(context)
    {
        _count = count;
        _itemAt = itemAt;
        _move = move;
        SetLayoutManager(new LinearLayoutManager(context, LinearLayoutManager.Vertical, false));
        NestedScrollingEnabled = false;
        OverScrollMode = OverScrollMode.Never;
        ClipToOutline = true;
        var background = new GradientDrawable();
        background.SetColor(Color.ParseColor("#262626"));
        background.SetCornerRadius((float)AndroidUiTheme.Dp(context, 10));
        Background = background;
        _adapter = new RowAdapter(this);
        SetAdapter(_adapter);
        AddItemDecoration(new Divider(context));
        _touchHelper = new ItemTouchHelper(new DragCallback(this));
        _touchHelper.AttachToRecyclerView(this);
    }

    /// <summary>行数。</summary>
    internal int RowCount => _count();

    /// <summary>创建行视图。</summary>
    internal RowView CreateRow() => new(Context!, this);

    /// <summary>按位置绑定行。</summary>
    internal void BindRow(RowView row, int position)
    {
        if (position < 0 || position >= _count()) return;
        row.Bind(_itemAt(position));
    }

    /// <summary>把行移动到目标位置并通知列表。</summary>
    internal void MoveRow(int from, int to)
    {
        int count = _count();
        if (from < 0 || from >= count || to < 0 || to >= count || from == to) return;
        _move(from, to);
        _adapter.NotifyItemMoved(from, to);
    }

    /// <summary>开始拖动指定行。</summary>
    internal void StartDrag(View row)
    {
        var holder = GetChildViewHolder(row);
        if (holder != null) _touchHelper.StartDrag(holder);
    }

    /// <summary>行内排序菜单（上移/下移），不占用常驻按钮。</summary>
    internal void ShowMoveMenu(RowView row)
    {
        int position = GetChildAdapterPosition(row);
        int count = _count();
        if (position == NoPosition) return;
        var menu = new PopupMenu(Context!, row.HandleView);
        var up = menu.Menu!.Add(0, 0, 0, "上移")!;
        up.SetEnabled(position > 0);
        var down = menu.Menu!.Add(0, 1, 1, "下移")!;
        down.SetEnabled(position < count - 1);
        menu.MenuItemClick += (_, args) =>
        {
            if (args.Item?.ItemId == 0) MoveRow(position, position - 1);
            else if (args.Item?.ItemId == 1) MoveRow(position, position + 1);
        };
        menu.Show();
    }

    /// <summary>行模型：勾选、主文本、可选副文本与读屏描述。</summary>
    internal sealed class SettingsReorderItem
    {
        internal SettingsReorderItem(string title, string? subtitle, Func<bool> getChecked,
            Action<bool> setChecked, string? accessibilityDescription = null)
        {
            Title = title;
            Subtitle = subtitle;
            GetChecked = getChecked;
            SetChecked = setChecked;
            AccessibilityDescription = accessibilityDescription;
        }

        internal string Title { get; }
        internal string? Subtitle { get; }
        internal Func<bool> GetChecked { get; }
        internal Action<bool> SetChecked { get; }
        internal string? AccessibilityDescription { get; }
    }

    /// <summary>设配器：行数取工作副本，内容在绑定时读取。</summary>
    private sealed class RowAdapter : RecyclerView.Adapter
    {
        private readonly AndroidSettingsReorderList _owner;

        public RowAdapter(AndroidSettingsReorderList owner) => _owner = owner;

        public override int ItemCount => _owner._count();

        public override RecyclerView.ViewHolder OnCreateViewHolder(ViewGroup parent, int viewType)
            => new RowHolder(_owner.CreateRow());

        public override void OnBindViewHolder(RecyclerView.ViewHolder holder, int position)
        {
            if (holder is RowHolder rowHolder) _owner.BindRow(rowHolder.Row, position);
        }
    }

    /// <summary>行 Holder。</summary>
    private sealed class RowHolder : RecyclerView.ViewHolder
    {
        internal RowView Row { get; }

        public RowHolder(RowView row) : base(row) => Row = row;
    }

    /// <summary>行：勾选框、标题/副标题、行尾把手；把手轻点开菜单，长按开始拖拽。</summary>
    internal sealed class RowView : LinearLayout
    {
        private readonly AndroidSettingsReorderList _owner;
        private readonly MaterialCheckBox _check;
        private readonly TextView _title;
        private readonly TextView _subtitle;
        private readonly TextView _handle;
        private SettingsReorderItem? _item;
        private bool _binding;

        internal RowView(Context context, AndroidSettingsReorderList owner) : base(context)
        {
            _owner = owner;
            Orientation = Orientation.Horizontal;
            SetGravity(GravityFlags.CenterVertical);
            LayoutParameters = new RecyclerView.LayoutParams(
                ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent);
            double density = AndroidUiTheme.Density(context);
            SetPadding((int)(16 * density), (int)(10 * density), (int)(8 * density), (int)(10 * density));
            SetMinimumHeight((int)(52 * density));

            _check = new MaterialCheckBox(context);
            _check.CheckedChange += (_, args) =>
            {
                if (_binding) return;
                _item?.SetChecked(args.IsChecked);
                UpdateDescription();
            };
            var column = new LinearLayout(context) { Orientation = Orientation.Vertical };
            _title = new TextView(context) { TextSize = 15 };
            _title.SetTextColor(AndroidUiTheme.TextPrimary);
            _subtitle = new TextView(context) { TextSize = 12 };
            _subtitle.SetTextColor(AndroidUiTheme.TextSecondary);
            _subtitle.SetPadding(0, (int)(2 * density), 0, 0);
            column.AddView(_title, new LayoutParams(LayoutParams.MatchParent, LayoutParams.WrapContent));
            column.AddView(_subtitle, new LayoutParams(LayoutParams.MatchParent, LayoutParams.WrapContent));

            _handle = new TextView(context)
            {
                Text = "≡",
                TextSize = 22,
                Gravity = GravityFlags.Center,
            };
            _handle.SetTextColor(AndroidUiTheme.TextSecondary);
            _handle.Clickable = true;
            _handle.Focusable = true;
            _handle.LongClickable = true;
            _handle.ContentDescription = "排序把手：轻点打开上移/下移菜单，长按拖动";
            _handle.Click += (_, _) => _owner.ShowMoveMenu(this);
            _handle.LongClick += (_, args) =>
            {
                args.Handled = true;
                _owner.StartDrag(this);
            };

            var checkboxSize = (int)AndroidUiTheme.Dp(context, 44);
            AddView(_check, new LayoutParams(checkboxSize, checkboxSize));
            AddView(column, new LayoutParams(0, LayoutParams.WrapContent, 1));
            AddView(_handle, new LayoutParams(checkboxSize, checkboxSize));
        }

        /// <summary>排序把手的锚点视图。</summary>
        internal View HandleView => _handle;

        /// <summary>绑定行模型；程序化勾选不写回工作副本。</summary>
        internal void Bind(SettingsReorderItem item)
        {
            _item = item;
            _binding = true;
            _check.Checked = item.GetChecked();
            _binding = false;
            _title.Text = item.Title;
            bool hasSubtitle = !string.IsNullOrWhiteSpace(item.Subtitle);
            _subtitle.Text = item.Subtitle ?? "";
            _subtitle.Visibility = hasSubtitle ? ViewStates.Visible : ViewStates.Gone;
            UpdateDescription();
        }

        /// <summary>同步行与勾选框的读屏描述；勾选变化后立即广播新状态。</summary>
        private void UpdateDescription()
        {
            if (_item == null) return;
            string description = _item.AccessibilityDescription
                ?? $"{_item.Title}，{(_item.GetChecked() ? "已启用" : "未启用")}";
            ContentDescription = description;
            _check.ContentDescription = description;
            _handle.ContentDescription = $"{_item.Title} 排序把手：轻点打开上移/下移菜单，长按拖动";
        }
    }

    /// <summary>行间 1dp 分隔线。</summary>
    private sealed class Divider : RecyclerView.ItemDecoration
    {
        private readonly int _height;
        private readonly Paint _paint;

        internal Divider(Context context)
        {
            _height = Math.Max(1, (int)AndroidUiTheme.Dp(context, DesignTokens.ExiftableDividerThickness));
            _paint = AndroidUiTheme.Paint(AndroidUiTheme.FromToken(DesignTokens.ColorDivider));
        }

        public override void OnDraw(Canvas canvas, RecyclerView parent, RecyclerView.State state)
        {
            for (int index = 0; index < parent.ChildCount - 1; index++)
            {
                var child = parent.GetChildAt(index);
                if (child == null) continue;
                float y = child.Bottom + child.TranslationY;
                canvas.DrawRect(parent.PaddingLeft, y,
                    parent.Width - parent.PaddingRight, y + _height, _paint);
            }
        }
    }

    /// <summary>拖拽回调：仅上下整行移动，删除上/下按钮。</summary>
    private sealed class DragCallback : ItemTouchHelper.Callback
    {
        private readonly AndroidSettingsReorderList _owner;

        internal DragCallback(AndroidSettingsReorderList owner) => _owner = owner;

        public override int GetMovementFlags(RecyclerView recyclerView, RecyclerView.ViewHolder viewHolder)
            => MakeMovementFlags(ItemTouchHelper.Up | ItemTouchHelper.Down, 0);

        public override bool OnMove(RecyclerView recyclerView, RecyclerView.ViewHolder source,
            RecyclerView.ViewHolder target)
        {
            int from = source.BindingAdapterPosition;
            int to = target.BindingAdapterPosition;
            if (from == RecyclerView.NoPosition || to == RecyclerView.NoPosition || from == to) return false;
            _owner.MoveRow(from, to);
            return true;
        }

        public override void OnSwiped(RecyclerView.ViewHolder viewHolder, int direction) { }

        // SETTINGS-15：拖动只由行尾把手触发，整行长按不误触。
        public override bool IsLongPressDragEnabled => false;

        public override bool IsItemViewSwipeEnabled => false;
    }
}
