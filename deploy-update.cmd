@echo off
chcp 65001 > nul
setlocal enabledelayedexpansion

set SCRIPT_DIR=%~dp0
set SERVER_IP_FILE=%SCRIPT_DIR%SERVER_IP.txt

echo ============================================
echo   Biblio - сборка и публикация обновления
echo ============================================
echo.

:: Читаем сохраненный IP
set SERVER_IP=
if exist "%SERVER_IP_FILE%" set /p SERVER_IP=<"%SERVER_IP_FILE%"

:: Спрашиваем IP
if "%SERVER_IP%"=="" goto ask_ip_new
echo Текущий сервер: %SERVER_IP%
set /p NEW_IP=IP сервера (Enter - оставить %SERVER_IP%):
if not "!NEW_IP!"=="" set SERVER_IP=!NEW_IP!
goto ask_ip_done

:ask_ip_new
set /p SERVER_IP=IP сервера (например 172.16.5.2):

:ask_ip_done
if "%SERVER_IP%"=="" (
    echo ОШИБКА: IP сервера не указан!
    pause & exit /b 1
)
echo %SERVER_IP%> "%SERVER_IP_FILE%"

:: Спрашиваем версию
set /p VERSION=Новая версия (например 1.0.2):
if "%VERSION%"=="" (
    echo ОШИБКА: версия не указана!
    pause & exit /b 1
)

:: Спрашиваем описание
set /p NOTES=Описание изменений (Enter - пропустить):
if "%NOTES%"=="" set NOTES=Обновление %VERSION%

set UPDATES_DIR=\\%SERVER_IP%\updates

echo.
echo   Сервер:   %SERVER_IP%
echo   Папка:    %UPDATES_DIR%
echo   Версия:   %VERSION%
echo   Описание: %NOTES%
echo.

:: [1/3] Записываем версию
echo %VERSION%> "%SCRIPT_DIR%VERSION"
echo [1/3] Версия %VERSION% записана в VERSION

:: [2/3] Сборка
echo.
echo [2/3] Сборка...
echo ----------------------------------------
set DEPLOY_MODE=1
call "%SCRIPT_DIR%build.cmd"
if %errorlevel% neq 0 (
    echo.
    echo ОШИБКА сборки! Публикация отменена.
    pause & exit /b 1
)
echo ----------------------------------------

:: Проверяем установщик
set INSTALLER=%SCRIPT_DIR%installer\Output\biblio-setup-%VERSION%.exe
if not exist "%INSTALLER%" (
    echo ОШИБКА: установщик не найден: %INSTALLER%
    pause & exit /b 1
)

:: [3/3] Публикация
echo.
echo [3/3] Публикация на %UPDATES_DIR%...
if not exist "%UPDATES_DIR%" (
    mkdir "%UPDATES_DIR%" 2>nul
    if %errorlevel% neq 0 (
        echo ОШИБКА: нет доступа к %UPDATES_DIR%
        echo Убедитесь что папка "updates" расшарена на сервере %SERVER_IP%
        pause & exit /b 1
    )
)

copy /y "%INSTALLER%" "%UPDATES_DIR%\biblio-setup.exe" >nul
if %errorlevel% neq 0 (
    echo ОШИБКА копирования установщика!
    pause & exit /b 1
)

echo {"Version":"%VERSION%","ReleaseNotes":"%NOTES%","InstallerFile":"biblio-setup.exe"} > "%UPDATES_DIR%\version.json"

echo.
echo ============================================
echo   Готово! Версия %VERSION% опубликована.
echo   BibAdminWeb -^> Настройки -^> Проверить обновления
echo ============================================
pause
