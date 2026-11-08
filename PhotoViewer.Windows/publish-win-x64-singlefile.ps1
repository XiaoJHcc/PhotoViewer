[CmdletBinding()]
param(
    [ValidateSet('standard', 'ai', 'both')][string]$Edition = 'both',
    [ValidateSet('msix', 'portable', 'both')][string]$Form = 'both',
    [ValidateSet('Debug', 'Release')][string]$Configuration = 'Release',
    [string]$RuntimeIdentifier = 'win-x64',
    [string]$OutputDirectory = '',
    [string]$VCRedistDirectory = '',
    [string]$PackagePublisher = '',
    [string]$CertificatePath = '',
    [string]$CertificatePassword = '',
    [string]$CertificateThumbprint = '',
    [switch]$AllowUnsigned,
    [switch]$Local,
    [switch]$KeepSymbols
)

# -Form msix 是主发布形态：每个产品一个独立 MSIX（Identity.Name 用规范包 ID，显示名分别
# 由 PhotoViewerDisplayName 决定，AUMID/文件关联/单实例键随包身份分离）。
# -Form portable 是附加形态：单文件 EXE 沿用同一产品身份与数据规则，不另建身份。
# 产品显示名、包 ID、分发名与 DINO 能力都从 PhotoViewer.Windows.csproj 的 MSBuild 求值读取，
# 脚本不再自带一套产品判断；只把 edition 作为唯一外部身份输入传进去。

$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [System.Text.UTF8Encoding]::new($false)
$OutputEncoding = [Console]::OutputEncoding

if ($RuntimeIdentifier -ne 'win-x64') {
    throw '当前 Windows 发布仅验证并支持 win-x64，因为仓库中只引用了 LibHeif.Native.win-x64。'
}

$repoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$windowsProject = Join-Path $PSScriptRoot 'PhotoViewer.Windows.csproj'
$globalJson = Join-Path $repoRoot 'global.json'
$directoryBuildProps = Join-Path $repoRoot 'Directory.Build.props'
$releaseDirectory = Join-Path $repoRoot 'release'
$artifactsDirectory = Join-Path $repoRoot 'artifacts'

