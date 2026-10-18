# filter 拆分笔记（草稿）

## platforms

| ID | 平台事实与已登记差异 |
|---|---|
| FILTER-01 | 共享状态：`FilterBarViewModel`（排序键/方向/星级筛选/相似开关）。macOS：`NSPopUpButton`×3 + `NSTextField` 计数 + 自绘 `FilterToggleButton`，几何走共享 `MainScreenLayout.FilterBarFrames`（`MacMainWindow.cs:150-160`）；菜单用 `NSMenu.PopUpMenu` 锚到按钮左下、内容左缘对齐，向下 8 点预留 + 5 点菜单外框 = 3 点可见间距（`MacMainWindow.cs:260` 起）。iPad：`UIButton` + `UIMenu`（`ShowsMenuAsPrimaryAction`，当前项 `UIMenuElementState.On`），计数 `UILabel` 12 点（`iOSFilterBar.cs:43-79`）。Windows：WinUI `ComboBox`×3 + `TextBlock` + `ToggleButton`（`WindowsFilterBar.cs:34-67`）。Android：Canvas 自绘控件 + 自绘 `PopupWindow`（锚在被点控件下方 2dp，空间不足翻上方，返回键先关；`AndroidFilterBar.cs:148-255`）；2026-10-18 修过“itemId 恒 0 导致点击不切换”。 |
| FILTER-02 | 四端共用 `SortFileList`（`ThumbnailListViewModel.cs:320-339`）。名称取 `f.Name`（代表文件名，含扩展名），日期 `PhotoDate ?? ModifiedDate ?? MinValue`，大小取代表文件 `FileSize`。`SortMode.Rating` 枚举存在（`FolderViewModel.cs:22`）但菜单未提供。 |
| FILTER-03 | 命中逻辑在 `ApplyFilter`（`ThumbnailListViewModel.cs:270-291`）；冲突定义 `ImageFile.HasRatingConflict`（`Core/Image/ImageFile.cs:191`），依赖同名合并填入的 `HiddenFileRatings`。`ShowRating` 只被卡片/控制栏/相似面板渲染读取，与筛选条无绑定。 |
| FILTER-04 | 文案 `共 N 张`：Mac `MacMainWindow.cs:665`、iOS `iOSFilterBar.cs:71`、Windows `WindowsFilterBar.cs:102`；Android 窄屏紧凑 `N 张` 且控件 84/76dp（`AndroidFilterBar.cs:89-93`，`Plans/native-rebuild-phase-android.md:252`）。计数按系统字体自然测量；Mac 与旧壳基线有约 3.5 点既有差异（`Plans/native-rebuild-phase-3.md:73`）。 |
| FILTER-05 | 列表重算与当前项映射在 `ApplyFilter`（`ThumbnailListViewModel.cs:293-309`）；上一张/下一张在 `ControlViewModel.ExecutePrevious/ExecuteNext`（`ControlViewModel.cs:278-300`）。iOS 胶片栏条目数改由业务筛选列表提供，避免 `reloadData` 后布局读到过期条目数（`Plans/native-rebuild-phase-ipad.md:124`）。 |
| FILTER-06 | 开关写 `SettingsModel.SimilarityPanelExpanded`（`Core/Settings/SettingsModel.cs:28`）；面板可见性 = 开关 && 文件栏可见（`SimilarityPanelViewModel.cs:50`），候选池 = `FilteredFiles`（`SimilarityPanelViewModel.cs:437`）。几何：macOS/iPad 90×27、末位、紧随计数 5 点（`Plans/native-rebuild-phase-3.md:53`）；Windows 用 90×32 的 `ToggleButton`（`WindowsFilterBar.cs:126-133`，`WindowsLayout.cs:25`）；Android 默认 90、窄屏 76。无默认快捷键。 |
| FILTER-07 | 共享常数：90×32、间距 5、计数高 17、开关高 27、外边距 10（`PhotoViewer.Shared/MainScreenLayout.cs:29-39`）；列布局总长 204 + 顶部让位，行布局高 52。macOS 列布局顶距 34 让交通灯、行布局整组居中（`MacMainWindow.cs:20,475`）；iPad/Android 行布局从头侧安全边 + 10 起排（`iOSNativeRootViewController.cs:424` 传 safe.Top+10，`AndroidNativeRootView.cs:267` 传 10；`Plans/native-rebuild-phase-ipad.md:149`、`Plans/native-rebuild-phase-android.md:90`）。Windows 筛选条是自带 `StackPanel` + 外边距 10，不消费共享 `FilterBarFrames`（`WindowsFilterBar.cs:64`），列布局没有额外的 34 顶距。 |
| FILTER-08 | macOS 菜单保留原生勾号、键盘选择与取消；iPad 按钮可点区 = 90×32（低于系统 44 点建议，未登记为 filter 差异）；Android 自绘菜单按行高 40 左右绘制、点外部关闭；Windows 用原生 `ComboBox`。菜单打开期间全局快捷键的抑制由各壳的输入层负责（`INPUT-01`）。 |
| FILTER-09 | 设置模型无排序/方向/星级筛选字段（`SettingsModel.cs:9-47`），三项仅存在于 `FilterBarViewModel` 实例；相似开关是唯一持久化项。格式启用/同名合并变化触发 `ApplyFilter`（`ThumbnailListViewModel.cs:77-80`）。 |
| FILTER-10 | 空列表空状态文案由胶片栏承担（`FILM-05`；Windows 为「无匹配照片」）。筛选重算清空缩略图队列并只加载首屏 20 张（`ThumbnailListViewModel.cs:398-410`）。 |

