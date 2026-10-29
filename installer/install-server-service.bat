@echo off
chcp 65001 >nul
rem ─────────────────────────────────────────────────────────────────────────────
rem 사내톡 서버를 Windows 서비스로 등록하고 시작한다.
rem   - 관리자 권한 필요 (오른쪽 클릭 → 관리자 권한으로 실행)
rem   - 이 파일은 publish\server-service 폴더(ChatApp.Service.exe 옆)에서 실행한다.
rem   - 하는 일: 서비스 등록(자동 시작·지연) → MariaDB 서비스가 있으면 의존 관계 설정
rem             → 실패 시 자동 재시작 설정 → 방화벽 포트 열기 → 시작
rem ─────────────────────────────────────────────────────────────────────────────
setlocal
set "SERVICE_NAME=ChatApp Server"
set "DISPLAY_NAME=사내톡 서버"
set "HERE=%~dp0"
set "SERVICE_EXE=%HERE%ChatApp.Service.exe"
set "SERVER_DIR=%HERE%ChatApp.Server"

rem 1) 관리자 권한 확인
net session >nul 2>&1
if errorlevel 1 (
  echo [오류] 관리자 권한이 필요합니다. 이 파일을 오른쪽 클릭 → "관리자 권한으로 실행" 하세요.
  goto :fail
)

rem 2) 파일 확인
if not exist "%SERVICE_EXE%" (
  echo [오류] ChatApp.Service.exe가 없습니다. 솔루션 폴더에서 publish.bat을 실행해 만든
  echo        publish\server-service 폴더 안에서 이 파일을 실행하세요.
  goto :fail
)
if not exist "%SERVER_DIR%\ChatApp.Server.exe" (
  echo [오류] ChatApp.Server\ChatApp.Server.exe가 없습니다. publish.bat을 다시 실행하세요.
  goto :fail
)
if not exist "%SERVER_DIR%\appsettings.json" (
  copy /y "%SERVER_DIR%\appsettings.example.json" "%SERVER_DIR%\appsettings.json" >nul
  echo [준비] ChatApp.Server\appsettings.json을 예시에서 만들었습니다.
  echo        DB 비밀번호^(ConnectionString^)와 이메일 도메인을 고친 뒤 이 파일을 다시 실행하세요.
  notepad "%SERVER_DIR%\appsettings.json"
  goto :fail
)

rem 3) 이미 있으면 먼저 지운다 (다시 설치·업데이트)
sc.exe query "%SERVICE_NAME%" >nul 2>&1
if not errorlevel 1 (
  echo [정리] 기존 서비스를 멈추고 지웁니다...
  sc.exe stop "%SERVICE_NAME%" >nul 2>&1
  timeout /t 3 /nobreak >nul
  sc.exe delete "%SERVICE_NAME%" >nul
  timeout /t 2 /nobreak >nul
)

rem 4) 등록: 자동(지연) 시작 = 부팅 직후 DB·네트워크가 먼저 뜬 다음 시작
sc.exe create "%SERVICE_NAME%" binPath= "\"%SERVICE_EXE%\"" start= delayed-auto DisplayName= "%DISPLAY_NAME%"
if errorlevel 1 goto :fail
sc.exe description "%SERVICE_NAME%" "사내톡 메신저 서버 (TCP+TLS). 서버가 죽으면 자동으로 다시 시작합니다." >nul

rem MariaDB(또는 MySQL)가 같은 PC의 서비스면 그 서비스가 먼저 시작되게 한다
set "DB_SERVICE="
sc.exe query MariaDB >nul 2>&1 && set "DB_SERVICE=MariaDB"
if not defined DB_SERVICE (
  sc.exe query MySQL >nul 2>&1 && set "DB_SERVICE=MySQL"
)
if defined DB_SERVICE (
  sc.exe config "%SERVICE_NAME%" depend= %DB_SERVICE% >nul
  echo [설정] %DB_SERVICE% 서비스가 먼저 시작되도록 했습니다.
)

rem 서비스(감독 프로그램) 자체가 죽어도 5초 / 30초 / 60초 뒤 다시 시작, 하루 지나면 실패 횟수 초기화
sc.exe failure "%SERVICE_NAME%" reset= 86400 actions= restart/5000/restart/30000/restart/60000 >nul

rem 5) 방화벽: appsettings.json의 Port (없으면 9000)
set "PORT=9000"
for /f %%p in ('powershell -NoProfile -Command "if ((Get-Content -Raw '%SERVER_DIR%\appsettings.json') -match '.Port.\s*:\s*(\d+)') { $Matches[1] }"') do set "PORT=%%p"
netsh advfirewall firewall delete rule name="사내톡 서버" >nul 2>&1
netsh advfirewall firewall add rule name="사내톡 서버" dir=in action=allow protocol=TCP localport=%PORT% >nul
echo [설정] 방화벽에서 TCP %PORT% 포트를 열었습니다.

rem 6) 시작
sc.exe start "%SERVICE_NAME%" >nul
if errorlevel 1 (
  echo [오류] 서비스를 시작하지 못했습니다. 이벤트 뷰어 → Windows 로그 → 응용 프로그램, 또는
  echo        ChatApp.Server\logs 폴더의 로그를 확인하세요.
  goto :fail
)
timeout /t 3 /nobreak >nul
sc.exe query "%SERVICE_NAME%" | findstr /i "STATE"
echo.
echo 완료: "%DISPLAY_NAME%" 서비스가 등록되었습니다. PC를 켜면 자동으로 시작됩니다.
echo   상태 보기 : sc query "%SERVICE_NAME%"   (또는 services.msc)
echo   로그      : %SERVER_DIR%\logs
echo   해제      : uninstall-server-service.bat
endlocal
pause
exit /b 0

:fail
endlocal
pause
exit /b 1
