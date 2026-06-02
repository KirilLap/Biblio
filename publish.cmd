@echo off
cd /d "%~dp0"
echo Building BibAdminWeb...
dotnet publish BibAdminWeb -c Release -o BibAdminWeb\bin\Publish\win-x64
if %errorlevel% neq 0 (
    echo Build failed!
    pause
    exit /b 1
)

echo.
echo Removing runtime/settings files from publish output...
echo (these must NOT overwrite production settings on update)

set OUT=BibAdminWeb\bin\Publish\win-x64

rem -- Главный файл настроек (порт, пароль, все глобальные настройки) --
del /f /q "%OUT%\global_settings.json" 2>nul

rem -- Runtime-данные (реестр ПК, сессии, история) --
del /f /q "%OUT%\registry.json"         2>nul
del /f /q "%OUT%\active_sessions.json"  2>nul
del /f /q "%OUT%\deleted_pcs.json"      2>nul
del /f /q "%OUT%\server_heartbeat.json" 2>nul
del /f /q "%OUT%\readers.json"          2>nul
del /f /q "%OUT%\*_history.json"        2>nul
del /f /q "%OUT%\*.log"                 2>nul
del /f /q "%OUT%\*.flag"                2>nul

echo.
echo Done! Files in: %OUT%
pause
