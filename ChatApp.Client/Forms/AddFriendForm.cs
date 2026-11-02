using ChatApp.Client.Network;
using ChatApp.Client.Ui;
using ChatApp.Common.Models;
using ChatApp.Common.Protocol;

namespace ChatApp.Client.Forms;

// ─────────────────────────────────────────────────────────────────────────────
// [파일 요약] AddFriendForm.cs - 친구 추가 창 (아이디·휴대폰 번호로 찾아서 추가)
// - [아이디] 또는 [휴대폰 번호]를 고르고 정확히 입력 → [찾기] → 찾은 사람 확인 → [친구 추가]
// - 서버는 정확히 일치할 때만 찾아 준다. 휴대폰으로 찾으면 상대 번호는 가린 채(010-****-5678) 보인다.
// - 창을 닫지 않고 여러 명을 차례로 추가할 수 있다. 찾은 사람에게 바로 [1:1 대화]도 열 수 있다.
// - 스레드: 화면은 UI 스레드. 서버 응답(FriendSearchRes·FriendList·Error)은 수신 스레드에서 와서 RunOnUi로 넘긴다.
// ─────────────────────────────────────────────────────────────────────────────
//
// ───────────────────────────────────────────────────────────────────────────
// [공부 노트 · 면접 대비]
//
// ■ 한 줄 요약
//   친구 추가 창. "찾기(FriendSearchReq) → 결과 확인 → 추가(FriendAdd)"의 2단계를 한 창에서 처리한다. 응답을 기다리는 동안은 _waitingSearch/_waitingAdd 플래그와 버튼 비활성화로 중복 요청과 엉뚱한 응답을 막는다.
//
// ■ 흐름
//   ① 찾는 방식 선택(아이디 / 휴대폰) → UpdateKind가 안내 글자를 바꾸고 입력칸을 비운다
//   ② Search(): 빈 값, 휴대폰 형식(PhoneRules.Normalize)을 화면에서 먼저 확인 → FriendSearchReq 전송 → _waitingSearch = true, [찾기] 버튼 비활성화
//   ③ 응답 FriendSearchRes → ShowResult: 결과 카드 표시(이름, 부서 · 사원번호 · 가려진 번호). 나 자신이면 "나", 이미 친구면 "이미 친구"로 버튼을 막는다
//   ④ AddFriend(): FriendAdd 전송(본문 = 사원번호) → 응답이 FriendList(친구 목록 갱신)면 성공으로 보고 "이미 친구" 상태로 바꾼다
//   ⑤ [1:1 대화]는 이 창이 직접 열지 않고 ChatRequested 이벤트로 메인 창에 맡긴다(메인 창이 대화창을 만든다)
//
// ■ 이 파일에서 나오는 개념과 검색어
//   - 응답 기다림 플래그 + switch의 when 조건 ....... "state flags pending request response matching"
//   - 정확히 일치할 때만 검색(개인정보 보호) ........ "exact match lookup prevent enumeration"
//   - 번호 마스킹(010-****-5678) ................... "phone number masking privacy"
//   - 이벤트로 상위 창에 위임 ....................... "event delegation child form to parent form"
//   - 같은 패킷 종류를 여러 창이 나눠 받는 문제 ...... "shared message bus multiple consumers filtering"
//
// ■ 면접 예상 질문과 답
//   Q1. 친구 검색을 "정확히 일치"로만 한 이유는요?
//       A. 이름이나 번호의 일부로 검색되면 회사 사람의 연락처를 훑어 모을 수 있습니다. 정확한 아이디나 번호를 알 때만 찾게 하고, 찾았을 때도 상대 번호는 가려서(마스킹) 보여 줍니다. 서버가 이 규칙을 지키고 화면은 결과를 보여 줄 뿐입니다.
//   Q2. 응답 패킷이 여러 창에 모두 전달되는데, 이 창은 자기 응답을 어떻게 가려내나요?
//       A. PacketReceived는 연결된 모든 구독자에게 전달됩니다. 이 창은 "내가 요청을 보내고 기다리는 중일 때(_waitingSearch/_waitingAdd)"만 해당 응답을 처리합니다(case ... when). 메인 창도 이 창이 기다리는 중이면 Error 패킷을 대화창에 넣지 않고 넘깁니다(IsWaiting).
//   Q3. 추가 성공을 어떻게 알아내나요?
//       A. 추가 요청에 대한 성공 응답은 갱신된 친구 목록(FriendList)입니다. 이 창은 기다리는 중에 FriendList가 오면 성공으로 보고 결과 카드를 "이미 친구"로 바꿉니다.
//   Q4. 창을 닫지 않고 연속으로 추가할 수 있나요?
//       A. 네. 모달이 아니라 Show(this)로 띄우고, 성공해도 닫지 않습니다. 메인 창은 이미 열려 있으면 새로 만들지 않고 앞으로 가져옵니다.
//
// ■ 알아 둘 점 (코드는 바꾸지 않음)
//   - Error 패킷에는 "어느 요청의 오류인지" 표시가 없어서, 기다리는 중에 온 다른 동작의 오류도 이 창이 가져갈 수 있다(요청 번호를 붙이면 정확해진다). 지금 규모에서는 거의 문제되지 않는다.
//   - 응답이 아예 안 오면(서버 정지 등) 플래그가 풀리지 않아 [찾기]가 계속 꺼져 있다. 창을 닫았다 열면 해결된다.
//   - 번호로 찾을 때 "그 번호로 등록된 사용자가 없습니다"와 "상대가 번호를 등록하지 않았을 수 있어요"를 함께 안내하는 것은 의도된 문구다(번호를 숨긴 사람과 없는 사람을 구분해 주지 않음).
// ───────────────────────────────────────────────────────────────────────────

