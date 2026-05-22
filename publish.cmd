@echo off
cd /d "%~dp0"
echo Building BibAdminWeb...
dotnet publish BibAdminWeb -c Release -o BibAdminWeb\bin\Publish\win-x64
if %errorlevel% neq 0 (
    echo Build failed!
    pause
    exit /b 1
)
echo.
echo Done! Files in: BibAdminWeb\bin\Publish\win-x64
pause
