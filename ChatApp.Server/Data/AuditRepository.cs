using System.Data.Common;
using ChatApp.Common.Models;

namespace ChatApp.Server.Data;

// ═══════════════════════════════════════════════════════════════════════════
// [파일 역할] audit_logs 테이블(감사 로그) 쓰기·검색.
// [스레드]   쓰기(InsertMany)는 AuditLog의 기록 스레드 1개만, 검색(Query)은 관리자의 수신 스레드에서 부른다.
//            메서드마다 연결을 따로 여니 동시에 불려도 안전하다.
// ═══════════════════════════════════════════════════════════════════════════

/// <summary>감사 로그 한 건 (DB에 쓰기 전).</summary>
public sealed record AuditRecord(DateTime Time, string? ActorNo, string Action, string? TargetNo, string? Detail, string? ClientIp);

public sealed class AuditRepository(Db db)
{
    /// <summary>여러 건을 트랜잭션 하나로 저장한다 (한 건씩 커밋하는 것보다 훨씬 빠르다).</summary>
    public void InsertMany(IReadOnlyList<AuditRecord> records)
    {
        if (records.Count == 0) return;
        using DbConnection conn = db.Open();
        using DbTransaction tx = conn.BeginTransaction();
        using DbCommand cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO audit_logs (actor_no, action, target_no, detail, client_ip, created_at)
            VALUES (@actor, @action, @target, @detail, @ip, @at)
            """;
        foreach (AuditRecord r in records)
        {
            cmd.Parameters.Clear();
            // [왜] 칸 길이에 맞게 자른다. 너무 긴 값 하나 때문에 묶음 전체가 실패하면 다른 기록까지 잃는다.
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
                ActorNo = r.IsDBNull(1) ? null : r.GetString(1),
                Action = r.GetString(2),
                TargetNo = r.IsDBNull(3) ? null : r.GetString(3),
                Detail = r.IsDBNull(4) ? null : r.GetString(4),
                ClientIp = r.IsDBNull(5) ? null : r.GetString(5),
            });
        }
        return list;
    }

    private static string? Cut(string? s, int max) => s is null || s.Length <= max ? s : s[..max];
}