## status

| ID | 现状（按平台） |
|---|---|
| FILTER-01 | 共同语义自旧壳延续，四端原生壳均走共享 VM。iPad：2026-10-17 模拟器两机型程序化通过（三控件 90×32、计数自然宽、间距 5；`Design/ui-contract.md:82` 仍写“行/列组居中”，同日修复后行布局已改为前导对齐，见 questions 第 10 条）。Windows：原生筛选条语义一致，逐项对拍与手感待专项（同条）。macOS：菜单锚点与布局由 `--pv-native-layout` / `--pv-native-similarity` 回归覆盖（`Plans/native-rebuild-phase-3.md:73`），FILTER-01 无独立套件。Android：2026-10-18 模拟器 `AndroidNativeChecks main` PASS 14 含筛选条几何，下拉真实点击已修并目视（`Plans/native-rebuild-phase-android.md:15,198,259-260`），程序化断言待 A6。 |
| FILTER-02 | 无独立验收；排序行为由共享 VM 覆盖，四端未单独断言顺序与稳定性。 |
| FILTER-03 | 无独立验收；“改星后暂留/切走移除/计数一致”由 FILM-06 套件在 macOS（2026-10-16 `--pv-native-toolbar`）与 iPad（2026-10-17 模拟器两机型）程序化覆盖；Android 2026-10-18 只核对点星写回与主图身份不变，暂留与失败提示待 A6（`Design/ui-contract.md:150`）。 |
| FILTER-04 | 计数文本与几何由各端 layout/主屏检查间接覆盖；无独立计数断言。 |
| FILTER-05 | 无独立验收；点选/翻页不居中和列表偏移约束由 FILM-01/03 套件覆盖。 |
| FILTER-06 | macOS `--pv-native-similarity` PASS（toggle 双向、真实点击、几何；`Plans/native-rebuild-phase-3.md:73`）。iPad 2026-10-17 模拟器两机型 + 真机 `--pv-native-similarity-check` PASS（toggle 真实点击，`Design/ui-contract.md:154`）。Windows `--pv-native-smoke` 覆盖空态→提取→点选不重锚，视觉/手感待作者手验（同条）。Android 2026-10-18 模拟器目视 toggle 点击与面板，程序化待 A6（同条）。 |
| FILTER-07 | iPad 2026-10-17 模拟器两机型 `--pv-native-layout-check` 24 项 PASS（几何与行布局前导对齐，`Plans/native-rebuild-phase-ipad.md:149,171`）。macOS 基线/布局回归 PASS（`Plans/native-rebuild-phase-3.md:73`）。Windows 150% 下三尺寸两向程序化通过（`Design/ui-contract.md:80`）。Android 2026-10-18 `main` PASS 14（`Plans/native-rebuild-phase-android.md:198`），紧凑宽度为登记偏差（同文件 252）。 |
| FILTER-08 | 无独立验收；macOS 菜单行为作者手验，iPad/Android 菜单为系统原生控件目视。 |
| FILTER-09 | 无独立验收；相似开关跨启动持久化由共享设置读写覆盖，顺序/筛选不持久化未在验收中单列。 |
| FILTER-10 | 无独立验收；空筛选结果由 FILM-05 各端检查覆盖。 |

