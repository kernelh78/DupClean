@echo off
rem 이 파일은 UTF-8로 저장되어 있다. 한국어 Windows 콘솔(CP949)에서 한글이 깨지지 않도록 UTF-8로 전환한다.
chcp 65001 >nul
setlocal

rem 어디서 실행하든 이 파일이 있는 폴더(저장소 루트) 기준으로 동작한다.
cd /d "%~dp0"

rem 출력 위치는 src\DupClean.UI\Properties\PublishProfiles\win-x64.pubxml 의 PublishDir 과 같아야 한다.
set "OUT_DIR=%~dp0dist"
set "OUT_EXE=%OUT_DIR%\DupClean.exe"

echo DupClean 빌드 및 패키징 시작...
echo.

dotnet publish src\DupClean.UI\DupClean.UI.csproj ^
  /p:PublishProfile=win-x64 ^
  -c Release ^
  -v minimal

if %ERRORLEVEL% NEQ 0 goto :build_failed

rem 실제 출력 폴더에 파일이 만들어졌는지 확인한다.
if not exist "%OUT_EXE%" goto :missing_output

echo.
echo [완료] DupClean.exe 생성됨
echo        위치: %OUT_EXE%
echo.
dir "%OUT_EXE%"
goto :done

:build_failed
echo.
echo [오류] 빌드 실패.
pause
exit /b 1

:missing_output
echo.
echo [오류] 빌드는 끝났지만 아래 파일이 없습니다.
echo        %OUT_EXE%
echo        win-x64.pubxml 의 PublishDir 설정을 확인하세요.
pause
exit /b 1

:done
echo.
pause
