# 设置窗口 — 拆分草稿（settings）

从 [ui-contract.md](../../ui-contract.md)、[P2 UI 方案](../../../Plans/native-rebuild-phase-2-ui.md) §4、[Windows 设置方案](../../../Plans/native-rebuild-windows-settings.md)、[iPad](../../../Plans/native-rebuild-phase-ipad.md)/[Android](../../../Plans/native-rebuild-phase-android.md) 清单与四端源码整理。只作草稿，规则正文见 [settings.md](../settings.md)。

## platforms

**通用链路**：`MainViewModel.OpenSettingWindow()` → `NativeWindowPresenter.ShowSettingsWindow(Settings)` → 平台 `INativeSettingsPresenter.TryPresent`。工作副本统一 `NativeSettingsSession`（`PhotoViewer/Core/Settings/NativeSettingsSession.cs`，`applyRuntimeSettings: false`），关闭时 `Apply()` 一次回写；iOS 自建同构会话（`iOSNativeSettingsPresenter.cs` 内 `iOSNativeSettingsSession`）。

**settings-01 控制列表行**
- `settings-01@mac`：SwiftUI 行＝勾选框 + 名称 + 主/次录制按钮（宽 108，冲突橙描边）+ 行尾 `DragHandle`；按压绑定进入录制；`.onMove` 拖拽排序（实现未限制只能从把手拖，与 Windows 方案 §7.6 的"只从把手拖"表述有出入）。`MacSettingsUI.swift:924-975`。
- `settings-01@win`：`SettingsCard`/`ListView` 行＝`CheckBox` + 名称 + 主/次录制 `Button`（120×32）+ 行尾 `\uE76F` 视觉把手；整行拖（WinUI 无常驻把手元素）。`WindowsSettingsList.cs`、`WindowsHotkeyRecorder.cs`。
- `settings-01@ios`：`iOSNativeEmbeddedCheckableReorderListView<HotkeyItem>`，行只有勾选图标 + 名称，编辑态右侧系统 reorder control；**不显示主/次绑定，也不提供录制**（`iOSNativeSettingsPresenter.cs:1469`，单元格 `:1773-1869`）。一级页副标题写"快捷键只读查看"（`:265`）。
- `settings-01@android`：`MaterialCheckBox` + 名称 + `↑`/`↓` 按钮，无拖拽、无录制（`AndroidNativeSettingsPresenter.cs:319-323`，`:538-544`）。

**settings-02 分类与导航**
- `settings-02@mac`：`NavigationSplitView` 系统侧栏 + 工具栏前进/后退历史（`MacNativeSettingsPresenter.cs:38-133`）；窗口复用，关闭后重建工作副本。
- `settings-02@win`：`NavigationView` 左栏常驻 240 DIP，无历史导航；单例、不进任务栏、不可最大化；主窗口关闭先关设置（`native-rebuild-windows-settings.md` §4/§8.3）。
- `settings-02@ios`：`UITableViewController` 根页（五分类 + 副标题）push 详情；关闭按钮与系统下拉都走 `ApplyPendingChanges`，dismiss 代理挂在 `ViewWillAppear`（`iOSNativeSettingsPresenter.cs:180-236`、`:242-339`）。
- `settings-02@android`：`BottomSheetDialog` 内根页/子页切换，顶部 `‹`/`✕`；下拉、返回键、关闭统一 `session.Apply()`（`AndroidNativeSettingsPresenter.cs:58-99`、`:103-200`）。

**settings-03 外观与几何**
- `settings-03@mac`：SwiftUI 内容 880×720、强制 DarkAqua、系统侧栏、`Form(.grouped)`、前进/后退（`MacSettingsUI.swift:304`，`NativeSettingsAcceptance.cs:21-23`）。
- `settings-03@win`：960×720（min 720×560），Mica，`TitleBar` 48，左栏 240，页宽上限 800，`SettingsCard`；系统 accent，仅内容高亮用应用蓝（`WindowsLayout.cs:483-500`，Windows 方案 §4/§5）。
- `settings-03@ios`：`PageSheet`，medium/large detents、grabber、圆角 24、inset grouped 表格，系统外观（`iOSNativeSettingsPresenter.cs:96-104`、`:367-433`）。
- `settings-03@android`：Material 3 `BottomSheetDialog`，默认半屏、可上拖全屏、顶部把手，系统主题（`AndroidNativeSettingsPresenter.cs:58-83`）。

