# RAW 开发与调色（Develop & Grading）方案 v1

> 状态(2026-11-01)：立项评估。本文是**现状审计 + 可行性结论 + 分阶段方案**，尚未实施。审计证据来自代码、本机 `CIRAWFilter.h` 头文件与运行时实测（macOS 26.6.2 / Xcode 27.1 SDK）、以及对外部资料的核实（来源见 §8）。

## 0 结论摘要

1. **色彩管理链路是对的，RAW 渲染链路不是我们控制的。** scRGB F16 / Display P3 / ICC / skcms 这套显示层已经正确；但"RAW 长什么样"由三端三套引擎的默认参数决定，我们只喂了方向与缩放。
2. **镜头校正：Apple 默认开且实测生效，Windows 与 Android 完全没有。** Apple 只有一个 Bool（无强度、无 CA/暗角分项），是否真的校正取决于文件内嵌畸变元数据，覆盖不可查、无法验证正确性。
3. **三端不是同一张照片**：16F vs 8 位、相机白平衡 vs codec 默认、Apple 自带锐化/降噪、Windows/Android 无高光恢复。目前没有任何跨端一致性验证。
4. **可以做 ACR 风格的调色与预览，推荐"两级管线"**：`Develop`（统一线性基底）+ `Grade`（共享 GPU 调色）。Light/Color 必须在线性域才有 RAW 语义；Effects/HSL 本来就是显示域操作。
5. **最贵的一步是 Windows**：现在只有 8 位 WIC 输出、且没调用 `IWICDevelopRaw`，无法参与统一管线。
6. **镜头校正需要自带 profile 数据**：文件内嵌 opcode（最准、零许可成本）、lensfun（最全、LGPL-3 + CC BY-SA）、Adobe LCP（私有、已停止分发）三条路组合。

## 1 现状审计

### 1.1 三端 RAW 链路

| | macOS / iOS | Windows | Android |
|---|---|---|---|
| 引擎 | `CIRawFilter`（`ApplePresentationDecoder`） | WIC RAW codec（`WindowsRawDecoder`） | LibRaw 0.22 + 薄 C ABI（`AndroidRawDecoder`） |
| 位深 | 16F 扩展线性 sRGB | 8 位 BGRA | 8 位 sRGB / P3 |
| 白平衡 | 引擎默认（可调 `neutralTemperature/Tint`） | codec 默认 as-shot | `use_camera_wb=1` |
| 曝光 | `exposure=0`，引擎 `baselineExposure` 生效 | codec 默认 | 自限 auto-bright（最大 ≈1.3 EV 提升） |
| 高光 | `highlightRecoveryEnabled` 默认 true | 无（clip） | `highlight=0`（clip） |
| 镜头校正 | **默认开**（见 1.2） | **无** | **无**（LibRaw 不具备） |
| 色彩矩阵 | Apple 自有校准，无相机 profile | codec 默认，产物按 sRGB 声明 | LibRaw 内部机型矩阵 |
| 锐化 / 降噪 | 默认开（实测 sharpness 1.0、luma NR 0.29、chroma NR 0.5） | codec 默认 | AHD，默认 |
| 我们显式设置的参数 | `draftMode=false`、`orientation=Up`、`scaleFactor` | 仅裁剪与像素格式 | 输出色、half_size、quality、bright |

`ApplePresentationDecoder.DecodeRaw` 只碰上面 3 个参数；`WindowsRawDecoder.DecodeFull` 只做 `ConvertTo(32bppBGRA)`；`pvraw.cpp` 的 `pv_raw_applyParams` 固定了 LibRaw 输出参数。

### 1.2 镜头校正：Apple 有、但不可控也不可验

实测（`TestCase/A7C01489.ARW`，ILCE-7CM2 + TAMRON 35-100mm F2.8 A078，1500×1000 代理，线性 F16）：

