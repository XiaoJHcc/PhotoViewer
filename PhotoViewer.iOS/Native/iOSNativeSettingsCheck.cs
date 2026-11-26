using CoreGraphics;
using Foundation;
using System.Globalization;
using System.Text.RegularExpressions;
using PhotoViewer.Apple;
using PhotoViewer.Core.Input;
using PhotoViewer.Core.Platform;
using PhotoViewer.iOS.Core;
using PhotoViewer.ViewModels.Settings;
using UIKit;

namespace PhotoViewer.iOS.Native;

internal static class iOSNativeSettingsCheck
{
    private static string BindingFrameNote = string.Empty;
    private static double ControlRowHeight;

    internal static async Task RunAsync(iOSNativeRootViewController mainRoot, iOSNativeSettingsRootViewController root, List<string> lines)
    {
        var settings = root.Session.WorkingSettings;
        var snapshot = settings.CreateSnapshot();
        await Task.Delay(700);
        await VerifySheetPresentationAsync(root, lines);
        await SnapshotStageAsync("settings-root");
        await VerifyFilePageCacheLimitsAsync(mainRoot, root, settings, lines);
        await UiDispatcher.InvokeAsync(() => root.RowSelected(root.TableView, NSIndexPath.FromRowSection(1, 0)));
        await WaitForPage<iOSNativePreviewSettingsViewController>(root);
        await SnapshotStageAsync("settings-preview");
        await UiDispatcher.InvokeAsync(() =>
        {
            var page = (iOSNativePreviewSettingsViewController)root.NavigationController!.TopViewController!;
            VerifyNativeInsetGroupedPage(page, "预览页");
            page.TableView.LayoutIfNeeded();
            var labels = Descendants(page.TableView).OfType<UILabel>().Select(label => label.Text ?? string.Empty).ToArray();
            if (labels.Contains("缩放位置显示"))
                throw new InvalidOperationException("预览页仍显示“缩放位置显示”分组小标题");
            if (!labels.Contains("缩放时显示指示器"))
                throw new InvalidOperationException("预览页缺少缩放指示器开关");
            if (page.NumberOfSections(page.TableView) < 2)
                throw new InvalidOperationException("预览页分组数量不足");
            if (!string.IsNullOrEmpty(page.TitleForHeader(page.TableView, 0)))
                throw new InvalidOperationException("缩放指示器开关仍带分组小标题");
            if (page.TitleForHeader(page.TableView, 1) != "缩放比例预设")
                throw new InvalidOperationException("预览页缺少缩放比例预设分组标题");
        });
        lines.Add("PASS SETTINGS-07 预览页：原生 InsetGrouped，缩放指示器开关不带分组小标题，预设分组标题保留");
        await UiDispatcher.InvokeAsync(() =>
        {
            var page = (iOSNativePreviewSettingsViewController)root.NavigationController!.TopViewController!;
            if (page.NavigationItem.RightBarButtonItem == null)
                throw new InvalidOperationException("预览设置子页缺少导航栏关闭按钮");
            int presetSection = page.PresetSectionIndex;
            var section = page.PresetSection ?? throw new InvalidOperationException("预览页缺少原生缩放预设分组");
            if (presetSection < 0) throw new InvalidOperationException("预览页缩放预设分组索引无效");
            if ((int)page.TableView.NumberOfRowsInSection(presetSection) != settings.ScalePresets.Count + 1)
                throw new InvalidOperationException("缩放预设分组行数与预设数+新增行不符");

            page.TableView.ScrollToRow(NSIndexPath.FromRowSection(0, presetSection), UITableViewScrollPosition.Top, false);
            page.TableView.LayoutIfNeeded();
            var deletes = section.DeleteButtons;
            for (int index = 0; index < deletes.Count; index++)
            {
                if (deletes[index].Bounds.Width < 44 || deletes[index].Bounds.Height < 44)
                    throw new InvalidOperationException("删除按钮命中区小于44点");
            }
            if (page.GetTrailingSwipeActionsConfiguration(page.TableView, NSIndexPath.FromRowSection(0, presetSection)) == null)
                throw new InvalidOperationException("缩放预设行缺少原生左滑删除动作");

            var preset = settings.ScalePresets[0];
            var field = section.Fields[0];
            field.Text = "66.667";
            field.SendActionForControlEvents(UIControlEvent.EditingDidEnd);
            if (Math.Abs(preset.Value - .66667) > .000001 || !section.Fields.Contains(field))
                throw new InvalidOperationException("倍率提交重建了输入框或数值未写副本");
            field.Text = "";
            field.SendActionForControlEvents(UIControlEvent.EditingDidEnd);
            if (!section.HasValidationError || page.GetViewForFooter(page.TableView, presetSection) == null || field.Text != preset.Text)
                throw new InvalidOperationException("非法倍率未在系统分组脚注提示或未恢复原值");
            int count = settings.ScalePresets.Count;
            page.RowSelected(page.TableView, NSIndexPath.FromRowSection(section.AddRowIndex, presetSection));
            if (settings.ScalePresets.Count != count + 1 || !settings.ScalePresets.Any(value => value.Value == 1))
                throw new InvalidOperationException("点击「添加缩放预设」行未新增100%");
            while (settings.ScalePresets.Count > 1) section.DeleteButtons[0].SendActionForControlEvents(UIControlEvent.TouchUpInside);
            if (section.DeleteButtons.Single().Enabled) throw new InvalidOperationException("最后倍率项未禁删");
            if (page.GetTrailingSwipeActionsConfiguration(page.TableView, NSIndexPath.FromRowSection(0, presetSection)) != null)
                throw new InvalidOperationException("最后倍率项仍允许左滑删除");
            page.RowSelected(page.TableView, NSIndexPath.FromRowSection(section.AddRowIndex, presetSection));
            page.RowSelected(page.TableView, NSIndexPath.FromRowSection(section.AddRowIndex, presetSection));
        });
        await SnapshotStageAsync("settings-preview-presets");
        await VerifyKeyboardDismissAsync(root, lines);
        await UiDispatcher.InvokeAsync(() => root.NavigationController!.PopViewController(false));
        await WaitForPage<iOSNativeSettingsRootViewController>(root);
        lines.Add("PASS SETTINGS-07 原生缩放预设分组：独立 InsetGrouped 单元格 + 44点删除按钮/原生左滑删除 + 末行「添加缩放预设」+ 系统分组脚注校验错误、保留输入框身份、最后项禁删（非手指实测）");
        await UiDispatcher.InvokeAsync(() => root.RowSelected(root.TableView, NSIndexPath.FromRowSection(2, 0)));
        await WaitForPage<iOSNativeControlSettingsViewController>(root);
        var control = (iOSNativeControlSettingsViewController)root.NavigationController!.TopViewController!;
        bool phoneForm = control.PhoneForm;
        if (phoneForm)
        {
            await VerifyPhoneControlPageAsync(control, settings, lines);
        }
        else
        {
            await VerifyListPresentationAsync(control, settings, lines);
            await VerifyInlineBindingAsync(control, settings, lines);
            await VerifyPhoneFormRebuildAsync(control, settings, lines);
            await VerifyCaptureAsync(root, control, settings, lines);
        }
        await VerifySliderRowAsync(control, lines);
        if (phoneForm)
        {
            // 手机形态跳过了捕获层（不外设编辑入口），显式退回设置首页再进 EXIF。
            await UiDispatcher.InvokeAsync(() => root.NavigationController!.PopToViewController(root, false));
            await WaitForPage<iOSNativeSettingsRootViewController>(root);
        }
        await VerifyExifListMatchesControlRowHeightAsync(root, lines, phoneForm);
        await UiDispatcher.InvokeAsync(() => root.NavigationController!.PopToViewController(root, false));
        await VerifyAiSettingsPageAsync(root, settings, lines);
        await UiDispatcher.InvokeAsync(() =>
        {
            control.Capabilities.Inject(false, false);
            root.NavigationController!.PopToViewController(root, false);
            settings.ApplySnapshot(snapshot);
        });
    }

    /// <summary>核对设置页半屏 sheet 使用系统自动同心圆角、原生 PageSheet、固定深色与内容滚动守卫。</summary>
    private static async Task VerifySheetPresentationAsync(iOSNativeSettingsRootViewController root, List<string> lines)
    {
        await UiDispatcher.InvokeAsync(() =>
        {
            var navigation = root.NavigationController as iOSNativeSettingsNavigationController
                ?? throw new InvalidOperationException("设置页缺少原生导航控制器");
            if (navigation.ModalPresentationStyle != UIModalPresentationStyle.PageSheet)
                throw new InvalidOperationException($"设置页不是原生 PageSheet：{navigation.ModalPresentationStyle}");
            if (navigation.OverrideUserInterfaceStyle != UIUserInterfaceStyle.Dark)
                throw new InvalidOperationException("设置页未固定深色外观");
            var sheet = navigation.SheetPresentationController
                ?? throw new InvalidOperationException("设置页缺少 UISheetPresentationController");
            if (sheet.Detents is not { Length: 2 })
                throw new InvalidOperationException($"设置页 sheet 档位数异常：{sheet.Detents?.Length}");
            if (!sheet.PrefersGrabberVisible || sheet.PrefersScrollingExpandsWhenScrolledToEdge)
                throw new InvalidOperationException(
                    $"设置页 sheet 参数异常：grabber={sheet.PrefersGrabberVisible} expand={sheet.PrefersScrollingExpandsWhenScrolledToEdge}");
            if (sheet.PreferredCornerRadius != UISheetPresentationControllerDetent.AutomaticDimension)
                throw new InvalidOperationException($"设置页覆写了系统同心圆角：{sheet.PreferredCornerRadius}");
            var presentation = navigation.PresentationController
                ?? throw new InvalidOperationException("设置页缺少 PresentationController");
            var presentationDelegate = presentation.Delegate
                ?? throw new InvalidOperationException("设置页缺少关闭代理");
            if (!presentationDelegate.ShouldDismiss(presentation))
                throw new InvalidOperationException("设置页在非内容滚动状态下阻止了把手/外部关闭");

            // 1) 滚动视图已在顶部边界时再次下拉：允许关闭半屏。
            var table = root.TableView;
            var originalOffset = table.ContentOffset;
            nfloat topY = -table.AdjustedContentInset.Top;
            try
            {
                table.SetContentOffset(new CGPoint(0, topY), false);
                navigation.ScrollGuard.SimulateScrollPanForChecks(table, UIGestureRecognizerState.Began);
                if (!presentationDelegate.ShouldDismiss(presentation))
                    throw new InvalidOperationException("设置页内容已在顶部边界，再次下拉应允许关闭半屏");

                // 2) 滚动视图未到顶时起手向下滚动页面：本轮手势全程拦截误关闭（即使途中滑回顶部）。
                table.SetContentOffset(new CGPoint(0, topY + 40), false);
                navigation.ScrollGuard.SimulateScrollPanForChecks(table, UIGestureRecognizerState.Began);
                if (presentationDelegate.ShouldDismiss(presentation))
                    throw new InvalidOperationException("设置页内容未到顶起手滚动时未拦截误关闭");
                table.SetContentOffset(new CGPoint(0, topY), false);
                navigation.ScrollGuard.SimulateScrollPanForChecks(table, UIGestureRecognizerState.Changed);
                if (presentationDelegate.ShouldDismiss(presentation))
                    throw new InvalidOperationException("设置页同一次滚动手势滑回顶部时误触发关闭");
            }
            finally
            {
                navigation.ScrollGuard.ResetForChecks();
                table.SetContentOffset(originalOffset, false);
            }
            if (!presentationDelegate.ShouldDismiss(presentation))
                throw new InvalidOperationException("设置页滚动手势结束后未恢复下拉关闭能力");
        });
        lines.Add("PASS SETTINGS-03/04@ios 半屏 sheet：原生 PageSheet + 固定深色 + medium/large + 系统自动同心圆角（AutomaticDimension）+ 内容未到顶滚动不误关、到顶后再次下拉允许关闭");
    }

