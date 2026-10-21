<#
.SYNOPSIS
    构建 Windows x64 原生 HEVC 解码 DLL（pvhevc.dll）：静态 FFmpeg（仅 HEVC 解码器）+ 薄 C 封装。

.DESCRIPTION
    构建策略：
      · FFmpeg 只保留 libavcodec + libavutil，仅启用 hevc 解码器与 hevc parser
      · libavcodec.a / libavutil.a 静态链接进 pvhevc.dll，运行时只依赖 Windows 系统 DLL
      · 使用 MSYS2 CLANG64 工具链（clang + nasm）；x86 SIMD 汇编提供 HEVC 8/10/12-bit 加速
      · LGPL 构建：不传 --enable-gpl / --enable-nonfree，随附 FFmpeg 许可证文本

    对外契约见 NativeSrc/pvhevc/pvhevc.h；调用方（.NET）P/Invoke 该头文件声明的 5 个导出。

    依赖环境：
      · MSYS2 安装于 C:\msys64，clang64 环境已安装：
          pacman -S --needed --noconfirm mingw-w64-clang-x86_64-toolchain `
                                    mingw-w64-clang-x86_64-nasm `
                                    mingw-w64-clang-x86_64-pkgconf
      · 网络连接（从 https://ffmpeg.org/releases/ 下载源码 tarball）

.PARAMETER FfmpegVersion
    FFmpeg 源码版本，默认 8.1.3（LGPL 2.1 or later）。

.PARAMETER Msys2Root
    MSYS2 根目录，留空则依次探测 MSYS2_ROOT / MINGW_PREFIX 根 / C:\msys64。

.PARAMETER Msys2Env
    MSYS2 环境名，默认 CLANG64（对应 mingw-w64-clang-x86_64 工具链）。

.PARAMETER Jobs
    并行编译任务数，默认取逻辑处理器数。

.PARAMETER BuildDir
    临时构建目录，默认脚本同级的 .build-windows-native/（可安全删除）。

.EXAMPLE
    .\build-native-windows-hevc.ps1

.EXAMPLE
    .\build-native-windows-hevc.ps1 -FfmpegVersion "8.1.3" -Jobs 20
