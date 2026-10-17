# Windows 原生设置窗口方案（W-M5）

> 2026-10-17 设计定稿并实施；同日完成 Win11 视觉重构。隶属 [native-rebuild-windows-p2.md](native-rebuild-windows-p2.md) §6 **W-M5**；交互契约 [SETTINGS-01/02](../Design/ui-contract.md)；共享工作副本 [NativeSettingsSession.cs](../PhotoViewer/Core/Settings/NativeSettingsSession.cs)。
>
> 作者口径：**不要混搭新旧控件，保证一个软件内设计风格完全统一；验收只在 Win11，设计语言以最新 Win11 规范为准，旧系统自动回退、不设验收限制。** 本方案据此采用 Win11 设置页结构（Mica + TitleBar + NavigationView + SettingsCard），页面内容仍用 C# 构建。
>
> **实施结果（2026-10-17）**：`--pv-native-settings` **7 项 PASS**、主冒烟 24 项无回归。**根因纠正**：此前 agent 报告的「本工程无法加载 `NavigationView`/`TitleBar`/`NumberBox`/`TextBox`/`InfoBar`」并非控件限制，而是工程没有 `App.xaml`/`IXamlMetadataProvider`，导致 `XamlControlsResources` 无法装载；补根 `App.xaml` 后这些控件全部可用。实际实现已回到本方案原结构，并改用 `CommunityToolkit.WinUI.Controls.SettingsControls` 的 `SettingsCard`。详见 §16。

---

## 1. 目标

1. 五分类 **文件 / 预览 / 控制 / EXIF / AI** 原生呈现，分类、分组、字段与 iOS 原生设置页逐项对齐（SETTINGS-02），只换 Windows 外壳。
2. 交互完整：显隐勾选、拖拽排序、快捷键录制与冲突高亮、滑块/数值联动、缩放预设增删、清库二次确认、关闭回写。
3. 视觉统一：全部使用 WinUI 3 与 WinUI 主题资源；Win11 设置页结构（Mica、标题栏、左栏、设置卡片），不出现 Win10 时代壳控件（`CommandBar`/`AppBarButton`/`MenuBar`/`Pivot`/`Hub`/`MessageDialog`）、不出现第二种灰或第二种圆角。
4. **验收只在 Win11**：设计与交互按最新 Win11 规范；旧版系统由平台自动回退，不设验收项，也不为其改设计或加分支。

## 2. 非目标

- 不跟随系统亮/暗：应用级固定深色（`Application.RequestedTheme="Dark"`），与主窗口一致。
- 不改设置 JSON 字段名、默认值、共享 `SettingsViewModel` 语义；不新建第二份命令表。
- 不做工具页（EXIF 详情 / 照片统计 / DINO 诊断）；不迁移 Avalonia 旧壳设置页（旧壳不做产品验收）。
- 不做前进/后退历史导航（macOS 26 系统设置专属形态，Windows 设置左栏常驻）。
- 不做搜索框、面包屑与「关于」页；当前五页均为顶层页，不需要返回栈。

## 3. 设计决策