## questions

1. **当前照片被筛选排除后的翻页起点**：`HasPreviousFile/HasNextFile` 对 `IndexOf == -1` 分别返回 false / `-1 < Count-1`，于是“下一张”跳到筛选列表第一张、上一张无动作（`ThumbnailListViewModel.cs:355-368`、`ControlViewModel.cs:278-300`）。FILTER-05 定为“从原索引落到最近的前/后项”，需确认是否改共享导航。
2. **计数可能停在旧值**：`ApplyFilter` 在把当前隐藏伴侣映射为代表卡片时 `return`（`ThumbnailListViewModel.cs:298-306`），跳过了末尾的 `RaiseFilteredCountChanged()`（同文件 309）。列表已变但计数未刷新，需修或确认。
3. **排序/筛选不持久化是否是有意设计**：设置模型只有 `SimilarityPanelExpanded`（`SettingsModel.cs:28`），排序键/方向/星级筛选每次启动回到默认（`FilterBarViewModel.cs:51-70`）。若希望“选片进度不丢失”延伸到筛选条件，应新增字段；当前规范按会话级写入。
4. **名称排序的比较口径**：`OrderBy(f => f.Name)` 使用当前区域文化的字符串比较（`ThumbnailListViewModel.cs:325`），不同系统语言下同一目录可能顺序不同；四端要“结果一致”需改 `OrdinalIgnoreCase` 等确定性比较器。另 `SortMode.Rating` 枚举存在但菜单从不提供（`FolderViewModel.cs:22`、`FilterBarViewModel.cs:20-25`），需确认是遗漏还是有意不做评分排序。
5. **Windows 筛选条几何未走共享口径**：`WindowsFilterBar` 用自带 `StackPanel` + 外边距 10（`WindowsFilterBar.cs:64`），相似开关是 90×32（`WindowsFilterBar.cs:126-133`，`WindowsLayout.cs:25`）而共享/iPad 为 90×27；`Plans/native-rebuild-windows-p2.md:111` 又写列布局顶距 34。三者需统一或登记为差异。
6. **同名合并组的排序/大小口径**：名称与大小都取代表文件，隐藏伴侣不参与排序、大小不累加（`ThumbnailListViewModel.cs:325-334`）。“文件大小”排序对 RAW+JPG 组只反映代表格式，需确认。
7. **“星级冲突”在同名合并关闭时**：菜单项始终存在且结果恒为空；FILTER-03 暂定为“不隐藏、不禁用、不报错”，若希望禁用需改共享选项列表。
8. **计数包含暂留项**：改星落出筛选的当前照片被强制保留（`ThumbnailListViewModel.cs:273`），而计数取 `FilteredFiles.Count`（同文件 72、309），于是计数比筛选条件多 1；FILM-06 写“计数与筛选一致”，两处表述需对齐（是“可见列表数”还是“命中数”）。
9. **ShowRating 关闭后星级筛选仍可用**：筛选条与 `Settings.ShowRating` 无绑定（`SettingsViewModel.Rating.cs:12`、`FilterBarViewModel.cs`），用户可在看不到星级时按星级筛选；FILTER-03 以“应当”保留，需作者确认。
10. **行布局对齐的文档冲突**：`Design/ui-contract.md:82` 写 iPad“行布局横向居中”，但同日修复记录与实现为 `alignLeading: true`（`Plans/native-rebuild-phase-ipad.md:149`、`iOSFilterBar.cs:97-99`）。规范按“无窗口装饰的平台可前导对齐”表述，platforms.md 需以实现为准。
11. **Android 紧凑宽度未进令牌**：84/76dp 与“N 张”只在壳内常量与计划偏差里（`AndroidFilterBar.cs:89-93`、`Plans/native-rebuild-phase-android.md:252`），tokens 是否需要 `filter.control.width.compact` 等命名待与 tokens 负责人对齐。
12. **macOS 计数宽度差异**：旧壳基线按系统字体测得约 3.5 点差异（`Plans/native-rebuild-phase-3.md:73`），属已登记视觉差异还是待修，未在规范中判定。
