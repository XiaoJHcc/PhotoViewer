using AppKit;
using CoreGraphics;
using Foundation;

namespace PhotoViewer.Mac.Native;

/// <summary>设置页通用原生可勾选排序表格，支持拖动和系统滚动。</summary>
public sealed class MacReorderList : NSScrollView
{
    private readonly NSTableView _table;
    private readonly Source _source;
    private readonly RowsDelegate _delegate;
    /// <summary>泛型仅在工厂中使用，平台 NSObject 子类保持非泛型，满足原生注册约束。</summary>
    public static MacReorderList Create<TItem>(IList<TItem> items, Func<TItem, string> title, Func<TItem, bool> enabled, Action<TItem, bool> setEnabled, Action<int, int> move)
        => new(() => items.Count, index => title(items[index]), index => enabled(items[index]), (index, value) => setEnabled(items[index], value), move);
    /// <summary>创建列表并绑定共享集合与勾选/移动操作。</summary>
    private MacReorderList(Func<int> count, Func<int, string> title, Func<int, bool> enabled, Action<int, bool> setEnabled, Action<int, int> move)
    {
        Frame = new CGRect(0, 0, 500, Math.Min(260, count() * 34 + 12));
        HasVerticalScroller = true; BorderType = NSBorderType.BezelBorder;
        _table = new NSTableView { HeaderView = null, RowHeight = 34 };
        _table.AddColumn(new NSTableColumn("Value") { Width = 500 });
        _source = new Source(count, move); _delegate = new RowsDelegate(title, enabled, setEnabled);
        _table.DataSource = _source; _table.Delegate = _delegate; DocumentView = _table;
        _table.RegisterForDraggedTypes(["PhotoViewer.Settings.Order"]);
        _table.SetDraggingSourceOperationMask(NSDragOperation.Move, true);
        _table.Frame = new CGRect(0, 0, 480, count() * 34);
        _table.ReloadData();
    }
    /// <summary>让原生列随表单宽度变化。</summary>
    public override void SetFrameSize(CGSize newSize)
    {
        base.SetFrameSize(newSize);
        if (_table != null) { _table.TableColumns()[0].Width = (nfloat)Math.Max(100, (double)newSize.Width - 20); _table.Frame = new CGRect(0, 0, newSize.Width, _table.Frame.Height); }
    }
    /// <summary>关闭分类或窗口时断开原生数据源，释放对工作设置集合的引用。</summary>
    protected override void Dispose(bool disposing)
    {
        if (disposing) { _table.DataSource = null; _table.Delegate = null; _source.Dispose(); _delegate.Dispose(); }
        base.Dispose(disposing);
    }
    /// <summary>列表数据及原生拖放移动。</summary>
    private sealed class Source(Func<int> count, Action<int, int> move) : NSTableViewDataSource
    {
        private int _dragRow = -1;
        /// <summary>集合行数。</summary>
        public override nint GetRowCount(NSTableView tableView) => count();
        /// <summary>开始拖动时记录源索引，并写入本地 pasteboard 类型。</summary>
        public override bool WriteRows(NSTableView tableView, NSIndexSet rowIndexes, NSPasteboard pasteboard)
        {
            _dragRow = (int)rowIndexes.FirstIndex;
            pasteboard.DeclareTypes(["PhotoViewer.Settings.Order"], null); pasteboard.SetStringForType(_dragRow.ToString(), "PhotoViewer.Settings.Order"); return true;
        }
        /// <summary>只允许在行之间移动同一表格项。</summary>
        public override NSDragOperation ValidateDrop(NSTableView tableView, INSDraggingInfo info, nint row, NSTableViewDropOperation operation)
            => _dragRow >= 0 && operation == NSTableViewDropOperation.Above ? NSDragOperation.Move : NSDragOperation.None;
        /// <summary>将插入位置换算为移除后的集合索引，调用共享移动方法。</summary>
        public override bool AcceptDrop(NSTableView tableView, INSDraggingInfo info, nint row, NSTableViewDropOperation operation)
        {
            if (_dragRow < 0) return false;
            int target = (int)row; if (target > _dragRow) target--;
            target = Math.Clamp(target, 0, count() - 1);
            if (target != _dragRow) move(_dragRow, target);
            _dragRow = -1; tableView.ReloadData(); return true;
        }
    }
    /// <summary>每行使用原生复选框，不模拟开关外观。</summary>
    private sealed class RowsDelegate(Func<int, string> title, Func<int, bool> enabled, Action<int, bool> setEnabled) : NSTableViewDelegate
    {
        /// <summary>复用集合项语义构建原生勾选行。</summary>
        public override NSView GetViewForItem(NSTableView tableView, NSTableColumn? tableColumn, nint row)
        {
            int index = (int)row;
            var checkbox = NSButton.CreateCheckbox(title(index), () => { });
            checkbox.State = enabled(index) ? NSCellStateValue.On : NSCellStateValue.Off;
            checkbox.Activated += (_, _) => setEnabled(index, checkbox.State == NSCellStateValue.On);
            return checkbox;
        }
    }
}