| # | 决策 | 理由 |
|---|---|---|
| D1 | **控件世代统一**：全部 WinUI 3；禁用 Win10 壳控件与 `Windows.UI.Xaml` 旧命名空间 | 主窗口已经是 WinUI 3；混入旧壳控件会产生两套 hover/focus/圆角语言 |
| D2 | **结构**：独立顶层窗口 + `TitleBar` + `NavigationView`(Left) + 页面 `ScrollViewer` + `SettingsCard`；不用 `ContentDialog` 承载整页 / `TabView` / Win32 属性表 | 与 Win11 设置页一致；`ContentDialog` 只用于清库确认 |
| D3 | **皮肤**：颜色、字号、圆角、卡片材质全部取 WinUI 主题资源（`CardBackgroundFillColorDefaultBrush`、`TextFillColorPrimaryBrush`、`ControlCornerRadius` 等），不再维护 `#111/#222/#333` 第二套灰度 | 卡片/控件状态/强调色/深色回退由 WinUI 统一；应用自有颜色只留给内容高亮 |
| D4 | **使用 Mica**（2026-10-17 修订） | 原方案担心「像系统设置而非本应用」；视觉验收显示无 Mica 的纯深色底明显缺少 Win11 观感。现用 `MicaBackdrop`，靠本应用的分类、文案与主窗口一致的内容保持身份。旧系统由 `MicaBackdrop` 自动回退，不写 OS 分支 |
| D5 | **验收基线 Win11，旧系统不设门**：不抬系统、不加分支；设计不为旧系统让步 | 验收只在 Win11；旧系统由平台自动回退，不纳入验收，也不写条件分支 |
| D6 | **强调色**：原生控件用系统 accent；应用 `#1E90FF` 只作内容高亮（当前照片描边等），不覆盖控件 accent | 原生选中/开关/滑条跟随系统 accent；强行改色会破坏无障碍与系统一致性 |
| D7 | **标题栏**：WinUI `TitleBar` 控件 + `ExtendsContentIntoTitleBar`，齿轮图标 + “设置”，caption 按钮透明融入 Mica | 与 Win11 设置页一致；自绘 chrome 只保留主窗口一套 |
| D8 | **页面仍纯 C# 构建，不写页面 XAML**；根 `App.xaml` 只承载应用级 `XamlControlsResources` 与深色主题 | 与主窗口、macOS 程序化 AppKit 一致；`App.xaml` 是 WinUI 控件模板/`IXamlMetadataProvider` 的必要引导，不承载控件布局 |
| D9 | **设置卡片用 CommunityToolkit `SettingsCard`**（`CommunityToolkit.WinUI.Controls.SettingsControls` 8.2.251219） | 与 Win11 设置页同源卡片视觉，避免手写 Border 复刻材质/描边/无障碍/点击态 |

## 4. 窗口结构

```
Window 960×720（min 720×560，Mica，标准边框 + 系统 caption）
├─ TitleBar 控件（48，透明，齿轮图标 + “设置”）
└─ NavigationView（Left，240，透明内容岛）
   ├ 文件 / 预览 / 控制 / EXIF / AI（16px 图标；系统 accent 选中药丸 + 左缘指示条）
   └ Content（透明）
      └ 页面 ScrollViewer（透明）
         └ StackPanel（MaxWidth 800，Padding 36/28/36/36）
            ├ Title 28/36 SemiBold
            ├ BodyStrong 14/20 组标题
            └ SettingsCard × N（主题卡片，MinHeight 64，4 圆角，4 卡间距）
```

- `ExtendsContentIntoTitleBar = true` + `Window.SetTitleBar(titleBar)`；`TitleBar.Title = "设置"`，`IsBackButtonVisible = false`，`IsPaneToggleButtonVisible = false`，`IconSource = FontIconSource(E713)`。
- `NavigationView`：`PaneDisplayMode = Left`、`IsPaneToggleButtonVisible = false`、`IsSettingsVisible = false`、`IsBackButtonVisible = Collapsed`、`CompactModeThresholdWidth = 0`、`ExpandedModeThresholdWidth = 0`、`OpenPaneLength = 240`、`IsPaneOpen = true`、`IsTitleBarAutoPaddingEnabled = false`。
- 5 个 `NavigationViewItem` 用 `FontIcon`（16px，字体族统一走 `WindowsIcons` 的 fallback 链，不在页面里硬编码字体名）；选中项 `Tag` = 页索引，与页面 `Visibility` 同步；默认选中「文件」。
- 覆盖 `NavigationViewContentGridBorderThickness=0`、`NavigationViewContentGridCornerRadius=0`、`NavigationViewContentBackground=透明`，让 Mica 平铺到内容区。
- 窗口：`OverlappedPresenter` 可缩放、不可最大化；`AppWindow.IsShownInSwitchers = false`（不进任务栏/Alt+Tab），打开时相对主窗口居中；单例，再次打开只 `Activate()`。
- 主窗口关闭时先关设置窗口，避免孤儿窗口与未回写的工作副本。

