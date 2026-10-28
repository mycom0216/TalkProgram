using ChatApp.Client.Ui;

namespace ChatApp.Client.Chat;

// ─────────────────────────────────────────────────────────────────────────────
// [파일 요약] Conversation.cs - 대화 하나(채팅방 1개 또는 1:1 상대 1명)의 메시지 목록
// - 역할: 말풍선(ChatItem) 목록을 보관하고, 바뀔 때마다 이벤트로 열려 있는 대화창에 알린다.
// - 스레드: UI 스레드 전용. (MainForm이 수신 스레드의 패킷을 BeginInvoke로 UI 스레드에 넘긴 뒤에 여기를 바꾼다)
//   [왜] lock이 없는 이유: 오직 UI 스레드 한 곳에서만 읽고 쓰므로 동시 접근이 일어나지 않는다.
// - 연결: MainForm이 Conversation들을 사전(Dictionary)으로 갖고 있고, ChatWindow는 그중 하나를 화면에 보여 준다.
// [왜] "데이터(대화)"와 "화면(창)"을 분리했다 (MVC/MVVM 패턴의 가장 단순한 형태).
//   - 창을 X로 닫아도 대화 내용은 메인 창의 Conversation에 남아, 다시 열면 그대로 보인다.
//   - 창이 닫혀 있을 때 온 메시지도 Conversation에 쌓이고 안 읽은 수(Unread)가 올라간다.
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// 대화 하나(채팅방 또는 1:1)의 메시지 보관함.
/// 대화창을 닫았다가 다시 열어도 이전 대화가 남아 있도록, 창과 별개로 메인 창이 갖고 있는다.
/// UI 스레드에서만 사용한다.
/// </summary>
// [개념] 기본 생성자(primary constructor) - class 이름 뒤 (string key, string title):
//   C# 12 문법. 생성자 매개변수를 클래스 전체에서 쓸 수 있고, 아래처럼 속성 초기값으로 바로 넘길 수 있다.
internal sealed class Conversation(string key, string title)
{
    public string Key { get; } = key;          // "room:2", "dm:A0001"
    public string Title { get; set; } = title;

    // [개념] [] : C# 12 컬렉션 식. new List<ChatItem>()과 같다.
    public List<ChatItem> Items { get; } = [];

    /// <summary>창이 안 보이거나 다른 창을 보고 있을 때 쌓인 메시지 수.</summary>
    public int Unread { get; set; }

    /// <summary>열려 있는 대화창이 이 이벤트로 새 말풍선을 받는다.</summary>
    // [개념] event Action<ChatItem>
    //   - Action<T>는 "T 하나를 받고 아무것도 돌려주지 않는 함수"를 담는 형식(델리게이트).
    //   - event를 붙이면 바깥에서는 += (구독), -= (구독 해제)만 할 수 있고, 발생(Invoke)은 이 클래스만 할 수 있다.
    //   - 대화창이 열리면 +=로 구독하고, 닫히면 -=로 해제한다(해제하지 않으면 닫힌 창이 메모리에 계속 남는다).
    //   - ?는 구독자가 하나도 없을 수 있다(null)는 뜻이다.
    public event Action<ChatItem>? ItemAdded;

    /// <summary>메시지 하나를 끝에 추가하고, 열려 있는 창에 알린다.</summary>
    public void Add(ChatItem item)
    {
        Items.Add(item);
        // [개념] ?.Invoke : 구독자가 있을 때만 호출(null이면 아무 일도 안 함). 창이 닫혀 있으면 구독자가 없다.
        ItemAdded?.Invoke(item);
    }

    /// <summary>파일 진행률·상태처럼 이미 있는 말풍선의 내용이 바뀌었을 때.</summary>
    public event Action<ChatItem>? ItemChanged;

    // [왜] 말풍선 크기는 그대로이고 내용(진행률 %)만 바뀌므로, 창은 전체를 다시 배치하지 않고 다시 그리기만 하면 된다.
    public void NotifyChanged(ChatItem item) => ItemChanged?.Invoke(item);

    // ───────── 지난 대화 (DB 기록) ─────────

    /// <summary>1:1 지난 대화를 이미 요청했는지 (창을 닫았다 열 때마다 다시 요청하지 않도록).</summary>
    public bool HistoryRequested { get; set; }

    /// <summary>여러 묶음으로 나뉘어 오는 지난 대화를 Last가 올 때까지 모아 두는 곳.</summary>
    // [왜] 사진 미리보기가 많으면 서버가 지난 대화를 여러 패킷으로 나눠 보낸다(패킷 하나는 최대 1MB).
    //   묶음이 올 때마다 화면에 넣으면 순서가 꼬이고 여러 번 다시 그려지므로, 마지막 묶음까지 모은 뒤 한 번에 넣는다.
    public List<ChatItem> PendingHistory { get; } = [];

