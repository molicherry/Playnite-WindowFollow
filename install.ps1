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

function Test-SourceArtifacts {
    if (-not (Test-Path $SourceDir)) {
        throw "找不到构建产物目录：$SourceDir。请先运行 .\build.ps1 构建插件。"
    }

    $dll = Join-Path $SourceDir $moduleFileName
    $manifest = Join-Path $SourceDir 'extension.yaml'
    if (-not (Test-Path $dll)) { throw "构建产物缺少 $moduleFileName：$dll" }
    if (-not (Test-Path $manifest)) { throw "构建产物缺少 extension.yaml：$manifest" }

    $manifestText = Get-Content $manifest -Raw
    $manifestId = Get-ManifestField $manifestText 'Id'
    $manifestModule = Get-ManifestField $manifestText 'Module'
    $manifestVersion = Get-ManifestField $manifestText 'Version'

    if (-not $manifestId -or -not $manifestId.StartsWith($manifestIdPrefix, [StringComparison]::Ordinal)) {
        throw "extension.yaml 的 Id 不是本插件（应为 ${manifestIdPrefix}...），拒绝安装：$manifestId"
    }
    if ($manifestModule -ne $moduleFileName) {
        throw "extension.yaml 的 Module（$manifestModule）与 $moduleFileName 不一致。"
    }
    $parsedVersion = $null
    if (-not [System.Version]::TryParse($manifestVersion, [ref]$parsedVersion)) {
        throw "extension.yaml 的 Version 不是合法版本号：$manifestVersion"
    }

    Write-Info "扩展 Id   : $manifestId"
    Write-Info "扩展版本  : $manifestVersion"
    return $manifestId
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
    if (-not (Test-Path $targetDir)) {
        Write-Host "插件未安装（目录不存在）：$targetDir" -ForegroundColor Yellow
        return
    }

    $targetManifest = Join-Path $targetDir 'extension.yaml'
    if (-not (Test-Path $targetManifest)) {
        throw "目标目录里没有 extension.yaml，为避免误删已停止：$targetDir"
    }

    $targetManifestText = Get-Content $targetManifest -Raw
    $targetId = Get-ManifestField $targetManifestText 'Id'
    if (-not $targetId -or -not $targetId.StartsWith($manifestIdPrefix, [StringComparison]::Ordinal)) {
        throw "目标目录不是本插件（Id=$targetId），为避免误删已停止：$targetDir"
    }

    Remove-Item $targetDir -Recurse -Force
    Write-Host ""
    Write-Host "已卸载插件：$targetDir" -ForegroundColor Green
    Write-Host "提示：插件设置数据仍保留在 %AppData%\Playnite\ExtensionsData\c40d275b-5dad-4f8e-9640-137ec5267ef1（插件 GUID 目录，如需彻底清理请手动删除）。"
    return
}

# 覆盖安装时，旧版本做一次备份（默认）。
if ((Test-Path $targetDir) -and -not $NoBackup) {
    $backupRoot = Join-Path $scriptRoot '.build\install-backup'
    if (-not (Test-Path $backupRoot)) { New-Item -ItemType Directory -Path $backupRoot -Force | Out-Null }
    $backupDir = Join-Path $backupRoot (Get-Date -Format 'yyyyMMdd-HHmmss')
    Copy-Item $targetDir $backupDir -Recurse -Force
    Write-Info "旧版本已备份到：$backupDir"
}

# 校验旧目录确实是本插件，避免误删用户目录里的其他内容。
if (Test-Path $targetDir) {
    $existingManifest = Join-Path $targetDir 'extension.yaml'
    if (Test-Path $existingManifest) {
        $existingManifestText = Get-Content $existingManifest -Raw
        $existingId = Get-ManifestField $existingManifestText 'Id'
        if (-not $existingId -or -not $existingId.StartsWith($manifestIdPrefix, [StringComparison]::Ordinal)) {
            throw "目标文件夹已存在且不是本插件（Id=$existingId），为避免误删已停止：$targetDir"
        }
    }

    Remove-Item $targetDir -Recurse -Force
}

if (-not (Test-Path $resolvedExtensionsDir)) {
    New-Item -ItemType Directory -Path $resolvedExtensionsDir -Force | Out-Null
}

New-Item -ItemType Directory -Path $targetDir -Force | Out-Null
Copy-Item (Join-Path $SourceDir '*') -Destination $targetDir -Recurse -Force

Write-Step "安装成功"
Get-ChildItem $targetDir | ForEach-Object { Write-Info ("  - " + $_.Name) }
Write-Host ""
Write-Host "下一步：" -ForegroundColor Green
Write-Host "  1. 启动 Playnite（Desktop 模式），进入 设置 -> 扩展 -> $pluginName 配置 Sunshine 地址/用户名/密码。"
Write-Host "  2. 在 Streaming 手机上启动一次串流，然后从 Playnite 启动游戏验证手机端是否跟随游戏窗口。"
Write-Host "  3. 遇到问题请查看 Playnite 日志：%AppData%\Playnite\playnite.log（搜索 SunshineWindowFollow）。"