- `isLensCorrectionSupported = true`、`isLensCorrectionEnabled = true`（默认）；
- 同一实例仅切换该开关重渲染：`mean|Δ| = 0.0030`、`max|Δ| = 0.486`、71% 采样变化；同参数连渲两次差值恒为 0（确定性成立），故差异确实来自校正；
- 差异在中心与四角均存在，量级小，与 100mm 焦距下极小的畸变/暗角一致。

结论：**Apple 确实做了某种镜头校正，但它是一个 Bool**——没有强度、没有畸变/TCA/暗角分项，`CIRAWFilter.h` 也没有任何镜头 profile 输入接口（LCP 不支持）。Apple 文档只说"文件含畸变元数据时默认校正"，覆盖机型不公开。

Windows 侧即使调用 `IWICDevelopRaw` 也没有镜头校正能力（微软文档无对应方法）；Android 的 LibRaw `dcraw_process` 管线里根本没有几何校正阶段，`params.aber[4]` 只是 R/B 通道缩放级的横向色差处理，不是畸变校正。

### 1.3 色彩处理

- 显示层正确：`ColorEngine`（skcms）按 ICC/EXIF 推断源色域，Apple 表面用 ExtendedLinearSrgb，Windows/Android 各自打对标签。
- RAW 渲染层不可控：三套引擎的白平衡基准、相机→工作空间矩阵、色调曲线、锐化/降噪默认值都不同，且都不对应 ACR 的 Process Version / Camera Matching Profile。
- 我们没有读 `AsShotNeutral`、没有相机矩阵、没有 DNG opcode，因此**无法把"渲染"从引擎手里拿回来**。
- `decoderVersion` 当前实测可用 7/8，默认 8；RAW 9 需 macOS/iOS 27 且要显式 opt-in。

### 1.4 已确认的问题清单

| 编号 | 问题 | 证据 |
|---|---|---|
| R-01 | 同一 RAW 在三端由三套引擎渲染，默认参数不同，无跨端一致性验证 | §1.1 |
| R-02 | Windows 只有 8 位产物，且未使用 `IWICDevelopRaw`，无任何开发控制 | `WindowsRawDecoder.DecodeFull` |
| R-03 | 胶片栏缩略图来自内嵌 JPEG（相机自己的校正与 Picture Style），主图来自 RAW 引擎 → 两处观感不同 | `ThumbnailService.EnumerateNonHeifSources` |
| R-04 | Apple 默认开启锐化与降噪，应用层无声明、无开关 | 运行时实测默认值 |
| R-05 | 镜头校正不可验证、不可调（强度/TCA/暗角），Windows/Android 完全没有 | §1.2 |
| R-06 | Windows/Android 无高光恢复 | LibRaw `highlight=0`；WIC 无该能力 |
| R-07 | 只有评分写入 XMP，没有任何调色状态与非破坏性侧车 | `PhotoViewer/Core/Exif/Xmp*` |
| R-08 | 没有"线性基底"产物，曝光/白平衡只能在已做色调映射的图上近似 | 现有解码出口 |

## 2 Camera RAW 能力拆解

ACR 的管线本质是：解包 → 黑电平 → 白平衡（raw 域）→ 去马赛克 → 相机矩阵/Profile → 高光恢复 → 曝光与色调映射 → 局部对比/HSL/去雾 → 锐化与降噪 → 输出变换。**"哪些参数必须在哪个域"决定了架构**：

| ACR 分组 | 参数 | 必须在 | 说明 |
|---|---|---|---|
| Light | 曝光 | 线性域 | 线性域乘 2^EV 才是真实档位 |
| Light | 对比度/高光/阴影/白色/黑色 | 显示域（色调曲线） | 本质是 S 曲线分段 |
| Color | 色温/色调 | 线性域（raw 增益） | 在显示域近似会有色偏 |
| Color | 自然饱和度/饱和度 | 显示域 | |
| Effects | 去雾 | 显示域（需局部透射率估计） | 暗通道先验，多 Pass |
| Effects | 清晰度/纹理 | 显示域（局部对比） | 大半径 USM / 双边分离 |
| Effects | 晕影/颗粒 | 显示域 | |
| HSL | 8 色带 H/S/L | 显示域 | 色相带加权，保亮度 |
| Optics | 畸变/暗角/TCA | 几何与乘法，宜在开发阶段 | 也可在渲染阶段做 UV warp |

