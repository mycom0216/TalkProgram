SET NAMES utf8mb4;
USE chatapp;

-- 기존 코드가 저장한 로컬 DATETIME 값을 UTC로 바꾼다.
-- 먼저 preview_009_utc_timestamps.sql 결과를 확인하고 전체 DB 백업을 만든다.
-- 아래 두 값을 실제 기존 저장 시간대에 맞게 직접 설정해야 변환이 진행된다.
-- 예: 한국 표준시라면 offset=9, confirmation='BACKUP_DONE_AND_SOURCE_TIMEZONE_CONFIRMED'
SET @legacy_offset_hours = NULL;
SET @utc_migration_confirmation = NULL;
SET @apply_utc_migration = (
  (SELECT COUNT(*) = 0 FROM schema_version WHERE version = 9)
  AND @legacy_offset_hours BETWEEN -14 AND 14
  AND @utc_migration_confirmation = 'BACKUP_DONE_AND_SOURCE_TIMEZONE_CONFIRMED'
);
SELECT IF(@apply_utc_migration,
  CONCAT('Migration 009 will shift legacy timestamps by ', @legacy_offset_hours, ' hours.'),
  'Migration 009 NOT applied. Set the offset and explicit backup/timezone confirmation after reviewing the preview.') AS migration_status;
START TRANSACTION;

UPDATE users SET created_at = DATE_SUB(created_at, INTERVAL @legacy_offset_hours HOUR) WHERE @apply_utc_migration AND created_at IS NOT NULL;
UPDATE users SET last_login_at = DATE_SUB(last_login_at, INTERVAL @legacy_offset_hours HOUR) WHERE @apply_utc_migration AND last_login_at IS NOT NULL;
UPDATE users SET locked_until = DATE_SUB(locked_until, INTERVAL @legacy_offset_hours HOUR) WHERE @apply_utc_migration AND locked_until IS NOT NULL;
UPDATE rooms SET created_at = DATE_SUB(created_at, INTERVAL @legacy_offset_hours HOUR) WHERE @apply_utc_migration;
UPDATE rooms SET closed_at = DATE_SUB(closed_at, INTERVAL @legacy_offset_hours HOUR) WHERE @apply_utc_migration AND closed_at IS NOT NULL;
UPDATE room_members SET joined_at = DATE_SUB(joined_at, INTERVAL @legacy_offset_hours HOUR) WHERE @apply_utc_migration;
UPDATE room_members SET last_read_at = DATE_SUB(last_read_at, INTERVAL @legacy_offset_hours HOUR) WHERE @apply_utc_migration AND last_read_at IS NOT NULL;
UPDATE room_notices SET updated_at = DATE_SUB(updated_at, INTERVAL @legacy_offset_hours HOUR) WHERE @apply_utc_migration;
UPDATE messages SET sent_at = DATE_SUB(sent_at, INTERVAL @legacy_offset_hours HOUR) WHERE @apply_utc_migration;
UPDATE messages SET deleted_at = DATE_SUB(deleted_at, INTERVAL @legacy_offset_hours HOUR) WHERE @apply_utc_migration AND deleted_at IS NOT NULL;
UPDATE messages SET edited_at = DATE_SUB(edited_at, INTERVAL @legacy_offset_hours HOUR) WHERE @apply_utc_migration AND edited_at IS NOT NULL;
UPDATE direct_reads SET last_read_at = DATE_SUB(last_read_at, INTERVAL @legacy_offset_hours HOUR) WHERE @apply_utc_migration;
UPDATE message_reactions SET created_at = DATE_SUB(created_at, INTERVAL @legacy_offset_hours HOUR) WHERE @apply_utc_migration;
UPDATE files SET created_at = DATE_SUB(created_at, INTERVAL @legacy_offset_hours HOUR) WHERE @apply_utc_migration;
UPDATE audit_logs SET created_at = DATE_SUB(created_at, INTERVAL @legacy_offset_hours HOUR) WHERE @apply_utc_migration;
-- expires_at은 008부터 C#에서 UTC로 전달했으므로 그대로 두고, DB 기본값으로 찍은 created_at만 변환한다.
UPDATE login_tokens SET created_at = DATE_SUB(created_at, INTERVAL @legacy_offset_hours HOUR) WHERE @apply_utc_migration;

INSERT IGNORE INTO schema_version (version, description)
SELECT 9, 'utc_timestamps' WHERE @apply_utc_migration;
COMMIT;
