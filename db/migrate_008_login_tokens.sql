SET NAMES utf8mb4;
USE chatapp;

CREATE TABLE IF NOT EXISTS login_tokens (
  employee_no VARCHAR(20) PRIMARY KEY,
  token_hash CHAR(64) NOT NULL,
  expires_at DATETIME NOT NULL,
  created_at DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
  CONSTRAINT fk_login_tokens_user FOREIGN KEY (employee_no) REFERENCES users(employee_no) ON DELETE CASCADE
) ENGINE=InnoDB;

INSERT IGNORE INTO schema_version (version, description) VALUES (8, 'login_tokens');