/// <summary>친구 추가 창.</summary>
internal sealed class AddFriendForm : Form
{
    private readonly ChatClient _client;
    private readonly RadioButton _byId = new() { Text = "아이디(사원번호)", AutoSize = true, Checked = true, Name = "FriendById" };
    private readonly RadioButton _byPhone = new() { Text = "휴대폰 번호", AutoSize = true, Name = "FriendByPhone" };
    private readonly TextBox _query = new() { Width = 220, MaxLength = 30, Name = "FriendQuery" };
    private readonly Button _search = UiHelper.CreateButton("찾기", primary: true);
    private readonly Label _resultName = new() { AutoSize = true, Font = Theme.BodyBold, Name = "FriendResultName" };
    private readonly Label _resultDetail = new() { AutoSize = true, ForeColor = UiHelper.Muted };
    private readonly Button _add = UiHelper.CreateButton("친구 추가", primary: true);
    private readonly Button _chat = UiHelper.CreateButton("1:1 대화");
    private readonly Panel _resultBox = new() { Height = 92, Dock = DockStyle.Top, Padding = new Padding(12, 10, 12, 10), BackColor = Theme.Background, Visible = false };
    private readonly Label _status = new() { Dock = DockStyle.Bottom, Height = 40, ForeColor = UiHelper.Muted, Name = "FriendStatus" };

    private FriendSearchResult? _result;
    // [왜] 요청을 보내면 true, 응답을 받으면 false. "내가 보낸 요청의 응답인가?"를 판단하는 최소한의 표시이자, 같은 버튼을 연타해 요청이 겹치는 것도 막는다.
    private bool _waitingSearch, _waitingAdd;

    /// <summary>찾은 사람과 1:1 대화를 열어 달라는 요청 (MainForm이 연다).</summary>
    // [개념] 이 창은 대화창을 직접 만들지 않고 "열어 달라"는 사실만 알린다. 대화창 관리는 MainForm의 책임이라서 창끼리 서로를 몰라도 된다.
    public event Action<UserInfo>? ChatRequested;

    /// <summary>서버 응답을 기다리는 중인지. 기다리는 동안 온 오류는 이 창이 보여 준다(메인 창이 대화창에 넣지 않게).</summary>
    // [읽는 법] MainForm.Network의 "case PacketType.Error when _addFriendForm is { IsWaiting: true }"가 이 값을 본다. 기다리는 중의 오류는 이 창이 보여 주고, 아니면 일반 대화창에 오류 줄로 들어간다.
    public bool IsWaiting => _waitingSearch || _waitingAdd;

