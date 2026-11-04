#Requires -Version 5.1
<#
.SYNOPSIS
    安装 / 更新 Playnite "Sunshine Window Follow" 插件。

.DESCRIPTION
    把 build.ps1 产出的 dist\ 目录（SunshineWindowFollow.dll + extension.yaml + icon.png）
    复制到 Playnite 扩展目录。

    扩展目录解析规则（与 Playnite 源码一致，见 Playnite\Settings\PlaynitePaths.cs）：
      * 便携版 Playnite（安装目录没有 unins000.exe）：<Playnite目录>\Extensions
      * 安装版 Playnite：%AppData%\Playnite\Extensions
      * 也可以用 -ExtensionsDir 直接指定扩展目录。

    本机没有安装 Playnite 时，脚本会回退到 %AppData%\Playnite\Extensions（安装到别的机器时再拷过去，
    或者用 -ExtensionsDir 指向目标机器上的目录）。

.PARAMETER ExtensionsDir
    直接指定 Playnite 扩展目录（该目录下应已有其他插件文件夹，或由 Playnite 自动创建）。
    例如：D:\Playnite\Extensions 或 C:\Users\me\AppData\Roaming\Playnite\Extensions。

.PARAMETER PlayniteDir
    Playnite 安装目录（包含 Playnite.FullscreenApp.exe）。用于推导扩展目录，
    并区分便携版 / 安装版。

.PARAMETER SourceDir
    构建产物目录，默认 <脚本目录>\dist。

.PARAMETER Uninstall
    卸载插件（只删除扩展目录下的 SunshineWindowFollow 文件夹，不会动 Playnite 的插件设置数据）。

.PARAMETER NoBackup
    覆盖安装前不备份旧版本（默认会把旧版本备份到 .build\install-backup）。

.EXAMPLE
    .\install.ps1
    .\install.ps1 -PlayniteDir "C:\Program Files (x86)\Playnite"
    .\install.ps1 -ExtensionsDir "D:\Playnite\Extensions"
    .\install.ps1 -Uninstall
#>
[CmdletBinding()]
param(
    [string]$ExtensionsDir,

    [string]$PlayniteDir,

    [string]$SourceDir,

    [switch]$Uninstall,

    [switch]$NoBackup
)

$ErrorActionPreference = 'Stop'
$scriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$pluginName = 'Sunshine Window Follow'
$pluginFolderName = 'SunshineWindowFollow'
$manifestIdPrefix = 'SunshineWindowFollow_'
$moduleFileName = 'SunshineWindowFollow.dll'

if (-not $SourceDir) { $SourceDir = Join-Path $scriptRoot 'dist' }

function Write-Step([string]$message) {
    Write-Host ""
    Write-Host "==> $message" -ForegroundColor Cyan
}

function Write-Info([string]$message) {
    Write-Host "    $message"
}

function Get-ManifestField([string]$manifestText, [string]$fieldName) {
    $match = [regex]::Match($manifestText, '(?m)^\s*' + [regex]::Escape($fieldName) + ':\s*(.+?)\s*$')
    if ($match.Success) { return $match.Groups[1].Value.Trim('"', "'") }
    return $null
}

function Read-PluginManifest {
    param(
        [Parameter(Mandatory = $true)][string]$ManifestPath,
        [Parameter(Mandatory = $true)][string]$Description
    )

    if (-not (Test-Path -LiteralPath $ManifestPath -PathType Leaf)) {
        throw "$Description 缺少 extension.yaml：$ManifestPath"
    }

    try {
        $manifestText = Get-Content -LiteralPath $ManifestPath -Raw -ErrorAction Stop
    }
    catch {
        throw "$Description 的 extension.yaml 无法读取：$ManifestPath（$($_.Exception.Message)）"
    }

    if ([string]::IsNullOrWhiteSpace($manifestText)) {
        throw "$Description 的 extension.yaml 为空：$ManifestPath"
    }

    $manifestId = Get-ManifestField $manifestText 'Id'
    $manifestModule = Get-ManifestField $manifestText 'Module'
    $manifestVersion = Get-ManifestField $manifestText 'Version'

    if (-not $manifestId -or -not $manifestId.StartsWith($manifestIdPrefix, [StringComparison]::Ordinal)) {
        throw "$Description 的 extension.yaml Id 无效（应为 ${manifestIdPrefix}...）：$manifestId"
    }
    if ($manifestModule -ne $moduleFileName) {
        throw "$Description 的 extension.yaml Module（$manifestModule）与 $moduleFileName 不一致。"
    }

    $parsedVersion = $null
    if (-not [System.Version]::TryParse($manifestVersion, [ref]$parsedVersion)) {
        throw "$Description 的 extension.yaml Version 不是合法版本号：$manifestVersion"
    }

    return [pscustomobject]@{
        Id      = $manifestId
        Module  = $manifestModule
        Version = $manifestVersion
    }
}

