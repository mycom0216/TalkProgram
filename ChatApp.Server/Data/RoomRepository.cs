using System.Data.Common;
using ChatApp.Common.Models;
using ChatApp.Server.Core;

namespace ChatApp.Server.Data;

// ═══════════════════════════════════════════════════════════════════════════
// [파일 역할] rooms, room_members 테이블 읽기·쓰기.
// [핵심] 방과 멤버를 DB에 저장해서 서버를 다시 켜도 단체방이 그대로 남게 한다(migrate_005).
// [스레드] 여러 수신 스레드가 동시에 부른다. 메서드마다 연결을 따로 열므로 안전하다.
// ═══════════════════════════════════════════════════════════════════════════
public sealed class RoomRepository(Db db)
{
    /// <summary>기본 방("전체 채팅", 1번)이 없으면 만든다. 서버 시작 시 1번 호출.</summary>
    // [개념] INSERT IGNORE: 같은 PRIMARY KEY가 이미 있으면 오류 없이 아무 일도 하지 않는다.
    public void EnsureLobby()
    {
        using DbConnection conn = db.Open();
        using DbCommand cmd = conn.CreateCommand();
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
        var rooms = new Dictionary<int, Room>();
        using DbConnection conn = db.Open();
        try
        {
            using (DbCommand cmd = conn.CreateCommand())
            {
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
                    room.Members[no] = new RoomMember(no, r.GetString(2), Db.AsUtc(r.GetDateTime(3)), r.IsDBNull(4) ? null : Db.AsUtc(r.GetDateTime(4)));
                }
            }
        }
        catch (DbException ex) when (ex.SqlState is "42S02" or "42S22") // 테이블·칸 없음 = migrate_005 전
        {
            return [];
        }
        // 멤버가 하나도 없는 방은 쓸 수 없으므로 뺀다
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
