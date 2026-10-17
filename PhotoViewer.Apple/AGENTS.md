# PhotoViewer.Apple — Apple 平台共享源

`PhotoViewer.Apple/` 是**纯源文件目录**（不是独立 csproj/程序集）：`PhotoViewer.Mac/PhotoViewer.Mac.csproj` 与 `PhotoViewer.iOS/PhotoViewer.iOS.csproj` 都以 `<Compile Include="..\PhotoViewer.Apple\**\*.cs"/>` 链接编译同一目录（I1 起 iPad 原生壳已接入）。

**硬规则**：本目录不得出现 AppKit / UIKit 类型（`NSView`、`NSEvent`、`NSImage`、`NSColor`、`UIView` 等）——iOS 侧无法编译。只允许 Foundation / CoreGraphics / CoreAnimation / ImageIO / Metal 与共享 `PhotoViewer` 项目类型。平台事件字段解包、原生视图与图层挂载、系统图标对象创建一律留在各 head 的 `Native/`、`Core/` 下。

命名空间统一为 `PhotoViewer.Apple`。改动共享源会同时影响 Mac 与 iOS 两个原生壳；合并门 = Mac 八套 `--pv-native-*` 回归零行为变化 + iOS 六套 `--pv-native-*/-check` 回归通过（几何改动尤其要两平台都跑）。

