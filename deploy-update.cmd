@echo off
setlocal enabledelayedexpansion

set SCRIPT_DIR=%~dp0
set ISCC="C:\Program Files (x86)\Inno Setup 6\ISCC.exe"
set VERSION_FILE=%SCRIPT_DIR%VERSION
set SERVER_IP_FILE=%SCRIPT_DIR%SERVER_IP.txt

title Biblio - публикация обновления

:: Проверяем Inno Setup сразу
if not exist %ISCC% (
    echo.
    echo  ОШИБКА: Inno Setup не найден!
    echo  Путь: C:\Program Files ^(x86^)\Inno Setup 6\ISCC.exe
    echo  Скачайте: https://jrsoftware.org/isdl.php
    echo.
    pause & exit /b 1
)

:: Читаем текущую версию
if not exist "%VERSION_FILE%" ( echo ОШИБКА: файл VERSION не найден! & pause & exit /b 1 )
set /p CUR_VER=<"%VERSION_FILE%"
if "!CUR_VER!"=="" ( echo ОШИБКА: файл VERSION пустой! & pause & exit /b 1 )

for /f "tokens=1,2,3 delims=." %%a in ("!CUR_VER!") do (
    set MAJOR=%%a
    set MINOR=%%b
    set PATCH=%%c
)
set /a PATCH1=!PATCH!+1
set /a MINOR1=!MINOR!+1
set /a MAJOR1=!MAJOR!+1

:: Выбор версии
echo.
echo ============================================
echo   Biblio - публикация обновления
echo ============================================
echo.
echo  Текущая версия: !CUR_VER!
echo.
echo  Какое обновление?
echo    [1] Патч  -^> !MAJOR!.!MINOR!.!PATCH1!  (мелкий баг-фикс)
echo    [2] Минор -^> !MAJOR!.!MINOR1!.0  (новая функция)
echo    [3] Мажор -^> !MAJOR1!.0.0  (крупное обновление)
echo    [4] Своя версия
echo.
set /p VER_CHOICE= Выбор [1-4]:

if "!VER_CHOICE!"=="1" set NEW_VER=!MAJOR!.!MINOR!.!PATCH1!
if "!VER_CHOICE!"=="2" set NEW_VER=!MAJOR!.!MINOR1!.0
if "!VER_CHOICE!"=="3" set NEW_VER=!MAJOR1!.0.0
if "!VER_CHOICE!"=="4" set /p NEW_VER= Введи версию (например 1.2.0):
if "!NEW_VER!"=="" ( echo ОШИБКА: версия не выбрана! & pause & exit /b 1 )

:: Описание
echo.
set /p NOTES= Описание изменений (Enter - пропустить):
if "!NOTES!"=="" set NOTES=Обновление !NEW_VER!

:: Что собираем
echo.
echo  Что обновляем?
echo    [1] BibClient
echo    [2] BibAdminWeb
echo    [3] Оба
echo.
set /p BUILD_CHOICE= Выбор [1-3]:

set BUILD_CLIENT=0
set BUILD_ADMINWEB=0
if "!BUILD_CHOICE!"=="1" set BUILD_CLIENT=1
if "!BUILD_CHOICE!"=="2" set BUILD_ADMINWEB=1
if "!BUILD_CHOICE!"=="3" set BUILD_CLIENT=1
if "!BUILD_CHOICE!"=="3" set BUILD_ADMINWEB=1
if "!BUILD_CLIENT!!BUILD_ADMINWEB!"=="00" ( echo ОШИБКА: введи 1, 2 или 3 & pause & exit /b 1 )

:: IP сервера
set SERVER_IP=
if exist "%SERVER_IP_FILE%" set /p SERVER_IP=<"%SERVER_IP_FILE%"
if "!SERVER_IP!"=="" goto ASK_IP
echo.
echo  Текущий сервер: !SERVER_IP!
set /p NEW_IP= IP сервера (Enter - оставить !SERVER_IP!):
if not "!NEW_IP!"=="" set SERVER_IP=!NEW_IP!
goto IP_DONE
:ASK_IP
set /p SERVER_IP= IP сервера (например 172.16.5.2):
:IP_DONE
if "!SERVER_IP!"=="" ( echo ОШИБКА: IP не указан! & pause & exit /b 1 )
echo !SERVER_IP!>"%SERVER_IP_FILE%"
set UPDATES_DIR=\\!SERVER_IP!\updates

