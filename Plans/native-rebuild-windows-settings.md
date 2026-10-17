# Windows 原生设置窗口方案（W-M5）

> 2026-10-17 设计定稿。隶属 [native-rebuild-windows-p2.md](native-rebuild-windows-p2.md) §6 **W-M5**；交互契约 [SETTINGS-01/02](../Design/ui-contract.md)；共享工作副本 [NativeSettingsSession.cs](../PhotoViewer/Core/Settings/NativeSettingsSession.cs)。
>
> 作者口径（2026-10-17）：**不要混搭新旧控件，保证一个软件内设计风格完全统一；验收只在 Win11，设计语言以最新 Win11 规范为准，旧系统自动回退、不设验收限制。** 本方案据此把「Windows 11 设置页结构」与「应用现有深色皮肤」合并：不引入 Mica、不抬最低系统、不做任何 OS 条件分支。
>
> **实施结果（2026-10-17）**：W-M5 已落地，`--pv-native-settings` **7 项 PASS**、主冒烟 24 项无回归。**本工程无法加载 `NavigationView` / `TitleBar` / `NumberBox` / `TextBox` / `RichEditBox` / `InfoBar` 模板**（缺 TabView 资源或 `E_FAIL`）；实际实现按 **§16** 替换为 ListView 导航、系统 caption 深色、Slider 数值与自绘状态条。**§4/§6/§7 中对应控件的描述以 §16 为准。**

---

## 1. 目标

1. 五分类 **文件 / 预览 / 控制 / EXIF / AI** 原生呈现，分类、分组、字段与 iOS 原生设置页逐项对齐（SETTINGS-02），只换 Windows 外壳。
2. 交互完整：显隐勾选、拖拽排序、快捷键录制与冲突高亮、滑块/数值联动、缩放预设增删、清库二次确认、关闭回写。
3. 视觉统一：全部使用 WinUI 3（`Microsoft.UI.Xaml.Controls`）与同一套深色 token；不出现 Win10 时代壳控件（`CommandBar`/`AppBarButton`/`MenuBar`/`Pivot`/`Hub`/`MessageDialog`）、不出现第二种灰或第二种圆角。
4. **验收只在 Win11**：设计与交互按最新 Win11 规范；旧版系统由平台自动回退，不设验收项，也不为其改设计或加分支。

## 2. 非目标

- 不跟随系统亮/暗：与主窗口一致，窗口根元素强制 `ElementTheme.Dark`。
- 不改设置 JSON 字段名、默认值、共享 `SettingsViewModel` 语义；不新建第二份命令表。
- 不做 Mica / 亚克力 / 壁纸材质（理由见 D4）。
- 不抬 `TargetFramework` / `TargetPlatformMinVersion`，不写 `OperatingSystem.IsWindowsVersionAtLeast` 分支。
- 不做工具页（EXIF 详情 / 照片统计 / DINO 诊断）；不迁移 Avalonia 旧壳设置页（旧壳不做产品验收）。
- 不做前进/后退历史导航（macOS 26 系统设置专属形态，Windows 设置左栏常驻）。

## 3. 设计决策

| # | 决策 | 理由 |
|---|---|---|
| D1 | **控件世代统一**：全部 WinUI 3；禁用 Win10 壳控件与 `Windows.UI.Xaml` 旧命名空间 | 主窗口已经是 WinUI 3；混入旧壳控件会产生两套 hover/focus/圆角语言 |
| D2 | **结构**：独立顶层窗口 + `NavigationView`(Left) + 页面 `ScrollViewer` + 卡片；不用 `ContentDialog` / `TabView` / Win32 属性表 | ContentDialog 撑不起 5 页长表单与录制；TabView 是文档标签语义；属性表是旧观感 |
| D3 | **皮肤**：沿用 `WindowsLayout` 的 `#111/#222/#333/#EEEEEE/#AAAAAA/#777777`；设置页新增的只有几何与字号 token | 设置窗口必须像同一个应用，而不是系统设置同款；不新增第二套灰 |
| D4 | **不用 Mica** | 会把设置窗口变成「系统设置同款」而不是「PhotoViewer 同款」；新版观感来自结构、控件状态与字号层级，不是壁纸材质。这是应用身份选择，不是旧系统兼容妥协 |
| D5 | **验收基线 Win11，旧系统不设门**：不抬系统、不加分支；设计不为旧系统让步 | 验收只在 Win11；旧系统由平台自动回退，不纳入验收，也不写条件分支 |
| D6 | **强调色**：原生控件用系统 accent；应用 `#1E90FF` 只作内容高亮（当前照片描边等），不覆盖控件 accent | 原生的选中/开关/滑条本来就跟随系统 accent；强行改色会破坏无障碍与系统一致性 |
| D7 | **标题栏**：WinUI `TitleBar` 控件 + 系统 caption（1.7+，本仓 1.8 可用） | 自绘 chrome 只保留主窗口一套；设置窗口不复用自动显隐标题栏（那只适合看图） |
| D8 | **纯 C# 建 UI，不写 `.xaml`**；复用 `NativeSettingsSession` 工作副本，关闭回写 | 与主窗口、macOS 程序化 AppKit 一致；不引入 XAML 编译目标 |

