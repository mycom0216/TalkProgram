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
//
// ───────────────────────────────────────────────────────────────────────────
// [공부 노트 · 면접 대비]
//
// ■ 한 줄 요약
//   하루에 한 번(서버 시작 1분 뒤부터 24시간마다) 오래된 대화·감사 로그·파일을 지우는 청소 전담 스레드. 기간은 설정으로 정하고 0이면 지우지 않는다.
//
// ■ 기본값 (ServerConfig)
//   대화 365일, 파일 180일, 감사 로그 730일, 닫힌 방의 파일 0일(= 닫히면 바로 정리 대상, 음수면 끔). 끝나지 않은 업로드 기록은 하루 지나면 실패로 보고 지운다.
//
// ■ 핵심 아이디어: "나눠서 지우기"
//   수십만 행을 DELETE 한 문장으로 지우면 그동안 테이블이 잠겨 채팅 저장이 멈춘다. 그래서 5,000행씩 LIMIT로 지우고, 한 번 지울 때마다 0.2초 쉬어 채팅 저장이 끼어들 틈을 준다.
//   파일은 디스크와 DB 두 군데이므로 "디스크 먼저, DB 행은 그다음"으로 지운다(디스크 삭제가 실패해도 DB 행은 지워 같은 파일을 영원히 재시도하지 않음).
//
// ■ 이 파일에서 나오는 개념과 검색어
//   - 배치(batch) 삭제, 락 시간 줄이기 ..................... "batch delete LIMIT avoid long lock"
//   - 전용 백그라운드 스레드, 주기 작업 ..................... "background thread periodic task WaitOne timeout"
//   - CancellationToken.WaitHandle.WaitOne(시간) ........... "interruptible sleep CancellationToken WaitHandle"
//   - do { } while (조건) ................................... "C# do while loop"
//   - 데이터 보관 기간 정책(retention policy) .............. "data retention policy GDPR log retention"
//
// ■ 면접 예상 질문과 답
//   Q1. 오래된 데이터를 한 번에 DELETE하지 않고 나눠서 지우는 이유는요?
//       A. 큰 DELETE 하나는 오래 걸리고 그동안 해당 행들이 잠겨, 같은 테이블에 쓰는 채팅 저장이 기다리게 됩니다. LIMIT 5,000으로 나눠 지우고 사이에 0.2초 쉬면 한 번에 잠기는 시간이 짧아져 서비스에 영향이 거의 없습니다. 지운 개수가 한 묶음보다 적으면 더 지울 게 없다는 뜻이라 반복을 끝냅니다.
//   Q2. Thread.Sleep 대신 WaitOne(24시간)을 쓰는 이유는요?
//       A. Sleep은 중간에 깨울 수 없지만, 취소 토큰의 WaitHandle.WaitOne은 "정해진 시간 또는 서버 종료 신호 중 먼저 오는 것"에서 깨어납니다. 서버를 끌 때 24시간을 기다리지 않고 바로 스레드가 끝납니다(Dispose가 Cancel 후 Join 2초).
//   Q3. 스레드 안에서 모든 예외를 잡는 이유는요?
//       A. 직접 만든 스레드에서 잡히지 않은 예외가 나면 프로세스 전체가 종료됩니다. 정리 작업 한 번의 실패가 서버를 죽이면 안 되므로 로그만 남기고, 다음 주기(24시간 뒤)에 다시 시도합니다.
//   Q4. 파일 삭제 순서를 "디스크 먼저, DB 나중"으로 한 이유는요?
//       A. DB 행을 먼저 지우고 디스크 삭제가 실패하면 파일이 DB에서는 사라졌는데 디스크에는 남아 영영 못 찾는 "고아 파일"이 생깁니다. 디스크를 먼저 지우면 최악의 경우에도 이미 없는 파일의 DB 행이 남을 뿐이고, 이 코드는 그 경우에도 DB 행을 지워 정리합니다.
//   Q5. 서버 시작 1분 뒤에 첫 정리를 하는 이유는요?
//       A. 서버를 켠 직후에는 클라이언트가 한꺼번에 재접속해 부하가 몰립니다. 그 시간을 피해서 정리를 시작합니다.
//   Q6. 정리를 서버 점검 시간에 수동으로 돌릴 수 있나요?
//       A. 네. RunOnce가 public이라 서버 콘솔의 /cleanup 명령으로 바로 한 번 실행할 수 있고, 테스트에서도 부릅니다.
//   Q7. 한계는요? (솔직하게 답하기)
//       A. (1) 시간 기준(NOW())이 DB 서버 시계라, 서버와 DB의 시계가 다르면 기준이 어긋난다(시간대 설정 확인). (2) 대화 삭제는 "복구 불가"라서 실수로 짧은 기간을 설정하면 되돌릴 수 없다 — 운영 전 백업·설정 확인이 필요하다. (3) 파일 삭제는 한 번에 500개씩 반복하므로 매우 많으면 오래 걸리고, 그 사이 서버 종료 신호가 오면 중간에 멈춘다(다음 날 이어서 정리됨).
//
// ■ 문제가 났을 때 어디를 볼까 (유지보수)
//   - 디스크가 계속 찬다 ....................... FileRetentionDays(0이면 안 지움), 로그의 "정리: ... 파일 N개", DeleteFiles의 DB 조건
//   - 오래된 대화가 안 지워진다 ................ MessageRetentionDays가 0인지, 로그 "보관 기간 정리 실패" 메시지, DB 연결 오류
//   - 정리 중에 채팅이 느려진다 ................ BatchSize(5000)와 쉬는 시간(200ms)을 줄여서 한 번에 잠기는 시간을 줄여 본다
//   - 지금 당장 정리하고 싶다 .................. 서버 콘솔에서 /cleanup
// ───────────────────────────────────────────────────────────────────────────
/// <summary>보관 기간이 지난 대화·파일·감사 로그를 전용 스레드가 하루에 한 번 지우는 서비스.</summary>
// [읽는 법] 필요한 부품: db(DB 연결), files(디스크 파일 삭제), config(보관 기간 설정), log. ": IDisposable"은 서버를 끌 때 Dispose()로 스레드를 정리한다는 약속이다.
public sealed class RetentionService(Db db, FileService files, ServerConfig config, ServerLogger log) : IDisposable
{
    private const int BatchSize = 5000;
    private readonly CancellationTokenSource _stop = new();
    private Thread? _thread;

