SET NAMES utf8mb4;
USE chatapp;

-- Read-only preview. Compare these DATETIME values with known local events;
-- server timezone settings alone cannot prove how historical DATETIME values were written.
SELECT @@global.time_zone AS db_global_timezone,
       @@session.time_zone AS db_session_timezone,
       NOW() AS db_now,
       UTC_TIMESTAMP() AS db_utc_now,
       (SELECT COUNT(*) FROM schema_version WHERE version = 9) AS migration_009_already_applied;

SELECT 'users.created_at' AS field_name, COUNT(*) AS row_count, MIN(created_at) AS oldest, MAX(created_at) AS newest FROM users
UNION ALL SELECT 'users.last_login_at', COUNT(last_login_at), MIN(last_login_at), MAX(last_login_at) FROM users
UNION ALL SELECT 'rooms.created_at', COUNT(*), MIN(created_at), MAX(created_at) FROM rooms
UNION ALL SELECT 'messages.sent_at', COUNT(*), MIN(sent_at), MAX(sent_at) FROM messages
UNION ALL SELECT 'files.created_at', COUNT(*), MIN(created_at), MAX(created_at) FROM files
UNION ALL SELECT 'audit_logs.created_at', COUNT(*), MIN(created_at), MAX(created_at) FROM audit_logs
ORDER BY field_name;

-- Sample rows make it easier to compare stored values against known local timestamps.
SELECT message_id, room_id, sender_no, sent_at, LEFT(content, 100) AS content_preview
FROM messages ORDER BY sent_at DESC LIMIT 10;
