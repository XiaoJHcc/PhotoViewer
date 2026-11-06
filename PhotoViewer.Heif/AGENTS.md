# PhotoViewer.Heif — libheif 色彩解码共享源

> 纯源文件目录，**不是独立程序集**。由 `PhotoViewer.Windows`、`PhotoViewer.Android` 与 `Tools/HeifFastDecodeTest` 用 `Compile Include` 显式链接；Apple 头（Mac/iOS）不链接，走 ImageIO。

`namespace PhotoViewer.Core.Image.Heif`；只依赖 `PhotoViewer`（Core）、`LibHeifSharp` 与 `SkiaSharp`，不得出现任何平台类型（WinUI/AndroidX/AppKit）。

| 文件 | 职责 |
|---|---|
| `LibHeifColorDecoder.cs` | `internal static` 的 libheif 软解出口：读容器真实色彩声明（ICC 优先，其次 `nclx`）→ 打标签 → 经共享 `ColorEngine` 转到目标口径；共享标签词汇表达不了的组合（BT.2020 原色、BT.709 / 伽马 2.2 / 伽马 2.8 曲线）用同一套 skcms（Skia 预设与纯幂参数）换算，不自造矩阵。位深契约：8 位源走交错 RGB24 → BGRA32；10/12 位源请求 `InterleavedRgb48LE` → 源空间半浮点码值（归一化除数取平面自身声明的码值范围，不扫像素、不看白点）→ 色彩转换，不先量化到 8 位。PQ/HLG 与其余原色/传递特性如实拒绝并写 `HeifLoader.SetLastDecodeError`。几何取 libheif 真实变换：产物已是显示朝向（`Orientation` 恒 1），`SourceWidth/SourceHeight` 是全幅变换后尺寸，代理缩放保留；不按 EXIF 猜 HEIF 方向。`InspectColor` 只读声明（容器解析器优先，读不了的结构再用 libheif 句柄读色彩属性，不解码像素），供"只认 ICC 的系统解码器"判定能否接文件：PQ/HLG 拒绝，两条路都读不到声明时返回 `UnsupportedContainer`，调用方不得据此假定 SDR。 |

## 出口口径

`LibHeifOutput` 决定产物：`Bgra32Srgb`（缩略图/分析，`IHeifDecoder` 契约）、`RgbaF16ScRgb`（Windows 主图线性表面）、`Rgba8Srgb` / `Rgba8DisplayP3`（Android 8 位展示档）。

## 边界

- 不是解码器入口：`IHeifDecoder` 实现（快速路径、容器 `thmb` 项、系统 `BitmapFactory`、许可上限）留在各平台头工程；这里只负责"已经交给 libheif 之后"的色彩与位深。
- 产物方向恒为 1（libheif 已应用容器 `irot`/`imir`）：链接本模块的 `IHeifDecoder` 必须声明 `OutputPreRotated = true`，`ThumbnailService`/`BitmapLoader` 不得再旋转。
- 不扩 HDR：PQ/HLG 只拒绝，不做 tone map；透明度口径与原实现一致（不透明）。
- 新增色彩标签或原色/传递函数支持只改这一处，不要在平台头工程复制矩阵或曲线表。
- 验证入口：`Tools/HeifFastDecodeTest e2e`（真实色卡 ICC/nclx、10/12 位暗平面、8 位 sRGB/P3 出口、PQ/HLG 拒绝、`irot` 非 0 的方向与源几何）。
