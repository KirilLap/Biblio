@echo off
setlocal

:: ============================================================
:: deploy-update.cmd — публикация обновления на сервер по сети
::
:: Использование:
::   deploy-update.cmd                  — спросит версию и заметки
::   deploy-update.cmd 1.0.2 "Описание" — без вопросов
:: ============================================================

:: ── НАСТРОЙКИ ────────────────────────────────────────────────
:: Сетевой путь к папке установки BibAdminWeb на сервере.
:: Пример: \\192.168.1.10\c$\Program Files\Biblio\BibAdminWeb
set SERVER_PATH=\\СЕРВЕР\c$\Program Files\Biblio\BibAdminWeb
:: ─────────────────────────────────────────────────────────────

set INSTALLER_DIR=%~dp0installer\Output

:: Читаем версию: аргумент → или спрашиваем
set VERSION=%~1
if "%VERSION%"=="" (
    set /p VERSION=Введите версию (например 1.0.2):
)
if "%VERSION%"=="" ( echo ОШИБКА: версия не указана! & pause & exit /b 1 )

:: Читаем заметки: аргумент → или спрашиваем
set NOTES=%~2
if "%NOTES%"=="" (
    set /p NOTES=Описание изменений (Enter — пропустить):
)
if "%NOTES%"=="" set NOTES=Обновление %VERSION%

set INSTALLER=%INSTALLER_DIR%\biblio-setup-%VERSION%.exe

echo.
echo ============================================
echo   Публикация обновления %VERSION%
echo   Сервер: %SERVER_PATH%
echo   Заметки: %NOTES%
echo ============================================
echo.

:: Проверяем наличие установщика
if not exist "%INSTALLER%" (
    echo ОШИБКА: файл не найден: %INSTALLER%
    echo Сначала запустите build.cmd для сборки версии %VERSION%
    pause & exit /b 1
)

:: Проверяем доступность сервера
if not exist "%SERVER_PATH%" (
    echo ОШИБКА: сетевой путь недоступен: %SERVER_PATH%
    echo Проверьте подключение к серверу и правильность пути в скрипте.
    pause & exit /b 1
)

:: Создаём папку updates если её нет
set UPDATES_DIR=%SERVER_PATH%\updates
if not exist "%UPDATES_DIR%" (
    echo Создаём папку updates на сервере...
    mkdir "%UPDATES_DIR%"
    if %errorlevel% neq 0 ( echo ОШИБКА создания папки! & pause & exit /b 1 )
)

:: Копируем установщик
echo Копируем установщик...
copy /y "%INSTALLER%" "%UPDATES_DIR%\biblio-setup.exe" >nul
if %errorlevel% neq 0 ( echo ОШИБКА копирования установщика! & pause & exit /b 1 )

:: Создаём version.json
echo Создаём version.json...
echo {"Version":"%VERSION%","ReleaseNotes":"%NOTES%","InstallerFile":"biblio-setup.exe"} > "%UPDATES_DIR%\version.json"
if %errorlevel% neq 0 ( echo ОШИБКА создания version.json! & pause & exit /b 1 )

:: Также обновляем VERSION файл в репозитории
echo %VERSION%> "%~dp0VERSION"

echo.
echo ============================================
echo   Готово!
echo   Версия %VERSION% опубликована на сервере.
echo   Откройте веб-интерфейс BibAdminWeb -^>
echo   Настройки -^> "Проверить обновления"
echo ============================================
pause
