[CmdletBinding()]
param(
    [string]$Configuration = 'Release',
    [string]$Framework = 'net9.0-android',
    [string]$AndroidSdkDirectory = '',
    [string]$OutputDirectory = ''
)

$ErrorActionPreference = 'Stop'

$repoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$androidProject = Join-Path $PSScriptRoot 'PhotoViewer.Android.csproj'
$sharedProject = Join-Path $repoRoot 'PhotoViewer\PhotoViewer.csproj'
$globalJson = Join-Path $repoRoot 'global.json'
$directoryBuildProps = Join-Path $repoRoot 'Directory.Build.props'
$releaseDirectory = Join-Path $repoRoot 'release'

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

if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path $PSScriptRoot 'bin\Release\net9.0-android\publish'
}

$OutputDirectory = [System.IO.Path]::GetFullPath($OutputDirectory)
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
    if ($selectedDotnet) {
        $temporarilyBypassGlobalJson = -not $selectedDotnet.HasRequiredSdk
    }
}

if (-not $selectedDotnet) {
    throw '未找到可用的 Android dotnet 环境：既没有具备 android workload 的 dotnet，也无法继续发布 APK。'
}

$dotnetPath = $selectedDotnet.Path

Write-Host "[发布] 使用 dotnet: $dotnetPath"
Write-Host "[发布] Android SDK: $AndroidSdkDirectory"
Write-Host "[发布] 输出目录: $OutputDirectory"

if ($temporarilyBypassGlobalJson) {
    Write-Warning "当前未找到同时满足 SDK $requiredSdkVersion 与 android workload 的 dotnet，将临时绕开 global.json 并使用已安装 android workload 的 dotnet。"
}

$sharedProjectBackup = "$sharedProject.publishbak"
$globalJsonBackup = "$globalJson.publishbak"
$originalSharedProjectContent = Get-Content $sharedProject -Raw
$patchedSharedProjectContent = $originalSharedProjectContent -replace '<TargetFrameworks>net9.0;net9.0-ios</TargetFrameworks>', '<TargetFramework>net9.0</TargetFramework>'
$needsProjectPatch = $patchedSharedProjectContent -ne $originalSharedProjectContent

try {
    if ($temporarilyBypassGlobalJson -and (Test-Path $globalJson)) {
        Move-Item $globalJson $globalJsonBackup -Force
    }

    foreach ($path in @(
        (Join-Path $repoRoot 'PhotoViewer\obj'),
        (Join-Path $repoRoot 'PhotoViewer.Android\obj')
    )) {
        if (Test-Path $path) {
            Remove-Item $path -Recurse -Force -ErrorAction SilentlyContinue
        }
    }

    if (Test-Path $OutputDirectory) {
        Remove-Item $OutputDirectory -Recurse -Force -ErrorAction SilentlyContinue
    }

    if ($needsProjectPatch) {
        Copy-Item $sharedProject $sharedProjectBackup -Force
        Set-Content $sharedProject $patchedSharedProjectContent -Encoding UTF8
    }

    & $dotnetPath publish $androidProject `
        -c $Configuration `
        -f $Framework `
        /p:AndroidPackageFormat=apk `
        /p:AndroidSdkDirectory="$AndroidSdkDirectory"

    if ($LASTEXITCODE -ne 0) {
        throw "dotnet publish 失败，退出码：$LASTEXITCODE"
    }

    if (-not (Test-Path $OutputDirectory)) {
        throw "发布完成后未找到输出目录：$OutputDirectory"
    }

    if (-not (Test-Path $releaseDirectory)) {
        New-Item -ItemType Directory -Path $releaseDirectory | Out-Null
    }

    $signedApkPath = Join-Path $OutputDirectory 'cc.xiaojh.PhotoViewer-Signed.apk'
    $apkPath = Join-Path $OutputDirectory 'cc.xiaojh.PhotoViewer.apk'

    if (Test-Path $apkPath) {
        $releaseApkPath = Join-Path $releaseDirectory "PhotoViewer-$appVersion-android.apk"
        Copy-Item $apkPath $releaseApkPath -Force
        Write-Host "[发布] 已复制到仓库根目录：$releaseApkPath"
    }

    if (Test-Path $signedApkPath) {
        $releaseSignedApkPath = Join-Path $releaseDirectory "PhotoViewer-$appVersion-android-signed.apk"
        Copy-Item $signedApkPath $releaseSignedApkPath -Force
        Write-Host "[发布] 已复制到仓库根目录：$releaseSignedApkPath"
    }

    $outputFiles = Get-ChildItem $OutputDirectory -Filter '*.apk' -File | Sort-Object Name
    if ($outputFiles.Count -eq 0) {
        throw '发布完成后未找到任何 APK 文件。'
    }

    Write-Host '[发布] 输出文件：'
    $outputFiles | Select-Object Name, Length | Format-Table -AutoSize
}
finally {
    if ($needsProjectPatch -and (Test-Path $sharedProjectBackup)) {
        Move-Item $sharedProjectBackup $sharedProject -Force
    }

    if (Test-Path $globalJsonBackup) {
        Move-Item $globalJsonBackup $globalJson -Force
    }
}


