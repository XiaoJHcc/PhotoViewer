# 原生重构总计划 v1(master plan)

> 2026-10-19 立项。本计划是重构**总纲**,收编 [color-managed-native-rendering-plan-1.md](color-managed-native-rendering-plan-1.md)(下称 plan-1)为"色彩专章"——其色彩根因调查(§0)、输出层选型(§3.2)、skcms/LibRaw 决策(§1)继续有效,本文不重复;分阶段实施以本文 §6 为准(plan-1 §6 被本文替换)。
>
> **驱动**:plan-1 只解决色彩。本次重构同时根治四个相互纠缠的问题:
> 1. **色彩管理**(plan-1 原案,实证:Mac 上 sRGB 过饱和);
> 2. **浏览性能**(切图延迟、缩放不跟手,根因见 §2.1);
> 3. **内存失控**(iPad 闪退,根因见 §2.2);
> 4. **UI 绘制性能与原生感**(iOS 原生设置页已验证收益;Mac 上要更原生的控件)。
>
> **已确认的三大方向决策**(2026-10-19 与作者对齐):
> - **UI 路线:四平台全原生**(AppKit / UIKit / WinUI3 / Android Views),放弃 Avalonia —— 维持 plan-1 原案;Mac 版是设计基准,其他平台控件行为/观感跟随 macOS HIG 风格。
> - **商店:只上 iOS App Store**;Mac 继续公证直发(dmg,不进 MAS,不强制沙盒),Windows/Android 直发。
> - **落地顺序:Core 先行,平台顺序再定** —— 先完成 Core 平台中立化与新内存/解码架构(在旧 Avalonia 壳内验证收益),再按验证条件选首个原生壳平台。
>
> **质量红线**:不接受视觉可见的降分辨率方案。本文所有性能手段的分级策略在 §4.2 论证其视觉无损性。

---

## 1. 目标与非目标

**目标**:一套 UI 无关的共享 C# Core(解码/色彩/内存/EXIF/DB/AI/设置)+ 四平台薄原生壳;照片渲染走各平台原生 GPU 表面(浮点管线 + 正确色彩标签);统一内存管控;全色域精确显示;iPad 不再因内存闪退;切图/缩放达到系统照片 app 水准。

**非目标**(显式排除,防范围蔓延):
- 不重写 `Training/` 训练仓、不改 DINOv3/CV 算法本身(仅适配新像素契约);
- 不换核心语言(.NET 保留,plan-1 §1 已定);
- 不做 HDR 编辑、不做 RAW 自定义渲染管线(Windows RAW 用 LibRaw 内建输出,plan-1 §1 已定);
- 不做 Mac App Store 沙盒适配(直发公证即可)。

---

## 2. 现状诊断(调查结论摘要,证据索引见附录 A)

### 2.1 性能:主图链路在哪些环节花了不该花的钱

| # | 环节 | 问题 | 证据 |
|---|---|---|---|
| P1 | 主图解码尺寸 | **永远全分辨率**(42MP ≈ 168MB/张),零降采样;缩略图通道(短边 120)与主图通道完全割裂 | `PhotoViewer/Core/Image/BitmapLoader.cs:385`;`MacHeifDecoder.cs:17-18` |
| P2 | EXIF 旋转 | orientation 3/6/8 走 `RenderTargetBitmap` 全尺寸离屏重绘,多一份全尺寸缓冲 | `BitmapLoader.cs:591-698` |
| P3 | 缓存淘汰 | 淘汰循环内**阻塞式双 `GC.Collect` + 同步等 UI 线程 Dispose**,卡在 P0 切图主链路 | `BitmapLoader.cs:244-252` |
| P4 | 缩放上屏 | `Image` + RenderTransform + `HighQuality` 每帧对 42MP 纹理重采样;缩放指示器把全图**再画一遍**;无惯性、无双击动画、滚轮只有预设档位 | `Views/Main/ImageView.axaml:30, 83-87` |
| P5 | P2 预取 | 前 10 后 5 全分辨率邻居(≈2.5GB 需求),超缓存上限后大量被预留闸拒绝、互相挤兑 | `BitmapPrefetcher.cs:184-198` |
| P6 | 分析伴生 | 直方图每次切图对主图整图 `CopyPixels` + 全像素扫描;增强预览全图两遍逐像素 CLHE | `HistogramRenderer.cs:47`;`ImageEnhancer.cs:33-78` |

