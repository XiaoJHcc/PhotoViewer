# P2 原生产品验收

## 作者验收结论（2026-10-16）

作者对 macOS **原生壳**的实跑结论：**色彩通过、画质通过、手感通过、系统"减弱动态效果"开启态通过**；设置页列表拖拽排序与主窗口拖入文件/目录（真实鼠标拖放）亦于 2026-10-16 手工验收通过。本轮另修两个缺陷（关闭窗口崩溃、标题栏双击不能最大化），并新增对应回归断言，详见 [P3 清单](native-rebuild-phase-3.md) §3.2。

作者同时裁定：**Avalonia 旧壳不做产品验收 —— 它是要被原生壳替换掉的**。因此本文与 [ui-contract](../Design/ui-contract.md) 里一切"旧壳对照 / 共享旧壳代码核对 / 旧壳缺口待补"的条目只作迁移期参考，**不再作为验收项**；各平台验收在各自原生壳落地后单独建立（macOS 已完成，下一个是 iOS）。

仍未关闭的项（作者 2026-10-16 裁定：**性能相关一律"以后再做"，暂缓不是放弃**）：

- **大图性能门**：RAW 冷图约 1.7–1.9s，未达 fit <100ms；HEIF/OUT-JPG/灰阶矩阵需真实素材实测。
- **胶片栏滚动帧率 / 内存峰值对 Phase 1 基线的复测**、**RAW 冷图 1.7–1.9s 的分段耗时剖面**（只测不改）：同属延后。
- **切图卡顿**（预载与缓存清理）：作者判定属共享层问题，转后续专项。

待作者决定的非性能项（不属于本轮收尾，勿擅自执行）：

- **发布通道切原生**：`Release` 目前默认旧壳（`PhotoViewer.Mac/Program.cs` 的 `#if DEBUG` 分流），切 dmg 默认内容属主计划 Phase 6。
- **退役 Avalonia 旧壳**：删旧壳/共享 Avalonia UI/适配层，需其他平台原生壳就位（下一个是 iOS）。
- **macOS 分发公证与上架**：现 DMG 为 adhoc 签名（`publish.sh` 用 `codesign -s -`），若要免除 Gatekeeper 提示需 Developer ID + 公证；主计划只规划了 iOS 上架，**macOS 侧未立项**。

已程序化补齐的两项契约覆盖（2026-10-16，证据见 `release/native-p2-acceptance/`）：

- **FILM-05 延迟加载与失败形态**（`--pv-native-filmstrip`，`filmstrip-many-checks.txt`）：自有素材上断言列表身份/几何不等解码、好图缩略图与日期异步补齐、滚动后可见卡片身份立即有效、不可解码文件仍可选为主图并报错且不呈现伪造图像。
- **FILM-06 完整真实改星交互**（`--pv-native-toolbar`，`toolbar-ui-checks.txt`）：胶片栏卡片星级按钮真实点击后文件读回一致、主图身份不变、点星不选中父卡片；改星落出筛选仍暂留、切走后移除且计数与筛选一致；收尾还原。

说明：鼠标拖放类交互无法用合成事件验证（系统事件注入权限受限，程序化只能验集合 Move），这类项的产品结论以作者实操为准，不冒充程序化通过。

## 启动

执行 VS Code **Debug Mac**（Debug 默认原生），或 **Run Mac** 复用已构建应用。旧壳对照：

```sh
open -n PhotoViewer.Mac/bin/Debug/net10.0-macos/PhotoViewer.Mac.app --args --legacy-ui
```

Release 发布通道仍为旧壳，原生通过 `--native` 进入；验收前不切换 dmg 默认内容。

## 人工操作清单

