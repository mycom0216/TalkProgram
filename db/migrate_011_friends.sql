-- ─────────────────────────────────────────────────────────────────────────────
-- 011: 친구 목록 + 휴대폰 번호 (친구 찾기용)
-- 실행: "C:\Program Files\MariaDB 13.0\bin\mysql.exe" -u root -p chatapp < db\migrate_011_friends.sql
-- 여러 번 실행해도 안전하다.
-- ─────────────────────────────────────────────────────────────────────────────
SET NAMES utf8mb4;
USE chatapp;

-- 휴대폰 번호: 숫자만 저장(01012345678). 한 번호는 한 계정에만 (UNIQUE). 등록은 선택이라 NULL 허용.
SET @has_phone = (SELECT COUNT(*) FROM information_schema.columns
  WHERE table_schema = DATABASE() AND table_name = 'users' AND column_name = 'phone');
SET @phone_ddl = IF(@has_phone = 0,
  'ALTER TABLE users ADD COLUMN phone VARCHAR(11) NULL AFTER email, ADD UNIQUE INDEX uq_users_phone (phone)',
  'SELECT ''phone column already exists'' AS migration_status');
PREPARE phone_stmt FROM @phone_ddl;
EXECUTE phone_stmt;
DEALLOCATE PREPARE phone_stmt;

-- 친구: owner가 friend를 자기 목록에 추가 (한쪽 방향)
CREATE TABLE IF NOT EXISTS friends (
  owner_no    VARCHAR(20) NOT NULL,
  friend_no   VARCHAR(20) NOT NULL,
  created_at  DATETIME(3) NOT NULL,
  PRIMARY KEY (owner_no, friend_no),
  INDEX idx_friends_friend (friend_no),
  CONSTRAINT fk_friends_owner  FOREIGN KEY (owner_no)  REFERENCES users(employee_no) ON DELETE CASCADE,
  CONSTRAINT fk_friends_friend FOREIGN KEY (friend_no) REFERENCES users(employee_no) ON DELETE CASCADE
) ENGINE=InnoDB;

INSERT IGNORE INTO schema_version (version, description) VALUES (11, 'friends_phone');
