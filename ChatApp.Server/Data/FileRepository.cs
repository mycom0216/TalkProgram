using System.Data.Common;
using ChatApp.Common.Models;

namespace ChatApp.Server.Data;

//
// ───────────────────────────────────────────────────────────────────────────
// [공부 노트 · 면접 대비]
//
// ■ 한 줄 요약
//   files 표(올린 파일의 정보) 담당: 올리기 시작할 때 UPLOADING으로 넣고, 다 받으면 DONE으로 바꾸며, 조회·삭제·"방 파일 보관함 목록"을 제공한다. 실제 파일 내용은 디스크에 있고 DB에는 정보만 있다.
//
// ■ 한 행의 수명
//   InsertUploading(UPLOADING) → [조각 수신] → MarkDone(DONE, 시각을 완료 시각으로 갱신) → (보관 기간이 지나면) RetentionService가 Delete
//   실패·취소 시에는 FileService가 Delete로 지운다.
//
// ■ 이 파일에서 나오는 개념과 검색어
//   - 상태 컬럼(UPLOADING → DONE)으로 진행 표시 ......... "status column state machine database"
//   - 키셋(keyset) 페이지네이션 vs OFFSET ............... "keyset pagination vs offset"
//   - "한 개 더 읽어서" 다음 쪽 유무 판단(pageSize + 1) .. "fetch N+1 rows to detect has more"
//   - LEFT JOIN으로 권한 조건을 SQL에 넣기 ............... "authorization in SQL query join membership"
//   - record와 명명된 인자 ............................... "C# record named arguments"
//   - SqlState(오류 코드) ................................ "SQLSTATE 42S02 table not found"
//
// ■ 면접 예상 질문과 답
//   Q1. 올리기를 시작할 때 DB에 먼저 넣는(UPLOADING) 이유는요?
//       A. 3GB를 다 받은 뒤에야 DB 문제(표 없음, 연결 오류)를 알면 시간과 디스크가 낭비됩니다. 시작할 때 넣으면 문제를 바로 알려 줄 수 있고, 상태가 DONE이 아닌 파일은 내려받기·목록 대상에서 제외되므로 반쯤 받은 파일이 노출되지 않습니다.
//   Q2. MarkDone에서 시각을 DB의 CURRENT_TIMESTAMP가 아니라 C# 서버 시각으로 쓰는 이유는요?
//       A. 채팅 메시지의 시각은 C# 서버가 만들기 때문에, 파일 시각도 같은 시계를 써야 대화 기록과 시간순으로 합칠 때 순서가 뒤섞이지 않습니다. DB와 서버의 시간대·시계가 다를 때를 대비한 선택입니다. 밀리초 미만을 잘라 DB의 DATETIME(3) 정밀도와 맞춥니다.
//   Q3. 파일 보관함 목록에서 OFFSET 대신 "이전 항목 기준"으로 페이지를 나누는 이유는요?
//       A. OFFSET은 뒤로 갈수록 앞의 행을 모두 읽고 버려서 느려지고, 보는 도중 새 파일이 올라오면 항목이 겹치거나 빠집니다. 마지막으로 본 (시각, 파일 ID)보다 "이전" 조건으로 읽으면 항상 일정한 속도이고 중복·누락이 없습니다. 시각이 같은 항목은 파일 ID로 순서를 가립니다.
//   Q4. pageSize + 1개를 읽는 이유는요?
//       A. 다음 쪽이 있는지 알려면 COUNT 쿼리를 따로 날리거나 한 개 더 읽어 보면 됩니다. 51개가 오면 "더 있음(HasMore)"이고 마지막 하나는 버립니다. 추가 쿼리 없이 한 번에 끝납니다.
//   Q5. 권한 확인을 SQL 안에서도 하는 이유는요? 메모리에서 이미 확인하지 않나요?
//       A. 방어를 두 겹으로 하기 위해서입니다. 서비스 코드(메모리)에서 "방에 참여 중인가"를 확인하고, 쿼리는 room_members를 조인해 "멤버이고 멤버가 된 시각 이후의 파일만" 돌려줍니다. 한 쪽에 버그가 있어도 다른 쪽이 막습니다. 기본 방은 멤버 표가 없으므로 예외로 허용합니다.
//   Q6. 파일 종류(FileKind)를 문자열로 저장하고 읽을 때 Enum.TryParse를 쓰는 이유는요?
//       A. 숫자로 저장하면 enum 순서를 바꿀 때 옛 데이터의 뜻이 달라집니다. 이름(IMAGE, FILE 등)으로 저장하면 안전하고, 읽을 때 알 수 없는 값이 들어 있으면 TryParse가 실패해 FileKind.File로 대체되어 예외로 목록이 통째로 실패하지 않습니다.
//   Q7. 한계는요? (솔직하게 답하기)
//       A. (1) 파일 이름을 SQL 검색하는 기능이 없다. (2) 열 번호로 읽는 방식이라 SELECT 순서가 바뀌면 조용히 어긋난다. (3) Delete는 행만 지우며 디스크 파일 삭제는 호출한 쪽의 책임이라, 둘이 어긋나면 고아 파일이 생길 수 있다(RetentionService는 디스크를 먼저 지워 이를 줄인다).
//
// ■ 문제가 났을 때 어디를 볼까 (유지보수)
//   - "files 테이블이 없습니다" ................ db/migrate_002_files.sql 실행(SqlState 42S02)
//   - 파일 보관함에 안 보인다 .................. status가 DONE인지, 멤버가 된 시각(joined_at) 이후의 파일인지, room_id 일치
//   - 보관함에서 같은 파일이 두 번 나온다 ...... 정렬 기준(created_at, file_id)과 "이전" 조건이 모두 같은 방향인지
//   - 시각 순서가 이상하다 ..................... MarkDone의 시각 소스(서버 시계), UTC 처리(AsUtc)
// ───────────────────────────────────────────────────────────────────────────

