@echo off
rem 이 파일은 UTF-8로 저장되어 있다. 한국어 Windows 콘솔(CP949)에서 한글이 깨지지 않도록 UTF-8로 전환한다.
chcp 65001 >nul
setlocal

rem 어디서 실행하든 이 파일이 있는 폴더(저장소 루트) 기준으로 동작한다.
cd /d "%~dp0"

rem x64(일반 Intel/AMD PC)용과 ARM64(Snapdragon 등 ARM 기종)용을 차례로 만든다.
rem 출력 위치는 src\DupClean.UI\Properties\PublishProfiles\win-*.pubxml 의 PublishDir 과 같아야 한다.
rem   dist\win-x64\DupClean.exe
rem   dist\win-arm64\DupClean.exe

echo DupClean 빌드 및 패키징 시작 (x64, ARM64)...
echo.

call :publish win-x64
if errorlevel 1 goto :failed

call :publish win-arm64
if errorlevel 1 goto :failed

echo.
echo [완료] 두 버전을 모두 만들었습니다.
echo        x64   : %~dp0dist\win-x64\DupClean.exe
echo        ARM64 : %~dp0dist\win-arm64\DupClean.exe
echo.
dir "%~dp0dist\win-x64\DupClean.exe" "%~dp0dist\win-arm64\DupClean.exe" | findstr /i "DupClean.exe"
goto :done

rem ── 한 버전 빌드: %1 = win-x64 / win-arm64 ─────────────────────────
:publish
set "RID=%~1"
set "OUT_EXE=%~dp0dist\%RID%\DupClean.exe"

rem 이전 결과물이 남아 있으면 이번 빌드가 실패해도 "있음"으로 보일 수 있으므로 먼저 지운다.
if exist "%OUT_EXE%" del /q "%OUT_EXE%"

echo ── %RID% 빌드 중...
dotnet publish src\DupClean.UI\DupClean.UI.csproj ^
  /p:PublishProfile=%RID% ^
  -c Release ^
  -v minimal
if errorlevel 1 (
  echo.
  echo [오류] %RID% 빌드 실패.
  exit /b 1
)

rem 실제 출력 폴더에 파일이 만들어졌는지 확인한다.
if not exist "%OUT_EXE%" (
  echo.
  echo [오류] %RID% 빌드는 끝났지만 아래 파일이 없습니다.
  echo        %OUT_EXE%
  echo        %RID%.pubxml 의 PublishDir 설정을 확인하세요.
  exit /b 1
)
echo    → %OUT_EXE%
echo.
exit /b 0

:failed
echo.
echo 빌드를 중단했습니다. 위의 오류 메시지를 확인하세요.
pause
exit /b 1

:done
echo.
pause
