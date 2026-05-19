#define AppName    "Biblio"
#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif
#define AppPublisher "Biblio"
#define SrcAdminWeb  "..\BibAdminWeb\bin\Publish\win-x64"

[Setup]
AppId={{A1B2C3D4-E5F6-7890-ABCD-EF1234567890}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher={#AppPublisher}
DefaultDirName={autopf}\Biblio
OutputDir=Output
OutputBaseFilename=bibadminweb-setup-{#AppVersion}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
PrivilegesRequired=admin
ArchitecturesInstallIn64BitMode=x64compatible
DisableWelcomePage=yes
DisableDirPage=yes
DisableProgramGroupPage=yes
CreateUninstallRegKey=no

[Languages]
Name: "ru"; MessagesFile: "compiler:Languages\Russian.isl"

[Files]
Source: "{#SrcAdminWeb}\*"; DestDir: "{app}\BibAdminWeb"; \
    Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autodesktop}\BibAdmin Web"; Filename: "{app}\BibAdminWeb\BibAdminWeb.exe"

[Run]
Filename: "{app}\BibAdminWeb\BibAdminWeb.exe"; \
    Flags: postinstall nowait runascurrentuser

[UninstallRun]
Filename: "netsh.exe"; \
    Parameters: "advfirewall firewall delete rule name=""BibAdminWeb"""; \
    Flags: runhidden

[Code]
procedure StopRunningProcesses();
var
  ResultCode: Integer;
begin
  Exec('taskkill.exe', '/f /im BibAdminWeb.exe', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Sleep(1000);
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  StopRunningProcesses();
  Result := '';
end;

function ReadPortFromSettings(SettingsFile: String): String;
var
  Content: AnsiString;
  Pos1, Pos2: Integer;
  Token: String;
begin
  Result := '';
  if not FileExists(SettingsFile) then Exit;
  if not LoadStringFromFile(SettingsFile, Content) then Exit;
  Token := '"ServerPort":';
  Pos1 := Pos(Token, Content);
  if Pos1 = 0 then Exit;
  Pos1 := Pos1 + Length(Token);
  while (Pos1 <= Length(Content)) and (Content[Pos1] = ' ') do
    Pos1 := Pos1 + 1;
  Pos2 := Pos1;
  while (Pos2 <= Length(Content)) and
        (Content[Pos2] >= '0') and (Content[Pos2] <= '9') do
    Pos2 := Pos2 + 1;
  if Pos2 > Pos1 then
    Result := Copy(Content, Pos1, Pos2 - Pos1);
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  Port: String;
  ResultCode: Integer;
begin
  if CurStep = ssPostInstall then
  begin
    Port := ReadPortFromSettings(ExpandConstant('{app}\BibAdminWeb\global_settings.json'));
    if Port = '' then Port := '8080';
    Exec('netsh.exe',
      'advfirewall firewall add rule name="BibAdminWeb" dir=in action=allow protocol=TCP localport=' + Port,
      '', SW_HIDE, ewWaitUntilTerminated, ResultCode);

    Exec(ExpandConstant('{app}\BibAdminWeb\BibAdminWeb.exe'), '', '', SW_SHOW, ewNoWait, ResultCode);
  end;
end;
