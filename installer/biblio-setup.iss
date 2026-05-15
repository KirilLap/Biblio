#define AppName    "Biblio"
; AppVersion передаётся из build.cmd через /DAppVersion=X.Y.Z
; При ручной компиляции через ISCC без параметра используется "0.0.0"
#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif
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

; Автозапуск BibAdmin от имени администратора
Filename: "{app}\BibAdmin\BibAdmin.exe"; \
    Flags: postinstall nowait runascurrentuser; Components: admin

; Автозапуск BibAdminWeb от имени администратора
Filename: "{app}\BibAdminWeb\BibAdminWeb.exe"; \
    Flags: postinstall nowait runascurrentuser; Components: adminweb

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
  IsValid: Boolean;
begin
  Result := True;
  if CurPageID = PortPage.ID then
  begin
    PortStr := Trim(PortPage.Values[0]);
    IsValid := False;
    try
      PortNum := StrToInt(PortStr);
      if (PortNum >= 1024) and (PortNum <= 65535) then
        IsValid := True;
    except
      // Порт не является числом, оставляем IsValid = False
    end;
    
    if not IsValid then
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

    // Записываем выбранный порт в global_settings.json для BibAdminWeb
    // Всегда перезаписываем при установке/обновлении
    if IsComponentSelected('adminweb') then
    begin
      SettingsFile := ExpandConstant('{app}\BibAdminWeb\global_settings.json');
      SaveStringToFile(SettingsFile,
        '{' + #13#10 +
        '  "ServerPort": ' + Port + ',' + #13#10 +
        '  "IsFirstRun": false,' + #13#10 +
        '  "AdminPasswordHash": "03ac674216f3e15c761ee1a5e255f067953623c8b388b4459e13f978d7c846f4",' + #13#10 +
        '  "Tariff": 3000,' + #13#10 +
        '  "Operators": []' + #13#10 +
        '}',
        False);
    end;

    // Записываем выбранный порт в global_settings.json для BibAdmin
    // Всегда перезаписываем при установке/обновлении
    if IsComponentSelected('admin') then
    begin
      SettingsFile := ExpandConstant('{app}\BibAdmin\global_settings.json');
      SaveStringToFile(SettingsFile,
        '{' + #13#10 +
        '  "ServerPort": ' + Port + ',' + #13#10 +
        '  "IsFirstRun": false,' + #13#10 +
        '  "AdminPasswordHash": "03ac674216f3e15c761ee1a5e255f067953623c8b388b4459e13f978d7c846f4",' + #13#10 +
        '  "Tariff": 3000,' + #13#10 +
        '  "PreventClose": true,' + #13#10 +
        '  "AutoStartWithUser": true' + #13#10 +
        '}',
        False);
    end;

    // Записываем настройки для BibClient (порт сервера и локальный IP)
    // Всегда перезаписываем при установке/обновлении
    if IsComponentSelected('client') then
    begin
      SettingsFile := ExpandConstant('{app}\BibClient\settings.json');
      SaveStringToFile(SettingsFile,
        '{' + #13#10 +
        '  "PcNumberValue": 1,' + #13#10 +
        '  "CustomName": "",' + #13#10 +
        '  "ServerIp": "",' + #13#10 +
        '  "ServerPort": ' + Port + ',' + #13#10 +
        '  "AdminPasswordHash": "03ac674216f3e15c761ee1a5e255f067953623c8b388b4459e13f978d7c846f4",' + #13#10 +
        '  "ShowPcName": true,' + #13#10 +
        '  "ShowPcNumber": true,' + #13#10 +
        '  "PreventClose": true,' + #13#10 +
        '  "AutoStartWithUser": true' + #13#10 +
        '}',
        False);
    end;
  end;
end;

[UninstallCode]
procedure DeinitializeUninstall();
var
  AppPath: String;
begin
  AppPath := ExpandConstant('{app}');
  
  // Полное удаление папки установки при деинсталляции
  if not DelTree(AppPath, True, True, True) then
  begin
    MsgBox('Не удалось автоматически удалить некоторые файлы в папке:' + #13#10 + AppPath + #13#10 + 
           'Пожалуйста, удалите эту папку вручную после перезагрузки компьютера.', mbWarning, MB_OK);
  end;
end;
