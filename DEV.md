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
- 💻 在文件边栏 EXPLORER 底部的 SOLUTION EXPLORER 中，右键点击项目 `PhotoViewer.Windows`，选择 Debug > **Start New Instance**。或：
- ⚡ **Debug Windows**：完整发版构建并启动**原生壳**（WinUI 3）。
- ⚡ **Run Windows**：**仅启动**，不触发构建，直接启动上次构建版本的**原生壳**。
- ⚡ **Debug Windows (Legacy)** / **Run Windows (Legacy)**：同上的旧壳（Avalonia）路径，追加 `--legacy-ui`。
- ⚡ **Compare Windows (Native + Legacy)**：构建后先后启动两壳，用于原生与旧壳的对拍（几何/交互回归）。
- ⚡ **Check Windows Native**：构建并运行定向原生冒烟，隔离设置/数据库/日志和自有照片，结束后自动关闭；报告在 `release/native-win-acceptance/checks.json`，失败退出码 1。覆盖调度/模型/两向 resize/标题栏/Full 热替换，不替代完整 UI、跨屏与色彩验收。
  - 专项（命令行从仓库根运行，参数在 `--native` 后追加）：`--pv-native-settings`（设置窗口 7 项，报告 `settings-checks.json`）、`--pv-native-settings-hold`（保持设置窗口供目视）、`--pv-native-tools`（工具窗口 5 项，报告 `tools-checks.json`）、`--pv-native-tools-hold`（保持工具窗口供截图/目视）。

> Debug 默认原生壳，`--legacy-ui` 显式退回旧壳；Release 相反（默认旧壳，`--native` 进原生），与 macOS 同一套口径。
> 需要直接打开某张照片时在命令末尾追加路径：`dotnet run --project PhotoViewer.Windows -c Debug -- D:\path\a.jpg`。

### 2. Android
*前提：手机已通过 ADB 连接。*

