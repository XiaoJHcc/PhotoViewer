using AppKit;
using CoreGraphics;
using Foundation;

namespace PhotoViewer.Mac.Native;

/// <summary>AppKit 视图式排序表格；复选框、名称与行内编辑器各占独立列。</summary>
public sealed class MacReorderList : NSScrollView
{
    private readonly NSTableView _table;
    private readonly Source _source;
    private readonly RowsDelegate _delegate;
    private readonly bool _hasEditors;

    /// <summary>将共享集合绑定到原生勾选、拖放与可选行内编辑器，返回完整列表。</summary>
    public static MacReorderList Create<TItem>(IList<TItem> items, Func<TItem, string> title, Func<TItem, bool> enabled,
        Action<TItem, bool> setEnabled, Action<int, int> move, Func<TItem, bool, NSView>? editor = null)
        => new(() => items.Count, index => title(items[index]), index => enabled(items[index]),
            (index, value) => setEnabled(items[index], value), move,
            editor == null ? null : (index, primary) => editor(items[index], primary));

    /// <summary>创建系统表格与本地拖动数据源，名称区域承担行选择和拖动。</summary>
    private MacReorderList(Func<int> count, Func<int, string> title, Func<int, bool> enabled,
        Action<int, bool> setEnabled, Action<int, int> move, Func<int, bool, NSView>? editor)
    {
        _hasEditors = editor != null;
        Frame = new CGRect(0, 0, 500, Math.Min(_hasEditors ? 440 : 340, count() * 36 + (_hasEditors ? 28 : 4)));
        HasVerticalScroller = true;
        ScrollerStyle = NSScrollerStyle.Overlay;
        BorderType = NSBorderType.NoBorder;
        DrawsBackground = false;
        AutomaticallyAdjustsContentInsets = false;
        _table = new NSTableView
        {
            HeaderView = _hasEditors ? new NSTableHeaderView() : null, RowHeight = 34,
            IntercellSpacing = new CGSize(0, 2), Style = NSTableViewStyle.FullWidth,
            BackgroundColor = NSColor.Clear, AllowsMultipleSelection = false,
            AllowsColumnReordering = false, AllowsColumnResizing = false,
            ColumnAutoresizingStyle = NSTableViewColumnAutoresizingStyle.None,
        };
        _table.AddColumn(new NSTableColumn("Enabled") { Title = "", Width = 32, MinWidth = 32, MaxWidth = 32 });
        _table.AddColumn(new NSTableColumn("Name") { Title = "功能", Width = 240, MinWidth = 100 });
        if (_hasEditors)
        {
            _table.AddColumn(new NSTableColumn("Primary") { Title = "主要快捷键", Width = 116, MinWidth = 116, MaxWidth = 116 });
            _table.AddColumn(new NSTableColumn("Secondary") { Title = "次要快捷键", Width = 116, MinWidth = 116, MaxWidth = 116 });
        }
        _source = new Source(count, move);
        _delegate = new RowsDelegate(title, enabled, setEnabled, editor);
        _table.DataSource = _source;
        _table.Delegate = _delegate;
        DocumentView = _table;
        _table.RegisterForDraggedTypes([Source.DragType]);
        _table.SetDraggingSourceOperationMask(NSDragOperation.Move, true);
        _table.Frame = new CGRect(0, 0, 500, count() * 36);
        _table.ReloadData();
    }

    /// <summary>供设置编辑和原生验收访问同一个系统表格。</summary>
    internal NSTableView Table => _table;

    /// <summary>刷新行内快捷键和冲突标记，保留当前滚动与行选择。</summary>
    internal void RefreshRows() => _table.ReloadData();

    /// <summary>固定勾选与快捷键列宽，名称列使用剩余空间，文档跟随裁剪视口宽度。</summary>
    public override void SetFrameSize(CGSize newSize)
    {
        base.SetFrameSize(newSize);
        if (_table == null) return;
        nfloat width = ContentView.Bounds.Width;
        _table.TableColumns()[1].Width = (nfloat)Math.Max(100, (double)width - 32 - (_hasEditors ? 232 : 0));
        _table.Frame = new CGRect(0, 0, width, _table.Frame.Height);
    }

