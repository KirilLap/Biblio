@echo off
setlocal

:: BibClient installation path on the client PC
set INSTALL_DIR=C:\Program Files\BibClient

echo ============================================
echo   BibClient Update
echo ============================================
echo.
echo Install dir: %INSTALL_DIR%
echo Source:      %~dp0
echo.
echo Press any key to continue or Ctrl+C to cancel...
pause >nul

:: Administrator rights required
net session >nul 2>&1
if %errorlevel% neq 0 (
    echo ERROR: Run as Administrator!
    pause
    exit /b 1
)

echo.
echo [1/4] Stopping BibClientWatchdog service...
sc stop BibClientWatchdog >nul 2>&1
timeout /t 3 /nobreak >nul

echo [2/4] Killing BibClient process...
taskkill /f /im BibClient.exe >nul 2>&1
timeout /t 2 /nobreak >nul

echo [3/4] Copying files...
if not exist "%INSTALL_DIR%" mkdir "%INSTALL_DIR%"
xcopy /y /e /q "%~dp0*.*" "%INSTALL_DIR%\"
if %errorlevel% neq 0 (
    echo ERROR: File copy failed!
    pause
    exit /b 1
)

echo [4/4] Starting BibClientWatchdog service...
sc start BibClientWatchdog >nul 2>&1
timeout /t 2 /nobreak >nul

echo.
echo Done! BibClient updated and started.
pause
