using System.Collections.Concurrent;
using System.Data.Common;
using ChatApp.Common.Models;
using ChatApp.Common.Protocol;
using ChatApp.Server.Core;
using ChatApp.Server.Data;
using ChatApp.Server.Logging;

// ═══════════════════════════════════════════════════════════════════════════
// MessageStore.cs — 대화 기록(채팅·귓속말)을 DB에 저장하고, 지난 대화를 불러와 보내 주는 "DB 작업 스레드"
//
// [연결 관계]
//   MessageService.HandleChat / HandleWhisper ──Save()──────────────▶ 큐 ──▶ DbWriter 스레드 ──▶ MessageRepository.InsertMany
//   RoomService(로그인 복원·방 만들기·입장·초대) ──RequestRoomHistory()─▶ 큐 ──▶ DbWriter 스레드 ──▶ LoadRoom → History 패킷 전송
//   MessageService.HandleHistoryRequest(1:1)  ──RequestDirectHistory()▶ 큐 ──▶ DbWriter 스레드 ──▶ LoadDirect → History 패킷 전송
//   파일 메시지의 미리보기는 FileService.ReadThumbnail(생성자로 받은 함수)로 디스크에서 읽어 붙인다.
//
// [관련 패킷]
//   History (Data = HistoryBatch): 서버 → 클라이언트. 방 입장 시 자동, 1:1은 HistoryReq(Target = 상대 사원번호) 요청 시.
//   묶음이 여러 개면 마지막 묶음만 Last = true.
//
// [스레드]
//   - Save / Request*History: 아무 스레드(주로 각 세션의 수신 스레드)에서 호출 → 큐에 넣고 즉시 반환
//   - Run / Flush / SendHistory: 오직 "DbWriter" 스레드 1개에서만 실행 → DB 작업끼리는 lock이 필요 없다
//   - Dispose: 서버 종료 시 메인 스레드(ChatServer.Stop)에서 호출
// ═══════════════════════════════════════════════════════════════════════════
//
// ───────────────────────────────────────────────────────────────────────────
// [공부 노트 · 면접 대비]
//
// ■ 한 줄 요약
//   DB 저장을 전담하는 "DbWriter" 스레드와 그 앞의 큐. 채팅 저장, 지난 대화 읽기, 삭제·공감·읽음 같은 DB 작업을 한 줄로 세워 순서대로 처리한다.
//
// ■ 읽는 순서
//   ① Job 종류(SaveJob/HistoryJob/ActionJob) → ② 큐에 넣는 쪽(Save, Enqueue, Request*History) → ③ 꺼내 처리하는 Run
//   → ④ Flush(모아서 저장) → ⑤ SendHistory(지난 대화를 묶음으로 전송) → ⑥ Dispose(종료 때 마무리)
//
// ■ 한 장 그림
//   [수신 스레드들] ──Save/Enqueue/Request──▶ [ 큐(BlockingCollection) ] ──▶ [DbWriter 스레드 1개] ──▶ DB
//         (생산자, 바로 돌아옴)                       (들어온 순서대로)           (소비자, 모아서 저장)
//
// ■ 이 파일에서 나오는 개념과 검색어
//   - 생산자-소비자 패턴 ......... "producer consumer pattern BlockingCollection"
//   - 배치(묶음) 저장과 트랜잭션 ... "batch insert transaction commit performance"
//   - FIFO 순서 보장 .............. "FIFO queue ordering guarantee"
//   - record와 with 식 ............ "C# record with expression immutable"
//   - IDisposable / Join ........... "IDisposable Thread.Join graceful shutdown"
//
// ■ 면접 예상 질문과 답
//   Q1. 채팅을 받을 때 바로 INSERT하지 않고 큐를 두는 이유는요?
//       A. DB가 잠깐 느려지면 그 사람의 채팅 처리도 같이 멈춥니다. 수신 스레드는 큐에 넣기만 하고 바로 다음 패킷을 처리하고, DbWriter 한 스레드가 꺼내서 저장합니다.
//   Q2. 저장·삭제·공감·지난 대화 불러오기를 왜 "한 큐"로 처리하나요?
//       A. 순서를 지키기 위해서입니다. "보냄 → 곧바로 삭제"가 저장 → 삭제 순서로 처리되어 없는 메시지를 지우는 일이 없고, "보냄 → 곧바로 입장"에서도 입장한 사람이 방금 메시지를 받습니다.
//   Q3. 배치 저장이 왜 빠른가요? 지연은 없나요?
//       A. 커밋마다 디스크에 확실히 기록하는 비용이 드는데, 여러 개를 모아 한 번에 커밋하면 그 비용을 나눕니다. 큐가 비어 있으면 바로 저장해서 평소 지연은 없고, 쌓여 있을 때만 최대 200개까지 모읍니다.
//   Q4. 묶음 저장 중 1개가 실패하면요?
//       A. 묶음은 트랜잭션 하나라 200개가 모두 롤백됩니다. 그래서 한 개씩 다시 저장해서 문제 있는 메시지만 버리고 나머지는 살립니다.
//   Q5. 서버를 끌 때 마지막 대화가 사라지지 않게 하려면요?
//       A. Dispose에서 CompleteAdding으로 "더 안 받는다"고 표시하면 큐가 빌 때까지 처리하고 스레드가 끝납니다. Join(최대 5초)으로 기다려, DB가 멈춰 있어도 종료가 영원히 막히지 않게 합니다.
//   Q6. 최근 메시지를 메모리에 기억하는(_recent) 이유는요?
//       A. 방금 보낸 메시지는 아직 큐에서 저장을 기다리는 중일 수 있습니다. 그 메시지에 답장·삭제·공감이 오면 DB에는 아직 없으므로, 최근 20,000개는 메모리에서 먼저 찾습니다.
//   Q7. 이 구조의 한계는 무엇인가요? (솔직하게 답하기)
//       A. (1) 저장 큐에 개수 제한이 없어서 DB가 오래 느리면 큐가 메모리에 계속 쌓입니다(감시·경보나 한도가 필요). (2) DbWriter가 하나라 쓰기가 한 줄로 서는 병목이 될 수 있습니다. 순서 보장과 맞바꾼 선택이고,
//          더 키우려면 방 단위로 나눠 여러 소비자를 두는 방법을 검토합니다.
//   Q8. 지난 대화를 여러 묶음으로 나눠 보내는 이유는요?
//       A. 패킷 하나는 최대 1MB인데, 사진 미리보기가 여러 장이면 한 패킷에 다 못 담습니다. 크기를 어림해서 약 600KB가 되면 보내고 새 묶음을 시작하며, 마지막 묶음에만 Last = true를 붙입니다.
//   Q9. 지난 대화 요청에 개수 제한(20개)을 둔 이유는요?
//       A. 지난 대화 한 번은 DB 읽기와 최대 수 MB 전송이라 무겁습니다. 같은 요청을 연달아 보내 DbWriter를 독점하는 것을 막기 위해 한 사람이 동시에 기다릴 수 있는 수를 제한합니다.
//
// ■ 문제가 났을 때 어디를 볼까 (유지보수)
//   - 서버를 재시작했더니 마지막 대화가 없다 ... Dispose의 Join(5초)이 끝나기 전에 강제 종료됐는지, 로그의 "대화 저장 실패"
//   - 메시지 번호가 겹치거나 이상하다 ........... NextId와 _lastId 초기화(MaxMessageId), 로그의 "마지막 메시지 번호를 읽지 못해"
//   - 지난 대화가 안 나오거나 일부만 나온다 ..... SendHistory의 DbException 로그, HasMore·Last 값, 묶음 크기
//   - 서버 메모리가 늘고 저장이 늦다 ............. DB 상태(느림·정지) → _queue에 작업이 쌓이는 중인지
// ───────────────────────────────────────────────────────────────────────────
namespace ChatApp.Server.Services;

