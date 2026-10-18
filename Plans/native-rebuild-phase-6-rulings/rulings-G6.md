# 裁定 G6 · visual-tokens（platforms.md「令牌层平台差异」全表，共 34 项）

> 2026-10-18。逐条读代码复核（`platforms.md` 的表按 `tokens.json` 抄录，个别描述与代码不符，本文以代码为准并在脚注标出）。分类见 [Design/README.md](../../README.md) §3：**U** 行为/视觉差异必须统一；**P** 平台差异（控件/文字度量/输入方式，无优劣）；**X** 规范与代码矛盾或缺陷（改代码或改规范）。判断依据只取 多数 / 更优 / 原版（Avalonia 旧壳计入 5 个实现之一，平票时作 tie-break）。
>
> 行按域排列：filter → film → image / nav → analysis → sim → status → exiftable → color。`—` 表示该端无此元素（不是差异）。

| Q | 类别 | Avalonia | mac | win | iOS | android | 裁定 | 依据 | 后续（谁改什么） | 需作者 |
|---|---|---|---|---|---|---|---|---|---|---|
| G6-01 filter.toggle.height | U | 27 | 27 | 32 | 27 | 27 | 统一 27；开关不得借用 32 高下拉控件 | 多数 4:1；FILTER-07 即 27 | win：Height/MinHeight 置 27 | 否 |
| G6-02 filter.toggle.width | P | 90 | 90 | 90 | 90 | 76 紧凑 | 保留 76，仅 <600 宽手机；其余一律 90 | 输入/屏宽差异；filter.md 已许 | 登记 FILTER-01@android；tokens 注明仅紧凑 | 否 |
| G6-03 film.card.name.width | U | 90 | 86 | 90 | 86 | 86 | 统一 86：内层占位 90 左右各留 2 | 多数 3:2 | win：MaxWidth 90→86 | 否 |
| G6-04 image.hint.detail.gap | P | 8 间距 | 28 | 8 间距 | 28 | 24 | 保留 android 24：24 行高 + 6 内边距≈桌面 8 | 文字行高差异，视觉等效 | tokens 注明 28=行高 20+8；android 不改 | 否 |
| G6-05 image.busy.cornerRadius | U | — | — | — | 11 | 0 | 统一 11 的圆角药丸 | 令牌 + IMAGE-07；android 未实现 | android：GradientDrawable 圆角 11dp | 否 |
| G6-06 image.busy.bottomOffset | X | — | — | — | 24 | 20+安全区 | 统一 24；安全区不得重复扣 | 主图分区已排除底部安全区（`AndroidMainScreenLayout`） | android：删 `- _insets.Bottom`；提示条同病 | 否 |
| G6-07 nav.cornerRadius | U | 8 | 6 | 8 | 6 | 6 | 统一 6 | 多数 3:2 | win：8→6 | 否 |
| G6-08 nav.frame.thickness | U | 2 | 1 | 2 | 1 | 1 | 统一 1 | 多数 3:2 | win：2→1 | 否 |
| G6-09 color.nav.background.alpha | U | 0.573 | 0.6 | 0.573 | 0.78 | 0.784 | 统一 0.6 | 桌面一簇≈0.6，移动一簇 0.78 | ios/android→0.6；win 0x92→0x99 | 否 |
| G6-10 analysis.tile.innerCornerRadius | P | 5 | 5 | 0 | 5 | 5 | 保留 win 0：单层裁切视觉等效 | 已登记 ANALYSIS-02@windows | 不改 | 否 |
| G6-11 sim.action.width | U | 90 | 90 | 90 | 90 | 64 | 统一 90×28 按钮 | 多数 4:1；SIM-03 固定卡片宽 | android：64→90 并画按钮外观 | 否 |
| G6-12 color.sim.action.background | U | #2A2A2A | #2A2A2A | #2A2A2A | #2A2A2A | 透明 | 统一 #2A2A2A | 多数 4:1 | android：补按钮底色 | 否 |
| G6-13 color.sim.action.border | U | #555555 | #555555 | #555555 | #555555 | 透明 | 统一 #555555，1 点 | 多数 4:1 | android：补描边 | 否 |
| G6-14 color.sim.action.foreground | U | #CCCCCC | #CCCCCC | #CCCCCC | #CCCCCC | #0A84FF | 统一 #CCCCCC | 多数 4:1 | android：文字改 #CCC | 否 |
| G6-15 status.toast.fontSize | U | — | NSAlert | 12 | 仅日志 | 13 | 统一 12 | 令牌 + STATE-03 | android：13→12 | 否 |
| G6-16 status.toast.cornerRadius | U | — | NSAlert | 6 | 仅日志 | 0 | 统一 6 | 令牌 + STATE-03 | android：圆角 6dp | 否 |
| G6-17 status.toast.maxWidth | U | — | NSAlert | 500 | 仅日志 | 260 | 统一 min(500, 可用宽) | 令牌 500；260 是硬顶 | android：260→min(500,宽−40) | 否 |
| G6-18 color.status.toast.background | U | — | NSAlert | #222222 | 仅日志 | #282828 | 统一 #222222 | 令牌 | android：改 #222 | 否 |
| G6-19 color.status.toast.background.alpha | U | — | NSAlert | 0.94 | 仅日志 | 0.902 | 统一 0.94 | 令牌 | android：230→240 | 否 |
| G6-20 color.status.toast.foreground | U | — | NSAlert | #808080 | 仅日志 | #FFFFFF | 统一 #FFFFFF | 更优：灰字在 #222 上 4.0:1，不达 AA | win 改白；tokens 值改白 | 否 |
| G6-21 exiftable.cell.paddingX | U | 10 | 10 | 8 | 10 | 10 | 统一 10，取共享 `ExifTableGeometry` | 多数 4:1 | win：8→10；复核 7 列最小宽 | 否 |
| G6-22 exiftable.cell.paddingY | U | 8 | 8 | 6 | 8 | 8 | 统一 8 | 多数 4:1 | win：6→8 | 否 |
| G6-23 color.filter.toggle.checked.background | U | #1F3A5F | #1F3A5F | #1F3A5F | 系统蓝 | #0A84FF | 统一 #1F3A5F | 多数 3:2；白字压系统蓝仅 3.6:1 | ios/android：改深蓝底 | 否 |
| G6-24 color.filter.toggle.checked.border | U | #3A6FA8 | #3A6FA8 | #3A6FA8 | #444444 | #0A84FF | 统一 #3A6FA8 | 多数 3:2；iOS 实际未随选中改边 | ios/android：改描边 | 否 |
| G6-25 color.filter.toggle.checked.foreground | U | #AECBEE | #AECBEE | #AECBEE | #FFFFFF | #FFFFFF | 统一 #AECBEE | 多数 3:2；对比 6.9:1 | ios/android：改字色 | 否 |
| G6-26 color.filter.toggle.foreground | U | #AAAAAA | #AAAAAA | #AAAAAA | #FFFFFF | #EEEEEE | 统一 #AAAAAA | 多数 3:2；未选中应为次要色 | ios/android：改 #AAA | 否 |
| G6-27 color.film.card.background | U | #222222 | #222222 | #222222 | #222222 | #262626 | 统一 #222222 | 多数 4:1 | android：卡片面改 #222（ControlFill 仍供控件） | 否 |
| G6-28 color.film.card.currentBorder | U | #1E90FF | 系统蓝 | #1E90FF | 系统蓝 | #0A84FF | 统一 #0A84FF（Apple 暗色 SystemBlue 同值） | 多数 3:2；PRIN-08 语义色一致 | win：#1E90FF→#0A84FF；film.md:52 改引令牌 | 否 |
| G6-29 color.star.on | U | #FFD700 | #FFD214 | #FFD700 | #FFD214 | #FFD214 | 统一 #FFD214 | 多数 3:2 | win：改 #FFD214 | 否 |
| G6-30 color.star.off | U | #808080 | #808080 | #808080 | #808080 | #888888 | 统一 #808080 | 多数 4:1 | android：改 #808080 | 否 |
| G6-31 color.star.clear | U | #666666 | 系统次级 | #666666 | #666666 | #888888 | 统一 #666666 | 多数 3:2；图形色不套文本语义 | mac：显式 #666；android 改 #666 | 否 |
| G6-32 color.toolbar.block.background | U | #222222 | #222222 | #222222 | #222222 | #262626 | 统一 #222222 | 多数 4:1 | android：块底改 #222 | 否 |
| G6-33 color.bar.background | U | #111111 | #111111 | #111111 | #121212 | #1A1A1A | 统一 #111111 | 多数 3:2；令牌 | ios/android：改 #111 | 否 |
| G6-34 color.text.secondary | P | #AAAAAA | 系统次级 | #AAAAAA | 系统次级 | #AAAAAA | 保留 Apple 系统次级文字色 | 平台语义色，与 #AAA 肉眼差 ≤2 阶 | tokens 保留 macos/ios 平台值 | 否 |

