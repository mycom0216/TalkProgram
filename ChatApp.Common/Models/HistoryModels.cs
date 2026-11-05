namespace ChatApp.Common.Models;

// ─────────────────────────────────────────────────────────────────────────────
// [파일 역할] "지난 대화 불러오기"에 쓰는 데이터 모양(DTO).
//   - HistoryItem : DB에서 읽은 지난 메시지 1개 (채팅, 귓속말, 파일)
//   - HistoryBatch: 그 메시지 묶음 (History 패킷 하나에 담기는 단위)
// [연결 관계] 서버 MessageRepository(DB 읽기) → MessageStore(DB 작업 스레드, 묶음으로 나눠 전송)
//   → 클라이언트 MainForm.HandleHistory → Conversation.InsertHistory(대화 맨 앞에 끼워 넣기)
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>지난 메시지 종류.</summary>
public enum HistoryKind
{
    /// <summary>방 채팅 메시지.</summary>
    Chat,
    /// <summary>1:1 대화 메시지.</summary>
    Whisper,
    /// <summary>파일 메시지 (File 속성에 파일 정보가 들어 있다).</summary>
    File,
}

/// <summary>DB에서 불러온 지난 메시지 1개.</summary>
public sealed class HistoryItem
{
    /// <summary>메시지 종류(일반 채팅 / 귓속말 / 파일).</summary>
    public HistoryKind Kind { get; set; }
    /// <summary>보낸 사람 사원번호.</summary>
    public string SenderNo { get; set; } = "";
    /// <summary>보낸 사람 표시 이름. 예: "홍길동 대리" (DB의 users 테이블과 JOIN해서 얻음).</summary>
    public string SenderName { get; set; } = "";
    /// <summary>메시지 내용.</summary>
    public string Body { get; set; } = "";
    /// <summary>메시지를 보낸 시각. 서버가 밀리초 단위로 잘라 보낸다.</summary>
    // [주의] 서버는 실시간 메시지 시각을 밀리초 단위로 잘라서 보낸다(DB가 밀리초까지만 저장하므로).
    //   그래야 클라이언트가 "실시간으로 받은 것"과 "기록에서 불러온 것"이 같은 메시지인지 시각으로 비교할 수 있다.
    public DateTime Time { get; set; }

    /// <summary>Kind == File일 때 파일 정보(미리보기 포함).</summary>
    public SharedFile? File { get; set; }

    /// <summary>메시지 번호 (답장·삭제·공감에 쓴다). 파일 메시지는 0.</summary>
    public long MessageId { get; set; }

    /// <summary>답장이면 원래 메시지 요약.</summary>
    public ReplyInfo? ReplyTo { get; set; }

    /// <summary>보낸 사람이 삭제한 메시지 (Body는 비어 있다).</summary>
    public bool Deleted { get; set; }

    /// <summary>수정된 메시지인지.</summary>
    public bool Edited { get; set; }

    /// <summary>공감 현황 (없으면 null).</summary>
    public List<ReactionInfo>? Reactions { get; set; }
}

/// <summary>
/// 지난 대화 묶음 (서버 → 클라이언트, History 패킷의 Data).
/// 사진 미리보기가 많으면 패킷 하나(최대 1MB)에 다 못 담으므로 여러 묶음으로 나눠 보낸다.
/// 클라이언트는 Last = true인 묶음이 올 때까지 모았다가 한꺼번에 대화 맨 앞에 끼워 넣는다.
/// </summary>
// [왜] 묶음마다 바로 화면에 넣으면 묶음 순서대로 앞에 끼우느라 순서가 뒤집히고, 화면도 여러 번 다시 그려진다.
//   마지막 묶음(Last)을 받은 뒤 한 번에 넣으면 단순하고 깔끔하다.
public sealed class HistoryBatch
{
    /// <summary>한 번에 불러오는 최대 메시지 수.</summary>
    // [왜] 몇 년치 대화를 한꺼번에 보내면 느리고 메모리도 많이 쓴다. 메신저들처럼 최근 일부만 먼저 보여 준다.
    public const int Limit = 50;

    /// <summary>방 대화면 방 번호.</summary>
    public int? RoomId { get; set; }

    /// <summary>1:1 대화면 상대 사원번호.</summary>
    public string? PeerNo { get; set; }

    /// <summary>오래된 것 → 최근 순서.</summary>
    public List<HistoryItem> Items { get; set; } = [];

    /// <summary>마지막 묶음이면 true. 기록이 하나도 없어도 Last = true인 빈 묶음이 와서 "불러오기 끝"을 알린다.</summary>
    public bool Last { get; set; } = true;

    /// <summary>이 페이지보다 오래된 기록이 더 있는지. 마지막 묶음에서만 의미가 있다.</summary>
    public bool HasMore { get; set; }

    /// <summary>1:1이면 상대가 마지막으로 읽은 시각 (내 메시지 옆 "1" 표시용). 방은 RoomInfo.Members에 있다.</summary>
    public DateTime? PeerLastReadAt { get; set; }
}

/// <summary>이전 대화 페이지 요청. 방/1:1 대상과 마지막으로 표시한 메시지 시각을 담는다.</summary>
public sealed class HistoryRequest
{
    /// <summary>지난 대화를 요청할 방 번호. 1:1이면 null.</summary>
    public int? RoomId { get; set; }
    /// <summary>1:1 대화의 상대 사원번호. 방이면 null.</summary>
    public string? PeerNo { get; set; }
    /// <summary>화면에 있는 가장 오래된 메시지의 시각. 이보다 앞선 기록을 요청한다.</summary>
    public DateTime? Before { get; set; }
    /// <summary>그 메시지의 키("M:" + 20자리 메시지 번호, 파일이면 "F:" + 파일 ID). 같은 시각의 항목을 구분한다.</summary>
    public string? BeforeKey { get; set; }
}
