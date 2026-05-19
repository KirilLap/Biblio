@echo off
chcp 65001 > nul
setlocal

set SCRIPT_DIR=%~dp0

:: Читаем текущую версию
set /p VERSION=<"%SCRIPT_DIR%VERSION"
if "%VERSION%"=="" ( echo ОШИБКА: файл VERSION пустой! & pause & exit /b 1 )

echo ============================================
echo   Biblio %VERSION% - сборка полного установщика
echo ============================================
echo.
echo Этот скрипт собирает biblio-setup.exe для первичной установки.
echo Обновления клиентских ПК — через deploy-update.cmd
echo.
echo Нажми Enter для продолжения или Ctrl+C для отмены...
pause >nul

set DEPLOY_MODE=1
call "%SCRIPT_DIR%build.cmd"
if %errorlevel% neq 0 ( pause & exit /b 1 )
