using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Android.OS;
using Android.Views;
using PhotoViewer.Android.Core;
using PhotoViewer.Core.Input;
using PhotoViewer.Shared;
using PhotoViewer.ViewModels.Settings;
using IOPath = System.IO.Path;

namespace PhotoViewer.Android.Native;

/// <summary>
/// <c>--es pv-check input</c>（验收专用）：合成滚动事件核对 INPUT-04 精确/离散滚轮分流与修饰键绑定优先（INPUT-03），
/// 并经 <c>Activity.DispatchKeyEvent</c> 构造带 <c>RepeatCount</c> 的真实按键事件核对 INPUT-05 首下/重复设闸与键位别名，
/// 再通过控制栏虚拟节点快照与硬件键盘派发核对 TOOLBAR-11 焦点顺序与命令触发，最后向主图产品入口
/// <c>AndroidPhotoView.OnTouchEvent</c> 派发真实 Down/Move/Up/Cancel（DownTime 起于第一下）核对触摸语义：
/// 放大态拖动跨长按超时、历史批次越容差、双指与 Cancel 后的长按抑制、静止长按与真实双击（INPUT-03/06/07）。
/// 事件与按键都走产品自己的入口，不直接调用内部派发方法，也不另开测试分支。
/// </summary>
internal static partial class AndroidNativeChecks
{
    /// <summary>输入模式入口：先打开固定素材，再按 INPUT-03 → INPUT-04 → INPUT-05 → TOOLBAR-11 → 触摸语义顺序核对。</summary>
    private static async Task RunInputChecksAsync()
    {
        var activity = _activity!;
        var main = _main!;
        var session = _session!;
        await WaitForScreenAsync();
        // 素材每次重写：星级断言要从 0 起跑，重复运行结论一致。
        string folder = EnsureLayoutFolder(activity, refresh: true);
        await main.FolderVM.OpenFolderAsync(new AndroidFolderRef(folder));
        await WaitAsync(() => main.CurrentFile != null && session.Image != null, "输入素材上屏", 20000);
        await Task.Delay(400);

        await RunStepAsync("INPUT-04 精确滚动", CheckPreciseScrollAsync);
        await RunStepAsync("INPUT-04 离散滚轮", CheckDiscreteWheelAsync);
        await RunStepAsync("INPUT-03 修饰键滚轮优先", CheckModifierWheelAsync);
        await RunStepAsync("INPUT-05 按键重复", CheckKeyRepeatAsync);
        await RunStepAsync("INPUT-05 键位别名", CheckKeyAliasesAsync);
        await RunStepAsync("TOOLBAR-11 焦点顺序", CheckFocusOrderAsync);
        await RunStepAsync("TOOLBAR-11 键盘触发", CheckKeyboardCommandsAsync);
        // 触摸语义换用手势专用素材（切到另一文件夹，排在最后不影响上面的列表索引断言）。
        await RunStepAsync("INPUT-03 拖动跨长按", CheckDragPastLongPressAsync);
        await RunStepAsync("INPUT-07 历史批次越容差", CheckHistoryBatchAsync);
        await RunStepAsync("INPUT-07 双指取消", CheckTwoFingerAsync);
        await RunStepAsync("INPUT-07 静止长按", CheckLongPressAsync);
        await RunStepAsync("INPUT-03 真实双击", CheckRealDoubleTapAsync);
    }

    /// <summary>INPUT-04：触控板精确滚动只平移照片，不派发滚轮命令。</summary>
    private static async Task CheckPreciseScrollAsync()
    {
        var root = _root!;
        root.Photo.ZoomToPhysical(2);
        await WaitAsync(() => !root.Photo.IsFit && Math.Abs(root.Photo.PhysicalScale - 2) < .05, "放大到物理 200%", 10000);
        await Task.Delay(350);

        var viewport = root.Photo.Viewport;
        double scale = viewport.Scale, panX = viewport.PanX, panY = viewport.PanY;
        int wheel = 0;
        var previousWheel = root.Photo.WheelCommand;
        root.Photo.WheelCommand = (up, metaState) =>
        {
            bool handled = previousWheel?.Invoke(up, metaState) ?? false;
            if (handled) wheel++;
            return handled;
        };
        try
        {
            bool consumed = root.Photo.OnGenericMotionEvent(ScrollEvent(.4f, InputSourceType.Touchpad));
            await Task.Delay(250);
            bool scaleKept = Math.Abs(viewport.Scale - scale) < 1e-6;
            bool moved = Math.Abs(viewport.PanX - panX) > .5 || Math.Abs(viewport.PanY - panY) > .5;
            Check("INPUT-04 精确滚动平移", consumed && scaleKept && moved && wheel == 0,
                $"consumed={consumed} scale={scale:0.###}->{viewport.Scale:0.###} "
                + $"pan=({panX:0.#},{panY:0.#})->({viewport.PanX:0.#},{viewport.PanY:0.#}) wheel={wheel}");
        }
        finally
        {
            root.Photo.WheelCommand = previousWheel;
        }
    }

