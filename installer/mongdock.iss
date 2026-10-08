; mongdock 설치 프로그램 (Inno Setup 6)
;   build-release.ps1 이 publish 후 다음처럼 컴파일한다:
;   ISCC.exe /DAppVersion=0.2.0 /DSourceDir=..\dist\mongdock /DOutputDir=..\dist /DOutputName=mongdock-v0.2.0-setup installer\mongdock.iss
; 관리자 권한 없이 %LOCALAPPDATA%\Programs\mongdock 에 설치 (zip 수동 설치 위치와 같아서 그대로 덮어쓰기 업그레이드).

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif
#ifndef NumericVersion
  #define NumericVersion "0.0.0"
#endif
#ifndef SourceDir
  ; framework-dependent publish 출력 (build-release.ps1 의 dist\mongdock-setup)
  #define SourceDir "..\dist\mongdock-setup"
#endif
#ifndef OutputDir
  #define OutputDir "..\dist"
#endif
#ifndef OutputName
  #define OutputName "mongdock-" + AppVersion + "-setup"
#endif

#define AppName "mongdock"
#define AppExe "mongdock.exe"
#define AppUrl "https://github.com/mong-head/mongdock"

[Setup]
; AppId 는 절대 바꾸지 말 것 (업그레이드/제거가 이 값으로 같은 앱을 찾는다)
AppId={{E7300DFF-4D79-4C67-BF72-F83A83F78D73}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName=mongdock (몽독) {#AppVersion}
UninstallDisplayName=mongdock (몽독)
AppPublisher=mong-head
AppPublisherURL={#AppUrl}
AppSupportURL={#AppUrl}/issues
AppUpdatesURL={#AppUrl}/releases
VersionInfoVersion={#NumericVersion}
VersionInfoProductName=mongdock
VersionInfoDescription=mongdock (몽독) 설치 프로그램
PrivilegesRequired=lowest
DefaultDirName={localappdata}\Programs\mongdock
DisableDirPage=yes
DefaultGroupName=mongdock
DisableProgramGroupPage=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.19041
UninstallDisplayIcon={app}\{#AppExe}
SetupIconFile=..\src\mongdock\Assets\mongdock.ico
OutputDir={#OutputDir}
OutputBaseFilename={#OutputName}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
; 실행 중인 mongdock 은 [Code] 에서 정상 종료 요청으로 끈다 (Restart Manager 로 강제 종료하면 작업 표시줄 복원이 안 될 수 있음)
CloseApplications=no
RestartApplications=no

[Languages]
Name: "korean"; MessagesFile: "compiler:Languages\Korean.isl"

[CustomMessages]
korean.AutoStartTask=로그인 시 자동 실행
korean.OtherTasks=기타:
korean.RuntimeDownloadTitle=.NET 데스크톱 런타임 준비
korean.RuntimeDownloadDesc=mongdock 에 필요한 .NET 8 Desktop Runtime(x64)을 마이크로소프트에서 받아 설치합니다 (처음 한 번).
korean.RuntimeInstalling=.NET 8 Desktop Runtime 설치 중... (관리자 권한 확인 창이 뜨면 "예")
korean.RuntimeDownloadFailed=.NET 8 Desktop Runtime 을 받지 못했습니다: %1%n%n인터넷 연결을 확인하고 다시 시도하거나, 직접 설치한 뒤 다시 실행하세요:%nhttps://dotnet.microsoft.com/download/dotnet/8.0
korean.RuntimeInstallFailed=.NET 8 Desktop Runtime 을 설치하지 못했습니다 (%1).%n%n관리자 권한 확인을 "예" 로 하거나, 직접 설치한 뒤 다시 실행하세요:%nhttps://dotnet.microsoft.com/download/dotnet/8.0
korean.DeleteSettingsPrompt=mongdock 설정과 로그(AppData\Roaming\mongdock 폴더)도 지울까요?%n%n"아니요" 를 누르면 남겨 두어 나중에 다시 설치할 때 그대로 쓸 수 있습니다.

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked
Name: "autostart"; Description: "{cm:AutoStartTask}"; GroupDescription: "{cm:OtherTasks}"

[InstallDelete]
; v0.3.x 이전 레이아웃 정리: 지금은 framework-dependent 단일 mongdock.exe 하나라 옆에 남은 dll·런타임 설정·디버그 파일은 필요 없다
; (남아 있으면 크기만 차지하고 다른 버전 파일과 섞임). 설정 폴더(%APPDATA%\mongdock)는 건드리지 않는다.
Type: files; Name: "{app}\*.dll"
Type: files; Name: "{app}\mongdock.deps.json"
Type: files; Name: "{app}\mongdock.runtimeconfig.json"
Type: files; Name: "{app}\mongdock.pdb"
Type: files; Name: "{app}\createdump.exe"
; self-contained 단일 파일이 풀어 두던 네이티브 라이브러리 (%TEMP%\.net\mongdock\<해시>) — 몽독은 이미 꺼진 뒤라 안전
Type: filesandordirs; Name: "{localappdata}\Temp\.net\mongdock"

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{userprograms}\mongdock"; Filename: "{app}\{#AppExe}"; WorkingDir: "{app}"
Name: "{userdesktop}\mongdock"; Filename: "{app}\{#AppExe}"; WorkingDir: "{app}"; Tasks: desktopicon

[Registry]
; 앱의 StartupService 와 같은 키·값 이름·형식 (따옴표로 감싼 exe 경로).
; 설치 끝(ssPostInstall)에 mongdock.exe --register-startup 이 작업 스케줄러 로그온 작업으로 옮기고 이 값을 지운다
; (Run 키는 로그온 후 수십 초 늦게 실행됨). 작업 등록이 막힌 PC(회사 정책 등)에서는 이 값이 그대로 남아 대체로 쓰인다.
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "mongdock"; ValueData: """{app}\{#AppExe}"""; Tasks: autostart

[UninstallDelete]
; 앱이 받은 업데이트 설치 파일·캐시 (%LOCALAPPDATA%\mongdock — 설정 폴더 %APPDATA%\mongdock 은 [Code] 에서 따로 물어봄)
Type: filesandordirs; Name: "{localappdata}\mongdock\updates"
Type: filesandordirs; Name: "{localappdata}\mongdock\cache"
Type: dirifempty; Name: "{localappdata}\mongdock"

[Run]
; 대화형 설치: 마침 화면의 "mongdock 실행" 체크(기본 켬)로 결정.
; 조용한 설치(/SILENT, /VERYSILENT — 앱의 "지금 업데이트" 가 /SILENT 로 실행): skipifsilent 가 없으므로 체크된 것으로 보고 항상 다시 실행.
Filename: "{app}\{#AppExe}"; Description: "{cm:LaunchProgram,mongdock}"; WorkingDir: "{app}"; Flags: nowait postinstall

[Code]
const
  RunKey = 'Software\Microsoft\Windows\CurrentVersion\Run';
  ExitEventName = 'Local\mongdock.Exit';
  InstanceMutexName = 'Local\mongdock.SingleInstance';
  EVENT_MODIFY_STATE = $0002;
  SYNCHRONIZE = $00100000;
  SW_SHOWNA = 8;
  SoundCurrentKey = 'AppEvents\Schemes\Apps\.Default\Notification.Default\.Current';
  SoundDefaultKey = 'AppEvents\Schemes\Apps\.Default\Notification.Default\.Default';
  CP_UTF8 = 65001;
  { 앱의 StartupService.TaskName 과 같은 규칙: 'mongdock-' + 사용자 이름 (작업 이름은 PC 전체에서 하나라 계정마다 따로) }
  StartupTaskPrefix = 'mongdock-';

function OpenEvent(dwDesiredAccess: DWORD; bInheritHandle: BOOL; lpName: String): THandle;
  external 'OpenEventW@kernel32.dll stdcall';
function SetEvent(hEvent: THandle): BOOL;
  external 'SetEvent@kernel32.dll stdcall';
function OpenMutex(dwDesiredAccess: DWORD; bInheritHandle: BOOL; lpName: String): THandle;
  external 'OpenMutexW@kernel32.dll stdcall';
function CloseHandle(hObject: THandle): BOOL;
  external 'CloseHandle@kernel32.dll stdcall';
function FindWindowEx(hWndParent, hWndChildAfter: HWND; lpszClass: String; lpszWindow: LongWord): HWND;
  external 'FindWindowExW@user32.dll stdcall';
function ShowWindowAsync(hWnd: HWND; nCmdShow: Integer): BOOL;
  external 'ShowWindowAsync@user32.dll stdcall';
function MultiByteToWideChar(CodePage: Cardinal; dwFlags: DWORD; lpMultiByteStr: AnsiString; cbMultiByte: Integer; lpWideCharStr: String; cchWideChar: Integer): Integer;
  external 'MultiByteToWideChar@kernel32.dll stdcall';
function ExpandEnvironmentStrings(lpSrc: String; lpDst: String; nSize: DWORD): DWORD;
  external 'ExpandEnvironmentStringsW@kernel32.dll stdcall';

{ mongdock 이 실행 중인지: 단일 인스턴스 뮤텍스가 있으면 실행 중 (프로세스가 끝나면 뮤텍스도 사라진다) }
function IsMongdockRunning(): Boolean;
var
  H: THandle;
begin
  H := OpenMutex(SYNCHRONIZE, False, InstanceMutexName);
  Result := H <> 0;
  if Result then
    CloseHandle(H);
end;

function WaitForExit(TimeoutMs: Integer): Boolean;
var
  Waited: Integer;
begin
  Waited := 0;
  while IsMongdockRunning() and (Waited < TimeoutMs) do
  begin
    Sleep(250);
    Waited := Waited + 250;
  end;
  Result := not IsMongdockRunning();
end;

{ 강제 종료 뒤: mongdock 이 숨겨 둔 작업 표시줄(주 + 보조 모니터)을 다시 보이게 한다 }
procedure RestoreTaskbar();
var
  Wnd: HWND;
  I: Integer;
begin
  Wnd := FindWindowEx(0, 0, 'Shell_TrayWnd', 0);
  if Wnd <> 0 then
    ShowWindowAsync(Wnd, SW_SHOWNA);
  Wnd := 0;
  for I := 1 to 16 do
  begin
    Wnd := FindWindowEx(0, Wnd, 'Shell_SecondaryTrayWnd', 0);
    if Wnd = 0 then
      Break;
    ShowWindowAsync(Wnd, SW_SHOWNA);
  end;
end;

procedure RunTaskkill(Params: String);
var
  Code: Integer;
begin
  Exec(ExpandConstant('{sys}\taskkill.exe'), Params, '', SW_HIDE, ewWaitUntilTerminated, Code);
end;

{ 실행 중인 mongdock 을 끈다.
  1) --exit 와 같은 종료 이벤트 → 트레이 "종료" 와 같은 정상 종료 (작업 표시줄·AppBar 복원)
  2) 이벤트가 없는 이전 버전/응답 없음 → taskkill (WM_CLOSE 로 창을 닫아 AppBar 해제) → taskkill /F
  강제 종료까지 갔다면 작업 표시줄을 직접 복원한다. }
procedure CloseMongdock();
var
  H: THandle;
begin
  if not IsMongdockRunning() then
    Exit;

  H := OpenEvent(EVENT_MODIFY_STATE, False, ExitEventName);
  if H <> 0 then
  begin
    Log('mongdock 정상 종료 요청');
    SetEvent(H);
    CloseHandle(H);
    if WaitForExit(10000) then
      Exit;
  end;

  Log('mongdock 이 정상 종료되지 않음 → taskkill');
  RunTaskkill('/IM {#AppExe}');
  if not WaitForExit(3000) then
  begin
    RunTaskkill('/F /IM {#AppExe}');
    WaitForExit(5000);
  end;
  RestoreTaskbar();
end;

{ 제거 시: mongdock 이 작업 표시줄을 숨기느라 켜 둔 작업 표시줄 자동 숨김이 (강제 종료로) 남아 있으면 원래대로.
  기록 파일은 정상 종료 때 지워지므로, 있으면 설치 폴더의 exe 로 되돌린다 (ABM_SETSTATE). }
procedure RestoreTaskbarAutoHide();
var
  StateFile, Exe: String;
  Code: Integer;
begin
  StateFile := ExpandConstant('{userappdata}\mongdock\cache\taskbar-state.json');
  if not FileExists(StateFile) then
    Exit;
  Exe := ExpandConstant('{app}\{#AppExe}');
  if not FileExists(Exe) then
    Exit;
  Log('작업 표시줄 상태 기록 있음 → ' + Exe + ' --restore-taskbar');
  if not Exec(Exe, '--restore-taskbar', '', SW_HIDE, ewWaitUntilTerminated, Code) then
    Log('--restore-taskbar 실행 실패: ' + IntToStr(Code));
end;

{ ---------- .NET 8 Desktop Runtime (x64) ----------
  설치본은 framework-dependent 라 런타임이 필요하다. 없으면 마이크로소프트 공식 주소(최신 8.0.x 로 리디렉트)에서 받아
  /install /quiet /norestart 로 설치한다. 런타임 설치만 관리자 권한(UAC), 몽독 자체는 사용자 설치 그대로. }
const
  RuntimeUrl = 'https://aka.ms/dotnet/8.0/windowsdesktop-runtime-win-x64.exe';
  RuntimeFile = 'windowsdesktop-runtime-8.0-win-x64.exe';
  ERROR_CANCELLED = 1223;

var
  RuntimePage: TDownloadWizardPage;

{ Root\shared\<Name>\8.* 중 Marker 파일이 있는 폴더가 있는지 }
function HasFramework8(const Root, Name, Marker: String): Boolean;
var
  Dir: String;
  FindRec: TFindRec;
begin
  Result := False;
  Dir := AddBackslash(Root) + 'shared\' + Name + '\';
  if FindFirst(Dir + '8.*', FindRec) then
  try
    repeat
      if ((FindRec.Attributes and FILE_ATTRIBUTE_DIRECTORY) <> 0) and FileExists(Dir + FindRec.Name + '\' + Marker) then
        Result := True;
    until Result or not FindNext(FindRec);
  finally
    FindClose(FindRec);
  end;
end;

function HasDesktopRuntime8In(const Root: String): Boolean;
begin
  Result := (Root <> '') and DirExists(AddBackslash(Root) + 'host\fxr') and
    HasFramework8(Root, 'Microsoft.NETCore.App', 'System.Private.CoreLib.dll') and
    HasFramework8(Root, 'Microsoft.WindowsDesktop.App', 'PresentationFramework.dll');
end;

{ apphost(mongdock.exe)가 런타임을 찾는 순서와 같게: DOTNET_ROOT(_X64) → 등록된 설치 위치(32비트 레지스트리 뷰) → Program Files\dotnet }
function IsDesktopRuntime8Installed(): Boolean;
var
  Loc: String;
begin
  Result := HasDesktopRuntime8In(GetEnv('DOTNET_ROOT_X64')) or HasDesktopRuntime8In(GetEnv('DOTNET_ROOT'));
  if not Result and RegQueryStringValue(HKLM32, 'SOFTWARE\dotnet\Setup\InstalledVersions\x64', 'InstallLocation', Loc) then
    Result := HasDesktopRuntime8In(RemoveBackslashUnlessRoot(Loc));
  if not Result then
    Result := HasDesktopRuntime8In(ExpandConstant('{commonpf64}\dotnet'));
end;

{ 받은 설치 파일을 관리자 권한으로 조용히 실행. 성공 여부는 종료 코드 대신 설치 결과(파일)로 판단 }
function InstallRuntime(const Path: String): String;
var
  Code: Integer;
begin
  Result := '';
  Log('.NET Desktop Runtime 설치: ' + Path);
  if not ShellExec('runas', Path, '/install /quiet /norestart', '', SW_SHOW, ewWaitUntilTerminated, Code) then
  begin
    if Code = ERROR_CANCELLED then
      Result := FmtMessage(CustomMessage('RuntimeInstallFailed'), ['관리자 권한 확인 취소'])
    else
      Result := FmtMessage(CustomMessage('RuntimeInstallFailed'), [SysErrorMessage(Code)]);
  end
  else if not IsDesktopRuntime8Installed() then
    Result := FmtMessage(CustomMessage('RuntimeInstallFailed'), ['설치 후에도 런타임을 찾지 못함, 코드 ' + IntToStr(Code)]);
  DeleteFile(Path);
  if Result = '' then
    Log('.NET Desktop Runtime 설치 완료')
  else
    Log(Result);
end;

{ 런타임 보장. 빈 문자열이면 준비됨, 아니면 사용자에게 보여 줄 오류.
  UsePage: 대화형 설치의 다운로드 페이지(진행률·취소) 사용. 조용한 설치(/SILENT, 앱의 자동 업데이트)는 화면 없이 받는다. }
function EnsureDesktopRuntime(UsePage: Boolean): String;
var
  Path: String;
begin
  Result := '';
  if IsDesktopRuntime8Installed() then
  begin
    Log('.NET 8 Desktop Runtime 있음 — 다운로드 안 함');
    Exit;
  end;
  Log('.NET 8 Desktop Runtime 없음 → 다운로드: ' + RuntimeUrl);
  Path := ExpandConstant('{tmp}\' + RuntimeFile);
  if UsePage and (RuntimePage <> nil) then
  begin
    RuntimePage.Clear;
    RuntimePage.Add(RuntimeUrl, RuntimeFile, '');
    RuntimePage.Show;
    try
      try
        RuntimePage.Download;
      except
        if RuntimePage.AbortedByUser then
          Result := FmtMessage(CustomMessage('RuntimeDownloadFailed'), ['취소함'])
        else
          Result := FmtMessage(CustomMessage('RuntimeDownloadFailed'), [GetExceptionMessage]);
        Log(Result);
        Exit;
      end;
      RuntimePage.SetText(CustomMessage('RuntimeInstalling'), '');
      RuntimePage.ProgressBar.Style := npbstMarquee;
      Result := InstallRuntime(Path);
    finally
      RuntimePage.ProgressBar.Style := npbstNormal;
      RuntimePage.Hide;
    end;
  end
  else
  begin
    try
      DownloadTemporaryFile(RuntimeUrl, RuntimeFile, '', nil);
    except
      Result := FmtMessage(CustomMessage('RuntimeDownloadFailed'), [GetExceptionMessage]);
      Log(Result);
      Exit;
    end;
    Result := InstallRuntime(Path);
  end;
end;

procedure InitializeWizard();
begin
  RuntimePage := CreateDownloadPage(CustomMessage('RuntimeDownloadTitle'), CustomMessage('RuntimeDownloadDesc'), nil);
  RuntimePage.ShowBaseNameInsteadOfUrl := True;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  { 런타임을 먼저 (실패하면 몽독을 끄지 않고 설치 중단 — 자동 업데이트면 앱이 "다시 시도" 를 보여 준다).
    대화형 설치는 준비 화면 다음 단계에서 이미 끝났으므로 여기서는 확인만 된다. }
  Result := EnsureDesktopRuntime(False);
  if Result <> '' then
    Exit;
  CloseMongdock();
  if IsMongdockRunning() then
    Result := 'mongdock 을 종료하지 못했습니다. 트레이 아이콘 오른쪽 클릭 → 종료 후 다시 시도하세요.'
  else
    Result := '';
end;

{ 현재 사용자의 "윈도우 시작 시 실행" 로그온 작업 이름 }
function StartupTaskName(): String;
begin
  Result := StartupTaskPrefix + GetUserNameString();
end;

{ 로그온 작업이 있는지 (비관리자도 자기 작업은 조회 가능) }
function StartupTaskExists(): Boolean;
var
  RC: Integer;
begin
  Result := Exec(ExpandConstant('{sys}\schtasks.exe'), '/Query /TN "' + StartupTaskName() + '"', '', SW_HIDE, ewWaitUntilTerminated, RC) and (RC = 0);
end;

{ 로그온 작업 삭제 (없으면 아무 일 없음). 비관리자도 자기가 만든 작업은 지울 수 있다 }
procedure DeleteStartupTask();
var
  RC: Integer;
begin
  if Exec(ExpandConstant('{sys}\schtasks.exe'), '/Delete /TN "' + StartupTaskName() + '" /F', '', SW_HIDE, ewWaitUntilTerminated, RC) then
    Log('시작 작업 삭제 (schtasks 종료 코드 ' + IntToStr(RC) + ')');
end;

var
  { 설치 시작 때 자동 실행(Run 값 또는 로그온 작업)이 이미 등록돼 있었는지 (앱 메뉴·설정 창에서 켠 것 포함) }
  HadAutoStart: Boolean;
  { 작업 선택 화면을 실제로 보고 다음으로 넘어갔는지 (= 체크 상태가 사용자의 명시적 선택) }
  TasksPageConfirmed: Boolean;
  AutoStartPreselected: Boolean;

function InitializeSetup(): Boolean;
begin
  HadAutoStart := RegValueExists(HKEY_CURRENT_USER, RunKey, 'mongdock') or StartupTaskExists();
  TasksPageConfirmed := False;
  AutoStartPreselected := False;
  Result := True;
end;

procedure CurPageChanged(CurPageID: Integer);
begin
  { 이미 자동 실행 중이면 (이전 설치 때 체크를 껐더라도) 체크된 상태로 시작한다. 처음 한 번만 — 사용자가 끈 뒤 뒤로 갔다 오면 존중 }
  if (CurPageID = wpSelectTasks) and HadAutoStart and not AutoStartPreselected then
  begin
    WizardSelectTasks('autostart');
    AutoStartPreselected := True;
  end;
end;

function NextButtonClick(CurPageID: Integer): Boolean;
var
  Err: String;
begin
  if CurPageID = wpSelectTasks then
    TasksPageConfirmed := True;
  Result := True;
  { 대화형 설치: "설치" 를 누르면 (필요할 때만) 다운로드 페이지로 런타임 준비. 실패하면 준비 화면에 머문다 (다시 시도/취소).
    조용한 설치는 PrepareToInstall 에서 화면 없이 처리 }
  if (CurPageID = wpReady) and not WizardSilent() then
  begin
    Err := EnsureDesktopRuntime(True);
    if Err <> '' then
    begin
      SuppressibleMsgBox(Err, mbCriticalError, MB_OK, IDOK);
      Result := False;
    end;
  end;
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  RC: Integer;
begin
  { 아이콘이 바뀌어도 시작 메뉴·작업 표시줄이 예전 그림(아이콘 캐시)을 계속 보여 주는 문제 → 사용자 아이콘 캐시 새로 고침 }
  if CurStep = ssPostInstall then
    Exec(ExpandConstant('{sys}\ie4uinit.exe'), '-show', '', SW_HIDE, ewWaitUntilTerminated, RC);
  { 자동 실행 체크: [Registry] 가 써 둔 Run 값을 작업 스케줄러 로그온 작업으로 옮긴다 (실행 경로도 지금 설치 위치로 갱신).
    작업 등록이 안 되면 앱이 Run 값을 그대로 둔다 (대체). 마침 화면의 "mongdock 실행"([Run])보다 먼저 끝난다. }
  if (CurStep = ssPostInstall) and WizardIsTaskSelected('autostart') then
  begin
    if Exec(ExpandConstant('{app}\{#AppExe}'), '--register-startup', ExpandConstant('{app}'), SW_HIDE, ewWaitUntilTerminated, RC) then
      Log('시작 작업 등록 (종료 코드 ' + IntToStr(RC) + ', 0 = 작업 스케줄러 / 1 = Run 키로 대체)')
    else
      Log('시작 작업 등록 실행 실패 — Run 값 유지');
  end;
  { 자동 실행 체크가 꺼져 있을 때 Run 값·로그온 작업을 지우는 건, 처음부터 없었거나 사용자가 작업 선택 화면에서 직접 끈 경우뿐.
    조용한 설치(/SILENT) 등으로 화면을 거치지 않은 업그레이드에서는 앱에서 켜 둔 자동 실행을 그대로 둔다. }
  if (CurStep = ssPostInstall) and not WizardIsTaskSelected('autostart') then
    if (not HadAutoStart) or TasksPageConfirmed then
    begin
      RegDeleteValue(HKEY_CURRENT_USER, RunKey, 'mongdock');
      if HadAutoStart then
        DeleteStartupTask();
    end;
end;

{ UTF-8 바이트(settings.json, BOM 없음) → 문자열 }
function Utf8ToStr(const S: AnsiString): String;
var
  Len: Integer;
begin
  Result := '';
  if Length(S) = 0 then
    Exit;
  Len := MultiByteToWideChar(CP_UTF8, 0, S, Length(S), Result, 0);
  if Len <= 0 then
    Exit;
  SetLength(Result, Len);
  if MultiByteToWideChar(CP_UTF8, 0, S, Length(S), Result, Len) <> Len then
    Result := '';
end;

{ %SystemRoot% 같은 환경 변수 펼치기 (없으면 그대로) }
function ExpandEnv(const S: String): String;
var
  Buf: String;
  N: DWORD;
begin
  Result := S;
  if Pos('%', S) = 0 then
    Exit;
  SetLength(Buf, 2048);
  N := ExpandEnvironmentStrings(S, Buf, 2048);
  if (N > 0) and (N <= 2048) then
    Result := Copy(Buf, 1, N - 1);
end;

function IsJsonSpace(C: Char): Boolean;
begin
  Result := (C = ' ') or (C = #9) or (C = #13) or (C = #10);
end;

{ settings.json 의 notifications.originalSound 문자열 값 (몽독이 처음 소리를 바꾸기 전의 레지스트리 값).
  키가 없거나 null 이거나 읽을 수 없으면 False. 몽독 설정에서 이 키 이름은 하나뿐이라 단순 검색으로 찾는다. }
function ReadOriginalSound(const Path: String; var Value: String): Boolean;
var
  Raw: AnsiString;
  Text: String;
  P, I, L: Integer;
  C: Char;
begin
  Result := False;
  Value := '';
  if not FileExists(Path) then
    Exit;
  if not LoadStringFromFile(Path, Raw) then
    Exit;
  Text := Utf8ToStr(Raw);
  P := Pos('"originalsound"', Lowercase(Text));
  if P = 0 then
    Exit;
  L := Length(Text);
  I := P + Length('"originalsound"');
  while (I <= L) and IsJsonSpace(Text[I]) do
    I := I + 1;
  if (I > L) or (Text[I] <> ':') then
    Exit;
  I := I + 1;
  while (I <= L) and IsJsonSpace(Text[I]) do
    I := I + 1;
  { null(아직 바꾼 적 없음) 등 문자열이 아니면 포기 }
  if (I > L) or (Text[I] <> '"') then
    Exit;
  I := I + 1;
  while I <= L do
  begin
    C := Text[I];
    if C = '"' then
    begin
      Result := True;
      Exit;
    end;
    if C = '\' then
    begin
      I := I + 1;
      if I > L then
        Exit;
      C := Text[I];
      case C of
        'n': Value := Value + #10;
        'r': Value := Value + #13;
        't': Value := Value + #9;
        'b', 'f', 'u': Exit; { 경로에는 나오지 않음 → 포기하고 .Default 로 }
      else
        Value := Value + C; { \\ \" \/ }
      end;
    end
    else
      Value := Value + C;
    I := I + 1;
  end;
end;

{ 몽독이 바꿔 둔 윈도우 알림 소리를 되돌린다.
  .Current 가 몽독 파일(%APPDATA%\mongdock\sounds\notification.wav)을 가리킬 때만 (그 뒤 사용자가 직접 바꿨으면 그대로 둔다):
  settings.json 의 originalSound 가 있으면 그 값, 없으면 같은 키의 .Default 값을 .Current 에 (REG_EXPAND_SZ). }
procedure RestoreNotificationSound();
var
  Cur, Managed, Original, Def: String;
begin
  if not RegQueryStringValue(HKEY_CURRENT_USER, SoundCurrentKey, '', Cur) then
    Exit;
  Managed := ExpandConstant('{userappdata}\mongdock\sounds\notification.wav');
  if (CompareText(Cur, Managed) <> 0) and (CompareText(ExpandEnv(Cur), Managed) <> 0) then
    Exit;

  if ReadOriginalSound(ExpandConstant('{userappdata}\mongdock\settings.json'), Original) then
  begin
    if Pos('%', Original) > 0 then
      RegWriteExpandStringValue(HKEY_CURRENT_USER, SoundCurrentKey, '', Original)
    else
      RegWriteStringValue(HKEY_CURRENT_USER, SoundCurrentKey, '', Original);
    Log('알림 소리 원래대로 (settings.json originalSound): "' + Original + '"');
  end
  else if RegQueryStringValue(HKEY_CURRENT_USER, SoundDefaultKey, '', Def) then
  begin
    RegWriteExpandStringValue(HKEY_CURRENT_USER, SoundCurrentKey, '', Def);
    Log('알림 소리 윈도우 기본값으로 (.Default): "' + Def + '"');
  end
  else
    Log('알림 소리: 원래 값과 .Default 를 찾지 못해 그대로 둠');
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  DataDir: String;
begin
  if CurUninstallStep = usUninstall then
  begin
    CloseMongdock();
    RestoreTaskbarAutoHide();
    { 앱 메뉴에서 켠 자동 실행도 같은 값·작업이므로 설치 때 선택 여부와 상관없이 지운다 }
    RegDeleteValue(HKEY_CURRENT_USER, RunKey, 'mongdock');
    DeleteStartupTask();
    { 설정 폴더(originalSound 가 든 settings.json)를 지울지 묻기 전에 }
    RestoreNotificationSound();
  end;

  if CurUninstallStep = usPostUninstall then
  begin
    DataDir := ExpandConstant('{userappdata}\mongdock');
    if DirExists(DataDir) and not UninstallSilent() then
      if MsgBox(CustomMessage('DeleteSettingsPrompt'), mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES then
        DelTree(DataDir, True, True, True);
  end;
end;
