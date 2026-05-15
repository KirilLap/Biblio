@echo off
chcp 65001 > nul
setlocal

:: ============================================================
:: deploy-update.cmd
::
:: 1. Спрашивает IP сервера, версию и описание
:: 2. Записывает версию в файл VERSION
:: 3. Запускает build.cmd (сборка всего)
:: 4. Копирует установщик и version.json на сервер
::
:: IP сервера сохраняется в файл SERVER_IP.txt рядом со скриптом.
:: При следующем запуске подставляется автоматически.
:: ============================================================

set SCRIPT_DIR=%~dp0
set SERVER_IP_FILE=%SCRIPT_DIR%SERVER_IP.txt

echo ============================================
echo   Biblio — сборка и публикация обновления
echo ============================================
echo.

:: Читаем сохранённый IP сервера (если есть)
set SAVED_IP=
if exist "%SERVER_IP_FILE%" set /p SAVED_IP=<"%SERVER_IP_FILE%"

:: Спрашиваем IP — показываем сохранённый как подсказку
if "%SAVED_IP%"=="" (
    set /p SERVER_IP=IP адрес сервера (например 172.16.5.2):
) else (
    set /p SERVER_IP=IP адрес сервера (Enter = %SAVED_IP%):
    if "!SERVER_IP!"=="" set SERVER_IP=%SAVED_IP%
)

:: Включаем расширения для !переменных!
setlocal enabledelayedexpansion
if "%SERVER_IP%"=="" set SERVER_IP=%SAVED_IP%
if "%SERVER_IP%"=="" ( echo ОШИБКА: IP сервера не указан! & pause & exit /b 1 )

:: Сохраняем IP для следующего раза
echo %SERVER_IP%> "%SERVER_IP_FILE%"

set UPDATES_DIR=\\%SERVER_IP%\updates

:: Запрашиваем версию
set /p VERSION=Новая версия (например 1.0.2):
if "%VERSION%"=="" ( echo ОШИБКА: версия не указана! & pause & exit /b 1 )

:: Запрашиваем описание
set /p NOTES=Описание изменений (Enter — пропустить):
if "%NOTES%"=="" set NOTES=Обновление %VERSION%

echo.
echo IP сервера: %SERVER_IP%
echo Папка:      %UPDATES_DIR%
echo Версия:     %VERSION%
echo Описание:   %NOTES%
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

:: Создаём папку updates на сервере если нет
echo.
echo [3/3] Публикация на %UPDATES_DIR%...
if not exist "%UPDATES_DIR%" (
    echo Создаём папку %UPDATES_DIR%...
    mkdir "%UPDATES_DIR%" 2>nul
    if %errorlevel% neq 0 (
        echo ОШИБКА: нет доступа к серверу %SERVER_IP%
        echo Проверьте что папка "updates" расшарена на сервере.
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
