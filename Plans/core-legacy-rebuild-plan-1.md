# 共享 Core 遗产重建：存储、设置、评分与打开

## 目标

把早期 Avalonia 时代继承到原生壳的共享层，按实际产品意图重建为明确的领域契约，而不是继续增加平台分支。正式版前不保留旧设置 schema、旧接口和旧数据的迁移层。照片写入必须可验证；失败不得伪装成功，也不得承诺跨文件绝对原子性。

实施顺序：存储身份/能力/授权 → 设置事务 → 同名组与 XMP 评分 → 外部打开与可取消文件夹会话。原生 UI、解码/色彩、内存管理和 AI/数据库内部不在本项目重写。

## 历史依据

- `586fa69` 已形成设置 VM 即状态真源；`7951829`（2026-03-02）才引入 JSON 持久化、双向手映射与节流保存，v0.3.7 之前没有设置文档。
- `91bc3b5`、`012ca8f`、`29d3ec7` 在 2025-09 形成 XMP 前缀扫描和单字符写入；`daf5ca3` 只拆出平台 writer，没有重建包模型。
- `73fc099`、`cb5ff5d` 形成共享外部打开队列；原生迁移后入口分流，旧有串行/去重语义没有完整保留。
- `3da083c` 将 Avalonia 存储接口机械映射为 `IFileRef`/`IFolderRef`，只有只读句柄，不能表达写回、sidecar、原子替换与授权生命周期。
- 早期 Android USB 写入的整文件重写、GC、系统 `sync`、延时和 drop_caches 已删除；当前为 fd `Force/Sync`。仓库没有私有 MTP/PTP 栈，USB 相机依赖系统 DocumentsProvider，能力必须实证。

## 主要发现

- 设置普通读取异常会返回默认值，真实加载结束又无条件排队保存，存在覆盖原配置的路径；文件保存还存在 Delete→Move 窗口，0 被同时当作合法值与缺省哨兵。
- XMP 读写没有共同语义模型；只能修改已有 Rating 字符，RAW sidecar 文档与实现不符；同名组部分失败可静默成功，快速改星缺少串行化，PNG 直接改字节会破坏 CRC。
- `PathKey` 与 `Path.LocalPath` 在 SAF 上分裂并被混用；存储接口无写入、创建、替换和 typed failure；Android 授权门面为 no-op，iOS scope 只保留不释放。
- 外部打开的 pending 与 handler 均 fire-and-forget；Mac/Windows 命令行绕过共享服务，多文件在不同壳有不同语义；枚举失败前已清空旧列表，代际只丢结果而不取消工作。

## 目标契约

### 存储

`StorageItemId` 是唯一身份，URI 只作地址/日志，本地路径只作平台优化。能力使用 Unknown/Supported/Unsupported 三态，覆盖随机写、原子替换、同目录创建、删除与持久授权。打开照片不主动试写；真实读写返回 AccessDenied/AccessRevoked/ReadOnly/NotSeekable/Unsupported/ObjectGone/ChangedExternally 等明确错误。

本地文件使用唯一临时名、flush 与同卷替换。iOS 授权随打开会话成对持有/释放。Android 区分本地与 document ref，集中处理 URI/documentId/treeId、Provider flags、属性和 grant；不自研 MTP，不用延时或 sync 伪装相机写卡完成。

### 设置

不可变 `AppSettings` 是运行时快照，平台默认值和稳定 id 目录各有唯一来源。`SettingsRepository` 拥有加载、串行提交和 flush；设置页只编辑 draft。新文档 schemaVersion=2，不保留旧迁移分支；持久化 DTO 可以 nullable 表示缺失，领域快照不使用 0/空值哨兵。

提交顺序为归一化 → 后台持久化 → 成功后一次发布。草稿不产生自动保存，400ms 全量保存管线退役。普通读取失败、备份失败和未来版本不得覆盖原文。Windows/macOS 用原子文件替换；Apple/Android 可保留平台偏好中的整份 JSON，但写入必须可确认完成。相似面板等视图状态移出设置 draft。

### XMP 与评分

XmpCore 是 XMP 语义真源，容器适配器负责 canonical packet 位置、主图/预览/扩展区分、长度/偏移/checksum。原位写只在语义解析后执行；需要扩容时必须由容器适配器配合 AtomicReplace，不支持则明确失败。PNG CRC、WebP RIFF、JPEG 段长、TIFF tag 700、HEIF item/iloc 各自由所属适配器维护。

专有 RAW 优先标准 `<stem>.xmp`，已有内嵌 Rating 仍读取；首次创建 sidecar 要保留其它 canonical XMP 属性。DNG/JPEG/HEIF/PNG/WebP/TIFF 继续内嵌，不静默改投 sidecar。