## 脚注（逐条证据）

- **G6-01**：Avalonia `PhotoViewer/Views/Main/File/FilterBarView.axaml:106-109`（开关无显式高，旧壳量得 27）；共享 `PhotoViewer.Shared/MainScreenLayout.cs:19`、`:35`；mac `PhotoViewer.Mac/Native/MacMainWindow.cs:475`；iOS `PhotoViewer.iOS/Native/iOSFilterBar.cs:98`；android `PhotoViewer.Android/Native/AndroidFilterBar.cs:89`；win `PhotoViewer.Windows/Native/WindowsFilterBar.cs:132` + `PhotoViewer.Windows/Native/WindowsLayout.cs:25`（32）；规范 `Design/spec/filter.md:81`。
- **G6-02**：android `PhotoViewer.Android/Native/AndroidFilterBar.cs:87`（`_compact = horizontal && _contentWidth < 600`）、`:91-92`（下拉 84 / 开关 76）；Avalonia `FilterBarView.axaml:109`（90）；共享 `PhotoViewer.Shared/MainScreenLayout.cs:29`、`:100`；规范 `Design/spec/filter.md:85`（写作 `filter-07@android`，与 `platforms.md:126` 的 `FILTER-01@android` 不同号）。
- **G6-03**：Avalonia `PhotoViewer/Controls/ThumbnailCard.axaml:128`（MaxWidth 90）；mac `PhotoViewer.Mac/Native/MacThumbnailItem.cs:76`（86）；iOS `PhotoViewer.iOS/Native/iOSThumbnailCard.cs:101`（86）；win `PhotoViewer.Windows/Native/WindowsThumbnailCard.cs:164` + `WindowsLayout.cs:51`（CardWidth 90）；android `PhotoViewer.Android/Native/AndroidThumbnailCard.cs:168`（86）。
- **G6-04**：Avalonia `PhotoViewer/Views/Main/ImageView.axaml:64-70`（副文 Margin 0,8）；mac `PhotoViewer.Mac/Native/MacMainWindow.cs:472-474`（副文顶 = 标题顶 + 28）；iOS `PhotoViewer.iOS/Native/iOSNativeRootViewController.cs:518-520`；win `PhotoViewer.Windows/Native/WindowsMainWindow.cs:94-104`（Margin 8）；android `PhotoViewer.Android/Native/AndroidNativeRootView.cs:362-365`（labelHeight 24；副文顶 = 标题顶 + 24，副文自身 Padding 上 6，见 `:162`）；规范 `Design/spec/state.md:8`。
- **G6-05**：iOS `PhotoViewer.iOS/Native/iOSNativeRootViewController.cs:223-232`（96×28、圆角 11）；android `PhotoViewer.Android/Native/AndroidNativeRootView.cs:168-178`（`SetBackgroundColor`，无圆角）、`:367-370`（96×28 落位）；mac/win 无药丸（`Design/platforms.md:147`）；规范 `Design/spec/image.md:87`。
- **G6-06**：iOS `PhotoViewer.iOS/Native/iOSNativeRootViewController.cs:521-524`（`pane.Height − 28 − 24`，pane 已扣安全区）；android `PhotoViewer.Android/Native/AndroidNativeRootView.cs:367-370`（`− 20 − _insets.Bottom`）；`PhotoViewer.Android/Native/AndroidMainScreenLayout.cs:45`（boxHeight = height − top − bottom）、`:55`（Photo 已下移 top，下沿不吃底部安全区）；同一重复扣法见 `AndroidNativeRootView.cs:373`（状态提示 `− 24 − _insets.Bottom`）。
- **G6-07**：Avalonia `PhotoViewer/Views/Main/ImageView.axaml:77-79`（8）；mac `PhotoViewer.Mac/Native/MacMainWindow.cs:143`（6）；iOS `PhotoViewer.iOS/Native/iOSNativeRootViewController.cs:205`（6）；android `PhotoViewer.Android/Native/AndroidNavigationOverlay.cs:86`（6）；win `PhotoViewer.Windows/Native/WindowsLayout.cs:165` + `PhotoViewer.Windows/Native/WindowsNavigator.cs:86`（8）；规范 `Design/spec/image.md:72`。
- **G6-08**：Avalonia `ImageView.axaml:91-97`（StrokeThickness 2）；mac `MacMainWindow.cs:145`（1）；iOS `iOSNativeRootViewController.cs:212`（1）；android `AndroidNavigationOverlay.cs:40`（1）；win `WindowsLayout.cs:168` + `WindowsNavigator.cs:59`（2）；规范 `Design/spec/image.md:72`。
- **G6-09**：Avalonia `ImageView.axaml:78`（`#9222`，0x92/255 = 0.573）；mac `MacMainWindow.cs:142`（0.6）；win `WindowsLayout.cs:180`（0x92）；iOS `iOSNativeRootViewController.cs:204`（0.78）；android `AndroidNavigationOverlay.cs:39`（200/255 = 0.784）；规范 `Design/spec/image.md:72`。
- **G6-10**：Avalonia `PhotoViewer/Controls/DiagnosticTile.axaml:25-34`（外 6 / 内 5）；mac `PhotoViewer.Mac/Native/MacDiagnosticTile.cs:18-19`；iOS `PhotoViewer.iOS/Native/iOSDiagnosticTile.cs:22-23`；android `PhotoViewer.Android/Native/AndroidAnalysisPanel.cs:69`、`:219`（内 6−1）；win `PhotoViewer.Windows/Native/WindowsAnalysisPanel.cs:486-492`（单层 Border）+ `WindowsLayout.cs:325`（外 6）；登记 `Design/platforms.md:161-162`。
- **G6-11**：Avalonia `PhotoViewer/Views/Main/File/SimilarityListView.axaml:23-25`（28×90）；mac `PhotoViewer.Mac/Native/MacSimilarityPanel.cs:37`（90×28）；iOS `PhotoViewer.iOS/Native/iOSSimilarityPanel.cs:34`；win `WindowsLayout.cs:281` + `PhotoViewer.Windows/Native/WindowsSimilarityPanel.cs:326`；android `PhotoViewer.Android/Native/AndroidSimilarityPanel.cs:24`（ActionWidth 40）、`:82`（64×28 纯文本）；规范 `Design/spec/similarity.md:33`。
- **G6-12、G6-13、G6-14**：Avalonia `SimilarityListView.axaml:28-30`（边 #555 / 底 #2A2A2A / 字 #CCC）；mac `MacSimilarityPanel.cs:489-490`、`:536`；iOS `iOSSimilarityPanel.cs:481-485`；win `WindowsLayout.cs:290`、`:293`、`:296`；android `AndroidSimilarityPanel.cs:77`（字 `Accent`）、`:82`（无底色/描边）。
- **G6-15、G6-16、G6-17**：win `WindowsLayout.cs:193`（12）、`:202`（6）、`:145`（HintMaxWidth 500）+ `PhotoViewer.Windows/Native/WindowsMainWindow.cs:115`、`:123`、`:126`；android `PhotoViewer.Android/Native/AndroidNativeRootView.cs:181`（13）、`:186`（`SetBackgroundColor`，无圆角）、`:371`（`Math.Min(260, …)`）；mac `PhotoViewer.Mac/Native/MacMainWindow.cs:852-856`（NSAlert）；iOS `PhotoViewer.iOS/Native/iOSNativeRootViewController.cs:647-652`（仅日志）；规范 `Design/spec/state.md:18`。
- **G6-18、G6-19、G6-20**：win `WindowsLayout.cs:211`（0xF0 #222）、`:459`（HintTitleForeground #808080）+ `WindowsMainWindow.cs:116`；android `AndroidNativeRootView.cs:185-186`（白字、`Color.Argb(230, 40, 40, 40)`）；对比度按 WCAG 2.x 计算：#808080/#222222 = 4.03:1（12 点正文需 4.5:1），#FFFFFF/#222222 ≈ 15.9:1；规范 `Design/spec/state.md:18`。
- **G6-21、G6-22**：共享 `PhotoViewer.Shared/ExifTableGeometry.cs:17`（10）、`:20`（8）；Avalonia `PhotoViewer/Views/Tools/ExifDetailView.axaml:31-43`（Padding 10,8）；win `PhotoViewer.Windows/Native/WindowsLayout.cs:556`、`:559`（8/6）+ `PhotoViewer.Windows/Native/WindowsExifToolPage.cs:442-445`；mac/iOS/android 均取共享常量。
- **G6-23、G6-24、G6-25、G6-26**：Avalonia `FilterBarView.axaml:32`（未选中 #AAA）、`:40-42`（选中 #1F3A5F / #3A6FA8 / #AECBEE）；mac `MacMainWindow.cs:285-289`；win `WindowsLayout.cs:385`、`:394`、`:397`、`:400`；iOS `PhotoViewer.iOS/Native/iOSFilterBar.cs:85`（背景 `UIColor.SystemBlue`，无边框变更，文字沿用 `:114-124` 的白字与 #444 边框）——`tokens.json` 记的「iOS checked.border = system-accent」与代码不符；android `AndroidFilterBar.cs:114-119`（Accent 底/边、白字，未选中 `TextPrimary` #EEEEEE）；白字压 #0A84FF = 3.6:1，深蓝底压 #AECBEE = 6.9:1。
- **G6-27**：Avalonia `PhotoViewer/Controls/ThumbnailCard.axaml:65`（#222）；mac `MacThumbnailItem.cs:65`（2/15 白 = #222）；iOS `PhotoViewer.iOS/Native/iOSThumbnailCard.cs:84`（同）；win `WindowsLayout.cs:414`（BlockBackground #222）；android `PhotoViewer.Android/Native/AndroidUiTheme.cs:24`（ControlFill #262626）+ `AndroidThumbnailCard.cs:131`。
- **G6-28**：Avalonia `PhotoViewer/Views/Main/File/ThumbnailConverters.cs:17`（`Colors.DodgerBlue` #1E90FF）；mac `MacThumbnailItem.cs:150`（`NSColor.SystemBlue`）；iOS `iOSThumbnailCard.cs:202`（`UIColor.SystemBlue`）；win `WindowsLayout.cs:420`（#1E90FF，非 Windows 系统强调色）+ `WindowsThumbnailCard.cs:66`；android `AndroidUiTheme.cs:32`（#0A84FF）+ `AndroidThumbnailCard.cs:131`；规范 `Design/spec/film.md:52`（现写「平台语义色」，与 PRIN-08「语义色四端一致」冲突，按令牌改为引用 `color.film.card.currentBorder`）；mac 强制 DarkAqua 下 SystemBlue 即 #0A84FF。
- **G6-29、G6-30、G6-31**：Avalonia `PhotoViewer/Views/Main/ControlView.axaml.cs:182-185`（`Colors.Gold` #FFD700 / `Colors.Gray` #808080）、`ThumbnailCard.axaml:164`（清零叉 #666）；mac `MacThumbnailItem.cs:162`（#FFD214 / `NSColor.Gray`）、`:99`（清零叉 `NSColor.SecondaryLabel`）；iOS `iOSThumbnailCard.cs:104`（#666）、`:212`（#FFD214 / `UIColor.Gray`）；win `WindowsLayout.cs:429`（#FFD700）、`:432`（#808080）、`:435`（#666）；android `AndroidUiTheme.cs:34`（#FFD214）、`:36`（#888888）、`PhotoViewer.Android/Native/AndroidControlBar.cs:248`（清零叉 #888888）。
- **G6-32**：Avalonia `PhotoViewer/Views/Main/ControlView.axaml:74`、`:152`（#222）；mac `PhotoViewer.Mac/Native/MacControlBar.cs:231`（2/15 白 = #222）；iOS `PhotoViewer.iOS/Native/iOSControlBar.cs:77`、`:246`（同）；win `WindowsLayout.cs:414`（#222）；android `AndroidControlBar.cs:206`、`:231`（ControlFill #262626）。
- **G6-33**：Avalonia `PhotoViewer/Views/Main/MainView.axaml:20-97`（各栏 #111111）；mac `PhotoViewer.Mac/Native/MacMainWindow.cs:457`（文件栏 0.067 白 = #111）、`:459`（控制栏同值）；win `WindowsLayout.cs:405`（#111111）；iOS `PhotoViewer.iOS/Native/iOSNativeRootViewController.cs:180`（根 #121212）+ `iOSControlBar.cs:44`（0.07 白 = #121212）；android `AndroidUiTheme.cs:20`（#1A1A1A）。
- **G6-34**：Avalonia `PhotoViewer/Views/Main/ControlView.axaml:90`（#AAA）；mac `PhotoViewer.Mac/Native/MacControlBar.cs:241`（`NSColor.SecondaryLabel`）；iOS `PhotoViewer.iOS/Native/iOSControlBar.cs:263`（`UIColor.SecondaryLabel`）；win `WindowsLayout.cs:474`（#AAAAAA）；android `AndroidUiTheme.cs:30`（#AAAAAA）；`tokens.json` 的 `color.text.secondary` 无 spec 关联。

