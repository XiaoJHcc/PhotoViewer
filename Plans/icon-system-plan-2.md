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
- Windows 类型检查仍止于既有 `WindowsLayout.StatusToastStackGap` 缺失（`WindowsMainWindow.cs:255`、`:1935`），没有新增图标相关错误；Windows 真屏验收未执行，设计状态保留待核验。