    /// <summary>INPUT-04：整格离散滚轮一格一次命令；非整格增量按精确滚动平移，不累积成命令。</summary>
    private static async Task CheckDiscreteWheelAsync()
    {
        var root = _root!;
        var main = _main!;
        await SelectFirstFileAsync();
        root.Photo.ZoomToPhysical(2);
        await WaitAsync(() => !root.Photo.IsFit && Math.Abs(root.Photo.PhysicalScale - 2) < .05, "放大到物理 200%", 10000);
        await Task.Delay(300);

        int wheel = 0;
        var previousWheel = root.Photo.WheelCommand;
        root.Photo.WheelCommand = (up, metaState) =>
        {
            bool handled = previousWheel?.Invoke(up, metaState) ?? false;
            if (handled) wheel++;
            return handled;
        };
        try
        {
            // 非整格增量属高分辨率滚动：只平移，不派发命令，也不累积。
            var viewport = root.Photo.Viewport;
            double panX = viewport.PanX, panY = viewport.PanY;
            int wheelBefore = wheel;
            bool first = root.Photo.OnGenericMotionEvent(ScrollEvent(.5f, InputSourceType.Mouse));
            bool second = root.Photo.OnGenericMotionEvent(ScrollEvent(.5f, InputSourceType.Mouse));
            await Task.Delay(300);
            bool moved = Math.Abs(viewport.PanX - panX) > .5 || Math.Abs(viewport.PanY - panY) > .5;
            Check("INPUT-04 非整格增量按精确滚动",
                first && second && moved && wheel == wheelBefore,
                $"consumed={first}/{second} wheel={wheelBefore}->{wheel} "
                + $"pan=({panX:0.#},{panY:0.#})->({viewport.PanX:0.#},{viewport.PanY:0.#})");

            // 整格离散滚轮：一格一次命令。
            await SelectFirstFileAsync();
            var before = main.CurrentFile;
            int wheelBeforeClick = wheel;
            bool consumed = root.Photo.OnGenericMotionEvent(ScrollEvent(1f, InputSourceType.Mouse));
            await Task.Delay(300);
            Check("INPUT-04 离散滚轮一格一次",
                consumed && wheel == wheelBeforeClick + 1 && !ReferenceEquals(main.CurrentFile, before),
                $"consumed={consumed} wheel={wheelBeforeClick}->{wheel} file={before?.Name}->{main.CurrentFile?.Name}");
        }
        finally
        {
            root.Photo.WheelCommand = previousWheel;
        }
    }

    /// <summary>
    /// INPUT-03：带修饰键的滚轮先查用户绑定（默认 Ctrl+滚轮上 = 放大比例），命中后不再落到精确滚动平移。
    /// 用触控板来源的精确事件制造回归：若修饰键判定晚于精确/离散分流，这里只会平移而不缩放。
    /// </summary>
    private static async Task CheckModifierWheelAsync()
    {
        var root = _root!;
        await SelectFirstFileAsync();
        if (!root.Photo.IsFit) root.Photo.ToggleFit();
        await WaitAsync(() => root.Photo.IsFit, "回到适应态", 10000);
        await Task.Delay(350);

        var viewport = root.Photo.Viewport;
        double scale = viewport.Scale, panX = viewport.PanX, panY = viewport.PanY;
        int wheel = 0;
        var previousWheel = root.Photo.WheelCommand;
        root.Photo.WheelCommand = (up, metaState) =>
        {
            bool handled = previousWheel?.Invoke(up, metaState) ?? false;
            if (handled) wheel++;
            return handled;
        };
        try
        {
            // 触控板精确事件 + Ctrl：修饰键绑定优先于平移。
            bool consumed = root.Photo.OnGenericMotionEvent(
                ScrollEvent(-.6f, InputSourceType.Touchpad, 0, MetaKeyStates.CtrlOn));
            await Task.Delay(500);
            bool zoomed = viewport.Scale > scale + 1e-6;
            bool panKept = Math.Abs(viewport.PanX - panX) < .5 && Math.Abs(viewport.PanY - panY) < .5;
            Check("INPUT-03 Ctrl+精确滚动优先", consumed && wheel == 1 && zoomed && panKept,
                $"consumed={consumed} wheel={wheel} scale={scale:0.###}->{viewport.Scale:0.###} "
                + $"pan=({panX:0.#},{panY:0.#})->({viewport.PanX:0.#},{viewport.PanY:0.#})");
        }
        finally
        {
            root.Photo.WheelCommand = previousWheel;
        }
    }

    /// <summary>INPUT-05：首下、系统重复与不可重复命令的设闸，全部经 Activity.DispatchKeyEvent 的真实按键事件。</summary>
    private static async Task CheckKeyRepeatAsync()
    {
        var main = _main!;
        var files = main.FileVM.ThumbnailList.FilteredFiles;
        if (files.Count < 3) throw new InvalidOperationException($"输入素材不足三张（{files.Count}）");
        await SelectFirstFileAsync();

        // 首下：Down + RepeatCount 0。
        bool consumedDown = DispatchHardwareKey(Keycode.DpadRight);
        await WaitAsync(() => ReferenceEquals(main.CurrentFile, files[1]), "首下翻页", 6000);
        Check("INPUT-05 首下派发",
            consumedDown && ReferenceEquals(main.CurrentFile, files[1]),
            $"consumed={consumedDown} file={main.CurrentFile?.Name} expect={files[1].Name} total={files.Count}");

        // 系统重复：Down + RepeatCount>0；可重复命令继续执行。
        bool consumedRepeat = DispatchHardwareKey(Keycode.DpadRight, repeat: 1);
        await WaitAsync(() => ReferenceEquals(main.CurrentFile, files[2]), "重复翻页", 6000);
        Check("INPUT-05 可重复命令执行",
            consumedRepeat && ReferenceEquals(main.CurrentFile, files[2]),
            $"consumed={consumedRepeat} file={main.CurrentFile?.Name} expect={files[2].Name} total={files.Count}");

        // 不可重复命令：重复事件被消费但不再执行，随后的首下才写入。
        var rated = main.CurrentFile!;
        await main.SetRatingAsync(rated, 0);
        await WaitAsync(() => rated.Rating == 0, "星级复位", 8000);
        bool consumedRepeatRating = DispatchHardwareKey(Keycode.Numpad3, repeat: 2);
        await Task.Delay(1200);
        int afterRepeat = rated.Rating;
        bool consumedKey = DispatchHardwareKey(Keycode.Numpad3);
        await WaitAsync(() => rated.Rating == 3, "评分键写入", 10000);
        Check("INPUT-05 不可重复命令设闸",
            consumedRepeatRating && afterRepeat == 0 && consumedKey && rated.Rating == 3,
            $"repeatedConsumed={consumedRepeatRating} rating 0->{afterRepeat}，首下后={rated.Rating} keyConsumed={consumedKey}");

        // KEYCODE_ACTION_MULTIPLE 是输入法的多字符提交，不得冒充长按/重复去执行命令。
        await SelectFirstFileAsync();
        var beforeMultiple = main.CurrentFile;
        Check("INPUT-05 Multiple 不冒充长按",
            !MultipleAdvancesFile(Keycode.DpadRight) && ReferenceEquals(main.CurrentFile, beforeMultiple),
            $"file={beforeMultiple?.Name}->{main.CurrentFile?.Name}");

        // 默认绑定把评分 1–5 挂在主行数字键（D1–D5）上；硬件键盘的主行数字键码是 Num0–Num9。
        var current = main.CurrentFile!;
        await main.SetRatingAsync(current, 0);
        await WaitAsync(() => current.Rating == 0, "星级复位", 8000);
        bool consumedDigit = DispatchHardwareKey(Keycode.Num3);
        await Task.Delay(1200);
        Check("INPUT-05 主行数字键映射",
            consumedDigit && current.Rating == 3,
            $"key=Num3(KEYCODE_3) consumed={consumedDigit} rating={current.Rating}（默认绑定为 D3）");
    }

