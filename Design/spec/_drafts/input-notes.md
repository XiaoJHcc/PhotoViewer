# input 模块拆分草稿（INPUT）

> 本文件是 [input.md](../input.md) 拆分的来源记录：平台实现事实、验收状态原文与待确认问题。规则正文不写这些内容。

## platforms

| ID | 平台实现与登记的差异 |
|---|---|
| INPUT-01 | 共享查询 `PhotoViewer.Shared/InputCommandMap.cs`（`CommandForGesture`）。macOS：`PhotoViewer.Mac/Native/MacInput.cs` 解包 `NSEvent`，`MacMainWindow` 的窗口级 local monitor 派发。iOS：`PhotoViewer.Apple/AppleInputMapping.cs`（HID usage 47 键 + 修饰键掩码），`iOSNativeRootViewController.PressesBegan` 派发。Android：`PhotoViewer.Android/Native/AndroidInputMapping.cs` 解包 `KeyEvent`，`AndroidNativeActivity.DispatchKeyEvent` 入口；Ctrl/Alt/Shift/Meta 按 Android 语义，`GestureKey` 名称与 Apple 共用。Windows：`PhotoViewer.Windows/Native/WindowsInput.cs`（虚拟键 + `GetKeyState` 修饰键）。冲突只标记不阻止，派发按设置列表顺序取第一条（`SettingsViewModel.Hotkeys.cs:242`）。 |
| INPUT-02 | iOS 判据：`KeyboardInputBlocked = PresentedViewController != null || _editingTextInputs.Count > 0`，`UITextField`/`UITextView` 编辑通知维护集合，收起后 `BecomeFirstResponder()` 归还焦点。macOS：local monitor 检查 `FirstResponder is NSTextView or NSTextField`；设置/工具为独立窗口，天然不受影响。Windows：录制态自己在窗口根 `PreviewKeyDown` 捕获；设置/工具为独立窗口。Android：Activity `DispatchKeyEvent` 在设置 sheet/文本输入期间让位（计划 §A4）。 |
| INPUT-03 | macOS：`MetalPhotoView` 捏合 `MagnifyWithEvent`、精确滚动 `ScrollWheel`、拖动 `MouseDragged`、双击 `ClickCount == 2` 调 `ToggleFit(锚点)`。iOS：`UIScrollView` 为唯一交互真源（捏合/平移/双击 `ZoomToRect`），导航图额外支持点按/拖动定位（Mac 只展示）。Android：`ScaleGestureDetector` + `GestureDetector`，`AndroidPhotoView` 以 dp 为点、`density` 为 BackingScale。Windows：`WindowsPhotoView` 处理指针手势与滚轮。 |
| INPUT-04 | macOS：`MetalPhotoView.ScrollWheel` 按 `HasPreciseScrollingDeltas` 分流——精确滚动平移、非精确滚轮交给 `MacInput` 查绑定。iOS：离散滚轮识别器（`Minimum/MaximumNumberOfTouches = 0`）只走用户鼠标绑定；触控板连续滚动由 `UIScrollView` 平移。Android：`AndroidPhotoView.WheelScrolled` 全部按滚轮绑定处理，没有精确/离散分流。Windows：`WindowsMainWindow.OnPhotoWheel` → `WindowsInput.FromWheel(delta)`，同样不分精确/离散。 |
| INPUT-05 | iOS：`NSTimer` 固定节奏（延迟 0.35 s、间隔 0.06 s），每次重复重新查绑定；`UIPress` 无 repeat 标志、系统速率不可读，登记为平台差异。macOS/Windows/Android 由系统重复键事件驱动（Android 同时处理 `KeyEventActions.Multiple`）。当前三端对所有按住键都重复，包括评分与一次性命令。 |
| INPUT-06 | iOS：`UIPanGestureRecognizer` 单指、24 点阈值、水平位移需明显大于纵向，只在适应态翻页，走共享 Previous/Next；与滚动视图平移、捏合、双击同时识别。Android：同语义（计划 §1.9/§A2）。macOS/Windows 无此规则。 |
| INPUT-07 | macOS：主图 `Menu` 挂在根视图与主图上，`RightMouseDown` 放行到原生菜单；标题栏拖动区不参与。iOS：`UIContextMenuInteraction`，主图用触点处 1 点透明定向预览（照片不抬升），卡片菜单保留系统抬升。Android：手指处自绘 `PopupWindow`（夹在屏幕内、可滚动、返回键先关）。Windows：`MenuFlyout` 挂主图区。 |
| INPUT-08 | macOS：SwiftUI 设置页内的录制控件，真实 `NSEvent` 捕获。Windows：`WindowsHotkeyRecorder.cs`，窗口根 `PreviewKeyDown` + `AddHandler(..., handledEventsToo)`，Esc 取消、Delete/Backspace 清空，录制态吞滚轮与中键。iOS：设置页控制列表只有显隐与排序，**快捷键只读查看**（`iOSNativeSettingsPresenter.cs:265`、`:1465`），没有录制控件。Android：控制列表用勾选 + 上下按钮排序，录制未实现（计划 §A4 偏差）。 |
| INPUT-09 | 四端命令实现一致：macOS `MacMainWindow.ZoomPreset`、iOS/Android `ZoomPreset` + `Photo.ZoomBy(1.25/.8)`、Windows `WindowsMainWindow` 同名分支。均以视口中心为锚。 |
| INPUT-10 | macOS/Windows 空态：提示文字 + 透明命中层，左键打开选择器。iOS：空态主图点按打开文件夹选择器，长按菜单承担窗口级入口。Android：同 iOS 路径。 |
| INPUT-11 | macOS：Esc 由系统关菜单；标题栏双击走窗口级监视器（SHELL-02）。Windows：Alt+F4 等系统键不拦。iOS：无返回键；模态收起由系统承担。Android：`OnBackPressedCallback` 先关设置/工具 sheet 与菜单。 |
| INPUT-12 | 卡片星级 14 点低于 44 点建议，iOS 用卡片菜单补足评分（`iOSContextMenus.Card`）。Android 卡片长按菜单同样含评分。macOS/Windows 用 tooltip + 右键菜单。 |

