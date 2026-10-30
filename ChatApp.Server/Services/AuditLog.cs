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
//
// ───────────────────────────────────────────────────────────────────────────
// [공부 노트 · 면접 대비]
//
// ■ 한 줄 요약
//   "누가 무엇을 했나"를 DB에 남기는 기록기. 호출하는 쪽은 큐에 넣기만 하고, AuditWriter 스레드 1개가 최대 100건씩 모아 AuditRepository.InsertMany로 저장한다. 큐가 꽉 차면 기다리지 않고 버린다.
//
// ■ 흐름
//   Record(action, actor, target, detail, ip) → IP에서 포트 떼기(StripPort) → AuditRecord 만들기(시각은 이 순간의 UTC) → TryAdd(큐 5만 건 한도)
//   AuditWriter: 하나 꺼내 기다림 → 쌓인 게 있으면 100건까지 더 꺼냄 → InsertMany 한 번(DB 트랜잭션 하나) → 실패하면 로그에 요약+건별 내용 남김
//   Dispose: CompleteAdding → 남은 기록을 저장하도록 최대 5초 기다림(Join)
//
// ■ 이 파일에서 나오는 개념과 검색어
//   - 생산자-소비자와 bounded BlockingCollection ....... "BlockingCollection boundedCapacity TryAdd"
//   - 배치(묶음) 저장으로 DB 부하 줄이기 ............... "batch insert transaction performance"
//   - 가용성 vs 완전성(버릴까, 기다릴까) ............... "availability vs durability trade-off logging"
//   - IPEndPoint.TryParse(IPv4/IPv6 + 포트) ........... "IPEndPoint.TryParse IPv6"
//   - 감사 로그와 일반 로그의 차이 ..................... "audit log vs application log"
//
// ■ 면접 예상 질문과 답
//   Q1. 감사 로그 저장을 왜 비동기(큐)로 했나요?
//       A. 로그인 한 번마다 DB INSERT를 직접 하면 DB가 잠깐 느려질 때 로그인·파일 전송까지 느려집니다. 기록은 조금 늦어도 되므로 큐에 넣고 바로 돌아가고, 저장은 전용 스레드가 모아서 합니다.
//   Q2. 큐가 가득 차면 버린다고요? 감사 로그를 버려도 되나요?
//       A. 이는 선택입니다. 감사 기록 때문에 로그인·채팅이 멈추면 서비스 전체가 멈추므로 가용성을 우선했습니다. 대신 버릴 때 콘솔·파일 로그에 에러로 남깁니다. 법적 증거 수준의 완전성이 필요한 서비스라면 반대로 기다리게(또는 디스크에 임시 저장) 설계해야 합니다. 한도 5만 건은 평소에는 거의 도달하기 어려운 값이라고 생각하지만, 실제 사용량으로 확인한 값은 아닙니다.
//   Q3. 서버가 갑자기 죽으면 큐에 있던 기록은요?
//       A. 메모리에만 있으므로 사라집니다. 정상 종료에서는 Dispose가 남은 기록을 저장하고(최대 5초), 강제 종료·전원 차단에서는 그 사이 쌓인 기록은 잃을 수 있습니다. 저장이 매우 빠른 편이라 그 구간은 짧지만 0은 아닙니다.
//   Q4. 저장 중 DB 오류가 나면요?
//       A. DbException을 잡아 "N건 저장 실패" 요약과 각 기록의 내용을 서버 로그에 남깁니다. 서버는 멈추지 않고, 사람이 로그 파일에서 흔적을 되살릴 수 있게 한 것입니다. 이 배치는 다시 시도하지 않고 버려집니다(재시도 로직 없음).
//   Q5. 시각을 Record 순간에 찍는 이유는요?
//       A. 저장이 늦어져도 "일어난 시각"이 정확해야 하기 때문입니다. DB에 넣는 시각으로 찍으면 큐 대기 시간만큼 틀어집니다.
//   Q6. IP에서 포트를 떼는 이유는요?
//       A. 포트는 접속마다 바뀌는 임시 번호라 "같은 PC에서 온 기록"을 검색하거나 묶어 보기 어렵습니다. IP만 남기면 검색·집계가 쉽습니다. IPv6 주소의 [::1]:포트 형태도 IPEndPoint.TryParse가 처리합니다.
//
// ■ 디버깅할 때 먼저 볼 곳
//   - 감사 기록이 안 보인다 ............ 서버 로그의 "감사 로그 ... 저장 실패"(DB 연결·칸 길이), "대기열이 가득 차" 메시지
//   - 종료 때 마지막 기록이 없다 ........ Dispose의 5초 제한, 서버가 강제 종료되었는지
//
// ■ 개선 아이디어 (코드는 바꾸지 않음)
//   - 저장 실패한 배치를 파일에 따로 보관하거나 몇 번 재시도
//   - 감사 기록이 버려진 횟수를 세어 관리자 화면·종료 시 요약으로 보여 주기
// ───────────────────────────────────────────────────────────────────────────

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
