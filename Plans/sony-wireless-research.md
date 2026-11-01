# 索尼相机无线连接调研：Creators' App 时代协议与可落地路径

> 目标：给 PhotoViewer 增加 Wi-Fi 连接索尼相机、浏览并下载照片的能力，评估能否顺带把星级写回相机。
> 范围：2020 年后机型（A7C II / A6700 为主，A6100 单独标注）；不含 USB、不含 FTP 上传。
> 结论口径：**A** = 索尼官方文档原文；**B** = 公开项目在真机验证过；**C** = 本文推断，需实测。

## 1. 结论

| 能力 | 结论 | 依据 |
|---|---|---|
| Wi-Fi 发现相机 | **可行**：SSDP 找 `urn:schemas-sony-com:service:DigitalImaging`，读 `dd.xml` 与 `DigitalImagingDesc.xml`（DID） | A |
| 建立会话 | **可行**：TCP **15740** 上的标准 PTP/IP + 索尼 `SDIO_Connect` 三步握手 | A |
| 浏览存储卡 | **可行**：`SDIO_GetContentInfoList (0x923C)`，A7C II / A6700 在官方支持矩阵内 | A |
| 缩略图 / 原图下载 | **可行**：`0x923E` / `0x923D`，分段读、可断点续传 | A |
| 读星级 | **可行**：内容列表里带 `rating` 字段（-1/0/1–5） | A + B |
| **写星级** | **不可行**：官方命令表与 CrSDK 都没有写评分接口；社区亦无公开实现 | A + C |
| 删除相机内照片 | **可行**：`SDIO_DeleteContent (0x9250)`，仅 A7C II / A6700 / A7 IV / A7S III / A7CR | A |
| 遥控拍摄 / 实时取景 | 可行但本任务不需要；实时取景是 HTTP 60152，不是 PTP | A + B |
| 需要 Sony 账号 / 云端密钥 | **不需要**；本地链路无证书、无 token，配对只是相机屏上按一次 OK | A + B |

**一句话**：浏览与下载是"照文档实现"的工程量，不是逆向；星级只能读、不能写，产品上应把相机来源的星级设计成**只读**，本地评分落到 XMP。

## 2. 机型能力

官方 PTP 3 参考（2025-08 版）第 83–84 页支持矩阵，按列逐项核对后的结果：

| 机型 | 内容浏览 `0x923C` | 下载 `0x923D` | 删除 `0x9250` | 读星级 | 备注 |
|---|---|---|---|---|---|
| **ILCE-7CM2（A7C II）** | ✓ | ✓ | ✓ | ✓ | 本任务主力 |
| **ILCE-6700（A6700）** | ✓ | ✓ | ✓ | ✓ | 同上 |
| ILCE-7M4 / 7SM3 / 7CR | ✓ | ✓ | ✓ | ✓ | 同代 |
| ILCE-1 / 1M2 / 9M3 / 7RM5 / FX3 / FX30 / ZV-E1 等 | ✓ | ✓ | ✗ | ✓ | 不能删 |
| ZV-E10M2 | ✗ | ✗ | ✗ | ✗ | 无 T 模式 |
| **ILCE-6100** | 未列入该表 | — | — | — | 2019 年机型，疑走 PTP 2 子集/老 API，须单独验证 |

> A6100 在 Camera Remote Command 的"支持机型"页面出现，但不在 PTP 3 参考的 2020+ 兼容表内。不要把 A7C II 的结论直接套到 A6100。

## 3. 协议全景

```
① 发现        SSDP M-SEARCH → http://<cam>:80/dd.xml → DigitalImagingDesc.xml (DID)
② 连接        TCP 15740（PTP/IP），命令行 + 事件行两条连接
③ 握手        OpenSession → SDIO_Connect(1) → SDIO_Connect(2) → SDIO_GetExtDeviceInfo → SDIO_Connect(3)
④ 会话模式    FunctionMode 0=遥控 / 1=内容传输 / 2=遥控+传输
⑤ 浏览        0x923B 拍摄日期 → 0x923C 内容列表（含 rating）→ 0x923E 缩略图 → 0x923D 原图
⑥ 删除        0x9250（仅部分机型）
```

