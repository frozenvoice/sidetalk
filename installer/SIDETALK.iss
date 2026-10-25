; SIDETALK per-user installer (Inno Setup 6.7.3, prepared by scripts\prepare-inno.ps1).
; It installs and replaces only program files under {app}. SIDETALK's own data stays in
; %LOCALAPPDATA%\SIDETALK (profile, Sandboxie box storage, command and recovery history,
; consent) and is never moved, reset or deleted here. Do not add [UninstallDelete] or any
; rule that reaches outside {app}. Official KakaoTalk and Sandboxie are not included and
; their processes are never closed: CloseApplications=no and no process is stopped.
#ifndef AppVersion
  #error AppVersion is required; scripts\build-installer.ps1 reads it from the published Sidetalk.dll
#endif
#ifndef VersionInfoVersion
  #error VersionInfoVersion is required
#endif
#ifndef SourceDir
  #error SourceDir is required
#endif
#ifndef OutputDir
  #define OutputDir "..\artifacts\release"
#endif

#define AppName "SIDETALK"
#define ProductionAppId "AF6EF9DE-E012-4945-BBD1-39ED6572282D"
#ifndef AppId
  #define AppId ProductionAppId
#endif
; Only the isolated installer regression (tests\Sidetalk.InstallerChecks) defines this.
; It must come with its own SIDETALKInstallerTest-* AppId and every location, so a test
; install can never share the real install, Start Menu, desktop or setup mutex.
#ifdef FixtureNamespaceSHA256
  #if SameText(AppId, ProductionAppId) || Pos("SIDETALKInstallerTest-", AppId) != 1 || Len(AppId) <= Len("SIDETALKInstallerTest-")
    #error FixtureNamespaceSHA256 requires a separate SIDETALKInstallerTest-* AppId
  #endif
  #if Len(FixtureNamespaceSHA256) != 64
    #error FixtureNamespaceSHA256 must contain exactly 64 lowercase hex characters
  #endif
  #ifndef DefaultInstallDir
    #error A fixture installer must define its own DefaultInstallDir
  #endif
  #ifndef StartMenuDir
    #error A fixture installer must define its own StartMenuDir
  #endif
  #ifndef DesktopDir
    #error A fixture installer must define its own DesktopDir
  #endif
  #define private SetupMutexName "SIDETALK.Fixture." + FixtureNamespaceSHA256 + ".Setup"
#else
  #if !SameText(AppId, ProductionAppId)
    #error Only the isolated installer regression may change AppId
  #endif
  #define private SetupMutexName "SIDETALK.Setup"
#endif
#ifndef DefaultInstallDir
  #define DefaultInstallDir "{localappdata}\Programs\SIDETALK"
#endif
#ifndef StartMenuDir
  #define StartMenuDir "{autoprograms}"
#endif
#ifndef DesktopDir
  #define DesktopDir "{autodesktop}"
#endif
; The executable the uninstaller asks to remove SIDETALK-made "보조 카카오톡 실행" links.
; Only the isolated regression replaces it with a recorder that touches no user folder.
#ifndef IntegrationCleanupExe
  #define IntegrationCleanupExe "Sidetalk.exe"
#endif
; Windows Application User Model ID, not the Inno Setup AppId above. It must stay
; byte-for-byte equal to AppIdentity.AppUserModelId in src\Sidetalk.App\AppIdentity.cs
; so the running window and this Start Menu shortcut share one taskbar identity.
#ifndef AppUserModelID
  #define AppUserModelID "SIDETALK.SIDETALK"
#endif