## 4. 窗口结构

```
Window 960×720（min 720×560，#111111，标准边框 + 系统 caption）
├─ TitleBar 控件（48，透明）        “设置”
└─ NavigationView（Left，220，#111111）
   ├ 文件 / 预览 / 控制 / EXIF / AI（16px 图标；选中态用系统 accent 药丸）
   └ Content（#111111）
      └ 页面 ScrollViewer
         └ StackPanel（MaxWidth 800，Padding 24）
            ├ Title 28/36 SemiBold
            ├ BodyStrong 14/20 组标题
            └ Card × N（#222222 + 1px #333333，圆角 4，MinHeight 64）
```

- `ExtendsContentIntoTitleBar = true` + `Window.SetTitleBar(titleBar)`；`TitleBar.Title = "设置"`，`IsBackButtonVisible = false`，`IsPaneToggleButtonVisible = false`；根 Grid 背景 `#111111`。
- `NavigationView`：`PaneDisplayMode = Left`、`IsPaneToggleButtonVisible = false`、`IsSettingsVisible = false`、`IsBackButtonVisible = Collapsed`、`CompactModeThresholdWidth = 0`、`ExpandedModeThresholdWidth = 0`、`OpenPaneLength = 220`、`IsPaneOpen = true`。
- 5 个 `NavigationViewItem` 用 `FontIcon`（16px，字体族统一走 `WindowsIcons` 的 fallback 链，不在页面里硬编码字体名）；选中项与页面 `Visibility` 同步；默认选中「文件」。
- 窗口：`OverlappedPresenter` 可缩放、不可最大化；`AppWindow.IsShownInSwitchers = false`（不进任务栏/Alt+Tab），打开时相对主窗口居中；单例，再次打开只 `Activate()`。
- 主窗口关闭时先关设置窗口，避免孤儿窗口与未回写的工作副本。

## 5. 配色与版式

**版式全部按最新 Win11（Fluent 2）口径，不复用旧壳/Avalonia 设置页的尺寸**；只复用应用已有的颜色常量（下表），避免出现两套灰。

**配色（复用现有常量，不新增灰度）**

| 用途 | 常量 | 值 |
|---|---|---|
| 窗口 / 页面底 | `WindowsLayout.BarBackground` | `#111111` |
| 卡片底 | `WindowsLayout.BlockBackground` | `#222222` |
| 卡片描边 / 分隔线 | `WindowsLayout.Divider` | `#333333` |
| 主文字 | `WindowsLayout.CardNameForeground` | `#EEEEEE` |
| 副文字 / 描述 | `WindowsLayout.ExifLabelForeground` | `#AAAAAA` |
| 三级文字 / 把手 / 占位 | `WindowsLayout.SimilarityCaptionForeground` | `#777777` |
| 冲突高亮 | 新增 `SettingsConflictForeground` | `#FF8C00` |
| 强调 | 系统 accent（原生控件内部） | 跟随系统 |

**版式**