/// <summary>files 테이블 한 행.</summary>
// [읽는 법] files 표의 한 행. RoomId와 ReceiverNo 중 하나만 값이 있다(방 파일 / 1:1 파일). StoredPath는 서버 내부 저장 위치(년월/파일ID)이며 클라이언트에는 보내지 않는다. Done은 DB의 status가 "DONE"인지를 true/false로 바꾼 값이다.
public sealed record FileRecord(
    string FileId,
    int? RoomId,
    string SenderNo,
    string? ReceiverNo,
    string FileName,
    long Size,
    FileKind Kind,
    string StoredPath,
    bool Done);

/// <summary>
/// files 테이블. 올리기를 시작할 때 UPLOADING으로 넣고, 다 받으면 DONE으로 바꾼다.
/// (시작할 때 넣어 두면 DB 문제를 큰 파일을 다 받기 "전에" 알 수 있다)
/// </summary>
public sealed class FileRepository(Db db)
{
    /// <summary>테이블이 없을 때 MariaDB가 주는 SQLSTATE.</summary>
    public const string TableMissingState = "42S02";

    /// <summary>서버 시작 시 files 테이블이 있는지 확인. 없으면 false.</summary>
    public bool TableExists()
    {
        try
        {
            using DbConnection conn = db.Open();
            using DbCommand cmd = conn.CreateCommand();
            // [읽는 법] 행은 한 개도 읽지 않으면서(LIMIT 0) 표가 있는지만 확인한다. 표가 없으면 SQLSTATE 42S02 오류가 나므로 예외 필터로 잡아 false로 바꾼다. 같은 코드(TableMissingState)를 FileService가 안내 문구를 고르는 데도 쓴다.
            cmd.CommandText = "SELECT 1 FROM files LIMIT 0";
            cmd.ExecuteNonQuery();
            return true;
        }
        catch (DbException ex) when (ex.SqlState == TableMissingState)
        {
            return false;
        }
    }