    /// <summary>INPUT-05：Enter/Backspace/Delete 与小键盘运算符/回车的键位别名对齐共享枚举。</summary>
    private static Task CheckKeyAliasesAsync()
    {
        var mismatches = new List<string>();
        void Expect(Keycode code, GestureKey expected)
        {
            var actual = AndroidInputMapping.FromKeyEvent(code, 0)?.Key?.Key;
            if (actual != expected) mismatches.Add($"{code}→{actual?.ToString() ?? "null"}≠{expected}");
        }

        Expect(Keycode.Enter, GestureKey.Return);
        Expect(Keycode.NumpadEnter, GestureKey.Return);
        Expect(Keycode.Del, GestureKey.Back);
        Expect(Keycode.ForwardDel, GestureKey.Delete);
        Expect(Keycode.NumpadAdd, GestureKey.OemPlus);
        Expect(Keycode.NumpadSubtract, GestureKey.OemMinus);
        Expect(Keycode.NumpadDot, GestureKey.Decimal);
        Expect(Keycode.NumpadMultiply, GestureKey.Multiply);
        Expect(Keycode.NumpadDivide, GestureKey.Divide);
        Check("INPUT-05 键位别名", mismatches.Count == 0,
            mismatches.Count == 0
                ? "Enter/NumpadEnter→Return、Del→Back、ForwardDel→Delete、小键盘 + − . * / 全部对齐"
                : string.Join("，", mismatches));
        // 系统返回键不参与命令绑定，录制路径也就不会写出永远派发不到的死绑定。
        Check("INPUT-05 系统返回键不绑定", AndroidInputMapping.FromKeyEvent(Keycode.Back, 0) == null,
            $"KEYCODE_BACK→{AndroidInputMapping.FromKeyEvent(Keycode.Back, 0)?.ToDisplayString() ?? "null"}");
        return Task.CompletedTask;
    }

    /// <summary>TOOLBAR-11：虚拟节点按视觉顺序暴露名称、评分方向与命令入口。</summary>
    private static async Task CheckFocusOrderAsync()
    {
        var root = _root!;
        var main = _main!;
        await Task.Delay(200);
        var nodes = root.ControlBar.NodesForChecks();
        if (nodes.Count == 0)
        {
            Check("TOOLBAR-11 焦点顺序", false, "控制栏没有虚拟节点");
            return;
        }

        bool named = nodes.All(node => !string.IsNullOrWhiteSpace(node.Name));
        bool rectOk = nodes.All(node => node.Width > 0 && node.Height > 0);
        bool row = main.IsRowLayout;

        var ratingNodes = nodes.Where(node => node.Kind == nameof(ControlBarGroupKind.Rating)).ToList();
        // 行布局沿主轴（横）0→5，列布局反序 5→0；按视觉位置排序后再比名称。
        var ordered = row
            ? ratingNodes.OrderBy(node => node.Y).ThenBy(node => node.X).ToList()
            : ratingNodes.OrderBy(node => node.X).ThenBy(node => node.Y).ToList();
        var expected = row
            ? new[] { "无星级", "1 星", "2 星", "3 星", "4 星", "5 星" }
            : new[] { "5 星", "4 星", "3 星", "2 星", "1 星", "无星级" };
        bool ratingOrder = ordered.Count == expected.Length && ordered.Select(node => node.Name).SequenceEqual(expected);

        var commandNodes = nodes.Where(node => node.Kind == nameof(ControlBarGroupKind.Command)).ToList();
        var commands = root.ControlBar.Commands;
        // TOOLBAR-04：读屏名称必须是「名称（主 / 次）」。期望值按设计格式在本套件独立拼装，
        // 不复用共享格式化函数，共享实现写坏格式时这里照样失败。
        var expectedNames = main.ControlVM.EnabledControls.Select(ExpectedCommandName).ToList();
        bool commandsOk = commandNodes.Count == commands.Count
            && commandNodes.Select(node => node.Name).SequenceEqual(expectedNames);

        Check("TOOLBAR-11 焦点顺序", named && rectOk && ratingOrder && commandsOk,
            $"nodes={nodes.Count} named={named} rectPositive={rectOk} row={row} "
            + $"rating={string.Join("/", ordered.Select(node => node.Name))} "
            + $"commands={commandNodes.Count}/{commands.Count} "
            + $"names=[{string.Join(" | ", commandNodes.Select(node => node.Name))}] expected=[{string.Join(" | ", expectedNames)}]");
    }