| 项 | 值 |
|---|---|
| 页面标题 | 28 / 36，SemiBold |
| 组标题 | 14 / 20，SemiBold |
| 行标题 | 14 / 20，Regular |
| 行描述 | 12 / 16，`#AAAAAA`，自动换行 |
| 卡片 | 圆角 4，1px 描边，MinHeight 64，Padding 16/12 |
| 卡间距 / 组间距 / 页边距 | 4 / 24 / 24 |
| 页宽上限 | 800 |
| 导航栏宽 | 220 |
| 列表行高 | 单行 48 / 含副标题 60 |
| 按钮 | 默认 Fluent；行内图标按钮 28×28，透明底，hover 用 `SubtleFillColorSecondary` |

## 6. 控件映射

> 实施替换见 §16：`NavigationView`→`ListView`、`TitleBar`→系统 caption、`NumberBox`→`Slider`、`InfoBar`→`Border`、`TextBox` 不使用。

| 设置元素 | 控件 | 说明 |
|---|---|---|
| 分类导航 | `NavigationView` + `NavigationViewItem` | 左栏常驻，无汉堡/返回 |
| 分组容器 | 页面 `ScrollViewer` + `StackPanel` | 不嵌套滚动 |
| 设置行 | 自建 `WindowsSettingsCard`（Border + Grid） | 左标题/描述，右控件；WinUI 没有 `SettingsCard` 控件，按 Win11 卡片口径自建 |
| 布尔（带描述） | `ToggleSwitch` | 清空 `OnContent`/`OffContent`，`MinWidth = 0`；纯开关行整卡可点 |
| 列表行内勾选 | `CheckBox` | 行首，`MinWidth = 0` |
| 可排序表 | `ListView` | `SelectionMode = None`、`ReorderMode = Enabled`、`AllowDrop/CanReorderItems = true`；行尾把手字形 `\uE76F` |
| 枚举 | `ComboBox` | 右对齐，宽 240 |
| 整数（线程/预载） | `NumberBox` | `SpinButtonPlacementMode = Inline` |
| 数值（缓存/阈值/延时） | `Slider` | 标题 + 右侧数值，拉条在下 |
| 快捷键绑定 | 自绘 `Button` | Windows 无录制控件；录制逻辑见 §8.2 |
| 缩放预设 | `NumberBox` + 删除/添加按钮 | 一行一个预设，底部「添加预设」 |
| 清库确认 | `ContentDialog` | `XamlRoot` = 设置窗口内容；Primary=清除，Close=取消 |
| 清库状态 | `InfoBar` | `IsClosable = false`；VM 无级别字段，统一 Information，不猜级别 |
| 窗口标题 | `TitleBar` | 见 D7 |

## 7. 页面内容

### 7.1 文件

- 支持的文件格式：可排序表（`CheckBox` + 格式名 + 扩展名副标题 + 把手）。
- 同名文件视为同一张图片：卡片 + `ToggleSwitch` + 描述。
- 缓存与预加载：原生/软件解码线程（`NumberBox`）、缓存内存上限（`Slider` + 数值）、缓存数量上限（`Slider` + 数值）、上一张/下一张/列表滚动预载（`NumberBox`）、滚动预载延时（`Slider`）；下方预算/缓存提示沿用共享文本，按级别取色：信息用副文字色、warning 用 `SettingsConflictForeground`、error 用现有 `SimilarityErrorForeground`。

### 7.2 预览

- 缩放时显示指示器：卡片 + `ToggleSwitch`。
- 缩放比例预设：`NumberBox`（90 宽）+ `%` + 派生显示文本 + 删除图标按钮；底部「添加预设」。`NumberBox` 的 Enter/失焦提交语义正好替代旧壳的 `DeferredNumericTextBox`，不再需要「点击进入编辑态」的两段式交互。

### 7.3 控制

- 控制栏布局位置：`ComboBox`，下方描述用当前项的 `Description`。
- 控制栏功能/快捷键：可排序表，行 = 勾选 + 名称 + 主录制按钮 + 次录制按钮 + 把手；录制态、冲突色、清空态由行订阅 `HotkeyItem.PropertyChanged` 刷新。
- **不显示「苹果键盘映射」整段**（Apple 专属，`UseAppleKeyboard` 在 Windows 恒 false）。

### 7.4 EXIF

- 显示/收纳：可排序表（勾选 + 字段名 + 把手）。
- 评分与写回：`ShowRating`、`SafeSetRating`（带描述）；安卓 USB 提示不显示。

### 7.5 AI

