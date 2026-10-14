# Core/Color — 色彩解释与转换

`namespace PhotoViewer.Core.Color`；无 UI 框架引用。原生主图流程见根 AGENTS.md §5.6。

| 文件 | 职责 |
|---|---|
| ImageColorSpace.cs | 与像素编码正交的 Legacy / sRGB / Display P3 / Adobe RGB / Linear sRGB / ICC 标签。 |
| ColorEngine.cs | Skia skcms：ToScRgb16 保留负值与 >1 分量；ToSrgb8 为 BGRA8 分析口径；ToAnalysisSrgb8 同时应用显示方向。常用空间常驻，自定义 ICC 按调用释放；非像素对齐行距先打包。 |

- 线性 scRGB/F16 必须搭配线性色彩标签，Apple 表面使用 ExtendedLinearSrgb。
- JPEG 无 ICC 推断集中于 InferJpegColorSpace；ICC 有效时优先，未知用户输入默认 sRGB。
- 转换返回一份调用方租约；输入所有权不转移。原格式命中仍 Acquire，不能自行 Dispose 源持有者租约。
- 验证工具：[Tools/ColorTestTool](../../../Tools/ColorTestTool/ColorTestTool.csproj)，验证独立矩阵/传递函数、广色域负值、行距及往返。
