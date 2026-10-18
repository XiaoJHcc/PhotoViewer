# state.md 草稿笔记（平台实现 / 验收状态 / 疑问）

> 仅供拆分与对账使用，不属于规范正文。状态文案摘自 `Design/ui-contract.md` §4 与各阶段清单，日期保留原样；规则条目见 [../state.md](../state.md)。

## platforms

### 外壳与布局
| ID | 平台 | 控件 / 入口 | 已登记差异 |
|---|---|---|---|
| SHELL-01 | mac | `NSWindow` 全内容视图；分区由 `MainScreenLayout` 落 frame | — |
| SHELL-01 | win | WinUI 3 五分区；几何唯一来源 `WindowsLayout.cs` | — |
| SHELL-01 | ios | UIKit 根控制器 + `MainScreenLayout` + 真实 `SafeAreaInsets`；行布局筛选条从安全区前导 +10 起排 | `SHELL-01@ios`：无交通灯让位 |
| SHELL-01 | android | `AndroidNativeRootView` + `AndroidMainScreenLayout` 叠加 insets；筛选条 `alignLeading` | `SHELL-01@android`：无交通灯让位 |
| SHELL-02 | mac | `FullSizeContentView` + 隐藏标题；两条顶部拖动区（行 44 / 列 24），窗口级监视器实现双击 | 双击按 `AppleActionOnDoubleClick`；交通灯嵌入左上 |
| SHELL-02 | win | 自绘 50 DIP 热区、指针进入显示/移开隐藏；caption 边界按按钮布局推导；拖动区只在可见时登记 | `SHELL-02@win`：隐藏态零区域 |
| SHELL-02 | ios | 无窗口装饰，隐藏状态栏；主图长按菜单承担窗口级入口 | `SHELL-02@ios`：无标题栏双击语义 |
| SHELL-02 | android | `AppCompatActivity` + Material 3 深色；系统栏/insets | `SHELL-02@android`：窗口装饰属平台 |
| SHELL-03 | mac | 系统全屏按钮 + 显示菜单；`NSUserDefaults` 存全屏 | — |
| SHELL-03 | win | 标题栏按钮 + `OverlappedPresenter` 全屏；最大化/还原/全屏四态往返 | — |
| SHELL-03 | ios/android | 不提供入口 | `SHELL-03@ios` / `@android` |
| SHELL-04 | mac | `FrameAutosaveName = PhotoViewer.Native.MainWindow`；ShowFilm/ShowControls/Fullscreen 存 `NSUserDefaults` | — |
| SHELL-04 | ios | `NSUserDefaults` 存 ShowFilm/ShowControls（`RestoreWindowPreferences`） | — |
| SHELL-04 | win | 无栏位显隐持久化；窗口帧由系统管理 | `SHELL-04@win`：待补 |
| SHELL-04 | android | 无栏位显隐/窗口帧持久化 | `SHELL-04@android`：缺口 |

### 筛选条
| ID | 平台 | 控件 | 差异 |
|---|---|---|---|
| FILTER-01/03 | mac | `NSPopUpButton`（90×32）+ 自绘 `FilterToggleButton`（90×27）；菜单 `NSMenu.PopUpMenu` 锚到按钮左下，留 3 点间距 | 菜单锚点实现细节，不入规则 |
| FILTER-01/03 | ios | `UIButton` + `UIMenu`，90×32、间距 5、计数自然宽、toggle 90×27 | — |
| FILTER-01/03 | win | `WindowsFilterBar` 原生；toggle 复用 32 高筛选控件 | `FILTER-03@win`：toggle 高 32，非 27 |
| FILTER-01/03 | android | 自绘 90×32 下拉 + `PopupMenu`；toggle 窄屏 76 宽 | `FILTER-03@android`：紧凑宽度 |
| FILTER-02 | 全部 | 共享 `ThumbnailListViewModel.SortFileList` | — |

