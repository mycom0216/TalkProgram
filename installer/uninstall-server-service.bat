@echo off
chcp 65001 >nul
rem 사내톡 서버 Windows 서비스를 멈추고 등록을 지운다. (관리자 권한 필요)
rem 프로그램 파일·업로드 파일·로그·DB는 지우지 않는다.
setlocal
set "SERVICE_NAME=ChatApp Server"

net session >nul 2>&1
if errorlevel 1 (
  rem 관리자 권한이 없으면 시스템 서비스를 바꾸지 않고 여기서 끝낸다.
  echo [오류] 관리자 권한이 필요합니다. 이 파일을 오른쪽 클릭 → "관리자 권한으로 실행" 하세요.
  goto :end
)

sc.exe query "%SERVICE_NAME%" >nul 2>&1
if errorlevel 1 (
  rem 서비스가 없어도 방화벽 규칙은 남아 있을 수 있으므로 아래 정리 단계로 간다.
  echo 등록된 "%SERVICE_NAME%" 서비스가 없습니다.
  goto :firewall
)

echo 서비스를 멈춥니다... 접속자 정리와 기록 저장에 최대 30초 걸립니다.
rem 서비스를 요청한 뒤, 최대 30초 동안 완전히 멈출 때까지 1초씩 확인한다.
sc.exe stop "%SERVICE_NAME%" >nul 2>&1
set /a WAITED=0
:wait
sc.exe query "%SERVICE_NAME%" | findstr /i "STOPPED" >nul && goto :delete
if %WAITED% geq 30 goto :delete
timeout /t 1 /nobreak >nul
set /a WAITED+=1
goto :wait

:delete
rem 서비스 등록 정보만 지운다. 프로그램 파일과 DB는 따로 보존한다.
sc.exe delete "%SERVICE_NAME%" >nul
echo 서비스 등록을 지웠습니다.

:firewall
rem 서비스가 사용하던 방화벽 예외도 함께 치운다.
netsh advfirewall firewall delete rule name="사내톡 서버" >nul 2>&1
echo 방화벽 규칙도 지웠습니다. 프로그램·업로드 파일·로그·DB는 그대로 남아 있습니다.

:end
endlocal
pause