## 5. 配色与版式

**配色（全部取 WinUI 主题资源）**

| 用途 | 资源键 |
|---|---|
| 窗口/页面底 | Mica（`MicaBackdrop`）+ 透明内容底 |
| 卡片底 | `CardBackgroundFillColorDefaultBrush` |
| 卡片描边 | `CardStrokeColorDefaultBrush` |
| 主文字 | `TextFillColorPrimaryBrush` |
| 副文字 / 描述 | `TextFillColorSecondaryBrush` |
| 三级文字 / 把手 | `TextFillColorTertiaryBrush` |
| 分隔线 | `DividerStrokeColorDefaultBrush` |
| 控件/按钮底与描边 | `ControlFillColorDefaultBrush` / `ControlStrokeColorDefaultBrush` |
| 警告 / 错误 | `SystemFillColorCautionBrush` / `SystemFillColorCriticalBrush` |
| 强调 / 选中 | 系统 accent（`AccentFillColorDefaultBrush` 等） |

**版式**

| 项 | 值 |
|---|---|
| 页面标题 | `TitleTextBlockStyle`（28/36，SemiBold） |
| 组标题 | `BodyStrongTextBlockStyle`（14/20，SemiBold） |
| 行标题 / 描述 | `BodyTextBlockStyle`（14/20）/ `CaptionTextBlockStyle`（12/16，副文字色） |
| 卡片 | `SettingsCard` 主题样式，4 圆角，MinHeight 64，Padding 16/12 |
| 卡间距 / 组间距 / 页边距 | 4 / 24 / 36（顶 28） |
| 页宽上限 | 800 |
| 导航栏宽 | 240 |
| 数值框 | `NumberBox`，宽 140，`SpinButtonPlacementMode=Inline` |
| 快捷键按钮 | 120×32，默认 Fluent Button；冲突/录制只改 border/foreground |
| 清库状态 | `InfoBar`（`IsClosable=false`，Information） |

## 6. 控件映射

| 设置元素 | 控件 | 说明 |
|---|---|---|
| 分类导航 | `NavigationView` + `NavigationViewItem` | 左栏常驻，无汉堡/返回 |
| 窗口标题 | `TitleBar` | 齿轮图标 + `SetTitleBar`，caption 透明 |
| 设置行 | `SettingsCard`（CommunityToolkit） | Header/Description 左，Content 右；整块列表/滑块用 Vertical |
| 布尔（带描述） | `ToggleSwitch` | 清空 `OnContent`/`OffContent`，`MinWidth = 0`；整卡可点 |
| 列表行内勾选 | `CheckBox` | 行首，`MinWidth = 0` |
| 可排序表 | `ListView` | `SelectionMode = None`、`ReorderMode = Enabled`、`AllowDrop/CanReorderItems = true`；行尾把手字形 `\uE76F` |
| 枚举 | `ComboBox` | 右对齐，宽 240 |
| 整数（线程/预载/数量） | `NumberBox` | `SpinButtonPlacementMode = Inline`，带“线程/张”单位文本 |
| 数值（缓存/阈值/延时） | `Slider` | 标题 + 右侧数值（放在 Content 垂直布局的顶部行），拉条在下 |
| 快捷键绑定 | 自绘 `Button` | Windows 无录制控件；录制逻辑见 §8.2 |
| 缩放预设 | `NumberBox` + 删除/添加按钮 | 一行一个预设，底部「添加预设」 |
| 清库确认 | `ContentDialog` | `XamlRoot` = 设置窗口内容 |
| 清库状态 | `InfoBar` | `IsClosable = false`；VM 无级别字段，统一 Information，不猜级别 |

