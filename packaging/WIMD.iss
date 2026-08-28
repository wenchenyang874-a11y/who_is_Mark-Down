#define MyAppName "WIMD"
#ifndef MyAppVersion
  #error MyAppVersion must be supplied by packaging/build-release.ps1
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
Root: HKCU; Subkey: "Software\Classes\Directory\shell\WIMD"; ValueType: string; ValueData: "用 WIMD 打开"; Flags: uninsdeletekey
Root: HKCU; Subkey: "Software\Classes\Directory\shell\WIMD"; ValueType: string; ValueName: "Icon"; ValueData: "{app}\{#MyAppExeName},0"
Root: HKCU; Subkey: "Software\Classes\Directory\shell\WIMD\command"; ValueType: string; ValueData: """{app}\{#MyAppExeName}"" ""%1"""

[Icons]
Name: "{autoprograms}\WIMD"; Filename: "{app}\{#MyAppExeName}"
Name: "{autodesktop}\WIMD"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Parameters: "--restore-update-session"; Description: "重新打开并恢复 WIMD 窗口"; Flags: nowait postinstall skipifsilent; Check: ShouldRestoreUpdateSession
Filename: "{app}\{#MyAppExeName}"; Description: "启动 WIMD"; Flags: nowait postinstall skipifsilent; Check: ShouldLaunchNormally

[UninstallDelete]
; WIMD owns only this per-user settings/cache directory. User Markdown files,
; document assets and custom background source files are never stored here.
Type: filesandordirs; Name: "{localappdata}\WIMD"

[Code]
const
  WimdUninstallKey =
    'Software\Microsoft\Windows\CurrentVersion\Uninstall\{A278F55A-4D41-46B4-A1D9-DA41D8C7D655}_is1';
  WimdRunningMutexName =
    'Local\WIMD.UpdateRestart.A278F55A4D4146B4A1D9DA41D8C7D655';
  WimdRestartRequestRelativePath = 'WIMD\update-restart.request';

var
  ExistingInstallFound: Boolean;
  ExistingInstallDir: string;
  ExistingInstallVersion: string;
  RestartRequestCreated: Boolean;
  RestartRequestToken: string;
  InstallationCompleted: Boolean;

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

function GetRestartRequestFilePath: string;
begin
  Result := AddBackslash(ExpandConstant('{localappdata}')) +
    WimdRestartRequestRelativePath;
end;

function WriteRestartRequest(const Phase: string): Boolean;
var
  RequestDirectory: string;
  RequestLines: TArrayOfString;
begin
  RequestDirectory := ExtractFileDir(GetRestartRequestFilePath);
  SetArrayLength(RequestLines, 1);
  RequestLines[0] := Phase + ':' + RestartRequestToken;

  { SaveStringToFile accepts AnsiString in Unicode Inno Setup. Passing the
    Unicode expression above compiled successfully but raised Type Mismatch at
    runtime after the user confirmed "Close and install". Keep the request in
    BOM-less UTF-8 so the .NET reader can decode it strictly and consistently. }
  Result := ForceDirectories(RequestDirectory) and
    SaveStringsToUTF8FileWithoutBOM(
      GetRestartRequestFilePath,
      RequestLines,
      False);
end;

function ConfirmCloseAndInstall: Boolean;
var
  Prompt: string;
begin
  Prompt :=
    '“关闭并安装”说明' + #13#10 + #13#10 +
    '安装程序检测到 WIMD 正在运行。继续后将：' + #13#10 +
    '1. 把各窗口尚未保存的 Markdown 正文写入当前用户的临时恢复区；' + #13#10 +
    '2. 关闭 WIMD 并覆盖安装；' + #13#10 +
    '3. 在安装完成页面默认勾选“重新打开并恢复 WIMD 窗口”。' + #13#10 + #13#10 +
    '恢复后，未保存的文档仍保持“未保存”状态，不会自动覆盖原文件。' + #13#10 +
    '如果取消完成页勾选，恢复数据会保留到下次启动 WIMD。' + #13#10 + #13#10 +
    '点击“确定”继续关闭并安装，或点击“取消”返回。';
  Result := MsgBox(Prompt, mbConfirmation, MB_OKCANCEL) = IDOK;
end;

function NextButtonClick(CurPageID: Integer): Boolean;
begin
  Result := True;
  if (CurPageID <> wpReady) or RestartRequestCreated then
    Exit;

  if not CheckForMutexes(WimdRunningMutexName) then
    Exit;

  if not ConfirmCloseAndInstall then
  begin
    Result := False;
    Exit;
  end;

  RestartRequestToken :=
    { Pascal Script expects Char separators here. Empty strings compile but
      raise Type Mismatch only after the close-and-install confirmation. }
    GetDateTimeString('yyyymmddhhnnss', #0, #0) + '-' +
    IntToStr(Random(1000000000));
  RestartRequestCreated := WriteRestartRequest('capture');
  if not RestartRequestCreated then
  begin
    MsgBox(
      '无法创建 WIMD 临时恢复区。为避免丢失未保存的文档，安装已暂停。' +
      '' + #13#10 + #13#10 +
      '请先在 WIMD 中手动保存文档，然后重试。',
      mbError,
      MB_OK);
    Result := False;
  end;
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if (CurStep = ssPostInstall) and RestartRequestCreated then
  begin
    InstallationCompleted := True;
    if not WriteRestartRequest('restore') then
      MsgBox(
        'WIMD 已安装，但无法标记临时恢复区。请不要删除本机 WIMD 应用数据，并在启动后检查未保存文档。',
        mbError,
        MB_OK);
  end;
end;

procedure DeinitializeSetup;
begin
  { If setup is cancelled after WIMD closed, keep the captured dirty text
    recoverable on the next ordinary launch instead of deleting it. }
  if RestartRequestCreated and (not InstallationCompleted) then
    WriteRestartRequest('restore');
end;

function ShouldRestoreUpdateSession: Boolean;
begin
  Result := RestartRequestCreated;
end;

function ShouldLaunchNormally: Boolean;
begin
  Result := not RestartRequestCreated;
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