/// <summary>
/// 대화 기록 저장·불러오기 전용 "DB 작업 스레드" (생산자-소비자 패턴, ServerLogger와 같은 구조).
///
/// 왜 따로 스레드를 두나?
/// - 채팅을 받을 때마다 수신 스레드에서 바로 INSERT하면, DB가 잠깐 느려질 때 그 사람의 채팅 처리도 같이 멈춘다.
/// - 그래서 수신 스레드는 큐에 넣기만 하고(Save, 아주 빠름) 바로 다음 패킷을 처리한다.
///   DB 작업 스레드 1개가 큐에서 꺼내 모아서(배치) 저장한다.
///
/// 왜 "불러오기"도 같은 큐로 처리하나? (순서 보장)
/// - 큐는 넣은 순서대로(FIFO) 처리된다. 방에 들어오기 직전에 보낸 메시지는 불러오기 요청보다 먼저 큐에 들어가 있으므로,
///   불러오기를 처리할 때는 이미 DB에 저장된 뒤다. 저장과 불러오기를 다른 스레드로 나누면 "방금 한 말이 기록에 없는" 경쟁 상태가 생긴다.
/// </summary>
// [개념] 생산자-소비자(Producer-Consumer) 패턴:
//   여러 "생산자"(수신 스레드들)가 작업을 큐에 넣고, "소비자"(DbWriter 스레드 1개)가 꺼내 처리한다.
//   생산자는 느린 작업(DB)을 기다리지 않아도 되고, 소비자는 일이 없으면 잠들어 CPU를 쓰지 않는다.
// [개념] IDisposable: "다 쓰면 정리(Dispose)해야 하는 자원을 가진 객체"라는 표시. 여기서는 스레드 종료·남은 저장 마무리.
public sealed class MessageStore : IDisposable
{
    // 큐에 넣는 작업 종류 (record = 값만 담는 간단한 클래스)
    // [개념] abstract record + 상속 record: "작업"이라는 공통 부모 아래 저장 작업 / 불러오기 작업 두 종류를 둔다.
    //   한 큐에 서로 다른 종류의 작업을 넣고, 꺼낼 때 is 패턴(job is SaveJob save)으로 종류를 구분한다.
    // [개념] record SaveJob(StoredMessage Message): 괄호 안이 곧 속성 목록인 "위치 레코드". 생성자·속성·비교가 자동으로 만들어진다.
    private abstract record Job;
    private sealed record SaveJob(StoredMessage Message) : Job;
    // Before = 요청한 시각. 이 시각 "이전" 메시지만 기록으로 보낸다. 이후 메시지는 실시간 패킷으로 받으므로 중복을 줄인다.
    // Since = 이 시각 이전 메시지는 주지 않는다(나중에 들어온 방 멤버는 들어오기 전 대화를 보지 못한다).
    private sealed record HistoryJob(ClientSession Session, int? RoomId, string? Me, string? Peer, DateTime Before, DateTime? Since = null, string? BeforeKey = null) : Job;
    // 그 밖의 DB 작업(읽음 위치 저장, 삭제 표시, 공감 등). 같은 큐에서 순서대로 처리되므로 "저장 → 삭제" 순서가 뒤바뀌지 않는다.
    private sealed record ActionJob(string Name, Action Work) : Job;

