SET NAMES utf8mb4;
USE chatapp;

-- Preserve existing bearer tokens under a legacy device identity so older clients
-- continue to work until they are upgraded. New clients have a per-install ID.
-- Safe to retry if the DDL succeeded but the version row did not get written.
SET @utc_migration_done = (SELECT COUNT(*) > 0 FROM schema_version WHERE version = 9);
SET @has_device_id = (SELECT COUNT(*) > 0 FROM information_schema.columns
  WHERE table_schema = DATABASE() AND table_name = 'login_tokens' AND column_name = 'device_id');
SET @apply_device_token_ddl = @utc_migration_done AND NOT @has_device_id;
SET @device_token_ddl = IF(@apply_device_token_ddl,
  'ALTER TABLE login_tokens ADD COLUMN device_id CHAR(32) NOT NULL DEFAULT ''legacy'' AFTER employee_no, DROP PRIMARY KEY, ADD PRIMARY KEY (employee_no, device_id)',
  'SELECT ''Skipped device token DDL (009 required; schema may already be migrated)'' AS migration_status');
PREPARE device_token_stmt FROM @device_token_ddl;
EXECUTE device_token_stmt;
DEALLOCATE PREPARE device_token_stmt;

INSERT IGNORE INTO schema_version (version, description)
SELECT 10, 'device_login_tokens'
WHERE @utc_migration_done AND (SELECT COUNT(*) > 0 FROM information_schema.columns
  WHERE table_schema = DATABASE() AND table_name = 'login_tokens' AND column_name = 'device_id');
