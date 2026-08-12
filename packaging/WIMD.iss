#define MyAppName "WIMD"
#ifndef MyAppVersion
  #define MyAppVersion "1.3.0"
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
DisableDirPage=no
LicenseFile=license.zh-CN.txt
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
VersionInfoDescription=WIMD Windows 安装程序
VersionInfoProductName=WIMD
VersionInfoProductVersion={#MyAppVersion}
ShowLanguageDialog=no

[Languages]
Name: "chinesesimplified"; MessagesFile: "languages\ChineseSimplified.isl"

[Tasks]
Name: "desktopicon"; Description: "创建桌面快捷方式"; GroupDescription: "附加选项："; Flags: unchecked

[Files]
Source: "..\artifacts\publish\win-x64\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "..\LICENSE"; DestDir: "{app}"; DestName: "LICENSE.txt"; Flags: ignoreversion

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

[Code]
const
  WimdUninstallKey =
    'Software\Microsoft\Windows\CurrentVersion\Uninstall\{A278F55A-4D41-46B4-A1D9-DA41D8C7D655}_is1';

var
  ExistingInstallFound: Boolean;
  ExistingInstallDir: string;
  ExistingInstallVersion: string;

function TryReadExistingInstall(RootKey: Integer): Boolean;
var
  InstallDir: string;
begin
  Result := RegQueryStringValue(RootKey, WimdUninstallKey, 'InstallLocation', InstallDir) and
    (Trim(InstallDir) <> '');
  if not Result then
    Exit;

  ExistingInstallDir := InstallDir;
  if not RegQueryStringValue(RootKey, WimdUninstallKey, 'DisplayVersion', ExistingInstallVersion) then
    ExistingInstallVersion := '未知版本';
end;

procedure DetectExistingInstall;
begin
  ExistingInstallFound := TryReadExistingInstall(HKCU);
  if (not ExistingInstallFound) and IsWin64 then
    ExistingInstallFound := TryReadExistingInstall(HKLM64);
  if not ExistingInstallFound then
    ExistingInstallFound := TryReadExistingInstall(HKLM32);
end;

function InitializeSetup: Boolean;
var
  Prompt: string;
begin
  DetectExistingInstall;
  Result := True;
  if (not ExistingInstallFound) or WizardSilent then
    Exit;

  { Bug fix: Inno Setup normally hides the directory page during upgrades, but
    it did not tell users why. Confirm the detected target before overwriting it. }
  Prompt :=
    '检测到此电脑已安装 WIMD ' + ExistingInstallVersion + '。' + #13#10 + #13#10 +
    '安装位置：' + ExistingInstallDir + #13#10 + #13#10 +
    '点击“确定”将覆盖安装到原位置；点击“取消”退出安装。';
  Result := MsgBox(Prompt, mbConfirmation, MB_OKCANCEL) = IDOK;
end;

procedure InitializeWizard;
begin
  if ExistingInstallFound then
    WizardForm.DirEdit.Text := ExistingInstallDir;
end;

function ShouldSkipPage(PageID: Integer): Boolean;
begin
  { An upgrade must keep using the detected installation directory. The user
    explicitly chose a simple overwrite flow instead of alternate locations. }
  Result := ExistingInstallFound and (PageID = wpSelectDir);
end;