    // [개념] const: 컴파일할 때 값이 박히는 상수. 바뀌지 않는 설정값에 쓴다.
    private const int MaxBatch = 200;                  // 한 트랜잭션에 저장할 최대 메시지 수
    // [개념] 600_000: 숫자 사이의 _ 는 읽기 쉽게 하는 구분자일 뿐 값에는 영향이 없다(= 600000).
    private const int MaxHistoryPacketBytes = 600_000; // 지난 대화 패킷 하나의 대략적인 최대 크기 (패킷 한도 1MB보다 여유 있게)

    // [개념] BlockingCollection: 스레드 안전한 큐 + "비어 있으면 꺼내는 쪽이 기다림(블로킹)" 기능.
    //   Add = 넣기(여러 스레드 동시 가능), GetConsumingEnumerable = 하나씩 꺼내기(없으면 들어올 때까지 대기).
    //   직접 lock + Monitor.Wait/Pulse로 만들 수도 있지만 실수하기 쉬워서 .NET이 제공하는 것을 쓴다.
    private readonly BlockingCollection<Job> _queue = new();
    private readonly MessageRepository _repository;
    // [개념] Func<string, byte[]?>: "string을 받아 byte[]?를 돌려주는 함수"를 담는 변수(델리게이트).
    // [왜] FileService 객체 전체를 받지 않고 필요한 기능(미리보기 읽기) 하나만 함수로 받는다 → 두 클래스가 덜 얽힌다(결합도 ↓).
    private readonly Func<string, byte[]?> _loadThumbnail;
    private readonly ServerLogger _log;
    private readonly Thread _thread;

