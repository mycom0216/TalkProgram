using System.Data.Common;
using System.Text;
using ChatApp.Common.Models;

namespace ChatApp.Server.Data;

/// <summary>저장할 메시지 1개. RoomId가 있으면 방 채팅, ReceiverNo가 있으면 1:1(귓속말).</summary>
/// <param name="Id">서버가 보내는 순간 정한 메시지 번호 (답장·삭제·공감이 이 번호로 메시지를 가리킨다).</param>
/// <param name="ReplyTo">답장이면 원래 메시지 번호.</param>
public sealed record StoredMessage(long Id, int? RoomId, string SenderNo, string? ReceiverNo, string Content, string Type,
                                   DateTime SentAt, long? ReplyTo = null);

/// <summary>메시지 한 개의 기본 정보 (답장·삭제·공감의 권한 확인용).</summary>
public sealed record MessageMeta(long Id, int? RoomId, string SenderNo, string? ReceiverNo, string SenderName,
                                 string Content, DateTime SentAt, bool Deleted, bool Edited = false);

/// <summary>지난 대화 1줄 + (파일이면) 서버 디스크의 저장 경로 (미리보기를 읽을 때 사용).</summary>
public sealed record HistoryRow(HistoryItem Item, string? StoredPath);

/// <summary>
/// messages(채팅·귓속말)·files(파일 메시지)·message_reactions(공감)·direct_reads(1:1 읽음) 테이블을 읽고 쓴다.
/// 쓰기는 대부분 MessageStore의 DB 작업 스레드에서, 조회 일부(메시지 찾기·검색)는 요청한 사람의 수신 스레드에서 부른다.
/// 메서드마다 연결을 따로 열므로 여러 스레드에서 불러도 안전하다.
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
        // [왜] message_id를 DB가 아니라 서버가 정해서 넣는다: 저장(이 스레드, 나중)보다 전송(수신 스레드, 즉시)이 먼저라
        //      AUTO_INCREMENT를 기다리면 받는 사람에게 번호를 알려 줄 수 없다. (MessageStore.NextId 참고)
        cmd.CommandText = """
            INSERT INTO messages (message_id, room_id, sender_no, receiver_no, content, msg_type, sent_at, reply_to)
            VALUES (@id, @room, @sender, @receiver, @content, @type, @sent, @reply)
            """;
        // 같은 명령을 파라미터 값만 바꿔 가며 재사용한다
        DbParameter id = AddParam(cmd, "@id"), room = AddParam(cmd, "@room"), sender = AddParam(cmd, "@sender"),
                    receiver = AddParam(cmd, "@receiver"), content = AddParam(cmd, "@content"), type = AddParam(cmd, "@type"),
                    sent = AddParam(cmd, "@sent"), reply = AddParam(cmd, "@reply");

        foreach (StoredMessage m in messages)
        {
            id.Value = m.Id;
            room.Value = (object?)m.RoomId ?? DBNull.Value;
            sender.Value = m.SenderNo;
            receiver.Value = (object?)m.ReceiverNo ?? DBNull.Value;
            content.Value = m.Content;
            type.Value = m.Type;
            sent.Value = m.SentAt;
            reply.Value = (object?)m.ReplyTo ?? DBNull.Value;
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
    }

    /// <summary>지금까지 쓴 가장 큰 메시지 번호 (서버 시작 시 번호 이어 붙이기용).</summary>
    public long MaxMessageId()
    {
        using DbConnection conn = db.Open();
        using DbCommand cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COALESCE(MAX(message_id), 0) FROM messages";
        return Convert.ToInt64(cmd.ExecuteScalar());
    }

    /// <summary>메시지 하나 찾기. 없으면 null.</summary>
    public MessageMeta? Find(long messageId)
    {
        using DbConnection conn = db.Open();
        using DbCommand cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT m.message_id, m.room_id, m.sender_no, m.receiver_no, COALESCE(CONCAT(u.name, ' ', u.position), m.sender_no),
                   m.content, m.sent_at, m.deleted_at IS NOT NULL, m.edited_at IS NOT NULL
            FROM messages m LEFT JOIN users u ON u.employee_no = m.sender_no
            WHERE m.message_id = @id
            """;
        cmd.AddParam("@id", messageId);
        using DbDataReader r = cmd.ExecuteReader();
        if (!r.Read()) return null;
        return new MessageMeta(r.GetInt64(0), r.IsDBNull(1) ? null : r.GetInt32(1), r.GetString(2), r.IsDBNull(3) ? null : r.GetString(3),
                               r.GetString(4), r.GetString(5), Db.AsUtc(r.GetDateTime(6)), Convert.ToInt32(r.GetValue(7)) == 1,
                               Convert.ToInt32(r.GetValue(8)) == 1);
    }

    public bool MarkEdited(long messageId, string content)
    {
        using DbConnection conn = db.Open();
        using DbCommand cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE messages SET content = @content, edited_at = CURRENT_TIMESTAMP(3) WHERE message_id = @id AND deleted_at IS NULL";
        cmd.AddParam("@content", content);
        cmd.AddParam("@id", messageId);
        return cmd.ExecuteNonQuery() > 0;
    }

    /// <summary>메시지 삭제 표시. 내용은 남겨 둔다(회사 감사·분쟁 대응). 화면과 지난 대화·검색에서는 보이지 않는다.</summary>
    public void MarkDeleted(long messageId, string deletedBy)
    {
        using DbConnection conn = db.Open();
        using DbCommand cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE messages SET deleted_at = NOW(), deleted_by = @by WHERE message_id = @id AND deleted_at IS NULL";
        cmd.AddParam("@by", deletedBy);
        cmd.AddParam("@id", messageId);
        cmd.ExecuteNonQuery();
    }

    /// <summary>공감 누르기/취소 (이미 눌렀으면 취소). 바뀐 뒤의 그 메시지 공감 현황을 돌려준다.</summary>
    public List<ReactionInfo> ToggleReaction(long messageId, string employeeNo, string emoji)
    {
        using DbConnection conn = db.Open();
        using (DbCommand del = conn.CreateCommand())
        {
            del.CommandText = "DELETE FROM message_reactions WHERE message_id = @id AND employee_no = @no AND emoji = @emoji";
            del.AddParam("@id", messageId);
            del.AddParam("@no", employeeNo);
            del.AddParam("@emoji", emoji);
            if (del.ExecuteNonQuery() == 0)
            {
                // 지워진 게 없다 = 아직 안 누름 → 추가
                using DbCommand ins = conn.CreateCommand();
                ins.CommandText = "INSERT IGNORE INTO message_reactions (message_id, employee_no, emoji) VALUES (@id, @no, @emoji)";
                ins.AddParam("@id", messageId);
                ins.AddParam("@no", employeeNo);
                ins.AddParam("@emoji", emoji);
                ins.ExecuteNonQuery();
            }
        }
        return LoadReactions(conn, [messageId]).GetValueOrDefault(messageId) ?? [];
    }

    /// <summary>여러 메시지의 공감 현황을 한 번에 읽는다.</summary>
    private static Dictionary<long, List<ReactionInfo>> LoadReactions(DbConnection conn, IReadOnlyCollection<long> ids)
    {
        var result = new Dictionary<long, List<ReactionInfo>>();
        if (ids.Count == 0) return result;
        using DbCommand cmd = conn.CreateCommand();
        // [주의] IN (...)에 값을 문자열로 이어 붙이지 않고 파라미터(@i0, @i1 ...)로 넣는다(SQL Injection 방지 습관).
        var names = new List<string>();
        int i = 0;
        foreach (long id in ids)
        {
            string name = $"@i{i++}";
            names.Add(name);
            cmd.AddParam(name, id);
        }
        cmd.CommandText = $"""
            SELECT message_id, emoji, employee_no FROM message_reactions
            WHERE message_id IN ({string.Join(",", names)})
            ORDER BY created_at
            """;
        using DbDataReader r = cmd.ExecuteReader();
        while (r.Read())
        {
            long id = r.GetInt64(0);
            string emoji = r.GetString(1);
            List<ReactionInfo> list = result.TryGetValue(id, out var l) ? l : result[id] = [];
            ReactionInfo info = list.FirstOrDefault(x => x.Emoji == emoji) ?? Add(list, new ReactionInfo { Emoji = emoji });
            info.EmployeeNos.Add(r.GetString(2));
        }
        return result;

        static ReactionInfo Add(List<ReactionInfo> list, ReactionInfo item) { list.Add(item); return item; }
    }

    // ───────── 1:1 읽음 위치 ─────────

    /// <summary>1:1 읽음 위치 저장 (더 뒤로 갈 때만).</summary>
    public void UpsertDirectRead(string readerNo, string peerNo, DateTime upTo)
    {
        using DbConnection conn = db.Open();
        using DbCommand cmd = conn.CreateCommand();
        // [개념] INSERT ... ON DUPLICATE KEY UPDATE: 없으면 넣고, 이미 있으면 고친다(한 문장이라 원자적).
        cmd.CommandText = """
            INSERT INTO direct_reads (reader_no, peer_no, last_read_at) VALUES (@r, @p, @at)
            ON DUPLICATE KEY UPDATE last_read_at = GREATEST(last_read_at, VALUES(last_read_at))
            """;
        cmd.AddParam("@r", readerNo);
        cmd.AddParam("@p", peerNo);
        cmd.AddParam("@at", upTo);
        cmd.ExecuteNonQuery();
    }

    /// <summary>reader가 peer와의 1:1 대화를 마지막으로 읽은 시각. 없으면 null.</summary>
    public DateTime? GetDirectRead(string readerNo, string peerNo)
    {
        using DbConnection conn = db.Open();
        using DbCommand cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT last_read_at FROM direct_reads WHERE reader_no = @r AND peer_no = @p";
        cmd.AddParam("@r", readerNo);
        cmd.AddParam("@p", peerNo);
        return cmd.ExecuteScalar() is DateTime t ? Db.AsUtc(t) : null;
    }

    /// <summary>
    /// 내가 아직 읽지 않은 1:1 메시지를 보낸 사람별로 센다 (로그인할 때 "안 읽은 1:1"을 알려 주려고).
    /// </summary>
    public List<UnreadDirect> LoadUnreadDirect(string me)
    {
        using DbConnection conn = db.Open();
        using DbCommand cmd = conn.CreateCommand();
        // 상대별로: 내가 마지막으로 읽은 시각(direct_reads) 이후에 상대가 보낸 메시지 수, 마지막 메시지
        cmd.CommandText = """
            SELECT m.sender_no, COALESCE(CONCAT(u.name, ' ', u.position), m.sender_no), COUNT(*), MAX(m.sent_at),
                   SUBSTRING_INDEX(GROUP_CONCAT(IF(m.deleted_at IS NULL, m.content, '') ORDER BY m.sent_at DESC SEPARATOR '\n'), '\n', 1)
            FROM messages m
            LEFT JOIN direct_reads d ON d.reader_no = @me AND d.peer_no = m.sender_no
            LEFT JOIN users u ON u.employee_no = m.sender_no
            WHERE m.room_id IS NULL AND m.receiver_no = @me
              AND m.sent_at > COALESCE(d.last_read_at, '1000-01-01')
              AND m.sent_at > NOW() - INTERVAL 30 DAY
            GROUP BY m.sender_no, u.name, u.position
            ORDER BY MAX(m.sent_at) DESC
            LIMIT 50
            """;
        cmd.AddParam("@me", me);
        var list = new List<UnreadDirect>();
        using DbDataReader r = cmd.ExecuteReader();
        while (r.Read())
            list.Add(new UnreadDirect
            {
                PeerNo = r.GetString(0), PeerName = r.GetString(1), Count = Convert.ToInt32(r.GetValue(2)),
                LastTime = Db.AsUtc(r.GetDateTime(3)), LastText = r.IsDBNull(4) ? "" : Preview(r.GetString(4)),
            });
        return list;
    }

    // ───────── 지난 대화 ─────────

    /// <summary>방의 지난 대화 (since 이후 ~ before 이전, 최근 limit개). 오래된 것 → 최근 순서로 돌려준다.</summary>
    /// <param name="since">이 시각 이전 메시지는 주지 않는다 (나중에 들어온 멤버는 들어오기 전 대화를 보지 못한다). null = 처음부터.</param>
    public List<HistoryRow> LoadRoom(int roomId, DateTime before, int limit, DateTime? since, string? beforeKey = null)
    {
        const string messageWhere = "m.room_id = @room AND m.sent_at >= @since";
        const string fileWhere = "f.room_id = @room AND f.created_at >= @since";
        return Load(messageWhere, fileWhere, before, limit, beforeKey, cmd =>
        {
            cmd.AddParam("@room", roomId);
            cmd.AddParam("@since", since ?? DateTime.MinValue);
        });
    }

    /// <summary>두 사람의 1:1 지난 대화.</summary>
    public List<HistoryRow> LoadDirect(string me, string peer, DateTime before, int limit, string? beforeKey = null)
    {
        // [왜] (내가 보낸 것) UNION (상대가 보낸 것)처럼 두 방향을 따로 찾는 조건으로 쓴다.
        //      인덱스 idx_dm(sender_no, receiver_no, sent_at)이 두 방향 모두에 그대로 쓰여, 대화가 많아도 빠르다.
        const string pair = "((sender_no = @me AND receiver_no = @peer) OR (sender_no = @peer AND receiver_no = @me))";
        string messageWhere = "m.room_id IS NULL AND m.msg_type = 'WHISPER' AND " + pair.Replace("sender_no", "m.sender_no").Replace("receiver_no", "m.receiver_no");
        string fileWhere = "f.room_id IS NULL AND " + pair.Replace("sender_no", "f.sender_no").Replace("receiver_no", "f.receiver_no");
        return Load(messageWhere, fileWhere, before, limit, beforeKey, cmd =>
        {
            cmd.AddParam("@me", me);
            cmd.AddParam("@peer", peer);
        });
    }

    /// <summary>
    /// 메시지와 파일을 각각 최근 limit개씩 읽어 시간순으로 합친 뒤, 그중 최근 limit개만 남긴다.
    /// (두 테이블을 SQL UNION으로 합칠 수도 있지만, 열 구성이 달라 C#에서 합치는 편이 읽기 쉽다)
    /// </summary>
    private List<HistoryRow> Load(string messageWhere, string fileWhere, DateTime before, int limit, string? beforeKey, Action<DbCommand> addParams)
    {
        string messageCursor = beforeKey is null ? "m.sent_at < @before" : "(m.sent_at < @before OR (m.sent_at = @before AND CONCAT('M:', LPAD(m.message_id, 20, '0')) < @before_key))";
        string fileCursor = beforeKey is null ? "f.created_at < @before" : "(f.created_at < @before OR (f.created_at = @before AND CONCAT('F:', f.file_id) < @before_key))";
        var messageRows = new List<HistoryRow>();
        var fileRows = new List<HistoryRow>();
        using DbConnection conn = db.Open();

        using (DbCommand cmd = conn.CreateCommand())
        {
            // users와 JOIN해서 "이름 직급"을 함께 가져온다 (탈퇴 등으로 사용자가 없으면 사원번호만 표시).
            // 답장이면 원래 메시지(rm)와 그 보낸 사람(ru)도 함께 읽어 인용 미리보기를 만든다.
            cmd.CommandText = $"""
                SELECT m.message_id, m.sender_no, COALESCE(CONCAT(u.name, ' ', u.position), m.sender_no),
                       m.content, m.msg_type, m.sent_at, m.deleted_at IS NOT NULL,
                       m.reply_to, COALESCE(CONCAT(ru.name, ' ', ru.position), rm.sender_no), rm.content, rm.deleted_at IS NOT NULL,
                       m.edited_at IS NOT NULL
                FROM messages m
                LEFT JOIN users u ON u.employee_no = m.sender_no
                LEFT JOIN messages rm ON rm.message_id = m.reply_to
                LEFT JOIN users ru ON ru.employee_no = rm.sender_no
                WHERE {messageWhere} AND {messageCursor}
                ORDER BY m.sent_at DESC, m.message_id DESC
                LIMIT @limit
                """;
            addParams(cmd);
            cmd.AddParam("@before", before);
            if (beforeKey is not null) cmd.AddParam("@before_key", beforeKey);
            cmd.AddParam("@limit", limit);
            using DbDataReader r = cmd.ExecuteReader();
            while (r.Read())
            {
                bool deleted = Convert.ToInt32(r.GetValue(6)) == 1;
                ReplyInfo? reply = null;
                if (!r.IsDBNull(7))
                {
                    bool replyDeleted = !r.IsDBNull(10) && Convert.ToInt32(r.GetValue(10)) == 1;
                    reply = new ReplyInfo
                    {
                        MessageId = r.GetInt64(7),
                        SenderName = r.IsDBNull(8) ? "" : r.GetString(8),
                        Text = r.IsDBNull(9) ? "(지난 메시지)" : replyDeleted ? "삭제된 메시지입니다." : Preview(r.GetString(9)),
                    };
                }
                messageRows.Add(new HistoryRow(new HistoryItem
                {
                    MessageId = r.GetInt64(0),
                    Kind = r.GetString(4) == "WHISPER" ? HistoryKind.Whisper : HistoryKind.Chat,
                    SenderNo = r.GetString(1),
                    SenderName = r.GetString(2),
                    Body = deleted ? "" : r.GetString(3), // 삭제된 메시지 내용은 클라이언트에 보내지 않는다
                    Deleted = deleted,
                    Time = Db.AsUtc(r.GetDateTime(5)),
                    ReplyTo = reply,
                    Edited = Convert.ToInt32(r.GetValue(11)) == 1,
                }, null));
            }
        }

        // 공감 현황을 한 번에 붙인다 (메시지마다 따로 조회하면 50번 왕복)
        var ids = messageRows.Select(x => x.Item.MessageId).ToList();
        foreach (var (id, reactions) in LoadReactions(conn, ids))
            messageRows.First(x => x.Item.MessageId == id).Item.Reactions = reactions;

        using (DbCommand cmd = conn.CreateCommand())
        {
            cmd.CommandText = $"""
                SELECT f.file_id, f.sender_no, COALESCE(CONCAT(u.name, ' ', u.position), f.sender_no),
                       f.file_name, f.file_size, f.file_kind, f.stored_path, f.created_at, f.room_id, f.receiver_no
                FROM files f LEFT JOIN users u ON u.employee_no = f.sender_no
                WHERE {fileWhere} AND f.status = 'DONE' AND {fileCursor}
                ORDER BY f.created_at DESC, f.file_id DESC
                LIMIT @limit
                """;
            addParams(cmd);
            cmd.AddParam("@before", before);
            if (beforeKey is not null) cmd.AddParam("@before_key", beforeKey);
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
                    SentAt = Db.AsUtc(r.GetDateTime(7)),
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
        return messageRows.Concat(fileRows).OrderBy(x => x.Item.Time).ThenBy(HistorySortKey, StringComparer.Ordinal).TakeLast(limit).ToList();
    }

    private static string HistorySortKey(HistoryRow row) => row.Item.File is SharedFile file
        ? "F:" + file.FileId
        : "M:" + row.Item.MessageId.ToString("D20", System.Globalization.CultureInfo.InvariantCulture);

    // ───────── 검색 ─────────

    /// <summary>
    /// 내가 볼 수 있는 대화(멤버인 방 + 내가 낀 1:1)에서 글자를 찾는다. 최근 것부터 limit개.
    /// </summary>
    // [왜] 권한 조건을 SQL에 직접 넣는다. 일단 다 찾고 C#에서 거르면, 남의 대화 때문에 결과 개수가 모자라거나 느려진다.
    // [주의] LIKE '%검색어%'는 인덱스를 쓰지 못해 전체를 훑는다. 그래서 보관 기간 정리(RetentionService)와 결과 개수 제한이 중요하다.
    //        (MariaDB FULLTEXT는 한국어 띄어쓰기 없는 검색에 약해서 LIKE를 썼다)
    public List<SearchHit> Search(string me, IReadOnlyCollection<int> roomIds, string keyword, int limit)
    {
        using DbConnection conn = db.Open();
        using DbCommand cmd = conn.CreateCommand();
        var roomNames = new List<string>();
        int i = 0;
        foreach (int id in roomIds)
        {
            string name = $"@r{i++}";
            roomNames.Add(name);
            cmd.AddParam(name, id);
        }
        string roomCondition = roomNames.Count > 0 ? $"m.room_id IN ({string.Join(",", roomNames)})" : "FALSE";
        cmd.CommandText = $"""
            SELECT m.message_id, m.room_id, m.sender_no, COALESCE(CONCAT(u.name, ' ', u.position), m.sender_no),
                   m.receiver_no, m.content, m.sent_at,
                   COALESCE(CONCAT(pu.name, ' ', pu.position), IF(m.sender_no = @me, m.receiver_no, m.sender_no))
            FROM messages m
            LEFT JOIN users u ON u.employee_no = m.sender_no
            LEFT JOIN users pu ON pu.employee_no = IF(m.sender_no = @me, m.receiver_no, m.sender_no)
            WHERE m.deleted_at IS NULL
              AND m.content LIKE @kw ESCAPE '\\'
              AND ({roomCondition} OR (m.room_id IS NULL AND (m.sender_no = @me OR m.receiver_no = @me)))
            ORDER BY m.sent_at DESC
            LIMIT @limit
            """;
        cmd.AddParam("@me", me);
        cmd.AddParam("@kw", "%" + EscapeLike(keyword) + "%");
        cmd.AddParam("@limit", limit);

        var hits = new List<SearchHit>();
        using DbDataReader r = cmd.ExecuteReader();
        while (r.Read())
        {
            int? roomId = r.IsDBNull(1) ? null : r.GetInt32(1);
            string senderNo = r.GetString(2);
            string? receiverNo = r.IsDBNull(4) ? null : r.GetString(4);
            hits.Add(new SearchHit
            {
                MessageId = r.GetInt64(0),
                RoomId = roomId,
                SenderNo = senderNo,
                SenderName = r.GetString(3),
                Body = r.GetString(5),
                Time = Db.AsUtc(r.GetDateTime(6)),
                PeerNo = roomId is null ? (senderNo == me ? receiverNo : senderNo) : null,
                PeerName = roomId is null && !r.IsDBNull(7) ? r.GetString(7) : null,
            });
        }
        return hits;
    }

    /// <summary>LIKE의 특수 문자(%, _, \)를 글자 그대로 찾게 바꾼다. 예) "100%" 검색이 "100으로 시작하는 모든 것"이 되지 않게.</summary>
    public static string EscapeLike(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (char c in s)
        {
            if (c is '%' or '_' or '\\') sb.Append('\\');
            sb.Append(c);
        }
        return sb.ToString();
    }

    /// <summary>답장 미리보기용으로 앞부분만.</summary>
    public static string Preview(string text)
        => ChatApp.Common.Utils.TextUtil.Truncate(text.ReplaceLineEndings(" "), ReplyInfo.MaxPreviewLength);

    private static DbParameter AddParam(DbCommand cmd, string name)
    {
        DbParameter p = cmd.CreateParameter();
        p.ParameterName = name;
        cmd.Parameters.Add(p);
        return p;
    }
}
