@echo off
chcp 65001 >nul
setlocal enabledelayedexpansion

set "SCRIPT_DIR=%~dp0"
set "ISCC=C:\Program Files (x86)\Inno Setup 6\ISCC.exe"
set "VERSION_FILE=%SCRIPT_DIR%VERSION"
set "SERVER_IP_FILE=%SCRIPT_DIR%SERVER_IP.txt"

title Biblio - Публикация обновления

:: Читаем текущую версию
if not exist "%VERSION_FILE%" ( echo Ошибка: файл VERSION не найден! & pause & exit /b 1 )
set /p CUR_VER=<"%VERSION_FILE%"
if "!CUR_VER!"=="" ( echo Ошибка: файл VERSION пустой! & pause & exit /b 1 )

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
echo   Biblio - Публикация обновления
echo ============================================
echo.
echo  Текущая версия: !CUR_VER!
echo.
echo  Новая версия?
echo    [1] Патч   !MAJOR!.!MINOR!.!PATCH1!  (мелкий баг-фикс)
echo    [2] Минор  !MAJOR!.!MINOR1!.0  (новый функционал)
echo    [3] Мажор  !MAJOR1!.0.0  (крупное обновление)
echo    [4] Своя версия
echo.
set /p VER_CHOICE= Выбор [1-4]:

if "!VER_CHOICE!"=="1" set "NEW_VER=!MAJOR!.!MINOR!.!PATCH1!"
if "!VER_CHOICE!"=="2" set "NEW_VER=!MAJOR!.!MINOR1!.0"
if "!VER_CHOICE!"=="3" set "NEW_VER=!MAJOR1!.0.0"
if "!VER_CHOICE!"=="4" set /p NEW_VER= Версия (например 1.2.0):
if "!NEW_VER!"=="" ( echo Ошибка: версия не выбрана! & pause & exit /b 1 )

:: Release notes
echo.
set /p NOTES= Описание изменений (Enter - пропустить):
if "!NOTES!"=="" set "NOTES=Обновление !NEW_VER!"

:: Что собираем
echo.
echo  Что собираем?
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
if "!BUILD_CLIENT!!BUILD_ADMINWEB!"=="00" ( echo Ошибка: выберите 1, 2 или 3 & pause & exit /b 1 )

:: Тип пакета
echo.
echo  Тип пакета?
echo    [1] exe  (Inno Setup, полноценная установка)
echo    [2] zip  (без установщика, быстрое обновление)
echo    [3] Оба типа
echo.
set /p PKG_CHOICE= Выбор [1-3]:

set PKG_EXE=0
set PKG_ZIP=0
if "!PKG_CHOICE!"=="1" set PKG_EXE=1
if "!PKG_CHOICE!"=="2" set PKG_ZIP=1
if "!PKG_CHOICE!"=="3" set PKG_EXE=1
if "!PKG_CHOICE!"=="3" set PKG_ZIP=1
if "!PKG_EXE!!PKG_ZIP!"=="00" ( echo Ошибка: выберите 1, 2 или 3 & pause & exit /b 1 )

:: Проверяем Inno Setup только если нужен exe
if "!PKG_EXE!"=="0" goto SKIP_ISCC_CHECK
if not exist "!ISCC!" (
    echo.
    echo  Ошибка: Inno Setup не найден!
    echo  Путь: C:\Program Files (x86)\Inno Setup 6\ISCC.exe
    echo  Скачать: https://jrsoftware.org/isdl.php
    echo.
    pause & exit /b 1
)
:SKIP_ISCC_CHECK

:: IP сервера
set SERVER_IP=
if exist "%SERVER_IP_FILE%" set /p SERVER_IP=<"%SERVER_IP_FILE%"
if "!SERVER_IP!"=="" goto ASK_IP
echo.
echo  Текущий сервер: !SERVER_IP!
set /p NEW_IP= IP сервера (Enter - оставить !SERVER_IP!):
if not "!NEW_IP!"=="" set "SERVER_IP=!NEW_IP!"
goto IP_DONE
:ASK_IP
set /p SERVER_IP= IP сервера (например 172.16.5.2):
:IP_DONE
if "!SERVER_IP!"=="" ( echo Ошибка: IP не указан! & pause & exit /b 1 )
echo !SERVER_IP!>"%SERVER_IP_FILE%"
set "UPDATES_DIR=\\!SERVER_IP!\updates"