    /// <summary>TOOLBAR-04：命令读屏名称按设计格式在此独立拼装（空槽省略、主次相同只显示一次）。</summary>
    private static string ExpectedCommandName(SettingsViewModel.HotkeyItem hotkey)
    {
        var slots = new List<string>();
        if (hotkey.PrimaryHotkey != null) slots.Add(hotkey.PrimaryHotkeyText);
        if (hotkey.SecondaryHotkey != null && (slots.Count == 0 || slots[0] != hotkey.SecondaryHotkeyText))
            slots.Add(hotkey.SecondaryHotkeyText);
        return slots.Count == 0 ? hotkey.Name : $"{hotkey.Name}（{string.Join(" / ", slots)}）";
    }

    /// <summary>TOOLBAR-11：硬件键盘按键经共享命令表驱动主图 Fit 与翻页。</summary>
    private static async Task CheckKeyboardCommandsAsync()
    {
        var root = _root!;
        var main = _main!;
        var files = main.FileVM.ThumbnailList.FilteredFiles;
        if (files.Count >= 2 && ReferenceEquals(main.CurrentFile, files[^1])) await SelectFirstFileAsync();

        bool beforeFit = root.Photo.IsFit;
        bool consumedFit = DispatchHardwareKey(Keycode.F);
        await WaitAsync(() => root.Photo.IsFit != beforeFit, "F 切换适应态", 6000);
        bool afterFit = root.Photo.IsFit;

        var beforeFile = main.CurrentFile;
        bool consumedNext = DispatchHardwareKey(Keycode.DpadRight);
        await WaitAsync(() => !ReferenceEquals(main.CurrentFile, beforeFile), "Right 下一张", 6000);
        Check("TOOLBAR-11 键盘触发命令",
            consumedFit && consumedNext && afterFit != beforeFit && !ReferenceEquals(main.CurrentFile, beforeFile),
            $"fit {beforeFit}->{afterFit} fitConsumed={consumedFit} "
            + $"file={beforeFile?.Name}->{main.CurrentFile?.Name} nextConsumed={consumedNext}");
    }

    /// <summary>构造真实按键事件并经 Activity 的键盘入口派发（与系统路径同一解码与派发代码）。</summary>
    /// <param name="key">原生键码。</param>
    /// <param name="repeat">系统重复次数；0 为首次按下。</param>
    /// <param name="action">按键动作。</param>
    /// <returns>是否被消费。</returns>
    private static bool DispatchHardwareKey(Keycode key, int repeat = 0, KeyEventActions action = KeyEventActions.Down)
    {
        long now = SystemClock.UptimeMillis();
        var e = new KeyEvent(now, now, action, key, repeat, MetaKeyStates.None);
        return _activity!.DispatchKeyEvent(e);
    }

    /// <summary>派发一次 <see cref="KeyEventActions.Multiple"/>，返回是否被当成命令执行（翻页）。</summary>
    /// <param name="key">原生键码。</param>
    /// <returns>派发后当前照片是否变化。</returns>
    private static bool MultipleAdvancesFile(Keycode key)
    {
        var main = _main!;
        var before = main.CurrentFile;
        DispatchHardwareKey(key, action: KeyEventActions.Multiple);
        return !ReferenceEquals(main.CurrentFile, before);
    }

    /// <summary>回到筛选列表首项（各步之间复位当前照片，避免上一步的翻页影响下一步结论）。</summary>
    private static async Task SelectFirstFileAsync()
    {
        var main = _main!;
        var session = _session!;
        var files = main.FileVM.ThumbnailList.FilteredFiles;
        if (files.Count == 0) throw new InvalidOperationException("筛选列表为空");
        if (!ReferenceEquals(main.CurrentFile, files[0])) main.FileVM.ThumbnailList.SelectImageCommand(files[0]);
        await WaitAsync(() => ReferenceEquals(main.CurrentFile, files[0]) && session.Image != null, "回到首张", 10000);
        await Task.Delay(150);
    }

    // ── 触摸语义（真实 MotionEvent 走产品入口）────────────────────────────────────

    /// <summary>手势素材文件名。</summary>
    private const string GesturePhotoName = "gesture-large.jpg";

    /// <summary>未收尾手势的按下时间（0 = 没有按着的手指），异常收尾据此补一条 Cancel。</summary>
    private static long _touchDownTime;

    /// <summary>系统径向触控容差（px）：与产品视图读同一份 <see cref="ViewConfiguration"/>，判定口径一致。</summary>
    private static int TouchSlopPx => Math.Max(1, ViewConfiguration.Get(_activity!)?.ScaledTouchSlop ?? 8);

    /// <summary>单个拖动采样点的位移（px）：远超系统径向容差，必然被判成拖动而不是点按。</summary>
    private static float DragStep() => Math.Max(24, TouchSlopPx * 2);

    /// <summary>系统长按 + 点按超时再加余量：静止按住这么久之后仍不得冒出长按/点按。</summary>
    private static int LongPressWaitMs =>
        (int)ViewConfiguration.LongPressTimeout + (int)ViewConfiguration.TapTimeout + 250;

    /// <summary>主图视图中心的触摸像素坐标（视图自身坐标系）。</summary>
    private static (float X, float Y) PhotoCenter()
    {
        var photo = _root!.Photo;
        return (photo.Width / 2f, photo.Height / 2f);
    }