    private static void VerifyNativeInsetGroupedPage(iOSNativeSettingsFormViewController page, string label)
    {
        if (page.TableView.Style != UITableViewStyle.InsetGrouped)
            throw new InvalidOperationException($"{label} 未使用原生 InsetGrouped 列表：{page.TableView.Style}");
        if (page.TableView.ContentInsetAdjustmentBehavior != UIScrollViewContentInsetAdjustmentBehavior.Automatic)
            throw new InvalidOperationException($"{label} 未采用系统安全区自动 inset：{page.TableView.ContentInsetAdjustmentBehavior}");
    }

    /// <summary>
    /// EDITION-02/03：AI 设置页只多出「相似聚类」参数与两个独立的即时计算开关；
    /// 标准版只有「分析诊断 / 即时计算 CV」，不建相似分组或 DINO 开关。开关经真实 ValueChanged 写工作副本。
    /// </summary>
    private static async Task VerifyAiSettingsPageAsync(iOSNativeSettingsRootViewController root, SettingsViewModel settings, List<string> lines)
    {
        await UiDispatcher.InvokeAsync(() => root.RowSelected(root.TableView, NSIndexPath.FromRowSection(4, 0)));
        await WaitForPage<iOSNativeAiSettingsViewController>(root);
        await UiDispatcher.InvokeAsync(() =>
        {
            var page = (iOSNativeAiSettingsViewController)root.NavigationController!.TopViewController!;
            VerifyNativeInsetGroupedPage(page, "AI 设置页");
            page.TableView.LayoutIfNeeded();
            var headers = Enumerable.Range(0, (int)page.NumberOfSections(page.TableView))
                .Select(section => page.TitleForHeader(page.TableView, section) ?? string.Empty)
                .ToArray();
            var labels = Descendants(page.TableView).OfType<UILabel>().Select(label => label.Text ?? string.Empty).Concat(headers).ToArray();
            var switches = Descendants(page.TableView)
                .OfType<UISwitch>()
                .OrderBy(toggle => (double)toggle.ConvertPointToView(CGPoint.Empty, page.TableView).Y)
                .ToArray();
#if PHOTOVIEWER_DINO
            foreach (string expected in new[] { "相似聚类", "相似度阈值", "最多数量", "分析诊断", "即时计算 DINO", "即时计算 CV" })
                if (!labels.Contains(expected, StringComparer.Ordinal))
                    throw new InvalidOperationException($"AI 设置页缺少「{expected}」：{string.Join(" | ", labels)}");
            if (switches.Length != 2)
                throw new InvalidOperationException($"AI 设置页应为两个独立开关，实际 {switches.Length}");
            bool dino = settings.AnalysisImmediateCompute;
            switches[0].On = !dino;
            switches[0].SendActionForControlEvents(UIControlEvent.ValueChanged);
            if (settings.AnalysisImmediateCompute != !dino)
                throw new InvalidOperationException("即时计算 DINO 开关未写工作副本");
            settings.AnalysisImmediateCompute = dino;
            bool cv = settings.AnalysisImmediateCvCompute;
            switches[1].On = !cv;
            switches[1].SendActionForControlEvents(UIControlEvent.ValueChanged);
            if (settings.AnalysisImmediateCvCompute != !cv)
                throw new InvalidOperationException("即时计算 CV 开关未写工作副本");
            settings.AnalysisImmediateCvCompute = cv;
            lines.Add("PASS AI 设置页：原生 InsetGrouped，相似聚类参数 + 即时计算 DINO / CV 两个独立开关（真实 ValueChanged 写工作副本）");
#else
            if (labels.Contains("相似聚类", StringComparer.Ordinal) || labels.Contains("相似度阈值", StringComparer.Ordinal)
                || labels.Contains("最多数量", StringComparer.Ordinal) || labels.Contains("即时计算 DINO", StringComparer.Ordinal)
                || labels.Contains("即时计算 DINO/CV", StringComparer.Ordinal))
                throw new InvalidOperationException($"标准版设置页残留 DINO/相似聚类控件：{string.Join(" | ", labels)}");
            foreach (string expected in new[] { "分析诊断", "即时计算 CV" })
                if (!labels.Contains(expected, StringComparer.Ordinal))
                    throw new InvalidOperationException($"标准版设置页缺少「{expected}」：{string.Join(" | ", labels)}");
            if (switches.Length != 1)
                throw new InvalidOperationException($"标准版设置页应只有 CV 一个开关，实际 {switches.Length}");
            bool cv = settings.AnalysisImmediateCvCompute;
            switches[0].On = !cv;
            switches[0].SendActionForControlEvents(UIControlEvent.ValueChanged);
            if (settings.AnalysisImmediateCvCompute != !cv)
                throw new InvalidOperationException("标准版即时计算 CV 开关未写工作副本");
            settings.AnalysisImmediateCvCompute = cv;
            lines.Add("PASS 标准版设置页：原生 InsetGrouped，仅「分析诊断 / 即时计算 CV」一个开关，无相似分组或 DINO 开关");
#endif
        });
        await SnapshotStageAsync(iOSAppCapabilities.HasDino ? "settings-ai" : "settings-cv");
        await UiDispatcher.InvokeAsync(() => root.NavigationController!.PopToViewController(root, false));
    }

    /// <summary>
    /// SETTINGS-16：滑条行的标题、拉条与当前值排在同一行，窄宽下先压缩标题而不与数值叠压。
    /// </summary>
    private static async Task VerifySliderRowAsync(iOSNativeSettingsFormViewController page, List<string> lines)
    {
        var notes = new List<string>();
        await UiDispatcher.InvokeAsync(() =>
        {
            foreach (double width in new double[] { 320, 480, 768 })
            {
                var row = page.CreateSliderRowForChecks("缓存内存上限", "4096 MB");
                row.Frame = new CGRect(0, 0, (nfloat)width, 60);
                row.LayoutIfNeeded();

                var labels = Descendants(row).OfType<UILabel>().ToArray();
                var title = labels.First(label => label.Text == "缓存内存上限");
                var value = labels.First(label => label.Text == "4096 MB");
                var slider = Descendants(row).OfType<UISlider>().Single();
                var titleFrame = title.ConvertRectToView(title.Bounds, row);
                var sliderFrame = slider.ConvertRectToView(slider.Bounds, row);
                var valueFrame = value.ConvertRectToView(value.Bounds, row);
                if (Math.Abs((double)titleFrame.GetMidY() - (double)sliderFrame.GetMidY()) > 1
                    || Math.Abs((double)valueFrame.GetMidY() - (double)sliderFrame.GetMidY()) > 1)
                    throw new InvalidOperationException(
                        $"拉条行没有排在同一行：宽度 {width} title={titleFrame} slider={sliderFrame} value={valueFrame}");
                if (titleFrame.GetMaxX() > sliderFrame.X + .5 || sliderFrame.GetMaxX() > valueFrame.X + .5)
                    throw new InvalidOperationException(
                        $"拉条行顺序/间距异常：宽度 {width} title={titleFrame} slider={sliderFrame} value={valueFrame}");
                if (sliderFrame.Width < 87)
                    throw new InvalidOperationException($"窄宽下拉条被压没：宽度 {width} slider={sliderFrame}");
                if (Math.Abs((double)valueFrame.GetMaxX() - ((double)row.Bounds.Width - 16)) > 1)
                    throw new InvalidOperationException(
                        $"当前值未右对齐到行尾：宽度 {width} value={valueFrame} row={row.Bounds}");
                notes.Add($"{width:0}: 标题 {titleFrame.Width:0} / 拉条 {sliderFrame.Width:0} / 数值 {valueFrame.Width:0} 同一行且右对齐");
            }
        });
        lines.Add("PASS SETTINGS-16 滑条行：320/480/768 三个宽度下标题→拉条→当前值同行、拉条保有最小宽度、数值右对齐行尾（同一 CreateSliderRow 构建器，非页面截图实测）；" + string.Join("；", notes));
    }

