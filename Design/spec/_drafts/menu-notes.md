# menu 模块拆分笔记（草稿，不进入规范）

## platforms

| ID | 平台 | 实现事实 |
|---|---|---|
| MENU-01 | mac | `MacMainWindow.cs:885-899` 用 `NSMenu` 挂在照片视图与根视图（`:203-204`）。项序：打开文件…、打开文件夹…、⎯、胶片栏、分析栏、控制栏、⎯、工具（无省略号）、设置…。**无「增强」**。系统菜单栏另有 PhotoViewer/文件/显示三菜单（`:860-876`），显示菜单含胶片栏、控制栏、三种布局、全屏，**无分析栏、无增强**。 |
| MENU-01 | win | `WindowsMainWindow.cs:342-384` 原生 `MenuFlyout`，`ContextFlyout` 挂在根网格（`:193`，覆盖含胶片栏/标题栏的整窗）。项序：打开文件…、打开文件夹…、⎯、缩略图栏、分析栏、控制栏、⎯、工具…、设置…。**无「增强」**，且胶片栏文案被有意改回「缩略图栏」（`Plans/native-rebuild-windows-p2.md:397`）。 |
| MENU-01 | ios | `iOSContextMenus.cs:48-68` `UIMenu` + SF Symbols，四段：打开文件夹…／胶片栏·分析栏·控制栏（内联分组）／增强／工具…·设置…（内联分组）。无「打开文件…」；`iOSNativeRootViewController.cs:683-686` 每次构建现读共享状态。 |
| MENU-01 | android | `AndroidNativeRootView.cs:478-583` 自绘 `PopupWindow`（非系统 `PopupMenu`，原因为系统弹层飞出屏幕，`Plans/native-rebuild-phase-android.md:262` 发现 #33）。宽 224dp、行高 44dp、内边距 4dp、分隔线 1dp、圆角 8dp、阴影 12dp、屏幕边距 8dp；超屏高时整块可滚动。项序：打开文件夹…／胶片栏·分析栏·控制栏／增强预览／工具…·设置…（`:545-555`）。分组与分隔线按 `:270` 发现 #41 收敛。 |
| MENU-02 | mac | 勾选由 `UpdateMenuChecks` 统一刷新（`MacMainWindow.cs:612`、`:883`），状态变化即更新已存在的菜单对象；菜单可常驻打开。胶片栏/控制栏显隐写入 `NSUserDefaults`（`:616-617`），分析栏不持久化。 |
| MENU-02 | win | 弹层 `Opening` 时同步三个勾选（`WindowsMainWindow.cs:363-368`）。栏位显隐无持久化。 |
| MENU-02 | ios | 菜单每次弹出现建（`iOSContextMenus.cs:12`），勾选在构建时求值；胶片栏/控制栏显隐写入 `NSUserDefaults`（`iOSNativeRootViewController.cs:1042-1052`），分析栏不持久化。 |
| MENU-02 | android | 长按时现读共享状态（`AndroidNativeRootView.cs:548-552`）。增强取 `PhotoLoadSession.IsEnhanced`（`:552`）。 |
| MENU-03 | mac | 系统 `NSMenu` 定位/键盘/取消由 AppKit 提供；无窗口内夹取代码。 |
| MENU-03 | win | 系统 `MenuFlyout` 定位与取消；无自绘夹取。 |
| MENU-03 | ios | `UIContextMenuInteraction` 挂在主图容器（`iOSNativeRootViewController.cs:235`），长按/辅助点按同入口；菜单外点击由系统收起。 |
| MENU-03 | android | 触点坐标换算为屏幕坐标后夹取到 `[8dp, 屏宽-菜单宽-8dp]`（`AndroidNativeRootView.cs:566-568`）；返回键优先关闭菜单（`:599-607`，`AndroidNativeShell.cs:41-43`）。 |
| MENU-04 | ios | 主图用触点处 1 点透明 `UITargetedPreview`（`iOSContextMenus.cs:149-175`），照片不抬升不缩小；卡片保留系统默认抬升。 |
| MENU-04 | android | 自绘弹层不触碰主图视图，照片区不动；弹层关闭前主图持续渲染。 |
| MENU-04 | mac/win | 系统右键菜单不改变照片位置。 |
| MENU-05 | ios | `iOSContextMenus.cs:77-93`：标题 `文件名 · yyyy-MM-dd HH:mm:ss zzz`，子菜单「评分」含「无星级/1 星…5 星」并勾选当前星级；回调只调共享改星（`iOSNativeRootViewController` 评分路径）。 |
| MENU-05 | android | `AndroidThumbnailCard.cs:288-307` 系统 `PopupMenu`：第 1 行文件名、第 2 行 `yyyy-MM-dd HH:mm`（均为普通菜单项 id 0/1），随后 0–5 六个可勾选项；长按由 `GestureDetector.OnLongPress` 触发（`:337`）。 |
| MENU-05 | mac | 无卡片菜单；完整名/时间靠悬停提示（`MacThumbnailItem.cs:143-145`），评分靠卡片星级按钮。 |
| MENU-05 | win | 无卡片菜单；`WindowsThumbnailCard.cs:316-319` 用 `ToolTipService` 承担完整名与完整时间。 |
| MENU-06 | 全部 | 无平台在菜单里显示快捷键列；mac 只在系统菜单栏给「设置…」标 `⌘,`（`MacMainWindow.cs:863`）。文案差异：「工具」无省略号（mac）、「增强」vs「增强预览」（iOS vs Android）、「缩略图栏」（win）。 |
| MENU-07 | 全部 | 无照片时各平台菜单项均可执行：Android 增强/工具直接调会话与窗口门面（`AndroidNativeRootView.cs:552-555`），iOS 同（`iOSNativeRootViewController.cs:683-686`）；未做置灰。 |
| MENU-07 | ios/android | 设置/工具以 sheet、PageSheet、BottomSheetDialog 呈现时主图被系统遮挡，菜单不可达；桌面为独立窗口，主窗口菜单仍可用。 |