### 2.2 内存:iPad 闪退机制链

1. **无统一管控**:主缓存 / 列表缩略图(无上限,`ImageFile.ClearThumbnail` 零调用)/ 分析缓存 / 增强位图 / ONNX session 各自为政,`AppMemoryLimitMB` 只用于推导设置默认值,运行期零强制。
2. **告警响应太弱**:仅 iOS 有监听,且只把主缓存砍到**触发时水位的 80%**;Dispose 排在 UI 队列后;`TrimOnMemoryWarning` 是死代码。Android/桌面端零监听。
3. **告警→回填死循环**:trim 与预取无联动,P2 预取立刻把水位顶回 `MaxCacheSize` → 再告警 → jetsam。
4. **瞬时尖峰无准入**:主图/缩略图/索引解码不经字节预留,在途内存互相不可见;EXIF 旋转双缓冲、CV 提取 3 份全分辨率缓冲,尖峰可达 GB 级。
5. **预算依据失真**:iOS "CrashLimitMB" 表取自崩溃日志推算而非实测 jetsam 上限,默认值系统性偏高。

### 2.3 色彩

plan-1 §0 调查结论不变:Avalonia 全平台零色彩管理、解码端丢 ICC、输出端无色彩空间标签,**照片渲染必须绕开 Avalonia 表面**。

### 2.4 UI:迁移工作量与模板

- UI 资产:26 个 .axaml + 30 个 .cs ≈ 7400 行;VM 25 个文件 ≈ 7100 行。
- **原生化模板已验证**:iOS 原生设置页三件套 —— 共享门面 `INativeSettingsPresenter.TryPresent(SettingsViewModel)` + 平台侧 UIKit 手写 VC 栈 + **快照-工作副本-关闭回写**(`iOSNativeSettingsSession`,no-op 存储吞掉编辑期落盘,dismiss 时 `ApplySnapshot` 一次性回写)。证据:`PhotoViewer.iOS/Core/iOSNativeSettingsPresenter.cs`。
- **真正障碍不在 View,在 Core 耦合**:`Avalonia.Media.Imaging.Bitmap` 是像素货币(19 个 Core 文件)、`IStorageFile`/`IStorageFolder` 渗入 Core 与 VM、`Dispatcher.UIThread` 散布全 VM、VM 直接管 `Window`。ReactiveUI 本体跨平台可用,保留。

---

## 3. 决策记录(在 plan-1 ADR 之上的增量)

| 决策 | 结论 | 否决项与理由 |
|---|---|---|
| UI 路线 | **四平台全原生**(确认 plan-1);Mac 版为交互/视觉基准,Win/Android 控件定制跟随 | 半原生(仅 Apple 原生):Windows 广色域长期无解,双框架税更重;仅嵌原生表面:UI 绘制性能与原生感收益不足 |
| 商店 | **只上 iOS App Store**;Mac 公证直发不沙盒 | Mac App Store:沙盒对"就地写 XMP + 自由选目录"伤害大,收益不成比例 |
| 落地顺序 | **Core 先行**:Phase 0 中立化 → Phase 1 内存/解码架构在旧壳内先行落地验证 → 原生壳 | 直接重写首个壳:验证周期太长,旧壳用户(尤其 iPad)痛点无人接 |
| 显示分级 | **代理图 + 全分辨率热替换**(§4.2),fit 模式按屏幕物理像素解码 | "永远全分辨率":168MB/张 的解码与内存开销是切图延迟与闪退的共同根源;"视觉降分辨率":红线,不做 |
| 内存架构 | **MemoryGovernor 统一内存管家**(§4.3):全部缓存登记、压力分级响应、解码准入制 | 各缓存自治加告警监听(现状补丁路线):已证明救不了 jetsam |
| VM 层 | **保留 ReactiveUI**,净化 Avalonia 类型引用(§4.4) | 换手工通知:7100 行 VM 重写,收益为零 |
| 位图释放 | 显式缓冲所有权 + 确定性 Dispose,**禁止**淘汰路径 `GC.Collect` | 现状(BitmapLoader.cs:250-252):P0 链路全局停顿 |