> **macOS 前置依赖**
> 1. **JDK 17+**：`dotnet build` 内部需要 keytool / javac。推荐通过 [Adoptium](https://adoptium.net) 安装，或 `brew install temurin`。安装后确认 `keytool -version` 可用。
> 2. **Android SDK**：首次使用请通过 [Android Studio](https://developer.android.com/studio) 安装，SDK 默认落在 `~/Library/Android/sdk`；项目已内置该路径的备用寻址，无需额外设置环境变量。
> 3. **ADB**：Tasks 已硬编码使用 `~/Library/Android/sdk/platform-tools/adb`，无需将其加入 PATH。

- ⚡ **Debug Android**：编译并自动将新版推送到已连接手机上直接启动，并在随后启动独立终端附加捕捉 Logcat 打印。
- ⚡ **Install Android**：**仅安装**，不触发构建，直接安装上次构建版本。

### 3. Mac
- ⚡ **Debug Mac**：完整触发 `dotnet build` 后，自动启动原生版 `.app`，解决原生的系统签名拦截。
- ⚡ **Run Mac**：**仅启动**，不触发构建，直接打开上次已编译签名完毕的原生版 `.app`。
- ⚡ **Debug Mac (Legacy)** / **Run Mac (Legacy)**：带 `--legacy-ui` 编译或直接启动 Avalonia 旧壳实例。
- ⚡ **Compare Mac (Native + Legacy)**：编译并同时拉起原生版与 Avalonia 旧壳两个实例，便于同屏对比。

> `refactor/native-rebuild` 的 P2：Debug 默认 AppKit + Metal 原生壳；对照旧壳使用 `open -n PhotoViewer.Mac/bin/Debug/net10.0-macos/PhotoViewer.Mac.app --args --legacy-ui`。Release 默认旧壳，验收前不切换发布通道；Release 原生验证用 `--native`。产品验收与程序化冒烟见 [Plans/native-rebuild-phase-2-acceptance.md](Plans/native-rebuild-phase-2-acceptance.md)。

### 4. iOS
*前提：安装 Xcode。模拟器调试需开启任意一台 Simulator (`open -a Simulator`)。真机需配置 Developer Certificate（在 Xcode 中登录 Apple ID 即可自动管理签名）。*
- ⚡ **Clean Build Caches (All Platforms)**：清理仓库内所有平台的 `bin/obj`，并清空 NuGet 临时/http 缓存、Xcode DerivedData、Xamarin iOS/macOS 本机构建缓存；适合版本回退、SDK 升降级、疑似脏缓存时执行。
- ⚡ **Debug iOS Simulator**：构建完毕后，自动透过 `simctl` 覆写推入当前运行的模拟器并拉起（Debug 默认 UIKit 原生壳）。
- ⚡ **Install iOS Simulator**：**仅安装**上一次构建产物至当前运行的模拟器，不重新构建。
- ⚡ **Debug iOS**：构建 arm64 **Release** 产物后，弹窗选择目标设备，使用 `xcrun devicectl` 安装并启动；Release 默认进 Avalonia 旧壳。
- ⚡ **Debug iOS (Native)**：与 **Debug iOS** 相同，但以 `--native` 启动 iPad 原生壳（Release 配置、真机程序化验收用这条）。
- ⚡ **Install iOS**：弹窗选择目标设备，将上一次构建产物覆写安装至真机并启动，不重新构建。
- ⚡ **Install iOS Stable (Release)**：将 `release/ios-stable/PhotoViewer.iOS.app` 中归档的稳定包重新安装到真机并启动，不依赖当前 Release 输出目录。
- ⚡ **Debug iOS Simulator (Legacy)**：与 **Debug iOS Simulator** 相同，但带 `--legacy-ui` 启动 Avalonia 旧壳，便于与原生壳对照。

> `refactor/native-rebuild` 的 I1–I5 已落地：**Debug 默认 UIKit 原生壳**（不创建 Avalonia 对象），I1 基座、I2 主图（Metal + `UIScrollView` 手势 + 编码协商）、I3 主屏布局（文件栏/胶片栏/筛选条/控制栏、安全区、两向与自动布局）、I4 输入（上下文菜单/硬件键盘/滚轮/轻扫翻页/拖入/设置回写）、I5 P3 功能（分析栏/相似聚类/工具页/增强预览）全部可用。旧壳启动加 `--legacy-ui`，或写入开关 `defaults write cc.xiaojh.PhotoViewer PVLegacyUI -bool YES`。**Release 默认旧壳**，模拟器与真机同规则：原生壳用 `--native`（真机开发用 **Debug iOS (Native)**）。启动参数由 `NSProcessInfo` 传入，故 `xcrun simctl launch <udid> cc.xiaojh.PhotoViewer --pv-open <路径>` 可直接验证原生壳的外部打开链路。
>
> **程序化验收钩子**（参数只在验收会话生效，报告写 App 沙盒 `Documents/PhotoViewer/`）：`--pv-native-photo-check <目录>`、`--pv-native-layout-check <目录>`、`--pv-native-input-check <目录>`、`--pv-native-analysis-check <目录>`、`--pv-native-similarity-check <目录>`、`--pv-native-tools-check <目录>`、`--pv-dino-check <文件>`；辅助开关 `--pv-layout-mode row|col`、`--pv-analysis-open`。素材要求、真机 `devicectl` 步骤与各套件断言见 [PhotoViewer.iOS/AGENTS.md](PhotoViewer.iOS/AGENTS.md) "验收线索"；计划与结果见 [Plans/native-rebuild-phase-ipad.md](Plans/native-rebuild-phase-ipad.md)，作者手验见 [Plans/native-rebuild-phase-ipad-acceptance.md](Plans/native-rebuild-phase-ipad-acceptance.md)。

- ⚡ **Refresh iOS Profile**：免费 Apple ID 的 Provisioning Profile 每 7 天过期一次，过期后 **Debug iOS** 会构建失败（`找不到任何可用预配配置文件`）。执行此 Task 调用占位 Xcode 工程 `Tools/RefreshIosProfile/` 触发 `xcodebuild -allowProvisioningUpdates` 续期同 bundle id 的 Profile，随后再跑 **Debug iOS** 即可正常安装到真机。
  - **首次使用**：先复制本地配置并填入你的 Apple Developer Team ID：
    ```
    cp Tools/RefreshIosProfile/Local.xcconfig.sample Tools/RefreshIosProfile/Local.xcconfig
    # 编辑 Local.xcconfig，把 DEVELOPMENT_TEAM 替换为你的 10 位 Team ID
    ```
    Team ID 可在 Xcode → Settings → Accounts 中查到。`Local.xcconfig` 已在 `.gitignore` 中，不会提交。
  - **前提**：Xcode 已登录你的 Apple ID，且目标真机已在该 Apple ID 下注册（免费账号上限 3 台）。

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
- 包含签名对齐。首次发布前需生成证书：
  - **Windows**：在 `PhotoViewer.Android/` 目录运行 `setup-android-signing.ps1`
  - **macOS**：运行 `bash PhotoViewer.Android/setup-android-signing.sh`
  - 证书配置写入 `signing.json`（已被 .gitignore 排除）。

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

---

### DINOv3 ONNX 模型

相似聚类使用 DINOv3 ViT-S/16 的 [CLS] 特征。模型文件 `PhotoViewer/Assets/Models/dinov3_vits16.onnx` 受 **Meta DINOv3 License** 约束，**不入库**（`.gitignore` 已拦截）；每位开发者在本地生成一次即可。

1. **获取原始权重**（任选其一）：
   - **HuggingFace**（需登录并接受 gated license）：`facebook/dinov3-vits16-pretrain-lvd1689m`
   - **ModelScope 镜像**：下载到 `~/.cache/modelscope/hub/models/facebook/dinov3-vits16-pretrain-lvd1689m`

2. **导出 ONNX**（首次需 `pip install torch transformers onnx onnxruntime onnxscript`）：
   ```
   python Training/onnx/export_dinov3_onnx.py \
       --model-id <本地权重目录或 HF id> \
       --output PhotoViewer/Assets/Models/dinov3_vits16.onnx
   ```
   产物约 82 MB，权重内嵌为单文件（`external_data=False`）。

3. **验证一致性**（可选，首次导出或改脚本时建议跑）：
   ```
   python Training/onnx/verify_onnx_parity.py \
       --model-id <同上> \
       --onnx PhotoViewer/Assets/Models/dinov3_vits16.onnx
   ```
   输出 `cos_mean ≥ 0.999` 即通过。

模型缺失时应用仍可运行，只是相似面板为空、日志会打印一行 `model missing` 提示。
