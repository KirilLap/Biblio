@echo off
chcp 65001 > nul
setlocal enabledelayedexpansion

set SCRIPT_DIR=%~dp0
set ISCC="C:\Program Files (x86)\Inno Setup 6\ISCC.exe"
set VERSION_FILE=%SCRIPT_DIR%VERSION
set SERVER_IP_FILE=%SCRIPT_DIR%SERVER_IP.txt

:: ─── Читаем текущую версию ───────────────────────────────────────────────────
set /p CUR_VER=<"%VERSION_FILE%"
if "%CUR_VER%"=="" ( echo ОШИБКА: файл VERSION пустой! & pause & exit /b 1 )

for /f "tokens=1,2,3 delims=." %%a in ("%CUR_VER%") do (
    set MAJOR=%%a
    set MINOR=%%b
    set PATCH=%%c
)
set /a PATCH1=%PATCH%+1
set /a MINOR1=%MINOR%+1
set /a MAJOR1=%MAJOR%+1

:: ─── Выбор версии ────────────────────────────────────────────────────────────
echo ============================================
echo   Biblio - публикация обновления
echo ============================================
echo.
echo Текущая версия: %CUR_VER%
echo.
echo Какое обновление?
echo   [1] Патч    ^-^> %MAJOR%.%MINOR%.%PATCH1%   (мелкий баг-фикс)
echo   [2] Минор   ^-^> %MAJOR%.%MINOR1%.0    (новая функция)
echo   [3] Мажор   ^-^> %MAJOR1%.0.0       (крупное обновление)
echo   [4] Своя версия
echo.
set /p VER_CHOICE=Выбор [1-4]:

if "%VER_CHOICE%"=="1" set NEW_VER=%MAJOR%.%MINOR%.%PATCH1%
if "%VER_CHOICE%"=="2" set NEW_VER=%MAJOR%.%MINOR1%.0
if "%VER_CHOICE%"=="3" set NEW_VER=%MAJOR1%.0.0
if "%VER_CHOICE%"=="4" (
    set /p NEW_VER=Введи версию ^(например 1.2.0^):
)
if "!NEW_VER!"=="" ( echo ОШИБКА: версия не указана! & pause & exit /b 1 )

:: ─── Описание изменений ───────────────────────────────────────────────────────
echo.
set /p NOTES=Описание изменений (Enter - пропустить):
if "!NOTES!"=="" set NOTES=Обновление !NEW_VER!

:: ─── Что собираем ────────────────────────────────────────────────────────────
echo.
echo Что обновляем?
echo   [1] BibClient
echo   [2] BibAdminWeb
echo   [3] Оба
echo.
set /p BUILD_CHOICE=Выбор [1-3]:

set BUILD_CLIENT=0
set BUILD_ADMINWEB=0
if "%BUILD_CHOICE%"=="1" set BUILD_CLIENT=1
if "%BUILD_CHOICE%"=="2" set BUILD_ADMINWEB=1
if "%BUILD_CHOICE%"=="3" ( set BUILD_CLIENT=1 & set BUILD_ADMINWEB=1 )
if "!BUILD_CLIENT!!BUILD_ADMINWEB!"=="00" ( echo ОШИБКА: неверный выбор! & pause & exit /b 1 )

:: ─── IP сервера ───────────────────────────────────────────────────────────────
set SERVER_IP=
if exist "%SERVER_IP_FILE%" set /p SERVER_IP=<"%SERVER_IP_FILE%"

if "!SERVER_IP!"=="" goto ask_ip_new
echo.
echo Текущий сервер: !SERVER_IP!
set /p NEW_IP=IP сервера (Enter - оставить !SERVER_IP!):
if not "!NEW_IP!"=="" set SERVER_IP=!NEW_IP!
goto ask_ip_done

:ask_ip_new
set /p SERVER_IP=IP сервера (например 172.16.5.2):

:ask_ip_done
if "!SERVER_IP!"=="" ( echo ОШИБКА: IP не указан! & pause & exit /b 1 )
echo !SERVER_IP!>"%SERVER_IP_FILE%"

set UPDATES_DIR=\\!SERVER_IP!\updates

:: ─── Подтверждение ───────────────────────────────────────────────────────────
echo.
echo ============================================
echo   %CUR_VER% ^-^> !NEW_VER!
if "!BUILD_CLIENT!"=="1" echo   Компонент: BibClient
if "!BUILD_ADMINWEB!"=="1" echo   Компонент: BibAdminWeb
echo   Сервер:   !SERVER_IP!
echo   Описание: !NOTES!
echo ============================================
echo.
set /p CONFIRM=Продолжить? [Y/N]:
if /i not "!CONFIRM!"=="Y" ( echo Отменено. & pause & exit /b 0 )

