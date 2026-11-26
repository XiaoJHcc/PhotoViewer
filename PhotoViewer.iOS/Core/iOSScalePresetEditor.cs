using System.Globalization;
using CoreGraphics;
using Foundation;
using PhotoViewer.ViewModels.Settings;
using UIKit;

namespace PhotoViewer.iOS.Core;

/// <summary>
/// 缩放比例预设的原生 <see cref="UITableViewStyle.InsetGrouped"/> 分组（SETTINGS-07）：
/// 每条预设为独立原生单元格（无边框数值输入 + % + 行尾删除按钮 + 原生左滑删除），
/// 末行为系统样式「添加缩放预设」操作行，校验错误显示在系统分组脚注中。
/// 提交排序通过 <see cref="UITableView.MoveRow"/> 移动现有单元格，不销毁输入框、不抢焦点、不重弹键盘。
/// </summary>
internal sealed class iOSScalePresetSection : iOSNativeSettingsFormViewController.SettingsSection
{
    private const string AddCellReuseId = "ScalePresetAddRow";
    private const string ValidationMessage = "请输入大于 0 且不超过 1600% 的数字";

    private readonly SettingsViewModel _settings;
    private readonly iOSNativeSettingsFormViewController _host;
    private readonly Dictionary<SettingsViewModel.ScalePreset, iOSScalePresetCell> _cells = [];
    private readonly UITableViewHeaderFooterView _errorFooter;
    private readonly UILabel _errorLabel;
    private bool _hasValidationError;

    internal iOSScalePresetSection(
        string headerTitle,
        SettingsViewModel settings,
        iOSNativeSettingsFormViewController host)
        : base(headerTitle)
    {
        _settings = settings;
        _host = host;

        _errorLabel = new UILabel
        {
            TextColor = UIColor.SystemRed,
            Font = UIFont.PreferredFootnote!,
            AdjustsFontForContentSizeCategory = true,
            Lines = 0,
            LineBreakMode = UILineBreakMode.WordWrap,
            TranslatesAutoresizingMaskIntoConstraints = false,
        };
        _errorFooter = new UITableViewHeaderFooterView();
        _errorFooter.ContentView.AddSubview(_errorLabel);
        NSLayoutConstraint.ActivateConstraints(
        [
            _errorLabel.TopAnchor.ConstraintEqualTo(_errorFooter.ContentView.TopAnchor, 8),
            _errorLabel.LeadingAnchor.ConstraintEqualTo(_errorFooter.ContentView.LayoutMarginsGuide.LeadingAnchor),
            _errorLabel.TrailingAnchor.ConstraintEqualTo(_errorFooter.ContentView.LayoutMarginsGuide.TrailingAnchor),
            _errorLabel.BottomAnchor.ConstraintEqualTo(_errorFooter.ContentView.BottomAnchor, -8),
        ]);

        SyncCells();
    }

    internal override int RowCount => _settings.ScalePresets.Count + 1;

    internal override UIView? FooterView => _hasValidationError ? _errorFooter : null;

    internal bool HasValidationError => _hasValidationError;

    /// <summary>当前按顺序对应的全部预设输入框（验收读写）。</summary>
    internal IReadOnlyList<UITextField> Fields
        => _settings.ScalePresets.Select(preset => GetOrCreateCell(preset).Field).ToArray();

    /// <summary>当前按顺序对应的全部预设删除按钮（验收核对禁删与命中区）。</summary>
    internal IReadOnlyList<UIButton> DeleteButtons
        => _settings.ScalePresets.Select(preset => GetOrCreateCell(preset).DeleteButton).ToArray();

    /// <summary>末行「添加缩放预设」在分组中的行索引。</summary>
    internal int AddRowIndex => _settings.ScalePresets.Count;

    internal override UITableViewCell GetCell(UITableView tableView, NSIndexPath indexPath)
    {
        int row = (int)indexPath.Row;
        if (row >= _settings.ScalePresets.Count)
        {
            return GetAddCell(tableView);
        }

        var preset = _settings.ScalePresets[row];
        var cell = GetOrCreateCell(preset);
        cell.SetCanDelete(_settings.CanRemoveScalePreset);
        return cell;
    }