- 相似聚类：阈值 `Slider`（75–95%）、最多数量 `Slider` + `NumberBox`（1–32，指数映射沿用共享 `...Exp` 属性）。
- 分析诊断：即时计算 DINO/CV 的 `ToggleSwitch`（P3 已落地，不再是禁用项）。
- 特征数据库：清库按钮 + `ContentDialog` 二次确认 + `InfoBar` 状态；执行时走共享 `ClearDatabaseCommand` 的两段确认（先 arm 再执行），按钮按 `ClearDbBusy` 禁用。

### 7.6 与 macOS/iOS 的差异（登记 ui-contract）

| 项 | macOS / iOS | Windows |
|---|---|---|
| 分类导航 | `NavigationSplitView` / 半屏 sheet | `NavigationView` 左栏常驻，无前进/后退 |
| 行布局 | `Form` 行（标题左、控件右） | 卡片式行（同语义，Win11 卡片口径） |
| 排序 | 只从右侧把手拖（`.onMove`） | 整行拖；行尾把手只是视觉锚点 |
| 键盘映射 | 显示「使用苹果键盘」段 | 隐藏（Apple 专属） |
| 强调色 | 系统强调色 | 原生控件系统 accent；应用蓝仅内容高亮 |

## 8. 关键交互

### 8.1 拖拽排序（三张表）

- `ListView.ItemsSource` **直接绑定共享集合**（`Hotkeys` / `FileFormats` / `ExifDisplayItems`），不建并行行集合，避免双份顺序。
- 行内容在 `ContainerContentChanging` 里用 C# 构建（`args.ItemContainer.Content = BuildRow(args.Item)`，`args.Handled = true`），并统一把容器 `Padding = 0`、`MinHeight = 0`、`HorizontalContentAlignment = Stretch`；回收时退订行内事件。
- 重排由 `ListView` 直接改 `ItemsSource`（`ObservableCollection` 实现 `IList`），共享 VM 的 `CollectionChanged` 负责落盘；不需要 `DragItemsCompleted` 手工映射。**该行为先在单表原型里证实**（拖一次后 `CollectionChanged` 是否触发、顺序是否与落盘一致）；若 `ReorderMode` 不写回源集合，则改为在 `DragItemsCompleted` 里读容器顺序后回写 VM，仍然只有一份顺序。
- 表固定高度 = 行数 × 行高，`ScrollViewer.SetVerticalScrollMode(list, Disabled)`、`HorizontalScrollMode = Disabled`，滚动交给外层页面。
- **行为差异**：WinUI 没有常驻把手元素（框架资源里只有 `RightReorderHint` 等落点提示），因此是**整行拖**；行尾 `\uE76F` 只是视觉把手，拖它等于拖整行。此差异登记进 SETTINGS-01 的 Windows 列。

### 8.2 快捷键录制

- 行内 `Button` 显示 `AppGesture.ToDisplayString()`（Windows 下 `UseAppleKeyboard = false`，输出 `Ctrl`/`Alt`/`Win`；鼠标为「滚轮上 / 中键 / 侧键」）；点击进入录制态，文案「按下按键…」。
- 捕获挂在窗口根：`PreviewKeyDown` + `AddHandler(KeyDownEvent, …, handledEventsToo: true)`；`e.Key` + `WindowsInput.CurrentModifiers()` → `AppGesture`；Esc 取消、Delete/Backspace 清空。
- 修饰键单独按不会触发 `KeyDown`，靠 `GetKeyState`（`WindowsInput` 已封装）；录制态吞掉 `PointerWheelChanged`（避免页面滚动）与中键 `PointerPressed`。
- 录制期间把 Space/方向键在预览阶段标记 `Handled`，不让 ListView/NavigationView 拿去滚屏或移焦点；主窗口快捷键天然不受影响（独立窗口，`WindowsMainWindow._root.KeyDown` 不会收到）。
- 结束后 `Settings.CheckHotkeyConflicts()`，冲突行按钮描边 `#FF8C00`。

### 8.3 生命周期与回写

