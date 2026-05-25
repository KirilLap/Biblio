#define AppName    "Biblio"
#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif
#define AppPublisher "Biblio"
#define SrcClient    "..\BibClient\bin\Publish\win-x64"

[Setup]
AppId={{A1B2C3D4-E5F6-7890-ABCD-EF1234567890}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher={#AppPublisher}
DefaultDirName={autopf}\Biblio
OutputDir=Output
OutputBaseFilename=bibclient-setup-{#AppVersion}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
PrivilegesRequired=admin
ArchitecturesInstallIn64BitMode=x64compatible
DisableWelcomePage=yes
DisableDirPage=yes
DisableProgramGroupPage=yes
CreateUninstallRegKey=no
Uninstallable=no

[Languages]
Name: "ru"; MessagesFile: "compiler:Languages\Russian.isl"

[Files]
Source: "{#SrcClient}\*"; DestDir: "{app}\BibClient"; \
    Flags: ignoreversion recursesubdirs createallsubdirs

[Run]
Filename: "sc.exe"; \
    Parameters: "create BibClientWatchdog binPath= ""{app}\BibClient\BibClientService.exe"" start= auto DisplayName= ""BibClient Watchdog"""; \
    Flags: runhidden waituntilterminated
Filename: "sc.exe"; Parameters: "start BibClientWatchdog"; \
    Flags: runhidden waituntilterminated

[UninstallRun]
Filename: "sc.exe"; Parameters: "stop BibClientWatchdog";   Flags: runhidden
Filename: "sc.exe"; Parameters: "delete BibClientWatchdog"; Flags: runhidden

[Code]
procedure StopRunningProcesses();
var
  ResultCode: Integer;
begin
  Exec('sc.exe', 'stop BibClientWatchdog', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Sleep(2000);
  Exec('taskkill.exe', '/f /im BibClientGuardian.exe', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Exec('taskkill.exe', '/f /im BibClient.exe',         '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  StopRunningProcesses();
  Result := '';
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  ResultCode: Integer;
begin
  if CurStep = ssPostInstall then
    Exec(ExpandConstant('{app}\BibClient\BibClient.exe'), '', '', SW_SHOW, ewNoWait, ResultCode);
end;
