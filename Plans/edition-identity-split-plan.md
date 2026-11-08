# 双产品四平台分发与包身份整改方案

> 状态：统一 edition 身份来源、四端包身份、发布脚本与扫描器已落地。Android 双 APK、macOS 双 app/DMG、iOS 双模拟器 app 验收通过，macOS 同时运行及 iOS 一次性模拟器并存安装/同时运行通过；Windows 完成 MSIX 静态清单、66 项扫描自检与两版类型检查。Android 真机与 Play/AAB、Windows 真实 MSIX 打包安装、Apple 分发签名/TestFlight、四端更新卸载及桌面共享数据仍待后续验收或实施。
> 关联：[发行版本设计规范](../Design/spec/editions.md) · [分发包体积优化计划](distribution-package-size-optimization-plan.md) · [DEV.md 打包发布](../DEV.md)
> 核心决策：**两个独立产品 × 四个平台 = 八条独立发布轨道**；**桌面端显式共享适合共用的产品族数据，其余状态独立**。

## 1. 目标形态

`PhotoViewer` 与 `PhotoViewer AI` 是两个并列产品，不是同一应用的替换安装变体，也不存在标准版 ↔ AI 版的应用内切换或升级关系。

每个产品分别拥有 Windows、macOS、iOS/iPadOS、Android 发行物。平台、架构和渠道只改变发行物，不改变产品身份。两版必须能够：

- 在同一设备并存安装，桌面端能够同时运行。
- 分别启动、二次激活、更新和卸载，互不误伤另一版。
- 分别持有签名资料、商店记录、安装身份、单实例身份和私有数据。
- 在桌面端通过明确的产品族共享区复用通用设置和分析数据，而不是通过相同包名获得数据。

历史安装、旧商店记录和旧数据的迁移是发布执行任务，不得反向改变本方案的身份与数据边界。

## 2. 统一产品身份

| 维度 | 标准版 | AI 版 |
|---|---|---|
| 产品显示名 | `PhotoViewer` | `PhotoViewer AI` |
| 内部产品键 | `photoviewer.standard` | `photoviewer.ai` |
| 构建版本键 | `standard` | `ai` |
| 全平台规范应用 ID | `cc.xiaojh.PhotoViewer` | `cc.xiaojh.PhotoViewer.AI` |
| 分发文件名前缀 | `PhotoViewer` | `PhotoViewer-AI` |

身份规则：

1. **版本差异进入应用 ID，平台不进入应用 ID。** macOS 不使用 `.Mac`，其他平台也不添加平台后缀。
2. 标准版是默认产品，使用无后缀 ID；AI 版固定使用 `.AI`。
3. 用户可见名称使用空格；机器文件名使用横线。包身份不使用 `PhotoViewer-AI` 作为显示名。
4. 工程名、命名空间、程序集名和内部可执行文件名不承担产品身份，不得据此派生商店或安装身份。
5. 正式渠道、CPU 架构和构建号不编码进应用 ID。非公开 Debug 包如需与正式包并存，可统一使用 `<应用ID>.Debug`，但不进入公开发行矩阵。

## 3. 四平台标准发行形态

| 平台 | 每个产品的主发布形态 | 身份与安装契约 |
|---|---|---|
| Windows | 签名的 **MSIX** | 独立 `Identity.Name`、Package Family 和 AUMID；两版并存、同时运行、分别更新 |
| macOS | Developer ID 签名并公证的 **DMG，各含一个 `.app`** | `CFBundleIdentifier` 使用规范应用 ID；Launch Services 注册为两个应用 |
| iOS / iPadOS | 两个独立的 App Store Connect App，经 **TestFlight / App Store** 分发 | 两个 Bundle ID、两个 App 记录、独立描述文件、测试和上架轨道 |
| Android | Play 渠道使用 **AAB**；直接分发使用签名 **APK** | 两个 `applicationId`、两个商店条目或下载项、独立沙盒 |

补充边界：

- Windows 便携单文件 EXE 如继续提供，只是附加发行形态，必须沿用对应产品的身份、单实例和数据规则，不得形成另一套更新或存储语义；主分发体系以 MSIX 为准。
- iOS IPA 是归档和上传载体；开发签名直装 IPA 属于开发验收，不是公众主发行渠道。
- macOS 与 iOS 使用相同规范应用 ID 是跨平台同一产品的表达，不因此共享沙盒或应用私有状态。
- 两个产品使用同一发布者团队、证书或密钥是正常情况；发布者身份不代替产品身份。

建议分发命名：

```text
<PhotoViewer|PhotoViewer-AI>-<版本>-<平台>-<架构>.<msix|dmg|ipa|aab|apk>
```

文件名只用于归档和下载，不参与安装身份判定。

## 4. 数据边界与桌面共享例外

### 4.1 总体分层

1. **产品身份与私有状态**：四端均按两个产品独立。
2. **产品族共享数据**：仅桌面端显式建立，且只容纳适合共用的数据。
3. **用户数据**：照片及写入照片中的评分属于用户，两版均可按系统授权访问，不视为应用私有共享状态。
4. **移动端沙盒**：iOS 与 Android 的设置、数据库、缓存和日志各自独立，不照搬桌面共享例外。

