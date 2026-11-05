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
//
// ───────────────────────────────────────────────────────────────────────────
// [공부 노트 · 면접 대비]
//
// ■ 한 줄 요약
//   대화 하나(방 또는 1:1)의 "데이터 쪽 주인". 말풍선 목록, 안 읽은 수, 읽음 위치, 지난 대화 합치기, 댓글 수를 맡고, 바뀔 때마다 이벤트로 열려 있는 창에 알린다.
//
// ■ 읽는 순서
//   ① Key/Items/Unread → ② 이벤트 4개(ItemAdded, ItemChanged, ReadStateChanged, Reloaded) → ③ Add → ④ 댓글(RecountReplies, GetThread)
//   → ⑤ 지난 대화(InsertHistory, DedupKey) → ⑥ RoomKey/DirectKey
//
// ■ 이벤트 구분 (어느 이벤트가 언제 나가는가)
//   ItemAdded ........ 새 말풍선 1개 끝에 추가 → 창이 아래에 붙여 그림
//   ItemChanged ...... 이미 있는 말풍선의 내용만 바뀜(진행률, 댓글 수) → 창이 그 부분만 다시 그림
//   ReadStateChanged . 읽음 숫자("1")가 여러 개 한꺼번에 바뀜 → 다시 그리기만
//   Reloaded ......... 목록 중간/앞에 끼워 넣음 → 창이 전체를 다시 배치
//
// ■ 이 파일에서 나오는 개념과 검색어
//   - 옵저버 패턴(event/델리게이트), 구독 해제 ...... "observer pattern C# event unsubscribe memory leak"
//   - 데이터와 화면 분리(Model-View) ................ "separate model and view WinForms"
//   - 중복 제거(HashSet, DistinctBy, 중복 키) ....... "deduplicate with HashSet composite key"
//   - 두 정렬 목록 병합(merge) ...................... "merge two sorted lists"
//   - 안정 정렬(OrderBy는 같은 값의 순서를 유지) ..... "stable sort LINQ OrderBy"
//   - 커서 기반 페이지 불러오기(더 오래된 것) ........ "cursor pagination chat history"
//
// ■ 면접 예상 질문과 답
//   Q1. 대화 데이터를 창(ChatWindow)이 아니라 Conversation에 둔 이유는요?
//       A. 창을 X로 닫아도 대화 내용이 남아 다시 열면 그대로 보이고, 창이 닫혀 있을 때 온 메시지도 쌓이면서 안 읽은 수가 올라야 하기 때문입니다. 데이터(Model)와 화면(View)을 분리한 가장 단순한 형태입니다.
//   Q2. 지난 대화를 합칠 때 같은 메시지가 두 번 보이지 않게 한 방법은요?
//       A. 서버 메시지 번호가 있으면 "id:번호"로, 파일은 "file:파일번호"로, 번호가 없는 옛 기록은 "보낸 사람|내용|밀리초 시각"으로 비교하는 DedupKey를 만들어 HashSet에 담고, 이미 있는 것은 버립니다. 들어온 묶음 안의 중복은 DistinctBy로 먼저 제거합니다. 시각을 밀리초까지만 비교하는 이유는 DB(DATETIME(3))가 밀리초까지만 저장하기 때문입니다.
//   Q3. 지난 대화는 어떻게 시간 순서대로 끼워 넣나요?
//       A. 이미 있는 목록(Items)과 새 기록(fresh, 시각순 정렬)이 둘 다 정렬되어 있으므로 병합 정렬의 병합 단계처럼 앞에서부터 비교하며 섞습니다. 처음 불러올 때는 모두 지금 대화보다 앞에 들어가고, 자동 재접속 뒤의 기록은 끊김 알림과 재연결 알림 사이로 들어갑니다. 한 번에 합친 뒤 Reloaded를 한 번만 발생시켜 창이 한 번만 다시 그립니다.
//   Q4. 이미 화면에 있는 메시지가 다시 들어오면 그냥 버리나요?
//       A. 버리기 전에 최신 상태로 맞춥니다. 끊겨 있던 동안 삭제되었거나 수정되었거나 공감이 바뀌었을 수 있기 때문입니다(Deleted, Edited, Text, Reactions를 갱신).
//   Q5. 댓글 수는 서버에 묻지 않고 왜 클라이언트에서 세나요?
//       A. 불러온 범위가 항상 "어느 시점부터 지금까지"라서 원래 메시지가 보이면 그 뒤에 달린 댓글도 모두 불러와져 있습니다. 서버 왕복 없이 정확히 셀 수 있습니다. 한계: 원래 메시지가 보이지만 댓글이 범위 밖인 경우는 없다는 전제에 의존합니다.
//   Q6. 한계는요? (솔직하게 답하기)
//       A. (1) 서버 시각과 이 PC에서 만든 알림의 시각을 비교해 위치를 정하므로 PC 시계가 크게 틀리면 알림 위치가 조금 어긋난다. (2) Items가 List라서 FindMessage가 O(n)이다(최근부터 찾아 보통 빠르지만 아주 긴 대화에서는 메시지 번호 사전을 두는 편이 낫다). (3) Items를 바깥에서 직접 바꾸면 이벤트가 나가지 않는다 — Add/InsertHistory로만 바꾸는 규칙에 의존한다.
//
// ■ 문제가 났을 때 어디를 볼까
//   - 같은 메시지가 두 번 보인다 ............ DedupKey가 서로 다르게 만들어지는지(시각 밀리초, 메시지 번호 유무)
//   - 지난 대화가 중간에 끼어 어긋난다 ....... InsertHistory의 병합 비교(시각 Time), PC 시계 차이
//   - 읽음 "1"이 안 사라진다 ................ PeerLastReadAt 갱신, LatestMessageTime에서 올리는 중인 파일이 빠지는지
//   - 댓글 수가 틀린다 ...................... RecountReplies가 불렸는지(지난 대화 삽입 후, 댓글 삭제 후)
//   - 창이 닫힌 뒤에도 메모리가 줄지 않는다 .. ItemAdded/ItemChanged를 -=로 해제했는지(ChatWindow)
// ───────────────────────────────────────────────────────────────────────────

