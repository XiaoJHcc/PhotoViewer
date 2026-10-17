# PhotoViewer 跨平台 UI 与交互契约

本文件是四个平台的设计对账草稿，记录已确认要求、旧版表现、当前实现和待评估差异。它不把旧壳的每个细节冻结为永久约束；产品意图与原生表现的详细分类尚未完成。

已由作者明确要求的交互与视觉意图应保留。**2026-10-15 主界面对拍决定优先于此前宽松约定**：macOS 主界面的布局结构、尺寸、边距按 Avalonia 原设计对齐；背景按原设计或原生深色环境选择，边框和圆角先采用原生表现。菜单大小、字号、字重和语义色若接近原设计或效果更好，可经真屏判断保留原生，不能以“原生控件”为理由任意改变分组、占位和留白。设置界面不属于本轮对拍范围；其他平台的数值仍须逐项评估，不能自动套用 macOS 结果。

首轮基线：2026-10-14，对照现有 Avalonia 缩略图代码、共享 VM 和作者已确认的 macOS 设计约束。本文不是全产品审计完成声明；未核验项明确列为待核验。

## 1. 维护与验收规则

1. 每项设计使用稳定 ID。修复引用 ID；改变行为时在同一任务更新规则、平台差异、验收步骤与实现入口。
2. 移植前先逐条检查触发条件、不会触发的操作、动画、边界、并发中断、持久化和错误状态，不能只比较截图。
3. 共同行为放在本文；平台控件与尺寸放在平台方案。差异先区分功能/交互意图丢失与合理的原生表现；尚无法判定的列为待评估，不自动当成缺口或新增硬约束。
4. 验收状态分为“代码核对 / 实机程序化通过 / 作者手验通过 / 待核验 / 缺口 / 阶段延期”。构建通过不升级交互验收状态。
5. 每轮平台验收记录 OS、设备、版本或工作区改动、测试素材、契约 ID 和证据路径。四个平台按同一 ID 比较；不可把其他平台的成功复制为本平台成功。
6. 新发现的旧实现 bug 不成为设计要求。先区分产品意图、实现缺陷和原生表现，再修订；源码与本文均供追溯，作者最新决定优先。细分类后续整理，现阶段不定死。

## 2. 胶片栏定位与动画

### FILM-01：选中状态与定位请求分离

改变 `CurrentFile` 负责主图与高亮；只有显式定位请求负责滚动。不能订阅所有 `CurrentFile` 变化无条件居中，也不能在重载列表时无条件居中。

| 操作 | 切换当前照片 | 主动居中 | 依据与条件 |
|---|---|---|---|
| 鼠标或触摸点选卡片 | 是 | 否 | `ThumbnailListViewModel.SelectImageCommand` 只赋值；保持用户正在浏览的列表位置 |
| 上一张 / 下一张（按钮、键盘、绑定的离散滚轮、iOS 适应态横向轻扫） | 是 | 是 | 对共享 Previous/Next 的每个入口都必须显式 `ScrollToCurrent`；到端点不环绕、不启动新定位。iOS 轻扫只在适应态生效，走同一命令入口 |
| 切换上一张（Exchange） | 是 | 否 | `OnExchange` 只恢复 `LastFile`；不可擅自等同于 Previous |
| 打开文件或文件夹，包括外部打开 | 是 | 是 | `FolderViewModel` 默认 `scrollToCurrent=true`；调用方明确传 false 时不定位 |
| 排序或筛选刷新 | 不由滚动逻辑决定 | 否 | `ApplySort/ApplyFilter` 只更新列表；不得因 ReloadData 把用户位置抢走 |
| 评分引起列表刷新 | 评分本身不切图 | 否 | 当前项暂留规则见 FILM-06；不能把改星当成导航 |
| 手动滚动列表 | 否 | 否 | 浏览未选中的卡片时保持主图和高亮身份 |
| 普通窗口尺寸变化 | 否 | 否 | 只更新几何并将原偏移约束到合法范围 |
| 缩略图、EXIF、缓存状态就绪 | 否 | 否 | 只刷新卡片内容与状态 |

### FILM-02：定位的几何与动效

- 沿列表主轴把当前卡片中心移到视口中心；横向只改变 X，纵向只改变 Y。不要只调用“确保可见”。
- 目标偏移 = `卡片起点 + (卡片长度 - 视口长度) / 2`，限制到 `[0, max(0, 内容长度 - 视口长度)]`。首尾允许偏离中心；短列表不制造空白来强行居中。
- 当前对齐参考为 **350ms，CubicEaseOut**：`1 - (1 - t)^3`，相差小于 1 逻辑单位不播放动画。这是旧版动效参数，不是四平台不可调整的常量；主图立即切换、不被动画阻塞是需要保留的交互意图。
- 新定位取消旧动画，从当前显示位置接续，最后请求生效；不回跳到旧动画起点，也不等待旧目标完成。
- 当前项不在筛选列表、空列表、隐藏栏位或视图关闭时不启动定位。布局就绪后读取真实卡片几何；虚拟化不能成为不定位的理由。
- 动画经过的可见缩略图继续加载，不能等待动画结束才显示。滚动性能由共享调度器保障，不取消 P0 主图优先级。

### FILM-03：中断与系统无障碍

用户点选卡片、滚动列表或拖动滚动条时接管列表，撤销尚未执行的定位和正在播放的动画。列表替换、方向变化、隐藏、关闭时废弃旧目标；下一次显式请求按最新几何重新计算。

此条把旧版已有的“新请求取消旧动画 / 视图解绑取消”补全为明确契约。旧 Avalonia 代码未完整覆盖用户接管；macOS 本轮补齐，其他平台待实现核对。系统启用减少动态效果时直接定位终点，保留相同触发条件；macOS 已接入且**开启态已由作者实测通过（2026-10-16）**，旧壳尚未接入，不宣称四平台已一致。

### FILM-04：布局、显隐与平台差异

上下布局为横向单行，左右布局为纵向单列，屏幕顺序严格等于筛选后的业务索引。自动布局根据窗口内容区方向，不能根据照片横竖切换。普通鼠标纵向滚轮在横向胶片栏上滚动列表，不触发主图翻页。

macOS 当前保留前轮已实现的增强：**重新显示胶片栏、切换列表方向时显式定位当前项**，仍使用 FILM-02 动画。这不是从 Avalonia 代码推导出的共同行为；其他平台尚未采用，记录为平台差异。普通 resize 和列表刷新不属于此增强。

### FILM-05：卡片视觉与点击区域

