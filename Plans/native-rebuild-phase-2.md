# Phase 2 执行清单 — macOS 首个原生壳 + 色彩管线

> 2026-10-13 立项,隶属 [native-rebuild-master-plan-1.md](native-rebuild-master-plan-1.md) §6 Phase 2 + [color-managed-native-rendering-plan-1.md](color-managed-native-rendering-plan-1.md)(色彩专章,§0/§1/§3 继续有效)。前置:Phase 0 验收门全关([native-rebuild-phase-0.md](native-rebuild-phase-0.md))、Phase 1 验收门全关([native-rebuild-phase-1.md](native-rebuild-phase-1.md) §1,2026-10-13 iPad mini 7 实证)。
>
> **首壳平台裁定:macOS**。依据:① 迭代最快(无设备部署/签名摩擦,截图可程序化对拍);② 色彩实证地(OUT-JPG 过饱和案发平台,验证门主场);③ master plan 定 Mac 版为交互/视觉设计基准,基准先行可避免他平台跟随返工;④ iPad 痛点已被 Phase 1 实证根治(500 连翻恒 Normal),iOS 排 Phase 3 时 Metal 渲染器直接复用。
>
> **铁律**(沿用 Phase 0/1):
> 1. 每里程碑:构建零错误、无新增警告 → 审查 → 按作者授权提交。
> 2. 画质红线:fit 显示与 100% 放大不接受视觉可见降分辨率;100% 及以上永远全分辨率(沿用 Phase 1 固化测试)。
> 3. 函数 XML 中文注释;不改 UI 字符串(原生壳新写除外);计划外发现记入 §5。
> 4. 本阶段**允许 UI 形态变化**(2026-10-12 决策:Avalonia UI 仅为产品参考;macOS HIG 为基准),但功能项对齐现有版本,**人工验收自本阶段开始**。
> 5. mono interp 教训(Phase 1-M5):新写 P/Invoke 一律显式指针化(IntPtr/unsafe),禁用 ref/out 封送。

---

## 0. 产品决策点

**2026-10-14 作者确认并授权直接实施 P2**：决策点 2 的功能边界按推荐顺延；原生 UI 沿用现有分区、按钮功能及选中/高亮含义，设置保留现有分类与选项，控件风格不要求像素级一致。具体方向见 [native-rebuild-phase-2-ui.md](native-rebuild-phase-2-ui.md)，作者最后验收实际产品。

**实施状态**：M1–M4 已落地，M5 程序化核验与文档已执行，人工色彩/手感与真实素材性能门保持待验。Debug 默认 AppKit，Release 默认旧壳。验证和启动说明见 [P2 验收清单](native-rebuild-phase-2-acceptance.md)。

**作者追加约束**：主窗口继续采用隐藏式标题栏；原本方形的缩略图区、控制按钮等保持方形；通用控件在保留布局位置与大体形状的前提下原生化。Apple 图标迁移建议采用 SF Symbols 的语义映射，详见 UI 方案 §1.4–1.5，示意图不能代替最终原生符号验收。

**设置页方向已由作者确定**：参考现有 iOS 原生设置的分类导航、分组表单与专门编辑方式，直接采用 macOS 原生设置风格，功能一致即可，不追求 Avalonia 视觉一致。具体以 UI 方案 §4 为准，之前网页里的设置布局不作为实现规格；主窗口隐藏标题栏与方形元素在原生实现中落实。

| # | 决策点 | 推荐 | 影响 | 理由 |
|---|---|---|---|---|
| 1 | 首壳平台 = macOS(master plan 授权"Phase 1 收尾时定") | **macOS** | 你验收的设备 = 本机 Mac | 见文首裁定依据 |
| 2 | Phase 2 功能边界:**纳入**主窗口骨架 / Metal 主图(缩放平移手势) / 胶片条(评分星+抖动徽标) / 筛选条 / 菜单+快捷键 / 设置页 / 全屏 / 评分写回 / 外部打开;**顺延 Phase 3** 分析栏(细节预览/直方图/4 诊断瓦片)、相似聚类面板、工具页(EXIF 详情/统计/DINO 诊断)、增强预览 | **顺延（作者已确认）** | 首壳暂缺分析功能(评分/选片核心环完整) | 聚焦"看图核心环"先把色彩与壳做透;分析栏诊断瓦片 Phase 3 与 iOS 共享 Metal/原生图片视图成果更省;增强预览需 float 像素内核适配(plan-1 §3.4),与分析栏同批合理 |
| 3 | Mac dmg 发布通道切换时点 | 验收通过前 Avalonia 版照常发布;通过后 dmg 默认原生壳,旧壳经启动开关保留至 Phase 6 | 过渡期内 dmg 内容不变 | master plan"新架构未达标前不切换发布通道";门面回退机制保底 |
| 4 | 设置页 Mac 原生形态 | **macOS 原生设置风格（作者已确定）**：参考已有 iOS 原生设置的分组与交互，分类导航 + 分组表单，功能一致 | UI 观感 | 作者明确不追求 Avalonia 视觉一致；实现后真实原生界面验收 |

