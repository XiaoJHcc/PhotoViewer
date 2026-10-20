using System.Globalization;
using PhotoViewer.ViewModels.Settings;
using UIKit;

namespace PhotoViewer.iOS.Core;

internal sealed class iOSScalePresetEditor : UIStackView
{
    private readonly SettingsViewModel _settings;
    private readonly Dictionary<SettingsViewModel.ScalePreset, (UIStackView Row, UITextField Field, UIButton Delete)> _rows = [];
    private readonly UILabel _error = new() { TextColor = UIColor.SystemRed, Lines = 0, Hidden = true };
    internal UIButton AddButton { get; }
    internal IReadOnlyList<UITextField> Fields => _settings.ScalePresets.Select(preset => _rows[preset].Field).ToArray();
    internal bool HasValidationError => !_error.Hidden;
    internal IReadOnlyList<UIButton> DeleteButtons => _settings.ScalePresets.Select(preset => _rows[preset].Delete).ToArray();

    internal iOSScalePresetEditor(SettingsViewModel settings)
    {
        _settings = settings;
        Axis = UILayoutConstraintAxis.Vertical;
        Spacing = 1;
        TranslatesAutoresizingMaskIntoConstraints = false;
        AddButton = IconButton("plus.circle", "新增100%预设");
        AddButton.TouchUpInside += (_, _) =>
        {
            this.EndEditing(true);
            _settings.AddScalePreset();
            _settings.SortScalePreset();
            SyncRows();
        };
        AddArrangedSubview(_error);
        AddArrangedSubview(AddButton);
        SyncRows();
    }

    private void SyncRows()
    {
        foreach (var stale in _rows.Keys.Where(preset => !_settings.ScalePresets.Contains(preset)).ToArray())
        {
            var row = _rows[stale].Row;
            RemoveArrangedSubview(row);
            row.RemoveFromSuperview();
            _rows.Remove(stale);
        }
        foreach (var preset in _settings.ScalePresets)
        {
            if (!_rows.ContainsKey(preset)) CreateRow(preset);
            _rows[preset].Delete.Enabled = _settings.CanRemoveScalePreset;
        }
        ReorderRows();
    }

    private void ReorderRows()
    {
        for (int index = 0; index < _settings.ScalePresets.Count; index++)
        {
            var row = _rows[_settings.ScalePresets[index]].Row;
            if (Array.IndexOf(ArrangedSubviews, row) == index) continue;
            RemoveArrangedSubview(row);
            InsertArrangedSubview(row, (nuint)index);
        }
    }

    private void CreateRow(SettingsViewModel.ScalePreset preset)
    {
        var row = new UIStackView
        {
            Axis = UILayoutConstraintAxis.Horizontal,
            Alignment = UIStackViewAlignment.Center,
            Spacing = 8,
            LayoutMarginsRelativeArrangement = true,
            LayoutMargins = new UIEdgeInsets(10, 16, 10, 16),
        };
        var field = new UITextField
        {
            Text = preset.Text,
            KeyboardType = UIKeyboardType.DecimalPad,
            BorderStyle = UITextBorderStyle.RoundedRect,
            TextAlignment = UITextAlignment.Center,
            AccessibilityLabel = "缩放比例预设",
        };
        field.WidthAnchor.ConstraintEqualTo(96).Active = true;
        var toolbar = new UIToolbar();
        toolbar.SizeToFit();
        toolbar.Items = [new UIBarButtonItem(UIBarButtonSystemItem.FlexibleSpace), new UIBarButtonItem(UIBarButtonSystemItem.Done, (_, _) => field.ResignFirstResponder())];
        field.InputAccessoryView = toolbar;
        field.ShouldReturn = _ => { field.ResignFirstResponder(); return true; };
        field.EditingDidEnd += (_, _) => Commit(preset, field);
        var delete = IconButton("minus.circle", "删除预设");
        delete.TouchUpInside += (_, _) =>
        {
            this.EndEditing(true);
            if (!_settings.CanRemoveScalePreset) return;
            _settings.RemoveScalePreset(preset);
            SyncRows();
        };
        row.AddArrangedSubview(field);
        row.AddArrangedSubview(new UILabel { Text = "%", TextColor = UIColor.SecondaryLabel });
        row.AddArrangedSubview(new UIView());
        row.AddArrangedSubview(delete);
        _rows[preset] = (row, field, delete);
        InsertArrangedSubview(row, (nuint)Math.Max(0, ArrangedSubviews.Length - 2));
    }

    private void Commit(SettingsViewModel.ScalePreset preset, UITextField field)
    {
        bool valid = (double.TryParse(field.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double value)
            || double.TryParse(field.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out value))
            && SettingsViewModel.IsValidScalePresetPercent(value);
        _error.Hidden = valid;
        _error.Text = valid ? null : "请输入大于0且不超过1600%的数字";
        if (valid)
        {
            preset.Text = value.ToString("0.###", CultureInfo.InvariantCulture);
            _settings.ApplyScalePreset();
        }
        field.Text = preset.Text;
        BeginInvokeOnMainThread(ReorderRows);
    }

    private static UIButton IconButton(string symbol, string label)
    {
        var button = new UIButton(UIButtonType.System) { AccessibilityLabel = label };
        button.SetImage(UIImage.GetSystemImage(symbol), UIControlState.Normal);
        button.WidthAnchor.ConstraintEqualTo(44).Active = true;
        button.HeightAnchor.ConstraintEqualTo(44).Active = true;
        return button;
    }
}