    /// <param name="loadThumbnail">파일 저장 경로 → 미리보기 바이트 (FileService.ReadThumbnail)</param>
    // ── 메시지 번호 발급 ──
    // [왜] 메시지 번호(MessageId)는 "보내는 순간" 필요하다(받는 사람이 그 번호로 답장·삭제·공감을 한다).
    //      DB의 AUTO_INCREMENT는 저장한 뒤에야 알 수 있는데 저장은 이 스레드가 나중에 하므로, 서버가 직접 발급한다.
    //      시작할 때 DB의 가장 큰 번호를 읽어 그다음부터 Interlocked로 1씩 늘린다(여러 수신 스레드가 동시에 받아도 겹치지 않음).
    private long _lastId;

    /// <summary>새 메시지 번호 (여러 스레드에서 동시에 불러도 겹치지 않는다).</summary>
    public long NextId() => Interlocked.Increment(ref _lastId);

    // ── 최근 메시지 기억 (답장·삭제·공감 권한 확인용) ──
    // [왜] 방금 보낸 메시지는 아직 DB에 저장되기 전일 수 있다(큐에서 기다리는 중). 그래서 최근 메시지는 메모리에서 먼저 찾고,
    //      없을 때만 DB를 본다. 메모리가 무한히 커지지 않게 최근 RecentLimit개만 기억한다.
    private const int RecentLimit = 20_000;
    private readonly ConcurrentDictionary<long, MessageMeta> _recent = new();
    private readonly ConcurrentQueue<long> _recentOrder = new();

    /// <summary>방금 보낸 메시지를 기억한다.</summary>
    public void Remember(MessageMeta meta)
    {
        // [읽는 법] 메모리 장부(_recent)에 넣고, 넣은 순서를 따로 큐(_recentOrder)에 기록한다. Dictionary는 순서를 모르기 때문이다.
        //           20,000개를 넘으면 가장 오래된 것부터 지운다(먼저 들어온 것이 먼저 나가는 FIFO).
        _recent[meta.Id] = meta;
        _recentOrder.Enqueue(meta.Id);
        while (_recentOrder.Count > RecentLimit && _recentOrder.TryDequeue(out long old)) _recent.TryRemove(old, out _);
    }

    /// <summary>메시지 찾기: 최근 것은 메모리, 아니면 DB. 없으면 null. [스레드] 요청한 사람의 수신 스레드에서 DB를 읽을 수 있다.</summary>
    public MessageMeta? Find(long messageId)
    {
        if (_recent.TryGetValue(messageId, out MessageMeta? meta)) return meta;
        try { return _repository.Find(messageId); }
        catch (DbException ex)
        {
            _log.Error($"메시지 찾기 실패({messageId}): {ex.Message}");
            return null;
        }
    }

    /// <summary>기억해 둔 메시지를 삭제됨으로 바꾼다.</summary>
    public void ForgetContent(long messageId)
    {
        // [개념] with 식: record 객체를 "복사하면서 일부 값만 바꾼" 새 객체를 만든다(원본은 그대로). 다른 스레드가 옛 객체를 읽는 중이어도
        //        값이 도중에 바뀌지 않고, 새 객체로 통째로 바꿔 끼우므로 안전하다.
        if (_recent.TryGetValue(messageId, out MessageMeta? meta)) _recent[messageId] = meta with { Deleted = true };
    }

    public void UpdateContent(long messageId, string content)
    {
        if (_recent.TryGetValue(messageId, out MessageMeta? meta))
            _recent[messageId] = meta with { Content = content, Edited = true };
    }

    public MessageStore(MessageRepository repository, Func<string, byte[]?> loadThumbnail, ServerLogger log)
    {
        _repository = repository;
        _loadThumbnail = loadThumbnail;
        _log = log;
        try
        {
            _lastId = repository.MaxMessageId();
        }
        catch (DbException ex)
        {
            // DB를 못 읽으면 "지금 시각(밀리초) × 1000"부터 시작해 예전 번호와 겹치지 않게 한다
            _lastId = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1000;
            log.Error($"마지막 메시지 번호를 읽지 못해 {_lastId}부터 시작합니다: {ex.Message}");
        }
        // [개념] new Thread(Run): Run 메서드를 새 스레드에서 실행. 생성자에서 바로 시작해 서버가 켜지면 항상 대기 중이다.
        // [왜] Name을 붙이면 로그([T n])와 Visual Studio 디버거의 스레드 창에서 어떤 스레드인지 바로 알 수 있다.
        _thread = new Thread(Run) { IsBackground = true, Name = "DbWriter" };
        _thread.Start();
    }