因此：**Light 的曝光 + Color 的色温色调 + Optics 需要线性基底；其余全部可以在显示域做。**

### 2.1 各分组在三端的可实现性

| 分组 | Apple（CIRawFilter 原生） | 需自研 | 备注 |
|---|---|---|---|
| Light | 曝光 `exposure`；对比 `contrastAmount` 近似；高光 Bool；阴影 `boostShadowAmount`/`shadowBias` 近似 | 高光/阴影/白/黑四段独立曲线 | 原生参数语义与 ACR 不可比 |
| Color | 色温/色调 `neutralTemperature`/`neutralTint` | 饱和度、自然饱和度、HSL 8 带 | 无相机 profile |
| Effects | 清晰度近似 `contrastAmount` | 去雾、颗粒、晕影 | RAW 9 下 `detailAmount` 失效 |
| HSL | 无 | 全部 | ColorCube / 自定义 kernel |
| Optics | 仅一个 Bool | 畸变/TCA/暗角 + profile 数据 | 无强度控制 |

Windows（`IWICDevelopRaw`）可做曝光/色温/色调/对比/饱和/锐化/降噪/色调曲线，**无镜头校正/无色差/无 HSL/无局部**；且规范只强制 codec 实现 `SetRotation`+`SetRenderMode`，其余能力随 codec 浮动。

## 3 目标架构

### 3.1 两级管线

```
文件 → Develop（每端一个后端，产出"线性基底"）
     → Grade（共享参数 + 各端 GPU 着色器）
     → 上屏 / 分析 / 导出
```

**Develop 契约**：`DecodedImage` + 线性工作空间（扩展线性 sRGB）+ 源尺寸/方向 + 元数据（as-shot 白平衡、镜头、相机）。

三端取法：

| 平台 | 取法 | 状态 |
|---|---|---|
| macOS / iOS | `CIRawFilter`：`boostAmount = 0`（关全局色调曲线，得到线性响应）、`extendedDynamicRangeAmount = 1`（保住 >1 的高光）、`exposure = 0`、`baselineExposure` 归一化，渲染进扩展线性 sRGB | 现成 API，只需改参数 |
| Android | LibRaw：`output_bps=16`、`gamm[0]=gamm[1]=1`、`no_auto_bright=1`、`use_camera_wb=1`、`highlight` 视需要 | 需改 `pvraw.cpp` 并扩 ABI |
| Windows | 必须换 LibRaw（自编译或 Sdcb.LibRaw） | **最大工程量**；`Sdcb.LibRaw` 无 macOS/Android 运行时，需自行交叉编译四端 |

实测补充：`boostAmount=0` 后线性均值 0.148（原 0.31），`extendedDynamicRangeAmount=1` 时最大值从 1.02 抬到 1.50，说明这条路径能拿到"未被色调曲线压缩"的基底，但**高光余量有限**（约 +0.6 EV），因为 Apple 在更早的阶段已做高光恢复。真正的 RAW 高光余量只有自建管线（LibRaw `unpack()` 后自处理）才有。

### 3.2 Grade 阶段

- 共享 C#：`GradingSettings`（值域与 ACR 同名）→ 每帧只算一次派生量（白平衡增益 3×3、曝光系数、色调曲线 LUT、HSL 8 带权重、镜头 warp 系数）。
- 各端着色器：三端都是自有 GPU 管线（`MetalPhotoRenderer` / `WindowsPhotoView` 的 D3D11 / `AndroidPhotoRenderer` 的 GLES3），各加一段 fragment uniform + 纹理（LUT），数学由共享常量约束，避免三份实现漂移。
- 去雾/清晰度需要多 Pass（模糊金字塔、透射率），单 Pass 做不了。
- 代理预览：滑杆拖动只重绘代理纹理（毫秒级）；Full 在停手后按需重算，沿用现有 `PhotoLoadSession` 代际/租约模型。

