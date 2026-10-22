# Windows 发布收尾盘点（2026-10-19）

## 优先更正：单文件工程阻塞已关闭（2026-10-19 实测）

> **本节为当前结论，优先于下方保留的 DeepSeek 初轮调查记录。初轮「WinUI 3 不支持单文件」「PRI 缺失」「只能改用多文件」等推断已被后续实测否定；历史命令、体积与待办不代表当前发布状态。**

- **WinAppSDK 1.8 支持非打包、自包含单文件**：[官方部署说明](https://learn.microsoft.com/en-us/windows/apps/package-and-deploy/unpackage-winui-app)。本机 `microsoft.windowsappsdk.base/1.8.251216001/buildTransitive/Microsoft.WindowsAppSDK.SingleFile.targets` 默认 `IncludeAllContentForSelfExtract=true` 并校验单文件条件。单文件指**单一分发 EXE，运行时会解包，不是零落盘**；PRI 在 bundle 解包目录，并未丢失。
- **`0x80040111` 的实际根因是复用旧 WinRT 清单**：多文件构建留下的清单无 `loadFrom`；仅重新生成 `obj/Release/net10.0-windows10.0.19041.0/win-x64/Manifests` 中两个文件，单文件即可运行。发布脚本保留清 `obj`，防止复用旧清单。
- **改名限制已解除**：此前改名造成 settings/tools 缺 `TextFillColorPrimaryBrush`；现 `PortableSingleFile.targets` 使用 .NET 官方 `PrepareForBundle` 扩展点，将应用 PRI 额外以固定 `resources.pri` 名放入 bundle，供 WinUI 默认资源查找回退。无运行时复制资源、无 SDK 补丁；改为中文/带空格文件名也通过三套验收。曾尝试显式 `ResourceManagerRequested`，但会丢失框架资源解析，已撤除该临时方案，生产 App.xaml 引导保持不变。
- **CRT 已内置，不再要求用户安装运行库**：从 Visual Studio 2022 Build Tools 官方 `VC/Redist/MSVC/14.44.35112/x64/Microsoft.VC143.CRT` 原样携带 `msvcp140.dll`、`msvcp140_1.dll`、`vcruntime140.dll`、`vcruntime140_1.dll`，文件版本均 14.44.35211.0，微软签名有效。依赖导入/导出检查覆盖 libheif 编解码链和 ONNX 所需符号；UCRT 是支持的 Windows 自带组件。来源与再分发须遵守 [VS 官方清单及许可](https://learn.microsoft.com/en-us/visualstudio/releases/2022/redistribution#visual-c-runtime-files)，不携带 Debug CRT、不从 System32 复制、不执行安装器。构建脚本自动 vswhere 定位或接受显式 `-VCRedistDirectory`，校验架构、版本、完整性后再发布。
- **正式脚本最新实跑**：保留清 `obj`、全内容解包、不 trim、不 R2R、WinPS 5.1 UTF-8 BOM；原名与“照片 查看器.exe”在独立目录/各自全新缓存，分别 **smoke 32 / settings 7 / tools 5，共 88 项全 PASS**。HEIF 验收真实生成 HEVC 四象限图片，并经生产会话和缩略图解码、Fit/100% GPU 回读；素材不是相机原片。DirectML 实际推理后，枚举已加载模块，四 CRT（14.44.35211.0）、libheif、ONNX、DirectML 全来自本应用解包目录，不是系统目录。普通 smoke 为 25 项，`--pv-native-portable` 加 7 项来源检查。额外用未携带 CRT 的 Debug 进程做负例，验收器按预期拒绝 `C:\WINDOWS\SYSTEM32\MSVCP140.dll`，证明来源检查不是恒通过；该负例失败是预期结果，不是正式包验收失败。
- **最新产物**：`release/PhotoViewer-0.8.0-win-x64.exe`，**230,753,372 字节（230.8 MB / 220.1 MiB）**，SHA256 `BE82F815DC51920A5DFDF2046C49E0AAA11259EE1A495494BC24072581F45D9F`。报告与 CRT 源文件哈希：`release/rebuild-check/windows-singlefile/e77f66d7296440f8a0d0a46ad76e434d/deployment.json`，各套 JSON 位于该目录的 `original/` 与 `改名 空格目录/` 下。Debug/Release 常规构建均 0 错 0 警。
- **明确未关闭项**：本机无现成 Sandbox/干净虚拟机，未卸载宿主运行库或修改系统功能；因此尚无干净 Windows 全系统实测，不能把新缓存或模块来源证明冒充干净机验证。打包与改名工程问题已修，干净机、Win10、真实相机素材、签名仍据实保留。

### 剩余事项（当前分类）

- **发布风险**：未签名及 SmartScreen 提示风险；无运行时干净机与 Win10 尚未验。最低声明 Win10 1809、实测 Win11 是不同口径，并不矛盾。当前仅提供 x64 原生发布产物、无 ARM64 原生产物，不据此断言 Windows ARM 不能通过 x64 仿真运行。
- **硬件/素材验收**：175%/200% DPI 已由作者 2026-10-21 手验通过；仍待真实跨屏、相机八方向素材、增强 + 分析长时间稳定性。设置真实拖拽/录制/清库/预设增删、工具真实多格式/文件夹与 CSV 选择器已由作者 2026-10-18 手验，不再挂起。
- **独立延期**：Windows 缺 RAW **主图**解码，但内嵌缩略图、列表与星级写回可用；W-M7 色彩/RAW 原本独立立项。性能门、压测入口与 p50/p95 基线明确以后再做，**不计发布前置**。
- **验收/小缺口**：W-M6 专项与 Windows 几何导出未做；旧壳几何对拍随 6D 失效，后续按 6F 导出与样机比对。`Design/status.md` Windows 现仅 **1 格待核验**（SCOPE-04）与 6 格代码核对，不是缺功能；`INPUT-01` 仍需判定，不能直接认定功能缺失。`FILTER-08` / `TOOLBAR-11` / `ANALYSIS-11` / `SETTINGS-13` 的读屏类要求已于 2026-10-21 删除，不再挂账。Training `DatasetBuilder` Windows 实跑复核仍待补。

---

## 以下为 DeepSeek 初轮调查原记录（保留溯源，结论已被上方更正取代）

> 背景：mac / iPad / Android 收尾已完成，Windows 因「缺 `.pri`」被留到 PC 上处理。本文是 2026-10-19 在 Windows 本机的实测记录 + 结论 + 其余待收尾清单，供决定出包形态与排期。
> 实测环境：`refactor/native-rebuild` @ `47e5389`，`AppVersion 0.8.0`，.NET SDK `10.0.201`，`net10.0-windows10.0.19041.0`（最低 `10.0.17763.0`），`Microsoft.WindowsAppSDK 1.8.260921001`，win-x64，Windows 11 本机 150%。

## 0. 结论摘要

1. **单文件发布不可用，且登记的根因是错的**：`0x80040111` 发生在 `Application.Start` 里的 WinRT 激活（`Microsoft.UI.Xaml.Application` 无法激活），**不是缺 `.pri`**。实测把 4 个 `.pri` 全部拷到单文件 exe 旁边，启动仍然同样失败。
2. **「缺 `.pri`」这条已知限制来自对症状的错配**：上游 [WindowsAppSDK#6720](https://github.com/microsoft/WindowsAppSDK/issues/6720) 描述的是「启动约 10 秒后 `0xC000027B`」，与本案「启动即 `0x80040111`」不是同一症状；上游同时明确 WinUI 3 自包含**不支持** `PublishSingleFile`（原生依赖必须留在 exe 旁）。
3. **多文件发布是当前唯一可用形态**：同日实测产物含 4 个 `.pri`，exe 正常启动到 `startup complete` / `main window activated`，525 MB / 532 文件。但仓库里**没有**多文件发布脚本或 Task，唯一的发布脚本是跑不通的单文件脚本。
4. **需要作者拍的板**：单文件放弃后出包形态选 zip / 安装器 / MSIX 之一（见 §3），以及 0.8.0 Windows 的体积口径。
5. 其余 Windows 待收尾项见 §4：RAW 解码完全缺失、W-M7 色彩、W-M6 专项 harness 与几何导出、无压测入口、DPI/跨屏/真实素材手验、`status.md` 1 格待核验与 6 格代码核对。

## 1. 实测记录

| # | 试验 | 结果 |
|---|---|---|
| 1 | `dotnet build PhotoViewer.Windows -c Release` | **0 错误 0 警告**（`NU1903` 已随 6E-5 消失，与 `b4a8248` 记录一致） |
| 2 | 多文件发布（`PublishSingleFile=false`，自包含，win-x64） | 产物 **532 文件 / 525 MB**；含 `PhotoViewer.Windows.pri`、`Microsoft.UI.pri`、`Microsoft.UI.Xaml.Controls.pri`、`Microsoft.WindowsAppRuntime.pri` |
| 3 | 运行 #2 的 exe | 正常启动：`native-startup.log` 记录 `main window activated` + `startup complete`，进程存活 |
| 4 | 单文件发布（照 `publish-win-x64-singlefile.ps1` 原参数） | 产物只有 `PhotoViewer.Windows.exe`（**230 MB**）+ 3 个 pdb（脚本运行时会删 pdb）；**四个 `.pri` 全部缺失**；发布耗时约 **236 s** |
| 5 | 运行 #4 的 exe | 启动即失败：`COMException (0x80040111) 类工厂无法供应请求的类`，栈顶 `Microsoft.UI.Xaml.Application.Start` → `WindowsNativeApplication.Run` |
| 6 | 把 #2 的 4 个 `.pri` 拷到 #4 的 exe 旁再启动 | **仍然 `0x80040111`**（同样的栈）——决定性反证：缺 `.pri` 不是死因 |
| 7 | 用 `CustomAfterMicrosoftCommonTargets` 注入 target，把 `.pri` 从 bundle 里拉出来（`ExcludeFromSingleFile`） | MSBuild **两次卡死**（进程 CPU 个位数秒、全线程等待），未产出结果；且由 #6 已可判定该方向无收益，放弃 |

临时产物（可删）：`C:\Users\doudo\AppData\Local\Temp\pv-pri\` —— `multi-nofix\` 为可启动的多文件产物，`sf-nofix\` 为失败对照，合计约 843 MB。

复现命令（Git Bash；`/p:` 会被 MSYS 改写，必须写 `-p:`）：

```bash
# 多文件（可用）
dotnet publish PhotoViewer.Windows/PhotoViewer.Windows.csproj -c Release -r win-x64 \
  --self-contained true -o out-multi -p:PublishSingleFile=false
# 单文件（现状失败）
dotnet publish PhotoViewer.Windows/PhotoViewer.Windows.csproj -c Release -r win-x64 \
  --self-contained true -o out-single -p:PublishSingleFile=true \
  -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true \
  -p:PublishTrimmed=false -p:PublishReadyToRun=false -p:DebugType=None -p:DebugSymbols=false
```

## 2. 根因更正

- **失败点**：`Program.Main` → `WindowsNativeApplication.Run` → `Microsoft.UI.Xaml.Application.Start`，抛 `CLASS_E_CLASSNOTAVAILABLE`。这是 **WinRT 激活**（class factory）失败，发生在资源系统被访问之前，所以 `.pri` 在不在都不改变结果。
- **为什么单文件会失败**：非打包自包含的 WinUI 3 依赖「exe 同目录有一整套 WinAppSDK 原生/WinRT DLL」来完成免注册激活。单文件把这些 DLL 打进 bundle（`IncludeNativeLibrariesForSelfExtract` 只在运行时解到临时目录），exe 所在目录不再具备激活条件。上游立场一致：`PublishSingleFile=true` **不受** WinUI 3 支持（[Microsoft Q&A](https://learn.microsoft.com/en-us/answers/questions/5968479/winui-3-published-unpackaged-self-contained-app-cr)、[microsoft-ui-xaml#10173](https://github.com/microsoft/microsoft-ui-xaml/issues/10173)）。
- **多文件为什么本来就有 `.pri`**：`PhotoViewer.Windows.csproj:21` 开了 `EnableMsixTooling`，把 MSIX 的 publish 接线（`AddProjectPriToResolvedFileToPublish`）导了进来。缺的只是单文件场景下的 `ExcludeFromSingleFile`，而该文件并非启动失败的原因。
- **结论**：6E-2 与 `DEV.md` / `AGENTS.md` / `native-rebuild-windows-p2.md` 里「缺 `.pri` → `0x80040111`」的因果要更正为「WinUI 3 不支持单文件自包含部署」。

## 3. 出包形态：三个选项（待作者定）

| 选项 | 做法 | 优点 | 代价 |
|---|---|---|---|
| A. 多文件 + zip | 把多文件产物打成 `PhotoViewer-<ver>-win-x64.zip` | **今天已验证可跑**，改动最小；下载体积与单文件压缩后同量级（~230 MB 级） | 用户要解压后再运行；README「下载 exe 直接运行」需改；与历次 `*-win-x64.exe` 命名断代 |
| B. 安装器自解包 | 7z SFX / Inno Setup 解到 `%LOCALAPPDATA%\PhotoViewer` + 开始菜单项 | 保留「单一下载、双击安装」体验，可做卸载 | 多一个打包工具链与签名议题 |
| C. MSIX 打包签名 | 走打包路径出 `.msix` | 最正规，可自动更新 | 需证书与商店/旁加载策略；工作量最大，且要另建打包管线 |

无论选哪个，都要：改 `PhotoViewer.Windows/publish-win-x64-singlefile.ps1`（现为单文件专用并硬拒非 win-x64）、补 `.vscode/tasks.json` 对应 Task、改 DEV.md 发布章节。

另需一并决定：**体积口径**（多文件 525 MB / 单文件压缩 230 MB，原 6E-2 记 239 MB）。

## 4. 其余 Windows 待收尾事项

### 4.1 发布/出包

- `release/` 里最新 Windows 产物是 `PhotoViewer-0.7.1-win-x64.exe`（7 月，旧 Avalonia 壳）；0.8.0 尚无任何 Windows 产物。
- **无代码签名**：直发 exe 会触发 SmartScreen。文档未登记为待办，属发布面风险。
- **仅 x64**：`LibHeif.Native` 只有 win-x64，脚本硬拒其他 RID，Windows on ARM 不可用。

### 4.2 功能缺口（已登记）

- **Windows 完全没有 RAW 解码**（README 状态表写「暂不支持」）：0.8.0 Windows 版打不开 ARW/CR3，与 mac/iPad 有明显落差。对应 `native-rebuild-windows-p2.md` §13.2 #13。
- **W-M7 色彩**（ICC/广色域标签/ACM 探测/Win10 烘焙回退）未做；`Design/status.md` 的 `SHELL-05`（DPI/跨屏）仍是待核验。§13.2 #12。
- **`INPUT-01`**（文本框获焦暂停全局快捷键）挂待核验：主窗口其实没有文本框，设置窗口已独立吞键（§13.1 #20 已验），很可能是一次被误挂的待验项，值得一次实测判定为「满足/不适用」。
- `SCOPE-04`（RAW 预览限制）、`SCOPE-05` 待核验。

### 4.3 验收缺口

- **W-M6 专项 harness + 旧壳几何 JSON 对拍未开始**（§13.2 #11）：6F 已让 Mac 导出 381 节点几何，Windows 尚无同格式导出，未纳入比对。
- 设备矩阵：200%/175% DPI、真实跨屏（不同 scale）、真实相机八方向素材与旧壳逐项对拍、真实鼠标/触控板手感、增强 + 分析栏同时开启的长时间稳定性（§13.2 #1/#2/#3/#5/#16）。
- **性能无入口**：Windows 原生壳没有 `--pv-stress`（`StressFlipDriver` 未接线），没有任何 p50/p95 基线。
- `Design/status.md` Windows 列 127 个 ID 中仍有 **38 格**为待核验/缺口：TOOLBAR 8、SIM 8、INPUT 6、SETTINGS 5、FILTER 3、ANALYSIS 3、SCOPE 2，以及 SHELL-05 / OPEN-07 / TOOLS-04 各 1。

### 4.4 文档账

- `DEV.md:105`、`PhotoViewer.Windows/AGENTS.md:131`、`Plans/native-rebuild-windows-p2.md:65`、`Plans/native-rebuild-phase-6.md:58`（6E-2）：根因表述需更正（见 §2）。
- `Plans/native-rebuild-phase-6.md` §7 发现清单：应补一条「单文件 0x80040111 根因误判为缺 `.pri`」。
- 命名口径：DEV.md / README 写 `PhotoViewer.Windows.exe`，`release/` 实际是 `PhotoViewer-<版本>-win-x64.exe`。
- README「已测试平台 Windows 11」与 csproj 最低 `10.0.17763.0`（Win10 1809）口径不一致。
- Training `DatasetBuilder` 去 Avalonia 后的 **Windows 实跑复核**仍挂着（`native-rebuild-phase-6.md` §3 保留项）。

## 5. 建议顺序

1. 定出包形态（§3）→ 改脚本 + Task + DEV.md，产出 0.8.0 Windows 包并实测启动。
2. 更正 §2 的根因，补齐 §4.4 的文档回写（含 6E-2 状态）。
3. 单独判定 `INPUT-01` 是缺口还是误挂。
4. 之后按 W-M6（几何导出 + 专项 harness）/ W-M7（色彩、RAW）排期；性能入口与 iPad/Android 一起做。
