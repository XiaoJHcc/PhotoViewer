<#
.SYNOPSIS
    Windows 版 Android APK 发布脚本，等价于 publish-android-apk.sh。

.DESCRIPTION
    用法：powershell -ExecutionPolicy Bypass -File publish-android-apk.ps1
              [-Edition standard|ai|both] [-Local] [-AndroidSdkDirectory <path>]

    两版共用 ApplicationId（cc.xiaojh.PhotoViewer），只按显示名与 AI 能力区分：
      standard  PhotoViewer     无 DINO 模型 / ONNX Runtime / 许可证
      ai        PhotoViewer-AI  内置 DINOv3 模型、ONNX Runtime 与许可证
    只发 arm64-v8a，APK 不得含 x86_64 / armeabi-v7a。

    正式发布写 release\packages\<PhotoViewer|PhotoViewer-AI>-<AppVersion>-android-arm64.apk；
    -Local 只做本机验证，产物落 artifacts\scratch\android-local-publish，不写 release。
    -Edition 默认 both。版本号与构建号唯一来源是根 Directory.Build.props。
    构建与还原都显式传 -p:PhotoViewerEnableDino=true|false，两版 bin/obj 按 edition 隔离；
    只清当前 edition 的发布输出，不删共享工程 obj。
    签名凭据只进命令行参数，不打印。
#>
[CmdletBinding()]
param(
    [ValidateSet('standard', 'ai', 'both')]
    [string]$Edition = 'both',
    [switch]$Local,
    [string]$Configuration = 'Release',
    [string]$Framework = 'net10.0-android',
    [string]$AndroidSdkDirectory = ''
)

$ErrorActionPreference = 'Stop'

$repoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$androidProject = Join-Path $PSScriptRoot 'PhotoViewer.Android.csproj'
$globalJson = Join-Path $repoRoot 'global.json'
$directoryBuildProps = Join-Path $repoRoot 'Directory.Build.props'
$packageDirectory = Join-Path $repoRoot 'release\packages'
$localDirectory = Join-Path $repoRoot 'artifacts\scratch\android-local-publish'
$reportDirectory = Join-Path $repoRoot 'artifacts\acceptance\android-editions'
$scanner = Join-Path $repoRoot 'Tools\check-android-package.py'
$packageId = 'cc.xiaojh.PhotoViewer'

$editions = if ($Edition -eq 'both') { @('standard', 'ai') } else { @($Edition) }

if (-not (Test-Path $androidProject)) {
    throw "未找到工程文件：$androidProject"
}

if (-not (Test-Path $scanner)) {
    throw "缺少包扫描工具：$scanner"
}

$pythonPath = (Get-Command python3 -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Source -First 1)
if (-not $pythonPath) {
    $pythonPath = (Get-Command python -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Source -First 1)
}
if (-not $pythonPath) {
    throw 'Android 包扫描需要 python3（或 python）。'
}

if ([string]::IsNullOrWhiteSpace($AndroidSdkDirectory)) {
    if (-not [string]::IsNullOrWhiteSpace($env:ANDROID_SDK_ROOT)) {
        $AndroidSdkDirectory = $env:ANDROID_SDK_ROOT
    }
    else {
        $AndroidSdkDirectory = Join-Path $env:LOCALAPPDATA 'Android\Sdk'
    }
}

$AndroidSdkDirectory = [System.IO.Path]::GetFullPath($AndroidSdkDirectory)
if (-not (Test-Path $AndroidSdkDirectory)) {
    throw "未找到 Android SDK 目录：$AndroidSdkDirectory"
}

# ──────────────────────────────────────────────────────────────
# 读取签名配置（signing.json 或环境变量）；凭据只进参数，不打印
# ──────────────────────────────────────────────────────────────
$signingJsonPath = Join-Path $PSScriptRoot 'signing.json'
$signingConfig = $null

if (Test-Path $signingJsonPath) {
    $signingConfig = Get-Content $signingJsonPath -Raw | ConvertFrom-Json
}
elseif ($env:ANDROID_KEYSTORE_PATH -and $env:ANDROID_KEYSTORE_PASSWORD -and
        $env:ANDROID_KEY_ALIAS -and $env:ANDROID_KEY_PASSWORD) {
    $signingConfig = [PSCustomObject]@{
        keystoreFile     = $env:ANDROID_KEYSTORE_PATH
        keystorePassword = $env:ANDROID_KEYSTORE_PASSWORD
        keyAlias         = $env:ANDROID_KEY_ALIAS
        keyPassword      = $env:ANDROID_KEY_PASSWORD
    }
}
else {
    Write-Warning '未找到签名配置：signing.json 不存在，且未设置 ANDROID_KEYSTORE_PATH 等环境变量。'
    Write-Warning '发布的 APK 将使用自动生成的临时签名，无法用于正式更新。请运行 setup-android-signing.ps1 生成证书。'
}

