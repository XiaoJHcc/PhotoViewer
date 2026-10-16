# Phase 3 执行清单 — macOS 原生缺失功能补全（分析栏 / 相似聚类 / 工具页 / 增强预览）

> 2026-10-16 立项，隶属 [native-rebuild-master-plan-1.md](native-rebuild-master-plan-1.md) 与
> [Phase 2 执行清单](native-rebuild-phase-2.md) 决策点 2 的顺延项。前置：P2 主界面几何/工具栏/胶片条/设置
> 已按作者要求对拍通过（见 [P2 验收](native-rebuild-phase-2-acceptance.md)）。
>
> **作者要求（2026-10-16）**：把剩余缺失功能按原 Avalonia 设计重构到 Mac 原生；布局结构、尺寸、边距、
> 功能要点、选中/高亮含义尽量一致；下拉框、右键菜单等小型纯功能控件保留 mac 原生规范；工具窗口可参考
> 设置窗口的重构方式，允许按 mac 原生风格实现，不要求与旧壳逐像素一致。

## 0. 范围（从 SCOPE-01 解禁）

| # | 功能 | 旧壳入口 | 原生落点 |
|---|---|---|---|
| 1 | 分析栏（细节预览 ×2 + 直方图 + DINO PCA / Cosine / 锐度 / 抖动拖影 + 抖动矢量场 + 判定角标） | 主图右键菜单「分析栏」、命令 `ToggleAnalysisView` | `Native/MacAnalysisPanel.cs` + 瓦片视图 |
| 2 | 增强预览（`ImageEnhancer` CLHE，跨切图持续生效） | 命令 `ToggleEnhance`（默认 `E`，控制栏显示） | `PhotoLoadSession` 增强态 + 控制栏按钮 |
| 3 | 相似聚类面板（DINO CLS 相似度、分数柱、抽取进度、未提取计数） | 筛选条「相似聚类」toggle | `Native/MacSimilarityPanel.cs` + 筛选条 toggle |
| 4 | 工具窗口（EXIF 详情 / DINO 诊断；照片数据统计仍为 Windows 专属，macOS 不显示入口） | 主图右键菜单「工具」、控制栏 EXIF 块点击 | `Native/MacToolsWindow.cs` 等（AppKit 独立窗口） |

`Design/ui-contract.md` SCOPE-01 随之改为「macOS 已完成」，不再隐藏入口。

## 1. 架构决定

1. **共享层抽中性 presenter，两个展示层复用，不复制编排逻辑。** 旧壳 VM 保留原公开 API 与 AXAML 模板，
   内部改为适配器；原生层直接消费 presenter 的中性产物（`DecodedImage` / `ShakeField` / 文本）。
   - 新增 `Core/AI/AnalysisTile.cs`、`Core/AI/AnalysisPresenter.cs`（分析栏）。
   - 新增 `Core/AI/DinoDiagnosticPresenter.cs`（DINO 诊断工具页）。
   - `ViewModels/Main/AnalysisViewModel.cs`、`ViewModels/Tools/DinoDebugViewModel.cs` 改为适配器
     （只做 `DecodedImage` → Avalonia `Bitmap` 转换与控件层状态）。
2. **增强预览下沉到展示会话。** 原生展示由 `PhotoLoadSession` 编排，增强态因此落在会话内
   （`IsEnhanced` / `ToggleEnhanced()` / `Image` 返回增强图），与旧壳 `ImageViewModel` 的增强语义一致；
   旧壳行为不变。
3. **分析栏细节预览的裁剪归各自展示层**，因为裁剪源在两侧形态不同（Avalonia 已有全分辨率
   `Bitmap`；原生需要一次显示朝向的小图解码）。presenter 只提供归一化中心与对焦框。
4. **工具窗口用 AppKit，不复用 SwiftUI 设置桥。** 理由：DINO 诊断页的瓦片、准星、矢量场与主窗口分析栏
   同构，直接复用原生瓦片视图可保证两侧渲染一致；EXIF 详情是长分组列表，AppKit 表格足够。设置窗口的
   SwiftUI 桥保持现状不动。
5. **几何与视觉严格按旧壳对拍**：分析栏瓦片边长 = `floor((可用长度 - 20 - 8×(n-1)) / n)`；外框 `#222` /
   `#444` 1 点边 / 圆角 6 / 内圆角 5 / 角标药丸 `#88000000` 8,4 圆角 5,0,5,0 / 占位 12 号 `#777` /
   热力图 `BitmapInterpolationMode=None`（原生 closer：`NSImageScaling` + 关闭插值）/ 准星白色 1 点、
   臂长 12 / 细节预览高亮 `#00FF00`。