---

## 4. 子系统设计

### 4.1 色彩管线(plan-1 专章,此处只列增量)

解码归一化、skcms 色彩引擎、输出层四表(Metal rgba16Float / DXGI FP16 / GLES P3)全部沿用 plan-1 §3。增量两点:

**按平台的像素产物矩阵**(响应 iOS 内存约束 —— 16float 42MP ≈ 330MB/张在 iPad 不可行):

| 平台 | 全分辨率产物 | 代理图产物 | 说明 |
|---|---|---|---|
| macOS | 16float scRGB | 8bit sRGB | 桌面内存宽松,全量 plan-1 配置 |
| iOS | **按机型/压力二选一**:16float scRGB(高预算机型)或 8bit P3 + 抖动(低预算/压力下) | 8bit sRGB | 由 MemoryGovernor 依据预算实时决策;8bit P3 在 Apple 屏上仍全色域正确,损失仅为超 P3 内容与平滑渐变余量 |
| Windows | 16float scRGB(ACM)/ 8bit 烘焙(Win10 回退,plan-1 §3.2) | 8bit sRGB | — |
| Android | 8bit P3(探测失败回退 sRGB)+ 抖动 | 8bit sRGB | plan-1 已定 Android SDR 只有 8bit P3 |

**派生功能输入口径**:直方图/分析栏/DINO 诊断的输入从"全分辨率位图"改为"代理图 + 必要的全分辨率按需取样"(DINO 本来就只吃 560 短边缩略图,直方图用代理图统计视觉无差)——消灭 §2.1-P6 的整图拷贝。

### 4.2 显示与解码管线(性能核心)

**分级显示策略**(替代"永远全分辨率"):

1. **代理图(Proxy)**:解码目标 = 当前渲染表面物理像素 × overscan(默认 1.5×,供缩放余量)。iPad 11" 横屏 ≈ 2388×1668 × 1.5 ≈ 6MP(≈24MB),相对 42MP/168MB,解码耗时与内存同时降一个量级。ImageIO/libheif/Skia 均支持缩解码,缩解码本身快数倍。
2. **热替换**:缩放级别超过代理图物理像素能支撑的倍率(即屏幕开始放大代理图的像素)时,后台解码全分辨率产物,就绪后在**同一变换下**无缝换纹理;快速翻页时代理图先行(数十 ms 上屏),全分辨率解码挂 latest-wins 取消闸。
3. **视觉无损论证**:fit 模式下屏幕物理像素 ≈ 4MP,42MP 位图经 GPU 重采样到 4MP 与 6MP 代理图重采样到 4MP,信息量相同,原生 GPU 采样质量 ≥ Avalonia `HighQuality`;**100% 及以上放大永远以全分辨率数据呈现**(硬保证,测试用例固化)。这不是"降分辨率方案",而是"按需供给像素" —— Lightroom / 系统照片 app 的标准做法。

**上屏与交互**:

- 原生 GPU 渲染器(plan-1 §3.3 不变):纹理四边形 + 顶点变换;**EXIF 旋转迁移为顶点变换**(消灭 §2.1-P2 的 `RenderTargetBitmap` 全尺寸重绘);缩放/平移/惯性/双击用各平台原生手势识别器在原生层闭环(消灭 §2.1-P4:连续缩放、惯性、双击动画全部原生化);缩放指示器改为渲染器内一次合成(不再第二个 Image 重画全图)。
- 重采样:GPU 双线性为基线,bicubic shader 为缩放停顿后的一帧精修(二期增强,Lanczos 不做)。
- 切图链路保持 plan-1 前的调度思想(P0/P1/P2/P3 + TrackP0 让位),但:淘汰路径禁止 `GC.Collect`(§3 决策);P2 预取内容从"全分辨率邻居"改为"代理图邻居(默认前后窗口)+ 当前 ±1 全分辨率",窗口大小由 MemoryGovernor 按预算下发。

### 4.3 统一内存架构(MemoryGovernor,新组件)