    /// <summary>메시지 저장 예약 (어느 스레드에서 불러도 안전, 바로 돌아온다).</summary>
    // [스레드] 수신 스레드에서 호출. DB를 기다리지 않으므로 채팅 처리 속도에 영향이 거의 없다.
    public void Save(StoredMessage message) => TryAdd(new SaveJob(message));

    /// <summary>방 지난 대화를 session에게 보내도록 예약. 지금 시각 이전 것만 보낸다(이후 것은 실시간으로 받으므로).</summary>
    public void RequestRoomHistory(ClientSession session, int roomId, DateTime? since = null, DateTime? before = null, string? beforeKey = null)
        // [왜] 기록을 어느 세션에게 보낼지(session)를 작업에 같이 담아 둔다. DbWriter 스레드가 읽은 뒤 그 세션의 송신 큐에 넣는다.
        => TryAddHistory(new HistoryJob(session, roomId, null, null, before ?? DateTime.UtcNow, since, beforeKey));

    /// <summary>
    /// DB 작업을 큐에 넣는다 (읽음 위치 저장, 삭제 표시, 공감 등). 저장 작업과 같은 큐라 들어온 순서대로 처리된다.
    /// </summary>
    // [예] A가 메시지를 보내자마자 삭제하면: 저장 작업 → 삭제 작업 순서로 큐에 들어가므로, 없는 메시지를 지우려는 일이 생기지 않는다.
    public void Enqueue(string name, Action work) => TryAdd(new ActionJob(name, work));

    /// <summary>1:1 지난 대화를 session에게 보내도록 예약.</summary>
    public void RequestDirectHistory(ClientSession session, string me, string peer, DateTime? before = null, string? beforeKey = null)
        => TryAddHistory(new HistoryJob(session, null, me, peer, before ?? DateTime.UtcNow, null, beforeKey));

    /// <summary>한 사람이 동시에 기다릴 수 있는 지난 대화 요청 수.</summary>
    // [왜] 지난 대화 1번 = DB 읽기 + 최대 수 MB(사진 미리보기). 같은 요청을 1초에 수백 번 보내면
    //      DbWriter 스레드가 그 일만 하느라 다른 사람의 대화 저장이 밀리고, 서버 메모리도 크게 쓴다.
    //      로그인 직후 참여한 방이 여러 개면 한꺼번에 요청이 오므로 넉넉히 두되 무한정은 막는다.
    private const int MaxPendingHistoryPerSession = 20;

    private void TryAddHistory(HistoryJob job)
    {
        if (Interlocked.Increment(ref job.Session.PendingHistory) > MaxPendingHistoryPerSession)
        {
            Interlocked.Decrement(ref job.Session.PendingHistory);
            _log.Info($"[{job.Session.Id}] 지난 대화 요청이 너무 많아 무시합니다.");
            return;
        }
        TryAdd(job);
    }

    private void TryAdd(Job job)
    {
        try { _queue.Add(job); }
        // [주의] Dispose()에서 CompleteAdding을 부른 뒤에 Add하면 InvalidOperationException이 난다.
        //   서버가 꺼지는 순간 도착한 채팅 때문에 예외로 죽지 않도록 조용히 무시한다.
        catch (InvalidOperationException) { /* 서버 종료 중 (CompleteAdding 이후) */ }
    }

    // ───────────────────────── DB 작업 스레드 ─────────────────────────