    /// <summary>
    /// SETTINGS-08：EXIF 显示表与控制栏命令表使用一致的标准行高；只有命令表行带命令图标。
    /// </summary>
    private static async Task VerifyExifListMatchesControlRowHeightAsync(
        iOSNativeSettingsRootViewController root, List<string> lines, bool phoneForm)
    {
        await UiDispatcher.InvokeAsync(() => root.RowSelected(root.TableView, NSIndexPath.FromRowSection(3, 0)));
        await WaitForPage<iOSNativeExifSettingsViewController>(root);
        await UiDispatcher.InvokeAsync(() =>
        {
            var page = (iOSNativeExifSettingsViewController)root.NavigationController!.TopViewController!;
            VerifyNativeInsetGroupedPage(page, "EXIF 设置页");
            int section = page.ExifDisplaySectionIndex;
            if (section < 0) throw new InvalidOperationException("EXIF 设置页缺少 EXIF 显示分组");
            page.TableView.ScrollToRow(NSIndexPath.FromRowSection(0, section), UITableViewScrollPosition.Top, false);
            page.TableView.LayoutIfNeeded();
            var cell = page.TableView.CellAt(NSIndexPath.FromRowSection(0, section)) as iOSNativeCheckableSettingsCell
                ?? throw new InvalidOperationException("EXIF 显示表未生成真实单元格");
            if ((double)cell.Bounds.Height < iOSNativeSettingsMetrics.ListRowHeight - .5)
                throw new InvalidOperationException($"EXIF 显示行高低于标准最小行高：{cell.Bounds.Height}");
            if (!cell.LeadingIconView.Hidden)
                throw new InvalidOperationException("EXIF 显示行不应带命令图标");
            if (!page.CanMoveRow(page.TableView, NSIndexPath.FromRowSection(0, section)))
                throw new InvalidOperationException("EXIF 显示行未开启拖拽排序");
            var clamped = page.CustomizeMoveTarget(page.TableView, NSIndexPath.FromRowSection(0, section), NSIndexPath.FromRowSection(0, section + 1));
            if (clamped.Section != section)
                throw new InvalidOperationException($"EXIF 显示行跨分组拖拽未夹回原分组：{clamped.Section}/{section}");
            if (ControlRowHeight > 0 && Math.Abs((double)cell.Bounds.Height - ControlRowHeight) > .5)
                throw new InvalidOperationException($"EXIF 显示行高 {cell.Bounds.Height} 与控制栏命令表行高 {ControlRowHeight} 不一致");
        });
        lines.Add(phoneForm
            ? $"PASS SETTINGS-08/17 行高：EXIF 显示表为原生 InsetGrouped 自适应行高（>= {iOSNativeSettingsMetrics.ListRowHeight}，实测 {ControlRowHeight:0.##}）、不带命令图标、拖拽限在本组内"
            : $"PASS SETTINGS-08 行高一致：控制栏命令表与 EXIF 显示表同为原生 InsetGrouped 自适应行高（实测 {ControlRowHeight:0.##}），命令图标只出现在命令表行");
        await UiDispatcher.InvokeAsync(() => root.NavigationController!.PopViewController(false));
        await WaitForPage<iOSNativeSettingsRootViewController>(root);
    }

    /// <summary>
    /// 文件页缓存项：iOS 的缓存容量由实测预算自动管理，不出现「缓存内存上限 / 缓存数量上限」两项，
    /// 两条提示保留并改为按实测预算与适屏尺寸表述。
    /// </summary>
    private static async Task VerifyFilePageCacheLimitsAsync(
        iOSNativeRootViewController mainRoot,
        iOSNativeSettingsRootViewController root,
        SettingsViewModel settings,
        List<string> lines)
    {
        await UiDispatcher.InvokeAsync(() => root.RowSelected(root.TableView, NSIndexPath.FromRowSection(0, 0)));
        await WaitForPage<iOSNativeFileSettingsViewController>(root);
        await SnapshotStageAsync("settings-file");
        await UiDispatcher.InvokeAsync(() =>
        {
            var page = (iOSNativeFileSettingsViewController)root.NavigationController!.TopViewController!;
            VerifyNativeInsetGroupedPage(page, "文件设置页");
            if (settings.ShowsCacheLimits) throw new InvalidOperationException("iPad 缓存容量应由实测预算自动管理");
            if (settings.PreloadMaximum != 10) throw new InvalidOperationException($"自动口径下预载上限应为 10，实际 {settings.PreloadMaximum}");

            // 滚到底部让「缓存与预加载」分组及其原生 Section Footer 进入视图树。
            ScrollToBottom(page.TableView);
            page.TableView.LayoutIfNeeded();

            var labels = Descendants(page.TableView).OfType<UILabel>().ToArray();
            var texts = labels.Select(label => label.Text ?? string.Empty).ToArray();
            if (texts.Contains("缓存内存上限") || texts.Contains("缓存数量上限"))
                throw new InvalidOperationException("自动口径下仍显示缓存上限两项");

            // 能力提示按当前文本属性定位：第一行系统总内存/当前可用，第二行两档张数（均允许换行）
            var budgetLabel = labels.FirstOrDefault(label => label.Text == settings.PerformanceBudgetInfo)
                ?? throw new InvalidOperationException("文件页缺少系统内存提示");
            var countLabel = labels.FirstOrDefault(label => label.Text == settings.BitmapCacheCountInfo)
                ?? throw new InvalidOperationException("文件页缺少缓存张数提示");
            string budget = budgetLabel.Text ?? string.Empty;
            string count = countLabel.Text ?? string.Empty;

            if (budget.Contains("设备性能", StringComparison.Ordinal) || budget.Contains("核 / 内存", StringComparison.Ordinal)
                || budget.Contains("上次系统内存告警", StringComparison.Ordinal))
                throw new InvalidOperationException("系统内存提示仍是旧的运行时预算口径：" + budget);
            AssertCountWording(count, "默认设置");
            if (budget.Contains("超过", StringComparison.Ordinal) || WarningPattern.IsMatch(budget))
                throw new InvalidOperationException("自动管理缓存的平台不应追加用户上限警告：" + budget);

            if (budgetLabel.Lines != 0 || countLabel.Lines != 0)
                throw new InvalidOperationException($"能力提示未允许换行：{budgetLabel.Lines}/{countLabel.Lines}");
            if (!SameColor(budgetLabel.TextColor, UIColor.SecondaryLabel) || !SameColor(countLabel.TextColor, UIColor.SecondaryLabel))
                throw new InvalidOperationException("常规级别的能力提示未取次级色");
            if (settings.PerformanceBudgetInfoLevel != PerfInfoLevel.Normal
                || settings.BitmapCacheCountInfoLevel != PerfInfoLevel.Normal)
                throw new InvalidOperationException("自动管理缓存的平台不应出现内存告警级别");

            var info = new iOSMemoryBudgetProvider().GetSystemMemoryInfo();
            if (info.TotalBytes <= 0) throw new InvalidOperationException("未能读到系统总物理内存");
            if (!ContainsSize(budget, info.TotalBytes))
                throw new InvalidOperationException($"系统内存提示未给出总物理内存 {info.TotalBytes} 字节：{budget}");
            if (info.AvailableBytes < 0)
            {
                if (!budget.Contains("未知", StringComparison.Ordinal))
                    throw new InvalidOperationException("系统可用内存读取失败时应如实显示“未知”：" + budget);
            }
            else
            {
                if (info.AvailableBytes > info.TotalBytes)
                    throw new InvalidOperationException($"系统可用内存超过总内存：{info.AvailableBytes}/{info.TotalBytes}");
                if (!ContainsSize(budget, info.AvailableBytes) && CountSizes(budget) < 2)
                    throw new InvalidOperationException($"系统内存提示未给出系统可用内存 {info.AvailableBytes} 字节：{budget}");
            }
        });

        string countBefore = string.Empty;
        string countAfter = string.Empty;
        await UiDispatcher.InvokeAsync(() =>
        {
            countBefore = settings.BitmapCacheCountInfo;
            int forward = settings.PreloadForwardCount;
            int memory = settings.BitmapCacheMaxMemory;
            int count = settings.BitmapCacheMaxCount;
            try
            {
                settings.PreloadForwardCount = forward == 0 ? 1 : 0;
                settings.BitmapCacheMaxMemory = 512;
                settings.BitmapCacheMaxCount = 1;
                settings.BitmapCacheMaxMemory = 32768;
                settings.BitmapCacheMaxCount = 400;
            }
            finally
            {
                settings.PreloadForwardCount = forward;
                settings.BitmapCacheMaxMemory = memory;
                settings.BitmapCacheMaxCount = count;
            }
            countAfter = settings.BitmapCacheCountInfo;
        });
        await Task.Delay(150);
        await UiDispatcher.InvokeAsync(() =>
        {
            if (countAfter != countBefore)
                throw new InvalidOperationException($"缓存张数随隐藏的用户上限/预载变化：{countBefore} → {countAfter}");
            var page = (iOSNativeFileSettingsViewController)root.NavigationController!.TopViewController!;
            var labels = Descendants(page.TableView).OfType<UILabel>().Select(label => label.Text ?? string.Empty).ToArray();
            if (!labels.Contains(settings.BitmapCacheCountInfo))
                throw new InvalidOperationException("缓存张数提示未随属性变化刷新");
        });

        // SETTINGS-15 / 周期刷新回归：原生 InsetGrouped 格式分组可拖拽且限在本组内；周期通知不把长表单滚回顶部。
        double bottomBefore = 0;
        await UiDispatcher.InvokeAsync(() =>
        {
            var page = (iOSNativeFileSettingsViewController)root.NavigationController!.TopViewController!;
            int formatSection = page.FileFormatSectionIndex;
            if (formatSection < 0) throw new InvalidOperationException("文件页缺少文件格式分组");
            int rows = (int)page.TableView.NumberOfRowsInSection(formatSection);
            if (rows < 2) throw new InvalidOperationException($"文件格式表行数不足：{rows}");
            if (!page.CanMoveRow(page.TableView, NSIndexPath.FromRowSection(0, formatSection)))
                throw new InvalidOperationException("文件格式分组未开启拖拽排序");
            if (page.CanEditRow(page.TableView, NSIndexPath.FromRowSection(0, 0)))
                throw new InvalidOperationException("同名合并开关行不应进入编辑态");
            var clamped = page.CustomizeMoveTarget(page.TableView, NSIndexPath.FromRowSection(0, formatSection), NSIndexPath.FromRowSection(0, 0));
            if (clamped.Section != formatSection)
                throw new InvalidOperationException($"文件格式拖拽未限制在本组内：{clamped.Section}/{formatSection}");
            bottomBefore = ScrollToBottom(page.TableView);
            page.TableView.LayoutIfNeeded();
        });
        await Task.Delay(2600);
        UIButton? firstCommand = null;
        bool scrolled = false;
        await UiDispatcher.InvokeAsync(() =>
        {
            var page = (iOSNativeFileSettingsViewController)root.NavigationController!.TopViewController!;
            double minY = -(double)page.TableView.AdjustedContentInset.Top;
            scrolled = bottomBefore > minY + 1;
            if (scrolled && Math.Abs((double)page.TableView.ContentOffset.Y - bottomBefore) > 1.5)
                throw new InvalidOperationException($"周期刷新把长表单滚回顶部：offset={page.TableView.ContentOffset.Y} bottom={bottomBefore}");
            firstCommand = mainRoot.ControlBar.Commands.Values.FirstOrDefault();
            mainRoot.NotifySettingsProperty(nameof(SettingsViewModel.BitmapCacheCountInfo));
        });
        await Task.Delay(200);
        await UiDispatcher.InvokeAsync(() =>
        {
            var page = (iOSNativeFileSettingsViewController)root.NavigationController!.TopViewController!;
            if (scrolled && Math.Abs((double)page.TableView.ContentOffset.Y - bottomBefore) > 1.5)
                throw new InvalidOperationException($"缓存计数通知把长表单滚回顶部：offset={page.TableView.ContentOffset.Y} bottom={bottomBefore}");
            var current = mainRoot.ControlBar.Commands.Values.FirstOrDefault();
            if (firstCommand != null && !ReferenceEquals(current, firstCommand))
                throw new InvalidOperationException("缓存计数等诊断通知仍重建了控制栏按钮");
        });
        lines.Add(scrolled
            ? "PASS 周期刷新回归：原生 InsetGrouped 格式表可拖拽且限在本组内；2.6s 内存探测与缓存计数通知后，文件页滚动位置与控制栏按钮身份均保持"
            : "NOTE 周期刷新回归：当前窗口尺寸下文件页不足以滚动，只核对了控制栏按钮身份与格式表分组边界");

        await UiDispatcher.InvokeAsync(() => root.NavigationController!.PopViewController(false));
        await WaitForPage<iOSNativeSettingsRootViewController>(root);
        lines.Add("PASS SETTINGS-06@ios：文件页原生 InsetGrouped，由实测预算自动管理缓存，不显示缓存内存/数量两项；提示在系统分组脚注内正确刷新且不随隐藏上限/预载变化");
    }

