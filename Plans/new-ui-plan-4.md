# 新 UI 全平台合规起点 · Plan 4 · 布局与原生控件收拾

> 依据：[Plan 3](new-ui-plan-3.md) 的静态首屏成果、提交 `cae08ac` 对 Apple 两端原生控件与规范的修正，以及作者最新裁定。分支：`new-ui`。
> 阶段号 `C1–C4` 只用于本计划、验收证据与提交信息，不进入 `Design/` 和代码注释。

## 1. 目标

Plan 3 把 iOS 敲定的静态布局推广到了 Web / Android / macOS，但 Mac 验收暴露出规范本身写错（EXIF 背景与对齐）和原生写法缺失（iPad 触控板 hover）等问题，作者已在 iOS 与 Mac 上修正。本阶段把**能用布局与原生控件做到的部分**在全部平台收拾到合规，统一后续工作的起点：

- 外观对标链：**Android → iOS**（手机竖横屏与平板宽形态），**macOS → iPadOS 宽形态**（行 / 列 / 自动），**Web → macOS**（宽形态以 Mac 为外观参照，含悬停形状；手机形态保留 iOS 口径）。Windows 的界面迁移后续以 Web 为基准。
- 边界、定位、形状、圆角、背景出现条件与颜色、对齐、悬停 / 按下 / 焦点 / 禁用反馈与读屏名称按规范与平台官方写法落实；不用经验常数、按型号拟合或自绘本应原生的控件。
- **不包含**：筛选控件内部（计数样式、菜单结构）与星级控件内部的未定规范（紧凑星簇、拖动、弹窗、触感、字号定稿），液态玻璃高光 / 折射 / 融合等依赖原生的光学装饰，Windows 原生界面迁移。两类自定义控件的**外轮廓、位置与悬停区形状**仍在范围内。

## 2. 作者裁定与本阶段规范改动

| 项 | 裁定 | 落点 |
|---|---|---|
| 外观对标链 | 如上；参照只决定形状、材质层次与状态出现方式，几何与颜色仍取规范与令牌 | `Design/README.md` 总则、§3 |
| Web 定位 | 几何与行为仍只依据 `Design/`；宽形态主屏按钮外观以 macOS 原生壳为参照，允许读取 Mac 实现与 Mac 截图取形状 | `Design/README.md`、`PhotoViewer.Web/AGENTS.md` |
| 筛选控件形态 | 移动端（iPadOS、Android）宽形态用「筛选 / 更多」96 合体；桌面（macOS、Windows、Web）宽形态用三段合体 / 单胶囊 | FILTER-01/07/08；共享 `MobileFilterLayout`（由 iOS 本端几何原样迁入） |
| 筛选 / 更多图标 | 新增 `icon.filter`（fi-rr-bars-filter）与 `icon.more`（fi-rr-menu-dots），Android 与 Web 手机形态用 uicons；Apple 两端继续 SF Symbols | `tokens.json` |
| EXIF 衬底 | 横排（上下布局、手机竖屏顶行）衬底块；竖排（左右布局、手机横屏）一律无衬底 | TOOLBAR-05、SHELL-12 |
| 自绘桌面按钮状态 | 命令圆按钮常态只有按钮材质、悬停才出描边；评分槽悬停 / 按下为槽内内切正圆 | TOOLBAR-10 |
| 弹出控件 | 自绘下拉、菜单、tooltip 仍按 Fluent 深色令牌，本阶段不改 | TOOLBAR-10、README §3 |

## 3. 平台整改清单

### C1 Android → iOS

审计结论：几何层已调用共享代码，缺口集中在控件材质、状态与读屏，根源是宽形态控制栏与筛选条各自整块自绘。