## status

| ID | 现状（按平台，日期取自来源） |
|---|---|
| INPUT-01 | macOS：`--pv-native-input` PASS（2026-10-16/17，含事件类型取值、方向键派发、空态左键、右键菜单、标题栏双击、退出空操作）。iOS/iPadOS：2026-10-17 模拟器两机型 + 真机 `--pv-native-input-check` PASS（27→28→29 checks；HID usage → 共享手势表 47 键往返、合成派发执行共享命令、文本输入获焦与模态展示期间均不派发）。Windows：虚拟键/滚轮经 `WindowsInput` 映射到共享 `AppGesture`，专项事件与手感待 W-M6。Android：2026-10-18 模拟器目视/日志（DPAD Right/F 触发 Next/Fit），程序化断言待 A6。 |
| INPUT-02 | iOS：`--pv-native-input-check` 覆盖文本输入获焦与模态期间不派发（2026-10-17）。macOS/Windows：代码核对；无专项断言。Android：设置 sheet/文本输入让位为目视结论（2026-10-18）。 |
| INPUT-03 | iOS：`--pv-native-gesture-check` 24 checks（2026-10-17，模拟器 + 真机）覆盖双击锚点、捏合、放大态平移、导航图几何与拖动；`--pv-native-input-check` 覆盖轻扫翻页。macOS：`--pv-native-input` 子集 + 真机手感验收（2026-10-16 色彩/画质/手感通过）。Windows：`--pv-native-smoke` 部分覆盖（导航图、EXIF 方向），手势专项待 W-M6。Android：2026-10-18 模拟器目视（缩放/平移/双击/导航图）。 |
| INPUT-04 | macOS：精确/非精确分流已实现，程序化未单列断言。iOS：离散滚轮识别器配置与映射已程序化核对，滚轮方向与"有绑定时不平移"待作者真机（2026-10-17）。Windows/Android：实现上不分精确/离散（缺口，见 questions）。 |
| INPUT-05 | iOS：固定节奏已实现，`UIPress` 无法合成、重复节奏未做程序化断言（2026-10-17）。macOS/Windows/Android：系统重复，无专项断言。 |
| INPUT-06 | iOS：模拟器 + 真机 `--pv-native-input-check` 覆盖（2026-10-17），模拟器另以真实左滑目视；真机手感待作者。Android：2026-10-18 模拟器目视，断言待 A6。 |
| INPUT-07 | macOS：`--pv-native-input` 右键菜单 PASS（2026-10-16/17）。iOS：`--pv-native-input-check` 断言两类菜单与真实长按截图（2026-10-17）。Android：2026-10-18 模拟器目视（长按菜单、返回键关闭）。Windows：右键菜单存在，无专项断言。 |
| INPUT-08 | macOS：设置页程序化核验含真实 `NSEvent` 录制（2026-10-16），真实鼠标拖拽排序作者手验通过；录制手感未单列。Windows：`--pv-native-settings` 7 项 PASS 含"录制状态"（2026-10-17），真实键鼠录制待作者手验。iOS：无录制入口（只读）。Android：未实现，列表用上下按钮（2026-10-18）。 |
| INPUT-09 | 四端实现；`--pv-native-gesture-check` 覆盖 Fit 双向与缩放，预设/比例缩放无逐端断言。 |
| INPUT-10 | macOS：`--pv-native-input` 覆盖空态左键与提示点击穿透（2026-10-16/17）。Windows：`--pv-native-smoke` 未断言空态提示（仅覆盖写回失败提示），属代码核对。iOS/Android：空态点按目视。 |
| INPUT-11 | macOS：`--pv-native-input` 覆盖标题栏双击与区外双击不接管（2026-10-16/17）。Android：返回键先关 sheet 为目视结论（2026-10-18）。iOS/Windows：系统键未专项断言。 |
| INPUT-12 | iOS：卡片菜单评分由 `--pv-native-input-check` 覆盖（2026-10-17）。Android：卡片长按菜单目视（2026-10-18）。焦点顺序无任何平台断言。 |

