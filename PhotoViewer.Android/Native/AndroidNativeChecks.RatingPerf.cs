using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using Activity = Android.App.Activity;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using Android.Views;
using PhotoViewer.Android.Core;
using PhotoViewer.Core;
using PhotoViewer.Core.Image;
using PhotoViewer.Shared;
using PhotoViewer.ViewModels.Main;
using IOPath = System.IO.Path;

namespace PhotoViewer.Android.Native;

/// <summary>
/// <c>--es pv-check rating-perf</c>（验收专用）：在真实 SAF <c>content://</c> 文档树上量「连续改星」的墙钟，
/// 用来定位真机（HIF + ARW + 同名 sidecar 三来源组）写评分慢的位置。
/// </summary>
/// <remarks>
/// <para>素材由调用方推到 App 内部 <c>files/pv-rating-fixtures/</c>（真实相机的
/// <c>&lt;stem&gt;.HIF</c> / <c>&lt;stem&gt;.ARW</c> 与可选同名 <c>&lt;stem&gt;.xmp</c>）。本模式只**读**该目录，
/// 把同一组输入按 <c>rp-01</c>/<c>rp-02</c>/<c>rp-03</c> 复制到 <see cref="DebugTestDocumentsProvider"/> 的
/// <c>writable/rating-perf</c> 测试树，之后所有读写都经真实 <c>content://</c> 通路；绝不写输入夹具、用户照片或外部存储。
/// 目录缺失时用 App 内部合成的一张 JPEG + 同名 <c>.xmp</c> 兜底，报告里注明「合成回退」。</para>
/// <para>测量口径：<c>trigger</c> 是入口触发到 VM 收敛（星级与冲突态都到位）的墙钟——tap 入口走
/// 星级控件的真实触摸（展开态点槽位 / 紧凑态打开弹窗再点目标格），业务入口走共享
/// <see cref="MainViewModel.SetRatingWithResultAsync"/>；<c>disk</c> 是随后逐来源（内嵌 + 同名 sidecar）
/// 重新读盘的墙钟。两者都不含夹具复制开销；写入窗口内的通知数与「冲突为真」次数一并记录，
/// 用来判断转场/暂态里是否闪出假冲突。<c>rating-perf</c> 不设速度门槛：判定只看写入是否收敛、
/// 通知期间是否出现假冲突，耗时全部写进报告供人读。</para>
/// <para><c>DebugTestDocumentsProvider</c> 只随 <c>-p:PhotoViewerAndroidStorageChecks=true</c> 的 Debug
/// 构建注册；Release 与普通 Debug 跑本模式都会如实报 FAIL 并结束。</para>
/// </remarks>
internal static partial class AndroidNativeChecks
{
    /// <summary>输入夹具目录（App 内部 <c>files/</c> 下）；只读，不参与写入。</summary>
    private const string RatingPerfInputFolderName = "pv-rating-fixtures";

    /// <summary>合成回退夹具目录（同样是 App 内部；与真机夹具分开，避免混淆来源）。</summary>
    private const string RatingPerfGeneratedFolderName = "pv-rating-fixtures-generated";

    /// <summary>验收 Provider writable 树里的测试子目录；每次运行先清空重建。</summary>
    private const string RatingPerfStoreFolderName = "rating-perf";

    /// <summary>同一组输入复制成几个测试 basename；≥2 组才能测「不同组并行」。</summary>
    private const int RatingPerfCopies = 3;

    /// <summary>一致评分基线：先把整组统一写到该值，再从它开始连续改星。</summary>
    private const int RatingPerfBaselineRating = 3;

    /// <summary>一次写入结束后再等一小段，采样可能迟到到达的通知与假冲突。</summary>
    private const int RatingPerfSettleMs = 200;

    /// <summary>一组输入夹具：同一 basename 的照片（HIF/ARW…）与可选同名 sidecar。</summary>
    /// <param name="Stem">原始 basename。</param>
    /// <param name="Images">照片文件绝对路径（App 内部夹具目录内，只读）。</param>
    /// <param name="Sidecar">同名 <c>.xmp</c> 绝对路径；没有为 null。</param>
    /// <param name="Generated">是否合成回退夹具。</param>
    private sealed record RatingPerfInput(
        string Stem, IReadOnlyList<string> Images, string? Sidecar, bool Generated);