## 1. 验收门(Phase 2 出口标准)

- [ ] **色彩**(继承 plan-1 §6-Phase1 门):三色域测试图(sRGB / Display P3 / Adobe RGB 标记的同画面色卡)与系统"预览"app 逐屏一致(截图对拍);OUT-JPG 茶园集过饱和消失且饱和绿细节保留;灰阶中性(观片背景与画面灰无色偏);拖到 sRGB 外接屏色彩收敛无需重载(人工)。
- [ ] **性能/内存**:切图 P0 延迟、胶片条滚动帧率、内存峰值不劣于 Phase 1 后版本(对照 phase-1 §5 基线);fit 上屏(代理)目标 < 100ms。
- [ ] **回归**：程序化已验证真实窗口打开/翻图、已有 XMP 原位评级、独立 XMP 字节服务、指纹三表索引与设置落盘；Finder/Dock 事件及交互手测待作者。仓库没有自动 RAW sidecar 创建/关联，不将其误记为本期回归功能。
- [ ] **画质**:100% 放大永远全分辨率(硬约束);fit 与 100% 目检无回归(高细节风景/织物纹理/文字边缘图集)。
- [ ] **人工验收**(本阶段起):原生壳整体手感 + 顺延功能缺席形态确认(决策点 2/4)。
- [x] **工程**:四平台构建零错误零新增警告;Core 零新增 UI 耦合;AGENTS.md(根 + 新增/变动模块)同步。Mac NU1903、Android 14 条既有基线据实保留。

## 2. 基线与测试资产(开工先做,约半天)

- OUT-JPG 过饱和在旧壳复现截图存档(对照基线);灰阶图同拍。
- 测试图集制作:Python PIL(ImageCms 嵌 ICC)生成同画面色卡 ×3 profile(sRGB/P3/Adobe RGB)+ 灰阶 ramp + 饱和色块集;画质图集沿用 §8 全局口径;42MP HEIC/ARW 回归集复用 TestCase + Phase 1 压测集生成脚本。
- 性能/内存基线**引用 phase-1 §5 数据**,不重测。
- 探针先行项(结果直接决定 M1/M2 内部选型,开工第一天做):
  1. **SkiaSharp 3.119 色彩转换路径**:已实证 `SKColorSpaceXform` 被移除(SkiaSharp 3.x 破坏性变更);探 `SKPixmap.ReadPixels` 目标色空间转换 + `Float16` 色型是否可用/正确;不可行则 ColorEngine 转 lcms2(plan-1 §1 备选,P/Invoke 显式指针化)。
  2. **.NET macOS Metal 绑定冒烟**:`MTLDevice.CreateSystemDefaultDevice` / `CAMetalLayer` / `ReplaceRegion` 最小三角渲染——Microsoft.macOS 26.2 pack 在机,绑定类型存在性已实证(2026-10-13),渲染正确性待探。

## 3. 里程碑

### P2-M1 — ColorEngine + 色彩契约 + Mac 解码归一化(共享层,旧壳零行为变化)
- 新增 `PhotoViewer/Core/Color/`:`ColorEngine`(ToScRgb16 / ToSrgb8;常见 profile 常驻缓存 sRGB/P3/AdobeRGB;无内嵌 profile 的 JPEG 按 EXIF ColorSpace 标签推断、均无按 sRGB——推断规则集中于此)。
- `DecodedImage` 契约扩展(plan-1 §2):`PixelEncoding`(UInt8Bgra 直通 / UInt8 sRGB / UInt8 P3 / Float16 scRGB)+ Stride + 色彩空间标签;现有 8bit BGRA 路径映射为 UInt8Bgra,**旧壳适配层零改动**(色彩仍锁死,属预期内有限收益,同 Phase 1 口径)。
- Mac 解码归一化:JPEG 解码切 ImageIO(与 HEIF/RAW 同引擎、忠实 ICC、复用 M4 子采样路径);解码产物携带显式色彩空间;主图线 16float scRGB、缩略图线 8bit 不变(plan-1 §2 两条产物线)。
- 16float 内存账本：复用按字节分桶的 ImageBufferPool；LRU 记实际池化字节，准入按 8B/像素估算，GPU 客户端单独记账。冷图使用 F16 代理档/淘汰，避免 sRGB8 唯一中间态提前裁剪广色域。
- 验证:profile 转换数值对拍(sRGB↔P3↔AdobeRGB 色卡逐像素 ΔE 报告);四平台构建;gov 自测全过。

