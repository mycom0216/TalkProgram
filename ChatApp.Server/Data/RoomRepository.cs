using System.Data.Common;
using ChatApp.Common.Models;
using ChatApp.Server.Core;

namespace ChatApp.Server.Data;

// ═══════════════════════════════════════════════════════════════════════════
// [파일 역할] rooms, room_members 테이블 읽기·쓰기.
// [핵심] 방과 멤버를 DB에 저장해서 서버를 다시 켜도 단체방이 그대로 남게 한다(migrate_005).
// [스레드] 여러 수신 스레드가 동시에 부른다. 메서드마다 연결을 따로 열므로 안전하다.
// ═══════════════════════════════════════════════════════════════════════════
//
// ───────────────────────────────────────────────────────────────────────────
// [공부 노트 · 면접 대비]
//
// ■ 한 줄 요약
//   채팅방과 멤버를 DB(rooms, room_members, room_notices)에 저장하고 읽는다. 서버가 켜질 때 한 번 읽어 메모리(RoomManager)에 올리고, 이후 변경은 메모리와 함께 DB에도 반영한다.
//
// ■ 표 3개의 관계
//   rooms(방 정보: 번호, 이름, 방장, 정원, 비공개 여부, 닫힌 시각) 1 ─ N room_members(방 번호 + 사원번호가 기본 키, 입장 시각, 마지막 읽은 시각)
//   rooms 1 ─ 0..1 room_notices(고정 공지). 방이 지워지면(CASCADE) 멤버·공지도 함께 지워지도록 외래 키가 걸려 있다.
//   "닫힌 방"은 행을 지우지 않고 closed_at에 시각만 넣는다(대화 기록·파일 기록이 방 번호로 계속 연결되어야 하므로).
//
// ■ 이 파일에서 나오는 개념과 검색어
//   - INSERT IGNORE / ON DUPLICATE KEY UPDATE(upsert) ...... "MariaDB INSERT IGNORE ON DUPLICATE KEY UPDATE"
//   - LAST_INSERT_ID(), AUTO_INCREMENT ..................... "LAST_INSERT_ID per connection"
//   - 트랜잭션(여러 문장을 한 덩어리로) ..................... "database transaction atomicity"
//   - 소프트 삭제(closed_at) vs 하드 삭제 .................. "soft delete vs hard delete"
//   - GREATEST로 "더 뒤로만" 갱신 ........................... "SQL GREATEST monotonic update"
//   - LEFT JOIN + COALESCE(없을 때 대체 값) ................. "LEFT JOIN COALESCE default value"
//
// ■ 면접 예상 질문과 답
//   Q1. 방을 지울 때 행을 DELETE하지 않고 closed_at만 채우는 이유는요?
//       A. 대화·파일 기록이 방 번호로 연결되어 있고, 감사·분쟁에 대비해 기록을 남겨야 하기 때문입니다. 행을 지우면 연결이 끊기거나(외래 키 설정에 따라 기록이 같이 사라지거나) 방 이름을 알 수 없게 됩니다. 소프트 삭제로 두면 "닫힌 방의 파일 정리" 같은 후속 작업도 closed_at으로 할 수 있습니다.
//   Q2. 새 방의 번호를 어떻게 알아내나요? 동시에 두 사람이 방을 만들어도 섞이지 않나요?
//       A. AUTO_INCREMENT로 DB가 번호를 주고, 같은 연결에서 SELECT LAST_INSERT_ID()로 읽습니다. LAST_INSERT_ID는 "이 연결"에서 마지막으로 만든 값이라 다른 사람이 동시에 만들어도 섞이지 않습니다. 그래서 INSERT와 SELECT를 반드시 같은 연결(conn)에서 합니다.
//   Q3. 멤버 추가에 INSERT IGNORE를 쓰는 이유는요?
//       A. (방, 사원번호)가 기본 키라 이미 있는 멤버를 또 넣으면 오류가 납니다. IGNORE는 그 경우 오류 없이 건너뛰므로, 입장 요청이 겹치거나 재시도되어도 안전합니다(멱등). 여러 명을 한 트랜잭션으로 넣어 "일부만 들어간" 상태를 막습니다.
//   Q4. 읽음 위치 갱신에 GREATEST를 쓰는 이유는요?
//       A. 읽음 신호가 순서가 바뀌어 도착해도 "더 앞의 시각"으로 되돌아가면 안 되기 때문입니다. GREATEST(기존 값, 새 값)은 항상 더 뒤의 시각만 남깁니다. 값이 NULL이면 COALESCE로 새 값을 기준으로 씁니다. 메모리 쪽(RoomManager.MarkRead)도 같은 규칙이라 둘이 일치합니다.
//   Q5. 공지는 왜 별도 표(room_notices)이고, 저장은 어떻게 하나요?
//       A. 공지는 대부분의 방에 없기 때문에 rooms에 칸을 두기보다 필요한 방만 행을 둡니다. 저장은 "없으면 INSERT, 있으면 UPDATE"를 한 문장으로 하는 upsert(ON DUPLICATE KEY UPDATE)이고, 공지를 지우는 것은 행 삭제입니다. 빈 문자열 = 삭제라는 약속을 서비스와 맞춰 둡니다.
//   Q6. 서버가 시작할 때 방을 읽는 코드에서 오류를 어떻게 다루나요?
//       A. 표나 칸이 없을 때(SQLSTATE 42S02, 42S22 = 마이그레이션 전)는 빈 목록을 돌려줘 기본 방만으로 서버가 켜집니다. 그 밖의 DB 오류는 ChatServer가 잡아 "기본 방만 사용"으로 계속 켭니다. 또 멤버가 한 명도 없는 방은 쓸 수 없으므로 제외합니다.
//   Q7. 한계는요? (솔직하게 답하기)
//       A. (1) Create와 AddMembers가 한 트랜잭션이 아니라서, 그 사이에 실패하면 멤버 없는 방 행이 남을 수 있다(서버 재시작 때는 멤버 없는 방을 제외해서 보이지는 않는다). (2) 정지·퇴사한 계정도 멤버로 남기므로 정원을 차지한다(의도된 선택이지만 정리 정책이 필요하다). (3) 열 번호(0~6)로 읽는 방식이라 SELECT 순서를 바꾸면 조용히 어긋난다. (4) 닫힌 방의 행은 계속 쌓인다.
//
// ■ 문제가 났을 때 어디를 볼까 (유지보수)
//   - 서버를 재시작하니 방이 사라졌다 .......... closed_at이 채워졌는지, room_members 행이 있는지(멤버 없는 방은 로딩에서 제외됨)
//   - "채팅방을 불러오지 못했습니다" ............ 로그의 DbException 메시지, 마이그레이션 005~006 적용 여부
//   - 방 이름 옆 방장 표시가 번호로 보인다 ...... users 조인 실패(탈퇴·삭제된 계정) → COALESCE가 사원번호로 대체한 것
//   - 읽음 숫자가 되돌아간다 ................... UpdateRead의 GREATEST, 메모리 MarkRead와 같은 규칙인지
// ───────────────────────────────────────────────────────────────────────────
public sealed class RoomRepository(Db db)
{
    /// <summary>기본 방("전체 채팅", 1번)이 없으면 만든다. 서버 시작 시 1번 호출.</summary>
    // [개념] INSERT IGNORE: 같은 PRIMARY KEY가 이미 있으면 오류 없이 아무 일도 하지 않는다.
    public void EnsureLobby()
    {
        using DbConnection conn = db.Open();
        using DbCommand cmd = conn.CreateCommand();
        // [읽는 법] 기본 방은 번호를 직접 지정해(RoomInfo.LobbyId = 1) 넣는다. 서버를 켤 때마다 호출해도 이미 있으면 IGNORE가 무시하므로 안전하다. 방장(owner_no)이 NULL이고 정원 0(= 제한 없음)인 것이 기본 방의 특징이다.
        cmd.CommandText = """
            INSERT IGNORE INTO rooms (room_id, room_name, owner_no, max_users)
            VALUES (@id, '전체 채팅', NULL, 0)
            """;
        cmd.AddParam("@id", RoomInfo.LobbyId);
        cmd.ExecuteNonQuery();
    }

