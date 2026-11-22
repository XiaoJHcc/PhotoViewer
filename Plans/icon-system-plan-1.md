# 三端语义图标体系

## 目标

以 `Design/icons.json` 统一 Apple / Windows / Android 的图标语义，Windows 收敛到 Fluent UI System Icons，Android 收敛到 Material Symbols Rounded；消除缺图标、同语义多项混用、跨族回退和 Regular / Filled 风格不一致。Web 保持 uicons 参考壳体系。

## 范围

- 规范：新增 ICON-01–05，修订主屏图标总则及 Windows / Android 平台差异。
- 资产：固定上游提交，生成 Regular / Filled 字体子集、共享语义码位表与许可证。
- Windows：主图菜单、筛选、控制栏、胶片卡片、评分来源浮窗、设置与工具分类、设置排序把手和删除按钮；系统窗口按钮保持原生。
- Android：主屏命令、手机胶囊、主图菜单、筛选、评分、设置排序行与已有导航/增删图形；不新增分类图标或装饰。
- 验收：三端语义对比页、码位与产物完整性检查、现有平台回归和真屏边界登记。

## 实施顺序

1. 先写规范与清单，生成候选资产及三栏对比，检查全部 54 个语义项。
2. 迁移 Windows 映射与字体来源，更新对应原生验收。
3. 迁移 Android 映射与字体来源，移除业务界面的 uicons / 系统 drawable 混用，更新对应原生验收。
4. 更新设计实现状态、模块手册和开发操作说明。
5. 跑生成检查、共享契约、Windows 可用构建/类型检查、Android 标准版构建及可运行的设备检查；保存对比报告。

## 验收标准

- 清单中每项都有三端有效映射，随包字体包含全部生成码位，`--check` 通过。
- Windows 与 Android 业务图标不再引用旧字体或系统业务 drawable；未知语义显式失败。
- 同一命令在主屏与设置行同图；筛选 1–5 星可区分；EXIF 独立；星级 Regular / Filled 同族。
- 不改变控件几何、命令行为、设置持久化和读屏名称。
- Windows 真屏、Android 真机/模拟器的未执行项明确记为待核验，不以构建通过代替。

## 本轮验收

- `python3 Tools/generate-icons.py --check`：54 个语义项、全部码位、字体摘要与生成码位表通过；macOS 实际 SF Symbols 对比报告生成于 `artifacts/acceptance/icon-catalog/index.html`。
- Android 标准版 Debug 构建通过；API 35 模拟器真实视图检查：`main` 38/38、`filter` 26/26、`settings` 88/88。主屏与设置截图位于 `artifacts/acceptance/icon-catalog/android-*.png`。
- `Tools/check-android-package.py --self-test`：28 项通过；`Tools/check-windows-package.py --self-test`：EXE 45 项、MSIX 21 项通过。实际 Android APK 另核对两份 Material 字体与许可证均在 `assets/Icons/`。
- Mac / iOS 标准版 Debug 构建通过，验证 Apple 共享映射没有编译回归。
- `Tools/CoreContractCheck` 标准版：2047 PASS / 1 SKIP / 0 FAIL。
- `Tools/WindowsTypeCheck` 仍止于既有 `WindowsLayout.StatusToastStackGap` 缺失（`WindowsMainWindow.cs:255`、`:1935`），本轮没有改动该业务；Windows 真屏构建与 Fluent 界面验收未执行，设计状态保留待核验。