- 打开真实 JPEG/HEIF/RAW 或目录；确认目标照片选中、胶片条可滚动、排序/星级筛选/计数正确。
- 主窗口隐藏标题栏；交通灯与筛选器无重叠。上下/左右/自动布局、胶片条/控制栏显隐、系统全屏正常。
- 缩略图区保持方形；当前蓝描边、Full 缓存淡蓝内边、星级与“抖”徽标含义正确。点缩略图星级不会切换主图。
- 双击、F、预设缩放、捏合、拖动、触控板平移与离散滚轮翻图；100% 为源像素对屏幕物理像素 1:1。代理→Full 不跳位置，导航图与可见范围同步。
- 设置窗口固定深色外观，由 SwiftUI 承载 macOS 26 系统设置风双栏（`NavigationSplitView` + `Form(.grouped)`）：文件/预览/控制/EXIF/AI；格式/按钮/EXIF 拖动排序，复选框单独点击，行间插入与拖动到滚动区边缘的自动滚动需手工确认。控制功能的勾选/名称/主次快捷键同一行，采用苹果修饰键和方向键符号；录制/清空/取消/冲突可见。关闭设置后应用，重启检查持久化。
- 评分文件写回、同名合并同步、失败提示；Finder“打开方式”、Dock/窗口拖入及文件 URL 再次打开。
- 系统预览与应用对拍 sRGB/P3/Adobe RGB、灰阶、高细节素材及 OUT-JPG；外接 sRGB 屏拖动后无需重载。帧导出不能代替此屏幕合成器验收。

## 程序化核验与产物

- `Tools/ColorTestTool`：23 项独立矩阵/传递函数、广色域负值/超一、非对齐行距与 8 位往返，最大线性分量误差 0.000488。
- `Tools/generate-native-color-fixtures.py`：系统 ICC 三色域色块/灰阶 + 8 EXIF 方向，使用可写的 XMP Rating 测试槽。
- `--pv-native-smoke <绝对图片路径>`：真实 AppKit 窗口、Metal 上屏、Full、翻图、共享外部打开队列、布局、五类设置实际落盘、无 Avalonia 主图副本、评级/CV/DINO/指纹三表索引读写。生成文件与 RAW 副本目录必须是 agent 自有测试目录。8 个 EXIF 方向独立 GPU 角点验证，ICC sRGB/P3/Adobe 与无 ICC EXIF Adobe 均通过。
- `release/native-p2-acceptance/`：JSON 报告及实际原生控件绘制/GPU 照片帧合成图，构建及运行日志。设置/SQLite 测试独立持久化，原始 TestCase 照片未修改。
- 四平台构建：Mac、Windows、iOS 模拟器、Android 均已编译；Windows/iOS 0 警告，Mac NU1903 与 Android 14 项未改文件中的警告为现有基线。最终检查以阶段清单记录为准。

最近程序化结果：ColorTestTool **23** 项；JPEG 色卡/八方向 **39** 项；RAW 副本 **17** 项；生成 HEIF（sRGB/P3）**14** 项。设置副本编辑期间缓存/键盘映射不变，关闭后同步运行时并实际落盘；分析输入的八方向与 GPU 对拍一致。HEIF 生成样本用于链路验证，不代替相机大图性能测试。

复跑示例（先执行 Debug Mac 构建）：

```sh
dotnet run --project Tools/ColorTestTool
PhotoViewer.Mac/bin/Debug/net10.0-macos/PhotoViewer.Mac.app/Contents/MacOS/PhotoViewer.Mac --native --pv-native-smoke "$PWD/release/native-p2-fixtures/color-srgb.jpg"
```

色卡由 `Tools/generate-native-color-fixtures.py` 生成，需 Pillow 和 macOS 系统 ICC。报告包含实际等待加载的时间；RAW 冷图约 1.7–1.9s，尚未满足 fit <100ms 目标，不宣称性能门通过。原生设置绘制导出可能遗漏系统选中背景，最终以窗口实际显示为准。

## 计划修订与待验范围

