# Android 液态玻璃主屏方案 1

## 目标与边界

按产品实际用量，移植 [AndroidLiquidGlass（Backdrop）](https://github.com/Kyant0/AndroidLiquidGlass) 中主屏材质所需的实现，形成 Android Views 可用的独立 C# 模块，再以该模块为唯一基础重做主屏材质：栏底真实模糊照片，主屏玻璃控件与示例 `LiquidButton` 同款光学与动画，主屏小型弹出菜单（星级弹窗、筛选下拉、主图长按菜单、「更多」菜单）改为同一套玻璃面板。玻璃取样主屏实际画面，筛选控件正确透出其下滚过的胶片列表，弹出菜单透出其下的照片、栏位与玻璃按钮。

不在范围内：设置与工具半屏保持 Material BottomSheet；相邻玻璃按压融合（SHELL-12 玻璃容器融合）、自适应亮度图标不在本方案；提示类小部件（缩放指示器、「计算中」药丸、提示条、toast）保持现状。主屏几何、命中区、读屏语义与共享布局不变。

## 现状与约束

- 主图是 `SurfaceView` + 自有 EGL14/GLES 渲染器（P3 直通优先），画面在系统合成器的独立图层上，应用界面层取不到其像素；`TextureView` 内容经界面渲染线程合成，API 31 起可被 `RenderNode` 录制取样（见 [BlurView](https://github.com/Dimezis/BlurView) 的平台说明）。
- 小型弹出菜单目前使用三处独立 `PopupWindow` 宿主（主图长按与「更多」共用一处），取不到主窗口画面；iOS 星级弹窗已是根视图上的玻璃面板加全窗透明拦截层（RATING-07@ios），窗口内浮层有同项目先例。
- Backdrop 是 Compose Multiplatform 库，需 Kotlin Compose 编译器插件，无法在 .NET Android Views 工程直接引用；其 Android 实现只依赖公开平台接口：`RenderNode`（API 29）、`RenderEffect`（API 31，模糊与色彩矩阵）、`RuntimeShader`（API 33，折射与高光着色器）。
- 工程最低 API 24；主力验收真机为小米 25128PNA1C 与小米 13（API 33）。

## Backdrop 移植库

### 范围与版本锁定

新建 Android 类库 `PhotoViewer.Android.Backdrop/`（`net10.0-android`，最低 API 24，不引用任何 PhotoViewer 工程），头工程引用。按产品实际用量移植，不要求完整复刻上游 API、示例页或无关组件。上游锁定 `Kyant0/AndroidLiquidGlass@eb4a0ea`（backdrop 2.0.1）与其依赖 `io.github.kyant0:shapes` 1.2.1，均为 Apache-2.0；模块 `AGENTS.md` 记录上游版本、逐文件映射、所用 AGSL 与升级流程（按上游提交差异同步，不在移植层夹带产品逻辑），许可与 NOTICE 随 APK 打包。

| 层 | 上游 | 移植内容 | 落地 |
|---|---|---|---|
| 效果、单层录制与分级 | `backdrop` 的 `LayerBackdrop`、`blur` / `vibrancy` / `colorControls`、`isRenderEffectSupported` / `isRuntimeShaderSupported` | 必要的单层照片录制与取样、模糊、饱和度与版本判断 | 阶段 2 |
| 多层与光学 | `backdrop` 实际用到的 `Backdrop` / `CombinedBackdrop`、`drawBackdrop` 管线、层变换、`lens`、`Highlight`、`Shadow` 等 | 阶段 2 之外的分层录制与组合、变换、光学与高光/阴影；色散、`InnerShadow`、`exportedBackdrop` 等仅在配方或实现实际依赖时纳入 | 阶段 3 |
| 形状 | `shapes` 的 `Capsule` / `RoundedRectangle` | G2 连续曲率、圆角样式与插值，输出 `android.graphics.Path` 与 `ViewOutlineProvider` | 阶段 3 |
| 交互件 | 示例应用 `InteractiveHighlight`、`DragGestureInspector`、`DampedDragAnimation` | 按压进度与位置弹簧、径向按压高光着色器、拖动手势检视（按下即起手、无触控容差，与上游一致） | 阶段 3 |
| 组件行为 | 示例应用 `LiquidButton` 的产品用配方与行为 | 只移植玻璃按钮、合体与星级胶囊实际使用的效果链、高光、形变与弹簧；不承诺完整 `LiquidButton` API；`LiquidToggle` / `LiquidSlider` / `LiquidBottomTabs` 不移植 | 阶段 3 |

所用 AGSL 着色器逐字移植，常量与参数默认值不改。

### Compose 到 Views 的映射

| 上游概念 | Views 对应 |
|---|---|
| `Modifier.layerBackdrop` / `GraphicsLayer` | 录制子树的 `LayerBackdropLayout`（`ViewGroup`），把子视图录进 `RenderNode` 后绘制 |
| `Modifier.drawBackdrop` | 可挂在任意 `View` 上的绘制器，另提供 `BackdropFrameLayout` 便捷容器；参数、回调与默认值同名对应 |
| `layerBlock`（`GraphicsLayerScope`） | 视图渲染变换（平移、缩放、旋转、枢轴），背景按其逆变换取样，与上游 `InverseLayerScope` 同算法 |
| 坐标关系 `localPositionOf` | `View.transformMatrixToGlobal` 求两视图间矩阵 |
| `CompositingStrategy.Offscreen` / 形状裁切 | `RenderNode.SetUseCompositingLayer` 与轮廓裁切 |
| Compose `spring(阻尼比, 刚度, 可见阈值)` | 同一阻尼谐振模型的闭式解 `BackdropSpring`（固定时间点采样，与 Compose / AndroidX 解析解一致） |
| `Dp` / `Density` | `DisplayMetrics` 换算 |

上游的取样规则原样保留：玻璃元素不得位于自己取样的 `LayerBackdrop` 子树内；嵌套与组合背景按上游语义工作。

### 移植与配方验收

- **配方**：产品组件的效果链与参数逐项取自上游「配方与令牌」，所用 AGSL 着色器逐字移植，常量与默认值不改。
- **最小对照（可选）**：若做上游示例对照，仅限同一壁纸、同一参数、同一配方的最小对照，才采用除文字抗锯齿区外通道差不超过 2 的逐像素门槛。
- **产品验收**：实际产品按产品配方、关键画面、三色校准与作者手验验收；不以向 iOS 校准的结果或无上游对应组件的生产截图做逐像素对照。
- **动画曲线**：按压进度、位置回弹在固定时间点采样，与上游 Compose 弹簧的解析解对照。
- **分级**：API 31/32 只有模糊、饱和度与表面色，无折射与着色器高光；API 33+ 完整，与上游 `isRenderEffectSupported` / `isRuntimeShaderSupported` 行为一致。

## 主屏接入

### 主图承载

阶段 2 起，主图视图由 `SurfaceView` 改为 `TextureView`，EGL 窗口表面建在 `SurfaceTexture` 上，纹理、手势、色彩协商与上传路径保留。所有 API 级别统一使用 `TextureView`。缓冲尺寸恒等于视图物理像素尺寸，视图自身不做缩放平移变换，物理 100% 不经二次重采样。

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

接入层在移植库之上提供五个组件，参数全部来自令牌：玻璃按钮（`LiquidButton` 配方）、玻璃合体、玻璃星级胶囊、栏底材质（含 EXIF 衬底）、玻璃弹出面板（示例 Dialog 配方）。现有 `AndroidGlassButton` / `AndroidGlassCombo` / `AndroidRatingControl` / `AndroidBarScrim` / EXIF 块衬底替换为前四个组件，三处 `PopupWindow` 宿主改由玻璃弹出面板承载；三处宿主分别承载星级弹窗、筛选下拉，以及主图长按菜单与「更多」菜单共用的入口。不保留旧的独立色层实现；系统版本回退由组件自身的正式色层外观承担。

简版外观不实施：不产出临时落地形态，产品只保留按系统版本分级的正式回退（含色层），不在临时外观上再拆一次。所有 API 级别使用同一 `TextureView` 宿主，三处弹窗宿主最终都在主窗口内呈现；玻璃控件在布局中的锚点视图、玻璃控件层与窗口内弹出层随阶段 3 与完整材质一起落地。

### 配方与令牌（取自上游示例）

| 组件 | 上游配方 | 本项目调整 |
|---|---|---|
| 玻璃按钮 / 合体 / 星级胶囊 | `vibrancy`（饱和度 1.5）→ `blur` 2dp → `lens` 高度 12dp、强度 24dp；形状 G2 `Capsule`；默认高光（白 50%、Plus、45°、衰减 1、线宽 0.5dp）；默认阴影（半径 24dp、下移 4dp、黑 10%）；按压放大交叉轴 4dp、拖动 `minDim × tanh(0.05 × 偏移 / minDim)` 与方向拉伸 4dp；按压高光整面白 8% + 跟手径向白 15%；弹簧阻尼 0.5、刚度 300 | 表面色 = `color.toolbar.button.tint` × `shell.glass.button.tint.opacity@android`，按 TOOLBAR-10 校准 |
| 玻璃弹出面板 | 深色 Dialog：`colorControls` 饱和度 1.5 → `blur` 8dp → `lens` 高度 24dp、强度 48dp、景深；`Highlight.Plain`；表面 `#121212` 40% | 形状取各弹窗既有圆角令牌（G2 `RoundedRectangle`），不加背景压暗 |
| 栏底材质 | 以示例底栏 `vibrancy` → `blur` 8dp 为起点，不加折射 | 饱和度 `shell.glass.android.bar.vibrancy.saturation`（1.5）、模糊半径 `shell.bar.blur.radius@android`（8dp）与色层按 SHELL-12 三色衬底向 iOS 校准；带衬底 EXIF 叠 `color.toolbar.block.background` × `shell.glass.android.exif.tint.opacity@android`（0.80 为首版起点） |

新增令牌统一放在 `shell.glass.android.*`、`popup.glass.android.*` 两组，`tokens.json` 记录上游出处。

## 界面行为（按阶段审批后写入设计规范）

阶段 2 只写第 1 项里的栏底与带衬底 EXIF；按钮相关的光学与第 2–9 项随阶段 3 审批。

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

### 阶段 0 技术验证（2–3 工作日）

在独立短分支做原型，原型代码不合入 `main`，按三组分开验收：

- **TextureView 宿主**：P3 夹具经 `TextureView` 显示后，整窗 `PixelCopy` 色块与现有 `color` 检查期望一致，记录窗口缓冲为 8 位 P3 或 F16；物理 100% 时整窗回读与源像素逐像素一致；触摸到上屏延迟较 `SurfaceView` 基线增加不超过 1 帧（帧指标对照）。
- **最小照片层取样**：实际照片随缩放、拖动、切图更新，栏底实时模糊；对照 `SurfaceView` 基线无明显掉帧；`zoom` 心跳不超过 250ms。
- **完整多层**：三层嵌套录制下，小米真机 API 33+ 上，按钮下方的照片与滚过的卡片正确折射，面板取到照片、栏位与玻璃按钮；放大态拖动与胶片列表甩动时掉帧率不显著高于 `SurfaceView` 基线。

宿主未过不切换主图；最小照片取样未过不合入阶段 2；仅完整多层未过时，不阻塞阶段 2 的验收。不满足时的备选：保持 `SurfaceView` 主图，仅在 Android 13+ 为叠在胶片列表上的玻璃控件与弹出面板另取界面层分层折射；照片管线不动，但属拼接实现，需重新审批，不能算作原案完成。

#### 阶段 0 实测结论（2026-11-21，`android-glass` 短分支）

- 设备：小米 13（`2211133C`，API 33，1080×2400，120Hz）。标准版与 AI 版 Debug 均构建成功；独立 Backdrop Release 构建 0 警告、0 错误，标准版 APK 与许可扫描通过。
- **宿主初测未全过，已定位并解决**：EGL 缓冲等于视图物理像素，整窗 P3 色块与既有 `color` 期望一致；初测物理 100% 的 8 位整窗回读有 34,011 / 1,859,760 个像素不一致（98.171% 逐字节一致，最大通道差 2，零几何偏移）。同帧对照证明 GL 与整窗的差异逐项相同，不是系统合成或 `PixelCopy` 量化；请求 F16 窗口没有像素或 Graphics 内存变化，不需要该路线。物理 100% 改用 `GL_NEAREST` 后，上传纹理抽样、GL 全区域与整窗全区域均为 0 差异，最终 `glass` 31/31 全 PASS，**阶段 0 门槛通过，可进入后续阶段**。
- 延迟同脚本对照：`SurfaceView` 输入→swap 中位 9.96ms，`TextureView` 10.28ms；到界面层取用的中位差约 7.15ms，低于本机一帧 8.33ms。该口径不含系统合成与光子时间。宿主心跳最大值多轮均低于 250ms。
- 最小照片层取样：初版「正常绘制 + 再录制」漏掉 `TextureView` 像素；改成与 BlurTarget 同序的「录制子树 → 将同一 `RenderNode` 上屏」后，取样空态负对照、切图、缩放、vibrancy、8dp 模糊与心跳全部通过。
- 完整多层：三层录制、照片层像素、场景/屏幕层归属、按钮下方内容、卡片移动后的实时更新、窗口内面板取样及心跳全部通过。阶段 0 仅证明取样机制，不包含折射、高光、G2 形状与弹簧。
- 回归：`perf` 72/72、`input` 29/29、`layout` 93/93 通过；`zoom` 与 `SurfaceView` 基线一样仅失败同三条顶点缓冲断言，不记为本次回归。`color` 在基线与候选均发生过 EGL 就绪后、独立检查开始前的原生退出，未形成稳定的全量结果；候选整窗 P3 由 `glass` 实测覆盖。原生退出当轮日志与退出信息均保留，不合并归因。
- 证据：`artifacts/acceptance/android-glass-phase0/` 下的 `baseline/`、`candidate/` 与 `diagnosis/`（初测/复测报告、同帧对照、截图、Logcat、SurfaceFlinger 与包扫描）。AI 包扫描另受本机既有模型哈希 `b1579…` 不符合当前 `bcba…` 发布契约阻塞，与本方案改动无关。

### 阶段 1 不实施（控件结构与锚点并入阶段 3）

原计划的简版外观（半透明、渐变描边、普通高光，不取主图）不再产出：它只是临时形态，最终产品只保留按系统版本分级的正式回退，先做一遍再拆掉不合算。与之配套的必要控件结构工作——玻璃控件在布局中的锚点视图、玻璃控件层与屏幕层的取样层级、窗口内弹出层——随阶段 3 与完整材质一起落地。本阶段没有交付物，不占排期。

纯外观简版不需要 `TextureView`；真正实时模糊下方照片在本路线必须使用 `TextureView`。对普通 `View` 单独模糊不代表能取到主图。

### 阶段 2 真实栏底与 EXIF 模糊（已交付）

在独立分支把主图迁到 `TextureView` 并接入最小照片层，用 `RenderEffect` 给计划内栏底与带衬底的 EXIF 出真模糊；按钮、合体、星级胶囊与小型弹出菜单保持现有实现。本阶段不做按钮与弹窗材质、不带按压缩放、跟手高光、折射与 G2 形状，也不改弹窗宿主。范围即 SHELL-12 的 Android 栏底与带衬底 EXIF 条款，加上 TOOLBAR-05/10 的 Android 相关条款。

必须处理生命周期、前后台与旋转、缓冲及离屏预算、拖动翻页；`color` / `zoom` / `perf` / `input` / `layout` 不退化，渲染器输出与整窗终态同时核对，未加材质的主图区核物理 100%，复验 P3 与阶段 0 门槛，完成三色衬底校准。主图切换与第一个真实取样场景一起验收、一起合入。

API 31+ 使用真模糊，API 24–30 不取样、保留色层；所有 API 级别保持原案的同一 `TextureView` 宿主，不改为双后端，低版本同样跑主图回归。若真机不能达标，版本分流是另行评估的架构变更。本阶段是中间交付，交付后不等于液态玻璃完成。

#### 阶段 2 实测结论

- **产品材质**：主图在所有 API 级别统一 `TextureView`，包住主图分区的 `LayerBackdropLayout` 是唯一取样源；六个栏底与带衬底 EXIF 共用 `AndroidPhotoMaterial`（API 31+ vibrancy 1.5 → blur 8dp → 令牌色层），API 24–30 不录制、保留色层。主图真实出帧经 `PresentationFrameRendered` 显式驱动照片层重录，材质按渲染代际失效；共享离屏效果层估算（RGBA8）并入 `ExternalFootprint`。
- **回归**（API 33 真机）：标准 Debug 完整 `glass` 41/41，AI Release 完整 `glass` 41/41；`layout` 93/93、`input` 29/29、`perf` 72/72、`color` 119/119；`zoom` 40/3，三条失败与阶段 0 相同（顶点缓冲断言），HIF 心跳 100.3ms。
- **三色衬底**（sRGB 8 位，黑 / 50% 灰 / 白）：栏底 `#212121` / `#3B3B3B` / `#545454`（33/59/84），带衬底 EXIF `#1B1B1B` / `#353535` / `#4E4E4E`（27/53/78）。
- **API 24–30 回退**：**同源码 x86_64 临时验收构建、不含私有原生库**（`libpvraw` / `libpvhevc` / `libheif` 按设计回退），在 API 28 x86_64 AVD 上只跑 `product`：14/14 通过，`tier=ColorLayer`、`RecordsBackdrop=false`、材质绘制计数 0、栏底保持直通色层且不产生低通。该结果只验证色层回退，**正式分发仍只 arm64**，未声称低版本 arm64 真机已通过。
- **已知边界**：AI 版 Debug 的 Assembly Store 在本次真机启动时缺少 `zh-CN/System.Private.CoreLib.resources` 并中止，AI 版真机验收以 Release 为准；该启动/打包问题不属于玻璃断言。按钮、合体、星级胶囊与小型弹出菜单的光学仍属阶段 3。
- 证据：`artifacts/acceptance/android-glass-phase2/`（标准/AI 完整 `glass` 报告与截图、各回归报告、三色读数、API 28 product 报告与 Logcat、普通浏览截图）。

### 阶段 3 完整液态玻璃（7–11 工作日）

同一控件加场景层与屏幕层，以及上游实际用到的折射、高光、G2 形状与按压交互，补齐所有控件与弹出入口。弹窗可拆「入窗行为（可先用纯色）」与「玻璃材质」两批；最终 API 33+ 完整，API 31/32 有饱和度、模糊与色层、无折射与着色器高光，API 24–30 使用色层，所有版本的弹窗最终都在窗口内。

验收沿用原案：`pv-check glass`（层级、锚点与呈现矩形一致、配方取值、分级降级、按压曲线）；`layout` / `input` / `faults` / `filter` / `rating` 中相关断言全 PASS；`zoom` 心跳仍不超过 250ms；读屏模态与焦点归还；弹窗键盘导航、屏内夹取与星级触摸转交通过相关断言；三色校准读数在实际交付时写入 `platforms.md`，并保留作者观感；真机性能与功耗记录；翻页压测零超时；标准版与 AI 版均通过；文档与许可齐备。

#### 阶段 3 实测结论

- **完整材质**：主屏为根视图 → 屏幕层 → 场景层 → 照片层；玻璃锚点保留布局、触摸、滚动、读屏与焦点，控件层只取场景层，窗口内弹层只取屏幕层。圆按钮、合体、星级胶囊与 AI 相似开关已接入 G2、折射、高光、阴影、按压与弹簧；星级只放大不位移不拉伸。主图菜单、筛选下拉与星级弹窗全部为窗口内玻璃面板，`PopupWindow` 已移除。
- **双版本真机**（小米 13，API 33）：标准版 Debug 与 AI 版 Release 的完整 `glass` 均 **60/60**。物理 100% 均为 0 / 1,859,760 像素差异；栏底黑 / 灰 / 白 = 33 / 59 / 84，EXIF = 27 / 53 / 78，栏上按钮 = 63 / 85 / 106.9。弹层效果跨帧复用，整窗截图保留。
- **回归**：标准版 `layout` 93/93、`input` 29/29、`faults` 46/46、`filter` 34/34、`rating` 38/38、`perf` 72/72、`color` 119/119、`main` 38/38、`form` 22/22；AI 版分别为 93/93、29/29、34/34、38/38、24/24，另有 `edition` 12/12。`zoom` 仍为 40/3，三条失败与阶段 0/2 相同（顶点缓冲断言），HIF 实际会话心跳 84.9ms；`glass` 心跳低于 250ms。60 次对比翻页压测零超时。
- **验收辅助**：嵌套层的像素隔离改为移出非被测视图并按原父容器/索引放回，主图与被测材质始终保留；不放宽任何像素断言。弹窗模态、点外不穿透、焦点归还、键盘导航与减少动态效果由 `filter` 34/34 覆盖。
- **构建与许可**：Backdrop Debug / Release 均 0 警告 0 错误，标准版与 AI 版头工程构建通过；最小 Android 库清单保证 Release AAR 合法。标准版与 AI 版 APK 的身份、arm64、模型/许可与签名扫描通过，扫描器 30 项自检通过。
- **未覆盖边界**：API 31/32 与 API 24–30 的分级实现已接线，但本轮只有 API 33 真机结果，低版本回退待复验；作者观感与 TalkBack 实际朗读、真机横屏待验。AI Debug 冷启动原生退出与标准版个别 .NET 程序集加载退出单独记录，未合并归因到玻璃断言。
- 证据：`artifacts/acceptance/android-glass-phase3/`（双版本 `glass` 与回归报告、三色读数、弹窗/产品截图、APK 扫描、60 次翻页记录、启动异常日志）。

估算：截至真模糊（阶段 0 + 阶段 2）8–13 个工作日，含阶段 3 总计 15–24 个工作日，验证后复估。短分支按可发布里程碑合入，不等待完整效果。各阶段行为先审批，再更新该阶段所需的设计规范与令牌，然后实施：阶段 2 是 SHELL-12 的 Android 栏底与带衬底 EXIF，以及 TOOLBAR-05/10 的 Android 相关条款；RATING-07、FILTER-08、MENU-03、FILM-07 的 Android 条目随阶段 3 审批。令牌改动跑生成校验；`platforms.md` 与 `status.md` 只在实际交付时更新，避免在交付前写成现状。

阶段 2 是中间交付，不构成原案完成；原案完成以「界面行为」九项为准，其中 FILM-07 按原文单列定稿、不阻塞主线。缩减或改变最终范围须重新审批。

## 主要改动面

- 新模块：`PhotoViewer.Android.Backdrop/`（移植库、许可声明、模块 AGENTS；如需上游最小对照，素材仅进 Debug）。
- 主图：`Native/AndroidPhotoView*.cs`、`Native/AndroidPhotoRenderer.cs`（仅表面来源与出帧）。
- 主屏与弹出：`Native/AndroidNativeRootView.cs`、`Native/AndroidPhoneChrome.cs`、`Native/AndroidControlBar.cs`、`Native/AndroidControlBarScrollHost.cs`、`Native/AndroidFilterBar.cs`、`Native/AndroidFilmstripView.cs`、`Native/AndroidRatingPopup.cs`、`Native/AndroidUiTheme.cs`、`AndroidNativeActivity.cs`（返回键顺序接新弹出层）。
- 检查：`Native/AndroidNativeChecks.{Color,Zoom,Performance,Input,Layout,Filter,Faults,Rating}.cs`，新增 `glass` 模式。
- 依赖：弹簧由独立 Backdrop 库内的 `BackdropSpring` 闭式解实现，不新增 AndroidX DynamicAnimation 包。
- 设计：`Design/spec/{shell,toolbar,rating,filter,menu,film}.md`、`Design/platforms.md`、`Design/tokens.json`、`Design/status.md`；`PhotoViewer.Android/AGENTS.md`、根 `AGENTS.md` 模块手册。

## 假设

- 移植库只承载上游已有能力；产品差异（悬停、星级胶囊不位移、版本降级外观、弹窗出入场）全部在接入层，以令牌与组件参数表达。
- 按钮常态底色继续按 TOOLBAR-10 向 iOS 校准，校准不含折射与高光。
- 最终所有 API 级别统一使用 TextureView 与窗口内弹出层，不保留 SurfaceView 与 `PopupWindow` 分支。
- 如需上游最小对照，所用素材只进 Debug 构建，不随 Release 分发。
