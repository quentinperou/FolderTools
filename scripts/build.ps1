<#
.SYNOPSIS
    Builds FolderTools and its installer.

.DESCRIPTION
    Output in the dist\ folder:
      app\                              the app (FolderTools.exe...) and the context menu DLL (FolderToolsMenu.dll)
      FolderTools.Menu.msix             the "sparse" package that adds "FolderTools ›" to the Windows 11 context menu
      FolderTools-Setup-<version>.exe   the installer (needs Inno Setup 6)

    Prerequisites: see "Development" in the README.

.PARAMETER NoInstaller
    Do not build the installer (useful without Inno Setup).

.PARAMETER SkipTests
    Do not run the unit tests.

.PARAMETER UninstallLog
    Build an installer whose uninstaller always writes "Uninstall Log <date>.txt" in %TEMP%, even when started from
    Windows Settings (UninstallLogging=yes): to check what the uninstall removes (scripts\uninstall-inventory.ps1).

.EXAMPLE
    .\scripts\build.ps1
#>
[CmdletBinding()]
param(
    [switch]$NoInstaller,
    [switch]$SkipTests,
    [switch]$UninstallLog
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$dist = Join-Path $root 'dist'
$build = Join-Path $root 'build'
$appDir = Join-Path $dist 'app'

function Write-Step([string]$Text) { Write-Host "`n==> $Text" -ForegroundColor Cyan }

function Invoke-Native([scriptblock]$Command, [string]$What) {
    & $Command
    if ($LASTEXITCODE -ne 0) { throw "$What failed (exit code $LASTEXITCODE)." }
}

$version = ([xml](Get-Content (Join-Path $root 'Directory.Build.props'))).Project.PropertyGroup.Version
Write-Host "FolderTools $version" -ForegroundColor Green

Write-Step 'Visual Studio environment (MSVC, CMake, Windows SDK)'
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
if (-not (Test-Path $vswhere)) { throw 'Visual Studio Build Tools not found: see the prerequisites in the README.' }
$vs = & $vswhere -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if (-not $vs) { throw 'The Visual Studio "Desktop development with C++" workload is missing.' }
Import-Module (Join-Path $vs 'Common7\Tools\Microsoft.VisualStudio.DevShell.dll')
Enter-VsDevShell -VsInstallPath $vs -SkipAutomaticLocation -DevCmdArguments '-arch=x64 -host_arch=x64' 2>$null | Out-Null
foreach ($tool in 'cmake', 'ninja', 'makeappx', 'dotnet') {
    if (-not (Get-Command $tool -ErrorAction SilentlyContinue)) { throw "Tool not found: $tool. See the prerequisites in the README." }
}

# Start from an empty dist folder (build output only).
if ([IO.Directory]::Exists($dist)) { [IO.Directory]::Delete($dist, $true) }
New-Item -ItemType Directory -Force $appDir | Out-Null

if (-not $SkipTests) {
    Write-Step 'Tests'
    Invoke-Native { dotnet test (Join-Path $root 'tests\FolderTools.Tests') -c Release --nologo } 'The tests'
}

Write-Step 'Application (.NET)'
Invoke-Native {
    dotnet publish (Join-Path $root 'src\FolderTools') -c Release -r win-x64 --self-contained false `
        -p:DebugType=none -o $appDir --nologo
} 'Building the application'

Write-Step 'Context menu DLL (C++)'
$shellBuild = Join-Path $build 'shell'
Invoke-Native { cmake -S (Join-Path $root 'src\FolderTools.Shell') -B $shellBuild -G Ninja -DCMAKE_BUILD_TYPE=Release } 'CMake configuration'
Invoke-Native { cmake --build $shellBuild } 'Building the DLL'
Copy-Item (Join-Path $shellBuild 'FolderToolsMenu.dll') $appDir
# The colored folder icons of "Folder color ›", referenced by the desktop.ini of colored folders.
Copy-Item (Join-Path $root 'src\FolderTools.Shell\Colors') (Join-Path $appDir 'Colors') -Recurse

Write-Step 'Sparse package (Windows 11 menu)'
Invoke-Native { makeappx pack /o /nv /d (Join-Path $root 'package') /p (Join-Path $dist 'FolderTools.Menu.msix') } 'makeappx'

if (-not $NoInstaller) {
    Write-Step 'Installer (Inno Setup)'
    $iscc = @(
        (Get-Command iscc -ErrorAction SilentlyContinue).Source
        (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe')
        (Join-Path $env:ProgramFiles 'Inno Setup 6\ISCC.exe')
        (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe')
    ) | Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1
    if ($iscc) {
        $defines = @("/DAppVersion=$version")
        if ($UninstallLog) { $defines += '/DUninstallLog' }
        Invoke-Native { & $iscc /Q @defines (Join-Path $root 'installer\FolderTools.iss') } 'Inno Setup'
    }
    else {
        Write-Warning 'Inno Setup 6 not found: installer not built (winget install JRSoftware.InnoSetup).'
    }
}

Write-Step 'Done'
Get-ChildItem $dist | ForEach-Object { Write-Host "  dist\$($_.Name)" }