### 主图
| ID | 平台 | 实现 | 差异 |
|---|---|---|---|
| IMAGE-01/02/03 | mac | `MetalPhotoView` + 共享 `PhotoViewport`；`PhotoLoadSession` 管代际/代理/Full | 100% = 物理像素 |
| IMAGE-01/02/03 | ios | `UIScrollView` 是唯一交互真源，程序化缩放走 `ZoomToRect`/`SetZoomScale` | 小图/RAW 预览夹回适应（`IMAGE-02@ios`） |
| IMAGE-01/02/03 | win | `SwapChainPanel` + D3D11 F16；`WindowsPhotoView.ToggleFit` | `IMAGE-02@win`：Fit 只用视口中心，无对焦点优先 |
| IMAGE-01/02/03 | android | `SurfaceView` + EGL14/GLES，视口复用 `PhotoViewport` | 小图同 iOS 夹回适应 |
| IMAGE-04 | mac | 导航图只展示，不接受点按 | `IMAGE-04@mac` |
| IMAGE-04 | ios/android | 点按/拖动导航图定位（`CenterOnNormalizedPoint` / 滚动偏移） | `IMAGE-04@ios` / `@android` |
| IMAGE-04 | win | 会话帧经 Skia 缩到导航尺寸，白框/百分比原生绘制 | — |
| IMAGE-05 | mac/ios/win/android | `PhotoLoadSession.ToggleEnhanced`；iOS/Android 显示「计算中…」药丸 96×28 | — |

### 控制栏
| ID | 平台 | 实现 | 差异 |
|---|---|---|---|
| TOOLBAR-* | mac | AppKit 三分组 + SF Symbols；EXIF 组整组可点开工具窗 | 按钮外观原生，几何按 token |
| TOOLBAR-* | ios | 自定义 UIKit 三分组；EXIF 块打开工具 sheet | — |
| TOOLBAR-* | win | C# 构建 WinUI 三分组；EXIF 组 `Tapped` 整组命中 | 空照片无 EXIF 块则无命中 |
| TOOLBAR-* | android | 自绘三分组；EXIF 块打开工具 BottomSheet | — |

### 分析栏
| ID | 平台 | 实现 | 差异 |
|---|---|---|---|
| ANALYSIS-* | mac | `MacAnalysisPanel` + `MacDetailPreview` / `MacDiagnosticTile` / `MacShakeFieldView`；hover 绿框 | — |
| ANALYSIS-* | ios | `iOSAnalysisHost` + UIKit 瓦片；指针 hover、手指点按定格 | `ANALYSIS-04@ios` |
| ANALYSIS-* | win | `WindowsAnalysisPanel` 实现 `IAnalysisHost`；`DisplayCrop` 出细节；`WindowsAnalysisTilePanel` 在 Measure/Arrange 排等边长 | 单层裁切（旧壳双层圆角不复刻） |
| ANALYSIS-* | android | 自绘瓦片；`DisplayCrop` 细节；点按定格 + 主图 3dp 绿框；抖动矢量线简化 | `ANALYSIS-04@android` |

### 相似聚类
| ID | 平台 | 实现 | 差异 |
|---|---|---|---|
| SIM-* | mac | `MacSimilarityPanel`；行布局从胶片栏右侧切出、列布局扩宽文件栏 | — |
| SIM-* | ios | `iOSSimilarityPanel`，复用胶片栏卡片/布局 | — |
| SIM-* | win | `WindowsSimilarityPanel`；toggle 90×32 | `SIM-02@win`：toggle 高 32 |
| SIM-* | android | `AndroidSimilarityPanel`（RecyclerView）；行切出/列扩宽；窄屏紧凑 toggle | `SIM-02@android` |

