@echo off
chcp 65001 >nul
rem ─────────────────────────────────────────────────────────────────────────────
rem 사내톡 배포 파일 만들기
rem   publish\client  : 직원 PC에 설치할 클라이언트 (.NET 설치 없이 실행되는 단일 exe)
rem   publish\server  : 서버 PC에 복사할 서버 프로그램
rem   publish\사내톡-설치-버전.exe : Inno Setup이 설치되어 있으면 설치 파일까지 만든다
rem 사용법: 이 파일이 있는 폴더(솔루션 폴더)에서 publish.bat 실행 (Developer PowerShell / 명령 프롬프트)
rem 버전을 올릴 때: ChatApp.Client\ChatApp.Client.csproj 의 <Version> 과
rem               서버 appsettings.json 의 ClientLatestVersion 을 같은 값으로 바꾼다.
rem ─────────────────────────────────────────────────────────────────────────────
setlocal
cd /d "%~dp0"

echo [1/3] 클라이언트 게시 (win-x64, 단일 파일, .NET 런타임 포함)
dotnet publish ChatApp.Client\ChatApp.Client.csproj -c Release -r win-x64 --self-contained true ^
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o publish\client
if errorlevel 1 goto :fail

echo [2/3] 서버 게시
dotnet publish ChatApp.Server\ChatApp.Server.csproj -c Release -o publish\server
if errorlevel 1 goto :fail
rem 서버 설정 예시와 DB 스크립트도 함께 넣는다 (appsettings.json은 비밀번호가 있어 복사하지 않음)
copy /y ChatApp.Server\appsettings.example.json publish\server\ >nul
if not exist publish\server\db mkdir publish\server\db
copy /y db\*.sql publish\server\db\ >nul
copy /y db\backup.bat publish\server\db\ >nul

echo [2b/3] Windows 서비스 배포 폴더 게시 (관리자 PowerShell에서 설치 스크립트 실행)
dotnet publish ChatApp.Service\ChatApp.Service.csproj -c Release -r win-x64 --self-contained true -o publish\server-service
if errorlevel 1 goto :fail
dotnet publish ChatApp.Server\ChatApp.Server.csproj -c Release -r win-x64 --self-contained true -o publish\server-service\ChatApp.Server
if errorlevel 1 goto :fail
copy /y ChatApp.Server\appsettings.example.json publish\server-service\ChatApp.Server\ >nul
if not exist publish\server-service\ChatApp.Server\db mkdir publish\server-service\ChatApp.Server\db
copy /y db\*.sql publish\server-service\ChatApp.Server\db\ >nul
copy /y db\backup.bat publish\server-service\ChatApp.Server\db\ >nul
copy /y installer\install-server-service.bat publish\server-service\ >nul
copy /y installer\uninstall-server-service.bat publish\server-service\ >nul

echo [3/3] 설치 파일 (Inno Setup)
set "ISCC=%ProgramFiles(x86)%\Inno Setup 6\ISCC.exe"
if not exist "%ISCC%" set "ISCC=%ProgramFiles%\Inno Setup 6\ISCC.exe"
if not exist "%ISCC%" (
  echo   Inno Setup 6이 없어 설치 파일은 건너뜁니다. https://jrsoftware.org/isinfo.php 에서 설치 후 다시 실행하세요.
  echo   publish\client 폴더의 exe만 직원 PC에 복사해도 바로 실행됩니다.
  goto :done
)
"%ISCC%" /Qp installer\ChatApp.iss
if errorlevel 1 goto :fail

:done
echo.
echo 완료: publish 폴더를 확인하세요.
exit /b 0

:fail
echo.
echo 실패했습니다. 위 오류 메시지를 확인하세요. (실행 중인 사내톡이 있으면 트레이에서 종료 후 다시)
exit /b 1
