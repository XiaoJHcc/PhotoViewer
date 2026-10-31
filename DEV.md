# PhotoViewer 开发与发布指南

日常构建、运行和发布统一使用 VS Code Tasks。本文只保留环境要求与操作方式；架构和实现约束见各模块 `AGENTS.md`，任务过程与验收结果放在 `Plans/`，设计状态放在 `Design/status.md`。

## 环境准备

- 安装满足仓库 `global.json` 的 **.NET SDK**，以及 VS Code 的 **C# Dev Kit**、**C#** 扩展。
- **Windows**：发布需安装 Visual Studio / Build Tools 的 Release x64 C++ Redistributable 组件，供脚本取得官方 CRT；不从 Windows 系统目录复制 DLL。
- **macOS / iOS**：安装 Xcode，执行 `sudo dotnet workload install macos ios`。Xcode 与 workload 版本须匹配；临时关闭版本校验的配置在升级工具链时应复核。
- **Android**：安装 `dotnet workload install android`、JDK 17+ 和 Android SDK。macOS 默认 SDK/ADB 路径为 `~/Library/Android/sdk`；Apple Silicon 模拟器使用 `arm64-v8a` 镜像。
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

从仓库根直接打开图片或文件夹：

```bash
dotnet run --project PhotoViewer.Windows -c Debug -- "D:/photos/a.jpg"
```

专项参数追加在 `--` 后：`--pv-native-settings`、`--pv-native-tools`；各自的 `-hold` 变体保持窗口供目视。报告位于工作目录的 `release/native-win-acceptance/`。检查范围与工程约束见 [Windows 模块手册](PhotoViewer.Windows/AGENTS.md)。

性能与压测入口同样使用隔离设置、数据库与日志（`release/native-win-acceptance/`），不触碰用户设置与照片：

```bash
dotnet build PhotoViewer.Windows -c Release -p:Platform=x64
"./PhotoViewer.Windows/bin/x64/Release/net10.0-windows10.0.19041.0/win-x64/PhotoViewer.Windows.exe" "--pv-perf-probe=D:/photos/A7C2"
"./PhotoViewer.Windows/bin/x64/Release/net10.0-windows10.0.19041.0/win-x64/PhotoViewer.Windows.exe" "--pv-stress=D:/photos/A7C2" --pv-stress-count=100 --pv-stress-mode=ab
```

探针按 HIF / ARW / JPG 各取 6 张（首张预热）分段计时，写 `perf-probe.txt` 后自行退出；压测按展示列表连翻（`--pv-stress-mode=ab` 改为相邻两张来回切），逐张写 `stress-flip.jsonl`，`done` 行的 `extra` 含视口、代理目标与纹理上传耗时统计。

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

`pv-check` 支持 `main|analysis|similarity|settings|tools`；报告通过 `adb exec-out run-as cc.xiaojh.PhotoViewer cat files/PhotoViewer/android-checks-<mode>.txt` 读取。具体素材与参数见 [Android 模块手册](PhotoViewer.Android/AGENTS.md)。

### macOS

| Task | 用途 |
|---|---|
| **Debug Mac** | 构建、签名并启动原生 `.app`。 |
| **Run Mac** | 启动已有构建，不重新编译。 |

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

## 打包发布

版本号由根目录 `Directory.Build.props` 控制，发布产物位于 `release/`。

Mac 采用 Developer ID 公证直发，不进入 Mac App Store。首次发布前在 Xcode 的 **Settings → Accounts → Manage Certificates** 创建带本机私钥的 **Developer ID Application** 证书，再把 App 专用密码存入本机钥匙串：

```bash
xcrun notarytool store-credentials "PhotoViewer-notary" \
    --apple-id "<Apple ID>" --team-id "<Team ID>" --password "<App 专用密码>"
```

可用 `MAC_DEVELOPER_ID_IDENTITY` 指定签名身份、`MAC_NOTARY_PROFILE` 指定其他钥匙串配置；默认自动选择首个 Developer ID Application 身份并读取 `PhotoViewer-notary`。凭据不得写入仓库。若 Apple 已返回 `Accepted`、但附加票据时网络失败，运行 `bash PhotoViewer.Mac/publish.sh --staple-only`，只重试票据附加与验证，不重新构建或上传。

