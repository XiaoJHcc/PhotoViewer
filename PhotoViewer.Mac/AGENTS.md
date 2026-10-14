# PhotoViewer.Mac — 原生与迁移期旧壳

P2 起：Debug 默认原生 AppKit；`--legacy-ui` 显式启动旧壳。Release 默认旧壳，`--native` 启动原生；发布通道由作者产品验收后切换。构建/运行仍走根 DEV.md 的 Tasks。

| 文件/目录 | 职责 |
|---|---|
| Program.cs | 原生/旧壳入口分流，禁止启动原生时再创建 Avalonia 应用。 |
| Core/MacPresentationDecoder.cs | ImageIO JPEG/HEIF/RAW → RGBA16F 扩展线性 sRGB；JPEG 扫描 ICC APP2 后按共享 EXIF 推断无标签色域（ImageIO ProfileName 会自动补值）；源尺寸与未应用 EXIF 方向交渲染器；无内嵌预览的小图子采样。 |
| Core/MacHeifDecoder.cs / MacRawDecoder.cs | 旧壳及已有缩略图路径，维持原有 8 位口径。 |
| Core/MacStorageRefs.cs | 原生本地存储引用，复用共享文件源、评分及元数据服务。 |
| Native/MacNativeApplication.cs | AppKit 生命周期、ReactiveUI builder、平台门面、Finder/Dock/URL 打开接线。 |
| Native/MetalPhotoView.cs | 正确线性层标签、F16 纹理、mipmap、GPU EXIF 方向、物理倍率、原生缩放/平移/惯性与 GPU 帧读回。 |
| Native/MacMainWindow.cs | 隐藏标题栏、系统菜单、两向布局、筛选、可滚动控制栏、原生右键菜单、评分/打开、对焦缩放/导航图；跨屏物理尺寸刷新与全屏/显隐状态保存。 |
| Native/MacThumbnailItem.cs | NSCollectionView 虚拟化卡片，80 方形图片/选中与缓存描边/评级/抖动。必须保留 NativeHandle 构造器供原生复用。 |
| Native/MacFilmstripView.cs | 固定单行/单列布局，业务索引即几何顺序，按项目数扩展滚动主轴；继承 FlowLayout 保留 AppKit ScrollDirection 契约，横向支持纵向鼠标滚轮。 |
| Native/NativeFilmstripAcceptance.cs | 显式 --pv-native-smoke --pv-native-filmstrip 验证真实卡片身份/几何、升降序、全项滚入、手动滚动保持与鼠标横滚；只读用户照片。 |
| Native/MacSymbols.cs / MacInput.cs | SF Symbols 语义映射、独立 NSImage 生命周期、AppGesture/苹果键盘映射；不改持久化字码。 |
| Native/MacNativeSettingsPresenter.cs / MacReorderList.cs | macOS 原生分类侧栏与表单、拖动排序、快捷键录制、工作副本关闭回写。NSObject 子类不可用泛型；delegate 与 .NET 同名事件不能混用。 |
| Native/MacUiServices.cs | AppKit/.NET/Rx 调度、NSOpenPanel、bundle 模型资源。 |
| Native/NativeAcceptanceRunner.cs / NativeSnapshots.cs | 显式 --pv-native-smoke 的真实窗口回归、ICC/EXIF 与八方向 GPU 角点验证、设置落盘、评分/CV/DINO/指纹索引三表读写、控件+GPU 帧导出；只写自有测试副本与独立测试设置/数据库。 |

- 图片：共享 PhotoLoadSession 持租约，视图同步上传后只持 GPU 纹理。原生模式不创建 WriteableBitmap 副本；缓存/准入/预取复用 BitmapLoader。
- 主窗口保留隐藏标题栏与方形元素；设置按 macOS 原生风格，视觉不复刻 Avalonia。P3 分析/聚类/工具/增强入口暂不显示，也不在切图时隐式推理。
- 相对命令行路径用启动 PWD 解析，SDK 会改变工作目录；验收输出不能写进 .app，避免破坏签名。
- 帧导出证明原生布局与 GPU 渲染，不等同于 WindowServer 跨屏色彩对拍。真实产品验收项目见 Plans/native-rebuild-phase-2-acceptance.md。
