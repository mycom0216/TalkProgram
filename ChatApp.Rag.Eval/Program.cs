using System.Diagnostics;
using System.Text;
using System.Text.Json;
using ChatApp.Rag;

// ─────────────────────────────────────────────────────────────────────────────
// RAG 검색 품질 평가. 같은 질문 세트로 BM25 / 벡터 / 하이브리드를 비교한다.
//   Recall@k : 정답 메시지가 들어 있는 청크가 상위 k개 안에 있는 질문의 비율
//   MRR      : 정답 청크가 처음 나온 순위의 역수 평균 (1등이면 1, 2등이면 0.5 ...)
//   권한 누출 : 못 보는 방(room:hr)의 청크가 결과에 섞인 횟수 (반드시 0)
// 주의: 질문 20개짜리 가상 데이터라 "벤치마크"가 아니라 파이프라인 점검 + 방법 간 상대 비교용이다.
// ─────────────────────────────────────────────────────────────────────────────

string embedModel = "embeddinggemma", chatModel = "kanana2:3b", url = "http://localhost:11434";
int maxChars = 300;
bool answers = false;
for (int i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--embed": embedModel = args[++i]; break;
        case "--chat": chatModel = args[++i]; break;
        case "--url": url = args[++i]; break;
        case "--chars": maxChars = int.Parse(args[++i]); break;
        case "--answers": answers = true; break;
        default: Console.Error.WriteLine($"알 수 없는 옵션: {args[i]}"); return 2;
    }
}

