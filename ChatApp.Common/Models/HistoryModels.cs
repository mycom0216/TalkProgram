namespace ChatApp.Common.Models;

// ─────────────────────────────────────────────────────────────────────────────
// [파일 역할] "지난 대화 불러오기"에 쓰는 데이터 모양(DTO).
//   - HistoryItem : DB에서 읽은 지난 메시지 1개 (채팅, 귓속말, 파일)
//   - HistoryBatch: 그 메시지 묶음 (History 패킷 하나에 담기는 단위)
// [연결 관계] 서버 MessageRepository(DB 읽기) → MessageStore(DB 작업 스레드, 묶음으로 나눠 전송)
//   → 클라이언트 MainForm.HandleHistory → Conversation.InsertHistory(대화 맨 앞에 끼워 넣기)
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>지난 메시지 종류.</summary>
public enum HistoryKind { Chat, Whisper, File }

/// <summary>DB에서 불러온 지난 메시지 1개.</summary>
public sealed class HistoryItem
{
    public HistoryKind Kind { get; set; }
    public string SenderNo { get; set; } = "";
    public string SenderName { get; set; } = "";   // "홍길동 대리" (DB의 users 테이블과 JOIN해서 얻음)
    public string Body { get; set; } = "";
    // [주의] 서버는 실시간 메시지 시각을 밀리초 단위로 잘라서 보낸다(DB가 밀리초까지만 저장하므로).
    //   그래야 클라이언트가 "실시간으로 받은 것"과 "기록에서 불러온 것"이 같은 메시지인지 시각으로 비교할 수 있다.
    public DateTime Time { get; set; }

    /// <summary>Kind == File일 때 파일 정보(미리보기 포함).</summary>
    public SharedFile? File { get; set; }
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
}
