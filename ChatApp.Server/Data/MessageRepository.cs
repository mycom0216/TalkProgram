using System.Data.Common;
using System.Text;
using ChatApp.Common.Models;

namespace ChatApp.Server.Data;

//
// ───────────────────────────────────────────────────────────────────────────
// [공부 노트 · 면접 대비]
//
// ■ 한 줄 요약
//   대화에 관한 모든 SQL: 메시지 묶음 저장, 한 건 찾기, 수정·삭제 표시, 공감 토글, 1:1 읽음 위치와 "안 읽은 1:1", 지난 대화(방·1:1), 검색.
//   쓰기는 대부분 MessageStore의 DbWriter 스레드가, 일부 조회(찾기·검색)는 요청한 사람의 수신 스레드가 부른다. 메서드마다 연결을 따로 열어 어느 스레드에서 불러도 안전하다.
//
// ■ 표와 규칙
//   messages: 방 채팅은 room_id가 있고, 1:1은 room_id가 NULL + receiver_no + msg_type = WHISPER. 삭제는 deleted_at/deleted_by(내용은 남김), 수정은 edited_at, 답장은 reply_to(같은 표의 다른 행)
//   files: 파일 말풍선도 대화 기록에 나오므로 지난 대화는 messages와 files 두 표를 함께 읽어 시간순으로 합친다.
//   message_reactions(공감), direct_reads(1:1에서 내가 어디까지 읽었는지: 읽는 사람 + 상대가 키)
//
// ■ 읽는 순서
//   ① 맨 위 record 4개(저장용·권한 확인용·지난 대화 한 줄) → ② InsertMany → ③ Find/MarkEdited/MarkDeleted → ④ ToggleReaction/LoadReactions → ⑤ 1:1 읽음·안 읽은 수
//   → ⑥ LoadRoom/LoadDirect → Load(핵심, 가장 길다) → ⑦ Search → ⑧ 맨 아래 작은 도우미(EscapeLike, Preview, AddParam)
//
// ■ 이 파일에서 나오는 개념과 검색어
//   - 묶음 INSERT + 명령 재사용(파라미터 값만 교체) ......... "ADO.NET reuse command parameters batch insert transaction"
//   - 소프트 삭제(deleted_at) ............................... "soft delete audit"
//   - 키셋 페이지네이션(시각 + 고유 키) ...................... "keyset pagination tie-breaker"
//   - N+1 문제와 IN (...) 한 번에 읽기 ....................... "N+1 query problem IN clause"
//   - LIKE와 인덱스, 와일드카드 이스케이프 ................... "SQL LIKE leading wildcard index escape"
//   - GROUP BY / GROUP_CONCAT / SUBSTRING_INDEX .............. "MySQL GROUP_CONCAT SUBSTRING_INDEX last row per group"
//   - 자기 조인(답장 원문 가져오기) ........................... "self join reply to parent row"
//   - LINQ 안정 정렬(OrderBy → ThenBy), TakeLast ............. "LINQ OrderBy stable sort ThenBy TakeLast"
//
// ■ 면접 예상 질문과 답
//   Q1. 메시지를 묶어서 저장하는데, 명령 객체와 매개변수를 재사용하는 이유는요?
//       A. 같은 INSERT 문을 값만 바꿔 반복하므로, 명령과 매개변수 객체를 한 번 만들고 값(Value)만 교체하면 객체 생성과 SQL 해석 비용이 줄어듭니다. 전체를 한 트랜잭션으로 커밋해 디스크 기록 횟수도 1번으로 줄입니다. 중간에 실패하면 모두 롤백되므로, 호출한 MessageStore가 한 건씩 다시 저장해 문제 메시지만 버립니다.
//   Q2. 메시지 번호를 DB가 아니라 서버가 정해서 넣는 이유는요?
//       A. 저장은 큐에서 나중에 일어나는데 받는 사람에게는 바로 번호를 알려 줘야 답장·삭제·공감을 할 수 있기 때문입니다. AUTO_INCREMENT를 쓰면 저장이 끝난 뒤에야 번호를 알 수 있습니다. 서버가 시작할 때 MaxMessageId()로 가장 큰 번호를 읽어 이어서 발급합니다.
//   Q3. 삭제된 메시지의 내용을 DB에 남기고 클라이언트에는 보내지 않는 방식을 어떻게 구현했나요?
//       A. MarkDeleted는 deleted_at/deleted_by만 채우는 소프트 삭제입니다. 지난 대화를 읽을 때 deleted이면 Body를 빈 문자열로 바꾸고 Deleted 표시만 보내며, 검색은 deleted_at IS NULL 조건으로 제외합니다. 답장의 원문이 삭제됐으면 "삭제된 메시지입니다."로 가립니다. 회사 감사·분쟁 대비로 원본은 DB에만 있습니다.
//   Q4. 지난 대화에서 한 번에 가져오는 개수와 "다음 쪽"을 어떻게 처리하나요?
//       A. (시각, 고유 키)보다 이전인 것만 읽는 키셋 방식입니다. 메시지와 파일을 각각 최근 limit개씩 읽어 C#에서 시간순으로 합친 뒤 최근 limit개만 남깁니다. 같은 밀리초에 여러 개가 있어도 순서가 흔들리지 않도록 "M:메시지번호(20자리 0 채움)" / "F:파일ID" 형태의 문자열 키를 두 번째 정렬 기준으로 씁니다.
//   Q5. 공감(리액션)을 메시지마다 따로 조회하지 않는 이유는요?
//       A. 50개를 읽으면서 메시지마다 조회하면 DB 왕복이 50번 늘어나는 N+1 문제가 생깁니다. 읽은 메시지 번호를 모아 IN (@i0, @i1, ...) 한 번으로 모두 읽고 번호별로 묶어 붙입니다. IN 목록도 문자열로 이어 붙이지 않고 매개변수 이름만 만들어 넣어 SQL 인젝션을 막습니다.
//   Q6. 공감 누르기/취소(ToggleReaction)는 어떻게 동작하나요?
//       A. 먼저 DELETE를 시도해서 지워진 행이 있으면 "이미 눌렀던 것 = 취소", 0행이면 "아직 안 눌렀음"이라 INSERT IGNORE로 추가합니다. 그 뒤 그 메시지의 공감 현황 전체를 다시 읽어 돌려줍니다. 한 연결 안에서 일어나고, (메시지, 사원번호, 이모지)가 기본 키라 동시에 두 번 눌러도 중복 행은 생기지 않습니다.
//   Q7. 검색이 느려지지 않게 어떤 장치를 했나요? 한계도 알려 주세요.
//       A. LIKE '%검색어%'는 앞쪽에 %가 있어 인덱스를 못 쓰고 훑습니다. 그래서 결과 개수 제한(limit), 검색어 최소·최대 길이, 사람당 2초 간격 제한, 삭제된 메시지 제외, 보관 기간 정리로 범위를 줄였습니다. 한국어는 띄어쓰기 없는 검색에서 FULLTEXT가 약해 LIKE를 택했습니다. 메시지가 아주 많아지면 별도 검색 엔진(예: Elasticsearch)이 필요합니다.
//   Q8. 검색어에 %나 _가 들어 있으면요?
//       A. LIKE에서 %는 "아무 글자들", _는 "아무 글자 한 개"라서 사용자가 "100%"를 검색하면 "100으로 시작하는 모든 것"이 됩니다. EscapeLike가 %, _, \ 앞에 역슬래시를 붙여 글자 그대로 찾게 하고, SQL에는 ESCAPE 문자를 지정합니다. 값은 매개변수로 넘깁니다.
//   Q9. "안 읽은 1:1" 수를 구하는 쿼리는 어떤 구조인가요?
//       A. 내가 받은 1:1 메시지 중 "내가 마지막으로 읽은 시각(direct_reads) 이후 + 최근 30일"만 보낸 사람별로 묶어 개수·마지막 시각을 구합니다. 마지막 메시지 내용은 GROUP_CONCAT으로 최근순으로 이어 붙인 뒤 SUBSTRING_INDEX로 첫 줄만 잘라 냅니다. 한 번의 쿼리로 끝나는 대신, 메시지가 매우 많거나 줄바꿈이 있는 글은 미리보기가 첫 줄만 보일 수 있습니다.
//   Q10. 한계는요? (솔직하게 답하기)
//       A. (1) 검색은 "내가 멤버인 방" 전체를 대상으로 하고 멤버가 된 시각 이후로 제한하지 않아, 지난 대화(입장 이후만 보임)와 규칙이 다르다 — 입장 전 대화가 검색 결과에 나올 수 있다(수정 후보). (2) 열 번호(0~11)로 읽는 코드라 SELECT 순서를 바꾸면 조용히 어긋난다. (3) 지난 대화는 두 표를 각각 limit개씩 읽어 합치므로 최대 2×limit행을 읽는다. (4) 공감 토글(DELETE 후 INSERT)은 트랜잭션이 아니라서 아주 드물게 동시 클릭이 겹치면 결과가 한 번 어긋날 수 있다(INSERT IGNORE로 중복 행은 막힘).
//
// ■ 문제가 났을 때 어디를 볼까 (유지보수)
//   - 지난 대화가 일부만 나온다 ................ Load의 limit, 키셋 조건(beforeKey), 파일이 DONE 상태인지, 삭제 여부(Deleted)
//   - 같은 시각의 메시지 순서가 뒤바뀐다 ........ HistorySortKey("M:" 0 채움 / "F:" 파일ID)와 SQL ORDER BY의 방향이 같은지
//   - 검색에서 삭제한 메시지가 나온다 ........... Search의 deleted_at IS NULL 조건
//   - 검색이 느리다 ........................... LIKE 전체 훑기: 메시지 수, 보관 기간(MessageRetentionDays), 결과 limit
//   - 안 읽은 1:1 수가 틀리다 ................... direct_reads의 last_read_at, 30일 제한, UpsertDirectRead의 GREATEST
//   - 저장 때 "Data too long" ................... 내용 칸 크기(VARCHAR(500))와 MessageService.MaxMessageLength가 같은지
// ───────────────────────────────────────────────────────────────────────────