1. `WindowsNativeApplication` 构造 `WindowsNativeSettingsPresenter`（懒建窗口）。
2. `MainViewModel.OpenSettingWindow()` → `NativeWindowPresenter.ShowSettingsWindow(settings)` → presenter `TryPresent(settings)`；**只走这一条路**，不额外注册 `NativeSettingsPresenter`。
3. `TryPresent`：已打开则 `Activate()`；否则 `new NativeSettingsSession(settings)`、建窗口、选中「文件」、`Activate()`。
4. `Closed`：`session.Apply()` → `session.Dispose()` → 清空引用；重复关闭只回写一次。
5. 主窗口 `Closed`：先关闭设置窗口（保证回写），再走原有 `Detach()`。
6. 关闭即应用，**没有 Apply/Cancel 按钮**（与 macOS/iOS 同口径）；页面内改动只写工作副本。

### 8.4 清库

`ContentDialog`（Primary=清除、Close=取消、`DefaultButton = Close`）→ 确认后按共享两段确认执行 `ClearDatabaseCommand`；`ClearDbBusy` 期间按钮禁用；结果写 `InfoBar`（`IsOpen = 有状态文本`），不伪造成功。

## 9. 实现分解

| 文件 | 职责 |
|---|---|
| `PhotoViewer.Desktop/Native/WindowsNativeSettingsPresenter.cs` | `INativeSettingsPresenter`：工作副本、单例、打开/关闭回写、生命周期 |
| `PhotoViewer.Desktop/Native/WindowsSettingsWindow.cs` | 窗口壳：`TitleBar`、`NavigationView`、页面宿主、关闭事件、尺寸/居中 |
| `PhotoViewer.Desktop/Native/WindowsSettingsCard.cs` | 卡片/开关行/数值行/描述/提示等行原语 |
| `PhotoViewer.Desktop/Native/WindowsSettingsList.cs` | 可排序表：`ListView` 装配、行构建、把手、容器属性、空态 |
| `PhotoViewer.Desktop/Native/WindowsHotkeyRecorder.cs` | 录制按钮、捕获、格式化、冲突态 |
| `PhotoViewer.Desktop/Native/WindowsSettingsPage.File.cs` 等五个 partial | 五页内容：`File` / `Preview` / `Control` / `Exif` / `Ai` |
| `PhotoViewer.Desktop/Native/WindowsSettingsAcceptance.cs` | `--pv-native-settings` 定向验收（由 `WindowsMainWindow.RunSmokeAsync` 调用；隔离设置、自检、收尾还原） |
| `PhotoViewer.Desktop/Native/WindowsLayout.cs` | 新增「设置窗口」段：导航栏 220、页宽 800、卡片 64/16/12/4、行高 48/60、字号、冲突色 |
| `PhotoViewer.Desktop/Native/WindowsNativeApplication.cs` | 注入 presenter；`ShowSettingsWindow` 从抛异常改为转交；主窗口关闭时先关设置 |
| `PhotoViewer.Desktop/Native/WindowsMainWindow.cs` | 右键菜单末尾（分隔线后）新增「设置…」，放开入口 |
| `PhotoViewer.Desktop/Native/WindowsIcons.cs` | 新增五个分类字形（文件/预览/控制/EXIF/AI）；字体族改为 `"Segoe Fluent Icons,Segoe MDL2 Assets"`（WinUI 的 fallback 链，主窗口同时受益；旧系统不单独验收） |

## 10. 系统基线与回退

**设计与验收只按 Win11 当前版本（Fluent 2 / WinUI 3 1.8）**；旧版系统由平台自动回退，不纳入验收，也不为其写条件分支。

| 项 | Win11（验收基线） | 旧系统 |
|---|---|---|
| 控件 / 字号 / 圆角 / 间距 | 最新 Win11 规范 | 由 WinUI 3 与 OS 自行回退 |
| 窗口圆角、caption 按钮 | Win11 原生 | 由 OS 决定，不验收 |
| 字体 / 图标 | Segoe UI Variable / Segoe Fluent Icons | 系统回退；图标字体族走 WinUI 的 fallback 链 |
| Mica | 不用（D4，应用身份选择） | 不涉及 |
| `TitleBar` 控件 | 验收项 | 不在验收范围 |

## 11. 风险与验证门

