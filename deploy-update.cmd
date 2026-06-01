@echo off
setlocal enabledelayedexpansion

set "SCRIPT_DIR=%~dp0"
set "ISCC=C:\Program Files (x86)\Inno Setup 6\ISCC.exe"
set "VERSION_FILE=%SCRIPT_DIR%VERSION"
set "SERVER_IP_FILE=%SCRIPT_DIR%SERVER_IP.txt"

title Biblio - Deploy Update

:: Read current version
if not exist "%VERSION_FILE%" ( echo ERROR: VERSION file not found! & pause & exit /b 1 )
set /p CUR_VER=<"%VERSION_FILE%"
if "!CUR_VER!"=="" ( echo ERROR: VERSION file is empty! & pause & exit /b 1 )

for /f "tokens=1,2,3 delims=." %%a in ("!CUR_VER!") do (
    set MAJOR=%%a
    set MINOR=%%b
    set PATCH=%%c
)
set /a PATCH1=!PATCH!+1
set /a MINOR1=!MINOR!+1
set /a MAJOR1=!MAJOR!+1

:: Choose new version
echo.
echo ============================================
echo   Biblio - Deploy Update
echo ============================================
echo.
echo  Current version: !CUR_VER!
echo.
echo  New version?
echo    [1] Patch  !MAJOR!.!MINOR!.!PATCH1!
echo    [2] Minor  !MAJOR!.!MINOR1!.0
echo    [3] Major  !MAJOR1!.0.0
echo    [4] Custom
echo.
set /p VER_CHOICE= Choice [1-4]:

if "!VER_CHOICE!"=="1" set "NEW_VER=!MAJOR!.!MINOR!.!PATCH1!"
if "!VER_CHOICE!"=="2" set "NEW_VER=!MAJOR!.!MINOR1!.0"
if "!VER_CHOICE!"=="3" set "NEW_VER=!MAJOR1!.0.0"
if "!VER_CHOICE!"=="4" set /p NEW_VER= Version (e.g. 1.2.0):
if "!NEW_VER!"=="" ( echo ERROR: No version selected! & pause & exit /b 1 )

:: Release notes
echo.
set /p NOTES= Release notes (Enter to skip):
if "!NOTES!"=="" set "NOTES=Update !NEW_VER!"

:: Component selection
echo.
echo  Build component?
echo    [1] BibClient
echo    [2] BibAdminWeb
echo    [3] Both
echo.
set /p BUILD_CHOICE= Choice [1-3]:

set BUILD_CLIENT=0
set BUILD_ADMINWEB=0
if "!BUILD_CHOICE!"=="1" set BUILD_CLIENT=1
if "!BUILD_CHOICE!"=="2" set BUILD_ADMINWEB=1
if "!BUILD_CHOICE!"=="3" set BUILD_CLIENT=1
if "!BUILD_CHOICE!"=="3" set BUILD_ADMINWEB=1
if "!BUILD_CLIENT!!BUILD_ADMINWEB!"=="00" ( echo ERROR: Choose 1, 2 or 3 & pause & exit /b 1 )

:: Package type
echo.
echo  Package type?
echo    [1] exe  - Inno Setup installer
echo    [2] zip  - fast update, no installer
echo    [3] Both
echo.
set /p PKG_CHOICE= Choice [1-3]:

set PKG_EXE=0
set PKG_ZIP=0
if "!PKG_CHOICE!"=="1" set PKG_EXE=1
if "!PKG_CHOICE!"=="2" set PKG_ZIP=1
if "!PKG_CHOICE!"=="3" set PKG_EXE=1
if "!PKG_CHOICE!"=="3" set PKG_ZIP=1
if "!PKG_EXE!!PKG_ZIP!"=="00" ( echo ERROR: Choose 1, 2 or 3 & pause & exit /b 1 )

:: Check Inno Setup if needed
if "!PKG_EXE!"=="0" goto SKIP_ISCC_CHECK
if not exist "!ISCC!" (
    echo.
    echo  ERROR: Inno Setup not found!
    echo  Path: C:\Program Files (x86)\Inno Setup 6\ISCC.exe
    echo  Download: https://jrsoftware.org/isdl.php
    echo.
    pause & exit /b 1
)
:SKIP_ISCC_CHECK

:: Server IP
set SERVER_IP=
if exist "%SERVER_IP_FILE%" set /p SERVER_IP=<"%SERVER_IP_FILE%"
if "!SERVER_IP!"=="" goto ASK_IP
echo.
echo  Current server: !SERVER_IP!
set /p NEW_IP= Server IP (Enter to keep !SERVER_IP!):
if not "!NEW_IP!"=="" set "SERVER_IP=!NEW_IP!"
goto IP_DONE
:ASK_IP
set /p SERVER_IP= Server IP (e.g. 172.16.5.2):
:IP_DONE
if "!SERVER_IP!"=="" ( echo ERROR: No server IP! & pause & exit /b 1 )
echo !SERVER_IP!>"%SERVER_IP_FILE%"
set "UPDATES_DIR=\\!SERVER_IP!\updates"