    /// <summary>一次连续改星采样的各段耗时与收敛结果。</summary>
    /// <param name="Entry">入口口径（<c>tap</c> / <c>business</c>）。</param>
    /// <param name="Requested">目标星级。</param>
    /// <param name="Outcome">写入结果或等待状态。</param>
    /// <param name="TriggerMs">入口触发到 VM 收敛的墙钟（ms）。</param>
    /// <param name="DiskMs">逐来源重新读盘墙钟（ms）。</param>
    /// <param name="Notifications">写入窗口内 <c>Rating</c>/<c>RatingSources</c>/<c>HasRatingConflict</c> 通知数。</param>
    /// <param name="ConflictFlips">其中观察到组级冲突为真的次数（假冲突指针）。</param>
    /// <param name="ConflictAfter">收尾后组级冲突态。</param>
    /// <param name="Verdict">VM 是否收敛到目标值。</param>
    /// <param name="Sources">逐来源磁盘真值摘要。</param>
    /// <param name="Converged">全部来源磁盘真值是否都等于目标值。</param>
    private sealed record RatingPerfSample(
        string Entry,
        int Requested,
        string Outcome,
        double TriggerMs,
        double DiskMs,
        int Notifications,
        int ConflictFlips,
        bool ConflictAfter,
        bool Verdict,
        string Sources,
        bool Converged);

    /// <summary>逐来源读盘结果。</summary>
    /// <param name="Converged">全部来源是否都等于期望星级。</param>
    /// <param name="Ms">读盘墙钟（ms）。</param>
    /// <param name="Detail">逐来源摘要。</param>
    private sealed record RatingPerfDiskRead(bool Converged, double Ms, string Detail);

