using System;
using System.Threading.Tasks;
using Android.Views;
using PhotoViewer.Shared;
using PhotoViewer.ViewModels.Main;

namespace PhotoViewer.Android.Native;

/// <summary>
/// 栏位开关矩阵（验收专用）：逐个开关文件栏/控制栏/上下张/评分/手机 EXIF/分析栏与 AI 相似面板，
/// 在真实视图上核对显隐、槽位释放、剩余元素位置与主图适应区是否跟随共享几何；每个组合结束后恢复原状态。
/// </summary>
internal static partial class AndroidNativeChecks
{
    /// <summary>等待一次布局落定：开关只改共享状态，视图显隐与槽位矩形在下一帧布局后才生效。</summary>
    /// <param name="view">根视图。</param>
    private static async Task WaitLayoutAsync(View view)
    {
        for (int attempt = 0; attempt < 40; attempt++)
        {
            if (!view.IsLayoutRequested)
            {
                await Task.Delay(80);
                if (!view.IsLayoutRequested) return;
            }
            await Task.Delay(50);
        }
    }

    /// <summary>按当前形态跑一轮开关矩阵；全部断言基于真实视图状态与共享槽位结果。</summary>
    /// <param name="root">主屏根视图。</param>
    /// <param name="main">共享主 VM。</param>
    private static async Task RunToggleMatrixAsync(AndroidNativeRootView root, MainViewModel main)
    {
        bool phone = root.Form != ScreenForm.Wide;
        bool film = main.IsThumbnailViewVisible;
        bool control = main.IsControlViewVisible;
        bool exif = main.IsExifViewVisible;
        bool analysis = main.IsAnalysisViewVisible;
        bool rating = main.Settings.ShowRating;
        bool navigation = main.Settings.ShowNavigationButtons;
        try
        {
            Phase("开关矩阵");
            // 全关：栏位各自释放，主图适应区不被压到零。
            main.IsThumbnailViewVisible = false;
            main.IsControlViewVisible = false;
            main.IsExifViewVisible = false;
            main.IsAnalysisViewVisible = false;
            await WaitLayoutAsync(root);
            if (phone)
            {
                var slots = root.PhoneSlots;
                Check("toggles 手机全关：胶片/上下张/评分/增强/EXIF 全部收起",
                    root.Filmstrip.Visibility != global::Android.Views.ViewStates.Visible
                    && root.PhoneNavigation.Visibility != global::Android.Views.ViewStates.Visible
                    && root.PhoneRating.Visibility != global::Android.Views.ViewStates.Visible
                    && root.PhoneFitEnhance.Visibility != global::Android.Views.ViewStates.Visible
                    && root.PhoneExif.Visibility != global::Android.Views.ViewStates.Visible
                    && !Has(slots.Filmstrip) && !Has(slots.Navigation) && !Has(slots.Rating)
                    && !Has(slots.FitEnhance) && !Has(slots.Exif),
                    $"film={slots.Filmstrip} nav={slots.Navigation} rating={slots.Rating} exif={slots.Exif}");
                Check("toggles 手机全关：筛选/更多仍常驻",
                    Has(slots.Filter) && Has(slots.More) && root.FilterBar.Visibility == global::Android.Views.ViewStates.Visible,
                    $"filter={slots.Filter} more={slots.More} bar={root.FilterBar.Visibility}");
                Check("toggles 手机全关：主图适应区合法",
                    root.FitArea.Width > 0 && root.FitArea.Height > 0
                    && Near(root.FitArea.X, root.FitInsets.Left) && Near(root.FitArea.Y, root.FitInsets.Top),
                    $"fit={root.FitArea} insets={root.FitInsets}");
            }
            else
            {
                // 行布局的文件栏背景包含顶部安全带，隐藏时内容区归零、控件不绘制；控制栏背景则为零。
                Check("toggles 宽形态全关：文件栏内容与控制栏分区归零",
                    !Has(root.Panes.FileContent) && !Has(root.Panes.ControlBackground) && Has(root.Panes.Photo),
                    $"file={root.Panes.FileBar} content={root.Panes.FileContent} control={root.Panes.ControlBackground} photo={root.Panes.Photo}");
                Check("toggles 宽形态全关：主图适应区=分区主图",
                    SameFrame(root.FitArea, root.Panes.Photo), $"fit={root.FitArea} photo={root.Panes.Photo}");
            }

            // 独立胶片栏：只恢复文件栏，控制栏仍收起。
            main.IsThumbnailViewVisible = true;
            await WaitLayoutAsync(root);
            Check("toggles 独立胶片栏", root.Filmstrip.Visibility == global::Android.Views.ViewStates.Visible
                    && (!phone || Has(root.PhoneSlots.Filmstrip))
                    && (phone || Has(root.Panes.FileBar)),
                $"phone={phone} filmstrip={root.Filmstrip.Visibility} slots={root.PhoneSlots.Filmstrip} file={root.Panes.FileBar}");

            // 独立控制栏：胶片收起，控制内容出现。
            main.IsThumbnailViewVisible = false;
            main.IsControlViewVisible = true;
            await WaitLayoutAsync(root);
            if (phone)
            {
                var slots = root.PhoneSlots;
                bool controls = main.Settings.ShowRating
                    ? root.PhoneRating.Visibility == global::Android.Views.ViewStates.Visible
                    : true;
                // 竖屏上下张属于底行（控制栏），只有横屏的上下张要求胶片栏可见。
                bool navigationExpected = root.Form == ScreenForm.PhonePortrait
                    ? Has(slots.Navigation)
                    : false;
                Check("toggles 独立控制栏（手机）",
                    root.PhoneFitEnhance.Visibility == global::Android.Views.ViewStates.Visible && controls
                    && !Has(slots.Filmstrip) && Has(slots.Navigation) == navigationExpected,
                    $"fit={slots.FitEnhance} rating={slots.Rating} film={slots.Filmstrip} nav={slots.Navigation}");
            }
            else
            {
                Check("toggles 独立控制栏（宽形态）",
                    Has(root.Panes.ControlContent) && root.ControlBar.GroupCount > 0
                    && !Has(root.Panes.FileContent),
                    $"control={root.Panes.ControlContent} groups={root.ControlBar.GroupCount}");
            }

            // 关闭上下张：合体收起，星级槽位并入让出的长度。
            main.IsThumbnailViewVisible = true;
            main.Settings.ShowNavigationButtons = false;
            await WaitLayoutAsync(root);
            if (phone)
            {
                var slots = root.PhoneSlots;
                Check("toggles 关闭上下张释放长度",
                    !Has(slots.Navigation) && root.PhoneNavigation.Visibility != global::Android.Views.ViewStates.Visible,
                    $"nav={slots.Navigation} rating={slots.Rating}");
            }
            main.Settings.ShowNavigationButtons = true;

            // 关闭评分：手机移除评分槽，宽形态移除评分分组。
            main.Settings.ShowRating = false;
            await WaitLayoutAsync(root);
            if (phone)
            {
                Check("toggles 关闭评分移除槽位",
                    !Has(root.PhoneSlots.Rating) && root.PhoneRating.Visibility != global::Android.Views.ViewStates.Visible,
                    $"rating={root.PhoneSlots.Rating}");
            }
            else
            {
                Check("toggles 关闭评分移除分组", !root.ControlBar.HasRatingGroup,
                    $"groups={root.ControlBar.GroupCount}");
            }
            main.Settings.ShowRating = true;

            // 手机 EXIF 开关：关掉只影响 EXIF 块。
            if (phone)
            {
                main.IsExifViewVisible = false;
                await WaitLayoutAsync(root);
                Check("toggles 关闭手机 EXIF 只收起 EXIF 块",
                    !Has(root.PhoneSlots.Exif) && root.PhoneExif.Visibility != global::Android.Views.ViewStates.Visible
                    && Has(root.PhoneSlots.Filter) && Has(root.PhoneSlots.More),
                    $"exif={root.PhoneSlots.Exif} filter={root.PhoneSlots.Filter} more={root.PhoneSlots.More}");
            }
            main.IsExifViewVisible = true;

#if PHOTOVIEWER_DINO
            // AI 相似面板：只改变文件栏让宽，不影响筛选/当前照片。
            if (!phone)
            {
                var filter = main.FileVM.FilterBar;
                bool previous = filter.IsSimilarityPanelOpen;
                double filmWidthBefore = root.Filmstrip.Width;
                filter.IsSimilarityPanelOpen = !previous;
                await WaitLayoutAsync(root);
                Check("toggles AI 相似面板开合改变文件栏让宽",
                    root.SimilarityPane.Visibility == global::Android.Views.ViewStates.Visible
                    && root.Filmstrip.Width < filmWidthBefore - 1,
                    $"panel={root.SimilarityPane.Visibility} film={filmWidthBefore}->{root.Filmstrip.Width}");
                filter.IsSimilarityPanelOpen = previous;
                await WaitLayoutAsync(root);
            }
#endif

            // 恢复全开并核对。
            main.IsThumbnailViewVisible = true;
            main.IsControlViewVisible = true;
            main.IsExifViewVisible = true;
            main.IsAnalysisViewVisible = analysis;
            await WaitLayoutAsync(root);
            if (phone)
            {
                Check("toggles 恢复全开",
                    root.Filmstrip.Visibility == global::Android.Views.ViewStates.Visible
                    && root.PhoneFitEnhance.Visibility == global::Android.Views.ViewStates.Visible
                    && Has(root.PhoneSlots.Filter) && Has(root.PhoneSlots.More),
                    $"film={root.PhoneSlots.Filmstrip} fit={root.PhoneSlots.FitEnhance}");
            }
            else
            {
                Check("toggles 恢复全开", Has(root.Panes.FileContent) && Has(root.Panes.ControlContent)
                    && root.ControlBar.HasExifGroup,
                    $"file={root.Panes.FileBar} control={root.Panes.ControlContent}");
            }
        }
        finally
        {
            main.IsThumbnailViewVisible = film;
            main.IsControlViewVisible = control;
            main.IsExifViewVisible = exif;
            main.IsAnalysisViewVisible = analysis;
            main.Settings.ShowRating = rating;
            main.Settings.ShowNavigationButtons = navigation;
            await WaitLayoutAsync(root);
        }
    }
}