## 汇总

- 类别：**U 29**（G6-01、03、05、07–09、11–33）；**P 4**（G6-02 手机紧凑宽、G6-04 文字行高、G6-10 单层裁切、G6-34 Apple 系统次级文字色）；**X 1**（G6-06 Android 重复扣安全区）。
- **需作者 0**：34 项全部有实现先例或规范数值可依；无持久化契约、JSON 字段、DB schema 或 App Store 取舍，均不构成产品决策。
- 规范侧要动的行：
  - `Design/tokens.json`：**30 个令牌删去 `platforms` 字段**（只保留 G6-02 的 android 紧凑值、G6-04 的 android 24、G6-10 的 win 0、G6-34 的 macos/ios）；`color.status.toast.foreground` 值由 `#808080` 改 `#FFFFFF`；`color.film.card.currentBorder` 的 win/android 平台值删除（统一 #0A84FF）。
  - `Design/platforms.md:27-66`：整表按本文重写，最终只留 4 行平台差异（FILTER-01@android 紧凑、IMAGE-01@android 提示行高、ANALYSIS-02@windows、`color.text.secondary@mac/@ios`）。
  - `Design/spec/filter.md:85`：`filter-07@android` 改为 `FILTER-01@android`（与 platforms.md 同号）。
  - `Design/spec/state.md:8`：`image.hint.detail.gap` 后补「28 = 标题行高 20 + 间距 8」的构成说明。
  - `Design/spec/image.md:72`：不必改（6 / 0.6 / 1 已是令牌值）。
- 实现缺口清单（按平台，按令牌计）：**win 10 处**——开关高、文件名宽、导航圆角、导航框厚、导航不透明度取整、提示条字色、EXIF 内边距 ×2、当前项描边、金色；**android 20 处**——药丸圆角与距底、相似按钮宽/底/边/字、提示条字号/圆角/宽/底/透明度、开关四色、卡片底、块底、星灰、清叉、栏位底；**iOS 6 处**——开关四色、导航不透明度、栏位底；**mac 1 处**——星清叉色。
