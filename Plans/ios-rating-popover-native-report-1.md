# iOS 26 星级冲突浮窗问题报告

## 结论与范围

本次只处理 iOS / iPadOS 的星级来源冲突浮窗及其验收、文档。共享评分来源、全来源写回和 macOS `NSPopover` 实现未修改。

作者已核查当前实现。当前证据覆盖 iPad mini (A17 Pro) / iOS 26.3 模拟器上的原生展示、布局、输入、整组写回和放大截图；不覆盖真机触屏、其他设备尺寸或其他 iOS 版本。

## 问题与根因

1. **锚点语义错误**：1 点中心锚点与 `SourceItem` / `SourceView` / `SourceRect` 混用，使箭头指向图标内部而非入口边缘，且没有保留完整槽位矩形。
2. **绕过系统内容区**：手工 frame 从 `popup.padding` 直接排条目，没有使用 UIKit 在箭头侧提供的安全区。实测上/下箭头方向的内容安全区为 13 点，导致顶部文字被挤入箭头覆盖区。
3. **把展示开始当成展示完成**：`IsOpen` 早于原生过渡完成，曾经截到缩小过渡态。旧展示链未完全退出时也不能开启下一次展示。
4. **虚拟化按钮不是稳定锚点**：实测卡片会复用到其他照片（如 `orientation-5.jpg`），其 frame 改变，而列表 `contentOffset` 可以不变。直接持有原 `UIButton` 会把旧弹层关联到错误入口。
5. **系统默认边距把箭头推离贴边入口**：`PopoverLayoutMargins` 读回 0（表示用系统默认），实测 iPad 左缘主体停在 19 点；iOS 26 箭头尖距主体边缘最少约 12.75 点，三角中心在 23 点时箭头只能落在约 31.75 点，表现为尖角错位、贴边一角被压成硬角。把边距设为安全区 + 8 点后主体贴到 8 点，箭头尖落在约 22.75 点对准三角，系统自行把贴边一角形变为平滑过渡（边距 12 点时已偏 2 点，30 点时偏 20 点）。
6. **无关设置通知关闭卡片浮窗**：`BitmapCacheCountInfo` 等运行时状态也走设置变化通知，控制栏刷新时无条件关闭卡片浮窗，展示约 0.4 秒后就消失；重载还会把同一照片换到另一个复用卡片实例上。
7. **外形实现方式错误**：曾尝试用 `UIPopoverBackgroundView`、`UIBezierPath` 和玻璃拼接拟合 Mac 曲线。那只是使用官方扩展点自行绘制，不是苹果提供的 Mac 同款控件，已全部删除，未进入交付。

## 最终实现

实现位于 `PhotoViewer.iOS/Native/iOSRatingSourcesPopover.cs`：

- 继续使用系统 `UIPopoverPresentationController`，不自绘弹层外框、箭头或玻璃形状。
- 内容使用 `UIStackView` 与 `SafeAreaLayoutGuide`，在系统安全区之上叠加 `popup.*` 令牌约束。条目、星排与分隔线继续按共享尺寸和来源真值布局，不再绕过箭头区。
- 所有入口统一使用主窗口 `SourceView` + 入口完整 `SourceRect`。矩形由实际按钮在主窗口坐标系中换算得到，不持有会被复用的卡片按钮，也不创建合成 `SourceItem` 视图。
- 展示和退出都通过 UIKit 完成回调串行衔接；不再以固定延时猜测过渡结束。
- `GetAdaptivePresentationStyle` 两个公开代理重载均返回 `UIModalPresentationStyle.None`，紧凑屏保留原生 popover，不再自动适配为半屏 sheet。
- `PopoverLayoutMargins` = 根视图安全区 + `popup.ratingSources.edgeMargin`（8 点），贴边入口的箭头对准三角中心，贴边一角由系统形变，不自绘。
- 控制栏刷新重载列表后，只在同一照片的入口仍停在展示矩形上时保留卡片浮窗。
- 实际滚动、换向或布局移动关闭浮窗，不把旧锚点跟随到另一张照片。
- iOS 原生箭头与贴边形变按系统外观保留，不把 Mac `NSPopover` 的曲线外形当成 iOS 的对等控件或像素验收目标。