| 元素 | 共同行为 | 可平台化部分 |
|---|---|---|
| 照片区 | macOS 对拍为 80×80，90×138 卡片外框加四周 3 点占位；原比例适应留边、不拉伸 | 系统描边与圆角；其他平台尺寸另评估 |
| 当前项 | 外卡片蓝色高亮，身份来自 `IsCurrent` | 蓝色取平台语义色 |
| 主图缓存 | 图片内部蓝色细边，独立于当前项；仅 Full 缓存触发；细边必须完整可见（压在缩略图之上，不能被图片盖掉左右两边） | 透明度与细边绘制 |
| 文件名 | 与同名合并身份一致，截断可查看完整名 | tooltip 或触摸等价入口 |
| 第二行日期 | 读取共享 `PhotoDate`，不要只取 EXIF 后把回退日期丢掉；完整时间可查看 | 时间格式可随布局调整 |
| 评分 | 0–5，金色实心 / 灰色空心，独立清零；点星不选择父卡片 | 平台符号与控件 |
| 抖动 | `IsShake == true` 才显示橙色徽标；未知不是正常 | 平台绘制 |
| 加载与错误 | 保持卡片身份，滚动不等解码，失败仍可选择主图 | 占位与延迟提示形式 |

### FILM-06：评分与筛选的连续性

评分写回成功后更新内存与筛选；当前照片因改星不符合筛选时暂留，切走后再移除。不得瞬间丢失选片进度。评级按钮操作保持当前主图身份；文件写入失败不能呈现虚假成功。本文只描述 UI，写入/同名同步流程见根 AGENTS.md §5.3。

## 3. 其他设计要求与对拍参考