function Test-SourceArtifacts {
    if (-not (Test-Path -LiteralPath $SourceDir -PathType Container)) {
        throw "找不到构建产物目录：$SourceDir。请先运行 .\build.ps1 构建插件。"
    }

    $dll = Join-Path $SourceDir $moduleFileName
    if (-not (Test-Path -LiteralPath $dll -PathType Leaf)) {
        throw "构建产物缺少 $moduleFileName：$dll"
    }

    $manifest = Read-PluginManifest (Join-Path $SourceDir 'extension.yaml') '构建产物'

    Write-Info "扩展 Id   : $($manifest.Id)"
    Write-Info "扩展版本  : $($manifest.Version)"
    return $manifest.Id
}

function Find-PlayniteInstallDir {
    $candidates = New-Object System.Collections.Generic.List[string]

    $uninstallKeys = @(
        'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\*',
        'HKLM:\Software\Microsoft\Windows\CurrentVersion\Uninstall\*',
        'HKLM:\Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\*'
    )

    foreach ($key in $uninstallKeys) {
        try {
            $entries = Get-ItemProperty $key -ErrorAction SilentlyContinue
        }
        catch {
            continue
        }

        foreach ($entry in $entries) {
            if (-not $entry.DisplayName -or $entry.DisplayName -notlike '*Playnite*') { continue }

            if ($entry.InstallLocation) {
                $location = $entry.InstallLocation.Trim('"')
                if ((Test-Path (Join-Path $location 'Playnite.FullscreenApp.exe')) -and -not $candidates.Contains($location)) {
                    $candidates.Add($location)
                }
            }

            if ($entry.DisplayIcon) {
                $iconPath = $entry.DisplayIcon.Trim('"')
                if ($iconPath -match '^(.*?),\d+$') { $iconPath = $Matches[1] }
                $iconDir = Split-Path -Parent $iconPath
                if ($iconDir -and (Test-Path (Join-Path $iconDir 'Playnite.FullscreenApp.exe')) -and -not $candidates.Contains($iconDir)) {
                    $candidates.Add($iconDir)
                }
            }
        }
    }

    foreach ($commonPath in @(
        (Join-Path ${env:ProgramFiles(x86)} 'Playnite'),
        (Join-Path $env:ProgramFiles 'Playnite'),
        (Join-Path $env:LOCALAPPDATA 'Playnite'))) {
        if ((Test-Path (Join-Path $commonPath 'Playnite.FullscreenApp.exe')) -and -not $candidates.Contains($commonPath)) {
            $candidates.Add($commonPath)
        }
    }

    if ($candidates.Count -gt 0) { return $candidates[0] }
    return $null
}

function Resolve-ExtensionsDirectory {
    if ($ExtensionsDir) {
        Write-Info "使用参数指定的扩展目录。"
        return (Resolve-AbsolutePath $ExtensionsDir)
    }

    $detectedPlayniteDir = $null
    if ($PlayniteDir) {
        $detectedPlayniteDir = (Resolve-AbsolutePath $PlayniteDir)
        Write-Info "使用参数指定的 Playnite 目录：$detectedPlayniteDir"
    }
    else {
        $detectedPlayniteDir = Find-PlayniteInstallDir
        if ($detectedPlayniteDir) {
            Write-Info "自动检测到 Playnite 安装目录：$detectedPlayniteDir"
        }
        else {
            Write-Info "未检测到本机 Playnite 安装，回退到用户扩展目录。"
        }
    }

    if ($detectedPlayniteDir) {
        $hasUninstaller = Test-Path (Join-Path $detectedPlayniteDir 'unins000.exe')
        if ($hasUninstaller) {
            Write-Info "检测到安装版 Playnite（存在 unins000.exe），使用用户扩展目录。"
            return (Join-Path $env:APPDATA 'Playnite\Extensions')
        }

        Write-Info "检测到便携版 Playnite（没有 unins000.exe），使用程序目录下的 Extensions。"
        return (Join-Path $detectedPlayniteDir 'Extensions')
    }

    return (Join-Path $env:APPDATA 'Playnite\Extensions')
}

function Resolve-AbsolutePath([string]$path) {
    if ([System.IO.Path]::IsPathRooted($path)) { return $path }
    return [System.IO.Path]::GetFullPath((Join-Path (Get-Location).Path $path))
}

# ---------------------------------------------------------------- main

Write-Host "Playnite 插件安装/更新：$pluginName"

if (-not $Uninstall) {
    $manifestId = Test-SourceArtifacts
    Write-Info "构建产物  : $SourceDir"
}
else {
    Write-Step "卸载插件"
}

$resolvedExtensionsDir = Resolve-ExtensionsDirectory
$targetDir = Join-Path $resolvedExtensionsDir $pluginFolderName
Write-Info "扩展目录  : $resolvedExtensionsDir"
Write-Info "目标文件夹: $targetDir"

