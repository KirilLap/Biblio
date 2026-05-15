@echo off
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

echo ============================================
echo   Biblio %VERSION% - обновление сервера
echo ============================================
echo   Описание: %NOTES%
echo.

:: Остановить BibAdminWeb и BibAdmin
echo [1/5] Останавливаем BibAdminWeb и BibAdmin...
taskkill /f /im BibAdminWeb.exe 2>nul
taskkill /f /im BibAdmin.exe    2>nul
timeout /t 2 /nobreak >nul

:: Обновить BibAdminWeb
echo [2/5] Обновляем BibAdminWeb...
xcopy "%SRC_ADMINWEB%\*" "%INSTALL_DIR%\BibAdminWeb\" /y /e /q
if %errorlevel% neq 0 ( echo ОШИБКА копирования BibAdminWeb! & pause & exit /b 1 )

:: Обновить BibAdmin
echo [3/5] Обновляем BibAdmin...
xcopy "%SRC_ADMIN%\*" "%INSTALL_DIR%\BibAdmin\" /y /e /q
if %errorlevel% neq 0 ( echo ОШИБКА копирования BibAdmin! & pause & exit /b 1 )

:: Положить установщик в папку updates (клиенты скачают сами)
echo [4/5] Публикуем установщик для клиентов...
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
echo [5/5] Запускаем BibAdminWeb...
start "" "%INSTALL_DIR%\BibAdminWeb\BibAdminWeb.exe"

echo.
echo ============================================
echo   Готово! Версия %VERSION% развёрнута.
echo   BibAdminWeb обновлён и запущен.
echo   Клиенты получат уведомление об обновлении
echo   при следующем запуске BibClient.
echo ============================================
pause
