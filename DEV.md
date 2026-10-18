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
- ⚡ **Debug Windows**：完整构建并启动 **WinUI 3 原生壳**。
- ⚡ **Run Windows**：**仅启动**，不触发构建，直接启动上次构建版本的原生壳。
- ⚡ **Check Windows Native**：构建并运行定向原生冒烟，隔离设置/数据库/日志和自有照片，结束后自动关闭；报告在 `release/native-win-acceptance/checks.json`，失败退出码 1。覆盖调度/模型/两向 resize/标题栏/Full 热替换，不替代完整 UI、跨屏与色彩验收。
  - 专项（命令行从仓库根运行，参数直接追加在 `--` 后）：`--pv-native-settings`（设置窗口 7 项，报告 `settings-checks.json`）、`--pv-native-settings-hold`（保持设置窗口供目视）、`--pv-native-tools`（工具窗口 5 项，报告 `tools-checks.json`）、`--pv-native-tools-hold`（保持工具窗口供截图/目视）。

> Debug 与 Release 都直接进原生壳，没有旧壳入口或分流参数（历史 `--native` / `--legacy-ui` 传入时被忽略）。
> 需要直接打开某张照片时在命令末尾追加路径：`dotnet run --project PhotoViewer.Windows -c Debug -- D:\path\a.jpg`。

### 2. Android
*前提：手机已通过 ADB 连接。*

