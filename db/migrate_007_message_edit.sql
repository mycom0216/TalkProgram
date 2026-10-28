-- 메시지 수정 기록 시각
SET NAMES utf8mb4;
USE chatapp;

CREATE TABLE IF NOT EXISTS schema_version (
  version     INT          PRIMARY KEY,
  description VARCHAR(100) NOT NULL,
  applied_at  DATETIME     NOT NULL DEFAULT CURRENT_TIMESTAMP
) ENGINE=InnoDB;
INSERT IGNORE INTO schema_version (version, description) VALUES
  (2, 'files'), (3, 'status_message'), (4, 'admin'), (5, 'rooms_messages'), (6, 'room_notices');

ALTER TABLE messages ADD COLUMN IF NOT EXISTS edited_at DATETIME(3) NULL;
INSERT IGNORE INTO schema_version (version, description) VALUES (7, 'message_edit');