```
MemoryGovernor(Core/Platform 或 Core/Memory,静态门面 + 平台注入)
├── IMemoryBudgetProvider(平台注入):iOS = os_proc_available_memory P/Invoke + 机型表;
│   Android = ActivityManager.MemoryInfo + onTrimMemory 级别;桌面 = 物理内存比例
├── IMemoryClient(全部缓存实现):ReportFootprint() / Trim(PressureLevel)
│   登记对象:主位图缓存、列表缩略图缓存、AnalysisResultCache、DinoFeatureCache、
│   ShakeFlagService、增强位图、ONNX session、DecodedImage 缓冲池
└── 压力级别:Normal / Caution / Warning / Critical
```

**响应矩阵**(告警或周期水位探测触发):

| 级别 | 触发 | 动作 |
|---|---|---|
| Caution | 可用内存 < 预算 40%,或 Android `TRIM_MEMORY_RUNNING_LOW` | 停 P2/P3 预取;缩略图只留可视区 ±2 屏;批量索引暂停 |
| Warning | iOS `DidReceiveMemoryWarning` / Android `UI_HIDDEN` 以上 / 可用 < 25% | 主缓存砍到 当前 + 1;分析缓存/增强位图清空;ONNX session 卸载(可重建);解码并发降到 1 |
| Critical | 可用 < 15% 或连续 Warning | 只留当前纹理;一切后台工作取消并挂起 |

**关键规则**:

1. **恢复渐进式**:压力解除后延迟 ~30s 再逐步恢复预取窗口,打破 §2.2-3 的"告警→回填"循环。
2. **解码准入制**:一切解码(P0 主图、缩略图、预取、索引、CV/DINO)先 `Governor.Acquire(estimatedBytes)` 拿许可,在途内存统一可见;超额排队或降级(如全分辨率请求在 Warning 下降为代理图)。
3. **缩略图纳入 LRU**:`ImageFile.Thumbnail` 移入统一缩略图缓存(字节 + 张数双限),`ClearThumbnail` 死代码随 `ImageFile` 重构消灭;文件夹切换显式整批释放,不依赖 finalizer。
4. **估算复核**:解码完成后按实际字节校正账本(现状只估不复核)。
5. **可观测**:每次压力事件记录快照(各 client footprint、在途解码、最近 N 次切图),写本地日志,供闪退现场回放。

### 4.4 UI 原生迁移(推广 iOS 设置页模板)

**模板三件套**(已被 iOS 设置页验证,§2.4)推广为所有原生页面的标准做法:
1. 共享层门面接口(`INativeXxxPresenter.TryPresent(...)`,不引入平台类型),调用点失败回退旧 Avalonia 页面 —— 迁移期新旧并存的安全网;
2. 平台侧手写原生页面,直接消费共享 VM;
3. 快照-工作副本-关闭回写(编辑型页面)或只读绑定(展示型页面)。

**Core / VM 净化清单**(Phase 0 的主要内容,全在共享层,一次做完):

| 耦合点 | 现状 | 目标 |
|---|---|---|
| 像素货币 | `Avalonia.Media.Imaging.Bitmap`(19 个 Core 文件) | `DecodedImage` 契约(plan-1 §2):像素缓冲 + 色彩空间 + 尺寸;Avalonia 壳用薄适配层转 `WriteableBitmap`(临时,退役阶段删除) |
| 存储抽象 | `IStorageFile/IStorageFolder` 渗入 Core(`ExternalOpenService`/`ImageFile`)与 VM | Core 自有 `IFileRef/IFolderRef`(路径 + 读流 + 安全作用域句柄);平台壳负责从 picker/bookmark/SAF 解析 |
| UI 调度 | `Dispatcher.UIThread` 散布全 VM | `IUiDispatcher` 抽象(Post/Send/CheckAccess),启动注入 |
| 窗口管理 | VM 直接 `new Window`(`MainViewModel`/`ControlViewModel`) | `INativeWindowPresenter` 门面(同设置页模式) |
| 快捷键模型 | `Avalonia.Input.KeyGesture` ↔ `AppGesture` 互转 | `AppGesture` 提升为 Core 自有模型,平台壳单向映射 |
| 笔刷着色 | VM 里 `SolidColorBrush`(`SettingsViewModel.BitmapCache.cs:229-269`) | VM 输出语义枚举,View 决定颜色 |

