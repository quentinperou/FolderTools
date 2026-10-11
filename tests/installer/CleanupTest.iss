; Test installer for installer\UserDataCleanup.iss, without administrator rights (PrivilegesRequired=lowest):
; built, installed and uninstalled in /VERYSILENT by tests\installer\Test-UninstallCleanup.ps1. It uses its own
; names, so it never touches the real app's settings.

#define UserDataName "FolderToolsCleanupTest"
#define UpdateTempName "FolderToolsCleanupTest-Update"

[Setup]
AppId=FolderToolsCleanupTest
AppName=FolderTools cleanup test
AppVersion=1.0
DefaultDirName={localappdata}\Programs\FolderToolsCleanupTest
PrivilegesRequired=lowest
DisableProgramGroupPage=yes
OutputDir=..\..\build\cleanup-test
OutputBaseFilename=CleanupTest-Setup
UninstallLogging=yes
Uninstallable=yes
CreateUninstallRegKey=yes

[Files]
Source: "CleanupTest.iss"; DestDir: "{app}"

[Code]
#include "..\..\installer\UserDataCleanup.iss"

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usPostUninstall then
    DeleteUserData(ExpandConstant('{app}'));
end;