`PhotoViewer.iOS/Native/iOSNativeRatingPopoverProbe.cs` 只读取公开 UIKit 几何与视图层级，供验收诊断，不创建替代弹层或改变产品行为。

## 规范核对

全局方向规范原本就在 [RATING-04](../Design/spec/rating.md) 第 66–69 行：

- 胶片条卡片等普通入口默认向下，只有下方不足且上方可容纳时才向上回退。
- 底部横排控制栏的星级入口向上。
- 右侧竖排控制栏的星级入口向左。

这些规则没有改为平台各自决定。`Design/platforms.md` 只记录原生宿主和差异；模块手册同步为实际实现，不再保留 1 点中心锚点或控制栏更新 `SourceView` 的旧描述。

人工核查保留参数 `--pv-native-rating-hold` 只用于验收会话：在左侧完整快照与内容几何通过后持续展示真实 UIKit 场景，不写完成验收报告，也不提升验收状态。

## 验证证据

- 构建：`dotnet build PhotoViewer.iOS/PhotoViewer.iOS.csproj -c Debug -r iossimulator-arm64`，0 错误、28 条既有 nullable 警告。
- 边距实验（iPad mini A17 Pro / iOS 26.3，三角中心 x=23）：默认边距主体 x=19、箭头尖约 31.75；边距 4/8 主体 x=4/8、箭头尖约 22.75（截图像素读取）；12 → 24.75；30 → 42.75。负值会让主体越出窗口，不采用。
- 截图核对：iPad mini 与 iPhone 17 竖屏（安全区上 62 点）各自的行布局、列布局贴边卡片，箭头均对准三角，主体左缘 8 点且未越出窗口，贴边一角平滑。
- 布局：`--pv-native-layout-check layout-fixtures` 29 PASS / 3 NOTE / 0 FAIL（NOTE 为当前窗口模式不接受程序化旋转及对应尺寸核对）；新增边距与贴边主体位置断言，稳定期按照片重新取卡片核对。
- 输入：`--pv-native-input-check layout-fixtures` 48 PASS / 4 NOTE / 0 FAIL。
- 本机只有 iOS 26.3 模拟器，iOS 27 与真机触屏未验证。

证据文件：

- [贴边边距后布局验收](../release/native-p2-acceptance/ios-rating-layout-edge.txt) · [布局验收 1](../release/native-p2-acceptance/ios-rating-layout-1.txt) · [布局验收 2](../release/native-p2-acceptance/ios-rating-layout-2.txt) · [提交前默认布局验收](../release/native-p2-acceptance/ios-rating-layout-final.txt)
- [输入验收](../release/native-p2-acceptance/ios-rating-input.txt)
- [顶部胶片条原图](../release/native-p2-acceptance/probe-rating-sources-card-row-popover.png) · [顶部放大图](../release/native-p2-acceptance/ios-rating-row-zoom.png) · [入口几何标记](../release/native-p2-acceptance/ios-rating-row-geometry.png)
- [左侧胶片条原图](../release/native-p2-acceptance/probe-rating-sources-card-popover.png) · [左侧放大图](../release/native-p2-acceptance/ios-rating-col-zoom.png) · [入口几何标记](../release/native-p2-acceptance/ios-rating-col-geometry.png)
- [底部控制栏](../release/native-p2-acceptance/probe-rating-sources-popover.png) · [右侧控制栏](../release/native-p2-acceptance/probe-rating-sources-control-col-popover.png)

## 未覆盖项

- 真机触屏手感、更多 iPhone/iPad 尺寸、多任务和旧 iOS 版本未在本轮核查。
- 模拟器输入检查不能代替真实硬件、Files 安全作用域或所有原生过渡效果的验证。
- Windows / Android / Web 的来源浮窗未在本次扩展实现；共享评分存储与 macOS 实现不在本次修改范围。