/// <summary>
/// 대화 하나(채팅방 또는 1:1)의 메시지 보관함.
/// 대화창을 닫았다가 다시 열어도 이전 대화가 남아 있도록, 창과 별개로 메인 창이 갖고 있는다.
/// UI 스레드에서만 사용한다.
/// </summary>
// [개념] 기본 생성자(primary constructor) - class 이름 뒤 (string key, string title):
//   C# 12 문법. 생성자 매개변수를 클래스 전체에서 쓸 수 있고, 아래처럼 속성 초기값으로 바로 넘길 수 있다.
internal sealed class Conversation(string key, string title)
{
    /// <summary>대화를 구분하는 키. 방은 "room:2", 1:1은 "dm:A0001" 형태.</summary>
    public string Key { get; } = key;

    /// <summary>대화 제목(방 이름 또는 상대 이름).</summary>
    public string Title { get; set; } = title;

    /// <summary>이 대화의 말풍선 목록 (시간 순서).</summary>
    // [개념] [] : C# 12 컬렉션 식. new List<ChatItem>()과 같다.
    public List<ChatItem> Items { get; } = [];

    /// <summary>창이 안 보이거나 다른 창을 보고 있을 때 쌓인 메시지 수.</summary>
    public int Unread { get; set; }

    // ───────── 읽음 표시 ─────────

    /// <summary>1:1 상대가 어디까지 읽었는지 (서버가 알려 줌). 이 시각 뒤의 내 메시지 옆에 "1"이 보인다.</summary>
    public DateTime? PeerLastReadAt { get; set; }

    /// <summary>서버에 마지막으로 "여기까지 읽음"을 보낸 시각 (같은 값을 계속 보내지 않으려고).</summary>
    public DateTime LastMarkedRead { get; set; }

