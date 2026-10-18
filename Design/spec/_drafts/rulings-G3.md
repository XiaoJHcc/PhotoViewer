# 裁定 G3 · shell / filter（B11 B13 B33 B34 B36 C01–C04 C15–C17 D13–D23）

核对方式：逐条读代码复核（共享 VM / 四个原生壳 / 旧 Avalonia 壳），不采信问题原文。分类按 `Design/README.md`：U 行为差异必须统一、P 登记为 `ID@platform`、X 代码缺陷或规范/实现矛盾（改代码或改规范）。裁定依据只取（多数 / 更优 / 原版）。行号见文末脚注。

| Q | 类别 | Avalonia | mac | win | iOS | android | 裁定 | 依据 | 后续 | 需作者 |
|---|---|---|---|---|---|---|---|---|---|---|
| B11 | X | 手动列布局生效 | 同共享 VM | 同共享 VM | 同共享 VM | 同共享 VM，未强制行 | 允许手动列布局；撤销「手机纵向只走行」说法 | 原版+多数 | platforms.md:96 与 Android 方案 §1.7 改口径；代码不动 | — |
| B13 | P | 会话内，不持久化 | 偏好持久化 | 会话内 | 偏好持久化 | 会话内 | 保持各端现状并正式登记；不把栏位显隐写进设置 JSON | 规范「应当」+已声明差异 | platforms.md 登记 @win/@android；shell.md:58 改成 iOS 持久、Android 不持久 | — |
| B33 | X | 行布局整组居中 | 居中（列布局 34 让位） | 原生居中 | 前导 10 起排 | 前导 10 起排 | 规范已允许；旧契约过期 | 更优（无装饰端不留空带） | ui-contract.md:82 改为前导对齐 | — |
| B34 | P | 自带 StackPanel 居中 | 共享 frames | 自带 StackPanel+10 | 共享 frames | 共享 frames | 登记 @win；几何值与令牌一致，不要求改用共享 frames | 更优（原生排布） | platforms.md 登记；filter.md:83 注明 34 仅 macOS | — |
| B36 | P | 鼠标 32 高 | 鼠标 32 高 | 鼠标 32 高 | 触控 90×32 | 触控 90/84×32 | 登记 @ios/@android，不改视觉几何 | 输入方式不同 | platforms.md 补 FILTER-08@ios/@android | — |
| C01 | X | 落出后 Next→首项、Prev 无动作 | 同（本地 Move） | 同（共享命令） | 同（本地 Move） | 同（本地 Move） | 按 FILTER-05 以重算前索引取最近前/后项 | 规范明确 | 共享 VM 加 MoveCurrent；mac/iOS/Android 改调；Avalonia 复用 | — |
| C02 | X | 映射代表卡片后提前 return | 同共享 VM | 同共享 VM | 同共享 VM | 同共享 VM | 修正后计数必发通知 | 规范同帧一致 | ThumbnailListViewModel:298-309 调整顺序 | — |
| C03 | X | CurrentCulture 字符串排序 | 同共享 VM | 同共享 VM | 同共享 VM | 同共享 VM | 改 OrdinalIgnoreCase，四端同序 | 规范四端一致 | ThumbnailListViewModel:325；Mac 验收预期同步 | — |
| C04 | X | 计数含 FILM-06 暂留项 | 同共享 VM | 同共享 VM | 同共享 VM | 同共享 VM | 规范补「暂留项计入计数」，与可见卡片一致 | 更优（列表与文本一致） | filter.md:54-57、film.md:64 措辞对齐 | — |
| C15 | X | 旧壳无此自绘壳 | 自实现双击 | 系统 caption 区域 | 无标题栏 | 无标题栏 | Windows 已委托系统 caption，无缺口 | 更优（系统原生路径） | platforms.md:104 删除「未见双击」 | — |
| C16 | X | — | 系统交通灯 | 仅 tooltip、无名称 | — | — | 补 AutomationProperties.Name | SHELL-06 可读名称 | WindowsTitleBar.cs:157 加名称 | — |
| C17 | X | — | 按实际布局 | 实际优先、常量兜底 | — | — | 兜底式与按钮令牌同式，合规 | 更优（启动期兜底） | 无需改；SHELL-02 可注一句 | — |
| D13 | X | 逻辑点 | pt | DIP×DPI | pt | dp | 最小尺寸按逻辑点，不随文本缩放放大 | 平台惯例+多数 | shell.md:37 补一句；Windows 溢出归 C11 | — |
| D14 | X | 布局/全屏无过渡 | 同 | 同 | 同 | 同 | 条款保留为「不得新增过渡」 | 更优（防回归） | shell.md:68 改写 | — |
| D15 | X | 无失败路径 | 无失败路径 | 无失败路径 | — | — | 删「全屏失败」提示要求，帧恢复按 SHELL-04 夹取 | 无 API 可报错 | shell.md:70 改写 | — |
| D16 | X | 全局深色 | 主窗+设置深色 | 主窗+设置深色 | 观片面深、设置跟随系统 | 全局深色 | SHELL-05 只约束主窗口；设置/工具归各自规范 | 更优（规范分工） | shell.md:62 加范围句；浅色与否归 D07 | — |
| D17 | X | — | Minimize/None/其余 Zoom | 系统 caption | — | — | 补 Fill（Maximize 已被 Zoom 覆盖），文案列全系统取值 | 系统偏好必须遵循 | MacMainWindow:223-233 加 Fill；shell.md:30 列值 | — |
| D18 | X | — | — | — | 恒隐状态栏 | 系统栏 | 布局已按安全区自动让位，允许未来显示状态栏 | 更优（已实现） | shell.md:33 补一句 | — |
| D19 | X | 不持久化 | 同 | 同 | 同 | 同 | 维持会话级，不新增 JSON 字段 | 多数+原版+规范已定 | 无；filter.md:103 已明确 | — |
| D20 | X | 按代表卡片排序 | 同 | 同 | 同 | 同 | 维持代表文件口径，写进规范 | 多数+PRIN-06 | filter.md:34 补「大小取代表文件」 | — |
| D21 | X | 冲突项恒显示 | 同 | 同 | 同 | 同 | 维持恒显示、命中为空 | 规范已定 | 无 | — |
| D22 | X | 星级筛选不受 ShowRating 影响 | 同 | 同 | 同 | 同 | 维持可用 | 规范已定（应当） | 无 | — |
| D23 | X | 基线宽度 | 自然宽 +3.5pt | 同文案 | 同文案 | 同文案 | 合规差异，冻结基线不作验收标准 | 规范自然宽度 | 无代码；filter-notes 结案 | — |