| ID | 共同行为或明确平台要求 | 实现与验收入口 |
|---|---|---|
| SHELL-01 | 两向/自动布局、独立栏位显隐；空间释放给主图；方形照片区与按钮不因原生化拉长 | macOS 尺寸表见 P2 UI §1；**iPad 原生壳 2026-10-17 模拟器两机型程序化通过**（`--pv-native-layout-check`：两向与自动布局、真实安全区、隐藏栏位释放给主图、60×60/50×40/30×30/40×40 方形控件不拉长）；真机旋转/Split View/Stage Manager 待作者手验；Android 原生壳待核对；**Windows 原生壳**在 150% 下两向 1200×850 / 830×650 / 700×480 及最大化/还原往返程序化通过，200%/175% DPI 与真实跨屏待验 |
| SHELL-02 | 主窗口隐藏式标题栏，系统窗口能力保留，拖动区域不得吞筛选或照片手势；标题栏区（拖动区）内**双击**按系统偏好执行窗口动作（缺省=最大化/缩放切换，`Minimize`=最小化，`None`=不动作），并吞掉该事件不落到主图；全屏无标题栏时不接管 | macOS P2 UI §1.1；隐藏标题栏的原生壳须自实现双击（AppKit 不会自带），macOS 走窗口级监视器 + `AppleActionOnDoubleClick`，回归 `--pv-native-input`；**iOS 为平台差异**：无窗口装饰/标题栏双击语义，隐藏状态栏，主图长按菜单承担窗口级入口（设置/栏位显隐），安全区口径见下"iOS/iPadOS 平台差异"；**Windows**：自绘自动显隐标题栏（50 DIP 热区，指针移开/失活隐藏），拖动区只在可见时登记、隐藏态零区域且不压标题栏按钮/不吞筛选条，caption 边界由实际按钮布局推导；`--pv-native-smoke` 在 150% 下两向三尺寸 + 最大化/还原/全屏/退出全屏往返通过；真实鼠标拖动/最大化/还原已实机验证，双击、200%/175%、跨屏与手感待验 |
| FILTER-01 | 排序/升降序/星级筛选/计数同一业务语义，菜单选择更新共享状态 | 原生控件可不同；macOS 菜单对齐与间距见 P2 UI §1.3；**iPad 用 `UIButton`+`UIMenu` 下拉，三控件 90×32、计数自然宽、间距 5、行布局横向居中/列布局 116 栏内居中；相似聚类 toggle 90×27 紧随计数（I5 起显示，参与整组居中）（2026-10-17 模拟器两机型程序化通过）**；**Windows** 原生筛选条（`WindowsFilterBar`）沿用排序/方向/星级与计数语义，逐项对拍与手感待 W-M6 |
| INPUT-01 | 快捷键沿用用户主/次绑定；输入或录制控件获焦时不误触照片快捷键 | `AppGesture`、苹果键盘映射；各平台事件核验。**iPad 原生壳 2026-10-17 模拟器 + 真机程序化通过**（`--pv-native-input-check`：HID usage → 共享手势表 47 键往返、合成派发执行共享命令、文本输入获焦与模态展示期间均不派发，见下"iOS/iPadOS 平台差异"）；**Windows** 虚拟键/滚轮经 `WindowsInput` 映射到共享 `AppGesture`，专项事件与手感待 W-M6 |
| SETTINGS-01 | 控制列表每行包含显隐、名称、主/次快捷键与右侧拖拽把手；无老式表头、无内部嵌套滚动，整表内嵌卡片平铺 | 原生控件由平台选择；macOS 用 SwiftUI `List`/`Form` 内嵌分组行（`.onMove` 拖拽排序）；**iOS**：原生半屏设置页随 I1 落地（`iOSNativeSettingsPresenter`），I4 起入口为主图长按菜单，I4 验收只覆盖"关闭回写后主屏传播"，列表逐条视觉契约待作者手验；**Windows**：W-M5 已原生（2026-10-17），左侧 `NavigationView` 分类 + `SettingsCard` 卡片行，行 = 勾选 + 名称 + 主/次录制按钮 + 行尾把手；拖拽为**整行拖**（WinUI 无常驻把手元素），真实拖拽手感待作者手验；程序化 `--pv-native-settings` 覆盖行重排回写 |
| SETTINGS-02 | 分类/分组、主副标题、开关/步进器/拉条/缩放预设卡片与 iOS 原生设置逐项对齐，保留桌面快捷键/键盘映射/清库；工作副本关闭回写，不改 JSON 字段 | macOS 用 SwiftUI 原生设置（C# 持工作副本与派生文本）；**iOS 原生设置**：半屏浮窗 + 工作副本关闭回写，`--pv-native-input-check` 在模拟器 + 真机断言布局/控制栏/快捷键/EXIF/评分/文件格式筛选的即时传播；逐条视觉与输入体验待作者手验；旧 Avalonia 设置不是原生视觉规格 |
| SETTINGS-03 | macOS 设置采用 macOS 26 系统设置样式：`NavigationSplitView` 系统 Sidebar（彩色圆角图标底板、隐藏折叠按钮）、工具栏前进/后退与分页标题、右侧 `Form(.grouped)` 原生分组表单（不再手绘卡片框） | macOS 专属视觉约束，不能直接强制其他平台也用 SwiftUI 样式；iOS 沿用自身原生半屏设置外观；**Windows** 用 Mica + WinUI `TitleBar` + `NavigationView` + `SettingsCard`（Win11 设置结构，系统 accent 与主题资源），不套 macOS 样式 |
| TOOLS-01 | 工具页入口与功能：主图右键「工具…」（Windows 名称带省略号）与控制栏 EXIF 信息块点击打开独立工具窗口；EXIF 详情按同一张照片的伴侣格式（RAW 优先、JPG/HEIF 可切换）渲染 7 列元数据；DINO 诊断显示原图缩略图 / PCA-RGB / Cosine 三张 DINO patch 瓦片与锐度 / 抖动拖影两张 CV 瓦片、刚体拟合文本，点击瓦片落准星并重算 cosine 参考点；照片数据统计选择多文件夹 + 通配符、递归扫描并导出 CSV（Windows 专属） | `ToolsViewModel` 持 `ExifDetail` / `PhotoStats` / `DinoPresenter`；DINO 数据与推理在共享 `DinoDiagnosticPresenter`，工具壳不自行驱动。macOS/iPad 为“列表 + 子页”两态（工具列表只有 EXIF 详情 / DINO 诊断，照片数据统计不建入口）；**Windows（2026-10-17）改为设置页结构**：独立 960×720（min 800×560、不进任务栏）Mica + `TitleBar` + `NavigationView` 三分类 + `SettingsCard` 页面，无列表首页/返回按钮；EXIF 7 列表格在 `SelectorBar` 多格式切换下按需加载，DINO 5 瓦片按 3+2 同宽网格排列（第六格留空、两排正方形上沿齐平）+ 全宽刚体拟合文本，照片统计用工具窗口自身句柄的文件夹/CSV 选择器；`--pv-native-tools` 5 项 PASS（含 3+2 同宽与顶对齐断言），真实多格式切换与文件选择器待作者手验 |
| IMAGE-01 | 100% 使用源像素对应屏幕物理像素；Full 热替换位置/朝向/缩放不跳变 | 主流程见根 AGENTS.md §5.1/§5.7/§5.8；色彩门单独验收。**iPad 原生壳**：`--pv-native-photo-check` 在模拟器两机型 + 真机程序化通过（代理先行、物理 100% 换 Full 且变换保持、中心色差在阈值内、八方向 GPU 角点、色卡线性值）；P3/Adobe RGB 目视与跨屏色彩待作者手验；**Windows**：150% 下 `--pv-native-smoke` 覆盖 3600×2400 代理→Full、导航图与 EXIF 方向 1–8（方向 6 另验共享 `DisplayCrop`）；真实相机八方向素材、200%/175% DPI、跨屏与 W-M7 色彩/RAW 待验 |
| SIM-01 | 相似聚类面板由筛选条 toggle 显隐，状态持久化；面板三态（空 / 部分提取 / 有相似项）与未提取计数、`提取全部`/`补齐全部` 按钮或进度一致；卡片第二行显示相似度百分比与柱状占比（75% 起）；**点击卡片切主图但保留原锚点**——被点中的卡片自身高亮（取主图身份 `IsCurrentImage`），胶片栏锚点卡片继续高亮（`IsCurrent`），两处同时可见；卡片星级只改评分不切图 | 共同语义与计数/按钮文案来自共享 `SimilarityPanelViewModel` 与 `SimilarityService`；macOS 原生面板 `Native/MacSimilarityPanel.cs`，几何与旧壳 `SimilarityListView` 同口径（列布局扩宽文件栏、行布局从胶片栏右侧切出，MinWidth 106），展开/收起与选中并存由 `--pv-native-similarity` 回归；**iPad 原生面板 `Native/iOSSimilarityPanel.cs` 复用共享 `SimilarityPanelGeometry` 与胶片栏卡片/布局**：行布局从胶片栏右侧切出（相似项出现后重算宽度）、列布局扩宽文件栏、MinWidth 106，真实 toggle 点击、真实“提取全部”、分数条与双高亮由 `--pv-native-similarity-check` 程序化通过（模拟器两机型 + 真机）；**Windows**：`--pv-native-smoke` 覆盖空态→提取→列表→点选切主图不重锚；逐项视觉与鼠标手感待作者手验 |
| ANALYSIS-01 | 分析栏由主图右键菜单与命令 `ToggleAnalysisView` 显隐；瓦片顺序为 对焦点（仅有 Sony 对焦数据时）/ 中心 / 直方图 / DINO PCA / Cosine x,y / 锐度 / 抖动拖影；正方形瓦片边长 = `floor((可用主轴长度 − 20 − 8×(n−1)) / n)`；诊断瓦片共享准星与 cosine 参考点，细节预览 hover 联动主图绿框、双击定位到裁剪中心；库/cache 无结果时显示 `未提取`，开启"即时计算DINO/CV"后改占位 `计算中…` 并回退现算；**批量提取写库后当前照片的诊断瓦片立即自行出图**（不需切图） | 编排在共享 `Core/AI/AnalysisPresenter`（旧壳 VM 与 macOS/iPad/Windows 原生分析栏共用）；入库经由 `FolderFeatureIndexer.FeaturesWritten` 让分析栏作废该指纹缓存并重路由；macOS 几何与旧壳 `AnalysisView` 同口径（行布局在主图下方、列布局在其右侧，交叉轴 = 边长 + 20），回归 `--pv-native-analysis`；**iPad 原生分析栏**沿用同一几何（主轴取安全区内容长），细节预览另有“手指点按定格绿框”平台差异（见下），`--pv-native-analysis-check` 覆盖两向几何、瓦片顺序/绘制、准星、F16 代理与 8 位 P3 Full 的直方图/裁剪（中心像素对主图帧读回）、未提取→真实索引刷新、增强与点按定格；**Windows**：`WindowsAnalysisPanel` 实现 `IAnalysisHost` 消费共享 `AnalysisPresenter`，细节预览用 `DisplayCrop`；`--pv-native-smoke` 覆盖预览/直方图/即时计算/准星/两向几何/增强帧诊断重算；真实素材与手感待作者手验 |
| ANL-01 | 原生路径不得运行旧壳 `AnalysisViewModel`（原生展示时其 `AnalysisVisible` 恒为 false，只保留薄适配，不订阅、不建位图）；分析栏编排统一走共享 `AnalysisPresenter`，Windows 不自绘独立数据链路 | Windows 第三轮实机崩溃根因：`AnalysisViewModel` 在 WinUI 回调里建 Avalonia 位图 → `0xC000027B`，仅在库里有提取结果时触发。现 `WindowsAnalysisPanel` 实现 `IAnalysisHost`，消费 `AnalysisPresenter`/`AnalysisTile`，细节预览走 `DisplayCrop`，增强归 `PhotoLoadSession`（`ToggleEnhanced`；`Image` 返回增强帧，压力 Warning 自动关闭）；旧「诊断瓦片恒用原片」的 Windows 专属偏差已移除。见 P2-W §15.2/§16 |
| ANL-02 | 分析栏瓦片：内容不带自己的圆角（贴外框内沿，圆角只由外框 `CornerRadius` 裁切产生），且按**照片原比例**置入（居中留边），不拉满方形瓦片 | 旧壳 `DiagnosticTile` 曾用"外框 6 + 内层 InnerClip 5"双层圆角，150% 下呈"卡里套卡"；Windows 已改为单层裁切 + 共享 `AnalysisPresenter.AspectRatio` 非方形位图（长边 512），瓦片内部 `ContentHost` 按同一纵横比给出内容矩形并 `Fill`，直方图瓦片保持 1:1（旧壳同为 `AspectRatio="1"`），旧壳待退役前统一；见 P2-W §15.6 第 2 条 / §18 |
| ANL-03 | 分析栏瓦片一律等边长，边长由布局系统在测量/排列时给出；首开、换向、窗口变形后不得出现"挤成一坨 / 大小错位" | Windows 用 `WindowsAnalysisTilePanel` 在 Measure/Arrange 里排布，`--pv-native-smoke` 两向断言等边长（P2-W §15.6 第 3 条）；禁止"读实际宽度再推迟回写子项尺寸"的写法 |
| ANL-04 | 主图绿框只在指针悬停细节预览瓦片时显示；换图、代理→全分辨率、瓦片尺寸变化等重建裁剪图的行为不得自动点亮绿框 | 旧壳同口径（`DetailPreview` 仅高亮态推 `SetDetailHighlight`，`ImageViewModel` 图片尺寸变化时先置 null）。Windows 由 `AnalysisPreviewTile` 自持悬停态 + `ResetHover()` 实现，换图/代理→Full/瓦片尺寸变化重建 `DisplayCrop` 裁剪图不自动点亮；见 P2-W §15.6 第 4 条 |
| SCOPE-01 | 延期功能保留配置，原生壳不显示不可用入口、不在切图隐式推理 | macOS P3 已落地分析栏 / 相似聚类 / 工具窗口 / 增强预览（照片数据统计仍为 Windows 专属，macOS 不显示入口）；**iPad I5 起四类入口全部解禁**（分析栏/增强在控制栏与主图菜单、相似聚类 toggle、工具页 sheet+导航控制器；照片数据统计仍为 Windows 专属不建入口），阶段边界见 [P3 清单](../Plans/native-rebuild-phase-3.md)。**切图不隐式推理**：相似聚类只在面板展开且文件栏可见时重算（`SimilarityPanelViewModel`），收起即取消在途任务；抖动徽标只读库、分析栏只在可见时工作；**Windows 主屏**：分析栏/相似聚类/增强预览已原生可用（相似聚类只在面板展开时计算，切图不隐式推理）；设置窗口 W-M5、工具窗口（2026-10-17，右键「工具…」与控制栏 EXIF 信息块入口）均已原生；照片数据统计为 Windows 专属并在原生工具窗口提供，macOS/iPad 不建入口 |