同名组由共享 `PhotoGroupMap` 计算，列表、EXIF 和评分共用。评分按组串行、未开始请求 latest-wins、物理写入不中途取消；写后语义重读。组内失败逆序尽力补偿，补偿不完整必须报告 Partial 并按磁盘真值显示，不得静默部分成功。

### 打开

平台只把原生事件转换为存储引用和 OpenRequest。实例化 channel 单消费者顺序处理；一批只选首个可打开目标，重复外部打开幂等。FolderLoadSession 拥有授权、取消与工作快照，后发取消先发，新枚举成功后一次发布，失败保留旧列表或按规则回退单图。Mac/Windows/iOS/Android 的命令行、系统打开、分享与拖放统一进入该流程。

## 验收

新增 `Tools/CoreContractCheck` 覆盖本地存储、设置、XMP、同名组、打开与故障注入；Android 增加独立 `pv-check storage`，以 debug DocumentsProvider 覆盖 content URI、只读、不可 seek、Provider 拒绝访问故障注入和嵌套目录。`ACTION_VIEW` 仅覆盖同 UID 打开；跨 UID 临时授权、系统授权撤销、DocumentsUI、USB/MTP/云 Provider 不属于此套件。保留现有 SettingsDefaultsCheck 与四端原生分套回归。

真实 Windows 激活、iOS 安全作用域、Android SAF/USB 相机必须在对应系统验证。手机侧 fsync 不等于相机写卡完成；USB 成功需独立读回或另一设备复核。无法在本机执行的门保持未验，不以静态检查或模拟器冒充。

## 状态

- [x] 历史考古、当前调用链与关键缺陷复核
- [x] 共享工程与 SettingsDefaultsCheck 基线通过
- [x] 存储契约与四端适配
- [x] 设置事务
- [x] 同名组与 XMP 评分
- [x] 外部打开与文件夹会话
- [x] 本机可执行回归与文档收口：CoreContractCheck 839 PASS / 1 SKIP / 0 FAIL（夹具入库后全量），SettingsDefaultsCheck 通过；macOS 与 iOS 模拟器分套通过；Android faults 20/0、storage 22/0；Windows 本轮此前只有 C# 类型检查（2026-11-02 补本机实跑，见下节）
- [x] Windows 真实 UI 与设置关闭/写回（2026-11-02 本机实跑：`CoreContractCheck` 818 PASS / 1 SKIP / 0 FAIL，`--pv-native-smoke` 30/30 连跑两次、settings 10/10、tools 5/5；三个原先在 Windows 跳过的权限/只读/不可读分支改为实跑）
- [x] Windows AppInstance 二次激活与命令行参数（2026-11-02：新增 `--pv-native-activation-check`，真实拉起第二进程验证转发/定位/退出码，连跑两次 PASS）
- [ ] macOS Finder/Dock 实际文件关联事件
- [ ] iOS 真机外部目录 security scope 生命周期
- [ ] Android 跨 UID 临时授权、系统 persisted grant 撤销、DocumentsUI、真实 USB/MTP/相机写卡与云 Provider

## 2026-11-02 Windows 实跑发现

- **分组重建有可见空窗（已修）**：`PhotoGroupMap.Build` 原先把所有参与文件 `Group = null` 再回填，而列表投影会在后台元数据刷新完成后重跑；并发读取（重复激活判同组、显示名）会读到 `Group == null`，契约自检因此偶发误判「同组伴侣」。现改为先回填新组、再清空本次参与但不在任何组里的文件，读者只会看到「旧组或新组」；自检的稳定等待保留为防御。

- **本地读取不带 `FileShare.Delete`**：`LocalFileRef.OpenReadAsync` 原为 `FileShare.ReadWrite`，Windows 上只要有一个读取句柄（缩略图/EXIF 元数据在途读取），外部删除/改名、以及本应用 `File.Replace` 原子替换都会报共享冲突。契约自检删除 `d.jpg` 时直接抛 `IOException` 中断整轮。改为 `FileShare.ReadWrite | FileShare.Delete` 后，刷新语义（外部改动由下一次刷新接管）才真正成立。
- **三个 Windows 分支从未实现**：`RandomAccessChecks` 的「权限拒绝打开」、`RatingTransactionRegressionChecks` 的「只读 sidecar 预检」与「不可读 sidecar 映射」在 `OperatingSystem.IsWindows()` 分支里直接 Skip。现按平台实现：只读用文件属性、不可读用独占句柄制造共享冲突；两边断言同一套语义。
- **Windows 身份大小写**：「本地路径身份保留文件名」按 POSIX 逐字比较，在 Windows 身份整条大写时误报；改为大小写不敏感比较（`LocalStorageIdentity` 的 Windows 归一不变）。