| Task | 产物与说明 |
|---|---|
| **Publish Windows EXE (Release)** | 自包含 `PhotoViewer-<版本>-win-x64.exe`，运行时自动解包内置依赖；不启用 trim/R2R。 |
| **Publish Android APK (Release)** | 构建并签名 APK。 |
| **Publish Mac DMG (Release)** | 构建应用，以 Developer ID + Hardened Runtime 逐个签名原生文件，封装并签名 DMG，提交 Apple 公证、附加票据并验证 Gatekeeper 评估。 |
| **Publish iOS IPA (Release)** | 生成版本化 IPA，同时归档 `.app` / `.ipa` 到 `release/ios-stable/`。 |
| **Upload iOS TestFlight** | 使用 `Apple Distribution` 与 `PhotoViewer iOS App Store` Profile 归档并上传 App Store Connect；归档与导出产物位于 `release/testflight/`。上传前自动执行归档检查。 |
| **Validate iOS TestFlight Archive** | 检查当前版本归档的版本号、Bundle ID、分发签名、entitlements、AOT/ONNX 符号与图标，不上传。 |
| **Check iOS TestFlight Device** | 核对设备版本，连续 5 次冷启动并确认进程存活、无新增崩溃报告；设备需先通过 TestFlight 安装当前构建。 |

每次上传 iOS 构建前递增 `AppBuildNumber`，即使 `AppVersion` 不变也不复用构建号。TestFlight 上传复用 Xcode 中已登录的开发者账号，不改变 Release 真机安装所用的开发签名。iOS Release 通过 `MtouchNoSymbolStrip=true` 保留 Mono AOT 启动符号，不能为缩减体积重新开启符号剥离。首次上传前复制 `PhotoViewer.iOS/ExportOptions.AppStore.Local.sample.plist` 为同目录 `ExportOptions.AppStore.Local.plist` 并填写本机 Team ID；该文件已忽略，不提交。

Windows 发布脚本自动定位 Visual Studio 官方 Release x64 CRT；自定义构建环境可向 `PhotoViewer.Windows/publish-win-x64-singlefile.ps1` 传 `-VCRedistDirectory`。脚本在原名/改名 EXE 的独立目录和新缓存中执行验收，通过后归档，并在 `release/rebuild-check/windows-singlefile/` 保存报告与 `deployment.json`。CRT 来源须符合 [微软再分发许可](https://learn.microsoft.com/en-us/visualstudio/releases/2022/redistribution#visual-c-runtime-files)。

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
python3 Tools/compare-layout-geometry.py /tmp/web-geometry.json release/native-p2-acceptance/native-layout-geometry.txt
```

### 在 macOS 类型检查 Windows 壳

WinUI XAML 编译器只能在 Windows 上运行。跨平台修改后可先检查 C# 类型，但不能代替 Windows 本机构建和运行：

```bash
dotnet msbuild Tools/WindowsTypeCheck/WindowsTypeCheck.csproj -restore -t:Compile -v:m
```

### Windows HEVC 解码库

`PhotoViewer.Windows/NativeLibs/win-x64/pvhevc.dll`（静态链接「仅 HEVC 解码器」LGPL FFmpeg）已入库；更换 FFmpeg 版本或工具链后用 MSYS2 CLANG64 重建 `PhotoViewer.Windows/build-native-windows-hevc.ps1`，并把 `pvhevc.dll` 与 `pvhevc.txt` 一起更新。
重建后在 `Tools/HeifTileBench/` 目录执行 `python pvhevc_check.py`（逐瓦片对照 `heif_tile_extract.py` 的 Annex-B 产物与耗时）复核二进制；托管侧容器解析与 YUV 转换的对照验证用 `Tools/HeifFastDecodeTest`（`selftest`/`smoke`/`parse`/`extract`/`validate`/`bench`）。

### EXIF 字段名称

- 英文名称先用 MetadataExtractor，未知字段由 `PhotoViewer/Core/Exif/ExifToolTags.Generated.cs` 补充；重新生成：`python3 Tools/generate-exiftool-tags.py`。
- 中文翻译在 `PhotoViewer/Core/Exif/ExifChinese.Generated.cs`；重新生成：`python3 Tools/generate-chinese-template.py`。
- 两个生成文件都不要手改。人工覆盖分别放在 `ExifToolTags.cs` 的 `_overrideTables`、`ExifChinese.cs` 的 `_overrideNames`；中文键使用英文覆盖后的最终字段名。

### DINOv3 ONNX 模型

`PhotoViewer/Assets/Models/dinov3_vits16.onnx` 受 **Meta DINOv3 License** 约束，不入库；开发者在本地生成。原始权重可从需授权的 HuggingFace `facebook/dinov3-vits16-pretrain-lvd1689m` 或 ModelScope 镜像获取。

准备 Python 环境中的 `torch transformers onnx onnxruntime onnxscript`，然后导出并校验：

```bash
python Training/onnx/export_dinov3_onnx.py \
    --model-id <本地权重目录或HF模型ID> \
    --output PhotoViewer/Assets/Models/dinov3_vits16.onnx
python Training/onnx/verify_onnx_parity.py \
    --model-id <同上> \
    --onnx PhotoViewer/Assets/Models/dinov3_vits16.onnx
```

模型权重内嵌在同一个 ONNX 文件中（`external_data=False`）；一致性校验要求 `cos_mean ≥ 0.999`。模型缺失不影响基础浏览，但相似聚类不可用。