if (-not [string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = [System.IO.Path]::GetFullPath($OutputDirectory)
}
$sdkConfig = Get-Content $globalJson -Raw | ConvertFrom-Json
$requiredSdkVersion = $sdkConfig.sdk.version
$sdkRollForward = if ($sdkConfig.sdk.rollForward) { $sdkConfig.sdk.rollForward } else { 'latestMinor' }
$propsXml = [xml](Get-Content $directoryBuildProps -Raw -Encoding UTF8)
$appVersionNode = $propsXml.SelectSingleNode('/Project/PropertyGroup/AppVersion')
$buildNumberNode = $propsXml.SelectSingleNode('/Project/PropertyGroup/AppBuildNumber')
$appVersion = if ($null -ne $appVersionNode) { $appVersionNode.InnerText.Trim() } else { '' }
$appBuildNumber = if ($null -ne $buildNumberNode) { $buildNumberNode.InnerText.Trim() } else { '' }

if ([string]::IsNullOrWhiteSpace($appVersion)) {
    throw '未能从 Directory.Build.props 读取 AppVersion。'
}
if ([string]::IsNullOrWhiteSpace($appBuildNumber)) {
    throw '未能从 Directory.Build.props 读取 AppBuildNumber。'
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

# MSIX Publisher 必须与签名证书主题一致；默认与根工程一致的团队标识。
$packagePublisher = if ([string]::IsNullOrWhiteSpace($PackagePublisher)) { 'CN=xiaojh.cc' } else { $PackagePublisher }

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
$pythonPath = (Get-Command python -ErrorAction Stop).Source
$scanner = Join-Path $repoRoot 'Tools\check-windows-package.py'
$editions = if ($Edition -eq 'both') { @('standard', 'ai') } else { @($Edition) }
$runId = [guid]::NewGuid().ToString('N')
$validationBase = Join-Path $artifactsDirectory "acceptance\windows-editions\$runId"
$validatedPackages = @()
$runStarted = Get-Date

function Get-WindowsEditionIdentity {
    param([Parameter(Mandatory = $true)][string]$CurrentEdition)

    $properties = @(
        'PhotoViewerEdition',
        'PhotoViewerPackageId',
        'PhotoViewerProductId',
        'PhotoViewerDisplayName',
        'PhotoViewerDistributionName',
        'PhotoViewerEnableDino',
        'PhotoViewerApplicationId',
        'PhotoViewerPackageVersion',
        'PhotoViewerPackagePublisher',
        'PhotoViewerFileAssociationName'
    )
    $arguments = @('msbuild', $windowsProject, '-nologo', "-p:PhotoViewerEdition=$CurrentEdition")
    foreach ($property in $properties) {
        $arguments += "-getProperty:$property"
    }
    $raw = @(& $dotnetPath @arguments)
    if ($LASTEXITCODE -ne 0) {
        throw "MSBuild 求值产品身份失败（edition=$CurrentEdition），退出码：$LASTEXITCODE"
    }
    $text = ($raw -join [Environment]::NewLine)
    $start = $text.IndexOf('{')
    $end = $text.LastIndexOf('}')
    if ($start -lt 0 -or $end -le $start) {
        throw "MSBuild 未返回产品身份 JSON（edition=$CurrentEdition）：$text"
    }
    $values = ($text.Substring($start, $end - $start + 1) | ConvertFrom-Json).Properties
    if ($null -eq $values -or $values.PhotoViewerEdition -ne $CurrentEdition) {
        throw "MSBuild 返回的发行版本与请求不一致（请求 $CurrentEdition）。"
    }
    $expectedDino = if ($CurrentEdition -eq 'ai') { 'true' } else { 'false' }
    if ($values.PhotoViewerEnableDino -ne $expectedDino) {
        throw "DINO 能力必须由 edition 派生（$CurrentEdition 期望 $expectedDino，实际 $($values.PhotoViewerEnableDino)）。"
    }
    $suffix = if ($CurrentEdition -eq 'ai') { '.AI' } else { '' }
    if ($values.PhotoViewerPackageId -ne ('cc.xiaojh.PhotoViewer' + $suffix)) {
        throw "包 ID 与 edition 不符：$CurrentEdition -> $($values.PhotoViewerPackageId)"
    }
    [pscustomobject]@{
        Edition = $CurrentEdition
        PackageId = $values.PhotoViewerPackageId
        ProductId = $values.PhotoViewerProductId
        DisplayName = $values.PhotoViewerDisplayName
        DistributionName = $values.PhotoViewerDistributionName
        ApplicationId = $values.PhotoViewerApplicationId
        PackageVersion = $values.PhotoViewerPackageVersion
        FileAssociationName = $values.PhotoViewerFileAssociationName
        # 分发命名规则：<分发名前缀>-<版本>-win-x64；开发构建的程序集名不承担产品身份。
        AssemblyName = "$($values.PhotoViewerDistributionName)-$appVersion-win-x64"
    }
}

function Invoke-PackageScan {
    param(
        [Parameter(Mandatory = $true)][string]$PackagePath,
        [Parameter(Mandatory = $true)]$Identity,
        [Parameter(Mandatory = $true)][string]$ReportPath,
        [switch]$RequireSignature
    )

    $scanArguments = @(
        $scanner, $PackagePath,
        '--edition', $Identity.Edition,
        '--output', $ReportPath,
        '--version', $appVersion,
        '--build-number', $appBuildNumber,
        '--package-id', $Identity.PackageId,
        '--product-id', $Identity.ProductId,
        '--display-name', $Identity.DisplayName,
        '--application-id', $Identity.ApplicationId,
        '--app-assembly', $Identity.AssemblyName,
        '--publisher', $packagePublisher
    )
    if ($RequireSignature) {
        $scanArguments += '--require-signature'
    }
    & $pythonPath @scanArguments | Out-Host
    if ($LASTEXITCODE -ne 0) {
        throw "包扫描失败：$PackagePath（报告 $ReportPath）"
    }
}

function Publish-WindowsMsix {
    param(
        [Parameter(Mandatory = $true)]$Identity,
        [Parameter(Mandatory = $true)][string]$PublishDirectory,
        [Parameter(Mandatory = $true)][string]$ValidationRoot
    )

    $publisher = $packagePublisher
    $signingArguments = @()
    $signed = $false
    if (-not [string]::IsNullOrWhiteSpace($CertificatePath)) {
        $signingArguments = @(
            '-p:AppxPackageSigningEnabled=true',
            "-p:PackageCertificateKeyFile=$CertificatePath"
        )
        if (-not [string]::IsNullOrWhiteSpace($CertificatePassword)) {
            $signingArguments += "-p:PackageCertificatePassword=$CertificatePassword"
        }
        $signed = $true
    }
    elseif (-not [string]::IsNullOrWhiteSpace($CertificateThumbprint)) {
        $signingArguments = @(
            '-p:AppxPackageSigningEnabled=true',
            "-p:PackageCertificateThumbprint=$CertificateThumbprint"
        )
        $signed = $true
    }
    elseif ($Local -or $AllowUnsigned) {
        $signingArguments = @('-p:AppxPackageSigningEnabled=false')
        Write-Warning "[发布] $($Identity.Edition) MSIX 未签名：仅用于本地结构校验，不能安装或分发。"
    }
    else {
        throw "[发布] $($Identity.Edition) MSIX 必须签名：请传 -CertificatePath/-CertificatePassword 或 -CertificateThumbprint；本地验证可用 -Local 或 -AllowUnsigned 生成未签名包。"
    }

    if (Test-Path -LiteralPath $PublishDirectory) {
        Remove-Item -LiteralPath $PublishDirectory -Recurse -Force
    }
    $appPackages = Join-Path $ValidationRoot 'AppPackages'
    if (Test-Path -LiteralPath $appPackages) {
        Remove-Item -LiteralPath $appPackages -Recurse -Force
    }
    Write-Host "[发布] $($Identity.Edition) MSIX 输出目录: $PublishDirectory"

    & $dotnetPath publish $windowsProject `
        -c $Configuration -r $RuntimeIdentifier --self-contained true -o $PublishDirectory `
        "-p:PhotoViewerEdition=$($Identity.Edition)" `
        "-p:WindowsReleaseAssemblyName=$($Identity.AssemblyName)" `
        "-p:WindowsPackageForm=msix" `
        "-p:PhotoViewerPackagePublisher=$publisher" `
        "-p:WindowsVCRedistDirectory=$VCRedistDirectory" `
        "-p:AppxPackageDir=$appPackages\" `
        -p:Platform=x64 -p:PublishAppxPackage=true -p:AppxBundle=Never `
        -p:PublishSingleFile=false -p:PublishTrimmed=false -p:PublishReadyToRun=false `
        -p:DebugType=None -p:DebugSymbols=false @signingArguments | Out-Host
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet publish（MSIX，$($Identity.Edition)）失败，退出码：$LASTEXITCODE"
    }
    if (-not $KeepSymbols) {
        Get-ChildItem $PublishDirectory -Filter '*.pdb' -File -Recurse -ErrorAction SilentlyContinue | Remove-Item -Force
    }

    $searchRoots = @($appPackages, $PublishDirectory) | Where-Object { Test-Path -LiteralPath $_ }
    $candidates = @(Get-ChildItem -Path $searchRoots -Filter '*.msix' -Recurse -File -ErrorAction SilentlyContinue |
        Where-Object { $_.LastWriteTime -ge $runStarted } |
        Sort-Object LastWriteTime -Descending)
    if ($candidates.Count -ne 1) {
        throw "MSIX 打包未产出唯一 .msix（实际 $($candidates.Count) 个）。检查 Windows SDK makeappx 与 MSIX 打包目标是否可用：$(($candidates | ForEach-Object { $_.FullName }) -join '; ')"
    }

    $msixName = "$($Identity.DistributionName)-$appVersion-win-x64.msix"
    $msixPath = Join-Path $ValidationRoot $msixName
    Copy-Item -LiteralPath $candidates[0].FullName -Destination $msixPath -Force

    $scanPath = Join-Path $ValidationRoot 'package.json'
    Invoke-PackageScan -PackagePath $msixPath -Identity $Identity -ReportPath $scanPath -RequireSignature:$signed

    $scan = Get-Content -LiteralPath $scanPath -Raw -Encoding UTF8 | ConvertFrom-Json
    $manifest = [pscustomobject]@{
        Edition = $Identity.Edition
        Form = 'msix'
        Local = [bool]$Local
        PackageId = $Identity.PackageId
        ProductId = $Identity.ProductId
        DisplayName = $Identity.DisplayName
        DistributionName = $Identity.DistributionName
        ApplicationId = $Identity.ApplicationId
        PackageVersion = $Identity.PackageVersion
        PackageFamilyName = $scan.identity.packageFamilyName
        Aumid = $scan.identity.aumid
        FileName = $msixName
        SHA256 = (Get-FileHash $msixPath -Algorithm SHA256).Hash
        Bytes = (Get-Item $msixPath).Length
        Signed = [bool]$scan.identity.signed
        SignaturePresent = [bool]$scan.identity.signaturePresent
        Publisher = $scan.identity.publisher
        PackageScan = $scanPath
        VCRedistDirectory = $VCRedistDirectory
        VCRedistVersion = $vcRedist.Version.ToString()
        InstallValidation = 'pending: two-package install/update/uninstall must be verified on Windows with sideload policy; this script does not install packages.'
        Checks = @()
    }
    $manifest | ConvertTo-Json -Depth 6 | Set-Content (Join-Path $ValidationRoot 'deployment.json') -Encoding UTF8
    Write-Host "[发布] $($Identity.Edition) MSIX 身份校验通过：$msixPath"
    return [pscustomobject]@{
        Edition = $Identity.Edition
        Form = 'msix'
        Path = $msixPath
        Name = $msixName
        SHA256 = $manifest.SHA256
        Signed = $manifest.Signed
        PackageId = $Identity.PackageId
        Aumid = $manifest.Aumid
    }
}

function Publish-WindowsPortable {
    param(
        [Parameter(Mandatory = $true)]$Identity,
        [Parameter(Mandatory = $true)][string]$PublishDirectory,
        [Parameter(Mandatory = $true)][string]$ValidationRoot
    )

    $exeName = "$($Identity.AssemblyName).exe"
    Write-Host "[发布] $($Identity.Edition) 单文件 EXE 输出目录: $PublishDirectory"
    if (Test-Path -LiteralPath $PublishDirectory) {
        Remove-Item -LiteralPath $PublishDirectory -Recurse -Force
    }

    & $dotnetPath publish $windowsProject `
        -c $Configuration -r $RuntimeIdentifier --self-contained true -o $PublishDirectory `
        "-p:PhotoViewerEdition=$($Identity.Edition)" `
        "-p:WindowsReleaseAssemblyName=$($Identity.AssemblyName)" `
        "-p:WindowsVCRedistDirectory=$VCRedistDirectory" `
        -p:PublishSingleFile=true -p:IncludeAllContentForSelfExtract=true `
        -p:EnableCompressionInSingleFile=true -p:PublishTrimmed=false -p:PublishReadyToRun=false `
        -p:DebugType=None -p:DebugSymbols=false | Out-Host
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet publish（单文件，$($Identity.Edition)）失败，退出码：$LASTEXITCODE"
    }
    if (-not $KeepSymbols) {
        Get-ChildItem $PublishDirectory -Filter '*.pdb' -File -ErrorAction SilentlyContinue | Remove-Item -Force
    }
    $exePath = Join-Path $PublishDirectory $exeName
    $remainingFiles = @(Get-ChildItem $PublishDirectory -Recurse -File | Where-Object { $_.Extension -ne '.pdb' })
    if ($remainingFiles.Count -ne 1 -or $remainingFiles[0].FullName -ne $exePath) {
        throw '单文件发布目录不是独立单 EXE，停止归档。'
    }
    $scanPath = Join-Path $ValidationRoot 'package.json'
    Invoke-PackageScan -PackagePath $exePath -Identity $Identity -ReportPath $scanPath

    $validationResults = @()
    $previousExtractDirectory = $env:DOTNET_BUNDLE_EXTRACT_BASE_DIR
    try {
        foreach ($variant in @(
            @{ Directory = 'original'; FileName = $exeName },
            @{ Directory = '改名 空格目录'; FileName = '照片 查看器.exe' }
        )) {
            $validationDirectory = Join-Path $ValidationRoot $variant.Directory
            New-Item -ItemType Directory -Path $validationDirectory -Force | Out-Null
            $validationExe = Join-Path $validationDirectory $variant.FileName
            Copy-Item -LiteralPath $exePath -Destination $validationExe
            $renamedScan = Join-Path $validationDirectory 'package.json'
            Invoke-PackageScan -PackagePath $validationExe -Identity $Identity -ReportPath $renamedScan
            foreach ($check in @(
                @{ Mode = 'smoke'; Report = 'checks.json' },
                @{ Mode = 'settings'; Report = 'settings-checks.json' },
                @{ Mode = 'tools'; Report = 'tools-checks.json' },
                @{ Mode = 'edition'; Report = "edition-checks-$($Identity.Edition).json" }
            )) {
                $checkOutput = "reports/$($check.Mode)"
                $env:DOTNET_BUNDLE_EXTRACT_BASE_DIR = Join-Path $validationDirectory "bundle-cache-$($check.Mode)"
                $process = Start-Process -FilePath $validationExe -WorkingDirectory $validationDirectory `
                    -ArgumentList "--pv-native-$($check.Mode)", '--pv-native-portable', "--pv-acceptance-output=$checkOutput" -PassThru
                try {
                    if (-not $process.WaitForExit(300000)) {
                        $process.Kill()
                        $process.WaitForExit()
                        throw "发布验收 $($check.Mode) 超时，报告目录：$validationDirectory"
                    }
                    if ($process.ExitCode -ne 0) {
                        throw "发布验收 $($check.Mode) 失败（$($process.ExitCode)），报告目录：$validationDirectory"
                    }
                } finally {
                    $process.Dispose()
                }
                $reportPath = Join-Path (Join-Path $validationDirectory $checkOutput) $check.Report
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
                Remove-Item -LiteralPath $env:DOTNET_BUNDLE_EXTRACT_BASE_DIR -Recurse -Force
                Write-Host "[发布] $($Identity.Edition) / $($variant.FileName) / $($check.Mode): $($results.Count) 项 PASS"
            }
            Remove-Item -LiteralPath $validationExe -Force
        }
    } finally {
        $env:DOTNET_BUNDLE_EXTRACT_BASE_DIR = $previousExtractDirectory
    }
    $crtFiles = @('msvcp140.dll', 'msvcp140_1.dll', 'vcruntime140.dll', 'vcruntime140_1.dll') | ForEach-Object {
        [pscustomobject]@{ Name = $_; SHA256 = (Get-FileHash (Join-Path $VCRedistDirectory $_) -Algorithm SHA256).Hash }
    }
    $manifest = [pscustomobject]@{
        Edition = $Identity.Edition
        Form = 'portable'
        Local = [bool]$Local
        PackageId = $Identity.PackageId
        ProductId = $Identity.ProductId
        DisplayName = $Identity.DisplayName
        DistributionName = $Identity.DistributionName
        FileName = $exeName
        SHA256 = (Get-FileHash $exePath -Algorithm SHA256).Hash
        Bytes = (Get-Item $exePath).Length
        PackageScan = $scanPath
        VCRedistDirectory = $VCRedistDirectory
        VCRedistVersion = $vcRedist.Version.ToString()
        VCRedistFiles = @($crtFiles)
        Checks = $validationResults
    }
    $manifest | ConvertTo-Json -Depth 6 | Set-Content (Join-Path $ValidationRoot 'deployment.json') -Encoding UTF8
    Write-Host "[发布] $($Identity.Edition) 单文件验收通过：$exePath"
    return [pscustomobject]@{
        Edition = $Identity.Edition
        Form = 'portable'
        Path = $exePath
        Name = $exeName
        SHA256 = $manifest.SHA256
        Signed = $false
        PackageId = $Identity.PackageId
        Aumid = $null
    }
}

