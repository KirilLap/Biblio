@echo off
setlocal
:: Путь к установленному BibAdminWeb (менять если другая папка)
set INSTALL_DIR=C:\Program Files\Biblio

:: Папка с новыми собранными файлами
set SRC_ADMINWEB=%~dp0BibAdminWeb\bin\Publish\win-x64
set SRC_ADMIN=%~dp0BibAdmin\bin\Publish\win-x64
set INSTALLER=%~dp0installer\Output\biblio-setup-1.0.0.exe

echo ============================================
echo   Biblio - обновление сервера
echo ============================================
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

:: Копируем установщик
copy /y "%INSTALLER%" "%INSTALL_DIR%\BibAdminWeb\updates\biblio-setup.exe" >nul
if %errorlevel% neq 0 ( echo ОШИБКА: установщик не найден: %INSTALLER% & pause & exit /b 1 )

:: Создаём version.json (клиенты сравнивают версию с этим файлом)
echo {"Version":"1.0.1","ReleaseNotes":"Исправлена настройка IP, загрузка фона, окно настроек","InstallerFile":"biblio-setup.exe"} > "%INSTALL_DIR%\BibAdminWeb\updates\version.json"

:: Запустить BibAdminWeb
echo [5/5] Запускаем BibAdminWeb...
start "" "%INSTALL_DIR%\BibAdminWeb\BibAdminWeb.exe"

echo.
echo ============================================
echo   Готово!
echo   BibAdminWeb обновлён и запущен.
echo   Клиенты получат уведомление об обновлении
echo   при следующем запуске BibClient.
echo ============================================
pause
