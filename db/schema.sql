-- ChatApp (사내 메신저) MariaDB 스키마 — 새로 설치할 때 이 파일 하나만 실행 (스키마 버전 5)
-- 실행: "C:\Program Files\MariaDB 13.0\bin\mysql.exe" -u root -p < "C:\Users\LMS\Downloads\ChatApp\ChatApp\db\schema.sql"
--       (HeidiSQL: 파일 → SQL 파일 실행)
-- 주의: 아래 '여기에_DB_비밀번호'를 원하는 비밀번호로 바꾸고, appsettings.json에도 같은 값을 넣으세요.
-- 이미 예전 버전으로 만든 DB라면 이 파일 대신 migrate_00X 스크립트를 번호 순서대로 실행하세요(맨 아래 변경 이력).
SET NAMES utf8mb4;

CREATE DATABASE IF NOT EXISTS chatapp
  DEFAULT CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci;  -- 한글·이모지 저장
USE chatapp;

-- 서버 프로그램 전용 계정 (root 사용 금지)
CREATE USER IF NOT EXISTS 'chatapp'@'localhost' IDENTIFIED BY '여기에_DB_비밀번호';
GRANT SELECT, INSERT, UPDATE, DELETE ON chatapp.* TO 'chatapp'@'localhost';
FLUSH PRIVILEGES;

CREATE TABLE IF NOT EXISTS users (
  employee_no         VARCHAR(20)  PRIMARY KEY,           -- 사원번호 = 로그인 아이디
  email               VARCHAR(100) NOT NULL UNIQUE,       -- 사내 도메인만 허용
  phone               VARCHAR(11)  NULL UNIQUE,           -- 휴대폰 번호(숫자만, 선택). 친구 찾기에만 쓰고 공개하지 않는다
  name                VARCHAR(30)  NOT NULL,
  department          VARCHAR(30)  NOT NULL,
  position            VARCHAR(20)  NOT NULL,              -- 직급
  role                ENUM('USER','ADMIN') NOT NULL DEFAULT 'USER',
  status              ENUM('PENDING','ACTIVE','SUSPENDED','RESIGNED') NOT NULL DEFAULT 'PENDING',
  auth_source         ENUM('LOCAL','LDAP') NOT NULL DEFAULT 'LOCAL',
  password_hash       VARCHAR(100) NULL,                  -- LDAP 계정은 NULL
  salt                VARCHAR(50)  NULL,
  failed_login_count  TINYINT UNSIGNED NOT NULL DEFAULT 0,
  locked_until        DATETIME     NULL,                  -- 5회 실패 시 잠금 해제 시각
  created_at          DATETIME     NOT NULL DEFAULT CURRENT_TIMESTAMP,
  last_login_at       DATETIME     NULL,
  status_message      VARCHAR(60)  NULL,                  -- 상태 메시지 (예: 오후 반차)
  must_change_password TINYINT(1)  NOT NULL DEFAULT 0     -- 1 = 관리자가 초기화한 임시 비밀번호 (다음 로그인 때 변경 필수)
) ENGINE=InnoDB;

-- 채팅방: 서버를 다시 켜도 남는다. 마지막 멤버가 나가면 closed_at이 채워지고 목록에서 사라진다(대화 기록은 남음).
CREATE TABLE IF NOT EXISTS rooms (
  room_id     INT AUTO_INCREMENT PRIMARY KEY,
  room_name   VARCHAR(50)      NOT NULL,
  owner_no    VARCHAR(20)      NULL,
  max_users   SMALLINT UNSIGNED NOT NULL DEFAULT 1000,     -- 최대 1000명 (TINYINT는 255까지라 부족). 기본도 1000명
  is_private  TINYINT(1)       NOT NULL DEFAULT 0,          -- 1 = 비공개(초대로만 참여, 목록에 안 보임)
  closed_at   DATETIME         NULL,
  created_at  DATETIME         NOT NULL DEFAULT CURRENT_TIMESTAMP,
  FOREIGN KEY (owner_no) REFERENCES users(employee_no) ON DELETE SET NULL
) ENGINE=InnoDB;

-- 방 멤버 (접속을 끊어도 멤버로 남는다). last_read_at = 마지막으로 읽은 시각 → 메시지 옆 "안 읽은 사람 수"
CREATE TABLE IF NOT EXISTS room_members (
  room_id      INT          NOT NULL,
  employee_no  VARCHAR(20)  NOT NULL,
  joined_at    DATETIME(3)  NOT NULL DEFAULT CURRENT_TIMESTAMP(3),
  last_read_at DATETIME(3)  NULL,
  PRIMARY KEY (room_id, employee_no),
  INDEX idx_member (employee_no),
  FOREIGN KEY (room_id)     REFERENCES rooms(room_id)     ON DELETE CASCADE,
  FOREIGN KEY (employee_no) REFERENCES users(employee_no) ON DELETE CASCADE
) ENGINE=InnoDB;

