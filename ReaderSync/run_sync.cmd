@echo off
rem Запуск робота обновления читателей. Параметры передаются в reader_sync.py (например --retry, --full, --headed).
cd /d "%~dp0"
set "PLAYWRIGHT_BROWSERS_PATH=%~dp0browsers"
set "PYTHONIOENCODING=utf-8"
"%~dp0venv\Scripts\python.exe" "%~dp0reader_sync.py" %*
exit /b %ERRORLEVEL%
