# Core/Input — 平台中立输入模型

> 模块内手册。手势的运行时应用(捕获/匹配/执行)在 UI 层(`Controls/HotkeyButton`、`Views/Main/ControlView.axaml.cs`、`Views/Main/ImageView.axaml.cs`),经 `Platform/AvaloniaGestureMapping` 与 Avalonia 输入事件互转。

`namespace PhotoViewer.Core.Input`

P2 增加 `AppleKeyboardMapping.ApplyForRuntime` 显式配置重载，原生设置副本录制不临时改写全局映射；键/修饰键的 JSON 名值不变。

| 文件 | 模块 | 职责 |
|---|---|---|
| [GestureModels.cs](GestureModels.cs) | 手势模型 | `GestureKey`(成员名镜像 `Avalonia.Input.Key`,JSON 按名称持久化)/ `GestureModifiers`(数值与 `Avalonia.Input.KeyModifiers` 一致,JSON 按数值持久化)/ `AppKeyGesture` / `MouseGestureEx` / `AppGesture` / `AppleMappingTarget` / `AppleKeyboardMapping`(Cmd/Option/Ctrl 映射)。**改成员名或数值 = 丢用户快捷键配置,禁止。** |