$sdkConfig = Get-Content $globalJson -Raw | ConvertFrom-Json
$requiredSdkVersion = $sdkConfig.sdk.version
$sdkRollForward = if ($sdkConfig.sdk.rollForward) { $sdkConfig.sdk.rollForward } else { 'latestMinor' }
$appVersion = ([xml](Get-Content $directoryBuildProps -Raw)).Project.PropertyGroup.AppVersion

if ([string]::IsNullOrWhiteSpace($appVersion)) {
    throw '未能从 Directory.Build.props 读取 AppVersion。'
}

$dotnetCandidates = @(
    (Join-Path $env:ProgramFiles 'dotnet\dotnet.exe'),
    (Join-Path $env:USERPROFILE '.dotnet\dotnet.exe'),
    (Get-Command dotnet -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Source -First 1)
) | Where-Object { $_ -and (Test-Path $_) } | Select-Object -Unique

function Test-SdkSatisfiesVersion {
    param([string]$InstalledVersion, [string]$RequiredVersion, [string]$RollForward)
    $installed = [version]$InstalledVersion
    $required = [version]$RequiredVersion
    switch ($RollForward) {
        { $_ -in @('disable', 'exact') } { return $InstalledVersion -eq $RequiredVersion }
        { $_ -in @('patch', 'latestPatch') } { return $installed.Major -eq $required.Major -and $installed.Minor -eq $required.Minor -and $installed -ge $required }
        { $_ -in @('minor', 'latestMinor') } { return $installed.Major -eq $required.Major -and $installed -ge $required }
        default { return $installed -ge $required }
    }
}

function Test-DotnetHasWorkload {
    param(
        [string]$DotnetPath,
        [string]$WorkloadId
    )

    Push-Location $env:TEMP
    try {
        $workloadList = & $DotnetPath workload list 2>$null | Out-String
        return $LASTEXITCODE -eq 0 -and $workloadList -match "(?m)^\s*$([regex]::Escape($WorkloadId))\s"
    }
    finally {
        Pop-Location
    }
}

$dotnetInfos = foreach ($candidate in $dotnetCandidates) {
    $installedSdks = & $candidate --list-sdks 2>$null
    [pscustomobject]@{
        Path = $candidate
        HasRequiredSdk = [bool]($installedSdks | Where-Object {
            $_ -match '^(\S+)\s' -and (Test-SdkSatisfiesVersion -InstalledVersion $Matches[1] -RequiredVersion $requiredSdkVersion -RollForward $sdkRollForward)
        })
        HasAndroidWorkload = Test-DotnetHasWorkload -DotnetPath $candidate -WorkloadId 'android'
    }
}

$selectedDotnet = $dotnetInfos | Where-Object { $_.HasRequiredSdk -and $_.HasAndroidWorkload } | Select-Object -First 1
$temporarilyBypassGlobalJson = $false