    /// <summary>
    /// 软键盘收起：输入框获焦后经 Done 按钮或空白点击收起，提交后 1.5 秒内不得再次弹出键盘或抢回焦点。
    /// </summary>
    private static async Task VerifyKeyboardDismissAsync(iOSNativeSettingsRootViewController root, List<string> lines)
    {
        bool keyboardVisible = false;
        double keyboardHeight = 0;
        DateTime lastHide = DateTime.MinValue, lastShow = DateTime.MinValue;
        using var showObserver = UIKeyboard.Notifications.ObserveWillShow((_, args) =>
        {
            keyboardVisible = true;
            keyboardHeight = Math.Max(keyboardHeight, (double)args.FrameEnd.Height);
            lastShow = DateTime.UtcNow;
        });
        using var hideObserver = UIKeyboard.Notifications.ObserveWillHide((_, _) =>
        {
            keyboardVisible = false;
            lastHide = DateTime.UtcNow;
        });
        foreach (var path in new[] { "done", "blank" })
        {
            iOSScalePresetSection? section = null;
            await UiDispatcher.InvokeAsync(() =>
            {
                var page = (iOSNativePreviewSettingsViewController)root.NavigationController!.TopViewController!;
                section = page.PresetSection ?? throw new InvalidOperationException("预览页缺少原生缩放预设分组");
                if (!section.Fields[0].BecomeFirstResponder()) throw new InvalidOperationException("倍率输入框无法获焦");
            });
            await Task.Delay(700);
            if (path == "done") await SnapshotStageAsync("settings-preview-keyboard");
            await UiDispatcher.InvokeAsync(() =>
            {
                var field = section!.Fields[0];
                if (!field.IsFirstResponder) throw new InvalidOperationException("倍率输入框获焦后丢失焦点");
                field.Text = path == "done" ? "150" : "250";
                if (path == "done")
                {
                    var done = (field.InputAccessoryView as UIToolbar)?.Items?.LastOrDefault()
                        ?? throw new InvalidOperationException("倍率输入框缺少 Done");
                    if (done.Target is NSObject target && done.Action is { } action) target.PerformSelector(action, done, 0);
                    else throw new InvalidOperationException("Done 按钮没有动作");
                }
                else
                {
                    var page = (iOSNativePreviewSettingsViewController)root.NavigationController!.TopViewController!;
                    var tap = page.TableView.GestureRecognizers?.OfType<UITapGestureRecognizer>().SingleOrDefault(recognizer => recognizer.Delegate is iOSNonControlTapDelegate)
                        ?? throw new InvalidOperationException("页面缺少空白点击收键盘识别器");
                    if (tap.CancelsTouchesInView) throw new InvalidOperationException("空白点击识别器吞掉了控件点击");
                    page.View!.EndEditing(true);
                }
            });
            await Task.Delay(1500);
            await UiDispatcher.InvokeAsync(() =>
            {
                if (section!.Fields.Any(field => field.IsFirstResponder)) throw new InvalidOperationException($"{path} 收起后输入框重新获焦");
                if (keyboardVisible || lastShow > lastHide) throw new InvalidOperationException($"{path} 收起后键盘再次弹出");
                if (!root.Session.WorkingSettings.ScalePresets.Any(preset => Math.Abs(preset.Value - (path == "done" ? 1.5 : 2.5)) < 1e-9))
                    throw new InvalidOperationException($"{path} 收起未提交倍率");
            });
        }
        lines.Add($"PASS SETTINGS-07 键盘收起：Done 与空白点击收起后提交倍率，1.5s 后键盘保持收起、无焦点抢回（键盘区高度 {keyboardHeight:0}；空白点击为同一识别器动作注入，非手指实测）");
    }

    /// <summary>
    /// 手机形态控制页保留布局位置与控制栏功能；iPhone 的布局只有「手机」一项。
    /// 「控制栏功能」标题下为上下张开关卡片，紧接一张不带标题的完整可勾选排序命令表卡片：
    /// 手机形态不显示绑定编辑与键盘映射，已勾选但未生效的命令只淡化勾号；组合开关与勾选/排序后立即重算。
    /// </summary>
    private static async Task VerifyPhoneControlPageAsync(iOSNativeControlSettingsViewController page,
        SettingsViewModel settings, List<string> lines)
    {
        var originalDisplay = settings.Hotkeys.Select(item => (Item: item, item.IsDisplay)).ToArray();
        bool originalNavigation = settings.ShowNavigationButtons;
        await UiDispatcher.InvokeAsync(() =>
        {
            VerifyNativeInsetGroupedPage(page, "手机控制页");
            page.TableView.LayoutIfNeeded();
            if (page.HotkeySectionIndex < 0)
                throw new InvalidOperationException("手机形态未创建可勾选排序的命令表");
            if (page.KeyboardMappingSectionIndex >= 0)
                throw new InvalidOperationException("手机形态仍创建键盘映射分组");
            if (page.TitleForHeader(page.TableView, page.LayoutModeSectionIndex) != "边栏布局位置")
                throw new InvalidOperationException("手机形态缺少「边栏布局位置」分组");
            if (page.TitleForHeader(page.TableView, page.NavigationSectionIndex) != "控制栏功能")
                throw new InvalidOperationException("手机形态缺少「控制栏功能」分组");
            if (Descendants(page.TableView).OfType<UISwitch>().Any())
                throw new InvalidOperationException("上下张应使用勾选行，而不是系统开关控件");

            bool phoneDevice = iOSNativeControlSettingsViewController.IsPhoneDevice;
            int layoutSection = page.LayoutModeSectionIndex;
            nint expectedModes = phoneDevice ? 1 : settings.LayoutModes.Count;
            if (page.TableView.NumberOfRowsInSection(layoutSection) != expectedModes)
                throw new InvalidOperationException($"布局模式数量错误：{page.TableView.NumberOfRowsInSection(layoutSection)}，应为 {expectedModes}");
            if (phoneDevice)
            {
                page.TableView.ScrollToRow(NSIndexPath.FromRowSection(0, layoutSection), UITableViewScrollPosition.Top, false);
                page.TableView.LayoutIfNeeded();
                var modeCell = page.TableView.CellAt(NSIndexPath.FromRowSection(0, layoutSection))
                    ?? throw new InvalidOperationException("iPhone 未生成唯一的「手机」布局行");
                if (!Descendants(modeCell).OfType<UILabel>().Any(label => label.Text == "手机")
                    || modeCell.Accessory != UITableViewCellAccessory.Checkmark)
                    throw new InvalidOperationException(
                        $"iPhone 布局位置不是单选并勾选的「手机」：accessory={modeCell.Accessory}");
            }

            VerifyNavigationRow(page, settings);
            VerifySplitCards(page, settings);

            int hotkeySection = page.HotkeySectionIndex;
            page.TableView.ScrollToRow(NSIndexPath.FromRowSection(0, hotkeySection), UITableViewScrollPosition.Top, false);
            page.TableView.LayoutIfNeeded();
            foreach (var cell in HotkeyVisibleCells(page))
            {
                if (cell.TrailingAccessory != null || Descendants(cell).OfType<UIButton>().Any(button => !button.Hidden))
                    throw new InvalidOperationException("手机形态命令表仍显示快捷键绑定按钮");
                if (!cell.ShowsReorderControl)
                    throw new InvalidOperationException($"手机形态命令行 {cell.TitleLabel.Text} 缺少拖拽排序把手");
                if (string.IsNullOrEmpty(cell.AccessibilityValue))
                    throw new InvalidOperationException($"手机形态命令行 {cell.TitleLabel.Text} 读屏值未区分勾选与实际显示");
            }

            var candidates = settings.Hotkeys
                .Where(item => item.Command is not ("Previous" or "Next"))
                .Take(4).ToArray();
            if (candidates.Length < 4)
                throw new InvalidOperationException("命令目录不足以核对手机形态的未生效勾号");
            foreach (var candidate in candidates) candidate.IsDisplay = true;
            page.RefreshHotkeyRows();
            var inactiveIndex = NSIndexPath.FromRowSection(settings.Hotkeys.IndexOf(candidates[3]), hotkeySection);
            page.TableView.ScrollToRow(inactiveIndex, UITableViewScrollPosition.Middle, false);
            page.TableView.LayoutIfNeeded();
            var inactiveCell = page.TableView.CellAt(inactiveIndex) as iOSNativeCheckableSettingsCell
                ?? throw new InvalidOperationException("未生成未生效命令的真实单元格");
            var expectedTint = UIColor.FromWhiteAlpha((nfloat)1, (nfloat)PhotoViewer.Core.Design.DesignTokens.SettingsPhoneInactiveCheckOpacity);
            if (inactiveCell.CheckmarkView.Hidden || inactiveCell.CheckmarkActive)
                throw new InvalidOperationException("已勾选但未生效的命令勾号未标记为淡化态");
            if (!(inactiveCell.CheckmarkView.TintColor?.IsEqual(expectedTint) ?? false))
                throw new InvalidOperationException($"未生效勾号不是浅灰白低不透明度：{inactiveCell.CheckmarkView.TintColor}");
            if (inactiveCell.AccessibilityValue?.Contains("未在手机控制栏显示", StringComparison.Ordinal) != true)
                throw new InvalidOperationException($"未生效命令读屏值未说明实际显示状态：{inactiveCell.AccessibilityValue}");

            var previous = settings.Hotkeys.First(item => item.Command == "Previous");
            previous.IsDisplay = true;
            settings.ShowNavigationButtons = true;
            page.RefreshHotkeyRows();
            var previousIndex = NSIndexPath.FromRowSection(settings.Hotkeys.IndexOf(previous), hotkeySection);
            page.TableView.ScrollToRow(previousIndex, UITableViewScrollPosition.Middle, false);
            page.TableView.LayoutIfNeeded();
            var previousCell = page.TableView.CellAt(previousIndex) as iOSNativeCheckableSettingsCell
                ?? throw new InvalidOperationException("未生成上一张命令的真实单元格");
            if (previousCell.CheckmarkActive)
                throw new InvalidOperationException("组合开关开启时独立上一张仍标记为生效");
            page.RowSelected(page.TableView, NSIndexPath.FromRowSection(0, page.NavigationSectionIndex));
            if (settings.ShowNavigationButtons)
                throw new InvalidOperationException("点选组合开关行未关闭显示上下张");
            page.TableView.ScrollToRow(previousIndex, UITableViewScrollPosition.Middle, false);
            page.TableView.LayoutIfNeeded();
            previousCell = page.TableView.CellAt(previousIndex) as iOSNativeCheckableSettingsCell
                ?? throw new InvalidOperationException("组合开关关闭后上一张命令单元格消失");
            if (!previousCell.CheckmarkActive || !(previousCell.CheckmarkView.TintColor?.IsEqual(UIColor.SystemBlue) ?? false))
                throw new InvalidOperationException("组合开关关闭后独立上一张未即时进入生效集合");
            settings.ShowNavigationButtons = true;
            page.RefreshHotkeyRows();

            var firstCandidateIndex = NSIndexPath.FromRowSection(settings.Hotkeys.IndexOf(candidates[0]), hotkeySection);
            bool displayed = candidates[0].IsDisplay;
            page.RowSelected(page.TableView, firstCandidateIndex);
            if (candidates[0].IsDisplay == displayed)
                throw new InvalidOperationException("点行未切换命令勾选");
            page.RowSelected(page.TableView, firstCandidateIndex);
            if (candidates[0].IsDisplay != displayed) throw new InvalidOperationException("恢复命令勾选失败");
            var second = settings.Hotkeys[1];
            page.MoveRow(page.TableView, NSIndexPath.FromRowSection(1, hotkeySection), NSIndexPath.FromRowSection(0, hotkeySection));
            if (!ReferenceEquals(settings.Hotkeys[0], second)) throw new InvalidOperationException("拖拽排序未写工作副本");
            page.MoveRow(page.TableView, NSIndexPath.FromRowSection(1, hotkeySection), NSIndexPath.FromRowSection(0, hotkeySection));
        });
        await UiDispatcher.InvokeAsync(() =>
        {
            foreach (var (item, display) in originalDisplay) item.IsDisplay = display;
            settings.ShowNavigationButtons = originalNavigation;
            page.RefreshHotkeyRows();
        });
        lines.Add("PASS SETTINGS-08/17 手机形态控制页：原生 InsetGrouped，保留边栏布局位置（" +
            (iOSNativeControlSettingsViewController.IsPhoneDevice ? "仅单选「手机」" : "保留全部选项以便切回") +
            "），唯一「控制栏功能」标题下上下张开关与命令表为两张独立分组卡片、无第二个标题；未生效勾号浅灰白低不透明度并说明读屏状态，" +
            "组合开关/勾选/排序即时重算，快捷键编辑与键盘映射不显示");
    }