1. **控制栏命令改原生**：命令组改为真实 `MaterialButton` 子视图（44dp 圆形、uicons 字形、Material 状态层承担悬停 / 按下 / 焦点 / 禁用），读屏名取共享 `CommandTooltipText`；移动端不加 `TooltipText`（长按会出浮层，与 TOOLBAR-04「不做长按浮层」冲突）。EXIF 与星级仍为自绘定制组件。命令在行 / 列交叉轴居中（TOOLBAR-02）；评分胶囊矩形取 `RatingControlGeometry.ControlFrame`（列布局不再被拉成 52 宽）；禁用只降内容 alpha。
2. **筛选入口改原生并对齐 iPad**：宽形态改为 `MobileFilterLayout` 的「筛选 / 更多」96 合体 + AI 开关（行布局整组自然宽居中、列布局与卡片同末端）；合体、手机竖屏圆按钮、横屏 88 合体统一为原生胶囊容器内的 Material 图标按钮分段（与上下张、缩放增强合体同一组件），删除手绘分隔线、三点与胶囊底，修正刘海在右时分隔线落到胶囊外缘的镜像错误；图标取 `icon.filter` / `icon.more`；补读屏名。菜单内容沿用既有业务。
3. **手机按钮外观统一**：顶行、底行的圆按钮与合体按钮同一套 Material 规格与状态层，不再混用自绘描边、无描边与手调颜色。
4. **星级控件悬停**：逐格悬停 / 按下为槽内正圆（原生 `RippleDrawable` 或 Material 状态层透明度），胶囊外轮廓不变；补读屏名与增减动作。
5. **EXIF 块**：标签 / 值按字体度量成对居中（同 iOS），两处底色统一取 `color.toolbar.block.background`；修正仍写 44 的注释与手册。
6. **其余**：卡片读屏名与 `PerformClick`；相似面板动作改 `MaterialButton`；分析瓦片颜色 / 圆角取令牌；滚动宿主改原生 `HorizontalScrollView` / `ScrollView`（保留溢出才可滚、偏移不重置的既有断言）；星形统一 uicons 字形。

### C2 macOS → iPadOS 宽形态

审计结论：分区、锚点与尺寸已与 iPad 逐项一致，剩余为状态刷新方式与少量手调常量。

1. **控制栏就地刷新**：切图、评分写回、EXIF 到达不再整栏销毁重建；按内容摘要只在显隐 / 顺序 / 绑定变化时重建，EXIF 项数不变只换文本、评分只刷显示值、同名命令按钮保留实例（同 iPad），保住悬停、按下与键盘焦点。
2. **令牌化**：卡片文字色取 `color.film.card.name`；玻璃圆角取 `shell.glass.button.size / 2`；与令牌等值的手写颜色改取令牌。
3. **登记能力边界**：列布局胶片栏从筛选下方滚过没有公开的系统滚动边缘效果（FILM-07@mac）；macOS 26 玻璃无按压交互（SHELL-12@mac）。均不自绘模拟。
4. **验收补齐**：静态矩阵补「控制栏开 + EXIF 关 + 分析栏关」（工具区透明、无分隔线）；AI 版复跑相似面板开合。

### C3 Web → macOS

1. **默认档宽形态改为 Mac 形态**：筛选为三段合体 / 单胶囊（同一玻璃胶囊内分段，计数在控件内），AI 开关同胶囊材质；评分恒展开 224；竖排 EXIF 无衬底 52、横排衬底块；命令圆钮常态无描边、悬停出描边；评分槽悬停 / 按下为内切正圆；禁用只降内容 0.42；分析细节瓦片悬停描边；不再有 `aria-pressed` 强调底。手机形态保留 iOS 口径，筛选 / 更多改用 uicons 图标。
2. **mac 配置档收窄**：只保留浏览器没有的窗口装饰几何（列布局 42 让位、标题栏让位、拖动区导出），其余与默认档一致。
3. **检查与测试**：按新目标改写 FILTER-01/06/07/08、TOOLBAR-03/05、SHELL-01 等断言与 Node 基线；补「默认档与 mac 档只在窗口装饰几何上不同」的不变式检查。
4. **手册**：`PhotoViewer.Web/AGENTS.md` 改写唯一依据条款与目录说明。

### C4 收尾

- `Design/platforms.md` 按实际实现改写 @android / @mac / @web 条目，`Design/status.md` 回填本次实际覆盖。
- 双版本构建与回归：Android 两版构建 + 手机 / 平板代表矩阵；Mac 两版构建 + layout / toolbar / filmstrip / analysis / similarity；Web Node 与 `?check=all`；共享改动后 iOS 双版模拟器构建与 form / layout 回归、`CoreContractCheck`、Windows 类型检查。
- 截图矩阵交作者目视复核；作者签字前不标「作者手验通过」。

## 4. 验收

- 每个平台的整改项在对应原生检查中有断言或截图证据；模拟器与真机分开标注。
- 不删除既有失败断言制造通过；旧前提失效时保留等价覆盖并写明替换理由。
- 证据保存在 `artifacts/acceptance/new-ui/compliance/`，不入库。

## 5. 待作者裁定（不阻塞本阶段）

- 星级字形：TOOLBAR-10 写 16 / 12（Mac 现行），RATING-07 写 `rating.control.glyph.*` 20 / 15（iOS、Web 现行），随星级控件专项一并定稿。
- Android 触控目标：冻结 44dp 与 Material 建议 48dp 的取舍（沿用 Plan 3 遗留项）。

## 6. 实施与验收结果

### 6.1 实现