### 3.3 状态与持久化

- 每张照片一份 `GradingSettings`，写 XMP 侧车 `crs:` 命名空间（字段子集见 §3.6）。
- 好处：与 Lightroom / Camera Raw 双向可读；原文件只读，符合既有约束。
- 缓存键加入 `GradingSettings` 版本，避免旧产物串色。

### 3.4 与现有管线的接点

- 参考 `ImageEnhancer`（雾天增强）与 `PhotoLoadSession`：调色应挂同一层——切图持续生效、新产物到达自动重算、内存压力下可降级。
- 分析口径（直方图、`DisplayCrop`、DINO/CV 输入、细节预览绿框）必须明确"用调色前还是调色后"，建议：**人眼看到的（主图/直方图/导航图）用调色后；CV/DINO 用调色前**，避免特征随滑杆漂移。
- 100% 口径：若镜头校正走渲染期 warp，则"1 源像素 = 1 屏幕像素"改为"1 校正后像素 = 1 屏幕像素"，需在 spec 中登记。

### 3.5 代理预览与全分辨率导出

**预览天然就是代理分辨率**：调色是显示 Pass 里的片元着色器，成本 = 屏幕像素，与源分辨率无关。拖动滑杆只重绘屏幕大小的四边形，代理图只用来在 Full 未到时保证流畅。

**导出**：同一套着色器做一次离屏全分辨率 Pass。像素管线不变，只是渲染目标改成全尺寸。

三条一致性规则（否则预览与导出会对不上）：

1. **参数归一化**：曝光用 EV、滑杆用 0–100、模糊/局部半径用图像对角线比例，任何参数都不带像素单位。
2. **局部算子在固定工作分辨率算**：去雾的透射率图、清晰度的 base/detail 分离统一在长边 2048 的中继图上计算，再双线性上采样到目标分辨率应用。这样预览与导出共用同一张中继图，差别只剩最终的合成采样。
3. **LUT/矩阵与分辨率无关**：色调曲线 LUT、白平衡矩阵、HSL 权重只由参数决定。

验收：全分辨率导出后缩到代理尺寸，与预览帧比对，`mean ΔE < 1`、`max < 3`；局部算子开/关各测一遍。

导出管线：全分辨率 Develop → 离屏 Grade → 编码（JPEG 8bit + ICC / TIFF 16bit）→ 元数据（复制 EXIF、写 `Orientation=1`、嵌 ICC）。

工程约束：

- **Android 必须分块**：`GL_MAX_TEXTURE_SIZE` 常见 4096，7008 宽的图放不进单张纹理，要按 tile（建议 2048²）+ halo（= 最大模糊半径）拼接。Mac/iOS/Windows 的 16384 上限可单幅。
- **内存**：24MP F16 单张 262MB，全程峰值按 3× 估。导出走独占队列，不进 LRU 缓存，完成即释放；逐张进度 + 可取消。
- **首版范围**：桌面端（Mac/Windows）先做；移动端导出后置，受纹理尺寸与内存限制。

### 3.6 XMP 侧车与 Adobe 互通

**现有实现写不了 `crs:`**：评分写入是**等长字节改写**（`XmpRatingSpanLocator`：只替换已存在 `xap:Rating` 的值，值变长只消耗邻近空白），而 ARW 的 TIFF 包里没有为几百字节的 `crs:` 块预留空间，改长度就得重建容器。所以**调色设置只能写 sidecar**。

这需要改两处既有决策：

1. `Design/spec/scope.md`：「不提供…导出图片的入口」——导出功能要进 scope。
2. `Plans/rating-storage-correction-plan-1.md`：「不创建 sidecar」——改为**只在写入调色设置时创建**，纯评分流程保持不创建。

三个必须遵守的互通纪律：