    internal override void OnRowSelected(UITableView tableView, NSIndexPath indexPath)
    {
        tableView.DeselectRow(indexPath, true);
        int row = (int)indexPath.Row;
        if (row >= _settings.ScalePresets.Count)
        {
            AddPreset();
            return;
        }

        if (row >= 0 && row < _settings.ScalePresets.Count)
        {
            GetOrCreateCell(_settings.ScalePresets[row]).FocusField();
        }
    }

    internal override UISwipeActionsConfiguration? GetTrailingSwipeActions(UITableView tableView, NSIndexPath indexPath)
    {
        int row = (int)indexPath.Row;
        if (row < 0 || row >= _settings.ScalePresets.Count || !_settings.CanRemoveScalePreset)
        {
            return null;
        }

        var preset = _settings.ScalePresets[row];
        var delete = UIContextualAction.FromContextualActionStyle(
            UIContextualActionStyle.Destructive,
            "删除",
            (_, _, completion) =>
            {
                bool deleted = RemovePreset(preset);
                completion(deleted);
            });
        var config = UISwipeActionsConfiguration.FromActions([delete]);
        config.PerformsFirstActionWithFullSwipe = true;
        return config;
    }

    /// <summary>新增一项 100% 预设，排序后在原生表格中插入对应行。</summary>
    internal void AddPreset()
    {
        _host.View?.EndEditing(true);
        var before = new HashSet<SettingsViewModel.ScalePreset>(_settings.ScalePresets);
        _settings.AddScalePreset();
        _settings.SortScalePreset();
        var added = _settings.ScalePresets.FirstOrDefault(preset => !before.Contains(preset));
        SyncCells();

        int section = _host.SectionIndexOf(this);
        if (section >= 0 && added != null && _host.ViewIfLoaded?.Window != null)
        {
            int newIndex = _settings.ScalePresets.IndexOf(added);
            if (newIndex >= 0)
            {
                var insertPath = NSIndexPath.FromRowSection(newIndex, section);
                _host.TableView.InsertRows([insertPath], UITableViewRowAnimation.Automatic);
                UpdateDeleteStates();
                return;
            }
        }

        _host.TableView.ReloadData();
    }

    /// <summary>删除指定预设并从原生表格移除该行；仅剩一项时忽略。</summary>
    internal bool RemovePreset(SettingsViewModel.ScalePreset preset)
    {
        _host.View?.EndEditing(true);
        if (!_settings.CanRemoveScalePreset) return false;
        int oldIndex = _settings.ScalePresets.IndexOf(preset);
        if (oldIndex < 0) return false;

        _settings.RemoveScalePreset(preset);
        _cells.Remove(preset);

        int section = _host.SectionIndexOf(this);
        if (section >= 0 && _host.ViewIfLoaded?.Window != null)
        {
            _host.TableView.DeleteRows([NSIndexPath.FromRowSection(oldIndex, section)], UITableViewRowAnimation.Automatic);
            UpdateDeleteStates();
        }
        else
        {
            _host.TableView.ReloadData();
        }

        return true;
    }

    private void Commit(SettingsViewModel.ScalePreset preset, UITextField field)
    {
        bool valid = (double.TryParse(field.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double value)
            || double.TryParse(field.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out value))
            && SettingsViewModel.IsValidScalePresetPercent(value);

        SetValidationError(!valid);

        int oldIndex = _settings.ScalePresets.IndexOf(preset);
        if (valid)
        {
            preset.Text = value.ToString("0.###", CultureInfo.InvariantCulture);
            _settings.ApplyScalePreset();
        }
        field.Text = preset.Text;

        int newIndex = _settings.ScalePresets.IndexOf(preset);
        if (valid && oldIndex >= 0 && newIndex >= 0 && oldIndex != newIndex)
        {
            int from = oldIndex;
            int to = newIndex;
            _host.BeginInvokeOnMainThread(() => MovePresetRow(from, to));
        }
    }

    private void MovePresetRow(int fromIndex, int toIndex)
    {
        int section = _host.SectionIndexOf(this);
        if (section < 0 || _host.ViewIfLoaded?.Window == null) return;
        int count = _settings.ScalePresets.Count;
        if (fromIndex < 0 || fromIndex >= count || toIndex < 0 || toIndex >= count || fromIndex == toIndex) return;

        // 仅移动原生单元格，不 ReloadData、不重建 UITextField，避免打断其他行焦点或重弹键盘。
        _host.TableView.BeginUpdates();
        _host.TableView.MoveRow(
            NSIndexPath.FromRowSection(fromIndex, section),
            NSIndexPath.FromRowSection(toIndex, section));
        _host.TableView.EndUpdates();
    }