    /// <summary>DbWriter 스레드 본체: 큐에서 작업을 하나씩 꺼내 처리한다.</summary>
    // [스레드] 이 메서드는 DbWriter 스레드에서만 돈다. DB 연결도 이 스레드만 쓰므로 DB 작업끼리 충돌이 없다.
    private void Run()
    {
        // [개념] 배치(batch) 저장: 메시지를 하나씩 저장(커밋)하지 않고 여러 개를 모아서 한 번에 저장한다.
        //   DB는 커밋할 때마다 디스크에 확실히 기록(fsync)하므로, 1000개를 1000번 커밋하는 것보다 1번 커밋이 훨씬 빠르다.
        var batch = new List<StoredMessage>();

        // GetConsumingEnumerable: 큐가 비면 기다리고, CompleteAdding()이 불리고 큐가 비면 끝난다
        foreach (Job job in _queue.GetConsumingEnumerable())
        {
            if (job is SaveJob save)
            {
                batch.Add(save.Message);
                // 뒤에 작업이 더 쌓여 있으면 조금 더 모았다가 한 번에 저장 (채팅이 몰릴 때 효율 ↑)
                // [왜] 큐가 비었으면 바로 저장(평소엔 지연 없음), 쌓여 있으면 최대 200개까지 모았다가 저장(몰릴 때 효율).
                //   "몇 초마다 저장" 같은 타이머 방식보다 지연이 적고 코드도 단순하다.
                if (_queue.Count > 0 && batch.Count < MaxBatch) continue;
                Flush(batch);
            }
            else if (job is ActionJob action)
            {
                Flush(batch); // 순서 보장: 앞에 쌓인 메시지를 먼저 저장한다 (방금 보낸 메시지를 삭제·공감하는 경우)
                try
                {
                    action.Work();
                }
                catch (Exception ex)
                {
                    _log.Error($"DB 작업 실패({action.Name}): {ex.Message}");
                }
            }
            else if (job is HistoryJob history)
            {
                // [주의] FIFO 큐 순서 보장이 이 설계의 핵심이다.
                //   예) A가 "안녕" 전송(Save가 큐에 들어감) → 직후 B가 입장(불러오기 요청이 큐 뒤에 들어감)
                //   큐는 들어온 순서대로 처리되므로, B의 불러오기를 처리하기 전에 "안녕"이 반드시 DB에 저장되어 있다.
                //   그래서 B는 입장 직전 메시지도 놓치지 않는다.
                Flush(batch); // 순서 보장: 앞에 쌓인 메시지를 먼저 저장하고 나서 읽는다
                try
                {
                    SendHistory(history);
                }
                catch (Exception ex)
                {
                    // [주의] 이 스레드가 예외로 죽으면 이후 모든 대화 저장이 멈추고(큐만 쌓임) 서버도 종료된다. 반드시 여기서 잡는다.
                    _log.Error($"지난 대화 보내기 실패: {ex.Message}");
                }
                finally
                {
                    Interlocked.Decrement(ref history.Session.PendingHistory);
                }
            }
        }
        // [개념] foreach가 끝나는 때 = Dispose()에서 CompleteAdding을 부르고 큐가 완전히 빈 뒤. 즉 서버 종료 시점이다.
        Flush(batch); // 종료 직전에 남은 것 저장
    }

    /// <summary>모아 둔 메시지를 DB에 한 번에 저장하고 목록을 비운다.</summary>
    private void Flush(List<StoredMessage> batch)
    {
        if (batch.Count == 0) return;
        try
        {
            _repository.InsertMany(batch);
        }
        catch (DbException ex) when (batch.Count > 1)
        {
            // [왜] 묶음 저장은 트랜잭션 하나라서, 200개 중 1개만 문제가 있어도 200개가 모두 롤백된다.
            //      한 개씩 다시 저장해서 "문제 있는 메시지만" 버리고 나머지는 살린다.
            _log.Error($"대화 묶음 저장 실패 ({batch.Count}개), 한 개씩 다시 시도: {ex.Message}");
            int lost = 0;
            foreach (StoredMessage message in batch)
            {
                try { _repository.InsertMany([message]); }
                catch (DbException) { lost++; }
            }
            if (lost > 0) _log.Error($"대화 {lost}개를 저장하지 못했습니다.");
        }
        catch (DbException ex)
        {
            // DB가 꺼져 있어도 채팅 자체는 계속 되어야 하므로 기록만 포기하고 로그를 남긴다
            _log.Error($"대화 저장 실패 ({batch.Count}개): {ex.Message}");
        }
        catch (Exception ex)
        {
            _log.Error($"대화 저장 중 예상치 못한 오류 ({batch.Count}개): {ex.Message}");
        }
        batch.Clear();
    }

