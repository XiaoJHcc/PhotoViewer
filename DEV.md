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

续期 Profile 前，Xcode 须已登录 Apple ID 并注册设备；首次使用先配置本地 Team ID：

```bash
cp Tools/RefreshIosProfile/Local.xcconfig.sample Tools/RefreshIosProfile/Local.xcconfig
```

编辑 `Local.xcconfig` 的 `DEVELOPMENT_TEAM`；该文件已被忽略，不提交到仓库。

## 打包发布

版本号由根目录 `Directory.Build.props` 控制，发布产物位于 `release/`。

| Task | 产物与说明 |
|---|---|
| **Publish Windows EXE (Release)** | 自包含 `PhotoViewer-<版本>-win-x64.exe`，运行时自动解包内置依赖；不启用 trim/R2R。 |
| **Publish Android APK (Release)** | 构建并签名 APK。 |
| **Publish Mac DMG (Release)** | 构建应用、签名原生依赖并封装 DMG。 |
| **Publish iOS IPA (Release)** | 生成版本化 IPA，同时归档 `.app` / `.ipa` 到 `release/ios-stable/`。 |

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
