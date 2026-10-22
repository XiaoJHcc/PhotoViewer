using System.Globalization;
using Foundation;
using PhotoViewer.ViewModels.Settings;
using UIKit;

namespace PhotoViewer.iOS.Core;

internal sealed class iOSScalePresetEditor : UIStackView
{
    private readonly SettingsViewModel _settings;
    private readonly Dictionary<SettingsViewModel.ScalePreset, (UIView Row, UITextField Field, UIButton Delete)> _rows = [];
    private readonly UILabel _error = new() { TextColor = UIColor.SystemRed, Lines = 0, Hidden = true };
    internal UIButton AddButton { get; }
    internal IReadOnlyList<UITextField> Fields => _settings.ScalePresets.Select(preset => _rows[preset].Field).ToArray();
    internal bool HasValidationError => !_error.Hidden;
    private readonly UIView _errorRow;
    private readonly UIView _addRow;

    /// <summary>预设行的输入组（输入框 + %），验收核对其水平居中。</summary>
    internal IReadOnlyList<UIView> FieldGroups => _settings.ScalePresets.Select(preset => _rows[preset].Field.Superview!).ToArray();
    internal IReadOnlyList<UIButton> DeleteButtons => _settings.ScalePresets.Select(preset => _rows[preset].Delete).ToArray();

    internal iOSScalePresetEditor(SettingsViewModel settings)
    {
        _settings = settings;
        Axis = UILayoutConstraintAxis.Vertical;
        Spacing = 0;
        TranslatesAutoresizingMaskIntoConstraints = false;
        AddButton = IconButton("plus.circle.fill", UIColor.SystemGreen, "新增100%预设");
        AddButton.TouchUpInside += (_, _) =>
        {
            this.EndEditing(true);
            _settings.AddScalePreset();
            _settings.SortScalePreset();
            SyncRows();
        };
        _error.Font = UIFont.PreferredFootnote!;
        AddArrangedSubview(_errorRow = WrapError());
        AddArrangedSubview(_addRow = CreateAddRow());
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
        var field = new UITextField
        {
            Text = preset.Text,
            KeyboardType = UIKeyboardType.DecimalPad,
            BorderStyle = UITextBorderStyle.RoundedRect,
            TextAlignment = UITextAlignment.Center,
            AccessibilityLabel = "缩放比例预设",
            TranslatesAutoresizingMaskIntoConstraints = false,
        };
        field.WidthAnchor.ConstraintEqualTo(96).Active = true;
        var toolbar = new UIToolbar();
        toolbar.SizeToFit();
        toolbar.Items = [new UIBarButtonItem(UIBarButtonSystemItem.FlexibleSpace), new UIBarButtonItem(UIBarButtonSystemItem.Done, (_, _) => field.ResignFirstResponder())];
        field.InputAccessoryView = toolbar;
        field.ShouldReturn = _ => { field.ResignFirstResponder(); return true; };
        field.EditingDidEnd += (_, _) => Commit(preset, field);
        var delete = IconButton("minus.circle.fill", UIColor.SystemRed, "删除预设");
        delete.TouchUpInside += (_, _) =>
        {
            this.EndEditing(true);
            if (!_settings.CanRemoveScalePreset) return;
            _settings.RemoveScalePreset(preset);
            SyncRows();
        };
        var group = new UIStackView([field, new UILabel { Text = "%", TextColor = UIColor.SecondaryLabel }])
        {
            Axis = UILayoutConstraintAxis.Horizontal,
            Alignment = UIStackViewAlignment.Center,
            Spacing = 6,
            TranslatesAutoresizingMaskIntoConstraints = false,
        };
        var row = CreateRowContainer(withSeparator: true);
        row.AddSubview(group);
        row.AddSubview(delete);
        NSLayoutConstraint.ActivateConstraints(
        [
            group.CenterXAnchor.ConstraintEqualTo(row.CenterXAnchor),
            group.CenterYAnchor.ConstraintEqualTo(row.CenterYAnchor),
            field.HeightAnchor.ConstraintGreaterThanOrEqualTo(34),
            delete.TrailingAnchor.ConstraintEqualTo(row.TrailingAnchor, (nfloat)(-TrailingInset)),
            delete.CenterYAnchor.ConstraintEqualTo(row.CenterYAnchor),
        ]);
        _rows[preset] = (row, field, delete);
        InsertArrangedSubview(row, (nuint)Math.Max(0, ArrangedSubviews.Length - 2));
    }

