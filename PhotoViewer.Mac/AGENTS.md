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
| Native/MacSymbols.cs / MacInput.cs | SF Symbols 语义映射、独立 NSImage 生命周期、AppGesture/苹果键盘映射；不改持久化字码。`MacInput.FromEvent` 必须按 `NSEventType` 只读对应字段（鼠标/键盘读取 `KeyCode`/`ButtonNumber`/`ScrollingDeltaY`/`CharactersIgnoringModifiers` 的无关项会抛 `NSInternalInconsistencyException` 并被 ObjC 桥吞掉），再复用 `FromRaw` 的同一映射口径（SwiftUI 录制按字段拆包走 `FromRaw`）。 |
| Native/MacNativeSettingsPresenter.cs / MacSettingsBridge.cs | macOS 26 设置窗口的 C# 侧：持有工作副本（NativeSettingsSession）、校验与持久化，通过 JSON 状态快照 + 动作回传驱动 SwiftUI 界面，派生文本（性能预算/缓存提示/快捷键符号/冲突/清库状态）与窗口历史导航全在 C# 计算后推入；保留桌面端快捷键录制判定、苹果键盘映射与清库。MacSettingsBridge 定义 camelCase DTO、source-generated JSON 契约与 libMacSettingsUI.dylib 的 C ABI 动态绑定（NativeLibrary + `delegate* unmanaged[Cdecl]` + `@UnmanagedCallersOnly` 回调）。工作副本关闭回写，不改 JSON 字段。 |
| Native/SwiftSettings/MacSettingsUI.swift | 设置窗口 SwiftUI 实现（macOS 26 `NavigationSplitView` + `Form(.grouped)` 系统设置外观：侧栏彩色圆角 SF Symbol 底板、工具栏前进/后退、分页标题；内嵌可排序列表的行高与留白统一由 `SettingsMetrics` 规范——单行 38 点、含副标题 54 点，对齐原生 `Form` 行高）。以 `@_cdecl` 导出 C ABI；自身 `ObservableObject` 承接 C# 状态，交互以 JSON 动作回传；`NSEvent.addLocalMonitorForEvents` 捕获真实键/鼠标/滚轮手势回传 C# 判定（录制事件只读与事件类型相关的字段，合成事件的滚轮增量可能为 NaN 故手工拼装 JSON）。由 csproj 的 `_BuildSwiftSettingsDylib` 目标编译进 `MonoBundle` 并交 `_FixDylibCodeSigning` 签名。 |
| Native/NativeSettingsAcceptance.cs | 显式 --pv-native-smoke --pv-native-settings 验证 SwiftUI 设置窗口：深色外观 + NSHostingController 挂载、五分类分组表单渲染、分页历史前进后退、勾选动作往返并触发重绘、真实 NSEvent 录制/删除清空/Escape 取消、文件格式/快捷键/EXIF 三处排序动作、窗口缩放、关闭回写与重开恢复；并尝试系统 screencapture 取真实窗口位图（无屏幕录制权限时在报告中如实记录）。采用隔离设置；真实鼠标拖放与手感仍须人工验收。 |
| Native/NativeInputAcceptance.cs | 显式 --pv-native-smoke --pv-native-input（不加载照片）验证主图区域输入：`MacInput.FromEvent` 对鼠标/键盘/抬起事件不抛异常且映射到预期手势、空首屏提示覆盖主图中心且不拦截点击、方向键经共享命令表被消费、左键打开原生文件选择器（桩实现免模态）、右键弹出原生上下文菜单（菜单代理捕获 MenuWillOpen）。冒烟进程非最前台窗口时不派发首击，故点击直接投递主图视图。 |
| Native/MacUiServices.cs | AppKit/.NET/Rx 调度、NSOpenPanel、bundle 模型资源。 |
| Native/NativeAcceptanceRunner.cs / NativeSnapshots.cs | 显式 --pv-native-smoke 的真实窗口回归、ICC/EXIF 与八方向 GPU 角点验证、设置落盘、评分/CV/DINO/指纹索引三表读写、控件+GPU 帧导出；只写自有测试副本与独立测试设置/数据库。 |

- 图片：共享 PhotoLoadSession 持租约，视图同步上传后只持 GPU 纹理。原生模式不创建 WriteableBitmap 副本；缓存/准入/预取复用 BitmapLoader。
- 主窗口保留隐藏标题栏与方形元素；布局结构、尺寸和边距按 Avalonia 主界面对拍，边框/圆角与适合的字体/菜单保留原生。左右文件栏内容宽 116 点，卡片外框 90×138、带 3 点四周占位为 96×144，缩略图 80 方形；列表边距为左右 10、上 5、下 10。定位遵守 [FILM-01–04](../Design/ui-contract.md)，显隐恢复/换向定位是已登记的 Mac 差异。控制栏分组按主轴边距测量并整组换行/换列，超长组使用 overlay 系统滚动。**设置窗口独立于主窗口**：固定深色外观，由 SwiftUI（libMacSettingsUI.dylib）承载 macOS 26 系统设置风双栏表单，快捷键与显隐/排序位于同一行；主窗口本轮设计不变。P3 分析/聚类/工具/增强入口暂不显示，也不在切图时隐式推理。
- 相对命令行路径用启动 PWD 解析，SDK 会改变工作目录；验收输出不能写进 .app，避免破坏签名。
- 帧导出证明原生布局与 GPU 渲染，不等同于 WindowServer 跨屏色彩对拍。真实产品验收项目见 Plans/native-rebuild-phase-2-acceptance.md。
