# Core/Storage — 平台中立存储抽象

> 模块内手册。macOS / iOS / Windows 的本地路径统一使用 [LocalStorageRefs.cs](LocalStorageRefs.cs) 的 `LocalFileRef` / `LocalFolderRef`；Android 的 SAF 与本地实现都在 `PhotoViewer.Android/Core/AndroidStorageRefs.cs`。iOS 的访问租约由 `iOSStorageAccessSessionFactory` + `iOSStorageAccessSession` 实现（沙盒自有目录走本地，外部 URL 成对 Start/Stop 安全作用域），Android 由 `AndroidStorageAccessSessionFactory` 校验/补取 SAF 持久授权。各端文件选择器在启动期注入 `IFilePickerService`（`MacUiServices` / `iOSUiServices` / `AndroidFilePicker` / `WindowsUiServices`）。没有共享静态存储访问门面，也没有 Mac 安全作用域书签。

`namespace PhotoViewer.Core.Storage`

**身份与地址分离**：`StorageId` 是唯一身份（本地 `file:<规范化绝对路径>`，Windows 统一大写；SAF `android-document:<authority>:<documentId>`）。`Path` 只作打开地址与日志，`TryGetLocalPath()` 只用于本地优化（例如直接走 `System.IO`），二者都不得替代身份比较或能力判断。

| 文件 | 模块 | 职责 |
|---|---|---|
| [StorageRefs.cs](StorageRefs.cs) | 存储项引用 | `IStorageItemRef`（`Name` / `Path` / `StorageId` / `TryGetLocalPath` / `Capabilities`）、`IFileRef`（读取、属性、父目录、随机读写、原子替换、删除）、`IFolderRef`（枚举、查找/新建子文件）、`StorageItemBasicProperties`。Core 与 ViewModel 只依赖本组类型，平台实现注入。 |
| [StorageItemId.cs](StorageItemId.cs) | 唯一身份 | `StorageItemId` 的本地/提供者构造与 URI 归一化；比较一律用它，不用路径字符串。 |
| [StorageCapabilities.cs](StorageCapabilities.cs) | 能力三态 | `StorageCapability.Unknown / Supported / Unsupported` × `RandomAccessWrite` / `AtomicReplace` / `CreateChild` / `Delete` / `PersistAccess`；另含 `StorageRevision`（大小、修改时间、可选版本）用于写入前后复核。 |
| [StorageWriteSession.cs](StorageWriteSession.cs) | 随机读写会话 | `IStorageRandomAccessSession`：`Length` / `Revision` / 定长 `ReadAsync` / `WriteAsync` / `SetLengthAsync` / `FlushAsync`，全部接受取消令牌；`LocalRandomAccessSession` 用 `FileStream(RandomAccess)` 实现，`FlushAsync` 同时刷到磁盘。 |
| [StorageException.cs](StorageException.cs) | 类型化错误 | `StorageError`（`AccessDenied` / `AccessRevoked` / `ReadOnly` / `NotSeekable` / `Unsupported` / `ObjectGone` / `ChangedExternally` / `InvalidName` / `IoError`）与 `StorageException`；平台异常经 `From` 归一化，调用方按语义分支而不是看消息文本。 |
| [LocalStorageRefs.cs](LocalStorageRefs.cs) | 本地文件系统实现 | `LocalFileRef` / `LocalFolderRef`：`System.IO` 直接实现；除 `PersistAccess` 外本地能力均支持（原子替换、随机读写、新建、删除）；原子替换用唯一临时文件 + 替换/移动，失败清理临时文件。Mac / iOS 沙盒目录 / Windows 与命令行工具共用。 |
| [FilePickerService.cs](FilePickerService.cs) | 文件选择器门面 | `FilePickFilter`（显示名 + 扩展名模式 + `IsGenericImage`）+ `IFilePickerService` + `FilePicker` 静态门面，启动期由 UI 层注入；`PreferFolderPicker` 声明主入口是目录选择（触屏端返回 true），共享层据此选择器类型，不按操作系统判断。 |
| [StorageAccessSession.cs](StorageAccessSession.cs) | 打开会话访问租约 | `IStorageAccessSession`（`Attach` + `Dispose`）与 `IStorageAccessSessionFactory`：一次打开的根项与附加项权限随会话持有；新快照提交成功时释放被取代会话的租约，当前快照保留租约到下一次提交或文件源释放，失败/取消随会话释放。默认 `LocalStorageAccessSessionFactory` 只校验本地路径，未覆盖的存储项返回 `Unsupported`，不阻断打开。 |