#>
[CmdletBinding()]
param(
    [string] $FfmpegVersion = "8.1.3",
    [string] $Msys2Root     = "",
    [string] $Msys2Env      = "CLANG64",
    [int]    $Jobs          = 0,
    [string] $BuildDir      = ""
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

# Windows PowerShell 5.1 在参数绑定阶段 $PSScriptRoot 仍为空，默认目录延后解析
if (-not $BuildDir) { $BuildDir = Join-Path $PSScriptRoot ".build-windows-native" }

$shimDir   = Join-Path $PSScriptRoot "NativeSrc\pvhevc"
$outputDir = Join-Path $PSScriptRoot "NativeLibs\win-x64"

# 运行时只允许依赖 Windows 系统 DLL / UCRT；出现下列任何一项即视为链接失败
$forbiddenImports = @(
    "libc++.dll",
    "libc++abi.dll",
    "libunwind.dll",
    "libwinpthread-1.dll",
    "libgcc_s_seh-1.dll",
    "libgcc_s_dw2-1.dll",
    "libssp-0.dll"
)

# ════════════════════════════════════════════════════════════
# 工具探测函数
# ════════════════════════════════════════════════════════════

<#
.SYNOPSIS 自动查找 MSYS2 根目录。
#>
function Find-Msys2Root {
    if ($Msys2Root -and (Test-Path (Join-Path $Msys2Root "usr\bin\bash.exe"))) { return $Msys2Root }

    $candidates = @(
        $env:MSYS2_ROOT,
        $env:MSYS2_ROOT_DIR,
        "C:\msys64",
        "D:\msys64"
    )
    foreach ($c in $candidates) {
        if ($c -and (Test-Path (Join-Path $c "usr\bin\bash.exe"))) { return $c }
    }
    return $null
}

<#
.SYNOPSIS 把 Windows 路径转换为 MSYS2 路径（D:\a\b -> /d/a/b）。
.PARAMETER Path 待转换路径
#>
function ConvertTo-MsysPath {
    param([string]$Path)
    $full = [System.IO.Path]::GetFullPath($Path) -replace '\\', '/'
    if ($full -match '^([A-Za-z]):(.*)$') {
        return "/" + $Matches[1].ToLowerInvariant() + $Matches[2]
    }
    return $full
}

<#
.SYNOPSIS 执行 MSYS2 bash 命令并返回输出（适用于短命令）。
.PARAMETER Command 传给 bash -lc 的命令串
#>
function Invoke-MsysBash {
    param([string]$Command)

    $bash    = Join-Path $script:Msys2Root "usr\bin\bash.exe"
    $prevEap = $ErrorActionPreference
    $prevEnv = $env:MSYSTEM

    $ErrorActionPreference = "Continue"
    $env:MSYSTEM = $script:Msys2Env

    $out  = & $bash -lc $Command 2>&1
    $code = $LASTEXITCODE

    $env:MSYSTEM = $prevEnv
    $ErrorActionPreference = $prevEap

    return [pscustomobject]@{ ExitCode = $code; Output = @($out) }
}

<#
.SYNOPSIS 执行 MSYS2 bash 命令，输出写入日志文件（适用于长任务）；失败时打印日志尾部。
.PARAMETER Command 传给 bash -lc 的命令串
.PARAMETER LogPath 日志文件（Windows 路径）
.PARAMETER Label   失败时显示的任务名称
#>
function Invoke-MsysBashLogged {
    param([string]$Command, [string]$LogPath, [string]$Label)

    $bash    = Join-Path $script:Msys2Root "usr\bin\bash.exe"
    $msysLog = ConvertTo-MsysPath $LogPath
    $prevEap = $ErrorActionPreference
    $prevEnv = $env:MSYSTEM

    $ErrorActionPreference = "Continue"
    $env:MSYSTEM = $script:Msys2Env

    & $bash -lc "$Command > '$msysLog' 2>&1"
    $code = $LASTEXITCODE

    $env:MSYSTEM = $prevEnv
    $ErrorActionPreference = $prevEap

    if ($code -ne 0) {
        Write-Host "  ✗ $Label 失败（exit=$code），日志尾部：" -ForegroundColor Red
        if (Test-Path $LogPath) {
            Get-Content $LogPath -Tail 40 | ForEach-Object { Write-Host "    $_" -ForegroundColor DarkGray }
            Write-Host "  完整日志：$LogPath" -ForegroundColor DarkGray
        }
        throw "$Label 失败"
    }
    return $code
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
.SYNOPSIS 解压 .tar.xz 到目标目录。
.DESCRIPTION 走 MSYS2 的 tar：PATH 里若有 MSYS/GNU tar，它会把 "D:" 当成 rsh 远端主机名，
             直接用 Windows 路径会失败；统一转换到 MSYS 路径后由 bash 执行。
.PARAMETER Archive 压缩包路径
.PARAMETER OutDir  解压目标目录
#>
function Expand-TarXz {
    param([string]$Archive, [string]$OutDir)
    New-Item $OutDir -ItemType Directory -Force | Out-Null
    Write-Host "  解压 $(Split-Path $Archive -Leaf) ..." -ForegroundColor Cyan
    $archiveMsys = ConvertTo-MsysPath $Archive
    $outMsys     = ConvertTo-MsysPath $OutDir
    $r = Invoke-MsysBash "tar -xf '$archiveMsys' -C '$outMsys'"
    if ($r.ExitCode -ne 0) {
        $r.Output | Write-Host
        throw "解压失败：$Archive（tar exit=$($r.ExitCode)）"
    }
    Write-Host "  ✓ 解压完成" -ForegroundColor Green
}

# ════════════════════════════════════════════════════════════
# 主流程
# ════════════════════════════════════════════════════════════

if ($Jobs -le 0) { $Jobs = [Environment]::ProcessorCount }

Write-Host ""
Write-Host "══════════════════════════════════════════════════════" -ForegroundColor Yellow
Write-Host "  build-native-windows-hevc.ps1"                         -ForegroundColor Yellow
Write-Host "  FFmpeg $FfmpegVersion  静态 libavcodec + libavutil（仅 HEVC 解码，LGPL）"
Write-Host "  目标: NativeLibs\win-x64\pvhevc.dll"
Write-Host "══════════════════════════════════════════════════════" -ForegroundColor Yellow
Write-Host ""

# ── 1. 检测工具 ──────────────────────────────────────────
Write-Host "─── 检测工具 ───────────────────────────────────────────"

$script:Msys2Root  = Find-Msys2Root
$script:Msys2Env   = $Msys2Env
if (-not $script:Msys2Root) {
    Write-Host @"

错误：找不到 MSYS2。
请从 https://www.msys2.org 安装，或传入参数 -Msys2Root "C:\msys64"。

"@ -ForegroundColor Red
    exit 1
}
Write-Host "MSYS2 : $script:Msys2Root  ($Msys2Env)"

$toolCheck = Invoke-MsysBash "for t in clang nasm make llvm-objdump llvm-nm; do printf '%s=' `$t; command -v `$t || echo MISSING; done"
$toolCheck.Output | ForEach-Object { Write-Host "  $_" }
if ($toolCheck.ExitCode -ne 0 -or ($toolCheck.Output -join "`n") -match "MISSING") {
    Write-Host @"

错误：$Msys2Env 环境缺少编译工具。
请先执行（在普通终端里）：
  C:\msys64\usr\bin\bash -lc "pacman -S --needed --noconfirm mingw-w64-clang-x86_64-toolchain mingw-w64-clang-x86_64-nasm mingw-w64-clang-x86_64-pkgconf"

"@ -ForegroundColor Red
    exit 1
}

# ── 2. 目录 ──────────────────────────────────────────────
$downloadsDir = Join-Path $BuildDir "downloads"
$srcDir       = Join-Path $BuildDir "src"
$logDir       = Join-Path $BuildDir "logs"
$shimOutDir   = Join-Path $BuildDir "out"

New-Item $downloadsDir -ItemType Directory -Force | Out-Null
New-Item $srcDir       -ItemType Directory -Force | Out-Null
New-Item $logDir       -ItemType Directory -Force | Out-Null
New-Item $shimOutDir   -ItemType Directory -Force | Out-Null
New-Item $outputDir    -ItemType Directory -Force | Out-Null

# ── 3. 下载源码 ──────────────────────────────────────────
Write-Host ""
Write-Host "─── 下载源码 ───────────────────────────────────────────"

$ffArchive = Join-Path $downloadsDir "ffmpeg-$FfmpegVersion.tar.xz"
Get-Archive `
    -Url  "https://ffmpeg.org/releases/ffmpeg-$FfmpegVersion.tar.xz" `
    -Dest $ffArchive `
    -Label "FFmpeg $FfmpegVersion"

# ── 4. 解压（仅首次）────────────────────────────────────
Write-Host ""
Write-Host "─── 解压源码 ───────────────────────────────────────────"

$ffSrc = Join-Path $srcDir "ffmpeg-$FfmpegVersion"
if (-not (Test-Path (Join-Path $ffSrc "configure"))) {
    Expand-TarXz -Archive $ffArchive -OutDir $srcDir
}
if (-not (Test-Path (Join-Path $ffSrc "configure"))) { throw "FFmpeg 源码目录不完整：$ffSrc" }

$ffSrcMsys = ConvertTo-MsysPath $ffSrc
$logMsys   = ConvertTo-MsysPath $logDir

# ── 5. configure ────────────────────────────────────────
Write-Host ""
Write-Host "─── configure ──────────────────────────────────────────" -ForegroundColor Magenta

# 只保留解码必需组件；关闭全部程序/网络/滤镜/swscale 以缩小体积
# LGPL：不传 --enable-gpl / --enable-nonfree
$configureArgs = @(
    "--target-os=mingw32"
    "--arch=x86_64"
    "--cc=clang"
    "--cxx=clang++"
    "--ld=clang"
    "--disable-everything"
    "--disable-programs"
    "--disable-doc"
    "--disable-network"
    "--disable-autodetect"
    "--disable-debug"
    "--enable-static"
    "--disable-shared"
    "--enable-avcodec"
    "--enable-decoder=hevc"
    "--enable-parser=hevc"
    "--disable-avformat"
    "--disable-swscale"
    "--disable-swresample"
    "--disable-avfilter"
    "--disable-avdevice"
    "--enable-pic"
    "--enable-x86asm"
)

$configureArgsLine = "./configure " + ($configureArgs -join ' ')
$configureCmd      = "cd '$ffSrcMsys' && $configureArgsLine"
$configureLine     = $configureArgsLine

$configMak = Join-Path $ffSrc "ffbuild\config.mak"
if (-not (Test-Path $configMak)) {
    Write-Host "  运行 configure ..." -ForegroundColor DarkCyan
    Invoke-MsysBashLogged -Command $configureCmd -LogPath (Join-Path $logDir "configure.log") -Label "configure" | Out-Null
} else {
    Write-Host "  [跳过] 已有 ffbuild/config.mak（删除 $BuildDir\src 可强制重配）" -ForegroundColor DarkGray
}
Write-Host "  ✓ configure 完成" -ForegroundColor Green

# ── 6. 编译 ─────────────────────────────────────────────
Write-Host ""
Write-Host "─── 编译 libavcodec / libavutil ────────────────────────" -ForegroundColor Magenta

Invoke-MsysBashLogged -Command "cd '$ffSrcMsys' && make -j$Jobs" `
    -LogPath (Join-Path $logDir "make.log") `
    -Label "make" | Out-Null
Write-Host "  ✓ 编译完成" -ForegroundColor Green

# ── 7. 校验构建配置（HEVC 解码器 + x86 SIMD 汇编）────────
Write-Host ""
Write-Host "─── 校验构建配置 ──────────────────────────────────────"

# 组件开关在 config_components.h，工具链/许可证开关在 config.h
$configText = (Get-Content (Join-Path $ffSrc "config.h") -Raw) + "`n" +
              (Get-Content (Join-Path $ffSrc "config_components.h") -Raw)
foreach ($needle in @("#define CONFIG_HEVC_DECODER 1", "#define CONFIG_HEVC_PARSER 1", "#define HAVE_X86ASM 1", "#define CONFIG_GPL 0", "#define CONFIG_NONFREE 0", "#define CONFIG_AVCODEC 1", "#define CONFIG_SWSCALE 0")) {
    if ($configText -notmatch [regex]::Escape($needle)) {
        throw "构建配置缺少预期定义：$needle"
    }
}
Write-Host "  ✓ CONFIG_HEVC_DECODER / CONFIG_HEVC_PARSER / HAVE_X86ASM 均启用，GPL/NONFREE 关闭" -ForegroundColor Green

# HEVC 10-bit SIMD 符号存在即证明 nasm 汇编进了 libavcodec.a
$asmCheck = Invoke-MsysBash "cd '$ffSrcMsys' && llvm-nm --defined-only libavcodec/libavcodec.a 2>/dev/null | grep -cE ' [Tt] (ff_)?hevc_put.*_10_(sse|avx)'"
$asmCount = ($asmCheck.Output -join "").Trim()
if ($asmCheck.ExitCode -ne 0 -or [int]$asmCount -le 0) {
    throw "libavcodec.a 中未找到 HEVC 10-bit x86 SIMD 符号，nasm 汇编未生效"
}
Write-Host "  ✓ HEVC 10-bit x86 SIMD 符号 $asmCount 个（nasm 已生效）" -ForegroundColor Green

$nasmVersion = (Invoke-MsysBash "nasm -v").Output
Write-Host "  nasm: $($nasmVersion -join ' ')" -ForegroundColor DarkGray

# ── 8. 编译并链接 pvhevc.dll ────────────────────────────
Write-Host ""
Write-Host "─── 链接 pvhevc.dll ───────────────────────────────────" -ForegroundColor Magenta

$shimMsys = ConvertTo-MsysPath $shimDir
$outMsys  = ConvertTo-MsysPath $shimOutDir

# 取 configure 计算出的系统库（EXTRALIBS-*），避免硬编码
$extraLibs = @()
$configMakLines = Get-Content $configMak
foreach ($line in $configMakLines) {
    if ($line -match '^EXTRALIBS[^=]*=(.*)$') {
        foreach ($tok in ($Matches[1] -split '\s+')) {
            if ($tok -and ($extraLibs -notcontains $tok)) { $extraLibs += $tok }
        }
    }
}
Write-Host "  系统库: $($extraLibs -join ' ')" -ForegroundColor DarkGray

$linkCmd = "cd '$ffSrcMsys' && " +
    "clang -O2 -Wall -Wextra -c '$shimMsys/pvhevc.c' -o '$outMsys/pvhevc.o' -I '$shimMsys' -I . && " +
    "clang -shared -o '$outMsys/pvhevc.dll' '$outMsys/pvhevc.o' " +
    "libavcodec/libavcodec.a libavutil/libavutil.a " +
    "-static " + ($extraLibs -join ' ') +
    " -Wl,--nxcompat -Wl,--dynamicbase -Wl,--high-entropy-va"

Invoke-MsysBashLogged -Command $linkCmd -LogPath (Join-Path $logDir "link.log") -Label "链接 pvhevc.dll" | Out-Null

$dllPath = Join-Path $shimOutDir "pvhevc.dll"
if (-not (Test-Path $dllPath)) { throw "未生成 pvhevc.dll：$dllPath" }
Write-Host "  ✓ pvhevc.dll 生成" -ForegroundColor Green

# ── 9. 校验 DLL 导入表（不得依赖非系统运行库）───────────
Write-Host ""
Write-Host "─── 校验 DLL 导入表 ───────────────────────────────────"

$shimMsysForObjdump = ConvertTo-MsysPath $dllPath
$objdump = Invoke-MsysBash "llvm-objdump -p '$shimMsysForObjdump' | grep -i 'DLL Name:'"
$selfName = Split-Path $dllPath -Leaf
$imports = @()
foreach ($line in $objdump.Output) {
    # llvm-objdump 也会打印导出目录里的 "DLL Name:"，那一条是镜像自身
    if ("$line" -match 'DLL Name:\s*(\S+)' -and $Matches[1] -ine $selfName) { $imports += $Matches[1] }
}
$imports = $imports | Sort-Object -Unique
foreach ($imp in $imports) { Write-Host "  $imp" -ForegroundColor DarkGray }

$bad = @()
foreach ($imp in $imports) {
    foreach ($f in $forbiddenImports) {
        if ($imp -ieq $f) { $bad += $imp }
    }
}
if ($bad.Count -gt 0) {
    throw "pvhevc.dll 依赖了禁止的运行库 DLL：$($bad -join ', ')"
}
Write-Host "  ✓ 仅依赖 Windows 系统 DLL / UCRT" -ForegroundColor Green

# ── 10. 产出到 NativeLibs ───────────────────────────────
Write-Host ""
Write-Host "─── 产出 ──────────────────────────────────────────────"

$outDll = Join-Path $outputDir "pvhevc.dll"
Copy-Item $dllPath $outDll -Force
$sizeKb = [math]::Round((Get-Item $outDll).Length / 1KB, 1)
Write-Host "  ✓ NativeLibs\win-x64\pvhevc.dll  ($sizeKb KB)" -ForegroundColor Green

# 许可证：configure 输出为 "LGPL version 2.1 or later"，随附对应文本
$licenseName = "COPYING.LGPLv2.1"
$licenseSource = Join-Path $ffSrc $licenseName
if (-not (Test-Path $licenseSource)) {
    $licenseName = "COPYING.LGPLv3"
    $licenseSource = Join-Path $ffSrc $licenseName
}
if (-not (Test-Path $licenseSource)) { throw "找不到 FFmpeg 许可证文本（COPYING.LGPLv2.1 / COPYING.LGPLv3）" }
Copy-Item $licenseSource (Join-Path $outputDir "pvhevc-FFmpeg-LICENSE.txt") -Force
Write-Host "  ✓ NativeLibs\win-x64\pvhevc-FFmpeg-LICENSE.txt  ($licenseName)" -ForegroundColor Green

$versionHeader = Get-Content (Join-Path $ffSrc "RELEASE") -ErrorAction SilentlyContinue
$buildInfo = @(
    "pvhevc.dll — PhotoViewer HEVC still-picture decoder"
    ""
    "FFmpeg version : $FfmpegVersion"
    "License        : LGPL 2.1 or later ($licenseName); no --enable-gpl / --enable-nonfree"
    "Toolchain      : MSYS2 $Msys2Env (clang + nasm), static libavcodec.a + libavutil.a"
    "Shim source    : PhotoViewer.Windows/NativeSrc/pvhevc/pvhevc.c (+ pvhevc.h)"
    "Build script   : PhotoViewer.Windows/build-native-windows-hevc.ps1"
    "Built          : $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')"
    ""
    "configure line :"
    $configureLine
) -join "`r`n"
Set-Content -Path (Join-Path $outputDir "pvhevc.txt") -Value $buildInfo -Encoding UTF8
Write-Host "  ✓ NativeLibs\win-x64\pvhevc.txt" -ForegroundColor Green

# ── 11. 汇总 ────────────────────────────────────────────
Write-Host ""
Write-Host "══════════════════════════════════════════════════════" -ForegroundColor Yellow
Write-Host "  完成！" -ForegroundColor Yellow
Write-Host "    · NativeLibs\win-x64\pvhevc.dll                  ($sizeKb KB)" -ForegroundColor Green
Write-Host "    · NativeLibs\win-x64\pvhevc.txt"                                -ForegroundColor Green
Write-Host "    · NativeLibs\win-x64\pvhevc-FFmpeg-LICENSE.txt"                  -ForegroundColor Green
Write-Host ""
Write-Host "  DLL 导入: $($imports -join ', ')"
Write-Host "  临时构建目录（可删除）：$BuildDir"
Write-Host "══════════════════════════════════════════════════════" -ForegroundColor Yellow
