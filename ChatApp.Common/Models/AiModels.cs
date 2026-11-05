namespace ChatApp.Common.Models;

// ─────────────────────────────────────────────────────────────────────────────
// AI 비서(대화 기록 질의응답). 서버의 로컬 LLM이 "내가 볼 수 있는 대화"만 근거로 답한다.
// 흐름: 클라이언트 AiAskReq(Data = AiAskRequest) → 서버 AiAskRes(Data = AiAnswer)
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>AI 비서에게 하는 질문.</summary>
public sealed class AiAskRequest
{
    /// <summary>질문 최소 글자 수.</summary>
    public const int MinLength = 2;
    /// <summary>질문 최대 글자 수 (CPU 추론은 입력이 길수록 느려진다).</summary>
    public const int MaxLength = 300;

    public string Question { get; set; } = "";
}

/// <summary>답변과 그 근거.</summary>
public sealed class AiAnswer
{
    public string Question { get; set; } = "";

    /// <summary>답변 본문. 근거를 못 찾았으면 "관련된 대화 기록을 찾을 수 없습니다."</summary>
    public string Text { get; set; } = "";

    /// <summary>false면 대화 기록에서 근거를 찾지 못한 것 (답변은 안내 문구).</summary>
    public bool Grounded { get; set; }

    /// <summary>답변에 쓴 대화 조각. 사용자가 직접 확인할 수 있게 함께 보여 준다.</summary>
    public List<AiSource> Sources { get; set; } = [];

    /// <summary>답을 만드는 데 걸린 시간(초).</summary>
    public double ElapsedSeconds { get; set; }
}

/// <summary>근거로 쓴 대화 조각 하나.</summary>
public sealed class AiSource
{
    /// <summary>방 이름 또는 "1:1 대화 (상대 이름)".</summary>
    public string Title { get; set; } = "";

    /// <summary>대화 조각 본문 ("[시각] 이름: 내용" 줄들).</summary>
    public string Excerpt { get; set; } = "";

    public DateTime Time { get; set; }
}