## 2. 里程碑（全部完成，验证记录见 §3）

- **P3-M1 共享层中性化**：`AnalysisTile` + `IAnalysisHost` + `AnalysisPresenter` + `DinoDiagnosticPresenter`；
  `AnalysisViewModel` / `DinoDebugViewModel` 改适配器（`ToolsViewModel` 持 presenter 供两处共用）；
  `PhotoLoadSession` 增强态；`SimilarityPanelViewModel` 摘除原生早退。四平台构建零错误、无新增警告。
- **P3-M2 原生分析栏**：`MacAnalysisHost` / `MacAnalysisPanel` / `MacDetailPreview` / `MacDiagnosticTile` /
  `MacShakeFieldView`；接进 `MacMainWindow` 两向布局、右键菜单（顺序与旧壳一致）、`ToggleAnalysisView`；
  分析栏可见时主图升 Full；主图绿框与双击定位接线。回归 `--pv-native-analysis`。
- **P3-M3 原生增强预览**：控制栏按钮（`ToggleEnhance` → `wand.and.stars`）+ 快捷键 + 默认 `E` 绑定；
  增强图进分析栏（直方图/细节跟随，设置开启时 4 张诊断瓦片实时重算）。同一套件验证开关与还原。
- **P3-M4 原生相似聚类**：筛选条 `FilterToggleButton`（90×27，末位，紧随计数 5 点）+ `MacSimilarityPanel`
  （卡片复用 `MacThumbnailItem` + 分数柱、三态、进度与未提取计数）；行布局从胶片栏右侧切出（含旧壳 MaxWidth
  规则）、列布局扩宽文件栏；`LegacyLayoutAcceptance` 恢复 toggle 参与对拍并重出旧壳基线。回归
  `--pv-native-similarity` 与 `--pv-native-layout`。
- **P3-M5 原生工具窗口**：`MacToolsWindow`（懒创建单例，880×720 深色，两态外壳）+ `MacExifDetailView`
  （7 列分组标签行、多格式切换）+ `MacDinoDiagnosticView`（2×3 瓦片 + 刚体文本，只消费共享 presenter）；
  门面走 `NativeWindowPresenter`，入口为主图右键菜单「工具」与控制栏 EXIF 块点击；照片数据统计不建入口。
  回归 `--pv-native-tools`。
- **P3-M6 验收 + 文档**：八套原生回归全 PASS；AGENTS.md（根 / Mac / Core/AI / Core/Image / ViewModels 两处）+
  `Design/ui-contract.md`（SCOPE-01 改判、新增 ANALYSIS-01 / SIM-01）+ handoff 同步。

## 3. 验收门

程序化证据（隔离冒烟会话，`release/native-p2-acceptance/`）：

- [x] 原生分析栏：`--pv-native-analysis` **PASS**（日志 `[NativeSmoke] PASS analysis`）；检查清单 `native-analysis-checks.txt` 记录行布局 6 张 106 正方形瓦片（gap 8 / padding 10 / cross 126）、列布局 70 / cross 90、瓦片顺序与角标、点击落准星、直方图与细节预览出图（proxy=False）、关闭后主图恢复。
- [x] 增强预览：同一套件验证 `ToggleEnhance` 经 `PhotoLoadSession` 生效并还原（增强图 8 MB 实测）。
- [x] 基线回归：`--pv-native-input` / `--pv-native-analysis` / `--pv-native-similarity` / `--pv-native-tools` / `--pv-native-settings` / `--pv-native-filmstrip` / `--pv-native-toolbar` / `--pv-native-layout` 八套全部 **PASS**（分析栏、相似聚类与共享层改动未破坏既有对拍）。
- [x] 工具窗口：`--pv-native-tools` **PASS**（日志 `[NativeSmoke] PASS tools`）；清单 `native-tools-checks.txt` 记录懒创建单例、深色外观与 880×720、列表页两入口（无 Windows 专属项）、EXIF 页 6 组 / 50 标签渲染 + 返回按钮、DINO 页 5 张瓦片 + presenter 状态就绪、关闭与重开。
- [x] 四平台构建：Mac / Desktop / iOS / Android 全部 **0 错误**；Android 仅 `PhotoViewer.Android/Core/` 两个文件的既有基线警告（非本次改动文件），iOS 与 Desktop 0 警告，Mac 保持既有 NU1903 三条。
- [x] 相似聚类：`--pv-native-similarity` **PASS**（日志 `[NativeSmoke] PASS similarity`）；清单逐项记录 toggle 双向一致、行布局让位、列布局扩宽（cross ≥ MinWidth 106）、两向收起后几何逐项还原、顶边拖动区不覆盖 toggle、相似项卡片与胶片栏锚点高亮并存、列表/空态与 VM 一致。布局基线已随筛选条新增 toggle 重出：`--pv-native-layout` PASS，`--legacy-ui --pv-legacy-layout` PASS 6 轮，旧壳实测与原生逐项吻合（列布局筛选条高 204 / 排序项 13,34,90,32 / toggle 13,167,90,27；行布局 toggle 竖向居中 12.5），仅计数文本按系统字体自然宽度有约 3.5 点的既有已登记差异。
- [ ] 旧壳（Windows/iOS/Android）分析栏与 DINO 诊断页只经编译与共享 presenter 实测覆盖，Avalonia 适配层的视觉结果留待对应平台实机（本轮无 Windows/移动端设备）。
- [ ] 真实照片端到端（打开目录 → 翻图 → 开分析栏 → 增强 → 相似聚类 → 工具页）无异常、无崩溃。
- [ ] 真实鼠标拖放、手感与真屏色彩验收仍留作者人工（不以程序化结果替代）。