-- 방장 또는 관리자가 고정하는 방별 상단 공지
CREATE TABLE IF NOT EXISTS room_notices (
  room_id     INT          PRIMARY KEY,
  notice_text VARCHAR(200) NOT NULL,
  updated_by  VARCHAR(20)  NOT NULL,
  updated_at  DATETIME(3)  NOT NULL DEFAULT CURRENT_TIMESTAMP(3),
  FOREIGN KEY (room_id)    REFERENCES rooms(room_id) ON DELETE CASCADE,
  FOREIGN KEY (updated_by) REFERENCES users(employee_no)
) ENGINE=InnoDB;

-- 대화 (방 메시지 + 1:1). message_id는 서버가 보내는 순간 정해서 직접 넣는다(답장·삭제·공감에 쓰려고).
CREATE TABLE IF NOT EXISTS messages (
  message_id   BIGINT AUTO_INCREMENT PRIMARY KEY,
  room_id      INT          NULL,
  sender_no    VARCHAR(20)  NOT NULL,
  receiver_no  VARCHAR(20)  NULL,                       -- 1:1일 때만 값
  content      VARCHAR(500) NOT NULL,
  msg_type     ENUM('CHAT','WHISPER','SYSTEM') NOT NULL,
  sent_at      DATETIME(3)  NOT NULL DEFAULT CURRENT_TIMESTAMP(3),
  reply_to     BIGINT       NULL,                       -- 답장이면 원래 메시지 번호
  deleted_at   DATETIME     NULL,                       -- 삭제 표시 (내용은 감사용으로 남긴다)
  deleted_by   VARCHAR(20)  NULL,
  edited_at    DATETIME(3)  NULL,
  INDEX idx_room_time (room_id, sent_at),
  INDEX idx_sent_at (sent_at),
  INDEX idx_dm (sender_no, receiver_no, sent_at),       -- 1:1 지난 대화
  FOREIGN KEY (room_id)   REFERENCES rooms(room_id) ON DELETE SET NULL,
  FOREIGN KEY (sender_no) REFERENCES users(employee_no)
) ENGINE=InnoDB;

-- 1:1 대화 읽음 위치
CREATE TABLE IF NOT EXISTS direct_reads (
  reader_no    VARCHAR(20)  NOT NULL,
  peer_no      VARCHAR(20)  NOT NULL,
  last_read_at DATETIME(3)  NOT NULL,
  PRIMARY KEY (reader_no, peer_no),
  FOREIGN KEY (reader_no) REFERENCES users(employee_no) ON DELETE CASCADE
) ENGINE=InnoDB;

-- 공감(이모지 반응)
CREATE TABLE IF NOT EXISTS message_reactions (
  message_id  BIGINT       NOT NULL,
  employee_no VARCHAR(20)  NOT NULL,
  emoji       VARCHAR(16)  NOT NULL,
  created_at  DATETIME(3)  NOT NULL DEFAULT CURRENT_TIMESTAMP(3),
  PRIMARY KEY (message_id, employee_no, emoji),
  FOREIGN KEY (message_id) REFERENCES messages(message_id) ON DELETE CASCADE
) ENGINE=InnoDB;

-- 대화에 올린 파일·이미지·동영상 (실제 파일은 서버의 업로드 폴더에 file_id 이름으로 저장)
CREATE TABLE IF NOT EXISTS files (
  file_id      CHAR(32)     PRIMARY KEY,
  room_id      INT          NULL,
  sender_no    VARCHAR(20)  NOT NULL,
  receiver_no  VARCHAR(20)  NULL,
  file_name    VARCHAR(255) NOT NULL,
  file_size    BIGINT       NOT NULL,
  file_kind    ENUM('FILE','IMAGE','VIDEO') NOT NULL,
  stored_path  VARCHAR(255) NOT NULL,
  status       ENUM('UPLOADING','DONE') NOT NULL DEFAULT 'UPLOADING',
  created_at   DATETIME(3)  NOT NULL DEFAULT CURRENT_TIMESTAMP(3),
  INDEX idx_room_time (room_id, created_at),
  INDEX idx_files_dm (sender_no, receiver_no, created_at),
  INDEX idx_files_status (status, created_at),
  FOREIGN KEY (room_id)   REFERENCES rooms(room_id) ON DELETE SET NULL,
  FOREIGN KEY (sender_no) REFERENCES users(employee_no)
) ENGINE=InnoDB;