/// <summary>저장할 메시지 1개. RoomId가 있으면 방 채팅, ReceiverNo가 있으면 1:1(귓속말).</summary>
/// <param name="Id">서버가 보내는 순간 정한 메시지 번호 (답장·삭제·공감이 이 번호로 메시지를 가리킨다).</param>
/// <param name="ReplyTo">답장이면 원래 메시지 번호.</param>
// [읽는 법] 저장 직전의 메시지 한 건(값 묶음). Id는 서버가 먼저 정한 번호, RoomId가 null이고 ReceiverNo가 있으면 1:1, Type은 CHAT/WHISPER/SYSTEM 같은 종류 문자열, ReplyTo는 답장일 때만 값이 있다(기본값 null이라 생략 가능).
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
        // [읽는 법] 매개변수 객체를 한 번만 만들어 변수에 보관하고(아래 맨 끝의 AddParam 도우미가 값 없는 매개변수를 만든다), 반복문에서는 .Value만 바꿔 같은 명령을 재사용한다. 건마다 새로 만드는 것보다 가볍다.
        DbParameter id = AddParam(cmd, "@id"), room = AddParam(cmd, "@room"), sender = AddParam(cmd, "@sender"),
                    receiver = AddParam(cmd, "@receiver"), content = AddParam(cmd, "@content"), type = AddParam(cmd, "@type"),
                    sent = AddParam(cmd, "@sent"), reply = AddParam(cmd, "@reply");

        foreach (StoredMessage m in messages)
        {
            id.Value = m.Id;
            // [개념] m.RoomId는 int?(값 또는 null)이다. DB에는 null이 아니라 DBNull.Value를 넘겨야 하고, ?? 의 양쪽 형식이 같아야 해서 앞을 (object?)로 바꿔 준다. 같은 이유로 receiver, reply도 같은 형태로 쓴다.
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
        // [읽는 법] 가장 큰 메시지 번호. 표가 비어 있으면 MAX가 NULL이므로 COALESCE로 0. 서버가 시작할 때 한 번 읽어 그 다음 번호부터 발급하므로, 재시작해도 번호가 겹치지 않는다.
        cmd.CommandText = "SELECT COALESCE(MAX(message_id), 0) FROM messages";
        return Convert.ToInt64(cmd.ExecuteScalar());
    }

    /// <summary>메시지 하나 찾기. 없으면 null.</summary>
    // [읽는 법] 답장·삭제·공감·수정에서 "이 번호의 메시지가 누구 것이고 어느 방(또는 1:1)이며 이미 삭제됐는가"를 확인하려고 쓴다. 방 번호·보낸 사람·받는 사람이 있어 호출한 쪽이 권한을 판단한다. "deleted_at IS NOT NULL" 같은 식은 결과가 0/1 숫자로 나와 == 1로 bool로 바꾼다.
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

    /// <summary>삭제되지 않은 메시지의 내용을 바꾸고 수정 시각을 기록한다. 바뀐 행이 있으면 true, 없으면(없는/삭제된 메시지) false.</summary>
    public bool MarkEdited(long messageId, string content)
    {
        using DbConnection conn = db.Open();
        using DbCommand cmd = conn.CreateCommand();
        // [읽는 법] 수정: 내용을 바꾸고 edited_at을 기록한다("수정됨" 표시용). 이미 삭제된 메시지는 건드리지 않도록 deleted_at IS NULL 조건이 붙어 있고, 바뀐 행이 있는지(ExecuteNonQuery() > 0)를 true/false로 돌려준다. 수정 가능 시간(5분)은 서비스에서 확인한다.
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
        // [읽는 법] 소프트 삭제: 행은 남기고 삭제 시각과 삭제한 사람만 기록한다. 이미 삭제된 것은 다시 바꾸지 않는다(처음 삭제한 사람이 보존됨). 내용(content)은 그대로라 감사·분쟁에 쓸 수 있고, 읽어서 보내는 쪽에서 가린다.
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
            // [읽는 법] ExecuteNonQuery는 "영향받은 행 수"를 돌려준다. DELETE가 0행이면 이 사람은 이 이모지를 아직 안 누른 것이므로 추가(INSERT IGNORE)하고, 1행이면 방금 취소한 것이다. 한 문장의 결과로 "눌렀는가"를 알아내 별도의 SELECT가 필요 없다.
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
        // [읽는 법] 같은 연결(conn)을 재사용해 그 메시지의 공감 현황을 다시 읽어 돌려준다. [messageId]는 원소 하나짜리 컬렉션 식이고, 공감이 하나도 없으면 Dictionary에 키가 없어 GetValueOrDefault가 null을 주므로 ?? []로 빈 목록을 돌려준다.
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
            // [읽는 법] IN (...) 목록을 만드는 안전한 방법: 값마다 @i0, @i1, ... 이름의 매개변수를 만들어 SQL에는 이름만 이어 붙이고 값은 따로 넘긴다. i++는 "현재 값을 쓰고 1 증가". 값은 숫자(long)지만 같은 습관을 지켜 항상 매개변수를 쓰게 한다.
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
            // [읽는 법] "메시지 번호 → 이모지별 목록" 만들기. 이미 있으면 그것을 쓰고(l), 없으면 빈 리스트를 Dictionary에 넣으면서 그 리스트를 바로 쓴다(대입식의 값 = 넣은 값). 아래 줄은 같은 이모지의 항목이 있으면 거기에, 없으면 새로 만들어(Add 도우미) 사원번호를 추가한다.
            List<ReactionInfo> list = result.TryGetValue(id, out var l) ? l : result[id] = [];
            ReactionInfo info = list.FirstOrDefault(x => x.Emoji == emoji) ?? Add(list, new ReactionInfo { Emoji = emoji });
            info.EmployeeNos.Add(r.GetString(2));
        }
        return result;

        static ReactionInfo Add(List<ReactionInfo> list, ReactionInfo item) { list.Add(item); return item; }
    }

    // ───────── 1:1 읽음 위치 ─────────

    /// <summary>1:1 읽음 위치 저장 (더 뒤로 갈 때만).</summary>
    // [읽는 법] 1:1 읽음 위치 = "읽는 사람(readerNo)이 상대(peerNo)와의 대화를 어디까지 읽었나". (읽는 사람, 상대)가 키라서 없으면 INSERT, 있으면 GREATEST로 "더 뒤의 시각만" 갱신한다(한 문장이라 원자적, 순서가 바뀌어 도착해도 뒤로 물러나지 않음).
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
        // [읽는 법] ExecuteScalar는 첫 행 첫 열의 값을 돌려준다. 행이 없거나 값이 NULL이면 DateTime이 아니므로 null이 되어 "읽은 기록 없음"을 뜻한다. 패턴 매칭 is DateTime t로 타입 확인과 변수 선언을 한 번에 한다.
        return cmd.ExecuteScalar() is DateTime t ? Db.AsUtc(t) : null;
    }

    /// <summary>
    /// 내가 아직 읽지 않은 1:1 메시지를 보낸 사람별로 센다 (로그인할 때 "안 읽은 1:1"을 알려 주려고).
    /// </summary>
    // [쿼리 읽는 법] ① 내가 받은 1:1(방 NULL, 받는 사람 = 나)만 ② 내가 읽은 위치(direct_reads, 없으면 아주 옛날 날짜) 이후 + 최근 30일 ③ 보낸 사람별로 묶어(GROUP BY) 개수·마지막 시각 ④ 최근순 상위 50명.
    //                  마지막 내용: 최근순으로 이어 붙인(GROUP_CONCAT) 문자열에서 첫 줄만(SUBSTRING_INDEX) 꺼내고, 삭제된 메시지는 빈 문자열로 바꿔 내용이 새지 않게 한다. 로그인 때 한 번 호출된다.
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
    // [읽는 법] 방 지난 대화 조회의 얇은 겹. 방 번호·"입장 시각 이후"(since) 조건을 SQL 조각(messageWhere/fileWhere)과 매개변수로 만들어 공통 함수 Load에 넘긴다. since가 null이면 DateTime.MinValue로 "처음부터". 조건 문자열은 코드 안의 상수라 사용자 입력이 섞이지 않는다.
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
        // [읽는 법] 1:1 대화는 "(내가 보내고 상대가 받음) 또는 (상대가 보내고 내가 받음)" 두 방향을 한 조건으로 묶는다. 표마다 열 이름 앞에 별칭(m., f.)이 필요해서 Replace로 접두어를 붙여 두 표용 조건을 만든다. idx_dm(보낸 사람, 받는 사람, 시각) 인덱스가 두 방향 모두에 쓰인다.
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
    // [전체 흐름] ① 메시지 표에서 최근 limit개(답장 원문·보낸 사람까지 JOIN) ② 그 메시지들의 공감을 IN 한 번으로 ③ 파일 표에서 최근 limit개 ④ 뒤집어(오래된 순) 합친 뒤 시간순 정렬, 최근 limit개만 남김.
    //             Action<DbCommand> addParams는 "호출한 쪽이 방/1:1에 맞는 매개변수를 넣어 주는 함수"를 받는 방식이다(콜백). 방과 1:1이 같은 Load를 공유해 중복을 없앤다.
    private List<HistoryRow> Load(string messageWhere, string fileWhere, DateTime before, int limit, string? beforeKey, Action<DbCommand> addParams)
    {
        // [읽는 법] 다음 쪽 조건. 키가 없으면 "시각 < before", 있으면 "시각이 더 이르거나, 같은 시각이면 고유 키 문자열이 더 작은 것". 고유 키는 메시지 "M:" + 번호(20자리로 0 채움, 문자열 비교가 숫자 순서와 같아지게), 파일 "F:" + 파일ID이다. 정렬 기준과 같아야 빠짐·겹침이 없다.
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
                    // [읽는 법] 삭제된 메시지는 내용을 읽어 오더라도 빈 문자열로 바꿔 보내고 Deleted = true만 알린다. 서버 DB에는 원본이 남지만 다른 사용자의 화면으로는 나가지 않는다(네트워크를 엿보거나 패킷을 조작해도 내용이 없다).
                    Body = deleted ? "" : r.GetString(3), // 삭제된 메시지 내용은 클라이언트에 보내지 않는다
                    Deleted = deleted,
                    Time = Db.AsUtc(r.GetDateTime(5)),
                    ReplyTo = reply,
                    Edited = Convert.ToInt32(r.GetValue(11)) == 1,
                }, null));
            }
        }

        // 공감 현황을 한 번에 붙인다 (메시지마다 따로 조회하면 50번 왕복)
        // [읽는 법] 읽은 메시지의 번호만 모아 공감을 한 번에 읽는다(N+1 방지). 돌려받은 (번호 → 공감 목록)을 각 메시지에 붙인다. messageRows.First(...)는 번호가 같은 행을 찾는 것이라 건수가 적어(limit 이하) 부담이 없다.
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
        // [읽는 법] SQL은 "최근 것부터"(DESC)로 읽어야 limit이 "가장 최근 N개"가 된다. 화면에는 오래된 것부터 보여야 하므로 메시지·파일 목록을 각각 뒤집은 다음 합친다.
        messageRows.Reverse();
        fileRows.Reverse();
        // [읽는 법] LINQ 한 줄 요약: 두 목록을 이어 붙이고(Concat) → 시각순 정렬(OrderBy) → 시각이 같으면 고유 키 문자열 순(ThenBy, 글자 코드 기준 Ordinal) → 마지막 limit개(TakeLast) → 리스트. OrderBy는 안정 정렬이라 같은 값끼리는 입력 순서가 유지된다.
        return messageRows.Concat(fileRows).OrderBy(x => x.Item.Time).ThenBy(HistorySortKey, StringComparer.Ordinal).TakeLast(limit).ToList();
    }

    // [읽는 법] 위의 SQL 커서에 쓴 키와 같은 모양("F:파일ID" / "M:0000...번호")을 C#에서도 만든다. 두 곳의 형식이 어긋나면 같은 시각의 항목 순서가 SQL과 C#에서 달라져 다음 쪽에 빠짐·중복이 생긴다. "D20"은 20자리로 0을 채우는 숫자 서식이다.
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
    // [쿼리 읽는 법] 삭제되지 않았고(deleted_at IS NULL) 내용에 검색어가 들어 있으며, (내가 멤버인 방의 메시지) 또는 (방이 없는 = 1:1 중 내가 보내거나 받은 것)만. 권한 조건을 SQL에 넣어 남의 대화가 결과에서 처음부터 빠지게 한다. 최근순 limit개.
    //                  상대 이름(pu)은 "내가 보낸 것이면 받는 사람, 아니면 보낸 사람"을 IF로 골라 JOIN한다. [알림] 방 검색에는 입장 시각 조건이 없다(노트의 한계 참고).
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
        // [읽는 법] 내 방이 하나도 없으면 IN () 문법 오류가 나므로 FALSE로 대체한다. 있으면 방 번호를 매개변수로 만든 IN 목록. 이 SQL 조각 역시 코드가 만든 것이라 사용자 입력은 매개변수로만 들어간다.
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
        // [읽는 법] 검색어를 이스케이프한 뒤 앞뒤에 %를 붙여 "포함" 검색으로 만든다. 값 전체를 매개변수로 넘기므로 검색어에 따옴표가 있어도 SQL 인젝션이 되지 않는다.
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
    // [읽는 법] 글자를 하나씩 보면서 %, _, \ 앞에 역슬래시를 붙인다. StringBuilder에 용량(s.Length)을 미리 잡아 두면 문자열을 매번 새로 만들지 않아 효율적이다.
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

    // [읽는 법] Db.cs의 AddParam 확장 메서드와 이름은 같지만 이쪽은 "값 없이" 매개변수 객체만 만들어 돌려준다(InsertMany가 값을 나중에 계속 바꿔 넣으려고). 같은 이름이지만 호출 모양이 다르다: AddParam(cmd, 이름)은 이 비공개 메서드, cmd.AddParam(이름, 값)은 확장 메서드다.
    private static DbParameter AddParam(DbCommand cmd, string name)
    {
        DbParameter p = cmd.CreateParameter();
        p.ParameterName = name;
        cmd.Parameters.Add(p);
        return p;
    }
}
