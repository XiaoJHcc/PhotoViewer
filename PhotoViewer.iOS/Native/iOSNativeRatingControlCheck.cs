using System.Diagnostics;
using CoreGraphics;
using Foundation;
using PhotoViewer.Core;
using PhotoViewer.Core.Design;
using PhotoViewer.Core.Exif;
using PhotoViewer.Core.Image;
using PhotoViewer.Core.Platform;
using PhotoViewer.Core.Settings;
using PhotoViewer.Core.Storage;
using PhotoViewer.Shared;
using PhotoViewer.ViewModels.Main;
using UIKit;

namespace PhotoViewer.iOS.Native;

/// <summary>
/// 验收专用（非产品行为，测试钩子）：<c>--pv-native-rating-control-check &lt;目录&gt;</c> 程序化核对星级控件与星级弹窗——
/// [RATING-07] 展开/紧凑两态的形态阈值、点按与滑动预览（触感计数）、取消带与取消、紧凑态弹窗落位与同手势续接、
/// 弹窗外点/ Esc / 切图 / 入口移动四种收起路径、减少动态效果、竖排紧凑弹窗、读屏增减与禁用态；
/// [RATING-04] 冲突三角打开来源弹窗、整组写回与冲突消解、卡片三角入口；iPhone 竖屏紧凑/展开、横屏两种刘海方向
/// （真实 <c>RequestGeometryUpdate</c> 旋转、控制列竖排次序与方向表、旋转收起弹窗）；iPad 宽形态行/列控制栏的
/// 星级分组、胶囊尺寸、命令圆按钮与 [TOOLBAR-03]「显示评分」显隐。输入只经接缝方法驱动（星排/控件的 Track*、
/// 读屏入口、卡片按钮动作），坐标一律由真实视图 frame 换算。
/// 素材先复制到 App 自建的可写工作副本 <c>Documents/rating-control-fixtures-work</c>，收尾删除；
/// 临时设置、方向、弹窗、减少动态效果提供器与临时视图全部还原。
/// 报告写 Documents/PhotoViewer/native-rating-control-checks.txt，打印 <c>[NativeSmoke] PASS rating-control (n checks)</c> / FAIL。
/// </summary>
internal static class iOSNativeRatingControlCheck
{
    /// <summary>验收开关。</summary>
    private const string Switch = "--pv-native-rating-control-check";
    /// <summary>截图检查点开关。</summary>
    private const string SnapshotsSwitch = "--pv-native-ui-snapshots";
    /// <summary>截图检查点停留时间（覆盖外部轮询与截图）。</summary>
    private static readonly TimeSpan StageHold = TimeSpan.FromMilliseconds(1500);

    /// <summary>解析验收开关；命中即以给定目录启动异步核对。</summary>
    /// <param name="args">进程启动参数。</param>
    /// <param name="root">原生根控制器。</param>
    /// <param name="session">原生展示会话。</param>
    public static void MaybeStart(string[] args, iOSNativeRootViewController root, PhotoLoadSession session)
    {
        string? directory = null;
        for (int index = 0; index + 1 < args.Length; index++)
        {
            if (string.Equals(args[index], Switch, StringComparison.Ordinal)) directory = args[index + 1];
        }
        if (directory == null) return;
        bool snapshots = args.Contains(SnapshotsSwitch, StringComparer.Ordinal);
        _ = RunAsync(root, session, ResolveDocumentsPath(directory), snapshots);
    }