主界面严格几何对拍（2026-10-15，macOS 26.6.2 / arm64 / 本工作区未提交改动）：仅主界面，设置不改。`--legacy-ui --pv-legacy-layout <绝对测试图片>` 采集原 Avalonia 两向 × 1200×850 / 830×650 / 700×480 的真实客户区几何；测试视觉树隐藏 P3 相似聚类按钮，统一四项 EXIF/六个命令及评分。`--pv-native-smoke --pv-native-layout <同一图片>` 验证对应原生几何、卡片占位/内框、短横向列表居中、显隐释放、自动方向、150 长边等比导航图和真实命令。均使用隔离设置/数据库，主布局验收只读照片。产物 `legacy-layout-geometry.json`、`native-layout-geometry.txt`、`native-layout-checks.txt` 与两套 `*-layout-{col|row}-{宽}.png`。视图导出不等于系统截图；加 `--pv-layout-hold` 可在每方向 830×650 停留 30 秒供系统取证。窗口截图已在 KimiCU 检查：原生信息块不再出现白块，系统菜单/按钮外观保留。主图区、胶片条容器和三组控制栏跨六布局共 30 项对比通过（分组坐标精确匹配；计数保留系统字体自然宽度，17 点布局行高与旧版一致）。最终作者视觉/手感确认已于 **2026-10-16 通过**（见文首作者验收结论），不把程序化结果升级为作者验收。

本轮最终复跑：`layout-main-final.log` 为 `PASS layout`，`layout-toolbar-final.log` 为 `PASS toolbar`，`layout-filmstrip-final.log` 为 `PASS filmstrip`，`layout-gpu-final.log` 为 `PASS 39 checks`（包括新等比导航帧上的八方向 GPU/分析口径采样）。`layout-build-final.log` 零错误、无新增编译警告，保留既有 NU1903；ONNX CoreML 分区提示是既有运行时信息。已修首批集合重载时业务数量先于 AppKit 数量就绪导致的越界布局日志，最终胶片条 stderr 无该告警。KimiCU 校准期间的真窗口截图归档为 `layout-native-window-col.jpg` / `layout-native-window-row.jpg` / `layout-legacy-window-col.jpg`；最终几何以报告为准（列布局计数行高最后从系统自然高度统一为旧版 17 点）。测试仅改自有 fixture 的评级槽，未写用户照片；当轮未标记设置手验、跨屏色彩及触控板手感通过 —— 这三项已于 **2026-10-16 由作者统一验收通过**（见文首）。

工具栏对拍回归（2026-10-15）：`--pv-native-smoke --pv-native-toolbar <自有测试图片路径>`；两向布局与 830×650 / 700×480 / 1200×850 三档窗口验证整组排列不重叠、EXIF 小块与文字层级、方形按钮实际绘制像素、评分方向、显隐/排序/主次绑定、编号评分快捷命令、空照片状态，以及真实上一张/下一张和评分按钮。评分只写 `native-p2-toolbar-fixtures` 自有副本，读回 XMP 且确认主图不切换。全启用的超长命令组验证系统横向滚动。报告 `toolbar-ui-checks.txt`，按钮像素 `toolbar-button-surface.png`；实际 WindowServer 对拍截图为 `toolbar-legacy-reference.png` / `toolbar-native-before.png` / `toolbar-native-after.png`。本轮工具栏程序化 `PASS toolbar`，无新增编译警告；尺寸和材质属于当前原生表现，未冻结为四平台规格。

胶片条定向回归：`--pv-native-smoke --pv-native-filmstrip <绝对图片路径>`，使用作者 `PhotoViewerTest` 的五组 HIF/ARW 与 12 张色卡，按 [FILM-01–04](../Design/ui-contract.md) 核对左右/上下布局、名称升降序、真实卡片点击不居中、普通赋值/交换/排序/筛选不抢位置、显式导航的动画中间帧与居中终点（首尾截断）、末次请求覆盖和点击/滚轮接管、显隐恢复定位、复用身份及横向鼠标滚轮。报告 `filmstrip-five-checks.txt` / `filmstrip-many-checks.txt`；不修改作者照片。触控板手感与滚动手感已于 2026-10-16 由作者验收通过；**系统"减少动态效果"开启态已由作者 2026-10-16 实测通过**。

