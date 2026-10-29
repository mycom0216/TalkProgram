-- ─────────────────────────────────────────────────────────────────────────────
-- 012: 프로필 사진 버전 (사진 파일은 uploads\profiles 폴더, DB에는 바꾼 시각만)
-- 실행: "C:\Program Files\MariaDB 13.0\bin\mysql.exe" -u root -p chatapp < db\migrate_012_profile_photo.sql
-- 여러 번 실행해도 안전하다. (011을 먼저 실행해야 한다)
-- ─────────────────────────────────────────────────────────────────────────────
SET NAMES utf8mb4;
USE chatapp;

SET @has_photo = (SELECT COUNT(*) FROM information_schema.columns
  WHERE table_schema = DATABASE() AND table_name = 'users' AND column_name = 'photo_version');
SET @photo_ddl = IF(@has_photo = 0,
  'ALTER TABLE users ADD COLUMN photo_version BIGINT NOT NULL DEFAULT 0',
  'SELECT ''photo_version column already exists'' AS migration_status');
PREPARE photo_stmt FROM @photo_ddl;
EXECUTE photo_stmt;
DEALLOCATE PREPARE photo_stmt;

INSERT IGNORE INTO schema_version (version, description)
SELECT 12, 'profile_photo' FROM DUAL WHERE EXISTS (SELECT 1 FROM schema_version WHERE version = 11);