### P2-M2 — Metal 照片层独立验证(渲染探针,先行 de-risk,plan-1 §7 缓解项)
- Mac 头工程内独立 `MetalPhotoView`(NSView + CAMetalLayer `rgba16Float` + **ExtendedLinearSrgb**):纹理四边形 + 三线性 mipmap + **顶点变换**(缩放/平移/八种 EXIF 方向) + `ReplaceRegion` pinned 纹理上传。标签必须与线性数据匹配。
- **缩小滤波画质红线**(2026-10-14 旧壳修复带出):代理图已按屏幕物理像素 2× 超采样解码,旧壳上屏仍差最后一级(Avalonia HighQuality = 单遍 Mitchell 无 mipmap);Metal 层须实现 **trilinear/mipmap 链(或等价各向异性缩小滤波)**,不许退化为纯双线性直采——fit 浏览"无可见降质"在此收口。
- 启动开关进独立 demo 渲染测试图集,对拍系统预览(截图程序化对比 + 人工目检);色彩标签实证(打标前后对照)。
- 原生手势雏形:NSMagnify/NSPan/双击/惯性,原生层闭环(plan-1 §3.3)。
- 验证:三色域一致;纹理上传帧时(42MP F16);拖屏收敛(外接 sRGB 屏,人工可后补)。

### P2-M3 — Mac 原生主窗口接管(壳)
- 原生/旧壳启动入口显式分流；原生运行独立 NSApplication，不创建 Avalonia Application/主窗口，错误明确报告。迁移期旧壳由 `--legacy-ui` 进入。
- AppKit 骨架:NSWindow + 菜单(NSMenu + AppGesture 单向映射)+ 主图区 = M2 `MetalPhotoView`。
- **VM 契约切分**：共享 PhotoLoadSession 管理代际/代理→Full/展示租约，MetalPhotoView 管理变换并上报物理视口；原生模式 ImageViewModel 不订阅旧加载/创建 WriteableBitmap，旧壳仍维持原有状态机。
- 胶片条:`NSCollectionView`(虚拟化)接 `ThumbnailListViewModel`(FilteredFiles + 统一 LRU 缩略图查询视图,已平台中立);评分星/抖动徽标原生绘制;筛选条。
- 评分写回 / 外部打开(`MacExternalOpenBridge` 改投原生窗口)/ 文件夹选择(NSOpenPanel)全链路接通。
- 验证:程序化回归(评分写回回环/外部打开/指纹 DB)+ Mac 冒烟 + 四平台构建。

### P2-M4 — Mac 原生设置页 + 收尾加固
- `MacNativeSettingsPresenter`(AppKit 原生分类导航 + 分组表单/专门编辑区，按 macOS 系统设置风格组织；功能对齐现有 iOS 原生设置，不沿用网页里的 Avalonia 式设置布局；快照-工作副本-关闭回写模板,iOS 已有蓝本)。
- 全屏/布局切换/窗口状态持久化;顺延功能的隐藏或占位形态落地(决策点 2)。
- 验证:设置读写回环(9 个 partial 全覆盖)+ 冒烟。

### P2-M5 — 验收门执行 + 文档收口
- §1 全门执行(色彩矩阵/拖屏/性能内存对照/回归清单);人工验收组织(截图包 + 操作清单给作者);AGENTS.md(根 + Core/Color 新模块 + Mac 头工程 + 受影响模块)同步;提交。

## 4. 提交纪律

作者已授权先提交 P2 实现与胶片条修复，产品验收继续进行；提交不代表人工验收或性能门关闭。不操作 Training 独立仓的用户改动。

## 5. 基线数据与发现清单(执行中填写)

