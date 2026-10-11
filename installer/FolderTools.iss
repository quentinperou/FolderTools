; FolderTools installer (Inno Setup 6), in English and French (picked from the Windows display language).
; Built by scripts\build.ps1, which prepares dist\ first:
;   ISCC.exe /DAppVersion=1.0.0 installer\FolderTools.iss
; The app also runs it to install an update (src\FolderTools\Updater.cs): silent, closing the running app, /RELAUNCH=1.

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif

; Windows file versions are numbers only: 1.1.0 for a 1.1.0-beta.1 pre-release.
#if Pos("-", AppVersion) > 0
  #define AppFileVersion Copy(AppVersion, 1, Pos("-", AppVersion) - 1)
#else
  #define AppFileVersion AppVersion
#endif

#define AppName "FolderTools"
#define AppExe "FolderTools.exe"
#define MenuDll "FolderToolsMenu.dll"
#define PackageName "FolderTools.ContextMenu"

[Setup]
AppId={{19F0E0B9-1859-4642-894A-648B58719233}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher=QuentinPerou
AppPublisherURL=https://github.com/quentinperou
AppSupportURL=https://github.com/quentinperou/FolderTools
AppUpdatesURL=https://github.com/quentinperou/FolderTools/releases
VersionInfoVersion={#AppFileVersion}
DefaultDirName={autopf}\FolderTools
DisableProgramGroupPage=yes
; Admin rights: needed to register the unsigned context-menu package (Add-AppxPackage -AllowUnsigned).
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.19041
OutputDir=..\dist
OutputBaseFilename=FolderTools-Setup-{#AppVersion}
SetupIconFile=..\src\FolderTools\Assets\FolderTools.ico
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName={#AppName}
WizardStyle=modern
Compression=lzma2/max
SolidCompression=yes
CloseApplications=yes
RestartApplications=no
; Only ask for the language when Windows uses one the installer does not have.
ShowLanguageDialog=auto
; The uninstaller removes the settings of the account that uninstalls (below): expected, not a mistake.
UsedUserAreasWarning=no

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"
Name: "french"; MessagesFile: "compiler:Languages\French.isl"

[CustomMessages]
english.MenuSize=Folder sizes
french.MenuSize=Taille des dossiers
english.MenuClean=Delete empty folders
french.MenuClean=Supprimer les dossiers vides
english.MenuNotRegistered=The "FolderTools" entry could not be added to the new Windows 11 context menu.%nIt is still available under "Show more options".
french.MenuNotRegistered=L'entrée « FolderTools » n'a pas pu être ajoutée au nouveau menu clic droit de Windows 11.%nElle reste disponible via « Afficher plus d'options ».
english.RuntimeMissing=FolderTools needs the Microsoft ".NET 10 Desktop Runtime", which is not installed on this PC.%nInstall it now?
french.RuntimeMissing=FolderTools a besoin du « .NET 10 Desktop Runtime » (Microsoft), absent de ce PC.%nL'installer maintenant ?
english.RuntimeInstalling=Installing the .NET 10 Desktop Runtime...
french.RuntimeInstalling=Installation du .NET 10 Desktop Runtime…
english.RuntimeFailed=The automatic installation failed. The download page will open:%nchoose ".NET Desktop Runtime 10" (x64).
french.RuntimeFailed=L'installation automatique a échoué. La page de téléchargement va s'ouvrir :%nchoisissez « .NET Desktop Runtime 10 » (x64).

[Files]
Source: "..\dist\app\*"; DestDir: "{app}"; Excludes: "{#MenuDll}"; Flags: ignoreversion recursesubdirs createallsubdirs
; Explorer may still have the DLL loaded during an update: it is then replaced at the next restart.
Source: "..\dist\app\{#MenuDll}"; DestDir: "{app}"; Flags: ignoreversion restartreplace uninsrestartdelete
Source: "..\dist\FolderTools.Menu.msix"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
; The app's name, not translated.
Name: "{autoprograms}\FolderTools"; Filename: "{app}\{#AppExe}"

[Registry]
; Classic menu entry "FolderTools >" with two sub-entries, on folders, on the background of an open folder and on
; drives: the whole menu on Windows 10, "Show more options" on Windows 11. MUIVerb points at the localized string
; table of the DLL (101 FolderTools, 102 Folder sizes, 103 Delete empty folders), so the labels follow each user's
; display language; the default values are a fallback in the installer's language. The sub-entries are listed in
; the order of their key names, hence "1Size" and "2Clean".
#define MenuEntry(Type) \
  'Root: HKLM; Subkey: "Software\Classes\' + Type + '\shell\FolderTools"; ValueType: string; ValueName: "MUIVerb"; ValueData: "@{app}\' + MenuDll + ',-101"; Flags: uninsdeletekey' + NewLine + \
  'Root: HKLM; Subkey: "Software\Classes\' + Type + '\shell\FolderTools"; ValueType: string; ValueName: "Icon"; ValueData: """{app}\' + AppExe + '"",0"' + NewLine + \
  'Root: HKLM; Subkey: "Software\Classes\' + Type + '\shell\FolderTools"; ValueType: string; ValueName: "SubCommands"; ValueData: ""' + NewLine + \
  'Root: HKLM; Subkey: "Software\Classes\' + Type + '\shell\FolderTools\shell\1Size"; ValueType: string; ValueName: ""; ValueData: "{cm:MenuSize}"' + NewLine + \
  'Root: HKLM; Subkey: "Software\Classes\' + Type + '\shell\FolderTools\shell\1Size"; ValueType: string; ValueName: "MUIVerb"; ValueData: "@{app}\' + MenuDll + ',-102"' + NewLine + \
  'Root: HKLM; Subkey: "Software\Classes\' + Type + '\shell\FolderTools\shell\1Size"; ValueType: string; ValueName: "Icon"; ValueData: """{app}\' + MenuDll + '"",-201"' + NewLine + \
  'Root: HKLM; Subkey: "Software\Classes\' + Type + '\shell\FolderTools\shell\1Size\command"; ValueType: string; ValueName: ""; ValueData: """{app}\' + AppExe + '"" size ""%V"""' + NewLine + \
  'Root: HKLM; Subkey: "Software\Classes\' + Type + '\shell\FolderTools\shell\2Clean"; ValueType: string; ValueName: ""; ValueData: "{cm:MenuClean}"' + NewLine + \
  'Root: HKLM; Subkey: "Software\Classes\' + Type + '\shell\FolderTools\shell\2Clean"; ValueType: string; ValueName: "MUIVerb"; ValueData: "@{app}\' + MenuDll + ',-103"' + NewLine + \
  'Root: HKLM; Subkey: "Software\Classes\' + Type + '\shell\FolderTools\shell\2Clean"; ValueType: string; ValueName: "Icon"; ValueData: """{app}\' + MenuDll + '"",-202"' + NewLine + \
  'Root: HKLM; Subkey: "Software\Classes\' + Type + '\shell\FolderTools\shell\2Clean\command"; ValueType: string; ValueName: ""; ValueData: """{app}\' + AppExe + '"" clean ""%V"""' + NewLine
{#MenuEntry("Directory")}
{#MenuEntry("Directory\Background")}
{#MenuEntry("Drive")}
; The preprocessed script, to check the lines generated above: build\FolderTools.preprocessed.iss
#expr SaveToFile(AddBackslash(SourcePath) + "..\build\FolderTools.preprocessed.iss")

; The setting read by the menu DLL (Windows 11 menu shown or hidden), removed on uninstall only.
Root: HKCU; Subkey: "Software\FolderTools"; Flags: uninsdeletekey dontcreatekey

[UninstallDelete]
; Settings of the app (settings.json).
Type: filesandordirs; Name: "{localappdata}\FolderTools"

[Run]
; Update installed from the app (silent, /RELAUNCH=1): start the app again, as the user, not elevated.
Filename: "{app}\{#AppExe}"; Flags: nowait runasoriginaluser skipifnotsilent; Check: RelaunchRequested

[Code]
{ Set by the app when it installs an update (Updater.InstallerArguments). }
function RelaunchRequested: Boolean;
begin
  Result := ExpandConstant('{param:RELAUNCH|0}') = '1';
end;

function IsWindows11: Boolean;
var
  Version: TWindowsVersion;
begin
  GetWindowsVersionEx(Version);
  Result := (Version.Major > 10) or ((Version.Major = 10) and (Version.Build >= 22000));
end;

function RunPowerShell(const Command: String): Boolean;
var
  ResultCode: Integer;
begin
  Result := Exec(ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe'),
    '-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command "' + Command + '"',
    '', SW_HIDE, ewWaitUntilTerminated, ResultCode) and (ResultCode = 0);
end;

{ Windows 11 new context menu: sparse package pointing at the install folder. }
procedure UnregisterContextMenu;
begin
  RunPowerShell('Get-AppxPackage -Name {#PackageName} | Remove-AppxPackage');
end;

function SetEnvironmentVariable(Name, Value: String): BOOL;
  external 'SetEnvironmentVariableW@kernel32.dll stdcall';

procedure RegisterContextMenu;
begin
  { The paths reach PowerShell through environment variables (inherited by the child process), not inside the
    command text: a quote in the install folder (L'appli, or the typographic ’ that PowerShell also treats as a
    quote) would end a quoted string and break the command. }
  SetEnvironmentVariable('FOLDERTOOLS_MSIX', ExpandConstant('{app}\FolderTools.Menu.msix'));
  SetEnvironmentVariable('FOLDERTOOLS_LOCATION', ExpandConstant('{app}'));
  if not RunPowerShell('Add-AppxPackage -Path $env:FOLDERTOOLS_MSIX -ExternalLocation $env:FOLDERTOOLS_LOCATION -AllowUnsigned') then
    MsgBox(CustomMessage('MenuNotRegistered'), mbInformation, MB_OK);
end;

{ The app is framework-dependent: it needs the .NET 10 Desktop Runtime. }
function IsDesktopRuntimeInstalled: Boolean;
var
  FindRec: TFindRec;
begin
  Result := FindFirst(ExpandConstant('{commonpf64}\dotnet\shared\Microsoft.WindowsDesktop.App\10.*'), FindRec);
  if Result then
    FindClose(FindRec);
end;

procedure EnsureDesktopRuntime;
var
  ResultCode: Integer;
begin
  if IsDesktopRuntimeInstalled then
    Exit;
  if MsgBox(CustomMessage('RuntimeMissing'), mbConfirmation, MB_YESNO) <> IDYES then
    Exit;
  WizardForm.StatusLabel.Caption := CustomMessage('RuntimeInstalling');
  if not Exec('winget.exe', 'install --id Microsoft.DotNet.DesktopRuntime.10 -e --silent --accept-source-agreements --accept-package-agreements',
    '', SW_HIDE, ewWaitUntilTerminated, ResultCode) or not IsDesktopRuntimeInstalled then
  begin
    MsgBox(CustomMessage('RuntimeFailed'), mbInformation, MB_OK);
    ShellExec('open', 'https://dotnet.microsoft.com/download/dotnet/10.0', '', '', SW_SHOWNORMAL, ewNoWait, ResultCode);
  end;
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssInstall then
  begin
    { Update: drop the previous registration before the files are replaced. }
    if IsWindows11 then
      UnregisterContextMenu;
  end
  else if CurStep = ssPostInstall then
  begin
    EnsureDesktopRuntime;
    if IsWindows11 then
      RegisterContextMenu;
  end;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if (CurUninstallStep = usUninstall) and IsWindows11 then
    UnregisterContextMenu;
end;
