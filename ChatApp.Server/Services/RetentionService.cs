using System.Data.Common;
using ChatApp.Server.Data;
using ChatApp.Server.Logging;

namespace ChatApp.Server.Services;

// ═══════════════════════════════════════════════════════════════════════════
// [파일 역할] 보관 기간 정리: 오래된 대화·파일·감사 로그를 하루 한 번 지운다.
// [왜] 지우지 않으면 DB와 디스크가 끝없이 커진다(파일은 하나에 최대 3GB). 또 회사는 "대화는 1년, 감사 로그는 2년"처럼
//      보관 기간 규정이 있는 경우가 많아, 그 기간이 지난 개인 대화를 계속 들고 있는 것도 문제가 될 수 있다.
//      기간은 appsettings.json(MessageRetentionDays 등)으로 정하고, 0이면 지우지 않는다.
// [스레드] 전용 "Retention" 스레드 하나. 서버 시작 1분 뒤 첫 정리, 그 뒤로 24시간마다.
//          한 번에 5000행씩 나눠 지운다: 수십만 행을 한 문장으로 지우면 그동안 테이블이 잠겨 채팅 저장이 멈춘다.
// ═══════════════════════════════════════════════════════════════════════════
public sealed class RetentionService(Db db, FileService files, ServerConfig config, ServerLogger log) : IDisposable
{
    private const int BatchSize = 5000;
    private readonly CancellationTokenSource _stop = new();
    private Thread? _thread;

    public void Start()
    {
        _thread = new Thread(Run) { IsBackground = true, Name = "Retention" };
        _thread.Start();
    }

    private void Run()
    {
        // 시작하자마자 돌리지 않는다 (서버 시작 직후 접속이 몰리는 시간을 피함)
        if (_stop.Token.WaitHandle.WaitOne(TimeSpan.FromMinutes(1))) return;
        do
        {
            try { RunOnce(); }
            catch (Exception ex) { log.Error($"보관 기간 정리 실패: {ex.Message}"); } // 이 스레드가 죽으면 서버 전체가 죽는다
        }
        while (!_stop.Token.WaitHandle.WaitOne(TimeSpan.FromHours(24)));
    }

    /// <summary>한 번 정리한다. (서버 콘솔 /cleanup 명령과 테스트에서도 부른다)</summary>
    public void RunOnce()
    {
        // ① 서버가 꺼지며 남은 "올리는 중" 행: 하루가 지났으면 실패한 업로드다 (임시 파일은 FileService가 시작할 때 지운다)
        int stale = Execute("DELETE FROM files WHERE status = 'UPLOADING' AND created_at < NOW() - INTERVAL 1 DAY LIMIT @n");
        if (stale > 0) log.Info($"정리: 끝나지 않은 업로드 기록 {stale}건");

        if (config.MessageRetentionDays > 0)
        {
            int n = DeleteInBatches("DELETE FROM messages WHERE sent_at < NOW() - INTERVAL @days DAY LIMIT @n", config.MessageRetentionDays);
            if (n > 0) log.Info($"정리: {config.MessageRetentionDays}일 지난 대화 {n}건");
        }
        if (config.AuditRetentionDays > 0)
        {
            int n = DeleteInBatches("DELETE FROM audit_logs WHERE created_at < NOW() - INTERVAL @days DAY LIMIT @n", config.AuditRetentionDays);
            if (n > 0) log.Info($"정리: {config.AuditRetentionDays}일 지난 감사 로그 {n}건");
        }
        if (config.FileRetentionDays > 0)
        {
            int n = DeleteFiles("SELECT file_id, stored_path FROM files WHERE status = 'DONE' AND created_at < NOW() - INTERVAL @days DAY LIMIT 500",
                                config.FileRetentionDays);
            if (n > 0) log.Info($"정리: {config.FileRetentionDays}일 지난 파일 {n}개 (디스크에서도 삭제)");
        }
        if (config.ClosedRoomFileDays >= 0)
        {
            // 삭제된 방·닫힌 방의 파일: 아무도 받을 수 없으므로 원본을 지운다 (대화 기록은 보관 기간까지 남김)
            int n = DeleteFiles("SELECT f.file_id, f.stored_path FROM files f JOIN rooms r ON r.room_id = f.room_id " +
                                "WHERE f.status = 'DONE' AND r.closed_at IS NOT NULL AND r.closed_at <= NOW() - INTERVAL @days DAY LIMIT 500",
                                config.ClosedRoomFileDays);
            if (n > 0) log.Info($"정리: 삭제·닫힌 방의 파일 {n}개 (디스크에서도 삭제)");
        }
    }

    /// <summary>sql로 고른 파일(file_id, stored_path를 500개씩): 디스크의 파일(+미리보기)을 지우고 DB 행도 지운다.</summary>
    private int DeleteFiles(string sql, int days)
    {
        int total = 0;
        while (!_stop.IsCancellationRequested)
        {
            var batch = new List<(string Id, string Path)>();
            using (DbConnection conn = db.Open())
            using (DbCommand cmd = conn.CreateCommand())
            {
                cmd.CommandText = sql;
                cmd.AddParam("@days", days);
                using DbDataReader r = cmd.ExecuteReader();
                while (r.Read()) batch.Add((r.GetString(0), r.GetString(1)));
            }
            if (batch.Count == 0) break;

            foreach (var (id, path) in batch)
            {
                files.DeleteStoredFile(path); // 디스크 먼저 (실패해도 DB 행은 지워 다시 시도하지 않게)
                using DbConnection conn = db.Open();
                using DbCommand cmd = conn.CreateCommand();
                cmd.CommandText = "DELETE FROM files WHERE file_id = @id";
                cmd.AddParam("@id", id);
                cmd.ExecuteNonQuery();
            }
            total += batch.Count;
        }
        return total;
    }

    private int DeleteInBatches(string sql, int days)
    {
        int total = 0;
        while (!_stop.IsCancellationRequested)
        {
            int n = Execute(sql, days);
            total += n;
            if (n < BatchSize) break;
            Thread.Sleep(200); // 다른 작업(채팅 저장)이 끼어들 틈을 준다
        }
        return total;
    }

    private int Execute(string sql, int days = 0)
    {
        using DbConnection conn = db.Open();
        using DbCommand cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.AddParam("@n", BatchSize);
        cmd.AddParam("@days", days);
        return cmd.ExecuteNonQuery();
    }

    public void Dispose()
    {
        _stop.Cancel();
        _thread?.Join(TimeSpan.FromSeconds(2));
    }
}