### 菜单 / 输入 / 打开 / 状态
| ID | 平台 | 入口 | 差异 |
|---|---|---|---|
| MENU-01 | mac | 窗口级 `NSMenu`（rootView + Photo 共用）；无「增强预览」项 | `MENU-01@mac` 缺项 |
| MENU-01 | ios | `UIContextMenuInteraction`；触点 1 点透明定向预览，菜单锚手 | 与双击/捏合/平移并行识别 |
| MENU-01 | win | `MenuFlyout`；栏位项文案「缩略图栏」；无「增强预览」项 | `MENU-01@win` 命名/缺项 |
| MENU-01 | android | 自绘 `PopupWindow` 锚手指，夹取屏幕、可滚动；返回键先关菜单 | — |
| MENU-02 | ios/android | 卡片长按菜单，标题 = 完整文件名 + 拍摄时间，评分 0–5 | 桌面用 tooltip 等价 |
| INPUT-01 | mac | `NSEvent` 本地监视器；`MacInput`；文本控件获焦时让位 | 苹果修饰键映射 |
| INPUT-01 | ios | `UIKey.KeyCode`(HID) → `AppleInputMapping`；47 键映射表 | 固定重复 350/60 |
| INPUT-01 | win | `WindowsInput.FromKey/FromWheel`；无主窗口文本输入 | — |
| INPUT-01 | android | `DispatchKeyEvent` → `AndroidInputMapping`；DPAD 可用 | — |
| INPUT-02 | ios/android | 适应态单指横滑翻页 24 点阈值；放大态平移；捏合连续缩放 | — |
| INPUT-02 | mac | 精确滚轮平移、离散滚轮走绑定；捏合 `MagnifyWithEvent` | 无轻扫翻页 |
| OPEN-01/02 | mac | `NSOpenPanel`（文件/文件夹分开）；Finder 打开方式；根视图拖入 | — |
| OPEN-01/02 | ios | `UIDocumentPicker`；scene openURL；`UIDropInteraction`；安全作用域书签 | 父目录不可访问 → 单图模式 |
| OPEN-01/02 | win | FileOpenPicker/FolderPicker；`Program.Main` 参数；StorageItems 拖入 | — |
| OPEN-01/02 | android | SAF `ACTION_OPEN_DOCUMENT_TREE` + 持久授权；VIEW/SEND/SEND_MULTIPLE；`OnDragListener` 未接 | `OPEN-02@android` 拖入缺口 |
| STATE-01 | mac/ios/win | 空态标签 + 错误详情（文件名/解码器原因）；无延迟加载提示 | 延迟提示未实现 |
| STATE-01 | android | 空态 + 「正在载入…」（立即出现）+ 「无法显示照片」 | `STATE-01@android` 即时提示 |
| STATE-03 | mac | 评分失败用模态 `NSAlert` | `STATE-03@mac` 模态 |
| STATE-03 | win/android | 底部提示条，4 s 自动消失（Android 4000ms） | — |
| STATE-03 | ios | 评分失败只写 `Console.Error`，无可见提示 | `STATE-03@ios` 缺口 |

### 设置 / 工具 / 边界
| ID | 平台 | 实现 | 差异 |
|---|---|---|---|
| SETTINGS-01/02 | mac | SwiftUI `NavigationSplitView` + `Form(.grouped)`；工作副本关闭回写 | 行内录制按钮 |
| SETTINGS-01/02 | ios | 半屏设置页（`iOSNativeSettingsPresenter`），五分类 | — |
| SETTINGS-01/02 | win | Win11 结构：Mica + `TitleBar` + `NavigationView` + `SettingsCard`；整行拖 | 无苹果键盘映射段 |
| SETTINGS-01/02 | android | Material 3 BottomSheet；数值 +/-、列表上下排序（拖拽/录制待补） | `SETTINGS-01@android` |
| SETTINGS-03 | mac | macOS 26 系统设置样式（侧栏/历史导航/分组表单） | 平台专属视觉 |
| SETTINGS-04 | win | 自绘录制按钮，`PreviewKeyDown` 捕获，Esc/Delete 语义 | 无原生录制控件 |
| SETTINGS-04 | android | 录制未实现，暂以文本/默认绑定呈现 | 缺口 |
| TOOLS-* | mac | 独立 AppKit 窗口（880×720），列表 + 子页两态 | 照片统计不建入口 |
| TOOLS-* | ios | PageSheet + `UINavigationController`，列表两态 | 同上 |
| TOOLS-* | win | 960×720 Mica + `NavigationView` 三分类；`SelectorBar` 切格式；DINO 3+2 | 照片统计唯一入口 |
| TOOLS-* | android | BottomSheet；EXIF 表 4 个可见列承载 7 列几何；DINO 5 瓦片 | 照片统计不建入口 |
| SCOPE-* | 全部 | 共享 presenter / 服务承担推理与计算 | — |