:: Confirm
echo.
echo ============================================
echo   !CUR_VER! -^> !NEW_VER!
if "!BUILD_CLIENT!"=="1"   echo   Component : BibClient
if "!BUILD_ADMINWEB!"=="1" echo   Component : BibAdminWeb
if "!PKG_EXE!"=="1"        echo   Package   : exe installer
if "!PKG_ZIP!"=="1"        echo   Package   : zip archive
echo   Server    : !SERVER_IP!
echo   Notes     : !NOTES!
echo ============================================
echo.
set /p CONFIRM= Continue? [Y/N]:
if /i "!CONFIRM!"=="Y" goto BUILD
echo. & echo  Cancelled. & pause & exit /b 0

:BUILD
echo !NEW_VER!>"%VERSION_FILE%"
echo.

:: ── BibClient ──────────────────────────────────────────────────────────
if "!BUILD_CLIENT!"=="0" goto SKIP_CLIENT

echo ============================================
echo  BibClient - dotnet publish...
echo ============================================
dotnet publish "%SCRIPT_DIR%BibClient\BibClient.csproj" -p:PublishProfile=win-x64
if errorlevel 1 ( echo. & echo  ERROR: dotnet publish BibClient failed! & pause & exit /b 1 )

if "!PKG_EXE!"=="0" goto SKIP_CLIENT_EXE
echo.
echo ============================================
echo  BibClient - Inno Setup...
echo ============================================
"!ISCC!" /DAppVersion=!NEW_VER! "%SCRIPT_DIR%installer\bibclient-setup.iss"
if errorlevel 1 ( echo. & echo  ERROR: Inno Setup BibClient failed! & pause & exit /b 1 )
echo  [OK] bibclient-setup-!NEW_VER!.exe built
:SKIP_CLIENT_EXE

if "!PKG_ZIP!"=="0" goto SKIP_CLIENT_ZIP
echo.
echo ============================================
echo  BibClient - creating zip...
echo ============================================
if not exist "!SCRIPT_DIR!installer\Output" mkdir "!SCRIPT_DIR!installer\Output"
set "BZIP_SRC=!SCRIPT_DIR!BibClient\bin\Release\net10.0-windows\win-x64\publish"
set "BZIP_DST=!SCRIPT_DIR!installer\Output\bibclient-update-!NEW_VER!.zip"
if exist "!BZIP_DST!" del /f "!BZIP_DST!"
powershell -NoProfile -Command "Compress-Archive -Path ($env:BZIP_SRC + '\*') -DestinationPath $env:BZIP_DST -Force"
if errorlevel 1 ( echo. & echo  ERROR: zip creation BibClient failed! & pause & exit /b 1 )
echo  [OK] bibclient-update-!NEW_VER!.zip created
:SKIP_CLIENT_ZIP

:SKIP_CLIENT

:: ── BibAdminWeb ─────────────────────────────────────────────────────────
if "!BUILD_ADMINWEB!"=="0" goto SKIP_ADMINWEB

echo.
echo ============================================
echo  BibAdminWeb - dotnet publish...
echo ============================================
dotnet publish "%SCRIPT_DIR%BibAdminWeb\BibAdminWeb.csproj" -p:PublishProfile=win-x64
if errorlevel 1 ( echo. & echo  ERROR: dotnet publish BibAdminWeb failed! & pause & exit /b 1 )

if "!PKG_EXE!"=="0" goto SKIP_ADMINWEB_EXE
echo.
echo ============================================
echo  BibAdminWeb - Inno Setup...
echo ============================================
"!ISCC!" /DAppVersion=!NEW_VER! "%SCRIPT_DIR%installer\bibadminweb-setup.iss"
if errorlevel 1 ( echo. & echo  ERROR: Inno Setup BibAdminWeb failed! & pause & exit /b 1 )
echo  [OK] bibadminweb-setup-!NEW_VER!.exe built
:SKIP_ADMINWEB_EXE

if "!PKG_ZIP!"=="0" goto SKIP_ADMINWEB_ZIP
echo.
echo ============================================
echo  BibAdminWeb - creating zip...
echo ============================================
if not exist "!SCRIPT_DIR!installer\Output" mkdir "!SCRIPT_DIR!installer\Output"
set "AWZIP_SRC=!SCRIPT_DIR!BibAdminWeb\bin\Release\net10.0-windows\win-x64\publish"
set "AWZIP_DST=!SCRIPT_DIR!installer\Output\bibadminweb-update-!NEW_VER!.zip"
if exist "!AWZIP_DST!" del /f "!AWZIP_DST!"
powershell -NoProfile -Command "Compress-Archive -Path ($env:AWZIP_SRC + '\*') -DestinationPath $env:AWZIP_DST -Force"
if errorlevel 1 ( echo. & echo  ERROR: zip creation BibAdminWeb failed! & pause & exit /b 1 )
echo  [OK] bibadminweb-update-!NEW_VER!.zip created
:SKIP_ADMINWEB_ZIP