    /// <summary>
    /// SETTINGS-08/17：上下张组合开关与命令表各占一张原生 InsetGrouped 分组卡片，共用唯一的「控制栏功能」标题。
    /// </summary>
    private static void VerifySplitCards(iOSNativeControlSettingsViewController page, SettingsViewModel settings)
    {
        int navigationSection = page.NavigationSectionIndex;
        int hotkeySection = page.HotkeySectionIndex;
        if (navigationSection < 0 || hotkeySection != navigationSection + 1)
            throw new InvalidOperationException($"上下张与命令表未作为相邻原生分组排列：{navigationSection}/{hotkeySection}");
        if (page.TitleForHeader(page.TableView, navigationSection) != "控制栏功能")
            throw new InvalidOperationException("第一张分组卡片标题不是「控制栏功能」");
        if (page.TableView.NumberOfRowsInSection(navigationSection) != 1)
            throw new InvalidOperationException("上下张分组卡片混入了其他行");
        if (!string.IsNullOrEmpty(page.TitleForHeader(page.TableView, hotkeySection)))
            throw new InvalidOperationException("命令表分组卡片保留了第二个标题");
        if ((int)page.TableView.NumberOfRowsInSection(hotkeySection) != settings.Hotkeys.Count)
            throw new InvalidOperationException($"命令表分组行数与命令目录不符：{page.TableView.NumberOfRowsInSection(hotkeySection)}/{settings.Hotkeys.Count}");
        var clampedUp = page.CustomizeMoveTarget(page.TableView, NSIndexPath.FromRowSection(0, hotkeySection), NSIndexPath.FromRowSection(0, navigationSection));
        if (clampedUp.Section != hotkeySection)
            throw new InvalidOperationException($"命令表拖拽未夹回原分组：{clampedUp.Section}/{hotkeySection}");
    }

    /// <summary>核对上一张/下一张在「控制栏功能」标题下的原生单行样式。</summary>
    private static void VerifyNavigationRow(iOSNativeControlSettingsViewController page, SettingsViewModel settings)
    {
        int section = page.NavigationSectionIndex;
        if (section < 0) throw new InvalidOperationException("控制栏功能缺少上一张/下一张分组");
        if (iOSNativeControlSettingsViewController.NavigationIconSymbol != "chevron.left.chevron.right")
            throw new InvalidOperationException("上一张/下一张未使用独立的双向箭头 SF Symbol");
        var indexPath = NSIndexPath.FromRowSection(0, section);
        page.TableView.ScrollToRow(indexPath, UITableViewScrollPosition.Middle, false);
        page.TableView.LayoutIfNeeded();
        var cell = page.TableView.CellAt(indexPath) as iOSNativeCheckableSettingsCell
            ?? throw new InvalidOperationException("未生成上一张/下一张真实列表行");
        if (cell.TitleLabel.Text != "上一张/下一张" || cell.LeadingIconView.Hidden || cell.LeadingIconView.Image == null
            || cell.CheckmarkView.Hidden != !settings.ShowNavigationButtons)
            throw new InvalidOperationException("上一张/下一张行未保持文字、图标与蓝色勾选状态");
        if (cell.CheckmarkView.TintColor?.IsEqual(UIColor.SystemBlue) != true)
            throw new InvalidOperationException("上一张/下一张勾选不是系统蓝色");
        if (cell.ShowsReorderControl || page.CanMoveRow(page.TableView, indexPath))
            throw new InvalidOperationException("上一张/下一张单项分组仍显示或允许拖拽排序");
        if ((double)cell.Bounds.Height < iOSNativeSettingsMetrics.ListRowHeight - .5)
            throw new InvalidOperationException($"上一张/下一张行高低于标准最小行高：{cell.Bounds.Height}");
        ControlRowHeight = (double)cell.Bounds.Height;
    }

    /// <summary>
    /// 宽形态切到「手机」后，键盘映射整组隐藏，命令表保留勾选与排序但去掉绑定编辑；
    /// 切回宽形态后键盘映射与原配置恢复。
    /// </summary>
    private static async Task VerifyPhoneFormRebuildAsync(iOSNativeControlSettingsViewController page,
        SettingsViewModel settings, List<string> lines)
    {
        var mode = settings.LayoutMode;
        await UiDispatcher.InvokeAsync(() => settings.LayoutMode = PhotoViewer.Core.Settings.LayoutMode.Phone);
        await Task.Delay(150);
        await UiDispatcher.InvokeAsync(() =>
        {
            page.TableView.LayoutIfNeeded();
            if (page.HotkeySectionIndex < 0)
                throw new InvalidOperationException("选了「手机」布局后丢失命令勾选排序表");
            if (page.KeyboardMappingSectionIndex >= 0)
                throw new InvalidOperationException("选了「手机」布局后仍保留键盘映射");
            if (page.NavigationSectionIndex < 0)
                throw new InvalidOperationException("选了「手机」布局后缺少上下张组合开关");
            VerifySplitCards(page, settings);
            page.TableView.ScrollToRow(NSIndexPath.FromRowSection(0, page.HotkeySectionIndex), UITableViewScrollPosition.Top, false);
            page.TableView.LayoutIfNeeded();
            foreach (var cell in HotkeyVisibleCells(page))
                if (cell.TrailingAccessory != null)
                    throw new InvalidOperationException("手机布局的命令行仍显示快捷键绑定按钮");
        });
        await UiDispatcher.InvokeAsync(() => settings.LayoutMode = mode);
        await Task.Delay(150);
        await UiDispatcher.InvokeAsync(() =>
        {
            if (page.HotkeySectionIndex < 0)
                throw new InvalidOperationException("切回宽形态后控制页未恢复命令表分组");
            VerifyNavigationRow(page, settings);
            VerifySplitCards(page, settings);
        });
        lines.Add("PASS SETTINGS-08 形态切换：选「手机」后键盘映射隐藏，唯一「控制栏功能」标题下仍为上下张与命令表两张原生分组卡片，命令表保留勾选排序但去掉绑定编辑；切回宽形态原配置恢复");
    }