### 3.1 发现与 DID 门槛

- SSDP 服务名：`urn:schemas-sony-com:service:DigitalImaging:1`；`dd.xml` 的 `SCPDURL` 指向 `DigitalImagingDesc.xml`。
- DID 里决定能力的关键标签：
  - `X_ServerVersion`：≥ 3.00 才支持 PTP-IP（Creators' App 的传输功能要求 ≥ 3.01）。
  - `X_PTP_MediaServerSupport=Enable`：支持内容传输模式。
  - `X_PTP_ContentsTransferSupport=Enable`：支持"遥控+传输"（FunctionMode 2），A7C II / A6700 应有。
  - `X_PTP_PairingNecessity`：`Necessary` 时才需要在相机屏上确认配对。
  - `X_SSH_Support=Enable`：PTP-IP 被 SSH 接管，必须走 22 端口转发，不能直连 15740。
- 连接方式三选一：Wi-Fi Direct（相机开热点）、Wi-Fi ST（相机加入路由器）、有线 LAN。手机连接（Smartphone Connect）只支持 Wi-Fi Direct。
- 已知 IP 时也可以跳过 SSDP：连上后发 `SDIO_GetDeviceDescriptionFile (0x923A)` 取 DID，USB/IP 通用。

### 3.2 PTP/IP 与握手

标准 PTP/IP 分帧：`uint32 length + uint32 packetType + body`，小端；类型 1/2/3/4/5 为初始化，6/7 为操作请求/响应，9/A/C 为数据阶段，8 为事件。

```
InitCommandRequest: GUID(16) + UTF-16LE 友好名 + 0x0000 + 0x00010000
```

官方握手（第 42–43 页）：

```
OpenSession(0x1002) 或 SDIO_OpenSession(0x9210)
SDIO_Connect(0x9201) PhaseType=1
SDIO_Connect(0x9201) PhaseType=2
SDIO_GetExtDeviceInfo(0x9202) InitiatorVersion=0x012C(300)
SDIO_Connect(0x9201) PhaseType=3
```

- `SDIO_GetExtDeviceInfo` 返回**支持的属性码与控制码数组**——客户端应据此决定发什么，而不是照抄机型表。
- 社区实测还多一步 `SDIO_GetVendorCodeVersion (0x9216)`：版本 ≥ 310 时 `SDIO_ControlDevice` 要多带一个参数（本任务用不到控制，但实现握手时应容忍）。
- 配对（Pairing）：相机把主机 GUID 存下来，最多 20 条；社区反馈**配对后很难解绑（需恢复出厂）**，开发期务必用"不配对连接"。

### 3.3 会话模式

| FunctionMode | 含义 | 内容浏览 | 适用 |
|---|---|---|---|
| 0 | Remote Control | ✗ | 遥控 |
| 1 | Content Transfer | ✓ | 相机处于"发送到智能手机"类模式 |
| 2 | Remote Control with Transfer | ✓ | **首选**：一次连接同时遥控+浏览（A7C II / A6700 支持） |

- 模式切换必须关会话重开，不能热切。
- 官方兼容表确认 A7C II / A6700 支持内容浏览；CrSDK 的 RemoteTransferMode（对应 FunctionMode 2）也覆盖这两台。**FunctionMode 2 是否在 PTP 层对它们开放，仍以 DID 的 `X_PTP_ContentsTransferSupport` 与实测为准。**
- 社区经验（A7R V）：**普通遥控会话里 `0x923B/0x923C` 返回 `CameraStatusError 0xA106`**；必须让相机进入传输类模式。这与"FunctionMode 2"一致——模式是相机侧状态，不是客户端发个标志就能解锁。

### 3.4 内容浏览：`0x923C`

参数（官方第 151 页）：

