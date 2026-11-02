using ChatApp.Client.Chat;
using ChatApp.Client.Ui;

namespace ChatApp.Client.Forms;

// ─────────────────────────────────────────────────────────────────────────────
// [파일 요약] ThreadForm.cs - 댓글 창 (카카오톡의 "댓글"처럼 메시지 하나에 달린 댓글만 모아 본다)
// - 맨 위에 원래 메시지, 그 아래 댓글들, 맨 아래 입력칸. 여기서 보낸 글은 원래 메시지에 대한 댓글이 된다.
// - 데이터는 대화창과 같은 Conversation을 본다. 대화에 댓글이 추가·수정·삭제되면 이 창도 바로 바뀐다.
// - 스레드: UI 스레드 전용. 실제 전송은 ChatWindow → MainForm이 한다(이 창은 "보내 달라"고만 알린다).
// ─────────────────────────────────────────────────────────────────────────────
//
// ───────────────────────────────────────────────────────────────────────────
// [공부 노트 · 면접 대비]
//
// ■ 한 줄 요약
//   메시지 하나에 달린 댓글만 모아 보여 주는 창. 데이터를 따로 갖지 않고 대화창과 같은 Conversation을 "구독"해서, 그 안에서 원래 메시지 + 댓글만 골라(GetThread) 화면용 복사본으로 다시 그린다. 보내기·공감·삭제·수정은 직접 하지 않고 이벤트로 ChatWindow에 알린다.
//
// ■ 핵심 아이디어 두 가지
//   ① 복사본(CloneForView)과 _originals 사전
//      - 말풍선(ChatItem)은 내용뿐 아니라 "화면 배치 좌표(Y, Bubble, TextRect …)"를 같은 객체 안에 가지고 있다.
//        같은 객체를 대화창과 댓글 창이 같이 그리면 서로 좌표를 덮어써서 위치가 꼬인다 → 내용만 복사한 새 객체를 만들어 이 창의 BubbleChatView에 준다.
//      - 복사본에서 사용자가 공감/삭제/수정을 누르면, _originals[복사본] = 진짜 말풍선으로 되돌려서 진짜 말풍선을 이벤트로 넘긴다(서버 요청은 진짜 MessageId 기준).
//   ② "통째로 다시 만들기"(Rebuild)
//      - 대화에 항목이 추가/변경/다시 불러오기(Reloaded)될 때 이 스레드에 속한 항목(InThread)이면 Rebuild로 복사본을 전부 새로 만든다.
//        댓글 수가 많지 않다는 가정으로 단순함을 택한 것이다(부분 갱신보다 버그가 적다).
//
// ■ 이 파일에서 나오는 개념과 검색어
//   - 옵저버 패턴(이벤트 구독/해제) ................. "observer pattern C# events unsubscribe FormClosed"
//   - 화면용 복사본(View model) vs 원본 ............ "view model copy vs domain object separation"
//   - 얕은 복사와 깊은 복사 ......................... "shallow copy vs deep copy reference list"
//   - 단순한 전체 다시 그리기 vs 증분 갱신 ........... "full rebuild vs incremental update tradeoff"
//   - 델리게이트로 권한 판단 주입(canDelete/canEdit) .. "inject predicate Func<T,bool> into UI"
//   - 이벤트로 책임 위임 ............................ "UI emits intent events, parent performs action"
//
// ■ 면접 예상 질문과 답
//   Q1. 왜 말풍선을 복사해서 보여 주나요?
//       A. 말풍선 객체가 화면 배치 좌표를 함께 들고 있어서, 대화창과 댓글 창이 같은 객체를 쓰면 서로의 좌표를 덮어씁니다. 내용만 복사한 객체를 따로 만들어 배치 정보가 섞이지 않게 합니다. 대신 사용자가 누른 복사본을 _originals로 원본에 되돌려 처리합니다.
//   Q2. 댓글이 새로 달리면 이 창은 어떻게 알고 갱신되나요?
//       A. Conversation의 ItemAdded/ItemChanged/Reloaded 이벤트를 구독해 두고, 그 항목이 이 스레드에 속하면(원래 메시지이거나 ReplyTo가 같으면) Rebuild로 다시 만듭니다. 창을 닫을 때 구독을 해제해서 닫힌 창이 계속 호출되는 누수를 막습니다.
//   Q3. 댓글 창에서 보낸 글은 어떻게 원래 메시지의 댓글이 되나요?
//       A. 이 창은 글만 SendRequested로 알리고, 원래 메시지 번호는 ChatWindow가 붙여 전송합니다. 그래서 이 창은 네트워크와 서버를 모릅니다.
//   Q4. 삭제·수정 가능 여부는 어디서 판단하나요?
//       A. 생성자로 받은 canDelete/canEdit 함수(ChatWindow가 만든 규칙)를 쓰고, 복사본을 원본으로 바꿔서 물어봅니다. 최종 허용 여부(삭제 24시간, 수정 5분 등)는 서버가 다시 확인합니다.
//
// ■ 알아 둘 점 (코드는 바꾸지 않음)
//   - CloneForView는 얕은 복사다. 특히 Reactions 목록은 같은 List를 가리킨다(복사본과 원본이 공유). 지금은 서버 응답으로 목록을 "통째로 교체"하는 방식이라 문제 없지만, 목록 안을 직접 고치는 코드가 생기면 두 화면이 같이 바뀐다.
//   - 이 창은 ReadStateChanged(읽음 숫자 갱신)를 구독하지 않는다. 그래서 복사본의 UnreadCount는 Rebuild 시점에 찍힌 값이고, 읽음이 바뀌어도 다음 Rebuild 전까지 그대로 보일 수 있다(구독 목록은 코드로 확인함).
//   - 원래 메시지가 지워져도 댓글은 달 수 있다(카카오톡과 같은 동작이라고 코드에 적혀 있음). 서버가 인용을 "삭제된 메시지입니다."로 바꿔 준다.
// ───────────────────────────────────────────────────────────────────────────

