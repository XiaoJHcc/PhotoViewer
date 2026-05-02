# PhotoViewer 开发与发布指南

本文档精简并以 Task 为导向，介绍如何在全新环境中，仅使用 VS Code 从零搭建并发布本项目。各平台的命令行操作均已封装为一键 Task，聚焦于“怎么做”。

## 一、 环境依赖与扩展安装

1. **核心环境安装**
   - **[.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)**（或更高版本）。
   - **必需工作负载**（需 `sudo` 权限）：
     - macOS / iOS：`sudo dotnet workload install macos ios`
     - Android：`sudo dotnet workload install android`

2. **必装 VS Code 扩展**
   - **C# Dev Kit** (`ms-dotnettools.csdevkit`)
   - **C#** (`ms-dotnettools.csharp`) 
   - **Avalonia for VS Code** (`avalonia.avalonia-vscode`)

*注：首次安装 C# Dev Kit 后需登录账号，等待依赖加载完毕，使智能提示生效。*

---

## 二、 操作方式指引

本工程的运行和发布统一使用 VS Code Task 执行，屏蔽细枝末节的配置。
- ⚡ **Task 任务操作**：按 `F1` 或 `Ctrl+Shift+P` 呼出命令面板，输入 `>task` 并选择 **Tasks: Run Task (运行任务)**，从中挑选对应命令。

> ⚠️ **注意**：请避免直接使用 UI 菜单的 `Publish (发布)` 选项，打包分发请务必使用下方约定的 Task，内部包含了代码签名和剪裁等专属操作。

---

## 三、 运行与调试 (Debug 构建)

### 1. Windows
- 💻 在文件边栏 EXPLORER 底部的 SOLUTION EXPLORER 中，右键点击项目 `PhotoViewer.Desktop`，选择 Debug > **Start New Instance**。或：
- ⚡ **Debug Windows**：完整发版构建并以此为新实例启动。
- ⚡ **Run Windows**：**仅启动**，不触发构建，直接启动上次构建版本。

### 2. Android
*前提：手机已通过 ADB 连接。*
- ⚡ **Debug Android**：编译并自动将新版推送到已连接手机上直接启动，并在随后启动独立终端附加捕捉 Logcat 打印。
- ⚡ **Install Android**：**仅安装**，不触发构建，直接安装上次构建版本。

### 3. Mac
- ⚡ **Debug Mac**：完整触发 `dotnet build` 后，自动 `open .app`，解决原生的系统签名拦截。
- ⚡ **Run Mac**：**仅启动**，不触发构建，直接 `open` 打开上次已编译签名完毕的 `.app`。

### 4. iOS
*前提：安装 Xcode。模拟器调试需开启任意一台 Simulator (`open -a Simulator`)。真机需配置 Developer Certificate（在 Xcode 中登录 Apple ID 即可自动管理签名）。*
- ⚡ **Clean Build Caches (All Platforms)**：清理仓库内所有平台的 `bin/obj`，并清空 NuGet 临时/http 缓存、Xcode DerivedData、Xamarin iOS/macOS 本机构建缓存；适合版本回退、SDK 升降级、疑似脏缓存时执行。
- ⚡ **Debug iOS Simulator**：构建完毕后，自动透过 `simctl` 覆写推入当前运行的模拟器并拉起。
- ⚡ **Install iOS Simulator**：**仅安装**上一次构建产物至当前运行的模拟器，不重新构建。
- ⚡ **Debug iOS**：构建 arm64 产物后，弹窗选择目标设备，使用 `xcrun devicectl` 安装并启动。
- ⚡ **Install iOS**：弹窗选择目标设备，将上一次构建产物覆写安装至真机并启动，不重新构建。
- ⚡ **Install iOS Stable (Release)**：将 `release/ios-stable/PhotoViewer.iOS.app` 中归档的稳定包重新安装到真机并启动，不依赖当前 Release 输出目录。

- ⚡ **Renew iOS Certificate**：清除本地 Provisioning Profile 缓存，强制重新构建以生成新 Profile（7 天免费证书期限从今天重新计算），然后安装并启动。每次证书过期后执行一次即可。

