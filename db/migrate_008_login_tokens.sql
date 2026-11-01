-- 이 스크립트는 로그인 상태를 기억하는 기기별 토큰을 저장할 표를 만듭니다.
-- 같은 스크립트를 다시 실행해도 이미 있는 표를 지우거나 덮어쓰지 않습니다.
SET NAMES utf8mb4;
USE chatapp;

-- 자동 로그인 토큰의 해시와 만료 시각을 저장합니다.
-- 원본 토큰 대신 해시만 저장해 DB 정보가 노출되어도 바로 사용할 수 없게 합니다.
CREATE TABLE IF NOT EXISTS login_tokens (
  employee_no VARCHAR(20) PRIMARY KEY,
  token_hash CHAR(64) NOT NULL,
  expires_at DATETIME NOT NULL,
  created_at DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
  CONSTRAINT fk_login_tokens_user FOREIGN KEY (employee_no) REFERENCES users(employee_no) ON DELETE CASCADE
) ENGINE=InnoDB;

-- 서버가 이 작업(8번)을 이미 끝냈다는 표시를 버전 표에 남깁니다.
INSERT IGNORE INTO schema_version (version, description) VALUES (8, 'login_tokens');
