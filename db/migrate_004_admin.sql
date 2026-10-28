-- 관리자 기능(6단계): 임시 비밀번호 강제 변경 + 감사 로그 검색용 인덱스
-- 실행: "C:\Program Files\MariaDB 13.0\bin\mysql.exe" -u root -p chatapp < "C:\Users\LMS\Downloads\ChatApp\ChatApp\db\migrate_004_admin.sql"
SET NAMES utf8mb4;
USE chatapp;

-- 1 = 관리자가 비밀번호를 초기화함 → 다음 로그인 때 새 비밀번호로 바꿔야 채팅을 쓸 수 있다
ALTER TABLE users ADD COLUMN IF NOT EXISTS must_change_password TINYINT(1) NOT NULL DEFAULT 0;

-- 감사 로그를 사람별·동작별로 찾을 때 쓰는 인덱스 (없으면 기록이 쌓일수록 검색이 느려진다)
CREATE INDEX IF NOT EXISTS idx_actor ON audit_logs (actor_no, created_at);
CREATE INDEX IF NOT EXISTS idx_target ON audit_logs (target_no, created_at);
CREATE INDEX IF NOT EXISTS idx_action ON audit_logs (action, created_at);

-- 첫 관리자 지정 (서버 콘솔에서 /grant 사원번호 로 해도 된다)
-- UPDATE users SET role = 'ADMIN' WHERE employee_no = '여기에_사원번호';
