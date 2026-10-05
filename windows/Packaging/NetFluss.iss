; NetFluss for Windows — installer.
;
; Built by windows/Packaging/build-release.ps1, which passes:
;   /DAppVersion=1.2.3      the version (from the win-vX.Y.Z tag)
;   /DArch=x64|arm64        the architecture of the published files
;   /DSource=<folder>       the self-contained publish output (app + Helper\)
;
; Per-user and unprivileged, like dropping NetFluss.app into ~/Applications: it installs to
; %LOCALAPPDATA%\Programs\NetFluss and never asks for administrator rights. The optional
; helper service is the one part that needs them, and the app installs it on request.
; The same setup, run with /VERYSILENT /LAUNCH, is what the in-app updater uses.

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif
#ifndef Arch
  #define Arch "x64"
#endif
#ifndef Source
  #define Source "..\artifacts\publish\" + Arch
#endif

[Setup]
AppId={{6F3C9A41-2E7B-4D58-9C1A-5B0E8D2F7A13}
AppName=NetFluss
AppVersion={#AppVersion}
AppVerName=NetFluss {#AppVersion}
AppPublisher=Rana GmbH
AppPublisherURL=https://github.com/rana-gmbh/NetFluss
AppSupportURL=https://github.com/rana-gmbh/NetFluss/issues
AppUpdatesURL=https://github.com/rana-gmbh/NetFluss/releases
DefaultDirName={localappdata}\Programs\NetFluss
DisableDirPage=auto
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
OutputDir=..\artifacts\release
OutputBaseFilename=NetFluss-Setup-{#AppVersion}-{#Arch}
SetupIconFile=..\src\NetFluss.App\Assets\NetFluss.ico
UninstallDisplayIcon={app}\NetFluss.exe
UninstallDisplayName=NetFluss
LicenseFile=..\..\LICENSE
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
; The app is a tray app; Restart Manager can miss it, so [Code] also asks it to quit.
CloseApplications=yes
RestartApplications=no
VersionInfoVersion={#AppVersion}
VersionInfoCompany=Rana GmbH
VersionInfoProductName=NetFluss
#if Arch == "arm64"
ArchitecturesAllowed=arm64
ArchitecturesInstallIn64BitMode=arm64
#else
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
#endif
MinVersion=10.0.19041

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"
Name: "german"; MessagesFile: "compiler:Languages\German.isl"

[CustomMessages]
english.AutoStart=Start NetFluss when I sign in
german.AutoStart=NetFluss bei der Anmeldung starten
english.LaunchNow=Launch NetFluss
german.LaunchNow=NetFluss starten

[Tasks]
Name: "autostart"; Description: "{cm:AutoStart}"

[Files]
Source: "{#Source}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\NetFluss"; Filename: "{app}\NetFluss.exe"

[Registry]
; The same value the app's own "Start with Windows" switch writes, so the two agree.
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "NetFluss"; ValueData: """{app}\NetFluss.exe"""; Tasks: autostart; Flags: uninsdeletevalue

[Run]
Filename: "{app}\NetFluss.exe"; Description: "{cm:LaunchNow}"; Flags: nowait postinstall skipifsilent
; An in-app update runs silently and asks to be relaunched.
Filename: "{app}\NetFluss.exe"; Flags: nowait; Check: LaunchAfterSilentUpdate

[UninstallRun]
; The helper is a machine-wide service; removing it needs one administrator approval.
Filename: "{app}\Helper\NetFluss.Service.exe"; Parameters: "uninstall"; Verb: "runas"; Flags: shellexec waituntilterminated; Check: HelperInstalled; RunOnceId: "RemoveHelper"

[UninstallDelete]
Type: dirifempty; Name: "{app}"

[Code]
function HelperInstalled: Boolean;
begin
  Result := RegKeyExists(HKLM, 'SYSTEM\CurrentControlSet\Services\NetFlussHelper');
end;

function LaunchAfterSilentUpdate: Boolean;
begin
  Result := WizardSilent and (Pos('/LAUNCH', UpperCase(GetCmdTail)) > 0);
end;

{ Asks a running NetFluss to quit cleanly — it saves statistics and the timer on the way
  out — and only then forces the issue. }
procedure StopNetFluss;
var
  ResultCode: Integer;
begin
  if FileExists(ExpandConstant('{app}\NetFluss.exe')) then
  begin
    Exec(ExpandConstant('{app}\NetFluss.exe'), '--quit', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
    Sleep(2500);
  end;
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/F /IM NetFluss.exe', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  StopNetFluss;
  Result := '';
end;

function InitializeUninstall: Boolean;
begin
  StopNetFluss;
  Result := True;
end;