## questions

1. **菜单项集合三端不一致**：macOS 主图右键菜单没有「增强」项（`PhotoViewer.Mac/Native/MacMainWindow.cs:885-899`），Windows 也没有（`PhotoViewer.Windows/Native/WindowsMainWindow.cs:370-382`），而 iOS（`PhotoViewer.iOS/Native/iOSContextMenus.cs:53-64`）与 Android（`PhotoViewer.Android/Native/AndroidNativeRootView.cs:546-555`）都有。ui-contract 只把 iOS 菜单当作参考。MENU 是否统一包含增强？
2. **术语不一致**：Windows 菜单把「胶片栏」写作「缩略图栏」（`PhotoViewer.Windows/Native/WindowsMainWindow.cs:357`），其余三端与 `01-regions.md` 都用「胶片栏」。
3. **iOS 快捷键只读**：设置页控制分类副标题写明"快捷键只读查看"（`PhotoViewer.iOS/Core/iOSNativeSettingsPresenter.cs:265`），列表只有显隐/排序（`:1465-1477`），与 INPUT-08 的录制规则冲突。是登记为 iOS 平台差异，还是补录制？
4. **滚轮精确/离散未分流**：Windows `FromWheel` 不区分精确滚动（`PhotoViewer.Windows/Native/WindowsInput.cs:125-130`、`WindowsMainWindow.cs:897-914`），触控板两指滚动会执行上一张/下一张；Android 同样（`AndroidNativeRootView.cs:467-475`）。与 INPUT-04 直接冲突。
5. **按键重复范围**：iOS 对所有按住键重复（`iOSNativeRootViewController.cs:735-757`），Android 处理系统 `Multiple`（`AndroidNativeActivity.cs:72-83`），macOS 由系统重复；INPUT-05 要求只对可连续命令重复，四端都要改，属新约束还是保留现状？
6. **评分写回失败的可见性不一致**：iOS 只写日志（`iOSNativeRootViewController.cs:650`），没有可见提示（违反 FILM-06/STATE）；macOS 用模态 `NSAlert`（`MacMainWindow.cs:852-856`）而非 4 s 提示；Windows `StatusToastMilliseconds = 4000`（`WindowsLayout.cs:208`）、Android `StatusDurationMs = 4000`（`AndroidNativeRootView.cs:32`）。STATE 应统一为哪种形态？
7. **空筛选结果文案缺失**：只有 Windows 胶片栏有「无匹配照片」（`WindowsFilmstripView.cs:104`）；macOS/iOS/Android 没有对应空状态文本，与 FILM-05 不符。是缺口还是需求变更？
8. **加载提示延迟无实现**：`Plans/native-rebuild-phase-2-ui.md:117` 只写"加载提示延迟出现"，代码里没有延迟参数或状态（`PhotoLoadSession` 只有 `Error`/`IsEnhancing`）。STATE 若要求延迟提示，需要先定 token。
9. **Android 拖入未接**：计划 §A4 偏差列明"拖入（`OnDragListener`）未接"（`Plans/native-rebuild-phase-android.md:186`、`:250`），而 iOS/macOS/Windows 都实现了拖入 → OPEN 需要登记 `OPEN-xx@android` 或补实现。
10. **前缀登记与新 ID 冲突（跨模块）**：`Design/README.md:57` 写 `ANL-01–04` 并入 `ANALYSIS-02–05`，但本次拆分说明是 `ANL-01` 退役、`ANL-02/03/04 → ANALYSIS-02/03/04`。analysis 模块的编号需在合并前统一。
11. **INPUT-03 的"单击空态"与 iOS 不一致**：iOS 主图点按在细节预览定格时先解除定格（`iOSNativeRootViewController.cs:660-676`），不是每次都打开文件选择器；规则里需要为这一优先级留位置（细节预览归 ANALYSIS）。
12. **Android 手机纵向只能行布局**：`Plans/native-rebuild-phase-android.md:86` 规定纵向恒为行布局，与 SHELL 的"自动按窗口方向"在手机上结果一致，但布局模式设置里选「左右」时是否允许列布局未说明。
