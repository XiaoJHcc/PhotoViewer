# PhotoViewer 开发与发布指南

日常构建、运行和发布统一使用 VS Code Tasks。本文只保留环境要求与操作方式；架构和实现约束见各模块 `AGENTS.md`，任务过程与验收结果放在 `Plans/`，设计状态放在 `Design/status.md`。

## 环境准备

- 安装满足仓库 `global.json` 的 **.NET SDK**，以及 VS Code 的 **C# Dev Kit**、**C#** 扩展。
- **Windows**：发布需安装 Visual Studio / Build Tools 的 Release x64 C++ Redistributable 组件，供脚本取得官方 CRT；不从 Windows 系统目录复制 DLL。
- **macOS / iOS**：安装 Xcode，执行 `sudo dotnet workload install macos ios`。Xcode 与 workload 版本须匹配；临时关闭版本校验的配置在升级工具链时应复核。
- **Android**：安装 `dotnet workload install android`、JDK 17+ 和 Android SDK。macOS 默认 SDK/ADB 路径为 `~/Library/Android/sdk`；Apple Silicon 模拟器使用 `arm64-v8a` 镜像。
- **本地专属内容（不入库）**：iOS 的 `Tools/RefreshIosProfile/Local.xcconfig`（Team ID）与 `PhotoViewer.iOS/ExportOptions.AppStore.Local.plist`、Android 的 `signing.json` 与 `keystore/`，分别由同目录 `.sample` 或 `setup-android-signing.sh` 生成；`TestCase/` 是本地自备照片样本（`Tools/ColorEngineLutCheck` 等可选工具用），`native-p2-raw/` 是 RAW 就地评分冒烟用的副本；DINOv3 模型受许可约束不入库，见 [DINOv3 ONNX 模型](#dinov3-onnx-模型)。除这些与本机凭据外，构建、运行与验收所需文件都随仓库分发。
- 若不同平台需要不同 SDK，使用临时目录中的 `global.json` 与绝对项目路径构建，不为本机环境随意改仓库 SDK 约束。

通过 `F1` → **Tasks: Run Task** 选择任务，不使用通用 UI Publish 菜单代替项目发布脚本。**Clean Build Caches (All Platforms)** 会清理平台 `bin/obj` 及相关工具链缓存，仅在版本切换或疑似脏缓存时使用。

## 运行与调试

四个平台的 Debug / Release 均使用原生壳。

### Windows

| Task | 用途 |
|---|---|
| **Debug Windows** | 构建并启动 WinUI 3 原生壳。 |
| **Run Windows** | 启动已有构建，不重新编译。 |
| **Check Windows Native** | 构建并运行隔离冒烟，结束后自动关闭；失败退出码为 1。 |
| **Check Windows Color** | 构建并运行隔离色彩专项（真实 JPEG/HEIF/RAW、显示器 ICC、GPU 输出切换），报告 `color-checks.json`；不改系统显示配置，真屏/跨屏结论另验。 |

从仓库根直接打开图片或文件夹：

```bash
dotnet run --project PhotoViewer.Windows -c Debug -- "D:/photos/a.jpg"
```

专项参数追加在 `--` 后：`--pv-native-settings`、`--pv-native-tools`；各自的 `-hold` 变体保持窗口供目视。`--pv-native-activation-check` 验证单实例二次激活（本进程持键并拉起第二个进程，核对转发、定位与退出码；数据仍走隔离目录）。报告位于工作目录的 `artifacts/acceptance/native-win-acceptance/`。检查范围与工程约束见 [Windows 模块手册](PhotoViewer.Windows/AGENTS.md)。

性能与压测入口同样使用隔离设置、数据库与日志（`artifacts/acceptance/native-win-acceptance/`），不触碰用户设置与照片：

```bash
dotnet build PhotoViewer.Windows -c Release -p:Platform=x64
"./PhotoViewer.Windows/bin/x64/Release/net10.0-windows10.0.19041.0/win-x64/PhotoViewer.Windows.exe" "--pv-perf-probe=D:/photos/A7C2"
"./PhotoViewer.Windows/bin/x64/Release/net10.0-windows10.0.19041.0/win-x64/PhotoViewer.Windows.exe" "--pv-stress=D:/photos/A7C2" --pv-stress-count=100 --pv-stress-mode=ab
```

探针按 HIF / ARW / JPG 各取 6 张（首张预热）分段计时，写 `perf-probe.txt` 后自行退出；ARW 另测一次 WIC 开发下的代理/Full 派生时间、像素字节账，并与正常解码产物逐字节对照（会额外占用测试内存，不代表普通浏览的进程峰值）。压测按展示列表连翻（`--pv-stress-mode=ab` 改为相邻两张来回切），逐张写 `stress-flip.jsonl`；Windows 的完成阶段是 `present-return`（目标代际正式纹理绑定且 Present 返回，不等同屏幕扫描完成），另记 `pixelsReadyMs` 与就绪后提交时间。`done.extra` 区分同步/异步上传、真实上传与缓存命中、暂存创建/映射/目标创建/拷贝/mip/后台墙钟、Render/Present、UI 心跳及内存账。可加 `--pv-stress-window=3000x1800` 固定物理窗口、`--pv-stress-full` 强制 Full、`--pv-stress-budget-mb=2048` 模拟小应用预算；模拟不代表小内存真机。`--pv-stress-count` 大于列表长度时循环翻页，A/B 持续切前两张。所有隔离入口支持 `--pv-acceptance-output=artifacts/acceptance/<本轮目录>` 分开保存报告。`--pv-native-upload` 可独立跑异步代际/真实GPU像素/持续压力回归，同时也属于完整 smoke。

### Android

设备需通过 ADB 连接，也可先启动 Android 模拟器。

| Task | 用途 |
|---|---|
| **Debug Android** | 构建、安装、启动，并打开 Logcat。 |
| **Install Android** | 仅安装已有构建。 |

原生验收夹具先推到 `/data/local/tmp`，再用 `run-as cc.xiaojh.PhotoViewer` 复制到应用的 `files/pv-fixtures`。启动示例：

```bash
adb shell am start -n cc.xiaojh.PhotoViewer/.AndroidBootstrapActivity --es pv-open @files/pv-fixtures --es pv-check main
```

`pv-check` 支持 `main|analysis|similarity|settings|tools` 基线模式，以及 `faults|layout|input|storage|filter|zoom|color` 独立模式；`filter` 覆盖下拉跨探测周期、重复布局与窄屏滚动，`zoom` 覆盖 26MP 代理→Full 上传、GPU 分块边界与实际 HIF 放大性能（需照片副本放在应用内部或外部 `pv-check-zoom/`）。`color` 需 P3 真机：把 `Fixtures/jpeg/color-*.jpg`、`Fixtures/heif/color-*.heic` 与 `Fixtures/jpeg-large/color-p3-large.jpg` 复制到应用的 `files/pv-color-fixtures/`，再以 `--es pv-check color` 冷启动；检查使用隔离设置，覆盖实际 EGL 编码、主图回读、位图原始 P3 存储、导航图、代理/Full 与增强。默认按设备能力尝试 P3；`--ez pv-srgb true` 或 `--ez pv-p3 false` 可强制 sRGB 对照。报告通过 `adb exec-out run-as cc.xiaojh.PhotoViewer cat files/PhotoViewer/android-checks-<mode>.txt` 读取。具体素材与参数见 [Android 模块手册](PhotoViewer.Android/AGENTS.md)。

### macOS

| Task | 用途 |
|---|---|
| **Debug Mac** | 构建、签名并启动标准版 `PhotoViewer.app`（不含 DINO/ONNX）。 |
| **Run Mac** | 启动已有标准版构建，不重新编译。 |
| **Debug Mac AI** | 构建、签名并启动 `PhotoViewer-AI.app`，内置 DINO 模型与 ONNX Runtime。 |
| **Run Mac AI** | 启动已有 AI 版构建，不重新编译。 |

两个版本各自使用 `PhotoViewer.Mac/bin/<standard|ai>/` 与 `obj/<standard|ai>/`，共享工程 `PhotoViewer/` 同样按版本隔离 `bin/obj`，两版不复用还原资产与中间文件。命令行构建、运行与发布都必须显式传 `-p:PhotoViewerEnableDino=false|true`，缺失时构建直接报错，不走默认目录；上面的 Tasks 已带该参数。构建只改写 `obj/<edition>/…/Info.plist` 副本，仓库内的 `PhotoViewer.Mac/Info.plist` 保持原样。

### Web 参考壳

| Task | 用途 |
|---|---|
| **Serve Web** | 在 `PhotoViewer.Web/` 起本地静态服务，浏览器打开 `http://localhost:8080/`（ES module 不能经 `file://` 加载）。 |
| **Test Web** | 校验令牌产物最新并运行 `node --test` 纯逻辑单测。 |

只需 Node 18+ 与 Python 3，无其他依赖。推到 `main` 后由 `.github/workflows/web-pages.yml` 先跑令牌校验与单测，再发布到 GitHub Pages（需在仓库设置中把 Pages 来源设为 GitHub Actions）。页面参数与实现约束见 [Web 模块手册](PhotoViewer.Web/AGENTS.md)。

### iOS / iPadOS

模拟器需先在 Xcode 中启动；真机需配置开发者证书和 Provisioning Profile。

| Task | 用途 |
|---|---|
| **Debug iOS Simulator** | 构建、安装并启动模拟器应用。 |
| **Install iOS Simulator** | 仅安装已有模拟器构建。 |
| **Debug iOS** | 构建 arm64 Release，选择真机并安装启动。 |
| **Install iOS** | 选择真机，安装已有 Release 构建。 |
| **Install iOS Stable (Release)** | 安装 `release/ios-stable/` 中归档的稳定应用。 |
| **Refresh iOS Profile** | 通过 Xcode 续期免费 Apple ID 的开发预配文件。 |

设备选项在 `.vscode/tasks.json` 的 `inputs[iosDevice]` 中维护。首次真机运行须在系统设置中信任开发者证书。启动参数、程序化验收与素材要求见 [iOS 模块手册](PhotoViewer.iOS/AGENTS.md)。

续期 Profile 前，Xcode 须已登录 Apple ID 并注册设备（Settings → Accounts）；免费账号的 Profile 只有 7 天有效期，过期后真机构建报「在 iOS 上找不到 … 的任何可用预配配置文件」，先跑 **Refresh iOS Profile** 续期，若该项报 `No Accounts` 则说明 Xcode 里没有可用的 Apple ID 会话，须先重新登录再续期。首次使用先配置本地 Team ID：

```bash
cp Tools/RefreshIosProfile/Local.xcconfig.sample Tools/RefreshIosProfile/Local.xcconfig
```

编辑 `Local.xcconfig` 的 `DEVELOPMENT_TEAM`；该文件已被忽略，不提交到仓库。

**内存上限 entitlement**：`PhotoViewer.iOS/Entitlements.plist` 声明 `com.apple.developer.kernel.increased-memory-limit`（免费 Apple ID 也可签进 Profile），`Tools/RefreshIosProfile/RefreshIosProfile/Entitlements.plist` 必须与它保持一致，否则续期后的 Profile 不带该能力、真机预算退回小口径。两份文件同改同验；运行时不依赖它生效，缺失时按同一逻辑自动按小预算运行。

## 验收素材

验收素材是测试输入而非构建产物，统一放在 `Fixtures/` 随版本库分发，新克隆无需再拷贝；清单、初始星级、缺件口径与 Core 契约自检命令见 [Fixtures/README.md](Fixtures/README.md)。验收报告与截图写 `artifacts/acceptance/`。

## 产物目录

`release/` 只放分发包与打包过程产物，顶层只允许 `packages/`、`ios-stable/`、`testflight/` 和 `SHA256SUMS`；验收报告、截图、日志、测试数据库、崩溃报告一律放 `artifacts/`。两者都不入库，报告/截图等出现在 `release/` 视为错误。

| 路径 | 内容 | 生命周期 |
|---|---|---|
| `release/packages/` | 历史与当前分发包，命名 `<PhotoViewer|PhotoViewer-AI>-<版本>-<平台后缀>.<dmg/apk/ipa/exe>` | 每版每平台每变体一份，长期保留；未签名 APK 等中间产物不归档 |
| `release/ios-stable/` | 最新 iOS 稳定安装源（`.app`） | 每次 **Publish iOS IPA** 覆盖 |
| `release/testflight/` | 最近一次 TestFlight `.xcarchive` | 每次上传覆盖，脚本只保留最新归档 |
| `release/SHA256SUMS` | 分发包校验清单 | 每次发包后由 `check-release.py --write` 重写 |
| `artifacts/acceptance/` | 各套件报告与截图；同名套件目录覆盖写，带日期目录按轮次归档 | 每平台只保留最近 2 份轮次 |
| `artifacts/diagnostics/` | 崩溃报告等诊断物 | 问题结案后删除 |
| `artifacts/scratch/` | RAW 副本、临时状态 | 随时可清 |

发布后运行：

```bash
python3 Tools/check-release.py --write   # 校验 release 布局并重写 SHA256SUMS
bash Tools/clean-artifacts.sh            # 预览可清理项；加 --yes 执行
```

`check-release.py` 按双版本矩阵校验平台后缀与扩展名：`-win-x64` `.exe`、`-mac-arm64` `.dmg`、`-android-arm64` `.apk`、`-ios`/`-ios-arm64` `.ipa`；历史包继续接受 `-arm64` `.dmg` 与 `-android-signed` `.apk`，`-android-x86_64` 等架构后缀直接拒绝。当前版本缺 Mac 双版本包时给出提示，不阻断其他平台尚未迁移的归档。`clean-artifacts.sh` 会清空 `artifacts/scratch/`，删除各套件目录中的数据库、烟测设置与夹具副本，把 `release/testflight/` 收敛到最新归档，并将 mac/ios/android 轮次目录收敛到最近 2 份。

校验清单可在 `release/` 内核对：`cd release && shasum -a 256 -c SHA256SUMS`。

## 打包发布

版本号由根目录 `Directory.Build.props` 的 `AppVersion` / `AppBuildNumber` 统一控制，发布脚本不另行改版本；分发包位于 `release/packages/`，打包过程产物位于 `release/ios-stable/` 与 `release/testflight/`。

Mac 采用 Developer ID 公证直发，不进入 Mac App Store。首次发布前在 Xcode 的 **Settings → Accounts → Manage Certificates** 创建带本机私钥的 **Developer ID Application** 证书，再把 App 专用密码存入本机钥匙串：

```bash
xcrun notarytool store-credentials "PhotoViewer-notary" \
    --apple-id "<Apple ID>" --team-id "<Team ID>" --password "<App 专用密码>"
```

可用 `MAC_DEVELOPER_ID_IDENTITY` 指定签名身份、`MAC_NOTARY_PROFILE` 指定其他钥匙串配置；默认自动选择首个 Developer ID Application 身份并读取 `PhotoViewer-notary`。凭据不得写入仓库。若 Apple 已返回 `Accepted`、但附加票据时网络失败，运行 `bash PhotoViewer.Mac/publish.sh --staple-only --edition <standard|ai|both>`，只重试对应变体的票据附加与验证，不重新构建或上传。

Mac 发布脚本以根 `Directory.Build.props` 的 `AppVersion` / `AppBuildNumber` 为唯一版本来源；兼容位置参数（`bash PhotoViewer.Mac/publish.sh 0.9.0`），但与之一致才继续，不一致立即报错退出，不擅自改写版本或构建号。正式发布在 `artifacts/acceptance/mac-editions/<edition>-publish.json` 保存机器可读证据：签名身份、`codesign` 校验、公证提交与状态、`stapler` 与 Gatekeeper 结果、DMG 大小与 SHA-256、只读挂载后卷内 `.app` 的扫描报告路径；公证状态非 `Accepted` 时保留 `<edition>-notarization-log.json` 并终止。发布与挂载核对都调用 `Tools/check-mac-package.py`，按 `--version` / `--build-number`（缺省读根 props）核对包目录名、`Info.plist` 身份与主程序 arm64 架构，并分别约束两版的模型、许可证与托管/原生推理资产。

只做本机预览时运行 `bash PhotoViewer.Mac/publish.sh --local [--edition standard|ai|both]`：adhoc 构建、扫描与 DMG 装载验证后结束，DMG 落在 `artifacts/scratch/mac-local-dmg/`，不签名 Developer ID、不公证、不写入 `release/packages`。单独扫描任一架包：

```bash
python3 Tools/check-mac-package.py PhotoViewer.Mac/bin/standard/Release/net10.0-macos/osx-arm64/PhotoViewer.app \
    --edition standard --output artifacts/acceptance/mac-editions/scan.json
python3 Tools/check-mac-package.py --self-test    # 正反判定自检
```

| Task | 产物与说明 |
|---|---|
| **Publish Windows EXE (Release)** | 自包含 `release/packages/PhotoViewer-<版本>-win-x64.exe`，运行时自动解包内置依赖；不启用 trim/R2R。 |
| **Publish Android APK (Release)** | 构建并签名 APK，归档 `release/packages/PhotoViewer-<版本>-android-signed.apk`。 |
| **Publish Mac DMG (Release, Standard + AI)** | 默认依次构建标准版和 AI 版，核对 `.app` 身份与内容后以 Developer ID + Hardened Runtime 签名、公证并验证；分别归档 `release/packages/PhotoViewer-<版本>-mac-arm64.dmg` 与 `PhotoViewer-AI-<版本>-mac-arm64.dmg`。仅需单变体时运行 `bash PhotoViewer.Mac/publish.sh --edition standard` 或 `--edition ai`。 |
| **Publish Mac DMG (Local Adhoc Check)** | 只在本机验证：adhoc 构建、包扫描与 DMG 装载核对，产物留在 `artifacts/scratch/mac-local-dmg/`，不签名 Developer ID、不公证、不归档。 |
| **Publish iOS IPA (Release)** | 生成 `release/packages/PhotoViewer-<版本>-ios-arm64.ipa`，并覆盖 `release/ios-stable/` 的 `.app` 安装源。 |
| **Upload iOS TestFlight** | 使用 `Apple Distribution` 与 `PhotoViewer iOS App Store` Profile 归档并上传 App Store Connect；`release/testflight/` 只保留最近一次归档。上传前自动执行归档检查。 |
| **Validate iOS TestFlight Archive** | 检查当前版本归档的版本号、Bundle ID、分发签名、entitlements、AOT/ONNX 符号与图标，不上传。 |
| **Check iOS TestFlight Device** | 核对设备版本，连续 5 次冷启动并确认进程存活、无新增崩溃报告；设备需先通过 TestFlight 安装当前构建。 |

每次上传 iOS 构建前递增 `AppBuildNumber`，即使 `AppVersion` 不变也不复用构建号。TestFlight 上传复用 Xcode 中已登录的开发者账号，不改变 Release 真机安装所用的开发签名。iOS Release 通过 `MtouchNoSymbolStrip=true` 保留 Mono AOT 启动符号，不能为缩减体积重新开启符号剥离。首次上传前复制 `PhotoViewer.iOS/ExportOptions.AppStore.Local.sample.plist` 为同目录 `ExportOptions.AppStore.Local.plist` 并填写本机 Team ID；该文件已忽略，不提交。

Windows 发布脚本自动定位 Visual Studio 官方 Release x64 CRT；自定义构建环境可向 `PhotoViewer.Windows/publish-win-x64-singlefile.ps1` 传 `-VCRedistDirectory`。脚本在原名/改名 EXE 的独立目录和新缓存中执行验收，通过后归档，并在 `artifacts/acceptance/rebuild-check/windows-singlefile/` 保存报告与 `deployment.json`。CRT 来源须符合 [微软再分发许可](https://learn.microsoft.com/en-us/visualstudio/releases/2022/redistribution#visual-c-runtime-files)。

Android 首次发布前，在对应宿主执行：

```powershell
# Windows
PhotoViewer.Android/setup-android-signing.ps1
```

```bash
# macOS
bash PhotoViewer.Android/setup-android-signing.sh
```

签名配置写入被忽略的 `signing.json`。iOS IPA 仅能安装到签名与预配允许的设备；重新安装归档稳定版本使用 **Install iOS Stable (Release)**。

## 维护工具

### 设计令牌与几何

`Design/tokens.json` 是设计数值的唯一来源；生成的 C# / Web 令牌文件不得手改：

```bash
python3 Tools/generate-design-tokens.py
python3 Tools/generate-design-tokens.py --check
node PhotoViewer.Web/tools/export-geometry.mjs --profile mac --count-width 52 --out /tmp/web-geometry.json
python3 Tools/compare-layout-geometry.py /tmp/web-geometry.json artifacts/acceptance/native-p2-acceptance/native-layout-geometry.txt
```

### 在 macOS 类型检查 Windows 壳

WinUI XAML 编译器只能在 Windows 上运行。跨平台修改后可先检查 C# 类型，但不能代替 Windows 本机构建和运行：

```bash
dotnet msbuild Tools/WindowsTypeCheck/WindowsTypeCheck.csproj -restore -t:Compile -v:m
```

### Windows HEVC 解码库

`PhotoViewer.Windows/NativeLibs/win-x64/pvhevc.dll`（静态链接「仅 HEVC 解码器」LGPL FFmpeg）已入库；更换 FFmpeg 版本或工具链后用 MSYS2 CLANG64 重建 `PhotoViewer.Windows/build-native-windows-hevc.ps1`，并把 `pvhevc.dll` 与 `pvhevc.txt` 一起更新。
重建后在 `Tools/HeifTileBench/` 目录执行 `python pvhevc_check.py`（逐瓦片对照 `heif_tile_extract.py` 的 Annex-B 产物与耗时）复核二进制；托管侧容器解析与 YUV 转换的对照验证用 `Tools/HeifFastDecodeTest`（`selftest`/`smoke`/`parse`/`extract`/`validate`/`e2e`/`bench`）。改动 HEIF 色彩链、F16 几何或 libheif 码值归一化后至少跑：

```bash
dotnet run --project Tools/HeifFastDecodeTest -c Release -- e2e     # 快速路径 vs libheif 回落逐色块 + 48 位通道 + 自建 12 位暗平面码值范围
dotnet run --project Tools/HeifFastDecodeTest -c Release -- smoke   # 容器/色彩支持面与转换器口径
dotnet run --project Tools/HeifFastDecodeTest -c Release -- read    # 文件读取、取消、长度变化与单缓冲分配
```


### 色彩转换回归

改动 `ColorEngine` 时运行共享色彩契约与串行参考对照；后者无参覆盖查表和通用分带路径，`-- general` / `-- lut` 可单跑。大图计时包含池化租借与转换，不包含解码或呈现；两端原生色彩检查仍需分别运行。

```bash
dotnet run --project Tools/ColorTestTool -c Release
dotnet run --project Tools/ColorEngineLutCheck -c Release
```

### EXIF 字段名称

- 英文名称先用 MetadataExtractor，未知字段由 `PhotoViewer/Core/Exif/ExifToolTags.Generated.cs` 补充；重新生成：`python3 Tools/generate-exiftool-tags.py`。
- 中文翻译在 `PhotoViewer/Core/Exif/ExifChinese.Generated.cs`；重新生成：`python3 Tools/generate-chinese-template.py`。
- 两个生成文件都不要手改。人工覆盖分别放在 `ExifToolTags.cs` 的 `_overrideTables`、`ExifChinese.cs` 的 `_overrideNames`；中文键使用英文覆盖后的最终字段名。

### DINOv3 ONNX 模型

`PhotoViewer/Assets/Models/dinov3_vits16.onnx` 受 **Meta DINOv3 License** 约束，不入库；开发者在本地生成。导出脚本在独立仓 [PhotoViewer-Training](https://github.com/XiaoJHcc/PhotoViewer-Training)，按约定检出到本仓 `Training/`（主库忽略）：

```bash
git clone https://github.com/XiaoJHcc/PhotoViewer-Training.git Training
```

原始权重可从需授权的 HuggingFace `facebook/dinov3-vits16-pretrain-lvd1689m` 或 ModelScope 镜像获取。

准备 Python 环境中的 `torch transformers onnx onnxruntime onnxscript`，然后导出并校验：

```bash
python Training/onnx/export_dinov3_onnx.py \
    --model-id <本地权重目录或HF模型ID> \
    --output PhotoViewer/Assets/Models/dinov3_vits16.onnx
python Training/onnx/verify_onnx_parity.py \
    --model-id <同上> \
    --onnx PhotoViewer/Assets/Models/dinov3_vits16.onnx
```

模型权重内嵌在同一个 ONNX 文件中（`external_data=False`）；一致性校验要求 `cos_mean ≥ 0.999`。模型缺失不影响构建与基础浏览（Windows 壳按文件存在性条件打包模型），只有 DINO 诊断与相似聚类运行期报缺少模型；原生验收里的 DINO 检查需要模型。Mac 标准版不打包模型或推理运行时，AI 版打包同一模型并附 `Assets/Models/DINOv3-LICENSE.md`，发布扫描会核对模型字节数与 SHA-256。
