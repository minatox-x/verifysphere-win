; =============================================================================
; VerifySphere - Windows Installer (Inno Setup script)
; =============================================================================
; Produces a single verifysphere-setup.exe that:
;   - Installs VerifySphere to a stable per-user location
;     (%LocalAppData%\Programs\VerifySphere) - no admin/UAC prompt required
;   - Adds Start Menu shortcut + optional Desktop shortcut
;   - Registers the verifysphere:// URI scheme under HKCU\Software\Classes,
;     pointing at the installed copy of VerifySphere.exe - this is the same
;     per-user, no-admin registration approach used by install_uri_scheme.ps1,
;     just performed automatically during setup instead of as a manual step
;   - Appears in Settings > Apps > Installed apps with a working Uninstall
;     button, which also removes the URI scheme registration
;
; Because the app now always lives at the same installer-managed path, users
; never manually move the .exe around - which is what broke the URI scheme
; registration before. Reinstalling/updating simply overwrites in place.
;
; Build with the Inno Setup Compiler (ISCC.exe):
;   ISCC.exe /DMyAppVersion="1.0.0" setup.iss
; MyAppVersion defaults to 1.0.0 if not passed (see #ifndef block below).
; =============================================================================

#define MyAppName "VerifySphere"
#ifndef MyAppVersion
  #define MyAppVersion "1.0.0"
#endif
#define MyAppPublisher "VerifySphere"
#define MyAppExeName "VerifySphere.exe"
#define MyAppURLScheme "verifysphere"
; Fixed GUID for this application - do not change between releases, it is
; what lets Windows treat upgrades as the same app rather than a new install.
#define MyAppId "{A7F3E2D1-9C4B-4A8E-8F1D-3B6C9E2A5F70}"

[Setup]
AppId={#MyAppId}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppSupportURL=https://github.com/
DefaultDirName={localappdata}\Programs\{#MyAppName}
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
DisableWelcomePage=no
; No admin rights required - installs entirely within the user's own profile
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir=.
OutputBaseFilename=verifysphere-setup
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
UninstallDisplayIcon={app}\{#MyAppExeName}
SetupLogging=yes
; Prevents install into a path containing another vendor's files by mistake
DirExistsWarning=auto

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
; VerifySphere.exe must be placed in the same directory as this .iss file
; before compiling (the release workflow does this automatically).
Source: "VerifySphere.exe"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{group}\Uninstall {#MyAppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Registry]
; Registers the verifysphere:// URI scheme under the CURRENT USER hive.
; HKCU\Software\Classes is merged by Windows into the effective
; HKEY_CLASSES_ROOT view for this user, giving identical behaviour to a
; machine-wide (HKLM) registration without requiring admin rights.
;
; "uninsdeletekey" on the top-level key means the entire verifysphere key
; tree (including the \shell\open\command subkey below) is automatically
; removed when the app is uninstalled - so uninstalling always leaves the
; system clean, with no leftover protocol registration.
Root: HKCU; Subkey: "Software\Classes\{#MyAppURLScheme}"; ValueType: string; ValueName: ""; ValueData: "URL:VerifySphere Protocol"; Flags: uninsdeletekey
Root: HKCU; Subkey: "Software\Classes\{#MyAppURLScheme}"; ValueType: string; ValueName: "URL Protocol"; ValueData: ""
Root: HKCU; Subkey: "Software\Classes\{#MyAppURLScheme}\DefaultIcon"; ValueType: string; ValueName: ""; ValueData: """{app}\{#MyAppExeName}"",0"
Root: HKCU; Subkey: "Software\Classes\{#MyAppURLScheme}\shell\open\command"; ValueType: string; ValueName: ""; ValueData: """{app}\{#MyAppExeName}"" ""%1"""

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#StringChange(MyAppName, '&', '&&')}}"; Flags: nowait postinstall skipifsilent