- **新建 sidecar 时同时写 `xmp:Rating`**。相机 RAW 的侧车是 ACR/Lightroom 的唯一设置来源，侧车里没有星级，LR 会显示 0 星，覆盖掉 ARW 内嵌的评分。星级放独立 `rdf:Description`，`-1` 表示 rejected。
- **未知字段原样保留**：读-改-写用 XmpCore 解析后只增删自己的属性，不动别人的命名空间。Lightroom 15 起还会在旁边写 `.ACR` 侧车（蒙版/AI 超分/去噪），**不得删除或改写**。
- **精确值写自己的命名空间**：ACR 的 `Temperature/Tint` 定义在 Adobe 的相机色彩空间里，和我们的白平衡增益不是同一尺度，反复换算会漂移。所以 `crs:` 写近似值给 Adobe 看，`pv:` 命名空间写精确值给自己读；读取时优先 `pv:`，其次 `crs:`。

**最小字段集**（照抄即可被 ACR 识别）：

| 类别 | 字段 |
|---|---|
| 必需 | `crs:Version`（ACR 插件版本，非处理版本）、`crs:ProcessVersion`（PV5=`11.0`、PV6=`15.4`，**写错会降级渲染**）、`crs:WhiteBalance`、`crs:HasSettings="True"`、`crs:AlreadyApplied="False"`、`crs:RawFileName` |
| 白平衡 | 仅当 `WhiteBalance="Custom"` 时 `Temperature`(K)/`Tint`(±150) 才生效 |
| 五段影调 | `Exposure2012`(-5..+5 EV)、`Contrast2012`/`Highlights2012`/`Shadows2012`/`Whites2012`/`Blacks2012`(-100..100) |
| 颜色/效果 | `Vibrance`/`Saturation`/`Dehaze`/`Clarity2012`/`Texture`(-100..100) |
| HSL | 八带 `Red/Orange/Yellow/Green/Aqua/Blue/Purple/Magenta` 的 `HueAdjustment*`/`SaturationAdjustment*`/`LuminanceAdjustment*` |
| 光学 | `LensProfileEnable`/`AutoLateralCA`/`LensProfileName`/`LensManualDistortionAmount`/`VignetteAmount`/`VignetteMidpoint` |
| 曲线 | `ToneCurveName2012` + `ToneCurvePV2012`（`rdf:Seq`，每项 `"x, y"`，0..255） |
| 不要写 | `crs:Look`、`crs:CameraProfile`（无对应 .dcp 时）、`AlreadyApplied="True"`、任何自造的 `crs:*` 扩展 |

**互通的实际体验边界**：Lightroom 不监听外部侧车改动，只在导入或手动「从文件读取元数据」时生效——所以是"文件级互通"，不是实时同步。

**语义互通 ≠ 像素一致**：ACR 读我们的侧车能把滑杆摆到对应位置，但它的引擎渲染结果不会和我们一样（没有 Camera Matching Profile，`.dcp` 的私有查找表无法复刻）。验收标准只能是"滑杆位置一致 + 方向一致"，不能是像素一致。

## 4 镜头校正

### 4.1 数据源（本机实测）

Adobe 的 `.lcp` 是**纯文本 XMP**，不是私有二进制。本机 `/Library/Application Support/Adobe/CameraRaw/LensProfiles/1.0/` 有 **3479 个 profile**，用包内已有的 `XmpCore` 就能解析：

- 每个 profile 按 **焦距 × 光圈 × 对焦距离** 采样成网格（如 16/20/24/28/35mm × f/2.97/4/4.97/6），逐点给 `stCamera:RadialDistortParam1..3`（poly3 畸变）、`VignetteModelParam1..3` / `VignetteModelPiecewiseParam`（暗角）、`PerspectiveModel`；899 个 profile 另带 `Chromatic*`（色差）。
- 覆盖确实到"最新"：Tamron E 口 164 支，含 A064（16-30/2.8）、A075（25-200）、F072（90/2.8）等 2025–2026 年的镜头。
- **但测试用的 A078（35-100/2.8）本机库里没有**——厂商新镜同样会滞后，缺 profile 时必须能退回内嵌数据或不校正。

