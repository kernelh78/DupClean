@echo off
echo DupClean 빌드 및 패키징 시작...
echo.

dotnet publish src\DupClean.UI\DupClean.UI.csproj ^
  /p:PublishProfile=win-x64 ^
  -c Release ^
  -v minimal

if %ERRORLEVEL% NEQ 0 (
    echo.
    echo [오류] 빌드 실패.
    pause
    exit /b 1
)

echo.
echo [완료] dist\ 폴더에 DupClean.exe 생성됨.
echo.
dir ..\dist\DupClean.exe
pause