    /// <summary>生成可放大到物理 100% 的手势素材；拖动检查另等待 Full 就绪。</summary>
    private static string EnsureGestureFolder()
    {
        var folder = IOPath.Combine(_activity!.FilesDir!.AbsolutePath, "pv-check-gestures");
        Directory.CreateDirectory(folder);
        var path = IOPath.Combine(folder, GesturePhotoName);
        if (!File.Exists(path) || new FileInfo(path).Length == 0) WriteCheckJpeg(path, 2600, 1800);
        return folder;
    }

    /// <summary>切到手势素材并等它上屏（各步骤独立起跑，前一步失败也能得到自己的结论）。</summary>
    private static async Task EnsureGesturePhotoAsync()
    {
        var main = _main!;
        var session = _session!;
        if (main.CurrentFile?.Name == GesturePhotoName && session.Image != null) return;
        await OpenFolderAndWaitAsync(EnsureGestureFolder());
        await WaitAsync(() => main.CurrentFile?.Name == GesturePhotoName && session.Image != null, "手势素材上屏");
        // EXIF/缩略图落定后再开始计数，避免它们的通知冒充手势引起的状态变化。
        await Task.Delay(800);
    }

    /// <summary>等到视口缩放稳定（动画结束），且放大态不再挂代理；返回稳定后的物理倍率。</summary>
    /// <param name="label">超时说明。</param>
    private static async Task<double> WaitStableViewportAsync(string label)
    {
        var photo = _root!.Photo;
        var session = _session!;
        var deadline = DateTime.UtcNow.AddSeconds(20);
        double previous = double.NaN;
        while (DateTime.UtcNow < deadline)
        {
            double scale = photo.PhysicalScale;
            if (!double.IsNaN(previous) && Math.Abs(scale - previous) < 1e-3 && (photo.IsFit || !session.IsProxy))
            {
                // 收尾动画最后几个百分点也落在容差里：再等一帧以上，保证手势读到的是终点状态。
                await Task.Delay(250);
                return scale;
            }
            previous = scale;
            await Task.Delay(120);
        }
        throw new TimeoutException($"等待视口稳定超时：{label}"
            + $"（physical={photo.PhysicalScale:0.###} fit={photo.IsFit} proxy={session.IsProxy}）");
    }

    /// <summary>构造一条触摸事件：DownTime 由第一下给出（不拿当前时间冒充），源为触摸屏；调用方释放。</summary>
    private static MotionEvent PhotoTouch(long downTime, long eventTime, MotionEventActions action, float x, float y)
    {
        var e = MotionEvent.Obtain(downTime, eventTime, action, x, y, MetaKeyStates.None)!;
        e.SetSource(InputSourceType.Touchscreen);
        return e;
    }

    /// <summary>把一条单指触摸事件交给产品入口 <c>OnTouchEvent</c>；Down/Up 维护异常收尾用的按下时间。</summary>
    /// <param name="action">触摸动作。</param>
    /// <param name="downTime">本手势第一下的时间。</param>
    /// <param name="x">视图内横坐标（px）。</param>
    /// <param name="y">视图内纵坐标（px）。</param>
    /// <returns>是否被消费。</returns>
    private static bool SendPhotoTouch(MotionEventActions action, long downTime, float x, float y)
    {
        using var e = PhotoTouch(downTime, SystemClock.UptimeMillis(), action, x, y);
        if (action == MotionEventActions.Down) _touchDownTime = downTime;
        bool consumed = _root!.Photo.OnTouchEvent(e);
        if (action is MotionEventActions.Up or MotionEventActions.Cancel) _touchDownTime = 0;
        return consumed;
    }

    /// <summary>把一条多指触摸事件走同一产品入口（Source/Pressure/Size 由 <see cref="MultiTouchEvent"/> 构造）。</summary>
    /// <param name="action">触摸动作。</param>
    /// <param name="actionIndex">触发动作的指针下标。</param>
    /// <param name="downTime">本手势第一下的时间。</param>
    /// <param name="pointers">当前全部指针。</param>
    /// <returns>是否被消费。</returns>
    private static bool SendPhotoMultiTouch(MotionEventActions action, int actionIndex, long downTime,
        params (int Id, float X, float Y)[] pointers)
    {
        using var e = MultiTouchEvent(downTime, SystemClock.UptimeMillis(), action, actionIndex, pointers);
        if (action == MotionEventActions.Down) _touchDownTime = downTime;
        bool consumed = _root!.Photo.OnTouchEvent(e);
        if (action == MotionEventActions.Up) _touchDownTime = 0;
        return consumed;
    }

    /// <summary>异常收尾：仍按着的手指补一条 Cancel，下一步不继承按下状态。</summary>
    /// <param name="x">视图内横坐标（px）。</param>
    /// <param name="y">视图内纵坐标（px）。</param>
    private static void CancelActiveTouch(float x, float y)
    {
        long downTime = _touchDownTime;
        if (downTime == 0) return;
        SendPhotoTouch(MotionEventActions.Cancel, downTime, x, y);
    }

