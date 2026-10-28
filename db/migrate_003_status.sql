-- [변경] 상태 메시지 (users.status_message)
-- 이미 만든 DB는 이 파일만 root로 한 번 실행하면 된다.
--   cmd:  "C:\Program Files\MariaDB 13.0\bin\mysql.exe" -u root -p chatapp < "C:\Users\LMS\Downloads\ChatApp\ChatApp\db\migrate_003_status.sql"
USE chatapp;

-- 상태 메시지 (예: "오후 반차", "내선 1234"). 다음 로그인에도 남도록 저장한다.
ALTER TABLE users ADD COLUMN IF NOT EXISTS status_message VARCHAR(60) NULL;

-- 광고 기능은 뺐다. 예전 migrate_003_ads_status.sql로 광고 테이블을 이미 만들었다면 지워도 된다(남아 있어도 무해).
DROP TABLE IF EXISTS ad_events;
DROP TABLE IF EXISTS ads;
