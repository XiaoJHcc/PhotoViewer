# 裁定复核：主代理改判与补充（2026-10-18）

方法：多数（Avalonia 原版 + 四端，桌面/移动可分开）、更优解（按 L0 原则）、原版兜底；依赖原生控件或输入方式且无优劣的登记为 `ID@平台`。下列条目覆盖 rulings-G1…G6 的对应行，其余行按原文生效。

| Q | 原提议 | 改判 | 理由 |
|---|---|---|---|
| B13 SHELL-04 栏位显隐持久化 | P：维持各端现状 | **U：四端都持久化**到平台偏好存储（键名同 Mac `PhotoViewer.Native.ShowFilm` / `ShowControls`；分析栏仍为会话态）。不进设置 JSON，不动持久化契约。Windows、Android 补 | 投票 2:3，但记住用户布局明显更优（PRIN-03），实现代价小 |
| D07 SETTINGS-03 设置页明暗 | P：iOS 跟随系统 | **U：设置页四端固定深色**，iOS 设置展示控制器设 `OverrideUserInterfaceStyle = Dark`（与 iOS 工具页一致） | 原版 `App.axaml:5` 固定 Dark，4/5 固定深色；iOS 工具页本已强制深色，设置页是遗漏 |
| B35 Android 设置控件 | P：三项均为平台差异 | **拆分**：数值 `+/-` 步进 = `SETTINGS-12@android`（P，与 iOS `UIStepper` 同类）；列表排序 **U：改为拖拽**（`ItemTouchHelper` + 行尾把手），上下按钮不是原生做法；拖入文件 = `OPEN-07@android`（P，手机无常见拖入来源） | 三端（mac/iOS/win）拖拽排序，Android 有原生拖拽方案 |
| C27 SETTINGS-14 设置损坏 | 需作者 | **X：直接定**：读取失败时先把原文件改名备份为 `settings.json.corrupt-<时间戳>`，再按默认启动；保存/读取失败在设置页顶部显示一行状态。字段与格式不变 | 不改契约，只防丢失用户配置（PRIN-02） |

## 保留给作者

| Q | 问题 | 建议 |
|---|---|---|
| B30 SCOPE-04@ios | iPad 读 Sony ARW 只有 1616×1080 内嵌预览，100% 与细节预览按预览尺寸 | 先登记平台限制；若 iPad 看 RAW 是目标场景，单独立项 `CIRAWFilter` 全分辨率解码（内存与性能另评） |
