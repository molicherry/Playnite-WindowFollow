#Requires -Version 5.1
<#
.SYNOPSIS
    构建 Playnite "Sunshine Window Follow" 插件。

.DESCRIPTION
    本机验证过的构建路径分两种，脚本会自动选择：

    1. dotnet SDK 可用时（推荐日常开发）：
       直接 dotnet build SunshineWindowFollow.csproj（官方 SDK-style 工程）。
    2. 只有 MSBuild/Roslyn（例如只装了 Visual Studio 2022 BuildTools、没有 dotnet CLI）：
       使用 VS 自带的 Roslyn csc.exe，配合从 NuGet 下载的
         - Microsoft.NETFramework.ReferenceAssemblies.net462（.NET Framework 4.6.2 参考程序集）
         - PlayniteSDK（Playnite SDK 程序集）
       做离线编译。首次运行需要联网下载约 20MB 的 NuGet 包，缓存在 .build\packages。

    两种方式最终产物都放到 dist\ 目录（SunshineWindowFollow.dll + extension.yaml + icon.png），
    可直接用 install.ps1 安装 / 更新到 Playnite。

.PARAMETER Configuration
    Release（默认）或 Debug。

.PARAMETER OutputDir
    输出目录，默认 <脚本目录>\dist。

.PARAMETER PlayniteDir
    本机 Playnite 安装目录（包含 Playnite.SDK.dll）。指定后优先引用其中的 Playnite.SDK.dll。
    install.ps1 也用它来解析便携版 Playnite 的扩展目录。

.PARAMETER PlayniteSdkVersion
    使用 NuGet 回退时引用的 PlayniteSDK 版本，默认 6.17.0。
    请根据实际安装的 Playnite 版本调整（Playnite 只接受 SDK 主版本相同且不高于自身版本的插件）。

.PARAMETER OfflineOnly
    强制使用 csc 离线构建（即使检测到 dotnet SDK）。

.PARAMETER CscPath
    显式指定 Roslyn csc.exe 路径（默认自动在 VS2022 BuildTools / VS 目录中查找）。

.PARAMETER ForceDownload
    删除 .build\packages 缓存并重新下载 NuGet 包。

.EXAMPLE
    .\build.ps1
    .\build.ps1 -Configuration Debug
    .\build.ps1 -PlayniteDir "C:\Program Files (x86)\Playnite"
#>
[CmdletBinding()]
param(
    [ValidateSet('Release', 'Debug')]
    [string]$Configuration = 'Release',

    [string]$OutputDir,

    [string]$PlayniteDir,

    [string]$PlayniteSdkVersion = '6.17.0',

    [switch]$OfflineOnly,

    [string]$CscPath,

    [switch]$ForceDownload,

    [switch]$NoClean
)

$ErrorActionPreference = 'Stop'
$scriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$assemblyName = 'SunshineWindowFollow'
$packageRoot = Join-Path $scriptRoot '.build\packages'
if (-not $OutputDir) { $OutputDir = Join-Path $scriptRoot 'dist' }

function Write-Step([string]$message) {
    Write-Host ""
    Write-Host "==> $message" -ForegroundColor Cyan
}

function Write-Info([string]$message) {
    Write-Host "    $message"
}

function Ensure-Directory([string]$path) {
    if (-not (Test-Path $path)) {
        New-Item -ItemType Directory -Path $path -Force | Out-Null
    }
}

