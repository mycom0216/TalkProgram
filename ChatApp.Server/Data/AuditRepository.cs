using System.Data.Common;
using ChatApp.Common.Models;

namespace ChatApp.Server.Data;

// ═══════════════════════════════════════════════════════════════════════════
// [파일 역할] audit_logs 테이블(감사 로그) 쓰기·검색.
// [스레드]   쓰기(InsertMany)는 AuditLog의 기록 스레드 1개만, 검색(Query)은 관리자의 수신 스레드에서 부른다.
//            메서드마다 연결을 따로 여니 동시에 불려도 안전하다.
// ═══════════════════════════════════════════════════════════════════════════
//
// ───────────────────────────────────────────────────────────────────────────
// [공부 노트 · 면접 대비]
//
// ■ 한 줄 요약
//   감사 로그(누가·언제·무엇을·누구에게·어느 IP에서)를 audit_logs 표에 "묶어서" 쓰고, 조건으로 검색해 읽는다. 쓰는 쪽은 AuditLog 스레드 하나, 읽는 쪽은 관리자 요청이다.
//
// ■ 이 파일에서 나오는 개념과 검색어
//   - 트랜잭션과 묶음(batch) INSERT ................... "database transaction batch insert performance"
//   - 선택 조건 패턴 (@x IS NULL OR col = @x) ......... "optional filter parameter IS NULL OR SQL"
//   - ORDER BY + LIMIT 페이지/최근순 조회 .............. "ORDER BY LIMIT recent rows"
//   - IsDBNull 확인 후 읽기 ............................ "ADO.NET DBNull IsDBNull GetString"
//   - 입력 길이 제한(Cut)과 DB 칸 길이 ................. "truncate to column length avoid insert failure"
//
// ■ 면접 예상 질문과 답
//   Q1. 여러 건을 하나의 트랜잭션으로 저장하는 이유는요?
//       A. 커밋마다 디스크에 확실히 기록하는 비용이 들어서, 한 건씩 커밋하면 느립니다. 묶어서 한 번에 커밋하면 비용을 나눠 훨씬 빠릅니다. 또 전부 성공하거나 전부 취소되므로 중간까지만 저장된 상태가 남지 않습니다.
//   Q2. 값을 칸 길이에 맞춰 미리 자르는(Cut) 이유는요?
//       A. 묶음 중 한 건이 칸 길이를 넘어 오류가 나면 트랜잭션 전체가 취소되어 다른 기록까지 잃습니다. 감사 로그는 "일부라도 남기는 것"이 중요하므로, 길이 제한을 넘는 값은 잘라서라도 저장합니다(상세 255자, IP 45자 = IPv6 최대 길이).
//   Q3. 감사 로그를 읽는 쪽과 쓰는 쪽을 분리한 이유는요?
//       A. 쓰기는 요청 처리 속도에 영향을 주지 않도록 AuditLog의 전용 스레드가 큐에서 꺼내 묶음으로 저장하고, 읽기(검색)는 관리자 요청이 직접 DB를 조회합니다. 메서드마다 연결을 따로 열기 때문에 동시에 불려도 안전합니다.
//   Q4. 검색 조건이 "있을 수도 없을 수도" 있는 쿼리를 어떻게 하나로 처리했나요?
//       A. (@no IS NULL OR actor_no = @no OR target_no = @no)처럼 "값이 없으면(NULL) 조건을 무시"하는 패턴으로 SQL 하나가 모든 조합을 처리합니다. 대신 데이터가 많아지면 DB가 인덱스를 잘 못 쓸 수 있어, 필요하면 조건에 따라 SQL을 나눠 만드는 방법으로 바꿉니다(단, 이때도 값은 매개변수로).
//   Q5. 최대 몇 건까지 조회하게 막았나요?
//       A. Math.Clamp(요청값, 1, AuditQuery.MaxLimit)로 범위를 강제합니다. 관리자가 실수로 수백만 건을 요청해 서버 메모리와 네트워크를 쓰는 일을 막습니다.
//   Q6. 한계는요? (솔직하게 답하기)
//       A. (1) 시간 범위 조건이 없어 아주 오래된 기록을 찾기 어렵다(보관 기간 정리 + 최근순만 지원). (2) 읽는 코드가 열 번호(0~5)로 접근해 SELECT 순서를 바꾸면 조용히 어긋난다. (3) 감사 로그가 쌓이는 표 자체를 사용자가 지울 수 없게 막는 권한 분리(DB 계정 분리)는 이 코드 밖의 운영 문제다.
//
// ■ 문제가 났을 때 어디를 볼까 (유지보수)
//   - 관리자 화면에 감사 로그가 비어 있다 ...... AuditLog 스레드가 큐를 비웠는지(AuditWriter), InsertMany의 DbException 로그
//   - 검색이 느리다 ........................... audit_logs의 인덱스(created_at, action, actor_no), OR 조건 때문에 인덱스를 못 타는지
//   - 일부 기록이 잘려 있다 ................... Cut의 최대 길이(상세 255자), 더 길게 필요하면 DB 칸과 함께 늘리기
// ───────────────────────────────────────────────────────────────────────────

/// <summary>감사 로그 한 건 (DB에 쓰기 전).</summary>
// [읽는 법] DB에 쓰기 전의 감사 로그 한 건(값 묶음). 시각은 기록 요청 시점에 정해져 큐에 들어가므로, 저장이 늦어져도 "일어난 시각"이 유지된다. 행위자·대상·상세·IP는 해당이 없으면 null이다(서버 시작 같은 항목).
public sealed record AuditRecord(DateTime Time, string? ActorNo, string Action, string? TargetNo, string? Detail, string? ClientIp);