| 数据源 | 覆盖 | 许可 | 结论 |
|---|---|---|---|
| 用户机器上的 ACR LCP（运行期读） | 本机 3479 支，随 ACR 更新 | 运行期读取，不随应用分发 | **首选** |
| 文件内嵌（Sony/Canon/Nikon MakerNote、DNG opcode） | 取决于机型是否写入 | 无 | 次选，缺 LCP 时用 |
| lensfun | 数千支 | 库 LGPL-3 / 数据 CC BY-SA 3.0 | 兜底（用户可选安装或离线打包） |
| 自建标定 | 自己拍靶 | 自有 | 长期补充 |

### 4.2 实现要点

- 几何模型直接用 LCP 的 poly3；走 lensfun 时 poly3/poly5；都没有才用 Brown-Conrady。
- 渲染期 UV 逆映射 warp + 双线性采样；色差按 R/B 通道独立缩放；暗角是径向增益。
- 三个变量按焦距/光圈/对焦距离插值，焦距权重最高（畸变随焦距变化最大）。
- 校正后四角会出现黑边，按 profile 的 `ScaleFactor` 或实测有效区裁切；裁切会改变源尺寸，必须与 100% 口径、导航图、焦点框一并登记。
- **互通红利**：写 sidecar 时直接写 ACR 认识的 `crs:LensProfileName`，ACR 会选中同一份 profile，几何完全一致。

## 5 相机与镜头覆盖

没有任何一套栈能"立刻覆盖最新机型"：

- LibRaw 0.22.3 支持 1284 款机型（0.22.0 能力清单），新机一批一批补；CIRawFilter 运行时 941 个机型名（macOS 26.6.2 实测，含 "Sony Alpha ILCE-7C II"），随 OS 更新；WIC 取决于 Store 的 Raw Image Extension。
- Apple 公开文章口径（约 562 款）与 Core Image 解码器口径（784 / 本机 941）不是同一统计，判断覆盖只能用运行时 `supportedCameraModels`。
- lensfun 稳定版停在 0.3.4（2023-07），但数据库到 2026-10 仍在提交——库旧、数据新，取数时按数据仓库快照。
- 现实策略是**分层回退**（现有代码已有雏形）：LibRaw → 平台引擎 → 内嵌整幅 JPEG（尺寸必须等于有效成像区）→ 如实停在预览档。
- 镜头数据要可热更新（独立的 profile JSON / 版本号），不能绑在应用发版上。
- 关注点：macOS 27 的 `downloadResources`（RAW 资源按需下载）说明 Apple 可能转向"可下载的 RAW 支持"，这会改变新机覆盖的游戏规则。

## 6 分阶段实施

| 阶段 | 内容 | 产出 | 验收 | 估算 |
|---|---|---|---|---|
| P0 审计收口 | 建"四端同图对拍"基准：同一 RAW 出图，与 LibRaw 固定参数参考比对 | 基准图 + ΔE/几何报告 | 能量化 R-01 | 3–5 天 |
| P1 线性基底 | Apple `boostAmount=0`+EDR；Android LibRaw 16 位线性；Windows 引入 LibRaw | 统一 Develop 契约 | 三端线性均值/曲线一致，ΔE 在阈值内 | 1–2 周 |
| P2 Light+Color | 五段影调 + 白平衡/饱和度；显示 Pass 着色器；sidecar 读写（§3.6） | 可用调色面板 | 滑杆拖动 <16ms/帧；LR 能读出相同滑杆位置 | 1.5–2 周 |
| P3 Effects+HSL | 去雾（暗通道+导向滤波）、清晰度、晕影、颗粒、HSL 8 带 | 完整面板 | 与 ACR 方向一致（不追数值） | 1–2 周 |
| P4 Optics | LCP 解析 + poly3 warp + 暗角 + 自动裁切 | 镜头校正 | 直线校正后残余 <1px（24MP） | 1–2 周 |
| P5 全分辨率导出 | 离屏全分辨率 Pass + 编码 + 元数据；一致性验收（§3.5） | 桌面端导出 | 导出缩图与预览 `mean ΔE < 1`；批量可取消 | 1–1.5 周 |
| P6 移动端导出 | Android 分块导出（halo 拼接） | 移动端导出 | 与桌面同图一致 | +1 周 |