## 7. 页面内容

### 7.1 文件

- 支持的文件格式：可排序表（`CheckBox` + 格式名 + 扩展名副标题 + 把手），整表一张 `SettingsCard`（`Padding=4`）。
- 同名文件视为同一张图片：卡片 + `ToggleSwitch` + 描述。
- 缓存与预加载：原生/软件解码线程（`NumberBox`）、缓存内存上限（`Slider` + 数值）、缓存数量上限（`Slider` + 数值）、上一张/下一张/列表滚动预载（`NumberBox`）、滚动预载延时（`Slider`）；下方预算/缓存提示沿用共享文本，按级别取 `SystemFillColor*` / 副文字色。

### 7.2 预览

- 缩放时显示指示器：卡片 + `ToggleSwitch`。
- 缩放比例预设：`NumberBox`（宽 140）+ `%` + 删除图标按钮；底部「添加预设」。`NumberBox` 的 Enter/失焦提交语义替代旧壳的 `DeferredNumericTextBox`；显示按 1 位小数（33.333 → 33.3），不做两段式编辑态。

### 7.3 控制

- 控制栏布局位置：`SettingsCard` + `ComboBox`，描述用当前项的 `Description`。
- 控制栏功能/快捷键：可排序表，行 = 勾选 + 名称 + 主录制按钮 + 次录制按钮 + 把手；录制态、冲突色、清空态由行订阅 `HotkeyItem.PropertyChanged` 刷新。
- **不显示「苹果键盘映射」整段**（Apple 专属，`UseAppleKeyboard` 在 Windows 恒 false）。

### 7.4 EXIF

- 显示/收纳：可排序表（勾选 + 字段名 + 把手），整表一张 `SettingsCard`。
- 评分与写回：`ShowRating`、`SafeSetRating`（带描述）；安卓 USB 提示不显示。

### 7.5 AI

- 相似聚类：阈值 `Slider`（75–95%）、最多数量 `NumberBox`（1–32）。
- 分析诊断：即时计算 DINO/CV 的 `ToggleSwitch`（P3 已落地，不是禁用项）。
- 特征数据库：清库按钮 + `ContentDialog` 二次确认 + `InfoBar` 状态；执行时走共享 `ClearDatabaseCommand` 的两段确认（先 arm 再执行），按钮按 `ClearDbBusy` 禁用。

### 7.6 与 macOS/iOS 的差异（登记 ui-contract）

| 项 | macOS / iOS | Windows |
|---|---|---|
| 分类导航 | `NavigationSplitView` / 半屏 sheet | `NavigationView` 左栏常驻，无前进/后退 |
| 行布局 | `Form` 行（标题左、控件右） | `SettingsCard` 卡片式行（同语义，Win11 卡片口径） |
| 排序 | 只从右侧把手拖（`.onMove`） | 整行拖；行尾把手只是视觉锚点 |
| 键盘映射 | 显示「使用苹果键盘」段 | 隐藏（Apple 专属） |
| 强调色 | 系统强调色 | 原生控件系统 accent；应用蓝仅内容高亮 |
| 材质 | AppKit / UIKit 系统材质 | Mica + WinUI 主题卡片；旧系统自动回退 |

## 8. 关键交互

### 8.1 拖拽排序（三张表）

- `ListView.ItemsSource` **直接放 `WindowsSettingsRowElement`（UIElement 行）**；共享集合只作为 `Item` 回写目标。原因：本工程 `ContainerContentChanging` 里替换 `Container.Content` 不显示（`Handled=true` 也一样）。
- 行容器只改 `Padding = 0`、`MinHeight = 0`、`HorizontalContentAlignment = Stretch`；拖后按行顺序逐项 `Move` 回写共享集合，`CollectionChanged` 负责落盘。
- 表固定高度 = 行数 × 行高，`ScrollViewer.SetVerticalScrollMode(list, Disabled)`、`HorizontalScrollMode = Disabled`，滚动交给外层页面。
- **行为差异**：WinUI 没有常驻把手元素，因此是**整行拖**；行尾 `\uE76F` 只是视觉把手，拖它等于拖整行。此差异登记进 SETTINGS-01 的 Windows 列。

