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

:: Временная папка для бэкапа данных сервера
set BAK=%TEMP%\biblio_update_bak

echo ============================================
echo   Biblio %VERSION% - обновление сервера
echo ============================================
echo   Описание: %NOTES%
echo.

:: [1/6] Остановить BibAdminWeb и BibAdmin
echo [1/6] Останавливаем сервисы...
:: Сначала запрашиваем мягкое завершение (WM_CLOSE / CTRL_CLOSE) — это позволяет
:: сохранить active_sessions.json через StopAsync. После 5 секунд добиваем /f.
taskkill /im BibAdminWeb.exe 2>nul
taskkill /im BibAdmin.exe    2>nul
timeout /t 5 /nobreak >nul
taskkill /f /im BibAdminWeb.exe 2>nul
taskkill /f /im BibAdmin.exe    2>nul
timeout /t 2 /nobreak >nul

:: [2/6] Сохранить данные сервера перед обновлением
echo [2/6] Сохраняем данные сервера...
if exist "%BAK%" rmdir /s /q "%BAK%"
mkdir "%BAK%\adminweb" 2>nul
mkdir "%BAK%\admin"    2>nul

:: BibAdminWeb — критичные файлы данных
for %%F in (global_settings.json clients.json active_sessions.json deleted_pcs.json pending_commands.json auth_tokens.json) do (
    if exist "%INSTALL_DIR%\BibAdminWeb\%%F" copy /y "%INSTALL_DIR%\BibAdminWeb\%%F" "%BAK%\adminweb\%%F" >nul
)
:: BibAdminWeb — папка с загруженными файлами (фоны)
if exist "%INSTALL_DIR%\BibAdminWeb\Files" xcopy "%INSTALL_DIR%\BibAdminWeb\Files\*" "%BAK%\adminweb\Files\" /y /e /q /i 2>nul

:: BibAdmin — критичные файлы данных
for %%F in (global_settings.json clients.json active_sessions.json deleted_pcs.json pending_commands.json) do (
    if exist "%INSTALL_DIR%\BibAdmin\%%F" copy /y "%INSTALL_DIR%\BibAdmin\%%F" "%BAK%\admin\%%F" >nul
)
:: BibAdmin — папка с загруженными файлами (фоны)
if exist "%INSTALL_DIR%\BibAdmin\Files" xcopy "%INSTALL_DIR%\BibAdmin\Files\*" "%BAK%\admin\Files\" /y /e /q /i 2>nul

:: [3/6] Обновить BibAdminWeb
echo [3/6] Обновляем BibAdminWeb...
xcopy "%SRC_ADMINWEB%\*" "%INSTALL_DIR%\BibAdminWeb\" /y /e /q
if %errorlevel% neq 0 ( echo ОШИБКА копирования BibAdminWeb! & pause & exit /b 1 )

:: [4/6] Обновить BibAdmin
echo [4/6] Обновляем BibAdmin...
xcopy "%SRC_ADMIN%\*" "%INSTALL_DIR%\BibAdmin\" /y /e /q
if %errorlevel% neq 0 ( echo ОШИБКА копирования BibAdmin! & pause & exit /b 1 )

:: [5/6] Восстановить данные сервера
echo [5/6] Восстанавливаем данные сервера...

:: BibAdminWeb
for %%F in (global_settings.json clients.json active_sessions.json deleted_pcs.json pending_commands.json auth_tokens.json) do (
    if exist "%BAK%\adminweb\%%F" copy /y "%BAK%\adminweb\%%F" "%INSTALL_DIR%\BibAdminWeb\%%F" >nul
)
if exist "%BAK%\adminweb\Files" xcopy "%BAK%\adminweb\Files\*" "%INSTALL_DIR%\BibAdminWeb\Files\" /y /e /q /i 2>nul

:: BibAdmin
for %%F in (global_settings.json clients.json active_sessions.json deleted_pcs.json pending_commands.json) do (
    if exist "%BAK%\admin\%%F" copy /y "%BAK%\admin\%%F" "%INSTALL_DIR%\BibAdmin\%%F" >nul
)
if exist "%BAK%\admin\Files" xcopy "%BAK%\admin\Files\*" "%INSTALL_DIR%\BibAdmin\Files\" /y /e /q /i 2>nul

:: [6/6] Публикуем установщик для клиентов
echo [6/6] Публикуем установщик для клиентов...
if not exist "%INSTALL_DIR%\BibAdminWeb\updates" mkdir "%INSTALL_DIR%\BibAdminWeb\updates"

if not exist "%INSTALLER%" (
    echo ОШИБКА: установщик не найден: %INSTALLER%
    echo Сначала запустите build.cmd
    pause & exit /b 1
)

copy /y "%INSTALLER%" "%INSTALL_DIR%\BibAdminWeb\updates\biblio-setup.exe" >nul
echo {"Version":"%VERSION%","ReleaseNotes":"%NOTES%","InstallerFile":"biblio-setup.exe"} > "%INSTALL_DIR%\BibAdminWeb\updates\version.json"

:: Запустить BibAdminWeb
echo.
echo Запускаем BibAdminWeb...
start "" "%INSTALL_DIR%\BibAdminWeb\BibAdminWeb.exe"

echo.
echo ============================================
echo   Готово! Версия %VERSION% развёрнута.
echo   BibAdminWeb обновлён и запущен.
echo   Настройки и данные ПК сохранены.
echo   Клиенты получат тихое обновление
echo   при следующем запуске BibClient.
echo ============================================
pause