    /// <summary>지난 대화를 맨 앞에 끼워 넣은 뒤 (창이 전체를 다시 그려야 함).</summary>
    public event Action? Reloaded;

    private bool _historyLoaded; // 지난 대화를 한 번이라도 넣었는지 (구분선은 처음 한 번만)

    /// <summary>
    /// 지난 대화를 대화 맨 앞에 넣는다.
    /// 불러오는 사이에 실시간으로도 받은 메시지가 있을 수 있으므로(서버에서 저장과 전송이 거의 동시에 일어남)
    /// 같은 메시지는 건너뛴다. 같은지 비교는 DedupKey(보낸 사람 + 내용 + 밀리초 단위 시각, 파일은 파일 ID)로 한다.
    /// </summary>
    // [주의] 중복이 생기는 경쟁 상태 예시:
    //   ① 내가 방에 들어감 → ② 바로 그때 누가 보낸 메시지가 실시간으로 나에게 옴 + 동시에 DB에도 저장됨
    //   → ③ 서버가 지난 대화를 읽을 때 그 메시지도 포함됨 → 같은 메시지가 두 번 보일 수 있다.
    //   그래서 이미 화면에 있는 메시지와 같은 Key를 가진 기록은 버린다.
    public int InsertHistory(List<ChatItem> history)
    {
        // [개념] HashSet: 중복 없는 집합. Contains(포함 여부 확인)가 목록 길이와 상관없이 매우 빠르다(평균 O(1)).
        //   List.Contains는 처음부터 끝까지 하나씩 비교하므로(O(n)) 많을수록 느려진다.
        var existing = Items.Select(DedupKey).ToHashSet();
        // [개념] LINQ Where: 조건에 맞는 것만 골라낸다. 여기서는 "아직 화면에 없는 기록"만 남긴다.
        List<ChatItem> fresh = history.Where(h => !existing.Contains(DedupKey(h))).ToList();
        if (fresh.Count == 0) return 0;

        // 시간 순서대로 끼워 넣는다 (두 정렬된 목록 합치기, merge).
        // [왜] 처음 열 때의 지난 대화는 모두 지금 대화보다 앞에 들어가고,
        //      자동 재접속 뒤에 받은 "끊겨 있던 동안의 대화"는 끊김 알림과 재연결 알림 사이에 들어간다.
        // [주의] 서버가 준 시각과 이 PC에서 만든 알림의 시각을 비교하므로, 두 컴퓨터 시계가 크게 다르면 위치가 조금 어긋날 수 있다.
        // [개념] OrderBy는 "안정 정렬": 시각이 같은 항목끼리는 원래 순서가 유지된다.
        bool firstLoad = !_historyLoaded;
        _historyLoaded = true;
        var merged = new List<ChatItem>(Items.Count + fresh.Count + 1);
        int i = 0;
        ChatItem? lastFresh = null;
        foreach (ChatItem item in fresh.OrderBy(h => h.Time))
        {
            while (i < Items.Count && Items[i].Time <= item.Time) merged.Add(Items[i++]);
            merged.Add(item);
            lastFresh = item;
        }
        // 처음 불러온 경우에만, 지난 대화와 지금 대화 사이에 구분선 (카카오톡의 "여기까지 읽었습니다"와 비슷한 역할)
        if (firstLoad && lastFresh is not null)
            merged.Insert(merged.IndexOf(lastFresh) + 1, new ChatItem { Kind = ChatItemKind.System, Text = "──── 여기까지 지난 대화 ────", Time = lastFresh.Time });
        while (i < Items.Count) merged.Add(Items[i++]);

        Items.Clear();
        Items.AddRange(merged);
        Reloaded?.Invoke();
        return fresh.Count;
    }

    // [왜] 시각을 밀리초(fff)까지만 비교하는 이유: DB(DATETIME(3))는 밀리초까지만 저장하므로,
    //   실시간 메시지의 시각도 서버가 밀리초까지만 남겨서 보낸다. 그래야 두 경로로 온 같은 메시지의 Key가 똑같아진다.
    // [개념] 삼항 연산자 (조건 ? A : B) + 문자열 보간 $"...{값}...", {Time:형식}은 날짜를 지정한 형식의 글자로 바꾼다.
    private static string DedupKey(ChatItem item) => item.File is not null
        ? $"file:{item.File.Info.FileId}"
        : $"{item.SenderKey}|{item.Text}|{item.Time:yyyyMMddHHmmssfff}";

    // 대화를 사전(Dictionary)에 넣을 때 쓰는 키. 방은 "room:번호", 1:1은 "dm:상대 사원번호".
    // [왜] 방 번호 2와 사원번호 "2"가 섞이지 않도록 앞에 종류를 붙인다.
    public static string RoomKey(int roomId) => $"room:{roomId}";
    public static string DirectKey(string employeeNo) => $"dm:{employeeNo}";
}
