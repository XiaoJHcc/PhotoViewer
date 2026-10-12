# Core/Storage — 平台中立存储抽象

> 模块内手册。Avalonia 适配器在 [Platform/AvaloniaStorageRefs.cs](../../Platform/AvaloniaStorageRefs.cs)(暴露 `Inner` 供头工程解包原始存储项);选择器实现在 [Platform/AvaloniaFilePickerService.cs](../../Platform/AvaloniaFilePickerService.cs)(UTI/MIME 推导表也在其中)。

`namespace PhotoViewer.Core.Storage`

| File | 模块 | Responsibility |
|---|---|---|
| [StorageRefs.cs](StorageRefs.cs) | 存储项引用 | `IStorageItemRef`(`Name`/`Path`/`PathKey`/`TryGetLocalPath`)/ `IFileRef`(+`OpenReadAsync`/`GetBasicPropertiesAsync`/`GetParentAsync`)/ `IFolderRef`(+`GetItemsAsync`)/ `StorageItemBasicProperties`。语义逐项对应 Avalonia `IStorageItem`,Core 与 ViewModel 只依赖本组类型。 |
| [FilePickerService.cs](FilePickerService.cs) | 文件选择器门面 | `FilePickFilter`(显示名 + 扩展名模式 + `IsGenericImage`)+ `IFilePickerService` + `FilePicker` 静态门面,启动期由 UI 层注入。 |
