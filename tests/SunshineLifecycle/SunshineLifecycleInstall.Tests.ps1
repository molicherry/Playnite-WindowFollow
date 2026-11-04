#Requires -Version 5.1
<#
.SYNOPSIS
    install.ps1 的生命周期回归测试（无需 Pester）。

.DESCRIPTION
    覆盖：
      1. 目标目录存在但 extension.yaml 缺失 -> 停止，且不删除目录内容；
      2. 目标目录存在但 manifest 无效 -> 停止，且不删除目录内容；
      3. 有效旧版本 -> 校验后备份（本测试用 -NoBackup 跳过备份）并替换；
      4. 卸载时 manifest 缺失 -> 停止，且不删除目录内容；
      5. 有效插件目录 -> 卸载成功。

    直接运行： powershell -ExecutionPolicy Bypass -File .\tests\SunshineLifecycle\SunshineLifecycleInstall.Tests.ps1
#>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$testRoot = Join-Path ([System.IO.Path]::GetTempPath()) ('SunshineLifecycleInstall-' + [Guid]::NewGuid().ToString('N'))
$repoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$installScript = Join-Path $repoRoot 'install.ps1'
$sourceDir = Join-Path $testRoot 'source'
$extensionsDir = Join-Path $testRoot 'extensions'
$targetDir = Join-Path $extensionsDir 'SunshineWindowFollow'
$pluginId = 'SunshineWindowFollow_c40d275b-5dad-4f8e-9640-137ec5267ef1'

function Write-Check([string]$message) {
    Write-Host "PASS  $message"
}

function Assert-True([bool]$condition, [string]$message) {
    if (-not $condition) {
        throw "测试断言失败：$message"
    }
}

function New-ValidManifest([string]$version) {
    return @"
Id: $pluginId
Name: Sunshine Window Follow
Version: $version
Module: SunshineWindowFollow.dll
Type: GenericPlugin
"@
}

function Invoke-InstallScript {
    param([hashtable]$Parameters)

    # 脚本内部用 throw 停止；这里捕获后由调用方断言退出行为。
    try {
        & $installScript @Parameters 6>$null 3>$null | Out-Null
        return $true
    }
    catch {
        Write-Verbose ("install.ps1 按预期停止：" + $_.Exception.Message)
        return $false
    }
}

try {
    if (-not (Test-Path -LiteralPath $installScript -PathType Leaf)) {
        throw "找不到 install.ps1：$installScript"
    }

    New-Item -ItemType Directory -Path $sourceDir -Force | Out-Null
    New-Item -ItemType Directory -Path $extensionsDir -Force | Out-Null
    Set-Content -LiteralPath (Join-Path $sourceDir 'extension.yaml') -Value (New-ValidManifest '2.0.0') -Encoding UTF8
    Set-Content -LiteralPath (Join-Path $sourceDir 'SunshineWindowFollow.dll') -Value 'fake-dll' -Encoding ASCII
    Set-Content -LiteralPath (Join-Path $sourceDir 'new-only.txt') -Value 'new' -Encoding ASCII

    # 1. manifest 缺失
    New-Item -ItemType Directory -Path $targetDir -Force | Out-Null
    Set-Content -LiteralPath (Join-Path $targetDir 'sentinel.txt') -Value 'keep-me' -Encoding ASCII
    $ok = Invoke-InstallScript -Parameters @{
        SourceDir = $sourceDir
        ExtensionsDir = $extensionsDir
        NoBackup = $true
    }
    Assert-True (-not $ok) '目标目录缺少 manifest 时安装必须停止'
    Assert-True (Test-Path -LiteralPath (Join-Path $targetDir 'sentinel.txt')) 'manifest 缺失时不能删除目标目录内容'
    Write-Check '目标目录 manifest 缺失时停止且不递归删除'

    # 2. manifest 无效
    Set-Content -LiteralPath (Join-Path $targetDir 'extension.yaml') -Value "Id: SomeoneElse`nVersion: nope`nModule: Other.dll" -Encoding UTF8
    $ok = Invoke-InstallScript -Parameters @{
        SourceDir = $sourceDir
        ExtensionsDir = $extensionsDir
        NoBackup = $true
    }
    Assert-True (-not $ok) '目标目录 manifest 无效时安装必须停止'
    Assert-True (Test-Path -LiteralPath (Join-Path $targetDir 'sentinel.txt')) 'manifest 无效时不能删除目标目录内容'
    Write-Check '目标目录 manifest 无效时停止且不递归删除'

    # 3. 有效旧版本 -> 替换
    Set-Content -LiteralPath (Join-Path $targetDir 'extension.yaml') -Value (New-ValidManifest '1.0.0') -Encoding UTF8
    Set-Content -LiteralPath (Join-Path $targetDir 'old-only.txt') -Value 'old' -Encoding ASCII
    $ok = Invoke-InstallScript -Parameters @{
        SourceDir = $sourceDir
        ExtensionsDir = $extensionsDir
        NoBackup = $true
    }
    Assert-True $ok '有效旧版本应安装成功'
    Assert-True (Test-Path -LiteralPath (Join-Path $targetDir 'new-only.txt')) '安装后应存在新版本文件'
    Assert-True (-not (Test-Path -LiteralPath (Join-Path $targetDir 'old-only.txt'))) '安装后旧版本文件应被替换'
    Write-Check '有效旧版本校验后安全替换'

    # 4. 卸载时 manifest 缺失
    Remove-Item -LiteralPath (Join-Path $targetDir 'extension.yaml') -Force
    Set-Content -LiteralPath (Join-Path $targetDir 'sentinel.txt') -Value 'keep-me' -Encoding ASCII
    $ok = Invoke-InstallScript -Parameters @{
        SourceDir = $sourceDir
        ExtensionsDir = $extensionsDir
        Uninstall = $true
    }
    Assert-True (-not $ok) '卸载时 manifest 缺失必须停止'
    Assert-True (Test-Path -LiteralPath (Join-Path $targetDir 'sentinel.txt')) '卸载时 manifest 缺失不能删除目录'
    Write-Check '卸载时 manifest 缺失停止且不递归删除'

    # 5. 有效卸载
    Set-Content -LiteralPath (Join-Path $targetDir 'extension.yaml') -Value (New-ValidManifest '2.0.0') -Encoding UTF8
    $ok = Invoke-InstallScript -Parameters @{
        SourceDir = $sourceDir
        ExtensionsDir = $extensionsDir
        Uninstall = $true
    }
    Assert-True $ok '有效插件目录应卸载成功'
    Assert-True (-not (Test-Path -LiteralPath $targetDir)) '卸载后目标目录应删除'
    Write-Check '有效插件目录卸载成功'

    Write-Host ''
    Write-Host 'install.ps1 生命周期测试全部通过。'
}
finally {
    if (Test-Path -LiteralPath $testRoot) {
        Remove-Item -LiteralPath $testRoot -Recurse -Force -ErrorAction SilentlyContinue
    }
}