- **共享**：iPad 两段合体几何迁入 `PhotoViewer.Shared/MobileFilterLayout.cs`，iOS 与 Android 共用；`CoreContractCheck` 新增其契约，并把 `cae08ac` 遗留的横屏无衬底 EXIF 旧断言（44 宽）改为固有项宽 52、上限 64、与按钮同轴，EXIF 透明容器允许伸进分析栏接缝但不压瓦片。
- **Android**：控制栏改为 `FrameLayout`，命令为 Material 圆按钮；筛选入口、手机圆按钮与全部合体统一为 Material 按钮 / 胶囊容器（修正刘海在右时分隔线落到胶囊外缘），宽形态改用「筛选 / 更多」96 合体；主屏按钮统一为主题样式 `Widget.PhotoViewer.GlassButton`（Material 3 图标按钮，uicons 字形放在图标槽），按下 / 悬停 / 焦点与星级逐格共用主文字色的 Material 3 状态层 `pv_glass_state_layer`；星级胶囊与按钮同底，逐格悬停 / 按下为原生涟漪内切正圆，补读屏范围与增减；系统安全区与屏幕圆角按 SHELL-10 分开避让（先按安全区排出贴角元素，再按元素实际位置与半圆端外形求圆角避让线，两者取较大者）；EXIF 成对居中、衬底取令牌；星形统一 uicons；卡片读屏名、相似动作 Material 按钮、分析瓦片令牌化。滚动宿主保留自写实现（换原生滚动视图需重写两向固有测量与偏移迁移，既有断言全部保留）。
- **macOS**：控制栏按结构摘要就地刷新，切图、评分写回、EXIF 到达保留控件实例；卡片文字色、玻璃圆角、分隔线、导航图、分析与相似面板颜色改取等值令牌；静态矩阵补「只剩评分与命令」组合。
- **Web**：宽形态改为 Mac 形态（三段玻璃胶囊 / 单胶囊、段宽取各段最宽选项、评分恒 224、竖排 EXIF 无衬底 52、命令悬停出描边、评分槽内切圆悬停、禁用只降内容、细节瓦片悬停描边、卡片提示收窄到文字）；手机形态改用 `icon.filter` / `icon.more`，横屏 EXIF 52 同轴；`?platform=mac` 只保留窗口装饰几何差异。

### 6.2 程序化结果

| 检查 | 结果 |
|---|---|
| 共享契约 | standard 1979 PASS / 1 SKIP / 0 FAIL；AI 1995 PASS / 1 SKIP / 0 FAIL |
| Windows 类型检查 | 通过 |
| iOS | 双版模拟器构建 0 错误；运行时套件未复跑（共享改动为纯迁移，契约覆盖） |
| Web | Node 352 PASS / 0 FAIL；`?check=all` 双版各 70/70；`?check=mac` 2/2；mac 配置档与 Mac 原生几何共有 213 节点，差异只在筛选段宽（导出工具近似测字）与段坐标空间 |
| macOS | 双版 Debug / Release 0 错误；layout、toolbar、filmstrip、analysis、input 双版通过，AI similarity 通过；tools 套件因会话锁屏无法截图未在最终树复跑 |
| Android | 双版构建 0 错误；圆角避让与按钮样式收尾后的最终树在手机与平板等效窗口复跑 layout / form / input / filter / main 及平板 analysis、AI similarity / edition 全部 0 FAIL（字形居中改为读按钮实际绘制像素）；平板 AI settings 2 条拖动断言失败，HEAD 基线同值失败，非本轮引入 |

证据在 `artifacts/acceptance/new-ui/compliance/`（`android/`、`macos-standard/`、`macos-ai/`、`web/`）。

### 6.3 待作者复核

- 三端截图观感、Android 真机触控与外接鼠标悬停（模拟器只能注入 hover）；Android 真机圆角屏上竖屏底行与横屏刘海侧列上下的实际边距、按下状态层观感（模拟器圆角约 18dp，小于按钮半圆端 22，圆角避让在模拟器上不产生让位，公式由合成半径检查覆盖）。
- Android 栏底毛玻璃暂缓：`SurfaceView` 主图没有视图层的公开背衬模糊，唯一可行路线是在自有 GL 渲染器内对栏位区域做降采样模糊，需另行裁定 SHELL-12@android。
- Mac 卡片文字色由纯白改为令牌 `color.film.card.name`（#EEEEEE）。
- Android 相似聚类开关未选中文字取 `color.text.primary`（与其他 Material 按钮一致），已登记为平台差异。
- 无令牌的遗留字面量：Mac 计算中药丸底色、栏位默认底，Android 分析瓦片 letterbox 底色；Web 三段胶囊段内边距与分隔线缩进（`web-shell-gaps.md` G55）。