    /// <summary>控制栏命令表：纯触控只留勾选/名称/排序，外设连接后同一行就地出现主/次绑定按钮。</summary>
    private static async Task VerifyListPresentationAsync(iOSNativeControlSettingsViewController page, SettingsViewModel settings, List<string> lines)
    {
        await UiDispatcher.InvokeAsync(() =>
        {
            VerifyNativeInsetGroupedPage(page, "宽形态控制页");
            page.TableView.LayoutIfNeeded();
            if (page.TitleForHeader(page.TableView, page.LayoutModeSectionIndex) != "边栏布局位置")
                throw new InvalidOperationException("控制页布局分组标题不是“边栏布局位置”");
            VerifyNavigationRow(page, settings);
            VerifySplitCards(page, settings);
            int layoutSection = page.LayoutModeSectionIndex;
            page.TableView.ScrollToRow(NSIndexPath.FromRowSection(0, layoutSection), UITableViewScrollPosition.Top, false);
            page.TableView.LayoutIfNeeded();
            int checkedCount = 0;
            for (int row = 0; row < settings.LayoutModes.Count; row++)
            {
                var cell = page.TableView.CellAt(NSIndexPath.FromRowSection(row, layoutSection))
                    ?? throw new InvalidOperationException($"布局模式第 {row} 行未生成");
                if (cell.Bounds.Height < 44) throw new InvalidOperationException("布局模式行高小于44");
                if (cell.Accessory == UITableViewCellAccessory.Checkmark) checkedCount++;
            }
            if (checkedCount != 1)
                throw new InvalidOperationException($"布局模式不是单选勾选：{checkedCount}");
        });
        var item = settings.Hotkeys[0];
        var draft = item.PrimaryHotkey;
        double scrollOffset = 0;
        iOSNativeCheckableSettingsCell? stableCell = null;
        await UiDispatcher.InvokeAsync(() =>
        {
            settings.Hotkeys[1].PrimaryHotkey = draft;
            settings.CheckHotkeyConflicts();
            page.TableView.ScrollToRow(NSIndexPath.FromRowSection(0, page.HotkeySectionIndex), UITableViewScrollPosition.Top, false);
            page.TableView.LayoutIfNeeded();
            scrollOffset = (double)page.TableView.ContentOffset.Y;
        });
        foreach (var mode in new (bool Keyboard, bool Mouse, string Stage)[] { (false, false, "control-touch"), (true, false, "control-keyboard"), (false, true, "control-mouse"), (false, false, "control-touch-restored") })
        {
            await UiDispatcher.InvokeAsync(() =>
            {
                page.Capabilities.Inject(mode.Keyboard, mode.Mouse);
                page.TableView.LayoutIfNeeded();
                bool external = mode.Keyboard || mode.Mouse;
                var cell = page.TableView.CellAt(NSIndexPath.FromRowSection(0, page.HotkeySectionIndex)) as iOSNativeCheckableSettingsCell
                    ?? throw new InvalidOperationException("未生成真实快捷键行");
                if (cell.Window == null || stableCell != null && !ReferenceEquals(stableCell, cell))
                    throw new InvalidOperationException("能力变化重建了行或行未在真实视图树内");
                stableCell = cell;
                cell.LayoutIfNeeded();
                if (cell.TitleLabel.Text != item.Name || !cell.ShowsReorderControl || cell.CheckmarkView.Hidden == item.IsDisplay)
                    throw new InvalidOperationException("行未保留名称/勾选/排序");
                if ((double)cell.Bounds.Height < iOSNativeSettingsMetrics.ListRowHeight - .5
                    || (ControlRowHeight > 0 && Math.Abs((double)cell.Bounds.Height - ControlRowHeight) > .5))
                    throw new InvalidOperationException($"命令表行高 {cell.Bounds.Height} 与上一张/下一张行高 {ControlRowHeight} 不一致");
                VerifyCommandIcon(cell, item);
                if (cell.EditingAccessory != UITableViewCellAccessory.None)
                    throw new InvalidOperationException("行尾不应再有 ⓘ 详情入口");
                if (!external)
                {
                    if (cell.TrailingAccessory != null || Descendants(cell).OfType<UIButton>().Any(button => !button.Hidden))
                        throw new InvalidOperationException("纯触控仍显示绑定按钮");
                    if (!string.IsNullOrEmpty(cell.AccessibilityValue))
                        throw new InvalidOperationException("纯触控仍有无障碍绑定值");
                    if (Math.Abs((double)cell.TitleLabel.Frame.GetMidY() - (double)cell.ContentView.Bounds.Height / 2) > 1)
                        throw new InvalidOperationException("纯触控标题未垂直居中");
                }
                else
                {
                    VerifyBindingButtons(page, cell, item);
                }
                if ((page.KeyboardMappingSectionIndex >= 0) != external)
                    throw new InvalidOperationException($"键盘映射分组显隐未同步外设能力：index={page.KeyboardMappingSectionIndex} external={external}");
                if (external)
                {
                    var mappingRows = page.KeyboardMappingRows
                        ?? throw new InvalidOperationException("外设连接时缺少键盘映射行");
                    var pickers = mappingRows.SelectMany(Descendants).OfType<UIButton>().Where(button => button.ShowsMenuAsPrimaryAction).ToArray();
                    if (pickers.Length != 3) throw new InvalidOperationException($"键盘映射弹出按钮数量异常：{pickers.Length}");
                    foreach (var picker in pickers)
                    {
                        var row = Ancestor<UITableViewCell>(picker) ?? throw new InvalidOperationException("弹出按钮不在单元格内");
                        row.Frame = new CGRect(0, 0, Math.Max((nfloat)320, page.TableView.Bounds.Width - 32), 44);
                        row.LayoutIfNeeded();
                        var frame = picker.ConvertRectToView(picker.Bounds, row.ContentView);
                        var label = picker.Superview!.Subviews.OfType<UILabel>().First();
                        var labelFrame = label.ConvertRectToView(label.Bounds, row.ContentView);
                        string? title = picker.CurrentTitle ?? picker.Configuration?.Title;
                        if (!picker.ChangesSelectionAsPrimaryAction || picker.Configuration?.Indicator != UIButtonConfigurationIndicator.Popup
                            || string.IsNullOrEmpty(title) || row.ContentView.Bounds.Height < 44
                            || frame.GetMaxX() > row.ContentView.Bounds.Width + .5 || frame.IntersectsWith(labelFrame)
                            || Math.Abs((double)frame.GetMaxX() - ((double)row.ContentView.Bounds.Width - 16)) > 1)
                            throw new InvalidOperationException($"键盘映射行不是标准弹出行：title={title} frame={frame} label={labelFrame} row={row.ContentView.Bounds}");
                    }
                }
                if (!ReferenceEquals(item.PrimaryHotkey, draft) || !item.HasPrimaryConflict)
                    throw new InvalidOperationException("能力变化丢失绑定草稿/冲突状态");
                if (Math.Abs((double)page.TableView.ContentOffset.Y - scrollOffset) > .5)
                    throw new InvalidOperationException($"能力变化改变了滚动位置：{page.TableView.ContentOffset.Y}/{scrollOffset}");
            });
            await SnapshotStageAsync(mode.Stage);
        }
        await UiDispatcher.InvokeAsync(() =>
        {
            int hotkeySection = page.HotkeySectionIndex;
            bool displayed = item.IsDisplay;
            page.RowSelected(page.TableView, NSIndexPath.FromRowSection(0, hotkeySection));
            if (item.IsDisplay == displayed) throw new InvalidOperationException("点行未切换控制栏显示");
            page.RowSelected(page.TableView, NSIndexPath.FromRowSection(0, hotkeySection));
            var second = settings.Hotkeys[1];
            page.MoveRow(page.TableView, NSIndexPath.FromRowSection(1, hotkeySection), NSIndexPath.FromRowSection(0, hotkeySection));
            if (!ReferenceEquals(settings.Hotkeys[0], second)) throw new InvalidOperationException("拖拽排序未写工作副本");
            page.MoveRow(page.TableView, NSIndexPath.FromRowSection(1, hotkeySection), NSIndexPath.FromRowSection(0, hotkeySection));
        });
        lines.Add("NOTE 行内绑定按钮几何：" + BindingFrameNote);
        lines.Add($"PASS INPUT-13 控制栏命令表：原生 InsetGrouped，唯一「控制栏功能」标题下上下张与命令表为相邻独立分组、纯触控仅勾选/命令图标/名称/排序、标题居中、标准行高 {iOSNativeSettingsMetrics.ListRowHeight}；外设连接后同一cell就地出现两个108×28主/次绑定按钮且滚动位置与草稿不变");
    }

    private static IEnumerable<iOSNativeCheckableSettingsCell> HotkeyVisibleCells(iOSNativeControlSettingsViewController page)
    {
        int section = page.HotkeySectionIndex;
        foreach (var cell in page.TableView.VisibleCells)
        {
            if (cell is iOSNativeCheckableSettingsCell checkable
                && page.TableView.IndexPathForCell(cell) is { } indexPath
                && indexPath.Section == section)
            {
                yield return checkable;
            }
        }
    }

    /// <summary>命令表行的命令图标：与命令目录/控制栏按钮同一 SF Symbols，几何不压名称。</summary>
    private static void VerifyCommandIcon(iOSNativeCheckableSettingsCell cell, SettingsViewModel.HotkeyItem item)
    {
        var icon = cell.LeadingIconView;
        if (icon.Hidden || icon.Image is not { } image)
            throw new InvalidOperationException($"命令表行缺少命令图标：{item.Command}");
        icon.LayoutIfNeeded();
        var frame = icon.ConvertRectToView(icon.Bounds, cell);
        var title = cell.TitleLabel.ConvertRectToView(cell.TitleLabel.Bounds, cell);
        var checkmark = cell.CheckmarkView.ConvertRectToView(cell.CheckmarkView.Bounds, cell);
        if (Math.Abs((double)icon.Bounds.Width - PhotoViewer.Core.Design.DesignTokens.SettingsMobileCommandIconSlotSize) > .5
            || Math.Abs((double)icon.Bounds.Height - PhotoViewer.Core.Design.DesignTokens.SettingsMobileCommandIconSlotSize) > .5)
            throw new InvalidOperationException($"命令图标槽位尺寸异常：bounds={icon.Bounds} cellFrame={frame}");
        if (icon.ContentMode != UIViewContentMode.Center
            || (double)image.Size.Width > (double)icon.Bounds.Width
            || (double)image.Size.Height > (double)icon.Bounds.Height)
            throw new InvalidOperationException($"命令图标未按原尺寸在槽内居中：{item.Command} image={image.Size} bounds={icon.Bounds}");
        if (Math.Abs((double)frame.GetMidY() - (double)cell.Bounds.Height / 2) > .5)
            throw new InvalidOperationException($"命令图标未在行内垂直居中：{frame} 行高 {cell.Bounds.Height}");
        if (frame.IntersectsWith(title) || frame.IntersectsWith(checkmark) || frame.GetMaxX() > title.X + .5)
            throw new InvalidOperationException($"命令图标与勾选/命令名重叠：icon={frame} check={checkmark} title={title}");
        if (Math.Abs((double)(frame.X - checkmark.GetMaxX()) - 8) > .5)
            throw new InvalidOperationException($"勾选到命令图标间距不是 8：{frame.X - checkmark.GetMaxX()}");
        if (Math.Abs((double)(title.X - frame.GetMaxX()) - 8) > .5)
            throw new InvalidOperationException($"命令图标到命令名间距不是 8：{title.X - frame.GetMaxX()}");
    }

