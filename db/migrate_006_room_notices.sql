-- 방별 고정 공지
-- 실행: mysql -u root -p chatapp < db/migrate_006_room_notices.sql
-- 여러 번 실행해도 안전하다(IF NOT EXISTS / INSERT IGNORE).
SET NAMES utf8mb4;
USE chatapp;

CREATE TABLE IF NOT EXISTS schema_version (
  version     INT          PRIMARY KEY,
  description VARCHAR(100) NOT NULL,
  applied_at  DATETIME     NOT NULL DEFAULT CURRENT_TIMESTAMP
) ENGINE=InnoDB;
INSERT IGNORE INTO schema_version (version, description) VALUES
  (2, 'files'), (3, 'status_message'), (4, 'admin'), (5, 'rooms_messages');

CREATE TABLE IF NOT EXISTS room_notices (
  room_id     INT          PRIMARY KEY,
  notice_text VARCHAR(200) NOT NULL,
  updated_by  VARCHAR(20)  NOT NULL,
  updated_at  DATETIME(3)  NOT NULL DEFAULT CURRENT_TIMESTAMP(3),
  FOREIGN KEY (room_id)    REFERENCES rooms(room_id) ON DELETE CASCADE,
  FOREIGN KEY (updated_by) REFERENCES users(employee_no)
) ENGINE=InnoDB;

INSERT IGNORE INTO schema_version (version, description) VALUES (6, 'room_notices');
