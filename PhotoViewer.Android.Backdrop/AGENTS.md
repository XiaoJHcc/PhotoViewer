# PhotoViewer.Android.Backdrop — Backdrop 液态玻璃移植库

> 独立 `net10.0-android` 类库，最低 API 24。只承载 [Kyant0/AndroidLiquidGlass](https://github.com/Kyant0/AndroidLiquidGlass)（Backdrop）向 Android Views 的移植，**不引用任何 PhotoViewer 工程**，不含主屏几何、令牌、产品组件与产品逻辑。头工程已以 `ProjectReference` 引用本库（阶段 0 起先跑取样检查，产品材质在阶段 2/3 接入）。

## 上游锁定

| 项 | 值 |
|---|---|
| 上游仓库 | `https://github.com/Kyant0/AndroidLiquidGlass` |
| 锁定提交 | `eb4a0ea2e17542ea28c2e71088f7dd9acf77aad5` |
| 上游版本 | `io.github.kyant0:backdrop:2.0.1`（`backdrop/build.gradle.kts` 的 `coordinates`） |
| 依赖（未移植） | `io.github.kyant0:shapes:1.2.1`（`gradle/libs.versions.toml` 的 `kyantShapes`），G2 形状只在阶段 3 才需要；源码仓库 `https://github.com/Kyant0/Shapes`，Maven Central 的 POM 声明 Apache License 2.0、作者 Kyant |
| 许可 | Apache License 2.0，Copyright 2025 Kyant；`LICENSE` 与上游逐字节相同，归属见 `NOTICE` |
| 上游最低 API | 21（`minSdk = 21`），本模块按产品口径取 24 |

## 逐文件映射

| 上游（相对仓库根） | 本模块 | 移植程度 |
|---|---|---|
| `backdrop/src/androidMain/kotlin/com/kyant/backdrop/Platform.kt` | `BackdropCapabilities.cs` | `isRenderEffectSupported` = API 31（S）、`isRuntimeShaderSupported` = API 33（Tiramisu）逐条照搬；另加分级 `BackdropTier` 与诊断单行文本 |
| `backdrop/src/commonMain/kotlin/com/kyant/backdrop/Backdrop.kt` | `IBackdrop.cs` | 取样源抽象；`drawBackdrop(density, coordinates, layerBlock)` 改为 `DrawBackdrop(canvas, context)`，`layerBlock` 属阶段 3 |
| `backdrop/src/commonMain/kotlin/com/kyant/backdrop/backdrops/EmptyBackdrop.kt` | `EmptyBackdrop.cs` | 空取样源 |
| `backdrop/src/commonMain/kotlin/com/kyant/backdrop/backdrops/LayerBackdrop.kt` | `LayerBackdrop.cs` | 单层取样：`translate(-localPositionOf(coordinates))` 等价为 `translate(层窗口原点 − 画布窗口原点)`；效果经离屏 `RenderNode` 承载 |
| `backdrop/src/commonMain/kotlin/com/kyant/backdrop/backdrops/LayerBackdropModifier.kt` | `LayerBackdropLayout.cs` | `Modifier.layerBackdrop` → `FrameLayout` 派生容器：整棵子树录进 `RenderNode`，再把同一节点绘制上屏（与 BlurTarget 同序，确保 TextureView 也走录制路径） |
| `backdrop/src/commonMain/kotlin/com/kyant/backdrop/BackdropEffectScope.kt` | `BackdropEffectScope.cs` + `BackdropEffect.cs` | `Density` / `size` / `padding` / `renderEffect` 与 `apply` / `reset` 同义；`build` → `Build()` 返回不可变 `BackdropEffect` 快照 |
| `backdrop/src/commonMain/kotlin/com/kyant/backdrop/effects/Blur.kt` | `BackdropEffectScope.Blur` | `BlurEffect(renderEffect, r, r, tileMode)` 等价 `createChainEffect(blur, previous)`；`padding` 提升条件（非 `Clamp` 或已有前序效果）逐条照搬 |
| `backdrop/src/commonMain/kotlin/com/kyant/backdrop/effects/ColorFilter.kt` | `BackdropEffectScope.ColorFilter` / `ColorControls` / `Vibrancy` / `Opacity` | `colorControlsColorFilter` 的矩阵逐项搬运（亮度权重 0.213/0.715/0.072，中心点偏移 `(0.5 − c/2 + brightness) × 255`），`vibrancy` = 饱和度 1.5 |
| `backdrop/src/commonMain/kotlin/com/kyant/backdrop/effects/RenderEffect.kt` | `BackdropEffectScope.Effect` | 只保留 `effect(effect)`（追加已有平台效果）；`runtimeShaderEffect` 属阶段 3 |
| `backdrop/src/androidMain/kotlin/com/kyant/backdrop/internal/RenderEffect.kt` | `BackdropEffectScope`（内联） | `chain` / `ColorFilterEffect` 两个平台桥接直接落到 `RenderEffect.CreateChainEffect` / `CreateColorFilterEffect` |
| `backdrop/src/commonMain/kotlin/com/kyant/backdrop/DrawBackdropModifier.kt` | `BackdropDrawer.cs` | 只移植「背景层 + 效果链」的最小段落；高光、阴影、内阴影、`exportedBackdrop`、`onDrawBehind/Surface/Front` 回调与 `layerBlock` 均未移植 |

未移植（阶段 3 范围，`Plans/android-liquid-glass-plan-1.md`）：`backdrops/Backdrop.kt`、`backdrops/CanvasBackdrop.kt`、`backdrops/CombinedBackdrop.kt`、`effects/Lens.kt`、`effects/RenderEffect.kt` 的着色器分支、`internal/{Shaders,InverseLayerScope,ShapeProvider,Outline,Paint}.kt`、`highlight/*`、`shadow/*`、`RuntimeShader` 与 `RuntimeShaderCache`、以及示例应用的 `LiquidButton` / 交互件。`shapes` 的 `Capsule` / `RoundedRectangle` 同样未移植。

## AGSL 着色器

最小效果链（模糊、饱和度、`colorControls`、`opacity`）**不含任何 AGSL**：模糊走平台 `RenderEffect.createBlurEffect`，色彩走 `RenderEffect.createColorFilterEffect` + `ColorMatrix`，与上游同样是平台能力而非着色器。上游的 AGSL 字面量只存在于阶段 3 才移植的文件：

| 上游文件 | 着色器 | 阶段 |
|---|---|---|
| `effects/Lens.kt` | 透镜折射与景深（含 `Lens` 常量） | 阶段 3 |
| `internal/Shaders.kt` | 高光、阴影、内阴影的共用着色器 | 阶段 3 |
| `highlight/HighlightStyle.kt` / `HighlightModifier.kt` | `Highlight.Default` / `Highlight.Plain` 配方与径向高光 | 阶段 3 |

这些着色器移植时必须逐字搬运，常量与参数默认值不改；移植时同步更新本表。

## 分级

| 分级 | API | 取样 | 效果 |
|---|---|---|---|
| `BackdropTier.ColorLayer` | 24–30 | 不取样 | 无；调用方画正式色层 |
| `BackdropTier.Blur` | 31–32 | 单层 `RenderNode` 取样 | 模糊 + 饱和度 + 表面色；无折射、无着色器高光 |
| `BackdropTier.Full` | 33+ | 同上 | 另有 `RuntimeShader`，供阶段 3 的折射与高光 |

`BackdropCapabilities.IsLayerRecordingSupported`（API 29）单列：`RenderNode` 与 `Canvas.drawRenderNode` 从 29 起可用，但产品口径下 31 以下不取样，保留该属性只为如实上报平台能力。低于 31 时效果链方法全部静默跳过（与上游 `if (!isRenderEffectSupported()) return` 一致），配方代码可跨版本共用。

## Views 侧的对应关系

- **录制**：`LayerBackdropLayout.DispatchDraw` 用 `RenderNode.BeginRecording(w, h)` 得到的 `RecordingCanvas` 绘制背景与整棵子树，再把同一个 `RenderNode` 绘制上屏。上屏与取样来自同一份显示列表，不会漏掉只走硬件录制路径的 `TextureView`，也避免每帧两次遍历。软件画布（离屏 `View.draw`、`Bitmap` 回读）不录制：`Canvas.drawRenderNode` 只支持硬件画布。
- **取样**：`LayerBackdrop.DrawBackdrop` 按 `translate(层窗口原点 − 画布窗口原点)` 画节点，等价上游 `translate(-localPositionOf)`。`BackdropDrawContext.ForView` 从视图自身 `OnDraw` 取密度与画布原点，滚动容器偏移由 `GetLocationInWindow` 反映。**取样会把整层画出来**（不是只画本视图那一块），调用方必须先在画布上裁到自己的边界或形状（`canvas.ClipRect(0, 0, Width, Height)` / `ClipPath`），否则取样内容会溢出到别处。
- **效果**：有 `RenderEffect` 时经一张离屏 `RenderNode`：按 `padding` 四面外扩、把层节点画到内偏移处、效果挂在离屏节点上，于是模糊能取到边界外像素（对应上游把 `renderEffect` 挂在加大后的 `GraphicsLayer`）。离屏节点的**显示列表在源层每次完成新录制后重录一次**（`SetRecordedBounds` 置脏，复用同一个 `RenderNode`），不只依赖「引用同一个源节点自动跟随」——多帧实测里效果节点的缓存层可能继续复用上一次的取样输出。重录之后（或效果引用变化时）重新设置一次 `RenderEffect`，不假设 `BeginRecording` / `SetPosition` 会保留节点上原有的效果；同帧未重录且引用相同的其他取样方不重复设置。效果节点创建时调用 `SetUseCompositingLayer(true)`，使效果链不依赖当前是否存在可见消费者。效果链对象本身仍按密度与取样尺寸缓存，不逐帧重建。
- **`CompositingStrategy.Offscreen` / 形状裁切**：`BackdropDrawer.Draw(..., Path clipPath)` 在离屏录制内 `ClipPath`，即内容先被裁切再上效果，与上游 `GraphicsLayer(clip = true, shape = …)` 同序。
- **`TileMode`**：Java 枚举在绑定里是类而非常量，故本模块自带 `BackdropTileMode` 四值并映射到平台实例。
- **取样顺序约束**：玻璃元素不得位于它所取样的 `LayerBackdropLayout` 子树内；取样方在层之后绘制时本来就读到本帧内容，层内容变化而取样方未被失效时订阅 `Recorded` 并让自己失效，可在下一次遍历跟上（最多一帧延迟）。

## 用法骨架

```csharp
// 场景层：照片 + 栏位（不含玻璃控件）
var scene = new LayerBackdropLayout(context);
root.AddView(scene);
// …往 scene 里放主图与栏内容

// 栏底材质：在任意视图的 OnDraw 里取样（binding 的 OnDraw 形参可空，按头工程习惯处理）
private readonly BackdropEffectScope _effects = new();
private readonly int[] _location = new int[2];
private BackdropEffect _effect = BackdropEffect.None;
private (float Density, int Width, int Height) _effectKey = (-1f, 0, 0);

/// 只在配方参数、密度或取样尺寸变化时重建。每帧 Apply + Build 会逐帧新建 RenderEffect，
/// 那是白扔的分配与校验开销；配方常量属于接入层，不进本库。
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

    // 取样画的是整层：先裁到本视图（或形状），save/restore 成对。
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
```

## 升级流程

1. 取上游新提交的 `backdrop/` 子树（`git log --oneline <旧 sha>..<新 sha> -- backdrop`），逐文件对照本表判断是否有移植范围内改动。
2. 只看上表「本模块」列涉及的文件；`Lens` / `highlight` / `shadow` / `shapes` / 示例应用的变更属阶段 3，不进本库。
3. 逐条同步到对应 C# 文件：效果链顺序、`padding` 提升条件、色彩矩阵常量必须与上游逐字一致，不得就地优化或夹带产品逻辑。
4. 阶段 3 引入 AGSL 时逐字搬运着色器字符串，并补上「AGSL 着色器」表。
5. 更新本文件的上游提交、版本与映射表；在真机（API 31/32 与 API 33+）各跑一次分级回归，确认 `BackdropCapabilities.Describe()` 与实际外观一致。
6. 上游许可若变化，同步 `LICENSE` 与 `NOTICE`。

## 许可与打包

`LICENSE`（Apache-2.0 全文）与 `NOTICE`（归属与修改说明）随模块入库，必须随 APK 分发。库工程的 `AndroidAsset` 不保证被头工程继承，因此头工程需要显式打包：

```xml
<ItemGroup>
  <AndroidAsset Include="..\PhotoViewer.Android.Backdrop\LICENSE"
                Link="Assets\Licenses\AndroidLiquidGlass-LICENSE.txt" />
  <AndroidAsset Include="..\PhotoViewer.Android.Backdrop\NOTICE"
                Link="Assets\Licenses\AndroidLiquidGlass-NOTICE.txt" />
</ItemGroup>
```

`Tools/check-android-package.py` 需把这两项加进 APK 扫描清单（与 DINOv3 License 同法）。阶段 3 引入 `shapes` 代码时，同一份 Apache-2.0 文本覆盖两个上游作品，`NOTICE` 里注明即可。

## 构建与验证

本仓库默认 SDK 可能没有 android workload（`PhotoViewer.Android/AGENTS.md`「构建」）。在任一临时目录放 `{"sdk":{"version":"10.0.201","rollForward":"disable"}}`，从该目录构建：

```bash
cd /tmp/pv-sdk201
dotnet build /path/to/PhotoViewer.Android.Backdrop/PhotoViewer.Android.Backdrop.csproj -c Debug
```

模块自身不含真机断言。头工程的 `pv-check glass` 是唯一验收入口：缺省 `all` = `host` + `product`。

- `product`：**正式产品材质验收**（`PhotoViewer.Android/Native/AndroidNativeChecks.Glass.Product.cs`），跑在主屏现有照片层与共享材质上（6 个栏底、带衬底 EXIF），含结构/配方/分级断言与真实像素证明（黑/灰/白衬底、1px 条纹低通、阶跃边缘展开）和切图/缩放跟随。
- `sample` / `layers`：显式 legacy 机制检查（`AndroidNativeChecks.Glass.Backdrop.cs`），只在点名时运行，用临时装配的 `AndroidPhotoView` + `LayerBackdropLayout` 舞台取证、收尾整体拆除；不作为产品材质验收证据。

## 边界与已知限制

- 不含产品逻辑：令牌、几何、命中区、读屏、悬停、按压弹簧、弹窗入窗行为全部在接入层。
- 只画宿主交给它的层：`SurfaceView` 内容在系统合成器的独立图层上、不在界面渲染层，录不到；`TextureView` 子视图被 `RenderNode` 录制时会带上纹理（产品主图依赖这一点），本模块不对其他视图组合与平台版本作保证。
- 子树上屏与取样共用同一次 `RenderNode` 录制；嵌套层仍各有自己的录制与合成成本，性能结论只在真机测。
- `LayerBackdropLayout` 拥有自己的 `Backdrop`，视图被释放（`Dispose`）时一并回收 `RenderNode`，宿主不必另外记账。
- 层内容变化无需通知：源层每次完成新录制即把效果显示列表置脏，下一次取样用同一个 `RenderNode` 重录；层尺寸变化由实现按当前录制尺寸自动检测重建。**只有替换传给取样的裁切形状才需要 `LayerBackdrop.Invalidate()`**。
- 视图自身带缩放/旋转变换时如何按逆变换取样（上游 `InverseLayerScope`）未实现，属阶段 3。
- 低版本不提供半透明自绘替代：分级为 `ColorLayer` 时调用方必须画正式色层。