| # | 含义 |
|---|---|
| 1 | CreateTime 低 32 位（Unix ms，UTC 或本地） |
| 2 | CreateTime 高 32 位 |
| 3 | Content Limit（最多返回多少条） |
| 4 | Extra1：bit7–0 = 槽位号（1/2） |
| 5 | Extra2：bit1–0 = 时间类型（0 UTC / 1 本地） |

先调 `0x923B SDIO_GetCapturedDateList`（参数 slot）拿拍摄日期分组，再用日期做游标翻页。日期是"游标"不是精确过滤，翻全卡时要按路径去重。

返回的二进制布局（社区在 A7R V 上逐字节验到末尾，**rating 就在 ContentInfo 里**）：

```
ManagementInformation
  uint16 version (100) + byte[2]
  DbInfo: uint64 dataTime, byte[64] physicalMediaId, byte[64] databaseId, uint32 slotId
uint32 contentCount
contentCount × ContentInfo:
  uint32 contentType, contentId, dirNumber, fileNumber, groupType, groupId, representative
  uint64 creationDateTime, modificationDateTimeUtc, creationDateTimeLocalTime, modificationDateTimeLocalTime
  uint32 rating              // -1 = 不适用, 0 = 未评分, 1..5
  uint32 protectionStatus
  uint32 dummyContent
  uint32 shotMarkCount + uint8[shotMarkCount]
  uint32 fileCount + ContentFile[fileCount]
ContentFile:
  uint16 fileId + byte[2]
  uint32 pathLength + UTF-8 path（如 B:/DCIM/100MSDCF/DSC03424.ARW）
  uint32 fileFormat (14337 JPG / 45313 RAW / 45328 HEIF / 12296 WAV / 47490 MP4)
  uint64 fileSize
  byte[32] umid
  uint32 hasImageParam (+ uint32 width, height)
  uint32 hasVideoParam (+ 变长块，未解码)
  uint32 hasAudioParam (+ 变长块，未解码)
```

- 视频/音频参数块长度未公开：解析到那里应停止，猜长度会让后续记录全部错位。
- `contentType=0` 是列表结束/空位标志。
- 一个 content 可含多个文件（RAW+JPEG/HEIF 成对），由客户端按格式挑，不是相机只给一种。

### 3.5 下载：`0x923D` / `0x923E`

- `0x923D` 参数：contentID、slot+fileID、offset 低/高 32 位、最大字节数（含 endpoint 信息）；响应回时间戳，可用于续传。
- 社区实测 67 MB ARW 约 4.1 s（~16 MB/s）。
- `0x923E SDIO_GetContentCompressedData` 取缩略图/压缩图，适合胶片栏。
- 断点续传：重连后比对 ContentInfoList 的 `dataTime` 与"ContentInfoList 再生成时间"（设备属性），一致则按 UMID 续；不一致则重取列表、丢弃半包。

### 3.6 删除与事件

- `0x9250 SDIO_DeleteContent`：A7C II / A6700 / A7 IV / A7S III / A7CR 支持；受 `protectionStatus` 约束。
- 事件：`SDIE_ContentInfoListChanged (0xC234)` 通知列表变化；`SDIE_OperationResults` 报操作结果。官方建议不要依赖全部 vendor 事件（老机型有丢事件），状态用轮询 `SDIO_GetAllExtDevicePropInfo (0x9209)` 兜底。

### 3.7 BLE 配网（可选，体验用）

社区实测的 GATT 交接（无需抓包）：

```
扫到厂商 ID 0x012D → 连接 → 写 0000CC08 = 01 → 读 0000CC06（SSID）/ 0000CC07（密码）
```

拿到后系统侧加入该热点即可。注意：**BLE 配对可能不可逆**，调试时用"不配对连接"；macOS 上 CoreBluetooth 需要宿主 .app 的 `NSBluetoothAlwaysUsageDescription`，命令行脚本会直接崩。

## 4. 星级：读得到，写不了

### 4.1 读

