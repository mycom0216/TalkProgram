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
    /// <summary>답장 미리보기에 담는 원문의 최대 글자 수(60).</summary>
    public const int MaxPreviewLength = 60;

    /// <summary>원래 메시지 번호.</summary>
    public long MessageId { get; set; }
    /// <summary>원래 메시지를 보낸 사람의 표시 이름.</summary>
    public string SenderName { get; set; } = "";
    /// <summary>원문 앞부분(MaxPreviewLength자). 삭제된 메시지면 "삭제된 메시지입니다."</summary>
    public string Text { get; set; } = "";
}

// ===============
/// <summary>메시지 하나를 가리킨다 (삭제 요청·삭제 알림). 어느 대화의 메시지인지도 함께 담는다.</summary>
public sealed class MessageRef
{
    /// <summary>가리킬 메시지 번호.</summary>
    public long MessageId { get; set; }
    /// <summary>방 메시지면 방 번호. 1:1이면 null.</summary>
    public int? RoomId { get; set; }
    /// <summary>1:1이면 "받는 사람 입장에서의" 상대 사원번호. 방이면 null.</summary>
    public string? PeerNo { get; set; }
}

// ===============
/// <summary>기존 메시지의 글을 바꾸어 달라는 요청입니다.</summary>
public sealed class EditMessageRequest
{
    /// <summary>고칠 메시지 번호.</summary>
    public long MessageId { get; set; }
    /// <summary>새 내용.</summary>
    public string Text { get; set; } = "";
}

// ===============
/// <summary>사용자가 지금 글을 입력 중인지 알려 달라는 요청입니다.</summary>
public sealed class TypingRequest
{
    /// <summary>방에서 입력 중이면 방 번호. 1:1이면 null.</summary>
    public int? RoomId { get; set; }
    /// <summary>1:1에서 입력 중임을 알릴 상대 사원번호. 방이면 null.</summary>
    public string? PeerNo { get; set; }
    /// <summary>true = 입력 중, false = 입력을 멈춤.</summary>
    public bool IsTyping { get; set; }
}

// ===============
/// <summary>대화 상대에게 보여 줄 입력 중 상태 정보입니다.</summary>
public sealed class TypingInfo
{
    /// <summary>방에서 입력 중이면 방 번호. 1:1이면 null.</summary>
    public int? RoomId { get; set; }
    /// <summary>1:1이면 입력 중인 사람(받는 사람 입장에서의 상대) 사원번호. 방이면 null.</summary>
    public string? PeerNo { get; set; }
    /// <summary>입력 중인 사람의 사원번호.</summary>
    public string EmployeeNo { get; set; } = "";
    /// <summary>입력 중인 사람의 표시 이름.</summary>
    public string DisplayName { get; set; } = "";
    /// <summary>true = 입력 중, false = 입력을 멈춤.</summary>
    public bool IsTyping { get; set; }
}

// ===============
/// <summary>한 가지 공감(이모지)과 누른 사람들.</summary>
public sealed class ReactionInfo
{
    /// <summary>공감 이모지.</summary>
    public string Emoji { get; set; } = "";
    /// <summary>이 공감을 누른 사람들의 사원번호 목록.</summary>
    public List<string> EmployeeNos { get; set; } = [];
}

// ===============
/// <summary>공감 누르기/취소 요청.</summary>
public sealed class ReactionRequest
{
// ===============
    /// <summary>고를 수 있는 공감. 아무 글자나 받지 않는 이유: 화면을 깨는 긴 문자열·이상한 문자를 막기 위해.</summary>
    public static readonly string[] Allowed = ["👍", "❤️", "😂", "😮", "😢", "🙏"];

    /// <summary>공감을 누르거나 취소할 메시지 번호.</summary>
    public long MessageId { get; set; }
    /// <summary>공감 이모지(Allowed 중 하나). 같은 공감을 다시 보내면 취소된다.</summary>
    public string Emoji { get; set; } = "";
}

// ===============
/// <summary>메시지의 공감 현황 (공감이 바뀔 때마다 그 대화의 사람들에게 보낸다).</summary>
public sealed class ReactionUpdate
{
    /// <summary>공감이 바뀐 메시지 번호.</summary>
    public long MessageId { get; set; }
    /// <summary>방 메시지면 방 번호. 1:1이면 null.</summary>
    public int? RoomId { get; set; }
    /// <summary>1:1이면 받는 사람 입장에서의 상대 사원번호. 방이면 null.</summary>
    public string? PeerNo { get; set; }
    /// <summary>이모지별 공감 현황(이모지와 누른 사람들).</summary>
    public List<ReactionInfo> Reactions { get; set; } = [];
}

