# 跨平台 Fluent 视觉对齐

## 目标

Apple 保持 SF Symbols；Windows 与 Android 共用同一套 Fluent UI System Icons、字体子集和光学尺寸。修正仅以平台名称近似代替实际造型的映射，统一真实 UI 的字号来源与视觉尺寸，保留原始非对称构图和排版边距。

## 范围

- 规范：图标目录改为 Apple / 共享 Fluent 映射；明确设计字号、墨迹尺寸、图标槽与命中区的不同职责。
- 资产：以 macOS 实际 SF Symbols 的 18 点 Regular 渲染为参照，计算各项墨迹边界框对角线之比并取全目录算术平均，所有 Fluent 字形共用唯一归一倍率；保留原始宽高比、排版边距和非对称构图，生成同一套 Regular / Filled 字体供 Windows 和 Android 使用。
- 映射：逐项复核全部语义，优先选择与 SF 构图、方向、轮廓和填充一致的 Fluent 图标。
- UI：Windows 菜单使用 `menu.icon.fontSize`；Android 图标统一按 dp 绘制，菜单、按钮、设置行、卡片、评分和弹窗均使用同一语义目录与所属控件字号。
- 验收：报告分别展示原始映射、实际墨迹尺寸与归一结果；检查 Android 真实视图和截图，Windows 做类型检查，Mac / iOS 验证共享目录编译与 SF 渲染。

## 验收标准

- Windows 与 Android 对同一语义使用相同 Fluent 字形、填充状态、归一字体和标称字号，不再引用 Material Symbols。
- 全部 Fluent 字形共用由 54 项 SF / Fluent 对角线倍率算术平均得到的唯一比例，宽高比不变，无单项缩放例外；所有标称尺寸仍由所属控件令牌决定。
- Android 图标不随系统 fontScale 改变，文字缩放规则不变；菜单词义图标使用 `menu.icon.fontSize`，图标槽使用 `menu.icon.size`。
- 文件夹、适应、增强、胶片栏、EXIF、无星级、全部星级、AI、排序把手、统计与 DINO 等既有近似映射得到修正。
- 图标生成检查、包扫描自检、Core 契约、Android 标准版构建与可运行检查通过；Windows / Apple 未能执行的真屏项如实登记。

## 本轮验收

- `python3 Tools/generate-icons.py --check`：54 个语义项、全部码位、字体摘要、生成码位表与全目录唯一平均倍率通过；全部字形使用 `1.322201511804601`，无平移、无单项缩放或构图修正，Regular/Filled 的 advance 与 LSB 保持上游数据。
- 独立复审：生成器仅执行子集与原点等比缩放，字体度量自洽，无额外居中或构图改动。
- Android 标准版 Debug 构建、安装与 APK 扫描通过；真实视图 `main` 38/38、`filter` 26/26、`settings` 88/88、`layout` 116/116 通过。fontScale 1.0 与 1.3 两档均通过，图标按 dp 保持尺寸，设置页实截图位于 `artifacts/acceptance/icon-catalog/`。
- `Tools/check-android-package.py --self-test`：28 项通过；`Tools/check-windows-package.py --self-test`：EXE 45 项、MSIX 21 项通过。
- Mac / iOS 标准版 Debug 构建通过；Web 单测 362 项通过；`Tools/CoreContractCheck` 标准版 2047 PASS / 1 SKIP / 0 FAIL。
- Windows 类型检查仍止于当时已有的 `WindowsLayout.StatusToastStackGap` 缺失（`WindowsMainWindow.cs:255`、`:1935`）；此轮未做 Windows 真屏验收。

## 排版框纠正与复验

- 旧版仅从字体原点缩放轮廓，保留原 advance、LSB 与基线度量，造成墨迹越出排版框；WinUI 居中排版框时墨迹偏移。以 Windows↔macOS、Android↔iOS 的对应槽为对标，保持全目录统一倍率与既有 UI 令牌，在字体生成阶段把墨迹放进居中的方形排版框。无逐项 UI 位移或二次缩放。
- Windows 标准版实测 150%：打开文件夹在同一 66×66 像素槽内，旧字体中心偏差 `(+1.5, −4.5)px`、新字体 `(+0.5, +1.0)px`；图标专项 9 项像素断言与原生菜单通过，设置、工具和评分专项通过。完整冒烟的标题栏灰阶断言在修复前后均失败（`#151516` 对 `#323232`）。专项证据：`artifacts/acceptance/icon-alignment/`。
- Android 标准版 Debug 构建通过；无可运行 arm64 设备，未重跑设备上的布局与 fontScale 矩阵。Apple 原生控件未重新截图对拍。评分胶囊桌面/移动字号仍取各自既有令牌；系统托管菜单、原生设置侧栏以及桌面来源浮窗与移动星级弹窗不视为同一几何。

## 原始设计锚点与裁切回归

- 逐一对照固定上游的 45 个独立 SVG / TTF 字形：源字体的 1 em 方框为 SVG `viewBox`，按各自锚点归一后四边位置最大差 0.001281 em（小于 1 个 TTF 单位）。轮廓绕原锚点以同一倍率缩放，移除生成期的墨迹外接矩形居中。18 点时左右箭头保留约 ±0.89 点原设计偏心；最远边约 0.595 em，仍在 ±0.6 em 的字体排版框内。供对照的原始与实际资产指标在 `artifacts/acceptance/icon-anchors/`。
- 移动设置命令图标使用独立 24 点槽，保留 18 点字号；产品验收对照宽松参考渲染验证真实宿主的边界与轮廓完整性，单纯的中心断言不作为无裁边证明。
- Windows 标准版图标专项 21 项通过，覆盖原生菜单与设置／工具的标题栏和分类模板；设置、工具专项通过。旧标题栏字号／分类默认字号在负向对照被拒绝。完整冒烟仍遇到本机已有的 `SHELL-02` 标题栏灰阶断言失败，图标段通过。Web 362 项通过，Core 2023 PASS / 1 SKIP / 0 FAIL。Android 标准版 APK 扫描通过，包内两份字体与源资产逐字节相同；本机缺 Apple workload，无 iOS/macOS 构建与截图结果。
- Android 真机补验（标准 Debug，2211133C，API 33，1080×2400、440 dpi／2.75×）：`fontScale=1.0` 设置 90、布局 93、筛选 34 项通过；`fontScale=1.3` 设置 90、布局 93 项通过。两档雾天增强和评分星均为 49.5 px 字号、66×66 px 槽、四边至少留 5 px；产品与宽松参考的墨迹面积及容差内覆盖一致。真机发现 Material 复合图标绘制会平移画布，验收改用按钮局部坐标独立推导槽中心，不再读取未含画布平移的 `Drawable.Bounds`。报告在 `artifacts/acceptance/icon-anchors/android-device/`；本轮未覆盖其他屏幕密度或 Apple 对拍。