| 风险 | 等级 | 验证门 / 缓解 |
|---|---|---|
| 非 XAML + 非打包自包含工程里复杂控件模板不可用（`ProgressBar` 前科） | 高 | 开工第一步：空白窗口冒烟 `NavigationView`/`ToggleSwitch`/`NumberBox`/`Slider`/`InfoBar`/`ContentDialog`/`ListView(ReorderMode)`/`TitleBar`，接 `DebugSettings.XamlResourceReferenceFailed` 看日志 |
| `TitleBar` 控件在 Win11 上的绘制与 caption 对比度 | 低 | Win11 实机截图核对；旧系统不在验收范围 |
| `ListView` 行元素注入（`ContainerContentChanging`）与重排/回收冲突 | 中 | 先做单表原型：行内勾选可点、hover 正常、拖后集合顺序与落盘一致 |
| 旧系统图标字形回退（PUA 空框） | 低（不验收） | 字体族统一走 WinUI fallback 链；不单独验收旧系统 |
| 录制时滚轮/空格被页面消费 | 中 | 录制态 `PreviewKeyDown` + 滚轮 `Handled`；程序化断言 + 作者手验 |
| 设置窗口成为孤儿或未回写 | 中 | 主窗口关闭先关设置；`Closed` 幂等回写；退出路径自检 |
| 720 宽下描述换行导致横向溢出 | 低 | 页宽上限 800 + `TextWrapping`；720 宽断言 |

## 12. 验收

**程序化（`--pv-native-settings`，隔离设置/数据库）**

1. 五分类可切换，页面标题/组标题/卡片数量与 VM 一致。
2. 勾选/下拉/滑块/`NumberBox` 往返；关闭后重开恢复；主窗口控制栏按回写重建。
3. 三张表：重排后共享集合顺序与设置 JSON 一致；行内勾选可点。
4. 录制：键盘映射写入主/次绑定，冲突标记出现；Esc 取消、Delete 清空（程序化驱动映射函数 + UI 状态，真实按键由作者手验）。
5. 清库：取消不执行；确认后状态可见、`ClearDbBusy` 期间按钮禁用。
6. 关闭回写一次、重复关闭不重复写；主窗口关闭时设置窗口同步关闭。
7. 720×560 无横向溢出；根元素 `ActualTheme == Dark`。
8. 截图导出供目视（无屏幕录制权限时如实记录）。

**人工（作者）**

- Win11 截图：底色、卡片、字号、图标、caption、无第二种灰/圆角（旧系统不验收）。
- 真实鼠标拖拽排序手感；快捷键录制（含滚轮/中键）；125/150/200% 文本缩放。

## 13. 待作者确认（默认按推荐执行）

1. 设置入口只放右键菜单「设置…」（推荐），还是再加控制栏命令？
2. 窗口不可最大化（推荐）、不进任务栏（`IsShownInSwitchers = false`）、随主窗口关闭，是否接受？
3. 原生控件使用系统 accent（推荐），主窗口的 `#1E90FF` 仅作内容高亮，是否接受？
4. 排序是整行拖 + 行尾把手视觉（WinUI 无原生把手），是否接受？

## 14. 文档同步（实现收尾必须一起改）

- `PhotoViewer.Desktop/AGENTS.md`：文件清单与「当前状态」（去掉「设置窗口未原生」）。
- 根 `AGENTS.md` §5.8：设置窗口原生链路一句话 + 入口。
- [native-rebuild-windows-p2.md](native-rebuild-windows-p2.md)：§6 W-M5、§13.2 #9 状态。
- [Design/ui-contract.md](../Design/ui-contract.md)：SETTINGS-01/02 Windows 列（整行拖 + 把手视觉）；SETTINGS-03 注明为 macOS 专属，Windows 用应用皮肤。
- `Plans/native-rebuild-handoff.md`：下一步与状态。

## 15. 实施顺序（每步都有门）

1. **控件冒烟**（§11 第一行）：8 个控件 + 深色 + 字体 fallback；Win11 通过后再动页面。
2. **窗口壳**：`TitleBar` + `NavigationView` + 五个空页 + 关闭事件；720/960 两尺寸截图。
3. **行原语**：卡片、开关行、数值行、描述；在「文件」页落第一张卡片。
4. **五页内容**：按 §7 顺序铺开，先开关/下拉，后滑块/数值。
5. **三张排序表 + 录制**：单表原型 → 三表复用 → 录制与冲突。
6. **Presenter 与入口**：`NativeWindowPresenterImpl` 转交、右键菜单、主窗口关闭联动。
7. **`--pv-native-settings` 验收 + §14 文档同步**；Win11 实机截图与手感验收（旧系统不验）。

