# PhotoViewer.Apple — Apple 平台共享源

`PhotoViewer.Apple/` 是**纯源文件目录**（不是独立 csproj/程序集）：`PhotoViewer.Mac/PhotoViewer.Mac.csproj` 与 `PhotoViewer.iOS/PhotoViewer.iOS.csproj` 都以 `<Compile Include="..\PhotoViewer.Apple\**\*.cs"/>` 链接编译同一目录（I1 起 iPad 原生壳已接入）。

**硬规则**：本目录不得出现 AppKit / UIKit 类型（`NSView`、`NSEvent`、`NSImage`、`NSColor`、`UIView` 等）——iOS 侧无法编译。只允许 Foundation / CoreGraphics / CoreAnimation / ImageIO / Metal 与共享 `PhotoViewer` 项目类型。平台事件字段解包、原生视图与图层挂载、系统图标对象创建一律留在各 head 的 `Native/`、`Core/` 下。

命名空间统一为 `PhotoViewer.Apple`。**几何/视口与命令查询已迁到 [PhotoViewer.Shared](../PhotoViewer.Shared/AGENTS.md)**（`PhotoViewport`、`MainScreenLayout`、`FilmstripGeometry`、`ControlBarLayout`、`AnalysisPanelGeometry`、`SimilarityPanelGeometry`、`ExifTableGeometry`、`InputCommandMap`），本目录只保留 Apple 专属源。改动任一共享源都会同时影响 Mac 与 iOS（Android 也链 `PhotoViewer.Shared`）；合并门 = Mac 八套 `--pv-native-*` 回归零行为变化 + iOS 七套 `--pv-native-*/-check` 回归通过。

| 文件 | 职责 |
|---|---|
| ApplePresentationDecoder.cs | ImageIO 的 JPEG/HEIF/RAW 统一主图解码器，按请求编码产出两种表面：RGBA16F 扩展线性 sRGB（代理档，广色域超范围分量不裁剪）或 8 位 Display P3（Full 档/压力降级；RGBA8888 预乘，P3 立方体外的原色钳到 P3 边界）。两者都携带源尺寸与未应用 EXIF 方向；JPEG 扫描 ICC APP2 后按共享 EXIF 推断无标签色域；无内嵌预览的用户图片按短边缩解码。原 `PhotoViewer.Mac/Core/MacPresentationDecoder.cs`，F16 路径行为未变。 |
| MetalPhotoRenderer.cs | Metal 渲染核心：设备/队列、MSL 着色器（RGBA16F 顶点变换 + 八方向 UV）、扩展线性 sRGB 的 `CAMetalLayer`（**绘制目标恒为 RGBA16F**，两种主图编码共用）、纹理上传与 blit mipmap、帧编码、导航图等比线性帧读回、纹理字节记账（按实际编码的每像素字节）。纹理格式按展示编码选择：F16 线性 scRGB → `RGBA16Float`；8 位 P3 → `RGBA8Unorm_sRGB`（采样即线性 P3，再由第二条片段管线用 3×3 矩阵 `P3ToExtendedLinearSrgb`（片段 uniform，唯一来源）换算到扩展线性 sRGB）。两种编码共用采样器/UV/方向/mipmap 逻辑，故 F16 代理↔P3 Full 热替换不产生可见色偏。宿主只负责把 `Layer` 挂到视图并调用 `DrawFrame`；视口数学取共享 `PhotoViewport`。 |
| AppleAcceptanceChecks.cs | Mac `--pv-native-smoke` 与 iOS `--pv-native-photo-check` **共用**的验收判定：GPU EXIF 八方向角点核对（导航图线性帧）、色卡中心像素与期望线性 scRGB 值核对（8 位 P3 产物先经 `ColorEngine` 归一到线性 scRGB）、`ColorExpectation` 按编码给出下界（P3 立方体装不下的 Adobe 红原色按 P3 钳位值）。 |
| AppleInputMapping.cs | 原生事件码 + 修饰键掩码 → 共享 `AppGesture` 的 **Apple 专属解码**：`FromRaw` / `FromHidUsage`、HID usage → `GestureKey` 表、`AppleKeyboardMapping` 修饰键语义。数值即契约。「手势 → 用户命令」的通用查询在共享 `PhotoViewer.Shared/InputCommandMap.CommandForGesture`，Mac 的 `MacInput.FromEvent/ResolveCommand` 与 iOS `PressesBegan` 共用同一张命令表。Android 不得复用本文件，另写 `AndroidInputMapping`。 |
| EnhancementAcceptance.cs | 增强预览的共用验收工具：确定性生成低对比度 JPEG（2400×1600，窄亮度区间渐变 + 平缓色块，只写自有沙盒/输出目录）；两帧 BGRA8 的抽样平均/最大绝对差；**`CreateOrientationJpeg(path, orientation, width, height)`** 生成带 EXIF 方向码的四角异色 JPEG（插入最小 EXIF APP1，不依赖第三方写库）；**`CornerSignatures(image)`** 给导航帧四角分类（红/蓝/绿/白/灰），用于证明增强前后方向与色相排列不变。Mac `--pv-native-analysis` 与 iOS `--pv-native-analysis-check` 都用它证明"增强前后上屏像素明显变化"（平均差 ≥6、最大差 ≥30）与"增强不旋转照片"，只断言 `EnhancedDisplay` 不再足以防止"点了没反应"回归。 |
| AppleSymbolNames.cs | 业务命令 → SF Symbols 名称字符串映射；系统图标对象的创建留在各平台头部。 |