// ===============
/// <summary>"이 대화를 여기(UpTo 시각)까지 읽었다" (클라이언트 → 서버).</summary>
public sealed class ReadMark
{
    /// <summary>읽은 대화가 방이면 방 번호. 1:1이면 null.</summary>
    public int? RoomId { get; set; }
    /// <summary>읽은 대화가 1:1이면 상대 사원번호. 방이면 null.</summary>
    public string? PeerNo { get; set; }
    /// <summary>이 시각까지의 메시지를 읽었다는 표시.</summary>
    public DateTime UpTo { get; set; }
}

// ===============
/// <summary>누가 어디까지 읽었는지 (서버 → 그 대화의 사람들).</summary>
public sealed class ReadUpdate
{
    /// <summary>방이면 방 번호. 1:1이면 null.</summary>
    public int? RoomId { get; set; }
    /// <summary>1:1이면 받는 사람 입장에서의 상대(= 읽은 사람) 사원번호. 방이면 null.</summary>
    public string? PeerNo { get; set; }
    /// <summary>읽은 사람의 사원번호.</summary>
    public string EmployeeNo { get; set; } = "";
    /// <summary>그 사람이 마지막으로 읽은 시각.</summary>
    public DateTime LastReadAt { get; set; }
}

// ===============
/// <summary>로그인했을 때 알려 주는 "안 읽은 1:1 메시지" (보낸 사람별).</summary>
public sealed class UnreadDirect
{
    /// <summary>메시지를 보낸 상대의 사원번호.</summary>
    public string PeerNo { get; set; } = "";
    /// <summary>상대의 표시 이름.</summary>
    public string PeerName { get; set; } = "";
    /// <summary>안 읽은 메시지 수.</summary>
    public int Count { get; set; }
    /// <summary>가장 최근 메시지의 미리보기. 삭제된 메시지면 빈 문자열.</summary>
    public string LastText { get; set; } = "";
    /// <summary>가장 최근 메시지의 시각.</summary>
    public DateTime LastTime { get; set; }
}

// ===============
/// <summary>서버 대화 검색 요청.</summary>
public sealed class SearchRequest
{
    /// <summary>검색어 최소 글자 수(2).</summary>
    public const int MinKeywordLength = 2;
    /// <summary>한 번에 돌려주는 검색 결과의 최대 개수(100).</summary>
    public const int MaxResults = 100;

    /// <summary>찾을 단어.</summary>
    public string Keyword { get; set; } = "";
}

// ===============
/// <summary>검색 결과 한 줄.</summary>
public sealed class SearchHit
{
    /// <summary>일치한 메시지 번호.</summary>
    public long MessageId { get; set; }
    /// <summary>방 메시지면 방 번호. 1:1이면 null.</summary>
    public int? RoomId { get; set; }
    /// <summary>방 메시지면 방 이름. 1:1이면 null.</summary>
    public string? RoomName { get; set; }
    /// <summary>1:1이면 나 말고 상대 사원번호. 방이면 null.</summary>
    public string? PeerNo { get; set; }
    /// <summary>1:1이면 상대의 표시 이름. 방이면 null.</summary>
    public string? PeerName { get; set; }
    /// <summary>보낸 사람 사원번호.</summary>
    public string SenderNo { get; set; } = "";
    /// <summary>보낸 사람 표시 이름.</summary>
    public string SenderName { get; set; } = "";
    /// <summary>메시지 내용.</summary>
    public string Body { get; set; } = "";
    /// <summary>메시지를 보낸 시각(UTC).</summary>
    public DateTime Time { get; set; }
}

// ===============
/// <summary>검색한 단어와 그 단어가 들어 있는 메시지들의 목록입니다.</summary>
public sealed class SearchResult
{
    /// <summary>검색에 쓴 단어.</summary>
    public string Keyword { get; set; } = "";
    /// <summary>그 단어가 들어 있는 메시지 목록.</summary>
    public List<SearchHit> Hits { get; set; } = [];
}