    private void SetValidationError(bool hasError)
    {
        if (_hasValidationError == hasError) return;
        _hasValidationError = hasError;
        _errorLabel.Text = hasError ? ValidationMessage : null;
        int section = _host.SectionIndexOf(this);
        if (section < 0 || _host.ViewIfLoaded?.Window == null) return;
        // 仅触发分组脚注重排，不重建预设单元格，不抢回输入框焦点。
        UIView.PerformWithoutAnimation(() =>
        {
            _host.TableView.BeginUpdates();
            _host.TableView.EndUpdates();
        });
    }

    private void SyncCells()
    {
        foreach (var stale in _cells.Keys.Where(preset => !_settings.ScalePresets.Contains(preset)).ToArray())
        {
            _cells.Remove(stale);
        }
        foreach (var preset in _settings.ScalePresets)
        {
            GetOrCreateCell(preset);
        }
        UpdateDeleteStates();
    }

    private void UpdateDeleteStates()
    {
        bool canDelete = _settings.CanRemoveScalePreset;
        foreach (var cell in _cells.Values)
        {
            cell.SetCanDelete(canDelete);
        }
    }

    private iOSScalePresetCell GetOrCreateCell(SettingsViewModel.ScalePreset preset)
    {
        if (!_cells.TryGetValue(preset, out var cell))
        {
            cell = new iOSScalePresetCell(preset, Commit, p => _ = RemovePreset(p));
            _cells[preset] = cell;
        }
        cell.SetCanDelete(_settings.CanRemoveScalePreset);
        return cell;
    }

    private static UITableViewCell GetAddCell(UITableView tableView)
    {
        var cell = tableView.DequeueReusableCell(AddCellReuseId)
            ?? new UITableViewCell(UITableViewCellStyle.Default, AddCellReuseId);
        var config = UIListContentConfiguration.CellConfiguration;
        config.Text = "添加缩放预设";
        config.TextProperties.Color = UIColor.SystemBlue;
        config.Image = UIImage.GetSystemImage("plus.circle.fill")
            ?.ApplyConfiguration((UIImageSymbolConfiguration)UIImageSymbolConfiguration.Create(20)
                .GetConfiguration(UIImageSymbolConfiguration.Create([UIColor.White, UIColor.SystemGreen])));
        cell.ContentConfiguration = config;
        cell.SelectionStyle = UITableViewCellSelectionStyle.Default;
        cell.AccessibilityTraits = UIAccessibilityTrait.Button;
        return cell;
    }
}

/// <summary>
/// 单条缩放比例预设的原生 <see cref="UITableViewCell"/>：
/// 遵循系统 <c>LayoutMarginsGuide</c>、支持 Dynamic Type，内含无边框数值输入框、% 单位与 44×44 删除按钮。
/// </summary>
internal sealed class iOSScalePresetCell : UITableViewCell
{
    private readonly UITextField _field;
    private readonly UIButton _deleteButton;

    internal UITextField Field => _field;
    internal UIButton DeleteButton => _deleteButton;