### 4.2 桌面数据分类

| 数据类别 | 桌面端策略 |
|---|---|
| 文件格式、布局、控制栏、快捷键、EXIF、评分、通用预览与缓存设置 | 共享通用设置 |
| 胶片栏、分析栏、控制栏等两版共有界面状态 | 可共享，逐项登记 |
| CV 特征及可复用分析结果 | 共享 |
| DINO 特征数据库 | 可位于共享物理库；仅 AI 版读取、写入和清理 DINO 数据，标准版不得触发 DINO 任务 |
| 相似聚类参数、DINO 即时计算、相似面板状态 | AI 版私有 |
| 窗口位置、当前会话、临时任务、后台提取状态 | 各自私有 |
| 日志、崩溃报告、诊断文件 | 各自私有 |
| 单实例键、激活目标、更新状态、包数据 | 必须独立 |

### 4.3 共享实现的硬规则

- 建立显式的产品族共享存储区，不依赖相同 Bundle ID、相同 MSIX 身份或相同沙盒获得共享。
- 通用设置与 AI 专属设置分文档存储；标准版不需要通过保留不可见 AI 字段维持兼容。
- 共享配置需要 schema 版本、原子写入、跨进程锁或字段级合并，并响应另一进程的外部变更；禁止用“最后关闭者覆盖整份 JSON”作为双开语义。
- 共享 SQLite 使用 WAL、忙等待和迁移锁；清除 DINO 特征只能由 AI 版执行，标准版不得删除、重建或清空共享库中的 AI 数据。
- 安装、更新、卸载任一产品不得隐式清除产品族共享数据，也不得改变另一产品的安装与运行状态。

## 5. 构建、版本与发布模型

构建输入统一为：

```text
edition   = standard | ai
platform  = windows | macos | ios | android
channel   = store | direct | testflight
arch      = x64 | arm64
config    = debug | release
```