    /// <summary>
    /// 서버가 시각을 매긴 메시지 중 가장 마지막 것의 시각 (읽음 표시 기준).
    /// [주의] 아직 올리는 중인 내 파일 말풍선은 이 PC의 시계로 시각이 찍혀 있어 뺀다(PC 시계가 빠르면 읽음 위치가 앞서 나간다).
    /// </summary>
    // [읽는 법] 뒤에서부터 훑어 "읽음 기준이 될 마지막 메시지"를 찾는다. 시스템 알림은 건너뛰고, 아직 올리는 중인 내 파일(MessageId == 0이고 Ready가 아님)도 건너뛴다 — 그 말풍선의 시각은 서버가 아니라 이 PC 시계라서 읽음 위치를 앞서 보내기 때문이다.
    public DateTime? LatestMessageTime
    {
        get
        {
            for (int i = Items.Count - 1; i >= 0; i--)
            {
                ChatItem item = Items[i];
                if (item.IsSystemLike || (item.MessageId == 0 && item.File is { State: not FileState.Ready })) continue;
                return item.Time;
            }
            return null;
        }
    }

    /// <summary>서버 메시지 번호로 말풍선 찾기 (삭제·공감 반영용). 없으면 null.</summary>
    public ChatItem? FindMessage(long messageId)
    {
        if (messageId <= 0) return null;
        for (int i = Items.Count - 1; i >= 0; i--) // 최근 메시지일수록 자주 찾으므로 뒤에서부터
            if (Items[i].MessageId == messageId) return Items[i];
        foreach (ChatItem pending in PendingHistory)
            if (pending.MessageId == messageId) return pending;
        return null;
    }

    /// <summary>읽음 숫자처럼 여러 말풍선의 표시가 한꺼번에 바뀌었을 때 (창은 다시 그리기만).</summary>
    public event Action? ReadStateChanged;
    /// <summary>ReadStateChanged 이벤트를 발생시켜 열려 있는 창에 읽음 표시가 바뀌었음을 알린다.</summary>
    public void NotifyReadStateChanged() => ReadStateChanged?.Invoke();

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