    /// <summary><c>rating-perf</c> 主流程：准备夹具 → 打开 SAF 文件夹 → 基线 → 连续改星 → 转场/并行采样。</summary>
    private static async Task RunRatingPerfChecksAsync()
    {
        await WaitForScreenAsync();
        var activity = _activity!;
        var main = _main!;
        var session = _session!;
        var resolver = activity.ContentResolver ?? Application.Context.ContentResolver!;

        var provider = activity.PackageManager?.ResolveContentProvider(
            DebugTestDocumentsProvider.TestAuthority, PackageInfoFlags.MatchDefaultOnly);
        if (provider == null)
        {
            Check("rating-perf 验收 Provider 可用", false,
                $"未注册 {DebugTestDocumentsProvider.TestAuthority}（只有 Debug 且显式传 "
                + "-p:PhotoViewerAndroidStorageChecks=true 的构建注册 DebugTestDocumentsProvider；"
                + "Release 与普通 Debug 都不注册）");
            return;
        }
        Check("rating-perf 验收 Provider 可用", true,
            $"authority={DebugTestDocumentsProvider.TestAuthority} safeSetRating={main.Settings.SafeSetRating} "
            + $"sameNameAsOnePhoto={main.Settings.SameNameAsOnePhoto}");

        var input = DiscoverRatingPerfInput(activity);
        if (input == null) return;
        int copies = CopyRatingPerfFixtures(input);
        if (copies == 0) return;

        var treeUri = TreeUri(DebugTestDocumentsProvider.WritableRoot + "/" + RatingPerfStoreFolderName);
        var folder = new AndroidFolderRef(resolver, treeUri, RatingPerfStoreFolderName);
        IReadOnlyList<PhotoGroup>? groups = null;
        await RunStepAsync("rating-perf 打开 SAF 文件夹", async () =>
        {
            var sw = Stopwatch.StartNew();
            await main.FolderVM.OpenFolderAsync(folder);
            // 同名合并打开时列表只列组代表；关闭合并时每个文件各占一项。
            int expected = main.Settings.SameNameAsOnePhoto
                ? copies
                : copies * input.Images.Count;
            await WaitAsync(() => main.CurrentFile != null
                    && main.FileVM.ThumbnailList.FilteredFiles.Count >= expected,
                $"SAF 文件夹上屏：{RatingPerfStoreFolderName}", 90000);
            await WaitForListSettledAsync(main);
            double openMs = sw.Elapsed.TotalMilliseconds;
            var files = main.FileVM.ThumbnailList.FilteredFiles;
            groups = RatingPerfGroups(main);
            Check("rating-perf 打开 SAF 文件夹", groups.Count >= 2,
                $"openMs={openMs:F0} files={files.Count}/{expected} groups={groups.Count} "
                + $"mergeSameName={main.Settings.SameNameAsOnePhoto} "
                + $"current={main.CurrentFile?.Name ?? "null"} "
                + $"names=[{string.Join(",", files.Select(file => file.Name))}]");
        });
        if (groups is null || groups.Count < 2) return;
        var targets = groups.Select(group => group.Representative).ToArray();

        await RunStepAsync("rating-perf 读取吞吐基线", () => ReadRatingPerfThroughputAsync(targets[0]));

        bool baselineOk = false;
        await RunStepAsync("rating-perf 一致评分基线", async () =>
        {
            baselineOk = await NormalizeRatingPerfGroupsAsync(targets, RatingPerfBaselineRating);
        });

        await RunStepAsync("rating-perf 连续改星（tap）", async () =>
        {
            var samples = new List<RatingPerfSample>();
            if (!await SelectRatingPerfTargetAsync(main, targets[0]))
            {
                Check("rating-perf 连续改星（tap）", false, $"{targets[0].Name} 未成为当前照片");
                return;
            }
            await Task.Delay(250);
            foreach (int value in new[] { 4, 5, 2, 3 })
                samples.Add(await MeasureRatingPerfChangeAsync(main, targets[0], value, useTap: true));
            AppendRatingPerfVerdict("rating-perf 连续改星（tap）", samples);
        });

        await RunStepAsync("rating-perf 重复采样（业务入口）", async () =>
        {
            var samples = new List<RatingPerfSample>();
            foreach (int value in new[] { 5, 2, 4 })
                samples.Add(await MeasureRatingPerfChangeAsync(main, targets[0], value, useTap: false));
            foreach (int value in new[] { 5, 2, 3 })
                samples.Add(await MeasureRatingPerfChangeAsync(main, targets[1], value, useTap: false));
            AppendRatingPerfVerdict("rating-perf 重复采样（业务入口）", samples);
            // 基线写入本身也计入假冲突统计：真机慢的正是这一段的来源收敛过程。
            Check("rating-perf 写入期间无假冲突",
                baselineOk && samples.All(sample => sample.ConflictFlips == 0 && !sample.ConflictAfter),
                $"baselineOk={baselineOk} "
                + $"flips=[{string.Join(",", samples.Select(sample => sample.ConflictFlips))}] "
                + $"after=[{string.Join(",", samples.Select(sample => sample.ConflictAfter))}]");
        });

        await RunStepAsync("rating-perf 新图转场首写", async () =>
        {
            var target = targets[1];
            var sw = Stopwatch.StartNew();
            int generation = session.Generation;
            main.FileVM.ThumbnailList.SelectImageCommand(target);
            await WaitAsync(() => ReferenceEquals(main.CurrentFile, target)
                    && session.Generation > generation && session.Image != null,
                $"转场到 {target.Name}", 90000);
            double switchMs = sw.Elapsed.TotalMilliseconds;
            var sample = await MeasureRatingPerfChangeAsync(main, target, 4, useTap: false);
            Check("rating-perf 新图转场首写", sample.Converged && sample.Verdict,
                $"switchMs={switchMs:F0} image={session.Image?.Width}x{session.Image?.Height} "
                + DescribeRatingPerfSample(sample));
        });

        await RunStepAsync("rating-perf 不同组并行", async () =>
        {
            var first = targets[1];
            var second = targets.Length > 2 ? targets[2] : targets[0];
            double firstMs = -1, secondMs = -1;
            var sw = Stopwatch.StartNew();
            var firstTask = main.SetRatingWithResultAsync(first, 5)
                .ContinueWith(_ => firstMs = sw.Elapsed.TotalMilliseconds, TaskScheduler.Default);
            var secondTask = main.SetRatingWithResultAsync(second, 2)
                .ContinueWith(_ => secondMs = sw.Elapsed.TotalMilliseconds, TaskScheduler.Default);
            await Task.WhenAll(firstTask, secondTask);
            double bothMs = sw.Elapsed.TotalMilliseconds;
            var firstRead = await ReadRatingPerfSourcesAsync(first, 5);
            var secondRead = await ReadRatingPerfSourcesAsync(second, 2);
            bool ok = firstRead.Converged && secondRead.Converged
                && !first.HasRatingConflict && !second.HasRatingConflict;
            Check("rating-perf 不同组并行", ok,
                $"{first.Name}:{firstMs:F0}ms/{firstRead.Detail} {second.Name}:{secondMs:F0}ms/{secondRead.Detail} "
                + $"bothMs={bothMs:F0} conflict={first.HasRatingConflict}/{second.HasRatingConflict}");
        });
    }

