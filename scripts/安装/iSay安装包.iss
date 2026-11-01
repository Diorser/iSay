#ifndef Layout
  #error 请通过 scripts/发布/构建Windows发行.ps1 提供发行参数
#endif

[Setup]
AppId={{7CC92146-214A-4D84-B24A-1E1C3C52881E}
AppName=iSay
AppVersion={#ProductVersion}
AppPublisher=iSay
DefaultDirName={localappdata}\Programs\iSayVoiceDesk
DefaultGroupName=iSay
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64
ArchitecturesInstallIn64BitMode=x64
MinVersion=10.0.19041
WizardStyle=modern
Compression=lzma2/normal
SolidCompression=yes
LZMANumBlockThreads=1
LZMADictionarySize=16384
OutputDir={#OutputDirectory}
OutputBaseFilename=iSay-{#ProductVersion}-win-x64-full
SetupIconFile={#Layout}\iSay.ico
UninstallDisplayIcon={app}\iSay.ico
CloseApplications=no
RestartApplications=no
SetupLogging=yes
UsePreviousLanguage=yes

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"
Name: "chinesesimp"; MessagesFile: "{#ChineseLanguageFile}"

[Tasks]
Name: "desktopicon"; Description: "{cm:DesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"
Name: "startup"; Description: "{cm:Startup}"; GroupDescription: "{cm:AdditionalIcons}"

[CustomMessages]
english.DesktopIcon=Create a desktop shortcut
english.Startup=Start iSay when I sign in
english.CloseApp=Please exit iSay from the tray menu before installing. Other applications will remain open.
english.RuntimeError=A bundled runtime component could not be installed. Installation has stopped. You can retry after resolving the runtime installer error.
english.RuntimeReboot=The runtime installer requires a restart. Restart Windows, then run this installer again.
english.RuntimeHashError=The bundled runtime failed verification. Download the complete installer again from a trusted source.
english.ComponentError=The input component could not be registered for the current user. Installation is incomplete. See the installer log.
english.ComponentVersionError=The installed input component has different contents under the same version. Installation stopped to preserve the loaded component.
english.ProgressTitle=Preparing iSay
english.ProgressDescription=Installing bundled components and enabling voice input
english.ProgressDetail=Please wait. This step runs in the background and may take several minutes. Windows may request administrator permission.%nDiagnostic logs: %1
english.StageVcX64=Preparing Microsoft Visual C++ (x64)
english.StageVcX86=Preparing Microsoft Visual C++ (x86)
english.StageDesktop=Preparing Microsoft .NET Desktop Runtime
english.StageLanguages=Reading the current user's input languages
english.StageSystem=Registering the system input component
english.StageUser=Enabling the current user's input component
english.FailureStage=Failed step: %1
english.FailureCode=Process exit code: %1
english.FailureLaunch=The process could not be started: %1
english.FailureLogs=Diagnostic logs: %1
english.LogError=The installer could not create diagnostic logs. Check that your user profile is writable, then retry.
english.LanguageError=The current user's input language list is missing or invalid. Installation has stopped before system registration.
english.RuntimeVerifyError=The installer exited successfully, but the required runtime was not found. Restart Windows if requested, then retry.
chinesesimp.DesktopIcon=创建桌面快捷方式
chinesesimp.Startup=登录 Windows 时启动 iSay
chinesesimp.CloseApp=请先从托盘菜单退出 iSay，再继续安装。其他应用会保持打开。
chinesesimp.RuntimeError=随包运行组件未能安装，安装已停止。处理运行组件安装错误后可重试。
chinesesimp.RuntimeReboot=运行时安装器要求重启。请重启 Windows，再次运行此安装包。
chinesesimp.RuntimeHashError=随包运行时校验失败，请从可信来源重新下载完整安装包。
chinesesimp.ComponentError=未能为当前用户登记输入组件，安装尚未完成。请查看安装日志。
chinesesimp.ComponentVersionError=已安装的同版本输入组件内容不同，已停止安装以保留正在加载的组件。
chinesesimp.ProgressTitle=正在准备 iSay
chinesesimp.ProgressDescription=安装随包组件并启用语音输入
chinesesimp.ProgressDetail=请稍候。此步骤在后台执行，可能需要几分钟。Windows 可能要求管理员授权。%n诊断日志：%1
chinesesimp.StageVcX64=准备 Microsoft Visual C++（x64）
chinesesimp.StageVcX86=准备 Microsoft Visual C++（x86）
chinesesimp.StageDesktop=准备 Microsoft .NET 桌面运行时
chinesesimp.StageLanguages=读取当前用户的输入语言
chinesesimp.StageSystem=登记系统输入组件
chinesesimp.StageUser=启用当前用户的输入组件
chinesesimp.FailureStage=失败步骤：%1
chinesesimp.FailureCode=进程退出码：%1
chinesesimp.FailureLaunch=未能启动进程：%1
chinesesimp.FailureLogs=诊断日志：%1
chinesesimp.LogError=无法创建安装诊断日志，请检查当前用户目录是否可写后重试。
chinesesimp.LanguageError=当前用户的输入语言列表缺失或无效，已在系统登记前停止安装。
chinesesimp.RuntimeVerifyError=安装器已成功退出，但未检测到所需运行时。如提示需要重启，请重启后重试。

[Files]
Source: "{#RuntimeInstaller}"; DestName: "windowsdesktop-runtime.exe"; Flags: dontcopy noencryption
Source: "{#VcX64Installer}"; DestName: "vc_redist.x64.exe"; Flags: dontcopy noencryption
Source: "{#VcX86Installer}"; DestName: "vc_redist.x86.exe"; Flags: dontcopy noencryption
Source: "{#Layout}\*"; DestDir: "{app}"; Excludes: "composition\*"; Flags: ignoreversion recursesubdirs createallsubdirs
; 旧 TSF 可能仍被其他进程加载，同版本不覆盖，卸载时留到下次登录后手工清理。
Source: "{#Layout}\composition\{#CompositionVersion}\*"; DestDir: "{app}\composition\{#CompositionVersion}"; Flags: onlyifdoesntexist recursesubdirs createallsubdirs uninsneveruninstall
Source: "{#Layout}\composition\iSay.TextService.Setup.exe"; DestDir: "{app}\composition"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\iSay"; Filename: "{app}\iSay.exe"
Name: "{autodesktop}\iSay"; Filename: "{app}\iSay.exe"; Tasks: desktopicon

[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "iSayVoiceDesk"; ValueData: """{app}\iSay.exe"" --background"; Tasks: startup; Flags: uninsdeletevalue
; COM 路径始终登记到启动安装器的原用户，不放在提升的子进程中。
Root: HKCU64; Subkey: "Software\Classes\CLSID\{{429952CD-D1B7-4769-858A-6D56E4EF488D}\InprocServer32"; ValueType: string; ValueData: "{app}\composition\{#CompositionVersion}\x64\iSay.TextService.dll"; Flags: uninsdeletekey
Root: HKCU64; Subkey: "Software\Classes\CLSID\{{429952CD-D1B7-4769-858A-6D56E4EF488D}\InprocServer32"; ValueType: string; ValueName: "ThreadingModel"; ValueData: "Apartment"
Root: HKCU32; Subkey: "Software\Classes\CLSID\{{429952CD-D1B7-4769-858A-6D56E4EF488D}\InprocServer32"; ValueType: string; ValueData: "{app}\composition\{#CompositionVersion}\x86\iSay.TextService.dll"; Flags: uninsdeletekey
Root: HKCU32; Subkey: "Software\Classes\CLSID\{{429952CD-D1B7-4769-858A-6D56E4EF488D}\InprocServer32"; ValueType: string; ValueName: "ThreadingModel"; ValueData: "Apartment"

[Run]
Filename: "{app}\iSay.exe"; Description: "iSay"; Flags: nowait postinstall skipifsilent runasoriginaluser; Check: CanStartApplication

[UninstallRun]
Filename: "{app}\composition\iSay.TextService.Setup.exe"; Parameters: "--deactivate"; Flags: runhidden waituntilterminated skipifdoesntexist
Filename: "{app}\composition\iSay.TextService.Setup.exe"; Parameters: "--uninstall-user"; Flags: runhidden waituntilterminated skipifdoesntexist

[Code]
var
  RuntimeNeedsRestart: Boolean;
  ComponentReady: Boolean;
  DiagnosticDirectory: String;
  ProgressPage: TOutputMarqueeProgressWizardPage;

function GetTickCount64: Int64;
  external 'GetTickCount64@kernel32.dll stdcall';
function GetCurrentProcessId: Cardinal;
  external 'GetCurrentProcessId@kernel32.dll stdcall';

procedure Diagnostic(Message: String);
var
  Lines: TArrayOfString;
begin
  Log(Message);
  if DiagnosticDirectory = '' then Exit;
  SetArrayLength(Lines, 1);
  Lines[0] := GetDateTimeString('yyyy-mm-dd hh:nn:ss', '-', ':') + ' ' + Message;
  if not SaveStringsToUTF8File(AddBackslash(DiagnosticDirectory) + '安装汇总.log', Lines, True) then
    Log('无法写入安装汇总日志。');
end;

procedure CopySetupLog;
var
  Source: String;
begin
  if DiagnosticDirectory = '' then Exit;
  Source := ExpandConstant('{log}');
  if (Source <> '') and FileExists(Source) then
    if not FileCopy(Source, AddBackslash(DiagnosticDirectory) + 'Inno安装.log', False) then
      Log('安装主日志仍在：' + Source);
end;

function StageFailure(Heading, Stage, Details: String): String;
begin
  Result := Heading + #13#10 + FmtMessage(CustomMessage('FailureStage'), [Stage]);
  if Details <> '' then Result := Result + #13#10 + Details;
  Result := Result + #13#10 + FmtMessage(CustomMessage('FailureLogs'), [DiagnosticDirectory]);
  Diagnostic('失败：' + Stage + '；' + Details);
  CopySetupLog;
end;

function ExitCodeText(Code: Integer): String;
begin
  Result := IntToStr(Code) + ' (0x' + Format('%.8x', [Code]) + ')';
end;

function LogTail(Path: String): String;
var
  Lines: TArrayOfString;
  Index, First: Integer;
begin
  Result := '';
  if not LoadStringsFromFile(Path, Lines) then Exit;
  First := GetArrayLength(Lines) - 4;
  if First < 0 then First := 0;
  for Index := First to GetArrayLength(Lines) - 1 do
    if Trim(Lines[Index]) <> '' then Result := Result + #13#10 + Copy(Lines[Index], 1, 300);
end;

function PrepareChildLog(Path, Stage: String): Boolean;
var
  Lines: TArrayOfString;
begin
  { 先由原用户创建文件，提权阶段和用户阶段各写各的日志，避免覆盖或跨账户创建文件。 }
  SetArrayLength(Lines, 0);
  Result := SaveStringsToUTF8File(Path, Lines, False);
  if not Result then Diagnostic('无法创建步骤日志：' + Stage + '；' + Path);
end;

function RunStage(Stage, FileName, Parameters, ChildLog: String; Elevated: Boolean;
  var ResultCode: Integer): Boolean;
var
  Started: Int64;
begin
  Result := False;
  ResultCode := -1;
  Started := GetTickCount64;
  Diagnostic('开始：' + Stage + '；日志：' + ChildLog);
  ProgressPage.SetText(Stage, FmtMessage(CustomMessage('ProgressDetail'), [DiagnosticDirectory]));
  ProgressPage.Show;
  ProgressPage.Animate;
  try
    try
      if Elevated then
        Result := ShellExec('runas', FileName, Parameters, '', SW_HIDE, ewWaitUntilTerminated, ResultCode)
      else
        Result := ExecAsOriginalUser(FileName, Parameters, '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
    except
      Diagnostic('步骤启动异常：' + Stage + '；' + GetExceptionMessage);
    end;
  finally
    ProgressPage.Hide;
    Diagnostic('结束：' + Stage + '；已启动=' + IntToStr(Ord(Result)) +
      '；代码=' + ExitCodeText(ResultCode) + '；耗时毫秒=' + IntToStr(GetTickCount64 - Started));
    CopySetupLog;
  end;
end;

function ProcessFailure(Heading, Stage, ChildLog: String; Started: Boolean; Code: Integer): String;
var
  Details: String;
begin
  if Started then Details := FmtMessage(CustomMessage('FailureCode'), [ExitCodeText(Code)])
  else Details := FmtMessage(CustomMessage('FailureLaunch'), [ExitCodeText(Code) + ' ' + SysErrorMessage(Code)]);
  Result := StageFailure(Heading, Stage, Details + LogTail(ChildLog));
end;

procedure InitializeWizard;
begin
  DiagnosticDirectory := ExpandConstant('{localappdata}\iSay\安装日志\') +
    GetDateTimeString('yyyymmdd-hhnnss', '-', ':') + '-' + IntToStr(GetCurrentProcessId);
  if not ForceDirectories(DiagnosticDirectory) then RaiseException(CustomMessage('LogError'));
  if not PrepareChildLog(AddBackslash(DiagnosticDirectory) + '安装汇总.log', '初始化') then
    RaiseException(CustomMessage('LogError'));
  ProgressPage := CreateOutputMarqueeProgressPage(CustomMessage('ProgressTitle'), CustomMessage('ProgressDescription'));
  Diagnostic('安装开始；产品={#ProductVersion}；输入组件={#CompositionVersion}；原用户=' + GetUserNameString);
end;

procedure DeinitializeSetup;
begin
  Diagnostic('安装退出；输入组件就绪=' + IntToStr(Ord(ComponentReady)) +
    '；需要重启=' + IntToStr(Ord(RuntimeNeedsRestart)));
  CopySetupLog;
end;

function RuntimeVersionSupported(Version: String): Boolean;
var
  Installed, Minimum: Int64;
begin
  Result := False;
  if Copy(Version, 1, 5) <> '10.0.' then Exit;
  if Pos('-', Version) <> 0 then Exit;
  if StrToVersion(Version, Installed) and StrToVersion('{#MinimumRuntimeVersion}', Minimum) then
    Result := ComparePackedVersion(Installed, Minimum) >= 0;
end;

function HasDesktopRuntimeInView(Root: Integer): Boolean;
var
  Versions: TArrayOfString;
  Index: Integer;
  Location, Framework: String;
begin
  Result := False;
  if not RegQueryStringValue(Root, 'SOFTWARE\dotnet\Setup\InstalledVersions\x64', 'InstallLocation', Location) then Exit;
  if not RegGetValueNames(Root, 'SOFTWARE\dotnet\Setup\InstalledVersions\x64\sharedfx\Microsoft.WindowsDesktop.App', Versions) then Exit;
  for Index := 0 to GetArrayLength(Versions) - 1 do
    if RuntimeVersionSupported(Versions[Index]) then begin
      Framework := AddBackslash(Location) + 'shared\Microsoft.WindowsDesktop.App\' + Versions[Index] + '\';
      if FileExists(Framework + 'PresentationFramework.dll') and FileExists(Framework + 'WindowsBase.dll') then begin
        Result := True;
        Exit;
      end;
    end;
end;

function HasDesktopRuntime: Boolean;
begin
  Result := HasDesktopRuntimeInView(HKLM32) or HasDesktopRuntimeInView(HKLM64);
end;

function HasVcRuntimeInView(Root: Integer; Architecture, MinimumVersion: String): Boolean;
var
  Installed: Cardinal;
  Version: String;
  CurrentPacked, MinimumPacked: Int64;
begin
  Result := False;
  if RegQueryDWordValue(Root, 'SOFTWARE\Microsoft\VisualStudio\14.0\VC\Runtimes\' + Architecture, 'Installed', Installed) and (Installed = 1) and
    RegQueryStringValue(Root, 'SOFTWARE\Microsoft\VisualStudio\14.0\VC\Runtimes\' + Architecture, 'Version', Version) then begin
    if Copy(Version, 1, 1) = 'v' then Delete(Version, 1, 1);
    if StrToVersion(Version, CurrentPacked) and StrToVersion(MinimumVersion, MinimumPacked) then
      Result := ComparePackedVersion(CurrentPacked, MinimumPacked) >= 0;
  end;
end;

function HasVcRuntime(Architecture, MinimumVersion: String): Boolean;
begin
  Result := HasVcRuntimeInView(HKLM32, Architecture, MinimumVersion) or
    HasVcRuntimeInView(HKLM64, Architecture, MinimumVersion);
end;

function InstallRuntime(Stage, RuntimePath, ExpectedHash, ChildLog: String): String;
var
  Started: Boolean;
  ResultCode: Integer;
begin
  Result := '';
  if GetSHA256OfFile(RuntimePath) <> ExpectedHash then begin
    Result := StageFailure(CustomMessage('RuntimeHashError'), Stage, ''); Exit;
  end;
  if not PrepareChildLog(ChildLog, Stage) then begin
    Result := StageFailure(CustomMessage('LogError'), Stage, ''); Exit;
  end;
  Started := RunStage(Stage, RuntimePath, '/install /quiet /norestart /log "' + ChildLog + '"', ChildLog, True, ResultCode);
  if not Started then begin
    Result := ProcessFailure(CustomMessage('RuntimeError'), Stage, ChildLog, False, ResultCode); Exit;
  end;
  if ResultCode = 3010 then RuntimeNeedsRestart := True;
  if ResultCode = 1641 then begin
    RuntimeNeedsRestart := True;
    Result := ProcessFailure(CustomMessage('RuntimeReboot'), Stage, ChildLog, True, ResultCode); Exit;
  end;
  if (ResultCode <> 0) and (ResultCode <> 3010) then
    Result := ProcessFailure(CustomMessage('RuntimeError'), Stage, ChildLog, True, ResultCode);
end;

function EnsureVcRuntime(Architecture, MinimumVersion, ExpectedHash: String): String;
var
  Stage, RuntimePath, ChildLog: String;
begin
  Result := '';
  if Architecture = 'x64' then Stage := CustomMessage('StageVcX64') else Stage := CustomMessage('StageVcX86');
  if HasVcRuntime(Architecture, MinimumVersion) then begin
    Diagnostic('跳过：' + Stage + '；已满足最低版本 ' + MinimumVersion); Exit;
  end;
  ExtractTemporaryFile('vc_redist.' + Architecture + '.exe');
  RuntimePath := ExpandConstant('{tmp}\vc_redist.') + Architecture + '.exe';
  ChildLog := AddBackslash(DiagnosticDirectory) + 'vc-' + Architecture + '.log';
  Result := InstallRuntime(Stage, RuntimePath, ExpectedHash, ChildLog);
  if (Result = '') and not HasVcRuntime(Architecture, MinimumVersion) then begin
    if RuntimeNeedsRestart then Result := StageFailure(CustomMessage('RuntimeReboot'), Stage, '')
    else Result := StageFailure(CustomMessage('RuntimeVerifyError'), Stage, LogTail(ChildLog));
  end;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  RuntimePath, Existing: String;
begin
  Result := '';
  if CheckForMutexes('Local\iSayVoiceDesk') then begin
    Result := CustomMessage('CloseApp');
    Exit;
  end;
  Existing := ExpandConstant('{app}\composition\{#CompositionVersion}\x64\iSay.TextService.dll');
  if FileExists(Existing) and (GetSHA256OfFile(Existing) <> '{#TsfX64SHA256}') then begin
    Result := CustomMessage('ComponentVersionError'); Exit;
  end;
  Existing := ExpandConstant('{app}\composition\{#CompositionVersion}\x86\iSay.TextService.dll');
  if FileExists(Existing) and (GetSHA256OfFile(Existing) <> '{#TsfX86SHA256}') then begin
    Result := CustomMessage('ComponentVersionError'); Exit;
  end;
  Result := EnsureVcRuntime('x64', '{#VcX64Version}', '{#VcX64SHA256}');
  if Result <> '' then begin NeedsRestart := RuntimeNeedsRestart; Exit; end;
  Result := EnsureVcRuntime('x86', '{#VcX86Version}', '{#VcX86SHA256}');
  if Result <> '' then begin NeedsRestart := RuntimeNeedsRestart; Exit; end;
  if HasDesktopRuntime then begin
    Diagnostic('跳过：' + CustomMessage('StageDesktop') + '；已满足最低版本 {#MinimumRuntimeVersion}'); Exit;
  end;
  ExtractTemporaryFile('windowsdesktop-runtime.exe');
  RuntimePath := ExpandConstant('{tmp}\windowsdesktop-runtime.exe');
  Result := InstallRuntime(CustomMessage('StageDesktop'), RuntimePath, '{#RuntimeSHA256}',
    AddBackslash(DiagnosticDirectory) + 'desktop-runtime.log');
  NeedsRestart := RuntimeNeedsRestart;
  if Result <> '' then Exit;
  if not HasDesktopRuntime then begin
    NeedsRestart := RuntimeNeedsRestart;
    if RuntimeNeedsRestart then Result := StageFailure(CustomMessage('RuntimeReboot'), CustomMessage('StageDesktop'), '')
    else Result := StageFailure(CustomMessage('RuntimeVerifyError'), CustomMessage('StageDesktop'), '');
  end;
end;

function ReadUserLanguages(Path: String; var Languages: String): Boolean;
var
  Lines: TArrayOfString;
  Index, Start, Value, Count: Integer;
  Token, Seen: String;
begin
  Result := False;
  Languages := '';
  if not LoadStringsFromFile(Path, Lines) then Exit;
  if GetArrayLength(Lines) = 0 then Exit;
  Languages := Lines[0];
  if (Length(Languages) = 0) or (Length(Languages) > 767) then Exit;
  for Index := 1 to GetArrayLength(Lines) - 1 do if Lines[Index] <> '' then Exit;
  Seen := ',';
  Start := 1;
  Count := 0;
  for Index := 1 to Length(Languages) + 1 do begin
    if Index <= Length(Languages) then
      if (Languages[Index] <> ',') and ((Languages[Index] < '0') or (Languages[Index] > '9')) then Exit;
    if Index <= Length(Languages) then begin
      if Languages[Index] <> ',' then Continue;
    end;
    Token := Copy(Languages, Start, Index - Start);
    Value := StrToIntDef(Token, 0);
    if (Value < 1) or (Value > 65534) or (Pos(',' + IntToStr(Value) + ',', Seen) <> 0) then Exit;
    Seen := Seen + IntToStr(Value) + ',';
    Count := Count + 1;
    if Count > 128 then Exit;
    Start := Index + 1;
  end;
  Result := Count > 0;
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  ResultCode: Integer;
  SetupPath, ComponentPath, LanguageLog, SystemLog, UserLog, Languages, Stage: String;
  Started: Boolean;
begin
  if CurStep = ssPostInstall then begin
    SetupPath := ExpandConstant('{app}\composition\iSay.TextService.Setup.exe');
    ComponentPath := ExpandConstant('{app}\composition\{#CompositionVersion}');
    LanguageLog := AddBackslash(DiagnosticDirectory) + 'user-languages.txt';
    SystemLog := AddBackslash(DiagnosticDirectory) + 'tsf-system.jsonl';
    UserLog := AddBackslash(DiagnosticDirectory) + 'tsf-user.jsonl';
    Stage := CustomMessage('StageLanguages');
    if not PrepareChildLog(LanguageLog, Stage) then RaiseException(StageFailure(CustomMessage('LogError'), Stage, ''));
    { 语言由原用户读取，管理员代输凭据不能改变要登记的语言集合。 }
    Started := RunStage(Stage, SetupPath, '--list-languages --log "' + LanguageLog + '"', LanguageLog, False, ResultCode);
    if not Started or (ResultCode <> 0) then
      RaiseException(ProcessFailure(CustomMessage('ComponentError'), Stage, LanguageLog, Started, ResultCode));
    if not ReadUserLanguages(LanguageLog, Languages) then
      RaiseException(StageFailure(CustomMessage('LanguageError'), Stage, LogTail(LanguageLog)));
    Diagnostic('原用户语言：' + Languages);
    Stage := CustomMessage('StageSystem');
    if not PrepareChildLog(SystemLog, Stage) then RaiseException(StageFailure(CustomMessage('LogError'), Stage, ''));
    { 系统类别需要管理员权限；这里的固定动作不写原用户的 COM、设置或词库。 }
    Started := RunStage(Stage, SetupPath, '--prepare-system "' + ComponentPath + '" --languages "' + Languages + '" --log "' + SystemLog + '"', SystemLog, True, ResultCode);
    if not Started or (ResultCode <> 0) then
      RaiseException(ProcessFailure(CustomMessage('ComponentError'), Stage, SystemLog, Started, ResultCode));
    Stage := CustomMessage('StageUser');
    if not PrepareChildLog(UserLog, Stage) then RaiseException(StageFailure(CustomMessage('LogError'), Stage, ''));
    { 用户配置单独在原用户令牌下登记，不切换系统默认键盘。 }
    Started := RunStage(Stage, SetupPath, '--install-user "' + ComponentPath + '" --log "' + UserLog + '"', UserLog, False, ResultCode);
    ComponentReady := Started and (ResultCode = 0);
    if not ComponentReady then
      RaiseException(ProcessFailure(CustomMessage('ComponentError'), Stage, UserLog, Started, ResultCode));
  end;
end;

function NeedRestart: Boolean;
begin
  Result := RuntimeNeedsRestart;
end;

function CanStartApplication: Boolean;
begin
  Result := ComponentReady and not RuntimeNeedsRestart;
end;