### 8.2 快捷键录制

- 行内 `Button` 显示 `AppGesture.ToDisplayString()`（Windows 下 `UseAppleKeyboard = false`，输出 `Ctrl`/`Alt`/`Win`；鼠标为「滚轮上 / 中键 / 侧键」）；点击进入录制态，文案「按下按键…」。
- 捕获挂在窗口根：`PreviewKeyDown` + `AddHandler(KeyDownEvent, …, handledEventsToo: true)`；`e.Key` + `WindowsInput.CurrentModifiers()` → `AppGesture`；Esc 取消、Delete/Backspace 清空。
- 修饰键单独按不会触发 `KeyDown`，靠 `GetKeyState`（`WindowsInput` 已封装）；录制态吞掉 `PointerWheelChanged`（避免页面滚动）与中键 `PointerPressed`。
- 录制期间把 Space/方向键在预览阶段标记 `Handled`，不让 ListView/NavigationView 拿去滚屏或移焦点；主窗口快捷键天然不受影响（独立窗口）。
- 结束后 `Settings.CheckHotkeyConflicts()`，冲突行按钮 border/foreground 用 `SystemFillColorCautionBrush`。

### 8.3 生命周期与回写

1. `WindowsNativeApplication` 构造 `WindowsNativeSettingsPresenter`（懒建窗口）。
2. `MainViewModel.OpenSettingWindow()` → `NativeWindowPresenter.ShowSettingsWindow(settings)` → presenter `TryPresent(settings)`；**只走这一条路**。
3. `TryPresent`：已打开则 `Activate()`；否则 `new NativeSettingsSession(settings)`、建窗口、选中「文件」、`Activate()`。
4. `Closed`：`session.Apply()` → `session.Dispose()` → 清空引用；重复关闭只回写一次。
5. 主窗口 `Closed`：先关闭设置窗口（保证回写），再走原有 `Detach()`。
6. 关闭即应用，**没有 Apply/Cancel 按钮**（与 macOS/iOS 同口径）；页面内改动只写工作副本。

### 8.4 清库

`ContentDialog`（Primary=清除、Close=取消、`DefaultButton = Close`）→ 确认后按共享两段确认执行 `ClearDatabaseCommand`；`ClearDbBusy` 期间按钮禁用；结果写 `InfoBar`（`IsOpen = 有状态文本`），不伪造成功。

## 9. 实现分解

| 文件 | 职责 |
|---|---|
| `PhotoViewer.Windows/App.xaml` / `App.xaml.cs` | WinUI `Application` + `XamlControlsResources` + 深色主题；`OnLaunched` 装配门面、`MainViewModel`、主窗口、外部打开。 |
| `PhotoViewer.Windows/Native/WindowsNativeApplication.cs` | `Application.Start` + DispatcherQueue/同步上下文、门面注入、窗口句柄。 |
| `PhotoViewer.Windows/Native/WindowsNativeSettingsPresenter.cs` | `INativeSettingsPresenter`：工作副本、单例、打开/关闭回写、生命周期 |
| `PhotoViewer.Windows/Native/WindowsSettingsWindow.cs` | 窗口壳：Mica、`TitleBar`、`NavigationView`、页面宿主、关闭事件、尺寸/居中 |
| `PhotoViewer.Windows/Native/WindowsSettingsCard.cs` | 页面/分组标题、`SettingsCard`、开关/滑块行、提示、分隔线、把手、图标按钮 |
| `PhotoViewer.Windows/Native/WindowsSettingsList.cs` | 可排序表：`ListView` 装配、UIElement 行、把手、容器属性、空态 |
| `PhotoViewer.Windows/Native/WindowsHotkeyRecorder.cs` | 录制按钮、捕获、格式化、冲突态 |
| `PhotoViewer.Windows/Native/WindowsSettingsPage.File.cs` 等五个 partial | 五页内容：`File` / `Preview` / `Control` / `Exif` / `Ai` |
| `PhotoViewer.Windows/Native/WindowsSettingsAcceptance.cs` | `--pv-native-settings` 定向验收（隔离设置、自检、收尾还原） |
| `PhotoViewer.Windows/Native/WindowsLayout.cs` | 设置窗口几何：窗口/导航/标题栏/页宽/卡片圆角/行高/NumberBox 宽 |
| `Directory.Packages.props` | `Microsoft.WindowsAppSDK` + `CommunityToolkit.WinUI.Controls.SettingsControls` 版本 |

