using System.Collections.Concurrent;
using System.Data.Common;
using ChatApp.Server.Data;
using ChatApp.Server.Logging;

namespace ChatApp.Server.Services;

// ═══════════════════════════════════════════════════════════════════════════
// [파일 역할] 감사 로그(audit log) 기록기. "누가, 언제, 어디서(IP), 무엇을 했는지"를 DB audit_logs에 남긴다.
// [개념]     감사 로그: 보안 사고가 났을 때 되짚어 볼 수 있는 기록. 일반 로그(서버 콘솔·파일)는 개발자가 문제를 찾는 용도라
//            형식이 자유롭고 지워지기도 하지만, 감사 로그는 정해진 칸(행위자·동작·대상)으로 검색할 수 있어야 한다.
//            예) "퇴사한 A씨가 마지막으로 파일을 받은 게 언제?", "누가 B씨 계정을 정지했지?"
// [스레드]   Record()는 아무 스레드(로그인 처리 중인 수신 스레드, 파일 스레드, 서버 콘솔)에서나 부른다 → 큐에 넣고 바로 돌아간다.
//            실제 DB 쓰기는 전용 "AuditWriter" 스레드 1개가 모아서 한다.
// [왜]       로그인 한 번에 DB INSERT를 바로 하면, DB가 잠깐 느려질 때 로그인·파일 전송까지 같이 느려진다.
//            기록은 조금 늦게 들어가도 되므로 큐에 맡기고, 여러 건을 트랜잭션 하나로 묶어 쓴다(MessageStore와 같은 구조).
// ═══════════════════════════════════════════════════════════════════════════

public sealed class AuditLog : IDisposable
{
    private const int BatchSize = 100;

    private readonly AuditRepository _repository;
    private readonly ServerLogger _log;
    private readonly BlockingCollection<AuditRecord> _queue = new(boundedCapacity: 50_000);
    private readonly Thread _thread;

    public AuditLog(AuditRepository repository, ServerLogger log)
    {
        _repository = repository;
        _log = log;
        _thread = new Thread(WriteLoop) { IsBackground = true, Name = "AuditWriter" };
        _thread.Start();
    }

    /// <summary>감사 기록 1건 예약. 막히지 않고 바로 돌아온다.</summary>
    /// <param name="action">AuditActions의 상수 (LOGIN_OK, SUSPEND ...)</param>
    /// <param name="actor">한 사람 사원번호 (서버 콘솔이면 "CONSOLE")</param>
    /// <param name="target">대상 사원번호</param>
    /// <param name="detail">내용 (사유, 파일 이름 등)</param>
    /// <param name="ip">접속 주소 ("IP:포트"면 IP만 남긴다)</param>
    public void Record(string action, string? actor, string? target = null, string? detail = null, string? ip = null)
    {
        var record = new AuditRecord(DateTime.UtcNow, actor, action, target, detail, StripPort(ip));
        // [왜] TryAdd: DB가 오래 멈춰 큐가 5만 건까지 찼다면, 기다리지 않고 버리고 콘솔에 알린다.
        //      감사 기록 때문에 로그인·채팅 스레드가 멈추면 서비스 전체가 멈추기 때문이다(가용성 우선).
        bool added;
        try { added = _queue.TryAdd(record); }
        catch (InvalidOperationException) { return; } // 서버 종료 중(CompleteAdding 이후)
        if (!added)
            _log.Error($"감사 로그 대기열이 가득 차 기록을 버렸습니다: {action} {actor} → {target}");
    }

    /// <summary>[스레드] AuditWriter 전용. 하나 꺼낸 뒤, 쌓여 있는 것을 최대 100개까지 더 꺼내 한 번에 저장한다.</summary>
    private void WriteLoop()
    {
        var batch = new List<AuditRecord>(BatchSize);
        // [개념] GetConsumingEnumerable: 큐가 비면 기다리고, CompleteAdding()이 불리고 다 꺼내면 foreach가 끝난다.
        foreach (AuditRecord first in _queue.GetConsumingEnumerable())
        {
            batch.Add(first);
            while (batch.Count < BatchSize && _queue.TryTake(out AuditRecord? more)) batch.Add(more);
            try
            {
                _repository.InsertMany(batch);
            }
            catch (DbException ex)
            {
                // 기록 실패가 서버를 멈추게 하지는 않는다. 대신 콘솔/파일 로그에 남겨 흔적은 남긴다.
                _log.Error($"감사 로그 {batch.Count}건 저장 실패: {ex.Message}");
                foreach (AuditRecord r in batch)
                    _log.Error($"  (감사) {r.Time:HH:mm:ss} {r.Action} {r.ActorNo} → {r.TargetNo} {r.Detail} {r.ClientIp}");
            }
            batch.Clear();
        }
    }

    /// <summary>"10.0.0.5:51234" → "10.0.0.5", "[::1]:51234" → "::1"</summary>
    private static string? StripPort(string? endPoint)
    {
        if (string.IsNullOrEmpty(endPoint)) return endPoint;
        if (System.Net.IPEndPoint.TryParse(endPoint, out System.Net.IPEndPoint? ep)) return ep.Address.ToString();
        return endPoint;
    }

    /// <summary>서버 종료 시: 남은 기록을 모두 저장하고 끝낸다(최대 5초).</summary>
    public void Dispose()
    {
        _queue.CompleteAdding();
        _thread.Join(TimeSpan.FromSeconds(5));
    }
}