统计：U 0 / P 3 / X 20；需作者 0。

## 脚注（证据行号）

- **B11**：`PhotoViewer/ViewModels/Main/MainViewModel.cs:277-286`（LayoutMode→IsRowLayout，手动态不随尺寸变）、`:300-315`（Auto 只按横竖）；`PhotoViewer.Mac/Native/MacMainWindow.cs:430`、`PhotoViewer.Windows/Native/WindowsMainWindow.cs:498`、`PhotoViewer.iOS/Native/iOSNativeRootViewController.cs:422`、`PhotoViewer.Android/Native/AndroidNativeRootView.cs:264-267` 都只读 `_main.IsRowLayout`；Android 设置里 `PhotoViewer.Android/Native/AndroidNativeSettingsPresenter.cs:368-396` 提供全部三种模式。与 `Design/platforms.md:96`、`Plans/native-rebuild-phase-android.md:86` 的「纵向只走行」不符。
- **B13**：`PhotoViewer/ViewModels/Main/MainViewModel.cs:326-337`（默认 true、无持久化）、`PhotoViewer/Core/Settings/SettingsModel.cs:9-47`（无栏位字段，仅 `:28` 相似面板）；mac 读 `PhotoViewer.Mac/Native/MacMainWindow.cs:104-106`、写 `:616-617`；iOS 恢复 `PhotoViewer.iOS/Native/iOSNativeRootViewController.cs:1038-1052`、保存调用 `:830`；Windows 会话态 `PhotoViewer.Windows/Native/WindowsMainWindow.cs:597-620`；Android 会话态 `PhotoViewer.Android/Native/AndroidNativeRootView.cs:269`。规范 `Design/spec/shell.md:43,58`。
- **B33**：iOS `PhotoViewer.iOS/Native/iOSFilterBar.cs:93-105`（`alignLeading: true`）；规范 `Design/spec/filter.md:86` 已允许；旧契约 `Design/ui-contract.md:82` 仍写横向居中；Avalonia 居中见 `PhotoViewer/Views/Main/File/FilterBarView.axaml:47-51`。
- **B34**：`PhotoViewer.Windows/Native/WindowsFilterBar.cs:60-68`（StackPanel + Margin 10）、`:86-99`（换向只改 Orientation）、`:111-119`（90×32）；常量 `PhotoViewer.Windows/Native/WindowsLayout.cs:22-31`；规范 `Design/spec/filter.md:81-85`、`Design/spec/shell.md:29`（SHELL-02@win）。
- **B36**：iOS `PhotoViewer.iOS/Native/iOSFilterBar.cs:100-104`（frame 90×32）、`:114-129`（UIButton 无命中区外扩）；Android `PhotoViewer.Android/Native/AndroidFilterBar.cs:141-160`（命中区=共享 frames）、`:89-93`（行布局 90，窄屏 84/76）；规范 `Design/spec/filter.md:97`，44 pt 条款仅限窗口级控件 `Design/spec/shell.md:69`。
- **C01**：`PhotoViewer/ViewModels/Main/File/ThumbnailListViewModel.cs:355-368`（IndexOf=-1 时 HasNext 真、HasPrevious 假）、`PhotoViewer/ViewModels/Main/ControlViewModel.cs:278-300`（Next 取 `[0]`）；mac `PhotoViewer.Mac/Native/MacMainWindow.cs:566-572`、iOS `PhotoViewer.iOS/Native/iOSNativeRootViewController.cs:637-647`、Android `PhotoViewer.Android/Native/AndroidNativeRootView.cs:637-647` 各自复制同一算法；Windows 走共享命令 `PhotoViewer.Windows/Native/WindowsMainWindow.cs:283-290`；规范 `Design/spec/filter.md:65`。
- **C02**：`PhotoViewer/ViewModels/Main/File/ThumbnailListViewModel.cs:294`（先赋 FilteredFiles）、`:298-308`（映射代表后 return）、`:309`（计数通知被跳过）。
- **C03**：`PhotoViewer/ViewModels/Main/File/ThumbnailListViewModel.cs:325-326`（默认 CurrentCulture 比较）；Mac 验收同口径 `PhotoViewer.Mac/Native/NativeFilmstripAcceptance.cs:34`；规范 `Design/spec/filter.md:31`。
- **C04**：`PhotoViewer/ViewModels/Main/File/ThumbnailListViewModel.cs:64-67`（暂留字段）、`:272-282`（暂留项无条件命中）、`:309`；计数取 `FilteredFiles.Count` `PhotoViewer/ViewModels/Main/File/FilterBarViewModel.cs:89`；规范 `Design/spec/filter.md:54-57`、`Design/spec/film.md:64`。
- **C15**：`PhotoViewer.Windows/Native/WindowsMainWindow.cs:659-714`（`NonClientRegionKind.Caption` 登记/清空，`:669`、`:713`），按钮区独立 `PhotoViewer.Windows/Native/WindowsTitleBar.cs:56-59`；系统 caption 自带拖动与双击最大化。`Design/platforms.md:104` 记载与此相反。
- **C16**：`PhotoViewer.Windows/Native/WindowsTitleBar.cs:132-169`，仅 `:157` 设 tooltip，无 `AutomationProperties.Name`；规范 `Design/spec/shell.md:67`、`Design/platforms.md:112`。
- **C17**：`PhotoViewer.Windows/Native/WindowsTitleBar.cs:84-103`（常量兜底 = 4×(36+8)+4）；令牌 `PhotoViewer.Windows/Native/WindowsLayout.cs:228-252`；正常路径取实际布局 `PhotoViewer.Windows/Native/WindowsMainWindow.cs:678-679`；规范 `Design/spec/shell.md:32`。
- **D13**：令牌 `Design/tokens.json:32-50`（pt）、`Design/spec/shell.md:37`；Windows 最小尺寸按栅格化比例换算 `PhotoViewer.Windows/Native/WindowsMainWindow.cs:481-487`、`PhotoViewer.Windows/Native/WindowsLayout.cs:216-225`；mac 用点 `PhotoViewer.Mac/Native/MacMainWindow.cs:109-112`；四壳均无文本缩放分支。
- **D14**：`Design/spec/shell.md:68`；mac 布局直接落 frame `PhotoViewer.Mac/Native/MacMainWindow.cs:432-520`；Windows 直接重建行列 `PhotoViewer.Windows/Native/WindowsMainWindow.cs:512-595`；iOS/Android 同（无过渡 API 调用）。
- **D15**：`Design/spec/shell.md:70`；mac 全屏 `PhotoViewer.Mac/Native/MacMainWindow.cs:207-212`、`:872`（无返回值）；Windows `PhotoViewer.Windows/Native/WindowsMainWindow.cs:435-459`（presenter 无失败回调）；帧恢复由 `Design/spec/shell.md:43,46` 的夹取兜底。
- **D16**：`Design/spec/shell.md:62`；`Design/README.md:35`（设置/工具遵循平台规范）；Avalonia `PhotoViewer/App.axaml:5`；mac `PhotoViewer.Mac/Native/MacMainWindow.cs:109`、`PhotoViewer.Mac/Native/SwiftSettings/MacSettingsUI.swift:309`；Windows `PhotoViewer.Windows/Native/WindowsSettingsWindow.cs:51`、`PhotoViewer.Windows/Native/WindowsToolsWindow.cs:53`；iOS 工具 `PhotoViewer.iOS/Native/iOSToolsPresenter.cs:81`（设置未强制，跟随系统）；Android `PhotoViewer.Android/Resources/values/styles.xml:19,29`。
- **D17**：`PhotoViewer.Mac/Native/MacMainWindow.cs:222-233`（Minimize/None/其余 `Zoom`）；本机 macOS 26 系统设置导出值含 `Fill`、`Maximize`、`Minimize`、`None`（`/System/Library/ExtensionKit/Extensions/DesktopSettings.appex` 资源字符串）；规范 `Design/spec/shell.md:30`；Windows 由系统 caption 原生处理。
- **D18**：`PhotoViewer.iOS/Native/iOSNativeRootViewController.cs:282-287`（`PrefersStatusBarHidden => true`）、`PhotoViewer.iOS/Info.plist:52,68`；布局用真实安全区 `PhotoViewer.iOS/Native/iOSNativeRootViewController.cs:417-430`；规范 `Design/spec/shell.md:33`。
- **D19**：`PhotoViewer/ViewModels/Main/File/FilterBarViewModel.cs:51-70`（三项初值）；`PhotoViewer/Core/Settings/SettingsModel.cs:9-47`（无字段）；规范 `Design/spec/filter.md:103` 明确不新增 JSON 字段。
- **D20**：`PhotoViewer/ViewModels/Main/File/ThumbnailListViewModel.cs:201-260`（同名分组、按格式顺序选代表）、`:322-334`（按代表 Name/FileSize 排序）；规范 `Design/spec/filter.md:34`。
- **D21**：`PhotoViewer/ViewModels/Main/File/FilterBarViewModel.cs:48`（选项恒存在）、`PhotoViewer/ViewModels/Main/File/ThumbnailListViewModel.cs:281`（`Conflict` 命中条件）；规范 `Design/spec/filter.md:48`。
- **D22**：`PhotoViewer/ViewModels/Settings/SettingsViewModel.Rating.cs:12`（ShowRating 只控渲染）；`PhotoViewer/ViewModels/Main/File/FilterBarViewModel.cs:34-49`（筛选项与 ShowRating 无绑定）；规范 `Design/spec/filter.md:50`。
- **D23**：`PhotoViewer.Mac/Native/MacMainWindow.cs:432-433`（`SizeToFit` 自然宽）、`:665`（文案）；`Plans/native-rebuild-phase-3.md:73`（约 3.5 点）；规范 `Design/spec/filter.md:89`（自然宽度、不设固定宽）。