**控件与风格**:Mac 版为基准(菜单、快捷键、表单、列表全部 AppKit 原生 + HIG);iOS 沿用 UIKit(设置页已有);WinUI3/Android 控件在布局密度、圆角、分组样式上跟随 Mac 版视觉规范。迁移顺序逐页推进,**一次平台做透再开下一个**(plan-1 §5 原则不变)。

### 4.5 iOS App Store 合规(Phase 内嵌,不单独成阶段)

- **沙盒**:iOS 天然沙盒;现有 `StorageAccessManager`(安全作用域书签,iOS 已注册)保留并提升为 Core 文件抽象的一部分;外部打开/文件夹授权路径不变。
- **XMP 写回**:用户经 picker 授权的文件/目录可写,现有 `XmpWriter` 就地写 + sidecar 逻辑不变;批量评分写回需在 UI 上有进度与失败汇总(商店审核对静默失败敏感)。
- **权限用途字符串**:照片库(若支持相册导入)、文件访问;Info.plist 在原生壳阶段补齐。
- **隐私清单 / 签名 / 分发**:Privacy Manifest、App Transport Security、TestFlight 流程,Phase 末冲刺清单化。
- Mac/Windows/Android 直发公证,无沙盒改造。

---

## 5. Core 改造总清单(Phase 0 + Phase 1 的全部内容)

1. `DecodedImage` 契约 + `ColorEngine`(plan-1 §4.1-4.3);
2. `MemoryGovernor` + `IMemoryBudgetProvider` 四平台实现 + 全部缓存登记改造(§4.3);
3. 分级解码:解码器接口加目标尺寸参数,主图链路代理/全分辨率双产物(§4.2);
4. EXIF 旋转从像素重绘改为元数据标记(旋转角随 `DecodedImage` 下发,渲染层消费);
5. 存储/调度/窗口/快捷键抽象(§4.4 表);
6. 淘汰路径去 `GC.Collect`、位图确定性 Dispose、use-after-dispose 竞态修复(命中返回与并发淘汰之间的引用计数或锁);
7. Windows `LibRawDecoder`(plan-1 §4.5);
8. Avalonia 薄适配层(临时):`DecodedImage → WriteableBitmap`,让旧壳全程可跑可发布。

## 6. 分阶段实施

> 每个阶段结束必须:编译零警告 + 真机/真屏验证过门 + 根与各模块 AGENTS.md 同步。旧 Avalonia 版在迁移期间保持可发布(分支隔离),新架构未达标前不切换发布通道。
> 注意 Phase 1 不是 plan-1 反对的"临时修补":其全部产物(MemoryGovernor、分级解码、净化后的 Core)都在共享层,原生壳阶段原样带走。

### Phase 0 — Core 平台中立化(纯重构,零行为变化)
§5 第 1/4/5/8 项。产出:Core 不再引用 Avalonia 程序集;旧壳经适配层运行;四平台功能零回归。
**验证门**:四平台构建 + 全功能人工回归清单(评分写回/外部打开/指纹 DB/AI 索引/分析栏/设置)通过。

### Phase 1 — 内存与解码新架构(旧壳内先行落地)
§5 第 2/3/6 项。产出:MemoryGovernor 全家、代理图分级、去阻塞 GC;**iPad 闪退与切图延迟在此阶段即被根治**(色彩除外 —— Avalonia 输出端锁死,输入端 ICC 解析可先行,输出仍 8bit 直通,属预期内有限收益)。
**验证门**:iPad(A16 级低配机型)42MP 连翻 500 张无闪退、无内存告警死循环;切图 P0 延迟(缓存 miss)较现版下降 ≥50%;fit 显示与 100% 放大画质目检无回归。

### Phase 2 — 首个原生壳(平台在 Phase 1 收尾时定)
候选 macOS(迭代快、色彩实证地)或 iOS(痛点最重、设置页已原生)。内容:原生窗口 + Metal 照片层 + 胶片条 + 设置(复用模板)+ 快捷键。
**验证门**(plan-1 §6-Phase1 验证门原样继承):三色域测试图与系统预览逐屏一致;OUT-JPG 过饱和消失;拖屏收敛;性能/内存不劣于 Phase 1 后版本。

### Phase 3 — 第二个 Apple 平台
复用 Phase 2 的 Metal 渲染器;另一个平台的设置页/胶片条/工具页归位。