:: ─── Сохраняем версию ────────────────────────────────────────────────────────
echo !NEW_VER!>"%VERSION_FILE%"

if not exist %ISCC% ( echo ОШИБКА: Inno Setup не найден по пути %ISCC%! & pause & exit /b 1 )

:: ─── Сборка BibClient ────────────────────────────────────────────────────────
if "!BUILD_CLIENT!"=="1" (
    echo.
    echo [BibClient] Сборка...
    dotnet publish "%SCRIPT_DIR%BibClient\BibClient.csproj" -p:PublishProfile=win-x64
    if !errorlevel! neq 0 ( echo ОШИБКА сборки BibClient! & pause & exit /b 1 )

    echo [BibClient] Сборка установщика...
    %ISCC% /DAppVersion=!NEW_VER! "%SCRIPT_DIR%installer\bibclient-setup.iss"
    if !errorlevel! neq 0 ( echo ОШИБКА сборки bibclient-setup! & pause & exit /b 1 )
)

:: ─── Сборка BibAdminWeb ───────────────────────────────────────────────────────
if "!BUILD_ADMINWEB!"=="1" (
    echo.
    echo [BibAdminWeb] Сборка...
    dotnet publish "%SCRIPT_DIR%BibAdminWeb\BibAdminWeb.csproj" -p:PublishProfile=win-x64
    if !errorlevel! neq 0 ( echo ОШИБКА сборки BibAdminWeb! & pause & exit /b 1 )

    echo [BibAdminWeb] Сборка установщика...
    %ISCC% /DAppVersion=!NEW_VER! "%SCRIPT_DIR%installer\bibadminweb-setup.iss"
    if !errorlevel! neq 0 ( echo ОШИБКА сборки bibadminweb-setup! & pause & exit /b 1 )
)

:: ─── Публикация на сервер ────────────────────────────────────────────────────
echo.
echo Публикация на \\!SERVER_IP!\updates...

if not exist "!UPDATES_DIR!" (
    mkdir "!UPDATES_DIR!" 2>nul
    if !errorlevel! neq 0 (
        echo ОШИБКА: нет доступа к !UPDATES_DIR!
        echo Убедитесь что папка "updates" расшарена на сервере !SERVER_IP!
        pause & exit /b 1
    )
)

if "!BUILD_CLIENT!"=="1" (
    set CLIENT_EXE=%SCRIPT_DIR%installer\Output\bibclient-setup-!NEW_VER!.exe
    if not exist "!CLIENT_EXE!" ( echo ОШИБКА: файл не найден: !CLIENT_EXE! & pause & exit /b 1 )
    copy /y "!CLIENT_EXE!" "!UPDATES_DIR!\bibclient-setup.exe" >nul
    if !errorlevel! neq 0 ( echo ОШИБКА копирования BibClient! & pause & exit /b 1 )
    echo {"Version":"!NEW_VER!","ReleaseNotes":"!NOTES!","InstallerFile":"bibclient-setup.exe"} > "!UPDATES_DIR!\bibclient-version.json"
    echo [OK] BibClient !NEW_VER! опубликован
)

if "!BUILD_ADMINWEB!"=="1" (
    set ADMINWEB_EXE=%SCRIPT_DIR%installer\Output\bibadminweb-setup-!NEW_VER!.exe
    if not exist "!ADMINWEB_EXE!" ( echo ОШИБКА: файл не найден: !ADMINWEB_EXE! & pause & exit /b 1 )
    copy /y "!ADMINWEB_EXE!" "!UPDATES_DIR!\bibadminweb-setup.exe" >nul
    if !errorlevel! neq 0 ( echo ОШИБКА копирования BibAdminWeb! & pause & exit /b 1 )
    echo {"Version":"!NEW_VER!","ReleaseNotes":"!NOTES!","InstallerFile":"bibadminweb-setup.exe"} > "!UPDATES_DIR!\bibadminweb-version.json"
    echo [OK] BibAdminWeb !NEW_VER! опубликован
)

echo.
echo ============================================
echo   Готово! Версия !NEW_VER! опубликована.
echo   BibAdminWeb -^> кнопка "Обновить клиенты"
echo ============================================
pause
