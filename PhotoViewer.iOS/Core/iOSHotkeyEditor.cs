using CoreGraphics;
using Foundation;
using GameController;
using PhotoViewer.Apple;
using PhotoViewer.Core.Input;
using PhotoViewer.ViewModels.Settings;
using UIKit;

namespace PhotoViewer.iOS.Core;

internal sealed class iOSHotkeyEditor : UIView
{
    private readonly SettingsViewModel _settings;
    private readonly iOSInputCapabilities _capabilities;
    private readonly Action _focus;
    private readonly UITableView _table;
    private readonly NSLayoutConstraint _heightConstraint;
    internal UITableView Table => _table;
    internal double BindingRowHeight => HasExternalInput ? 100 : 52;
    internal IReadOnlyList<BindingCell> Cells => _cells.Values.ToArray();
    private readonly Dictionary<SettingsViewModel.HotkeyItem, BindingCell> _cells = [];
    private readonly List<GCControllerButtonInput> _mouseButtons = [];
    private readonly List<GCControllerDirectionPad> _mouseScrolls = [];
    private readonly NSObject _inactive;
    private (SettingsViewModel.HotkeyItem Item, bool Secondary)? _recording;
    private ulong _modifiers;
    internal bool IsRecording => _recording.HasValue;
    internal bool HasExternalInput => _capabilities.HasExternalInput;

    internal iOSHotkeyEditor(SettingsViewModel settings, iOSInputCapabilities capabilities, Action focus)
    {
        _settings = settings; _capabilities = capabilities; _focus = focus;
        TranslatesAutoresizingMaskIntoConstraints = false;
        _table = new UITableView(CGRect.Empty, UITableViewStyle.Plain)
        {
            ScrollEnabled = false, BackgroundColor = UIColor.Clear, RowHeight = 100,
            AllowsSelectionDuringEditing = true,
        };
        _table.Source = new Source(this);
        _table.SetEditing(true, false);
        AddSubview(_table);
        _heightConstraint = HeightAnchor.ConstraintEqualTo((nfloat)(_settings.Hotkeys.Count * BindingRowHeight));
        _heightConstraint.Active = true;
        _table.RowHeight = (nfloat)BindingRowHeight;
        _capabilities.Changed += OnCapabilitiesChanged;
        _inactive = NSNotificationCenter.DefaultCenter.AddObserver(UIApplication.WillResignActiveNotification, _ => CancelRecording());
    }
    public override void LayoutSubviews() { base.LayoutSubviews(); _table.Frame = Bounds; }
    private void OnCapabilitiesChanged()
    {
        if (!HasExternalInput) CancelRecording();
        else if (IsRecording) AttachMouse();
        RefreshBindings();
        _table.RowHeight = (nfloat)BindingRowHeight;
        _heightConstraint.Constant = (nfloat)(_settings.Hotkeys.Count * BindingRowHeight);
        UIView.PerformWithoutAnimation(() => { _table.BeginUpdates(); _table.EndUpdates(); });
        SetNeedsLayout();
    }
    private iOSHotkeyCaptureView? _captureSurface;
    private NSTimer? _captureReleaseTimer;
    private bool _waitingForMouseRelease;
    private long _recordingGeneration;
    internal iOSHotkeyCaptureView? CaptureSurface => _captureSurface;
    internal ulong CurrentModifiers => _modifiers;