    internal iOSScalePresetCell(
        SettingsViewModel.ScalePreset preset,
        Action<SettingsViewModel.ScalePreset, UITextField> onCommit,
        Action<SettingsViewModel.ScalePreset> onDelete)
        : base(UITableViewCellStyle.Default, null)
    {
        SelectionStyle = UITableViewCellSelectionStyle.None;

        var font = UIFontMetrics.DefaultMetrics.GetScaledFont(
            UIFont.MonospacedDigitSystemFontOfSize(17, UIFontWeight.Regular)!);

        _field = new UITextField
        {
            Text = preset.Text,
            Placeholder = "100",
            Font = font,
            AdjustsFontForContentSizeCategory = true,
            TextColor = UIColor.Label,
            KeyboardType = UIKeyboardType.DecimalPad,
            BorderStyle = UITextBorderStyle.None,
            ClearButtonMode = UITextFieldViewMode.WhileEditing,
            AccessibilityLabel = "缩放比例预设",
            TranslatesAutoresizingMaskIntoConstraints = false,
        };
        var toolbar = new UIToolbar(new CGRect(0, 0, 320, 44));
        toolbar.Items =
        [
            new UIBarButtonItem(UIBarButtonSystemItem.FlexibleSpace),
            new UIBarButtonItem(UIBarButtonSystemItem.Done, (_, _) => _field.ResignFirstResponder()),
        ];
        toolbar.SizeToFit();
        _field.InputAccessoryView = toolbar;
        _field.ShouldReturn = _ => { _field.ResignFirstResponder(); return true; };
        _field.EditingDidEnd += (_, _) => onCommit(preset, _field);

        var percentLabel = new UILabel
        {
            Text = "%",
            Font = UIFont.PreferredBody!,
            AdjustsFontForContentSizeCategory = true,
            TextColor = UIColor.SecondaryLabel,
            TranslatesAutoresizingMaskIntoConstraints = false,
        };
        percentLabel.SetContentHuggingPriority((float)UILayoutPriority.Required, UILayoutConstraintAxis.Horizontal);
        percentLabel.SetContentCompressionResistancePriority((float)UILayoutPriority.Required, UILayoutConstraintAxis.Horizontal);

        var deleteConfig = UIButtonConfiguration.PlainButtonConfiguration;
        deleteConfig.Image = UIImage.GetSystemImage("minus.circle.fill");
        deleteConfig.PreferredSymbolConfigurationForImage = (UIImageSymbolConfiguration)UIImageSymbolConfiguration.Create(22)
            .GetConfiguration(UIImageSymbolConfiguration.Create([UIColor.White, UIColor.SystemRed]));
        deleteConfig.ContentInsets = NSDirectionalEdgeInsets.Zero;
        _deleteButton = new UIButton(UIButtonType.System)
        {
            Configuration = deleteConfig,
            AccessibilityLabel = "删除预设",
            TranslatesAutoresizingMaskIntoConstraints = false,
        };
        _deleteButton.TouchUpInside += (_, _) => onDelete(preset);

        ContentView.AddSubview(_field);
        ContentView.AddSubview(percentLabel);
        ContentView.AddSubview(_deleteButton);

        var minHeight = ContentView.HeightAnchor.ConstraintGreaterThanOrEqualTo(48);
        minHeight.Priority = 999;

        NSLayoutConstraint.ActivateConstraints(
        [
            minHeight,
            _field.LeadingAnchor.ConstraintEqualTo(ContentView.LayoutMarginsGuide.LeadingAnchor),
            _field.TopAnchor.ConstraintGreaterThanOrEqualTo(ContentView.TopAnchor, 6),
            _field.BottomAnchor.ConstraintLessThanOrEqualTo(ContentView.BottomAnchor, -6),
            _field.CenterYAnchor.ConstraintEqualTo(ContentView.CenterYAnchor),
            _field.WidthAnchor.ConstraintGreaterThanOrEqualTo(64),
            _field.HeightAnchor.ConstraintGreaterThanOrEqualTo(34),

            percentLabel.LeadingAnchor.ConstraintEqualTo(_field.TrailingAnchor, 6),
            percentLabel.CenterYAnchor.ConstraintEqualTo(ContentView.CenterYAnchor),

            _deleteButton.LeadingAnchor.ConstraintGreaterThanOrEqualTo(percentLabel.TrailingAnchor, 12),
            // 44×44 命中区右侧外扩，使 22pt 图标视觉右缘与系统 LayoutMarginsGuide 右缘对齐。
            _deleteButton.TrailingAnchor.ConstraintEqualTo(ContentView.LayoutMarginsGuide.TrailingAnchor, 11),
            _deleteButton.CenterYAnchor.ConstraintEqualTo(ContentView.CenterYAnchor),
            _deleteButton.WidthAnchor.ConstraintEqualTo(44),
            _deleteButton.HeightAnchor.ConstraintEqualTo(44),
        ]);
        _field.SetContentHuggingPriority((float)UILayoutPriority.DefaultHigh, UILayoutConstraintAxis.Horizontal);
    }

    internal void SetCanDelete(bool canDelete) => _deleteButton.Enabled = canDelete;

    internal void FocusField() => _field.BecomeFirstResponder();
}