P0/P1 是分水岭：没有统一线性基底，后面的滑杆只是"滤镜"，不是 RAW 调色。整体约 7–11 周。

## 7 已有研究（GitHub / 开源）

- **没有可直接嵌入的「ACR 面板」开源实现。** 面板语义（Process Version / Camera Matching Profile）是 Adobe 私有。
- **darktable 5.6.2**（GPL-3+）：唯一把 ACR 各分组拆成独立模块的项目，可当算法字典——`exposure.c`/`basicadj.c`（Light）、`temperature.c`/`channelmixerrgb.c`/`colorzones.c`（Color/HSL）、`hazeremoval.c`/`diffuse.c`/`vignette.c`/`grain.c`（Effects）、`lens.c`（Optics，lensfun + 内嵌元数据双源）。**GPL 只能读不能链。**
- **RawTherapee / ART**（GPL-3）：强项是小波/局部对比/CAM02，思路可借。
- **Filmulator**：按受光量模拟银盐显影，天然高光滚降 + 局部对比，适合"少参数出好 look"。
- **LibRaw**：全平台解码与元数据的事实标准，但**不做畸变/暗角**；`aber[4]` 只是 R/B 缩放级 CA。
- **rawspeed**（LGPL-2）：只解 CFA，不含处理，适合替换 LibRaw 的解包层。
- **Sdcb.LibRaw**：.NET 绑定，0.21.x、2025 后无新版，原生运行时只有 win/linux，**不能直接用于 macOS/iOS/Android**。
- **lensfun**：镜头数据最全（含第三方），库 LGPL-3 / 数据 CC BY-SA 3.0，无官方 .NET 绑定。
- 结论：**算法可抄，代码基本不可链**；调色面板与镜头数据必须自研/自建。

## 8 风险与不做的部分

- **不追求与 ACR 数值一致**：没有 Camera Matching Profile 与 Adobe 的 PV 语义，只能做到"方向一致、观感接近"。
- **不做局部调整/蒙版、不做 AI 降噪、不做透视/全景**：超出"基本调色"范围。
- **性能与内存**：24MP F16 约 200MB/张；调色版缓存必须走代理 + LRU，不能与现有缓存叠加。
- **许可**：darktable/RawTherapee 是 GPL-3，只能读算法不能链接；lensfun LGPL-3 + CC BY-SA 数据；DNG SDK 是 Adobe 自有许可（需署名）。
- **iOS 算力**：RAW 全分辨率开发 + 多 Pass 调色在 iPad 上要限定代理尺寸，Full 的重算必须可取消。

## 9 参考

- Apple：`CIRAWFilter.h`（本机 SDK 27.0）、[CIRAWFilter 文档](https://developer.apple.com/documentation/coreimage/cirawfilter)、[WWDC26-305](https://developer.apple.com/videos/play/wwdc2026/305/)、[WWDC21-10160](https://developer.apple.com/videos/play/wwdc2021/10160/)
- 微软：[IWICDevelopRaw](https://learn.microsoft.com/en-us/windows/win32/wic/-wic-imp-iwicdevelopraw)、[WIC codecs](https://learn.microsoft.com/en-us/windows/win32/wic/native-wic-codecs)
- LibRaw：[API 数据结构](https://www.libraw.org/docs/API-datastruct-eng.html)、[下载/机型数](https://www.libraw.org/download)
- 算法：[darktable 模块清单](https://github.com/darktable-org/darktable/blob/master/src/iop/CMakeLists.txt)、[darktable lens correction](https://docs.darktable.org/usermanual/5.6/en/module-reference/processing-modules/lens-correction/)、[He 暗通道去雾](https://people.csail.mit.edu/kaiming/cvpr09/index.html)、[DNG 1.7 规范（Opcode 3 WarpRectilinear）](https://helpx.adobe.com/content/dam/help/en/photoshop/pdf/DNG_Spec_1_7_0_0.pdf)
- 镜头数据：[lensfun](https://github.com/lensfun/lensfun)
