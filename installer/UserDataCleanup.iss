// Removes, at uninstall, what the app creates by itself for the user (the installer's own files, shortcut and
// HKLM keys are removed by Inno Setup). Included in the [Code] section of FolderTools.iss, and of
// tests\installer\CleanupTest.iss, which checks it without administrator rights.
//
// Expects the ISPP defines:
//   UserDataName    folder %LOCALAPPDATA%\<name> (settings.json) and key HKCU\Software\<name> (menu toggle)
//   UpdateTempName  folder %TEMP%\<name>, where the app downloads an update installer
//   PackageName, ColorPackageName  (optional) sparse packages, whose %LOCALAPPDATA%\Packages\<name>_* folder Windows
//                   may leave
//
// Run as administrator, {localappdata}, the temp folder and HKCU are those of the account that accepted the UAC
// prompt: other Windows accounts keep their settings (out of reach, on purpose).
// Careful: a line starting with "[" is read as a new section, even indented here: never split a Format(..., [...]).

procedure CleanupDeleteFolder(const Path: String);
begin
  if not DirExists(Path) then
    Log('Cleanup: not present: ' + Path)
  else if DelTree(Path, True, True, True) then
    Log('Cleanup: deleted ' + Path)
  else
    Log('Cleanup: could not delete ' + Path);
end;

procedure CleanupDeleteUserKey(const Key: String);
begin
  if not RegKeyExists(HKCU, Key) then
    Log('Cleanup: not present: HKCU\' + Key)
  else if RegDeleteKeyIncludingSubkeys(HKCU, Key) then
    Log('Cleanup: deleted HKCU\' + Key)
  else
    Log('Cleanup: could not delete HKCU\' + Key);
end;

{ Windows caches the names of the programs it has started and of the menu labels it has shown (MUIVerb), one value
  per path: "<app>\FolderTools.exe.FriendlyAppName", "@<app>\FolderToolsMenu.dll,-101"... Only the values pointing
  into the install folder are removed. }
procedure CleanupDeleteMuiCache(const AppDir: String);
var
  Key, Prefix: String;
  Names: TArrayOfString;
  I, Count: Integer;
begin
  Key := 'Software\Classes\Local Settings\Software\Microsoft\Windows\Shell\MuiCache';
  Prefix := Lowercase(AddBackslash(AppDir));
  Count := 0;
  if RegGetValueNames(HKCU, Key, Names) then
    for I := 0 to GetArrayLength(Names) - 1 do
      if Pos(Prefix, Lowercase(Names[I])) > 0 then
        if RegDeleteValue(HKCU, Key, Names[I]) then
        begin
          Log('Cleanup: deleted MuiCache value ' + Names[I]);
          Count := Count + 1;
        end;
  if Count = 0 then
    Log('Cleanup: no MuiCache value for ' + AppDir);
end;

#ifdef PackageName
{ Remove-AppxPackage normally deletes the package's data folder; if Windows left it, it goes now. }
procedure CleanupDeletePackageData(const Name: String);
var
  FindRec: TFindRec;
  Root: String;
  Found: Boolean;
begin
  Root := ExpandConstant('{localappdata}\Packages\');
  Found := False;
  if FindFirst(Root + Name + '_*', FindRec) then
  begin
    repeat
      if (FindRec.Attributes and FILE_ATTRIBUTE_DIRECTORY) <> 0 then
      begin
        CleanupDeleteFolder(Root + FindRec.Name);
        Found := True;
      end;
    until not FindNext(FindRec);
    FindClose(FindRec);
  end;
  if not Found then
    Log('Cleanup: no data folder left for the package ' + Name);
end;
#endif

{ Called at usPostUninstall. An update installed over the app does not run the uninstaller: settings stay. }
procedure DeleteUserData(const AppDir: String);
begin
  Log('Cleanup: removing the data of the app for ' + GetUserNameString);
  CleanupDeleteFolder(ExpandConstant('{localappdata}\{#UserDataName}'));
  CleanupDeleteFolder(AddBackslash(GetTempDir) + '{#UpdateTempName}');
  CleanupDeleteUserKey('Software\{#UserDataName}');
  CleanupDeleteMuiCache(AppDir);
#ifdef PackageName
  CleanupDeletePackageData('{#PackageName}');
#endif
#ifdef ColorPackageName
  CleanupDeletePackageData('{#ColorPackageName}');
#endif
end;