CREATE TABLE IF NOT EXISTS audit_logs (
  log_id      BIGINT AUTO_INCREMENT PRIMARY KEY,
  actor_no    VARCHAR(20)  NULL,
  action      VARCHAR(30)  NOT NULL,                    -- LOGIN_OK, LOGIN_FAIL, APPROVE, SUSPEND, RESIGN, KICK, NOTICE
  target_no   VARCHAR(20)  NULL,
  detail      VARCHAR(255) NULL,
  client_ip   VARCHAR(45)  NULL,
  created_at  DATETIME(3)  NOT NULL DEFAULT CURRENT_TIMESTAMP(3),
  INDEX idx_created (created_at),
  INDEX idx_actor (actor_no, created_at),
  INDEX idx_target (target_no, created_at),
  INDEX idx_action (action, created_at)
) ENGINE=InnoDB;

-- 적용한 스키마 버전 (서버가 시작할 때 확인한다)
CREATE TABLE IF NOT EXISTS schema_version (
  version     INT          PRIMARY KEY,
  description VARCHAR(100) NOT NULL,
  applied_at  DATETIME     NOT NULL DEFAULT CURRENT_TIMESTAMP
) ENGINE=InnoDB;
INSERT IGNORE INTO schema_version (version, description) VALUES
  (2, 'files'), (3, 'status_message'), (4, 'admin'), (5, 'rooms_messages'), (6, 'room_notices'), (7, 'message_edit'), (8, 'login_tokens'), (9, 'utc_timestamps'), (10, 'device_login_tokens'), (11, 'friends_phone');

-- 친구: owner가 friend를 자기 목록에 추가 (한쪽 방향, 상대 승인 없음)
CREATE TABLE IF NOT EXISTS friends (
  owner_no    VARCHAR(20) NOT NULL,
  friend_no   VARCHAR(20) NOT NULL,
  created_at  DATETIME(3) NOT NULL,
  PRIMARY KEY (owner_no, friend_no),
  INDEX idx_friends_friend (friend_no),
  CONSTRAINT fk_friends_owner  FOREIGN KEY (owner_no)  REFERENCES users(employee_no) ON DELETE CASCADE,
  CONSTRAINT fk_friends_friend FOREIGN KEY (friend_no) REFERENCES users(employee_no) ON DELETE CASCADE
) ENGINE=InnoDB;

CREATE TABLE IF NOT EXISTS login_tokens (
  employee_no VARCHAR(20) NOT NULL,
  device_id CHAR(32) NOT NULL DEFAULT 'legacy',
  token_hash CHAR(64) NOT NULL,
  expires_at DATETIME NOT NULL,
  created_at DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
  PRIMARY KEY (employee_no, device_id),
  CONSTRAINT fk_login_tokens_user FOREIGN KEY (employee_no) REFERENCES users(employee_no) ON DELETE CASCADE
) ENGINE=InnoDB;

-- 최초 관리자는 가입 후 서버 콘솔에서 /grant 사원번호 로 지정하거나 아래 SQL로 지정
-- UPDATE users SET role = 'ADMIN', status = 'ACTIVE' WHERE employee_no = 'A0001';

-- [변경 이력] 방 최대 인원 50 → 1000명: 이미 만든 DB는 아래 한 줄만 실행하면 된다
-- ALTER TABLE rooms MODIFY max_users SMALLINT UNSIGNED NOT NULL DEFAULT 10;

-- [변경 이력] 파일 전송(files 테이블): 이미 만든 DB는 db/migrate_002_files.sql 실행

-- [변경 이력] 상태 메시지 users.status_message: 이미 만든 DB는 db/migrate_003_status.sql 실행

-- [변경 이력] 관리자 기능(must_change_password, audit_logs 인덱스): 이미 만든 DB는 db/migrate_004_admin.sql 실행

-- [변경 이력] 영구·비공개 방, 메시지 번호·답장·삭제·공감, 읽음 표시, 인덱스: 이미 만든 DB는 db/migrate_005_rooms_messages.sql 실행

-- [변경 이력] 방별 고정 공지: 이미 만든 DB는 db/migrate_006_room_notices.sql 실행

-- [변경 이력] 메시지 수정: 이미 만든 DB는 db/migrate_007_message_edit.sql 실행
