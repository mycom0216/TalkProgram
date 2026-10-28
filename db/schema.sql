-- ChatApp (사내 메신저) MariaDB 스키마
-- 실행: MariaDB에 root로 접속한 뒤 이 파일 전체를 실행 (HeidiSQL: 파일 → SQL 파일 실행)
-- 주의: 아래 '여기에_DB_비밀번호'를 원하는 비밀번호로 바꾸고, appsettings.json에도 같은 값을 넣으세요.

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

CREATE TABLE IF NOT EXISTS rooms (
  room_id     INT AUTO_INCREMENT PRIMARY KEY,
  room_name   VARCHAR(50)      NOT NULL,
  owner_no    VARCHAR(20)      NULL,
  max_users   SMALLINT UNSIGNED NOT NULL DEFAULT 10,       -- 최대 1000명 (TINYINT는 255까지라 부족)
  created_at  DATETIME         NOT NULL DEFAULT CURRENT_TIMESTAMP,
  FOREIGN KEY (owner_no) REFERENCES users(employee_no) ON DELETE SET NULL
) ENGINE=InnoDB;

CREATE TABLE IF NOT EXISTS messages (
  message_id   BIGINT AUTO_INCREMENT PRIMARY KEY,
  room_id      INT          NULL,
  sender_no    VARCHAR(20)  NOT NULL,
  receiver_no  VARCHAR(20)  NULL,                       -- 귓속말일 때만 값
  content      VARCHAR(500) NOT NULL,
  msg_type     ENUM('CHAT','WHISPER','SYSTEM') NOT NULL,
  sent_at      DATETIME(3)  NOT NULL DEFAULT CURRENT_TIMESTAMP(3),
  INDEX idx_room_time (room_id, sent_at),
  INDEX idx_sent_at (sent_at),
  FOREIGN KEY (room_id)   REFERENCES rooms(room_id) ON DELETE SET NULL,
  FOREIGN KEY (sender_no) REFERENCES users(employee_no)
) ENGINE=InnoDB;

CREATE TABLE IF NOT EXISTS file_logs (
  file_id      BIGINT AUTO_INCREMENT PRIMARY KEY,
  sender_no    VARCHAR(20)  NOT NULL,
  receiver_no  VARCHAR(20)  NOT NULL,
  file_name    VARCHAR(255) NOT NULL,
  file_size    BIGINT       NOT NULL,
  status       ENUM('SENT','REJECTED','FAILED') NOT NULL,
  sent_at      DATETIME     NOT NULL DEFAULT CURRENT_TIMESTAMP,
  FOREIGN KEY (sender_no) REFERENCES users(employee_no)
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

-- 최초 관리자는 가입 후 직접 지정 (6단계 관리자 기능에서 사용)
-- UPDATE users SET role = 'ADMIN', status = 'ACTIVE' WHERE employee_no = 'A0001';

-- [변경 이력] 방 최대 인원 50 → 1000명: 이미 만든 DB는 아래 한 줄만 실행하면 된다
-- ALTER TABLE rooms MODIFY max_users SMALLINT UNSIGNED NOT NULL DEFAULT 10;

-- [변경 이력] 파일 전송(files 테이블): 이미 만든 DB는 db/migrate_002_files.sql 실행

-- [변경 이력] 상태 메시지 users.status_message: 이미 만든 DB는 db/migrate_003_status.sql 실행