## 10. 系统基线与回退

**设计与验收只按 Win11 当前版本（Fluent 2 / WinUI 3 1.8）**；旧版系统由平台自动回退，不纳入验收，也不为其写条件分支。

| 项 | Win11（验收基线） | 旧系统 |
|---|---|---|
| 控件 / 字号 / 圆角 / 间距 | 最新 Win11 规范 | 由 WinUI 3 与 OS 自行回退 |
| 窗口圆角、caption 按钮 | Win11 原生 | 由 OS 决定，不验收 |
| Mica | 验收项（`MicaBackdrop`） | 自动回退为纯色，不设验收 |
| 字体 / 图标 | Segoe UI Variable / Segoe Fluent Icons | 系统回退；图标字体族走 WinUI 的 fallback 链 |
| `TitleBar` 控件 | 验收项 | 不在验收范围 |

## 11. 风险与验证门

| 风险 | 等级 | 验证门 / 缓解 |
|---|---|---|
| 缺 `App.xaml` 的纯 C# 工程没有 `IXamlMetadataProvider`，Fluent 控件模板不可用 | 高（已发生） | 保留根 `App.xaml` + `XamlControlsResources`；`--pv-native-settings` 断言 `NavigationView`/`TitleBar`/`NumberBox`/`InfoBar` 真实实例化并完成布局 |
| `SettingsCard` 内容默认右对齐导致列表缩到右侧 | 中（已发生） | 整块列表/滑块行 `ContentAlignment=Vertical` + `HorizontalContentAlignment=Stretch`；截图验收 |
| `NumberBox` 内联 spin 按钮挤压数值 | 中（已发生） | 宽 140；预设按 1 位小数；截图验收 |
| `NavigationView` 内容岛圆角边框破坏 Mica 平铺 | 低（已解决） | 覆盖三个内容岛资源；截图验收 |
| `TitleBar`/Mica 在 Win10 回退 | 低（不验收） | 使用平台自带回退，不加代码分支 |
| 真实拖拽排序/录制手感 | 中 | 程序化断言状态 + 作者手验 |
| 设置窗口成为孤儿或未回写 | 中 | 主窗口关闭先关设置；`Closed` 幂等回写；退出路径自检 |
| 720 宽下快捷键表拥挤 | 低 | 名称 `NoWrap`+Ellipsis；720 宽截图人工检查；不追求 Win11 默认 960 之外的战术布局 |
| 缺 `App.xaml` 时 `Application.Resources` 访问时机 | 中 | 只在 `App.InitializeComponent()` 之后读取；helper 缺失资源即抛异常，不静默回退 |

## 12. 验收

**程序化（`--pv-native-settings`，隔离设置/数据库）**

