using CoreGraphics;
using Foundation;
using PhotoViewer.Core.Settings;
using PhotoViewer.ViewModels.Settings;
using UIKit;

namespace PhotoViewer.iOS.Core;

/// <summary>布局模式选择：系统副标题单元格（名称 + 说明），当前项显示勾选；卡片高度跟随自适应行高。</summary>
internal sealed class iOSLayoutModePicker : UIView
{
    private readonly SettingsViewModel _settings;
    private readonly SettingsViewModel.LayoutModeItem[] _items;
    private readonly bool _phoneOnly;
    private readonly UITableView _table;
    private readonly NSLayoutConstraint _height;

    internal iOSLayoutModePicker(SettingsViewModel settings, bool phoneOnly = false)
    {
        _settings = settings;
        _phoneOnly = phoneOnly;
        _items = settings.LayoutModes.Where(item => !phoneOnly || item.Value == LayoutMode.Phone).ToArray();
        TranslatesAutoresizingMaskIntoConstraints = false;
        _table = new UITableView(CGRect.Empty, UITableViewStyle.Plain)
        {
            ScrollEnabled = false,
            BackgroundColor = UIColor.Clear,
            RowHeight = UITableView.AutomaticDimension,
            EstimatedRowHeight = 64,
            SeparatorInset = UIEdgeInsets.Zero,
            CellLayoutMarginsFollowReadableWidth = false,
            TableFooterView = new UIView(CGRect.Empty),
        };
        _table.Source = new Source(this);
        _height = HeightAnchor.ConstraintEqualTo(64 * _items.Length);
        _height.Active = true;
        AddSubview(_table);
    }

    public override void LayoutSubviews()
    {
        base.LayoutSubviews();
        _table.Frame = Bounds;
        _table.LayoutIfNeeded();
        nfloat content = _table.ContentSize.Height;
        if (content > 0 && Math.Abs((double)(content - _height.Constant)) > .5)
        {
            _height.Constant = content;
            Superview?.SetNeedsLayout();
        }
    }

    private sealed class Source(iOSLayoutModePicker owner) : UITableViewSource
    {
        public override nint RowsInSection(UITableView tableView, nint section) => owner._items.Length;

        public override UITableViewCell GetCell(UITableView tableView, NSIndexPath indexPath)
        {
            var item = owner._items[indexPath.Row];
            var cell = tableView.DequeueReusableCell("LayoutModeCell") ?? new UITableViewCell(UITableViewCellStyle.Subtitle, "LayoutModeCell");
            var config = UIListContentConfiguration.SubtitleCellConfiguration;
            config.Text = item.DisplayName;
            config.SecondaryText = item.Description;
            config.SecondaryTextProperties.NumberOfLines = 0;
            config.SecondaryTextProperties.Color = UIColor.SecondaryLabel;
            cell.ContentConfiguration = config;
            cell.Accessory = owner._phoneOnly || item.Value == owner._settings.LayoutMode
                ? UITableViewCellAccessory.Checkmark
                : UITableViewCellAccessory.None;
            cell.BackgroundColor = UIColor.Clear;
            iOSNativeSettingsMetrics.ApplyRowSeparator(tableView, cell, indexPath);
            return cell;
        }

        public override void RowSelected(UITableView tableView, NSIndexPath indexPath)
        {
            owner._settings.LayoutMode = owner._items[indexPath.Row].Value;
            tableView.DeselectRow(indexPath, true);
            tableView.ReloadData();
        }
    }
}