:SKIP_ADMINWEB

:: ── Publish to server ────────────────────────────────────────────────
echo.
echo ============================================
echo  Publishing to \\!SERVER_IP!\updates...
echo ============================================
if not exist "!UPDATES_DIR!" mkdir "!UPDATES_DIR!" 2>nul
if not exist "!UPDATES_DIR!" (
    echo  ERROR: Cannot access !UPDATES_DIR!
    echo  Set up share named "updates" on !SERVER_IP!
    pause & exit /b 1
)

if "!BUILD_CLIENT!"=="0" goto SKIP_PUBLISH_CLIENT

if "!PKG_EXE!"=="1" (
    set "F=!SCRIPT_DIR!installer\Output\bibclient-setup-!NEW_VER!.exe"
    if not exist "!F!" ( echo  ERROR: not found: !F! & pause & exit /b 1 )
    copy /y "!F!" "!UPDATES_DIR!\bibclient-setup.exe" >nul
    if errorlevel 1 ( echo  ERROR: copy BibClient exe failed! & pause & exit /b 1 )
    echo {"Version":"!NEW_VER!","ReleaseNotes":"!NOTES!","InstallerFile":"bibclient-setup.exe"}>"!UPDATES_DIR!\bibclient-version.json"
    echo {"Version":"!NEW_VER!","ReleaseNotes":"!NOTES!","InstallerFile":"bibclient-setup.exe"}>"!UPDATES_DIR!\version.json"
    echo  [OK] BibClient !NEW_VER! exe published
)
if "!PKG_ZIP!"=="1" (
    set "FZ=!SCRIPT_DIR!installer\Output\bibclient-update-!NEW_VER!.zip"
    if not exist "!FZ!" ( echo  ERROR: not found: !FZ! & pause & exit /b 1 )
    copy /y "!FZ!" "!UPDATES_DIR!\bibclient-update.zip" >nul
    if errorlevel 1 ( echo  ERROR: copy BibClient zip failed! & pause & exit /b 1 )
    echo {"Version":"!NEW_VER!","ReleaseNotes":"!NOTES!"}>"!UPDATES_DIR!\bibclient-zip-version.json"
    echo  [OK] BibClient !NEW_VER! zip published
)

:SKIP_PUBLISH_CLIENT

if "!BUILD_ADMINWEB!"=="0" goto SKIP_PUBLISH_ADMINWEB

if "!PKG_EXE!"=="1" (
    set "FA=!SCRIPT_DIR!installer\Output\bibadminweb-setup-!NEW_VER!.exe"
    if not exist "!FA!" ( echo  ERROR: not found: !FA! & pause & exit /b 1 )
    copy /y "!FA!" "!UPDATES_DIR!\bibadminweb-setup.exe" >nul
    if errorlevel 1 ( echo  ERROR: copy BibAdminWeb exe failed! & pause & exit /b 1 )
    echo {"Version":"!NEW_VER!","ReleaseNotes":"!NOTES!","InstallerFile":"bibadminweb-setup.exe"}>"!UPDATES_DIR!\bibadminweb-version.json"
    echo  [OK] BibAdminWeb !NEW_VER! exe published
)
if "!PKG_ZIP!"=="1" (
    set "FAZ=!SCRIPT_DIR!installer\Output\bibadminweb-update-!NEW_VER!.zip"
    if not exist "!FAZ!" ( echo  ERROR: not found: !FAZ! & pause & exit /b 1 )
    copy /y "!FAZ!" "!UPDATES_DIR!\bibadminweb-update.zip" >nul
    if errorlevel 1 ( echo  ERROR: copy BibAdminWeb zip failed! & pause & exit /b 1 )
    echo {"Version":"!NEW_VER!","ReleaseNotes":"!NOTES!"}>"!UPDATES_DIR!\bibadminweb-zip-version.json"
    echo  [OK] BibAdminWeb !NEW_VER! zip published
)

:SKIP_PUBLISH_ADMINWEB

echo.
echo ============================================
echo   Done! Version !NEW_VER! published.
if "!PKG_EXE!"=="1" echo   Press "Check for updates" in BibAdminWeb.
if "!PKG_ZIP!"=="1" echo   Press "Update clients from folder" in BibAdminWeb.
echo ============================================
echo.
pause