1. 深色主题 + `MicaBackdrop`；`NavigationView` / `TitleBar` / `NumberBox` / `InfoBar` 真实实例化。
2. 五分类可切换，页面标题/组标题/卡片数量与 VM 一致；960 DIP 外层尺寸。
3. 勾选/下拉/滑块/`NumberBox` 往返；关闭后重开恢复；主窗口控制栏按回写重建。
4. 三张表：重排后共享集合顺序与设置 JSON 一致；行内勾选可点。
5. 录制：键盘映射写入主/次绑定，冲突标记出现；Esc 取消、Delete 清空（程序化驱动映射函数 + UI 状态，真实按键由作者手验）。
6. 清库：取消不执行；确认后状态可见、`ClearDbBusy` 期间按钮禁用。
7. 关闭回写一次、重复关闭不重复写；主窗口关闭时设置窗口同步关闭。

**人工（作者）**

- Win11 截图：Mica、卡片、字号、图标、caption、导航选中态、无第二种灰/圆角（旧系统不验收）。
- 真实鼠标拖拽排序手感；快捷键录制（含滚轮/中键）；清库确认；预设增删；125/150/200% 文本缩放。

## 13. 待作者确认

1. 设置入口只放右键菜单「设置…」（推荐），还是再加控制栏命令？
2. 窗口不可最大化（推荐）、不进任务栏（`IsShownInSwitchers = false`）、随主窗口关闭，是否接受？
3. 原生控件使用系统 accent（推荐），主窗口的 `#1E90FF` 仅作内容高亮，是否接受？
4. 排序是整行拖 + 行尾把手视觉（WinUI 无原生把手），是否接受？
5. 使用 Mica（推荐）还是恢复纯深色应用底？当前按 Win11 验收基线使用 Mica。

## 14. 文档同步（实现收尾必须一起改）

- `PhotoViewer.Windows/AGENTS.md`：文件清单、WinUI 硬约束、设置窗口验收。
- [Design/architecture.md](../Design/architecture.md) §5.8：设置窗口原生链路一句话 + 入口 + 根因结论。
- [native-rebuild-windows-p2.md](native-rebuild-windows-p2.md)：§0.2 控件事实、§6 W-M5、§13.2 #9、§17。
- [Design/ui-contract.md](../Design/ui-contract.md)：SETTINGS-01/02/03 Windows 列（NavigationView / Mica / SettingsCard / 整行拖）。
- `PhotoViewer/ViewModels/Settings/AGENTS.md`：去掉「无法加载控件」的旧结论。
- `Plans/native-rebuild-handoff.md`：下一步与状态。

## 15. 实施顺序（每步都有门）

1. **控件冒烟**：根 `App.xaml` + `XamlControlsResources` + `MicaBackdrop` + `TitleBar` + `NavigationView` + `NumberBox` + `InfoBar`；`--pv-native-settings` 类型的布局断言。
2. **窗口壳**：`TitleBar` + `NavigationView` + 五个空页 + 关闭事件；720/960 两尺寸截图。
3. **行原语**：`SettingsCard`、开关行、数值行、描述；在「文件」页落第一张卡片。
4. **五页内容**：按 §7 顺序铺开，先开关/下拉，后滑块/数值。
5. **三张排序表 + 录制**：单表原型 → 三表复用 → 录制与冲突。
6. **Presenter 与入口**：`NativeWindowPresenterImpl` 转交、右键菜单、主窗口关闭联动。
7. **`--pv-native-settings` 验收 + §14 文档同步**；Win11 实机截图与手感验收（旧系统不验）。

## 16. 实施记录与根因纠正（2026-10-17）

### 16.1 根因

W-M5 原先声称 `NavigationView` / `TitleBar` / `NumberBox` / `TextBox` / `RichEditBox` / `InfoBar` 在本工程加载失败（缺 `TabViewScrollButtonBackground` / `TabViewButtonBackground` 或 `E_FAIL`），于是全部换成了 `ListView` / `Slider` / 自绘 `Border`。这是**症状描述正确、归因错误**：

