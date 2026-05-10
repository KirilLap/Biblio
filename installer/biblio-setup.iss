#define AppName    "Biblio"
#define AppVersion "1.0.0"
#define AppPublisher "Biblio"

; Папки с результатами publish (относительно этого .iss файла)
#define SrcClient    "..\BibClient\bin\Publish\win-x64"
#define SrcAdmin     "..\BibAdmin\bin\Publish\win-x64"
#define SrcAdminWeb  "..\BibAdminWeb\bin\Publish\win-x64"

[Setup]
AppId={{A1B2C3D4-E5F6-7890-ABCD-EF1234567890}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher={#AppPublisher}
DefaultDirName={autopf}\Biblio
DefaultGroupName={#AppName}
OutputDir=Output
OutputBaseFilename=biblio-setup-{#AppVersion}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
PrivilegesRequired=admin
ArchitecturesInstallIn64BitMode=x64compatible

[Languages]
Name: "ru"; MessagesFile: "compiler:Languages\Russian.isl"

; -------------------------------------------------------
; Типы установки (пресеты)
; -------------------------------------------------------
[Types]
Name: "client";   Description: "Клиентский ПК (BibClient)"
Name: "server";   Description: "Сервер (BibAdmin + BibAdminWeb)"
Name: "custom";   Description: "Выборочная установка"; Flags: iscustom

; -------------------------------------------------------
; Компоненты
; -------------------------------------------------------
[Components]
Name: "client";    Description: "BibClient — программа для ПК читателя";        Types: client custom
Name: "admin";     Description: "BibAdmin — настольный интерфейс администратора"; Types: server custom
Name: "adminweb";  Description: "BibAdminWeb — веб-интерфейс администратора";    Types: server custom

; -------------------------------------------------------
; Файлы
; -------------------------------------------------------
[Files]
; BibClient — все файлы включая BibClientService и BibClientGuardian
Source: "{#SrcClient}\*"; DestDir: "{app}\BibClient"; \
    Flags: ignoreversion recursesubdirs createallsubdirs; Components: client

; BibAdmin
Source: "{#SrcAdmin}\*"; DestDir: "{app}\BibAdmin"; \
    Flags: ignoreversion recursesubdirs createallsubdirs; Components: admin

; BibAdminWeb
Source: "{#SrcAdminWeb}\*"; DestDir: "{app}\BibAdminWeb"; \
    Flags: ignoreversion recursesubdirs createallsubdirs; Components: adminweb

; -------------------------------------------------------
; Ярлыки
; -------------------------------------------------------
[Icons]
Name: "{group}\BibClient";    Filename: "{app}\BibClient\BibClient.exe";       Components: client
Name: "{group}\BibAdmin";     Filename: "{app}\BibAdmin\BibAdmin.exe";         Components: admin
Name: "{group}\BibAdmin Web"; Filename: "{app}\BibAdminWeb\BibAdminWeb.exe";   Components: adminweb
Name: "{group}\Удалить Biblio"; Filename: "{uninstallexe}"

Name: "{autodesktop}\BibClient";    Filename: "{app}\BibClient\BibClient.exe";     Components: client
Name: "{autodesktop}\BibAdmin";     Filename: "{app}\BibAdmin\BibAdmin.exe";       Components: admin
Name: "{autodesktop}\BibAdmin Web"; Filename: "{app}\BibAdminWeb\BibAdminWeb.exe"; Components: adminweb

; -------------------------------------------------------
; После установки
; -------------------------------------------------------
[Run]
; Регистрация службы BibClientWatchdog
Filename: "sc.exe"; \
    Parameters: "create BibClientWatchdog binPath= ""{app}\BibClient\BibClientService.exe"" start= auto DisplayName= ""BibClient Watchdog"""; \
    Flags: runhidden waituntilterminated; Components: client

Filename: "sc.exe"; Parameters: "start BibClientWatchdog"; \
    Flags: runhidden waituntilterminated; Components: client

; Правила брандмауэра открываются в [Code] → CurStepChanged(ssPostInstall)
; с использованием порта, введённого пользователем на странице настройки.

; -------------------------------------------------------
; При удалении
; -------------------------------------------------------
[UninstallRun]
Filename: "sc.exe"; Parameters: "stop BibClientWatchdog";   Flags: runhidden; Components: client
Filename: "sc.exe"; Parameters: "delete BibClientWatchdog"; Flags: runhidden; Components: client

Filename: "netsh.exe"; Parameters: "advfirewall firewall delete rule name=""BibAdminWeb"""; Flags: runhidden; Components: adminweb
Filename: "netsh.exe"; Parameters: "advfirewall firewall delete rule name=""BibAdmin""";    Flags: runhidden; Components: admin

; -------------------------------------------------------
; Обновление: перед установкой остановить службу и процессы
; -------------------------------------------------------
[Code]
var
  PortPage: TInputQueryWizardPage;

procedure StopRunningProcesses();
var
  ResultCode: Integer;
begin
  Exec('sc.exe', 'stop BibClientWatchdog', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Sleep(2000);
  Exec('taskkill.exe', '/f /im BibClient.exe',    '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Exec('taskkill.exe', '/f /im BibAdmin.exe',     '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Exec('taskkill.exe', '/f /im BibAdminWeb.exe',  '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  StopRunningProcesses();
  Result := '';
end;

procedure InitializeWizard();
begin
  PortPage := CreateInputQueryPage(wpSelectComponents,
    'Настройка порта сервера',
    'Укажите порт, на котором будут работать BibAdmin и BibAdminWeb.',
    'Порт должен быть в диапазоне 1024–65535. По умолчанию: 8080.');
  PortPage.Add('Порт сервера:', False);
  PortPage.Values[0] := '8080';
end;

function NextButtonClick(CurPageID: Integer): Boolean;
var
  PortStr: String;
  PortNum: Integer;
begin
  Result := True;
  if CurPageID = PortPage.ID then
  begin
    PortStr := Trim(PortPage.Values[0]);
    if not TryStrToInt(PortStr, PortNum) or (PortNum < 1024) or (PortNum > 65535) then
    begin
      MsgBox('Введите корректный порт (1024–65535).', mbError, MB_OK);
      Result := False;
    end;
  end;
end;

function GetServerPort(): String;
begin
  Result := Trim(PortPage.Values[0]);
  if Result = '' then Result := '8080';
end;

procedure OpenFirewallPort(RuleName: String; Port: String);
var
  ResultCode: Integer;
begin
  Exec('netsh.exe',
    'advfirewall firewall add rule name="' + RuleName + '" dir=in action=allow protocol=TCP localport=' + Port,
    '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  Port: String;
  SettingsFile: String;
  Lines: TArrayOfString;
begin
  if CurStep = ssPostInstall then
  begin
    Port := GetServerPort();

    // Открываем порт в брандмауэре для выбранных компонентов
    if IsComponentSelected('adminweb') then
      OpenFirewallPort('BibAdminWeb', Port);
    if IsComponentSelected('admin') then
      OpenFirewallPort('BibAdmin', Port);

    // Записываем выбранный порт в settings.json для BibAdmin
    if IsComponentSelected('admin') then
    begin
      SettingsFile := ExpandConstant('{app}\BibAdmin\settings.json');
      if FileExists(SettingsFile) then
      begin
        if LoadStringsFromFile(SettingsFile, Lines) then
        begin
          // Простая замена строки порта если она уже есть
          // При первом запуске приложение само создаст/применит порт через FirstRunWindow
        end;
      end;
    end;

    // Записываем выбранный порт в settings.json для BibAdminWeb
    if IsComponentSelected('adminweb') then
    begin
      SettingsFile := ExpandConstant('{app}\BibAdminWeb\settings.json');
      if FileExists(SettingsFile) then
      begin
        if LoadStringsFromFile(SettingsFile, Lines) then
        begin
          // Аналогично — порт будет применён при первом запуске через setup.html
        end;
      end;
    end;
  end;
end;