/// <summary>메시지 하나에 달린 댓글만 모아 보여 주고 댓글을 쓰는 창.</summary>
internal sealed class ThreadForm : Form
{
    private readonly Conversation _conv;
    // [읽는 법] 이 창이 보여 주는 "원래 메시지"의 서버 번호. 모든 판단(InThread)이 이 번호 하나로 이루어진다.
    private readonly long _rootId;
    private readonly BubbleChatView _view = new() { Dock = DockStyle.Fill, ShowThreadChips = false };
    private readonly Label _title = new();
    private readonly TextBox _input = new();
    private readonly Button _send = new();
    // 화면에 보이는 복사본 → 대화에 있는 진짜 말풍선 (공감·삭제·수정은 진짜 말풍선으로 알린다)
    // [개념] 키 = 화면용 복사본, 값 = 대화에 있는 진짜 말풍선. 사전 키가 "내용"이 아니라 "객체 참조"라서 같은 내용의 다른 복사본과 섞이지 않는다(ChatItem이 record가 아닌 class이므로 참조 비교).
    private readonly Dictionary<ChatItem, ChatItem> _originals = [];

    /// <summary>댓글 보내기 (보낼 글). 원래 메시지 번호는 ChatWindow가 붙인다.</summary>
    // [읽는 법] 아래 이벤트 4개가 이 창의 "바깥으로 나가는 문"이다: 보내기 / 공감 / 삭제 / 수정. 실제 처리와 서버 전송은 ChatWindow → MainForm이 한다.
    public event Action<string>? SendRequested;

    /// <summary>공감을 누르거나 취소할 때 발생한다: (대화에 있는 말풍선, 이모지).</summary>
    public event Action<ChatItem, string>? ReactionRequested;

    /// <summary>메시지 삭제를 요청할 때 발생한다. 인수는 대화에 있는 말풍선.</summary>
    public event Action<ChatItem>? DeleteRequested;

    /// <summary>메시지 수정을 요청할 때 발생한다. 인수는 대화에 있는 말풍선.</summary>
    public event Action<ChatItem>? EditRequested;

