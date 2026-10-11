# CLAUDE.md

Guidance for Claude Code and contributors working on this repository. `README.md` (English) and
`README.fr.md` (French) describe the project for users.

## The project

**FolderTools**: a small open-source (MIT) Windows 11 app, started from the File Explorer context menu
(“FolderTools ›” on folders, folder backgrounds and drives), with two tools:

- **Folder sizes**: the folder as a tree (size, files, subfolders, share of the parent, newest file), sortable,
  with a row menu (open, copy path, show only this folder, delete to the recycle bin) and a CSV export;
- **Delete empty folders**: finds them, lists them checked, deletes the checked ones (recycle bin or for good),
  then reports what could not be deleted. Main use case: cleaning the Windows temp folder.

UI in English and French, light/dark/system theme. The name “FolderTools” is never translated. Author:
[QuentinPerou](https://github.com/quentinperou). Repository: https://github.com/quentinperou/FolderTools (to be
created). Sister projects with the same stack and conventions: CropPdf (`F:\DEV\crop-pdf`) and PageCompose. Credits
wording chosen by the author (bottom of the READMEs): “Developed by QuentinPerou, with Claude Code (Anthropic)”.

## Project status (October 11, 2026)

**Version 1.0.0, not released.** Scope decided with the author on October 11, 2026 (framing questions):
sub-menu “FolderTools ›” (not two top-level entries); on folders, folder backgrounds and drives; expandable tree for
sizes; real size (sum of file lengths, not size on disk); columns subfolders + % + newest file; row menu; CSV export;
empty folders recursive, ignored files configurable (default `Thumbs.db`, `desktop.ini`, `.DS_Store`), recycle bin
by default or deletion for good (setting), preview with checkboxes then confirmation, the starting folder never
deleted; “Run as administrator” button when folders are inaccessible; a Settings toggle to hide the entry from the
new Windows 11 menu. **Folder colors** (macOS-like tags, “FolderTools › Color ›”, through `desktop.ini`
`IconResource`) are planned for **v1.1**.

Done and checked on this PC (Windows 11 build 26200):
- 87 unit tests pass (scanner, empty folders, CSV, sizes, command line, sensitive locations, updates, localization).
- Full build (`scripts\build.ps1`): app, DLL, sparse package, installer `dist\FolderTools-Setup-1.0.0.exe`.
- Windows captured with `PrintWindow`: home, size tree (F:\DEV, then the whole C:\ drive: 375 GB, 1.3 million
  files, 49.6 s, ~230 MB of memory, “607 folders could not be read” bar with the admin button), cleaning window
  (search, recycle-bin deletion of a test tree through UI Automation, report), settings, light and dark themes.

- Installed twice on this PC (UAC accepted by the author): package `Status : Ok`; `tools\InvokeMenu` (the menu
  called like File Explorer does) shows “FolderTools” with sub-entries “Taille des dossiers” / “Supprimer les
  dossiers vides” and their icons, and the started `FolderTools.exe size "<folder>"` has `explorer.exe` as parent.
- Afterwards (not installed yet: two installer runs ended with exit code 2, UAC prompt not accepted): the settings
  window rebuilt with a page menu like PageCompose (General, Empty folders, Updates, About), the filled-folder app
  icon (variant “A” chosen by the author among four), the “Select all” check box of the cleaning window, the
  `desktop6:*WriteVirtualization` lines of the manifest.

Not verified yet:
- **The new-menu toggle** (`HideInNewContextMenu`). My tests were inconclusive: the commands run by Claude Code are
  sandboxed and their registry/file writes are invisible to other processes (see “Working on the project”). Test it
  for real: installed app → Settings → General → toggle off → right-click a folder;
- in File Explorer itself: the new and classic menus, starting from the background of a folder (site fallback in the
  DLL) and from a drive (classic menu only: the manifest refuses `Drive`, see “Known pitfalls”);
- “Run as administrator” relaunch (UAC), deletion from the size tree (recycle bin with Windows dialogs), CSV opened
  in Excel, permanent deletion mode, language switch rebuilding open windows;
- English Windows, Windows 10;
- the update path (no release exists yet).

Next steps: install `dist\FolderTools-Setup-1.0.0.exe` and check the menu; create the GitHub repository (author's
go-ahead needed for `git init` / push); README screenshots (`.github/screenshot.png`, `.github/screenshot.fr.png`,
on a neutral folder: no personal folder names); release v1.0.0; then v1.1 (folder colors).