**settings-04 入口与关闭**
- 入口只有主图菜单：`settings-04@mac` 应用菜单「设置…」`⌘,`（`MacMainWindow.cs:863`）+ 照片右键菜单（`:896`）；`settings-04@win` 照片右键菜单末项（`WindowsMainWindow.cs:377-382`）；`settings-04@ios` 主图长按菜单末项（`iOSNativeRootViewController.cs:686`，menu 顺序见 ui-contract iOS 差异节）；`settings-04@android` 主图长按菜单（`AndroidNativeRootView.cs:555`）。控制栏无设置命令。
- iOS 模态展示期间不派发快捷键（`--pv-native-input-check` 断言）；Android `DispatchKeyEvent` 在 sheet/文本输入期间让位；Windows 设置是独立窗口，主窗口快捷键天然不经过它。

**settings-05 生效**
- 编辑期隔离由 `applyRuntimeSettings: false` 提供（`SettingsViewModel.cs:40`；缓存/解码/键盘映射的副作用都在该开关之后，如 `SettingsViewModel.BitmapCache.cs:107`、`:132`、`:151`）。
- 关闭回写一次：`NativeSettingsSession.Apply()` 有 `_applied` 幂等（`NativeSettingsSession.cs:20-25`）；Windows/iOS/Android 同口径。

**settings-06 文件页**
- 默认值：缓存内存 = min(系统上限/2, 4096) 且 ≥512，缓存数量在 <4096 MB 时 = min(内存/132, 30)，解码线程按内存比例估算（`SettingsViewModel.BitmapCache.cs:44-64`）。
- `settings-06@win`：`NumberBox`（宽 140、内联 spin）+ `Slider`，单位"线程/张"；`settings-06@ios`：`UIStepper`/`UISlider`（内存 512–32768、数量 1–400、延时 100–5000，`iOSNativeSettingsPresenter.cs:1076-1143`）；`settings-06@android`：全部用 +/-（内存步长 512、数量步长 10、阈值步长 1，`AndroidNativeSettingsPresenter.cs:270-273`、`:354-360`）；`settings-06@mac`：SwiftUI 数值/步进行。
- 格式表：iOS 行高 60 带扩展名副标题（`iOSNativeSettingsPresenter.cs:1064`）；EXIF 表 52 单行（`:1407`）；Windows/Android 同样带副文本与把手/上下按钮。

**settings-07 预览页**
- 预设编辑器：`@mac` SwiftUI 行；`@win` `NumberBox`（14→33.3 一位小数显示）+ 删除/添加；`@ios` `UITextField`（DecimalPad）+ 非法输入 `UIAlertController`；`@android` `AlertDialog` 输入。
- 上限：mac 校验 `>0 && <=1600`（`MacNativeSettingsPresenter.cs:368`）；iOS 只校验 `>0`（`iOSNativeSettingsPresenter.cs:1350-1365`）；Windows/Android 未设上限。
- 默认列表在 `SettingsViewModel.ImagePreview.cs:21-30`；新增固定 100%（`:37`）。

**settings-08 控制页**
- 布局选择：`@mac` SwiftUI Picker；`@win` `ComboBox`（宽 240）；`@ios` `UIAlertController` ActionSheet（`iOSNativeSettingsPresenter.cs:1483-1505`）；`@android` `AlertDialog` 列表（`AndroidNativeSettingsPresenter.cs:368-390`）。
- Apple 键盘映射：`@mac` 显示"使用苹果键盘"+ Command/Option/Control 三项（`MacNativeSettingsPresenter.cs:205-209`）；`@win` 整段隐藏（Windows 方案 §7.3）；`@ios`、`@android` 不显示。iOS 的 `UseAppleKeyboard` 默认 true（`SettingsViewModel.Hotkeys.cs:110-117`）。
- 隐藏命令不解除快捷键：手势查询遍历全部 `Hotkeys`（`SettingsViewModel.Hotkeys.cs:242-251`），与 `IsDisplay` 无关。

**settings-09 EXIF 页**
- 四端都有 EXIF 表与两个评分开关；iOS 行高 52 无副标题（`iOSNativeSettingsPresenter.cs:1407-1415`）。
- `ShowRating` 关闭后控制栏评分组与卡片星级同时消失：Android 模拟器目视覆盖；iOS 程序化只覆盖关闭回写后"评分设置传播"，卡片星级显隐未单独断言。

**settings-10 AI 页**
- `settings-10@ios` 只有阈值、最多数量、即时计算三项（`iOSNativeSettingsPresenter.cs:1537-1571`）；`@android` 同样无清库（`AndroidNativeSettingsPresenter.cs:350-365`）。
- `settings-10@mac`：清库前走系统确认框，然后顺序执行共享两段确认命令（`MacNativeSettingsPresenter.cs:296-299`、`:375-382`）；`@win`：`ContentDialog` 确认 + `InfoBar` 状态（Windows 方案 §8.4）。