[Setup]
AppId={{{#AppId}}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher=SIDETALK
AppPublisherURL=https://github.com/frozenvoice/sidetalk
VersionInfoVersion={#VersionInfoVersion}
VersionInfoProductName={#AppName}
VersionInfoProductVersion={#VersionInfoVersion}
VersionInfoProductTextVersion={#AppVersion}
VersionInfoTextVersion={#AppVersion}
VersionInfoDescription=SIDETALK Setup
DefaultDirName={#DefaultInstallDir}
; The location is fixed; PrepareToInstall also rejects a /DIR= override.
DisableDirPage=yes
UsePreviousAppDir=no
; [Icons] uses {#StartMenuDir} directly so the shortcut is a Programs-root
; "SIDETALK" item, not a folder. Name the group and skip its page.
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
AllowNoIcons=no
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
OutputDir={#OutputDir}
OutputBaseFilename=SIDETALK-Setup
SetupIconFile=..\src\Sidetalk.App\Assets\Sidetalk.ico
; Installed Apps shows the installed Sidetalk.exe's own embedded icon.
UninstallDisplayIcon={app}\Sidetalk.exe
UninstallDisplayName={#AppName}
Compression=lzma2/max
SolidCompression=yes
; WizardStyle and the window art come from SetupTheme.iss, included at the end.
SetupLogging=yes
SetupMutex={#SetupMutexName}
; Never ask Restart Manager to close anything: only SIDETALK's own files are
; checked, and a running SIDETALK blocks Setup until the person closes it.
CloseApplications=no
RestartApplications=no
RestartIfNeededByRun=no
AllowCancelDuringInstall=yes
Uninstallable=yes

#ifdef SidetalkSigningEnabled
SignTool=sidetalksign
SignedUninstaller=yes
#endif

[Languages]
Name: "korean"; MessagesFile: "compiler:Languages\Korean.isl"

[Tasks]
Name: "desktopicon"; Description: "바탕 화면에 SIDETALK 바로가기 만들기"; GroupDescription: "추가 바로가기:"; Flags: unchecked

[InstallDelete]
; Only folders that hold nothing but published program files. {app} is verified
; to be the fixed install location before any of this runs.
Type: filesandordirs; Name: "{app}\docs"
Type: filesandordirs; Name: "{app}\tools\IsolationProbe"

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
; Installer-owned main entry. The app's own "보조 카카오톡 실행" links are separate:
; SIDETALK creates and removes those through user-launch-integration ownership.
Name: "{#StartMenuDir}\{#AppName}"; Filename: "{app}\Sidetalk.exe"; WorkingDir: "{app}"; Comment: "SIDETALK 보조 카카오톡 관리"; AppUserModelID: "{#AppUserModelID}"
Name: "{#DesktopDir}\{#AppName}"; Filename: "{app}\Sidetalk.exe"; WorkingDir: "{app}"; Comment: "SIDETALK 보조 카카오톡 관리"; AppUserModelID: "{#AppUserModelID}"; Tasks: desktopicon

[Run]
; Opens SIDETALK only. KakaoTalk still requires SIDETALK's own confirmation.
Filename: "{app}\Sidetalk.exe"; WorkingDir: "{app}"; Description: "SIDETALK 실행"; Flags: nowait postinstall skipifsilent; Check: ShouldOfferLaunch

[Code]
const
  INVALID_HANDLE_VALUE = -1;
  INVALID_FILE_ATTRIBUTES = $FFFFFFFF;
  FILE_SHARE_READ = $00000001;
  FILE_SHARE_WRITE = $00000002;
  FILE_SHARE_DELETE = $00000004;
  OPEN_EXISTING = 3;
  GENERIC_READ = $80000000;
  GENERIC_WRITE = $40000000;
  DELETE_ACCESS = $00010000;
  CLEANUP_PRESERVED_EDITED = 3;
  InstallerVersion = '{#AppVersion}';

var
  PrepareFailure: String;

function GetFileAttributesW(Name: String): Cardinal;
  external 'GetFileAttributesW@kernel32.dll stdcall';
function CreateFileW(Name: String; DesiredAccess, ShareMode, SecurityAttributes: Cardinal; CreationDisposition, FlagsAndAttributes, TemplateFile: Cardinal): THandle;
  external 'CreateFileW@kernel32.dll stdcall';
function CloseHandle(Handle: THandle): Boolean;
  external 'CloseHandle@kernel32.dll stdcall';

function HasParameter(const Name: String): Boolean;
var
  I: Integer;
begin
  Result := False;
  for I := 1 to ParamCount do
    if CompareText(ParamStr(I), Name) = 0 then begin
      Result := True;
      Exit;
    end;
end;

function ShouldOfferLaunch(): Boolean;
begin
  Result := not HasParameter('/SIDETALKNOLAUNCH');
end;

function PathHasReparsePoint(const Path: String): Boolean;
var
  Cursor: String;
  Attributes: Cardinal;
begin
  Result := False;
  Cursor := RemoveBackslashUnlessRoot(Path);
  while Cursor <> '' do begin
    Attributes := GetFileAttributesW(Cursor);
    if (Attributes <> INVALID_FILE_ATTRIBUTES) and ((Attributes and FILE_ATTRIBUTE_REPARSE_POINT) <> 0) then begin
      Result := True;
      Exit;
    end;
    if ExtractFileDir(Cursor) = Cursor then
      Exit;
    Cursor := ExtractFileDir(Cursor);
  end;
end;

{ A running image cannot be opened for write/delete. This checks exactly the
  installed SIDETALK files, never a process name, so an unrelated program with
  the same image name is neither detected nor touched. }
function InstalledFileIsInUse(const Path: String): Boolean;
var
  Handle: THandle;
begin
  Result := False;
  if GetFileAttributesW(Path) = INVALID_FILE_ATTRIBUTES then
    Exit;
  Handle := CreateFileW(Path, GENERIC_READ or GENERIC_WRITE or DELETE_ACCESS,
    FILE_SHARE_READ or FILE_SHARE_WRITE or FILE_SHARE_DELETE, 0, OPEN_EXISTING, 0, 0);
  if Handle = INVALID_HANDLE_VALUE then
    Result := True
  else
    CloseHandle(Handle);
end;

function VersionPart(Value: String; Index: Integer): Integer;
var
  Dot: Integer;
begin
  Result := -1;
  while Index > 1 do begin
    Dot := Pos('.', Value);
    if Dot = 0 then
      Exit;
    Delete(Value, 1, Dot);
    Index := Index - 1;
  end;
  Dot := Pos('.', Value);
  if Dot > 0 then
    Value := Copy(Value, 1, Dot - 1);
  Result := StrToIntDef(Value, -1);
end;

{ SIDETALK versions are plain major.minor.patch. -2 means "not comparable". }
function CompareVersions(const Left, Right: String): Integer;
var
  I, L, R: Integer;
begin
  Result := 0;
  for I := 1 to 3 do begin
    L := VersionPart(Left, I);
    R := VersionPart(Right, I);
    if (L < 0) or (R < 0) then begin
      Result := -2;
      Exit;
    end;
    if L < R then begin
      Result := -1;
      Exit;
    end;
    if L > R then begin
      Result := 1;
      Exit;
    end;
  end;
end;

function RunningError(const AppDir: String): String;
begin
  Result := '';
  if InstalledFileIsInUse(AddBackslash(AppDir) + 'Sidetalk.exe') or
     InstalledFileIsInUse(AddBackslash(AppDir) + 'tools\IsolationProbe\Sidetalk.IsolationProbe.exe') then
    Result := 'SIDETALK 또는 IsolationProbe가 실행 중입니다. SIDETALK 창과 IsolationProbe를 정상 종료한 뒤 다시 시도하세요. ' +
      '카카오톡과 Sandboxie는 종료할 필요가 없으며 설치 프로그램도 종료하지 않습니다. 기존 설치는 변경되지 않았습니다.';
end;

function InstallPreconditionError(): String;
var
  AppDir, Expected, InstalledVersion, Key: String;
  Comparison: Integer;
begin
  AppDir := RemoveBackslashUnlessRoot(ExpandConstant('{app}'));
  Expected := RemoveBackslashUnlessRoot(ExpandConstant('{#DefaultInstallDir}'));
  if CompareText(AppDir, Expected) <> 0 then begin
    Result := 'SIDETALK는 고정된 사용자 설치 위치(' + Expected + ')에만 설치합니다. 다른 위치는 지원하지 않습니다. 기존 설치는 변경되지 않았습니다.';
    Exit;
  end;
  if PathHasReparsePoint(AppDir) then begin
    Result := 'SIDETALK 설치 경로에 연결(reparse point)이 있어 안전하게 진행할 수 없습니다. 기존 설치는 변경되지 않았습니다.';
    Exit;
  end;
  Result := RunningError(AppDir);
  if Result <> '' then
    Exit;
  Key := 'Software\Microsoft\Windows\CurrentVersion\Uninstall\' + Chr(123) + '{#AppId}' + Chr(125) + '_is1';
  if RegQueryStringValue(HKCU, Key, 'DisplayVersion', InstalledVersion) then begin
    Comparison := CompareVersions(InstallerVersion, InstalledVersion);
    if Comparison = -1 then
      Result := 'SIDETALK ' + InstalledVersion + '이(가) 이미 설치되어 있어 더 낮은 ' + InstallerVersion + ' 버전으로 바꾸지 않습니다. 기존 설치는 변경되지 않았습니다.'
    else if Comparison = -2 then
      Result := '설치된 SIDETALK 버전(' + InstalledVersion + ')을 확인할 수 없어 업데이트를 중단했습니다. 기존 설치는 변경되지 않았습니다.';
  end;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  { Same-version reinstall is allowed (local developer builds); downgrade is not. }
  Result := InstallPreconditionError();
  PrepareFailure := Result;
  if Result <> '' then
    Log('SIDETALK install blocked: ' + Result);
end;

function InitializeUninstall(): Boolean;
var
  ErrorText: String;
begin
  Result := True;
  ErrorText := RunningError(ExpandConstant('{app}'));
  if ErrorText = '' then
    if PathHasReparsePoint(ExpandConstant('{app}')) then
      ErrorText := 'SIDETALK 설치 경로에 연결(reparse point)이 있어 안전하게 제거할 수 없습니다.';
  if ErrorText <> '' then begin
    Log('SIDETALK uninstall blocked: ' + ErrorText);
    SuppressibleMsgBox(ErrorText, mbError, MB_OK, IDOK);
    Result := False;
  end;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  Cleanup: String;
  ResultCode: Integer;
begin
  if CurUninstallStep <> usUninstall then
    Exit;
  { Before any file is removed: let SIDETALK remove only the "보조 카카오톡 실행"
    links it can prove it owns for this executable. Profile, box storage,
    consent and history in %LOCALAPPDATA%\SIDETALK are left as they are. A
    failure here never falls back to deleting anything. }
  Cleanup := ExpandConstant('{app}\{#IntegrationCleanupExe}');
  if not FileExists(Cleanup) then begin
    Log('SIDETALK shortcut cleanup skipped: ' + Cleanup + ' is missing.');
    Exit;
  end;
  if not Exec(Cleanup, '--uninstall-owned-integration', ExpandConstant('{app}'), SW_HIDE, ewWaitUntilTerminated, ResultCode) then begin
    Log('SIDETALK shortcut cleanup could not start: ' + SysErrorMessage(ResultCode));
    SuppressibleMsgBox('SIDETALK가 만든 "보조 카카오톡 실행" 바로가기를 확인하지 못해 그대로 두었습니다. 필요하면 직접 삭제하세요. SIDETALK 사용자 데이터는 삭제하지 않습니다.', mbInformation, MB_OK, IDOK);
    Exit;
  end;
  Log('SIDETALK shortcut cleanup exit code: ' + IntToStr(ResultCode));
  if ResultCode = CLEANUP_PRESERVED_EDITED then
    SuppressibleMsgBox('사용자가 바꾼 "보조 카카오톡 실행" 바로가기는 그대로 두었습니다. 필요하면 직접 삭제하세요.', mbInformation, MB_OK, IDOK)
  else if ResultCode <> 0 then
    SuppressibleMsgBox('SIDETALK가 만든 "보조 카카오톡 실행" 바로가기 상태를 확인하지 못해 그대로 두었습니다. 필요하면 직접 삭제하세요. SIDETALK 사용자 데이터는 삭제하지 않습니다.', mbInformation, MB_OK, IDOK);
end;

{ Presentation hooks for SetupTheme.iss. They only choose wording; the gates
  above still decide whether anything is installed. }
function SetupThemeMode(): Integer;
var
  InstalledVersion: String;
begin
  Result := 0;
  if RegQueryStringValue(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Uninstall\' + Chr(123) + '{#AppId}' + Chr(125) + '_is1',
    'DisplayVersion', InstalledVersion) then begin
    { A refused downgrade or unreadable version keeps the plain install wording;
      PrepareToInstall reports why it stops. }
    case CompareVersions(InstallerVersion, InstalledVersion) of
      0: Result := 2;
      1: Result := 1;
    end;
  end;
end;

function SetupThemeFailure(): String;
begin
  Result := PrepareFailure;
end;

#define public SetupThemeAppName AppName
#define public SetupThemeAppObject "SIDETALK를"
#define public SetupThemeVersion AppVersion
#define public SetupThemeNote "카카오톡과 Sandboxie는 종료하지 않습니다."
#define public SetupThemeTask "desktopicon"
#define public SetupThemeTaskCaption "바탕 화면에 SIDETALK 바로가기 만들기"
#define public SetupThemeArt "..\assets\installer"
#include "SetupTheme.iss"