- 工程当时没有 `App.xaml`（`ApplicationDefinition`）与 XAML 元数据提供器（`IXamlMetadataProvider`）。
- WinUI 3 的 `XamlControlsResources` 需要该提供器；没有它，`new XamlControlsResources()` / 应用资源合并会抛 `Cannot find a resource with the given key: AcrylicBackgroundFillColorDefaultBrush`。
- 缺了 `XamlControlsResources`，只有基础模板可用；`NavigationView`/`TitleBar`/`NumberBox`/`TextBox`/`InfoBar`/`ProgressBar` 的模板引用的 `TabView*` 等资源全部缺失。
- 因此不是「非 XAML / 非打包 / 自包含工程不能用这些控件」，而是**缺少 XAML 资源引导**。

### 16.2 修复

- 增加根 `App.xaml`（`ApplicationDefinition`），合并 `XamlControlsResources`，设 `RequestedTheme="Dark"`；`App.xaml.cs` 接管原 `NativeApplication` 的 `OnLaunched`、外部打开与窗口生命周期。
- `WindowsNativeApplication` 只保留 `Application.Start`、DispatcherQueue/同步上下文、门面注入与窗口句柄。
- 增加 `CommunityToolkit.WinUI.Controls.SettingsControls` 依赖，使用官方 `SettingsCard`。
- 窗口壳改为 `MicaBackdrop` + `TitleBar` + `NavigationView`；页面行原语改为主题资源 + `SettingsCard`。
- 数值项恢复 `NumberBox`；清库状态恢复 `InfoBar`；快捷键按钮/冲突色改用主题资源。

### 16.3 当前实现

- 窗口：960×720（min 720×560），Mica，`TitleBar`（齿轮 + 设置），系统 caption 透明融入；不进任务栏，单例；主窗口关闭先关设置。
- 导航：`NavigationView` 五类（文件/预览/控制/EXIF/AI），`OpenPaneLength=240`，选中态为 WinUI 默认 accent 药丸 + 左缘指示条；内容岛边框/圆角覆盖为 0，Mica 平铺。
- 卡片：`SettingsCard`，主题材质；列表卡 `Padding=4`；滑块行的值放在 Content 顶部右对齐。
- 数值：线程/预载/最多数量/缩放预设用 `NumberBox`（140 宽，Inline spin）；缓存/延时/阈值保留 `Slider`。
- 清库：`ContentDialog` 确认 + 共享两段命令 + `InfoBar` 状态。

### 16.4 偏离与修正

| 原偏离 | 现状 |
|---|---|
| `NavigationView` → `ListView` | 已恢复 `NavigationView` |
| `TitleBar` → 系统 caption | 已恢复 `TitleBar` + 透明 caption |
| `NumberBox` → `Slider` | 数值项恢复 `NumberBox`；滑块仍用于连续值 |
| `InfoBar` → `Border` | 已恢复 `InfoBar` |
| 无文本输入控件 | 仍不需要独立 `TextBox`；`NumberBox` 内部使用 `TextBox` |
| 无 Mica | 已使用 `MicaBackdrop`（D4 修订） |
| 纯 C#、无 App.xaml | 根 `App.xaml` 仅作资源引导；页面仍纯 C# |

## 17. 验证（2026-10-17）

- `dotnet build PhotoViewer.Windows/PhotoViewer.Windows.csproj -c Debug`：0 error；新增代码 0 warning（仓库既有 `NU1903` SQLite 告警不计入本次改动）。
- `--pv-native-smoke`：**24 项 PASS**（主屏无回归）。
- `--pv-native-settings`：**7 项 PASS**（深色/Mica、NavigationView+TitleBar+NumberBox+InfoBar、五分类/尺寸、开关+NumberBox+Slider、列表重排、录制开始取消/手势显示、关闭回写）。
- 150% 实机截图：`release/native-win-acceptance/settings-page{0..4}.png`（DPI-aware `PrintWindow`）。
- 待作者手验：真实拖拽排序、真实键鼠录制（含滚轮/中键）、清库确认、预设增删、真实手感、125/150/200% 文本缩放。