**settings-11 录制**
- `settings-11@mac`：Swift 侧真实 `NSEvent` 录制，Esc=53、Delete/Backspace=51/117（`MacNativeSettingsPresenter.cs:384-407`）；验收真实事件（`NativeSettingsAcceptance.cs:72-91`）。
- `settings-11@win`：窗口根 `PreviewKeyDown` + `handledEventsToo`，录制吞滚轮/中键/Space/方向键（Windows 方案 §8.2）。
- `settings-11@ios`、`@android`：无录制控件（见 settings-01 平台行）。
- iOS 冲突比较把 Add/Subtract 归一为 OemPlus/OemMinus（`SettingsViewModel.Hotkeys.cs:230-239`）。

**settings-13 无障碍（未专项实现/验收）**
- iOS 勾选态是一个隐藏/显示的 `checkmark` `UIImageView`（`iOSNativeSettingsPresenter.cs:1868`），未显式设置 `AccessibilityValue`；Windows `CheckBox`、Android `MaterialCheckBox`、mac SwiftUI 控件由系统提供语义。四端均无键盘/Tab 专项验收记录。

**settings-14 存储**
- `@win`/`@mac`：JSON 文件（Windows `%LOCALAPPDATA%\PhotoViewer\settings.json`；macOS `ApplicationData` 下同名），临时文件替换写入（`SettingsService.cs:85-181`）。
- `@ios`：`NSUserDefaults` 键 `cc.xiaojh.PhotoViewer.Settings.json`，值是同一份 JSON（`iOSSettingsStorage.cs:12`）。
- `@android`：`SharedPreferences` `cc.xiaojh.PhotoViewer.Settings` 键 `settings_json`（`AndroidSettingsStorage.cs:12-14`）。
- 迁移/补默认：`ToggleDetailView` → `ToggleAnalysisView`（`SettingsViewModel.Persistence.cs:303-308`），缺命令按 id 补（`:320-327`）；预设为空列表时不覆盖默认（`:161-164`）。
- 落盘节流 400 ms（`:22-27`）；失败只写控制台（`:60-72`，`SettingsService.cs:60-76`）。

**settings-15 重排**
- `@mac` 只从把手拖（`.onMove`）；`@win` 整行拖，行尾把手只是视觉；`@ios` 编辑态系统拖拽；`@android` 上下按钮（A4 偏差，`native-rebuild-phase-android.md:250`）。Android 每次移动后重建整页（`AndroidNativeSettingsPresenter.cs:321-322`）。

## status

复制自 ui-contract §4 与各阶段清单，仅作追溯。

- **SETTINGS-01/02 · Windows**：原生已落地（2026-10-17）：五分类、深色 + Mica、`NavigationView` 分类 + `SettingsCard` 卡片行、整行拖排序；`--pv-native-settings` 7 项 PASS（深色/Mica、NavigationView+TitleBar+NumberBox+InfoBar、五分类/尺寸、开关+NumberBox+滑条、重排、录制状态、关闭回写）；真实拖拽、真实键鼠录制、清库确认与预设增删待作者手验。
- **SETTINGS-01/02 · macOS**：SwiftUI 原生设置（macOS 26）程序化核验通过（五分类渲染/历史导航/勾选往返重绘/真实 NSEvent 录制/排序/缩放/关闭回写）；真实鼠标拖拽排序作者 2026-10-16 手工验收通过（程序化注入仍受阻）。
- **SETTINGS-01/02 · iOS/iPadOS**：原生半屏设置页随 I1 落地；I4 起入口为主图长按菜单，关闭回写后布局/控制栏/快捷键/EXIF/评分/格式筛选传播在模拟器 + 真机程序化通过（`--pv-native-input-check`，2026-10-17）；逐条视觉与输入契约待作者手验。
- **SETTINGS-01/02 · Android**：2026-10-18 模拟器目视：五分类半屏 BottomSheet、共享工作副本、关闭/下滑/返回回写；ShowRating 关闭后控制栏评分组与卡片星级即时消失；数值用 +/-、列表用上下排序（拖拽/录制待补）。A6 起 `--es pv-check settings` 在 API 35 arm64 模拟器 PASS 16，但只覆盖设置 sheet 的真实打开/返回关闭与主屏基线，不覆盖逐项编辑。
- **SETTINGS-03 · macOS**：深色外观、SwiftUI 宿主挂载、五分类渲染、历史前进后退、三档窗口尺寸渲染均程序化通过（`NativeSettingsAcceptance`）；视觉细节由作者真屏判断。
- **SETTINGS-03 · Windows**：Mica、`TitleBar`、`NavigationView`、`NumberBox`、`InfoBar` 真实实例化已程序化通过（2026-10-17）；200%/175% DPI、文本缩放与真实跨屏待验。
- **SETTINGS-03 · iOS/Android**：无独立视觉验收项；Android 为模拟器目视，iOS 为模拟器 + 真机功能验收，外观逐条待作者手验。
- **SETTINGS-11 · 录制**：macOS 真实 NSEvent 录制/Esc 取消/Delete 清空程序化通过；Windows 仅"录制状态"程序化断言，真实键鼠录制待作者手验；iOS/Android 无录制。
- **SETTINGS-14 · 关闭回写**：Windows 7 项含"关闭回写一次、重复关闭不重复写"；macOS `NativeSettingsAcceptance` 覆盖关闭后重开不丢行；iOS/Android 覆盖关闭传播。

