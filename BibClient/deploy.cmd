@echo off
setlocal

:: Путь установки BibClient на клиентском ПК
set INSTALL_DIR=C:\Program Files\BibClient

echo ============================================
echo   Обновление BibClient
echo ============================================
echo.
echo Папка установки: %INSTALL_DIR%
echo Источник:        %~dp0
echo.
echo Нажмите любую клавишу для продолжения или Ctrl+C для отмены...
pause >nul

:: Права администратора обязательны
net session >nul 2>&1
if %errorlevel% neq 0 (
    echo ОШИБКА: Запустите от имени администратора!
    pause
    exit /b 1
)

echo.
echo [1/4] Остановка службы BibClientWatchdog...
sc stop BibClientWatchdog >nul 2>&1
timeout /t 3 /nobreak >nul

echo [2/4] Завершение процесса BibClient...
taskkill /f /im BibClient.exe >nul 2>&1
timeout /t 2 /nobreak >nul

echo [3/4] Копирование файлов...
if not exist "%INSTALL_DIR%" mkdir "%INSTALL_DIR%"
xcopy /y /e /q "%~dp0*.*" "%INSTALL_DIR%\"
if %errorlevel% neq 0 (
    echo ОШИБКА при копировании файлов!
    pause
    exit /b 1
)

echo [4/4] Запуск службы BibClientWatchdog...
sc start BibClientWatchdog >nul 2>&1
timeout /t 2 /nobreak >nul

echo.
echo Готово! BibClient обновлён и запущен.
pause
