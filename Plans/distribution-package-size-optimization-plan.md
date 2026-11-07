# 分发包体积优化计划

> 状态：macOS P0/P1 本地实施与验收完成；iOS 双版本分拆、本地 IPA 优化和模拟器验收完成，真机/分发渠道验收未覆盖；Android P1 本地实施与模拟器验收完成，真机/正式归档未覆盖；Windows 待实施（2026-11-08）
> 关联：[发行版本设计规范](../Design/spec/editions.md) · [DINOv3 选片 Plan-1](dinov3-photo-ranking-plan-1.md) · [DINOv3 ONNX 构建说明](../DEV.md#dinov3-onnx-模型)
> 范围：四平台模型与推理运行时分发、包体清理、发布矩阵。模型结构、预处理、特征 schema 与聚类算法不在本计划内变更。标准版入口、CV 保留范围、独立即时计算门控与版本切换兼容以发行版本设计规范为准。早期计划中关于模型打包、许可复核和在线下载的表述，以本计划的现行决策为准。

## 0. 决策

1. **首要发布两个完整、独立、可离线使用的版本**：
   - **标准版（无 DINO）**：不包含模型、ONNX Runtime、DirectML 及 DINO/相似聚类入口，不只是隐藏 UI 或删除模型文件。
   - **DINO 内置版**：继续内置当前固定 ONNX，完整保留相似聚类、DINO 诊断和本地推理能力，下载后无需联网。
2. **在线下载是远期选择，不进入首轮实施**。本轮不建设下载器、模型清单服务、自动更新或后台网络任务，也不为此提前增加网络权限和发布合规事项。
3. 两个版本是用户二选一的发行形态；不得因自动更新或渠道配置在用户不知情时切换版本。切换版本不得清空照片数据库、设置或既有 DINO 特征缓存。
4. 首轮先完成双版本和明确的误打包清理，再做 ABI、压缩与依赖裁剪。不得把未经验证的深度裁剪体积估算写成发布承诺。
5. **Android 只发布 arm64-v8a 的 `-android-arm64` 包**，构建时排除 x86_64 原生资产，不提供 x86_64 安卓包。x86 模拟器和 x86 设备不属于发布支持面。

## 1. 调研基线

### 1.1 统计口径

- Windows、Android、iOS 取 0.9.0 实际发布包；macOS 取最新现有 0.8.8 DMG。
- Windows 通过 Release 资产的 .NET single-file bundle 清单统计；APK/IPA 实际解包；DMG 挂载后统计 `.app`。
- 下载包大小与包内未压缩大小分开记录。Android 未压缩总量不等于安装后的实际磁盘占用；iOS Symbols 不进入用户 `.app`。
- 下表“移除 DINO 后”同时移除模型和 ONNX Runtime/DirectML。Windows/macOS 的部分压缩量由 bundle 清单或同算法压缩测得；iOS 静态链接在主执行文件中，只能给区间。

### 1.2 当前模型契约

| 项 | 当前值 |
|---|---|
| 文件 | `dinov3_vits16.onnx` |
| 原始大小 | 86,594,677 bytes（82.58 MiB） |
| SHA-256 | `bcba38490ec2eded4a240751bebac198903daba7263871e8fee3aa39b03352e8` |
| `ModelId` | `dinov3_vits16_f32_518_v2` |
| 输入 | 518×518、RGB、opset 17 |
| 输出 | `cls_embedding` + `patch_tokens` |
| 特殊处理 | DirectML 所需 Reshape 后处理；权重内嵌于单一 ONNX |

官方原始权重和社区 ONNX 不能直接替代当前文件。模型外置时也必须分发同一导出契约、通过一致性与四端推理验收的产物。

### 1.3 当前包体与移除 DINO 的测算

单位均为 MiB；“仅移模型”保留推理运行时，可作为远期模型外置的体积基线。

| 平台 | 当前下载包 | DINO 在下载包中占用 | 仅移模型 | 完整移除 DINO |
|---|---:|---:|---:|---:|
| Windows | 222.28 | 82.58，未压缩内嵌 | 139.70 | 124.14；叠加深度优化估算 86–96 |
| macOS | 114.09 | 约 76.6 | 约 37.5 | 约 23–24 |
| Android 双 ABI | 169.00 | 82.58，不压缩 | 86.42 | 66.38 |
| Android arm64 单包 | 128.72（由现包扣除 x86_64 测算） | 82.58 | 46.14 | 约 36.96 |
| iOS TestFlight IPA | 132.31 | 76.55 | 55.76 | 约 47–50 |
| iOS 本地 IPA（不带 Symbols） | 121.64 | 76.55 | 45.09 | 约 36–39 |

iOS 估算尚未计入 Symbols 随 ONNX 移除而同步缩小的收益，不作为精确预算。无 DINO 首轮仍以实际构建结果为准。

### 1.4 主要组成结论

- **共享 Core+ViewModels 不是大头**：Windows 原始 1.41 MiB；macOS 1.37 MiB；Android 双 ABI AOT 7.74 MiB；iOS 可独立识别的 IL/AOT 数据 2.15 MiB，实际 AOT 代码另在主执行文件内。
- **Windows**：.NET Runtime/BCL/Desktop 原始 154.97 MiB、压缩后 62.21 MiB；WinUI/Windows App SDK/WinRT 原始 93.18 MiB、压缩后 31.25 MiB；ONNX+DirectML 压缩后 15.56 MiB。
- **macOS**：CoreCLR+BCL 原始 23.48 MiB；SkiaSharp 14.51 MiB；ONNX 项合计 47.40 MiB，但含下述误带 Windows DLL。
- **Android**：双 ABI 的 ONNX Runtime 原始 55.62 MiB、压缩后 20.03 MiB；HEIF/RAW 原生库原始 42.23 MiB；.NET/Mono/BCL/IL store 原始 48.69 MiB。
- **iOS**：主执行文件原始 124.48 MiB、IPA 内 27.67 MiB，包含 Mono AOT、ONNX Runtime 与平台原生代码；外置 Symbols 原始 76.58 MiB、IPA 内 10.67 MiB。

### 1.5 已确认的清理项与独立优化轴

| 项 | 实测大小/收益 | 处理口径 |
|---|---|---|
| macOS 误带 `onnxruntime.dll` | 13.56 MiB 原始；预计 DMG 省约 4.9 MiB | 已确认是 Windows ARM64 PE DLL，macOS 不可加载；从 Apple 构建资产中排除，保留真正的 `libonnxruntime.dylib` |
| Windows 重复 `pvhevc.dll` | 重复项压缩后 0.76 MiB | 根目录与 `NativeLibs/win-x64/` 各一份；保留运行解析所需的一份并做便携验收 |
| Android 双 ABI | x86_64 占下载包 40.28 MiB；arm64 占 38.03 MiB | 仅保留并发布 arm64-v8a，构建排除 x86_64；不提供 x86_64 安卓包 |
| Windows/Android 模型未压缩 | 无损压缩约省 6.03 MiB | 仅 DINO 内置版候选；保持解压后的模型字节和哈希不变 |
| iOS Symbols | IPA 内 10.67 MiB | TestFlight 保留符号化材料；本地直装包可不携带，不通过 strip 规避现有 AOT 启动约束 |
| Windows WPF/WinForms | 清单合计压缩后约 30.72 MiB | 深度裁剪候选，须先证明 DirectN/WicNet/WinUI 反射依赖不受影响 |
| Windows `aom.dll` + `libx265.dll` | 压缩后约 4.62 MiB | 仅在自定义 libheif 保持当前格式支持并通过解码验收后移除 |
| Windows App SDK AI/Widgets | 压缩后约 2.27 MiB | 深度裁剪候选，不与首轮双版本混在一起验证 |

## 2. 双版本构建设计

### 2.1 构建开关必须同时控制四层

新增统一、显式的 DINO 构建能力开关，四端发布脚本均显式传值，不依赖各工程不同的隐式默认值。开关至少控制：

1. **共享托管实现**：模型资源、ONNX 托管包、DINO 提取/缓存/索引及相似聚类实现按同一能力边界编译和链接。
2. **平台原生依赖**：Windows 的 ORT/DirectML、macOS 的 ORT 动态库、Android 的 ORT/JNI 库、iOS 的静态 ORT 链接和符号导出随实现一起移除或保留。
3. **能力注册与 UI**：无 DINO 版不注册 DINO 服务，不显示相似聚类、DINO 诊断及依赖 patch token 的入口，不出现空面板或“缺少模型”错误。
4. **打包内容**：无 DINO 版必须经产物检查证明没有模型和推理二进制，不能只依据工程配置判断。

无 DINO 版可以保留不依赖 ONNX 的照片统计、EXIF、评分、常规图像分析和数据库 schema。既有 `photo_features` / `photo_patches` 数据只读保留，不因无 DINO 构建启动而删除。

### 2.2 发布矩阵与命名

新发布统一使用 `PhotoViewer`（无 DINO 标准版）和 `PhotoViewer-AI`（DINO 内置版）两个产品名，格式为 `<产品名>-<版本>-<平台后缀>.<扩展名>`；历史包不改名。

| 平台 | 平台后缀 | 标准版 | DINO 内置版 |
|---|---|---|---|
| Windows | `-win-x64` | `PhotoViewer-<版本>-win-x64.exe` | `PhotoViewer-AI-<版本>-win-x64.exe` |
| macOS | `-mac-arm64` | `PhotoViewer-<版本>-mac-arm64.dmg` | `PhotoViewer-AI-<版本>-mac-arm64.dmg` |
| Android | `-android-arm64` | `PhotoViewer-<版本>-android-arm64.apk` | `PhotoViewer-AI-<版本>-android-arm64.apk` |
| iOS | `-ios` | `PhotoViewer-<版本>-ios.ipa` | `PhotoViewer-AI-<版本>-ios.ipa` |

签名状态由发布验收和校验文件证明，不写进文件名。Android 固定使用 `-android-arm64`，不生成 `-android-x86_64` 产物。

- 桌面和 Android 可直接提供两个下载项；同包名/同 Bundle ID 的版本按替换安装处理，不要求同机共存。
- **iOS 发布渠道需要单独裁定**：TestFlight 可用不同 build number 分批验证两个变体；App Store 同一 Bundle ID 不能当成两个并列 App 提供选择。若必须长期并列上架，需要独立 Bundle ID、App 入口和签名资料，这属于另一项发布决策，不在首轮默认承诺内。
- 更新渠道按变体隔离，禁止标准版自动更新成带 82.58 MiB 模型的 DINO 版，反之亦然。

## 3. 分阶段路线

### P0：包体卫生收尾

- 修复 macOS Windows ARM64 ORT 误打包。
- 修复 Windows `pvhevc.dll` 重复打包。
- 将包内文件清单、大小和 SHA-256 纳入发布证据，避免同类问题靠人工解包才发现。

### P1：双版本构建与发布

- 收敛 DINO 能力边界，接入统一构建开关。
- 四端生成标准版和 DINO 内置版；DINO 内置版保持当前固定模型与行为。
- Android 两个变体均只构建和发布 arm64-v8a，APK 中不得残留 `lib/x86_64/`。
- 完成产物扫描、基础功能、DINO 功能、升级切换和体积回归验收。
- 桌面/Android 建立并列下载项；iOS 先明确 TestFlight 双变体流程，再决定 App Store 发行形态。

### P1-Mac：本地实施与验收结果

- `PhotoViewerEnableDino` 同时控制共享实现、Mac 原生依赖、模型/License、UI 和打包内容。Mac 标准版 Tasks 显式传 `PhotoViewerEnableDino=false`，AI 版显式传 `true`，裸构建缺少或传入非法值时报错；iOS 推广见下节，Windows / Android 当前仍默认 AI 行为，待各自实施。
- 标准版移除 DINO 提取/缓存/索引、相似聚类、patch 派生及 ONNX/MathNet 引用，保留 CV 分析、抖动徽标和三瓦片 CV 诊断。DINO/CV 即时计算已拆成独立设置；旧文档缺 CV 字段时继承原组合值。
- `publish.sh --edition standard|ai|both` 发布独立 `.app` 与 DMG；`--local` 只运行 adhoc 构建、扫描及只读装载验证，不写正式归档。`bin/obj` 按变体隔离，源 `Info.plist` 构建前后无改动。扫描保存身份/版本/构建号、文件大小及 SHA-256，并拒绝标准版推理资产、AI 必需托管/原生组件或模型/许可证缺失；正反自检 9 项通过。`check-release.py` 校验双产品平台矩阵，兼容未迁移平台历史包名并拒绝未支持架构。
- 首轮拆分测得标准版 `.app` 49.81 MiB、DMG 22.01 MiB；AI `.app` 167.95 MiB、DMG 109.16 MiB。正确性修复后的最终本地 Release/adhoc 实测分别为 **49.91 / 22.13 MiB** 与 **168.12 / 110.12 MiB**；这些是本地验证包，不是正式签名发布体积。包扫描与装载证据位于 `artifacts/acceptance/mac-editions/*-local-*.json`。
- 双版本 layout / analysis / tools / settings / input / toolbar / filmstrip / faults / texture-cache 及 AI similarity 共 19 套原生回归通过。新增真实 P3/F16 与 EXIF 6 输入的 CV 数值对拍、无库 DINO-only 实际推理比例、库基线保留；Core 覆盖部分失败重试、成功侧复用和切图代际。AI → 标准 → AI 跨进程设置/视图状态恢复，CLS、patch、CV blob SHA-256、模型标识、CV spec/尺寸保持一致。证据位于 `artifacts/acceptance/mac-editions-fixes/`。
- `CoreContractCheck`：AI **1181 PASS**、标准版 **1165 PASS**，均 0 FAIL、1 个既有 Windows 平台用例 SKIP；`SettingsDefaultsCheck`、68 项色彩契约与 Windows 类型检查通过。Web **344** 个单测通过，内置浏览器两版非 demo `check=all` 均 **56/56**；`?demo&check=all` 仍有既有 MENU-03 窗口/页面坐标混用断言问题，不在本任务改动范围。实际 Developer ID 签名、公证、stapler/Gatekeeper 和正式归档未执行，仍由正式发布任务验收。

### P1-iOS：实施与本地验收

- iOS 构建显式传 `PhotoViewerEnableDino=false|true`，头工程与共享工程按 `standard/ai` 隔离恢复资产、中间文件和输出；缺参/非法值的真实 restore 均被拒绝。构建从模板生成中间目录 `Info.plist`，源模板哈希保持不变；Bundle ID 与可执行文件名不变，显示名分别为 `PhotoViewer` / `PhotoViewer-AI`。
- 标准版移除模型、ORT 托管/静态原生链接与 DINO/相似入口，保留四项筛选条、CV 分析与三瓦片 CV 诊断；AI 版保留五项筛选条、相似聚类和五瓦片诊断，两侧即时计算设置独立。
- 新增 `publish.sh --edition standard|ai|both [--local]` 和实际产物扫描。扫描核对身份、版本/构建号、arm64、文件体积/SHA-256、模型契约与许可证，以及主程序已定义 ORT 符号；18 项正反自检通过。本地直装 IPA 不携带顶层 `Symbols/`，dSYM/Symbols 单独保留，不 strip、不改 AOT。TestFlight 必须显式指定单变体，提供 `--archive-only`，staging 校验通过后才替换旧归档。
- iOS 26.3 隔离 iPad 模拟器的标准版 photo/layout/input/analysis/tools/faults/gesture/texture-cache 八套、AI 版对应八套及 similarity/真实 DINO 共十八套已通过；另有两版严格能力检查，含 AI 四开关组合真实出图、CV 独立参考对拍与现算不写库。DINO 故障注入已改为只断言 DINO 失败，CV 一侧独立成功，不沿用组合门控的旧断言。
- 共享核心契约：标准版 1165 PASS、AI 版 1181 PASS，均 0 FAIL、1 个既有平台用例 SKIP。双版 Debug 和开发签名 Release/IPA 构建及扫描通过。最终标准版 `.app` **122.55 MiB**、IPA **33.37 MiB**；AI 版 `.app` **253.04 MiB**、IPA **122.09 MiB**，标准版下载包较 AI 版小 **72.67%**。包内 ORT 符号分别为 0 / 10，IPA 主执行文件与构建产物哈希相同，两版 IPA 均无顶层 Symbols，dSYM 保留；这些是本地开发签名验证包，不是 TestFlight 下载体积。证据在 `artifacts/acceptance/ios-editions/`，本地 IPA 在 `artifacts/scratch/ios-local-publish/`，未写入正式发布归档。
- 真实 AI → 标准 → AI 覆盖安装三阶段通过：使用隔离键的 `AppleSettingsStore` / `AppleViewStateStore` 与同一验收数据库，标准版保留 DINO/CV 设置及展开态但不恢复面板，修改通用设置后 AI 版恢复；CLS、patch、CV 的 SHA-256、model_id、CV spec/尺寸逐项一致。初次失败来自模拟器 cfprefsd 延迟落盘与旧报告误判，验收现等待实际偏好文件持久化并核对当前阶段 START/PASS；未修改生产存储行为。证据在 `artifacts/acceptance/ios-editions/roundtrip-live/` 与 `roundtrip/`。
- **未覆盖边界**：iPad 真机已安装标准版，但锁屏阻止启动，未获得真机运行结论。未上传 TestFlight；分发签名归档、渠道选择和作者手验不由本地开发签名包替代。App Store 并列上架身份仍未裁定。

### P1-Android：实施与本地验收

- Android 构建显式传 `PhotoViewerEnableDino=false|true`，头工程与共享工程按 `standard/ai` 隔离恢复资产、中间文件和输出；缺参/非法值均拒绝。两版保持同一 ApplicationId，显示名分别为 `PhotoViewer` / `PhotoViewer-AI`，清单由源模板生成中间目录副本。仅构建 `android-arm64`，APK 不含 x86_64/armeabi-v7a。
- 标准版移除模型、ONNX/MathNet 托管与原生依赖、DINO/相似入口，保留四项筛选条、CV 分析和三瓦片 CV 诊断；AI 版保留完整能力、固定模型和 DINOv3 License，两版 DINO/CV 即时计算独立。标准版 Debug/Release 均显式 `AndroidUseAssemblyStore=false`，AI 版 Debug 保持 `true`：缺少 DINO 程序集的 assembly store 布局会在 ReactiveUI/后台类型初始化阶段持续等待，改走逐程序集 DSO 后双版冷启动与回归通过。
- `publish-android-apk.sh --edition standard|ai|both [--local]`（Windows 为 `.ps1 -Edition …`）自动选择具备 android workload 的 SDK，逐版发布并调用 `Tools/check-android-package.py` 扫描实际 APK 的身份、显示名、版本、ABI、模型/License 与托管/原生推理组件，保存逐文件大小和 SHA-256；扫描器 23 项正反自检通过。本地包在 `artifacts/scratch/android-local-publish/`，机器可读证据在 `artifacts/acceptance/android-editions/`，未写正式归档。
- arm64 模拟器的标准版 edition/main/analysis/settings/tools/faults 六套、AI 版对应六套及 similarity 共十三套通过，计数分别为 13/22/52/105/40/41 与 12/22/52/106/40/37/23，均 0 FAIL。共享核心契约：标准版 **1187 PASS**、AI 版 **1203 PASS**，均 0 FAIL、1 个既有平台用例 SKIP。双版 Debug/Release 构建、包扫描和 Release 冷启动通过。
- 最终本地 Release 标准版 **35.40 MiB**、AI 版 **129.08 MiB**，标准版下载包较 AI 版小 **72.57%**；这些是本地签名验证包，不是应用商店下载体积。
- 真实 AI → 标准 → AI 覆盖安装三阶段通过（prepare 18、standard 28、ai 16，共 62 项）：使用隔离设置档 `edition-roundtrip` 与同一验收数据库，标准版保留 DINO/CV 设置及展开态但不恢复面板，修改通用设置后 AI 版恢复；CLS、patch、CV 的 SHA-256、model_id、CV spec/尺寸逐项一致。首轮设置值失败为提交后 VM 回填时序，验收现等待内存与持久化值一致，未修改生产存储行为。证据在 `artifacts/acceptance/android-editions/roundtrip-*.txt`。真机、正式归档及作者手验未覆盖。

### P1：推广前的正确性与发布门

- 分析栏始终先取当前照片的只读基线，增强只替换已授权的一侧；不得从上一轮瓦片反推基线。未增强时只补缺失侧；即时缓存只存成功侧，刷新可以重试失败侧且继续复用成功侧。
- CV 诊断与 DINO 诊断的 CV 现算输入统一为显示方向的 BGRA8 sRGB；读库使用记录内的 CV 尺寸。DINO-only 的瓦片比例独立于 CV 开关。
- 回归必须覆盖 F16/广色域/旋转输入数值对照、已有部分特征、增强状态打开分析栏、连续切图、部分失败后刷新、隐藏取消，以及 CLS/patch/CV 内容与版本标识的跨进程往返；不以空库四开关组合或行数检查替代。
- 每个变体的恢复资产、中间产物和输出目录隔离；manifest 从源模板生成到中间目录，不由构建改写仓库文件。普通开发构建与发布使用同一变体标识与路径约定。
- 发布版本唯一来源为 `Directory.Build.props`，显式版本参数不得仅更改文件名。扫描器同时验证包身份、版本/构建号、架构、禁止项与必需依赖，并在 DMG 只读挂载后再次扫描实际应用。
- 本地 adhoc 验收与正式 Developer ID/公证分开记录；前者不能进入正式归档或替代 Gatekeeper 发布结论。
- 三端推广时把独立 CV 控件、宿主 `ImmediateCvCompute`、属性变更重路由和编译能力一起接通；移除旧组合开关的默认接口回落，避免 UI 写独立字段而运行时仍读 DINO 字段。未实施的三端保持原组合行为，不提前切换半套接线。
- Web 参考壳通过 `?edition=standard|ai` 表达两版，默认 AI；标准版必须实际不创建相似任务与 DINO 瓦片，参考壳版本选择不得变成产品内升级或版本切换入口。
- iOS App Store 是否长期并列两个 App 仍是单独发布决策，不能用 TestFlight 两个构建号代替裁定；在裁定前不更改 Bundle ID 或签名资料。

### P2：独立无损优化

按收益和依赖风险分开落地、分开验收，不作为双版本首发阻塞项：

1. DINO 内置版模型无损压缩，运行时仍向 ORT 提供相同模型字节。
2. Windows 评估托管裁剪、桌面程序集清理、Windows App SDK 组件清理和最小 libheif 构建。
3. iOS 保持 TestFlight Symbols；仅区分本地直装包，不改变现有原生符号保留策略。
4. 模型量化不纳入“无损优化”，如另立任务必须重新做相似度质量与四端推理验收。

### P3：远期在线模型外置（仅保留调研，不实施）

在线路线仍只外置模型，推理继续端侧执行；用户主动触发下载，基础功能保持离线可用。届时需要另立实施计划，至少覆盖：

- 以 `ModelId` 组织不可变 URL，禁止可覆盖的 `latest.onnx`。
- 用户确认、进度、取消、断点续传、临时文件、原子改名和失败重试。
- 固定字节数与 SHA-256，加载前校验 ONNX 双输出；远程清单不能成为唯一信任根。
- 版本化缓存、旧模型保留、切换时不误删特征，以及无网/下载失败降级。
- 主备下载源、流量成本、国内可达性和隐私合规验收。

#### 托管调研事实

- **Meta DINOv3 License** 第 1 条允许使用、复制、修改和分发 DINO 材料及衍生作品；分发须继续遵守该协议并随模型提供 License。因此内置版和远期在线分发都应携带许可文本：[官方 License](https://github.com/facebookresearch/dinov3/blob/main/LICENSE.md)。
- **GitHub Releases**：单文件须小于 2 GiB，无 Release 总大小和带宽额度限制，可作免费备用源：[官方说明](https://docs.github.com/en/repositories/releasing-projects-on-github/about-releases#storage-and-bandwidth-quotas)。
- **Cloudflare R2**：标准存储有 10 GB-month/月免费额度，互联网出口流量免费；正式使用建议自有域名，国内可达性仍需实测：[价格](https://developers.cloudflare.com/r2/pricing/) · [公开 Bucket](https://developers.cloudflare.com/r2/buckets/public-buckets/)。
- **阿里云 OSS / 腾讯云 COS + CDN**：适合国内正式主源，需计算下载流量费用并绑定自有域名。
- **ModelScope / Hugging Face 自建模型仓库**：可分别作为国内和海外镜像；官方原始权重页面不是当前定制 ONNX 的现成下载源：[ModelScope 原始模型](https://www.modelscope.cn/models/facebook/dinov3-vits16-pretrain-lvd1689m) · [Hugging Face 原始模型](https://huggingface.co/facebook/dinov3-vits16-pretrain-lvd1689m)。

## 4. 验收标准

### 4.1 标准版（无 DINO）

- 包扫描无 `.onnx`、ONNX Runtime 托管/原生文件；Windows 无 DirectML，Android 无 ORT/JNI，macOS 无 ORT 动态库，iOS 主二进制无 ORT 链接与 `_OrtGetApiBase` 导出。
- 四端冷启动及浏览、解码、胶片栏、筛选、评分写回、EXIF、设置、工具页现有验收全部通过；不得出现模型缺失异常或后台 DINO 任务。
- UI 无相似聚类、DINO 诊断及依赖 DINO patch 的入口；非 DINO 分析能力不回归。
- 从 DINO 内置版切换到标准版再切回，设置、照片数据库和既有特征缓存保留；相同 `ModelId` 的缓存仍可复用。

### 4.2 DINO 内置版

- 模型字节数和 SHA-256 与 §1.2 完全一致，四端 CLS+patch 双输出、相似聚类及既有 DINO 验收全部通过。
- 保持 DirectML/CoreML/NNAPI 当前平台策略及 CPU 回退；模型加载不依赖网络。
- 随包提供 DINOv3 License；发布记录列出模型 `ModelId`、哈希和各端实际包体。

### 4.3 发布与体积

- 两个版本分别运行现有平台发布验收，不以“共享同一份代码”替代对实际产物的检查。
- 每个版本保存包内分类体积清单，与 §1.3 基线对比；偏差必须能由具体文件解释。
- 深度优化只以实际通过验收的包为准；本计划中的区间和理论相减值不得写成已完成收益。