`ContentInfo.rating` 就是相机内星级（-1/0/1–5），随内容列表一次返回，不需要额外请求。这是"相机上显示几星，我们就能显示几星"。

### 4.2 写：证据链

1. **官方命令表没有写评分**：PTP 3 参考的操作表里，对象属性只有 `GetObjectPropValue (0x9803)` / `GetObjectPropList (0x9805)`，**没有 `SetObjectPropValue (0x9804)`**；`SDIO_*` 全表也没有 Rating 写操作。
2. **官方 SDK 没有写评分**：Camera Remote SDK 102 个 API 中无任何 `Set*Contents*`；`CrContentsInfo::rating` 只读，且只在 RemoteTransferMode 存在；设备属性里的 `Rating1–5` 属于"连拍组首帧标记"，不是文件星级。
3. **开源界没有先例**：`sarnau/sony-camera-protocol`、`peiyuanix/zv-e10-creators-app-patcher`、`alpha-fairy` 的 opcode 表里都没有 rating 写操作；Creators' App 自身只按星级**筛选**传输。
4. **相机星级存在 MakerNote，不在 XMP**：ExifTool 的 Sony 标签表里是 `0x2002 Rating (int32u)`；相机 playback 读的就是它。这也解释了为什么本地写 `XMP:Rating` 相机端多半看不见。

结论：**公开协议内不存在把星级写回相机的路径**。唯一残余可能是未文档化的私有 opcode（例如向 `0x9804` 发 `0xDC8A`），成本低可以试一次，但不要把它写进产品计划。

### 4.3 替代方案（建议直接采纳）

- 相机来源的照片：**星级只读**，UI 上禁用写操作并说明来源（不要做"看起来能点、点了没反应"）。
- 需要评分时：先把照片下载/导入本地库，再走现有 RATING 全来源写入（XMP）。
- 若必须"相机内可见"：只能让用户在相机 playback 里评；或在导入后写 MakerNote `0x2002`（本地文件），但这已超出无线任务，且会与现有 XMP 口径产生第二真源，需单独立项。
- 另一个容易忽略的点：PhotoViewer 目前把相机可见性寄托在 RAW 内嵌 XMP 上（`Design/spec/rating.md`），而 ExifTool 显示机内星级是 MakerNote `0x2002`。**"相机读 XMP"这一假设需要实机验证**，否则本地评分在相机上本来就不显示，与无线功能无关。

## 5. 官方资料与获取

### 5.1 Camera Remote Command（PTP 扩展，首选资料）

- 入口：<https://support.d-imaging.sony.co.jp/app/cameraremotecommand/en/index.html>（中文镜像：<https://www.sonystyle.com.cn/minisite/cross/appcenter/di/camera_remote_toolkit/command.html>）。
- 内容：PTP 命令参考 + 示例代码；覆盖 USB / 有线 LAN / **Wi-Fi**；2024.1.0 起支持 PTP-IP；最新 2.02.00（2026-06）。
- 获取：页面按地区分流到申请表/许可协议，需申请并接受协议（页面未标价）；索尼在 libgphoto2 的公开说明中确认"PTP 命令文档已无 NDA 公开"（<https://github.com/gphoto/libgphoto2/blob/master/camlibs/ptp2/README.Sony>）。
- 已有一份 2025-08 版《Camera Control PTP 3 Reference》全文（723 页）在公开仓库中：<https://github.com/jakkuh/sony-alpha-python/blob/main/docs/Camera%20Control%20PTP%203%20Reference.pdf>。本文的 opcode、参数、兼容表均出自该文档；正式项目仍建议走官方申请。

### 5.2 Camera Remote SDK（高层 C++，备选）

