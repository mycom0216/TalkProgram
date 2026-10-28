@echo off
rem 한글이 깨지지 않게 UTF-8 코드 페이지로
chcp 65001 >nul
rem ─────────────────────────────────────────────────────────────────────────
rem 사내톡 DB 백업 (매일 한 번 실행 권장)
rem  - chatapp DB 전체를 db\backup 폴더에 "chatapp-날짜.sql"로 저장하고, 30일 지난 백업은 지운다.
rem  - 서버를 멈추지 않고 백업한다(--single-transaction: 백업하는 동안에도 채팅 저장이 된다).
rem  - 업로드 파일(서버 실행 폴더의 uploads)은 이 스크립트가 아니라 폴더째 복사해 따로 보관하세요.
rem
rem 비밀번호 설정 (처음 한 번): 이 파일 옆에 backup.cnf를 만들고 아래 세 줄을 적는다. (Git에는 올라가지 않음)
rem     [client]
rem     user=root
rem     password=여기에_root_비밀번호
rem   backup.cnf가 없으면 실행할 때마다 비밀번호를 묻는다.
rem
rem 매일 자동 실행: 작업 스케줄러 → 기본 작업 만들기 → 매일 새벽 3시 → 이 파일 실행
rem 복구:          "C:\Program Files\MariaDB 13.0\bin\mysql.exe" -u root -p chatapp < db\backup\chatapp-YYYYMMDD.sql
rem ─────────────────────────────────────────────────────────────────────────
setlocal
set MARIADB=C:\Program Files\MariaDB 13.0\bin
set BACKUP_DIR=%~dp0backup
if not exist "%BACKUP_DIR%" mkdir "%BACKUP_DIR%"

for /f %%d in ('powershell -NoProfile -Command "Get-Date -Format yyyyMMdd"') do set TODAY=%%d
set FILE=%BACKUP_DIR%\chatapp-%TODAY%.sql

if exist "%~dp0backup.cnf" (
    set AUTH=--defaults-extra-file="%~dp0backup.cnf"
) else (
    set AUTH=-u root -p
)

echo 백업 중: %FILE%
"%MARIADB%\mariadb-dump.exe" %AUTH% --single-transaction --routines --default-character-set=utf8mb4 chatapp > "%FILE%"
if errorlevel 1 (
    echo 백업 실패. MariaDB 경로와 비밀번호를 확인하세요.
    exit /b 1
)

rem 30일 지난 백업 삭제
forfiles /p "%BACKUP_DIR%" /m chatapp-*.sql /d -30 /c "cmd /c del @path" 2>nul
echo 완료.
endlocal