### Phase 4 — Windows(WinUI3 + DXGI FP16 + ACM/烘焙回退 + LibRawDecoder)
### Phase 5 — Android(Views + GLSurfaceView P3 探测 + SAF 迁移)

### Phase 6 — 退役 Avalonia + iOS App Store 上架
删四平台 Avalonia 壳、共享 UI 工程、适配层;`Directory.Packages.props` 移除 Avalonia 全系;iOS 合规冲刺(权限字符串、隐私清单、TestFlight、审核材料);README/DEV/AGENTS 全面改写。

## 7. 风险与缓解(增量,plan-1 §7 色彩类风险继续有效)

| 风险 | 等级 | 缓解 |
|---|---|---|
| 代理图/全分辨率热替换的衔接可感知(瞬间模糊) | 中 | 仅在缩放超过代理能力时触发;替换在同一变换下做;交互测试固化"100% 必全分辨率";不达标则调大 overscan |
| MemoryGovernor 分级阈值按机型失真 | 中 | 预算依据实测可用内存(os_proc_available_memory)而非静态表;阈值进设置页高级项可调;压力快照日志支撑迭代 |
| iOS 8bit P3 回退牺牲超 P3 内容 | 低 | 仅低预算机型/压力期启用;设置页给"始终高质量"开关(16float 强制) |
| VM 净化触碰面广(25 VM + 19 Core 文件) | 中 | Phase 0 一次性做完而非渐进;适配层保证全程可编译可发布 |
| iOS 审核对文件访问/XMP 写回的关注 | 低 | 用途字符串写清"照片元数据同步";就地写仅限用户显式授权的目录 |
| 迁移期双套 UI 维护(iOS 设置页已有同步约束先例) | 中 | 门面回退机制让新旧页面可随时切换;冻结旧 UI 新功能 |

## 8. 全局验证标准

- **色彩**:plan-1 §8 矩阵(四平台 × 三源色域 × 广/sRGB 屏,对拍系统参考渲染)。
- **性能**:切图 P0 延迟、胶片条滚动帧率、缩放手势跟手度(帧时间)、批量索引吞吐,均不劣于现版;fit 上屏时间(代理图)目标 < 100ms。
- **内存**:iPad 低配机型 42MP 连翻压测无 jetsam;压力事件快照可回放;各缓存 footprint 可随时 dump。
- **画质**:fit 与 100% 放大目检无回归(测试图集固化:高细节风景、织物纹理、文字边缘)。
- **回归**:评分写回(in-place + sidecar)、外部打开、指纹/DB、AI 索引、分析栏、抖动徽标、设置持久化逐项过。

---

## 附录 A:现状证据索引

- 主图链路:`PhotoViewer/Views/Main/ImageView.axaml:29-42, 83-87`;`PhotoViewer/ViewModels/Main/ImageViewModel.cs:147-213`;`PhotoViewer/Core/Image/BitmapLoader.cs:362-421`(全分辨率解码)、`:591-698`(旋转重绘)、`:200-259`(淘汰 + 阻塞 GC)、`:264-275`(预留闸)
- 调度/预取:`PhotoViewer/Core/Image/DecodeScheduler.cs:68-92, 207`;`PhotoViewer/Core/Image/BitmapPrefetcher.cs:146-158, 184-198`
- 内存:`PhotoViewer.iOS/AppDelegate.cs:54-68`(告警监听);`BitmapLoader.cs:883-956`(trim);`PhotoViewer/Core/Image/ImageFile.cs:425-430`(`ClearThumbnail` 死代码);`PhotoViewer.iOS/Core/iOSPerformanceBudget.cs:40-71`(机型表)
- 色彩:plan-1 §0(含 libAvaloniaNative.dylib 拆包实证与 Swift 实验)
- UI 模板:`PhotoViewer.iOS/Core/iOSNativeSettingsPresenter.cs`(三件套);`PhotoViewer.iOS/TODO-native-settings.md`(8-phase 蓝本);`PhotoViewer/Core/Settings/NativeSettingsPresenter.cs`(门面)
- 耦合盘点:Core 19 文件 using Avalonia(像素货币);VM 13 文件 28 处;`MainViewModel.cs:133-165`(VM 管窗口);`SettingsViewModel.BitmapCache.cs:229-269`(VM 用笔刷)
