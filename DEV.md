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
- ⚡ **Debug iOS Simulator**：构建完毕后，自动透过 `simctl` 覆写推入当前运行的模拟器并拉起。
- ⚡ **Install iOS Simulator**：**仅安装**上一次构建产物至当前运行的模拟器，不重新构建。
- ⚡ **Debug iOS**：构建 arm64 产物后，弹窗选择目标设备，使用 `xcrun devicectl` 安装并启动。
- ⚡ **Install iOS**：弹窗选择目标设备，将上一次构建产物覆写安装至真机并启动，不重新构建。

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
- 构建生产发布包，配合 Apple 开发者证书编译；如须上架则需将产物通过 Xcode/Transporter 继续流转。
