#define MyAppName "WIMD"
#ifndef MyAppVersion
  #define MyAppVersion "1.1.0"
#endif
#define MyAppPublisher "wenchenyang874-a11y"
#define MyAppURL "https://github.com/wenchenyang874-a11y/who_is_Mark-Down"
#define MyAppExeName "WIMD.exe"

[Setup]
AppId={{A278F55A-4D41-46B4-A1D9-DA41D8C7D655}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}/issues
AppUpdatesURL={#MyAppURL}/releases
DefaultDirName={autopf}\WIMD
DefaultGroupName=WIMD
DisableProgramGroupPage=yes
LicenseFile=..\LICENSE
OutputDir=..\artifacts\installer
OutputBaseFilename=WIMD-Setup-v{#MyAppVersion}-win-x64
SetupIconFile=..\assets\app.ico
UninstallDisplayIcon={app}\{#MyAppExeName}
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
PrivilegesRequired=lowest
CloseApplications=yes
RestartApplications=no
VersionInfoVersion={#MyAppVersion}.0
VersionInfoCompany={#MyAppPublisher}
VersionInfoDescription=WIMD Windows Installer
VersionInfoProductName=WIMD
VersionInfoProductVersion={#MyAppVersion}

[Tasks]
Name: "desktopicon"; Description: "创建桌面快捷方式"; GroupDescription: "附加图标："; Flags: unchecked

[Files]
Source: "..\artifacts\publish\win-x64\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Registry]
Root: HKCU; Subkey: "Software\Classes\WIMD.Markdown"; ValueType: string; ValueData: "Markdown 文档"; Flags: uninsdeletekey
Root: HKCU; Subkey: "Software\Classes\WIMD.Markdown\DefaultIcon"; ValueType: string; ValueData: "{app}\{#MyAppExeName},0"
Root: HKCU; Subkey: "Software\Classes\WIMD.Markdown\shell\open\command"; ValueType: string; ValueData: """{app}\{#MyAppExeName}"" ""%1"""
Root: HKCU; Subkey: "Software\Classes\.md\OpenWithProgids"; ValueType: string; ValueName: "WIMD.Markdown"; ValueData: ""; Flags: uninsdeletevalue
Root: HKCU; Subkey: "Software\Classes\.markdown\OpenWithProgids"; ValueType: string; ValueName: "WIMD.Markdown"; ValueData: ""; Flags: uninsdeletevalue
Root: HKCU; Subkey: "Software\Classes\SystemFileAssociations\.md\shell\WIMD"; ValueType: string; ValueData: "用 WIMD 打开"; Flags: uninsdeletekey
Root: HKCU; Subkey: "Software\Classes\SystemFileAssociations\.md\shell\WIMD"; ValueType: string; ValueName: "Icon"; ValueData: "{app}\{#MyAppExeName},0"
Root: HKCU; Subkey: "Software\Classes\SystemFileAssociations\.md\shell\WIMD\command"; ValueType: string; ValueData: """{app}\{#MyAppExeName}"" ""%1"""
Root: HKCU; Subkey: "Software\Classes\SystemFileAssociations\.markdown\shell\WIMD"; ValueType: string; ValueData: "用 WIMD 打开"; Flags: uninsdeletekey
Root: HKCU; Subkey: "Software\Classes\SystemFileAssociations\.markdown\shell\WIMD"; ValueType: string; ValueName: "Icon"; ValueData: "{app}\{#MyAppExeName},0"
Root: HKCU; Subkey: "Software\Classes\SystemFileAssociations\.markdown\shell\WIMD\command"; ValueType: string; ValueData: """{app}\{#MyAppExeName}"" ""%1"""

[Icons]
Name: "{autoprograms}\WIMD"; Filename: "{app}\{#MyAppExeName}"
Name: "{autodesktop}\WIMD"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "启动 WIMD"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
; WIMD owns only this per-user settings/cache directory. User Markdown files,
; document assets and custom background source files are never stored here.
Type: filesandordirs; Name: "{localappdata}\WIMD"