    /// <summary>files 표에 올리기 시작한 파일을 UPLOADING 상태로 한 줄 넣는다.</summary>
    public void InsertUploading(FileRecord f)
    {
        using DbConnection conn = db.Open();
        using DbCommand cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO files (file_id, room_id, sender_no, receiver_no, file_name, file_size, file_kind, stored_path, status)
            VALUES (@id, @room, @sender, @receiver, @name, @size, @kind, @path, 'UPLOADING')
            """;
        cmd.AddParam("@id", f.FileId);
        cmd.AddParam("@room", f.RoomId);
        cmd.AddParam("@sender", f.SenderNo);
        cmd.AddParam("@receiver", f.ReceiverNo);
        cmd.AddParam("@name", f.FileName);
        cmd.AddParam("@size", f.Size);
        // [읽는 법] enum을 이름 문자열(대문자)로 저장한다. 읽을 때는 Enum.TryParse(ignoreCase: true)로 되돌린다. 순서가 아니라 이름에 의존하므로 enum 정의 순서를 바꿔도 옛 데이터가 안전하다.
        cmd.AddParam("@kind", f.Kind.ToString().ToUpperInvariant());
        cmd.AddParam("@path", f.StoredPath);
        cmd.ExecuteNonQuery();
    }

    /// <summary>파일을 DONE(올리기 완료)으로 바꾸고 created_at을 지금(UTC)으로 갱신한다.</summary>
    public void MarkDone(string fileId)
    {
        using DbConnection conn = db.Open();
        using DbCommand cmd = conn.CreateCommand();
        // created_at을 "올리기가 끝난 시각"으로 바꾼다. 대화 기록에서 파일 메시지가 실제로 올라온 위치에 보이도록.
        // 시각은 DB 서버 시계(CURRENT_TIMESTAMP)가 아니라 채팅 메시지와 같은 C# 서버 시계를 쓴다.
        // (DB와 채팅 서버의 시간대가 다르면 기록을 시간순으로 합칠 때 순서가 뒤섞이기 때문)
        // [읽는 법] 완료 표시와 동시에 created_at을 "올리기가 끝난 시각"으로 덮어쓴다. 대화 기록에서 파일 말풍선이 시작 시각이 아니라 실제 올라온 위치에 보이게 하려는 것이다.
        cmd.CommandText = "UPDATE files SET status = 'DONE', created_at = @now WHERE file_id = @id";
        DateTime now = DateTime.UtcNow;
        cmd.AddParam("@now", new DateTime(now.Ticks - now.Ticks % TimeSpan.TicksPerMillisecond));
        cmd.AddParam("@id", fileId);
        cmd.ExecuteNonQuery();
    }

    /// <summary>files 표에서 해당 파일 기록을 지운다 (디스크 파일은 지우지 않는다).</summary>
    public void Delete(string fileId)
    {
        using DbConnection conn = db.Open();
        using DbCommand cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM files WHERE file_id = @id";
        cmd.AddParam("@id", fileId);
        cmd.ExecuteNonQuery();
    }

    /// <summary>파일 번호로 파일 기록을 찾는다. 없으면 null (Done은 상태가 DONE일 때 true).</summary>
    public FileRecord? Find(string fileId)
    {
        using DbConnection conn = db.Open();
        using DbCommand cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT file_id, room_id, sender_no, receiver_no, file_name, file_size, file_kind, stored_path, status
            FROM files WHERE file_id = @id
            """;
        cmd.AddParam("@id", fileId);

        using DbDataReader r = cmd.ExecuteReader();
        if (!r.Read()) return null;
        // [읽는 법] 열 번호는 SELECT 목록 순서(0부터)와 같아야 한다. 이름 붙여 쓴(FileId: ...) 인자 덕분에 어느 값이 어느 속성인지 한눈에 보인다. NULL 가능한 칸(방, 받는 사람)만 IsDBNull로 확인하고, 숫자는 Convert로 안전하게 변환한다.
        return new FileRecord(
            FileId: r.GetString(0),
            RoomId: r.IsDBNull(1) ? null : Convert.ToInt32(r.GetValue(1)),
            SenderNo: r.GetString(2),
            ReceiverNo: r.IsDBNull(3) ? null : r.GetString(3),
            FileName: r.GetString(4),
            Size: Convert.ToInt64(r.GetValue(5)),
            Kind: Enum.TryParse(r.GetString(6), ignoreCase: true, out FileKind kind) ? kind : FileKind.File,
            StoredPath: r.GetString(7),
            Done: r.GetString(8) == "DONE");
    }