:: Подтверждение
echo.
echo ============================================
echo   !CUR_VER! -^> !NEW_VER!
if "!BUILD_CLIENT!"=="1"   echo   Компонент  : BibClient
if "!BUILD_ADMINWEB!"=="1" echo   Компонент  : BibAdminWeb
if "!PKG_EXE!"=="1"        echo   Пакет      : exe установщик
if "!PKG_ZIP!"=="1"        echo   Пакет      : zip архив
echo   Сервер     : !SERVER_IP!
echo   Описание   : !NOTES!
echo ============================================
echo.
set /p CONFIRM= Продолжить? [Y/N]:
if /i "!CONFIRM!"=="Y" goto BUILD
echo. & echo  Отменено. & pause & exit /b 0

:BUILD
echo !NEW_VER!>"%VERSION_FILE%"
echo.

:: ── BibClient ──────────────────────────────────────────────────────────
if "!BUILD_CLIENT!"=="0" goto SKIP_CLIENT

echo ============================================
echo  BibClient - dotnet publish...
echo ============================================
dotnet publish "%SCRIPT_DIR%BibClient\BibClient.csproj" -p:PublishProfile=win-x64
if errorlevel 1 ( echo. & echo  Ошибка dotnet publish BibClient! & pause & exit /b 1 )

if "!PKG_EXE!"=="0" goto SKIP_CLIENT_EXE
echo.
echo ============================================
echo  BibClient - Inno Setup...
echo ============================================
"!ISCC!" /DAppVersion=!NEW_VER! "%SCRIPT_DIR%installer\bibclient-setup.iss"
if errorlevel 1 ( echo. & echo  Ошибка Inno Setup BibClient! & pause & exit /b 1 )
echo  [OK] bibclient-setup-!NEW_VER!.exe собран
:SKIP_CLIENT_EXE

if "!PKG_ZIP!"=="0" goto SKIP_CLIENT_ZIP
echo.
echo ============================================
echo  BibClient - создание zip...
echo ============================================
if not exist "!SCRIPT_DIR!installer\Output" mkdir "!SCRIPT_DIR!installer\Output"
set "BZIP_SRC=!SCRIPT_DIR!BibClient\bin\Release\net10.0-windows\win-x64\publish"
set "BZIP_DST=!SCRIPT_DIR!installer\Output\bibclient-update-!NEW_VER!.zip"
if exist "!BZIP_DST!" del /f "!BZIP_DST!"
powershell -NoProfile -Command "Compress-Archive -Path ($env:BZIP_SRC + '\*') -DestinationPath $env:BZIP_DST -Force"
if errorlevel 1 ( echo. & echo  Ошибка создания zip BibClient! & pause & exit /b 1 )
echo  [OK] bibclient-update-!NEW_VER!.zip создан
:SKIP_CLIENT_ZIP

:SKIP_CLIENT

:: ── BibAdminWeb ─────────────────────────────────────────────────────────
if "!BUILD_ADMINWEB!"=="0" goto SKIP_ADMINWEB

echo.
echo ============================================
echo  BibAdminWeb - dotnet publish...
echo ============================================
dotnet publish "%SCRIPT_DIR%BibAdminWeb\BibAdminWeb.csproj" -p:PublishProfile=win-x64
if errorlevel 1 ( echo. & echo  Ошибка dotnet publish BibAdminWeb! & pause & exit /b 1 )

if "!PKG_EXE!"=="0" goto SKIP_ADMINWEB_EXE
echo.
echo ============================================
echo  BibAdminWeb - Inno Setup...
echo ============================================
"!ISCC!" /DAppVersion=!NEW_VER! "%SCRIPT_DIR%installer\bibadminweb-setup.iss"
if errorlevel 1 ( echo. & echo  Ошибка Inno Setup BibAdminWeb! & pause & exit /b 1 )
echo  [OK] bibadminweb-setup-!NEW_VER!.exe собран
:SKIP_ADMINWEB_EXE

if "!PKG_ZIP!"=="0" goto SKIP_ADMINWEB_ZIP
echo.
echo ============================================
echo  BibAdminWeb - создание zip...
echo ============================================
if not exist "!SCRIPT_DIR!installer\Output" mkdir "!SCRIPT_DIR!installer\Output"
set "AWZIP_SRC=!SCRIPT_DIR!BibAdminWeb\bin\Release\net10.0-windows\win-x64\publish"
set "AWZIP_DST=!SCRIPT_DIR!installer\Output\bibadminweb-update-!NEW_VER!.zip"
if exist "!AWZIP_DST!" del /f "!AWZIP_DST!"
powershell -NoProfile -Command "Compress-Archive -Path ($env:AWZIP_SRC + '\*') -DestinationPath $env:AWZIP_DST -Force"
if errorlevel 1 ( echo. & echo  Ошибка создания zip BibAdminWeb! & pause & exit /b 1 )
echo  [OK] bibadminweb-update-!NEW_VER!.zip создан
:SKIP_ADMINWEB_ZIP