if (-not $selectedDotnet) {
    $selectedDotnet = $dotnetInfos | Where-Object { $_.HasAndroidWorkload } | Select-Object -First 1
    if ($selectedDotnet -and -not $selectedDotnet.HasRequiredSdk) {
        throw "已找到安装了 android workload 的 dotnet：$($selectedDotnet.Path)，但它不满足 global.json 要求的 SDK $requiredSdkVersion。请先对兼容的 .NET SDK 安装 android workload，例如：`"C:\Program Files\dotnet\dotnet.exe`" workload install android"
    }
}

if (-not $selectedDotnet) {
    throw '未找到可用的 Android dotnet 环境：既没有具备 android workload 的 dotnet，也无法继续发布 APK。'
}

$dotnetPath = $selectedDotnet.Path

Write-Host "[发布] 使用 dotnet: $dotnetPath"
Write-Host "[发布] Android SDK: $AndroidSdkDirectory"
Write-Host "[发布] 发行版本: $($editions -join ', ')"

if ($temporarilyBypassGlobalJson) {
    Write-Warning "当前未找到同时满足 SDK $requiredSdkVersion 与 android workload 的 dotnet，将临时绕开 global.json 并使用已安装 android workload 的 dotnet。"
}

$globalJsonBackup = "$globalJson.publishbak"

try {
    if ($temporarilyBypassGlobalJson -and (Test-Path $globalJson)) {
        Move-Item $globalJson $globalJsonBackup -Force
    }

    $signingArgs = @()
    if ($signingConfig) {
        $keystoreFullPath = if ([System.IO.Path]::IsPathRooted($signingConfig.keystoreFile)) {
            $signingConfig.keystoreFile
        } else {
            [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot $signingConfig.keystoreFile))
        }
        if (-not (Test-Path $keystoreFullPath)) {
            throw "Keystore 文件不存在：$keystoreFullPath`n请运行 setup-android-signing.ps1 重新生成证书。"
        }
        $signingArgs = @(
            '/p:AndroidKeyStore=true',
            "/p:AndroidSigningKeyStore=$keystoreFullPath",
            "/p:AndroidSigningKeyAlias=$($signingConfig.keyAlias)",
            "/p:AndroidSigningKeyPass=$($signingConfig.keyPassword)",
            "/p:AndroidSigningStorePass=$($signingConfig.keystorePassword)"
        )
    }

    $destinationDirectory = if ($Local) { $localDirectory } else { $packageDirectory }
    if ($Local) {
        Write-Host '[发布] 本地验证模式：产物写入 artifacts\scratch\android-local-publish，不写 release'
    }

    foreach ($edition in $editions) {
        $editionFlag = if ($edition -eq 'ai') { 'true' } else { 'false' }
        $productName = if ($edition -eq 'ai') { 'PhotoViewer-AI' } else { 'PhotoViewer' }
        $publishDirectory = Join-Path $PSScriptRoot "bin\$edition\$Configuration\net10.0-android\publish"

        Write-Host "[发布] === ${edition}（${productName}） ==="

        # 只清当前 edition 的发布输出；共享工程 obj 由 edition 隔离目录承担，不再整目录删除。
        if (Test-Path $publishDirectory) {
            Remove-Item $publishDirectory -Recurse -Force -ErrorAction SilentlyContinue
        }

        & $dotnetPath publish $androidProject `
            -c $Configuration `
            -f $Framework `
            "-p:PhotoViewerEnableDino=$editionFlag" `
            /p:AndroidPackageFormat=apk `
            "/p:AndroidSdkDirectory=$AndroidSdkDirectory" `
            @signingArgs

        if ($LASTEXITCODE -ne 0) {
            throw "dotnet publish 失败（${edition}），退出码：$LASTEXITCODE"
        }

        $candidates = @(
            (Join-Path $publishDirectory "$packageId-Signed.apk"),
            (Join-Path $publishDirectory "$packageId.apk")
        )
        $apkPath = $candidates | Where-Object { Test-Path $_ } | Select-Object -First 1
        if (-not $apkPath) {
            $apkPath = Get-ChildItem $publishDirectory -Filter '*.apk' -File -ErrorAction SilentlyContinue |
                Select-Object -First 1 -ExpandProperty FullName
        }
        if (-not $apkPath) {
            throw "${edition} 发布完成后未找到 APK：$publishDirectory"
        }

        if (-not (Test-Path $destinationDirectory)) {
            New-Item -ItemType Directory -Path $destinationDirectory | Out-Null
        }
        if (-not (Test-Path $reportDirectory)) {
            New-Item -ItemType Directory -Path $reportDirectory | Out-Null
        }

        $destApk = Join-Path $destinationDirectory "$productName-$appVersion-android-arm64.apk"
        Copy-Item $apkPath $destApk -Force
        Write-Host "[发布] 产物：$destApk"

        # 包扫描读实际 APK：身份/显示名/版本/ABI/模型/许可证/推理组件，失败即中断发布。
        & $pythonPath $scanner $destApk --edition $edition --output (Join-Path $reportDirectory "$edition-package.json")
        if ($LASTEXITCODE -ne 0) {
            throw "${edition} 包扫描未通过，发布中止。"
        }
    }

    if (-not $Local) {
        & $pythonPath (Join-Path $repoRoot 'Tools\check-release.py') --write
        if ($LASTEXITCODE -ne 0) {
            throw 'check-release.py 校验 release/ 失败。'
        }
    }

    Write-Host "[发布] 完成：$($editions -join ', ')"
}
finally {
    if (Test-Path $globalJsonBackup) {
        Move-Item $globalJsonBackup $globalJson -Force
    }
}