function Initialize-NuGetClient {
    # Windows PowerShell 5.1 默认可能仍用 TLS1.0，访问 api.nuget.org 需要 TLS1.2。
    try {
        [Net.ServicePointManager]::SecurityProtocol =
            [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12
    }
    catch { }
}

function Get-NuGetPackage {
    param(
        [Parameter(Mandatory = $true)][string]$PackageId,
        [Parameter(Mandatory = $true)][string]$Version,
        [Parameter(Mandatory = $true)][string]$CacheName
    )

    $targetDir = Join-Path $packageRoot $CacheName
    $markerFile = Join-Path $targetDir '.extracted.ok'
    if ((Test-Path $markerFile) -and -not $ForceDownload) {
        return $targetDir
    }

    Initialize-NuGetClient
    Ensure-Directory $packageRoot

    $idLower = $PackageId.ToLowerInvariant()
    $versionLower = $Version.ToLowerInvariant()
    $nupkgPath = Join-Path $packageRoot ("{0}.{1}.nupkg" -f $idLower, $versionLower)
    $url = "https://api.nuget.org/v3-flatcontainer/$idLower/$versionLower/$idLower.$versionLower.nupkg"

    if (-not (Test-Path $nupkgPath) -or $ForceDownload) {
        Write-Info "下载 NuGet 包 $PackageId $Version ..."
        Invoke-WebRequest -Uri $url -OutFile $nupkgPath -UseBasicParsing -TimeoutSec 300
    }
    else {
        Write-Info "使用已缓存的 NuGet 包 $PackageId $Version"
    }

    if (Test-Path $targetDir) { Remove-Item $targetDir -Recurse -Force }
    Ensure-Directory $targetDir
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [System.IO.Compression.ZipFile]::ExtractToDirectory($nupkgPath, $targetDir)
    Set-Content -Path $markerFile -Value "extracted from $($nupkgPath)"
    return $targetDir
}

function Get-ReferenceAssemblyDirectory {
    # 1) 机器上装了 .NET Framework 4.6.2 targeting pack 时直接用。
    $installed = Join-Path ${env:ProgramFiles(x86)} 'Reference Assemblies\Microsoft\Framework\.NETFramework\v4.6.2'
    if ((Test-Path (Join-Path $installed 'mscorlib.dll')) -and -not $ForceDownload) {
        return $installed
    }

    # 2) 否则使用 NuGet 参考程序集包（构建时等价于 targeting pack）。
    $packageDir = Get-NuGetPackage -PackageId 'Microsoft.NETFramework.ReferenceAssemblies.net462' -Version '1.0.3' -CacheName 'msnetfxref.net462.1.0.3'
    $refDir = Join-Path $packageDir 'build\.NETFramework\v4.6.2'
    if (-not (Test-Path (Join-Path $refDir 'mscorlib.dll'))) {
        throw "参考程序集包内容不完整：找不到 $refDir\mscorlib.dll"
    }
    return $refDir
}

function Get-PlayniteSdkAssembly {
    if ($PlayniteDir) {
        $candidate = Join-Path $PlayniteDir 'Playnite.SDK.dll'
        if (-not (Test-Path $candidate)) {
            throw "PlayniteDir 下找不到 Playnite.SDK.dll：$candidate"
        }
        return $candidate
    }

    $packageDir = Get-NuGetPackage -PackageId 'PlayniteSDK' -Version $PlayniteSdkVersion -CacheName ("playnitesdk." + $PlayniteSdkVersion)
    $sdkDll = Join-Path $packageDir 'lib\net462\Playnite.SDK.dll'
    if (-not (Test-Path $sdkDll)) {
        throw "PlayniteSDK NuGet 包内容不完整：找不到 $sdkDll"
    }
    return $sdkDll
}

function Find-Csc {
    if ($CscPath) {
        if (-not (Test-Path $CscPath)) { throw "找不到指定的 csc.exe：$CscPath" }
        return $CscPath
    }

    $vsRoots = @(
        (Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\2022'),
        (Join-Path $env:ProgramFiles 'Microsoft Visual Studio\2022'),
        (Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\2019')
    )
    $editions = @('BuildTools', 'Community', 'Professional', 'Enterprise')
    foreach ($root in $vsRoots) {
        if (-not (Test-Path $root)) { continue }
        foreach ($edition in $editions) {
            $candidate = Join-Path $root "$edition\MSBuild\Current\Bin\Roslyn\csc.exe"
            if (Test-Path $candidate) { return $candidate }
        }
    }

    $command = Get-Command csc.exe -ErrorAction SilentlyContinue
    if ($command) { return $command.Source }

    throw "找不到 Roslyn csc.exe。请安装 Visual Studio 2022 BuildTools（含 MSBuild）后用 -CscPath 指定，或安装 dotnet SDK。"
}

function Copy-Artifacts {
    param([string]$BuildOutputDir)

    Ensure-Directory $OutputDir
    if (-not $NoClean) {
        Get-ChildItem $OutputDir -Force | Remove-Item -Recurse -Force -ErrorAction SilentlyContinue
    }

    $dllPath = Join-Path $BuildOutputDir "$assemblyName.dll"
    if (-not (Test-Path $dllPath)) {
        throw "构建输出中找不到 $dllPath"
    }

    Copy-Item $dllPath $OutputDir -Force
    $pdbPath = Join-Path $BuildOutputDir "$assemblyName.pdb"
    if (Test-Path $pdbPath) { Copy-Item $pdbPath $OutputDir -Force }
    Copy-Item (Join-Path $scriptRoot 'extension.yaml') $OutputDir -Force
    $iconPath = Join-Path $scriptRoot 'icon.png'
    if (Test-Path $iconPath) { Copy-Item $iconPath $OutputDir -Force }

    # 校验 extension.yaml 的 Module 与程序名一致，避免装进 Playnite 后加载失败。
    $manifestText = Get-Content (Join-Path $OutputDir 'extension.yaml') -Raw
    $moduleMatch = [regex]::Match($manifestText, '(?m)^\s*Module:\s*(\S+)\s*$')
    if (-not $moduleMatch.Success) {
        throw 'extension.yaml 缺少 Module 字段'
    }
    if ($moduleMatch.Groups[1].Value -ne "$assemblyName.dll") {
        throw "extension.yaml 的 Module（$($moduleMatch.Groups[1].Value)）与程序名（$assemblyName.dll）不一致"
    }
}

function Invoke-DotnetBuild {
    param([string]$DotnetPath)

    $projectFile = Join-Path $scriptRoot 'SunshineWindowFollow.csproj'
    $arguments = @('build', $projectFile, '-c', $Configuration, '-nologo')
    if ($PlayniteDir) { $arguments += "/p:PlayniteDir=$PlayniteDir" }
    $arguments += "/p:PlayniteSdkVersion=$PlayniteSdkVersion"

    Write-Step "使用 dotnet SDK 构建：$DotnetPath build $projectFile -c $Configuration"
    & $DotnetPath @arguments
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet build 失败（退出码 $LASTEXITCODE）"
    }

    $binDir = Join-Path $scriptRoot "bin\$Configuration\net462"
    Copy-Artifacts -BuildOutputDir $binDir
}

function Invoke-CscBuild {
    $csc = Find-Csc
    $refDir = Get-ReferenceAssemblyDirectory
    $sdkDll = Get-PlayniteSdkAssembly
    $sdkVersion = [System.Reflection.AssemblyName]::GetAssemblyName($sdkDll).Version

    $frameworkReferences = @(
        'mscorlib.dll',
        'System.dll',
        'System.Core.dll',
        'System.Xml.dll',
        'System.Xaml.dll',
        'WindowsBase.dll',
        'PresentationCore.dll',
        'PresentationFramework.dll',
        'System.Net.Http.dll'
    )

    $sourceFiles = @(Get-ChildItem (Join-Path $scriptRoot 'src') -Recurse -Filter '*.cs' |
        Sort-Object FullName |
        ForEach-Object { $_.FullName })
    if ($sourceFiles.Count -eq 0) {
        throw "在 $(Join-Path $scriptRoot 'src') 下没有找到任何 .cs 源文件"
    }

    $intermediateDir = Join-Path $scriptRoot ('.build\obj\' + $Configuration)
    Ensure-Directory $intermediateDir
    $dllOutput = Join-Path $intermediateDir "$assemblyName.dll"
    $pdbOutput = Join-Path $intermediateDir "$assemblyName.pdb"
    $responseFile = Join-Path $intermediateDir 'csc.rsp'

    $responseLines = New-Object System.Collections.Generic.List[string]
    $responseLines.Add('/nologo')
    $responseLines.Add('/nostdlib+')
    $responseLines.Add('/target:library')
    $responseLines.Add('/langversion:7.3')
    $responseLines.Add('/codepage:65001')
    $responseLines.Add('/out:' + $dllOutput)
    if ($Configuration -eq 'Debug') {
        $responseLines.Add('/debug:portable')
        $responseLines.Add('/optimize-')
        $responseLines.Add('/define:DEBUG;TRACE')
    }
    else {
        $responseLines.Add('/debug:portable')
        $responseLines.Add('/optimize+')
        $responseLines.Add('/define:TRACE')
    }
    $responseLines.Add('/deterministic+')
    $responseLines.Add('/pdb:' + $pdbOutput)

    foreach ($reference in $frameworkReferences) {
        $referencePath = Join-Path $refDir $reference
        if (-not (Test-Path $referencePath)) {
            throw "参考程序集中缺少 $reference：$referencePath"
        }
        $responseLines.Add('/r:' + $referencePath)
    }
    $responseLines.Add('/r:' + $sdkDll)
    foreach ($sourceFile in $sourceFiles) {
        $responseLines.Add($sourceFile)
    }

    [System.IO.File]::WriteAllLines($responseFile, $responseLines)

    Write-Step "使用 Roslyn csc 编译（离线构建）"
    Write-Info "csc          : $csc"
    Write-Info "参考程序集   : $refDir"
    Write-Info "Playnite SDK : $sdkDll（版本 $sdkVersion）"
    Write-Info "源文件       : $($sourceFiles.Count) 个"
    & $csc "@$responseFile"
    if ($LASTEXITCODE -ne 0) {
        throw "csc 编译失败（退出码 $LASTEXITCODE），详见上面的编译错误"
    }

    Copy-Artifacts -BuildOutputDir $intermediateDir
    return $sdkVersion
}

# ---------------------------------------------------------------- main

Write-Host "Playnite Sunshine Window Follow 构建脚本"
Write-Host "  项目目录: $scriptRoot"
Write-Host "  配置    : $Configuration"

$dotnetPath = $null
if (-not $OfflineOnly) {
    $dotnetCommand = Get-Command dotnet -ErrorAction SilentlyContinue
    if ($dotnetCommand) { $dotnetPath = $dotnetCommand.Source }
}

$sdkVersionUsed = $null
if ($dotnetPath) {
    Invoke-DotnetBuild -DotnetPath $dotnetPath
    $sdkVersionUsed = '(dotnet build 使用 csproj 中配置的 SDK)'
}
else {
    if (-not $OfflineOnly) {
        Write-Info "未检测到 dotnet CLI，改用 csc 离线构建（首次运行需要联网下载 NuGet 包）。"
    }
    $sdkVersionUsed = Invoke-CscBuild
}

Write-Step "构建成功"
Write-Info "输出目录    : $OutputDir"
Write-Info "Playnite SDK: $sdkVersionUsed"
Write-Info "产物文件    :"
Get-ChildItem $OutputDir | ForEach-Object { Write-Info ("  - " + $_.Name) }
Write-Host ""
Write-Host "安装到 Playnite： powershell -ExecutionPolicy Bypass -File .\install.ps1" -ForegroundColor Green
Write-Host ""
