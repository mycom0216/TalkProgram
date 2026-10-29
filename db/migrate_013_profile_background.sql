-- ─────────────────────────────────────────────────────────────────────────────
-- 013: 프로필 배경 사진 버전 (사진 파일은 uploads\profiles\사원번호.bg, DB에는 바꾼 시각만)
-- 실행: "C:\Program Files\MariaDB 13.0\bin\mysql.exe" -u root -p chatapp < db\migrate_013_profile_background.sql
-- 여러 번 실행해도 안전하다. (012를 먼저 실행해야 한다)
-- ─────────────────────────────────────────────────────────────────────────────
SET NAMES utf8mb4;
USE chatapp;

SET @has_bg = (SELECT COUNT(*) FROM information_schema.columns
  WHERE table_schema = DATABASE() AND table_name = 'users' AND column_name = 'background_version');
SET @bg_ddl = IF(@has_bg = 0,
  'ALTER TABLE users ADD COLUMN background_version BIGINT NOT NULL DEFAULT 0',
  'SELECT ''background_version column already exists'' AS migration_status');
PREPARE bg_stmt FROM @bg_ddl;
EXECUTE bg_stmt;
DEALLOCATE PREPARE bg_stmt;

INSERT IGNORE INTO schema_version (version, description)
SELECT 13, 'profile_background' FROM DUAL WHERE EXISTS (SELECT 1 FROM schema_version WHERE version = 12);
