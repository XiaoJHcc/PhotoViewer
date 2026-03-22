# PUBLISH.md — PhotoViewer

全项目打包发布说明统一维护在这里。

> 日常开发请直接使用 IDE（推荐 JetBrains Rider）打开解决方案；本文件专用于**分发产物**（`.exe` / `.apk` / `.dmg`）的构建说明。

## 统一约定

- 版本号统一来自 `Directory.Build.props`
  - `AppVersion`：显示版本号
  - `AppBuildNumber`：平台构建号
- 所有打包脚本在完成后，都会额外复制一份最终产物到仓库根目录的 `release/` 文件夹
- `release/` 目录用于集中收集分发文件，已加入 Git 忽略规则

## Windows 单文件 EXE

入口脚本：`PhotoViewer.Desktop/publish-win-x64-singlefile.ps1`

```powershell
powershell -ExecutionPolicy Bypass -File .\PhotoViewer.Desktop\publish-win-x64-singlefile.ps1
```

默认输出：

- 原始发布目录：`PhotoViewer.Desktop\bin\Release\publish-singlefile\win-x64\PhotoViewer.Desktop.exe`
- 汇总目录副本：`release\PhotoViewer-<AppVersion>-win-x64.exe`

说明：

- 当前仅支持 `win-x64`
- 原因是仓库当前只引用了 `LibHeif.Native.win-x64`
- 脚本会在发布期间**临时**将 `PhotoViewer/PhotoViewer.csproj` 收窄为 `net9.0`，发布完成后自动恢复，以避免被共享项目的 `net9.0-ios` 目标阻塞

## Android APK

入口脚本：`PhotoViewer.Android/publish-android-apk.ps1`

```powershell
powershell -ExecutionPolicy Bypass -File .\PhotoViewer.Android\publish-android-apk.ps1
```

如需显式指定 Android SDK 路径：

```powershell
powershell -ExecutionPolicy Bypass -File .\PhotoViewer.Android\publish-android-apk.ps1 -AndroidSdkDirectory "$env:LOCALAPPDATA\Android\Sdk"
```

默认输出：

- 原始发布目录：`PhotoViewer.Android\bin\Release\net9.0-android\publish\`
- 汇总目录副本：
  - `release\PhotoViewer-<AppVersion>-android.apk`
  - `release\PhotoViewer-<AppVersion>-android-signed.apk`

说明：

- 如果未配置正式 keystore，`*-Signed.apk` 通常仍为调试签名，适合本地安装和测试，不适合直接上架商店
- 脚本也会临时将共享项目收窄为 `net9.0`，发布完成后自动恢复

## macOS DMG

入口脚本：`PhotoViewer.Mac/publish.sh`

```bash
cd PhotoViewer.Mac
bash publish.sh
```

也可以显式传入版本号：

```bash
cd PhotoViewer.Mac
bash publish.sh 0.4.0
```

默认输出：

- 原始发布目录：`PhotoViewer.Mac/bin/Release/dist/PhotoViewer-<AppVersion>-arm64.dmg`
- 汇总目录副本：`release/PhotoViewer-<AppVersion>-arm64.dmg`

说明：

- 若未传入版本号，脚本默认从 `Directory.Build.props` 读取 `AppVersion`
- 当前脚本面向 `osx-arm64`

## 当前分发范围

当前已明确提供分发脚本或打包入口的平台：

- Windows
- Android
- macOS

`iOS` 当前仍以 Rider / Xcode 构建与设备部署为主，不在本文件的分发产物范围内。