    /// <summary>정리 스레드를 시작한다 (시작 1분 뒤 첫 정리, 이후 24시간마다).</summary>
    public void Start()
    {
        // [읽는 법] 전용 스레드 하나를 만든다. IsBackground = true는 서버가 끝나면 이 스레드도 같이 끝나도록(프로세스를 붙잡지 않도록) 하는 설정이고, Name은 디버거·로그에서 알아보기 위한 이름이다.
        _thread = new Thread(Run) { IsBackground = true, Name = "Retention" };
        _thread.Start();
    }

    private void Run()
    {
        // 시작하자마자 돌리지 않는다 (서버 시작 직후 접속이 몰리는 시간을 피함)
        // [읽는 법] WaitOne(시간)은 "그 시간 동안 기다리되, 종료 신호가 오면 즉시 true로 깨어남"이다. true면 서버가 꺼지는 중이므로 바로 return한다. 아니면(false) 1분이 지난 것이니 첫 정리를 시작한다.
        if (_stop.Token.WaitHandle.WaitOne(TimeSpan.FromMinutes(1))) return;
        do
        {
            try { RunOnce(); }
            catch (Exception ex) { log.Error($"보관 기간 정리 실패: {ex.Message}"); } // 이 스레드가 죽으면 서버 전체가 죽는다
        }
        // [읽는 법] do { 정리 } while (24시간 기다리기가 끝났는가) 구조. 먼저 한 번 정리하고, 24시간을 기다린 뒤(종료 신호가 오면 루프 탈출) 또 정리한다. do-while이라 첫 정리는 조건과 상관없이 반드시 실행된다.
        while (!_stop.Token.WaitHandle.WaitOne(TimeSpan.FromHours(24)));
    }

