<#
.SYNOPSIS
    交叉编译 libde265 (静态) + libheif (共享) 为 Android .so，输出到 NativeLibs/{abi}/。

.DESCRIPTION
    构建策略：
      · libde265 编译为静态库 (.a)，再链接进 libheif.so
      · libheif 编译为共享库 (.so)，只需分发此一个文件
      · 使用 ANDROID_STL=c++_static，无需额外分发 libc++_shared.so

    依赖环境：
      · Android NDK r21+（推荐 r25+）
      · CMake 3.21+（Android SDK Manager 可安装，自带 Ninja）
      · 网络连接（从 GitHub 下载源码）

    NDK 探测顺序：
      1. -NdkRoot 参数
      2. 环境变量 ANDROID_NDK_HOME / ANDROID_NDK / ANDROID_NDK_ROOT
      3. ANDROID_HOME\ndk\ 下最新版本
      4. %LOCALAPPDATA%\Android\Sdk\ndk\ 下最新版本

.PARAMETER NdkRoot
    Android NDK 根目录，留空则自动探测。

.PARAMETER LibheifVersion
    libheif 版本号，默认 1.18.2（需 >= 1.17.0，与 LibHeifSharp 3.x 兼容）。

.PARAMETER Libde265Version
    libde265 版本号，默认 1.0.15。

.PARAMETER Abis
    要编译的 ABI 列表，默认 arm64-v8a、armeabi-v7a、x86_64。

.PARAMETER ApiLevel
    最低 Android API 级别，默认 21。

.PARAMETER BuildDir
    临时构建目录，默认脚本同级的 .build-android-native/（可安全删除）。

.EXAMPLE
    .\build-native-android.ps1

.EXAMPLE
    .\build-native-android.ps1 -NdkRoot "C:\Android\Sdk\ndk\27.2.12479018" -Abis @("arm64-v8a")

.EXAMPLE
    .\build-native-android.ps1 -LibheifVersion "1.19.1" -Libde265Version "1.0.15"
