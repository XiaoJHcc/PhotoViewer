# PhotoViewer.Android.Backdrop — Backdrop 液态玻璃移植库

> 独立 `net10.0-android` 类库，最低 API 24。只承载 [Kyant0/AndroidLiquidGlass](https://github.com/Kyant0/AndroidLiquidGlass)（Backdrop）与其依赖 [Shapes](https://github.com/Kyant0/Shapes) 向 Android Views 的移植，**不引用任何 PhotoViewer 工程**，不含主屏几何、令牌、产品组件与产品逻辑。头工程以 `ProjectReference` 引用本库；`Properties/AndroidManifest.xml` 是最小 Android 库清单，供 Release AAR 打包。

## 上游锁定

| 项 | 值 |
|---|---|
| 上游仓库 | `https://github.com/Kyant0/AndroidLiquidGlass` |
| 锁定提交 | `eb4a0ea2e17542ea28c2e71088f7dd9acf77aad5` |
| 上游版本 | `io.github.kyant0:backdrop:2.0.1`（`backdrop/build.gradle.kts` 的 `coordinates`） |
| 形状仓库 | `https://github.com/Kyant0/Shapes`，版本 `1.2.1`（标签提交 `ad83e4ad9ca6219cb0091fdc75d9b349865bf12b`），Maven 坐标 `io.github.kyant0:shapes:1.2.1` |
| 许可 | 两个作品均为 Apache License 2.0；Backdrop Copyright 2025 Kyant，Shapes Copyright 2026 Kyant。`LICENSE` 与 Backdrop 上游逐字节相同，同一份文本覆盖两个作品，归属见 `NOTICE` |
| 上游最低 API | Backdrop `minSdk = 21`；本模块按产品口径取 24 |

## 逐文件映射

| 上游（相对仓库根） | 本模块 | 移植程度 |
|---|---|---|
| `backdrop/src/androidMain/kotlin/com/kyant/backdrop/Platform.kt` | `BackdropCapabilities.cs` | `isRenderEffectSupported` = API 31（S）、`isRuntimeShaderSupported` = API 33（Tiramisu）逐条照搬；另加分级 `BackdropTier` 与诊断单行文本 |
| `backdrop/src/commonMain/kotlin/com/kyant/backdrop/Backdrop.kt` | `IBackdrop.cs` | 取样源抽象；`drawBackdrop(density, coordinates, layerBlock)` 改为 `DrawBackdrop(canvas, context)`，层变换放在 `BackdropDrawContext.LayerTransform` |
| `backdrop/src/commonMain/kotlin/com/kyant/backdrop/backdrops/EmptyBackdrop.kt` | `EmptyBackdrop.cs` | 空取样源 |
| `backdrop/src/commonMain/kotlin/com/kyant/backdrop/backdrops/LayerBackdrop.kt` | `LayerBackdrop.cs` | 单层取样：`translate(-localPositionOf(coordinates))` 等价为 `translate(层窗口原点 − 画布窗口原点)`；先按 `BackdropLayerTransform` 做逆变换；只持有源层录制节点，效果层归消费者 |
| `backdrop/src/commonMain/kotlin/com/kyant/backdrop/backdrops/CombinedBackdrop.kt` | `CombinedBackdrop.cs` | 2 个、3 个与可变参数组合合并为一份 `params IBackdrop[]`；按顺序叠加、坐标相关性取或；不持有节点，带效果时由消费者效果层把合成结果整体录制 |
| `backdrop/src/commonMain/kotlin/com/kyant/backdrop/backdrops/LayerBackdropModifier.kt` | `LayerBackdropLayout.cs` | `Modifier.layerBackdrop` → `FrameLayout` 派生容器：整棵子树录进 `RenderNode`，再把同一节点绘制上屏（与 BlurTarget 同序，确保 `TextureView` 也走录制路径） |
| `backdrop/src/commonMain/kotlin/com/kyant/backdrop/BackdropEffectScope.kt` | `BackdropEffectScope.cs` + `BackdropEffect.cs` | `Density` / `size` / `padding` / `renderEffect` / `shape` 与 `apply` / `reset` 同义；`build` → `Build()` 返回不可变 `BackdropEffect` 快照。**每个绘制消费者一份 scope**，scope 持有一张可复用效果层，快照携带内部所有者，`Dispose` 释放 |
| `backdrop/src/commonMain/kotlin/com/kyant/backdrop/effects/Blur.kt` | `BackdropEffectScope.Blur` | `BlurEffect(renderEffect, r, r, tileMode)` 等价 `createChainEffect(blur, previous)`；`padding` 提升条件（非 `Clamp` 或已有前序效果）逐条照搬 |
| `backdrop/src/commonMain/kotlin/com/kyant/backdrop/effects/ColorFilter.kt` | `BackdropEffectScope.ColorFilter` / `ColorControls` / `Vibrancy` / `Opacity` | `colorControlsColorFilter` 的矩阵逐项搬运（亮度权重 0.213/0.715/0.072，中心点偏移 `(0.5 − c/2 + brightness) × 255`），`vibrancy` = 饱和度 1.5 |
| `backdrop/src/commonMain/kotlin/com/kyant/backdrop/effects/Lens.kt` | `BackdropEffectScope.Lens` | 折射高度先从 `padding` 扣除，圆角半径取 `Shape` 四角，uniform 名与取值顺序照搬；非圆角形状抛与上游相同的不支持异常；含色散分支 |
| `backdrop/src/commonMain/kotlin/com/kyant/backdrop/effects/RenderEffect.kt` | `BackdropEffectScope.Effect` / `RuntimeShaderEffect` | `effect` 追加平台效果；`runtimeShaderEffect` 按 key 复用着色器、设置 uniform 后 `createRuntimeShaderEffect` |
| `backdrop/src/commonMain/kotlin/com/kyant/backdrop/internal/RenderEffect.kt` + `androidMain/.../internal/RenderEffect.kt` | `BackdropEffectScope`（内联） | `chain` / `ColorFilterEffect` / `RuntimeShaderEffect` 直接落到对应 `RenderEffect` 工厂 |
| `backdrop/src/androidMain/kotlin/com/kyant/backdrop/RuntimeShader.kt` + `commonMain/.../RuntimeShader.kt` | `BackdropRuntimeShader.cs` | 包装 `android.graphics.RuntimeShader`；float / int / color uniform 全部重载；`PlatformShader` 供 `Paint.SetShader` 与效果输入 |
| `backdrop/src/commonMain/kotlin/com/kyant/backdrop/RuntimeShaderCache.kt` | `BackdropRuntimeShaderCache` | 按 key 复用；`clear` 只丢引用、不主动释放平台对象（与上游一致） |
| `backdrop/src/commonMain/kotlin/com/kyant/backdrop/internal/InverseLayerScope.kt` | `BackdropLayerTransform.cs` | 只保留旋转与缩放字段；`rotationZ == 0` 时 `scale(1/scaleX, 1/scaleY)`，否则按上游 2×2 矩阵求逆；原点固定在左上角，平移不参与逆变换 |
| `backdrop/src/commonMain/kotlin/com/kyant/backdrop/internal/Shaders.kt` | `BackdropShaders.cs` | 圆角矩形 SDF、折射、色散折射、默认高光、环境高光逐字搬运 |
| `backdrop/src/commonMain/kotlin/com/kyant/backdrop/internal/ShapeProvider.kt` | `BackdropShapeOutline.cs` | 按形状 / 尺寸 / 密度缓存 `Path` 与四角半径，逐帧复用 |
| `backdrop/src/commonMain/kotlin/com/kyant/backdrop/internal/Outline.kt` | `BackdropShapeOutlineProvider.cs` | 矩形 / 圆角矩形 / 一般路径三分支；API 30+ 用 `Outline.SetPath`，低版本用 `SetConvexPath` |
| `backdrop/src/commonMain/kotlin/com/kyant/backdrop/internal/Paint.kt` + `androidMain/.../internal/Paint.kt` | `BackdropPaint.cs`（及绘制器内联） | 模糊走 `BlurMaskFilter`，着色器走 `Paint.SetShader`；上游 `BlendMode` 映射为 Porter-Duff，`Plus` = `Add` |
| `backdrop/src/commonMain/kotlin/com/kyant/backdrop/internal/LayerRecorder.kt` | `LayerBackdropLayout.cs` / `BackdropEffectLayer.cs` | `recordLayer(size, block)` 的「按给定尺寸录制」落到 `RenderNode.BeginRecording`；源层录制用容器实测尺寸，消费者效果层用 `context.WidthPx / HeightPx + 2 × padding`；Views 侧以像素坐标绘制，不需要注入 Compose 的 `drawContext.density` |
| `backdrop/src/commonMain/kotlin/com/kyant/backdrop/highlight/Highlight.kt` | `BackdropHighlight.cs` | 线宽 0.5dp、模糊半径取线宽一半、整体不透明度 1；`Default` / `Ambient` / `Plain` 三个预置 |
| `backdrop/src/commonMain/kotlin/com/kyant/backdrop/highlight/HighlightStyle.kt` | `BackdropHighlightStyle.cs` | `Plain` 白 38%、`Default` 白 50% / Plus / 45° / 衰减 1、`Ambient` 强度 0.38；着色器 uniform 与默认值照搬 |
| `backdrop/src/commonMain/kotlin/com/kyant/backdrop/highlight/HighlightModifier.kt` | `BackdropHighlightRenderer.cs` | 沿轮廓向内描边（`ceil(widthPx.coerceAtMost(minDim/2)) × 2`），裁到形状内；图层 alpha 与混合模式用 `Canvas.saveLayer` 承载 |
| `backdrop/src/commonMain/kotlin/com/kyant/backdrop/shadow/Shadow.kt` | `BackdropShadow.cs` | 半径 24dp、下移半径/6、黑 10%、整体不透明度 1、默认混合模式 |
| `backdrop/src/commonMain/kotlin/com/kyant/backdrop/shadow/ShadowModifier.kt` | `BackdropShadowRenderer.cs` | 离屏层按 `(ceil(w + 4r + dx), ceil(h + 4r + dy))` 录，层内画形状与清除遮罩，再画在 `(−2r, −2r)` |
| `backdrop/src/commonMain/kotlin/com/kyant/backdrop/DrawBackdropModifier.kt` | `BackdropDrawer.cs` + `BackdropEffectLayer.cs` | 只移植「按效果链绘制背景」与玻璃元素自己的离屏效果层归属；高光/阴影由独立绘制器按该节点的先后关系组合，`onDrawBehind/Surface/Front`、`exportedBackdrop` 由接入层自行处理 |
| `app/src/commonMain/kotlin/com/kyant/backdrop/catalog/utils/InteractiveHighlight.kt` | `BackdropInteractiveHighlight.cs` | 按压进度与位置弹簧（默认阻尼 0.5、刚度 300，阈值 0.001 / 0.01）、整面白 8% + 跟手径向白 15%、按下即起手、移动 `snapTo`、抬手弹回；颜色/半径/弹簧/动画开关可由产品令牌参数化，默认值保持上游；另提供只画径向高光的 `DrawRadial` 供接入层悬停态使用；AGSL 按 Android Paint 的预乘 alpha 契约输出 |
| `app/src/commonMain/kotlin/com/kyant/backdrop/catalog/utils/DragGestureInspector.kt` | `BackdropPressTracker.cs` | `Initial` + 主通道两次 Down 的语义落到 `MotionEvent`：Down 起手、Move 跟手、PointerUp 换手、Up 结束、Cancel 取消；不检查触控容差 |
| `app/src/commonMain/kotlin/com/kyant/backdrop/catalog/components/LiquidButton.kt` | `BackdropInteractiveTransform.cs` | 只搬运 `layerBlock` 的形变公式：交叉轴按压放大、`maxOffset × tanh(initialDerivative × offset / maxOffset)`、按方向投影的 X/Y 拉伸；4dp、0.05 等由调用方按配方给出 |
| `shapes/src/commonMain/kotlin/com/kyant/shapes/RoundedRectangularShape.kt` / `Capsule.kt` / `RoundedRectangle.kt` / `RoundedCornerStyle.kt` / `RectangleCornerRadii.kt` | `BackdropShape.cs` | `BackdropShape`、`BackdropCornerRadii`、`BackdropCornerStyle`、`Capsule`、`RoundedRectangle`、插值形状；圆角以 dp 给出，按密度换算 |
| `shapes/src/commonMain/kotlin/com/kyant/shapes/RoundedRectangleOutline.kt` / `ContinuousCurvatureRoundedRectangleCornerBuilder.kt` | `BackdropShapeGeometry.cs` | 矩形 / 普通圆角 / G2 连续曲率三分支，三次贝塞尔点求解常量与公式逐项照搬 |
| `shapes/src/commonMain/kotlin/com/kyant/shapes/Lerp.kt` | `BackdropShape.Lerp` | 比例为 0/1 返回原对象；其余按四角半径插值并夹取，样式按上游规则选取 |

未移植：`backdrops/CanvasBackdrop.kt`、`backdrops/Backdrop.kt`（`onDraw` 包装）、`shadow/InnerShadow*`、
`DrawBackdropModifier` 的 `onDrawBehind/Surface/Front` 与 `exportedBackdrop`、示例的
`LiquidToggle` / `LiquidSlider` / `LiquidBottomTabs` / `DampedDragAnimation` / 自适应亮度 / SDF 工具。
这些不属于当前配方需要的能力；内阴影的默认值为空，按钮与弹窗配方都不依赖它。

## AGSL 着色器

折射和边缘着色器按上游移植；径向交互高光输出预乘 alpha，`Paint` 不透明度保持为 1，强度只由着色器的颜色 uniform 控制。

| 上游 | 着色器 | 本模块常量 | 用途 | 最低 API |
|---|---|---|---|---|
| `internal/Shaders.kt` | `RoundedRectSDF` | `BackdropShaders.RoundedRectSdf` | 圆角矩形有向距离场与梯度，供下面四个着色器拼接 | 33（仅被着色器使用） |
| `internal/Shaders.kt` | `RoundedRectRefractionShaderString` | `BackdropShaders.RoundedRectRefraction` | 透镜折射，uniform `content` / `size` / `offset` / `cornerRadii` / `refractionHeight` / `refractionAmount` / `depthEffect` | 33 |
| `internal/Shaders.kt` | `RoundedRectRefractionWithDispersionShaderString` | `BackdropShaders.RoundedRectRefractionWithDispersion` | 带色散折射，另有 `chromaticAberration` | 33 |
| `internal/Shaders.kt` | `DefaultHighlightShaderString` | `BackdropShaders.DefaultHighlight` | 默认高光，uniform `size` / `cornerRadii` / `color` / `angle` / `falloff` | 33 |
| `internal/Shaders.kt` | `AmbientHighlightShaderString` | `BackdropShaders.AmbientHighlight` | 环境高光，只取朝向光源一侧 | 33 |
| `app/.../utils/InteractiveHighlight.kt` | 内联径向高光 | `BackdropShaders.InteractiveHighlight` | 按压跟手径向白 15%，uniform `size` / `color` / `radius` / `position` | 33 |

模糊、饱和度、`colorControls`、`opacity` 仍走平台 `RenderEffect.createBlurEffect` 与
`ColorMatrixColorFilter`，没有 AGSL。API 33 以下折射与着色器高光静默跳过；高光退回纯色描边
（上游同样 `setRuntimeShader(null)`），与分级表一致。

## 形状（Shapes 1.2.1）

- `BackdropCapsule` 半径恒为短边一半；`BackdropRoundedRectangle` 半径以 dp 给出并在 `[0, minDimension/2]` 夹取；默认 `BackdropCornerStyle.Continuous`（G2），`Circular` 走普通圆弧。
- 路径分支与上游一致：零半径为矩形；`Circular` 或正方形且四角顶满时输出圆角矩形；其余用 G2 三次曲线。四角不同的形状走不等圆角路径；`BackdropShape.Lerp` 逐角插值并夹取。
- `BackdropShapeOutline` 缓存同一尺寸下的 `Path` 与四角半径，供裁切、描边、折射与阴影共用；尺寸/密度/形状变化时重建。
- `BackdropShapeOutlineProvider` 把形状交给 `View.OutlineProvider`：矩形用 `SetRect`，四角相同且普通圆弧（或正方形顶满）用 `SetRoundRect`，其余用 `SetPath`（API 30+）或 `SetConvexPath`（API 24–29）。
- Shapes 的许可与归属见 `NOTICE`；同一份 Apache-2.0 文本覆盖 Backdrop 与 Shapes。

## 分级

| 分级 | API | 取样 | 效果 |
|---|---|---|---|
| `BackdropTier.ColorLayer` | 24–30 | 不取样 | 无；调用方画正式色层 |
| `BackdropTier.Blur` | 31–32 | 单层/多层 `RenderNode` 取样 | 模糊 + 饱和度 + 表面色；无折射、无着色器高光 |
| `BackdropTier.Full` | 33+ | 同上 | 另有 `RuntimeShader`：折射、色散、着色器高光与跟手高光 |

`BackdropCapabilities.IsLayerRecordingSupported`（API 29）单列：`RenderNode` 与
`Canvas.drawRenderNode` 从 29 起可用，但产品口径下 31 以下不取样，保留该属性只为如实上报平台能力。
低于 31 时效果链方法全部静默跳过（与上游 `if (!isRenderEffectSupported()) return` 一致），配方代码可跨版本共用。

## Views 侧的对应关系

- **录制**：`LayerBackdropLayout.DispatchDraw` 用 `RenderNode.BeginRecording(w, h)` 得到的 `RecordingCanvas` 绘制背景与整棵子树，再把同一个 `RenderNode` 绘制上屏。上屏与取样来自同一份显示列表，不会漏掉只走硬件录制路径的 `TextureView`，也避免每帧两次遍历。软件画布（离屏 `View.draw`、`Bitmap` 回读）不录制：`Canvas.drawRenderNode` 只支持硬件画布。
- **多层**：`CombinedBackdrop` 按数组顺序调用各源，各源共用同一份 `BackdropDrawContext`；它不持有节点。带效果链时，消费者的效果层把组合结果整体录进自己的本地节点，效果作用在合成结果上，避免逐源模糊后叠加。嵌套的 `LayerBackdropLayout` 仍各有自己的源层录制。
- **取样**：`LayerBackdrop.DrawBackdrop` 先按 `context.LayerTransform` 追加逆变换，再按
  `translate(层窗口原点 − 画布窗口原点)` 画节点，等价上游 `withTransform { inverseTransform; translate(-localPositionOf) }`。
  `BackdropDrawContext.ForView` 从视图自身读 `ScaleX` / `ScaleY` / `Rotation`，也提供
  `ForView(view, buffer, layerTransform)` 与 `WithLayerTransform` 供画布矩阵变换使用。逆变换原点固定在左上角，
  平移不参与逆变换，视图在窗口中的位置已含平移，取样原点差把它抵消；滚动容器偏移同样由 `GetLocationInWindow` 反映。**取样会把整层画出来**，调用方必须先在画布上裁到自己的边界或形状，否则取样内容会溢出。
- **效果层归属**：`BackdropEffectScope` 每个绘制消费者一份，内部持有一张可复用效果层；`Build()` 的快照携带该所有者。`BackdropEffectLayer` 只按本次消费者建节点：宽 `ceil(context.WidthPx + 2 × padding)`，高同理（padding 为浮点），与源层大小无关。录制时先 `translate(padding)`，裁切形状在消费者坐标，再把源层按「源层窗口原点 − 消费者画布原点」画进去；元素缩放/旋转只在录制背景取样时求逆，上屏只做 `translate(-padding)` 并继续跟随元素自身的画布变换。于是 Lens 的 `size`（消费者尺寸）与节点内本地坐标一致，控件不在源层原点也不影响。节点复用，不逐帧新建；手动 `new BackdropEffect(...)` 会新建一份效果层，逐帧绘制应使用 `BackdropEffectScope.Build()`。
- **效果重录**：`LayerBackdrop` 在每次源层录制和 `Invalidate()` 时递增内容代际；`CombinedBackdrop` 汇总子源代际。消费者效果层只在源代际、消费者原点/尺寸、层变换、padding、效果引用变化时重录显示列表，重录后重设一次 `RenderEffect`；带裁切形状时每次都重录（`Path` 可变）。效果节点创建时 `SetUseCompositingLayer(true)`，不依赖当前是否存在可见消费者。调用方按密度与尺寸缓存配方快照（见用法骨架），本库不逐帧重建 `RenderEffect`。`Build(paddingOverridePx)` 可让矩形消费者以自身边界作为 Clamp 边缘；按钮等需要配方外扩的组件仍使用默认 `Build()`。
- **RuntimeShader**：`BackdropRuntimeShaderCache` 按 key 复用着色器。uniform 是着色器对象上的状态，同一帧内多个组件不能共用一份缓存，否则后设的 uniform 会覆盖先前的绘制；每个绘制器/组件各持一份。AGSL 需 API 33，着色器对象可同时作为 `Paint.SetShader` 与 `RenderEffect` 输入。
- **形状与裁切**：`BackdropDrawer.Draw(..., Path clipPath)` 的路径用玻璃元素坐标，录制时先加 `padding` 再 `ClipPath`，即内容先被裁切再上效果；高光裁在 `BackdropShapeOutline.Path` 内，阴影按同一轮廓在形状外生成。接入层逐帧复用同一个 `BackdropShapeOutline`，避免重复构造 G2 路径。
- **高光**：`BackdropHighlightRenderer.Draw` 在形状内描边，`Canvas.saveLayer` 承载整体 alpha 与混合模式；线宽、模糊、角度、衰减与上游同名参数对应。`Plain` 无着色器，`Default` / `Ambient` 在 API 33+ 用 AGSL，33 以下退回纯色。
- **阴影**：`BackdropShadowRenderer.Draw` 在离屏层内先画模糊形状、再用清除模式挖掉本体，最后把层画在形状外；投影可以超出视图边界时由宿主提供不裁切的绘制范围。
- **按压交互**：`BackdropPressTracker` 不检查触控容差，Down 即起手，Move 跟手，PointerUp 换手，Up/Cancel 收尾；它应挂在承担布局/触摸/读屏的锚点视图上，玻璃材质视图的视觉变换不影响触点局部坐标（上游指针坐标同样不受 `graphicsLayer` 影响）。`BackdropInteractiveHighlight` 用闭式解弹簧推进按压进度与位移，颜色、径向半径与弹簧参数缺省取上游默认、可由调用方按令牌覆盖；`DrawRadial` 只画跟手径向高光，供悬停等接入层差异。`BackdropInteractiveTransform` 按上游 `layerBlock` 公式给出缩放、拉伸与橡皮筋位移。帧由接入层驱动，本库不持有视图或计时器。
- **生命周期**：`BackdropEffectScope.Dispose()` 丢弃它持有的效果节点与着色器缓存，之后快照与 scope 都不可再用；`BackdropEffectScope.Reset()` 只重置效果链，不释放节点。`CombinedBackdrop` 不持有资源，不需要 `Dispose`。`LayerBackdropLayout` 释放自己的源层节点。高光/阴影绘制器各自 `Dispose`。
- **每消费者一份 scope**：同一源层上的不同位置/尺寸控件必须各自持有 `BackdropEffectScope`。共用一份 scope（例如六处栏底共用一个材质对象）会让同一张效果节点在同一帧内被后画的消费者改写，先画的消费者在渲染时读到后者的内容。公共 API 无法从 `BackdropDrawContext` 可靠地猜出消费者身份，归属由调用方按控件建立。
- **`TileMode`**：Java 枚举在绑定里是类而非常量，故本模块自带 `BackdropTileMode` 四值并映射到平台实例。
- **取样顺序约束**：玻璃元素不得位于它所取样的 `LayerBackdropLayout` 子树内；取样方在层之后绘制时本来就读到本帧内容，层内容变化而取样方未被失效时订阅 `Recorded` 并让自己失效，可在下一次遍历跟上（最多一帧延迟）。

## 用法骨架

```csharp
// 场景层：照片 + 栏位（不含玻璃控件）
var scene = new LayerBackdropLayout(context);
root.AddView(scene);
// …往 scene 里放主图与栏内容

// 栏底材质：每个取样视图各持一份 scope 与快照（不要跨控件共用）。
private readonly BackdropEffectScope _effects = new();
private readonly int[] _location = new int[2];
private BackdropEffect _effect = BackdropEffect.None;
private (float Density, int Width, int Height) _effectKey = (-1f, 0, 0);

// 只在配方参数、密度或取样尺寸变化时重建。
private void RefreshEffect(BackdropDrawContext context)
{
    var key = (context.Density, (int)context.WidthPx, (int)context.HeightPx);
    if (key == _effectKey) return;

    _effectKey = key;
    _effects.Apply(scope =>
    {
        scope.Vibrancy();
        scope.Blur(scope.DpToPx(8f));
    }, context.Density, context.WidthPx, context.HeightPx);
    _effect = _effects.Build();
}

protected override void OnDraw(Canvas canvas)
{
    if (BackdropCapabilities.Tier == BackdropTier.ColorLayer)
    {
        canvas.DrawColor(TokenColor);        // 正式色层外观
        return;
    }

    var context = BackdropDrawContext.ForView(this, _location);
    RefreshEffect(context);

    int save = canvas.Save();
    try
    {
        canvas.ClipRect(0, 0, Width, Height);
        BackdropDrawer.Draw(canvas, _scene.Backdrop, _effect, context);
    }
    finally
    {
        canvas.RestoreToCount(save);
    }

    canvas.DrawColor(TokenColor);            // 表面色层
}

// 视图销毁时：_effects.Dispose();
```

六处栏底与带衬底 EXIF 若共用一个材质对象，必须把 `BackdropEffectScope` 与快照拆到每个取样视图；离屏占用按每个消费者节点（各自尺寸 + 2 × padding）之和估算，不按源层整幅尺寸计。

玻璃按钮（上游 `LiquidButton` 配方）在形状、效果、阴影与高光上的接法：

```csharp
var density = Resources?.DisplayMetrics?.Density ?? 1f;
var shape = new BackdropCapsule();
var outline = new BackdropShapeOutline(shape);
var effects = new BackdropEffectScope { Shape = shape }; // 每个控件一份
var shadow = new BackdropShadowRenderer();
var highlight = new BackdropHighlightRenderer();

// 尺寸/密度变化后、每帧绘制前：
outline.Update(Width, Height, density);
effects.Apply(scope =>
{
    scope.Vibrancy();
    scope.Blur(scope.DpToPx(2f));
    scope.Lens(scope.DpToPx(12f), scope.DpToPx(24f));
}, density, Width, Height);
var effect = effects.Build();
var context = BackdropDrawContext.ForView(this, _location);

shadow.Draw(canvas, outline, BackdropShadow.Default);   // 内容之前
int save = canvas.Save();
canvas.ClipPath(outline.Path);
BackdropDrawer.Draw(canvas, scene.Backdrop, effect, context);
canvas.DrawColor(surfaceColor);                         // 令牌表面色在接入层
canvas.RestoreToCount(save);
highlight.Draw(canvas, outline, BackdropHighlight.Default); // 内容之后

// 视图销毁时：effects.Dispose(); shadow.Dispose(); highlight.Dispose();
```

## 升级流程

1. 取上游新提交的 `backdrop/` 与 `shapes/` 子树（`git log --oneline <旧 sha>..<新 sha> -- backdrop`），逐文件对照本表判断是否有移植范围内改动。
2. 只看上表「本模块」列涉及的文件；未移植清单中的组件不因升级而自动进入本库。
3. 逐条同步到对应 C# 文件：效果链顺序、`padding` 提升条件、色彩矩阵常量、透镜 uniform 与着色器源码均以锁定上游为基准；交互高光保留上文 Android Paint 的预乘适配。
4. 形状公式与常量按 Shapes 标签逐项对照；输出仍是 `android.graphics.Path` 与 `ViewOutlineProvider`，不引入 Compose 类型。
5. 交互弹簧参数、起手/跟手/换手语义与 `layerBlock` 形变公式按示例同步；产品差异留在接入层。
6. 更新本文件的上游提交、版本与映射表；在真机（API 31/32 与 API 33+）各跑一次分级回归，确认 `BackdropCapabilities.Describe()` 与实际外观一致。
7. 上游许可若变化，同步 `LICENSE` 与 `NOTICE`。

## 许可与打包

`LICENSE`（Apache-2.0 全文，与 Backdrop 上游逐字节相同）与 `NOTICE`（两个作品的归属、锁定版本、逐文件来源与修改说明）随模块入库，必须随 APK 分发。库工程的 `AndroidAsset` 不保证被头工程继承，因此头工程需要显式打包：

```xml
<ItemGroup>
  <AndroidAsset Include="..\PhotoViewer.Android.Backdrop\LICENSE"
                Link="Assets\Licenses\AndroidLiquidGlass-LICENSE.txt" />
  <AndroidAsset Include="..\PhotoViewer.Android.Backdrop\NOTICE"
                Link="Assets\Licenses\AndroidLiquidGlass-NOTICE.txt" />
</ItemGroup>
```

`Tools/check-android-package.py` 需把这两项加进 APK 扫描清单（与 DINOv3 License 同法）。同一份 Apache-2.0 文本覆盖 Backdrop 与 Shapes，`NOTICE` 里分别注明。

## 构建与验证

本仓库默认 SDK 可能没有 android workload（`PhotoViewer.Android/AGENTS.md`「构建」）。在任一临时目录放 `{"sdk":{"version":"10.0.201","rollForward":"disable"}}`，从该目录构建：

```bash
cd /tmp/pv-sdk201
dotnet build /path/to/PhotoViewer.Android.Backdrop/PhotoViewer.Android.Backdrop.csproj -c Debug
```

模块自身不含真机断言。头工程的 `pv-check glass` 是唯一验收入口：缺省 `all` = `host` + `product`。

- `product`：**正式产品材质验收**（`PhotoViewer.Android/Native/AndroidNativeChecks.Glass.Product.cs`），跑在主屏现有照片层、场景层与屏幕层上，覆盖三层结构、6 个栏底、带衬底 EXIF、玻璃控件与窗口内弹层的结构/配方/分级、锚点/按压/焦点/回弹，以及黑/灰/白衬底、1px 条纹低通、阶跃边缘展开和切图/缩放跟随。
- `sample` / `layers`：显式 legacy 机制检查（`AndroidNativeChecks.Glass.Backdrop.cs`），只在点名时运行，用临时装配的 `AndroidPhotoView` + `LayerBackdropLayout` 舞台取证、收尾整体拆除；不作为产品材质验收证据。

## 边界与已知限制

- 不含产品逻辑：令牌、几何、命中区、读屏、悬停、产品自己的按压/滚动弹簧参数、弹窗入窗行为全部在接入层。本库只提供上游已有能力的 Views 形态。
- 只画宿主交给它的层：`SurfaceView` 内容在系统合成器的独立图层上、不在界面渲染层，录不到；`TextureView` 子视图被 `RenderNode` 录制时会带上纹理（产品主图依赖这一点），本模块不对其他视图组合与平台版本作保证。
- 子树上屏与取样共用同一次 `RenderNode` 录制；嵌套层仍各有自己的录制与合成成本，性能结论只在真机测。
- `LayerBackdropLayout` 拥有自己的源层 `Backdrop`，视图被释放（`Dispose`）时一并回收 `RenderNode`；消费者效果层归各自的 `BackdropEffectScope`，两者互不代管。
- 层内容变化仍需让消费者视图重绘（订阅 `LayerBackdropLayout.Recorded` 或显式 `Invalidate`）；源层每次完成新录制即递增内容代际，消费者效果层在消费者重绘时自行重录，不需要逐控件失效效果层。消费者位置/尺寸或 padding 变化、效果引用变化、裁切形状变化时同样重录；`LayerBackdrop.Invalidate()` 只用于外部改动了源层内容且没走正常录制的情况。
- 逆变换只补偿玻璃元素自身的缩放与旋转（左上角原点），不补偿平移，与上游 `InverseLayerScope` 相同；祖先容器的缩放/旋转不在补偿范围内。
- 高光与阴影通过 `Canvas.saveLayer` 绘制，画布裁剪仍由宿主决定；阴影半径外扩超出视图时，接入层需提供不裁切的绘制范围。
- 低版本不提供半透明自绘替代：分级为 `ColorLayer` 时调用方必须画正式色层。API 31/32 没有折射与着色器高光，高光退回纯色描边。
- 未移植的 `InnerShadow`、`CanvasBackdrop`、`exportedBackdrop` 与示例组件不提供兼容桩，需要时按上游新增文件与映射表同步。
- 效果层的归属由调用方保证：公共 API 不猜测消费者身份，也不能从 `BackdropDrawContext` 唯一识别一个控件。共用 scope 的效果层会在同一帧内互相覆盖，这是接入错误，不是库内可自动修复的情况。
