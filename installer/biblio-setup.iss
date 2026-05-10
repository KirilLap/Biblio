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

; Открыть порт 8080 для BibAdminWeb
Filename: "netsh.exe"; \
    Parameters: "advfirewall firewall add rule name=""BibAdminWeb"" dir=in action=allow protocol=TCP localport=8080"; \
    Flags: runhidden waituntilterminated; Components: adminweb

; Открыть порт 8080 для BibAdmin
Filename: "netsh.exe"; \
    Parameters: "advfirewall firewall add rule name=""BibAdmin"" dir=in action=allow protocol=TCP localport=8080"; \
    Flags: runhidden waituntilterminated; Components: admin

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
procedure StopRunningProcesses();
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