    /// <summary>
    /// 单指拖动一次并跨过系统长按超时：越容差后停住等到长按/点按超时，再继续拖到抬起。
    /// 返回拖动前后的视口平移差与事件是否都被消费。
    /// </summary>
    private static async Task<(double DeltaX, double DeltaY, bool Consumed)> DragAcrossLongPressAsync(
        float x, float y, float step)
    {
        var viewport = _root!.Photo.Viewport;
        double startX = viewport.PanX, startY = viewport.PanY;
        long downTime = SystemClock.UptimeMillis();
        bool consumed = SendPhotoTouch(MotionEventActions.Down, downTime, x, y);
        await Task.Delay(16);
        consumed &= SendPhotoTouch(MotionEventActions.Move, downTime, x - step, y);
        consumed &= SendPhotoTouch(MotionEventActions.Move, downTime, x - step * 2, y + step * .5f);
        await Task.Delay(LongPressWaitMs);
        consumed &= SendPhotoTouch(MotionEventActions.Move, downTime, x - step * 3, y);
        consumed &= SendPhotoTouch(MotionEventActions.Move, downTime, x - step * 4, y - step * .5f);
        consumed &= SendPhotoTouch(MotionEventActions.Up, downTime, x - step * 4, y - step * .5f);
        return (viewport.PanX - startX, viewport.PanY - startY, consumed);
    }

    /// <summary>
    /// 一组真实轻点两下（Down/Up + Down/Up）：两下间距落在系统双击窗口内且大于最小间隔，交给手势识别器自行判成双击，
    /// 不直接调 <c>DoubleTapAt</c>。返回第二下 Down 刚落地时的物理倍率——若同一下 Down 把刚起的动画取消，
    /// 这里已经在终点。
    /// </summary>
    private static async Task<(bool Consumed, double ImmediateScale)> DoubleTapAsync(float x, float y)
    {
        long first = SystemClock.UptimeMillis();
        bool consumed = SendPhotoTouch(MotionEventActions.Down, first, x, y);
        await Task.Delay(30);
        consumed &= SendPhotoTouch(MotionEventActions.Up, first, x, y);
        await Task.Delay(60);
        long second = SystemClock.UptimeMillis();
        consumed &= SendPhotoTouch(MotionEventActions.Down, second, x, y);
        double immediate = _root!.Photo.PhysicalScale;
        await Task.Delay(30);
        consumed &= SendPhotoTouch(MotionEventActions.Up, second, x, y);
        return (consumed, immediate);
    }

    /// <summary>放大到物理 200% 并把平移摆回中心，保证各方向都还有平移余量。</summary>
    private static async Task ZoomForGestureAsync(string label)
    {
        var photo = _root!.Photo;
        photo.ZoomToPhysical(2);
        await WaitStableViewportAsync(label);
        photo.CenterOnNormalizedPoint(.5, .5);
    }

    /// <summary>
    /// INPUT-03：放大态单指拖动跨过系统长按超时仍只平移——分析栏隐藏与显示两态各一次；
    /// 这一次手势里不得冒长按、点按、翻页，也不得让分析栏重算（<c>HostChanged</c> 恒零）。
    /// </summary>
    private static async Task CheckDragPastLongPressAsync()
    {
        var root = _root!;
        var main = _main!;
        var center = PhotoCenter();
        bool previousVisible = main.IsAnalysisViewVisible;
        bool previousImmediate = main.Settings.AnalysisImmediateCompute;
        using var probe = new GestureProbe(root.Photo, root.AnalysisHostForChecks);
        try
        {
            await EnsureGesturePhotoAsync();
            // 即时推理默认关：拖动期间不让 DINO/CV 改写分析栏内容，把结论收在触摸语义上。
            main.Settings.AnalysisImmediateCompute = false;
            await Task.Delay(300);
            foreach (bool visible in new[] { false, true })
            {
                main.IsAnalysisViewVisible = visible;
                await Task.Delay(400);
                await ZoomForGestureAsync($"放大到物理 200%（分析栏{(visible ? "显示" : "隐藏")}）");
                probe.Reset();
                var (deltaX, deltaY, consumed) = await DragAcrossLongPressAsync(center.X, center.Y, DragStep());
                bool panned = Math.Abs(deltaX) > 1 || Math.Abs(deltaY) > 1;
                bool quiet = probe.LongPress == 0 && probe.Tap == 0 && probe.Swipe == 0 && probe.HostChanged == 0;
                Check($"INPUT-03 拖动跨长按（分析栏{(visible ? "显示" : "隐藏")}）",
                    consumed && panned && quiet && !root.Photo.IsFit,
                    $"consumed={consumed} pan=({deltaX:0.#},{deltaY:0.#}) fit={root.Photo.IsFit} "
                    + $"physical={root.Photo.PhysicalScale:0.##} {probe.Describe}");
            }
        }
        finally
        {
            CancelActiveTouch(center.X, center.Y);
            root.DismissPhotoMenu();
            main.IsAnalysisViewVisible = previousVisible;
            main.Settings.AnalysisImmediateCompute = previousImmediate;
        }
    }

    /// <summary>
    /// INPUT-07：一个 <see cref="MotionEvent"/> 里的历史批次先离开容差再回到起点——判定必须看历史采样，
    /// 否则手指"没动"就长按弹菜单。历史越界即把点击识别器取消，随后按住跨过长按超时也不得有长按/点按。
    /// </summary>
    private static async Task CheckHistoryBatchAsync()
    {
        var root = _root!;
        var center = PhotoCenter();
        using var probe = new GestureProbe(root.Photo, root.AnalysisHostForChecks);
        try
        {
            await EnsureGesturePhotoAsync();
            await ZoomForGestureAsync("放大到物理 200%");
            probe.Reset();
            var viewport = root.Photo.Viewport;
            double panX = viewport.PanX, panY = viewport.PanY;
            long downTime = SystemClock.UptimeMillis();
            SendPhotoTouch(MotionEventActions.Down, downTime, center.X, center.Y);
            await Task.Delay(30);
            int history;
            // 历史：离开容差 → 回到起点；当前采样仍在起点，位移判定只能靠历史才看得见。
            using (var move = PhotoTouch(downTime, SystemClock.UptimeMillis(), MotionEventActions.Move, center.X, center.Y))
            {
                long time = move.EventTime;
                move.AddBatch(time + 1, center.X - DragStep() * 3, center.Y, 1, 1, MetaKeyStates.None);
                move.AddBatch(time + 2, center.X, center.Y, 1, 1, MetaKeyStates.None);
                history = move.HistorySize;
                root.Photo.OnTouchEvent(move);
            }
            await Task.Delay(LongPressWaitMs);
            SendPhotoTouch(MotionEventActions.Up, downTime, center.X, center.Y);
            double movedX = viewport.PanX - panX, movedY = viewport.PanY - panY;
            Check("INPUT-07 历史批次越容差不弹菜单",
                history >= 2 && Math.Abs(movedX) < 1 && Math.Abs(movedY) < 1
                && probe.LongPress == 0 && probe.Tap == 0,
                $"history={history} pan=({movedX:0.#},{movedY:0.#}) {probe.Describe}");
        }
        finally
        {
            CancelActiveTouch(center.X, center.Y);
            root.DismissPhotoMenu();
        }
    }