| 日期 | 发现 | 处置 |
|---|---|---|
| 2026-10-13 | `SKColorSpaceXform` 已在 SkiaSharp 3.x 移除(3.119.4 实证无此类型);`SKPixmap.ReadPixels` 目标色空间转换 + Float16 为首选替代,lcms2 备选 | M1 开工第一天探针定论,记入本表 |
| 2026-10-13 | Microsoft.macOS 26.2 pack 在机;AppKit/Metal/QuartzCore 绑定类型存在性实证(CAMetalLayer/NSCollectionView/NSGestureRecognizer/IMTLDevice) | M2 渲染冒烟探针复核正确性 |
| 2026-10-13 | Phase 1-M5 教训:mono 解释器 ref/out P/Invoke 封送间歇崩溃;本阶段新 P/Invoke 一律显式指针化(铁律 5) | 全阶段适用 |
| 2026-10-13 | plan-1 §3.4 派生功能 float 化(直方图/分析/增强 CLHE)随功能顺延 Phase 3,本阶段不做 | 决策点 2 |
| 2026-10-14 | 旧壳修复:P1-M4b 代理 overscan 1.5→2.0(作者实证 1.5× 下"解码降采样+上屏降采样"双重 Mitchell 有可见软化;2× 超采样后上屏重采样近乎无损);双击/键盘放大不触发 Full 升级(根因:`ZoomTo`/`FocusOnImagePoint` 先改 Scale 后落 Fit=false,Scale 订阅见到 Fit=true 直接 return;改为同时监听 Scale+Fit) | 已修(共享层,原生壳直接继承);上屏最后一级滤波纳入 P2-M2 |
| 2026-10-14 | 开工审阅发现待实证项：线性 scRGB 与 Apple 非线性 extended sRGB 标签不应混用；sRGB 代理及唯一 sRGB 8bit 冷缓存中间态会提前裁剪广色域 | 见 UI 方案 §6；M1/M2 探针后收口编码/标签及代理/冷缓存产物，不按旧文字直接落地 |
| 2026-10-14 | skcms Float16 探针通过，最大线性分量误差 0.000488；P3/Adobe 红保留负值/超一；ImageIO 对无 ICC JPEG 自动补 ProfileName=sRGB | 使用 SKPixmap.ReadPixels；JPEG 实际扫描 APP2 判断内嵌 ICC，再用 EXIF 推断；原生代理与 Full 均为 F16 |
| 2026-10-14 | 真实 AppKit/Metal 色卡与 RAW 副本冒烟通过；八种 GPU 方向、100% Full、五类设置落盘、评级/CV/DINO/指纹索引三表回环 | 独立设置/数据库与自有图片，报告在 release/native-p2-acceptance；系统色彩对拍和真实素材性能仍待验 |
| 2026-10-14 | 文档曾称 RAW 自动 sidecar，但实现只有已有 Rating 原位更新 | 保留现有行为，根/设置模块/验收文档纠正；新 sidecar 能力单独立项 |
| 2026-10-14 | 合并到已取消的代理任务被误判为失败，P0 随后全解码；并发大小代理可能覆盖与丢租约 | 取消单独传播，仍有效调用重发代理；缓存原子替换且尺寸单调。修后 RAW 实测保持 Proxy，但冷图仍约 1.9s，性能门未通过 |
| 2026-10-14 | 作者五张实拍暴露胶片条缺陷：文档尺寸被视口截断、流式换行错序、当前项定位/横向滚动失败 | 独立单轴布局按项目数扩展；保留 AppKit FlowLayout 滚动方向契约，滚动位置不随控制刷新复位。五组 HIF/ARW 与 12 张色卡验证身份/几何/升降序/末项/鼠标横滚通过，原照片只读 |

## 6. 风险与缓解(本阶段增量;master plan §7 与 plan-1 §7 继续有效)

| 风险 | 等级 | 缓解 |
|---|---|---|
| .NET macOS Metal 绑定在真实渲染器下不成熟(纹理上传/帧调度) | 中 | M2 独立探针先行定罪;不可行时回退 OpenGL(NSOpenGLView,色彩标签同样可控)或 CAOpenGLLayer,渲染层抽象保住 VM/色彩管线全部投资 |
| SkiaSharp 3 色彩 API 缺口(F16 转换不可用) | 中 | M1 探针;lcms2 备选(plan-1 §1);色彩引擎接口与实现解耦 |
| 16float 内存膨胀(LRU 策略失效) | 低 | 统一 LRU 的 F16 代理/Full 档、实际池化字节准入复核与 Governor；GPU 独立记账，大图峰值/连续翻页仍需真实素材验收 |
| NSCollectionView 虚拟化与 ThumbnailListViewModel 对接量超预期 | 中 | 胶片条数据源先简后繁(先全量小集验证,再接虚拟化);交互规格以现有行为为准列清单动工(plan-1 §7) |
| 缩放状态机下沉原生层与 VM 加载编排的接缝不清 | 中 | M3 动工前先出 VM↔渲染器契约表(视口上报/加载产物/升级触发三向),评审后实施 |
