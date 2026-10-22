[CmdletBinding()]
param(
    [string]$Configuration = 'Release',
    [string]$RuntimeIdentifier = 'win-x64',
    [string]$OutputDirectory = '',
    [string]$VCRedistDirectory = '',
    [switch]$KeepSymbols
)

$ErrorActionPreference = 'Stop'

if ($RuntimeIdentifier -ne 'win-x64') {
    throw '当前 Windows 单文件发布仅验证并支持 win-x64，因为仓库中只引用了 LibHeif.Native.win-x64。'
}

$repoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$windowsProject = Join-Path $PSScriptRoot 'PhotoViewer.Windows.csproj'
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

function Get-WindowsVCRedistInfo {
    param([Parameter(Mandatory = $true)][string]$Directory)

    $resolved = Get-Item -LiteralPath $Directory
    if (-not $resolved.PSIsContainer -or $resolved.PSProvider.Name -ne 'FileSystem') {
        throw "VC++ CRT 来源不是文件系统目录：$Directory"
    }
    $directoryPath = $resolved.FullName
    if ($directoryPath -match '(?i)(^|[\\/])debug_nonredist([\\/]|$)') {
        throw "禁止分发 debug_nonredist：$directoryPath"
    }
    foreach ($systemDirectory in @('System32', 'SysWOW64')) {
        $systemPath = Join-Path $env:WINDIR $systemDirectory
        if ($directoryPath.Equals($systemPath, [System.StringComparison]::OrdinalIgnoreCase) -or
            $directoryPath.StartsWith($systemPath + '\', [System.StringComparison]::OrdinalIgnoreCase)) {
            throw "禁止从 Windows 系统目录分发 CRT，请使用官方 Visual Studio Redist：$directoryPath"
        }
    }
    $minimumVersion = [version]'14.44.35211.0'
    $crtVersion = $null
    foreach ($name in @('msvcp140.dll', 'msvcp140_1.dll', 'vcruntime140.dll', 'vcruntime140_1.dll')) {
        $filePath = Join-Path $directoryPath $name
        if (-not (Test-Path -LiteralPath $filePath -PathType Leaf)) {
            throw "缺少 VC++ CRT 文件：$filePath"
        }
        $reader = [System.IO.BinaryReader]::new([System.IO.File]::OpenRead($filePath))
        try {
            if ($reader.BaseStream.Length -lt 64 -or $reader.ReadUInt16() -ne 0x5A4D) {
                throw "VC++ CRT 不是有效 PE 文件：$filePath"
            }
            $reader.BaseStream.Position = 0x3C
            $peOffset = $reader.ReadInt32()
            if ($peOffset -lt 64 -or $peOffset -gt $reader.BaseStream.Length - 26) {
                throw "VC++ CRT 的 PE 头无效：$filePath"
            }
            $reader.BaseStream.Position = $peOffset
            if ($reader.ReadUInt32() -ne 0x00004550 -or $reader.ReadUInt16() -ne 0x8664) {
                throw "VC++ CRT 必须是 x64 PE：$filePath"
            }
            $reader.BaseStream.Position = $peOffset + 24
            if ($reader.ReadUInt16() -ne 0x20B) {
                throw "VC++ CRT 必须是 PE32+：$filePath"
            }
        }
        finally {
            $reader.Dispose()
        }
        $info = [System.Diagnostics.FileVersionInfo]::GetVersionInfo($filePath)
        $version = [version]::new($info.FileMajorPart, $info.FileMinorPart, $info.FileBuildPart, $info.FilePrivatePart)
        if ($info.IsDebug -or $version.Major -ne 14 -or $version -lt $minimumVersion) {
            throw "VC++ CRT 必须是 Release v14 且版本不低于 $minimumVersion：$filePath ($version)"
        }
        if ($null -ne $crtVersion -and $version -ne $crtVersion) {
            throw "四个 VC++ CRT 文件版本必须一致：$filePath ($version，预期 $crtVersion)"
        }
        $crtVersion = $version
    }
    [pscustomobject]@{ Directory = $directoryPath; Version = $crtVersion }
}

function Resolve-WindowsVCRedist {
    param([string]$Directory)

    if (-not [string]::IsNullOrWhiteSpace($Directory)) {
        return Get-WindowsVCRedistInfo -Directory $Directory
    }
    $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
    if (-not (Test-Path -LiteralPath $vswhere -PathType Leaf)) {
        throw '未找到 vswhere.exe；请用 -VCRedistDirectory 指定官方 Visual Studio Release x64 CRT 目录。'
    }
    $matches = @(& $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Redist.14.Latest `
        -find 'VC\Redist\MSVC\*\x64\Microsoft.VC14*.CRT\msvcp140.dll')
    if ($LASTEXITCODE -ne 0) {
        throw "vswhere 查询 VC++ CRT 失败，退出码：$LASTEXITCODE"
    }
    $candidates = @()
    $rejections = @()
    foreach ($path in ($matches | Where-Object { -not [string]::IsNullOrWhiteSpace($_) } | Select-Object -Unique)) {
        try {
            $candidates += Get-WindowsVCRedistInfo -Directory (Split-Path -Parent $path)
        }
        catch {
            $rejections += $_.Exception.Message
        }
    }
    $selected = $candidates | Sort-Object -Property Version, Directory -Descending | Select-Object -First 1
    if ($null -eq $selected) {
        throw "未找到完整、版本一致且不低于 14.44.35211.0 的官方 Release x64 CRT；请用 -VCRedistDirectory 指定目录。$($rejections -join '; ')"
    }
    return $selected
}

$vcRedist = Resolve-WindowsVCRedist -Directory $VCRedistDirectory
$VCRedistDirectory = $vcRedist.Directory
Write-Host "[发布] VC++ CRT: $VCRedistDirectory ($($vcRedist.Version))"

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

if (Test-Path $OutputDirectory) {
    Remove-Item $OutputDirectory -Recurse -Force
}

foreach ($path in @(
    (Join-Path $repoRoot 'PhotoViewer\obj'),
    (Join-Path $repoRoot 'PhotoViewer.Windows\obj')
)) {
    if (Test-Path $path) {
        Remove-Item $path -Recurse -Force
    }
}

$assemblyName = "PhotoViewer-$appVersion-win-x64"
$exeName = "$assemblyName.exe"

& $dotnetPath publish $windowsProject `
    -c $Configuration `
    -r $RuntimeIdentifier `
    --self-contained true `
    -o $OutputDirectory `
    "-p:WindowsReleaseAssemblyName=$assemblyName" `
    "-p:WindowsVCRedistDirectory=$VCRedistDirectory" `
    -p:PublishSingleFile=true `
    -p:IncludeAllContentForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true `
    -p:PublishTrimmed=false `
    -p:PublishReadyToRun=false `
    -p:DebugType=None `
    -p:DebugSymbols=false

if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish 失败，退出码：$LASTEXITCODE"
}

if (-not $KeepSymbols) {
    Get-ChildItem $OutputDirectory -Filter '*.pdb' -File -ErrorAction SilentlyContinue | Remove-Item -Force
}

$exePath = Join-Path $OutputDirectory $exeName
if (-not (Test-Path $exePath)) {
    throw "发布完成后未找到 $exeName。"
}

$remainingFiles = @(Get-ChildItem $OutputDirectory -Recurse -File | Where-Object { $_.Extension -ne '.pdb' })
if ($remainingFiles.Count -ne 1 -or $remainingFiles[0].FullName -ne $exePath) {
    throw '发布目录不是独立单 EXE，停止归档。'
}

$validationRoot = Join-Path $releaseDirectory ("rebuild-check\windows-singlefile\" + [guid]::NewGuid().ToString('N'))
$validationResults = @()
$previousExtractDirectory = $env:DOTNET_BUNDLE_EXTRACT_BASE_DIR
try {
    foreach ($variant in @(
        @{ Directory = 'original'; FileName = $exeName },
        @{ Directory = '改名 空格目录'; FileName = '照片 查看器.exe' }
    )) {
        $validationDirectory = Join-Path $validationRoot $variant.Directory
        New-Item -ItemType Directory -Path $validationDirectory -Force | Out-Null
        $validationExe = Join-Path $validationDirectory $variant.FileName
        Copy-Item $exePath $validationExe
        $env:DOTNET_BUNDLE_EXTRACT_BASE_DIR = Join-Path $validationDirectory 'bundle-cache'
        foreach ($check in @(
            @{ Mode = 'smoke'; Report = 'checks.json' },
            @{ Mode = 'settings'; Report = 'settings-checks.json' },
            @{ Mode = 'tools'; Report = 'tools-checks.json' }
        )) {
            $process = Start-Process -FilePath $validationExe -WorkingDirectory $validationDirectory `
                -ArgumentList "--pv-native-$($check.Mode)", '--pv-native-portable' -PassThru
            try {
                if (-not $process.WaitForExit(180000)) {
                    $process.Kill()
                    $process.WaitForExit()
                    throw "发布验收 $($check.Mode) 超时，报告目录：$validationDirectory"
                }
                if ($process.ExitCode -ne 0) {
                    throw "发布验收 $($check.Mode) 失败（$($process.ExitCode)），报告目录：$validationDirectory"
                }
            }
            finally {
                $process.Dispose()
            }
            $reportPath = Join-Path $validationDirectory ("release\native-win-acceptance\" + $check.Report)
            $results = @(Get-Content $reportPath -Raw -Encoding UTF8 | ConvertFrom-Json | ForEach-Object { $_ })
            if ($results.Count -eq 0 -or @($results | Where-Object { $_ -notlike 'PASS *' }).Count -ne 0) {
                throw "发布验收报告未全部通过：$reportPath"
            }
            $validationResults += [pscustomobject]@{
                FileName = $variant.FileName
                Mode = $check.Mode
                Passed = $results.Count
                Report = $reportPath
            }
            Write-Host "[发布] $($variant.FileName) / $($check.Mode): $($results.Count) 项 PASS"
        }
    }
}
finally {
    $env:DOTNET_BUNDLE_EXTRACT_BASE_DIR = $previousExtractDirectory
}

$crtFiles = @('msvcp140.dll', 'msvcp140_1.dll', 'vcruntime140.dll', 'vcruntime140_1.dll') | ForEach-Object {
    [pscustomobject]@{ Name = $_; SHA256 = (Get-FileHash (Join-Path $VCRedistDirectory $_) -Algorithm SHA256).Hash }
}
$manifest = [pscustomobject]@{
    FileName = $exeName
    SHA256 = (Get-FileHash $exePath -Algorithm SHA256).Hash
    Bytes = (Get-Item $exePath).Length
    VCRedistDirectory = $VCRedistDirectory
    VCRedistVersion = $vcRedist.Version.ToString()
    VCRedistFiles = @($crtFiles)
    Checks = $validationResults
}
$manifest | ConvertTo-Json -Depth 5 | Set-Content (Join-Path $validationRoot 'deployment.json') -Encoding UTF8
$releaseExePath = Join-Path $releaseDirectory $exeName
Copy-Item $exePath $releaseExePath -Force
Write-Host "[发布] 单文件验收通过，已归档：$releaseExePath"
Write-Host "[发布] 验收目录：$validationRoot"

