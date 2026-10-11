<#
.SYNOPSIS
    Checks installer\UserDataCleanup.iss without administrator rights.
.DESCRIPTION
    Builds tests\installer\CleanupTest.iss (per-user install), installs it silently, creates the kind of data the
    app creates (a folder in %LOCALAPPDATA%, a download folder in %TEMP%, a key in HKCU, MuiCache values), then
    uninstalls silently and checks that all of it is gone, and that a MuiCache value of another program is kept.
    Needs Inno Setup 6. Exit code 0 when everything passes.
#>
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$name = 'FolderToolsCleanupTest'
$appDir = Join-Path $env:LOCALAPPDATA "Programs\$name"
$dataDir = Join-Path $env:LOCALAPPDATA $name
$tempDir = Join-Path ([IO.Path]::GetTempPath()) "$name-Update"
$userKey = "HKCU:\Software\$name"
$muiKey = 'HKCU:\Software\Classes\Local Settings\Software\Microsoft\Windows\Shell\MuiCache'
$ourMui = "$appDir\$name.exe.FriendlyAppName"
$ourMuiVerb = "@$appDir\Menu.dll,-101"
$otherMui = "$appDir-Other\Other.exe.FriendlyAppName" # same prefix, other folder: must stay

$iscc = @(
    (Get-Command iscc -ErrorAction SilentlyContinue).Source
    (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe')
    (Join-Path $env:ProgramFiles 'Inno Setup 6\ISCC.exe')
    (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe')
) | Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1
if (-not $iscc) { throw 'Inno Setup 6 not found.' }

& $iscc /Q (Join-Path $PSScriptRoot 'CleanupTest.iss')
if ($LASTEXITCODE -ne 0) { throw "ISCC failed ($LASTEXITCODE)." }
$setup = Join-Path $root 'build\cleanup-test\CleanupTest-Setup.exe'

$p = Start-Process $setup -ArgumentList '/VERYSILENT', '/SUPPRESSMSGBOXES' -PassThru -Wait
if ($p.ExitCode -ne 0) { throw "Install failed ($($p.ExitCode))." }

# What the app would create.
New-Item -ItemType Directory -Force (Join-Path $dataDir 'Sub') | Out-Null
Set-Content (Join-Path $dataDir 'settings.json') '{}'
New-Item -ItemType Directory -Force $tempDir | Out-Null
Set-Content (Join-Path $tempDir 'Setup.exe') 'x'
New-Item -Path "$userKey\Sub" -Force | Out-Null
New-ItemProperty -Path $userKey -Name HideInNewContextMenu -Value 1 -PropertyType DWord -Force | Out-Null
if (-not (Test-Path $muiKey)) { New-Item -Path $muiKey -Force | Out-Null }
foreach ($value in $ourMui, $ourMuiVerb, $otherMui) { New-ItemProperty -Path $muiKey -Name $value -Value 'x' -Force | Out-Null }

# UninstallLogging only applies when started from Windows Settings: started directly, the log needs /LOG.
$logFile = Join-Path ([IO.Path]::GetTempPath()) "$name-uninstall.log"
if (Test-Path $logFile) { [IO.File]::Delete($logFile) }
$p = Start-Process (Join-Path $appDir 'unins000.exe') -ArgumentList '/VERYSILENT', '/SUPPRESSMSGBOXES', "/LOG=`"$logFile`"" -PassThru -Wait
# The uninstaller starts a copy of itself from the temp folder and exits: wait for that copy to finish.
$deadline = (Get-Date).AddSeconds(60)
while ((Test-Path (Join-Path $appDir 'unins000.exe')) -and (Get-Date) -lt $deadline) { Start-Sleep -Milliseconds 300 }
Start-Sleep -Seconds 1

$mui = Get-ItemProperty $muiKey
$checks = [ordered]@{
    'install folder removed'          = -not (Test-Path $appDir)
    '%LOCALAPPDATA% folder removed'   = -not (Test-Path $dataDir)
    '%TEMP% download folder removed'  = -not (Test-Path $tempDir)
    'HKCU key removed'                = -not (Test-Path $userKey)
    'MuiCache values removed'         = ($null -eq $mui.$ourMui) -and ($null -eq $mui.$ourMuiVerb)
    'other MuiCache value kept'       = $null -ne $mui.$otherMui
}
Remove-ItemProperty -Path $muiKey -Name $otherMui -ErrorAction SilentlyContinue

$failed = 0
foreach ($check in $checks.GetEnumerator()) {
    if ($check.Value) { Write-Host "[PASS] $($check.Key)" -ForegroundColor Green }
    else { Write-Host "[FAIL] $($check.Key)" -ForegroundColor Red; $failed++ }
}

if (Test-Path $logFile) {
    Write-Host "`nUninstall log: $logFile"
    Get-Content $logFile | Select-String 'Cleanup:' | ForEach-Object { Write-Host "  $($_.Line)" }
    [IO.File]::Delete($logFile)
}
else {
    Write-Host '[FAIL] no uninstall log' -ForegroundColor Red
    $failed++
}
exit $failed