:SKIP_ADMINWEB

:: ── Публикация на сервер ────────────────────────────────────────────────
echo.
echo ============================================
echo  Публикация на \\!SERVER_IP!\updates...
echo ============================================
if not exist "!UPDATES_DIR!" mkdir "!UPDATES_DIR!" 2>nul
if not exist "!UPDATES_DIR!" (
    echo  Ошибка: нет доступа к !UPDATES_DIR!
    echo  Настройте шаринг папки updates с именем "updates" на !SERVER_IP!
    pause & exit /b 1
)

if "!BUILD_CLIENT!"=="0" goto SKIP_PUBLISH_CLIENT

if "!PKG_EXE!"=="1" (
    set "F=!SCRIPT_DIR!installer\Output\bibclient-setup-!NEW_VER!.exe"
    if not exist "!F!" ( echo  Ошибка: не найден !F! & pause & exit /b 1 )
    copy /y "!F!" "!UPDATES_DIR!\bibclient-setup.exe" >nul
    if errorlevel 1 ( echo  Ошибка копирования BibClient exe! & pause & exit /b 1 )
    echo {"Version":"!NEW_VER!","ReleaseNotes":"!NOTES!","InstallerFile":"bibclient-setup.exe"}>"!UPDATES_DIR!\bibclient-version.json"
    echo {"Version":"!NEW_VER!","ReleaseNotes":"!NOTES!","InstallerFile":"bibclient-setup.exe"}>"!UPDATES_DIR!\version.json"
    echo  [OK] BibClient !NEW_VER! exe опубликован
)
if "!PKG_ZIP!"=="1" (
    set "FZ=!SCRIPT_DIR!installer\Output\bibclient-update-!NEW_VER!.zip"
    if not exist "!FZ!" ( echo  Ошибка: не найден !FZ! & pause & exit /b 1 )
    copy /y "!FZ!" "!UPDATES_DIR!\bibclient-update.zip" >nul
    if errorlevel 1 ( echo  Ошибка копирования BibClient zip! & pause & exit /b 1 )
    echo {"Version":"!NEW_VER!","ReleaseNotes":"!NOTES!"}>"!UPDATES_DIR!\bibclient-zip-version.json"
    echo  [OK] BibClient !NEW_VER! zip опубликован
)

:SKIP_PUBLISH_CLIENT

if "!BUILD_ADMINWEB!"=="0" goto SKIP_PUBLISH_ADMINWEB

if "!PKG_EXE!"=="1" (
    set "FA=!SCRIPT_DIR!installer\Output\bibadminweb-setup-!NEW_VER!.exe"
    if not exist "!FA!" ( echo  Ошибка: не найден !FA! & pause & exit /b 1 )
    copy /y "!FA!" "!UPDATES_DIR!\bibadminweb-setup.exe" >nul
    if errorlevel 1 ( echo  Ошибка копирования BibAdminWeb exe! & pause & exit /b 1 )
    echo {"Version":"!NEW_VER!","ReleaseNotes":"!NOTES!","InstallerFile":"bibadminweb-setup.exe"}>"!UPDATES_DIR!\bibadminweb-version.json"
    echo  [OK] BibAdminWeb !NEW_VER! exe опубликован
)
if "!PKG_ZIP!"=="1" (
    set "FAZ=!SCRIPT_DIR!installer\Output\bibadminweb-update-!NEW_VER!.zip"
    if not exist "!FAZ!" ( echo  Ошибка: не найден !FAZ! & pause & exit /b 1 )
    copy /y "!FAZ!" "!UPDATES_DIR!\bibadminweb-update.zip" >nul
    if errorlevel 1 ( echo  Ошибка копирования BibAdminWeb zip! & pause & exit /b 1 )
    echo {"Version":"!NEW_VER!","ReleaseNotes":"!NOTES!"}>"!UPDATES_DIR!\bibadminweb-zip-version.json"
    echo  [OK] BibAdminWeb !NEW_VER! zip опубликован
)

:SKIP_PUBLISH_ADMINWEB

echo.
echo ============================================
echo   Готово! Версия !NEW_VER! опубликована.
if "!PKG_EXE!"=="1" echo   Нажмите "Проверить обновления" в BibAdminWeb.
if "!PKG_ZIP!"=="1" echo   Нажмите "Обновить клиенты из папки" в BibAdminWeb.
echo ============================================
echo.
pause