        // 댓글이면 원래 메시지의 "↳ 댓글 N"을 하나 올린다
        if (item.ReplyTo is { } reply && !item.Deleted && FindMessage(reply.MessageId) is { } root && root != item)
        {
            root.ReplyCount++;
            NotifyChanged(root);
        }
    }

    // ───────── 댓글 수 ─────────

    /// <summary>
    /// 모든 말풍선의 댓글 수를 다시 센다 (지난 대화를 넣은 뒤, 댓글이 지워진 뒤). 값이 바뀐 말풍선은 창에 알린다.
    /// [왜] 서버에 따로 묻지 않고 지금 불러온 대화에서 센다: 불러온 범위는 항상 "어느 시점 ~ 지금"이라
    ///      원래 메시지가 보이면 그 뒤에 달린 댓글도 모두 불러와져 있다.
    /// </summary>
    public void RecountReplies(bool notify = true)
    {
        var counts = new Dictionary<long, int>();
        foreach (ChatItem item in Items)
            if (item.ReplyTo is { } reply && !item.Deleted)
                counts[reply.MessageId] = counts.GetValueOrDefault(reply.MessageId) + 1;
        foreach (ChatItem item in Items)
        {
            if (item.MessageId <= 0) continue;
            int count = counts.GetValueOrDefault(item.MessageId);
            if (item.ReplyCount == count) continue;
            item.ReplyCount = count;
            if (notify) NotifyChanged(item);
        }
    }

    /// <summary>그 메시지와 거기 달린 댓글들 (시간 순서). 댓글 창이 쓴다.</summary>
    public List<ChatItem> GetThread(long rootId)
        => Items.Where(i => i.MessageId == rootId || i.ReplyTo?.MessageId == rootId).ToList();

    /// <summary>파일 진행률·상태처럼 이미 있는 말풍선의 내용이 바뀌었을 때.</summary>
    public event Action<ChatItem>? ItemChanged;

    /// <summary>ItemChanged 이벤트를 발생시켜 해당 말풍선을 창이 다시 그리게 한다.</summary>
    // [왜] 말풍선 크기는 그대로이고 내용(진행률 %)만 바뀌므로, 창은 전체를 다시 배치하지 않고 다시 그리기만 하면 된다.
    public void NotifyChanged(ChatItem item) => ItemChanged?.Invoke(item);

    // ───────── 지난 대화 (DB 기록) ─────────

    /// <summary>1:1 지난 대화를 이미 요청했는지 (창을 닫았다 열 때마다 다시 요청하지 않도록).</summary>
    public bool HistoryRequested { get; set; }

    /// <summary>처음 불러온 50개보다 오래된 메시지가 남아 있는지.</summary>
    public bool HistoryHasMore { get; set; }

    /// <summary>이전 페이지 요청 중복 전송 방지.</summary>
    public bool HistoryLoading { get; set; }

    /// <summary>현재 받은 여러 패킷을 이전 페이지로 앞에 붙이는 중인지.</summary>
    public bool LoadingOlderHistory { get; set; }

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

        // 이미 화면에 있는 메시지라도, 끊겨 있던 사이 지워졌거나 공감이 바뀌었을 수 있다 → 최신 상태로 맞춘다
        bool updated = false;
        foreach (ChatItem h in history.Where(h => h.MessageId > 0))
        {
            if (FindMessage(h.MessageId) is not { } old || old == h) continue;
            if (old.Deleted != h.Deleted || old.Edited != h.Edited || old.Text != h.Text || old.Reactions.Count != h.Reactions.Count || h.Reactions.Count > 0)
            {
                old.Deleted |= h.Deleted;
                old.Edited = h.Edited;
                if (!h.Deleted) old.Text = h.Text;
                old.Reactions = h.Reactions;
                updated = true;
            }
        }

        // [개념] LINQ Where: 조건에 맞는 것만 골라낸다. 여기서는 "아직 화면에 없는 기록"만 남긴다.
        // 들어온 묶음 안의 중복(재접속으로 같은 기록을 두 번 받은 경우)도 하나만 남긴다
        // [읽는 법] 한 줄에 세 단계: ① DistinctBy: 들어온 묶음 안의 중복 제거 ② Where: 이미 화면에 있는 것 제거 ③ ToList: 목록으로 확정(이후 반복해서 쓰므로 지연 평가를 끝낸다).
        List<ChatItem> fresh = history.DistinctBy(DedupKey).Where(h => !existing.Contains(DedupKey(h))).ToList();
        if (fresh.Count == 0)
        {
            if (updated) Reloaded?.Invoke();
            return 0;
        }

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
        // [읽는 법] 합친 직후에는 창이 곧 Reloaded로 전체를 다시 그리므로, 댓글 수가 바뀐 말풍선마다 알리지(notify) 않는다 → 불필요한 다시 그리기를 줄인다.
        RecountReplies(notify: false); // 지난 대화 속 댓글까지 세고, 창은 아래 Reloaded로 한꺼번에 다시 그린다
        Reloaded?.Invoke();
        return fresh.Count;
    }

    // [왜] 서버 메시지 번호가 있으면 그것으로 비교한다(가장 정확). 번호가 없는 옛 기록만 아래 방식으로 비교한다.
    // [왜] 시각을 밀리초(fff)까지만 비교하는 이유: DB(DATETIME(3))는 밀리초까지만 저장하므로,
    //   실시간 메시지의 시각도 서버가 밀리초까지만 남겨서 보낸다. 그래야 두 경로로 온 같은 메시지의 Key가 똑같아진다.
    // [개념] 삼항 연산자 (조건 ? A : B) + 문자열 보간 $"...{값}...", {Time:형식}은 날짜를 지정한 형식의 글자로 바꾼다.
    private static string DedupKey(ChatItem item) => item.File is not null
        ? $"file:{item.File.Info.FileId}"
        : item.MessageId > 0 ? $"id:{item.MessageId}"
        : $"{item.SenderKey}|{item.Text}|{item.Time:yyyyMMddHHmmssfff}";

    /// <summary>채팅방 번호로 대화 키("room:번호")를 만들어 돌려준다.</summary>
    // 대화를 사전(Dictionary)에 넣을 때 쓰는 키. 방은 "room:번호", 1:1은 "dm:상대 사원번호".
    // [왜] 방 번호 2와 사원번호 "2"가 섞이지 않도록 앞에 종류를 붙인다.
    public static string RoomKey(int roomId) => $"room:{roomId}";

    /// <summary>상대 사원번호로 1:1 대화 키("dm:사원번호")를 만들어 돌려준다.</summary>
    public static string DirectKey(string employeeNo) => $"dm:{employeeNo}";
}