这些条目混合已确认要求与待分类的对拍参考，不代表全部为硬约束或相应平台已验证通过。后续主图手势、错误状态、触摸交互及设置项可继续补充与分类。

### iOS/iPadOS 平台差异（2026-10-17，I3 主屏布局 + I4 输入 + I5 P3 功能）

> 本节条目均为**程序化通过或已登记差异**（模拟器两机型 + 真机报告）；触摸手感、旋转与 Split View/Stage Manager、硬件键盘/鼠标/触控板、文件拖入与色彩目视等作者手验项见 [iPad 验收清单](../Plans/native-rebuild-phase-ipad-acceptance.md)，不以程序化结果代替。

- **重新显示/换向定位**：原为 macOS 独有增强（[FILM-04](#film-04布局显隐与平台差异)），**iPad 原生壳同口径采用**——隐藏后重新显示胶片栏、切换列表方向（含自动布局换向）时显式定位当前项；普通尺寸变化（Split View/Stage Manager 拖动、旋转但方向未变）只约束偏移，不抢用户位置。程序化覆盖见 `--pv-native-layout-check`（栏位显隐释放空间、每向首个布局的定位）。
- **主图长按菜单（替代旧壳长按浮层）**：`UIContextMenuInteraction`，手指长按与触控板/鼠标辅助点按同一入口；项与 Mac 原生右键菜单同序——打开文件夹…、胶片栏（勾选）、分析栏（勾选）、控制栏（勾选）、增强（勾选）、工具…、设置…（I5 起全部为真实入口）；勾选态每次弹出读共享状态。**菜单用触点处 1 点透明定向预览，弹出时照片不抬升/不缩小、菜单锚在手指**（系统默认的整视图预览会让主图退到一边）；卡片菜单保留系统默认抬升（不移动列表布局）。与滚动视图捏合/平移、双击互不抢占。
- **卡片上下文菜单（触屏 tooltip 等价物，[FILM-05](#film-05卡片视觉与点击区域)）**：菜单标题 = 完整文件名 + 拍摄时间（`yyyy-MM-dd HH:mm:ss zzz`，与 Mac tooltip 同格式），功能为"评分 0–5"（勾选当前星级）；写回走共享评分路径，**不改变当前主图**（[FILM-06](#film-06评分与筛选的连续性) 暂留语义由共享列表负责）。卡片星级按钮几何保持 90×138/14×14 对拍结果（不因触屏放大，低于系统 44 点建议），触控目标由菜单补足。
- **主图缩放/平移**：透明 `UIScrollView` + 透明缩放内容视图承载系统捏合与平移（`ViewForZoomingInScrollView`），支持放大、放大态惯性与缩到适应以下回弹；`DidZoom` 重算居中内边距。**2026-10-17 修复**：此前缺少缩放内容视图，UIKit 从不响应捏合（`zoomScale` 恒 1、无平移），只剩直接改视口的双击。模拟器以真实 Option+拖拽捏合与单指拖动验证。
- **适应态横向轻扫 = 上一张/下一张**：只有视口处于适应态时生效（放大态横向拖动仍是平移），走共享 Previous/Next，因此与按钮/键盘一样触发显式定位（[FILM-01](#film-01选中状态与定位请求分离)）；端点不环绕。单指识别（双指捏合起手不翻页），水平位移阈值 24 点且需明显大于纵向位移，与滚动视图平移同时识别。
- **硬件键盘**：`UIKey.KeyCode`（HID usage）经共享 `AppleInputMapping` 映射为 `AppGesture`，再查用户主/次绑定（与 Mac 同一张命令表与苹果键盘映射口径，持久化字段不变）；`PressesBegan` 派发，已按住的键由固定节奏计时器重复（350ms 延迟 / 60ms 间隔——iOS 没有读取系统键盘重复速率的公开接口）。输入控件获焦或设置页/文档选择器等模态展示期间不派发（[INPUT-01](#3-其他设计要求与对拍参考)）。
- **触控板/鼠标滚轮**：离散滚轮（鼠标滚轮）只走用户鼠标绑定（与 Mac 的非精确滚轮口径一致，默认上一张/下一张），不驱动主图平移；触控板连续滚动仍由 `UIScrollView` 平移、捏合不变。
- **拖入**：`UIDropInteraction` 挂主屏根视图，接收"文件"App 的文件/文件夹 URL（安全作用域保留），走与 openURL/"打开方式"相同的共享 `ExternalOpenService` 路径（与 Mac 根视图拖入同语义）。
- **设置写回即时刷新**：从主图菜单打开原生设置页，关闭（下拉或关闭按钮）统一经工作副本回写；布局模式、栏位显隐（主屏菜单）、控制栏命令显隐/顺序、快捷键重绑、EXIF/评分分组与文件格式筛选立即在主屏生效。
- **安全区与状态栏**：背景铺满屏幕边缘、内容收在真实 `SafeAreaInsets` 内；`UIViewControllerBasedStatusBarAppearance = true` 时 `Info.plist` 的 `UIStatusBarHidden` 被忽略，根控制器显式 `PrefersStatusBarHidden() => true`，实测 `safe.Top = 0`（如改为显示状态栏，布局按真实 insets 自动让位）。主图满幅（可越过左右安全区，与 macOS 主图铺满观片区同义）。**行布局筛选条从安全区前导边 + 10 起排，不在左上留 macOS 交通灯空带**（列布局仍是 116 栏内居中）；Mac 保持居中让位。
- **控制栏重建**：EXIF 项/命令显隐或顺序改变后按共享设置整体重建；旧分组先 `RemoveFromSuperview` 再释放，重建幂等（2026-10-17 修复设置回写后多组 EXIF/按钮重叠）。
- **两向验收方式**：iPadOS 26 的当前窗口模式下 **`requestGeometryUpdate` 不接受程序化方向变更**（实测返回 `The current windowing mode does not allow for programmatic changes to interface orientation.`），故两向几何用显式 `--pv-layout-mode row|col` 与真实尺寸/安全区一致的布局数学核对；真机旋转与手感由作者手验（未通过项不以模拟器结果代替）。
- **分析栏入口与几何（I5）**：筛选条之外由主图菜单与 `ToggleAnalysisView` 命令（控制栏按钮按共享设置显隐）打开；行布局在主图下方、列布局在其右侧，交叉轴 = 瓦片边长 + 20，主轴瓦片起点额外叠加真实安全区内边距；分析栏可见时主图升 Full（细节预览要全分辨率源）。
- **增强预览验收（2026-10-17 加严）**：控制栏命令/主图菜单 → `PhotoLoadSession.ToggleEnhanced()`；验收在确定性低对比度素材上比较增强前后 GPU 帧（共享 `EnhancementAcceptance`，平均差 ≥6、最大差 ≥30）并要求关闭后完全还原。只断言 `EnhancedDisplay` 无法回归"点了没反应"。
- **细节预览触屏语义（ANALYSIS-01 的 iOS 差异，2026-10-17 程序化通过）**：触控板/鼠标悬停沿用 hover 绿框；**手指点按在“定格”与“解除”间切换**——定格后离开不消失，再点一次或点主图空白解除；双击把主图定位到裁剪中心并放大到 100%。
- **相似聚类面板（I5）**：toggle 90×27 显示；行布局按共享 `SimilarityPanelGeometry` 从胶片栏右侧切出（相似项出现后按卡片数重算宽度，封顶内容宽一半）、列布局扩宽文件栏（MinWidth 106）；卡片复用胶片栏卡片，第二行分数百分比 + 75% 起柱状条；选中取主图身份、胶片栏锚点保留。
- **工具页（I5）**：主图菜单「工具…」与控制栏 EXIF 信息块打开 `PageSheet` + `UINavigationController`；列表页只有 EXIF 详情 / DINO 诊断（照片数据统计为 Windows 专属）；EXIF 详情用 `UITableView` + 共享 7 列几何（多格式可切换），DINO 页复用分析栏同一套 UIKit 瓦片。

### TOOLBAR-01：本轮工具栏对拍（2026-10-15）

明确修正的信息/交互丢失：EXIF 标签和值应可区分、完整值可查看；EXIF/评分/命令保留分组及顺序，不因空间不足将评分拆散；设置的命令显隐/排序与主次绑定应反映到按钮，显式显示的评分快捷命令应能辨别星数。这些是使用工具栏所需的信息与操作含义。

macOS 对拍几何：EXIF 小块 50×40，按钮点击区 40×40，评分 30×30；横排评分 0→5，竖排 5→0；整组按空间换行/换列，每行/列居中。外 padding 10、组间距 10；EXIF 每块主轴两端各 4，块间另隔 4；评分组主轴两端各 8；命令间隔 8。同一行/列的组在交叉轴拉伸、组内控件居中。四项 EXIF/六个命令时，横排自然组长为 244/196/280，竖排为 204/196/280；与旧壳同尺寸真实布局比对，不再把错轴 inset 当作原生差异。

EXIF 与评分块沿用原设计 #222 深灰底，文字保留系统层级；避免动态 NSColor 在无外观上下文时被固定为白色。SmallSquare 系统按钮保留方形实际绘制与原生反馈。菜单、字体、边框、圆角可按本轮原生表现判断；P3 起 EXIF 详情、分析栏、增强预览入口在原生壳恢复可用（P2 曾按阶段隐藏）。

**iPad 原生控制栏（2026-10-17 程序化通过）**：分组口径与上列几何完全共用共享 `ControlBarLayout`——三分组（EXIF 信息块 50×40 / 评分 30×30 / 命令 40×40）、外 padding 10、组间距 10、整组换行/换列与行内居中、横排评分 0→5 与竖排 5→0；显隐与顺序来自共享设置，**I5 起分析栏/增强预览等命令按设置显隐真实可用**（SCOPE-01 解禁）；EXIF 信息块点击打开工具页（sheet + 导航控制器）。

Windows 对拍几何（2026-10-17，150% 缩放实机）：同一套数值成立 —— EXIF 小块 50×40、命令 40×40、评分 30×30、外 padding 10、组间距 10、EXIF 每块主轴两端 4 + 块间另隔 4、评分组主轴两端 8、命令间隔 8；
竖排组高 EXIF 204 / 评分 196、横排组宽 EXIF 244 / 评分 196，与旧壳实测一致。P3 主屏入口落地后原生壳已恢复「分析栏」「增强预览」两个命令，先前因按钮数少 120 DIP 造成的换行差异不再存在；
换行、居中与整组拉伸仍走同一套规则。图标集取平台原生（Windows Segoe Fluent Icons ↔ 旧壳 uicons），与 macOS 用 SF Symbols 同策略。**EXIF 信息块整组可点**（2026-10-17）：点击任意 EXIF 块与右键「工具…」共用 `MainViewModel.OpenToolsWindow`，打开原生工具窗口；`Tapped` 挂在整组上，空照片（无 EXIF 块）无命中区域。

## 4. 四平台对账表

“共享旧壳代码核对”表示阅读 Avalonia/VM 源码，尚不能替代该平台实机验收。macOS 下表的程序化状态由 `NativeFilmstripAcceptance` 对真实 AppKit 窗口验证，证据在 `release/native-p2-acceptance/filmstrip-*-checks.txt`。iOS 列的程序化状态来自 iPad 模拟器（iPad mini / iPad Pro 两机型）与 iPad 真机的 `--pv-native-*-check` 报告；程序化通过不等于作者手验，手感/设备项见 [iPad 验收清单](../Plans/native-rebuild-phase-ipad-acceptance.md)。Windows 列的程序化状态来自 150% DPI 下 `--pv-native-smoke` 的 24 项 + `--pv-native-settings` 的 7 项 + `--pv-native-tools` 的 5 项定向回归（`release/native-win-acceptance/checks.json`、`settings-checks.json`、`tools-checks.json`），设置/工具回归含 Mica/NavigationView/TitleBar 等控件加载与页面内容断言，不代表完整 W-M6 验收；200%/175% DPI、真实跨屏、真实相机八方向素材、W-M7 色彩/RAW、设置真实拖拽/录制手感、工具真实多格式切换/文件选择器仍未验收。

**作者 2026-10-16 裁定**：macOS 原生壳的**色彩、画质、手感、减少动态效果开启态、真实鼠标拖放（设置页拖拽排序 / 主窗口拖入文件）**验收通过；**Avalonia 旧壳不再做产品验收（它将被原生壳替换）**，故表中 Windows / iOS / Android 列的"共享旧壳代码核对 / 旧壳缺口"只作迁移期参考，**不作为验收结论**——各平台验收在各自原生壳落地后单独建立。macOS 列的交互项已无"待作者验收 / 待核验"格子（最后的 FILM-05 延迟加载与 FILM-06 真实改星交互已于 2026-10-16 补成程序化断言）；仍未关闭的是性能门（见 P2 验收未关闭项，作者裁定"以后再做"）。

| 契约 | Windows | macOS 原生 | iOS/iPadOS | Android |
|---|---|---|---|---|
| FILM-01 触发条件 | P2-W：外部打开非首张定位由 `--pv-native-smoke` 断言（偏移离开列表头部）；点选/筛选不居中与共享 VM 同源；外部打开文件夹走文件夹模式；真实滚轮与手感待作者手验 | 本轮实机程序化验证 | **模拟器两机型程序化通过（2026-10-17，`--pv-native-layout-check`：点选第 5 张后列表偏移不变、卡片立即高亮、主图身份随之切换）**；真机 `--pv-native-input-check` 覆盖轻扫翻页走共享 Previous/Next 并显式定位 | 共享旧壳代码核对 |
| FILM-02 350ms/边界/末次请求 | 350ms 三次缓出 + 端点夹取；定位为收敛式（布局未就绪/列表重建后补做），行为与旧壳实测一致；真实滚动条拖动与手感待作者验收 | 本轮实机程序化验证；**手感作者 2026-10-16 验收通过** | **模拟器两机型程序化通过（2026-10-17：真实"下一张"按钮触发 350ms 三次缓出，采样含中间帧，终点 = 共享居中偏移公式；首尾不环绕且停在合法范围）**；手感待作者真机 | 共享旧壳代码核对 |
| FILM-03 用户接管/减少动态效果 | **`--pv-native-smoke`**：点选/滚轮/拖动滚动条/触摸接管即撤销动画与排队请求（在途一帧用同位置无动画请求压掉）；系统「减少动态效果」直接到终点；两分支程序化通过，手感待作者手验 | 本轮验证接管；减少动态效果读取系统值（`NSWorkspace.AccessibilityDisplayShouldReduceMotion`），**开启态作者实测通过（2026-10-16：显式导航直接到终点、点选仍不定位）** | **模拟器两机型程序化通过（2026-10-17：动画中触发滚动委托的用户拖动即撤销定位并停在用户位置；注入减少动态效果开关后 30ms 内直达终点）**；系统开关真实开启态与手感待作者真机 | 旧壳缺口，待补 |
| FILM-04 两向顺序/滚轮 | 两向顺序与滚轮轴 150% 实机验证；换向按当前项重新定位（项距 96↔144 变化后旧偏移无意义），不再甩回顶部；200%/175% DPI、真实跨屏与手感待验 | 既有与本轮程序化验证；显隐/换向定位为已登记差异 | **模拟器两机型程序化通过（2026-10-17：两向分区、卡片 96×144/80 方形、屏幕顺序 = 筛选业务索引、隐藏栏位把空间让给主图）**；旋转、Split View/Stage Manager、触摸手感与滚轮方向待作者手验（换向/重新显示定位为 iOS 同口径差异，见下） | 触摸及显隐待核验 |
| FILM-05 信息与标识 | 文件名取 `DisplayName`、第二行拍摄时间带完整时间 tooltip；空筛选结果给「无匹配照片」；卡片几何与真窗口对拍；「已缓存」描边改为独立一层压在缩略图之上（150% 实机确认四边可见）；延迟加载专项与手感待验 | 已补 PhotoDate 回退/完整时间 tooltip、原生占位/加载状态；卡片几何与真窗口对拍通过；**延迟加载与失败形态已程序化覆盖**（自有素材：身份/几何不等解码、好图缩略图与日期异步补齐、滚动后可见卡片身份立即有效、不可解码文件仍可选为主图且不呈现伪造图像），回归 `--pv-native-filmstrip`，证据 `filmstrip-many-checks.txt` | **模拟器两机型程序化通过（2026-10-17：卡片外框 96×144/占位区 90×138/照片 80 方形/星级 14 点/文件名尾部截断/第二行拍摄时间/抖动徽标/当前项高亮取 `IsCurrent` 均按几何与身份断言）**；完整文件名与拍摄时间由卡片上下文菜单标题承载（I4，模拟器 + 真机 `--pv-native-input-check` 均断言标题内容），抖动详情暂无触屏等价入口；触控目标差异见下 | 平台实机待核验 |
| FILM-06 评分筛选连续性 | 写回失败 → 主图区底部 4 s 状态提示、星色不变；`--pv-native-smoke` 以只读素材触发通过；真实改星交互与筛选暂留待作者手验 | 共用 VM；**完整真实改星交互已程序化覆盖**：胶片栏卡片星级按钮真实点击 → 文件读回星级一致 + 主图身份不变 + 点星不选中父卡片（命中测试断言）；改星后落出筛选**仍暂留**、切走后移除且计数与筛选一致；收尾还原评级与筛选，回归 `--pv-native-toolbar`，证据 `toolbar-ui-checks.txt` | **模拟器两机型程序化通过（2026-10-17：卡片星级按钮真实触发 → 自有副本 XMP 读回一致、命中不落整卡、主图身份不变；改星后暂留在筛选列表内、切走后落出且计数与共享筛选一致；收尾还原评级与筛选）**；真机 `--pv-native-input-check` 断言卡片上下文菜单评分读回一致且不切主图，筛选暂留仍由共享列表承担 | 平台实机待核验 |
| SHELL-02 隐藏式标题栏与拖动区 | 自绘自动显隐标题栏：指针进 50 DIP 热区显示、移开/失活隐藏；拖动区只在可见时登记（隐藏态零区域），不压标题栏按钮、不吞筛选条；caption 边界由实际按钮布局推导。150% 下两向三尺寸 + 最大化/还原/全屏/退出全屏往返程序化通过；真实鼠标拖动/最大化/还原已实机验证，双击、200%/175%、跨屏与手感待验 | 顶边拖动区不吞筛选/照片手势（`--pv-native-similarity` 断言拖动区与 toggle 不相交）；标题栏双击自实现并按 `AppleActionOnDoubleClick` 分派（`--pv-native-input` 四点断言 + 真实双击真机双向切换） | 平台差异：iPad 无窗口装饰与标题栏双击语义，隐藏状态栏；设置与栏位显隐由主图长按菜单承担，安全区口径见下 | 窗口装饰属平台差异 |
| SETTINGS-01/02 | **原生已落地（2026-10-17）**：五分类、深色 + Mica、`NavigationView` 分类 + `SettingsCard` 卡片行、整行拖排序；`--pv-native-settings` 7 项 PASS（深色/Mica、NavigationView+TitleBar+NumberBox+InfoBar、五分类/尺寸、开关+NumberBox+滑条、重排、录制状态、关闭回写）；真实拖拽、真实键鼠录制、清库确认与预设增删待作者手验 | SwiftUI 原生设置（macOS 26）：程序化核验通过（五分类渲染/历史导航/勾选往返重绘/真实 NSEvent 录制/排序/缩放/关闭回写）；**真实鼠标拖拽排序作者 2026-10-16 手工验收通过**（程序化注入仍受阻，不以集合 Move 冒充） | 原生半屏设置页随 I1 落地（`iOSNativeSettingsPresenter`）；I4 起入口为主图长按菜单，关闭回写后布局/控制栏/快捷键/EXIF/评分/格式筛选传播在模拟器 + 真机程序化通过（`--pv-native-input-check`）；逐条视觉与输入契约**待作者手验** | 旧壳；原生待迁移 |
| IMAGE-01 | **`--pv-native-smoke`（150%）**：3600×2400 源代理→Full 与物理 100% 热替换；导航图；EXIF 方向 1–8（GPU 交换链四角回读 + 导航图四角，方向 6 另验共享 `DisplayCrop`）程序化通过；真实相机八方向素材、200%/175% DPI、跨屏与 W-M7 色彩/RAW 待验 | **色彩验收门已关闭（作者 2026-10-16：色彩通过、画质通过）**；性能门（大图 <100ms）见 P2 验收未关闭项 | **模拟器两机型 + 真机程序化通过（2026-10-17，`--pv-native-photo-check`：代理先行、物理 100% 换 Full、变换保持、代理↔Full 中心色差在阈值内、八方向 GPU 角点、色卡线性值）**；P3/Adobe RGB 目视与跨屏色彩**待作者手验** | 见 P1，原生待迁移 |
| SIM-01 相似聚类面板 | **`--pv-native-smoke`（150%）**：相似聚类 toggle → 空态 → 真实「提取全部」→ 分数列表 → 点选切主图且不重锚，程序化通过；逐项视觉与鼠标手感待作者手验 | 本轮实机程序化验证（toggle 双向一致 / 两向让位几何 / 列表与空态互斥 / 收起还原）；作者真机使用后反馈的两处问题（点选未双高亮、toggle 闪烁与"点不动"）已修并补真实点击断言，修复后未见新反馈 | **模拟器两机型 + 真机程序化通过（2026-10-17，`--pv-native-similarity-check`：toggle 双向与真实点击、两向让位/收起还原、MinWidth 106、空态/列表态与共享 VM、卡片分数条、真实“提取全部”、相似卡片点选双高亮且锚点保留）**；触摸手感待作者真机 | 共享旧壳代码核对 |
| ANALYSIS-01 分析栏 | **`--pv-native-smoke`（150%）**：`WindowsAnalysisPanel` 实现 `IAnalysisHost` 消费共享 `AnalysisPresenter`；细节预览（`DisplayCrop`）/直方图/即时计算/准星/两向几何/增强帧诊断重算程序化通过；真实素材与手感待作者手验 | 本轮实机程序化验证（两向瓦片几何 / 顺序角标 / 落准星 / 直方图与细节预览出图 / 增强联动）；作者反馈的"'提取全部'后仍显示未提取"已修（入库即作废缓存并重路由），修复后未见新反馈 | **模拟器两机型 + 真机程序化通过（2026-10-17，`--pv-native-analysis-check`：菜单/控制栏入口、两向瓦片几何与顺序、最近邻/背景 alpha、准星、F16 代理与 8 位 P3 Full 直方图/裁剪像素、未提取→真实索引刷新、增强开关与会话还原、点按定格绿框）**；触摸手感（含定格）待作者真机 | 共享旧壳代码核对 |
| TOOLBAR-01 控制栏 | 150% 实机对拍通过（见 §3 TOOLBAR-01 段）；EXIF 信息块整组可点打开工具窗口已实机验证；200%/175% DPI 与手感待验 | 见 P2/P3 | **模拟器两机型程序化通过（2026-10-17：三分组 50×40/30×30/40×40、换行/居中、I5 命令可用、EXIF 块打开工具页）**；真机 `--pv-native-tools-check` 覆盖 EXIF 块入口；触摸手感待作者真机 | 共享旧壳代码核对 |
| TOOLS-01 工具页 | **原生已落地（2026-10-17）**：Mica + `TitleBar` + `NavigationView` 三分类（EXIF 详情 / 照片数据统计 / DINO 诊断）+ `SettingsCard`，960×720（min 800×560、不进任务栏）；入口为右键「工具…」与控制栏 EXIF 块；EXIF 7 列表格、DINO 5 瓦片 + 刚体拟合、照片统计文件夹/通配符/CSV 导出；`--pv-native-tools` 5 项 PASS；真实多格式切换与文件夹/CSV 选择器待作者手验 | 独立 AppKit 工具窗口（EXIF 详情 / DINO 诊断，照片数据统计不建入口）；`--pv-native-tools` PASS（懒创建单例、EXIF 6 组、DINO 5 瓦片、关闭重开） | sheet + 导航控制器，列表页只有 EXIF 详情 / DINO 诊断；`--pv-native-tools-check` PASS（模拟器两机型 + 真机）；触摸手感待作者真机 | 旧壳；原生待迁移 |

发现登记：Avalonia 可见范围估算代码存在方向判断不一致，不能复制为设计；可见中心预取与 macOS 每帧上报节奏也尚未专项对齐。本轮修定位条件和动画，不借此重写共享预取。

## 5. FILM 回归操作清单

使用至少 12 张可读照片，左右/上下布局与名称升/降序分别执行；另测短列表和空结果。测试不修改用户照片。

1. 滚至任意位置，点选可见卡片：立即切主图/高亮，等待超过 350ms，列表不动（FILM-01）。
2. 点击下一张、按上一张绑定：主图立即切换，100ms 左右有滚动中间帧，约 350ms 后中心/边界正确（FILM-01/02）。
3. 用显式请求跨越多项，再连续换方向：从当前帧接续，最终定位最后请求，无回跳（FILM-02）。
4. 动画中点选或滚轮滚动：当前位置由用户接管，旧动画不再推进；排队但未执行的请求也失效（FILM-03）。
5. 切换上一张、排序、筛选、刷新卡片：不主动居中；列表变短只做边界限制（FILM-01）。
6. 第一张/最后一张、短列表：无越界/额外空白；端点再翻页不环绕（FILM-02）。
7. 外部打开非首张定位目标；`scrollToCurrent=false` 不定位；目标不在筛选中不乱跳（FILM-01/02）。
8. 横向胶片栏用普通纵向滚轮：滚动列表，主图身份不变；触控板惯性保留（FILM-04）。
9. macOS 隐藏后显示/换向定位当前项；普通 resize 不抢位置；动画中隐藏或关闭无残留回调（FILM-03/04）。
10. 开启系统减少动态效果（macOS：系统设置 → 辅助功能 → 显示 → 减弱动态效果）：同样的显式请求直接到终点，点选仍不定位（FILM-03）。

程序化已覆盖的部分见平台验收报告；外部打开参数、短/空结果、真实滚动条拖动和触控板手感仍须独立记录，不因清单存在就标通过。第 10 条（减少动态效果开启态）已由作者于 2026-10-16 在 macOS 实测通过。iPad 对应手验步骤（含轻扫翻页、点按定格、拖入）见 [iPad 验收清单](../Plans/native-rebuild-phase-ipad-acceptance.md)。Windows 侧第 4/7/10 条已由 `--pv-native-smoke` 覆盖（接管后偏移不再推进、外部打开非首张定位、减少动态效果直接到终点）；第 2/3/6/8/9 条仍以作者手测为准。

## 6. 追溯入口

- 基线定位/动画：[ThumbnailListView.axaml.cs](../PhotoViewer/Views/Main/File/ThumbnailListView.axaml.cs)。
- 基线触发：[ThumbnailListViewModel.cs](../PhotoViewer/ViewModels/Main/File/ThumbnailListViewModel.cs)、[ControlViewModel.cs](../PhotoViewer/ViewModels/Main/ControlViewModel.cs)、[FolderViewModel.cs](../PhotoViewer/ViewModels/Main/FolderViewModel.cs)。
- macOS 实现：[MacMainWindow.cs](../PhotoViewer.Mac/Native/MacMainWindow.cs)、[MacFilmstripView.cs](../PhotoViewer.Mac/Native/MacFilmstripView.cs)。
- Windows 原生实现：[WindowsMainWindow.cs](../PhotoViewer.Desktop/Native/WindowsMainWindow.cs)、[WindowsLayout.cs](../PhotoViewer.Desktop/Native/WindowsLayout.cs)(几何常量唯一来源)、[WindowsTitleBar.cs](../PhotoViewer.Desktop/Native/WindowsTitleBar.cs)、[WindowsNavigator.cs](../PhotoViewer.Desktop/Native/WindowsNavigator.cs)(缩放指示器)、[WindowsFilmstripView.cs](../PhotoViewer.Desktop/Native/WindowsFilmstripView.cs)(定位/接管/减少动态效果)、[WindowsAnalysisPanel.cs](../PhotoViewer.Desktop/Native/WindowsAnalysisPanel.cs)(共享 `AnalysisPresenter` 宿主 + `DisplayCrop` 细节预览)、[WindowsAnalysisTilePanel.cs](../PhotoViewer.Desktop/Native/WindowsAnalysisTilePanel.cs)(Measure/Arrange 等边长)。
- macOS 控件/几何：[P2 UI 方案](../Plans/native-rebuild-phase-2-ui.md)；阶段验收：[P2 验收](../Plans/native-rebuild-phase-2-acceptance.md)；进度：[handoff](../Plans/native-rebuild-handoff.md)。
- iPad 实现：[iOSNativeRootViewController.cs](../PhotoViewer.iOS/Native/iOSNativeRootViewController.cs)、[iOSPhotoView.cs](../PhotoViewer.iOS/Native/iOSPhotoView.cs)、[iOSFilmstripView.cs](../PhotoViewer.iOS/Native/iOSFilmstripView.cs)；阶段状态：[iPad 执行清单](../Plans/native-rebuild-phase-ipad.md)；作者手验：[iPad 验收清单](../Plans/native-rebuild-phase-ipad-acceptance.md)。