    /// <summary>지난 대화를 DB에서 읽어 요청한 사람에게 History 패킷(여러 묶음일 수 있음)으로 보낸다.</summary>
    private void SendHistory(HistoryJob job)
    {
        if (job.Session.IsClosed) return; // 그사이 나간 사람

        List<HistoryRow> rows;
        DateTime? peerRead = null;
        try
        {
            // 방 번호가 있으면 방 기록, 없으면 1:1 기록 (job.Me!, job.Peer!: 1:1 작업이면 둘 다 반드시 채워져 있다)
            rows = job.RoomId is int roomId
                ? _repository.LoadRoom(roomId, job.Before, HistoryBatch.Limit, job.Since, job.BeforeKey)
                : _repository.LoadDirect(job.Me!, job.Peer!, job.Before, HistoryBatch.Limit, job.BeforeKey);
            // 1:1이면 상대가 어디까지 읽었는지도 함께 보낸다 (내 메시지 옆 "1" 표시)
            if (job.RoomId is null) peerRead = _repository.GetDirectRead(job.Peer!, job.Me!);
        }
        catch (DbException ex)
        {
            // 기록을 못 불러와도 채팅은 계속 된다. 클라이언트는 기록 없이 시작할 뿐이다.
            _log.Error($"대화 기록 불러오기 실패: {ex.Message}");
            return;
        }

        // 파일 메시지는 디스크에서 미리보기를 읽어 붙인다
        foreach (HistoryRow row in rows)
        {
            // [개념] 패턴 두 개를 && 로 연결: File이 있고 저장 경로도 있을 때만 file, path 변수로 꺼내 쓴다.
            if (row.Item.File is SharedFile file && row.StoredPath is string path)
                file.Thumbnail = _loadThumbnail(path);
        }

        // 크기를 대략 계산해 가며 여러 묶음으로 나눈다 (미리보기 사진이 많으면 패킷 하나에 다 안 들어감)
        var current = NewBatch(job, peerRead);
        int size = 0;
        foreach (HistoryRow row in rows)
        {
            // [개념] 패킷 크기 어림셈: 기본 300바이트 + 한글은 UTF-8로 글자당 최대 3바이트 + 미리보기는 Base64로 바뀌면 4/3배로 커진다.
            // [왜] 패킷 하나는 최대 1MB(PacketSerializer.MaxBodySize)라서, 사진 미리보기가 여러 장이면 한 패킷에 다 못 담는다.
            //   약 600KB를 넘기 전에 지금 묶음을 보내고 새 묶음을 시작한다.
            int itemSize = 300 + row.Item.Body.Length * 3 + (row.Item.File?.Thumbnail?.Length ?? 0) * 4 / 3
                           + (row.Item.ReplyTo?.Text.Length ?? 0) * 3                                      // 답장 인용
                           + (row.Item.Reactions?.Sum(x => 20 + x.EmployeeNos.Count * 25) ?? 0);         // 공감한 사람 목록
            if (current.Items.Count > 0 && size + itemSize > MaxHistoryPacketBytes)
            {
                // 아직 뒤에 묶음이 더 있다는 표시 → 클라이언트는 Last = true가 올 때까지 모아 둔다
                current.Last = false;
                // [스레드] Send는 세션의 송신 큐에 넣기만 하므로 DbWriter 스레드가 네트워크 때문에 멈추지 않는다.
                job.Session.Send(Packet.Create(PacketType.History).WithData(current));
                current = NewBatch(job, peerRead);
                size = 0;
            }
            current.Items.Add(row.Item);
            size += itemSize;
        }
        // 마지막 묶음 (기록이 하나도 없어도 Last = true인 빈 묶음을 보내 "불러오기 끝"을 알린다)
        current.HasMore = rows.Count >= HistoryBatch.Limit;
        job.Session.Send(Packet.Create(PacketType.History).WithData(current));
    }

    /// <summary>빈 묶음 하나 만들기. 기본값은 Last = true(마지막 묶음).</summary>
    private static HistoryBatch NewBatch(HistoryJob job, DateTime? peerRead)
        => new() { RoomId = job.RoomId, PeerNo = job.Peer, Last = true, PeerLastReadAt = peerRead };

    /// <summary>서버 종료 시: 더 받지 않고, 남은 저장을 마칠 때까지 최대 5초 기다린다.</summary>
    public void Dispose()
    {
        // [개념] CompleteAdding: "더 이상 넣지 않는다"는 표시. 큐가 비면 Run의 foreach가 끝나 스레드가 종료된다.
        _queue.CompleteAdding();
        // [개념] Join: 그 스레드가 끝날 때까지 기다린다. 최대 5초만 기다려 DB가 멈춰 있어도 서버 종료가 영원히 막히지 않게 한다.
        // [왜] 이걸 안 하면 서버를 끌 때 큐에 남아 있던(아직 저장 안 된) 마지막 대화가 사라진다.
        _thread.Join(TimeSpan.FromSeconds(5));
    }
}
