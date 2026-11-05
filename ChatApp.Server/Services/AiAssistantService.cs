using System.Collections.Concurrent;
using System.Diagnostics;
using ChatApp.Common.Models;
using ChatApp.Common.Protocol;
using ChatApp.Rag;
using ChatApp.Server.Core;
using ChatApp.Server.Data;
using ChatApp.Server.Logging;

namespace ChatApp.Server.Services;

// ═══════════════════════════════════════════════════════════════════════════
// [파일 역할] "AI 비서": 사용자가 한국어로 묻으면, 서버에 저장된 대화 중 "그 사람이 볼 수 있는 것"만 찾아
//             서버 PC의 로컬 LLM(Ollama)이 근거를 인용해 답한다. 대화 내용은 서버 밖으로 나가지 않는다.
// [연결]     PacketHandler가 AiAskReq를 HandleAsk로 넘긴다. 실제 검색·생성은 ChatApp.Rag 라이브러리가 한다.
// [스레드]   HandleAsk는 수신 스레드에서 불리지만 오래 걸리는 일(CPU 추론 수십 초)은 Task로 넘기고 바로 돌아온다.
//            수신 스레드를 붙잡으면 그 사람의 하트비트·채팅이 멈춰 연결이 끊긴다.
//            CPU 추론은 동시에 여러 개 돌리면 모두 느려지므로 SemaphoreSlim(1)로 한 번에 하나씩 처리한다.
// ═══════════════════════════════════════════════════════════════════════════
//
// ───────────────────────────────────────────────────────────────────────────
// [공부 노트 · 면접 대비]
//
// ■ 한 줄 요약
//   RAG(검색 증강 생성) 파이프라인을 사내 메신저에 붙인 서비스. 핵심은 "권한을 지키는 RAG"이다:
//   검색 범위를 질문한 사람이 멤버인 방과 본인이 낀 1:1로 제한한 뒤에 순위를 매긴다.
//
// ■ 면접 예상 질문과 답
//   Q1. 사내 대화를 LLM에 넣으면 남의 비공개 대화가 답에 섞여 나오지 않나요?
//       A. 그래서 권한 필터를 "검색 결과를 만든 뒤"가 아니라 "순위를 계산하기 전"에 적용합니다. 질문자가 볼 수 있는 대화 키(room:번호, dm:사번|사번)
//          집합 밖의 청크는 BM25·벡터 후보에도, 프롬프트에도, 점수에도 들어오지 않습니다. 평가 도구가 못 보는 방을 묻는 질문으로 누출 0건을 확인하고,
//          "모든 방 허용" 대조군에서는 정답이 나오는 것을 함께 보여서 검사가 의미 있다는 것을 증명합니다.
//   Q2. 왜 외부 API(ChatGPT 등) 대신 로컬 LLM인가요?
//       A. 사내 메신저 대화는 외부 반출이 어려운 데이터(인사·계약·고객 정보)가 많습니다. Ollama를 같은 서버에서 돌려 대화가 PC 밖으로 나가지 않게 했고,
//          대신 모델이 작아(3B급) 답변 품질과 속도는 클라우드 모델보다 떨어진다는 트레이드오프를 인정하고, 근거 인용과 "모르면 거절"로 보완했습니다.
//   Q3. GPU가 없는/약한 PC에서 어떻게 돌리나요?
//       A. CPU 전용(num_gpu = 0)으로 돌립니다. 구형 GPU는 CUDA 오류로 Ollama가 죽는 것을 직접 겪었습니다. 임베딩은 내용 해시로 캐시해 안 바뀐 대화는
//          다시 계산하지 않고, 질문은 한 번에 하나씩 처리하며 대기열·간격 제한으로 서버가 독점되지 않게 했습니다.
//   Q4. 한계는요? (솔직하게 답하기)
//       A. (1) 방 검색은 "멤버가 된 시각 이후만" 규칙이 없다 — 기존 대화 검색과 같은 한계로, 입장 전 대화가 근거로 나올 수 있다(수정 후보).
//          (2) 색인은 최근 N개를 주기적으로 다시 만든다(수정·삭제 즉시 반영 아님, 최대 RefreshMinutes 지연; 삭제는 다음 갱신 때 빠짐).
//          (3) 첨부 파일 내용은 색인하지 않는다. (4) 3B 모델은 긴 추론·계산에 약하고 한국어 문장이 어색할 수 있다. (5) 질문 내용은 감사 로그에 남기지 않는다(글자 수만 서버 로그).
// ───────────────────────────────────────────────────────────────────────────

/// <summary>대화 기록 질의응답 AI 비서 (로컬 LLM + RAG).</summary>
public sealed class AiAssistantService : IDisposable
{
    private readonly AiConfig _config;
    private readonly Func<int, IReadOnlyList<MessageRepository.IndexLine>> _loadLines;
    private readonly Func<int, string?> _roomName;
    private readonly Func<string, IReadOnlyCollection<int>> _memberRoomIds;
    private readonly IEmbedder _embedder;
    private readonly IChatModel _chat;
    private readonly string _embedModel;
    private readonly EmbeddingCache _cache;
    private readonly ServerLogger? _log;

