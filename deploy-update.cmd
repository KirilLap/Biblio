@echo off
setlocal

:: ============================================================
:: deploy-update.cmd
::
:: 1. Спрашивает версию и описание
:: 2. Записывает версию в файл VERSION
:: 3. Запускает build.cmd (сборка всего)
:: 4. Копирует установщик и version.json в папку на сервере
:: ============================================================

set UPDATES_DIR=\\172.16.5.2\updates
set SCRIPT_DIR=%~dp0

echo ============================================
echo   Biblio — сборка и публикация обновления
echo ============================================
echo.

:: Запрашиваем версию
set /p VERSION=Введите новую версию (например 1.0.2):
if "%VERSION%"=="" ( echo ОШИБКА: версия не указана! & pause & exit /b 1 )

:: Запрашиваем описание
set /p NOTES=Описание изменений (Enter — пропустить):
if "%NOTES%"=="" set NOTES=Обновление %VERSION%

echo.
echo Версия:    %VERSION%
echo Описание:  %NOTES%
echo Сервер:    %UPDATES_DIR%
echo.

:: Записываем версию в файл VERSION
echo %VERSION%> "%SCRIPT_DIR%VERSION"
echo [1/3] Версия %VERSION% записана в VERSION

:: Запускаем сборку (DEPLOY_MODE подавляет pause в конце build.cmd)
echo.
echo [2/3] Сборка...
echo ────────────────────────────────────────────
set DEPLOY_MODE=1
call "%SCRIPT_DIR%build.cmd"
if %errorlevel% neq 0 ( echo. & echo ОШИБКА сборки! Публикация отменена. & pause & exit /b 1 )
echo ────────────────────────────────────────────

:: Проверяем наличие собранного установщика
set INSTALLER=%SCRIPT_DIR%installer\Output\biblio-setup-%VERSION%.exe
if not exist "%INSTALLER%" (
    echo ОШИБКА: установщик не найден после сборки: %INSTALLER%
    pause & exit /b 1
)

:: Проверяем доступность сервера
echo.
echo [3/3] Публикация на сервер %UPDATES_DIR%...
if not exist "%UPDATES_DIR%" (
    echo Создаём папку %UPDATES_DIR%...
    mkdir "%UPDATES_DIR%" 2>nul
    if %errorlevel% neq 0 (
        echo ОШИБКА: не удалось создать папку. Проверьте доступ к серверу 172.16.5.2
        pause & exit /b 1
    )
)

:: Копируем установщик
copy /y "%INSTALLER%" "%UPDATES_DIR%\biblio-setup.exe" >nul
if %errorlevel% neq 0 ( echo ОШИБКА копирования установщика! & pause & exit /b 1 )

:: Создаём version.json
echo {"Version":"%VERSION%","ReleaseNotes":"%NOTES%","InstallerFile":"biblio-setup.exe"} > "%UPDATES_DIR%\version.json"
if %errorlevel% neq 0 ( echo ОШИБКА создания version.json! & pause & exit /b 1 )

echo.
echo ============================================
echo   Готово! Версия %VERSION% опубликована.
echo.
echo   Откройте BibAdminWeb -^> Настройки
echo   -^> "Проверить обновления" -^> Установить
echo ============================================
pause