> **macOS 前置依赖**
> 1. **Android workload**：`sudo dotnet workload install android`（本机 dotnet 在 `/usr/local/share/dotnet`，需要管理员）。`dotnet workload list` 出现 `android` 后 Android 头才能构建；否则报 `NETSDK1147`。**本机 10.0.201 下已装 android workload，默认 10.0.402 下没有**：临时用后者构建时，在临时目录放 `{"sdk":{"version":"10.0.201","rollForward":"disable"}}` 的 `global.json`，再从该目录用绝对路径 `dotnet build`；不要改仓库 `global.json`（Apple 头需要 10.0.402 的 27 工作负载）。
> 2. **JDK 17+**：`dotnet build` 内部需要 keytool / javac。推荐通过 [Adoptium](https://adoptium.net) 安装，或 `brew install temurin`。安装后确认 `keytool -version` 可用（本机为 Temurin 21）。
> 3. **Android SDK**：可用 [Android Studio](https://developer.android.com/studio)，也可只用 cmdline-tools。SDK 默认落在 `~/Library/Android/sdk`，项目已内置该路径的备用寻址，无需额外设置环境变量。
> 4. **模拟器（Apple Silicon）**：镜像必须 `arm64-v8a`，`x86_64` 镜像不可用。本机已装 `platforms;android-35`、`emulator`、`system-images;android-35;google_apis;arm64-v8a` 并创建 AVD `pv-api35`；启动：`~/Library/Android/sdk/emulator/emulator -avd pv-api35`。
> 5. **ADB**：Tasks 已硬编码使用 `~/Library/Android/sdk/platform-tools/adb`，无需将其加入 PATH。

- ⚡ **Debug Android**：编译并自动将新版推送到已连接设备（真机或模拟器）直接启动，并在随后启动独立终端附加捕捉 Logcat 打印。
- ⚡ **Install Android**：**仅安装**，不触发构建，直接安装上次构建版本。

> **唯一入口**：Debug 与 Release 都只启动原生壳。唯一 launcher 是 `AndroidBootstrapActivity`（转发 `ACTION_VIEW` / `ACTION_SEND` 到 `AndroidNativeActivity`），目标 Activity 不带 launcher/filter；历史 `pv-flags` 分流参数已不存在。
>
> **原生壳验收（A6 起步）**：夹具先推到 App 内部目录（`adb push` 到 `/data/local/tmp` 后 `run-as cc.xiaojh.PhotoViewer cp` 到 `files/pv-fixtures`），再：
> `adb shell am start -n cc.xiaojh.PhotoViewer/.AndroidBootstrapActivity --es pv-open @files/pv-fixtures --es pv-check main`
> （`pv-check` 可取 `main|analysis|similarity|settings|tools`；报告 `adb exec-out run-as cc.xiaojh.PhotoViewer cat files/PhotoViewer/android-checks-<mode>.txt`）。
> 设置半屏可直接用 `--ez pv-settings true` 在启动后打开。

### 3. Mac
- ⚡ **Debug Mac**：完整触发 `dotnet build` 后，自动启动原生版 `.app`，解决原生的系统签名拦截。
- ⚡ **Run Mac**：**仅启动**，不触发构建，直接打开上次已编译签名完毕的原生版 `.app`。

> Debug 与 Release 都直接进 AppKit + Metal 原生壳（历史 `--legacy-ui` / `--native` 参数被忽略）。产品验收与程序化冒烟见 [Plans/native-rebuild-phase-2-acceptance.md](Plans/native-rebuild-phase-2-acceptance.md)。

### 4. iOS
*前提：安装 Xcode。模拟器调试需开启任意一台 Simulator (`open -a Simulator`)。真机需配置 Developer Certificate（在 Xcode 中登录 Apple ID 即可自动管理签名）。*
- ⚡ **Clean Build Caches (All Platforms)**：清理仓库内所有平台的 `bin/obj`，并清空 NuGet 临时/http 缓存、Xcode DerivedData、Xamarin iOS/macOS 本机构建缓存；适合版本回退、SDK 升降级、疑似脏缓存时执行。
- ⚡ **Debug iOS Simulator**：构建完毕后，自动透过 `simctl` 覆写推入当前运行的模拟器并拉起（UIKit 原生壳）。
- ⚡ **Install iOS Simulator**：**仅安装**上一次构建产物至当前运行的模拟器，不重新构建。
- ⚡ **Debug iOS**：构建 arm64 **Release** 产物后，弹窗选择目标设备，使用 `xcrun devicectl` 安装并启动（Release 配置同样直接进原生壳，真机程序化验收用这条）。
- ⚡ **Install iOS**：弹窗选择目标设备，将上一次构建产物覆写安装至真机并启动，不重新构建。
- ⚡ **Install iOS Stable (Release)**：将 `release/ios-stable/PhotoViewer.iOS.app` 中归档的稳定包重新安装到真机并启动，不依赖当前 Release 输出目录。

> I1–I5 已落地，Debug 与 Release 都只启动 **UIKit 原生壳**（历史 `--native` / `--legacy-ui` 参数与 `PVLegacyUI` 偏好不再参与壳选择）：I1 基座、I2 主图（Metal + `UIScrollView` 手势 + 编码协商）、I3 主屏布局（文件栏/胶片栏/筛选条/控制栏、安全区、两向与自动布局）、I4 输入（上下文菜单/硬件键盘/滚轮/轻扫翻页/拖入/设置回写）、I5 P3 功能（分析栏/相似聚类/工具页/增强预览）全部可用。启动参数由 `NSProcessInfo` 传入，故 `xcrun simctl launch <udid> cc.xiaojh.PhotoViewer --pv-open <路径>` 可直接验证外部打开链路。
>
> **程序化验收钩子**（参数只在验收会话生效，报告写 App 沙盒 `Documents/PhotoViewer/`）：`--pv-native-photo-check <目录>`、`--pv-native-gesture-check <目录>`、`--pv-native-layout-check <目录>`、`--pv-native-input-check <目录>`、`--pv-native-analysis-check <目录>`、`--pv-native-similarity-check <目录>`、`--pv-native-tools-check <目录>`、`--pv-dino-check <文件>`；辅助开关 `--pv-layout-mode row|col`、`--pv-analysis-open`。素材要求、真机 `devicectl` 步骤与各套件断言见 [PhotoViewer.iOS/AGENTS.md](PhotoViewer.iOS/AGENTS.md) "验收线索"；计划与结果见 [Plans/native-rebuild-phase-ipad.md](Plans/native-rebuild-phase-ipad.md)，作者手验见 [Plans/native-rebuild-phase-ipad-acceptance.md](Plans/native-rebuild-phase-ipad-acceptance.md)。

- ⚡ **Refresh iOS Profile**：免费 Apple ID 的 Provisioning Profile 每 7 天过期一次，过期后 **Debug iOS** 会构建失败（`找不到任何可用预配配置文件`）。执行此 Task 调用占位 Xcode 工程 `Tools/RefreshIosProfile/` 触发 `xcodebuild -allowProvisioningUpdates` 续期同 bundle id 的 Profile，随后再跑 **Debug iOS** 即可正常安装到真机。
  - **首次使用**：先复制本地配置并填入你的 Apple Developer Team ID：
    ```
    cp Tools/RefreshIosProfile/Local.xcconfig.sample Tools/RefreshIosProfile/Local.xcconfig
    # 编辑 Local.xcconfig，把 DEVELOPMENT_TEAM 替换为你的 10 位 Team ID
    ```
    Team ID 可在 Xcode → Settings → Accounts 中查到。`Local.xcconfig` 已在 `.gitignore` 中，不会提交。
  - **前提**：Xcode 已登录你的 Apple ID，且目标真机已在该 Apple ID 下注册（免费账号上限 3 台）。

> **首次真机运行须信任开发者证书**：安装后如果应用无法启动，在 iPhone 进入 **设置 → 通用 → VPN 与设备管理** 找到对应的开发者帐号，点击**信任**后重新执行 **Install iOS** 即可。

> **多台设备**：执行 Debug iOS / Install iOS 时会弹窗显示所有已配置的设备名供选择（默认 XJH-mini7，另一项为 iPhone 17 `XJH-17`）。如需增减设备，在 `.vscode/tasks.json` 的 `inputs[iosDevice].options` 数组中编辑。

> **工具链版本（2026-10-18）**：iOS/macOS 构建使用 **Xcode 27.1 + .NET SDK 10.0.402 + ios/macos workload 27.0.10722**。该 workload 声明需要 Xcode 27.0，SDK 的 `_ValidateXcodeVersion` 做 major.minor 精确比较，会把 27.1 判为不匹配；两个 head 的 csproj 因此设了 `ValidateXcodeVersion=false`（27.1 已实测构建、安装、在 iPhone 17 / iOS 27.0.1 与 iPad mini / iOS 26.6 上启动正常）。发布支持 Xcode 27.1 的 workload 后应删除该属性，恢复版本校验。

---

## 四、 打包发布 (Release)

版本号统一由仓库根目录 `Directory.Build.props` 控制。各个 Task 执行完毕后，所有文件均沉淀在 `release/` 目录。

### 1. Windows 单文件
- ⚡ Task: **`Publish Windows EXE (Release)`**
- 原生支持剪裁，打包成单文件免依赖执行程序。
- **已知限制（2026-10-19）**：单文件产物缺 `PhotoViewer.Windows.pri` 时 `Application.Start` 抛 `0x80040111`，单文件发布尚未打通；多文件发布可用。单文件发布前需先修 PRI/运行时打包（见 [PhotoViewer.Windows/AGENTS.md](PhotoViewer.Windows/AGENTS.md) 与 [Plans/native-rebuild-phase-6.md](Plans/native-rebuild-phase-6.md) 6E-2）。

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

### 设计令牌

`Design/tokens.json` 是尺寸、颜色、动效数值的唯一来源；改值后重新生成共享常量 `PhotoViewer/Core/Design/DesignTokens.g.cs` 与主窗口样机令牌 `Design/prototype/tokens.js`（两份都请勿手改，`--check` 同时校验）：
```
python3 Tools/generate-design-tokens.py          # 生成
python3 Tools/generate-design-tokens.py --check  # 校验是否最新
```

主窗口样机几何与 macOS 验收基准比对（`release/` 产物不提交）：
```
node Design/prototype/export-geometry.mjs --out /tmp/proto-geometry.json
python3 Tools/compare-layout-geometry.py /tmp/proto-geometry.json release/native-p2-acceptance/native-layout-geometry.txt
```

### 在 macOS 上类型检查 Windows 壳

WinUI XAML 编译器只能在 Windows 运行，macOS 上完整构建 `PhotoViewer.Windows` 会止于 XamlCompiler。改 Windows 壳代码后至少跑一次 C# 类型检查（不产出可运行程序，不替代 Windows 本机构建与 harness）：
```
dotnet msbuild Tools/WindowsTypeCheck/WindowsTypeCheck.csproj -restore -t:Compile -v:m
```

---

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
