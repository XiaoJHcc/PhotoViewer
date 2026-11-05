# Core/Color — 色彩解释与转换

`namespace PhotoViewer.Core.Color`；无 UI 框架引用。原生主图流程见[架构文档](../../../Design/architecture.md) §5.6。

| 文件 | 职责 |
|---|---|
| ImageColorSpace.cs | 与像素编码正交的 Legacy / sRGB / Display P3 / Adobe RGB / Linear sRGB / ICC 标签。 |
| SkColorSpaces.cs | （internal）`ImageColorSpace` + ICC → `SKColorSpace` 的**唯一映射**，供色彩转换与 Core/Image 像素操作共用；常用空间常驻共享，ICC 每次新建并以可释放句柄表达所有权，`Legacy` 按 sRGB 解释。新增色彩标签只改这一处。 |
| JpegColorMetadata.cs | JPEG 容器色彩元数据：只扫标记段（到 SOS 即停、读完恢复流位置、不解码像素），按序号拼回 APP2 多片 `ICC_PROFILE` 并校验 `acsp`/声明长度，读 APP1 EXIF `ColorSpace(0xA001)` 与互操作标识 `R0x`（R03 = Adobe RGB）。`ResolveColorSpace()`：有效 ICC 优先（数值上就是 sRGB 的 profile 归为 sRGB，保住查表快通道），无 ICC 时交给 `ColorEngine.InferJpegColorSpace`，两者皆缺省 sRGB。 |
| ColorEngine.cs | Skia skcms：ToScRgb16 保留负值与 >1 分量；ToSrgb8 为 BGRA8 分析口径；ToDisplayP3 为 8 位 Display P3（供 8 位展示档在增强后回到原编码，不扩大成 F16）；ToDisplayEncoding 按显式（编码 + 色彩标签 + ICC）回写任意展示口径，供"先在 sRGB8 上算完"的流程回到源口径；ToAnalysisSrgb8 同时应用显示方向。源/目标编码经 `ColorType`（internal）显式映射（RgbaF16/Rgba8888/Bgra8888；Rgb24 先转 Bgra32），未知编码抛出不猜字节序——8 位 Display P3 展示产物因此可无损参与 CV/DINO、直方图与增强口径。色彩空间解析统一走 `SkColorSpaces`；非像素对齐行距先打包。**同原色配对走查表快通道**：线性 F16 → sRGB8、sRGB8/Rgb24 → 线性 F16 只差传递函数，按行分带并行做逐通道查表（65536 项半精度位模式表 / 256 项 8 位表，均由 skcms 自身枚举生成，故含负值/NaN/Inf 在内逐字节等同 skcms），源 alpha 非不透明（预乘语义）时自动退回 skcms；换原色（Display P3/Adobe RGB/ICC）仍由 skcms 处理。 |

- 线性 scRGB/F16 必须搭配线性色彩标签，Apple 表面使用 ExtendedLinearSrgb。
- JPEG 色域只能从容器元数据判定（`JpegColorMetadata`）：**Skia 对任何文件只暴露数值色彩空间，`SKColorSpace.ToProfile()` 恒为空（连 `CreateIcc` 建出来的也是），拿不到 ICC 原始字节**，所以 JPEG 的 ICC 由 Core/Image 从 APP2 自读后随产物携带，色域推断只消费该结果。有效 ICC 优先，`EXIF ColorSpace=2` 或 `R03` 推 Adobe RGB，其余默认 sRGB。
- `Legacy` 是"未标记、按 sRGB 解释"的历史口径，与 `Srgb` 数值等价（`Legacy → Srgb` 的 8 位转换逐字节不变），显式规范化不会让 DINO/CV 旧像素漂移。
- 转换返回一份调用方租约；输入所有权不转移。原格式命中仍 Acquire，不能自行 Dispose 源持有者租约。
- 验证工具：[Tools/ColorTestTool](../../../Tools/ColorTestTool/ColorTestTool.csproj)，用真实 `Fixtures/jpeg`（sRGB/P3/Adobe/未标记 Adobe、6000×4000 P3）与自造 PNG 校验：独立矩阵/传递函数、三色域转 F16 与参考一致、ICC 跨代理/缩放/方向/裁剪/增强全链路不丢、非 JPEG 广色域保全、`Legacy → sRGB8` 逐字节等价、行距与往返；[Tools/ColorEngineLutCheck](../../../Tools/ColorEngineLutCheck/ColorEngineLutCheck.csproj) 把查表快通道与 skcms 逐字节对照（全部半精度位模式、随机 8 位组合、真实照片）并给出新旧耗时。