    /// <summary>关闭分类或窗口时断开原生数据源，释放工作设置集合引用。</summary>
    protected override void Dispose(bool disposing)
    {
        if (disposing) { _table.DataSource = null; _table.Delegate = null; _source.Dispose(); _delegate.Dispose(); }
        base.Dispose(disposing);
    }

    /// <summary>使用逐行 pasteboard writer 生成原生拖动预览和行间插入反馈。</summary>
    private sealed class Source(Func<int> count, Action<int, int> move) : NSTableViewDataSource
    {
        internal const string DragType = "cc.xiaojh.PhotoViewer.settings-order";

        /// <summary>返回共享集合行数。</summary>
        public override nint GetRowCount(NSTableView tableView) => count();

        /// <summary>为原生拖动会话生成源行索引，允许系统自动滚动。</summary>
        public override INSPasteboardWriting GetPasteboardWriterForRow(NSTableView tableView, nint row)
        {
            var item = new NSPasteboardItem();
            item.SetStringForType(row.ToString(), DragType);
            return item;
        }

        /// <summary>只接受同一表格的有效源行；落在行上时转为行间插入。</summary>
        public override NSDragOperation ValidateDrop(NSTableView tableView, INSDraggingInfo info, nint row, NSTableViewDropOperation operation)
        {
            if (info.DraggingSource?.Handle != tableView.Handle || !TryReadRow(info, out _) || row < 0 || row > count()) return NSDragOperation.None;
            tableView.SetDropRowDropOperation(Math.Clamp(row, 0, count()), NSTableViewDropOperation.Above);
            return NSDragOperation.Move;
        }

        /// <summary>将插入位置换算为移除后的索引并移动，更新选择和真实行视图。</summary>
        public override bool AcceptDrop(NSTableView tableView, INSDraggingInfo info, nint row, NSTableViewDropOperation operation)
        {
            if (info.DraggingSource?.Handle != tableView.Handle || !TryReadRow(info, out int source) || row < 0 || row > count()) return false;
            int target = Math.Clamp((int)row - (row > source ? 1 : 0), 0, count() - 1);
            if (target != source) move(source, target);
            tableView.ReloadData();
            tableView.SelectRow(target, false);
            return true;
        }

        /// <summary>从当前拖动会话读取并校验索引，避免使用上一轮拖动的残留状态。</summary>
        private bool TryReadRow(INSDraggingInfo info, out int row)
            => int.TryParse(info.DraggingPasteboard.GetStringForType(DragType), out row) && row >= 0 && row < count();
    }

    /// <summary>原生独立单元格缩小复选框点击范围，使名称区域可顺畅选择和拖动。</summary>
    private sealed class RowsDelegate(Func<int, string> title, Func<int, bool> enabled,
        Action<int, bool> setEnabled, Func<int, bool, NSView>? editor) : NSTableViewDelegate
    {
        /// <summary>按列创建复选框、名称或编辑按钮，复用同一共享项目的语义。</summary>
        public override NSView GetViewForItem(NSTableView tableView, NSTableColumn? tableColumn, nint row)
        {
            int index = (int)row;
            string column = tableColumn?.Identifier ?? "Name";
            if (column is "Primary" or "Secondary") return editor!(index, column == "Primary");
            if (column == "Enabled")
            {
                var checkbox = NSButton.CreateCheckbox("", () => { });
                checkbox.State = enabled(index) ? NSCellStateValue.On : NSCellStateValue.Off;
                checkbox.ToolTip = title(index);
                checkbox.Activated += (_, _) => setEnabled(index, checkbox.State == NSCellStateValue.On);
                return checkbox;
            }
            var label = NSTextField.CreateLabel(title(index));
            label.Font = NSFont.SystemFontOfSize(13) ?? throw new InvalidOperationException("系统字体不可用");
            label.TextColor = NSColor.Label;
            label.LineBreakMode = NSLineBreakMode.TruncatingTail;
            label.ToolTip = title(index);
            var cell = new NSTableCellView(new CGRect(0, 0, 240, 34)) { TextField = label };
            label.Frame = new CGRect(4, 8, 232, 20);
            label.AutoresizingMask = NSViewResizingMask.WidthSizable;
            cell.AddSubview(label);
            return cell;
        }
    }
}
