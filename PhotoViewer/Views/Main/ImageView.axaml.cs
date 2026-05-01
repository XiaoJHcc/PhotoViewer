using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;
using System.IO;
using Avalonia.Threading;
using PhotoViewer.Controls;
using PhotoViewer.ViewModels;

namespace PhotoViewer.Views;

public partial class ImageView : UserControl
{
    private ImageViewModel? ViewModel => DataContext as ImageViewModel;

    ////////////
    /// 右键菜单
    ////////////

    #region Menu

    private Grid? _contextMenuOverlay;
    private Border? _contextMenuHost;
    private CheckableMenuHeader? _detailViewMenuHeader;
    private CheckableMenuHeader? _thumbnailViewMenuHeader;
    private CheckableMenuHeader? _controlViewMenuHeader;

    private void InitMenu()
    {
        _contextMenuOverlay = this.FindControl<Grid>("ContextMenuOverlay");
        _contextMenuHost = this.FindControl<Border>("ContextMenuHost");
        _thumbnailViewMenuHeader = this.FindControl<CheckableMenuHeader>("ThumbnailMenuHeader");
        _detailViewMenuHeader = this.FindControl<CheckableMenuHeader>("DetailMenuHeader");
        _controlViewMenuHeader = this.FindControl<CheckableMenuHeader>("ControlMenuHeader");
        UpdateMenuCheckStates();
    }

    private void ShowMenuAt(Point point)
    {
        if (_contextMenuOverlay == null || _contextMenuHost == null) return;
        UpdateMenuCheckStates();

        _contextMenuOverlay.IsVisible = true;
        _contextMenuHost.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var menuSize = _contextMenuHost.DesiredSize;

        var overlaySize = _contextMenuOverlay.Bounds.Size;
        if (overlaySize.Width <= 0 || overlaySize.Height <= 0)
        {
            overlaySize = Bounds.Size;
        }

        var x = point.X;
        var y = point.Y;
        if (x + menuSize.Width > overlaySize.Width)
        {
            x = Math.Max(0, overlaySize.Width - menuSize.Width - 4);
        }
        if (y + menuSize.Height > overlaySize.Height)
        {
            y = Math.Max(0, overlaySize.Height - menuSize.Height - 4);
        }

        Canvas.SetLeft(_contextMenuHost, x);
        Canvas.SetTop(_contextMenuHost, y);
    }

    private void HideMenu()
    {
        if (_contextMenuOverlay == null) return;
        _contextMenuOverlay.IsVisible = false;
    }

    private void ShowMenuAtPointer(Point point)
    {
        ShowMenuAt(point);
    }

    private void ShowMenuAtTouch(Point point)
    {
        ShowMenuAt(new Point(point.X, point.Y - 40));
    }

    private void ToggleDetailViewFromMenu()
    {
        if (ViewModel?.Main == null) return;
        ViewModel.Main.ToggleDetailView();
        UpdateMenuCheckStates();
        HideMenu();
    }

    private void ToggleThumbnailViewFromMenu()
    {
        if (ViewModel?.Main == null) return;
        ViewModel.Main.ToggleThumbnailView();
        UpdateMenuCheckStates();
        HideMenu();
    }

    private void ToggleControlViewFromMenu()
    {
        if (ViewModel?.Main == null) return;
        ViewModel.Main.ToggleControlView();
        UpdateMenuCheckStates();
        HideMenu();
    }

    private void UpdateMenuCheckStates()
    {
        if (_detailViewMenuHeader != null)
        {
            _detailViewMenuHeader.IsIconVisible = ViewModel?.Main.IsDetailViewVisible ?? false;
        }

        if (_thumbnailViewMenuHeader != null)
        {
            _thumbnailViewMenuHeader.IsIconVisible = ViewModel?.Main.IsThumbnailViewVisible ?? false;
        }

        if (_controlViewMenuHeader != null)
        {
            _controlViewMenuHeader.IsIconVisible = ViewModel?.Main.IsControlViewVisible ?? false;
        }
    }

    private void OnMenuOverlayPressed(object? sender, PointerPressedEventArgs e)
    {
        if (_contextMenuOverlay == null || _contextMenuHost == null) return;
        if (_contextMenuHost.IsPointerOver) return;
        HideMenu();
        e.Handled = true;
    }

    private void OnToggleThumbnailMenuClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        ToggleThumbnailViewFromMenu();
        e.Handled = true;
    }

    private void OnToggleDetailMenuClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        ToggleDetailViewFromMenu();
        e.Handled = true;
    }

    private void OnToggleControlMenuClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        ToggleControlViewFromMenu();
        e.Handled = true;
    }

    private void OnOpenSettingMenuClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        OpenImageSetting();
        HideMenu();
        e.Handled = true;
    }

    /// <summary>
    /// 右键菜单"打开文件/打开文件夹"点击：调用与控制栏相同的文件选择器功能，并关闭菜单
    /// </summary>
    private void OnOpenFileMenuClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        HideMenu();
        _ = ViewModel?.Main.FolderVM.OpenFilePickerAsync();
        e.Handled = true;
    }

    #endregion


    /// <summary>
    /// 构造函数
    /// </summary>
    public ImageView()
    {
        InitializeComponent();

        InitMenu();

        SetupEventHandlers();

        // 启用拖拽支持
        DragDrop.SetAllowDrop(this, true);
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DropEvent, OnDrop);
    }

    /// <summary>
    /// 打开图片预览设置窗口
    /// </summary>
    private void OpenImageSetting()
    {
        if (TopLevel.GetTopLevel(this) is Window parentWindow)
        {
            ViewModel?.Main.OpenSettingWindow(parentWindow);
        }
        else
        {
            ViewModel?.Main.OpenSettingModal();
        }
    }

    ////////////////////
    /// 预览控制
    ////////////////////

    #region Control

    // 菜单长按状态
    private Point _pressPosition;

    private DispatcherTimer _longPressTimer = new()
    {
        Interval = TimeSpan.FromMilliseconds(800) // 长按时间（毫秒）
    };

    private const double MoveTolerance = 10; // 移动容差（像素）
    private bool _isLongPressTriggered;

    // 长按成功
    private void OnLongPressTimerTick(object? sender, EventArgs e)
    {
        _longPressTimer.Stop();
        _isLongPressTriggered = true;
        ShowMenuAtTouch(_pressPosition);
    }

    // 长按取消
    private void CancelLongPress()
    {
        _longPressTimer.Stop();
    }

    // 操作状态
    private Vector _lastPanPosition;
    private Vector _lastCenter;

    private double _lastDistance;

    // private bool _isDragging;
    private bool _wasTwoFingers = false; // 标记上一次操作是否为双指


    // 活动指针跟踪
    private readonly Dictionary<IPointer, Point> _activePointers = new();

    private void SetupEventHandlers()
    {
        PointerPressed += OnPointerPressed;
        PointerMoved += OnPointerMoved;
        PointerReleased += OnPointerReleased;
        PointerWheelChanged += OnPointerWheelChanged;
        DoubleTapped += OnDoubleTapped;
        // KeyDown += OnKeyDown;

        _longPressTimer.Tick += OnLongPressTimerTick;

        // 监听尺寸变化
        this.GetObservable(BoundsProperty)
            .Subscribe(_ =>
                ViewModel?.UpdateView(new Vector(Bounds.Size.Width, Bounds.Size.Height)));
    }

    /// <summary>
    /// 监听滚轮
    /// </summary>
    private void OnPointerWheelChanged(object? sender, PointerWheelEventArgs e)
    {
        if (ViewModel == null) return;

        // 匹配鼠标滚轮快捷键（仅限 ImageView 范围内）
        var action = e.Delta.Y > 0 ? MouseAction.WheelUp : MouseAction.WheelDown;
        var pos = e.GetPosition(this);
        if (TryExecuteMouseGesture(action, e.KeyModifiers, pos))
        {
            e.Handled = true;
        }
    }

    /// <summary>
    /// 监听点击
    /// </summary>
    private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        // 新增：先尝试匹配鼠标按键快捷键（仅限鼠标）
        if (e.Pointer.Type == PointerType.Mouse)
        {
            var kind = e.GetCurrentPoint(this).Properties.PointerUpdateKind;
            MouseAction? action = kind switch
            {
                PointerUpdateKind.LeftButtonPressed => MouseAction.LeftClick,
                PointerUpdateKind.RightButtonPressed => MouseAction.RightClick,
                PointerUpdateKind.MiddleButtonPressed => MouseAction.MiddleClick,
                PointerUpdateKind.XButton1Pressed => MouseAction.XButton1Click,
                PointerUpdateKind.XButton2Pressed => MouseAction.XButton2Click,
                _ => null
            };

            if (action != null)
            {
                // 禁止无修饰的 左/右 键作为独立快捷键；其余允许
                var mappedMods = AppleKeyboardMapping.ApplyForRuntime(e.KeyModifiers);
                if (!((action is MouseAction.LeftClick or MouseAction.RightClick) && mappedMods == KeyModifiers.None))
                {
                    var pos = e.GetPosition(this);
                    if (TryExecuteMouseGesture(action.Value, e.KeyModifiers, pos))
                    {
                        e.Handled = true;
                        return;
                    }
                }
            }
        }

        // 右键 打开菜单（仅当未匹配快捷键时）
        if (e.Properties.IsRightButtonPressed)
        {
            var pos = e.GetPosition(this);
            _pressPosition = pos;
            ShowMenuAtPointer(pos);
            e.Handled = true;
        }

        // 左键
        if (e.Properties.IsLeftButtonPressed)
        {
            // 触摸等待判断：点击、拖动、长按
            if (e.Pointer.Type is PointerType.Touch or PointerType.Pen)
            {
                _isLongPressTriggered = false;
                _pressPosition = e.GetPosition(this);
                _longPressTimer.Start();
                e.Pointer.Capture(this);
            }

            // 已打开图片时 准备拖动图片
            if (ViewModel?.SourceBitmap != null)
            {
                var point = e.GetPosition(this);
                var pointer = e.Pointer;

                pointer.Capture(this);

                _activePointers[pointer] = point;
                _wasTwoFingers = false;

                if (_activePointers.Count == 1)
                {
                    _lastPanPosition = point;
                    // _isDragging = true;
                }
            }
            else
            {
                // 无图片时 鼠标左键打开图片
                if (e.Pointer.Type == PointerType.Mouse)
                {
                    _ = ViewModel?.Main.FolderVM.OpenFilePickerAsync();
                }
            }

            e.Handled = true;
        }
    }

    /// <summary>
    /// 监听拖动
    /// </summary>
    private void OnPointerMoved(object? sender, PointerEventArgs e)
    {
        if (!e.Properties.IsLeftButtonPressed) return; // 非左键全无效

        if (ViewModel == null || !_activePointers.TryGetValue(e.Pointer, out var lastPoint)) return;

        var currentPoint = e.GetPosition(this);

        // 触屏设备识别长按
        if (e.Pointer.Type is PointerType.Touch or PointerType.Pen || _longPressTimer.IsEnabled)
        {
            // 超出容差范围，取消长按
            if (Vector.Distance(_pressPosition, currentPoint) > MoveTolerance)
                CancelLongPress();
        }

        // 当从双指变为单指时，不执行任何操作
        if (_wasTwoFingers && _activePointers.Count == 1)
        {
            _activePointers[e.Pointer] = currentPoint;
            return;
        }

        switch (_activePointers.Count)
        {
            case 1:
                // 单指拖动
                ViewModel.Move(currentPoint - _lastPanPosition);

                _lastPanPosition = currentPoint;
                _activePointers[e.Pointer] = currentPoint;
                break;
            case >= 2:
            {
                // 双指缩放
                _wasTwoFingers = true;
                var points = _activePointers.Values.ToArray();
                var center = GetCenter(points);
                var distance = GetDistance(points);

                if (_lastDistance > 0)
                {
                    var scaleChange = distance / _lastDistance;
                    ViewModel.ZoomTo(ViewModel.Scale * scaleChange, center);

                    var centerOffset = center - _lastCenter;
                    ViewModel.Move(centerOffset);

                    ViewModel.Fit = false;
                }

                _lastCenter = center;
                _lastDistance = distance;

                // 更新当前点
                _activePointers[e.Pointer] = currentPoint;
                break;
            }
        }

        e.Handled = true;
    }

    private static Vector GetCenter(IEnumerable<Point> points)
    {
        return new Vector(
            points.Average(p => p.X),
            points.Average(p => p.Y)
        );
    }

    private static double GetDistance(IEnumerable<Point> points)
    {
        var arr = points.Take(2).ToArray();
        if (arr.Length < 2) return 0;
        return Point.Distance(arr[0], arr[1]);
    }

    /// <summary>
    /// 监听抬手
    /// </summary>
    private void OnPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        var pointer = e.Pointer;

        // 触摸设备响应长按
        if (e.Pointer.Type is PointerType.Touch or PointerType.Pen)
        {
            // 取消长按计时
            CancelLongPress();

            // 未触发长按 且无图片时 打开图片
            if (!_isLongPressTriggered && ViewModel?.SourceBitmap == null)
                _ = ViewModel?.Main.FolderVM.OpenFilePickerAsync();
        }

        if (_activePointers.ContainsKey(pointer))
        {
            _activePointers.Remove(pointer);
            pointer.Capture(null);
        }

        // 当从双指变为单指时
        if (_wasTwoFingers && _activePointers.Count == 1)
        {
            // 清除拖动状态
            // _isDragging = false;

            // 更新最后位置为剩余手指的位置
            _lastPanPosition = _activePointers.Values.First();
        }

        if (_activePointers.Count < 2)
        {
            _lastDistance = 0;

            // 双指状态结束
            if (_activePointers.Count == 0)
            {
                _wasTwoFingers = false;
            }
        }

        if (_activePointers.Count == 0)
        {
            // _isDragging = false;
        }

        e.Handled = true;
    }

    /// <summary>
    /// 监听双击
    /// </summary>
    private void OnDoubleTapped(object? sender, TappedEventArgs e)
    {
        ViewModel?.ToggleFit(e.GetPosition(this));
        e.Handled = true;
    }

    private double SnapToPreset(double zoom)
    {
        // 预设缩放比例
        double[] presets = { 0.125, 0.25, 0.5, 1.0, 2.0, 4.0 };

        var closest = presets
            .Select(p => new { Value = p, Diff = Math.Abs(p - zoom) })
            .OrderBy(x => x.Diff)
            .FirstOrDefault();

        return closest?.Value ?? zoom;
    }

    #endregion


    ////////////////////
    // 打开图片
    ////////////////////

    #region OpenFile

    /// <summary>
    /// 拖入文件
    /// </summary>
    private void OnDragOver(object? sender, DragEventArgs e)
    {
        var hasValidFile = e.DataTransfer.TryGetFiles()?
            .OfType<IStorageFile>()
            .Any(f => ViewModel?.Main.FolderVM.IsImageFile(f.Name) == true) ?? false;

        e.DragEffects = hasValidFile ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private async Task OnDrop(object? sender, DragEventArgs e)
    {
        var files = e.DataTransfer.TryGetFiles()?
            .OfType<IStorageFile>()
            .ToList();
        if (files?.Count > 0 && files[0] is IStorageFile file)
        {
            // 通过 MainViewModel 处理拖拽文件 (逻辑同选择打开文件)
            await ViewModel?.Main.FolderVM.OpenImageAsync(file)!;
        }

        e.Handled = true;
    }

    // 移除 OpenImageAsync, LoadNewFolderAsync, LoadNewImageAsync 等方法
    // 这些逻辑已移到 MainViewModel

    #endregion

    /// <summary>
    /// 在 ImageView 内尝试执行鼠标手势快捷键（携带鼠标位置）
    /// </summary>
    private bool TryExecuteMouseGesture(MouseAction action, KeyModifiers rawMods, Point position)
    {
        if (ViewModel?.Main is null) return false;

        // 运行时应用苹果键盘修饰键映射，统一比较口径
        var mods = AppleKeyboardMapping.ApplyForRuntime(rawMods);
        var gesture = AppGesture.FromMouse(new MouseGestureEx(action, mods));

        // 根据手势查询配置的命令
        var cmdName = ViewModel.Main.Settings.GetCommandByGesture(gesture);
        if (string.IsNullOrEmpty(cmdName)) return false;

        var cmd = ViewModel.Main.ControlVM.GetCommandByName(cmdName);
        if (cmd?.CanExecute(null) == true)
        {
            var ctx = new PointerContext(new Vector(position.X, position.Y));
            cmd.Execute(ctx);
            return true;
        }

        return false;
    }
}