> 减少动态效果怎么验（作者问过"这是什么"）：它是 macOS 的**辅助功能**开关 —— 系统设置 → 辅助功能 → 显示 → **减弱动态效果**（Reduce Motion）。开启后本应用的显式导航（上一张/下一张、外部打开定位）应**直接跳到终点**、不再播放 350ms 三次缓出；点选卡片仍然不居中。该值只由系统提供、进程内无法伪造，所以只能人工开一次开关核对。**作者 2026-10-16 实测通过。**

2026-10-14 本轮两组实跑均 `PASS filmstrip`：日志 `filmstrip-interaction-contract.log` / `filmstrip-interaction-photos.log`；12 张色卡的四种方向/排序组合均有动画中间帧和终点记录，五组实拍同时覆盖横向不溢出的短列表。主图保持立即切换；动画手感已于 2026-10-16 由作者验收通过，仍不以程序化通过代替作者验收。

筛选菜单对齐回归：同一胶片条定向回归实际打开三个原生菜单，读取菜单辅助功能屏幕坐标与按钮屏幕坐标，要求左边缘误差 ≤1 逻辑点、菜单不窄于按钮、菜单与按钮垂直间距在 2–8 点范围内、当前项勾号保留；键盘选择菜单项后检查共享排序属性更新。macOS 26 实测三项垂直间距均为 3 点。系统限制导致屏幕边缘避让时仍由 AppKit 调整。

设置定向回归（2026-10-14）：`--pv-native-smoke --pv-native-settings <绝对图片路径>`，验证 DarkAqua、NSSplitViewController 系统侧栏、五分类、原生勾选、同行快捷键录制/删除/取消、pasteboard writer、排序后的项目身份、隐藏/重开设置窗口后的绑定释放，以及 760/1060 两档窗口宽度无列溢出和快捷键列宽稳定。报告 `settings-ui-checks.txt`；`settings-window-0.png` 至 `settings-window-4.png` 为宿主 screencapture 取得的真实窗口截图，包含系统侧栏材质。视图缓存导出中的白色侧栏是 NSVisualEffectView 离屏导出限制，不代表真实窗口外观。实际鼠标拖放的**程序化**验证被系统事件注入权限阻止、从未通过，因此不以集合 Move 测试冒充；该项改由作者 2026-10-16 手工实操验收通过。

设置页 macOS 26 系统设置风重构（2026-10-15）：右侧分页逐项对齐 iOS 原生设置——隐藏标题栏 FullSizeContentView、系统侧栏彩色圆角图标底板、右侧前进/后退历史胶囊与分页大标题；开关、步进器（等宽数值 + 加减胶囊）、连续拉条、下拉与缩放预设行取代原数值文本框；`MacReorderList` 改为无表头、无内部滚动的内嵌平铺表，新增最右拖拽把手列与文件格式主副标题。`settings-ui-checks.txt` 新增侧栏彩色图标、分页标题历史前进后退、内嵌列表无表头/五列/完整平铺、文件格式主副标题五项断言；五分类截图 `settings-0.png` 至 `settings-4.png` 已按新设计重出。`settings-window-*.png` 是上一版设计的宿主 screencapture，本轮会话无屏幕录制权限未重拍，侧栏材质与彩色底板以真机窗口为准；真实鼠标拖放改由作者 2026-10-16 手工验收通过（程序化注入仍受阻）。

