-- [변경] 파일·이미지·동영상 전송 (files 테이블 추가)
-- 이미 schema.sql로 DB를 만든 경우 이 파일만 root로 한 번 실행하면 된다.
--   cmd:  "C:\Program Files\MariaDB 13.0\bin\mysql.exe" -u root -p chatapp < "C:\Users\LMS\Downloads\ChatApp\ChatApp\db\migrate_002_files.sql"
USE chatapp;

CREATE TABLE IF NOT EXISTS files (
  file_id      CHAR(32)     PRIMARY KEY,                -- GUID(16진수 32자). 서버 디스크의 파일 이름이기도 하다
  room_id      INT          NULL,                       -- 방에 보낸 파일
  sender_no    VARCHAR(20)  NOT NULL,
  receiver_no  VARCHAR(20)  NULL,                       -- 1:1로 보낸 파일
  file_name    VARCHAR(255) NOT NULL,                   -- 원래 이름(화면 표시·저장할 때 사용)
  file_size    BIGINT       NOT NULL,
  file_kind    ENUM('FILE','IMAGE','VIDEO') NOT NULL,
  stored_path  VARCHAR(255) NOT NULL,                   -- 업로드 폴더 기준 상대 경로 (예: 202610/파일ID)
  status       ENUM('UPLOADING','DONE') NOT NULL DEFAULT 'UPLOADING',
  created_at   DATETIME(3)  NOT NULL DEFAULT CURRENT_TIMESTAMP(3),
  INDEX idx_room_time (room_id, created_at),
  FOREIGN KEY (room_id)   REFERENCES rooms(room_id) ON DELETE SET NULL,
  FOREIGN KEY (sender_no) REFERENCES users(employee_no)
) ENGINE=InnoDB;