## 16. 实施记录与偏离（2026-10-17）

W-M5 已落地。开工先做空白窗口逐控件冒烟，发现本工程（非 XAML / 非打包 / 自包含）有一批 WinUI 控件无法加载，方案随之替换。

### 16.1 控件可用性实测

| 控件 | 结果 | 替代 |
|---|---|---|
| `NavigationView` | 首次布局 `XamlParseException`（缺 `TabViewScrollButtonBackground`） | 左侧 `ListView` + UIElement 行 |
| `TitleBar` | 同上 | 系统标题栏 + `AppWindowTitleBar` 配色 |
| `NumberBox` | 同上 | `Slider`（线程/预载/最多数量/缩放预设） |
| `TextBox` / `RichEditBox` | `E_FAIL(0x80004005)` | 不使用文本输入 |
| `InfoBar` | 同上（缺 TabView 资源） | `Border` 状态条 |
| `ProgressBar` | 既有结论（`E_FAIL` / 缺 TabView 资源） | 自绘进度 |

可用且已验证：`ContentDialog`、`ListView`、`ComboBox`、`Slider`、`ToggleSwitch`、`CheckBox`、`Button`、`TextBlock`、`FontIcon`、`ScrollViewer`、`StackPanel`、`Border`、`Grid`。`XamlControlsResources` 在 `OnLaunched` 之后合并仍抛 `AcrylicBackgroundFillColorDefaultBrush`。

### 16.2 实际实现

- **窗口**：960×720（min 720×560），系统标题栏深色（`AppWindowTitleBar` 颜色可用；`PreferredHeightOption` 抛 `0x8007139F`，不调高度），不进任务栏，单例；主窗口关闭先关设置。
- **尺寸**：激活**前**用主窗口 `GetDpiForWindow` 定物理尺寸；激活后再 `Resize` 不刷新内容岛。960 DIP 外层对应 945 DIP 内容（窗口边框占 15 DIP）。
- **导航**：`ListView` 五行（图标 + 文本），`SelectionMode=Single`；容器只改 `Padding/MinHeight`。
- **列表**：`ListView.ItemsSource` 直接放 `UIElement` 行（`ContainerContentChanging` 替换 `Content` 不显示）；`ReorderMode=Enabled`；拖后按行顺序逐项 `Move` 回写共享集合。
- **数值**：`Slider` + 右侧数值文本；线程/预载/最多数量线性，缓存/延时/缩放预设指数（缩放预设 1%~1600%）。
- **录制**：窗口根 `PreviewKeyDown`/`KeyDown`/滚轮/中键；Esc 取消、Delete 清空、冲突橙、失活取消；主窗口快捷键因独立窗口天然隔离。
- **清库**：`ContentDialog` 确认 + 共享两段确认命令 + `Border` 状态条。
- **入口**：右键菜单「设置…」→ `MainViewModel.OpenSettingWindow` → `NativeWindowPresenter` → presenter。
- **图标**：`WindowsIcons.FluentFontFamily` 改 `Segoe Fluent Icons,Segoe MDL2 Assets` 回退链，新增五分类字形。

### 16.3 验收

- `--pv-native-settings` **7 项 PASS**：深色/五分类/960 外层 945 内容/开关+滑条回写/UI 行重排回写/录制开始取消/手势显示/关闭回写。
- `--pv-native-smoke` **24 项全 PASS**（无回归）。
- 截图：`release/native-win-acceptance/settings-page{0..4}.png`（DPI-aware `PrintWindow`，1440×1080）。
- 待作者手验：真实拖拽排序、真实键鼠录制（含滚轮/中键）、清库确认、预设增删、真实手感。

### 16.4 偏离清单

- `NavigationView` → `ListView`；`TitleBar` → 系统 caption；`NumberBox` → `Slider`；`InfoBar` → `Border`；无文本输入控件。
- 窗口尺寸在激活前定，而非 `Loaded` 后。
- 排序整行拖 + 行尾把手视觉、无前进/后退、无 Apple 映射段——与方案一致。
