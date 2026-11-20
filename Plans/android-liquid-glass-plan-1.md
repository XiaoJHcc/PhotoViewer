# Android 液态玻璃主屏方案 1

## 目标与边界

把 [AndroidLiquidGlass（Backdrop）](https://github.com/Kyant0/AndroidLiquidGlass) 完整、准确地移植为 Android Views 可用的独立 C# 库，再以该库为唯一基础重做主屏材质：栏底真实模糊照片，主屏玻璃控件与示例 `LiquidButton` 同款光学与动画，主屏小型弹出菜单（星级弹窗、筛选下拉、主图长按菜单、「更多」菜单）改为同一套玻璃面板。玻璃取样主屏实际画面，筛选控件正确透出其下滚过的胶片列表，弹出菜单透出其下的照片、栏位与玻璃按钮。

不在范围内：设置与工具半屏保持 Material BottomSheet；相邻玻璃按压融合（SHELL-12 玻璃容器融合）、自适应亮度图标不在本方案；提示类小部件（缩放指示器、「计算中」药丸、提示条、toast）保持现状。主屏几何、命中区、读屏语义与共享布局不变。

## 现状与约束

- 主图是 `SurfaceView` + 自有 EGL14/GLES 渲染器（P3 直通优先），画面在系统合成器的独立图层上，应用界面层取不到其像素；`TextureView` 内容经界面渲染线程合成，API 31 起可被 `RenderNode` 录制取样（见 [BlurView](https://github.com/Dimezis/BlurView) 的平台说明）。
- 三个小型弹出菜单目前都是独立 `PopupWindow`，取不到主窗口画面；iOS 星级弹窗已是根视图上的玻璃面板加全窗透明拦截层（RATING-07@ios），窗口内浮层有同项目先例。
- Backdrop 是 Compose Multiplatform 库，需 Kotlin Compose 编译器插件，无法在 .NET Android Views 工程直接引用；其 Android 实现只依赖公开平台接口：`RenderNode`（API 29）、`RenderEffect`（API 31，模糊与色彩矩阵）、`RuntimeShader`（API 33，折射与高光着色器）。
- 工程最低 API 24；主力验收真机为小米 25128PNA1C 与小米 13（API 33）。

## 移植库

### 范围与版本锁定

新建 Android 类库 `PhotoViewer.Android.Backdrop/`（`net10.0-android`，最低 API 24，不引用任何 PhotoViewer 工程），头工程引用。上游锁定 `Kyant0/AndroidLiquidGlass@eb4a0ea`（backdrop 2.0.1）与其依赖 `io.github.kyant0:shapes` 1.2.1，均为 Apache-2.0；模块 `AGENTS.md` 记录上游版本、逐文件映射与升级流程（按上游提交差异同步，不在移植层夹带产品逻辑），许可与 NOTICE 随 APK 打包。

| 层 | 上游 | 移植内容 |
|---|---|---|
| 核心库 | `backdrop` 模块全部（约 2550 行 Kotlin） | `Backdrop` 接口与 `LayerBackdrop` / `CombinedBackdrop` / `CanvasBackdrop` / `EmptyBackdrop`；`drawBackdrop` 与 `drawPlainBackdrop` 的完整绘制管线（外阴影 → 层变换 → `onDrawBehind` → 效果后的背景层 → `onDrawSurface` → 内容 → `onDrawFront`，高光与内阴影按上游顺序）；`exportedBackdrop`；效果作用域的 `blur` / `lens`（含景深与色散）/ `colorControls` / `vibrancy` / `colorFilter` / `opacity` / `effect` / `runtimeShaderEffect` 与 padding 语义；`Highlight`（Default / Ambient / Plain）、`Shadow`、`InnerShadow` 与插值；着色器缓存；`isRenderEffectSupported` / `isRuntimeShaderSupported` 版本分级 |
| 形状库 | `shapes` 全部（约 1070 行） | G2 连续曲率 `Capsule` / `RoundedRectangle` / `UnevenRoundedRectangle`、圆角样式与插值，输出 `android.graphics.Path` 与 `ViewOutlineProvider` |
| 交互件 | 示例应用 `InteractiveHighlight`、`DragGestureInspector`、`DampedDragAnimation` | 按压进度与位置弹簧、径向按压高光着色器、拖动手势检视（按下即起手、无触控容差，与上游一致） |
| 组件 | 示例应用 `LiquidButton` | 完整移植；`LiquidToggle` / `LiquidSlider` / `LiquidBottomTabs` 本方案不使用，暂不移植，后续可在同一基础上补齐 |

AGSL 着色器逐字移植，常量与参数默认值不改。

### Compose 到 Views 的映射

| 上游概念 | Views 对应 |
|---|---|
| `Modifier.layerBackdrop` / `GraphicsLayer` | 录制子树的 `LayerBackdropLayout`（`ViewGroup`），把子视图录进 `RenderNode` 后绘制 |
| `Modifier.drawBackdrop` | 可挂在任意 `View` 上的绘制器，另提供 `BackdropFrameLayout` 便捷容器；参数、回调与默认值同名对应 |
| `layerBlock`（`GraphicsLayerScope`） | 视图渲染变换（平移、缩放、旋转、枢轴），背景按其逆变换取样，与上游 `InverseLayerScope` 同算法 |
| 坐标关系 `localPositionOf` | `View.transformMatrixToGlobal` 求两视图间矩阵 |
| `CompositingStrategy.Offscreen` / 形状裁切 | `RenderNode.SetUseCompositingLayer` 与轮廓裁切 |
| Compose `spring(阻尼比, 刚度, 可见阈值)` | AndroidX `SpringAnimation` / `SpringForce`（同一阻尼谐振模型） |
| `Dp` / `Density` | `DisplayMetrics` 换算 |

上游的取样规则原样保留：玻璃元素不得位于自己取样的 `LayerBackdrop` 子树内；嵌套与组合背景按上游语义工作。

### 移植准确性验收

- **对照目录页**：仅 Debug 的目录页，用上游示例同一张壁纸与同一参数复刻 Buttons、Dialog、Glass Playground（固定参数）、Scroll Container 四页；同一真机、同一密度下与上游示例 APK 截图逐像素比较，除文字抗锯齿区外通道差不超过 2。
- **动画曲线**：按压进度、位置回弹在固定时间点采样，与上游 Compose 弹簧的解析解对照。
- **分级**：API 31/32 只有模糊与色彩矩阵、API 33+ 完整，与上游 `isRenderEffectSupported` / `isRuntimeShaderSupported` 行为一致。

## 主屏接入

### 主图承载

主图视图由 `SurfaceView` 改为 `TextureView`，EGL 窗口表面建在 `SurfaceTexture` 上，纹理、手势、色彩协商与上传路径保留。所有 API 级别统一使用 `TextureView`。缓冲尺寸恒等于视图物理像素尺寸，视图自身不做缩放平移变换，物理 100% 不经二次重采样。

已知代价：手指到画面约多 1 帧延迟；照片每帧变化都要 GPU 全屏合成，功耗上升；P3 直通改为经界面渲染器色彩管理；部分设备广色域窗口可能使用 F16 缓冲（窗口显存翻倍）；未来 HDR 显示需另行评估。

### 取样层级

```text
根视图
├─ 屏幕层（LayerBackdrop）
│  ├─ 场景层（LayerBackdrop）
│  │  ├─ 照片层（LayerBackdrop）：TextureView 主图
│  │  ├─ 栏底材质：取照片层
│  │  ├─ 栏内容：胶片列表、分析栏、相似面板、EXIF 块（衬底取照片层）
│  │  └─ 提示类叠层
│  └─ 玻璃控件层：圆按钮、合体、星级胶囊、命令按钮、相似开关，取场景层
└─ 弹出层：全窗透明拦截层 + 玻璃面板，取屏幕层
```

玻璃控件在原有布局中保留锚点视图，承担布局、触摸、滚动、读屏与焦点；玻璃控件层中的对应视图按锚点真实屏幕矩形（含滚动宿主裁切）绘制材质、图标与按压效果。这是满足上游取样规则的接入方式，属于接入层，不进入移植库。

### 产品组件

接入层在移植库之上提供五个组件，参数全部来自令牌：玻璃按钮（`LiquidButton` 配方）、玻璃合体、玻璃星级胶囊、栏底材质（含 EXIF 衬底）、玻璃弹出面板（示例 Dialog 配方）。现有 `AndroidGlassButton` / `AndroidGlassCombo` / `AndroidRatingControl` / `AndroidBarScrim` / EXIF 块衬底与三个 `PopupWindow` 替换为这五个组件，不保留旧色层实现。

### 配方与令牌（取自上游示例）

| 组件 | 上游配方 | 本项目调整 |
|---|---|---|
| 玻璃按钮 / 合体 / 星级胶囊 | `vibrancy`（饱和度 1.5）→ `blur` 2dp → `lens` 高度 12dp、强度 24dp；形状 G2 `Capsule`；默认高光（白 50%、Plus、45°、衰减 1、线宽 0.5dp）；默认阴影（半径 24dp、下移 4dp、黑 10%）；按压放大交叉轴 4dp、拖动 `minDim × tanh(0.05 × 偏移 / minDim)` 与方向拉伸 4dp；按压高光整面白 8% + 跟手径向白 15%；弹簧阻尼 0.5、刚度 300 | 表面色 = `color.toolbar.button.tint` × `shell.glass.button.tint.opacity@android`，按 TOOLBAR-10 校准 |
| 玻璃弹出面板 | 深色 Dialog：`colorControls` 饱和度 1.5 → `blur` 8dp → `lens` 高度 24dp、强度 48dp、景深；`Highlight.Plain`；表面 `#121212` 40% | 形状取各弹窗既有圆角令牌（G2 `RoundedRectangle`），不加背景压暗 |
| 栏底材质 | 以示例底栏 `vibrancy` → `blur` 8dp 为起点，不加折射 | 模糊半径 `shell.bar.blur.radius@android` 与色层按 SHELL-12 三色衬底向 iOS 校准 |

新增令牌统一放在 `shell.glass.android.*`、`popup.glass.android.*` 两组，`tokens.json` 记录上游出处。

## 界面行为（待审批后写入设计规范）

1. **栏底**：文件栏与胶片侧栏、工具区、手机顶行与底行背景由纯深灰色层改为真实模糊下方照片再叠色层，按纯黑 / 50% 灰 / 纯白衬底向 iOS 校准；有衬底的 EXIF 块同样改为真模糊。
2. **主屏玻璃控件**：手机形态圆按钮、筛选/更多合体、星级胶囊，宽形态筛选合体、命令圆按钮、星级胶囊与 AI 版相似聚类开关，按玻璃按钮配方绘制；胶片列表从筛选控件下方滚过时，卡片经玻璃折射可见。
3. **触摸按压**：与上游一致，按下即起手，取消涟漪；放大、随手指橡皮筋位移与方向拉伸、整面与跟手高光，松手弹簧回弹。合体整块胶囊一起形变，跟手高光落在手指处。星级胶囊只放大与跟手高光，不位移不拉伸，不影响滑动选星与紧凑态起手转交星级弹窗。可滚动控制栏内的按钮在越过系统触控容差后交给滚动并回弹。
4. **鼠标/触控板悬停**：上游没有悬停态；悬停显示同一跟手径向高光，强度为按压的一半，不放大不形变。键盘焦点沿用现有状态层。
5. **禁用**：玻璃照常绘制，内容按 `color.toolbar.disabled.content.alpha` 降低，无按压动画。
6. **小型弹出菜单**：星级弹窗、筛选下拉、主图长按菜单与「更多」菜单改为主窗口内的玻璃面板，面板透出其下的照片、栏位与玻璃按钮；位置、尺寸、内容、方向与各自既有规则不变（RATING-04/07、FILTER-08、MENU-01/03/06）。打开时铺全窗透明拦截层：点面板外关闭且该次点按不穿透；返回键关闭顺序不变；读屏焦点移入面板，背景内容对读屏隐藏，关闭后焦点回到入口。出入场为淡入淡出，时长取系统 `config_shortAnimTime`；菜单项悬停/按下沿用 `color.menu.item.hover/pressed`，星排逐格状态沿用 RATING-07。卡片长按与紧凑态起手的同一次触摸继续转交弹窗星排。
7. **减少动态效果**（系统动画时长缩放为 0）：不放大、不位移，高光即显即隐，弹出菜单无淡入淡出。
8. **系统版本**：Android 13 及以上完整液态玻璃；Android 12 只有饱和度、模糊与表面色，无折射与着色器高光；Android 11 及以下不取样，栏底、按钮与弹出面板使用当前色层外观，弹出菜单同样在窗口内。
9. **胶片列表边缘**：横屏胶片栏从入口合体下方滚过时的边缘过渡（FILM-07）在设计阶段单独定稿，不阻塞主线。

## 阶段与验收

| 阶段 | 内容 | 验收 | 估时 |
|---|---|---|---|
| 0 技术验证 | 独立分支：TextureView 承载现有渲染器；三层嵌套录制；一个玻璃按钮叠在胶片列表上；一个窗口内玻璃面板叠在玻璃按钮上 | 见「放行条件」，任一不满足即停，回报重新评估 | 2–3 天 |
| 1 移植库 | 核心库、形状库、交互件与 `LiquidButton` 完整移植；对照目录页 | 「移植准确性验收」三项通过 | 6–8 天 |
| 2 设计规范 | 改 SHELL-12、TOOLBAR-05/10、RATING-07、FILTER-08、MENU-03、FILM-07 的 Android 条目与 platforms.md；新增令牌；Android 由色层口径改为登记移植玻璃组件 | 令牌生成校验通过；规范无旧口径残留 | 1–1.5 天 |
| 3 主图迁移 | `AndroidPhotoView` 改 TextureView 宿主、表面生命周期与尺寸变化、出帧节奏；缓冲与离屏层计入外部占用；`color`/`zoom`/`perf`/`input`/`layout` 检查迁移，色彩与清晰度同时核对渲染器输出与整窗 `PixelCopy` 终态 | 迁移前后各检查模式 PASS 数不减；P3 夹具终态与期望一致；物理 100% 逐像素一致 | 4–6 天 |
| 4 主屏接入 | 三层录制与玻璃控件层、锚点同步（含控制栏滚动与胶片列表）；五个产品组件替换旧实现；手势冲突规则 | 新增 `pv-check glass`（层级、锚点与呈现矩形一致、配方取值、分级降级、按压曲线）；`layout`/`input`/`faults` 全 PASS | 4–5 天 |
| 5 弹出菜单入窗 | 三个 `PopupWindow` 改窗口内玻璃面板：拦截层、返回键、读屏模态与焦点归还、键盘导航、屏内夹取、出入场、触摸转交 | `filter`/`faults`/`rating` 中弹窗相关断言迁移后全 PASS；读屏模态与焦点归还新增断言 | 4–5 天 |
| 6 校准与真机 | 三色衬底校准栏底与按钮常态；真机观感对照上游示例 APK；性能与功耗记录 | 校准读数写入 platforms.md；`zoom` 心跳维持 250ms 门槛；翻页压测零超时；作者手验观感 | 4–5 天 |
| 7 收尾 | Android AGENTS、移植库 AGENTS、platforms、status；许可声明；清理旧实现与旧口径 | 文档无旧口径与补丁式描述 | 1 天 |

合计约 27–35 个工作日。

### 放行条件（阶段 0）

- 小米真机 API 33+ 上，玻璃按钮叠在胶片列表上时照片与卡片都被正确折射；窗口内面板叠在玻璃按钮上时三者都被正确取样。
- P3 夹具经 TextureView 显示后，整窗 `PixelCopy` 色块与现有 `color` 检查期望一致；记录窗口缓冲格式（8 位 P3 或 F16）。
- 物理 100% 时整窗回读与源像素逐像素一致，无重采样发软。
- 触摸到上屏延迟较 SurfaceView 基线增加不超过 1 帧（帧指标对照）。
- 放大态拖动、胶片列表甩动在玻璃开启时，掉帧率不显著高于 SurfaceView 基线；`zoom` 心跳不超过 250ms。

不满足时的备选：保持 SurfaceView 主图，仅在 Android 13+ 为叠在胶片列表上的玻璃控件与弹出面板另取界面层分层折射。该方案照片管线不动但属拼接实现，需重新审批；移植库不受影响。

## 主要改动面

- 新模块：`PhotoViewer.Android.Backdrop/`（移植库、对照目录页素材仅 Debug、许可声明、模块 AGENTS）。
- 主图：`Native/AndroidPhotoView*.cs`、`Native/AndroidPhotoRenderer.cs`（仅表面来源与出帧）。
- 主屏与弹出：`Native/AndroidNativeRootView.cs`、`Native/AndroidPhoneChrome.cs`、`Native/AndroidControlBar.cs`、`Native/AndroidControlBarScrollHost.cs`、`Native/AndroidFilterBar.cs`、`Native/AndroidFilmstripView.cs`、`Native/AndroidRatingPopup.cs`、`Native/AndroidUiTheme.cs`、`AndroidNativeActivity.cs`（返回键顺序接新弹出层）。
- 检查：`Native/AndroidNativeChecks.{Color,Zoom,Performance,Input,Layout,Filter,Faults,Rating}.cs`，新增 `glass` 模式。
- 依赖：`Directory.Packages.props` 新增 `Xamarin.AndroidX.DynamicAnimation`（NuGet 现有 1.1.0.6）。
- 设计：`Design/spec/{shell,toolbar,rating,filter,menu,film}.md`、`Design/platforms.md`、`Design/tokens.json`、`Design/status.md`；`PhotoViewer.Android/AGENTS.md`、根 `AGENTS.md` 模块手册。

## 假设

- 移植库只承载上游已有能力；产品差异（悬停、星级胶囊不位移、版本降级外观、弹窗出入场）全部在接入层，以令牌与组件参数表达。
- 按钮常态底色继续按 TOOLBAR-10 向 iOS 校准，校准不含折射与高光。
- 所有 API 级别统一使用 TextureView 与窗口内弹出层，不保留 SurfaceView 与 `PopupWindow` 分支。
- 对照目录页使用的上游壁纸只进 Debug 构建，不随 Release 分发。
