@echo off
echo Сборка BibClient (self-contained, win-x64)...
dotnet publish BibClient\BibClient.csproj -p:PublishProfile=win-x64
if %errorlevel% neq 0 (
    echo ОШИБКА сборки!
    pause
    exit /b 1
)
echo.
echo Готово! Файлы в: BibClient\bin\Publish\win-x64\
pause
