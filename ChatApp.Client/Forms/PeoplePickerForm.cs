using ChatApp.Client.Network;
using ChatApp.Client.Ui;
using ChatApp.Common.Models;
using ChatApp.Common.Protocol;

namespace ChatApp.Client.Forms;

// ─────────────────────────────────────────────────────────────────────────────
// [파일 요약] PeoplePickerForm.cs - "사람 고르기" 창 (회사 전체 사용자 목록에서 고른다)
// - 쓰임: ① 채팅방에 초대할 사람 고르기(여러 명, 체크) ② 접속하지 않은 동료에게 1:1 보내기(한 명)
// - 목록: 창이 뜨면 서버에 DirectoryReq를 보내고, Directory(사용 중인 모든 사람)를 받아 채운다.
//         접속 중인 동료 목록(UserList)과 달리 오프라인인 사람도 들어 있다.
// - 스레드: 화면은 UI 스레드. 서버 응답은 수신 스레드에서 와서 RunOnUi로 넘긴다.
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>회사 사람 고르기. OK면 Selected에 고른 사람들.</summary>
public sealed class PeoplePickerForm : Form
{
    private readonly ChatClient _client;
    private readonly bool _multi;
    private readonly HashSet<string> _exclude;
    private readonly TextBox _filter = new() { Dock = DockStyle.Top, PlaceholderText = "이름·부서·사원번호로 찾기" };
    private readonly ListView _list = new()
    {
        Dock = DockStyle.Fill,
        View = View.Details,
        FullRowSelect = true,
        GridLines = false,
        HideSelection = false,
        HeaderStyle = ColumnHeaderStyle.Nonclickable,
    };
    private readonly Label _status = new() { Dock = DockStyle.Bottom, Height = 24, ForeColor = UiHelper.Muted, TextAlign = ContentAlignment.MiddleLeft };
    private readonly Button _ok;

    private List<UserInfo> _all = [];
    private readonly HashSet<string> _checked = [];
    private bool _filling;  // 목록을 다시 채우는 중에는 체크 이벤트를 무시한다
    private bool _received; // 서버에서 목록을 받았는지 (받기 전의 0명과 진짜 0명을 구분)
    private readonly string? _hint; // 한 명 고르기의 안내 문구 (없으면 1:1 안내)

    /// <summary>고른 사람들 (한 명 고르기면 1명).</summary>
    public List<UserInfo> Selected { get; private set; } = [];

    /// <param name="title">창 제목 (예: "'개발팀' 방에 초대")</param>
    /// <param name="multiSelect">true = 체크로 여러 명, false = 한 명(더블클릭으로 바로 선택)</param>
    /// <param name="exclude">목록에서 뺄 사원번호 (나 자신, 이미 방에 있는 사람)</param>
    /// <param name="people">고를 사람을 직접 줄 때(예: 방 멤버 중 새 방장). 없으면 서버에서 회사 사람 전체를 받아 온다.</param>
    /// <param name="okText">확인 버튼 글자 (없으면 "초대"/"대화하기")</param>
    /// <param name="hint">아래쪽 안내 문구 (한 명 고르기)</param>
    /// <param name="skipText">세 번째 버튼 글자 (예: "그냥 나가기"). 누르면 아무도 고르지 않고 DialogResult.Ignore로 닫힌다.</param>
    public PeoplePickerForm(ChatClient client, string title, bool multiSelect, IEnumerable<string> exclude,
                            IEnumerable<UserInfo>? people = null, string? okText = null, string? hint = null, string? skipText = null)
    {
        _client = client;
        _multi = multiSelect;
        _exclude = exclude.ToHashSet();
        _hint = hint;

        Text = title;
        Font = UiHelper.BaseFont;
        StartPosition = FormStartPosition.CenterParent;
        Size = LogicalToDeviceUnits(new Size(460, 520));
        MinimumSize = LogicalToDeviceUnits(new Size(360, 360));
        ShowInTaskbar = false;
        MinimizeBox = false;
        BackColor = Color.White;

        _list.CheckBoxes = multiSelect;
        _list.MultiSelect = false;
        _list.Columns.Add("이름", LogicalToDeviceUnits(140));
        _list.Columns.Add("부서", LogicalToDeviceUnits(130));
        _list.Columns.Add("사원번호", LogicalToDeviceUnits(110));
        _list.ItemChecked += (_, e) =>
        {
            if (_filling || e.Item.Tag is not UserInfo u) return;
            if (e.Item.Checked) _checked.Add(u.EmployeeNo); else _checked.Remove(u.EmployeeNo);
            UpdateStatus();
        };
        _list.DoubleClick += (_, _) => { if (!_multi) Accept(); };
        _list.SelectedIndexChanged += (_, _) => { if (!_multi) _ok!.Enabled = _list.SelectedItems.Count > 0; };

        _filter.TextChanged += (_, _) => Fill();

        _ok = UiHelper.CreateButton(okText ?? (multiSelect ? "초대" : "대화하기"), primary: true);
        var cancel = UiHelper.CreateButton("취소");
        _ok.Enabled = false;
        _ok.Click += (_, _) => Accept();
        cancel.Click += (_, _) => Close();
        FlowLayoutPanel buttons;
        if (skipText is not null)
        {
            var skip = UiHelper.CreateButton(skipText);
            skip.AutoSize = true;
            skip.Click += (_, _) => { DialogResult = DialogResult.Ignore; Close(); };
            buttons = UiHelper.CreateButtonRow(cancel, skip, _ok);
        }
        else
        {
            buttons = UiHelper.CreateButtonRow(cancel, _ok);
        }
        buttons.Dock = DockStyle.Bottom;

        var body = new Panel { Dock = DockStyle.Fill, Padding = new Padding(LogicalToDeviceUnits(10)) };
        body.Controls.Add(_list);
        body.Controls.Add(_filter);
        body.Controls.Add(_status);
        body.Controls.Add(buttons);
        _list.BringToFront();
        Controls.Add(body);
        AcceptButton = _ok;
        CancelButton = cancel;

        if (people is not null)
        {
            // 목록을 직접 받은 경우: 서버에 묻지 않는다
            _received = true;
            _all = people.Where(u => !_exclude.Contains(u.EmployeeNo)).OrderBy(u => u.Name).ToList();
            Shown += (_, _) => { _filter.Focus(); Fill(); };
            return;
        }

        _status.Text = "회사 사람 목록을 불러오는 중...";
        _client.PacketReceived += OnPacketReceived;
        FormClosed += (_, _) => _client.PacketReceived -= OnPacketReceived;
        Shown += (_, _) =>
        {
            _filter.Focus();
            if (!_client.Send(Packet.Create(PacketType.DirectoryReq))) _status.Text = "서버와 연결이 끊어져 목록을 불러오지 못했습니다.";
        };
    }

