using System.Text;
using System.Text.RegularExpressions;

namespace ChatApp.Rag;

public sealed class RagOptions
{
    /// <summary>LLM에 넘길 근거 청크 수. 작은 모델은 많이 줄수록 오히려 헷갈려 하므로 적게.</summary>
    public int TopK { get; init; } = 4;

    /// <summary>이 값보다 질문과의 의미 유사도(코사인)가 낮은 청크는 근거로 쓰지 않는다. 모델마다 달라 평가 도구로 맞춘다.</summary>
    public double MinCosine { get; init; } = 0.0;

    /// <summary>프롬프트에 넣는 근거 글자 수 상한 (CPU에서는 입력이 길수록 느려진다).</summary>
    public int MaxContextChars { get; init; } = 3000;

    /// <summary>
    /// true면 답을 만들기 전에 "이 근거에 질문의 답이 직접 적혀 있는가?"를 LLM에게 예/아니오로 먼저 묻고, 아니오면 답하지 않는다.
    /// [왜] 3B급 작은 모델은 관련 없는 근거를 받아도 그럴듯한 답을 지어낸다(실측: 연봉 질문에 근거 없이 "3.5%"). 코사인 유사도 하나로는
    ///      "답 있는 질문"과 "답 없는 질문"이 겹쳐 걸러지지 않아서, 생성 전에 한 번 더 확인한다. LLM 호출이 1번 늘지만 CPU에서도 1~2초 수준이다.
    /// </summary>
    public bool VerifyEvidence { get; init; } = true;
}

/// <summary>
/// 질문 → 검색(권한 필터 적용) → 근거만 보고 답하게 하는 프롬프트 → 답변 + 출처.
/// 근거가 없으면 LLM을 부르지 않고 "찾을 수 없다"고 답한다(환각 방지 + 시간 절약).
/// </summary>
public sealed class RagAssistant(RagIndex index, IChatModel model, RagOptions? options = null)
{
    public const string NotFound = "관련된 대화 기록을 찾을 수 없습니다.";

    private readonly RagOptions _opt = options ?? new RagOptions();

    private const string SystemPrompt = """
        당신은 사내 메신저의 대화 기록을 근거로 질문에 답하는 비서입니다.
        규칙:
        1. 반드시 아래 [근거]에 적힌 내용만 사용해 한국어로 짧고 정확하게 답하세요.
        2. [근거]에 답이 없으면 추측하지 말고 "대화 기록에서 찾을 수 없습니다"라고만 답하세요.
        3. 답에 사용한 근거의 번호를 문장 끝에 [1], [2] 형태로 붙이세요.
        4. 날짜, 숫자, 사람 이름은 [근거]에 적힌 그대로 쓰세요.
        """;

    /// <summary>
    /// 검증용 프롬프트. 출력 형식을 "판정: 예/아니오"로 고정하고 예시를 붙였다.
    /// [왜] 처음에는 "예 또는 아니오 한 단어만"이라고만 시켰는데, 3B 모델이 "232"처럼 질문의 답을 그대로 말해 버려
    ///      답이 있는 질문의 대부분이 거절됐다(실측: 20개 중 15개). 형식을 고정하고 예시를 주면 지시를 따르는 비율이 올라간다.
    /// </summary>
    public const string VerifierPrompt = """
        당신은 검증자입니다. [근거]를 읽고, 질문에 대한 답이 [근거]에 들어 있는지 판정하세요.
        출력은 반드시 한 줄이며 "판정: 예" 또는 "판정: 아니오" 형식이어야 합니다. 질문에 답하지 마세요.
        - 질문에 답이 되는 내용(숫자, 날짜, 이름, 규칙 등)이 [근거]에 적혀 있으면 "판정: 예"입니다. "이번 주 금요일"처럼 상대적인 표현이어도 답이 적혀 있으면 예입니다.
        - 질문과 주제만 비슷할 뿐 답이 없거나, 일반 상식으로 짐작해야 하면 "판정: 아니오"입니다.

        예시 1) [근거] 배포는 29일 밤 10시입니다. / 질문: 배포 언제 해? → 판정: 예
        예시 2) [근거] 점심은 칼국수를 먹었습니다. / 질문: 올해 회사 매출은 얼마야? → 판정: 아니오
        예시 3) [근거] 회의는 다음 주 화요일 3시입니다. / 질문: 회의 장소는 어디야? → 판정: 아니오
        """;

    // "판정: 예"일 때만 통과한다. 형식을 안 지킨 답은 첫 단어가 "예"/"네"/"yes"인 경우만 통과시키고, 나머지(숫자만 말함, 빈 답, "예상..." 등)는 거절한다.
    // 의심스러우면 답하지 않는 쪽이 안전하다(fail closed).
    internal static bool IsYes(string verdict)
    {
        Match tagged = Regex.Match(verdict, @"판정\s*[:：]\s*(\p{L}+)");
        string word = tagged.Success ? tagged.Groups[1].Value : Regex.Match(verdict, @"^[^\p{L}]*(\p{L}+)").Groups[1].Value;
        return word.ToLowerInvariant() is "예" or "네" or "yes";
    }
    public async Task<RagAnswer> AskAsync(string question, IReadOnlySet<string> allowed, CancellationToken ct = default)
    {
        List<Hit> hits = await index.SearchAsync(question, allowed, _opt.TopK, SearchMode.Hybrid, ct);
        // 키워드 전용 색인(Cosine = 0)일 때는 유사도 기준을 적용할 수 없으므로 그대로 쓴다
        var evidence = hits.Where(h => h.Cosine == 0 || h.Cosine >= _opt.MinCosine).ToList();
        if (evidence.Count == 0) return new RagAnswer(NotFound, [], Grounded: false);

        // 근거를 번호 붙여 프롬프트에 넣는다. 글자 수 상한을 넘으면 뒤쪽 근거를 버린다(순위 높은 것 우선).
        var used = new List<Hit>();
        var context = new StringBuilder();
        foreach (Hit h in evidence)
        {
            string block = $"[{used.Count + 1}]\n{h.Chunk.Text}\n\n";
            if (used.Count > 0 && context.Length + block.Length > _opt.MaxContextChars) break;
            context.Append(block);
            used.Add(h);
        }

        string prompt = $"[근거]\n{context}질문: {question}";

        // 근거에 답이 직접 있는지 먼저 확인한다 (없으면 지어내지 않고 거절)
        if (_opt.VerifyEvidence)
        {
            string verdict = await model.CompleteAsync(VerifierPrompt, prompt, ct);
            if (!IsYes(verdict)) return new RagAnswer(NotFound, [], Grounded: false);
        }

        string answer = await model.CompleteAsync(SystemPrompt, prompt, ct);
        if (string.IsNullOrWhiteSpace(answer)) return new RagAnswer(NotFound, [], Grounded: false);

        // 모델이 인용한 번호만 출처로 남긴다. 존재하지 않는 번호([9])는 버린다(없는 출처를 지어내는 환각 방지).
        var cited = Regex.Matches(answer, @"\[(\d+)\]").Select(m => int.Parse(m.Groups[1].Value))
                         .Where(n => n >= 1 && n <= used.Count).Distinct().OrderBy(n => n).Select(n => used[n - 1]).ToList();
        bool refused = answer.Contains("찾을 수 없습니다");
        return new RagAnswer(answer, cited.Count > 0 ? cited : used, Grounded: !refused);
    }
}
