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
    private bool _waitingSearch, _waitingAdd;

    /// <summary>찾은 사람과 1:1 대화를 열어 달라는 요청 (MainForm이 연다).</summary>
    public event Action<UserInfo>? ChatRequested;

    /// <summary>서버 응답을 기다리는 중인지. 기다리는 동안 온 오류는 이 창이 보여 준다(메인 창이 대화창에 넣지 않게).</summary>
    public bool IsWaiting => _waitingSearch || _waitingAdd;

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
                case PacketType.FriendSearchRes when _waitingSearch:
                    _waitingSearch = false;
                    _search.Enabled = true;
                    ShowResult(packet.GetData<FriendSearchResult>());
                    break;
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
