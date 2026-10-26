@echo off
chcp 65001 >nul
rem Установка робота ReaderSync. Запускать ОТ ИМЕНИ АДМИНИСТРАТОРА на компьютере с сервером BibAdminWeb.
rem Создаёт отдельное окружение Python в этой папке (venv) — другие программы на Python не затрагивает.
cd /d "%~dp0"

rem «setup.cmd dev» — только окружение и браузер, без заданий Планировщика (для проверки на рабочем ПК)
set "DEVMODE="
if /i "%~1"=="dev" set "DEVMODE=1"

net session >nul 2>&1
if errorlevel 1 if not defined DEVMODE (
  echo [ОШИБКА] Запустите setup.cmd от имени администратора.
  pause
  exit /b 1
)

echo === 1/4  Отдельное окружение Python (venv) ===
if not exist "venv\Scripts\python.exe" (
  where py >nul 2>&1 && ( py -3 -m venv venv ) || ( python -m venv venv )
)
if not exist "venv\Scripts\python.exe" (
  echo [ОШИБКА] Не удалось создать venv. Проверьте, что Python установлен.
  pause
  exit /b 1
)

echo === 2/4  Playwright и браузер ===
"venv\Scripts\python.exe" -m pip install --upgrade pip
"venv\Scripts\python.exe" -m pip install -r requirements.txt
if errorlevel 1 ( echo [ОШИБКА] Не удалось установить Playwright. & pause & exit /b 1 )
set "PLAYWRIGHT_BROWSERS_PATH=%~dp0browsers"
"venv\Scripts\python.exe" -m playwright install chromium
if errorlevel 1 ( echo [ОШИБКА] Не удалось скачать браузер. & pause & exit /b 1 )

echo === 3/4  Файл настроек ===
if not exist "config.ini" (
  copy /y "config.example.ini" "config.ini" >nul
  echo Создан config.ini — впишите в него логин и пароль UZNEL и проверьте адрес сервера.
) else (
  echo config.ini уже есть — не трогаю.
)

if defined DEVMODE (
  rem На рабочем ПК включаем видимое окно браузера
  powershell -NoProfile -Command "(Get-Content -Encoding UTF8 'config.ini') -replace '^show_browser\s*=.*','show_browser = yes' | Set-Content -Encoding UTF8 'config.ini'"
  echo === 4/4  Режим проверки: задания Планировщика не создаются, окно браузера включено ===
  echo.
  echo Готово. Заполните config.ini и запустите:
  echo   run_sync.cmd --headed --dry-run --from 01-10-2026
  pause
  exit /b 0
)

echo === 4/4  Задания Планировщика: 19:00 ежедневно и повтор в 08:00 ===
schtasks /Create /F /TN "BibLibReaderSync" /TR "\"%~dp0run_sync.cmd\"" /SC DAILY /ST 19:00 /RU SYSTEM /RL HIGHEST
schtasks /Create /F /TN "BibLibReaderSyncRetry" /TR "\"%~dp0run_sync.cmd\" --retry" /SC DAILY /ST 08:00 /RU SYSTEM /RL HIGHEST

echo.
echo Готово. Дальше:
echo   1. Заполните config.ini (логин и пароль UZNEL).
echo   2. Проверочный запуск с окном браузера, без отправки на сервер:
echo        run_sync.cmd --headed --dry-run --from 01-10-2026
echo   3. Первая полная загрузка базы:
echo        run_sync.cmd --full
pause