string dataPath = Path.Combine(AppContext.BaseDirectory, "data", "dataset.json");
var data = JsonSerializer.Deserialize<Dataset>(File.ReadAllText(dataPath), new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
var lines = data.Lines.Select(l => new ChatLine(l.Id, l.Conv, l.Sender, l.Text, l.Time)).ToList();
var visible = data.Visible.ToHashSet();
var everything = lines.Select(l => l.ConversationKey).ToHashSet();

List<Chunk> chunks = Chunker.Build(lines, maxChars);
Console.WriteLine($"대화 {lines.Count}줄 → 청크 {chunks.Count}개 (최대 {maxChars}자), 질문 {data.Questions.Count}개");
Console.WriteLine($"임베딩 모델: {embedModel} / 답변 모델: {chatModel}");

var ollama = new OllamaClient(embedModel, chatModel, url);
string cachePath = Path.Combine(Path.GetTempPath(), $"chatapp-rag-eval-{embedModel.Replace(':', '_')}.bin");
var cache = new EmbeddingCache(cachePath);

var sw = Stopwatch.StartNew();
RagIndex index;
try
{
    index = await RagIndex.BuildAsync(chunks, ollama, embedModel, cache);
}
catch (HttpRequestException ex)
{
    Console.Error.WriteLine($"Ollama에 연결하지 못했습니다 ({url}): {ex.Message}\n'ollama serve'가 켜져 있고 '{embedModel}' 모델이 설치되어 있는지 확인하세요.");
    return 1;
}
Console.WriteLine($"색인 생성 {sw.Elapsed.TotalSeconds:F1}초 (캐시된 벡터 {cache.Count}개 포함)\n");

var answerable = data.Questions.Where(q => q.Kind == "answerable").ToList();

// ── ① 검색 품질 ──────────────────────────────────────────────
Console.WriteLine($"## 검색 품질 (답 있는 질문 {answerable.Count}개, 보이는 방만 검색)");
Console.WriteLine("| 방법 | Recall@1 | Recall@3 | Recall@5 | MRR | 질문당 검색 시간 |");
Console.WriteLine("|---|---|---|---|---|---|");
var failures = new Dictionary<SearchMode, List<string>>();
foreach (SearchMode mode in Enum.GetValues<SearchMode>())
{
    int[] ks = [1, 3, 5];
    var found = new int[ks.Length];
    double rrSum = 0;
    failures[mode] = [];
    var timer = Stopwatch.StartNew();
    foreach (Question q in answerable)
    {
        List<Hit> hits = await index.SearchAsync(q.Q, visible, 5, mode);
        int firstRank = hits.FirstOrDefault(h => h.Chunk.MessageIds.Any(q.Gold.Contains))?.Rank ?? 0;
        for (int n = 0; n < ks.Length; n++) if (firstRank is > 0 && firstRank <= ks[n]) found[n]++;
        if (firstRank > 0) rrSum += 1.0 / firstRank;
        if (firstRank is 0 or > 3) failures[mode].Add($"{q.Q} (정답 순위: {(firstRank == 0 ? "5위 밖" : firstRank + "위")})");
    }
    timer.Stop();
    double n0 = answerable.Count;
    Console.WriteLine($"| {mode} | {found[0] / n0:P0} | {found[1] / n0:P0} | {found[2] / n0:P0} | {rrSum / n0:F3} | {timer.ElapsedMilliseconds / n0:F0}ms |");
}

Console.WriteLine("\n### 3위 안에 못 찾은 질문");
foreach (var (mode, list) in failures)
{
    Console.WriteLine($"- {mode}: {(list.Count == 0 ? "없음" : "")}");
    foreach (string f in list) Console.WriteLine($"    · {f}");
}

// ── ② 권한 누출 ──────────────────────────────────────────────
var restricted = data.Questions.Where(q => q.Kind == "restricted").ToList();
int leaks = 0, controlFound = 0;
foreach (Question q in restricted)
    foreach (SearchMode mode in Enum.GetValues<SearchMode>())
    {
        List<Hit> hits = await index.SearchAsync(q.Q, visible, 5, mode);
        leaks += hits.Count(h => !visible.Contains(h.Chunk.ConversationKey));
    }
// 대조군: 같은 질문을 "모든 방 허용"으로 검색하면 정답이 나와야 한다 (없으면 누출 검사가 의미 없다)
foreach (Question q in restricted)
{
    List<Hit> hits = await index.SearchAsync(q.Q, everything, 3, SearchMode.Hybrid);
    if (hits.Any(h => h.Chunk.MessageIds.Any(q.Gold.Contains))) controlFound++;
}
Console.WriteLine($"\n## 권한 필터 (못 보는 방 room:hr 대상 질문 {restricted.Count}개 × 3가지 방법)");
Console.WriteLine($"- 못 보는 방의 청크가 결과에 섞인 횟수: {leaks} (0이어야 함)");
Console.WriteLine($"- 대조군: 모든 방을 허용하면 정답이 3위 안에 나온 질문 {controlFound}/{restricted.Count} (정보가 색인에 실제로 있다는 증거)");

// ── ③ "모르면 거절" 기준 잡기 ────────────────────────────────
var unanswerable = data.Questions.Where(q => q.Kind == "unanswerable").ToList();
async Task<double> TopCosine(string question)
{
    List<Hit> hits = await index.SearchAsync(question, visible, 1, SearchMode.Vector);
    return hits.Count == 0 ? 0 : hits[0].Cosine;
}
var yes = new List<double>(); foreach (Question q in answerable) yes.Add(await TopCosine(q.Q));
var no = new List<double>(); foreach (Question q in unanswerable) no.Add(await TopCosine(q.Q));
Console.WriteLine("\n## 의미 유사도(코사인) 분포 — 근거 없음 판정 기준(MinCosine) 잡기");
Console.WriteLine($"- 답 있는 질문의 1등 코사인: 최소 {yes.Min():F3} / 평균 {yes.Average():F3}");
Console.WriteLine($"- 답 없는 질문의 1등 코사인: 최대 {no.Max():F3} / 평균 {no.Average():F3}");
if (yes.Min() > no.Max())
    Console.WriteLine($"- 두 집단이 갈라진다 → MinCosine ≈ {(yes.Min() + no.Max()) / 2:F2} 로 두면 이 질문 세트에서는 전부 맞게 거절/응답");
else
    Console.WriteLine("- 두 집단이 겹친다 → 코사인 하나로는 거절 판정이 불완전하다 (LLM의 '찾을 수 없습니다' 응답과 함께 써야 함)");

// ── ④ 답변 평가 (선택: --answers. 질문마다 LLM을 부르므로 CPU에서는 몇 분 걸린다) ──────────
// 정답 판정은 "답변에 기대 키워드가 들어 있는가"라는 거친 기준이다(expect). 사람이 읽는 것보다 엄격하거나 느슨할 수 있다.
//   답 있는 질문: 정답 / 오답(답은 했지만 키워드 없음) / 거절(답을 안 함)
//   답 없는·못 보는 질문: 올바른 거절 / 지어낸 답(환각)
if (answers)
{
    Console.WriteLine($"\n## 답변 평가 ({chatModel}, 질문 {data.Questions.Count}개, MinCosine=0.30)");
    Console.WriteLine("| 근거 검증 단계 | 답 있는 질문: 정답 | 오답 | 거절 | 답 없는/못 보는 질문: 올바른 거절 | 지어낸 답 | 질문당 평균 시간 |");
    Console.WriteLine("|---|---|---|---|---|---|---|");
    var details = new List<string>();
    foreach (bool verify in new[] { false, true })
    {
        var assistant = new RagAssistant(index, ollama, new RagOptions { MinCosine = 0.30, VerifyEvidence = verify });
        int right = 0, wrong = 0, refused = 0, refusedOk = 0, fabricated = 0;
        double seconds = 0;
        foreach (Question q in data.Questions)
        {
            var t = Stopwatch.StartNew();
            RagAnswer a = await assistant.AskAsync(q.Q, visible);
            seconds += t.Elapsed.TotalSeconds;
            bool declined = !a.Grounded;
            string tag = verify ? "검증O" : "검증X";
            if (q.Kind == "answerable")
            {
                if (declined) { refused++; details.Add($"[{tag}] 거절: {q.Q}"); }
                else if (Matches(a.Text, q.Expect)) right++;
                else { wrong++; details.Add($"[{tag}] 오답: {q.Q} → {a.Text.ReplaceLineEndings(" ")}"); }
            }
            else
            {
                if (declined) refusedOk++;
                else { fabricated++; details.Add($"[{tag}] 지어낸 답({q.Kind}): {q.Q} → {a.Text.ReplaceLineEndings(" ")}"); }
            }
        }
        int offTopic = data.Questions.Count(q => q.Kind != "answerable");
        Console.WriteLine($"| {(verify ? "켬" : "끔")} | {right}/{answerable.Count} | {wrong} | {refused} | {refusedOk}/{offTopic} | {fabricated} | {seconds / data.Questions.Count:F1}초 |");
    }
    Console.WriteLine("\n### 틀리거나 거절하거나 지어낸 경우");
    foreach (string d in details) Console.WriteLine($"- {d}");
}

// 기대 키워드 중 하나라도 만족하면 정답. "a&b"는 a와 b가 모두 들어 있어야 한다. 공백·대소문자는 무시한다.
static bool Matches(string text, List<string>? expect)
{
    if (expect is null || expect.Count == 0) return false;
    string norm = new string(text.Where(c => !char.IsWhiteSpace(c)).ToArray()).ToLowerInvariant();
    return expect.Any(e => e.Split('&').All(part => norm.Contains(new string(part.Where(c => !char.IsWhiteSpace(c)).ToArray()).ToLowerInvariant())));
}
return leaks == 0 ? 0 : 3;

// ── 데이터 형식 ──
internal sealed record Dataset(List<string> Visible, List<LineRow> Lines, List<Question> Questions);
internal sealed record LineRow(long Id, string Conv, string Sender, DateTime Time, string Text);
internal sealed record Question(string Q, List<long> Gold, string Kind, List<string>? Expect = null);
