# Phase 2 执行清单 — macOS 首个原生壳 + 色彩管线

> 2026-10-13 立项,隶属 [native-rebuild-master-plan-1.md](native-rebuild-master-plan-1.md) §6 Phase 2 + [color-managed-native-rendering-plan-1.md](color-managed-native-rendering-plan-1.md)(色彩专章,§0/§1/§3 继续有效)。前置:Phase 0 验收门全关([native-rebuild-phase-0.md](native-rebuild-phase-0.md))、Phase 1 验收门全关([native-rebuild-phase-1.md](native-rebuild-phase-1.md) §1,2026-10-13 iPad mini 7 实证)。
>
> **首壳平台裁定:macOS**。依据:① 迭代最快(无设备部署/签名摩擦,截图可程序化对拍);② 色彩实证地(OUT-JPG 过饱和案发平台,验证门主场);③ master plan 定 Mac 版为交互/视觉设计基准,基准先行可避免他平台跟随返工;④ iPad 痛点已被 Phase 1 实证根治(500 连翻恒 Normal),iOS 排 Phase 3 时 Metal 渲染器直接复用。
>
> **铁律**(沿用 Phase 0/1):
> 1. 每里程碑:构建零错误、无新增警告 → 审查 → 单独提交。
> 2. 画质红线:fit 显示与 100% 放大不接受视觉可见降分辨率;100% 及以上永远全分辨率(沿用 Phase 1 固化测试)。
> 3. 函数 XML 中文注释;不改 UI 字符串(原生壳新写除外);计划外发现记入 §5。
> 4. 本阶段**允许 UI 形态变化**(2026-10-12 决策:Avalonia UI 仅为产品参考;macOS HIG 为基准),但功能项对齐现有版本,**人工验收自本阶段开始**。
> 5. mono interp 教训(Phase 1-M5):新写 P/Invoke 一律显式指针化(IntPtr/unsafe),禁用 ref/out 封送。

---

## 0. 产品决策点(待作者确认;无异议则按推荐执行)

| # | 决策点 | 推荐 | 影响 | 理由 |
|---|---|---|---|---|
| 1 | 首壳平台 = macOS(master plan 授权"Phase 1 收尾时定") | **macOS** | 你验收的设备 = 本机 Mac | 见文首裁定依据 |
| 2 | Phase 2 功能边界:**纳入**主窗口骨架 / Metal 主图(缩放平移手势) / 胶片条(评分星+抖动徽标) / 筛选条 / 菜单+快捷键 / 设置页 / 全屏 / 评分写回 / 外部打开;**顺延 Phase 3** 分析栏(细节预览/直方图/4 诊断瓦片)、相似聚类面板、工具页(EXIF 详情/统计/DINO 诊断)、增强预览 | **顺延** | 首壳暂缺分析功能(评分/选片核心环完整) | 聚焦"看图核心环"先把色彩与壳做透;分析栏诊断瓦片 Phase 3 与 iOS 共享 Metal/原生图片视图成果更省;增强预览需 float 像素内核适配(plan-1 §3.4),与分析栏同批合理 |
| 3 | Mac dmg 发布通道切换时点 | 验收通过前 Avalonia 版照常发布;通过后 dmg 默认原生壳,旧壳经启动开关保留至 Phase 6 | 过渡期内 dmg 内容不变 | master plan"新架构未达标前不切换发布通道";门面回退机制保底 |
| 4 | 设置页 Mac 原生形态 | 系统设置风格表单(跟随 macOS HIG) | UI 观感 | 原生阶段 UI/手感优化已授权,人工验收把关 |

## 1. 验收门(Phase 2 出口标准)

- [ ] **色彩**(继承 plan-1 §6-Phase1 门):三色域测试图(sRGB / Display P3 / Adobe RGB 标记的同画面色卡)与系统"预览"app 逐屏一致(截图对拍);OUT-JPG 茶园集过饱和消失且饱和绿细节保留;灰阶中性(观片背景与画面灰无色偏);拖到 sRGB 外接屏色彩收敛无需重载(人工)。
- [ ] **性能/内存**:切图 P0 延迟、胶片条滚动帧率、内存峰值不劣于 Phase 1 后版本(对照 phase-1 §5 基线);fit 上屏(代理)目标 < 100ms。
- [ ] **回归**(程序化自验):评分写回(in-place + sidecar)/ 外部打开 / 指纹 DB / AI 索引 / 设置持久化逐项过。
- [ ] **画质**:100% 放大永远全分辨率(硬约束);fit 与 100% 目检无回归(高细节风景/织物纹理/文字边缘图集)。
- [ ] **人工验收**(本阶段起):原生壳整体手感 + 顺延功能缺席形态确认(决策点 2/4)。
- [ ] **工程**:四平台构建零错误零新增警告;Core 零新增 UI 耦合;AGENTS.md(根 + 新增/变动模块)同步。

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
- 16float 内存账本:ImageBufferPool 增 F16 档;LRU 容量按编码字节重估(42MP F16 ≈ 330MB/张,桌面预算);混合缓存策略(plan-1 §3.4:当前±少量邻居 F16 常驻 + 其余 8bit 中间态毫秒级重转)。
- 验证:profile 转换数值对拍(sRGB↔P3↔AdobeRGB 色卡逐像素 ΔE 报告);四平台构建;gov 自测全过。

