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
| Native/MacControlBar.cs | EXIF 信息块、评分与命令三个原生分组；按空间整体换行/换列并居中，超长组用系统滚动；保留评分方向、显隐/顺序、编号评分快捷命令及苹果主次绑定提示。 |
| Native/NativeMainLayoutAcceptance.cs | 显式 --pv-native-smoke --pv-native-layout 验证两向/三尺寸主界面几何、栏位显隐、自动方向、导航图与真实命令；--pv-layout-hold 在 830×650 暂停供系统截图。 |
| Native/LegacyLayoutAcceptance.cs | 显式 --legacy-ui --pv-legacy-layout 采集同配置旧壳的两向/三尺寸实际几何和控件渲染；隔离设置与数据库。 |
| Native/NativeToolbarAcceptance.cs | 显式 --pv-native-smoke --pv-native-toolbar 验证两向/窄宽窗分组几何、信息层级、形状、评分方向、显隐/排序/主次绑定及真实按钮事件；只对自有 toolbar-fixtures 素材执行评分写回。 |
| Native/MacThumbnailItem.cs | NSCollectionView 虚拟化卡片，80 方形图片/选中与缓存描边/评级/抖动。保留 `init`/`initWithNibName:bundle:`/`NativeHandle`/`LoadView` 幂等初始化与 `LiveItems` 托管常驻，防止复用池卡片被 GC 回收后重建 View 导致空缺。 |
| Native/MacFilmstripView.cs | 固定单行/单列布局与可中断的 350ms 三次缓出滚动；业务索引即几何顺序，按项目数扩展主轴；系统减少动态效果直接定位，横向支持纵向鼠标滚轮。 |
| Native/NativeFilmstripAcceptance.cs | 显式 --pv-native-smoke --pv-native-filmstrip 验证 FILM 契约的真实卡片点击、定位条件、动画中间帧/终点/末次请求、用户接管、身份/几何及两向滚动；实际打开筛选菜单核对对齐、间距、勾号与选择事件；只读用户照片。 |
| Native/MacSymbols.cs / MacInput.cs | SF Symbols 语义映射、独立 NSImage 生命周期、AppGesture/苹果键盘映射；不改持久化字码。 |
| Native/MacNativeSettingsPresenter.cs / MacReorderList.cs | DarkAqua 设置窗口：NSSplitViewController 系统侧栏、SourceList 与 SF Symbols、语义色分组表单；视图式 NSTableView 独立勾选/名称/主次快捷键列，逐行 pasteboard writer 与系统行间拖放；工作副本关闭回写。NSObject 子类不可用泛型；delegate 与 .NET 同名事件不能混用。 |
| Native/NativeSettingsAcceptance.cs | 显式 --pv-native-smoke --pv-native-settings 的五分类、深色/系统侧栏、行内录制/清空/取消、勾选、pasteboard writer/排序绑定、窗口宽度回归；采用隔离设置，系统截图从宿主终端取得；真实鼠标拖放须人工验收。 |
| Native/MacUiServices.cs | AppKit/.NET/Rx 调度、NSOpenPanel、bundle 模型资源。 |
| Native/NativeAcceptanceRunner.cs / NativeSnapshots.cs | 显式 --pv-native-smoke 的真实窗口回归、ICC/EXIF 与八方向 GPU 角点验证、设置落盘、评分/CV/DINO/指纹索引三表读写、控件+GPU 帧导出；只写自有测试副本与独立测试设置/数据库。 |

- 图片：共享 PhotoLoadSession 持租约，视图同步上传后只持 GPU 纹理。原生模式不创建 WriteableBitmap 副本；缓存/准入/预取复用 BitmapLoader。
- 主窗口保留隐藏标题栏与方形元素；布局结构、尺寸和边距按 Avalonia 主界面对拍，边框/圆角与适合的字体/菜单保留原生。左右文件栏内容宽 116 点，卡片外框 90×138、带 3 点四周占位为 96×144，缩略图 80 方形；列表边距为左右 10、上 5、下 10。定位遵守 [FILM-01–04](../Design/ui-contract.md)，显隐恢复/换向定位是已登记的 Mac 差异。控制栏分组按主轴边距测量并整组换行/换列，超长组使用 overlay 系统滚动。设置固定深色外观，快捷键与显隐/排序位于同一行，本轮不改设置设计。P3 分析/聚类/工具/增强入口暂不显示，也不在切图时隐式推理。
- 相对命令行路径用启动 PWD 解析，SDK 会改变工作目录；验收输出不能写进 .app，避免破坏签名。
- 帧导出证明原生布局与 GPU 渲染，不等同于 WindowServer 跨屏色彩对拍。真实产品验收项目见 Plans/native-rebuild-phase-2-acceptance.md。
