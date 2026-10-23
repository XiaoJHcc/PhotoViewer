using System;
using System.Linq;
using System.Threading.Tasks;
using Android.Views;
using PhotoViewer.Android.Core;
using PhotoViewer.Shared;

namespace PhotoViewer.Android.Native;

/// <summary>
/// <c>--es pv-check input</c>（验收专用）：合成滚动事件核对 INPUT-04 精确/离散滚轮分流、按键重复设闸（INPUT-05），
/// 并通过控制栏虚拟节点快照与硬件键盘派发核对 TOOLBAR-11 焦点顺序与命令触发。
/// 事件与按键都走产品自己的入口，不另开测试分支。
/// </summary>
internal static partial class AndroidNativeChecks
{
    /// <summary>输入模式入口：先打开固定素材，再按 INPUT-04 → INPUT-05 → TOOLBAR-11 顺序核对。</summary>
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
        await RunStepAsync("INPUT-05 按键重复", CheckKeyRepeatAsync);
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
        void OnWheel(bool up, int metaState) => wheel++;
        root.Photo.WheelScrolled += OnWheel;
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
            root.Photo.WheelScrolled -= OnWheel;
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
        void OnWheel(bool up, int metaState) => wheel++;
        root.Photo.WheelScrolled += OnWheel;
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
            root.Photo.WheelScrolled -= OnWheel;
        }
    }

    /// <summary>INPUT-05：可重复命令按住连续执行，不可重复命令按住只执行一次。</summary>
    private static async Task CheckKeyRepeatAsync()
    {
        var main = _main!;
        var session = _session!;
        var files = main.FileVM.ThumbnailList.FilteredFiles;
        if (files.Count < 2) throw new InvalidOperationException($"输入素材不足两张（{files.Count}）");
        await SelectFirstFileAsync();

        bool consumedNext = AndroidNativeShell.HandleHardwareKey(Keycode.DpadRight, 0, repeated: true);
        await WaitAsync(() => !ReferenceEquals(main.CurrentFile, files[0]), "重复键翻页", 6000);
        Check("INPUT-05 可重复命令执行",
            consumedNext && ReferenceEquals(main.CurrentFile, files[1]),
            $"consumed={consumedNext} file={main.CurrentFile?.Name} expect={files[1].Name} total={files.Count}");

        var current = main.CurrentFile!;
        int before = current.Rating;
        // 评分的“3”走小键盘键：主行数字键在键码表里是 Num3，映射表当前解析不到（见下一条检查）。
        bool consumedRepeat = AndroidNativeShell.HandleHardwareKey(Keycode.Numpad3, 0, repeated: true);
        await Task.Delay(1200);
        int afterRepeat = current.Rating;
        bool consumedKey = AndroidNativeShell.HandleHardwareKey(Keycode.Numpad3, 0, repeated: false);
        await WaitAsync(() => current.Rating == 3, "评分键写入", 10000);
        Check("INPUT-05 不可重复命令设闸",
            consumedRepeat && afterRepeat == before && consumedKey && current.Rating == 3,
            $"repeatedConsumed={consumedRepeat} rating {before}->{afterRepeat}，非重复按键后={current.Rating} "
            + $"keyConsumed={consumedKey} image={session.Image != null}");

        // 默认绑定把评分 1–5 挂在主行数字键（D1–D5）上；硬件键盘的主行数字键码是 Num0–Num9。
        await main.SetRatingAsync(current, 0);
        await WaitAsync(() => current.Rating == 0, "星级复位", 8000);
        bool consumedDigit = AndroidNativeShell.HandleHardwareKey(Keycode.Num3, 0);
        await Task.Delay(1200);
        Check("INPUT-05 主行数字键映射",
            consumedDigit && current.Rating == 3,
            $"key=Num3(KEYCODE_3) consumed={consumedDigit} rating={current.Rating}（默认绑定为 D3）");
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
        bool consumedFit = AndroidNativeShell.HandleHardwareKey(Keycode.F, 0);
        await WaitAsync(() => root.Photo.IsFit != beforeFit, "F 切换适应态", 6000);
        bool afterFit = root.Photo.IsFit;

        var beforeFile = main.CurrentFile;
        bool consumedNext = AndroidNativeShell.HandleHardwareKey(Keycode.DpadRight, 0);
        await WaitAsync(() => !ReferenceEquals(main.CurrentFile, beforeFile), "Right 下一张", 6000);
        Check("TOOLBAR-11 键盘触发命令",
            consumedFit && consumedNext && afterFit != beforeFit && !ReferenceEquals(main.CurrentFile, beforeFile),
            $"fit {beforeFit}->{afterFit} fitConsumed={consumedFit} "
            + $"file={beforeFile?.Name}->{main.CurrentFile?.Name} nextConsumed={consumedNext}");
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
