<#
.SYNOPSIS
    Lists everything FolderTools and its installer create, present ([x]) or absent ([ ]).
.DESCRIPTION
    Run it after an uninstall: everything must be absent, except what the summary calls out. Also shows the
    PendingFileRenameOperations entries (files Windows deletes at the next restart) and the newest uninstall log.
    Read-only: it changes nothing. Run it from an elevated prompt to see the package for every account.
.EXAMPLE
    .\scripts\uninstall-inventory.ps1
#>
$ErrorActionPreference = 'Continue'
$present = 0

function Show([string]$Label, [bool]$IsPresent, [string]$Detail = '') {
    if ($IsPresent) { $script:present++ }
    $mark = if ($IsPresent) { '[x]' } else { '[ ]' }
    $color = if ($IsPresent) { 'Yellow' } else { 'DarkGray' }
    Write-Host ("{0} {1}{2}" -f $mark, $Label, $(if ($Detail) { "  ($Detail)" } else { '' })) -ForegroundColor $color
}

function Section([string]$Title) { Write-Host "`n$Title" -ForegroundColor Cyan }

$installDir = Join-Path $env:ProgramFiles 'FolderTools'

Section 'Files (installer)'
Show "Install folder: $installDir" (Test-Path $installDir) $(if (Test-Path $installDir) { (Get-ChildItem $installDir -Recurse -File | Measure-Object).Count.ToString() + ' files' })
foreach ($shortcut in (Join-Path $env:ProgramData 'Microsoft\Windows\Start Menu\Programs\FolderTools.lnk'),
                      (Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs\FolderTools.lnk')) {
    Show "Start menu shortcut: $shortcut" (Test-Path $shortcut)
}

Section 'Files (app, current account)'
Show "Settings: $env:LOCALAPPDATA\FolderTools" (Test-Path "$env:LOCALAPPDATA\FolderTools")
Show "Roaming (not used by the app): $env:APPDATA\FolderTools" (Test-Path "$env:APPDATA\FolderTools")
$updateDir = Join-Path ([IO.Path]::GetTempPath()) 'FolderTools-Update'
Show "Update download: $updateDir" (Test-Path $updateDir)
$packageData = @(Get-ChildItem "$env:LOCALAPPDATA\Packages" -Directory -ErrorAction SilentlyContinue | Where-Object { $_.Name -like 'FolderTools.ContextMenu_*' -or $_.Name -like 'FolderTools.ColorMenu_*' })
Show "Package data: $env:LOCALAPPDATA\Packages\FolderTools.ContextMenu_*, FolderTools.ColorMenu_*" ($packageData.Count -gt 0) (($packageData | ForEach-Object Name) -join ', ')

Section 'Registry'
foreach ($type in 'Directory', 'Directory\Background', 'Drive') {
    Show "HKLM\SOFTWARE\Classes\$type\shell\FolderTools (classic menu)" (Test-Path "Registry::HKEY_LOCAL_MACHINE\SOFTWARE\Classes\$type\shell\FolderTools")
    if ($type -eq 'Directory') { Show 'HKLM\SOFTWARE\Classes\Directory\shell\FolderToolsColor (classic menu, Windows 10)' (Test-Path 'Registry::HKEY_LOCAL_MACHINE\SOFTWARE\Classes\Directory\shell\FolderToolsColor') }
    Show "HKCU\Software\Classes\$type\shell\FolderTools" (Test-Path "Registry::HKEY_CURRENT_USER\Software\Classes\$type\shell\FolderTools")
}
foreach ($root in 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall', 'HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall', 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall') {
    $key = Join-Path $root '{19F0E0B9-1859-4642-894A-648B58719233}_is1'
    Show "Uninstall entry: $key" (Test-Path $key)
}
Show 'HKCU\Software\FolderTools (menu toggle)' (Test-Path 'HKCU:\Software\FolderTools')
$muiKey = 'HKCU:\Software\Classes\Local Settings\Software\Microsoft\Windows\Shell\MuiCache'
$mui = @((Get-Item $muiKey -ErrorAction SilentlyContinue).Property | Where-Object { $_ -like "*$installDir\*" })
Show 'MuiCache values pointing into the install folder' ($mui.Count -gt 0) ($mui -join '; ')
foreach ($run in 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run', 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Run') {
    $values = @((Get-Item $run -ErrorAction SilentlyContinue).Property | Where-Object { $_ -like '*FolderTools*' })
    Show "Startup entry in $run (not used by the app)" ($values.Count -gt 0) ($values -join ', ')
}

Section 'Windows'
foreach ($name in 'FolderTools.ContextMenu', 'FolderTools.ColorMenu') {
    Show "Package $name (current account)" ($null -ne (Get-AppxPackage -Name $name -ErrorAction SilentlyContinue))
}
$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if ($isAdmin) {
    $all = @('FolderTools.ContextMenu', 'FolderTools.ColorMenu' | ForEach-Object { Get-AppxPackage -AllUsers -Name $_ -ErrorAction SilentlyContinue })
    Show 'Packages of FolderTools (any account)' ($all.Count -gt 0) (($all.PackageUserInformation | ForEach-Object { "$($_.UserSecurityId.Username): $($_.InstallState)" }) -join ', ')
}
else {
    Write-Host '    (package of other accounts: run from an elevated prompt to check)' -ForegroundColor DarkGray
}
$tasks = @(Get-ScheduledTask -ErrorAction SilentlyContinue | Where-Object { $_.TaskName -like '*FolderTools*' })
Show 'Scheduled tasks (not used by the app)' ($tasks.Count -gt 0) (($tasks | ForEach-Object TaskName) -join ', ')
$processes = @(Get-Process FolderTools -ErrorAction SilentlyContinue)
Show 'FolderTools.exe running' ($processes.Count -gt 0)
$hosts = @(Get-Process dllhost -ErrorAction SilentlyContinue | Where-Object { try { $_.Modules.ModuleName -contains 'FolderToolsMenu.dll' } catch { $false } })
Show 'dllhost with FolderToolsMenu.dll loaded' ($hosts.Count -gt 0) (($hosts | ForEach-Object Id) -join ', ')

Section 'Pending deletions at the next restart (PendingFileRenameOperations)'
$pending = @((Get-ItemProperty 'HKLM:\SYSTEM\CurrentControlSet\Control\Session Manager' -Name PendingFileRenameOperations -ErrorAction SilentlyContinue).PendingFileRenameOperations |
    Where-Object { $_ -like '*FolderTools*' })
Show 'Entries about FolderTools' ($pending.Count -gt 0) ($pending -join '; ')

Section 'Uninstall log'
$log = Get-ChildItem ([IO.Path]::GetTempPath()) -Filter 'Uninstall Log*.txt' -ErrorAction SilentlyContinue | Sort-Object LastWriteTime | Select-Object -Last 1
if ($log) {
    Write-Host "    $($log.FullName) ($($log.LastWriteTime))"
    Get-Content $log.FullName | Select-String 'Cleanup:|error|failed' | ForEach-Object { Write-Host "    $($_.Line)" }
}
else {
    Write-Host '    none in %TEMP% (build the installer with -UninstallLog to get one)' -ForegroundColor DarkGray
}

Write-Host ("`n{0} item(s) present." -f $present) -ForegroundColor $(if ($present) { 'Yellow' } else { 'Green' })
