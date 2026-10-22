# Avalonia 旧壳冻结基线

只读追溯材料，不是验收标准（权威顺序见 [Design/README.md](../../README.md) §5）。Avalonia 删除后用来查原设计意图，不再更新。

## 来源

| 文件 | 内容 | 采集 |
|---|---|---|
| `legacy-layout-geometry.json` | 两向（col/row）× 三尺寸（1200×850 / 830×650 / 700×480）客户区内真实已布局控件几何，坐标统一换算到 MainView，逻辑点；共 492 节点 | 2026-10-16 21:32，macOS 26.6.2 arm64，Retina（RenderScaling=2），`--legacy-ui --pv-legacy-layout release/native-p2-fixtures/color-srgb.jpg`（12 张夹具，见 `release/native-p2-fixtures/`），驱动器 `PhotoViewer.Mac/Native/LegacyLayoutAcceptance.cs`；测试口径：四项 EXIF（光圈/快门/ISO/等效焦距）+ 六个命令（Previous/Next/Exchange/Fit/ZoomInPreset/ZoomOutPreset）+ 评分，相似面板收起 |
| `legacy-layout-{col,row}-{1200,830,700}.png` | 同次运行的 `RenderTargetBitmap` 控件渲染导出 | 同上；不是系统窗口截图，无标题栏、阴影与色彩管理 |
| `legacy-window-col.jpg` | 旧壳左右布局系统窗口截图 | 2026-10-15 |
| `toolbar-legacy-window.jpg` | 旧壳控制栏对拍窗口截图（由 PNG 转 JPEG） | 2026-10-15 |

采集时刻对应的旧壳视图代码：`PhotoViewer/Views/`、`Controls/`、`App.axaml` 自 2026-10-16 起无改动，与 git tag `avalonia-final` 所指提交一致。

## 已知限制

- 2026-10-17 起（`41abd7b`，展示编码协商）旧壳在 Mac 上已无法加载主图：`BitmapLoader` 要求注入展示编码策略，旧壳路径未注入，报"展示编码策略未注入"。Avalonia 不再验收（作者 2026-10-16 裁定），此缺陷不修，故本基线无法重新采集；`avalonia-final` 之前的提交在当前工具链（Xcode 27.1 / macOS workload 27，最低 macOS 14）下也已无法直接构建。
- 只有 macOS 采集；Windows/iOS/Android 旧壳几何未冻结，各平台以本文件的逻辑点几何为准。
- 夹具无 EXIF 时 EXIF 块显示 `--`；几何不受影响。
- 已知旧壳缺陷不构成设计：纵向可见范围估算误用 X/Width、分析瓦片"外框 6 + 内层 5"双层圆角、"已缓存"描边被缩略图覆盖（见 ui-contract §4 发现登记、ANL-02、FILM-05）。