## status

> 按 ID 摘录四端状态（原文措辞），日期为记录日期。未列出的平台表示未见专门记录。

| ID | Windows | macOS | iOS / iPadOS | Android |
|---|---|---|---|---|
| SHELL-01 | 150% 下两向 1200×850 / 830×650 / 700×480 及最大化/还原往返程序化通过（2026-10-17）；200%/175% DPI、跨屏待验 | 主界面布局/尺寸/边距对拍通过 | 模拟器两机型程序化通过（2026-10-17，两向/自动/安全区/隐藏栏位释放/方形控件）；真机旋转/Split View 待作者手验 | 模拟器目视（2026-10-18）；程序化断言待 A6 |
| SHELL-02 | `--pv-native-smoke` 150% 窗口四态往返通过；真实拖动/最大化/还原实机验证；双击、200%/175%、跨屏待验 | `--pv-native-input` 四点断言 + 真实双击双向切换 | 平台差异（无标题栏语义） | 平台差异（窗口装饰） |
| SHELL-03 | 最大化/还原/全屏/退出全屏往返程序化通过（2026-10-17） | 全屏入口与状态持久化代码核对 | 不适用 | 不适用 |
| SHELL-04 | 缺口（无栏位显隐持久化） | `FrameAutosaveName` + 偏好存储代码核对 | 偏好存储代码核对 | 缺口 |
| FILTER-01 | 原生筛选条沿用共享语义；逐项对拍与手感待 W-M6 | 菜单对齐/间距对拍 | 模拟器两机型程序化通过（2026-10-17：三控件 90×32、计数自然宽、间距 5、toggle 90×27） | 模拟器目视（2026-10-18）；itemId 缺陷已修 |
| FILTER-02 | 共享 VM 代码核对 | 同左 | 同左 | 同左 |
| FILTER-03 | 150% 实机对拍通过（分组/间距/居中） | 对拍通过 | 程序化通过（2026-10-17） | 目视（紧凑 toggle） |
| IMAGE-01 | `--pv-native-smoke` 150%：3600×2400 代理→Full、物理 100%、导航图、EXIF 方向 1–8 程序化通过；200%/175%、跨屏、色彩/RAW 待验 | 色彩验收门已关闭（作者 2026-10-16：色彩/画质通过）；性能门未关闭 | 模拟器两机型 + 真机程序化通过（2026-10-17，`photo` 10 / `gesture` 24 checks）；P3/Adobe RGB 目视待作者 | 模拟器目视（2026-10-18）：方向 6、6000×4000 代理→Full、Fit→100%；P3 为 sRGB 回退；断言待 A6 |
| IMAGE-02 | 缺口：`ToggleFit` 只用视口中心，无对焦点优先（待对齐） | Fit/双击走共享语义，跟随对焦按 5% UV 重估；`--pv-native-smoke` PASS | 模拟器 + 真机 24 checks（双击精确物理 100%、Fit 双向、切图保持、对焦跟随、代理→Full 不跳）；真机手感待复测 | 目视：Fit→100%、导航图正确；双击物理 100% 断言待 A6 |
| IMAGE-03 | `--pv-native-smoke` 覆盖 100%/Full 与导航图 | 程序化验证 | 模拟器 + 真机捏合/平移序列断言（手指下图像点保持 ±1 点） | 目视；断言待 A6 |
| IMAGE-04 | `--pv-native-smoke` 覆盖导航图 | 程序化验证（只展示） | 程序化通过（适应态隐藏、可见框/百分比随动、拖动定位） | 目视（点按/拖动定位） |
| IMAGE-05 | `--pv-native-smoke` 覆盖增强与增强帧诊断重算 | 增强前后 GPU 帧验收（平均差 ≥6、最大差 ≥30） | 程序化通过（2026-10-17，含真实菜单点击平均差 17.7） | 目视（「计算中…」提示） |
| TOOLBAR-01 | 150% 实机对拍通过；EXIF 块整组可点实机验证；200%/175% 待验 | 见 P2/P3 | 模拟器两机型程序化通过（2026-10-17） | 目视（三分组/换行/居中）；EXIF 块入口已落地 |
| TOOLBAR-02 | 150% 对拍：50×40 / 40×40 / 30×30、244/196/280 与 204/196/280 | 对拍通过 | 程序化通过 | 目视 |
| TOOLBAR-03 | EXIF 标签/值可区分、完整值 tooltip 已落地 | 对拍通过 | 程序化通过 | 目视 |
| ANALYSIS-01 | `--pv-native-smoke`：预览/直方图/即时计算/准星/两向几何/增强帧重算程序化通过；真实素材待手验 | 本轮实机程序化验证；「提取后仍显示未提取」已修 | 模拟器两机型 + 真机 15 checks（含未提取→真实索引刷新）；触摸手感待真机 | 目视（2026-10-18）；断言待 A6 |
| ANALYSIS-02 | 单层裁切 + 原比例 `Fill`；见 P2-W §15.6/§18 | 视觉对拍 | 程序化通过（最近邻/背景 alpha） | 目视 |
| ANALYSIS-03 | `WindowsAnalysisTilePanel` 两向等边长断言通过 | 程序化验证 | 程序化通过 | 目视 |
| ANALYSIS-04 | `AnalysisPreviewTile` 自持悬停态 + `ResetHover()`，重建不点亮 | 程序化验证 | 程序化通过（hover + 点按定格） | 目视（点按定格绿框） |
| ANALYSIS-05 | 两向几何/顺序断言通过 | 行布局 6 瓦片 106、列 70，顺序角标 | 15 checks（两向几何/顺序） | 目视（6–7 瓦片约 58dp） |
| ANALYSIS-06 | 准星/细节预览/双击定位程序化通过 | 程序化验证 | 程序化通过（双击定位、点按定格） | 目视（点按落准星） |
| SIM-01 | `--pv-native-smoke`：toggle→空态→提取→列表→点选不重锚通过 | 程序化验证；点选未双高亮、toggle 闪烁已修并补真实点击断言 | 模拟器 + 真机 10 checks（双高亮/分数条/真实提取） | 目视（toggle/空态/提取/分数/让位）；断言待 A6 |
| SIM-02 | 行让位/列扩宽程序化通过 | 程序化验证 | 程序化通过（MinWidth 106、两向让位） | 目视 |
| MENU-01 | 实机三场景（空态右键、空态左键、载图后右键）通过；菜单项含分析栏勾选 | 右键菜单结构代码核对 | 模拟器 + 真机 `input` 27/28 checks（菜单项、勾选态、动作） | 目视（自绘菜单夹取/返回） |
| MENU-02 | 未单列（卡片菜单为触屏路径） | tooltip 代码核对 | 模拟器 + 真机断言（标题内容、评分读回、不切图） | 目视（卡片长按评分菜单） |
| INPUT-01 | 虚拟键/滚轮映射到共享 `AppGesture`；专项事件与手感待 W-M6 | `--pv-native-input` PASS | 模拟器 + 真机 27/28/29 checks（HID 47 键、文本获焦/模态不派发） | 目视（DPAD Right/F 触发 Next/Fit）；手感待 A6 |
| INPUT-02 | `--pv-native-smoke` 覆盖滚轮语义 | 程序化验证（精确/离散滚轮分开） | 模拟器 + 真机（轻扫 24 点、放大态平移、捏合驱动） | 目视；断言待 A6 |
| INPUT-03 | 待验 | 系统重复 | 固定 350/60 重复代码核对 | 待验 |
| OPEN-01 | 外部打开非首张定位由 `--pv-native-smoke` 断言；`Open` 命令改走统一选择器 | 拖入文件/目录作者 2026-10-16 手验通过 | 拖入走 `ExternalOpenService`，`input` checks 覆盖 | SAF 选文件夹/外部打开模拟器目视 |
| OPEN-02 | 拖放 StorageItems 实机验证 | 作者 2026-10-16 手验通过 | 模拟器 + 真机 `input` 覆盖拖入 | 拖入未接（缺口） |
| OPEN-03 | 共享 VM 代码核对 | 同左 | 同左 | 同左 |
| STATE-01 | 空首屏提示代码核对 | 空态/失败形态程序化覆盖 | 空态提示代码核对 | 目视（正在载入…） |
| STATE-02 | 空筛选「无匹配照片」150% 实机确认；解码失败待验 | 延迟加载与失败形态程序化覆盖 | 空态/失败代码核对 | 目视（无法显示照片） |
| STATE-03 | FILM-06 只读素材触发通过（提示可见、文案含「无法写入星级」、星色未变） | 模态警告代码核对 | 缺口（仅 `Console.Error`） | 目视（4 s 提示） |
| STATE-04 | 清库二次确认/InfoBar 程序化通过 | 清库失效重路由程序化验证 | 程序化验证 | 目视 |
| SETTINGS-01 | 原生已落地（2026-10-17），7 项 PASS；真实拖拽待手验 | 程序化核验 + 真实鼠标拖拽作者 2026-10-16 通过 | I4 起入口为主图菜单；关闭回写传播程序化通过；逐条视觉待手验 | 目视（五分类/关闭回写）；拖拽/录制待补 |
| SETTINGS-02 | 五分类/工作副本/关闭回写程序化通过 | 程序化核验 | 模拟器 + 真机程序化通过（六类设置传播） | 目视 |
| SETTINGS-03 | 不适用（Win11 结构） | 作者 2026-10-16 手验通过 | 不适用（系统半屏设置外观） | 不适用（Material 3） |
| SETTINGS-04 | 录制状态断言通过；真实键鼠录制待作者手验 | 真实 NSEvent 录制程序化核验 | 待作者手验 | 待补 |
| TOOLS-01 | 原生已落地（2026-10-17），5 项 PASS；真实多格式切换待手验 | `--pv-native-tools` PASS（单例/EXIF 6 组/DINO 5 瓦片/重开） | 模拟器 + 真机 7 checks | 目视（列表只有 EXIF/DINO） |
| TOOLS-02 | EXIF 7 列 + `SelectorBar` 多格式按需加载；真实切换待手验 | 6 组 / 50 标签 PASS | 7 checks | 目视（4 可见列承载 7 列几何） |
| TOOLS-03 | DINO 5 瓦片 3+2 + 刚体文本 + 照片统计 CSV 导出 PASS | DINO 5 瓦片 PASS；照片统计不建入口 | 程序化通过；照片统计不建入口 | 目视；照片统计不建入口 |
| SCOPE-01 | 分析栏/相似聚类/增强已原生；切图不隐式推理 | P3 已落地 | I5 起四类入口解禁 | A5 已落地 |
| SCOPE-02 | 设置/工具窗口及统计已原生 | 照片统计不建入口 | 照片统计不建入口 | 照片统计不建入口 |

