using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Android.OS;
using Android.Views;
using PhotoViewer.Android.Core;
using PhotoViewer.Core.Input;
using PhotoViewer.Shared;

namespace PhotoViewer.Android.Native;

/// <summary>
/// <c>--es pv-check input</c>（验收专用）：合成滚动事件核对 INPUT-04 精确/离散滚轮分流与修饰键绑定优先（INPUT-03），
/// 并经 <c>Activity.DispatchKeyEvent</c> 构造带 <c>RepeatCount</c> 的真实按键事件核对 INPUT-05 首下/重复设闸与键位别名，
/// 再通过控制栏虚拟节点快照与硬件键盘派发核对 TOOLBAR-11 焦点顺序与命令触发。
/// 事件与按键都走产品自己的入口，不直接调用内部派发方法，也不另开测试分支。
/// </summary>
internal static partial class AndroidNativeChecks
{
    /// <summary>输入模式入口：先打开固定素材，再按 INPUT-03 → INPUT-04 → INPUT-05 → TOOLBAR-11 顺序核对。</summary>
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
        string nextName = main.ControlVM.EnabledControls.FirstOrDefault(item => item.Command == "Next")?.Name ?? "下一张";
        string fitName = main.ControlVM.EnabledControls.FirstOrDefault(item => item.Command == "Fit")?.Name ?? "缩放适应";
        bool commandsOk = commandNodes.Count == commands.Count
            && commandNodes.Any(node => node.Name.Contains(nextName, StringComparison.Ordinal))
            && commandNodes.Any(node => node.Name.Contains(fitName, StringComparison.Ordinal));

        Check("TOOLBAR-11 焦点顺序", named && rectOk && ratingOrder && commandsOk,
            $"nodes={nodes.Count} named={named} rectPositive={rectOk} row={row} "
            + $"rating={string.Join("/", ordered.Select(node => node.Name))} "
            + $"commands={commandNodes.Count}/{commands.Count} next={nextName} fit={fitName}");
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
}
