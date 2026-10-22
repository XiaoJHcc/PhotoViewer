# Core/Storage — 平台中立存储抽象

> 模块内手册。平台实现分在各头工程：macOS `Mac/Core/MacStorageRefs.cs`、iOS `Core/iOSStorageRefs.cs`、Android `Core/AndroidStorageRefs.cs`、Windows 直接用 `LocalStorageRefs`；各端的文件选择器在启动期注入 `IFilePickerService`（`MacUiServices` / `iOSUiServices` / `AndroidFilePicker` / `WindowsUiServices`）。共享工程不再有 Avalonia 适配器。

`namespace PhotoViewer.Core.Storage`

| 文件 | 模块 | 职责 |
|---|---|---|
| [StorageRefs.cs](StorageRefs.cs) | 存储项引用 | `IStorageItemRef`(`Name`/`Path`/`PathKey`/`TryGetLocalPath`)/ `IFileRef`(+`OpenReadAsync`/`GetBasicPropertiesAsync`/`GetParentAsync`)/ `IFolderRef`(+`GetItemsAsync`)/ `StorageItemBasicProperties`。Core 与 ViewModel 只依赖本组类型（接口形状沿用早期存储抽象的语义，现由各端原生实现）。 |
| [LocalStorageRefs.cs](LocalStorageRefs.cs) | 本地文件系统实现 | `LocalFileRef`/`LocalFolderRef`:System.IO 直接实现(不经平台存储层),用于沙盒内路径与本机路径场景(P1-M5 压测驱动器、命令行工具),无安全作用域书签开销。 |
| [FilePickerService.cs](FilePickerService.cs) | 文件选择器门面 | `FilePickFilter`(显示名 + 扩展名模式 + `IsGenericImage`)+ `IFilePickerService` + `FilePicker` 静态门面,启动期由 UI 层注入。 |
