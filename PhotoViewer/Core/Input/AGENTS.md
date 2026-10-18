# Core/Input — 平台中立输入模型

> 模块内手册。手势的运行时应用（捕获/匹配/执行）在四个原生壳：`MacInput`（NSEvent）、`AppleInputMapping`（HID usage）、`WindowsInput`（虚拟键/滚轮）、`AndroidInputMapping`（`KeyEvent`），共同查共享 `PhotoViewer.Shared/InputCommandMap.cs` 的命令表；原 `Controls/HotkeyButton`、`Views/*.axaml.cs` 与 `Platform/AvaloniaGestureMapping` 已随旧壳删除。

`namespace PhotoViewer.Core.Input`

`AppleKeyboardMapping.ApplyForRuntime` 提供显式配置重载，原生设置副本录制不临时改写全局映射；键/修饰键的 JSON 名值不变。

| 文件 | 模块 | 职责 |
|---|---|---|
| [GestureModels.cs](GestureModels.cs) | 手势模型 | `GestureKey`（JSON 按名称持久化）/ `GestureModifiers`（JSON 按数值持久化）/ `AppKeyGesture` / `MouseGestureEx` / `AppGesture` / `AppleMappingTarget` / `AppleKeyboardMapping`(Cmd/Option/Ctrl 映射)。成员名与数值是冻结的持久化契约（历史上镜像 Avalonia 的键枚举，现已独立）；**改成员名或数值 = 丢用户快捷键配置,禁止。** |