    /// <summary>친구 추가 창을 만든다. client로 사용자 검색과 친구 추가를 서버에 요청한다.</summary>
    public AddFriendForm(ChatClient client)
    {
        _client = client;
        Text = $"{Theme.AppName} - 친구 추가";
        Font = UiHelper.BaseFont;
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        BackColor = Color.White;
        ClientSize = LogicalToDeviceUnits(new Size(420, 300));

        var kind = new FlowLayoutPanel { Dock = DockStyle.Top, Height = LogicalToDeviceUnits(34), Padding = new Padding(10, 8, 10, 0), WrapContents = false };
        kind.Controls.Add(_byId);
        kind.Controls.Add(_byPhone);

        var row = new FlowLayoutPanel { Dock = DockStyle.Top, Height = LogicalToDeviceUnits(44), Padding = new Padding(10, 6, 10, 0), WrapContents = false };
        _query.Margin = new Padding(0, 4, 6, 0);
        _search.Name = "FriendSearchButton";
        row.Controls.Add(_query);
        row.Controls.Add(_search);

        _add.Name = "AddFriendOk";
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, FlowDirection = FlowDirection.RightToLeft, WrapContents = false };
        buttons.Controls.Add(_add);
        buttons.Controls.Add(_chat);
        var texts = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false };
        texts.Controls.Add(_resultName);
        texts.Controls.Add(_resultDetail);
        _resultBox.Controls.Add(texts);
        _resultBox.Controls.Add(buttons);
        // [주의] Dock 순서: 아래 Controls.Add 순서가 중요하다(나중에 넣은 것이 먼저 자리를 잡음). 아래→위 막대를 먼저 넣고, 마지막에 맨 위 줄을 넣는다.
        _resultBox.Height = LogicalToDeviceUnits(92);

        var hint = new Label
        {
            Dock = DockStyle.Bottom,
            Height = LogicalToDeviceUnits(40),
            Padding = new Padding(10, 0, 10, 0),
            ForeColor = UiHelper.Muted,
            Text = "아이디·휴대폰 번호가 정확히 같아야 찾을 수 있습니다.\n상대의 휴대폰 번호는 가려서 보이고, 내 번호는 [내 휴대폰 번호 설정]에서 등록합니다.",
        };
        _status.Padding = new Padding(10, 0, 10, 0);
        _status.Height = LogicalToDeviceUnits(28);

        var gap = new Panel { Dock = DockStyle.Top, Height = LogicalToDeviceUnits(8) };
        Controls.Add(_status);
        Controls.Add(hint);
        Controls.Add(_resultBox);
        Controls.Add(gap);
        Controls.Add(row);
        Controls.Add(kind);
        AcceptButton = _search;

        _byId.CheckedChanged += (_, _) => UpdateKind();
        UpdateKind();
        _search.Click += (_, _) => Search();
        _add.Click += (_, _) => AddFriend();
        // [읽는 법] _result?.User is { } user : 결과가 있고 그 안의 User가 null이 아니면 user로 꺼낸다(패턴 매칭). 없으면 아무 일도 하지 않는다.
        _chat.Click += (_, _) => { if (_result?.User is { } user) ChatRequested?.Invoke(user); };

        _client.PacketReceived += OnPacketReceived;
        FormClosed += (_, _) => _client.PacketReceived -= OnPacketReceived;
        Shown += (_, _) => _query.Focus();
    }

    private void UpdateKind()
    {
        _query.PlaceholderText = _byId.Checked ? "사원번호 (예: A0001)" : "휴대폰 번호 (예: 010-1234-5678)";
        _query.Clear();
        _query.Focus();
    }

    private void Search()
    {
        string query = _query.Text.Trim();
        if (query.Length == 0)
        {
            ShowStatus(_byId.Checked ? "찾을 아이디(사원번호)를 입력하세요." : "찾을 휴대폰 번호를 입력하세요.", error: true);
            return;
        }
        // [개념] 형식 검사는 Common의 PhoneRules를 서버와 같이 쓴다. 화면에서 먼저 거르면 서버 요청이 줄고 사용자도 바로 고칠 수 있다(서버가 다시 검사함).
        if (_byPhone.Checked && PhoneRules.Normalize(query) is null)
        {
            ShowStatus("휴대폰 번호 형식이 올바르지 않습니다. (예: 010-1234-5678)", error: true);
            return;
        }
        var req = new FriendSearchRequest { By = _byId.Checked ? FriendSearchBy.EmployeeNo : FriendSearchBy.Phone, Query = query };
        if (!_client.Send(Packet.Create(PacketType.FriendSearchReq).WithData(req)))
        {
            ShowStatus("서버와 연결이 끊어져 찾을 수 없습니다.", error: true);
            return;
        }
        _waitingSearch = true;
        _search.Enabled = false;
        ShowStatus("찾는 중...", error: false);
    }

    private void AddFriend()
    {
        if (_result?.User is not { } user) return;
        if (!_client.Send(Packet.Create(PacketType.FriendAdd, body: user.EmployeeNo)))
        {
            ShowStatus("서버와 연결이 끊어져 추가할 수 없습니다.", error: true);
            return;
        }
        _waitingAdd = true;
        _add.Enabled = false;
        ShowStatus("추가하는 중...", error: false);
    }

    // [스레드] 수신 스레드 → UI 스레드
    private void OnPacketReceived(Packet packet)
    {
        if (packet.Type is not (PacketType.FriendSearchRes or PacketType.FriendList or PacketType.Error)) return;
        UiHelper.RunOnUi(this, () =>
        {
            switch (packet.Type)
            {
                // [읽는 법] case 뒤의 when은 추가 조건. "검색 응답이면서 내가 검색을 기다리는 중일 때만" 처리한다. 다른 창이 보낸 요청의 응답은 무시한다.
                case PacketType.FriendSearchRes when _waitingSearch:
                    _waitingSearch = false;
                    _search.Enabled = true;
                    ShowResult(packet.GetData<FriendSearchResult>());
                    break;
                // [왜] 친구 목록(FriendList)은 다른 이유로도 오지만(로그인 직후 등), 우리가 추가를 기다리는 중일 때만 "추가 성공"으로 해석한다.
                case PacketType.FriendList when _waitingAdd:
                    _waitingAdd = false;
                    if (_result is not null) _result.AlreadyFriend = true;
                    ShowResult(_result);
                    ShowStatus(packet.Body ?? "친구로 추가했습니다.", error: false);
                    break;
                case PacketType.Error when IsWaiting:
                    _waitingSearch = _waitingAdd = false;
                    _search.Enabled = true;
                    if (_result is not null) ShowResult(_result);
                    ShowStatus(packet.Body ?? "처리하지 못했습니다.", error: true);
                    break;
            }
        });
    }

    private void ShowResult(FriendSearchResult? result)
    {
        _result = result;
        if (result?.User is not { } user)
        {
            _resultBox.Visible = false;
            ShowStatus(result?.By == FriendSearchBy.Phone
                ? "그 번호로 등록된 사용자가 없습니다. (상대가 번호를 등록하지 않았을 수 있어요)"
                : "그 아이디의 사용자가 없습니다.", error: true);
            return;
        }
        _resultBox.Visible = true;
        _resultName.Text = user.DisplayName;
        _resultDetail.Text = string.Join(" · ", new[] { user.Department, user.EmployeeNo, result.MaskedPhone }.Where(s => !string.IsNullOrEmpty(s)));
        // [읽는 법] 같은 버튼 하나가 상태에 따라 글자와 사용 가능 여부가 바뀐다: 나 자신 → "나"(비활성) / 이미 친구 → "이미 친구"(비활성) / 그 외 → "친구 추가"(활성).
        _add.Text = result.IsMe ? "나" : result.AlreadyFriend ? "이미 친구" : "친구 추가";
        _add.Enabled = !result.IsMe && !result.AlreadyFriend;
        _chat.Visible = !result.IsMe;
        ShowStatus(result.IsMe ? "나 자신은 친구로 추가할 수 없습니다." : result.AlreadyFriend ? "이미 친구 목록에 있습니다." : "찾았습니다. [친구 추가]를 누르세요.",
            error: false);
    }

    private void ShowStatus(string text, bool error)
    {
        _status.Text = text;
        _status.ForeColor = error ? UiHelper.Danger : UiHelper.Muted;
    }
}