    // ── 夹具 ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// 识别输入夹具：<c>files/pv-rating-fixtures/</c> 下按 basename 归组（同一 stem 的 HIF/ARW 即真机那一组，
    /// 同名 <c>.xmp</c> 是共享来源）。取文件最多的一组（优先「照片 + sidecar」三元组）；
    /// 目录缺失或没有照片时用合成 JPEG + 同名 <c>.xmp</c> 兜底。
    /// </summary>
    /// <param name="activity">宿主 Activity（提供内部目录）。</param>
    /// <returns>选中的夹具；无法准备返回 null（已记 FAIL）。</returns>
    private static RatingPerfInput? DiscoverRatingPerfInput(Activity activity)
    {
        string folder = IOPath.Combine(activity.FilesDir!.AbsolutePath, RatingPerfInputFolderName);
        string[] entries = Directory.Exists(folder)
            ? Directory.GetFiles(folder).OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToArray()
            : [];
        var byStem = entries
            .Where(path => !string.Equals(IOPath.GetExtension(path), ".xmp", StringComparison.OrdinalIgnoreCase))
            .GroupBy(path => IOPath.GetFileNameWithoutExtension(path), StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(group => group.Count())
            .ThenBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (byStem.Length == 0)
        {
            var generated = GenerateRatingPerfFallbackInput(activity);
            Check("rating-perf 夹具识别", generated != null,
                generated == null
                    ? $"没有可用素材：{folder} 为空或缺目录，合成回退也失败"
                    : $"未找到 {folder} 下的照片，改用合成回退素材（来源={RatingPerfGeneratedFolderName}，"
                        + "只能验证通路与耗时量级，HIF/ARW 结论需推真机夹具）；"
                        + $"images=[{string.Join(",", generated.Images.Select(IOPath.GetFileName))}] "
                        + $"sidecar={IOPath.GetFileName(generated.Sidecar ?? string.Empty)}");
            return generated;
        }

        var chosen = byStem[0];
        var images = chosen.Select(path => IOPath.GetFullPath(path)).ToArray();
        string? sidecar = entries.FirstOrDefault(path =>
            string.Equals(IOPath.GetExtension(path), ".xmp", StringComparison.OrdinalIgnoreCase)
            && string.Equals(IOPath.GetFileNameWithoutExtension(path), chosen.Key, StringComparison.OrdinalIgnoreCase));
        var input = new RatingPerfInput(chosen.Key, images, sidecar, Generated: false);
        var stems = byStem.Select(group =>
            $"{group.Key}=[{string.Join(",", group.Select(IOPath.GetFileName))}]").ToArray();
        Check("rating-perf 夹具识别", true,
            $"folder={folder} chosen={chosen.Key} "
            + $"images=[{string.Join(",", images.Select(IOPath.GetFileName))}] "
            + $"sidecar={(sidecar == null ? "无" : IOPath.GetFileName(sidecar))} stems=[{string.Join(" ", stems)}]");
        return input;
    }

    /// <summary>没有真机夹具时的兜底素材：一张 1200×800 JPEG（内嵌 XMP 星级 0）与同名 <c>.xmp</c>。</summary>
    /// <param name="activity">宿主 Activity。</param>
    /// <returns>合成夹具；写入失败返回 null。</returns>
    private static RatingPerfInput? GenerateRatingPerfFallbackInput(Activity activity)
    {
        try
        {
            string folder = IOPath.Combine(activity.FilesDir!.AbsolutePath, RatingPerfGeneratedFolderName);
            Directory.CreateDirectory(folder);
            string image = IOPath.Combine(folder, "fallback.jpg");
            string sidecar = IOPath.Combine(folder, "fallback.xmp");
            if (!File.Exists(image) || new FileInfo(image).Length == 0)
                WriteCheckJpeg(image, 1200, 800, rating: 0, xmpPadding: 1024);
            if (!File.Exists(sidecar) || new FileInfo(sidecar).Length == 0)
                File.WriteAllBytes(sidecar, BuildXmpPacket(0, 1024));
            return new RatingPerfInput("fallback", [image], sidecar, Generated: true);
        }
        catch (Exception exception)
        {
            Console.WriteLine($"[NativeCheck] 合成回退夹具失败：{exception.Message}");
            return null;
        }
    }

    /// <summary>
    /// 把选中的同一组输入复制成 <see cref="RatingPerfCopies"/> 个测试 basename（<c>rp-01</c>…）到验收
    /// Provider 的 writable 树；每次先清空该子目录。输入夹具只读，永不改动。
    /// </summary>
    /// <param name="input">选中的夹具。</param>
    /// <returns>成功复制的组数；0 表示失败（已记 FAIL）。</returns>
    private static int CopyRatingPerfFixtures(RatingPerfInput input)
    {
        string storeFolder = PathForWritable(RatingPerfStoreFolderName);
        try
        {
            if (Directory.Exists(storeFolder)) Directory.Delete(storeFolder, recursive: true);
            Directory.CreateDirectory(storeFolder);
        }
        catch (Exception exception)
        {
            Check("rating-perf 复制夹具到验收 Provider", false,
                $"重建 {storeFolder} 失败：{exception.GetType().Name}：{exception.Message}");
            return 0;
        }

        var failures = new List<string>();
        long bytes = 0;
        var sw = Stopwatch.StartNew();
        for (int copy = 1; copy <= RatingPerfCopies; copy++)
        {
            string basename = $"rp-{copy:00}";
            foreach (string source in input.Images)
            {
                string target = IOPath.Combine(storeFolder, basename + IOPath.GetExtension(source));
                try
                {
                    File.Copy(source, target, overwrite: true);
                    bytes += new FileInfo(target).Length;
                }
                catch (Exception exception)
                {
                    failures.Add($"{IOPath.GetFileName(target)}:{exception.GetType().Name}");
                }
            }

            if (input.Sidecar is not { Length: > 0 } sidecar) continue;
            string sidecarTarget = IOPath.Combine(storeFolder, basename + ".xmp");
            try
            {
                File.Copy(sidecar, sidecarTarget, overwrite: true);
                bytes += new FileInfo(sidecarTarget).Length;
            }
            catch (Exception exception)
            {
                failures.Add($"{IOPath.GetFileName(sidecarTarget)}:{exception.GetType().Name}");
            }
        }

        double ms = sw.Elapsed.TotalMilliseconds;
        var copied = Directory.Exists(storeFolder)
            ? Directory.GetFiles(storeFolder).Select(IOPath.GetFileName)
                .OrderBy(name => name, StringComparer.OrdinalIgnoreCase).ToArray()
            : Array.Empty<string>();
        Check("rating-perf 复制夹具到验收 Provider",
            failures.Count == 0 && copied.Length == RatingPerfCopies * (input.Images.Count + (input.Sidecar == null ? 0 : 1)),
            $"source={(input.Generated ? "合成回退" : input.Stem)} copies={RatingPerfCopies} "
            + $"copyMs={ms:F0} bytes={bytes}({RatingPerfRate(bytes, ms)}) files=[{string.Join(",", copied)}] "
            + $"failures=[{string.Join(",", failures)}]");
        return failures.Count == 0 ? RatingPerfCopies : 0;
    }

    // ── 分组与基线 ────────────────────────────────────────────────────────────

    /// <summary>
    /// 按共享规则（与 <c>MainViewModel</c> 取评分组完全同一套格式表与「同名合并」开关）重建分组，
    /// 顺带把每个文件的 <c>Group</c> 刷新成最新对象。
    /// </summary>
    /// <param name="main">共享主 VM。</param>
    /// <returns>当前文件夹的分组列表。</returns>
    private static IReadOnlyList<PhotoGroup> RatingPerfGroups(MainViewModel main)
    {
        var formats = main.Settings.FileFormats
            .Select(item => new PhotoFormatRule(item.Id, item.DisplayName, item.Extensions, item.IsEnabled))
            .ToArray();
        return PhotoGroupMap.Build(main.FolderVM.AllFiles, main.Settings.SameNameAsOnePhoto, formats).Groups;
    }

    /// <summary>把每个组统一写到基线星级，并逐来源确认收敛（一致评分基线，冲突素材也在此收敛）。</summary>
    /// <param name="targets">各组代表文件。</param>
    /// <param name="rating">基线星级。</param>
    /// <returns>全部组都收敛到基线且无冲突返回 true。</returns>
    private static async Task<bool> NormalizeRatingPerfGroupsAsync(IReadOnlyList<ImageFile> targets, int rating)
    {
        var main = _main!;
        bool ok = true;
        var detail = new List<string>();
        var sw = Stopwatch.StartNew();
        foreach (var target in targets)
        {
            var outcome = await main.SetRatingWithResultAsync(target, rating);
            var read = await ReadRatingPerfSourcesAsync(target, rating);
            bool converged = read.Converged && !target.HasRatingConflict;
            ok &= converged;
            detail.Add($"{target.Name}:{outcome}/{read.Ms:F0}ms[{read.Detail}]"
                + $" conflict={target.HasRatingConflict}");
        }

        double ms = sw.Elapsed.TotalMilliseconds;
        if (!ok && !string.IsNullOrWhiteSpace(main.LastRatingWriteError))
            detail.Add($"lastError={main.LastRatingWriteError}");
        Check("rating-perf 一致评分基线", ok,
            $"baseline={rating} groups={targets.Count} totalMs={ms:F0} {string.Join(" | ", detail)}");
        return ok;
    }

    // ── 采样 ──────────────────────────────────────────────────────────────────

    /// <summary>让指定照片成为当前照片（tap 入口必须先切图；业务入口不依赖它）。</summary>
    /// <param name="main">共享主 VM。</param>
    /// <param name="target">目标照片。</param>
    /// <returns>已成为当前照片返回 true。</returns>
    private static async Task<bool> SelectRatingPerfTargetAsync(MainViewModel main, ImageFile target)
    {
        if (ReferenceEquals(main.CurrentFile, target)) return true;
        main.FileVM.ThumbnailList.SelectImageCommand(target);
        try
        {
            await WaitAsync(() => ReferenceEquals(main.CurrentFile, target), $"{target.Name} 成为当前照片", 30000);
            return true;
        }
        catch (TimeoutException)
        {
            return false;
        }
    }

    /// <summary>
    /// 一次连续改星采样：从入口触发开始计时，等到 VM 收敛后逐来源读盘，并统计窗口内的通知与假冲突。
    /// </summary>
    /// <param name="main">共享主 VM。</param>
    /// <param name="target">目标照片（其 <c>Group</c> 为最新分组）。</param>
    /// <param name="rating">目标星级（必须与当前值不同，否则没有可测的写回）。</param>
    /// <param name="useTap">true 走真实触摸入口，false 走共享业务入口。</param>
    /// <returns>本次采样。</returns>
    private static async Task<RatingPerfSample> MeasureRatingPerfChangeAsync(
        MainViewModel main, ImageFile target, int rating, bool useTap)
    {
        var group = target.Group ?? throw new InvalidOperationException($"{target.Name} 没有评分组");
        var watched = group.Files.ToArray();
        int notifications = 0, conflictFlips = 0;
        void OnChanged(object? sender, PropertyChangedEventArgs args)
        {
            if (args.PropertyName is not (nameof(ImageFile.Rating)
                or nameof(ImageFile.RatingSources) or nameof(ImageFile.HasRatingConflict))) return;
            notifications++;
            if (sender is ImageFile { HasRatingConflict: true }) conflictFlips++;
        }

        foreach (var file in watched) file.PropertyChanged += OnChanged;
        string entry = useTap ? "tap" : "business";
        string outcome;
        var sw = Stopwatch.StartNew();
        try
        {
            if (useTap)
            {
                string? tap = await TapRatingPerfValueAsync(_root!, rating);
                if (tap == null)
                {
                    // 星级控件不可用（无当前照片/未排布）时退回业务入口，报告如实标注。
                    entry = "business(no-control)";
                    outcome = (await main.SetRatingWithResultAsync(target, rating))?.ToString() ?? "null";
                }
                else
                {
                    entry = $"tap({tap})";
                    try
                    {
                        await WaitAsync(() => target.Rating == rating && !target.HasRatingConflict,
                            $"tap 改星收敛：{target.Name} → {rating}", 20000);
                        outcome = main.LastRatingWriteOutcome?.ToString() ?? "tap";
                    }
                    catch (TimeoutException)
                    {
                        outcome = $"tap-timeout(rating={target.Rating},冲突={target.HasRatingConflict})";
                    }
                }
            }
            else
            {
                outcome = (await main.SetRatingWithResultAsync(target, rating))?.ToString() ?? "null";
            }
        }
        finally
        {
            foreach (var file in watched) file.PropertyChanged -= OnChanged;
        }

        double triggerMs = sw.Elapsed.TotalMilliseconds;
        bool verdict = target.Rating == rating && !target.HasRatingConflict;
        // 收尾窗口：迟到的后台刷新/库同步可能再发通知，假冲突也要落在这里面。
        await Task.Delay(RatingPerfSettleMs);
        bool conflictAfter = target.HasRatingConflict;
        var read = await ReadRatingPerfSourcesAsync(target, rating);
        return new RatingPerfSample(entry, rating, outcome, triggerMs, read.Ms, notifications,
            conflictFlips, conflictAfter, verdict, read.Detail, read.Converged);
    }

    /// <summary>
    /// 真实触摸改星：展开态点目标槽位；紧凑态起手打开星级弹窗并把落点交给弹窗星排再抬手
    /// （与 RATING-07 同一条消费路径）。控件不可用返回 null。
    /// </summary>
    /// <param name="root">主屏根视图。</param>
    /// <param name="value">目标星级 0–5。</param>
    /// <returns>入口形态文本；不可用为 null。</returns>
    private static async Task<string?> TapRatingPerfValueAsync(AndroidNativeRootView root, int value)
    {
        var candidates = new List<AndroidRatingControl>(2);
        if (root.Form != ScreenForm.Wide) candidates.Add(root.PhoneRating);
        candidates.Add(root.ControlBar.RatingControl);
        var control = candidates.FirstOrDefault(item => item.IsShown && item.Width > 0 && item.Height > 0);
        if (control == null) return null;

        var popup = root.RatingPopup;
        if (control.Form == RatingControlForm.Expanded)
        {
            var slot = control.SlotRectPx(value);
            if (slot.Width() <= 0 || slot.Height() <= 0) return null;
            TapLocal(control, slot.CenterX(), slot.CenterY());
            return "expanded";
        }

        using var entry = ScreenRectOf(control);
        long now = SystemClock.UptimeMillis();
        using (var down = MotionEvent.Obtain(now, now, MotionEventActions.Down,
                   entry.CenterX(), entry.CenterY(), 0)!)
            control.DispatchTouchEvent(down);
        try
        {
            using (var up = MotionEvent.Obtain(now, SystemClock.UptimeMillis(), MotionEventActions.Up,
                       entry.CenterX(), entry.CenterY(), 0)!)
                control.DispatchTouchEvent(up);
            await WaitAsync(() =>
            {
                using var rect = popup.SlotRectPx(0, value);
                return popup.IsOpen && rect.Width() > 0 && rect.Height() > 0;
            }, "紧凑星级弹窗槽位完成布局", 5000);
            using var slot = popup.SlotRectPx(0, value);
            TapLocal(popup.ContentViewForChecks!, slot.CenterX(), slot.CenterY());
            return "compact";
        }
        catch (TimeoutException)
        {
            if (popup.IsOpen) popup.Close();
            throw;
        }
    }

    /// <summary>逐来源重新读盘：每个成员的内嵌值，加上（若存在）同名 sidecar；侧车缺失不计入失败。</summary>
    /// <param name="target">目标照片。</param>
    /// <param name="expected">期望星级。</param>
    /// <returns>读盘结果。</returns>
    private static async Task<RatingPerfDiskRead> ReadRatingPerfSourcesAsync(ImageFile target, int expected)
    {
        var members = target.Group?.Files ?? new[] { target };
        var detail = new List<string>();
        bool converged = true;
        var sw = Stopwatch.StartNew();
        foreach (var member in members)
        {
            var snapshot = await XmpRatingReader.ReadAsync(member.File);
            bool ok = snapshot.State == XmpRatingState.Rated && snapshot.Value == expected;
            converged &= ok;
            detail.Add($"{member.Name}={DescribeRatingPerfSnapshot(snapshot)}");
        }

        var sidecar = await XmpRatingReader.ReadSidecarAsync(target.File);
        if (sidecar is { } sidecarSnapshot)
        {
            bool ok = sidecarSnapshot.State == XmpRatingState.Rated && sidecarSnapshot.Value == expected;
            converged &= ok;
            detail.Add($"{XmpRatingReader.SidecarName(target.Name)}={DescribeRatingPerfSnapshot(sidecarSnapshot)}");
        }

        return new RatingPerfDiskRead(converged, sw.Elapsed.TotalMilliseconds, string.Join(" ", detail));
    }

    /// <summary>评分快照写成 <c>3</c>/<c>Missing</c> 这类单值文本。</summary>
    private static string DescribeRatingPerfSnapshot(XmpRatingSnapshot snapshot)
        => snapshot.State == XmpRatingState.Rated
            ? (snapshot.Value ?? 0).ToString(CultureInfo.InvariantCulture)
            : snapshot.State.ToString();

    /// <summary>
    /// 读取吞吐基线：逐成员整文件顺序读（<c>OpenReadAsync</c>）与属性重查，再单独计时一次
    /// 同名 sidecar 的 <c>FindFileAsync</c>。只用于把「SAF 读慢」和「写回慢」分开，不设门槛。
    /// </summary>
    /// <param name="target">取样目标照片。</param>
    private static async Task ReadRatingPerfThroughputAsync(ImageFile target)
    {
        var members = target.Group?.Files ?? new[] { target };
        var detail = new List<string>();
        bool ok = true;
        foreach (var member in members)
        {
            var sw = Stopwatch.StartNew();
            byte[] bytes = await ReadAllBytesAsync(member.File);
            double readMs = sw.Elapsed.TotalMilliseconds;
            var propsSw = Stopwatch.StartNew();
            var props = await member.File.GetBasicPropertiesAsync();
            double propsMs = propsSw.Elapsed.TotalMilliseconds;
            bool sizeOk = props.Size == null || props.Size == (ulong)bytes.Length;
            ok &= sizeOk && bytes.Length > 0;
            detail.Add($"{member.Name}={bytes.Length}B read={readMs:F0}ms({RatingPerfRate(bytes.Length, readMs)})"
                + $" props={props.Size}:{propsMs:F0}ms");
        }

        var findSw = Stopwatch.StartNew();
        var parent = await target.File.GetParentAsync();
        var sidecarFile = parent == null
            ? null
            : await parent.FindFileAsync(XmpRatingReader.SidecarName(target.Name));
        detail.Add($"find({XmpRatingReader.SidecarName(target.Name)})="
            + $"{findSw.Elapsed.TotalMilliseconds:F0}ms found={sidecarFile != null}");
        Check("rating-perf 读取吞吐基线", ok, string.Join(" ", detail));
    }

    // ── 报告 ──────────────────────────────────────────────────────────────────

    /// <summary>把一组采样写成一条 PASS/FAIL：判定只看是否收敛，耗时逐条写在明细里。</summary>
    /// <param name="name">检查名。</param>
    /// <param name="samples">采样列表。</param>
    private static void AppendRatingPerfVerdict(string name, IReadOnlyList<RatingPerfSample> samples)
    {
        bool ok = samples.Count > 0 && samples.All(sample => sample.Converged && sample.Verdict);
        Check(name, ok, string.Join(" | ", samples.Select(DescribeRatingPerfSample)));
    }

    /// <summary>一条采样的可读明细：入口、目标值、入口→VM、读盘、通知与冲突、逐来源真值。</summary>
    /// <param name="sample">采样。</param>
    /// <returns>明细文本。</returns>
    private static string DescribeRatingPerfSample(RatingPerfSample sample)
        => $"{sample.Entry}→{sample.Requested}: trigger={sample.TriggerMs:F0}ms disk={sample.DiskMs:F0}ms "
            + $"notify={sample.Notifications} conflictFlips={sample.ConflictFlips} "
            + $"conflictAfter={sample.ConflictAfter} outcome={sample.Outcome} [{sample.Sources}]";

    /// <summary>字节吞吐（MB/s）；耗时为 0 时不给数值，避免除零造出假速度。</summary>
    /// <param name="bytes">字节数。</param>
    /// <param name="ms">耗时（ms）。</param>
    /// <returns>形如 <c>12.3MB/s</c> 的文本。</returns>
    private static string RatingPerfRate(long bytes, double ms)
        => ms <= 0.5
            ? "-"
            : (bytes / 1048576d / (ms / 1000d)).ToString("0.0", CultureInfo.InvariantCulture) + "MB/s";
}
