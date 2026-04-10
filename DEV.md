# PhotoViewer 开发与发布指南

本文档介绍如何在一台全新的电脑上，仅使用 VS Code 和基础环境，从零开始搭建、调试直至发布本项目的桌面端与移动端。

## 一、 环境依赖与扩展安装

1. **核心环境安装**
   - **[.NET 9 SDK](https://dotnet.microsoft.com/download/dotnet/9.0)**：所有平台通用的核心环境，只需装它即可。
   - **安卓开发工作负载 (可选)**：打开终端（`Ctrl+~`），运行指令 `dotnet workload install android` 即可自动拉取 Android 所需的底层链。

2. **VS Code 扩展 (必装)**

   当使用 VS Code 打开工程时，右下角会弹出推荐扩展列表，请**全部安装**以获得完整的开发体验：
   - **C# Dev Kit** (`ms-dotnettools.csdevkit`)：提供项目资源管理器树、智能补全以及 .NET Core 的调试支持。
   - **C#** (`ms-dotnettools.csharp`)：与 C# Dev Kit 同步安装。
   - **Avalonia for VS Code** (`avalonia.avalonia-vscode`)：提供 XAML UI 实时预览和语法检查。

*注意：首次安装完 C# Dev Kit 后，需要登录账号，并静待扩展在后台把项目的依赖拉好（若有输出面板或右下角进度，就耐心等完），以确保智能提示和代码高亮完全生效。*

---

## 二、 操作方式指引

为了避免赘述，本工程的日常开发动作统一抽象为以下三种标准方式：

- 🖱️ **UI 操作**：特指在侧边栏 **文件管理器 (Explorer)** 底部的 **解决方案资源管理器 (Solution Explorer)** 中右键目标项目(例如PhotoViewer.Desktop)，选择 `生成 (Build)`、`清理 (Clean)` 或 `调试 (Debug) -> 启动新实例 (Start New Instance)`。
- ⚡ **Task 任务**：按 `F1` 或 `Ctrl+Shift+P` 呼出顶部命令面板，输入 `>task` 选择 **Tasks: Run Task (运行任务)**，从中下拉选取。
- 💻 **CLI 指令**：按下快捷键 `Ctrl+~` 呼出集成终端，直接执行脚本或 dotnet 命令。

> ⚠️ **注意**： UI 菜单自带的 `Publish (发布)` 不包含本项目深度定制的系统原生剪裁或签名逻辑，**请严格避免直接点击 UI 发布**！打包分发请认准下文指明的 Task 或 CLI 指令。

---

## 三、 运行与调试

### 1. Windows
- 🖱️ **UI**: 右键 `PhotoViewer.Desktop` -> **调试 -> 启动新实例**。
- 💻 **CLI (带参启图)**: `dotnet run --project PhotoViewer.Desktop "C:\path\to\image.jpg"`

### 2. Android
*前提条件：手机已通过 USB 连接到电脑，并处于“USB 调试”模式。*
- ⚡ **Task (推荐应用流)**: 输入 `>task` 选择运行 **`Build Android (Debug)`**。这会通过内置的安卓构建引擎直接把现烤的包强推安装并在手机上打开，兼容最纯净的基础工作链。
- ⚡ **Task (查验日志)**: 程序运行后，随时输入 `>task` 选择运行 **`Watch Android Logcat`**。它会在后台挂载并捕捉崩溃及排错日志。

> **⚠️ 关于安卓断点调试与 UI 启动 (必读)**
> 经过核实，在**不安装 Android Studio** 的极简 VS Code 生态下，C# Dev Kit 会直接拒绝安卓平台的右键 `执行新实例` 或设备下发（报错 `No launchable target found` / 或一直要求配齐 SDK 组件）。这是微软当前跨平台组件的底层依赖限制。因此对于日常开发，推荐以上述的 **Task** 搭配真机效果和 Log 辅助作为核心流——这绝对能顺畅跑通。

### 3. Mac

### 4. iOS

---

## 四、 打包发布 (Release)

版本号由仓库根目录 `Directory.Build.props` 统一中控。各平台的最终产物均会自动合并输出到 `release/` 目录下。

### 1. Windows 单文件 EXE 
原生支持剪裁和免框架的单文件执行。
- ⚡ **Task**: 运行 **`Publish Windows (Release)`**
- 💻 **CLI**: 
  ```powershell
  powershell -ExecutionPolicy Bypass -File .\PhotoViewer.Desktop\publish-win-x64-singlefile.ps1
  ```

### 2. Android APK (含签名对齐)
*(正式发布前，请确保在根目录使用 `keytool` 生成了正式的 `release.keystore`)*
- ⚡ **Task**: 运行 **`Publish Android APK (Release)`**
- 💻 **CLI**: 
  ```powershell
  powershell -ExecutionPolicy Bypass -File .\PhotoViewer.Android\publish-android-apk.ps1 -AndroidSdkDirectory "$env:LOCALAPPDATA\Android\Sdk"
  ```

### 3. macOS App Bundle

### 4. iOS IPA (仅开发者设备调试)

---

## 五、 NuGet 包管理

跨平台组件版本由 `Directory.Packages.props` 集中式管理。
- 🖱️ **UI**: 若只升级已有包的版本号，直接去 `Directory.Packages.props` 修改对应数字。
- 💻 **CLI**: 若要引入全新依赖，在某项目级目录下执行：`dotnet add package [内容]`。