## status

| ID | Windows | macOS 原生 | iOS/iPadOS | Android |
|---|---|---|---|---|
| MENU-01 / 02 / 03 | 代码核对：右键菜单可弹出、可执行打开选择器与栏位切换；150% 实机验证「空首屏右键只出菜单、左键出选择器」与「右键菜单可弹出文件选择器并成功返回」（`Plans/native-rebuild-windows-p2.md:294`、`:311`、`:338`）；无菜单专项断言 | 代码核对：`MacMainWindow.cs:885-899`；系统菜单栏与右键菜单均实机可见，无独立验收条目 | **模拟器两机型 + 真机程序化通过（2026-10-17，`--pv-native-input-check`）**：菜单项集合、SF Symbols 图标、勾选态与栏位/增强一致、动作真实生效、重新弹出勾选同步、打开文件夹命中选择器（`Plans/native-rebuild-phase-ipad.md:145`；`ui-contract.md:105`） | **模拟器目视（2026-10-18）**：长按弹菜单、勾选态现读、返回键先关、弹层夹取与滚动（`Plans/native-rebuild-phase-android.md:184`、`:262`）；程序化断言待 A6 |
| MENU-04 | 不适用（系统菜单） | 不适用（系统菜单） | 作者真机缺陷②「长按菜单把照片抬升/缩小到一边」**已修**，模拟器真实长按截图照片区与长按前逐像素一致（`Plans/native-rebuild-phase-ipad.md:147`，2026-10-17）；真实手感待作者 | 模拟器目视（2026-10-18）：自绘弹层不移动照片；手感未验 |
| MENU-05 | 不适用（无卡片菜单） | 不适用（悬停提示 + 星级按钮） | 菜单标题含完整文件名 + 拍摄时间、评分读回一致且不切主图，`--pv-native-input-check` 模拟器 + 真机覆盖（`ui-contract.md:106`、`:149`，2026-10-17）；触摸手感待作者 | 模拟器目视（2026-10-18）：卡片长按菜单、评分写回（`ui-contract.md:149`）；长按是否同时点选未断言 |
| MENU-06 | 缺口：「缩略图栏」文案；无快捷键列；「工具…」省略号正确 | 缺口：「工具」缺省略号；无快捷键列 | 与规范一致（工具…、胶片栏、增强）；无快捷键列 | 缺口：「增强预览」文案（规范为「增强」）；无快捷键列 |
| MENU-07 | 待核验 | 待核验 | 待核验 | 待核验 |