$forms = if ($Form -eq 'both') { @('msix', 'portable') } else { @($Form) }
foreach ($currentEdition in $editions) {
    $identity = Get-WindowsEditionIdentity -CurrentEdition $currentEdition
    Write-Host "[发布] $currentEdition 身份: package=$($identity.PackageId) product=$($identity.ProductId) display=$($identity.DisplayName) distribution=$($identity.DistributionName) assembly=$($identity.AssemblyName)"
    Write-Host "[发布] 构建输入: edition=$currentEdition（DINO 与所有产品映射由 edition 派生）"

    foreach ($currentForm in $forms) {
        $container = if ($currentForm -eq 'msix') { 'msix' } else { 'singlefile' }
        if (-not [string]::IsNullOrWhiteSpace($OutputDirectory)) {
            $editionOutput = Join-Path $OutputDirectory $currentEdition
            $publishDirectory = if ($forms.Count -gt 1) {
                Join-Path $editionOutput $container
            } else {
                $editionOutput
            }
        } elseif ($Local) {
            $publishDirectory = Join-Path $artifactsDirectory "scratch\windows-local-publish\$currentEdition\$container"
        } else {
            $publishDirectory = Join-Path $PSScriptRoot "bin\$currentEdition\$Configuration\publish-$container\win-x64"
        }
        $validationRoot = if ($forms.Count -gt 1) {
            Join-Path (Join-Path $validationBase $currentEdition) $currentForm
        } else {
            Join-Path $validationBase $currentEdition
        }
        New-Item -ItemType Directory -Path $validationRoot -Force | Out-Null

        $package = if ($currentForm -eq 'msix') {
            Publish-WindowsMsix -Identity $identity -PublishDirectory $publishDirectory -ValidationRoot $validationRoot
        } else {
            Publish-WindowsPortable -Identity $identity -PublishDirectory $publishDirectory -ValidationRoot $validationRoot
        }
        $validatedPackages += $package
    }
}

