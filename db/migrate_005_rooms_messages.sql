-- 영구 채팅방·비공개 방(초대), 메시지 ID·답장·삭제·공감, 읽음 표시, 검색·조회 성능 인덱스, 보관 기간 정리
-- 실행: "C:\Program Files\MariaDB 13.0\bin\mysql.exe" -u root -p chatapp < "C:\Users\LMS\Downloads\ChatApp\ChatApp\db\migrate_005_rooms_messages.sql"
-- 여러 번 실행해도 안전하다(IF NOT EXISTS).
SET NAMES utf8mb4;
USE chatapp;

-- ── 적용한 마이그레이션 기록 ──
-- [왜] 어떤 스크립트를 이미 실행했는지 DB 안에 남겨 두면, 서버가 시작할 때 "DB가 최신인지" 확인할 수 있다.
CREATE TABLE IF NOT EXISTS schema_version (
  version     INT          PRIMARY KEY,
  description VARCHAR(100) NOT NULL,
  applied_at  DATETIME     NOT NULL DEFAULT CURRENT_TIMESTAMP
) ENGINE=InnoDB;
INSERT IGNORE INTO schema_version (version, description) VALUES
  (2, 'files'), (3, 'status_message'), (4, 'admin');

-- ── 채팅방: 비공개 여부, 닫힘 ──
-- 방은 이제 서버를 재시작해도 남는다. 마지막 멤버가 나가면 closed_at이 채워지고 목록에서 사라진다(기록은 남음).
ALTER TABLE rooms ADD COLUMN IF NOT EXISTS is_private TINYINT(1) NOT NULL DEFAULT 0;
ALTER TABLE rooms ADD COLUMN IF NOT EXISTS closed_at  DATETIME NULL;

-- ── 방 멤버 (카카오톡 단체방처럼 접속을 끊어도 멤버로 남는다) ──
-- last_read_at = 이 사람이 이 방에서 마지막으로 읽은 메시지 시각 → 메시지 옆 "안 읽은 사람 수" 계산에 쓴다.
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

-- 예전 방(서버 메모리에만 있던 방)은 멤버 정보가 없으므로 닫힌 방으로 표시한다 (기본 방 1번 제외)
UPDATE rooms SET closed_at = NOW()
WHERE room_id <> 1 AND closed_at IS NULL
  AND room_id NOT IN (SELECT DISTINCT room_id FROM room_members);

-- ── 1:1 대화 읽음 위치 ──
CREATE TABLE IF NOT EXISTS direct_reads (
  reader_no    VARCHAR(20)  NOT NULL,
  peer_no      VARCHAR(20)  NOT NULL,
  last_read_at DATETIME(3)  NOT NULL,
  PRIMARY KEY (reader_no, peer_no),
  FOREIGN KEY (reader_no) REFERENCES users(employee_no) ON DELETE CASCADE
) ENGINE=InnoDB;

-- ── 메시지: 답장·삭제 ──
-- message_id는 이제 서버가 보내는 순간 정해서(클라이언트가 답장·삭제·공감에 쓰려고) 직접 넣는다.
ALTER TABLE messages ADD COLUMN IF NOT EXISTS reply_to   BIGINT     NULL;
ALTER TABLE messages ADD COLUMN IF NOT EXISTS deleted_at DATETIME   NULL;
ALTER TABLE messages ADD COLUMN IF NOT EXISTS deleted_by VARCHAR(20) NULL;

-- ── 공감(이모지 반응) ──
CREATE TABLE IF NOT EXISTS message_reactions (
  message_id  BIGINT       NOT NULL,
  employee_no VARCHAR(20)  NOT NULL,
  emoji       VARCHAR(16)  NOT NULL,
  created_at  DATETIME(3)  NOT NULL DEFAULT CURRENT_TIMESTAMP(3),
  PRIMARY KEY (message_id, employee_no, emoji),
  FOREIGN KEY (message_id) REFERENCES messages(message_id) ON DELETE CASCADE
) ENGINE=InnoDB;

-- ── 조회 성능 인덱스 ──
-- 1:1 지난 대화: (보낸 사람, 받는 사람, 시각) 순서로 찾는다. 없으면 회사 전체 귓속말을 다 훑는다.
CREATE INDEX IF NOT EXISTS idx_dm      ON messages (sender_no, receiver_no, sent_at);
CREATE INDEX IF NOT EXISTS idx_files_dm ON files    (sender_no, receiver_no, created_at);
-- 보관 기간 정리(오래된 것 삭제)와 멈춘 업로드 정리
CREATE INDEX IF NOT EXISTS idx_files_status ON files (status, created_at);

-- 쓰지 않는 옛 테이블 (파일 전송 기록은 files 테이블과 감사 로그가 대신한다)
DROP TABLE IF EXISTS file_logs;

INSERT IGNORE INTO schema_version (version, description) VALUES (5, 'rooms_messages');
