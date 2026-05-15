@echo off
chcp 65001 > nul
setlocal

:: ============================================================
:: Использование:
::   update-server.cmd "Описание изменений"
::
:: Версия читается из файла VERSION (рядом со скриптом).
:: Установщик ищется: installer\Output\biblio-setup-<VERSION>.exe
:: ============================================================

:: Путь к установленному BibAdminWeb (менять если другая папка)
set INSTALL_DIR=C:\Program Files\Biblio

:: Читаем версию из файла VERSION
set /p VERSION=<"%~dp0VERSION"
if "%VERSION%"=="" ( echo ОШИБКА: файл VERSION пустой или не найден! & pause & exit /b 1 )

:: Описание релиза (первый аргумент скрипта, или пустое)
set NOTES=%~1
if "%NOTES%"=="" set NOTES=Обновление %VERSION%

:: Папка с новыми собранными файлами
set SRC_ADMINWEB=%~dp0BibAdminWeb\bin\Publish\win-x64
set SRC_ADMIN=%~dp0BibAdmin\bin\Publish\win-x64
set INSTALLER=%~dp0installer\Output\biblio-setup-%VERSION%.exe

:: Файлы настроек (не должны перезаписываться при обновлении)
set SETTINGS_ADMINWEB=%INSTALL_DIR%\BibAdminWeb\global_settings.json
set SETTINGS_ADMIN=%INSTALL_DIR%\BibAdmin\global_settings.json
set BAK_DIR=%TEMP%\biblio_update_bak

echo ============================================
echo   Biblio %VERSION% - обновление сервера
echo ============================================
echo   Описание: %NOTES%
echo.

:: Остановить BibAdminWeb и BibAdmin
echo [1/6] Останавливаем BibAdminWeb и BibAdmin...
taskkill /f /im BibAdminWeb.exe 2>nul
taskkill /f /im BibAdmin.exe    2>nul
timeout /t 2 /nobreak >nul

:: Сохранить настройки перед обновлением
echo [2/6] Сохраняем настройки...
mkdir "%BAK_DIR%" 2>nul
if exist "%SETTINGS_ADMINWEB%" copy /y "%SETTINGS_ADMINWEB%" "%BAK_DIR%\adminweb_settings.json" >nul
if exist "%SETTINGS_ADMIN%"    copy /y "%SETTINGS_ADMIN%"    "%BAK_DIR%\admin_settings.json"    >nul

:: Обновить BibAdminWeb
echo [3/6] Обновляем BibAdminWeb...
xcopy "%SRC_ADMINWEB%\*" "%INSTALL_DIR%\BibAdminWeb\" /y /e /q
if %errorlevel% neq 0 ( echo ОШИБКА копирования BibAdminWeb! & pause & exit /b 1 )

:: Обновить BibAdmin
echo [4/6] Обновляем BibAdmin...
xcopy "%SRC_ADMIN%\*" "%INSTALL_DIR%\BibAdmin\" /y /e /q
if %errorlevel% neq 0 ( echo ОШИБКА копирования BibAdmin! & pause & exit /b 1 )

:: Восстановить настройки (xcopy мог перезаписать если файл попал в publish)
echo [5/6] Восстанавливаем настройки...
if exist "%BAK_DIR%\adminweb_settings.json" copy /y "%BAK_DIR%\adminweb_settings.json" "%SETTINGS_ADMINWEB%" >nul
if exist "%BAK_DIR%\admin_settings.json"    copy /y "%BAK_DIR%\admin_settings.json"    "%SETTINGS_ADMIN%"    >nul

:: Положить установщик в папку updates (клиенты скачают сами)
echo [6/6] Публикуем установщик для клиентов...
if not exist "%INSTALL_DIR%\BibAdminWeb\updates" mkdir "%INSTALL_DIR%\BibAdminWeb\updates"

if not exist "%INSTALLER%" (
    echo ОШИБКА: установщик не найден: %INSTALLER%
    echo Сначала запустите build.cmd
    pause & exit /b 1
)

copy /y "%INSTALLER%" "%INSTALL_DIR%\BibAdminWeb\updates\biblio-setup.exe" >nul

:: Создаём version.json (клиенты сравнивают версию с этим файлом)
echo {"Version":"%VERSION%","ReleaseNotes":"%NOTES%","InstallerFile":"biblio-setup.exe"} > "%INSTALL_DIR%\BibAdminWeb\updates\version.json"

:: Запустить BibAdminWeb
echo.
echo Запускаем BibAdminWeb...
start "" "%INSTALL_DIR%\BibAdminWeb\BibAdminWeb.exe"

echo.
echo ============================================
echo   Готово! Версия %VERSION% развёрнута.
echo   BibAdminWeb обновлён и запущен.
echo   Клиенты получат тихое обновление
echo   при следующем запуске BibClient.
echo ============================================
pause
