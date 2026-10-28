using System.Data.Common;
using ChatApp.Common.Models;

namespace ChatApp.Server.Data;

/// <summary>저장할 메시지 1개. RoomId가 있으면 방 채팅, ReceiverNo가 있으면 1:1(귓속말).</summary>
public sealed record StoredMessage(int? RoomId, string SenderNo, string? ReceiverNo, string Content, string Type, DateTime SentAt);

/// <summary>지난 대화 1줄 + (파일이면) 서버 디스크의 저장 경로 (미리보기를 읽을 때 사용).</summary>
public sealed record HistoryRow(HistoryItem Item, string? StoredPath);

/// <summary>
/// messages 테이블(채팅·귓속말)과 files 테이블(파일 메시지)에서 지난 대화를 읽고 쓴다.
/// 이 클래스는 MessageStore의 DB 작업 스레드 하나에서만 호출된다.
/// </summary>
public sealed class MessageRepository(Db db)
{
    /// <summary>
    /// 여러 메시지를 트랜잭션 하나로 저장한다.
    /// 메시지마다 따로 커밋하면 디스크 기록(fsync)이 메시지 수만큼 일어나 느리다. 묶어서 한 번에 커밋하면 훨씬 빠르다.
    /// </summary>
    public void InsertMany(IReadOnlyList<StoredMessage> messages)
    {
        if (messages.Count == 0) return;

        using DbConnection conn = db.Open();
        using DbTransaction tx = conn.BeginTransaction();
        using DbCommand cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO messages (room_id, sender_no, receiver_no, content, msg_type, sent_at)
            VALUES (@room, @sender, @receiver, @content, @type, @sent)
            """;
        // 같은 명령을 파라미터 값만 바꿔 가며 재사용한다
        DbParameter room = AddParam(cmd, "@room"), sender = AddParam(cmd, "@sender"), receiver = AddParam(cmd, "@receiver"),
                    content = AddParam(cmd, "@content"), type = AddParam(cmd, "@type"), sent = AddParam(cmd, "@sent");

        foreach (StoredMessage m in messages)
        {
            room.Value = (object?)m.RoomId ?? DBNull.Value;
            sender.Value = m.SenderNo;
            receiver.Value = (object?)m.ReceiverNo ?? DBNull.Value;
            content.Value = m.Content;
            type.Value = m.Type;
            sent.Value = m.SentAt;
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
    }

    /// <summary>방의 지난 대화 (before 이전, 최근 limit개). 오래된 것 → 최근 순서로 돌려준다.</summary>
    public List<HistoryRow> LoadRoom(int roomId, DateTime before, int limit)
    {
        const string messageWhere = "m.room_id = @room";
        const string fileWhere = "f.room_id = @room";
        return Load(messageWhere, fileWhere, before, limit, cmd => cmd.AddParam("@room", roomId));
    }

    /// <summary>두 사람의 1:1 지난 대화.</summary>
    public List<HistoryRow> LoadDirect(string me, string peer, DateTime before, int limit)
    {
        // (내가 보낸 것) 또는 (상대가 나에게 보낸 것)
        const string pair = "((sender_no = @me AND receiver_no = @peer) OR (sender_no = @peer AND receiver_no = @me))";
        string messageWhere = "m.room_id IS NULL AND m.msg_type = 'WHISPER' AND " + pair.Replace("sender_no", "m.sender_no").Replace("receiver_no", "m.receiver_no");
        string fileWhere = "f.room_id IS NULL AND " + pair.Replace("sender_no", "f.sender_no").Replace("receiver_no", "f.receiver_no");
        return Load(messageWhere, fileWhere, before, limit, cmd =>
        {
            cmd.AddParam("@me", me);
            cmd.AddParam("@peer", peer);
        });
    }

    /// <summary>
    /// 메시지와 파일을 각각 최근 limit개씩 읽어 시간순으로 합친 뒤, 그중 최근 limit개만 남긴다.
    /// (두 테이블을 SQL UNION으로 합칠 수도 있지만, 열 구성이 달라 C#에서 합치는 편이 읽기 쉽다)
    /// </summary>
    private List<HistoryRow> Load(string messageWhere, string fileWhere, DateTime before, int limit, Action<DbCommand> addParams)
    {
        var messageRows = new List<HistoryRow>();
        var fileRows = new List<HistoryRow>();
        using DbConnection conn = db.Open();

        using (DbCommand cmd = conn.CreateCommand())
        {
            // users와 JOIN해서 "이름 직급"을 함께 가져온다 (탈퇴 등으로 사용자가 없으면 사원번호만 표시)
            cmd.CommandText = $"""
                SELECT m.sender_no, COALESCE(CONCAT(u.name, ' ', u.position), m.sender_no), m.content, m.msg_type, m.sent_at
                FROM messages m LEFT JOIN users u ON u.employee_no = m.sender_no
                WHERE {messageWhere} AND m.sent_at < @before
                ORDER BY m.sent_at DESC, m.message_id DESC
                LIMIT @limit
                """;
            addParams(cmd);
            cmd.AddParam("@before", before);
            cmd.AddParam("@limit", limit);
            using DbDataReader r = cmd.ExecuteReader();
            while (r.Read())
            {
                messageRows.Add(new HistoryRow(new HistoryItem
                {
                    Kind = r.GetString(3) == "WHISPER" ? HistoryKind.Whisper : HistoryKind.Chat,
                    SenderNo = r.GetString(0),
                    SenderName = r.GetString(1),
                    Body = r.GetString(2),
                    Time = r.GetDateTime(4),
                }, null));
            }
        }

        using (DbCommand cmd = conn.CreateCommand())
        {
            cmd.CommandText = $"""
                SELECT f.file_id, f.sender_no, COALESCE(CONCAT(u.name, ' ', u.position), f.sender_no),
                       f.file_name, f.file_size, f.file_kind, f.stored_path, f.created_at, f.room_id, f.receiver_no
                FROM files f LEFT JOIN users u ON u.employee_no = f.sender_no
                WHERE {fileWhere} AND f.status = 'DONE' AND f.created_at < @before
                ORDER BY f.created_at DESC
                LIMIT @limit
                """;
            addParams(cmd);
            cmd.AddParam("@before", before);
            cmd.AddParam("@limit", limit);
            using DbDataReader r = cmd.ExecuteReader();
            while (r.Read())
            {
                var file = new SharedFile
                {
                    FileId = r.GetString(0),
                    SenderNo = r.GetString(1),
                    SenderName = r.GetString(2),
                    FileName = r.GetString(3),
                    Size = Convert.ToInt64(r.GetValue(4)),
                    Kind = Enum.TryParse(r.GetString(5), ignoreCase: true, out FileKind kind) ? kind : FileKind.File,
                    SentAt = r.GetDateTime(7),
                    RoomId = r.IsDBNull(8) ? null : Convert.ToInt32(r.GetValue(8)),
                    ToEmployeeNo = r.IsDBNull(9) ? null : r.GetString(9),
                };
                fileRows.Add(new HistoryRow(new HistoryItem
                {
                    Kind = HistoryKind.File,
                    SenderNo = file.SenderNo,
                    SenderName = file.SenderName,
                    Body = $"[파일] {file.FileName}",
                    Time = file.SentAt,
                    File = file,
                }, r.GetString(6)));
            }
        }

        // SQL은 최근 것부터(DESC) 읽었으므로 뒤집어서 오래된 것부터로 만든 뒤 두 목록을 시간순으로 합친다.
        // OrderBy는 "안정 정렬"이라 같은 밀리초에 보낸 메시지끼리는 원래(입력) 순서가 유지된다.
        messageRows.Reverse();
        fileRows.Reverse();
        return messageRows.Concat(fileRows).OrderBy(x => x.Item.Time).TakeLast(limit).ToList();
    }

    private static DbParameter AddParam(DbCommand cmd, string name)
    {
        DbParameter p = cmd.CreateParameter();
        p.ParameterName = name;
        cmd.Parameters.Add(p);
        return p;
    }
}