    private readonly SemaphoreSlim _gate = new(1, 1);                 // 임베딩·추론은 한 번에 하나
    private readonly ConcurrentDictionary<string, long> _lastAsk = new();
    private int _pending;                                              // 처리 중 + 대기 중인 질문 수

    private RagIndex? _index;
    private DateTime _builtAtUtc;
    private Dictionary<string, string> _names = [];                    // 사원번호 → 표시 이름 (1:1 출처 이름용)

    public bool Enabled => _config.Enabled;

    /// <summary>운영용: 설정에서 Ollama 클라이언트를 만든다.</summary>
    public static AiAssistantService Create(AiConfig config, MessageRepository repository, ChatApp.Server.Core.RoomManager rooms, ServerLogger log)
    {
        var ollama = new OllamaClient(config.EmbedModel, config.ChatModel, config.OllamaUrl) { UseGpu = config.UseGpu };
        string cachePath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, config.CacheFile));
        if (config.Enabled)
            log.Info($"AI 비서 켜짐: 임베딩 {config.EmbedModel}, 답변 {config.ChatModel}, {(config.UseGpu ? "GPU 사용" : "CPU 전용")} ({config.OllamaUrl})");
        return new AiAssistantService(config, repository.LoadForIndex, rooms.GetName, rooms.GetMemberRoomIds, ollama, ollama,
                                      config.EmbedModel, new EmbeddingCache(cachePath), log);
    }

    /// <summary>테스트용 포함 모든 부품을 받는 생성자 (DB·Ollama 없이도 동작을 확인할 수 있다).</summary>
    public AiAssistantService(AiConfig config, Func<int, IReadOnlyList<MessageRepository.IndexLine>> loadLines, Func<int, string?> roomName,
                              Func<string, IReadOnlyCollection<int>> memberRoomIds, IEmbedder embedder, IChatModel chat,
                              string embedModel, EmbeddingCache cache, ServerLogger? log = null)
    {
        _config = config;
        _loadLines = loadLines;
        _roomName = roomName;
        _memberRoomIds = memberRoomIds;
        _embedder = embedder;
        _chat = chat;
        _embedModel = embedModel;
        _cache = cache;
        _log = log;
    }

    // ───────── 패킷 처리 ─────────

    /// <summary>AiAskReq 처리. 검사를 통과하면 별도 작업으로 넘기고 바로 돌아온다(답은 나중에 AiAskRes로 보낸다).</summary>
    public void HandleAsk(ClientSession session, Packet packet)
    {
        if (!_config.Enabled)
        {
            Fail(session, "AI 비서가 꺼져 있습니다. 서버 관리자에게 문의하세요.");
            return;
        }
        string question = packet.GetData<AiAskRequest>()?.Question.Trim() ?? "";
        if (question.Length < AiAskRequest.MinLength || question.Length > AiAskRequest.MaxLength)
        {
            Fail(session, $"질문은 {AiAskRequest.MinLength}~{AiAskRequest.MaxLength}자로 입력하세요.");
            return;
        }

        string me = session.User!.EmployeeNo;
        // 사람당 최소 간격: 연달아 눌러 CPU를 독점하지 못하게 (ConcurrentDictionary라 두 스레드가 같은 사람을 동시에 처리해도 안전)
        long now = Environment.TickCount64;
        long last = _lastAsk.GetOrAdd(me, 0);
        if (now - last < _config.CooldownSeconds * 1000L)
        {
            Fail(session, $"질문은 {_config.CooldownSeconds}초에 한 번만 할 수 있습니다.");
            return;
        }
        _lastAsk[me] = now;

        // 대기열 제한: 먼저 올리고 확인해야 두 요청이 동시에 와도 한도를 넘지 않는다
        if (Interlocked.Increment(ref _pending) > _config.MaxQueue)
        {
            Interlocked.Decrement(ref _pending);
            Fail(session, "AI 비서가 다른 질문을 처리 중입니다. 잠시 후 다시 질문해 주세요.");
            return;
        }

        // 권한 범위는 "질문한 순간"의 멤버 방으로 고정한다 (수신 스레드에서 지금 계산)
        IReadOnlyCollection<int> roomIds = _memberRoomIds(me).ToList();
        _log?.Info($"AI 질문 {me} ({question.Length}자)");
        _ = Task.Run(async () =>
        {
            try
            {
                AiAnswer answer = await AskAsync(me, roomIds, question);
                session.Send(Packet.Create(PacketType.AiAskRes).WithData(answer));
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
            {
                _log?.Error($"AI 비서: Ollama 호출 실패 - {ex.Message}");
                Fail(session, "AI 서버(Ollama)에 연결하지 못했습니다. 서버 관리자에게 문의하세요.");
            }
            catch (Exception ex)
            {
                // 한 사람의 실패가 서버를 죽이지 않게 마지막 방어선. 원인은 서버 로그에만 남기고 사용자에게는 일반 문구.
                _log?.Error($"AI 비서 오류: {ex}");
                Fail(session, "AI 비서에서 오류가 났습니다.");
            }
            finally
            {
                Interlocked.Decrement(ref _pending);
            }
        });
    }

    // ───────── 핵심 로직 (패킷과 무관 → 테스트 가능) ─────────

    /// <summary>
    /// 질문에 답한다. me가 볼 수 있는 대화(roomIds의 방 + 본인이 낀 1:1)만 근거로 쓴다.
    /// </summary>
    public async Task<AiAnswer> AskAsync(string me, IReadOnlyCollection<int> roomIds, string question, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var timer = Stopwatch.StartNew();
            RagIndex index = await GetIndexAsync(ct);
            IReadOnlySet<string> allowed = AllowedKeys(me, roomIds, index.Chunks.Select(c => c.ConversationKey));

            var assistant = new RagAssistant(index, _chat, new RagOptions { MinCosine = _config.MinCosine, VerifyEvidence = _config.VerifyEvidence });
            RagAnswer result = await assistant.AskAsync(question, allowed, ct);

            return new AiAnswer
            {
                Question = question,
                Text = result.Text,
                Grounded = result.Grounded,
                ElapsedSeconds = Math.Round(timer.Elapsed.TotalSeconds, 1),
                Sources = result.Sources.Select(h => new AiSource
                {
                    Title = TitleOf(h.Chunk.ConversationKey, me),
                    Excerpt = ChatApp.Common.Utils.TextUtil.Truncate(h.Chunk.Text, 400),
                    Time = h.Chunk.Start,
                }).ToList(),
            };
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>색인이 없거나 오래됐으면 DB의 최근 대화로 새로 만든다. (_gate 안에서만 부른다)</summary>
    private async Task<RagIndex> GetIndexAsync(CancellationToken ct)
    {
        if (_index is not null && DateTime.UtcNow - _builtAtUtc < TimeSpan.FromMinutes(Math.Max(1, _config.RefreshMinutes))) return _index;

        IReadOnlyList<MessageRepository.IndexLine> rows = _loadLines(Math.Max(100, _config.MaxIndexedMessages));
        _names = rows.GroupBy(r => r.SenderNo).ToDictionary(g => g.Key, g => g.Last().SenderName);
        List<ChatLine> lines = rows.Select(r => new ChatLine(r.Id, KeyOf(r), r.SenderName, r.Content, r.SentAt)).ToList();
        List<Chunk> chunks = Chunker.Build(lines);

        var timer = Stopwatch.StartNew();
        _index = await RagIndex.BuildAsync(chunks, _embedder, _embedModel, _cache, ct);
        _builtAtUtc = DateTime.UtcNow;
        _log?.Info($"AI 색인 갱신: 메시지 {lines.Count}개 → 청크 {chunks.Count}개 ({timer.Elapsed.TotalSeconds:F1}초)");
        return _index;
    }

    // ───────── 대화 키 (누가 볼 수 있는가의 단위) ─────────

    /// <summary>방은 "room:번호", 1:1은 두 사원번호를 정렬해 "dm:작은|큰" (보낸 방향과 상관없이 같은 대화가 같은 키).</summary>
    public static string KeyOf(MessageRepository.IndexLine row)
        => row.RoomId is int roomId ? $"room:{roomId}" : DmKey(row.SenderNo, row.ReceiverNo ?? "");

    public static string DmKey(string a, string b)
        => string.CompareOrdinal(a, b) <= 0 ? $"dm:{a}|{b}" : $"dm:{b}|{a}";

    /// <summary>
    /// me가 볼 수 있는 대화 키 집합: 멤버인 방 + 본인이 한쪽인 1:1.
    /// 키 모양이 이상하면(사원번호에 '|'가 들어가 쪼개지는 등) 누구에게도 허용하지 않는다(fail closed: 의심스러우면 막는다).
    /// </summary>
    public static HashSet<string> AllowedKeys(string me, IEnumerable<int> roomIds, IEnumerable<string> allKeys)
    {
        var allowed = roomIds.Select(id => $"room:{id}").ToHashSet();
        foreach (string key in allKeys)
        {
            if (!key.StartsWith("dm:", StringComparison.Ordinal)) continue;
            string[] parts = key[3..].Split('|');
            if (parts.Length == 2 && (parts[0] == me || parts[1] == me)) allowed.Add(key);
        }
        return allowed;
    }

    private string TitleOf(string key, string me)
    {
        if (key.StartsWith("room:", StringComparison.Ordinal) && int.TryParse(key[5..], out int roomId))
            return _roomName(roomId) ?? $"방 {roomId}";
        string[] parts = key.StartsWith("dm:", StringComparison.Ordinal) ? key[3..].Split('|') : [];
        if (parts.Length == 2)
        {
            string peer = parts[0] == me ? parts[1] : parts[0];
            return $"1:1 대화 ({_names.GetValueOrDefault(peer) ?? peer})";
        }
        return key;
    }

    private static void Fail(ClientSession session, string message)
        => session.Send(Packet.Create(PacketType.AiAskRes, message, success: false));

    public void Dispose() => _gate.Dispose();
}