    private int S(int v) => LogicalToDeviceUnits(v);

    /// <summary>댓글 창을 만든다. root는 원래 메시지, canDelete/canEdit는 말풍선별 삭제·수정 가능 여부 규칙.</summary>
    public ThreadForm(Conversation conv, ChatItem root, string roomTitle, string myKey, Func<ChatItem, bool> canDelete, Func<ChatItem, bool> canEdit)
    {
        _conv = conv;
        _rootId = root.MessageId;

        Text = $"댓글 - {roomTitle}";
        Font = Theme.Body;
        BackColor = Theme.Surface;
        Size = LogicalToDeviceUnits(new Size(400, 560));
        MinimumSize = LogicalToDeviceUnits(new Size(320, 360));
        StartPosition = FormStartPosition.WindowsDefaultLocation;

        _title.Dock = DockStyle.Top;
        _title.Height = S(40);
        _title.Padding = new Padding(S(12), 0, S(12), 0);
        _title.TextAlign = ContentAlignment.MiddleLeft;
        _title.Font = Theme.BodyBold;
        _title.ForeColor = Color.White;
        _title.BackColor = Theme.Primary;
        _title.AutoEllipsis = true;
        _title.UseMnemonic = false;

        _view.MyKey = myKey;
        // [읽는 법] 복사본이 들어오면 사전에서 원본을 찾아 바깥(ChatWindow)이 준 규칙(canDelete)으로 판단한다. 사전에 없으면(= 이 창 목록에 없는 항목) 불가.
        _view.CanDelete = item => _originals.TryGetValue(item, out ChatItem? o) && canDelete(o);
        _view.CanEdit = item => _originals.TryGetValue(item, out ChatItem? o) && canEdit(o);
        // [왜] 이 창에서는 어떤 말풍선의 "답장"을 눌러도 결국 원래 메시지에 대한 댓글이므로, 대상 선택 없이 입력칸으로 포커스만 옮긴다.
        _view.ReplyRequested += _ => _input.Focus(); // 이 창에서는 무엇을 눌러도 원래 메시지에 대한 댓글
        _view.ReactionRequested += (item, emoji) => { if (_originals.TryGetValue(item, out ChatItem? o)) ReactionRequested?.Invoke(o, emoji); };
        _view.DeleteRequested += item => { if (_originals.TryGetValue(item, out ChatItem? o)) DeleteRequested?.Invoke(o); };
        _view.EditRequested += item => { if (_originals.TryGetValue(item, out ChatItem? o)) EditRequested?.Invoke(o); };

        _input.Multiline = true;
        _input.AcceptsReturn = true;
        _input.BorderStyle = BorderStyle.None;
        _input.Font = Theme.Body;
        _input.MaxLength = 500;
        _input.Dock = DockStyle.Fill;
        _input.PlaceholderText = "댓글 입력 (Enter 보내기, Shift+Enter 줄바꿈)";
        _input.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Enter && !e.Shift) { e.SuppressKeyPress = true; Send(); }
            else if (e.KeyCode == Keys.Escape) { e.SuppressKeyPress = true; Close(); }
        };
        _send.Text = "댓글";
        _send.Dock = DockStyle.Right;
        _send.Width = S(64);
        _send.FlatStyle = FlatStyle.Flat;
        _send.FlatAppearance.BorderSize = 0;
        _send.BackColor = Theme.Primary;
        _send.ForeColor = Color.White;
        _send.Font = Theme.BodyBold;
        _send.Cursor = Cursors.Hand;
        _send.Click += (_, _) => Send();

        var inputBox = new Panel { Dock = DockStyle.Fill, Padding = new Padding(S(6)), BackColor = Theme.Surface };
        inputBox.Controls.Add(_input);
        inputBox.Controls.Add(_send);
        _input.BringToFront();
        var inputArea = new Panel { Dock = DockStyle.Bottom, Height = S(72), Padding = new Padding(S(8)), BackColor = Theme.Background };
        inputArea.Controls.Add(inputBox);

        Controls.Add(_view);
        Controls.Add(inputArea);
        Controls.Add(_title);
        _view.BringToFront();

        Rebuild();
        // [주의] 구독했으면 반드시 FormClosed에서 해제(-=)한다. Conversation은 창보다 오래 살기 때문에, 해제하지 않으면 닫힌 창을 계속 붙잡고 호출한다(메모리 누수 + 죽은 창 갱신).
        conv.ItemAdded += OnItemAdded;
        conv.ItemChanged += OnItemChanged;
        conv.Reloaded += Rebuild;
        FormClosed += (_, _) =>
        {
            conv.ItemAdded -= OnItemAdded;
            conv.ItemChanged -= OnItemChanged;
            conv.Reloaded -= Rebuild;
        };
        Shown += (_, _) => { _view.ScrollToBottom(); _input.Focus(); };
    }

    /// <summary>이 창이 보여 주는 원래 메시지 번호.</summary>
    public long RootId => _rootId;

    // [읽는 법] 이 스레드에 속하는 항목 = 원래 메시지 자신이거나, ReplyTo(답장 대상)가 원래 메시지인 것. 댓글의 댓글(2단 이상)은 이 조건에 걸리지 않는다.
    private bool InThread(ChatItem item) => item.MessageId == _rootId || item.ReplyTo?.MessageId == _rootId;

    private void OnItemAdded(ChatItem item) { if (InThread(item)) Rebuild(); }
    private void OnItemChanged(ChatItem item) { if (InThread(item)) Rebuild(); }

    /// <summary>대화에서 원래 메시지와 댓글을 다시 모아 복사본으로 보여 준다 (댓글 수가 많지 않아 통째로 다시 만들어도 가볍다).</summary>
    // [읽는 법] ① 원본에서 스레드 항목을 모아 복사본 생성 + 사전 등록 ② 제목줄 갱신(댓글 수는 원래 메시지와 삭제된 댓글을 뺀 개수) ③ BubbleChatView에 새 목록 전달(SetItems).
    private void Rebuild()
    {
        _originals.Clear();
        var clones = new List<ChatItem>();
        foreach (ChatItem original in _conv.GetThread(_rootId))
        {
            ChatItem clone = original.CloneForView();
            _originals[clone] = original;
            clones.Add(clone);
        }
        ChatItem? root = _conv.FindMessage(_rootId);
        int count = clones.Count(c => c.MessageId != _rootId && !c.Deleted);
        string who = root is null ? "" : root.Kind == ChatItemKind.Mine ? "나" : root.Sender;
        string preview = root is null ? "" : root.DisplayText.ReplaceLineEndings(" ");
        _title.Text = $"댓글 {count}개 · {who}: {preview}";
        _view.SetItems(clones);
    }

    /// <summary>입력칸과 [전송] 버튼을 켜거나 끈다 (연결이 끊겼거나 방을 나갔을 때 끈다).</summary>
    // [읽는 법] 바깥(ChatWindow)이 연결이 끊겼거나 방에서 나갔을 때 입력을 막는 데 쓴다. 이 창은 왜 막는지는 모른다.
    public void SetInputEnabled(bool enabled)
    {
        _input.Enabled = enabled;
        _send.Enabled = enabled;
        _send.BackColor = enabled ? Theme.Primary : Theme.Offline;
    }

    private void Send()
    {
        string text = _input.Text.Trim();
        if (text.Length == 0 || !_input.Enabled) return;
        // 원래 메시지가 그사이 지워졌어도 댓글은 달 수 있다 (카카오톡과 같음). 서버가 인용을 "삭제된 메시지입니다."로 붙인다.
        // [왜] 보낸 직후 입력칸을 비우고 포커스를 되돌린다. 실제 전송 성공 여부는 이 창이 확인하지 않는다(결과는 대화에 새 항목이 추가되면 Rebuild로 보인다).
        SendRequested?.Invoke(text);
        _input.Clear();
        _input.Focus();
    }
}
