[CmdletBinding()]
param(
    [string]$Configuration = 'Release',
    [string]$RuntimeIdentifier = 'win-x64',
    [string]$OutputDirectory = '',
    [switch]$KeepSymbols
)

$ErrorActionPreference = 'Stop'

if ($RuntimeIdentifier -ne 'win-x64') {
    throw '当前 Windows 单文件发布仅验证并支持 win-x64，因为仓库中只引用了 LibHeif.Native.win-x64。'
}

$repoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$desktopProject = Join-Path $PSScriptRoot 'PhotoViewer.Desktop.csproj'
$sharedProject = Join-Path $repoRoot 'PhotoViewer\PhotoViewer.csproj'
$globalJson = Join-Path $repoRoot 'global.json'
$directoryBuildProps = Join-Path $repoRoot 'Directory.Build.props'
$releaseDirectory = Join-Path $repoRoot 'release'

if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path $PSScriptRoot 'bin\Release\publish-singlefile\win-x64'
}

$OutputDirectory = [System.IO.Path]::GetFullPath($OutputDirectory)
$sdkConfig = Get-Content $globalJson -Raw | ConvertFrom-Json
$requiredSdkVersion = $sdkConfig.sdk.version
$sdkRollForward = if ($sdkConfig.sdk.rollForward) { $sdkConfig.sdk.rollForward } else { 'latestMinor' }
$appVersion = ([xml](Get-Content $directoryBuildProps -Raw)).Project.PropertyGroup.AppVersion

if ([string]::IsNullOrWhiteSpace($appVersion)) {
    throw '未能从 Directory.Build.props 读取 AppVersion。'
}

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

$dotnetCandidates = @(
    (Join-Path $env:ProgramFiles 'dotnet\dotnet.exe'),
    (Join-Path $env:USERPROFILE '.dotnet\dotnet.exe'),
    (Get-Command dotnet -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Source -First 1)
) | Where-Object { $_ -and (Test-Path $_) } | Select-Object -Unique

$dotnetPath = $null
foreach ($candidate in $dotnetCandidates) {
    $installedSdks = & $candidate --list-sdks 2>$null
    $matchingSdk = $installedSdks | Where-Object {
        $_ -match '^(\S+)\s' -and (Test-SdkSatisfiesVersion -InstalledVersion $Matches[1] -RequiredVersion $requiredSdkVersion -RollForward $sdkRollForward)
    }
    if ($matchingSdk) {
        $dotnetPath = $candidate
        break
    }
}

if (-not $dotnetPath) {
    throw "未找到满足 global.json 版本约束（$requiredSdkVersion，rollForward=$sdkRollForward）的 dotnet SDK。"
}

Write-Host "[发布] 使用 dotnet: $dotnetPath"
Write-Host "[发布] 输出目录: $OutputDirectory"

$sharedProjectBackup = "$sharedProject.publishbak"
$originalSharedProjectContent = Get-Content $sharedProject -Raw
$patchedSharedProjectContent = $originalSharedProjectContent -replace '<TargetFrameworks>net9.0;net9.0-ios</TargetFrameworks>', '<TargetFramework>net9.0</TargetFramework>'
$needsProjectPatch = $patchedSharedProjectContent -ne $originalSharedProjectContent

try {
    if (Test-Path $OutputDirectory) {
        Remove-Item $OutputDirectory -Recurse -Force
    }

    foreach ($path in @(
        (Join-Path $repoRoot 'PhotoViewer\obj'),
        (Join-Path $repoRoot 'PhotoViewer.Desktop\obj')
    )) {
        if (Test-Path $path) {
            Remove-Item $path -Recurse -Force -ErrorAction SilentlyContinue
        }
    }

    if ($needsProjectPatch) {
        Copy-Item $sharedProject $sharedProjectBackup -Force
        Set-Content $sharedProject $patchedSharedProjectContent -Encoding UTF8
    }

    & $dotnetPath publish $desktopProject `
        -c $Configuration `
        -r $RuntimeIdentifier `
        --self-contained true `
        -o $OutputDirectory `
        /p:PublishSingleFile=true `
        /p:IncludeNativeLibrariesForSelfExtract=true `
        /p:EnableCompressionInSingleFile=true `
        /p:PublishTrimmed=false `
        /p:PublishReadyToRun=false `
        /p:DebugType=None `
        /p:DebugSymbols=false

    if ($LASTEXITCODE -ne 0) {
        throw "dotnet publish 失败，退出码：$LASTEXITCODE"
    }

    if (-not $KeepSymbols) {
        Get-ChildItem $OutputDirectory -Filter '*.pdb' -File -ErrorAction SilentlyContinue | Remove-Item -Force
    }

    $exePath = Join-Path $OutputDirectory 'PhotoViewer.Desktop.exe'
    if (-not (Test-Path $exePath)) {
        throw '发布完成后未找到 PhotoViewer.Desktop.exe。'
    }

    if (-not (Test-Path $releaseDirectory)) {
        New-Item -ItemType Directory -Path $releaseDirectory | Out-Null
    }

    $releaseExePath = Join-Path $releaseDirectory "PhotoViewer-$appVersion-win-x64.exe"
    Copy-Item $exePath $releaseExePath -Force

    $remainingFiles = Get-ChildItem $OutputDirectory -File | Sort-Object Name
    Write-Host '[发布] 输出文件：'
    $remainingFiles | Select-Object Name, Length | Format-Table -AutoSize

    if ($remainingFiles.Count -ne 1 -or $remainingFiles[0].Name -ne 'PhotoViewer.Desktop.exe') {
        Write-Warning '当前输出目录中仍包含除 exe 之外的文件，请检查上方列表。'
    }
    else {
        Write-Host "[发布] 单文件 exe 已生成：$exePath"
    }

    Write-Host "[发布] 已复制到仓库根目录：$releaseExePath"
}
finally {
    if ($needsProjectPatch -and (Test-Path $sharedProjectBackup)) {
        Move-Item $sharedProjectBackup $sharedProject -Force
    }
}