- 外部构建入口以 `PhotoViewerEdition=standard|ai` 为产品事实来源；DINO 编译能力、依赖和资产由 edition 推导。`PhotoViewerEnableDino` 不再充当身份来源，过渡期也只能作为派生值，禁止与 edition 分别手工指定。
- 八条发布轨道的输出、还原资产和中间目录完全隔离；每份产物必须从实际包核对身份，不能仅从构建参数推断。
- 八条轨道可以共享同一产品版本号。构建号按各产品、各商店的规则独立递增，标准版与 AI 版可以使用相同构建号。
- App Store Connect 以 Bundle ID、版本号和构建字符串组合识别构建；删除当前跨 edition 构建号互斥限制，保留每个 App 内的构建号单调递增和重复上传拒绝规则。依据：[Apple App information](https://developer.apple.com/help/app-store-connect/reference/app-information/app-information/)。
- 每份产物生成机器可读清单，至少包含 `productId`、`packageId`、`edition`、`platform`、`channel`、`arch`、`version`、`build`、签名信息和 SHA-256。

## 6. 实施工作项

### 6.1 统一身份来源与发布清单

- 在 `Directory.Build.props` 集中定义 edition、规范应用 ID、显示名和分发文件名前缀，并提供平台构建实际消费的属性。
- 各头工程只消费统一映射，不在 `.sh`、`.ps1`、Python 扫描器和 Tasks 中各写一套互相可能漂移的产品判断。
- 发布脚本把 edition 对应的期望身份显式传给扫描器，报告记录实际值与期望值。

### 6.2 Windows

- 将主发布目标调整为两个独立 MSIX 包，包身份分别为 `cc.xiaojh.PhotoViewer` 与 `cc.xiaojh.PhotoViewer.AI`，显示名保持 `PhotoViewer` / `PhotoViewer AI`。
- 分离 AUMID、`AppInstance` 单实例键、激活目标和私有运行状态；同一版仍保持单实例，跨版不得互相转发激活。
- 更新 `PhotoViewer.Windows.csproj`、MSIX 清单、发布脚本和 `Tools/check-windows-package.py`，以实际包身份、版本和资源为验收依据。
- 便携 EXE 若保留，沿用同一产品键和共享/私有数据分类，不另建第三套身份。
- 接入桌面共享存储及双进程并发规则，不能仅拆分单实例键后继续无协调地共写设置文件。

### 6.3 macOS

- `PhotoViewer.Mac/Info.plist` 继续只作源模板；在 edition 隔离的中间副本中写入 `cc.xiaojh.PhotoViewer` 或 `cc.xiaojh.PhotoViewer.AI`，取消 `.Mac` 身份后缀。
- `PhotoViewer.Mac/publish.sh` 与 `Tools/check-mac-package.py` 按 edition 核对 Bundle ID、显示名、签名、公证和实际 DMG 内容。
- 通用设置使用显式产品族共享存储；不得把 `NSUserDefaults.StandardUserDefaults` 的同键读写误当成跨 Bundle ID 共享。AI 专属设置与诊断日志留在各产品私有域。

### 6.4 iOS / iPadOS

- 在 `PhotoViewer.iOS.csproj` 生成的中间 `Info.plist` 中写入对应规范 Bundle ID。
- 在 Apple Developer 后台建立两个 App ID 及开发/分发描述文件，在 App Store Connect 建立两个 App 记录；entitlement 按实际能力配置。
- `ExportOptions` 维护两个 Bundle ID 到描述文件的映射；`publish-testflight.sh` 按 edition 选择签名资料。
- 删除 `publish-testflight.sh` 的跨 edition 构建号冲突检查，更新 `validate-testflight-archive.sh` 和 `check-testflight-device.sh`，使其按 edition 校验实际 Bundle ID。
- `Tools/RefreshIosProfile` 支持两个产品的开发和分发资料；`.vscode/tasks.json` 的构建、安装、启动和设备巡检使用对应 ID。
- `Tools/check-ios-package.py` 按 edition 推导默认期望 ID，并覆盖错 ID 的反向自检。

### 6.5 Android

- `PhotoViewer.Android.csproj` 按 edition 生成两个 `ApplicationId`，删除同 ID 替换安装的规则。
- Debug `DocumentsProvider` 及其他系统组件 authority 必须由实际包名派生，避免双包组件冲突。
- 更新 `.sh` / `.ps1` 发布脚本、`.vscode/tasks.json`、ADB 启动/安装/取报告命令及验收代码中的包名来源。
- `Tools/check-android-package.py` 按 edition 推导默认期望 ID，并覆盖错 ID、错 ABI 和错能力资产的反向自检。
- 两版使用各自沙盒；不得为了复用设置或数据库重新引入共享 UID 或跨包私有目录访问。

### 6.6 桌面共享数据

- 建立统一的共享数据目录与私有数据目录映射，登记每一项设置的归属，禁止仅以“桌面共享”笼统覆盖全部状态。
- 拆分通用设置与 AI 专属设置，补充跨进程原子写、锁/合并、外部变更通知和损坏恢复。
- 为共享数据库补充 WAL 并发、迁移锁、AI 专属表所有权及标准版只读边界验收。

### 6.7 规范与长期文档同步

- 重写 `Design/spec/editions.md` 的包身份、数据边界和双版本验收条款，删除替换安装、同包身份和移动端跨版共享状态的旧前提。
- 更新 `DEV.md`、四端 `AGENTS.md` 和 `distribution-package-size-optimization-plan.md`，使发布矩阵、命令、扫描规则和本方案一致。
- 长期文档只保留最终规则，不堆叠本次整改过程；发布执行与迁移记录另留本计划或对应验收文档。

## 7. 验收门

### 7.1 包体与身份

- 八条轨道分别扫描实际产物：产品 ID、平台、架构、版本、签名和能力资产全部符合矩阵。
- 扫描器正反自检必须覆盖标准版误用 AI ID、AI 版误用标准版 ID、错平台、错架构及模型/推理资产错误。
- 每份发布清单中的包哈希与实际最终分发文件一致。

### 7.2 安装与生命周期

- 四端分别验证两版并存安装、两个应用入口、各自冷启动和卸载互不影响。
- Windows 与 macOS 验证两版同时运行；同一版的第二次激活只转发给同版主实例，跨版不转发。
- 每个产品分别执行一次更新，证明更新不会替换或修改另一产品。

### 7.3 桌面共享与隔离

- 两版同时修改通用设置时不丢字段、不整档回退，另一版能按设计看到共享变更。
- AI 专属设置、窗口会话、日志和后台任务不进入另一版。
- 共享数据库并发读写和迁移可重复执行；标准版不能清理或生成 DINO 数据，AI 版清理不影响通用设置。

### 7.4 商店与渠道

- iOS 两个 App 记录分别完成真实 TestFlight 上传与安装；两版使用相同版本号和构建号也必须有明确验证记录，archive-only 不替代渠道验收。
- Android 如启用 Play，两个条目分别完成上传；直接分发 APK 与商店 AAB 的 applicationId 必须一致。
- Windows MSIX 分别安装和更新，核对 Package Family、AUMID 与文件关联目标。

## 8. 建议实施顺序

1. 先落地统一身份模型、规范文档和扫描器，冻结八条发布轨道。
2. 完成 Android 与 macOS 包身份及实际包体验证。
3. 完成 iOS 外部 App ID、描述文件和 App 记录，再打通双 TestFlight 轨道。
4. 完成 Windows MSIX 主发布形态及双实例/激活拆分。
5. 落地桌面共享数据分类、并发协调和数据库所有权，再开放双开验收。
6. 最后执行四端安装、更新、卸载、共享数据和商店渠道总验收。

## 9. 明确排除的错误解法

- 继续让两版共用应用 ID，只靠显示名、文件名或构建号区分。
- 为 macOS 保留 `.Mac` 或为其他平台发明平台专属包身份后缀。
- 用同包覆盖安装、共享 UID、共享沙盒或共享单实例键实现数据复用。
- 要求标准版和 AI 版的构建号互相避让。
- 把桌面共享例外扩大成全部设置、日志、任务和运行状态混用。
- 为了沿用历史安装或旧商店记录，改变本方案已经确定的产品身份与数据边界。
