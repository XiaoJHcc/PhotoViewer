# PhotoViewer.Apple — Apple 平台共享源

`PhotoViewer.Apple/` 是**纯源文件目录**（不是独立 csproj/程序集）：由 `PhotoViewer.Mac/PhotoViewer.Mac.csproj` 以 `<Compile Include="..\PhotoViewer.Apple\**\*.cs"/>` 链接编译，iOS 原生壳（UIKit）后续以同样方式链接同一目录。

**硬规则**：本目录不得出现 AppKit / UIKit 类型（`NSView`、`NSEvent`、`NSImage`、`NSColor`、`UIView` 等）——iOS 侧无法编译。只允许 Foundation / CoreGraphics / CoreAnimation / ImageIO / Metal 与共享 `PhotoViewer` 项目类型。平台事件字段解包、原生视图与图层挂载、系统图标对象创建一律留在各 head 的 `Native/`、`Core/` 下。

命名空间统一为 `PhotoViewer.Apple`。改动共享源会同时影响 Mac 与 iOS 两个原生壳；迁移期以 Mac 的八套 `--pv-native-*` 回归作为合并门（零行为变化）。

| 文件 | 职责 |
|---|---|
| ApplePresentationDecoder.cs | ImageIO 的 JPEG/HEIF/RAW 统一主图解码器：输出 RGBA16F 扩展线性 sRGB 并携带源尺寸与未应用 EXIF 方向；JPEG 扫描 ICC APP2 后按共享 EXIF 推断无标签色域；无内嵌预览的用户图片按短边缩解码。原 `PhotoViewer.Mac/Core/MacPresentationDecoder.cs`，行为未变。 |
| MetalPhotoRenderer.cs | Metal 渲染核心：设备/队列、MSL 着色器（RGBA16F 顶点变换 + 八方向 UV）、扩展线性 sRGB 的 `CAMetalLayer`、纹理上传与 blit mipmap、帧编码、导航图等比线性帧读回、纹理字节记账。宿主只负责把 `Layer` 挂到视图并调用 `DrawFrame`。 |
| PhotoViewport.cs | 视口纯数学（纯 double）：适应缩放、锚点缩放与对焦点居中、平移钳制、导航图尺寸/可见范围、`NeedsFull`（物理 100% 或代理不足）。宿主把结果换算成原生坐标并驱动重绘。 |
| FilmstripGeometry.cs | 胶片栏几何（96×144 卡片、10/5/10 内边距、横向首项居中、文档长度、可见项范围）与 `FilmstripAnimation`（350ms 三次缓出曲线、进度与 1 点阈值判定）。 |
| ControlBarLayout.cs | 控制栏几何：分组类型（EXIF/评分/命令）的子控件尺寸与内边距、分组主轴/交叉轴长度、整组换行/换列、行内居中、文档尺寸与子控件偏移。宿主只落 frame。 |
| AnalysisPanelGeometry.cs | 分析栏瓦片几何：边长 `floor((可用主轴长度 − 20 − 8×(n−1)) / n)`、交叉轴 = 边长 + 20、槽位起点。 |
| AppleInputMapping.cs | 原生事件码 + 修饰键掩码 → 共享 `AppGesture` 映射（含键码表），供各平台事件解包与 SwiftUI 录制复用同一口径；数值即契约。 |
| AppleSymbolNames.cs | 业务命令 → SF Symbols 名称字符串映射；系统图标对象的创建留在各平台头部。 |