    /// <summary>校验行内主/次绑定按钮的尺寸、位置、标题与无障碍信息（外设连接态）。</summary>
    private static void VerifyBindingButtons(iOSNativeControlSettingsViewController page, iOSNativeCheckableSettingsCell cell, SettingsViewModel.HotkeyItem item)
    {
        var buttons = cell.TrailingAccessory as iOSHotkeyBindingButtons
            ?? throw new InvalidOperationException("外设连接后未在行内显示绑定按钮");
        var reorder = Descendants(cell).FirstOrDefault(view => view is UIControl and not UIButton && !view.Hidden);
        var reorderFrame = reorder?.ConvertRectToView(reorder.Bounds, cell) ?? CGRect.Empty;
        var titleFrame = cell.TitleLabel.ConvertRectToView(cell.TitleLabel.Bounds, cell);
        var contentFrame = cell.ContentView.ConvertRectToView(cell.ContentView.Bounds, cell);
        var frames = new List<CGRect>();
        foreach (var (button, secondary) in new[] { (buttons.PrimaryButton, false), (buttons.SecondaryButton, true) })
        {
            var frame = button.ConvertRectToView(button.Bounds, cell);
            frames.Add(frame);
            if (Math.Abs((double)frame.Width - 108) > .5 || Math.Abs((double)frame.Height - 28) > .5)
                throw new InvalidOperationException($"绑定按钮尺寸不是108×28：{frame}");
            if (Math.Abs((double)frame.GetMidY() - (double)cell.Bounds.Height / 2) > .5)
                throw new InvalidOperationException($"绑定按钮未在行内垂直居中：{frame} 行高 {cell.Bounds.Height}");
            if (frame.IntersectsWith(titleFrame))
                throw new InvalidOperationException($"绑定按钮与命令名重叠：{frame} title={titleFrame}");
            if (reorder != null && frame.IntersectsWith(reorderFrame))
                throw new InvalidOperationException($"绑定按钮与拖拽手柄重叠：{frame} reorder={reorderFrame}");
            if (frame.X < contentFrame.X - .5 || frame.GetMaxX() > contentFrame.GetMaxX() + .5)
                throw new InvalidOperationException($"绑定按钮超出内容区：{frame} content={contentFrame}");
            string expectedTitle = page.Recorder.Display(secondary ? item.SecondaryHotkey : item.PrimaryHotkey);
            var gesture = secondary ? item.SecondaryHotkey : item.PrimaryHotkey;
            bool conflict = gesture != null && (secondary ? item.HasSecondaryConflict : item.HasPrimaryConflict);
            string expectedValue = conflict ? expectedTitle + "，冲突" : expectedTitle;
            if (TitleOf(button) != expectedTitle)
                throw new InvalidOperationException($"绑定按钮标题不是当前手势：{TitleOf(button)}/{expectedTitle}");
            if (button.AccessibilityValue != expectedValue || !button.IsAccessibilityElement)
                throw new InvalidOperationException($"绑定按钮读屏信息不完整：{button.AccessibilityValue}/{expectedValue}");
        }
        if (frames[1].X < frames[0].GetMaxX())
            throw new InvalidOperationException($"主/次按钮相互重叠：{frames[0]} {frames[1]}");
        BindingFrameNote = $"主 {frames[0]} 次 {frames[1]} 拖拽 {reorderFrame} 名称 {titleFrame} 内容 {contentFrame}";
    }

    /// <summary>行内绑定按钮：点按钮录制、键盘提交、Esc 取消、Delete 清空、菜单清空、冲突橙色、纯触控隐藏。</summary>
    private static async Task VerifyInlineBindingAsync(iOSNativeControlSettingsViewController page, SettingsViewModel settings, List<string> lines)
    {
        var item = settings.Hotkeys[0];
        var other = settings.Hotkeys[1];
        var originalPrimary = item.PrimaryHotkey;
        var originalSecondary = item.SecondaryHotkey;
        var recorder = page.Recorder;
        int hotkeySection = page.HotkeySectionIndex;
        await UiDispatcher.InvokeAsync(() =>
        {
            page.Capabilities.Inject(true, false);
            page.TableView.ScrollToRow(NSIndexPath.FromRowSection(0, hotkeySection), UITableViewScrollPosition.Top, false);
            page.TableView.LayoutIfNeeded();
            var cell = page.TableView.CellAt(NSIndexPath.FromRowSection(0, hotkeySection)) as iOSNativeCheckableSettingsCell
                ?? throw new InvalidOperationException("未生成真实快捷键行");
            var buttons = cell.TrailingAccessory as iOSHotkeyBindingButtons
                ?? throw new InvalidOperationException("键盘连接后未在行内显示绑定按钮");
            var primary = buttons.PrimaryButton;

            primary.SendActionForControlEvents(UIControlEvent.TouchUpInside);
            if (!recorder.IsRecordingSlot(item, false))
                throw new InvalidOperationException("点主绑定按钮未进入录制态");
            if (!page.IsFirstResponder) throw new InvalidOperationException("录制未让控制页成为按键响应者");
            if (!buttons.IsTinted(false) || TitleOf(primary) != "按下按键…")
                throw new InvalidOperationException($"录制槽位未显示按下按键…：{TitleOf(primary)}");
            if (buttons.IsTinted(true)) throw new InvalidOperationException("非录制槽位也换成了录制配色");
        });
        await SnapshotStageAsync("control-recording");
        await UiDispatcher.InvokeAsync(() =>
        {
            var cell = page.TableView.CellAt(NSIndexPath.FromRowSection(0, hotkeySection)) as iOSNativeCheckableSettingsCell;
            var buttons = (iOSHotkeyBindingButtons)cell!.TrailingAccessory!;
            var primary = buttons.PrimaryButton;
            recorder.CaptureKey(8, AppleInputMapping.CommandMask);
            var expected = AppleInputMapping.FromHidUsage(8, AppleInputMapping.CommandMask, settings);
            if (recorder.IsRecording || item.PrimaryHotkey?.Key?.Key != expected?.Key?.Key
                || item.PrimaryHotkey?.Key?.Modifiers != expected?.Key?.Modifiers)
                throw new InvalidOperationException("键盘录制未复用AppleInputMapping");
            if (TitleOf(primary) != recorder.Display(item.PrimaryHotkey) || buttons.IsTinted(false))
                throw new InvalidOperationException($"提交后行内按钮未刷新：{TitleOf(primary)}");

            other.PrimaryHotkey = item.PrimaryHotkey;
            settings.CheckHotkeyConflicts();
            page.RefreshHotkeyRows();
            if (!item.HasPrimaryConflict || !other.HasPrimaryConflict)
                throw new InvalidOperationException("冲突未同时标记双方");
            if (!(TitleColorOf(primary)?.Equals(UIColor.SystemOrange) ?? false)
                || primary.AccessibilityValue?.Contains("冲突", StringComparison.Ordinal) != true)
                throw new InvalidOperationException($"冲突槽位未以橙色标出：{TitleColorOf(primary)} {primary.AccessibilityValue}");

            var before = item.PrimaryHotkey;
            primary.SendActionForControlEvents(UIControlEvent.TouchUpInside);
            recorder.CaptureKey(41, 0);
            if (recorder.IsRecording || !ReferenceEquals(item.PrimaryHotkey, before))
                throw new InvalidOperationException("Esc改写了绑定或未退出录制");

            primary.SendActionForControlEvents(UIControlEvent.TouchUpInside);
            recorder.CaptureKey(42, 0);
            if (item.PrimaryHotkey != null || item.HasPrimaryConflict || other.HasPrimaryConflict)
                throw new InvalidOperationException("Delete未清空或未重算冲突");
            if (TitleOf(primary) != "未设置")
                throw new InvalidOperationException($"清空后按钮未显示未设置：{TitleOf(primary)}");

            item.SecondaryHotkey = before;
            settings.CheckHotkeyConflicts();
            page.RefreshHotkeyRows();
            var secondary = buttons.SecondaryButton;
            var menu = secondary.Menu ?? throw new InvalidOperationException("绑定按钮缺少清空菜单");
            var actions = menu.Children.OfType<UIAction>().ToArray();
            if (actions.Length != 1 || actions[0].Title != "清空"
                || (actions[0].Attributes & UIMenuElementAttributes.Destructive) == 0)
                throw new InvalidOperationException("清空菜单不是单条破坏性动作");
            if (secondary.ShowsMenuAsPrimaryAction)
                throw new InvalidOperationException("清空菜单抢占了按钮主操作");
            buttons.ClearAction(true);
            if (item.SecondaryHotkey != null || TitleOf(secondary) != "未设置")
                throw new InvalidOperationException("菜单清空未清掉次绑定");
            if (secondary.Menu != null)
                throw new InvalidOperationException("未设置空槽仍保留了清空菜单");

            bool displayed = item.IsDisplay;
            page.RowSelected(page.TableView, NSIndexPath.FromRowSection(0, hotkeySection));
            if (item.IsDisplay == displayed)
                throw new InvalidOperationException("行内按钮存在时点行未切换勾选");
            page.RowSelected(page.TableView, NSIndexPath.FromRowSection(0, hotkeySection));

            primary.SendActionForControlEvents(UIControlEvent.TouchUpInside);
            if (!recorder.IsRecording) throw new InvalidOperationException("断开前未进入录制态");
            page.Capabilities.Inject(false, false);
            if (recorder.IsRecording || recorder.CaptureSurface != null)
                throw new InvalidOperationException("最后外设断开未取消录制");
            var touchCell = page.TableView.CellAt(NSIndexPath.FromRowSection(0, hotkeySection)) as iOSNativeCheckableSettingsCell;
            if (touchCell?.TrailingAccessory != null || Descendants(touchCell!).OfType<UIButton>().Any(button => !button.Hidden))
                throw new InvalidOperationException("断开外设后行内仍显示绑定按钮");
            if (!(iOSHotkeyRecorder.KeyLabel(GestureKey.D1) == "1" && iOSHotkeyRecorder.KeyLabel(GestureKey.NumPad1) == "数字 1"
                && iOSHotkeyRecorder.KeyLabel(GestureKey.Oem1) == ";" && iOSHotkeyRecorder.KeyLabel(GestureKey.Oem5) == "\\"
                && iOSHotkeyRecorder.KeyLabel(GestureKey.Return) == "↩" && iOSHotkeyRecorder.KeyLabel(GestureKey.Delete) == "⌦"
                && iOSHotkeyRecorder.KeyLabel(GestureKey.Space) == "空格"))
                throw new InvalidOperationException("数字/符号/编辑键仍显示存储枚举名");

            item.PrimaryHotkey = originalPrimary;
            item.SecondaryHotkey = originalSecondary;
            settings.CheckHotkeyConflicts();
            page.RefreshHotkeyRows();
        });
        lines.Add("PASS INPUT-13 行内绑定：点按钮录制（tinted+按下按键…并成为按键响应者）、键盘⌘提交走AppleInputMapping、Esc取消、Delete清空并重算冲突、长按菜单单条破坏性「清空」清掉次绑定、冲突橙色+读屏冲突、按钮不抢行勾选；断开最后外设取消录制并隐藏按钮；系统能力测试缝注入，非硬件连接实测");
        lines.Add("PASS 快捷键可读键名：数字/数字键盘/Oem别名/编辑键转键帽文本，非第二张命令表");
    }

