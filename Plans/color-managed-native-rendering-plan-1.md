# 色彩管理根治与原生渲染重构计划 v1

> **状态(2026-10-19)**:本计划已被收编为 [native-rebuild-master-plan-1.md](native-rebuild-master-plan-1.md) 的"色彩专章"。本文的色彩根因调查(§0)、ADR(§1)、色彩管线设计(§3)继续有效;分阶段实施(§6)以 master plan §6 为准。性能、内存、UI 迁移、App Store 上架等新增维度见 master plan。
>
> 2026-10-12 立项。目标:彻底根治色彩管理问题 —— 全平台用满屏幕色域、精确还原任意源色域照片(sRGB / Display P3 / Adobe RGB / RAW 渲染),并以此为契机把 UI 从 Avalonia 迁移到各平台原生,保留 .NET + 共享 C# Core。
> 触发事件：Mac 上浏览 `OUT-JPG`（sRGB 后期输出）出现明显过饱和。
> **明确不做临时修补**（ImageIO DeviceRGB→sRGB 等 P0 纠偏改动）：所有过渡性修补都会被本重构的真子集覆盖，不做重复投入。

---

## 0. 现状与根因（已完成调查，含实证）

### 0.1 根因链

| 层 | 事实 | 证据 |
|---|---|---|
| 输出端 | Avalonia 全平台零色彩管理；macOS 原生层 `CAMetalLayer.colorspace` 为 nil，WindowServer 不做任何转换，像素值原样打到屏幕 | [AvaloniaUI/Avalonia#8450](https://github.com/AvaloniaUI/Avalonia/issues/8450)；本地拆包 `libAvaloniaNative.dylib`（12.0.2）：有 `CAMetalLayer`，**零** ColorSpace/setColorspace 引用 |
| 解码端（JPG） | `new Bitmap(stream)` / `Bitmap.DecodeToWidth` 走 Skia `SKBitmap.Decode`，目标色彩空间为 null，**不应用内嵌 ICC**，输出文件原始编码值 | Avalonia 12.0.2 `ImmutableBitmap.cs` 源码 |
| 解码端（Mac/iOS HEIF/RAW） | `CGBitmapContextCreate` 目标用 `CGColorSpaceCreateDeviceRGB()` —— 实测为"空设备"直通，ColorSync **不做转换** | 本机 Swift 实验：sRGB 纯红画入 DeviceRGB 上下文出来仍是原值；画入 DisplayP3 上下文则正确转换 |
| 环境 | 内建 Liquid Retina 为广色域屏（当前 profile "Apple Built-in Display 6727K 2608"） | `system_profiler` |

### 0.2 为什么只有 OUT-JPG 露馅

ARW 经 ImageIO RAW 引擎渲染输出 Display P3 编码（`sips` 实测），P3 数值原样打到 P3 屏 ≈ 巧合正确；sRGB 的 OUT-JPG 原样打到 P3 屏 = 全面过饱和，饱和绿色（茶园）最明显。**整个 App 在 Mac 上从未正确过，只是被巧合掩盖。**

### 0.3 全平台审计结论

| 链路 | 输入端（ICC→工作空间） | 输出端 | 现状 |
|---|---|---|---|
| Mac JPG 主图/缩略图 (Skia) | 丢 ICC 直通 | 无转换 | sRGB 过饱和；Adobe RGB 偏色 |
| Mac/iOS HEIF/RAW (ImageIO→DeviceRGB) | 丢 ICC 直通 | Mac 无转换 / iOS 假定 sRGB | P3 源在 Mac 巧合正确、在 iOS 偏灰；sRGB HEIC 在 Mac 过饱和 |
| Windows JPG (Skia) | 丢 ICC | DWM 假定 sRGB | sRGB 碰巧正确；Adobe RGB/P3 偏色 |
| Windows HEIF (LibHeifSharp) | libheif 不解 ICC | DWM 假定 sRGB | iPhone P3 HEIC 偏灰 |
| Android HEIF (BitmapFactory) | 未设 `InPreferredColorSpace` | SurfaceFlinger 假定 sRGB | P3 HEIC 偏灰 |

附注：Avalonia UI 自身的颜色在 Mac 上同样过饱和（观片背景灰不中性），同根因。

### 0.4 为什么 Avalonia 无法内部修复

全色域精确显示要求控制输出表面的**像素格式**（8bit 装不下超 sRGB 值）与**色彩空间标签**（OS 才知道如何映射到屏幕）。Avalonia 把两者锁死：整窗一个 8bit 表面、无色彩空间 API、无 extended-range 支持。结论：**照片渲染必须绕开 Avalonia 渲染表面**；而本 App 的 UI 几乎全部为照片服务，胶片条若也要求全色域，Avalonia 手里只剩设置页与面板 —— 保留它的成本超过重写它。

---

## 1. 决策记录（ADR）

| 决策 | 结论 | 否决项与理由 |
|---|---|---|
| 工作空间 | **extended sRGB（scRGB 思路：sRGB primaries + gamma，浮点、允许 >1）** | sRGB 8bit（压掉超 sRGB 色域，不满足"用满屏幕色域"）；按显示器烘焙（多屏拖动/换 profile/截图全错，每次换屏要重转缓存） |
| UI 框架 | **放弃 Avalonia，各平台原生 UI**（macOS AppKit / iOS UIKit / Windows WinUI3 / Android Views），共享 C# Core 保留 | MAUI（桌面端弱、Windows 仍需手写色彩）；Qt（C++ 重写+授权）；Electron/Tauri（扔掉 C# 核心资产）；Flutter/Compose MP（Skia 系同样无色彩管理） |
| 核心语言 | **保留 .NET**。重活（解码/推理/色彩转换/合成）本来就在原生库执行，外层语言对用户可感知性能无影响；C++ 化收益 ≈ 0 而成本是整个 Core + 永久双语言税 | C++/Rust 核心（成像工业标准答案，但小团队的跨语言摩擦成本 > 收益） |
| C++ 库接入纪律 | 新需求先找成熟 .NET 绑定，没有则写 C ABI shim；仅被 profiler 定罪的单个热点内核才下沉 C++。每项天级，不月级 | — |
| Windows RAW | **Sdcb.LibRaw**（活跃维护，自带 win 原生运行时）实现 `IRawDecoder` | WIC RAW（依赖厂商 codec pack，不可靠） |
| 色彩引擎 | **skcms**（经 SkiaSharp `SKColorSpace`/`SKColorSpaceXform`，Avalonia 移除后显式引用 SkiaSharp；支持 half-float 输出、SIMD 级速度） | lcms2（可用但需自行打包四平台原生库，留作备选） |

---

## 2. 目标架构

```
┌─ 各平台原生壳（AppKit / UIKit / WinUI3 / Android Views）──────────┐
│  原生照片表面（Metal / DXGI / GLES：浮点管线 + 正确色彩标签）        │
│  原生列表与面板（NSCollectionView / ItemsView / RecyclerView…）     │
└──────────────┬───────────────────────────────────────────────────┘
               │ 平台中立契约
┌──────────────▼───────────────────────────────────────────────────┐
│  共享 C# Core（全部保留，去 Avalonia 耦合）                          │
│  DecodePipeline（解码→16bit+ICC→skcms→scRGB float 像素缓冲）        │
│  Exif / XmpWriter / Database / AI(DINOv3+CV) / Settings           │
└──────────────────────────────────────────────────────────────────┘
```

**像素契约（Core → 平台壳）**：`DecodedImage { IntPtr Pixels; int Width, Height, Stride; PixelEncoding Encoding /* Float16 scRGB | UInt8 sRGB | UInt8 DisplayP3 */; }`。平台壳只负责把缓冲灌进纹理并正确打标签，不再做任何像素解释。

**两条产物线**：
- **主图线**：16bit float scRGB（全色域、HDR 演进空间）。
- **缩略图线**：8bit 即可，但 Apple/Android 上直接把带源 ICC 的解码结果交原生图片视图（UIImageView/ImageView 天生色彩管理，零转换）；Windows 上缩略图转 sRGB 8bit（WinUI 假定 sRGB）。

---

## 3. 色彩管线设计（核心）

### 3.1 解码归一化（源 → scRGB float16）

| 源 | 路径 | ICC 处理 |
|---|---|---|
| JPEG（Apple） | ImageIO `CGImageSource` → 画入 **named sRGB/P3 上下文或直接读 CGImage** 取其 colorspace → skcms → scRGB | ImageIO 忠实解析内嵌 ICC（实测） |
| JPEG（Windows/Android） | SkiaSharp `SKCodec` 显式指定 dst `SKColorSpace.CreateSrgb()` 得 8bit sRGB 中间态 → skcms → scRGB；**或**直接取内嵌 ICC 一步到 scRGB | Skia 指定 dst colorspace 时应用 ICC |
| HEIF（Apple） | 同上 ImageIO 路径 | 内嵌 ICC / nclx 均被 ImageIO 解析 |
| HEIF（Windows） | LibHeifSharp 解码 + 读取 ICC box/nclx → skcms → scRGB | libheif 不转，需自取 profile |
| RAW（Apple） | ImageIO RAW 引擎渲染（输出 P3 编码）→ skcms → scRGB | 保留现有机型覆盖 |
| RAW（Windows） | **Sdcb.LibRaw**：debayer → 16bit（先用 libraw 内建 sRGB 输出，后续升级相机 ICC 自定义管线）→ skcms → scRGB | 新增能力 |

统一规则：**解码产物必须携带显式色彩空间**；无内嵌 profile 的 JPEG 按 EXIF ColorSpace 标签（sRGB/Adobe RGB）推断，均无则按 sRGB（行业标准惯例）。

### 3.2 输出层（scRGB → 屏幕，逐平台选型）

| 平台 | 表面 | pixelFormat | 色彩标签 | 映射责任人 |
|---|---|---|---|---|
| macOS | `CAMetalLayer`（自有 NSView） | `rgba16Float` | `kCGColorSpaceExtendedSRGB` | WindowServer |
| iOS | `CAMetalLayer`（自有 UIView） | `rgba16Float` | `kCGColorSpaceExtendedSRGB` | 系统合成器 |
| Windows | DXGI swapchain（子 HWND） | `R16G16B16A16_FLOAT` | `DXGI_COLOR_SPACE_RGB_FULL_G10_P709` | Win11 22H2+ ACM；**Win10/无 ACM 回退：着色器内按当前显示器 ICC（WCS API 读取）烘焙** |
| Android | `GLSurfaceView`（NativeControlHost 等价物/原生 View） | RGBA8（+抖动） | `EGL_GL_COLORSPACE_DISPLAY_P3_EXT`（API 26+，运行时探测，失败回退 sRGB） | SurfaceFlinger |

Apple 选 16float extended sRGB 而非 8bit P3：无 banding、装得下 >P3 内容、HDR/EDR 演进零改动。Android SDR 广色域只有 8bit P3 可用，加抖动防带状。Windows 的 scRGB 与 extended sRGB 同构，转换管线完全复用。

### 3.3 渲染器（每平台一个，刻意保持极简）

带纹理四边形：双线性采样 + 顶点变换实现缩放/平移；不实现花哨滤镜。缩放的重采样质量（bicubic/Lanczos）按平台评估二期增强。手势（缩放/平移/惯性/双击）用各平台原生手势识别器，在原生层内闭环。

### 3.4 派生功能适配

- EXIF 旋转/镜像：现有 `ApplyOrientationTransform` 逻辑迁移为顶点变换。
- 直方图/分析栏/DINO 诊断：输入像素现为 scRGB float；统计逻辑不变，展示瓦片转 8bit sRGB 交原生视图。
- 增强预览（CLHE）：像素内核输入变 float16 scRGB —— 亮度/色度分解公式不变，按位宽调整量化级数。
- 位图缓存：16float 全图 ≈ 8B/px（42MP ≈ 330MB），LRU 容量估算重写；策略改为"当前 ± 少量邻居 float 常驻 + 其余保留 8bit sRGB 中间态重转"——转换是毫秒级，重转比重解码便宜两个量级。
- `AnalysisResultCache`/指纹/DB/AI 索引：输入改为 scRGB 后的影响面需逐点过一遍（DINO 输入是 560 短边 sRGB 缩略图，在 scRGB→sRGB 转换后喂模型，与训练分布一致）。

---

## 4. Core 改造清单（共享层，先行）

1. **去 Avalonia 耦合**：`BitmapLoader`/`ThumbnailService`/`ImageEnhancer`/`HistogramRenderer` 等的返回类型从 `Avalonia.Media.Imaging.Bitmap` 改为 `DecodedImage`（像素缓冲 + 色彩空间 + 尺寸）。这是全重构的总开关，一次做完。
2. **新增 `ColorEngine`**（Core/Color/）：skcms 封装 —— `ToScRgb16(src, srcProfile)`、`ToSrgb8(...)`、profile 解析/缓存（sRGB/P3/AdobeRGB 常见 profile 常驻）。
3. **解码器接口扩展**：`IHeifDecoder`/`IRawDecoder`/JPEG 解码统一返回携带色彩空间的中间产物；JPEG 无 profile 推断规则集中在 ColorEngine。
4. **DecodeScheduler / 预取 / 淘汰保护 / 指纹 / DB / EXIF / XMP / 设置存储**：逻辑不变，仅适配新像素类型与内存估算。
5. **新增 Windows RAW**：`PhotoViewer.Desktop` 侧 `LibRawDecoder : IRawDecoder`（Sdcb.LibRaw），与 MacRawDecoder/iOSRawDecoder 对齐。注意确认 win-arm64 运行时包；LibRaw CVE 频发，纳入依赖更新纪律。

---

## 5. UI 迁移清单（逐平台原生）

| 现有 Avalonia 组件 | macOS | iOS | Windows | Android |
|---|---|---|---|---|
| 主图视图（缩放/平移） | NSView+Metal 照片层 | UIView+Metal | swapchain 子 HWND | GLSurfaceView |
| 胶片条（虚拟化列表+评分+徽标） | NSCollectionView | UICollectionView | WinUI ItemsView | RecyclerView |
| 分析栏（直方图/诊断瓦片） | NSStackView + 原生图片视图 | 同左 | WinUI Panel | LinearLayout+ImageView |
| 设置页（9 个 partial） | 系统设置风格表单 | **沿用现有 iOS 原生设置页**（已有！） | WinUI SettingsCard 风格 | PreferenceFragment |
| EXIF 详情/统计/DINO 诊断工具页 | 原生窗口/面板 | 原生页面 | WinUI 窗口 | 原生 Activity/Fragment |
| 菜单/快捷键 | NSMenu + 键盘等效 | UIKeyCommand | WinUI Accelerator | 键事件 |

迁移原则：**一次平台做透再开下一个**；共享 ViewModel 层评估保留 ReactiveUI（VM 与 UI 技术无关）或简化为手工通知，Phase 1 试点时定。

---

## 6. 分阶段实施

> 每个阶段结束都必须：编译零警告 + 真机/真屏色彩验证通过 + AGENTS.md 同步。Avalonia 版在迁移期间保持可发布（分支隔离），新架构未达标前不切换发布通道。

### Phase 1 — Core 平台中立化 + macOS 原生试点（最大、唯一有架构风险的阶段）
1. Core 改造（§4 全部），产出 `DecodedImage` 契约；Avalonia 壳暂时用一个薄适配层把 `DecodedImage` 转 `WriteableBitmap` 保持现有版本可跑（适配层是临时代码，Phase 5 删除）。
2. macOS 原生壳：AppKit 窗口 + Metal 照片层（rgba16Float + extended sRGB）+ 胶片条 + 基础快捷键。VM 复用。
3. **验证门（不过不进 Phase 2）**：
   - 标准测试图（sRGB / Display P3 / Adobe RGB 标记的同画面色卡）与系统"预览"app 逐屏对比一致；灰阶中性（观片背景与画面灰无色偏）；
   - OUT-JPG 茶园集与 Lightroom/预览对拍：过饱和消失且饱和绿细节保留；
   - ARW（P3 渲染）饱和区与转换前主观一致（色域不再被压）；
   - 拖到 sRGB 外接屏：色彩收敛正确、无需任何重载；
   - 性能：切图延迟、胶片条滚动帧率、内存占用不劣于现版。

### Phase 2 — iOS 原生
UIKit 壳 + 复用 Phase 1 的 Metal 渲染器（同 GPU 栈）+ 已有 iOS 原生设置页归位。安全区/相册权限/安全作用域书签迁移。

### Phase 3 — Windows
WinUI3 壳 + DXGI FP16 swapchain（scRGB）+ ACM 探测与 Win10 烘焙回退 + `LibRawDecoder`（补 Windows RAW 能力）+ 现有文件关联/外部打开迁移。

### Phase 4 — Android
Views 壳 + GLSurfaceView（P3 探测回退 sRGB）+ SAF 持久权限迁移。

### Phase 5 — 退役 Avalonia
删除四平台 Avalonia 壳、`PhotoViewer/` 共享 UI 工程、临时适配层；`Directory.Packages.props` 移除 Avalonia 全系包；README/DEV/AGENTS 全面改写。

---

## 7. 风险与缓解

| 风险 | 等级 | 缓解 |
|---|---|---|
| Phase 1 交互层（缩放手势/胶片条联动）重做量超预期 | 中 | 交互规格以现有行为为准逐条列清单再动工；PhotoView 先行独立 demo 验证 Metal 渲染与色彩，再接壳 |
| Apple 平台 .NET 绑定（AppKit）长期投入二等公民 | 中 | Core 保持 UI 无关（本次重构后彻底无耦），未来迁移成本锁在壳内；Swift 互操作持续观察 |
| Win10 无 ACM → 烘焙回退的正确性 | 中 | 烘焙路径着色器内做 3D LUT/matrix+TRC，用色卡测试图验证；Win10 占比逐年下降 |
| Android 设备 P3 EGL 碎片化 | 低 | 运行时探测 + sRGB 回退，行为表格化 |
| 16float 内存膨胀导致缓存策略失效 | 中 | §3.4 混合缓存策略；实机压测 42MP 连翻 |
| LibRaw CVE | 低 | 依赖更新纪律；远期评估 RAW 解码进程隔离 |
| 迁移期双套工程维护 | 中 | 分支隔离 + 冻结 Avalonia 版功能（只修崩溃）直到切换 |

---

## 8. 验证标准（全局）

- **色彩**：四平台 × 三种源色域测试图 × （广色域屏 + sRGB 屏）矩阵，与系统参考渲染（macOS 预览 / Windows 照片 / iOS 照片）对拍一致；条件允许时色度计抽查 ΔE。
- **性能**：切图 P0 延迟、胶片条滚动帧率、批量索引吞吐、内存峰值，均不劣于当前 Avalonia 版。
- **回归**：评分写回（in-place + sidecar）、外部打开、指纹/DB、AI 索引、分析栏、抖动徽标 —— 现有功能清单逐项过。