### 3.1 作者反馈修复（2026-10-16）

| 反馈 | 根因 | 修复 | 回归 |
|---|---|---|---|
| 相似聚类面板里点选后该卡片没有高亮（胶片栏锚点高亮保留） | `MacThumbnailItem.Refresh` 的卡片描边一律取列表锚点 `IsCurrent`，相似面板也因此看不到自己的选中项 | `Bind(..., selectByDisplayedImage)` 让相似面板取主图身份 `IsCurrentImage`，胶片栏保持 `IsCurrent` | `--pv-native-similarity` 新增断言：面板卡片与胶片栏锚点卡片同时高亮，且锚点 `IsCurrent` 不被抢走 |
| 「提取全部」之后 DINO/CV 诊断瓦片仍显示「未提取」（徽标已亮） | 提炼前算出的空 entry（含"未提取"）被 `AnalysisResultCache` 缓存；入库后既不作废缓存也没有任何信号让分析栏重路由 | `FolderFeatureIndexer` 每写成功一组即 `AnalysisResultCache.Invalidate(fingerprint)` 并抛 `FeaturesWritten`；`AnalysisPresenter` 订阅它（与 `ShakeFlagService.RecheckRequested`）后重新路由；`Refresh` 先归位 UI 线程 | `--pv-native-analysis` 新增断言：未入库照片四张瓦片为「未提取」→ 跑一次真实索引 → 瓦片自行出图且期间未切图 |
| 相似聚类按钮"有时可点、有时不可点" | 顶边悬停拖动区（窗口拖动层）右段起点用的是「计数」右边缘，新增在计数之后的相似聚类 toggle 正好被盖住；拖动层随鼠标进出顶部显隐，于是按钮时灵时不灵 | 拖动区右段起点改取最后一个筛选控件的右边缘；`FilterToggleButton.IsOn` 改为值不变时不重绘 | `--pv-native-similarity` 新增断言：两个拖动区与 toggle 的 Frame 均不相交 |
| 相似聚类按钮"点不动"（上一轮修复引入） | 把 `IsOn` 的 getter 改成读私有字段后，**点击时 AppKit 翻转的是 cell 的 State**，`Activated` 里回读 `IsOn` 永远是旧值 → 回写共享状态被 setter 的等值早退吃掉，点击等于没发生（外观还会被 AppKit 翻成 On，看似"亮着但没反应"） | 彻底不依赖 AppKit Toggle 语义：按钮改用 `MomentaryPushIn`（无系统外观），勾选态由类自持，点击在内部翻转后经 `Toggled` 回调回写共享状态；样式按「勾选 × 悬停」四态应用并与已应用组合比较，避免重复重绘 | `--pv-native-similarity` 新增**真实点击**断言：`PerformClick` 后共享状态与面板开合随之变化；此前套件只做程序化赋值，才漏掉这条回归。同时补齐旧壳的悬停配色（`#333`/`#666`/`#CCC`，勾选悬停 `#26477A`/`#4A85C4`/`#CFE0F5`） |
- [ ] 旧壳（Windows/iOS/Android）分析栏与 DINO 诊断页只经编译与共享 presenter 实测覆盖，Avalonia 适配层的视觉结果留待对应平台实机（本轮无 Windows/移动端设备）。