    /// <summary>
    /// INPUT-07：双指按住不动、未达捏合阈值就等过长按超时也不得弹菜单（第二指落指即取消点击识别器）；
    /// 抬起一指后剩下的手指不得沿用被抬手指的坐标，首个单指移动只能平移这一小段（无指针跳变）。
    /// </summary>
    private static async Task CheckTwoFingerAsync()
    {
        var root = _root!;
        var photo = root.Photo;
        var center = PhotoCenter();
        using var probe = new GestureProbe(photo, root.AnalysisHostForChecks);
        try
        {
            await EnsureGesturePhotoAsync();
            await ZoomForGestureAsync("放大到物理 200%");
            probe.Reset();
            var viewport = photo.Viewport;
            double scale = viewport.Scale, density = photo.Density;
            // 两指相距足够远：真出现指针跳变会明显超过一小段平移。
            float gap = DragStep() * 6;
            long downTime = SystemClock.UptimeMillis();
            bool consumed = SendPhotoMultiTouch(MotionEventActions.Down, 0, downTime, (0, center.X, center.Y));
            consumed &= SendPhotoMultiTouch(MotionEventActions.PointerDown, 1, downTime,
                (0, center.X, center.Y), (1, center.X + gap, center.Y));
            await Task.Delay(LongPressWaitMs);
            int duringHold = probe.LongPress;
            bool scaleKept = Math.Abs(viewport.Scale - scale) < 1e-6;
            Check("INPUT-07 双指按住不弹菜单",
                consumed && duringHold == 0 && probe.LongPress == 0 && scaleKept,
                $"consumed={consumed} longPressDuringHold={duringHold} scale={scale:0.###}->{viewport.Scale:0.###} "
                + probe.Describe);

            double panX = viewport.PanX, panY = viewport.PanY;
            SendPhotoMultiTouch(MotionEventActions.PointerUp, 0, downTime,
                (0, center.X, center.Y), (1, center.X + gap, center.Y));
            await Task.Delay(30);
            float step = DragStep();
            SendPhotoMultiTouch(MotionEventActions.Move, 0, downTime, (1, center.X + gap + step, center.Y));
            SendPhotoMultiTouch(MotionEventActions.Up, 0, downTime, (1, center.X + gap + step, center.Y));
            double movedX = viewport.PanX - panX, movedY = viewport.PanY - panY;
            double expected = step / density;
            bool jumped = Math.Abs(movedX - expected) > 4 || Math.Abs(movedY) > 4;
            Check("INPUT-03 抬指后无指针跳变",
                !jumped && Math.Abs(movedX) > .5 && probe.Tap == 0 && probe.Swipe == 0,
                $"pan=({movedX:0.#},{movedY:0.#}) expected≈({expected:0.#},0) gap={gap:0}px {probe.Describe}");
        }
        finally
        {
            CancelActiveTouch(center.X, center.Y);
            root.DismissPhotoMenu();
        }
    }

    /// <summary>
    /// INPUT-07：Cancel 之后不再弹菜单；随后静止按住真长按恰好一次、菜单真的弹出，
    /// 长按期间不拖动、抬起不翻页也不点按（适应态看翻页、放大态看平移）。
    /// </summary>
    private static async Task CheckLongPressAsync()
    {
        var root = _root!;
        var photo = root.Photo;
        var center = PhotoCenter();
        using var probe = new GestureProbe(photo, root.AnalysisHostForChecks);
        try
        {
            await EnsureGesturePhotoAsync();
            if (!photo.IsFit)
            {
                photo.ToggleFit();
                await WaitStableViewportAsync("回到适应态");
            }
            probe.Reset();
            long downTime = SystemClock.UptimeMillis();
            SendPhotoTouch(MotionEventActions.Down, downTime, center.X, center.Y);
            SendPhotoTouch(MotionEventActions.Move, downTime, center.X - DragStep(), center.Y);
            SendPhotoTouch(MotionEventActions.Cancel, downTime, center.X - DragStep(), center.Y);
            await Task.Delay(LongPressWaitMs);
            Check("INPUT-07 Cancel 后不长按", probe.LongPress == 0 && probe.Tap == 0, probe.Describe);

            foreach (bool fit in new[] { true, false })
            {
                if (fit != photo.IsFit)
                {
                    photo.ToggleFit();
                    await WaitStableViewportAsync(fit ? "回到适应态" : "放大到物理 100%");
                }
                probe.Reset();
                var viewport = photo.Viewport;
                double panX = viewport.PanX, panY = viewport.PanY;
                downTime = SystemClock.UptimeMillis();
                SendPhotoTouch(MotionEventActions.Down, downTime, center.X, center.Y);
                await Task.Delay(LongPressWaitMs);
                int afterHold = probe.LongPress;
                bool menuShown = root.DismissPhotoMenu();
                // 长按之后继续拖再抬起：不得平移、不得翻页、不得点按。
                SendPhotoTouch(MotionEventActions.Move, downTime, center.X + DragStep() * 8, center.Y);
                SendPhotoTouch(MotionEventActions.Up, downTime, center.X + DragStep() * 8, center.Y);
                await Task.Delay(120);
                bool noPan = Math.Abs(viewport.PanX - panX) < .5 && Math.Abs(viewport.PanY - panY) < .5;
                Check($"INPUT-07 静止长按恰好一次（{(fit ? "适应" : "放大")}）",
                    afterHold == 1 && menuShown && probe.LongPress == 1 && probe.Tap == 0 && probe.Swipe == 0
                    && noPan && photo.IsFit == fit,
                    $"longPress={afterHold} menu={menuShown} fit={photo.IsFit} "
                    + $"pan=({viewport.PanX - panX:0.#},{viewport.PanY - panY:0.#}) {probe.Describe}");
            }
        }
        finally
        {
            CancelActiveTouch(center.X, center.Y);
            root.DismissPhotoMenu();
        }
    }