    /// <summary>닫히지 않은 방과 멤버를 모두 읽는다 (서버 시작 시). room_members가 없으면(migrate_005 전) 빈 목록.</summary>
    public List<Room> LoadOpenRooms()
    {
        // [읽는 법] 두 번의 SELECT로 읽는다: ① 방 정보를 번호별 Room으로 만들어 이 Dictionary에 넣고, ② 멤버를 읽어 해당 방의 Members에 채운다. 번호로 방을 바로 찾기 위해 Dictionary를 쓴다. 하나의 연결(conn)에 명령을 두 번 만들어 순서대로 실행한다.
        var rooms = new Dictionary<int, Room>();
        using DbConnection conn = db.Open();
        try
        {
            using (DbCommand cmd = conn.CreateCommand())
            {
                // [읽는 법] 방장 이름을 users에서 가져와 "이름 직급"으로 만든다. LEFT JOIN이므로 방장 계정이 없어도 방 행은 남고, COALESCE가 그때 사원번호로 대신한다. 공지(room_notices)도 LEFT JOIN해서 없으면 빈 문자열로 처리한다. 기본 방(@lobby)과 닫힌 방은 제외한다.
                cmd.CommandText = """
                    SELECT r.room_id, r.room_name, r.owner_no, COALESCE(CONCAT(u.name, ' ', u.position), r.owner_no),
                           r.max_users, r.is_private, COALESCE(n.notice_text, '')
                    FROM rooms r LEFT JOIN users u ON u.employee_no = r.owner_no
                    LEFT JOIN room_notices n ON n.room_id = r.room_id
                    WHERE r.closed_at IS NULL AND r.room_id <> @lobby
                    """;
                cmd.AddParam("@lobby", RoomInfo.LobbyId);
                using DbDataReader r = cmd.ExecuteReader();
                while (r.Read())
                {
                    int id = r.GetInt32(0);
                    rooms[id] = new Room(id, r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2), r.IsDBNull(3) ? null : r.GetString(3),
                                         Convert.ToInt32(r.GetValue(4)), Convert.ToInt32(r.GetValue(5)) == 1)
                    // [읽는 법] new Room(...) { PinnedNotice = ... } 는 생성자로 필수 값을 넣고, 이어서 객체 초기화자로 나머지 속성을 채우는 문법이다. is_private은 DB에서 0/1 숫자이므로 == 1로 bool로 바꾼다.
                    { PinnedNotice = r.GetString(6) };
                }
            }

            using (DbCommand cmd = conn.CreateCommand())
            {
                // 정지·퇴사한 사람도 멤버로는 남긴다(다시 사용하게 되면 그대로). 접속은 어차피 못 한다.
                cmd.CommandText = """
                    SELECT m.room_id, m.employee_no, COALESCE(CONCAT(u.name, ' ', u.position), m.employee_no), m.joined_at, m.last_read_at
                    FROM room_members m
                    JOIN rooms r ON r.room_id = m.room_id AND r.closed_at IS NULL
                    LEFT JOIN users u ON u.employee_no = m.employee_no
                    """;
                using DbDataReader r = cmd.ExecuteReader();
                while (r.Read())
                {
                    if (!rooms.TryGetValue(r.GetInt32(0), out Room? room)) continue;
                    string no = r.GetString(1);
                    // [읽는 법] 입장 시각과 마지막 읽은 시각은 UTC 꼬리표를 붙여 메모리에 올린다. 마지막 읽은 시각은 아직 읽은 적이 없으면 NULL이라 null로 둔다. 닫힌 방의 멤버 행은 쿼리의 JOIN 조건(r.closed_at IS NULL)에서 이미 걸러진다.
                    room.Members[no] = new RoomMember(no, r.GetString(2), Db.AsUtc(r.GetDateTime(3)), r.IsDBNull(4) ? null : Db.AsUtc(r.GetDateTime(4)));
                }
            }
        }
        // [읽는 법] 예외 필터로 "표 없음(42S02) 또는 칸 없음(42S22)"일 때만 잡는다 — 마이그레이션 전의 옛 DB다. 그 외의 오류는 위로 올려 숨기지 않는다. 빈 목록 []를 돌려주므로 서버는 기본 방만으로 시작한다.
        catch (DbException ex) when (ex.SqlState is "42S02" or "42S22") // 테이블·칸 없음 = migrate_005 전
        {
            return [];
        }
        // 멤버가 하나도 없는 방은 쓸 수 없으므로 뺀다
        // [읽는 법] 멤버가 한 명도 없는 방(예: 방 만들기 도중 실패한 흔적)은 쓸 수 없으므로 목록에서 뺀다. LINQ의 Where로 걸러 새 리스트로 만든다.
        return rooms.Values.Where(r => r.Members.Count > 0).ToList();
    }

    /// <summary>새 방 저장. DB가 발급한 방 번호(AUTO_INCREMENT)를 돌려준다.</summary>
    public int Create(string name, string ownerNo, int maxUsers, bool isPrivate)
    {
        using DbConnection conn = db.Open();
        using (DbCommand insert = conn.CreateCommand())
        {
            insert.CommandText = "INSERT INTO rooms (room_name, owner_no, max_users, is_private) VALUES (@name, @owner, @max, @private)";
            insert.AddParam("@name", name);
            insert.AddParam("@owner", ownerNo);
            insert.AddParam("@max", maxUsers);
            insert.AddParam("@private", isPrivate ? 1 : 0);
            insert.ExecuteNonQuery();
        }
        // [개념] LAST_INSERT_ID(): "이 연결"에서 마지막으로 만든 AUTO_INCREMENT 값. 다른 사람이 동시에 방을 만들어도 섞이지 않는다.
        using DbCommand select = conn.CreateCommand();
        // [읽는 법] 위 INSERT와 반드시 같은 연결에서 실행해야 한다(다른 연결이면 그 연결의 값을 읽는다). 연결 하나에서 INSERT → SELECT를 이어 하기 때문에 동시에 방을 만드는 다른 사람과 번호가 섞이지 않는다.
        select.CommandText = "SELECT LAST_INSERT_ID()";
        return Convert.ToInt32(select.ExecuteScalar());
    }

    /// <summary>멤버 여러 명 추가 (이미 있으면 무시).</summary>
    public void AddMembers(int roomId, IEnumerable<RoomMember> members)
    {
        using DbConnection conn = db.Open();
        using DbTransaction tx = conn.BeginTransaction();
        foreach (RoomMember m in members)
        {
            using DbCommand cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            // [읽는 법] 멤버를 한 명씩 INSERT하되 모두 같은 트랜잭션(tx)에 넣어, 여러 명을 초대할 때 일부만 저장되는 일을 막는다. 입장 시각(@at)은 서비스가 정한 값(RoomService.NowMs)을 그대로 넣어 메모리와 DB의 시각이 같다.
            cmd.CommandText = "INSERT IGNORE INTO room_members (room_id, employee_no, joined_at) VALUES (@room, @no, @at)";
            cmd.AddParam("@room", roomId);
            cmd.AddParam("@no", m.EmployeeNo);
            cmd.AddParam("@at", m.JoinedAt);
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
    }

    public void RemoveMember(int roomId, string employeeNo)
    {
        using DbConnection conn = db.Open();
        using DbCommand cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM room_members WHERE room_id = @room AND employee_no = @no";
        cmd.AddParam("@room", roomId);
        cmd.AddParam("@no", employeeNo);
        cmd.ExecuteNonQuery();
    }

    /// <summary>마지막 멤버가 나간 방을 닫는다 (대화 기록은 남는다).</summary>
    public void Close(int roomId)
    {
        using DbConnection conn = db.Open();
        using DbCommand cmd = conn.CreateCommand();
        // [읽는 법] 마지막 멤버가 나가 방이 닫힐 때 호출된다(소프트 삭제). 행과 대화 기록은 남고, 닫힌 시각만 기록된다. 이 시각을 기준으로 RetentionService가 "닫힌 방의 파일"을 정리한다. NOW()는 이 연결의 시간대(UTC)를 따른다.
        cmd.CommandText = "UPDATE rooms SET closed_at = NOW() WHERE room_id = @room";
        cmd.AddParam("@room", roomId);
        cmd.ExecuteNonQuery();
    }

    /// <summary>방장 바꾸기.</summary>
    public void SetOwner(int roomId, string ownerNo)
    {
        using DbConnection conn = db.Open();
        using DbCommand cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE rooms SET owner_no = @owner WHERE room_id = @room";
        cmd.AddParam("@owner", ownerNo);
        cmd.AddParam("@room", roomId);
        cmd.ExecuteNonQuery();
    }

    public void SetMaxUsers(int roomId, int maxUsers)
    {
        using DbConnection conn = db.Open();
        using DbCommand cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE rooms SET max_users = @max WHERE room_id = @room";
        cmd.AddParam("@max", maxUsers);
        cmd.AddParam("@room", roomId);
        cmd.ExecuteNonQuery();
    }

    public void SetPinnedNotice(int roomId, string text, string actorNo)
    {
        using DbConnection conn = db.Open();
        using DbCommand cmd = conn.CreateCommand();
        if (text.Length == 0)
        {
            cmd.CommandText = "DELETE FROM room_notices WHERE room_id = @room";
            cmd.AddParam("@room", roomId);
        }
        else
        {
            // [읽는 법] upsert: 그 방의 공지 행이 없으면 INSERT, 이미 있으면(기본 키 충돌) ON DUPLICATE KEY UPDATE 아래의 값으로 갱신한다. 누가(updated_by) 언제 바꿨는지가 함께 남는다. 위 if 분기는 "빈 문자열이면 행 삭제"다.
            cmd.CommandText = """
                INSERT INTO room_notices (room_id, notice_text, updated_by, updated_at)
                VALUES (@room, @text, @actor, CURRENT_TIMESTAMP(3))
                ON DUPLICATE KEY UPDATE notice_text = VALUES(notice_text), updated_by = VALUES(updated_by), updated_at = CURRENT_TIMESTAMP(3)
                """;
            cmd.AddParam("@room", roomId);
            cmd.AddParam("@text", text);
            cmd.AddParam("@actor", actorNo);
        }
        cmd.ExecuteNonQuery();
    }

    /// <summary>방 삭제: 멤버를 모두 빼고 닫는다 (대화 기록은 남는다 → 감사·분쟁 대비).</summary>
    public void Delete(int roomId)
    {
        using DbConnection conn = db.Open();
        using DbTransaction tx = conn.BeginTransaction();
        using (DbCommand cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            // [읽는 법] 방 삭제는 두 문장을 한 트랜잭션으로 묶는다: ① 모든 멤버 행 삭제 ② 방에 closed_at 기록. 둘 중 하나만 반영되는 상태(멤버는 없는데 방은 열림, 또는 그 반대)를 막는다. 방 행과 대화 기록은 남는다.
            cmd.CommandText = "DELETE FROM room_members WHERE room_id = @room";
            cmd.AddParam("@room", roomId);
            cmd.ExecuteNonQuery();
        }
        using (DbCommand cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = "UPDATE rooms SET closed_at = NOW() WHERE room_id = @room";
            cmd.AddParam("@room", roomId);
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
    }

    /// <summary>방 읽음 위치 저장. 더 뒤로 갈 때만 바꾼다 (GREATEST).</summary>
    public void UpdateRead(int roomId, string employeeNo, DateTime upTo)
    {
        using DbConnection conn = db.Open();
        using DbCommand cmd = conn.CreateCommand();
        // [읽는 법] COALESCE(기존 값, 새 값)로 NULL을 새 값으로 채운 뒤 GREATEST로 둘 중 큰 값을 남긴다. 결과: 읽음 위치는 절대 뒤로 물러나지 않는다(늦게 도착한 옛 읽음 신호를 무시).
        cmd.CommandText = """
            UPDATE room_members SET last_read_at = GREATEST(COALESCE(last_read_at, @at), @at)
            WHERE room_id = @room AND employee_no = @no
            """;
        cmd.AddParam("@at", upTo);
        cmd.AddParam("@room", roomId);
        cmd.AddParam("@no", employeeNo);
        cmd.ExecuteNonQuery();
    }
}