/// <summary>audit_logs 표에 감사 로그를 저장하는 저장소.</summary>
public sealed class AuditRepository(Db db)
{
    /// <summary>여러 건을 트랜잭션 하나로 저장한다 (한 건씩 커밋하는 것보다 훨씬 빠르다).</summary>
    public void InsertMany(IReadOnlyList<AuditRecord> records)
    {
        if (records.Count == 0) return;
        using DbConnection conn = db.Open();
        // [읽는 법] 여기부터 tx.Commit()까지의 모든 INSERT가 한 덩어리다. Commit 전에 예외가 나면 using이 끝날 때 트랜잭션이 자동으로 롤백된다(저장 취소). 명령(cmd.Transaction = tx)에도 같은 트랜잭션을 연결해야 한 덩어리로 묶인다.
        using DbTransaction tx = conn.BeginTransaction();
        using DbCommand cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO audit_logs (actor_no, action, target_no, detail, client_ip, created_at)
            VALUES (@actor, @action, @target, @detail, @ip, @at)
            """;
        foreach (AuditRecord r in records)
        {
            // [읽는 법] 같은 명령 객체를 재사용하면서 건마다 매개변수만 비우고 새로 채운다. SQL 문장은 한 번 정해 두고 값만 바꿔 반복 실행하므로, 문장을 매번 새로 만드는 것보다 효율적이다.
            cmd.Parameters.Clear();
            // [왜] 칸 길이에 맞게 자른다. 너무 긴 값 하나 때문에 묶음 전체가 실패하면 다른 기록까지 잃는다.
            // [읽는 법] 칸 길이 상한: 사원번호 20, 동작 30, 대상 20, 상세 255, IP 45(IPv6 텍스트의 최대 길이). 이 숫자는 audit_logs 표의 칸 크기와 맞아야 한다 — 표를 바꾸면 여기도 같이 바꾼다.
            cmd.AddParam("@actor", Cut(r.ActorNo, 20));
            cmd.AddParam("@action", Cut(r.Action, 30));
            cmd.AddParam("@target", Cut(r.TargetNo, 20));
            cmd.AddParam("@detail", Cut(r.Detail, 255));
            cmd.AddParam("@ip", Cut(r.ClientIp, 45));
            cmd.AddParam("@at", r.Time);
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
    }

    /// <summary>최근 기록부터 검색.</summary>
    public List<AuditEntry> Query(AuditQuery q)
    {
        using DbConnection conn = db.Open();
        using DbCommand cmd = conn.CreateCommand();
        // [읽는 법] "선택 조건" 패턴. 사원번호를 주지 않으면(NULL) 첫 항이 참이라 조건이 사라지고, 주면 "행위자이거나 대상인" 기록을 모두 찾는다. 아래 줄의 동작 조건도 같은 방식이다.
        // [읽는 법] 최근 것부터. 시각이 같은 기록이 여러 개일 때 순서가 흔들리지 않도록 기본 키(log_id)를 두 번째 정렬 기준으로 둔다. Program.cs의 /audit 명령은 이 결과를 뒤집어서 오래된 것 → 최근 순으로 출력한다.
        cmd.CommandText = """
            SELECT created_at, actor_no, action, target_no, detail, client_ip
            FROM audit_logs
            WHERE (@no IS NULL OR actor_no = @no OR target_no = @no)
              AND (@action IS NULL OR action = @action)
            ORDER BY created_at DESC, log_id DESC
            LIMIT @limit
            """;
        cmd.AddParam("@no", string.IsNullOrWhiteSpace(q.EmployeeNo) ? null : q.EmployeeNo.Trim());
        cmd.AddParam("@action", string.IsNullOrWhiteSpace(q.Action) ? null : q.Action.Trim());
        cmd.AddParam("@limit", Math.Clamp(q.Limit, 1, AuditQuery.MaxLimit));

        var list = new List<AuditEntry>();
        using DbDataReader r = cmd.ExecuteReader();
        while (r.Read())
        {
            list.Add(new AuditEntry
            {
                Time = Db.AsUtc(r.GetDateTime(0)),
                // [읽는 법] NULL이 들어 있을 수 있는 칸은 IsDBNull로 먼저 확인한다. NULL인 칸에 GetString을 바로 부르면 예외가 난다. 숫자 1은 SELECT 목록의 두 번째 열(0부터 시작)을 뜻한다.
                ActorNo = r.IsDBNull(1) ? null : r.GetString(1),
                Action = r.GetString(2),
                TargetNo = r.IsDBNull(3) ? null : r.GetString(3),
                Detail = r.IsDBNull(4) ? null : r.GetString(4),
                ClientIp = r.IsDBNull(5) ? null : r.GetString(5),
            });
        }
        return list;
    }

    // [읽는 법] null이거나 이미 짧으면 그대로, 길면 앞의 max글자만. s[..max]는 범위 연산자(앞에서 max개). 주의: 글자 단위(UTF-16)로 자르므로 이모지 같은 두 칸짜리 글자가 중간에 잘릴 수 있다 — 감사 로그 상세에는 영향이 거의 없다.
    private static string? Cut(string? s, int max) => s is null || s.Length <= max ? s : s[..max];
}