## questions

1. **iOS 快捷键行不符合 SETTINGS-01**：iOS 行只有勾选 + 名称，既无主/次绑定文本也无录制入口（`PhotoViewer.iOS/Core/iOSNativeSettingsPresenter.cs:1469`，行实现 `:1773-1869`）。一级页副标题自己写着"快捷键只读查看"（`:265`），但连只读绑定都没显示。是触屏简化（应登记 `settings-01@ios`）还是缺口？
2. **移动端没有"清空特征数据库"**：AI 页在 iOS（`iOSNativeSettingsPresenter.cs:1537-1571`）和 Android（`AndroidNativeSettingsPresenter.cs:350-365`）只有阈值/数量/即时计算。SETTINGS-02 把"特征数据库"列为固定分组、SETTINGS-10 要求桌面端提供。移动端永久不提供，还是补一个开发者入口？
3. **预设删空的语义**：代码允许删到空，此时 `ZoomPreset` 什么都不做（`PhotoViewer/ViewModels/Main/ImageViewModel.cs:780-800`）；重启读取时 `model.ScalePresets.Count > 0` 才覆盖默认（`SettingsViewModel.Persistence.cs:161-164`），空列表又变回 8 个默认挡。spec 现写"保留至少一项"，需作者确认是禁止删空、还是允许空且持久化为空。另：iOS 校验只要求 >0（`iOSNativeSettingsPresenter.cs:1350-1365`），mac 限 1600（`MacNativeSettingsPresenter.cs:368`），Windows/Android 无上限——上限是否四端统一？
4. **设置读写失败完全静默**：保存失败只 `Console.WriteLine`（`SettingsViewModel.Persistence.cs:60-72`、`SettingsService.cs:60-76`）；JSON 损坏时按默认值启动并在 400 ms 后把默认值写回，等于覆盖用户原配置（`SettingsService.cs:40-57`）。这两类失败是否需要在设置页或主屏给可见状态（STATE），以及损坏文档是否应备份原文件？
5. **栏位显隐的持久化只有 macOS/iOS**：两端写 `NSUserDefaults` 的 `PhotoViewer.Native.ShowFilm/ShowControls`（`PhotoViewer.Mac/Native/MacMainWindow.cs:106-107`、`:616-617`；`PhotoViewer.iOS/Native/iOSNativeRootViewController.cs:1042-1052`），Windows/Android 未发现对应存储，重启回到默认。01-regions 写"可隐藏，状态持久化"。同一类"视图状态"里，相似面板展开态却在设置 JSON（`SettingsModel.cs:28`）。应由 SHELL 统一规定存储位置，还是各平台自定？
6. **快捷键冲突的派发规则未定**：`GetCommandByGesture` 按列表顺序返回第一条匹配（`SettingsViewModel.Hotkeys.cs:242-251`），允许保存冲突但不说明谁生效。是否把"列表靠前者优先"写成 INPUT 的规则（并在录制时提示）？
7. **iOS 的 Apple 键盘映射不可编辑**：`UseAppleKeyboard` 在 Apple 平台默认 true（`SettingsViewModel.Hotkeys.cs:110-117`），但 iOS 设置页没有"使用苹果键盘/Cmd/Option/Control 映射"段（`iOSNativeSettingsPresenter.cs:1452-1478`）。是刻意只给 macOS，还是 iPad 硬件键盘用户也应能改？
8. **设置窗口外观**：macOS 强制 DarkAqua、Windows 固定深色 + Mica，iOS/Android 跟随系统外观。PRIN-09 的"观片环境固定深色"是否只约束主图？SETTINGS-03 现按"平台外观"处理，需确认设置页在浅色系统下也跟随浅色。
9. **Android 用 +/- 替代拉条、用上下按钮替代拖拽**：上下按钮已登记为 A4 偏差（`native-rebuild-phase-android.md:250`），但 +/- 步进不在任何登记里；Windows 是整行拖（已登记）。哪些算 `settings-06@android` 的合法平台差异，需在 platforms.md 定稿。
10. **设置项与旧壳的差异未核**：旧 Avalonia 设置页有"忽略透明度"字段（`SettingsViewModel.BitmapCache.cs:256-263`，setter 被注释掉，界面已不可用），JSON 中也没有该字段；确认它已彻底退役，不需要在 spec 留 ID。