    // [스레드] 수신 스레드 → UI 스레드
    private void OnPacketReceived(Packet packet)
    {
        if (packet.Type != PacketType.Directory) return;
        UiHelper.RunOnUi(this, () =>
        {
            _received = true;
            _all = (packet.GetData<List<UserInfo>>() ?? [])
                .Where(u => !_exclude.Contains(u.EmployeeNo))
                .OrderBy(u => u.Department).ThenBy(u => u.Name).ToList();
            Fill();
        });
    }

    /// <summary>찾기 글자에 맞는 사람만 목록에 채운다. 체크한 사람은 걸러져 안 보여도 기억한다.</summary>
    private void Fill()
    {
        string q = _filter.Text.Trim();
        _filling = true;
        _list.BeginUpdate();
        _list.Items.Clear();
        foreach (UserInfo u in _all.Where(u => q.Length == 0 ||
                     u.Name.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                     u.Department.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                     u.EmployeeNo.Contains(q, StringComparison.OrdinalIgnoreCase)))
        {
            var row = new ListViewItem(u.DisplayName) { Tag = u, Checked = _checked.Contains(u.EmployeeNo) };
            row.SubItems.Add(u.Department);
            row.SubItems.Add(u.EmployeeNo);
            _list.Items.Add(row);
        }
        _list.EndUpdate();
        _filling = false;
        UpdateStatus();
    }

    private void UpdateStatus()
    {
        if (_multi)
        {
            if (_all.Count == 0 && _received)
            {
                _status.Text = "초대할 수 있는 사람이 없습니다. (회사 사람이 모두 이미 이 방 멤버입니다)";
                _ok.Enabled = false;
                return;
            }
            _status.Text = $"{_list.Items.Count}명 표시 · {_checked.Count}명 선택" +
                           (_checked.Count > RoomInviteRequest.MaxPerRequest ? $" (한 번에 최대 {RoomInviteRequest.MaxPerRequest}명)" : "");
            _ok.Enabled = _checked.Count is > 0 and <= RoomInviteRequest.MaxPerRequest;
        }
        else
        {
            _status.Text = _hint is not null ? $"{_list.Items.Count}명 · {_hint}"
                : $"{_list.Items.Count}명 · 더블클릭하면 1:1 대화를 엽니다 (접속하지 않은 사람에게도 보낼 수 있어요)";
        }
    }

    private void Accept()
    {
        Selected = _multi
            ? _all.Where(u => _checked.Contains(u.EmployeeNo)).ToList()
            : _list.SelectedItems.Cast<ListViewItem>().Select(i => (UserInfo)i.Tag!).Take(1).ToList();
        if (Selected.Count == 0) return;
        DialogResult = DialogResult.OK;
        Close();
    }
}