## questions

1. **ANALYSIS 编号映射冲突**：`Design/README.md:57` 记录「原 ANL-01–04 并入为 ANALYSIS-02–05」，本次任务口径是「ANL-02/03/04 → ANALYSIS-02/03/04，ANL-01 退役」。`Design/spec/state.md` 按任务口径写，并把新规则用了 ANALYSIS-05（顺序/几何/准星/细节预览）。若以 README 为准，需把 ANALYSIS-05 再后移一位。
2. **Mac/Windows 主图菜单缺「增强预览」**：iOS 与 Android 菜单含该项（`PhotoViewer.iOS/Native/iOSContextMenus.cs:33`、`PhotoViewer.Android/Native/AndroidNativeRootView.cs:552`），macOS `PhotoViewer.Mac/Native/MacMainWindow.cs:885-899` 与 Windows `PhotoViewer.Windows/Native/WindowsMainWindow.cs:367-381` 都没有。是遗漏还是桌面端以控制栏按钮/热键为唯一入口？MENU-01 目前按「四端同序」写。
3. **评分写回失败的可见性不一致**：Windows 底部提示条 4 s（`WindowsMainWindow.cs:798-812`）、Android 同样（`AndroidNativeRootView.cs:682-693`）、macOS 用模态 `NSAlert`（`MacMainWindow.cs:536-541`）、iOS 只写 `Console.Error`（`iOSNativeRootViewController.cs:650`）。规范倾向非模态提示条（STATE-03）；macOS 是否改为提示条、iOS 是否补可见提示需要裁定。
4. **加载提示无延迟实现**：设计意图是「未命中时加载提示延迟出现，减少闪烁」（`Plans/native-rebuild-phase-2-ui.md:117`），但 Android 立即显示「正在载入…」（`AndroidNativeRootView.cs:876-882`），macOS/Windows/iOS 完全不显示加载提示（`MacMainWindow.cs:135-141`、`WindowsMainWindow.cs:853-873`、`iOSNativeRootViewController.cs:1025-1035`）。`status.loading.delay` 的数值也无来源，暂标「待定」。
5. **空筛选结果只有 Windows 有文案**：`WindowsFilmstripView.cs:104` 显示「无匹配照片」，macOS/iOS/Android 胶片栏没有对应空态（`MacFilmstripView.cs`、`iOSFilmstripView.cs`、`AndroidFilmstripView.cs`）。ui-contract FILM-05 把「无筛选结果」写作共同行为，但只有一端落地。
6. **栏位显隐持久化缺口**：macOS（`MacMainWindow.cs:106-107`）与 iOS（`iOSNativeRootViewController.cs:1042-1052`）写平台偏好，Windows 与 Android 未持久化。SHELL-03 写「应当持久化」，两端目前是缺口。
7. **菜单命名不统一**：Windows 用「缩略图栏」（`WindowsMainWindow.cs:357`，ui-contract 明确改回旧壳文案），macOS/iOS/Android 用「胶片栏」。规范正文用「胶片栏」，Windows 文案差异需登记或统一。
8. **macOS 缩放上限未声明**：`Design/tokens.json` 的 `image.zoom.max-scale`（16）备注「Mac 的 Metal 宿主未声明同一上限」。IMAGE-03 写「平台未声明时按同一值对齐」，Mac 需补实现或登记差异。
9. **ANALYSIS-05 参考点跨图保留**：`AnalysisPresenter.cs:90-93` 与 `:161-184` 显示 cosine 参考点只在构造时取网格中心、点击时更新，切图不重置。这是有意保留还是应在切图时回到中心？目前规则未写「切图重置」。
10. **Windows 标题栏双击未核实**：`WindowsTitleBar.cs` 只有按钮回调，未见双击按系统偏好的实现；ui-contract SHELL-02 的 Windows 列把「双击」列为待验。SHELL-02 要求四端一致，Windows 可能缺实现。
11. **`FILTER-03` toggle 高度**：规范统一 27，Windows 直接用 32 高的筛选控件（`Design/tokens.json` 的 `filter.toggle.height` platforms 记录 32）。是否登记为 `FILTER-03@win` 的合法原生差异。
12. **iOS 顶部安全区**：iOS 行布局筛选条从安全区前导 +10 起排（`Plans/native-rebuild-phase-3.md` 相关记录与 `MainScreenLayout.FilterBarFrames(alignLeading:true)`），规范写「不留交通灯空带」，但 macOS 列布局仍有 `shell.filter.leading-inset`（34）。两向的 leading 语义不同，需在 platforms.md 写清，避免被当成几何缺口。