    private UIView CreateAddRow()
    {
        var row = CreateRowContainer(withSeparator: false);
        row.AddSubview(AddButton);
        NSLayoutConstraint.ActivateConstraints(
        [
            AddButton.TrailingAnchor.ConstraintEqualTo(row.TrailingAnchor, (nfloat)(-TrailingInset)),
            AddButton.CenterYAnchor.ConstraintEqualTo(row.CenterYAnchor),
        ]);
        return row;
    }

    private UIView WrapError()
    {
        var row = new UIView { TranslatesAutoresizingMaskIntoConstraints = false, Hidden = true };
        _error.TranslatesAutoresizingMaskIntoConstraints = false;
        row.AddSubview(_error);
        NSLayoutConstraint.ActivateConstraints(
        [
            _error.TopAnchor.ConstraintEqualTo(row.TopAnchor, 8),
            _error.BottomAnchor.ConstraintEqualTo(row.BottomAnchor, -8),
            _error.LeadingAnchor.ConstraintEqualTo(row.LeadingAnchor, 16),
            _error.TrailingAnchor.ConstraintEqualTo(row.TrailingAnchor, -16),
        ]);
        return row;
    }

    /// <summary>标准 52 点行；预设行底部带与卡片一致的发丝分隔线。</summary>
    private static UIView CreateRowContainer(bool withSeparator)
    {
        var row = new UIView { TranslatesAutoresizingMaskIntoConstraints = false };
        row.HeightAnchor.ConstraintEqualTo(52).Active = true;
        if (withSeparator)
        {
            var separator = new UIView { BackgroundColor = UIColor.Separator, TranslatesAutoresizingMaskIntoConstraints = false };
            row.AddSubview(separator);
            NSLayoutConstraint.ActivateConstraints(
            [
                separator.LeadingAnchor.ConstraintEqualTo(row.LeadingAnchor),
                separator.TrailingAnchor.ConstraintEqualTo(row.TrailingAnchor),
                separator.BottomAnchor.ConstraintEqualTo(row.BottomAnchor),
                separator.HeightAnchor.ConstraintEqualTo(1 / UIScreen.MainScreen.Scale),
            ]);
        }
        return row;
    }

    private void Commit(SettingsViewModel.ScalePreset preset, UITextField field)
    {
        bool valid = (double.TryParse(field.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double value)
            || double.TryParse(field.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out value))
            && SettingsViewModel.IsValidScalePresetPercent(value);
        _error.Hidden = valid;
        _errorRow.Hidden = valid;
        _error.Text = valid ? null : "请输入大于0且不超过1600%的数字";
        if (valid)
        {
            preset.Text = value.ToString("0.###", CultureInfo.InvariantCulture);
            _settings.ApplyScalePreset();
        }
        field.Text = preset.Text;
        BeginInvokeOnMainThread(ReorderRows);
    }

    /// <summary>行尾按钮右边距：44 点命中区内图标与卡片右缘留出标准 16 点。</summary>
    private const double TrailingInset = 4;

    /// <summary>无边框调色板符号按钮（白色符号 + 彩色圆底），44×44 命中区，禁用时系统自动淡化。</summary>
    private static UIButton IconButton(string symbol, UIColor color, string label)
    {
        var configuration = UIButtonConfiguration.PlainButtonConfiguration;
        configuration.Image = UIImage.GetSystemImage(symbol);
        configuration.PreferredSymbolConfigurationForImage = (UIImageSymbolConfiguration)UIImageSymbolConfiguration.Create(22)
            .GetConfiguration(UIImageSymbolConfiguration.Create([UIColor.White, color]));
        configuration.ContentInsets = new NSDirectionalEdgeInsets(0, 0, 0, 0);
        var button = new UIButton(UIButtonType.System)
        {
            Configuration = configuration,
            AccessibilityLabel = label,
            TranslatesAutoresizingMaskIntoConstraints = false,
        };
        button.WidthAnchor.ConstraintEqualTo(44).Active = true;
        button.HeightAnchor.ConstraintEqualTo(44).Active = true;
        return button;
    }
}