## Commands

```powershell
.\scripts\build.ps1                       # everything: tests, app, DLL, MSIX package, installer -> dist\
.\scripts\build.ps1 -SkipTests -NoInstaller
dotnet test tests\FolderTools.Tests       # unit tests (fast, no UI)
dotnet build src\FolderTools              # build the app only
dotnet run --project src\FolderTools -- size "D:\Photos"
dotnet run --project src\FolderTools -- clean "%TEMP%"
```

- `FOLDERTOOLS_THEME=Light|Dark` and `FOLDERTOOLS_LANG=en|fr` force the theme / UI language at startup.
- User settings: `%LOCALAPPDATA%\FolderTools\settings.json`; the new-menu toggle: `HKCU\Software\FolderTools`,
  DWORD `HideInNewContextMenu` (read by the DLL each time the menu opens).
- The context menu can only be tested installed (`dist\FolderTools-Setup-<version>.exe`, UAC prompt).
- `build.ps1` enters the Visual Studio environment itself (MSVC, CMake, Ninja, makeappx) through vswhere.
- Inno Setup writes the preprocessed script to `build\FolderTools.preprocessed.iss` (to check the registry lines
  generated by the `MenuEntry` macro).
- `.\scripts\build.ps1 -UninstallLog` builds an installer whose uninstaller always logs (`UninstallLogging=yes`,
  “Uninstall Log <date>.txt” in %TEMP%) when started from Windows Settings.
- `.\scripts\uninstall-inventory.ps1`: read-only list, `[x]` present / `[ ]` absent, of everything the app and the
  installer create, plus `PendingFileRenameOperations` and the newest uninstall log. Elevated, it also lists the
  package for every account.
- `.\tests\installer\Test-UninstallCleanup.ps1`: checks `installer\UserDataCleanup.iss` without UAC (a per-user
  test installer with its own names, installed and uninstalled in `/VERYSILENT`, uninstall log through `/LOG`).

## Uninstall

What exists after an install and use, and who removes it:

| Item | Created by | Removed by |
|------|------------|------------|
| `{app}` = `C:\Program Files\FolderTools` | installer `[Files]` | Inno; the DLL may be pending until restart (`uninsrestartdelete`) |
| Start menu shortcut (common programs) | `[Icons]` | Inno |
| `HKLM\SOFTWARE\Classes\{Directory,Directory\Background,Drive}\shell\FolderTools` | `[Registry]` | Inno (`uninsdeletekey`) |
| Uninstall entry `{19F0E0B9-…}_is1` | Inno | Inno |
| Package `FolderTools.ContextMenu` (+ `%LOCALAPPDATA%\Packages\FolderTools.ContextMenu_*`) | `[Code]` `Add-AppxPackage` | `[Code]` at `usUninstall`, **all accounts**; leftover data folder at `usPostUninstall` |
| `%LOCALAPPDATA%\FolderTools\settings.json` | app (`AppSettings`) | `[Code]` `DeleteUserData` at `usPostUninstall` |
| `HKCU\Software\FolderTools` (`HideInNewContextMenu`) | app (`AppSettings.ShowInNewContextMenu`) | same |
| `%TEMP%\FolderTools-Update` | app (`Updater`; also deleted at the next app start) | same |
| `MuiCache` values under `HKCU\…\Shell\MuiCache` pointing into `{app}` | Windows | same (only values with the `{app}\` prefix) |

Not created: `%APPDATA%`, startup entries, scheduled tasks, file associations. Kept on purpose: other accounts'
settings (in admin mode `{localappdata}`/HKCU are the UAC account's: documented, not forced), the .NET runtime,
the user's CSV exports. An update runs the installer, not the uninstaller: settings stay. The user-data cleanup lives
in `installer\UserDataCleanup.iss` (included in `[Code]`, with `Log()` lines prefixed `Cleanup:`), shared with the
test installer. Verified: `Test-UninstallCleanup.ps1` passes (6 checks); the real uninstall from Windows Settings
followed by `uninstall-inventory.ps1` is still to be done by the author (needs UAC).

## Working on the project

- The app may be in use on screen while work is in progress: drive it through UI Automation or posted window
  messages rather than sending input to the screen. A scratch PowerShell script with `PrintWindow`
  (`PW_RENDERFULLCONTENT`, DPI-aware process) captures a window even when it is covered.
- Docs (both READMEs, this file) are expected to be kept up to date at the end of each work session, including
  the “Project status” section above.
- Personal or machine-specific notes go in `CLAUDE.local.md` (git-ignored), not in this file.
- Never test deletion on real folders: use a test tree in the temp folder (`TempTree` in the tests does that).
- **Claude Code's sandbox**: registry and file writes made from its shell (and by the processes it starts) are not
  seen by processes started outside it (File Explorer, the menu's `dllhost`, the installed app started by
  Explorer), even with the sandbox flag turned off. A setting written that way does not reach the menu DLL: test
  such settings through the installed app, or ask the author to check.
- `tools\InvokeMenu` (dev tool, not in the solution) calls the **installed** menu like File Explorer does:
  `dotnet run --project tools\InvokeMenu -- <folder> [size|clean]` prints the entry, its flags, the sub-entries
  (title, icon, state) and invokes the chosen one.

## Architecture

| Folder | Role |
|--------|------|
| `src/FolderTools.Core` | Pure, tested logic, no UI text: `FolderScanner` + `FolderNode` (size tree), `EmptyFolderCleaner` (find, re-check, delete), `CsvExport`, `SizeFormat`, `LaunchRequest` (command line), `SensitiveLocations`, `SemanticVersion`, `UpdateFeed` (GitHub Releases). |
| `src/FolderTools` | WPF .NET 10. `App` (startup by command line, theme, language, `OpenSettings` + `IRebuildable`), `HomeWindow`, `SizeWindow` + `SizeRow` (flat list showing the tree), `CleanWindow` + `CleanItem`, `SettingsWindow` + `AppSettings`, `WindowsShell` (recycle bin, Explorer, elevation, size/count formatting), `Updater`, `Localization.cs` (`Strings`, `{local:Tr}`), `Properties/Strings*.resx`. |
| `src/FolderTools.Shell` | C++ DLL (WRL): `FolderToolsCommand` (“FolderTools ›”, `ECF_HASSUBCOMMANDS`) and two `SubCommand`s that have File Explorer start `FolderTools.exe size|clean "<folder>"`. Labels in `FolderToolsMenu.rc`, sub-entry icons `SizeMenu.ico` / `CleanMenu.ico` (resources 201/202). |
| `package/` | `AppxManifest.xml` of the “sparse” package + logos. Packed by `makeappx` in `build.ps1`. |
| `installer/FolderTools.iss` | Inno Setup installer (English/French): copies to Program Files, registers the package, adds the classic cascading menu on `Directory`, `Directory\Background` and `Drive`. |
| `scripts/` | `build.ps1`, `make-icons.ps1` (app icon, menu icons, package logos; generated files are committed). |
| `tools/InvokeMenu` | Dev tool: runs the installed menu commands like File Explorer. Not in the solution. |
| `.github/workflows/build.yml` | CI: build + tests on every push; on a `v*` tag, publishes the installer in a Release (pre-release when the tag has a `-`). |

### Technical choices (and why)

- Same base as CropPdf (see its CLAUDE.md for the details and history): **unsigned sparse package** with the OID
  publisher, registered by the installer with `Add-AppxPackage -ExternalLocation -AllowUnsigned` (admin);
  **classic entries** as well; **WPF + Fluent** `ThemeMode`; framework-dependent app; DLL with the static CRT;
  updates from GitHub Releases (Stable/Beta channels, SHA-256 check, silent install, `/RELAUNCH=1`).
- **One process per right-click**: the DLL starts `FolderTools.exe size|clean "<folder>"` (up to 8 selected
  folders). `LaunchRequest` parses it; a drive root arrives as `C:"` (`\"` is an escaped quote on the command line)
  and is repaired. Without arguments: the home window, which is also the only place that checks for updates.
- **Scanner**: `FileSystemEnumerable` (name, size, date, attributes in one pass), `AttributesToSkip = 0` (hidden
  and system files count), errors kept (`IgnoreInaccessible = false`) and counted per folder. The first 3 levels
  are scanned with `Parallel.ForEach`. A node's `Children` array is assigned once, before its subfolders are
  scanned, so the UI can list them during the scan; totals are final once `IsComplete`. Full paths are built
  on demand (memory on a whole drive). Reparse points: only **junctions and symbolic links** (`LinkTarget` not
  null) are skipped; OneDrive/cloud folders are reparse points too and must be read.
- **Size window**: a `ListView` + `GridView` showing a flattened tree (`SizeRow`, indentation by level), virtualized.
  A 300 ms timer refreshes the rows during the scan; `Reconcile` moves existing rows instead of rebuilding the list
  (selection and scroll stay); sorting or changing the top folder resets the list. The files directly in a folder
  that also has subfolders get a “[n files in this folder]” row. The analyzed folder's row shows the bytes counted
  so far. Shares are hidden until the parent's total is known.
- **Empty folders**: the outermost empty folders are listed, with their nested count. Deletion re-checks each folder
  first. *For good*: only ignored files and empty folders are removed, with non-recursive `RemoveDirectory`, so a
  file created in the meantime stops the deletion. *Recycle bin*: `SHFileOperation` in batches of 64, silent, on an
  STA thread; success is read afterwards with `Directory.Exists`. Junctions/symlinks count as content (never
  removed). The starting folder is never deleted. A warning bar shows for sensitive locations.
- **Deleting from the size tree**: `SHFileOperation` with File Explorer's own progress/error dialogs and
  `FOF_WANTNUKEWARNING` (asks before a permanent deletion on a drive without recycle bin), then `FolderNode.Detach`
  updates the totals of every parent without rescanning.
- **Run as administrator**: `runas` relaunch with the same `LaunchRequest`; the window closes if UAC was accepted.
- **New-menu toggle**: a DWORD in HKCU read by the DLL in `GetState` (`ECS_HIDDEN`): no admin rights, no
  re-registration, effective at the next right-click. The classic entry always stays. The manifest turns registry
  and file-system write virtualization off (`desktop6:RegistryWriteVirtualization` / `FileSystemWriteVirtualization`,
  allowed by `unvirtualizedResources`) so that the packaged DLL reads the same HKCU as the unpackaged app.
- **Settings window**: a page menu on the left (`ListBox`, `MenuItem` style with an accent bar), pages stacked in one
  grid cell and hidden (not collapsed) so the window keeps the height of the tallest page. `App.OpenSettings(owner,
  SettingsPage)` opens a given page (the cleaning window's “Change” link opens *Empty folders*). Toggles use
  `Checked`/`Unchecked`, not `Click`.
- **Numbers and dates** use `CultureInfo.CurrentCulture` (regional settings, like Explorer), texts the UI language.
  The CSV uses the culture's list separator and decimal mark, UTF-8 with BOM (Excel).
- **Folder color**: `FolderBrush` (`#E9A21E`) is the only hard-coded color, the app's identity, readable in both themes.

### Localization

- App texts: `Properties/Strings.resx` (English, neutral) and `Strings.fr.resx`. XAML uses `{local:Tr Key}`, code
  uses `Strings.Get("Key")` / `Strings.Format("Key", ...)` / `Strings.Plural("Key", count, ...)` (keys `Key_One` /
  `Key_Other`; French uses the singular for 0 and 1). **Never hard-code a UI text.**
- Texts are read when a window is built: a language change rebuilds every open window (`IRebuildable`, same
  folder, so the size window scans again; a cleaning window busy deleting keeps its language) and reopens Settings.
- `LocalizationTests` checks both files have the same keys and placeholders, and that every key used in
  `src/FolderTools/*.cs|*.xaml` exists (patterns: `{local:Tr X}`, `Strings.Get("X")`, `Strings.Format("X"`,
  `"ConfirmX"`, and `Strings.Plural(... "X" ...` → `X_One` + `X_Other`). Keys mentioned in comments count too.
- Context menu labels: string tables in `FolderToolsMenu.rc` (English neutral, en-US, French neutral; **never
  `LANG_NEUTRAL`** for strings). 101 FolderTools, 102 Folder sizes, 103 Delete empty folders. They follow the Windows
  display language, not the app setting. The classic entries use `MUIVerb = @<app>\FolderToolsMenu.dll,-10x`.
- Installer: `[Languages]` english/french + `[CustomMessages]`, `ShowLanguageDialog=auto`.

## Conventions

- **Code, comments, docs, scripts and commit messages in English.** User-facing texts go through the resources
  (English + French). Keep both READMEs in sync.
- Commit messages **without** a “Co-Authored-By: Claude” trailer (author's choice; the README credits line is enough).
- `.ps1` and `.iss` files are **UTF-8 with BOM** and CRLF (Windows PowerShell 5.1 and Inno Setup misread BOM-less
  UTF-8). Scripts must stay compatible with Windows PowerShell 5.1.
- UI colors: Fluent theme resources through `DynamicResource`, except `FolderBrush`.
- Icon-only buttons get an `AutomationProperties.Name`.
- A single version in `Directory.Build.props` (app + installer). The **package** version (`AppxManifest.xml`) only
  changes when the manifest changes.
- The README's **Privacy policy** lists every network access of the app (today: update check from the home window
  and Settings, update download, links on click): keep it in step with the code.

## Known pitfalls

- **CLSID `8119219C-28DF-42B9-B5B3-9B2904535DDC`**: in `dllmain.cpp` *and* `AppxManifest.xml` (three times). The
  package name `FolderTools.ContextMenu`: in `AppxManifest.xml` *and* `FolderTools.iss`.
- `desktop5:ItemType Type` only accepts `*`, `.ext`, `Directory` and `Directory\Background` (makeappx schema error
  for `Drive`): drives only have the classic entry (`Drive\shell`).
- WPF Fluent: a `ListView` with a `GridView` needs its `ItemContainerStyle` based on
  `GridView.GridViewItemContainerStyleKey`; based on `{x:Type ListViewItem}`, the cells are not used (rows show
  `ToString()`).
- A WPF `Image` showing the `.ico` takes a small frame and blows it up (blurry icon in the home window and About):
  inside the app, use `Assets/FolderTools.png` (256 px, generated by `make-icons.ps1`) with
  `RenderOptions.BitmapScalingMode="HighQuality"`. Window title bars keep the `.ico`.
- `IsChecked` set through UI Automation (`TogglePattern`) raises no `Click`: the cleaning window listens to the
  items' `PropertyChanged` instead.
- Inno Setup `[Code]`: comments are `//` or `{ }`, never `;` (an included `.iss` starting with `;` lines fails with
  “'BEGIN' expected”). `UninstallLogging=yes` only logs when the uninstaller is started from Windows Settings
  (started directly, pass `/LOG=<file>`).
- Inno Setup: `{#Const}` is not expanded inside an ISPP string literal (the `MenuEntry` macro concatenates
  `MenuDll`/`AppExe` instead). A line starting with `[` is a section header, even inside `[Code]`. Paths reach
  PowerShell through environment variables, never inside the `-Command` text.
- **Starting the app from the DLL**: through File Explorer (`IShellDispatch2::ShellExecute`), not `CreateProcess`
  from `dllhost` (the app would inherit the package identity: blank taskbar tile). See CropPdf's CLAUDE.md.
- `exdisp.h` does not compile with `WIN32_LEAN_AND_MEAN`; `rc.exe` needs a trailing newline at the end of `resource.h`.
- File Explorer may keep the DLL loaded during an update: the installer then replaces it at the next restart.
- `dotnet run file.cs` in a folder without `nuget.config` may fail to restore (`NU1100`): copy the repo's
  `nuget.config` next to it and add `#:property PublishAot=false`.

## Debugging the context menu

- Is the package registered? `Get-AppxPackage FolderTools.ContextMenu` must show `Status : Ok`.
- The DLL is only updated by reinstalling. If File Explorer keeps it loaded, restart File Explorer.
- Identity of the started app: `GetPackageFullName` on `FolderTools.exe` must fail (error 15700) and its parent
  must be `explorer.exe`.
- The entry hidden on purpose? `HKCU\Software\FolderTools\HideInNewContextMenu` = 1.

## To do

- v1.1 **folder colors**: “FolderTools › Color ›” sub-menu with colored dots; writes `desktop.ini`
  (`[.ShellClassInfo] IconResource=<ico>,0`), sets the folder `ReadOnly`/`System` attribute and the file
  `Hidden`/`System`, then `SHChangeNotify(SHCNE_UPDATEITEM)`; colored folder icons generated by `make-icons.ps1`;
  decide where the icons live (install folder: lost on uninstall; copied into the folder: survives on a USB drive).
- README screenshots; signing through SignPath Foundation (see CropPdf); ARM64; more languages.