#>
[CmdletBinding()]
param(
    [string]   $NdkRoot        = "",
    [string]   $LibheifVersion  = "1.18.2",
    [string]   $Libde265Version = "1.0.15",
    [string[]] $Abis            = @("arm64-v8a", "armeabi-v7a", "x86_64"),
    [int]      $ApiLevel        = 21,
    [string]   $BuildDir        = (Join-Path $PSScriptRoot ".build-android-native")
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

# ════════════════════════════════════════════════════════════
# 工具探测函数
# ════════════════════════════════════════════════════════════

<#
.SYNOPSIS 自动查找 Android NDK 根目录。
#>
function Find-NdkRoot {
    # 1. 命令行参数
    if ($NdkRoot -and (Test-Path $NdkRoot)) { return $NdkRoot }

    # 2. 环境变量
    foreach ($var in @("ANDROID_NDK_HOME", "ANDROID_NDK", "ANDROID_NDK_ROOT")) {
        $val = [Environment]::GetEnvironmentVariable($var)
        if ($val -and (Test-Path $val)) { return $val }
    }

    # 3. Android SDK ndk 子目录（取最新版本）
    $sdkCandidates = @(
        $env:ANDROID_HOME,
        (Join-Path $env:LOCALAPPDATA "Android\Sdk"),
        "C:\Android\Sdk",
        "C:\android-sdk"
    ) | Where-Object { $_ -and (Test-Path $_) }

    foreach ($sdk in $sdkCandidates) {
        $ndkBase = Join-Path $sdk "ndk"
        if (-not (Test-Path $ndkBase)) { continue }
        $latest = Get-ChildItem $ndkBase -Directory -ErrorAction SilentlyContinue |
                  Where-Object { Test-Path (Join-Path $_.FullName "build\cmake\android.toolchain.cmake") } |
                  Sort-Object Name -Descending |
                  Select-Object -First 1
        if ($latest) { return $latest.FullName }
    }
    return $null
}

<#
.SYNOPSIS 在 PATH 或 Android SDK cmake 目录中查找可执行文件。
.PARAMETER Name 可执行文件名（不含 .exe）
#>
function Find-Tool {
    param([string]$Name)

    # 1. 系统 PATH
    $fromPath = Get-Command $Name -ErrorAction SilentlyContinue
    if ($fromPath) { return $fromPath.Source }

    # 2. Android SDK 自带 cmake/{version}/bin/
    $sdkCandidates = @(
        $env:ANDROID_HOME,
        (Join-Path $env:LOCALAPPDATA "Android\Sdk")
    ) | Where-Object { $_ -and (Test-Path $_) }

    foreach ($sdk in $sdkCandidates) {
        $cmakeBase = Join-Path $sdk "cmake"
        if (-not (Test-Path $cmakeBase)) { continue }
        $latest = Get-ChildItem $cmakeBase -Directory -ErrorAction SilentlyContinue |
                  Sort-Object Name -Descending | Select-Object -First 1
        if ($latest) {
            $exe = Join-Path $latest.FullName "bin\$Name.exe"
            if (Test-Path $exe) { return $exe }
        }
    }
    return $null
}

# ════════════════════════════════════════════════════════════
# 下载 / 解压
# ════════════════════════════════════════════════════════════

<#
.SYNOPSIS 下载文件（已存在则跳过）。
.PARAMETER Url   下载地址
.PARAMETER Dest  本地保存路径
.PARAMETER Label 显示名称
#>
function Get-Archive {
    param([string]$Url, [string]$Dest, [string]$Label)
    if (Test-Path $Dest) {
        Write-Host "  [跳过] $Label 已存在" -ForegroundColor DarkGray
        return
    }
    Write-Host "  下载 $Label ..." -ForegroundColor Cyan
    Invoke-WebRequest -Uri $Url -OutFile $Dest -UseBasicParsing
    Write-Host "  ✓ $Label 下载完成" -ForegroundColor Green
}

<#
.SYNOPSIS 解压 .tar.gz 到目标目录。
.PARAMETER Archive 压缩包路径
.PARAMETER OutDir  解压目标目录
#>
function Expand-TarGz {
    param([string]$Archive, [string]$OutDir)
    New-Item $OutDir -ItemType Directory -Force | Out-Null
    Write-Host "  解压 $(Split-Path $Archive -Leaf) ..." -ForegroundColor Cyan
    # Windows 10 1803+ 自带 tar，支持 .tar.gz
    $result = & tar -xzf $Archive -C $OutDir 2>&1
    if ($LASTEXITCODE -ne 0) {
        $result | Write-Host
        throw "解压失败：$Archive（tar exit=$LASTEXITCODE）"
    }
    Write-Host "  ✓ 解压完成" -ForegroundColor Green
}

# ════════════════════════════════════════════════════════════
# CMake 配置 + 构建
# ════════════════════════════════════════════════════════════

<#
.SYNOPSIS 对指定 ABI 执行 cmake configure + build + install。
.PARAMETER SourceDir  CMakeLists.txt 所在目录
.PARAMETER BinaryDir  构建产物临时目录
.PARAMETER InstallDir cmake install 目标目录
.PARAMETER Abi        Android ABI（arm64-v8a 等）
.PARAMETER ExtraArgs  追加到 cmake 的额外参数
#>
function Invoke-CMakeBuild {
    param(
        [string]   $SourceDir,
        [string]   $BinaryDir,
        [string]   $InstallDir,
        [string]   $Abi,
        [string[]] $ExtraArgs = @()
    )

    $toolchain = Join-Path $ndkRoot "build\cmake\android.toolchain.cmake"

    $configArgs = @(
        "-S", $SourceDir,
        "-B", $BinaryDir,
        "-G", "Ninja",
        "--no-warn-unused-cli",
        "-DCMAKE_MAKE_PROGRAM=$ninja",
        "-DCMAKE_TOOLCHAIN_FILE=$toolchain",
        "-DANDROID_ABI=$Abi",
        "-DANDROID_PLATFORM=android-$ApiLevel",
        # c++_static：libde265 已静态嵌入 libheif.so，只有一个 .so 使用 C++
        # 无需额外分发 libc++_shared.so
        "-DANDROID_STL=c++_static",
        "-DCMAKE_BUILD_TYPE=Release",
        "-DCMAKE_INSTALL_PREFIX=$InstallDir"
    ) + $ExtraArgs

    Write-Verbose "cmake $($configArgs -join ' ')"
    Write-Host "    configure ..." -ForegroundColor DarkCyan

    # 临时降低 ErrorActionPreference，避免 cmake 的 stderr 警告（Deprecation Warning 等）
    # 被 PowerShell 当作 NativeCommandError 中断脚本；改为手动检查 $LASTEXITCODE。
    $savedEAP = $ErrorActionPreference
    $ErrorActionPreference = "Continue"

    $out = & $cmake @configArgs 2>&1
    $configExit = $LASTEXITCODE

    $ErrorActionPreference = $savedEAP

    if ($configExit -ne 0) {
        $out | Write-Host
        throw "cmake configure 失败（$Abi，exit=$configExit）"
    }

    Write-Host "    build + install ..." -ForegroundColor DarkCyan

    $ErrorActionPreference = "Continue"
    $out = & $cmake --build $BinaryDir --target install --config Release -j 4 2>&1
    $buildExit = $LASTEXITCODE
    $ErrorActionPreference = $savedEAP

    if ($buildExit -ne 0) {
        $out | Write-Host
        throw "cmake build 失败（$Abi，exit=$buildExit）"
    }

    Write-Host "    ✓ 完成" -ForegroundColor Green
}

# ════════════════════════════════════════════════════════════
# 主流程
# ════════════════════════════════════════════════════════════

Write-Host ""
Write-Host "══════════════════════════════════════════════════════" -ForegroundColor Yellow
Write-Host "  build-native-android.ps1"                            -ForegroundColor Yellow
Write-Host "  libheif $LibheifVersion  +  libde265 $Libde265Version"
Write-Host "  ABI: $($Abis -join ', ')   API: $ApiLevel"
Write-Host "══════════════════════════════════════════════════════" -ForegroundColor Yellow
Write-Host ""

# ── 1. 检测工具 ──────────────────────────────────────────
Write-Host "─── 检测工具 ───────────────────────────────────────────"

$ndkRoot = Find-NdkRoot
if (-not $ndkRoot) {
    Write-Host @"

错误：找不到 Android NDK。
请通过以下任一方式提供：
  · 设置环境变量  ANDROID_NDK_HOME=C:\Android\Sdk\ndk\27.2.12479018
  · 或传入参数    .\build-native-android.ps1 -NdkRoot "C:\..."
  · 或通过 Android Studio > SDK Manager > SDK Tools > NDK (Side by side) 安装

"@ -ForegroundColor Red
    exit 1
}
Write-Host "NDK  : $ndkRoot"

$cmake = Find-Tool "cmake"
if (-not $cmake) {
    Write-Host @"

错误：找不到 cmake。
请通过 Android Studio > SDK Manager > SDK Tools > CMake 安装，
或从 https://cmake.org 下载并加入 PATH。

"@ -ForegroundColor Red
    exit 1
}
Write-Host "CMake: $cmake"

$ninja = Find-Tool "ninja"
if (-not $ninja) {
    Write-Host @"

错误：找不到 ninja。
Android SDK 的 CMake 组件自带 ninja，请确认 CMake 已通过 SDK Manager 正确安装。
也可从 https://ninja-build.org 下载后加入 PATH。

"@ -ForegroundColor Red
    exit 1
}
Write-Host "Ninja: $ninja"

# ── 2. 目录 ──────────────────────────────────────────────
$downloadsDir = Join-Path $BuildDir "downloads"
$srcDir       = Join-Path $BuildDir "src"
$outputBase   = Join-Path $PSScriptRoot "NativeLibs"

New-Item $downloadsDir -ItemType Directory -Force | Out-Null
New-Item $srcDir       -ItemType Directory -Force | Out-Null

# ── 3. 下载源码 ──────────────────────────────────────────
Write-Host ""
Write-Host "─── 下载源码 ───────────────────────────────────────────"

$de265Archive  = Join-Path $downloadsDir "libde265-$Libde265Version.tar.gz"
$heifArchive   = Join-Path $downloadsDir "libheif-$LibheifVersion.tar.gz"

Get-Archive `
    -Url  "https://github.com/strukturag/libde265/archive/refs/tags/v$Libde265Version.tar.gz" `
    -Dest $de265Archive `
    -Label "libde265 $Libde265Version"

Get-Archive `
    -Url  "https://github.com/strukturag/libheif/archive/refs/tags/v$LibheifVersion.tar.gz" `
    -Dest $heifArchive `
    -Label "libheif $LibheifVersion"

# ── 4. 解压（仅首次）────────────────────────────────────
Write-Host ""
Write-Host "─── 解压源码 ───────────────────────────────────────────"

# GitHub archive 解压后目录名为 "{repo}-{version}"
$de265Src  = Join-Path $srcDir "libde265-$Libde265Version"
$libheifSrc = Join-Path $srcDir "libheif-$LibheifVersion"

if (-not (Test-Path $de265Src)) {
    Expand-TarGz -Archive $de265Archive -OutDir $srcDir
    if (-not (Test-Path $de265Src)) {
        # 部分 GitHub tag archive 解压出的目录名可能不同，尝试查找
        $found = Get-ChildItem $srcDir -Directory | Where-Object { $_.Name -like "libde265*" } | Select-Object -First 1
        if ($found -and $found.FullName -ne $de265Src) {
            Rename-Item $found.FullName $de265Src
        }
    }
}

if (-not (Test-Path $libheifSrc)) {
    Expand-TarGz -Archive $heifArchive -OutDir $srcDir
    if (-not (Test-Path $libheifSrc)) {
        $found = Get-ChildItem $srcDir -Directory | Where-Object { $_.Name -like "libheif*" } | Select-Object -First 1
        if ($found -and $found.FullName -ne $libheifSrc) {
            Rename-Item $found.FullName $libheifSrc
        }
    }
}

if (-not (Test-Path $de265Src))  { throw "libde265 源码目录不存在：$de265Src" }
if (-not (Test-Path $libheifSrc)) { throw "libheif 源码目录不存在：$libheifSrc" }

# ── CMake 4.x 兼容性 patch ───────────────────────────────
# libde265 1.0.x 使用 cmake_minimum_required(VERSION 3.3.2)，CMake 4.x 要求 >= 3.5
# 同时 CMake 4.x 对 < 3.16 会发出 Deprecation Warning，统一升到 3.16 消除噪音
$de265Cmake = Join-Path $de265Src "CMakeLists.txt"
$cmakeContent = Get-Content $de265Cmake -Raw
if ($cmakeContent -match 'cmake_minimum_required\s*\(VERSION\s+3\.(0|1|2|3|4|5|6|7|8|9|10|11|12|13|14|15)\b') {
    Write-Host "  [patch] libde265 CMakeLists.txt: cmake_minimum_required → 3.16 (CMake 4.x 兼容)" -ForegroundColor DarkGray
    $cmakeContent = $cmakeContent -replace '(cmake_minimum_required\s*\(VERSION\s+)3\.(0|1|2|3|4|5|6|7|8|9|10|11|12|13|14|15)(\.\d+)?', '${1}3.16'
    Set-Content $de265Cmake $cmakeContent -Encoding UTF8
}

# ── 5. 逐 ABI 编译 ──────────────────────────────────────
$generatedFiles = [System.Collections.Generic.List[string]]::new()

foreach ($abi in $Abis) {
    Write-Host ""
    Write-Host "─── ABI: $abi ──────────────────────────────────────────" -ForegroundColor Magenta

    $installDir    = Join-Path $BuildDir "install\$abi"
    $de265BuildDir = Join-Path $BuildDir "build\de265-$abi"
    $heifBuildDir  = Join-Path $BuildDir "build\heif-$abi"
    $outDir        = Join-Path $outputBase $abi

    New-Item $installDir -ItemType Directory -Force | Out-Null
    New-Item $outDir     -ItemType Directory -Force | Out-Null

    # ── 5a. libde265（静态库）──────────────────────────
    Write-Host "  [1/2] libde265 (static .a → 嵌入 libheif.so)" -ForegroundColor Cyan
    Invoke-CMakeBuild `
        -SourceDir $de265Src `
        -BinaryDir $de265BuildDir `
        -InstallDir $installDir `
        -Abi $abi `
        -ExtraArgs @(
            "-DBUILD_SHARED_LIBS=OFF",   # 静态库，链接进 libheif.so
            "-DENABLE_DECODER=ON",
            "-DENABLE_ENCODER=OFF",
            "-DBUILD_TESTS=OFF"
        )

    # ── 5b. libheif（共享库，静态链接 libde265 + STL）──
    Write-Host "  [2/2] libheif (shared .so)" -ForegroundColor Cyan
    Invoke-CMakeBuild `
        -SourceDir $libheifSrc `
        -BinaryDir $heifBuildDir `
        -InstallDir $installDir `
        -Abi $abi `
        -ExtraArgs @(
            "-DBUILD_SHARED_LIBS=ON",
            # 让 cmake find_package(libde265) 找到上面安装的静态库
            # 注意：Android NDK toolchain 将 find_path/find_library 限制为仅搜索
            # CMAKE_FIND_ROOT_PATH 内的路径（ONLY 模式）。CMAKE_PREFIX_PATH 不受此约束
            # 但 FindLIBDE265.cmake 使用 find_path/find_library，所以必须同时设置两者。
            "-DCMAKE_PREFIX_PATH=$installDir",
            "-DCMAKE_FIND_ROOT_PATH=$installDir",
            # 只启用 HEVC (libde265)，禁用所有其他编解码器
            "-DWITH_LIBDE265=ON",
            "-DENABLE_PLUGIN_LOADING=OFF",  # Android 不支持运行时插件加载
            "-DWITH_EXAMPLES=OFF",
            "-DWITH_X265=OFF",
            "-DWITH_AOM_DECODER=OFF",
            "-DWITH_AOM_ENCODER=OFF",
            "-DWITH_DAV1D=OFF",
            "-DWITH_KVAZAAR=OFF",
            "-DWITH_JPEG_DECODER=OFF",
            "-DWITH_JPEG_ENCODER=OFF",
            "-DWITH_PNG_DECODER=OFF",
            "-DWITH_PNG_ENCODER=OFF",
            "-DWITH_GDK_PIXBUF=OFF",
            "-DBUILD_TESTING=OFF"   # 禁用测试程序，避免链接 __android_log_write 失败
        )

    # ── 5c. 复制 libheif.so 到 NativeLibs ──────────────
    $heifSo = Join-Path $installDir "lib\libheif.so"
    if (Test-Path $heifSo) {
        $dest = Join-Path $outDir "libheif.so"
        Copy-Item $heifSo $dest -Force
        $size = [math]::Round((Get-Item $dest).Length / 1MB, 2)
        Write-Host "  ✓ NativeLibs/$abi/libheif.so  ($size MB)" -ForegroundColor Green
        $generatedFiles.Add("NativeLibs/$abi/libheif.so")
    } else {
        Write-Host "  ✗ 找不到 libheif.so，请检查上方编译输出" -ForegroundColor Red
    }
}

# ── 6. 汇总 ─────────────────────────────────────────────
Write-Host ""
Write-Host "══════════════════════════════════════════════════════" -ForegroundColor Yellow
Write-Host "  完成！已生成以下文件：" -ForegroundColor Yellow
$generatedFiles | ForEach-Object { Write-Host "    · $_" -ForegroundColor Green }
Write-Host ""
Write-Host "  下一步：重新构建 Android 项目，.so 会自动打包进 APK。"
Write-Host "  临时构建目录（可删除）：$BuildDir"
Write-Host "══════════════════════════════════════════════════════" -ForegroundColor Yellow

