using System.Data.Common;
using ChatApp.Common.Models;

namespace ChatApp.Server.Data;

// ═══════════════════════════════════════════════════════════════════════════
// [파일 역할] rooms 테이블에 방을 기록하고 방 번호를 발급받는다.
// [스레드]   방을 만드는 사람의 수신 스레드에서 호출된다(동시에 여러 명 가능). 메서드마다 자기 연결을 쓴다.
// [연결]     Program.cs가 만들어 ChatServer → PacketHandler(HandleRoomCreate)에 넘긴다.
// ═══════════════════════════════════════════════════════════════════════════
/// <summary>
/// rooms 테이블. 방 번호를 DB(AUTO_INCREMENT)에서 받아 와서,
/// 서버를 다시 켜도 방 번호가 겹치지 않고 대화 기록(messages.room_id)과 연결할 수 있게 한다.
/// </summary>
// [개념] (Db db) = 기본 생성자. 메서드 안에서 db를 바로 쓸 수 있다.
// [왜] 방 번호를 서버 메모리 카운터가 아니라 DB AUTO_INCREMENT로 받는 이유: 서버를 껐다 켜도
//      번호가 1부터 다시 시작하지 않아, 예전 방의 대화 기록과 새 방이 같은 번호로 섞이지 않는다.
public sealed class RoomRepository(Db db)
{
    /// <summary>기본 방("전체 채팅", 1번)이 없으면 만든다. 서버 시작 시 1회 호출.</summary>
    // [개념] INSERT IGNORE: 같은 기본 키(room_id = 1)가 이미 있으면 오류 없이 그냥 넘어간다.
    //        "없으면 만들기"를 한 문장으로 처리할 수 있다.
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

    /// <summary>새 방을 저장하고 방 번호를 돌려준다.</summary>
    public int Create(string name, string ownerNo, int maxUsers)
    {
        using DbConnection conn = db.Open();

        // [왜] INSERT와 SELECT LAST_INSERT_ID()를 "같은 연결(conn)"에서 실행해야 한다.
        //      LAST_INSERT_ID는 연결마다 따로 기억되는 값이기 때문이다.
        using (DbCommand insert = conn.CreateCommand())
        {
            insert.CommandText = "INSERT INTO rooms (room_name, owner_no, max_users) VALUES (@name, @owner, @max)";
            insert.AddParam("@name", name);
            insert.AddParam("@owner", ownerNo);
            insert.AddParam("@max", maxUsers);
            insert.ExecuteNonQuery();
        }

        // [개념] AUTO_INCREMENT: INSERT할 때 room_id를 비워 두면 DB가 1, 2, 3... 자동으로 번호를 매긴다.
        // [주의] SELECT MAX(room_id)로 번호를 알아내면, 그 사이 다른 스레드가 만든 방 번호를 잘못 가져올 수 있다.
        // LAST_INSERT_ID()는 "이 연결"에서 마지막으로 만든 번호라, 다른 스레드가 동시에 방을 만들어도 섞이지 않는다
        using DbCommand select = conn.CreateCommand();
        select.CommandText = "SELECT LAST_INSERT_ID()";
        return Convert.ToInt32(select.ExecuteScalar());
    }
}
