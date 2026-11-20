; Inno Setup script of the QoffeeAutomat agent (PLAN.md D-019, S09, S11, S13, task T601).
;
; Build it with build-installer.ps1, which publishes nothing itself: run publish.ps1 first, it stages the files. The
; script reads its inputs from environment variables, so that no quoting of command-line defines is needed:
;   QOFFEE_APP_VERSION      SemVer, for example 0.1.0 or 0.1.0-ci.1 (required)
;   QOFFEE_VERSION_NUMERIC  four numbers for the version resource, for example 0.1.0.0 (required)
;   QOFFEE_ARCH             x64 (default) or arm64
;   QOFFEE_SOURCE_DIR       the staging directory (default ..\..\artifacts\packaging\stage\win-<arch>)
;   QOFFEE_OUTPUT_DIR       where the installer goes (default ..\..\artifacts\packaging)
;
; What the installer does:
;   * installs below Program Files (any other directory is refused: the service runs as LocalSystem and starts every
;     file in the directory, so standard users must not be able to change it, PLAN.md S09),
;   * lets "qoffeeautomat service install" create the data directories below %ProgramData%\QoffeeAutomat with their
;     ACLs (SYSTEM and Administrators only, S05/S09) and register the service (LocalSystem, delayed automatic start,
;     restart on failure, Event Log source), then starts it,
;   * opens the QoffeeLink port (default 8481) for the private and domain firewall profiles only (S09 item 9),
;   * adds a Start menu shortcut to the UI (http://127.0.0.1:<ui port>/) and shows where the setup token is,
;   * upgrades in place: stops the service and every SessionHost, replaces the files (the wwwroot folder is cleared first
;     because the UI files carry hashes in their names), starts the service again; configuration, secrets and data stay,
;   * uninstalls the service, the firewall rule and the files, and asks whether to delete the data (default: keep).
;
; Silent installation (needs an elevated process):
;   QoffeeAutomat-Setup-<version>-<arch>.exe /VERYSILENT /SUPPRESSMSGBOXES /NORESTART [/LOG=<file>]
;       [/LINKPORT=<port>] [/UIPORT=<port>] [/HUBENROLL=<enrollment string>]
; The ports are optional. An explicit port re-registers the service with it; without one an existing service and its
; firewall rule are kept as they are. /HUBENROLL joins the central hub (PLAN.md S20, T905) once the service runs, with
; "qoffeeautomat hub join <string>": the string is the one that the hub shows (qahub1.<code>). It contains an enrollment
; token and joining gives the hub full control over the PC. The installer does not write the string to its own log, but
; Inno Setup records its command line in the log of /LOG, so give the file only to administrators or use a token with a
; short expiry and one use. A failed join does not fail the installation; run "qoffeeautomat hub join" afterwards.
; Silent uninstallation:
;   "<app dir>\unins000.exe" /VERYSILENT /SUPPRESSMSGBOXES /NORESTART [/PURGEDATA]
; /PURGEDATA also deletes %ProgramData%\QoffeeAutomat; without it the data stays.

#define AppVersion GetEnv("QOFFEE_APP_VERSION")
#if AppVersion == ""
  #error Set the environment variable QOFFEE_APP_VERSION (use build-installer.ps1).
#endif

#define VersionNumeric GetEnv("QOFFEE_VERSION_NUMERIC")
#if VersionNumeric == ""
  #error Set the environment variable QOFFEE_VERSION_NUMERIC (use build-installer.ps1).
#endif

#define ArchFromEnv GetEnv("QOFFEE_ARCH")
#if ArchFromEnv == ""
  #define Arch "x64"
#else
  #define Arch ArchFromEnv
#endif
#if Arch != "x64"
  #if Arch != "arm64"
    #error QOFFEE_ARCH must be x64 or arm64.
  #endif
#endif

#define SourceDirFromEnv GetEnv("QOFFEE_SOURCE_DIR")
#if SourceDirFromEnv == ""
  #define SourceDir "..\..\artifacts\packaging\stage\win-" + Arch
#else
  #define SourceDir SourceDirFromEnv
#endif

#define OutputDirFromEnv GetEnv("QOFFEE_OUTPUT_DIR")
#if OutputDirFromEnv == ""
  #define OutputDir "..\..\artifacts\packaging"
#else
  #define OutputDir OutputDirFromEnv
#endif

#define AppUrl "https://github.com/toeck98/QoffeeAutomat"

[Setup]
; AppId identifies the installation for upgrades and for the uninstall entry. Never change it.
AppId={{8F3C2A5E-6B1D-4E7A-9C42-1D5B7E0A3F96}
AppName=QoffeeAutomat
AppVersion={#AppVersion}
AppVerName=QoffeeAutomat {#AppVersion}
AppPublisher=toeck98
AppPublisherURL={#AppUrl}
AppSupportURL={#AppUrl}/issues
AppUpdatesURL={#AppUrl}/releases
VersionInfoVersion={#VersionNumeric}
VersionInfoProductVersion={#VersionNumeric}
VersionInfoProductName=QoffeeAutomat
VersionInfoCompany=toeck98
VersionInfoDescription=QoffeeAutomat Setup
VersionInfoCopyright=Copyright (c) toeck98
DefaultDirName={autopf}\QoffeeAutomat
DefaultGroupName=QoffeeAutomat
DisableDirPage=yes
DisableProgramGroupPage=yes
DisableReadyPage=yes
PrivilegesRequired=admin
MinVersion=10.0.17763
#if Arch == "arm64"
ArchitecturesAllowed=arm64
ArchitecturesInstallIn64BitMode=arm64
#else
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
#endif
OutputDir={#OutputDir}
OutputBaseFilename=QoffeeAutomat-Setup-{#AppVersion}-{#Arch}
SetupIconFile=..\..\assets\brand\app.ico
UninstallDisplayIcon={app}\QoffeeAutomat.Agent.exe
LicenseFile=..\..\LICENSE
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
SetupLogging=yes
; The service and the SessionHosts are stopped by the script (PrepareToInstall), not by the Restart Manager.
CloseApplications=no
RestartApplications=no

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Excludes: "*.pdb"; Flags: ignoreversion recursesubdirs createallsubdirs

[InstallDelete]
; The files of the UI have hashes in their names; remove the old ones so that an upgrade leaves nothing behind.
Type: filesandordirs; Name: "{app}\wwwroot"
; The template gallery (T701) is part of the installation; a template that a newer version dropped must not stay.
Type: filesandordirs; Name: "{app}\templates"

[INI]
; The Start menu shortcut to the UI: an internet shortcut, because the address depends on the UI port.
Filename: "{group}\QoffeeAutomat.url"; Section: "InternetShortcut"; Key: "URL"; String: "{code:UiUrl}"
Filename: "{group}\QoffeeAutomat.url"; Section: "InternetShortcut"; Key: "IconFile"; String: "{app}\QoffeeAutomat.Agent.exe"
Filename: "{group}\QoffeeAutomat.url"; Section: "InternetShortcut"; Key: "IconIndex"; String: "0"

[Icons]
Name: "{group}\Uninstall QoffeeAutomat"; Filename: "{uninstallexe}"

[Run]
Filename: "{code:UiUrl}"; Description: "Open the QoffeeAutomat UI"; Flags: postinstall shellexec skipifsilent nowait

[UninstallRun]
; Order matters: stop the service and the SessionHosts first, then remove the service (its Event Log source too) and the
; firewall rule while the executable still exists.
Filename: "{sys}\net.exe"; Parameters: "stop QoffeeAutomat"; RunOnceId: "StopService"; Flags: runhidden
Filename: "{sys}\taskkill.exe"; Parameters: "/F /T /IM QoffeeAutomat.SessionHost.exe"; RunOnceId: "StopSessionHosts"; Flags: runhidden
Filename: "{sys}\taskkill.exe"; Parameters: "/F /T /IM QoffeeAutomat.Agent.exe"; RunOnceId: "StopAgent"; Flags: runhidden
Filename: "{app}\QoffeeAutomat.Agent.exe"; Parameters: "service uninstall"; RunOnceId: "RemoveService"; Flags: runhidden
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall delete rule name=""QoffeeAutomat QoffeeLink"""; RunOnceId: "RemoveFirewallRule"; Flags: runhidden

[UninstallDelete]
Type: filesandordirs; Name: "{group}"

[Code]
const
  ServiceName = 'QoffeeAutomat';
  FirewallRuleName = 'QoffeeAutomat QoffeeLink';
  DefaultUiPort = '8480';
  DefaultLinkPort = '8481';

var
  WasInstalled: Boolean;
  InstallFailed: Boolean;
  ShowTokenPage: Boolean;
  PurgeData: Boolean;
  TokenPage: TOutputMsgWizardPage;

{ Runs a program hidden, waits for it and returns its exit code (-1 when it could not be started). }
function RunTool(const FileName, Params: String): Integer;
var
  ResultCode: Integer;
begin
  if Exec(FileName, Params, '', SW_HIDE, ewWaitUntilTerminated, ResultCode) then
    Result := ResultCode
  else
    Result := -1;
  Log(Format('Ran "%s" %s -> %d', [FileName, Params, Result]));
end;

{ Reads /NAME or /NAME=value from the command line of Setup or of the uninstaller. }
function FindSwitch(const Name: String; var Value: String): Boolean;
var
  I: Integer;
  Argument, Prefix: String;
begin
  Result := False;
  Value := '';
  Prefix := '/' + Uppercase(Name);
  for I := 1 to ParamCount do
  begin
    Argument := ParamStr(I);
    if Uppercase(Argument) = Prefix then
    begin
      Result := True;
      Exit;
    end;
    if Pos(Prefix + '=', Uppercase(Argument)) = 1 then
    begin
      Result := True;
      Value := Copy(Argument, Length(Prefix) + 2, Length(Argument));
      Exit;
    end;
  end;
end;

{ Looks like an enrollment string of the hub (PLAN.md S20): qahub1.<base64url>. The agent checks the rest. }
function IsEnrollmentString(const Value: String): Boolean;
begin
  Result := (Pos('qahub1.', Value) = 1) and (Length(Value) > 7);
end;

function IsValidPort(const Value: String): Boolean;
var
  Number: Integer;
begin
  Number := StrToIntDef(Value, -1);
  Result := (Number >= 1) and (Number <= 65535) and (IntToStr(Number) = Value);
end;

function ExplicitUiPort: String;
var
  Value: String;
begin
  Result := '';
  if FindSwitch('UIPORT', Value) then
    Result := Value;
end;

function ExplicitLinkPort: String;
var
  Value: String;
begin
  Result := '';
  if FindSwitch('LINKPORT', Value) then
    Result := Value;
end;

function UiPort: String;
begin
  Result := ExplicitUiPort;
  if Result = '' then
    Result := DefaultUiPort;
end;

{ The address of the UI; used by the shortcut and by the "open the UI" entry. }
function UiUrl(Param: String): String;
begin
  Result := 'http://127.0.0.1:' + UiPort + '/';
end;

function DataRoot: String;
begin
  Result := ExpandConstant('{commonappdata}\QoffeeAutomat');
end;

function SetupTokenPath: String;
begin
  Result := DataRoot + '\secrets\setup-token.txt';
end;

function ServiceExists: Boolean;
begin
  Result := RunTool(ExpandConstant('{sys}\sc.exe'), 'query ' + ServiceName) = 0;
end;

function FirewallRuleExists: Boolean;
begin
  Result := RunTool(ExpandConstant('{sys}\netsh.exe'), 'advfirewall firewall show rule name="' + FirewallRuleName + '"') = 0;
end;

{ The service runs as LocalSystem and starts every file in the installation directory, so the directory must be one that
  standard users cannot change: Program Files. }
function IsBelowProgramFiles(const Directory: String): Boolean;
var
  Base, Path: String;
begin
  Base := AddBackslash(Lowercase(ExpandFileName(ExpandConstant('{autopf}'))));
  Path := AddBackslash(Lowercase(ExpandFileName(Directory)));
  Result := (Pos(Base, Path) = 1) and (Pos('..', Path) = 0);
end;

procedure StopServiceAndSessionHosts;
begin
  if ServiceExists then
  begin
    { Wait up to 30 s for SCM to reach Stopped so taskkill never races with STOP_PENDING or triggers failure recovery. }
    RunTool(
      ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe'),
      '-NoProfile -NonInteractive -Command "Stop-Service -Name ''' + ServiceName + ''' -Force -ErrorAction SilentlyContinue; ' +
      '$s = Get-Service -Name ''' + ServiceName + ''' -ErrorAction SilentlyContinue; ' +
      'if ($s -and $s.Status -ne ''Stopped'') { $s.WaitForStatus(''Stopped'', [TimeSpan]::FromSeconds(30)) }"');
  end;
  { The agent stops its SessionHosts with the service; this catches the ones that did not follow and an agent that was
    started by hand, because their executables would block the replacement of the files. }
  RunTool(ExpandConstant('{sys}\taskkill.exe'), '/F /T /IM QoffeeAutomat.SessionHost.exe');
  RunTool(ExpandConstant('{sys}\taskkill.exe'), '/F /T /IM QoffeeAutomat.Agent.exe');
  Sleep(1500);
end;

function InitializeSetup: Boolean;
var
  Value: String;
begin
  Result := True;
  if FindSwitch('UIPORT', Value) and not IsValidPort(Value) then
  begin
    SuppressibleMsgBox('/UIPORT must be a port number between 1 and 65535.', mbError, MB_OK, IDOK);
    Result := False;
    Exit;
  end;
  if FindSwitch('LINKPORT', Value) and not IsValidPort(Value) then
  begin
    SuppressibleMsgBox('/LINKPORT must be a port number between 1 and 65535.', mbError, MB_OK, IDOK);
    Result := False;
    Exit;
  end;
  { The value is not shown: it contains a token. }
  if FindSwitch('HUBENROLL', Value) and not IsEnrollmentString(Value) then
  begin
    SuppressibleMsgBox('/HUBENROLL must be the enrollment string that the hub shows (it starts with qahub1.).', mbError, MB_OK, IDOK);
    Result := False;
  end;
end;

procedure InitializeWizard;
begin
  { Shown after the installation, before the finish page. }
  TokenPage := CreateOutputMsgPage(wpInfoAfter, 'Setup token', 'Finish the setup in the QoffeeAutomat UI', '');
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  Result := '';
  if not IsBelowProgramFiles(WizardDirValue) then
  begin
    Result := 'QoffeeAutomat must be installed below "' + ExpandConstant('{autopf}') +
      '". The service runs as LocalSystem, so the directory must not be writable by standard users.';
    Exit;
  end;
  WasInstalled := ServiceExists;
  StopServiceAndSessionHosts;
end;

procedure AddFirewallRule(const Port: String);
begin
  { Delete first, so that the rule exists once and has the right port and program. Private and domain profiles only
    (PLAN.md S09 item 9); the public profile stays closed. }
  RunTool(ExpandConstant('{sys}\netsh.exe'), 'advfirewall firewall delete rule name="' + FirewallRuleName + '"');
  if RunTool(ExpandConstant('{sys}\netsh.exe'),
    'advfirewall firewall add rule name="' + FirewallRuleName + '" dir=in action=allow protocol=TCP localport=' + Port +
    ' profile=private,domain program="' + ExpandConstant('{app}\QoffeeAutomat.Agent.exe') + '"' +
    ' description="QoffeeLink: lets Home Assistant connect to the QoffeeAutomat agent."') <> 0 then
  begin
    InstallFailed := True;
    Log('The firewall rule could not be created.');
    SuppressibleMsgBox('The firewall rule for the QoffeeLink port could not be created. Home Assistant on another device ' +
      'cannot connect until you allow TCP port ' + Port + ' on private and domain networks.', mbError, MB_OK, IDOK);
  end;
end;

procedure WaitForSetupToken;
var
  Attempt: Integer;
begin
  { The agent writes the token on its first start. }
  for Attempt := 1 to 60 do
  begin
    if FileExists(SetupTokenPath) then
      Exit;
    Sleep(500);
  end;
end;

{ Joins the hub of /HUBENROLL (PLAN.md S20, T905): runs "hub join" of the running agent. The string contains a token, so
  neither the string nor the command line is logged. The service needs a moment to open the channel of the verb (exit
  code 69: no agent found yet), so that code is tried again; any other code ends the attempt. A failed join does not fail
  the installation: the person can run "qoffeeautomat hub join" later. }
procedure JoinHub(const Agent: String);
var
  Enrollment: String;
  ResultCode, Attempt: Integer;
begin
  if (not FindSwitch('HUBENROLL', Enrollment)) or (Enrollment = '') then
    Exit;

  ResultCode := -1;
  for Attempt := 1 to 30 do
  begin
    if not Exec(Agent, 'hub join "' + Enrollment + '"', '', SW_HIDE, ewWaitUntilTerminated, ResultCode) then
      ResultCode := -1;
    if ResultCode = 0 then
    begin
      Log('Joined the hub of /HUBENROLL.');
      Exit;
    end;
    if ResultCode <> 69 then
      Break;
    Sleep(1000);
  end;
  Log(Format('The agent could not join the hub of /HUBENROLL (exit code %d). Run "qoffeeautomat hub join" later.', [ResultCode]));
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  Agent, Params, LinkPort: String;
  ExplicitPorts, NeedServiceInstall: Boolean;
begin
  if CurStep <> ssPostInstall then
    Exit;

  Agent := ExpandConstant('{app}\QoffeeAutomat.Agent.exe');
  ExplicitPorts := (ExplicitUiPort <> '') or (ExplicitLinkPort <> '');
  NeedServiceInstall := (not WasInstalled) or ExplicitPorts;

  { Creates the data directories with their ACLs (S05, S09) and the service. An explicit port re-registers the service,
    because the ports are part of its command line. }
  if WasInstalled and NeedServiceInstall then
    RunTool(Agent, 'service uninstall');
  if NeedServiceInstall then
  begin
    Params := 'service install';
    if ExplicitUiPort <> '' then
      Params := Params + ' --ui-port ' + ExplicitUiPort;
    if ExplicitLinkPort <> '' then
      Params := Params + ' --link-port ' + ExplicitLinkPort;
    if RunTool(Agent, Params) <> 0 then
    begin
      InstallFailed := True;
      SuppressibleMsgBox('The QoffeeAutomat service could not be installed. See the Setup log for details.', mbError, MB_OK, IDOK);
      Exit;
    end;
  end;

  { An existing rule stays as it is unless a port was given: the user may have changed the port in the settings. }
  LinkPort := ExplicitLinkPort;
  if LinkPort = '' then
    LinkPort := DefaultLinkPort;
  if (ExplicitLinkPort <> '') or (not FirewallRuleExists) then
    AddFirewallRule(LinkPort);

  if RunTool(Agent, 'service start') <> 0 then
  begin
    InstallFailed := True;
    SuppressibleMsgBox('The QoffeeAutomat service was installed but did not start. See the Event Log and ' +
      DataRoot + '\logs for details.', mbError, MB_OK, IDOK);
    Exit;
  end;

  if not WasInstalled then
    WaitForSetupToken;

  JoinHub(Agent);

  { A fresh installation always shows where to find the token; an upgrade only while the password is not set yet
    (setting it deletes the file). }
  ShowTokenPage := FileExists(SetupTokenPath) or (not WasInstalled);
  Log(Format('Setup token file: %s (exists: %d)', [SetupTokenPath, Ord(FileExists(SetupTokenPath))]));
end;

function ShouldSkipPage(PageID: Integer): Boolean;
begin
  Result := False;
  if PageID = TokenPage.ID then
    Result := not ShowTokenPage;
end;

procedure CurPageChanged(CurPageID: Integer);
var
  Text: String;
begin
  if CurPageID <> TokenPage.ID then
    Exit;

  if FileExists(SetupTokenPath) then
    Text := 'QoffeeAutomat is installed and running.' + #13#10#13#10 +
      'To finish the setup, open the UI at ' + UiUrl('') + ' (the shortcut "QoffeeAutomat" in the Start menu), enter the ' +
      'setup token and choose a password.' + #13#10#13#10 +
      'The setup token is in this file. Only administrators can read it, so open it from an elevated program ' +
      '(for example Notepad started with "Run as administrator"):' + #13#10#13#10 +
      SetupTokenPath + #13#10#13#10 +
      'The file disappears when the password is set.'
  else
    Text := 'QoffeeAutomat is installed, but the setup token file does not exist yet:' + #13#10#13#10 +
      SetupTokenPath + #13#10#13#10 +
      'The agent writes it on its first start. Open the UI at ' + UiUrl('') + ' in a moment; if the file is still ' +
      'missing, look at the logs in ' + DataRoot + '\logs.';
  TokenPage.MsgLabel.Caption := Text;
end;

function InitializeUninstall: Boolean;
var
  Value: String;
begin
  Result := True;
  PurgeData := FindSwitch('PURGEDATA', Value) and (Value <> '0');
  if (not PurgeData) and (not UninstallSilent) then
    PurgeData := SuppressibleMsgBox(
      'Do you also want to delete the QoffeeAutomat data (configuration, commands and sensors, scripts, secrets, run ' +
      'history and logs) in' + #13#10 + DataRoot + '?' + #13#10#13#10 +
      'Choose No to keep it, for example to install QoffeeAutomat again later.',
      mbConfirmation, MB_YESNO or MB_DEFBUTTON2, IDNO) = IDYES;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  Root: String;
begin
  if (CurUninstallStep = usPostUninstall) and PurgeData then
  begin
    Root := DataRoot;
    { Never delete anything but the product folder itself. }
    if (Length(Root) > Length('\QoffeeAutomat')) and (Lowercase(Copy(Root, Length(Root) - 13, 14)) = '\qoffeeautomat') then
    begin
      Log('Deleting ' + Root);
      if not DelTree(Root, True, True, True) then
        Log('Not everything in ' + Root + ' could be deleted.');
    end;
  end;
end;