# 检测 Playnite 是否在运行（Windows 会锁定已加载的 DLL，复制可能失败）。
$playniteProcesses = @()
try {
    $playniteProcesses = @(Get-Process -Name 'Playnite.DesktopApp', 'Playnite.FullscreenApp', 'Playnite' -ErrorAction SilentlyContinue)
}
catch {
    $playniteProcesses = @()
}

if ($playniteProcesses.Count -gt 0) {
    Write-Host "警告：检测到 Playnite 正在运行，插件文件可能被占用。建议先退出 Playnite 再安装。" -ForegroundColor Yellow
}

if ($Uninstall) {
    if (-not (Test-Path -LiteralPath $targetDir)) {
        Write-Host "插件未安装（目录不存在）：$targetDir" -ForegroundColor Yellow
        return
    }

    if (-not (Test-Path -LiteralPath $targetDir -PathType Container)) {
        throw "目标路径存在但不是文件夹，为避免误删已停止：$targetDir"
    }

    # 先完整校验 manifest，缺失/无效时绝不递归删除。
    $null = Read-PluginManifest (Join-Path $targetDir 'extension.yaml') '目标文件夹'

    Remove-Item -LiteralPath $targetDir -Recurse -Force
    Write-Host ""
    Write-Host "已卸载插件：$targetDir" -ForegroundColor Green
    Write-Host "提示：插件设置数据仍保留在 %AppData%\Playnite\ExtensionsData\c40d275b-5dad-4f8e-9640-137ec5267ef1（插件 GUID 目录，如需彻底清理请手动删除）。"
    return
}

# 目标目录存在时，必须先校验 manifest；缺失/无效立即停止，
# 不能先备份或递归删除，否则可能误删用户目录。
$targetExists = Test-Path -LiteralPath $targetDir
if ($targetExists) {
    if (-not (Test-Path -LiteralPath $targetDir -PathType Container)) {
        throw "目标路径存在但不是文件夹，已停止：$targetDir"
    }

    $null = Read-PluginManifest (Join-Path $targetDir 'extension.yaml') '目标文件夹'
}

if (-not (Test-Path -LiteralPath $resolvedExtensionsDir)) {
    New-Item -ItemType Directory -Path $resolvedExtensionsDir -Force | Out-Null
}

# 先在扩展目录内准备一份新的插件副本并校验，再备份/替换旧目录。
$stagingDir = Join-Path $resolvedExtensionsDir ('.' + $pluginFolderName + '.staging.' + [Guid]::NewGuid().ToString('N'))
$oldDir = $null

try {
    New-Item -ItemType Directory -Path $stagingDir -Force | Out-Null
    Copy-Item -Path (Join-Path $SourceDir '*') -Destination $stagingDir -Recurse -Force
    $null = Read-PluginManifest (Join-Path $stagingDir 'extension.yaml') '构建产物副本'

    if ($targetExists -and -not $NoBackup) {
        $backupRoot = Join-Path (Join-Path $scriptRoot '.build') 'install-backup'
        if (-not (Test-Path -LiteralPath $backupRoot)) {
            New-Item -ItemType Directory -Path $backupRoot -Force | Out-Null
        }
        $backupDir = Join-Path $backupRoot ((Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + [Guid]::NewGuid().ToString('N').Substring(0, 8))
        New-Item -ItemType Directory -Path $backupDir -Force | Out-Null
        Copy-Item -Path (Join-Path $targetDir '*') -Destination $backupDir -Recurse -Force
        Write-Info "旧版本已备份到：$backupDir"
    }

    if ($targetExists) {
        $oldDir = Join-Path $resolvedExtensionsDir ('.' + $pluginFolderName + '.old.' + [Guid]::NewGuid().ToString('N'))
        Move-Item -LiteralPath $targetDir -Destination $oldDir
    }

    try {
        Move-Item -LiteralPath $stagingDir -Destination $targetDir
    }
    catch {
        if ($oldDir -and (Test-Path -LiteralPath $oldDir)) {
            Move-Item -LiteralPath $oldDir -Destination $targetDir
        }
        throw
    }

    if ($oldDir -and (Test-Path -LiteralPath $oldDir)) {
        Remove-Item -LiteralPath $oldDir -Recurse -Force -ErrorAction SilentlyContinue
    }
}
finally {
    if (Test-Path -LiteralPath $stagingDir) {
        Remove-Item -LiteralPath $stagingDir -Recurse -Force -ErrorAction SilentlyContinue
    }
}

Write-Step "安装成功"
Get-ChildItem $targetDir | ForEach-Object { Write-Info ("  - " + $_.Name) }
Write-Host ""
Write-Host "下一步：" -ForegroundColor Green
Write-Host "  1. 启动 Playnite（Desktop 模式），进入 设置 -> 扩展 -> $pluginName 配置 Sunshine 地址/用户名/密码。"
Write-Host "  2. 在 Streaming 手机上启动一次串流，然后从 Playnite 启动游戏验证手机端是否跟随游戏窗口。"
Write-Host "  3. 遇到问题请查看 Playnite 日志：%AppData%\Playnite\playnite.log（搜索 SunshineWindowFollow）。"