| 文件 | 职责 |
|---|---|
| ApplePresentationDecoder.cs | ImageIO 的 JPEG/HEIF/RAW 统一主图解码器，按请求编码产出两种表面：RGBA16F 扩展线性 sRGB（代理档，广色域超范围分量不裁剪）或 8 位 Display P3（Full 档/压力降级；RGBA8888 预乘，P3 立方体外的原色钳到 P3 边界）。两者都携带源尺寸与未应用 EXIF 方向；JPEG 扫描 ICC APP2 后按共享 EXIF 推断无标签色域；无内嵌预览的用户图片按短边缩解码。原 `PhotoViewer.Mac/Core/MacPresentationDecoder.cs`，F16 路径行为未变。 |
| MetalPhotoRenderer.cs | Metal 渲染核心：设备/队列、MSL 着色器（RGBA16F 顶点变换 + 八方向 UV）、扩展线性 sRGB 的 `CAMetalLayer`（**绘制目标恒为 RGBA16F**，两种主图编码共用）、纹理上传与 blit mipmap、帧编码、导航图等比线性帧读回、纹理字节记账（按实际编码的每像素字节）。纹理格式按展示编码选择：F16 线性 scRGB → `RGBA16Float`；8 位 P3 → `RGBA8Unorm_sRGB`（采样即线性 P3，再由第二条片段管线用 3×3 矩阵 `P3ToExtendedLinearSrgb`（片段 uniform，唯一来源）换算到扩展线性 sRGB）。两种编码共用采样器/UV/方向/mipmap 逻辑，故 F16 代理↔P3 Full 热替换不产生可见色偏。宿主只负责把 `Layer` 挂到视图并调用 `DrawFrame`。 |
| PhotoViewport.cs | 视口纯数学（纯 double）：适应缩放（`FitScale`）、锚点缩放与对焦点居中、平移钳制、导航图尺寸/可见范围（`NavigationSize`/`NavigationViewport`）、`NeedsFull`（物理 100% 或代理不足）；`ApplyGestureView(scale, panX, panY)` 供宿主手势（iOS `UIScrollView` 的 zoomScale/contentOffset）直接写入并统一钳制；`ApplyTexture(textureWidth)` 供**同一张照片的代理↔Full 热替换**只更新纹理宽度、不碰缩放/平移（iPad 与 Mac 共用，Mac 仍用 `ApplyImage`）；`MatchesImage` 供宿主判断是否同一张照片。宿主把结果换算成原生坐标并驱动重绘。 |
| AppleAcceptanceChecks.cs | Mac `--pv-native-smoke` 与 iOS `--pv-native-photo-check` **共用**的验收判定：GPU EXIF 八方向角点核对（导航图线性帧）、色卡中心像素与期望线性 scRGB 值核对（8 位 P3 产物先经 `ColorEngine` 归一到线性 scRGB）、`ColorExpectation` 按编码给出下界（P3 立方体装不下的 Adobe 红原色按 P3 钳位值）。 |
| FilmstripGeometry.cs | 胶片栏几何（96×144 卡片、10/5/10 内边距、横向首项居中、文档长度、可见项范围、FILM-02 居中偏移）与 `FilmstripAnimation`（350ms 三次缓出曲线、进度与 1 点阈值判定）。 |
| MainScreenLayout.cs | 主屏分区几何：文件栏内容宽 116、筛选条五个控件位置（90×32 / 90×27 / 间距 5 / 外边距 10；行布局默认横向居中给 macOS 交通灯让位，`FilterBarFrames(alignLeading: true)` 时从内容前导边 + 10 起排，iPad 用此模式；列布局 116 栏内纵排）、文件栏与控制栏主轴长度、两向分区矩形（文件栏/分隔线/主图/分析栏/控制栏，任一处长度为 0 即隐藏并把空间让给主图）。Mac 与 iPad 原生壳共用同一份数学，平台差异（macOS 隐藏标题栏让位、iOS 真实安全区/无交通灯）留在各自 head。 |
| ControlBarLayout.cs | 控制栏几何：分组类型（EXIF/评分/命令）的子控件尺寸与内边距、分组主轴/交叉轴长度、整组换行/换列、行内居中、文档尺寸与子控件偏移。宿主只落 frame。 |
| AnalysisPanelGeometry.cs | 分析栏瓦片几何：边长 `floor((可用主轴长度 − 20 − 8×(n−1)) / n)`、交叉轴 = 边长 + 20、槽位起点。Mac 与 iPad 原生分析栏共用。 |
| SimilarityPanelGeometry.cs | 相似聚类面板几何：MinWidth 106 / MinHeight 154、外边距 4、列表内边距 8、列布局面板宽（卡片 96 + 内边距 + 描边）与行布局"从胶片栏右侧切出"的宽度规则（卡片数 × 96，封顶可用宽度一半）。Mac 与 iPad 原生面板共用。 |
| ExifTableGeometry.cs | EXIF 详情页 7 列行几何：Tag ID 60 点、其余 2:2:3、列间 1 点分隔线、单元格内边距 10/8、值列最多 3 行；给出卡片宽度下的列宽与可用文本宽。Mac 原生 EXIF 页与 iPad 工具页共用。 |
| AppleInputMapping.cs | 原生事件码 + 修饰键掩码 → 共享 `AppGesture` 映射，供各平台事件解包与 SwiftUI 录制复用同一口径；数值即契约。**HID usage → `GestureKey` 映射表**（iOS `UIKey.KeyCode`，方向/字母/数字/符号/功能键/编辑键/数字键盘/修饰键逐条登记，未登记返回 null）与 `FromHidUsage` 入口；修饰键解码（含苹果键盘映射）与"手势 → 用户命令"查询 `CommandForGesture` 也在共享源，Mac 的 `MacInput.FromEvent/ResolveCommand` 与 iOS `PressesBegan` 共用同一张命令表。 |
| EnhancementAcceptance.cs | 增强预览的共用验收工具：确定性生成低对比度 JPEG（2400×1600，窄亮度区间渐变 + 平缓色块，只写自有沙盒/输出目录）；两帧 BGRA8 的抽样平均/最大绝对差；**`CreateOrientationJpeg(path, orientation, width, height)`** 生成带 EXIF 方向码的四角异色 JPEG（插入最小 EXIF APP1，不依赖第三方写库）；**`CornerSignatures(image)`** 给导航帧四角分类（红/蓝/绿/白/灰），用于证明增强前后方向与色相排列不变。Mac `--pv-native-analysis` 与 iOS `--pv-native-analysis-check` 都用它证明"增强前后上屏像素明显变化"（平均差 ≥6、最大差 ≥30）与"增强不旋转照片"，只断言 `EnhancedDisplay` 不再足以防止"点了没反应"回归。 |
| AppleSymbolNames.cs | 业务命令 → SF Symbols 名称字符串映射；系统图标对象的创建留在各平台头部。 |