    /// <summary>录制捕获层：阻断底层控件/导航，主键按左键、右/中键与滚轮写映射并留层；直接触控取消录制。</summary>
    private static async Task VerifyCaptureAsync(iOSNativeSettingsRootViewController root, iOSNativeControlSettingsViewController page,
        SettingsViewModel settings, List<string> lines)
    {
        var item = settings.Hotkeys[0];
        var recorder = page.Recorder;
        int hotkeySection = page.HotkeySectionIndex;
        await UiDispatcher.InvokeAsync(() =>
        {
            page.Capabilities.Inject(false, true);
            page.TableView.ScrollToRow(NSIndexPath.FromRowSection(0, hotkeySection), UITableViewScrollPosition.Top, false);
            page.TableView.LayoutIfNeeded();
        });
        foreach (nint button in new nint[] { 0, 1, 2 })
        {
            await UiDispatcher.InvokeAsync(() =>
            {
                page.BeginRecording(item, false);
                var surface = recorder.CaptureSurface ?? throw new InvalidOperationException("录制没有透明捕获层");
                var target = page.TableView.CellAt(NSIndexPath.FromRowSection(0, hotkeySection)) as iOSNativeCheckableSettingsCell
                    ?? throw new InvalidOperationException("未找到快捷键行");
                var point = target.ConvertPointToView(new CGPoint(target.Bounds.Width / 2, target.Bounds.Height / 2), surface.Window);
                if (!ReferenceEquals(surface.Window!.HitTest(point, null), surface)
                    || !ReferenceEquals(surface.Window.HitTest(new CGPoint(surface.Bounds.Width / 2, 30), null), surface))
                    throw new InvalidOperationException("录制期间底层设置控件或导航仍能命中");
                if (!page.IsFirstResponder || !surface.AccessibilityViewIsModal)
                    throw new InvalidOperationException("录制未保持非文本原生响应者或AX未隔离");
                surface.PointerDown(button, AppleInputMapping.CommandMask);
                int eventType = button == 0 ? AppleInputMapping.LeftMouseDown : AppleInputMapping.OtherMouseDown;
                var expected = AppleInputMapping.FromRaw(eventType, 0, AppleInputMapping.CommandMask, button, 0, false, null, settings);
                if (item.PrimaryHotkey?.Mouse?.Action != expected?.Mouse?.Action
                    || item.PrimaryHotkey?.Mouse?.Modifiers != expected?.Mouse?.Modifiers)
                    throw new InvalidOperationException($"捕获层鼠标按钮 {button} 没有沿同一Apple映射写工作副本");
                if (!ReferenceEquals(surface.Window.HitTest(point, null), surface))
                    throw new InvalidOperationException("鼠标Down提交后捕获层提前消失");
            });
            await UiDispatcher.InvokeAsync(() => recorder.CaptureSurface!.PointerUp());
            await Task.Delay(60);
            await UiDispatcher.InvokeAsync(() =>
            {
                if (recorder.CaptureSurface != null) throw new InvalidOperationException("鼠标Up后捕获层未释放");
            });
        }
        await UiDispatcher.InvokeAsync(() =>
        {
            page.BeginRecording(item, true);
            recorder.CaptureMouse(0, 1);
            if (recorder.CaptureSurface == null || item.SecondaryHotkey?.Mouse?.Action != MouseAction.WheelUp)
                throw new InvalidOperationException("滚轮未写绑定或未保留捕获层吞同轮事件");
        });
        await Task.Delay(280);
        await UiDispatcher.InvokeAsync(() =>
        {
            if (recorder.CaptureSurface != null) throw new InvalidOperationException("滚轮捕获层空闲后未释放");

            page.BeginRecording(item, false);
            var surface = recorder.CaptureSurface ?? throw new InvalidOperationException("直接触控前没有捕获层");
            var before = item.PrimaryHotkey;
            surface.CancelFromDirectTouch();
            if (recorder.IsRecording || recorder.CaptureSurface != null || !ReferenceEquals(item.PrimaryHotkey, before))
                throw new InvalidOperationException("直接触控未取消录制或改写了绑定");
            var cell = page.TableView.CellAt(NSIndexPath.FromRowSection(0, hotkeySection)) as iOSNativeCheckableSettingsCell
                ?? throw new InvalidOperationException("未找到快捷键行");
            var window = page.View?.Window ?? throw new InvalidOperationException("控制页不在窗口内");
            var point = cell.ConvertPointToView(new CGPoint(cell.Bounds.Width / 2, cell.Bounds.Height / 2), window);
            if (ReferenceEquals(window.HitTest(point, null), surface))
                throw new InvalidOperationException("取消后捕获层仍拦截触摸");
            bool displayed = item.IsDisplay;
            page.RowSelected(page.TableView, NSIndexPath.FromRowSection(0, hotkeySection));
            if (item.IsDisplay == displayed) throw new InvalidOperationException("取消录制后点行未切换勾选");
            page.RowSelected(page.TableView, NSIndexPath.FromRowSection(0, hotkeySection));
        });
        await UiDispatcher.InvokeAsync(() =>
        {
            page.BeginRecording(item, false);
            root.NavigationController!.PopViewController(false);
        });
        await WaitForPage<iOSNativeSettingsRootViewController>(root);
        await UiDispatcher.InvokeAsync(() =>
        {
            if (recorder.IsRecording || recorder.CaptureSurface != null) throw new InvalidOperationException("离开控制页未取消录制");
        });
        lines.Add("PASS INPUT-08 捕获层：实际Window.HitTest阻断控制页控件/导航，主键按左键、右/中键Down写共享映射且留层吞Up，Up后释放；滚轮留层吞事件后空闲释放；离页取消录制；非系统鼠标注入");
        lines.Add("PASS INPUT-14 直接触控取消：录制中直接触控捕获层 → IsRecording=false、原绑定不变、CaptureSurface=null，随后点行勾选立即生效（直接触控为同一入口注入，非手指实测）");
    }

    private static double ScrollToBottom(UIScrollView scroll)
    {
        scroll.LayoutIfNeeded();
        double minY = -(double)scroll.AdjustedContentInset.Top;
        double bottom = Math.Max(minY, (double)scroll.ContentSize.Height - (double)scroll.Bounds.Height + (double)scroll.AdjustedContentInset.Bottom);
        scroll.SetContentOffset(new CGPoint(0, bottom), false);
        return bottom;
    }

    internal static async Task SnapshotStageAsync(string stage)
    {
        if (!NSProcessInfo.ProcessInfo.Arguments.Contains("--pv-native-ui-snapshots", StringComparer.Ordinal)) return;
        string directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "PhotoViewer");
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(Path.Combine(directory, "native-ui-snapshot-stage.txt"), stage);
        await Task.Delay(2200);
    }

    private static async Task<T> WaitForPage<T>(iOSNativeSettingsRootViewController root) where T : UIViewController
    {
        for (int attempt = 0; attempt < 120; attempt++)
        {
            var page = await UiDispatcher.InvokeAsync(() =>
            {
                if (root.NavigationController?.TopViewController is not T top) return null;
                top.LoadViewIfNeeded();
                return top.ViewIfLoaded?.Window != null && root.NavigationController.GetTransitionCoordinator() == null ? top : null;
            });
            if (page != null) { await Task.Delay(400); return page; }
            await Task.Delay(50);
        }
        throw new InvalidOperationException($"设置导航未到达{typeof(T).Name}，实际{root.NavigationController?.TopViewController?.GetType().Name}");
    }

    private static T? Ancestor<T>(UIView view) where T : UIView
    {
        for (var parent = view.Superview; parent != null; parent = parent.Superview)
            if (parent is T match) return match;
        return null;
    }

    private static string TitleOf(UIButton button)
        => button.Configuration?.AttributedTitle?.Value ?? button.Configuration?.Title ?? string.Empty;

    private static UIColor? TitleColorOf(UIButton button)
    {
        if (button.Configuration?.AttributedTitle is { Length: > 0 } title
            && title.GetAttribute(UIStringAttributeKey.ForegroundColor, 0, out _) is UIColor color)
        {
            return color;
        }
        return button.Configuration?.BaseForegroundColor;
    }

    private static IEnumerable<UIView> Descendants(UIView root)
    {
        foreach (var child in root.Subviews)
        {
            yield return child;
            foreach (var nested in Descendants(child)) yield return nested;
        }
    }

    private static bool ContainsSize(string text, long bytes)
    {
        if (bytes <= 0) return false;
        foreach (Match match in SizePattern.Matches(text))
        {
            double value = double.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
            double factor = match.Groups[2].Value.ToUpperInvariant() switch
            {
                "TB" or "TIB" => 1024d * 1024 * 1024 * 1024,
                "GB" or "GIB" => 1024d * 1024 * 1024,
                "MB" or "MIB" => 1024d * 1024,
                "KB" or "KIB" => 1024d,
                _ => 1d,
            };
            if (Math.Abs(value * factor - bytes) <= Math.Max(bytes * 0.1, 512d * 1024 * 1024)) return true;
        }
        return false;
    }

    private static readonly Regex SizePattern =
        new(@"([0-9]+(?:\.[0-9]+)?)\s*(TIB|GIB|MIB|KIB|TB|GB|MB|KB|B)", RegexOptions.IgnoreCase);

    private static int CountSizes(string text) => SizePattern.Matches(text).Count;

    private static void AssertCountWording(string text, string stage)
    {
        foreach (var banned in new[] { "缓存能力", "最大", "约" })
            if (text.Contains(banned, StringComparison.Ordinal))
                throw new InvalidOperationException($"{stage}：张数文案仍含「{banned}」：{text}");
        ParseCounts(text, stage);
    }

    private static (long? Screen, long? Full) ParseCounts(string text, string stage)
        => (ParseCountSlot(text, ScreenCountPattern, ScreenUnknownPattern, "适屏照片", stage),
            ParseCountSlot(text, FullCountPattern, FullUnknownPattern, "3300 万像素照片", stage));

    private static long? ParseCountSlot(string text, Regex countPattern, Regex unknownPattern, string slot, string stage)
    {
        if (countPattern.Match(text) is { Success: true } match && long.TryParse(match.Groups[1].Value, out var value))
            return value;
        if (unknownPattern.IsMatch(text)) return null;
        throw new InvalidOperationException($"{stage}：{slot}张数缺失或口径不符：{text}");
    }

    private static readonly Regex ScreenCountPattern = new(@"可缓存\s*([0-9]+)\s*张适屏照片");
    private static readonly Regex ScreenUnknownPattern = new(@"适屏照片[：:]\s*未知");
    private static readonly Regex FullCountPattern = new(@"([0-9]+)\s*张\s*3300\s*万像素照片");
    private static readonly Regex FullUnknownPattern = new(@"3300\s*万像素照片[：:]\s*未知");
    private static readonly Regex WarningPattern = new(@"（当前设置超过(系统总内存|应用内存|系统可用内存|设备缓存)上限）");

    private static bool SameColor(UIColor? actual, UIColor expected)
        => actual != null && (ReferenceEquals(actual, expected) || actual.Equals(expected));
}