    /// <summary>방 파일 보관함 페이지. 조회 SQL에서 방 멤버십과 가입 시각을 함께 검증한다.</summary>
    public (List<SharedFile> Files, bool HasMore) LoadRoomGallery(string employeeNo, int roomId, DateTime? before,
        string? beforeFileId, int pageSize = 50)
    {
        using DbConnection conn = db.Open();
        using DbCommand cmd = conn.CreateCommand();
        // [읽는 법] 요청한 사람이 이 방의 멤버이면 rm 행이 붙고, 멤버가 아니면 rm이 NULL이다. 아래 WHERE의 "rm.employee_no IS NOT NULL AND f.created_at >= rm.joined_at"가 "멤버이고, 들어온 뒤에 올라온 파일만"이라는 규칙이다. 기본 방(@lobby)은 멤버 표가 없으므로 따로 허용한다.
        // [읽는 법] 키셋 페이지네이션 조건. 첫 쪽이면(@before가 NULL) 조건 없음, 다음 쪽이면 "마지막으로 본 것보다 이전": 시각이 더 이르거나, 시각이 같으면 파일 ID가 더 작은 것. 정렬(created_at DESC, file_id DESC)과 같은 기준이어야 빠짐·겹침이 없다.
        cmd.CommandText = """
            SELECT f.file_id, f.file_name, f.file_size, f.file_kind, f.room_id, f.sender_no,
                   COALESCE(CONCAT(u.name, ' ', u.position), f.sender_no), f.created_at
            FROM files f
            LEFT JOIN room_members rm ON rm.room_id = f.room_id AND rm.employee_no = @employee
            LEFT JOIN users u ON u.employee_no = f.sender_no
            WHERE f.room_id = @room AND f.status = 'DONE'
              AND (f.room_id = @lobby OR (rm.employee_no IS NOT NULL AND f.created_at >= rm.joined_at))
              AND (@before IS NULL OR f.created_at < @before OR (f.created_at = @before AND f.file_id < @before_id))
            ORDER BY f.created_at DESC, f.file_id DESC
            LIMIT @limit
            """;
        cmd.AddParam("@employee", employeeNo);
        cmd.AddParam("@room", roomId);
        cmd.AddParam("@lobby", RoomInfo.LobbyId);
        cmd.AddParam("@before", before);
        cmd.AddParam("@before_id", beforeFileId);
        // [읽는 법] 한 개 더 읽어 "다음 쪽이 있는지" 판단한다. 아래에서 개수가 pageSize를 넘으면 hasMore = true로 하고 마지막 한 개는 버린다. 별도의 COUNT 쿼리가 필요 없다.
        cmd.AddParam("@limit", pageSize + 1);
        var files = new List<SharedFile>(pageSize + 1);
        using (DbDataReader r = cmd.ExecuteReader())
        {
            while (r.Read())
            {
                files.Add(new SharedFile
                {
                    FileId = r.GetString(0), FileName = r.GetString(1), Size = Convert.ToInt64(r.GetValue(2)),
                    Kind = Enum.TryParse(r.GetString(3), true, out FileKind kind) ? kind : FileKind.File,
                    RoomId = Convert.ToInt32(r.GetValue(4)), SenderNo = r.GetString(5), SenderName = r.GetString(6),
                    SentAt = Db.AsUtc(r.GetDateTime(7)),
                });
            }
        }
        bool hasMore = files.Count > pageSize;
        if (hasMore) files.RemoveAt(files.Count - 1);
        return (files, hasMore);
    }
}
