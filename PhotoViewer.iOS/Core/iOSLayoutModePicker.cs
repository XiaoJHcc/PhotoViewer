using CoreGraphics;
using Foundation;
using PhotoViewer.ViewModels.Settings;
using UIKit;

namespace PhotoViewer.iOS.Core;

internal sealed class iOSLayoutModePicker : UIView
{
    private readonly SettingsViewModel _settings;
    private readonly UITableView _table;
    internal iOSLayoutModePicker(SettingsViewModel settings)
    {
        _settings = settings;
        TranslatesAutoresizingMaskIntoConstraints = false;
        _table = new UITableView(CGRect.Empty, UITableViewStyle.Plain)
        {
            ScrollEnabled = false,
            BackgroundColor = UIColor.Clear,
            RowHeight = 68,
        };
        _table.Source = new Source(this);
        HeightAnchor.ConstraintEqualTo(204).Active = true;
        AddSubview(_table);
    }
    public override void LayoutSubviews() { base.LayoutSubviews(); _table.Frame = Bounds; }
    private sealed class Source(iOSLayoutModePicker owner) : UITableViewSource
    {
        public override nint RowsInSection(UITableView tableView, nint section) => owner._settings.LayoutModes.Count;
        public override UITableViewCell GetCell(UITableView tableView, NSIndexPath indexPath)
        {
            var item = owner._settings.LayoutModes[indexPath.Row];
            var cell = new UITableViewCell(UITableViewCellStyle.Subtitle, null);
            var config = UIListContentConfiguration.SubtitleCellConfiguration;
            config.Text = item.DisplayName;
            config.SecondaryText = item.Description;
            config.SecondaryTextProperties.NumberOfLines = 0;
            cell.ContentConfiguration = config;
            cell.Accessory = item.Value == owner._settings.LayoutMode ? UITableViewCellAccessory.Checkmark : UITableViewCellAccessory.None;
            cell.BackgroundColor = UIColor.Clear;
            return cell;
        }
        public override void RowSelected(UITableView tableView, NSIndexPath indexPath)
        {
            owner._settings.LayoutMode = owner._settings.LayoutModes[indexPath.Row].Value;
            tableView.DeselectRow(indexPath, true);
            tableView.ReloadData();
        }
    }
}
