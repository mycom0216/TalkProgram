-- ─────────────────────────────────────────────────────────────────────────────
-- [선택] 이미 만든 방들의 정원을 최대(1000명)로 올린다.
-- 새로 만드는 방은 프로그램이 기본 1000명으로 만들므로, 예전에 10명 등으로 만든 방에만 필요하다.
-- 스키마 버전은 바뀌지 않는다 (실행하지 않아도 서버는 정상 동작).
-- 실행: 명령 프롬프트(CMD)에서  mariadb -u root -p chatapp < db\update_room_capacity_1000.sql
-- 실행 후 서버를 다시 시작해야 화면에 반영된다 (서버가 시작할 때 방 정보를 읽어 둔다).
-- ─────────────────────────────────────────────────────────────────────────────
ALTER TABLE rooms MODIFY max_users SMALLINT UNSIGNED NOT NULL DEFAULT 1000;
UPDATE rooms SET max_users = 1000 WHERE room_id <> 1 AND max_users BETWEEN 1 AND 999;
SELECT room_id, room_name, max_users FROM rooms ORDER BY room_id;