### P2-M2 — Metal 照片层独立验证(渲染探针,先行 de-risk,plan-1 §7 缓解项)
- Mac 头工程内独立 `MetalPhotoView`(NSView + CAMetalLayer `rgba16Float` + `kCGColorSpaceExtendedSRGB`):纹理四边形 + 双线性采样 + **顶点变换**(缩放/平移/**EXIF 旋转**——消灭 §2.1-P2 的重绘路径) + `ReplaceRegion` pinned 纹理上传。
- 启动开关进独立 demo 渲染测试图集,对拍系统预览(截图程序化对比 + 人工目检);色彩标签实证(打标前后对照)。
- 原生手势雏形:NSMagnify/NSPan/双击/惯性,原生层闭环(plan-1 §3.3)。
- 验证:三色域一致;纹理上传帧时(42MP F16);拖屏收敛(外接 sRGB 屏,人工可后补)。

### P2-M3 — Mac 原生主窗口接管(壳)
- `INativeMainWindowPresenter` 门面(iOS 设置页模板三件套推广):TryPresent 成功则原生窗口接管,失败/开关回退旧 Avalonia 窗口(迁移期安全网,master plan §4.4)。
- AppKit 骨架:NSWindow + 菜单(NSMenu + AppGesture 单向映射)+ 主图区 = M2 `MetalPhotoView`。
- **VM 契约切分**(本里程碑关键设计点):`ImageViewModel` 保留加载编排(generation 闸、proxy→Full 升级、`CurrentIsProxy`),`DecodedImage` 直出原生层渲染;缩放/平移状态机下沉原生层(展示态),原生层视口上报驱动 Full 升级触发(M4b 语义平移);VM 的 Scale/Fit/pan 展示态随之退役。
- 胶片条:`NSCollectionView`(虚拟化)接 `ThumbnailListViewModel`(FilteredFiles + 统一 LRU 缩略图查询视图,已平台中立);评分星/抖动徽标原生绘制;筛选条。
- 评分写回 / 外部打开(`MacExternalOpenBridge` 改投原生窗口)/ 文件夹选择(NSOpenPanel)全链路接通。
- 验证:程序化回归(评分写回回环/外部打开/指纹 DB)+ Mac 冒烟 + 四平台构建。

### P2-M4 — Mac 原生设置页 + 收尾加固
- `MacNativeSettingsPresenter`(AppKit 系统设置风格表单;快照-工作副本-关闭回写模板,iOS 已有蓝本)。
- 全屏/布局切换/窗口状态持久化;顺延功能的隐藏或占位形态落地(决策点 2)。
- 验证:设置读写回环(9 个 partial 全覆盖)+ 冒烟。

### P2-M5 — 验收门执行 + 文档收口
- §1 全门执行(色彩矩阵/拖屏/性能内存对照/回归清单);人工验收组织(截图包 + 操作清单给作者);AGENTS.md(根 + Core/Color 新模块 + Mac 头工程 + 受影响模块)同步;提交。

## 4. 提交纪律

每里程碑一个 commit,信息格式:`P2-M<n>: <摘要>`。`git add -A` 全量暂存;提交信息不用反引号。

## 5. 基线数据与发现清单(执行中填写)

| 日期 | 发现 | 处置 |
|---|---|---|
| 2026-10-13 | `SKColorSpaceXform` 已在 SkiaSharp 3.x 移除(3.119.4 实证无此类型);`SKPixmap.ReadPixels` 目标色空间转换 + Float16 为首选替代,lcms2 备选 | M1 开工第一天探针定论,记入本表 |
| 2026-10-13 | Microsoft.macOS 26.2 pack 在机;AppKit/Metal/QuartzCore 绑定类型存在性实证(CAMetalLayer/NSCollectionView/NSGestureRecognizer/IMTLDevice) | M2 渲染冒烟探针复核正确性 |
| 2026-10-13 | Phase 1-M5 教训:mono 解释器 ref/out P/Invoke 封送间歇崩溃;本阶段新 P/Invoke 一律显式指针化(铁律 5) | 全阶段适用 |
| 2026-10-13 | plan-1 §3.4 派生功能 float 化(直方图/分析/增强 CLHE)随功能顺延 Phase 3,本阶段不做 | 决策点 2 |

## 6. 风险与缓解(本阶段增量;master plan §7 与 plan-1 §7 继续有效)

| 风险 | 等级 | 缓解 |
|---|---|---|
| .NET macOS Metal 绑定在真实渲染器下不成熟(纹理上传/帧调度) | 中 | M2 独立探针先行定罪;不可行时回退 OpenGL(NSOpenGLView,色彩标签同样可控)或 CAOpenGLLayer,渲染层抽象保住 VM/色彩管线全部投资 |
| SkiaSharp 3 色彩 API 缺口(F16 转换不可用) | 中 | M1 探针;lcms2 备选(plan-1 §1);色彩引擎接口与实现解耦 |
| 16float 内存膨胀(LRU 策略失效) | 低 | Mac 桌面预算宽松 + 混合缓存(§3.4 中间态重转毫秒级);ImageBufferPool F16 档纳入 Governor 客户端 |
| NSCollectionView 虚拟化与 ThumbnailListViewModel 对接量超预期 | 中 | 胶片条数据源先简后繁(先全量小集验证,再接虚拟化);交互规格以现有行为为准列清单动工(plan-1 §7) |
| 缩放状态机下沉原生层与 VM 加载编排的接缝不清 | 中 | M3 动工前先出 VM↔渲染器契约表(视口上报/加载产物/升级触发三向),评审后实施 |