if ($Edition -eq 'both' -and $Form -ne 'msix') {
    $standardPackage = $validatedPackages | Where-Object { $_.Edition -eq 'standard' -and $_.Form -eq 'portable' }
    $aiPackage = $validatedPackages | Where-Object { $_.Edition -eq 'ai' -and $_.Form -eq 'portable' }
    $roundtripOutput = Join-Path $validationBase 'roundtrip'
    $previousExtractDirectory = $env:DOTNET_BUNDLE_EXTRACT_BASE_DIR
    try {
        $env:DOTNET_BUNDLE_EXTRACT_BASE_DIR = Join-Path $roundtripOutput 'bundle-cache'
        $arguments = @(
            '--pv-native-edition-roundtrip=all',
            ('--pv-edition-standard-exe="{0}"' -f $standardPackage.Path),
            ('--pv-edition-ai-exe="{0}"' -f $aiPackage.Path),
            ('--pv-acceptance-output="{0}"' -f $roundtripOutput)
        )
        $process = Start-Process -FilePath $aiPackage.Path -WorkingDirectory $repoRoot -ArgumentList $arguments -PassThru
        try {
            if (-not $process.WaitForExit(300000)) {
                $process.Kill()
                $process.WaitForExit()
                throw "双版本往返验收超时：$roundtripOutput"
            }
            if ($process.ExitCode -ne 0) { throw "双版本往返验收失败（$($process.ExitCode)）：$roundtripOutput" }
        } finally {
            $process.Dispose()
        }
        $roundtripReport = Join-Path $roundtripOutput 'edition-roundtrip-all.txt'
        $results = @(Get-Content -LiteralPath $roundtripReport -Encoding UTF8)
        if ($results -notcontains '== ORCHESTRATOR PASS' -or
            @($results | Where-Object { $_ -match '^(FAIL |== ORCHESTRATOR FAIL)' }).Count -ne 0) {
            throw "双版本往返报告未通过：$roundtripReport"
        }
        $stageReports = @()
        foreach ($stage in @('prepare', 'standard', 'ai')) {
            $stageReport = Join-Path $roundtripOutput "edition-roundtrip-$stage.txt"
            $stageResults = @(Get-Content -LiteralPath $stageReport -Encoding UTF8)
            if (@($stageResults | Where-Object { $_ -like "== STAGE PASS $stage passed=* failed=0" }).Count -ne 1 -or
                @($stageResults | Where-Object { $_ -match '^(FAIL |== STAGE FAIL)' }).Count -ne 0) {
                throw "双版本往返阶段未通过：$stageReport"
            }
            $stageReports += $stageReport
        }
        [pscustomobject]@{ Packages = $validatedPackages; Report = $roundtripReport; Stages = $stageReports } |
            ConvertTo-Json -Depth 5 | Set-Content (Join-Path $roundtripOutput 'deployment.json') -Encoding UTF8
        Remove-Item -LiteralPath $env:DOTNET_BUNDLE_EXTRACT_BASE_DIR -Recurse -Force
        Write-Host '[发布] AI → 标准 → AI 跨进程往返验收通过'
    } finally {
        $env:DOTNET_BUNDLE_EXTRACT_BASE_DIR = $previousExtractDirectory
    }
}

if (-not $Local) {
    $releasePackages = Join-Path $releaseDirectory 'packages'
    New-Item -ItemType Directory -Path $releasePackages -Force | Out-Null
    foreach ($package in $validatedPackages) {
        Copy-Item -LiteralPath $package.Path -Destination (Join-Path $releasePackages $package.Name) -Force
    }
    & $pythonPath (Join-Path $repoRoot 'Tools\check-release.py') --write
    if ($LASTEXITCODE -ne 0) { throw '发布归档布局或校验清单生成失败。' }
}
Write-Host "[发布] 验收目录：$validationBase"
if ($Form -ne 'portable') {
    Write-Host '[发布] MSIX 双包安装 / 同时运行 / 分别更新卸载需在 Windows 上以真实旁加载策略验收；本脚本只校验包身份、版本与资源，不安装任何包。'
}