    /// <summary>한 번 정리한다. (서버 콘솔 /cleanup 명령과 테스트에서도 부른다)</summary>
    public void RunOnce()
    {
        // ① 서버가 꺼지며 남은 "올리는 중" 행: 하루가 지났으면 실패한 업로드다 (임시 파일은 FileService가 시작할 때 지운다)
        // [읽는 법] 서버가 비정상 종료되면 "올리는 중" 상태로 남은 행이 생긴다. 하루가 지난 UPLOADING 행은 실패한 업로드로 보고 지운다(임시 파일은 FileService가 서버 시작 때 지움). LIMIT @n으로 한 번에 지우는 양을 제한한다.
        int stale = Execute("DELETE FROM files WHERE status = 'UPLOADING' AND created_at < NOW() - INTERVAL 1 DAY LIMIT @n");
        if (stale > 0) log.Info($"정리: 끝나지 않은 업로드 기록 {stale}건");

        if (config.MessageRetentionDays > 0)
        {
            // [읽는 법] 기간 설정이 0보다 클 때만 실행(0 = 끔). "NOW() − INTERVAL @days DAY"보다 오래된 행을 지운다. @days는 문자열에 끼워 넣지 않고 매개변수로 넘긴다. 단, 이 쿼리는 서버 시각이 아니라 DB 서버의 시각을 기준으로 한다.
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
            // [읽는 법] 닫힌(삭제된) 방에 올라온 파일을 찾는다. 방이 닫히면 아무도 받을 수 없는데 디스크는 계속 차지하기 때문이다. 대화 기록은 위의 대화 보관 기간까지 그대로 남긴다. 설정이 0이면 닫히는 즉시(0일 후) 대상이 된다.
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
        // [읽는 법] 서버 종료 신호가 오면 다음 묶음부터 처리를 멈춘다. 파일은 "500개 조회 → 디스크 삭제 → DB 행 삭제"를 더 지울 게 없을 때(batch.Count == 0)까지 반복한다.
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
                // [읽는 법] 디스크 삭제는 FileService가 실패해도 예외를 내지 않고 로그만 남긴다. 그래서 이어지는 DB 행 삭제는 항상 실행되고, 같은 파일이 다음 조회에 다시 걸려 무한 반복되는 일이 없다.
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
            // [읽는 법] 한 묶음(5,000행)을 지운 뒤 0.2초 쉰다. 그동안 DbWriter 등 다른 스레드가 같은 테이블에 쓸 수 있다. 지운 수가 묶음 크기보다 적으면(n < BatchSize) 남은 게 없다는 뜻이라 쉬지 않고 끝낸다.
            Thread.Sleep(200); // 다른 작업(채팅 저장)이 끼어들 틈을 준다
        }
        return total;
    }

    private int Execute(string sql, int days = 0)
    {
        using DbConnection conn = db.Open();
        using DbCommand cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        // [읽는 법] 쿼리에 @n(한 번에 지울 최대 행 수)과 @days(기간)를 매개변수로 넣는다. SQL에 쓰이지 않는 매개변수가 있어도 문제없어서 모든 쿼리가 이 한 함수를 공유한다. 돌려주는 값은 실제로 지워진 행 수다.
        cmd.AddParam("@n", BatchSize);
        cmd.AddParam("@days", days);
        return cmd.ExecuteNonQuery();
    }

    /// <summary>정리 스레드에 종료 신호를 보내고 최대 2초 기다린다.</summary>
    public void Dispose()
    {
        _stop.Cancel();
        // [읽는 법] 취소 신호를 보낸 뒤 스레드가 끝나기를 최대 2초 기다린다. 정리가 중간(DB 호출 중)이어도 서버 종료가 오래 막히지 않게 상한을 둔다. 2초 안에 못 끝나도 IsBackground 스레드라 프로세스는 종료된다.
        _thread?.Join(TimeSpan.FromSeconds(2));
    }
}
