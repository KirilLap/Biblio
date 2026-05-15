@echo off
setlocal
set ISCC="C:\Program Files (x86)\Inno Setup 6\ISCC.exe"

:: Читаем версию из файла VERSION
set /p VERSION=<"%~dp0VERSION"
if "%VERSION%"=="" ( echo ОШИБКА: файл VERSION пустой! & pause & exit /b 1 )

echo ============================================
echo   Biblio %VERSION% - сборка всех компонентов
echo ============================================
echo.

echo [1/4] Сборка BibClient (включает Service и Guardian)...
dotnet publish BibClient\BibClient.csproj -p:PublishProfile=win-x64
if %errorlevel% neq 0 ( echo ОШИБКА сборки BibClient! & pause & exit /b 1 )

echo [2/4] Сборка BibAdmin...
dotnet publish BibAdmin\BibAdmin.csproj -p:PublishProfile=win-x64
if %errorlevel% neq 0 ( echo ОШИБКА сборки BibAdmin! & pause & exit /b 1 )

echo [3/4] Сборка BibAdminWeb...
dotnet publish BibAdminWeb\BibAdminWeb.csproj -p:PublishProfile=win-x64
if %errorlevel% neq 0 ( echo ОШИБКА сборки BibAdminWeb! & pause & exit /b 1 )

echo.
echo [4/4] Сборка установщика (Inno Setup)...
if not exist %ISCC% (
    echo ВНИМАНИЕ: Inno Setup не найден по пути %ISCC%
    echo Скачайте с https://jrsoftware.org/isdl.php и установите.
    echo Файлы publish готовы в папках bin\Publish\win-x64\
    pause
    exit /b 0
)

%ISCC% /DAppVersion=%VERSION% installer\biblio-setup.iss
if %errorlevel% neq 0 ( echo ОШИБКА сборки установщика! & pause & exit /b 1 )

echo.
echo ============================================
echo   Готово! Установщик: installer\Output\biblio-setup-%VERSION%.exe
echo ============================================
pause