    /// <summary>相对路径按沙盒 Documents 解析（与壳层 --pv-open 同一口径）。</summary>
    /// <param name="path">绝对或相对路径。</param>
    /// <returns>绝对路径。</returns>
    private static string ResolveDocumentsPath(string path)
        => Path.IsPathRooted(path) ? path : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), path);

    /// <summary>核对主流程：建工作副本 → 逐项断言 → 收尾还原 → 落盘并打印结论。</summary>
    /// <param name="root">原生根控制器。</param>
    /// <param name="session">原生展示会话。</param>
    /// <param name="directory">自有夹具目录（绝对路径）。</param>
    /// <param name="snapshots">是否写截图检查点并停留。</param>
    private static async Task RunAsync(iOSNativeRootViewController root, PhotoLoadSession session, string directory,
        bool snapshots)
    {
        var lines = new List<string>();
        string output = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "PhotoViewer");
        Directory.CreateDirectory(output);
        string report = Path.Combine(output, "native-rating-control-checks.txt");
        var probe = new RatingControlProbe(root, session, directory, snapshots, lines);
        try
        {
            await probe.ExecuteAsync();
            await probe.RestoreAsync();
            lines.Add("PASS 收尾还原：方向、设置快照、栏位显隐、筛选、当前照片、弹窗、减少动态效果提供器与临时视图回到进入本检查时的状态，" +
                "工作副本已删除");
            if (snapshots) await WriteStageAsync("done");
            await File.WriteAllLinesAsync(report, lines);
            Console.WriteLine($"[NativeSmoke] PASS rating-control ({lines.Count} checks)");
        }
        catch (Exception exception)
        {
            lines.Add($"FAIL {exception}");
            if (snapshots) await WriteStageAsync("done");
            await File.WriteAllLinesAsync(report, lines);
            Console.Error.WriteLine("[NativeSmoke] FAIL rating-control " + exception);
            try
            {
                await probe.RestoreAsync();
            }
            catch (Exception restoreFailure)
            {
                Console.Error.WriteLine("[NativeSmoke] rating-control 还原失败 " + restoreFailure.Message);
            }
        }
    }

    /// <summary>写入当前截图阶段名（外部脚本据此截图）。</summary>
    /// <param name="stage">阶段名；<c>done</c> 表示验收结束。</param>
    /// <returns>写入任务。</returns>
    private static Task WriteStageAsync(string stage)
    {
        string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "PhotoViewer",
            "native-ui-snapshot-stage.txt");
        return File.WriteAllTextAsync(path, stage);
    }

    /// <summary>冲突夹具：两个同 stem 的 jpg（内嵌不同星级）加一份真实 sidecar。</summary>
    /// <param name="Photo">代表文件（当前照片）。</param>
    /// <param name="Group">同名合并组。</param>
    /// <param name="First"><c>.jpg</c> 成员路径。</param>
    /// <param name="Second">按 <c>.jpeg</c> 成员路径。</param>
    /// <param name="Sidecar"><c>.xmp</c> 来源路径。</param>
    private sealed record ConflictFixture(ImageFile Photo, PhotoGroup Group, string First, string Second, string Sidecar);

    /// <summary>一次验收运行的完整状态：单独持有，收尾可重入地还原全部临时改动。</summary>
    private sealed class RatingControlProbe
    {
        /// <summary>几何比较允许误差（点）。</summary>
        private const double Tolerance = .5;
        private readonly iOSNativeRootViewController _root;
        private readonly PhotoLoadSession _session;
        private readonly string _source;
        private readonly bool _snapshots;
        private readonly List<string> _lines;
        private readonly List<(ImageFile File, int Value)> _commits = [];
        private readonly List<UIView> _temporaryViews = [];
        private MainViewModel _main = null!;
        private AppSettings? _entrySettings;
        private UIInterfaceOrientation _entryOrientation;
        private string? _entryFile;
        private string _entryFilter = "All";
        private bool _entryFilm = true, _entryControl = true, _entryAnalysis;
        private Func<bool>? _popupReduceMotion;
        private string _workspace = "";
        private ConflictFixture? _conflict;
        private Action<ImageFile, int>? _onCommitted;
        private ImageFile _representative = null!;
        private bool _restored;

        /// <summary>建立一次验收运行。</summary>
        /// <param name="root">原生根控制器。</param>
        /// <param name="session">原生展示会话。</param>
        /// <param name="source">操作者提供的夹具目录（沙盒内，只读）。</param>
        /// <param name="snapshots">是否写截图检查点并停留。</param>
        /// <param name="lines">报告行。</param>
        internal RatingControlProbe(iOSNativeRootViewController root, PhotoLoadSession session, string source,
            bool snapshots, List<string> lines)
        {
            _root = root;
            _session = session;
            _source = source;
            _snapshots = snapshots;
            _lines = lines;
        }

        /// <summary>是否 iPhone（iPhone 走真实旋转与手机槽位，iPad 走宽形态行/列）。</summary>
        private static bool PhoneDevice => UIDevice.CurrentDevice.UserInterfaceIdiom == UIUserInterfaceIdiom.Phone;

        /// <summary>执行全部断言；异常直接上抛给入口落 FAIL。</summary>
        /// <returns>任务。</returns>
        internal async Task ExecuteAsync()
        {
            _main = iOSNativeShell.Main ?? throw new InvalidOperationException("原生壳主 VM 未就绪");
            await WaitAsync(() => _root.ViewIfLoaded?.Window != null && _root.Stage.Bounds.Width > 100
                && _root.Photo.Bounds.Width > 100, "主屏布局");
            string[] fixtures = Directory.GetFiles(_source, "*.jpg");
            if (fixtures.Length < 4)
                throw new InvalidOperationException($"星级验收需要至少 4 张自有 jpg 夹具，实际 {fixtures.Length}（{_source}）");
            _entrySettings = _main.Settings.CreateSnapshot();
            _entryOrientation = CurrentOrientation();
            _entryFile = _main.CurrentFile?.File.StorageId.Value;
            _entryFilter = _main.FileVM.FilterBar.SelectedRatingFilter;
            _entryFilm = _main.IsThumbnailViewVisible;
            _entryControl = _main.IsControlViewVisible;
            _entryAnalysis = _main.IsAnalysisViewVisible;
            _popupReduceMotion = _root.RatingPopup.ReduceMotionProvider;

            _onCommitted = (file, value) => _commits.Add((file, value));
            _root.RatingCommitted += _onCommitted;
            _workspace = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                "rating-control-fixtures-work");
            if (Directory.Exists(_workspace)) Directory.Delete(_workspace, true);
            Directory.CreateDirectory(_workspace);
            foreach (string fixture in fixtures) File.Copy(fixture, Path.Combine(_workspace, Path.GetFileName(fixture)), true);

            await UiDispatcher.InvokeAsync(() =>
            {
                _main.Settings.ShowRating = true;
                _main.Settings.SameNameAsOnePhoto = true;
                _main.Settings.LayoutMode = PhoneDevice ? LayoutMode.Auto : LayoutMode.Row;
                _main.IsThumbnailViewVisible = true;
                _main.IsControlViewVisible = true;
                _main.IsAnalysisViewVisible = false;
                _main.FileVM.FilterBar.SelectedRatingFilter = "All";
                foreach (var format in _main.Settings.FileFormats)
                    if (format.DisplayName == "JPG") format.IsEnabled = true;
                _root.BuildControls();
            });
            await _main.FolderVM.OpenFolderAsync(new LocalFolderRef(_workspace));
            await WaitAsync(() => _main.CurrentFile != null && _session.Image != null, "工作副本首图");
            await SettleAsync();
            _representative = _main.CurrentFile ?? throw new InvalidOperationException("工作副本没有当前照片");
            _lines.Add($"PASS 素材：{fixtures.Length} 张自有夹具（{_source}）→ 可写工作副本 {_workspace}，" +
                $"代表文件 {_representative.Name}，设备={(PhoneDevice ? "iPhone" : "iPad")}，" +
                $"窗口 {Size(_root.View!.Bounds.Size)}，安全区 {Insets(_root.View!.SafeAreaInsets)}");

            VerifyThresholds();
            await VerifyExpandedTapAsync();
            await VerifyExpandedSlideAsync();
            await VerifyExpandedCancelAsync();
            await VerifyCompactTapAsync();
            await VerifyCompactSlideAsync();
            await VerifyCompactCancelAsync();
            await VerifyPopupClosingAsync();
            await VerifyReduceMotionAsync();
            await VerifyConflictAsync();
            await VerifyCardTriangleAsync();
            await VerifyVerticalCompactAsync();
            await VerifyAccessibilityAsync();
            await VerifyDisabledAsync();
            if (PhoneDevice) await VerifyPhoneAsync();
            else await VerifyPadAsync();
        }

        /// <summary>收尾还原（幂等）：弹窗、临时视图、减少动态效果、设置、方向、栏位、筛选、当前照片与工作副本。</summary>
        /// <returns>任务。</returns>
        internal async Task RestoreAsync()
        {
            if (_restored || _entrySettings == null) return;
            _restored = true;
            if (_onCommitted != null)
            {
                _root.RatingCommitted -= _onCommitted;
                _onCommitted = null;
            }
            foreach (var view in _temporaryViews.ToArray())
            {
                view.RemoveFromSuperview();
                view.Dispose();
            }
            _temporaryViews.Clear();
            _root.RatingPopup.ReduceMotionProvider = _popupReduceMotion ?? (() => UIAccessibility.IsReduceMotionEnabled);
            _root.RatingPopup.CloseImmediately();
            await UiDispatcher.InvokeAsync(() =>
            {
                _main.Settings.ApplySnapshot(_entrySettings);
                _main.IsThumbnailViewVisible = _entryFilm;
                _main.IsControlViewVisible = _entryControl;
                _main.IsAnalysisViewVisible = _entryAnalysis;
                _main.FileVM.FilterBar.SelectedRatingFilter = _entryFilter;
                _root.SupportedOrientationsOverride = null;
                _root.SetNeedsUpdateOfSupportedInterfaceOrientations();
            });
            await SettleAsync();
            if (CurrentOrientation() != _entryOrientation)
            {
                var mask = _entryOrientation switch
                {
                    UIInterfaceOrientation.Portrait => UIInterfaceOrientationMask.Portrait,
                    UIInterfaceOrientation.PortraitUpsideDown => UIInterfaceOrientationMask.PortraitUpsideDown,
                    UIInterfaceOrientation.LandscapeLeft => UIInterfaceOrientationMask.LandscapeLeft,
                    UIInterfaceOrientation.LandscapeRight => UIInterfaceOrientationMask.LandscapeRight,
                    _ => UIInterfaceOrientationMask.Portrait,
                };
                try
                {
                    await RequestOrientationAsync(mask);
                }
                catch (InvalidOperationException exception)
                {
                    Console.WriteLine("[NativeSmoke] rating-control 收尾旋转未生效：" + exception.Message);
                }
            }
            // 先回到操作者的源目录再删工作副本：当前照片不能停在一个即将消失的目录里。
            if (Directory.Exists(_source))
            {
                await _main.FolderVM.OpenFolderAsync(new LocalFolderRef(_source));
                await WaitAsync(() => _main.CurrentFile != null, "回到源目录");
                if (_entryFile is { Length: > 0 })
                {
                    var target = _main.FolderVM.AllFiles.FirstOrDefault(file => file.File.StorageId.Value == _entryFile);
                    if (target != null) await _main.FolderVM.OpenImageAsync(target.File);
                }
            }
            await UiDispatcher.InvokeAsync(_root.BuildControls);
            await SettleAsync();
            if (Directory.Exists(_workspace)) Directory.Delete(_workspace, true);
        }

        /// <summary>记录一个截图检查点并停留，供外部脚本截图。</summary>
        /// <param name="stage">检查点名（与截图文件名一致）。</param>
        /// <returns>任务。</returns>
        private async Task HoldAsync(string stage)
        {
            if (!_snapshots) return;
            _lines.Add($"STAGE {stage}");
            await WriteStageAsync(stage);
            await Task.Delay(StageHold);
        }

        #region 临时控件与坐标换算

        /// <summary>控件入口的默认展开方向（与根控制器同一张共享方向表）。</summary>
        /// <returns>弹窗方向。</returns>
        private RatingPopupDirection DefaultDirection() => RatingControlGeometry.PopupDirection(
            _root.Form, _main.IsRowLayout, _root.NotchOnLeft, RatingPopupAnchor.Control);

        /// <summary>
        /// 建一个临时星级控件：接线与根控制器一致（同一个写回入口、同一个弹窗实例、同一个开弹窗回调），
        /// 挂到舞台上供几何与手势核对，收尾统一摘除。
        /// </summary>
        /// <param name="frame">舞台坐标中的槽位矩形。</param>
        /// <param name="horizontal">槽位主轴是否水平。</param>
        /// <param name="direction">弹窗展开方向。</param>
        /// <returns>已接线的控件。</returns>
        private iOSRatingControl CreateStandalone(CGRect frame, bool horizontal, Func<RatingPopupDirection> direction)
        {
            var control = new iOSRatingControl { Frame = frame, Horizontal = horizontal };
            control.CommitRequested = value =>
            {
                if (_main.CurrentFile is { } file) _ = CommitAsync(control, file, value);
            };
            control.PopupRequested = () =>
            {
                if (_main.CurrentFile is not { } file) return false;
                _root.RatingPopup.Open(file, control.Capsule, RatingPopupAnchor.Control, direction(),
                    horizontalRow: control.Horizontal);
                return true;
            };
            control.Popup = _root.RatingPopup;
            _root.Stage.AddSubview(control);
            _temporaryViews.Add(control);
            LayoutControl(control);
            return control;
        }

        /// <summary>写回后按磁盘真值刷新临时控件（与根控制器 <c>UpdateRatingControls</c> 同一口径）。</summary>
        /// <param name="control">发起提交的控件。</param>
        /// <param name="file">目标照片。</param>
        /// <param name="value">写入值。</param>
        /// <returns>任务。</returns>
        private async Task CommitAsync(iOSRatingControl control, ImageFile file, int value)
        {
            await _root.RateAsync(file, value);
            RefreshControl(control);
        }

        /// <summary>把当前照片的真值同步到临时控件（值、冲突、可用）。</summary>
        /// <param name="control">临时控件。</param>
        private void RefreshControl(iOSRatingControl control)
        {
            var file = _main.CurrentFile;
            control.SetState(Math.Max(0, file?.Rating ?? 0), file?.HasRatingConflict ?? false, file != null);
            LayoutControl(control);
        }

        /// <summary>设置槽位 frame 并强制布局（形态与胶囊都在 LayoutSubviews 里判定）。</summary>
        /// <param name="control">目标控件。</param>
        /// <param name="width">槽位宽。</param>
        /// <param name="height">槽位高。</param>
        /// <param name="horizontal">槽位主轴是否水平。</param>
        private static void ApplySlot(iOSRatingControl control, double width, double height, bool horizontal)
        {
            control.Horizontal = horizontal;
            control.Frame = new CGRect(control.Frame.X, control.Frame.Y, width, height);
            LayoutControl(control);
        }

        /// <summary>控件与其星排各强制一轮布局（星排格子只在自身 LayoutSubviews 里落位）。</summary>
        /// <param name="control">目标控件。</param>
        private static void LayoutControl(iOSRatingControl control)
        {
            control.SetNeedsLayout();
            control.LayoutIfNeeded();
            control.Row.SetNeedsLayout();
            control.Row.LayoutIfNeeded();
        }

        /// <summary>摘除临时视图并释放托管句柄。</summary>
        /// <param name="view">临时视图。</param>
        private void RemoveView(UIView view)
        {
            _temporaryViews.Remove(view);
            view.RemoveFromSuperview();
            view.Dispose();
        }

        /// <summary>舞台中部的一个槽位矩形（横向居中）。</summary>
        /// <param name="width">槽位宽。</param>
        /// <param name="height">槽位高。</param>
        /// <param name="fraction">纵向位置比例（0–1，取矩形中心）。</param>
        /// <returns>舞台坐标矩形。</returns>
        private CGRect Centered(double width, double height, double fraction)
        {
            var stage = _root.Stage.Bounds;
            return new CGRect(((double)stage.Width - width) / 2, (double)stage.Height * fraction - height / 2, width, height);
        }

        /// <summary>星排格子的中心点（星排自身坐标）。</summary>
        /// <param name="row">星排。</param>
        /// <param name="value">星级值（0–5）。</param>
        /// <returns>格子中心。</returns>
        private static CGPoint SlotCenter(iOSRatingRow row, int value)
        {
            // 星排格子只在自身 LayoutSubviews 里落位：读数前强制一轮，避免读到全零 frame。
            row.SetNeedsLayout();
            row.LayoutIfNeeded();
            var frame = row.SlotViews[value].Frame;
            return new CGPoint(frame.X + frame.Width / 2, frame.Y + frame.Height / 2);
        }

        /// <summary>控件胶囊中心（控件自身坐标）。</summary>
        /// <param name="control">星级控件。</param>
        /// <returns>中心点。</returns>
        private CGPoint CapsuleCenter(iOSRatingControl control)
        {
            var capsule = control.Capsule.Frame;
            return new CGPoint(capsule.X + capsule.Width / 2, capsule.Y + capsule.Height / 2);
        }

        /// <summary>视图在宿主（星级弹窗的父视图）坐标中的矩形。</summary>
        /// <param name="view">目标视图。</param>
        /// <returns>宿主坐标矩形。</returns>
        private CGRect FrameInHost(UIView view) => view.ConvertRectToView(view.Bounds, _root.View!);

        /// <summary>视图在舞台坐标中的矩形。</summary>
        /// <param name="view">目标视图。</param>
        /// <returns>舞台坐标矩形。</returns>
        private CGRect FrameInStage(UIView view) => view.ConvertRectToView(view.Bounds, _root.Stage);

        #endregion

        #region 写回与等待

        /// <summary>触感计数器快照。</summary>
        /// <returns>选择触感与提交触感次数。</returns>
        private static (int Selection, int Commit) Haptics()
            => (iOSRatingHaptics.SelectionCount, iOSRatingHaptics.CommitCount);

        /// <summary>关闭星级弹窗并等退场动画结束（避免下一轮重新打开时新旧面板重叠）。</summary>
        /// <returns>任务。</returns>
        private async Task EnsurePopupClosedAsync()
        {
            _root.RatingPopup.CloseImmediately();
            await Task.Delay(60);
        }

        /// <summary>按共享写回路径设置一个已知星级（不经根控制器的提交事件，不影响写回计数）。</summary>
        /// <param name="file">目标照片。</param>
        /// <param name="value">星级。</param>
        /// <returns>任务。</returns>
        private async Task SetRatingAsync(ImageFile file, int value)
        {
            await _main.SetRatingAsync(file, value);
            await WaitAsync(() => file.Rating == value, $"写回 {value} 星（{file.Name}）");
            await WaitForDiskAsync(file, value, "已知星级落盘");
        }

        /// <summary>把指定照片变成当前照片并等主图就绪。</summary>
        /// <param name="file">目标照片。</param>
        /// <returns>任务。</returns>
        private async Task OpenFileAsync(ImageFile file)
        {
            if (!ReferenceEquals(_main.CurrentFile, file)) await _main.FolderVM.OpenImageAsync(file.File);
            await WaitAsync(() => ReferenceEquals(_main.CurrentFile, file), $"当前照片切到 {file.Name}");
            await SettleAsync();
        }

        /// <summary>等待文件内存星级与磁盘真值都到指定值（评分写回是异步事务）。</summary>
        /// <param name="file">目标照片。</param>
        /// <param name="value">期望值。</param>
        /// <param name="label">失败标签。</param>
        /// <returns>任务。</returns>
        private async Task WaitForFileRatingAsync(ImageFile file, int value, string label)
        {
            await WaitAsync(() => file.Rating == value, $"{label}：内存星级 {value}（实际 {file.Rating}）", 30);
            await WaitForDiskAsync(file, value, label);
        }

        /// <summary>轮询磁盘真值（内嵌 XMP）。</summary>
        /// <param name="file">目标照片。</param>
        /// <param name="value">期望值。</param>
        /// <param name="label">失败标签。</param>
        /// <returns>任务。</returns>
        private static async Task WaitForDiskAsync(ImageFile file, int value, string label)
        {
            int last = int.MinValue;
            for (int attempt = 0; attempt < 300; attempt++)
            {
                last = await ReadDiskRatingAsync(file);
                if (last == value) return;
                await Task.Delay(40);
            }
            throw new InvalidOperationException($"{label}：磁盘真值 {last} ≠ 期望 {value}（{file.Name}）");
        }

        /// <summary>读文件的内嵌 XMP 星级（磁盘真值）。</summary>
        /// <param name="file">目标照片。</param>
        /// <returns>星级（非 Rated 按 0）。</returns>
        private static async Task<int> ReadDiskRatingAsync(ImageFile file)
        {
            string path = file.File.TryGetLocalPath()
                ?? throw new InvalidOperationException($"无法读取本地路径：{file.Name}");
            var snapshot = await XmpRatingStoreFactory.Create(new LocalFileRef(path)).ReadAsync();
            return snapshot.State == XmpRatingState.Rated ? snapshot.Value ?? 0 : 0;
        }

        /// <summary>读一个来源自身在磁盘上的星级。</summary>
        /// <param name="source">评分来源。</param>
        /// <returns>星级（非 Rated 按 0）。</returns>
        private static async Task<int> ReadSourceRatingAsync(RatingSourceState source)
        {
            var snapshot = await XmpRatingStoreFactory.Create(source.File).ReadAsync();
            return snapshot.State == XmpRatingState.Rated ? snapshot.Value ?? 0 : 0;
        }

        #endregion

        #region 冲突夹具

        /// <summary>
        /// 在可写工作副本里建立 <c>rating-conflict</c> 组：<c>.jpg</c> 内嵌 4、<c>.jpeg</c> 内嵌 2、
        /// 同名 sidecar 写 1，重开目录后作为当前照片（RATING-04 的冲突前提）。
        /// </summary>
        /// <returns>冲突夹具。</returns>
        private async Task<ConflictFixture> CreateConflictAsync()
        {
            string first = Path.Combine(_workspace, "rating-conflict.jpg");
            string second = Path.Combine(_workspace, "rating-conflict.jpeg");
            string sidecar = Path.Combine(_workspace, "rating-conflict.xmp");
            string[] seeds = Directory.GetFiles(_workspace, "*.jpg")
                .Where(path => !Path.GetFileName(path).StartsWith("rating-conflict", StringComparison.Ordinal))
                .OrderBy(path => path, StringComparer.Ordinal)
                .ToArray();
            if (seeds.Length < 2) throw new InvalidOperationException("工作副本里没有两张可用于冲突夹具的 jpg");
            File.Copy(seeds[0], first, true);
            File.Copy(seeds[1], second, true);
            await XmpRatingStoreFactory.Create(new LocalFileRef(first)).WriteAsync(4, true);
            await XmpRatingStoreFactory.Create(new LocalFileRef(second)).WriteAsync(2, true);
            await File.WriteAllTextAsync(sidecar,
                "<x:xmpmeta xmlns:x='adobe:ns:meta/'><rdf:RDF xmlns:rdf='http://www.w3.org/1999/02/22-rdf-syntax-ns#'><rdf:Description rdf:about='' xmlns:xmp='http://ns.adobe.com/xap/1.0/' xmp:Rating='1'/></rdf:RDF></x:xmpmeta>");
            await UiDispatcher.InvokeAsync(() => _main.FileVM.FilterBar.SelectedRatingFilter = "All");
            await _main.FolderVM.OpenFolderAsync(new LocalFolderRef(_workspace));
            await WaitAsync(() => _main.FolderVM.AllFiles.Any(file => file.Name == "rating-conflict.jpg"), "冲突夹具枚举");
            var member = _main.FolderVM.AllFiles.First(file => file.Name == "rating-conflict.jpg");
            var group = member.Group ?? throw new InvalidOperationException("冲突夹具未按同名合并成组");
            if (group.Files.Count != 2)
                throw new InvalidOperationException($"冲突夹具组应含 2 个文件，实际 {group.Files.Count}");
            foreach (var file in group.Files) await file.LoadExifDataAsync();
            var photo = group.Representative;
            await _main.FolderVM.OpenImageAsync(photo.File);
            await WaitAsync(() => ReferenceEquals(_main.CurrentFile, photo) && photo.HasRatingConflict, "冲突状态就绪");
            await SettleAsync();
            return new ConflictFixture(photo, group, first, second, sidecar);
        }

        /// <summary>把已存在的冲突夹具重新写成冲突态（组内取值不同）并刷新内存真值。</summary>
        /// <param name="fixture">冲突夹具。</param>
        /// <returns>任务。</returns>
        private async Task RebuildConflictAsync(ConflictFixture fixture)
        {
            await XmpRatingStoreFactory.Create(new LocalFileRef(fixture.First)).WriteAsync(4, true);
            await XmpRatingStoreFactory.Create(new LocalFileRef(fixture.Second)).WriteAsync(2, true);
            await XmpRatingStoreFactory.Create(new LocalFileRef(fixture.Sidecar)).WriteAsync(1, true);
            foreach (var member in fixture.Group.Files) await member.RefreshMetadataAsync();
            await OpenFileAsync(fixture.Photo);
            await WaitAsync(() => fixture.Photo.HasRatingConflict, "冲突状态重建");
            await SettleAsync();
        }

        /// <summary>按真实写回路径把整组写成一个值（冲突随之解除）。</summary>
        /// <param name="fixture">冲突夹具。</param>
        /// <param name="value">星级。</param>
        /// <returns>任务。</returns>
        private async Task ResolveConflictAsync(ConflictFixture fixture, int value)
        {
            await _main.SetRatingAsync(fixture.Photo, value);
            await WaitAsync(() => !fixture.Photo.HasRatingConflict, $"冲突消解为 {value} 星");
            await WaitForFileRatingAsync(fixture.Photo, value, "冲突消解落盘");
            await SettleAsync();
        }

        #endregion

        #region 断言 1–4：展开态

        /// <summary>RATING-07 形态阈值：槽位主轴达到固有长度即展开，否则紧凑；胶囊长度与居中取共享几何。</summary>
        private void VerifyThresholds()
        {
            var control = CreateStandalone(new CGRect(0, 0, 260, 44), true, DefaultDirection);
            try
            {
                var cases = new (double Main, double Cross, bool Horizontal, RatingControlForm Form)[]
                {
                    (223, 44, true, RatingControlForm.Compact),
                    (224, 44, true, RatingControlForm.Expanded),
                    (223, 44, false, RatingControlForm.Compact),
                    (224, 44, false, RatingControlForm.Expanded),
                };
                foreach (var item in cases)
                {
                    double width = item.Horizontal ? item.Main : item.Cross;
                    double height = item.Horizontal ? item.Cross : item.Main;
                    ApplySlot(control, width, height, item.Horizontal);
                    string context = $"{width:0.#}×{height:0.#}（{(item.Horizontal ? "横" : "竖")}）";
                    if (control.Form != item.Form)
                        throw new InvalidOperationException($"{context} 槽位判定为 {control.Form}，期望 {item.Form}");
                    if (control.Form != RatingControlGeometry.ResolveForm(item.Main))
                        throw new InvalidOperationException($"{context} 形态与 ResolveForm({item.Main:0.#}) 不一致");
                }

                ApplySlot(control, 260, 44, true);
                var expanded = control.CapsuleFrame;
                if (Math.Abs(expanded.Width - RatingControlGeometry.ExpandedLength) > Tolerance
                    || Math.Abs(expanded.X - (260 - RatingControlGeometry.ExpandedLength) / 2) > Tolerance
                    || Math.Abs(expanded.Height - RatingControlGeometry.Thickness) > Tolerance)
                    throw new InvalidOperationException($"260 槽位里的展开胶囊 {Rect(expanded)} 不是 224 长居中");

                ApplySlot(control, 150, 44, true);
                var compact = control.CapsuleFrame;
                if (Math.Abs(compact.X) > Tolerance || Math.Abs(compact.Width - 150) > Tolerance
                    || Math.Abs(compact.Height - RatingControlGeometry.Thickness) > Tolerance)
                    throw new InvalidOperationException($"150 槽位里的紧凑胶囊 {Rect(compact)} 未铺满槽位主轴");

                _lines.Add("PASS RATING-07 形态阈值：223×44 紧凑、224×44 展开、44×223 紧凑、44×224 展开；" +
                    "展开胶囊 224 居中于 260 槽位，紧凑胶囊铺满 150 槽位");
            }
            finally
            {
                RemoveView(control);
            }
        }

        /// <summary>RATING-07 展开态点按：一次手势一次写回，纯点按不产生选择触感。</summary>
        /// <returns>任务。</returns>
        private async Task VerifyExpandedTapAsync()
        {
            var file = _representative;
            await OpenFileAsync(file);
            await SetRatingAsync(file, 2);
            var control = CreateStandalone(Centered(260, 44, .40), true, DefaultDirection);
            try
            {
                RefreshControl(control);
                var row = control.Row;
                var baseline = Haptics();
                int before = _commits.Count;
                var point = SlotCenter(row, 4);
                row.TrackBegan(point);
                row.TrackEnded(point);
                await WaitAsync(() => _commits.Count == before + 1, "展开态点按写回一次");
                var commit = _commits[before];
                if (commit.Value != 4)
                    throw new InvalidOperationException($"展开态点按写回 {commit.Value} ≠ 4");
                if (!ReferenceEquals(commit.File, file))
                    throw new InvalidOperationException($"展开态点按写回的不是当前照片：{commit.File.Name}");
                var after = Haptics();
                if (after.Selection != baseline.Selection)
                    throw new InvalidOperationException($"纯点按产生了 {after.Selection - baseline.Selection} 次选择触感");
                if (after.Commit != baseline.Commit + 1)
                    throw new InvalidOperationException($"提交触感增量 {after.Commit - baseline.Commit} ≠ 1");
                await WaitForFileRatingAsync(file, 4, "展开态点按");
                RefreshControl(control);
                if (control.Value != 4)
                    throw new InvalidOperationException($"写回后控件显示 {control.Value} ≠ 4");
                _lines.Add($"PASS RATING-07 展开态点按：4 号格按下—抬起写回 1 次（值 4），选择触感 " +
                    $"{after.Selection - baseline.Selection} 次、提交触感 1 次，内存与磁盘真值均为 4");
            }
            finally
            {
                RemoveView(control);
            }
        }

        /// <summary>RATING-07 展开态滑动：预览逐档跟随、每跨一档一次选择触感，抬手前不写盘。</summary>
        /// <returns>任务。</returns>
        private async Task VerifyExpandedSlideAsync()
        {
            var file = _representative;
            await OpenFileAsync(file);
            await SetRatingAsync(file, 2);
            var control = CreateStandalone(Centered(260, 44, .40), true, DefaultDirection);
            try
            {
                RefreshControl(control);
                var row = control.Row;
                var baseline = Haptics();
                int before = _commits.Count;
                row.TrackBegan(SlotCenter(row, 1));
                if (row.Preview != 1)
                    throw new InvalidOperationException($"起手预览 {row.Preview} ≠ 1");
                var previews = new List<int> { 1 };
                foreach (int value in new[] { 2, 3, 4 })
                {
                    row.TrackMoved(SlotCenter(row, value));
                    if (row.Preview != value)
                        throw new InvalidOperationException($"滑动到 {value} 时预览 {row.Preview} ≠ {value}");
                    previews.Add(value);
                }
                var afterMoves = Haptics();
                if (afterMoves.Selection != baseline.Selection + 3)
                    throw new InvalidOperationException($"三次跨档的选择触感增量 {afterMoves.Selection - baseline.Selection} ≠ 3");
                if (_commits.Count != before)
                    throw new InvalidOperationException($"滑动过程中写回了 {_commits.Count - before} 次");
                int midDisk = await ReadDiskRatingAsync(file);
                if (midDisk != 2)
                    throw new InvalidOperationException($"滑动过程中磁盘被改写为 {midDisk} 星");
                row.TrackEnded(SlotCenter(row, 4));
                await WaitAsync(() => _commits.Count == before + 1, "滑动抬手写回一次");
                if (_commits[before].Value != 4)
                    throw new InvalidOperationException($"滑动抬手写回 {_commits[before].Value} ≠ 4");
                await WaitForFileRatingAsync(file, 4, "滑动抬手");
                RefreshControl(control);
                if (control.Value != 4)
                    throw new InvalidOperationException($"写回后控件显示 {control.Value} ≠ 4");
                _lines.Add($"PASS RATING-07 展开态滑动：1→2→3→4 预览序列 {string.Join(",", previews)}、选择触感 3 次、" +
                    "抬手 1 次写回（值 4）；抬手前 0 次写回且磁盘保持 2 星");
            }
            finally
            {
                RemoveView(control);
            }
        }

        /// <summary>RATING-07 取消语义：滑回原值不写、交叉轴越出取消带预览清空且不写、系统打断不写。</summary>
        /// <returns>任务。</returns>
        private async Task VerifyExpandedCancelAsync()
        {
            var file = _representative;
            await OpenFileAsync(file);
            await SetRatingAsync(file, 4);
            var control = CreateStandalone(Centered(260, 44, .40), true, DefaultDirection);
            try
            {
                RefreshControl(control);
                var row = control.Row;
                int before = _commits.Count;
                row.TrackBegan(SlotCenter(row, 3));
                row.TrackMoved(SlotCenter(row, 4));
                row.TrackEnded(SlotCenter(row, 4));
                if (_commits.Count != before)
                    throw new InvalidOperationException("滑回原值仍写回了星级");
                if (row.Preview != null)
                    throw new InvalidOperationException("滑回原值抬手后预览未清空");

                row.TrackBegan(SlotCenter(row, 4));
                var center = SlotCenter(row, 4);
                var outside = new CGPoint(center.X, row.Bounds.Height + RatingControlGeometry.CancelSlop + 2);
                row.TrackMoved(outside);
                if (row.Preview != null)
                    throw new InvalidOperationException($"越出取消带（交叉轴 {outside.Y:0.#}）后预览仍为 {row.Preview}");
                row.TrackEnded(outside);
                if (_commits.Count != before)
                    throw new InvalidOperationException("在取消带外抬手仍写回了星级");

                row.TrackBegan(SlotCenter(row, 2));
                row.TrackMoved(SlotCenter(row, 3));
                row.TrackCancelled();
                if (row.Preview != null)
                    throw new InvalidOperationException("TrackCancelled 后预览未清空");
                if (_commits.Count != before)
                    throw new InvalidOperationException("TrackCancelled 仍写回了星级");

                int disk = await ReadDiskRatingAsync(file);
                if (disk != 4)
                    throw new InvalidOperationException($"取消路径改写了磁盘（{disk} 星）");
                _lines.Add("PASS RATING-07 手势取消：滑回原值 0 次写回；交叉轴越出取消带时预览清空、抬手 0 次写回；" +
                    "TrackCancelled 0 次写回，磁盘保持 4 星");
            }
            finally
            {
                RemoveView(control);
            }
        }

        #endregion

        #region 断言 5–9：紧凑态与弹窗

        /// <summary>RATING-07 紧凑态点按：打开单条目弹窗、微小位移保持弹窗、点弹窗星排一次写回。</summary>
        /// <returns>任务。</returns>
        private async Task VerifyCompactTapAsync()
        {
            var file = _representative;
            await OpenFileAsync(file);
            await SetRatingAsync(file, 4);
            var control = CreateStandalone(Centered(150, 44, .55), true, DefaultDirection);
            try
            {
                RefreshControl(control);
                await EnsurePopupClosedAsync();
                var popup = _root.RatingPopup;
                int before = _commits.Count;
                var capsule = CapsuleCenter(control);
                control.TrackBegan(capsule);
                if (!popup.IsOpen)
                    throw new InvalidOperationException("紧凑态起手没有打开星级弹窗");
                if (popup.Mode != iOSRatingPopup.RatingPopupMode.Single)
                    throw new InvalidOperationException($"非冲突弹窗形态为 {popup.Mode}");
                if (popup.Direction != RatingPopupDirection.Up)
                    throw new InvalidOperationException($"竖屏/行布局弹窗方向为 {popup.Direction} ≠ Up");
                var capsuleHost = FrameInHost(control.Capsule);
                double gap = DesignTokens.RatingPopupGap;
                if (popup.PanelFrame.MaxY > (double)capsuleHost.Y - gap + Tolerance)
                    throw new InvalidOperationException(
                        $"弹窗底边 {popup.PanelFrame.MaxY:0.#} 未停在胶囊顶 {capsuleHost.Y:0.#} 上方 {gap:0.#} 点");
                RequirePanelInsideSafeArea("紧凑态弹窗");

                var nudged = new CGPoint(capsule.X + RatingControlGeometry.TapSlop / 2, capsule.Y);
                control.TrackMoved(nudged);
                if (!popup.IsOpen)
                    throw new InvalidOperationException("小于点按阈值的移动关闭了弹窗");
                if (popup.TrackingRow is not { } tracking)
                    throw new InvalidOperationException("单条目弹窗没有可续接的星排");
                if (tracking.Preview != null)
                    throw new InvalidOperationException($"小于点按阈值的移动出现了预览 {tracking.Preview}");
                control.TrackEnded(nudged);
                if (!popup.IsOpen)
                    throw new InvalidOperationException("点按抬手关闭了弹窗");
                if (_commits.Count != before)
                    throw new InvalidOperationException("点按抬手写回了星级");

                var point = SlotCenter(tracking, 2);
                tracking.TrackBegan(point);
                tracking.TrackEnded(point);
                await WaitAsync(() => _commits.Count == before + 1, "弹窗星排点选写回一次");
                if (_commits[before].Value != 2)
                    throw new InvalidOperationException($"弹窗星排点选写回 {_commits[before].Value} ≠ 2");
                await WaitForFileRatingAsync(file, 2, "紧凑态弹窗写回");
                await WaitAsync(() => !popup.IsOpen, "写回成功后弹窗收起");
                RefreshControl(control);
                if (control.Value != 2)
                    throw new InvalidOperationException($"写回后控件显示 {control.Value} ≠ 2");
                _lines.Add($"PASS RATING-07 紧凑态点按：起手打开单条目弹窗（方向 Up，底边在胶囊顶上方 ≥{gap:0.#} 点、面板在安全区内），" +
                    $"小于点按阈值的移动不预览、抬手 0 次写回并保持弹窗；点弹窗 2 号星写回 1 次（值 2）后弹窗收起、控件显示 2");
            }
            finally
            {
                RemoveView(control);
                await EnsurePopupClosedAsync();
            }
        }

        /// <summary>RATING-07 紧凑态滑动：手指不必移进弹窗，抬手按弹窗主轴投影取值并写回。</summary>
        /// <returns>任务。</returns>
        private async Task VerifyCompactSlideAsync()
        {
            var file = _representative;
            await OpenFileAsync(file);
            await SetRatingAsync(file, 2);
            var control = CreateStandalone(Centered(150, 44, .55), true, DefaultDirection);
            try
            {
                RefreshControl(control);
                await EnsurePopupClosedAsync();
                var popup = _root.RatingPopup;
                int before = _commits.Count;
                var capsule = CapsuleCenter(control);
                control.TrackBegan(capsule);
                if (popup.TrackingRow is not { } tracking)
                    throw new InvalidOperationException("紧凑态滑动没有可续接的星排");
                var slot5 = tracking.ConvertPointToView(SlotCenter(tracking, 5), control);
                var target = new CGPoint((double)slot5.X, capsule.Y);
                control.TrackMoved(target);
                if (tracking.Preview != 5)
                    throw new InvalidOperationException($"弹窗主轴投影预览 {tracking.Preview} ≠ 5");
                control.TrackEnded(target);
                await WaitAsync(() => _commits.Count == before + 1, "紧凑态滑动写回一次");
                if (_commits[before].Value != 5)
                    throw new InvalidOperationException($"紧凑态滑动写回 {_commits[before].Value} ≠ 5");
                await WaitForFileRatingAsync(file, 5, "紧凑态滑动");
                await WaitAsync(() => !popup.IsOpen, "滑动写回成功后弹窗收起");
                RefreshControl(control);
                if (control.Value != 5)
                    throw new InvalidOperationException($"写回后控件显示 {control.Value} ≠ 5");
                _lines.Add("PASS RATING-07 紧凑态滑动：在胶囊高度上横移到弹窗 5 号格的横坐标即预览 5，" +
                    "抬手 1 次写回（值 5）并收起弹窗、控件显示 5");
            }
            finally
            {
                RemoveView(control);
                await EnsurePopupClosedAsync();
            }
        }

        /// <summary>RATING-07 紧凑态取消：交叉轴越出取消带即清空预览、抬手不写并收起弹窗。</summary>
        /// <returns>任务。</returns>
        private async Task VerifyCompactCancelAsync()
        {
            var file = _representative;
            await OpenFileAsync(file);
            await SetRatingAsync(file, 5);
            var control = CreateStandalone(Centered(150, 44, .55), true, DefaultDirection);
            try
            {
                RefreshControl(control);
                await EnsurePopupClosedAsync();
                var popup = _root.RatingPopup;
                int before = _commits.Count;
                var capsule = CapsuleCenter(control);
                control.TrackBegan(capsule);
                if (popup.TrackingRow is not { } tracking)
                    throw new InvalidOperationException("紧凑态取消没有可续接的星排");
                var below = new CGPoint(capsule.X,
                    (double)control.Capsule.Frame.GetMaxY() + RatingControlGeometry.CancelSlop + 2);
                control.TrackMoved(below);
                if (tracking.Preview != null)
                    throw new InvalidOperationException($"越出取消带后预览仍为 {tracking.Preview}");
                control.TrackEnded(below);
                if (_commits.Count != before)
                    throw new InvalidOperationException("取消带外抬手仍写回了星级");
                await WaitAsync(() => !popup.IsOpen, "取消后弹窗收起");
                int disk = await ReadDiskRatingAsync(file);
                if (disk != 5)
                    throw new InvalidOperationException($"取消路径改写了磁盘（{disk} 星）");
                _lines.Add("PASS RATING-07 紧凑态取消：沿交叉轴越出胶囊 + 取消阈值后预览为空、抬手 0 次写回并收起弹窗，磁盘保持 5 星");
            }
            finally
            {
                RemoveView(control);
                await EnsurePopupClosedAsync();
            }
        }

        /// <summary>RATING-07 弹窗收起路径：外部拦截层与 Close、Esc、切图、入口移动复检。</summary>
        /// <returns>任务。</returns>
        private async Task VerifyPopupClosingAsync()
        {
            var file = _representative;
            await OpenFileAsync(file);
            await SetRatingAsync(file, 3);
            var control = CreateStandalone(Centered(150, 44, .55), true, DefaultDirection);
            try
            {
                RefreshControl(control);
                var popup = _root.RatingPopup;
                var host = _root.View!;
                var capsule = CapsuleCenter(control);

                await EnsurePopupClosedAsync();
                control.TrackBegan(capsule);
                control.TrackEnded(capsule);
                if (!popup.IsOpen) throw new InvalidOperationException("外部点按检查：弹窗未打开");
                var catcher = popup.Catcher ?? throw new InvalidOperationException("弹窗没有全窗拦截层");
                if (!SameRect(catcher.Frame, host.Bounds))
                    throw new InvalidOperationException($"拦截层 {Rect(catcher.Frame)} 未覆盖宿主 {Rect(host.Bounds)}");
                var subviews = host.Subviews;
                int catcherIndex = Array.IndexOf(subviews, catcher);
                int panelIndex = popup.Panel is { } panel ? Array.IndexOf(subviews, panel) : -2;
                if (catcherIndex < 0 || panelIndex != catcherIndex + 1)
                    throw new InvalidOperationException($"拦截层未紧贴面板下方（拦截层 {catcherIndex}，面板 {panelIndex}）");
                popup.Close();
                await WaitAsync(() => !popup.IsOpen, "Close() 收起弹窗");

                control.TrackBegan(capsule);
                if (!popup.IsOpen) throw new InvalidOperationException("Esc 检查：弹窗未打开");
                if (!_root.HandleEscapeKey())
                    throw new InvalidOperationException("Esc 入口未处理已打开的弹窗");
                await WaitAsync(() => !popup.IsOpen, "Esc 收起弹窗");

                control.TrackBegan(capsule);
                if (!popup.IsOpen) throw new InvalidOperationException("切图检查：弹窗未打开");
                _main.FileVM.ThumbnailList.MoveCurrent(1);
                await WaitAsync(() => !popup.IsOpen, "切图收起弹窗");
                var moved = _main.CurrentFile;
                if (moved != null && ReferenceEquals(moved, file))
                    throw new InvalidOperationException("MoveCurrent(1) 没有切换当前照片");
                _main.FileVM.ThumbnailList.MoveCurrent(-1);
                await WaitAsync(() => ReferenceEquals(_main.CurrentFile, file), "切回原照片");
                await SettleAsync();

                control.TrackBegan(capsule);
                if (!popup.IsOpen) throw new InvalidOperationException("入口移动检查：弹窗未打开");
                var movedFrame = new CGRect(control.Frame.X + 18, control.Frame.Y, control.Frame.Width, control.Frame.Height);
                control.Frame = movedFrame;
                LayoutControl(control);
                popup.RevalidateAnchor();
                await WaitAsync(() => !popup.IsOpen, "入口移动后收起弹窗");
                control.Frame = new CGRect(movedFrame.X - 18, movedFrame.Y, movedFrame.Width, movedFrame.Height);
                LayoutControl(control);
                _lines.Add("PASS RATING-07 弹窗收起：全窗拦截层覆盖宿主且紧贴面板下方、Close() 收起；Esc 入口收起；" +
                    "切图收起；入口 frame 移动后 RevalidateAnchor 收起");
            }
            finally
            {
                RemoveView(control);
                await EnsurePopupClosedAsync();
            }
        }

        /// <summary>RATING-07 减少动态效果：打开弹窗不做缩放位移入场，面板 transform 保持单位矩阵。</summary>
        /// <returns>任务。</returns>
        private async Task VerifyReduceMotionAsync()
        {
            var control = CreateStandalone(Centered(150, 44, .55), true, DefaultDirection);
            try
            {
                RefreshControl(control);
                await EnsurePopupClosedAsync();
                var popup = _root.RatingPopup;
                popup.ReduceMotionProvider = () => true;
                var capsule = CapsuleCenter(control);
                control.TrackBegan(capsule);
                var panel = popup.Panel ?? throw new InvalidOperationException("减少动态效果检查：弹窗未打开");
                if (!IsIdentity(panel.Transform))
                    throw new InvalidOperationException($"减少动态效果时面板仍有入场变换 {panel.Transform}");
                control.TrackCancelled();
                await EnsurePopupClosedAsync();
                _lines.Add("PASS RATING-07 减少动态效果：提供器返回 true 时打开弹窗不做缩放/位移入场（面板 transform 为单位矩阵）");
            }
            finally
            {
                _root.RatingPopup.ReduceMotionProvider =
                    _popupReduceMotion ?? (() => UIAccessibility.IsReduceMotionEnabled);
                RemoveView(control);
                await EnsurePopupClosedAsync();
            }
        }

        #endregion

        #region 断言 10–14：冲突、竖排弹窗、读屏与禁用

        /// <summary>RATING-04 冲突：三角只开来源弹窗不写值，任一行点值整组写回并消解冲突。</summary>
        /// <returns>任务。</returns>
        private async Task VerifyConflictAsync()
        {
            var fixture = await CreateConflictAsync();
            _conflict = fixture;
            var control = CreateStandalone(Centered(260, 44, .40), true, DefaultDirection);
            try
            {
                RefreshControl(control);
                var row = control.Row;
                if (!control.Conflict)
                    throw new InvalidOperationException("冲突组作为当前照片时控件未标记 Conflict");
                if (row.ClearSymbol != "exclamationmark.triangle.fill")
                    throw new InvalidOperationException($"冲突时 0 号槽位字形为 {row.ClearSymbol}");
                RequireColor(row.SlotViews[0].TintColor, DesignTokens.ColorStarConflict, "冲突三角颜色");

                int commits = _commits.Count;
                var point = SlotCenter(row, 0);
                row.TrackBegan(point);
                row.TrackEnded(point);
                if (_commits.Count != commits)
                    throw new InvalidOperationException("落在冲突三角上不应写回星级");
                var popup = _root.RatingPopup;
                if (!popup.IsOpen) throw new InvalidOperationException("冲突三角没有打开来源弹窗");
                if (popup.Mode != iOSRatingPopup.RatingPopupMode.Sources)
                    throw new InvalidOperationException($"冲突弹窗形态为 {popup.Mode} ≠ Sources");
                int sourceCount = fixture.Group.RatingSources.Count;
                if (popup.Rows.Count != sourceCount)
                    throw new InvalidOperationException($"来源弹窗条目 {popup.Rows.Count} ≠ 组来源 {sourceCount}");
                if (popup.DividerViews.Count != sourceCount - 1)
                    throw new InvalidOperationException($"来源弹窗分隔线 {popup.DividerViews.Count} ≠ 来源数 − 1（{sourceCount - 1}）");

                var entry = popup.Rows[0];
                entry.SetNeedsLayout();
                entry.LayoutIfNeeded();
                var star = SlotCenter(entry, 3);
                entry.TrackBegan(star);
                entry.TrackEnded(star);
                await WaitAsync(() => _commits.Count == commits + 1, "来源星排写回一次");
                if (_commits[commits].Value != 3)
                    throw new InvalidOperationException($"来源星排写回 {_commits[commits].Value} ≠ 3");
                await WaitForFileRatingAsync(fixture.Photo, 3, "整组写回");
                await WaitAsync(() => !popup.IsOpen, "冲突消解后弹窗收起");
                if (fixture.Photo.HasRatingConflict)
                    throw new InvalidOperationException("整组写回同一值后仍有评分冲突");
                foreach (var source in fixture.Group.RatingSources)
                {
                    int disk = await ReadSourceRatingAsync(source);
                    if (disk != 3)
                        throw new InvalidOperationException($"来源 {source.DisplayName} 磁盘真值 {disk} ≠ 3");
                }
                RefreshControl(control);
                if (control.Conflict)
                    throw new InvalidOperationException("冲突消解后控件仍标记 Conflict");
                if (control.Row.ClearSymbol != "xmark")
                    throw new InvalidOperationException($"冲突消解后 0 号槽位仍为 {control.Row.ClearSymbol}");
                _lines.Add($"PASS RATING-04 冲突来源弹窗：0 号槽位为 exclamationmark.triangle.fill（color.star.conflict），" +
                    $"落三角 0 次写回即打开来源弹窗（{sourceCount} 条、分隔线 {sourceCount - 1} 条）；" +
                    "点任一行 3 号星整组写回 3 星，全部来源磁盘真值 3、冲突解除、弹窗收起、0 号回到 xmark");
            }
            finally
            {
                RemoveView(control);
                await EnsurePopupClosedAsync();
            }

            await RebuildConflictAsync(fixture);
            var compact = CreateStandalone(Centered(150, 44, .55), true, DefaultDirection);
            try
            {
                RefreshControl(compact);
                if (!compact.Conflict)
                    throw new InvalidOperationException("重建冲突后紧凑控件未标记 Conflict");
                await EnsurePopupClosedAsync();
                var popup = _root.RatingPopup;
                int before = _commits.Count;
                var capsule = CapsuleCenter(compact);
                compact.TrackBegan(capsule);
                if (!popup.IsOpen) throw new InvalidOperationException("紧凑冲突点按没有打开来源弹窗");
                if (popup.Mode != iOSRatingPopup.RatingPopupMode.Sources)
                    throw new InvalidOperationException($"紧凑冲突弹窗形态为 {popup.Mode} ≠ Sources");
                var moved = new CGPoint(capsule.X + 60, capsule.Y);
                compact.TrackMoved(moved);
                foreach (var row in popup.Rows)
                {
                    if (row.Preview != null)
                        throw new InvalidOperationException($"来源弹窗不应产生预览，实际出现 {row.Preview}");
                }
                if (_commits.Count != before)
                    throw new InvalidOperationException("紧凑冲突滑动写回了星级");
                compact.TrackEnded(moved);
                if (!popup.IsOpen)
                    throw new InvalidOperationException("紧凑冲突抬手不应关闭来源弹窗");
                _lines.Add("PASS RATING-04 紧凑冲突：胶囊点按打开 Sources 弹窗，沿主轴移动不产生任何行的预览、0 次写回，抬手保持弹窗");
            }
            finally
            {
                RemoveView(compact);
                await EnsurePopupClosedAsync();
            }
        }

        /// <summary>RATING-04 卡片冲突三角：按卡片槽位打开来源弹窗并锚定整卡，滚动胶片条收起。</summary>
        /// <returns>任务。</returns>
        private async Task VerifyCardTriangleAsync()
        {
            var popup = _root.RatingPopup;
            await EnsurePopupClosedAsync();
            var file = _main.CurrentFile ?? throw new InvalidOperationException("卡片冲突三角检查没有当前照片");
            if (!file.HasRatingConflict)
                throw new InvalidOperationException("卡片冲突三角检查需要当前照片仍处于冲突态");
            if (!_main.IsThumbnailViewVisible)
                await UiDispatcher.InvokeAsync(() => _main.IsThumbnailViewVisible = true);
            var files = _main.FileVM.ThumbnailList.FilteredFiles;
            int index = files.IndexOf(file);
            if (index < 0) throw new InvalidOperationException($"当前照片 {file.Name} 不在胶片栏列表里");
            var card = await WaitForCardAsync(index, "卡片冲突三角");
            if (card.ClearRatingSymbol != "exclamationmark.triangle.fill")
                throw new InvalidOperationException($"卡片 0 号槽位未换成警示三角：{card.ClearRatingSymbol}");
            await UiDispatcher.InvokeAsync(() => card.StarButtons[0].SendActionForControlEvents(UIControlEvent.TouchUpInside));
            await WaitAsync(() => popup.IsOpen, "卡片三角打开弹窗");
            if (popup.AnchorKind != RatingPopupAnchor.Card)
                throw new InvalidOperationException($"卡片三角入口类型为 {popup.AnchorKind} ≠ Card");
            if (!ReferenceEquals(popup.AnchorView, card))
                throw new InvalidOperationException("卡片三角弹窗没有锚定整张卡片");
            if (popup.Mode != iOSRatingPopup.RatingPopupMode.Sources)
                throw new InvalidOperationException($"卡片冲突弹窗形态为 {popup.Mode} ≠ Sources");
            var expected = RatingControlGeometry.PopupDirection(_root.Form, _main.IsRowLayout, _root.NotchOnLeft,
                RatingPopupAnchor.Card);
            if (popup.Direction != expected)
                throw new InvalidOperationException($"卡片入口方向 {popup.Direction} ≠ 期望 {expected}");
            var cardHost = FrameInHost(card);
            RequirePanelOnSide(new LayoutRect(cardHost.X, cardHost.Y, cardHost.Width, cardHost.Height), expected, "卡片冲突三角");

            var collection = _root.Filmstrip.Collection;
            collection.LayoutIfNeeded();
            var offset = collection.ContentOffset;
            var document = collection.ContentSize;
            var viewport = collection.Bounds.Size;
            double maxX = Math.Max(0, (double)document.Width - (double)viewport.Width);
            double maxY = Math.Max(0, (double)document.Height - (double)viewport.Height);
            double dx = Math.Min(24, maxX - (double)offset.X);
            double dy = Math.Min(24, maxY - (double)offset.Y);
            if (Math.Abs(dx) < .01 && Math.Abs(dy) < .01) dx = -Math.Min(24, (double)offset.X);
            if (Math.Abs(dx) < .01 && Math.Abs(dy) < .01) dy = -Math.Min(24, (double)offset.Y);
            if (Math.Abs(dx) < .01 && Math.Abs(dy) < .01)
                throw new InvalidOperationException(
                    $"胶片条没有可滚动余量（文档 {Size(document)} 视口 {Size(viewport)} 偏移 {Rect(offset)} " +
                    $"条数 {_root.Filmstrip.DisplayCount} 可见 {collection.IndexPathsForVisibleItems.Length} " +
                    $"隐藏 {_root.Filmstrip.Hidden} 版面 {Size(_root.Filmstrip.DocumentSize)}），无法用滚动核对卡片弹窗收起");
            collection.SetContentOffset(new CGPoint((double)offset.X + dx, (double)offset.Y + dy), false);
            await WaitAsync(() => !popup.IsOpen, "滚动胶片条收起卡片弹窗");
            collection.SetContentOffset(offset, false);
            _lines.Add($"PASS RATING-04 卡片冲突三角：卡片槽位 0 打开 Sources 弹窗并锚定整卡（方向 {expected}，" +
                "面板落在该侧，放不下时贴住放置边界），滚动胶片条后自动收起");
        }

        /// <summary>RATING-07 竖排紧凑弹窗：列宽、竖排星列次序、左侧展开与竖排滑动写回。</summary>
        /// <returns>任务。</returns>
        private async Task VerifyVerticalCompactAsync()
        {
            var file = _representative;
            await OpenFileAsync(file);
            await SetRatingAsync(file, 3);
            var stage = _root.Stage.Bounds;
            var frame = new CGRect((double)stage.Width - 44 - 16, ((double)stage.Height - 150) / 2, 44, 150);
            var control = CreateStandalone(frame, false, () => RatingPopupDirection.Left);
            try
            {
                RefreshControl(control);
                if (control.Form != RatingControlForm.Compact)
                    throw new InvalidOperationException($"44×150 竖排槽位判定为 {control.Form} ≠ Compact");
                await EnsurePopupClosedAsync();
                var popup = _root.RatingPopup;
                int before = _commits.Count;
                var capsule = CapsuleCenter(control);
                control.TrackBegan(capsule);
                if (!popup.IsOpen) throw new InvalidOperationException("竖排紧凑控件没有打开弹窗");
                if (popup.Direction != RatingPopupDirection.Left)
                    throw new InvalidOperationException($"竖排弹窗方向为 {popup.Direction} ≠ Left");
                if (popup.TrackingRow is not { } tracking)
                    throw new InvalidOperationException("竖排单条目弹窗没有可续接的星排");
                tracking.SetNeedsLayout();
                tracking.LayoutIfNeeded();
                if (tracking.Horizontal)
                    throw new InvalidOperationException("竖排控件的弹窗星排是横排");
                if (tracking.SlotViews[5].Frame.Y >= tracking.SlotViews[0].Frame.Y)
                    throw new InvalidOperationException("竖排星列 5 号未排在 0 号上方");
                if (Math.Abs(popup.PanelFrame.Width - RatingPopupLayout.ColumnWidth) > Tolerance)
                    throw new InvalidOperationException(
                        $"竖排弹窗宽 {popup.PanelFrame.Width:0.#} ≠ 列宽 {RatingPopupLayout.ColumnWidth:0.#}");
                var capsuleHost = FrameInHost(control.Capsule);
                if (popup.PanelFrame.MaxX > (double)capsuleHost.X - DesignTokens.RatingPopupGap + Tolerance)
                    throw new InvalidOperationException(
                        $"弹窗右缘 {popup.PanelFrame.MaxX:0.#} 未停在胶囊左缘 {capsuleHost.X:0.#} 左侧 {DesignTokens.RatingPopupGap:0.#} 点");
                RequirePanelInsideSafeArea("竖排紧凑弹窗");
                var slot1 = tracking.ConvertPointToView(SlotCenter(tracking, 1), control);
                var target = new CGPoint(capsule.X, (double)slot1.Y);
                control.TrackMoved(target);
                if (tracking.Preview != 1)
                    throw new InvalidOperationException($"竖排滑动预览 {tracking.Preview} ≠ 1");
                control.TrackEnded(target);
                await WaitAsync(() => _commits.Count == before + 1, "竖排滑动写回一次");
                if (_commits[before].Value != 1)
                    throw new InvalidOperationException($"竖排滑动写回 {_commits[before].Value} ≠ 1");
                await WaitForFileRatingAsync(file, 1, "竖排滑动");
                await WaitAsync(() => !popup.IsOpen, "竖排滑动写回后弹窗收起");
                _lines.Add($"PASS RATING-07 竖排紧凑弹窗：44×150 竖排控件为紧凑态、弹窗星排竖排（5 号在 0 号上方）、" +
                    $"面板宽 = 列宽 {RatingPopupLayout.ColumnWidth:0.#}、右缘停在胶囊左侧；竖排滑到 1 号预览 1、抬手 1 次写回（值 1）");
            }
            finally
            {
                RemoveView(control);
                await EnsurePopupClosedAsync();
            }
        }

        /// <summary>RATING-07 读屏：可调节语义，增减各写回一次，读屏值与星级一致。</summary>
        /// <returns>任务。</returns>
        private async Task VerifyAccessibilityAsync()
        {
            var file = _representative;
            await OpenFileAsync(file);
            await SetRatingAsync(file, 2);
            var control = CreateStandalone(Centered(260, 44, .40), true, DefaultDirection);
            try
            {
                RefreshControl(control);
                if ((control.AccessibilityTraits & UIAccessibilityTrait.Adjustable) == 0)
                    throw new InvalidOperationException($"控件 traits 缺少 Adjustable：{control.AccessibilityTraits}");
                if (control.AccessibilityValue != "2 星")
                    throw new InvalidOperationException($"初始读屏值 {control.AccessibilityValue} ≠ 「2 星」");
                int before = _commits.Count;
                control.AccessibilityIncrement();
                await WaitAsync(() => _commits.Count == before + 1, "读屏增量写回一次");
                if (_commits[before].Value != 3)
                    throw new InvalidOperationException($"读屏增量写回 {_commits[before].Value} ≠ 3");
                await WaitForFileRatingAsync(file, 3, "读屏增量");
                RefreshControl(control);
                if (control.AccessibilityValue != "3 星")
                    throw new InvalidOperationException($"写回后读屏值 {control.AccessibilityValue} ≠ 「3 星」");
                if (control.Row.AccessibilityValue != "3 星")
                    throw new InvalidOperationException($"星排读屏值 {control.Row.AccessibilityValue} ≠ 「3 星」");
                _lines.Add("PASS RATING-07 读屏：控件 traits 含 Adjustable，从 2 星增量写回 1 次（值 3），控件与星排读屏值均为「3 星」");
            }
            finally
            {
                RemoveView(control);
            }
        }

        /// <summary>RATING-07 禁用态：几何与命中区不变，手势不产生写回。</summary>
        /// <returns>任务。</returns>
        private async Task VerifyDisabledAsync()
        {
            var file = _representative;
            await OpenFileAsync(file);
            await SetRatingAsync(file, 2);
            var control = CreateStandalone(Centered(260, 44, .40), true, DefaultDirection);
            try
            {
                control.SetState(2, false, enabled: false);
                LayoutControl(control);
                var frame = control.Frame;
                var capsule = control.Capsule.Frame;
                var slots = control.Row.SlotViews.Select(view => view.Frame).ToArray();
                int before = _commits.Count;
                var point = SlotCenter(control.Row, 4);
                control.Row.TrackBegan(point);
                control.Row.TrackEnded(point);
                if (_commits.Count != before)
                    throw new InvalidOperationException("禁用态按下—抬起仍写回了星级");
                if (control.Frame != frame)
                    throw new InvalidOperationException($"禁用态控件 frame 变化：{Rect(control.Frame)} vs {Rect(frame)}");
                if (!SameRect(control.Capsule.Frame, capsule))
                    throw new InvalidOperationException($"禁用态胶囊 frame 变化：{Rect(control.Capsule.Frame)} vs {Rect(capsule)}");
                for (int value = 0; value < slots.Length; value++)
                {
                    if (!SameRect(control.Row.SlotViews[value].Frame, slots[value]))
                        throw new InvalidOperationException(
                            $"禁用态 {value} 号格 frame 变化：{Rect(control.Row.SlotViews[value].Frame)} vs {Rect(slots[value])}");
                }
                _lines.Add("PASS RATING-07 禁用态：SetState(enabled:false) 后按下—抬起 0 次写回，控件/胶囊/六格 frame 全部不变");
            }
            finally
            {
                RefreshControl(control);
                RemoveView(control);
            }
        }

        #endregion

        #region iPhone：手机槽位与真实旋转

        /// <summary>iPhone：竖屏紧凑/展开两态在真实底行控件上跑 5–7 / 2–4，横屏两种刘海方向核对控制列与弹窗方向。</summary>
        /// <returns>任务。</returns>
        private async Task VerifyPhoneAsync()
        {
            await UiDispatcher.InvokeAsync(() => _main.Settings.LayoutMode = LayoutMode.Auto);
            await EnsurePortraitAsync();
            await SettleAsync();
            var phone = _root.PhoneRatingControl;
            var file = _representative;
            await OpenFileAsync(file);
            var popup = _root.RatingPopup;

            // 竖屏 · 上下张开启 → 槽位放不下 → 紧凑
            await UiDispatcher.InvokeAsync(() => _main.Settings.ShowNavigationButtons = true);
            await SettleAsync();
            var slot = _root.PhoneSlots.Rating;
            if (slot.Width >= RatingControlGeometry.ExpandedLength)
                throw new InvalidOperationException($"竖屏开启上下张后星级槽位 {slot.Width:0.#} 仍放得下展开长度 224");
            if (phone.Form != RatingControlGeometry.ResolveForm(slot.Width))
                throw new InvalidOperationException(
                    $"竖屏紧凑槽位 {slot.Width:0.#} 的形态 {phone.Form} 与 ResolveForm 不一致");
            if (phone.Form != RatingControlForm.Compact)
                throw new InvalidOperationException($"竖屏开启上下张时控件形态为 {phone.Form} ≠ Compact");
            if (Math.Abs((double)phone.Bounds.Width - slot.Width) > Tolerance)
                throw new InvalidOperationException($"控件 bounds 宽 {phone.Bounds.Width:0.#} ≠ 槽位 {slot.Width:0.#}");
            await HoldAsync("rating-portrait-compact");

            await SetRatingAsync(file, 3);
            await EnsurePopupClosedAsync();
            int before = _commits.Count;
            var capsule = CapsuleCenter(phone);
            phone.TrackBegan(capsule);
            if (!popup.IsOpen) throw new InvalidOperationException("手机竖屏紧凑控件没有打开弹窗");
            if (popup.Mode != iOSRatingPopup.RatingPopupMode.Single)
                throw new InvalidOperationException($"手机竖屏紧凑弹窗形态为 {popup.Mode} ≠ Single");
            if (popup.Direction != RatingPopupDirection.Up)
                throw new InvalidOperationException($"手机竖屏弹窗方向为 {popup.Direction} ≠ Up");
            var capsuleHost = FrameInHost(phone.Capsule);
            if (popup.PanelFrame.MaxY > (double)capsuleHost.Y - DesignTokens.RatingPopupGap + Tolerance)
                throw new InvalidOperationException(
                    $"手机竖屏弹窗底边 {popup.PanelFrame.MaxY:0.#} 未停在胶囊顶 {capsuleHost.Y:0.#} 上方");
            RequirePanelInsideSafeArea("手机竖屏紧凑弹窗");
            await HoldAsync("rating-portrait-popup-single");
            phone.TrackEnded(capsule);
            if (!popup.IsOpen) throw new InvalidOperationException("手机竖屏紧凑点按抬手关闭了弹窗");
            if (_commits.Count != before) throw new InvalidOperationException("手机竖屏紧凑点按写回了星级");

            if (popup.TrackingRow is not { } tapRow) throw new InvalidOperationException("手机竖屏弹窗没有可续接的星排");
            tapRow.SetNeedsLayout();
            tapRow.LayoutIfNeeded();
            var tapPoint = SlotCenter(tapRow, 2);
            tapRow.TrackBegan(tapPoint);
            tapRow.TrackEnded(tapPoint);
            await WaitAsync(() => _commits.Count == before + 1, "手机竖屏弹窗点选写回一次");
            if (_commits[before].Value != 2)
                throw new InvalidOperationException($"手机竖屏弹窗点选写回 {_commits[before].Value} ≠ 2");
            await WaitForFileRatingAsync(file, 2, "手机竖屏弹窗写回");
            await WaitAsync(() => !popup.IsOpen && phone.Value == 2, "弹窗收起且控件显示 2");

            int slide = _commits.Count;
            phone.TrackBegan(capsule);
            if (popup.TrackingRow is not { } slideRow) throw new InvalidOperationException("手机竖屏滑动没有可续接的星排");
            slideRow.SetNeedsLayout();
            slideRow.LayoutIfNeeded();
            var slot5Point = slideRow.ConvertPointToView(SlotCenter(slideRow, 5), phone);
            var slideTarget = new CGPoint((double)slot5Point.X, capsule.Y);
            phone.TrackMoved(slideTarget);
            if (slideRow.Preview != 5)
                throw new InvalidOperationException($"手机竖屏紧凑滑动预览 {slideRow.Preview} ≠ 5");
            phone.TrackEnded(slideTarget);
            await WaitAsync(() => _commits.Count == slide + 1, "手机竖屏滑动写回一次");
            if (_commits[slide].Value != 5)
                throw new InvalidOperationException($"手机竖屏滑动写回 {_commits[slide].Value} ≠ 5");
            await WaitForFileRatingAsync(file, 5, "手机竖屏滑动");
            await WaitAsync(() => !popup.IsOpen && phone.Value == 5, "滑动后弹窗收起且控件显示 5");

            await EnsurePopupClosedAsync();
            int cancel = _commits.Count;
            phone.TrackBegan(capsule);
            if (popup.TrackingRow is not { } cancelRow) throw new InvalidOperationException("手机竖屏取消没有可续接的星排");
            var below = new CGPoint(capsule.X,
                (double)phone.Capsule.Frame.GetMaxY() + RatingControlGeometry.CancelSlop + 2);
            phone.TrackMoved(below);
            if (cancelRow.Preview != null)
                throw new InvalidOperationException($"手机竖屏紧凑取消后预览仍为 {cancelRow.Preview}");
            phone.TrackEnded(below);
            if (_commits.Count != cancel) throw new InvalidOperationException("手机竖屏取消带外抬手写回了星级");
            await WaitAsync(() => !popup.IsOpen, "手机竖屏紧凑取消后弹窗收起");
            _lines.Add($"PASS RATING-07 手机竖屏紧凑：上下张开启时槽位 {slot.Width:0.#} → 紧凑态；起手打开单条目弹窗（Up、" +
                "面板在胶囊上方且安全区内），点弹窗 2 号星写回 2、滑动到 5 号写回 5、越出取消带 0 次写回并收起弹窗");

            // 竖屏 · 上下张关闭 → 槽位放得下 → 展开
            await UiDispatcher.InvokeAsync(() => _main.Settings.ShowNavigationButtons = false);
            await SettleAsync();
            slot = _root.PhoneSlots.Rating;
            if (slot.Width < RatingControlGeometry.ExpandedLength)
                throw new InvalidOperationException($"竖屏关闭上下张后星级槽位 {slot.Width:0.#} 仍放不下展开长度 224");
            if (phone.Form != RatingControlGeometry.ResolveForm(slot.Width))
                throw new InvalidOperationException(
                    $"竖屏展开槽位 {slot.Width:0.#} 的形态 {phone.Form} 与 ResolveForm 不一致");
            if (phone.Form != RatingControlForm.Expanded)
                throw new InvalidOperationException($"竖屏关闭上下张时控件形态为 {phone.Form} ≠ Expanded");
            var capsuleFrame = phone.CapsuleFrame;
            if (Math.Abs(capsuleFrame.Width - RatingControlGeometry.ExpandedLength) > Tolerance
                || Math.Abs(capsuleFrame.X - (slot.Width - RatingControlGeometry.ExpandedLength) / 2) > Tolerance)
                throw new InvalidOperationException($"展开胶囊 {Rect(capsuleFrame)} 未在槽位 224 长居中");
            await HoldAsync("rating-portrait-expanded");

            await SetRatingAsync(file, 2);
            int tap = _commits.Count;
            var row = phone.Row;
            row.SetNeedsLayout();
            row.LayoutIfNeeded();
            var tapCenter = SlotCenter(row, 4);
            row.TrackBegan(tapCenter);
            row.TrackEnded(tapCenter);
            await WaitAsync(() => _commits.Count == tap + 1, "手机竖屏展开点按写回一次");
            if (_commits[tap].Value != 4)
                throw new InvalidOperationException($"手机竖屏展开点按写回 {_commits[tap].Value} ≠ 4");
            await WaitForFileRatingAsync(file, 4, "手机竖屏展开点按");
            await WaitAsync(() => phone.Value == 4, "控件显示 4");

            await SetRatingAsync(file, 2);
            int drag = _commits.Count;
            row.TrackBegan(SlotCenter(row, 1));
            var previews = new List<int>();
            foreach (int value in new[] { 2, 3, 4 })
            {
                row.TrackMoved(SlotCenter(row, value));
                if (row.Preview != value)
                    throw new InvalidOperationException($"手机竖屏竖排滑动到 {value} 时预览 {row.Preview} ≠ {value}");
                previews.Add(value);
            }
            if (_commits.Count != drag)
                throw new InvalidOperationException($"手机竖屏展开滑动过程中写回了 {_commits.Count - drag} 次");
            row.TrackEnded(SlotCenter(row, 4));
            await WaitAsync(() => _commits.Count == drag + 1, "手机竖屏展开滑动写回一次");
            if (_commits[drag].Value != 4)
                throw new InvalidOperationException($"手机竖屏展开滑动写回 {_commits[drag].Value} ≠ 4");
            await WaitForFileRatingAsync(file, 4, "手机竖屏展开滑动");

            int expandedCancel = _commits.Count;
            row.TrackBegan(SlotCenter(row, 4));
            var outPoint = new CGPoint(SlotCenter(row, 4).X,
                (double)row.Bounds.Height + RatingControlGeometry.CancelSlop + 2);
            row.TrackMoved(outPoint);
            if (row.Preview != null)
                throw new InvalidOperationException($"手机竖屏展开越出取消带后预览仍为 {row.Preview}");
            row.TrackEnded(outPoint);
            if (_commits.Count != expandedCancel)
                throw new InvalidOperationException("手机竖屏展开越出取消带后仍写回星级");
            _lines.Add($"PASS RATING-07 手机竖屏展开：上下张关闭时槽位 {slot.Width:0.#} → 展开态、胶囊 224 居中；" +
                $"点 4 号写回 4、滑 1→{string.Join("→", previews)} 后抬手写回 4、越出取消带 0 次写回");

            // 冲突来源弹窗（竖屏快照）
            var fixture = _conflict ?? throw new InvalidOperationException("缺少冲突夹具");
            await RebuildConflictAsync(fixture);
            await EnsurePopupClosedAsync();
            int conflictCommits = _commits.Count;
            row.SetNeedsLayout();
            row.LayoutIfNeeded();
            var zero = SlotCenter(row, 0);
            row.TrackBegan(zero);
            row.TrackEnded(zero);
            if (!popup.IsOpen || popup.Mode != iOSRatingPopup.RatingPopupMode.Sources)
                throw new InvalidOperationException("手机竖屏冲突三角没有打开来源弹窗");
            if (_commits.Count != conflictCommits)
                throw new InvalidOperationException("手机竖屏冲突三角写回了星级");
            await HoldAsync("rating-portrait-popup-sources");
            popup.Close();
            await WaitAsync(() => !popup.IsOpen, "竖屏来源弹窗收起");

            // 横屏（刘海在左）
            await UiDispatcher.InvokeAsync(() =>
            {
                _root.SupportedOrientationsOverride = UIInterfaceOrientationMask.LandscapeRight;
                _root.SetNeedsUpdateOfSupportedInterfaceOrientations();
            });
            await RequestOrientationAsync(UIInterfaceOrientationMask.LandscapeRight);
            await SettleAsync();
            if (_root.Form != ScreenForm.PhoneLandscape || !_root.NotchOnLeft)
                throw new InvalidOperationException($"横屏（刘海在左）形态 {_root.Form}、刘海在左={_root.NotchOnLeft}");
            await VerifyLandscapeControlAsync(fixture, RatingPopupDirection.Left, "rating-landscape-notch-left",
                "rating-landscape-popup", suite: "横屏刘海在左");

            // 弹窗打开状态下旋转到刘海在右：旋转收起弹窗，控件值仍等于文件真值
            var conflictFile = _main.CurrentFile ?? throw new InvalidOperationException("横屏旋转检查没有当前照片");
            await EnsurePopupClosedAsync();
            row = phone.Row;
            row.SetNeedsLayout();
            row.LayoutIfNeeded();
            zero = SlotCenter(row, 0);
            row.TrackBegan(zero);
            row.TrackEnded(zero);
            if (!popup.IsOpen) throw new InvalidOperationException("旋转收起检查：来源弹窗未打开");
            await UiDispatcher.InvokeAsync(() =>
            {
                _root.SupportedOrientationsOverride = UIInterfaceOrientationMask.LandscapeLeft;
                _root.SetNeedsUpdateOfSupportedInterfaceOrientations();
            });
            await RequestOrientationAsync(UIInterfaceOrientationMask.LandscapeLeft);
            await WaitAsync(() => !popup.IsOpen, "旋转收起弹窗");
            await SettleAsync();
            if (_root.NotchOnLeft)
                throw new InvalidOperationException("横屏（刘海在右）仍未切换刘海方向");
            if (phone.Value != Math.Max(0, conflictFile.Rating))
                throw new InvalidOperationException(
                    $"旋转后控件显示 {phone.Value} ≠ 文件真值 {Math.Max(0, conflictFile.Rating)}");

            // 横屏（刘海在右）：同一份几何与方向表
            await VerifyLandscapeControlAsync(fixture, RatingPopupDirection.Right, snapshot: null,
                popupStage: null, suite: "横屏刘海在右");

            await UiDispatcher.InvokeAsync(() =>
            {
                _root.SupportedOrientationsOverride = UIInterfaceOrientationMask.Portrait;
                _root.SetNeedsUpdateOfSupportedInterfaceOrientations();
            });
            await RequestOrientationAsync(UIInterfaceOrientationMask.Portrait);
            await SettleAsync();
        }

        /// <summary>
        /// 手机横屏一个刘海方向：控制列竖排次序与形态、竖排滑动 5→2、冲突三角弹窗方向与面板所在侧。
        /// </summary>
        /// <param name="fixture">冲突夹具。</param>
        /// <param name="expected">该方向期望的弹窗方向。</param>
        /// <param name="snapshot">控制列截图检查点（null 不截图）。</param>
        /// <param name="popupStage">弹窗截图检查点（null 不截图）。</param>
        /// <param name="suite">报告范围名。</param>
        /// <returns>任务。</returns>
        private async Task VerifyLandscapeControlAsync(ConflictFixture fixture, RatingPopupDirection expected,
            string? snapshot, string? popupStage, string suite)
        {
            var phone = _root.PhoneRatingControl;
            var slot = _root.PhoneSlots.Rating;
            if (phone.Horizontal)
                throw new InvalidOperationException($"{suite} 星级控件不是竖排");
            if (Math.Abs((double)phone.Bounds.Width - RatingControlGeometry.Thickness) > Tolerance)
                throw new InvalidOperationException($"{suite} 竖排控件宽 {phone.Bounds.Width:0.#} ≠ 44");
            var stageFrame = FrameInStage(phone);
            if (!SameRect(stageFrame, new CGRect(slot.X, slot.Y, slot.Width, slot.Height)))
                throw new InvalidOperationException($"{suite} 控件 frame {Rect(stageFrame)} 与槽位 {Rect(slot)} 不一致");
            if (phone.Form != RatingControlGeometry.ResolveForm(slot.Height))
                throw new InvalidOperationException(
                    $"{suite} 槽位高 {slot.Height:0.#} 的形态 {phone.Form} 与 ResolveForm 不一致");
            if (phone.Form != RatingControlForm.Expanded)
                throw new InvalidOperationException($"{suite} 控制列星级槽位高 {slot.Height:0.#} 未达展开长度 224");
            var row = phone.Row;
            row.SetNeedsLayout();
            row.LayoutIfNeeded();
            if (row.SlotViews[5].Frame.Y >= row.SlotViews[0].Frame.Y)
                throw new InvalidOperationException($"{suite} 竖排星列 5 号未排在 0 号上方");
            if (snapshot != null) await HoldAsync(snapshot);

            await ResolveConflictAsync(fixture, 3);
            await SetRatingAsync(fixture.Photo, 3);
            int before = _commits.Count;
            row.TrackBegan(SlotCenter(row, 5));
            if (row.Preview != 5) throw new InvalidOperationException($"{suite} 竖排起手预览 {row.Preview} ≠ 5");
            var previews = new List<int> { 5 };
            foreach (int value in new[] { 4, 3, 2 })
            {
                row.TrackMoved(SlotCenter(row, value));
                if (row.Preview != value)
                    throw new InvalidOperationException($"{suite} 竖排滑动到 {value} 时预览 {row.Preview} ≠ {value}");
                previews.Add(value);
            }
            if (_commits.Count != before)
                throw new InvalidOperationException($"{suite} 竖排滑动过程中写回了 {_commits.Count - before} 次");
            row.TrackEnded(SlotCenter(row, 2));
            await WaitAsync(() => _commits.Count == before + 1, $"{suite} 竖排滑动写回一次");
            if (_commits[before].Value != 2)
                throw new InvalidOperationException($"{suite} 竖排滑动写回 {_commits[before].Value} ≠ 2");
            await WaitForFileRatingAsync(fixture.Photo, 2, $"{suite} 竖排滑动");

            await RebuildConflictAsync(fixture);
            await EnsurePopupClosedAsync();
            var popup = _root.RatingPopup;
            row.SetNeedsLayout();
            row.LayoutIfNeeded();
            var zero = SlotCenter(row, 0);
            row.TrackBegan(zero);
            row.TrackEnded(zero);
            if (!popup.IsOpen) throw new InvalidOperationException($"{suite} 冲突三角没有打开弹窗");
            if (popup.Mode != iOSRatingPopup.RatingPopupMode.Sources)
                throw new InvalidOperationException($"{suite} 冲突弹窗形态为 {popup.Mode} ≠ Sources");
            if (popup.Direction != expected)
                throw new InvalidOperationException($"{suite} 控件弹窗方向 {popup.Direction} ≠ 期望 {expected}");
            var capsuleHost = FrameInHost(phone.Capsule);
            RequirePanelOnSide(new LayoutRect(capsuleHost.X, capsuleHost.Y, capsuleHost.Width, capsuleHost.Height),
                expected, suite);
            if (popupStage != null) await HoldAsync(popupStage);
            _lines.Add($"PASS RATING-07/04 {suite}：控制列为竖排（宽 44、槽位高 {slot.Height:0.#} → 展开）、" +
                $"星列 5 号在 0 号上方；竖排滑动预览 {string.Join("→", previews)} 后抬手写回 2；" +
                $"冲突三角弹窗方向 {expected} 且面板落在该侧");
        }

        #endregion

        #region iPad：宽形态控制栏

        /// <summary>iPad：行/列两向的星级分组、胶囊与命令按钮几何、冲突三角弹窗方向，以及显示评分显隐。</summary>
        /// <returns>任务。</returns>
        private async Task VerifyPadAsync()
        {
            var fixture = _conflict ?? throw new InvalidOperationException("缺少冲突夹具");
            await UiDispatcher.InvokeAsync(() =>
            {
                _main.Settings.LayoutMode = LayoutMode.Row;
                _main.Settings.ShowRating = true;
                _root.BuildControls();
            });
            await WaitAsync(() => _root.Form == ScreenForm.Wide && _main.IsRowLayout, "宽形态行布局");
            await SettleAsync();
            var control = _root.ControlBar.RatingControl;
            if (!_root.ControlBar.HasRatingGroup)
                throw new InvalidOperationException("宽形态行布局缺少星级分组");
            if (control.Form != RatingControlForm.Expanded)
                throw new InvalidOperationException($"行布局星级控件形态为 {control.Form} ≠ Expanded");
            RequireCapsule(control, 224, 44, "行布局");
            RequireCommandButtons(44, "行布局");
            await HoldAsync("rating-wide-row");

            await RebuildConflictAsync(fixture);
            await EnsurePopupClosedAsync();
            var popup = _root.RatingPopup;
            int before = _commits.Count;
            var row = control.Row;
            row.SetNeedsLayout();
            row.LayoutIfNeeded();
            var zero = SlotCenter(row, 0);
            row.TrackBegan(zero);
            row.TrackEnded(zero);
            if (!popup.IsOpen) throw new InvalidOperationException("行布局冲突三角没有打开弹窗");
            if (popup.Mode != iOSRatingPopup.RatingPopupMode.Sources)
                throw new InvalidOperationException($"行布局冲突弹窗形态为 {popup.Mode} ≠ Sources");
            if (popup.Direction != RatingPopupDirection.Up)
                throw new InvalidOperationException($"行布局控件弹窗方向 {popup.Direction} ≠ Up");
            var capsuleHost = FrameInHost(control.Capsule);
            RequirePanelOnSide(new LayoutRect(capsuleHost.X, capsuleHost.Y, capsuleHost.Width, capsuleHost.Height),
                RatingPopupDirection.Up, "行布局");
            if (_commits.Count != before) throw new InvalidOperationException("行布局冲突三角写回了星级");
            await HoldAsync("rating-wide-row-popup-sources");
            popup.Close();
            await WaitAsync(() => !popup.IsOpen, "行布局来源弹窗收起");
            _lines.Add("PASS RATING-07 宽形态行布局：星级分组在场、控件展开、胶囊 224×44、命令按钮 44×44；" +
                "冲突三角打开 Sources 弹窗（方向 Up、面板在上方或贴住放置边界）且 0 次写回");

            await UiDispatcher.InvokeAsync(() => _main.Settings.LayoutMode = LayoutMode.Col);
            await WaitAsync(() => !_main.IsRowLayout, "宽形态列布局");
            await SettleAsync();
            control = _root.ControlBar.RatingControl;
            if (control.Form != RatingControlForm.Expanded)
                throw new InvalidOperationException($"列布局星级控件形态为 {control.Form} ≠ Expanded");
            RequireCapsule(control, 44, 224, "列布局");
            RequireCommandButtons(44, "列布局");
            await HoldAsync("rating-wide-col");

            await EnsurePopupClosedAsync();
            row = control.Row;
            row.SetNeedsLayout();
            row.LayoutIfNeeded();
            zero = SlotCenter(row, 0);
            row.TrackBegan(zero);
            row.TrackEnded(zero);
            if (!popup.IsOpen) throw new InvalidOperationException("列布局冲突三角没有打开弹窗");
            if (popup.Direction != RatingPopupDirection.Left)
                throw new InvalidOperationException($"列布局控件弹窗方向 {popup.Direction} ≠ Left");
            capsuleHost = FrameInHost(control.Capsule);
            RequirePanelOnSide(new LayoutRect(capsuleHost.X, capsuleHost.Y, capsuleHost.Width, capsuleHost.Height),
                RatingPopupDirection.Left, "列布局");
            await HoldAsync("rating-wide-col-popup");
            popup.Close();
            await WaitAsync(() => !popup.IsOpen, "列布局来源弹窗收起");
            _lines.Add("PASS RATING-07 宽形态列布局：控件展开、胶囊 44×224、命令按钮 44×44；" +
                "冲突三角弹窗方向 Left 且面板落在控件左侧（放不下时贴住放置边界）");

            await UiDispatcher.InvokeAsync(() => _main.Settings.ShowRating = false);
            await WaitAsync(() => !_root.ControlBar.HasRatingGroup, "显示评分关闭移除星级组");
            await SettleAsync();
            if (!_root.ControlBar.RatingControl.Hidden)
                throw new InvalidOperationException("显示评分关闭后星级控件仍可见");
            await UiDispatcher.InvokeAsync(() => _main.Settings.ShowRating = true);
            await WaitAsync(() => _root.ControlBar.HasRatingGroup, "显示评分开启恢复星级组");
            await SettleAsync();
            if (_root.ControlBar.RatingControl.Hidden)
                throw new InvalidOperationException("显示评分开启后星级控件仍隐藏");
            if (_root.ControlBar.RatingControl.Form != RatingControlForm.Expanded)
                throw new InvalidOperationException($"恢复后星级控件形态为 {_root.ControlBar.RatingControl.Form} ≠ Expanded");
            _lines.Add("PASS TOOLBAR-03 显示评分：关闭后宽形态控制栏移除星级分组（HasRatingGroup=false、控件隐藏），" +
                "开启后星级分组恢复为展开态");
        }

        /// <summary>核对星级控件胶囊尺寸（控件自身坐标）。</summary>
        /// <param name="control">星级控件。</param>
        /// <param name="width">期望宽。</param>
        /// <param name="height">期望高。</param>
        /// <param name="context">报告范围名。</param>
        private static void RequireCapsule(iOSRatingControl control, double width, double height, string context)
        {
            var frame = control.CapsuleFrame;
            if (Math.Abs(frame.Width - width) > Tolerance || Math.Abs(frame.Height - height) > Tolerance)
                throw new InvalidOperationException($"{context} 胶囊 {Rect(frame)} ≠ {width:0.#}×{height:0.#}");
        }

        /// <summary>核对控制栏命令按钮都是 44 点圆按钮。</summary>
        /// <param name="size">期望边长。</param>
        /// <param name="context">报告范围名。</param>
        private void RequireCommandButtons(double size, string context)
        {
            var commands = _root.ControlBar.Commands;
            if (commands.Count == 0) throw new InvalidOperationException($"{context} 控制栏没有命令按钮");
            foreach (var (command, button) in commands)
            {
                var frame = button.Frame;
                if (Math.Abs((double)frame.Width - size) > Tolerance || Math.Abs((double)frame.Height - size) > Tolerance)
                    throw new InvalidOperationException($"{context} 命令 {command} 按钮 {Rect(frame)} ≠ {size:0.#}×{size:0.#}");
            }
        }

        #endregion

        #region 通用等待、旋转与几何判断

        /// <summary>轮询条件，超时抛错（不输出虚假通过）。</summary>
        /// <param name="condition">条件。</param>
        /// <param name="label">等待标签。</param>
        /// <param name="seconds">超时秒数。</param>
        /// <returns>任务。</returns>
        private static async Task WaitAsync(Func<bool> condition, string label, int seconds = 20)
        {
            var watch = Stopwatch.StartNew();
            while (!condition())
            {
                if (watch.Elapsed > TimeSpan.FromSeconds(seconds))
                    throw new TimeoutException($"等待超时：{label}");
                await Task.Delay(25);
            }
        }

        /// <summary>等待一轮布局落地（UIKit 布局 + 共享 VM 通知）。</summary>
        /// <returns>任务。</returns>
        private async Task SettleAsync()
        {
            await Task.Delay(120);
            await UiDispatcher.InvokeAsync(() =>
            {
                _root.View!.LayoutIfNeeded();
                _root.Stage.LayoutIfNeeded();
                _root.ControlBar.LayoutIfNeeded();
            });
            await Task.Delay(80);
        }

        /// <summary>当前界面方向（以 window scene 的有效几何为准）。</summary>
        /// <returns>界面方向。</returns>
        private UIInterfaceOrientation CurrentOrientation()
            => _root.View?.Window?.WindowScene?.EffectiveGeometry?.InterfaceOrientation ?? UIInterfaceOrientation.Unknown;

        /// <summary>确保界面在竖屏（iPhone 竖屏段与收尾共用）。</summary>
        /// <returns>任务。</returns>
        private async Task EnsurePortraitAsync()
        {
            if (CurrentOrientation() == UIInterfaceOrientation.Portrait) return;
            await UiDispatcher.InvokeAsync(() =>
            {
                _root.SupportedOrientationsOverride = UIInterfaceOrientationMask.Portrait;
                _root.SetNeedsUpdateOfSupportedInterfaceOrientations();
            });
            await RequestOrientationAsync(UIInterfaceOrientationMask.Portrait);
        }

        /// <summary>请求系统旋转到指定方向并等待界面方向到位（与形态验收同一口径）。</summary>
        /// <param name="mask">目标方向掩码（单一方向）。</param>
        /// <returns>任务。</returns>
        private async Task RequestOrientationAsync(UIInterfaceOrientationMask mask)
        {
            var expected = mask switch
            {
                UIInterfaceOrientationMask.Portrait => UIInterfaceOrientation.Portrait,
                UIInterfaceOrientationMask.LandscapeRight => UIInterfaceOrientation.LandscapeRight,
                UIInterfaceOrientationMask.LandscapeLeft => UIInterfaceOrientation.LandscapeLeft,
                _ => throw new InvalidOperationException($"星级验收只支持单一方向请求：{mask}"),
            };
            var scene = _root.View!.Window?.WindowScene
                ?? throw new InvalidOperationException("主屏尚未连接到 window scene，无法请求旋转");
            bool accepted = false;
            string failure = "未收到回调";
            scene.RequestGeometryUpdate(new UIWindowSceneGeometryPreferencesIOS(mask), error =>
            {
                accepted = error == null;
                failure = error?.LocalizedDescription ?? string.Empty;
            });
            var watch = Stopwatch.StartNew();
            while (watch.Elapsed < TimeSpan.FromSeconds(10))
            {
                await Task.Delay(100);
                if (CurrentOrientation() == expected) break;
            }
            var actual = CurrentOrientation();
            if (actual != expected)
            {
                throw new InvalidOperationException(
                    $"方向切换超时：请求 {mask}，实际 {actual}（accepted={accepted}，error={failure}，" +
                    $"窗口 {Size(_root.View!.Bounds.Size)}，形态 {_root.Form}）");
            }
            await SettleAsync();
        }

        /// <summary>取胶片栏某一项的卡片；不在可见范围时先程序化滚到该项。</summary>
        /// <param name="index">显示快照索引。</param>
        /// <param name="scope">报告范围名。</param>
        /// <returns>已绑定该索引的卡片。</returns>
        private async Task<iOSThumbnailCard> WaitForCardAsync(int index, string scope)
        {
            var files = _main.FileVM.ThumbnailList.FilteredFiles;
            if (index < 0 || index >= files.Count)
                throw new InvalidOperationException($"{scope} 目标索引越界：{index}/{files.Count}");
            var visible = _root.Filmstrip.Collection.IndexPathsForVisibleItems.Select(path => (int)path.Item).ToArray();
            if (!visible.Contains(index))
            {
                // 共享实例：UIKit 回调与验收可能同时持有同一包装，不得释放。
                var path = NSIndexPath.FromItemSection(index, 0);
                _root.Filmstrip.Collection.ScrollToItem(path, UICollectionViewScrollPosition.None, false);
                await SettleAsync();
            }
            var deadline = DateTime.UtcNow.AddSeconds(6);
            while (DateTime.UtcNow < deadline)
            {
                _root.Filmstrip.Collection.LayoutIfNeeded();
                if (_root.Filmstrip.CardAt(index) is { } card && ReferenceEquals(card.BoundFile, files[index])) return card;
                await Task.Delay(25);
            }
            throw new TimeoutException(
                $"{scope} 胶片栏卡片未创建（索引 {index}，偏移 {Rect(_root.Filmstrip.Collection.ContentOffset)}）");
        }

        /// <summary>面板必须完整落在宿主安全区内。</summary>
        /// <param name="context">报告范围名。</param>
        private void RequirePanelInsideSafeArea(string context)
        {
            var host = _root.View!;
            var safe = host.SafeAreaInsets;
            var allowed = new CGRect((double)safe.Left, (double)safe.Top,
                Math.Max(1, (double)host.Bounds.Width - (double)safe.Left - (double)safe.Right),
                Math.Max(1, (double)host.Bounds.Height - (double)safe.Top - (double)safe.Bottom));
            var frame = _root.RatingPopup.PanelFrame;
            var panel = new CGRect(frame.X, frame.Y, frame.Width, frame.Height);
            if (!Contains(allowed, panel))
                throw new InvalidOperationException($"{context} 面板 {Rect(panel)} 越出宿主安全区 {Rect(allowed)}");
        }

        /// <summary>面板必须落在入口的指定一侧；放不下时允许贴住该侧的放置边界（与共享落位夹取同义）。</summary>
        /// <param name="anchor">入口在宿主坐标中的矩形。</param>
        /// <param name="direction">展开方向。</param>
        /// <param name="context">报告范围名。</param>
        private void RequirePanelOnSide(LayoutRect anchor, RatingPopupDirection direction, string context)
        {
            var panel = _root.RatingPopup.PanelFrame;
            bool onSide = direction switch
            {
                RatingPopupDirection.Up => panel.MaxY <= anchor.Y + Tolerance,
                RatingPopupDirection.Down => panel.Y >= anchor.MaxY - Tolerance,
                RatingPopupDirection.Left => panel.MaxX <= anchor.X + Tolerance,
                _ => panel.X >= anchor.MaxX - Tolerance,
            };
            if (onSide) return;
            var host = _root.View!;
            var safe = host.SafeAreaInsets;
            double edge = DesignTokens.PopupRatingSourcesEdgeMargin;
            var bounds = new LayoutRect((double)safe.Left + edge, (double)safe.Top + edge,
                Math.Max(1, (double)host.Bounds.Width - (double)safe.Left - (double)safe.Right - edge * 2),
                Math.Max(1, (double)host.Bounds.Height - (double)safe.Top - (double)safe.Bottom - edge * 2));
            bool clamped = direction switch
            {
                RatingPopupDirection.Up => panel.Y <= bounds.Y + Tolerance,
                RatingPopupDirection.Down => panel.MaxY >= bounds.MaxY - Tolerance,
                RatingPopupDirection.Left => panel.X <= bounds.X + Tolerance,
                _ => panel.MaxX >= bounds.MaxX - Tolerance,
            };
            if (!clamped)
                throw new InvalidOperationException(
                    $"{context} 面板 {Rect(panel)} 既不在入口 {Rect(anchor)} 的 {direction} 侧，也没有贴住放置边界 {Rect(bounds)}");
        }

        /// <summary>比较颜色与令牌（分量逐项 ±0.01）。</summary>
        /// <param name="actual">实际颜色。</param>
        /// <param name="token">令牌 ARGB。</param>
        /// <param name="context">报告范围名。</param>
        private static void RequireColor(UIColor? actual, uint token, string context)
        {
            var expected = iOSColors.FromToken(token);
            var left = actual?.CGColor.Components ?? [];
            var right = expected.CGColor.Components ?? [];
            if (actual == null || left.Length != right.Length
                || left.Zip(right).Any(pair => Math.Abs((double)(pair.First - pair.Second)) > .01))
                throw new InvalidOperationException(
                    $"{context}：实际 {actual?.ToString() ?? "null"}，预期令牌 0x{token:X8}");
        }

        /// <summary>两个 UIKit 矩形是否一致（±0.5）。</summary>
        /// <param name="left">矩形一。</param>
        /// <param name="right">矩形二。</param>
        /// <returns>一致时为 true。</returns>
        private static bool SameRect(CGRect left, CGRect right)
            => Math.Abs((double)left.X - (double)right.X) <= Tolerance
                && Math.Abs((double)left.Y - (double)right.Y) <= Tolerance
                && Math.Abs((double)left.Width - (double)right.Width) <= Tolerance
                && Math.Abs((double)left.Height - (double)right.Height) <= Tolerance;

        /// <summary>外矩形是否包含内矩形（±0.5）。</summary>
        /// <param name="outer">外矩形。</param>
        /// <param name="inner">内矩形。</param>
        /// <returns>包含时为 true。</returns>
        private static bool Contains(CGRect outer, CGRect inner)
            => (double)inner.X >= (double)outer.X - Tolerance && (double)inner.Y >= (double)outer.Y - Tolerance
                && (double)inner.GetMaxX() <= (double)outer.GetMaxX() + Tolerance
                && (double)inner.GetMaxY() <= (double)outer.GetMaxY() + Tolerance;

        /// <summary>仿射变换是否为单位矩阵。</summary>
        /// <param name="transform">变换。</param>
        /// <returns>为单位矩阵时为 true。</returns>
        private static bool IsIdentity(CGAffineTransform transform)
            => Math.Abs((double)transform.A - 1) < 1e-6 && Math.Abs((double)transform.B) < 1e-6
                && Math.Abs((double)transform.C) < 1e-6 && Math.Abs((double)transform.D - 1) < 1e-6
                && Math.Abs((double)transform.Tx) < .01 && Math.Abs((double)transform.Ty) < .01;

        /// <summary>尺寸格式化。</summary>
        /// <param name="size">尺寸。</param>
        /// <returns>文本。</returns>
        private static string Size(CGSize size) => FormattableString.Invariant($"{size.Width:0.#}×{size.Height:0.#}");

        /// <summary>内边距格式化。</summary>
        /// <param name="insets">内边距。</param>
        /// <returns>文本。</returns>
        private static string Insets(UIEdgeInsets insets)
            => FormattableString.Invariant($"T{insets.Top:0.#} L{insets.Left:0.#} B{insets.Bottom:0.#} R{insets.Right:0.#}");

        /// <summary>共享矩形格式化。</summary>
        /// <param name="rect">矩形。</param>
        /// <returns>文本。</returns>
        private static string Rect(LayoutRect rect)
            => FormattableString.Invariant($"{rect.X:0.#},{rect.Y:0.#} {rect.Width:0.#}×{rect.Height:0.#}");

        /// <summary>UIKit 矩形格式化。</summary>
        /// <param name="rect">矩形。</param>
        /// <returns>文本。</returns>
        private static string Rect(CGRect rect)
            => FormattableString.Invariant($"{rect.X:0.#},{rect.Y:0.#} {rect.Width:0.#}×{rect.Height:0.#}");

        /// <summary>UIKit 点格式化。</summary>
        /// <param name="point">点。</param>
        /// <returns>文本。</returns>
        private static string Rect(CGPoint point) => FormattableString.Invariant($"{point.X:0.#},{point.Y:0.#}");

        #endregion
    }
}