设置页 SwiftUI 原生化（2026-10-15，替换上一版 AppKit 手搓实现）：设置窗口改由 `Native/SwiftSettings/MacSettingsUI.swift` 用 SwiftUI（macOS 26）实现，C# 侧 `MacNativeSettingsPresenter` 只保留工作副本、校验、派生文本与持久化，两侧通过 JSON 状态快照 + `@_cdecl` C ABI 动作回传通信；`MacReorderList.cs` 已删除。C# 的 csproj 新增 `_BuildSwiftSettingsDylib` 目标把 Swift 编译成 `MonoBundle/libMacSettingsUI.dylib`，并沿用 `_FixDylibCodeSigning` 签名。五分类逐项对齐 iOS 原生设置并找回桌面特性（快捷键录制、苹果键盘映射、清库）；界面为 `NavigationSplitView` 系统侧栏 + 右侧 `Form(.formStyle(.grouped))`，不再手绘卡片框。验收 `--pv-native-smoke --pv-native-settings` 新增/改写断言：深色外观 + `NSHostingController` 挂载、五分类分组表单渲染、分页历史前进后退、勾选动作往返并触发 SwiftUI 重绘、真实 `NSEvent` 录制/Delete 清空/Escape 取消、文件格式/快捷键/EXIF 三处排序、760/1060/860 三档窗口缩放、关闭回写与重开恢复，并通过 `screencapture -l` 取得真实窗口位图 `settings-window.png`（本轮授予屏幕录制权限，彩色侧栏底板与导航按钮已取证）。记录：合成 `NSEvent` 的滚轮增量可能为 NaN，直接喂 `JSONSerialization` 会抛异常，故录制事件改为手工拼装 JSON 且只读与事件类型相关的字段。设置页专项 `PASS settings`、完整 P2 `PASS 39 checks`，构建 0 错误、无新增警告；真实鼠标拖放与手感均已由作者 2026-10-16 手工验收通过（拖放的程序化注入仍受阻，产品结论以实操为准）。

主图输入回归修复（2026-10-16）：`MacInput` 拆出 `FromRaw` 供 SwiftUI 录制复用后，`FromEvent` 改为一次性求值全部字段（`KeyCode`/`ButtonNumber`/`ScrollingDeltaY`/`CharactersIgnoringModifiers`），而 NSEvent 对与事件类型无关的字段会抛 `NSInternalInconsistencyException`；异常经 `MarshalManagedException` 记录后被吞，表现为主图右键菜单不弹出、空首屏左键无法打开文件，且全部键盘快捷键一并失效。改为按 `NSEventType` 只读对应字段后再复用 `FromRaw`。新增 `--pv-native-smoke --pv-native-input`（不加载照片）产出 `native-input-checks.txt`：断言鼠标/键盘/抬起事件映射不抛异常且正确、空首屏提示覆盖主图中心且透传点击、方向键经共享命令表被消费、左键打开原生文件选择器（桩实现免模态）、右键弹出原生上下文菜单（菜单代理捕获 `MenuWillOpen`）。`PASS input` ×3 稳定；同时复跑基线 `PASS 39 checks` 与设置 `PASS settings`。冒烟进程非最前台窗口时系统不派发首击，故点击直接投递主图视图，与胶片栏回归同一做法。

- Apple 表面使用 **ExtendedLinearSrgb**，匹配 F16 线性数据；代理也保留 F16 广色域。
- 加载编排使用共享 **PhotoLoadSession**，旧 ImageViewModel 展示状态留给旧壳；此设计减少新旧变换状态耦合，保持旧壳可回归。
- 冷条目使用统一 LRU 的代理档/淘汰策略；不以 sRGB8 作为广色域唯一中间态。F16 与池化实际分配字节参与账本，GPU 客户端单独登记。
- 仓库实际评分路径是**已有 XMP Rating 的原位更新**，RAW 样本同样原位更新；独立 `.xmp` 的字节写服务可工作，但当前没有自动创建/关联 RAW sidecar。此前总纲与根文档的“RAW 自动 sidecar”描述失准，不能记作已回归的产品功能；P2 保留实际现有行为，新增自动 sidecar 需单独立项。
- 色彩跨屏对拍、手感与真实鼠标拖放（设置页拖拽排序、主窗口拖入文件）已由作者 2026-10-16 验收通过（见文首作者验收结论）；性能目标 <100ms 的大图、HEIF/OUT-JPG 矩阵仍以真实素材实测裁定，不能由小色卡冒烟代替。
