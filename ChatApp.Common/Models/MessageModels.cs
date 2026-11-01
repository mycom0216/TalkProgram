namespace ChatApp.Common.Models;

// ─────────────────────────────────────────────────────────────────────────────
// [파일 역할] 메시지 하나하나에 붙는 기능(답장·삭제·공감·읽음 표시·검색)에 쓰는 데이터 모양.
// [핵심] 메시지 ID: 서버가 메시지를 받는 순간 번호(MessageId)를 붙여 모두에게 보낸다.
//        그래야 "이 메시지에 답장", "이 메시지 삭제", "이 메시지에 공감"처럼 특정 메시지를 가리킬 수 있다.
//        (예전에는 보낸 사람+내용+시각으로 같은 메시지인지 짐작했다)
// ─────────────────────────────────────────────────────────────────────────────

// ===============
/// <summary>답장 대상 메시지의 요약 (말풍선 위에 인용으로 보여 준다).</summary>
public sealed class ReplyInfo
{
    public const int MaxPreviewLength = 60;

    public long MessageId { get; set; }
    public string SenderName { get; set; } = "";
    public string Text { get; set; } = "";       // 원문 앞부분 (MaxPreviewLength자)
}

// ===============
/// <summary>메시지 하나를 가리킨다 (삭제 요청·삭제 알림). 어느 대화의 메시지인지도 함께 담는다.</summary>
public sealed class MessageRef
{
    public long MessageId { get; set; }
    public int? RoomId { get; set; }        // 방 메시지면 방 번호
    public string? PeerNo { get; set; }     // 1:1이면 "받는 사람 입장에서의" 상대 사원번호
}

// ===============
/// <summary>기존 메시지의 글을 바꾸어 달라는 요청입니다.</summary>
public sealed class EditMessageRequest
{
    public long MessageId { get; set; }
    public string Text { get; set; } = "";
}

// ===============
/// <summary>사용자가 지금 글을 입력 중인지 알려 달라는 요청입니다.</summary>
public sealed class TypingRequest
{
    public int? RoomId { get; set; }
    public string? PeerNo { get; set; }
    public bool IsTyping { get; set; }
}

// ===============
/// <summary>대화 상대에게 보여 줄 입력 중 상태 정보입니다.</summary>
public sealed class TypingInfo
{
    public int? RoomId { get; set; }
    public string? PeerNo { get; set; }
    public string EmployeeNo { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public bool IsTyping { get; set; }
}

// ===============
/// <summary>한 가지 공감(이모지)과 누른 사람들.</summary>
public sealed class ReactionInfo
{
    public string Emoji { get; set; } = "";
    public List<string> EmployeeNos { get; set; } = [];
}

// ===============
/// <summary>공감 누르기/취소 요청.</summary>
public sealed class ReactionRequest
{
// ===============
    /// <summary>고를 수 있는 공감. 아무 글자나 받지 않는 이유: 화면을 깨는 긴 문자열·이상한 문자를 막기 위해.</summary>
    public static readonly string[] Allowed = ["👍", "❤️", "😂", "😮", "😢", "🙏"];

    public long MessageId { get; set; }
    public string Emoji { get; set; } = "";
}

// ===============
/// <summary>메시지의 공감 현황 (공감이 바뀔 때마다 그 대화의 사람들에게 보낸다).</summary>
public sealed class ReactionUpdate
{
    public long MessageId { get; set; }
    public int? RoomId { get; set; }
    public string? PeerNo { get; set; }
    public List<ReactionInfo> Reactions { get; set; } = [];
}

// ===============
/// <summary>"이 대화를 여기(UpTo 시각)까지 읽었다" (클라이언트 → 서버).</summary>
public sealed class ReadMark
{
    public int? RoomId { get; set; }
    public string? PeerNo { get; set; }
    public DateTime UpTo { get; set; }
}

// ===============
/// <summary>누가 어디까지 읽었는지 (서버 → 그 대화의 사람들).</summary>
public sealed class ReadUpdate
{
    public int? RoomId { get; set; }
    public string? PeerNo { get; set; }     // 1:1이면 받는 사람 입장에서의 상대(= 읽은 사람)
    public string EmployeeNo { get; set; } = "";
    public DateTime LastReadAt { get; set; }
}

// ===============
/// <summary>로그인했을 때 알려 주는 "안 읽은 1:1 메시지" (보낸 사람별).</summary>
public sealed class UnreadDirect
{
    public string PeerNo { get; set; } = "";
    public string PeerName { get; set; } = "";
    public int Count { get; set; }
    public string LastText { get; set; } = "";
    public DateTime LastTime { get; set; }
}

// ===============
/// <summary>서버 대화 검색 요청.</summary>
public sealed class SearchRequest
{
    public const int MinKeywordLength = 2;
    public const int MaxResults = 100;

    public string Keyword { get; set; } = "";
}

// ===============
/// <summary>검색 결과 한 줄.</summary>
public sealed class SearchHit
{
    public long MessageId { get; set; }
    public int? RoomId { get; set; }
    public string? RoomName { get; set; }
    public string? PeerNo { get; set; }          // 1:1이면 나 말고 상대
    public string? PeerName { get; set; }
    public string SenderNo { get; set; } = "";
    public string SenderName { get; set; } = "";
    public string Body { get; set; } = "";
    public DateTime Time { get; set; }
}

// ===============
/// <summary>검색한 단어와 그 단어가 들어 있는 메시지들의 목록입니다.</summary>
public sealed class SearchResult
{
    public string Keyword { get; set; } = "";
    public List<SearchHit> Hits { get; set; } = [];
}