> **首次真机运行须信任开发者证书**：安装后如果应用无法启动，在 iPhone 进入 **设置 → 通用 → VPN 与设备管理** 找到对应的开发者帐号，点击**信任**后重新执行 **Install iOS** 即可。

> **多台设备**：执行 Debug iOS / Install iOS 时会弹窗显示所有已配置的设备名供选择（默认 XJH-mini7）。如需增减设备，在 `.vscode/tasks.json` 的 `inputs[iosDevice].options` 数组中编辑。

---

## 四、 打包发布 (Release)

版本号统一由仓库根目录 `Directory.Build.props` 控制。各个 Task 执行完毕后，所有文件均沉淀在 `release/` 目录。

### 1. Windows 单文件
- ⚡ Task: **`Publish Windows EXE (Release)`**
- 原生支持剪裁，打包成真正的单文件免依赖执行程序。

### 2. Android APK
- ⚡ Task: **`Publish Android APK (Release)`**
- 包含签名对齐。首次发布前需在 `PhotoViewer.Android/` 目录运行 `setup-android-signing.ps1` 生成证书（配置写入 `signing.json`，已被 .gitignore 排除）。

### 3. macOS App & DMG
- ⚡ Task: **`Publish Mac DMG (Release)`**
- 内部自动拆分 `restore` 规避 SDK Bug，完成 App 封包归档并对所有原生依赖附加 macOS 必要代码签名，最终封装为 DMG。

### 4. iOS IPA (限开发者受托设备)
- ⚡ Task: **`Publish iOS IPA (Release)`**
- 构建生产发布包，并额外把稳定包归档到 `release/ios-stable/PhotoViewer.iOS.app` 与 `release/ios-stable/PhotoViewer.iOS.ipa`；同时复制一份版本化 IPA 到 `release/PhotoViewer-版本号-ios-arm64.ipa`。
- 若你想保留一个可随时回装的稳定真机版本，先执行此 Task 固化产物，之后开发过程中随时运行 **Install iOS Stable (Release)** 即可恢复到这份稳定包。

---

## 五、 手动维护/更新数据

### EXIF 字段名称（英文）

EXIF 字段英文名由两层决定：
1. **MetadataExtractor 库**：已知字段由库自动识别，无需维护。
2. **ExifTool 补充名称**（`PhotoViewer/Core/Exif/ExifToolTags.Generated.cs`）：对库无法识别的 Unknown tag，从 ExifTool Perl 源码爬取英文名作为补充。通过以下脚本更新：
   ```
   python3 Tools/generate-exiftool-tags.py
   ```
   脚本会完全覆盖 `PhotoViewer/Core/Exif/ExifToolTags.Generated.cs`，请勿手动编辑该文件。

**手动纠正/补充英文名**：若生成表中某条目名称有误、或存在脚本未收录的私有 tag（如 `0x7038`），在 `PhotoViewer/Core/Exif/ExifToolTags.cs` 的 `_overrideTables` 对应品牌下添加条目，优先级高于生成表，且不受脚本覆盖影响：
```csharp
["Sony"] = new Dictionary<int, string>
{
    [0x0201] = "PreviewImageStart",   // 纠正：生成表误标为 MoreInfo0201
    [0x7038] = "SonyRawImageSize",    // 补充：生成表缺失的私有 tag
},
```

---

### EXIF 字段名称（中文）

中文名同样由两层决定：
1. **自动生成表**（`PhotoViewer/Core/Exif/ExifChinese.Generated.cs`）：内嵌预置中文翻译，通过以下脚本生成/更新：
   ```
   python3 Tools/generate-chinese-template.py
   ```
   脚本会完全覆盖 `PhotoViewer/Core/Exif/ExifChinese.Generated.cs`，请勿手动编辑该文件。生成表中注释掉的条目表示尚未翻译，可以复制到手动覆盖表中覆盖翻译。

2. **手动覆盖表**（`PhotoViewer/Core/Exif/ExifChinese.cs` 中的 `_overrideNames`）：优先级最高，脚本永不修改此文件。在此添加/修改，永久生效：
   ```csharp
   ["PreviewImageStart"] = "预览图像起始",
   ["SonyRawImageSize"]  = "RAW 图像尺寸",
   ```
   键名使用最终确定的英文名（经过上方 `_overrideTables` 修正后的名称）。