| 项 | 结论 |
|---|---|
| Wi-Fi | 1.12.00（2024-04）起；1.13 起有 RemoteTransferMode（遥控+传输） |
| 浏览/下载/删除 | 有；删除仅 5 机型（7CR/7SM3/7M4/7CM2/6700） |
| 读星级 | 仅 T 模式，`CrContentsInfo::rating` |
| 写星级 | **没有** |
| 语言 | 仅 C++（官方称"C# 版可应要求提供"，需商务渠道） |
| 授权 | 免费、可商用；但**日本官网明示不向个人提供**，实际是 B2B 定向发放 |
| 结论 | 适合"官方合规"路线；对个人项目是商务门槛，且仍解决不了写星级 |

### 5.3 老 Camera Remote API

2013 年的 SSDP + HTTP JSON（:10000）方案，索尼已于 2020-12 宣布随 CrSDK 发布终止支持；仅老机型（A7 初代、A6000 系等），无星级。A6100 若不在 PTP 3 覆盖面内，可能落在这里——但它是"死协议"，不建议新写。

## 6. 开源成果（GitHub）

| 项目 | 语言 | 协议 | 能力 | 价值 |
|---|---|---|---|---|
| [sarnau/sony-camera-protocol](https://github.com/sarnau/sony-camera-protocol) | Python | BLE + PTP/IP | 真机 A7R V：快门、下载 60MP、卡浏览 `0x923B/C/D`、446 个属性、live view；附 1600 行协议文档 | ★★★★★ 目前最完整的可运行参考，含 ContentInfo 逐字节布局 |
| [peiyuanix/zv-e10-creators-app-patcher](https://github.com/peiyuanix/zv-e10-creators-app-patcher) | Smali/Python | Creators' App 3.5.0 | 反编译 App 得到 SSDP/DID、会话模式、存储 ID、opcode 表；附中文协议说明 | ★★★★★ 直接回答"App 怎么连" |
| [frank26080115/alpha-fairy](https://github.com/frank26080115/alpha-fairy) | C | PTP/IP | 长期逆向笔记：配对、属性包格式、机型差异 | ★★★★ 原理与踩坑 |
| [gphoto/libgphoto2](https://github.com/gphoto/libgphoto2) | C | USB/PTP-IP | `ptp.h` 里 Sony `SDIO_*` 常量齐全；生产级实现 | ★★★★ 常量表与错误码 |
| [jakkuh/sony-alpha-python](https://github.com/jakkuh/sony-alpha-python) | Python | PTP/IP + SSH | FX30/A6700 实连；SSH 隧道认证；仓库内含 PTP 3 参考 PDF | ★★★★ 新版机 + SSH 路径 |
| [dykomenko/camera-wifi-transfer](https://github.com/dykomenko/camera-wifi-transfer) | Python | PTP/IP | Windows GUI，列卡下载 ARW/JPEG/视频（a6500） | ★★★ 桌面端产品形态参考 |
| [schorschii/ImagingEdge4Linux](https://github.com/schorschii/ImagingEdge4Linux) | Python | 老 HTTP API | Linux 下从索尼相机 Wi-Fi 下载 | ★★ 仅老协议 |
| [Johnnybyzhang/CrSDK-API-Reference](https://github.com/Johnnybyzhang/CrSDK-API-Reference) | HTML | — | CrSDK 全量 API 文档镜像 | ★★★ 查官方能力边界 |

**没有**发现任何公开项目实现"向相机写星级"。

## 7. 自己拿 Creators' App 并验证

### 7.1 Android（推荐，成本最低）

- 包名 `jp.co.sony.ips.portalapp`；APKMirror / APKPure 有 XAPK/APKM，免费、一般无需登录。
- 版本基线建议 **3.5.0**（patcher 针对该版，XAPK SHA-256 `b0716bf5…f443f09`）。
- 解包与反编译：

```bash
unzip -d work "Creators' App_3.5.0.xapk"      # base.apk + config.*.apk（.so 在 config 里）
jadx -d out --no-res work/base.apk
grep -rn "SDIO_\|15740\|PtpIp\|DigitalImagingDesc\|camera_guide" out/
```

- 类名基本未混淆（`Initializer`、`PtpIpCamera`、`ContentsPushController`），可直接读状态机。
- 重点看：`assets/camera_guide.json`（机型白名单，24h 后可从服务器热更新）、`isUxpSupported()`（DID ≥ 3.01 才开传输）。

### 7.2 抓包（不需要 root 的方案）

PTP/IP 是**明文裸 TCP**，mitmproxy 这类 HTTP 代理看不到，必须 L2/L3 抓：

1. 让相机以"接入点"方式加入 Mac 开的热点（macOS 互联网共享），或相机 Wi-Fi Direct、Mac 直接连 `DIRECT-xxx`。
2. `sudo tcpdump -i bridge100 -s0 -w sony.pcap 'tcp port 15740 or udp port 1900'`。
3. Wireshark 打开，过滤器 `ptpip`；Sony 私有 opcode 需手工对照本文表。

更省事的做法：**不用抓 App**，直接用 §6 的 Python 客户端连一次，打印 opcode 序列，就是"标准答案"。

### 7.3 iOS（不建议）

App Store 的 IPA 是 FairPlay 加密，静态分析不可行；解密需越狱，现代固件无可用越狱。iOS 与 Android 用同一套 PTP/IP 与 opcode，Android 侧做完即可，iOS 只在验证平台差异时才需要。

### 7.4 法律与合规

- 反编译用于互操作有讨论空间，但**分发**索尼代码/二进制、分发改包 APK 明确侵权；参考项目都只发 patch 脚本、要求用户自备原包。
- 商用产品不要内置从 App 提取的私有常量表；用运行时探测（`SDIO_GetExtDeviceInfo` 返回的支持列表）决定发什么更稳。
- 官方文档有许可协议，正式发布前走一次申请。

## 8. 对 PhotoViewer 的接入建议

### 8.1 分层

```
Core/Remote/           新增：PTP/IP 客户端（传输、握手、会话、内容、下载、事件）
  PtpIpTransport.cs      分帧、命令/事件双连接、超时与重连
  SonySession.cs         SDIO 握手、FunctionMode、能力缓存
  SonyContentClient.cs   0x923B/0x923C/0x923E/0x923D/0x9250
Core/Storage/          现有文件源抽象加一个 Remote 实现（列表/缩略图/打开流）
Core/Image/            不动：下载落到本地缓存文件或流，走现有解码
ViewModels/Main/       文件源切换；相机来源的星级只读
```

### 8.2 产品语义

- 相机来源是一个独立"设备"源：按拍摄日期分组，缩略图用 `0x923E`，原图按需 `0x923D` 落缓存。
- 星级显示相机值，**不提供写入口**；需要评分先"导入到本地"。
- 连接状态、相机型号、电量可作为设备信息展示（`0x9209` 属性里有）。
- 删除是危险操作，建议只做"从相机删除"显式命令，且仅对支持的机型显示。

### 8.3 性能与缓存

- 16 MB/s 量级，60MP ARW 约 4–8 s；缩略图必须优先、可并发但限制 2–3 路。
- 下载落盘缓存（按 UMID/内容 ID 命名），二次打开直接读本地；断点续传用 `0x923D` 的 offset。
- 大文件下载要能取消；PTP 事务是串行的，取消后要重置事务 ID/会话。

### 8.4 平台差异

- macOS/Windows：直接 TCP + 系统 Wi-Fi 切换；macOS 可用 CoreBluetooth 做 BLE 取密码（需 .app 权限声明）。
- iOS/Android：可以复用同一 C# 客户端；Android 需 `NEARBY_WIFI_DEVICES`/`CHANGE_WIFI_STATE`，iOS 加入相机热点要用户手动去设置（无法程序化入网）。
- 不建议依赖 Wi-Fi Direct 的"自动弹窗"；统一引导用户手动连 `DIRECT-` 热点最稳。

### 8.5 里程碑

1. **Spike（1–2 天）**：命令行客户端连 A7C II，跑通握手 + `0x923C` + 下载一张 ARW，确认真实 DID 与模式。
2. **只读浏览**：设备源 + 胶片栏 + 缩略图 + 原图缓存。
3. **删除/断点续传/重连**：错误码与相机侧状态机对齐。
4. **产品化**：设置页入口、连接引导、失败文案、四端验收。

## 9. 待验证清单（按优先级）

| # | 实验 | 判据 |
|---|---|---|
| 1 | 用 `sarnau/sony-camera-protocol` 或自写脚本连 A7C II（Wi-Fi Direct，Smartphone Connect） | 能完成三步握手并拿到 `0x923C` 列表 |
| 2 | 读 DID：`X_PTP_ContentsTransferSupport` 是否为 `Enable` | 决定能否用 FunctionMode 2（遥控+浏览同连） |
| 3 | 在 PC Remote（接入点）模式下试 FunctionMode 2 + `0x923C` | 成功则 UX 最好：相机连路由器、可自动重连 |
| 4 | 对比 Smartphone Connect（FunctionMode 1 + `0x9212`） | 作为回退路径 |
| 5 | 读一张相机内已评星的照片，核对 `rating` 与相机显示一致 | 确认字段语义（-1/0/1–5） |
| 6 | 对某个对象句柄发一次 `SetObjectPropValue (0x9804)`，属性 `0xDC8A`（Rating）= 4 | 预期 `Operation_Not_Supported`；若 OK，再验证相机 playback 是否显示 4 星 |
| 7 | A6100 单独测试 | 是否支持 PTP-IP / 内容浏览；不支持则明确"仅 A7C II/A6700" |
| 8 | 抓一次 Creators' App 的完整会话 | 仅作对照，确认机型差异与可选 opcode |

## 10. 风险

- **机型/固件差异**：DID 标志、存储 ID、属性包格式都可能不同；不要假设 A7C II 的结论对 A6700 逐字节成立。
- **模式是相机侧状态**：客户端无法凭会话参数解锁内容传输；UI 必须引导用户切对相机菜单。
- **配对后难解绑**：调试一律"不配对"；正式产品要设计解绑说明（恢复出厂）。
- **Wi-Fi Direct 单客户端**：手机/Mac 只能一个连相机热点；调试时先断开手机。
- **SSH 模式**：接入点 + 访问认证开启时 15740 不可直连，需要 SSH 转发（.NET 需引入 SSH 客户端），建议首版不支持，文档写明关闭访问认证或改用 Wi-Fi Direct。
- **官方 SDK 的商务门槛**：不向个人提供；若走官方路线需先确认资格。
- **文档许可**：公开镜像只用于研发对照，正式发布走官方申请。

## 11. 参考

- Sony Camera Remote Command：<https://support.d-imaging.sony.co.jp/app/cameraremotecommand/en/index.html>
- Camera Control PTP 3 Reference（2025-08，公开镜像）：<https://github.com/jakkuh/sony-alpha-python/blob/main/docs/Camera%20Control%20PTP%203%20Reference.pdf>
- Sony Camera Remote SDK：<https://support.d-imaging.sony.co.jp/app/sdk/en/index.html>
- CrSDK API 文档镜像：<https://github.com/Johnnybyzhang/CrSDK-API-Reference>
- libgphoto2（Sony opcode / PTP-IP）：<https://github.com/gphoto/libgphoto2>
- sarnau 协议文档：<https://github.com/sarnau/sony-camera-protocol/blob/main/SONY-CAMERA-PROTOCOL.md>
- Creators' App 逆向笔记：<https://github.com/peiyuanix/zv-e10-creators-app-patcher/blob/main/docs/zh-CN/01-how-it-works.md>
- alpha-fairy 逆向笔记：<https://github.com/frank26080115/alpha-fairy/blob/main/doc/Camera-Reverse-Engineering.md>
- ExifTool Sony 标签（Rating 0x2002）：<https://exiftool.org/TagNames/Sony.html>
- Sony 星级存储讨论：<https://photo.stackexchange.com/questions/135587/finding-sony-star-ratings-in-image-metadata>
