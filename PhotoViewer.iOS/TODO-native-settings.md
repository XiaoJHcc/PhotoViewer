# iOS 原生设置半屏浮窗 TODO

目标：将当前移动端 Avalonia 设置模态替换为 iOS 原生半屏浮窗，交互风格对齐系统设置卡片/日历编辑卡片，先覆盖设置页，不改主图浏览链路。

## 现状锚点

- 当前移动端设置入口：`PhotoViewer/ViewModels/MainViewModel.cs` 的 `OpenSettingModal()`。
- 当前触发位置：`PhotoViewer/Views/Main/ImageView.axaml.cs` 的 `OpenImageSetting()`。
- 当前移动端设置内容：`PhotoViewer/Views/Settings/SettingsView.axaml`，包含「文件 / 预览 / 控制 / EXIF」四个分页。
- 当前移动端设置页附带 Avalonia 特定补丁：`PhotoViewer/Views/Settings/SettingsView.axaml.cs`，负责键盘避让、输入焦点与底部占位处理。

## 迁移原则

- iOS 端直接使用原生半屏浮窗，不继续复用 Avalonia 设置模态容器。
- 设置数据与持久化继续复用共享层，避免复制业务逻辑。
- 第一阶段只做 iOS；Android 暂不跟进。
- 第一阶段不追求与 Avalonia 设置页完全同构，优先保证原生交互、输入体验和信息架构清晰。

## Phase 1: 拆出原生设置入口

- 在 `PhotoViewer.iOS/Core/` 新增设置浮窗协调器，例如 `iOSNativeSettingsPresenter`。
- 从 iOS 平台层提供一个“显示原生设置页”的入口，避免在共享 ViewModel 中直接引用 UIKit。
- 调整移动端打开设置逻辑：iOS 上优先走原生 presenter，不再调用 `OpenSettingModal()`；其余平台保持现状。
- 保留 Avalonia 设置模态作为回退路径，待 iOS 原生页稳定后再决定是否删除。

## Phase 2: 确定原生页面结构

- 使用 `UIViewController` + `UINavigationController` + `UISheetPresentationController` 实现半屏卡片。
- 采用系统 detents，至少支持：
  - 中等高度（`medium`）
  - 大高度（`large`）
- 顶部使用原生导航栏，包含标题、关闭按钮，必要时支持分页内二级页面跳转。
- 页面结构建议改为原生常见分组列表，而不是继续强行映射 Avalonia 的 `TabControl`。

建议的信息架构：

- 一级列表：文件、预览、控制、EXIF。
- 点击一级项后进入对应分组详情页。
- 若某一组内容较短，也可直接在同一页用分组 Section 展示。

## Phase 3: 抽设置读写桥接层

- 为 iOS 原生页准备一个不依赖 Avalonia 控件的设置访问接口。
- 优先直接复用 `SettingsService` / `SettingsModel`；若 `SettingsViewModel` 中混入了 Avalonia 语义，则只复用纯数据和命令逻辑，不直接复用页面 VM。
- 为原生页提供这些能力：
  - 读取当前设置值
  - 写入设置值
  - 响应设置变更后刷新主界面
  - 对需要立即生效的设置执行同步回调

建议新增一层轻量映射：

- `iOSNativeSettingsSnapshot`：原生页初始化时的只读快照。
- `iOSNativeSettingsActions`：封装写回方法，避免 UIKit 页面直接碰共享实现细节。

## Phase 4: 第一批优先原生化的设置项

优先做这些“纯表单”项：

- 文件
  - 同名文件视为同一张图片
  - 原生解码线程
  - 软件解码线程
  - 缓存内存上限
  - 缓存数量上限
  - 预载数量与延时
- 预览
  - 缩放时显示指示器
  - 缩放比例预设（可先做增删改，不必第一版就追求和现有编辑态完全一致）
- EXIF
  - 显示星级评分
  - 安全修改文件星级

这些内容适合直接映射到 iOS 原生控件：

- `UISwitch`
- `UIStepper`
- `UITextField`
- `UISlider`
- `UICollectionView` / `UITableView`
- `UIAlertController` 或二级编辑页

## Phase 5: 明确后置项

这些功能不建议放进第一版：

- 控制页中的快捷键录制
- 复杂排序拖拽的完全复刻
- 和 Avalonia 设置页完全一致的四页签视觉结构
- 所有说明文字、边缘样式、间距完全像现在的自定义设计

处理建议：

- 热键录制：iOS 第一版先只读展示当前快捷键，不提供录制或修改。
- 排序类设置：先支持开关勾选，再决定是否追加原生拖拽排序。
- 苹果键盘映射：如果仍有真实 iPad 外接键盘使用场景，再纳入第二阶段。

## Phase 6: 排序类设置的原生方案

当前 Avalonia 设置页里有三类排序列表：

- 文件格式排序
- EXIF 显示项排序
- 控制栏功能排序

当前收敛方案：

- 文件格式排序：已支持勾选启用 + 原生拖拽排序。
- EXIF 显示项排序：已支持勾选启用 + 原生拖拽排序。
- 控制栏功能排序：已支持勾选显示 + 原生拖拽排序，快捷键保持只读展示。

若后续要补排序：

- 优先使用 `UICollectionViewDiffableDataSource` + reordering。
- 不建议在第一版为了追平 Avalonia 的 `SortableList` 行为，提前写大量手势桥接代码。

## Phase 7: 与 Avalonia 主界面的同步

- 原生设置页修改值后，需要确保共享层状态与当前主界面同步。
- 对以下内容重点验证：
  - 布局相关设置是否即时刷新主界面
  - 预载/缓存设置修改后后台逻辑是否即时采用新值
  - EXIF 相关设置修改后详情栏是否立即生效
- 原生设置页统一在关闭时 apply，避免边编辑边刷新主界面造成移动端单页状态抖动。

## Phase 8: 删除 Avalonia 移动端设置补丁

当 iOS 设置页已稳定并默认走原生路径后，再评估清理以下内容：

- `SettingsView.axaml.cs` 中仅为 iOS 设置页存在的键盘避让逻辑
- `DeferredNumericTextBox` 在 iOS 设置页中的专用输入补丁
- `OpenSettingModal()` 在 iOS 上的调用路径

注意：这些逻辑不能过早删除，因为 Android 仍可能依赖现有 Avalonia 设置页。

## 验收标准

- 点击设置后，以 iOS 原生半屏卡片弹出。
- 半屏卡片支持拖拽扩展到更高高度，并可下滑关闭。
- 数字输入、滑条、开关均使用原生控件，键盘和安全区行为正常。
- 第一批设置项能够正确读写并持久化。
- 关闭设置页后，主界面对应行为按预期刷新。
- iOS 路径稳定后，不再依赖 Avalonia 设置模态来承载设置表单。