    internal void BeginRecording(SettingsViewModel.HotkeyItem item, bool secondary)
    {
        if (!HasExternalInput || Window is not { } window) return;
        CancelRecording();
        _recording = (item, secondary);
        _modifiers = 0;
        _captureSurface = new iOSHotkeyCaptureView(this, window.Bounds);
        window.AddSubview(_captureSurface);
        SetScrolling(false);
        AttachMouse();
        _focus();
        RefreshBindings();
    }
    internal void CancelRecording()
    {
        _recording = null;
        _recordingGeneration++;
        _waitingForMouseRelease = false;
        DetachMouse();
        RemoveCaptureSurface();
        RefreshBindings();
    }
    private void DetachMouse()
    {
        foreach (var button in _mouseButtons) button.PressedChangedHandler = null;
        foreach (var scroll in _mouseScrolls) scroll.ValueChangedHandler = null;
        _mouseButtons.Clear(); _mouseScrolls.Clear();
    }
    private void RemoveCaptureSurface()
    {
        _captureReleaseTimer?.Invalidate();
        _captureReleaseTimer?.Dispose();
        _captureReleaseTimer = null;
        _captureSurface?.RemoveFromSuperview();
        _captureSurface?.Dispose();
        _captureSurface = null;
        SetScrolling(true);
    }
    internal void ReleaseCapturedPointer()
    {
        _waitingForMouseRelease = false;
        long generation = _recordingGeneration;
        BeginInvokeOnMainThread(() =>
        {
            if (generation == _recordingGeneration) ReleaseCaptureIfIdle();
        });
    }
    private void ReleaseCaptureIfIdle()
    {
        if (IsRecording || _waitingForMouseRelease || _captureSurface is { PointerActive: true } or { ScrollActive: true }) return;
        DetachMouse();
        RemoveCaptureSurface();
    }
    private void ScheduleWheelRelease()
    {
        _captureReleaseTimer?.Invalidate();
        _captureReleaseTimer?.Dispose();
        _captureReleaseTimer = NSTimer.CreateScheduledTimer(TimeSpan.FromMilliseconds(200), _ => ReleaseCaptureIfIdle());
    }
    private void SetScrolling(bool enabled)
    {
        for (var view = Superview; view != null; view = view.Superview)
            if (view is UIScrollView scroll) scroll.ScrollEnabled = enabled;
    }
    internal void UpdateModifiers(ulong modifiers) => _modifiers = modifiers;
    internal bool CaptureKey(int usage, ulong modifiers)
    {
        if (!IsRecording) return false;
        _modifiers = modifiers;
        if (usage == 41) { CancelRecording(); return true; }
        if (usage is 42 or 76) { Commit(null); return true; }
        if (usage is >= 224 and <= 231 || usage == 43) return true;
        var gesture = AppleInputMapping.FromHidUsage(usage, modifiers, _settings);
        if (gesture != null) Commit(gesture);
        return true;
    }
    internal bool CaptureMouse(nint button, double wheel = 0)
    {
        if (!IsRecording) return false;
        var gesture = AppleInputMapping.FromRaw(wheel == 0 ? AppleInputMapping.OtherMouseDown : AppleInputMapping.ScrollWheel,
            0, _modifiers, button, wheel, false, null, _settings);
        if (gesture != null)
        {
            _waitingForMouseRelease = wheel == 0;
            Commit(gesture, retainSurface: true);
            if (wheel != 0) ScheduleWheelRelease();
        }
        return true;
    }
    private void Commit(AppGesture? gesture, bool retainSurface = false)
    {
        if (_recording is not { } recording) return;
        if (recording.Secondary) recording.Item.SecondaryHotkey = gesture;
        else recording.Item.PrimaryHotkey = gesture;
        if (retainSurface) _recording = null;
        else CancelRecording();
        _settings.CheckHotkeyConflicts();
        RefreshBindings();
    }
    private void AttachMouse()
    {
        long generation = _recordingGeneration;
        foreach (var mouse in GCMouse.Mice)
        {
            if (mouse.MouseInput is not { } input) continue;
            void Attach(GCControllerButtonInput? button, int index)
            {
                if (button == null || _mouseButtons.Contains(button)) return;
                _mouseButtons.Add(button);
                button.PressedChangedHandler = (_, _, pressed) => UIApplication.SharedApplication.BeginInvokeOnMainThread(() =>
                {
                    if (generation != _recordingGeneration) return;
                    if (pressed) CaptureMouse(index);
                    else ReleaseCapturedPointer();
                });
            }
            Attach(input.MiddleButton, 2);
            var auxiliary = input.AuxiliaryButtons ?? [];
            for (int index = 0; index < auxiliary.Length; index++) Attach(auxiliary[index], index + 3);
            if (!_mouseScrolls.Contains(input.Scroll))
            {
                _mouseScrolls.Add(input.Scroll);
                input.Scroll.ValueChangedHandler = (_, _, y) =>
                {
                    if (y != 0) UIApplication.SharedApplication.BeginInvokeOnMainThread(() =>
                    {
                        if (generation == _recordingGeneration) CaptureMouse(0, y);
                    });
                };
            }
        }
    }
    internal void RefreshBindings()
    {
        foreach (var (item, cell) in _cells) cell.Refresh(item, this);
    }
    private string Display(AppGesture? gesture)
    {
        if (gesture == null) return "未设置";
        var flags = gesture.Key?.Modifiers ?? gesture.Mouse!.Modifiers;
        string Modifier(GestureModifiers target)
        {
            if (!flags.HasFlag(target)) return "";
            foreach (var (mask, symbol) in new[] { (AppleInputMapping.CommandMask, "⌘"), (AppleInputMapping.AlternateMask, "⌥"), (AppleInputMapping.ControlMask, "⌃") })
                if (AppleInputMapping.FromHidUsage(4, mask, _settings)?.Key?.Modifiers == target) return symbol;
            return target.ToString() + "+";
        }
        string modifiers = Modifier(GestureModifiers.Meta) + Modifier(GestureModifiers.Alt) + Modifier(GestureModifiers.Control)
            + (flags.HasFlag(GestureModifiers.Shift) ? "⇧" : "");
        string name = gesture.Key is { } keyboard ? KeyLabel(keyboard.Key) : gesture.Mouse!.Action switch
        {
            MouseAction.WheelUp => "滚轮上", MouseAction.WheelDown => "滚轮下", MouseAction.MiddleClick => "中键",
            MouseAction.LeftClick => "左键", MouseAction.RightClick => "右键", MouseAction.XButton1Click => "侧键 1", _ => "侧键 2",
        };
        return modifiers + name;
    }
    internal static string KeyLabel(GestureKey key)
    {
        string name = key.ToString();
        if (name.Length == 2 && name[0] == 'D' && char.IsDigit(name[1])) return name[1..];
        if (name.StartsWith("NumPad", StringComparison.Ordinal)) return "数字 " + name[6..];
        return key switch
        {
            GestureKey.Left => "←", GestureKey.Right => "→", GestureKey.Up => "↑", GestureKey.Down => "↓",
            GestureKey.Return or GestureKey.Enter => "↩", GestureKey.Tab => "⇥", GestureKey.Back => "⌫",
            GestureKey.Delete => "⌦", GestureKey.Escape => "⎋", GestureKey.Space => "空格",
            GestureKey.Home => "↖", GestureKey.End => "↘", GestureKey.PageUp or GestureKey.Prior => "⇞",
            GestureKey.PageDown or GestureKey.Next => "⇟", GestureKey.Insert => "插入", GestureKey.CapsLock or GestureKey.Capital => "⇪",
            GestureKey.NumLock => "数字锁定", GestureKey.PrintScreen or GestureKey.Snapshot => "截屏", GestureKey.Scroll => "滚动锁定", GestureKey.Pause => "暂停",
            GestureKey.OemPlus => "=", GestureKey.OemMinus => "−", GestureKey.OemTilde or GestureKey.Oem3 => "`",
            GestureKey.OemComma => ",", GestureKey.OemPeriod => ".", GestureKey.OemQuestion or GestureKey.Oem2 => "/",
            GestureKey.OemSemicolon or GestureKey.Oem1 => ";", GestureKey.OemQuotes or GestureKey.Oem7 => "'",
            GestureKey.OemOpenBrackets or GestureKey.Oem4 => "[", GestureKey.OemCloseBrackets or GestureKey.Oem6 => "]",
            GestureKey.OemPipe or GestureKey.Oem5 or GestureKey.OemBackslash or GestureKey.Oem102 => "\\",
            GestureKey.Add => "数字 +", GestureKey.Subtract => "数字 −", GestureKey.Multiply => "数字 ×",
            GestureKey.Divide => "数字 /", GestureKey.Decimal => "数字 .", _ => name,
        };
    }
    private sealed class Source(iOSHotkeyEditor owner) : UITableViewSource
    {
        public override nint RowsInSection(UITableView tableView, nint section) => owner._settings.Hotkeys.Count;
        public override UITableViewCell GetCell(UITableView tableView, NSIndexPath indexPath)
        {
            var item = owner._settings.Hotkeys[indexPath.Row];
            if (!owner._cells.TryGetValue(item, out var cell)) owner._cells[item] = cell = new BindingCell(item, owner);
            cell.ShowsReorderControl = true;
            cell.Refresh(item, owner);
            return cell;
        }
        public override nfloat GetHeightForRow(UITableView tableView, NSIndexPath indexPath) => (nfloat)owner.BindingRowHeight;
        public override bool CanMoveRow(UITableView tableView, NSIndexPath indexPath) => true;
        public override bool CanEditRow(UITableView tableView, NSIndexPath indexPath) => true;
        public override bool ShouldIndentWhileEditing(UITableView tableView, NSIndexPath indexPath) => false;
        public override UITableViewCellEditingStyle EditingStyleForRow(UITableView tableView, NSIndexPath indexPath) => UITableViewCellEditingStyle.None;
        public override void MoveRow(UITableView tableView, NSIndexPath from, NSIndexPath to) => owner._settings.MoveHotkey(from.Row, to.Row);
    }
    internal sealed class BindingCell : UITableViewCell
    {
        private readonly UILabel _name = new() { Lines = 1 };
        private readonly UISwitch _toggle = new();
        private readonly UIButton[] _bindings = [new(UIButtonType.System), new(UIButtonType.System)];
        private readonly UIButton[] _clear = [new(UIButtonType.System), new(UIButtonType.System)];
        internal IReadOnlyList<UIButton> BindingButtons => _bindings;
        internal IReadOnlyList<UIButton> ClearButtons => _clear;
        internal UILabel NameLabel => _name;
        internal UISwitch DisplayToggle => _toggle;
        internal BindingCell(SettingsViewModel.HotkeyItem item, iOSHotkeyEditor owner) : base(UITableViewCellStyle.Default, null)
        {
            BackgroundColor = UIColor.Clear;
            SelectionStyle = UITableViewCellSelectionStyle.None;
            ContentView.AddSubview(_name); ContentView.AddSubview(_toggle);
            _toggle.ValueChanged += (_, _) => item.IsDisplay = _toggle.On;
            for (int index = 0; index < 2; index++)
            {
                int slot = index;
                _bindings[index].TitleLabel!.Font = UIFont.SystemFontOfSize(12)!;
                _bindings[index].TouchUpInside += (_, _) => owner.BeginRecording(item, slot == 1);
                _clear[index].SetImage(UIImage.GetSystemImage("xmark.circle"), UIControlState.Normal);
                _clear[index].AccessibilityLabel = slot == 0 ? "清空主绑定" : "清空次绑定";
                _clear[index].TouchUpInside += (_, _) =>
                {
                    owner.CancelRecording();
                    if (slot == 0) item.PrimaryHotkey = null; else item.SecondaryHotkey = null;
                    owner.RefreshBindings();
                };
                ContentView.AddSubview(_bindings[index]); ContentView.AddSubview(_clear[index]);
            }
        }
        internal void Refresh(SettingsViewModel.HotkeyItem item, iOSHotkeyEditor owner)
        {
            _name.Text = item.Name;
            _toggle.On = item.IsDisplay;
            for (int index = 0; index < 2; index++)
            {
                bool secondary = index == 1;
                bool conflict = secondary ? item.HasSecondaryConflict : item.HasPrimaryConflict;
                bool recording = owner._recording is { } target && ReferenceEquals(target.Item, item) && target.Secondary == secondary;
                string text = recording ? "按下按键…" : owner.Display(secondary ? item.SecondaryHotkey : item.PrimaryHotkey);
                bool visible = owner.HasExternalInput;
                string? title = visible ? $"{(secondary ? "次" : "主")} {text}{(conflict ? "（冲突）" : "")}" : null;
                _bindings[index].SetTitle(title, UIControlState.Normal);
                _bindings[index].AccessibilityLabel = title;
                _bindings[index].Hidden = !visible;
                _bindings[index].Enabled = visible;
                _bindings[index].IsAccessibilityElement = visible;
                _bindings[index].AccessibilityElementsHidden = !visible;
                _bindings[index].SetTitleColor(conflict ? UIColor.SystemRed : UIColor.SystemBlue, UIControlState.Normal);
                _clear[index].Hidden = !visible;
                _clear[index].IsAccessibilityElement = visible;
                _clear[index].AccessibilityElementsHidden = !visible;
            }
            SetNeedsLayout();
        }
        public override void LayoutSubviews()
        {
            base.LayoutSubviews();
            double width = ContentView.Bounds.Width;
            bool expanded = !_bindings[0].Hidden;
            double titleY = expanded ? 8 : ((double)ContentView.Bounds.Height - 26) / 2;
            _name.Frame = new CGRect(16, titleY, Math.Max(1, width - 95), 26);
            _toggle.Frame = new CGRect(width - 67, titleY - 3, 51, 31);
            for (int index = 0; index < 2; index++)
            {
                double x = 12 + index * (width - 24) / 2;
                double slot = (width - 24) / 2;
                _bindings[index].Frame = expanded ? new CGRect(x, 43, Math.Max(1, slot - 34), 44) : CGRect.Empty;
                _clear[index].Frame = expanded ? new CGRect(x + slot - 34, 43, 34, 44) : CGRect.Empty;
            }
        }
    }
    internal void StopObserving()
    {
        CancelRecording();
        _capabilities.Changed -= OnCapabilitiesChanged;
        NSNotificationCenter.DefaultCenter.RemoveObserver(_inactive);
    }
    protected override void Dispose(bool disposing)
    {
        if (disposing) StopObserving();
        base.Dispose(disposing);
    }
}