## questions

1. **「增强」是否属于主图菜单**：iOS/Android 有（`iOSContextMenus.cs:60`、`AndroidNativeRootView.cs:552`），mac/Windows 无（`MacMainWindow.cs:891-896`、`WindowsMainWindow.cs:370-379`）；`ui-contract.md:105` 又称 iOS 菜单「与 Mac 原生右键菜单同序」却列出了增强。规范按四端一致收录增强，mac/Windows 记为缺口——需作者确认，或改为 `MENU-01@ios/android` 平台差异。
2. **「胶片栏」还是「缩略图栏」**：Windows 有意改回旧壳文案（`Plans/native-rebuild-windows-p2.md:397`），mac/iOS/Android 用「胶片栏」（`MacMainWindow.cs:891`、`iOSContextMenus.cs:56`、`AndroidNativeRootView.cs:548`）。规范统一为「胶片栏」，与 `Design/spec/01-regions.md` 一致；Windows 需改回。
3. **Android 长按卡片是否会同时切图**：`AndroidThumbnailCard.cs:264-285` 的 `Up` 分支无长按守卫，`OnLongPress` 弹菜单后抬手仍会走星级/整卡点选；与 MENU-05「不切主图」冲突，且 Android 检查（`AndroidNativeChecks.cs`）无菜单断言，未验证。
4. **卡片菜单标题形态与时间格式**：iOS 单行标题 `yyyy-MM-dd HH:mm:ss zzz`（`iOSContextMenus.cs:88-90`）；Android 是两条普通菜单行、格式 `yyyy-MM-dd HH:mm`（`AndroidThumbnailCard.cs:293-294`），且这两行可被点击（无动作）。规范取 iOS 形态，Android 需对齐或登记差异。
5. **触屏是否要「打开文件…」**：iOS 已具备单文件选择器 `PickFileAsync`（`iOSUiServices.cs:100-104`），但菜单只给文件夹（`iOSNativeRootViewController.cs:683-686`）；Android SAF 同样只有文件夹（`AndroidNativeRootView.cs:546`）。规范按「触屏只有打开文件夹…」写，若作者要求单文件入口需另立条目（OPEN 侧已有单图模式）。
6. **「工具」省略号**：mac 为「工具」（`MacMainWindow.cs:895`），iOS/Windows 为「工具…」（`iOSContextMenus.cs:63`、`WindowsMainWindow.cs:377`）。规范按省略号规则要求带省略号，mac 记为缺口。
7. **系统菜单栏与主图菜单不一致**：mac 显示菜单缺分析栏与增强（`MacMainWindow.cs:868-872`），但右键菜单有分析栏；若 MENU-01 的「可以」条款生效，显示菜单是否也要补齐三项勾选项？
8. **菜单是否显示快捷键**：四端都未在上下文菜单显示主/次绑定；mac 仅「设置…」有 `⌘,`（`MacMainWindow.cs:863`）。MENU-06 把它写成「应当」，若作者认为属原生控件自定，可降为「可以」。
9. **无照片时「增强」置灰**：现有实现均可直接执行（`AndroidNativeRootView.cs:552`、`iOSNativeRootViewController.cs:683-686`），MENU-07 要求置灰；「工具…」允许打开空态是否可接受，也需确认（工具窗口 EXIF/DINO 在无照片时无内容）。
10. **栏位显隐持久化口径**：mac/iOS 只持久化胶片栏与控制栏，分析栏不持久化（`MacMainWindow.cs:616-617`、`iOSNativeRootViewController.cs:1051-1052`）；Windows/Android 未持久化。属 SHELL 规则，MENU-02 只写「随 shell」，拆分时需与 shell 模块对齐。