:: Подтверждение
echo.
echo ============================================
echo   !CUR_VER! -^> !NEW_VER!
if "!BUILD_CLIENT!"=="1"   echo   Компонент : BibClient
if "!BUILD_ADMINWEB!"=="1" echo   Компонент : BibAdminWeb
echo   Сервер    : !SERVER_IP!
echo   Описание  : !NOTES!
echo ============================================
echo.
set /p CONFIRM= Продолжить? [Y/N]:
if /i "!CONFIRM!"=="Y" goto BUILD
echo. & echo  Отменено. & pause & exit /b 0

:BUILD
echo !NEW_VER!>"%VERSION_FILE%"
echo.

:: Сборка BibClient
if "!BUILD_CLIENT!"=="0" goto SKIP_CLIENT
echo ============================================
echo  BibClient - dotnet publish...
echo ============================================
dotnet publish "%SCRIPT_DIR%BibClient\BibClient.csproj" -p:PublishProfile=win-x64
if errorlevel 1 ( echo. & echo  ОШИБКА dotnet publish BibClient! & pause & exit /b 1 )
echo.
echo ============================================
echo  BibClient - Inno Setup...
echo ============================================
%ISCC% /DAppVersion=!NEW_VER! "%SCRIPT_DIR%installer\bibclient-setup.iss"
if errorlevel 1 ( echo. & echo  ОШИБКА Inno Setup BibClient! & pause & exit /b 1 )
echo  [OK] bibclient-setup-!NEW_VER!.exe собран
:SKIP_CLIENT

:: Сборка BibAdminWeb
if "!BUILD_ADMINWEB!"=="0" goto SKIP_ADMINWEB
echo.
echo ============================================
echo  BibAdminWeb - dotnet publish...
echo ============================================
dotnet publish "%SCRIPT_DIR%BibAdminWeb\BibAdminWeb.csproj" -p:PublishProfile=win-x64
if errorlevel 1 ( echo. & echo  ОШИБКА dotnet publish BibAdminWeb! & pause & exit /b 1 )
echo.
echo ============================================
echo  BibAdminWeb - Inno Setup...
echo ============================================
%ISCC% /DAppVersion=!NEW_VER! "%SCRIPT_DIR%installer\bibadminweb-setup.iss"
if errorlevel 1 ( echo. & echo  ОШИБКА Inno Setup BibAdminWeb! & pause & exit /b 1 )
echo  [OK] bibadminweb-setup-!NEW_VER!.exe собран
:SKIP_ADMINWEB

:: Публикация на сервер
echo.
echo ============================================
echo  Публикация на \\!SERVER_IP!\updates...
echo ============================================
if not exist "!UPDATES_DIR!" mkdir "!UPDATES_DIR!" 2>nul
if not exist "!UPDATES_DIR!" (
    echo  ОШИБКА: нет доступа к !UPDATES_DIR!
    echo  Убедитесь что папка updates расшарена на сервере !SERVER_IP!
    pause & exit /b 1
)

if "!BUILD_CLIENT!"=="1" (
    set F=!SCRIPT_DIR!installer\Output\bibclient-setup-!NEW_VER!.exe
    if not exist "!F!" ( echo  ОШИБКА: не найден !F! & pause & exit /b 1 )
    copy /y "!F!" "!UPDATES_DIR!\bibclient-setup.exe" >nul
    if errorlevel 1 ( echo  ОШИБКА копирования BibClient! & pause & exit /b 1 )
    (echo {"Version":"!NEW_VER!","ReleaseNotes":"!NOTES!","InstallerFile":"bibclient-setup.exe"})>"!UPDATES_DIR!\bibclient-version.json"
    echo  [OK] BibClient !NEW_VER! опубликован
)
if "!BUILD_ADMINWEB!"=="1" (
    set F=!SCRIPT_DIR!installer\Output\bibadminweb-setup-!NEW_VER!.exe
    if not exist "!F!" ( echo  ОШИБКА: не найден !F! & pause & exit /b 1 )
    copy /y "!F!" "!UPDATES_DIR!\bibadminweb-setup.exe" >nul
    if errorlevel 1 ( echo  ОШИБКА копирования BibAdminWeb! & pause & exit /b 1 )
    (echo {"Version":"!NEW_VER!","ReleaseNotes":"!NOTES!","InstallerFile":"bibadminweb-setup.exe"})>"!UPDATES_DIR!\bibadminweb-version.json"
    echo  [OK] BibAdminWeb !NEW_VER! опубликован
)

echo.
echo ============================================
echo   ГОТОВО! Версия !NEW_VER! опубликована.
echo   Нажми "Обновить клиенты" в BibAdminWeb.
echo ============================================
echo.
pause