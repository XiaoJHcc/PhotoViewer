<#
.SYNOPSIS
    初始化 Android 发布签名证书。
.DESCRIPTION
    交互式生成 PKCS12 格式的 release.keystore，并将配置写入 signing.json（gitignored）。
    仅需在全新机器上执行一次，或在需要更换证书时重新执行。
.PARAMETER KeyAlias
    Keystore 内的 Key 别名，默认 photoviewer。
.PARAMETER ValidityDays
    证书有效期（天），默认 36500（约 100 年）。
.PARAMETER KeystoreFile
    Keystore 文件相对于本脚本的路径，默认 keystore\release.keystore。
#>
[CmdletBinding()]
param(
    [string]$KeyAlias = 'photoviewer',
    [int]$ValidityDays = 36500,
    [string]$KeystoreFile = 'keystore\release.keystore'
)

$ErrorActionPreference = 'Stop'
$scriptDir = $PSScriptRoot

# ──────────────────────────────────────────────────────────────
# 1. 定位 keytool
# ──────────────────────────────────────────────────────────────

function Find-Keytool {
    # JAVA_HOME 优先
    if ($env:JAVA_HOME) {
        $kt = Join-Path $env:JAVA_HOME 'bin\keytool.exe'
        if (Test-Path $kt) { return $kt }
    }

    # 系统 PATH
    $kt = Get-Command keytool -ErrorAction SilentlyContinue
    if ($kt) { return $kt.Source }

    # .NET Android workload 随附的 Microsoft OpenJDK
    $msJdkRoot = 'C:\Program Files\Microsoft'
    if (Test-Path $msJdkRoot) {
        $found = Get-ChildItem $msJdkRoot -Filter 'jdk-*' -Directory -ErrorAction SilentlyContinue |
            Sort-Object Name -Descending |
            ForEach-Object { Join-Path $_.FullName 'bin\keytool.exe' } |
            Where-Object { Test-Path $_ } |
            Select-Object -First 1
        if ($found) { return $found }
    }

    # Android Studio 内嵌 JBR
    foreach ($candidate in @(
        'C:\Program Files\Android\Android Studio\jbr\bin\keytool.exe',
        'C:\Program Files (x86)\Android\Android Studio\jbr\bin\keytool.exe'
    )) {
        if (Test-Path $candidate) { return $candidate }
    }

    # Oracle / Eclipse Adoptium / Azul 等独立 JDK 安装
    foreach ($root in @('C:\Program Files\Java', 'C:\Program Files\Eclipse Adoptium',
                        'C:\Program Files\Zulu', 'C:\Program Files\BellSoft\LibericaJDK*')) {
        $found = Get-ChildItem $root -Filter 'keytool.exe' -Recurse -ErrorAction SilentlyContinue |
            Select-Object -First 1 -ExpandProperty FullName
        if ($found) { return $found }
    }

    return $null
}

$keytool = Find-Keytool
if (-not $keytool) {
    Write-Host ''
    Write-Host '未找到 keytool。请执行以下任一操作后重新运行本脚本：'
    Write-Host '  - 安装 JDK 并设置 JAVA_HOME 环境变量'
    Write-Host '  - 安装 Android Studio（内含 JDK）'
    Write-Host '  - 安装 .NET Android workload：dotnet workload install android'
    exit 1
}

Write-Host "[签名配置] 使用 keytool: $keytool"

# ──────────────────────────────────────────────────────────────
# 2. 确认 Keystore 路径
# ──────────────────────────────────────────────────────────────

$keystoreFullPath = [System.IO.Path]::GetFullPath((Join-Path $scriptDir $KeystoreFile))
$keystoreDir = Split-Path $keystoreFullPath -Parent

if (Test-Path $keystoreFullPath) {
    Write-Warning "Keystore 文件已存在：$keystoreFullPath"
    $confirm = Read-Host '是否覆盖并重新生成？(y/N)'
    if ($confirm -notin @('y', 'Y')) {
        Write-Host '已取消。若需重新配置密码，请直接编辑 signing.json。'
        exit 0
    }
}

if (-not (Test-Path $keystoreDir)) {
    New-Item -ItemType Directory -Path $keystoreDir | Out-Null
}

# ──────────────────────────────────────────────────────────────
# 3. 交互式收集密码
# ──────────────────────────────────────────────────────────────

Write-Host ''
Write-Host '=== 配置 Android 发布签名证书 ==='
Write-Host '说明：密码长度至少 6 位，建议使用强密码并妥善备份至密码管理器。'
Write-Host ''

function Read-SecureStringPlain {
    param([string]$Prompt)
    $ss = Read-Host $Prompt -AsSecureString
    return [System.Runtime.InteropServices.Marshal]::PtrToStringAuto(
        [System.Runtime.InteropServices.Marshal]::SecureStringToBSTR($ss))
}

$storePass = Read-SecureStringPlain '请输入 Keystore 密码'
$storePassConfirm = Read-SecureStringPlain '请再次输入 Keystore 密码（确认）'

if ($storePass -ne $storePassConfirm) {
    Write-Host '密码不一致，请重新运行。' -ForegroundColor Red
    exit 1
}
if ($storePass.Length -lt 6) {
    Write-Host 'Keystore 密码至少需要 6 位。' -ForegroundColor Red
    exit 1
}

$keyPass = Read-SecureStringPlain '请输入 Key 密码（可与 Keystore 密码相同）'
if ($keyPass.Length -lt 6) {
    Write-Host 'Key 密码至少需要 6 位。' -ForegroundColor Red
    exit 1
}

# ──────────────────────────────────────────────────────────────
# 4. 生成 Keystore
# ──────────────────────────────────────────────────────────────

$dname = 'CN=PhotoViewer, O=cc.xiaojh, C=CN'

Write-Host ''
Write-Host '[签名配置] 正在生成 Keystore...'

& $keytool `
    -genkeypair `
    -v `
    -storetype PKCS12 `
    -keystore $keystoreFullPath `
    -alias $KeyAlias `
    -keyalg RSA `
    -keysize 2048 `
    -validity $ValidityDays `
    -storepass $storePass `
    -keypass $keyPass `
    -dname $dname

if ($LASTEXITCODE -ne 0) {
    Write-Host "Keystore 生成失败（退出码 $LASTEXITCODE）。" -ForegroundColor Red
    exit $LASTEXITCODE
}

# ──────────────────────────────────────────────────────────────
# 5. 写入 signing.json
# ──────────────────────────────────────────────────────────────

$signingConfig = [ordered]@{
    keystoreFile     = ($KeystoreFile -replace '\\', '/')
    keystorePassword = $storePass
    keyAlias         = $KeyAlias
    keyPassword      = $keyPass
}

$signingJsonPath = Join-Path $scriptDir 'signing.json'
$signingConfig | ConvertTo-Json -Depth 2 | Set-Content $signingJsonPath -Encoding UTF8

# ──────────────────────────────────────────────────────────────
# 6. 完成提示
# ──────────────────────────────────────────────────────────────

Write-Host ''
Write-Host "[签名配置] Keystore 已生成：$keystoreFullPath"
Write-Host "[签名配置] 配置文件已写入：$signingJsonPath"
Write-Host ''
Write-Host '重要提示：' -ForegroundColor Yellow
Write-Host "  1. keystore/ 目录与 signing.json 已被 .gitignore 排除，不会提交到版本库。"
Write-Host "  2. 请将 $keystoreFullPath 和密码备份到密码管理器或安全存储，丢失后无法对已发布的 APK 进行更新。"
Write-Host "  3. 下次运行 [Publish Android APK (Release)] Task 将自动使用此证书签名。"