    /// <summary>
    /// INPUT-03：两组真实 Down/Up 组成的双击（不经 <c>DoubleTapAt</c>）在适应与物理 100% 间往返；
    /// 第二下 Down 刚落地时的缩放仍须在动画途中——同一下 Down 若把刚起的动画取消，会直接跳到终点。
    /// </summary>
    private static async Task CheckRealDoubleTapAsync()
    {
        var root = _root!;
        var photo = root.Photo;
        var center = PhotoCenter();
        using var probe = new GestureProbe(photo, root.AnalysisHostForChecks);
        try
        {
            await EnsureGesturePhotoAsync();
            if (!photo.IsFit)
            {
                photo.ToggleFit();
                await WaitStableViewportAsync("回到适应态");
            }
            probe.Reset();
            double fitScale = photo.PhysicalScale;
            var (consumed, immediate) = await DoubleTapAsync(center.X, center.Y);
            await Task.Delay(600);
            Check("INPUT-03 真实双击到 100%",
                consumed && !photo.IsFit && Math.Abs(photo.PhysicalScale - 1) < .05
                && Math.Abs(immediate - fitScale) < .05 && probe.Swipe == 0,
                $"consumed={consumed} fit={fitScale:0.##} immediate={immediate:0.##} "
                + $"now={photo.PhysicalScale:0.##} isFit={photo.IsFit} {probe.Describe}");

            // 双击窗口（300ms）过后再点第二组，避免被当成连续双击的延续。
            await Task.Delay(400);
            probe.Reset();
            (consumed, immediate) = await DoubleTapAsync(center.X, center.Y);
            await Task.Delay(600);
            Check("INPUT-03 真实双击回适应",
                consumed && photo.IsFit && Math.Abs(immediate - 1) < .05 && probe.Swipe == 0,
                $"consumed={consumed} immediate={immediate:0.##} isFit={photo.IsFit} "
                + $"physical={photo.PhysicalScale:0.##} {probe.Describe}");
        }
        finally
        {
            CancelActiveTouch(center.X, center.Y);
            root.DismissPhotoMenu();
        }
    }

    /// <summary>
    /// 主图手势与分析宿主通知的计数订阅器（验收用）：长按/点按/翻页回调与 <c>HostChanged</c> 各记次数，
    /// <see cref="Reset"/> 后重新起算，Dispose 即退订，异常路径也不会留下订阅。
    /// </summary>
    private sealed class GestureProbe : IDisposable
    {
        private readonly AndroidPhotoView _photo;
        private readonly AndroidAnalysisHost _host;
        private readonly Action<float, float> _longPress;
        private readonly Action _tap;
        private readonly Action<int> _swipe;
        private readonly Action _hostChanged;

        /// <summary>订阅三条手势回调与宿主状态变化。</summary>
        /// <param name="photo">主图视图。</param>
        /// <param name="host">分析宿主。</param>
        public GestureProbe(AndroidPhotoView photo, AndroidAnalysisHost host)
        {
            _photo = photo;
            _host = host;
            _longPress = (_, _) => LongPress++;
            _tap = () => Tap++;
            _swipe = _ => Swipe++;
            _hostChanged = () => HostChanged++;
            photo.LongPressRequested += _longPress;
            photo.TapRequested += _tap;
            photo.SwipeRequested += _swipe;
            host.HostChanged += _hostChanged;
        }

        /// <summary>长按回调次数。</summary>
        public int LongPress { get; private set; }
        /// <summary>点按回调次数。</summary>
        public int Tap { get; private set; }
        /// <summary>翻页回调次数。</summary>
        public int Swipe { get; private set; }
        /// <summary>分析宿主状态变化次数。</summary>
        public int HostChanged { get; private set; }

        /// <summary>清零计数（每次手势前复位）。</summary>
        public void Reset()
        {
            LongPress = 0;
            Tap = 0;
            Swipe = 0;
            HostChanged = 0;
        }

        /// <summary>报告用的一行读数。</summary>
        public string Describe => $"longPress={LongPress} tap={Tap} swipe={Swipe} host={HostChanged}";

        /// <summary>退订全部回调。</summary>
        public void Dispose()
        {
            _photo.LongPressRequested -= _longPress;
            _photo.TapRequested -= _tap;
            _photo.SwipeRequested -= _swipe;
            _host.HostChanged -= _hostChanged;
        }
    }
}
